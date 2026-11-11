using MAAUnified.App.Services;
using MAAUnified.Application.Services;

namespace MAAUnified.Tests;

public sealed class ConfigurationSaveTrackerTests
{
    [Fact]
    public async Task RunTrackedAsync_SuccessClearsPending_WhenNoNewerEditArrives()
    {
        var tracker = ConfigurationSaveTracker.Instance;
        var key = NewKey();
        var diagnostics = CreateDiagnostics(out var root);
        try
        {
            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            Assert.True(tracker.IsPendingOrFailed(key));

            var saved = await tracker.RunTrackedAsync(
                key,
                "Tracker test",
                key,
                diagnostics,
                static _ => Task.FromResult(true));

            Assert.True(saved);
            Assert.False(tracker.IsPendingOrFailed(key));
            Assert.DoesNotContain(key, tracker.GetPendingOrFailedKeys(diagnostics));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RunTrackedAsync_SupersededSuccessPreservesNewerPendingMarker()
    {
        var tracker = ConfigurationSaveTracker.Instance;
        var key = NewKey();
        var diagnostics = CreateDiagnostics(out var root);
        try
        {
            var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSaveToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            var firstSave = tracker.RunTrackedAsync(
                key,
                "Tracker test",
                key,
                diagnostics,
                async _ =>
                {
                    saveStarted.TrySetResult(true);
                    await allowSaveToComplete.Task;
                    return true;
                });

            await saveStarted.Task;

            // A newer edit supersedes the in-flight save before it completes.
            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            allowSaveToComplete.TrySetResult(true);

            Assert.True(await firstSave);
            Assert.True(tracker.IsPendingOrFailed(key));
            Assert.Contains(key, tracker.GetPendingOrFailedKeys(diagnostics));

            // The close-flush retry path can now see and complete the newer edit.
            var failedKeys = await tracker.RetryPendingOrFailedAsync(
                candidate => string.Equals(candidate, key, StringComparison.Ordinal),
                diagnosticsService: diagnostics);
            Assert.Empty(failedKeys);
            Assert.False(tracker.IsPendingOrFailed(key));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RunTrackedAsync_StaleSuccessDoesNotClearNewerFailure()
    {
        var tracker = ConfigurationSaveTracker.Instance;
        var key = NewKey();
        var diagnostics = CreateDiagnostics(out var root);
        try
        {
            var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSaveToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            var staleSave = tracker.RunTrackedAsync(
                key,
                "Tracker test",
                key,
                diagnostics,
                async _ =>
                {
                    saveStarted.TrySetResult(true);
                    await allowSaveToComplete.Task;
                    return true;
                });

            await saveStarted.Task;

            // A newer edit schedules a replacement save, which then fails.
            var newerSaveShouldFail = true;
            Task<bool> NewerSaveAsync(CancellationToken _) => Task.FromResult(!newerSaveShouldFail);
            tracker.MarkPending(key, "Tracker test", key, diagnostics, NewerSaveAsync);
            var newerSaveFailed = await tracker.RunTrackedAsync(
                key,
                "Tracker test",
                key,
                diagnostics,
                NewerSaveAsync);
            Assert.False(newerSaveFailed);
            Assert.True(tracker.IsPendingOrFailed(key));

            // The stale save's late success must not hide the newer failure.
            newerSaveShouldFail = false;
            allowSaveToComplete.TrySetResult(true);
            Assert.True(await staleSave);
            Assert.True(tracker.IsPendingOrFailed(key));
            Assert.Contains(key, tracker.GetPendingOrFailedKeys(diagnostics));

            var failedKeys = await tracker.RetryPendingOrFailedAsync(
                candidate => string.Equals(candidate, key, StringComparison.Ordinal),
                diagnosticsService: diagnostics);
            Assert.Empty(failedKeys);
            Assert.False(tracker.IsPendingOrFailed(key));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RunTrackedAsync_StaleFailurePreservesNewerPendingMarker()
    {
        var tracker = ConfigurationSaveTracker.Instance;
        var key = NewKey();
        var diagnostics = CreateDiagnostics(out var root);
        try
        {
            var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSaveToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            var staleSave = tracker.RunTrackedAsync(
                key,
                "Tracker test",
                key,
                diagnostics,
                async _ =>
                {
                    saveStarted.TrySetResult(true);
                    await allowSaveToComplete.Task;
                    return false;
                });

            await saveStarted.Task;

            // A newer edit supersedes the in-flight save before it completes.
            tracker.MarkPending(key, "Tracker test", key, diagnostics, static _ => Task.FromResult(true));
            allowSaveToComplete.TrySetResult(true);

            Assert.False(await staleSave);
            Assert.True(tracker.IsPendingOrFailed(key));

            var failedKeys = await tracker.RetryPendingOrFailedAsync(
                candidate => string.Equals(candidate, key, StringComparison.Ordinal),
                diagnosticsService: diagnostics);
            Assert.Empty(failedKeys);
            Assert.False(tracker.IsPendingOrFailed(key));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewKey()
    {
        return "Tests.ConfigurationSaveTracker." + Guid.NewGuid().ToString("N");
    }

    private static UiDiagnosticsService CreateDiagnostics(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "maaunified-save-tracker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new UiDiagnosticsService(root, new UiLogService());
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; temp directories are unique per test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort; temp directories are unique per test.
        }
    }
}
