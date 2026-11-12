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

/// <summary>
/// WPF parity coverage for P1-04 (dev-v2 d58cb0825b / 3b4f06dd86 / 0b048d5e94):
/// <c>PostActions.LockScreen</c> (1 &lt;&lt; 9), <c>PostActions.ExitPCClient</c> (1 &lt;&lt; 10),
/// connection-mode filtered exit targets, the pre-await snapshot and the cancellable countdown
/// with a keep-awake lease.
/// </summary>
public sealed class PostActionParityFeatureTests
{
    [Fact]
    public void PostActionType_DevV2Additions_MatchWpfPostActionsBitPositions()
    {
        // Frozen WPF PostActionSetting.PostActions values (v6.3.7 + dev-v2 additions).
        Assert.Equal(0, (int)PostActionType.ExitArknights);
        Assert.Equal(1, (int)PostActionType.BackToAndroidHome);
        Assert.Equal(2, (int)PostActionType.ExitEmulator);
        Assert.Equal(3, (int)PostActionType.ExitSelf);
        Assert.Equal(4, (int)PostActionType.Hibernate);
        Assert.Equal(5, (int)PostActionType.Shutdown);
        Assert.Equal(6, (int)PostActionType.Sleep);
        Assert.Equal(1 << 9, (int)PostActionType.LockScreen);
        Assert.Equal(1 << 10, (int)PostActionType.ExitPCClient);
    }

