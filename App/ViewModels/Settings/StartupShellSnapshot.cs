using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.Settings;

public sealed record StartupShellSnapshot(
    string Theme,
    string Language,
    bool UseTray,
    bool MinimizeToTray,
    bool DeveloperModeEnabled,
    bool WindowTitleScrollable,
    int UiScalePercent,
    bool UseSoftwareRendering,
    string LogItemDateFormatString,
    string BackgroundImagePath,
    int BackgroundOpacity,
    int BackgroundBlur,
    string BackgroundStretchMode,
    string HotkeyShowGui,
    string HotkeyLinkStart)
{
    private const string ThemeModeKey = "Theme.Mode";
    private const string DefaultTheme = "Light";
    private const string DefaultLanguage = UiLanguageCatalog.DefaultLanguage;
    private const string DefaultBackgroundStretchMode = "Fill";
    private const string DefaultLogItemDateFormat = "HH:mm:ss";
    private const string DeveloperModeConfigKey = "GUI.DeveloperMode";
    private const string DefaultHotkeyShowGui = HotkeyConfigurationCodec.DefaultHotkeyShowGui;
    private const string DefaultHotkeyLinkStart = HotkeyConfigurationCodec.DefaultHotkeyLinkStart;
    private const int BackgroundOpacityMin = 0;
    private const int BackgroundOpacityMax = 100;
    private const int BackgroundBlurMin = 0;
    private const int BackgroundBlurMax = 80;
    private const int DefaultUiScalePercent = 100;
    private const int UiScalePercentMin = 70;
    private const int UiScalePercentMax = 140;

    public static StartupShellSnapshot Default { get; } = new(
        Theme: DefaultTheme,
        Language: DefaultLanguage,
        UseTray: true,
        MinimizeToTray: false,
        DeveloperModeEnabled: false,
        WindowTitleScrollable: false,
        UiScalePercent: DefaultUiScalePercent,
        UseSoftwareRendering: false,
        LogItemDateFormatString: DefaultLogItemDateFormat,
        BackgroundImagePath: string.Empty,
        BackgroundOpacity: 45,
        BackgroundBlur: 12,
        BackgroundStretchMode: DefaultBackgroundStretchMode,
        HotkeyShowGui: DefaultHotkeyShowGui,
        HotkeyLinkStart: DefaultHotkeyLinkStart);

    public static StartupShellSnapshot FromConfig(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rawHotkeys = ReadGlobalString(config, ConfigurationKeys.HotKeys, string.Empty);
        var parsedHotkeys = HotkeyConfigurationCodec.Parse(rawHotkeys);
        var useTray = ReadGlobalBool(config, ConfigurationKeys.UseTray, true);

        return new StartupShellSnapshot(
            Theme: NormalizeTheme(ReadGlobalString(config, ThemeModeKey, DefaultTheme)),
            Language: NormalizeLanguage(ReadGlobalString(config, ConfigurationKeys.Localization, DefaultLanguage)),
            UseTray: useTray,
            MinimizeToTray: useTray && ReadGlobalBool(config, ConfigurationKeys.MinimizeToTray, false),
            DeveloperModeEnabled: ReadGlobalBool(config, DeveloperModeConfigKey, false),
            WindowTitleScrollable: ReadGlobalBool(config, ConfigurationKeys.WindowTitleScrollable, false),
            UiScalePercent: Math.Clamp(
                ReadGlobalInt(config, ConfigurationKeys.UiScalePercent, DefaultUiScalePercent),
                UiScalePercentMin,
                UiScalePercentMax),
            UseSoftwareRendering: ReadGlobalBool(config, ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering, false),
            LogItemDateFormatString: NormalizeLogItemDateFormat(
                ReadGlobalString(config, ConfigurationKeys.LogItemDateFormat, DefaultLogItemDateFormat)),
            BackgroundImagePath: NormalizeBackgroundPath(ReadGlobalString(config, ConfigurationKeys.BackgroundImagePath, string.Empty)),
            BackgroundOpacity: Math.Clamp(
                ReadGlobalInt(config, ConfigurationKeys.BackgroundOpacity, Default.BackgroundOpacity),
                BackgroundOpacityMin,
                BackgroundOpacityMax),
            BackgroundBlur: Math.Clamp(
                ReadGlobalInt(config, ConfigurationKeys.BackgroundBlurEffectRadius, Default.BackgroundBlur),
                BackgroundBlurMin,
                BackgroundBlurMax),
            BackgroundStretchMode: NormalizeBackgroundStretchMode(
                ReadGlobalString(config, ConfigurationKeys.BackgroundImageStretchMode, DefaultBackgroundStretchMode)),
            HotkeyShowGui: parsedHotkeys.ShowGui,
            HotkeyLinkStart: parsedHotkeys.LinkStart);
    }

    private static string ReadGlobalString(UnifiedConfig config, string key, string fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToString(),
        };
    }

    private static int ReadGlobalInt(UnifiedConfig config, string key, int fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<string>(out var textValue)
                && int.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedText))
            {
                return parsedText;
            }
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue != 0;
            }

            if (value.TryGetValue<string>(out var stringValue))
            {
                if (bool.TryParse(stringValue, out var parsedBool))
                {
                    return parsedBool;
                }

                if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        return bool.TryParse(node.ToString(), out var parsedFallback) ? parsedFallback : fallback;
    }

    private static string NormalizeTheme(string? value)
    {
        var normalized = value?.Trim();
        return SettingsOptionCatalog.BuildThemeOptions(UiLanguageCatalog.DefaultLanguage)
            .Select(static option => option.Value)
            .FirstOrDefault(option => string.Equals(option, normalized, StringComparison.OrdinalIgnoreCase))
            ?? DefaultTheme;
    }

    private static string NormalizeBackgroundStretchMode(string? value)
    {
        var normalized = value?.Trim();
        return SettingsOptionCatalog.BuildBackgroundStretchOptions(UiLanguageCatalog.DefaultLanguage)
            .Select(static option => option.Value)
            .FirstOrDefault(option => string.Equals(option, normalized, StringComparison.OrdinalIgnoreCase))
            ?? DefaultBackgroundStretchMode;
    }

    private static string NormalizeLanguage(string? value)
    {
        var normalized = value?.Trim();
        return SettingsOptionCatalog.BuildLanguageOptions()
            .Select(static option => option.Value)
            .FirstOrDefault(option => string.Equals(option, normalized, StringComparison.OrdinalIgnoreCase))
            ?? DefaultLanguage;
    }

    private static string NormalizeBackgroundPath(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeLogItemDateFormat(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultLogItemDateFormat : value.Trim();
    }
}
