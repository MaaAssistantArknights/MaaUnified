using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;

namespace MAAUnified.Tests;

public sealed class ExclusiveSessionOperationRegressionTests
{
    [Fact]
    public void ExclusiveSessionOperation_ShouldRejectLateNormalOperationsAndRecover()
    {
        var shared = new ConnectionGameSharedStateViewModel();
        var transitions = new List<bool>();
        shared.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(ConnectionGameSharedStateViewModel.IsSessionOperationActive),
                    StringComparison.Ordinal))
            {
                transitions.Add(shared.IsSessionOperationActive);
            }
        };

        using var exclusive = shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart");
        Assert.NotNull(exclusive);
        Assert.True(shared.IsSessionOperationActive);
        Assert.True(shared.IsSessionOperationExclusiveActive);
        Assert.False(shared.TryBeginTaskStartRequest());
        Assert.False(shared.TryBeginScreenshotTestActivity());
        Assert.Null(shared.TryBeginSessionOperation("TaskQueue.LinkStart"));
        Assert.Throws<InvalidOperationException>(
            () => shared.BeginSessionOperation("TaskQueue.LinkStart"));

        exclusive.Dispose();
        exclusive.Dispose();
        Assert.False(shared.IsSessionOperationActive);
        Assert.False(shared.IsSessionOperationExclusiveActive);
        Assert.Equal([true, false], transitions);

        using var normal = shared.BeginSessionOperation("TaskQueue.LinkStart");
        Assert.True(shared.IsSessionOperationActive);
        Assert.Null(shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart"));
    }

    [Fact]
    public async Task TaskQueueRun_ShouldProjectStopAndRejectDirectStartDuringScreenshotActivity()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var shared = new ConnectionGameSharedStateViewModel();
        var page = new TaskQueuePageViewModel(fixture.Runtime, shared);
        var runGateChanges = new List<bool>();
        page.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(TaskQueuePageViewModel.CanToggleRun), StringComparison.Ordinal))
            {
                runGateChanges.Add(page.CanToggleRun);
            }
        };

        Assert.True(shared.TryBeginScreenshotTestActivity());
        Assert.True(shared.IsScreenshotTestActive);
        Assert.True(shared.IsSessionOperationActive);
        Assert.True(page.IsExternalSessionActivityActive);
        Assert.True(page.CanToggleRun);
        Assert.Equal(page.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), page.RunButtonText);

        await page.StartAsync();

        Assert.False(page.IsRunning);
        Assert.False(page.IsStartRequestActive);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Contains("会话操作", page.LastErrorMessage, StringComparison.Ordinal);

        shared.EndScreenshotTestActivity();
        Assert.False(shared.IsSessionOperationActive);
        Assert.False(page.IsExternalSessionActivityActive);
        Assert.True(page.CanToggleRun);
        Assert.Equal(page.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), page.RunButtonText);
        Assert.Contains(true, runGateChanges);
    }

    [Fact]
    public async Task TaskQueueRun_ShouldProjectStopAndRejectDirectStartDuringExclusiveLease()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var shared = new ConnectionGameSharedStateViewModel();
        var page = new TaskQueuePageViewModel(fixture.Runtime, shared);
        using var exclusive = shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart");
        Assert.NotNull(exclusive);

        Assert.True(page.IsExternalSessionActivityActive);
        Assert.True(page.CanToggleRun);
        Assert.Equal(page.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), page.RunButtonText);
        await page.StartAsync();

        Assert.False(page.IsRunning);
        Assert.False(page.IsStartRequestActive);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Contains("定时任务", page.LastErrorMessage, StringComparison.Ordinal);

        exclusive.Dispose();
        Assert.True(page.CanToggleRun);
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, MAAUnifiedRuntime runtime)
        {
            Root = root;
            Runtime = runtime;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runtime = MAAUnifiedRuntimeFactory.Create(root, SensitiveSettingsService.CreateEphemeralForTests());
            await runtime.ConfigurationService.LoadOrBootstrapAsync();
            return new RuntimeFixture(root, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
