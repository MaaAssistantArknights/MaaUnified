using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.Infrastructure;

internal static class OverlayTargetPersistence
{
    public static string SerializePreviewPreference(OverlayTarget target)
    {
        return IsPreviewId(target.Id) ? bool.TrueString : bool.FalseString;
    }

    public static string Serialize(OverlayTarget target)
    {
        var payload = new PersistedOverlayTargetSelection(
            TargetId: target.Id,
            NativeHandle: target.NativeHandle,
            ProcessId: target.ProcessId,
            ProcessName: NormalizeText(target.ProcessName),
            WindowTitle: NormalizeText(target.WindowTitle));
        return JsonSerializer.Serialize(payload);
    }

    public static OverlayTarget? ResolveSelection(
        IReadOnlyList<OverlayTarget> targets,
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        string? preferredTargetId = null)
    {
        if (targets.Count == 0)
        {
            return null;
        }

        var previewPinned = LoadPreviewPreference(globalValues);
        if (TryFindById(targets, preferredTargetId, out var selected)
            && ShouldHonorSelection(selected, targets, previewPinned))
        {
            return selected;
        }

        var persisted = Load(globalValues);
        if (persisted is not null)
        {
            if (TryFindById(targets, persisted.TargetId, out selected)
                && MatchesPersistedIdentity(selected, persisted)
                && ShouldHonorSelection(selected, targets, previewPinned))
            {
                return selected;
            }

            if (persisted.NativeHandle is long nativeHandle
                && TryFindByHandle(targets, nativeHandle, out selected)
                && MatchesPersistedIdentity(selected, persisted))
            {
                return selected;
            }

            if (TryFindByProcessMetadata(targets, persisted, out selected))
            {
                return selected;
            }
        }

        if (!previewPinned)
        {
            var primaryNative = targets.FirstOrDefault(static target => !IsPreviewId(target.Id) && target.IsPrimary);
            if (primaryNative is not null)
            {
                return primaryNative;
            }

            var firstNative = targets.FirstOrDefault(static target => !IsPreviewId(target.Id));
            if (firstNative is not null)
            {
                return firstNative;
            }
        }

        return targets.FirstOrDefault(static target => target.IsPrimary) ?? targets[0];
    }

    public static bool ShouldPersistResolvedSelection(
        OverlayTarget? selected,
        IReadOnlyDictionary<string, JsonNode?> globalValues)
    {
        // WPF parity (OverlayViewModel.LoadConfig → SetTargetHwnd(found, persist:false), freeze
        // 23ddd27): automatic re-resolution never rewrites the persisted binding; WPF persists only
        // on an explicit user pick (TaskQueueViewModel.ChooseOverlayTarget → SetTargetHwnd with
        // persist: true). Persisting an automatic fallback while the persisted target is
        // temporarily absent (e.g. the game is not running) would overwrite the binding with an
        // unrelated window and break automatic re-discovery when the game returns. Persist only
        // when no native binding exists yet (first write / legacy preview → native migration) or
        // when the resolved target still carries the persisted identity (a restarted game re-found
        // by process id or by process name + title).
        if (selected is null)
        {
            return false;
        }

        var persisted = Load(globalValues);
        if (persisted is null || IsPreviewId(persisted.TargetId))
        {
            return true;
        }

        return MatchesProcessMetadata(selected, persisted);
    }

    public static bool ShouldDefaultToPreview(
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        string? preferredTargetId = null)
    {
        return IsPreviewId(preferredTargetId)
            && Load(globalValues) is null
            && !LoadPreviewPreference(globalValues);
    }

