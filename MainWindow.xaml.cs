using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusRtuSimulator.Models;
using ModbusRtuSimulator.Protocol;
using ModbusRtuSimulator.Services;

namespace ModbusRtuSimulator;

public partial class MainWindow : Window
{
    private const int MaxLogEntries = 5000;
    private readonly Dictionary<Guid, PortRuntime> _portRuntimes = new();
    private readonly Dictionary<ObservableCollection<ServerScript>, ScriptEditorWindow> _scriptEditors = new();
    private readonly TagScriptEngine _scriptEngine = new();
    private readonly DispatcherTimer _scriptTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _logCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<Guid, DateTime> _lastScriptRuns = new();
    private readonly HashSet<string> _reportedScriptErrors = new(StringComparer.Ordinal);
    private readonly List<LogEntry> _logCaptureEntries = new();
    private readonly Stopwatch _logCaptureStopwatch = new();
    private readonly ObservableCollection<ModbusSlave> _emptySlaves = new();
    private readonly ObservableCollection<VirtualVariable> _macroSourceVariableOptions = new();
    private readonly ObservableCollection<EventMacroOption> _eventMacroOptions = new();
    private TreeView? _memoryTree;
    private string? _currentConfigurationPath;
    private TimeSpan _logCaptureDuration;
    private bool _autoScrollLogs = true;

    public MainWindow()
    {
        InitializeComponent();
        Ports = new ObservableCollection<PortDefinition>();
        AvailablePorts = new ObservableCollection<string>();
        var initialPort = new PortDefinition("Порт1");
        initialPort.Slaves.Add(new ModbusSlave("Slave1", 1, populateDefaultMap: false));
        Ports.Add(initialPort);
        Scripts = new ObservableCollection<ServerScript>();
        LogEntries = new ObservableCollection<LogEntry>();
        WarningEntries = new ObservableCollection<LogEntry>();
        DataContext = this;
        PortsGrid.ItemsSource = Ports;
        SlavesGrid.ItemsSource = Slaves;
        RefreshPorts();
        _scriptTimer.Tick += ScriptTimer_Tick;
        _logCaptureTimer.Tick += LogCaptureTimer_Tick;
        PortsGrid.SelectedIndex = 0;
        SlavesGrid.SelectedIndex = 0;
        UpdateVirtualVariablesView();
        UpdateMemoryGrid();
        Activated += (_, _) => RefreshMacroBindings();
    }

