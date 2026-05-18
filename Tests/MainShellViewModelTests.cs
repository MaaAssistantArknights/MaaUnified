using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class MainShellViewModelTests
{
    [Theory]
    [InlineData(SessionState.Connected, false, true, false)]
    [InlineData(SessionState.Connected, true, false, false)]
    [InlineData(SessionState.Running, false, false, true)]
    [InlineData(SessionState.Idle, false, true, false)]
    public async Task CanStartExecution_ShouldMatchTrayStartState(
        SessionState sessionState,
        bool hasBlockingIssue,
        bool expectedStartEnabled,
        bool expectedStopEnabled)
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.ViewModel.InitializeAsync();
        await SetSessionStateAsync(fixture.Runtime.SessionService, sessionState);
        InvokeRefreshConfigValidationState(
            fixture.ViewModel,
            hasBlockingIssue ? [CreateBlockingIssue()] : []);
        await InvokeSyncTrayMenuStateAsync(fixture.ViewModel);

        Assert.Equal(expectedStartEnabled, fixture.ViewModel.CanStartExecution);
        Assert.Equal(expectedStopEnabled, fixture.ViewModel.CanStopExecution);
        Assert.NotNull(fixture.TrayService.LastMenuState);
        Assert.Equal(expectedStartEnabled, fixture.TrayService.LastMenuState!.StartEnabled);
        Assert.Equal(expectedStopEnabled, fixture.TrayService.LastMenuState.StopEnabled);
    }

    [Fact]
    public async Task InitializeAsync_ShouldCompleteBeforeDeferredCoreWarmupCompletes()
    {
        var bridge = new DelayedInitializeBridge(TimeSpan.FromMilliseconds(600));
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);

        var startupTask = fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.WaitForFirstScreenReadyAsync();

        Assert.True(fixture.ViewModel.TaskQueueRootPage.IsLoaded);
        Assert.False(fixture.ViewModel.IsCoreReady);
        Assert.False(startupTask.IsCompleted);
        Assert.Equal(0, bridge.InitializeCallCount);

        Assert.True(await WaitUntilAsync(
            () =>
                fixture.ViewModel.CopilotRootPage.IsLoaded
                && fixture.ViewModel.ToolboxRootPage.IsLoaded
                && fixture.ViewModel.SettingsRootPage.IsLoaded,
            retry: 160,
            delayMs: 25));
        Assert.False(fixture.ViewModel.IsCoreReady);
        Assert.Equal(0, bridge.InitializeCallCount);

        await startupTask;

        Assert.False(fixture.ViewModel.IsCoreReady);
        Assert.True(await WaitUntilAsync(() => bridge.InitializeCallCount == 1, retry: 160, delayMs: 25));
        Assert.True(await WaitUntilAsync(() => fixture.ViewModel.IsCoreReady, retry: 240, delayMs: 25));
    }

    [Fact]
    public async Task InitializeAsync_FirstScreenReady_ShouldNotWaitForDeferredOverlayTargetProbe()
    {
        var overlayFeatureService = new DelayedOverlayFeatureService(TimeSpan.FromMilliseconds(900));
        await using var fixture = await TestFixture.CreateAsync(overlayFeatureService: overlayFeatureService);

        var startupTask = fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.WaitForFirstScreenReadyAsync();

        Assert.True(fixture.ViewModel.TaskQueueRootPage.IsLoaded);
        Assert.False(startupTask.IsCompleted);
        Assert.False(overlayFeatureService.QueryCompleted);

        await startupTask;

        Assert.True(overlayFeatureService.QueryCompleted);
    }

    [Fact]
    public async Task WaitForStartupSnapshotReadyAsync_ShouldApplyShellSnapshotBeforeSettingsPageInitialization()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateStartupSnapshotConfigJson(),
            bridge: new DelayedInitializeBridge(TimeSpan.FromMilliseconds(200)));

        var startupTask = fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.WaitForStartupSnapshotReadyAsync();

        Assert.Equal("en-us", fixture.ViewModel.CurrentShellLanguage);
        Assert.Equal("Dark", fixture.ViewModel.AppliedTheme);
        Assert.Equal("en-us", fixture.ViewModel.TaskQueuePage.Texts.Language);
        Assert.Equal("Ctrl+Shift+Alt+G", fixture.ViewModel.SettingsPage.HotkeyShowGui);
        Assert.Equal("Ctrl+Shift+Alt+R", fixture.ViewModel.SettingsPage.HotkeyLinkStart);
        Assert.Equal(RootPageLoadState.NotStarted, fixture.ViewModel.SettingsRootPage.LoadState);

        await startupTask;
    }

    [Fact]
    public async Task ExecuteStartupLaunchBehaviorAsync_RunDirectlyConfigured_ShouldStartTaskQueue()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateLaunchBehaviorConfigJson(
                runDirectly: true,
                minimizeDirectly: false,
                openEmulatorAfterLaunch: false));
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.ExecuteStartupLaunchBehaviorAsync();

        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task ExecuteStartupLaunchBehaviorAsync_AllOptionsConfigured_ShouldInvokeStartupActionsOnceInOrder()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateLaunchBehaviorConfigJson(
                runDirectly: true,
                minimizeDirectly: true,
                openEmulatorAfterLaunch: true));
        await fixture.ViewModel.InitializeAsync();
        var calls = new List<string>();

        await fixture.ViewModel.ExecuteStartupLaunchBehaviorAsync(
            startEmulatorAsync: _ =>
            {
                calls.Add("emulator");
                return Task.FromResult(true);
            },
            startTaskQueueAsync: _ =>
            {
                calls.Add("start");
                return Task.CompletedTask;
            },
            minimizeWindowAsync: _ =>
            {
                calls.Add("minimize");
                return Task.CompletedTask;
            });

        await fixture.ViewModel.ExecuteStartupLaunchBehaviorAsync(
            startEmulatorAsync: _ =>
            {
                calls.Add("emulator-again");
                return Task.FromResult(true);
            },
            startTaskQueueAsync: _ =>
            {
                calls.Add("start-again");
                return Task.CompletedTask;
            },
            minimizeWindowAsync: _ =>
            {
                calls.Add("minimize-again");
                return Task.CompletedTask;
            });

        Assert.Equal(["minimize", "emulator", "start"], calls);
    }

    [Fact]
    public async Task InitializeAsync_CoreInitFailure_ShouldKeepDeferredPagesLoaded()
    {
        await using var fixture = await TestFixture.CreateAsync(
            bridge: new FailingInitializeBridge(CoreErrorCode.ResourceNotFound, "Client resource directory was not found."));

        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => fixture.ViewModel.TaskQueuePage.HasCoreInitializationMessage,
            retry: 160,
            delayMs: 25));
        Assert.False(fixture.ViewModel.IsCoreReady);
        Assert.True(fixture.ViewModel.TaskQueuePage.HasCoreInitializationMessage);
        Assert.True(fixture.ViewModel.CopilotRootPage.IsLoaded);
        Assert.True(fixture.ViewModel.ToolboxRootPage.IsLoaded);
        Assert.True(fixture.ViewModel.SettingsRootPage.IsLoaded);
    }

    [Fact]
    public async Task ConfigIssueDetails_ShouldOnlyKeepBlockingIssues_WithCompleteFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var issues = new[]
        {
            new ConfigValidationIssue
            {
                Scope = string.Empty,
                Code = "BlockingCode",
                Field = string.Empty,
                Message = "Need fix",
                Blocking = true,
                ProfileName = null,
                TaskIndex = null,
                TaskName = string.Empty,
                SuggestedAction = null,
            },
            new ConfigValidationIssue
            {
                Scope = "TaskValidation",
                Code = "WarnOnly",
                Field = "times",
                Message = "Warning",
                Blocking = false,
            },
        };

        InvokeRefreshConfigValidationState(fixture.ViewModel, issues);

        var detail = Assert.Single(fixture.ViewModel.ConfigIssueDetails);
        Assert.Equal("-", detail.Scope);
        Assert.Equal("BlockingCode", detail.Code);
        Assert.Equal("-", detail.Field);
        Assert.True(detail.Blocking);
        Assert.Equal("-", detail.ProfileName);
        Assert.Equal("-", detail.TaskIndex);
        Assert.Equal("-", detail.TaskName);
        Assert.Equal("Need fix", detail.Message);
        Assert.Equal("-", detail.SuggestedAction);
    }

    [Fact]
    public async Task InitializeAsync_WithBlockingValidationIssues_ShouldDisableStartAndExposeIssueDetails()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateBlockingConfigJson());
        await fixture.ViewModel.InitializeAsync();
        await SetSessionStateAsync(fixture.Runtime.SessionService, SessionState.Connected);
        await InvokeSyncTrayMenuStateAsync(fixture.ViewModel);

        Assert.True(fixture.ViewModel.HasBlockingConfigIssues);
        Assert.False(fixture.ViewModel.CanStartExecution);
        Assert.NotEmpty(fixture.ViewModel.ConfigIssueDetails);

        var detail = fixture.ViewModel.ConfigIssueDetails[0];
        Assert.NotEqual("-", detail.Scope);
        Assert.NotEqual("-", detail.Code);
        Assert.NotEqual("-", detail.Field);
        Assert.NotEqual("-", detail.Message);
        Assert.NotEqual("-", detail.SuggestedAction);
    }

    [Fact]
    public async Task InitializeAsync_OutdatedSchema_ShowsMigrationDialog()
    {
        var dialogService = new CapturingDialogService();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateOutdatedSchemaConfigJson(),
            dialogService: dialogService);

        await fixture.ViewModel.InitializeAsync();

        Assert.Equal(1, dialogService.ShowTextCallCount);
        Assert.NotNull(dialogService.LastTextRequest);
        Assert.Equal("App.Shell.Config.SchemaMigration", dialogService.LastTextScope);
        Assert.Equal(
            fixture.ViewModel.RootTexts["Main.SchemaMigration.Dialog.Title"],
            dialogService.LastTextRequest!.Title);
        Assert.Equal(
            fixture.ViewModel.RootTexts["Main.SchemaMigration.Dialog.Confirm"],
            dialogService.LastTextRequest.ConfirmText);
        Assert.Equal(
            fixture.ViewModel.RootTexts["Main.SchemaMigration.Dialog.Cancel"],
            dialogService.LastTextRequest.CancelText);
        Assert.True(dialogService.LastTextRequest.ReadOnlyContent);
        Assert.True(dialogService.LastTextRequest.MultiLine);
        Assert.Contains("当前版本: v1", dialogService.LastTextRequest.DefaultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_OutdatedSchema_DialogUnavailable_DoesNotBlockStartup()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateOutdatedSchemaConfigJson(),
            dialogService: NoOpAppDialogService.Instance);

        await fixture.ViewModel.InitializeAsync();

        Assert.NotEqual("Initializing...", fixture.ViewModel.GlobalStatus);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Config.SchemaMigration.DialogUnavailable"));
    }

    [Fact]
    public async Task InitializeAsync_OutdatedSchema_DialogOpen_ShouldNotBlockDeferredStartupOrVersionChecks()
    {
        var dialogService = new CapturingDialogService
        {
            TextTask = new TaskCompletionSource<DialogCompletion<TextDialogPayload>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateOutdatedSchemaConfigJson(),
            dialogService: dialogService,
            versionUpdateFeatureService: versionUpdate);

        var startupTask = fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => dialogService.ShowTextCallCount == 1,
            retry: 240,
            delayMs: 50));
        Assert.True(await WaitUntilAsync(
            () =>
                fixture.ViewModel.SettingsRootPage.IsLoaded
                && fixture.ViewModel.CopilotRootPage.IsLoaded
                && fixture.ViewModel.ToolboxRootPage.IsLoaded
                && versionUpdate.CheckForUpdatesCallCount >= 1
                && versionUpdate.CheckResourceCallCount >= 1,
            retry: 240,
            delayMs: 50));

        await startupTask.WaitAsync(TimeSpan.FromSeconds(10));

        dialogService.TextTask.TrySetResult(
            new DialogCompletion<TextDialogPayload>(
                DialogReturnSemantic.Confirm,
                new TextDialogPayload(dialogService.LastTextRequest?.DefaultText ?? string.Empty),
                "released"));
    }

    [Fact]
    public async Task InitializeAsync_AnnouncementDialogOpen_ShouldContinueDeferredStartup_AndNotBlockStartupVersionUpdate()
    {
        var dialogService = new CapturingDialogService
        {
            AnnouncementTask = new TaskCompletionSource<DialogCompletion<AnnouncementDialogPayload>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService();
        var announcementFeatureService = new ScriptedAnnouncementFeatureService(
            new AnnouncementState("cached announcement", false, false));
        await using var fixture = await TestFixture.CreateAsync(
            dialogService: dialogService,
            versionUpdateFeatureService: versionUpdate,
            announcementFeatureService: announcementFeatureService);

        var startupTask = fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => dialogService.AnnouncementCallCount == 1,
            retry: 240,
            delayMs: 50));
        Assert.Equal("App.Initialize.Announcement.Dialog", dialogService.LastAnnouncementScope);
        Assert.True(await WaitUntilAsync(
            () =>
                fixture.ViewModel.SettingsRootPage.IsLoaded
                && fixture.ViewModel.CopilotRootPage.IsLoaded
                && fixture.ViewModel.ToolboxRootPage.IsLoaded,
            retry: 240,
            delayMs: 50));
        await startupTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntilAsync(
            () =>
                versionUpdate.CheckForUpdatesCallCount >= 1
                && versionUpdate.CheckResourceCallCount >= 1,
            retry: 240,
            delayMs: 50));

        dialogService.AnnouncementTask.TrySetResult(
            new DialogCompletion<AnnouncementDialogPayload>(
                DialogReturnSemantic.Close,
                null,
                "released"));
    }

    [Fact]
    public async Task AchievementToast_ImmediatePointerPause_ShouldNotFreezeCountdown()
    {
        var dismissedIds = new List<string>();
        var toast = new AchievementToastItemViewModel(
            "ToastImmediateHover",
            "Achievement Unlocked",
            "Immediate hover",
            "Countdown should keep moving.",
            "#42A5F5",
            autoClose: true,
            DateTimeOffset.UtcNow,
            dismissedIds.Add);

        try
        {
            toast.StartPresentation();
            Dispatcher.UIThread.RunJobs();
            toast.PauseCloseCountdown();
            Assert.False(toast.IsCloseCountdownPaused);

            await Task.Delay(550);
            toast.PauseCloseCountdown();
            Assert.True(toast.IsCloseCountdownPaused);
            Assert.Empty(dismissedIds);
        }
        finally
        {
            toast.Dispose();
        }
    }

    [Fact]
    public async Task AchievementToast_AutoClose_ShouldDismissAfterVisibleCountdown()
    {
        var dismissedIds = new List<string>();
        var toast = new AchievementToastItemViewModel(
            "ToastAutoClose",
            "Achievement Unlocked",
            "Auto close",
            "Countdown should dismiss the toast.",
            "#42A5F5",
            autoClose: true,
            DateTimeOffset.UtcNow,
            dismissedIds.Add);

        try
        {
            toast.StartPresentation();
            Dispatcher.UIThread.RunJobs();
            SetAchievementToastRemainingSeconds(toast, 0.05d);

            Assert.True(await WaitUntilAsync(
                () =>
                {
                    Dispatcher.UIThread.RunJobs();
                    return dismissedIds.Any(id => string.Equals(id, "ToastAutoClose", StringComparison.Ordinal));
                },
                retry: 80,
                delayMs: 25));
        }
        finally
        {
            toast.Dispose();
        }
    }

    [Fact]
    public async Task AchievementToast_AutoClose_ShouldNotStartCountdownBeforePresentation()
    {
        var toast = new AchievementToastItemViewModel(
            "ToastAutoCloseBeforeLoaded",
            "Achievement Unlocked",
            "Auto close",
            "Countdown should wait until presentation starts.",
            "#42A5F5",
            autoClose: true,
            DateTimeOffset.UtcNow,
            _ => { });

        try
        {
            await Task.Delay(200);
            Assert.Equal(7d, GetAchievementToastRemainingSeconds(toast), precision: 2);

            toast.StartPresentation();
            await Task.Delay(200);
            Assert.True(GetAchievementToastRemainingSeconds(toast) < 6.95d);
        }
        finally
        {
            Dispatcher.UIThread.RunJobs();
            toast.Dispose();
        }
    }

    [Fact]
    public async Task AchievementToast_StartupRelease_ShouldWaitForAnnouncementCompletion()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var announcementGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.ViewModel.PrepareStartupAchievementToastAnnouncementGate(announcementGate.Task);
        fixture.ViewModel.BeginAchievementToastStartupRelease();

        var unlockResult = fixture.Runtime.AchievementTrackerService.Unlock("Linguist");
        Assert.True(unlockResult.Success);
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fixture.ViewModel.AchievementToasts);

        announcementGate.TrySetResult(true);

        Assert.True(await WaitUntilAsync(
            () =>
            {
                Dispatcher.UIThread.RunJobs();
                return fixture.ViewModel.AchievementToasts.Count == 1;
            },
            retry: 80,
            delayMs: 25));
    }

    [Fact]
    public async Task AchievementToast_StartupRelease_ShouldProceedImmediatelyWhenAnnouncementNotNeeded()
    {
        await using var fixture = await TestFixture.CreateAsync();

        fixture.ViewModel.PrepareStartupAchievementToastAnnouncementGate(Task.CompletedTask);
        fixture.ViewModel.BeginAchievementToastStartupRelease();

        var unlockResult = fixture.Runtime.AchievementTrackerService.Unlock("Linguist");
        Assert.True(unlockResult.Success);

        Assert.True(await WaitUntilAsync(
            () =>
            {
                Dispatcher.UIThread.RunJobs();
                return fixture.ViewModel.AchievementToasts.Count == 1;
            },
            retry: 40,
            delayMs: 25));
    }

    [Fact]
    public async Task InitializeAsync_CoreInitFailure_ShouldRaiseDialogError()
    {
        await using var fixture = await TestFixture.CreateAsync(
            bridge: new FailingInitializeBridge(CoreErrorCode.ResourceNotFound, "Client resource directory was not found."));
        var raised = new List<DialogErrorRaisedEvent>();
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised.Add(e);

        await fixture.ViewModel.InitializeAsync();
        Assert.True(await WaitUntilAsync(
            () => raised.Any(e => string.Equals(e.Context, "App.CoreWarmup", StringComparison.Ordinal)),
            retry: 160,
            delayMs: 25));

        var coreFailure = Assert.Single(
            raised,
            e => string.Equals(e.Context, "App.CoreWarmup", StringComparison.Ordinal));
        Assert.Equal(CoreErrorCode.ResourceNotFound.ToString(), coreFailure.Result.Error?.Code);
        Assert.Contains("Core 初始化失败", coreFailure.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ScreencapCostCallback_ShouldRefreshConnectionSummary()
    {
        var bridge = new FakeBridge();
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);
        await fixture.ViewModel.InitializeAsync();
        Assert.True(await WaitUntilAsync(() => fixture.ViewModel.IsCoreReady, retry: 160, delayMs: 25));

        var timestamp = new DateTimeOffset(2026, 3, 13, 1, 2, 3, TimeSpan.Zero);
        bridge.Publish(
            new CoreCallbackEvent(
                2,
                "ConnectionInfo",
                """{"what":"ScreencapCost","details":{"min":123,"avg":234,"max":345}}""",
                timestamp));

        var expected = ConnectionGameSharedStateViewModel.FormatScreencapCost(123, 234, 345, timestamp);
        Assert.True(await WaitUntilAsync(
            () =>
            {
                Dispatcher.UIThread.RunJobs(null);
                return string.Equals(
                    fixture.ViewModel.ConnectionGameSharedState.ScreencapCost,
                    expected,
                    StringComparison.Ordinal);
            }));
    }

    [Fact]
    public async Task ExecuteSwitchLanguageAsync_ShouldUseShellFeatureService()
    {
        var shellSpy = new SpyShellFeatureService("ja-jp");
        await using var fixture = await TestFixture.CreateAsync(shellSpy);

        await fixture.ViewModel.ExecuteSwitchLanguageAsync("ja-jp");

        Assert.True(await WaitUntilAsync(() => string.Equals(fixture.ViewModel.SettingsPage.Language, "ja-jp", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("zh-cn", shellSpy.LastCurrentLanguage);
        Assert.Equal("ja-jp", shellSpy.LastTargetLanguage);
        Assert.Equal("ja-jp", fixture.ViewModel.SettingsPage.Language);
        Assert.True(fixture.TrayService.InitializeCallCount > 0);
        Assert.Equal(MainShellViewModel.AppDisplayName, fixture.TrayService.LastAppTitle);
    }

    [Fact]
    public async Task SwitchLanguageToAsync_ShouldSyncSettingsTaskQueueAndTrayText()
    {
        var shellSpy = new SpyShellFeatureService("pallas");
        await using var fixture = await TestFixture.CreateAsync(shellSpy);
        var changedProperties = new List<string>();
        fixture.ViewModel.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };
        var tabTitleBefore = fixture.ViewModel.RootTexts["Main.Tab.TaskQueue"];

        await fixture.ViewModel.SwitchLanguageToAsync("pallas");

        Assert.True(await WaitUntilAsync(() =>
            string.Equals(fixture.ViewModel.SettingsPage.Language, "pallas", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fixture.ViewModel.TaskQueuePage.Texts.Language, "pallas", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("zh-cn", shellSpy.LastCurrentLanguage);
        Assert.Equal("pallas", shellSpy.LastTargetLanguage);
        Assert.Equal("pallas", fixture.ViewModel.SettingsPage.Language);
        Assert.Equal("pallas", fixture.ViewModel.TaskQueuePage.Texts.Language);
        Assert.NotNull(fixture.TrayService.LastMenuText);
        Assert.False(string.IsNullOrWhiteSpace(fixture.TrayService.LastMenuText!.SwitchLanguage));
        Assert.Contains(nameof(MainShellViewModel.RootTexts), changedProperties);
        Assert.NotEqual(tabTitleBefore, fixture.ViewModel.RootTexts["Main.Tab.TaskQueue"]);
    }

    [Fact]
    public async Task SwitchLanguageCycleAsync_ShouldSyncSettingsTaskQueueAndTrayText()
    {
        var shellSpy = new SpyShellFeatureService("en-us");
        await using var fixture = await TestFixture.CreateAsync(shellSpy);

        await fixture.ViewModel.SwitchLanguageCycleAsync();

        Assert.True(await WaitUntilAsync(() =>
            string.Equals(fixture.ViewModel.SettingsPage.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fixture.ViewModel.TaskQueuePage.Texts.Language, "en-us", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("zh-cn", shellSpy.LastCurrentLanguage);
        Assert.Null(shellSpy.LastTargetLanguage);
        Assert.Equal("en-us", fixture.ViewModel.SettingsPage.Language);
        Assert.Equal("en-us", fixture.ViewModel.TaskQueuePage.Texts.Language);
        Assert.NotNull(fixture.TrayService.LastMenuText);
        Assert.False(string.IsNullOrWhiteSpace(fixture.TrayService.LastMenuText!.SwitchLanguage));
    }

    [Fact]
    public async Task SettingsPage_ChangeLanguageAsync_ShouldRefreshOpenStartUpTaskTexts()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.TaskQueuePage.SelectedTask = Assert.Single(
            fixture.ViewModel.TaskQueuePage.Tasks,
            task => string.Equals(task.Type, TaskModuleTypes.StartUp, StringComparison.OrdinalIgnoreCase));
        await fixture.ViewModel.TaskQueuePage.WaitForPendingBindingAsync();

        var zhTitle = fixture.ViewModel.TaskQueuePage.StartUpModule.Texts["StartUp.Title"];
        var zhManualRun = fixture.ViewModel.TaskQueuePage.StartUpModule.Texts["StartUp.AccountSwitchManualRun"];

        await fixture.ViewModel.SettingsPage.ChangeLanguageAsync("en-us");

        Assert.True(await WaitUntilAsync(() =>
            string.Equals(fixture.ViewModel.SettingsPage.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fixture.ViewModel.TaskQueuePage.Texts.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fixture.ViewModel.TaskQueuePage.StartUpModule.Texts.Language, "en-us", StringComparison.OrdinalIgnoreCase)));
        await fixture.ViewModel.TaskQueuePage.WaitForPendingBindingAsync();

        Assert.NotEqual(zhTitle, fixture.ViewModel.TaskQueuePage.StartUpModule.Texts["StartUp.Title"]);
        Assert.NotEqual(zhManualRun, fixture.ViewModel.TaskQueuePage.StartUpModule.Texts["StartUp.AccountSwitchManualRun"]);
    }

    [Fact]
    public void LanguageQuickCycle_ShouldStayWithinStableLocales()
    {
        Assert.Equal("en-us", UiLanguageCatalog.NextInQuickCycle("zh-cn"));
        Assert.Equal("zh-cn", UiLanguageCatalog.NextInQuickCycle("en-us"));
        Assert.Equal("zh-cn", UiLanguageCatalog.NextInQuickCycle("ja-jp"));
    }

    [Fact]
    public async Task ExecuteTrayCommandAsync_SwitchLanguageCycle_ShouldUseShellFeatureService()
    {
        var shellSpy = new SpyShellFeatureService("en-us");
        await using var fixture = await TestFixture.CreateAsync(shellSpy);

        var action = await fixture.ViewModel.ExecuteTrayCommandAsync(TrayCommandId.SwitchLanguage, "test-tray");

        Assert.Equal(ShellUiAction.None, action);
        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("zh-cn", shellSpy.LastCurrentLanguage);
        Assert.Null(shellSpy.LastTargetLanguage);
        Assert.Contains(fixture.ViewModel.GrowlMessages, msg => msg.Contains("语言切换为: en-us", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteTrayLanguageSwitchAsync_TargetLanguage_UsesTrayScopeAndGrowl()
    {
        var shellSpy = new SpyShellFeatureService("ja-jp");
        await using var fixture = await TestFixture.CreateAsync(shellSpy);

        await fixture.ViewModel.ExecuteTrayLanguageSwitchAsync("ja-jp", "window-shell-menu");

        Assert.True(await WaitUntilAsync(() => string.Equals(fixture.ViewModel.SettingsPage.Language, "ja-jp", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("ja-jp", shellSpy.LastTargetLanguage);
        Assert.Equal("ja-jp", fixture.ViewModel.SettingsPage.Language);
        Assert.Contains(fixture.ViewModel.GrowlMessages, msg => msg.Contains("语言切换为: ja-jp", StringComparison.Ordinal));
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "App.Shell.Tray.SwitchLanguage"));
    }

    [Fact]
    public async Task ApplyGuiSettingsAsync_ShouldNotChangeCurrentLanguage()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.ViewModel.InitializeAsync();

        var applyMethod = typeof(MainShellViewModel).GetMethod(
            "ApplyGuiSettingsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        var snapshot = fixture.ViewModel.SettingsPage.CurrentGuiSnapshot with
        {
            Theme = "Dark",
        };

        var applyTask = applyMethod!.Invoke(fixture.ViewModel, [snapshot, CancellationToken.None]) as Task;
        Assert.NotNull(applyTask);
        await applyTask!;

        Assert.Equal("zh-cn", fixture.ViewModel.CurrentShellLanguage);
        Assert.Equal("zh-cn", fixture.ViewModel.SettingsPage.Language);
    }

    [Fact]
    public async Task SwitchLanguageToAsync_WhenCoordinatorFails_ShouldKeepRuntimeLanguage()
    {
        var shellSpy = new SpyShellFeatureService("ja-jp");
        var coordinator = new FailingUiLanguageCoordinator("zh-cn");
        await using var fixture = await TestFixture.CreateAsync(shellSpy, uiLanguageCoordinator: coordinator);

        await fixture.ViewModel.SwitchLanguageToAsync("ja-jp");

        Assert.Equal(1, shellSpy.SwitchLanguageCallCount);
        Assert.Equal("zh-cn", fixture.ViewModel.CurrentShellLanguage);
        Assert.Equal("zh-cn", fixture.ViewModel.SettingsPage.Language);
        Assert.Equal("zh-cn", fixture.ViewModel.TaskQueuePage.Texts.Language);
        Assert.True(string.IsNullOrWhiteSpace(ReadGlobalString(fixture.Runtime.ConfigurationService, ConfigurationKeys.Localization)));
    }

    [Fact]
    public async Task ReportLocalizationFallback_ShouldDeduplicateByScopeLanguageAndKey()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var first = new LocalizationFallbackInfo("TaskQueue.Localization", "ja-jp", "TaskQueue.Unknown.Key", "en-us");
        var second = new LocalizationFallbackInfo("TaskQueue.Localization", "ja-jp", "TaskQueue.Unknown.Key.2", "en-us");

        fixture.ViewModel.ReportLocalizationFallback(first);
        fixture.ViewModel.ReportLocalizationFallback(first);
        fixture.ViewModel.ReportLocalizationFallback(second);

        var lines = await WaitForEventLinesAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Localization.Fallback",
            expectedCount: 2);
        Assert.Equal(2, lines.Count);
        Assert.Single(lines.Where(line => line.Contains("key=TaskQueue.Unknown.Key; fallback=", StringComparison.Ordinal)));
        Assert.Single(lines.Where(line => line.Contains("key=TaskQueue.Unknown.Key.2; fallback=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SettingsHotkeyError_WithUnknownCode_ShouldReportLocalizationFallback()
    {
        await using var fixture = await TestFixture.CreateAsync(hotkeyService: new UnknownErrorHotkeyService());
        await fixture.ViewModel.SettingsPage.InitializeAsync();

        await fixture.ViewModel.SettingsPage.RegisterHotkeysAsync();

        Assert.True(fixture.ViewModel.SettingsPage.HasHotkeyErrorMessage);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Localization.Fallback",
            retry: 80));
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "key=PlatformCapability.Error.HotkeyErrorNotMapped",
            retry: 80));
    }

    [Fact]
    public async Task ExecuteTrayCommandAsync_ForceShowAndExit_ShouldReturnUiAction()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var showAction = await fixture.ViewModel.ExecuteTrayCommandAsync(TrayCommandId.ForceShow, "test-tray");
        var closeAction = await fixture.ViewModel.ExecuteTrayCommandAsync(TrayCommandId.Exit, "test-tray");

        Assert.Equal(ShellUiAction.ShowMainWindow, showAction);
        Assert.Equal(ShellUiAction.CloseMainWindow, closeAction);
    }

    [Fact]
    public async Task ExecuteTrayCommandAsync_Restart_ShouldUseLifecycleServiceAndCloseWindow()
    {
        var lifecycle = new SpyAppLifecycleService(UiOperationResult.Ok("Restart process launched."));
        await using var fixture = await TestFixture.CreateAsync(appLifecycleService: lifecycle);

        var action = await fixture.ViewModel.ExecuteTrayCommandAsync(TrayCommandId.Restart, "test-tray");

        Assert.Equal(1, lifecycle.RestartCallCount);
        Assert.Equal(ShellUiAction.CloseMainWindow, action);
        Assert.Contains(fixture.ViewModel.GrowlMessages, msg => msg.Contains("重启命令已触发", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetTrayVisibleAsync_ShouldRecordDiagnosticsAndPushGrowl()
    {
        await using var fixture = await TestFixture.CreateAsync();

        await fixture.ViewModel.SetTrayVisibleAsync(false);

        Assert.Contains(fixture.ViewModel.GrowlMessages, msg => msg.Contains("set-visible", StringComparison.Ordinal));
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "App.Shell.Tray.SetVisible"));
    }

    [Fact]
    public async Task ToggleOverlayFromTrayAsync_ShouldRecordDiagnosticsAndPushGrowl()
    {
        await using var fixture = await TestFixture.CreateAsync();

        await fixture.ViewModel.ToggleOverlayFromTrayAsync();

        Assert.Contains(fixture.ViewModel.GrowlMessages, msg => msg.Contains("Overlay 已", StringComparison.Ordinal));
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "App.Shell.Tray.ToggleOverlay"));
    }

    [Fact]
    public async Task ExecuteManualImportAsync_ShouldRefreshTaskQueueSettingsAndConnectionState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ],
                  "ConnectAddress": "10.8.0.1:5555",
                  "ConnectConfig": "Mumu",
                  "AdbPath": "/tmp/adb-imported"
                }
              },
              "GUI": {
                "Localization": "en-us"
              }
            }
            """);

        fixture.ViewModel.SelectedImportSource = ImportSource.GuiNewOnly;

        await fixture.ViewModel.ExecuteManualImportAsync();

        Assert.Equal("en-us", fixture.ViewModel.SettingsPage.Language);
        Assert.Equal("10.8.0.1:5555", fixture.ViewModel.ConnectionGameSharedState.ConnectAddress);
        Assert.Equal("Mumu", fixture.ViewModel.ConnectionGameSharedState.ConnectConfig);
        Assert.Equal("/tmp/adb-imported", fixture.ViewModel.ConnectionGameSharedState.AdbPath);
        Assert.Contains(
            fixture.ViewModel.TaskQueuePage.Tasks,
            task => string.Equals(task.Type, "Fight", StringComparison.Ordinal));
        Assert.True(TaskQueueContainsLog(
            fixture.ViewModel.TaskQueuePage,
            "已强行导入旧配置：gui.new.json"));
    }

    [Fact]
    public async Task ExecuteManualImportAsync_ShouldRefreshConnectionState_FromLegacyConnectionKeys()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ],
                  "Connect.Address": "172.16.1.8:6000",
                  "Connect.ConnectConfig": "BlueStacks",
                  "Connect.AdbPath": "/tmp/adb-legacy"
                }
              }
            }
            """);

        fixture.ViewModel.SelectedImportSource = ImportSource.GuiNewOnly;
        await fixture.ViewModel.ExecuteManualImportAsync();

        Assert.Equal("172.16.1.8:6000", fixture.ViewModel.ConnectionGameSharedState.ConnectAddress);
        Assert.Equal("BlueStacks", fixture.ViewModel.ConnectionGameSharedState.ConnectConfig);
        Assert.Equal("/tmp/adb-legacy", fixture.ViewModel.ConnectionGameSharedState.AdbPath);
    }

    [Fact]
    public async Task ExecuteManualImportAsync_ShouldEmitImportRefreshEvent()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        fixture.ViewModel.SelectedImportSource = ImportSource.GuiNewOnly;
        await fixture.ViewModel.ExecuteManualImportAsync();

        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "App.Shell.ImportLegacy.Refresh"));
    }

    [Theory]
    [InlineData(ImportSource.Auto)]
    [InlineData(ImportSource.GuiNewOnly)]
    [InlineData(ImportSource.GuiOnly)]
    public async Task ExecuteManualImportAsync_ShouldMapSelectedImportSource(
        ImportSource selectedImportSource)
    {
        var shellSpy = new SpyShellFeatureService("zh-cn");
        await using var fixture = await TestFixture.CreateAsync(shellService: shellSpy);

        fixture.ViewModel.SelectedImportSource = selectedImportSource;
        await fixture.ViewModel.ExecuteManualImportAsync();

        Assert.Equal(1, shellSpy.ImportLegacyCallCount);
        Assert.Equal(selectedImportSource, shellSpy.LastImportSource);
        Assert.True(shellSpy.LastImportManualImport);
    }

    [Fact]
    public async Task InitializeAsync_AutoImportedLegacyConfig_ShouldAppendImportLogToTaskQueue()
    {
        await using var fixture = await TestFixture.CreateAsync(preloadConfig: false);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => TaskQueueContainsLog(
                fixture.ViewModel.TaskQueuePage,
                "已自动加载并转换 gui.new.json")));
    }

    [Fact]
    public async Task SettingsProfileSwitch_ShouldRefreshTaskQueueAndConnectionState()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateSwitchableProfilesConfigJson());
        await fixture.ViewModel.InitializeAsync();

        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.Contains(
            fixture.ViewModel.TaskQueuePage.Tasks,
            task => string.Equals(task.Type, "Recruit", StringComparison.Ordinal));

        fixture.ViewModel.SettingsPage.ConfigurationManagerSelectedProfile = "Alt";
        await fixture.ViewModel.SettingsPage.SwitchConfigurationProfileAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(
                fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
                "Alt",
                StringComparison.OrdinalIgnoreCase)));
        Assert.True(await WaitUntilAsync(
            () => string.Equals(
                      fixture.ViewModel.ConnectionGameSharedState.ConnectAddress,
                      "10.0.0.2:5555",
                      StringComparison.Ordinal)
                  && string.Equals(
                      fixture.ViewModel.ConnectionGameSharedState.ConnectConfig,
                      "BlueStacks",
                      StringComparison.Ordinal)
                  && string.Equals(
                      fixture.ViewModel.ConnectionGameSharedState.AdbPath,
                      "/tmp/adb-alt",
                      StringComparison.Ordinal)));
        Assert.True(await WaitUntilAsync(
            () => fixture.ViewModel.TaskQueuePage.Tasks.Any(
                      task => string.Equals(task.Type, "Fight", StringComparison.Ordinal))
                  && fixture.ViewModel.TaskQueuePage.Tasks.All(
                      task => !string.Equals(task.Type, "Recruit", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task SettingsProfileSwitch_ShouldFlushPendingPostActionChangesBeforeSwitch()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateSwitchableProfilesConfigJson());
        await fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.TaskQueuePage.ReloadConfigurationContextAsync();

        fixture.ViewModel.TaskQueuePage.PostActionModule.Shutdown = true;
        fixture.ViewModel.SettingsPage.ConfigurationManagerSelectedProfile = "Alt";
        await fixture.ViewModel.SettingsPage.SwitchConfigurationProfileAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(
                fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
                "Alt",
                StringComparison.OrdinalIgnoreCase)));

        var defaultProfile = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles["Default"];
        var altProfile = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles["Alt"];
        var defaultPostAction = PostActionConfig.FromJson(defaultProfile.Values["TaskQueue.PostAction"]);
        var altPostAction = PostActionConfig.FromJson(altProfile.Values["TaskQueue.PostAction"]);

        Assert.True(defaultPostAction.Shutdown);
        Assert.False(altPostAction.Shutdown);
    }

    [Fact]
    public async Task SettingsProfileSwitch_ShouldRefreshTaskSelectionTaskParamsAndTaskQueueScopedConfig()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateTaskQueueScopedSwitchConfigJson());
        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(fixture.ViewModel.TaskQueuePage.SelectedTask?.Name, "Fight Default", StringComparison.Ordinal)));
        Assert.Equal("CE-6", fixture.ViewModel.TaskQueuePage.FightModule.Stage);
        Assert.Equal(SelectionBatchMode.Clear, fixture.ViewModel.TaskQueuePage.SelectionBatchMode);

        fixture.ViewModel.SettingsPage.ConfigurationManagerSelectedProfile = "Alt";
        await fixture.ViewModel.SettingsPage.SwitchConfigurationProfileAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(
                      fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
                      "Alt",
                      StringComparison.OrdinalIgnoreCase)
                  && string.Equals(
                      fixture.ViewModel.TaskQueuePage.SelectedTask?.Name,
                      "Rogue Alt",
                      StringComparison.Ordinal)));
        Assert.True(await WaitUntilAsync(
            () => fixture.ViewModel.TaskQueuePage.SelectionBatchMode == SelectionBatchMode.Inverse));
        Assert.True(await WaitUntilAsync(
            () => fixture.ViewModel.TaskQueuePage.RoguelikeModule.DelayAbortUntilCombatComplete == false));

        fixture.ViewModel.TaskQueuePage.SelectedTask = Assert.Single(
            fixture.ViewModel.TaskQueuePage.Tasks,
            task => string.Equals(task.Name, "Infrast Alt", StringComparison.Ordinal));
        await fixture.ViewModel.TaskQueuePage.WaitForPendingBindingAsync();
        Assert.Equal("243_layout_4_times_a_day.json", fixture.ViewModel.TaskQueuePage.InfrastModule.DefaultInfrast);
        Assert.Equal(65, fixture.ViewModel.TaskQueuePage.InfrastModule.DormThresholdPercent);

        fixture.ViewModel.TaskQueuePage.SelectedTask = Assert.Single(
            fixture.ViewModel.TaskQueuePage.Tasks,
            task => string.Equals(task.Name, "Fight Alt", StringComparison.Ordinal));
        await fixture.ViewModel.TaskQueuePage.WaitForPendingBindingAsync();
        Assert.Equal("1-7", fixture.ViewModel.TaskQueuePage.FightModule.Stage);
    }

    [Fact]
    public async Task SettingsProfileSwitch_WhenTargetProfileHasNoTaskSelection_ShouldPreserveCurrentTaskSelectionAndRefreshBoundParams()
    {
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateNoTaskSelectionSwitchConfigJson());
        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(fixture.ViewModel.TaskQueuePage.SelectedTask?.Name, "Start Default", StringComparison.Ordinal)));
        Assert.Equal("default-account", fixture.ViewModel.TaskQueuePage.StartUpModule.AccountName);

        fixture.ViewModel.SettingsPage.ConfigurationManagerSelectedProfile = "Alt";
        await fixture.ViewModel.SettingsPage.SwitchConfigurationProfileAsync();

        Assert.True(await WaitUntilAsync(
            () => string.Equals(
                      fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
                      "Alt",
                      StringComparison.OrdinalIgnoreCase)
                  && string.Equals(
                      fixture.ViewModel.TaskQueuePage.SelectedTask?.Name,
                      "Start Alt",
                      StringComparison.Ordinal)));
        Assert.True(await WaitUntilAsync(
            () => fixture.ViewModel.TaskQueuePage.StartUpModule.IsTaskBound
                  && string.Equals(
                      fixture.ViewModel.TaskQueuePage.StartUpModule.AccountName,
                      "alt-account",
                      StringComparison.Ordinal)));
        Assert.False(fixture.ViewModel.TaskQueuePage.ShowTaskConfigHint);
        Assert.NotNull(fixture.ViewModel.TaskQueuePage.SelectedTaskSettingsViewModel);
    }

    [Fact]
    public async Task RuntimeFactory_ShouldInjectShellFeatureService()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runtime = MAAUnifiedRuntimeFactory.Create(root);
        try
        {
            Assert.IsType<ShellFeatureService>(runtime.ShellFeatureService);
        }
        finally
        {
            await runtime.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Theory]
    [InlineData(100, true, false, 0.9)]
    [InlineData(80, true, false, 0.72)]
    [InlineData(130, true, false, 1.17)]
    [InlineData(200, true, false, 1.26)]
    [InlineData(100, false, true, 0.81)]
    [InlineData(80, false, true, 0.648)]
    [InlineData(130, false, true, 1.053)]
    [InlineData(200, false, true, 1.134)]
    [InlineData(100, false, false, 1.0)]
    [InlineData(80, false, false, 0.8)]
    [InlineData(40, false, false, 0.7)]
    [InlineData(200, false, false, 1.4)]
    public void ComputeEffectiveUiScaleFactor_AppliesPlatformBaseAndClamp(
        int uiScalePercent,
        bool isWindows,
        bool isMacOS,
        double expected)
    {
        var actual = MainShellViewModel.ComputeEffectiveUiScaleFactor(uiScalePercent, isWindows, isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void LegacyMainViewModelFile_ShouldNotExist()
    {
        var root = GetMaaUnifiedRoot();
        var legacyVmPath = Path.Combine(root, "App", "ViewModels", "MainViewModel.cs");
        Assert.False(File.Exists(legacyVmPath), "MainViewModel.cs should not exist after main shell unification.");
    }

    [Fact]
    public void MainWindow_OnSwitchLanguageToClick_ShouldUseTrayLanguageEntryPoint()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);
        Assert.DoesNotContain(
            "await VM.SwitchLanguageToAsync(targetLanguage);",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "await VM.ExecuteTrayLanguageSwitchAsync(targetLanguage, \"window-shell-menu\");",
            content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnManualUpdateClick_ShouldUseNonDialogCheckEntryPoint()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);
        Assert.DoesNotContain(
            "await VM.SettingsPage.CheckVersionUpdateWithDialogAsync();",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "CheckVersionUpdateAsync();",
            content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VersionUpdateSettingsView_OnCheckUpdateClick_ShouldUseNonDialogCheckEntryPoint()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml.cs");
        var content = File.ReadAllText(path);
        Assert.DoesNotContain(
            "await VM.CheckVersionUpdateWithDialogAsync();",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "await VM.CheckVersionUpdateAsync();",
            content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShouldExposeClickableWindowUpdatePrompts()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml");
        var content = File.ReadAllText(path);

        Assert.Contains("HasVisibleWindowUpdateInfo", content, StringComparison.Ordinal);
        Assert.Contains("Tapped=\"OnWindowUpdateOverlayTapped\"", content, StringComparison.Ordinal);
        Assert.Contains("HasVisibleWindowVersionUpdateInfo", content, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RootTexts[Main.Title.UpdateVersion]}\"", content, StringComparison.Ordinal);
        Assert.Contains("WindowVersionUpdateInfo", content, StringComparison.Ordinal);
        Assert.Contains("HasVisibleWindowResourceUpdateInfo", content, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RootTexts[Main.Title.UpdateResource]}\"", content, StringComparison.Ordinal);
        Assert.Contains("WindowResourceUpdateInfo", content, StringComparison.Ordinal);
        Assert.Contains("main-shell-update-close", content, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDismissWindowUpdateClick\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnManualUpdateClick\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnManualUpdateResourceClick\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupVersionUpdateCheck_ShouldDriveWindowPromptTexts()
    {
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: "pkg.zip",
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: false),
                "发现新版本：v2.0.0"),
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-03 10:00:00",
                    ReleaseNote: "episode",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: true,
                    DownloadUrl: null),
                "检测到资源更新。"),
        };
        await using var fixture = await TestFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        await fixture.ViewModel.SettingsPage.InitializeAsync();
        fixture.ViewModel.SettingsPage.VersionUpdateResourceSource = "MirrorChyan";

        Assert.False(fixture.ViewModel.HasWindowUpdateInfo);

        await fixture.ViewModel.SettingsPage.RunStartupVersionUpdateCheckAsync();

        Assert.True(fixture.ViewModel.HasWindowVersionUpdateInfo);
        Assert.True(fixture.ViewModel.HasWindowResourceUpdateInfo);
        Assert.Contains("episode", fixture.ViewModel.WindowResourceUpdateInfo, StringComparison.Ordinal);
        Assert.Contains("版本更新", fixture.ViewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains("资源更新", fixture.ViewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ShouldTriggerStartupVersionAndResourceUpdateWorkflow()
    {
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            LoadedPolicy = VersionUpdatePolicy.Default with
            {
                ResourceUpdateSource = "MirrorChyan",
            },
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: string.Empty,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: false),
                "发现新版本：v2.0.0"),
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-03 10:00:00",
                    ReleaseNote: "episode",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: true,
                    DownloadUrl: null),
                "检测到资源更新。"),
        };
        await using var fixture = await TestFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);

        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(() =>
            versionUpdate.CheckForUpdatesCallCount >= 1
            && versionUpdate.CheckResourceCallCount >= 1));
        Assert.Equal("MirrorChyan", fixture.ViewModel.SettingsPage.VersionUpdateResourceSource);
        Assert.Equal(0, versionUpdate.UpdateResourceCallCount);
        Assert.True(fixture.ViewModel.HasWindowVersionUpdateInfo);
        Assert.True(fixture.ViewModel.HasWindowResourceUpdateInfo);
        Assert.Contains("episode", fixture.ViewModel.WindowResourceUpdateInfo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_WhenFirstBootFlagSet_ShouldShowPersistedUpdateDialogWithoutBlockingChecks()
    {
        var dialogService = new CapturingDialogService
        {
            VersionUpdateReturn = DialogReturnSemantic.Close,
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            LoadedPolicy = VersionUpdatePolicy.Default with
            {
                StartupUpdateCheck = true,
                IsFirstBoot = true,
                VersionName = "Release v2.0.0",
                VersionBody = "Body",
            },
        };
        await using var fixture = await TestFixture.CreateAsync(
            versionUpdateFeatureService: versionUpdate,
            dialogService: dialogService);
        await fixture.ViewModel.InitializeAsync();

        Assert.True(await WaitUntilAsync(() =>
            dialogService.VersionUpdateCallCount > 0));
        Assert.False(fixture.ViewModel.SettingsPage.VersionUpdateIsFirstBoot);
        Assert.Equal(1, dialogService.VersionUpdateCallCount);
        Assert.NotNull(dialogService.LastVersionUpdateRequest);
        Assert.Equal("Release v2.0.0", dialogService.LastVersionUpdateRequest!.TargetVersion);
        Assert.True(await WaitUntilAsync(() =>
            versionUpdate.CheckForUpdatesCallCount >= 1
            && versionUpdate.CheckResourceCallCount >= 1));
    }

    [Fact]
    public async Task CheckVersionUpdateAsync_ShouldUseNonDialogFlow()
    {
        var dialogService = new CapturingDialogService
        {
            VersionUpdateReturn = DialogReturnSemantic.Close,
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: string.Empty,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: false),
                "发现新版本：v2.0.0。"),
        };
        await using var fixture = await TestFixture.CreateAsync(
            versionUpdateFeatureService: versionUpdate,
            dialogService: dialogService);
        await fixture.ViewModel.SettingsPage.InitializeAsync();

        await fixture.ViewModel.SettingsPage.CheckVersionUpdateAsync();

        Assert.Equal(0, dialogService.VersionUpdateCallCount);
        Assert.Equal(0, dialogService.WarningConfirmCallCount);
        Assert.Contains("v2.0.0", fixture.ViewModel.SettingsPage.VersionUpdateStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckVersionUpdateWithDialogAsync_ShouldShowVersionUpdateDialog()
    {
        var dialogService = new CapturingDialogService
        {
            VersionUpdateReturn = DialogReturnSemantic.Close,
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: "pkg.zip",
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: true,
                    PreparedPackagePath: Path.Combine(Path.GetTempPath(), "pkg.zip")),
                "发现新版本：v2.0.0。已准备更新包，重启后即可应用。"),
        };
        await using var fixture = await TestFixture.CreateAsync(
            versionUpdateFeatureService: versionUpdate,
            dialogService: dialogService);
        await fixture.ViewModel.SettingsPage.InitializeAsync();

        await fixture.ViewModel.SettingsPage.CheckVersionUpdateWithDialogAsync();

        Assert.Equal(1, dialogService.VersionUpdateCallCount);
        Assert.NotNull(dialogService.LastVersionUpdateRequest);
        Assert.Equal("v2.0.0", dialogService.LastVersionUpdateRequest!.TargetVersion);
        Assert.Equal(0, dialogService.WarningConfirmCallCount);
        Assert.Equal(
            fixture.ViewModel.SettingsPage.RootTexts["Settings.VersionUpdate.Status.DialogClosed"],
            fixture.ViewModel.SettingsPage.VersionUpdateStatusMessage);
    }

    [Fact]
    public async Task CheckVersionUpdateAsync_WhenAutoInstallEnabledAndDialogConfirmed_ShouldRestart()
    {
        var lifecycle = new SpyAppLifecycleService(UiOperationResult.Ok("Restart process launched."));
        var dialogService = new CapturingDialogService
        {
            VersionUpdateReturn = DialogReturnSemantic.Confirm,
        };
        var versionUpdate = new ScriptedVersionUpdateFeatureService
        {
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: "pkg.zip",
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: true,
                    PreparedPackagePath: Path.Combine(Path.GetTempPath(), "pkg.zip")),
                "发现新版本：v2.0.0。已准备更新包，重启后即可应用。"),
        };
        await using var fixture = await TestFixture.CreateAsync(
            versionUpdateFeatureService: versionUpdate,
            dialogService: dialogService,
            appLifecycleService: lifecycle);
        await fixture.ViewModel.SettingsPage.InitializeAsync();
        fixture.ViewModel.SettingsPage.VersionUpdateAutoInstall = true;

        await fixture.ViewModel.SettingsPage.CheckVersionUpdateAsync();

        Assert.Equal(0, dialogService.VersionUpdateCallCount);
        Assert.Equal(0, dialogService.WarningConfirmCallCount);
        Assert.Equal(1, lifecycle.RestartCallCount);
        Assert.Equal(
            fixture.ViewModel.SettingsPage.RootTexts["Settings.VersionUpdate.RestartManualClose"],
            fixture.ViewModel.SettingsPage.VersionUpdateStatusMessage);
    }

    [Fact]
    public async Task ToolboxPage_ShouldUseShellDialogService_ForGachaDisclaimerConfirmation()
    {
        var dialogService = new CapturingDialogService
        {
            WarningConfirmReturn = DialogReturnSemantic.Confirm,
        };
        await using var fixture = await TestFixture.CreateAsync(dialogService: dialogService);
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ToolboxPage.SetLanguage("en-us");

        var confirmed = await fixture.ViewModel.ToolboxPage.ConfirmGachaDisclaimerAsync();

        Assert.True(confirmed);
        Assert.Equal(1, dialogService.WarningConfirmCallCount);
        Assert.Equal("Toolbox.Gacha.Disclaimer", dialogService.LastWarningConfirmScope);
        var request = dialogService.LastWarningConfirmRequest;
        Assert.NotNull(request);
        Assert.Equal(fixture.ViewModel.ToolboxPage.GachaWarningText, request.Message);
        var chrome = request.Chrome;
        Assert.NotNull(chrome);
        var chromeSnapshot = chrome!.GetSnapshot(request.Language);
        Assert.Equal(
            fixture.ViewModel.ToolboxPage.GachaDisclaimerLeadText,
            chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.LeadText));
        Assert.Equal(
            fixture.ViewModel.ToolboxPage.GachaDisclaimerEmphasisText,
            chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.EmphasisText));
        Assert.Equal(
            fixture.ViewModel.ToolboxPage.GachaDisclaimerBodyText,
            chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.DetailText));
        Assert.False(fixture.ViewModel.ToolboxPage.GachaShowDisclaimer);
    }

    private static ConfigValidationIssue CreateBlockingIssue()
    {
        return new ConfigValidationIssue
        {
            Scope = "TaskValidation",
            Code = "Issue",
            Field = "field",
            Message = "blocked",
            Blocking = true,
            ProfileName = "Default",
            TaskIndex = 0,
            TaskName = "TaskA",
            SuggestedAction = "Fix it",
        };
    }

    private static string CreateBlockingConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "Recruit",
                      "IsEnabled": true,
                      "Params": {
                        "times": 4
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """;
    }

    private static string CreateOutdatedSchemaConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 1,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": []
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """;
    }

    private static string CreateStartupSnapshotConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": []
                }
              },
              "GlobalValues": {
                "Theme.Mode": "Dark",
                "GUI.Localization": "en-us",
                "GUI.UseTray": true,
                "GUI.MinimizeToTray": false,
                "GUI.WindowTitleScrollable": true,
                "GUI.DeveloperMode": true,
                "GUI.Background.ImagePath": "",
                "GUI.Background.Opacity": 32,
                "GUI.Background.BlurEffectRadius": 9,
                "GUI.Background.StretchMode": "UniformToFill",
                "HotKeys": "ShowGui=Ctrl+Shift+Alt+G;LinkStart=Ctrl+Shift+Alt+R"
              },
              "Migration": {}
            }
            """;
    }

    private static string CreateLaunchBehaviorConfigJson(
        bool runDirectly,
        bool minimizeDirectly,
        bool openEmulatorAfterLaunch)
    {
        return
            $$"""
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "Start.RunDirectly": {{runDirectly.ToString().ToLowerInvariant()}},
                    "Start.OpenEmulatorAfterLaunch": {{openEmulatorAfterLaunch.ToString().ToLowerInvariant()}}
                  },
                  "TaskQueue": []
                }
              },
              "GlobalValues": {
                "Start.MinimizeDirectly": {{minimizeDirectly.ToString().ToLowerInvariant()}}
              },
              "Migration": {}
            }
            """;
    }

    private static string CreateSwitchableProfilesConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "ConnectAddress": "127.0.0.1:5555",
                    "ConnectConfig": "General",
                    "AdbPath": "/tmp/adb-default",
                    "TaskQueue.PostAction": {
                      "exit_emulator": true,
                      "commands": {}
                    }
                  },
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "Recruit",
                      "IsEnabled": true,
                      "Params": {
                        "times": 4
                      }
                    }
                  ]
                },
                "Alt": {
                  "Values": {
                    "ConnectAddress": "10.0.0.2:5555",
                    "ConnectConfig": "BlueStacks",
                    "AdbPath": "/tmp/adb-alt",
                    "TaskQueue.PostAction": {
                      "exit_self": true,
                      "commands": {}
                    }
                  },
                  "TaskQueue": [
                    {
                      "Type": "Fight",
                      "Name": "Fight",
                      "IsEnabled": true,
                      "Params": {}
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """;
    }

    private static string CreateTaskQueueScopedSwitchConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "ConnectAddress": "127.0.0.1:5555",
                    "ConnectConfig": "General",
                    "AdbPath": "/tmp/adb-default",
                    "TaskSelectedIndex": 1,
                    "Infrast.DefaultInfrast": "153_layout_3_times_a_day.json",
                    "GUI.InverseClearMode": "ClearInverse",
                    "MainFunction.InverseMode": "False",
                    "Roguelike.RoguelikeDelayAbortUntilCombatComplete": "True"
                  },
                  "TaskQueue": [
                    {
                      "Type": "Infrast",
                      "Name": "Infrast Default",
                      "IsEnabled": true,
                      "Params": {
                        "mode": 0,
                        "drones": "Money",
                        "threshold": 0.30,
                        "facility": ["Mfg", "Dorm"]
                      }
                    },
                    {
                      "Type": "Fight",
                      "Name": "Fight Default",
                      "IsEnabled": true,
                      "Params": {
                        "stage": "CE-6",
                        "times": 2
                      }
                    },
                    {
                      "Type": "Roguelike",
                      "Name": "Rogue Default",
                      "IsEnabled": true,
                      "Params": {
                        "theme": "Sami",
                        "mode": 0
                      }
                    }
                  ]
                },
                "Alt": {
                  "Values": {
                    "ConnectAddress": "10.0.0.2:5555",
                    "ConnectConfig": "BlueStacks",
                    "AdbPath": "/tmp/adb-alt",
                    "TaskSelectedIndex": 2,
                    "Infrast.DefaultInfrast": "243_layout_4_times_a_day.json",
                    "GUI.InverseClearMode": "ClearInverse",
                    "MainFunction.InverseMode": "True",
                    "Roguelike.RoguelikeDelayAbortUntilCombatComplete": "False"
                  },
                  "TaskQueue": [
                    {
                      "Type": "Infrast",
                      "Name": "Infrast Alt",
                      "IsEnabled": true,
                      "Params": {
                        "mode": 0,
                        "drones": "PureGold",
                        "threshold": 0.65,
                        "facility": ["Mfg", "Trade", "Dorm"]
                      }
                    },
                    {
                      "Type": "Fight",
                      "Name": "Fight Alt",
                      "IsEnabled": true,
                      "Params": {
                        "stage": "1-7",
                        "times": 7
                      }
                    },
                    {
                      "Type": "Roguelike",
                      "Name": "Rogue Alt",
                      "IsEnabled": true,
                      "Params": {
                        "theme": "JieGarden",
                        "mode": 0
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {
                "GUI.Localization": "zh-cn"
              },
              "Migration": {}
            }
            """;
    }

    private static string CreateNoTaskSelectionSwitchConfigJson()
    {
        return
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "TaskSelectedIndex": 0,
                    "ConnectAddress": "127.0.0.1:5555"
                  },
                  "TaskQueue": [
                    {
                      "Type": "StartUp",
                      "Name": "Start Default",
                      "IsEnabled": true,
                      "Params": {
                        "account_name": "default-account",
                        "client_type": "Official",
                        "start_game_enabled": false
                      }
                    }
                  ]
                },
                "Alt": {
                  "Values": {
                    "TaskSelectedIndex": -1,
                    "ConnectAddress": "10.0.0.2:5555"
                  },
                  "TaskQueue": [
                    {
                      "Type": "StartUp",
                      "Name": "Start Alt",
                      "IsEnabled": true,
                      "Params": {
                        "account_name": "alt-account",
                        "client_type": "Official",
                        "start_game_enabled": false
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {
                "GUI.Localization": "zh-cn"
              },
              "Migration": {}
            }
            """;
    }

    private static async Task SetSessionStateAsync(UnifiedSessionService sessionService, SessionState targetState)
    {
        switch (targetState)
        {
            case SessionState.Idle:
                break;
            case SessionState.Connected:
                Assert.True((await sessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
                break;
            case SessionState.Running:
                Assert.True((await sessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
                Assert.True((await sessionService.StartAsync()).Success);
                break;
            default:
                throw new NotSupportedException($"Test helper does not support target state {targetState}.");
        }
    }

    private static string ReadGlobalString(UnifiedConfigurationService config, string key)
    {
        if (!config.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
        {
            return text;
        }

        return node.ToString();
    }

    private static void InvokeRefreshConfigValidationState(MainShellViewModel vm, IReadOnlyList<ConfigValidationIssue> issues)
    {
        var method = typeof(MainShellViewModel).GetMethod(
            "RefreshConfigValidationState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, [issues]);
    }

    private static async Task InvokeSyncTrayMenuStateAsync(MainShellViewModel vm)
    {
        var method = typeof(MainShellViewModel).GetMethod(
            "SyncTrayMenuStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static void SetAchievementToastRemainingSeconds(AchievementToastItemViewModel toast, double seconds)
    {
        var field = typeof(AchievementToastItemViewModel).GetField(
            "_remainingCloseCountdownSeconds",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(toast, seconds);
    }

    private static double GetAchievementToastRemainingSeconds(AchievementToastItemViewModel toast)
    {
        var field = typeof(AchievementToastItemViewModel).GetField(
            "_remainingCloseCountdownSeconds",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (double)field!.GetValue(toast)!;
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private static async Task<bool> WaitForLogContainsAsync(string path, string expected, int retry = 20, int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (content.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int retry = 40, int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static bool TaskQueueContainsLog(TaskQueuePageViewModel page, string expected)
    {
        return page.LogCards
            .SelectMany(card => card.Items)
            .Any(item => item.Content.Contains(expected, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<string>> WaitForEventLinesAsync(
        string path,
        string expected,
        int expectedCount,
        int retry = 40,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (File.Exists(path))
            {
                var lines = await File.ReadAllLinesAsync(path);
                var matched = lines
                    .Where(line => line.Contains(expected, StringComparison.Ordinal))
                    .ToArray();
                if (matched.Length >= expectedCount)
                {
                    return matched;
                }
            }

            await Task.Delay(delayMs);
        }

        return Array.Empty<string>();
    }

    private sealed class ScriptedVersionUpdateFeatureService : IVersionUpdateFeatureService
    {
        public VersionUpdatePolicy LoadedPolicy { get; set; } = VersionUpdatePolicy.Default;

        public int CheckForUpdatesCallCount { get; private set; }

        public int CheckResourceCallCount { get; private set; }

        public int UpdateResourceCallCount { get; private set; }

        public UiOperationResult<VersionUpdateCheckResult> CheckForUpdatesResult { get; set; } =
            UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v1.0.0",
                    ReleaseName: string.Empty,
                    Summary: string.Empty,
                    Body: string.Empty,
                    PackageName: string.Empty,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: false,
                    HasPackage: false),
                "Checked.");

        public UiOperationResult<ResourceUpdateCheckResult> CheckResourceUpdateResult { get; set; } =
            UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: false,
                    DisplayVersion: string.Empty,
                    ReleaseNote: string.Empty,
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: null),
                "Resources are up to date.");

        public Task<UiOperationResult<VersionUpdatePolicy>> LoadPolicyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<VersionUpdatePolicy>.Ok(LoadedPolicy, "Loaded."));

        public Task<UiOperationResult<ResourceVersionInfo>> LoadResourceVersionInfoAsync(
            string? clientType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<ResourceVersionInfo>.Ok(ResourceVersionInfo.Empty, "Loaded."));

        public Task<UiOperationResult> SaveChannelAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Saved."));

        public Task<UiOperationResult> SaveProxyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Saved."));

        public Task<UiOperationResult> SavePolicyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
        {
            LoadedPolicy = policy;
            return Task.FromResult(UiOperationResult.Ok("Saved."));
        }

        public Task<UiOperationResult<string>> UpdateResourceAsync(
            VersionUpdatePolicy policy,
            string? clientType,
            IProgress<VersionUpdateProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateResourceCallCount++;
            return Task.FromResult(UiOperationResult<string>.Ok("Updated.", "Updated."));
        }

        public Task<UiOperationResult<ResourceUpdateCheckResult>> CheckResourceUpdateAsync(
            VersionUpdatePolicy policy,
            string? clientType,
            CancellationToken cancellationToken = default)
        {
            CheckResourceCallCount++;
            return Task.FromResult(CheckResourceUpdateResult);
        }

        public Task<UiOperationResult<VersionUpdateCheckResult>> CheckForUpdatesAsync(
            VersionUpdatePolicy policy,
            string currentVersion,
            IProgress<VersionUpdateProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CheckForUpdatesCallCount++;
            return Task.FromResult(CheckForUpdatesResult with
            {
                Value = CheckForUpdatesResult.Value is null
                    ? null
                    : CheckForUpdatesResult.Value with { CurrentVersion = currentVersion },
            });
        }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            MAAUnifiedRuntime runtime,
            MainShellViewModel viewModel,
            CapturingTrayService trayService)
        {
            Root = root;
            Runtime = runtime;
            ViewModel = viewModel;
            TrayService = trayService;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public MainShellViewModel ViewModel { get; }

        public CapturingTrayService TrayService { get; }

        public static async Task<TestFixture> CreateAsync(
            IShellFeatureService? shellService = null,
            IUiLanguageCoordinator? uiLanguageCoordinator = null,
            IAppLifecycleService? appLifecycleService = null,
            IGlobalHotkeyService? hotkeyService = null,
            IOverlayFeatureService? overlayFeatureService = null,
            IVersionUpdateFeatureService? versionUpdateFeatureService = null,
            IAnnouncementFeatureService? announcementFeatureService = null,
            string? existingAvaloniaJson = null,
            IAppDialogService? dialogService = null,
            IMaaCoreBridge? bridge = null,
            bool preloadConfig = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            if (!string.IsNullOrWhiteSpace(existingAvaloniaJson))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, "config", "avalonia.json"),
                    existingAvaloniaJson);
            }

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            if (preloadConfig)
            {
                await config.LoadOrBootstrapAsync();
            }

            var runtimeBridge = bridge ?? new FakeBridge();
            var session = new UnifiedSessionService(runtimeBridge, config, log, new SessionStateMachine());
            var tray = new CapturingTrayService();
            var platform = new PlatformServiceBundle
            {
                TrayService = tray,
                NotificationService = new NoOpNotificationService(),
                HotkeyService = hotkeyService ?? new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config);
            shellService ??= new ShellFeatureService(connect);

            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = runtimeBridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, runtimeBridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = shellService,
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = overlayFeatureService ?? new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                VersionUpdateFeatureService = versionUpdateFeatureService ?? new VersionUpdateFeatureService(config, diagnostics, uiLogService: log, runtimeBaseDirectory: root),
                AnnouncementFeatureService = announcementFeatureService ?? new AnnouncementFeatureService(config),
                UiLanguageCoordinator = uiLanguageCoordinator ?? new UiLanguageCoordinator(config),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
                AppLifecycleService = appLifecycleService ?? new NoOpAppLifecycleService(),
            };

            return new TestFixture(root, runtime, new MainShellViewModel(runtime, dialogService), tray);
        }

        public async ValueTask DisposeAsync()
        {
            TestShellCleanup.StopTimerScheduler(ViewModel);
            ViewModel.CancelStartupInitialization();
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class DelayedOverlayFeatureService : IOverlayFeatureService
    {
        private readonly TimeSpan _delay;

        public DelayedOverlayFeatureService(TimeSpan delay)
        {
            _delay = delay;
        }

        public bool QueryCompleted { get; private set; }

        public Task<string> GetOverlayModeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("preview");
        }

        public async Task<UiOperationResult<IReadOnlyList<OverlayTarget>>> GetOverlayTargetsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_delay, cancellationToken);
            QueryCompleted = true;
            return UiOperationResult<IReadOnlyList<OverlayTarget>>.Ok(
                [new OverlayTarget("preview", "Preview + Logs", true)],
                "Delayed overlay targets loaded.");
        }

        public Task<UiOperationResult> SelectOverlayTargetAsync(string targetId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UiOperationResult.Ok($"Selected `{targetId}`."));
        }

        public Task<UiOperationResult> ToggleOverlayVisibilityAsync(bool visible, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UiOperationResult.Ok($"Overlay visible={visible}."));
        }
    }

    private sealed class CapturingDialogService : IAppDialogService
    {
        public int AnnouncementCallCount { get; private set; }

        public string? LastAnnouncementScope { get; private set; }

        public AnnouncementDialogRequest? LastAnnouncementRequest { get; private set; }

        public TaskCompletionSource<DialogCompletion<AnnouncementDialogPayload>>? AnnouncementTask { get; set; }

        public int VersionUpdateCallCount { get; private set; }

        public VersionUpdateDialogRequest? LastVersionUpdateRequest { get; private set; }

        public DialogReturnSemantic VersionUpdateReturn { get; set; } = DialogReturnSemantic.Close;

        public int ShowTextCallCount { get; private set; }

        public string? LastTextScope { get; private set; }

        public TextDialogRequest? LastTextRequest { get; private set; }

        public TaskCompletionSource<DialogCompletion<TextDialogPayload>>? TextTask { get; set; }

        public int WarningConfirmCallCount { get; private set; }

        public WarningConfirmDialogRequest? LastWarningConfirmRequest { get; private set; }

        public string? LastWarningConfirmScope { get; private set; }

        public DialogReturnSemantic WarningConfirmReturn { get; set; } = DialogReturnSemantic.Close;

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            AnnouncementCallCount++;
            LastAnnouncementScope = sourceScope;
            LastAnnouncementRequest = request;
            if (AnnouncementTask is not null)
            {
                cancellationToken.Register(() => AnnouncementTask.TrySetCanceled(cancellationToken));
                return AnnouncementTask.Task;
            }

            return Task.FromResult(
                new DialogCompletion<AnnouncementDialogPayload>(
                    DialogReturnSemantic.Close,
                    null,
                    "captured"));
        }

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            VersionUpdateCallCount++;
            LastVersionUpdateRequest = request;
            return Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(
                VersionUpdateReturn,
                null,
                "captured"));
        }

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(
                DialogReturnSemantic.Close,
                null,
                "captured"));
        }

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(
                DialogReturnSemantic.Close,
                null,
                "captured"));
        }

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DialogCompletion<ErrorDialogPayload>(
                DialogReturnSemantic.Close,
                null,
                "captured"));
        }

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(
                DialogReturnSemantic.Close,
                null,
                "captured"));
        }

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            ShowTextCallCount++;
            LastTextScope = sourceScope;
            LastTextRequest = request;
            if (TextTask is not null)
            {
                cancellationToken.Register(() => TextTask.TrySetCanceled(cancellationToken));
                return TextTask.Task;
            }

            return Task.FromResult(new DialogCompletion<TextDialogPayload>(
                DialogReturnSemantic.Confirm,
                new TextDialogPayload(request.DefaultText),
                "captured"));
        }

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCallCount++;
            LastWarningConfirmRequest = request;
            LastWarningConfirmScope = sourceScope;
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                WarningConfirmReturn,
                WarningConfirmReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "captured"));
        }
    }

    private sealed class ScriptedAnnouncementFeatureService : IAnnouncementFeatureService
    {
        public ScriptedAnnouncementFeatureService(AnnouncementState state)
        {
            State = state;
        }

        public AnnouncementState State { get; private set; }

        public int LoadStateCallCount { get; private set; }

        public int SaveStateCallCount { get; private set; }

        public Task<UiOperationResult<AnnouncementState>> LoadStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadStateCallCount++;
            return Task.FromResult(UiOperationResult<AnnouncementState>.Ok(State, "loaded"));
        }

        public Task<UiOperationResult> SaveStateAsync(AnnouncementState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveStateCallCount++;
            State = state;
            return Task.FromResult(UiOperationResult.Ok("saved"));
        }
    }

    private sealed class SpyAppLifecycleService : IAppLifecycleService
    {
        private readonly UiOperationResult _result;

        public SpyAppLifecycleService(UiOperationResult result)
        {
            _result = result;
        }

        public int RestartCallCount { get; private set; }

        public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class SpyShellFeatureService : IShellFeatureService
    {
        private readonly string _nextLanguage;

        public SpyShellFeatureService(string nextLanguage)
        {
            _nextLanguage = nextLanguage;
        }

        public int SwitchLanguageCallCount { get; private set; }

        public string LastCurrentLanguage { get; private set; } = string.Empty;

        public string? LastTargetLanguage { get; private set; }

        public int ImportLegacyCallCount { get; private set; }

        public ImportSource? LastImportSource { get; private set; }

        public bool LastImportManualImport { get; private set; }

        public Task<UiOperationResult> ConnectAsync(
            string address,
            string config,
            string? adbPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(UiOperationResult.Ok("Connected."));
        }

        public Task<UiOperationResult<ImportReport>> ImportLegacyConfigAsync(
            ImportSource source,
            bool manualImport,
            CancellationToken cancellationToken = default)
        {
            ImportLegacyCallCount++;
            LastImportSource = source;
            LastImportManualImport = manualImport;
            return Task.FromResult(UiOperationResult<ImportReport>.Ok(new ImportReport
            {
                Source = source,
                Success = true,
            }, "Imported."));
        }

        public Task<UiOperationResult<string>> SwitchLanguageAsync(
            string currentLanguage,
            string? targetLanguage = null,
            CancellationToken cancellationToken = default)
        {
            SwitchLanguageCallCount++;
            LastCurrentLanguage = currentLanguage;
            LastTargetLanguage = targetLanguage;
            return Task.FromResult(UiOperationResult<string>.Ok(_nextLanguage, $"Language switched to {_nextLanguage}."));
        }

        public IReadOnlyList<string> GetSupportedLanguages()
        {
            return ["zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas"];
        }
    }

    private sealed class CapturingTrayService : ITrayService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "tray test service",
            Provider: "test");

        public event EventHandler<TrayCommandEvent>? CommandInvoked;

        public event EventHandler<TrayMenuRequestEvent>? MenuRequested;

        public int InitializeCallCount { get; private set; }

        public TrayMenuState? LastMenuState { get; private set; }

        public TrayMenuText? LastMenuText { get; private set; }

        public string? LastAppTitle { get; private set; }

        public Task<PlatformOperationResult> InitializeAsync(
            string appTitle,
            TrayMenuText? menuText,
            CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            LastAppTitle = appTitle;
            LastMenuText = menuText;
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "initialized", "tray.initialize"));
        }

        public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "shutdown", "tray.shutdown"));
        }

        public Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "show", "tray.show"));
        }

        public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
        {
            LastMenuState = state;
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-menu", "tray.setMenuState"));
        }

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-visible", "tray.setVisible"));
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public int InitializeCallCount { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public void Publish(CoreCallbackEvent callback)
        {
            _callbackChannel.Writer.TryWrite(callback);
        }

        public ValueTask DisposeAsync()
        {
            _callbackChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedInitializeBridge : IMaaCoreBridge
    {
        private readonly TimeSpan _delay;
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public int InitializeCallCount { get; private set; }

        public DelayedInitializeBridge(TimeSpan delay)
        {
            _delay = delay;
        }

        public async Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            await Task.Delay(_delay, cancellationToken);
            return CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbackChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingInitializeBridge : IMaaCoreBridge
    {
        private readonly CoreError _error;

        public int InitializeCallCount { get; private set; }

        public FailingInitializeBridge(CoreErrorCode code, string message)
        {
            _error = new CoreError(code, message);
        }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Fail(_error));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingUiLanguageCoordinator : IUiLanguageCoordinator
    {
        public FailingUiLanguageCoordinator(string currentLanguage)
        {
            CurrentLanguage = currentLanguage;
        }

        public string CurrentLanguage { get; }

        public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;

        public Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                UiOperationResult<string>.Fail(
                    UiErrorCode.LanguageSwitchFailed,
                    $"Failed to switch language to {targetLanguage}."));
        }
    }

    private sealed class UnknownErrorHotkeyService : IGlobalHotkeyService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "test hotkey service",
            Provider: "test-hotkey");

        public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

        public Task<PlatformOperationResult> RegisterAsync(
            string name,
            string gesture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "unknown hotkey registration failure",
                "HotkeyErrorNotMapped",
                "hotkey.register"));
        }

        public Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "unregistered",
                "hotkey.unregister"));
        }
    }
}
