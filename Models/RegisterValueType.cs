namespace ModbusRtuSimulator.Models;

public enum RegisterValueType
{
    UInt16,
    Float32
}

public enum FloatWordOrder
{
    ABCD,
    CDAB,
    BADC,
    DCBA
}
