using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Configuration;

public sealed class GuiNewJsonConfigImporter : IConfigImporter
{
    private const string FileName = "gui.new.json";

    public string Name => "gui.new.json";

    public bool CanImport(LegacyConfigSnapshot snapshot) => snapshot.GuiNewExists;

    public async Task ImportAsync(
        LegacyConfigSnapshot snapshot,
        UnifiedConfig target,
        ImportReport report,
        bool fillMissingOnly,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.GuiNewExists)
        {
            AppendUnique(report.MissingFiles, FileName);
            report.DefaultFallbackCount += 1;
            report.Warnings.Add("gui.new.json not found, skipped");
            return;
        }

        try
        {
            await using var stream = File.OpenRead(snapshot.GuiNewPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            var uiLanguage = ResolveUiLanguage(root, target);

            if (TryGetPropertyIgnoreCase(root, "Current", out var currentProp) && currentProp.ValueKind == JsonValueKind.String)
            {
                target.CurrentProfile = currentProp.GetString() ?? target.CurrentProfile;
                report.MappedFieldCount += 1;
            }

            if (TryGetPropertyIgnoreCase(root, "Configurations", out var configsProp) && configsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var configProp in configsProp.EnumerateObject())
                {
                    if (!target.Profiles.TryGetValue(configProp.Name, out var profile))
                    {
                        profile = new UnifiedProfile();
                        target.Profiles[configProp.Name] = profile;
                    }

                    if (configProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    JsonElement? taskQueueProp = null;
                    foreach (var valueProp in configProp.Value.EnumerateObject())
                    {
                        // dev-v2 (`382009b97b`) nests every per-profile setting under `Gui`. Must stay
                        // case sensitive: v6.3.7 stores the flat `GUI.InverseClearMode` key here.
                        if (string.Equals(valueProp.Name, "Gui", StringComparison.Ordinal)
                            && valueProp.Value.ValueKind == JsonValueKind.Object)
                        {
                            MergeProfileGuiSection(profile, configProp.Name, valueProp.Value, fillMissingOnly, report);
                            continue;
                        }

                        // dev-v2 382009b97b / 5b39509df6：Copilot 设置是每档案的独立对象（与 Gui 平级），
                        // 内容迁入 CurrentConfig.Copilot；MAAUnified 仍以扁平键存于 profile.Values。
                        if (string.Equals(valueProp.Name, "Copilot", StringComparison.Ordinal)
                            && valueProp.Value.ValueKind == JsonValueKind.Object)
                        {
                            MergeProfileCopilotSection(profile, configProp.Name, valueProp.Value, fillMissingOnly, report);
                            continue;
                        }

                        if (string.Equals(valueProp.Name, "TaskQueue", StringComparison.OrdinalIgnoreCase))
                        {
                            if (valueProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                taskQueueProp = valueProp.Value;
                            }
                            else if (valueProp.Value.ValueKind == JsonValueKind.Null)
                            {
                                if (!fillMissingOnly)
                                {
                                    profile.TaskQueue.Clear();
                                }
                            }
                            else
                            {
                                report.Warnings.Add(
                                    $"Profile `{configProp.Name}` has an invalid TaskQueue value; expected an array or null.");
                            }

                            continue;
                        }

                        var normalizedKey = NormalizeProfileKey(valueProp.Name);
                        if (string.Equals(normalizedKey, "GUI.InverseClearMode", StringComparison.OrdinalIgnoreCase))
                        {
                            JsonImportMergeHelper.MergeGlobalValue(
                                target,
                                "GUI.InverseClearMode",
                                JsonImportMergeHelper.ToJsonNode(valueProp.Value),
                                fillMissingOnly,
                                report);
                            continue;
                        }
                        JsonImportMergeHelper.MergeProfileValue(
                            profile,
                            configProp.Name,
                            normalizedKey,
                            JsonImportMergeHelper.ToJsonNode(valueProp.Value),
                            fillMissingOnly,
                            report);
                    }

                    if (taskQueueProp is JsonElement queueElement)
                    {
                        MergeTaskQueue(profile, target, queueElement, uiLanguage, fillMissingOnly, report);
                    }
                }
            }

            // v6.3.7 keeps UI settings in the root `GUI` object; dev-v2 (`382009b97b`) renamed it
            // to `Gui` and nested Background/Performance inside. The legacy branch stays
            // case-insensitive for older files that serialize the object under other casings.
            if (root.TryGetProperty("Gui", out _) && !root.TryGetProperty("GUI", out _))
            {
                MergeDevV2RootGui(root, target, fillMissingOnly, report);
            }
            else
            {
                MergeObjectAsGlobal(root, "GUI", target, fillMissingOnly, report);
            }

            MergeObjectAsGlobal(root, "VersionUpdate", target, fillMissingOnly, report);
            MergeObjectAsGlobal(root, "AnnouncementInfo", target, fillMissingOnly, report);
            MergeDevV2Update(root, target, fillMissingOnly, report);
            MergeTimersAsGlobal(root, target, fillMissingOnly, report);

            report.ImportedGuiNew = true;
            AppendUnique(report.ImportedFiles, FileName);
        }
        catch (Exception ex)
        {
            AppendUnique(report.DamagedFiles, FileName);
            report.Errors.Add($"Failed to import gui.new.json (errorType={ex.GetType().Name}).");
        }
    }

