using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Application.Orchestration;

public sealed record SessionCallbackEnvelope(
    CoreCallbackEvent Callback,
    JsonObject? Payload,
    string? What,
    string? TaskChain,
    string? SubTask,
    int? TaskId,
    string? ParseError)
{
    public static SessionCallbackEnvelope FromRaw(CoreCallbackEvent callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (string.IsNullOrWhiteSpace(callback.PayloadJson))
        {
            return new SessionCallbackEnvelope(callback, null, null, null, null, null, null);
        }

        try
        {
            if (JsonNode.Parse(callback.PayloadJson) is not JsonObject payload)
            {
                return new SessionCallbackEnvelope(
                    callback,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "payload is not a JSON object");
            }

            return new SessionCallbackEnvelope(
                callback,
                payload,
                GetWhat(payload, out var whatParseError),
                GetString(payload, "task_chain") ?? GetString(payload, "taskchain"),
                GetString(payload, "sub_task") ?? GetString(payload, "subtask"),
                GetInt(payload, "task_id") ?? GetInt(payload, "taskid"),
                whatParseError);
        }
        catch (JsonException ex)
        {
            return new SessionCallbackEnvelope(
                callback,
                null,
                null,
                null,
                null,
                null,
                $"payload parse failed: {ex.Message}");
        }
    }

    private static string? GetString(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        return node.ToString();
    }

    private static string? GetWhat(JsonObject payload, out string? parseError)
    {
        foreach (var property in payload)
        {
            if (!string.Equals(property.Key, "what", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is null)
            {
                parseError = "property `what` is empty";
                return null;
            }

            if (property.Value.GetValueKind() != JsonValueKind.String)
            {
                parseError = "property `what` is not a string";
                return null;
            }

            if (property.Value is not JsonValue value || !value.TryGetValue(out string? text))
            {
                parseError = "property `what` is not a string";
                return null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                parseError = "property `what` is empty";
                return null;
            }

            parseError = null;
            return text;
        }

        parseError = null;
        return null;
    }

    private static int? GetInt(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int number))
            {
                return number;
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out number))
            {
                return number;
            }
        }

        return null;
    }
}

