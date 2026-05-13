using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed record HotkeyConfigurationSnapshot(
    string ShowGui,
    string LinkStart,
    IReadOnlyList<string> Warnings);

public static class HotkeyConfigurationCodec
{
    public const string ShowGuiHotkeyName = "ShowGui";
    public const string LinkStartHotkeyName = "LinkStart";
    public const string MacDefaultsVersionKey = "GUI.HotKeys.MacDefaultsVersion";
    private const int CurrentMacDefaultsVersion = 1;
    private const string LegacyDefaultHotkeyShowGui = "Ctrl+Shift+Alt+M";
    private const string LegacyDefaultHotkeyLinkStart = "Ctrl+Shift+Alt+L";
    private const string MacDefaultHotkeyShowGui = "Meta+Shift+M";
    private const string MacDefaultHotkeyLinkStart = "Meta+Shift+L";

    private static readonly IReadOnlyDictionary<int, string> LegacyWpfKeyMap = BuildLegacyWpfKeyMap();

    public static string DefaultHotkeyShowGui => GetDefaultHotkeyShowGui();

    public static string DefaultHotkeyLinkStart => GetDefaultHotkeyLinkStart();

    public static HotkeyConfigurationSnapshot Parse(string? raw)
    {
        var warnings = new List<string>();
        var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (LooksLikeLegacyJson(raw))
            {
                ParseLegacyJson(raw, parsed, warnings);
            }
            else
            {
                ParseSemicolon(raw, parsed, warnings);
            }
        }

