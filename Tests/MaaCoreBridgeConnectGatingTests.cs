using System.Reflection;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class MaaCoreBridgeConnectGatingTests
{
    private const int MsgConnectionInfo = 2;
    private const int MsgAsyncCallInfo = 4;
    private const int PendingAsyncCallId = 777;

    [Theory]
    [InlineData("MacSCK")]
    [InlineData("MacBGR")]
    [InlineData("CompatMac")]
    [InlineData("MacPlayTools")]
    public async Task TryHandlePendingConnect_PlayCover_ResolutionInfoCompletesConnect(string connectConfig)
    {
        var (bridge, completion) = CreatePendingConnect(connectConfig);

        InvokeCallback(bridge, MsgAsyncCallInfo, AsyncCallSucceededPayload);
        Assert.False(completion.Task.IsCompleted); // the async-call ack alone must not complete the connect

        InvokeCallback(bridge, MsgConnectionInfo, ResolutionInfoPayload);

        var result = await completion.Task;
        Assert.True(completion.Task.IsCompletedSuccessfully);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task TryHandlePendingConnect_Adb_ResolutionInfoDoesNotCompleteConnect()
    {
        var (bridge, completion) = CreatePendingConnect("General");

        InvokeCallback(bridge, MsgAsyncCallInfo, AsyncCallSucceededPayload);
        InvokeCallback(bridge, MsgConnectionInfo, ResolutionInfoPayload);

        // ResolutionInfo must not be treated as "connected" for ADB-style connects.
        Assert.False(completion.Task.IsCompleted);

        // A real Connected callback still completes it, so the gating doesn't break normal connects.
        InvokeCallback(bridge, MsgConnectionInfo, ConnectedPayload);
        var result = await completion.Task;
        Assert.True(completion.Task.IsCompletedSuccessfully);
        Assert.True(result.Success);
    }

    private static (MaaCoreBridgeNative Bridge, TaskCompletionSource<CoreResult<bool>> Completion) CreatePendingConnect(
        string connectConfig)
    {
        var bridge = new MaaCoreBridgeNative();

        var pendingType = typeof(MaaCoreBridgeNative).GetNestedType("ConnectPendingState", BindingFlags.NonPublic);
        Assert.NotNull(pendingType);

        var pending = Activator.CreateInstance(
            pendingType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [PendingAsyncCallId, connectConfig],
            culture: null);
        Assert.NotNull(pending);

        typeof(MaaCoreBridgeNative)
            .GetField("_pendingConnect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bridge, pending);

        var completion = (TaskCompletionSource<CoreResult<bool>>)pendingType!
            .GetProperty("Completion")!
            .GetValue(pending)!;

        return (bridge, completion);
    }

    private static void InvokeCallback(MaaCoreBridgeNative bridge, int msgId, string payloadJson)
    {
        var method = typeof(MaaCoreBridgeNative).GetMethod(
            "TryHandlePendingConnect",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(bridge, [new CoreCallbackEvent(msgId, "callback", payloadJson, DateTimeOffset.UtcNow)]);
    }

    private static string AsyncCallSucceededPayload
        => "{\"async_call_id\":" + PendingAsyncCallId + ",\"what\":\"Connect\",\"details\":{\"ret\":true}}";

    private static string ResolutionInfoPayload
        => """{"what":"ResolutionInfo","details":{"width":1280,"height":720}}""";

    private static string ConnectedPayload
        => """{"what":"Connected","details":{}}""";
}
