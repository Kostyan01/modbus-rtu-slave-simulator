using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Services;

public static class FloatRegisterCodec
{
    public static (ushort First, ushort Second) Encode(float value, FloatWordOrder order)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        var a = (byte)(bits >> 24);
        var b = (byte)(bits >> 16);
        var c = (byte)(bits >> 8);
        var d = (byte)bits;
        return order switch
        {
            FloatWordOrder.ABCD => ((ushort)(a << 8 | b), (ushort)(c << 8 | d)),
            FloatWordOrder.CDAB => ((ushort)(c << 8 | d), (ushort)(a << 8 | b)),
            FloatWordOrder.BADC => ((ushort)(b << 8 | a), (ushort)(d << 8 | c)),
            FloatWordOrder.DCBA => ((ushort)(d << 8 | c), (ushort)(b << 8 | a)),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
    }

    public static float Decode(ushort first, ushort second, FloatWordOrder order)
    {
        var firstHigh = (byte)(first >> 8);
        var firstLow = (byte)first;
        var secondHigh = (byte)(second >> 8);
        var secondLow = (byte)second;
        var bits = order switch
        {
            FloatWordOrder.ABCD => (uint)firstHigh << 24 | (uint)firstLow << 16 | (uint)secondHigh << 8 | secondLow,
            FloatWordOrder.CDAB => (uint)secondHigh << 24 | (uint)secondLow << 16 | (uint)firstHigh << 8 | firstLow,
            FloatWordOrder.BADC => (uint)firstLow << 24 | (uint)firstHigh << 16 | (uint)secondLow << 8 | secondHigh,
            FloatWordOrder.DCBA => (uint)secondLow << 24 | (uint)secondHigh << 16 | (uint)firstLow << 8 | firstHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
