using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
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
    private readonly Func<string, Uri, CancellationToken, Task<EndpointProbeResult>> _probeAsync;
    private readonly object _pollingGate = new();
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

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
        Func<string, Uri, CancellationToken, Task<EndpointProbeResult>>? probeAsync = null)
    {
        _configService = configService;
        _sessionService = sessionService;
        _connectFeatureService = connectFeatureService;
        _taskQueueFeatureService = taskQueueFeatureService;
        _toolboxFeatureService = toolboxFeatureService;
        _coreBridge = coreBridge;
        _logService = logService;
        _probeAsync = probeAsync ?? ProbeEndpointAsync;
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
        }

        return Task.FromResult(CoreResult<bool>.Ok(true));
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pollingCts;
        Task? pollingTask;
        lock (_pollingGate)
        {
            pollingCts = _pollingCts;
            pollingTask = _pollingTask;
            _pollingCts = null;
            _pollingTask = null;
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

        var validationError = ValidateRequest(request, out var getTaskUri, out var reportUri);
        if (validationError is not null)
        {
            return UiOperationResult<RemoteControlConnectivityResult>.Fail(
                UiErrorCode.RemoteControlInvalidParameters,
                validationError);
        }

        var getTaskProbe = await _probeAsync("GetTask", getTaskUri!, cancellationToken);
        var reportProbe = await _probeAsync("Report", reportUri!, cancellationToken);
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
            JsonSerializer.Serialize(result));
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
                if (!snapshot.IsConfigured)
                {
                    await DelayAsync(snapshot.PollIntervalMs, cancellationToken);
                    continue;
                }

                try
                {
                    var request = await FetchTaskAsync(snapshot, cancellationToken);
                    if (request is null)
                    {
                        await DelayAsync(snapshot.PollIntervalMs, cancellationToken);
                        continue;
                    }

                    var dispatcher = new RemoteControlCommandDispatcher(
                        _configService,
                        _sessionService,
                        _connectFeatureService,
                        _taskQueueFeatureService,
                        _toolboxFeatureService,
                        _coreBridge,
                        _logService);
                    var execution = await dispatcher.DispatchAsync(request, cancellationToken);
                    await ReportAsync(snapshot, execution, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService?.Warn($"Remote control polling iteration failed: {ex.Message}");
                }

                await DelayAsync(snapshot.PollIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logService?.Error($"Remote control polling crashed: {ex.Message}");
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

    private async Task<RemoteControlCommandRequest?> FetchTaskAsync(
        RemoteControlPollingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.GetTaskEndpoint is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(snapshot.GetTaskEndpoint, snapshot.UserIdentity, snapshot.DeviceIdentity));
        request.Headers.TryAddWithoutValidation("X-User-Identity", snapshot.UserIdentity);
        request.Headers.TryAddWithoutValidation("X-Device-Identity", snapshot.DeviceIdentity);

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken);
            throw new HttpRequestException($"GetTask request failed with HTTP {(int)response.StatusCode}: {body}");
        }

        var payloadText = await SafeReadBodyAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return null;
        }

        return ParseCommandRequest(payloadText, snapshot);
    }

    private async Task ReportAsync(
        RemoteControlPollingSnapshot snapshot,
        RemoteControlCommandResult result,
        CancellationToken cancellationToken)
    {
        if (snapshot.ReportEndpoint is null)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["command"] = result.RawCommand,
            ["normalizedCommand"] = result.NormalizedCommand,
            ["success"] = result.Success,
            ["message"] = result.Message,
            ["errorCode"] = result.ErrorCode,
            ["details"] = result.Details,
            ["taskType"] = result.TaskType,
            ["coreTaskId"] = result.CoreTaskId,
            ["userIdentity"] = snapshot.UserIdentity,
            ["deviceIdentity"] = snapshot.DeviceIdentity,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["imageContentType"] = result.ImageContentType,
        };

        if (result.ImageBytes is not null)
        {
            payload["imageBytesBase64"] = JsonValue.Create(Convert.ToBase64String(result.ImageBytes));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, snapshot.ReportEndpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-User-Identity", snapshot.UserIdentity);
        request.Headers.TryAddWithoutValidation("X-Device-Identity", snapshot.DeviceIdentity);

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken);
            _logService?.Warn($"Remote control report failed: HTTP {(int)response.StatusCode}; {body}");
        }
    }

    private RemoteControlPollingSnapshot ReadSnapshot()
    {
        if (_configService is null)
        {
            return new RemoteControlPollingSnapshot(null, null, Environment.UserName, Environment.MachineName, 5000);
        }

        var config = _configService.CurrentConfig;
        var getTaskEndpoint = ReadUri(config, ConfigurationKeys.RemoteControlGetTaskEndpointUri);
        var reportEndpoint = ReadUri(config, ConfigurationKeys.RemoteControlReportStatusUri);
        var userIdentity = ReadString(config, ConfigurationKeys.RemoteControlUserIdentity, Environment.UserName);
        var deviceIdentity = ReadString(config, ConfigurationKeys.RemoteControlDeviceIdentity, Environment.MachineName);
        var pollIntervalMs = ReadInt(config, ConfigurationKeys.RemoteControlPollIntervalMs, 5000);
        return new RemoteControlPollingSnapshot(getTaskEndpoint, reportEndpoint, userIdentity, deviceIdentity, pollIntervalMs);
    }

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
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
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
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
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
            return $"GetTask endpoint is invalid: `{request.GetTaskEndpoint}`";
        }

        if (!TryParseHttpUri(request.ReportEndpoint, out reportUri))
        {
            return $"Report endpoint is invalid: `{request.ReportEndpoint}`";
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

    private static Uri BuildRequestUri(Uri baseUri, string userIdentity, string deviceIdentity)
    {
        var builder = new UriBuilder(baseUri);
        var query = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
        query += $"userIdentity={Uri.EscapeDataString(userIdentity)}&deviceIdentity={Uri.EscapeDataString(deviceIdentity)}";
        builder.Query = query;
        return builder.Uri;
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

    private static RemoteControlCommandRequest? ParseCommandRequest(string payloadText, RemoteControlPollingSnapshot snapshot)
    {
        try
        {
            var node = JsonNode.Parse(payloadText);
            return ParseCommandNode(node, snapshot);
        }
        catch (JsonException)
        {
            return new RemoteControlCommandRequest(payloadText.Trim(), null, snapshot.UserIdentity, snapshot.DeviceIdentity);
        }
    }

    private static RemoteControlCommandRequest? ParseCommandNode(JsonNode? node, RemoteControlPollingSnapshot snapshot)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var parsed = ParseCommandNode(item, snapshot);
                if (parsed is not null)
                {
                    return parsed;
                }
            }

            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? scalar))
        {
            return string.IsNullOrWhiteSpace(scalar)
                ? null
                : new RemoteControlCommandRequest(scalar.Trim(), null, snapshot.UserIdentity, snapshot.DeviceIdentity);
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj.TryGetPropertyValue("tasks", out var tasksNode) && tasksNode is not null)
        {
            var fromTasks = ParseCommandNode(tasksNode, snapshot);
            if (fromTasks is not null)
            {
                return fromTasks;
            }
        }

        if (obj.TryGetPropertyValue("task", out var taskNode) && taskNode is not null)
        {
            var fromTask = ParseCommandNode(taskNode, snapshot);
            if (fromTask is not null)
            {
                return fromTask;
            }
        }

        var command = ExtractString(obj, "command", "cmd", "name", "action", "task", "type");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return new RemoteControlCommandRequest(
            command.Trim(),
            obj.DeepClone(),
            snapshot.UserIdentity,
            snapshot.DeviceIdentity);
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
                return new EndpointProbeResult(name, endpoint.ToString(), true, statusCode, $"HTTP {statusCode}");
            }

            return new EndpointProbeResult(name, endpoint.ToString(), false, statusCode, $"HTTP {statusCode}", UiErrorCode.RemoteControlNetworkFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EndpointProbeResult(name, endpoint.ToString(), false, null, "Request timed out", UiErrorCode.RemoteControlNetworkFailure);
        }
        catch (HttpRequestException ex)
        {
            return new EndpointProbeResult(name, endpoint.ToString(), false, null, $"Network error: {ex.Message}", UiErrorCode.RemoteControlNetworkFailure);
        }
    }
}
