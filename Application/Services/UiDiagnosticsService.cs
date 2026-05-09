using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MAAUnified.Application.Models;
using MAAUnified.Platform;

namespace MAAUnified.Application.Services;

public sealed class UiDiagnosticsService
{
    private const string StartupLogFileName = "avalonia-ui-startup.log";
    private const long MaxBundleEntrySizeBytes = 20L * 1024L * 1024L;
    private const string PerfEventTypeUiThreadLag = "ui_thread_lag";
    private const string PerfEventTypeNavigationTiming = "navigation_timing";
    private const string PerfEventTypeScreenshotTest = "screenshot_test";
    private static readonly TimeSpan DefaultUiLagThrottleInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _performanceEventGate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPerformanceEventAt = new(StringComparer.Ordinal);
    private readonly string _debugDirectory;

    public UiDiagnosticsService(string baseDirectory, UiLogService uiLogService)
    {
        _debugDirectory = Path.Combine(baseDirectory, "debug");
        StartupLogPath = Path.Combine(_debugDirectory, StartupLogFileName);
        ErrorLogPath = Path.Combine(_debugDirectory, "avalonia-ui-errors.log");
        EventLogPath = Path.Combine(_debugDirectory, "avalonia-ui-events.log");
        PlatformEventLogPath = Path.Combine(_debugDirectory, "avalonia-platform-events.log");

        if (global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            uiLogService.LogReceived += log =>
            {
                _ = WriteLineAsync(EventLogPath, $"{log.Timestamp:O} [{log.Level}] {log.Message}");
            };
        }
    }

    public string ErrorLogPath { get; }

    public string EventLogPath { get; }

    public string PlatformEventLogPath { get; }

    public string StartupLogPath { get; }

    public async Task RecordErrorAsync(string scope, string message, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append(" [ERROR] [")
            .Append(scope)
            .Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        await WriteLineAsync(ErrorLogPath, builder.ToString(), cancellationToken);
    }

    public Task RecordFailedResultAsync(string scope, UiOperationResult result, CancellationToken cancellationToken = default)
    {
        var details = result.Error?.Details;
        var code = string.IsNullOrWhiteSpace(result.Error?.Code)
            ? UiErrorCode.UiOperationFailed
            : result.Error!.Code;
        var caseId = code;
        var payload = $"{DateTimeOffset.UtcNow:O} [FAILED] [{scope}] {result.Message} | code={code} | case_id={caseId}";
        if (!string.IsNullOrWhiteSpace(details))
        {
            payload += $" | details={details}";
        }

        return WriteLineAsync(ErrorLogPath, payload, cancellationToken);
    }

    public Task RecordConfigValidationFailureAsync(ConfigValidationIssue? issue, CancellationToken cancellationToken = default)
    {
        var scope = issue?.Scope ?? "ConfigValidation";
        var code = issue?.Code ?? "ConfigValidationBlocked";
        var caseId = code;
        var field = issue?.Field ?? "config";
        var profile = issue?.ProfileName ?? "-";
        var taskIndex = issue?.TaskIndex?.ToString() ?? "-";
        var message = issue?.Message ?? "Execution blocked due to config validation issues.";
        var payload =
            $"{DateTimeOffset.UtcNow:O} [FAILED][{scope}] code={code} case_id={caseId} field={field} profile={profile} taskIndex={taskIndex} message={message}";
        return WriteLineAsync(ErrorLogPath, payload, cancellationToken);
    }

    public Task RecordEventAsync(string scope, string message, CancellationToken cancellationToken = default)
    {
        if (!global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            return Task.CompletedTask;
        }

        return WriteLineAsync(EventLogPath, $"{DateTimeOffset.UtcNow:O} [EVENT] [{scope}] {message}", cancellationToken);
    }

    public Task RecordUiLagAsync(
        string scope,
        double lagMs,
        int thresholdMs,
        int probeIntervalMs,
        TimeSpan? minInterval = null,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?>
        {
            ["thresholdMs"] = thresholdMs,
            ["probeIntervalMs"] = probeIntervalMs,
        };
        return RecordPerformanceEventAsync(
            PerfEventTypeUiThreadLag,
            scope,
            lagMs,
            fields,
            minInterval ?? DefaultUiLagThrottleInterval,
            cancellationToken);
    }

