using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
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
using MAAUnified.Application.Services.Notifications;
using MAAUnified.Application.Services.VersionUpdate;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel
{
    private static readonly TimeSpan DefaultAboutAnnouncementTimeout = TimeSpan.FromSeconds(5);

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
            ["DiscordWebhook"] = "Discord Webhook",
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
            // WPF f9c14e0be1 removed the ｢Discord Webhook｣ channel and migrates its URL into a
            // Custom Webhook channel, so a stored CSV entry resolves to CustomWebhook.
            ["Discord Webhook"] = "CustomWebhook",
            ["DiscordWebhook"] = "CustomWebhook",
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

    public async Task RetrySecureStorageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Runtime.ConfigurationService.RetrySecureStorageAsync(cancellationToken);
            OnPropertyChanged(nameof(CanUseSensitiveSettings));
            OnPropertyChanged(nameof(HasSensitiveStorageWarning));
            OnPropertyChanged(nameof(CanRetrySecureStorage));
            OnPropertyChanged(nameof(SensitiveStorageStatusText));
        }
        catch (Exception ex)
        {
            Runtime.LogService.Warn($"Sensitive storage retry failed: errorType={ex.GetType().Name}.");
        }
    }

    public async Task ConfirmDiscardUnreadableSensitiveValuesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDiscardUnreadableSensitiveValues)
        {
            return;
        }

        var chrome = CreateSettingsDialogChrome(texts => new DialogChromeSnapshot(
            title: "Discard unreadable sensitive value?",
            confirmText: "Discard and re-enter",
            cancelText: "Keep original config"));
        var result = await _dialogService.ShowWarningConfirmAsync(
            new WarningConfirmDialogRequest(
                Title: "Discard unreadable sensitive value?",
                Message: "The encrypted legacy value cannot be recovered on this system. Confirming permanently discards that unreadable value on the next save and enables the affected fields so you can re-enter it. Until then, the original config file remains unchanged.",
                ConfirmText: "Discard and re-enter",
                CancelText: "Keep original config",
                Language: Language,
                Chrome: chrome),
            "Settings.SensitiveStorage.DiscardUnreadable.Dialog",
            cancellationToken);
        if (result.Return != DialogReturnSemantic.Confirm)
        {
            return;
        }

        await Runtime.ConfigurationService.ConfirmDiscardUnreadableLegacySensitiveValuesAsync(cancellationToken);
        OnPropertyChanged(nameof(CanUseSensitiveSettings));
        OnPropertyChanged(nameof(HasSensitiveStorageWarning));
        OnPropertyChanged(nameof(CanDiscardUnreadableSensitiveValues));
        OnPropertyChanged(nameof(SensitiveStorageStatusText));
    }

    public async Task SaveRemoteControlAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.Remote",
            SaveRemoteControlCoreAsync,
            cancellationToken);
    }

    private async Task SaveRemoteControlCoreAsync(CancellationToken cancellationToken = default)
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
            await ApplyNoExternalNotificationProviderFailureAsync(
                "Settings.ExternalNotification.Validate",
                cancellationToken);
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
                "{0} channel parameters validated."),
            BuildExternalNotificationProviderListText(enabledProviders));
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
            await ApplyNoExternalNotificationProviderFailureAsync(
                "Settings.ExternalNotification.TestSend",
                cancellationToken);
            return;
        }

        var failedProviders = new List<(string Provider, UiOperationResult Result)>();
        foreach (var provider in enabledProviders)
        {
            UiOperationResult result;
            try
            {
                var parameterText = _notificationProviderParameters.TryGetValue(provider, out var storedParameters)
                    ? storedParameters
                    : string.Empty;
                result = await Runtime.NotificationProviderFeatureService.SendTestAsync(
                    new NotificationProviderTestRequest(
                        provider,
                        parameterText,
                        NotificationTitle,
                        NotificationMessage),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // WPF frozen ExternalNotificationService wraps each provider send in a
                // per-provider try/catch and continues the loop with a failure toast;
                // a single throwing provider must not abort the remaining test sends.
                result = UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderFailed,
                    $"Notification test failed via `{provider}`.",
                    $"errorType={ex.GetType().Name}");
            }

            if (!result.Success)
            {
                failedProviders.Add((provider, result));
            }
        }

        if (failedProviders.Count == 1)
        {
            // Preserve the original error classification for single-provider failures.
            await ApplyExternalNotificationFailure(
                failedProviders[0].Result,
                "Settings.ExternalNotification.TestSend",
                cancellationToken);
            return;
        }

        if (failedProviders.Count > 1)
        {
            var distinctCodes = failedProviders
                .Select(failure => failure.Result.Error?.Code)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var aggregatedCode = distinctCodes.Count == 1
                ? distinctCodes[0] ?? UiErrorCode.NotificationProviderNetworkFailure
                : UiErrorCode.NotificationProviderNetworkFailure;
            var aggregatedMessage = string.Join(
                Environment.NewLine,
                failedProviders.Select(failure =>
                    $"{MapProviderToLegacyDisplayName(failure.Provider)}: {FormatExternalNotificationMessage(failure.Result.Error?.Code, failure.Result.Message)}"));
            await ApplyExternalNotificationFailure(
                UiOperationResult.Fail(aggregatedCode, aggregatedMessage),
                "Settings.ExternalNotification.TestSend",
                cancellationToken);
            return;
        }

        ExternalNotificationStatusMessage = string.Format(
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.TestSucceeded",
                "{0} channel test notification sent."),
            BuildExternalNotificationProviderListText(enabledProviders));
        StatusMessage = ExternalNotificationStatusMessage;
        LastErrorMessage = string.Empty;
        await RecordEventAsync(
            "Settings.ExternalNotification.TestSend",
            ExternalNotificationStatusMessage,
            cancellationToken);
    }

    public async Task SaveExternalNotificationAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.Notification",
            SaveExternalNotificationCoreAsync,
            cancellationToken);
    }

    private async Task SaveExternalNotificationCoreAsync(CancellationToken cancellationToken = default)
    {
        ClearExternalNotificationStatus();
        PersistCurrentProviderParameters();
        var enabledProviders = ResolveEnabledNotificationProviders();

        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.ExternalNotificationEnabled] = BuildEnabledProviderConfigValue(enabledProviders),
            [ConfigurationKeys.ExternalNotificationSendWhenComplete] = ExternalNotificationSendWhenComplete.ToString(),
            [ConfigurationKeys.ExternalNotificationSendWhenError] = ExternalNotificationSendWhenError.ToString(),
            [ConfigurationKeys.ExternalNotificationSendWhenStalled] = ExternalNotificationSendWhenStalled.ToString(),
            [ConfigurationKeys.ExternalNotificationSendBeforeScheduledStart] = ExternalNotificationSendBeforeScheduledStart.ToString(),
            // Dual-write the WPF legacy key so the toggle choice also travels back to WPF
            // (WPF frozen 23ddd271 only reads/writes ExternalNotification.SendWhenTimeout).
            // Reads stay new-key-first with the legacy key as fallback.
            [ConfigurationKeys.ExternalNotificationSendWhenTimeout] = ExternalNotificationSendWhenStalled.ToString(),
            [ConfigurationKeys.ExternalNotificationEnableDetails] = ExternalNotificationEnableDetails.ToString(),
        };

        var applyProviderResult = await PopulateExternalNotificationProviderUpdatesAsync(
            updates,
            enabledProviders,
            // WPF persists incomplete provider settings and reports errors while testing or sending.
            validateParameters: false,
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

        ExternalNotificationStatusMessage = BuildExternalNotificationSaveStatusMessage(enabledProviders);
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

        OnPropertyChanged(nameof(CanSelectExternalNotificationProvider));
        SynchronizeNotificationProviderSelectionsFromEnabledSet();
        _selectedNotificationProvider = NormalizeNotificationProvider(_selectedNotificationProvider);
        RefreshSelectedNotificationProviderText(_selectedNotificationProvider);
        OnPropertyChanged(nameof(SelectedNotificationProvider));
    }

    private void PersistCurrentProviderParameters()
    {
        if (!string.IsNullOrWhiteSpace(SelectedNotificationProvider))
        {
            _notificationProviderParameters[SelectedNotificationProvider] = NotificationProviderParametersText;
        }
    }

    private void RefreshSelectedNotificationProviderText(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            SetNotificationProviderParametersTextField(string.Empty);
            return;
        }

        var stored = _notificationProviderParameters.TryGetValue(provider, out var existing)
            ? existing
            : string.Empty;
        UpdateProviderParameterMapFromText(provider, stored, markDirty: false);
        SetNotificationProviderParametersTextField(
            _notificationProviderParameters.TryGetValue(provider, out var normalizedText)
                ? normalizedText
                : stored);
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
            map[key] = string.Equals(key, "body", StringComparison.OrdinalIgnoreCase)
                ? value
                : value.Trim();
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
        RefreshSelectedNotificationProviderText(selected);
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

            var serialized = string.Equals(parameterKey, "body", StringComparison.OrdinalIgnoreCase)
                ? value
                : value.Trim();
            lines.Add($"{parameterKey}={serialized}");
        }

        if (string.Equals(provider, "Bark", StringComparison.OrdinalIgnoreCase)
            && !lines.Any(line => line.StartsWith("server=", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("server=https://api.day.app");
        }

        if (string.Equals(provider, "CustomWebhook", StringComparison.OrdinalIgnoreCase))
        {
            // dev-v2 migration: a saved ｢Discord Webhook｣ URL becomes a Custom Webhook channel.
            // The body field is free-form and consumes every following line when parsed, so the
            // migrated url must be inserted before it.
            var bodyIndex = lines.FindIndex(
                static line => line.StartsWith("body=", StringComparison.OrdinalIgnoreCase));
            if (!lines.Any(static line => line.StartsWith("url=", StringComparison.OrdinalIgnoreCase)))
            {
                var legacyWebhookUrl = ReadProfileString(
                    config,
                    ConfigurationKeys.ExternalNotificationDiscordWebhookUrl,
                    string.Empty);
                if (!string.IsNullOrWhiteSpace(legacyWebhookUrl))
                {
                    lines.Insert(bodyIndex < 0 ? lines.Count : bodyIndex, $"url={legacyWebhookUrl.Trim()}");
                    if (bodyIndex >= 0)
                    {
                        bodyIndex++;
                    }
                }
            }

            if (bodyIndex < 0)
            {
                lines.Add($"body={ExternalNotificationChannels.DefaultCustomWebhookBody}");
            }
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

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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
                        "Invalid parameter line {0}; expected key=value"),
                    i + 1);
                return false;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                error = string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.ExternalNotification.Error.ParameterKeyEmpty",
                        "Invalid parameter line {0}; expected key=value"),
                    i + 1);
                return false;
            }

            if (string.Equals(key, "body", StringComparison.OrdinalIgnoreCase))
            {
                // The WPF field is free-form JSON and can legally span several lines.
                parameters[key] = string.Join("\n", lines.Skip(i))[(lines[i].IndexOf('=') + 1)..];
                break;
            }

            if (string.Equals(key, "headers", StringComparison.OrdinalIgnoreCase))
            {
                // Custom Webhook headers are `Name: value` lines and may span several
                // lines; continuation lines never look like `key=value` assignments.
                var headers = new StringBuilder(value);
                while (TryReadHeaderContinuationLine(lines, ref i, out var continuation))
                {
                    headers.Append('\n').Append(continuation);
                }

                parameters[key] = headers.ToString();
                continue;
            }

            parameters[key] = value;
        }

        return true;
    }

    private static bool TryReadHeaderContinuationLine(string[] lines, ref int index, out string continuation)
    {
        continuation = string.Empty;
        var lookahead = index + 1;
        while (lookahead < lines.Length && lines[lookahead].Trim().Length == 0)
        {
            lookahead++;
        }

        if (lookahead >= lines.Length)
        {
            return false;
        }

        var candidate = lines[lookahead].Trim();
        var equalIndex = candidate.IndexOf('=');
        var colonIndex = candidate.IndexOf(':');
        var looksLikeAssignment = equalIndex > 0 && (colonIndex < 0 || equalIndex < colonIndex);
        if (looksLikeAssignment)
        {
            return false;
        }

        continuation = candidate;
        index = lookahead;
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
        var latestStateResult = await LoadLatestAnnouncementStateAsync(cancellationToken);
        var state = await ApplyResultAsync(latestStateResult, "Settings.About.CheckAnnouncement", cancellationToken);
        if (state is null)
        {
            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.AnnouncementLoadFailed",
                "公告读取失败。");
            AboutErrorMessage = latestStateResult.Message;
            return;
        }

        if (!state.HasAnnouncementInfo)
        {
            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.AnnouncementInfoEmpty",
                "当前没有公告内容。");
            AboutErrorMessage = string.Empty;
            return;
        }

        var dialogResult = await ShowAnnouncementDialogCoreAsync(
            state,
            "Settings.About.Announcement.Dialog",
            cancellationToken);
        if (dialogResult.Payload is not null && dialogResult.Return != DialogReturnSemantic.Cancel)
        {
            if (!await TryPersistAnnouncementDialogPayloadAsync(
                    state,
                    dialogResult.Payload,
                    "Settings.About.Announcement.Save",
                    cancellationToken))
            {
                AboutStatusMessage = LocalizeSettingsText(
                    "Settings.About.Status.AnnouncementSaveFailed",
                    "公告状态保存失败。");
                AboutErrorMessage = LastErrorMessage;
                return;
            }

            AboutStatusMessage = string.Empty;
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

    public async Task ShowStartupAnnouncementAsync(CancellationToken cancellationToken = default)
    {
        EnsureStartupAnnouncementCompletionPending();
        try
        {
            var latestStateResult = await LoadLatestAnnouncementStateAsync(cancellationToken);
            var state = await ApplyResultAsync(latestStateResult, "App.Initialize.Announcement", cancellationToken);
            if (state is null
                || !state.HasAnnouncementInfo
                || state.DoNotRemindThisAnnouncementAgain
                || state.DoNotShowAnnouncement)
            {
                CompleteStartupAnnouncementCompletion();
                return;
            }

            var dialogTask = ShowAnnouncementDialogCoreAsync(
                state,
                "App.Initialize.Announcement.Dialog",
                cancellationToken);

            _ = FinalizeStartupAnnouncementAsync(state, dialogTask, cancellationToken);
            await Task.Yield();
        }
        catch
        {
            CompleteStartupAnnouncementCompletion();
            throw;
        }
    }

    private async Task FinalizeStartupAnnouncementAsync(
        AnnouncementState state,
        Task<DialogCompletion<AnnouncementDialogPayload>> dialogTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var dialogResult = await dialogTask;
            if (dialogResult.Payload is null || dialogResult.Return == DialogReturnSemantic.Cancel)
            {
                return;
            }

            _ = await TryPersistAnnouncementDialogPayloadAsync(
                state,
                dialogResult.Payload,
                "App.Initialize.Announcement.Save",
                cancellationToken);
        }
        finally
        {
            CompleteStartupAnnouncementCompletion();
        }
    }

    private async Task<UiOperationResult<AnnouncementState>> LoadLatestAnnouncementStateAsync(CancellationToken cancellationToken)
    {
        var loadResult = await Runtime.AnnouncementFeatureService.LoadStateAsync(cancellationToken);
        if (!loadResult.Success || loadResult.Value is null)
        {
            return UiOperationResult<AnnouncementState>.Fail(
                loadResult.Error?.Code ?? UiErrorCode.AnnouncementServiceUnavailable,
                loadResult.Message,
                loadResult.Error?.Details);
        }

        var state = loadResult.Value.ForLanguage(Language);
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
                if (!saveFetchedState.Success)
                {
                    return UiOperationResult<AnnouncementState>.Fail(
                        saveFetchedState.Error?.Code ?? UiErrorCode.AnnouncementServiceUnavailable,
                        saveFetchedState.Message,
                        saveFetchedState.Error?.Details);
                }
            }

            return UiOperationResult<AnnouncementState>.Ok(state, "Announcement state refreshed.");
        }

        if (state.HasAnnouncementInfo)
        {
            return UiOperationResult<AnnouncementState>.Ok(
                state,
                fetchResult.Success
                    ? "Loaded cached announcement state."
                    : "Loaded cached announcement state after refresh failure.");
        }

        if (!fetchResult.Success)
        {
            return UiOperationResult<AnnouncementState>.Fail(
                fetchResult.Error?.Code ?? UiErrorCode.AnnouncementServiceUnavailable,
                fetchResult.Message,
                fetchResult.Error?.Details);
        }

        return UiOperationResult<AnnouncementState>.Ok(state, "Announcement state loaded with empty content.");
    }

    private async Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementDialogCoreAsync(
        AnnouncementState state,
        string sourceScope,
        CancellationToken cancellationToken)
    {
        var request = BuildAnnouncementDialogRequest(state);
        return await _dialogService.ShowAnnouncementAsync(request, sourceScope, cancellationToken);
    }

    private AnnouncementDialogRequest BuildAnnouncementDialogRequest(AnnouncementState state)
    {
        var announcementChrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.About.Dialog.Title", "Announcement"),
                confirmText: texts.GetOrDefault("Settings.About.Dialog.Confirm", "Confirm"),
                cancelText: texts.GetOrDefault("Settings.About.Dialog.Cancel", "Cancel")));
        var announcementChromeSnapshot = announcementChrome.GetSnapshot();
        return new AnnouncementDialogRequest(
            Title: announcementChromeSnapshot.Title,
            AnnouncementInfo: state.AnnouncementInfo,
            DoNotRemindThisAnnouncementAgain: state.DoNotRemindThisAnnouncementAgain,
            DoNotShowAnnouncement: state.DoNotShowAnnouncement,
            ConfirmText: announcementChromeSnapshot.ConfirmText ?? LocalizeSettingsText("Settings.About.Dialog.Confirm", "Confirm"),
            CancelText: announcementChromeSnapshot.CancelText ?? LocalizeSettingsText("Settings.About.Dialog.Cancel", "Cancel"),
            Chrome: announcementChrome);
    }

    private async Task<bool> TryPersistAnnouncementDialogPayloadAsync(
        AnnouncementState currentState,
        AnnouncementDialogPayload payload,
        string saveScope,
        CancellationToken cancellationToken)
    {
        var nextState = currentState.WithDialogPreferences(
            payload.DoNotRemindThisAnnouncementAgain,
            payload.DoNotShowAnnouncement);
        var saveResult = await Runtime.AnnouncementFeatureService.SaveStateAsync(nextState, cancellationToken);
        if (!await ApplyResultAsync(saveResult, saveScope, cancellationToken))
        {
            LastErrorMessage = saveResult.Message;
            return false;
        }

        LastErrorMessage = string.Empty;
        return true;
    }

    private async Task<UiOperationResult<AnnouncementFetchResult>> FetchLatestAnnouncementInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidatePaths = BuildAnnouncementCandidatePaths();
        using var httpClientLease = await ResolveAboutAnnouncementHttpClientAsync(cancellationToken);
        Exception? lastException = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_aboutAnnouncementTimeout);
        var requestCancellationToken = timeoutCts.Token;

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

                    using var response = await httpClientLease.Client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(requestCancellationToken);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return UiOperationResult<AnnouncementFetchResult>.Ok(
                            new AnnouncementFetchResult(content, requestUri),
                            "Announcement downloaded.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex) when (requestCancellationToken.IsCancellationRequested)
                {
                    lastException = ex;
                    return UiOperationResult<AnnouncementFetchResult>.Fail(
                        UiErrorCode.AnnouncementServiceUnavailable,
                        $"Announcement download timed out after {_aboutAnnouncementTimeout.TotalSeconds:0} seconds.");
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
            return [LegacyAnnouncementPaths[0]];
        }

        return [LegacyAnnouncementPaths[1]];
    }

    private static Uri BuildAnnouncementUri(string baseUrl, string relativePath)
    {
        var normalizedBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : $"{baseUrl}/";
        return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), relativePath);
    }

    private async Task<AboutAnnouncementHttpClientLease> ResolveAboutAnnouncementHttpClientAsync(
        CancellationToken cancellationToken)
    {
        if (_aboutAnnouncementHttpClient is not null)
        {
            return new AboutAnnouncementHttpClientLease(_aboutAnnouncementHttpClient, disposeClient: false);
        }

        var policyResult = await Runtime.VersionUpdateFeatureService.LoadPolicyAsync(cancellationToken);
        var policy = policyResult.Success && policyResult.Value is not null
            ? policyResult.Value
            : VersionUpdatePolicy.Default;
        return new AboutAnnouncementHttpClientLease(
            VersionUpdateHttpClientFactory.Create(policy, Timeout.InfiniteTimeSpan),
            disposeClient: true);
    }

    private sealed class AboutAnnouncementHttpClientLease(HttpClient client, bool disposeClient) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            if (disposeClient)
            {
                Client.Dispose();
            }
        }
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
            var hadEnabledProviders = _enabledNotificationProviders.Count > 0;
            _enabledNotificationProviders.Add(item.Provider);
            if (!hadEnabledProviders
                || !_enabledNotificationProviders.Contains(SelectedNotificationProvider))
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
            return [];
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

    private string BuildExternalNotificationSaveStatusMessage(IReadOnlyCollection<string> enabledProviders)
    {
        if (enabledProviders.Count == 0)
        {
            return RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.SaveSucceeded",
                "External notification settings saved.");
        }

        return string.Format(
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Status.SaveSucceededWithProvider",
                "{0} notification parameters validated and saved."),
            BuildExternalNotificationProviderListText(enabledProviders));
    }

    private static string BuildExternalNotificationProviderListText(IEnumerable<string> providers)
    {
        return string.Join(
            ", ",
            providers
                .Where(static provider => !string.IsNullOrWhiteSpace(provider))
                .Select(MapProviderToLegacyDisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string BuildExternalNotificationConfigurationSummary(IEnumerable<string> providers)
    {
        var summaryParts = new List<string>();
        foreach (var provider in providers
                     .Where(static provider => !string.IsNullOrWhiteSpace(provider))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var providerSummary = BuildExternalNotificationProviderSummary(provider);
            if (!string.IsNullOrWhiteSpace(providerSummary))
            {
                summaryParts.Add(providerSummary);
            }
        }

        return string.Join(" | ", summaryParts);
    }

    private string BuildExternalNotificationProviderSummary(string provider)
    {
        var summaryFields = BuildExternalNotificationProviderSummaryFields(provider);
        return summaryFields.Count == 0
            ? MapProviderToLegacyDisplayName(provider)
            : $"{MapProviderToLegacyDisplayName(provider)}: {string.Join(", ", summaryFields)}";
    }

    private List<string> BuildExternalNotificationProviderSummaryFields(string provider)
    {
        if (!_notificationProviderParameters.TryGetValue(provider, out var parameterText)
            || string.IsNullOrWhiteSpace(parameterText)
            || !TryParseProviderParameterText(parameterText, out var parsed, out _))
        {
            return [];
        }

        IEnumerable<string> orderedKeys = ProviderConfigKeyMap.TryGetValue(provider, out var keyMap)
            ? keyMap.Keys
            : parsed.Keys;
        var summaryFields = new List<string>();
        foreach (var key in orderedKeys)
        {
            if (!ShouldIncludeExternalNotificationSummaryField(provider, key)
                || !parsed.TryGetValue(key, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            summaryFields.Add($"{key}={value}");
        }

        return summaryFields;
    }

    private static bool ShouldIncludeExternalNotificationSummaryField(string provider, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return provider switch
        {
            "Smtp" => key.Equals("server", StringComparison.OrdinalIgnoreCase)
                || key.Equals("port", StringComparison.OrdinalIgnoreCase)
                || key.Equals("user", StringComparison.OrdinalIgnoreCase)
                || key.Equals("from", StringComparison.OrdinalIgnoreCase)
                || key.Equals("to", StringComparison.OrdinalIgnoreCase),
            "Bark" => key.Equals("server", StringComparison.OrdinalIgnoreCase),
            "Discord" => key.Equals("userId", StringComparison.OrdinalIgnoreCase),
            "Telegram" => key.Equals("chatId", StringComparison.OrdinalIgnoreCase)
                || key.Equals("topicId", StringComparison.OrdinalIgnoreCase),
            "Qmsg" => key.Equals("server", StringComparison.OrdinalIgnoreCase)
                || key.Equals("user", StringComparison.OrdinalIgnoreCase)
                || key.Equals("bot", StringComparison.OrdinalIgnoreCase),
            "Gotify" => key.Equals("server", StringComparison.OrdinalIgnoreCase),
            _ => key.Contains("server", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || key.Equals("from", StringComparison.OrdinalIgnoreCase)
                || key.Equals("to", StringComparison.OrdinalIgnoreCase)
                || key.Equals("user", StringComparison.OrdinalIgnoreCase),
        };
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

    private async Task ApplyNoExternalNotificationProviderFailureAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var result = UiOperationResult.Fail(
            UiErrorCode.NotificationProviderInvalidParameters,
            RootTexts.GetOrDefault(
                "Settings.ExternalNotification.Error.NoEnabledProvider",
                "Select at least one notification provider before validating or sending a test notification."));
        await ApplyExternalNotificationFailure(result, scope, cancellationToken);
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
