using System.Text;
using System.Text.Json;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Models;
using MAAUnified.CoreBridge;

namespace MAAUnified.App.ViewModels.Infrastructure;

public enum ConnectionFailureCategory
{
    Unknown,
    AddressEmpty,
    AdbPathInvalid,
    MacBundledAdbUnavailable,
    EmulatorPathInvalid,
    ConnectTimeout,
    AdbDeviceUnavailable,
    TouchModeUnavailable,
    CoreConnectFailed,
    CoreUnavailable,
    AdbCommandFailed,
}

public sealed record ConnectionAttemptFailure(string Candidate, UiOperationResult Result);

public sealed record AdbCommandFailureInfo(
    string CommandName,
    string FileName,
    string Arguments,
    int? ExitCode,
    string? StandardError,
    string? StandardOutput,
    string? ExceptionMessage = null)
{
    public bool Success => ExitCode == 0 && string.IsNullOrWhiteSpace(ExceptionMessage);

    public string DisplayCommand => string.IsNullOrWhiteSpace(Arguments)
        ? FileName
        : $"{FileName} {Arguments}";

    public string BuildDetails()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ADB command: {CommandName}");
        builder.AppendLine($"Process: {DisplayCommand}");
        if (ExitCode is int exitCode)
        {
            builder.AppendLine($"ExitCode: {exitCode}");
        }

        if (!string.IsNullOrWhiteSpace(ExceptionMessage))
        {
            builder.AppendLine($"Exception: {ExceptionMessage.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(StandardError))
        {
            builder.AppendLine("stderr:");
            builder.AppendLine(StandardError.Trim());
        }

        if (!string.IsNullOrWhiteSpace(StandardOutput))
        {
            builder.AppendLine("stdout:");
            builder.AppendLine(StandardOutput.Trim());
        }

        return builder.ToString().Trim();
    }
}

public sealed record ConnectionFailureDiagnostic(
    bool IsSpecific,
    ConnectionFailureCategory Category,
    string Message,
    string Suggestion,
    string Details)
{
    public string BuildDialogMessage()
    {
        if (!IsSpecific)
        {
            return Message;
        }

        if (string.IsNullOrWhiteSpace(Suggestion))
        {
            return Message;
        }

        return $"{Message}{Environment.NewLine}{Suggestion}";
    }
}

public static class ConnectionFailureDiagnosticBuilder
{
    public static ConnectionFailureDiagnostic Build(
        UiOperationResult connectResult,
        ConnectionGameSharedStateViewModel? state,
        IEnumerable<ConnectionAttemptFailure>? candidateFailures = null,
        AdbCommandFailureInfo? adbCommandFailure = null,
        string? language = null)
    {
        var attempts = candidateFailures?.ToList() ?? [];
        var details = BuildDetails(connectResult, attempts, adbCommandFailure);
        var fallbackMessage = BuildGenericMessage(state, language);
        var fallbackSuggestion = Select(language, "请检查连接设置，尝试重启模拟器与 ADB；仍失败时重启电脑。", "Check connection settings, try restarting the emulator and ADB, then reboot if it still fails.");

        var code = connectResult.Error?.Code ?? string.Empty;
        var messageText = connectResult.Message ?? string.Empty;
        var rawText = FirstNonEmpty(connectResult.Error?.Details, connectResult.Message) ?? string.Empty;
        var extracted = ExtractCoreReason(connectResult.Error?.Details);

        if (IsCode(code, nameof(CoreErrorCode.ConnectTimeout)))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.ConnectTimeout,
                Select(language, "连接模拟器超时。", "Connecting to the emulator timed out."),
                Select(language, "请确认模拟器已启动，连接地址和端口正确，ADB 服务可用。", "Make sure the emulator is running, the address and port are correct, and ADB is available."),
                details);
        }

        if (ContainsAny(rawText, "TouchModeNotAvailable", "Touch mode is not available"))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.TouchModeUnavailable,
                Select(language, "当前触控模式不可用。", "The current touch mode is not available."),
                Select(language, "请前往连接设置切换其他触控模式后重试。", "Switch to another touch mode in connection settings, then try again."),
                details);
        }

        if (IsCode(code, nameof(CoreErrorCode.NotInitialized)) || IsCode(code, nameof(CoreErrorCode.Disposed)))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.CoreUnavailable,
                Select(language, "连接核心尚未就绪或已释放。", "The connection core is not ready or has been disposed."),
                Select(language, "请稍等后重试；如果持续出现，请重启应用。", "Try again after a moment; restart the app if it keeps happening."),
                details);
        }

        if (ContainsAny(messageText, "InitFailed callback", "ret=false", "invalid async call id"))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.CoreConnectFailed,
                Select(language, $"连接核心返回失败：{messageText.Trim()}", $"The connection core reported a failure: {messageText.Trim()}"),
                Select(language, "请检查模拟器、ADB 路径和连接配置；详情中保留了 core 回调信息。", "Check the emulator, ADB path, and connection config; core callback details are preserved below."),
                details);
        }

        if (LooksLikeConnectCommandNotConnected(extracted) || LooksLikeConnectCommandNotConnected(messageText))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.AdbDeviceUnavailable,
                Select(language, "ADB 未连接到目标设备。", "ADB did not connect to the target device."),
                Select(language, "请检查连接地址和端口是否正确，常见 ADB 端口为 5555；确认模拟器已启动、ADB 已开启，并与电脑在同一网络内。", "Check that the address and port are correct; common ADB port is 5555. Make sure the emulator is running, ADB is enabled, and the device is on the same network."),
                details);
        }

        if (!string.IsNullOrWhiteSpace(extracted)
            && !string.Equals(extracted, "ConnectFailed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extracted, "Disconnect", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(extracted, "Connection command failed to exec"))
            {
                return new ConnectionFailureDiagnostic(
                    true,
                    ConnectionFailureCategory.AdbCommandFailed,
                    Select(language, "ADB 启动失败。", "ADB failed to start."),
                    Select(language, "请检查连接设置中的 ADB 路径是否指向 adb 可执行文件，而不是 dmg、zip、目录或其他安装包。", "Check that the ADB path points to the adb executable, not a dmg, zip, folder, or installer package."),
                    details);
            }

            return new ConnectionFailureDiagnostic(
                true,
                ConnectionFailureCategory.CoreConnectFailed,
                Select(language, "连接失败。", "Connection failed."),
                Select(language, "请点击“详细报错”查看核心返回的错误信息，并据此检查模拟器和 ADB 连接。", "Open error details to inspect the core error output, then check the emulator and ADB connection."),
                details);
        }

        if (adbCommandFailure is not null && !adbCommandFailure.Success)
        {
            return new ConnectionFailureDiagnostic(
                true,
                LooksLikeAdbDeviceUnavailable(adbCommandFailure)
                    ? ConnectionFailureCategory.AdbDeviceUnavailable
                    : ConnectionFailureCategory.AdbCommandFailed,
                BuildAdbCommandFailureMessage(adbCommandFailure, language),
                BuildAdbCommandFailureSuggestion(adbCommandFailure, language),
                details);
        }

        var settingsHint = state?.BuildConnectionSettingsHintMessage();
        if (!string.IsNullOrWhiteSpace(settingsHint))
        {
            return new ConnectionFailureDiagnostic(
                true,
                ClassifySettingsHint(settingsHint),
                BuildSpecificSettingsMessage(settingsHint, language),
                Select(language, "请先修正连接设置后再重试。", "Fix the connection settings first, then try again."),
                details);
        }

        return new ConnectionFailureDiagnostic(
            false,
            ConnectionFailureCategory.Unknown,
            fallbackMessage,
            fallbackSuggestion,
            details);
    }

    private static string BuildGenericMessage(ConnectionGameSharedStateViewModel? state, string? language)
    {
        var segments = new List<string>
        {
            Select(
                language,
                "连接失败。请“检查连接设置” -> “尝试重启模拟器与 ADB” -> “重启电脑”。",
                "Connection failed. Check connection settings -> try restarting the emulator and ADB -> reboot the computer."),
        };

        var settingsHint = state?.BuildConnectionSettingsHintMessage();
        if (!string.IsNullOrWhiteSpace(settingsHint))
        {
            segments.Add(settingsHint);
        }

        return string.Join(Environment.NewLine, segments);
    }

    private static string BuildAdbCommandFailureMessage(AdbCommandFailureInfo failure, string? language)
    {
        if (LooksLikeAdbExecutablePathProblem(failure))
        {
            return Select(language, "ADB 启动失败。", "ADB failed to start.");
        }

        if (LooksLikeAdbDeviceUnavailable(failure))
        {
            return Select(language, "ADB 未连接到目标设备。", "ADB did not connect to the target device.");
        }

        return Select(language, "ADB 命令执行失败。", "ADB command failed.");
    }

    private static string BuildAdbCommandFailureSuggestion(AdbCommandFailureInfo failure, string? language)
    {
        if (LooksLikeAdbExecutablePathProblem(failure))
        {
            return Select(
                language,
                "请检查连接设置中的 ADB 路径是否指向 adb 可执行文件，而不是 dmg、zip、目录或其他安装包。",
                "Check that the ADB path points to the adb executable, not a dmg, zip, folder, or installer package.");
        }

        if (LooksLikeAdbDeviceUnavailable(failure))
        {
            return Select(
                language,
                "请检查连接地址和端口是否正确，常见 ADB 端口为 5555；确认模拟器已启动、ADB 已开启，并与电脑在同一网络内。",
                "Check that the address and port are correct; common ADB port is 5555. Make sure the emulator is running, ADB is enabled, and the device is on the same network.");
        }

        return Select(
            language,
            "请点击“详细报错”查看 ADB 输出；确认 ADB 可执行，或尝试重启模拟器与 ADB。",
            "Open error details to inspect ADB output; make sure ADB is executable, or restart the emulator and ADB.");
    }

    private static bool LooksLikeAdbExecutablePathProblem(AdbCommandFailureInfo failure)
    {
        var text = string.Join(
            "\n",
            failure.FileName,
            failure.StandardError,
            failure.StandardOutput,
            failure.ExceptionMessage);
        return ContainsAny(
            text,
            "Permission denied",
            "trying to start process",
            ".dmg",
            ".zip",
            ".app",
            "No such file",
            "No such file or directory",
            "not found");
    }

    private static bool LooksLikeAdbDeviceUnavailable(AdbCommandFailureInfo failure)
        => ContainsAny(
            BuildAdbFailureSearchText(failure),
            "no such device",
            "device offline",
            "device unauthorized",
            "unable to connect",
            "failed to connect",
            "cannot connect",
            "Connection refused",
            "No route to host",
            "timed out");

    private static string BuildAdbFailureSearchText(AdbCommandFailureInfo failure)
        => string.Join(
            "\n",
            failure.CommandName,
            failure.FileName,
            failure.Arguments,
            failure.StandardError,
            failure.StandardOutput,
            failure.ExceptionMessage);

    private static string BuildSpecificSettingsMessage(string settingsHint, string? language)
    {
        var normalized = settingsHint.Trim();
        return Select(
            language,
            $"连接失败：{normalized}",
            $"Connection failed: {normalized}");
    }

    private static string BuildDetails(
        UiOperationResult connectResult,
        IReadOnlyList<ConnectionAttemptFailure> candidateFailures,
        AdbCommandFailureInfo? adbCommandFailure)
    {
        var builder = new StringBuilder();
        if (adbCommandFailure is not null)
        {
            builder.AppendLine(adbCommandFailure.BuildDetails());
            builder.AppendLine();
        }

        builder.AppendLine("Final connection result:");
        builder.AppendLine($"Code: {FormatCode(connectResult.Error?.Code)}");
        builder.AppendLine($"Message: {connectResult.Message}");
        if (!string.IsNullOrWhiteSpace(connectResult.Error?.Details))
        {
            builder.AppendLine("Details:");
            builder.AppendLine(connectResult.Error.Details.Trim());
        }

        if (candidateFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Connection candidates:");
            foreach (var attempt in candidateFailures)
            {
                builder.AppendLine($"- {attempt.Candidate}: {FormatCode(attempt.Result.Error?.Code)}; {attempt.Result.Message}");
                if (!string.IsNullOrWhiteSpace(attempt.Result.Error?.Details))
                {
                    builder.AppendLine($"  Details: {Flatten(attempt.Result.Error.Details)}");
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string FormatCode(string? code)
        => string.IsNullOrWhiteSpace(code) ? "<none>" : code;

    private static ConnectionFailureCategory ClassifySettingsHint(string settingsHint)
    {
        if (ContainsAny(settingsHint, "连接地址为空", "Connection address is empty"))
        {
            return ConnectionFailureCategory.AddressEmpty;
        }

        if (ContainsAny(settingsHint, "ADB 路径", "ADB path"))
        {
            return ConnectionFailureCategory.AdbPathInvalid;
        }

        if (ContainsAny(settingsHint, "内置 ADB", "Bundled ADB"))
        {
            return ConnectionFailureCategory.MacBundledAdbUnavailable;
        }

        if (ContainsAny(settingsHint, "模拟器", "emulator", "MuMu", "LDPlayer"))
        {
            return ConnectionFailureCategory.EmulatorPathInvalid;
        }

        return ConnectionFailureCategory.AdbPathInvalid;
    }

    private static string? ExtractCoreReason(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(details);
            var root = doc.RootElement;
            if (root.TryGetProperty("details", out var detailObject)
                && detailObject.ValueKind == JsonValueKind.Object
                && detailObject.TryGetProperty("raw_output", out var rawOutput)
                && rawOutput.ValueKind == JsonValueKind.String)
            {
                return Normalize(rawOutput.GetString());
            }

            if (root.TryGetProperty("why", out var why)
                && why.ValueKind == JsonValueKind.String)
            {
                return Normalize(why.GetString());
            }

            if (root.TryGetProperty("what", out var what)
                && what.ValueKind == JsonValueKind.String)
            {
                return Normalize(what.GetString());
            }
        }
        catch (JsonException)
        {
            return Normalize(details);
        }

        return Normalize(details);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Replace("\\r\\n", Environment.NewLine, StringComparison.Ordinal)
            .Replace("\\n", Environment.NewLine, StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsCode(string code, string expected)
        => string.Equals(code, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool LooksLikeConnectCommandNotConnected(string? value)
        => ContainsAny(
            value,
            "Connection command did not report",
            "did not report \"connected\"",
            "did not report connected");

    private static string Flatten(string value)
        => value.ReplaceLineEndings(" ").Trim();

    private static string Select(string? language, string zh, string en)
        => DialogTextCatalog.UseChinese(language) ? zh : en;
}
