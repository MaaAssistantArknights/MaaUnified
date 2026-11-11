using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAAUnified.CoreBridge;

public interface IMaaCoreBridge : IAsyncDisposable
{
    bool SupportsBackToHome => false;

    bool SupportsStartCloseDown => false;

    // Monotonic sequence number of the most recently enqueued callback.
    // Session orchestration snapshots this at run start to recognize terminal
    // callbacks that were enqueued by an earlier (already stopped) batch but
    // are still waiting in the pump. Bridges that do not tag callback
    // sequences return 0, which disables the staleness check.
    long EnqueuedCallbackSequence => 0;

    Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
        CoreInitializeRequest request,
        CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
        CoreInstanceOptions options,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Instance options are unsupported by current bridge.")));

    Task<CoreResult<bool>> RecoverFromAbandonedStopAsync(CancellationToken cancellationToken = default)
        => this is IMaaCoreBridgeRecovery recovery
            ? recovery.RecoverAbandonedStopAsync(cancellationToken)
            : Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Abandoned native stop recovery is unsupported by current bridge.")));

    Task<CoreResult<bool>> RecoverFromTaintedTaskQueueAsync(CancellationToken cancellationToken = default)
        => this is IMaaCoreBridgeQueueRecovery recovery
            ? recovery.RecoverTaintedTaskQueueAsync(cancellationToken)
            : Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Tainted task queue recovery is unsupported by current bridge.")));

    Task<CoreResult<bool>> SetConnectionExtrasAsync(
        string name,
        string extrasJson,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Connection extras are unsupported by current bridge.")));

    Task<CoreResult<int>> AppendTaskAsync(
        CoreTaskRequest task,
        CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> SetTaskParamsAsync(
        int taskId,
        string paramsJson,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Task parameter updates are unsupported by current bridge.")));

    Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> BackToHomeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "BackToHome is unsupported by current bridge.")));

    Task<CoreResult<bool>> StartCloseDownAsync(string clientType, CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "CloseDown is unsupported by current bridge.")));

    Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default);

    Task<CoreResult<bool>> ReloadResourceAsync(
        string? clientType = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Resource reload is unsupported by current bridge.")));

    Task<CoreResult<bool>> AttachWindowAsync(
        CoreAttachWindowRequest request,
        CancellationToken cancellationToken = default);

    Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default);

    Task<CoreResult<byte[]>> CaptureImageAsync(CancellationToken cancellationToken = default)
        => GetImageAsync(cancellationToken);

    // Forces a fresh screencap before reading the frame (WPF AsstGetFreshImage
    // semantics). The default falls back to the cached-frame read.
    Task<CoreResult<byte[]>> GetFreshImageAsync(CancellationToken cancellationToken = default)
        => GetImageAsync(cancellationToken);

    Task<CoreResult<byte[]>> GetImageBgrAsync(
        bool forceScreencap = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.NotSupported, "Raw BGR image is unsupported by current bridge.")));

    IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync(CancellationToken cancellationToken = default);
}

public interface IMaaCoreBridgeRecovery
{
    Task<CoreResult<bool>> RecoverAbandonedStopAsync(CancellationToken cancellationToken = default);
}

public interface IMaaCoreBridgeQueueRecovery
{
    Task<CoreResult<bool>> RecoverTaintedTaskQueueAsync(CancellationToken cancellationToken = default);
}
