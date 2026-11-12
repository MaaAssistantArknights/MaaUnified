using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.ThirdParty;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

internal sealed class ToolboxTestFixture : IAsyncDisposable
{
    private ToolboxTestFixture(
        string root,
        UnifiedConfigurationService config,
        RecordingBridge bridge,
        MAAUnifiedRuntime runtime,
        ConnectionGameSharedStateViewModel connectionState)
    {
        Root = root;
        Config = config;
        Bridge = bridge;
        Runtime = runtime;
        ConnectionState = connectionState;
    }

    public string Root { get; }

    public UnifiedConfigurationService Config { get; }

    public RecordingBridge Bridge { get; }

    public MAAUnifiedRuntime Runtime { get; }

    public ConnectionGameSharedStateViewModel ConnectionState { get; }

    public string ReadyAdbPath { get; private set; } = string.Empty;

    public static async Task<ToolboxTestFixture> CreateAsync(
        IReadOnlyDictionary<string, string>? globalSeeds = null,
        IReadOnlyDictionary<string, JsonNode?>? profileSeeds = null,
        INotificationProviderFeatureService? notificationProviderFeatureService = null,
        IYituliuOpenApiService? yituliuOpenApiService = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-toolbox-tests", Guid.NewGuid().ToString("N"));
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
        var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "toolbox-ready");

        if (globalSeeds is not null)
        {
            foreach (var pair in globalSeeds)
            {
                config.CurrentConfig.GlobalValues[pair.Key] = JsonValue.Create(pair.Value);
            }
        }

        if (config.TryGetCurrentProfile(out var profile))
        {
            profile.Values["ClientType"] = JsonValue.Create("Official");
            profile.Values["ServerType"] = JsonValue.Create("CN");
            profile.Values["ConnectAddress"] = JsonValue.Create(TestConnectionFixtureSupport.ReadyConnectAddress);
            profile.Values["ConnectConfig"] = JsonValue.Create(TestConnectionFixtureSupport.ReadyConnectConfig);
            profile.Values["AdbPath"] = JsonValue.Create(readyAdbPath);
            profile.Values["AutoDetect"] = JsonValue.Create(true);
            profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            if (profileSeeds is not null)
            {
                foreach (var pair in profileSeeds)
                {
                    profile.Values[pair.Key] = pair.Value?.DeepClone();
                }
            }
        }

        var bridge = new RecordingBridge();
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
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
        var connect = new ConnectFeatureService(session, config, log, bridge, root);
        var runtime = new MAAUnifiedRuntime
        {
            CoreBridge = bridge,
            ConfigurationService = config,
            ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
            SessionService = session,
            Platform = platform,
            LogService = log,
            DiagnosticsService = diagnostics,
            ConnectFeatureService = connect,
            ShellFeatureService = new ShellFeatureService(connect),
            TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
            CopilotFeatureService = new CopilotFeatureService(),
            ToolboxFeatureService = new ToolboxFeatureService(bridge, connect, config),
            RemoteControlFeatureService = new RemoteControlFeatureService(),
            PlatformCapabilityService = capability,
            OverlayFeatureService = new OverlayFeatureService(capability),
            NotificationProviderFeatureService = notificationProviderFeatureService ?? new NotificationProviderFeatureService(),
            SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
            DialogFeatureService = new DialogFeatureService(diagnostics),
            PostActionFeatureService = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService),
        };

        if (yituliuOpenApiService is not null)
        {
            runtime.YituliuOpenApiService = yituliuOpenApiService;
        }

        var connectionState = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = readyAdbPath,
            ClientType = "Official",
            AutoDetect = true,
            MacUseBundledAdb = false,
        };

        return new ToolboxTestFixture(root, config, bridge, runtime, connectionState)
        {
            ReadyAdbPath = readyAdbPath,
        };
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
            // ignore temp cleanup failures
        }
    }

    internal sealed class RecordingBridge : IMaaCoreBridge, IMaaCoreBridgeQueueRecovery
    {
        private static readonly byte[] TinyPng =
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+W0cAAAAASUVORK5CYII=");
        private static readonly byte[] RawBgrFrame = BuildRawBgrFrame();

        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _nextTaskId = 1;

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public List<CoreTaskRequest> ActiveQueuedTasks { get; } = [];

        public int ConnectCallCount { get; private set; }

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public int GetImageBgrCallCount { get; private set; }

        public int RecoverTaintedTaskQueueCallCount { get; private set; }
        public int GetImageCallCount { get; private set; }

        public byte[]? RawBgrFrameOverride { get; set; }

        public bool ForceConnectFailure { get; set; }

        public CoreErrorCode ConnectFailureCode { get; set; } = CoreErrorCode.ConnectFailed;

        public string ConnectFailureMessage { get; set; } = "Connection command failed to exec";

        public Queue<CoreResult<bool>> StartResults { get; } = new();

        public Queue<CoreResult<bool>> ApplyInstanceOptionsResults { get; } = new();

        public CoreInstanceOptions? LastInstanceOptions { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            if (ForceConnectFailure)
            {
                return Task.FromResult(CoreResult<bool>.Fail(new CoreError(ConnectFailureCode, ConnectFailureMessage)));
            }

            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendedTasks.Add(task);
            ActiveQueuedTasks.Add(task);
            return Task.FromResult(CoreResult<int>.Ok(_nextTaskId++));
        }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions options,
            CancellationToken cancellationToken = default)
        {
            LastInstanceOptions = options;
            return Task.FromResult(ApplyInstanceOptionsResults.Count > 0
                ? ApplyInstanceOptionsResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return Task.FromResult(StartResults.Count > 0
                ? StartResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> RecoverTaintedTaskQueueAsync(CancellationToken cancellationToken = default)
        {
            RecoverTaintedTaskQueueCallCount++;
            ActiveQueuedTasks.Clear();
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            GetImageCallCount++;
            return Task.FromResult(CoreResult<byte[]>.Ok(TinyPng));
        }

        public Task<CoreResult<byte[]>> GetImageBgrAsync(bool forceScreencap = false, CancellationToken cancellationToken = default)
        {
            GetImageBgrCallCount++;
            return Task.FromResult(CoreResult<byte[]>.Ok(RawBgrFrameOverride ?? RawBgrFrame));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbacks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public void EnqueueCallback(CoreCallbackEvent callback)
        {
            _callbacks.Writer.TryWrite(callback);
        }

        public ValueTask DisposeAsync()
        {
            _callbacks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static byte[] BuildRawBgrFrame()
        {
            const int width = 1280;
            const int height = 720;
            const int channels = 3;
            var data = new byte[width * height * channels];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = ((y * width) + x) * channels;
                    data[offset] = (byte)(x % 256);
                    data[offset + 1] = (byte)(y % 256);
                    data[offset + 2] = 96;
                }
            }

            return data;
        }
    }
}
