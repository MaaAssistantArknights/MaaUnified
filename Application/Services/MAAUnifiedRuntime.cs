using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.ThirdParty;
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

    /// <summary>
    /// Gets or sets the yituliu OpenAPI client used by the operator-box recognition path. Tests
    /// replace it with a fake transport; production uses the injectable HttpClient default.
    /// </summary>
    public IYituliuOpenApiService YituliuOpenApiService { get; set; } = new YituliuOpenApiService();

    public required IDialogFeatureService DialogFeatureService { get; init; }

    public required IPostActionFeatureService PostActionFeatureService { get; set; }

    public IAppLifecycleService AppLifecycleService { get; set; } = new NoOpAppLifecycleService();

    /// <summary>
    /// Gets the unified task start gate shared by every entry point that may dispatch tasks to
    /// Core. WPF parity (<c>Bootstrapper.TryGetTaskBlockReason</c>): resource broken &gt;
    /// restart required &gt; core not initialized.
    /// </summary>
    public TaskStartGate TaskStartGate { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether the loaded Core resource is broken this session
    /// (for example a task chain referenced an undefined task name).
    /// </summary>
    public bool IsResourceBroken => TaskStartGate.IsResourceBroken;

    /// <summary>
    /// Gets the diagnostic reason recorded when <see cref="IsResourceBroken"/> was first set.
    /// </summary>
    public string? ResourceBrokenReason => TaskStartGate.ResourceBrokenReason;

    /// <summary>
    /// 标记 Core 资源破损：错误日志 + 拦截所有新任务启动，直到资源重新加载成功。
    /// </summary>
    /// <param name="reason">诊断原因（触发点）。</param>
    public void MarkResourceBroken(string reason) => TaskStartGate.MarkResourceBroken(reason);

    /// <summary>
    /// 资源修复/重载成功后解除资源破损拦截。
    /// </summary>
    /// <param name="reason">诊断原因（解除来源）。</param>
    /// <returns>本次调用是否真的解除了拦截。</returns>
    public bool ClearResourceBroken(string reason) => TaskStartGate.ClearResourceBroken(reason);

    /// <summary>
    /// 更新 Core 初始化完成状态，供统一启动拦截判定使用。
    /// </summary>
    /// <param name="isReady">Core 是否可用。</param>
    /// <param name="message">Core 未就绪时的用户可见原因。</param>
    public void SetCoreReady(bool isReady, string? message = null) => TaskStartGate.SetCoreReady(isReady, message);

    /// <summary>
    /// 获取当前禁止开始新任务的原因；<see langword="null"/> 表示可启动。
    /// 所有下发 Core 任务的入口统一经此判定。
    /// </summary>
    /// <param name="scope">发起启动的入口标识，用于日志定位。</param>
    /// <param name="coreWarmupAvailable">该入口是否会自行触发 Core 预热。</param>
    /// <returns>拦截结果；可启动时为 <see langword="null"/>。</returns>
    public TaskStartBlock? TryGetTaskStartBlock(string scope, bool coreWarmupAvailable = false)
        => TaskStartGate.TryGetBlock(scope, coreWarmupAvailable);

    /// <summary>
    /// Gets a value indicating whether this session must be restarted before new tasks may run.
    /// WPF parity (<c>Bootstrapper.RequiresRestart</c>): a stop timeout force collect leaves Core
    /// in an untrusted state (it may still be hung and re-emit late callbacks), and a DirectML
    /// device removal makes the GPU inference device unavailable. Both cases block every task
    /// start until the process restarts. Process-scoped, so restarting clears it.
    /// </summary>
    public bool RequiresRestart => TaskStartGate.RequiresRestart;

    /// <summary>
    /// Gets the diagnostic reason recorded when <see cref="RequiresRestart"/> was first set.
    /// </summary>
    public string? RequiresRestartReason => TaskStartGate.RequiresRestartReason;

    /// <summary>
    /// Marks the current session as requiring a restart. Idempotent: the first reason wins.
    /// </summary>
    /// <param name="reason">Diagnostic reason (scope or Core exception kind).</param>
    public void MarkRequiresRestart(string reason) => TaskStartGate.MarkRequiresRestart(reason);

    public async ValueTask DisposeAsync()
    {
        // WPF parity (Bootstrapper.Release -> SettingsViewModel.Sober): restore the sober language
        // when exiting a drunk (Pallas) session, before any other teardown touches the config.
        await RunExitCleanupAsync(
            "App.Exit.Cleanup.SoberLanguage",
            () => UiLanguageCoordinator is UiLanguageCoordinator coordinator
                ? coordinator.SoberOnExitAsync(CancellationToken.None)
                : Task.CompletedTask).ConfigureAwait(false);

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.WebApi",
            () => WebApiFeatureService.StopAsync(CancellationToken.None)).ConfigureAwait(false);

        // Starting disposal synchronously cancels polling before Core teardown. Do not let an
        // in-flight non-cancellable native capture prevent Core from receiving its exit budget.
        var remoteControlCleanupTask = BeginRemoteControlExitCleanup();

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.CoreBridge",
            async () => await CoreBridge.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);

        await remoteControlCleanupTask.ConfigureAwait(false);

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.Tray",
            () => Platform.TrayService.ShutdownAsync(CancellationToken.None)).ConfigureAwait(false);

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.Notification",
            async () =>
            {
                if (Platform.NotificationService is IAsyncDisposable notificationAsyncDisposable)
                {
                    await notificationAsyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (Platform.NotificationService is IDisposable notificationDisposable)
                {
                    notificationDisposable.Dispose();
                }
            }).ConfigureAwait(false);

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.Hotkey",
            async () =>
            {
                if (Platform.HotkeyService is IAsyncDisposable hotkeyAsyncDisposable)
                {
                    await hotkeyAsyncDisposable.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                if (Platform.HotkeyService is IDisposable hotkeyDisposable)
                {
                    hotkeyDisposable.Dispose();
                    return;
                }

                await Platform.HotkeyService.UnregisterAsync("ShowGui", CancellationToken.None).ConfigureAwait(false);
                await Platform.HotkeyService.UnregisterAsync("LinkStart", CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

        await RunExitCleanupAsync(
            "App.Exit.Cleanup.Overlay",
            () =>
            {
                if (Platform.OverlayService is IDisposable overlayDisposable)
                {
                    overlayDisposable.Dispose();
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
    }

    private Task BeginRemoteControlExitCleanup()
    {
        if (RemoteControlFeatureService is not IAsyncDisposable remoteControlDisposable)
        {
            return Task.CompletedTask;
        }

        try
        {
            // IAsyncDisposable implementations begin their shutdown before returning the ValueTask.
            // RemoteControlFeatureService uses that boundary to cancel its polling token.
            var disposeTask = remoteControlDisposable.DisposeAsync().AsTask();
            return RunExitCleanupAsync("App.Exit.Cleanup.RemoteControl", () => disposeTask);
        }
        catch (Exception ex)
        {
            return RunExitCleanupAsync(
                "App.Exit.Cleanup.RemoteControl",
                () => Task.FromException(ex));
        }
    }

    private async Task RunExitCleanupAsync(string scope, Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            await RecordExitCleanupEventAsync(scope, "begin").ConfigureAwait(false);
            await cleanup().ConfigureAwait(false);
            await RecordExitCleanupEventAsync(scope, "end").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Warn($"{scope} failed during app exit: {ex.Message}");
            await DiagnosticsService.RecordErrorAsync(scope, "Exit cleanup failed.", ex, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordExitCleanupEventAsync(string scope, string message)
    {
        try
        {
            await DiagnosticsService.RecordEventAsync(scope, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Warn($"{scope} diagnostics event failed during app exit: {ex.Message}");
        }
    }
}

public static class MAAUnifiedRuntimeFactory
{
    internal static IUnifiedConfigStore CreateConfigStore(string baseDirectory)
        => new AvaloniaJsonConfigStore(RuntimeLayout.NormalizeDirectory(baseDirectory));

    public static MAAUnifiedRuntime Create(
        string baseDirectory,
        SensitiveSettingsService sensitiveSettingsService)
    {
        ArgumentNullException.ThrowIfNull(sensitiveSettingsService);
        var runtimeBaseDirectory = RuntimeLayout.NormalizeDirectory(baseDirectory);
        var logService = new UiLogService([runtimeBaseDirectory]);
        var diagnosticsService = new UiDiagnosticsService(runtimeBaseDirectory, logService);
        var store = CreateConfigStore(runtimeBaseDirectory);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            logService,
            runtimeBaseDirectory,
            sensitiveSettingsService);
        Func<IEnumerable<string>> sensitiveValueProvider = () =>
            SensitiveSettingKeyRegistry.GetPlaintextValues(configService.CurrentConfig);
        logService.SetSensitiveValueProvider(sensitiveValueProvider);
        diagnosticsService.SetSensitiveValueProvider(sensitiveValueProvider);
        var bridge = new MaaCoreBridgeNative();
        var stateMachine = new SessionStateMachine();
        var taskStartGate = new TaskStartGate(logService, () => ResolveTaskStartGateLanguage(configService));
        var sessionService = new UnifiedSessionService(bridge, configService, logService, stateMachine, taskStartGate);
        var platform = PlatformServicesFactory.CreateDefaults();
        var resourceWorkflowService = new ResourceWorkflowService(
            runtimeBaseDirectory,
            bridge,
            logService,
            platform.GpuCapabilityService);

        var connectFeatureService = new ConnectFeatureService(
            sessionService,
            configService,
            logService,
            bridge,
            runtimeBaseDirectory,
            overlayTargetProvider: platform.OverlayService);
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
            logService,
            probeAsync: null,
            sendHttpAsync: null,
            taskStartGate: taskStartGate);
        var platformCapabilityService = new PlatformCapabilityFeatureService(platform, diagnosticsService);
        var overlayFeatureService = new OverlayFeatureService(platformCapabilityService);
        var notificationProviderFeatureService = new NotificationProviderFeatureService(configService);
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
            appLifecycleService,
            bridge);
        var dialogFeatureService = new DialogFeatureService(diagnosticsService);
        var postActionFeatureService = new PostActionFeatureService(
            configService,
            diagnosticsService,
            platform.PostActionExecutorService,
            bridge,
            appLifecycleService,
            new NoOpPostActionPromptService(),
            sessionService);

        _ = remoteControlFeatureService.StartRemotePollingAsync();

        return new MAAUnifiedRuntime {
            CoreBridge = bridge,
            TaskStartGate = taskStartGate,
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

    private static string ResolveTaskStartGateLanguage(UnifiedConfigurationService configService)
    {
        if (configService.CurrentConfig.GlobalValues.TryGetValue("GUI.Localization", out var value)
            && value is System.Text.Json.Nodes.JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }
}
