using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.RemoteControl;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

/// <summary>
/// WPF frozen-commit parity for Remote Control execution semantics:
/// completion-timed reports (UntilIdleAsync), sequential/instant queue routing,
/// silent consumption of unknown task types, fresh-frame capture, and
/// Settings-Stage1 succeeding without a Fight task.
/// </summary>
public sealed class RemoteControlExecutionSemanticsTests
{
    [Fact]
    public void Classify_RoutesCommandsLikeWpfQueues()
    {
        // WPF sequential queue (frozen RemoteControlService.PollJobTaskLoop).
        foreach (var command in new[]
        {
            "LinkStart", "LinkStart-Base", "LinkStart-WakeUp", "LinkStart-Combat",
            "LinkStart-Recruiting", "LinkStart-Mall", "LinkStart-Mission",
            "LinkStart-AutoRoguelike", "LinkStart-Reclamation",
            "Toolbox-GachaOnce", "Toolbox-GachaTenTimes",
            "CaptureImage", "Settings-ConnectAddress", "Settings-Stage1",
        })
        {
            Assert.Equal(RemoteControlCommandKind.Sequential, RemoteControlCommandDispatcher.Classify(command));
        }

        // WPF instant queue.
        foreach (var command in new[] { "CaptureImageNow", "HeartBeat", "StopTask" })
        {
            Assert.Equal(RemoteControlCommandKind.Instant, RemoteControlCommandDispatcher.Classify(command));
        }

        // Unknown types are neither sequential nor instant.
        Assert.Equal(RemoteControlCommandKind.Unknown, RemoteControlCommandDispatcher.Classify("NotARealCommand"));
        Assert.Equal(RemoteControlCommandKind.Unknown, RemoteControlCommandDispatcher.Classify(""));
        Assert.Equal(RemoteControlCommandKind.Unknown, RemoteControlCommandDispatcher.Classify(null));
    }

