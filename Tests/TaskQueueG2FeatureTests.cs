using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class TaskQueueG2FeatureTests
{
    private static readonly byte[] SinglePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+W0cAAAAASUVORK5CYII=");

    [Fact]
    public async Task StartUpModule_ClientTypeEmptySelectionDuringUiAttach_ShouldNotClearAccountName()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            AccountName = "doctor-a",
            ClientType = "Official",
            StartGameEnabled = true,
            ConnectConfig = "General",
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = fixture.ReadyAdbPath,
            TouchMode = "MaaFwAdb",
            AutoDetectConnection = false,
            AttachWindowScreencapMethod = "2",
            AttachWindowMouseMethod = "64",
            AttachWindowKeyboardMethod = "64",
        })).Success);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ClientType = "Official",
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.Equal("doctor-a", vm.StartUpModule.AccountName);

        vm.StartUpModule.SelectedClientTypeValue = string.Empty;
        vm.StartUpModule.SelectedClientTypeOption = null;

        Assert.Equal("Official", vm.StartUpModule.ClientType);
        Assert.Equal("doctor-a", vm.StartUpModule.AccountName);

        vm.StartUpModule.SelectedClientTypeValue = "YoStarEN";

        Assert.Equal("YoStarEN", vm.StartUpModule.ClientType);
        // WPF parity: the stored account name is preserved when switching to a
        // client without account switching; only serialization blanks it.
        Assert.Equal("doctor-a", vm.StartUpModule.AccountName);
    }

    [Fact]
    public async Task CustomModule_UsesWpfCommaDelimiterAndPreservesRawNames()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.CustomModule.TaskNamesText = "First;Second|Third\nFourth，FinalTask, FinalTask";
        Assert.True(await vm.CustomModule.SaveAsync());

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var taskNames = Assert.IsType<JsonArray>(parameters["task_names"]);
        Assert.Equal(
            new[] { "First;Second|Third\nFourth", "FinalTask", "FinalTask" },
            taskNames.Select(node => node?.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task AccountSwitchManualRun_ShouldConnectThenAppendStartUpWhenInitiallyDisconnected()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            AccountName = "doctor-a",
            ClientType = "Official",
            StartGameEnabled = true,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = fixture.ReadyAdbPath,
            TouchMode = "MaaFwAdb",
            AutoDetectConnection = false,
            AttachWindowScreencapMethod = "2",
            AttachWindowMouseMethod = "64",
            AttachWindowKeyboardMethod = "64",
        })).Success);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ClientType = "Official",
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = fixture.ReadyAdbPath,
            TouchMode = "MaaFwAdb",
            AutoDetect = false,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.NotEqual(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
        await vm.StartUpModule.RunAccountSwitchManualAsync();

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(TaskModuleTypes.StartUp, Assert.Single(fixture.Bridge.AppendedTasks).Type);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task StartAsync_ShouldFlushDirtyBoundModulesBeforeQueueAndStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "flush-before-start";

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal("flush-before-start", appendedParams["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task StartAsync_WhenNoTasksAreRunnable_ShouldNotCallCoreStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "disabled-startup", enabled: false)).Success);

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
                ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();
        var persistedQueue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(persistedQueue.Success);
        Assert.False(Assert.Single(persistedQueue.Value!).IsEnabled);

        await vm.StartAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        // WPF LinkStart parity: an empty append result still stops the core so any
        // residual queue from a previous attempt cannot leak into the next run.
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.False(vm.IsRunning);
        Assert.Contains("可执行的核心任务", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenSecondAppendFails_ShouldClearCoreQueueAndAllowRetry()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        fixture.Bridge.FailAppendCallNumber = 2;

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
                ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.FailAppendCallNumber = null;
        await vm.StartAsync();

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartAsync_WhenCoreStartFails_ShouldClearCoreQueueAndAllowRetry()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ForceStartFailure = true;

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
                ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.ForceStartFailure = false;
        await vm.StartAsync();

        Assert.Equal(2, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartAsync_WhenRunOwnerIsSelfAndDialogDismissed_ShouldKeepRunningRunAndLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var dialogService = new RunOwnerDialogService { WarningConfirmReturn = DialogReturnSemantic.Close };
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialogService);
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        vm.AppendSystemLog("run-one-marker");
        var logCountBeforeBlockedStart = vm.LogCards.SelectMany(card => card.Items).Count();
        Assert.True(logCountBeforeBlockedStart > 0);

        // A racing second start (e.g. a scheduled LinkStart while the first run is active)
        // is rejected by the self-owner check. The rejected start must neither clear the
        // visible logs of the run still in progress nor disturb the running state.
        await vm.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dialogService.WarningConfirmCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        // The rejection may append its own failure entry, but it must never clear the
        // visible logs of the run that is still in progress.
        Assert.True(vm.LogCards.SelectMany(card => card.Items).Count() >= logCountBeforeBlockedStart);
        Assert.Equal(1, CountTaskQueueLogs(vm, "run-one-marker"));
    }

    [Fact]
    public async Task StartAsync_WhenRunOwnerIsSelfAndDialogConfirmsStop_ShouldStopWithoutDeadlock()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var dialogService = new RunOwnerDialogService { WarningConfirmReturn = DialogReturnSemantic.Cancel };
        TaskQueuePageViewModel? vmRef = null;
        var stopOwnerCalls = 0;
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialogService,
            stopRunOwnerAsync: async (owner, cancellationToken) =>
            {
                stopOwnerCalls++;
                Assert.Equal("TaskQueue", owner);
                await vmRef!.StopAsync(cancellationToken);
            });
        vmRef = vm;
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);

        // Confirming "stop current task" routes back into StopAsync. StartAsync must have
        // released the run-transition lock before showing the dialog, otherwise this call
        // deadlocks and every subsequent run transition is blocked until app restart.
        await vm.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dialogService.WarningConfirmCount);
        Assert.Equal(1, stopOwnerCalls);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task ToggleRunAsync_WhenCoreQueueCleanupRequiredAndSessionIdle_ShouldRetryCleanupAndRecover()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.FailAppendCallNumber = 2;
        fixture.Bridge.ForceStopFailure = true;
        // Make the failed cleanup stop look like an abandoned native stop so the session
        // falls back to Idle while the VM still requires a core-queue cleanup.
        fixture.Bridge.StopFailureMessage = "native stop timed out";

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEdit);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);

        fixture.Bridge.ForceStopFailure = false;
        fixture.Bridge.FailAppendCallNumber = null;

        // The cleanup flag must retry the core stop even in Idle state; otherwise the run
        // control stays stuck in a non-editable "running" state with no way to recover.
        await vm.ToggleRunAsync();

        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.False(vm.IsRunning);
        Assert.True(vm.CanEdit);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        await vm.StartAsync();
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task StartAsync_WhenAppendThrowsUnexpectedly_ShouldCleanupCoreQueueAndNotThrow()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.ThrowAppendCallNumber = 2;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // A non-cancellation failure mid-start must be contained inside StartAsync: callers
        // reach this method from async-void UI handlers, and the partially appended core
        // queue must be cleaned up so it cannot leak into the next run.
        await vm.StartAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.ThrowAppendCallNumber = null;
        await vm.StartAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task StopAsync_DuringRoguelikeDelayWait_RepeatedStopForcesImmediateStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.Values[LegacyConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete] = JsonValue.Create(true);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        // Enter roguelike combat so StopAsync takes the delayed-stop wait (up to 600s).
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "SubTaskExtraInfo",
            """{"task_chain":"Roguelike","what":"StageInfo"}""",
            DateTimeOffset.UtcNow));

        var firstStop = vm.StopAsync();
        await Task.Delay(500);
        Assert.False(firstStop.IsCompleted);
        Assert.Equal(0, fixture.Bridge.StopCallCount);

        // WPF parity (WaitUntilRoguelikeCombatComplete breaks on Stopping): a repeated Stop
        // while the delayed stop is waiting must force the wait to end immediately instead of
        // blocking every run transition behind the lock until combat ends or 600s elapse.
        var secondStop = vm.StopAsync();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task RunSelectedTaskOnce_WhenRunOwnerIsSelf_ShouldKeepRunningRunAndLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        vm.AppendSystemLog("run-once-marker");
        var logCountBeforeRejectedRunOnce = vm.LogCards.SelectMany(card => card.Items).Count();
        Assert.True(logCountBeforeRejectedRunOnce > 0);

        vm.SelectedTask = vm.Tasks[0];
        // A run-once request rejected by the self-owner check must not clear the visible logs
        // of the run that is still in progress (same rule as StartAsync).
        await vm.RunSelectedTaskOnceAsync();

        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.True(vm.LogCards.SelectMany(card => card.Items).Count() >= logCountBeforeRejectedRunOnce);
        Assert.Equal(1, CountTaskQueueLogs(vm, "run-once-marker"));
    }

    [Fact]
    public async Task RunSelectedTaskOnce_WhenAppendThrowsUnexpectedly_ShouldCleanupCoreQueueAndNotThrow()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.ThrowAppendCallNumber = 1;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];

        // A non-cancellation failure mid-run-once must be contained inside the method: callers
        // reach it from async-void UI handlers, and the core queue must be cleaned up.
        await vm.RunSelectedTaskOnceAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.ThrowAppendCallNumber = null;
        await vm.RunSelectedTaskOnceAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal("startup-a", Assert.Single(fixture.Bridge.AppendedTasks).Name);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task AccountSwitch_WhenAppendThrowsUnexpectedly_ShouldCleanupCoreQueueAndNotThrow()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.ThrowAppendCallNumber = 1;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "doctor-a";

        // Same containment guarantee as StartAsync: the failure must not escape into the
        // async-void StartUp-module callback, and the core queue must be cleaned up.
        await vm.StartUpModule.RunAccountSwitchManualAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.ThrowAppendCallNumber = null;
        await vm.StartUpModule.RunAccountSwitchManualAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal("startup-a", Assert.Single(fixture.Bridge.AppendedTasks).Name);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task StartAsync_WhenCoreQueueCleanupPending_ShouldRetryCleanupBeforeAppending()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.FailAppendCallNumber = 2;
        fixture.Bridge.ForceStopFailure = true;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.True(vm.IsRunning);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);

        // Release the run owner via a direct stop while the cleanup keeps failing; the cleanup
        // flag stays set, which previously let a timer/tray StartAsync append onto the dirty
        // core queue and re-execute stale tasks.
        await vm.StopAsync();
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.IsRunning);

        await vm.StartAsync();
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.True(vm.IsRunning);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);

        // ToggleRun still recovers once the native stop works again, and the next start runs
        // on a clean queue.
        fixture.Bridge.ForceStopFailure = false;
        fixture.Bridge.FailAppendCallNumber = null;
        await vm.ToggleRunAsync();
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.False(vm.IsRunning);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        await vm.StartAsync();
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task StopAsync_WhenCoreQueueCleanupPending_ShouldRetryCleanupAndClearFlag()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.FailAppendCallNumber = 2;
        fixture.Bridge.ForceStopFailure = true;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.True(vm.IsRunning);

        // A direct StopAsync (e.g. confirmed from the run-owner dialog) must retry the pending
        // cleanup and clear the flag; otherwise IsRunning stays stuck true until the user
        // happens to click ToggleRun.
        fixture.Bridge.ForceStopFailure = false;
        fixture.Bridge.FailAppendCallNumber = null;
        await vm.StopAsync();

        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.False(vm.IsRunning);
        Assert.True(vm.CanEdit);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        await vm.StartAsync();
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_ForUserDataUpdateOnly_ShouldNotTriggerPostAction()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.UserDataUpdate, "user-data-update")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;
        MapCoreTaskId(fixture.Runtime.SessionService, 3, 0);

        // A pure data-update run (OperBox/Depot) is not part of WPF's _mainTaskTypes, so
        // its completion must not trigger notifications or post actions (e.g. shutdown).
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"task_chain":"OperBox","task_index":0,"run_id":"run-g2-userdata","finished_tasks":[3]}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(TaskQueueItemStatus.Success, vm.Tasks[0].Status);
        Assert.Equal(0, fixture.PostAction.ExecuteCount);
    }

    [Fact]
    public void CoreBridge_DefaultStopTimeout_ShouldMatchWpfSixtySecondStopBudget()
    {
        var field = typeof(MaaCoreBridgeNative).GetField("DefaultStopTimeout", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.IsType<TimeSpan>(field!.GetValue(null)));
    }

    [Fact]
    public async Task RunSelectedTaskOnce_WhileConnecting_ShouldBlockEditsAndRunRequestedTask()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        fixture.Bridge.ConnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.ConnectRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
                ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];

        var runTask = vm.RunSelectedTaskOnceAsync();
        await fixture.Bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(vm.CanEdit);
        vm.SelectedTask = vm.Tasks[1];
        await vm.MoveSelectedTaskAsync(-1);
        Assert.Equal(["startup-a", "startup-b"], vm.Tasks.Select(task => task.Name));

        fixture.Bridge.ConnectRelease.TrySetResult(true);
        await runTask;

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("startup-a", appended.Name);
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunSelectedTaskOnce_WhenAppendOrStartFails_ShouldClearCoreQueueAndAllowRetry(bool failAppend)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.FailAppendCallNumber = failAppend ? 1 : null;
        fixture.Bridge.ForceStartFailure = !failAppend;

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
                ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        vm.Tasks[0].Status = TaskQueueItemStatus.Error;

        await vm.RunSelectedTaskOnceAsync();

        Assert.Equal(failAppend ? 0 : 1, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[0].Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.FailAppendCallNumber = null;
        fixture.Bridge.ForceStartFailure = false;
        await vm.RunSelectedTaskOnceAsync();

        Assert.Equal(failAppend ? 1 : 2, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartAsync_StopDuringConnectedAppend_ShouldClearPartialQueueAndNotStartCore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.BlockAppendCallNumber = 2;
        fixture.Bridge.AppendBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.AppendRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var startTask = vm.StartAsync();
        await fixture.Bridge.AppendBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(fixture.Bridge.AppendedTasks);

        await vm.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task RunSelectedTaskOnce_StopDuringCoreStart_ShouldWaitForStopAndNotRemainRunning()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.StartStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.StartRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.IgnoreStartCancellation = true;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);

        var runTask = vm.RunSelectedTaskOnceAsync();
        await fixture.Bridge.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopTask = vm.ToggleRunAsync();
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        fixture.Bridge.StartRelease.TrySetResult(true);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task StartAsync_ConcurrentSameOwnerRequest_ShouldNotReleaseRunningLease()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.BlockAppendCallNumber = 1;
        fixture.Bridge.AppendBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.AppendRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var firstStart = vm.StartAsync();
        await fixture.Bridge.AppendBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStart = vm.StartAsync();
        fixture.Bridge.AppendRelease.TrySetResult(true);
        await Task.WhenAll(firstStart, secondStart).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.False(fixture.Runtime.SessionService.TryBeginRun("RemoteControl", out var currentOwner));
        Assert.Equal("TaskQueue", currentOwner);
    }

    [Fact]
    public async Task AllTasksCompleted_SlowPostAction_ShouldReleaseOwnerBeforePostActionFinishes()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.PostAction.ExecutionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PostAction.ExecutionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.StartAsync();
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Runtime.SessionService.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        fixture.Bridge.Publish(new CoreCallbackEvent(
            10003,
            "AllTasksCompleted",
            """{"finished_tasks":[1],"run_id":"slow-terminal"}""",
            DateTimeOffset.UtcNow));
        Assert.True(await WaitForConditionAsync(() => fixture.Runtime.SessionService.CurrentState == SessionState.Connected));
        await fixture.PostAction.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // WPF parity: AsstProxy marks the run idle (SetIdle(true)) before firing
        // CheckAfterCompleted in the background, so the run owner must be released
        // before post actions run and a new run can start while a slow post action
        // (e.g. the power-action confirmation countdown) is still in progress.
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("RemoteControl", out var currentOwner));
        Assert.Equal("RemoteControl", currentOwner);
        fixture.Runtime.SessionService.EndRun("RemoteControl");

        fixture.PostAction.ExecutionRelease.TrySetResult(true);
        Assert.True(await WaitForConditionAsync(() => fixture.PostAction.ExecuteCount == 1));
        pumpCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask);
    }

    [Fact]
    public async Task StartAsync_WhenCleanupStopFails_ShouldRetainQueueAndOwnerUntilExplicitCleanupSucceeds()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.FailAppendCallNumber = 2;
        fixture.Bridge.ForceStopFailure = true;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(vm.CanEdit);
        Assert.True(vm.IsOwnRunActive);
        Assert.False(fixture.Runtime.SessionService.TryBeginRun("RemoteControl", out _));

        await vm.StartAsync();
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.StartCallCount);

        fixture.Bridge.ForceStopFailure = false;
        await vm.ToggleRunAsync();
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanEdit);

        fixture.Bridge.FailAppendCallNumber = null;
        await vm.StartAsync();
        Assert.Equal(["startup-a", "startup-b"], fixture.Bridge.AppendedTasks.Select(task => task.Name));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task AccountSwitch_WhileAppendBlocked_ShouldRejectQueueMoveAndRunOriginalStartup()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.BlockAppendCallNumber = 1;
        fixture.Bridge.AppendBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.AppendRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "doctor-a";

        var accountSwitchTask = vm.StartUpModule.RunAccountSwitchManualAsync();
        await fixture.Bridge.AppendBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.CanEdit);

        vm.SelectedTask = vm.Tasks[1];
        await vm.MoveSelectedTaskAsync(-1);
        Assert.Equal(["startup-a", "startup-b"], vm.Tasks.Select(task => task.Name));

        fixture.Bridge.AppendRelease.TrySetResult(true);
        await accountSwitchTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("startup-a", Assert.Single(fixture.Bridge.AppendedTasks).Name);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Theory]
    [InlineData("TaskChainCompleted")]
    [InlineData("TaskChainError")]
    [InlineData("SubTaskError")]
    public async Task NonTerminalCallback_ShouldKeepTaskQueueRunOwnerUntilOverallTerminalCallback(string callbackName)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            callbackName,
            """{"task_chain":"Fight","task_index":0,"run_id":"run-owner"}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.False(fixture.Runtime.SessionService.TryBeginRun("RemoteControl", out var currentOwner));
        Assert.Equal("TaskQueue", currentOwner);

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10003,
            "AllTasksCompleted",
            """{"finished_tasks":[1],"run_id":"run-owner"}""",
            DateTimeOffset.UtcNow));

        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartUpModule_SaveAsync_ShouldPersistMacUseBundledAdbToProfileAndReload(bool useBundledAdb)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(!useBundledAdb);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = fixture.ReadyAdbPath,
            MacUseBundledAdb = !useBundledAdb,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.StartUpModule.MacUseBundledAdb = useBundledAdb;

        Assert.True(await vm.StartUpModule.SaveAsync());
        Assert.Equal(useBundledAdb, profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey]?.GetValue<bool>());

        var queueResult = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queueResult.Success);
        var task = Assert.Single(queueResult.Value!);
        var (dto, issues) = TaskParamCompiler.ReadStartUp(
            task,
            profile,
            fixture.Runtime.ConfigurationService.CurrentConfig,
            strict: true);
        Assert.Empty(issues);
        Assert.Equal(useBundledAdb, dto.MacUseBundledAdb);

        var reloadedShared = new ConnectionGameSharedStateViewModel();
        ConnectionGameProfileSync.ReadFromProfile(profile, reloadedShared, tolerateMissing: false);
        var reloadedVm = new TaskQueuePageViewModel(fixture.Runtime, reloadedShared);
        await reloadedVm.InitializeAsync();
        reloadedVm.SelectedTask = Assert.Single(reloadedVm.Tasks);
        await reloadedVm.WaitForPendingBindingAsync();

        Assert.Equal(useBundledAdb, reloadedVm.StartUpModule.MacUseBundledAdb);
    }

    [Fact]
    public async Task StartAsync_WhenAutoConnectFails_ShouldRaiseConnectFailedDialog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ForceConnectFailure = true;

        var raised = new List<DialogErrorRaisedEvent>();
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised.Add(e);
        var navigatedSection = string.Empty;
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            RetryOnDisconnected = false,
            AllowAdbRestart = false,
            AllowAdbHardRestart = false,
        };
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            sharedState,
            navigateToSettingsSection: section => navigatedSection = section);
        await vm.InitializeAsync();

        await vm.StartAsync();

        var dialogError = Assert.Single(
            raised,
            error => string.Equals(error.Context, "TaskQueue.Start", StringComparison.Ordinal));
        Assert.Equal(UiErrorCode.ConnectFailed, dialogError.Result.Error?.Code);
        Assert.Equal(string.Empty, navigatedSection);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task StartAsync_WithNoAppendedTasks_ShouldNotStartSession_LikeWpf()
    {
        // WPF parity: TaskQueueViewModel logs "UnselectedTask", returns to idle and
        // does not call AsstStart when zero tasks were appended.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(0, false)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.False(string.IsNullOrWhiteSpace(vm.LastErrorMessage));
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        // The run owner must be released so a later start can proceed.
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(0, true)).Success);
        await vm.ReloadTasksAsync();
        await vm.StartAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task StopAsync_ShouldFlushDirtyBoundModulesBeforeStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "before-start";

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        vm.StartUpModule.AccountName = "before-stop";

        await vm.StopAsync();

        Assert.Equal(1, fixture.Bridge.StopCallCount);
        var paramsResult = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(paramsResult.Success);
        Assert.Equal("before-stop", paramsResult.Value?["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task StopAsync_WhenBoundModuleSaveFails_ShouldStillStopRunningQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        // Queue a dirty module edit, then make the flush fail by removing the profile's
        // task entries so persisting the bound module can no longer resolve its task.
        vm.StartUpModule.AccountName = "before-stop";
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.TaskQueue.Clear();

        await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // WPF parity: Stop() has no configuration-save precondition. A failed flush must
        // not abort the stop, otherwise the running queue stays unstoppable while the
        // config store is failing.
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WhenForcedDelayedStopFails_ShouldNotReenterDelayWaitOnNextStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.Values[LegacyConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete] = JsonValue.Create(true);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        // Enter roguelike combat so StopAsync takes the delayed-stop wait (up to 600s).
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "SubTaskExtraInfo",
            """{"task_chain":"Roguelike","what":"StageInfo"}""",
            DateTimeOffset.UtcNow));

        var firstStop = vm.StopAsync();
        await Task.Delay(500);
        Assert.False(firstStop.IsCompleted);
        Assert.Equal(0, fixture.Bridge.StopCallCount);

        // The repeated stop forces the delayed wait to end, but the core stop fails.
        fixture.Bridge.ForceStopFailure = true;
        var secondStop = vm.StopAsync();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal(2, fixture.Bridge.StopCallCount);

        // WPF parity: ManualStop always stops immediately. The failed forced stop must
        // not consume the force request, so the next stop attempts the core stop
        // immediately instead of re-entering the 600s delayed wait.
        fixture.Bridge.ForceStopFailure = false;
        await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, fixture.Bridge.StopCallCount);
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ToggleRunAsync_WhenCleanupRequiredAndRunOwnedByAnotherFeature_ShouldShowDialogInsteadOfStoppingCore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.Bridge.FailAppendCallNumber = 2;
        fixture.Bridge.ForceStopFailure = true;

        var dialogService = new RunOwnerDialogService { WarningConfirmReturn = DialogReturnSemantic.Close };
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialogService);
        await vm.InitializeAsync();

        // A failed start whose cleanup stop also fails leaves the core-queue cleanup
        // flag set and the run owner retained.
        await vm.StartAsync();
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.IsRunning);

        // A direct stop releases the owner but the cleanup flag stays set because the
        // cleanup retry keeps failing.
        await vm.StopAsync();
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.IsRunning);

        // Another feature (e.g. Copilot) acquires the run and starts running.
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Copilot", out _));
        Assert.True((await fixture.Runtime.SessionService.StartAsync()).Success);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        var stopCallsBeforeToggle = fixture.Bridge.StopCallCount;

        // The pending cleanup flag must not bypass the run-owner confirmation dialog
        // or silently stop the other feature's run.
        await vm.ToggleRunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dialogService.WarningConfirmCount);
        Assert.Equal(stopCallsBeforeToggle, fixture.Bridge.StopCallCount);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("Copilot", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartAndStopAsync_ShouldUpdateAchievementTracker()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        await vm.StopAsync();

        Assert.Equal(2, fixture.Runtime.AchievementTrackerService.GetProgress("TaskChainKing"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgress("MissionStartCount"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("UseDaily"));

        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync("zh-cn");
        Assert.True(snapshot.Success);
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "TacticalRetreat" && item.IsUnlocked);
    }

    [Fact]
    public async Task RunningState_ShouldBlockQueueMutationsAndEnabledToggle()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.True(vm.IsRunning);

        var beforeCount = vm.Tasks.Count;
        await vm.AddTaskAsync();
        Assert.Equal(beforeCount, vm.Tasks.Count);
        Assert.False(string.IsNullOrWhiteSpace(vm.LastErrorMessage));

        var originalEnabled = vm.Tasks[0].IsEnabled;
        vm.Tasks[0].IsEnabled = !originalEnabled;
        var reverted = await WaitForConditionAsync(() => vm.Tasks[0].IsEnabled == originalEnabled);
        Assert.True(reverted);
    }

    [Fact]
    public async Task StartAsync_ShouldEnterRunningUiStateWhileAutoConnectIsPending()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ConnectDelay = TimeSpan.FromMilliseconds(200);
        fixture.Bridge.ConnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = fixture.ReadyAdbPath,
            MacUseBundledAdb = false,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();

        var startTask = vm.StartAsync();
        await fixture.Bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEdit);
        Assert.True(vm.CanToggleRun);
        Assert.True(vm.IsOwnRunActive);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        await startTask;

        Assert.True(vm.IsRunning);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);
    }

    [Fact]
    public async Task StartAsync_AutoConnectFailure_ShouldReturnToEditableStateAndReportError()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ForceConnectFailure = true;

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = fixture.ReadyAdbPath,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Contains("连接失败", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildConnectFailureMessage_WhenConnectResultHasSpecificDiagnostic_ShouldKeepDiagnosticFirstLine()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5554",
                ConnectConfig = "General",
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        var diagnosticMessage = string.Join(
            Environment.NewLine,
            "无法连接到这个地址或端口。",
            "请确认模拟器已启动，连接地址和端口正确；常见 ADB 端口为 5555。");
        var connectResult = UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            diagnosticMessage,
            "probe=tcp host=127.0.0.1 port=5554 timeoutMs=750");

        var message = Assert.IsType<string>(
            typeof(TaskQueuePageViewModel)
                .GetMethod("BuildConnectFailureMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [connectResult]));
        var firstLine = message
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First();
        var localized = DialogTextCatalog.LocalizeErrorResult(
            "zh-cn",
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, message, connectResult.Error?.Details));

        Assert.Equal("无法连接到这个地址或端口。", firstLine);
        Assert.Equal("无法连接到这个地址或端口。", localized.Message);
        Assert.Equal(localized.Message, localized.Error?.Message);
        Assert.NotEqual("连接模拟器失败。", localized.Message);
        Assert.Contains("连接失败。请", message, StringComparison.Ordinal);
        Assert.DoesNotContain("连接回调：无法连接到这个地址或端口。", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-cn", "连接失败，正在尝试通过 ADB 重新连接。", "Connection failed")]
    [InlineData("en-us", "Connection failed. Trying to reconnect by ADB.", "连接失败")]
    public async Task ConnectionRecoveryAttemptLog_ShouldUseCurrentLanguageOnly(
        string language,
        string expected,
        string unexpected)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage(language);

        typeof(TaskQueuePageViewModel)
            .GetMethod("AppendConnectionRecoveryAttemptLog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, ["正在尝试通过 ADB 重新连接。", "Trying to reconnect by ADB."]);

        var content = Assert.Single(vm.LogCards).PrimaryContent;
        Assert.Equal(expected, content);
        Assert.DoesNotContain(unexpected, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_TaskChainStart_WithTaskIndex_ShouldUpdateOnlyTargetTask()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal(2, vm.Tasks.Count);

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":1,"run_id":"run-g2"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
    }

    [Fact]
    public async Task Callback_TaskChainStart_ShouldAppendWpfTaskStartLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-log"}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("开始任务: fight-a", card.PrimaryContent);
    }

    [Fact]
    public async Task LanguageSwitch_ShouldRefreshLiveTaskTextWithoutBacktrackingExistingRuntimeLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "Fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        var taskBefore = Assert.Single(vm.Tasks);
        var displayNameBefore = taskBefore.DisplayName;

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-language"}""",
            DateTimeOffset.UtcNow));

        var startLogBefore = Assert.Single(vm.LogCards).PrimaryContent;

        vm.SetLanguage("en-us");

        var taskAfter = Assert.Single(vm.Tasks);
        Assert.Equal("Combat", taskAfter.DisplayName);
        Assert.NotEqual(displayNameBefore, taskAfter.DisplayName);
        Assert.Equal(startLogBefore, Assert.Single(vm.LogCards).PrimaryContent);

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-language"}""",
            DateTimeOffset.UtcNow));

        var logEntries = vm.LogCards
            .SelectMany(card => card.Items)
            .ToArray();
        Assert.Equal(startLogBefore, logEntries[0].Content);
        Assert.Contains("Combat", logEntries[^1].Content, StringComparison.Ordinal);
        Assert.NotEqual(startLogBefore, logEntries[^1].Content);
    }

    [Fact]
    public async Task Callback_TaskChainCompleted_ShouldMergeIntoTaskStartCard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "开始唤醒")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_index":0,"run_id":"run-g2-startup-split"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "TaskChainCompleted",
            """{"task_chain":"StartUp","task_index":0,"run_id":"run-g2-startup-split"}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal(2, card.Items.Count);
        Assert.Contains("开始任务: 开始唤醒", card.Items[0].Content, StringComparison.Ordinal);
        Assert.Contains("完成任务: 开始唤醒", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitch_AfterFightSanitySnapshot_ShouldUseCurrentLanguageForFutureLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "Fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10010,
            "SubTaskExtraInfo",
            """{"what":"SanityBeforeStage","details":{"current_sanity":96,"max_sanity":135,"report_time":"2026-04-03 12:00:00.000"}}""",
            DateTimeOffset.UtcNow));

        vm.SetLanguage("en-us");

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10011,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-sanity-language"}""",
            DateTimeOffset.UtcNow));

        var content = Assert.Single(vm.LogCards).PrimaryContent;
        Assert.Contains("Sanity:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("理智:", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_StageDrops_ShouldMergeIntoCurrentCardSortDropsAndRequestThumbnail()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-stage-drops"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"StageDrops","details":{"stats":[{"itemName":"A","quantity":10000,"addQuantity":1},{"itemName":"B","quantity":3,"addQuantity":2},{"itemName":"C","quantity":20000,"addQuantity":2}],"stage":{"stageCode":"1-7"},"cur_times":1}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.Bridge.GetImageCallCount > 0));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal(2, card.Items.Count);
        Assert.Equal("开始任务: fight-a", card.Items[0].Content);
        Assert.Contains("1-7", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("C : 20k (+2)", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("B : 3 (+2)", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("A : 10k (+1)", card.PrimaryContent, StringComparison.Ordinal);
        Assert.True(
            card.PrimaryContent.IndexOf("C : 20k (+2)", StringComparison.Ordinal)
            < card.PrimaryContent.IndexOf("B : 3 (+2)", StringComparison.Ordinal));
        Assert.True(
            card.PrimaryContent.IndexOf("B : 3 (+2)", StringComparison.Ordinal)
            < card.PrimaryContent.IndexOf("A : 10k (+1)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_EnterFacility_ShouldLocalizeFacilityNameLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "base-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            """{"task_chain":"Infrast","task_index":0,"what":"EnterFacility","details":{"facility":"Mfg","index":0}}""",
            DateTimeOffset.UtcNow));

        var content = Assert.Single(vm.LogCards).PrimaryContent;
        Assert.Equal("当前设施: 制造站 01", content);
    }

    [Fact]
    public async Task Callback_RecruitResult_ShouldPreserveWpfRareOperatorMetadata()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10003,
            "SubTaskExtraInfo",
            """{"task_chain":"Recruit","task_index":0,"what":"RecruitResult","details":{"level":5}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10004,
            "SubTaskExtraInfo",
            """{"task_chain":"Recruit","task_index":0,"what":"RecruitResult","details":{"level":3}}""",
            DateTimeOffset.UtcNow));

        // WPF: level + " ★ Tags"; >=5 uses RareOperator + Bold, otherwise Info color.
        var card = Assert.Single(vm.LogCards);
        Assert.Equal(2, card.Items.Count);
        var rare = card.Items[0];
        Assert.Equal("5 ★ Tags", rare.Content);
        Assert.Equal("RareOperatorLogBrush", rare.Color);
        Assert.True(rare.IsBold);
        Assert.True(rare.IsRareOperatorColor);
        var normal = card.Items[1];
        Assert.Equal("3 ★ Tags", normal.Content);
        Assert.Equal("InfoLogBrush", normal.Color);
        Assert.False(normal.IsBold);
        Assert.True(normal.IsInfoColor);
    }

    [Fact]
    public async Task AppendSystemLog_WithTimestampedMultilineText_ShouldCreateSingleCard()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.AppendSystemLog("""
            10:31:01
            开始任务：信用收支
            10:32:22
            完成任务：访问好友
            """);

        var card = Assert.Single(vm.LogCards);
        Assert.Contains("10:31:01", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("开始任务：信用收支", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("10:32:22", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("完成任务：访问好友", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_MallCompletedLogs_ShouldMergeIntoCurrentCard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "信用收支")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Mall","task_index":0,"run_id":"run-g2-mall-split"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskCompleted",
            """{"task_chain":"Mall","task_index":0,"sub_task":"ProcessTask","details":{"task":"VisitNextBlack"}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal(2, card.Items.Count);
        Assert.Contains("开始任务: 信用收支", card.Items[0].Content, StringComparison.Ordinal);
        Assert.Contains("完成任务: 访问好友", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_InfrastClueTasks_ShouldUpdateAchievementTracker()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            30001,
            "SubTaskCompleted",
            """{"task_chain":"Infrast","sub_task":"ProcessTask","details":{"task":"UnlockClues"}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            30002,
            "SubTaskCompleted",
            """{"task_chain":"Infrast","sub_task":"ProcessTask","details":{"task":"SendClues"}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("ClueUse"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgress("ClueObsession"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("ClueSend"));
    }

    [Fact]
    public async Task Callback_InfrastConfirmButton_ShouldAttachThumbnailWithoutAddingPlaceholderLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "base-a")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Infrast","task_index":0,"run_id":"run-g2-infrast-thumb"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            """{"task_chain":"Infrast","task_index":0,"what":"InfrastConfirmButton","details":{}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.Bridge.GetImageCallCount > 0));

        var card = Assert.Single(vm.LogCards);
        Assert.Single(card.Items);
        Assert.Equal("开始任务: base-a", card.PrimaryContent);
    }

    [Fact]
    public async Task Callback_UnmappedSubTaskStart_ShouldNotAppendGenericUserLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskStart",
            """{"task_chain":"Fight","task_index":0,"sub_task":"ProcessTask","details":{"task":"NotMappedInWpf"}}""",
            DateTimeOffset.UtcNow));

        Assert.Empty(vm.LogCards);
    }

    [Fact]
    public async Task UiLogService_NonDownloadMessages_ShouldNotLeakIntoTaskQueueLogCards()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        fixture.Runtime.LogService.Info("internal config trace");
        Dispatcher.UIThread.RunJobs(null);
        Assert.Empty(vm.LogCards);

        fixture.Runtime.LogService.Info("download 1/3");
        Dispatcher.UIThread.RunJobs(null);
        Assert.Empty(vm.LogCards);
        Assert.Contains("download", vm.DownloadLogEntry.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_TaskId_ShouldMapToCorrectQueueIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_id":2,"run_id":"run-g2-task-id"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
        Assert.Equal(1, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("resolveSource=task_id_map", eventLog);
    }

    [Fact]
    public async Task Callback_TaskIndexAndTaskIdConflict_ShouldPreferTaskIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_index":0,"task_id":2,"run_id":"run-g2-conflict"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[1].Status);
        Assert.Equal(0, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("resolveSource=task_index", eventLog);
    }

    [Fact]
    public async Task Callback_NoIndexWithDuplicateChain_ShouldUseHeuristicAndLogWarning()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;
        vm.Tasks[1].Status = TaskQueueItemStatus.Idle;

        var callback = new CoreCallbackEvent(
            20001,
            "SubTaskStart",
            """{"task_chain":"Fight","sub_task":"Stage","run_id":"run-g2-heuristic"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
        Assert.Equal(1, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("TaskQueue.Callback.ResolveTask", eventLog);
        Assert.Contains("resolveSource=chain_heuristic", eventLog);
    }

    [Fact]
    public async Task Callback_TaskChainError_ShouldSetErrorStatusAndSnapshot()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var callback = new CoreCallbackEvent(
            10000,
            "TaskChainError",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Error, vm.Tasks[0].Status);
        Assert.NotNull(vm.LastRuntimeStatus);
        Assert.Equal("TaskChainError", vm.LastRuntimeStatus!.Action);
        Assert.Equal(TaskQueueItemStatus.Error, vm.LastRuntimeStatus.Status);
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_WithUseNotifyEnabled_ShouldSendSystemNotificationOncePerRunId()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.UseNotify] = JsonValue.Create("True");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;
        MapCoreTaskId(fixture.Runtime.SessionService, 3, 0);

        var callback = new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-notify","finished_tasks":[3]}""",
            DateTimeOffset.UtcNow);

        await InvokeCallbackAsync(vm, callback);
        await InvokeCallbackAsync(vm, callback);

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.Equal(1, fixture.NotificationTracker.NotificationCallCount);
        Assert.False(string.IsNullOrWhiteSpace(fixture.NotificationTracker.LastTitle));
        Assert.True(string.IsNullOrEmpty(fixture.NotificationTracker.LastMessage));
    }

    [Fact]
    public async Task Callback_TaskChainError_WithUseNotifyDisabled_ShouldUseInAppFallback()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.UseNotify] = JsonValue.Create("False");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10000,
            "TaskChainError",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-no-notify"}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.False(fixture.NotificationTracker.LastNotification?.UseSystemNotification);
    }

    [Fact]
    public async Task CallbackPayloadMalformed_ShouldWarnAndContinue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            "{not-json}",
            DateTimeOffset.UtcNow));

        Assert.Contains(
            fixture.Runtime.LogService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                && log.Message.Contains("TaskQueue callback payload parse failed", StringComparison.Ordinal));

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-malformed"}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("TaskQueue.Callback.Parse", eventLog);
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_ShouldExecutePostActionOncePerRunId()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;
        vm.Tasks[1].Status = TaskQueueItemStatus.Error;
        MapCoreTaskId(fixture.Runtime.SessionService, 3, 0);

        var callback = new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2","finished_tasks":[3]}""",
            DateTimeOffset.UtcNow);

        await InvokeCallbackAsync(vm, callback);
        await InvokeCallbackAsync(vm, callback);

        Assert.All(vm.Tasks, task => Assert.Equal(TaskQueueItemStatus.Idle, task.Status));
        Assert.Equal(1, fixture.PostAction.ExecuteCount);
    }

    [Fact]
    public async Task Callback_TaskChainStopped_ShouldResetEveryTaskStatusToIdle()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Success;
        vm.Tasks[1].Status = TaskQueueItemStatus.Error;

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10005,
            "TaskChainStopped",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-stopped"}""",
            DateTimeOffset.UtcNow));

        Assert.All(vm.Tasks, task => Assert.Equal(TaskQueueItemStatus.Idle, task.Status));
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_ForPostActionCloseDown_ShouldNotRepeatCompletionLogOrPostAction()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"finished_tasks":[1],"run_id":"run-g2-main"}""",
            DateTimeOffset.UtcNow));

        var completionLogText = vm.RootTexts.GetOrDefault("AllTasksComplete", "All task(s) completed!\n(in {0})")
            .Split('{')[0]
            .Trim();
        var completionLogCount = CountTaskQueueLogs(vm, completionLogText);
        Assert.Equal(1, completionLogCount);
        Assert.Equal(1, fixture.PostAction.ExecuteCount);

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10000,
            "TaskChainStart",
            """{"task_chain":"CloseDown","task_id":999,"run_id":"run-g2-close-down"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"finished_tasks":[999],"run_id":"run-g2-close-down"}""",
            DateTimeOffset.UtcNow));

        completionLogCount = CountTaskQueueLogs(vm, completionLogText);
        Assert.Equal(1, completionLogCount);
        Assert.Equal(1, fixture.PostAction.ExecuteCount);
        Assert.Equal(0, CountTaskQueueLogs(vm, "CloseDown"));
    }

    [Fact]
    public async Task Callback_CloseDown_ShouldNotProjectTaskQueueStateOrEndOwnership()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-close-down-state"}""",
            DateTimeOffset.UtcNow));
        vm.Tasks[0].Status = TaskQueueItemStatus.Error;

        var runtimeStatusBefore = vm.LastRuntimeStatus;
        var logCardCountBefore = vm.LogCards.Count;
        var logItemCountBefore = vm.LogCards.Sum(card => card.Items.Count);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("TaskQueue", out _));

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "TaskChainStart",
            """{"task_chain":"CloseDown","task_id":999,"run_id":"close-down"}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(TaskQueueItemStatus.Error, vm.Tasks[0].Status);
        Assert.Equal(runtimeStatusBefore, vm.LastRuntimeStatus);
        Assert.Equal(logCardCountBefore, vm.LogCards.Count);
        Assert.Equal(logItemCountBefore, vm.LogCards.Sum(card => card.Items.Count));
        Assert.True(fixture.Runtime.SessionService.IsRunOwner("TaskQueue"));
    }

    [Fact]
    public async Task StopAsync_ManualStop_ShouldClearAllTaskStatuses()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);

        vm.Tasks[0].Status = TaskQueueItemStatus.Success;
        vm.Tasks[1].Status = TaskQueueItemStatus.Error;

        await vm.StopAsync();

        Assert.All(vm.Tasks, task => Assert.Equal(TaskQueueItemStatus.Idle, task.Status));
    }

    [Fact]
    public void DailyStageHint_ShouldFollowWpfStyleAndRenderDepotCounts()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"3231\":8,\"3261\":7,\"3232\":5,\"3262\":4}"}""");

        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            config,
            new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("CE-6: 龙门币", hint);
        Assert.Contains("AP-5: 红票", hint);
        Assert.Contains("LS-6: 经验", hint);
        Assert.Contains("PR-A-1/2: 奶&盾芯片", hint);
        Assert.Contains("(库存 8 & 7 / 5 & 4)", hint);
        Assert.DoesNotContain("今日资源关卡：", hint);
        Assert.DoesNotContain("周一了", hint);
    }

    [Fact]
    public void DailyStageHint_ShouldUseWpfPlaceholdersWhenInventoryIsIncomplete()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"3231\":8}"}""");

        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            config,
            new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("PR-A-1/2: 奶&盾芯片", hint);
        Assert.Contains("(库存 8 & -1 / -1 & -1)", hint);
    }

    [Fact]
    public void EmulatorStartResolution_Reconnect_DoesNotRequireAutoLaunchEnabled()
    {
        // WPF parity (TaskQueueViewModel.ConnectToEmulator -> StartSettings.TryToStartEmulator):
        // the reconnect path only requires a non-empty existing EmulatorPath and must not be
        // gated by OpenEmulatorAfterLaunch (StartEmulator).
        var emulatorPath = NewExistingEmulatorPath();
        var config = BuildEmulatorStartConfig(startEmulator: "False", emulatorPath);

        var resolved = TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            config,
            requireAutoLaunchEnabled: false,
            out var resolvedPath,
            out var arguments,
            out var waitSeconds,
            out _);

        Assert.True(resolved);
        Assert.Equal(emulatorPath, resolvedPath);
        Assert.Equal("--emu-arg", arguments);
        Assert.Equal(5, waitSeconds);
    }

    [Fact]
    public void EmulatorStartResolution_RejectsEmptyOrMissingPath()
    {
        var emptyPathConfig = BuildEmulatorStartConfig(startEmulator: "True", string.Empty);
        Assert.False(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            emptyPathConfig,
            requireAutoLaunchEnabled: false,
            out _,
            out _,
            out _,
            out _));

        var missingPathConfig = BuildEmulatorStartConfig(
            startEmulator: "True",
            Path.Combine(Path.GetTempPath(), "maa-missing-emulator-" + Guid.NewGuid().ToString("N") + ".exe"));
        Assert.False(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            missingPathConfig,
            requireAutoLaunchEnabled: false,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void EmulatorStartResolution_Startup_RequiresAutoLaunchEnabled()
    {
        var emulatorPath = NewExistingEmulatorPath();
        var disabledConfig = BuildEmulatorStartConfig(startEmulator: "False", emulatorPath);

        Assert.False(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            disabledConfig,
            requireAutoLaunchEnabled: true,
            out _,
            out _,
            out _,
            out _));

        var enabledConfig = BuildEmulatorStartConfig(startEmulator: "True", emulatorPath);
        Assert.True(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            enabledConfig,
            requireAutoLaunchEnabled: true,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void EmulatorStartResolution_WaitSecondsHasNoUpperClamp()
    {
        // WPF parity: EmulatorWaitSeconds is free-form text in WPF with no upper bound.
        var emulatorPath = NewExistingEmulatorPath();
        var config = BuildEmulatorStartConfig(startEmulator: "True", emulatorPath, waitSeconds: "3600");

        Assert.True(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            config,
            requireAutoLaunchEnabled: true,
            out _,
            out _,
            out var waitSeconds,
            out _));
        Assert.Equal(3600, waitSeconds);

        var negativeConfig = BuildEmulatorStartConfig(startEmulator: "True", emulatorPath, waitSeconds: "-5");
        Assert.True(TaskQueuePageViewModel.TryResolveEmulatorStartRequest(
            negativeConfig,
            requireAutoLaunchEnabled: true,
            out _,
            out _,
            out var negativeWaitSeconds,
            out _));
        Assert.Equal(0, negativeWaitSeconds);
    }

    [Fact]
    public async Task StopAsync_ShouldAppendStoppingLogLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);

        await vm.StopAsync();

        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        Assert.Contains(entries, entry => string.Equals(entry.Content, "正在停止……", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_TaskChainStopped_ShouldAppendStoppedLogLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10005,
            "TaskChainStopped",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-stopped-log"}""",
            DateTimeOffset.UtcNow));

        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        Assert.Contains(entries, entry => string.Equals(entry.Content, "已停止", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_PenguinUploadFailure_ShouldWarnOnlyForNonAnnihilation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-anni")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-normal")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto { Stage = "Annihilation" })).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(1, new FightTaskParamsDto { Stage = "1-7" })).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10010,
            "SubTaskError",
            """{"task_chain":"Fight","task_index":0,"subtask":"ReportToPenguinStats","details":{"why":"upload failed"}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10011,
            "SubTaskError",
            """{"task_chain":"Fight","task_index":1,"subtask":"ReportToPenguinStats","why":"upload failed"}""",
            DateTimeOffset.UtcNow));

        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        var annihilation = Assert.Single(entries, entry => entry.Content.Contains("AnnihilationStage", StringComparison.Ordinal));
        Assert.Equal("INFO", annihilation.Level);
        Assert.False(annihilation.IsWarning);
        var normal = Assert.Single(entries, entry => entry.Content.Contains("upload failed", StringComparison.Ordinal));
        Assert.Equal("WARN", normal.Level);
    }

    [Fact]
    public async Task Callback_FightTimes_TimesLimitDisabled_ShouldNotWarnLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-nolimit")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "1-7",
            EnableTimesLimit = false,
            Times = 5,
        })).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // WPF AsstProxy FightTimes gates the unused-times warning on
        // HasTimesLimited != false; with the limit off nothing is logged even
        // though the compiled params carry times=int.MaxValue.
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10020,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"FightTimes","details":{"finished":true,"times_finished":3,"series":1,"sanity_cost":-1}}""",
            DateTimeOffset.UtcNow));

        Assert.DoesNotContain(
            vm.LogCards.SelectMany(card => card.Items),
            entry => entry.Content.Contains("battles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_FightTimes_TimesLimitEnabled_ShouldWarnLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-limit")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "1-7",
            EnableTimesLimit = true,
            Times = 5,
        })).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10021,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"FightTimes","details":{"finished":true,"times_finished":3,"series":1,"sanity_cost":-1}}""",
            DateTimeOffset.UtcNow));

        var entry = Assert.Single(vm.LogCards.SelectMany(card => card.Items));
        Assert.Equal("WARN", entry.Level);
        Assert.True(entry.IsWarning);
        Assert.Contains("Completed 3 battles", entry.Content, StringComparison.Ordinal);
        Assert.Contains("exceeds 5 limit", entry.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_UseMedicine_ShouldTrackExpiringSeparatelyAndUseInfoColorLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-med")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10022,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"UseMedicine","details":{"is_expiring":true,"count":2}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10023,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"UseMedicine","details":{"is_expiring":false,"count":1}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10024,
            "SubTaskStart",
            """{"task_chain":"Fight","task_index":0,"sub_task":"ProcessTask","details":{"task":"StartButton2","exec_times":1}}""",
            DateTimeOffset.UtcNow));

        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        // WPF (AsstProxy UseMedicine): expiring uses only increment
        // ExpiringMedicineUsedTimes, and both variants log with UiLogColor.Info.
        var expiring = Assert.Single(entries, entry => entry.Content.Contains("Expiring medicine used", StringComparison.Ordinal));
        Assert.Equal("Expiring medicine used 2(+2)", expiring.Content);
        Assert.Equal("InfoLogBrush", expiring.Color);
        var regular = Assert.Single(
            entries,
            entry => entry.Content.Contains("Medicine used", StringComparison.Ordinal)
                && !entry.Content.Contains("Expiring", StringComparison.Ordinal));
        Assert.Equal("Medicine used 1(+1)", regular.Content);
        Assert.Equal("InfoLogBrush", regular.Color);

        // The mission-start summary must not double-count expiring medicine
        // (WPF MedicineUsedTimesWithExpiring: {0}=regular only, {1}=expiring).
        var missionStart = Assert.Single(entries, entry => entry.Content.Contains("Mission started", StringComparison.Ordinal));
        Assert.Contains("Medicine: 1,2(Expiring)", missionStart.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_SubTaskError_ShouldKeepTaskStatusAndOnlyLogLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-suberr")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10025,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-subtask-error"}""",
            DateTimeOffset.UtcNow));
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);

        // WPF ProcSubTaskError (AsstProxy.cs:1394+) only logs per-subtask error
        // text; it never changes task status or run state.
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10026,
            "SubTaskError",
            """{"task_chain":"Fight","task_index":0,"subtask":"RecognizeDrops","details":{}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        Assert.Contains(
            entries,
            entry => entry.Content.Contains("Drops recognition error", StringComparison.Ordinal)
                && entry.Level == "ERROR");
    }

    [Fact]
    public async Task Callback_ReclamationProcedureStart_MissingTimes_ShouldRenderMinusOneLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "ra-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // WPF AsstProxy ReclamationProcedureStart: subTaskDetails["times"] ?? -1.
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10027,
            "SubTaskExtraInfo",
            """{"task_chain":"Reclamation","task_index":0,"what":"ReclamationProcedureStart","details":{}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("Mission started -1 times", card.PrimaryContent);
    }

    [Fact]
    public async Task Callback_ReclamationSmeltGold_ShouldLogLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "ra-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10012,
            "SubTaskExtraInfo",
            """{"task_chain":"Reclamation","task_index":0,"what":"ReclamationSmeltGold","details":{"times":3}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("锻造赤金 3 次", card.PrimaryContent);
    }

    [Fact]
    public async Task Callback_RoguelikeNodeLogs_ShouldCarryWpfColorMetadata()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10013,
            "SubTaskStart",
            """{"task_chain":"Roguelike","task_index":0,"sub_task":"ProcessTask","details":{"task":"StageTrader","exec_times":1}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10014,
            "SubTaskStart",
            """{"task_chain":"Roguelike","task_index":0,"sub_task":"ProcessTask","details":{"task":"GamePass","exec_times":1}}""",
            DateTimeOffset.UtcNow));

        var entries = vm.LogCards.SelectMany(card => card.Items).ToArray();
        var trader = Assert.Single(entries, entry => string.Equals(entry.Color, "TraderIS", StringComparison.Ordinal));
        Assert.True(trader.IsTraderISColor);
        var gamePass = Assert.Single(entries, entry => string.Equals(entry.Color, "RareOperatorLogBrush", StringComparison.Ordinal));
        Assert.True(gamePass.IsRareOperatorColor);
    }

    [Fact]
    public async Task Callback_RecruitTagsDetected_EmptyTags_ShouldFallbackToErrorLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10015,
            "SubTaskExtraInfo",
            """{"task_chain":"Recruit","task_index":0,"what":"RecruitTagsDetected","details":{"tags":[]}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Contains("公招识别结果", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("错误", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_RecruitSupportOperator_ShouldUseLocalizedTemplateAndInfoColor()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10016,
            "SubTaskExtraInfo",
            """{"task_chain":"Recruit","task_index":0,"what":"RecruitSupportOperator","details":{"name":"能天使"}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("编入助战干员：能天使", card.PrimaryContent);
        Assert.Equal("InfoLogBrush", Assert.Single(card.Items).Color);
    }

    [Fact]
    public async Task Callback_EnterFacility_MissingIndex_ShouldRenderMinusOneLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "base-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10017,
            "SubTaskExtraInfo",
            """{"task_chain":"Infrast","task_index":0,"what":"EnterFacility","details":{"facility":"Mfg"}}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("当前设施: 制造站 -01", card.PrimaryContent);
    }

    [Fact]
    public async Task Callback_SanityBeforeStage_NonPositiveMax_ShouldClearSanityStateLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-sanity")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10018,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"SanityBeforeStage","details":{"current_sanity":10,"max_sanity":0,"report_time":"2026-04-03 12:00:00.000"}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10019,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-sanity-gate"}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.DoesNotContain("理智", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_TaskChainCompleted_ShouldUseDefaultLevelWithoutScreenshotLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-done")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10020,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-complete-plain"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10021,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-complete-plain"}""",
            DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs(null);

        var card = Assert.Single(vm.LogCards);
        var completed = card.Items[^1];
        Assert.Contains("完成任务", completed.Content, StringComparison.Ordinal);
        Assert.Equal("INFO", completed.Level);
        Assert.False(completed.IsSuccess);
        Assert.Equal(0, fixture.Bridge.GetImageCallCount);
    }

    [Fact]
    public async Task Callback_CombatRecordRecognition_ShouldNotLogIntoTaskQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Copilot", "copilot-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10022,
            "SubTaskStart",
            """{"task_chain":"Copilot","task_index":0,"sub_task":"CombatRecordRecognitionTask","what":"识别完成"}""",
            DateTimeOffset.UtcNow));

        Assert.Empty(vm.LogCards);
    }

    [Fact]
    public async Task TaskChainStoppedCallback_WithStaleRunLease_ShouldNotReleaseNewRunOwner()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        var firstRunLease = GetActiveRunLease(vm);
        Assert.True(firstRunLease.HasValue);

        // Stop run #1: the core stop succeeds and releases the owner from the stop path,
        // while a TaskChainStopped callback for run #1 can still be queued behind the UI
        // dispatcher (the callback pump does not block the stop continuation).
        await vm.StopAsync();
        Assert.NotEqual(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        // A new run starts immediately (e.g. a scheduled start) and acquires a fresh lease.
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        var secondRunLease = GetActiveRunLease(vm);
        Assert.True(secondRunLease.HasValue);
        Assert.NotEqual(firstRunLease.Value.Generation, secondRunLease.Value.Generation);

        // The delayed TaskChainStopped of run #1 carries its dispatch-time (stale) lease;
        // it must not release the new run's owner mid-run.
        await InvokeCallbackCoreAsync(vm, new CoreCallbackEvent(
            10011,
            "TaskChainStopped",
            """{"task_chain":"StartUp","task_index":0}""",
            DateTimeOffset.UtcNow), firstRunLease);

        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.IsRunning);

        await vm.StopAsync();
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StopAsync_ForceFlagRaisedAfterRunOwnerReleased_ShouldNotBypassNextRunDelayedStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue-a")).Success);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.Values[LegacyConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete] = JsonValue.Create(true);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);

        // Enter roguelike combat so StopAsync takes the delayed-stop wait.
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10021,
            "SubTaskExtraInfo",
            """{"task_chain":"Roguelike","what":"StageInfo"}""",
            DateTimeOffset.UtcNow));

        var firstStop = vm.StopAsync();
        await Task.Delay(500);
        Assert.False(firstStop.IsCompleted);
        Assert.Equal(0, fixture.Bridge.StopCallCount);

        // The run ends while stop #1 is still waiting: the core stops and the completion
        // callback releases the run owner before the waiting stop notices (1s poll).
        Assert.True((await fixture.Runtime.ConnectFeatureService.StopAsync()).Success);
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10022,
            "AllTasksCompleted",
            """{"task_chain":"Roguelike","task_index":0}""",
            DateTimeOffset.UtcNow));
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        // A stop entry (e.g. a confirmed run-owner dialog) arrives inside the window: the
        // delayed stop is still flagged as waiting, so it raises the force flag, but the
        // run owner is already gone and no ownership completion clears the flag again.
        var secondStop = vm.StopAsync();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsRunning);

        // The next run must not inherit the stale force flag: its first stop honors the
        // configured delayed stop again instead of bypassing it once.
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("TaskQueue", fixture.Runtime.SessionService.CurrentRunOwner);
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10023,
            "SubTaskExtraInfo",
            """{"task_chain":"Roguelike","what":"StageInfo"}""",
            DateTimeOffset.UtcNow));

        var thirdStop = vm.StopAsync();
        await Task.Delay(500);
        Assert.False(thirdStop.IsCompleted);
        Assert.Equal(1, fixture.Bridge.StopCallCount);

        // Cleanup: force the delayed stop to finish so the fixture stops cleanly.
        var fourthStop = vm.StopAsync();
        await Task.WhenAll(thirdStop, fourthStop).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, fixture.Bridge.StopCallCount);
        Assert.False(vm.IsRunning);
    }

    private static Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
        => InvokeCallbackCoreAsync(vm, callback, GetActiveRunLease(vm));

    private static UnifiedConfig BuildEmulatorStartConfig(
        string startEmulator,
        string emulatorPath,
        string waitSeconds = "5")
    {
        var config = new UnifiedConfig();
        var profile = config.Profiles[config.CurrentProfile];
        profile.Values[LegacyConfigurationKeys.StartEmulator] = JsonValue.Create(startEmulator);
        profile.Values[LegacyConfigurationKeys.EmulatorPath] = JsonValue.Create(emulatorPath);
        profile.Values[LegacyConfigurationKeys.EmulatorAddCommand] = JsonValue.Create(" --emu-arg ");
        profile.Values[LegacyConfigurationKeys.EmulatorWaitSeconds] = JsonValue.Create(waitSeconds);
        return config;
    }

    private static string NewExistingEmulatorPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "maa-emulator-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "stub");
        return path;
    }

    private static async Task InvokeCallbackCoreAsync(
        TaskQueuePageViewModel vm,
        CoreCallbackEvent callback,
        RunOwnershipLease? callbackRunLease)
    {
        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, [callback, callbackRunLease]) as Task;
        if (task is null)
        {
            throw new InvalidOperationException("HandleCallbackCoreAsync invocation returned null.");
        }

        await task;
    }

    private static RunOwnershipLease? GetActiveRunLease(TaskQueuePageViewModel vm)
    {
        var field = typeof(TaskQueuePageViewModel).GetField(
            "_activeRunLease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var snapshot = field!.GetValue(vm);
        if (snapshot is null)
        {
            return null;
        }

        var leaseProperty = snapshot.GetType().GetProperty("Lease", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(leaseProperty);
        return (RunOwnershipLease)leaseProperty!.GetValue(snapshot)!;
    }

    private static void MapCoreTaskId(UnifiedSessionService sessionService, int taskId, int taskIndex)
    {
        var method = typeof(UnifiedSessionService).GetMethod(
            "SetTaskIdMapping",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(sessionService, [taskId, taskIndex]);
    }

    private static async Task<string> ReadEventLogAsync(string root)
    {
        var path = Path.Combine(root, "debug", "avalonia-ui-events.log");
        for (var i = 0; i < 20; i++)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }

            await Task.Delay(10);
        }

        return string.Empty;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, int retry = 60, int delayMs = 20)
    {
        for (var i = 0; i < retry; i++)
        {
            Dispatcher.UIThread.RunJobs(null);
            if (predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static int CountTaskQueueLogs(TaskQueuePageViewModel vm, string content)
    {
        return vm.LogCards
            .SelectMany(card => card.Items)
            .Count(log => log.Content.Contains(content, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TaskQueuePage_RunOnce_OneShotRecruitExpediteSave_PersistsOriginalEnabledStates()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "Fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = "1-7",
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Recruit,
            Name = "Recruit",
            IsEnabled = false,
            Params = TaskParamCompiler.CompileRecruit(
                new RecruitTaskParamsDto
                {
                    Times = 2,
                    Refresh = true,
                    ForceRefresh = false,
                    UseExpedited = true,
                    SkipRobot = true,
                    SetTime = true,
                },
                profile,
                fixture.Runtime.ConfigurationService.CurrentConfig).Params,
        });
        await fixture.Runtime.ConfigurationService.SaveAsync();
        Assert.True((await fixture.Runtime.SessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            ensureCoreReadyForExecutionAsync: _ => Task.FromResult(true));
        await vm.InitializeAsync();
        Assert.Equal(2, vm.Tasks.Count);
        vm.SelectedTask = vm.Tasks[1];

        await vm.RunSelectedTaskOnceAsync();

        // In-memory enable states are always restored after the run-once window.
        Assert.True(profile.TaskQueue[0].IsEnabled);
        Assert.False(profile.TaskQueue[1].IsEnabled);
        // The one-shot expedite was consumed in memory by the appended batch.
        Assert.False(profile.TaskQueue[1].Params["expedite"]?.GetValue<bool>());

        // The append transaction saved mid-window while the run-once enable states were
        // forced; the persisted file must still carry the original enable states (and
        // keeps the existing contract of stripping the consumed one-shot expedite).
        var persisted = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            fixture.Root);
        await persisted.LoadOrBootstrapAsync();
        Assert.True(persisted.TryGetCurrentProfile(out var persistedProfile));
        Assert.Equal(2, persistedProfile.TaskQueue.Count);
        Assert.True(persistedProfile.TaskQueue[0].IsEnabled);
        Assert.False(persistedProfile.TaskQueue[1].IsEnabled);
        Assert.False(persistedProfile.TaskQueue[1].Params.ContainsKey("expedite"));
        Assert.False(persistedProfile.TaskQueue[1].Params.ContainsKey("expedite_times"));
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            MAAUnifiedRuntime runtime,
            TaskQueueFeatureService taskQueue,
            CapturingBridge bridge,
            CountingPostActionFeatureService postAction,
            NotificationTrackingPlatformCapabilityService notificationTracker,
            string readyAdbPath)
        {
            Root = root;
            Runtime = runtime;
            TaskQueue = taskQueue;
            Bridge = bridge;
            PostAction = postAction;
            NotificationTracker = notificationTracker;
            ReadyAdbPath = readyAdbPath;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public CapturingBridge Bridge { get; }

        public CountingPostActionFeatureService PostAction { get; }

        public NotificationTrackingPlatformCapabilityService NotificationTracker { get; }

        public string ReadyAdbPath { get; }

        public static async Task<TestFixture> CreateAsync(string language = "zh-cn")
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();
            config.CurrentConfig.GlobalValues["GUI.Localization"] = language;
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }
            var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "taskqueue-g2-ready");

            var bridge = new CapturingBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);

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
            var notificationTracker = new NotificationTrackingPlatformCapabilityService(capability);
            var connect = new ConnectFeatureService(session, config, log, bridge, root);
            var postAction = new CountingPostActionFeatureService();
            var achievementTracker = new AchievementTrackerService(config, root);

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
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = notificationTracker,
                OverlayFeatureService = new OverlayFeatureService(notificationTracker),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, notificationTracker, diagnostics),
                AchievementTrackerService = achievementTracker,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postAction,
            };

            return new TestFixture(root, runtime, taskQueue, bridge, postAction, notificationTracker, readyAdbPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Bridge.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // keep temporary folder for inspection when cleanup fails.
            }
        }
    }

    private sealed class CapturingBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;
        private bool _connected;
        private bool _running;

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public int GetImageCallCount { get; private set; }

        public TimeSpan ConnectDelay { get; set; }

        public bool ForceConnectFailure { get; set; }

        public bool ForceStartFailure { get; set; }

        public bool ForceStopFailure { get; set; }

        public string? StopFailureMessage { get; set; }

        public int? ThrowAppendCallNumber { get; set; }

        public int? FailAppendCallNumber { get; set; }

        public int? BlockAppendCallNumber { get; set; }

        public TaskCompletionSource<bool>? AppendBlocked { get; set; }

        public TaskCompletionSource<bool>? AppendRelease { get; set; }

        public TaskCompletionSource<bool>? StartStarted { get; set; }

        public TaskCompletionSource<bool>? StartRelease { get; set; }

        public bool IgnoreStartCancellation { get; set; }

        public TaskCompletionSource<bool>? ConnectStarted { get; set; }

        public TaskCompletionSource<bool>? ConnectRelease { get; set; }

        public byte[]? NextImageBytes { get; set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectStarted?.TrySetResult(true);
            if (ConnectDelay > TimeSpan.Zero)
            {
                await Task.Delay(ConnectDelay, cancellationToken);
            }

            if (ConnectRelease is not null)
            {
                await ConnectRelease.Task.WaitAsync(cancellationToken);
            }

            _connected = !ForceConnectFailure && !string.IsNullOrWhiteSpace(connectionInfo.Address);
            return _connected
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed"));
        }

        public async Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            var taskId = Interlocked.Increment(ref _taskId);
            if (BlockAppendCallNumber == taskId)
            {
                AppendBlocked?.TrySetResult(true);
                if (AppendRelease is not null)
                {
                    await AppendRelease.Task.WaitAsync(cancellationToken);
                }
            }

            if (ThrowAppendCallNumber == taskId)
            {
                throw new InvalidOperationException($"append call {taskId} exploded");
            }

            if (FailAppendCallNumber == taskId)
            {
                return CoreResult<int>.Fail(new CoreError(
                    CoreErrorCode.AppendTaskFailed,
                    $"append call {taskId} failed"));
            }

            AppendedTasks.Add(task);
            return CoreResult<int>.Ok(taskId);
        }

        public async Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            StartStarted?.TrySetResult(true);
            if (StartRelease is not null)
            {
                if (IgnoreStartCancellation)
                {
                    await StartRelease.Task;
                }
                else
                {
                    await StartRelease.Task.WaitAsync(cancellationToken);
                }
            }

            if (ForceStartFailure || !_connected)
            {
                return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "start failed"));
            }

            _running = true;
            return CoreResult<bool>.Ok(true);
        }

        public async Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            if (StartRelease is not null && StartStarted?.Task.IsCompleted == true)
            {
                await StartRelease.Task.WaitAsync(cancellationToken);
            }

            if (ForceStopFailure)
            {
                return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, StopFailureMessage ?? "forced stop failure"));
            }

            var hadQueuedWork = _running || AppendedTasks.Count > 0;
            _running = false;
            AppendedTasks.Clear();
            return hadQueuedWork
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "not running"));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            GetImageCallCount++;
            return Task.FromResult(NextImageBytes is { Length: > 0 } bytes
                ? CoreResult<byte[]>.Ok(bytes.ToArray())
                : CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public void Publish(CoreCallbackEvent callback)
        {
            _channel.Writer.TryWrite(callback);
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RunOwnerDialogService : IAppDialogService
    {
        private readonly IAppDialogService _inner = NoOpAppDialogService.Instance;

        public int WarningConfirmCount { get; private set; }

        public DialogReturnSemantic WarningConfirmReturn { get; set; } = DialogReturnSemantic.Close;

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowAnnouncementAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowVersionUpdateAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowProcessPickerAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowEmulatorPathAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => _inner.ShowErrorAsync(request, sourceScope, openIssueReportAsync, openSettingsAsync, cancellationToken);

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowAchievementListAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => _inner.ShowTextAsync(request, sourceScope, cancellationToken);

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCount++;
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                WarningConfirmReturn,
                null,
                "test-dialog"));
        }
    }

    private sealed class CountingPostActionFeatureService : IPostActionFeatureService
    {
        public int ExecuteCount { get; private set; }

        public TaskCompletionSource<bool>? ExecutionStarted { get; set; }

        public TaskCompletionSource<bool>? ExecutionRelease { get; set; }

        public Task<UiOperationResult<PostActionConfig>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionConfig>.Ok(new PostActionConfig(), "Loaded."));

        public Task<UiOperationResult> SaveAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Saved."));

        public Task<UiOperationResult<PostActionPreview>> GetCapabilityPreviewAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Previewed."));

        public Task<UiOperationResult<PostActionPreview>> ValidateSelectionAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Validated."));

        public async Task<UiOperationResult> ExecuteAfterCompletionAsync(
            PostActionExecutionContext context,
            PostActionConfig? configOverride = null,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            ExecutionStarted?.TrySetResult(true);
            if (ExecutionRelease is not null)
            {
                await ExecutionRelease.Task.WaitAsync(cancellationToken);
            }

            return UiOperationResult.Ok("Post action executed.");
        }
    }
}
