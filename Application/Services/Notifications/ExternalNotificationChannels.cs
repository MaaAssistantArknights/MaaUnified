using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services.Notifications;

/// <summary>
/// One configured external notification channel instance. WPF dev-v2 (f9c14e0be1) stores these as
/// an array of typed configs so the same provider can appear several times; MAAUnified still reads
/// the flat keys but models the result as instances so a legacy ｢Discord Webhook｣ entry and a
/// ｢Custom Webhook｣ entry can address two different endpoints at once.
/// </summary>
/// <param name="Provider">Canonical provider name.</param>
/// <param name="Parameters">Provider parameters as passed to the feature service.</param>
public sealed record ExternalNotificationChannel(
    string Provider,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Reads the enabled channels out of the flat external-notification keys and merges the removed
/// ｢Discord Webhook｣ channel into Custom Webhook, matching the dev-v2 migration (f9c14e0be1,
/// ConfigConverter): the old webhook URL becomes a Custom Webhook channel whose body is
/// <c>{"content": "{content}"}</c>.
/// </summary>
public static class ExternalNotificationChannels
{
    public const string DefaultCustomWebhookBody = "{\"content\": \"{content}\"}";

    private static readonly IReadOnlyDictionary<string, string> ProviderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SMTP"] = "Smtp",
            ["Smtp"] = "Smtp",
            ["ServerChan"] = "ServerChan",
            ["Bark"] = "Bark",
            ["Discord"] = "Discord",
            ["Discord Webhook"] = "DiscordWebhook",
            ["DiscordWebhook"] = "DiscordWebhook",
            ["DingTalk"] = "DingTalk",
            ["Telegram"] = "Telegram",
            ["Qmsg"] = "Qmsg",
            ["Gotify"] = "Gotify",
            ["Custom Webhook"] = "CustomWebhook",
            ["CustomWebhook"] = "CustomWebhook",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ConfigKeyMap =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationSmtpServer,
                ["port"] = ConfigurationKeys.ExternalNotificationSmtpPort,
                ["user"] = ConfigurationKeys.ExternalNotificationSmtpUser,
                ["password"] = ConfigurationKeys.ExternalNotificationSmtpPassword,
                ["useSsl"] = ConfigurationKeys.ExternalNotificationSmtpUseSsl,
                ["requiresAuthentication"] = ConfigurationKeys.ExternalNotificationSmtpRequiresAuthentication,
                ["from"] = ConfigurationKeys.ExternalNotificationSmtpFrom,
                ["to"] = ConfigurationKeys.ExternalNotificationSmtpTo,
            },
            ["ServerChan"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = ConfigurationKeys.ExternalNotificationServerChanSendKey,
            },
            ["Bark"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = ConfigurationKeys.ExternalNotificationBarkSendKey,
                ["server"] = ConfigurationKeys.ExternalNotificationBarkServer,
            },
            ["Discord"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationDiscordBotToken,
                ["userId"] = ConfigurationKeys.ExternalNotificationDiscordUserId,
            },
            ["DiscordWebhook"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["webhookUrl"] = ConfigurationKeys.ExternalNotificationDiscordWebhookUrl,
            },
            ["DingTalk"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["accessToken"] = ConfigurationKeys.ExternalNotificationDingTalkAccessToken,
                ["secret"] = ConfigurationKeys.ExternalNotificationDingTalkSecret,
            },
            ["Telegram"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationTelegramBotToken,
                ["chatId"] = ConfigurationKeys.ExternalNotificationTelegramChatId,
                ["topicId"] = ConfigurationKeys.ExternalNotificationTelegramTopicId,
            },
            ["Qmsg"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationQmsgServer,
                ["key"] = ConfigurationKeys.ExternalNotificationQmsgKey,
                ["user"] = ConfigurationKeys.ExternalNotificationQmsgUser,
                ["bot"] = ConfigurationKeys.ExternalNotificationQmsgBot,
            },
            ["Gotify"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationGotifyServer,
                ["token"] = ConfigurationKeys.ExternalNotificationGotifyToken,
            },
            ["CustomWebhook"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = ConfigurationKeys.ExternalNotificationCustomWebhookUrl,
                ["headers"] = SensitiveSettingKeyRegistry.ExternalNotificationCustomWebhookHeaders,
                ["body"] = ConfigurationKeys.ExternalNotificationCustomWebhookBody,
            },
        };

    /// <summary>
    /// Resolves the enabled channel instances in configuration order. Duplicate providers are kept
    /// because the legacy ｢Discord Webhook｣ entry becomes an additional Custom Webhook channel.
    /// </summary>
    public static IReadOnlyList<ExternalNotificationChannel> ResolveEnabled(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rawValue = NotificationConfigReader.ReadString(
            config,
            ConfigurationKeys.ExternalNotificationEnabled,
            string.Empty);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        var channels = new List<ExternalNotificationChannel>();
        foreach (var segment in rawValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ProviderAliases.TryGetValue(segment, out var provider))
            {
                continue;
            }

            channels.Add(BuildChannel(config, provider));
        }

        return channels;
    }

    public static string BuildParameterText(ExternalNotificationChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var lines = new List<string>(channel.Parameters.Count);
        foreach (var (key, value) in channel.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{key}={value}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ExternalNotificationChannel BuildChannel(UnifiedConfig config, string provider)
    {
        if (string.Equals(provider, "DiscordWebhook", StringComparison.OrdinalIgnoreCase))
        {
            // dev-v2 removed the channel and turns the saved URL into a Custom Webhook channel.
            var webhookUrl = ReadValue(config, ConfigurationKeys.ExternalNotificationDiscordWebhookUrl);
            return new ExternalNotificationChannel(
                "CustomWebhook",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["url"] = webhookUrl,
                    ["body"] = DefaultCustomWebhookBody,
                });
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameterKey, configKey) in ConfigKeyMap[provider])
        {
            var value = ReadValue(config, configKey);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[parameterKey] = value;
            }
        }

        if (string.Equals(provider, "Bark", StringComparison.OrdinalIgnoreCase)
            && !parameters.ContainsKey("server"))
        {
            parameters["server"] = "https://api.day.app";
        }

        return new ExternalNotificationChannel(provider, parameters);
    }

    private static string ReadValue(UnifiedConfig config, string key)
        => NotificationConfigReader.ReadString(config, key, string.Empty).Trim();
}
