using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel
{
    private static readonly HttpClient AboutAnnouncementHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly string[] AboutAnnouncementApiBaseUrls =
    [
        "https://api.maa.plus/MaaAssistantArknights/api/",
        "https://api2.maa.plus/MaaAssistantArknights/api/",
    ];

    private static readonly IReadOnlyDictionary<string, string> NotificationProviderLegacyDisplayNameMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp"] = "SMTP",
            ["ServerChan"] = "ServerChan",
            ["Bark"] = "Bark",
            ["Discord"] = "Discord",
            ["DingTalk"] = "DingTalk",
            ["Telegram"] = "Telegram",
            ["Qmsg"] = "Qmsg",
            ["Gotify"] = "Gotify",
            ["CustomWebhook"] = "Custom Webhook",
        };

    private static readonly IReadOnlyDictionary<string, string> NotificationProviderCanonicalNameMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SMTP"] = "Smtp",
            ["Smtp"] = "Smtp",
            ["ServerChan"] = "ServerChan",
            ["Bark"] = "Bark",
            ["Discord"] = "Discord",
            ["Discord Webhook"] = "Discord",
            ["DingTalk"] = "DingTalk",
            ["Telegram"] = "Telegram",
            ["Qmsg"] = "Qmsg",
            ["Gotify"] = "Gotify",
            ["Custom Webhook"] = "CustomWebhook",
            ["CustomWebhook"] = "CustomWebhook",
        };

    private static readonly string[] LegacyAnnouncementPaths =
    [
        "announcements/wpf.md",
        "announcements/wpf_en.md",
    ];

    private const string AboutBilibiliUrl = "https://space.bilibili.com/3493274731940507";
    private const string AboutGithubRepositoryUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights";
    private const string AboutQqGroupUrl = "https://api.maa.plus/MaaAssistantArknights/api/qqgroup/index.html";
    private const string AboutQqChannelUrl = "https://pd.qq.com/s/4j1ju9z47";
    private const string AboutTelegramUrl = "https://t.me/+Mgc2Zngr-hs3ZjU1";
    private const string AboutDiscordUrl = "https://discord.gg/23DfZ9uA4V";

    private readonly HashSet<string> _enabledNotificationProviders = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressNotificationProviderSelectionEvents;

    public ObservableCollection<NotificationProviderSelectionItem> NotificationProviderSelections { get; } = [];

    public async Task SaveRemoteControlAsync(CancellationToken cancellationToken = default)
    {
        ClearRemoteControlStatus();
        var normalizedUserIdentity = (RemoteUserIdentity ?? string.Empty).Trim();
        var normalizedDeviceIdentity = (RemoteDeviceIdentity ?? string.Empty).Trim();
        if (ContainsInvalidRemoteIdentity(normalizedUserIdentity) || ContainsInvalidRemoteIdentity(normalizedDeviceIdentity))
        {
            var validation = UiOperationResult.Fail(
                UiErrorCode.RemoteControlInvalidParameters,
                "Remote user/device identity cannot contain control characters.");
            RemoteControlErrorMessage = FormatRemoteControlMessage(validation.Error?.Code, validation.Message);
            RemoteControlWarningMessage = string.Empty;
            RemoteControlStatusMessage = RootTexts.GetOrDefault(
                "Settings.RemoteControl.Status.SaveFailed",
                "Failed to save remote control settings.");
            LastErrorMessage = RemoteControlErrorMessage;
            StatusMessage = RemoteControlStatusMessage;
            await RecordFailedResultAsync(
                "Settings.RemoteControl.Save.Validation",
                validation,
                cancellationToken);
            return;
        }

        RemoteUserIdentity = normalizedUserIdentity;
        RemoteDeviceIdentity = normalizedDeviceIdentity;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.RemoteControlGetTaskEndpointUri] = RemoteGetTaskEndpoint,
            [ConfigurationKeys.RemoteControlReportStatusUri] = RemoteReportEndpoint,
            [ConfigurationKeys.RemoteControlUserIdentity] = normalizedUserIdentity,
            [ConfigurationKeys.RemoteControlDeviceIdentity] = normalizedDeviceIdentity,
            [ConfigurationKeys.RemoteControlPollIntervalMs] = RemotePollInterval.ToString(),
        };

        var result = await SaveScopedSettingsAsync(
            profileUpdates: updates,
            successScope: "Settings.RemoteControl.Save",
            cancellationToken: cancellationToken);
        if (await ApplyResultAsync(result, "Settings.RemoteControl.Save", cancellationToken))
        {
            RemoteControlStatusMessage = RootTexts.GetOrDefault(
                "Settings.RemoteControl.Status.SaveSucceeded",
                "Remote control settings saved.");
            RemoteControlErrorMessage = string.Empty;
            RemoteControlWarningMessage = string.Empty;
            return;
        }

        RemoteControlErrorMessage = FormatRemoteControlMessage(result.Error?.Code, result.Message);
        RemoteControlWarningMessage = string.Empty;
        RemoteControlStatusMessage = RootTexts.GetOrDefault(
            "Settings.RemoteControl.Status.SaveFailed",
            "Failed to save remote control settings.");
    }

    public async Task TestRemoteControlConnectivityAsync(CancellationToken cancellationToken = default)
    {
        ClearRemoteControlStatus();
        var request = new RemoteControlConnectivityRequest(
            RemoteGetTaskEndpoint,
            RemoteReportEndpoint,
            RemotePollInterval);
        var result = await Runtime.RemoteControlFeatureService.TestConnectivityAsync(request, cancellationToken);
        if (result.Success)
        {
            var summary = BuildRemoteConnectivitySummary(result.Value);
            RemoteControlStatusMessage = string.Format(
                RootTexts.GetOrDefault("Settings.RemoteControl.Status.TestSucceeded", "Connectivity test succeeded. {0}"),
                summary);
            StatusMessage = RemoteControlStatusMessage;
            LastErrorMessage = string.Empty;
            await RecordEventAsync(
                "Settings.RemoteControl.Test",
                RemoteControlStatusMessage,
                cancellationToken);
            return;
        }

        var message = FormatRemoteControlMessage(result.Error?.Code, result.Message);
        var detailsSummary = BuildRemoteConnectivitySummary(ParseRemoteConnectivityDetails(result.Error?.Details));
        if (!string.IsNullOrWhiteSpace(detailsSummary))
        {
            message = $"{message} {detailsSummary}";
        }

        if (string.Equals(result.Error?.Code, UiErrorCode.RemoteControlUnsupported, StringComparison.Ordinal))
        {
            RemoteControlWarningMessage = message;
            RemoteControlErrorMessage = string.Empty;
        }
        else
        {
            RemoteControlErrorMessage = message;
            RemoteControlWarningMessage = string.Empty;
        }

        RemoteControlStatusMessage = RootTexts.GetOrDefault(
            "Settings.RemoteControl.Status.TestFailed",
            "Connectivity test failed.");
        LastErrorMessage = message;
        await RecordFailedResultAsync(
            "Settings.RemoteControl.Test",
            UiOperationResult.Fail(result.Error?.Code ?? UiErrorCode.RemoteControlConnectivityFailed, message, result.Error?.Details),
            cancellationToken);
    }

    public async Task ValidateExternalNotificationParametersAsync(CancellationToken cancellationToken = default)
    {
        ClearExternalNotificationStatus();
        PersistCurrentProviderParameters();
        var enabledProviders = ResolveEnabledNotificationProviders();
        if (enabledProviders.Count == 0)
        {
            LastErrorMessage = string.Empty;
            return;
        }

        foreach (var provider in enabledProviders)
        {
            var parameterText = _notificationProviderParameters.TryGetValue(provider, out var storedParameters)
                ? storedParameters
                : string.Empty;
            var result = await Runtime.NotificationProviderFeatureService.ValidateProviderParametersAsync(
                new NotificationProviderRequest(provider, parameterText),
                cancellationToken);
            if (!result.Success)
            {
                await ApplyExternalNotificationFailure(result, "Settings.ExternalNotification.Validate", cancellationToken);
                return;
            }
        }

        ExternalNotificationStatusMessage = string.Format(
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.ValidateSucceeded",
                "Provider `{0}` parameter validation succeeded."),
            string.Join(", ", enabledProviders.Select(MapProviderToLegacyDisplayName)));
        StatusMessage = ExternalNotificationStatusMessage;
        LastErrorMessage = string.Empty;
        await RecordEventAsync(
            "Settings.ExternalNotification.Validate",
            ExternalNotificationStatusMessage,
            cancellationToken);
    }

    public async Task TestExternalNotificationAsync(CancellationToken cancellationToken = default)
    {
        ClearExternalNotificationStatus();
        PersistCurrentProviderParameters();
        var enabledProviders = ResolveEnabledNotificationProviders();
        if (enabledProviders.Count == 0)
        {
            LastErrorMessage = string.Empty;
            return;
        }

        foreach (var provider in enabledProviders)
        {
            var parameterText = _notificationProviderParameters.TryGetValue(provider, out var storedParameters)
                ? storedParameters
                : string.Empty;
            var result = await Runtime.NotificationProviderFeatureService.SendTestAsync(
                new NotificationProviderTestRequest(
                    provider,
                    parameterText,
                    NotificationTitle,
                    NotificationMessage),
                cancellationToken);
            if (!result.Success)
            {
                await ApplyExternalNotificationFailure(result, "Settings.ExternalNotification.TestSend", cancellationToken);
                return;
            }
        }

        ExternalNotificationStatusMessage = string.Format(
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.TestSucceeded",
                "Provider `{0}` test notification sent."),
            string.Join(", ", enabledProviders.Select(MapProviderToLegacyDisplayName)));
        StatusMessage = ExternalNotificationStatusMessage;
        LastErrorMessage = string.Empty;
        await RecordEventAsync(
            "Settings.ExternalNotification.TestSend",
            ExternalNotificationStatusMessage,
            cancellationToken);
    }

    public async Task SaveExternalNotificationAsync(CancellationToken cancellationToken = default)
    {
        ClearExternalNotificationStatus();
        PersistCurrentProviderParameters();
        var enabledProviders = ResolveEnabledNotificationProviders();

        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.ExternalNotificationEnabled] = BuildEnabledProviderConfigValue(enabledProviders),
            [ConfigurationKeys.ExternalNotificationSendWhenComplete] = ExternalNotificationSendWhenComplete.ToString(),
            [ConfigurationKeys.ExternalNotificationSendWhenError] = ExternalNotificationSendWhenError.ToString(),
            [ConfigurationKeys.ExternalNotificationSendWhenTimeout] = ExternalNotificationSendWhenTimeout.ToString(),
            [ConfigurationKeys.ExternalNotificationEnableDetails] = ExternalNotificationEnableDetails.ToString(),
        };

        var applyProviderResult = await PopulateExternalNotificationProviderUpdatesAsync(
            updates,
            enabledProviders,
            validateParameters: enabledProviders.Count > 0,
            cancellationToken);
        if (!applyProviderResult.Success)
        {
            await ApplyExternalNotificationFailure(
                applyProviderResult,
                enabledProviders.Count > 0
                    ? "Settings.ExternalNotification.Save.Validate"
                    : "Settings.ExternalNotification.Save.Disabled",
                cancellationToken);
            return;
        }

        var saveResult = await SaveScopedSettingsAsync(
            profileUpdates: updates,
            successScope: "Settings.ExternalNotification.Save",
            cancellationToken: cancellationToken);
        if (!saveResult.Success)
        {
            await ApplyExternalNotificationFailure(saveResult, "Settings.ExternalNotification.Save", cancellationToken);
            return;
        }

        ExternalNotificationStatusMessage = RootTexts.GetOrDefault(
            "Settings.ExternalNotification.Status.SaveSucceeded",
            "External notification settings saved.");
        ExternalNotificationErrorMessage = string.Empty;
        ExternalNotificationWarningMessage = string.Empty;
        StatusMessage = ExternalNotificationStatusMessage;
        LastErrorMessage = string.Empty;
        await RecordEventAsync(
            "Settings.ExternalNotification.Save",
            ExternalNotificationStatusMessage,
            cancellationToken);
    }

    private string NormalizeNotificationProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return AvailableNotificationProviders.Count > 0
                ? AvailableNotificationProviders[0]
                : DefaultNotificationProviders[0];
        }

        var normalized = provider.Trim();
        foreach (var candidate in AvailableNotificationProviders)
        {
            if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return AvailableNotificationProviders.Count > 0
            ? AvailableNotificationProviders[0]
            : DefaultNotificationProviders[0];
    }

    private async Task EnsureNotificationProvidersLoadedAsync(CancellationToken cancellationToken)
    {
        string[] providers;
        try
        {
            providers = await Runtime.NotificationProviderFeatureService.GetAvailableProvidersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            providers = DefaultNotificationProviders;
            await RecordUnhandledExceptionAsync(
                "Settings.ExternalNotification.Providers",
                ex,
                UiErrorCode.NotificationProviderFailed,
                $"Failed to load provider list. Falling back to defaults: {ex.Message}",
                cancellationToken);
        }

        var normalizedProviders = providers
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedProviders.Count == 0)
        {
            normalizedProviders = [.. DefaultNotificationProviders];
        }

        AvailableNotificationProviders.Clear();
        NotificationProviderSelections.Clear();
        foreach (var provider in normalizedProviders)
        {
            AvailableNotificationProviders.Add(provider);
            if (!_notificationProviderParameters.ContainsKey(provider))
            {
                _notificationProviderParameters[provider] = string.Empty;
            }
            EnsureProviderParameterMap(provider);

            var selection = new NotificationProviderSelectionItem(provider, MapProviderToLegacyDisplayName(provider));
            selection.PropertyChanged += OnNotificationProviderSelectionPropertyChanged;
            NotificationProviderSelections.Add(selection);
        }

        SynchronizeNotificationProviderSelectionsFromEnabledSet();
        _selectedNotificationProvider = NormalizeNotificationProvider(_selectedNotificationProvider);
        var current = _notificationProviderParameters.TryGetValue(_selectedNotificationProvider, out var stored)
            ? stored
            : string.Empty;
        UpdateProviderParameterMapFromText(_selectedNotificationProvider, current, markDirty: false);
        OnPropertyChanged(nameof(SelectedNotificationProvider));
    }

    private void PersistCurrentProviderParameters()
    {
        if (!string.IsNullOrWhiteSpace(SelectedNotificationProvider))
        {
            _notificationProviderParameters[SelectedNotificationProvider] = NotificationProviderParametersText;
        }
    }

    private Dictionary<string, string> EnsureProviderParameterMap(string provider)
    {
        if (!_providerParameterValues.TryGetValue(provider, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _providerParameterValues[provider] = map;
        }

        return map;
    }

    private string GetProviderParameterValue(string provider, string key)
    {
        if (_providerParameterValues.TryGetValue(provider, out var map)
            && map.TryGetValue(key, out var value))
        {
            return value;
        }

        return string.Empty;
    }

    private bool TryGetProviderParameterBool(string provider, string key, out bool value)
    {
        var text = GetProviderParameterValue(provider, key);
        if (bool.TryParse(text, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = false;
        return false;
    }

    private void SetProviderParameterValue(string provider, string key, string? value)
    {
        var map = EnsureProviderParameterMap(provider);
        if (string.IsNullOrWhiteSpace(value))
        {
            map.Remove(key);
        }
        else
        {
            map[key] = value.Trim();
        }

        ApplyProviderParameterMapChanged(provider, markDirty: true, notifyFieldProperties: false);
    }

    private string BuildProviderParameterTextFromValues(string provider)
    {
        if (!ProviderConfigKeyMap.TryGetValue(provider, out var keyMap))
        {
            if (!_providerParameterValues.TryGetValue(provider, out var fallback))
            {
                return string.Empty;
            }

            return string.Join(
                Environment.NewLine,
                fallback
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        var map = EnsureProviderParameterMap(provider);
        var lines = new List<string>();
        foreach (var parameterKey in keyMap.Keys)
        {
            if (map.TryGetValue(parameterKey, out var stored) && !string.IsNullOrWhiteSpace(stored))
            {
                lines.Add($"{parameterKey}={stored}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ApplyProviderParameterMapChanged(string provider, bool markDirty, bool notifyFieldProperties)
    {
        var text = BuildProviderParameterTextFromValues(provider);
        _notificationProviderParameters[provider] = text;

        if (!_isApplyingProviderParametersText
            && string.Equals(provider, _selectedNotificationProvider, StringComparison.OrdinalIgnoreCase))
        {
            SetNotificationProviderParametersTextField(text);
        }

        if (notifyFieldProperties)
        {
            RefreshProviderFieldsForProvider(provider);
        }

        if (markDirty && !_suppressProviderParameterDirtyTracking)
        {
            MarkExternalNotificationDirty();
        }
    }

    private void RefreshProviderFieldsForProvider(string provider)
    {
        if (!ProviderFieldPropertyMap.TryGetValue(provider, out var properties))
        {
            return;
        }

        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }

    private void RefreshExternalNotificationSectionVisibility()
    {
        foreach (var propertyName in ExternalNotificationSectionPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void UpdateProviderParameterMapFromText(string provider, string text, bool markDirty)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }

        _isApplyingProviderParametersText = true;
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (TryParseProviderParameterText(text, out var parsed, out _))
            {
                foreach (var (key, value) in parsed)
                {
                    map[key] = value;
                }
            }

            _providerParameterValues[provider] = map;
            ApplyProviderParameterMapChanged(provider, markDirty, notifyFieldProperties: true);
        }
        finally
        {
            _isApplyingProviderParametersText = false;
        }
    }

    private void SetNotificationProviderParametersTextField(string text)
    {
        if (_notificationProviderParametersText != text)
        {
            _notificationProviderParametersText = text;
            OnPropertyChanged(nameof(NotificationProviderParametersText));
        }
    }

    private void LoadExternalNotificationProviderParametersFromConfig(UnifiedConfig config)
    {
        _notificationProviderParameters.Clear();
        _suppressProviderParameterDirtyTracking = true;
        try
        {
            foreach (var provider in AvailableNotificationProviders)
            {
                var parameterText = BuildProviderParameterTextFromConfig(provider, config);
                _notificationProviderParameters[provider] = parameterText;
                UpdateProviderParameterMapFromText(provider, parameterText, markDirty: false);
            }
        }
        finally
        {
            _suppressProviderParameterDirtyTracking = false;
        }

        var rawEnabledConfig = ReadProfileString(config, ConfigurationKeys.ExternalNotificationEnabled, string.Empty);
        var enabledProviders = ParseEnabledProviders(rawEnabledConfig);
        if (enabledProviders.Count == 0 && ExternalNotificationEnabled && AvailableNotificationProviders.Count > 0)
        {
            enabledProviders.Add(NormalizeNotificationProvider(_selectedNotificationProvider));
        }

        _enabledNotificationProviders.Clear();
        foreach (var provider in enabledProviders)
        {
            _enabledNotificationProviders.Add(provider);
        }

        ExternalNotificationEnabled = _enabledNotificationProviders.Count > 0;
        ExternalNotificationEnableDetails = ReadProfileBool(
            config,
            ConfigurationKeys.ExternalNotificationEnableDetails,
            false);
        SynchronizeNotificationProviderSelectionsFromEnabledSet();

        var selected = NormalizeNotificationProvider(_selectedNotificationProvider);
        if (_enabledNotificationProviders.Count > 0 && !_enabledNotificationProviders.Contains(selected))
        {
            selected = _enabledNotificationProviders.First();
        }

        _selectedNotificationProvider = selected;
        var text = _notificationProviderParameters.TryGetValue(selected, out var storedText)
            ? storedText
            : string.Empty;
        UpdateProviderParameterMapFromText(selected, text, markDirty: false);
        OnPropertyChanged(nameof(SelectedNotificationProvider));
    }

    private static string BuildProviderParameterTextFromConfig(string provider, UnifiedConfig config)
    {
        if (!ProviderConfigKeyMap.TryGetValue(provider, out var keyMap))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var (parameterKey, configKey) in keyMap)
        {
            if (!TryGetConfigNode(config, configKey, ConfigValuePreference.ProfileFirst, out var node) || node is null)
            {
                continue;
            }

            var value = node is JsonValue jsonValue
                ? jsonValue.ToString()
                : node.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            lines.Add($"{parameterKey}={value.Trim()}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool TryParseProviderParameterText(
        string? text,
        out Dictionary<string, string> parameters,
        out string? error)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                error = string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.ParameterFormatLine",
                        "Invalid parameter format (line {0}). Expected key=value: `{1}`"),
                    i + 1,
                    line);
                return false;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                error = string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.ParameterKeyEmpty",
                        "Invalid parameter format (line {0}). Key cannot be empty."),
                    i + 1);
                return false;
            }

            parameters[key] = value;
        }

        return true;
    }

    private async Task<UiOperationResult> PopulateExternalNotificationProviderUpdatesAsync(
        Dictionary<string, string> updates,
        IReadOnlyCollection<string> enabledProviders,
        bool validateParameters,
        CancellationToken cancellationToken)
    {
        foreach (var provider in AvailableNotificationProviders)
        {
            if (!ProviderConfigKeyMap.TryGetValue(provider, out var keyMap))
            {
                continue;
            }

            var parameterText = _notificationProviderParameters.TryGetValue(provider, out var stored)
                ? stored
                : string.Empty;

            if (string.IsNullOrWhiteSpace(parameterText))
            {
                foreach (var (_, configKey) in keyMap)
                {
                    updates[configKey] = string.Empty;
                }

                continue;
            }

            if (validateParameters && enabledProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                var validate = await Runtime.NotificationProviderFeatureService.ValidateProviderParametersAsync(
                    new NotificationProviderRequest(provider, parameterText),
                    cancellationToken);
                if (!validate.Success)
                {
                    return validate;
                }
            }

            if (!TryParseProviderParameterText(parameterText, out var parsed, out var parseError))
            {
                if (!validateParameters)
                {
                    continue;
                }

                return UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderInvalidParameters,
                    parseError
                    ?? RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.ParseFailed",
                        "Failed to parse provider parameters."));
            }

            foreach (var (parameterKey, configKey) in keyMap)
            {
                updates[configKey] = parsed.TryGetValue(parameterKey, out var value)
                    ? value
                    : string.Empty;
            }
        }

        return UiOperationResult.Ok(
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.PreparedUpdates",
                "Prepared external notification provider updates."));
    }

    public async Task OpenAboutBilibiliAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutBilibiliUrl,
            "Settings.About.OpenBilibili",
            "Settings.About.Status.OpenBilibiliSucceeded",
            "Opened bilibili.",
            cancellationToken);
    }

    public async Task OpenAboutGithubAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutGithubRepositoryUrl,
            "Settings.About.OpenGithub",
            "Settings.About.Status.OpenGithubSucceeded",
            "Opened GitHub repository.",
            cancellationToken);
    }

    public async Task OpenAboutQqGroupAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutQqGroupUrl,
            "Settings.About.OpenQqGroup",
            "Settings.About.Status.OpenQqGroupSucceeded",
            "Opened QQ group page.",
            cancellationToken);
    }

    public async Task OpenAboutQqChannelAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutQqChannelUrl,
            "Settings.About.OpenQqChannel",
            "Settings.About.Status.OpenQqChannelSucceeded",
            "Opened QQ channel page.",
            cancellationToken);
    }

    public async Task OpenAboutTelegramAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutTelegramUrl,
            "Settings.About.OpenTelegram",
            "Settings.About.Status.OpenTelegramSucceeded",
            "Opened Telegram.",
            cancellationToken);
    }

    public async Task OpenAboutDiscordAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutDiscordUrl,
            "Settings.About.OpenDiscord",
            "Settings.About.Status.OpenDiscordSucceeded",
            "Opened Discord.",
            cancellationToken);
    }

    public async Task CheckAndDownloadAboutAnnouncementWithDialogAsync(CancellationToken cancellationToken = default)
    {
        ClearAboutStatus();
        var loadResult = await Runtime.AnnouncementFeatureService.LoadStateAsync(cancellationToken);
        var state = await ApplyResultAsync(loadResult, "Settings.About.CheckAnnouncement", cancellationToken);
        if (state is null)
        {
            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.AnnouncementLoadFailed",
                "公告读取失败。");
            AboutErrorMessage = loadResult.Message;
            return;
        }

        var fetchResult = await FetchLatestAnnouncementInfoAsync(cancellationToken);
        if (fetchResult.Success
            && fetchResult.Value is AnnouncementFetchResult fetched
            && !string.IsNullOrWhiteSpace(fetched.Content))
        {
            var remoteInfo = fetched.Content.Trim();
            if (!string.Equals(remoteInfo, state.AnnouncementInfo, StringComparison.Ordinal))
            {
                state = state.WithFetchedAnnouncement(remoteInfo, fetched.SourceUri, DateTimeOffset.UtcNow);
                var saveFetchedState = await Runtime.AnnouncementFeatureService.SaveStateAsync(state, cancellationToken);
                if (!await ApplyResultAsync(saveFetchedState, "Settings.About.Announcement.SaveFetched", cancellationToken))
                {
                    AboutStatusMessage = LocalizeSettingsText(
                        "Settings.About.Status.AnnouncementSaveFailed",
                        "公告状态保存失败。");
                    AboutErrorMessage = saveFetchedState.Message;
                    return;
                }
            }
        }

        var announcementChrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.About.Dialog.Title", "Announcement"),
                confirmText: texts.GetOrDefault("Settings.About.Dialog.Confirm", "Confirm"),
                cancelText: texts.GetOrDefault("Settings.About.Dialog.Cancel", "Cancel")));
        var announcementChromeSnapshot = announcementChrome.GetSnapshot();
        var request = new AnnouncementDialogRequest(
            Title: announcementChromeSnapshot.Title,
            AnnouncementInfo: state.AnnouncementInfo,
            DoNotRemindThisAnnouncementAgain: state.DoNotRemindThisAnnouncementAgain,
            DoNotShowAnnouncement: state.DoNotShowAnnouncement,
            ConfirmText: announcementChromeSnapshot.ConfirmText ?? LocalizeSettingsText("Settings.About.Dialog.Confirm", "Confirm"),
            CancelText: announcementChromeSnapshot.CancelText ?? LocalizeSettingsText("Settings.About.Dialog.Cancel", "Cancel"),
            Chrome: announcementChrome);
        var dialogResult = await _dialogService.ShowAnnouncementAsync(request, "Settings.About.Announcement.Dialog", cancellationToken);
        if (dialogResult.Return == DialogReturnSemantic.Confirm && dialogResult.Payload is not null)
        {
            var nextState = state with
            {
                AnnouncementInfo = dialogResult.Payload.AnnouncementInfo,
                DoNotRemindThisAnnouncementAgain = dialogResult.Payload.DoNotRemindThisAnnouncementAgain,
                DoNotShowAnnouncement = dialogResult.Payload.DoNotShowAnnouncement,
            };
            var saveResult = await Runtime.AnnouncementFeatureService.SaveStateAsync(nextState, cancellationToken);
            if (!await ApplyResultAsync(saveResult, "Settings.About.Announcement.Save", cancellationToken))
            {
                AboutStatusMessage = LocalizeSettingsText(
                    "Settings.About.Status.AnnouncementSaveFailed",
                    "公告状态保存失败。");
                AboutErrorMessage = saveResult.Message;
                return;
            }

            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.AnnouncementSaveSucceeded",
                "公告状态已保存。");
            AboutErrorMessage = string.Empty;
            return;
        }

        AboutStatusMessage = dialogResult.Return == DialogReturnSemantic.Cancel
            ? LocalizeSettingsText(
                "Settings.About.Status.AnnouncementDialogCancelled",
                "公告弹窗已取消。")
            : LocalizeSettingsText(
                "Settings.About.Status.AnnouncementDialogClosed",
                "公告弹窗已关闭。");
        AboutErrorMessage = string.Empty;
    }

    private async Task<UiOperationResult<AnnouncementFetchResult>> FetchLatestAnnouncementInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidatePaths = BuildAnnouncementCandidatePaths();
        Exception? lastException = null;

        foreach (var baseUrl in AboutAnnouncementApiBaseUrls)
        {
            foreach (var relativePath in candidatePaths)
            {
                var requestUri = BuildAnnouncementUri(baseUrl, relativePath);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.CacheControl = new CacheControlHeaderValue
                    {
                        NoCache = true,
                    };

                    using var response = await AboutAnnouncementHttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return UiOperationResult<AnnouncementFetchResult>.Ok(
                            new AnnouncementFetchResult(content, requestUri),
                            "Announcement downloaded.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }
        }

        var errorMessage = lastException is null
            ? "Announcement endpoint returned no content."
            : $"Announcement download failed: {lastException.Message}";
        return UiOperationResult<AnnouncementFetchResult>.Fail(
            UiErrorCode.AnnouncementServiceUnavailable,
            errorMessage);
    }

    private IReadOnlyList<string> BuildAnnouncementCandidatePaths()
    {
        if (Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyAnnouncementPaths;
        }

        return [LegacyAnnouncementPaths[1], LegacyAnnouncementPaths[0]];
    }

    private static Uri BuildAnnouncementUri(string baseUrl, string relativePath)
    {
        var normalizedBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : $"{baseUrl}/";
        return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), relativePath);
    }

    private void OnNotificationProviderSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotificationProviderSelectionItem item
            || !string.Equals(e.PropertyName, nameof(NotificationProviderSelectionItem.IsEnabled), StringComparison.Ordinal))
        {
            return;
        }

        if (_suppressNotificationProviderSelectionEvents)
        {
            return;
        }

        if (item.IsEnabled)
        {
            _enabledNotificationProviders.Add(item.Provider);
            if (string.IsNullOrWhiteSpace(SelectedNotificationProvider))
            {
                SelectedNotificationProvider = item.Provider;
            }
        }
        else
        {
            _enabledNotificationProviders.Remove(item.Provider);
            if (_enabledNotificationProviders.Count > 0
                && string.Equals(SelectedNotificationProvider, item.Provider, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNotificationProvider = _enabledNotificationProviders.First();
            }
        }

        ExternalNotificationEnabled = _enabledNotificationProviders.Count > 0;
        MarkExternalNotificationDirty();
        RefreshExternalNotificationSectionVisibility();
    }

    private void SynchronizeNotificationProviderSelectionsFromEnabledSet()
    {
        _suppressNotificationProviderSelectionEvents = true;
        try
        {
            foreach (var selection in NotificationProviderSelections)
            {
                selection.IsEnabled = _enabledNotificationProviders.Contains(selection.Provider);
            }
        }
        finally
        {
            _suppressNotificationProviderSelectionEvents = false;
        }
    }

    private HashSet<string> ParseEnabledProviders(string rawValue)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return enabled;
        }

        foreach (var segment in rawValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (NotificationProviderCanonicalNameMap.TryGetValue(segment, out var canonical))
            {
                enabled.Add(canonical);
            }
        }

        return enabled;
    }

    private static string MapProviderToLegacyDisplayName(string provider)
    {
        return NotificationProviderLegacyDisplayNameMap.TryGetValue(provider, out var displayName)
            ? displayName
            : provider;
    }

    private List<string> ResolveEnabledNotificationProviders()
    {
        if (!ExternalNotificationEnabled)
        {
            _enabledNotificationProviders.Clear();
            SynchronizeNotificationProviderSelectionsFromEnabledSet();
            return [];
        }

        foreach (var provider in AvailableNotificationProviders)
        {
            if (_notificationProviderParameters.TryGetValue(provider, out var parameterText)
                && !string.IsNullOrWhiteSpace(parameterText))
            {
                _enabledNotificationProviders.Add(provider);
            }
        }

        var enabledProviders = _enabledNotificationProviders
            .Where(provider => AvailableNotificationProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabledProviders.Count == 0 && ExternalNotificationEnabled && !string.IsNullOrWhiteSpace(SelectedNotificationProvider))
        {
            enabledProviders.Add(SelectedNotificationProvider);
            _enabledNotificationProviders.Add(SelectedNotificationProvider);
            SynchronizeNotificationProviderSelectionsFromEnabledSet();
        }

        enabledProviders.Sort((left, right) =>
        {
            var leftIndex = AvailableNotificationProviders.IndexOf(left);
            var rightIndex = AvailableNotificationProviders.IndexOf(right);
            return leftIndex.CompareTo(rightIndex);
        });
        ExternalNotificationEnabled = enabledProviders.Count > 0;
        return enabledProviders;
    }

    private static string BuildEnabledProviderConfigValue(IEnumerable<string> enabledProviders)
    {
        return string.Join(
            ",",
            enabledProviders
                .Select(MapProviderToLegacyDisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string FormatRemoteControlMessage(string? code, string fallbackMessage)
    {
        return code switch
        {
            UiErrorCode.RemoteControlInvalidParameters => string.Format(
                RootTexts.GetOrDefault(
                    "Settings.RemoteControl.Error.InvalidParameters",
                    "Remote control parameter error: {0} ({1})"),
                fallbackMessage,
                UiErrorCode.RemoteControlInvalidParameters),
            UiErrorCode.RemoteControlNetworkFailure => string.Format(
                RootTexts.GetOrDefault(
                    "Settings.RemoteControl.Error.NetworkFailure",
                    "Remote control connectivity failed: {0} ({1})"),
                fallbackMessage,
                UiErrorCode.RemoteControlNetworkFailure),
            UiErrorCode.RemoteControlUnsupported => string.Format(
                RootTexts.GetOrDefault(
                    "Settings.RemoteControl.Error.Unsupported",
                    "Remote control connectivity test is unsupported in this environment: {0} ({1})"),
                fallbackMessage,
                UiErrorCode.RemoteControlUnsupported),
            _ => fallbackMessage,
        };
    }

    private static bool ContainsInvalidRemoteIdentity(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static RemoteControlConnectivityResult? ParseRemoteConnectivityDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteControlConnectivityResult>(details);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildRemoteConnectivitySummary(RemoteControlConnectivityResult? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        return $"GetTask={result.GetTaskProbe.Message}; Report={result.ReportProbe.Message}; Poll={result.PollIntervalMs}ms";
    }

    private string FormatExternalNotificationMessage(string? code, string fallbackMessage)
    {
        return code switch
        {
            UiErrorCode.NotificationProviderInvalidParameters
                => string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.InvalidParameters",
                        "External notification parameter error: {0} ({1})"),
                    fallbackMessage,
                    UiErrorCode.NotificationProviderInvalidParameters),
            UiErrorCode.NotificationProviderNetworkFailure
                => string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.NetworkFailure",
                        "External notification network failure: {0} ({1})"),
                    fallbackMessage,
                    UiErrorCode.NotificationProviderNetworkFailure),
            UiErrorCode.NotificationProviderUnsupported
                => string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.Unsupported",
                        "External notification is unsupported in this environment: {0} ({1})"),
                    fallbackMessage,
                    UiErrorCode.NotificationProviderUnsupported),
            _ => fallbackMessage,
        };
    }

    private async Task ApplyExternalNotificationFailure(
        UiOperationResult result,
        string scope,
        CancellationToken cancellationToken)
    {
        var message = FormatExternalNotificationMessage(result.Error?.Code, result.Message);
        if (string.Equals(result.Error?.Code, UiErrorCode.NotificationProviderUnsupported, StringComparison.Ordinal))
        {
            ExternalNotificationWarningMessage = message;
            ExternalNotificationErrorMessage = string.Empty;
        }
        else
        {
            ExternalNotificationErrorMessage = message;
            ExternalNotificationWarningMessage = string.Empty;
        }

        ExternalNotificationStatusMessage = RootTexts.GetOrDefault(
            "Settings.ExternalNotification.Status.OperationFailed",
            "External notification operation failed.");
        LastErrorMessage = message;
        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(result.Error?.Code ?? UiErrorCode.NotificationProviderFailed, message, result.Error?.Details),
            cancellationToken);
    }

    private sealed record AnnouncementFetchResult(string Content, Uri SourceUri);

    public sealed class NotificationProviderSelectionItem : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public NotificationProviderSelectionItem(string provider, string displayName)
        {
            Provider = provider;
            DisplayName = displayName;
        }

        public string Provider { get; }

        public string DisplayName { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
