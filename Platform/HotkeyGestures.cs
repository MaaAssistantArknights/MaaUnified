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

        if (IsModifierKey(keyName))
        {
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Pending);
        }

        // WPF cleared the editor for every non-modifier key pressed without a
        // modifier; that clear runs before the WPF exclusion list, so even the
        // excluded Apps (Menu) key clears the binding when pressed unmodified.
        // Keep that behavior even for keys we do not expose as a portable
        // binding token.
        if (normalizedModifiers == KeyModifiers.None)
        {
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Cleared);
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
            token = $"NumPad{keyName[6]}";
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
            "Pause" => SetToken("Pause", out token),
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
            "Add" => SetToken("Add", out token),
            "OemPlus" => SetToken("OemPlus", out token),
            "Subtract" => SetToken("Subtract", out token),
            "OemMinus" => SetToken("OemMinus", out token),
            "Multiply" => SetToken("Multiply", out token),
            "Divide" => SetToken("Divide", out token),
            "Decimal" => SetToken("Decimal", out token),
            "Separator" => SetToken("Separator", out token),
            "OemComma" => SetToken("OemComma", out token),
            "OemPeriod" => SetToken("OemPeriod", out token),
            "OemQuestion" => SetToken("OemQuestion", out token),
            "OemTilde" => SetToken("OemTilde", out token),
            "OemOpenBrackets" => SetToken("OemOpenBrackets", out token),
            "OemPipe" => SetToken("OemPipe", out token),
            "OemQuotes" => SetToken("OemQuotes", out token),
            "Oem102" => SetToken("Oem102", out token),
            // Avalonia reports duplicated OEM enum values under these names
            // (e.g. Key.Oem1.ToString() is "OemSemicolon"), and legacy WPF
            // configs may store the WPF Oem2-Oem7 names.
            "Oem1" => SetToken("Oem1", out token),
            "OemSemicolon" => SetToken("Oem1", out token),
            "Oem2" => SetToken("OemQuestion", out token),
            "Oem3" => SetToken("OemTilde", out token),
            "Oem4" => SetToken("OemOpenBrackets", out token),
            "Oem5" => SetToken("OemPipe", out token),
            "Oem6" => SetToken("Oem6", out token),
            "OemCloseBrackets" => SetToken("Oem6", out token),
            "Oem7" => SetToken("OemQuotes", out token),
            "OemBackslash" => SetToken("Oem102", out token),
            "Cancel" => SetToken("Cancel", out token),
            "CapsLock" => SetToken("CapsLock", out token),
            "Capital" => SetToken("CapsLock", out token),
            "PrintScreen" => SetToken("PrintScreen", out token),
            "Snapshot" => SetToken("PrintScreen", out token),
            "NumLock" => SetToken("NumLock", out token),
            "Scroll" => SetToken("ScrollLock", out token),
            "ScrollLock" => SetToken("ScrollLock", out token),
            "Sleep" => SetToken("Sleep", out token),
            "Help" => SetToken("Help", out token),
            "BrowserBack" => SetToken("BrowserBack", out token),
            "BrowserForward" => SetToken("BrowserForward", out token),
            "BrowserRefresh" => SetToken("BrowserRefresh", out token),
            "BrowserStop" => SetToken("BrowserStop", out token),
            "BrowserSearch" => SetToken("BrowserSearch", out token),
            "BrowserFavorites" => SetToken("BrowserFavorites", out token),
            "BrowserHome" => SetToken("BrowserHome", out token),
            "VolumeMute" => SetToken("VolumeMute", out token),
            "VolumeDown" => SetToken("VolumeDown", out token),
            "VolumeUp" => SetToken("VolumeUp", out token),
            "MediaNextTrack" => SetToken("MediaNextTrack", out token),
            "MediaPreviousTrack" => SetToken("MediaPreviousTrack", out token),
            "MediaStop" => SetToken("MediaStop", out token),
            "MediaPlayPause" => SetToken("MediaPlayPause", out token),
            "LaunchMail" => SetToken("LaunchMail", out token),
            "SelectMedia" => SetToken("SelectMedia", out token),
            "LaunchApplication1" => SetToken("LaunchApplication1", out token),
            "LaunchApplication2" => SetToken("LaunchApplication2", out token),
            _ => false,
        };
    }

    private static string? CanonicalizeKeyToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length == 7
            && trimmed[6] is >= '0' and <= '9')
        {
            return $"NumPad{trimmed[6]}";
        }

        if (TryMapAvaloniaKeyToToken(trimmed, out var mapped))
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
            "PAUSE" => "Pause",
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
            "ADD" => "Add",
            "OEMPLUS" => "OemPlus",
            "MINUS" => "OemMinus",
            "SUBTRACT" => "Subtract",
            "OEMMINUS" => "OemMinus",
            "MULTIPLY" => "Multiply",
            "DIVIDE" => "Divide",
            "DECIMAL" => "Decimal",
            "SEPARATOR" => "Separator",
            "OEMCOMMA" => "OemComma",
            "OEMPERIOD" => "OemPeriod",
            "OEM1" => "Oem1",
            "OEMQUESTION" or "OEM2" => "OemQuestion",
            "OEMTILDE" or "OEM3" => "OemTilde",
            "OEMOPENBRACKETS" or "OEM4" => "OemOpenBrackets",
            "OEMPIPE" or "OEM5" => "OemPipe",
            "OEM6" => "Oem6",
            "OEMCLOSEBRACKETS" => "Oem6",
            "OEMQUOTES" or "OEM7" => "OemQuotes",
            "OEM102" => "Oem102",
            "OEMSEMICOLON" => "Oem1",
            "OEMBACKSLASH" => "Oem102",
            "CANCEL" => "Cancel",
            "CAPSLOCK" or "CAPITAL" => "CapsLock",
            "PRINTSCREEN" or "SNAPSHOT" => "PrintScreen",
            "NUMLOCK" => "NumLock",
            "SCROLL" or "SCROLLLOCK" => "ScrollLock",
            "SLEEP" => "Sleep",
            "HELP" => "Help",
            "BROWSERBACK" => "BrowserBack",
            "BROWSERFORWARD" => "BrowserForward",
            "BROWSERREFRESH" => "BrowserRefresh",
            "BROWSERSTOP" => "BrowserStop",
            "BROWSERSEARCH" => "BrowserSearch",
            "BROWSERFAVORITES" => "BrowserFavorites",
            "BROWSERHOME" => "BrowserHome",
            "VOLUMEMUTE" => "VolumeMute",
            "VOLUMEDOWN" => "VolumeDown",
            "VOLUMEUP" => "VolumeUp",
            "MEDIANEXTTRACK" or "MEDIANEXT" => "MediaNextTrack",
            "MEDIAPREVIOUSTRACK" or "MEDIAPREVIOUS" => "MediaPreviousTrack",
            "MEDIASTOP" => "MediaStop",
            "MEDIAPLAYPAUSE" or "MEDIAPLAY" => "MediaPlayPause",
            "LAUNCHMAIL" => "LaunchMail",
            "SELECTMEDIA" => "SelectMedia",
            "LAUNCHAPPLICATION1" => "LaunchApplication1",
            "LAUNCHAPPLICATION2" => "LaunchApplication2",
            _ => null,
        };
    }

    // WPF accepts every non-modifier key in its hotkey editor, but SharpHook is
    // the sole trigger path and has no key code for a subset of the WPF vocabulary
    // (Select/Print/Execute, AbntC1/AbntC2, Oem8, and the IME/Dbe/Attn/CrSel/ExSel/
    // EraseEof/Play/Zoom/NoName/Pa1/DeadCharProcessed keys). Those keys stay
    // deliberately unsupported: capture rejects them with an explicit message and
    // legacy migration leaves the action unbound with a warning, instead of
    // silently binding a gesture that can never fire or substituting the default
    // chord.
    public static bool TryGetWindowsVirtualKey(string? token, out uint virtualKey)
    {
        virtualKey = 0;
        var canonical = CanonicalizeKeyToken(token ?? string.Empty);
        if (canonical is null)
        {
            return false;
        }

        if (canonical.Length == 1 && canonical[0] is >= 'A' and <= 'Z')
        {
            virtualKey = canonical[0];
            return true;
        }

        if (canonical.Length == 1 && canonical[0] is >= '0' and <= '9')
        {
            virtualKey = canonical[0];
            return true;
        }

        if (canonical.StartsWith("F", StringComparison.Ordinal)
            && int.TryParse(canonical.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        if (canonical.StartsWith("NumPad", StringComparison.Ordinal)
            && canonical.Length == 7
            && canonical[6] is >= '0' and <= '9')
        {
            virtualKey = (uint)(0x60 + (canonical[6] - '0'));
            return true;
        }

        return WindowsVirtualKeys.TryGetValue(canonical, out virtualKey);
    }

    private static readonly IReadOnlyDictionary<string, uint> WindowsVirtualKeys = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Pause"] = 0x13,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Plus"] = 0xBB,
        ["Add"] = 0x6B,
        ["OemPlus"] = 0xBB,
        ["Subtract"] = 0x6D,
        ["OemMinus"] = 0xBD,
        ["Multiply"] = 0x6A,
        ["Divide"] = 0x6F,
        ["Decimal"] = 0x6E,
        ["Separator"] = 0x6C,
        ["OemComma"] = 0xBC,
        ["OemPeriod"] = 0xBE,
        ["Oem1"] = 0xBA,
        ["OemQuestion"] = 0xBF,
        ["OemTilde"] = 0xC0,
        ["OemOpenBrackets"] = 0xDB,
        ["OemPipe"] = 0xDC,
        ["Oem6"] = 0xDD,
        ["OemQuotes"] = 0xDE,
        ["Oem102"] = 0xE2,
        ["Cancel"] = 0x03,
        ["CapsLock"] = 0x14,
        ["PrintScreen"] = 0x2C,
        ["Help"] = 0x2F,
        ["Sleep"] = 0x5F,
        ["NumLock"] = 0x90,
        ["ScrollLock"] = 0x91,
        ["BrowserBack"] = 0xA6,
        ["BrowserForward"] = 0xA7,
        ["BrowserRefresh"] = 0xA8,
        ["BrowserStop"] = 0xA9,
        ["BrowserSearch"] = 0xAA,
        ["BrowserFavorites"] = 0xAB,
        ["BrowserHome"] = 0xAC,
        ["VolumeMute"] = 0xAD,
        ["VolumeDown"] = 0xAE,
        ["VolumeUp"] = 0xAF,
        ["MediaNextTrack"] = 0xB0,
        ["MediaPreviousTrack"] = 0xB1,
        ["MediaStop"] = 0xB2,
        ["MediaPlayPause"] = 0xB3,
        ["LaunchMail"] = 0xB4,
        ["SelectMedia"] = 0xB5,
        ["LaunchApplication1"] = 0xB6,
        ["LaunchApplication2"] = 0xB7,
    };

    private static bool SetToken(string value, out string token)
    {
        token = value;
        return true;
    }
}
