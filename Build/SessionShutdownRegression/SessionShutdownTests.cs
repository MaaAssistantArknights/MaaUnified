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
        await WithWindowAsync((window, _, _) =>
        {
            var key = "session-shutdown-save-failure";
            var tracker = ConfigurationSaveTracker.Instance;
            tracker.MarkFailed(key, "Failed configuration");
            try
            {
                var args = ClosingArgs(reason, isProgrammatic: false);
                RaiseClosing(window, args);
                Assert.False(args.Cancel);
                Assert.False(window.IsEnabled);
                Assert.Contains("Failed configuration", tracker.FailedDisplayNames);
            }
            finally
            {
                tracker.ClearFailure(key);
            }
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