    private static void MergeTimersAsGlobal(
        JsonElement root,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (TryGetPropertyIgnoreCase(root, "Timers", out var timers) && timers.ValueKind == JsonValueKind.Object)
        {
            MergeTimerDictionary(timers, target, fillMissingOnly, report);
        }
        else if (TryGetPropertyIgnoreCase(root, "Timers", out timers) && timers.ValueKind == JsonValueKind.Array)
        {
            MergeTimerArray(timers, target, fillMissingOnly, report);
        }

        if (TryGetPropertyIgnoreCase(root, "Timer", out var timer) && timer.ValueKind == JsonValueKind.Object)
        {
            MergeTimerFlatObject(timer, target, fillMissingOnly, report);
        }
    }

    private static void MergeTimerDictionary(
        JsonElement timers,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        var properties = timers.EnumerateObject().ToArray();
        var zeroBased = properties.Any(static prop => string.Equals(prop.Name, "0", StringComparison.Ordinal));
        foreach (var prop in properties)
        {
            // dev-v2 (`382009b97b`): `Timers` became a named object whose slots live in
            // `Timers.List` as `{ Id, IsEnabled, Config, Hour, Minute }` items.
            if (string.Equals(prop.Name, "List", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    MergeDevV2TimerList(prop.Value, target, fillMissingOnly, report);
                }
                else if (prop.Value.ValueKind != JsonValueKind.Null)
                {
                    report.Warnings.Add($"Timer entry `List` was not recognized and was skipped.");
                }

                continue;
            }

            if (TryMergeTimerEntry(prop, zeroBased, target, fillMissingOnly, report))
            {
                continue;
            }

            var normalizedKey = NormalizeTimerKey(prop.Name);
            if (normalizedKey is null)
            {
                report.Warnings.Add($"Timer entry `{prop.Name}` was not recognized and was skipped.");
                continue;
            }

            JsonImportMergeHelper.MergeGlobalValue(
                target,
                normalizedKey,
                JsonImportMergeHelper.ToJsonNode(prop.Value),
                fillMissingOnly,
                report);
        }
    }

    private static void MergeDevV2TimerList(
        JsonElement list,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        var entries = list.EnumerateArray().ToArray();
        for (var position = 0; position < entries.Length; position++)
        {
            var entry = entries[position];
            if (entry.ValueKind != JsonValueKind.Object)
            {
                report.Warnings.Add($"Timer list entry #{position} is not an object and was skipped.");
                continue;
            }

            // `Timer.Id` is the 0-based slot id built by the dev-v2 ConfigConverter migration;
            // fall back to the array position when the id is missing or not an integer.
            var rawId = TryReadTimerIndex(entry, out var id) ? id : position;
            MergeTimerEntry(rawId, zeroBased: true, entry, target, fillMissingOnly, report);
        }
    }

