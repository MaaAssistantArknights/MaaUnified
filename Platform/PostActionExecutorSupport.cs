using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MAAUnified.Platform;

internal static class PostActionExecutorSupport
{
    internal static readonly TimeSpan EmulatorConsoleExitTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex UserScriptControlChars = new(@"\p{C}", RegexOptions.Compiled);

    public static PostActionCapabilityMatrix BuildCapabilityMatrix(PostActionExecutorRequest? request)
    {
        var exitArknights = BuildCoreManagedCapability("Exit Arknights", "maa-core");
        var backToHome = BuildCoreManagedCapability("Back to Android home", "maa-core");
        var exitSelf = BuildCoreManagedCapability("Exit MAA", "app-lifecycle");
        var exitEmulator = BuildExitEmulatorCapability(request);

        return new PostActionCapabilityMatrix(
            ExitArknights: exitArknights,
            BackToAndroidHome: backToHome,
            ExitEmulator: exitEmulator,
            ExitSelf: exitSelf,
            Hibernate: BuildPowerCapability(PostActionType.Hibernate),
            Shutdown: BuildPowerCapability(PostActionType.Shutdown),
            Sleep: BuildPowerCapability(PostActionType.Sleep),
            LockScreen: LockScreenHelper.GetCapability(),
            // PC client close-down reuses MaaCore CloseDown, same as ExitArknights; the PC
            // connection mode itself is filtered in the UI layer (WPF 3b4f06dd86).
            ExitPCClient: BuildCoreManagedCapability("Exit PC client", "maa-core"));
    }

    public static async Task<PlatformOperationResult> ExecuteLockScreenAsync(CancellationToken cancellationToken)
    {
        return await LockScreenHelper.ExecuteAsync(cancellationToken);
    }

    public static async Task<PlatformOperationResult> ExecuteExitEmulatorAsync(
        PostActionExecutorRequest? request,
        CancellationToken cancellationToken)
    {
        return await ExecuteNativeExitEmulatorAsync(request, cancellationToken);
    }

    public static async Task<PlatformOperationResult> ExecutePowerActionAsync(
        PostActionType action,
        CancellationToken cancellationToken)
    {
        var command = TryResolvePowerCommand(action);
        if (command is null)
        {
            return PlatformOperation.Failed(
                "system",
                $"Power action {action} is unsupported on current platform.",
                PlatformErrorCodes.PostActionUnsupported,
                $"post-action.{action}");
        }

        return await ExecuteDirectCommandAsync(
            "system",
            $"post-action.{action}",
            $"{action} command accepted.",
            command.Value.fileName,
            command.Value.arguments,
            cancellationToken);
    }

    internal static PlatformCapabilityStatus BuildCoreManagedCapability(string actionName, string provider)
    {
        return new PlatformCapabilityStatus(
            false,
            $"{actionName} requires native provider `{provider}`.",
            Provider: provider);
    }

