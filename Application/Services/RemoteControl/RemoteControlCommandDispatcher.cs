using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Application.Services.RemoteControl;

internal sealed class RemoteControlCommandDispatcher
{
    private static readonly object RunningTaskGate = new();
    private static string _runningTaskId = string.Empty;

    private readonly UnifiedConfigurationService? _configService;
    private readonly UnifiedSessionService? _sessionService;
    private readonly IConnectFeatureService? _connectFeatureService;
    private readonly ITaskQueueFeatureService? _taskQueueFeatureService;
    private readonly IToolboxFeatureService? _toolboxFeatureService;
    private readonly IMaaCoreBridge? _coreBridge;
    private readonly UiLogService? _logService;

    public RemoteControlCommandDispatcher(
        UnifiedConfigurationService? configService,
        UnifiedSessionService? sessionService,
        IConnectFeatureService? connectFeatureService,
        ITaskQueueFeatureService? taskQueueFeatureService,
        IToolboxFeatureService? toolboxFeatureService,
        IMaaCoreBridge? coreBridge,
        UiLogService? logService)
    {
        _configService = configService;
        _sessionService = sessionService;
        _connectFeatureService = connectFeatureService;
        _taskQueueFeatureService = taskQueueFeatureService;
        _toolboxFeatureService = toolboxFeatureService;
        _coreBridge = coreBridge;
        _logService = logService;
    }

    public async Task<RemoteControlCommandResult> DispatchAsync(
        RemoteControlCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var command = NormalizeCommand(request.RawCommand);
        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail(
                request,
                string.Empty,
                UiErrorCode.RemoteControlInvalidParameters,
                "Remote control command is empty.");
        }

