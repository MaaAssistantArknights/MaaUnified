using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Diagnostics;
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
    private const double UiLagThresholdMs = 120;
    private static bool _globalExceptionHandlersRegistered;
    private static int _shutdownStarted;
    private static AppCrashCaptureService? _crashCaptureService;
    private static DispatcherTimer? _uiLagProbeTimer;
    private static UiFontFamilyResourceUpdater? _uiFontFamilyResourceUpdater;
    private static DateTimeOffset _uiLagProbeExpectedAtUtc;
    private static DateTimeOffset _lastUiLagLoggedAtUtc = DateTimeOffset.MinValue;
    public static MAAUnifiedRuntime Runtime { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

            _ = InitializeShellAsync(vm, mainWindow);
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
            _ = Runtime.DiagnosticsService.RecordNavigationTimingAsync(
                "App.Startup.FirstScreen",
                "FrameworkInit",
                "FirstScreenReady",
                firstScreenStopwatch.Elapsed.TotalMilliseconds);
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
        _ = Runtime.DiagnosticsService.RecordUiLagAsync(
            "App.UiThreadLagProbe",
            lagMs,
            thresholdMs: (int)UiLagThresholdMs,
            probeIntervalMs: (int)UiLagProbeInterval.TotalMilliseconds,
            minInterval: UiLagLogMinInterval);
    }

    private static void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        StopUiLagProbe();
        _uiFontFamilyResourceUpdater?.Dispose();
        _uiFontFamilyResourceUpdater = null;
        if (Current is App app)
        {
            Runtime.UiLanguageCoordinator.LanguageChanged -= app.OnLegacyLocalizationLanguageChanged;
        }

        _ = DisposeRuntimeOnExitAsync();
    }

    private void ConfigureLegacyLocalizationResources()
    {
        ApplyLegacyLocalizationResources(Runtime.UiLanguageCoordinator.CurrentLanguage);
        Runtime.UiLanguageCoordinator.LanguageChanged += OnLegacyLocalizationLanguageChanged;
    }

    private void OnLegacyLocalizationLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyLegacyLocalizationResources(e.CurrentLanguage);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyLegacyLocalizationResources(e.CurrentLanguage));
    }

    private void ApplyLegacyLocalizationResources(string? language)
    {
        foreach (var (key, value) in AchievementTextCatalog.GetAllStrings(language))
        {
            Resources[key] = value;
        }
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
        _ = Runtime.DiagnosticsService.RecordEventAsync("App.UiFontFamily", message);
    }

    private static async Task DisposeRuntimeOnExitAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var disposeTask = Runtime.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(RuntimeDisposeTimeout));
            if (ReferenceEquals(completed, disposeTask))
            {
                await disposeTask;
                return;
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
                });
        }
        catch (Exception ex)
        {
            await SafeRecordExceptionAsync("App.Exit.Dispose", "Failed to dispose runtime during app exit.", ex);
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
        _ = ReportGlobalExceptionAsync("App.DispatcherUnhandledException", e.Exception, handled: true);
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
        _ = ReportGlobalExceptionAsync("App.UnobservedTaskException", exception, handled: true);
    }

    private static void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled exception payload type: {e.ExceptionObject?.GetType().FullName ?? "<null>"}");
        if (ShouldIgnoreUnhandledException(exception))
        {
            return;
        }

        _ = ReportGlobalExceptionAsync("AppDomain.CurrentDomain.UnhandledException", exception, handled: false, isTerminating: e.IsTerminating);
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
        bool isTerminating = false)
    {
        if (!TryMarkGlobalExceptionAsReported(context, exception))
        {
            return;
        }

        var summary = BuildUnhandledExceptionSummary(context, exception, handled, isTerminating);
        var details = BuildUnhandledExceptionDetails(context, exception, handled, isTerminating);

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
            await Runtime.DialogFeatureService.ReportErrorAsync(context, result);
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
}
