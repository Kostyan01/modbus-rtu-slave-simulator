using ModbusRtuSimulator.Models;

namespace ModbusRtuSimulator.Services;

public static class VirtualVariableMacroEvaluator
{
    public static bool IsSatisfied(VirtualVariableMacroAction action, VirtualVariable variable)
    {
        var value = TagScriptEngine.GetVirtualVariableValue(variable);
        if (variable.Type == VirtualVariableType.Bool)
            return action.Condition == MacroCondition.True && value is true;
        if (variable.Type == VirtualVariableType.Time || value is not decimal number)
            return false;

        return action.Condition switch
        {
            MacroCondition.Greater => number > action.Threshold,
            MacroCondition.Less => number < action.Threshold,
            MacroCondition.LessOrEqual => number <= action.Threshold,
            MacroCondition.GreaterOrEqual => number >= action.Threshold,
            MacroCondition.Equal => number == action.Threshold,
            _ => false
        };
    }

    public static bool IsValidFor(VirtualVariableMacroAction action, VirtualVariable variable) =>
        variable.Type switch
        {
            VirtualVariableType.Bool => action.Condition == MacroCondition.True,
            VirtualVariableType.Time => false,
            _ => Enum.IsDefined(action.Condition) && action.Condition != MacroCondition.True
        };
}
