using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using MAAUnified.App.Controls;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.Views;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.App;

public partial class App : Avalonia.Application
{
    private static readonly object GlobalExceptionGate = new();
    private static readonly HashSet<string> ReportedGlobalExceptions = new(StringComparer.Ordinal);
    private static readonly TimeSpan UiLagProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan UiLagLogMinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeDisposeTimeout = TimeSpan.FromSeconds(4);
    private static readonly string[] LegacyLocalizationResourceKeys =
    [
        "AlwaysAutoDetectConnectionTip",
        "BadModules.UseSoftwareRenderingTip",
        "ExternalNotificationBarkSendKey",
        "ExternalNotificationBarkServer",
        "ExternalNotificationCustomWebhook",
        "ExternalNotificationCustomWebhookBody",
        "ExternalNotificationCustomWebhookHeaders",
        "ExternalNotificationCustomWebhookPlaceholders",
        "ExternalNotificationCustomWebhookUrl",
        "ExternalNotificationDingTalkAccessToken",
        "ExternalNotificationDingTalkSecret",
        "ExternalNotificationDiscordBotToken",
        "ExternalNotificationDiscordUserId",
        "ExternalNotificationDiscordWebhookUrl",
        "ExternalNotificationGotifyServer",
        "ExternalNotificationGotifyToken",
        "ExternalNotificationQmsgBot",
        "ExternalNotificationQmsgKey",
        "ExternalNotificationQmsgServer",
        "ExternalNotificationQmsgUser",
        "ExternalNotificationServerChanSendKey",
        "ExternalNotificationSmtpAuth",
        "ExternalNotificationSmtpFrom",
        "ExternalNotificationSmtpPassword",
        "ExternalNotificationSmtpPort",
        "ExternalNotificationSmtpServer",
        "ExternalNotificationSmtpSsl",
        "ExternalNotificationSmtpTo",
        "ExternalNotificationSmtpUser",
        "ExternalNotificationTelegramBotToken",
        "ExternalNotificationTelegramChatId",
        "ExternalNotificationTelegramTopicId",
        "ForceGithubGlobalSourceTip",
        "ForceScheduledStartTip",
        "HotKeyChangingTip",
        "ResourceUpdateTip",
        "SystemNotificationInfo",
        "TimerCustomConfigTip",
        "UpdateAutoCheckTip",
        "UpdateCheckTip",
        "UpdateSourceTip",
        "UseGpuForInferenceTip",
    ];
    private const double UiLagThresholdMs = 120;
    private const uint FatalMessageBoxFlags = 0x00000010 | 0x00002000 | 0x00010000 | 0x00040000;
    private static bool _globalExceptionHandlersRegistered;
    private static int _shutdownStarted;
    private static int _fatalErrorDialogShown;
    private static AppCrashCaptureService? _crashCaptureService;
    private static DispatcherTimer? _uiLagProbeTimer;
    private static UiFontFamilyResourceUpdater? _uiFontFamilyResourceUpdater;
    private static DateTimeOffset _uiLagProbeExpectedAtUtc;
    private static DateTimeOffset _lastUiLagLoggedAtUtc = DateTimeOffset.MinValue;
    public static MAAUnifiedRuntime Runtime { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppTextEditingMenu.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Program.RecordStartupStage(
            "FrameworkInit.Enter",
            $"lifetime={ApplicationLifetime?.GetType().FullName ?? "<null>"}");

        try
        {
            var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
            Program.RecordStartupStage(
                "FrameworkInit.RuntimeCreate.Begin",
                $"executableBaseDir={AppContext.BaseDirectory}; runtimeBaseDir={runtimeBaseDirectory}");
            Runtime = MAAUnifiedRuntimeFactory.Create(runtimeBaseDirectory);
            Program.RecordStartupStage("FrameworkInit.RuntimeCreate.End", "MAAUnified runtime created.");
            ConfigureUiFontFamilyResource();
            ConfigureLegacyLocalizationResources();
        }
        catch (Exception ex)
        {
            Program.RecordStartupStage("FrameworkInit.RuntimeCreate.Fail", "MAAUnified runtime creation failed.", ex);
            throw;
        }

        _crashCaptureService = new AppCrashCaptureService(RuntimeLayout.ResolveRuntimeBaseDirectory());
        Program.RecordStartupStage("FrameworkInit.CrashCapture.Ready", "Crash capture service created.");
        RegisterGlobalExceptionHandlers();
        Program.RecordStartupStage("FrameworkInit.ExceptionHandlers.Ready", "Global exception handlers registered.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.RecordStartupStage("FrameworkInit.DesktopLifetime.Begin", "Configuring classic desktop lifetime.");
            var appLifecycleService = new AvaloniaDesktopAppLifecycleService(desktop);
            Runtime.AppLifecycleService = appLifecycleService;
            Runtime.PostActionFeatureService = new PostActionFeatureService(
                Runtime.ConfigurationService,
                Runtime.DiagnosticsService,
                Runtime.Platform.PostActionExecutorService,
                Runtime.CoreBridge,
                appLifecycleService,
                new AvaloniaPostActionPromptService(desktop));

            MainShellViewModel vm;
            try
            {
                vm = new MainShellViewModel(Runtime);
            }
            catch (Exception ex)
            {
                Program.RecordStartupStage("FrameworkInit.ViewModel.Fail", "MainShellViewModel creation failed.", ex);
                throw;
            }

            Program.RecordStartupStage("FrameworkInit.ViewModel.Created", "MainShellViewModel created.");
            var mainWindow = new MainWindow
            {
                DataContext = vm,
                IsEnabled = false,
            };
            Program.RecordStartupStage("FrameworkInit.MainWindow.Created", "MainWindow created and disabled pending initialization.");
            desktop.MainWindow = mainWindow;
            Program.RecordStartupStage("FrameworkInit.MainWindow.Assigned", "Desktop MainWindow assigned.");
            StartUiLagProbe();
            Program.RecordStartupStage("FrameworkInit.UiLagProbe.Started", "UI thread lag probe started.");
            desktop.Exit += OnDesktopExit;

            ForgetTask(
                InitializeShellAsync(vm, mainWindow),
                "App.InitializeShell.FireAndForget");
            Program.RecordStartupStage("FrameworkInit.InitializeShell.Scheduled", "Shell initialization scheduled.");
        }
        else
        {
            Program.RecordStartupStage(
                "FrameworkInit.NonDesktopLifetime",
                $"Skipping desktop shell setup because lifetime is {ApplicationLifetime?.GetType().FullName ?? "<null>"}.");
        }

        Program.RecordStartupStage("FrameworkInit.Complete", "Framework initialization completed.");

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeShellAsync(MainShellViewModel vm, MainWindow mainWindow)
    {
        Program.RecordStartupStage("InitializeShell.Begin", "Starting shell initialization.");
        try
        {
            var firstScreenStopwatch = Stopwatch.StartNew();
            Program.RecordStartupStage("InitializeShell.PendingCrashProbe.Begin", "Checking for previous crash reports.");
            await ReportPendingNativeCrashAsync();
            Program.RecordStartupStage("InitializeShell.PendingCrashProbe.End", "Previous crash report probe completed.");
            var startupTask = vm.InitializeAsync();
            mainWindow.IsEnabled = true;
            Program.RecordStartupStage(
                "InitializeShell.WindowEnabled",
                "Main window enabled while shell continues background initialization.");
            Program.RecordStartupStage("InitializeShell.FirstScreen.Wait", "Waiting for first screen to become interactive.");
            await vm.WaitForFirstScreenReadyAsync();
            firstScreenStopwatch.Stop();
            Program.RecordStartupStage("InitializeShell.FirstScreen.Ready", "First screen is interactive.");
            ForgetTask(
                Runtime.DiagnosticsService.RecordNavigationTimingAsync(
                    "App.Startup.FirstScreen",
                    "FrameworkInit",
                    "FirstScreenReady",
                    firstScreenStopwatch.Elapsed.TotalMilliseconds),
                "App.Startup.FirstScreen.Timing");
            await startupTask;
            Program.RecordStartupStage(
                "InitializeShell.End",
                $"Shell initialized. sessionState={Runtime.SessionService.CurrentState}; errorLog={Runtime.DiagnosticsService.ErrorLogPath}");
        }
        catch (Exception ex)
        {
            Program.RecordStartupStage("InitializeShell.Fail", "Shell initialization failed.", ex);
            await ReportGlobalExceptionAsync("App.Initialize", ex, handled: true);
        }
        finally
        {
            mainWindow.IsEnabled = true;
            Program.RecordStartupStage("InitializeShell.Finally", "Main window left enabled after shell initialization.");
        }
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        if (_globalExceptionHandlersRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
        Dispatcher.UIThread.UnhandledExceptionFilter += OnDispatcherUnhandledExceptionFilter;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        _globalExceptionHandlersRegistered = true;
    }

    private static void StartUiLagProbe()
    {
        if (_uiLagProbeTimer is not null)
        {
            return;
        }

        _uiLagProbeExpectedAtUtc = DateTimeOffset.UtcNow + UiLagProbeInterval;
        var timer = new DispatcherTimer
        {
            Interval = UiLagProbeInterval,
        };
        timer.Tick += OnUiLagProbeTick;
        timer.Start();
        _uiLagProbeTimer = timer;
    }

    private static void StopUiLagProbe()
    {
        if (_uiLagProbeTimer is null)
        {
            return;
        }

        _uiLagProbeTimer.Stop();
        _uiLagProbeTimer.Tick -= OnUiLagProbeTick;
        _uiLagProbeTimer = null;
    }

    private static void OnUiLagProbeTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var lagMs = (now - _uiLagProbeExpectedAtUtc).TotalMilliseconds;
        _uiLagProbeExpectedAtUtc = now + UiLagProbeInterval;

        if (lagMs < UiLagThresholdMs || now - _lastUiLagLoggedAtUtc < UiLagLogMinInterval)
        {
            return;
        }

        _lastUiLagLoggedAtUtc = now;
        ForgetTask(
            Runtime.DiagnosticsService.RecordUiLagAsync(
                "App.UiThreadLagProbe",
                lagMs,
                thresholdMs: (int)UiLagThresholdMs,
                probeIntervalMs: (int)UiLagProbeInterval.TotalMilliseconds,
                minInterval: UiLagLogMinInterval),
            "App.UiThreadLagProbe.Record");
    }

    private static void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Program.RecordStartupStage("App.Exit.DesktopExit", "Classic desktop lifetime exit event received.");
        StopUiLagProbe();
        _uiFontFamilyResourceUpdater?.Dispose();
        _uiFontFamilyResourceUpdater = null;
        if (Current is App app)
        {
            Runtime.UiLanguageCoordinator.LanguageChanged -= app.OnLegacyLocalizationLanguageChanged;
        }

        try
        {
            Program.RecordStartupStage("App.Exit.Dispose.Begin", "Disposing runtime during desktop exit.");
            var disposed = DisposeRuntimeOnExitAsync(CancellationToken.None).GetAwaiter().GetResult();
            Program.RecordStartupStage(
                "App.Exit.Dispose.End",
                $"Runtime disposal finished during desktop exit. completed={disposed}");
            if (!disposed || OperatingSystem.IsWindows())
            {
                // Native integrations can leave foreground threads alive after Avalonia shutdown or a disposal timeout.
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            ForgetTask(
                SafeRecordExceptionAsync("App.Exit.Dispose", "Failed to synchronously dispose runtime during app exit.", ex),
                "App.Exit.DisposeRuntime.Report");
            if (OperatingSystem.IsWindows())
            {
                Environment.Exit(0);
            }
        }
    }

    internal static void ForgetTask(Task task, string context)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveTaskAsync(task, context);
    }

