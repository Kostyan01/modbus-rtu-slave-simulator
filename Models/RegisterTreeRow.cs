using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using ModbusRtuSimulator.Services;

namespace ModbusRtuSimulator.Models;

public sealed class RegisterTreeRow : INotifyPropertyChanged
{
    private readonly ModbusSlave _slave;
    private readonly RegisterArea _area;
    private readonly ObservableCollection<RegisterBit> _bits;
    private MemoryPoint? _secondWord;
    private string _addressText;
    private string _addressError = "";
    private string _floatValueText;
    private string _floatValueError = "";

    public RegisterTreeRow(ModbusSlave slave, RegisterArea area, MemoryPoint register)
    {
        _slave = slave;
        _area = area;
        Register = register;
        _bits = area is RegisterArea.HoldingRegisters or RegisterArea.InputRegisters
            ? register.Bits
            : new ObservableCollection<RegisterBit>();
        _addressText = register.Address.ToString(CultureInfo.CurrentCulture);
        _floatValueText = GetFloatDisplay();
        Register.PropertyChanged += Register_PropertyChanged;
        UpdateSecondWordSubscription();
    }

    public MemoryPoint Register { get; }
    public ObservableCollection<RegisterBit> Bits => _bits;
    public bool IsRegisterArea => _area is RegisterArea.HoldingRegisters or RegisterArea.InputRegisters;
    public bool IsFloat32 => Register.ValueType == RegisterValueType.Float32;
    public bool IsNotFloat32 => !IsFloat32;
    public string FloatModeButtonText => IsFloat32 ? "UInt16" : "Float32";
    public IReadOnlyList<FloatWordOrder> FloatWordOrders { get; } = Enum.GetValues<FloatWordOrder>();

    public string AddressText
    {
        get => _addressText;
        set
        {
            _addressText = value;
            _addressError = "";
            if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.CurrentCulture, out var address))
            {
                _addressError = "Введите целочисленный адрес от 0 до 65535.";
                OnPropertyChanged(nameof(AddressError));
                OnPropertyChanged();
                return;
            }

            if (!TryChangeAddress(address, out _addressError))
            {
                OnPropertyChanged(nameof(AddressError));
                OnPropertyChanged();
                return;
            }

            OnPropertyChanged(nameof(AddressError));
            OnPropertyChanged();
        }
    }

    public string AddressError => _addressError;
    public string FloatValueError => _floatValueError;

    public string FloatValueText
    {
        get => _floatValueText;
        set
        {
            _floatValueText = value;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) &&
                !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                _floatValueError = "Введите число Float32.";
            }
            else if (!float.IsFinite(number))
            {
                _floatValueError = "Float32 должен быть конечным числом.";
            }
            else if (!TrySetFloat(number))
            {
                _floatValueError = "Не удалось записать Float32: проверьте наличие второго регистра.";
            }
            else
            {
                _floatValueError = "";
                _floatValueText = number.ToString("G", CultureInfo.CurrentCulture);
            }
            OnPropertyChanged(nameof(FloatValueError));
            OnPropertyChanged();
        }
    }

    public FloatWordOrder FloatWordOrder
    {
        get => Register.FloatWordOrder;
        set
        {
            if (Register.FloatWordOrder == value)
                return;
            var hasValue = TryGetFloat(out var currentValue);
            Register.FloatWordOrder = value;
            if (hasValue)
                TrySetFloat(currentValue);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FloatValueText));
        }
    }

    public string HighByte => (Register.Value >> 8).ToString("X2");
    public string LowByte => (Register.Value & 0xFF).ToString("X2");
    public string FloatDisplay => GetFloatDisplay();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MemoryMapChanged;

    public bool TryGetFloat(out float value)
    {
        value = 0;
        if (Register.ValueType != RegisterValueType.Float32 ||
            Register.Address == ushort.MaxValue ||
            _slave.FindPoint(_area, (ushort)(Register.Address + 1)) is not { } second)
            return false;
        value = FloatRegisterCodec.Decode(Register.Value, second.Value, Register.FloatWordOrder);
        return true;
    }

    public bool TrySetFloat(float value)
    {
        if (Register.ValueType != RegisterValueType.Float32 ||
            Register.Address == ushort.MaxValue ||
            _slave.FindPoint(_area, (ushort)(Register.Address + 1)) is not { } second)
            return false;
        var words = FloatRegisterCodec.Encode(value, Register.FloatWordOrder);
        Register.Value = words.First;
        second.Value = words.Second;
        return true;
    }

    public string GetFloatDisplay() =>
        TryGetFloat(out var value) ? value.ToString("G", CultureInfo.CurrentCulture) : "—";

    public static bool IsSecondFloatWord(ModbusSlave slave, RegisterArea area, MemoryPoint point) =>
        point.Address > 0 &&
        slave.FindPoint(area, (ushort)(point.Address - 1)) is { ValueType: RegisterValueType.Float32 };

    private bool TryChangeAddress(ushort address, out string error)
    {
        error = "";
        var pairedSecond = IsFloat32 && Register.Address < ushort.MaxValue
            ? _slave.FindPoint(_area, (ushort)(Register.Address + 1))
            : null;
        if (IsFloat32 && address == ushort.MaxValue)
        {
            error = "Float32 занимает два адреса и не может начинаться с 65535.";
            return false;
        }

        var nextAddress = IsFloat32 ? (ushort)(address + 1) : address;
        if (_slave.GetPoints(_area).Any(point =>
                !ReferenceEquals(point, Register) &&
                !ReferenceEquals(point, pairedSecond) &&
                (point.Address == address || IsFloat32 && point.Address == nextAddress)))
        {
            error = "Указанный адрес уже занят в этой области памяти.";
            return false;
        }

        Register.Address = address;
        if (pairedSecond is not null)
            pairedSecond.Address = nextAddress;
        _addressText = address.ToString(CultureInfo.CurrentCulture);
        UpdateSecondWordSubscription();
        OnPropertyChanged(nameof(FloatValueText));
        MemoryMapChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void UpdateSecondWordSubscription()
    {
        if (_secondWord is not null)
            _secondWord.PropertyChanged -= Register_PropertyChanged;
        _secondWord = Register.Address < ushort.MaxValue
            ? _slave.FindPoint(_area, (ushort)(Register.Address + 1))
            : null;
        if (_secondWord is not null)
            _secondWord.PropertyChanged += Register_PropertyChanged;
    }

    private void Register_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryPoint.Value))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighByte)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LowByte)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FloatDisplay)));
            if (ReferenceEquals(sender, Register) || ReferenceEquals(sender, _secondWord))
            {
                _floatValueText = GetFloatDisplay();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FloatValueText)));
            }
        }
        else if (ReferenceEquals(sender, Register))
        {
            if (e.PropertyName == nameof(MemoryPoint.Address))
            {
                _addressText = Register.Address.ToString(CultureInfo.CurrentCulture);
                UpdateSecondWordSubscription();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddressText)));
            }
            if (e.PropertyName is nameof(MemoryPoint.ValueType) or nameof(MemoryPoint.FloatWordOrder))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FloatDisplay)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FloatValueText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFloat32)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotFloat32)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FloatModeButtonText)));
            }
        }
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
