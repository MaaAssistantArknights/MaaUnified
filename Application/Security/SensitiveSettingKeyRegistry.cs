using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Security;

public enum SensitiveSettingScope
{
    Global = 0,
    Profile = 1,
}

public sealed record SensitiveSettingDefinition(
    string Key,
    SensitiveSettingScope Scope,
    string DisplayName,
    string LocalizationKey,
    string DefaultPlaintextValue = "");

public static class SensitiveSettingKeyRegistry
{
    public const string ExternalNotificationCustomWebhookHeaders = "ExternalNotification.CustomWebhook.Headers";

    private static readonly object _definitionsGate = new();
    private static readonly List<SensitiveSettingDefinition> _definitions =
    [
        Profile(ConfigurationKeys.RemoteControlGetTaskEndpointUri, "远程控制获取任务地址", "Settings.RemoteControl.GetTaskEndpoint"),
        Profile(ConfigurationKeys.RemoteControlReportStatusUri, "远程控制上报地址", "Settings.RemoteControl.ReportTaskEndpoint"),
        Profile(ConfigurationKeys.RemoteControlUserIdentity, "远程控制用户标识", "Settings.RemoteControl.UserIdentity"),
        Profile(ConfigurationKeys.RemoteControlDeviceIdentity, "远程控制设备标识", "Settings.RemoteControl.DeviceIdentity"),
        Profile(ConfigurationKeys.ExternalNotificationServerChanSendKey, "Server Chan 发送密钥", "ExternalNotificationServerChanSendKey"),
        Profile(ConfigurationKeys.ExternalNotificationBarkSendKey, "Bark 发送密钥", "ExternalNotificationBarkSendKey"),
        Profile(ConfigurationKeys.ExternalNotificationBarkServer, "Bark 服务器", "ExternalNotificationBarkServer", "https://api.day.app"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpServer, "SMTP 服务器", "ExternalNotificationSmtpServer"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpPort, "SMTP 端口", "ExternalNotificationSmtpPort"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpUser, "SMTP 用户名", "ExternalNotificationSmtpUser"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpPassword, "SMTP 密码", "ExternalNotificationSmtpPassword"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpFrom, "SMTP 发件人", "ExternalNotificationSmtpFrom"),
        Profile(ConfigurationKeys.ExternalNotificationSmtpTo, "SMTP 收件人", "ExternalNotificationSmtpTo"),
        Profile(ConfigurationKeys.ExternalNotificationDiscordBotToken, "Discord 机器人 Token", "ExternalNotificationDiscordBotToken"),
        Profile(ConfigurationKeys.ExternalNotificationDiscordUserId, "Discord 用户 ID", "ExternalNotificationDiscordUserId"),
        Profile(ConfigurationKeys.ExternalNotificationDiscordWebhookUrl, "Discord Webhook URL", "ExternalNotificationDiscordWebhookUrl"),
        Profile(ConfigurationKeys.ExternalNotificationDingTalkAccessToken, "钉钉 Access Token", "ExternalNotificationDingTalkAccessToken"),
        Profile(ConfigurationKeys.ExternalNotificationDingTalkSecret, "钉钉加签密钥", "ExternalNotificationDingTalkSecret"),
        Profile(ConfigurationKeys.ExternalNotificationTelegramBotToken, "Telegram 机器人 Token", "ExternalNotificationTelegramBotToken"),
        Profile(ConfigurationKeys.ExternalNotificationTelegramChatId, "Telegram 聊天 ID", "ExternalNotificationTelegramChatId"),
        Profile(ConfigurationKeys.ExternalNotificationTelegramTopicId, "Telegram 话题 ID", "ExternalNotificationTelegramTopicId"),
        Profile(ConfigurationKeys.ExternalNotificationQmsgServer, "Qmsg Server", "ExternalNotificationQmsgServer"),
        Profile(ConfigurationKeys.ExternalNotificationQmsgKey, "Qmsg Key", "ExternalNotificationQmsgKey"),
        Profile(ConfigurationKeys.ExternalNotificationQmsgUser, "Qmsg 用户 QQ", "ExternalNotificationQmsgUser"),
        Profile(ConfigurationKeys.ExternalNotificationQmsgBot, "Qmsg 机器人 QQ", "ExternalNotificationQmsgBot"),
        Profile(ConfigurationKeys.ExternalNotificationGotifyServer, "Gotify 服务器 URL", "ExternalNotificationGotifyServer"),
        Profile(ConfigurationKeys.ExternalNotificationGotifyToken, "Gotify 应用程序令牌", "ExternalNotificationGotifyToken"),
        Profile(ConfigurationKeys.ExternalNotificationCustomWebhookUrl, "自定义 Webhook URL", "ExternalNotificationCustomWebhookUrl"),
        Profile(ConfigurationKeys.ExternalNotificationCustomWebhookBody, "自定义 Webhook 消息体模板", "ExternalNotificationCustomWebhookBody"),
        Profile(ExternalNotificationCustomWebhookHeaders, "自定义 Webhook 请求头", "ExternalNotificationCustomWebhookHeaders"),
        Global(ConfigurationKeys.MirrorChyanCdk, "Mirror 酱 CDK", "Settings.VersionUpdate.MirrorCdk"),
        Global(ConfigurationKeys.GitHubToken, "GitHub Token", "Settings.VersionUpdate.GitHubToken"),
    ];

