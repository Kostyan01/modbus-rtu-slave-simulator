using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator;

public partial class ScriptEditorWindow : Window, INotifyPropertyChanged
{
    private readonly Action<ServerScript> _runScript;
    private readonly ModbusSlave? _boundSlave;
    private ServerScript? _selectedScript;

    public ScriptEditorWindow(
        ObservableCollection<ServerScript> scripts,
        IEnumerable<ModbusSlave> slaves,
        Action<ServerScript> runScript,
        ModbusSlave? boundSlave = null)
    {
        InitializeComponent();
        Scripts = scripts;
        _runScript = runScript;
        _boundSlave = boundSlave;
        if (boundSlave is not null)
            Title = $"Сценарии устройства — {boundSlave.Name} (THIS)";
        TriggerOptions = new[]
        {
            new TriggerOption(ScriptTrigger.Manual, "Разово / вручную"),
            new TriggerOption(ScriptTrigger.Cyclic, "Циклически"),
            new TriggerOption(ScriptTrigger.Event, "Событийный")
        };
        DataContext = this;
        RefreshAvailableSlaves(slaves);
        SelectedScript = Scripts.FirstOrDefault();
    }

    public ObservableCollection<ServerScript> Scripts { get; }
    public IReadOnlyList<TagBrowserNode> TagBrowserNodes { get; private set; } = Array.Empty<TagBrowserNode>();
    public IReadOnlyList<TriggerOption> TriggerOptions { get; }
    public bool HasSelectedScript => SelectedScript is not null;

    public ServerScript? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (ReferenceEquals(_selectedScript, value))
                return;
            _selectedScript = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedScript));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshAvailableSlaves(IEnumerable<ModbusSlave> slaves)
    {
        var allSlaves = slaves.ToArray();
        TagBrowserNodes = allSlaves.GroupBy(slave => slave.PortAlias, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreatePortBrowserNode(group.Key, group, _boundSlave))
            .OrderBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OnPropertyChanged(nameof(TagBrowserNodes));
    }

    private void AddScript_Click(object sender, RoutedEventArgs e)
    {
        var number = 1;
        while (Scripts.Any(script => script.Name == $"Сценарий {number}"))
            number++;
        var script = new ServerScript { Name = $"Сценарий {number}" };
        Scripts.Add(script);
        SelectedScript = script;
    }

    private void RemoveScript_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is not { } script)
            return;
        var index = Scripts.IndexOf(script);
        Scripts.Remove(script);
        SelectedScript = Scripts.Count == 0 ? null : Scripts[Math.Min(index, Scripts.Count - 1)];
    }

    private void RunScript_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is { } script)
            _runScript(script);
    }

    private void TagBrowser_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TagBrowserNode { Reference: { } reference })
            return;

        EditorTabs.SelectedIndex = 0;
        var start = Math.Min(CodeTextBox.SelectionStart, CodeTextBox.Text.Length);
        var length = Math.Min(CodeTextBox.SelectionLength, CodeTextBox.Text.Length - start);
        CodeTextBox.Text = CodeTextBox.Text.Remove(start, length).Insert(start, reference);
        CodeTextBox.CaretIndex = start + reference.Length;
        CodeTextBox.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static TagBrowserNode CreatePortBrowserNode(
        string portAlias, IEnumerable<ModbusSlave> slaves, ModbusSlave? boundSlave)
    {
        var devices = slaves.ToArray();
        var displayName = string.IsNullOrWhiteSpace(portAlias) ? "Устройства" : portAlias;
        return TagBrowserNode.CreateLazy(displayName, () =>
            devices.Select(slave => CreateDeviceBrowserNode(slave, boundSlave)));
    }

    private static TagBrowserNode CreateDeviceBrowserNode(ModbusSlave slave, ModbusSlave? boundSlave)
    {
        var prefix = ReferencePrefix(slave, boundSlave);
        return TagBrowserNode.CreateLazy($"{slave.Name} (ID {slave.Id})", () =>
        {
            var children = new List<TagBrowserNode>();
            foreach (var area in Enum.GetValues<RegisterArea>())
            {
                var points = slave.GetPoints(area).OrderBy(point => point.Address).ToArray();
                if (points.Length == 0)
                    continue;
                children.Add(TagBrowserNode.CreateLazy(area.ToString(), () =>
                    points.Select(point => CreatePointBrowserNode(point, area, prefix))));
            }

            var variables = slave.VirtualVariables.OrderBy(
                item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (variables.Length > 0)
            {
                children.Add(TagBrowserNode.CreateLazy("Виртуальные переменные", () =>
                    variables.Select(variable => new TagBrowserNode(
                        $"{variable.Name} : {variable.Type} (виртуальная)",
                        $"{prefix}.{QuoteIdentifier(variable.Name)}"))));
            }
            return children;
        });
    }

    private static TagBrowserNode CreatePointBrowserNode(
        MemoryPoint point, RegisterArea area, string prefix)
    {
        var pointReference = $"{prefix}.{QuoteIdentifier(point.Name)}";
        if (area is not (RegisterArea.HoldingRegisters or RegisterArea.InputRegisters))
            return new TagBrowserNode($"{point.Name} (адрес {point.Address})", pointReference);

        return TagBrowserNode.CreateLazy(
            $"{point.Name} (адрес {point.Address})",
            () => point.Bits.Select(bit => new TagBrowserNode(
                bit.Name, $"{pointReference}.{QuoteIdentifier(bit.Name)}")));
    }

    private void TagBrowserItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: TagBrowserNode node })
            node.EnsureChildrenLoaded();
    }

    private static string ReferencePrefix(ModbusSlave slave, ModbusSlave? boundSlave)
    {
        if (ReferenceEquals(slave, boundSlave))
            return "THIS";
        return string.IsNullOrWhiteSpace(slave.PortAlias)
            ? QuoteIdentifier(slave.Name)
            : $"{QuoteIdentifier(slave.PortAlias)}.{QuoteIdentifier(slave.Name)}";
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TriggerOption(ScriptTrigger Value, string Label);

public sealed class TagBrowserNode
{
    private Func<IEnumerable<TagBrowserNode>>? _childrenFactory;

    public TagBrowserNode(string displayName, string? reference)
    {
        DisplayName = displayName;
        Reference = reference;
    }

    private TagBrowserNode(string displayName, Func<IEnumerable<TagBrowserNode>> childrenFactory)
        : this(displayName, (string?)null)
    {
        _childrenFactory = childrenFactory;
        Children.Add(new TagBrowserNode("…", (string?)null));
    }

    public string DisplayName { get; }
    public string? Reference { get; }
    public ObservableCollection<TagBrowserNode> Children { get; } = new();

    public static TagBrowserNode CreateLazy(
        string displayName, Func<IEnumerable<TagBrowserNode>> childrenFactory) =>
        new(displayName, childrenFactory);

    public void EnsureChildrenLoaded()
    {
        if (_childrenFactory is null)
            return;
        var factory = _childrenFactory;
        _childrenFactory = null;
        Children.Clear();
        foreach (var child in factory())
            Children.Add(child);
    }
}
