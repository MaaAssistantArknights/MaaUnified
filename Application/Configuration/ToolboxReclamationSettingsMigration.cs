using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Application.Configuration;

/// <summary>
/// WPF dev-v2 (66e003377c) 把生息演算从任务队列迁入 <c>Toolbox.MiniGame.Reclamation</c>：
/// 队列里的 Reclamation 条目不再反序列化为任务，首个条目的参数搬进工具箱设置，
/// 其余重复条目直接丢弃。
/// 导入旧 <c>gui.json</c> / <c>gui.new.json</c> 时按同一口径搬运，参数保持不变。
/// </summary>
internal static class ToolboxReclamationSettingsMigration
{
    private const string ReclamationTaskType = "Reclamation";
    private const string ReclamationMiniGameTaskName = "Reclamation";

    /// <summary>
    /// 判断转换后的旧任务条目是否为生息演算。
    /// </summary>
    public static bool IsReclamationTask(UnifiedTaskItem task)
    {
        return task is not null
            && string.Equals(
                TaskModuleTypes.Normalize(task.Type),
                ReclamationTaskType,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把旧队列条目的参数写入工具箱生息演算设置。仅在目标键缺失时写入：
    /// 与 WPF <c>ToolboxSectionMigrationConverter</c> 一致，已存在的新版设置优先。
    /// </summary>
    /// <returns>至少写入一项参数时返回 true。</returns>
    public static bool ApplyTaskParams(UnifiedTaskItem task, UnifiedConfig config)
    {
        if (!IsReclamationTask(task) || config is null)
        {
            return false;
        }

        var updates = BuildSettingUpdates(task.Params);
        var applied = false;
        foreach (var (key, value) in updates)
        {
            if (config.GlobalValues.ContainsKey(key))
            {
                continue;
            }

            config.GlobalValues[key] = value;
            applied = true;
        }

        // 生息演算成为工具箱条目后，迁移过的配置直接定位到该条目，
        // 不覆盖用户此前选中的其它小游戏。
        if (applied
            && !config.GlobalValues.ContainsKey(LegacyConfigurationKeys.MiniGameTaskName))
        {
            config.GlobalValues[LegacyConfigurationKeys.MiniGameTaskName] =
                JsonValue.Create(ReclamationMiniGameTaskName);
        }

        return applied;
    }

    /// <summary>
    /// 把旧队列条目参数转换为工具箱键值（dev-v2 <c>Toolbox.MiniGame.Reclamation.*</c>）。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, JsonNode?>> BuildSettingUpdates(JsonObject? parameters)
    {
        parameters ??= [];

        var theme = ReadString(parameters, "theme", "Tales");
        var mode = ReadInt(parameters, "mode", DefaultModeForTheme(theme));
        var incrementMode = ReadInt(parameters, "increment_mode", 0);
        var maxCraftCountPerRound = ReadInt(parameters, "num_craft_batches", 16);
        var toolsToCraft = string.Join(';', ReadStringArray(parameters, "tools_to_craft"));
        var clearStore = ReadBool(parameters, "clear_store", true);

        return
        [
            new(ToolboxSettingKeys.ReclamationTheme, JsonValue.Create(theme)),
            new(ToolboxSettingKeys.ReclamationMode, JsonValue.Create(mode)),
            new(ToolboxSettingKeys.ReclamationIncrementMode, JsonValue.Create(incrementMode)),
            new(ToolboxSettingKeys.ReclamationMaxCraftCountPerRound, JsonValue.Create(maxCraftCountPerRound)),
            new(ToolboxSettingKeys.ReclamationToolToCraft, JsonValue.Create(toolsToCraft)),
            new(ToolboxSettingKeys.ReclamationClearStore, JsonValue.Create(clearStore)),
        ];
    }

    /// <summary>
    /// 读取工具箱生息演算设置（远控 <c>Toolbox-Reclamation</c> 与工具箱启动共用）。
    /// </summary>
    public static ReclamationTaskParamsDto ReadSettings(UnifiedConfig config)
    {
        var theme = ReadGlobalString(config, ToolboxSettingKeys.ReclamationTheme, "Tales");
        var mode = ReadGlobalInt(config, ToolboxSettingKeys.ReclamationMode, DefaultModeForTheme(theme));
        var incrementMode = ReadGlobalInt(config, ToolboxSettingKeys.ReclamationIncrementMode, 0);
        var maxCraftCountPerRound = ReadGlobalInt(config, ToolboxSettingKeys.ReclamationMaxCraftCountPerRound, 16);
        var toolToCraft = ReadGlobalString(config, ToolboxSettingKeys.ReclamationToolToCraft, string.Empty);

        return new ReclamationTaskParamsDto
        {
            Theme = theme,
            Mode = mode,
            IncrementMode = incrementMode,
            NumCraftBatches = maxCraftCountPerRound,
            ToolsToCraft = toolToCraft
                .Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ClearStore = ReadGlobalBool(config, ToolboxSettingKeys.ReclamationClearStore, true),
        };
    }

    private static string ReadGlobalString(UnifiedConfig config, string key, string fallback)
    {
        return config.GlobalValues.TryGetValue(key, out var node)
               && node is JsonValue value
               && value.TryGetValue(out string? text)
               && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : fallback;
    }

    private static int ReadGlobalInt(UnifiedConfig config, string key, int fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out int number))
        {
            return number;
        }

        return value.TryGetValue(out string? text)
               && int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out bool boolean))
        {
            return boolean;
        }

        if (value.TryGetValue(out string? text))
        {
            var normalized = text?.Trim();
            if (bool.TryParse(normalized, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number != 0;
            }
        }

        return fallback;
    }

    private static int DefaultModeForTheme(string theme)
    {
        return string.Equals(theme, "RelaunchAnchor", StringComparison.OrdinalIgnoreCase) ? 16 : 1;
    }

    private static string ReadString(JsonObject parameters, string name, string fallback)
    {
        if (parameters[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        return fallback;
    }

    private static int ReadInt(JsonObject parameters, string name, int fallback)
    {
        if (parameters[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out int number))
        {
            return number;
        }

        if (value.TryGetValue(out string? text)
            && int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool ReadBool(JsonObject parameters, string name, bool fallback)
    {
        if (parameters[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out bool boolean))
        {
            return boolean;
        }

        if (value.TryGetValue(out string? text))
        {
            var normalized = text?.Trim();
            if (bool.TryParse(normalized, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number != 0;
            }
        }

        return fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject parameters, string name)
    {
        if (parameters[name] is not JsonArray array)
        {
            // 旧 WPF 配置里 tools_to_craft 也可能存成单个分号串。
            var single = ReadString(parameters, name, string.Empty);
            return string.IsNullOrWhiteSpace(single)
                ? []
                : single
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
        }

        return array
            .Select(item => item is JsonValue value && value.TryGetValue(out string? text) ? text?.Trim() : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToArray();
    }
}