    private static Dictionary<string, SensitiveSettingDefinition> _byKey =
        _definitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 实例键前缀：ExternalNotification.Instance.&lt;id&gt;.&lt;parameter&gt;。与 WPF Configs 多实例
    /// 数组配套，参数值仍走敏感存储，不写进 config JSON。
    /// </summary>
    public const string ExternalNotificationInstanceKeyPrefix = "ExternalNotification.Instance.";

    /// <summary>
    /// 注册一组动态敏感键（多实例渠道参数）。重复注册幂等；注册后 ExtractAndRemove/Inject
    /// 会把这个键当作普通敏感键处理。
    /// </summary>
    /// <param name="definitions">要注册的敏感键定义。</param>
    public static void RegisterDynamic(IEnumerable<SensitiveSettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        lock (_definitionsGate)
        {
            var changed = false;
            foreach (var definition in definitions)
            {
                if (_byKey.ContainsKey(definition.Key))
                {
                    continue;
                }

                _definitions.Add(definition);
                _byKey[definition.Key] = definition;
                changed = true;
            }

            if (changed)
            {
                _byKey = new Dictionary<string, SensitiveSettingDefinition>(_byKey, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 构造一个多实例渠道的敏感键。
    /// </summary>
    /// <param name="instanceId">实例 id（配置数组中的 Id）。</param>
    /// <param name="parameterKey">渠道参数名。</param>
    /// <returns>敏感键。</returns>
    /// <summary>
    /// 从配置里的多实例数组（ExternalNotification.Configs）扫描实例 Id 与 provider，
    /// 为每个参数注册动态敏感键。必须在敏感存储 Extract/Inject 之前调用，否则实例参数
    /// 会被当作普通配置明文写入。
    /// </summary>
    /// <param name="config">配置快照。</param>
    public static void RegisterExternalNotificationInstanceKeys(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.GlobalValues.TryGetValue("ExternalNotification.Configs", out var node) || node is null)
        {
            return;
        }

        System.Text.Json.Nodes.JsonArray? array = node as System.Text.Json.Nodes.JsonArray;
        if (array is null && node is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                array = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonArray;
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }
        }

        if (array is null)
        {
            return;
        }

        var definitions = new List<SensitiveSettingDefinition>();
        foreach (var item in array)
        {
            if (item is not System.Text.Json.Nodes.JsonObject configObject)
            {
                continue;
            }

            if (!configObject.TryGetPropertyValue("Id", out var idNode)
                || idNode is not System.Text.Json.Nodes.JsonValue idValue
                || !idValue.TryGetValue<string>(out var instanceId)
                || string.IsNullOrWhiteSpace(instanceId))
            {
                continue;
            }

            if (!configObject.TryGetPropertyValue("$type", out var typeNode)
                || typeNode is not System.Text.Json.Nodes.JsonValue typeValue
                || !typeValue.TryGetValue<string>(out var provider)
                || string.IsNullOrWhiteSpace(provider))
            {
                continue;
            }

            foreach (var parameterKey in MAAUnified.Application.Services.Notifications.ExternalNotificationChannels.ProviderParameterKeys(provider))
            {
                definitions.Add(new SensitiveSettingDefinition(
                    BuildExternalNotificationInstanceKey(instanceId, parameterKey),
                    SensitiveSettingScope.Profile,
                    $"External notification instance {provider}.{parameterKey}",
                    "ExternalNotificationInstanceParameter"));
            }
        }

        if (definitions.Count > 0)
        {
            RegisterDynamic(definitions);
        }
    }

    public static string BuildExternalNotificationInstanceKey(string instanceId, string parameterKey)
        => $"{ExternalNotificationInstanceKeyPrefix}{instanceId}.{parameterKey}";

    public static IReadOnlyList<SensitiveSettingDefinition> Definitions => _definitions;

    public static bool IsSensitive(string key) => _byKey.ContainsKey(key);

    public static bool TryGet(string key, out SensitiveSettingDefinition definition)
        => _byKey.TryGetValue(key, out definition!);

    public static SensitiveSettingsDocument ExtractAndRemove(UnifiedConfig config, string storeId, long revision)
    {
        ArgumentNullException.ThrowIfNull(config);

        var document = new SensitiveSettingsDocument { StoreId = storeId, Revision = revision };
        NormalizeMisplacedProfileValues(config);
        foreach (var definition in _definitions.Where(definition => definition.Scope == SensitiveSettingScope.Global))
        {
            if (config.GlobalValues.Remove(definition.Key, out var value))
            {
                document.GlobalValues[definition.Key] = value?.DeepClone();
            }
        }

        foreach (var (profileName, profile) in config.Profiles)
        {
            Dictionary<string, JsonNode?>? values = null;
            foreach (var definition in _definitions.Where(definition => definition.Scope == SensitiveSettingScope.Profile))
            {
                if (!profile.Values.Remove(definition.Key, out var value))
                {
                    continue;
                }

                values ??= new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
                values[definition.Key] = value?.DeepClone();
            }

            if (values is not null)
            {
                document.Profiles[profileName] = values;
            }
        }

        return document;
    }

    public static int NormalizeMisplacedProfileValues(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var normalized = 0;
        config.Profiles.TryGetValue(config.CurrentProfile, out var currentProfile);
        if (currentProfile is null)
        {
            return 0;
        }

        foreach (var definition in _definitions.Where(definition => definition.Scope == SensitiveSettingScope.Profile))
        {
            if (!config.GlobalValues.Remove(definition.Key, out var value))
            {
                continue;
            }

            normalized++;
            if (currentProfile is not null && !currentProfile.Values.ContainsKey(definition.Key))
            {
                currentProfile.Values[definition.Key] = value?.DeepClone();
            }
        }

        return normalized;
    }

    public static bool HasMisplacedProfileValues(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _definitions.Any(definition =>
            definition.Scope == SensitiveSettingScope.Profile
            && config.GlobalValues.ContainsKey(definition.Key));
    }

    public static void Inject(UnifiedConfig config, SensitiveSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateDocument(document);

        foreach (var (key, value) in document.GlobalValues)
        {
            if (!TryGet(key, out var definition) || definition.Scope != SensitiveSettingScope.Global)
            {
                throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive document contains an invalid global key.");
            }

            config.GlobalValues[key] = value?.DeepClone();
        }

        foreach (var (profileName, values) in document.Profiles)
        {
            if (!config.Profiles.TryGetValue(profileName, out var profile))
            {
                continue;
            }

            foreach (var (key, value) in values)
            {
                if (!TryGet(key, out var definition) || definition.Scope != SensitiveSettingScope.Profile)
                {
                    throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive document contains an invalid profile key.");
                }

                profile.Values[key] = value?.DeepClone();
            }
        }
    }

    public static void InjectDefaults(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var definition in _definitions.Where(static definition =>
                     !string.IsNullOrEmpty(definition.DefaultPlaintextValue)))
        {
            if (definition.Scope == SensitiveSettingScope.Global)
            {
                config.GlobalValues.TryAdd(
                    definition.Key,
                    JsonValue.Create(definition.DefaultPlaintextValue));
                continue;
            }

            foreach (var profile in config.Profiles.Values)
            {
                profile.Values.TryAdd(
                    definition.Key,
                    JsonValue.Create(definition.DefaultPlaintextValue));
            }
        }
    }

    public static bool HasValues(SensitiveSettingsDocument document)
        => document.GlobalValues.Count > 0 || document.Profiles.Values.Any(values => values.Count > 0);

    public static IReadOnlyList<string> GetPlaintextValues(UnifiedConfig config)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in _definitions)
        {
            if (definition.Scope == SensitiveSettingScope.Global)
            {
                AddValue(config.GlobalValues, definition, values);
                continue;
            }

            AddValue(config.GlobalValues, definition, values);

            foreach (var profile in config.Profiles.Values)
            {
                AddValue(profile.Values, definition, values);
            }
        }