    public ObservableCollection<PortDefinition> Ports { get; }
    public ObservableCollection<string> AvailablePorts { get; }
    public IReadOnlyList<VirtualVariableType> VirtualVariableTypes { get; } =
        Array.AsReadOnly(Enum.GetValues<VirtualVariableType>());
    public ObservableCollection<VirtualVariable> MacroSourceVariableOptions => _macroSourceVariableOptions;
    public ObservableCollection<EventMacroOption> EventMacroOptions => _eventMacroOptions;
    public IReadOnlyList<MacroConditionOption> NumericMacroConditionOptions { get; } = new[]
    {
        new MacroConditionOption(MacroCondition.Greater, "Больше"),
        new MacroConditionOption(MacroCondition.Less, "Меньше"),
        new MacroConditionOption(MacroCondition.LessOrEqual, "Меньше или равно"),
        new MacroConditionOption(MacroCondition.GreaterOrEqual, "Больше или равно"),
        new MacroConditionOption(MacroCondition.Equal, "Равно")
    };
    public ObservableCollection<ModbusSlave> Slaves => SelectedPort?.Slaves ?? _emptySlaves;
    public ObservableCollection<ServerScript> Scripts { get; }
    public ObservableCollection<LogEntry> LogEntries { get; }
    public ObservableCollection<LogEntry> WarningEntries { get; }
    private PortDefinition? SelectedPort => PortsGrid.SelectedItem as PortDefinition;
    private ModbusSlave? SelectedSlave => SlavesGrid.SelectedItem as ModbusSlave;

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var portName in SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            AvailablePorts.Add(portName);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var opening = new List<PortRuntime>();
        try
        {
            PortsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PortsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            SlavesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            SlavesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ValidateSlaves();
            var enabledPorts = Ports.Where(port => port.IsEnabled).ToArray();
            if (enabledPorts.Length == 0)
                throw new InvalidOperationException("Включите хотя бы один COM-порт.");
            if (enabledPorts.Any(port => string.IsNullOrWhiteSpace(port.PortName)))
                throw new InvalidOperationException("Для каждого включённого порта укажите имя COM-порта.");
            if (enabledPorts.Select(port => port.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabledPorts.Length)
                throw new InvalidOperationException("Один COM-порт нельзя запускать более одного раза.");
            var activeSlaves = enabledPorts.SelectMany(port => port.Slaves).ToArray();
            ValidateScripts(Scripts, activeSlaves, null);
            foreach (var slave in activeSlaves)
                ValidateScripts(slave.Scripts, activeSlaves, slave);

            foreach (var port in enabledPorts)
            {
                var parity = ParseParity(port);
                var runtime = new PortRuntime(port);
                opening.Add(runtime);
                runtime.Serial.FrameReceived += frame => OnFrameReceived(runtime, frame);
                runtime.Serial.ErrorOccurred += message =>
                    Dispatcher.BeginInvoke(new Action(() => AddPortLog(port, "ОШИБКА", "", message)));
                runtime.Serial.Open(port.PortName, port.BaudRate, parity, port.StopBits, port.FrameAssemblyTimeoutMs);
                _portRuntimes.Add(port.Id, runtime);
            }

            _lastScriptRuns.Clear();
            _scriptTimer.Start();
            SetRunning(true);
            AddLog("СИСТЕМА", "", $"Запущено портов: {enabledPorts.Length}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            foreach (var runtime in opening)
            {
                runtime.Serial.Dispose();
                _portRuntimes.Remove(runtime.Definition.Id);
            }
            MessageBox.Show(this, ex.Message, "Не удалось запустить симулятор", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopSimulator();

    private void AddPort_Click(object sender, RoutedEventArgs e)
    {
        if (Ports.Count >= 10)
        {
            MessageBox.Show(this, "Можно настроить не более 10 портов.", "Добавление порта");
            return;
        }
        var index = 1;
        while (Ports.Any(port => string.Equals(port.Name, $"Порт{index}", StringComparison.OrdinalIgnoreCase)))
            index++;
        var port = new PortDefinition($"Порт{index}", AvailablePorts.FirstOrDefault() ?? "")
        {
            Parity = nameof(Parity.None)
        };
        Ports.Add(port);
        PortsGrid.SelectedItem = port;
        AddPortButton.IsEnabled = Ports.Count < 10;
        RemovePortButton.IsEnabled = Ports.Count > 1;
    }

    private void RemovePort_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPort is not { } port)
            return;
        if (Ports.Count == 1)
        {
            MessageBox.Show(this, "Нельзя удалить последнюю строку порта.", "Удаление порта");
            return;
        }
        var index = Ports.IndexOf(port);
        Ports.Remove(port);
        PortsGrid.SelectedIndex = Math.Min(index, Ports.Count - 1);
        AddPortButton.IsEnabled = Ports.Count < 10;
        RemovePortButton.IsEnabled = Ports.Count > 1;
    }

    private void PortsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectedPortView();

    private void AddSlave_Click(object sender, RoutedEventArgs e)
    {
        var id = Enumerable.Range(1, 247).Select(value => (ushort)value)
            .FirstOrDefault(candidate => Slaves.All(slave => slave.Id != candidate));
        if (id == 0)
        {
            MessageBox.Show(this, "Поддерживается не более 247 уникальных slave ID.", "Добавление устройства");
            return;
        }
        Slaves.Add(new ModbusSlave($"Slave{id}", id, populateDefaultMap: false));
        SlavesGrid.SelectedItem = Slaves[^1];
    }

    private void CloneSlave_Click(object sender, RoutedEventArgs e)
    {
        if (SlavesGrid.SelectedItem is not ModbusSlave source)
        {
            MessageBox.Show(this, "Сначала выберите устройство для копирования.", "Копирование устройства");
            return;
        }

        var id = Enumerable.Range(1, 247).Select(value => (ushort)value)
            .FirstOrDefault(candidate => Slaves.All(slave => slave.Id != candidate));
        if (id == 0)
        {
            MessageBox.Show(this, "Свободных slave ID не осталось.", "Копирование устройства");
            return;
        }

        var baseName = $"{source.Name}_Copy";
        var name = baseName;
        for (var suffix = 2; Slaves.Any(slave => string.Equals(slave.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{baseName}{suffix}";

        var clone = source.CloneAs(name, id);
        Slaves.Add(clone);
        SlavesGrid.SelectedItem = clone;
        AddLog("СИСТЕМА", "", $"Устройство «{source.Name}» скопировано как «{name}», ID {id}.");
    }

    private void RemoveSlave_Click(object sender, RoutedEventArgs e)
    {
        if (SlavesGrid.SelectedItem is not ModbusSlave slave)
            return;
        if (Slaves.Count == 1)
        {
            MessageBox.Show(this, "Нельзя удалить последнее устройство.", "Удаление устройства");
            return;
        }
        Slaves.Remove(slave);
        SlavesGrid.SelectedItem = Slaves[0];
    }

    private void SlavesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVirtualVariablesView();
        UpdateMemoryGrid();
    }

    private void AddVirtualVariable_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSlave is not { } slave)
            return;
        var index = 1;
        while (slave.VirtualVariables.Any(variable =>
                   string.Equals(variable.Name, $"Variable{index}", StringComparison.OrdinalIgnoreCase)))
            index++;
        var variable = new VirtualVariable($"Variable{index}");
        slave.VirtualVariables.Add(variable);
        VirtualVariablesGrid.SelectedItem = variable;
        VirtualVariablesGrid.ScrollIntoView(variable);
    }

    private void RemoveVirtualVariable_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSlave is { } slave && VirtualVariablesGrid.SelectedItem is VirtualVariable variable)
        {
            foreach (var action in slave.MacroActions.Where(action =>
                         string.Equals(action.SourceVariableName, variable.Name,
                             StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                action.SelectedVariable = null;
                slave.MacroActions.Remove(action);
            }
            slave.VirtualVariables.Remove(variable);
        }
    }

    private void UpdateVirtualVariablesView()
    {
        var slave = SelectedSlave;
        VirtualVariablesGrid.ItemsSource = slave?.VirtualVariables;
        MacroActionsGrid.ItemsSource = slave?.MacroActions;
        RefreshMacroBindings();
    }

    private void RefreshMacroBindings()
    {
        var slave = SelectedSlave;
        _macroSourceVariableOptions.Clear();
        if (slave is not null)
        {
            foreach (var variable in slave.VirtualVariables)
                _macroSourceVariableOptions.Add(variable);
        }

        _eventMacroOptions.Clear();
        _eventMacroOptions.Add(EventMacroOption.None);
        foreach (var script in Scripts.Where(script => script.Trigger == ScriptTrigger.Event))
            _eventMacroOptions.Add(new EventMacroOption(
                script.Id, script.Name, MacroTargetScope.Global, "", 0, ""));
        foreach (var device in GetAllSlaves())
        {
            foreach (var script in device.Scripts.Where(script => script.Trigger == ScriptTrigger.Event))
            {
                _eventMacroOptions.Add(new EventMacroOption(
                    script.Id, script.Name, MacroTargetScope.Device,
                    device.PortAlias, device.Id, device.Name));
            }
        }

        if (slave is null)
            return;
        foreach (var action in slave.MacroActions)
        {
            var variable = slave.VirtualVariables.FirstOrDefault(item =>
                string.Equals(item.Name, action.SourceVariableName, StringComparison.OrdinalIgnoreCase));
            if (variable is not null)
                action.SelectedVariable = variable;
            var macro = _eventMacroOptions.FirstOrDefault(option =>
                IsMacroTarget(action, option));
            if (macro is not null)
                action.SelectedMacro = macro;
        }
    }

    private static bool IsMacroTarget(VirtualVariableMacroAction action, EventMacroOption option) =>
        action.TargetScriptId == option.ScriptId &&
        action.TargetScope == option.Scope &&
        (option.Scope == MacroTargetScope.Global ||
         (action.TargetDeviceId == option.DeviceId &&
          string.Equals(action.TargetPortAlias, option.PortAlias, StringComparison.OrdinalIgnoreCase)));

    private void VirtualVariablesTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == VirtualVariablesTabs &&
            VirtualVariablesTabs.SelectedItem is TabItem { Header: "Выполнить макрос" })
            RefreshMacroBindings();
    }

