using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Protocol;

public sealed class ModbusRtuProcessor
{
    private const byte IllegalFunction = 1;
    private const byte IllegalDataAddress = 2;
    private const byte IllegalDataValue = 3;

    public event Action<ModbusSlave, MemoryPoint, TagAccessKind, ushort, ushort>? TagAccessed;

    public byte[]? Process(byte[] frame, IReadOnlyCollection<ModbusSlave> slaves)
    {
        if (frame.Length < 4 || frame.Length > 256 || !HasValidCrc(frame))
            return null;

        var unitId = frame[0];
        var function = frame[1];
        if (unitId == 0)
        {
            ProcessBroadcast(frame, function, slaves);
            return null;
        }

        var slave = slaves.FirstOrDefault(item => item.IsEnabled && item.Id == unitId);
        if (slave is null)
            return null;

        if (function is 1 or 2 or 3 or 4)
            return ReadValues(frame, slave);
        if (function is 5 or 6)
            return WriteSingle(frame, slave);
        if (function is 15 or 16)
            return WriteMultiple(frame, slave);

        return CreateException(unitId, function, IllegalFunction);
    }

    public static ushort ComputeCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> frame)
    {
        var expected = ComputeCrc(frame[..^2]);
        return frame[^2] == (byte)expected && frame[^1] == (byte)(expected >> 8);
    }

    public static byte[] AddCrc(ReadOnlySpan<byte> data)
    {
        var frame = new byte[data.Length + 2];
        data.CopyTo(frame);
        var crc = ComputeCrc(data);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private byte[]? ReadValues(byte[] frame, ModbusSlave slave)
    {
        if (frame.Length != 8)
            return null;

        var function = frame[1];
        var start = ReadUInt16(frame, 2);
        var quantity = ReadUInt16(frame, 4);
        var maxQuantity = function is 1 or 2 ? 2000 : 125;
        if (quantity == 0 || quantity > maxQuantity || (uint)start + quantity > 65536)
            return CreateException(frame[0], function, IllegalDataValue);

        var area = function switch
        {
            1 => RegisterArea.Coils,
            2 => RegisterArea.DiscreteInputs,
            3 => RegisterArea.HoldingRegisters,
            _ => RegisterArea.InputRegisters
        };

        if (function is 1 or 2)
        {
            var packed = new byte[(quantity + 7) / 8];
            for (var offset = 0; offset < quantity; offset++)
            {
                if (!TryRead(slave, area, (ushort)(start + offset), out var value))
                    return CreateException(frame[0], function, IllegalDataAddress);
                if (value != 0)
                    packed[offset / 8] |= (byte)(1 << (offset % 8));
            }

            var response = new byte[3 + packed.Length];
            response[0] = frame[0];
            response[1] = function;
            response[2] = (byte)packed.Length;
            packed.CopyTo(response, 3);
            return AddCrc(response);
        }

        var registerResponse = new byte[3 + quantity * 2];
        registerResponse[0] = frame[0];
        registerResponse[1] = function;
        registerResponse[2] = (byte)(quantity * 2);
        for (var offset = 0; offset < quantity; offset++)
        {
            if (!TryRead(slave, area, (ushort)(start + offset), out var value))
                return CreateException(frame[0], function, IllegalDataAddress);
            registerResponse[3 + offset * 2] = (byte)(value >> 8);
            registerResponse[4 + offset * 2] = (byte)value;
        }
        return AddCrc(registerResponse);
    }

    private byte[]? WriteSingle(byte[] frame, ModbusSlave slave)
    {
        if (frame.Length != 8)
            return null;

        var function = frame[1];
        var address = ReadUInt16(frame, 2);
        var value = ReadUInt16(frame, 4);
        var area = function == 5 ? RegisterArea.Coils : RegisterArea.HoldingRegisters;
        if (function == 5 && value is not (0x0000 or 0xFF00))
            return CreateException(frame[0], function, IllegalDataValue);
        if (function == 5)
            value = value == 0xFF00 ? (ushort)1 : (ushort)0;
        if (!TryWrite(slave, area, address, value))
            return CreateException(frame[0], function, IllegalDataAddress);
        return AddCrc(frame.AsSpan(0, 6));
    }

    private byte[]? WriteMultiple(byte[] frame, ModbusSlave slave)
    {
        if (frame.Length < 9)
            return null;

        var function = frame[1];
        var address = ReadUInt16(frame, 2);
        var quantity = ReadUInt16(frame, 4);
        var byteCount = frame[6];
        var isCoil = function == 15;
        var maxQuantity = isCoil ? 1968 : 123;
        var expectedByteCount = isCoil ? (quantity + 7) / 8 : quantity * 2;
        if (quantity == 0 || quantity > maxQuantity || (uint)address + quantity > 65536 ||
            byteCount != expectedByteCount || frame.Length != 9 + byteCount)
            return CreateException(frame[0], function, IllegalDataValue);

        var area = isCoil ? RegisterArea.Coils : RegisterArea.HoldingRegisters;
        var values = new ushort[quantity];
        for (var offset = 0; offset < quantity; offset++)
        {
            values[offset] = isCoil
                ? (ushort)((frame[7 + offset / 8] >> (offset % 8)) & 1)
                : ReadUInt16(frame, 7 + offset * 2);
            if (slave.FindPoint(area, (ushort)(address + offset)) is null)
                return CreateException(frame[0], function, IllegalDataAddress);
        }
        for (var offset = 0; offset < quantity; offset++)
            TryWrite(slave, area, (ushort)(address + offset), values[offset]);

        return AddCrc(frame.AsSpan(0, 6));
    }

    private void ProcessBroadcast(byte[] frame, byte function, IReadOnlyCollection<ModbusSlave> slaves)
    {
        if (function is not (5 or 6 or 15 or 16))
            return;

        foreach (var slave in slaves.Where(item => item.IsEnabled))
        {
            byte[] unitFrame = (byte[])frame.Clone();
            unitFrame[0] = (byte)slave.Id;
            var crc = ComputeCrc(unitFrame.AsSpan(0, unitFrame.Length - 2));
            unitFrame[^2] = (byte)crc;
            unitFrame[^1] = (byte)(crc >> 8);
            if (function is 5 or 6)
                WriteSingle(unitFrame, slave);
            else
                WriteMultiple(unitFrame, slave);
        }
    }

    private static byte[] CreateException(byte unitId, byte function, byte code) =>
        AddCrc(new byte[] { unitId, (byte)(function | 0x80), code });

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private bool TryRead(ModbusSlave slave, RegisterArea area, ushort address, out ushort value)
    {
        var point = slave.FindPoint(area, address);
        if (point is null)
        {
            value = 0;
            return false;
        }

        var previousValue = point.Value;
        TagAccessed?.Invoke(slave, point, TagAccessKind.Read, previousValue, previousValue);
        value = point.Value;
        return true;
    }

    private bool TryWrite(ModbusSlave slave, RegisterArea area, ushort address, ushort value)
    {
        var point = slave.FindPoint(area, address);
        if (point is null)
            return false;

        var previousValue = point.Value;
        point.Value = value;
        TagAccessed?.Invoke(slave, point, TagAccessKind.Write, previousValue, value);
        return true;
    }
}
