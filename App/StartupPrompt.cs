using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MAAUnified.App;

internal enum StartupPromptSeverity
{
    Warning = 0,
    Error = 1,
}

internal static class StartupPrompt
{
    private const uint WarningMessageBoxFlags = 0x00000030 | 0x00002000 | 0x00010000 | 0x00040000;
    private const uint ErrorMessageBoxFlags = 0x00000010 | 0x00002000 | 0x00010000 | 0x00040000;
    private const uint MessageBoxYesNoFlags = 0x00000004;
    private const int MessageBoxYesResult = 6;
    private const int MessageBoxNoResult = 7;

    public static void Show(string title, string message, StartupPromptSeverity severity)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = MessageBox(
                    nint.Zero,
                    message,
                    title,
                    severity == StartupPromptSeverity.Warning ? WarningMessageBoxFlags : ErrorMessageBoxFlags);
                return;
            }

            if (OperatingSystem.IsMacOS() && TryRun("osascript", BuildMacOsArguments(title, message, severity)))
            {
                return;
            }

            if (OperatingSystem.IsLinux() && Program.HasLinuxDesktopDisplay())
            {
                if (TryRun("zenity", BuildLinuxZenityArguments(title, message, severity))
                    || TryRun("kdialog", BuildLinuxKdialogArguments(title, message, severity))
                    || TryRun("xmessage", ["-center", message]))
                {
                    return;
                }
            }
        }
        catch
        {
            // Fall back to stderr only.
        }

        Console.Error.WriteLine($"{title}: {message}");
    }

    /// <summary>
    /// 展示带「重试 / 忽略本次」两个选项的启动提示。
    /// Windows 走原生 MessageBox，macOS 走 osascript 对话框，Linux 走 zenity/kdialog；
    /// 无法展示或无桌面环境时返回 <c>null</c>，调用方按「忽略本次」处理（修复标记保留，下次启动再提示）。
    /// </summary>
    /// <returns>用户选择重试时为 <c>true</c>，选择忽略时为 <c>false</c>，无法获取选择时为 <c>null</c>。</returns>
    public static bool? ShowChoice(
        string title,
        string message,
        string acceptText,
        string rejectText,
        StartupPromptSeverity severity)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var result = MessageBox(
                    nint.Zero,
                    message,
                    title,
                    (severity == StartupPromptSeverity.Warning ? WarningMessageBoxFlags : ErrorMessageBoxFlags) | MessageBoxYesNoFlags);
                return result switch
                {
                    MessageBoxYesResult => true,
                    MessageBoxNoResult => false,
                    _ => null,
                };
            }

            if (OperatingSystem.IsMacOS()
                && TryRunCapture("osascript", BuildMacOsChoiceArguments(title, message, acceptText, rejectText, severity), out var stdout))
            {
                return stdout.Contains($"button returned:{acceptText}", StringComparison.Ordinal);
            }

            if (OperatingSystem.IsLinux() && Program.HasLinuxDesktopDisplay())
            {
                if (TryRunWithExitCode("zenity", BuildLinuxZenityChoiceArguments(title, message, acceptText, rejectText), out var zenityExitCode))
                {
                    return zenityExitCode switch
                    {
                        0 => true,
                        1 => false,
                        _ => null,
                    };
                }

                if (TryRunWithExitCode("kdialog", BuildLinuxKdialogChoiceArguments(title, message, acceptText, rejectText), out var kdialogExitCode))
                {
                    return kdialogExitCode switch
                    {
                        0 => true,
                        1 => false,
                        _ => null,
                    };
                }
            }
        }
        catch
        {
            // Fall through to stderr.
        }

        Console.Error.WriteLine($"{title}: {message} [{rejectText} / {acceptText}]");
        return null;
    }

    private static bool TryRun(string fileName, IReadOnlyList<string> arguments)
        => TryRunWithExitCode(fileName, arguments, out var exitCode) && exitCode == 0;

    private static bool TryRunWithExitCode(string fileName, IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using var process = CreateProcess(fileName, arguments, redirectOutput: false);
            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                KillBestEffort(process);
                return false;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRunCapture(string fileName, IReadOnlyList<string> arguments, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            using var process = CreateProcess(fileName, arguments, redirectOutput: true);
            if (!process.Start())
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                KillBestEffort(process);
                return false;
            }

            stdout = outputTask.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Process CreateProcess(string fileName, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardError = false,
                RedirectStandardOutput = redirectOutput,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static void KillBestEffort(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore best-effort shutdown failures.
        }
    }

    private static string[] BuildMacOsArguments(string title, string message, StartupPromptSeverity severity)
    {
        var normalizedTitle = EscapeAppleScript(title);
        var normalizedMessage = EscapeAppleScript(message);
        var icon = severity == StartupPromptSeverity.Warning ? "caution" : "stop";
        return
        [
            "-e",
            $"display alert \"{normalizedTitle}\" message \"{normalizedMessage}\" as {icon}"
        ];
    }

    private static string[] BuildMacOsChoiceArguments(
        string title,
        string message,
        string acceptText,
        string rejectText,
        StartupPromptSeverity severity)
    {
        var normalizedTitle = EscapeAppleScript(title);
        var normalizedMessage = EscapeAppleScript(message);
        var normalizedAccept = EscapeAppleScript(acceptText);
        var normalizedReject = EscapeAppleScript(rejectText);
        var icon = severity == StartupPromptSeverity.Warning ? "caution" : "stop";
        return
        [
            "-e",
            $"display dialog \"{normalizedTitle}\" message \"{normalizedMessage}\" buttons {{\"{normalizedReject}\", \"{normalizedAccept}\"}} default button \"{normalizedAccept}\" with icon {icon}"
        ];
    }

    private static string[] BuildLinuxZenityArguments(string title, string message, StartupPromptSeverity severity)
    {
        return severity == StartupPromptSeverity.Warning
            ? ["--warning", $"--title={title}", $"--text={message}", "--no-wrap"]
            : ["--error", $"--title={title}", $"--text={message}", "--no-wrap"];
    }

    private static string[] BuildLinuxZenityChoiceArguments(
        string title,
        string message,
        string acceptText,
        string rejectText)
    {
        return
        [
            "--question",
            $"--title={title}",
            $"--text={message}",
            $"--ok-label={acceptText}",
            $"--cancel-label={rejectText}",
            "--no-wrap"
        ];
    }

    private static string[] BuildLinuxKdialogArguments(string title, string message, StartupPromptSeverity severity)
    {
        return severity == StartupPromptSeverity.Warning
            ? ["--title", title, "--sorry", message]
            : ["--title", title, "--error", message];
    }

    private static string[] BuildLinuxKdialogChoiceArguments(
        string title,
        string message,
        string acceptText,
        string rejectText)
    {
        return
        [
            "--title",
            title,
            "--yesno",
            message,
            "--yes-label",
            acceptText,
            "--no-label",
            rejectText
        ];
    }

    private static string EscapeAppleScript(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