    [Fact]
    public async Task RouteFetchedCommands_SkipsUnknownCommandsWithoutReport()
    {
        var reportUri = new Uri("http://127.0.0.1:1/report");
        var reportedBodies = new List<string>();
        await using var service = new RemoteControlFeatureService(
            null, null, null, null, null, null, null,
            probeAsync: null,
            sendHttpAsync: async (request, cancellationToken) =>
            {
                if (request.RequestUri == reportUri)
                {
                    reportedBodies.Add(request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken));
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var snapshot = new RemoteControlPollingSnapshot(null, reportUri, "alice", "pc-1", 1000);
        var requests = new List<RemoteControlCommandRequest>
        {
            new("NotARealCommand", null, "alice", "pc-1", TaskId: "90"),
            new("HeartBeat", null, "alice", "pc-1", TaskId: "91"),
        };

        await service.RouteFetchedCommandsAsync(snapshot, requests, CancellationToken.None);

        // WPF consumes unknown types silently: only the HeartBeat report exists.
        var body = Assert.Single(reportedBodies);
        Assert.Contains("\"task\":\"91\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"task\":\"90\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GachaDispatch_WaitsForSessionIdle_BeforeCompleting()
    {
        await using var bridge = new FakeBridge();
        var fixture = await ExecFixture.CreateAsync(bridge);
        try
        {
            using var pumpCts = new CancellationTokenSource();
            var pumpTask = fixture.Session.StartCallbackPumpAsync(_ => Task.CompletedTask, pumpCts.Token);

            Assert.True((await fixture.Session.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
            Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

            var toolbox = new FakeToolboxFeatureService(fixture.Session);
            var dispatcher = new RemoteControlCommandDispatcher(
                fixture.Config, fixture.Session, null, null, toolbox, bridge, fixture.Log);
            var request = new RemoteControlCommandRequest("Toolbox-GachaOnce", null, "alice", "pc-1", TaskId: "7");

            var dispatchTask = dispatcher.DispatchAsync(request, CancellationToken.None);
            await WaitUntilAsync(() => fixture.Session.CurrentState == SessionState.Running);

            // WPF parity: the sequential loop only reports after UntilIdleAsync,
            // so the dispatch must still be pending while the session runs.
            await Task.Delay(300);
            Assert.False(dispatchTask.IsCompleted);

            bridge.PublishCallback(new CoreCallbackEvent(1, "AllTasksCompleted", "{}", DateTimeOffset.Now));
            var result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Success, result.Message);
            Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);

            pumpCts.Cancel();
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
                // The callback pump exits via cancellation.
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CaptureCommands_ForceFreshFrameRead()
    {
        await using var bridge = new FakeBridge();
        var fixture = await ExecFixture.CreateAsync(bridge);
        try
        {
            var connection = new CoreConnectionInfo("127.0.0.1:5555", "General", null);
            Assert.True((await fixture.Session.ConnectAsync(connection)).Success);
            var connectFeature = new FakeConnectFeatureService(fixture.Session, [connection]);
            var dispatcher = new RemoteControlCommandDispatcher(
                fixture.Config, fixture.Session, connectFeature, null, null, bridge, fixture.Log);

            foreach (var command in new[] { "CaptureImage", "CaptureImageNow" })
            {
                var result = await dispatcher.DispatchAsync(
                    new RemoteControlCommandRequest(command, null, "alice", "pc-1", TaskId: "1"),
                    CancellationToken.None);
                Assert.True(result.Success, result.Message);
                Assert.NotNull(result.ImageBytes);
            }

            // WPF parity: both commands force a fresh screencap; the cached
            // frame read is never used for remote-control captures.
            Assert.Equal(2, bridge.GetFreshImageCallCount);
            Assert.Equal(0, bridge.GetImageCallCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stage1_WithoutFightTask_SucceedsAndPersistsGlobalStage()
    {
        await using var bridge = new FakeBridge();
        var fixture = await ExecFixture.CreateAsync(bridge);
        try
        {
            Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
            profile.TaskQueue.RemoveAll(task =>
                string.Equals(TaskModuleTypes.Normalize(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase));

            var dispatcher = new RemoteControlCommandDispatcher(
                fixture.Config, fixture.Session, null, null, null, bridge, fixture.Log);
            var request = new RemoteControlCommandRequest(
                "Settings-Stage1",
                new JsonObject { ["params"] = "CE-6" },
                "alice",
                "pc-1",
                TaskId: "12");

            var result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            // WPF parity: FightTask is an always-present settings model in WPF,
            // so Settings-Stage1 succeeds even with no Fight task queued.
            Assert.True(result.Success, result.Message);
            Assert.Equal("CE-6", fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Stage1]?.ToString());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stage1_WithFightTask_UpdatesFightTaskParams()
    {
        await using var bridge = new FakeBridge();
        var fixture = await ExecFixture.CreateAsync(bridge);
        try
        {
            Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
            if (profile.TaskQueue.All(task =>
                    !string.Equals(TaskModuleTypes.Normalize(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase)))
            {
                profile.TaskQueue.Add(new UnifiedTaskItem { Type = TaskModuleTypes.Fight, Name = "Fight", IsEnabled = true });
            }

            var dispatcher = new RemoteControlCommandDispatcher(
                fixture.Config, fixture.Session, null, null, null, bridge, fixture.Log);
            var request = new RemoteControlCommandRequest(
                "Settings-Stage1",
                new JsonObject { ["params"] = "1-7" },
                "alice",
                "pc-1",
                TaskId: "13");

            var result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            var fight = profile.TaskQueue.First(task =>
                string.Equals(TaskModuleTypes.Normalize(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("1-7", fight.Params["stage"]?.ToString());
            Assert.Equal("1-7", fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Stage1]?.ToString());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class ExecFixture : IAsyncDisposable
    {
        private ExecFixture(string root, UnifiedConfigurationService config, UnifiedSessionService session, UiLogService log)
        {
            Root = root;
            Config = config;
            Session = session;
            Log = log;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public UnifiedSessionService Session { get; }

        public UiLogService Log { get; }

        public static async Task<ExecFixture> CreateAsync(IMaaCoreBridge bridge)
        {
            var root = Path.Combine(Path.GetTempPath(), "maaunified-rc-exec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var log = new UiLogService();
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();
            return new ExecFixture(
                root,
                config,
                new UnifiedSessionService(bridge, config, log, new SessionStateMachine()),
                log);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Temporary test directory cleanup is best effort.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();
        private bool _connected;

        public int GetImageCallCount { get; private set; }

        public int GetFreshImageCallCount { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            _connected = true;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_connected
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            GetImageCallCount++;
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "cached frame read must not be used")));
        }

        public Task<CoreResult<byte[]>> GetFreshImageAsync(CancellationToken cancellationToken = default)
        {
            GetFreshImageCallCount++;
            return Task.FromResult(CoreResult<byte[]>.Ok([9, 9, 9]));
        }

        public void PublishCallback(CoreCallbackEvent callback) => _callbacks.Writer.TryWrite(callback);

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

    private sealed class FakeToolboxFeatureService(UnifiedSessionService session) : IToolboxFeatureService
    {
        public async Task<UiOperationResult<ToolboxDispatchResult>> DispatchToolAsync(
            ToolboxDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var start = await session.StartAsync(cancellationToken);
            return start.Success
                ? UiOperationResult<ToolboxDispatchResult>.Ok(
                    new ToolboxDispatchResult(request.Tool, "drawCount=1", DateTimeOffset.Now, 1, "Gacha"),
                    "Tool dispatched.")
                : UiOperationResult<ToolboxDispatchResult>.Fail(UiErrorCode.ToolboxExecutionFailed, "start failed");
        }

        public Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("stopped"));
    }

    private sealed class FakeConnectFeatureService(
        UnifiedSessionService session,
        IReadOnlyList<CoreConnectionInfo> candidates) : IConnectFeatureService
    {
        public Task<CoreResult<bool>> ValidateAndConnectAsync(
            string address,
            string config,
            string? adbPath,
            CancellationToken cancellationToken = default)
            => session.ConnectAsync(address, config, adbPath, cancellationToken: cancellationToken);

        public async Task<UiOperationResult> ConnectAsync(
            string address,
            string config,
            string? adbPath,
            CancellationToken cancellationToken = default)
            => UiOperationResult.FromCore(
                await session.ConnectAsync(address, config, adbPath, cancellationToken: cancellationToken),
                $"Connected to {address}");

        public async Task<UiOperationResult> ConnectAsync(
            CoreConnectionInfo connectionInfo,
            CoreInstanceOptions? instanceOptions = null,
            CancellationToken cancellationToken = default)
            => UiOperationResult.FromCore(
                await session.ConnectAsync(connectionInfo, cancellationToken),
                $"Connected to {connectionInfo.Address}");

        public UiOperationResult<IReadOnlyList<CoreConnectionInfo>> BuildCurrentProfileConnectionCandidates(
            bool includeConfiguredAddress = true)
            => UiOperationResult<IReadOnlyList<CoreConnectionInfo>>.Ok(candidates, "Connection candidates built.");

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions? instanceOptions = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public async Task<UiOperationResult> StartAsync(CancellationToken cancellationToken = default)
            => UiOperationResult.FromCore(await session.StartAsync(cancellationToken), "Task execution started.");

        public async Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
            => UiOperationResult.FromCore(await session.StopAsync(cancellationToken), "Task execution stopped.");

        public Task<UiOperationResult> WaitAndStopAsync(TimeSpan wait, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("stopped"));

        public Task<UiOperationResult<ImportReport>> ImportLegacyConfigAsync(
            ImportSource source,
            bool manualImport,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<ImportReport>.Fail(UiErrorCode.RemoteControlUnsupported, "unsupported"));
    }
}
