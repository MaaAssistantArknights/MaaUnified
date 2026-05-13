using System.Globalization;
using Avalonia.Input;

namespace MAAUnified.Platform;

public enum HotkeyDisplayPlatform
{
    Generic = 0,
    Windows = 1,
    MacOS = 2,
    Linux = 3,
}

public enum HotkeyCaptureResultKind
{
    Pending = 0,
    Cancelled = 1,
    Cleared = 2,
    Captured = 3,
    Rejected = 4,
}

public sealed record HotkeyCaptureResult(
    HotkeyCaptureResultKind Kind,
    HotkeyGesture? Gesture = null,
    string? Message = null);

public sealed record HotkeyGesture(
    bool Ctrl,
    bool Shift,
    bool Alt,
    bool Meta,
    string Key)
{
    public bool HasModifier => Ctrl || Shift || Alt || Meta;

    public string ToStorageString()
    {
        var parts = new List<string>(5);
        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Meta)
        {
            parts.Add("Meta");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    public string ToDisplayString(HotkeyDisplayPlatform platform)
    {
        if (platform == HotkeyDisplayPlatform.MacOS)
        {
            var macParts = new List<string>(5);
            if (Meta)
            {
                macParts.Add("Cmd");
            }

            if (Ctrl)
            {
                macParts.Add("Ctrl");
            }

            if (Shift)
            {
                macParts.Add("Shift");
            }

            if (Alt)
            {
                macParts.Add("Alt");
            }

            macParts.Add(Key);
            return string.Join(" + ", macParts);
        }

        var parts = new List<string>(5);
        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Meta)
        {
            parts.Add(platform == HotkeyDisplayPlatform.Windows ? "Win" : "Meta");
        }

        parts.Add(Key);
        return string.Join(" + ", parts);
    }

    public string ToChordKey()
    {
        return $"{Ctrl}:{Shift}:{Alt}:{Meta}:{Key}";
    }
}

public static class HotkeyGestureCodec
{
    public static HotkeyDisplayPlatform CurrentDisplayPlatform =>
        OperatingSystem.IsWindows()
            ? HotkeyDisplayPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? HotkeyDisplayPlatform.MacOS
                : OperatingSystem.IsLinux()
                    ? HotkeyDisplayPlatform.Linux
                    : HotkeyDisplayPlatform.Generic;

    public static bool TryNormalize(string? gesture, out string normalized)
    {
        normalized = string.Empty;
        if (!TryParse(gesture, out var parsed))
        {
            return false;
        }

        normalized = parsed.ToStorageString();
        return true;
    }

    public static bool TryParse(string? gesture, out HotkeyGesture parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var keyToken = CanonicalizeKeyToken(parts[^1]);
        if (keyToken is null)
        {
            return false;
        }

        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        bool meta = false;

        foreach (var rawModifier in parts[..^1])
        {
            switch (rawModifier.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                case "windows":
                    meta = true;
                    break;
                default:
                    return false;
            }
        }

        if (!(ctrl || shift || alt || meta))
        {
            return false;
        }

        parsed = new HotkeyGesture(ctrl, shift, alt, meta, keyToken);
        return true;
    }

    public static string NormalizeOrDefault(string? gesture, string fallback)
    {
        return TryNormalize(gesture, out var normalized)
            ? normalized
            : fallback;
    }

    public static string FormatDisplay(string? gesture, HotkeyDisplayPlatform? platform = null)
    {
        if (!TryParse(gesture, out var parsed))
        {
            return string.IsNullOrWhiteSpace(gesture)
                ? string.Empty
                : gesture.Trim();
        }

        return parsed.ToDisplayString(platform ?? CurrentDisplayPlatform);
    }

    public static HotkeyCaptureResult Capture(Key key, KeyModifiers modifiers)
    {
        var normalizedModifiers = NormalizeKeyModifiers(key, modifiers);
        var keyName = key.ToString();

        if (string.Equals(keyName, "Escape", StringComparison.Ordinal)
            && normalizedModifiers == KeyModifiers.None)
        {
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Cancelled);
        }

