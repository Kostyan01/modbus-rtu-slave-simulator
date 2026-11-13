using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusRtuSimulator.Models;

public sealed class PortDefinition : INotifyPropertyChanged
{
    private readonly HashSet<ModbusSlave> _attachedSlaves = new();
    private string _name;
    private string _portName;
    private int _baudRate = 9600;
    private string _parity = "None";
    private int _stopBits = 1;
    private int _frameAssemblyTimeoutMs = 30;
    private bool _isEnabled = true;

    public PortDefinition(string name, string portName = "")
    {
        _name = name;
        _portName = portName;
        Slaves = new ObservableCollection<ModbusSlave>();
        LogEntries = new ObservableCollection<LogEntry>();
        WarningEntries = new ObservableCollection<LogEntry>();
        Slaves.CollectionChanged += Slaves_CollectionChanged;
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            if (!SetField(ref _name, value))
                return;
            SynchronizeSlaveAliases();
        }
    }

    public string PortName
    {
        get => _portName;
        set => SetField(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetField(ref _baudRate, value);
    }

    public string Parity
    {
        get => _parity;
        set => SetField(ref _parity, value);
    }

    public int StopBits
    {
        get => _stopBits;
        set => SetField(ref _stopBits, value);
    }

    public int FrameAssemblyTimeoutMs
    {
        get => _frameAssemblyTimeoutMs;
        set => SetField(ref _frameAssemblyTimeoutMs, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public ObservableCollection<ModbusSlave> Slaves { get; }
    public ObservableCollection<LogEntry> LogEntries { get; }
    public ObservableCollection<LogEntry> WarningEntries { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Slaves_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SynchronizeSlaveAliases();

    private void SynchronizeSlaveAliases()
    {
        var current = Slaves.ToHashSet();
        foreach (var removed in _attachedSlaves.Except(current))
            removed.PortAlias = "";
        foreach (var slave in current)
            slave.PortAlias = Name;
        _attachedSlaves.Clear();
        _attachedSlaves.UnionWith(current);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
