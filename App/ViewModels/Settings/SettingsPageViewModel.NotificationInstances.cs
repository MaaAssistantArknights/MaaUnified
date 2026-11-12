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
public sealed class NotificationChannelInstanceViewModel : Infrastructure.ObservableObject
{
    private string _parametersText;

    public NotificationChannelInstanceViewModel(string id, string provider, string displayName, string parametersText)
    {
        Id = id;
        Provider = provider;
        DisplayName = displayName;
        _parametersText = parametersText;
    }

    public string Id { get; }

    public string Provider { get; }

    public string DisplayName { get; }

    public string ParametersText
    {
        get => _parametersText;
        set => SetProperty(ref _parametersText, value);
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

            foreach (var (key, value) in ParseNotificationParameterLines(instance.ParametersText))
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
            foreach (var (key, value) in ParseNotificationParameterLines(instance.ParametersText))
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
