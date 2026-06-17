using System.IO;
using System.Threading;
using MAAUnified.Application.Services;

namespace MAAUnified.Tests;

public sealed class UiDiagnosticsSynchronizationContextTests
{
    [Fact]
    public void RecordEventAsync_ShouldComplete_WhenSynchronouslyWaitedUnderNonPumpingSynchronizationContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-diagnostics-sync-context-tests", Guid.NewGuid().ToString("N"));
        var previousContext = SynchronizationContext.Current;
        var context = new NonPumpingSynchronizationContext();

        try
        {
            Directory.CreateDirectory(root);
            SynchronizationContext.SetSynchronizationContext(context);
            var diagnostics = new UiDiagnosticsService(root, new UiLogService());
            var line = new string('x', 1024 * 1024);

            var task = diagnostics.RecordEventAsync("Diagnostics.SyncContext", line);

#pragma warning disable xUnit1031
            Assert.True(
                task.Wait(TimeSpan.FromSeconds(2)),
                $"diagnostics write captured the current SynchronizationContext; postCount={context.PostCount}");
            task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            Assert.Contains("Diagnostics.SyncContext", File.ReadAllText(diagnostics.EventLogPath));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures in temporary test directories.
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
        }
    }
}