    public static PersistedOverlayTargetSelection? Load(IReadOnlyDictionary<string, JsonNode?> globalValues)
    {
        if (!globalValues.TryGetValue(LegacyConfigurationKeys.OverlayTarget, out var node)
            || node is null)
        {
            return null;
        }

        string raw;
        try
        {
            raw = node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            raw = node.ToJsonString();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            return new PersistedOverlayTargetSelection(
                TargetId: ReadString(root, "TargetId"),
                NativeHandle: ReadLong(root, "NativeHandle") ?? ReadLong(root, "Hwnd"),
                ProcessId: ReadInt(root, "ProcessId"),
                ProcessName: ReadString(root, "ProcessName"),
                WindowTitle: ReadString(root, "WindowTitle") ?? ReadString(root, "Title"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool LoadPreviewPreference(IReadOnlyDictionary<string, JsonNode?> globalValues)
    {
        if (!globalValues.TryGetValue(LegacyConfigurationKeys.OverlayPreviewPinned, out var node)
            || node is null)
        {
            return false;
        }

        return TryReadBoolean(node, out var pinned) && pinned;
    }

    private static bool TryFindById(
        IReadOnlyList<OverlayTarget> targets,
        string? targetId,
        out OverlayTarget? selected)
    {
        selected = null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        selected = targets.FirstOrDefault(target => string.Equals(target.Id, targetId, StringComparison.Ordinal));
        return selected is not null;
    }

    private static bool ShouldHonorSelection(
        OverlayTarget? selected,
        IReadOnlyList<OverlayTarget> targets,
        bool previewPinned)
    {
        if (selected is null)
        {
            return false;
        }

        if (!IsPreviewId(selected.Id))
        {
            return true;
        }

        return previewPinned || !targets.Any(static target => !IsPreviewId(target.Id));
    }

    private static bool TryFindByHandle(
        IReadOnlyList<OverlayTarget> targets,
        long nativeHandle,
        out OverlayTarget? selected)
    {
        selected = targets.FirstOrDefault(target => target.NativeHandle == nativeHandle);
        return selected is not null;
    }

    private static bool TryFindByProcessMetadata(
        IReadOnlyList<OverlayTarget> targets,
        PersistedOverlayTargetSelection persisted,
        out OverlayTarget? selected)
    {
        selected = targets.FirstOrDefault(target => MatchesProcessMetadata(target, persisted));
        return selected is not null;
    }

    private static bool MatchesProcessMetadata(
        OverlayTarget target,
        PersistedOverlayTargetSelection persisted)
    {
        // WPF parity (OverlayViewModel.TryFindWindow enumeration, freeze 23ddd27): per window, a
        // process-id match alone is sufficient; otherwise fall back to process-name equality plus
        // a one-directional title containment. The name+title fallback intentionally does not
        // require the persisted process id, so a restarted game (new pid/hwnd) is still found.
        if (IsPreviewId(target.Id))
        {
            return false;
        }

        if (persisted.ProcessId is { } processId && target.ProcessId == processId)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(persisted.ProcessName)
            && string.Equals(target.ProcessName, persisted.ProcessName, StringComparison.OrdinalIgnoreCase)
            && (persisted.WindowTitle is not { } persistedTitle
                || (target.WindowTitle ?? string.Empty).Contains(persistedTitle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesPersistedIdentity(
        OverlayTarget? target,
        PersistedOverlayTargetSelection persisted)
    {
        // WPF parity (OverlayViewModel.TryFindWindow hwnd path, freeze 23ddd27): a persisted
        // hwnd/TargetId is trusted only when the window still belongs to the persisted process
        // id; WPF performs no process-name or title check here. Configurations without a process
        // id cannot be validated (WPF compares against pid 0, which never matches a real window)
        // and must fall through to the metadata-based re-resolution.
        return target is not null
            && persisted.ProcessId is { } processId
            && target.ProcessId == processId;
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryReadBoolean(JsonNode node, out bool value)
    {
        value = false;

        try
        {
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<bool>(out value))
                {
                    return true;
                }

                if (jsonValue.TryGetValue<string>(out var text)
                    && bool.TryParse(text, out value))
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Ignore malformed settings and fall back to false.
        }

        return false;
    }

    private static bool IsPreviewId(string? targetId)
    {
        return string.Equals(targetId, "preview", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return NormalizeText(property.GetString());
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return null;
    }
}

internal sealed record PersistedOverlayTargetSelection(
    string? TargetId,
    long? NativeHandle,
    int? ProcessId,
    string? ProcessName,
    string? WindowTitle);
