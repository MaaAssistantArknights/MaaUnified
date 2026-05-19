using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;

namespace MAAUnified.App.Services;

public sealed class AvaloniaDesktopAppLifecycleService : IAppLifecycleService
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktopLifetime;
    private readonly ProcessAppLifecycleService _restartService = new();

    public AvaloniaDesktopAppLifecycleService(IClassicDesktopStyleApplicationLifetime desktopLifetime)
    {
        _desktopLifetime = desktopLifetime;
    }

    public bool SupportsExit => true;

    public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        => _restartService.RestartAsync(cancellationToken);

    public async Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                RequestShutdown();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(RequestShutdown, DispatcherPriority.Send, cancellationToken);
            }

            return UiOperationResult.Ok("Application shutdown requested.");
        }
        catch (Exception ex)
        {
            return UiOperationResult.Fail(UiErrorCode.AppExitFailed, $"Failed to exit application: {ex.Message}");
        }
    }

    private void RequestShutdown()
    {
        Program.RecordStartupStage("App.Exit.RequestShutdown", "Closing auxiliary windows before Avalonia shutdown.");
        CloseAuxiliaryWindows();
        Program.RecordStartupStage("App.Exit.RequestShutdown", "Invoking Avalonia desktop shutdown.");
        _desktopLifetime.Shutdown();
    }

    private void CloseAuxiliaryWindows()
    {
        var windows = _desktopLifetime.Windows.ToArray();
        foreach (var window in windows)
        {
            if (ReferenceEquals(window, _desktopLifetime.MainWindow))
            {
                continue;
            }

            try
            {
                window.Close();
            }
            catch (ObjectDisposedException)
            {
                // Ignore windows that were already torn down during shutdown.
            }
            catch (InvalidOperationException)
            {
                // Ignore transient close failures and continue shutting down the app.
            }
        }
    }
}