    [Fact]
    public void PostActionCapabilityMatrix_NewActions_DegradeExplicitlyWhenPlatformLeavesThemUnset()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var matrix = new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported);

        var lockScreen = matrix.Get(PostActionType.LockScreen);
        Assert.False(lockScreen.Supported);
        Assert.Contains("unsupported", lockScreen.Message, StringComparison.OrdinalIgnoreCase);

        var exitPCClient = matrix.Get(PostActionType.ExitPCClient);
        Assert.False(exitPCClient.Supported);
        Assert.Contains("unsupported", exitPCClient.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostActionConfig_RoundTripsP104Fields_AndKeepsLegacyJsonReadable()
    {
        var config = new PostActionConfig
        {
            LockScreen = true,
            ExitPCClient = true,
        };

        var json = config.ToJson();
        Assert.True(json["lock_screen"]!.GetValue<bool>());
        Assert.True(json["exit_pc_client"]!.GetValue<bool>());

        var parsed = PostActionConfig.FromJson(json);
        Assert.True(parsed.LockScreen);
        Assert.True(parsed.ExitPCClient);
        Assert.True(parsed.HasAnyAction());

        var clone = parsed.Clone();
        Assert.True(clone.LockScreen);
        Assert.True(clone.ExitPCClient);

        // Historical PascalCase payload (pre-P1-04 profiles) must still load.
        var pascal = PostActionConfig.FromJson(JsonNode.Parse("""{"LockScreen":true,"ExitPCClient":true}"""));
        Assert.True(pascal.LockScreen);
        Assert.True(pascal.ExitPCClient);

        // Old configs without the new keys keep every earlier field and default to false.
        var legacy = PostActionConfig.FromJson(JsonNode.Parse("""{"exit_arknights":true,"sleep":true}"""));
        Assert.True(legacy.ExitArknights);
        Assert.True(legacy.Sleep);
        Assert.False(legacy.LockScreen);
        Assert.False(legacy.ExitPCClient);
        Assert.False(PostActionConfig.Default.HasAnyAction());
    }

    [Fact]
    public async Task PostActionModule_ConnectionMode_FiltersVisibilityAndRuntimeConfig_WithoutRewritingSelections()
    {
        var shared = new ConnectionGameSharedStateViewModel();
        await using var fixture = await TestFixture.CreateAsync(executor: CreateSupportedExecutor());
        await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            ExitArknights = true,
            ExitPCClient = true,
        });

        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" }, shared);
        await vm.InitializeAsync();

        // ADB (default) connection: emulator/Arknights exits are shown, the PC client exit is not.
        Assert.False(vm.IsPCConnectMode);
        Assert.True(vm.ShowExitArknights);
        Assert.False(vm.ShowExitPCClient);
        Assert.True(vm.ExitArknights);
        Assert.False(vm.ExitPCClient);

        var adbConfig = vm.BuildRuntimeConfig();
        Assert.True(adbConfig.ExitArknights);
        Assert.False(adbConfig.ExitPCClient);

        // PC (window binding) connection: only the PC client exit is shown/executed.
        shared.ConnectConfig = "PC";
        Assert.True(vm.IsPCConnectMode);
        Assert.False(vm.ShowExitArknights);
        Assert.False(vm.ShowExitEmulator);
        Assert.True(vm.ShowExitPCClient);
        Assert.False(vm.ExitArknights);
        Assert.True(vm.ExitPCClient);

        var pcConfig = vm.BuildRuntimeConfig();
        Assert.False(pcConfig.ExitArknights);
        Assert.False(pcConfig.ExitEmulator);
        Assert.True(pcConfig.ExitPCClient);

        // Switching back never rewrote the stored selection (WPF 3b4f06dd86).
        shared.ConnectConfig = "General";
        Assert.True(vm.ExitArknights);
        Assert.False(vm.ExitPCClient);

        Assert.True(await vm.FlushPendingChangesAsync());
        var persisted = await fixture.PostAction.LoadAsync();
        Assert.True(persisted.Success);
        Assert.True(persisted.Value!.ExitArknights);
        Assert.True(persisted.Value.ExitPCClient);
        Assert.False(persisted.Value.ExitEmulator);
    }

    [Fact]
    public async Task PostActionModule_RuntimeConfig_IsASnapshot_ThatIgnoresLaterConnectionSwitch()
    {
        var shared = new ConnectionGameSharedStateViewModel { ConnectConfig = "PC" };
        await using var fixture = await TestFixture.CreateAsync(executor: CreateSupportedExecutor());
        await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            ExitArknights = true,
            ExitPCClient = true,
        });

        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" }, shared);
        await vm.InitializeAsync();

        // WPF 3b4f06dd86: the exit targets are read once, before the post-action sequence awaits.
        var snapshot = vm.BuildRuntimeConfig();
        Assert.True(snapshot.ExitPCClient);
        Assert.False(snapshot.ExitArknights);

        shared.ConnectConfig = "General";

        Assert.True(snapshot.ExitPCClient);
        Assert.False(snapshot.ExitArknights);

        var afterSwitch = vm.BuildRuntimeConfig();
        Assert.False(afterSwitch.ExitPCClient);
        Assert.True(afterSwitch.ExitArknights);
    }

    [Fact]
    public async Task PostActionModule_ExitPCClientAndExitArknights_AreDecoupled()
    {
        var shared = new ConnectionGameSharedStateViewModel();
        await using var fixture = await TestFixture.CreateAsync(executor: CreateSupportedExecutor());
        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" }, shared);
        await vm.InitializeAsync();

        vm.ExitArknights = true;
        vm.ExitPCClient = true;

        // The PC client selection did not clear the Arknights selection; each is only executed for
        // its own connection mode.
        Assert.True(vm.ExitArknights);
        Assert.False(vm.ExitPCClient);
        Assert.True(vm.BuildRuntimeConfig().ExitArknights);
        Assert.False(vm.BuildRuntimeConfig().ExitPCClient);

        shared.ConnectConfig = "PC";

        Assert.False(vm.ExitArknights);
        Assert.True(vm.ExitPCClient);

        shared.ConnectConfig = "General";

        Assert.True(vm.ExitArknights);
        Assert.False(vm.ExitPCClient);
    }

    [Fact]
    public async Task PostAction_Execute_ExitPCClient_UsesCoreCloseDown_WithItsOwnActionIdentity()
    {
        var bridge = new FakeBridge { SupportsNativeCloseDown = true };
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-pc-client"),
            new PostActionConfig { ExitPCClient = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, bridge.StartCloseDownCallCount);
        Assert.Contains("ExitPCClient", execute.Message, StringComparison.Ordinal);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=ExitPCClient", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostAction_Execute_ExitArknightsAndExitPCClient_ShareOneCloseDownCall()
    {
        var bridge = new FakeBridge { SupportsNativeCloseDown = true };
        await using var fixture = await TestFixture.CreateAsync(bridge: bridge);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-close-down-once"),
            new PostActionConfig { ExitArknights = true, ExitPCClient = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, bridge.StartCloseDownCallCount);
    }

    [Fact]
    public async Task PostAction_Execute_LockScreen_CountdownHoldsKeepAwake_ThenLocks()
    {
        var events = new List<string>();
        var prompt = new RecordingPromptService(UiOperationResult.Ok("confirmed"), events);
        var keepAwake = new RecordingKeepAwakeService(events);
        var executor = CreateSupportedExecutor(events);
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            promptService: prompt,
            keepAwakeService: keepAwake);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-lock"),
            new PostActionConfig { LockScreen = true });

        Assert.True(execute.Success, execute.Message);
        var request = Assert.Single(prompt.Requests);
        Assert.Equal(PostActionType.LockScreen, request.Action);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Countdown);
        Assert.Contains(PostActionType.LockScreen, executor.ExecutedActions);
        Assert.Equal(1, keepAwake.CreateCount);
        Assert.Equal(1, keepAwake.DisposeCount);

        // Keep-awake must cover the countdown and be released before the lock runs, otherwise it
        // would keep blocking a configured Sleep/Shutdown (WPF 0b048d5e94).
        var acquire = events.IndexOf("keep-awake:acquire");
        var promptIndex = events.IndexOf("prompt:LockScreen");
        var release = events.IndexOf("keep-awake:release");
        var executed = events.IndexOf("execute:LockScreen");
        Assert.True(acquire >= 0, string.Join(",", events));
        Assert.True(acquire < promptIndex, string.Join(",", events));
        Assert.True(promptIndex < release, string.Join(",", events));
        Assert.True(release < executed, string.Join(",", events));
    }

    [Fact]
    public async Task PostAction_Execute_LockScreen_CancelledCountdown_SkipsLockAndReleasesKeepAwake()
    {
        var events = new List<string>();
        var prompt = new RecordingPromptService(UiOperationResult.Cancelled("cancelled"), events);
        var keepAwake = new RecordingKeepAwakeService(events);
        var executor = CreateSupportedExecutor(events);
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            promptService: prompt,
            keepAwakeService: keepAwake);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-lock-cancel"),
            new PostActionConfig { LockScreen = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Single(prompt.Requests);
        Assert.DoesNotContain(PostActionType.LockScreen, executor.ExecutedActions);
        Assert.Equal(1, keepAwake.CreateCount);
        Assert.Equal(1, keepAwake.DisposeCount);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=LockScreen", eventLog, StringComparison.Ordinal);
        Assert.Contains($"errorCode={UiErrorCode.PostActionCancelled}", eventLog);
    }

    [Fact]
    public async Task PostAction_Execute_LockScreen_CountsAsSystemAction_AndSuppressesExitSelf()
    {
        var prompt = new RecordingPromptService(UiOperationResult.Ok("confirmed"));
        var executor = CreateSupportedExecutor();
        var appLifecycle = new CountingAppLifecycleService();
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            appLifecycleService: appLifecycle,
            promptService: prompt);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-lock-exitself"),
            new PostActionConfig { ExitSelf = true, LockScreen = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Contains(PostActionType.LockScreen, executor.ExecutedActions);
        Assert.Equal(0, appLifecycle.ExitCallCount);
    }

    [Fact]
    public async Task PostAction_Execute_IfNoOtherMaaWithLockScreen_ExitsSelfInsteadOfLocking()
    {
        var prompt = new RecordingPromptService(UiOperationResult.Ok("confirmed"));
        var executor = CreateSupportedExecutor();
        var appLifecycle = new CountingAppLifecycleService();
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            appLifecycleService: appLifecycle,
            promptService: prompt,
            hasOtherMaaProcess: () => true);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-lock-ifnoother"),
            new PostActionConfig { IfNoOtherMaa = true, LockScreen = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, appLifecycle.ExitCallCount);
        Assert.DoesNotContain(PostActionType.LockScreen, executor.ExecutedActions);
        Assert.Empty(prompt.Requests);
    }

    [Fact]
    public async Task PostAction_ValidateSelection_IfNoOtherMaaAcceptsLockScreenAsSystemAction()
    {
        var prompt = new RecordingPromptService(UiOperationResult.Ok("confirmed"));
        await using var fixture = await TestFixture.CreateAsync(
            executor: CreateSupportedExecutor(),
            promptService: prompt);

        var withLockScreen = await fixture.PostAction.ValidateSelectionAsync(new PostActionConfig
        {
            IfNoOtherMaa = true,
            LockScreen = true,
        });

        Assert.True(withLockScreen.Success);
        Assert.DoesNotContain(
            "PostAction.Warn.IfNoOtherNeedsSystemAction",
            withLockScreen.Value!.Warnings);

        var withoutSystemAction = await fixture.PostAction.ValidateSelectionAsync(new PostActionConfig
        {
            IfNoOtherMaa = true,
        });

        Assert.True(withoutSystemAction.Success);
        Assert.Contains(
            "PostAction.Warn.IfNoOtherNeedsSystemAction",
            withoutSystemAction.Value!.Warnings);
    }

    [Fact]
    public void CommandPostActionExecutor_LockScreenCapability_IsNativeWindowsOrExplicitPlaceholder()
    {
        var capability = new CommandPostActionExecutorService()
            .GetCapabilityMatrix()
            .Get(PostActionType.LockScreen);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(capability.Supported, capability.Message);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // macOS 走系统 CGSession 助手：存在即支持，缺失则显式降级而不是假成功。
            Assert.Equal("macos-cgsession", capability.Provider);
            if (capability.Supported)
            {
                Assert.Contains("CGSession", capability.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("CGSession", capability.Message, StringComparison.Ordinal);
                Assert.True(capability.HasFallback);
            }

            return;
        }

        // Linux: either loginctl/xdg-screensaver is available (supported) or the placeholder
        // reports the missing commands instead of pretending to succeed.
        if (!capability.Supported)
        {
            Assert.Contains("loginctl", capability.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SleepManagementService_KeepAwakeLease_ReleasesBackendRequestExactlyOnce()
    {
        var cleared = new List<nint>();
        var service = new SleepManagementService(_ => 42, handle => cleared.Add(handle));

        var lease = service.CreateKeepAwakeRequest("MAA post-action countdown");
        Assert.NotNull(lease);

        lease!.Dispose();
        lease.Dispose();

        var handle = Assert.Single(cleared);
        Assert.Equal((nint)42, handle);
    }

    [Fact]
    public void SleepManagementService_ReturnsNullLease_WhenBackendCannotKeepAwake()
    {
        var clearCalls = 0;
        var service = new SleepManagementService(_ => 0, _ => clearCalls++);

        Assert.Null(service.CreateKeepAwakeRequest("MAA post-action countdown"));
        Assert.Equal(0, clearCalls);
    }

    [Fact]
    public void SleepManagementService_Capability_DegradesExplicitlyOffWindows()
    {
        var service = new SleepManagementService();

        if (OperatingSystem.IsWindows())
        {
            Assert.True(service.Capability.Supported, service.Capability.Message);
            return;
        }

        // macOS/Linux are annotated placeholders: countdown runs without sleep protection.
        Assert.False(service.Capability.Supported);
        Assert.Contains("not implemented", service.Capability.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.Capability.HasFallback);
        Assert.Null(service.CreateKeepAwakeRequest("MAA post-action countdown"));
    }

    private static FakePostActionExecutorService CreateSupportedExecutor(List<string>? events = null)
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        return new FakePostActionExecutorService(
            new PostActionCapabilityMatrix(
                ExitArknights: supported,
                BackToAndroidHome: supported,
                ExitEmulator: supported,
                ExitSelf: supported,
                Hibernate: supported,
                Shutdown: supported,
                Sleep: supported,
                LockScreen: supported,
                ExitPCClient: supported),
            events);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            PostActionFeatureService postAction,
            MAAUnifiedRuntime runtime,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            PostAction = postAction;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public PostActionFeatureService PostAction { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(
            IPostActionExecutorService? executor = null,
            FakeBridge? bridge = null,
            IPostActionPromptService? promptService = null,
            IAppLifecycleService? appLifecycleService = null,
            Func<bool>? hasOtherMaaProcess = null,
            IKeepAwakeService? keepAwakeService = null)
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
            config.CurrentConfig.GlobalValues["GUI.Localization"] = "en-us";
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }

            bridge ??= new FakeBridge();
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
                PostActionExecutorService = executor ?? new NoOpPostActionExecutorService(),
                KeepAwakeService = keepAwakeService ?? NoOpKeepAwakeService.Instance,
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var lifecycle = appLifecycleService ?? new NoOpAppLifecycleService();
            var postAction = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService,
                bridge,
                lifecycle,
                promptService ?? new NoOpPostActionPromptService(),
                session,
                hasOtherMaaProcess,
                platform.KeepAwakeService);
            var connectFeatureService = new ConnectFeatureService(session, config, log, bridge, root);
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
                PostActionFeatureService = postAction,
                AppLifecycleService = lifecycle,
            };

            return new TestFixture(root, config, postAction, runtime, bridge);
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
                // keep the temporary folder for inspection when cleanup fails.
            }
        }
    }

    private sealed class FakePostActionExecutorService : IPostActionExecutorService
    {
        private readonly List<string>? _events;

        public FakePostActionExecutorService(PostActionCapabilityMatrix matrix, List<string>? events = null)
        {
            CapabilityMatrix = matrix;
            _events = events;
        }

        public PostActionCapabilityMatrix CapabilityMatrix { get; }

        public List<PostActionType> ExecutedActions { get; } = [];

        public PostActionCapabilityMatrix GetCapabilityMatrix(PostActionExecutorRequest? request = null)
            => CapabilityMatrix;

        public Task<PlatformOperationResult> ExecuteAsync(
            PostActionType action,
            PostActionExecutorRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedActions.Add(action);
            _events?.Add($"execute:{action}");
            return Task.FromResult(PlatformOperation.NativeSuccess(
                CapabilityMatrix.Get(action).Provider,
                $"Executed {action}.",
                $"post-action.{action}"));
        }

        public Task<PlatformOperationResult> ExecuteUserScriptAsync(
            string scriptPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess("user-script", "script executed.", "post-action.UserScript"));
    }

    private sealed class RecordingPromptService : IPostActionPromptService
    {
        private readonly UiOperationResult _result;
        private readonly List<string>? _events;

        public RecordingPromptService(UiOperationResult result, List<string>? events = null)
        {
            _result = result;
            _events = events;
        }

        public List<PostActionPromptRequest> Requests { get; } = [];

        public Task<UiOperationResult> ConfirmPowerActionAsync(
            PostActionPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            _events?.Add($"prompt:{request.Action}");
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingKeepAwakeService : IKeepAwakeService
    {
        private readonly List<string> _events;

        public RecordingKeepAwakeService(List<string> events)
        {
            _events = events;
        }

        public int CreateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public PlatformCapabilityStatus Capability { get; } = new(
            true,
            "test keep-awake",
            Provider: "test");

        public IDisposable? CreateKeepAwakeRequest(string reason)
        {
            CreateCount += 1;
            _events.Add("keep-awake:acquire");
            return new Lease(this);
        }

        private sealed class Lease(RecordingKeepAwakeService owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                owner.DisposeCount += 1;
                owner._events.Add("keep-awake:release");
            }
        }
    }

    private sealed class CountingAppLifecycleService : IAppLifecycleService
    {
        public int ExitCallCount { get; private set; }

        public bool SupportsExit => true;

        public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("restart"));

        public Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
        {
            ExitCallCount += 1;
            return Task.FromResult(UiOperationResult.Ok("exit"));
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public bool SupportsNativeCloseDown { get; set; }

        public int StartCloseDownCallCount { get; private set; }

        public bool SupportsBackToHome => false;

        public bool SupportsStartCloseDown => SupportsNativeCloseDown;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> BackToHomeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "back-to-home unsupported")));

        public Task<CoreResult<bool>> StartCloseDownAsync(string clientType, CancellationToken cancellationToken = default)
        {
            StartCloseDownCallCount += 1;
            return Task.FromResult(
                SupportsNativeCloseDown
                    ? CoreResult<bool>.Ok(true)
                    : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "close-down unsupported")));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
