using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.Settings;

/// <summary>
/// 一个外部通知渠道实例（WPF dev-v2 <c>Gui.ExternalNotification.Configs</c> 的数组元素）。
/// 参数值存放在敏感存储的实例键里，数组本身只保存 Id 与 provider，避免凭据进入 config JSON。
/// </summary>
public sealed class NotificationChannelParameterFieldViewModel(string key) : Infrastructure.ObservableObject
{
    private string _value = string.Empty;

    public string Key { get; } = key;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed class NotificationChannelInstanceViewModel : Infrastructure.ObservableObject
{
    private readonly Dictionary<string, string> _extraParameters = new(StringComparer.OrdinalIgnoreCase);

    public NotificationChannelInstanceViewModel(string id, string provider, string displayName, string parametersText)
    {
        Id = id;
        Provider = provider;
        DisplayName = displayName;
        foreach (var key in Application.Services.Notifications.ExternalNotificationChannels.ProviderParameterKeys(provider))
        {
            ParameterFields.Add(new NotificationChannelParameterFieldViewModel(key));
        }

        SetParametersText(parametersText);
    }

    public string Id { get; }

    public string Provider { get; }

    public string DisplayName { get; }

    /// <summary>Gets 当前 provider 的字段化参数编辑项。</summary>
    public ObservableCollection<NotificationChannelParameterFieldViewModel> ParameterFields { get; } = [];

    /// <summary>Gets 原始 key=value 文本（保留未知参数，供高级编辑/兼容旧调用）。</summary>
    public string ParametersText
    {
        get
        {
            var lines = ParameterFields
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{item.Key}={item.Value}")
                .Concat(_extraParameters.Select(pair => $"{pair.Key}={pair.Value}"));
            return string.Join(Environment.NewLine, lines);
        }
        set => SetParametersText(value);
    }

    /// <summary>把 key=value 文本填入字段；未知 key 保留在 extra 中。</summary>
    /// <param name="text">参数文本。</param>
    public void SetParametersText(string text)
    {
        foreach (var field in ParameterFields)
        {
            field.Value = string.Empty;
        }

        _extraParameters.Clear();
        foreach (var (key, value) in ParseLines(text))
        {
            var field = ParameterFields.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                _extraParameters[key] = value;
            }
            else
            {
                field.Value = value;
            }
        }

        OnPropertyChanged(nameof(ParametersText));
    }

    /// <summary>Gets 当前字段 + 未知参数合并后的参数表。</summary>
    /// <returns>参数表。</returns>
    public Dictionary<string, string> BuildParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ParameterFields)
        {
            if (!string.IsNullOrWhiteSpace(field.Value))
            {
                parameters[field.Key] = field.Value;
            }
        }

        foreach (var (key, value) in _extraParameters)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static IEnumerable<(string Key, string Value)> ParseLines(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            yield return (line[..index].Trim(), line[(index + 1)..]);
        }
    }
}

public sealed partial class SettingsPageViewModel
{
    private NotificationChannelInstanceViewModel? _selectedNotificationChannelInstance;

    /// <summary>Gets 多实例外部通知渠道。</summary>
    public ObservableCollection<NotificationChannelInstanceViewModel> NotificationChannelInstances { get; } = [];

    /// <summary>Gets or sets 当前编辑的渠道实例。</summary>
    public NotificationChannelInstanceViewModel? SelectedNotificationChannelInstance
    {
        get => _selectedNotificationChannelInstance;
        set => SetProperty(ref _selectedNotificationChannelInstance, value);
    }

    /// <summary>Gets a value indicating whether 存在多实例渠道（存在时以数组为发送来源）。</summary>
    public bool HasNotificationChannelInstances => NotificationChannelInstances.Count > 0;

    /// <summary>Gets a value indicating whether 可以添加当前选中的 provider 实例。</summary>
    public bool CanAddNotificationChannelInstance =>
        NotificationChannelInstances.Count < 20
        && !string.IsNullOrWhiteSpace(SelectedNotificationProvider)
        && AvailableNotificationProviders.Contains(SelectedNotificationProvider, StringComparer.OrdinalIgnoreCase);

    /// <summary>从配置数组加载实例；没有数组时保持旧单实例 UI。</summary>
    private void LoadNotificationChannelInstancesFromConfig(UnifiedConfig config)
    {
        NotificationChannelInstances.Clear();
        SelectedNotificationChannelInstance = null;
        if (!Application.Services.Notifications.ExternalNotificationChannels.TryReadConfiguredInstances(config, out var channels))
        {
            OnPropertyChanged(nameof(HasNotificationChannelInstances));
            OnPropertyChanged(nameof(CanAddNotificationChannelInstance));
            return;
        }

        foreach (var channel in channels.Where(channel => !string.IsNullOrWhiteSpace(channel.Id)))
        {
            NotificationChannelInstances.Add(new NotificationChannelInstanceViewModel(
                channel.Id!,
                channel.Provider,
                MapProviderToLegacyDisplayName(channel.Provider),
                Application.Services.Notifications.ExternalNotificationChannels.BuildParameterText(channel)));
        }

        SelectedNotificationChannelInstance = NotificationChannelInstances.FirstOrDefault();
        OnPropertyChanged(nameof(HasNotificationChannelInstances));
        OnPropertyChanged(nameof(CanAddNotificationChannelInstance));
    }

