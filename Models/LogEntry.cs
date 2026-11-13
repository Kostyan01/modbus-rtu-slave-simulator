namespace ModbusRtuSimulator.Models;

public sealed record LogEntry(string Time, string Direction, string Frame, string Message);
