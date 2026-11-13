using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace ModbusRtuSimulator.Models;

public sealed class MemoryPoint : INotifyPropertyChanged
{
    private ushort _address;
    private ushort _value;
    private string _name = "";
    private RegisterValueType _valueType;
    private FloatWordOrder _floatWordOrder;
    private bool _updatingBits;

    public MemoryPoint()
    {
        Bits = new ObservableCollection<RegisterBit>(
            Enumerable.Range(0, 16).Select(index => new RegisterBit(this, index)));
    }

    public ObservableCollection<RegisterBit> Bits { get; }

    public RegisterValueType ValueType
    {
        get => _valueType;
        set
        {
            if (_valueType == value)
                return;
            _valueType = value;
            OnPropertyChanged();
        }
    }

    public FloatWordOrder FloatWordOrder
    {
        get => _floatWordOrder;
        set
        {
            if (_floatWordOrder == value)
                return;
            _floatWordOrder = value;
            OnPropertyChanged();
        }
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

    public ushort Address
    {
        get => _address;
        set
        {
            if (_address == value)
                return;
            _address = value;
            OnPropertyChanged();
        }
    }

    public ushort Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            OnPropertyChanged();
            if (!_updatingBits)
            {
                _updatingBits = true;
                try
                {
                    for (var index = 0; index < Bits.Count; index++)
                        Bits[index].UpdateFromRegister((value & (1 << index)) != 0);
                }
                finally
                {
                    _updatingBits = false;
                }
            }
        }
    }

    public void SetBit(int index, bool value)
    {
        if ((uint)index >= 16)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (_updatingBits)
            return;
        var mask = (ushort)(1 << index);
        Value = value ? (ushort)(Value | mask) : (ushort)(Value & ~mask);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
