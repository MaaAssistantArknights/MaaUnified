using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAAUnified.CoreBridge;

public interface IMaaCoreBridge : IAsyncDisposable
{
    bool SupportsBackToHome => false;

    bool SupportsStartCloseDown => false;

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

    Task<CoreResult<bool>> SetConnectionExtrasAsync(
        string name,
        string extrasJson,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "Connection extras are unsupported by current bridge.")));

    Task<CoreResult<int>> AppendTaskAsync(
        CoreTaskRequest task,
        CancellationToken cancellationToken = default);

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