        if ((string.Equals(keyName, "Back", StringComparison.Ordinal)
             || string.Equals(keyName, "Delete", StringComparison.Ordinal))
            && normalizedModifiers == KeyModifiers.None)
        {
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Cleared);
        }

        if (IsModifierKey(keyName))
        {
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Pending);
        }

        if (!TryMapAvaloniaKeyToToken(keyName, out var keyToken))
        {
            return new HotkeyCaptureResult(
                HotkeyCaptureResultKind.Rejected,
                Message: $"Unsupported key `{keyName}`.");
        }

        var gesture = new HotkeyGesture(
            Ctrl: normalizedModifiers.HasFlag(KeyModifiers.Control),
            Shift: normalizedModifiers.HasFlag(KeyModifiers.Shift),
            Alt: normalizedModifiers.HasFlag(KeyModifiers.Alt),
            Meta: normalizedModifiers.HasFlag(KeyModifiers.Meta),
            Key: keyToken);

        if (!gesture.HasModifier)
        {
            return new HotkeyCaptureResult(
                HotkeyCaptureResultKind.Rejected,
                Message: "At least one modifier key is required.");
        }

        return new HotkeyCaptureResult(HotkeyCaptureResultKind.Captured, gesture);
    }

    private static KeyModifiers NormalizeKeyModifiers(Key key, KeyModifiers modifiers)
    {
        var normalized = modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        var keyName = key.ToString();
        if (string.Equals(keyName, "LWin", StringComparison.Ordinal)
            || string.Equals(keyName, "RWin", StringComparison.Ordinal))
        {
            normalized |= KeyModifiers.Meta;
        }

        return normalized;
    }

    private static bool IsModifierKey(string keyName)
    {
        return keyName is "LeftCtrl"
            or "RightCtrl"
            or "LeftShift"
            or "RightShift"
            or "LeftAlt"
            or "RightAlt"
            or "LWin"
            or "RWin"
            or "Apps"
            or "Ctrl"
            or "Shift"
            or "Alt";
    }

    private static bool TryMapAvaloniaKeyToToken(string keyName, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        if (keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]))
        {
            token = keyName.ToUpperInvariant();
            return true;
        }

        if (keyName.Length == 2
            && keyName[0] == 'D'
            && char.IsDigit(keyName[1]))
        {
            token = keyName[1].ToString();
            return true;
        }

        if (keyName.StartsWith("NumPad", StringComparison.Ordinal)
            && keyName.Length == 7
            && char.IsDigit(keyName[6]))
        {
            token = keyName[6].ToString();
            return true;
        }

        if (keyName.StartsWith("F", StringComparison.Ordinal)
            && int.TryParse(keyName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            token = $"F{functionKey}";
            return true;
        }

        return keyName switch
        {
            "Enter" => SetToken("Enter", out token),
            "Return" => SetToken("Enter", out token),
            "Tab" => SetToken("Tab", out token),
            "Space" => SetToken("Space", out token),
            "Escape" => SetToken("Escape", out token),
            "Back" => SetToken("Backspace", out token),
            "Delete" => SetToken("Delete", out token),
            "Insert" => SetToken("Insert", out token),
            "Home" => SetToken("Home", out token),
            "End" => SetToken("End", out token),
            "PageUp" => SetToken("PageUp", out token),
            "Prior" => SetToken("PageUp", out token),
            "PageDown" => SetToken("PageDown", out token),
            "Next" => SetToken("PageDown", out token),
            "Left" => SetToken("Left", out token),
            "Up" => SetToken("Up", out token),
            "Right" => SetToken("Right", out token),
            "Down" => SetToken("Down", out token),
            "Add" => SetToken("Plus", out token),
            "OemPlus" => SetToken("Plus", out token),
            "Subtract" => SetToken("Minus", out token),
            "OemMinus" => SetToken("Minus", out token),
            _ => false,
        };
    }

    private static string? CanonicalizeKeyToken(string token)
    {
        if (TryMapAvaloniaKeyToToken(token.Trim(), out var mapped))
        {
            return mapped;
        }

        var upper = token.Trim().ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z')
        {
            return upper;
        }

        if (upper.Length == 1 && upper[0] is >= '0' and <= '9')
        {
            return upper;
        }

        if (upper.StartsWith("F", StringComparison.Ordinal)
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return upper;
        }

        return upper switch
        {
            "ENTER" => "Enter",
            "TAB" => "Tab",
            "SPACE" => "Space",
            "ESC" => "Escape",
            "ESCAPE" => "Escape",
            "BACKSPACE" => "Backspace",
            "BACK" => "Backspace",
            "DELETE" => "Delete",
            "INSERT" => "Insert",
            "HOME" => "Home",
            "END" => "End",
            "PAGEUP" => "PageUp",
            "PRIOR" => "PageUp",
            "PAGEDOWN" => "PageDown",
            "NEXT" => "PageDown",
            "LEFT" => "Left",
            "UP" => "Up",
            "RIGHT" => "Right",
            "DOWN" => "Down",
            "PLUS" => "Plus",
            "MINUS" => "Minus",
            _ => null,
        };
    }

    private static bool SetToken(string value, out string token)
    {
        token = value;
        return true;
    }
}
