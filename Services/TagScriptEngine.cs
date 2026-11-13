using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Services;

public sealed class TagScriptException : Exception
{
    public TagScriptException(string message) : base(message)
    {
    }

}

public sealed class TagScriptEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, RuntimeState> _states = new();
    private readonly Dictionary<Guid, CompiledScript> _compiled = new();

    public TagScriptEngine(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public static void ValidateVirtualVariable(VirtualVariable variable) =>
        _ = Parser.ReadVirtualVariable(variable);

    public static object GetVirtualVariableValue(VirtualVariable variable) =>
        Parser.ReadVirtualVariable(variable);

    public void Validate(string source, IReadOnlyCollection<ModbusSlave> slaves)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(slaves);
        _ = new Parser(source, slaves).Compile();
    }

    public void Validate(
        ServerScript script, IReadOnlyCollection<ModbusSlave> slaves, ModbusSlave? thisSlave = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(slaves);
        _ = new Parser(script.Code, slaves, thisSlave).Compile();
    }

    public void Execute(string source, IReadOnlyCollection<ModbusSlave> slaves) =>
        Execute(GuidFromSource(source), source, slaves);

    public void Execute(ServerScript script, IReadOnlyCollection<ModbusSlave> slaves, ModbusSlave? thisSlave = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        Execute(script.Id, script.Code, slaves, thisSlave);
    }

    public void ClearState()
    {
        _states.Clear();
        _compiled.Clear();
    }

    private void Execute(
        Guid id, string source, IReadOnlyCollection<ModbusSlave> slaves, ModbusSlave? thisSlave = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(slaves);
        if (source.Length > 65536)
            throw new TagScriptException("Размер сценария не должен превышать 64 КБ.");

        if (!_compiled.TryGetValue(id, out var compiled) || compiled.Source != source)
        {
            compiled = new Parser(source, slaves, thisSlave).Compile();
            _compiled[id] = compiled;
            _states[id] = new RuntimeState();
        }

        if (!_states.TryGetValue(id, out var state))
            _states[id] = state = new RuntimeState();
        compiled.Execute(new ExecutionContext(state, _timeProvider));
    }

    private static Guid GuidFromSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class RuntimeState
    {
        public Dictionary<string, object> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Initialized { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ExecutionContext(RuntimeState state, TimeProvider timeProvider)
    {
        public RuntimeState State { get; } = state;
        public TimeProvider TimeProvider { get; } = timeProvider;
        public int Operations { get; set; }

        public void Step()
        {
            if (++Operations > 100_000)
                throw new TagScriptException("Сценарий превысил лимит 100000 операций за один запуск.");
        }
    }

    private sealed record VariableDeclaration(string Name, string Type, Expression? Initializer);

    private sealed record CompiledScript(
        string Source,
        IReadOnlyList<VariableDeclaration> Declarations,
        IReadOnlyList<Statement> Statements)
    {
        public void Execute(ExecutionContext context)
        {
            foreach (var declaration in Declarations)
            {
                if (!context.State.Variables.ContainsKey(declaration.Name))
                    context.State.Variables[declaration.Name] = Parser.DefaultValue(declaration.Type);
                if (context.State.Initialized.Add(declaration.Name) && declaration.Initializer is not null)
                    context.State.Variables[declaration.Name] =
                        Parser.ConvertToType(declaration.Initializer(context), declaration.Type, declaration.Name);
            }
            foreach (var statement in Statements)
                statement(context);
        }
    }

    private sealed class TimerBlock(TimerKind kind)
    {
        private bool _previousInput;
        private bool _output;
        private long _startedAt;
        private TimeSpan _elapsed;
        private bool _initialized;

        public TimerKind Kind { get; } = kind;
        public bool Q => _output;
        public TimeSpan ET => _elapsed;

        public void Update(ExecutionContext context, bool input, TimeSpan preset)
        {
            if (preset < TimeSpan.Zero)
                throw new TagScriptException("Время PT таймера не может быть отрицательным.");

            var now = context.TimeProvider.GetTimestamp();
            var elapsed = _initialized
                ? context.TimeProvider.GetElapsedTime(_startedAt, now)
                : TimeSpan.Zero;

            switch (Kind)
            {
                case TimerKind.Ton:
                    if (!input)
                    {
                        _output = false;
                        _elapsed = TimeSpan.Zero;
                        _initialized = false;
                    }
                    else
                    {
                        if (!_previousInput)
                        {
                            _startedAt = now;
                            _initialized = true;
                            elapsed = TimeSpan.Zero;
                        }
                        _elapsed = elapsed > preset ? preset : elapsed;
                        _output = elapsed >= preset;
                    }
                    break;
                case TimerKind.Tof:
                    if (input)
                    {
                        _output = true;
                        _elapsed = TimeSpan.Zero;
                        _initialized = false;
                    }
                    else if (_previousInput)
                    {
                        _startedAt = now;
                        _initialized = true;
                        _elapsed = TimeSpan.Zero;
                        _output = preset > TimeSpan.Zero;
                    }
                    else if (_output && _initialized)
                    {
                        _elapsed = elapsed > preset ? preset : elapsed;
                        _output = elapsed < preset;
                        if (!_output)
                            _initialized = false;
                    }
                    break;
                case TimerKind.Tp:
                    if (input && !_previousInput && !_output)
                    {
                        _startedAt = now;
                        _initialized = true;
                        _elapsed = TimeSpan.Zero;
                        _output = preset > TimeSpan.Zero;
                    }
                    else if (_output && _initialized)
                    {
                        _elapsed = elapsed > preset ? preset : elapsed;
                        _output = elapsed < preset;
                        if (!_output)
                            _initialized = false;
                    }
                    break;
            }

            _previousInput = input;
        }
    }

    private enum TimerKind { Ton, Tof, Tp }

    private enum TokenKind
    {
        End, Identifier, String, Number, Time, Assign, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        Plus, Minus, Star, Slash, Mod, And, Or, Xor, Not, Ampersand, Dot, Comma, Colon,
        LeftParen, RightParen, Semicolon, If, Then, Elsif, Else, EndIf, Var, EndVar, Case, Of, EndCase,
        For, To, By, Do, EndFor, While, EndWhile, Repeat, Until, EndRepeat, True, False
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
    private delegate object Expression(ExecutionContext context);
    private delegate void Statement(ExecutionContext context);

    private sealed class Parser
    {
        private const int MaxNesting = 64;
        private const int MaxLoopIterations = 10_000;
        private readonly string _source;
        private readonly IReadOnlyCollection<ModbusSlave> _slaves;
        private readonly ModbusSlave? _thisSlave;
        private readonly List<Token> _tokens;
        private readonly List<VariableDeclaration> _declarations = new();
        private readonly Dictionary<string, string> _variableTypes = new(StringComparer.OrdinalIgnoreCase);
        private int _position;
        private int _nesting;

        public Parser(string source, IReadOnlyCollection<ModbusSlave> slaves, ModbusSlave? thisSlave = null)
        {
            _source = source;
            _slaves = slaves;
            _thisSlave = thisSlave;
            if (source.Length > 65536)
                throw new TagScriptException("Размер сценария не должен превышать 64 КБ.");
            _tokens = Tokenize(source);
        }

        public CompiledScript Compile()
        {
            while (Match(TokenKind.Var))
                ParseVarBlock();
            var statements = ParseStatementsUntil(TokenKind.End);
            Consume(TokenKind.End, "Неожиданный конец ST-сценария.");
            return new CompiledScript(_source, _declarations, statements);
        }

        private void ParseVarBlock()
        {
            while (!Check(TokenKind.EndVar) && !Check(TokenKind.End))
            {
                var name = Consume(TokenKind.Identifier, "В VAR ожидается имя переменной.");
                Consume(TokenKind.Colon, "После имени переменной ожидается ':'.");
                var type = Consume(TokenKind.Identifier, "После ':' ожидается тип переменной.").Text.ToUpperInvariant();
                if (!IsSupportedType(type))
                    throw Error(Previous(), $"Тип '{type}' не поддерживается.");
                if (_variableTypes.ContainsKey(name.Text))
                    throw Error(name, $"Переменная '{name.Text}' уже объявлена.");
                _variableTypes.Add(name.Text, type);
                Expression? initializer = null;
                if (Match(TokenKind.Assign))
                    initializer = ParseExpression();
                _declarations.Add(new VariableDeclaration(name.Text, type, initializer));
                Consume(TokenKind.Semicolon, "После объявления переменной ожидается ';'.");
            }
            Consume(TokenKind.EndVar, "Ожидается END_VAR.");
            Match(TokenKind.Semicolon);
        }

        private List<Statement> ParseStatementsUntil(params TokenKind[] terminators)
        {
            var statements = new List<Statement>();
            while (!terminators.Contains(Current().Kind) && !Check(TokenKind.End))
                statements.Add(ParseStatement());
            return statements;
        }

        private Statement ParseStatement()
        {
            if (Match(TokenKind.If)) return ParseIf();
            if (Match(TokenKind.Case)) return ParseCase();
            if (Match(TokenKind.For)) return ParseFor();
            if (Match(TokenKind.While)) return ParseWhile();
            if (Match(TokenKind.Repeat)) return ParseRepeat();

            if (Match(TokenKind.String))
            {
                var stringTagReference = ParseTagReference(Previous());
                Consume(TokenKind.Assign, "Ожидается ':='.");
                var value = ParseExpression();
                Consume(TokenKind.Semicolon, "После присваивания ожидается ';'.");
                return context =>
                {
                    context.Step();
                    WriteTag(stringTagReference, value(context));
                };
            }

            var name = Consume(TokenKind.Identifier, "Ожидается ST-оператор.");
            if (Check(TokenKind.LeftParen))
                return ParseTimerCall(name);
            if (_variableTypes.TryGetValue(name.Text, out var variableType) && Check(TokenKind.Dot))
                return ParseTimerPropertyAssignment(name, variableType);
            if (Match(TokenKind.Assign))
            {
                if (!_variableTypes.ContainsKey(name.Text))
                    throw Error(name, $"Переменная '{name.Text}' не объявлена в VAR.");
                var value = ParseExpression();
                Consume(TokenKind.Semicolon, "После присваивания ожидается ';'.");
                return context =>
                {
                    context.Step();
                    context.State.Variables[name.Text] = ConvertToType(value(context), _variableTypes[name.Text], name.Text);
                };
            }

            var reference = ParseTagReference(name);
            Consume(TokenKind.Assign, "Ожидается ':='.");
            var expression = ParseExpression();
            Consume(TokenKind.Semicolon, "После присваивания ожидается ';'.");
            return context =>
            {
                context.Step();
                WriteTag(reference, expression(context));
            };
        }

        private Statement ParseTimerPropertyAssignment(Token name, string variableType)
        {
            Consume(TokenKind.Dot, "Ожидается точка перед выходом таймера.");
            var property = Consume(TokenKind.Identifier, "Ожидается имя выхода таймера.").Text;
            if (property.Equals("Q", StringComparison.OrdinalIgnoreCase) ||
                property.Equals("ET", StringComparison.OrdinalIgnoreCase))
                throw Error(name, $"Выход таймера {property} доступен только для чтения.");
            throw Error(name, $"Неизвестный выход таймера '{property}' типа {variableType}.");
        }

        private Statement ParseTimerCall(Token name)
        {
            if (!_variableTypes.TryGetValue(name.Text, out var variableType) ||
                !TryGetTimerKind(variableType, out var timerKind))
                throw Error(name, "Вызов блока таймера должен использовать переменную типа TON, TOF или TP.");

            Consume(TokenKind.LeftParen, "Ожидается '('.");
            Expression? input = null;
            Expression? preset = null;
            while (!Check(TokenKind.RightParen))
            {
                var argument = Consume(TokenKind.Identifier, "Ожидается имя параметра IN или PT.");
                Consume(TokenKind.Assign, "В параметрах блока таймера ожидается ':='.");
                var value = ParseExpression();
                if (argument.Text.Equals("IN", StringComparison.OrdinalIgnoreCase))
                    input = value;
                else if (argument.Text.Equals("PT", StringComparison.OrdinalIgnoreCase))
                    preset = value;
                else
                    throw Error(argument, $"Неизвестный параметр таймера '{argument.Text}'.");
                if (!Match(TokenKind.Comma))
                    break;
            }
            Consume(TokenKind.RightParen, "Ожидается ')' после параметров таймера.");
            Consume(TokenKind.Semicolon, "После вызова таймера ожидается ';'.");
            if (input is null || preset is null)
                throw Error(name, "Блок таймера требует параметры IN и PT.");

            return context =>
            {
                context.Step();
                if (!context.State.Variables.TryGetValue(name.Text, out var value) || value is not TimerBlock timer)
                    context.State.Variables[name.Text] = timer = new TimerBlock(timerKind);
                timer.Update(context, ToBoolean(input(context)), ToTime(preset(context)));
            };
        }

        private Statement ParseIf()
        {
            EnterNesting();
            var branches = new List<(Expression Condition, List<Statement> Body)>();
            var condition = ParseExpression();
            Consume(TokenKind.Then, "После IF-условия ожидается THEN.");
            branches.Add((condition, ParseStatementsUntil(TokenKind.Elsif, TokenKind.Else, TokenKind.EndIf)));
            while (Match(TokenKind.Elsif))
            {
                var elsifCondition = ParseExpression();
                Consume(TokenKind.Then, "После ELSIF ожидается THEN.");
                branches.Add((elsifCondition, ParseStatementsUntil(TokenKind.Elsif, TokenKind.Else, TokenKind.EndIf)));
            }
            List<Statement>? elseBody = null;
            if (Match(TokenKind.Else))
                elseBody = ParseStatementsUntil(TokenKind.EndIf);
            Consume(TokenKind.EndIf, "Ожидается END_IF.");
            Match(TokenKind.Semicolon);
            LeaveNesting();
            return context =>
            {
                context.Step();
                foreach (var branch in branches)
                {
                    if (!ToBoolean(branch.Condition(context)))
                        continue;
                    ExecuteStatements(context, branch.Body);
                    return;
                }
                if (elseBody is not null)
                    ExecuteStatements(context, elseBody);
            };
        }

        private Statement ParseCase()
        {
            EnterNesting();
            var value = ParseExpression();
            Consume(TokenKind.Of, "После выражения CASE ожидается OF.");
            var branches = new List<(object[] Labels, List<Statement> Body)>();
            while (!Check(TokenKind.Else) && !Check(TokenKind.EndCase) && !Check(TokenKind.End))
            {
                var labels = new List<object> { ParseCaseLabel() };
                while (Match(TokenKind.Comma))
                    labels.Add(ParseCaseLabel());
                Consume(TokenKind.Colon, "После меток CASE ожидается ':'.");
                branches.Add((labels.ToArray(), ParseStatementsUntil(TokenKind.Else, TokenKind.EndCase)));
            }
            List<Statement>? elseBody = null;
            if (Match(TokenKind.Else))
                elseBody = ParseStatementsUntil(TokenKind.EndCase);
            Consume(TokenKind.EndCase, "Ожидается END_CASE.");
            Match(TokenKind.Semicolon);
            LeaveNesting();
            return context =>
            {
                context.Step();
                var selected = value(context);
                foreach (var branch in branches)
                {
                    if (!branch.Labels.Any(label => ValuesEqual(label, selected)))
                        continue;
                    ExecuteStatements(context, branch.Body);
                    return;
                }
                if (elseBody is not null)
                    ExecuteStatements(context, elseBody);
            };
        }

        private object ParseCaseLabel()
        {
            if (Match(TokenKind.Number))
                return ParseNumber(Previous());
            if (Match(TokenKind.True)) return true;
            if (Match(TokenKind.False)) return false;
            throw Error(Current(), "Метка CASE должна быть числом или BOOL.");
        }

        private Statement ParseFor()
        {
            EnterNesting();
            var variable = Consume(TokenKind.Identifier, "После FOR ожидается переменная.");
            if (!_variableTypes.TryGetValue(variable.Text, out var type) || !IsIntegerType(type))
                throw Error(variable, "Переменная FOR должна быть объявлена целочисленным типом в VAR.");
            Consume(TokenKind.Assign, "После переменной FOR ожидается ':='.");
            var from = ParseExpression();
            Consume(TokenKind.To, "В цикле FOR ожидается TO.");
            var to = ParseExpression();
            Expression step = _ => 1m;
            if (Match(TokenKind.By))
                step = ParseExpression();
            Consume(TokenKind.Do, "После параметров FOR ожидается DO.");
            var body = ParseStatementsUntil(TokenKind.EndFor);
            Consume(TokenKind.EndFor, "Ожидается END_FOR.");
            Match(TokenKind.Semicolon);
            LeaveNesting();
            return context =>
            {
                var current = ToNumber(from(context));
                var last = ToNumber(to(context));
                var increment = ToNumber(step(context));
                if (increment == 0)
                    throw new TagScriptException("Шаг FOR не может быть равен нулю.");
                var iteration = 0;
                while ((increment > 0 ? current <= last : current >= last) && iteration < MaxLoopIterations)
                {
                    context.Step();
                    context.State.Variables[variable.Text] = ConvertToType(current, type, variable.Text);
                    ExecuteStatements(context, body);
                    current += increment;
                    iteration++;
                }
                if (increment > 0 ? current <= last : current >= last)
                    throw new TagScriptException($"Цикл FOR превысил лимит {MaxLoopIterations} итераций.");
            };
        }

        private Statement ParseWhile()
        {
            EnterNesting();
            var condition = ParseExpression();
            Consume(TokenKind.Do, "После WHILE ожидается DO.");
            var body = ParseStatementsUntil(TokenKind.EndWhile);
            Consume(TokenKind.EndWhile, "Ожидается END_WHILE.");
            Match(TokenKind.Semicolon);
            LeaveNesting();
            return context =>
            {
                var iteration = 0;
                while (ToBoolean(condition(context)) && iteration < MaxLoopIterations)
                {
                    context.Step();
                    ExecuteStatements(context, body);
                    iteration++;
                }
                if (iteration == MaxLoopIterations && ToBoolean(condition(context)))
                    throw new TagScriptException($"Цикл WHILE превысил лимит {MaxLoopIterations} итераций.");
            };
        }

        private Statement ParseRepeat()
        {
            EnterNesting();
            var body = ParseStatementsUntil(TokenKind.Until);
            Consume(TokenKind.Until, "Ожидается UNTIL в цикле REPEAT.");
            var condition = ParseExpression();
            Consume(TokenKind.EndRepeat, "Ожидается END_REPEAT.");
            Match(TokenKind.Semicolon);
            LeaveNesting();
            return context =>
            {
                var iteration = 0;
                do
                {
                    context.Step();
                    ExecuteStatements(context, body);
                    iteration++;
                } while (!ToBoolean(condition(context)) && iteration < MaxLoopIterations);
                if (iteration == MaxLoopIterations && !ToBoolean(condition(context)))
                    throw new TagScriptException($"Цикл REPEAT превысил лимит {MaxLoopIterations} итераций.");
            };
        }

        private Expression ParseExpression() => ParseOr();

        private Expression ParseOr()
        {
            var left = ParseXor();
            while (Match(TokenKind.Or))
            {
                var right = ParseXor();
                var previous = left;
                left = context => ToBoolean(previous(context)) || ToBoolean(right(context));
            }
            return left;
        }

        private Expression ParseXor()
        {
            var left = ParseAnd();
            while (Match(TokenKind.Xor))
            {
                var right = ParseAnd();
                var previous = left;
                left = context => ToBoolean(previous(context)) ^ ToBoolean(right(context));
            }
            return left;
        }

        private Expression ParseAnd()
        {
            var left = ParseComparison();
            while (Match(TokenKind.And))
            {
                var right = ParseComparison();
                var previous = left;
                left = context => ToBoolean(previous(context)) && ToBoolean(right(context));
            }
            return left;
        }

        private Expression ParseComparison()
        {
            var left = ParseAdditive();
            while (Match(TokenKind.Equal, TokenKind.NotEqual, TokenKind.Less, TokenKind.LessEqual,
                         TokenKind.Greater, TokenKind.GreaterEqual))
            {
                var operation = Previous().Kind;
                var right = ParseAdditive();
                var previous = left;
                left = context =>
                {
                    var first = previous(context);
                    var second = right(context);
                    return operation switch
                    {
                        TokenKind.Equal => ValuesEqual(first, second),
                        TokenKind.NotEqual => !ValuesEqual(first, second),
                        TokenKind.Less => CompareValues(first, second) < 0,
                        TokenKind.LessEqual => CompareValues(first, second) <= 0,
                        TokenKind.Greater => CompareValues(first, second) > 0,
                        _ => CompareValues(first, second) >= 0
                    };
                };
            }
            return left;
        }

        private Expression ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Match(TokenKind.Plus, TokenKind.Minus, TokenKind.Ampersand))
            {
                var operation = Previous().Kind;
                var right = ParseMultiplicative();
                var previous = left;
                left = context => operation == TokenKind.Ampersand
                    ? Convert.ToString(previous(context), CultureInfo.InvariantCulture) +
                      Convert.ToString(right(context), CultureInfo.InvariantCulture)
                    : operation == TokenKind.Plus
                        ? ToNumber(previous(context)) + ToNumber(right(context))
                        : ToNumber(previous(context)) - ToNumber(right(context));
            }
            return left;
        }

        private Expression ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Match(TokenKind.Star, TokenKind.Slash, TokenKind.Mod))
            {
                var operation = Previous().Kind;
                var right = ParseUnary();
                var previous = left;
                left = context =>
                {
                    var first = ToNumber(previous(context));
                    var second = ToNumber(right(context));
                    if (second == 0)
                        throw new TagScriptException("Деление на ноль.");
                    return operation switch
                    {
                        TokenKind.Star => first * second,
                        TokenKind.Slash => first / second,
                        _ => first % second
                    };
                };
            }
            return left;
        }

        private Expression ParseUnary()
        {
            if (++_nesting > MaxNesting)
                throw Error(Current(), $"Вложенность выражений не должна превышать {MaxNesting}.");
            try
            {
                if (Match(TokenKind.Not))
                {
                    var operand = ParseUnary();
                    return context => !ToBoolean(operand(context));
                }
                if (Match(TokenKind.Minus))
                {
                    var operand = ParseUnary();
                    return context => -ToNumber(operand(context));
                }
                if (Match(TokenKind.Plus))
                    return ParseUnary();
                return ParsePrimary();
            }
            finally
            {
                _nesting--;
            }
        }

        private Expression ParsePrimary()
        {
            if (Match(TokenKind.Number))
            {
                var value = ParseNumber(Previous());
                return _ => value;
            }
            if (Match(TokenKind.Time))
            {
                var value = ParseTimeLiteral(Previous());
                return _ => value;
            }
            if (Match(TokenKind.True)) return _ => true;
            if (Match(TokenKind.False)) return _ => false;
            if (Match(TokenKind.LeftParen))
            {
                var expression = ParseExpression();
                Consume(TokenKind.RightParen, "Ожидается ')'.");
                return expression;
            }
            if (Check(TokenKind.Identifier) && CheckNext(TokenKind.LeftParen))
                return ParseFunctionCall();
            if (Check(TokenKind.String) ||
                Check(TokenKind.Identifier) && CheckNext(TokenKind.Dot) && !_variableTypes.ContainsKey(Current().Text))
            {
                var first = Match(TokenKind.String) ? Previous() : Consume(TokenKind.Identifier, "");
                var reference = ParseTagReference(first);
                return _ => ReadTag(reference);
            }
            if (Match(TokenKind.Identifier))
            {
                var name = Previous();
                if (!_variableTypes.TryGetValue(name.Text, out var type))
                    throw Error(name, $"Переменная '{name.Text}' не объявлена в VAR.");
                if (Match(TokenKind.Dot))
                {
                    var property = Consume(TokenKind.Identifier, "После точки ожидается выход таймера.").Text;
                    return context => ReadTimerProperty(context, name.Text, type, property, name);
                }
                return context => context.State.Variables.TryGetValue(name.Text, out var value)
                    ? value
                    : DefaultValue(type);
            }
            throw Error(Current(), "Ожидается число, переменная, тег или функция.");
        }

        private Expression ParseFunctionCall()
        {
            var function = Consume(TokenKind.Identifier, "Ожидается функция.").Text.ToUpperInvariant();
            Consume(TokenKind.LeftParen, "Ожидается '('.");
            var arguments = new List<Expression>();
            while (!Check(TokenKind.RightParen))
            {
                arguments.Add(ParseExpression());
                if (!Match(TokenKind.Comma))
                    break;
            }
            Consume(TokenKind.RightParen, "Ожидается ')' после аргументов.");
            ValidateFunction(function, arguments.Count, Previous());
            return context => EvaluateFunction(function, arguments.Select(argument => argument(context)).ToArray());
        }

        private TagReference ParseTagReference(Token first)
        {
            var namespaceName = first.Text;
            Consume(TokenKind.Dot, "В ссылке на тег ожидается '.'.");
            var second = ConsumeName("После имени устройства ожидается имя тега.");
            ModbusSlave slave;
            Token tag;
            ModbusSlave portSlave = null!;
            if (namespaceName.Equals("THIS", StringComparison.OrdinalIgnoreCase))
            {
                slave = _thisSlave
                    ?? throw Error(first, "'THIS' доступен только во внутреннем сценарии устройства.");
                tag = second;
            }
            else if (Check(TokenKind.Dot) && IsNextName() &&
                     TryFindPortSlave(namespaceName, second.Text, first, out portSlave) &&
                     FindTagOrVariable(portSlave, _tokens[_position + 1].Text))
            {
                Consume(TokenKind.Dot, "В ссылке на тег ожидается '.'.");
                slave = portSlave;
                tag = ConsumeName("После имени устройства ожидается имя тега.");
            }
            else
            {
                slave = FindSlave(namespaceName, first);
                tag = second;
            }

            string? bit = null;
            if (Match(TokenKind.Dot))
                bit = ConsumeName("После имени регистра ожидается имя бита.").Text;
            var point = FindPoint(slave, tag.Text);
            var variable = FindVirtualVariable(slave, tag.Text);
            if (point is not null && variable is not null)
                throw Error(tag, $"Имя '{tag.Text}' одновременно используется тегом и виртуальной переменной.");
            if (variable is not null)
            {
                if (bit is not null)
                    throw Error(tag, "К виртуальной переменной нельзя обращаться как к биту регистра.");
                return new TagReference(slave, null, variable.Name, null, null);
            }
            if (point is null)
                throw Error(tag, $"Тег или виртуальная переменная '{namespaceName}.{tag.Text}' не найдены.");
            if (bit is not null && FindBit(point, bit) is null)
                throw Error(tag, $"Бит '{namespaceName}.{tag.Text}.{bit}' не найден.");

            var area = Enum.GetValues<RegisterArea>()
                .First(candidate => slave.GetPoints(candidate).Contains(point));
            return new TagReference(slave, point, null, bit, area);
        }

        private bool TryFindPortSlave(string alias, string deviceName, Token token, out ModbusSlave slave)
        {
            var matches = _slaves.Where(item =>
                    string.Equals(item.PortAlias, alias, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length > 1)
                throw Error(token, $"Устройство '{alias}.{deviceName}' неоднозначно.");
            slave = matches.FirstOrDefault()!;
            return matches.Length == 1;
        }

        private bool IsNextName() =>
            _position + 1 < _tokens.Count &&
            _tokens[_position + 1].Kind is TokenKind.Identifier or TokenKind.String;

        private Token ConsumeName(string message)
        {
            if (Match(TokenKind.Identifier, TokenKind.String))
                return Previous();
            throw Error(Current(), message);
        }

        private static object ReadTag(TagReference reference)
        {
            if (reference.VirtualVariableName is { } variableName)
            {
                var variable = FindVirtualVariable(reference.Slave, variableName)
                    ?? throw new TagScriptException(
                        $"Виртуальная переменная '{reference.Slave.Name}.{variableName}' не найдена.");
                return ReadVirtualVariable(variable);
            }

            var point = reference.Point!;
            if (reference.Bit is not null)
                return FindBit(point, reference.Bit)!.IsSet;
            if (point.ValueType != RegisterValueType.Float32)
                return (decimal)point.Value;
            if (!reference.Slave.TryReadFloat(reference.Area!.Value, point.Address, out var value))
                throw new TagScriptException(
                    $"Тег Float32 '{reference.Slave.Name}.{point.Name}' требует двух последовательных регистров.");
            return FloatToDecimal(value);
        }

        private static void WriteTag(TagReference reference, object value)
        {
            if (reference.VirtualVariableName is { } variableName)
            {
                var variable = FindVirtualVariable(reference.Slave, variableName)
                    ?? throw new TagScriptException(
                        $"Виртуальная переменная '{reference.Slave.Name}.{variableName}' не найдена.");
                var convertedValue = ConvertToType(
                    value, variable.Type.ToString().ToUpperInvariant(), variable.Name);
                variable.ValueText = FormatVirtualVariableValue(convertedValue);
                return;
            }

            var point = reference.Point!;
            if (reference.Bit is not null)
            {
                FindBit(point, reference.Bit)!.IsSet = ToBoolean(value);
                return;
            }
            var number = ToNumber(value);
            if (point.ValueType == RegisterValueType.Float32)
            {
                if (!reference.Slave.TryWriteFloat(reference.Area!.Value, point.Address, (float)number))
                    throw new TagScriptException(
                        $"Тег Float32 '{reference.Slave.Name}.{point.Name}' требует двух последовательных регистров.");
                return;
            }
            if (number < 0 || number > ushort.MaxValue || decimal.Truncate(number) != number)
                throw new TagScriptException($"Значение регистра '{point.Name}' должно быть целым числом от 0 до 65535.");
            if (reference.Slave.Coils.Contains(point) || reference.Slave.DiscreteInputs.Contains(point))
            {
                if (number is not (0 or 1))
                    throw new TagScriptException($"Тег '{reference.Slave.Name}.{point.Name}' принимает только 0 или 1.");
            }
            point.Value = (ushort)number;
        }

        public static object ReadVirtualVariable(VirtualVariable variable)
        {
            var type = variable.Type.ToString().ToUpperInvariant();
            object rawValue;
            if (type == "BOOL")
            {
                if (!bool.TryParse(variable.ValueText, out var boolean))
                    throw new TagScriptException(
                        $"Виртуальная переменная '{variable.Name}' должна содержать TRUE или FALSE.");
                rawValue = boolean;
            }
            else if (!decimal.TryParse(
                         variable.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw new TagScriptException(
                    $"Виртуальная переменная '{variable.Name}' содержит некорректное значение.");
            }
            else
            {
                rawValue = number;
            }

            return Parser.ConvertToType(rawValue, type, variable.Name);
        }

        private static string FormatVirtualVariableValue(object value) => value switch
        {
            bool boolean => boolean ? "TRUE" : "FALSE",
            TimeSpan time => ((decimal)time.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new TagScriptException("Неподдерживаемое значение виртуальной переменной.")
        };

        private object ReadTimerProperty(ExecutionContext context, string name, string type, string property, Token token)
        {
            if (!TryGetTimerKind(type, out _))
                throw Error(token, $"Переменная '{name}' типа {type} не является таймером.");
            if (!context.State.Variables.TryGetValue(name, out var value) || value is not TimerBlock timer)
                return property.Equals("Q", StringComparison.OrdinalIgnoreCase) ? false : TimeSpan.Zero;
            return property.ToUpperInvariant() switch
            {
                "Q" => timer.Q,
                "ET" => timer.ET,
                _ => throw Error(token, $"У таймера {name} нет выхода '{property}'.")
            };
        }

        private static void ValidateFunction(string name, int count, Token token)
        {
            var allowed = name switch
            {
                "ABS" or "SQRT" or "ROUND" or "TRUNC" or "BYTE" or "WORD" or "DWORD" or "SINT" or "USINT" or
                "INT" or "UINT" or "DINT" or "UDINT" or "LINT" or "ULINT" or "REAL" or "LREAL" or "BOOL" or
                "TIME" => new[] { 1 },
                "MIN" or "MAX" or "NORM_X" or "SCALE_X" => new[] { 3 },
                "LIMIT" or "LIN_SCALE" => new[] { 5 },
                _ => throw Error(token, $"Функция '{name}' не поддерживается.")
            };
            if (!allowed.Contains(count))
                throw Error(token, $"Функция {name} принимает {string.Join(" или ", allowed)} аргумента(ов).");
        }

        private static object EvaluateFunction(string name, object[] args)
        {
            if (name == "BOOL")
                return ToBoolean(args[0]);
            if (name == "TIME")
                return ToTime(args[0]);
            if (IsSupportedType(name) && name is not "TON" and not "TOF" and not "TP")
                return ConvertToType(args[0], name, name);
            var values = args.Select(ToNumber).ToArray();
            return name switch
            {
                "ABS" => Math.Abs(values[0]),
                "SQRT" => values[0] < 0 ? throw new TagScriptException("SQRT не принимает отрицательное значение.") : (decimal)Math.Sqrt((double)values[0]),
                "ROUND" => decimal.Round(values[0], MidpointRounding.AwayFromZero),
                "TRUNC" => decimal.Truncate(values[0]),
                "MIN" => Math.Min(values[0], Math.Min(values[1], values[2])),
                "MAX" => Math.Max(values[0], Math.Max(values[1], values[2])),
                "LIMIT" => Math.Min(values[2], Math.Max(values[0], values[1])),
                "NORM_X" => values[2] == values[0]
                    ? throw new TagScriptException("NORM_X: MIN и MAX не могут быть равны.")
                    : (values[1] - values[0]) / (values[2] - values[0]),
                "SCALE_X" => values[0] + values[1] * (values[2] - values[0]),
                "LIN_SCALE" => values[2] == values[1]
                    ? throw new TagScriptException("LIN_SCALE: входной диапазон не может быть нулевым.")
                    : values[3] + (values[0] - values[1]) * (values[4] - values[3]) / (values[2] - values[1]),
                _ => throw new TagScriptException($"Функция '{name}' не поддерживается.")
            };
        }

        private void EnterNesting()
        {
            if (++_nesting > MaxNesting)
                throw Error(Current(), $"Вложенность блоков не должна превышать {MaxNesting}.");
        }

        private void LeaveNesting() => _nesting--;

        private static void ExecuteStatements(ExecutionContext context, IEnumerable<Statement> statements)
        {
            foreach (var statement in statements)
                statement(context);
        }

        private ModbusSlave FindSlave(string name, Token token)
        {
            var matches = _slaves.Where(slave => string.Equals(slave.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length switch
            {
                0 => throw Error(token, $"Устройство '{name}' не найдено."),
                1 => matches[0],
                _ => throw Error(token, $"Имя устройства '{name}' неоднозначно.")
            };
        }

        private static MemoryPoint? FindPoint(ModbusSlave slave, string name)
        {
            var matches = Enum.GetValues<RegisterArea>().SelectMany(slave.GetPoints)
                .Where(point => string.Equals(point.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new TagScriptException($"Имя тега '{slave.Name}.{name}' неоднозначно.")
            };
        }

        private static bool FindTagOrVariable(ModbusSlave slave, string name) =>
            FindPoint(slave, name) is not null || FindVirtualVariable(slave, name) is not null;

        private static VirtualVariable? FindVirtualVariable(ModbusSlave slave, string name)
        {
            var matches = slave.VirtualVariables
                .Where(variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new TagScriptException($"Имя виртуальной переменной '{slave.Name}.{name}' неоднозначно.")
            };
        }

        private static RegisterBit? FindBit(MemoryPoint point, string name)
        {
            var matches = point.Bits.Where(bit => string.Equals(bit.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new TagScriptException($"Имя бита '{point.Name}.{name}' неоднозначно.")
            };
        }

        private static bool IsSupportedType(string type) =>
            type is "BOOL" or "BYTE" or "WORD" or "DWORD" or "SINT" or "USINT" or "INT" or "UINT" or
                "DINT" or "UDINT" or "LINT" or "ULINT" or "REAL" or "LREAL" or "TIME" or "TON" or "TOF" or "TP";

        private static bool IsIntegerType(string type) => type is "BYTE" or "WORD" or "DWORD" or "SINT" or "USINT" or
            "INT" or "UINT" or "DINT" or "UDINT" or "LINT" or "ULINT";

        private static bool TryGetTimerKind(string type, out TimerKind kind)
        {
            switch (type.ToUpperInvariant())
            {
                case "TON": kind = TimerKind.Ton; return true;
                case "TOF": kind = TimerKind.Tof; return true;
                case "TP": kind = TimerKind.Tp; return true;
                default: kind = default; return false;
            }
        }

        public static object DefaultValue(string type) => type switch
        {
            "BOOL" => false,
            "TIME" => TimeSpan.Zero,
            "TON" => new TimerBlock(TimerKind.Ton),
            "TOF" => new TimerBlock(TimerKind.Tof),
            "TP" => new TimerBlock(TimerKind.Tp),
            _ => 0m
        };

        public static object ConvertToType(object value, string type, string name)
        {
            if (type is "TON" or "TOF" or "TP")
                return value is TimerBlock timer && TryGetTimerKind(type, out var expected) && timer.Kind == expected
                    ? timer
                    : throw new TagScriptException($"Переменная '{name}' типа {type} должна использоваться как блок таймера.");
            if (type == "TIME")
                return ToTime(value);
            if (type == "BOOL")
                return ToBoolean(value);
            var number = ToNumber(value);
            return type switch
            {
                "BYTE" or "USINT" => ConvertInteger(number, 0, byte.MaxValue, type),
                "WORD" or "UINT" => ConvertInteger(number, 0, ushort.MaxValue, type),
                "DWORD" or "UDINT" => ConvertInteger(number, 0, uint.MaxValue, type),
                "SINT" => ConvertInteger(number, sbyte.MinValue, sbyte.MaxValue, type),
                "INT" => ConvertInteger(number, short.MinValue, short.MaxValue, type),
                "DINT" => ConvertInteger(number, int.MinValue, int.MaxValue, type),
                "LINT" => ConvertInteger(number, long.MinValue, long.MaxValue, type),
                "ULINT" => ConvertInteger(number, 0, (decimal)ulong.MaxValue, type),
                "REAL" => FloatToDecimal((float)number),
                "LREAL" => number,
                _ => throw new TagScriptException($"Тип '{type}' не поддерживается.")
            };
        }

        private static decimal FloatToDecimal(float value)
        {
            if (!float.IsFinite(value) ||
                !decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var result))
                throw new TagScriptException("Значение Float32 не может быть представлено числом ST.");
            return result;
        }

        private static decimal ConvertInteger(decimal value, decimal min, decimal max, string type)
        {
            var result = decimal.Truncate(value);
            if (result < min || result > max)
                throw new TagScriptException($"Значение выходит за диапазон типа {type} ({min}..{max}).");
            return result;
        }

        private static decimal ToNumber(object value) => value switch
        {
            decimal number => number,
            bool boolean => boolean ? 1m : 0m,
            _ => throw new TagScriptException("В выражении ожидалось числовое значение.")
        };

        private static bool ToBoolean(object value) => value switch
        {
            bool boolean => boolean,
            decimal number => number != 0,
            _ => throw new TagScriptException("В логическом выражении ожидалось BOOL.")
        };

        private static TimeSpan ToTime(object value) => value switch
        {
            TimeSpan time => time,
            decimal milliseconds when milliseconds >= 0 => TimeSpan.FromMilliseconds((double)milliseconds),
            _ => throw new TagScriptException("PT должен иметь тип TIME или неотрицательное число миллисекунд.")
        };

        private static bool ValuesEqual(object first, object second) =>
            first is bool firstBool && second is bool secondBool
                ? firstBool == secondBool
                : CompareValues(first, second) == 0;

        private static int CompareValues(object first, object second)
        {
            if (first is TimeSpan firstTime && second is TimeSpan secondTime)
                return firstTime.CompareTo(secondTime);
            return ToNumber(first).CompareTo(ToNumber(second));
        }

        private static decimal ParseNumber(Token token)
        {
            var text = token.Text.Replace("_", "", StringComparison.Ordinal);
            var radixSeparator = text.IndexOf('#');
            if (radixSeparator > 0 &&
                int.TryParse(text[..radixSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out var radix) &&
                radix is 2 or 8 or 10 or 16)
            {
                try
                {
                    return Convert.ToUInt64(text[(radixSeparator + 1)..], radix);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw Error(token, $"Некорректное число '{token.Text}'.");
                }
            }
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            throw Error(token, $"Некорректное число '{token.Text}'.");
        }

        private static TimeSpan ParseTimeLiteral(Token token)
        {
            var text = token.Text[(token.Text.IndexOf('#') + 1)..];
            var matches = Regex.Matches(text, @"(\d+(?:\.\d+)?)(ms|d|h|m|s)", RegexOptions.IgnoreCase);
            if (matches.Count == 0 || string.Concat(matches.Select(match => match.Value)).Length != text.Length)
                throw Error(token, $"Некорректный литерал времени '{token.Text}'. Используйте T#250ms, T#2s или T#1m30s.");
            double milliseconds = 0;
            foreach (Match match in matches)
            {
                var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                milliseconds += match.Groups[2].Value.ToLowerInvariant() switch
                {
                    "ms" => value,
                    "s" => value * 1000,
                    "m" => value * 60_000,
                    "h" => value * 3_600_000,
                    "d" => value * 86_400_000,
                    _ => 0
                };
            }
            if (!double.IsFinite(milliseconds) || milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
                throw Error(token, "Литерал времени выходит за допустимый диапазон.");
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            var line = 1;
            var column = 1;
            for (var index = 0; index < source.Length;)
            {
                var current = source[index];
                if (char.IsWhiteSpace(current))
                {
                    Advance(current, ref line, ref column);
                    index++;
                    continue;
                }
                if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                        Advance(source[index++], ref line, ref column);
                    continue;
                }
                if (current == '(' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    var startLine = line;
                    var startColumn = column;
                    Advance(source[index++], ref line, ref column);
                    Advance(source[index++], ref line, ref column);
                    while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == ')'))
                        Advance(source[index++], ref line, ref column);
                    if (index + 1 >= source.Length)
                        throw Error(new Token(TokenKind.End, "", startLine, startColumn), "Не закрыт комментарий (* ... *).");
                    Advance(source[index++], ref line, ref column);
                    Advance(source[index++], ref line, ref column);
                    continue;
                }

                var tokenLine = line;
                var tokenColumn = column;
                if (char.IsDigit(current))
                {
                    var radixEnd = index;
                    while (radixEnd < source.Length && char.IsDigit(source[radixEnd]))
                        radixEnd++;
                    if (radixEnd < source.Length && source[radixEnd] == '#')
                    {
                        var start = index;
                        while (index <= radixEnd)
                            Advance(source[index++], ref line, ref column);
                        while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                            Advance(source[index++], ref line, ref column);
                        tokens.Add(new Token(TokenKind.Number, source[start..index], tokenLine, tokenColumn));
                        continue;
                    }
                }
                if (char.IsDigit(current) || current == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1]))
                {
                    var start = index;
                    var hasDot = false;
                    while (index < source.Length)
                    {
                        var ch = source[index];
                        if (char.IsDigit(ch) || ch == '_')
                        {
                            Advance(ch, ref line, ref column);
                            index++;
                            continue;
                        }
                        if (ch == '.' && !hasDot && index + 1 < source.Length && char.IsDigit(source[index + 1]))
                        {
                            hasDot = true;
                            Advance(ch, ref line, ref column);
                            index++;
                            continue;
                        }
                        if (ch is 'e' or 'E')
                        {
                            Advance(ch, ref line, ref column);
                            index++;
                            if (index < source.Length && source[index] is '+' or '-')
                                Advance(source[index++], ref line, ref column);
                            continue;
                        }
                        break;
                    }
                    tokens.Add(new Token(TokenKind.Number, source[start..index], tokenLine, tokenColumn));
                    continue;
                }
                if (char.IsLetter(current) || current == '_')
                {
                    var start = index;
                    while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                        Advance(source[index++], ref line, ref column);
                    var text = source[start..index];
                    if ((text.Equals("T", StringComparison.OrdinalIgnoreCase) ||
                         text.Equals("TIME", StringComparison.OrdinalIgnoreCase)) &&
                        index < source.Length && source[index] == '#')
                    {
                        Advance(source[index++], ref line, ref column);
                        while (index < source.Length && !char.IsWhiteSpace(source[index]) &&
                               source[index] is not (',' or ')' or ';'))
                            Advance(source[index++], ref line, ref column);
                        tokens.Add(new Token(TokenKind.Time, source[start..index], tokenLine, tokenColumn));
                        continue;
                    }
                    tokens.Add(new Token(Keyword(text), text, tokenLine, tokenColumn));
                    continue;
                }
                if (current == '"')
                {
                    Advance(source[index++], ref line, ref column);
                    var value = new StringBuilder();
                    var closed = false;
                    while (index < source.Length)
                    {
                        current = source[index];
                        if (current == '"')
                        {
                            Advance(source[index++], ref line, ref column);
                            closed = true;
                            break;
                        }
                        if (current == '\\' && index + 1 < source.Length && source[index + 1] is '"' or '\\')
                        {
                            Advance(source[index++], ref line, ref column);
                            current = source[index];
                        }
                        value.Append(current);
                        Advance(source[index++], ref line, ref column);
                    }
                    if (!closed)
                        throw Error(new Token(TokenKind.String, "", tokenLine, tokenColumn), "Не закрыта строка в кавычках.");
                    tokens.Add(new Token(TokenKind.String, value.ToString(), tokenLine, tokenColumn));
                    continue;
                }

                var pair = index + 1 < source.Length ? source.Substring(index, 2) : "";
                var pairKind = pair switch
                {
                    ":=" => TokenKind.Assign,
                    "<>" => TokenKind.NotEqual,
                    "<=" => TokenKind.LessEqual,
                    ">=" => TokenKind.GreaterEqual,
                    _ => (TokenKind?)null
                };
                if (pairKind is { } kind)
                {
                    tokens.Add(new Token(kind, pair, tokenLine, tokenColumn));
                    Advance(source[index++], ref line, ref column);
                    Advance(source[index++], ref line, ref column);
                    continue;
                }
                var single = current switch
                {
                    '=' => TokenKind.Equal,
                    '<' => TokenKind.Less,
                    '>' => TokenKind.Greater,
                    '+' => TokenKind.Plus,
                    '-' => TokenKind.Minus,
                    '*' => TokenKind.Star,
                    '/' => TokenKind.Slash,
                    '&' => TokenKind.Ampersand,
                    '.' => TokenKind.Dot,
                    ',' => TokenKind.Comma,
                    ':' => TokenKind.Colon,
                    '(' => TokenKind.LeftParen,
                    ')' => TokenKind.RightParen,
                    ';' => TokenKind.Semicolon,
                    _ => throw Error(new Token(TokenKind.End, current.ToString(), tokenLine, tokenColumn),
                        $"Недопустимый символ '{current}'.")
                };
                tokens.Add(new Token(single, current.ToString(), tokenLine, tokenColumn));
                Advance(current, ref line, ref column);
                index++;
            }
            tokens.Add(new Token(TokenKind.End, "", line, column));
            return tokens;
        }

        private static TokenKind Keyword(string text) => text.ToUpperInvariant() switch
        {
            "VAR" => TokenKind.Var, "END_VAR" => TokenKind.EndVar,
            "IF" => TokenKind.If, "THEN" => TokenKind.Then, "ELSIF" => TokenKind.Elsif,
            "ELSE" => TokenKind.Else, "END_IF" => TokenKind.EndIf,
            "CASE" => TokenKind.Case, "OF" => TokenKind.Of, "END_CASE" => TokenKind.EndCase,
            "FOR" => TokenKind.For, "TO" => TokenKind.To, "BY" => TokenKind.By,
            "DO" => TokenKind.Do, "END_FOR" => TokenKind.EndFor,
            "WHILE" => TokenKind.While, "END_WHILE" => TokenKind.EndWhile,
            "REPEAT" => TokenKind.Repeat, "UNTIL" => TokenKind.Until, "END_REPEAT" => TokenKind.EndRepeat,
            "TRUE" => TokenKind.True, "FALSE" => TokenKind.False,
            "MOD" => TokenKind.Mod, "AND" => TokenKind.And, "OR" => TokenKind.Or,
            "XOR" => TokenKind.Xor, "NOT" => TokenKind.Not,
            _ => TokenKind.Identifier
        };

        private static void Advance(char value, ref int line, ref int column)
        {
            if (value == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }

        private bool Match(params TokenKind[] kinds)
        {
            if (!kinds.Contains(Current().Kind))
                return false;
            _position++;
            return true;
        }

        private bool Check(TokenKind kind) => Current().Kind == kind;
        private bool CheckNext(TokenKind kind) => _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == kind;

        private Token Consume(TokenKind kind, string message)
        {
            if (Check(kind))
                return _tokens[_position++];
            throw Error(Current(), message);
        }

        private Token Current() => _tokens[_position];
        private Token Previous() => _tokens[_position - 1];

        private static TagScriptException Error(Token token, string message) =>
            new($"Строка {token.Line}, столбец {token.Column}: {message}");

        private sealed record TagReference(
            ModbusSlave Slave,
            MemoryPoint? Point,
            string? VirtualVariableName,
            string? Bit,
            RegisterArea? Area);
    }
}
