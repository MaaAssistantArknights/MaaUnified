using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class TaskQueueReportRequestTests
{
    [Fact]
    public async Task ReportRequest_PenguinSuccess_ShouldPersistAssignedPenguinId()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { { GameDataReportService.PenguinIdResponseHeader, "penguin-123" } },
        });
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://penguin-stats.io/api/v2/report","subtask":"ReportToPenguinStats","headers":{"X-Penguin-Idempotency-Key":"MAAkey"},"body":"{}"}""");

        Assert.Single(handler.Requests);
        Assert.Equal("penguin-123", ReadProfileString(fixture, ConfigurationKeys.PenguinId));

        // The write-back is persisted, not just kept in memory.
        var persisted = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "config", "avalonia.json"));
        Assert.Contains("penguin-123", persisted, StringComparison.Ordinal);

        Assert.Contains(fixture.Runtime.LogService.Snapshot, entry => entry.Message.Contains("New PenguinId got: penguin-123", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.LogCards.SelectMany(card => card.Items), entry => entry.Content.Contains("Failed to report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportRequest_ExistingPenguinId_ShouldNotOverwrite()
    {
        await using var fixture = await TestFixture.CreateAsync();
        SetProfileValue(fixture, ConfigurationKeys.PenguinId, "existing-id");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { { GameDataReportService.PenguinIdResponseHeader, "new-id" } },
        });
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://penguin-stats.io/api/v2/report","subtask":"ReportToPenguinStats","headers":{},"body":"{}"}""");

        Assert.Single(handler.Requests);
        Assert.Equal("existing-id", ReadProfileString(fixture, ConfigurationKeys.PenguinId));
    }

    [Fact]
    public async Task ReportRequest_TxwyClient_ShouldSkipPenguinUpload()
    {
        await using var fixture = await TestFixture.CreateAsync();
        SetProfileValue(fixture, "ClientType", "txwy");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://penguin-stats.io/api/v2/report","subtask":"ReportToPenguinStats","headers":{},"body":"{}"}""");

        Assert.Empty(handler.Requests);
        Assert.Contains(fixture.Runtime.LogService.Snapshot, entry => entry.Message.Contains("PenguinStats report skipped for txwy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportRequest_TxwyClient_ShouldStillUploadYituliu()
    {
        await using var fixture = await TestFixture.CreateAsync();
        SetProfileValue(fixture, "ClientType", "txwy");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://yituliu.example.com/api/report","subtask":"ReportToYituliu","headers":{},"body":"{}"}""");

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReportRequest_EmptyUrl_ShouldNotSendRequest()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"","subtask":"ReportToPenguinStats","headers":{},"body":"{}"}""");

        Assert.Empty(handler.Requests);
        Assert.Contains(fixture.Runtime.LogService.Snapshot, entry => entry.Message.Contains("empty URL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportRequest_EmptyBody_ShouldNotSendRequest()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://penguin-stats.io/api/v2/report","subtask":"ReportToPenguinStats","headers":{},"body":""}""");

        Assert.Empty(handler.Requests);
        Assert.Contains(fixture.Runtime.LogService.Snapshot, entry => entry.Message.Contains("empty body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportRequest_PenguinTotalFailure_ShouldAppendGiveUpWarning()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://penguin-stats.io/api/v2/report","subtask":"ReportToPenguinStats","headers":{},"body":"{}"}""");

        var warnings = vm.LogCards.SelectMany(card => card.Items)
            .Where(entry => string.Equals(entry.Level, "WARN", StringComparison.Ordinal))
            .Select(entry => entry.Content)
            .ToArray();
        Assert.Contains(warnings, content => content.Contains("Failed to report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportRequest_YituliuFailure_ShouldStaySilent()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var vm = CreateViewModel(fixture, handler);

        await InvokeReportRequestAsync(
            vm,
            """{"url":"https://yituliu.example.com/api/report","subtask":"ReportToYituliu","headers":{},"body":"{}"}""");

        Assert.DoesNotContain(vm.LogCards.SelectMany(card => card.Items), entry => entry.Content.Contains("Failed to report", StringComparison.Ordinal));
    }

    private static TaskQueuePageViewModel CreateViewModel(TestFixture fixture, FakeHandler handler)
    {
        var reportService = new GameDataReportService(
            new HttpClient(handler),
            delayAsync: (_, _) => Task.CompletedTask);
        return new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            gameDataReportService: reportService);
    }

    private static void SetProfileValue(TestFixture fixture, string key, string value)
    {
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values[key] = JsonValue.Create(value);
    }

    private static string? ReadProfileString(TestFixture fixture, string key)
    {
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        return profile.Values.TryGetValue(key, out var node) && node is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;
    }

    private static async Task InvokeReportRequestAsync(TaskQueuePageViewModel vm, string payloadJson)
    {
        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var callback = new CoreCallbackEvent(30000, "ReportRequest", payloadJson, DateTimeOffset.UtcNow);
        var task = method.Invoke(vm, [callback]) as Task;
        Assert.NotNull(task);
        await task;

        var field = typeof(TaskQueuePageViewModel).GetField(
            "_reportRequestTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var reportTask = field.GetValue(vm) as Task;
        Assert.NotNull(reportTask);
        await reportTask;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(request);
            return _responder(request);
        }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            MAAUnifiedRuntime runtime,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(string language = "zh-cn")
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();
            config.CurrentConfig.GlobalValues["GUI.Localization"] = language;

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);

            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connectFeatureService = new ConnectFeatureService(session, config);
            var postActionFeatureService = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService);

            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connectFeatureService,
                ShellFeatureService = new ShellFeatureService(connectFeatureService),
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postActionFeatureService,
            };

            return new TestFixture(root, config, taskQueue, runtime, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Bridge.DisposeAsync();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // keep temporary folder for inspection when cleanup fails.
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
