using System.Text;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Configuration;

internal static class LegacyEncryptedConfigValueConverter
{
    private const string ExternalNotificationCustomWebhookHeaders = "ExternalNotification.CustomWebhook.Headers";
    private const string GlobalConfigurationName = "Global";

    private static readonly HashSet<string> ProfileEncryptedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ConfigurationKeys.RemoteControlGetTaskEndpointUri,
        ConfigurationKeys.RemoteControlReportStatusUri,
        ConfigurationKeys.RemoteControlUserIdentity,
        ConfigurationKeys.RemoteControlDeviceIdentity,
        ConfigurationKeys.ExternalNotificationServerChanSendKey,
        ConfigurationKeys.ExternalNotificationBarkSendKey,
        ConfigurationKeys.ExternalNotificationBarkServer,
        ConfigurationKeys.ExternalNotificationSmtpServer,
        ConfigurationKeys.ExternalNotificationSmtpPort,
        ConfigurationKeys.ExternalNotificationSmtpUser,
        ConfigurationKeys.ExternalNotificationSmtpPassword,
        ConfigurationKeys.ExternalNotificationSmtpFrom,
        ConfigurationKeys.ExternalNotificationSmtpTo,
        ConfigurationKeys.ExternalNotificationDiscordBotToken,
        ConfigurationKeys.ExternalNotificationDiscordUserId,
        ConfigurationKeys.ExternalNotificationDiscordWebhookUrl,
        ConfigurationKeys.ExternalNotificationDingTalkAccessToken,
        ConfigurationKeys.ExternalNotificationDingTalkSecret,
        ConfigurationKeys.ExternalNotificationTelegramBotToken,
        ConfigurationKeys.ExternalNotificationTelegramChatId,
        ConfigurationKeys.ExternalNotificationTelegramTopicId,
        ConfigurationKeys.ExternalNotificationQmsgServer,
        ConfigurationKeys.ExternalNotificationQmsgKey,
        ConfigurationKeys.ExternalNotificationQmsgUser,
        ConfigurationKeys.ExternalNotificationQmsgBot,
        ConfigurationKeys.ExternalNotificationGotifyServer,
        ConfigurationKeys.ExternalNotificationGotifyToken,
        ConfigurationKeys.ExternalNotificationCustomWebhookUrl,
        ConfigurationKeys.ExternalNotificationCustomWebhookBody,
        ExternalNotificationCustomWebhookHeaders,
    };

    private static readonly HashSet<string> GlobalEncryptedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ConfigurationKeys.MirrorChyanCdk,
    };

    private static readonly IReadOnlyDictionary<string, (string DisplayName, string ResourceKey)> DisplayNames =
        new Dictionary<string, (string DisplayName, string ResourceKey)>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationKeys.RemoteControlGetTaskEndpointUri] = ("远程控制获取任务地址", string.Empty),
            [ConfigurationKeys.RemoteControlReportStatusUri] = ("远程控制上报地址", string.Empty),
            [ConfigurationKeys.RemoteControlUserIdentity] = ("远程控制用户标识", string.Empty),
            [ConfigurationKeys.RemoteControlDeviceIdentity] = ("远程控制设备标识", string.Empty),
            [ConfigurationKeys.ExternalNotificationServerChanSendKey] = ("Server Chan 发送密钥", "ExternalNotificationServerChanSendKey"),
            [ConfigurationKeys.ExternalNotificationBarkSendKey] = ("Bark 发送密钥", "ExternalNotificationBarkSendKey"),
            [ConfigurationKeys.ExternalNotificationBarkServer] = ("Bark 服务器", "ExternalNotificationBarkServer"),
            [ConfigurationKeys.ExternalNotificationSmtpServer] = ("SMTP 服务器", "ExternalNotificationSmtpServer"),
            [ConfigurationKeys.ExternalNotificationSmtpPort] = ("SMTP 端口", "ExternalNotificationSmtpPort"),
            [ConfigurationKeys.ExternalNotificationSmtpUser] = ("SMTP 用户名", "ExternalNotificationSmtpUser"),
            [ConfigurationKeys.ExternalNotificationSmtpPassword] = ("SMTP 密码", "ExternalNotificationSmtpPassword"),
            [ConfigurationKeys.ExternalNotificationSmtpFrom] = ("SMTP 发件人", "ExternalNotificationSmtpFrom"),
            [ConfigurationKeys.ExternalNotificationSmtpTo] = ("SMTP 收件人", "ExternalNotificationSmtpTo"),
            [ConfigurationKeys.ExternalNotificationDiscordBotToken] = ("Discord 机器人 Token", "ExternalNotificationDiscordBotToken"),
            [ConfigurationKeys.ExternalNotificationDiscordUserId] = ("Discord 用户 ID", "ExternalNotificationDiscordUserId"),
            [ConfigurationKeys.ExternalNotificationDiscordWebhookUrl] = ("Discord Webhook URL", "ExternalNotificationDiscordWebhookUrl"),
            [ConfigurationKeys.ExternalNotificationDingTalkAccessToken] = ("钉钉 Access Token", "ExternalNotificationDingTalkAccessToken"),
            [ConfigurationKeys.ExternalNotificationDingTalkSecret] = ("钉钉加签密钥", "ExternalNotificationDingTalkSecret"),
            [ConfigurationKeys.ExternalNotificationTelegramBotToken] = ("Telegram 机器人 Token", "ExternalNotificationTelegramBotToken"),
            [ConfigurationKeys.ExternalNotificationTelegramChatId] = ("Telegram 聊天 ID", "ExternalNotificationTelegramChatId"),
            [ConfigurationKeys.ExternalNotificationTelegramTopicId] = ("Telegram 话题 ID", "ExternalNotificationTelegramTopicId"),
            [ConfigurationKeys.ExternalNotificationQmsgServer] = ("Qmsg Server", "ExternalNotificationQmsgServer"),
            [ConfigurationKeys.ExternalNotificationQmsgKey] = ("Qmsg Key", "ExternalNotificationQmsgKey"),
            [ConfigurationKeys.ExternalNotificationQmsgUser] = ("Qmsg 用户 QQ", "ExternalNotificationQmsgUser"),
            [ConfigurationKeys.ExternalNotificationQmsgBot] = ("Qmsg 机器人 QQ", "ExternalNotificationQmsgBot"),
            [ConfigurationKeys.ExternalNotificationGotifyServer] = ("Gotify 服务器 URL", "ExternalNotificationGotifyServer"),
            [ConfigurationKeys.ExternalNotificationGotifyToken] = ("Gotify 应用程序令牌", "ExternalNotificationGotifyToken"),
            [ConfigurationKeys.ExternalNotificationCustomWebhookUrl] = ("自定义 Webhook URL", "ExternalNotificationCustomWebhookUrl"),
            [ConfigurationKeys.ExternalNotificationCustomWebhookBody] = ("自定义 Webhook 消息体模板", "ExternalNotificationCustomWebhookBody"),
            [ExternalNotificationCustomWebhookHeaders] = ("自定义 Webhook 请求头", "ExternalNotificationCustomWebhookHeaders"),
            [ConfigurationKeys.MirrorChyanCdk] = ("Mirror 酱 CDK", string.Empty),
        };

    public static JsonNode? ConvertProfileValue(string profileName, string key, JsonNode? value, ImportReport report)
    {
        if (!ProfileEncryptedKeys.Contains(key))
        {
            return value;
        }

        return ConvertEncryptedStringValue(profileName, key, value, report, isGlobal: false);
    }

    public static JsonNode? ConvertGlobalValue(string key, JsonNode? value, ImportReport report)
    {
        if (!GlobalEncryptedKeys.Contains(key))
        {
            return value;
        }

        return ConvertEncryptedStringValue(GlobalConfigurationName, key, value, report, isGlobal: true);
    }

    private static JsonNode? ConvertEncryptedStringValue(
        string configurationName,
        string key,
        JsonNode? value,
        ImportReport report,
        bool isGlobal)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
        {
            return value;
        }

        if (string.IsNullOrEmpty(text) || IsKnownPlainDefault(key, text))
        {
            return value;
        }

        if (!TryDecodeWindowsDpapiBlob(text, out var encryptedBytes))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            AppendUnreadableValue(
                report,
                configurationName,
                key,
                isGlobal,
                "unsupported-platform",
                $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted on this platform and was cleared. Please re-enter it after import.");
            return JsonValue.Create(string.Empty);
        }

        try
        {
            var decrypted = InvokeWindowsProtectedData("Unprotect", encryptedBytes);
            if (decrypted is null)
            {
                AppendUnreadableValue(
                    report,
                    configurationName,
                    key,
                    isGlobal,
                    "decrypt-failed",
                    $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted and was cleared. Please re-enter it after import.");
                return JsonValue.Create(string.Empty);
            }

            return JsonValue.Create(Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            AppendUnreadableValue(
                report,
                configurationName,
                key,
                isGlobal,
                "decrypt-failed",
                $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted and was cleared. Please re-enter it after import.");
            return JsonValue.Create(string.Empty);
        }
    }

    private static bool IsKnownPlainDefault(string key, string value)
    {
        return string.Equals(key, ConfigurationKeys.ExternalNotificationBarkServer, StringComparison.OrdinalIgnoreCase)
               && string.Equals(value, "https://api.day.app", StringComparison.Ordinal);
    }

    private static bool TryDecodeWindowsDpapiBlob(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length < 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch
        {
            return false;
        }

        ReadOnlySpan<byte> dpapiProviderGuid =
        [
            0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11,
            0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
        ];

        return bytes.Length > 20
               && bytes[0] == 0x01
               && bytes[1] == 0x00
               && bytes[2] == 0x00
               && bytes[3] == 0x00
               && bytes.AsSpan(4, dpapiProviderGuid.Length).SequenceEqual(dpapiProviderGuid);
    }

    private static byte[]? InvokeWindowsProtectedData(string methodName, byte[] data)
    {
        var protectedDataType = Type.GetType(
            "System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        var dataProtectionScopeType = Type.GetType(
            "System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (protectedDataType is null || dataProtectionScopeType is null)
        {
            return null;
        }

        var method = protectedDataType.GetMethod(
            methodName,
            [typeof(byte[]), typeof(byte[]), dataProtectionScopeType]);
        if (method is null)
        {
            return null;
        }

        var currentUserScope = Enum.Parse(dataProtectionScopeType, "CurrentUser");
        return method.Invoke(null, [data, null, currentUserScope]) as byte[];
    }

    private static void AppendWarningOnce(ImportReport report, string warning)
    {
        if (!report.Warnings.Contains(warning, StringComparer.Ordinal))
        {
            report.Warnings.Add(warning);
        }
    }

    private static void AppendUnreadableValue(
        ImportReport report,
        string configurationName,
        string key,
        bool isGlobal,
        string reason,
        string warning)
    {
        AppendWarningOnce(report, warning);

        if (report.UnreadableValues.Any(value =>
                string.Equals(value.ConfigurationName, configurationName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var display = DisplayNames.TryGetValue(key, out var known)
            ? known
            : (DisplayName: key, ResourceKey: string.Empty);
        report.UnreadableValues.Add(new ImportUnreadableConfigValue(
            configurationName,
            key,
            display.DisplayName,
            display.ResourceKey,
            isGlobal,
            reason));
    }
}
