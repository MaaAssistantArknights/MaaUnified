using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class SessionStateUiProjectionTests
{
    private static readonly string[] RunOwnerDialogLanguages =
        ["zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas"];

    private static readonly string[] TaskQueueRunOwnerDialogKeys =
    [
        "TaskQueue.RunOwnerBlockedDialog.Title",
        "TaskQueue.RunOwnerBlockedDialog.Message",
        "TaskQueue.RunOwnerBlockedDialog.Message.Unknown",
        "TaskQueue.RunOwnerBlockedDialog.ConfirmButton",
        "TaskQueue.RunOwnerBlockedDialog.StopButton",
        "TaskQueue.RunOwnerBlockedDialog.DetailsButton",
        "TaskQueue.RunOwnerBlockedDialog.Owner.TaskQueue",
        "TaskQueue.RunOwnerBlockedDialog.Owner.Copilot",
        "TaskQueue.RunOwnerBlockedDialog.Owner.Toolbox",
        "TaskQueue.RunOwnerBlockedDialog.Owner.WebApi",
    ];

    [Fact]
    public async Task TaskQueuePage_ShouldProjectSessionState_ToRunControls()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.Equal(SessionState.Idle, vm.CurrentSessionState);
        Assert.False(vm.IsRunning);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);

        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.True(vm.IsRunning);
        Assert.True(vm.IsOwnRunActive);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("Toolbox.Action.Running", "Running..."), vm.RunButtonText);

        Assert.True((await fixture.Runtime.ConnectFeatureService.StopAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);
    }

    [Fact]
    public async Task TaskQueuePage_OwnRun_ShouldShowStopOnHover_AndToggleToStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.True(vm.IsOwnRunActive);
        Assert.Equal(vm.RootTexts.GetOrDefault("Toolbox.Action.Running", "Running..."), vm.RunButtonText);

        vm.SetRunButtonHover(true);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        vm.SetRunButtonHover(false);
        Assert.Equal(vm.RootTexts.GetOrDefault("Toolbox.Action.Running", "Running..."), vm.RunButtonText);

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.False(vm.IsOwnRunActive);
        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);
    }

    [Fact]
    public async Task TaskQueuePage_ShouldKeepLinkStartClickable_WhenConfigHasBlockingIssues()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateBlockingConfigJson());
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.HasBlockingConfigIssues);
        Assert.True(vm.CanToggleRun);
        Assert.True(vm.BlockingConfigIssueCount > 0);
    }

    [Fact]
    public async Task TaskQueuePage_ExternalRunOwner_ShouldShowStopAndStopOwnerDirectly()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialog = new RecordingDialogService(DialogReturnSemantic.Cancel);
        string? stoppedOwner = null;
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialog,
            stopRunOwnerAsync: async (owner, cancellationToken) =>
            {
                stoppedOwner = owner;
                fixture.Runtime.SessionService.EndRun(owner);
                _ = await fixture.Runtime.ConnectFeatureService.StopAsync(cancellationToken);
            });

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Toolbox", "窥屏", out _));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.True(vm.IsRunOwnedByAnotherFeature);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        await vm.ToggleRunAsync();

        Assert.Equal(0, dialog.WarningConfirmCallCount);
        Assert.Equal("Toolbox", stoppedOwner);
        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
    }

    [Fact]
    public async Task TaskQueuePage_LinkStart_WhenOwnerActiveButSessionConnected_ShouldLogAndShowDialog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialog = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialog);
        await vm.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Toolbox", "窥屏", out _));

        await vm.ToggleRunAsync();

        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.True(HasLinkStartFailureLog(vm));
        Assert.Contains("窥屏", dialog.LastWarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Toolbox", dialog.LastWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotPage_Start_WhenToolboxDisplayOwnerActive_ShouldShowSpecificOwnerName()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialog = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var vm = new CopilotPageViewModel(fixture.Runtime, dialogService: dialog);

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Toolbox", "窥屏", out _));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        await vm.StartAsync();

        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.Contains("窥屏", dialog.LastWarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Toolbox", dialog.LastWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_ToggleRun_WhenNotConnected_ShouldAutoConnectAndStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5555",
                ConnectConfig = "General",
                MacUseBundledAdb = false,
            });
        await vm.InitializeAsync();

        Assert.Equal(SessionState.Idle, vm.CurrentSessionState);

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState is SessionState.Running or SessionState.Connected);

        Assert.NotEqual(SessionState.Idle, vm.CurrentSessionState);
        Assert.DoesNotContain("Session state", vm.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskQueuePage_LinkStart_OnMacRawByNcRiskForceRun_ShouldUseCommonPromptAndKeepConfiguredCombination()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new FakeBridge();
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync("linkstart-risk");
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);
        if (fixture.Runtime.ConnectFeatureService is ConnectFeatureService connectFeatureService)
        {
            connectFeatureService.MacRawByNcRiskPromptService = prompt;
        }

        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = adbPath,
            MacUseBundledAdb = false,
            TouchMode = "minitouch",
            AdbLiteEnabled = false,
            AutoDetect = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, connectionState);
        await vm.InitializeAsync();

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState is SessionState.Running or SessionState.Connected);

        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("Connect", prompt.LastPrompt?.SourceScope);
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.False(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
        Assert.Equal("user-forced-risk-combination", bridge.LastConnectionInfo?.Extras?.FallbackReason);
    }

    [Fact]
    public async Task TaskQueuePage_ToggleRun_WhenConnectedSettingsChanged_ShouldReconnectWithCurrentSettings()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateRunnableConfigJson());
        var bridge = Assert.IsType<FakeBridge>(fixture.Bridge);
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "emulator-5554",
            ConnectConfig = "General",
            AdbPath = await fixture.CreateExecutableAdbAsync("adb-old"),
            MacUseBundledAdb = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, connectionState);
        await vm.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            connectionState.ConnectAddress,
            connectionState.ConnectConfig,
            connectionState.AdbPath)).Success);
        Assert.Equal(1, bridge.ConnectCallCount);

        connectionState.ConnectAddress = "emulator-5556";
        connectionState.AdbPath = await fixture.CreateExecutableAdbAsync("adb-new");
        fixture.WriteConnectionStateToProfile(connectionState);
        Assert.False(fixture.Runtime.SessionService.IsConnectedWith(
            connectionState.BuildCoreConnectionInfo(effectiveAdbPath: connectionState.AdbPath)));

        await vm.ToggleRunAsync();
        await WaitUntilAsync(
            () => bridge.ConnectCallCount == 2,
            failureMessage: () => $"connectCount={bridge.ConnectCallCount}, state={vm.CurrentSessionState}, error={vm.LastErrorMessage ?? "<null>"}");

        Assert.Equal(2, bridge.ConnectCallCount);
        Assert.NotNull(bridge.LastConnectionInfo);
        Assert.Equal("emulator-5556", bridge.LastConnectionInfo!.Address);
        Assert.Equal(connectionState.AdbPath, bridge.LastConnectionInfo.AdbPath);
    }

    [Fact]
    public async Task TaskQueuePage_LinkStart_ShouldExposeStopImmediately_WhenConnectIsPending()
    {
        var bridge = new BlockingConnectBridge();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-connect-pending");
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "emulator-5554",
            ConnectConfig = "General",
            AdbPath = adbPath,
            MacUseBundledAdb = false,
        };
        var originalProfile = fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile;
        Assert.True((await fixture.Runtime.ConfigurationProfileFeatureService.AddProfileAsync(
            "Second",
            originalProfile)).Success);
        using var settings = new SettingsPageViewModel(fixture.Runtime, sharedState);
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            sharedState);
        await vm.InitializeAsync();
        await settings.InitializeAsync();

        var startTask = vm.StartAsync();
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => vm.IsStartRequestActive);

        Assert.True(vm.IsOwnRunActive);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);
        Assert.True(sharedState.IsSessionOperationActive);
        Assert.True(sharedState.IsTaskStartRequestActive);
        Assert.False(vm.CanEdit);
        Assert.False(settings.IsSessionIdle);

        var stopClickTask = vm.ToggleRunAsync();
        await stopClickTask.WaitAsync(TimeSpan.FromSeconds(10));
        await startTask.WaitAsync(TimeSpan.FromSeconds(10));
        // 停止请求先更新 UI 状态、再落到连接层；并行跑全量时调度更慢，等 Stop 真正到达。
        // 极端调度下连接可能先被取消、从未进入 Connecting，此时不再额外调用 Core Stop。
        await WaitUntilAsync(
            () => bridge.StopCallCount == 1
                  || (startTask.IsCompleted && vm.CurrentSessionState == SessionState.Idle),
            retry: 200,
            delayMs: 25);

        Assert.InRange(bridge.StopCallCount, 0, 1);
        Assert.True(bridge.StopCallCount == 1 || startTask.IsCompleted);
        Assert.False(vm.IsStartRequestActive);
        Assert.False(vm.IsOwnRunActive);
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(sharedState.IsSessionOperationActive);
        Assert.False(sharedState.IsTaskStartRequestActive);
        Assert.True(settings.IsSessionIdle);
        Assert.True(vm.CanEdit);
        settings.ConfigurationManagerSelectedProfile = "Second";
        await settings.SwitchConfigurationProfileAsync();
        Assert.Equal("Second", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.Equal("Second", settings.ConfigurationManagerSelectedProfile);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);
    }

    [Fact]
    public async Task TaskQueuePage_LinkStartCancelDuringConnecting_ShouldReleaseRunOwnerAndAllowNextLinkStart()
    {
        var bridge = new BlockingConnectBridge
        {
            IgnoreConnectCancellation = true,
        };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-connect-cancel-during-connecting");
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "emulator-5554",
                ConnectConfig = "General",
                AdbPath = adbPath,
                MacUseBundledAdb = false,
            });
        await vm.InitializeAsync();

        var firstStartTask = vm.ToggleRunAsync();
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => vm.IsStartRequestActive);

        await vm.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await firstStartTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.False(vm.IsStartRequestActive);
        Assert.False(vm.IsOwnRunActive);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        bridge.BlockConnect = false;
        var secondStartTask = vm.ToggleRunAsync();
        await WaitUntilAsync(() => bridge.ConnectCallCount >= 2);
        await secondStartTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(fixture.Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Connected);
        Assert.True(bridge.ConnectCallCount >= 2);
    }

    [Fact]
    public async Task TaskQueuePage_LinkStart_ShouldBeBlockedWhileScreenshotLifecycleOperationOwnsConnect()
    {
        var bridge = new BlockingScreenshotBridge();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            ClientType = "YoStarEN",
            TouchMode = "MaaFwAdb",
            AttachWindowMouseMethod = "32",
            MacUseBundledAdb = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, connectionState);
        await vm.InitializeAsync();

        var screenshotTask = fixture.Runtime.ConnectFeatureService.RunScreenshotTestAsync(
            [connectionState.BuildCoreConnectionInfo(timeout: TimeSpan.FromSeconds(20))],
            sampleCount: 1);
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => HasLinkStartFailureLog(vm));
        Assert.Contains("already running", vm.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        bridge.ConnectCompletion!.SetResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "blocked failure")));
        var failedScreenshot = await screenshotTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(failedScreenshot.Success);

        bridge.BlockConnect = false;
        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => bridge.ConnectCallCount >= 2);
        Assert.True(fixture.Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Connected);
        Assert.NotNull(bridge.LastConnectionInfo);
        Assert.Equal("YoStarEN", bridge.LastConnectionInfo!.Extras?.ClientType);
        Assert.Equal("MaaFwAdb", bridge.LastConnectionInfo.Extras?.TouchMode);
        Assert.Equal("32", bridge.LastConnectionInfo.Extras?.AttachWindowMouseMethod);
    }

    [Fact]
    public async Task CopilotStart_ShouldRejectScreenshotActivityBeforeAppend()
    {
        var bridge = new BlockingScreenshotBridge
        {
            BlockConnect = false,
            BlockCapture = true,
        };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            MacUseBundledAdb = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var sharedState = new ConnectionGameSharedStateViewModel();
        var screenshotTask = fixture.Runtime.ConnectFeatureService.RunScreenshotTestAsync(
            [connectionState.BuildCoreConnectionInfo(timeout: TimeSpan.FromSeconds(20))],
            sampleCount: 1);

        var screenshotActivityStarted = false;
        try
        {
            await bridge.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);
            Assert.True(sharedState.TryBeginScreenshotTestActivity());
            screenshotActivityStarted = true;

            var vm = new CopilotPageViewModel(fixture.Runtime, connectionState: sharedState);
            vm.FilePath = fixture.CreateCopilotFile();
            await vm.ImportFromFileAsync();
            Assert.False(vm.CanStart);

            await vm.StartAsync();

            Assert.Equal(0, bridge.AppendCallCount);
            Assert.Equal(0, bridge.StartCallCount);
            Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
            Assert.Equal(
                vm.Texts.GetOrDefault(
                    "Copilot.Error.SessionActivity.OtherActive",
                    "Another session operation is active. Copilot cannot start until it is stopped or finishes."),
                vm.LastErrorMessage);
        }
        finally
        {
            if (screenshotActivityStarted)
            {
                sharedState.EndScreenshotTestActivity();
            }
            bridge.CaptureCompletion?.TrySetResult(CoreResult<byte[]>.Ok([1, 2, 3]));
            await screenshotTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ConnectFeatureService_TryCancelActiveLifecycleOperation_ShouldCancelBlockedScreenshot()
    {
        var bridge = new BlockingScreenshotBridge
        {
            BlockConnect = false,
            BlockCapture = true,
        };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            MacUseBundledAdb = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var screenshotTask = fixture.Runtime.ConnectFeatureService.RunScreenshotTestAsync(
            [connectionState.BuildCoreConnectionInfo(timeout: TimeSpan.FromSeconds(20))],
            sampleCount: 1);
        await bridge.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(fixture.Runtime.ConnectFeatureService.TryCancelActiveLifecycleOperation("M28 test stop"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await screenshotTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(fixture.Runtime.ConnectFeatureService.TryCancelActiveLifecycleOperation("already settled"));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task CopilotStart_ShouldPublishSharedActivityUntilOutcomeAndStop(string outcome)
    {
        var bridge = new BlockingConnectBridge { BlockConnect = false };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync($"copilot-activity-{outcome}");
        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            adbPath)).Success);

        var sharedState = new ConnectionGameSharedStateViewModel();
        using var settings = new SettingsPageViewModel(fixture.Runtime, sharedState);
        var originalProfile = await PrepareSecondProfileAsync(fixture, settings);
        var vm = new CopilotPageViewModel(fixture.Runtime, connectionState: sharedState);
        vm.FilePath = fixture.CreateCopilotFile();
        await vm.ImportFromFileAsync();
        bridge.BlockStart = true;
        using var cancellation = new CancellationTokenSource();

        var startTask = vm.StartAsync(cancellation.Token);
        await bridge.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(sharedState.IsSessionOperationActive);
        Assert.False(settings.IsSessionIdle);
        Assert.True(vm.IsStartRequestActive);
        await AssertProfileSwitchBlockedAsync(fixture, settings, originalProfile);

        switch (outcome)
        {
            case "success":
                bridge.CompleteStart(true);
                await startTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.False(settings.IsSessionIdle);
                bridge.StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
                await vm.StopAsync();
                Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "failure":
                bridge.CompleteStart(false, "scripted Copilot start failure");
                await startTask.WaitAsync(TimeSpan.FromSeconds(2));
                // A failed append/start taints the native queue, so the session may legitimately
                // fall back to Idle (reconnect required) instead of Connected.
                Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
                Assert.NotEqual(SessionState.Stopping, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "cancel":
                cancellation.Cancel();
                await IgnoreExpectedCancellationAsync(startTask);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ToolboxPeep_ShouldPublishSharedActivityUntilOutcomeAndStop(string outcome)
    {
        var bridge = new BlockingConnectBridge();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync($"peep-activity-{outcome}");
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            AdbPath = adbPath,
            MacUseBundledAdb = false,
            RetryOnDisconnected = false,
            AllowAdbRestart = false,
            AllowAdbHardRestart = false,
        };
        using var settings = new SettingsPageViewModel(fixture.Runtime, sharedState);
        var originalProfile = await PrepareSecondProfileAsync(fixture, settings);
        var taskQueue = new TaskQueuePageViewModel(fixture.Runtime, sharedState);
        var vm = new ToolboxPageViewModel(fixture.Runtime, sharedState);
        using var cancellation = new CancellationTokenSource();

        var peepTask = vm.TogglePeepAsync(cancellation.Token);
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(sharedState.IsSessionOperationActive);
        Assert.False(settings.IsSessionIdle);
        Assert.False(taskQueue.CanEdit);
        await AssertProfileSwitchBlockedAsync(fixture, settings, originalProfile);

        switch (outcome)
        {
            case "success":
                bridge.CompleteConnect(true);
                await peepTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(vm.Peeping);
                Assert.True(sharedState.IsSessionOperationActive);
                Assert.False(settings.IsSessionIdle);
                await vm.TogglePeepAsync();
                Assert.False(vm.Peeping);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(taskQueue.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "failure":
                bridge.CompleteConnect(false, "scripted Peep connection failure");
                await peepTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(vm.Peeping);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(taskQueue.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "cancel":
                cancellation.Cancel();
                await IgnoreExpectedCancellationAsync(peepTask);
                Assert.False(vm.Peeping);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(taskQueue.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task TaskQueueRunOnce_ShouldPublishSharedActivityUntilOutcomeAndStop(string outcome)
    {
        var bridge = new BlockingConnectBridge();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync($"run-once-activity-{outcome}");
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            AdbPath = adbPath,
            MacUseBundledAdb = false,
            RetryOnDisconnected = false,
            AllowAdbRestart = false,
            AllowAdbHardRestart = false,
        };
        using var settings = new SettingsPageViewModel(fixture.Runtime, sharedState);
        var originalProfile = await PrepareSecondProfileAsync(fixture, settings);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, sharedState);
        await vm.InitializeAsync();
        using var cancellation = new CancellationTokenSource();

        var runTask = vm.RunSelectedTaskOnceAsync(cancellation.Token);
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(sharedState.IsSessionOperationActive);
        Assert.False(settings.IsSessionIdle);
        Assert.False(vm.CanEdit);
        await AssertProfileSwitchBlockedAsync(fixture, settings, originalProfile);

        switch (outcome)
        {
            case "success":
                bridge.CompleteConnect(true);
                await runTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.False(settings.IsSessionIdle);
                Assert.False(vm.CanEdit);
                bridge.StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
                await vm.StopAsync();
                Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                break;
            case "failure":
                bridge.CompleteConnect(false, "scripted run-once connection failure");
                await runTask.WaitAsync(TimeSpan.FromSeconds(35));
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "cancel":
                cancellation.Cancel();
                await IgnoreExpectedCancellationAsync(runTask);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ManualAccountSwitch_ShouldPublishSharedActivityUntilOutcomeAndStop(string outcome)
    {
        var bridge = new BlockingConnectBridge { BlockConnect = false };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync($"account-switch-activity-{outcome}");
        var sharedState = new ConnectionGameSharedStateViewModel();
        using var settings = new SettingsPageViewModel(fixture.Runtime, sharedState);
        var originalProfile = await PrepareSecondProfileAsync(fixture, settings);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, sharedState);
        await vm.InitializeAsync();
        vm.StartUpModule.AccountName = "verifier-account";
        Assert.True(vm.StartUpModule.CanRunAccountSwitchNow);
        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            adbPath)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);
        bridge.BlockStart = true;
        using var cancellation = new CancellationTokenSource();

        var switchTask = vm.StartUpModule.RunAccountSwitchManualAsync(cancellation.Token);
        await bridge.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(sharedState.IsSessionOperationActive);
        Assert.False(settings.IsSessionIdle);
        Assert.False(vm.CanEdit);
        await AssertProfileSwitchBlockedAsync(fixture, settings, originalProfile);

        switch (outcome)
        {
            case "success":
                bridge.CompleteStart(true);
                await switchTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.False(settings.IsSessionIdle);
                Assert.False(vm.CanEdit);
                bridge.StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
                await vm.StopAsync();
                Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                break;
            case "failure":
                bridge.CompleteStart(false, "scripted account switch failure");
                await switchTask.WaitAsync(TimeSpan.FromSeconds(5));
                // A failed append/start taints the native queue, so the session may legitimately
                // fall back to Idle (reconnect required) instead of Connected.
                Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
                Assert.NotEqual(SessionState.Stopping, fixture.Runtime.SessionService.CurrentState);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            case "cancel":
                cancellation.Cancel();
                await IgnoreExpectedCancellationAsync(switchTask);
                Assert.False(sharedState.IsSessionOperationActive);
                Assert.True(settings.IsSessionIdle);
                Assert.True(vm.CanEdit);
                Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    [Fact]
    public async Task TaskQueuePage_Start_ShouldClearVisibleLogsFromPreviousRun()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateRunnableConfigJson());
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5555",
                ConnectConfig = "General",
                MacUseBundledAdb = false,
            });
        await vm.InitializeAsync();

        vm.AppendSystemLog("stale system log");
        typeof(TaskQueuePageViewModel)
            .GetMethod("UpdateDownloadLog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [DateTimeOffset.UtcNow, "INFO", "download old package"]);

        Assert.Contains("stale system log", FlattenLogs(vm), StringComparison.Ordinal);
        Assert.True(vm.HasDownloadLog);

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState is SessionState.Running or SessionState.Connected);

        Assert.DoesNotContain("stale system log", FlattenLogs(vm), StringComparison.Ordinal);
        Assert.False(vm.HasDownloadLog);
    }

    [Fact]
    public async Task TaskQueuePage_ShouldProjectExplicitUpdateLogs_ToDownloadPanel()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        typeof(TaskQueuePageViewModel)
            .GetMethod("UpdateDownloadLog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [DateTimeOffset.UtcNow, "INFO", "[update] Version update available: v2.0.0"]);

        Assert.Contains("Version update available: v2.0.0", vm.DownloadLogEntry.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("[update]", vm.DownloadLogEntry.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskQueuePage_Start_WhenConnectFails_ShouldShowConciseGuidanceWithoutIdleNoise()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await TestFixture.CreateAsync(bridge: new FailingConnectBridge());
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5555",
                ConnectConfig = "MuMuEmulator12",
                AdbPath = @"D:\Program Files\Netease\MuMuPlayer-12.0\shell\adb.exe",
            });
        await vm.InitializeAsync();

        await vm.StartAsync();
        await WaitUntilAsync(() => HasLinkStartFailureLog(vm));

        Assert.Contains("连接失败", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains("当前系统是 Linux", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("会话状态", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Session state", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("错误详情", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("ADB restart failed:", FlattenLogs(vm), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_Start_WhenValidationBlocks_ShouldSelectFirstBlockingTaskAndAppendFailureLog()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateValidationBlockingConfigJson());
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            MacUseBundledAdb = false,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, connectionState);
        await vm.InitializeAsync();

        Assert.Equal("reclamation-ok", vm.SelectedTask?.Name);
        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            connectionState.ConnectAddress,
            connectionState.ConnectConfig,
            connectionState.AdbPath)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        await vm.StartAsync();

        Assert.Equal(SessionState.Connected, vm.CurrentSessionState);
        Assert.Equal("recruit-blocking", vm.SelectedTask?.Name);
        await WaitUntilAsync(() => HasLinkStartFailureLog(vm));
        Assert.True(HasLinkStartFailureLog(vm));
        Assert.Contains("recruit-blocking", FlattenLogs(vm), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopilotPage_ShouldProjectSessionState_ToRunControls()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new CopilotPageViewModel(fixture.Runtime);

        Assert.Equal(SessionState.Idle, vm.CurrentSessionState);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);
        Assert.False(vm.IsRunning);

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);
        Assert.False(vm.IsRunning);

        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowStopButton);
        Assert.True(vm.IsRunning);
    }

    [Fact]
    public async Task CopilotPage_AppendWindow_ShouldExposeStopAndCancelBeforeCoreStart()
    {
        var bridge = new BlockingConnectBridge
        {
            BlockConnect = false,
            BlockAppend = true,
        };
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);
        MacBundledAdbPolicy.MarkCurrentTermsAccepted(
            fixture.Runtime.ConfigurationService.CurrentConfig,
            DateTimeOffset.UtcNow);
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        // P0-03 统一启动拦截：Copilot 入口在 Core 预热完成前会被 gate 拦截（WPF
        // Bootstrapper.TryGetTaskBlockReason 同序），因此先等预热完成再验证 append 窗口。
        await WaitUntilAsync(() => shell.IsCoreReady, retry: 240, delayMs: 25);
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = string.Empty;
        var vm = shell.CopilotPage;
        vm.Items.Add(new CopilotItemViewModel("sample", "Copilot", inlinePayload: "{}"));
        vm.SelectedItem = vm.Items[0];
        Assert.True(vm.CanEdit);

        var startTask = vm.StartAsync();
        await Task.WhenAny(bridge.AppendStarted.Task, startTask).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(
            bridge.AppendStarted.Task.IsCompleted,
            $"Copilot start ended before append: status={vm.StatusMessage}; error={vm.LastErrorMessage}; state={vm.CurrentSessionState}");

        Assert.Equal(SessionState.Connected, vm.CurrentSessionState);
        Assert.True(vm.IsStartRequestActive);
        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanEdit);
        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowStopButton);

        await shell.TaskQueuePage.ToggleRunAsync();
        await startTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(shell.ConnectionGameSharedState.IsSessionOperationActive);
        Assert.False(vm.IsStartRequestActive);
        Assert.True(vm.CanEdit);
        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public async Task CopilotPage_ConnectWindow_ShouldExposeStopAndCancelBeforeAppend()
    {
        var bridge = new BlockingConnectBridge();
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);
        var connectionState = new ConnectionGameSharedStateViewModel();
        var vm = new CopilotPageViewModel(
            fixture.Runtime,
            ensureConnectedAsync: cancellationToken => fixture.Runtime.ConnectFeatureService.ConnectAsync(
                "127.0.0.1:5555",
                "General",
                null,
                cancellationToken),
            connectionState: connectionState);
        vm.Items.Add(new CopilotItemViewModel("sample", "Copilot", inlinePayload: "{}"));
        vm.Items.Add(new CopilotItemViewModel("other", "Copilot", inlinePayload: "{}"));
        vm.SelectedItem = vm.Items[0];
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        Assert.True(vm.CanEdit);

        var startTask = vm.StartAsync();
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connecting);

        Assert.True(vm.IsStartRequestActive);
        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanEdit);
        Assert.Contains(nameof(CopilotPageViewModel.CanEdit), changedProperties);
        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowStopButton);

        vm.SelectedItem = vm.Items[1];
        Assert.Same(vm.Items[0], vm.SelectedItem);

        await vm.StopAsync();
        await startTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(connectionState.IsSessionOperationActive);
        Assert.True(vm.CanEdit);
        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);

        vm.SelectedItem = vm.Items[1];
        Assert.Same(vm.Items[1], vm.SelectedItem);
    }

    [Fact]
    public async Task CopilotPage_Stopping_ShouldKeepStartHiddenAndDisableRepeatedStop()
    {
        var bridge = new BlockingConnectBridge
        {
            BlockConnect = false,
            BlockStart = false,
            StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);
        var vm = new CopilotPageViewModel(fixture.Runtime);
        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Copilot", out _));
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        var stopTask = vm.StopAsync();
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Stopping);

        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowStopButton);
        Assert.False(vm.CanStop);

        bridge.StopCompletion!.TrySetResult(CoreResult<bool>.Ok(true));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);
    }

    [Fact]
    public async Task CopilotPage_Running_ShouldDisableEditingAndRecoverAfterStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var connectionState = new ConnectionGameSharedStateViewModel();
        var vm = new CopilotPageViewModel(fixture.Runtime, connectionState: connectionState);
        vm.Items.Add(new CopilotItemViewModel("sample", "Copilot", inlinePayload: "{}"));
        vm.SelectedItem = vm.Items[0];

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Copilot", out _));
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.True(vm.ShowStopButton);
        Assert.False(vm.ShowStartButton);
        Assert.False(vm.CanEdit);

        await vm.StopAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowStopButton);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task CopilotPage_ExternalTaskQueueConnecting_ShouldShowStopAndCancelStart()
    {
        var bridge = new BlockingConnectBridge();
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-external-connecting");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);

        var taskQueueStart = shell.TaskQueuePage.ToggleRunAsync();
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connecting);
        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Connecting);

        Assert.True(copilot.IsExternalSessionActivityActive);
        Assert.False(copilot.IsExternalRunActive);
        Assert.True(copilot.ShowStopButton);
        Assert.False(copilot.ShowStartButton);
        Assert.True(copilot.CanStop);
        Assert.False(copilot.CanEdit);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await taskQueueStart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(copilot.IsExternalSessionActivityActive);
        Assert.False(copilot.IsExternalRunActive);
        Assert.True(copilot.ShowStartButton);
        Assert.False(copilot.ShowStopButton);
    }

    [Fact]
    public async Task CopilotPage_ExternalTaskQueueRunning_ShouldShowStopAndStopSession()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-external-running");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            adbPath)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("TaskQueue", out _));
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);

        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(copilot.IsExternalRunActive);
        Assert.True(copilot.IsExternalSessionActivityActive);
        Assert.True(copilot.ShowStopButton);
        Assert.False(copilot.ShowStartButton);
        Assert.True(copilot.CanStop);
        Assert.False(copilot.CanEdit);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(copilot.IsExternalRunActive);
        Assert.False(copilot.IsExternalSessionActivityActive);
        Assert.True(copilot.ShowStartButton);
        Assert.False(copilot.ShowStopButton);
        Assert.True(copilot.CanEdit);
    }

    [Fact]
    public async Task TaskQueuePage_CentralStopDuringStart_ShouldNotLeakCancellationFromStartPipeline()
    {
        var bridge = new BlockingConnectBridge
        {
            BlockConnect = false,
            BlockStart = true,
        };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-central-stop-starting");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);
        var copilot = shell.CopilotPage;

        var taskQueueStart = shell.TaskQueuePage.ToggleRunAsync();
        await bridge.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(shell.ConnectionGameSharedState.IsSessionOperationActive);
        await WaitUntilAsync(() => copilot.IsExternalSessionActivityActive);
        Assert.True(copilot.ShowStopButton);
        Assert.True(copilot.CanStop);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await taskQueueStart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(fixture.Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Stopping);
        Assert.False(copilot.IsExternalSessionActivityActive);
        Assert.True(copilot.ShowStartButton);
    }

    [Fact]
    public async Task TaskQueuePage_Stopping_ShouldDisableRunControlAndIgnoreRepeatedToggle()
    {
        var bridge = new BlockingConnectBridge
        {
            BlockConnect = false,
            BlockStart = false,
            StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await TestFixture.CreateAsync(
            existingAvaloniaJson: CreateRunnableConfigJson(),
            bridge: bridge);
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-stopping");
        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            ConnectConfig = "General",
            AdbPath = adbPath,
            MacUseBundledAdb = false,
        };
        fixture.WriteConnectionStateToProfile(connectionState);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, connectionState);
        await vm.InitializeAsync();

        await vm.ToggleRunAsync();
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);
        Assert.True(vm.CanToggleRun);

        var stopTask = vm.ToggleRunAsync();
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Stopping);

        Assert.False(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        await vm.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, bridge.StopCallCount);

        bridge.StopCompletion!.TrySetResult(CoreResult<bool>.Ok(true));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);
    }

    [Fact]
    public async Task TaskQueuePage_ColdLinkStart_ShouldKeepRunOwnerAndLetCopilotStopTheRun()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateRunnableConfigJson());
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-cold-link-start");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);

        var taskQueue = shell.TaskQueuePage;
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);

        await taskQueue.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);

        // The Link Start pipeline connects while it already owns the run, so the
        // `Connected` transition inside the pipeline must not release the owner.
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(taskQueue.IsOwnRunActive);
        Assert.False(taskQueue.IsRunOwnedByAnotherFeature);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);

        Assert.True(copilot.ShowStopButton);
        Assert.True(copilot.CanStop);
        Assert.True(copilot.IsExternalRunActive);
        Assert.True(copilot.IsExternalSessionActivityActive);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(string.IsNullOrEmpty(copilot.LastErrorMessage), copilot.LastErrorMessage);
        Assert.True(copilot.ShowStartButton);
        Assert.False(copilot.ShowStopButton);
    }

    [Fact]
    public async Task TaskQueuePage_ColdRunOnce_ShouldKeepRunOwnerAndLetCopilotStopTheRun()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateRunnableConfigJson());
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-cold-run-once");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);

        var taskQueue = shell.TaskQueuePage;
        Assert.NotNull(taskQueue.SelectedTask);
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);

        await taskQueue.RunSelectedTaskOnceAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);

        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(taskQueue.IsOwnRunActive);
        Assert.False(taskQueue.IsRunOwnedByAnotherFeature);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);
        Assert.True(copilot.CanStop);
        Assert.True(copilot.IsExternalRunActive);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(string.IsNullOrEmpty(copilot.LastErrorMessage), copilot.LastErrorMessage);
    }

    [Fact]
    public async Task ShellStart_ColdScheduledStartEntry_ShouldKeepRunOwnerAndLetCopilotStopTheRun()
    {
        await using var fixture = await TestFixture.CreateAsync(existingAvaloniaJson: CreateRunnableConfigJson());
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();
        var adbPath = await fixture.CreateExecutableAdbAsync("adb-cold-shell-start");
        shell.ConnectionGameSharedState.MacUseBundledAdb = false;
        shell.ConnectionGameSharedState.ConnectAddress = "127.0.0.1:5555";
        shell.ConnectionGameSharedState.ConnectConfig = "General";
        shell.ConnectionGameSharedState.AdbPath = adbPath;
        fixture.WriteConnectionStateToProfile(shell.ConnectionGameSharedState);

        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        await shell.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);

        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);
        Assert.True(copilot.CanStop);
        Assert.True(copilot.IsExternalRunActive);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(string.IsNullOrEmpty(copilot.LastErrorMessage), copilot.LastErrorMessage);
    }

    [Fact]
    public async Task CopilotPage_WebApiOwnedRunningSession_StopShouldStopTheSession()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("WebApi", out _));
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);

        Assert.Equal("WebApi", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(copilot.IsExternalRunActive);
        Assert.True(copilot.CanStop);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(string.IsNullOrEmpty(copilot.LastErrorMessage), copilot.LastErrorMessage);
    }

    [Fact]
    public async Task TaskQueuePage_WebApiOwnedRun_DialogStopShouldReleaseTheOwner()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialogs = new RecordingDialogService(DialogReturnSemantic.Cancel);
        var shell = new MainShellViewModel(fixture.Runtime, dialogs);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("WebApi", out _));

        await shell.TaskQueuePage.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dialogs.WarningConfirmCallCount);
        Assert.Contains("Web API", dialogs.LastWarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApi", dialogs.LastWarningMessage, StringComparison.Ordinal);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task TaskQueuePage_RunOwnerBlockedDialog_ShouldUseDedicatedKeysAndLocalizedOwnerName()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialogs = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var shell = new MainShellViewModel(fixture.Runtime, dialogs);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("WebApi", out _));

        var manager = UiLocalizationResourceManager.Shared;
        var page = shell.TaskQueuePage;
        foreach (var language in RunOwnerDialogLanguages)
        {
            page.SetLanguage(language);
            var expectedOwnerName = manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Owner.WebApi").Value;
            var expectedTitle = manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Title").Value;
            var expectedMessage = string.Format(
                CultureInfo.InvariantCulture,
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Message").Value,
                expectedOwnerName);

            var callsBefore = dialogs.WarningConfirmCallCount;
            await page.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(callsBefore + 1, dialogs.WarningConfirmCallCount);
            var request = Assert.IsType<WarningConfirmDialogRequest>(dialogs.LastWarningRequest);
            Assert.Equal(expectedTitle, request.Title);
            Assert.Equal(expectedMessage, request.Message);
            Assert.Equal(
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.ConfirmButton").Value,
                request.ConfirmText);
            Assert.Equal(
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.StopButton").Value,
                request.CancelText);
            Assert.Equal(language, request.Language);

            var prompt = Assert.IsType<DialogChromeCatalog>(request.Chrome)
                .GetSnapshot(language)
                .GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt, request.Message);
            Assert.Equal(expectedMessage, prompt);

            // The dedicated TaskQueue keys must not replay the Toolbox/Copilot dialog wording.
            Assert.NotEqual(manager.Lookup(language, "Toolbox.BusyDialog.Title").Value, request.Title);
            Assert.NotEqual(
                string.Format(
                    CultureInfo.InvariantCulture,
                    manager.Lookup(language, "Copilot.RunOwnerBlockedDialog.Message").Value,
                    expectedOwnerName),
                request.Message);
            Assert.NotEqual(manager.Lookup(language, "Copilot.RunOwnerBlockedDialog.StopButton").Value, request.CancelText);

            // The internal owner id never reaches the user-facing dialog.
            Assert.DoesNotContain("WebApi", request.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("WebApi", request.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("WebApi", prompt, StringComparison.Ordinal);
            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", request.Title, StringComparison.Ordinal);
                Assert.StartsWith("[!! ", request.Message, StringComparison.Ordinal);
            }

        }

        Assert.Equal("WebApi", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task TaskQueuePage_UnknownRunOwner_DialogShouldHideRawOwnerIdBehindGenericFallback()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialogs = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var shell = new MainShellViewModel(fixture.Runtime, dialogs);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("ExternalBridgeOwner", out _));

        var manager = UiLocalizationResourceManager.Shared;
        var page = shell.TaskQueuePage;
        foreach (var language in RunOwnerDialogLanguages)
        {
            page.SetLanguage(language);
            var expectedMessage = manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Message.Unknown").Value;

            var callsBefore = dialogs.WarningConfirmCallCount;
            await page.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(callsBefore + 1, dialogs.WarningConfirmCallCount);
            var request = Assert.IsType<WarningConfirmDialogRequest>(dialogs.LastWarningRequest);
            Assert.Equal(expectedMessage, request.Message);
            Assert.DoesNotContain("ExternalBridgeOwner", request.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ExternalBridgeOwner", request.Title, StringComparison.Ordinal);
        }

        Assert.Equal("ExternalBridgeOwner", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Theory]
    [InlineData("Copilot", "TaskQueue.RunOwnerBlockedDialog.Owner.Copilot")]
    [InlineData("Toolbox", "TaskQueue.RunOwnerBlockedDialog.Owner.Toolbox")]
    [InlineData("WebApi", "TaskQueue.RunOwnerBlockedDialog.Owner.WebApi")]
    public async Task TaskQueuePage_RunOwnerBlockedDialog_ShouldNameEachKnownOwner(string owner, string ownerNameKey)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialogs = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var shell = new MainShellViewModel(fixture.Runtime, dialogs);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun(owner, out _));

        const string language = "ja-jp";
        var manager = UiLocalizationResourceManager.Shared;
        var expectedMessage = string.Format(
            CultureInfo.InvariantCulture,
            manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Message").Value,
            manager.Lookup(language, ownerNameKey).Value);

        shell.TaskQueuePage.SetLanguage(language);
        await shell.TaskQueuePage.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var request = Assert.IsType<WarningConfirmDialogRequest>(dialogs.LastWarningRequest);
        Assert.Equal(expectedMessage, request.Message);
        Assert.DoesNotContain(owner, request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_RunOwnerBlockedDialog_ShouldPreferTheRegisteredOwnerDisplayName()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var dialogs = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var shell = new MainShellViewModel(fixture.Runtime, dialogs);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        // Toolbox registers the human readable tool name as its run owner display name.
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Toolbox", "Recruit Recognition", out _));

        await shell.TaskQueuePage.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var request = Assert.IsType<WarningConfirmDialogRequest>(dialogs.LastWarningRequest);
        Assert.Contains("Recruit Recognition", request.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Toolbox", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueRunOwnerDialogKeys_ShouldBeDedicatedAndCompleteForEveryLocale()
    {
        var manager = UiLocalizationResourceManager.Shared;
        var ownerKeys = new[]
        {
            "TaskQueue.RunOwnerBlockedDialog.Owner.TaskQueue",
            "TaskQueue.RunOwnerBlockedDialog.Owner.Copilot",
            "TaskQueue.RunOwnerBlockedDialog.Owner.Toolbox",
            "TaskQueue.RunOwnerBlockedDialog.Owner.WebApi",
        };

        foreach (var language in RunOwnerDialogLanguages)
        {
            foreach (var key in TaskQueueRunOwnerDialogKeys)
            {
                var result = manager.Lookup(language, key);
                Assert.False(result.IsFallback, $"{language}:{key} fell back to {result.FallbackSource}.");
                Assert.NotEqual(key, result.Value);
            }

            var messageTemplate = manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Message").Value;
            Assert.Contains("{0}", messageTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "{0}",
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Message.Unknown").Value,
                StringComparison.Ordinal);

            var ownerNames = ownerKeys
                .Select(key => manager.Lookup(language, key).Value)
                .ToArray();
            Assert.Equal(ownerNames.Length, ownerNames.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(ownerNames, name => name.Contains("WebApi", StringComparison.Ordinal));

            Assert.NotEqual(
                manager.Lookup(language, "Copilot.RunOwnerBlockedDialog.Title").Value,
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Title").Value);
            Assert.NotEqual(
                manager.Lookup(language, "Copilot.RunOwnerBlockedDialog.Message").Value,
                messageTemplate);
            Assert.NotEqual(
                manager.Lookup(language, "Toolbox.BusyDialog.Title").Value,
                manager.Lookup(language, "TaskQueue.RunOwnerBlockedDialog.Title").Value);

            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", messageTemplate, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TaskQueueRunOwnerDialogSource_ShouldNotReuseToolboxOrCopilotDialogKeys()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "App",
            "ViewModels",
            "TaskQueue",
            "TaskQueuePageViewModel.cs"));

        foreach (var retiredKey in new[]
                 {
                     "Toolbox.BusyDialog.",
                     "Copilot.RunOwnerBlockedDialog.",
                 })
        {
            Assert.DoesNotContain(retiredKey, source, StringComparison.Ordinal);
        }

        Assert.Contains("TaskQueue.RunOwnerBlockedDialog.Title", source, StringComparison.Ordinal);
        Assert.Contains("TaskQueue.RunOwnerBlockedDialog.Message", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotPage_OwnerlessRunningSession_StopShouldStopTheSessionWithoutUnknownOwnerError()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var shell = new MainShellViewModel(fixture.Runtime);
        await shell.InitializeAsync();

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            "127.0.0.1:5555",
            "General",
            null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Running);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        var copilot = shell.CopilotPage;
        await WaitUntilAsync(() => copilot.CurrentSessionState == SessionState.Running);

        Assert.False(copilot.IsExternalRunActive);
        Assert.True(copilot.CanStop);

        await copilot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected);

        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
        Assert.DoesNotContain("Unknown", copilot.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotPage_Start_WhenSessionNotConnected_ShouldExposeCurrentLanguageErrorMessage()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new CopilotPageViewModel(fixture.Runtime);
        vm.Items.Add(new CopilotItemViewModel("sample", "Copilot", inlinePayload: "{}"));
        vm.SelectedItem = vm.Items[0];

        await vm.StartAsync();

        Assert.Contains("会话状态", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Session state", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains("开始", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotPage_Start_WhenConnected_ShouldStillRunEnsureConnectedCallback()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var ensureCalls = 0;
        var vm = new CopilotPageViewModel(
            fixture.Runtime,
            ensureConnectedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ConnectFailed, "connection changed"));
            });
        vm.Items.Add(new CopilotItemViewModel("sample", "Copilot", inlinePayload: "{}"));
        vm.SelectedItem = vm.Items[0];

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        await vm.StartAsync();

        Assert.Equal(1, ensureCalls);
        Assert.Equal("connection changed", vm.LastErrorMessage);
    }

    [Fact]
    public async Task TaskQueuePage_WaitAndStop_ShouldDisableControlsAndRecoverWhenSessionStops()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);
        await EnableRoguelikeWaitAsync(fixture, vm);

        var waitTask = vm.WaitAndStopAsync();
        await WaitUntilAsync(() => vm.IsWaitingForStop);

        Assert.True(vm.CanToggleRun);
        Assert.False(vm.CanWaitAndStop);
        Assert.Equal("等待中...", vm.WaitAndStopButtonText);

        Assert.True((await fixture.Runtime.ConnectFeatureService.StopAsync()).Success);
        await waitTask;
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.False(vm.IsWaitingForStop);
        Assert.True(vm.CanToggleRun);
        Assert.False(vm.CanWaitAndStop);
        Assert.Equal("等待并停止", vm.WaitAndStopButtonText);
        Assert.Equal(1, Assert.IsType<FakeBridge>(fixture.Bridge).StopCallCount);
    }

    [Fact]
    public async Task TaskQueuePage_WaitAndStop_Canceled_ShouldRecoverPendingState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);
        await EnableRoguelikeWaitAsync(fixture, vm);

        using var cts = new CancellationTokenSource();
        var waitTask = vm.WaitAndStopAsync(cts.Token);
        await WaitUntilAsync(() => vm.IsWaitingForStop);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);

        Assert.False(vm.IsWaitingForStop);
        Assert.True(vm.CanToggleRun);
        Assert.True(vm.CanWaitAndStop);
        Assert.Equal("等待并停止", vm.WaitAndStopButtonText);
    }

    [Fact]
    public async Task TaskQueuePage_WaitAndStop_ShouldDisableControlsDuringWait_AndRecoverAfterCancel()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);
        await EnableRoguelikeWaitAsync(fixture, vm);

        using var waitCts = new CancellationTokenSource();
        var waitTask = vm.WaitAndStopAsync(waitCts.Token);
        await WaitUntilAsync(() => vm.IsWaitingForStop);

        Assert.False(vm.CanWaitAndStop);
        Assert.True(vm.CanToggleRun);

        waitCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);

        Assert.True(vm.CanToggleRun);
        Assert.True(vm.CanWaitAndStop);
    }

    private static async Task EnableRoguelikeWaitAsync(
        TestFixture fixture,
        TaskQueuePageViewModel vm)
    {
        var config = fixture.Runtime.ConfigurationService.CurrentConfig;
        Assert.True(config.Profiles.TryGetValue(config.CurrentProfile, out var profile));
        profile!.Values[LegacyConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete] = JsonValue.Create(true);
        vm.SetLanguage("zh-cn");

        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, [new CoreCallbackEvent(
            3,
            "SubTaskExtraInfo",
            """{"task_chain":"Roguelike","what":"StageInfo"}""",
            DateTimeOffset.UtcNow), null]) as Task;
        Assert.NotNull(task);
        await task!;
        await WaitUntilAsync(() => vm.RoguelikeInCombatAndShowWait);
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // The operation is allowed to surface cancellation from the blocked core lifecycle call.
        }
    }

    private static async Task<string> PrepareSecondProfileAsync(
        TestFixture fixture,
        SettingsPageViewModel settings)
    {
        var originalProfile = fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile;
        var addResult = await fixture.Runtime.ConfigurationProfileFeatureService.AddProfileAsync(
            "Second",
            originalProfile);
        Assert.True(addResult.Success, addResult.Message);
        await settings.InitializeAsync();
        Assert.True(settings.IsSessionIdle);
        return originalProfile;
    }

    private static async Task AssertProfileSwitchBlockedAsync(
        TestFixture fixture,
        SettingsPageViewModel settings,
        string originalProfile)
    {
        settings.ConfigurationManagerSelectedProfile = "Second";
        await settings.SwitchConfigurationProfileAsync();
        Assert.Equal(originalProfile, fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.Equal(originalProfile, settings.ConfigurationManagerSelectedProfile);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        int retry = 80,
        int delayMs = 25,
        Func<string>? failureMessage = null)
    {
        for (var i = 0; i < retry; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException(failureMessage?.Invoke() ?? "Condition not reached in expected time.");
    }

    private static bool HasLinkStartFailureLog(TaskQueuePageViewModel vm)
    {
        try
        {
            return vm.LogCards
                .ToArray()
                .SelectMany(card => card.Items.ToArray())
                .Any(entry => entry.Level == "ERROR" && entry.Content.Contains("Link Start failed:", StringComparison.Ordinal));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string FlattenLogs(TaskQueuePageViewModel vm)
    {
        try
        {
            return string.Join(
                "\n",
                vm.LogCards
                    .ToArray()
                    .SelectMany(card => card.Items.ToArray())
                    .Select(entry => entry.Content));
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
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
                        "select": [1]
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

    private static string CreateRunnableConfigJson()
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
                    "ConnectConfig": "General"
                  },
                  "TaskQueue": [
                    {
                      "Type": "StartUp",
                      "Name": "StartUp",
                      "IsEnabled": true,
                      "Params": {
                        "client_type": "Official",
                        "start_game_enabled": true,
                        "account_name": ""
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

    private static string CreateValidationBlockingConfigJson()
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
                      "Type": "Reclamation",
                      "Name": "reclamation-ok",
                      "IsEnabled": true,
                      "Params": {
                        "theme": "Tales",
                        "mode": 1,
                        "increment_mode": 0,
                        "num_craft_batches": 16,
                        "tools_to_craft": [],
                        "clear_store": false
                      }
                    },
                    {
                      "Type": "Recruit",
                      "Name": "recruit-blocking",
                      "IsEnabled": true,
                      "Params": {
                        "times": -1
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

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(string root, MAAUnifiedRuntime runtime, IMaaCoreBridge bridge)
        {
            Root = root;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public IMaaCoreBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(
            string? existingAvaloniaJson = null,
            IMaaCoreBridge? bridge = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            TouchMacMaaAdbControlUnitLibrary(root);
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
            await config.LoadOrBootstrapAsync();
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }

            bridge ??= new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(
                session,
                config,
                log,
                bridge,
                root,
                enableQuickConnectionPrecheck: false)
            {
                MacRawByNcRiskPromptService = new RecordingMacRawByNcPromptService(
                    MacRawByNcRiskConnectionDecision.ForceRun),
            };
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = new ShellFeatureService(connect),
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new TestFixture(root, runtime, bridge);
        }

        private static void TouchMacMaaAdbControlUnitLibrary(string runtimeBaseDirectory)
        {
            Directory.CreateDirectory(runtimeBaseDirectory);
            File.WriteAllText(
                RuntimeLayout.ResolveMacMaaFrameworkRuntimeLibraryPath(
                    runtimeBaseDirectory,
                    RuntimeLayout.MacMaaAdbControlUnitLibraryFileName),
                "test-control-unit");
        }

        public async Task<string> CreateExecutableAdbAsync(string name)
        {
            var directory = Path.Combine(Root, "adb-tools", name);
            Directory.CreateDirectory(directory);
            var adbPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            await File.WriteAllTextAsync(
                adbPath,
                OperatingSystem.IsWindows()
                    ? string.Empty
                    : """
                      #!/bin/sh
                      if [ "$1" = "-s" ] && [ "$3" = "get-state" ]; then
                        printf 'device\n'
                        exit 0
                      fi
                      if [ "$1" = "devices" ]; then
                        printf 'List of devices attached\nemulator-5554\tdevice\nemulator-5556\tdevice\n127.0.0.1:5555\tdevice\n'
                        exit 0
                      fi
                      if [ "$1" = "connect" ] && [ -n "$2" ]; then
                        printf 'already connected to %s\n' "$2"
                        exit 0
                      fi
                      exit 0
                      """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    adbPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return adbPath;
        }

        public string CreateCopilotFile()
        {
            var path = Path.Combine(Root, "copilot-activity.json");
            var payload = new JsonObject
            {
                ["stage_name"] = "1-7",
                ["minimum_required"] = "v4.0",
                ["actions"] = new JsonArray(new JsonObject()),
            };
            File.WriteAllText(path, payload.ToJsonString());
            return path;
        }

        public void WriteConnectionStateToProfile(ConnectionGameSharedStateViewModel state)
        {
            Assert.True(Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
            ConnectionGameProfileSync.WriteToProfile(profile, state);
        }

        public async ValueTask DisposeAsync()
        {
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

    private sealed class RecordingDialogService(DialogReturnSemantic warningConfirmReturn) : IAppDialogService
    {
        public int WarningConfirmCallCount { get; private set; }

        public string LastWarningMessage { get; private set; } = string.Empty;

        public WarningConfirmDialogRequest? LastWarningRequest { get; private set; }

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCallCount++;
            LastWarningMessage = request.Message;
            LastWarningRequest = request;
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                warningConfirmReturn,
                warningConfirmReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "recording"));
        }
    }

    private sealed class RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision decision)
        : IMacRawByNcRiskConnectionPromptService
    {
        public int CallCount { get; private set; }

        public MacRawByNcRiskConnectionPrompt? LastPrompt { get; private set; }

        public Task<MacRawByNcRiskConnectionDecision> ConfirmAsync(
            MacRawByNcRiskConnectionPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = prompt;
            return Task.FromResult(decision);
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private bool _connected;
        private bool _running;

        public int ConnectCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public CoreConnectionInfo? LastConnectionInfo { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectionInfo = connectionInfo;
            _connected = !string.IsNullOrWhiteSpace(connectionInfo.Address);
            return Task.FromResult(_connected
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed")));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            if (!_connected)
            {
                return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
            }

            _running = true;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            var wasRunning = _running;
            _running = false;
            return Task.FromResult(wasRunning
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "not running")));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingConnectBridge : IMaaCoreBridge
    {
        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "Connection callback reported `ConnectFailed`.")));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "not running")));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, false, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingConnectBridge : IMaaCoreBridge
    {
        private readonly TaskCompletionSource<CoreResult<bool>> _connectCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CoreResult<int>> _appendCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CoreResult<bool>> _startCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _connected;
        private bool _running;

        public bool BlockConnect { get; set; } = true;
        public bool BlockAppend { get; set; }
        public bool BlockStart { get; set; }
        public bool IgnoreConnectCancellation { get; set; }
        public int ConnectCallCount { get; private set; }
        public int AppendCallCount { get; private set; }
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public TaskCompletionSource<bool> FirstConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AppendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StartStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CoreResult<bool>>? StopCompletion { get; set; }

        public void CompleteConnect(bool success, string message = "connect failed")
        {
            _connected = success;
            _connectCompletion.TrySetResult(success
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, message)));
        }

        public void CompleteStart(bool success, string message = "start failed")
        {
            _running = success;
            _startCompletion.TrySetResult(success
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, message)));
        }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            FirstConnectStarted.TrySetResult(true);
            if (!BlockConnect)
            {
                _connected = !string.IsNullOrWhiteSpace(connectionInfo.Address);
                return _connected
                    ? CoreResult<bool>.Ok(true)
                    : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed"));
            }

            if (IgnoreConnectCancellation)
            {
                return await _connectCompletion.Task;
            }

            using var registration = cancellationToken.Register(() => _connectCompletion.TrySetCanceled(cancellationToken));
            return await _connectCompletion.Task;
        }

        public async Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendCallCount++;
            AppendStarted.TrySetResult(true);
            if (!BlockAppend)
            {
                return CoreResult<int>.Ok(1);
            }

            return await _appendCompletion.Task.WaitAsync(cancellationToken);
        }

        public async Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            StartStarted.TrySetResult(true);
            if (!_connected)
            {
                return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected"));
            }

            if (!BlockStart)
            {
                _running = true;
                return CoreResult<bool>.Ok(true);
            }

            return await _startCompletion.Task.WaitAsync(cancellationToken);
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            _running = false;
            StopStarted.TrySetResult(true);
            return StopCompletion?.Task
                ?? Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "not running")));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingScreenshotBridge : IMaaCoreBridge
    {
        private bool _connected;
        private bool _running;

        public bool BlockConnect { get; set; } = true;
        public bool BlockCapture { get; set; }
        public int ConnectCallCount { get; private set; }
        public int AppendCallCount { get; private set; }
        public int StartCallCount { get; private set; }
        public CoreConnectionInfo? LastConnectionInfo { get; private set; }
        public TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CaptureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CoreResult<bool>>? ConnectCompletion { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CoreResult<byte[]>>? CaptureCompletion { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectionInfo = connectionInfo;
            ConnectStarted.TrySetResult(true);
            if (BlockConnect)
            {
                return await ConnectCompletion!.Task.WaitAsync(cancellationToken);
            }

            _connected = !string.IsNullOrWhiteSpace(connectionInfo.Address);
            return _connected
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed"));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendCallCount++;
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            if (!_connected)
            {
                return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
            }

            _running = true;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            _running = false;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async Task<CoreResult<byte[]>> GetImageBgrAsync(bool forceScreencap = false, CancellationToken cancellationToken = default)
        {
            CaptureStarted.TrySetResult(true);
            if (BlockCapture)
            {
                return await CaptureCompletion!.Task.WaitAsync(cancellationToken);
            }

            return CoreResult<byte[]>.Ok([1, 2, 3]);
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