    public Task RecordNavigationTimingAsync(
        string scope,
        string from,
        string to,
        double elapsedMs,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?>
        {
            ["from"] = from,
            ["to"] = to,
        };
        return RecordPerformanceEventAsync(
            PerfEventTypeNavigationTiming,
            scope,
            elapsedMs,
            fields,
            minInterval: null,
            cancellationToken);
    }

    public Task RecordScreenshotTestAsync(
        string scope,
        bool success,
        double elapsedMs,
        string? provider = null,
        string? details = null,
        int? width = null,
        int? height = null,
        TimeSpan? minInterval = null,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?>
        {
            ["success"] = success,
            ["provider"] = provider,
            ["details"] = details,
            ["width"] = width,
            ["height"] = height,
        };
        return RecordPerformanceEventAsync(
            PerfEventTypeScreenshotTest,
            scope,
            elapsedMs,
            fields,
            minInterval,
            cancellationToken);
    }

    public Task RecordPerformanceEventAsync(
        string eventType,
        string scope,
        double elapsedMs,
        IReadOnlyDictionary<string, object?>? fields = null,
        TimeSpan? minInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (!global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(scope))
        {
            return Task.CompletedTask;
        }

        var timestamp = DateTimeOffset.UtcNow;
        if (ShouldSkipPerformanceEvent(eventType, scope, minInterval, timestamp))
        {
            return Task.CompletedTask;
        }

        var payload = new UiPerformanceEventLogLine(
            timestamp,
            eventType.Trim(),
            scope.Trim(),
            Math.Max(0, elapsedMs),
            fields is null || fields.Count == 0 ? null : new Dictionary<string, object?>(fields));
        var line = $"{timestamp:O} [PERF] {JsonSerializer.Serialize(payload)}";
        return WriteLineAsync(EventLogPath, line, cancellationToken);
    }

    public Task RecordTemporaryTimingAsync(
        string scope,
        double elapsedMs,
        IReadOnlyDictionary<string, object?>? fields = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Task.CompletedTask;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var payload = new UiPerformanceEventLogLine(
            timestamp,
            "temporary_timing",
            scope.Trim(),
            Math.Max(0, elapsedMs),
            fields is null || fields.Count == 0 ? null : new Dictionary<string, object?>(fields));
        return WriteLineAsync(EventLogPath, $"{timestamp:O} [TEMP-PERF] {JsonSerializer.Serialize(payload)}", cancellationToken);
    }

    public Task RecordPlatformEventAsync(
        PlatformCapabilityId capability,
        string action,
        PlatformOperationResult result,
        CancellationToken cancellationToken = default)
    {
        if (!global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            return Task.CompletedTask;
        }

        var payload = new PlatformEventLogLine(
            DateTimeOffset.UtcNow,
            capability,
            action,
            result.Success,
            result.ExecutionMode.ToString(),
            result.Provider,
            result.UsedFallback,
            result.ErrorCode,
            result.Message,
            result.OperationId);
        return WriteLineAsync(PlatformEventLogPath, JsonSerializer.Serialize(payload), cancellationToken);
    }

    public Task RecordPlatformEventAsync<T>(
        PlatformCapabilityId capability,
        string action,
        PlatformOperationResult<T> result,
        CancellationToken cancellationToken = default)
    {
        if (!global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            return Task.CompletedTask;
        }

        var payload = new PlatformEventLogLine(
            DateTimeOffset.UtcNow,
            capability,
            action,
            result.Success,
            result.ExecutionMode.ToString(),
            result.Provider,
            result.UsedFallback,
            result.ErrorCode,
            result.Message,
            result.OperationId);
        return WriteLineAsync(PlatformEventLogPath, JsonSerializer.Serialize(payload), cancellationToken);
    }

