using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class TimerScheduleAK3FeatureTests
{
    [Fact]
    public async Task Timer_Schedule_Idle_TriggersStart()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var scheduledAt = BuildScheduleTime(6, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: false, profile: "Default");

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
    }

    [Fact]
    public async Task Timer_Schedule_RunningAndNoForce_Skips()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(7, 15);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: false, profile: "Default");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.Skip"));
    }

    [Fact]
    public async Task Timer_Schedule_RunningAndForce_StopThenStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(8, 20);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(2, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.StopAndStart"));
    }

    [Fact]
    public async Task Timer_Schedule_ActiveOperationWithoutForce_Skips()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var scheduledAt = BuildScheduleTime(8, 25);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");
        using var activityLease = fixture.Shell.ConnectionGameSharedState.BeginSessionOperation("Timer.Test.ActiveOperation");

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "reason=session-active-without-force"));
    }

    [Fact]
    public async Task Timer_Schedule_ExclusiveProfileSwitch_ShouldRejectLateTaskQueueStart()
    {
        var switchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await TestFixture.CreateAsync(
            preconnect: false,
            beforeProfileSwitch: async cancellationToken =>
            {
                switchEntered.TrySetResult(true);
                await releaseSwitch.Task.WaitAsync(cancellationToken);
            });
        try
        {
            var scheduledAt = BuildScheduleTime(8, 27);
            // Force is required for the T-minute profile switch: without it the miss of the
            // T-2 pre-switch intentionally starts with the current profile (WPF parity).
            await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: true, profile: "Night");

            var timerTask = StartEvaluateTimerScheduleTask(fixture.Shell, scheduledAt);
            await switchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(fixture.Shell.ConnectionGameSharedState.IsSessionOperationExclusiveActive);
            Assert.True(fixture.Shell.TaskQueuePage.CanToggleRun);
            Assert.Equal(
                fixture.Shell.TaskQueuePage.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"),
                fixture.Shell.TaskQueuePage.RunButtonText);
            Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);

            await fixture.Shell.TaskQueuePage.StartAsync();

            Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
            Assert.Equal(0, fixture.Bridge.StartCallCount);
            Assert.False(fixture.Shell.TaskQueuePage.IsStartRequestActive);
            Assert.Contains(
                "定时任务",
                fixture.Shell.TaskQueuePage.LastErrorMessage,
                StringComparison.Ordinal);

            releaseSwitch.TrySetResult(true);
            await timerTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
            Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
            Assert.Equal(1, fixture.Bridge.StartCallCount);
            Assert.False(fixture.Shell.ConnectionGameSharedState.IsSessionOperationExclusiveActive);
        }
        finally
        {
            releaseSwitch.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Timer_Schedule_CentralStopDuringExclusiveSwitch_ShouldCancelScheduledStart()
    {
        var switchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await TestFixture.CreateAsync(
            preconnect: false,
            beforeProfileSwitch: async cancellationToken =>
            {
                switchEntered.TrySetResult(true);
                await releaseSwitch.Task.WaitAsync(cancellationToken);
            });
        var scheduledAt = BuildScheduleTime(8, 28);
        // Force owns the T-minute profile switch; without it a missed T-2 pre-switch
        // deliberately starts with the current profile instead of switching.
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: true, profile: "Night");

        var timerTask = StartEvaluateTimerScheduleTask(fixture.Shell, scheduledAt);
        await switchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Shell.ConnectionGameSharedState.IsTimerSessionOperationActive);
        Assert.True(fixture.Shell.TaskQueuePage.CanToggleRun);
        Assert.Equal(
            fixture.Shell.TaskQueuePage.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"),
            fixture.Shell.TaskQueuePage.RunButtonText);

        var centralStopTask = fixture.Shell.TaskQueuePage.ToggleRunAsync();
        await Task.Delay(50);
        releaseSwitch.TrySetResult(true);

        await timerTask.WaitAsync(TimeSpan.FromSeconds(10));
        await centralStopTask.WaitAsync(TimeSpan.FromSeconds(10));

        // The central stop cancels the forced scheduled start; the profile switch may be
        // aborted before or after it completes, but no run may start.
        Assert.Contains(
            fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
            new[] { "Default", "Night" });
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.False(fixture.Shell.ConnectionGameSharedState.IsSessionOperationActive);
        Assert.Equal(
            fixture.Shell.TaskQueuePage.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"),
            fixture.Shell.TaskQueuePage.RunButtonText);
    }

    [Fact]
    public async Task Timer_Schedule_CentralStopBeyondFiveSeconds_ShouldNotStartAfterBlockedSwitchReleases()
    {
        var switchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await TestFixture.CreateAsync(
            preconnect: false,
            beforeProfileSwitch: async _ =>
            {
                switchEntered.TrySetResult(true);
                await releaseSwitch.Task;
            });
        var scheduledAt = BuildScheduleTime(8, 29);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: true, profile: "Night");

        var timerTask = StartEvaluateTimerScheduleTask(fixture.Shell, scheduledAt);
        await switchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var centralStopTask = fixture.Shell.TaskQueuePage.ToggleRunAsync();
        await centralStopTask.WaitAsync(TimeSpan.FromSeconds(10));

        releaseSwitch.TrySetResult(true);
        await timerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The central stop guarantees no scheduled start is issued after the blocked switch
        // releases; the optional profile switch is allowed to complete either way.
        Assert.Contains(
            fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
            new[] { "Default", "Night" });
        Assert.Contains(
            fixture.Runtime.SessionService.CurrentState,
            new[] { SessionState.Idle, SessionState.Connected });
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.False(fixture.Shell.ConnectionGameSharedState.IsSessionOperationActive);
    }

    [Fact]
    public async Task Timer_Schedule_ConnectedWithoutForce_StartsWithCurrentProfile()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(8, 26);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");
        Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        // WPF parity: a connected-but-idle session is reused by LinkStart; without force the
        // T-minute trigger does not switch profiles.
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        // The connection was established before the trigger; LinkStart reconnects because the
        // profile-scoped connection settings differ, so exactly one native stop is expected.
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Timer.Schedule.Start"));
    }

    [Fact]
    public async Task Timer_Schedule_ConnectedAndForce_StopsThenStarts()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(8, 28);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");
        Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Timer.Schedule.StopAndStart"));
    }

    [Fact]
    public async Task Timer_Schedule_ForceWaitsForActiveOperationBeforeSwitchAndStart()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var scheduledAt = BuildScheduleTime(8, 27);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: true, profile: "Night");
        var activityLease = fixture.Shell.ConnectionGameSharedState.BeginSessionOperation("Timer.Test.BlockingOperation");

        var timerTask = InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);
        await Task.Delay(100);
        Assert.False(timerTask.IsCompleted);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);

        activityLease.Dispose();
        await timerTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Timer.Schedule.WaitForIdle"));
    }

    [Fact]
    public async Task Timer_Schedule_SameMinute_Deduplicated()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(9, 10);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
    }

    [Fact]
    public async Task Timer_Schedule_LogsScopes()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var scheduledAt = BuildScheduleTime(10, 40);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");

        // WPF parity: the configuration switch happens at the T-2 pre-switch (HandleConfigChange);
        // the T-minute trigger only starts the queue.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(await WaitForConditionAsync(() => fixture.Shell.TaskQueuePage.IsRunning, retry: 80, delayMs: 20));
        Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogCountAtLeastAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.Trigger", 1, retry: 80, delayMs: 30));
        Assert.True(await WaitForLogCountAtLeastAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.SwitchProfile", 1, retry: 80, delayMs: 30));
        Assert.True(await WaitForLogCountAtLeastAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.Start", 1, retry: 80, delayMs: 30));
    }

    [Fact]
    public async Task Timer_Schedule_CustomProfileSwitch_ShouldNotLeakPostActionState()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var defaultProfile = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles["Default"];
        var nightProfile = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles["Night"];
        defaultProfile.Values["TaskQueue.PostAction"] = new JsonObject
        {
            ["exit_emulator"] = true,
            ["commands"] = new JsonObject(),
        };
        nightProfile.Values["TaskQueue.PostAction"] = new JsonObject
        {
            ["exit_self"] = true,
            ["commands"] = new JsonObject(),
        };
        await fixture.Runtime.ConfigurationService.SaveAsync();
        await fixture.Shell.TaskQueuePage.ReloadConfigurationContextAsync();

        fixture.Shell.TaskQueuePage.PostActionModule.Shutdown = true;
        Assert.True(await fixture.Shell.TaskQueuePage.FlushConfigurationSavesForCloseAsync());
        var scheduledAt = BuildScheduleTime(10, 41);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");

        // WPF parity: the profile switch (and its configuration flush) happens at the T-2
        // pre-switch minute, not at the scheduled start minute.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));

        Assert.True(await WaitForConditionAsync(
            () => string.Equals(
                fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile,
                "Night",
                StringComparison.OrdinalIgnoreCase),
            retry: 80,
            delayMs: 20));

        var savedDefaultPostAction = PostActionConfig.FromJson(defaultProfile.Values["TaskQueue.PostAction"]);
        var savedNightPostAction = PostActionConfig.FromJson(nightProfile.Values["TaskQueue.PostAction"]);

        Assert.True(savedDefaultPostAction.Shutdown);
        Assert.False(savedNightPostAction.Shutdown);
    }

    [Fact]
    public async Task Timer_Schedule_LogsScopes_RepeatedRuns_StayStable()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var expectedSwitchProfileCount = 0;
        var expectedStartCount = 0;

        for (var i = 0; i < 3; i++)
        {
            var scheduledAt = BuildScheduleTime(12, 10 + i);
            await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");

            // WPF parity: the T-2 pre-switch performs the profile switch; T only starts.
            await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));
            await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);
            expectedSwitchProfileCount++;
            expectedStartCount++;
            Assert.True(await WaitForConditionAsync(() => fixture.Shell.TaskQueuePage.IsRunning, retry: 80, delayMs: 20));
            Assert.Equal(expectedStartCount, fixture.Bridge.StartCallCount);
            Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
            Assert.True(await WaitForLogCountAtLeastAsync(
                fixture.Runtime.DiagnosticsService.EventLogPath,
                "Timer.Schedule.SwitchProfile",
                expectedSwitchProfileCount,
                retry: 80,
                delayMs: 30));
            Assert.True(await WaitForLogCountAtLeastAsync(
                fixture.Runtime.DiagnosticsService.EventLogPath,
                "Timer.Schedule.Start",
                expectedStartCount,
                retry: 80,
                delayMs: 30));

            await fixture.Shell.StopAsync();
            fixture.MoveSessionStateForTest(SessionState.Idle);
            fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile = "Default";
            await fixture.Runtime.ConfigurationService.SaveAsync();
            await fixture.Shell.TaskQueuePage.ReloadTasksAsync();
            await fixture.Shell.TaskQueuePage.WaitForPendingBindingAsync();
        }
    }

    [Fact]
    public async Task Timer_Schedule_InvalidProfile_LogsError()
    {
        await using var fixture = await TestFixture.CreateAsync(preconnect: false);
        var scheduledAt = BuildScheduleTime(11, 11);
        var settings = fixture.Shell.SettingsPage;
        foreach (var slot in settings.Timers)
        {
            slot.Enabled = false;
            slot.Time = "07:00";
            slot.Profile = "Default";
        }

        settings.CustomTimerConfig = true;
        settings.ForceScheduledStart = false;
        settings.Timers[0].Enabled = true;
        settings.Timers[0].Time = scheduledAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        settings.Timers[0].Profile = "MissingProfile";

        // The invalid profile is rejected at the T-2 pre-switch minute.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));

        Assert.False(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "Timer.Schedule.Error"));

        // WPF parity (HandleScheduledStart): the non-forced T-minute trigger does not re-validate
        // or switch the configuration; it starts with whatever configuration is current.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
    }

    [Fact]
    public async Task Timer_Schedule_NonForce_PreSwitchMissed_StartsWithCurrentProfileWithoutSwitching()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(19, 40);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);

        // WPF parity (TaskQueueViewModel.HandleScheduledStart): without ForceScheduledStart the
        // T-minute trigger goes straight to LinkStart. If the T-2 pre-switch was missed (app not
        // running, or the user switched away inside the T-2..T window), the run starts with the
        // current configuration instead of forcing the slot configuration.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.Start"));
        var eventLog = await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.EventLogPath);
        Assert.DoesNotContain("Timer.Schedule.SwitchProfile", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_Schedule_PreSwitch_TwoMinutesBefore_SwitchesProfileWithoutStarting()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(13, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");
        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);

        // WPF parity (TaskQueueViewModel.CheckTimers): the configuration pre-switch happens
        // two minutes before the scheduled time and does not start any task.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));

        Assert.False(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.PreSwitchProfile"));

        // At the scheduled time the slot starts with the pre-switched configuration.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.True(await WaitForConditionAsync(() => fixture.Shell.TaskQueuePage.IsRunning, retry: 80, delayMs: 20));
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal("Night", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
    }

    [Fact]
    public async Task Timer_Schedule_PreSwitch_RunningWithoutForce_DoesNotSwitch()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(14, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: true, profile: "Night");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);

        // WPF parity: HandleTimerLogic returns early when running without ForceScheduledStart,
        // so the T-2 pre-switch must not run either.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-2));

        Assert.Equal("Default", fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
    }

    [Fact]
    public async Task Timer_Schedule_Force_ProfileMismatch_Skips()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(15, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: true, profile: "Night");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);

        // WPF parity: force + CustomConfig with a mismatched current configuration warns and
        // skips the scheduled start (the T-2 pre-switch is expected to have aligned it).
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "reason=profile-mismatch"));
    }

    [Fact]
    public async Task Timer_Schedule_Force_CountdownCanceled_Skips()
    {
        var dialogService = new ScriptedDialogService(DialogReturnSemantic.Cancel);
        await using var fixture = await TestFixture.CreateAsync(dialogService);
        var scheduledAt = BuildScheduleTime(16, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, fixture.Bridge.StartCallCount);

        // WPF parity (TimerCanceledAsync): the 10-second countdown can cancel the forced restart.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Equal(0, fixture.Bridge.StopCallCount);
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        Assert.Equal(1, dialogService.ForceCountdownCallCount);
        Assert.Equal(10, dialogService.ForceCountdownRequests[0].CountdownSeconds);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.Canceled"));
    }

    [Fact]
    public async Task Timer_Schedule_Force_CountdownElapsed_Proceeds()
    {
        var dialogService = new ScriptedDialogService(DialogReturnSemantic.Confirm);
        await using var fixture = await TestFixture.CreateAsync(dialogService);
        var scheduledAt = BuildScheduleTime(17, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);

        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(1, dialogService.ForceCountdownCallCount);
        Assert.Equal(2, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
    }

    [Fact]
    public async Task Timer_Schedule_Force_AppendsCloseDownBeforeRestart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var scheduledAt = BuildScheduleTime(18, 30);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: true, custom: false, profile: "Default");
        await fixture.Shell.StartAsync();
        Assert.True(fixture.Shell.TaskQueuePage.IsRunning);
        fixture.Bridge.AppendedTaskTypes.Clear();

        // WPF parity (AsstAppendCloseDown): the forced restart queues a CloseDown task so the
        // game client is closed before the new queue starts.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt);

        Assert.Equal(2, fixture.Bridge.StartCallCount);
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Equal("CloseDown", fixture.Bridge.AppendedTaskTypes[0]);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.EventLogPath, "Timer.Schedule.CloseDown"));
    }

    [Fact]
    public async Task Timer_Schedule_NotifyBeforeScheduledStart_SendsOncePerMinute()
    {
        var notification = new RecordingNotificationService();
        await using var fixture = await TestFixture.CreateAsync(preconnect: false, notificationService: notification);
        var scheduledAt = BuildScheduleTime(9, 0);
        await fixture.ConfigureSingleTimerAsync(scheduledAt, force: false, custom: false, profile: "Default");

        var settings = fixture.Shell.SettingsPage;
        settings.NotifyBeforeScheduledStart = true;
        settings.ScheduledStartNotificationMinutes = 5;
        await settings.SaveTimerSettingsAsync();
        Assert.False(settings.HasPendingTimerChanges, settings.TimerValidationMessage);

        var notificationMinute = scheduledAt.AddMinutes(-5);
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, notificationMinute.AddSeconds(10));
        await WaitForNotificationCountAsync(notification, 1);

        Assert.Contains("09:00", notification.LastMessage, StringComparison.Ordinal);
        Assert.Contains("5", notification.LastMessage, StringComparison.Ordinal);

        // The scheduler ticks more than once per minute; each slot must notify only once.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, notificationMinute.AddSeconds(40));
        await Task.Delay(100);
        Assert.Equal(1, notification.CallCount);

        // Outside the lead window no notification is sent.
        await InvokeEvaluateTimerScheduleAsync(fixture.Shell, scheduledAt.AddMinutes(-5).AddMinutes(-1));
        await Task.Delay(100);
        Assert.Equal(1, notification.CallCount);
    }

    private static DateTimeOffset BuildScheduleTime(int hour, int minute)
    {
        return new DateTimeOffset(2026, 1, 1, hour, minute, 20, TimeSpan.FromHours(8));
    }

    private static async Task InvokeEvaluateTimerScheduleAsync(MainShellViewModel shell, DateTimeOffset now)
    {
        await StartEvaluateTimerScheduleTask(shell, now);
    }

    private static Task StartEvaluateTimerScheduleTask(MainShellViewModel shell, DateTimeOffset now)
    {
        var method = typeof(MainShellViewModel).GetMethod(
            "EvaluateTimerScheduleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(shell, [now, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        return task!;
    }

    private static async Task WaitForNotificationCountAsync(
        RecordingNotificationService notification,
        int expected,
        int retry = 40,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (notification.CallCount >= expected)
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        Assert.Equal(expected, notification.CallCount);
    }

    private static async Task<bool> WaitForLogContainsAsync(string path, string expected, int retry = 20, int delayMs = 30)
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

    private static async Task<bool> WaitForLogCountAtLeastAsync(
        string path,
        string expected,
        int expectedCount,
        int retry = 20,
        int delayMs = 30)
    {
        for (var i = 0; i < retry; i++)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                var count = CountOccurrences(content, expected);
                if (count >= expectedCount)
                {
                    return true;
                }
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> predicate,
        int retry = 20,
        int delayMs = 30)
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

    private static int CountOccurrences(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf(token, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            index += token.Length;
        }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            MAAUnifiedRuntime runtime,
            MainShellViewModel shell,
            CountingBridge bridge)
        {
            Root = root;
            Runtime = runtime;
            Shell = shell;
            Bridge = bridge;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public MainShellViewModel Shell { get; }

        public CountingBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(
            IAppDialogService? dialogService = null,
            bool preconnect = true,
            Func<CancellationToken, Task>? beforeProfileSwitch = null,
            INotificationService? notificationService = null)
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
            var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "timer-ak3-ready");

            config.CurrentConfig.Profiles["Night"] = new UnifiedProfile();
            foreach (var profile in config.CurrentConfig.Profiles.Values)
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
                profile.Values["ConnectAddress"] = JsonValue.Create(TestConnectionFixtureSupport.ReadyConnectAddress);
                profile.Values["ConnectConfig"] = JsonValue.Create(TestConnectionFixtureSupport.ReadyConnectConfig);
                profile.Values["AdbPath"] = JsonValue.Create(readyAdbPath);
            }

            await config.SaveAsync();

            var bridge = new CountingBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
                NotificationService = notificationService ?? new NoOpNotificationService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config, log, bridge, root);
            var configurationProfiles = new ConfigurationProfileFeatureService(config);
            IConfigurationProfileFeatureService runtimeConfigurationProfiles = beforeProfileSwitch is null
                ? configurationProfiles
                : new BeforeSwitchConfigurationProfileFeatureService(configurationProfiles, beforeProfileSwitch);
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
                ConfigurationProfileFeatureService = runtimeConfigurationProfiles,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            config.CurrentConfig.CurrentProfile = "Default";
            Assert.True((await runtime.TaskQueueFeatureService.AddTaskAsync("StartUp", "startup-default")).Success);
            config.CurrentConfig.CurrentProfile = "Night";
            Assert.True((await runtime.TaskQueueFeatureService.AddTaskAsync("StartUp", "startup-night")).Success);
            config.CurrentConfig.CurrentProfile = "Default";
            await config.SaveAsync();

            var shell = new MainShellViewModel(
                runtime,
                dialogService ?? new ScriptedDialogService(DialogReturnSemantic.Confirm));
            await shell.InitializeAsync();
            if (preconnect)
            {
                var connectResult = await TestConnectionFixtureSupport.ConnectReadyAsync(runtime.ConnectFeatureService, readyAdbPath);
                Assert.True(connectResult.Success, connectResult.Message);
            }

            TestShellCleanup.StopTimerScheduler(shell);
            await shell.TaskQueuePage.ReloadTasksAsync();

            return new TestFixture(root, runtime, shell, bridge);
        }

        public void MoveSessionStateForTest(SessionState state)
        {
            var field = typeof(UnifiedSessionService).GetField(
                "_stateMachine",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stateMachine = Assert.IsType<SessionStateMachine>(field?.GetValue(Runtime.SessionService));
            Assert.True(stateMachine.TryMoveTo(state), $"Expected session transition to {state}.");
        }

        public async Task ConfigureSingleTimerAsync(
            DateTimeOffset scheduledAt,
            bool force,
            bool custom,
            string profile)
        {
            var settings = Shell.SettingsPage;
            foreach (var slot in settings.Timers)
            {
                slot.Enabled = false;
                slot.Time = "07:00";
                slot.Profile = "Default";
            }

            settings.ForceScheduledStart = force;
            settings.ShowWindowBeforeForceScheduledStart = false;
            settings.CustomTimerConfig = custom;

            settings.Timers[0].Enabled = true;
            settings.Timers[0].Time = scheduledAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            settings.Timers[0].Profile = profile;

            await settings.SaveTimerSettingsAsync();
            Assert.False(settings.HasPendingTimerChanges, settings.TimerValidationMessage);
        }

        public async ValueTask DisposeAsync()
        {
            TestShellCleanup.StopTimerScheduler(Shell);
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

    private sealed class RecordingNotificationService : INotificationService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "recording",
            Provider: "test");

        public int CallCount { get; private set; }

        public string LastTitle { get; private set; } = string.Empty;

        public string LastMessage { get; private set; } = string.Empty;

        public Task<PlatformOperationResult> NotifyAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default)
            => NotifyAsync(new SystemNotificationRequest(title, message), cancellationToken);

        public Task<PlatformOperationResult> NotifyAsync(
            SystemNotificationRequest notification,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTitle = notification.Title;
            LastMessage = notification.Message;
            return Task.FromResult(PlatformOperation.NativeSuccess("test", "notified", "notify"));
        }
    }

    private sealed class ScriptedDialogService : IAppDialogService
    {
        private readonly DialogReturnSemantic _forceCountdownReturn;

        public ScriptedDialogService(DialogReturnSemantic forceCountdownReturn)
        {
            _forceCountdownReturn = forceCountdownReturn;
        }

        public int ForceCountdownCallCount { get; private set; }

        public List<WarningConfirmDialogRequest> ForceCountdownRequests { get; } = [];

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            // The macOS RawByNc risk prompt maps Cancel to ForceRun; confirm it away so the
            // minitouch default does not abort fixture connections on macOS. The force-start
            // countdown prompt uses the scripted semantic under test.
            var isForceCountdown = sourceScope.StartsWith("Timer.Schedule.ForceCountdown", StringComparison.Ordinal);
            var semantic = isForceCountdown ? _forceCountdownReturn : DialogReturnSemantic.Cancel;
            if (isForceCountdown)
            {
                ForceCountdownCallCount++;
                ForceCountdownRequests.Add(request);
            }

            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                semantic,
                semantic == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "scripted"));
        }
    }

    private sealed class BeforeSwitchConfigurationProfileFeatureService : IConfigurationProfileFeatureService
    {
        private readonly IConfigurationProfileFeatureService _inner;
        private readonly Func<CancellationToken, Task> _beforeSwitch;

        public BeforeSwitchConfigurationProfileFeatureService(
            IConfigurationProfileFeatureService inner,
            Func<CancellationToken, Task> beforeSwitch)
        {
            _inner = inner;
            _beforeSwitch = beforeSwitch;
        }

        public Task<UiOperationResult<ConfigurationProfileState>> LoadStateAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.LoadStateAsync(cancellationToken);
        }

        public Task<UiOperationResult<ConfigurationProfileState>> AddProfileAsync(
            string profileName,
            string? copyFrom = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.AddProfileAsync(profileName, copyFrom, cancellationToken);
        }

        public Task<UiOperationResult<ConfigurationProfileState>> DeleteProfileAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            return _inner.DeleteProfileAsync(profileName, cancellationToken);
        }

        public Task<UiOperationResult<ConfigurationProfileState>> MoveProfileAsync(
            string profileName,
            int offset,
            CancellationToken cancellationToken = default)
        {
            return _inner.MoveProfileAsync(profileName, offset, cancellationToken);
        }

        public async Task<UiOperationResult<ConfigurationProfileState>> SwitchProfileAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            await _beforeSwitch(cancellationToken);
            return await _inner.SwitchProfileAsync(profileName, cancellationToken);
        }
    }

    private sealed class CountingBridge : IMaaCoreBridge
    {
        public bool SupportsStartCloseDown => true;

        public List<string> AppendedTaskTypes { get; } = [];

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendedTaskTypes.Add(task.Type);
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
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
}
