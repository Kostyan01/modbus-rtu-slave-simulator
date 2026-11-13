using ModbusRtuSimulator.Models;
using ModbusRtuSimulator.Protocol;
using ModbusRtuSimulator.Services;
using System.Text.Json;
using System.Text;
using Xunit;

namespace ModbusRtuSimulator.Tests;

public sealed class ModbusRtuProcessorTests
{
    private readonly ModbusRtuProcessor _processor = new();

    [Fact]
    public void AddCrc_MatchesModbusRtuKnownFrame()
    {
        var frame = ModbusRtuProcessor.AddCrc(new byte[] { 1, 3, 0, 0, 0, 10 });

        Assert.Equal(new byte[] { 1, 3, 0, 0, 0, 10, 0xC5, 0xCD }, frame);
        Assert.True(ModbusRtuProcessor.HasValidCrc(frame));
    }

    [Fact]
    public void LogFileWriter_WritesUtf8CsvAndEscapesFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modbus-log-{Guid.NewGuid():N}.csv");
        try
        {
            LogFileWriter.WriteCsv(path, new[]
            {
                new LogEntry("2026-11-11 16:00:00.000", "ПРЕДУПРЕЖДЕНИЕ", "01 04", "Не настроено \"10001\"")
            });

            var content = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("Time,Direction,Frame,Message", content);
            Assert.Contains("\"ПРЕДУПРЕЖДЕНИЕ\"", content);
            Assert.Contains("\"Не настроено \"\"10001\"\"\"", content);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void NewDevice_CanStartWithEmptyMemoryMaps()
    {
        var slave = new ModbusSlave("Empty", 1, populateDefaultMap: false);

        Assert.All(Enum.GetValues<RegisterArea>(), area => Assert.Empty(slave.GetPoints(area)));
    }

    [Fact]
    public void Clone_CopiesMapsAndValuesWithoutSharingTagInstances()
    {
        var source = new ModbusSlave("Template", 1);
        source.HoldingRegisters[2].Name = "Temperature";
        source.HoldingRegisters[2].Value = 321;
        source.IsEnabled = false;
        source.Scripts.Add(new ServerScript
        {
            Name = "Initialize",
            Code = "THIS.Temperature := 321;",
            Trigger = ScriptTrigger.Cyclic
        });
        source.VirtualVariables.Add(new VirtualVariable("Enabled", VirtualVariableType.Bool)
        {
            ValueText = "TRUE"
        });

        var clone = source.CloneAs("Template_Copy", 2);

        Assert.Equal("Template_Copy", clone.Name);
        Assert.Equal((ushort)2, clone.Id);
        Assert.False(clone.IsEnabled);
        Assert.Equal("Temperature", clone.HoldingRegisters[2].Name);
        Assert.Equal((ushort)321, clone.HoldingRegisters[2].Value);
        var sourceScript = Assert.Single(source.Scripts);
        var clonedScript = Assert.Single(clone.Scripts);
        Assert.NotSame(sourceScript, clonedScript);
        Assert.NotEqual(sourceScript.Id, clonedScript.Id);
        Assert.Equal(sourceScript.Code, clonedScript.Code);
        var sourceVariable = Assert.Single(source.VirtualVariables);
        var clonedVariable = Assert.Single(clone.VirtualVariables);
        Assert.NotSame(sourceVariable, clonedVariable);
        Assert.Equal(sourceVariable.ValueText, clonedVariable.ValueText);
        clonedScript.Name = "Independent";
        Assert.Equal("Initialize", sourceScript.Name);
        clone.HoldingRegisters[2].Value = 7;
        Assert.Equal((ushort)321, source.HoldingRegisters[2].Value);
    }

    [Fact]
    public void Clone_RemapsMacroActionToClonedLocalEventScript()
    {
        var source = new ModbusSlave("Template", 1, populateDefaultMap: false);
        source.VirtualVariables.Add(new VirtualVariable("Ready", VirtualVariableType.Bool));
        var eventScript = new ServerScript
        {
            Name = "Run",
            Trigger = ScriptTrigger.Event,
            Code = "THIS.Ready := TRUE;"
        };
        source.Scripts.Add(eventScript);
        var sourcePort = new PortDefinition("LineA");
        sourcePort.Slaves.Add(source);
        source.MacroActions.Add(new VirtualVariableMacroAction
        {
            SourceVariableName = "Ready",
            TargetScriptId = eventScript.Id,
            TargetScope = MacroTargetScope.Device,
            TargetPortAlias = source.PortAlias,
            TargetDeviceId = source.Id,
            Condition = MacroCondition.True
        });

        var clone = source.CloneAs("Template_Copy", 2);

        var clonedAction = Assert.Single(clone.MacroActions);
        var clonedScript = Assert.Single(clone.Scripts);
        Assert.NotEqual(eventScript.Id, clonedAction.TargetScriptId);
        Assert.Equal(clonedScript.Id, clonedAction.TargetScriptId);
        Assert.Equal(clone.Id, clonedAction.TargetDeviceId);
        Assert.Equal(clone.PortAlias, clonedAction.TargetPortAlias);
    }

    [Fact]
    public void Process_ReadHoldingRegister_ReturnsValueAndCrc()
    {
        var slave = new ModbusSlave("Test", 1);
        slave.HoldingRegisters[0].Value = 0x1234;

        var response = Process(1, 3, 0, 0, 0, 1, slave);

        Assert.NotNull(response);
        Assert.Equal(new byte[] { 1, 3, 2, 0x12, 0x34 }, response![..5]);
        Assert.True(ModbusRtuProcessor.HasValidCrc(response));
    }

    [Fact]
    public void Process_WriteMultipleRegisters_UpdatesAllValues()
    {
        var slave = new ModbusSlave("Test", 1);
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 1, 16, 0, 0, 0, 2, 4, 0x12, 0x34, 0xAB, 0xCD });

