using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class RuntimeDisposalTests
{
    [Fact]
    public async Task DisposeAsync_ShouldDisposeNotificationService_WhenProviderIsDisposable()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-runtime-disposal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var log = new UiLogService();
        var diagnostics = new UiDiagnosticsService(root, log);
        var config = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root);
        var bridge = new NullBridge();
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
        var notificationService = new DisposableNotificationService();
        var platform = new PlatformServiceBundle
        {
            TrayService = new NoOpTrayService(),
            NotificationService = notificationService,
            HotkeyService = new NoOpGlobalHotkeyService(),
            AutostartService = new NoOpAutostartService(),
            FileDialogService = new NoOpFileDialogService(),
            OverlayService = new NoOpOverlayCapabilityService(),
            PostActionExecutorService = new NoOpPostActionExecutorService(),
        };

        var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
        var runtime = new MAAUnifiedRuntime
        {
            CoreBridge = bridge,
            ConfigurationService = config,
            ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
            SessionService = session,
            Platform = platform,
            LogService = log,
            DiagnosticsService = diagnostics,
            ConnectFeatureService = new ConnectFeatureService(session, config),
            ShellFeatureService = new ShellFeatureService(new ConnectFeatureService(session, config)),
            TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
            CopilotFeatureService = new CopilotFeatureService(),
            ToolboxFeatureService = new ToolboxFeatureService(),
            RemoteControlFeatureService = new RemoteControlFeatureService(),
            PlatformCapabilityService = capability,
            OverlayFeatureService = new OverlayFeatureService(capability),
            NotificationProviderFeatureService = new NotificationProviderFeatureService(),
            SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
            DialogFeatureService = new DialogFeatureService(diagnostics),
            PostActionFeatureService = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService),
            UiLanguageCoordinator = new UiLanguageCoordinator(config),
        };

        try
        {
            await runtime.DisposeAsync();
            Assert.True(notificationService.Disposed);
        }
        finally
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
    }

    private sealed class DisposableNotificationService : INotificationService, IDisposable
    {
        public bool Disposed { get; private set; }

        public PlatformCapabilityStatus Capability => new(
            Supported: true,
            Message: "test notification service",
            Provider: "test");

        public Task<PlatformOperationResult> NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "notified", "notification.notify"));

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class NullBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();

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

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

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
}
