using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services.WebApi;

namespace MAAUnified.Application.Services.Features;

public sealed class WebApiFeatureService : IWebApiFeatureService
{
    private const string WebApiRunOwner = "WebApi";
    private const int ApiSuccessCode = 10000;
    private const int ApiInvalidParamsCode = 10001;
    private const int ApiFailureCode = -1;

    private readonly UnifiedConfigurationService? _configService;
    private readonly UnifiedSessionService? _sessionService;
    private readonly IConnectFeatureService? _connectFeatureService;
    private readonly IAppLifecycleService? _appLifecycleService;
    private readonly WebApiTaskStore _taskStore = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApiServer? _server;
    private WebApiConfig _runningConfig = WebApiConfig.Default;

    public WebApiFeatureService()
    {
    }

    public WebApiFeatureService(UnifiedConfigurationService configService)
        : this(configService, null, null, null)
    {
    }

    public WebApiFeatureService(
        UnifiedConfigurationService configService,
        UnifiedSessionService? sessionService,
        IConnectFeatureService? connectFeatureService,
        IAppLifecycleService? appLifecycleService)
    {
        _configService = configService;
        _sessionService = sessionService;
        _connectFeatureService = connectFeatureService;
        _appLifecycleService = appLifecycleService;

        if (_sessionService is not null)
        {
            _sessionService.SessionStateChanged += HandleSessionStateChanged;
        }
    }

