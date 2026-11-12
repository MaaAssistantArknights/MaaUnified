using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// WPF dev-v2 Gui.ExternalNotification.Configs 存放位置。MAAUnified 读取该数组以支持
    /// 同类型渠道多实例；写回仍是扁平键 + Enabled CSV（单实例编辑）。
    /// </summary>
    public const string ConfigsKey = "ExternalNotification.Configs";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> WpfPropertyMap =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = "Server",
                ["port"] = "Port",
                ["user"] = "User",
                ["password"] = "Password",
                ["from"] = "From",
                ["to"] = "To",
                ["useSsl"] = "UseSsl",
                ["requiresAuthentication"] = "RequiresAuthentication",
            },
            ["ServerChan"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sendKey"] = "SendKey" },
            ["Bark"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = "SendKey",
                ["server"] = "Server",
            },
            ["Discord"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = "BotToken",
                ["userId"] = "UserId",
            },
            ["DingTalk"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["accessToken"] = "AccessToken",
                ["secret"] = "Secret",
            },
            ["Telegram"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = "BotToken",
                ["chatId"] = "ChatId",
                ["topicId"] = "TopicId",
            },
            ["Qmsg"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = "Server",
                ["key"] = "Key",
                ["user"] = "User",
                ["bot"] = "Bot",
            },
            ["Gotify"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = "Server",
                ["token"] = "Token",
            },
            ["CustomWebhook"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = "Url",
                ["headers"] = "Headers",
                ["body"] = "Body",
            },
        };

    /// <summary>
    /// 尝试读取 WPF dev-v2 的 Configs 数组（多实例）。数组元素形如
    /// <c>{"$type":"Bark","SendKey":"...","Server":"..."}</c>；值保持原样（WPF 可能写入加密串，
    /// 读取端不做解密，交由发送失败暴露）。无法解析或没有已知渠道时返回 false，调用方回退扁平键。
    /// </summary>
    /// <param name="config">配置快照。</param>
    /// <param name="channels">解析出的渠道实例。</param>
    /// <returns>是否读取到至少一个已知渠道。</returns>
    public static bool TryReadConfiguredInstances(
        UnifiedConfig config,
        out IReadOnlyList<ExternalNotificationChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(config);
        channels = [];

        if (!config.GlobalValues.TryGetValue(ConfigsKey, out var node) || node is null)
        {
            return false;
        }

        JsonArray? array = node switch
        {
            JsonArray direct => direct,
            JsonValue value when value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw) => TryParseArray(raw),
            _ => null,
        };
        if (array is null)
        {
            return false;
        }

        var parsed = new List<ExternalNotificationChannel>();
        foreach (var item in array)
        {
            if (item is not JsonObject configObject)
            {
                continue;
            }

            var type = ReadStringProperty(configObject, "$type") ?? ReadStringProperty(configObject, "Provider");
            if (string.IsNullOrWhiteSpace(type)
                || !ProviderAliases.TryGetValue(type, out var provider)
                || !WpfPropertyMap.TryGetValue(provider, out var propertyMap))
            {
                continue;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (parameterKey, propertyName) in propertyMap)
            {
                var value = ReadPropertyValue(configObject, propertyName);
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

            parsed.Add(new ExternalNotificationChannel(provider, parameters));
        }

        channels = parsed;
        return parsed.Count > 0;
    }

    private static JsonArray? TryParseArray(string raw)
    {
        try
        {
            return JsonNode.Parse(raw) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStringProperty(JsonObject obj, string propertyName)
        => obj.TryGetPropertyValue(propertyName, out var node) ? node?.GetValue<string>() : null;

    private static string? ReadPropertyValue(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue value => value.ToString(),
            _ => node.ToString(),
        };
    }

    /// <summary>
    /// Resolves the enabled channel instances in configuration order. Duplicate providers are kept
    /// because the legacy ｢Discord Webhook｣ entry becomes an additional Custom Webhook channel.
    /// </summary>
    public static IReadOnlyList<ExternalNotificationChannel> ResolveEnabled(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // WPF dev-v2 多实例配置优先；扁平键作为旧配置回退。
        if (TryReadConfiguredInstances(config, out var configuredChannels))
        {
            return configuredChannels;
        }

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
