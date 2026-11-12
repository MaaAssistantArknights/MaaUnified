using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class SessionStateSyncTests
{
    [Fact]
    public async Task CallbackStream_DrivesSessionStateTransitions()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var configService = CreateConfigService();
        var stateMachine = new SessionStateMachine();
        var session = new UnifiedSessionService(bridge, configService, logService, stateMachine);

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.Publish(new CoreCallbackEvent(10002, "TaskChainCompleted", "{}", DateTimeOffset.UtcNow));
        await Task.Delay(50);
        Assert.Equal(SessionState.Running, session.CurrentState);

        bridge.Publish(new CoreCallbackEvent(10003, "AllTasksCompleted", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.Publish(new CoreCallbackEvent(10004, "TaskChainStopped", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Disconnect"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task StopAsync_WhenCoreDoesNotSettle_ForceCollectRecoversIdleAndIgnoresLateResult()
    {
        var stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge
        {
            BlockStop = true,
            IgnoreStopCancellation = true,
            StopCompletion = stopCompletion,
        };
        var session = new UnifiedSessionService(
            bridge,
            CreateConfigService(),
            new UiLogService(),
            new SessionStateMachine());

        var start = await session.StartAsync();
        Assert.True(start.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
        Assert.True(session.TryAcquireRun("TaskQueue", out _, out _));

        var stopTask = session.StopAsync();
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionState.Stopping, session.CurrentState);

        // WPF TaskQueueViewModel.Stop timeout branch: the caller gives up on the native stop
        // and the session is recovered to Idle with the run owner released.
        Assert.True(session.ForceCollectAfterStopTimeout("Session.Test.StopTimeout"));
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.Null(session.CurrentRunOwner);

        // A repeated force collect on the already recovered session is a no-op.
        Assert.False(session.ForceCollectAfterStopTimeout("Session.Test.StopTimeout.Again"));

        // The hung native stop settles afterwards; its result must not move the state machine.
        stopCompletion.TrySetResult(true);
        var stop = await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stop.Success);
        await Task.Delay(150);
        Assert.Equal(SessionState.Idle, session.CurrentState);

        bridge.Complete();
    }

    [Fact]
    public async Task CallbackStream_ResolutionChanged_ShouldInvalidateConnectionAndReturnToIdle()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(
            bridge,
            CreateConfigService(),
            new UiLogService(),
            new SessionStateMachine());

        var start = await session.StartAsync();
        Assert.True(start.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        // WPF dev-v2 f4d8fba923: a runtime resolution change closes the connection and stops
        // the running task instead of leaving the session ｢running｣ against a dead link.
        bridge.Publish(new CoreCallbackEvent(
            9,
            "ConnectionInfo",
            """{"what":"ResolutionChanged","details":{"width":1920,"height":1080}}""",
            DateTimeOffset.UtcNow));

        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle);
        Assert.Null(session.LastSuccessfulConnectionInfo);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_StaleTerminalCallbackFromStoppedBatch_DoesNotRegressRunningState()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        // Batch A runs and is manually stopped before its terminal callbacks are pumped.
        var startA = await session.StartAsync();
        Assert.True(startA.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var stopA = await session.StopAsync();
        Assert.True(stopA.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        // Batch A's terminal callbacks are enqueued before the next start (native emits
        // them before AsstStop returns) but the backlogged pump processes them later.
        bridge.PublishSequenced(10004, "TaskChainStopped");
        bridge.PublishSequenced(10003, "AllTasksCompleted");

        // Batch B starts while batch A's terminal callbacks are still pending.
        var startB = await session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "Fight", true, "{}")],
            new CoreInstanceOptions());
        Assert.True(startB.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var staleTerminalProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(
                    callback =>
                    {
                        if (callback.Sequence <= 2
                            && string.Equals(callback.MsgName, "AllTasksCompleted", StringComparison.OrdinalIgnoreCase))
                        {
                            staleTerminalProcessed.TrySetResult(true);
                        }

                        return Task.CompletedTask;
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        // The stale terminal events of batch A must not regress the Running state of batch B.
        await staleTerminalProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionState.Running, session.CurrentState);

        bridge.PublishSequenced(10001, "TaskChainStart");
        await Task.Delay(50);
        Assert.Equal(SessionState.Running, session.CurrentState);

        // Terminal events of the current batch (enqueued after its start) still apply.
        bridge.PublishSequenced(10003, "AllTasksCompleted");
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_StaleLifecycleCallbacksFromDestroyedInstance_DoNotRegressRunningState()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        // Batch A runs and is stopped. Tainted-queue recovery destroys the old instance
        // before the bridge bumps its callback generation, so a Destroyed emitted by that
        // AsstDestroy (and any late InitFailed of the old instance) passes the generation
        // filter and is enqueued before the next run start.
        var startA = await session.StartAsync();
        Assert.True(startA.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var stopA = await session.StopAsync();
        Assert.True(stopA.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        bridge.PublishSequenced(5, "Destroyed");
        bridge.PublishSequenced(1, "InitFailed");

        // Batch B starts while the lifecycle callbacks of the old instance are still pending.
        var startB = await session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "Fight", true, "{}")],
            new CoreInstanceOptions());
        Assert.True(startB.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var staleLifecycleProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(
                    callback =>
                    {
                        if (callback.Sequence == 2
                            && string.Equals(callback.MsgName, "InitFailed", StringComparison.OrdinalIgnoreCase))
                        {
                            staleLifecycleProcessed.TrySetResult(true);
                        }

                        return Task.CompletedTask;
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        // The stale lifecycle events of the destroyed old instance must not regress the
        // Running state of batch B.
        await staleLifecycleProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionState.Running, session.CurrentState);

        // A lifecycle event of the current run (enqueued after its start) still applies.
        bridge.PublishSequenced(5, "Destroyed");
        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_StaleTaskChainStartFromStoppedBatch_DoesNotResurrectRunningState()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        // Batch A runs and is manually stopped while its TaskChainStart is still waiting
        // in the pump backlog (native emits it after AsstStart and enqueues it before
        // AsstStop returns).
        var startA = await session.StartAsync();
        Assert.True(startA.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        bridge.PublishSequenced(10001, "TaskChainStart");

        var stopA = await session.StopAsync();
        Assert.True(stopA.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        var staleTaskChainStartProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(
                    callback =>
                    {
                        if (string.Equals(callback.MsgName, "TaskChainStart", StringComparison.OrdinalIgnoreCase))
                        {
                            staleTaskChainStartProcessed.TrySetResult(true);
                        }

                        return Task.CompletedTask;
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        // The stale pre-stop TaskChainStart must not regress Connected back to Running.
        await staleTaskChainStartProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        // A TaskChainStart enqueued after the latest run boundary still applies.
        bridge.PublishSequenced(10001, "TaskChainStart");
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.PublishSequenced(10003, "AllTasksCompleted");
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_InitFailed_ShouldClearRunningSession()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());
        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try { await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        });

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);
        bridge.Publish(new CoreCallbackEvent(1, "InitFailed", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_ResolutionError_ShouldStopRunningCoreAndClearSession()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());
        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try { await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        });

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);
        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"ResolutionError"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle && bridge.StopCallCount == 1);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_MalformedConnectionInfoPayload_LogsWarning_AndPumpContinues()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", "{not-json}", DateTimeOffset.UtcNow));
        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));

        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);
        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.Ordinal)
                && log.Message.Contains("ConnectionInfo payload", StringComparison.Ordinal)
                && log.Message.Contains("parse failed", StringComparison.Ordinal));

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task ConnectFailure_FallbackState_IsResolvedFromRuntimeStatus()
    {
        var bridge = new FakeBridge
        {
            ConnectSucceeds = false,
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: false),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var result = await session.ConnectAsync("127.0.0.1:5555", "General", null);

        Assert.False(result.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);
    }

    [Fact]
    public async Task StartFailure_FallbackState_IsResolvedFromRuntimeStatus()
    {
        var bridge = new FakeBridge
        {
            StartSucceeds = false,
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: true),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var result = await session.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
    }

    [Fact]
    public async Task StopFailure_FallbackState_IsResolvedFromRuntimeStatus()
    {
        var bridge = new FakeBridge
        {
            StopSucceeds = false,
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: false),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await session.StartAsync()).Success);
        var result = await session.StopAsync();

        Assert.False(result.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);
    }

    [Fact]
    public async Task CallbackStream_ConnectionInfoAndTaskChainEvents_ShouldMapToExpectedStates()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Connected"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.Publish(new CoreCallbackEvent(3, "AllTasksCompleted", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Reconnected"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Disconnect"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Idle);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_ScreencapTelemetryConnectionInfo_ShouldNotWarn_AndPumpContinues()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Connected"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"FastestWayToScreencap","details":{"method":"RawByNc","cost":87}}""",
            DateTimeOffset.UtcNow));
        bridge.Publish(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"ScreencapCost","details":{"min":101,"avg":202,"max":303}}""",
            DateTimeOffset.UtcNow));
        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));

        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        Assert.DoesNotContain(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                && log.Message.Contains("ConnectionInfo.what", StringComparison.Ordinal)
                && (log.Message.Contains("ScreencapCost", StringComparison.Ordinal)
                    || log.Message.Contains("FastestWayToScreencap", StringComparison.Ordinal)));

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_ConnectedWhileConnecting_ShouldNotFinalizeSessionState()
    {
        var bridge = new FakeBridge
        {
            BlockConnect = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        var connectTask = session.ConnectAsync("127.0.0.1:5555", "General", null);
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, session.CurrentState);

        bridge.Publish(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555","config":"General"}}""",
            DateTimeOffset.UtcNow));

        await Task.Delay(100);
        Assert.Equal(SessionState.Connecting, session.CurrentState);

        bridge.ConnectCompletion.SetResult(true);
        Assert.True((await connectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_OnEventThrows_ShouldWarnAndContinuePump()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(
                    callback =>
                    {
                        if (string.Equals(callback.MsgName, "TaskChainStart", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("synthetic callback error");
                        }

                        return Task.CompletedTask;
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.Publish(new CoreCallbackEvent(10004, "TaskChainStopped", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;

        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                   && log.Message.Contains("Session.Callback handler failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CallbackStream_CallbackReceivedSubscriberThrows_ShouldWarnAndContinuePump()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());
        session.CallbackReceived += _ => throw new InvalidOperationException("synthetic callback subscriber error");

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        bridge.Publish(new CoreCallbackEvent(10004, "TaskChainStopped", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;

        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                   && log.Message.Contains("Session.Callback subscriber failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CallbackStream_ConnectionInfoUnknownWhat_ShouldNotJumpState_AndShouldWarn()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"Connected"}""", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Connected);

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":"AlienState"}""", DateTimeOffset.UtcNow));
        await Task.Delay(50);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;

        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                   && log.Message.Contains("unknown ConnectionInfo.what", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CallbackStream_ConnectionInfoWhatNotString_ShouldWarnAndContinuePump()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), logService, new SessionStateMachine());

        using var cts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });

        bridge.Publish(new CoreCallbackEvent(2, "ConnectionInfo", """{"what":123}""", DateTimeOffset.UtcNow));
        bridge.Publish(new CoreCallbackEvent(10001, "TaskChainStart", "{}", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => session.CurrentState == SessionState.Running);

        cts.Cancel();
        bridge.Complete();
        await pumpTask;

        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                   && log.Message.Contains("property `what` is not a string", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryBeginRun_EndRun_ShouldEnforceOwnerMutualExclusion()
    {
        var session = new UnifiedSessionService(new FakeBridge(), CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True(session.TryBeginRun("TaskQueue", out var owner1));
        Assert.Equal("TaskQueue", owner1);
        Assert.True(session.IsRunOwner("TaskQueue"));
        Assert.Equal("TaskQueue", session.CurrentRunOwnerDisplayName);

        Assert.False(session.TryBeginRun("Copilot", out var currentOwner));
        Assert.Equal("TaskQueue", currentOwner);
        Assert.True(session.IsRunOwner("TaskQueue"));
        Assert.Equal("TaskQueue", session.CurrentRunOwnerDisplayName);

        session.EndRun("Copilot");
        Assert.True(session.IsRunOwner("TaskQueue"));

        session.EndRun("TaskQueue");
        Assert.False(session.IsRunOwner("TaskQueue"));
        Assert.Null(session.CurrentRunOwnerDisplayName);
        Assert.True(session.TryBeginRun("Copilot", "作业站", out var owner2));
        Assert.Equal("Copilot", owner2);
        Assert.Equal("作业站", session.CurrentRunOwnerDisplayName);
    }

    [Fact]
    public void RunOwnershipLease_ShouldRejectSameOwnerReentryAndIgnoreStaleRelease()
    {
        var session = new UnifiedSessionService(new FakeBridge(), CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True(session.TryAcquireRun("TaskQueue", out var firstLease, out var firstOwner));
        Assert.Equal("TaskQueue", firstOwner);
        Assert.False(session.TryAcquireRun("TaskQueue", out _, out var blockedOwner));
        Assert.Equal("TaskQueue", blockedOwner);

        Assert.True(session.EndRun(firstLease));
        Assert.True(session.TryAcquireRun("TaskQueue", out var secondLease, out _));
        Assert.NotEqual(firstLease.Generation, secondLease.Generation);

        Assert.False(session.EndRun(firstLease));
        Assert.Equal("TaskQueue", session.CurrentRunOwner);
        Assert.True(session.EndRun(secondLease));
        Assert.Null(session.CurrentRunOwner);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfile_BlockingIssue_ShouldFailAndClearTaskIdMappings()
    {
        var bridge = new FakeBridge();
        var logService = new UiLogService();
        var configService = CreateConfigService();
        var profile = configService.CurrentConfig.Profiles[configService.CurrentConfig.CurrentProfile];
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
            Type = "Recruit",
            Name = "Recruit",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["times"] = 4,
            },
        });

        var session = new UnifiedSessionService(bridge, configService, logService, new SessionStateMachine());
        var result = await session.AppendTasksFromCurrentProfileAsync();

        Assert.False(result.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False(session.TryResolveTaskIndexByCoreTaskId(1, out _));
        Assert.Contains(
            logService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                   && log.Message.Contains("Append task blocked `Recruit`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppendCoreTasksAsync_SecondAppendFailure_ShouldResetQueue_RequireReconnect_AndClearMappings()
    {
        var bridge = new FakeBridge();
        bridge.AppendResults.Enqueue(CoreResult<int>.Ok(11));
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "second append failed")));
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var result = await session.AppendCoreTasksAsync([
            new CoreTaskRequest("Fight", "one", true, "{}"),
            new CoreTaskRequest("Fight", "two", true, "{}"),
        ]);

        Assert.False(result.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False(session.TryResolveTaskIndexByCoreTaskId(11, out _));
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(0, bridge.StartCallCount);

        Assert.True((await session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        Assert.True((await session.StartAsync()).Success);
        Assert.Equal(1, bridge.StartCallCount);
    }

    [Fact]
    public async Task AppendCoreTasksAsync_CanceledBeforeFirstAppend_ShouldNotTouchNativeQueue()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendCoreTasksAsync([
            new CoreTaskRequest("Fight", "one", true, "{}"),
        ], cts.Token));

        Assert.Equal(0, bridge.AppendCallCount);
        Assert.Equal(0, bridge.RecoverTaintedTaskQueueCallCount);
    }

    [Fact]
    public async Task AppendCoreTasksAsync_CanceledAfterFirstAppend_ShouldResetQueueBeforeThrowing()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge
        {
            OnAppend = _ => cts.Cancel(),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendCoreTasksAsync([
            new CoreTaskRequest("Fight", "one", true, "{}"),
            new CoreTaskRequest("Fight", "two", true, "{}"),
        ], cts.Token));

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False((await session.StartAsync()).Success);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfile_SecondAppendFailure_ShouldResetQueue()
    {
        var bridge = new FakeBridge();
        bridge.AppendResults.Enqueue(CoreResult<int>.Ok(17));
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "second append failed")));
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateFightTask("one"));
        profile.TaskQueue.Add(CreateFightTask("two"));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        var result = await session.AppendTasksFromCurrentProfileAsync();

        Assert.False(result.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False(session.TryResolveTaskIndexByCoreTaskId(17, out _));
        Assert.False((await session.StartAsync()).Success);
    }

    [Fact]
    public async Task AppendCoreTasksAsync_CleanupFailure_ShouldKeepStartBlocked()
    {
        var bridge = new FakeBridge { RecoverTaintedTaskQueueSucceeds = false };
        bridge.AppendResults.Enqueue(CoreResult<int>.Ok(11));
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "second append failed")));
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.False((await session.AppendCoreTasksAsync([
            new CoreTaskRequest("Fight", "one", true, "{}"),
            new CoreTaskRequest("Fight", "two", true, "{}"),
        ])).Success);

        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(0, bridge.StartCallCount);
    }

    [Fact]
    public async Task StartAsync_AfterProfileAppendFailure_ShouldRecoverAndRequireReconnect()
    {
        var bridge = new FakeBridge { StartSucceeds = false };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await session.AppendCoreTasksAsync([
            new CoreTaskRequest("Fight", "queued", true, "{}"),
        ])).Success);

        var result = await session.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(1, bridge.StartCallCount);
    }

    [Fact]
    public async Task AppendAndStartCoreTaskAsync_CanceledAfterAppend_ShouldRecoverAndRequireReconnect()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge
        {
            OnAppend = _ => cts.Cancel(),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendAndStartCoreTaskAsync(
            new CoreTaskRequest("Fight", "canceled", true, "{}"),
            cts.Token));

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(0, bridge.StartCallCount);
    }

    [Fact]
    public async Task AppendAndStartCoreTaskAsync_StartFailure_ShouldRecoverBeforeLaterStart()
    {
        var bridge = new FakeBridge();
        bridge.StartResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "start failed")));
        bridge.StartResults.Enqueue(CoreResult<bool>.Ok(true));
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var failed = await session.AppendAndStartCoreTaskAsync(
            new CoreTaskRequest("Fight", "stale", true, "{}"));

        Assert.False(failed.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(1, bridge.StartCallCount);

        Assert.True((await session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        var next = await session.AppendAndStartCoreTaskAsync(
            new CoreTaskRequest("Fight", "fresh", true, "{}"));

        Assert.True(next.Success);
        Assert.Equal(2, bridge.AppendCallCount);
        Assert.Equal(2, bridge.StartCallCount);
        Assert.Equal(SessionState.Running, session.CurrentState);
    }

    [Fact]
    public async Task AppendAndStartCoreTaskAsync_ShouldPublishTaskIdBeforeNativeStart()
    {
        var publishedTaskId = 0;
        var bridge = new FakeBridge
        {
            OnStart = () => Assert.Equal(1, publishedTaskId),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var result = await session.AppendAndStartCoreTaskAsync(
            new CoreTaskRequest("Copilot", "callback-order", true, "{}"),
            onAppended: taskId => publishedTaskId = taskId);

        Assert.True(result.Success);
        Assert.Equal(result.Value, publishedTaskId);
    }

    [Fact]
    public async Task AppendCoreTasksAndStartAsync_OptionFailure_ShouldNotAppendOrStart()
    {
        var bridge = new FakeBridge();
        bridge.ApplyInstanceOptionsResults.Enqueue(CoreResult<bool>.Fail(
            new CoreError(CoreErrorCode.NativeCallFailed, "option failed")));
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var result = await session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "blocked", true, "{}")],
            new CoreInstanceOptions(TouchMode: "maatouch"));

        Assert.False(result.Success);
        Assert.Equal(1, bridge.ApplyInstanceOptionsCallCount);
        Assert.Equal(0, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Equal(0, bridge.RecoverTaintedTaskQueueCallCount);
    }

    [Fact]
    public async Task AppendCoreTasksAndStartAsync_CanceledAfterAppend_ShouldRecoverAndRequireReconnect()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge { OnAppend = _ => cts.Cancel() };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "canceled", true, "{}")],
            new CoreInstanceOptions(),
            cts.Token));

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False((await session.StartAsync()).Success);
    }

    [Fact]
    public async Task AppendStartOperations_WhileRunning_ShouldRejectWithoutTouchingQueueOrMappings()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var first = await session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Fight", "first", true, "{}"));
        Assert.True(first.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(1, out _));

        var singleTask = await session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Fight", "second", true, "{}"));
        var batch = await session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "third", true, "{}")],
            new CoreInstanceOptions());
        var profile = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        foreach (var result in new[] { singleTask, batch, profile })
        {
            Assert.False(result.Success);
            Assert.Equal(CoreErrorCode.StartFailed, result.Error?.Code);
            Assert.Contains("stop the current run", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(1, bridge.StartCallCount);
        Assert.Equal(0, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(1, out _));
        Assert.Equal(SessionState.Running, session.CurrentState);
    }

    [Fact]
    public async Task ConnectAsync_WhenQueueRecoveryPreviouslyFailed_ShouldRetryRecoveryDuringReconnect()
    {
        var bridge = new FakeBridge { RecoverTaintedTaskQueueSucceeds = false };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "append failed")));

        var append = await session.AppendCoreTasksAsync([new CoreTaskRequest("Fight", "one", true, "{}")]);
        Assert.False(append.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(0, bridge.StartCallCount);

        bridge.RecoverTaintedTaskQueueSucceeds = true;
        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.Equal(2, bridge.RecoverTaintedTaskQueueCallCount);

        var started = await session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Fight", "fresh", true, "{}"));
        Assert.True(started.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
    }

    [Fact]
    public async Task ConnectAsync_WhenRetriedQueueRecoveryStillFails_ShouldConnectButKeepStartBlocked()
    {
        var bridge = new FakeBridge { RecoverTaintedTaskQueueSucceeds = false };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "append failed")));
        Assert.False((await session.AppendCoreTasksAsync([new CoreTaskRequest("Fight", "one", true, "{}")])).Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.Equal(2, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        Assert.False((await session.StartAsync()).Success);
        Assert.Equal(0, bridge.StartCallCount);
        var append = await session.AppendCoreTasksAsync([new CoreTaskRequest("Fight", "two", true, "{}")]);
        Assert.False(append.Success);
        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(2, bridge.RecoverTaintedTaskQueueCallCount);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfile_SaveFailureAfterRecruitAppend_ShouldRestoreOneShotExpediteForRetry()
    {
        var bridge = new FakeBridge();
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        var log = new UiLogService();
        var config = new UnifiedConfigurationService(
            new FailOnceSaveConfigStore(Path.Combine(root, "config", "avalonia.json")),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root);
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        var recruitParams = TaskParamCompiler.CompileRecruit(
            new RecruitTaskParamsDto
            {
                Times = 2,
                Refresh = false,
                ForceRefresh = false,
                UseExpedited = true,
                SkipRobot = true,
                SetTime = true,
            },
            profile,
            config.CurrentConfig).Params;
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Recruit",
            Name = "Recruit",
            IsEnabled = true,
            Params = recruitParams,
        });
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());

        await Assert.ThrowsAsync<IOException>(() => session.AppendTasksFromCurrentProfileAsync());

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Empty(bridge.AppendedTasks);
        AssertOneShotRecruitExpedited(profile.TaskQueue[0].Params);
        Assert.False((await session.StartAsync()).Success);

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var retry = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.True(retry.Success);
        AssertOneShotRecruitExpedited(Assert.Single(bridge.AppendedTasks));
        AssertOneShotRecruitWasConsumed(profile.TaskQueue[0].Params);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfileAndStartAsync_StartFailureAfterRecruitPersist_ShouldRestoreOneShotExpediteForRetry()
    {
        var bridge = new FakeBridge();
        bridge.StartResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "start failed")));
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateExpeditedRecruitTask(profile, config.CurrentConfig));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var failed = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.False(failed.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Empty(bridge.AppendedTasks);
        AssertOneShotRecruitExpedited(profile.TaskQueue[0].Params);

        Assert.True((await session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        var retry = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.True(retry.Success);
        AssertOneShotRecruitExpedited(Assert.Single(bridge.AppendedTasks));
        AssertOneShotRecruitWasConsumed(profile.TaskQueue[0].Params);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfileAndStartAsync_CanceledAfterRecruitAppend_ShouldRestoreOneShotExpediteForRetry()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge
        {
            OnAppend = _ => cts.Cancel(),
        };
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateExpeditedRecruitTask(profile, config.CurrentConfig));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendTasksFromCurrentProfileAndStartAsync(
            new CoreInstanceOptions(),
            cts.Token));

        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Empty(bridge.AppendedTasks);
        AssertOneShotRecruitExpedited(profile.TaskQueue[0].Params);

        Assert.True((await session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        var retry = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.True(retry.Success);
        AssertOneShotRecruitExpedited(Assert.Single(bridge.AppendedTasks));
        AssertOneShotRecruitWasConsumed(profile.TaskQueue[0].Params);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfileAndStartAsync_LaterAppendFailure_ShouldRestoreOneShotExpediteForRetry()
    {
        var bridge = new FakeBridge();
        bridge.AppendResults.Enqueue(CoreResult<int>.Ok(1));
        bridge.AppendResults.Enqueue(CoreResult<int>.Fail(new CoreError(CoreErrorCode.AppendTaskFailed, "later append failed")));
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateExpeditedRecruitTask(profile, config.CurrentConfig));
        profile.TaskQueue.Add(CreateFightTask("Fight"));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var failed = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.False(failed.Success);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Empty(bridge.AppendedTasks);
        AssertOneShotRecruitExpedited(profile.TaskQueue[0].Params);

        Assert.True((await session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        var retry = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());

        Assert.True(retry.Success);
        AssertOneShotRecruitExpedited(Assert.Single(bridge.AppendedTasks, task => task.Type == "Recruit"));
        Assert.Equal(2, bridge.AppendedTasks.Count);
        AssertOneShotRecruitWasConsumed(profile.TaskQueue[0].Params);
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfileAndStartAsync_OptionsCanceledWithStaleMappings_ShouldNotTaintQueue()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge
        {
            OnApplyInstanceOptions = () => cts.Cancel(),
        };
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateFightTask("Fight"));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var first = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());
        Assert.True(first.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        Assert.True((await session.StopAsync()).Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);
        // The finished batch's task-ID mappings are still present (stale).
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(1, out _));

        // Caller cancellation lands in the options phase, before the append core
        // clears the stale mappings.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AppendTasksFromCurrentProfileAndStartAsync(
            new CoreInstanceOptions(TouchMode: "maatouch"),
            cts.Token));

        Assert.Equal(1, bridge.ApplyInstanceOptionsCallCount);
        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(0, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        // The queue was never touched, so a retry starts without a reconnect.
        var retry = await session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions());
        Assert.True(retry.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
        Assert.Equal(2, bridge.AppendedTasks.Count);
    }

    [Fact]
    public async Task StartCloseDownAsync_StartFailure_ShouldRecoverAndRequireReconnect()
    {
        var bridge = new FakeBridge();
        bridge.StartResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "CloseDown failed")));
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var result = await session.StartCloseDownAsync("YoStarEN");

        Assert.False(result.Success);
        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(1, bridge.StartCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False((await session.StartAsync()).Success);
    }

    [Fact]
    public async Task StartCloseDownAsync_CanceledAfterAppend_ShouldRecoverBeforeThrowing()
    {
        using var cts = new CancellationTokenSource();
        var bridge = new FakeBridge { OnAppend = _ => cts.Cancel() };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartCloseDownAsync("Official", cts.Token));

        Assert.Equal(1, bridge.AppendCallCount);
        Assert.Equal(0, bridge.StartCallCount);
        Assert.Equal(1, bridge.RecoverTaintedTaskQueueCallCount);
        Assert.False((await session.StartAsync()).Success);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_ShouldWaitForAppendStartTransaction()
    {
        var startCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge { StartCompletion = startCompletion };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var startTask = session.AppendCoreTasksAndStartAsync(
            [new CoreTaskRequest("Fight", "running", true, "{}")],
            new CoreInstanceOptions());
        await bridge.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var reloadTask = session.ReloadResourceWhenIdleAsync(waitTimeout: TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(0, bridge.ReloadResourceCallCount);

        bridge.RuntimeStatus = new CoreRuntimeStatus(true, true, true);
        startCompletion.SetResult(true);
        Assert.True((await startTask).Success);
        await Task.Delay(100);
        Assert.Equal(0, bridge.ReloadResourceCallCount);

        bridge.RuntimeStatus = new CoreRuntimeStatus(true, true, false);
        Assert.True((await reloadTask).Success);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
    }

    private static UnifiedTaskItem CreateFightTask(string name) => new()
    {
        Type = "Fight",
        Name = name,
        IsEnabled = true,
        Params = new JsonObject
        {
            ["stage"] = "1-7",
            ["medicine"] = 0,
            ["stone"] = 0,
            ["times"] = 1,
            ["series"] = 1,
        },
    };

    private static UnifiedTaskItem CreateExpeditedRecruitTask(UnifiedProfile profile, UnifiedConfig config) => new()
    {
        Type = TaskModuleTypes.Recruit,
        Name = "Recruit",
        IsEnabled = true,
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
            config).Params,
    };

    private static void AssertOneShotRecruitExpedited(JsonObject parameters)
    {
        Assert.True(parameters["expedite"]?.GetValue<bool>());
        Assert.Equal(2, parameters["expedite_times"]?.GetValue<int>());
    }

    private static void AssertOneShotRecruitExpedited(CoreTaskRequest task)
    {
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(task.ParamsJson));
        AssertOneShotRecruitExpedited(parameters);
    }

    private static void AssertOneShotRecruitWasConsumed(JsonObject parameters)
    {
        Assert.False(parameters["expedite"]?.GetValue<bool>());
        Assert.False(parameters.ContainsKey("expedite_times"));
    }

    private sealed class FailOnceSaveConfigStore(string configPath) : IUnifiedConfigStore
    {
        public string ConfigPath { get; } = configPath;

        private bool _shouldFail = true;

        public bool Exists() => false;

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<UnifiedConfig?>(null);

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                return Task.FromException(new IOException("save failed"));
            }

            return Task.CompletedTask;
        }

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_WhenSessionIsIdle_ShouldReloadImmediately()
    {
        var bridge = new FakeBridge
        {
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: false),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        var result = await session.ReloadResourceWhenIdleAsync("YoStarEN");

        Assert.True(result.Success);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
        Assert.Equal("YoStarEN", bridge.LastReloadClientType);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_WhenSessionIsRunning_ShouldWaitUntilIdleThenReload()
    {
        var bridge = new FakeBridge
        {
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: true),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());
        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await session.StartAsync()).Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var reloadTask = session.ReloadResourceWhenIdleAsync(waitTimeout: TimeSpan.FromSeconds(2));
        await Task.Delay(120);
        Assert.Equal(0, bridge.ReloadResourceCallCount);

        bridge.RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: false);
        var result = await reloadTask;

        Assert.True(result.Success);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_WhenSessionStaysBusy_ShouldTimeoutWithoutReload()
    {
        var bridge = new FakeBridge
        {
            RuntimeStatus = new CoreRuntimeStatus(Initialized: true, Connected: true, Running: true),
        };
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());
        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await session.StartAsync()).Success);
        Assert.Equal(SessionState.Running, session.CurrentState);

        var result = await session.ReloadResourceWhenIdleAsync(waitTimeout: TimeSpan.FromMilliseconds(300));

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.ConnectTimeout, result.Error?.Code);
        Assert.Equal(0, bridge.ReloadResourceCallCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var startedAt = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - startedAt > timeoutMs)
            {
                throw new TimeoutException("Condition not reached in expected time.");
            }

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task SetQueuedTaskParamsAsync_MultiMappedIndex_ShouldApplyAllMappedTasksOnSuccess()
    {
        var bridge = new FakeBridge();
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.UserDataUpdate,
            Name = "UserDataUpdate",
            IsEnabled = true,
            Params = TaskModuleParameterDefaults.CreateUserDataUpdateDefaults(),
        });
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var appended = await session.AppendTasksFromCurrentProfileAsync();
        Assert.True(appended.Success);
        Assert.Equal(2, appended.Value);

        const string newParams = """{"update_oper_box":false}""";
        var update = await session.SetQueuedTaskParamsAsync(0, newParams, """{"update_oper_box":true}""");

        Assert.True(update.Success);
        Assert.Equal(2, bridge.SetTaskParamsCalls.Count);
        Assert.All(bridge.SetTaskParamsCalls, call => Assert.Equal(newParams, call.ParamsJson));
    }

    [Fact]
    public async Task SetQueuedTaskParamsAsync_MultiMappedIndex_ShouldRollBackAppliedSiblingOnFailure()
    {
        var bridge = new FakeBridge();
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.UserDataUpdate,
            Name = "UserDataUpdate",
            IsEnabled = true,
            Params = TaskModuleParameterDefaults.CreateUserDataUpdateDefaults(),
        });
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var appended = await session.AppendTasksFromCurrentProfileAsync();
        Assert.True(appended.Success);
        Assert.Equal(2, appended.Value);

        bridge.SetTaskParamsResults.Enqueue(CoreResult<bool>.Ok(true));
        bridge.SetTaskParamsResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NativeCallFailed, "second update failed")));

        const string newParams = """{"update_oper_box":false}""";
        const string previousParams = """{"update_oper_box":true}""";
        var update = await session.SetQueuedTaskParamsAsync(0, newParams, previousParams);

        Assert.False(update.Success);
        Assert.Equal(CoreErrorCode.NativeCallFailed, update.Error?.Code);
        Assert.Equal(3, bridge.SetTaskParamsCalls.Count);
        Assert.Equal(newParams, bridge.SetTaskParamsCalls[0].ParamsJson);
        Assert.Equal(newParams, bridge.SetTaskParamsCalls[1].ParamsJson);
        Assert.NotEqual(bridge.SetTaskParamsCalls[0].TaskId, bridge.SetTaskParamsCalls[1].TaskId);
        Assert.Equal(bridge.SetTaskParamsCalls[0].TaskId, bridge.SetTaskParamsCalls[2].TaskId);
        Assert.Equal(previousParams, bridge.SetTaskParamsCalls[2].ParamsJson);
    }

    [Fact]
    public async Task SetQueuedTaskParamsAsync_AfterAdhocAppendAndStart_ShouldNotTargetAdhocTask()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var started = await session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Copilot", "adhoc", true, "{}"));

        Assert.True(started.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
        // Ad-hoc task ids stay resolvable for callback task-id -> queue-index
        // resolution, but they are not a profile batch...
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(started.Value, out var adhocIndex));
        Assert.Equal(0, adhocIndex);

        // ...so a profile param edit must not reach the ad-hoc native task.
        var edit = await session.SetQueuedTaskParamsAsync(0, """{"stage":"1-7"}""", """{"stage":"1-6"}""");

        Assert.True(edit.Success);
        Assert.Empty(bridge.SetTaskParamsCalls);
    }

    [Fact]
    public async Task SetQueuedTaskParamsAsync_AfterAdhocBatch_ShouldNotTargetBatchTasks()
    {
        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, CreateConfigService(), new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var started = await session.AppendCoreTasksAndStartAsync(
            [
                new CoreTaskRequest("Fight", "one", true, "{}"),
                new CoreTaskRequest("Award", "two", true, "{}"),
            ],
            new CoreInstanceOptions());

        Assert.True(started.Success);
        Assert.Equal(SessionState.Running, session.CurrentState);
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(1, out var firstIndex));
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(2, out var secondIndex));
        Assert.Equal(0, firstIndex);
        Assert.Equal(1, secondIndex);

        var editFirst = await session.SetQueuedTaskParamsAsync(0, """{"stage":"1-7"}""", """{"stage":"1-6"}""");
        var editSecond = await session.SetQueuedTaskParamsAsync(1, """{"award":true}""", """{"award":false}""");

        Assert.True(editFirst.Success);
        Assert.True(editSecond.Success);
        Assert.Empty(bridge.SetTaskParamsCalls);
    }

    [Fact]
    public async Task SetQueuedTaskParamsAsync_AfterAdhocRun_ShouldNotTargetAdhocOrStaleProfileTask()
    {
        var bridge = new FakeBridge();
        var config = CreateConfigService();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        profile.TaskQueue.Add(CreateFightTask("Fight"));
        var session = new UnifiedSessionService(bridge, config, new UiLogService(), new SessionStateMachine());

        Assert.True((await session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var appended = await session.AppendTasksFromCurrentProfileAsync();
        Assert.True(appended.Success);
        // Profile batch registers its core task id under queue index 0.
        Assert.True(session.TryResolveTaskIndexByCoreTaskId(1, out var profileIndex));
        Assert.Equal(0, profileIndex);

        // An ad-hoc run (Copilot/Toolbox/WebApi) replaces the profile mappings with
        // its own non-profile mapping, so a later profile edit must target neither
        // the ad-hoc task nor the stale profile task.
        var adhoc = await session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Copilot", "adhoc", true, "{}"));
        Assert.True(adhoc.Success);

        var edit = await session.SetQueuedTaskParamsAsync(0, """{"stage":"1-7"}""", """{"stage":"1-6"}""");

        Assert.True(edit.Success);
        Assert.Empty(bridge.SetTaskParamsCalls);

        // The next profile append batch restores editability of the native queue.
        var reappended = await session.AppendTasksFromCurrentProfileAsync();
        Assert.True(reappended.Success);
        var editAfterProfileBatch = await session.SetQueuedTaskParamsAsync(0, """{"stage":"1-7"}""", """{"stage":"1-6"}""");
        Assert.True(editAfterProfileBatch.Success);
        var applied = Assert.Single(bridge.SetTaskParamsCalls);
        Assert.Equal(3, applied.TaskId);
    }

    private static UnifiedConfigurationService CreateConfigService()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var store = new AvaloniaJsonConfigStore(root);
        var log = new UiLogService();
        return new UnifiedConfigurationService(store, new GuiNewJsonConfigImporter(), new GuiJsonConfigImporter(), log, root);
    }

    private sealed class FakeBridge : IMaaCoreBridge, IMaaCoreBridgeQueueRecovery
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private long _publishedSequence;

        public bool ConnectSucceeds { get; init; } = true;

        public bool StartSucceeds { get; init; } = true;

        public bool StopSucceeds { get; init; } = true;

        public int StopCallCount { get; private set; }

        public bool BlockStop { get; init; }

        public bool IgnoreStopCancellation { get; init; }

        public TaskCompletionSource<bool>? StopCompletion { get; init; }

        public TaskCompletionSource<bool> StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AppendCallCount { get; private set; }

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public int StartCallCount { get; private set; }

        public int RecoverTaintedTaskQueueCallCount { get; private set; }

        public bool RecoverTaintedTaskQueueSucceeds { get; set; } = true;

        public Action<CoreTaskRequest>? OnAppend { get; init; }

        public Action? OnStart { get; init; }

        public Queue<CoreResult<int>> AppendResults { get; } = new();

        public Queue<CoreResult<bool>> StartResults { get; } = new();

        public Queue<CoreResult<bool>> ApplyInstanceOptionsResults { get; } = new();

        public int ApplyInstanceOptionsCallCount { get; private set; }

        public TaskCompletionSource<bool> StartStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool>? StartCompletion { get; init; }

        public bool BlockConnect { get; init; }

        public TaskCompletionSource<bool>? ConnectCompletion { get; init; }

        public TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CoreRuntimeStatus RuntimeStatus { get; set; } = new(true, true, false);

        public bool ReloadResourceSucceeds { get; init; } = true;

        public int ReloadResourceCallCount { get; private set; }

        public string? LastReloadClientType { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult(true);
            if (BlockConnect)
            {
                if (ConnectCompletion is null)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else
                {
                    await ConnectCompletion.Task.WaitAsync(cancellationToken);
                }
            }

            return ConnectSucceeds
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed"));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendCallCount++;
            AppendedTasks.Add(task);
            OnAppend?.Invoke(task);
            return Task.FromResult(AppendResults.Count > 0
                ? AppendResults.Dequeue()
                : CoreResult<int>.Ok(AppendCallCount));
        }

        public List<(int TaskId, string ParamsJson)> SetTaskParamsCalls { get; } = new();

        public Queue<CoreResult<bool>> SetTaskParamsResults { get; } = new();

        public Task<CoreResult<bool>> SetTaskParamsAsync(int taskId, string paramsJson, CancellationToken cancellationToken = default)
        {
            SetTaskParamsCalls.Add((taskId, paramsJson));
            return Task.FromResult(SetTaskParamsResults.Count > 0
                ? SetTaskParamsResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public Action? OnApplyInstanceOptions { get; init; }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions options,
            CancellationToken cancellationToken = default)
        {
            ApplyInstanceOptionsCallCount++;
            OnApplyInstanceOptions?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ApplyInstanceOptionsResults.Count > 0
                ? ApplyInstanceOptionsResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public async Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            StartStarted.TrySetResult(true);
            OnStart?.Invoke();
            if (StartCompletion is not null)
            {
                await StartCompletion.Task.WaitAsync(cancellationToken);
            }

            return StartResults.Count > 0
                ? StartResults.Dequeue()
                : StartSucceeds
                    ? CoreResult<bool>.Ok(true)
                    : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "start failed"));
        }

        public Task<CoreResult<bool>> RecoverTaintedTaskQueueAsync(CancellationToken cancellationToken = default)
        {
            RecoverTaintedTaskQueueCallCount++;
            AppendedTasks.Clear();
            return Task.FromResult(RecoverTaintedTaskQueueSucceeds
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "queue cleanup failed")));
        }

        public async Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            StopStarted.TrySetResult(true);
            if (BlockStop)
            {
                if (StopCompletion is null)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else if (IgnoreStopCancellation)
                {
                    await StopCompletion.Task;
                }
                else
                {
                    await StopCompletion.Task.WaitAsync(cancellationToken);
                }
            }

            return StopSucceeds
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "stop failed"));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(RuntimeStatus));

        public Task<CoreResult<bool>> ReloadResourceAsync(string? clientType = null, CancellationToken cancellationToken = default)
        {
            ReloadResourceCallCount++;
            LastReloadClientType = clientType;
            return Task.FromResult(ReloadResourceSucceeds
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ResourceLoadFailed, "reload failed")));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Publish(CoreCallbackEvent callback) => _channel.Writer.TryWrite(callback);

        public long EnqueuedCallbackSequence => Interlocked.Read(ref _publishedSequence);

        public void PublishSequenced(int msgId, string msgName, string payloadJson = "{}")
        {
            var sequence = Interlocked.Increment(ref _publishedSequence);
            Publish(new CoreCallbackEvent(msgId, msgName, payloadJson, DateTimeOffset.UtcNow, sequence));
        }

        public void Complete() => _channel.Writer.TryComplete();
    }
}
