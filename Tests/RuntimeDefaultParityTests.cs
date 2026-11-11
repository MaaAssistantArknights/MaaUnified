using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Advanced;
using MAAUnified.App.ViewModels.Settings;
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
/// M01 verifier/fixer round 10: pins Avalonia runtime defaults against the frozen WPF defaults
/// (frozen commit 23ddd271b40a3d0c18de8a6ae8305357cc032e19). The pinned values were audited
/// against frozen WPF source: background opacity "50" / blur "5"
/// (BackgroundSettingsUserControlModel.cs), remote-control poll interval "1000"
/// (RemoteControlUserControlModel.cs).
/// </summary>
[Collection("MainShellSerial")]
public sealed class RuntimeDefaultParityTests
{
    [Fact]
    public void StartupShellSnapshot_Defaults_ShouldMatchFrozenWpfBackgroundDefaults()
    {
        Assert.Equal(50, StartupShellSnapshot.Default.BackgroundOpacity);
        Assert.Equal(5, StartupShellSnapshot.Default.BackgroundBlur);
    }

    [Fact]
    public void StartupShellSnapshot_FromConfig_WithoutBackgroundKeys_ShouldFallBackToFrozenWpfDefaults()
    {
        var snapshot = StartupShellSnapshot.FromConfig(new UnifiedConfig());

        Assert.Equal(50, snapshot.BackgroundOpacity);
        Assert.Equal(5, snapshot.BackgroundBlur);
    }

    [Fact]
    public async Task SettingsPage_FreshConfig_ShouldExposeFrozenWpfBackgroundAndRemoteDefaults()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(50, vm.BackgroundOpacity);
        Assert.Equal(5, vm.BackgroundBlur);
        Assert.Equal(1000, vm.RemotePollInterval);
    }

    [Fact]
    public async Task RemoteControlCenter_FreshConfig_ShouldDefaultToFrozenWpfPollInterval()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new RemoteControlCenterPageViewModel(fixture.Runtime);
        Assert.Equal(1000, vm.PollIntervalMs);

        await vm.InitializeAsync();

        Assert.Equal(1000, vm.PollIntervalMs);
    }

    [Fact]
    public async Task RemoteControlFeatureService_FreshConfig_ShouldPollAtFrozenWpfDefault()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var service = new RemoteControlFeatureService(fixture.Config, null, null, null, null, null, null);

        // Prove the config-read branch is reached before asserting the fallback.
        var config = fixture.Config.CurrentConfig;
        config.Profiles[config.CurrentProfile].Values[ConfigurationKeys.RemoteControlPollIntervalMs] = JsonValue.Create("2500");
        Assert.Equal(2500, service.ReadSnapshot().PollIntervalMs);

        config.Profiles[config.CurrentProfile].Values.Remove(ConfigurationKeys.RemoteControlPollIntervalMs);
        Assert.Equal(1000, service.ReadSnapshot().PollIntervalMs);
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, MAAUnifiedRuntime runtime, UnifiedConfigurationService config)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public static async Task<RuntimeFixture> CreateAsync()
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

            var bridge = new FakeBridge();
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
            var connect = new ConnectFeatureService(session, config);
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
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config);
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
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