    /// <summary>添加一个当前选中 provider 的渠道实例。</summary>
    public void AddNotificationChannelInstance()
    {
        if (!CanAddNotificationChannelInstance)
        {
            return;
        }

        var provider = NormalizeNotificationProvider(SelectedNotificationProvider);
        var id = Guid.NewGuid().ToString("N")[..12];
        var instance = new NotificationChannelInstanceViewModel(
            id,
            provider,
            MapProviderToLegacyDisplayName(provider),
            string.Empty);
        NotificationChannelInstances.Add(instance);
        SelectedNotificationChannelInstance = instance;
        OnPropertyChanged(nameof(HasNotificationChannelInstances));
        OnPropertyChanged(nameof(CanAddNotificationChannelInstance));
        HasPendingGuiChanges = true;
    }

    /// <summary>删除一个渠道实例。</summary>
    /// <param name="instance">要删除的实例。</param>
    public void RemoveNotificationChannelInstance(NotificationChannelInstanceViewModel? instance)
    {
        if (instance is null)
        {
            return;
        }

        NotificationChannelInstances.Remove(instance);
        if (ReferenceEquals(SelectedNotificationChannelInstance, instance))
        {
            SelectedNotificationChannelInstance = NotificationChannelInstances.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasNotificationChannelInstances));
        OnPropertyChanged(nameof(CanAddNotificationChannelInstance));
        HasPendingGuiChanges = true;
    }

    /// <summary>把实例编辑写回配置字典：数组只留 Id/provider，参数写入敏感键。</summary>
    /// <param name="updates">保存字典（profile 键值）。</param>
    private void AddNotificationChannelInstanceUpdates(Dictionary<string, string> updates)
    {
        if (NotificationChannelInstances.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        var definitions = new List<SensitiveSettingDefinition>();
        var firstByProvider = new Dictionary<string, NotificationChannelInstanceViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in NotificationChannelInstances)
        {
            array.Add(new JsonObject
            {
                ["$type"] = instance.Provider,
                ["Id"] = instance.Id,
            });
            firstByProvider.TryAdd(instance.Provider, instance);

            foreach (var (key, value) in instance.BuildParameters())
            {
                var sensitiveKey = SensitiveSettingKeyRegistry.BuildExternalNotificationInstanceKey(instance.Id, key);
                definitions.Add(new SensitiveSettingDefinition(
                    sensitiveKey,
                    SensitiveSettingScope.Profile,
                    $"External notification instance {instance.Provider}.{key}",
                    "ExternalNotificationInstanceParameter"));
                updates[sensitiveKey] = value;
            }
        }

        if (definitions.Count > 0)
        {
            SensitiveSettingKeyRegistry.RegisterDynamic(definitions);
        }

        // 数组写入全局配置；profile 更新由调用方统一保存。
        Runtime.ConfigurationService.CurrentConfig.GlobalValues[Application.Services.Notifications.ExternalNotificationChannels.ConfigsKey] =
            JsonNode.Parse(array.ToJsonString());

        // 回写每个 provider 的第一个实例到旧扁平键，保证旧版本/单实例读取仍可用。
        foreach (var (provider, instance) in firstByProvider)
        {
            foreach (var (key, value) in instance.BuildParameters())
            {
                if (TryResolveProviderConfigKey(provider, key, out var flatKey))
                {
                    updates[flatKey] = value;
                }
            }
        }

        var enabledProviders = NotificationChannelInstances
            .Select(instance => instance.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        updates[ConfigurationKeys.ExternalNotificationEnabled] = BuildEnabledProviderConfigValue(enabledProviders);
    }

    private static IEnumerable<(string Key, string Value)> ParseNotificationParameterLines(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..];
            if (key.Length > 0)
            {
                yield return (key, value);
            }
        }
    }

    private static bool TryResolveProviderConfigKey(string provider, string parameterKey, out string configKey)
    {
        configKey = string.Empty;
        var map = provider switch
        {
            "Smtp" => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationSmtpServer,
                ["port"] = ConfigurationKeys.ExternalNotificationSmtpPort,
                ["user"] = ConfigurationKeys.ExternalNotificationSmtpUser,
                ["password"] = ConfigurationKeys.ExternalNotificationSmtpPassword,
                ["from"] = ConfigurationKeys.ExternalNotificationSmtpFrom,
                ["to"] = ConfigurationKeys.ExternalNotificationSmtpTo,
            },
            "Bark" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = ConfigurationKeys.ExternalNotificationBarkSendKey,
                ["server"] = ConfigurationKeys.ExternalNotificationBarkServer,
            },
            "ServerChan" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sendKey"] = ConfigurationKeys.ExternalNotificationServerChanSendKey },
            "Discord" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationDiscordBotToken,
                ["userId"] = ConfigurationKeys.ExternalNotificationDiscordUserId,
            },
            "Telegram" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationTelegramBotToken,
                ["chatId"] = ConfigurationKeys.ExternalNotificationTelegramChatId,
                ["topicId"] = ConfigurationKeys.ExternalNotificationTelegramTopicId,
            },
            "DingTalk" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["accessToken"] = ConfigurationKeys.ExternalNotificationDingTalkAccessToken,
                ["secret"] = ConfigurationKeys.ExternalNotificationDingTalkSecret,
            },
            "Qmsg" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationQmsgServer,
                ["key"] = ConfigurationKeys.ExternalNotificationQmsgKey,
                ["user"] = ConfigurationKeys.ExternalNotificationQmsgUser,
                ["bot"] = ConfigurationKeys.ExternalNotificationQmsgBot,
            },
            "Gotify" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationGotifyServer,
                ["token"] = ConfigurationKeys.ExternalNotificationGotifyToken,
            },
            "CustomWebhook" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = ConfigurationKeys.ExternalNotificationCustomWebhookUrl,
                ["body"] = ConfigurationKeys.ExternalNotificationCustomWebhookBody,
            },
            _ => null,
        };
        if (map is null || !map.TryGetValue(parameterKey, out var resolved))
        {
            return false;
        }

        configKey = resolved;
        return true;
    }
}