public sealed class UnifiedSessionService
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectingStopWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly IMaaCoreBridge _bridge;
    private readonly UnifiedConfigurationService _configService;
    private readonly UiLogService _logService;
    private readonly SessionStateMachine _stateMachine;
    private readonly object _taskIndexMapGate = new();
    private readonly object _runOwnerGate = new();
    private readonly object _connectionOperationGate = new();
    private readonly SemaphoreSlim _connectionOperationLock = new(1, 1);
    private readonly Dictionary<int, int> _taskIndexByCoreTaskId = new();
    private CoreConnectionInfo? _lastSuccessfulConnectionInfo;
    private string? _currentRunOwner;
    private string? _currentRunOwnerDisplayName;
    private CancellationTokenSource? _activeConnectCts;
    private CoreConnectionInfo? _activeConnectionTarget;
    private long _activeConnectionGeneration;
    private long _connectionOperationGeneration;
    private bool _ignoreIdleConnectionCallbacks;

    public UnifiedSessionService(
        IMaaCoreBridge bridge,
        UnifiedConfigurationService configService,
        UiLogService logService,
        SessionStateMachine stateMachine)
    {
        _bridge = bridge;
        _configService = configService;
        _logService = logService;
        _stateMachine = stateMachine;
        _stateMachine.StateChanged += OnSessionStateChanged;
    }

    public SessionState CurrentState => _stateMachine.CurrentState;

    public CoreConnectionInfo? LastSuccessfulConnectionInfo => _lastSuccessfulConnectionInfo;

    public event Action<SessionState>? SessionStateChanged;

    public event Action<CoreCallbackEvent>? CallbackReceived;

    public event Action<SessionCallbackEnvelope>? CallbackProjected;

    public string? CurrentRunOwner
    {
        get
        {
            lock (_runOwnerGate)
            {
                return _currentRunOwner;
            }
        }
    }

    public string? CurrentRunOwnerDisplayName
    {
        get
        {
            lock (_runOwnerGate)
            {
                return string.IsNullOrWhiteSpace(_currentRunOwnerDisplayName)
                    ? _currentRunOwner
                    : _currentRunOwnerDisplayName;
            }
        }
    }

    public bool TryBeginRun(string owner, out string? currentOwner)
    {
        return TryBeginRun(owner, displayName: null, out currentOwner);
    }

    public bool TryBeginRun(string owner, string? displayName, out string? currentOwner)
    {
        currentOwner = null;
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        var normalizedOwner = owner.Trim();
        lock (_runOwnerGate)
        {
            if (!string.IsNullOrWhiteSpace(_currentRunOwner)
                && !string.Equals(_currentRunOwner, normalizedOwner, StringComparison.Ordinal))
            {
                currentOwner = _currentRunOwner;
                return false;
            }

            _currentRunOwner = normalizedOwner;
            _currentRunOwnerDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? normalizedOwner
                : displayName.Trim();
            currentOwner = _currentRunOwner;
            return true;
        }
    }

    public bool IsRunOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        lock (_runOwnerGate)
        {
            return string.Equals(_currentRunOwner, owner.Trim(), StringComparison.Ordinal);
        }
    }

    public void EndRun(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        lock (_runOwnerGate)
        {
            if (string.Equals(_currentRunOwner, owner.Trim(), StringComparison.Ordinal))
            {
                _currentRunOwner = null;
                _currentRunOwnerDisplayName = null;
            }
        }
    }

    public bool TryResolveTaskIndexByCoreTaskId(int taskId, out int taskIndex)
    {
        lock (_taskIndexMapGate)
        {
            return _taskIndexByCoreTaskId.TryGetValue(taskId, out taskIndex);
        }
    }

    private void ClearTaskIdMappings()
    {
        lock (_taskIndexMapGate)
        {
            _taskIndexByCoreTaskId.Clear();
        }
    }

    private void SetTaskIdMapping(int coreTaskId, int queueIndex)
    {
        lock (_taskIndexMapGate)
        {
            _taskIndexByCoreTaskId[coreTaskId] = queueIndex;
        }
    }

    public async Task<CoreResult<bool>> ConnectAsync(
        string address,
        string connectConfig,
        string? adbPath,
        CoreConnectionExtras? extras = null,
        CancellationToken cancellationToken = default)
    {
        var connectionInfo = new CoreConnectionInfo(
            NormalizeConnectionText(address),
            NormalizeConnectionText(connectConfig),
            NormalizeConnectionPath(adbPath),
            extras,
            Timeout: DefaultConnectTimeout);
        return await ConnectAsync(connectionInfo, cancellationToken);
    }

    public async Task<CoreResult<bool>> ConnectAsync(
        CoreConnectionInfo requestedConnectionInfo,
        CancellationToken cancellationToken = default)
    {
        var connectionInfo = NormalizeConnectionInfo(requestedConnectionInfo, DefaultConnectTimeout);
        _logService.Debug(
            $"Session.Connect requested: state={CurrentState}, {BuildConnectionSummary(connectionInfo)}");

        await _connectionOperationLock.WaitAsync(cancellationToken);
        CancellationTokenSource? operationCts = null;
        bool disposeOperationCts = true;
        long generation = 0;
        try
        {
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var operationToken = operationCts.Token;
            lock (_connectionOperationGate)
            {
                generation = ++_connectionOperationGeneration;
                _activeConnectCts?.Dispose();
                _activeConnectCts = operationCts;
                _activeConnectionTarget = connectionInfo;
                _activeConnectionGeneration = generation;
                _ignoreIdleConnectionCallbacks = false;
            }

            if (CurrentState == SessionState.Connected && !LastSuccessfulConnectionInfoMatches(connectionInfo))
            {
                var previousSummary = _lastSuccessfulConnectionInfo is null
                    ? "<null>"
                    : BuildConnectionSummary(_lastSuccessfulConnectionInfo);
                _logService.Info(
                    $"Session.Connect invalidated previous connection before reconnect: current={BuildConnectionSummary(connectionInfo)}, previous={previousSummary}");
                _lastSuccessfulConnectionInfo = null;
            }

            MoveToState(SessionState.Connecting, "Session.Connect", "begin");
            CoreResult<bool> result;
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var bridgeConnectTask = _bridge.ConnectAsync(connectionInfo, operationToken);
                    var completedTask = await Task.WhenAny(
                        bridgeConnectTask,
                        WaitForConnectCancellationAsync(operationToken)).ConfigureAwait(false);
                    if (completedTask == bridgeConnectTask)
                    {
                        result = await bridgeConnectTask.ConfigureAwait(false);
                    }
                    else
                    {
                        disposeOperationCts = false;
                        ObserveAbandonedConnectTask(bridgeConnectTask, operationCts, connectionInfo, generation);
                        if (IsCurrentConnectionGeneration(generation))
                        {
                            var fallbackState = await ResolveFailureStateAsync("Session.Connect", SessionState.Idle, CancellationToken.None);
                            MoveToState(fallbackState, "Session.Connect", "connect-canceled-fallback");
                            throw new OperationCanceledException(operationToken);
                        }

                        _logService.Warn(
                            $"Session.Connect canceled after supersede for address={connectionInfo.Address}, generation={generation}.");
                        return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectTimeout, "Connect was superseded by a newer operation."));
                    }

                    if (result.Success || attempt > 0 || !IsAbandonedNativeStopFailure(result.Error))
                    {
                        break;
                    }

                    _logService.Warn(
                        $"Session.Connect recovering abandoned native stop before retry: {BuildConnectionSummary(connectionInfo)}");
                    var recover = await RecoverAbandonedStopAsync(operationToken).ConfigureAwait(false);
                    if (!recover.Success)
                    {
                        result = CoreResult<bool>.Fail(recover.Error!);
                        break;
                    }

                    _lastSuccessfulConnectionInfo = null;
                    MarkIdleConnectionCallbacksStale();
                    MoveToState(SessionState.Connecting, "Session.Connect", "abandoned-stop-recovered-retry");
                }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                if (IsCurrentConnectionGeneration(generation))
                {
                    var fallbackState = await ResolveFailureStateAsync("Session.Connect", SessionState.Idle, CancellationToken.None);
                    MoveToState(fallbackState, "Session.Connect", "connect-canceled-fallback");
                    throw;
                }

                _logService.Warn(
                    $"Session.Connect canceled after supersede for address={connectionInfo.Address}, generation={generation}.");
                return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectTimeout, "Connect was superseded by a newer operation."));
            }

            if (!IsCurrentConnectionGeneration(generation))
            {
                _logService.Warn(
                    $"Session.Connect ignored stale result for address={connectionInfo.Address}, generation={generation}.");
                return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectTimeout, "Connect result was superseded by a newer operation."));
            }

            if (result.Success)
            {
                _lastSuccessfulConnectionInfo = connectionInfo;
                MoveToState(SessionState.Connected, "Session.Connect", "connected");
                _logService.Info($"Connected: {BuildConnectionSummary(connectionInfo)}");
            }
            else
            {
                ClearActiveConnectionTarget(connectionInfo);
                var fallbackState = await ResolveFailureStateAsync("Session.Connect", SessionState.Idle, cancellationToken);
                MoveToState(fallbackState, "Session.Connect", "connect-failed-fallback");
                _logService.Warn(
                    $"Failed to connect: {BuildConnectionSummary(connectionInfo)}; error={result.Error?.Code} {result.Error?.Message}");
            }

            return result;
        }
        finally
        {
            lock (_connectionOperationGate)
            {
                if (ReferenceEquals(_activeConnectCts, operationCts))
                {
                    _activeConnectCts = null;
                }

                if (_activeConnectionGeneration == generation)
                {
                    _activeConnectionTarget = null;
                    _activeConnectionGeneration = 0;
                }
            }

            if (disposeOperationCts)
            {
                operationCts?.Dispose();
            }
            _connectionOperationLock.Release();
        }
    }

    public bool IsConnectedWith(CoreConnectionInfo connectionInfo)
    {
        return CurrentState == SessionState.Connected
            && LastSuccessfulConnectionInfoMatches(connectionInfo);
    }

    private bool LastSuccessfulConnectionInfoMatches(CoreConnectionInfo connectionInfo)
    {
        var last = _lastSuccessfulConnectionInfo;
        if (last is null)
        {
            return false;
        }

        return ConnectionInfoMatches(last, connectionInfo, compareExtras: true);
    }

    public async Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
        CoreInstanceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _bridge.ApplyInstanceOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        if (result.Success || !IsAbandonedNativeStopFailure(result.Error))
        {
            return result;
        }

        _logService.Warn("Session.ApplyInstanceOptions recovering abandoned native stop before retry.");
        var recover = await RecoverAbandonedStopAsync(cancellationToken).ConfigureAwait(false);
        if (!recover.Success)
        {
            return CoreResult<bool>.Fail(recover.Error!);
        }

        _lastSuccessfulConnectionInfo = null;
        ClearActiveConnectionTarget();
        MarkIdleConnectionCallbacksStale();
        MoveToState(SessionState.Idle, "Session.ApplyInstanceOptions", "abandoned-stop-recovered");
        return await _bridge.ApplyInstanceOptionsAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreResult<int>> AppendTasksFromCurrentProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!_configService.CurrentConfig.Profiles.TryGetValue(_configService.CurrentConfig.CurrentProfile, out var profile))
        {
            _logService.Warn($"Current profile `{_configService.CurrentConfig.CurrentProfile}` not found");
            return CoreResult<int>.Fail(new CoreError(CoreErrorCode.InvalidRequest, "Current profile was not found."));
        }

        ClearTaskIdMappings();

        int appended = 0;
        for (var queueIndex = 0; queueIndex < profile.TaskQueue.Count; queueIndex++)
        {
            var task = profile.TaskQueue[queueIndex];
            if (!task.IsEnabled)
            {
                continue;
            }

            var compiled = TaskParamCompiler.CompileTask(task, profile, _configService.CurrentConfig, strict: true);
            var blockingIssues = compiled.Issues.Where(i => i.Blocking).ToList();
            if (blockingIssues.Count > 0)
            {
                var details = string.Join(
                    "; ",
                    blockingIssues.Select(i => $"{i.Code}:{i.Field}:{i.Message}"));
                _logService.Warn($"Append task blocked `{task.Name}`: {details}");
                ClearTaskIdMappings();
                return CoreResult<int>.Fail(new CoreError(
                    CoreErrorCode.InvalidRequest,
                    $"Task `{task.Name}` validation failed: {details}"));
            }

            foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
            {
                _logService.Warn($"Task warning `{task.Name}`: {warning.Code}:{warning.Field}:{warning.Message}");
            }

            task.Type = compiled.NormalizedType;
            if (!string.Equals(compiled.NormalizedType, TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
            {
                task.Params = compiled.Params;
            }

            if (ShouldSkipTaskForToday(task, profile, _configService.CurrentConfig))
            {
                _logService.Info($"Skipped task `{task.Name}` because current weekly schedule disables it today.");
                continue;
            }

            if (string.Equals(compiled.NormalizedType, TaskModuleTypes.UserDataUpdate, StringComparison.OrdinalIgnoreCase))
            {
                var userDataTasks = BuildUserDataUpdateCoreTasks(task, profile, _configService.CurrentConfig);
                foreach (var userDataTask in userDataTasks)
                {
                    var appendCoreResult = await _bridge.AppendTaskAsync(userDataTask, cancellationToken);
                    if (!appendCoreResult.Success)
                    {
                        _logService.Warn($"Append task failed `{task.Name}`: {appendCoreResult.Error?.Code} {appendCoreResult.Error?.Message}");
                        ClearTaskIdMappings();
                        return CoreResult<int>.Fail(appendCoreResult.Error!);
                    }

                    SetTaskIdMapping(appendCoreResult.Value, queueIndex);
                    appended += 1;
                    _logService.Info($"Appended task #{appendCoreResult.Value}: {userDataTask.Name}");
                }

                continue;
            }

            var appendParams = TaskParamCompiler.BuildCoreParams(compiled.NormalizedType, compiled.Params);
            var appendResult = await _bridge.AppendTaskAsync(
                new CoreTaskRequest(compiled.NormalizedType, task.Name, task.IsEnabled, appendParams.ToJsonString()),
                cancellationToken);
            if (!appendResult.Success)
            {
                _logService.Warn($"Append task failed `{task.Name}`: {appendResult.Error?.Code} {appendResult.Error?.Message}");
                ClearTaskIdMappings();
                return CoreResult<int>.Fail(appendResult.Error!);
            }

            SetTaskIdMapping(appendResult.Value, queueIndex);

            appended += 1;
            _logService.Info($"Appended task #{appendResult.Value}: {task.Name}");
        }

        if (appended == 0)
        {
            _logService.Warn("No enabled tasks in current profile to append");
        }

        return CoreResult<int>.Ok(appended);
    }

    public async Task<CoreResult<int>> AppendCoreTasksAsync(
        IEnumerable<CoreTaskRequest> coreTasks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearTaskIdMappings();

        var index = 0;
        foreach (var task in coreTasks)
        {
            var appendResult = await _bridge.AppendTaskAsync(task, cancellationToken);
            if (!appendResult.Success)
            {
                ClearTaskIdMappings();
                return CoreResult<int>.Fail(appendResult.Error!);
            }

            SetTaskIdMapping(appendResult.Value, index);
            index += 1;
        }

        return CoreResult<int>.Ok(index);
    }

    private bool ShouldSkipTaskForToday(UnifiedTaskItem task, UnifiedProfile profile, UnifiedConfig config)
    {
        if (!string.Equals(TaskParamCompiler.NormalizeTaskType(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var (dto, _) = TaskParamCompiler.ReadFight(task, strict: false);
        if (!dto.UseWeeklySchedule)
        {
            return false;
        }

        var clientType = ResolveStringSetting(profile, config, LegacyConfigurationKeys.ClientType) ?? "Official";
        var currentDay = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, clientType).DayOfWeek;
        return !IsFightEnabledForDay(dto, currentDay);
    }

    private IReadOnlyList<CoreTaskRequest> BuildUserDataUpdateCoreTasks(UnifiedTaskItem task, UnifiedProfile profile, UnifiedConfig config)
    {
        var (dto, _) = TaskParamCompiler.ReadUserDataUpdate(task, strict: false);
        if (!dto.UpdateOperBox && !dto.UpdateDepot)
        {
            return [];
        }

        var nowUtc = DateTime.UtcNow;
        var clientType = ResolveStringSetting(profile, config, LegacyConfigurationKeys.ClientType) ?? "Official";
        var tasks = new List<CoreTaskRequest>(2);
        if (dto.UpdateOperBox && IsUserDataUpdateTriggerDue(ReadOperBoxSyncTime(config), dto.TriggerInterval, clientType, nowUtc))
        {
            tasks.Add(new CoreTaskRequest("OperBox", $"{task.Name}-OperBox", true, "{}"));
        }

        if (dto.UpdateDepot && IsUserDataUpdateTriggerDue(ReadDepotSyncTime(config), dto.TriggerInterval, clientType, nowUtc))
        {
            tasks.Add(new CoreTaskRequest("Depot", $"{task.Name}-Depot", true, "{}"));
        }

        return tasks;
    }

    private static bool IsFightEnabledForDay(FightTaskParamsDto dto, DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Sunday => dto.WeeklyScheduleSunday,
            DayOfWeek.Monday => dto.WeeklyScheduleMonday,
            DayOfWeek.Tuesday => dto.WeeklyScheduleTuesday,
            DayOfWeek.Wednesday => dto.WeeklyScheduleWednesday,
            DayOfWeek.Thursday => dto.WeeklyScheduleThursday,
            DayOfWeek.Friday => dto.WeeklyScheduleFriday,
            DayOfWeek.Saturday => dto.WeeklyScheduleSaturday,
            _ => true,
        };
    }

    private static bool IsUserDataUpdateTriggerDue(
        DateTimeOffset? lastSyncTime,
        string triggerInterval,
        string clientType,
        DateTime nowUtc)
    {
        if (string.Equals(triggerInterval, UserDataUpdateTaskParamsDto.TriggerEveryTime, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lastSyncTime is null)
        {
            return true;
        }

        var currentYjDate = MallDailyResetHelper.GetYjDate(nowUtc, clientType);
        var lastYjDate = MallDailyResetHelper.GetYjDate(lastSyncTime.Value.UtcDateTime, clientType);
        if (string.Equals(triggerInterval, UserDataUpdateTaskParamsDto.TriggerWeekly, StringComparison.OrdinalIgnoreCase))
        {
            return ISOWeek.GetYear(currentYjDate) != ISOWeek.GetYear(lastYjDate)
                   || ISOWeek.GetWeekOfYear(currentYjDate) != ISOWeek.GetWeekOfYear(lastYjDate);
        }

        return currentYjDate > lastYjDate;
    }

    private static DateTimeOffset? ReadOperBoxSyncTime(UnifiedConfig config)
    {
        if (!config.GlobalValues.TryGetValue(LegacyConfigurationKeys.OperBoxData, out var node) || node is null)
        {
            return null;
        }

        return ReadPersistedSyncTime(node);
    }

    private static DateTimeOffset? ReadDepotSyncTime(UnifiedConfig config)
    {
        if (!config.GlobalValues.TryGetValue(LegacyConfigurationKeys.DepotResult, out var node) || node is null)
        {
            return null;
        }

        return ReadPersistedSyncTime(node);
    }

    private static DateTimeOffset? ReadPersistedSyncTime(JsonNode node)
    {
        var payload = node is JsonValue jsonValue && jsonValue.TryGetValue(out string? raw)
            ? raw
            : node.ToJsonString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject obj)
            {
                return null;
            }

            if (obj["syncTime"] is not JsonValue syncValue || !syncValue.TryGetValue(out string? syncText))
            {
                return null;
            }

            return DateTimeOffset.TryParse(syncText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveStringSetting(UnifiedProfile profile, UnifiedConfig config, string key)
    {
        if (profile.Values.TryGetValue(key, out var profileNode)
            && profileNode is JsonValue profileValue
            && profileValue.TryGetValue(out string? profileText)
            && !string.IsNullOrWhiteSpace(profileText))
        {
            return profileText.Trim();
        }

        if (config.GlobalValues.TryGetValue(key, out var globalNode)
            && globalNode is JsonValue globalValue
            && globalValue.TryGetValue(out string? globalText)
            && !string.IsNullOrWhiteSpace(globalText))
        {
            return globalText.Trim();
        }

        return null;
    }

    public async Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
    {
        _logService.Debug($"Session.Start requested: state={CurrentState}");
        var result = await _bridge.StartAsync(cancellationToken);
        if (result.Success)
        {
            MoveToState(SessionState.Running, "Session.Start", "started");
        }
        else
        {
            var fallbackState = await ResolveFailureStateAsync("Session.Start", SessionState.Connected, cancellationToken);
            MoveToState(fallbackState, "Session.Start", "start-failed-fallback");
        }

        _logService.Info(result.Success ? "Task execution started" : $"Task execution failed to start: {result.Error?.Code} {result.Error?.Message}");
        return result;
    }

    public async Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
    {
        _logService.Debug($"Session.Stop requested: state={CurrentState}");

        var stopRequestedDuringConnect = CurrentState == SessionState.Connecting;
        if (stopRequestedDuringConnect)
        {
            MoveToState(SessionState.Stopping, "Session.Stop", "cancel-connecting");
            SupersedeActiveConnectOperation(cancelNativeOperation: true);
            MoveToState(SessionState.Idle, "Session.Stop", "supersede-connecting");
            if (!await _connectionOperationLock.WaitAsync(ConnectingStopWaitTimeout, cancellationToken).ConfigureAwait(false))
            {
                _logService.Info("Session.Stop marked pending connect as superseded; native connect is still settling.");
                return CoreResult<bool>.Ok(true);
            }

            _connectionOperationLock.Release();
            _logService.Info("Session.Stop completed after pending connect settled.");
            return CoreResult<bool>.Ok(true);
        }

        SupersedeActiveConnectOperation(cancelNativeOperation: true);
        await _connectionOperationLock.WaitAsync(cancellationToken);
        try
        {
            var initialState = CurrentState;
            MoveToState(SessionState.Stopping, "Session.Stop", "begin-stop");

            var result = await _bridge.StopAsync(cancellationToken);
            if (result.Success)
            {
                var fallbackState = await ResolveFailureStateAsync("Session.Stop", SessionState.Connected, cancellationToken);
                MoveToState(fallbackState == SessionState.Running ? SessionState.Connected : fallbackState, "Session.Stop", "stopped");
            }
            else if (IsAbandonedNativeStopFailure(result.Error))
            {
                _lastSuccessfulConnectionInfo = null;
                ClearActiveConnectionTarget();
                MarkIdleConnectionCallbacksStale();
                _logService.Warn(
                    $"Session.Stop stop-abandoned-invalidated: {result.Error?.Code} {result.Error?.Message}");
                MoveToState(SessionState.Idle, "Session.Stop", "stop-abandoned-invalidated");
            }
            else
            {
                var fallbackState = await ResolveFailureStateAsync("Session.Stop", SessionState.Connected, cancellationToken);
                MoveToState(fallbackState, "Session.Stop", "stop-failed-fallback");
            }

            _logService.Info(result.Success ? "Task execution stopped" : $"Task execution stop failed: {result.Error?.Code} {result.Error?.Message}");
            return result;
        }
        finally
        {
            _connectionOperationLock.Release();
        }
    }

    public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        return _bridge.GetRuntimeStatusAsync(cancellationToken);
    }

    public async Task<CoreResult<bool>> ReloadResourceWhenIdleAsync(
        string? clientType = null,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveTimeout = waitTimeout ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "Resource reload timeout must be greater than zero."));
        }

        _logService.Debug(
            $"Session.ReloadResource requested: state={CurrentState}, client={clientType ?? "<current>"}, timeout={effectiveTimeout}");
        var waitResult = await WaitUntilReloadSafeAsync(effectiveTimeout, cancellationToken);
        if (!waitResult.Success)
        {
            _logService.Warn(
                $"Session.ReloadResource aborted: {waitResult.Error?.Code} {waitResult.Error?.Message}");
            return waitResult;
        }

        var reloadResult = await _bridge.ReloadResourceAsync(clientType, cancellationToken);
        if (reloadResult.Success)
        {
            _logService.Info($"Session resources reloaded. client={clientType ?? "<current>"}");
        }
        else
        {
            _logService.Warn(
                $"Session resource reload failed: {reloadResult.Error?.Code} {reloadResult.Error?.Message}");
        }

        return reloadResult;
    }

    public async Task StartCallbackPumpAsync(Func<CoreCallbackEvent, Task> onEvent, CancellationToken cancellationToken = default)
    {
        await foreach (var callback in _bridge.CallbackStreamAsync(cancellationToken))
        {
            var envelope = SessionCallbackEnvelope.FromRaw(callback);

            try
            {
                ApplyCallbackToState(envelope);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.Warn(
                    $"Session.Callback state mapping failed for {callback.MsgName}({callback.MsgId}): {ex.Message}");
            }

            try
            {
                CallbackReceived?.Invoke(callback);
            }
            catch (Exception ex)
            {
                _logService.Warn(
                    $"Session.Callback subscriber failed for {callback.MsgName}({callback.MsgId}): {ex.Message}");
            }

            try
            {
                CallbackProjected?.Invoke(envelope);
            }
            catch (Exception ex)
            {
                _logService.Warn(
                    $"Session.Callback projection subscriber failed for {callback.MsgName}({callback.MsgId}): {ex.Message}");
            }

            try
            {
                await onEvent(callback);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.Warn(
                    $"Session.Callback handler failed for {callback.MsgName}({callback.MsgId}): {ex.Message}");
            }
        }
    }

    private void ApplyCallbackToState(SessionCallbackEnvelope envelope)
    {
        var callback = envelope.Callback;
        if (string.Equals(callback.MsgName, "TaskChainStart", StringComparison.OrdinalIgnoreCase))
        {
            MoveToState(SessionState.Running, "Session.Callback", "TaskChainStart");
            return;
        }

        if (string.Equals(callback.MsgName, "TaskChainCompleted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callback.MsgName, "TaskChainStopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callback.MsgName, "AllTasksCompleted", StringComparison.OrdinalIgnoreCase))
        {
            MoveToState(SessionState.Connected, "Session.Callback", callback.MsgName);
            return;
        }

        if (!string.Equals(callback.MsgName, "ConnectionInfo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var what = envelope.What;
        if (string.IsNullOrWhiteSpace(what))
        {
            var parseError = string.IsNullOrWhiteSpace(envelope.ParseError)
                ? "property `what` is missing"
                : envelope.ParseError;
            _logService.Warn(
                $"Session.Callback ignored ConnectionInfo payload: {parseError}; msgId={callback.MsgId}; msgName={callback.MsgName}");
            return;
        }

        if (string.Equals(what, "ResolutionGot", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetCallbackConnectionInfo(envelope.Payload, out var resolutionConnectionInfo))
            {
                _ = ShouldAcceptConnectedCallback(resolutionConnectionInfo, what);
            }

            return;
        }

        if (string.Equals(what, "Connected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "Reconnected", StringComparison.OrdinalIgnoreCase))
        {
            var hasCallbackConnectionInfo = TryGetCallbackConnectionInfo(envelope.Payload, out var callbackConnectionInfo);
            if (CurrentState == SessionState.Idle)
            {
                if (_ignoreIdleConnectionCallbacks)
                {
                    _logService.Warn(
                        $"Session.Callback ignored stale ConnectionInfo:{what} while session is idle.");
                    return;
                }

                _logService.Debug(
                    $"Session.Callback accepted ConnectionInfo:{what} while session is idle.");
            }

            if (!ShouldAcceptConnectedCallback(hasCallbackConnectionInfo ? callbackConnectionInfo : null, what))
            {
                return;
            }

            if (CurrentState == SessionState.Connecting)
            {
                _logService.Debug(
                    $"Session.Callback recorded ConnectionInfo:{what} while connect is still pending.");
                return;
            }

            if (hasCallbackConnectionInfo)
            {
                _lastSuccessfulConnectionInfo = CompleteCallbackConnectionInfo(callbackConnectionInfo);
            }

            MoveToState(SessionState.Connected, "Session.Callback", $"ConnectionInfo:{what}");
            return;
        }

        if (string.Equals(what, "Disconnect", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetCallbackConnectionInfo(envelope.Payload, out var disconnectConnectionInfo)
                && _lastSuccessfulConnectionInfo is not null
                && !ConnectionInfoMatches(_lastSuccessfulConnectionInfo, disconnectConnectionInfo, compareExtras: false))
            {
                _logService.Warn(
                    $"Session.Callback ignored stale ConnectionInfo:Disconnect for address={disconnectConnectionInfo.Address}, config={disconnectConnectionInfo.ConnectConfig}.");
                return;
            }

            _lastSuccessfulConnectionInfo = null;
            ClearActiveConnectionTarget();
            MoveToState(SessionState.Idle, "Session.Callback", "ConnectionInfo:Disconnect");
            return;
        }

        if (string.Equals(what, "ScreencapCost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "FastestWayToScreencap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logService.Warn(
            $"Session.Callback ignored unknown ConnectionInfo.what `{what}`; msgId={callback.MsgId}; msgName={callback.MsgName}");
    }

    private void OnSessionStateChanged(SessionState state)
    {
        _logService.Info($"Session state -> {state}");
        SessionStateChanged?.Invoke(state);
    }

    private void MoveToState(SessionState state, string scope, string reason)
    {
        var previous = _stateMachine.CurrentState;
        if (_stateMachine.TryMoveTo(state))
        {
            return;
        }

        _logService.Warn(
            $"{scope} ignored invalid state transition: {previous} -> {state}; reason={reason}");
    }

    private async Task<SessionState> ResolveFailureStateAsync(
        string scope,
        SessionState fallbackWhenStatusUnavailable,
        CancellationToken cancellationToken)
    {
        var runtimeResult = await _bridge.GetRuntimeStatusAsync(cancellationToken);
        if (!runtimeResult.Success || runtimeResult.Value is null)
        {
            _logService.Warn(
                $"{scope} fallback runtime status unavailable, use {fallbackWhenStatusUnavailable}: {runtimeResult.Error?.Code} {runtimeResult.Error?.Message}");
            return fallbackWhenStatusUnavailable;
        }

        var resolved = MapRuntimeStatusToSessionState(runtimeResult.Value);
        _logService.Warn(
            $"{scope} fallback resolved from runtime status: initialized={runtimeResult.Value.Initialized}, connected={runtimeResult.Value.Connected}, running={runtimeResult.Value.Running} -> {resolved}");
        return resolved;
    }

    private static SessionState MapRuntimeStatusToSessionState(CoreRuntimeStatus status)
    {
        if (status.Running)
        {
            return SessionState.Running;
        }

        if (status.Connected)
        {
            return SessionState.Connected;
        }

        return SessionState.Idle;
    }

    private static string NormalizeConnectionText(string? value)
        => (value ?? string.Empty).Trim();

    private static string? NormalizeConnectionPath(string? value)
    {
        var normalized = NormalizeConnectionText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static CoreConnectionInfo NormalizeConnectionInfo(
        CoreConnectionInfo connectionInfo,
        TimeSpan? fallbackTimeout = null)
    {
        return new CoreConnectionInfo(
            NormalizeConnectionText(connectionInfo.Address),
            NormalizeConnectionText(connectionInfo.ConnectConfig),
            NormalizeConnectionPath(connectionInfo.AdbPath),
            NormalizeConnectionExtras(connectionInfo.Extras),
            connectionInfo.Timeout ?? fallbackTimeout);
    }

    private static bool ConnectionInfoMatches(
        CoreConnectionInfo left,
        CoreConnectionInfo right,
        bool compareExtras)
    {
        var rightConfig = NormalizeConnectionText(right.ConnectConfig);
        var rightAdbPath = NormalizeConnectionPath(right.AdbPath);
        return string.Equals(
                NormalizeConnectionText(left.Address),
                NormalizeConnectionText(right.Address),
                StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(rightConfig)
                || string.Equals(
                    NormalizeConnectionText(left.ConnectConfig),
                    rightConfig,
                    StringComparison.Ordinal))
            && (rightAdbPath is null
                || string.Equals(
                    NormalizeConnectionPath(left.AdbPath),
                    rightAdbPath,
                    StringComparison.Ordinal))
            && (!compareExtras || NormalizeConnectionExtras(left.Extras) == NormalizeConnectionExtras(right.Extras));
    }

    private static CoreConnectionExtras NormalizeConnectionExtras(CoreConnectionExtras? extras)
    {
        extras ??= CoreConnectionExtras.Empty;
        return new CoreConnectionExtras(
            MacUseBundledAdb: extras.MacUseBundledAdb,
            TouchMode: NormalizeConnectionText(extras.TouchMode),
            AdbLiteEnabled: extras.AdbLiteEnabled,
            KillAdbOnExit: extras.KillAdbOnExit,
            MuMu12ExtrasEnabled: extras.MuMu12ExtrasEnabled,
            MuMu12EmulatorPath: NormalizeConnectionText(extras.MuMu12EmulatorPath),
            MuMuBridgeConnection: extras.MuMuBridgeConnection,
            MuMu12Index: NormalizeConnectionText(extras.MuMu12Index),
            LdPlayerExtrasEnabled: extras.LdPlayerExtrasEnabled,
            LdPlayerEmulatorPath: NormalizeConnectionText(extras.LdPlayerEmulatorPath),
            LdPlayerManualSetIndex: extras.LdPlayerManualSetIndex,
            LdPlayerIndex: NormalizeConnectionText(extras.LdPlayerIndex),
            AttachWindowScreencapMethod: NormalizeConnectionText(extras.AttachWindowScreencapMethod),
            AttachWindowMouseMethod: NormalizeConnectionText(extras.AttachWindowMouseMethod),
            AttachWindowKeyboardMethod: NormalizeConnectionText(extras.AttachWindowKeyboardMethod),
            ClientType: NormalizeConnectionText(extras.ClientType),
            FallbackStrategy: NormalizeConnectionText(extras.FallbackStrategy),
            ConfiguredTouchMode: NormalizeConnectionText(extras.ConfiguredTouchMode),
            ConfiguredAdbLiteEnabled: extras.ConfiguredAdbLiteEnabled,
            FallbackReason: NormalizeConnectionText(extras.FallbackReason),
            FallbackRequiredLibrary: NormalizeConnectionText(extras.FallbackRequiredLibrary),
            FallbackRequiredLibraryExists: extras.FallbackRequiredLibraryExists);
    }

    private static string BuildConnectionSummary(CoreConnectionInfo connectionInfo)
    {
        var extras = NormalizeConnectionExtras(connectionInfo.Extras);
        return $"address={connectionInfo.Address}, config={connectionInfo.ConnectConfig}, adb={connectionInfo.AdbPath ?? "<null>"}, "
               + $"macBundledAdb={extras.MacUseBundledAdb}, touch={extras.TouchMode}, adbLite={extras.AdbLiteEnabled}, killAdbOnExit={extras.KillAdbOnExit}, "
               + $"mumuExtras={extras.MuMu12ExtrasEnabled}:{extras.MuMu12EmulatorPath}:{extras.MuMuBridgeConnection}:{extras.MuMu12Index}, "
               + $"ldExtras={extras.LdPlayerExtrasEnabled}:{extras.LdPlayerEmulatorPath}:{extras.LdPlayerManualSetIndex}:{extras.LdPlayerIndex}, "
               + $"attach={extras.AttachWindowScreencapMethod}:{extras.AttachWindowMouseMethod}:{extras.AttachWindowKeyboardMethod}, "
               + $"clientType={extras.ClientType}, fallback={extras.FallbackStrategy}, "
               + $"configured=touch={extras.ConfiguredTouchMode},adbLite={extras.ConfiguredAdbLiteEnabled}, "
               + $"reason={extras.FallbackReason}";
    }

    private static bool TryGetCallbackConnectionInfo(JsonObject? payload, out CoreConnectionInfo connectionInfo)
    {
        connectionInfo = null!;
        if (payload?["details"] is not JsonObject details)
        {
            return false;
        }

        var address = NormalizeConnectionText(GetJsonString(details, "address"));
        var config = NormalizeConnectionText(GetJsonString(details, "config"));
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        connectionInfo = new CoreConnectionInfo(
            address,
            config,
            NormalizeConnectionPath(GetJsonString(details, "adb")));
        return true;
    }

    private static string? GetJsonString(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        return node.ToString();
    }

    private async Task<CoreResult<bool>> WaitUntilReloadSafeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadlineAt = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentState = CurrentState;
            if (!IsReloadHardBlockedState(currentState))
            {
                var runtimeResult = await _bridge.GetRuntimeStatusAsync(cancellationToken);
                if (runtimeResult.Success && runtimeResult.Value is { Running: false })
                {
                    return CoreResult<bool>.Ok(true);
                }

                if (!runtimeResult.Success && !IsReloadSoftBlockedState(currentState))
                {
                    // Runtime probe can fail transiently during reconnect/dispose windows.
                    // When state machine is already idle/connected, proceed with best effort.
                    return CoreResult<bool>.Ok(true);
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= deadlineAt)
            {
                return CoreResult<bool>.Fail(new CoreError(
                    CoreErrorCode.ConnectTimeout,
                    $"Timed out waiting for session to become idle before resource reload. state={CurrentState}"));
            }

            var wait = deadlineAt - now;
            if (wait > TimeSpan.FromMilliseconds(200))
            {
                wait = TimeSpan.FromMilliseconds(200);
            }

            await Task.Delay(wait, cancellationToken);
        }
    }

    private static bool IsReloadHardBlockedState(SessionState state)
    {
        return state == SessionState.Connecting;
    }

    private static bool IsReloadSoftBlockedState(SessionState state)
    {
        return state is SessionState.Running or SessionState.Stopping;
    }

    private static bool IsAbandonedNativeStopFailure(CoreError? error)
    {
        if (error?.Code != CoreErrorCode.StopFailed)
        {
            return false;
        }

        var text = $"{error.Message}\n{error.NativeDetails}";
        return text.Contains("native stop timed out", StringComparison.OrdinalIgnoreCase)
               || text.Contains("native instance was abandoned", StringComparison.OrdinalIgnoreCase)
               || text.Contains("native operation was abandoned", StringComparison.OrdinalIgnoreCase)
               || text.Contains("instance was abandoned", StringComparison.OrdinalIgnoreCase)
               || text.Contains("AsstStop did not return", StringComparison.OrdinalIgnoreCase);
    }

    private Task<CoreResult<bool>> RecoverAbandonedStopAsync(CancellationToken cancellationToken)
        => _bridge is IMaaCoreBridgeRecovery recovery
            ? recovery.RecoverAbandonedStopAsync(cancellationToken)
            : _bridge.RecoverFromAbandonedStopAsync(cancellationToken);

    private void ClearActiveConnectionTarget(CoreConnectionInfo? expected = null)
    {
        lock (_connectionOperationGate)
        {
            if (expected is not null
                && _activeConnectionTarget is not null
                && !ConnectionInfoMatches(_activeConnectionTarget, expected, compareExtras: true))
            {
                return;
            }

            _activeConnectionTarget = null;
            _activeConnectionGeneration = 0;
        }
    }

    private void MarkIdleConnectionCallbacksStale()
    {
        lock (_connectionOperationGate)
        {
            _ignoreIdleConnectionCallbacks = true;
        }
    }

    private bool IsCurrentConnectionGeneration(long generation)
    {
        lock (_connectionOperationGate)
        {
            return generation == _connectionOperationGeneration;
        }
    }

    private bool ShouldAcceptConnectedCallback(CoreConnectionInfo? callbackConnectionInfo, string what)
    {
        CoreConnectionInfo? activeTarget;
        lock (_connectionOperationGate)
        {
            activeTarget = _activeConnectionTarget;
        }

        if (callbackConnectionInfo is not null)
        {
            if (activeTarget is not null
                && !ConnectionInfoMatches(activeTarget, callbackConnectionInfo, compareExtras: false))
            {
                _logService.Warn(
                    $"Session.Callback ignored stale ConnectionInfo:{what} for address={callbackConnectionInfo.Address}, config={callbackConnectionInfo.ConnectConfig}; active address={activeTarget.Address}, config={activeTarget.ConnectConfig}.");
                return false;
            }

            if (_lastSuccessfulConnectionInfo is not null
                && !ConnectionInfoMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo, compareExtras: false))
            {
                _logService.Warn(
                    $"Session.Callback ignored stale ConnectionInfo:{what} for address={callbackConnectionInfo.Address}, config={callbackConnectionInfo.ConnectConfig}; current address={_lastSuccessfulConnectionInfo.Address}, config={_lastSuccessfulConnectionInfo.ConnectConfig}.");
                return false;
            }

            return _lastSuccessfulConnectionInfo is not null || activeTarget is not null;
        }

        if (_lastSuccessfulConnectionInfo is null && activeTarget is null)
        {
            if (CurrentState == SessionState.Idle)
            {
                return true;
            }

            _logService.Warn(
                $"Session.Callback ignored ConnectionInfo:{what} without connection details and no active connection target.");
            return false;
        }

        return true;
    }

    private CoreConnectionInfo CompleteCallbackConnectionInfo(CoreConnectionInfo callbackConnectionInfo)
    {
        CoreConnectionInfo? activeTarget;
        lock (_connectionOperationGate)
        {
            activeTarget = _activeConnectionTarget;
        }

        if (activeTarget is not null
            && ConnectionInfoMatches(activeTarget, callbackConnectionInfo, compareExtras: false))
        {
            return activeTarget;
        }

        if (_lastSuccessfulConnectionInfo is not null
            && ConnectionInfoMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo, compareExtras: false))
        {
            return _lastSuccessfulConnectionInfo;
        }

        return callbackConnectionInfo;
    }

    private static async Task WaitForConnectCancellationAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        await completion.Task.ConfigureAwait(false);
    }

    private void ObserveAbandonedConnectTask(
        Task<CoreResult<bool>> bridgeConnectTask,
        CancellationTokenSource operationCts,
        CoreConnectionInfo connectionInfo,
        long generation)
    {
        _ = bridgeConnectTask.ContinueWith(
            task =>
            {
                try
                {
                    if (task.IsFaulted)
                    {
                        _logService.Warn(
                            $"Abandoned connect task faulted after supersede: {BuildConnectionSummary(connectionInfo)}; generation={generation}; error={task.Exception?.GetBaseException().Message}");
                    }
                    else if (task.IsCanceled)
                    {
                        _logService.Debug(
                            $"Abandoned connect task canceled after supersede: {BuildConnectionSummary(connectionInfo)}; generation={generation}.");
                    }
                    else
                    {
                        _logService.Debug(
                            $"Abandoned connect task completed after supersede: {BuildConnectionSummary(connectionInfo)}; generation={generation}; success={task.Result.Success}.");
                    }
                }
                finally
                {
                    operationCts.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SupersedeActiveConnectOperation(bool cancelNativeOperation)
    {
        lock (_connectionOperationGate)
        {
            _connectionOperationGeneration++;
            _activeConnectionTarget = null;
            _activeConnectionGeneration = 0;
            if (_activeConnectCts is not null)
            {
                _ignoreIdleConnectionCallbacks = true;
            }

            try
            {
                if (cancelNativeOperation)
                {
                    _activeConnectCts?.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // Best effort: the operation has already completed.
            }
        }
    }
}
