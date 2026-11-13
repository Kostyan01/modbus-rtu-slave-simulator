using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusRtuSimulator.Models;

public enum ScriptTrigger
{
    Manual,
    Cyclic,
    OnRead,
    OnWrite,
    OnValueEqual,
    OnValueGreater,
    OnValueGreaterOrEqual,
    OnValueLess,
    OnValueLessOrEqual,
    OnRisingEdge,
    OnFallingEdge,
    Event
}

public enum TagAccessKind
{
    Read,
    Write
}

public enum ScriptTargetKind
{
    Register,
    Bit,
    VirtualVariable
}

public sealed class ServerScript : INotifyPropertyChanged
{
    private string _name = "Новый сценарий";
    private string _code = "";
    private bool _isEnabled = true;
    private ScriptTrigger _trigger = ScriptTrigger.Manual;
    private string _targetPortAlias = "";
    private string _targetDevice = "";
    private string _targetTag = "";
    private int _intervalMs = 1000;
    private decimal _triggerValue;
    private ScriptTargetKind _targetKind;
    private string _targetBit = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Code
    {
        get => _code;
        set => SetField(ref _code, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public ScriptTrigger Trigger
    {
        get => _trigger;
        set => SetField(ref _trigger, value);
    }

    public string TargetDevice
    {
        get => _targetDevice;
        set => SetField(ref _targetDevice, value);
    }

    public string TargetPortAlias
    {
        get => _targetPortAlias;
        set => SetField(ref _targetPortAlias, value);
    }

    public string TargetTag
    {
        get => _targetTag;
        set => SetField(ref _targetTag, value);
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => SetField(ref _intervalMs, value);
    }

    public decimal TriggerValue
    {
        get => _triggerValue;
        set => SetField(ref _triggerValue, value);
    }

    public ScriptTargetKind TargetKind
    {
        get => _targetKind;
        set => SetField(ref _targetKind, value);
    }

    public string TargetBit
    {
        get => _targetBit;
        set => SetField(ref _targetBit, value);
    }

    public ServerScript Clone(bool preserveId = false) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        Name = Name,
        Code = Code,
        IsEnabled = IsEnabled,
        Trigger = Trigger,
        TargetPortAlias = TargetPortAlias,
        TargetDevice = TargetDevice,
        TargetTag = TargetTag,
        TargetBit = TargetBit,
        TargetKind = TargetKind,
        IntervalMs = IntervalMs,
        TriggerValue = TriggerValue
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