    public Task<UiOperationResult<WebApiConfig>> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return Task.FromResult(UiOperationResult<WebApiConfig>.Fail(
                UiErrorCode.WebApiServiceUnavailable,
                "WebApi service is not initialized."));
        }

        var config = _configService.CurrentConfig;
        var loaded = new WebApiConfig(
            Enabled: ReadBool(config, "Advanced.WebApi.Enabled", false),
            Host: ReadString(config, "Advanced.WebApi.Host", "127.0.0.1"),
            Port: ReadInt(config, "Advanced.WebApi.Port", 51888),
            AccessToken: ReadString(config, "Advanced.WebApi.AccessToken", string.Empty));
        return Task.FromResult(UiOperationResult<WebApiConfig>.Ok(loaded, "Loaded WebApi config."));
    }

    public async Task<UiOperationResult> SaveConfigAsync(WebApiConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.WebApiServiceUnavailable, "WebApi service is not initialized.");
        }

        var validation = ValidateConfig(config);
        if (!validation.Success)
        {
            return validation;
        }

        foreach (var (key, value) in config.ToGlobalSettingUpdates())
        {
            _configService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        }

        await _configService.SaveAsync(cancellationToken).ConfigureAwait(false);
        return UiOperationResult.Ok("WebApi config saved.");
    }

    public Task<UiOperationResult<bool>> GetRunningStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var running = _server is not null;
        return Task.FromResult(UiOperationResult<bool>.Ok(running, running ? "WebApi is running." : "WebApi is stopped."));
    }

    public async Task<UiOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configResult = await LoadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!configResult.Success || configResult.Value is null)
        {
            return UiOperationResult.Fail(
                configResult.Error?.Code ?? UiErrorCode.WebApiLoadFailed,
                configResult.Message,
                configResult.Error?.Details);
        }

        var config = configResult.Value;
        var validation = ValidateConfig(config);
        if (!validation.Success)
        {
            return validation;
        }

        if (!config.Enabled)
        {
            return UiOperationResult.Fail(UiErrorCode.WebApiDisabled, "WebApi is disabled by configuration.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server is not null)
            {
                return UiOperationResult.Ok($"WebApi already running at {config.Host}:{config.Port}.");
            }

            if (!IsPortAvailable(config.Host, config.Port))
            {
                return UiOperationResult.Fail(
                    UiErrorCode.WebApiPortConflict,
                    $"WebApi port is occupied: {config.Host}:{config.Port}");
            }

            try
            {
                _runningConfig = config;
                _server = new WebApiServer(config.Host, config.Port, HandleRequestAsync);
                _server.Start();
                return UiOperationResult.Ok($"WebApi started at {config.Host}:{config.Port}.");
            }
            catch (Exception ex)
            {
                _server = null;
                return UiOperationResult.Fail(
                    UiErrorCode.WebApiServiceUnavailable,
                    $"Failed to start WebApi server: {ex.Message}");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server is null)
            {
                return UiOperationResult.Ok("WebApi already stopped.");
            }

            var server = _server;
            _server = null;
            await server.DisposeAsync().ConfigureAwait(false);
            return UiOperationResult.Ok("WebApi stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void HandleSessionStateChanged(SessionState state)
    {
        if (_sessionService is null)
        {
            return;
        }

        if (state is SessionState.Running or SessionState.Stopping)
        {
            return;
        }

        _sessionService.EndRun(WebApiRunOwner);
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.Unauthorized,
                    BuildFailurePayload(ApiFailureCode, "Unauthorized."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var method = context.Request.HttpMethod?.ToUpperInvariant() ?? "GET";
            var path = NormalizePath(context.Request.Url?.AbsolutePath);

            switch ((method, path))
            {
                case ("POST", "/task/append"):
                    await HandleAppendAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("POST", "/task/modify"):
                    await HandleModifyAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("GET", "/task/list"):
                    await HandleListAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("POST", "/task/start"):
                    await HandleStartAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("POST", "/task/stop"):
                    await HandleStopTasksAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("GET", "/task/running"):
                    await HandleRunningAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("GET", "/version"):
                    await HandleVersionAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case ("POST", "/destroy"):
                    await HandleDestroyAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    await WriteJsonAsync(
                        context.Response,
                        HttpStatusCode.NotFound,
                        BuildFailurePayload(ApiFailureCode, $"Unsupported route: {method} {path}"),
                        cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener is stopping.
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.InternalServerError,
                    BuildFailurePayload(ApiFailureCode, ex.Message),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    private async Task HandleAppendAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiInvalidParamsCode, "Invalid JSON payload."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var type = ReadJsonString(request, "type");
        var parameters = ReadParamsObject(request, "params");
        if (string.IsNullOrWhiteSpace(type) || parameters is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiInvalidParamsCode, "Task type and params are required."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var name = ReadJsonString(request, "name");
        var enabled = ReadJsonBool(request, "enabled") ?? true;
        var stored = _taskStore.Append(new WebApiTaskDefinition(
            0,
            type,
            string.IsNullOrWhiteSpace(name) ? type : name,
            parameters,
            enabled));

        await WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject
            {
                ["task_id"] = stored.Id,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleModifyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiInvalidParamsCode, "Invalid JSON payload."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var taskId = ReadJsonInt(request, "task_id");
        if (taskId is null || taskId <= 0 || !_taskStore.TryGet(taskId.Value, out var existing) || existing is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, "Task id was not found."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = existing with
        {
            TaskType = ReadJsonString(request, "type") ?? existing.TaskType,
            Name = ReadJsonString(request, "name") ?? existing.Name,
            Parameters = ReadParamsObject(request, "params") ?? existing.Parameters,
            Enabled = ReadJsonBool(request, "enabled") ?? existing.Enabled,
        };

        _taskStore.TryModify(taskId.Value, updated);
        await WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject
            {
                ["status"] = true,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private Task HandleListAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var tasks = new JsonArray(
            _taskStore.List()
                .Select(task => (JsonNode)new JsonObject
                {
                    ["task_id"] = task.Id,
                    ["task_type"] = task.TaskType,
                    ["task_name"] = task.Name,
                    ["task_params"] = task.Parameters?.ToJsonString() ?? "{}",
                    ["enabled"] = task.Enabled,
                })
                .ToArray());

        return WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject
            {
                ["tasks"] = tasks,
            }),
            cancellationToken);
    }

    private async Task HandleStartAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (_sessionService is null || _connectFeatureService is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, "WebApi execution services are unavailable."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_taskStore.IsEmpty)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiInvalidParamsCode, "No WebApi tasks were queued."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_sessionService.TryBeginRun(WebApiRunOwner, out var currentOwner))
        {
            var owner = string.IsNullOrWhiteSpace(currentOwner) ? "Unknown" : currentOwner;
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, $"Another run owner is active: {owner}."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var keepOwner = false;
        try
        {
            var appendResult = await _sessionService.AppendCoreTasksAsync(
                _taskStore.List().Select(static task => task.ToCoreTaskRequest()),
                cancellationToken).ConfigureAwait(false);
            if (!appendResult.Success)
            {
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.OK,
                    BuildFailurePayload(ApiFailureCode, appendResult.Error?.Message ?? "Failed to append WebApi tasks."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var startResult = await _connectFeatureService.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!startResult.Success)
            {
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.OK,
                    BuildFailurePayload(ApiFailureCode, startResult.Message),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            keepOwner = true;
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildSuccessPayload(new JsonObject
                {
                    ["status"] = true,
                    ["appended"] = appendResult.Value,
                }),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!keepOwner)
            {
                _sessionService.EndRun(WebApiRunOwner);
            }
        }
    }

    private async Task HandleStopTasksAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (_connectFeatureService is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, "WebApi execution services are unavailable."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _connectFeatureService.StopAsync(cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _taskStore.Clear();
            _sessionService?.EndRun(WebApiRunOwner);
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildSuccessPayload(new JsonObject
                {
                    ["status"] = true,
                }),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildFailurePayload(ApiFailureCode, result.Message),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRunningAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var running = false;
        if (_sessionService is not null)
        {
            var runtimeStatus = await _sessionService.GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
            running = runtimeStatus.Success
                ? runtimeStatus.Value?.Running == true
                : _sessionService.CurrentState == SessionState.Running;
        }

        await WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject
            {
                ["status"] = running,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private Task HandleVersionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var version = typeof(WebApiFeatureService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0];
        version ??= typeof(WebApiFeatureService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject
            {
                ["data"] = version,
            }),
            cancellationToken);
    }

    private async Task HandleDestroyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (_connectFeatureService is not null)
        {
            _ = await _connectFeatureService.StopAsync(cancellationToken).ConfigureAwait(false);
            _sessionService?.EndRun(WebApiRunOwner);
        }

        _taskStore.Clear();
        var stopResult = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stopResult.Success)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, stopResult.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_appLifecycleService is null)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, "Application lifecycle service is unavailable."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var exitResult = await _appLifecycleService.ExitAsync(cancellationToken).ConfigureAwait(false);
        if (!exitResult.Success)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.OK,
                BuildFailurePayload(ApiFailureCode, exitResult.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            context.Response,
            HttpStatusCode.OK,
            BuildSuccessPayload(new JsonObject()),
            cancellationToken).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var configuredToken = _runningConfig.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return true;
        }

        var bearer = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(bearer)
            && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(bearer["Bearer ".Length..].Trim(), configuredToken, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(request.Headers["X-Access-Token"]?.Trim(), configuredToken, StringComparison.Ordinal);
    }

    private static async Task<JsonObject?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return new JsonObject();
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text) as JsonObject;
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.LongLength;
        await using var output = response.OutputStream;
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject BuildSuccessPayload(JsonObject? data)
    {
        return new JsonObject
        {
            ["code"] = ApiSuccessCode,
            ["msg"] = "ok",
            ["data"] = data ?? new JsonObject(),
        };
    }

    private static JsonObject BuildFailurePayload(int code, string message)
    {
        return new JsonObject
        {
            ["code"] = code,
            ["msg"] = message,
        };
    }

    private static string NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        var normalized = rawPath.Trim();
        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
    }

    private static string? ReadJsonString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int? ReadJsonInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out string? text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadJsonBool(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue(out string? text)
                && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static JsonObject? ReadParamsObject(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonObject jsonObject)
        {
            return jsonObject.DeepClone() as JsonObject ?? new JsonObject();
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonNode.Parse(text) as JsonObject;
        }

        return null;
    }

    private static UiOperationResult ValidateConfig(WebApiConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Host))
        {
            return UiOperationResult.Fail(UiErrorCode.WebApiPortConflict, "WebApi host cannot be empty.");
        }

        if (config.Port < 1 || config.Port > 65535)
        {
            return UiOperationResult.Fail(UiErrorCode.WebApiPortConflict, $"WebApi port out of range: {config.Port}");
        }

        return UiOperationResult.Ok("WebApi config is valid.");
    }

    private static bool IsPortAvailable(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var ipAddress))
        {
            ipAddress = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback
                : IPAddress.Any;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(ipAddress, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static string ReadString(UnifiedConfig config, string key, string fallback)
    {
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            var text = node.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return fallback;
    }

    private static bool ReadBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (bool.TryParse(node.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static int ReadInt(UnifiedConfig config, string key, int fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
