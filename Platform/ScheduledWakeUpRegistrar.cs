using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace MAAUnified.Platform;

/// <summary>
/// Result of one scheduled-task CLI invocation. <see cref="NotFound"/> lets the registrar treat
/// a missing task as an already-completed delete (WPF 0b40a31009 filters 0x80070002 the same way).
/// </summary>
/// <param name="Success">Whether the command completed successfully.</param>
/// <param name="NotFound">Whether the task did not exist.</param>
/// <param name="Message">Diagnostic message.</param>
public sealed record ScheduledTaskCommandResult(bool Success, bool NotFound, string Message);

/// <summary>
/// Minimal Task Scheduler surface used by <see cref="WindowsScheduledWakeUpRegistrar"/>. Tests
/// inject a fake so registration and deletion are exercised without touching a real scheduler.
/// </summary>
public interface IScheduledTaskClient
{
    Task<ScheduledTaskCommandResult> CreateAsync(
        string taskName,
        string taskXml,
        CancellationToken cancellationToken = default);

    Task<ScheduledTaskCommandResult> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable task identity shared by every build of this executable. WPF derives the task name from
/// the process path hash (ScheduledWakeUp._taskName) so reinstalling into another directory does
/// not collide with a stale task.
/// </summary>
public static class ScheduledWakeUpTaskIdentity
{
    public const string TaskNamePrefix = "MaaUnifiedWakeUp_";

    public static string BuildTaskName(string executablePath)
    {
        var normalized = string.IsNullOrWhiteSpace(executablePath)
            ? "maa-unified"
            : executablePath.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
        return TaskNamePrefix + hash;
    }
}

/// <summary>
/// Builds the Task Scheduler XML for one wake-enabled daily trigger per slot. The wake-up lead is
/// fixed at five minutes (WPF ScheduledWakeUp.WakeUpLeadMinutes) because the in-app scheduler ticks
/// once per minute and the task only prepares the machine before the timer takes over.
/// </summary>
public static class ScheduledWakeUpTaskXml
{
    public const int WakeUpLeadMinutes = 5;

    public static string Build(ScheduledWakeUpPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        builder.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        builder.AppendLine("  <RegistrationInfo>");
        builder.AppendLine("    <Description>Wakes up the computer and launches MAA before an enabled scheduled timer.</Description>");
        builder.AppendLine("  </RegistrationInfo>");
        builder.AppendLine("  <Triggers>");
        foreach (var slot in plan.Slots.OrderBy(static slot => slot.Index))
        {
            builder.AppendLine("    <CalendarTrigger>");
            builder.AppendLine("      <StartBoundary>" + FormatStartBoundary(slot) + "</StartBoundary>");
            builder.AppendLine("      <Enabled>true</Enabled>");
            builder.AppendLine("      <ScheduleByDay>");
            builder.AppendLine("        <DaysInterval>1</DaysInterval>");
            builder.AppendLine("      </ScheduleByDay>");
            builder.AppendLine("    </CalendarTrigger>");
        }

        builder.AppendLine("  </Triggers>");
        builder.AppendLine("  <Principals>");
        builder.AppendLine("    <Principal id=\"Author\">");
        builder.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        builder.AppendLine("      <RunLevel>LeastPrivilege</RunLevel>");
        builder.AppendLine("    </Principal>");
        builder.AppendLine("  </Principals>");
        builder.AppendLine("  <Settings>");
        builder.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        builder.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        builder.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        builder.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        builder.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        builder.AppendLine("    <WakeToRun>true</WakeToRun>");
        builder.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        builder.AppendLine("    <Enabled>true</Enabled>");
        builder.AppendLine("  </Settings>");
        builder.AppendLine("  <Actions Context=\"Author\">");
        builder.AppendLine("    <Exec>");
        builder.AppendLine("      <Command>" + Escape(plan.ExecutablePath) + "</Command>");
        if (!string.IsNullOrWhiteSpace(plan.Arguments))
        {
            builder.AppendLine("      <Arguments>" + Escape(plan.Arguments) + "</Arguments>");
        }

        if (!string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            builder.AppendLine("      <WorkingDirectory>" + Escape(plan.WorkingDirectory) + "</WorkingDirectory>");
        }

        builder.AppendLine("    </Exec>");
        builder.AppendLine("  </Actions>");
        builder.AppendLine("</Task>");
        return builder.ToString();
    }

    internal static string FormatStartBoundary(ScheduledWakeUpSlot slot)
    {
        var totalMinutes = (slot.Hour * 60) + slot.Minute - WakeUpLeadMinutes;
        var wakeUpMinuteOfDay = ((totalMinutes % 1440) + 1440) % 1440;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"2000-01-01T{wakeUpMinuteOfDay / 60:D2}:{wakeUpMinuteOfDay % 60:D2}:00");
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}

/// <summary>
/// Windows Task Scheduler registrar. The task is always rebuilt (delete then create) so removed
/// or retimed slots cannot linger, mirroring WPF ScheduledWakeUp.SyncAll.
/// </summary>
public sealed class WindowsScheduledWakeUpRegistrar : IScheduledWakeUpRegistrar
{
    private readonly IScheduledTaskClient _client;
    private readonly string _executablePath;

