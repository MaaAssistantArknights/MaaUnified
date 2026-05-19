using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using RemoteControlFeatureServiceImpl = MAAUnified.Application.Services.Features.RemoteControlFeatureService;
using StageManagerFeatureServiceImpl = MAAUnified.Application.Services.Features.StageManagerFeatureService;
using WebApiFeatureServiceImpl = MAAUnified.Application.Services.Features.WebApiFeatureService;

namespace MAAUnified.Application.Services;

public sealed class MAAUnifiedRuntime : IAsyncDisposable
{
    private IUiLanguageCoordinator? _uiLanguageCoordinator;

    public required IMaaCoreBridge CoreBridge { get; init; }

    public required UnifiedConfigurationService ConfigurationService { get; init; }

    public required ResourceWorkflowService ResourceWorkflowService { get; init; }

    public required UnifiedSessionService SessionService { get; init; }

    public required PlatformServiceBundle Platform { get; init; }

    public required UiLogService LogService { get; init; }

    public required UiDiagnosticsService DiagnosticsService { get; init; }

    public required IConnectFeatureService ConnectFeatureService { get; init; }

    public required IShellFeatureService ShellFeatureService { get; init; }

    public required ITaskQueueFeatureService TaskQueueFeatureService { get; init; }

    public required ICopilotFeatureService CopilotFeatureService { get; init; }

    public required IToolboxFeatureService ToolboxFeatureService { get; init; }

    public required IRemoteControlFeatureService RemoteControlFeatureService { get; init; }

    public required IPlatformCapabilityService PlatformCapabilityService { get; init; }

    public required IOverlayFeatureService OverlayFeatureService { get; init; }

    public required INotificationProviderFeatureService NotificationProviderFeatureService { get; init; }

    public required ISettingsFeatureService SettingsFeatureService { get; init; }

    public IUiLanguageCoordinator UiLanguageCoordinator
    {
        get
        {
            _uiLanguageCoordinator ??= new UiLanguageCoordinator(ConfigurationService);
            return _uiLanguageCoordinator;
        }
        init => _uiLanguageCoordinator = value;
    }

    public IConfigurationProfileFeatureService ConfigurationProfileFeatureService { get; init; } = new ConfigurationProfileFeatureService();

    public IVersionUpdateFeatureService VersionUpdateFeatureService { get; init; } = new VersionUpdateFeatureService();

    public IAchievementFeatureService AchievementFeatureService { get; init; } = new AchievementFeatureService();

    public IAchievementTrackerService AchievementTrackerService { get; init; } = new AchievementTrackerService();

    public IAnnouncementFeatureService AnnouncementFeatureService { get; init; } = new AnnouncementFeatureService();

    public IStageManagerFeatureService StageManagerFeatureService { get; init; } = new StageManagerFeatureServiceImpl();

    public IWebApiFeatureService WebApiFeatureService { get; init; } = new WebApiFeatureServiceImpl();

    public required IDialogFeatureService DialogFeatureService { get; init; }

    public required IPostActionFeatureService PostActionFeatureService { get; set; }

    public IAppLifecycleService AppLifecycleService { get; set; } = new NoOpAppLifecycleService();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await WebApiFeatureService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort disposal.
        }

        try
        {
            if (RemoteControlFeatureService is IAsyncDisposable remoteControlDisposable)
            {
                await remoteControlDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort disposal.
        }

        try
        {
            await Platform.TrayService.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort disposal.
        }

        try
        {
            if (Platform.NotificationService is IAsyncDisposable notificationAsyncDisposable)
            {
                await notificationAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (Platform.NotificationService is IDisposable notificationDisposable)
            {
                notificationDisposable.Dispose();
            }
        }
        catch
        {
            // Best-effort disposal.
        }

        try
        {
            if (Platform.HotkeyService is IDisposable hotkeyDisposable)
            {
                hotkeyDisposable.Dispose();
            }

            if (Platform.OverlayService is IDisposable overlayDisposable)
            {
                overlayDisposable.Dispose();
            }
        }
        catch
        {
            // Best-effort disposal.
        }

        await CoreBridge.DisposeAsync().ConfigureAwait(false);
    }
}