    public async Task<string> BuildIssueReportBundleAsync(string baseDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_debugDirectory);
        var outputPath = Path.Combine(_debugDirectory, $"issue-report-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        var archivedEntryNames = new HashSet<string>(StringComparer.Ordinal);
        AddFileOrPlaceholder(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "config", "avalonia.json"),
            "config/avalonia.json",
            "avalonia.json not found when bundle was generated.");
        AddFileOrPlaceholder(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "debug", "config-import-report.json"),
            "debug/config-import-report.json",
            "config-import-report.json not found when bundle was generated.");
        AddFileOrPlaceholder(
            archive,
            archivedEntryNames,
            StartupLogPath,
            "debug/avalonia-ui-startup.log",
            "UI startup log is empty or missing.");
        AddFileOrPlaceholder(
            archive,
            archivedEntryNames,
            ErrorLogPath,
            "debug/avalonia-ui-errors.log",
            "UI error log is empty or missing.");
        if (global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            AddFileOrPlaceholder(
                archive,
                archivedEntryNames,
                EventLogPath,
                "debug/avalonia-ui-events.log",
                "UI event log is empty or missing.");
            AddFileOrPlaceholder(
                archive,
                archivedEntryNames,
                PlatformEventLogPath,
                "debug/avalonia-platform-events.log",
                "Platform event log is empty or missing.");
        }

        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "config"),
            "config",
            includeFile: static (_, fileName) => !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "debug"),
            "debug",
            includeFile: static (fullPath, fileName) =>
                !fileName.StartsWith("issue-report-", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-errors.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-startup.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-events.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-platform-events.log", StringComparison.OrdinalIgnoreCase));
        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "cache"),
            "cache",
            includeFile: static (_, _) => true);
        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "resource"),
            "resource",
            includeFile: static (fullPath, fileName) =>
                fileName.Contains("_custom.", StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}custom{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        await RecordEventAsync("IssueReport", $"Support bundle generated: {outputPath}", cancellationToken);
        return outputPath;
    }

    private async Task WriteLineAsync(string path, string line, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void AddFileOrPlaceholder(
        ZipArchive archive,
        ISet<string> archivedEntryNames,
        string filePath,
        string entryName,
        string placeholderMessage)
    {
        if (!archivedEntryNames.Add(entryName))
        {
            return;
        }

        if (File.Exists(filePath))
        {
            archive.CreateEntryFromFile(filePath, entryName);
            return;
        }

        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.WriteLine(placeholderMessage);
    }

    private static void AddDirectoryEntries(
        ZipArchive archive,
        ISet<string> archivedEntryNames,
        string directoryPath,
        string entryRoot,
        Func<string, string, bool> includeFile)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(filePath);
            if (!includeFile(filePath, fileName))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaxBundleEntrySizeBytes)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(directoryPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var entryName = string.IsNullOrWhiteSpace(relativePath)
                ? $"{entryRoot}/{fileName}"
                : $"{entryRoot}/{relativePath}";
            if (!archivedEntryNames.Add(entryName))
            {
                continue;
            }

            archive.CreateEntryFromFile(filePath, entryName);
        }
    }

    private bool ShouldSkipPerformanceEvent(
        string eventType,
        string scope,
        TimeSpan? minInterval,
        DateTimeOffset timestamp)
    {
        if (!minInterval.HasValue || minInterval.Value <= TimeSpan.Zero)
        {
            return false;
        }

        var dedupeKey = $"{eventType}|{scope}";
        lock (_performanceEventGate)
        {
            if (_lastPerformanceEventAt.TryGetValue(dedupeKey, out var previous)
                && timestamp - previous < minInterval.Value)
            {
                return true;
            }

            _lastPerformanceEventAt[dedupeKey] = timestamp;
            return false;
        }
    }

    private sealed record PlatformEventLogLine(
        DateTimeOffset Timestamp,
        PlatformCapabilityId Capability,
        string Action,
        bool Success,
        string ExecutionMode,
        string Provider,
        bool UsedFallback,
        string? ErrorCode,
        string Message,
        string? OperationId);

    private sealed record UiPerformanceEventLogLine(
        DateTimeOffset Timestamp,
        string EventType,
        string Scope,
        double ElapsedMs,
        Dictionary<string, object?>? Fields);
}
