using ModbusRtuSimulator.Models;
using ModbusRtuSimulator.Protocol;

namespace ModbusRtuSimulator.Services;

public static class ModbusReadWarning
{
    public static bool TryGetMissingAddressWarning(
        ReadOnlySpan<byte> request,
        byte[]? response,
        IReadOnlyCollection<ModbusSlave> slaves,
        out string warning)
    {
        warning = "";
        if (request.Length != 8 || !ModbusRtuProcessor.HasValidCrc(request) ||
            request[1] is not (3 or 4) ||
            response is not { Length: 5 } ||
            response[0] != request[0] ||
            response[1] != (byte)(request[1] | 0x80) ||
            response[2] != 2 ||
            !ModbusRtuProcessor.HasValidCrc(response))
            return false;

        var unitId = request[0];
        var slave = slaves.FirstOrDefault(item => item.IsEnabled && item.Id == unitId);
        if (slave is null)
            return false;

        var area = request[1] == 3 ? RegisterArea.HoldingRegisters : RegisterArea.InputRegisters;
        var start = (ushort)((request[2] << 8) | request[3]);
        var quantity = (ushort)((request[4] << 8) | request[5]);
        if (quantity == 0 || (uint)start + quantity > 65536)
            return false;

        var missing = new List<ushort>();
        for (var offset = 0; offset < quantity; offset++)
        {
            var address = (ushort)(start + offset);
            if (slave.FindPoint(area, address) is null)
                missing.Add(address);
        }
        if (missing.Count == 0)
            return false;

        var missingList = string.Join(", ", missing);
        warning = $"Мастер запросил {quantity} рег. области {area} с адреса {start}; " +
                  $"не настроены адреса: {missingList}. Добавьте их в карту {area}, " +
                  "например для 32-битного тега нужны оба регистра.";
        return true;
    }
}
