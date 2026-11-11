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
        var legacyAuthoritative = false;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (LooksLikeLegacyJson(raw))
            {
                legacyAuthoritative = ParseLegacyJson(raw, parsed, warnings);
            }
            else
            {
                ParseSemicolon(raw, parsed, warnings);
            }
        }

        // WPF parity (MaaHotKeyManager.GetPersistentHotKeys): the initial defaults
        // apply only when no hotkey config value was ever persisted. A well-formed
        // legacy payload is authoritative: an action it does not bind stays
        // unbound instead of being replaced by a default chord the user never
        // configured.
        return new HotkeyConfigurationSnapshot(
            ShowGui: ResolveGesture(parsed, ShowGuiHotkeyName, legacyAuthoritative ? string.Empty : DefaultHotkeyShowGui, warnings),
            LinkStart: ResolveGesture(parsed, LinkStartHotkeyName, legacyAuthoritative ? string.Empty : DefaultHotkeyLinkStart, warnings),
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

    private static bool ParseLegacyJson(
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
            return false;
        }

        if (node is not JsonObject root)
        {
            warnings.Add("Ignored unsupported legacy hotkey json payload.");
            return false;
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
            else
            {
                // WPF parity: an action explicitly present in the legacy payload but
                // not registrable here (no modifiers, or a key without a SharpHook
                // trigger code) migrates to unbound with the warning above rather
                // than being replaced by the default chord.
                parsed[canonicalKey] = string.Empty;
            }
        }

        return true;
    }

    private static bool TryParseLegacyGesture(
        string actionName,
        JsonObject hotkeyObject,
        ICollection<string> warnings,
        out string gesture)
    {
        gesture = string.Empty;
        if (!TryReadLegacyModifierMask(hotkeyObject["Modifiers"], out var modifierMask))
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
        if (string.Equals(name, ShowGuiHotkeyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "0", StringComparison.Ordinal))
        {
            return ShowGuiHotkeyName;
        }

        if (string.Equals(name, LinkStartHotkeyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "1", StringComparison.Ordinal))
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

    private static bool TryReadLegacyModifierMask(JsonNode? node, out int value)
    {
        if (TryReadLegacyInt(node, out value))
        {
            return true;
        }

        value = 0;
        if (node is not JsonValue scalar
            || !scalar.TryGetValue(out string? text)
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var rawModifier in text.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawModifier.Trim().ToLowerInvariant())
            {
                case "none":
                    break;
                case "control":
                case "ctrl":
                    value |= 0x2;
                    break;
                case "shift":
                    value |= 0x4;
                    break;
                case "alt":
                    value |= 0x1;
                    break;
                case "windows":
                case "window":
                case "win":
                case "meta":
                    value |= 0x8;
                    break;
                default:
                    value = 0;
                    return false;
            }
        }

        return true;
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

        var trimmed = stringValue.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
            && LegacyWpfKeyMap.TryGetValue(parsedValue, out mapped))
        {
            return mapped;
        }

        if (HotkeyGestureCodec.TryParse($"Ctrl+{trimmed}", out var parsed))
        {
            return parsed.Key;
        }

        return null;
    }

    // Numeric keys in legacy WPF payloads are System.Windows.Input.Key enum
    // values (MaaHotKey.Key). The frozen WPF build targets net10.0-windows and
    // Newtonsoft.Json serializes the Key enum as its numeric value. The net10
    // WindowsBase Key enum is compact above 121 (BrowserBack=122 .. Oem102=154,
    // ImeProcessed=155, System=156) and Avalonia.Key 11.3.14 uses the exact same
    // numbering, so this table must use those values.
    private static IReadOnlyDictionary<int, string> BuildLegacyWpfKeyMap()
    {
        var map = new Dictionary<int, string>
        {
            [1] = "Cancel",
            [2] = "Backspace",
            [3] = "Tab",
            [6] = "Enter",
            [7] = "Pause",
            [8] = "CapsLock",
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
            [30] = "PrintScreen",
            [31] = "Insert",
            [32] = "Delete",
            [33] = "Help",
            [73] = "Sleep",
            [84] = "Multiply",
            [85] = "Add",
            [86] = "Separator",
            [87] = "Subtract",
            [88] = "Decimal",
            [89] = "Divide",
            [114] = "NumLock",
            [115] = "ScrollLock",
            [122] = "BrowserBack",
            [123] = "BrowserForward",
            [124] = "BrowserRefresh",
            [125] = "BrowserStop",
            [126] = "BrowserSearch",
            [127] = "BrowserFavorites",
            [128] = "BrowserHome",
            [129] = "VolumeMute",
            [130] = "VolumeDown",
            [131] = "VolumeUp",
            [132] = "MediaNextTrack",
            [133] = "MediaPreviousTrack",
            [134] = "MediaStop",
            [135] = "MediaPlayPause",
            [136] = "LaunchMail",
            [137] = "SelectMedia",
            [138] = "LaunchApplication1",
            [139] = "LaunchApplication2",
        };

        for (var i = 0; i <= 9; i++)
        {
            map[34 + i] = i.ToString(CultureInfo.InvariantCulture);
            map[74 + i] = $"NumPad{i}";
        }

        for (var i = 0; i < 26; i++)
        {
            map[44 + i] = ((char)('A' + i)).ToString();
        }

        for (var i = 1; i <= 24; i++)
        {
            map[89 + i] = $"F{i}";
        }

        // AbntC1 (147), AbntC2 (148), and Oem8 (153) have no portable trigger
        // token, so they intentionally fall back with a warning.
        map[140] = "Oem1";
        map[141] = "OemPlus";
        map[142] = "OemComma";
        map[143] = "OemMinus";
        map[144] = "OemPeriod";
        map[145] = "OemQuestion";
        map[146] = "OemTilde";
        map[149] = "OemOpenBrackets";
        map[150] = "OemPipe";
        map[151] = "Oem6";
        map[152] = "OemQuotes";
        map[154] = "Oem102";

        return map;
    }
}
