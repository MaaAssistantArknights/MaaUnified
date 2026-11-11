using System.Diagnostics;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Application.Services;

public interface IAppLifecycleService
{
    bool SupportsExit => false;

    Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default);

    Task<UiOperationResult> RestartWithoutArgumentsAsync(CancellationToken cancellationToken = default)
        => RestartAsync(cancellationToken);

    Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiOperationResult.Fail(UiErrorCode.AppExitUnsupported, "Exit lifecycle service is unavailable."));
    }
}

public sealed class ProcessAppLifecycleService : IAppLifecycleService
{
    public const string RestartWaitForInstanceArgument = "--wait-for-single-instance";

    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private int _restartInitiated;

    public ProcessAppLifecycleService(Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        _processStarter = processStarter ?? Process.Start;
    }

    public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        => RestartCoreAsync(Environment.GetCommandLineArgs().Skip(1), cancellationToken);

    public Task<UiOperationResult> RestartWithoutArgumentsAsync(CancellationToken cancellationToken = default)
        => RestartCoreAsync([], cancellationToken);

    internal static IReadOnlyList<string> BuildRestartArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments
            .Where(argument => !string.Equals(argument, RestartWaitForInstanceArgument, StringComparison.Ordinal))
            .Append(RestartWaitForInstanceArgument)
            .ToArray();
    }

    private Task<UiOperationResult> RestartCoreAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.AppRestartExecutableMissing, "Current executable path is unavailable."));
        }

        var startInfo = BuildRestartStartInfo(executable);
        foreach (var argument in BuildRestartArguments(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // WPF parity: the frozen WPF bootstrapper starts the successor exactly
        // once, at the end of `OnExit`, so repeat Restart commands while the
        // process is still shutting down cannot spawn a second successor.
        // Without this latch two children would race the single-instance mutex
        // and the loser would exit with a spurious duplicate-instance warning.
        if (Interlocked.Exchange(ref _restartInitiated, 1) != 0)
        {
            return Task.FromResult(UiOperationResult.Ok("Restart already initiated."));
        }

        // WPF parity: the frozen WPF bootstrapper releases the single-instance
        // mutex first (`Bootstrapper.OnExit` -> `Release()`) and only then starts
        // the new process. Spawning while this instance still holds the guard
        // makes the child fail the guard check and exit, so Restart would
        // degrade into "closed but not restarted".
        PackageInstanceGuard.ReleaseProcessWideGuard();
        try
        {
            _ = _processStarter(startInfo);
            return Task.FromResult(UiOperationResult.Ok("Restart process launched."));
        }
        catch (Exception ex)
        {
            _ = PackageInstanceGuard.TryReacquireProcessWideGuard();
            Volatile.Write(ref _restartInitiated, 0);
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.AppRestartFailed, $"Failed to restart process: {ex.Message}"));
        }
    }

    // WPF parity (Bootstrapper.ShutdownAndRestartWithoutArgs): restart launches the
    // current executable without forwarding the original command-line arguments.
    internal static ProcessStartInfo BuildRestartStartInfo(string executablePath)
    {
        return new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
        };
    }
}

public sealed class NoOpAppLifecycleService : IAppLifecycleService
{
    public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiOperationResult.Fail(UiErrorCode.AppRestartUnsupported, "Restart lifecycle service is unavailable."));
    }
}
