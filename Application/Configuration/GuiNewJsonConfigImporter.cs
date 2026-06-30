using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;

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

            if (root.TryGetProperty("Current", out var currentProp) && currentProp.ValueKind == JsonValueKind.String)
            {
                target.CurrentProfile = currentProp.GetString() ?? target.CurrentProfile;
                report.MappedFieldCount += 1;
            }

            if (root.TryGetProperty("Configurations", out var configsProp) && configsProp.ValueKind == JsonValueKind.Object)
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
                        if (string.Equals(valueProp.Name, "TaskQueue", StringComparison.OrdinalIgnoreCase)
                            && valueProp.Value.ValueKind == JsonValueKind.Array)
                        {
                            taskQueueProp = valueProp.Value;
                            continue;
                        }

                        var normalizedKey = NormalizeProfileKey(valueProp.Name);
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
                        MergeTaskQueue(profile, target, queueElement, fillMissingOnly, report);
                    }
                }
            }

            MergeObjectAsGlobal(root, "GUI", target, fillMissingOnly, report);
            MergeObjectAsGlobal(root, "VersionUpdate", target, fillMissingOnly, report);
            MergeObjectAsGlobal(root, "AnnouncementInfo", target, fillMissingOnly, report);
            MergeTimersAsGlobal(root, target, fillMissingOnly, report);

            report.ImportedGuiNew = true;
            AppendUnique(report.ImportedFiles, FileName);
        }
        catch (Exception ex)
        {
            AppendUnique(report.DamagedFiles, FileName);
            report.Errors.Add($"Failed to import gui.new.json: {ex.Message}");
        }
    }

    private static void MergeTimersAsGlobal(
        JsonElement root,
        UnifiedConfig target,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (root.TryGetProperty("Timers", out var timers) && timers.ValueKind == JsonValueKind.Object)
        {
            MergeTimerDictionary(timers, target, fillMissingOnly, report);
        }
        else if (root.TryGetProperty("Timers", out timers) && timers.ValueKind == JsonValueKind.Array)
        {
            MergeTimerArray(timers, target, fillMissingOnly, report);
        }

        if (root.TryGetProperty("Timer", out var timer) && timer.ValueKind == JsonValueKind.Object)
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

        MergeTimerProperty(timer, "Enable", $"Timer.Timer{index}", target, fillMissingOnly, report);
        MergeTimerProperty(timer, "Hour", $"Timer.Timer{index}Hour", target, fillMissingOnly, report);
        MergeTimerProperty(timer, ["Minute", "Min"], $"Timer.Timer{index}Min", target, fillMissingOnly, report);
        MergeTimerProperty(timer, "Config", $"Timer.Timer{index}.Config", target, fillMissingOnly, report);
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
        if (TryGetPropertyIgnoreCase(entry, "Key", out var key))
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

        if (TryGetPropertyIgnoreCase(entry, "Index", out var indexProperty))
        {
            if (indexProperty.ValueKind == JsonValueKind.Number && indexProperty.TryGetInt32(out index))
            {
                return true;
            }

            if (indexProperty.ValueKind == JsonValueKind.String
                && int.TryParse(indexProperty.GetString(), out index))
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
        if (!root.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            JsonImportMergeHelper.MergeGlobalValue(
                target,
                $"{objectName}.{prop.Name}",
                JsonImportMergeHelper.ToJsonNode(prop.Value),
                fillMissingOnly,
                report);
        }
    }

    private static void MergeTaskQueue(
        UnifiedProfile profile,
        UnifiedConfig config,
        JsonElement taskQueue,
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

            if (IsUnsupportedReclamationMode(taskNode))
            {
                var taskName = ReadString(taskNode["Name"]) ?? "Reclamation";
                report.Warnings.Add(
                    $"Reclamation task `{taskName}` uses legacy RA/RelaunchAnchor mode, which is not supported by current schema; imported as archive mode.");
            }

            if (!LegacyTaskSchemaConverter.TryConvertLegacyTask(taskNode, profile, config, out var convertedTask, out var error))
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

    private static bool IsUnsupportedReclamationMode(JsonObject task)
    {
        var type = ReadString(task["$type"]) ?? ReadString(task["Type"]);
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        var normalizedType = type.Split(',')[0].Trim();
        var lastDot = normalizedType.LastIndexOf('.');
        if (lastDot >= 0)
        {
            normalizedType = normalizedType[(lastDot + 1)..];
        }

        return (string.Equals(normalizedType, "ReclamationTask", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedType, "Reclamation", StringComparison.OrdinalIgnoreCase))
            && LegacyConfigValueMappings.IsUnsupportedReclamationMode(task["Mode"]);
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text)
            ? text
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
