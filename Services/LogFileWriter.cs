using System.IO;
using System.Text;
using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Services;

public static class LogFileWriter
{
    public static void WriteCsv(string filePath, IEnumerable<LogEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(entries);

        var lines = new List<string> { "Time,Direction,Frame,Message" };
        lines.AddRange(entries.Select(entry => string.Join(",",
            Escape(entry.Time),
            Escape(entry.Direction),
            Escape(entry.Frame),
            Escape(entry.Message))));
        File.WriteAllLines(filePath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}