    private void AddMacroAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSlave is not { } slave)
            return;
        var action = new VirtualVariableMacroAction();
        slave.MacroActions.Add(action);
        MacroActionsGrid.SelectedItem = action;
    }

    private void RemoveMacroAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSlave is { } slave && MacroActionsGrid.SelectedItem is VirtualVariableMacroAction action)
        {
            action.SelectedVariable = null;
            slave.MacroActions.Remove(action);
        }
    }

    private void UpdateSelectedPortView()
    {
        SlavesGrid.ItemsSource = SelectedPort?.Slaves ?? _emptySlaves;
        if (SlavesGrid.Items.Count > 0 && SlavesGrid.SelectedIndex < 0)
            SlavesGrid.SelectedIndex = 0;
        UpdateVirtualVariablesView();
        LogGrid.ItemsSource = SelectedPort?.LogEntries;
        WarningGrid.ItemsSource = SelectedPort?.WarningEntries;
        UpdateMemoryGrid();
    }

    private void MemoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MemoryTabs)
            UpdateMemoryGrid();
    }

    private void UpdateMemoryGrid()
    {
        CommitMemoryGrid();
        if (MemoryTabs.SelectedItem is not TabItem tab ||
            !Enum.TryParse<RegisterArea>(tab.Tag?.ToString(), out var area))
            return;

        var points = SelectedSlave?.GetPoints(area);
        var rows = points?
            .Where(point => SelectedSlave is not { } slave || !RegisterTreeRow.IsSecondFloatWord(slave, area, point))
            .OrderBy(point => point.Address)
            .Select(point =>
            {
                var row = new RegisterTreeRow(SelectedSlave!, area, point);
                row.MemoryMapChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateMemoryGrid));
                return row;
            })
            .ToArray();
        var tree = new TreeView
        {
            Margin = new Thickness(3),
            ItemsSource = rows,
            ItemTemplate = (HierarchicalDataTemplate)FindResource("RegisterRowTemplate")
        };
        _memoryTree = tree;
        var panel = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var addButton = new Button { Content = "Добавить адрес" };
        addButton.Click += (_, _) => AddMemoryPoint(area);
        var removeButton = new Button { Content = "Удалить адрес" };
        removeButton.Click += (_, _) =>
        {
            if (tree.SelectedItem is RegisterTreeRow row)
                SelectedSlave?.GetPoints(area).Remove(row.Register);
        };
        controls.Children.Add(addButton);
        controls.Children.Add(removeButton);
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls);
        panel.Children.Add(tree);
        tab.Content = panel;
    }

    private void AddMemoryPoint(RegisterArea area)
    {
        if (SelectedSlave is not { } slave)
            return;
        var points = slave.GetPoints(area);
        var suggestedAddress = Enumerable.Range(0, 65536).Select(value => (ushort)value)
            .FirstOrDefault(candidate => points.All(point => point.Address != candidate));
        if (points.Count == ushort.MaxValue + 1)
        {
            MessageBox.Show(this, "Все адреса этой области уже заняты.", "Карта памяти");
            return;
        }
        var dialog = new AddMemoryPointWindow(suggestedAddress) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var address = dialog.Address;
        if (points.Any(point => point.Address == address))
        {
            MessageBox.Show(this, $"Адрес {address} уже существует в этой области.", "Карта памяти");
            return;
        }
        points.Add(new MemoryPoint
        {
            Address = address,
            Name = ModbusSlave.DefaultTagName(area, address)
        });
        UpdateMemoryGrid();
    }

    private void ToggleFloatMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RegisterTreeRow row } ||
            SelectedSlave is not { } slave ||
            MemoryTabs.SelectedItem is not TabItem tab ||
            !Enum.TryParse<RegisterArea>(tab.Tag?.ToString(), out var area) ||
            area is not (RegisterArea.HoldingRegisters or RegisterArea.InputRegisters))
            return;

        if (row.Register.ValueType == RegisterValueType.Float32)
        {
            row.Register.ValueType = RegisterValueType.UInt16;
            UpdateMemoryGrid();
            return;
        }

        if (row.Register.Address == ushort.MaxValue)
        {
            MessageBox.Show(this, "Для Float32 нужны два последовательных регистра; адрес 65535 нельзя использовать как начальный.",
                "Float32", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var points = slave.GetPoints(area);
        var second = slave.FindPoint(area, (ushort)(row.Register.Address + 1));
        if (second is null)
        {
            second = new MemoryPoint
            {
                Address = (ushort)(row.Register.Address + 1),
                Name = ModbusSlave.DefaultTagName(area, (ushort)(row.Register.Address + 1))
            };
        }

        if (second.ValueType == RegisterValueType.Float32)
        {
            MessageBox.Show(this, $"Адрес {second.Address} уже является началом другой пары Float32.",
                "Перекрывающиеся Float32", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var previous = points.FirstOrDefault(point =>
            point.Address < row.Register.Address &&
            point.Address + 1 == row.Register.Address &&
            point.ValueType == RegisterValueType.Float32);
        if (previous is not null)
        {
            MessageBox.Show(this, $"Адрес {row.Register.Address} уже является вторым словом Float32 «{previous.Name}».",
                "Перекрывающиеся Float32", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (slave.FindPoint(area, second.Address) is null)
            points.Add(second);
        row.Register.ValueType = RegisterValueType.Float32;
        if (!row.TrySetFloat(0))
            throw new InvalidOperationException("Не удалось записать Float32 в два регистра.");
        UpdateMemoryGrid();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON configuration (*.json)|*.json",
                FileName = _currentConfigurationPath is null
                    ? "modbus-rtu-config.json"
                    : Path.GetFileName(_currentConfigurationPath),
                InitialDirectory = ExistingDirectory(_currentConfigurationPath)
            };
            if (dialog.ShowDialog(this) != true)
                return;
            SaveConfiguration(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            MessageBox.Show(this, ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveConfigurationWithPrompt();

    private void LogScroll_Click(object sender, RoutedEventArgs e)
    {
        _autoScrollLogs = !_autoScrollLogs;
        LogScrollButton.Content = _autoScrollLogs ? "Автопрокрутка: вкл" : "Автопрокрутка: выкл";
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogEntries.Clear();
        WarningEntries.Clear();
        foreach (var port in Ports)
        {
            port.LogEntries.Clear();
            port.WarningEntries.Clear();
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logCaptureTimer.IsEnabled)
        {
            MessageBox.Show(this, "Запись журнала уже выполняется.", "Сохранение журнала",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_currentConfigurationPath))
        {
            MessageBox.Show(this, "Сначала откройте конфигурацию или сохраните её, чтобы определить папку для журнала.",
                "Сохранение журнала", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(LogCaptureSecondsBox.Text, out var seconds) || seconds is < 1 or > 86400)
        {
            MessageBox.Show(this, "Укажите длительность записи от 1 до 86400 секунд.",
                "Сохранение журнала", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _logCaptureEntries.Clear();
        _logCaptureDuration = TimeSpan.FromSeconds(seconds);
        _logCaptureStopwatch.Restart();
        _logCaptureTimer.Start();
        SaveLogButton.IsEnabled = false;
        LogCaptureStatusText.Text = $"Идёт запись: {seconds} сек.";
        AddLog("СИСТЕМА", "", $"Начата запись журнала на {seconds} сек.");
    }

    private void LogCaptureTimer_Tick(object? sender, EventArgs e)
    {
        var remaining = _logCaptureDuration - _logCaptureStopwatch.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            LogCaptureStatusText.Text = $"Идёт запись: {Math.Ceiling(remaining.TotalSeconds)} сек.";
            return;
        }

        _logCaptureTimer.Stop();
        _logCaptureStopwatch.Stop();
        SaveLogButton.IsEnabled = true;
        try
        {
            var directory = Path.GetDirectoryName(_currentConfigurationPath)
                ?? throw new InvalidOperationException("Не удалось определить папку конфигурации.");
            var filePath = Path.Combine(directory, $"modbus-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            LogFileWriter.WriteCsv(filePath, _logCaptureEntries);
            LogCaptureStatusText.Text = $"Сохранено: {Path.GetFileName(filePath)}";
            AddLog("СИСТЕМА", "", $"Журнал сохранён: {filePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LogCaptureStatusText.Text = "Ошибка сохранения журнала.";
            AddLog("ОШИБКА", "", $"Не удалось сохранить журнал: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка сохранения журнала", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveConfigurationWithPrompt()
    {
        if (_currentConfigurationPath is not null)
        {
            SaveConfigurationSafely(_currentConfigurationPath);
            return;
        }
        Export_Click(this, new RoutedEventArgs());
    }

    private void SaveConfigurationSafely(string filePath)
    {
        try
        {
            SaveConfiguration(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            MessageBox.Show(this, ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveConfiguration(string filePath)
    {
        CommitMemoryGrid();
        PortsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PortsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SlavesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SlavesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ValidateSlaves();
        var configuration = SimulatorConfiguration.FromPorts(Ports, Scripts);
        File.WriteAllText(filePath, JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions));
        _currentConfigurationPath = filePath;
        Title = $"Modbus RTU Slave Simulator — {Path.GetFileName(filePath)}";
        AddLog("СИСТЕМА", "", $"Конфигурация сохранена: {filePath}");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguration();
    }

    private void OpenConfiguration()
    {
        if (_portRuntimes.Count > 0)
        {
            MessageBox.Show(this, "Остановите все порты перед импортом конфигурации.", "Импорт");
            return;
        }

        var dialog = new OpenFileDialog { Filter = "JSON configuration (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var configuration = JsonSerializer.Deserialize<SimulatorConfiguration>(json)
                ?? throw new InvalidDataException("Файл конфигурации пуст или имеет неверный формат.");
            var loadedPorts = configuration.ToPorts();
            var loadedSlaves = loadedPorts.SelectMany(port => port.Slaves).ToArray();
            var loadedScripts = configuration.Scripts ?? new List<ServerScript>();
            var migratedScripts = NormalizeLegacyTriggers(
                loadedScripts.Concat(loadedSlaves.SelectMany(slave => slave.Scripts)));
            ValidateLoadedScripts(loadedScripts, loadedSlaves);
            ValidateMacroActions(loadedSlaves, loadedScripts);
            Ports.Clear();
            foreach (var port in loadedPorts)
                Ports.Add(port);
            _scriptEngine.ClearState();
            Scripts.Clear();
            foreach (var script in loadedScripts)
                Scripts.Add(script);
            _lastScriptRuns.Clear();
            _reportedScriptErrors.Clear();
            RefreshPorts();
            PortsGrid.ItemsSource = Ports;
            PortsGrid.SelectedIndex = 0;
            UpdateSelectedPortView();
            SlavesGrid.SelectedIndex = Slaves.Count > 0 ? 0 : -1;
            _currentConfigurationPath = dialog.FileName;
            Title = $"Modbus RTU Slave Simulator — {Path.GetFileName(dialog.FileName)}";
            AddLog("СИСТЕМА", "", $"Конфигурация импортирована: {dialog.FileName}");
            if (migratedScripts > 0)
                MessageBox.Show(this,
                    $"В конфигурации обновлено сценариев: {migratedScripts}. " +
                    "Старые режимы условий заменены разовым запуском. Для событийного запуска " +
                    "выберите режим «Событийный» и настройте связь в разделе виртуальных переменных.",
                    "Обновление сценариев", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (e.Key == Key.S)
        {
            SaveConfigurationWithPrompt();
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            OpenConfiguration();
            e.Handled = true;
        }
    }

    private void CommitMemoryGrid()
    {
        if (_memoryTree?.IsKeyboardFocusWithin == true)
            Keyboard.ClearFocus();
    }

    private static string? ExistingDirectory(string? filePath)
    {
        if (filePath is null)
            return null;
        var directory = Path.GetDirectoryName(filePath);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    private void ValidateSlaves()
    {
        PortsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PortsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SlavesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SlavesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (Ports.Count is < 1 or > 10)
            throw new InvalidOperationException("Настройте от 1 до 10 портов.");
        if (Ports.Any(port => string.IsNullOrWhiteSpace(port.Name)) ||
            Ports.Select(port => port.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Ports.Count)
            throw new InvalidOperationException("Псевдонимы портов должны быть заполнены и уникальны.");
        if (Ports.Any(port => string.Equals(port.Name, "THIS", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Псевдоним порта THIS зарезервирован для внутренних сценариев устройств.");
        var enabledPorts = Ports.Where(port => port.IsEnabled).ToArray();
        if (enabledPorts.Any(port => string.IsNullOrWhiteSpace(port.PortName)))
            throw new InvalidOperationException("Для каждого включённого порта укажите имя COM-порта.");
        if (enabledPorts.Select(port => port.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabledPorts.Length)
            throw new InvalidOperationException("Один COM-порт нельзя запускать более одного раза.");

        foreach (var port in Ports)
        {
            if (port.BaudRate <= 0)
                throw new InvalidOperationException($"Порт «{port.Name}»: укажите корректную скорость.");
            _ = ParseParity(port);
            if (port.StopBits is not (1 or 2))
                throw new InvalidOperationException($"Порт «{port.Name}»: стоп-биты должны быть 1 или 2.");
            if (port.FrameAssemblyTimeoutMs is < SerialPortService.MinimumFrameAssemblyTimeoutMs or
                > SerialPortService.MaximumFrameAssemblyTimeoutMs)
                throw new InvalidOperationException(
                    $"Порт «{port.Name}»: пауза сборки кадра должна быть от {SerialPortService.MinimumFrameAssemblyTimeoutMs} до {SerialPortService.MaximumFrameAssemblyTimeoutMs} мс.");

            var slaves = port.Slaves;
            if (slaves.Count > 247)
                throw new InvalidOperationException($"Порт «{port.Name}»: поддерживается не более 247 устройств.");
            if (slaves.Any(slave => slave.Id is < 1 or > 247) ||
                slaves.Select(slave => slave.Id).Distinct().Count() != slaves.Count)
                throw new InvalidOperationException($"Порт «{port.Name}»: ID устройств должны быть уникальными и находиться в диапазоне 1–247.");
            if (slaves.Select(slave => slave.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != slaves.Count)
                throw new InvalidOperationException($"Порт «{port.Name}»: имена устройств должны быть уникальными.");

            foreach (var slave in slaves)
            {
                if (string.IsNullOrWhiteSpace(slave.Name))
                    throw new InvalidOperationException($"У каждого устройства порта «{port.Name}» должно быть имя.");
                foreach (var area in Enum.GetValues<RegisterArea>())
                {
                    var points = slave.GetPoints(area);
                    if (points.Select(point => point.Address).Distinct().Count() != points.Count)
                        throw new InvalidOperationException($"Устройство {slave.Id}: адреса в области {area} должны быть уникальными.");
                    if (area is RegisterArea.Coils or RegisterArea.DiscreteInputs && points.Any(point => point.Value > 1))
                        throw new InvalidOperationException($"Устройство {slave.Id}: значения {area} должны быть 0 или 1.");
                    foreach (var point in points)
                    {
                        if (point.Bits.Count != 16 ||
                            point.Bits.Any(bit => string.IsNullOrWhiteSpace(bit.Name)) ||
                            point.Bits.Select(bit => bit.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 16)
                            throw new InvalidOperationException($"Устройство {slave.Id}: у регистра {point.Address} должны быть 16 уникальных имён битов.");
                        if (!Enum.IsDefined(point.ValueType) || !Enum.IsDefined(point.FloatWordOrder))
                            throw new InvalidOperationException($"Устройство {slave.Id}: некорректный тип или порядок слов регистра {point.Address}.");
                        if (point.ValueType != RegisterValueType.Float32)
                            continue;
                        if (area is not (RegisterArea.HoldingRegisters or RegisterArea.InputRegisters))
                            throw new InvalidOperationException("Float32 доступен только для регистров Holding/Input.");
                        if (point.Address == ushort.MaxValue ||
                            slave.FindPoint(area, (ushort)(point.Address + 1)) is null)
                            throw new InvalidOperationException($"Float32 по адресу {point.Address} требует второй последовательный регистр.");
                        if (point.Address > 0 &&
                            slave.FindPoint(area, (ushort)(point.Address - 1)) is { ValueType: RegisterValueType.Float32 })
                            throw new InvalidOperationException($"Float32 по адресу {point.Address} перекрывает предыдущую пару регистров.");
                    }
                }
                var tags = Enum.GetValues<RegisterArea>().SelectMany(slave.GetPoints).ToArray();
                if (tags.Any(point => string.IsNullOrWhiteSpace(point.Name)) ||
                    tags.GroupBy(point => point.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                    throw new InvalidOperationException($"Устройство {slave.Id}: имена тегов должны быть заполнены и уникальны.");
                var variableNames = slave.VirtualVariables.Select(variable => variable.Name).ToArray();
                if (variableNames.Any(string.IsNullOrWhiteSpace) ||
                    variableNames.Concat(tags.Select(point => point.Name))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != variableNames.Length + tags.Length ||
                    variableNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != variableNames.Length)
                    throw new InvalidOperationException(
                        $"Устройство {slave.Id}: имена виртуальных переменных должны быть заполнены, уникальны и не совпадать с тегами.");
                foreach (var variable in slave.VirtualVariables)
                {
                    if (!Enum.IsDefined(variable.Type))
                        throw new InvalidOperationException($"Устройство {slave.Id}: неизвестный тип виртуальной переменной.");
                    try
                    {
                        TagScriptEngine.ValidateVirtualVariable(variable);
                    }
                    catch (TagScriptException ex)
                    {
                        throw new InvalidOperationException(
                            $"Устройство {slave.Id}, переменная «{variable.Name}»: {ex.Message}", ex);
                    }
                }
            }
        }
        ValidateLoadedScripts(Scripts, GetAllSlaves());
        ValidateMacroActions(GetAllSlaves(), Scripts);
    }

    private void ValidateLoadedScripts(IEnumerable<ServerScript> scripts, IReadOnlyCollection<ModbusSlave> slaves)
    {
        ValidateScripts(scripts, slaves, null);
        foreach (var slave in slaves)
            ValidateScripts(slave.Scripts, slaves, slave);
    }

    private void ValidateScripts(
        IEnumerable<ServerScript> scripts, IReadOnlyCollection<ModbusSlave> slaves, ModbusSlave? thisSlave)
    {
        foreach (var script in scripts)
        {
            if (script is null)
                throw new InvalidOperationException("Конфигурация содержит пустой сценарий.");
            if (string.IsNullOrWhiteSpace(script.Name))
                throw new InvalidOperationException("У каждого сценария должно быть имя.");
            if (!Enum.IsDefined(script.Trigger))
                throw new InvalidOperationException($"Сценарий «{script.Name}»: неизвестный тип запуска.");
            if (!Enum.IsDefined(script.TargetKind))
                throw new InvalidOperationException($"Сценарий «{script.Name}»: неизвестный тип цели события.");
            if (script.Trigger == ScriptTrigger.Cyclic && script.IntervalMs is < 50 or > 60000)
                throw new InvalidOperationException($"Сценарий «{script.Name}»: интервал должен быть 50–60000 мс.");
            if (script.IsEnabled && string.IsNullOrWhiteSpace(script.Code))
                throw new InvalidOperationException($"Сценарий «{script.Name}»: добавьте код или отключите сценарий.");
            if (script.IsEnabled)
            {
                try
                {
                    _scriptEngine.Validate(script, slaves, thisSlave);
                }
                catch (TagScriptException ex)
                {
                    throw new InvalidOperationException($"Сценарий «{script.Name}»: {ex.Message}", ex);
                }
            }
            if (script.Trigger is ScriptTrigger.OnRead or ScriptTrigger.OnWrite or
                ScriptTrigger.OnValueEqual or ScriptTrigger.OnValueGreater or
                ScriptTrigger.OnValueGreaterOrEqual or ScriptTrigger.OnValueLess or
                ScriptTrigger.OnValueLessOrEqual or ScriptTrigger.OnRisingEdge or
                ScriptTrigger.OnFallingEdge)
                throw new InvalidOperationException(
                    $"Сценарий «{script.Name}»: неизвестный режим запуска. Выберите разовый, циклический или событийный.");
        }
    }

    private static int NormalizeLegacyTriggers(IEnumerable<ServerScript> scripts)
    {
        var migrated = 0;
        foreach (var script in scripts)
        {
            var changed = false;
            if (script.Trigger is ScriptTrigger.OnRead or ScriptTrigger.OnWrite or
                ScriptTrigger.OnValueEqual or ScriptTrigger.OnValueGreater or
                ScriptTrigger.OnValueGreaterOrEqual or ScriptTrigger.OnValueLess or
                ScriptTrigger.OnValueLessOrEqual or ScriptTrigger.OnRisingEdge or
                ScriptTrigger.OnFallingEdge)
            {
                script.Trigger = ScriptTrigger.Manual;
                changed = true;
            }
            if (script.TargetKind == ScriptTargetKind.Bit)
            {
                script.TargetKind = ScriptTargetKind.Register;
                script.TargetBit = "";
                changed = true;
            }
            if (changed)
                migrated++;
        }
        return migrated;
    }

    private static void ValidateMacroActions(
        IReadOnlyCollection<ModbusSlave> slaves, IEnumerable<ServerScript> globalScripts)
    {
        foreach (var sourceSlave in slaves)
        {
            foreach (var action in sourceSlave.MacroActions)
            {
                var variable = sourceSlave.VirtualVariables.FirstOrDefault(item =>
                    string.Equals(item.Name, action.SourceVariableName, StringComparison.OrdinalIgnoreCase));
                if (variable is null)
                    throw new InvalidOperationException(
                        $"Устройство «{sourceSlave.Name}»: выберите существующую виртуальную переменную для вызова макроса.");
                if (!VirtualVariableMacroEvaluator.IsValidFor(action, variable))
                    throw new InvalidOperationException(
                        $"Переменная «{variable.Name}» типа {variable.Type} не поддерживает выбранное условие запуска макроса.");
                if (!TryResolveEventMacro(action, slaves, globalScripts, out _, out _))
                    throw new InvalidOperationException(
                        $"Устройство «{sourceSlave.Name}», переменная «{variable.Name}»: выберите существующий сценарий в событийном режиме.");
            }
        }
    }

    private static bool TryResolveEventMacro(
        VirtualVariableMacroAction action,
        IReadOnlyCollection<ModbusSlave> slaves,
        IEnumerable<ServerScript> globalScripts,
        out ServerScript? script,
        out ModbusSlave? owner)
    {
        script = null;
        owner = null;
        if (action.TargetScriptId == Guid.Empty || !Enum.IsDefined(action.TargetScope))
            return false;

        if (action.TargetScope == MacroTargetScope.Global)
        {
            var matches = globalScripts.Where(candidate =>
                    candidate.Id == action.TargetScriptId && candidate.Trigger == ScriptTrigger.Event)
                .Take(2).ToArray();
            if (matches.Length != 1)
                return false;
            script = matches[0];
            return true;
        }

        var owners = slaves.Where(candidate =>
                candidate.Id == action.TargetDeviceId &&
                string.Equals(candidate.PortAlias, action.TargetPortAlias, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (owners.Length != 1)
            return false;
        var localScripts = owners[0].Scripts.Where(candidate =>
                candidate.Id == action.TargetScriptId && candidate.Trigger == ScriptTrigger.Event)
            .Take(2).ToArray();
        if (localScripts.Length != 1)
            return false;
        owner = owners[0];
        script = localScripts[0];
        return true;
    }

    private static Parity ParseParity(PortDefinition port)
    {
        if (Enum.TryParse<Parity>(port.Parity, true, out var parity) && Enum.IsDefined(parity))
            return parity;
        throw new InvalidOperationException($"Порт «{port.Name}»: выберите корректную чётность.");
    }

    private void Scripts_Click(object sender, RoutedEventArgs e)
    {
        ShowScriptEditor(Scripts, null);
    }

    private void DeviceScripts_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSlave is not { } slave)
        {
            MessageBox.Show(this, "Сначала выберите устройство.", "Внутренние сценарии");
            return;
        }

        ShowScriptEditor(slave.Scripts, slave);
    }

    private void ShowScriptEditor(ObservableCollection<ServerScript> scripts, ModbusSlave? boundSlave)
    {
        if (_scriptEditors.TryGetValue(scripts, out var existing))
        {
            existing.RefreshAvailableSlaves(GetAllSlaves());
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var editor = new ScriptEditorWindow(
            scripts,
            GetAllSlaves(),
            script => RunScript(script, "ручной запуск", boundSlave),
            boundSlave)
        {
            Owner = this
        };
        _scriptEditors.Add(scripts, editor);
        editor.Closed += (_, _) => _scriptEditors.Remove(scripts);
        editor.Activated += (_, _) =>
        {
            editor.RefreshAvailableSlaves(GetAllSlaves());
        };
        editor.Show();
    }

    private void ScriptTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var script in Scripts.Where(script => script.IsEnabled && script.Trigger == ScriptTrigger.Cyclic).ToArray())
        {
            if (_lastScriptRuns.TryGetValue(script.Id, out var lastRun) &&
                now - lastRun < TimeSpan.FromMilliseconds(script.IntervalMs))
                continue;
            _lastScriptRuns[script.Id] = now;
            RunScript(script, "циклический запуск");
        }
        foreach (var port in Ports.Where(port => _portRuntimes.ContainsKey(port.Id)).ToArray())
        {
            foreach (var slave in port.Slaves.Where(slave => slave.IsEnabled).ToArray())
            {
                foreach (var script in slave.Scripts
                             .Where(script => script.IsEnabled && script.Trigger == ScriptTrigger.Cyclic).ToArray())
                {
                    if (_lastScriptRuns.TryGetValue(script.Id, out var lastRun) &&
                        now - lastRun < TimeSpan.FromMilliseconds(script.IntervalMs))
                        continue;
                    _lastScriptRuns[script.Id] = now;
                    RunScript(script, "циклический запуск", slave);
                }
            }
        }
        EvaluateMacroActions();
    }

    private void EvaluateMacroActions()
    {
        var activeSlaves = Ports.Where(port => _portRuntimes.ContainsKey(port.Id))
            .SelectMany(port => port.Slaves.Where(slave => slave.IsEnabled))
            .ToArray();

        foreach (var sourceSlave in activeSlaves)
        {
            foreach (var action in sourceSlave.MacroActions.ToArray())
            {
                var variable = sourceSlave.VirtualVariables.FirstOrDefault(item =>
                    string.Equals(item.Name, action.SourceVariableName, StringComparison.OrdinalIgnoreCase));
                if (variable is null || !VirtualVariableMacroEvaluator.IsValidFor(action, variable))
                    continue;

                try
                {
                    var isSatisfied = VirtualVariableMacroEvaluator.IsSatisfied(action, variable);
                    _reportedScriptErrors.RemoveWhere(
                        key => key.StartsWith($"{action.Id}:macro:", StringComparison.Ordinal));
                    if (!isSatisfied)
                    {
                        continue;
                    }
                }
                catch (TagScriptException ex)
                {
                    var errorKey = $"{action.Id}:macro:{ex.Message}";
                    if (_reportedScriptErrors.Add(errorKey))
                        AddLog("ОШИБКА УСЛОВИЯ", "", $"Вызов макроса по «{variable.Name}»: {ex.Message}");
                    continue;
                }

                if (!TryResolveEventMacro(
                        action, activeSlaves, Scripts, out var script, out var owner) ||
                    script is null || !script.IsEnabled)
                    continue;
                RunScript(script, "событийный макрос", owner);
            }
        }
    }

    private void RunScript(ServerScript script, string reason, ModbusSlave? thisSlave = null)
    {
        try
        {
            if (!script.IsEnabled)
            {
                AddLog("СКРИПТ", "", $"«{script.Name}» отключён; запуск пропущен.");
                return;
            }
            _scriptEngine.Execute(script, GetScenarioSlaves(), thisSlave);
            _reportedScriptErrors.RemoveWhere(key => key.StartsWith($"{script.Id}:", StringComparison.Ordinal));
            if (reason is not ("циклический запуск" or "событийный макрос"))
                AddLog("СКРИПТ", "", $"«{script.Name}»: {reason}");
        }
        catch (Exception ex) when (ex is TagScriptException or OverflowException or DivideByZeroException)
        {
            var errorKey = $"{script.Id}:{script.Code}:{ex.Message}";
            if (_reportedScriptErrors.Add(errorKey))
            {
                AddLog("ОШИБКА СКРИПТА", "", $"«{script.Name}»: {ex.Message}");
                if (reason == "ручной запуск")
                    MessageBox.Show(this, ex.Message, $"Ошибка сценария «{script.Name}»",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private ModbusSlave[] GetAllSlaves() =>
        Ports.SelectMany(port => port.Slaves).ToArray();

    private ModbusSlave[] GetScenarioSlaves() =>
        _portRuntimes.Count == 0
            ? GetAllSlaves()
            : Ports.Where(port => _portRuntimes.ContainsKey(port.Id))
                .SelectMany(port => port.Slaves)
                .ToArray();

    private void OnFrameReceived(PortRuntime runtime, byte[] request)
    {
        var port = runtime.Definition;
        byte[]? response;
        try
        {
            response = Dispatcher.Invoke(() =>
            {
                AddPortLog(port, "RX", FormatFrame(request), $"Запрос, {request.Length} байт");
                var result = runtime.Processor.Process(request, port.Slaves);
                if (ModbusReadWarning.TryGetMissingAddressWarning(request, result, port.Slaves, out var warning))
                    AddPortWarningLog(port, FormatFrame(request), warning);
                if (request.Length >= 4 && !ModbusRtuProcessor.HasValidCrc(request))
                    AddPortLog(port, "СИСТЕМА", "", "Кадр отброшен: неверная CRC.");
                return result;
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            Dispatcher.BeginInvoke(new Action(() => AddPortLog(port, "ОШИБКА", "", $"Ошибка обработки кадра: {ex.Message}")));
            return;
        }

        if (response is null)
            return;
        try
        {
            if (runtime.Serial.Write(response))
                Dispatcher.BeginInvoke(new Action(() =>
                    AddPortLog(port, "TX", FormatFrame(response), $"Ответ, {response.Length} байт")));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                AddPortLog(port, "ОШИБКА", "", $"Ошибка записи COM-порта: {ex.Message}")));
        }
    }

    private void StopSimulator()
    {
        _scriptTimer.Stop();
        foreach (var runtime in _portRuntimes.Values.ToArray())
        {
            try
            {
                runtime.Serial.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                AddPortLog(runtime.Definition, "ОШИБКА", "", $"Ошибка закрытия порта: {ex.Message}");
            }
            finally
            {
                runtime.Serial.Dispose();
            }
        }
        _portRuntimes.Clear();
        SetRunning(false);
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        AddPortButton.IsEnabled = !running && Ports.Count < 10;
        RemovePortButton.IsEnabled = !running && Ports.Count > 1;
        PortsGrid.IsReadOnly = running;
        SlavesGrid.IsReadOnly = running;
        StatusText.Text = running
            ? $"Работают порты: {string.Join(", ", _portRuntimes.Values.Select(runtime => runtime.Definition.Name))}"
            : "Остановлен";
    }

    private void AddLog(string direction, string frame, string message)
    {
        var entry = new LogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), direction, frame, message);
        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);
        CaptureLogEntry(entry);
        if (_autoScrollLogs && SystemLogGrid.Items.Count > 0)
            SystemLogGrid.ScrollIntoView(SystemLogGrid.Items[^1]);
    }

    private void AddPortLog(PortDefinition port, string direction, string frame, string message)
    {
        var entry = new LogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), direction, frame, message);
        port.LogEntries.Add(entry);
        while (port.LogEntries.Count > MaxLogEntries)
            port.LogEntries.RemoveAt(0);
        CaptureLogEntry(entry);
        if (_autoScrollLogs && ReferenceEquals(SelectedPort, port) && LogGrid.Items.Count > 0)
            LogGrid.ScrollIntoView(LogGrid.Items[^1]);
    }

    private void AddPortWarningLog(PortDefinition port, string frame, string message)
    {
        var entry = new LogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            "ПРЕДУПРЕЖДЕНИЕ", frame, message);
        port.WarningEntries.Add(entry);
        while (port.WarningEntries.Count > MaxLogEntries)
            port.WarningEntries.RemoveAt(0);
        CaptureLogEntry(entry);
        if (_autoScrollLogs && ReferenceEquals(SelectedPort, port) && WarningGrid.Items.Count > 0)
            WarningGrid.ScrollIntoView(WarningGrid.Items[^1]);
    }

    private void CaptureLogEntry(LogEntry entry)
    {
        if (_logCaptureTimer.IsEnabled && _logCaptureStopwatch.Elapsed <= _logCaptureDuration)
            _logCaptureEntries.Add(entry);
    }

    private static string FormatFrame(IEnumerable<byte> frame) =>
        Convert.ToHexString(frame.ToArray());

    protected override void OnClosed(EventArgs e)
    {
        _scriptTimer.Stop();
        _logCaptureTimer.Stop();
        foreach (var runtime in _portRuntimes.Values)
            runtime.Serial.Dispose();
        _portRuntimes.Clear();
        base.OnClosed(e);
    }

    private sealed class PortRuntime(PortDefinition definition)
    {
        public PortDefinition Definition { get; } = definition;
        public SerialPortService Serial { get; } = new();
        public ModbusRtuProcessor Processor { get; } = new();
    }

}
