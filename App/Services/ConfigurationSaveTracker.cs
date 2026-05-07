using MAAUnified.Application.Models;
using MAAUnified.Application.Services;

namespace MAAUnified.App.Services;

public sealed class ConfigurationSaveTracker
{
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly Dictionary<string, SaveEntry> _entries = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> _activeIdleSource = CreateCompletedSource();

    public static ConfigurationSaveTracker Instance { get; } = new();

    public bool HasActiveSaves
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Any(static entry => entry.ActiveCount > 0);
            }
        }
    }

    public IReadOnlyList<string> ActiveDisplayNames => SnapshotNames(static entry => entry.ActiveCount > 0);

    public IReadOnlyList<string> PendingOrFailedKeys
    {
        get
        {
            lock (_gate)
            {
                return _entries
                    .Where(static pair => pair.Value.Pending || pair.Value.Failed)
                    .Select(static pair => pair.Key)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> FailedDisplayNames => SnapshotNames(static entry => entry.Failed);

    public IReadOnlyList<string> PendingOrFailedDisplayNames => SnapshotNames(static entry => entry.Pending || entry.Failed);

    public IReadOnlyList<string> ActiveOrPendingOrFailedDisplayNames => SnapshotNames(static entry => entry.ActiveCount > 0 || entry.Pending || entry.Failed);

    public event EventHandler? StateChanged;

    public void MarkPending(string key, string displayName)
    {
        MarkPending(
            key,
            displayName,
            scope: key,
            diagnosticsService: null,
            retryAsync: null);
    }

    public void MarkPending(
        string key,
        string displayName,
        string scope,
        UiDiagnosticsService? diagnosticsService,
        Func<CancellationToken, Task<bool>>? retryAsync)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            var entry = GetOrCreateEntry(key, displayName);
            entry.Scope = string.IsNullOrWhiteSpace(scope) ? key : scope;
            entry.DiagnosticsService = diagnosticsService;
            entry.RetryAsync = retryAsync;
            entry.Pending = true;
        }

        RaiseStateChanged();
    }

    public void RegisterSaveTarget(
        string key,
        string displayName,
        string scope,
        UiDiagnosticsService diagnosticsService,
        Func<CancellationToken, Task<bool>> retryAsync)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            var entry = GetOrCreateEntry(key, displayName);
            entry.Scope = string.IsNullOrWhiteSpace(scope) ? key : scope;
            entry.DiagnosticsService = diagnosticsService;
            entry.RetryAsync = retryAsync;
        }
    }

    public void ClearPending(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Pending = false;
            }
        }

        RaiseStateChanged();
    }

    public void ClearFailure(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Failed = false;
            }
        }

        RaiseStateChanged();
    }

    public void MarkFailed(string key, string displayName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            var entry = GetOrCreateEntry(key, displayName);
            entry.Pending = false;
            entry.Failed = true;
        }

        RaiseStateChanged();
    }

    public async Task<bool> RunTrackedAsync(
        string key,
        string displayName,
        string scope,
        UiDiagnosticsService diagnosticsService,
        Func<CancellationToken, Task<bool>> saveAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            key = scope;
        }

        RegisterSaveTarget(key, displayName, scope, diagnosticsService, saveAsync);
        BeginActive(key, displayName);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var saveTask = RunSaveOperationAsync(saveAsync, timeoutCts.Token);
            var completedTask = await Task.WhenAny(saveTask, Task.Delay(SaveTimeout, CancellationToken.None));
            if (!ReferenceEquals(completedTask, saveTask))
            {
                await RecordTimeoutAsync(displayName, scope, diagnosticsService);
                timeoutCts.Cancel();
                _ = ObserveLateSaveAsync(saveTask, displayName, scope, diagnosticsService);
                MarkFailedCore(key);
                return false;
            }

            try
            {
                var succeeded = await saveTask;
                if (succeeded)
                {
                    MarkSucceeded(key);
                }
                else
                {
                    MarkFailedCore(key);
                }

                return succeeded;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await RecordTimeoutAsync(displayName, scope, diagnosticsService);
                MarkFailedCore(key);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception ex)
            {
                await diagnosticsService.RecordErrorAsync(
                    scope,
                    $"{displayName} save failed unexpectedly.",
                    ex,
                    CancellationToken.None);
                MarkFailedCore(key);
                return false;
            }
        }
        finally
        {
            EndActive(key);
        }
    }

    private static async Task<bool> RunSaveOperationAsync(
        Func<CancellationToken, Task<bool>> saveAsync,
        CancellationToken cancellationToken)
    {
        return await saveAsync(cancellationToken);
    }

    private static Task RecordTimeoutAsync(
        string displayName,
        string scope,
        UiDiagnosticsService diagnosticsService)
    {
        var result = UiOperationResult.Fail(
            UiErrorCode.SettingsSaveFailed,
            $"{displayName} save timed out after {SaveTimeout.TotalSeconds:0} seconds.");
        return diagnosticsService.RecordFailedResultAsync(scope, result, CancellationToken.None);
    }

    private static async Task ObserveLateSaveAsync(
        Task<bool> saveTask,
        string displayName,
        string scope,
        UiDiagnosticsService diagnosticsService)
    {
        try
        {
            _ = await saveTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the timed-out save honors the timeout token after the UI has moved on.
        }
        catch (Exception ex)
        {
            await diagnosticsService.RecordErrorAsync(
                $"{scope}.LateCompletion",
                $"{displayName} save failed after timeout.",
                ex,
                CancellationToken.None);
        }
    }

    public async Task WaitForActiveSavesAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_entries.Values.All(static entry => entry.ActiveCount <= 0))
                {
                    return;
                }

                waitTask = _activeIdleSource.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> RetryPendingOrFailedAsync(
        Func<string, bool>? keyPredicate = null,
        CancellationToken cancellationToken = default)
    {
        var entries = SnapshotRetryEntries(keyPredicate);
        foreach (var entry in entries)
        {
            if (entry.RetryAsync is null || entry.DiagnosticsService is null)
            {
                MarkFailed(entry.Key, entry.DisplayName);
                continue;
            }

            _ = await RunTrackedAsync(
                entry.Key,
                entry.DisplayName,
                entry.Scope,
                entry.DiagnosticsService,
                entry.RetryAsync,
                cancellationToken);
        }

        return SnapshotNames(pair => (keyPredicate is null || keyPredicate(pair.Key)) && pair.Value.Failed);
    }

    public bool IsPendingOrFailed(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) && (entry.Pending || entry.Failed);
        }
    }

    private void BeginActive(string key, string displayName)
    {
        lock (_gate)
        {
            if (_entries.Values.All(static entry => entry.ActiveCount <= 0))
            {
                _activeIdleSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var entry = GetOrCreateEntry(key, displayName);
            entry.Pending = false;
            entry.ActiveCount++;
        }

        RaiseStateChanged();
    }

    private void EndActive(string key)
    {
        var becameIdle = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.ActiveCount > 0)
            {
                entry.ActiveCount--;
            }

            if (_entries.Values.All(static entry => entry.ActiveCount <= 0))
            {
                becameIdle = true;
                _activeIdleSource.TrySetResult(true);
            }
        }

        if (becameIdle)
        {
            RaiseStateChanged();
        }
    }

    private void MarkSucceeded(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Pending = false;
                entry.Failed = false;
            }
        }

        RaiseStateChanged();
    }

    private void MarkFailedCore(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Pending = false;
                entry.Failed = true;
            }
        }

        RaiseStateChanged();
    }

    private IReadOnlyList<string> SnapshotNames(Func<SaveEntry, bool> predicate)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(predicate)
                .Select(static entry => entry.DisplayName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    private IReadOnlyList<string> SnapshotNames(Func<KeyValuePair<string, SaveEntry>, bool> predicate)
    {
        lock (_gate)
        {
            return _entries
                .Where(predicate)
                .Select(static pair => pair.Value.DisplayName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    private IReadOnlyList<RetryEntry> SnapshotRetryEntries(Func<string, bool>? keyPredicate)
    {
        lock (_gate)
        {
            return _entries
                .Where(pair => (keyPredicate is null || keyPredicate(pair.Key))
                    && pair.Value.ActiveCount <= 0
                    && (pair.Value.Pending || pair.Value.Failed))
                .Select(pair => new RetryEntry(
                    pair.Key,
                    pair.Value.DisplayName,
                    string.IsNullOrWhiteSpace(pair.Value.Scope) ? pair.Key : pair.Value.Scope,
                    pair.Value.DiagnosticsService,
                    pair.Value.RetryAsync))
                .ToArray();
        }
    }

    private SaveEntry GetOrCreateEntry(string key, string displayName)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new SaveEntry(displayName);
            _entries[key] = entry;
            return entry;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            entry.DisplayName = displayName;
        }

        return entry;
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }

    private sealed class SaveEntry
    {
        public SaveEntry(string displayName)
        {
            DisplayName = displayName;
        }

        public string DisplayName { get; set; }

        public bool Pending { get; set; }

        public bool Failed { get; set; }

        public int ActiveCount { get; set; }

        public string Scope { get; set; } = string.Empty;

        public UiDiagnosticsService? DiagnosticsService { get; set; }

        public Func<CancellationToken, Task<bool>>? RetryAsync { get; set; }
    }

    private sealed record RetryEntry(
        string Key,
        string DisplayName,
        string Scope,
        UiDiagnosticsService? DiagnosticsService,
        Func<CancellationToken, Task<bool>>? RetryAsync);
}
