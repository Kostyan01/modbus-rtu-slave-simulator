using ModbusRtuSimulator.Protocol;

namespace ModbusRtuSimulator.Services;

public sealed class ModbusRtuFrameAssembler
{
    private const int MaxFrameLength = 256;
    private const int MaxBufferedBytes = 512;
    private readonly List<byte> _buffer = new();

    public int PendingByteCount => _buffer.Count;

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _buffer.Add(value);

        var frames = ExtractCompleteFrames();
        while (_buffer.Count > MaxBufferedBytes)
        {
            var count = Math.Min(MaxFrameLength, _buffer.Count - MaxBufferedBytes);
            frames.Add(_buffer.GetRange(0, count).ToArray());
            _buffer.RemoveRange(0, count);
        }

        return frames;
    }

    public byte[]? Flush()
    {
        if (_buffer.Count == 0)
            return null;

        var pending = _buffer.ToArray();
        _buffer.Clear();
        return pending;
    }

    public void Clear() => _buffer.Clear();

    private List<byte[]> ExtractCompleteFrames()
    {
        var frames = new List<byte[]>();
        while (_buffer.Count >= 4)
        {
            if (TryGetValidFrameAt(0, out var frameLength))
            {
                frames.Add(_buffer.GetRange(0, frameLength).ToArray());
                _buffer.RemoveRange(0, frameLength);
                continue;
            }

            var nextFrameStart = FindNextValidFrameStart();
            if (nextFrameStart < 0)
                break;

            frames.Add(_buffer.GetRange(0, nextFrameStart).ToArray());
            _buffer.RemoveRange(0, nextFrameStart);
        }

        return frames;
    }

    private int FindNextValidFrameStart()
    {
        for (var offset = 1; offset <= _buffer.Count - 4; offset++)
        {
            if (TryGetValidFrameAt(offset, out _))
                return offset;
        }

        return -1;
    }

    private bool TryGetValidFrameAt(int offset, out int frameLength)
    {
        frameLength = 0;
        if (_buffer[offset] > 247 || !TryGetExpectedFrameLength(offset, out frameLength) ||
            _buffer.Count - offset < frameLength)
            return false;

        return ModbusRtuProcessor.HasValidCrc(_buffer.GetRange(offset, frameLength).ToArray());
    }

    private bool TryGetExpectedFrameLength(int offset, out int frameLength)
    {
        frameLength = 0;
        var available = _buffer.Count - offset;
        if (available < 2)
            return false;

        var function = _buffer[offset + 1];
        if (function is >= 1 and <= 6)
        {
            frameLength = 8;
            return true;
        }

        if (function is 15 or 16)
        {
            if (available < 7)
                return false;
            frameLength = 9 + _buffer[offset + 6];
            return frameLength <= MaxFrameLength;
        }

        return false;
    }
}
