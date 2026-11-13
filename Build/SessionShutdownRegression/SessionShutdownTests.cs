using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels;
using MAAUnified.App.Views;
using MAAUnified.Application.Services;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MAAUnified.SessionShutdown.Tests.TestAppBuilder))]

namespace MAAUnified.SessionShutdown.Tests;

// A separate test process owns the dispatcher and static App.Runtime, without
// starting a desktop session, MaaCore, or the user's installed application.
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    private sealed class TestApplication : App.App
    {
        public override void OnFrameworkInitializationCompleted() { }
    }
}

public sealed class SessionShutdownTests
{
    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public async Task SystemShutdown_DoesNotCancelAndPersistsConfiguration(WindowCloseReason reason)
    {
        await WithWindowAsync((window, runtime, root) =>
        {
            runtime.ConfigurationService.CurrentConfig.GlobalValues["SessionShutdownRegression"] = reason.ToString();
            SetField(window, "_closeRequestPending", true);
            var args = ClosingArgs(reason, isProgrammatic: false);

            RaiseClosing(window, args);

            Assert.False(args.Cancel);
            Assert.False(window.IsEnabled);
            Assert.Contains(Directory.GetFiles(Path.Combine(root, "config"), "*.json"),
                path => File.ReadAllText(path).Contains(reason.ToString(), StringComparison.Ordinal));
        });
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public async Task SystemShutdown_DoesNotVetoLogoutWhenConfigurationSaveFails(WindowCloseReason reason)
    {
        await WithWindowAsync((window, runtime, _) =>
        {
            var key = $"session-shutdown-save-failure-{Guid.NewGuid():N}";
            var tracker = ConfigurationSaveTracker.Instance;
            var attempted = false;
            var failureReached = false;
            tracker.MarkPending(key, "Failed configuration", key, runtime.DiagnosticsService, async _ =>
            {
                attempted = true;
                await Task.Yield();
                Dispatcher.UIThread.VerifyAccess();
                failureReached = true;
                throw new IOException("Injected close-time save failure.");
            });
            try
            {
                var args = ClosingArgs(reason, isProgrammatic: false);
                RaiseClosing(window, args);
                Assert.True(attempted);
                Assert.True(failureReached);
                Assert.False(args.Cancel);
                Assert.False(window.IsEnabled);
                Assert.Contains("Failed configuration", tracker.FailedDisplayNames);
                Assert.Contains("Injected close-time save failure.",
                    File.ReadAllText(runtime.DiagnosticsService.ErrorLogPath));
            }
            finally
            {
                tracker.ClearPending(key);
                tracker.ClearFailure(key);
            }
        });
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public async Task SystemShutdown_ClosesWindowBeforeReturningToPlatform(WindowCloseReason reason)
    {
        await WithWindowAsync((window, _, _) =>
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;

            // Use Avalonia's real close path, not just the cancellable event.
            // X11's desktop lifetime closes the window before replying to XSMP.
            typeof(Window).GetMethod("CloseCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { reason, false, false });

            Assert.True(closed);
        });
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.WindowClosing, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true)]
    public async Task OrdinaryCloseAndUnconfirmedProgrammaticShutdown_KeepInteractiveHandling(
        WindowCloseReason reason, bool isProgrammatic)
    {
        await WithWindowAsync((window, _, _) =>
        {
            // An outstanding confirmation must not accidentally allow a second
            // ordinary close or turn TryShutdown into a system shutdown.
            SetField(window, "_closeRequestPending", true);
            var args = ClosingArgs(reason, isProgrammatic);
            RaiseClosing(window, args);
            Assert.True(args.Cancel);
        });
    }

    [AvaloniaFact]
    public async Task ConfirmedExit_RemainsAllowed()
    {
        await WithWindowAsync((window, _, _) =>
        {
            SetField(window, "_allowLifecycleClose", true);
            var args = ClosingArgs(WindowCloseReason.ApplicationShutdown, isProgrammatic: true);
            RaiseClosing(window, args);
            Assert.False(args.Cancel);
        });
    }

    [AvaloniaFact]
    public void Preparation_PumpsUiContinuationsBeforeReturning()
    {
        var resumed = false;
        Assert.True(SessionShutdownPreparation.Run(async _ =>
        {
            await Task.Yield();
            Dispatcher.UIThread.VerifyAccess();
            resumed = true;
            return true;
        }, TimeSpan.FromSeconds(3)));
        Assert.True(resumed);
    }

    [AvaloniaFact]
    public void Preparation_ReportsSaveFailures()
    {
        Assert.False(SessionShutdownPreparation.Run(_ => Task.FromResult(false), TimeSpan.FromSeconds(3)));
        Assert.Throws<IOException>(() => SessionShutdownPreparation.Run(async _ =>
        {
            await Task.Yield();
            throw new IOException("save failed");
        }, TimeSpan.FromSeconds(3)));
    }

    [AvaloniaFact]
    public void Preparation_HasBoundedWaitEvenWhenSaveIgnoresCancellation()
    {
        var pending = new TaskCompletionSource<bool>();
        CancellationToken token = default;
        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => SessionShutdownPreparation.Run(cancellationToken =>
        {
            token = cancellationToken;
            return pending.Task;
        }, TimeSpan.FromMilliseconds(80)));
        Assert.True(token.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        pending.SetException(new IOException("late save error"));
    }

    [AvaloniaFact]
    public async Task Preparation_DoesNotStartMoreSavesAfterTimedOutSaveCompletes()
    {
        await WithWindowAsync((_, runtime, _) =>
        {
            var tracker = ConfigurationSaveTracker.Instance;
            var prefix = $"session-shutdown-late-{Guid.NewGuid():N}";
            var pending = new TaskCompletionSource<bool>();
            var firstStarted = false;
            var nextStarted = false;
            Task<bool>? preparation = null;
            tracker.MarkPending(prefix + ".first", "First save", prefix, runtime.DiagnosticsService, _ =>
            {
                firstStarted = true;
                return pending.Task; // Deliberately ignore cancellation until released.
            });
            tracker.MarkPending(prefix + ".next", "Next save", prefix, runtime.DiagnosticsService, _ =>
            {
                nextStarted = true;
                return Task.FromResult(true);
            });
            try
            {
                Assert.Throws<TimeoutException>(() => SessionShutdownPreparation.Run(token =>
                {
                    preparation = RetryAsync(token);
                    return preparation;
                }, TimeSpan.FromMilliseconds(80)));
                Assert.True(firstStarted);
                Assert.False(nextStarted);

                pending.SetResult(true);
                var lateError = Record.Exception(() =>
                    SessionShutdownPreparation.Run(_ => preparation!, TimeSpan.FromSeconds(3)));
                Assert.False(nextStarted, "A second save started after the session shutdown timeout.");
                Assert.IsAssignableFrom<OperationCanceledException>(lateError);
            }
            finally
            {
                pending.TrySetResult(true);
                foreach (var key in new[] { prefix + ".first", prefix + ".next" })
                {
                    tracker.ClearPending(key);
                    tracker.ClearFailure(key);
                }
            }

            async Task<bool> RetryAsync(CancellationToken token)
            {
                await tracker.RetryPendingOrFailedAsync(
                    key => key.StartsWith(prefix, StringComparison.Ordinal), token, runtime.DiagnosticsService);
                return true;
            }
        });
    }

    [AvaloniaFact]
    public async Task Preparation_DoesNotStartRetryWithAlreadyCanceledToken()
    {
        await WithWindowAsync((_, runtime, _) =>
        {
            var tracker = ConfigurationSaveTracker.Instance;
            var key = $"session-shutdown-canceled-{Guid.NewGuid():N}";
            var started = false;
            tracker.MarkPending(key, "Canceled save", key, runtime.DiagnosticsService, _ =>
            {
                started = true;
                return Task.FromResult(true);
            });
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                Assert.ThrowsAny<OperationCanceledException>(() => SessionShutdownPreparation.Run(async _ =>
                {
                    await tracker.RetryPendingOrFailedAsync(candidate => candidate == key, cts.Token,
                        runtime.DiagnosticsService);
                    return true;
                }, TimeSpan.FromSeconds(3)));
                Assert.False(started);
            }
            finally
            {
                tracker.ClearPending(key);
                tracker.ClearFailure(key);
            }
        });
    }

    private static async Task WithWindowAsync(Action<MainWindow, MAAUnifiedRuntime, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-session-shutdown-tests", Guid.NewGuid().ToString("N"));
        var runtime = MAAUnifiedRuntimeFactory.Create(root);
        typeof(App.App).GetProperty("Runtime")!.SetValue(null, runtime);
        MainShellViewModel? shell = null;
        try
        {
            await runtime.ConfigurationService.LoadOrBootstrapAsync();
            shell = new MainShellViewModel(runtime);
            var window = new MainWindow { DataContext = shell };
            test(window, runtime, root);
        }
        finally
        {
            if (shell is not null)
            {
                (typeof(MainShellViewModel).GetField("_timerScheduleTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(shell) as DispatcherTimer)?.Stop();
            }
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static WindowClosingEventArgs ClosingArgs(WindowCloseReason reason, bool isProgrammatic) =>
        (WindowClosingEventArgs)Activator.CreateInstance(typeof(WindowClosingEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { reason, isProgrammatic }, null)!;

    private static void RaiseClosing(MainWindow window, WindowClosingEventArgs args) =>
        typeof(Window).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { args });

    private static void SetField(MainWindow window, string name, bool value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
}