    public WindowsScheduledWakeUpRegistrar(IScheduledTaskClient client, string? executablePath = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
    }

    public PlatformCapabilityStatus Capability { get; } = new(
        Supported: true,
        Message: "Scheduled wake-up uses Windows Task Scheduler.",
        Provider: "windows-task-scheduler");

    public string TaskName => ScheduledWakeUpTaskIdentity.BuildTaskName(_executablePath);

    public async Task<PlatformOperationResult> SyncAsync(
        ScheduledWakeUpPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var taskName = string.IsNullOrWhiteSpace(_executablePath)
            ? ScheduledWakeUpTaskIdentity.BuildTaskName(plan.ExecutablePath)
            : TaskName;

        var deleteResult = await _client.DeleteAsync(taskName, cancellationToken).ConfigureAwait(false);
        var deleteFailed = !deleteResult.Success && !deleteResult.NotFound;
        if (!plan.Enabled || plan.Slots.Count == 0)
        {
            return deleteFailed
                ? PlatformOperation.Failed(
                    Capability.Provider,
                    $"Failed to remove the previous scheduled wake-up task: {deleteResult.Message}",
                    PlatformErrorCodes.ScheduledWakeUpRemoveFailed,
                    "scheduledWakeUp.sync")
                : PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    $"Scheduled wake-up task `{taskName}` removed.",
                    "scheduledWakeUp.sync");
        }

        var executablePath = string.IsNullOrWhiteSpace(plan.ExecutablePath)
            ? _executablePath
            : plan.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "The executable path is unavailable for the scheduled wake-up task.",
                PlatformErrorCodes.ScheduledWakeUpExecutableMissing,
                "scheduledWakeUp.sync");
        }

        var effectivePlan = plan with { ExecutablePath = executablePath };
        var xml = ScheduledWakeUpTaskXml.Build(effectivePlan);
        var createResult = await _client.CreateAsync(taskName, xml, cancellationToken).ConfigureAwait(false);
        if (!createResult.Success)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                $"Failed to register the scheduled wake-up task: {createResult.Message}",
                PlatformErrorCodes.ScheduledWakeUpRegisterFailed,
                "scheduledWakeUp.sync");
        }

        var message = $"Scheduled wake-up task `{taskName}` registered with {plan.Slots.Count} trigger(s).";
        if (deleteFailed)
        {
            // `/Create /F` overwrites in place, so a failed pre-delete is recoverable; keep the
            // reason for diagnostics instead of aborting the rebuild.
            message += $" Previous task removal reported: {deleteResult.Message}";
        }

        return PlatformOperation.NativeSuccess(Capability.Provider, message, "scheduledWakeUp.sync");
    }
}

/// <summary>
/// Capability-declaring registrar for macOS/Linux (and any other OS): the settings switch is
/// disabled and no task is ever created. The planning doc explicitly rejects porting the Win32
/// implementation; launchd/systemd timers would need a separate design.
/// </summary>
public sealed class UnsupportedScheduledWakeUpRegistrar : IScheduledWakeUpRegistrar
{
    public PlatformCapabilityStatus Capability { get; } = new(
        Supported: false,
        Message: "Scheduled wake-up is unavailable: Windows Task Scheduler is required.",
        Provider: "unsupported");

    public Task<PlatformOperationResult> SyncAsync(
        ScheduledWakeUpPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformOperation.Failed(
            Capability.Provider,
            Capability.Message,
            PlatformErrorCodes.ScheduledWakeUpUnsupported,
            "scheduledWakeUp.sync"));
    }
}

/// <summary>
/// Executes <c>schtasks.exe</c>. The XML file is written as UTF-16 because Task Scheduler rejects
/// non-Unicode task definitions on some Windows builds.
/// </summary>
public sealed class SchtasksScheduledTaskClient : IScheduledTaskClient
{
    private const int CommandTimeoutMilliseconds = 30_000;
    private readonly string _schtasksPath;

    public SchtasksScheduledTaskClient(string schtasksPath = "schtasks.exe")
    {
        _schtasksPath = schtasksPath;
    }

    public async Task<ScheduledTaskCommandResult> CreateAsync(
        string taskName,
        string taskXml,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskXml);

        var xmlPath = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-wakeup-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(xmlPath, taskXml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
            var arguments = $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F";
            return await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ScheduledTaskCommandResult(false, false, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
                // Best effort cleanup; the temp file is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup; the temp file is harmless.
            }
        }
    }

    public Task<ScheduledTaskCommandResult> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return RunAsync($"/Delete /TN \"{taskName}\" /F", cancellationToken);
    }

    private async Task<ScheduledTaskCommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _schtasksPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            return new ScheduledTaskCommandResult(false, false, "Failed to start schtasks.exe.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ScheduledTaskCommandResult(false, false, "schtasks.exe timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var message = string.Join(
            " ",
            new[] { output, error }
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .Select(static part => part.Trim()));
        if (process.ExitCode == 0)
        {
            return new ScheduledTaskCommandResult(true, false, message);
        }

        var notFound = message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("找不到", StringComparison.Ordinal)
                       || message.Contains("不存在", StringComparison.Ordinal);
        return new ScheduledTaskCommandResult(false, notFound, message);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort.
        }
    }
}
