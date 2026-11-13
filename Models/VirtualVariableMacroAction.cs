using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ModbusRtuSimulator.Models;

public enum MacroTargetScope
{
    Global,
    Device
}

public enum MacroCondition
{
    True,
    Greater,
    Less,
    LessOrEqual,
    GreaterOrEqual,
    Equal
}

public sealed class VirtualVariableMacroAction : INotifyPropertyChanged
{
    private string _sourceVariableName = "";
    private Guid _targetScriptId;
    private MacroTargetScope _targetScope;
    private string _targetPortAlias = "";
    private ushort _targetDeviceId;
    private MacroCondition _condition = MacroCondition.Greater;
    private decimal _threshold;
    private VirtualVariable? _selectedVariable;
    private EventMacroOption? _selectedMacro;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceVariableName
    {
        get => _sourceVariableName;
        set => SetField(ref _sourceVariableName, value);
    }

    public Guid TargetScriptId
    {
        get => _targetScriptId;
        set => SetField(ref _targetScriptId, value);
    }

    public MacroTargetScope TargetScope
    {
        get => _targetScope;
        set => SetField(ref _targetScope, value);
    }

    public string TargetPortAlias
    {
        get => _targetPortAlias;
        set => SetField(ref _targetPortAlias, value);
    }

    public ushort TargetDeviceId
    {
        get => _targetDeviceId;
        set => SetField(ref _targetDeviceId, value);
    }

    public MacroCondition Condition
    {
        get => _condition;
        set => SetField(ref _condition, value);
    }

    public decimal Threshold
    {
        get => _threshold;
        set => SetField(ref _threshold, value);
    }

    [JsonIgnore]
    public VirtualVariable? SelectedVariable
    {
        get => _selectedVariable;
        set
        {
            if (ReferenceEquals(_selectedVariable, value))
                return;
            if (_selectedVariable is not null)
                _selectedVariable.PropertyChanged -= SelectedVariable_PropertyChanged;
            _selectedVariable = value;
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            value.PropertyChanged += SelectedVariable_PropertyChanged;
            SourceVariableName = value.Name;
            if (value.Type == VirtualVariableType.Bool)
                Condition = MacroCondition.True;
            else if (Condition == MacroCondition.True)
                Condition = MacroCondition.Greater;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public EventMacroOption? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (Equals(_selectedMacro, value))
                return;
            _selectedMacro = value;
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (value.ScriptId != Guid.Empty)
            {
                TargetScriptId = value.ScriptId;
                TargetScope = value.Scope;
                TargetPortAlias = value.PortAlias;
                TargetDeviceId = value.DeviceId;
            }
            else
            {
                TargetScriptId = Guid.Empty;
                TargetScope = MacroTargetScope.Global;
                TargetPortAlias = "";
                TargetDeviceId = 0;
            }
            OnPropertyChanged();
        }
    }

    public VirtualVariableMacroAction Clone(bool preserveId = false)
    {
        var clone = new VirtualVariableMacroAction
        {
            Id = preserveId ? Id : Guid.NewGuid(),
            SourceVariableName = SourceVariableName,
            TargetScriptId = TargetScriptId,
            TargetScope = TargetScope,
            TargetPortAlias = TargetPortAlias,
            TargetDeviceId = TargetDeviceId,
            Condition = Condition,
            Threshold = Threshold
        };
        return clone;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SelectedVariable_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VirtualVariable variable)
            return;
        if (e.PropertyName == nameof(VirtualVariable.Name))
            SourceVariableName = variable.Name;
        else if (e.PropertyName == nameof(VirtualVariable.Type))
        {
            if (variable.Type == VirtualVariableType.Bool)
                Condition = MacroCondition.True;
            else if (Condition == MacroCondition.True)
                Condition = MacroCondition.Greater;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record EventMacroOption(
    Guid ScriptId,
    string Name,
    MacroTargetScope Scope,
    string PortAlias,
    ushort DeviceId,
    string DeviceName)
{
    public static EventMacroOption None { get; } =
        new(Guid.Empty, "— не выбрано —", MacroTargetScope.Global, "", 0, "");

    public string DisplayName => ScriptId == Guid.Empty
        ? Name
        : Scope == MacroTargetScope.Global
        ? $"{Name} (общий сценарий)"
        : $"{Name} ({PortAlias}.{DeviceName}, ID {DeviceId})";
}

public sealed record MacroConditionOption(MacroCondition Value, string Label);