    private static void MergeTimerArray(
        JsonElement timers,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        var entries = timers
            .EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .ToArray();
        var zeroBased = entries.Any(static entry => TryReadTimerIndex(entry, out var index) && index == 0);

        foreach (var entry in entries)
        {
            if (!TryReadTimerIndex(entry, out var rawIndex))
            {
                report.Warnings.Add("Timer entry without index was skipped.");
                continue;
            }

            var timer = TryGetPropertyIgnoreCase(entry, "Value", out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : entry;
            MergeTimerEntry(rawIndex, zeroBased, timer, target, fillMissingOnly, report);
        }
    }

    private static bool TryMergeTimerEntry(
        JsonProperty prop,
        bool zeroBased,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (prop.Value.ValueKind != JsonValueKind.Object || !int.TryParse(prop.Name, out var rawIndex))
        {
            return false;
        }

        var timer = TryGetPropertyIgnoreCase(prop.Value, "Value", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : prop.Value;
        MergeTimerEntry(rawIndex, zeroBased, timer, target, fillMissingOnly, report);
        return true;
    }

    private static void MergeTimerEntry(
        int rawIndex,
        bool zeroBased,
        JsonElement timer,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        var index = zeroBased ? rawIndex + 1 : rawIndex;
        if (index is < 1 or > 8)
        {
            report.Warnings.Add($"Timer entry `{rawIndex}` is outside the supported 1-8 range and was skipped.");
            return;
        }

        MergeTimerEnabledProperty(timer, index, target, fillMissingOnly, report);
        MergeTimerProperty(timer, "Hour", $"Timer.Timer{index}Hour", target, fillMissingOnly, report);
        MergeTimerProperty(timer, ["Minute", "Min"], $"Timer.Timer{index}Min", target, fillMissingOnly, report);
        MergeTimerProperty(timer, "Config", $"Timer.Timer{index}.Config", target, fillMissingOnly, report);
    }

    private static void MergeTimerEnabledProperty(
        JsonElement timer,
        int index,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        // v6.3.7 stores `Enable`; dev-v2 stores the tri-state `IsEnabled` (null = half checked).
        if (!TryGetPropertyIgnoreCase(timer, "IsEnabled", out var value)
            && !TryGetPropertyIgnoreCase(timer, "Enable", out value))
        {
            return;
        }

        // The settings page persists a half-checked slot as an empty string, so a null
        // `IsEnabled` must not be stored as JSON null (it would read back as the fallback).
        var node = value.ValueKind == JsonValueKind.Null
            ? JsonValue.Create(string.Empty)
            : JsonImportMergeHelper.ToJsonNode(value);
        JsonImportMergeHelper.MergeGlobalValue(
            target,
            $"Timer.Timer{index}",
            node,
            fillMissingOnly,
            report);
    }

    private static void MergeTimerProperty(
        JsonElement timer,
        string[] sourceProperties,
        string targetKey,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        foreach (var sourceProperty in sourceProperties)
        {
            if (!TryGetPropertyIgnoreCase(timer, sourceProperty, out _))
            {
                continue;
            }

            MergeTimerProperty(timer, sourceProperty, targetKey, target, fillMissingOnly, report);
            return;
        }
    }

    private static void MergeTimerProperty(
        JsonElement timer,
        string sourceProperty,
        string targetKey,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (!TryGetPropertyIgnoreCase(timer, sourceProperty, out var value))
        {
            return;
        }

        JsonImportMergeHelper.MergeGlobalValue(
            target,
            targetKey,
            JsonImportMergeHelper.ToJsonNode(value),
            fillMissingOnly,
            report);
    }

    private static void MergeTimerFlatObject(
        JsonElement timer,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        foreach (var prop in timer.EnumerateObject())
        {
            var normalizedKey = NormalizeTimerKey(prop.Name);
            if (normalizedKey is null)
            {
                continue;
            }

            JsonImportMergeHelper.MergeGlobalValue(
                target,
                normalizedKey,
                JsonImportMergeHelper.ToJsonNode(prop.Value),
                fillMissingOnly,
                report);
        }
    }

    private static string? NormalizeTimerKey(string key)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.StartsWith("Timer.", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("Timer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "ForceScheduledStart", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "ShowWindowBeforeForceScheduledStart", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "CustomConfig", StringComparison.OrdinalIgnoreCase))
        {
            return $"Timer.{trimmed}";
        }

        return null;
    }

    private static bool TryReadTimerIndex(JsonElement entry, out int index)
    {
        // `Id` is the dev-v2 `Timer.Id` slot id; `Key`/`Index` cover the intermediate
        // dictionary-serialized-as-array shape.
        foreach (var propertyName in (string[])["Id", "Key", "Index"])
        {
            if (TryReadTimerIndexProperty(entry, propertyName, out index))
            {
                return true;
            }
        }

        index = 0;
        return false;
    }

    private static bool TryReadTimerIndexProperty(JsonElement entry, string propertyName, out int index)
    {
        if (TryGetPropertyIgnoreCase(entry, propertyName, out var key))
        {
            if (key.ValueKind == JsonValueKind.Number && key.TryGetInt32(out index))
            {
                return true;
            }

            if (key.ValueKind == JsonValueKind.String
                && int.TryParse(key.GetString(), out index))
            {
                return true;
            }
        }

        index = 0;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void MergeObjectAsGlobal(
        JsonElement root,
        string objectName,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (!TryGetPropertyIgnoreCase(root, objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            JsonImportMergeHelper.MergeGlobalValue(
                target,
                LegacyConfigValueMappings.NormalizeGlobalKey($"{objectName}.{prop.Name}"),
                JsonImportMergeHelper.ToJsonNode(prop.Value),
                fillMissingOnly,
                report);
        }
    }

    private static void MergeDevV2Update(
        JsonElement root,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (!root.TryGetProperty("Update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in update.EnumerateObject())
        {
            var legacyKey = LegacyConfigValueMappings.TryMapDevV2UpdateKey(prop.Name);
            if (legacyKey is null)
            {
                report.Warnings.Add($"Update key `{prop.Name}` is not supported by MAAUnified and was skipped.");
                continue;
            }

            JsonImportMergeHelper.MergeGlobalValue(
                target,
                LegacyConfigValueMappings.NormalizeGlobalKey(legacyKey),
                JsonImportMergeHelper.ToJsonNode(prop.Value),
                fillMissingOnly,
                report);
        }
    }

    private static void MergeDevV2RootGui(
        JsonElement root,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (!root.TryGetProperty("Gui", out var gui) || gui.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in gui.EnumerateObject())
        {
            MergeDevV2RootGuiValue(prop.Name, prop.Value, target, fillMissingOnly, report);
        }
    }

    private static void MergeDevV2RootGuiValue(
        string path,
        JsonElement value,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        var legacyKey = LegacyConfigValueMappings.TryMapDevV2RootGuiKey(path);
        if (legacyKey is not null)
        {
            JsonImportMergeHelper.MergeGlobalValue(
                target,
                LegacyConfigValueMappings.NormalizeGlobalKey(legacyKey),
                JsonImportMergeHelper.ToJsonNode(value),
                fillMissingOnly,
                report);
            return;
        }

        if (value.ValueKind == JsonValueKind.Object && LegacyConfigValueMappings.IsDevV2RootGuiSection(path))
        {
            foreach (var child in value.EnumerateObject())
            {
                MergeDevV2RootGuiValue($"{path}.{child.Name}", child.Value, target, fillMissingOnly, report);
            }

            return;
        }

        report.Warnings.Add($"Root `Gui` key `{path}` is not supported by MAAUnified and was skipped.");
    }

    private static void MergeProfileCopilotSection(
        UnifiedProfile profile,
        string profileName,
        JsonElement copilot,
        bool fillMissingOnly,
        ImportReport report)
    {
        foreach (var prop in copilot.EnumerateObject())
        {
            var key = prop.Name switch
            {
                "TaskList" => ConfigurationKeys.CopilotTaskList,
                "UserAdditional" => ConfigurationKeys.CopilotUserAdditional,
                "EnableUserAdditional" => ConfigurationKeys.CopilotAddUserAdditional,
                "SupportMode" => ConfigurationKeys.CopilotSupportUnitUsage,
                "UseFormation" => CopilotSettingKeys.UseFormation,
                "SelectFormation" => ConfigurationKeys.CopilotSelectFormation,
                "LoopTimes" => ConfigurationKeys.CopilotLoopTimes,
                _ => null,
            };
            if (key is null)
            {
                report.Warnings.Add(
                    $"Profile `{profileName}` key `Copilot.{prop.Name}` is not supported by MAAUnified and was skipped.");
                continue;
            }

            JsonNode? value;
            switch (prop.Name)
            {
                case "TaskList":
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        report.Warnings.Add(
                            $"Profile `{profileName}` Copilot.TaskList is not an array and was skipped.");
                        continue;
                    }

                    // 列表项保留原字段（name/file_path/copilot_id/is_raid/is_checked 等大小写变体
                    // 由 PersistedCopilotItem 读取逻辑统一兼容），下次保存时再规范化为 MAAUnified 形状。
                    value = JsonImportMergeHelper.ToJsonNode(prop.Value);
                    break;
                case "UserAdditional":
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        report.Warnings.Add(
                            $"Profile `{profileName}` Copilot.UserAdditional is not an array and was skipped.");
                        continue;
                    }

                    // dev-v2 保存为 UserAdditional 对象数组；MAAUnified 的 UserAdditional 是字符串键，
                    // 这里转成等价的 JSON 字符串，交给 ParseUserAdditionalItems 统一解析。
                    value = JsonValue.Create(prop.Value.GetRawText());
                    break;
                case "SupportMode":
                    if (!TryNormalizeCopilotSupportMode(prop.Value, out var supportMode))
                    {
                        report.Warnings.Add(
                            $"Profile `{profileName}` Copilot.SupportMode has an unrecognized value and was skipped.");
                        continue;
                    }

                    value = JsonValue.Create(supportMode.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    value = JsonImportMergeHelper.ToJsonNode(prop.Value);
                    break;
            }

            JsonImportMergeHelper.MergeProfileValue(profile, profileName, key, value, fillMissingOnly, report);
        }
    }

    // WPF CopilotSupportMode：WhenNeeded = 1、Random = 3；旧档可能存数字或枚举名
    private static bool TryNormalizeCopilotSupportMode(JsonElement value, out int supportMode)
    {
        supportMode = 1;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt32(out var numeric):
                supportMode = numeric == 3 ? 3 : 1;
                return true;
            case JsonValueKind.String:
                var raw = value.GetString();
                if (string.Equals(raw, "Random", StringComparison.OrdinalIgnoreCase))
                {
                    supportMode = 3;
                    return true;
                }

                if (string.Equals(raw, "WhenNeeded", StringComparison.OrdinalIgnoreCase))
                {
                    supportMode = 1;
                    return true;
                }

                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    supportMode = parsed == 3 ? 3 : 1;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static void MergeProfileGuiSection(
        UnifiedProfile profile,
        string profileName,
        JsonElement gui,
        bool fillMissingOnly,
        ImportReport report)
    {
        foreach (var prop in gui.EnumerateObject())
        {
            MergeProfileGuiValue(profile, profileName, prop.Name, prop.Value, fillMissingOnly, report);
        }
    }

    private static void MergeProfileGuiValue(
        UnifiedProfile profile,
        string profileName,
        string path,
        JsonElement value,
        bool fillMissingOnly,
        ImportReport report)
    {
        var legacyKey = LegacyConfigValueMappings.TryMapDevV2ProfileGuiKey(path);
        if (legacyKey is not null)
        {
            WarnOnUnsupportedPostActionFlags(profileName, legacyKey, value, report);
            JsonImportMergeHelper.MergeProfileValue(
                profile,
                profileName,
                LegacyConfigValueMappings.NormalizeProfileKey(legacyKey),
                JsonImportMergeHelper.ToJsonNode(value),
                fillMissingOnly,
                report);
            return;
        }

        if (value.ValueKind == JsonValueKind.Object && LegacyConfigValueMappings.IsDevV2ProfileGuiSection(path))
        {
            foreach (var child in value.EnumerateObject())
            {
                MergeProfileGuiValue(profile, profileName, $"{path}.{child.Name}", child.Value, fillMissingOnly, report);
            }

            return;
        }

        report.Warnings.Add($"Profile `{profileName}` key `Gui.{path}` is not supported by MAAUnified and was skipped.");
    }

    private static void WarnOnUnsupportedPostActionFlags(
        string profileName,
        string legacyKey,
        JsonElement value,
        ImportReport report)
    {
        // dev-v2 `SkipOnError`/`LockScreen`/`ExitPCClient` flags (P1-03 `cda1c3b07f`,
        // P1-04 `d58cb0825b`) have no MAAUnified counterpart yet; keep the supported low bits
        // and report the rest instead of dropping them silently.
        const int supportedPostActionMask = 0xFF;
        if (!string.Equals(legacyKey, "MainFunction.PostActions", StringComparison.Ordinal)
            || !TryReadPostActionFlags(value, out var flags)
            || (flags & ~supportedPostActionMask) == 0)
        {
            return;
        }

        report.Warnings.Add(
            $"Profile `{profileName}` post actions contain unsupported flags 0x{flags & ~supportedPostActionMask:X}; only ExitArknights/BackToAndroidHome/ExitEmulator/ExitSelf/Hibernate/Shutdown/Sleep were imported.");
    }

    private static bool TryReadPostActionFlags(JsonElement value, out int flags)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out flags))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out flags))
        {
            return true;
        }

        flags = 0;
        return false;
    }

    private static void MergeTaskQueue(
        UnifiedProfile profile,
        UnifiedConfig config,
        JsonElement taskQueue,
        string uiLanguage,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (fillMissingOnly && profile.TaskQueue.Count > 0)
        {
            report.ConflictCount += 1;
            return;
        }

        if (!fillMissingOnly)
        {
            profile.TaskQueue.Clear();
        }

        foreach (var task in taskQueue.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object)
            {
                report.Warnings.Add("TaskQueue contains non-object entry and was skipped.");
                continue;
            }

            var taskNode = JsonImportMergeHelper.ToJsonNode(task) as JsonObject;
            if (taskNode is null)
            {
                report.Warnings.Add("TaskQueue entry could not be converted to JsonObject and was skipped.");
                continue;
            }

            if (!LegacyTaskSchemaConverter.TryConvertLegacyTask(
                    taskNode,
                    profile,
                    config,
                    out var convertedTask,
                    out var error,
                    LegacyTaskSchemaConversionOptions.GuiNewJson,
                    uiLanguage))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    report.Errors.Add(error);
                }
            }

            profile.TaskQueue.Add(convertedTask);
            report.MappedFieldCount += 1;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;
    }

    private static string ResolveUiLanguage(JsonElement root, UnifiedConfig target)
    {
        // v6.3.7 keeps the language in the root `GUI` object; dev-v2 (`382009b97b`) renames it
        // to `Gui`. Both must be read before the globals are merged.
        if (TryReadRootGuiLanguage(root, "GUI") is { } legacyLanguage)
        {
            return legacyLanguage;
        }

        if (TryReadRootGuiLanguage(root, "Gui") is { } language)
        {
            return language;
        }

        if (target.GlobalValues.TryGetValue("GUI.Localization", out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        return "zh-cn";
    }

    private static string? TryReadRootGuiLanguage(JsonElement root, string guiPropertyName)
    {
        return root.TryGetProperty(guiPropertyName, out var gui)
               && gui.ValueKind == JsonValueKind.Object
               && gui.TryGetProperty("Localization", out var language)
               && language.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(language.GetString())
            ? language.GetString()
            : null;
    }

    private static string NormalizeProfileKey(string key)
    {
        return LegacyConfigValueMappings.NormalizeProfileKey(key);
    }

    private static void AppendUnique(ICollection<string> collection, string value)
    {
        if (!collection.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            collection.Add(value);
        }
    }
}