        return new HotkeyConfigurationSnapshot(
            ShowGui: ResolveGesture(parsed, ShowGuiHotkeyName, DefaultHotkeyShowGui, warnings),
            LinkStart: ResolveGesture(parsed, LinkStartHotkeyName, DefaultHotkeyLinkStart, warnings),
            Warnings: warnings);
    }

    public static string Serialize(string? showGui, string? linkStart)
    {
        var normalizedShowGui = NormalizeDraftGesture(showGui);
        var normalizedLinkStart = NormalizeDraftGesture(linkStart);
        return $"{ShowGuiHotkeyName}={normalizedShowGui};{LinkStartHotkeyName}={normalizedLinkStart}";
    }

    public static string NormalizeDraftGesture(string? gesture)
    {
        var trimmed = gesture?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return HotkeyGestureCodec.TryNormalize(trimmed, out var normalized)
            ? normalized
            : trimmed;
    }

    public static bool ApplyPlatformDefaultsMigration(UnifiedConfig config)
        => ApplyPlatformDefaultsMigration(config, OperatingSystem.IsMacOS());

    internal static bool ApplyPlatformDefaultsMigration(UnifiedConfig config, bool isMacOS)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!isMacOS)
        {
            return false;
        }

        var currentVersion = ReadInt(config, MacDefaultsVersionKey);
        if (currentVersion >= CurrentMacDefaultsVersion)
        {
            return false;
        }

        config.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create(
            $"{ShowGuiHotkeyName}={MacDefaultHotkeyShowGui};{LinkStartHotkeyName}={MacDefaultHotkeyLinkStart}");
        config.GlobalValues[MacDefaultsVersionKey] = JsonValue.Create(CurrentMacDefaultsVersion.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    internal static string GetDefaultHotkeyShowGui(bool isMacOS)
        => isMacOS ? MacDefaultHotkeyShowGui : LegacyDefaultHotkeyShowGui;

    internal static string GetDefaultHotkeyLinkStart(bool isMacOS)
        => isMacOS ? MacDefaultHotkeyLinkStart : LegacyDefaultHotkeyLinkStart;

    private static string GetDefaultHotkeyShowGui()
        => GetDefaultHotkeyShowGui(OperatingSystem.IsMacOS());

    private static string GetDefaultHotkeyLinkStart()
        => GetDefaultHotkeyLinkStart(OperatingSystem.IsMacOS());

    private static void ParseSemicolon(
        string raw,
        IDictionary<string, string?> parsed,
        ICollection<string> warnings)
    {
        foreach (var segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index < 0)
            {
                warnings.Add($"Ignored malformed hotkey segment: `{segment}`.");
                continue;
            }

            var key = segment[..index].Trim();
            var value = segment[(index + 1)..].Trim();
            var canonicalKey = CanonicalizeActionName(key);
            if (canonicalKey is null)
            {
                warnings.Add($"Ignored unknown hotkey key: `{key}`.");
                continue;
            }

            parsed[canonicalKey] = value;
        }
    }

    private static void ParseLegacyJson(
        string raw,
        IDictionary<string, string?> parsed,
        ICollection<string> warnings)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Ignored malformed legacy hotkey json: {ex.Message}");
            return;
        }

        if (node is not JsonObject root)
        {
            warnings.Add("Ignored unsupported legacy hotkey json payload.");
            return;
        }

        foreach (var (rawKey, rawValue) in root)
        {
            var canonicalKey = CanonicalizeActionName(rawKey);
            if (canonicalKey is null)
            {
                warnings.Add($"Ignored unknown legacy hotkey key: `{rawKey}`.");
                continue;
            }

            if (rawValue is null)
            {
                parsed[canonicalKey] = string.Empty;
                continue;
            }

            if (rawValue is JsonValue scalar
                && scalar.TryGetValue(out string? textValue))
            {
                parsed[canonicalKey] = NormalizeDraftGesture(textValue);
                continue;
            }

            if (rawValue is not JsonObject hotkeyObject)
            {
                warnings.Add($"Ignored unsupported legacy hotkey payload for `{canonicalKey}`.");
                continue;
            }

            if (TryParseLegacyGesture(canonicalKey, hotkeyObject, warnings, out var gesture))
            {
                parsed[canonicalKey] = gesture;
            }
        }
    }

    private static bool TryParseLegacyGesture(
        string actionName,
        JsonObject hotkeyObject,
        ICollection<string> warnings,
        out string gesture)
    {
        gesture = string.Empty;
        if (!TryReadLegacyInt(hotkeyObject["Modifiers"], out var modifierMask))
        {
            modifierMask = 0;
        }

        if (hotkeyObject["Key"] is null)
        {
            gesture = string.Empty;
            return true;
        }

        var keyToken = TryReadLegacyKey(hotkeyObject["Key"]);
        if (keyToken is null)
        {
            warnings.Add($"Ignored unsupported legacy hotkey key for `{actionName}`.");
            return false;
        }

        var hotkey = new HotkeyGesture(
            Ctrl: (modifierMask & 0x2) != 0,
            Shift: (modifierMask & 0x4) != 0,
            Alt: (modifierMask & 0x1) != 0,
            Meta: (modifierMask & 0x8) != 0,
            Key: keyToken);
        if (!hotkey.HasModifier)
        {
            warnings.Add($"Ignored legacy hotkey without modifiers for `{actionName}`.");
            return false;
        }

        gesture = hotkey.ToStorageString();
        return true;
    }

    private static string ResolveGesture(
        IReadOnlyDictionary<string, string?> parsed,
        string actionName,
        string fallback,
        ICollection<string> warnings)
    {
        if (!parsed.TryGetValue(actionName, out var value))
        {
            return fallback;
        }

        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (HotkeyGestureCodec.TryNormalize(trimmed, out var normalized))
        {
            return normalized;
        }

        warnings.Add($"Ignored invalid hotkey gesture for `{actionName}`: `{trimmed}`.");
        return fallback;
    }

    private static string? CanonicalizeActionName(string? name)
    {
        if (string.Equals(name, ShowGuiHotkeyName, StringComparison.OrdinalIgnoreCase))
        {
            return ShowGuiHotkeyName;
        }

        if (string.Equals(name, LinkStartHotkeyName, StringComparison.OrdinalIgnoreCase))
        {
            return LinkStartHotkeyName;
        }

        return null;
    }

    private static bool LooksLikeLegacyJson(string raw)
    {
        foreach (var ch in raw)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return ch is '{' or '[';
            }
        }

        return false;
    }

    private static int ReadInt(UnifiedConfig config, string key)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return 0;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<string>(out var textValue)
                && int.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

    private static bool TryReadLegacyInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue scalar)
        {
            return false;
        }

        if (scalar.TryGetValue(out int intValue))
        {
            value = intValue;
            return true;
        }

        if (scalar.TryGetValue(out long longValue)
            && longValue is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)longValue;
            return true;
        }

        if (scalar.TryGetValue(out string? stringValue)
            && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string? TryReadLegacyKey(JsonNode? node)
    {
        if (node is not JsonValue scalar)
        {
            return null;
        }

        if (scalar.TryGetValue(out int intValue)
            && LegacyWpfKeyMap.TryGetValue(intValue, out var mapped))
        {
            return mapped;
        }

        if (scalar.TryGetValue(out long longValue)
            && longValue is >= int.MinValue and <= int.MaxValue
            && LegacyWpfKeyMap.TryGetValue((int)longValue, out mapped))
        {
            return mapped;
        }

        if (!scalar.TryGetValue(out string? stringValue)
            || string.IsNullOrWhiteSpace(stringValue))
        {
            return null;
        }

        if (HotkeyGestureCodec.TryParse($"Ctrl+{stringValue.Trim()}", out var parsed))
        {
            return parsed.Key;
        }

        return null;
    }

    private static IReadOnlyDictionary<int, string> BuildLegacyWpfKeyMap()
    {
        var map = new Dictionary<int, string>
        {
            [2] = "Backspace",
            [3] = "Tab",
            [6] = "Enter",
            [13] = "Escape",
            [18] = "Space",
            [19] = "PageUp",
            [20] = "PageDown",
            [21] = "End",
            [22] = "Home",
            [23] = "Left",
            [24] = "Up",
            [25] = "Right",
            [26] = "Down",
            [31] = "Insert",
            [32] = "Delete",
            [85] = "Plus",
            [87] = "Minus",
        };

        for (var i = 0; i <= 9; i++)
        {
            map[34 + i] = i.ToString(CultureInfo.InvariantCulture);
            map[74 + i] = i.ToString(CultureInfo.InvariantCulture);
        }

        for (var i = 0; i < 26; i++)
        {
            map[44 + i] = ((char)('A' + i)).ToString();
        }

        for (var i = 1; i <= 24; i++)
        {
            map[89 + i] = $"F{i}";
        }

        return map;
    }
}
