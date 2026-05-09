using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;
using Xunit;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class TaskQueueNotificationTests
{
    [Fact]
    public async Task AllTasksCompleted_SendsNotification_WhenUseNotifyEnabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["RunId"] = "completion-run",
        };
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "AllTasksCompleted", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Equal("All tasks completed", fixture.NotificationCapability.LastTitle);
        Assert.False(string.IsNullOrWhiteSpace(fixture.NotificationCapability.LastMessage));
    }

    [Fact]
    public async Task AllTasksCompleted_DoesNotSendNotification_WhenUseNotifyDisabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: false);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["RunId"] = "completion-run",
        };
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "AllTasksCompleted", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await Task.Delay(50);
        Assert.Equal(0, fixture.NotificationCapability.NotificationCallCount);
    }

    [Fact]
    public async Task TaskChainError_SendsNotification_WhenUseNotifyEnabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["RunId"] = "error-run",
            ["TaskChain"] = "Fight",
        };
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "TaskChainError", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Contains("failed", fixture.NotificationCapability.LastTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Task error", fixture.NotificationCapability.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForNotificationCountAsync(
        NotificationTrackingPlatformCapabilityService service,
        int expectedCount,
        int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (service.NotificationCallCount != expectedCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (service.NotificationCallCount != expectedCount)
        {
            throw new TimeoutException(
                $"Expected notification count {expectedCount}, but saw {service.NotificationCallCount}.");
        }
    }

    private sealed class NotificationFixture : IAsyncDisposable
    {
        private NotificationFixture(
            string root,
            UnifiedConfigurationService config,
            MAAUnifiedRuntime runtime,
            NotificationTrackingPlatformCapabilityService notificationCapability,
            TaskQueuePageViewModel viewModel)
        {
            Root = root;
            Config = config;
            Runtime = runtime;
            NotificationCapability = notificationCapability;
            ViewModel = viewModel;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public NotificationTrackingPlatformCapabilityService NotificationCapability { get; }

        public TaskQueuePageViewModel ViewModel { get; }

        public static async Task<NotificationFixture> CreateAsync(bool useNotify, string language = "en-us")
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-taskqueue-notification-tests", Guid.NewGuid().ToString("N"));
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

            config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create(language);
            config.CurrentConfig.GlobalValues[ConfigurationKeys.UseNotify] = JsonValue.Create(useNotify);
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values["ClientType"] = JsonValue.Create("Official");
                profile.Values["ServerType"] = JsonValue.Create("CN");
                profile.Values["ConnectAddress"] = JsonValue.Create("127.0.0.1:5555");
                profile.Values["ConnectConfig"] = JsonValue.Create("General");
                profile.Values["AutoDetect"] = JsonValue.Create(true);
            }

            var bridge = new TestBridge();
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
            var tracking = new NotificationTrackingPlatformCapabilityService(capability);
            var connect = new ConnectFeatureService(session, config);
            var shell = new ShellFeatureService(connect);
            var taskQueue = new TaskQueueFeatureService(session, config);
            var dialog = new DialogFeatureService(diagnostics);
            var postAction = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService);
            var versionUpdate = new VersionUpdateFeatureService(config, diagnostics, uiLogService: log, runtimeBaseDirectory: root);

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
                ShellFeatureService = shell,
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = tracking,
                OverlayFeatureService = new OverlayFeatureService(tracking),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, tracking, diagnostics),
                DialogFeatureService = dialog,
                PostActionFeatureService = postAction,
                VersionUpdateFeatureService = versionUpdate,
                UiLanguageCoordinator = new UiLanguageCoordinator(config),
            };

            var connectionState = new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5555",
                ConnectConfig = "General",
                ClientType = "Official",
                AutoDetect = true,
            };

            var viewModel = new TaskQueuePageViewModel(runtime, connectionState);
            return new NotificationFixture(root, config, runtime, tracking, viewModel);
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
                // ignore cleanup failures
            }
        }

        public static async Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
        {
            var method = typeof(TaskQueuePageViewModel).GetMethod(
                "HandleCallbackCoreAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method is null)
            {
                throw new InvalidOperationException("Unable to locate TaskQueue callback handler.");
            }

            var result = method.Invoke(vm, new object?[] { callback }) as Task
                ?? throw new InvalidOperationException("TaskQueue callback handler did not return a task.");
            await result;
        }

        private sealed class TestBridge : IMaaCoreBridge
        {
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
                => Task.FromResult(CoreResult<byte[]>.Ok(Array.Empty<byte>()));

            public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
 
            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;
        }
    }
}
