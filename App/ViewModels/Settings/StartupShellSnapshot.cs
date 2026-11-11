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
    bool HideCloseButton,
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

    // Legacy WPF theme key (GUI.DarkMode). Kept out of Compat ConfigurationKeys on purpose: the
    // frozen baseline contract (BaselineCoverageTests) fixes the public legacy key set at 300.
    internal const string LegacyDarkModeKey = "GUI.DarkMode";

    // Frozen WPF default (BackgroundSettingsUserControlModel.cs: GetGlobalValue fallback).
    internal const string DefaultBackgroundImagePath = "background/background.png";
    private const string DefaultTheme = "Light";
    private const string DefaultLanguage = UiLanguageCatalog.DefaultLanguage;
    private const string DefaultBackgroundStretchMode = "Fill";
    private const string DefaultLogItemDateFormat = "HH:mm:ss";
    private const string DeveloperModeConfigKey = "GUI.DeveloperMode";
    private const int BackgroundOpacityMin = 0;
    private const int BackgroundOpacityMax = 100;
    private const int BackgroundBlurMin = 0;
    private const int BackgroundBlurMax = 50;
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
        HideCloseButton: false,
        UiScalePercent: DefaultUiScalePercent,
        UseSoftwareRendering: false,
        LogItemDateFormatString: DefaultLogItemDateFormat,
        BackgroundImagePath: DefaultBackgroundImagePath,
        BackgroundOpacity: 50,
        BackgroundBlur: 5,
        BackgroundStretchMode: DefaultBackgroundStretchMode,
        HotkeyShowGui: HotkeyConfigurationCodec.DefaultHotkeyShowGui,
        HotkeyLinkStart: HotkeyConfigurationCodec.DefaultHotkeyLinkStart);

    public static StartupShellSnapshot FromConfig(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(config);

        var rawHotkeys = ReadGlobalString(config, ConfigurationKeys.HotKeys, string.Empty);
        var parsedHotkeys = HotkeyConfigurationCodec.Parse(rawHotkeys);
        var useTray = ReadGlobalBool(config, ConfigurationKeys.UseTray, true);

        return new StartupShellSnapshot(
            Theme: NormalizeTheme(ReadThemeModeWithLegacyFallback(config)),
            Language: NormalizeLanguage(ReadGlobalString(config, ConfigurationKeys.Localization, DefaultLanguage)),
            UseTray: useTray,
            MinimizeToTray: useTray && ReadGlobalBool(config, ConfigurationKeys.MinimizeToTray, false),
            DeveloperModeEnabled: ReadGlobalBool(config, DeveloperModeConfigKey, false),
            WindowTitleScrollable: ReadGlobalBool(config, ConfigurationKeys.WindowTitleScrollable, false),
            HideCloseButton: ReadGlobalBool(config, ConfigurationKeys.HideCloseButton, false),
            UiScalePercent: Math.Clamp(
                ReadGlobalInt(config, ConfigurationKeys.UiScalePercent, DefaultUiScalePercent),
                UiScalePercentMin,
                UiScalePercentMax),
            UseSoftwareRendering: ReadGlobalBool(config, ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering, false),
            LogItemDateFormatString: NormalizeLogItemDateFormat(
                ReadGlobalString(config, ConfigurationKeys.LogItemDateFormat, DefaultLogItemDateFormat)),
            BackgroundImagePath: ReadBackgroundImagePath(config),
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

    private static string ReadThemeModeWithLegacyFallback(UnifiedConfig config)
    {
        // WPF parity: legacy configs only carry GUI.DarkMode (Light/Dark/SyncWithOs). Consume it when the
        // Avalonia-specific Theme.Mode key has never been written.
        if (config.GlobalValues.ContainsKey(ThemeModeKey))
        {
            return ReadGlobalString(config, ThemeModeKey, DefaultTheme);
        }

        if (config.GlobalValues.ContainsKey(LegacyDarkModeKey))
        {
            return ReadGlobalString(config, LegacyDarkModeKey, DefaultTheme);
        }

        return DefaultTheme;
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

    private static string ReadBackgroundImagePath(UnifiedConfig config)
    {
        // WPF parity (BackgroundSettingsUserControlModel): the conventional default path applies
        // only when the key was never written; an explicitly cleared (empty) stored value stays
        // empty so users can remove the background.
        if (!config.GlobalValues.TryGetValue(ConfigurationKeys.BackgroundImagePath, out var node) || node is null)
        {
            return DefaultBackgroundImagePath;
        }

        return NormalizeBackgroundPath(
            node is JsonValue value && value.TryGetValue(out string? text) ? text : node.ToString());
    }

    private static string NormalizeLogItemDateFormat(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultLogItemDateFormat : value.Trim();
    }
}
