using System.Windows;

namespace ModbusRtuSimulator;

public partial class AddMemoryPointWindow : Window
{
    public AddMemoryPointWindow(ushort suggestedAddress)
    {
        InitializeComponent();
        AddressBox.Text = suggestedAddress.ToString();
        AddressBox.SelectAll();
        AddressBox.Focus();
    }

    public ushort Address { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!ushort.TryParse(AddressBox.Text, out var address))
        {
            MessageBox.Show(this, "Введите целый адрес от 0 до 65535.", "Некорректный адрес",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Address = address;
        DialogResult = true;
    }
}
