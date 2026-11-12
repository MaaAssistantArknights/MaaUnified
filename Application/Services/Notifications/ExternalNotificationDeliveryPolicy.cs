using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services.Notifications;

/// <summary>
/// External notification delivery options. WPF moved them into
/// <c>Gui.ExternalNotification.Delivery</c> (f9c14e0be1); MAAUnified keeps them as flat profile
/// keys so the existing config tree and importer do not need another migration, but reads the
/// legacy <c>EnableDetails</c> flag as the fallback for the log packaging options.
/// </summary>
/// <param name="Enable">True when at least one channel is enabled.</param>
/// <param name="SendWhenComplete">Send on task completion.</param>
/// <param name="SendWhenError">Send on task failure.</param>
/// <param name="SendWhenStalled">Send on the stall reminder.</param>
/// <param name="SendBeforeScheduledStart">Send before a scheduled start.</param>
/// <param name="SendAfterLogCount">Send after <paramref name="NewLogCount"/> new log lines.</param>
/// <param name="NewLogCount">Log-line threshold for <paramref name="SendAfterLogCount"/>.</param>
/// <param name="SendWhenContentMatches">Send when a log line matches <paramref name="Whitelist"/>.</param>
/// <param name="Whitelist">Whitelist regular expressions, one per line.</param>
/// <param name="IncludePreviousLogs">Attach preceding log lines to the notification body.</param>
/// <param name="MaxEntries">Maximum number of attached log lines.</param>
/// <param name="TimeMinutes">Only attach log lines younger than this; 0 disables the window.</param>
/// <param name="FilterPreviousLogs">Drop attached lines that match <paramref name="Blacklist"/>.</param>
/// <param name="Blacklist">Blacklist regular expressions, one per line.</param>
public sealed record ExternalNotificationDeliveryPolicy(
    bool Enable,
    bool SendWhenComplete,
    bool SendWhenError,
    bool SendWhenStalled,
    bool SendBeforeScheduledStart,
    bool SendAfterLogCount,
    int NewLogCount,
    bool SendWhenContentMatches,
    string Whitelist,
    bool IncludePreviousLogs,
    int MaxEntries,
    int TimeMinutes,
    bool FilterPreviousLogs,
    string Blacklist)
{
    public const int DefaultNewLogCount = 10;

    /// <summary>WPF f9c14e0be1 keeps two lines by default.</summary>
    public const int DefaultMaxEntries = 2;

    /// <summary>WPF maps the legacy ｢include details｣ flag to 100 lines.</summary>
    public const int DetailedMaxEntries = 100;

    public const int DefaultTimeMinutes = 60;

    public static ExternalNotificationDeliveryPolicy FromConfig(UnifiedConfig config)
    {
        var enabledProviders = NotificationConfigReader
            .ReadString(config, ConfigurationKeys.ExternalNotificationEnabled, string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var includeDetailsLegacy = NotificationConfigReader.ReadBool(
            config,
            ConfigurationKeys.ExternalNotificationEnableDetails,
            false);
        var includePreviousLogs = NotificationConfigReader.ReadBool(
            config,
            ConfigurationKeys.ExternalNotificationIncludePreviousLogs,
            includeDetailsLegacy);
        var maxEntries = NotificationConfigReader.ReadInt(
            config,
            ConfigurationKeys.ExternalNotificationMaxEntries,
            includeDetailsLegacy ? DetailedMaxEntries : DefaultMaxEntries);
        var newLogCount = NotificationConfigReader.ReadInt(
            config,
            ConfigurationKeys.ExternalNotificationNewLogCount,
            DefaultNewLogCount);
        var timeMinutes = NotificationConfigReader.ReadInt(
            config,
            ConfigurationKeys.ExternalNotificationTimeMinutes,
            DefaultTimeMinutes);

        return new ExternalNotificationDeliveryPolicy(
            Enable: enabledProviders.Length > 0,
            SendWhenComplete: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendWhenComplete,
                true),
            SendWhenError: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendWhenError,
                true),
            SendWhenStalled: ReadSendWhenStalled(config),
            SendBeforeScheduledStart: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendBeforeScheduledStart,
                false),
            SendAfterLogCount: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendAfterLogCount,
                false),
            NewLogCount: Math.Clamp(newLogCount, 1, 10000),
            SendWhenContentMatches: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendWhenContentMatches,
                false),
            Whitelist: NotificationConfigReader.ReadString(
                config,
                ConfigurationKeys.ExternalNotificationWhitelist,
                string.Empty),
            IncludePreviousLogs: includePreviousLogs,
            MaxEntries: Math.Clamp(maxEntries, 0, 10000),
            TimeMinutes: Math.Clamp(timeMinutes, 0, 10080),
            FilterPreviousLogs: NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationFilterPreviousLogs,
                false),
            Blacklist: NotificationConfigReader.ReadString(
                config,
                ConfigurationKeys.ExternalNotificationBlacklist,
                string.Empty));
    }

    private static bool ReadSendWhenStalled(UnifiedConfig config)
    {
        if (NotificationConfigReader.HasValue(config, ConfigurationKeys.ExternalNotificationSendWhenStalled))
        {
            return NotificationConfigReader.ReadBool(
                config,
                ConfigurationKeys.ExternalNotificationSendWhenStalled,
                true);
        }

        return NotificationConfigReader.ReadBool(
            config,
            ConfigurationKeys.ExternalNotificationSendWhenTimeout,
            true);
    }
}

internal static class NotificationConfigReader
{
    public static bool HasValue(UnifiedConfig config, string key) => TryReadNode(config, key, out var node)
        && node is not null;

    public static string ReadString(UnifiedConfig config, string key, string fallback)
    {
        if (TryReadNode(config, key, out var node) && node is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (value.TryGetValue(out bool boolean))
            {
                return boolean ? bool.TrueString : bool.FalseString;
            }

            if (value.TryGetValue(out int number))
            {
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return fallback;
    }

    public static bool ReadBool(UnifiedConfig config, string key, bool fallback)
    {
        if (TryReadNode(config, key, out var node) && node is JsonValue value)
        {
            if (value.TryGetValue(out bool parsed))
            {
                return parsed;
            }

            if (value.TryGetValue(out string? text) && bool.TryParse(text, out var parsedText))
            {
                return parsedText;
            }
        }

        return fallback;
    }

    public static int ReadInt(UnifiedConfig config, string key, int fallback)
    {
        if (TryReadNode(config, key, out var node) && node is JsonValue value)
        {
            if (value.TryGetValue(out int parsed))
            {
                return parsed;
            }

            if (value.TryGetValue(out string? text)
                && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedText))
            {
                return parsedText;
            }
        }

        return fallback;
    }

    // WPF ConfigurationHelper.GetValue is profile-first with a global fallback; match that so a
    // legacy value kept in the global section still drives behaviour.
    public static bool TryReadNode(UnifiedConfig config, string key, out JsonNode? node)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out node)
            && node is not null)
        {
            return true;
        }

        return config.GlobalValues.TryGetValue(key, out node) && node is not null;
    }
}