        return [.. values];
    }

    public static bool HasNonDefaultValues(SensitiveSettingsDocument document)
    {
        foreach (var (key, value) in document.GlobalValues.Concat(document.Profiles.Values.SelectMany(values => values)))
        {
            var text = value?.ToString() ?? string.Empty;
            if (!TryGet(key, out var definition)
                || (!string.IsNullOrEmpty(text) && !string.Equals(text, definition.DefaultPlaintextValue, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public static void ValidateDocument(SensitiveSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.FormatVersion != SensitiveSettingsDocument.LatestFormatVersion
            || string.IsNullOrWhiteSpace(document.StoreId)
            || document.Revision <= 0
            || document.GlobalValues is null
            || document.Profiles is null)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive document metadata is invalid.");
        }

        ValidateScopedValues(document.GlobalValues, SensitiveSettingScope.Global, "global");
        EnsureUniqueKeys(document.Profiles.Keys, "profile name");
        foreach (var (profileName, values) in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profileName) || values is null)
            {
                throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive document contains an invalid profile.");
            }

            ValidateScopedValues(values, SensitiveSettingScope.Profile, "profile");
        }
    }

    private static SensitiveSettingDefinition Profile(string key, string displayName, string localizationKey = "", string defaultValue = "")
        => new(key, SensitiveSettingScope.Profile, displayName, localizationKey, defaultValue);

    private static SensitiveSettingDefinition Global(string key, string displayName, string localizationKey = "", string defaultValue = "")
        => new(key, SensitiveSettingScope.Global, displayName, localizationKey, defaultValue);

    private static void AddValue(
        IReadOnlyDictionary<string, JsonNode?> source,
        SensitiveSettingDefinition definition,
        ISet<string> destination)
    {
        if (!source.TryGetValue(definition.Key, out var node))
        {
            return;
        }

        var value = node?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(value) && !string.Equals(value, definition.DefaultPlaintextValue, StringComparison.Ordinal))
        {
            destination.Add(value);
        }
    }

    private static void ValidateScopedValues(
        IReadOnlyDictionary<string, JsonNode?> values,
        SensitiveSettingScope expectedScope,
        string scopeName)
    {
        EnsureUniqueKeys(values.Keys, $"{scopeName} key");
        foreach (var key in values.Keys)
        {
            if (!TryGet(key, out var definition) || definition.Scope != expectedScope)
            {
                throw new SensitiveStorageException(
                    SensitiveStorageErrorCode.InvalidDocument,
                    $"Sensitive document contains an invalid {scopeName} key.");
            }
        }
    }

    private static void EnsureUniqueKeys(IEnumerable<string> keys, string fieldName)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !unique.Add(key))
            {
                throw new SensitiveStorageException(
                    SensitiveStorageErrorCode.InvalidDocument,
                    $"Sensitive document contains a duplicate or empty {fieldName}.");
            }
        }
    }
}
