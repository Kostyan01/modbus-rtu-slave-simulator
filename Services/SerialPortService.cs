using System.IO;
using System.IO.Ports;
using System.Diagnostics;

namespace ModbusRtuSimulator.Services;

public sealed class SerialPortService : IDisposable
{
    private readonly object _bufferLock = new();
    private readonly object _portLock = new();
    private readonly ModbusRtuFrameAssembler _frameAssembler = new();
    private readonly Timer _frameTimer;
    private SerialPort? _serialPort;
    private int _frameAssemblyTimeoutMs = DefaultFrameAssemblyTimeoutMs;
    private long _lastReceiveTimestamp;
    private bool _disposed;

    public const int DefaultFrameAssemblyTimeoutMs = 30;
    public const int MinimumFrameAssemblyTimeoutMs = 5;
    public const int MaximumFrameAssemblyTimeoutMs = 200;

    public SerialPortService()
    {
        _frameTimer = new Timer(OnFrameTimeout, null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<byte[]>? FrameReceived;
    public event Action<string>? ErrorOccurred;
    public bool IsOpen
    {
        get
        {
            lock (_portLock)
                return _serialPort?.IsOpen == true;
        }
    }

    public void Open(
        string portName,
        int baudRate,
        Parity parity,
        int stopBits,
        int frameAssemblyTimeoutMs = DefaultFrameAssemblyTimeoutMs)
    {
        if (stopBits is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(stopBits));
        if (frameAssemblyTimeoutMs is < MinimumFrameAssemblyTimeoutMs or > MaximumFrameAssemblyTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(frameAssemblyTimeoutMs));

        lock (_portLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SerialPortService));
            if (_serialPort?.IsOpen == true)
                throw new InvalidOperationException("Последовательный порт уже открыт.");

            var port = new SerialPort(portName, baudRate, parity, 8,
                stopBits == 1 ? StopBits.One : StopBits.Two)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            try
            {
                _frameAssemblyTimeoutMs = frameAssemblyTimeoutMs;
                port.DataReceived += OnDataReceived;
                port.Open();
                _serialPort = port;
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }
    }

    public void Close()
    {
        lock (_bufferLock)
        {
            _frameTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _frameAssembler.Clear();
        }

        lock (_portLock)
        {
            var port = _serialPort;
            _serialPort = null;
            if (port is null)
                return;

            port.DataReceived -= OnDataReceived;
            try
            {
                if (port.IsOpen)
                    port.Close();
            }
            finally
            {
                port.Dispose();
            }
        }
    }

    public bool Write(byte[] data)
    {
        lock (_portLock)
        {
            if (_serialPort?.IsOpen != true)
                return false;
            _serialPort.Write(data, 0, data.Length);
            return true;
        }
    }

    public void Dispose()
    {
        Close();
        _frameTimer.Dispose();
        _disposed = true;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;
        try
        {
            IReadOnlyList<byte[]> frames;
            lock (_bufferLock)
            {
                var bytesToRead = port.BytesToRead;
                if (bytesToRead <= 0)
                    return;
                var incoming = new byte[bytesToRead];
                var read = port.Read(incoming, 0, incoming.Length);
                frames = _frameAssembler.Append(incoming.AsSpan(0, read));
                _lastReceiveTimestamp = Stopwatch.GetTimestamp();
                if (_frameAssembler.PendingByteCount == 0)
                    _frameTimer.Change(Timeout.Infinite, Timeout.Infinite);
                else
                    _frameTimer.Change(_frameAssemblyTimeoutMs, Timeout.Infinite);
            }
            foreach (var frame in frames)
                FrameReceived?.Invoke(frame);
        }
        catch (IOException ex)
        {
            ErrorOccurred?.Invoke($"Ошибка чтения COM-порта: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ErrorOccurred?.Invoke($"COM-порт недоступен: {ex.Message}");
        }
    }

    private void OnFrameTimeout(object? state)
    {
        byte[]? frame;
        lock (_bufferLock)
        {
            if (_frameAssembler.PendingByteCount == 0)
                return;

            var elapsedMs = Stopwatch.GetElapsedTime(_lastReceiveTimestamp).TotalMilliseconds;
            var remainingMs = _frameAssemblyTimeoutMs - elapsedMs;
            if (remainingMs > 0)
            {
                _frameTimer.Change(Math.Max(1, (int)Math.Ceiling(remainingMs)), Timeout.Infinite);
                return;
            }

            frame = _frameAssembler.Flush();
        }
        if (frame is not null)
            FrameReceived?.Invoke(frame);
    }
}
