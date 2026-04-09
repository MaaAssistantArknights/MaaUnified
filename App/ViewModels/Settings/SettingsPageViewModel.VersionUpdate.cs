using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel
{
    public async Task SaveVersionUpdateSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (IsVersionUpdateActionRunning)
        {
            return;
        }

        IsVersionUpdateActionRunning = true;
        try
        {
            await SaveVersionUpdateChannelAsync(cancellationToken);
            if (HasVersionUpdateErrorMessage)
            {
                return;
            }

            await SaveVersionUpdateProxyAsync(cancellationToken);
            RefreshVersionUpdateSchedulerState();
        }
        finally
        {
            IsVersionUpdateActionRunning = false;
        }
    }

    public async Task SaveVersionUpdateChannelAsync(CancellationToken cancellationToken = default)
    {
        VersionUpdateStatusMessage = string.Empty;
        VersionUpdateErrorMessage = string.Empty;

        var policy = BuildVersionUpdatePolicy();
        var saveResult = await Runtime.VersionUpdateFeatureService.SaveChannelAsync(policy, cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.VersionUpdate.Channel.Save", cancellationToken))
        {
            VersionUpdateErrorMessage = saveResult.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.SaveChannelFailed",
                "Failed to save update channel settings.");
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.Status.SaveChannelSucceeded",
            "Update channel settings saved.");
        VersionUpdateErrorMessage = string.Empty;
    }

    public async Task SaveVersionUpdateProxyAsync(CancellationToken cancellationToken = default)
    {
        VersionUpdateStatusMessage = string.Empty;
        VersionUpdateErrorMessage = string.Empty;

        var policy = BuildVersionUpdatePolicy();
        var saveResult = await Runtime.VersionUpdateFeatureService.SaveProxyAsync(policy, cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.VersionUpdate.Proxy.Save", cancellationToken))
        {
            VersionUpdateErrorMessage = saveResult.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.SaveProxyFailed",
                "Failed to save update proxy settings.");
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.Status.SaveProxySucceeded",
            "Update proxy settings saved.");
        VersionUpdateErrorMessage = string.Empty;
    }

    public async Task ShowSoftwareUpdateNotImplementedAsync(CancellationToken cancellationToken = default)
    {
        if (IsVersionUpdateActionRunning)
        {
            return;
        }

        IsVersionUpdateActionRunning = true;
        VersionUpdateErrorMessage = string.Empty;

        try
        {
            SetPendingVersionUpdateAvailability(false);
            var chrome = CreateSettingsDialogChrome(
                texts => new DialogChromeSnapshot(
                    title: texts.GetOrDefault("Settings.VersionUpdate.SoftwarePlaceholder.Title", "Software Update"),
                    confirmText: texts.GetOrDefault("Settings.VersionUpdate.SoftwarePlaceholder.Confirm", "OK"),
                    cancelText: texts.GetOrDefault("Settings.VersionUpdate.SoftwarePlaceholder.Cancel", "Close")));
            var chromeSnapshot = chrome.GetSnapshot();
            var request = new WarningConfirmDialogRequest(
                Title: chromeSnapshot.Title,
                Message: RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.SoftwarePlaceholder.Message",
                    "Software update is not available in MAA Unified yet. Please use the WPF version for software updates."),
                ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.SoftwarePlaceholder.Confirm", "OK"),
                CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.SoftwarePlaceholder.Cancel", "Close"),
                Language: Language,
                Chrome: chrome);
            await _dialogService.ShowWarningConfirmAsync(
                request,
                "Settings.VersionUpdate.SoftwarePlaceholder",
                cancellationToken);
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.SoftwarePlaceholder.Status",
                "Software update is not available in MAA Unified yet.");
        }
        finally
        {
            IsVersionUpdateActionRunning = false;
        }
    }

    public async Task CheckVersionUpdateAsync(CancellationToken cancellationToken = default)
    {
        await RunVersionUpdateCheckInternalAsync(
            "Settings.VersionUpdate.Check.Manual",
            showDialog: true,
            cancellationToken);
    }

    public async Task CheckVersionUpdateWithDialogAsync(CancellationToken cancellationToken = default)
    {
        await RunVersionUpdateCheckInternalAsync(
            "Settings.VersionUpdate.Check.ManualDialog",
            showDialog: true,
            cancellationToken);
    }

    public async Task RunStartupVersionUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (_versionUpdateStartupCheckTriggered)
        {
            return;
        }

        _versionUpdateStartupCheckTriggered = true;
        if (!VersionUpdateStartupCheck)
        {
            return;
        }

        await RunVersionUpdateCheckInternalAsync(
            "Settings.VersionUpdate.Check.Startup",
            showDialog: false,
            cancellationToken);
    }

    public async Task RunScheduledVersionUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!VersionUpdateScheduledCheck)
        {
            return;
        }

        await RunVersionUpdateCheckInternalAsync(
            "Settings.VersionUpdate.Check.Scheduled",
            showDialog: false,
            cancellationToken);
    }

    public async Task ManualUpdateResourceAsync(CancellationToken cancellationToken = default)
    {
        if (IsVersionUpdateActionRunning)
        {
            return;
        }

        IsVersionUpdateActionRunning = true;
        VersionUpdateStatusMessage = string.Empty;
        VersionUpdateErrorMessage = string.Empty;

        try
        {
            var policy = BuildVersionUpdatePolicy();
            var checkOperation = await Runtime.VersionUpdateFeatureService.CheckResourceUpdateAsync(
                policy,
                ConnectionGameSharedState.ClientType,
                cancellationToken);
            var availability = await ApplyResultNoDialogAsync(
                checkOperation,
                "Settings.VersionUpdate.Resource.Check",
                cancellationToken);
            if (availability is null)
            {
                VersionUpdateErrorMessage = checkOperation.Message;
                VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.Status.ResourceUpdateFailed",
                    "Resource update failed.");
                return;
            }

            SetPendingResourceUpdateAvailability(availability.IsUpdateAvailable);
            if (!availability.IsUpdateAvailable)
            {
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = string.Empty;
                return;
            }

            if (availability.RequiresMirrorChyanCdk
                && string.Equals(policy.ResourceUpdateSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(policy.MirrorChyanCdk))
            {
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = "MirrorChyan source requires a CDK.";
                return;
            }

            var updateResult = await Runtime.VersionUpdateFeatureService.UpdateResourceAsync(
                policy,
                ConnectionGameSharedState.ClientType,
                cancellationToken);
            var payload = await ApplyResultNoDialogAsync(updateResult, "Settings.VersionUpdate.Resource.Update", cancellationToken);
            if (payload is null)
            {
                VersionUpdateErrorMessage = updateResult.Message;
                VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.Status.ResourceUpdateFailed",
                    "Resource update failed.");
                return;
            }

            VersionUpdateStatusMessage = payload;
            VersionUpdateErrorMessage = string.Empty;
            SetPendingResourceUpdateAvailability(false);
            await RefreshVersionUpdateResourceInfoAsync(cancellationToken);
            ResourceVersionUpdated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsVersionUpdateActionRunning = false;
        }
    }

    public async Task RefreshVersionUpdateResourceInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Runtime.VersionUpdateFeatureService.LoadResourceVersionInfoAsync(
                ConnectionGameSharedState.ClientType,
                cancellationToken);
            var info = await ApplyResultNoDialogAsync(result, "Settings.VersionUpdate.ResourceInfo.Load", cancellationToken);
            if (info is null)
            {
                UpdatePanelResourceVersion = string.Empty;
                UpdatePanelResourceTime = string.Empty;
                return;
            }

            UpdatePanelResourceVersion = info.VersionName;
            UpdatePanelResourceTime = info.LastUpdatedAt == DateTime.MinValue
                ? string.Empty
                : info.LastUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            UpdatePanelResourceVersion = string.Empty;
            UpdatePanelResourceTime = string.Empty;
        }
    }

    public void RefreshVersionUpdateSchedulerState()
    {
        if (VersionUpdateScheduledCheck)
        {
            if (!_versionUpdateSchedulerTimer.IsEnabled)
            {
                _versionUpdateSchedulerTimer.Start();
            }

            return;
        }

        if (_versionUpdateSchedulerTimer.IsEnabled)
        {
            _versionUpdateSchedulerTimer.Stop();
        }
    }

    private void OnVersionUpdateSchedulerTick(object? sender, EventArgs e)
    {
        _ = RunScheduledVersionUpdateCheckAsync();
    }

    public async Task OpenVersionUpdateChangelogAsync(CancellationToken cancellationToken = default)
    {
        var result = await _openExternalTargetAsync(VersionUpdateChangelogUrl, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.VersionUpdate.OpenChangelog", cancellationToken))
        {
            VersionUpdateErrorMessage = result.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.OpenChangelogFailed",
                "Failed to open changelog.");
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.Status.OpenChangelogSucceeded",
            "Changelog opened.");
        VersionUpdateErrorMessage = string.Empty;
    }

    public async Task OpenVersionUpdateResourceRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await _openExternalTargetAsync(VersionUpdateResourceRepositoryUrl, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.VersionUpdate.OpenResourceRepository", cancellationToken))
        {
            VersionUpdateErrorMessage = result.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.OpenResourceRepositoryFailed",
                "Failed to open resource repository.");
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.Status.OpenResourceRepositorySucceeded",
            "Resource repository opened.");
        VersionUpdateErrorMessage = string.Empty;
    }

    public async Task OpenVersionUpdateMirrorChyanAsync(CancellationToken cancellationToken = default)
    {
        var result = await _openExternalTargetAsync(VersionUpdateMirrorChyanUrl, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.VersionUpdate.OpenMirrorChyan", cancellationToken))
        {
            VersionUpdateErrorMessage = result.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.OpenMirrorChyanFailed",
                "Failed to open MirrorChyan.");
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.Status.OpenMirrorChyanSucceeded",
            "MirrorChyan opened.");
        VersionUpdateErrorMessage = string.Empty;
    }

    private VersionUpdatePolicy BuildVersionUpdatePolicy()
    {
        return new VersionUpdatePolicy(
            Proxy: VersionUpdateProxy,
            ProxyType: VersionUpdateProxyType,
            VersionType: VersionUpdateVersionType,
            ResourceUpdateSource: VersionUpdateResourceSource,
            ForceGithubGlobalSource: VersionUpdateForceGithubSource,
            MirrorChyanCdk: VersionUpdateMirrorChyanCdk,
            MirrorChyanCdkExpired: VersionUpdateMirrorChyanCdkExpired,
            StartupUpdateCheck: VersionUpdateStartupCheck,
            ScheduledUpdateCheck: VersionUpdateScheduledCheck,
            ResourceApi: VersionUpdateResourceApi,
            AllowNightlyUpdates: VersionUpdateAllowNightly,
            HasAcknowledgedNightlyWarning: VersionUpdateAcknowledgedNightlyWarning,
            UseAria2: VersionUpdateUseAria2,
            AutoDownloadUpdatePackage: VersionUpdateAutoDownload,
            AutoInstallUpdatePackage: VersionUpdateAutoInstall,
            VersionName: VersionUpdateName,
            VersionBody: VersionUpdateBody,
            IsFirstBoot: VersionUpdateIsFirstBoot,
            VersionPackage: VersionUpdatePackage,
            DoNotShowUpdate: VersionUpdateDoNotShow);
    }

    private void ApplyVersionUpdatePolicy(VersionUpdatePolicy policy)
    {
        RunWithSuppressedSettingsBackfill(() =>
        {
            VersionUpdateProxy = policy.Proxy;
            VersionUpdateProxyType = policy.ProxyType;
            VersionUpdateVersionType = policy.VersionType;
            VersionUpdateResourceSource = policy.ResourceUpdateSource;
            VersionUpdateForceGithubSource = policy.ForceGithubGlobalSource;
            VersionUpdateMirrorChyanCdk = policy.MirrorChyanCdk;
            VersionUpdateMirrorChyanCdkExpired = policy.MirrorChyanCdkExpired;
            VersionUpdateStartupCheck = policy.StartupUpdateCheck;
            VersionUpdateScheduledCheck = policy.ScheduledUpdateCheck;
            VersionUpdateResourceApi = policy.ResourceApi;
            VersionUpdateAllowNightly = policy.AllowNightlyUpdates;
            VersionUpdateAcknowledgedNightlyWarning = policy.HasAcknowledgedNightlyWarning;
            VersionUpdateUseAria2 = policy.UseAria2;
            VersionUpdateAutoDownload = policy.AutoDownloadUpdatePackage;
            VersionUpdateAutoInstall = policy.AutoInstallUpdatePackage;
            VersionUpdateName = policy.VersionName;
            VersionUpdateBody = policy.VersionBody;
            VersionUpdateIsFirstBoot = policy.IsFirstBoot;
            VersionUpdatePackage = policy.VersionPackage;
            VersionUpdateDoNotShow = policy.DoNotShowUpdate;
        });
        RefreshVersionUpdateSchedulerState();
        SyncVersionUpdateAvailabilityFromState();
    }

    private async Task RunVersionUpdateCheckInternalAsync(
        string scope,
        bool showDialog,
        CancellationToken cancellationToken)
    {
        if (IsVersionUpdateActionRunning)
        {
            return;
        }

        IsVersionUpdateActionRunning = true;
        VersionUpdateStatusMessage = string.Empty;
        VersionUpdateErrorMessage = string.Empty;

        try
        {
            var checkOperation = await ExecuteVersionUpdateCheckAsync(scope, cancellationToken);
            var checkResult = await ApplyResultNoDialogAsync(checkOperation, scope, cancellationToken);
            if (checkResult is null)
            {
                VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.Status.CheckFailed",
                    "Update check failed.");
                VersionUpdateErrorMessage = checkOperation.Message;
                return;
            }

            await ApplyVersionUpdateCheckResultAsync(checkResult, cancellationToken);
            if (showDialog && checkResult.IsNewVersion)
            {
                await HandleVersionUpdateDialogAsync(
                    checkResult,
                    $"{scope}.Dialog",
                    cancellationToken);
            }
            else if (!showDialog)
            {
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = string.Empty;
            }
            else if (!HasVersionUpdateErrorMessage)
            {
                VersionUpdateStatusMessage = checkOperation.Message;
            }
        }
        finally
        {
            IsVersionUpdateActionRunning = false;
        }
    }

    private async Task<UiOperationResult<VersionUpdateCheckResult>> ExecuteVersionUpdateCheckAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var policy = BuildVersionUpdatePolicy();
        return await Runtime.VersionUpdateFeatureService.CheckForUpdatesAsync(
            policy,
            UpdatePanelUiVersion,
            cancellationToken);
    }

    private async Task ApplyVersionUpdateCheckResultAsync(
        VersionUpdateCheckResult checkResult,
        CancellationToken cancellationToken)
    {
        var resolvedName = string.IsNullOrWhiteSpace(checkResult.ReleaseName)
            ? checkResult.TargetVersion
            : checkResult.ReleaseName;
        VersionUpdateName = resolvedName;
        VersionUpdateBody = checkResult.Body;
        VersionUpdatePackage = checkResult.PreparedPackagePath ?? string.Empty;
        VersionUpdateDoNotShow = !checkResult.IsNewVersion;
        VersionUpdateIsFirstBoot = false;
        SetPendingVersionUpdateAvailability(checkResult.IsNewVersion);

        var persistResult = await Runtime.VersionUpdateFeatureService.SavePolicyAsync(
            BuildVersionUpdatePolicy(),
            cancellationToken);
        if (!persistResult.Success)
        {
            VersionUpdateStatusMessage = string.IsNullOrWhiteSpace(VersionUpdateStatusMessage)
                ? RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.Status.PersistFailed",
                    "Update result was refreshed, but failed to save into configuration.")
                : $"{VersionUpdateStatusMessage} ({RootTexts.GetOrDefault("Settings.VersionUpdate.Status.PersistFailedSuffix", "failed to save result")})";
            VersionUpdateErrorMessage = persistResult.Message;
        }
    }

    private async Task HandleVersionUpdateDialogAsync(
        VersionUpdateCheckResult checkResult,
        string scope,
        CancellationToken cancellationToken)
    {
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Title", "Version Update"),
                confirmText: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Confirm", "Confirm"),
                cancelText: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Cancel", "Later")));
        var chromeSnapshot = chrome.GetSnapshot();
        var dialogResult = await _dialogService.ShowVersionUpdateAsync(
            new VersionUpdateDialogRequest(
                Title: chromeSnapshot.Title,
                CurrentVersion: checkResult.CurrentVersion,
                TargetVersion: checkResult.TargetVersion,
                Summary: checkResult.Summary,
                Body: checkResult.Body,
                ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.Dialog.Confirm", "Confirm"),
                CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.Dialog.Cancel", "Later"),
                Chrome: chrome),
            scope,
            cancellationToken);

        if (HasPackageResolutionFailure(checkResult))
        {
            VersionUpdateStatusMessage = ResolveVersionUpdatePackageFailureMessage(checkResult);
            VersionUpdateErrorMessage = string.Empty;
            return;
        }

        switch (dialogResult.Return)
        {
            case DialogReturnSemantic.Confirm:
                VersionUpdateStatusMessage = LocalizeSettingsText(
                    "Settings.VersionUpdate.Status.DialogConfirmed",
                    "版本更新弹窗确认完成。");
                VersionUpdateErrorMessage = string.Empty;
                await HandlePreparedVersionUpdatePackageAsync(
                    checkResult,
                    $"{scope}.Package",
                    cancellationToken);
                return;
            case DialogReturnSemantic.Cancel:
                VersionUpdateStatusMessage = LocalizeSettingsText(
                    "Settings.VersionUpdate.Status.DialogCancelled",
                    "版本更新弹窗已取消。");
                VersionUpdateErrorMessage = string.Empty;
                return;
            default:
                VersionUpdateStatusMessage = LocalizeSettingsText(
                    "Settings.VersionUpdate.Status.DialogClosed",
                    "版本更新弹窗已关闭。");
                VersionUpdateErrorMessage = string.Empty;
                return;
        }
    }

    private static bool HasPackageResolutionFailure(VersionUpdateCheckResult checkResult)
    {
        return checkResult.PackageResolutionStatus is
            PackageResolutionStatus.WindowsManualUpdateRequired
            or PackageResolutionStatus.Unavailable
            or PackageResolutionStatus.DownloadFailed;
    }

    private string ResolveVersionUpdatePackageFailureMessage(VersionUpdateCheckResult checkResult)
    {
        var fallback = ResolveVersionUpdatePackageFailureFallback(checkResult.PackageResolutionStatus);
        return string.IsNullOrWhiteSpace(checkResult.PackageFailureMessageKey)
            ? fallback
            : LocalizeSettingsText(checkResult.PackageFailureMessageKey, fallback);
    }

    private static string ResolveVersionUpdatePackageFailureFallback(PackageResolutionStatus status)
    {
        return status switch
        {
            PackageResolutionStatus.WindowsManualUpdateRequired => "Windows 版目前暂未在 release 发布，请手动更新。",
            PackageResolutionStatus.Unavailable => "更新失败。",
            PackageResolutionStatus.DownloadFailed => "更新失败。",
            _ => string.Empty,
        };
    }

    private async Task HandlePreparedVersionUpdatePackageAsync(
        VersionUpdateCheckResult checkResult,
        string scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkResult.PreparedPackagePath))
        {
            return;
        }

        if (Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Stopping)
        {
            VersionUpdateStatusMessage = LocalizeSettingsText(
                "Settings.VersionUpdate.RestartPendingWhileRunning",
                "更新包已下载完成。当前任务仍在运行，请在空闲时重启 MAA 以应用更新。");
            VersionUpdateErrorMessage = string.Empty;
            return;
        }

        if (VersionUpdateAutoInstall)
        {
            await RestartForPreparedVersionUpdatePackageAsync(scope, cancellationToken);
            return;
        }

        await PromptForPreparedVersionUpdateRestartAsync(scope, cancellationToken);
    }

    private async Task PromptForPreparedVersionUpdateRestartAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.VersionUpdate.RestartDialog.Title", "Update Package Ready"),
                confirmText: texts.GetOrDefault("Settings.VersionUpdate.RestartDialog.Confirm", "Restart Now"),
                cancelText: texts.GetOrDefault("Settings.VersionUpdate.RestartDialog.Cancel", "Later")));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: RootTexts.GetOrDefault(
                "Settings.VersionUpdate.RestartDialog.Message",
                "The software update package has finished downloading. Restart MAA now to apply the update?"),
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.RestartDialog.Confirm", "Restart Now"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.RestartDialog.Cancel", "Later"),
            Language: Language,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            $"{scope}.Prompt",
            cancellationToken);
        if (dialogResult.Return != DialogReturnSemantic.Confirm)
        {
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.RestartPending",
                "The update package is ready. Restart MAA later to apply it.");
            VersionUpdateErrorMessage = string.Empty;
            return;
        }

        await RestartForPreparedVersionUpdatePackageAsync(scope, cancellationToken);
    }

    private async Task RestartForPreparedVersionUpdatePackageAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        Runtime.LogService.Info("[update] Restart requested to apply the downloaded software update package.");
        var restartResult = await Runtime.AppLifecycleService.RestartAsync(cancellationToken);
        if (!await ApplyResultAsync(restartResult, scope, cancellationToken))
        {
            VersionUpdateErrorMessage = restartResult.Message;
            return;
        }

        VersionUpdateStatusMessage = RootTexts.GetOrDefault(
            "Settings.VersionUpdate.RestartLaunched",
            "Restart has started to apply the software update.");
        VersionUpdateErrorMessage = string.Empty;

        if (!Runtime.AppLifecycleService.SupportsExit)
        {
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.RestartManualClose",
                "A new instance has started. Close the current instance to continue applying the update.");
            return;
        }

        await ApplyResultAsync(
            Runtime.AppLifecycleService.ExitAsync,
            $"{scope}.Exit",
            UiErrorCode.AppExitFailed,
            cancellationToken);
    }

    private void SyncVersionUpdateAvailabilityFromState()
    {
        SetPendingVersionUpdateAvailability(
            !VersionUpdateDoNotShow
            && (!string.IsNullOrWhiteSpace(VersionUpdateName)
                || !string.IsNullOrWhiteSpace(VersionUpdatePackage)));
    }

    private void SetPendingVersionUpdateAvailability(bool available)
    {
        if (_hasPendingVersionUpdateAvailability == available)
        {
            return;
        }

        _hasPendingVersionUpdateAvailability = available;
        OnPropertyChanged(nameof(HasPendingVersionUpdateAvailability));
        UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetPendingResourceUpdateAvailability(bool available)
    {
        if (_hasPendingResourceUpdateAvailability == available)
        {
            return;
        }

        _hasPendingResourceUpdateAvailability = available;
        OnPropertyChanged(nameof(HasPendingResourceUpdateAvailability));
        UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

}
