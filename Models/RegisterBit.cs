using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusRtuSimulator.Models;

public sealed class RegisterBit : INotifyPropertyChanged
{
    private readonly MemoryPoint _register;
    private string _name;
    private bool _isSet;

    public RegisterBit(MemoryPoint register, int index)
    {
        _register = register;
        Index = index;
        _name = $"Bit{index:D2}";
    }

    public int Index { get; }

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

    public bool IsSet
    {
        get => _isSet;
        set
        {
            if (_isSet == value)
                return;
            _isSet = value;
            _register.SetBit(Index, value);
            OnPropertyChanged();
        }
    }

    internal void UpdateFromRegister(bool value)
    {
        if (_isSet == value)
            return;
        _isSet = value;
        OnPropertyChanged(nameof(IsSet));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
