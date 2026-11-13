using System.IO;
using System.IO.Ports;
using System.Text.Json;
using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Services;

public sealed class SimulatorConfiguration
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "None";
    public int StopBits { get; set; } = 1;
    public int FrameAssemblyTimeoutMs { get; set; } = SerialPortService.DefaultFrameAssemblyTimeoutMs;
    public List<SlaveConfiguration> Slaves { get; set; } = new();
    public List<ServerScript> Scripts { get; set; } = new();
    public List<PortEntryConfiguration> Ports { get; set; } = new();

    public static SimulatorConfiguration FromPorts(
        IEnumerable<PortDefinition> ports, IEnumerable<ServerScript> scripts)
    {
        return new SimulatorConfiguration
        {
            Scripts = scripts.ToList(),
            Ports = ports.Select(port => new PortEntryConfiguration
            {
                Name = port.Name,
                PortName = port.PortName,
                BaudRate = port.BaudRate,
                Parity = port.Parity,
                StopBits = port.StopBits,
                FrameAssemblyTimeoutMs = port.FrameAssemblyTimeoutMs,
                IsEnabled = port.IsEnabled,
                Slaves = port.Slaves.Select(SerializeSlave).ToList()
            }).ToList()
        };
    }

    public List<PortDefinition> ToPorts()
    {
        if (Ports is null || Ports.Count == 0)
        {
            var legacyPort = new PortDefinition("Порт1", PortName)
            {
                BaudRate = BaudRate,
                Parity = Parity,
                StopBits = StopBits,
                FrameAssemblyTimeoutMs = FrameAssemblyTimeoutMs
            };
            foreach (var slave in ToSlaves())
                legacyPort.Slaves.Add(slave);
            return new List<PortDefinition> { legacyPort };
        }

        if (Ports.Count > 10 || Ports.Any(port => port is null || port.Slaves is null))
            throw new InvalidDataException("Конфигурация должна содержать не более 10 корректных портов.");
        if (Ports.Any(port => string.IsNullOrWhiteSpace(port.Name)) ||
            Ports.Select(port => port.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Ports.Count)
            throw new InvalidDataException("Псевдонимы портов должны быть заполнены и уникальны.");
        if (Ports.Any(port => string.Equals(port.Name, "THIS", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Псевдоним порта THIS зарезервирован для внутренних сценариев устройств.");

        var result = new List<PortDefinition>();
        foreach (var savedPort in Ports)
        {
            if (savedPort.BaudRate <= 0 ||
                !Enum.TryParse<Parity>(savedPort.Parity, true, out _) ||
                savedPort.StopBits is not (1 or 2) ||
                savedPort.FrameAssemblyTimeoutMs is < SerialPortService.MinimumFrameAssemblyTimeoutMs or
                > SerialPortService.MaximumFrameAssemblyTimeoutMs)
                throw new InvalidDataException($"Порт «{savedPort.Name}»: некорректные параметры последовательного порта.");

            var port = new PortDefinition(savedPort.Name, savedPort.PortName)
            {
                BaudRate = savedPort.BaudRate,
                Parity = savedPort.Parity,
                StopBits = savedPort.StopBits,
                FrameAssemblyTimeoutMs = savedPort.FrameAssemblyTimeoutMs,
                IsEnabled = savedPort.IsEnabled
            };
            var portSlaveConfiguration = new SimulatorConfiguration
            {
                FrameAssemblyTimeoutMs = savedPort.FrameAssemblyTimeoutMs,
                Slaves = savedPort.Slaves
            };
            foreach (var slave in portSlaveConfiguration.ToSlaves(allowEmpty: true))
                port.Slaves.Add(slave);
            result.Add(port);
        }
        return result;
    }

    public static SimulatorConfiguration FromSlaves(
        IEnumerable<ModbusSlave> slaves, IEnumerable<ServerScript> scripts,
        string portName, int baudRate, string parity, int stopBits,
        int frameAssemblyTimeoutMs = SerialPortService.DefaultFrameAssemblyTimeoutMs)
    {
        if (frameAssemblyTimeoutMs is < SerialPortService.MinimumFrameAssemblyTimeoutMs or
            > SerialPortService.MaximumFrameAssemblyTimeoutMs)
            throw new InvalidDataException(
                $"Максимальная пауза между частями кадра должна быть от {SerialPortService.MinimumFrameAssemblyTimeoutMs} до {SerialPortService.MaximumFrameAssemblyTimeoutMs} мс.");

        return new SimulatorConfiguration
        {
            PortName = portName,
            BaudRate = baudRate,
            Parity = parity,
            StopBits = stopBits,
            FrameAssemblyTimeoutMs = frameAssemblyTimeoutMs,
            Scripts = scripts.ToList(),
            Slaves = slaves.Select(SerializeSlave).ToList()
        };
    }

    public List<ModbusSlave> ToSlaves(bool allowEmpty = false)
    {
        if (FrameAssemblyTimeoutMs is < SerialPortService.MinimumFrameAssemblyTimeoutMs or
            > SerialPortService.MaximumFrameAssemblyTimeoutMs)
            throw new InvalidDataException(
                $"Максимальная пауза между частями кадра должна быть от {SerialPortService.MinimumFrameAssemblyTimeoutMs} до {SerialPortService.MaximumFrameAssemblyTimeoutMs} мс.");
        if (Slaves is null || (!allowEmpty && Slaves.Count == 0) || Slaves.Count > 247)
            throw new InvalidDataException(allowEmpty
                ? "В порту поддерживается не более 247 устройств."
                : "Конфигурация должна содержать от 1 до 247 устройств.");
        if (Slaves.Any(slave => slave is null || slave.Points is null || slave.Scripts is null ||
                            slave.Variables is null || slave.MacroActions is null ||
                                slave.Scripts.Any(script => script is null) ||
                            slave.MacroActions.Any(action => action is null) ||
                            slave.Points.Any(point => point is null) ||
                            slave.Variables.Any(variable => variable is null) || slave.Id is < 1 or > 247) ||
            Slaves.Select(slave => slave.Id).Distinct().Count() != Slaves.Count)
            throw new InvalidDataException("ID устройств должны быть уникальными и находиться в диапазоне 1–247.");
        if (Slaves.Any(slave => string.IsNullOrWhiteSpace(slave.Name)) ||
            Slaves.Select(slave => slave.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Slaves.Count)
            throw new InvalidDataException("Имена устройств должны быть заполнены и уникальны.");

        var result = new List<ModbusSlave>();
        foreach (var savedSlave in Slaves)
        {
            var slave = new ModbusSlave(savedSlave.Name, savedSlave.Id, populateDefaultMap: false)
                { IsEnabled = savedSlave.IsEnabled };
            foreach (var script in savedSlave.Scripts)
                slave.Scripts.Add(script.Clone(preserveId: true));
            foreach (var savedVariable in savedSlave.Variables)
            {
                var variableType = ParseEnum(
                    savedVariable.Type, VirtualVariableType.Bool, "тип виртуальной переменной");
                var variable = new VirtualVariable(
                    savedVariable.Name,
                    variableType)
                {
                    ValueText = savedVariable.ValueText ?? VirtualVariable.DefaultValue(variableType)
                };
                try
                {
                    TagScriptEngine.ValidateVirtualVariable(variable);
                }
                catch (TagScriptException ex)
                {
                    throw new InvalidDataException(
                        $"Устройство {savedSlave.Id}, переменная «{variable.Name}»: {ex.Message}", ex);
                }
                slave.VirtualVariables.Add(variable);
            }
            foreach (var action in savedSlave.MacroActions)
                slave.MacroActions.Add(action.Clone(preserveId: true));
            foreach (var areaGroup in savedSlave.Points.GroupBy(point =>
                         Enum.TryParse<RegisterArea>(point.Area, out var area)
                             && Enum.IsDefined(area)
                             ? area
                             : throw new InvalidDataException($"Неизвестная область памяти: {point.Area}")))
            {
                var points = slave.GetPoints(areaGroup.Key);
                points.Clear();
                if (areaGroup.Select(point => point.Address).Distinct().Count() != areaGroup.Count())
                    throw new InvalidDataException($"В устройстве {savedSlave.Id} повторяются адреса.");
                foreach (var point in areaGroup)
                {
                    if (areaGroup.Key is RegisterArea.Coils or RegisterArea.DiscreteInputs && point.Value > 1)
                        throw new InvalidDataException("Значения coils и discrete inputs должны быть 0 или 1.");
                    points.Add(new MemoryPoint
                    {
                        Name = string.IsNullOrWhiteSpace(point.Name)
                            ? ModbusSlave.DefaultTagName(areaGroup.Key, point.Address)
                            : point.Name,
                        Address = point.Address,
                        Value = point.Value,
                        ValueType = ParseEnum(point.ValueType, RegisterValueType.UInt16, "тип регистра"),
                        FloatWordOrder = ParseEnum(point.FloatWordOrder, FloatWordOrder.ABCD, "порядок слов float")
                    });
                    var loadedPoint = points[^1];
                    if (point.Bits is not null)
                    {
                        foreach (var bit in point.Bits)
                        {
                            if (bit is null || bit.Index is < 0 or > 15 || string.IsNullOrWhiteSpace(bit.Name))
                                throw new InvalidDataException($"Устройство {savedSlave.Id}: некорректное имя бита.");
                            loadedPoint.Bits[bit.Index].Name = bit.Name;
                        }
                        if (point.Bits.Select(bit => bit.Index).Distinct().Count() != point.Bits.Count ||
                            point.Bits.Count != 16)
                            throw new InvalidDataException($"Устройство {savedSlave.Id}: у регистра должно быть 16 уникальных битов.");
                    }
                }
            }
            var tags = Enum.GetValues<RegisterArea>().SelectMany(slave.GetPoints).ToArray();
            if (tags.Any(point => string.IsNullOrWhiteSpace(point.Name)) ||
                tags.Select(point => point.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Length)
                throw new InvalidDataException($"Устройство {savedSlave.Id}: имена тегов должны быть заполнены и уникальны.");
            var variableNames = slave.VirtualVariables.Select(variable => variable.Name).ToArray();
            if (variableNames.Any(string.IsNullOrWhiteSpace) ||
                variableNames.Concat(tags.Select(point => point.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != variableNames.Length + tags.Length)
                throw new InvalidDataException(
                    $"Устройство {savedSlave.Id}: имена виртуальных переменных должны быть заполнены и не совпадать с тегами.");
            foreach (var area in Enum.GetValues<RegisterArea>())
            {
                var areaPoints = slave.GetPoints(area);
                foreach (var point in areaPoints)
                {
                    if (point.Bits.Count != 16 ||
                        point.Bits.Any(bit => string.IsNullOrWhiteSpace(bit.Name)) ||
                        point.Bits.Select(bit => bit.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 16)
                        throw new InvalidDataException($"Устройство {savedSlave.Id}: имена битов регистра должны быть заполнены и уникальны.");
                    if (point.ValueType != RegisterValueType.Float32)
                        continue;
                    if (area is not (RegisterArea.HoldingRegisters or RegisterArea.InputRegisters))
                        throw new InvalidDataException("Float32 доступен только для регистров Holding/Input.");
                    if (point.Address == ushort.MaxValue || slave.FindPoint(area, (ushort)(point.Address + 1)) is null)
                        throw new InvalidDataException($"Float32 по адресу {point.Address} требует второй последовательный регистр.");
                    if (point.Address > 0 &&
                        slave.FindPoint(area, (ushort)(point.Address - 1)) is { ValueType: RegisterValueType.Float32 })
                        throw new InvalidDataException($"Float32 по адресу {point.Address} перекрывает предыдущую пару регистров.");
                }
            }
            result.Add(slave);
        }
        return result;
    }

    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    private static T ParseEnum<T>(string? value, T defaultValue, string description) where T : struct, Enum =>
        string.IsNullOrEmpty(value)
            ? defaultValue
            : Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new InvalidDataException($"Некорректный {description}: {value}.");

    private static SlaveConfiguration SerializeSlave(ModbusSlave slave) => new()
    {
        Name = slave.Name,
        Id = slave.Id,
        IsEnabled = slave.IsEnabled,
        Scripts = slave.Scripts.Select(script => script.Clone(preserveId: true)).ToList(),
        MacroActions = slave.MacroActions.Select(action => action.Clone(preserveId: true)).ToList(),
        Variables = slave.VirtualVariables.Select(variable => new VirtualVariableConfiguration
        {
            Name = variable.Name,
            Type = variable.Type.ToString(),
            ValueText = variable.ValueText
        }).ToList(),
        Points = Enum.GetValues<RegisterArea>()
            .SelectMany(area => slave.GetPoints(area).Select(point => new PointConfiguration
            {
                Name = point.Name,
                Area = area.ToString(),
                Address = point.Address,
                Value = point.Value,
                ValueType = point.ValueType.ToString(),
                FloatWordOrder = point.FloatWordOrder.ToString(),
                Bits = point.Bits.Select(bit => new BitConfiguration
                {
                    Index = bit.Index,
                    Name = bit.Name
                }).ToList()
            }))
            .ToList()
    };
}

public sealed class PortEntryConfiguration
{
    public string Name { get; set; } = "";
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "None";
    public int StopBits { get; set; } = 1;
    public int FrameAssemblyTimeoutMs { get; set; } = SerialPortService.DefaultFrameAssemblyTimeoutMs;
    public bool IsEnabled { get; set; } = true;
    public List<SlaveConfiguration> Slaves { get; set; } = new();
}

public sealed class SlaveConfiguration
{
    public string Name { get; set; } = "";
    public ushort Id { get; set; }
    public bool IsEnabled { get; set; }
    public List<ServerScript> Scripts { get; set; } = new();
    public List<VirtualVariableMacroAction> MacroActions { get; set; } = new();
    public List<VirtualVariableConfiguration> Variables { get; set; } = new();
    public List<PointConfiguration> Points { get; set; } = new();
}

public sealed class VirtualVariableConfiguration
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = nameof(VirtualVariableType.Bool);
    public string? ValueText { get; set; }
}

public sealed class PointConfiguration
{
    public string Name { get; set; } = "";
    public string Area { get; set; } = "";
    public ushort Address { get; set; }
    public ushort Value { get; set; }
    public string? ValueType { get; set; }
    public string? FloatWordOrder { get; set; }
    public List<BitConfiguration>? Bits { get; set; }
}

public sealed class BitConfiguration
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}
