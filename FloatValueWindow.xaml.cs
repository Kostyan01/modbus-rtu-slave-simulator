using System.Globalization;
using System.Windows;
using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator;

public partial class FloatValueWindow : Window
{
    public FloatValueWindow(float value, FloatWordOrder wordOrder)
    {
        InitializeComponent();
        ValueBox.Text = value.ToString("R", CultureInfo.InvariantCulture);
        WordOrderCombo.ItemsSource = Enum.GetValues<FloatWordOrder>();
        WordOrderCombo.SelectedItem = wordOrder;
    }

    public float Value { get; private set; }
    public FloatWordOrder WordOrder => (FloatWordOrder)WordOrderCombo.SelectedItem;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!float.TryParse(ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value))
        {
            MessageBox.Show(this, "Введите конечное число Float32, например 12.5.", "Некорректное значение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Value = value;
        DialogResult = true;
    }
}
