using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ModbusRtuSimulator.Services;

namespace ModbusRtuSimulator.Models;

public enum RegisterArea
{
    Coils,
    DiscreteInputs,
    HoldingRegisters,
    InputRegisters
}

public sealed class ModbusSlave : INotifyPropertyChanged
{
    private string _name;
    private ushort _id;
    private bool _isEnabled = true;

    public ModbusSlave(string name, ushort id, bool populateDefaultMap = true)
    {
        _name = name;
        _id = id;
        Coils = populateDefaultMap ? CreateDefaultPoints(RegisterArea.Coils) : new ObservableCollection<MemoryPoint>();
        DiscreteInputs = populateDefaultMap ? CreateDefaultPoints(RegisterArea.DiscreteInputs) : new ObservableCollection<MemoryPoint>();
        HoldingRegisters = populateDefaultMap ? CreateDefaultPoints(RegisterArea.HoldingRegisters) : new ObservableCollection<MemoryPoint>();
        InputRegisters = populateDefaultMap ? CreateDefaultPoints(RegisterArea.InputRegisters) : new ObservableCollection<MemoryPoint>();
        Scripts = new ObservableCollection<ServerScript>();
        VirtualVariables = new ObservableCollection<VirtualVariable>();
        MacroActions = new ObservableCollection<VirtualVariableMacroAction>();
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public ushort Id
    {
        get => _id;
        set
        {
            if (_id == value)
                return;
            _id = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public string PortAlias { get; internal set; } = "";

    public ObservableCollection<MemoryPoint> Coils { get; }
    public ObservableCollection<MemoryPoint> DiscreteInputs { get; }
    public ObservableCollection<MemoryPoint> HoldingRegisters { get; }
    public ObservableCollection<MemoryPoint> InputRegisters { get; }
    public ObservableCollection<ServerScript> Scripts { get; }
    public ObservableCollection<VirtualVariable> VirtualVariables { get; }
    public ObservableCollection<VirtualVariableMacroAction> MacroActions { get; }

    public ObservableCollection<MemoryPoint> GetPoints(RegisterArea area) =>
        area switch
        {
            RegisterArea.Coils => Coils,
            RegisterArea.DiscreteInputs => DiscreteInputs,
            RegisterArea.HoldingRegisters => HoldingRegisters,
            RegisterArea.InputRegisters => InputRegisters,
            _ => throw new ArgumentOutOfRangeException(nameof(area))
        };

    public bool TryRead(RegisterArea area, ushort address, out ushort value)
    {
        var point = FindPoint(area, address);
        value = point?.Value ?? 0;
        return point is not null;
    }

    public bool TryWrite(RegisterArea area, ushort address, ushort value)
    {
        var point = FindPoint(area, address);
        if (point is null)
            return false;
        point.Value = value;
        return true;
    }

    public MemoryPoint? FindPoint(RegisterArea area, ushort address) =>
        GetPoints(area).FirstOrDefault(item => item.Address == address);

    public bool TryReadFloat(RegisterArea area, ushort address, out float value)
    {
        var first = FindPoint(area, address);
        if (first?.ValueType != RegisterValueType.Float32 ||
            address == ushort.MaxValue ||
            FindPoint(area, (ushort)(address + 1)) is not { } second)
        {
            value = 0;
            return false;
        }
        value = FloatRegisterCodec.Decode(first.Value, second.Value, first.FloatWordOrder);
        return true;
    }

    public bool TryWriteFloat(RegisterArea area, ushort address, float value)
    {
        var first = FindPoint(area, address);
        if (first?.ValueType != RegisterValueType.Float32 ||
            address == ushort.MaxValue ||
            FindPoint(area, (ushort)(address + 1)) is not { } second)
            return false;
        var words = FloatRegisterCodec.Encode(value, first.FloatWordOrder);
        first.Value = words.First;
        second.Value = words.Second;
        return true;
    }

    public ModbusSlave CloneAs(string name, ushort id)
    {
        var clone = new ModbusSlave(name, id, populateDefaultMap: false)
        {
            IsEnabled = IsEnabled,
            PortAlias = PortAlias
        };
        var scriptIdMap = new Dictionary<Guid, Guid>();
        foreach (var area in Enum.GetValues<RegisterArea>())
        {
            foreach (var point in GetPoints(area))
            {
                clone.GetPoints(area).Add(new MemoryPoint
                {
                    Name = point.Name,
                    Address = point.Address,
                    Value = point.Value,
                    ValueType = point.ValueType,
                    FloatWordOrder = point.FloatWordOrder
                });
                var clonedPoint = clone.FindPoint(area, point.Address)!;
                for (var index = 0; index < point.Bits.Count; index++)
                    clonedPoint.Bits[index].Name = point.Bits[index].Name;
            }
        }
        foreach (var script in Scripts)
        {
            var clonedScript = script.Clone();
            scriptIdMap[script.Id] = clonedScript.Id;
            clone.Scripts.Add(clonedScript);
        }
        foreach (var variable in VirtualVariables)
            clone.VirtualVariables.Add(variable.Clone());
        foreach (var action in MacroActions)
        {
            var clonedAction = action.Clone();
            if (clonedAction.TargetScope == MacroTargetScope.Device &&
                scriptIdMap.TryGetValue(clonedAction.TargetScriptId, out var clonedScriptId))
            {
                clonedAction.TargetScriptId = clonedScriptId;
                clonedAction.TargetPortAlias = PortAlias;
                clonedAction.TargetDeviceId = id;
            }
            clone.MacroActions.Add(clonedAction);
        }
        return clone;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string DefaultTagName(RegisterArea area, ushort address) =>
        $"{area switch
        {
            RegisterArea.Coils => "Coil",
            RegisterArea.DiscreteInputs => "DiscreteInput",
            RegisterArea.HoldingRegisters => "HoldingRegister",
            RegisterArea.InputRegisters => "InputRegister",
            _ => throw new ArgumentOutOfRangeException(nameof(area))
        }}_{address}";

    private static ObservableCollection<MemoryPoint> CreateDefaultPoints(RegisterArea area) =>
        new(Enumerable.Range(0, 16).Select(address => new MemoryPoint
        {
            Address = (ushort)address,
            Name = DefaultTagName(area, (ushort)address)
        }));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