    internal static async Task RunUiTaskAsync(string context, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
        }
        catch (Exception ex) when (ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await ReportGlobalExceptionAsync(context, ex, handled: true);
        }
    }

    internal static void PostUiCallback(string context, Action action, DispatcherPriority? priority = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex) when (ShouldIgnoreUnhandledException(ex))
                    {
                    }
                    catch (Exception ex)
                    {
                        ForgetTask(
                            ReportGlobalExceptionAsync(context, ex, handled: true),
                            $"{context}.Report");
                    }
                },
                priority ?? DispatcherPriority.Normal);
        }
        catch (Exception ex) when (ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            ForgetTask(
                ReportGlobalExceptionAsync($"{context}.Post", ex, handled: true),
                $"{context}.Post.Report");
        }
    }

    private static async Task ObserveTaskAsync(Task task, string context)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await ReportGlobalExceptionAsync(context, ex, handled: true);
        }
    }

    private void ConfigureLegacyLocalizationResources()
    {
        ApplyLegacyLocalizationResources(Runtime.UiLanguageCoordinator.CurrentLanguage);
        Runtime.UiLanguageCoordinator.LanguageChanged += OnLegacyLocalizationLanguageChanged;
    }

    private void OnLegacyLocalizationLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        var queueDelay = Stopwatch.StartNew();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostUiCallback(
                "App.LegacyLocalization.LanguageChanged",
                () =>
                {
                    ForgetTask(
                        RecordAppTemporaryTimingAsync(
                            "App.LegacyLocalization.QueueDelay",
                            queueDelay.Elapsed.TotalMilliseconds,
                            ("language", e.CurrentLanguage),
                            ("postedFromUiThread", false)),
                        "App.LegacyLocalization.QueueDelay");
                    ApplyLegacyLocalizationResources(e.CurrentLanguage);
                },
                DispatcherPriority.Background);
            return;
        }

        PostUiCallback(
            "App.LegacyLocalization.LanguageChanged",
            () =>
            {
                ForgetTask(
                    RecordAppTemporaryTimingAsync(
                        "App.LegacyLocalization.QueueDelay",
                        queueDelay.Elapsed.TotalMilliseconds,
                        ("language", e.CurrentLanguage),
                        ("postedFromUiThread", true)),
                    "App.LegacyLocalization.QueueDelay");
                ApplyLegacyLocalizationResources(e.CurrentLanguage);
            },
            DispatcherPriority.Background);
    }

    private void ApplyLegacyLocalizationResources(string? language)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var entries = AchievementTextCatalog.GetAllStrings(language);
        ForgetTask(
            RecordAppTemporaryTimingAsync(
                "App.LegacyLocalization.LoadCatalog",
                step.Elapsed.TotalMilliseconds,
                ("language", language),
                ("entryCount", entries.Count)),
            "App.LegacyLocalization.LoadCatalog");

        step.Restart();
        var appliedCount = 0;
        foreach (var key in LegacyLocalizationResourceKeys)
        {
            if (!entries.TryGetValue(key, out var value))
            {
                continue;
            }

            Resources[key] = value;
            appliedCount++;
        }

        ForgetTask(
            RecordAppTemporaryTimingAsync(
                "App.LegacyLocalization.ApplyResources",
                step.Elapsed.TotalMilliseconds,
                ("language", language),
                ("entryCount", entries.Count),
                ("allowlistCount", LegacyLocalizationResourceKeys.Length),
                ("appliedCount", appliedCount)),
            "App.LegacyLocalization.ApplyResources");
        ForgetTask(
            RecordAppTemporaryTimingAsync(
                "App.LegacyLocalization.Total",
                total.Elapsed.TotalMilliseconds,
                ("language", language),
                ("entryCount", entries.Count),
                ("allowlistCount", LegacyLocalizationResourceKeys.Length),
                ("appliedCount", appliedCount)),
            "App.LegacyLocalization.Total");
    }

    private static Task RecordAppTemporaryTimingAsync(
        string scope,
        double elapsedMs,
        params (string Key, object? Value)[] fields)
    {
        if (Runtime is not { } runtime)
        {
            return Task.CompletedTask;
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                payload[key] = value;
            }
        }

        return runtime.DiagnosticsService.RecordTemporaryTimingAsync(scope, elapsedMs, payload);
    }

    private void ConfigureUiFontFamilyResource()
    {
        var startupLanguage = StartupShellSnapshot.FromConfig(Runtime.ConfigurationService.CurrentConfig).Language;
        _uiFontFamilyResourceUpdater?.Dispose();
        _uiFontFamilyResourceUpdater = new UiFontFamilyResourceUpdater(
            Resources,
            Runtime.UiLanguageCoordinator,
            new UiFontFamilyResolver(),
            RecordUiFontFamilyFallback);

        var resolution = _uiFontFamilyResourceUpdater.ApplyLanguage(startupLanguage);
        Program.RecordStartupStage(
            "FrameworkInit.UiFontFamily.Ready",
            $"language={resolution.Language}; actual={resolution.Actual}");
    }

    private static void RecordUiFontFamilyFallback(UiFontFamilyResolution resolution)
    {
        var message =
            $"UI font fallback: language={resolution.Language}; expected={resolution.Expected}; actual={resolution.Actual}; reason={resolution.Reason}";
        Runtime.LogService.Warn(message);
        ForgetTask(
            Runtime.DiagnosticsService.RecordEventAsync("App.UiFontFamily", message),
            "App.UiFontFamily.RecordFallback");
    }

    private static async Task<bool> DisposeRuntimeOnExitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return true;
        }

        try
        {
            var disposeTask = Task.Run(async () => await Runtime.DisposeAsync().ConfigureAwait(false));
            var completed = await Task.WhenAny(disposeTask, Task.Delay(RuntimeDisposeTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, disposeTask))
            {
                await disposeTask.ConfigureAwait(false);
                return true;
            }

            _ = disposeTask.ContinueWith(
                static task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            Runtime.LogService.Warn(
                $"Runtime dispose timed out during app exit: timeoutMs={RuntimeDisposeTimeout.TotalMilliseconds:0}");
            await Runtime.DiagnosticsService.RecordPerformanceEventAsync(
                "app_exit_dispose_timeout",
                "App.Exit",
                RuntimeDisposeTimeout.TotalMilliseconds,
                new Dictionary<string, object?>
                {
                    ["timeoutMs"] = (int)RuntimeDisposeTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await SafeRecordExceptionAsync("App.Exit.Dispose", "Failed to dispose runtime during app exit.", ex)
                .ConfigureAwait(false);
            return false;
        }
    }

    private static void OnDispatcherUnhandledExceptionFilter(object? sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        if (ShouldIgnoreUnhandledException(e.Exception))
        {
            return;
        }

        e.RequestCatch = true;
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (ShouldIgnoreUnhandledException(e.Exception))
        {
            return;
        }

        e.Handled = true;
        ForgetTask(
            ReportGlobalExceptionAsync("App.DispatcherUnhandledException", e.Exception, handled: true),
            "App.DispatcherUnhandledException.Report");
    }

    private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var exception = e.Exception.Flatten();
        if (ShouldIgnoreUnhandledException(exception))
        {
            e.SetObserved();
            return;
        }

        e.SetObserved();
        ForgetTask(
            ReportGlobalExceptionAsync("App.UnobservedTaskException", exception, handled: true),
            "App.UnobservedTaskException.Report");
    }

    private static void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled exception payload type: {e.ExceptionObject?.GetType().FullName ?? "<null>"}");
        if (ShouldIgnoreUnhandledException(exception))
        {
            return;
        }

        var summary = BuildUnhandledExceptionSummary(
            "AppDomain.CurrentDomain.UnhandledException",
            exception,
            handled: false,
            isTerminating: e.IsTerminating);
        var details = BuildUnhandledExceptionDetails(
            "AppDomain.CurrentDomain.UnhandledException",
            exception,
            handled: false,
            isTerminating: e.IsTerminating);
        if (e.IsTerminating)
        {
            TryShowFatalErrorDialog(summary);
        }

        ForgetTask(
            ReportGlobalExceptionAsync(
                "AppDomain.CurrentDomain.UnhandledException",
                exception,
                handled: false,
                isTerminating: e.IsTerminating,
                precomputedSummary: summary,
                precomputedDetails: details,
                reportToDialogFeature: !e.IsTerminating),
            "AppDomain.CurrentDomain.UnhandledException.Report");
    }

    internal static bool ShouldIgnoreUnhandledException(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            var inners = aggregate.Flatten().InnerExceptions;
            return inners.Count > 0 && inners.All(ShouldIgnoreUnhandledException);
        }

        return IsBenignLinuxAppMenuRegistrarFailure(exception);
    }

    private static bool IsBenignLinuxAppMenuRegistrarFailure(Exception exception)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (HasBenignLinuxAppMenuRegistrarMessage(exception.Message))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsBenignLinuxAppMenuRegistrarFailure);
        }

        return exception.InnerException is not null
            && IsBenignLinuxAppMenuRegistrarFailure(exception.InnerException);
    }

    private static bool HasBenignLinuxAppMenuRegistrarMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("org.freedesktop.DBus.Error.ServiceUnknown", StringComparison.OrdinalIgnoreCase)
            && message.Contains("com.canonical.AppMenu.Registrar", StringComparison.Ordinal);
    }

    private static async Task ReportPendingNativeCrashAsync()
    {
        if (_crashCaptureService is null)
        {
            return;
        }

        try
        {
            var crashReport = await _crashCaptureService.TryGetPendingCrashReportAsync();
            if (crashReport is null)
            {
                return;
            }

            await _crashCaptureService.MarkCrashReportAsSeenAsync(crashReport);

            var timestamp = crashReport.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            var message = $"Detected native crash log from a previous launch at {timestamp}.";
            var details =
                $"Crash log: {crashReport.CrashLogPath}{Environment.NewLine}" +
                $"Timestamp: {crashReport.LastWriteTimeUtc:O}{Environment.NewLine}{Environment.NewLine}" +
                crashReport.Detail;
            var result = UiOperationResult.Fail(UiErrorCode.CoreUnknown, message, details);

            Runtime.LogService.Error(message);
            await Runtime.DialogFeatureService.ReportErrorAsync("App.PreviousCrash", result);
        }
        catch (Exception ex)
        {
            await SafeRecordExceptionAsync("App.PreviousCrashProbe", "Failed to inspect previous crash log.", ex);
        }
    }

    private static async Task ReportGlobalExceptionAsync(
        string context,
        Exception exception,
        bool handled,
        bool isTerminating = false,
        string? precomputedSummary = null,
        string? precomputedDetails = null,
        bool reportToDialogFeature = true)
    {
        if (!TryMarkGlobalExceptionAsReported(context, exception))
        {
            return;
        }

        var summary = precomputedSummary ?? BuildUnhandledExceptionSummary(context, exception, handled, isTerminating);
        var details = precomputedDetails ?? BuildUnhandledExceptionDetails(context, exception, handled, isTerminating);

        try
        {
            Console.Error.WriteLine(summary);
            Console.Error.WriteLine(details);
        }
        catch
        {
            // Ignore stderr failures during crash reporting.
        }

        try
        {
            Runtime.LogService.Error(summary);
            var result = UiOperationResult.Fail(UiErrorCode.UiError, summary, details);
            await Runtime.DiagnosticsService.RecordErrorAsync(context, summary, exception);
            if (reportToDialogFeature)
            {
                await Runtime.DialogFeatureService.ReportErrorAsync(context, result);
            }
        }
        catch
        {
            // Avoid rethrowing from global exception handlers.
        }
    }

    private static bool TryMarkGlobalExceptionAsReported(string context, Exception exception)
    {
        var key = $"{context}|{exception.GetType().FullName}|{exception.Message}";
        lock (GlobalExceptionGate)
        {
            return ReportedGlobalExceptions.Add(key);
        }
    }

    private static string BuildUnhandledExceptionSummary(
        string context,
        Exception exception,
        bool handled,
        bool isTerminating)
    {
        var state = handled
            ? "handled"
            : isTerminating
                ? "terminating"
                : "unhandled";
        return $"Global exception ({state}) in {context}: {exception.GetType().Name}: {exception.Message}";
    }

    private static string BuildUnhandledExceptionDetails(
        string context,
        Exception exception,
        bool handled,
        bool isTerminating)
    {
        return
            $"Timestamp: {DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"Context: {context}{Environment.NewLine}" +
            $"Handled: {handled}{Environment.NewLine}" +
            $"IsTerminating: {isTerminating}{Environment.NewLine}" +
            $"ExecutableBaseDirectory: {AppContext.BaseDirectory}{Environment.NewLine}" +
            $"RuntimeBaseDirectory: {RuntimeLayout.ResolveRuntimeBaseDirectory()}{Environment.NewLine}" +
            $"ErrorLog: {Runtime.DiagnosticsService.ErrorLogPath}{Environment.NewLine}" +
            $"EventLog: {Runtime.DiagnosticsService.EventLogPath}{Environment.NewLine}" +
            $"PlatformLog: {Runtime.DiagnosticsService.PlatformEventLogPath}{Environment.NewLine}{Environment.NewLine}" +
            exception;
    }

    private static async Task SafeRecordExceptionAsync(string scope, string message, Exception exception)
    {
        try
        {
            Runtime.LogService.Error($"{message} {exception.GetType().Name}: {exception.Message}");
            await Runtime.DiagnosticsService.RecordErrorAsync(scope, message, exception);
        }
        catch
        {
            // Ignore logging failures during crash reporting.
        }
    }

    private static void TryShowFatalErrorDialog(string summary)
    {
        if (!OperatingSystem.IsWindows()
            || Interlocked.Exchange(ref _fatalErrorDialogShown, 1) != 0)
        {
            return;
        }

        try
        {
            _ = MessageBox(
                nint.Zero,
                BuildFatalErrorDialogMessage(summary),
                "MAAUnified Fatal Error",
                FatalMessageBoxFlags);
        }
        catch
        {
            // Best effort only. The process is already terminating.
        }
    }

    private static string BuildFatalErrorDialogMessage(string summary)
    {
        var builder = new StringBuilder()
            .AppendLine("MAAUnified encountered a fatal error and must close.")
            .AppendLine()
            .AppendLine(summary);

        var diagnostics = Runtime?.DiagnosticsService;
        if (!string.IsNullOrWhiteSpace(diagnostics?.ErrorLogPath))
        {
            builder
                .AppendLine()
                .AppendLine($"Error log: {diagnostics.ErrorLogPath}");

            if (!string.IsNullOrWhiteSpace(diagnostics.EventLogPath))
            {
                builder.AppendLine($"Event log: {diagnostics.EventLogPath}");
            }

            if (!string.IsNullOrWhiteSpace(diagnostics.PlatformEventLogPath))
            {
                builder.AppendLine($"Platform log: {diagnostics.PlatformEventLogPath}");
            }
        }

        builder
            .AppendLine()
            .AppendLine("Please reopen the app after this dialog closes.");
        return builder.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
