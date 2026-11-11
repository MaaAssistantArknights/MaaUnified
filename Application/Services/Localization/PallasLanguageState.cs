using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services.Localization;

/// <summary>
/// WPF parity helpers for the Pallas easter-egg state machine
/// (GUI.Cheers / GUI.Hangover / GUI.SoberLanguage / GUI.LastBuyWineTime).
/// Frozen WPF reference: SettingsViewModel.cs (Cheers, Hangover, Sober, SoberLanguage,
/// DidYouBuyWine) and GuiSettingsUserControlModel.cs Language setter.
/// </summary>
public static class PallasLanguageState
{
    // Keep aligned with the frozen WPF wine list in SettingsViewModel.DidYouBuyWine.
    private static readonly string[] WineTokens =
        ["酒", "liquor", "drink", "wine", "beer", "술", "🍷", "🍸", "🍺", "🍻", "🥃", "🍶"];

    public static bool IsCheers(UnifiedConfig config)
        => ReadGlobalBool(config, ConfigurationKeys.Cheers, fallback: false);

    public static bool IsHangover(UnifiedConfig config)
        => ReadGlobalBool(config, ConfigurationKeys.Hangover, fallback: false);

    public static string GetSoberLanguage(UnifiedConfig config)
    {
        var raw = ReadGlobalString(config, ConfigurationKeys.SoberLanguage, string.Empty);
        return UiLanguageCatalog.IsSupported(raw)
            ? UiLanguageCatalog.Normalize(raw)
            : UiLanguageCatalog.DefaultLanguage;
    }

    /// <summary>
    /// Mirrors the frozen WPF SettingsViewModel.Sober invoked from Bootstrapper.Release: a drunk
    /// session (Cheers + pallas) is restored to the sober language on exit.
    /// </summary>
    public static bool ApplySoberOnExit(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var current = UiLanguageCatalog.Normalize(
            ReadGlobalString(config, ConfigurationKeys.Localization, UiLanguageCatalog.DefaultLanguage));
        if (!IsCheers(config)
            || !string.Equals(current, UiLanguageCatalog.PallasLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        WriteGlobalString(config, ConfigurationKeys.Localization, GetSoberLanguage(config));
        WriteGlobalBool(config, ConfigurationKeys.Hangover, true);
        WriteGlobalBool(config, ConfigurationKeys.Cheers, false);
        return true;
    }

    /// <summary>
    /// Mirrors the frozen WPF SettingsViewModel.DidYouBuyWine: true when the current profile owns a
    /// Mall task whose buy-first list contains a wine token and no wine was recorded for the
    /// current YJ date yet.
    /// </summary>
    public static bool DidYouBuyWine(UnifiedConfig config, string? clientType, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);

        var lastRaw = ReadGlobalString(config, ConfigurationKeys.LastBuyWineTime, string.Empty);
        if (TryParseWineTime(lastRaw, out var lastBought)
            && lastBought.Date == MallDailyResetHelper.GetYjDate(utcNow, clientType ?? string.Empty))
        {
            return false;
        }

        return CurrentProfileHasWineMallTask(config);
    }

    public static void MarkWineBought(UnifiedConfig config, string? clientType, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        var yjDate = MallDailyResetHelper.GetYjDate(utcNow, clientType ?? string.Empty);
        WriteGlobalString(
            config,
            ConfigurationKeys.LastBuyWineTime,
            yjDate.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static bool CurrentProfileHasWineMallTask(UnifiedConfig config)
    {
        if (!config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            return false;
        }

        foreach (var task in profile.TaskQueue)
        {
            if (!string.Equals(task.Type, "Mall", StringComparison.OrdinalIgnoreCase)
                || task.IsEnabled is false)
            {
                continue;
            }

            if (task.Params["buy_first"] is not JsonArray buyFirst)
            {
                continue;
            }

            foreach (var entry in buyFirst)
            {
                var item = entry?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(item)
                    && WineTokens.Any(token => item.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseWineTime(string raw, out DateTime value)
    {
        return DateTime.TryParseExact(
            raw.Replace('-', '/'),
            "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    private static string ReadGlobalString(UnifiedConfig config, string key, string fallback)
    {
        if (config.GlobalValues.TryGetValue(key, out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? text))
        {
            return text;
        }

        return fallback;
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue(out string? textValue)
                && bool.TryParse(textValue, out var parsedText))
            {
                return parsedText;
            }
        }

        return bool.TryParse(node.ToString(), out var parsed) ? parsed : fallback;
    }

    private static void WriteGlobalString(UnifiedConfig config, string key, string value)
        => config.GlobalValues[key] = JsonValue.Create(value);

    private static void WriteGlobalBool(UnifiedConfig config, string key, bool value)
        => config.GlobalValues[key] = JsonValue.Create(value);
}
