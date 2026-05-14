using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;

namespace MAAUnified.App.Services;

public sealed class AvaloniaDesktopAppLifecycleService : IAppLifecycleService
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktopLifetime;
    private readonly Func<CancellationToken, Task>? _prepareShutdownAsync;
    private readonly ProcessAppLifecycleService _restartService = new();

    public AvaloniaDesktopAppLifecycleService(
        IClassicDesktopStyleApplicationLifetime desktopLifetime,
        Func<CancellationToken, Task>? prepareShutdownAsync = null)
    {
        _desktopLifetime = desktopLifetime;
        _prepareShutdownAsync = prepareShutdownAsync;
    }

    public bool SupportsExit => true;

    public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        => _restartService.RestartAsync(cancellationToken);

    public async Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_prepareShutdownAsync is not null)
            {
                await _prepareShutdownAsync(cancellationToken);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                _desktopLifetime.Shutdown();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => _desktopLifetime.Shutdown(), DispatcherPriority.Send, cancellationToken);
            }

            return UiOperationResult.Ok("Application shutdown requested.");
        }
        catch (Exception ex)
        {
            return UiOperationResult.Fail(UiErrorCode.AppExitFailed, $"Failed to exit application: {ex.Message}");
        }
    }
}
