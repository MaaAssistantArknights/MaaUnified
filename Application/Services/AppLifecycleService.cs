using System.Diagnostics;
using MAAUnified.Application.Models;

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

    private static Task<UiOperationResult> RestartCoreAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
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

            _ = Process.Start(startInfo);
            return Task.FromResult(UiOperationResult.Ok("Restart process launched."));
        }
        catch (Exception ex)
        {
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
