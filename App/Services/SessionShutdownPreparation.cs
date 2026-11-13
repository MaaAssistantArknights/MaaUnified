using Avalonia.Threading;

namespace MAAUnified.App.Services;

internal static class SessionShutdownPreparation
{
    // XSMP expects a synchronous answer. Pump the UI dispatcher so existing async
    // saves can finish without canceling logout or deadlocking their continuations.
    internal static bool Run(Func<CancellationToken, Task<bool>> prepare, TimeSpan timeout)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var timeoutCts = new CancellationTokenSource(timeout);
        var frame = new DispatcherFrame();
        using var timeoutRegistration = timeoutCts.Token.Register(() => frame.Continue = false);
        var preparation = prepare(timeoutCts.Token);
        if (!preparation.IsCompleted)
        {
            _ = preparation.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }

        if (!preparation.IsCompleted)
        {
            timeoutCts.Cancel();
            _ = preparation.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException("Configuration saving exceeded the session shutdown time limit.");
        }

        return preparation.GetAwaiter().GetResult();
    }
}