        var commandId = ResolveTaskId(request.Payload);
        try
        {
            return command switch
            {
                "LinkStart" => await DispatchLinkStartAsync(request, command, commandId, null, cancellationToken),
                "LinkStart-Base" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Infrast },
                    cancellationToken),
                "LinkStart-WakeUp" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.StartUp },
                    cancellationToken),
                "LinkStart-Combat" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Fight },
                    cancellationToken),
                "LinkStart-Recruiting" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Recruit },
                    cancellationToken),
                "LinkStart-Mall" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Mall },
                    cancellationToken),
                "LinkStart-Mission" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Award },
                    cancellationToken),
                "LinkStart-AutoRoguelike" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Roguelike },
                    cancellationToken),
                "LinkStart-Reclamation" => await DispatchLinkStartAsync(
                    request,
                    command,
                    commandId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskModuleTypes.Reclamation },
                    cancellationToken),
                "Toolbox-GachaOnce" => await DispatchGachaAsync(request, command, commandId, once: true, cancellationToken),
                "Toolbox-GachaTenTimes" => await DispatchGachaAsync(request, command, commandId, once: false, cancellationToken),
                "CaptureImage" => await DispatchCaptureImageAsync(request, command, cancellationToken),
                "CaptureImageNow" => await DispatchCaptureImageAsync(request, command, cancellationToken),
                "HeartBeat" => DispatchHeartBeat(request, command),
                "StopTask" => await DispatchStopTaskAsync(request, command, cancellationToken),
                "Settings-ConnectAddress" => await DispatchConnectAddressAsync(request, command, cancellationToken),
                "Settings-Stage1" => await DispatchStage1Async(request, command, cancellationToken),
                _ => Fail(
                    request,
                    command,
                    UiErrorCode.RemoteControlUnsupported,
                    $"Remote control command `{command}` is unsupported in unified.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService?.Warn($"Remote control command `{command}` failed with exception: {ex.Message}");
            return Fail(
                request,
                command,
                UiErrorCode.UiOperationFailed,
                $"Remote control command `{command}` crashed: {ex.Message}");
        }
    }

    private async Task<RemoteControlCommandResult> DispatchLinkStartAsync(
        RemoteControlCommandRequest request,
        string command,
        string? commandId,
        HashSet<string>? selectedTaskTypes,
        CancellationToken cancellationToken)
    {
        if (_taskQueueFeatureService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Task queue service is unavailable.");
        }

        var connected = await EnsureConnectedAsync(cancellationToken);
        if (!connected.Success)
        {
            return Fail(request, command, connected.Error?.Code ?? UiErrorCode.RemoteControlUnsupported, connected.Message, connected.Error?.Details);
        }

        CoreResult<int> appendResult;
        if (selectedTaskTypes is null)
        {
            appendResult = await _taskQueueFeatureService.QueueEnabledTasksAsync(cancellationToken);
        }
        else
        {
            appendResult = await QueueSelectedTaskTypesAsync(selectedTaskTypes, cancellationToken);
        }

        if (!appendResult.Success)
        {
            return Fail(
                request,
                command,
                appendResult.Error?.Code.ToString() ?? UiErrorCode.CoreUnknown,
                appendResult.Error?.Message ?? $"Remote command `{command}` failed while appending tasks.",
                appendResult.Error?.NativeDetails);
        }

        if (appendResult.Value <= 0)
        {
            return Fail(
                request,
                command,
                UiErrorCode.TaskNotFound,
                $"Remote command `{command}` did not match any enabled task.");
        }

        if (_connectFeatureService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Connect service is unavailable.");
        }

        var startResult = await _connectFeatureService.StartAsync(cancellationToken);
        if (!startResult.Success)
        {
            return Fail(
                request,
                command,
                startResult.Error?.Code ?? UiErrorCode.UiOperationFailed,
                startResult.Message,
                startResult.Error?.Details);
        }

        if (!string.IsNullOrWhiteSpace(commandId))
        {
            SetRunningTaskId(commandId);
        }

        var taskType = selectedTaskTypes is { Count: 1 }
            ? selectedTaskTypes.First()
            : null;
        return Success(
            request,
            command,
            $"Remote command `{command}` queued {appendResult.Value} task(s) and started execution.",
            details: $"queued={appendResult.Value}",
            taskType: taskType);
    }

    private async Task<RemoteControlCommandResult> DispatchGachaAsync(
        RemoteControlCommandRequest request,
        string command,
        string? commandId,
        bool once,
        CancellationToken cancellationToken)
    {
        if (_toolboxFeatureService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Toolbox service is unavailable.");
        }

        var result = await _toolboxFeatureService.DispatchToolAsync(
            new ToolboxDispatchRequest(
                ToolboxToolKind.Gacha,
                Gacha: new ToolboxGachaRequest(once),
                ParameterSummary: once ? "drawCount=1" : "drawCount=10"),
            cancellationToken);
        if (!result.Success || result.Value is null)
        {
            return Fail(
                request,
                command,
                result.Error?.Code ?? UiErrorCode.ToolboxExecutionFailed,
                result.Message,
                result.Error?.Details);
        }

        if (!string.IsNullOrWhiteSpace(commandId))
        {
            SetRunningTaskId(commandId);
        }

        return Success(
            request,
            command,
            $"Remote command `{command}` dispatched.",
            coreTaskId: result.Value.CoreTaskId,
            taskType: result.Value.TaskType,
            details: result.Value.ParameterSummary);
    }

    private async Task<RemoteControlCommandResult> DispatchCaptureImageAsync(
        RemoteControlCommandRequest request,
        string command,
        CancellationToken cancellationToken)
    {
        if (_coreBridge is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Core bridge is unavailable.");
        }

        var connected = await EnsureConnectedAsync(cancellationToken);
        if (!connected.Success)
        {
            return Fail(request, command, connected.Error?.Code ?? UiErrorCode.RemoteControlUnsupported, connected.Message, connected.Error?.Details);
        }

        var imageResult = await _coreBridge.GetImageAsync(cancellationToken);
        if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
        {
            return Fail(
                request,
                command,
                imageResult.Error?.Code.ToString() ?? UiErrorCode.CoreUnknown,
                imageResult.Error?.Message ?? "Failed to capture image.",
                imageResult.Error?.NativeDetails);
        }

        return new RemoteControlCommandResult(
            request.RawCommand,
            command,
            Success: true,
            $"Remote command `{command}` captured image bytes={imageResult.Value.Length}.",
            ErrorCode: null,
            Details: $"bytes={imageResult.Value.Length}",
            CoreTaskId: null,
            TaskType: null,
            ImageBytes: imageResult.Value,
            ImageContentType: "image/png");
    }

    private RemoteControlCommandResult DispatchHeartBeat(RemoteControlCommandRequest request, string command)
    {
        var state = _sessionService?.CurrentState.ToString() ?? "Unknown";
        var runningTaskId = GetRunningTaskId();
        if (_sessionService is not null && _sessionService.CurrentState is not SessionState.Running and not SessionState.Stopping)
        {
            runningTaskId = string.Empty;
            SetRunningTaskId(string.Empty);
        }

        var message = string.IsNullOrWhiteSpace(runningTaskId)
            ? $"Remote command `{command}` heartbeat ok. state={state}."
            : $"Remote command `{command}` heartbeat ok. state={state}; task={runningTaskId}.";

        return Success(
            request,
            command,
            message,
            details: string.IsNullOrWhiteSpace(runningTaskId) ? $"state={state}" : $"state={state};task={runningTaskId}");
    }

    private async Task<RemoteControlCommandResult> DispatchStopTaskAsync(
        RemoteControlCommandRequest request,
        string command,
        CancellationToken cancellationToken)
    {
        if (_connectFeatureService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Connect service is unavailable.");
        }

        var stopResult = await _connectFeatureService.StopAsync(cancellationToken);
        if (!stopResult.Success)
        {
            return Fail(
                request,
                command,
                stopResult.Error?.Code ?? UiErrorCode.UiOperationFailed,
                stopResult.Message,
                stopResult.Error?.Details);
        }

        SetRunningTaskId(string.Empty);
        return Success(request, command, $"Remote command `{command}` stopped current task.");
    }

    private async Task<RemoteControlCommandResult> DispatchConnectAddressAsync(
        RemoteControlCommandRequest request,
        string command,
        CancellationToken cancellationToken)
    {
        if (_configService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Configuration service is unavailable.");
        }

        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return Fail(request, command, UiErrorCode.ProfileMissing, "Current profile is missing.");
        }

        var rawAddress = ReadCommandParameter(
            request.Payload,
            "connectAddress",
            "address",
            "value",
            "data",
            "params");
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return Fail(request, command, UiErrorCode.RemoteControlInvalidParameters, "`Settings-ConnectAddress` requires a non-empty address.");
        }

        var address = rawAddress.Trim();
        profile.Values["ConnectAddress"] = JsonValue.Create(address);
        profile.Values[LegacyConfigurationKeys.ConnectAddress] = JsonValue.Create(address);

        try
        {
            await _configService.SaveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail(request, command, UiErrorCode.SettingsSaveFailed, $"Failed to save connect address: {ex.Message}");
        }

        return Success(request, command, $"Remote command `{command}` updated connect address to `{address}`.");
    }

    private async Task<RemoteControlCommandResult> DispatchStage1Async(
        RemoteControlCommandRequest request,
        string command,
        CancellationToken cancellationToken)
    {
        if (_configService is null)
        {
            return Fail(request, command, UiErrorCode.RemoteControlUnsupported, "Configuration service is unavailable.");
        }

        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return Fail(request, command, UiErrorCode.ProfileMissing, "Current profile is missing.");
        }

        var rawStage = ReadCommandParameter(
            request.Payload,
            "stage",
            "stage1",
            "value",
            "data",
            "params");
        if (string.IsNullOrWhiteSpace(rawStage))
        {
            return Fail(request, command, UiErrorCode.RemoteControlInvalidParameters, "`Settings-Stage1` requires a stage code.");
        }

        var normalizedStage = FightStageSelection.NormalizeStoredValue(rawStage.Trim());
        var taskIndex = -1;
        for (var index = 0; index < profile.TaskQueue.Count; index++)
        {
            if (string.Equals(TaskModuleTypes.Normalize(profile.TaskQueue[index].Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
            {
                taskIndex = index;
                break;
            }
        }

        if (taskIndex < 0)
        {
            return Fail(request, command, UiErrorCode.TaskNotFound, "No Fight task was found for `Settings-Stage1`.");
        }

        var fightTask = profile.TaskQueue[taskIndex];
        fightTask.Params["stage"] = JsonValue.Create(normalizedStage);
        _configService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Stage1] = JsonValue.Create(normalizedStage);

        try
        {
            await _configService.SaveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail(request, command, UiErrorCode.SettingsSaveFailed, $"Failed to save stage setting: {ex.Message}");
        }

        return Success(
            request,
            command,
            $"Remote command `{command}` updated stage to `{normalizedStage}`.",
            taskType: TaskModuleTypes.Fight,
            details: $"taskIndex={taskIndex}");
    }

    private async Task<CoreResult<int>> QueueSelectedTaskTypesAsync(
        IReadOnlySet<string> selectedTaskTypes,
        CancellationToken cancellationToken)
    {
        if (_configService is null || _taskQueueFeatureService is null)
        {
            return CoreResult<int>.Fail(new CoreError(CoreErrorCode.NotSupported, "Task queue dependencies are unavailable."));
        }

        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return CoreResult<int>.Fail(new CoreError(CoreErrorCode.InvalidRequest, "Current profile is missing."));
        }

        var snapshot = profile.TaskQueue.Select(static task => task.IsEnabled).ToArray();
        try
        {
            var enabledCount = 0;
            for (var index = 0; index < profile.TaskQueue.Count; index++)
            {
                var normalizedType = TaskModuleTypes.Normalize(profile.TaskQueue[index].Type);
                var enable = selectedTaskTypes.Contains(normalizedType);
                profile.TaskQueue[index].IsEnabled = enable;
                if (enable)
                {
                    enabledCount++;
                }
            }

            if (enabledCount == 0)
            {
                return CoreResult<int>.Ok(0);
            }

            return await _taskQueueFeatureService.QueueEnabledTasksAsync(cancellationToken);
        }
        finally
        {
            for (var index = 0; index < profile.TaskQueue.Count && index < snapshot.Length; index++)
            {
                profile.TaskQueue[index].IsEnabled = snapshot[index];
            }
        }
    }

    private async Task<UiOperationResult> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_sessionService is null || _connectFeatureService is null || _configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.RemoteControlUnsupported, "Remote command requires connect/session services.");
        }

        if (_sessionService.CurrentState is SessionState.Connected or SessionState.Running or SessionState.Stopping)
        {
            return UiOperationResult.Ok($"Session state is `{_sessionService.CurrentState}`.");
        }

        if (_sessionService.CurrentState is SessionState.Connecting)
        {
            return UiOperationResult.Fail(
                UiErrorCode.SessionStateNotAllowed,
                $"Session state `{_sessionService.CurrentState}` cannot connect right now.");
        }

        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return UiOperationResult.Fail(UiErrorCode.ProfileMissing, "Current profile is missing.");
        }

        var config = _configService.CurrentConfig;
        var address = ReadConfigString(profile, config, "ConnectAddress", LegacyConfigurationKeys.ConnectAddress) ?? "127.0.0.1:5555";
        var connectConfig = ReadConfigString(profile, config, "ConnectConfig", LegacyConfigurationKeys.ConnectConfig) ?? "General";
        var adbPath = ReadConfigString(profile, config, "AdbPath", LegacyConfigurationKeys.AdbPath);
        return await _connectFeatureService.ConnectAsync(address, connectConfig, adbPath, cancellationToken);
    }

    private static string? ReadConfigString(UnifiedProfile profile, UnifiedConfig config, string profileKey, string globalKey)
    {
        if (TryReadString(profile.Values, profileKey, out var profileValue))
        {
            return profileValue;
        }

        if (TryReadString(config.GlobalValues, globalKey, out var globalValue))
        {
            return globalValue;
        }

        return null;
    }

    private static bool TryReadString(IReadOnlyDictionary<string, JsonNode?> values, string key, out string? value)
    {
        value = null;
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue raw && raw.TryGetValue(out string? scalar) && !string.IsNullOrWhiteSpace(scalar))
        {
            value = scalar.Trim();
            return true;
        }

        var text = node.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text.Trim();
        return true;
    }

    private static string NormalizeCommand(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return string.Empty;
        }

        var normalized = rawCommand.Trim()
            .Replace('_', '-');
        if (normalized.StartsWith("LinkStart/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"LinkStart-{normalized["LinkStart/".Length..]}";
        }

        return normalized.ToLowerInvariant() switch
        {
            "linkstart" => "LinkStart",
            "linkstart-base" => "LinkStart-Base",
            "linkstart-wakeup" => "LinkStart-WakeUp",
            "linkstart-combat" => "LinkStart-Combat",
            "linkstart-recruiting" => "LinkStart-Recruiting",
            "linkstart-mall" => "LinkStart-Mall",
            "linkstart-mission" => "LinkStart-Mission",
            "linkstart-autoroguelike" => "LinkStart-AutoRoguelike",
            "linkstart-reclamation" => "LinkStart-Reclamation",
            "toolbox-gachaonce" => "Toolbox-GachaOnce",
            "toolbox-gachatentimes" => "Toolbox-GachaTenTimes",
            "captureimage" => "CaptureImage",
            "captureimagenow" => "CaptureImageNow",
            "heartbeat" => "HeartBeat",
            "stoptask" => "StopTask",
            "settings-connectaddress" => "Settings-ConnectAddress",
            "settings-stage1" => "Settings-Stage1",
            _ => normalized,
        };
    }

    private static string? ResolveTaskId(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in new[] { "id", "taskId", "task_id", "jobId", "job_id" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && TryReadNodeString(node, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadCommandParameter(JsonNode? payload, params string[] keys)
    {
        var commandPayload = ExtractPayloadNode(payload);
        if (TryReadNodeString(commandPayload, out var direct))
        {
            return direct;
        }

        if (commandPayload is JsonObject payloadObject)
        {
            foreach (var key in keys)
            {
                if (payloadObject.TryGetPropertyValue(key, out var node) && TryReadNodeString(node, out var value))
                {
                    return value;
                }
            }
        }

        if (payload is JsonObject rootObject)
        {
            foreach (var key in keys)
            {
                if (rootObject.TryGetPropertyValue(key, out var node) && TryReadNodeString(node, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static JsonNode? ExtractPayloadNode(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
        {
            return payload;
        }

        foreach (var key in new[] { "params", "payload", "parameters", "args", "data", "body", "request" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is not null)
            {
                return node;
            }
        }

        return payload;
    }

    private static bool TryReadNodeString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                value = s.Trim();
                return true;
            }

            var raw = scalar.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }

            return false;
        }

        return false;
    }

    private static string GetRunningTaskId()
    {
        lock (RunningTaskGate)
        {
            return _runningTaskId;
        }
    }

    private static void SetRunningTaskId(string? id)
    {
        lock (RunningTaskGate)
        {
            _runningTaskId = string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
        }
    }

    private static RemoteControlCommandResult Success(
        RemoteControlCommandRequest request,
        string command,
        string message,
        string? details = null,
        int? coreTaskId = null,
        string? taskType = null)
    {
        return new RemoteControlCommandResult(
            request.RawCommand,
            command,
            Success: true,
            message,
            ErrorCode: null,
            Details: details,
            CoreTaskId: coreTaskId,
            TaskType: taskType);
    }

    private static RemoteControlCommandResult Fail(
        RemoteControlCommandRequest request,
        string command,
        string code,
        string message,
        string? details = null)
    {
        return new RemoteControlCommandResult(
            request.RawCommand,
            command,
            Success: false,
            message,
            ErrorCode: code,
            Details: details);
    }
}