    internal static PlatformCapabilityStatus BuildExitEmulatorCapability(PostActionExecutorRequest? request)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsEmulatorExitHelper.GetCapability(request);
        }

        return new PlatformCapabilityStatus(
            false,
            "Exit emulator is unsupported on current platform.",
            Provider: "post-action");
    }

    internal static PlatformCapabilityStatus BuildPowerCapability(PostActionType action)
    {
        var command = TryResolvePowerCommand(action);
        if (command is null)
        {
            return new PlatformCapabilityStatus(
                false,
                $"Power action {action} is unsupported on current platform.",
                Provider: "system");
        }

        return new PlatformCapabilityStatus(
            true,
            $"Power action {action} is available via native system command.",
            Provider: "system");
    }

    // Mirrors WPF GameSettingsUserControlModel.ExecuteScript: quoted path + arguments,
    // optional -noWindow / -minimized flags, wait for exit.
    public static async Task<PlatformOperationResult> ExecuteUserScriptAsync(
        string scriptPath,
        CancellationToken cancellationToken)
    {
        const string provider = "user-script";
        const string operationId = "post-action.UserScript";
        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseUserScript(scriptPath, out var fileName, out var arguments, out var createNoWindow, out var minimized))
            {
                return PlatformOperation.Failed(
                    provider,
                    "User script path is empty or invalid.",
                    PlatformErrorCodes.PostActionExecutionFailed,
                    operationId);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
                CreateNoWindow = createNoWindow,
                UseShellExecute = !createNoWindow,
            };

            process = Process.Start(startInfo);
            if (process is null)
            {
                return PlatformOperation.Failed(
                    provider,
                    $"User script `{fileName}` did not start.",
                    PlatformErrorCodes.PostActionExecutionFailed,
                    operationId);
            }

            await process.WaitForExitAsync(cancellationToken);
            return PlatformOperation.NativeSuccess(
                provider,
                $"User script `{fileName}` executed.",
                operationId);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                provider,
                $"User script failed: {ex.Message}",
                PlatformErrorCodes.PostActionExecutionFailed,
                operationId);
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static bool TryParseUserScript(
        string? rawScriptPath,
        out string fileName,
        out string arguments,
        out bool createNoWindow,
        out bool minimized)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        createNoWindow = false;
        minimized = false;

        var sanitized = UserScriptControlChars.Replace((rawScriptPath ?? string.Empty).Trim(), string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return false;
        }

        if (sanitized.StartsWith('\"'))
        {
            var parts = sanitized.Split('\"', 3);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                return false;
            }

            fileName = parts[1];
            arguments = parts.Length > 2 ? parts[2] : string.Empty;
        }
        else
        {
            fileName = sanitized;
        }

        if (arguments.Contains("-noWindow", StringComparison.Ordinal))
        {
            createNoWindow = true;
            arguments = arguments.Replace("-noWindow", string.Empty, StringComparison.Ordinal).Trim();
        }

        if (arguments.Contains("-minimized", StringComparison.Ordinal))
        {
            minimized = true;
            arguments = arguments.Replace("-minimized", string.Empty, StringComparison.Ordinal).Trim();
        }

        return true;
    }

    internal static async Task<PlatformOperationResult> ExecuteDirectCommandAsync(
        string provider,
        string operationId,
        string successMessage,
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = new Process { StartInfo = startInfo };
            process.Start();
            using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
            using var waitCts = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(waitCts.Token);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0)
            {
                var message = string.IsNullOrWhiteSpace(stdout)
                    ? successMessage
                    : $"{successMessage} {stdout}";
                return PlatformOperation.NativeSuccess(provider, message.Trim(), operationId);
            }

            var failedMessage = string.IsNullOrWhiteSpace(stderr)
                ? $"{successMessage} Exit code {process.ExitCode}."
                : $"{successMessage} Exit code {process.ExitCode}: {stderr}";
            return PlatformOperation.Failed(
                provider,
                failedMessage,
                PlatformErrorCodes.PostActionExecutionFailed,
                operationId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.HasValue)
        {
            TryTerminateProcess(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            return PlatformOperation.Failed(
                provider,
                $"{successMessage} Timed out after {timeout.Value.TotalSeconds:0} seconds.",
                PlatformErrorCodes.PostActionExecutionFailed,
                operationId);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                provider,
                $"{successMessage} {ex.Message}",
                PlatformErrorCodes.PostActionExecutionFailed,
                operationId);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryTerminateProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort after command cancellation or timeout.
        }
    }

    private static async Task DrainOutputAsync(Task<string>? stdoutTask, Task<string>? stderrTask)
    {
        if (stdoutTask is null || stderrTask is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The process is already being torn down; avoid retaining output tasks.
        }
    }

    private static async Task<PlatformOperationResult> ExecuteNativeExitEmulatorAsync(
        PostActionExecutorRequest? request,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await WindowsEmulatorExitHelper.ExecuteAsync(request, cancellationToken);
        }

        return PlatformOperation.Failed(
            "post-action",
            "Exit emulator is unsupported on current platform.",
            PlatformErrorCodes.PostActionUnsupported,
            "post-action.ExitEmulator");
    }

    private static (string fileName, string arguments)? TryResolvePowerCommand(PostActionType action)
    {
        var command = GetPowerCommand(action);
        if (command is null)
        {
            return null;
        }

        return IsCommandAvailable(command.Value.fileName) ? command : null;
    }

    private static (string fileName, string arguments)? GetPowerCommand(PostActionType action)
    {
        if (OperatingSystem.IsWindows())
        {
            return action switch
            {
                PostActionType.Hibernate => ("shutdown", "/h"),
                PostActionType.Shutdown => ("shutdown", "/s /t 0"),
                PostActionType.Sleep => ("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
                _ => null,
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return action switch
            {
                PostActionType.Hibernate => ("systemctl", "hibernate"),
                PostActionType.Shutdown => ("systemctl", "poweroff"),
                PostActionType.Sleep => ("systemctl", "suspend"),
                _ => null,
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return action switch
            {
                PostActionType.Shutdown => ("shutdown", "-h now"),
                PostActionType.Sleep => ("pmset", "sleepnow"),
                _ => null,
            };
        }

        return null;
    }

    internal static bool IsCommandAvailable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (Path.IsPathRooted(fileName)
            || fileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || fileName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(fileName);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var searchDirectories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (OperatingSystem.IsWindows())
        {
            var hasExtension = Path.GetExtension(fileName).Length > 0;
            var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in searchDirectories)
            {
                if (hasExtension && File.Exists(Path.Combine(directory, fileName)))
                {
                    return true;
                }

                foreach (var extension in pathext)
                {
                    if (File.Exists(Path.Combine(directory, fileName + extension)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (var directory in searchDirectories)
        {
            if (File.Exists(Path.Combine(directory, fileName)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Lock-screen capability and execution.
/// WPF parity: <c>Utilities/PowerManagement.cs</c> <c>LockScreen()</c> (d58cb0825b) calls
/// <c>LockWorkStation</c>, and the post-action flow cancels the countdown before locking.
/// MAAUnified implements the Windows API (the only API WPF uses) plus a best-effort Linux
/// session lock; macOS is an explicit placeholder because macOS exposes no supported API for
/// locking the console session without user interaction.
/// </summary>
internal static class LockScreenHelper
{
    private const string Provider = "post-action";

    private static readonly (string FileName, string Arguments, string Provider)[] LinuxLockCommands =
    [
        ("loginctl", "lock-session", "loginctl"),
        ("xdg-screensaver", "lock", "xdg-screensaver"),
    ];

    public static PlatformCapabilityStatus GetCapability()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformCapabilityStatus(
                true,
                "Lock screen is available via the Windows LockWorkStation API.",
                Provider: "windows-lock-workstation");
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var command in LinuxLockCommands)
            {
                if (PostActionExecutorSupport.IsCommandAvailable(command.FileName))
                {
                    return new PlatformCapabilityStatus(
                        true,
                        $"Lock screen is available via `{command.FileName}`.",
                        Provider: command.Provider);
                }
            }

            return new PlatformCapabilityStatus(
                false,
                "Lock screen requires one of `loginctl` or `xdg-screensaver` on Linux and neither is available.",
                Provider: Provider);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformCapabilityStatus(
                false,
                "Lock screen is not implemented on macOS (placeholder): there is no supported non-interactive lock API.",
                Provider: "macos-placeholder",
                HasFallback: true,
                FallbackMode: "log-only");
        }

        return new PlatformCapabilityStatus(
            false,
            "Lock screen is unsupported on current platform.",
            Provider: Provider);
    }

    public static async Task<PlatformOperationResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            return LockWindowsWorkstation();
        }

        if (OperatingSystem.IsLinux())
        {
            var command = LinuxLockCommands.FirstOrDefault(candidate => PostActionExecutorSupport.IsCommandAvailable(candidate.FileName));
            if (command.FileName is null)
            {
                return PlatformOperation.Failed(
                    Provider,
                    "Lock screen requires one of `loginctl` or `xdg-screensaver` on Linux and neither is available.",
                    PlatformErrorCodes.PostActionUnsupported,
                    "post-action.LockScreen");
            }

            return await PostActionExecutorSupport.ExecuteDirectCommandAsync(
                command.Provider,
                "post-action.LockScreen",
                "Lock screen command accepted.",
                command.FileName,
                command.Arguments,
                cancellationToken);
        }

        var capability = GetCapability();
        return PlatformOperation.Failed(
            capability.Provider,
            capability.Message,
            PlatformErrorCodes.PostActionUnsupported,
            "post-action.LockScreen");
    }

    private static PlatformOperationResult LockWindowsWorkstation()
    {
        try
        {
            if (!LockWorkStation())
            {
                var error = Marshal.GetLastWin32Error();
                return PlatformOperation.Failed(
                    "windows-lock-workstation",
                    $"LockWorkStation failed with Win32 error {error}.",
                    PlatformErrorCodes.PostActionExecutionFailed,
                    "post-action.LockScreen");
            }

            return PlatformOperation.NativeSuccess(
                "windows-lock-workstation",
                "Workstation locked.",
                "post-action.LockScreen");
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                "windows-lock-workstation",
                $"LockWorkStation failed: {ex.Message}",
                PlatformErrorCodes.PostActionExecutionFailed,
                "post-action.LockScreen");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}

internal static class AndroidEmulatorAdbHelper
{
    public static PlatformCapabilityStatus GetCapability(PostActionExecutorRequest? request)
    {
        return TryBuildKillCommand(request, out var serial, out _)
            ? new PlatformCapabilityStatus(
                true,
                $"Exit emulator is available via adb emu kill for {serial}.",
                Provider: "adb")
            : new PlatformCapabilityStatus(
                false,
                "Exit emulator is unsupported unless the connect address maps to a local Android Emulator instance.",
                Provider: "adb");
    }

    public static async Task<PlatformOperationResult> ExecuteAsync(
        PostActionExecutorRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryBuildKillCommand(request, out var serial, out var command))
        {
            return PlatformOperation.Failed(
                "adb",
                "Exit emulator is unsupported unless the connect address maps to a local Android Emulator instance.",
                PlatformErrorCodes.PostActionUnsupported,
                "post-action.ExitEmulator");
        }

        return await PostActionExecutorSupport.ExecuteDirectCommandAsync(
            "adb",
            "post-action.ExitEmulator",
            $"Android emulator shutdown command accepted for {serial}.",
            command.fileName,
            command.arguments,
            cancellationToken);
    }

    internal static bool TryBuildKillCommand(
        PostActionExecutorRequest? request,
        out string serial,
        out (string fileName, string arguments) command)
    {
        serial = string.Empty;
        command = default;
        if (!TryResolveLocalEmulatorSerial(request?.ConnectAddress, out serial))
        {
            return false;
        }

        var adbPath = string.IsNullOrWhiteSpace(request?.AdbPath)
            ? "adb"
            : request!.AdbPath!.Trim();
        command = (adbPath, $"-s {serial} emu kill");
        return true;
    }

    private static bool TryResolveLocalEmulatorSerial(string? address, out string serial)
    {
        serial = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        if (trimmed.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            serial = trimmed;
            return true;
        }

        if (!WindowsEmulatorExitHelper.TryParseHostAndPort(trimmed, out var host, out var port))
        {
            return false;
        }

        if (!WindowsEmulatorExitHelper.IsLoopbackHost(host))
        {
            return false;
        }

        if (port >= 5555 && port % 2 == 1)
        {
            serial = $"emulator-{port - 1}";
            return true;
        }

        if (port >= 5554 && port % 2 == 0)
        {
            serial = $"emulator-{port}";
            return true;
        }

        return false;
    }
}

internal static class WindowsEmulatorExitHelper
{
    private static readonly string[] WindowTitles =
    [
        "明日方舟",
        "明日方舟 - MuMu模拟器",
        "BlueStacks App Player",
        "BlueStacks",
        "Google Play Games on PC Emulator",
    ];

    public static PlatformCapabilityStatus GetCapability(PostActionExecutorRequest? request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PlatformCapabilityStatus(false, "Windows native emulator shutdown is unavailable on current platform.", Provider: "windows-native");
        }

        if (IsRecognizedWindowsEmulator(request?.ConnectConfig) || CanUsePortFallback(request?.ConnectAddress))
        {
            return new PlatformCapabilityStatus(
                true,
                "Exit emulator is available via Windows native emulator shutdown.",
                Provider: "windows-native");
        }

        return new PlatformCapabilityStatus(
            false,
            "Exit emulator needs a recognized local Windows emulator connection.",
            Provider: "windows-native");
    }

    public static async Task<PlatformOperationResult> ExecuteAsync(
        PostActionExecutorRequest? request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedConfig = NormalizeToken(request?.ConnectConfig);

        return normalizedConfig switch
        {
            "mumu" or "mumu12" or "mumuemulator12" => await ExecuteMuMuAsync(request, cancellationToken),
            "ldplayer" => await ExecuteLdPlayerAsync(request, cancellationToken),
            "nox" => await ExecuteNoxAsync(request, cancellationToken),
            "xyaz" or "memu" => await ExecuteXyazAsync(request, cancellationToken),
            "bluestacks" => await ExecuteBlueStacksAsync(request, cancellationToken),
            _ => ExecuteWindowOrPortFallback(request, "windows-native.default"),
        };
    }

    internal static bool TryResolveMuMuIndex(PostActionExecutorRequest? request, out int emulatorIndex)
    {
        emulatorIndex = 0;
        if (request is null)
        {
            return false;
        }

        var address = (request.ConnectAddress ?? string.Empty).Trim();
        if (string.Equals(address, "127.0.0.1:16384", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (request.MuMuBridgeConnection && int.TryParse(request.MuMu12Index, out emulatorIndex) && emulatorIndex >= 0)
        {
            return true;
        }

        if (TryParseHostAndPort(address, out _, out var port))
        {
            if (port >= 16384)
            {
                emulatorIndex = (port - 16384) / 32;
                return emulatorIndex >= 0;
            }

            if (port == 7555)
            {
                emulatorIndex = 0;
                return true;
            }

            if (port >= 5555)
            {
                emulatorIndex = (port - 5555) / 2;
                return emulatorIndex >= 0;
            }
        }

        if (address.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(address["emulator-".Length..], out port))
        {
            emulatorIndex = (port - 5554) / 2;
            return emulatorIndex >= 0;
        }

        return false;
    }

    internal static bool TryResolveLdPlayerIndex(PostActionExecutorRequest? request, out int emulatorIndex)
    {
        emulatorIndex = 0;
        if (request is null)
        {
            return false;
        }

        if (request.LdPlayerManualSetIndex && int.TryParse(request.LdPlayerIndex, out emulatorIndex) && emulatorIndex >= 0)
        {
            return true;
        }

        var address = (request.ConnectAddress ?? string.Empty).Trim();
        if (TryParseHostAndPort(address, out _, out var port))
        {
            emulatorIndex = (port - 5555) / 2;
            return emulatorIndex >= 0;
        }

        if (address.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(address["emulator-".Length..], out port))
        {
            emulatorIndex = (port - 5554) / 2;
            return emulatorIndex >= 0;
        }

        return false;
    }

    internal static bool TryParseHostAndPort(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var parts = address.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out port))
        {
            return false;
        }

        host = parts[0];
        return !string.IsNullOrWhiteSpace(host);
    }

    internal static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PlatformOperationResult> ExecuteMuMuAsync(PostActionExecutorRequest? request, CancellationToken cancellationToken)
    {
        if (!TryResolveMuMuIndex(request, out var emulatorIndex))
        {
            return PlatformOperation.Failed(
                "windows-native",
                "Unable to resolve MuMu emulator index from current connection.",
                PlatformErrorCodes.PostActionUnsupported,
                "post-action.ExitEmulator");
        }

        if (TryResolveMuMuConsolePath(request?.MuMu12EmulatorPath, out var consolePath))
        {
            return await ExecuteConsoleWithFallbackAsync(
                (timeout, token) => PostActionExecutorSupport.ExecuteDirectCommandAsync(
                    "windows-native",
                    "post-action.ExitEmulator",
                    $"MuMu emulator shutdown command accepted for index {emulatorIndex}.",
                    consolePath,
                    $"api -v {emulatorIndex} shutdown_player",
                    token,
                    timeout),
                () => ExecuteWindowOrPortFallback(request, "windows-native.mumu"),
                cancellationToken);
        }

        return ExecuteWindowOrPortFallback(request, "windows-native.mumu");
    }

    private static async Task<PlatformOperationResult> ExecuteLdPlayerAsync(PostActionExecutorRequest? request, CancellationToken cancellationToken)
    {
        if (!TryResolveLdPlayerIndex(request, out var emulatorIndex))
        {
            return PlatformOperation.Failed(
                "windows-native",
                "Unable to resolve LDPlayer emulator index from current connection.",
                PlatformErrorCodes.PostActionUnsupported,
                "post-action.ExitEmulator");
        }

        if (TryResolveLdPlayerConsolePath(request?.LdPlayerEmulatorPath, out var consolePath))
        {
            return await ExecuteConsoleWithFallbackAsync(
                (timeout, token) => PostActionExecutorSupport.ExecuteDirectCommandAsync(
                    "windows-native",
                    "post-action.ExitEmulator",
                    $"LDPlayer shutdown command accepted for index {emulatorIndex}.",
                    consolePath,
                    $"quit --index {emulatorIndex}",
                    token,
                    timeout),
                () => ExecuteWindowOrPortFallback(request, "windows-native.ldplayer"),
                cancellationToken);
        }

        return ExecuteWindowOrPortFallback(request, "windows-native.ldplayer");
    }

    private static async Task<PlatformOperationResult> ExecuteNoxAsync(PostActionExecutorRequest? request, CancellationToken cancellationToken)
    {
        if (!TryResolveHostPortIndex(request?.ConnectAddress, 62001, 62024, out var emulatorIndex))
        {
            return PlatformOperation.Failed(
                "windows-native",
                "Unable to resolve Nox emulator index from current connection.",
                PlatformErrorCodes.PostActionUnsupported,
                "post-action.ExitEmulator");
        }

        if (TryResolveConsolePathFromProcess("Nox", "NoxConsole.exe", out var consolePath))
        {
            return await ExecuteConsoleWithFallbackAsync(
                (timeout, token) => PostActionExecutorSupport.ExecuteDirectCommandAsync(
                    "windows-native",
                    "post-action.ExitEmulator",
                    $"Nox shutdown command accepted for index {emulatorIndex}.",
                    consolePath,
                    $"quit -index:{emulatorIndex}",
                    token,
                    timeout),
                () => ExecuteWindowOrPortFallback(request, "windows-native.nox"),
                cancellationToken);
        }

        return ExecuteWindowOrPortFallback(request, "windows-native.nox");
    }

    private static async Task<PlatformOperationResult> ExecuteXyazAsync(PostActionExecutorRequest? request, CancellationToken cancellationToken)
    {
        if (!TryResolveXyazIndex(request?.ConnectAddress, out var emulatorIndex))
        {
            return PlatformOperation.Failed(
                "windows-native",
                "Unable to resolve XYAZ emulator index from current connection.",
                PlatformErrorCodes.PostActionUnsupported,
                "post-action.ExitEmulator");
        }

        if (TryResolveConsolePathFromProcess("MEmu", "memuc.exe", out var consolePath))
        {
            return await ExecuteConsoleWithFallbackAsync(
                (timeout, token) => PostActionExecutorSupport.ExecuteDirectCommandAsync(
                    "windows-native",
                    "post-action.ExitEmulator",
                    $"XYAZ shutdown command accepted for index {emulatorIndex}.",
                    consolePath,
                    $"stop -i {emulatorIndex}",
                    token,
                    timeout),
                () => ExecuteWindowOrPortFallback(request, "windows-native.xyaz"),
                cancellationToken);
        }

        return ExecuteWindowOrPortFallback(request, "windows-native.xyaz");
    }

    private static Task<PlatformOperationResult> ExecuteBlueStacksAsync(PostActionExecutorRequest? request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windowFallback = ExecuteWindowOrPortFallback(request, "windows-native.bluestacks");
        if (windowFallback.Success)
        {
            return Task.FromResult(windowFallback);
        }

        if (TryKillSingleProcessByName("HD-Player", out var message))
        {
            return Task.FromResult(PlatformOperation.NativeSuccess("windows-native", message, "post-action.ExitEmulator"));
        }

        return Task.FromResult(PlatformOperation.Failed(
            "windows-native",
            "Unable to close BlueStacks with window or process fallback.",
            PlatformErrorCodes.PostActionExecutionFailed,
            "post-action.ExitEmulator"));
    }

    private static PlatformOperationResult ExecuteWindowOrPortFallback(PostActionExecutorRequest? request, string provider)
    {
        if (TryCloseWindowProcess(out var windowMessage))
        {
            return PlatformOperation.NativeSuccess(provider, windowMessage, "post-action.ExitEmulator");
        }

        if (TryKillProcessByPort(request?.ConnectAddress, out var portMessage))
        {
            return PlatformOperation.NativeSuccess(provider, portMessage, "post-action.ExitEmulator");
        }

        return PlatformOperation.Failed(
            provider,
            "Unable to locate a local emulator window or owning process for current connection.",
            PlatformErrorCodes.PostActionUnsupported,
            "post-action.ExitEmulator");
    }

    internal static async Task<PlatformOperationResult> ExecuteConsoleWithFallbackAsync(
        Func<TimeSpan, CancellationToken, Task<PlatformOperationResult>> executeConsoleAsync,
        Func<PlatformOperationResult> executeFallback,
        CancellationToken cancellationToken)
    {
        var result = await executeConsoleAsync(PostActionExecutorSupport.EmulatorConsoleExitTimeout, cancellationToken);
        return result.Success ? result : executeFallback();
    }

    private static bool IsRecognizedWindowsEmulator(string? connectConfig)
    {
        return NormalizeToken(connectConfig) is "mumu" or "mumu12" or "mumuemulator12" or "ldplayer" or "nox" or "xyaz" or "memu" or "bluestacks";
    }

    private static bool TryResolveMuMuConsolePath(string? emulatorPath, out string consolePath)
    {
        if (TryResolveMuMuConsolePathFromInput(emulatorPath, out consolePath))
        {
            return true;
        }

        if (TryResolveMainModulePath("MuMuNxDevice", out var newPath) && TryResolveMuMuConsolePathFromInput(newPath, out consolePath))
        {
            return true;
        }

        return TryResolveMainModulePath("MuMuPlayer", out var oldPath) && TryResolveMuMuConsolePathFromInput(oldPath, out consolePath);
    }

    private static bool TryResolveMuMuConsolePathFromInput(string? path, out string consolePath)
    {
        consolePath = string.Empty;
        foreach (var candidate in EnumerateMuMuConsoleCandidates(path))
        {
            if (File.Exists(candidate))
            {
                consolePath = candidate;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateMuMuConsoleCandidates(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var root in EnumerateCandidateRoots(path))
        {
            yield return Path.Combine(root, "nx_main", "MuMuManager.exe");
            yield return Path.Combine(root, "shell", "MuMuManager.exe");
        }
    }

    private static bool TryResolveLdPlayerConsolePath(string? emulatorPath, out string consolePath)
    {
        consolePath = string.Empty;
        foreach (var root in EnumerateCandidateRoots(emulatorPath))
        {
            var candidate = Path.Combine(root, "ldconsole.exe");
            if (File.Exists(candidate))
            {
                consolePath = candidate;
                return true;
            }
        }

        return TryResolveConsolePathFromProcess("dnplayer", "ldconsole.exe", out consolePath);
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        if (File.Exists(path))
        {
            var fileName = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return directory;
                yield return Path.GetFullPath(Path.Combine(directory, ".."));
                if (string.Equals(fileName, "MuMuNxDevice.exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
                }
            }

            yield break;
        }

        if (Directory.Exists(path))
        {
            yield return path;
            yield return Path.GetFullPath(Path.Combine(path, ".."));
        }
    }

    private static bool TryResolveConsolePathFromProcess(string processName, string consoleExeName, out string consolePath)
    {
        consolePath = string.Empty;
        if (!TryResolveMainModulePath(processName, out var executablePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var candidate = Path.Combine(directory, consoleExeName);
        if (!File.Exists(candidate))
        {
            return false;
        }

        consolePath = candidate;
        return true;
    }

    private static bool TryResolveMainModulePath(string processName, out string executablePath)
    {
        executablePath = string.Empty;
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            return false;
        }

        try
        {
            executablePath = processes[0].MainModule?.FileName ?? string.Empty;
            return !string.IsNullOrWhiteSpace(executablePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveHostPortIndex(string? address, int primaryPort, int basePort, out int emulatorIndex)
    {
        emulatorIndex = 0;
        if (!TryParseHostAndPort(address, out _, out var port))
        {
            return false;
        }

        emulatorIndex = port == primaryPort ? 0 : port - basePort;
        return emulatorIndex >= 0;
    }

    private static bool TryResolveXyazIndex(string? address, out int emulatorIndex)
    {
        emulatorIndex = 0;
        return TryParseHostAndPort(address, out _, out var port)
               && (emulatorIndex = (port - 21503) / 10) >= 0;
    }

    private static bool TryCloseWindowProcess(out string message)
    {
        message = string.Empty;
        if (!TryFindWindowProcessId(out var pid))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.CloseMainWindow() || !process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    return false;
                }
            }

            message = $"Closed emulator process {pid} via window fallback.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindWindowProcessId(out int pid)
    {
        pid = 0;
        foreach (var title in WindowTitles)
        {
            var hwnd = FindWindow(null!, title);
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            GetWindowThreadProcessId(hwnd, out pid);
            if (pid > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryKillSingleProcessByName(string processName, out string message)
    {
        message = string.Empty;
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length != 1)
        {
            return false;
        }

        try
        {
            using var process = processes[0];
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(20000))
            {
                return false;
            }

            message = $"Closed emulator process {process.Id} via process fallback.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryKillProcessByPort(string? address, out string message)
    {
        message = string.Empty;
        if (!TryResolvePortFallbackEndpoint(address, out var expectedLocalEndpoint))
        {
            return false;
        }

        if (!TryGetOwningProcessId(expectedLocalEndpoint, out var pid))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                return false;
            }

            message = $"Closed emulator process {pid} via TCP port fallback.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetOwningProcessId(string expectedLocalEndpoint, out int pid)
    {
        pid = 0;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = Regex.Replace(line.Trim(), "\\s+", " ");
                var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(parts[1], expectedLocalEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(parts[^1], out pid) && pid > 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool CanUsePortFallback(string? address)
        => TryResolvePortFallbackEndpoint(address, out _);

    private static bool TryResolvePortFallbackEndpoint(string? address, out string endpoint)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        if (!TryParseHostAndPort(address.Trim(), out var host, out var port) || !IsLoopbackHost(host))
        {
            return false;
        }

        endpoint = $"{host}:{port}";
        return true;
    }

    private static string NormalizeToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var buffer = new char[text.Length];
        var index = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer, 0, index);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, EntryPoint = "FindWindow")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int id);
}
