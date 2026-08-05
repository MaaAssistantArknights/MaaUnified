using Avalonia.Threading;
using MAAUnified.App;
using Xunit;

namespace MAAUnified.Tests;

public sealed class SensitiveStorageCompositionTests
{
    [Fact]
    public void CreateWithoutSensitiveStorageUseDoesNotTouchRuntimeDirectory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"maaunified-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var runtimeBaseDirectory = Path.Combine(testRoot, "not-created");

        try
        {
            var service = SensitiveStorageComposition.Create(runtimeBaseDirectory);

            Assert.Equal(MAAUnified.Application.Security.SensitiveStorageState.Uninitialized, service.State);
            Assert.False(Directory.Exists(runtimeBaseDirectory));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PlaintextConsentDispatcher_FromWorkerThread_InvokesCallbackOnUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());

        var dispatched = Task.Run(() =>
            SensitiveStorageComposition.AvaloniaPlaintextConsent.RunOnUiThreadAsync(
                () => Task.FromResult(Dispatcher.UIThread.CheckAccess()),
                CancellationToken.None));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!dispatched.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs(null);
            Thread.Sleep(1);
        }

        Assert.True(dispatched.IsCompleted);
        Assert.True(await dispatched);
    }
}
