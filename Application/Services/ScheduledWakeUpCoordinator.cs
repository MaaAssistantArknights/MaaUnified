using MAAUnified.Platform;

namespace MAAUnified.Application.Services;

/// <summary>
/// Result of one scheduled wake-up reconciliation, surfaced to the settings page so an explicit
/// switch change can show a failure dialog while background reconciliation only logs.
/// </summary>
/// <param name="Plan">The plan that was applied.</param>
/// <param name="Result">The platform result.</param>
/// <param name="UserInitiated">True when the change came from a user toggle.</param>
public sealed record ScheduledWakeUpSyncResult(
    ScheduledWakeUpPlan Plan,
    PlatformOperationResult Result,
    bool UserInitiated);

/// <summary>
/// Serializes and debounces wake-up registration. WPF (0b40a31009) rebuilds the Task Scheduler
/// task on every TimerList change through a 500 ms debounce and once more in the background at
/// startup; the coordinator provides the same shape for any <see cref="IScheduledWakeUpRegistrar"/>.
/// </summary>
public sealed class ScheduledWakeUpCoordinator : IDisposable
{
    private readonly IScheduledWakeUpRegistrar _registrar;
    private readonly TimeSpan _debounceDelay;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;
    private ScheduledWakeUpPlan? _pendingPlan;
    private bool _pendingUserInitiated;
    private Task _pendingSync = Task.CompletedTask;
    private bool _disposed;

    public ScheduledWakeUpCoordinator(IScheduledWakeUpRegistrar registrar, TimeSpan? debounceDelay = null)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public PlatformCapabilityStatus Capability => _registrar.Capability;

    /// <summary>
    /// Raised after every applied reconciliation, on a background thread.
    /// </summary>
    public event EventHandler<ScheduledWakeUpSyncResult>? SyncCompleted;

    /// <summary>
    /// The task representing the most recently requested debounced reconciliation. Tests await
    /// this; production ignores it because <see cref="SyncCompleted"/> reports the outcome.
    /// </summary>
    public Task PendingSync
    {
        get
        {
            lock (_debounceGate)
            {
                return _pendingSync;
            }
        }
    }

    /// <summary>
    /// Queues a reconciliation after <c>debounceDelay</c>; a newer request cancels the pending one
    /// and replaces its plan, so consecutive spinner ticks collapse into one rebuild.
    /// </summary>
    /// <param name="plan">Target plan.</param>
    /// <param name="userInitiated">Whether the change came from a user toggle.</param>
    public void RequestSync(ScheduledWakeUpPlan plan, bool userInitiated = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CancellationTokenSource cts;
        lock (_debounceGate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingPlan = plan;
            _pendingUserInitiated = userInitiated;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _debounceCts = cts;
            _pendingSync = DebounceAsync(cts);
        }
    }

    /// <summary>
    /// Applies <paramref name="plan"/> immediately. Concurrent calls are serialized so a delete
    /// and a create can never interleave across two rebuilds (WPF ScheduledWakeUp._syncLock).
    /// </summary>
    /// <param name="plan">Target plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The platform result.</returns>
    public async Task<PlatformOperationResult> SyncNowAsync(
        ScheduledWakeUpPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registrar.Capability.Supported)
            {
                return PlatformOperation.Failed(
                    _registrar.Capability.Provider,
                    _registrar.Capability.Message,
                    PlatformErrorCodes.ScheduledWakeUpUnsupported,
                    "scheduledWakeUp.sync");
            }

            return await _registrar.SyncAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                _registrar.Capability.Provider,
                $"Failed to sync the scheduled wake-up task: {ex.Message}",
                PlatformErrorCodes.ScheduledWakeUpRegisterFailed,
                "scheduledWakeUp.sync");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task DebounceAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_debounceDelay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ScheduledWakeUpPlan? plan;
        bool userInitiated;
        lock (_debounceGate)
        {
            if (!ReferenceEquals(_debounceCts, cts))
            {
                return;
            }

            _debounceCts = null;
            plan = _pendingPlan;
            userInitiated = _pendingUserInitiated;
        }

        if (plan is null)
        {
            return;
        }

        var result = await SyncNowAsync(plan, CancellationToken.None).ConfigureAwait(false);
        SyncCompleted?.Invoke(this, new ScheduledWakeUpSyncResult(plan, result, userInitiated));
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_debounceGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cts = _debounceCts;
            _debounceCts = null;
            _pendingPlan = null;
        }

        cts?.Cancel();
        cts?.Dispose();

        // The semaphore is intentionally not disposed: disposal may race with an in-flight sync's
        // Release, and SemaphoreSlim needs no deterministic cleanup here.
    }
}
