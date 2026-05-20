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

    private static bool TryRun(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore best-effort shutdown failures.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
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

    private static string[] BuildLinuxZenityArguments(string title, string message, StartupPromptSeverity severity)
    {
        return severity == StartupPromptSeverity.Warning
            ? ["--warning", $"--title={title}", $"--text={message}", "--no-wrap"]
            : ["--error", $"--title={title}", $"--text={message}", "--no-wrap"];
    }

    private static string[] BuildLinuxKdialogArguments(string title, string message, StartupPromptSeverity severity)
    {
        return severity == StartupPromptSeverity.Warning
            ? ["--title", title, "--sorry", message]
            : ["--title", title, "--error", message];
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
