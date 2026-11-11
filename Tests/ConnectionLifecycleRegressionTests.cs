using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class ConnectionLifecycleRegressionTests
{
    private static CoreError CreateAbandonedStopError()
        => new(
            CoreErrorCode.StopFailed,
            "AsstStop did not return within 3.0s; native instance was abandoned.");

    [Fact]
    public async Task ConnectAsync_ShouldRefreshLastSuccessfulConnectionInfo_OnEverySuccess()
    {
        await using var fixture = await SessionFixture.CreateAsync(new ScriptedBridge());

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb-old")).Success);
        Assert.Equal("/tmp/adb-old", fixture.Session.LastSuccessfulConnectionInfo?.AdbPath);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:6666", "General", "/tmp/adb-new")).Success);

        Assert.Equal("127.0.0.1:6666", fixture.Session.LastSuccessfulConnectionInfo?.Address);
        Assert.Equal("/tmp/adb-new", fixture.Session.LastSuccessfulConnectionInfo?.AdbPath);
    }

    [Fact]
    public async Task ConnectAsync_ShouldPassExtendedTimeoutBudget_ToBridge()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        Assert.Equal(TimeSpan.FromSeconds(30), bridge.LastConnectionInfo?.Timeout);
    }

    [Fact]
    public async Task StopAsync_DuringConnecting_ShouldSupersedePendingConnectWithoutBridgeStop_AndAllowReconnect()
    {
        var bridge = new ScriptedBridge
        {
            BlockConnect = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        var connectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null);
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        var stopResult = await fixture.Session.StopAsync();

        Assert.True(stopResult.Success);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.False(bridge.StopStarted.Task.IsCompleted);

        bridge.ConnectCompletion.SetResult(true);
        var staleConnect = await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(staleConnect.Success);
        Assert.Equal(CoreErrorCode.ConnectTimeout, staleConnect.Error?.Code);

        bridge.BlockConnect = false;
        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:6666", "General", null)).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.Equal("127.0.0.1:6666", bridge.LastConnectionInfo?.Address);
    }

    [Fact]
    public async Task StopAsync_DuringConnecting_ShouldReturnIdleEvenWhenBridgeIgnoresCancellation_AndIgnoreStaleSuccess()
    {
        var bridge = new ScriptedBridge
        {
            BlockConnect = true,
            IgnoreConnectCancellation = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        var connectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null);
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        var stopResult = await fixture.Session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopResult.Success);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.False(bridge.StopStarted.Task.IsCompleted);

        var staleConnect = await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(staleConnect.Success);
        Assert.Equal(CoreErrorCode.ConnectTimeout, staleConnect.Error?.Code);

        bridge.ConnectCompletion.SetResult(true);
        await Task.Delay(100);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);
    }

    [Fact]
    public async Task StopAsync_WhenBridgeReportsAbandonedStop_ShouldInvalidateConnectedState()
    {
        var stopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        stopCompletion.SetResult(CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.StopFailed,
                "AsstStop did not return within 3.0s; native operation was abandoned.",
                "native stop timed out")));
        var bridge = new ScriptedBridge
        {
            StopCompletion = stopCompletion,
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

        var stopResult = await fixture.Session.StopAsync();

        Assert.False(stopResult.Success);
        Assert.Equal(CoreErrorCode.StopFailed, stopResult.Error?.Code);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);
    }

    [Fact]
    public async Task ApplyInstanceOptionsAsync_WhenBridgeReportsAbandonedStop_ShouldRecoverAndRetryOnce()
    {
        var bridge = new ScriptedBridge();
        bridge.ApplyInstanceOptionsResults.Enqueue(CoreResult<bool>.Fail(new CoreError(
            CoreErrorCode.StopFailed,
            "Bridge native stop timed out; instance was abandoned to avoid use-after-free.")));
        bridge.ApplyInstanceOptionsResults.Enqueue(CoreResult<bool>.Ok(true));
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        var result = await fixture.Session.ApplyInstanceOptionsAsync(new CoreInstanceOptions(TouchMode: "maatouch"));

        Assert.True(result.Success);
        Assert.Equal(2, bridge.ApplyInstanceOptionsCallCount);
        Assert.Equal(1, bridge.RecoverFromAbandonedStopCallCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenBridgeReportsAbandonedStop_ShouldRecoverAndRetrySameTargetOnce()
    {
        var bridge = new ScriptedBridge();
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Fail(new CoreError(
            CoreErrorCode.StopFailed,
            "Bridge native stop timed out; instance was abandoned to avoid use-after-free.")));
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Ok(true));
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        var result = await fixture.Session.ConnectAsync("127.0.0.1:5556", "General", "/tmp/adb-new");

        Assert.True(result.Success);
        Assert.Equal(2, bridge.ConnectCallCount);
        Assert.Equal(1, bridge.RecoverFromAbandonedStopCallCount);
        Assert.Equal("127.0.0.1:5556", bridge.LastConnectionInfo?.Address);
        Assert.Equal("/tmp/adb-new", fixture.Session.LastSuccessfulConnectionInfo?.AdbPath);
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectFails_ShouldIgnoreLateMatchingConnectedCallbackWhileIdle()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

        bridge.ConnectResults.Enqueue(CoreResult<bool>.Fail(new CoreError(
            CoreErrorCode.ConnectTimeout,
            "Connect timed out after 30s.")));
        var reconnect = await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null);
        Assert.False(reconnect.Success);
        Assert.Equal(CoreErrorCode.ConnectTimeout, reconnect.Error?.Code);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        // A late Connected callback from the failed attempt (e.g. the abort stop
        // budget expired before AsstStop could run) must not resurrect the failed
        // connection while the session is idle.
        bridge.PublishCallback(ConnectionCallback("Connected", "127.0.0.1:5555", "General"));
        await Task.Delay(100);

        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task ConnectAsync_WhenCallerCancels_ShouldIgnoreLateMatchingConnectedCallbackWhileIdle()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        bridge.BlockConnect = true;
        bridge.ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connectCts = new CancellationTokenSource();
        var reconnectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null, null, connectCts.Token);
        await WaitUntilAsync(() => bridge.ConnectCallCount >= 2);
        connectCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reconnectTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        // Same protection as the failed-connect path: a late Connected callback
        // from the canceled attempt must not move the session while idle.
        bridge.PublishCallback(ConnectionCallback("Connected", "127.0.0.1:5555", "General"));
        await Task.Delay(100);

        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_AfterAbandonedStop_ShouldIgnoreRepeatedStaleConnectedCallbacks()
    {
        var stopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        stopCompletion.SetResult(CoreResult<bool>.Fail(new CoreError(
            CoreErrorCode.StopFailed,
            "AsstStop did not return within 3.0s; native operation was abandoned.")));
        var bridge = new ScriptedBridge
        {
            StopCompletion = stopCompletion,
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.False((await fixture.Session.StopAsync()).Success);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        bridge.PublishCallback(ConnectionCallback("Connected", "127.0.0.1:5555", "General"));
        bridge.PublishCallback(ConnectionCallback("Reconnected", "127.0.0.1:5555", "General"));
        await Task.Delay(100);

        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task CallbackStream_DuringReconnect_ShouldIgnoreOldTargetCallbacksAndAcceptNewTarget()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb-old")).Success);

        bridge.BlockConnect = true;
        bridge.ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectTask = fixture.Session.ConnectAsync("127.0.0.1:5556", "General", "/tmp/adb-new");
        await WaitUntilAsync(() => bridge.LastConnectionInfo?.Address == "127.0.0.1:5556");

        bridge.PublishCallback(ConnectionCallback("Connected", "127.0.0.1:5555", "General", "/tmp/adb-old"));
        await Task.Delay(100);
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        bridge.ConnectCompletion.SetResult(true);
        Assert.True((await reconnectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        bridge.PublishCallback(ConnectionCallback("Reconnected", "127.0.0.1:5555", "General", "/tmp/adb-old"));
        await Task.Delay(100);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        bridge.PublishCallback(ConnectionCallback("Reconnected", "127.0.0.1:5556", "General", "/tmp/adb-new"));
        await Task.Delay(100);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task StopAsync_OnAbandonedNativeStop_ShouldReturnIdleAndClearLastConnection()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Session.StartAsync()).Success);
        bridge.StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.StopCompletion.SetResult(CoreResult<bool>.Fail(CreateAbandonedStopError()));

        var stopResult = await fixture.Session.StopAsync();

        Assert.False(stopResult.Success);
        Assert.Equal(CoreErrorCode.StopFailed, stopResult.Error?.Code);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);
    }

    [Fact]
    public async Task StopAsync_WhenCallerTokenCancelsInsideBridgeStop_ShouldPropagateCancellationWithoutStrandingStopping()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        // The bridge's post-stop not-running probe re-checks the caller token, so a
        // cancellation landing after AsstStop already succeeded surfaces as an OCE out
        // of the bridge stop; the session must still leave Stopping.
        using var cts = new CancellationTokenSource();
        bridge.StopCancellationToTrigger = cts;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Session.StopAsync(cts.Token));
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task StopAsync_WhenCallerTokenCancelsInSessionProbeAfterSuccessfulStop_ShouldResolveStateWithoutStrandingStopping()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        // The bridge stop succeeds, then the caller token is canceled exactly when the
        // session runs its post-stop status probe; the probe must outlive the caller
        // token so the session never strands in Stopping.
        using var cts = new CancellationTokenSource();
        bridge.StatusProbeCancellationToTrigger = cts;

        var result = await fixture.Session.StopAsync(cts.Token);

        Assert.True(result.Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task StopAsync_WhenCallerTokenCancelsInSessionProbeAfterFailedStop_ShouldResolveStateWithoutStrandingStopping()
    {
        var bridge = new ScriptedBridge
        {
            StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        bridge.StopCompletion.SetResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "stop failed")));
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        using var cts = new CancellationTokenSource();
        bridge.StatusProbeCancellationToTrigger = cts;

        var result = await fixture.Session.StopAsync(cts.Token);

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.StopFailed, result.Error?.Code);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task ConnectAsync_WhenCallerTokenCancelsInSessionProbeAfterFailedConnect_ShouldResolveStateWithoutStrandingConnecting()
    {
        var bridge = new ScriptedBridge
        {
            ConnectResult = CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed")),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        using var cts = new CancellationTokenSource();
        bridge.StatusProbeCancellationToTrigger = cts;

        var result = await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null, null, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.ConnectFailed, result.Error?.Code);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task StartAsync_WhenCallerTokenCancelsInSessionProbeAfterFailedStart_ShouldResolveStateAndReturnFailure()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        // Legacy session StartAsync without appended tasks: the bridge start fails and
        // the caller token is canceled exactly in the failure-state status probe.
        using var cts = new CancellationTokenSource();
        bridge.StatusProbeCancellationToTrigger = cts;

        var result = await fixture.Session.StartAsync(cts.Token);

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.StartFailed, result.Error?.Code);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task ApplyInstanceOptionsAsync_OnAbandonedNativeStop_ShouldRecoverAndRetryOnce()
    {
        var bridge = new ScriptedBridge();
        bridge.ApplyInstanceOptionsResults.Enqueue(CoreResult<bool>.Fail(CreateAbandonedStopError()));
        bridge.ApplyInstanceOptionsResults.Enqueue(CoreResult<bool>.Ok(true));
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        var result = await fixture.Session.ApplyInstanceOptionsAsync(new CoreInstanceOptions(TouchMode: "minitouch"));

        Assert.True(result.Success);
        Assert.Equal(1, bridge.RecoverFromAbandonedStopCallCount);
        Assert.Equal(2, bridge.ApplyInstanceOptionsCallCount);
    }

    [Fact]
    public async Task ConnectAsync_OnAbandonedNativeStop_ShouldRecoverFreshInstanceAndConnectNewInfo()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Fail(CreateAbandonedStopError()));
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Ok(true));

        var result = await fixture.Session.ConnectAsync("127.0.0.1:5556", "General", "/tmp/adb-new");

        Assert.True(result.Success);
        Assert.Equal(1, bridge.RecoverFromAbandonedStopCallCount);
        Assert.Equal("127.0.0.1:5556", bridge.LastConnectionInfo?.Address);
        Assert.Equal("/tmp/adb-new", bridge.LastConnectionInfo?.AdbPath);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task SessionCallback_StaleConnectedOrReconnected_ShouldNotRestoreIdleOrOverwriteCurrentConnection()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555","config":"General"}}""",
            DateTimeOffset.UtcNow));
        await Task.Delay(100);

        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5556", "General", null)).Success);
        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Reconnected","details":{"address":"127.0.0.1:5555","config":"General"}}""",
            DateTimeOffset.UtcNow));
        await Task.Delay(100);

        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void NativeConnectPending_ShouldWaitForAsyncCompletion_WhenConnectedCallbackArrivesFirst()
    {
        var connected = new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555"}}""",
            DateTimeOffset.UtcNow);
        var asyncComplete = new CoreCallbackEvent(
            4,
            "AsyncCallInfo",
            """{"async_call_id":7,"what":"Connect","details":{"ret":true,"cost":12000}}""",
            DateTimeOffset.UtcNow);

        var earlyResult = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, connected);
        Assert.Null(earlyResult);

        var completedResult = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, connected, asyncComplete);
        Assert.NotNull(completedResult);
        Assert.True(completedResult.Success);
    }

    [Fact]
    public void NativeConnectPending_ShouldCompleteSuccess_WhenAsyncCallArrivesFirst()
    {
        var asyncComplete = new CoreCallbackEvent(
            4,
            "AsyncCallInfo",
            """{"async_call_id":7,"what":"Connect","details":{"ret":true,"cost":12000}}""",
            DateTimeOffset.UtcNow);
        var connected = new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555"}}""",
            DateTimeOffset.UtcNow);

        var earlyResult = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, asyncComplete);
        Assert.Null(earlyResult);

        var completedResult = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, asyncComplete, connected);
        Assert.NotNull(completedResult);
        Assert.True(completedResult.Success);
    }

    [Fact]
    public void NativeConnectPending_ShouldStayPending_WhenScreencapProbeDelaysAsyncCallInfo()
    {
        var connected = new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """
            {"what":"Connected","details":{"adb":"/Users/halo/Documents/code/MaaAssistantArknights/publish/bin/platform-tools/adb","address":"192.168.0.252:5555","config":"General"}}
            """,
            DateTimeOffset.UtcNow);
        var screencapProbeTelemetry = new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"FastestWayToScreencap","details":{"method":"RawByNetcat","cost":28643}}""",
            DateTimeOffset.UtcNow);

        var result = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, connected, screencapProbeTelemetry);

        Assert.Null(result);
    }

    [Fact]
    public void NativeConnectPending_ShouldPreserveAsyncFailure_WhenConnectedArrivedFirst()
    {
        var connected = new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555"}}""",
            DateTimeOffset.UtcNow);
        var asyncFailure = new CoreCallbackEvent(
            4,
            "AsyncCallInfo",
            """{"async_call_id":7,"what":"Connect","details":{"ret":false,"cost":12000,"why":"probe failed"}}""",
            DateTimeOffset.UtcNow);

        var result = MaaCoreBridgeNative.ApplyConnectCallbacksForTest(7, connected, asyncFailure);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.ConnectFailed, result.Error?.Code);
        Assert.Contains("ret=false", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("probe failed", result.Error?.NativeDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeConnectPending_InvalidAsyncCallId_ShouldCompleteFailure()
    {
        var result = MaaCoreBridgeNative.CompleteInvalidAsyncConnectStartForTest(0);

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.ConnectFailed, result.Error?.Code);
        Assert.Contains("invalid async call id", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeGetImage_ShouldGrowBuffer_WhenNullSizeIndicatesBufferTooSmall()
    {
        var nullSize = ulong.MaxValue;
        var bufferSizes = new List<ulong>();

        var result = MaaCoreBridgeNative.ReadImageBytesWithDynamicBufferForTest(
            nullSize,
            initialBufferSize: 16,
            (buffer, bufferSize) =>
            {
                bufferSizes.Add(bufferSize);
                if (bufferSize < 64)
                {
                    return nullSize;
                }

                Marshal.WriteByte(buffer, 0, 0x12);
                Marshal.WriteByte(buffer, 1, 0x34);
                Marshal.WriteByte(buffer, 2, 0x56);
                return 3;
            });

        Assert.True(result.Success);
        Assert.Equal(new[] { 16UL, 32UL, 64UL }, bufferSizes);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, result.Value);
    }

    [Fact]
    public void NativeGetImageBgr_ShouldUseSameNullSizeGrowthSemantics()
    {
        var nullSize = ulong.MaxValue;
        var bufferSizes = new List<ulong>();

        var result = MaaCoreBridgeNative.ReadImageBytesWithDynamicBufferForTest(
            nullSize,
            initialBufferSize: 8,
            (buffer, bufferSize) =>
            {
                bufferSizes.Add(bufferSize);
                if (bufferSize < 32)
                {
                    return nullSize;
                }

                Marshal.WriteByte(buffer, 0, 0xA1);
                Marshal.WriteByte(buffer, 1, 0xB2);
                return 2;
            });

        Assert.True(result.Success);
        Assert.Equal(new[] { 8UL, 16UL, 32UL }, bufferSizes);
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, result.Value);
    }

    [Fact]
    public async Task SessionCallback_ShouldNotEnterConnected_WhileConnectIsPending()
    {
        var bridge = new ScriptedBridge
        {
            BlockConnect = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        var connectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb");
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Connected","details":{"address":"127.0.0.1:5555","config":"General","adb":"/tmp/adb"}}""",
            DateTimeOffset.UtcNow));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);
        Assert.False(connectTask.IsCompleted);

        bridge.ConnectCompletion.SetResult(true);
        Assert.True((await connectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SessionCallback_DisconnectWhileRunning_ShouldStopNativeAndMoveIdle()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb")).Success);
        Assert.True((await fixture.Session.StartAsync()).Success);

        bridge.PublishCallback(ConnectionCallback("Disconnect", "127.0.0.1:5555", "General", "/tmp/adb"));
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("UnsupportedResolution")]
    [InlineData("ResolutionError")]
    [InlineData("TouchModeNotAvailable")]
    public async Task SessionCallback_KnownFailureCallbacks_ShouldInvalidateConnection(string what)
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb")).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

        bridge.PublishCallback(ConnectionCallback(what, "127.0.0.1:5555", "General", "/tmp/adb"));
        await WaitUntilAsync(() => fixture.Session.CurrentState == SessionState.Idle);

        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);
        Assert.False(fixture.Session.IsConnectedWith(new CoreConnectionInfo("127.0.0.1:5555", "General", "/tmp/adb")));
        Assert.Equal(what, fixture.Session.LastConnectionCallbackStatus?.What);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task SessionCallback_StaleFailureCallbackForOtherTarget_ShouldKeepConnection()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb")).Success);

        bridge.PublishCallback(ConnectionCallback("ResolutionError", "127.0.0.1:9999", "General", "/tmp/adb-old"));
        await Task.Delay(150);

        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.Equal("127.0.0.1:5555", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task SessionCallback_StaleDisconnectDuringPendingConnect_ShouldKeepNewTarget()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb-old")).Success);

        bridge.BlockConnect = true;
        bridge.ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectTask = fixture.Session.ConnectAsync("127.0.0.1:5556", "General", "/tmp/adb-new");
        await WaitUntilAsync(() => bridge.LastConnectionInfo?.Address == "127.0.0.1:5556");
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        bridge.PublishCallback(ConnectionCallback("Disconnect", "127.0.0.1:5555", "General", "/tmp/adb-old"));
        await Task.Delay(150);
        Assert.Equal(SessionState.Connecting, fixture.Session.CurrentState);

        bridge.ConnectCompletion.SetResult(true);
        Assert.True((await reconnectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task SessionCallback_MatchingDisconnectDuringPendingConnect_ShouldClearTarget()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        bridge.BlockConnect = true;
        bridge.ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb");
        await WaitUntilAsync(() => bridge.LastConnectionInfo?.Address == "127.0.0.1:5555");

        bridge.PublishCallback(ConnectionCallback("Disconnect", "127.0.0.1:5555", "General", "/tmp/adb"));
        await WaitUntilAsync(() => fixture.Session.CurrentState == SessionState.Idle);

        // MaaCoreBridgeNative.TryHandlePendingConnectCallback fails a pending connect when a
        // Disconnect callback arrives, so mirror that here instead of completing successfully.
        bridge.ConnectResult = CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "disconnect during connect"));
        bridge.ConnectCompletion.SetResult(true);
        Assert.False((await connectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
        Assert.Null(fixture.Session.LastSuccessfulConnectionInfo);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task SessionCallback_DisconnectNativeStop_ShouldGateImmediateReconnect()
    {
        var bridge = new ScriptedBridge
        {
            StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb")).Success);
        Assert.True((await fixture.Session.StartAsync()).Success);
        var connectCallsBefore = bridge.ConnectCallCount;

        bridge.PublishCallback(ConnectionCallback("Disconnect", "127.0.0.1:5555", "General", "/tmp/adb"));
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);

        var reconnectTask = fixture.Session.ConnectAsync("127.0.0.1:5556", "General", "/tmp/adb");
        await Task.Delay(300);
        Assert.Equal(connectCallsBefore, bridge.ConnectCallCount);

        bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
        Assert.True((await reconnectTask.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(connectCallsBefore + 1, bridge.ConnectCallCount);
        Assert.Equal("127.0.0.1:5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task StartAsync_ShouldWaitForPendingNativeStopFromDisconnect()
    {
        var bridge = new ScriptedBridge
        {
            StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = RunCallbackPumpUntilCanceledAsync(fixture.Session, pumpCts.Token);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/adb")).Success);
        Assert.True((await fixture.Session.StartAsync()).Success);
        Assert.Equal(1, bridge.StartCallCount);

        bridge.PublishCallback(ConnectionCallback("Disconnect", "127.0.0.1:5555", "General", "/tmp/adb"));
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var startTask = fixture.Session.StartAsync();
        await Task.Delay(300);
        Assert.Equal(1, bridge.StartCallCount);

        bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
        Assert.True((await startTask.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(2, bridge.StartCallCount);

        pumpCts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task SessionCallback_ConnectedDuringPendingConnect_ShouldProjectActualAddressAdbAndStatus()
    {
        var bridge = new ScriptedBridge
        {
            BlockConnect = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        var connectTask = fixture.Session.ConnectAsync("127.0.0.1:5555", "General", "/tmp/requested-adb");
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.PublishCallback(ConnectionCallback("Connected", "emulator-5556", "General", "/tmp/actual-adb"));
        await WaitUntilAsync(() => fixture.Session.LastConnectionCallbackStatus?.AdbPath == "/tmp/actual-adb");
        bridge.ConnectCompletion.SetResult(true);

        Assert.True((await connectTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal("emulator-5556", fixture.Session.LastSuccessfulConnectionInfo?.Address);
        Assert.Equal("/tmp/actual-adb", fixture.Session.LastSuccessfulConnectionInfo?.AdbPath);
        Assert.Equal("emulator-5556", fixture.Session.LastConnectionCallbackStatus?.Address);
        Assert.Equal("Connected", fixture.Session.LastConnectionCallbackStatus?.What);

        bridge.PublishCallback(ConnectionCallback("Reconnected", "emulator-5558", "General", "/tmp/reconnected-adb"));
        await WaitUntilAsync(() => fixture.Session.LastSuccessfulConnectionInfo?.Address == "emulator-5558");
        Assert.Equal("/tmp/reconnected-adb", fixture.Session.LastSuccessfulConnectionInfo?.AdbPath);
        Assert.Equal("emulator-5558", fixture.Session.LastConnectionCallbackStatus?.Address);
        Assert.Equal("Reconnected", fixture.Session.LastConnectionCallbackStatus?.What);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SessionCallback_ShouldProjectReconnectResolutionAndScreencapMethod()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"Reconnecting","details":{"times":1}}""",
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => fixture.Session.LastConnectionCallbackStatus?.What == "Reconnecting");
        Assert.Equal(2, fixture.Session.LastConnectionCallbackStatus?.ReconnectAttempt);

        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"ResolutionGot","details":{"width":1920,"height":1080}}""",
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => fixture.Session.LastConnectionCallbackStatus?.What == "ResolutionGot");
        Assert.Equal(1920, fixture.Session.LastConnectionCallbackStatus?.Width);
        Assert.Equal(1080, fixture.Session.LastConnectionCallbackStatus?.Height);

        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"FastestWayToScreencap","details":{"method":"MumuExtras","cost":12}}""",
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => fixture.Session.LastConnectionCallbackStatus?.What == "FastestWayToScreencap");
        Assert.Equal("MumuExtras", fixture.Session.LastConnectionCallbackStatus?.Method);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ConnectCandidates_Success_ShouldDisableOneShotAutoDetectButPreserveAlwaysDetect()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["AutoDetect"] = JsonValue.Create(true);
        profile.Values["AlwaysAutoDetect"] = JsonValue.Create(false);
        var connect = new ConnectFeatureService(fixture.Session, fixture.Config);

        var result = await connect.ConnectCandidatesAsync(
            [new CoreConnectionInfo("127.0.0.1:5555", "General", null, Timeout: TimeSpan.FromSeconds(5))]);

        Assert.True(result.Success);
        Assert.False(profile.Values["AutoDetect"]?.GetValue<bool>());
        Assert.False(profile.Values["AlwaysAutoDetect"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ConnectCandidates_WhenAutoDetectSaveFails_ShouldKeepSuccessfulConnection()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["AutoDetect"] = JsonValue.Create(true);
        profile.Values["AlwaysAutoDetect"] = JsonValue.Create(false);
        fixture.ConfigStore.FailSaves = true;
        var connect = new ConnectFeatureService(fixture.Session, fixture.Config, fixture.Log);

        var result = await connect.ConnectCandidatesAsync(
            [new CoreConnectionInfo("127.0.0.1:5555", "General", null, Timeout: TimeSpan.FromSeconds(5))]);

        Assert.True(result.Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.False(profile.Values["AutoDetect"]?.GetValue<bool>());
        Assert.Contains(
            fixture.Log.Snapshot,
            entry => entry.Level == "WARN"
                && entry.Message.Contains("one-shot auto-detect could not be persisted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildConnectionCandidates_ShouldNormalizeAsciiSpacesAndLimitEachCandidateTimeout()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var connect = new ConnectFeatureService(fixture.Session, fixture.Config);

        var result = connect.BuildConnectionCandidates(
            " 127.0. 0.1 : 5555 ",
            " BlueStacks ",
            @" C:\Program Files\Android\adb.exe ",
            new CoreConnectionExtras(MuMu12EmulatorPath: @"C:\Program Files\Netease\MuMu"),
            autoDetect: false,
            timeout: TimeSpan.FromSeconds(20));

        var candidate = Assert.Single(result.Value!);
        Assert.Equal("127.0.0.1:5555", candidate.Address);
        Assert.Equal("BlueStacks", candidate.ConnectConfig);
        Assert.Equal(@"C:\Program Files\Android\adb.exe", candidate.AdbPath);
        Assert.Equal(@"C:\Program Files\Netease\MuMu", candidate.Extras?.MuMu12EmulatorPath);
        Assert.Equal(TimeSpan.FromSeconds(5), candidate.Timeout);
    }

    [Fact]
    public async Task ScreenshotTest_ShouldReturnFastestScreencapMethodFromConnectionCallback()
    {
        var bridge = new ScriptedBridge
        {
            BlockConnect = true,
            ConnectCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["AutoDetect"] = JsonValue.Create(false);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        var testTask = connect.RunScreenshotTestAsync(
            [new CoreConnectionInfo(
                "127.0.0.1:5555",
                "MuMuEmulator12",
                null,
                new CoreConnectionExtras(MuMu12ExtrasEnabled: true),
                TimeSpan.FromSeconds(5))],
            sampleCount: 1);
        await bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"FastestWayToScreencap","details":{"method":"MumuExtras","cost":12}}""",
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => fixture.Session.LastConnectionCallbackStatus?.Method == "MumuExtras");
        bridge.ConnectCompletion.SetResult(true);

        var result = await testTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Equal("MumuExtras", result.FastestScreencapMethod);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ScreenshotTest_ShouldRejectWrongExtrasMethodReportedDuringCapture()
    {
        var bridge = new ScriptedBridge
        {
            ScreencapCallback = new CoreCallbackEvent(
                2,
                "ConnectionInfo",
                """{"what":"FastestWayToScreencap","details":{"method":"RawByNetcat","cost":12}}""",
                DateTimeOffset.UtcNow),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["AutoDetect"] = JsonValue.Create(false);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        var result = await connect.RunScreenshotTestAsync(
            [new CoreConnectionInfo(
                "127.0.0.1:5555",
                "MuMuEmulator12",
                null,
                new CoreConnectionExtras(MuMu12ExtrasEnabled: true),
                TimeSpan.FromSeconds(5))],
            sampleCount: 1);

        Assert.False(result.Success);
        Assert.Equal("RawByNetcat", result.FastestScreencapMethod);
        Assert.Contains("Expected MumuExtras, actual RawByNetcat", result.Result.Message, StringComparison.Ordinal);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }


    [Fact]
    public void EmulatorRuntimeDiscovery_ShouldMapWpfProcessNamesAndResolveBundledAdb()
    {
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("HD-Player", out var blueStacks));
        Assert.Equal("BlueStacks", blueStacks);
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("dnplayer", out var ldPlayer));
        Assert.Equal("LDPlayer", ldPlayer);
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("Nox", out var nox));
        Assert.Equal("Nox", nox);
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("MuMuPlayer", out var muMu));
        Assert.Equal("MuMuEmulator12", muMu);
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("MuMuNxDevice", out var muMuNx));
        Assert.Equal("MuMuEmulator12", muMuNx);
        Assert.True(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("MEmu", out var xyaz));
        Assert.Equal("XYAZ", xyaz);
        Assert.False(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName("chrome", out _));
        Assert.False(EmulatorRuntimeDiscovery.TryMapEmulatorProcessName(null, out _));

        var ldAdb = EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath(
            "LDPlayer",
            "/emu/LDPlayer/dnplayer.exe",
            path => string.Equals(path, "/emu/LDPlayer/adb.exe", StringComparison.Ordinal));
        Assert.Equal("/emu/LDPlayer/adb.exe", ldAdb);

        var blueStacksAdb = EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath(
            "BlueStacks",
            "/emu/BlueStacks_nxt/HD-Player.exe",
            path => string.Equals(path, "/emu/BlueStacks_nxt/Engine/ProgramFiles/HD-Adb.exe", StringComparison.Ordinal));
        Assert.Equal("/emu/BlueStacks_nxt/Engine/ProgramFiles/HD-Adb.exe", blueStacksAdb);

        var muMuAdb = EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath(
            "MuMuEmulator12",
            "/emu/MuMu Player 12/shell/MuMuPlayer.exe",
            path => string.Equals(path, "/emu/MuMu Player 12/vmonitor/bin/adb_server.exe", StringComparison.Ordinal));
        Assert.Equal("/emu/MuMu Player 12/vmonitor/bin/adb_server.exe", muMuAdb);

        Assert.Null(EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath("Nox", "/emu/Nox/bin/Nox.exe", _ => false));
        Assert.Null(EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath("Unknown", "/emu/x.exe", _ => true));
    }

    [Fact]
    public void EmulatorRuntimeDiscovery_ShouldDetectAndDeduplicateRunningEmulators()
    {
        var discovered = EmulatorRuntimeDiscovery.DetectRunningEmulators(
            processProvider: () =>
            [
                new EmulatorProcessSnapshot("chrome", "/usr/bin/chrome"),
                new EmulatorProcessSnapshot("MuMuPlayer", "/emu/MuMu Player 12/shell/MuMuPlayer.exe"),
                new EmulatorProcessSnapshot("MuMuNxDevice", "/emu/MuMu Player 12/shell/MuMuNxDevice.exe"),
            ],
            fileExists: path => string.Equals(path, "/emu/MuMu Player 12/vmonitor/bin/adb_server.exe", StringComparison.Ordinal));

        var emulator = Assert.Single(discovered);
        Assert.Equal("MuMuEmulator12", emulator.EmulatorId);
        Assert.Equal("/emu/MuMu Player 12/vmonitor/bin/adb_server.exe", emulator.AdbPath);
    }

    [Fact]
    public void EmulatorRuntimeDiscovery_WhenMultipleBundledAdbExist_ShouldPreferLastWpfListedPath()
    {
        // WPF parity (WinAdapter.RefreshEmulatorsInfo): the loop overwrites the mapping for
        // every existing relative path, so the last existing entry in the WPF list wins.
        var blueStacksAdb = EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath(
            "BlueStacks",
            "/emu/BlueStacks_nxt/HD-Player.exe",
            path => path is "/emu/BlueStacks_nxt/HD-Adb.exe"
                or "/emu/BlueStacks_nxt/Engine/ProgramFiles/HD-Adb.exe");
        Assert.Equal("/emu/BlueStacks_nxt/Engine/ProgramFiles/HD-Adb.exe", blueStacksAdb);

        var muMuAdb = EmulatorRuntimeDiscovery.ResolveEmulatorAdbPath(
            "MuMuEmulator12",
            "/emu/MuMu Player 12/shell/MuMuPlayer.exe",
            path => path is "/emu/MuMu Player 12/vmonitor/bin/adb_server.exe"
                or "/emu/MuMu Player 12/shell/adb.exe");
        Assert.Equal("/emu/MuMu Player 12/shell/adb.exe", muMuAdb);
    }

    [Fact]
    public async Task RuntimeCandidateExpansion_WithEmptyAdbPath_ShouldAdoptDiscoveredEmulatorConfigAndBundledAdb()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false)
        {
            EmulatorDiscoveryOverride = () =>
            [
                new EmulatorRuntimeInfo(
                    "MuMuEmulator12",
                    "/emu/MuMu Player 12/shell/MuMuPlayer.exe",
                    "/emu/MuMu Player 12/nx_main/adb.exe"),
            ],
        };

        var expanded = await connect.ExpandRuntimeConnectionCandidatesAsync(
            [new CoreConnectionInfo(
                "127.0.0.1:16384",
                "General",
                null,
                Timeout: TimeSpan.FromSeconds(5),
                DiscoverRuntimeCandidates: true)],
            CancellationToken.None);

        var template = Assert.Single(expanded);
        Assert.Equal("127.0.0.1:16384", template.Address);
        Assert.Equal("MuMuEmulator12", template.ConnectConfig);
        Assert.Equal("/emu/MuMu Player 12/nx_main/adb.exe", template.AdbPath);
    }

    [Fact]
    public async Task RuntimeCandidateExpansion_WhenDiscoveryFindsNoEmulator_ShouldKeepOriginalCandidates()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false)
        {
            EmulatorDiscoveryOverride = () => [],
        };

        // Use a nonexistent ADB path so the runtime precheck cannot observe host-attached
        // devices; this test must stay hermetic on machines where `adb` is on PATH.
        var candidates = (IReadOnlyList<CoreConnectionInfo>)
        [
            new CoreConnectionInfo(
                "127.0.0.1:5555",
                "General",
                "/nonexistent/m05-test-adb",
                Timeout: TimeSpan.FromSeconds(5),
                DiscoverRuntimeCandidates: true),
            new CoreConnectionInfo(
                "127.0.0.1:5556",
                "General",
                "/nonexistent/m05-test-adb",
                Timeout: TimeSpan.FromSeconds(5),
                DiscoverRuntimeCandidates: true),
        ];
        var expanded = await connect.ExpandRuntimeConnectionCandidatesAsync(candidates, CancellationToken.None);

        Assert.Equal(2, expanded.Count);
        Assert.Equal("127.0.0.1:5555", expanded[0].Address);
        Assert.Equal("/nonexistent/m05-test-adb", expanded[0].AdbPath);
        Assert.Equal("General", expanded[0].ConnectConfig);
    }

    [Fact]
    public async Task RuntimeCandidateExpansion_WhenRuntimeDiscoveryNotRequested_ShouldNotInvokeEmulatorDiscovery()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var discoveryCalls = 0;
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false)
        {
            EmulatorDiscoveryOverride = () =>
            {
                discoveryCalls++;
                return [new EmulatorRuntimeInfo("MuMuEmulator12", null, "/emu/mumu-adb.exe")];
            },
        };

        var expanded = await connect.ExpandRuntimeConnectionCandidatesAsync(
            [new CoreConnectionInfo("127.0.0.1:5555", "General", null, Timeout: TimeSpan.FromSeconds(5))],
            CancellationToken.None);

        Assert.Equal(0, discoveryCalls);
        var candidate = Assert.Single(expanded);
        Assert.Null(candidate.AdbPath);
        Assert.Equal("General", candidate.ConnectConfig);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_WhenSupersededDuringIdleWait_ShouldSkipBridgeReload()
    {
        var bridge = new ScriptedBridge
        {
            RuntimeRunningOverride = true,
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        var staleReload = fixture.Session.ReloadResourceWhenIdleAsync(
            "YoStarEN",
            waitTimeout: TimeSpan.FromSeconds(10),
            isStillRequested: () => false);
        await Task.Delay(400);
        Assert.False(staleReload.IsCompleted);
        Assert.Equal(0, bridge.ReloadResourceCallCount);

        bridge.RuntimeRunningOverride = false;
        Assert.True((await staleReload.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(0, bridge.ReloadResourceCallCount);

        var latestReload = await fixture.Session.ReloadResourceWhenIdleAsync(
            "YoStarJP",
            isStillRequested: () => true);
        Assert.True(latestReload.Success);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
        Assert.Equal("YoStarJP", bridge.LastReloadClientType);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_ConcurrentRequests_ShouldSerializeBridgeReloadsInRequestOrder()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        bridge.ReloadResourceCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstReload = fixture.Session.ReloadResourceWhenIdleAsync("Official", isStillRequested: () => true);
        await bridge.ReloadResourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondReload = fixture.Session.ReloadResourceWhenIdleAsync("YoStarEN", isStillRequested: () => true);
        await Task.Delay(300);

        // The second request must not enter the bridge while the first reload is in flight.
        Assert.Equal(1, bridge.ReloadResourceCallCount);

        bridge.ReloadResourceCompletion.TrySetResult(true);
        Assert.True((await firstReload.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await secondReload.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(2, bridge.ReloadResourceCallCount);

        // The later request must be the terminal reload; an older request may never finish last.
        Assert.Equal(new[] { "Official", "YoStarEN" }, bridge.ReloadedClientTypes);
        Assert.Equal("YoStarEN", bridge.LastReloadClientType);
    }

    [Fact]
    public async Task ReloadResourceWhenIdleAsync_WhenSupersededBehindInFlightReload_ShouldSkipBridgeReload()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        bridge.ReloadResourceCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstReload = fixture.Session.ReloadResourceWhenIdleAsync("Official", isStillRequested: () => true);
        await bridge.ReloadResourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var supersededReload = fixture.Session.ReloadResourceWhenIdleAsync("YoStarEN", isStillRequested: () => false);
        await Task.Delay(300);

        bridge.ReloadResourceCompletion.TrySetResult(true);
        Assert.True((await firstReload.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await supersededReload.WaitAsync(TimeSpan.FromSeconds(5))).Success);

        // The superseded request re-validates the gate after the in-flight reload completes.
        Assert.Equal(1, bridge.ReloadResourceCallCount);
        Assert.Equal("Official", bridge.LastReloadClientType);
    }

    [Fact]
    public async Task ScreenshotTest_ShouldWaitForDelayedFastestMethodCallbackProjection()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["AutoDetect"] = JsonValue.Create(false);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            fixture.Root,
            enableQuickConnectionPrecheck: false);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

        var testTask = connect.RunScreenshotTestAsync(
            [new CoreConnectionInfo(
                "127.0.0.1:5555",
                "MuMuEmulator12",
                null,
                new CoreConnectionExtras(MuMu12ExtrasEnabled: true),
                TimeSpan.FromSeconds(5))],
            sampleCount: 1);
        await WaitUntilAsync(() => bridge.ImageBgrCallCount >= 1);

        // Simulate the FastestWayToScreencap envelope still queued behind the last forced capture.
        await Task.Delay(250);
        bridge.PublishCallback(new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            """{"what":"FastestWayToScreencap","details":{"method":"MumuExtras","cost":12}}""",
            DateTimeOffset.UtcNow));

        var result = await testTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Result.Message);
        Assert.Equal("MumuExtras", result.FastestScreencapMethod);

        await pumpCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void RuntimeCandidateDiscovery_ShouldParseBlueStacksPortAndReadyAdbSerials()
    {
        var blueStacksAddress = ConnectFeatureService.ParseBlueStacksDynamicAddress(
            [
                "bst.installed_images=\"Nougat64,Pie64\"",
                "bst.instance.Nougat64.status.adb_port=\"61234\"",
            ],
            configuredKeyword: null);
        var serials = ConnectFeatureService.ParseReadyAdbSerials(
            """
            List of devices attached
            emulator-5556 device product:sdk model:sdk transport_id:1
            127.0.0.1:16384 offline
            192.168.1.9:5555 device product:test
            1234567890ABCDEF device
            """);

        Assert.Equal("127.0.0.1:61234", blueStacksAddress);
        Assert.Equal(["emulator-5556", "192.168.1.9:5555"], serials);
    }

    [Fact]
    public void ParseReadyAdbSerials_ShouldSkipEmulator5554OnlyWhenMultipleDevicesOnline()
    {
        // WPF parity (DetectAdbConfig): with multiple online devices the default first console
        // serial emulator-5554 is skipped, but it stays a valid candidate as the only device.
        var single = ConnectFeatureService.ParseReadyAdbSerials(
            """
            List of devices attached
            emulator-5554 device product:sdk model:sdk transport_id:1
            """);
        Assert.Equal(["emulator-5554"], single);

        var multiple = ConnectFeatureService.ParseReadyAdbSerials(
            """
            List of devices attached
            emulator-5554 device product:sdk model:sdk transport_id:1
            emulator-5556 device product:sdk model:sdk transport_id:2
            """);
        Assert.Equal(["emulator-5556"], multiple);

        var placeholderOnly = ConnectFeatureService.ParseReadyAdbSerials(
            """
            List of devices attached
            emulator-5554 device
            1234567890ABCDEF device
            """);
        Assert.Empty(placeholderOnly);
    }

    [Fact]
    public async Task RemoteControlEnsureConnected_ShouldCompareEffectiveAdbPath_WhenBundledAdbEnabled()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(true);
        MacBundledAdbPolicy.MarkCurrentTermsAccepted(fixture.Config.CurrentConfig, DateTimeOffset.UtcNow);

        var bundledAdbPath = MacBundledAdbPolicy.ResolveBundledAdbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(bundledAdbPath)!);
        await File.WriteAllTextAsync(bundledAdbPath, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                bundledAdbPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var currentConnection = new CoreConnectionInfo(
            "127.0.0.1:5555",
            "General",
            bundledAdbPath,
            new CoreConnectionExtras(
                MacUseBundledAdb: true,
                TouchMode: "minitouch",
                AdbLiteEnabled: false,
                KillAdbOnExit: false));
        Assert.True((await fixture.Session.ConnectAsync(currentConnection)).Success);

        var connectFeature = new RecordingConnectFeature([currentConnection]);
        var dispatcherType = typeof(MacBundledAdbPolicy).Assembly.GetType(
            "MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = Activator.CreateInstance(
            dispatcherType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [fixture.Config, fixture.Session, connectFeature, null, null, bridge, new UiLogService()],
            culture: null);
        Assert.NotNull(dispatcher);
        var ensureConnected = dispatcherType.GetMethod(
            "EnsureConnectedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(ensureConnected);

        var resultTask = (Task<UiOperationResult>)ensureConnected.Invoke(dispatcher, [CancellationToken.None])!;
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(0, connectFeature.ConnectCallCount);
    }

    [Fact]
    public async Task RemoteControlEnsureConnected_ShouldReconnectWhenConnectionExtrasSignatureChanges()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["ConnectAddress"] = JsonValue.Create("127.0.0.1:5555");
        profile.Values["ConnectConfig"] = JsonValue.Create("General");
        profile.Values["ClientType"] = JsonValue.Create("YoStarEN");
        profile.Values["TouchMode"] = JsonValue.Create("minitouch");
        profile.Values["AttachWindowMouseMethod"] = JsonValue.Create("32");

        var connectedInfo = new CoreConnectionInfo(
            "127.0.0.1:5555",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AttachWindowMouseMethod: "16",
                ClientType: "YoStarEN"));
        Assert.True((await fixture.Session.ConnectAsync(connectedInfo)).Success);

        var candidates = new ConnectFeatureService(fixture.Session, fixture.Config)
            .BuildCurrentProfileConnectionCandidates(includeConfiguredAddress: true);
        Assert.True(candidates.Success);
        Assert.NotNull(candidates.Value);
        var connectFeature = new RecordingConnectFeature(candidates.Value!);

        var dispatcherType = typeof(MacBundledAdbPolicy).Assembly.GetType(
            "MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = Activator.CreateInstance(
            dispatcherType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [fixture.Config, fixture.Session, connectFeature, null, null, bridge, new UiLogService()],
            culture: null);
        Assert.NotNull(dispatcher);
        var ensureConnected = dispatcherType.GetMethod(
            "EnsureConnectedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(ensureConnected);

        var resultTask = (Task<UiOperationResult>)ensureConnected.Invoke(dispatcher, [CancellationToken.None])!;
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(1, connectFeature.ConnectCallCount);
    }

    [Fact]
    public async Task RemoteControlEnsureConnected_ShouldUseUnifiedCandidateSignatureFromConnectFeature()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var candidate = new CoreConnectionInfo(
            "127.0.0.1:5555",
            "General",
            "/resolved/adb",
            new CoreConnectionExtras(
                TouchMode: "MaaFwAdb",
                AdbLiteEnabled: true,
                KillAdbOnExit: true,
                MacUseBundledAdb: false,
                MuMu12ExtrasEnabled: true,
                MuMu12EmulatorPath: "/opt/mumu",
                MuMuBridgeConnection: true,
                MuMu12Index: "2",
                LdPlayerExtrasEnabled: true,
                LdPlayerEmulatorPath: "/opt/ld",
                LdPlayerManualSetIndex: true,
                LdPlayerIndex: "4",
                AttachWindowScreencapMethod: "2",
                AttachWindowMouseMethod: "32",
                AttachWindowKeyboardMethod: "64",
                ClientType: "YoStarEN"));
        var connectFeature = new RecordingConnectFeature([candidate]);

        var dispatcherType = typeof(MacBundledAdbPolicy).Assembly.GetType(
            "MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = Activator.CreateInstance(
            dispatcherType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [fixture.Config, fixture.Session, connectFeature, null, null, bridge, new UiLogService()],
            culture: null);
        Assert.NotNull(dispatcher);
        var ensureConnected = dispatcherType.GetMethod(
            "EnsureConnectedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(ensureConnected);

        var resultTask = (Task<UiOperationResult>)ensureConnected.Invoke(dispatcher, [CancellationToken.None])!;
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(1, connectFeature.ConnectCallCount);
        Assert.NotNull(connectFeature.LastConnectCandidates);
        var used = Assert.Single(connectFeature.LastConnectCandidates!);
        Assert.Equal(candidate.Address, used.Address);
        Assert.Equal(candidate.ConnectConfig, used.ConnectConfig);
        Assert.Equal(candidate.AdbPath, used.AdbPath);
        Assert.Equal(candidate.Extras, used.Extras);
    }

    [Fact]
    public async Task RemoteControlLinkStart_WhileSessionRunning_ShouldRejectWithoutConnectOrAppend()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var started = await fixture.Session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Fight", "running", true, "{}"));
        Assert.True(started.Success);
        Assert.Equal(SessionState.Running, fixture.Session.CurrentState);
        Assert.True(fixture.Session.TryResolveTaskIndexByCoreTaskId(1, out _));

        var connectFeature = new RecordingConnectFeature();
        var dispatcher = new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher(
            fixture.Config,
            fixture.Session,
            connectFeature,
            new TaskQueueFeatureService(fixture.Session, fixture.Config),
            toolboxFeatureService: null,
            bridge,
            new UiLogService());

        var result = await dispatcher.DispatchAsync(
            new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandRequest("LinkStart", null, "user", "device"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.SessionStateNotAllowed, result.ErrorCode);
        Assert.Equal(0, connectFeature.ConnectCallCount);
        Assert.Equal(SessionState.Running, fixture.Session.CurrentState);
        Assert.True(fixture.Session.TryResolveTaskIndexByCoreTaskId(1, out _));
    }

    [Fact]
    public async Task RemoteControlLinkStartSubset_OneShotRecruitExpediteSave_PersistsOriginalEnabledStates()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
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
                fixture.Config.CurrentConfig).Params,
        });
        await fixture.Config.SaveAsync();

        var currentConnection = new CoreConnectionInfo("127.0.0.1:5555", "General", null, new CoreConnectionExtras());
        Assert.True((await fixture.Session.ConnectAsync(currentConnection)).Success);

        var connectFeature = new RecordingConnectFeature([currentConnection])
        {
            AppendProfileTasksAndStartOverride = cancellationToken =>
                fixture.Session.AppendTasksFromCurrentProfileAndStartAsync(new CoreInstanceOptions(), cancellationToken),
        };
        var dispatcher = new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher(
            fixture.Config,
            fixture.Session,
            connectFeature,
            new TaskQueueFeatureService(fixture.Session, fixture.Config),
            toolboxFeatureService: null,
            bridge,
            new UiLogService());

        var result = await dispatcher.DispatchAsync(
            new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandRequest("LinkStart-Recruiting", null, "user", "device"));

        Assert.True(result.Success, result.Message);
        // In-memory enable states are always restored after the subset start.
        Assert.True(profile.TaskQueue[0].IsEnabled);
        Assert.True(profile.TaskQueue[1].IsEnabled);
        // The one-shot expedite was consumed in memory by the appended batch.
        Assert.False(profile.TaskQueue[1].Params["expedite"]?.GetValue<bool>());

        // The append transaction saved mid-window while the subset enable states were
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
        Assert.True(persistedProfile.TaskQueue[1].IsEnabled);
        Assert.False(persistedProfile.TaskQueue[1].Params.ContainsKey("expedite"));
        Assert.False(persistedProfile.TaskQueue[1].Params.ContainsKey("expedite_times"));
    }

    [Fact]
    public async Task RemoteControlGacha_WhileSessionRunning_ShouldRejectWithoutDispatchingTool()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var started = await fixture.Session.AppendAndStartCoreTaskAsync(new CoreTaskRequest("Fight", "running", true, "{}"));
        Assert.True(started.Success);
        Assert.Equal(SessionState.Running, fixture.Session.CurrentState);

        var toolboxFeature = new RecordingToolboxFeature();
        var dispatcher = new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandDispatcher(
            fixture.Config,
            fixture.Session,
            connectFeatureService: null,
            taskQueueFeatureService: null,
            toolboxFeature,
            bridge,
            new UiLogService());

        var result = await dispatcher.DispatchAsync(
            new MAAUnified.Application.Services.RemoteControl.RemoteControlCommandRequest("Toolbox-GachaOnce", null, "user", "device"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.SessionStateNotAllowed, result.ErrorCode);
        Assert.Equal(0, toolboxFeature.DispatchCallCount);
        Assert.Equal(SessionState.Running, fixture.Session.CurrentState);
        Assert.True(fixture.Session.TryResolveTaskIndexByCoreTaskId(1, out _));
    }

    [Fact]
    public async Task ConnectFeature_ShouldPassProfileConnectionExtras_ToBridge()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["ConnectConfig"] = JsonValue.Create("WSA");
        profile.Values["ClientType"] = JsonValue.Create("YoStarEN");
        profile.Values["TouchMode"] = JsonValue.Create("MaaFwAdb");
        profile.Values["AdbLiteEnabled"] = JsonValue.Create(true);
        profile.Values["KillAdbOnExit"] = JsonValue.Create(true);
        profile.Values["MuMu12ExtrasEnabled"] = JsonValue.Create(true);
        profile.Values["MuMu12EmulatorPath"] = JsonValue.Create("/opt/mumu");
        profile.Values["MuMuBridgeConnection"] = JsonValue.Create(true);
        profile.Values["MuMu12Index"] = JsonValue.Create("2");
        profile.Values["LdPlayerExtrasEnabled"] = JsonValue.Create(true);
        profile.Values["LdPlayerEmulatorPath"] = JsonValue.Create("/opt/ld");
        profile.Values["LdPlayerManualSetIndex"] = JsonValue.Create(true);
        profile.Values["LdPlayerIndex"] = JsonValue.Create("4");
        profile.Values["AttachWindowScreencapMethod"] = JsonValue.Create("2");
        profile.Values["AttachWindowMouseMethod"] = JsonValue.Create("32");
        profile.Values["AttachWindowKeyboardMethod"] = JsonValue.Create("2");

        var connect = new ConnectFeatureService(fixture.Session, fixture.Config);
        Assert.True((await connect.ConnectAsync("emulator-5554", "WSA", null)).Success);

        var extras = bridge.LastConnectionInfo?.Extras;
        Assert.NotNull(extras);
        Assert.Equal("YoStarEN", extras.ClientType);
        Assert.Equal("MaaFwAdb", extras.TouchMode);
        Assert.True(extras.AdbLiteEnabled);
        Assert.True(extras.KillAdbOnExit);
        Assert.True(extras.MuMu12ExtrasEnabled);
        Assert.Equal("/opt/mumu", extras.MuMu12EmulatorPath);
        Assert.True(extras.MuMuBridgeConnection);
        Assert.Equal("2", extras.MuMu12Index);
        Assert.True(extras.LdPlayerExtrasEnabled);
        Assert.Equal("/opt/ld", extras.LdPlayerEmulatorPath);
        Assert.True(extras.LdPlayerManualSetIndex);
        Assert.Equal("4", extras.LdPlayerIndex);
        Assert.Equal("2", extras.AttachWindowScreencapMethod);
        Assert.Equal("32", extras.AttachWindowMouseMethod);
        Assert.Equal("2", extras.AttachWindowKeyboardMethod);
    }

    [Fact]
    public async Task BuildCurrentProfileConnectionCandidates_OnMacRawByNcRisk_ShouldPreserveConfiguredRiskCombination()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["ConnectAddress"] = JsonValue.Create("192.168.0.252:5555");
        profile.Values["ConnectConfig"] = JsonValue.Create("General");
        profile.Values["TouchMode"] = JsonValue.Create("minitouch");
        profile.Values["AdbLiteEnabled"] = JsonValue.Create(false);
        profile.Values["AutoDetect"] = JsonValue.Create(false);
        fixture.Config.CurrentConfig.GlobalValues["CoreVersion"] = JsonValue.Create("v999.0.0");
        TouchMacMaaAdbControlUnitLibrary(fixture.Root);

        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            runtimeBaseDirectory: fixture.Root);
        var result = connect.BuildCurrentProfileConnectionCandidates(includeConfiguredAddress: true);

        Assert.True(result.Success);
        var candidate = Assert.Single(result.Value!);
        Assert.Equal("192.168.0.252:5555", candidate.Address);
        Assert.Equal("General", candidate.ConnectConfig);
        Assert.Equal("minitouch", candidate.Extras?.TouchMode);
        Assert.False(candidate.Extras?.AdbLiteEnabled);
        Assert.Null(candidate.Extras?.FallbackStrategy);
    }

    [Fact]
    public async Task ValidateAndConnectAsync_OnMacRawByNcRiskApplyRecommended_ShouldUpdateProfileAndContinue()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["TouchMode"] = JsonValue.Create("minitouch");
        profile.Values["AdbLiteEnabled"] = JsonValue.Create(false);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ApplyRecommended);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);

        var result = await connect.ValidateAndConnectAsync(new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(30)));

        Assert.True(result.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("Connect", prompt.LastPrompt?.SourceScope);
        Assert.Equal("minitouch", profile.Values["TouchMode"]?.GetValue<string>());
        Assert.True(profile.Values["AdbLiteEnabled"]?.GetValue<bool>());
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.True(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:applied-recommended-profile", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
        Assert.Contains(
            fixture.Log.Snapshot,
            entry => entry.Level == "WARN"
                && entry.Message.Contains("applied recommendation", StringComparison.Ordinal)
                && entry.Message.Contains("touch=minitouch", StringComparison.Ordinal)
                && entry.Message.Contains("adbLite=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAndConnectAsync_OnMacRawByNcRiskForceRun_ShouldKeepConfiguredCombinationAndRecordDiagnostic()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["TouchMode"] = JsonValue.Create("minitouch");
        profile.Values["AdbLiteEnabled"] = JsonValue.Create(false);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);

        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            fixture.Log,
            bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var result = await connect.ValidateAndConnectAsync(new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(30)));

        Assert.True(result.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("minitouch", profile.Values["TouchMode"]?.GetValue<string>());
        Assert.False(profile.Values["AdbLiteEnabled"]?.GetValue<bool>());
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.False(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
        Assert.Equal("user-forced-risk-combination", bridge.LastConnectionInfo?.Extras?.FallbackReason);
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.ConfiguredTouchMode);
        Assert.False(bridge.LastConnectionInfo?.Extras?.ConfiguredAdbLiteEnabled);
    }

    [Fact]
    public async Task ConnectCandidatesAsync_OnMacRawByNcRiskForceRun_ShouldPromptOnceForCandidateRetries()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "first candidate failed")));
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Ok(true));
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var candidates = new[]
        {
            new CoreConnectionInfo(
                "not-a-tcp-serial-1",
                "General",
                null,
                new CoreConnectionExtras(
                    TouchMode: "minitouch",
                    AdbLiteEnabled: false),
                TimeSpan.FromSeconds(30)),
            new CoreConnectionInfo(
                "not-a-tcp-serial-2",
                "General",
                null,
                new CoreConnectionExtras(
                    TouchMode: "minitouch",
                    AdbLiteEnabled: false),
                TimeSpan.FromSeconds(30)),
        };

        var result = await connect.ConnectCandidatesAsync(candidates);

        Assert.True(result.Success);
        Assert.Equal("not-a-tcp-serial-2", result.SuccessfulAddress);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.False(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
        Assert.Single(result.CandidateFailures);
    }

    [Fact]
    public async Task ConnectCandidatesAsync_OnMacRawByNcRiskForceRun_ShouldReuseDecisionAcrossOuterReconnects()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "first outer attempt failed")));
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Ok(true));
        bridge.ConnectResults.Enqueue(CoreResult<bool>.Ok(true));
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var candidate = new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(30));

        var first = await connect.ConnectCandidatesAsync([candidate]);
        var second = await connect.ConnectCandidatesAsync([candidate]);

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);

        var changedCandidate = candidate with
        {
            Extras = new CoreConnectionExtras(
                TouchMode: "maatouch",
                AdbLiteEnabled: false),
        };
        var third = await connect.ConnectCandidatesAsync([changedCandidate]);

        Assert.True(third.Success);
        Assert.Equal(2, prompt.CallCount);
        Assert.Equal("maatouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
    }

    [Fact]
    public async Task ValidateAndConnectAsync_OnMacRawByNcRiskCancelPrompt_ShouldNotConnectCore()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.Cancel);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);

        var result = await connect.ValidateAndConnectAsync(new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(20)));

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(1, prompt.CallCount);
        Assert.Null(bridge.LastConnectionInfo);
        Assert.Contains("prompt was closed", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunScreenshotTestAsync_OnMacRawByNcRiskApplyRecommended_ShouldUseCommonPromptAndContinue()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var adbPath = Path.Combine(fixture.Root, "adb");
        await File.WriteAllTextAsync(
            adbPath,
            "#!/bin/sh\n"
            + "case \"$1\" in\n"
            + "  -s) echo \"device\" ;;\n"
            + "  devices) echo \"List of devices attached\"; echo \"not-a-tcp-serial device\" ;;\n"
            + "esac\n"
            + "exit 0\n");
        File.SetUnixFileMode(
            adbPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var candidate = new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            adbPath,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(20));
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ApplyRecommended);

        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var result = await connect.RunScreenshotTestAsync([candidate], sampleCount: 1);

        Assert.True(result.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("ScreenshotTest", prompt.LastPrompt?.SourceScope);
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.True(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:applied-recommended-profile", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
    }

    [Fact]
    public async Task RunScreenshotTestAsync_OnMacRawByNcRiskForceRun_ShouldKeepConfiguredCombination()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);
        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var candidate = new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(30));

        var result = await connect.RunScreenshotTestAsync([candidate], sampleCount: 1);

        Assert.True(result.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("ScreenshotTest", prompt.LastPrompt?.SourceScope);
        Assert.Equal("not-a-tcp-serial", result.SuccessfulAddress);
        Assert.Equal("minitouch", bridge.LastConnectionInfo?.Extras?.TouchMode);
        Assert.False(bridge.LastConnectionInfo?.Extras?.AdbLiteEnabled);
        Assert.Equal("temporary-macos-rawbync-guard:force-run", bridge.LastConnectionInfo?.Extras?.FallbackStrategy);
        Assert.Equal("user-forced-risk-combination", bridge.LastConnectionInfo?.Extras?.FallbackReason);
    }

    [Fact]
    public async Task ConnectCandidatesAsync_WhenCoreRetFalseAfterPrecheck_ShouldIncludeAdbSerialDiagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var bridge = new ScriptedBridge
        {
            ConnectResult = CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.ConnectFailed,
                "AsstAsyncConnect callback reported ret=false.",
                """{"async_call_id":0,"what":"Connect","details":{"ret":false,"cost":0,"uuid":""}}""")),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var adbLog = Path.Combine(fixture.Root, "adb-commands.log");
        var adbPath = Path.Combine(fixture.Root, "adb");
        await File.WriteAllTextAsync(
            adbPath,
            "#!/bin/sh\n"
            + $"printf '%s ' \"$@\" >> '{adbLog}'\n"
            + $"printf '\\n' >> '{adbLog}'\n"
            + "case \"$1\" in\n"
            + "  connect) echo \"connected to $2\" ;;\n"
            + "  devices) echo \"List of devices attached\"; echo \"192.168.0.252:5555 device product:test\" ;;\n"
            + "  -s) echo \"device\" ;;\n"
            + "esac\n"
            + "exit 0\n");
        File.SetUnixFileMode(
            adbPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        TouchMacMaaAdbControlUnitLibrary(fixture.Root);

        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root);
        var candidate = new CoreConnectionInfo(
            address,
            "General",
            adbPath,
            new CoreConnectionExtras(
                TouchMode: "MaaFwAdb",
                AdbLiteEnabled: true),
            TimeSpan.FromSeconds(20));

        var result = await connect.ConnectCandidatesAsync([candidate]);

        Assert.False(result.Success);
        Assert.Contains("AsstAsyncConnect callback reported ret=false.", result.Result.Message, StringComparison.Ordinal);
        Assert.Contains("ADB state after core connect failure.", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("precheck=passed", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("adb connect", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("adb devices -l", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("adb serial get-state", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains($"serial={address}", result.Result.Error?.Details, StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("fallback=configured:MaaFwAdb/adbLite=True", result.Result.Error?.Details, StringComparison.Ordinal);
            Assert.Contains("effective=touch=MaaFwAdb,adbLite=True", result.Result.Error?.Details, StringComparison.Ordinal);
            Assert.Contains("extras=macBundledAdb=False,touch=MaaFwAdb,adbLite=True", result.Result.Error?.Details, StringComparison.Ordinal);
        }

        var commands = await File.ReadAllTextAsync(adbLog);
        Assert.Contains($"connect {address}", commands, StringComparison.Ordinal);
        Assert.Contains("devices -l", commands, StringComparison.Ordinal);
        Assert.Contains($"-s {address} get-state", commands, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectCandidatesAsync_OnMacRawByNcRiskForceRunRetFalse_ShouldReportForceDiagnostic()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bridge = new ScriptedBridge
        {
            ConnectResult = CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.ConnectFailed,
                "AsstAsyncConnect callback reported ret=false.",
                """{"async_call_id":0,"what":"Connect","details":{"ret":false,"cost":0,"uuid":""}}""")),
        };
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var prompt = new RecordingMacRawByNcPromptService(MacRawByNcRiskConnectionDecision.ForceRun);

        var connect = new ConnectFeatureService(
            fixture.Session,
            fixture.Config,
            bridge: bridge,
            runtimeBaseDirectory: fixture.Root,
            macRawByNcRiskPromptService: prompt);
        var candidate = new CoreConnectionInfo(
            "not-a-tcp-serial",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false),
            TimeSpan.FromSeconds(30));

        var result = await connect.ConnectCandidatesAsync([candidate]);

        Assert.False(result.Success);
        Assert.Equal(1, prompt.CallCount);
        Assert.NotNull(bridge.LastConnectionInfo);
        Assert.Contains("fallback=temporary-macos-rawbync-guard:force-run", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("reason=user-forced-risk-combination", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("configured=touch=minitouch,adbLite=False", result.Result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("effective=touch=minitouch,adbLite=False", result.Result.Error?.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsConnectedWith_ShouldIncludeConnectionExtrasSignature()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);
        var connectedInfo = new CoreConnectionInfo(
            "127.0.0.1:5555",
            "General",
            null,
            new CoreConnectionExtras(
                TouchMode: "minitouch",
                AdbLiteEnabled: false,
                KillAdbOnExit: false,
                MacUseBundledAdb: false,
                MuMu12ExtrasEnabled: false,
                LdPlayerExtrasEnabled: false,
                AttachWindowScreencapMethod: "2",
                AttachWindowMouseMethod: "32",
                AttachWindowKeyboardMethod: "2",
                ClientType: "YoStarEN"));

        Assert.True((await fixture.Session.ConnectAsync(connectedInfo)).Success);

        Assert.True(fixture.Session.IsConnectedWith(connectedInfo));
        Assert.False(fixture.Session.IsConnectedWith(connectedInfo with
        {
            Extras = connectedInfo.Extras! with { ClientType = "YoStarJP" },
        }));
        Assert.False(fixture.Session.IsConnectedWith(connectedInfo with
        {
            Extras = connectedInfo.Extras! with { TouchMode = "MaaTouch" },
        }));
        Assert.False(fixture.Session.IsConnectedWith(connectedInfo with
        {
            Extras = connectedInfo.Extras! with { AttachWindowMouseMethod = "64" },
        }));
    }

    [Fact]
    public async Task StopAsync_ShouldAwaitBridgeStopCompletion_BeforeLeavingStopping()
    {
        var bridge = new ScriptedBridge();
        await using var fixture = await SessionFixture.CreateAsync(bridge);

        Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        Assert.True((await fixture.Session.StartAsync()).Success);

        bridge.StopCompletion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTask = fixture.Session.StopAsync();
        await bridge.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(stopTask.IsCompleted);
        Assert.Equal(SessionState.Stopping, fixture.Session.CurrentState);

        bridge.StopCompletion.SetResult(CoreResult<bool>.Ok(true));
        Assert.True((await stopTask).Success);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public void MacAdbPolicy_ShouldRejectPollutedPaths_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.False(MacBundledAdbPolicy.TryResolveAdbPathForConnect(
            "C:\\platform-tools\\adb.exe",
            out _,
            out var windowsDiagnostic));
        Assert.Contains("Windows", windowsDiagnostic, StringComparison.OrdinalIgnoreCase);

        var root = Path.Combine(Path.GetTempPath(), "maaunified-adb-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dmg = Path.Combine(root, "platform-tools.dmg");
            File.WriteAllText(dmg, string.Empty);
            Assert.False(MacBundledAdbPolicy.TryResolveAdbPathForConnect(dmg, out _, out var dmgDiagnostic));
            Assert.Contains("archive", dmgDiagnostic, StringComparison.OrdinalIgnoreCase);

            var badDirectory = Path.Combine(root, "not-platform-tools");
            Directory.CreateDirectory(badDirectory);
            Assert.False(MacBundledAdbPolicy.TryResolveAdbPathForConnect(badDirectory, out _, out var directoryDiagnostic));
            Assert.Contains("directory", directoryDiagnostic, StringComparison.OrdinalIgnoreCase);

            Assert.False(MacBundledAdbPolicy.TryResolveAdbPathForConnect("/tmp/adb-from-test", out var missingAdb, out var missingDiagnostic));
            Assert.Equal("/tmp/adb-from-test", missingAdb);
            Assert.Contains("exists=False", missingDiagnostic, StringComparison.Ordinal);
            Assert.Contains("reason=missing", missingDiagnostic, StringComparison.Ordinal);
            Assert.Contains("resolutionFailure=missing", missingDiagnostic, StringComparison.Ordinal);

            Assert.True(MacBundledAdbPolicy.TryResolveAdbPathForConnect("adb", out var pathAdb, out var pathDiagnostic));
            Assert.Equal("adb", pathAdb);
            Assert.Contains("requested=adb", pathDiagnostic, StringComparison.Ordinal);
            Assert.Contains("effective=adb", pathDiagnostic, StringComparison.Ordinal);
            Assert.Contains("resolvedPath=", pathDiagnostic, StringComparison.Ordinal);
            Assert.Contains("reason=", pathDiagnostic, StringComparison.Ordinal);
            Assert.Contains("path=", pathDiagnostic, StringComparison.Ordinal);
            Assert.Contains("PATH=", pathDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdbRecoveryProcess_ShouldTimeoutAndKillProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), "maaunified-adb-kill-" + Guid.NewGuid().ToString("N"));
        var result = await TaskQueuePageViewModel.RunAdbRecoveryProcessForTestAsync(
            "test adb timeout",
            "/bin/sh",
            $"-c \"sleep 2; touch '{marker}'\"",
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ExceptionMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kill(entireProcessTree:true)", result.ExceptionMessage, StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task AdbRecoveryProcess_ShouldKillProcessTree_WhenCanceled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var marker = Path.Combine(Path.GetTempPath(), "maaunified-adb-cancel-" + Guid.NewGuid().ToString("N"));
        var result = await TaskQueuePageViewModel.RunAdbRecoveryProcessForTestAsync(
            "test adb cancel",
            "/bin/sh",
            $"-c \"sleep 2; touch '{marker}'\"",
            TimeSpan.FromSeconds(5),
            cts.Token);

        Assert.False(result.Success);
        Assert.Contains("canceled", result.ExceptionMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kill(entireProcessTree:true)", result.ExceptionMessage, StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task NativeStopWrapper_ShouldReturnTimeout_WhenStopCallBlocks()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultTask = MaaCoreBridgeNative.InvokeNativeStopWithTimeoutForTestAsync(
            () =>
            {
                started.SetResult(true);
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return true;
            },
            TimeSpan.FromMilliseconds(100));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.StopFailed, result.Error?.Code);
        Assert.Contains("AsstStop did not return", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeStopWrapper_ShouldReturnSuccess_WhenStopCallCompletes()
    {
        var result = await MaaCoreBridgeNative.InvokeNativeStopWithTimeoutForTestAsync(
            () => true,
            TimeSpan.FromSeconds(1));

        Assert.True(result.Success);
        Assert.True(result.Value);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void AbandonedStopRecovery_ShouldDestroyOnlyAfterSuccessfulStoppedCall(
        bool stopSucceeded,
        bool isRunning,
        bool expectedDestroy)
    {
        var result = stopSucceeded
            ? CoreResult<bool>.Ok(true)
            : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "AsstStop returned false."));

        Assert.Equal(
            expectedDestroy,
            MaaCoreBridgeNative.CanDestroyRecoveredAbandonedStopForTest(
                new nint(41),
                new nint(41),
                result,
                isRunning));
    }

    [Fact]
    public void AbandonedStopRecovery_ShouldNotDestroy_WhenHandleDoesNotMatch()
    {
        Assert.False(MaaCoreBridgeNative.CanDestroyRecoveredAbandonedStopForTest(
            new nint(41),
            new nint(42),
            CoreResult<bool>.Ok(true),
            isRunning: false));
    }

    [Fact]
    public async Task AbandonedStopRecovery_ShouldLateStopAndDestroy_WhenAbandonmentLeftNoTrackedStop()
    {
        var oldInstance = new nint(41);
        var newInstance = new nint(42);
        var stoppedInstances = new List<nint>();
        var destroyedInstances = new List<nint>();
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-abandoned-stop-test"),
            asstRunning: _ => false,
            asstStop: handle =>
            {
                stoppedInstances.Add(handle);
                return true;
            },
            asstDestroy: destroyedInstances.Add,
            asstLoadResource: _ => true,
            asstCreateEx: () => newInstance);

        // Simulate a connect abort whose AsstStop never ran because the
        // native-call lock stayed busy past the abort stop budget: the bridge
        // is marked abandoned without any tracked stop call.
        bridge.MarkNativeStopAbandonedForTest(null, nint.Zero);

        var recovery = await bridge.RecoverAbandonedStopAsync();

        Assert.True(recovery.Success);
        Assert.Single(stoppedInstances);
        Assert.Equal(oldInstance, stoppedInstances[0]);
        Assert.Single(destroyedInstances);
        Assert.Equal(oldInstance, destroyedInstances[0]);
        Assert.Equal(0, bridge.AbandonedInstanceCountForTest);
        Assert.False(bridge.NativeStopAbandonedForTest);
        Assert.False(bridge.HasAbandonedStopTrackingForTest);
    }

    [Fact]
    public async Task AbandonedStopRecovery_ShouldRetain_WhenLateStopLeavesCoreRunning()
    {
        var oldInstance = new nint(41);
        var newInstance = new nint(42);
        var stoppedInstances = new List<nint>();
        var destroyedInstances = new List<nint>();
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-abandoned-stop-test"),
            asstRunning: _ => true,
            asstStop: handle =>
            {
                stoppedInstances.Add(handle);
                return true;
            },
            asstDestroy: destroyedInstances.Add,
            asstLoadResource: _ => true,
            asstCreateEx: () => newInstance);
        bridge.MarkNativeStopAbandonedForTest(null, nint.Zero);

        var recovery = await bridge.RecoverAbandonedStopAsync();

        Assert.True(recovery.Success);
        Assert.Single(stoppedInstances);
        Assert.Empty(destroyedInstances);
        Assert.Equal(1, bridge.AbandonedInstanceCountForTest);
        Assert.False(bridge.NativeStopAbandonedForTest);
    }

    [Fact]
    public async Task AbandonedStopRecovery_ShouldRetainLockUntilLateStopCompletes_WhenLateStopDoesNotReturnInTime()
    {
        var stopStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldInstance = new nint(41);
        var newInstance = new nint(42);
        var destroyedInstances = new List<nint>();
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-abandoned-stop-test"),
            asstRunning: _ => false,
            asstStop: _ =>
            {
                stopStarted.TrySetResult(true);
                stopGate.Task.GetAwaiter().GetResult();
                return true;
            },
            asstDestroy: destroyedInstances.Add,
            asstLoadResource: _ => true,
            asstCreateEx: () => newInstance);
        bridge.MarkNativeStopAbandonedForTest(null, nint.Zero);

        try
        {
            var recovery = await bridge.RecoverAbandonedStopAsync();

            Assert.True(recovery.Success);
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(destroyedInstances);
            Assert.Equal(1, bridge.AbandonedInstanceCountForTest);
            Assert.True(bridge.HasAbandonedStopTrackingForTest);

            // The native-call lock stays retained until the in-flight late stop
            // completes; afterwards ordinary native calls must proceed again.
            stopGate.SetResult(true);
            var probe = await bridge.ApplyInstanceOptionsAsync(
                new CoreInstanceOptions(TouchMode: "maatouch")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(probe.Success);
            Assert.Equal(CoreErrorCode.NotSupported, probe.Error?.Code);
        }
        finally
        {
            stopGate.TrySetResult(true);
        }
    }

    [Fact]
    public async Task TaintedQueueRecovery_ShouldConsumeTrackedAbandonedStop_WhenResetSucceeds()
    {
        var oldInstance = new nint(41);
        var newInstance = new nint(42);
        var destroyedInstances = new List<nint>();
        var createdInstances = new List<nint>();
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-tainted-queue-test"),
            asstRunning: _ => false,
            asstStop: _ => true,
            asstDestroy: destroyedInstances.Add,
            asstLoadResource: _ => true,
            asstCreateEx: () =>
            {
                createdInstances.Add(newInstance);
                return newInstance;
            });

        // Simulate an earlier AsstStop on the old instance that timed out and
        // has since completed (the native-call lock is only reacquirable after
        // the abandoned call completes).
        bridge.MarkNativeStopAbandonedForTest(
            Task.FromResult(CoreResult<bool>.Ok(true)),
            oldInstance);

        var reset = await bridge.RecoverTaintedTaskQueueAsync();

        Assert.True(reset.Success);
        Assert.Single(destroyedInstances);
        Assert.Equal(oldInstance, destroyedInstances[0]);
        Assert.Single(createdInstances);
        Assert.False(bridge.NativeStopAbandonedForTest);
        Assert.False(bridge.HasAbandonedStopTrackingForTest);

        // Pre-fix the stale abandoned-stop state failed the next connect once
        // and the follow-up abandoned-stop recovery retained the healthy new
        // instance in the abandoned list for the process lifetime.
        var recovery = await bridge.RecoverAbandonedStopAsync();
        Assert.False(recovery.Success);
        Assert.Equal(CoreErrorCode.InvalidRequest, recovery.Error?.Code);
        Assert.Equal(0, bridge.AbandonedInstanceCountForTest);
        Assert.Single(createdInstances);
    }

    [Fact]
    public async Task TaintedQueueRecovery_ShouldClearAbandonedStopFlag_WhenAbandonedCallWasNotTrackedStop()
    {
        var oldInstance = new nint(41);
        var newInstance = new nint(42);
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-tainted-queue-test"),
            asstRunning: _ => false,
            asstStop: _ => true,
            asstDestroy: _ => { },
            asstLoadResource: _ => true,
            asstCreateEx: () => newInstance);

        // Simulate an abandoned native call that was not a tracked AsstStop
        // (e.g. a screencap timeout): the flag is set without stop tracking.
        bridge.MarkNativeStopAbandonedForTest(null, nint.Zero);

        var reset = await bridge.RecoverTaintedTaskQueueAsync();

        Assert.True(reset.Success);
        Assert.False(bridge.NativeStopAbandonedForTest);
        Assert.False(bridge.HasAbandonedStopTrackingForTest);
        Assert.Equal(0, bridge.AbandonedInstanceCountForTest);

        var recovery = await bridge.RecoverAbandonedStopAsync();
        Assert.False(recovery.Success);
        Assert.Equal(CoreErrorCode.InvalidRequest, recovery.Error?.Code);
    }

    [Fact]
    public async Task TaintedQueueRecovery_ShouldKeepAbandonedStopState_WhenResetFails()
    {
        var oldInstance = new nint(41);
        var destroyedInstances = new List<nint>();
        var bridge = new MaaCoreBridgeNative();
        bridge.InstallFakeNativeStateForTest(
            oldInstance,
            Path.Combine(Path.GetTempPath(), "maaunified-tainted-queue-test"),
            asstRunning: _ => true,
            asstStop: _ => true,
            asstDestroy: destroyedInstances.Add,
            asstLoadResource: _ => true,
            asstCreateEx: () => new nint(42));
        bridge.MarkNativeStopAbandonedForTest(
            Task.FromResult(CoreResult<bool>.Ok(true)),
            oldInstance);

        var reset = await bridge.RecoverTaintedTaskQueueAsync();

        Assert.False(reset.Success);
        Assert.Equal(CoreErrorCode.StopFailed, reset.Error?.Code);
        Assert.True(bridge.NativeStopAbandonedForTest);
        Assert.True(bridge.HasAbandonedStopTrackingForTest);
        Assert.Empty(destroyedInstances);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, null, false)]
    public void DisposeStop_ShouldDestroyOnlyWhenStoppedStateIsConfirmed(
        bool stopSucceeded,
        bool? isRunning,
        bool expectedDestroy)
    {
        Assert.Equal(
            expectedDestroy,
            MaaCoreBridgeNative.CanDestroyAfterDisposeStopForTest(stopSucceeded, isRunning));
    }

    [Fact]
    public async Task NativeScreencapWrapper_ShouldReturnTimeout_WhenNativeCallBlocks()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultTask = MaaCoreBridgeNative.InvokeNativeCallWithTimeoutForTestAsync(
            () =>
            {
                started.SetResult(true);
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return CoreResult<byte[]>.Ok([1, 2, 3]);
            },
            TimeSpan.FromMilliseconds(100),
            "AsstAsyncScreencap/AsstGetImageBgr",
            CoreErrorCode.GetImageFailed);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.GetImageFailed, result.Error?.Code);
        Assert.Contains("AsstAsyncScreencap", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("did not return", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeForcedBgrCapture_InvalidCallId_ShouldNotReadReadableStaleImage()
    {
        var readCount = 0;

        var result = MaaCoreBridgeNative.ReadForcedImageBgrForTest(
            () => 0,
            () =>
            {
                readCount++;
                return CoreResult<byte[]>.Ok([0x12, 0x34, 0x56]);
            });

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.GetImageFailed, result.Error?.Code);
        Assert.Contains("invalid call id", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, readCount);
    }

    [Fact]
    public void MacAdbResolutionContext_ShouldIncludeBundledAndEffectivePathDetails()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bundledPath = MacBundledAdbPolicy.ResolveBundledAdbPath();
        var context = MacBundledAdbPolicy.BuildResolutionContext(
            "adb",
            bundledPath,
            macBundledAdbRequested: true,
            resolutionFailure: "test failure");

        Assert.Contains("requested=adb", context, StringComparison.Ordinal);
        Assert.Contains($"effective={bundledPath}", context, StringComparison.Ordinal);
        Assert.Contains("macBundledAdbRequested=True", context, StringComparison.Ordinal);
        Assert.Contains("useBundled=True", context, StringComparison.Ordinal);
        Assert.Contains("effectiveIsBundled=True", context, StringComparison.Ordinal);
        Assert.Contains("expectedBundled=", context, StringComparison.Ordinal);
        Assert.Contains("resolvedPath=", context, StringComparison.Ordinal);
        Assert.Contains("exists=", context, StringComparison.Ordinal);
        Assert.Contains("reason=", context, StringComparison.Ordinal);
        Assert.Contains("path=", context, StringComparison.Ordinal);
        Assert.Contains("resolutionFailure=test failure", context, StringComparison.Ordinal);
        Assert.Contains("PATH=", context, StringComparison.Ordinal);
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

    private static CoreCallbackEvent ConnectionCallback(
        string what,
        string address,
        string config,
        string? adb = null)
    {
        var details = new JsonObject
        {
            ["address"] = address,
            ["config"] = config,
        };
        if (adb is not null)
        {
            details["adb"] = adb;
        }

        return new CoreCallbackEvent(
            2,
            "ConnectionInfo",
            new JsonObject
            {
                ["what"] = what,
                ["details"] = details,
            }.ToJsonString(),
            DateTimeOffset.UtcNow);
    }

    private static async Task RunCallbackPumpUntilCanceledAsync(
        UnifiedSessionService session,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.StartCallbackPumpAsync(_ => Task.CompletedTask, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on test shutdown.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        private SessionFixture(
            string root,
            UnifiedConfigurationService config,
            UnifiedSessionService session,
            IMaaCoreBridge bridge,
            UiLogService log,
            ToggleFailingConfigStore configStore)
        {
            Root = root;
            Config = config;
            Session = session;
            Bridge = bridge;
            Log = log;
            ConfigStore = configStore;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public UnifiedSessionService Session { get; }

        public IMaaCoreBridge Bridge { get; }

        public UiLogService Log { get; }

        public ToggleFailingConfigStore ConfigStore { get; }

        public static async Task<SessionFixture> CreateAsync(IMaaCoreBridge bridge)
        {
            var root = Path.Combine(Path.GetTempPath(), "maaunified-session-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var log = new UiLogService();
            var configStore = new ToggleFailingConfigStore(root);
            var config = new UnifiedConfigurationService(
                configStore,
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }

            return new SessionFixture(
                root,
                config,
                new UnifiedSessionService(bridge, config, log, new SessionStateMachine()),
                bridge,
                log,
                configStore);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Keep temporary files for local diagnosis.
            }
        }
    }

    private sealed class ToggleFailingConfigStore(string root) : IUnifiedConfigStore
    {
        private readonly AvaloniaJsonConfigStore _inner = new(root);

        public bool FailSaves { get; set; }

        public string ConfigPath => _inner.ConfigPath;

        public bool Exists() => _inner.Exists();

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
            => _inner.LoadAsync(cancellationToken);

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
            => FailSaves
                ? Task.FromException(new IOException("Synthetic config save failure."))
                : _inner.SaveAsync(config, cancellationToken);

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
            => _inner.BackupAsync(suffix, cancellationToken);

        public Task BackupSanitizedAsync(
            UnifiedConfig config,
            string suffix,
            CancellationToken cancellationToken = default)
            => _inner.BackupSanitizedAsync(config, suffix, cancellationToken);
    }

    private sealed class ScriptedBridge : IMaaCoreBridge, IMaaCoreBridgeRecovery
    {
        private bool _connected;
        private bool _running;
        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();

        public bool BlockConnect { get; set; }

        public bool IgnoreConnectCancellation { get; set; }

        public TaskCompletionSource<bool>? ConnectCompletion { get; set; }

        public CoreConnectionInfo? LastConnectionInfo { get; private set; }

        public int ConnectCallCount { get; private set; }

        public int ApplyInstanceOptionsCallCount { get; private set; }

        public int RecoverFromAbandonedStopCallCount { get; private set; }

        public TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CoreResult<bool>>? StopCompletion { get; set; }

        public CoreResult<bool>? ConnectResult { get; set; }

        public Queue<CoreResult<bool>> ConnectResults { get; } = new();

        public Queue<CoreResult<bool>> ApplyInstanceOptionsResults { get; } = new();

        public CoreCallbackEvent? ScreencapCallback { get; set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectionInfo = connectionInfo;
            ConnectStarted.TrySetResult(true);
            try
            {
                if (BlockConnect)
                {
                    if (ConnectCompletion is null)
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            IgnoreConnectCancellation ? CancellationToken.None : cancellationToken);
                    }
                    else if (IgnoreConnectCancellation)
                    {
                        await ConnectCompletion.Task;
                    }
                    else
                    {
                        await ConnectCompletion.Task.WaitAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Mirror the real bridge: a canceled connect whose abort stop ran
                // leaves the core disconnected.
                _connected = false;
                throw;
            }

            var result = ConnectResults.Count > 0
                ? ConnectResults.Dequeue()
                : ConnectResult ?? CoreResult<bool>.Ok(true);
            _connected = result.Success;
            return result;
        }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions options,
            CancellationToken cancellationToken = default)
        {
            ApplyInstanceOptionsCallCount++;
            return Task.FromResult(ApplyInstanceOptionsResults.Count > 0
                ? ApplyInstanceOptionsResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> RecoverFromAbandonedStopAsync(CancellationToken cancellationToken = default)
            => RecoverAbandonedStopAsync(cancellationToken);

        public Task<CoreResult<bool>> RecoverAbandonedStopAsync(CancellationToken cancellationToken = default)
        {
            RecoverFromAbandonedStopCallCount++;
            _connected = false;
            _running = false;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            _running = _connected;
            return Task.FromResult(_running
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
        }

        public CancellationTokenSource? StopCancellationToTrigger { get; set; }

        public async Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted.TrySetResult(true);
            if (StopCompletion is not null)
            {
                return await StopCompletion.Task;
            }

            _running = false;
            if (StopCancellationToTrigger is not null)
            {
                // Simulate the real bridge re-checking the caller token in its post-stop
                // not-running probe after AsstStop already succeeded.
                StopCancellationToTrigger.Cancel();
                await Task.Delay(1, cancellationToken);
            }

            return CoreResult<bool>.Ok(true);
        }

        public CancellationTokenSource? StatusProbeCancellationToTrigger { get; set; }

        public bool? RuntimeRunningOverride { get; set; }

        public int ReloadResourceCallCount { get; private set; }

        public string? LastReloadClientType { get; private set; }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            if (StatusProbeCancellationToTrigger is not null)
            {
                // Simulate a caller cancellation landing exactly in the session-side
                // post-operation status probe; the real bridge re-checks the token.
                StatusProbeCancellationToTrigger.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, RuntimeRunningOverride ?? _running)));
        }

        public TaskCompletionSource<bool>? ReloadResourceCompletion { get; set; }

        public TaskCompletionSource<bool> ReloadResourceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _reloadRecordGate = new();
        private readonly List<string?> _reloadedClientTypes = [];

        public IReadOnlyList<string?> ReloadedClientTypes
        {
            get
            {
                lock (_reloadRecordGate)
                {
                    return _reloadedClientTypes.ToArray();
                }
            }
        }

        public async Task<CoreResult<bool>> ReloadResourceAsync(string? clientType = null, CancellationToken cancellationToken = default)
        {
            lock (_reloadRecordGate)
            {
                ReloadResourceCallCount++;
                LastReloadClientType = clientType;
                _reloadedClientTypes.Add(clientType);
            }

            ReloadResourceStarted.TrySetResult(true);
            if (ReloadResourceCompletion is { } completion)
            {
                await completion.Task.WaitAsync(cancellationToken);
            }

            return CoreResult<bool>.Ok(true);
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public int ImageBgrCallCount { get; private set; }

        public int StartCallCount { get; private set; }

        public async Task<CoreResult<byte[]>> GetImageBgrAsync(bool forceScreencap = false, CancellationToken cancellationToken = default)
        {
            ImageBgrCallCount++;
            if (ScreencapCallback is { } callback)
            {
                ScreencapCallback = null;
                PublishCallback(callback);
                await Task.Delay(50, cancellationToken);
            }

            return CoreResult<byte[]>.Ok([1, 2, 3]);
        }

        public void PublishCallback(CoreCallbackEvent callback)
        {
            _callbacks.Writer.TryWrite(callback);
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbacks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbacks.Writer.TryComplete();
            return ValueTask.CompletedTask;
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

    private sealed class RecordingToolboxFeature : IToolboxFeatureService
    {
        public int DispatchCallCount { get; private set; }

        public Task<UiOperationResult<ToolboxDispatchResult>> DispatchToolAsync(
            ToolboxDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            DispatchCallCount++;
            return Task.FromResult(UiOperationResult<ToolboxDispatchResult>.Fail(
                UiErrorCode.ToolboxExecutionFailed,
                "not expected to run"));
        }

        public Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("stopped"));
    }

    private sealed class RecordingConnectFeature(
        IReadOnlyList<CoreConnectionInfo>? currentProfileCandidates = null)
        : IConnectFeatureService
    {
        public int ConnectCallCount { get; private set; }
        public IReadOnlyList<CoreConnectionInfo>? LastConnectCandidates { get; private set; }

        public Func<CancellationToken, Task<CoreResult<int>>>? AppendProfileTasksAndStartOverride { get; init; }

        public Task<CoreResult<int>> AppendProfileTasksAndStartAsync(CancellationToken cancellationToken = default)
            => AppendProfileTasksAndStartOverride?.Invoke(cancellationToken)
               ?? Task.FromResult(CoreResult<int>.Fail(new CoreError(
                   CoreErrorCode.NotSupported,
                   "Transactional profile task start is unsupported by current connect service.")));

        public UiOperationResult<IReadOnlyList<CoreConnectionInfo>> BuildCurrentProfileConnectionCandidates(
            bool includeConfiguredAddress = true)
            => currentProfileCandidates is { Count: > 0 }
                ? UiOperationResult<IReadOnlyList<CoreConnectionInfo>>.Ok(
                    includeConfiguredAddress
                        ? currentProfileCandidates
                        : currentProfileCandidates.Skip(1).ToList(),
                    "Connection candidates built.")
                : UiOperationResult<IReadOnlyList<CoreConnectionInfo>>.Fail(
                    UiErrorCode.ProfileMissing,
                    "Current profile connection settings are unavailable.");

        public Task<ConnectionConnectOperationResult> ConnectCandidatesAsync(
            IReadOnlyList<CoreConnectionInfo> candidates,
            CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectCandidates = candidates.ToList();
            var result = candidates.Count > 0
                ? UiOperationResult.Ok($"Connected to {candidates[0].Address}")
                : UiOperationResult.Fail(UiErrorCode.ConnectFailed, "No connection candidates were available.");
            return Task.FromResult(new ConnectionConnectOperationResult(
                result,
                result.Success && candidates.Count > 0 ? candidates[0].Address : null,
                []));
        }

        public Task<CoreResult<bool>> ValidateAndConnectAsync(
            string address,
            string config,
            string? adbPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<UiOperationResult> ConnectAsync(
            string address,
            string config,
            string? adbPath,
            CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            return Task.FromResult(UiOperationResult.Ok($"Connected to {address}"));
        }

        public Task<UiOperationResult> ConnectAsync(
            CoreConnectionInfo connectionInfo,
            CoreInstanceOptions? instanceOptions = null,
            CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            return Task.FromResult(UiOperationResult.Ok($"Connected to {connectionInfo.Address}"));
        }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions? instanceOptions = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<UiOperationResult> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("started"));

        public Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("stopped"));

        public Task<UiOperationResult> WaitAndStopAsync(TimeSpan wait, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("stopped"));

        public Task<UiOperationResult<ImportReport>> ImportLegacyConfigAsync(
            ImportSource source,
            bool manualImport,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<ImportReport>.Fail(UiErrorCode.UiOperationFailed, "not implemented"));
    }
}
