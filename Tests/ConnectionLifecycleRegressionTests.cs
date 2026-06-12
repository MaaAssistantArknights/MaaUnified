using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
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
            UiLogService log)
        {
            Root = root;
            Config = config;
            Session = session;
            Bridge = bridge;
            Log = log;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public UnifiedSessionService Session { get; }

        public IMaaCoreBridge Bridge { get; }

        public UiLogService Log { get; }

        public static async Task<SessionFixture> CreateAsync(IMaaCoreBridge bridge)
        {
            var root = Path.Combine(Path.GetTempPath(), "maaunified-session-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var log = new UiLogService();
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

            return new SessionFixture(
                root,
                config,
                new UnifiedSessionService(bridge, config, log, new SessionStateMachine()),
                bridge,
                log);
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

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectionInfo = connectionInfo;
            ConnectStarted.TrySetResult(true);
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
            _running = _connected;
            return Task.FromResult(_running
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted.TrySetResult(true);
            if (StopCompletion is not null)
            {
                return StopCompletion.Task;
            }

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

    private sealed class RecordingConnectFeature(
        IReadOnlyList<CoreConnectionInfo>? currentProfileCandidates = null)
        : IConnectFeatureService
    {
        public int ConnectCallCount { get; private set; }
        public IReadOnlyList<CoreConnectionInfo>? LastConnectCandidates { get; private set; }

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
