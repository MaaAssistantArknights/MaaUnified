using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.RemoteControl;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;

namespace MAAUnified.Application.Services.Features;

public sealed class RemoteControlFeatureService : IRemoteControlFeatureService, IAsyncDisposable
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly UnifiedConfigurationService? _configService;
    private readonly UnifiedSessionService? _sessionService;
    private readonly IConnectFeatureService? _connectFeatureService;
    private readonly ITaskQueueFeatureService? _taskQueueFeatureService;
    private readonly IToolboxFeatureService? _toolboxFeatureService;
    private readonly IMaaCoreBridge? _coreBridge;
    private readonly UiLogService? _logService;
    private readonly TaskStartGate? _taskStartGate;
    private readonly Func<string, Uri, CancellationToken, Task<EndpointProbeResult>> _probeAsync;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendHttpAsync;
    private readonly HashSet<string?> _seenTaskIds = new(StringComparer.Ordinal);
    private readonly object _pollingGate = new();
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private readonly Queue<(RemoteControlCommandRequest Request, RemoteControlPollingSnapshot Snapshot)> _sequentialQueue = new();
    private readonly SemaphoreSlim _sequentialSignal = new(0);
    private Task? _sequentialTask;

    public RemoteControlFeatureService()
        : this(null, null, null, null, null, null, null)
    {
    }

    internal RemoteControlFeatureService(
        UnifiedConfigurationService? configService,
        UnifiedSessionService? sessionService,
        IConnectFeatureService? connectFeatureService,
        ITaskQueueFeatureService? taskQueueFeatureService,
        IToolboxFeatureService? toolboxFeatureService,
        IMaaCoreBridge? coreBridge,
        UiLogService? logService,
        Func<string, Uri, CancellationToken, Task<EndpointProbeResult>>? probeAsync = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHttpAsync = null)
        : this(
            configService,
            sessionService,
            connectFeatureService,
            taskQueueFeatureService,
            toolboxFeatureService,
            coreBridge,
            logService,
            probeAsync,
            sendHttpAsync,
            taskStartGate: null)
    {
    }

    internal RemoteControlFeatureService(
        UnifiedConfigurationService? configService,
        UnifiedSessionService? sessionService,
        IConnectFeatureService? connectFeatureService,
        ITaskQueueFeatureService? taskQueueFeatureService,
        IToolboxFeatureService? toolboxFeatureService,
        IMaaCoreBridge? coreBridge,
        UiLogService? logService,
        Func<string, Uri, CancellationToken, Task<EndpointProbeResult>>? probeAsync,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHttpAsync,
        TaskStartGate? taskStartGate)
    {
        _configService = configService;
        _sessionService = sessionService;
        _connectFeatureService = connectFeatureService;
        _taskQueueFeatureService = taskQueueFeatureService;
        _toolboxFeatureService = toolboxFeatureService;
        _coreBridge = coreBridge;
        _logService = logService;
        _taskStartGate = taskStartGate;
        _probeAsync = probeAsync ?? ProbeEndpointAsync;
        _sendHttpAsync = sendHttpAsync ?? DefaultSendAsync;
    }

    private static Task<HttpResponseMessage> DefaultSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public Task<CoreResult<bool>> StartRemotePollingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasPollingDependencies)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        lock (_pollingGate)
        {
            if (_pollingTask is not null)
            {
                return Task.FromResult(CoreResult<bool>.Ok(true));
            }

            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollingTask = Task.Run(() => RunPollingLoopAsync(_pollingCts.Token), CancellationToken.None);
            _sequentialTask = Task.Run(() => RunSequentialExecutionLoopAsync(_pollingCts.Token), CancellationToken.None);
        }

        return Task.FromResult(CoreResult<bool>.Ok(true));
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pollingCts;
        Task? pollingTask;
        Task? sequentialTask;
        lock (_pollingGate)
        {
            pollingCts = _pollingCts;
            pollingTask = _pollingTask;
            sequentialTask = _sequentialTask;
            _pollingCts = null;
            _pollingTask = null;
            _sequentialTask = null;
        }

        if (pollingCts is null)
        {
            return;
        }

        try
        {
            pollingCts.Cancel();
            if (pollingTask is not null)
            {
                await pollingTask.ConfigureAwait(false);
            }

            if (sequentialTask is not null)
            {
                await sequentialTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Polling cleanup is best-effort.
        }
        finally
        {
            pollingCts.Dispose();
        }
    }

    public async Task<UiOperationResult<RemoteControlConnectivityResult>> TestConnectivityAsync(
        RemoteControlConnectivityRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanUseSensitiveSettings)
        {
            return UiOperationResult<RemoteControlConnectivityResult>.Fail(
                UiErrorCode.RemoteControlUnsupported,
                "Remote control is disabled while sensitive storage is unavailable.");
        }

        var validationError = ValidateRequest(request, out var getTaskUri, out var reportUri);
        if (validationError is not null)
        {
            return UiOperationResult<RemoteControlConnectivityResult>.Fail(
                UiErrorCode.RemoteControlInvalidParameters,
                validationError);
        }

        var getTaskProbe = NormalizeProbe("GetTask", await _probeAsync("GetTask", getTaskUri!, cancellationToken));
        var reportProbe = NormalizeProbe("Report", await _probeAsync("Report", reportUri!, cancellationToken));
        var result = new RemoteControlConnectivityResult(request.PollIntervalMs, getTaskProbe, reportProbe);

        if (getTaskProbe.Success && reportProbe.Success)
        {
            return UiOperationResult<RemoteControlConnectivityResult>.Ok(
                result,
                $"Remote control connectivity passed. GetTask={getTaskProbe.Message}; Report={reportProbe.Message}");
        }

        var firstFailure = getTaskProbe.Success ? reportProbe : getTaskProbe;
        var errorCode = string.Equals(firstFailure.ErrorCode, UiErrorCode.RemoteControlUnsupported, StringComparison.Ordinal)
            ? UiErrorCode.RemoteControlUnsupported
            : UiErrorCode.RemoteControlNetworkFailure;

        return UiOperationResult<RemoteControlConnectivityResult>.Fail(
            errorCode,
            $"Remote control connectivity failed. GetTask={getTaskProbe.Message}; Report={reportProbe.Message}",
            $"operation={firstFailure.Name}; status={firstFailure.StatusCode?.ToString() ?? "none"}; errorCode={errorCode}");
    }

    private static EndpointProbeResult NormalizeProbe(string operation, EndpointProbeResult probe)
    {
        var message = probe.StatusCode is int statusCode
            ? $"HTTP {statusCode}"
            : probe.Success ? "completed" : "network failure";
        return new EndpointProbeResult(
            operation,
            operation,
            probe.Success,
            probe.StatusCode,
            message,
            probe.ErrorCode);
    }

    private bool HasPollingDependencies =>
        _configService is not null
        && _sessionService is not null
        && _connectFeatureService is not null
        && _taskQueueFeatureService is not null
        && _toolboxFeatureService is not null
        && _coreBridge is not null;

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        _logService?.Info("Remote control polling started.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = ReadSnapshot();

                // WPF parity: wait one poll interval before every fetch, including the first one.
                await DelayAsync(snapshot.PollIntervalMs, cancellationToken);
                if (!snapshot.IsConfigured)
                {
                    continue;
                }

                try
                {
                    var requests = await FetchTasksAsync(snapshot, cancellationToken);
                    if (requests.Count == 0)
                    {
                        continue;
                    }

                    await RouteFetchedCommandsAsync(snapshot, requests, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService?.Warn($"Remote control polling iteration failed: errorType={ex.GetType().Name}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logService?.Error($"Remote control polling crashed: errorType={ex.GetType().Name}.");
        }
        finally
        {
            _logService?.Info("Remote control polling stopped.");
            lock (_pollingGate)
            {
                _pollingTask = null;
                _pollingCts?.Dispose();
                _pollingCts = null;
            }
        }
    }

    private RemoteControlCommandDispatcher CreateDispatcher()
        => new(
            _configService,
            _sessionService,
            _connectFeatureService,
            _taskQueueFeatureService,
            _toolboxFeatureService,
            _coreBridge,
            _logService,
            _taskStartGate);

    // WPF parity: sequential commands are queued to the dedicated execution
    // loop; instant commands execute immediately on the polling loop; unknown
    // types are consumed silently without dispatch or report.
    internal async Task RouteFetchedCommandsAsync(
        RemoteControlPollingSnapshot snapshot,
        IReadOnlyList<RemoteControlCommandRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            switch (RemoteControlCommandDispatcher.Classify(request.RawCommand))
            {
                case RemoteControlCommandKind.Instant:
                    {
                        var dispatcher = CreateDispatcher();
                        var execution = await dispatcher.DispatchAsync(request, cancellationToken);
                        await ReportAsync(snapshot, request, execution, cancellationToken);
                        break;
                    }

                case RemoteControlCommandKind.Sequential:
                    lock (_pollingGate)
                    {
                        _sequentialQueue.Enqueue((request, snapshot));
                    }

                    _sequentialSignal.Release();
                    break;
                default:
                    continue;
            }
        }
    }

    // WPF parity: sequential commands run on a dedicated execution loop so the
    // polling loop (and therefore instant commands such as StopTask) is never
    // blocked by a long-running sequential command.
    private async Task RunSequentialExecutionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _sequentialSignal.WaitAsync(cancellationToken);

                (RemoteControlCommandRequest Request, RemoteControlPollingSnapshot Snapshot) item;
                lock (_pollingGate)
                {
                    if (_sequentialQueue.Count == 0)
                    {
                        continue;
                    }

                    item = _sequentialQueue.Dequeue();
                }

                try
                {
                    var dispatcher = CreateDispatcher();
                    var execution = await dispatcher.DispatchAsync(item.Request, cancellationToken);
                    await ReportAsync(item.Snapshot, item.Request, execution, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService?.Warn($"Remote control sequential execution failed: errorType={ex.GetType().Name}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logService?.Error($"Remote control sequential execution crashed: errorType={ex.GetType().Name}.");
        }
    }

    internal async Task<IReadOnlyList<RemoteControlCommandRequest>> FetchTasksAsync(
        RemoteControlPollingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.GetTaskEndpoint is null)
        {
            return [];
        }

        // WPF wire parity: GetTask is a JSON POST carrying the user/device identities.
        var payload = new JsonObject
        {
            ["user"] = snapshot.UserIdentity,
            ["device"] = snapshot.DeviceIdentity,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, snapshot.GetTaskEndpoint)
        {
            Content = JsonContent.Create(payload),
        };

        using var response = await _sendHttpAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GetTask request failed with HTTP {(int)response.StatusCode}.");
        }

        var payloadText = await SafeReadBodyAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return [];
        }

        return FilterNewCommands(ParseCommandRequests(payloadText, snapshot));
    }

    private List<RemoteControlCommandRequest> FilterNewCommands(List<RemoteControlCommandRequest> requests)
    {
        // WPF parity: task ids that were already enqueued are skipped so a
        // repeated delivery never re-executes a command.
        var fresh = new List<RemoteControlCommandRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (_seenTaskIds.Add(request.TaskId))
            {
                fresh.Add(request);
            }
        }

        return fresh;
    }

    internal async Task ReportAsync(
        RemoteControlPollingSnapshot snapshot,
        RemoteControlCommandRequest commandRequest,
        RemoteControlCommandResult result,
        CancellationToken cancellationToken)
    {
        if (snapshot.ReportEndpoint is null)
        {
            return;
        }

        // WPF wire parity: reports carry only user/device/status/task/payload.
        var legacyPayload = result.ImageBytes is not null
            ? Convert.ToBase64String(result.ImageBytes)
            : result.LegacyReportPayload ?? string.Empty;
        var payload = new JsonObject
        {
            ["user"] = snapshot.UserIdentity,
            ["device"] = snapshot.DeviceIdentity,
            ["status"] = result.Success ? "SUCCESS" : "FAILED",
            ["task"] = commandRequest.TaskId ?? string.Empty,
            ["payload"] = legacyPayload,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, snapshot.ReportEndpoint)
        {
            Content = JsonContent.Create(payload),
        };

        using var response = await _sendHttpAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logService?.Warn($"Remote control report failed: HTTP {(int)response.StatusCode}.");
        }
    }

    internal RemoteControlPollingSnapshot ReadSnapshot()
    {
        if (_configService is null || !CanUseSensitiveSettings)
        {
            return new RemoteControlPollingSnapshot(null, null, Environment.UserName, Environment.MachineName, 1000);
        }

        var config = _configService.CurrentConfig;
        var getTaskEndpoint = ReadUri(config, ConfigurationKeys.RemoteControlGetTaskEndpointUri);
        var reportEndpoint = ReadUri(config, ConfigurationKeys.RemoteControlReportStatusUri);
        var userIdentity = ReadString(config, ConfigurationKeys.RemoteControlUserIdentity, Environment.UserName);
        var deviceIdentity = ReadString(config, ConfigurationKeys.RemoteControlDeviceIdentity, Environment.MachineName);
        // WPF parity: the legacy default poll interval is 1000 ms.
        var pollIntervalMs = ReadInt(config, ConfigurationKeys.RemoteControlPollIntervalMs, 1000);
        return new RemoteControlPollingSnapshot(getTaskEndpoint, reportEndpoint, userIdentity, deviceIdentity, pollIntervalMs);
    }

    private bool CanUseSensitiveSettings => _configService?.SensitiveStorageState
        is not (SensitiveStorageState.Unavailable or SensitiveStorageState.Corrupt);

    private static Uri? ReadUri(UnifiedConfig config, string key)
    {
        var raw = ReadString(config, key, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    private static string ReadString(UnifiedConfig config, string key, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out var node)
            && node is not null)
        {
            var raw = node.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
        }

        return fallback;
    }

    private static int ReadInt(UnifiedConfig config, string key, int fallback)
    {
        if (string.IsNullOrWhiteSpace(config.CurrentProfile)
            || !config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            || !profile.Values.TryGetValue(key, out var node)
            || node is null)
        {
            return fallback;
        }

        return int.TryParse(node.ToString(), out var parsed)
            ? Math.Clamp(parsed, 500, 60000)
            : fallback;
    }

    private static string? ValidateRequest(
        RemoteControlConnectivityRequest request,
        out Uri? getTaskUri,
        out Uri? reportUri)
    {
        getTaskUri = null;
        reportUri = null;
        if (request is null)
        {
            return "Remote control request cannot be null.";
        }

        if (!TryParseHttpUri(request.GetTaskEndpoint, out getTaskUri))
        {
            return "GetTask endpoint is invalid.";
        }

        if (!TryParseHttpUri(request.ReportEndpoint, out reportUri))
        {
            return "Report endpoint is invalid.";
        }

        if (request.PollIntervalMs < 500 || request.PollIntervalMs > 60000)
        {
            return $"Poll interval is out of range: {request.PollIntervalMs}.";
        }

        return null;
    }

    private static bool TryParseHttpUri(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task DelayAsync(int pollIntervalMs, CancellationToken cancellationToken)
    {
        await Task.Delay(Math.Clamp(pollIntervalMs, 500, 60000), cancellationToken);
    }

    internal static List<RemoteControlCommandRequest> ParseCommandRequests(string payloadText, RemoteControlPollingSnapshot snapshot)
    {
        var requests = new List<RemoteControlCommandRequest>();
        try
        {
            var node = JsonNode.Parse(payloadText);
            CollectCommandNodes(node, snapshot, requests);
        }
        catch (JsonException)
        {
            var trimmed = payloadText.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                requests.Add(new RemoteControlCommandRequest(trimmed, null, snapshot.UserIdentity, snapshot.DeviceIdentity));
            }
        }

        return requests;
    }

    private static void CollectCommandNodes(JsonNode? node, RemoteControlPollingSnapshot snapshot, List<RemoteControlCommandRequest> requests)
    {
        if (node is null)
        {
            return;
        }

        // WPF parity: every entry of a batch payload is collected, not just the first one.
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectCommandNodes(item, snapshot, requests);
            }

            return;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? scalar) && !string.IsNullOrWhiteSpace(scalar))
            {
                requests.Add(new RemoteControlCommandRequest(scalar.Trim(), null, snapshot.UserIdentity, snapshot.DeviceIdentity));
            }

            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        if (obj.TryGetPropertyValue("tasks", out var tasksNode) && tasksNode is not null and not JsonValue)
        {
            CollectCommandNodes(tasksNode, snapshot, requests);
            return;
        }

        if (obj.TryGetPropertyValue("task", out var taskNode) && taskNode is not null and not JsonValue)
        {
            CollectCommandNodes(taskNode, snapshot, requests);
            return;
        }

        var command = ExtractString(obj, "command", "cmd", "name", "action", "task", "type");
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        requests.Add(new RemoteControlCommandRequest(
            command.Trim(),
            obj.DeepClone(),
            snapshot.UserIdentity,
            snapshot.DeviceIdentity,
            ResolveTaskId(obj)));
    }

    private static string? ResolveTaskId(JsonObject obj)
    {
        foreach (var key in new[] { "id", "taskId", "task_id", "jobId", "job_id" })
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }

            var raw = value.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
        }

        return null;
    }

    private static string? ExtractString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var child) || child is null)
            {
                continue;
            }

            if (child is JsonValue value && value.TryGetValue(out string? scalar) && !string.IsNullOrWhiteSpace(scalar))
            {
                return scalar.Trim();
            }

            var text = child.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private async Task<EndpointProbeResult> ProbeEndpointAsync(
        string name,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new EndpointProbeResult(name, name, true, statusCode, $"HTTP {statusCode}");
            }

            return new EndpointProbeResult(name, name, false, statusCode, $"HTTP {statusCode}", UiErrorCode.RemoteControlNetworkFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EndpointProbeResult(name, name, false, null, "Request timed out", UiErrorCode.RemoteControlNetworkFailure);
        }
        catch (HttpRequestException)
        {
            return new EndpointProbeResult(name, name, false, null, "Network failure", UiErrorCode.RemoteControlNetworkFailure);
        }
    }
}
