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
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel
{
    public async Task SaveConnectionGameSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.ConnectionGame",
            SaveConnectionGameSettingsCoreAsync,
            cancellationToken);
    }

    private async Task SaveConnectionGameSettingsCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            LastErrorMessage = string.Format(
                RootTexts.GetOrDefault("Settings.SaveScoped.Error.ProfileMissing", "Current profile `{0}` not found."),
                Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
            await RecordFailedResultAsync(
                "Settings.ConnectionGame.Save",
                UiOperationResult.Fail(UiErrorCode.ProfileMissing, LastErrorMessage),
                cancellationToken: cancellationToken);
            return;
        }

        ConnectionGameProfileSync.WriteToProfile(profile, ConnectionGameSharedState);

        var saveResult = await Runtime.TaskQueueFeatureService.SaveAsync(cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.ConnectionGame.Save", cancellationToken))
        {
            return;
        }

        await TrySyncCoreInstanceOptionsAsync("Settings.ConnectionGame.SyncInstanceOptions", cancellationToken);
    }

    public async Task SaveTimerSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.Timer",
            SaveTimerSettingsCoreAsync,
            cancellationToken);
    }

    private async Task SaveTimerSettingsCoreAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = BuildTimerSnapshot();
        var validation = ValidateTimerSnapshot(snapshot);
        if (!validation.Success)
        {
            HasPendingTimerChanges = true;
            TimerValidationMessage = validation.Message;
            LastErrorMessage = validation.Message;
            await RecordFailedResultAsync(
                "Settings.Save.Timer.Validation",
                validation,
                cancellationToken);
            return;
        }

        var saveResult = await Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(
            snapshot.ToGlobalSettingUpdates(),
            cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.Save.Timer", cancellationToken))
        {
            HasPendingTimerChanges = true;
            MarkSettingsSaveFailure("Settings.AutoSave.Timer");
            return;
        }

        var readBackWarnings = new List<string>();
        var readBackSnapshot = ReadTimerSnapshot(Runtime.ConfigurationService.CurrentConfig, readBackWarnings);
        ApplyTimerSnapshot(readBackSnapshot);

        HasPendingTimerChanges = false;
        TimerValidationMessage = readBackWarnings.Count > 0
            ? string.Join(" ", readBackWarnings)
            : string.Empty;
        LastSuccessfulTimerSaveAt = DateTimeOffset.Now;

        if (readBackWarnings.Count > 0)
        {
            await RecordEventAsync(
                "Settings.Timer.Normalize",
                string.Join(" | ", readBackWarnings),
                cancellationToken);
        }
    }

    private async Task<UiOperationResult> SaveScopedSettingsAsync(
        IReadOnlyDictionary<string, string>? globalUpdates = null,
        IReadOnlyDictionary<string, string>? profileUpdates = null,
        string successScope = "Settings.SaveScoped",
        CancellationToken cancellationToken = default)
    {
        globalUpdates ??= EmptySettingUpdates;
        profileUpdates ??= EmptySettingUpdates;
        if (globalUpdates.Count == 0 && profileUpdates.Count == 0)
        {
            return UiOperationResult.Fail(
                UiErrorCode.SettingBatchEmpty,
                RootTexts.GetOrDefault("Settings.SaveScoped.Error.BatchEmpty", "No settings were provided."));
        }

        UnifiedProfile? profile = null;
        if (profileUpdates.Count > 0 && !Runtime.ConfigurationService.TryGetCurrentProfile(out profile))
        {
            return UiOperationResult.Fail(
                UiErrorCode.ProfileMissing,
                string.Format(
                    RootTexts.GetOrDefault("Settings.SaveScoped.Error.ProfileMissing", "Current profile `{0}` not found."),
                    Runtime.ConfigurationService.CurrentConfig.CurrentProfile));
        }

        var config = Runtime.ConfigurationService.CurrentConfig;
        var globalSnapshot = CloneJsonNodeMap(config.GlobalValues);
        Dictionary<string, JsonNode?>? profileSnapshot = profile is null
            ? null
            : CloneJsonNodeMap(profile.Values);

        try
        {
            foreach (var (key, value) in globalUpdates)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.SettingKeyMissing,
                        RootTexts.GetOrDefault("Settings.SaveScoped.Error.SettingKeyMissing", "Setting key cannot be empty."));
                }

                config.GlobalValues[key] = JsonValue.Create(value);
            }

            if (profile is not null)
            {
                foreach (var (key, value) in profileUpdates)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return UiOperationResult.Fail(
                            UiErrorCode.SettingKeyMissing,
                            RootTexts.GetOrDefault("Settings.SaveScoped.Error.SettingKeyMissing", "Setting key cannot be empty."));
                    }

                    profile.Values[key] = JsonValue.Create(value);
                }
            }

            await Runtime.ConfigurationService.SaveAsync(cancellationToken);
            await RecordEventAsync(
                successScope,
                string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.SaveScoped.Status.BatchSavedSummary",
                        "Saved settings batch: global={0}, profile={1}"),
                    globalUpdates.Count,
                    profileUpdates.Count),
                cancellationToken);
            return UiOperationResult.Ok(
                string.Format(
                    RootTexts.GetOrDefault("Settings.SaveScoped.Status.SavedCount", "Saved {0} settings."),
                    globalUpdates.Count + profileUpdates.Count));
        }
        catch (Exception ex)
        {
            config.GlobalValues = globalSnapshot;
            if (profile is not null && profileSnapshot is not null)
            {
                profile.Values = profileSnapshot;
            }

            Runtime.ConfigurationService.RevalidateCurrentConfig(logIssues: false);
            var saveFailedMessage = string.Format(
                RootTexts.GetOrDefault("Settings.SaveScoped.Error.SaveFailed", "Failed to save settings: {0}"),
                ex.Message);
            await RecordUnhandledExceptionAsync(
                $"{successScope}.Persist",
                ex,
                UiErrorCode.SettingsSaveFailed,
                saveFailedMessage);
            return UiOperationResult.Fail(
                UiErrorCode.SettingsSaveFailed,
                saveFailedMessage);
        }
    }

    public async Task SaveStartPerformanceSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.StartPerformance",
            SaveStartPerformanceSettingsCoreAsync,
            cancellationToken);
    }

    private async Task SaveStartPerformanceSettingsCoreAsync(CancellationToken cancellationToken = default)
    {
        var persistedWarnings = new List<string>();
        var persistedSnapshot = ReadStartPerformanceSnapshot(Runtime.ConfigurationService.CurrentConfig, persistedWarnings);
        var snapshot = BuildNormalizedStartPerformanceSnapshot();
        ApplyStartPerformanceSnapshotWithoutDirtyTracking(snapshot);

        var validation = ValidateStartPerformanceSnapshot(snapshot);
        if (!validation.Success)
        {
            HasPendingStartPerformanceChanges = true;
            StartPerformanceValidationMessage = validation.Message;
            LastErrorMessage = validation.Message;
            await RecordFailedResultAsync(
                "Settings.Save.StartPerformance.Validation",
                validation,
                cancellationToken);
            return;
        }

        if (persistedWarnings.Count == 0
            && snapshot == persistedSnapshot
            && HasExplicitStartPerformanceConfigValues(Runtime.ConfigurationService.CurrentConfig, snapshot))
        {
            HasPendingStartPerformanceChanges = false;
            StartPerformanceValidationMessage = string.Empty;
            return;
        }

        var saveResult = await SaveScopedSettingsAsync(
            globalUpdates: snapshot.ToGlobalSettingUpdates(),
            profileUpdates: snapshot.ToProfileSettingUpdates(),
            successScope: "Settings.Save.StartPerformance",
            cancellationToken: cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.Save.StartPerformance", cancellationToken))
        {
            HasPendingStartPerformanceChanges = true;
            MarkSettingsSaveFailure("Settings.AutoSave.StartPerformance");
            return;
        }

        var readBackWarnings = new List<string>();
        var readBackSnapshot = ReadStartPerformanceSnapshot(Runtime.ConfigurationService.CurrentConfig, readBackWarnings);
        ApplyStartPerformanceSnapshotWithoutDirtyTracking(readBackSnapshot);

        HasPendingStartPerformanceChanges = false;
        StartPerformanceValidationMessage = readBackWarnings.Count > 0
            ? string.Join(" ", readBackWarnings)
            : string.Empty;
        LastSuccessfulStartPerformanceSaveAt = DateTimeOffset.Now;

        if (persistedSnapshot.DeploymentWithPause != readBackSnapshot.DeploymentWithPause)
        {
            await TrySyncCoreInstanceOptionsAsync("Settings.StartPerformance.SyncInstanceOptions", cancellationToken);
        }

        if (ShouldPromptForGpuRestart(persistedSnapshot, readBackSnapshot))
        {
            await PromptForGpuRestartAsync(cancellationToken);
            return;
        }

        if (ShouldPromptForLaunchBehaviorRestart(persistedSnapshot, readBackSnapshot))
        {
            await PromptForLaunchBehaviorRestartAsync(cancellationToken);
        }
    }

    private static bool ShouldPromptForLaunchBehaviorRestart(
        StartPerformanceSettingsSnapshot previousSnapshot,
        StartPerformanceSettingsSnapshot currentSnapshot)
    {
        return previousSnapshot.RunDirectly != currentSnapshot.RunDirectly
               || previousSnapshot.MinimizeDirectly != currentSnapshot.MinimizeDirectly;
    }

    private async Task PromptForLaunchBehaviorRestartAsync(CancellationToken cancellationToken)
    {
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.Start.RestartDialog.Title", "Restart MAA"),
                confirmText: texts.GetOrDefault("Settings.Start.RestartDialog.Confirm", "Restart Now"),
                cancelText: texts.GetOrDefault("Settings.Start.RestartDialog.Cancel", "Later")));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: RootTexts.GetOrDefault(
                "Settings.Start.RestartDialog.Message",
                "Startup settings were saved. Restart MAA now to apply these changes?"),
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.Start.RestartDialog.Confirm", "Restart Now"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.Start.RestartDialog.Cancel", "Later"),
            Language: Language,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Settings.Save.StartPerformance.LaunchBehaviorRestartPrompt",
            cancellationToken);

        if (dialogResult.Return != DialogReturnSemantic.Confirm)
        {
            StatusMessage = RootTexts.GetOrDefault(
                "Settings.Start.RestartPending",
                "Startup settings were saved. Restart MAA later to apply these changes.");
            LastErrorMessage = string.Empty;
            await RecordEventAsync(
                "Settings.Save.StartPerformance.LaunchBehaviorRestartPrompt",
                $"deferred; return={dialogResult.Return}",
                cancellationToken);
            return;
        }

        var restartResult = await Runtime.AppLifecycleService.RestartAsync(cancellationToken);
        if (!await ApplyResultAsync(restartResult, "Settings.Save.StartPerformance.LaunchBehaviorRestart", cancellationToken))
        {
            return;
        }

        StatusMessage = RootTexts.GetOrDefault(
            "Settings.Start.RestartLaunched",
            "Restart has started.");
        await RecordEventAsync(
            "Settings.Save.StartPerformance.LaunchBehaviorRestart",
            "restart-launched",
            cancellationToken);

        if (!Runtime.AppLifecycleService.SupportsExit)
        {
            StatusMessage = RootTexts.GetOrDefault(
                "Settings.Start.RestartManualClose",
                "A new instance has started. Close the current instance to finish restarting.");
            return;
        }

        await ApplyResultAsync(
            Runtime.AppLifecycleService.ExitAsync,
            "Settings.Save.StartPerformance.LaunchBehaviorRestart.Exit",
            UiErrorCode.AppExitFailed,
            cancellationToken);
    }

    private CoreInstanceOptions BuildCurrentCoreInstanceOptions()
    {
        return ConnectionGameSharedState.BuildCoreInstanceOptions(DeploymentWithPause);
    }

    private async Task TrySyncCoreInstanceOptionsAsync(string scope, CancellationToken cancellationToken)
    {
        var result = await Runtime.ConnectFeatureService.ApplyInstanceOptionsAsync(
            BuildCurrentCoreInstanceOptions(),
            cancellationToken);

        if (result.Success || result.Error?.Code is CoreErrorCode.NotInitialized or CoreErrorCode.NotSupported or CoreErrorCode.Disposed)
        {
            return;
        }

        Runtime.LogService.Warn($"{scope}: {result.Error?.Code} {result.Error?.Message}");
        await RecordEventAsync(
            scope,
            string.Format(
                RootTexts.GetOrDefault(
                    "Settings.StartPerformance.Status.SyncCoreInstanceOptionsFailed",
                    "Failed to sync core instance options: {0} {1}"),
                result.Error?.Code,
                result.Error?.Message),
            cancellationToken);
    }

    public async Task SelectEmulatorPathWithDialogAsync(CancellationToken cancellationToken = default)
    {
        var candidates = BuildEmulatorPathDialogCandidates();
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("Settings.Start.Dialog.SelectEmulatorPathTitle", "Select Emulator Path"),
                confirmText: texts.GetOrDefault("Settings.Start.Dialog.SelectEmulatorPathConfirm", "Confirm"),
                cancelText: texts.GetOrDefault("Settings.Start.Dialog.SelectEmulatorPathCancel", "Cancel")));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new EmulatorPathDialogRequest(
            Title: chromeSnapshot.Title,
            CandidatePaths: candidates,
            SelectedPath: EmulatorPath,
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Settings.Start.Dialog.SelectEmulatorPathConfirm", "Confirm"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Settings.Start.Dialog.SelectEmulatorPathCancel", "Cancel"),
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowEmulatorPathAsync(request, "Settings.Start.SelectEmulatorPath.Dialog", cancellationToken);
        if (dialogResult.Return == DialogReturnSemantic.Confirm && dialogResult.Payload is not null)
        {
            EmulatorPath = dialogResult.Payload.SelectedPath;
            StatusMessage = string.Format(
                RootTexts.GetOrDefault("Settings.Start.Status.EmulatorPathUpdated", "Emulator path updated: {0}"),
                EmulatorPath);
            return;
        }

        StatusMessage = dialogResult.Return == DialogReturnSemantic.Cancel
            ? RootTexts.GetOrDefault("Settings.Start.Status.EmulatorPathSelectionCancelled", "Emulator path selection was cancelled.")
            : RootTexts.GetOrDefault("Settings.Start.Status.EmulatorPathSelectionClosed", "Emulator path dialog was closed.");
    }

    private StartPerformanceSettingsSnapshot BuildNormalizedStartPerformanceSnapshot()
    {
        return new StartPerformanceSettingsSnapshot(
            RunDirectly: RunDirectly,
            MinimizeDirectly: MinimizeDirectly,
            OpenEmulatorAfterLaunch: OpenEmulatorAfterLaunch,
            EmulatorPath: (EmulatorPath ?? string.Empty).Trim(),
            EmulatorAddCommand: (EmulatorAddCommand ?? string.Empty).Trim(),
            EmulatorWaitSeconds: EmulatorWaitSeconds,
            PerformanceUseGpu: PerformanceUseGpu,
            PerformanceAllowDeprecatedGpu: PerformanceAllowDeprecatedGpu,
            PerformancePreferredGpuDescription: (PerformancePreferredGpuDescription ?? string.Empty).Trim(),
            PerformancePreferredGpuInstancePath: (PerformancePreferredGpuInstancePath ?? string.Empty).Trim(),
            DeploymentWithPause: DeploymentWithPause,
            StartsWithScript: (StartsWithScript ?? string.Empty).Trim(),
            EndsWithScript: (EndsWithScript ?? string.Empty).Trim(),
            CopilotWithScript: CopilotWithScript,
            ManualStopWithScript: ManualStopWithScript,
            BlockSleep: BlockSleep,
            BlockSleepWithScreenOn: BlockSleepWithScreenOn,
            EnablePenguin: EnablePenguin,
            EnableYituliu: EnableYituliu,
            PenguinId: (PenguinId ?? string.Empty).Trim(),
            TaskTimeoutMinutes: Math.Max(0, TaskTimeoutMinutes),
            ReminderIntervalMinutes: Math.Max(1, ReminderIntervalMinutes));
    }

    private UiOperationResult ValidateStartPerformanceSnapshot(StartPerformanceSettingsSnapshot snapshot)
    {
        if (snapshot.EmulatorWaitSeconds < EmulatorWaitSecondsMin || snapshot.EmulatorWaitSeconds > EmulatorWaitSecondsMax)
        {
            return UiOperationResult.Fail(
                UiErrorCode.EmulatorWaitSecondsOutOfRange,
                string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.StartPerformance.Validation.EmulatorWaitSecondsOutOfRange",
                        "Emulator wait seconds must be within {0}-{1}."),
                    EmulatorWaitSecondsMin,
                    EmulatorWaitSecondsMax));
        }

        if (!snapshot.OpenEmulatorAfterLaunch)
        {
            return UiOperationResult.Ok(
                RootTexts.GetOrDefault(
                    "Settings.StartPerformance.Validation.Passed",
                    "Start/performance settings validation passed."));
        }

        if (string.IsNullOrWhiteSpace(snapshot.EmulatorPath))
        {
            return UiOperationResult.Fail(
                UiErrorCode.EmulatorPathMissing,
                RootTexts.GetOrDefault(
                    "Settings.StartPerformance.Validation.EmulatorPathRequired",
                    "Emulator path is required when automatic emulator launch is enabled."));
        }

        if (!File.Exists(snapshot.EmulatorPath))
        {
            return UiOperationResult.Fail(
                UiErrorCode.EmulatorPathNotFound,
                string.Format(
                    RootTexts.GetOrDefault(
                        "Settings.StartPerformance.Validation.EmulatorPathNotFound",
                        "Emulator path does not exist: {0}"),
                    snapshot.EmulatorPath));
        }

        return UiOperationResult.Ok(
            RootTexts.GetOrDefault(
                "Settings.StartPerformance.Validation.Passed",
                "Start/performance settings validation passed."));
    }

    private StartPerformanceSettingsSnapshot ReadStartPerformanceSnapshot(
        UnifiedConfig config,
        ICollection<string> warnings)
    {
        var emulatorPath = ReadProfileString(config, ConfigurationKeys.EmulatorPath, string.Empty).Trim();
        var emulatorAddCommand = ReadProfileString(config, ConfigurationKeys.EmulatorAddCommand, string.Empty).Trim();
        var preferredGpuDescription = ReadProfileString(config, ConfigurationKeys.PerformancePreferredGpuDescription, string.Empty).Trim();
        var preferredGpuInstancePath = ReadProfileString(config, ConfigurationKeys.PerformancePreferredGpuInstancePath, string.Empty).Trim();

        var rawWaitSeconds = ReadProfileInt(config, ConfigurationKeys.EmulatorWaitSeconds, DefaultEmulatorWaitSeconds);
        var emulatorWaitSeconds = Math.Clamp(rawWaitSeconds, EmulatorWaitSecondsMin, EmulatorWaitSecondsMax);
        if (emulatorWaitSeconds != rawWaitSeconds)
        {
            warnings.Add(
                FormatSettingsText(
                    "Settings.StartPerformance.Warning.EmulatorWaitSecondsClamped",
                    "模拟器等待秒数已从 {0} 限制为 {1}。",
                    rawWaitSeconds,
                    emulatorWaitSeconds));
        }

        return new StartPerformanceSettingsSnapshot(
            RunDirectly: ReadProfileBoolFlexible(config, ConfigurationKeys.RunDirectly, false),
            MinimizeDirectly: ReadGlobalBoolFlexible(config, ConfigurationKeys.MinimizeDirectly, false),
            OpenEmulatorAfterLaunch: ReadProfileBoolFlexible(config, ConfigurationKeys.StartEmulator, false),
            EmulatorPath: emulatorPath,
            EmulatorAddCommand: emulatorAddCommand,
            EmulatorWaitSeconds: emulatorWaitSeconds,
            PerformanceUseGpu: ReadProfileBoolFlexible(config, ConfigurationKeys.PerformanceUseGpu, false),
            PerformanceAllowDeprecatedGpu: ReadProfileBoolFlexible(config, ConfigurationKeys.PerformanceAllowDeprecatedGpu, false),
            PerformancePreferredGpuDescription: preferredGpuDescription,
            PerformancePreferredGpuInstancePath: preferredGpuInstancePath,
            DeploymentWithPause: ReadProfileBoolFlexible(config, ConfigurationKeys.RoguelikeDeploymentWithPause, false),
            StartsWithScript: ReadProfileString(config, ConfigurationKeys.StartsWithScript, string.Empty).Trim(),
            EndsWithScript: ReadProfileString(config, ConfigurationKeys.EndsWithScript, string.Empty).Trim(),
            CopilotWithScript: ReadProfileBoolFlexible(config, ConfigurationKeys.CopilotWithScript, false),
            ManualStopWithScript: ReadProfileBoolFlexible(config, ConfigurationKeys.ManualStopWithScript, false),
            BlockSleep: ReadProfileBoolFlexible(config, ConfigurationKeys.BlockSleep, false),
            BlockSleepWithScreenOn: ReadProfileBoolFlexible(config, ConfigurationKeys.BlockSleepWithScreenOn, true),
            EnablePenguin: ReadProfileBoolFlexible(config, ConfigurationKeys.EnablePenguin, true),
            EnableYituliu: ReadProfileBoolFlexible(config, ConfigurationKeys.EnableYituliu, true),
            PenguinId: ReadProfileString(config, ConfigurationKeys.PenguinId, string.Empty).Trim(),
            TaskTimeoutMinutes: Math.Max(0, ReadProfileInt(config, ConfigurationKeys.TaskTimeoutMinutes, DefaultTaskTimeoutMinutes)),
            ReminderIntervalMinutes: Math.Max(1, ReadProfileInt(config, ConfigurationKeys.ReminderIntervalMinutes, DefaultReminderIntervalMinutes)));
    }

    private static bool HasExplicitStartPerformanceConfigValues(
        UnifiedConfig config,
        StartPerformanceSettingsSnapshot snapshot)
    {
        if (!config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            return false;
        }

        return HasExplicitSettingValues(config.GlobalValues, snapshot.ToGlobalSettingUpdates())
               && HasExplicitSettingValues(profile.Values, snapshot.ToProfileSettingUpdates());
    }

    private static bool HasExplicitSettingValues(
        IReadOnlyDictionary<string, JsonNode?> values,
        IReadOnlyDictionary<string, string> expectedValues)
    {
        foreach (var (key, expectedValue) in expectedValues)
        {
            if (!values.TryGetValue(key, out var node) || node is null)
            {
                return false;
            }

            string actualValue;
            if (node is JsonValue jsonValue
                && jsonValue.TryGetValue(out string? textValue)
                && textValue is not null)
            {
                actualValue = textValue;
            }
            else
            {
                actualValue = node.ToString();
            }

            if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void NormalizeUnsupportedGpuSettingsInConfig(UnifiedConfig config, ICollection<string> warnings)
    {
        var supportMode = DetermineGpuSupportModeForNormalization();

        if (supportMode == GpuPlatformSupportMode.WindowsSupported)
        {
            return;
        }

        var normalized = NormalizeUnsupportedGpuSettings(config.GlobalValues);
        foreach (var profile in config.Profiles.Values)
        {
            normalized |= NormalizeUnsupportedGpuSettings(profile.Values);
        }

        if (normalized)
        {
            var message = RootTexts.GetOrDefault(
                "Settings.StartPerformance.Validation.GpuFallbackApplied",
                "当前平台不支持 GPU OCR，已移除不兼容 GPU 设置并回退为 CPU OCR。");
            warnings.Add(message);
        }
    }

    private GpuPlatformSupportMode DetermineGpuSupportModeForNormalization()
    {
        // Keep normalization aligned with the injected capability service so tests and
        // custom platform bundles can emulate Windows GPU behavior on non-Windows hosts.
        return Runtime.Platform.GpuCapabilityService is UnsupportedGpuCapabilityService
            ? GpuPlatformSupportMode.Unsupported
            : GpuPlatformSupportMode.WindowsSupported;
    }

    private static bool NormalizeUnsupportedGpuSettings(IDictionary<string, JsonNode?> values)
    {
        if (!ContainsUnsafeGpuSettings(values))
        {
            return false;
        }

        values.Remove(ConfigurationKeys.PerformanceUseGpu);
        values.Remove(ConfigurationKeys.PerformanceAllowDeprecatedGpu);
        values.Remove(ConfigurationKeys.PerformancePreferredGpuDescription);
        values.Remove(ConfigurationKeys.PerformancePreferredGpuInstancePath);
        return true;
    }

    private static bool ContainsUnsafeGpuSettings(IDictionary<string, JsonNode?> values)
    {
        return ReadGpuBool(values, ConfigurationKeys.PerformanceUseGpu)
               || ReadGpuBool(values, ConfigurationKeys.PerformanceAllowDeprecatedGpu)
               || !string.IsNullOrWhiteSpace(ReadGpuString(values, ConfigurationKeys.PerformancePreferredGpuDescription))
               || !string.IsNullOrWhiteSpace(ReadGpuString(values, ConfigurationKeys.PerformancePreferredGpuInstancePath));
    }

    private static bool ReadGpuBool(IDictionary<string, JsonNode?> values, string key)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool parsedBool))
            {
                return parsedBool;
            }

            if (value.TryGetValue(out int parsedInt))
            {
                return parsedInt != 0;
            }

            if (value.TryGetValue(out string? parsedText))
            {
                if (bool.TryParse(parsedText, out var parsed))
                {
                    return parsed;
                }

                if (int.TryParse(parsedText, out parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        var raw = node.ToString();
        if (bool.TryParse(raw, out var fallbackParsed))
        {
            return fallbackParsed;
        }

        return int.TryParse(raw, out var fallbackInt) && fallbackInt != 0;
    }

    private static string ReadGpuString(IDictionary<string, JsonNode?> values, string key)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue(out string? parsedText) && parsedText is not null)
        {
            return parsedText.Trim();
        }

        return node.ToString().Trim();
    }

    private void ApplyStartPerformanceSnapshotWithoutDirtyTracking(
        StartPerformanceSettingsSnapshot snapshot,
        bool refreshGpuUi = true)
    {
        _suppressStartPerformanceDirtyTracking = true;
        _suppressGpuUiRefresh = true;
        try
        {
            RunDirectly = snapshot.RunDirectly;
            MinimizeDirectly = snapshot.MinimizeDirectly;
            OpenEmulatorAfterLaunch = snapshot.OpenEmulatorAfterLaunch;
            EmulatorPath = snapshot.EmulatorPath;
            EmulatorAddCommand = snapshot.EmulatorAddCommand;
            EmulatorWaitSeconds = snapshot.EmulatorWaitSeconds;
            PerformanceUseGpu = snapshot.PerformanceUseGpu;
            PerformanceAllowDeprecatedGpu = snapshot.PerformanceAllowDeprecatedGpu;
            PerformancePreferredGpuDescription = snapshot.PerformancePreferredGpuDescription;
            PerformancePreferredGpuInstancePath = snapshot.PerformancePreferredGpuInstancePath;
            DeploymentWithPause = snapshot.DeploymentWithPause;
            StartsWithScript = snapshot.StartsWithScript;
            EndsWithScript = snapshot.EndsWithScript;
            CopilotWithScript = snapshot.CopilotWithScript;
            ManualStopWithScript = snapshot.ManualStopWithScript;
            BlockSleep = snapshot.BlockSleep;
            BlockSleepWithScreenOn = snapshot.BlockSleepWithScreenOn;
            EnablePenguin = snapshot.EnablePenguin;
            EnableYituliu = snapshot.EnableYituliu;
            PenguinId = snapshot.PenguinId;
            TaskTimeoutMinutes = snapshot.TaskTimeoutMinutes;
            ReminderIntervalMinutes = snapshot.ReminderIntervalMinutes;
        }
        finally
        {
            _suppressGpuUiRefresh = false;
            _suppressStartPerformanceDirtyTracking = false;
        }

        if (refreshGpuUi)
        {
            RefreshGpuUiState();
        }
    }

}