public static class MAAUnifiedRuntimeFactory
{
    public static MAAUnifiedRuntime Create(string baseDirectory)
    {
        var runtimeBaseDirectory = RuntimeLayout.NormalizeDirectory(baseDirectory);
        var logService = new UiLogService();
        var diagnosticsService = new UiDiagnosticsService(runtimeBaseDirectory, logService);
        var store = new AvaloniaJsonConfigStore(runtimeBaseDirectory);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            logService,
            runtimeBaseDirectory);
        var bridge = new MaaCoreBridgeNative();
        var stateMachine = new SessionStateMachine();
        var sessionService = new UnifiedSessionService(bridge, configService, logService, stateMachine);
        var platform = PlatformServicesFactory.CreateDefaults();
        var resourceWorkflowService = new ResourceWorkflowService(
            runtimeBaseDirectory,
            bridge,
            logService,
            platform.GpuCapabilityService);

        var connectFeatureService = new ConnectFeatureService(sessionService, configService);
        var shellFeatureService = new ShellFeatureService(connectFeatureService);
        var taskQueueFeatureService = new TaskQueueFeatureService(sessionService, configService);
        var copilotFeatureService = new CopilotFeatureService();
        var toolboxFeatureService = new ToolboxFeatureService(bridge, connectFeatureService);
        var remoteControlFeatureService = new RemoteControlFeatureServiceImpl(
            configService,
            sessionService,
            connectFeatureService,
            taskQueueFeatureService,
            toolboxFeatureService,
            bridge,
            logService);
        var platformCapabilityService = new PlatformCapabilityFeatureService(platform, diagnosticsService);
        var overlayFeatureService = new OverlayFeatureService(platformCapabilityService);
        var notificationProviderFeatureService = new NotificationProviderFeatureService();
        var settingsFeatureService = new SettingsFeatureService(configService, platformCapabilityService, diagnosticsService);
        var configurationProfileFeatureService = new ConfigurationProfileFeatureService(configService);
        var uiLanguageCoordinator = new UiLanguageCoordinator(configService);
        var appLifecycleService = new ProcessAppLifecycleService();
        var achievementTrackerService = new AchievementTrackerService(configService, runtimeBaseDirectory);
        var versionUpdateFeatureService = new VersionUpdateFeatureService(
            configService,
            diagnosticsService,
            achievementTrackerService,
            logService,
            runtimeBaseDirectory: runtimeBaseDirectory);
        var achievementFeatureService = new AchievementFeatureService(configService);
        var announcementFeatureService = new AnnouncementFeatureService(configService);
        var stageManagerFeatureService = new StageManagerFeatureServiceImpl(configService);
        var webApiFeatureService = new WebApiFeatureServiceImpl(
            configService,
            sessionService,
            connectFeatureService,
            appLifecycleService);
        var dialogFeatureService = new DialogFeatureService(diagnosticsService);
        var postActionFeatureService = new PostActionFeatureService(
            configService,
            diagnosticsService,
            platform.PostActionExecutorService,
            bridge,
            appLifecycleService,
            new NoOpPostActionPromptService());

        _ = remoteControlFeatureService.StartRemotePollingAsync();

        return new MAAUnifiedRuntime {
            CoreBridge = bridge,
            ConfigurationService = configService,
            ResourceWorkflowService = resourceWorkflowService,
            SessionService = sessionService,
            Platform = platform,
            LogService = logService,
            DiagnosticsService = diagnosticsService,
            ConnectFeatureService = connectFeatureService,
            ShellFeatureService = shellFeatureService,
            TaskQueueFeatureService = taskQueueFeatureService,
            CopilotFeatureService = copilotFeatureService,
            ToolboxFeatureService = toolboxFeatureService,
            RemoteControlFeatureService = remoteControlFeatureService,
            PlatformCapabilityService = platformCapabilityService,
            OverlayFeatureService = overlayFeatureService,
            NotificationProviderFeatureService = notificationProviderFeatureService,
            SettingsFeatureService = settingsFeatureService,
            UiLanguageCoordinator = uiLanguageCoordinator,
            ConfigurationProfileFeatureService = configurationProfileFeatureService,
            VersionUpdateFeatureService = versionUpdateFeatureService,
            AchievementFeatureService = achievementFeatureService,
            AchievementTrackerService = achievementTrackerService,
            AnnouncementFeatureService = announcementFeatureService,
            StageManagerFeatureService = stageManagerFeatureService,
            WebApiFeatureService = webApiFeatureService,
            DialogFeatureService = dialogFeatureService,
            PostActionFeatureService = postActionFeatureService,
            AppLifecycleService = appLifecycleService,
        };
    }
}
