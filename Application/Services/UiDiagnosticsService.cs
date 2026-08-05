using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
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
    private readonly string _baseDirectory;
    private Func<IEnumerable<string>> _sensitiveValueProvider = static () => [];

    public UiDiagnosticsService(string baseDirectory, UiLogService uiLogService)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
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

    public void SetSensitiveValueProvider(Func<IEnumerable<string>> provider)
        => _sensitiveValueProvider = provider ?? throw new ArgumentNullException(nameof(provider));

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

        await WriteLineAsync(ErrorLogPath, builder.ToString(), cancellationToken).ConfigureAwait(false);
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
        if (!DiagnosticFileSecurity.IsSafeDirectoryRoot(_debugDirectory))
        {
            throw new IOException("The diagnostic directory is a link or reparse point.");
        }

        var outputPath = Path.Combine(_debugDirectory, $"issue-report-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        var archivedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveValues = _sensitiveValueProvider()
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AddSanitizedConfigOrPlaceholder(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "config", "avalonia.json"),
            "config/avalonia.json",
            "{\"omitted\":\"config_not_found\"}",
            Path.Combine(baseDirectory, "config"));
        AddRedactedFileOrPlaceholder(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "debug", "config-import-report.json"),
            "debug/config-import-report.json",
            "config-import-report.json not found when bundle was generated.",
            sensitiveValues,
            [_baseDirectory],
            Path.Combine(baseDirectory, "debug"));
        AddRedactedFileOrPlaceholder(
            archive,
            archivedEntryNames,
            StartupLogPath,
            "debug/avalonia-ui-startup.log",
            "UI startup log is empty or missing.",
            sensitiveValues,
            [_baseDirectory],
            Path.Combine(baseDirectory, "debug"));
        AddRedactedFileOrPlaceholder(
            archive,
            archivedEntryNames,
            ErrorLogPath,
            "debug/avalonia-ui-errors.log",
            "UI error log is empty or missing.",
            sensitiveValues,
            [_baseDirectory],
            Path.Combine(baseDirectory, "debug"));
        if (global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            AddRedactedFileOrPlaceholder(
                archive,
                archivedEntryNames,
                EventLogPath,
                "debug/avalonia-ui-events.log",
                "UI event log is empty or missing.",
                sensitiveValues,
                [_baseDirectory],
                Path.Combine(baseDirectory, "debug"));
            AddRedactedFileOrPlaceholder(
                archive,
                archivedEntryNames,
                PlatformEventLogPath,
                "debug/avalonia-platform-events.log",
                "Platform event log is empty or missing.",
                sensitiveValues,
                [_baseDirectory],
                Path.Combine(baseDirectory, "debug"));
        }

        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "debug"),
            "debug",
            includeFile: static (fullPath, fileName) =>
                !fileName.StartsWith("issue-report-", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-errors.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-startup.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-ui-events.log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(fullPath), "avalonia-platform-events.log", StringComparison.OrdinalIgnoreCase),
            sensitiveValues,
            [_baseDirectory]);
        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "cache"),
            "cache",
            includeFile: static (_, _) => true,
            sensitiveValues,
            [_baseDirectory]);
        AddDirectoryEntries(
            archive,
            archivedEntryNames,
            Path.Combine(baseDirectory, "resource"),
            "resource",
            includeFile: static (fullPath, fileName) =>
                fileName.Contains("_custom.", StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}custom{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
            sensitiveValues,
            [_baseDirectory]);

        await RecordEventAsync("IssueReport", "Support bundle generated.", cancellationToken);
        return outputPath;
    }

    private async Task WriteLineAsync(string path, string line, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var redacted = DiagnosticDataSanitizer.Sanitize(line, _sensitiveValueProvider(), [_baseDirectory]);
            await File.AppendAllTextAsync(path, redacted + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void AddSanitizedConfigOrPlaceholder(
        ZipArchive archive,
        ISet<string> archivedEntryNames,
        string filePath,
        string entryName,
        string placeholderMessage,
        string trustedRoot)
    {
        if (!archivedEntryNames.Add(entryName))
        {
            return;
        }

        string content;
        if (!File.Exists(filePath))
        {
            content = placeholderMessage + Environment.NewLine;
        }
        else if (!DiagnosticFileSecurity.TryOpenRegularFile(trustedRoot, filePath, out var input))
        {
            return;
        }
        else
        {
            try
            {
                using (input)
                {
                    var config = JsonSerializer.Deserialize<UnifiedConfig>(
                        input,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new JsonException("Config was null.");
                    _ = SensitiveSettingKeyRegistry.ExtractAndRemove(config, "diagnostic-redaction", 1);
                    var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    content = SanitizeJsonTextOrPlaceholder(json, _sensitiveValueProvider(), [_baseDirectory]);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                content = $"{{\"omitted\":\"config_sanitization_failed\",\"errorType\":\"{ex.GetType().Name}\"}}{Environment.NewLine}";
            }
        }

        var entry = archive.CreateEntry(entryName);
        using var output = entry.Open();
        using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
    }

    private static void AddRedactedFileOrPlaceholder(
        ZipArchive archive,
        ISet<string> archivedEntryNames,
        string filePath,
        string entryName,
        string placeholderMessage,
        IEnumerable<string> sensitiveValues,
        IEnumerable<string> privateRoots,
        string trustedRoot)
    {
        if (!archivedEntryNames.Add(entryName))
        {
            return;
        }

        string content;
        if (!File.Exists(filePath))
        {
            content = string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase)
                ? "{\"omitted\":\"diagnostic_not_found\"}"
                : placeholderMessage;
        }
        else if (!DiagnosticFileSecurity.TryOpenRegularFile(trustedRoot, filePath, out var input))
        {
            return;
        }
        else
        {
            using (input)
            using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                content = reader.ReadToEnd();
            }
        }

        if (string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            content = SanitizeJsonTextOrPlaceholder(content, sensitiveValues, privateRoots);
        }
        else
        {
            content = DiagnosticDataSanitizer.Sanitize(content, sensitiveValues, privateRoots);
        }

        var entry = archive.CreateEntry(entryName);
        using var output = entry.Open();
        using var writer = new StreamWriter(output, Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddDirectoryEntries(
        ZipArchive archive,
        ISet<string> archivedEntryNames,
        string directoryPath,
        string entryRoot,
        Func<string, string, bool> includeFile,
        IReadOnlyList<string> sensitiveValues,
        IReadOnlyList<string> privateRoots)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var filePath in DiagnosticFileSecurity.EnumerateRegularFiles(directoryPath))
        {
            var fileName = Path.GetFileName(filePath);
            if (!includeFile(filePath, fileName))
            {
                continue;
            }

            if (!IsTextDiagnosticFile(filePath))
            {
                continue;
            }

            if (!DiagnosticFileSecurity.TryOpenRegularFile(directoryPath, filePath, out var input))
            {
                continue;
            }

            string content;
            using (input)
            {
                if (input.Length <= 0 || input.Length > MaxBundleEntrySizeBytes)
                {
                    continue;
                }

                using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                content = reader.ReadToEnd();
            }

            if (content.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            if (IsJsonDiagnosticFile(filePath))
            {
                // Parse JSON before scanning for secrets so numeric values such as SMTP port 25
                // retain their JSON type instead of being mistaken for string secrets.
                if (!TrySanitizeJsonText(content, sensitiveValues, privateRoots, out var sanitizedJson))
                {
                    continue;
                }

                content = sanitizedJson;
            }
            else
            {
                content = DiagnosticDataSanitizer.Sanitize(content, sensitiveValues, privateRoots);
            }

            var relativePath = Path.GetRelativePath(directoryPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var proposedEntryName = string.IsNullOrWhiteSpace(relativePath)
                ? $"{entryRoot}/{fileName}"
                : $"{entryRoot}/{relativePath}";
            var entryName = ReserveUniqueArchiveEntryName(
                archivedEntryNames,
                SanitizeArchiveEntryName(proposedEntryName, sensitiveValues, privateRoots));

            var entry = archive.CreateEntry(entryName);
            using var output = entry.Open();
            using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(content);
            writer.Flush();
        }
    }

    private static string SanitizeArchiveEntryName(
        string entryName,
        IEnumerable<string> sensitiveValues,
        IEnumerable<string> privateRoots)
    {
        var redacted = SensitiveDataRedactor.Redact(entryName.Replace('\\', '/'), sensitiveValues);
        var segments = redacted.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safeSegments = new List<string>(segments.Length);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = DiagnosticDataSanitizer.Sanitize(segments[index], sensitiveValues, privateRoots);
            if (index > 0
                && (string.Equals(segments[index - 1], "home", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segments[index - 1], "Users", StringComparison.OrdinalIgnoreCase)))
            {
                segment = DiagnosticDataSanitizer.UserHomeReplacement;
            }

            safeSegments.Add(SanitizeArchivePathSegment(segment));
        }

        return safeSegments.Count == 0 ? "diagnostic" : string.Join('/', safeSegments);
    }

    internal static string SanitizeArchivePathSegment(string segment)
    {
        var sanitized = new StringBuilder(segment.Length);
        foreach (var value in segment)
        {
            sanitized.Append(char.IsControl(value) || value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : value);
        }

        var result = sanitized.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(result) || result is "." or "..")
        {
            return "_";
        }

        var firstExtensionSeparator = result.IndexOf('.');
        var deviceBaseName = (firstExtensionSeparator >= 0 ? result[..firstExtensionSeparator] : result)
            .TrimEnd(' ', '.');
        if (deviceBaseName.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
        {
            result = $"_{result}";
        }

        return result;
    }

    private static string ReserveUniqueArchiveEntryName(ISet<string> archivedEntryNames, string entryName)
    {
        if (archivedEntryNames.Add(entryName))
        {
            return entryName;
        }

        var separatorIndex = entryName.LastIndexOf('/');
        var directory = separatorIndex >= 0 ? entryName[..(separatorIndex + 1)] : string.Empty;
        var fileName = separatorIndex >= 0 ? entryName[(separatorIndex + 1)..] : entryName;
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{directory}{stem}~{suffix}{extension}";
            if (archivedEntryNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsJsonDiagnosticFile(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeJsonTextOrPlaceholder(
        string text,
        IEnumerable<string> sensitiveValues,
        IEnumerable<string> privateRoots)
        => TrySanitizeJsonText(text, sensitiveValues, privateRoots, out var sanitized)
            ? sanitized
            : "{\"omitted\":\"invalid_or_unsafe_json\"}";

    private static bool TrySanitizeJsonText(
        string text,
        IEnumerable<string> sensitiveValues,
        IEnumerable<string> privateRoots,
        out string sanitized)
    {
        sanitized = string.Empty;
        try
        {
            var node = JsonNode.Parse(text);
            if (node is null)
            {
                return false;
            }

            node = SanitizeJsonNode(node, sensitiveValues, privateRoots);
            sanitized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode SanitizeJsonNode(
        JsonNode node,
        IEnumerable<string> sensitiveValues,
        IEnumerable<string> privateRoots)
    {
        if (node is JsonObject obj)
        {
            var properties = obj.ToArray();
            obj.Clear();
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                var sanitizedName = DiagnosticDataSanitizer.Sanitize(property.Key, sensitiveValues, privateRoots);
                sanitizedName = ReserveUniqueJsonPropertyName(propertyNames, sanitizedName);
                obj[sanitizedName] = property.Value is null
                    ? null
                    : SanitizeJsonNode(property.Value, sensitiveValues, privateRoots);
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                if (child is not null)
                {
                    var sanitizedChild = SanitizeJsonNode(child, sensitiveValues, privateRoots);
                    if (!ReferenceEquals(child, sanitizedChild))
                    {
                        array[i] = sanitizedChild;
                    }
                }
            }

            return array;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(DiagnosticDataSanitizer.Sanitize(text, sensitiveValues, privateRoots));
        }

        return node;
    }

    private static string ReserveUniqueJsonPropertyName(ISet<string> propertyNames, string propertyName)
    {
        if (propertyNames.Add(propertyName))
        {
            return propertyName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{propertyName}~{suffix}";
            if (propertyNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsTextDiagnosticFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".json" or ".log" or ".xml" or ".yaml" or ".yml" or ".csv" or ".ini" or ".cfg";

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