        var response = _processor.Process(request, new[] { slave });

        Assert.NotNull(response);
        Assert.Equal((ushort)0x1234, slave.HoldingRegisters[0].Value);
        Assert.Equal((ushort)0xABCD, slave.HoldingRegisters[1].Value);
        Assert.Equal(ModbusRtuProcessor.AddCrc(new byte[] { 1, 16, 0, 0, 0, 2 }), response);
    }

    [Fact]
    public void Process_WriteMultipleRegistersAndReadInputRegisters_SupportsFloat32()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        var first = new MemoryPoint
        {
            Name = "Temperature",
            Address = 2,
            ValueType = RegisterValueType.Float32,
            FloatWordOrder = FloatWordOrder.CDAB
        };
        slave.HoldingRegisters.Add(first);
        slave.HoldingRegisters.Add(new MemoryPoint { Name = "Temperature_2", Address = 3 });
        var inputWords = FloatRegisterCodec.Encode(98.5f, FloatWordOrder.DCBA);
        slave.InputRegisters.Add(new MemoryPoint
        {
            Name = "InputTemperature",
            Address = 2,
            Value = inputWords.First,
            ValueType = RegisterValueType.Float32,
            FloatWordOrder = FloatWordOrder.DCBA
        });
        slave.InputRegisters.Add(new MemoryPoint { Name = "InputTemperature_2", Address = 3, Value = inputWords.Second });
        var words = FloatRegisterCodec.Encode(123.25f, FloatWordOrder.CDAB);
        var write = ModbusRtuProcessor.AddCrc(new byte[]
        {
            1, 16, 0, 2, 0, 2, 4,
            (byte)(words.First >> 8), (byte)words.First,
            (byte)(words.Second >> 8), (byte)words.Second
        });

        var writeResponse = _processor.Process(write, new[] { slave });
        var holdingReadResponse = Process(1, 3, 0, 2, 0, 2, slave);
        var inputReadResponse = Process(1, 4, 0, 2, 0, 2, slave);

        Assert.Equal(ModbusRtuProcessor.AddCrc(new byte[] { 1, 16, 0, 2, 0, 2 }), writeResponse);
        Assert.NotNull(holdingReadResponse);
        Assert.Equal(new byte[]
        {
            1, 3, 4,
            (byte)(words.First >> 8), (byte)words.First,
            (byte)(words.Second >> 8), (byte)words.Second
        }, holdingReadResponse![..7]);
        Assert.NotNull(inputReadResponse);
        Assert.Equal(new byte[]
        {
            1, 4, 4,
            (byte)(inputWords.First >> 8), (byte)inputWords.First,
            (byte)(inputWords.Second >> 8), (byte)inputWords.Second
        }, inputReadResponse![..7]);
        Assert.True(slave.TryReadFloat(RegisterArea.HoldingRegisters, 2, out var value));
        Assert.Equal(123.25f, value);
        Assert.True(slave.TryReadFloat(RegisterArea.InputRegisters, 2, out var inputValue));
        Assert.Equal(98.5f, inputValue);
    }

    [Fact]
    public void Process_WriteSingleHoldingRegisterAndReadInputRegisterAtAddress10000_KeepAreasSeparate()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        slave.HoldingRegisters.Add(new MemoryPoint { Name = "Output", Address = 10000 });
        slave.InputRegisters.Add(new MemoryPoint { Name = "Input", Address = 10000, Value = 0xA55A });
        slave.InputRegisters.Add(new MemoryPoint { Name = "InputWord2", Address = 10001, Value = 0 });

        var writeResponse = _processor.Process(
            ModbusRtuProcessor.AddCrc(new byte[] { 1, 6, 0x27, 0x10, 0x12, 0x34 }), new[] { slave });
        var readResponse = Process(1, 4, 0x27, 0x10, 0, 1, slave);

        Assert.Equal((ushort)0x1234, slave.HoldingRegisters[0].Value);
        Assert.Equal((ushort)0xA55A, slave.InputRegisters[0].Value);
        Assert.Equal(ModbusRtuProcessor.AddCrc(new byte[] { 1, 6, 0x27, 0x10, 0x12, 0x34 }), writeResponse);
        Assert.NotNull(readResponse);
        Assert.Equal(new byte[] { 1, 4, 2, 0xA5, 0x5A }, readResponse![..5]);
    }

    [Fact]
    public void Process_ReadTwoInputRegistersAtAddress10000_ReturnsBothExplicitWords()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        slave.InputRegisters.Add(new MemoryPoint { Name = "Input", Address = 10000, Value = 0x8000 });
        slave.InputRegisters.Add(new MemoryPoint { Name = "InputWord2", Address = 10001, Value = 0 });

        var response = Process(1, 4, 0x27, 0x10, 0, 2, slave);

        Assert.NotNull(response);
        Assert.Equal(new byte[] { 1, 4, 4, 0x80, 0x00, 0x00, 0x00 }, response![..7]);
    }

    [Fact]
    public void ModbusReadWarning_ExplainsMissingSecondInputRegister()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        slave.InputRegisters.Add(new MemoryPoint { Name = "Inputs", Address = 10000, Value = 0x8000 });
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 1, 4, 0x27, 0x10, 0, 2 });
        var response = _processor.Process(request, new[] { slave });

        var hasWarning = ModbusReadWarning.TryGetMissingAddressWarning(request, response, new[] { slave }, out var warning);

        Assert.True(hasWarning);
        Assert.Contains("запросил 2 рег.", warning);
        Assert.Contains("не настроены адреса: 10001", warning);
        Assert.Contains("Добавьте их в карту InputRegisters", warning);
    }

    [Theory]
    [InlineData(FloatWordOrder.ABCD, 0x3F80, 0x0000)]
    [InlineData(FloatWordOrder.CDAB, 0x0000, 0x3F80)]
    [InlineData(FloatWordOrder.BADC, 0x803F, 0x0000)]
    [InlineData(FloatWordOrder.DCBA, 0x0000, 0x803F)]
    public void FloatRegisterCodec_UsesExpectedByteAndWordOrder(
        FloatWordOrder order, ushort expectedFirst, ushort expectedSecond)
    {
        var words = FloatRegisterCodec.Encode(1f, order);

        Assert.Equal(expectedFirst, words.First);
        Assert.Equal(expectedSecond, words.Second);
        Assert.Equal(1f, FloatRegisterCodec.Decode(words.First, words.Second, order));
    }

    [Fact]
    public void RegisterTreeRow_ChangesRegisterAndFloatPairAddressesWithoutReplacingTags()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        var first = new MemoryPoint
        {
            Name = "Temperature",
            Address = 10000,
            ValueType = RegisterValueType.Float32
        };
        var second = new MemoryPoint { Name = "TemperatureWord2", Address = 10001 };
        slave.InputRegisters.Add(first);
        slave.InputRegisters.Add(second);
        var row = new RegisterTreeRow(slave, RegisterArea.InputRegisters, first);

        row.AddressText = "12000";

        Assert.Equal("12000", row.AddressText);
        Assert.Same(first, slave.FindPoint(RegisterArea.InputRegisters, 12000));
        Assert.Same(second, slave.FindPoint(RegisterArea.InputRegisters, 12001));
        Assert.Equal("Temperature", first.Name);
        Assert.Equal("TemperatureWord2", second.Name);
        Assert.True(RegisterTreeRow.IsSecondFloatWord(slave, RegisterArea.InputRegisters, second));
    }

    [Fact]
    public void RegisterTreeRow_RejectsConflictingAddressAndUpdatesFloatInline()
    {
        var slave = new ModbusSlave("Test", 1, populateDefaultMap: false);
        var first = new MemoryPoint { Name = "Temperature", Address = 2, ValueType = RegisterValueType.Float32 };
        var second = new MemoryPoint { Name = "TemperatureWord2", Address = 3 };
        slave.InputRegisters.Add(first);
        slave.InputRegisters.Add(second);
        slave.InputRegisters.Add(new MemoryPoint { Name = "Reserved", Address = 9 });
        var row = new RegisterTreeRow(slave, RegisterArea.InputRegisters, first);

        row.AddressText = "8";
        row.FloatValueText = "12.5";

        Assert.Equal("Указанный адрес уже занят в этой области памяти.", row.AddressError);
        Assert.Equal((ushort)2, first.Address);
        Assert.Equal((ushort)3, second.Address);
        Assert.True(slave.TryReadFloat(RegisterArea.InputRegisters, 2, out var value));
        Assert.Equal(12.5f, value);

        row.FloatWordOrder = FloatWordOrder.DCBA;

        Assert.True(slave.TryReadFloat(RegisterArea.InputRegisters, 2, out value));
        Assert.Equal(12.5f, value);
        Assert.Equal(value.ToString("G", System.Globalization.CultureInfo.CurrentCulture), row.FloatValueText);
    }

    [Fact]
    public void Process_WriteMultipleRegistersWithMissingAddress_DoesNotPartiallyWrite()
    {
        var slave = new ModbusSlave("Test", 1);
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 1, 16, 0, 15, 0, 2, 4, 0x12, 0x34, 0xAB, 0xCD });

        var response = _processor.Process(request, new[] { slave });

        Assert.Equal((ushort)0, slave.HoldingRegisters[15].Value);
        Assert.Equal(ModbusRtuProcessor.AddCrc(new byte[] { 1, 0x90, 2 }), response);
    }

    [Fact]
    public void Process_BroadcastWrite_UpdatesEveryEnabledSlaveWithoutReply()
    {
        var first = new ModbusSlave("First", 1);
        var second = new ModbusSlave("Second", 2);
        var disabled = new ModbusSlave("Disabled", 3) { IsEnabled = false };
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 0, 6, 0, 0, 0x12, 0x34 });

        var response = _processor.Process(request, new[] { first, second, disabled });

        Assert.Null(response);
        Assert.Equal((ushort)0x1234, first.HoldingRegisters[0].Value);
        Assert.Equal((ushort)0x1234, second.HoldingRegisters[0].Value);
        Assert.Equal((ushort)0, disabled.HoldingRegisters[0].Value);
    }

    [Fact]
    public void Process_BadCrc_DoesNotRespondOrModifyMemory()
    {
        var slave = new ModbusSlave("Test", 1);
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 1, 6, 0, 0, 0, 5 });
        request[^1] ^= 0xFF;

        var response = _processor.Process(request, new[] { slave });

        Assert.Null(response);
        Assert.Equal((ushort)0, slave.HoldingRegisters[0].Value);
    }

    [Fact]
    public void Configuration_RoundTripsSlaveMemory()
    {
        var slave = new ModbusSlave("Test", 7);
        slave.HoldingRegisters[0].Value = 123;
        slave.HoldingRegisters[0].Name = "Temperature";
        slave.HoldingRegisters[0].Bits[5].Name = "PumpRunning";
        slave.HoldingRegisters[0].Bits[5].IsSet = true;
        slave.HoldingRegisters[2].ValueType = RegisterValueType.Float32;
        slave.HoldingRegisters[2].FloatWordOrder = FloatWordOrder.DCBA;
        var scripts = new[]
        {
            new ServerScript
            {
                Name = "Increment",
                Code = "Test.Temperature = Test.Temperature + 1;",
                Trigger = ScriptTrigger.Cyclic
            }
        };
        var internalScript = new ServerScript
        {
            Name = "Device-local",
            Code = "THIS.Temperature := 42;",
            Trigger = ScriptTrigger.Cyclic
        };
        slave.Scripts.Add(internalScript);
        var configuration = SimulatorConfiguration.FromSlaves(new[] { slave }, scripts, "COM1", 19200, "Even", 1);

        var restored = JsonSerializer.Deserialize<SimulatorConfiguration>(
            JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions))!.ToSlaves();

        Assert.Single(restored);
        Assert.Equal((ushort)7, restored[0].Id);
        Assert.Equal((ushort)123, restored[0].HoldingRegisters[0].Value);
        Assert.Equal("Temperature", restored[0].HoldingRegisters[0].Name);
        Assert.Equal("PumpRunning", restored[0].HoldingRegisters[0].Bits[5].Name);
        Assert.True(restored[0].HoldingRegisters[0].Bits[5].IsSet);
        Assert.Equal(RegisterValueType.Float32, restored[0].HoldingRegisters[2].ValueType);
        Assert.Equal(FloatWordOrder.DCBA, restored[0].HoldingRegisters[2].FloatWordOrder);
        Assert.Equal(internalScript.Id, Assert.Single(restored[0].Scripts).Id);
        Assert.Equal("Device-local", restored[0].Scripts[0].Name);
        Assert.Equal("Increment", JsonSerializer.Deserialize<SimulatorConfiguration>(
            JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions))!.Scripts[0].Name);
    }

    [Fact]
    public void Configuration_RoundTripsMultiplePortSettingsAndDevices()
    {
        var firstPort = new PortDefinition("LineA", "COM3")
        {
            BaudRate = 19200,
            Parity = "Even",
            StopBits = 1,
            FrameAssemblyTimeoutMs = 45
        };
        firstPort.Slaves.Add(new ModbusSlave("BRMOD1", 1, populateDefaultMap: false));
        firstPort.Slaves[0].Scripts.Add(new ServerScript
        {
            Name = "Local",
            Code = "THIS.Command := 1;"
        });
        firstPort.Slaves[0].VirtualVariables.Add(
            new VirtualVariable("Ready", VirtualVariableType.Bool) { ValueText = "TRUE" });
        firstPort.Slaves[0].VirtualVariables.Add(
            new VirtualVariable("TargetBit", VirtualVariableType.UInt) { ValueText = "17" });
        var secondPort = new PortDefinition("LineB", "COM4")
        {
            BaudRate = 38400,
            IsEnabled = false
        };
        secondPort.Slaves.Add(new ModbusSlave("BRMOD1", 1, populateDefaultMap: false));
        var globalScript = new ServerScript
        {
            Name = "Global",
            Code = "LineA.BRMOD1.Tag := 5;",
            TargetKind = ScriptTargetKind.Bit,
            TargetBit = "PumpRunning",
            TriggerValue = 3.5m
        };
        var configuration = SimulatorConfiguration.FromPorts(
            new[] { firstPort, secondPort }, new[] { globalScript });

        var restored = JsonSerializer.Deserialize<SimulatorConfiguration>(
            JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions))!.ToPorts();

        Assert.Equal(2, restored.Count);
        Assert.Equal("LineA", restored[0].Name);
        Assert.Equal("COM3", restored[0].PortName);
        Assert.Equal(19200, restored[0].BaudRate);
        Assert.Equal("Even", restored[0].Parity);
        Assert.Equal(45, restored[0].FrameAssemblyTimeoutMs);
        Assert.Equal("LineA", restored[0].Slaves[0].PortAlias);
        Assert.Equal("LineB", restored[1].Slaves[0].PortAlias);
        Assert.False(restored[1].IsEnabled);
        Assert.Equal("Local", Assert.Single(restored[0].Slaves[0].Scripts).Name);
        Assert.Equal("TRUE", restored[0].Slaves[0].VirtualVariables[0].ValueText);
        Assert.Equal(VirtualVariableType.UInt, restored[0].Slaves[0].VirtualVariables[1].Type);
        var restoredScript = JsonSerializer.Deserialize<SimulatorConfiguration>(
            JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions))!.Scripts[0];
        Assert.Equal(ScriptTargetKind.Bit, restoredScript.TargetKind);
        Assert.Equal("PumpRunning", restoredScript.TargetBit);
        Assert.Equal(3.5m, restoredScript.TriggerValue);
    }

    [Fact]
    public void Configuration_MigratesLegacySinglePortFile()
    {
        var legacy = SimulatorConfiguration.FromSlaves(
            new[] { new ModbusSlave("BRMOD1", 1, populateDefaultMap: false) },
            Array.Empty<ServerScript>(), "COM7", 9600, "None", 1);
        var restored = legacy.ToPorts();

        Assert.Single(restored);
        Assert.Equal("Порт1", restored[0].Name);
        Assert.Equal("COM7", restored[0].PortName);
        Assert.Equal("Порт1", restored[0].Slaves[0].PortAlias);
    }

    [Fact]
    public void ScriptEngine_SupportsArithmeticComparisonsAndIfElse()
    {
        var slave = new ModbusSlave("Device 1", 1);
        slave.HoldingRegisters[0].Name = "Temperature";
        slave.HoldingRegisters[0].Value = 20;
        var engine = new TagScriptEngine();

        engine.Execute("""
            VAR
                temperature : UINT;
                alarm : UINT;
            END_VAR
            temperature := "Device 1"."Temperature" + 2 * 3;
            IF temperature >= 26 AND TRUE THEN
                alarm := 7;
            ELSE
                alarm := 1;
            END_IF;
            "Device 1"."Temperature" := temperature;
            "Device 1".HoldingRegister_1 := alarm;
            """, new[] { slave });

        Assert.Equal((ushort)26, slave.HoldingRegisters[0].Value);
        Assert.Equal((ushort)7, slave.HoldingRegisters[1].Value);
    }

    [Fact]
    public void ScriptEngine_ResolvesThisToOwningDeviceForCopiedScenarioLogic()
    {
        var first = new ModbusSlave("BRMOD1", 1);
        var second = new ModbusSlave("BRMOD2", 2);
        first.HoldingRegisters[0].Name = "Command";
        first.HoldingRegisters[1].Name = "Result";
        second.HoldingRegisters[0].Name = "Command";
        second.HoldingRegisters[1].Name = "Result";
        first.HoldingRegisters[0].Value = 15;
        second.HoldingRegisters[0].Value = 27;
        var script = new ServerScript
        {
            Code = "THIS.Result := THIS.Command;"
        };
        var engine = new TagScriptEngine();

        engine.Validate(script, new[] { first, second }, first);
        engine.Execute(script, new[] { first, second }, first);
        var cloneScript = script.Clone();
        engine.Execute(cloneScript, new[] { first, second }, second);

        Assert.Equal((ushort)15, first.HoldingRegisters[1].Value);
        Assert.Equal((ushort)27, second.HoldingRegisters[1].Value);
        Assert.Throws<TagScriptException>(() => engine.Validate(script, new[] { first, second }));
    }

    [Fact]
    public void ScriptEngine_ResolvesPortQualifiedTagsAcrossPorts()
    {
        var firstPort = new PortDefinition("LineA");
        var first = new ModbusSlave("BRMOD1", 1, populateDefaultMap: false);
        first.HoldingRegisters.Add(new MemoryPoint { Name = "Source", Address = 0, Value = 123 });
        firstPort.Slaves.Add(first);
        var secondPort = new PortDefinition("LineB");
        var second = new ModbusSlave("BRMOD1", 1, populateDefaultMap: false);
        second.HoldingRegisters.Add(new MemoryPoint { Name = "Target", Address = 0 });
        secondPort.Slaves.Add(second);
        var slaves = new[] { first, second };

        new TagScriptEngine().Execute(
            "LineB.BRMOD1.Target := LineA.BRMOD1.Source;",
            slaves);

        Assert.Equal((ushort)123, second.HoldingRegisters[0].Value);
    }

    [Fact]
    public void ScriptEngine_SharesTypedVirtualVariablesAcrossScriptsAndPorts()
    {
        var firstPort = new PortDefinition("LineA");
        var first = new ModbusSlave("Local", 1, populateDefaultMap: false);
        first.VirtualVariables.Add(
            new VirtualVariable("Counter", VirtualVariableType.UInt) { ValueText = "5" });
        firstPort.Slaves.Add(first);
        var secondPort = new PortDefinition("LineB");
        var second = new ModbusSlave("Remote", 1, populateDefaultMap: false);
        second.VirtualVariables.Add(
            new VirtualVariable("Reference", VirtualVariableType.Real) { ValueText = "3.5" });
        secondPort.Slaves.Add(second);
        var slaves = new[] { first, second };
        var script = new ServerScript
        {
            Code = "THIS.Counter := LineB.Remote.Reference + THIS.Counter + 0.5;"
        };
        var engine = new TagScriptEngine();

        engine.Validate(script, slaves, first);
        engine.Execute(script, slaves, first);
        Assert.Equal("9", first.VirtualVariables[0].ValueText);

        engine.Execute(new ServerScript { Code = "THIS.Counter := THIS.Counter + 1;" }, slaves, first);
        Assert.Equal("10", first.VirtualVariables[0].ValueText);
    }

    [Theory]
    [InlineData(MacroCondition.Equal, 5, true)]
    [InlineData(MacroCondition.Equal, 4, false)]
    [InlineData(MacroCondition.Greater, 6, true)]
    [InlineData(MacroCondition.GreaterOrEqual, 5, true)]
    [InlineData(MacroCondition.Less, 4, true)]
    [InlineData(MacroCondition.LessOrEqual, 5, true)]
    public void VirtualVariableMacroEvaluator_AppliesNumericComparison(
        MacroCondition condition, decimal value, bool expected)
    {
        var variable = new VirtualVariable("Value", VirtualVariableType.Real)
        {
            ValueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var action = new VirtualVariableMacroAction
        {
            Condition = condition,
            Threshold = 5
        };

        Assert.Equal(expected, VirtualVariableMacroEvaluator.IsSatisfied(action, variable));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void VirtualVariableMacroEvaluator_BoolOnlySupportsTrue(bool value, bool expected)
    {
        var variable = new VirtualVariable("Ready", VirtualVariableType.Bool)
        {
            ValueText = value ? "TRUE" : "FALSE"
        };
        var action = new VirtualVariableMacroAction { Condition = MacroCondition.True };

        Assert.Equal(expected, VirtualVariableMacroEvaluator.IsSatisfied(action, variable));
        Assert.True(VirtualVariableMacroEvaluator.IsValidFor(action, variable));
    }

    [Fact]
    public void VirtualVariableMacroEvaluator_TimeDoesNotSupportTriggers()
    {
        var variable = new VirtualVariable("Delay", VirtualVariableType.Time);
        var action = new VirtualVariableMacroAction { Condition = MacroCondition.Greater };

        Assert.False(VirtualVariableMacroEvaluator.IsValidFor(action, variable));
        Assert.False(VirtualVariableMacroEvaluator.IsSatisfied(action, variable));
    }

    [Fact]
    public void Configuration_RoundTripsVirtualVariableMacroAction()
    {
        var port = new PortDefinition("LineA");
        var slave = new ModbusSlave("Device1", 1, populateDefaultMap: false);
        slave.VirtualVariables.Add(new VirtualVariable("Setpoint", VirtualVariableType.Real));
        port.Slaves.Add(slave);
        var macro = new ServerScript
        {
            Name = "Apply",
            Trigger = ScriptTrigger.Event,
            Code = "LineA.Device1.Setpoint := LineA.Device1.Setpoint + 1.0;"
        };
        var action = new VirtualVariableMacroAction
        {
            SourceVariableName = "Setpoint",
            TargetScriptId = macro.Id,
            TargetScope = MacroTargetScope.Global,
            Condition = MacroCondition.GreaterOrEqual,
            Threshold = 3.5m
        };
        slave.MacroActions.Add(action);
        var configuration = SimulatorConfiguration.FromPorts(new[] { port }, new[] { macro });

        var restored = JsonSerializer.Deserialize<SimulatorConfiguration>(
            JsonSerializer.Serialize(configuration, SimulatorConfiguration.JsonOptions))!.ToPorts();
        var restoredAction = Assert.Single(restored[0].Slaves[0].MacroActions);

        Assert.Equal(action.Id, restoredAction.Id);
        Assert.Equal("Setpoint", restoredAction.SourceVariableName);
        Assert.Equal(macro.Id, restoredAction.TargetScriptId);
        Assert.Equal(MacroTargetScope.Global, restoredAction.TargetScope);
        Assert.Equal(MacroCondition.GreaterOrEqual, restoredAction.Condition);
        Assert.Equal(3.5m, restoredAction.Threshold);
    }

    [Fact]
    public void ScriptEngine_SupportsBoundLocalVariablesAndCopyBack()
    {
        var slave = new ModbusSlave("Device1", 1);
        slave.HoldingRegisters[0].Name = "Temperature";
        slave.HoldingRegisters[0].Value = 24;
        var engine = new TagScriptEngine();

        engine.Execute("""
            VAR
                temperature : UINT;
                alarm : BOOL;
            END_VAR
            temperature := Device1.Temperature;
            temperature := temperature + 3;
            IF temperature >= 27 THEN
                alarm := TRUE;
            END_IF;
            Device1.Temperature := temperature;
            Device1.HoldingRegister_1 := UINT(alarm);
            """, new[] { slave });

        Assert.Equal((ushort)27, slave.HoldingRegisters[0].Value);
        Assert.Equal((ushort)1, slave.HoldingRegisters[1].Value);
    }

    [Fact]
    public void ScriptEngine_CopiesFloat32TagsWithoutLosingPrecision()
    {
        var slave = new ModbusSlave("BRMOD1", 1, populateDefaultMap: false);
        var source = new MemoryPoint
        {
            Name = "УголЗадание",
            Address = 10000,
            ValueType = RegisterValueType.Float32
        };
        var sourceSecond = new MemoryPoint { Name = "УголЗадание_2", Address = 10001 };
        var destination = new MemoryPoint
        {
            Name = "УголЗаслонки",
            Address = 10002,
            ValueType = RegisterValueType.Float32,
            FloatWordOrder = FloatWordOrder.CDAB
        };
        var destinationSecond = new MemoryPoint { Name = "УголЗаслонки_2", Address = 10003 };
        slave.InputRegisters.Add(source);
        slave.InputRegisters.Add(sourceSecond);
        slave.HoldingRegisters.Add(destination);
        slave.HoldingRegisters.Add(destinationSecond);
        Assert.True(slave.TryWriteFloat(RegisterArea.InputRegisters, source.Address, 3.3333335f));
        Assert.True(slave.TryWriteFloat(RegisterArea.HoldingRegisters, destination.Address, 42.0f));

        new TagScriptEngine().Execute(
            "BRMOD1.УголЗаслонки := BRMOD1.УголЗадание;",
            new[] { slave });

        Assert.True(slave.TryReadFloat(RegisterArea.HoldingRegisters, destination.Address, out var result));
        Assert.Equal(3.3333335f, result);
    }

    [Fact]
    public void ScriptEngine_CanCopyFloat32TagsThroughRealVariable()
    {
        var slave = new ModbusSlave("BRMOD1", 1, populateDefaultMap: false);
        var source = new MemoryPoint { Name = "УголЗадание", Address = 2, ValueType = RegisterValueType.Float32 };
        var sourceSecond = new MemoryPoint { Name = "УголЗадание_2", Address = 3 };
        var destination = new MemoryPoint { Name = "УголЗаслонки", Address = 4, ValueType = RegisterValueType.Float32 };
        var destinationSecond = new MemoryPoint { Name = "УголЗаслонки_2", Address = 5 };
        slave.InputRegisters.Add(source);
        slave.InputRegisters.Add(sourceSecond);
        slave.HoldingRegisters.Add(destination);
        slave.HoldingRegisters.Add(destinationSecond);
        Assert.True(slave.TryWriteFloat(RegisterArea.InputRegisters, source.Address, 3.3333335f));

        new TagScriptEngine().Execute("""
            VAR
                angle : REAL;
            END_VAR
            angle := BRMOD1.УголЗадание;
            BRMOD1.УголЗаслонки := angle;
            """, new[] { slave });

        Assert.True(slave.TryReadFloat(RegisterArea.HoldingRegisters, destination.Address, out var result));
        Assert.Equal(3.3333335f, result);
    }

    [Fact]
    public void ScriptEngine_RejectsUseOfVariableBeforeDeclaration()
    {
        var slave = new ModbusSlave("Device1", 1);

        var exception = Assert.Throws<TagScriptException>(() =>
            new TagScriptEngine().Validate("result := 1;", new[] { slave }));

        Assert.Contains("не объявлена", exception.Message);
    }

    [Fact]
    public void ScriptEngine_ReportsUnknownTagsWithoutChangingMemory()
    {
        var slave = new ModbusSlave("Device1", 1);
        var engine = new TagScriptEngine();

        var exception = Assert.Throws<TagScriptException>(() =>
            engine.Execute("Device1.Missing := 5;", new[] { slave }));

        Assert.Contains("не найден", exception.Message);
        Assert.Equal((ushort)0, slave.HoldingRegisters[0].Value);
    }

    [Fact]
    public void ScriptEngine_ValidatesWholeScriptBeforeExecution()
    {
        var slave = new ModbusSlave("Device1", 1);
        slave.HoldingRegisters[0].Value = 10;
        var engine = new TagScriptEngine();

        Assert.Throws<TagScriptException>(() =>
            engine.Execute("Device1.HoldingRegister_0 := 20; Device1.Missing := 5;", new[] { slave }));

        Assert.Equal((ushort)10, slave.HoldingRegisters[0].Value);
    }

    [Fact]
    public void ScriptEngine_ValidatesSyntaxWithoutChangingTags()
    {
        var slave = new ModbusSlave("Device1", 1);
        var engine = new TagScriptEngine();

        Assert.Throws<TagScriptException>(() => engine.Validate("Device1.HoldingRegister_0 := 5", new[] { slave }));

        Assert.Equal((ushort)0, slave.HoldingRegisters[0].Value);
    }

    [Fact]
    public void Process_WriteAccessEventReportsOldAndNewTagValues()
    {
        var slave = new ModbusSlave("Device1", 1);
        slave.HoldingRegisters[0].Name = "Command";
        ushort? previousValue = null;
        ushort? currentValue = null;
        TagAccessKind? accessKind = null;
        _processor.TagAccessed += (_, point, access, previous, value) =>
        {
            if (point.Name != "Command")
                return;
            accessKind = access;
            previousValue = previous;
            currentValue = value;
        };

        var response = Process(1, 6, 0, 0, 0, 1, slave);

        Assert.NotNull(response);
        Assert.Equal(TagAccessKind.Write, accessKind);
        Assert.Equal((ushort)0, previousValue);
        Assert.Equal((ushort)1, currentValue);
    }

    [Fact]
    public void ScriptEngine_CanAssignBooleanToBitTag()
    {
        var slave = new ModbusSlave("Device1", 1);
        slave.Coils[0].Name = "Running";

        new TagScriptEngine().Execute("Device1.Running := TRUE;", new[] { slave });

        Assert.Equal((ushort)1, slave.Coils[0].Value);
    }

    [Fact]
    public void ScriptEngine_CanReadAndWriteNamedRegisterBits()
    {
        var slave = new ModbusSlave("Device1", 1);
        var register = slave.HoldingRegisters[0];
        register.Bits[5].Name = "PumpRunning";

        new TagScriptEngine().Execute("""
            VAR
                running : BOOL;
            END_VAR
            running := Device1.HoldingRegister_0.PumpRunning;
            running := TRUE;
            Device1.HoldingRegister_0.PumpRunning := running;
            """, new[] { slave });

        Assert.True(register.Bits[5].IsSet);
        Assert.Equal((ushort)32, register.Value);
    }

    [Fact]
    public void ScriptEngine_SupportsCaseForLoopAndLinearConversionFunctions()
    {
        var slave = new ModbusSlave("Device1", 1);
        slave.HoldingRegisters[0].Name = "Raw";
        slave.HoldingRegisters[1].Name = "Flags";
        slave.HoldingRegisters[1].Bits[2].Name = "InRange";
        var script = new ServerScript
        {
            Code = """
                VAR
                    raw : UINT;
                    index : INT;
                    normalized : REAL;
                    engineering : REAL;
                END_VAR
                raw := 2048;
                FOR index := 1 TO 3 DO
                    raw := raw + 1;
                END_FOR;
                normalized := NORM_X(0.0, raw, 4095.0);
                engineering := SCALE_X(0.0, normalized, 100.0);
                engineering := LIN_SCALE(raw, 0.0, 4095.0, 0.0, 100.0);
                CASE index OF
                    3: Device1.Flags.InRange := engineering >= 50.0;
                    ELSE Device1.Flags.InRange := FALSE;
                END_CASE;
                Device1.Raw := raw;
                """
        };

        new TagScriptEngine().Execute(script, new[] { slave });

        Assert.Equal((ushort)2051, slave.HoldingRegisters[0].Value);
        Assert.True(slave.HoldingRegisters[1].Bits[2].IsSet);
    }

    [Fact]
    public void ScriptEngine_TonTofAndTpRetainStateBetweenExecutions()
    {
        var clock = new FakeTimeProvider();
        var slave = new ModbusSlave("Device1", 1);
        slave.Coils[0].Name = "Input";
        slave.Coils[0].Value = 1;
        slave.Coils[1].Name = "TonOut";
        slave.Coils[2].Name = "TofOut";
        slave.Coils[3].Name = "TpOut";
        var script = new ServerScript
        {
            Code = """
                VAR
                    input : BOOL;
                    onDelay : TON;
                    offDelay : TOF;
                    pulse : TP;
                END_VAR
                input := Device1.Input;
                onDelay(IN := input, PT := T#100ms);
                offDelay(IN := input, PT := T#100ms);
                pulse(IN := input, PT := T#100ms);
                Device1.TonOut := onDelay.Q;
                Device1.TofOut := offDelay.Q;
                Device1.TpOut := pulse.Q;
                """
        };
        var engine = new TagScriptEngine(clock);

        engine.Execute(script, new[] { slave });
        Assert.Equal((ushort)0, slave.Coils[1].Value);
        Assert.Equal((ushort)1, slave.Coils[2].Value);
        Assert.Equal((ushort)1, slave.Coils[3].Value);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        engine.Execute(script, new[] { slave });
        Assert.Equal((ushort)1, slave.Coils[1].Value);
        Assert.Equal((ushort)1, slave.Coils[2].Value);
        Assert.Equal((ushort)0, slave.Coils[3].Value);

        slave.Coils[0].Value = 0;
        engine.Execute(script, new[] { slave });

        Assert.Equal((ushort)0, slave.Coils[1].Value);
        Assert.Equal((ushort)1, slave.Coils[2].Value);
        Assert.Equal((ushort)0, slave.Coils[3].Value);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        engine.Execute(script, new[] { slave });
        Assert.Equal((ushort)0, slave.Coils[2].Value);
    }

    [Fact]
    public void ScriptEngine_InitializesTypedVariablesOnlyOncePerScript()
    {
        var slave = new ModbusSlave("Device1", 1);
        var script = new ServerScript
        {
            Code = """
                VAR
                    counter : UINT := 4;
                END_VAR
                counter := counter + 1;
                Device1.HoldingRegister_0 := counter;
                """
        };
        var engine = new TagScriptEngine();

        engine.Execute(script, new[] { slave });
        engine.Execute(script, new[] { slave });

        Assert.Equal((ushort)6, slave.HoldingRegisters[0].Value);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private byte[]? Process(byte unit, byte function, byte startHigh, byte startLow,
        byte quantityHigh, byte quantityLow, ModbusSlave slave) =>
        _processor.Process(ModbusRtuProcessor.AddCrc(new[]
        {
            unit, function, startHigh, startLow, quantityHigh, quantityLow
        }), new[] { slave });
}
