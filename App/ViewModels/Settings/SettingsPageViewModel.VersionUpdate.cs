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

    public async Task CheckVersionUpdateAsync(CancellationToken cancellationToken = default)
    {
        await RunVersionUpdateCheckInternalAsync(
            "Settings.VersionUpdate.Check.Manual",
            showDialog: false,
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
        var firstBootDialogRequest = await PrepareFirstBootVersionUpdateDialogRequestAsync(cancellationToken);
        if (firstBootDialogRequest is not null)
        {
            StartFirstBootVersionUpdateDialog(
                firstBootDialogRequest,
                "Settings.VersionUpdate.Check.Startup.FirstBoot",
                cancellationToken);
        }

        if (!VersionUpdateStartupCheck)
        {
            return;
        }

        await RunAutomaticVersionAndResourceUpdateFlowAsync(
            "Settings.VersionUpdate.Check.Startup",
            autoApplyResourceUpdate: false,
            cancellationToken);
    }

    public async Task RunScheduledVersionUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!VersionUpdateScheduledCheck)
        {
            return;
        }

        await RunAutomaticVersionAndResourceUpdateFlowAsync(
            "Settings.VersionUpdate.Check.Scheduled",
            autoApplyResourceUpdate: false,
            cancellationToken);
    }

    public async Task ManualUpdateResourceAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(VersionUpdateResourceSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(VersionUpdateMirrorChyanCdk))
        {
            ClearVersionUpdateActivityMessage();
            VersionUpdateStatusMessage = LocalizeSettingsText(
                "Settings.VersionUpdate.Status.MirrorChyanCdkRequired",
                "请前往 设置 > Version Update 配置 Mirror酱 CDK 或切换更新源。");
            VersionUpdateErrorMessage = string.Empty;
            return;
        }

        await RunResourceUpdateInternalAsync(
            "Settings.VersionUpdate.Resource.Manual",
            autoApplyUpdate: true,
            cancellationToken);
    }

    public async Task TryRunScheduledVersionUpdateCheckAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!VersionUpdateScheduledCheck)
        {
            return;
        }

        if (Interlocked.Exchange(ref _versionUpdateScheduledCheckRunning, 1) == 1)
        {
            return;
        }

        try
        {
            if (!ShouldRunScheduledVersionUpdateCheck(utcNow))
            {
                return;
            }

            await RunScheduledVersionUpdateCheckAsync(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _versionUpdateScheduledCheckRunning, 0);
        }
    }

    private async Task RunResourceUpdateInternalAsync(
        string scope,
        bool autoApplyUpdate,
        CancellationToken cancellationToken)
    {
        if (IsVersionUpdateActionRunning)
        {
            return;
        }

        IsVersionUpdateActionRunning = true;
        VersionUpdateActivityMessage = LocalizeSettingsText(
            "Settings.VersionUpdate.Activity.CheckingResource",
            "正在检查资源更新……");
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
                ClearVersionUpdateActivityMessage();
                SetPendingResourceUpdateState(null);
                VersionUpdateErrorMessage = checkOperation.Message;
                VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                    "Settings.VersionUpdate.Status.ResourceUpdateFailed",
                    "Resource update failed.");
                return;
            }

            SetPendingResourceUpdateState(availability);
            if (!availability.IsUpdateAvailable)
            {
                ClearVersionUpdateActivityMessage();
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = string.Empty;
                return;
            }

            if (availability.RequiresMirrorChyanCdk
                && string.Equals(policy.ResourceUpdateSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(policy.MirrorChyanCdk))
            {
                ClearVersionUpdateActivityMessage();
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = LocalizeSettingsText(
                    "Settings.VersionUpdate.Status.MirrorChyanCdkRequired",
                    "请前往 设置 > Version Update 配置 Mirror酱 CDK 或切换更新源。");
                return;
            }

            if (!autoApplyUpdate)
            {
                ClearVersionUpdateActivityMessage();
                VersionUpdateStatusMessage = checkOperation.Message;
                VersionUpdateErrorMessage = string.Empty;
                return;
            }

            if (!await ApplyResourceUpdateAsync(scope, policy, cancellationToken))
            {
                return;
            }
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
        _ = TryRunScheduledVersionUpdateCheckAsync(DateTimeOffset.UtcNow);
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
        VersionUpdateActivityMessage = LocalizeSettingsText(
            "Settings.VersionUpdate.Activity.CheckingSoftware",
            "正在检查软件更新……");
        VersionUpdateStatusMessage = string.Empty;
        VersionUpdateErrorMessage = string.Empty;

        try
        {
            var checkOperation = await ExecuteVersionUpdateCheckAsync(scope, cancellationToken);
            var checkResult = await ApplyResultNoDialogAsync(checkOperation, scope, cancellationToken);
            if (checkResult is null)
            {
                ClearVersionUpdateActivityMessage();
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
            else if (!showDialog && HasPackageResolutionFailure(checkResult))
            {
                VersionUpdateStatusMessage = ResolveVersionUpdatePackageFailureMessage(checkResult);
                VersionUpdateErrorMessage = string.Empty;
            }
            else if (!showDialog && !string.IsNullOrWhiteSpace(checkResult.PreparedPackagePath))
            {
                await HandlePreparedVersionUpdatePackageAsync(
                    checkResult,
                    $"{scope}.Package",
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

            if (string.IsNullOrWhiteSpace(checkResult.PreparedPackagePath))
            {
                ClearVersionUpdateActivityMessage();
            }
        }
        finally
        {
            IsVersionUpdateActionRunning = false;
        }
    }

    private async Task RunAutomaticVersionAndResourceUpdateFlowAsync(
        string scope,
        bool autoApplyResourceUpdate,
        CancellationToken cancellationToken)
    {
        await RunVersionUpdateCheckInternalAsync(
            $"{scope}.Software",
            showDialog: false,
            cancellationToken);

        await RunResourceUpdateInternalAsync(
            $"{scope}.Resource",
            autoApplyUpdate: autoApplyResourceUpdate,
            cancellationToken);
    }

    private async Task<UiOperationResult<VersionUpdateCheckResult>> ExecuteVersionUpdateCheckAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var policy = BuildVersionUpdatePolicy();
        var currentVersion = ResolveSoftwareUpdateCurrentVersion();
        return await Runtime.VersionUpdateFeatureService.CheckForUpdatesAsync(
            policy,
            currentVersion,
            CreateVersionUpdateProgressReporter(),
            cancellationToken);
    }

    private string ResolveSoftwareUpdateCurrentVersion()
    {
        var currentCoreVersion = _coreVersionResolver.Invoke()?.Trim();
        if (!string.IsNullOrWhiteSpace(currentCoreVersion))
        {
            UpdatePanelCoreVersion = currentCoreVersion;
            return currentCoreVersion;
        }

        var panelCoreVersion = UpdatePanelCoreVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(panelCoreVersion)
            && !string.Equals(panelCoreVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return panelCoreVersion;
        }

        return string.IsNullOrWhiteSpace(UpdatePanelUiVersion)
            ? "unknown"
            : UpdatePanelUiVersion.Trim();
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

    private async Task<bool> ApplyResourceUpdateAsync(
        string scope,
        VersionUpdatePolicy policy,
        CancellationToken cancellationToken)
    {
        var updateResult = await Runtime.VersionUpdateFeatureService.UpdateResourceAsync(
            policy,
            ConnectionGameSharedState.ClientType,
            CreateVersionUpdateProgressReporter(),
            cancellationToken);
        var payload = await ApplyResultNoDialogAsync(
            updateResult,
            $"{scope}.Update",
            cancellationToken);
        if (payload is null)
        {
            ClearVersionUpdateActivityMessage();
            VersionUpdateErrorMessage = updateResult.Message;
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.ResourceUpdateFailed",
                "Resource update failed.");
            return false;
        }

        await ReloadUpdatedResourcesAsync(cancellationToken);
        VersionUpdateStatusMessage = payload;
        VersionUpdateErrorMessage = string.Empty;
        SetPendingResourceUpdateState(null);
        await RefreshVersionUpdateResourceInfoAsync(cancellationToken);
        ResourceVersionUpdated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task ReloadUpdatedResourcesAsync(CancellationToken cancellationToken)
    {
        if (Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Stopping)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Runtime.SessionService.ReloadResourceWhenIdleAsync(
                            ConnectionGameSharedState.ClientType,
                            waitTimeout: TimeSpan.FromMinutes(15),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Runtime.LogService.Warn($"[update] Deferred resource reload failed: {ex.Message}");
                    }
                },
                CancellationToken.None);
            return;
        }

        var reloadResult = await Runtime.SessionService.ReloadResourceWhenIdleAsync(
            ConnectionGameSharedState.ClientType,
            waitTimeout: TimeSpan.FromSeconds(30),
            cancellationToken);
        if (reloadResult.Success)
        {
            return;
        }

        if (reloadResult.Error?.Code is CoreErrorCode.NotInitialized or CoreErrorCode.NotSupported or CoreErrorCode.Disposed)
        {
            return;
        }

        Runtime.LogService.Warn(
            $"[update] Resource reload failed after package update: {reloadResult.Error?.Code} {reloadResult.Error?.Message}");
    }

    private async Task<VersionUpdateDialogRequest?> PrepareFirstBootVersionUpdateDialogRequestAsync(
        CancellationToken cancellationToken)
    {
        if (!VersionUpdateIsFirstBoot)
        {
            var policyResult = await Runtime.VersionUpdateFeatureService.LoadPolicyAsync(cancellationToken);
            if (policyResult.Success && policyResult.Value is { IsFirstBoot: true } policy)
            {
                VersionUpdateName = policy.VersionName;
                VersionUpdateBody = policy.VersionBody;
                VersionUpdatePackage = policy.VersionPackage;
                VersionUpdateDoNotShow = policy.DoNotShowUpdate;
                VersionUpdateIsFirstBoot = true;
            }
        }

        if (!VersionUpdateIsFirstBoot)
        {
            return null;
        }

        var targetVersion = VersionUpdateName;
        var body = VersionUpdateBody;
        var doNotShow = VersionUpdateDoNotShow;
        VersionUpdateIsFirstBoot = false;
        var persistResult = await Runtime.VersionUpdateFeatureService.SavePolicyAsync(
            BuildVersionUpdatePolicy(),
            cancellationToken);
        if (!persistResult.Success)
        {
            VersionUpdateErrorMessage = persistResult.Message;
        }

        if (doNotShow
            || (string.IsNullOrWhiteSpace(targetVersion)
                && string.IsNullOrWhiteSpace(body)))
        {
            return null;
        }

        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Title", "Version Update"),
                confirmText: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Confirm", "Confirm"),
                cancelText: texts.GetOrDefault("Settings.VersionUpdate.Dialog.Cancel", "Later")));
        var chromeSnapshot = chrome.GetSnapshot();
        return new VersionUpdateDialogRequest(
            Title: chromeSnapshot.Title,
            CurrentVersion: UpdatePanelUiVersion,
            TargetVersion: targetVersion,
            Summary: targetVersion,
            Body: body,
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.Dialog.Confirm", "Confirm"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.VersionUpdate.Dialog.Cancel", "Later"),
            Chrome: chrome);
    }

    private void StartFirstBootVersionUpdateDialog(
        VersionUpdateDialogRequest request,
        string scope,
        CancellationToken cancellationToken)
    {
        _ = ObserveFirstBootVersionUpdateDialogAsync(request, scope, cancellationToken);
    }

    private async Task ObserveFirstBootVersionUpdateDialogAsync(
        VersionUpdateDialogRequest request,
        string scope,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dialogService.ShowVersionUpdateAsync(request, scope, cancellationToken);
            VersionUpdateStatusMessage = RootTexts.GetOrDefault(
                "Settings.VersionUpdate.Status.FirstBootShown",
                "Update notes displayed.");
            VersionUpdateErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // no-op
        }
    }

    private bool ShouldRunScheduledVersionUpdateCheck(DateTimeOffset utcNow)
    {
        var scheduledNow = GetScheduledUpdateClock(utcNow);
        if (scheduledNow.Minute != 0 || (scheduledNow.Hour != 0 && scheduledNow.Hour != 18))
        {
            return false;
        }

        var minuteKey = scheduledNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (string.Equals(_versionUpdateLastScheduledMinuteKey, minuteKey, StringComparison.Ordinal))
        {
            return false;
        }

        _versionUpdateLastScheduledMinuteKey = minuteKey;
        return true;
    }

    private DateTimeOffset GetScheduledUpdateClock(DateTimeOffset utcNow)
    {
        var clientType = ConnectionGameSharedState.ClientType;
        var offset = clientType switch
        {
            "YoStarEN" => -7,
            "YoStarJP" => 9,
            "YoStarKR" => 9,
            "txwy" => 8,
            _ => 8,
        };

        return utcNow.ToOffset(TimeSpan.FromHours(offset)).AddHours(-4);
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

    private void SetPendingResourceUpdateState(ResourceUpdateCheckResult? availability)
    {
        var available = availability?.IsUpdateAvailable == true;
        var displayVersion = available ? availability!.DisplayVersion ?? string.Empty : string.Empty;
        var releaseNote = available ? availability!.ReleaseNote ?? string.Empty : string.Empty;
        var versionTimestamp = available ? availability!.VersionTimestamp : null;
        var changed = false;

        if (_hasPendingResourceUpdateAvailability != available)
        {
            _hasPendingResourceUpdateAvailability = available;
            OnPropertyChanged(nameof(HasPendingResourceUpdateAvailability));
            changed = true;
        }

        if (!string.Equals(_pendingResourceUpdateDisplayVersion, displayVersion, StringComparison.Ordinal))
        {
            _pendingResourceUpdateDisplayVersion = displayVersion;
            changed = true;
        }

        if (!string.Equals(_pendingResourceUpdateReleaseNote, releaseNote, StringComparison.Ordinal))
        {
            _pendingResourceUpdateReleaseNote = releaseNote;
            changed = true;
        }

        if (_pendingResourceUpdateVersionTimestamp != versionTimestamp)
        {
            _pendingResourceUpdateVersionTimestamp = versionTimestamp;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(PendingResourceUpdateSummary));
        UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private IProgress<VersionUpdateProgressInfo> CreateVersionUpdateProgressReporter()
    {
        return new Progress<VersionUpdateProgressInfo>(progress =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyVersionUpdateProgress(progress);
                return;
            }

            Dispatcher.UIThread.Post(() => ApplyVersionUpdateProgress(progress));
        });
    }

    private void ApplyVersionUpdateProgress(VersionUpdateProgressInfo progress)
    {
        VersionUpdateActivityMessage = progress.Operation switch
        {
            VersionUpdateProgressOperation.ResourcePackage => BuildResourceUpdateActivityMessage(progress),
            VersionUpdateProgressOperation.SoftwarePackage => BuildSoftwarePackageActivityMessage(progress),
            _ => VersionUpdateActivityMessage,
        };
    }

    private string BuildResourceUpdateActivityMessage(VersionUpdateProgressInfo progress)
    {
        return progress.Stage switch
        {
            VersionUpdateProgressStage.Started when progress.Source == VersionUpdateProgressSource.MirrorChyan
                && !string.IsNullOrWhiteSpace(progress.Detail) =>
                string.Format(
                    LocalizeSettingsText(
                        "Settings.VersionUpdate.Activity.ResourceUpdatingMirrorChyan",
                        "正在使用「MirrorChyan」更新资源{0}……"),
                    progress.Detail),
            VersionUpdateProgressStage.Started => LocalizeSettingsText(
                "Settings.VersionUpdate.Activity.ResourceUpdating",
                "游戏资源正在更新。"),
            VersionUpdateProgressStage.Downloading => BuildTransferActivityMessage(
                progress.Source == VersionUpdateProgressSource.MirrorChyan
                    ? LocalizeSettingsText(
                        "Settings.VersionUpdate.Activity.ResourceDownloadingMirrorChyan",
                        "正在使用「MirrorChyan」下载……")
                    : LocalizeSettingsText(
                        "Settings.VersionUpdate.Activity.ResourceDownloadingGlobalSource",
                        "正在使用「GlobalSource」下载……"),
                progress),
            VersionUpdateProgressStage.Preparing => LocalizeSettingsText(
                "Settings.VersionUpdate.Activity.ResourcePreparing",
                "正在编制索引"),
            VersionUpdateProgressStage.Completed => LocalizeSettingsText(
                "Settings.VersionUpdate.Activity.ResourceUpdated",
                "游戏资源已更新。"),
            _ => VersionUpdateActivityMessage,
        };
    }

    private string BuildSoftwarePackageActivityMessage(VersionUpdateProgressInfo progress)
    {
        return progress.Stage switch
        {
            VersionUpdateProgressStage.Started => LocalizeSettingsText(
                "Settings.VersionUpdate.Activity.SoftwarePackageDownloading",
                "正在下载软件更新包……"),
            VersionUpdateProgressStage.Downloading => BuildTransferActivityMessage(
                LocalizeSettingsText(
                    "Settings.VersionUpdate.Activity.SoftwarePackageDownloading",
                    "正在下载软件更新包……"),
                progress),
            VersionUpdateProgressStage.Completed => LocalizeSettingsText(
                "Settings.VersionUpdate.Activity.SoftwarePackageDownloaded",
                "软件更新包已下载完成。"),
            _ => VersionUpdateActivityMessage,
        };
    }

    private static string BuildTransferActivityMessage(string header, VersionUpdateProgressInfo progress)
    {
        var detail = FormatVersionUpdateTransfer(progress);
        return string.IsNullOrWhiteSpace(detail)
            ? header
            : $"{header}\n{detail}";
    }

    private static string FormatVersionUpdateTransfer(VersionUpdateProgressInfo progress)
    {
        if (progress.BytesTransferred <= 0 && progress.TotalBytes <= 0)
        {
            return string.Empty;
        }

        var transferredMiB = progress.BytesTransferred / 1048576d;
        var progressText = progress.TotalBytes > 0
            ? $"[{transferredMiB:F1}MiB/{progress.TotalBytes / 1048576d:F1}MiB ({progress.BytesTransferred * 100d / progress.TotalBytes:F1}%)]"
            : $"[{transferredMiB:F1}MiB]";

        var speed = Math.Max(progress.BytesPerSecond, 0d);
        var speedText = speed >= 1048576d
            ? $"{speed / 1048576d:F1} MiB/s"
            : $"{speed / 1024d:F1} KiB/s";
        return $"{progressText} {speedText}".Trim();
    }

    private void ClearVersionUpdateActivityMessage()
    {
        VersionUpdateActivityMessage = string.Empty;
    }

}
