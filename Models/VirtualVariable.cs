using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusRtuSimulator.Models;

public enum VirtualVariableType
{
    Bool,
    Byte,
    Word,
    DWord,
    SInt,
    USInt,
    Int,
    UInt,
    DInt,
    UDInt,
    LInt,
    ULInt,
    Real,
    LReal,
    Time
}

public sealed class VirtualVariable : INotifyPropertyChanged
{
    private string _name;
    private VirtualVariableType _type;
    private string _valueText;

    public VirtualVariable(string name, VirtualVariableType type = VirtualVariableType.Bool)
    {
        _name = name;
        _type = type;
        _valueText = DefaultValue(type);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public VirtualVariableType Type
    {
        get => _type;
        set
        {
            if (!SetField(ref _type, value))
                return;
            ValueText = DefaultValue(value);
        }
    }

    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VirtualVariable Clone() => new(Name, Type) { ValueText = ValueText };

    public static string DefaultValue(VirtualVariableType type) =>
        type == VirtualVariableType.Bool ? "FALSE" : "0";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
