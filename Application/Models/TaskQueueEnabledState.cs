using System.Text.Json.Nodes;

namespace MAAUnified.Application.Models;

public static class TaskQueueEnabledState
{
    public const string MainTasksInvertNullFunctionKey = "GUI.MainTasksInvertNullFunction";

    public static bool IsEffectivelyEnabled(UnifiedTaskItem task, UnifiedConfig config)
    {
        return task.IsEnabled is true
            || (task.IsEnabled is null && !UsesInvertedNullSemantics(config));
    }

    public static bool ResetOneShotValue(UnifiedConfig config)
    {
        return UsesInvertedNullSemantics(config);
    }

    public static bool? ToggleOneShotValue(bool? current)
    {
        return current is null ? false : null;
    }

    public static bool UsesInvertedNullSemantics(UnifiedConfig config)
    {
        return ReadBool(config.GlobalValues, MainTasksInvertNullFunctionKey, fallback: false);
    }

    private static bool ReadBool(Dictionary<string, JsonNode?> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<string>(out var stringValue)
                && bool.TryParse(stringValue, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }
}
