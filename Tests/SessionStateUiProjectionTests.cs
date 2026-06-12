using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SessionStateUiProjectionTests
{
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
    public async Task TaskQueuePage_ExternalRunOwner_ShouldKeepStartButtonAndStopFromDialog()
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
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);

        await vm.ToggleRunAsync();

        Assert.Equal(1, dialog.WarningConfirmCallCount);
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
        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Toolbox", "窥屏", out _));
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

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

        var startTask = vm.StartAsync();
        await bridge.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => vm.IsStartRequestActive);

        Assert.True(vm.IsOwnRunActive);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        var stopClickTask = vm.ToggleRunAsync();
        await stopClickTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, bridge.StopCallCount);
        Assert.False(vm.IsStartRequestActive);
        Assert.False(vm.IsOwnRunActive);
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), vm.RunButtonText);
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));
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
        Assert.False(vm.IsRunning);

        Assert.True((await fixture.Runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Connected);

        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.False(vm.IsRunning);

        Assert.True((await fixture.Runtime.ConnectFeatureService.StartAsync()).Success);
        await WaitUntilAsync(() => vm.CurrentSessionState == SessionState.Running);

        Assert.True(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.True(vm.IsRunning);
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

        var waitTask = vm.WaitAndStopAsync();
        await WaitUntilAsync(() => vm.IsWaitingForStop);

        Assert.False(vm.CanToggleRun);
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

        using var waitCts = new CancellationTokenSource();
        var waitTask = vm.WaitAndStopAsync(waitCts.Token);

        Assert.False(vm.CanWaitAndStop);
        Assert.False(vm.CanToggleRun);

        waitCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);

        Assert.True(vm.CanWaitAndStop);
        Assert.True(vm.CanToggleRun);
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
            var connect = new ConnectFeatureService(session, config, log, bridge, root);
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

        private bool _connected;
        private bool _running;

        public bool BlockConnect { get; set; } = true;
        public bool IgnoreConnectCancellation { get; set; }
        public int ConnectCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public TaskCompletionSource<bool> FirstConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CoreResult<bool>>? StopCompletion { get; set; }

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

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            if (!_connected)
            {
                return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
            }

            return Task.FromResult(CoreResult<bool>.Ok(true));
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
        public int ConnectCallCount { get; private set; }
        public CoreConnectionInfo? LastConnectionInfo { get; private set; }
        public TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CoreResult<bool>>? ConnectCompletion { get; set; } =
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
            _running = false;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public Task<CoreResult<byte[]>> GetImageBgrAsync(bool forceScreencap = false, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Ok([1, 2, 3]));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
