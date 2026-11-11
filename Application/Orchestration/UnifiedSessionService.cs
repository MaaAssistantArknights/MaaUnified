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

public sealed record ConnectionCallbackStatus(
    string What,
    string? Address = null,
    string? AdbPath = null,
    string? ConnectConfig = null,
    int? ReconnectAttempt = null,
    int? Width = null,
    int? Height = null,
    string? Method = null);

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
    private readonly SemaphoreSlim _resourceReloadLock = new(1, 1);
    private readonly Dictionary<int, int> _taskIndexByCoreTaskId = new();
    private bool _taskIndexMappingsBelongToProfileBatch;
    private CoreConnectionInfo? _lastSuccessfulConnectionInfo;
    private string? _currentRunOwner;
    private string? _currentRunOwnerDisplayName;
    private CancellationTokenSource? _activeConnectCts;
    private CoreConnectionInfo? _activeConnectionTarget;
    private CoreConnectionInfo? _activeCallbackConnectionInfo;
    private CoreConnectionInfo? _previousConnectionTarget;
    private CoreConnectionInfo? _lastRequestedConnectionTarget;
    private CoreConnectionInfo? _lastRejectedConnectionTarget;
    private long _activeConnectionGeneration;
    private long _connectionOperationGeneration;
    // Snapshot of the bridge's callback enqueue counter taken at the latest run
    // boundary: immediately before a run start, and again when a run ends through a
    // stop or a tainted-queue reset. TaskChainStart, terminal, and instance-lifecycle
    // (InitFailed/Destroyed) callbacks carry the sequence they were enqueued with; such
    // an event whose sequence is not newer than this watermark belongs to an earlier
    // batch or a destroyed instance that is still waiting in the pump and must neither
    // move the session out of the current run nor resurrect a run that already ended.
    private long _runBoundaryCallbackWatermark;
    private bool _ignoreIdleConnectionCallbacks;
    private bool _taskQueueTainted;

    private sealed record OneShotRecruitExpediteSnapshot(
        UnifiedTaskItem Task,
        bool HadExpedite,
        JsonNode? Expedite,
        bool HadExpediteTimes,
        JsonNode? ExpediteTimes);

    private Task _pendingNativeStopTask = Task.CompletedTask;

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

    public ConnectionCallbackStatus? LastConnectionCallbackStatus { get; private set; }

    public event Action<SessionState>? SessionStateChanged;

    public event Action<CoreCallbackEvent>? CallbackReceived;

    public event Action<SessionCallbackEnvelope>? CallbackProjected;

    public event Action<ConnectionCallbackStatus>? ConnectionCallbackStatusChanged;

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

    public async Task<CoreResult<bool>> SetQueuedTaskParamsAsync(
        int taskIndex,
        string paramsJson,
        string? previousParamsJson = null,
        CancellationToken cancellationToken = default)
    {
        int[] coreTaskIds;
        lock (_taskIndexMapGate)
        {
            // Only mappings registered by a profile append batch may be targeted by
            // profile param edits. Ad-hoc runs (Copilot/Toolbox/WebApi) register
            // their task ids under the same indices for callback resolution only;
            // issuing AsstSetTaskParams on those ids would rewrite or spuriously
            // fail against an unrelated native task. The edit still applies to the
            // profile in memory and reaches the native queue at the next append.
            if (!_taskIndexMappingsBelongToProfileBatch)
            {
                return CoreResult<bool>.Ok(true);
            }

            coreTaskIds = _taskIndexByCoreTaskId
                .Where(pair => pair.Value == taskIndex)
                .Select(pair => pair.Key)
                .ToArray();
        }

        var appliedTaskIds = new List<int>(coreTaskIds.Length);
        foreach (var coreTaskId in coreTaskIds)
        {
            var update = await _bridge.SetTaskParamsAsync(coreTaskId, paramsJson, cancellationToken).ConfigureAwait(false);
            if (!update.Success)
            {
                await RollbackAppliedTaskParamsAsync(appliedTaskIds, previousParamsJson).ConfigureAwait(false);
                return update;
            }

            appliedTaskIds.Add(coreTaskId);
        }

        return CoreResult<bool>.Ok(true);
    }

    private async Task RollbackAppliedTaskParamsAsync(
        IReadOnlyList<int> appliedTaskIds,
        string? previousParamsJson)
    {
        // MaaCore exposes no read/rollback API. One queue index can map to several
        // native tasks (OperBox + Depot), so when a later update fails, restore the
        // siblings already updated to keep the native queue aligned with the profile.
        if (appliedTaskIds.Count == 0 || previousParamsJson is null)
        {
            return;
        }

        foreach (var appliedTaskId in appliedTaskIds)
        {
            var rollback = await _bridge.SetTaskParamsAsync(appliedTaskId, previousParamsJson, CancellationToken.None).ConfigureAwait(false);
            if (!rollback.Success)
            {
                _logService.Warn($"Failed to roll back queued task params for core task #{appliedTaskId}: {rollback.Error?.Code} {rollback.Error?.Message}");
            }
        }
    }

    private void ClearTaskIdMappings()
    {
        lock (_taskIndexMapGate)
        {
            _taskIndexByCoreTaskId.Clear();
            _taskIndexMappingsBelongToProfileBatch = false;
        }
    }

    private void BeginProfileTaskIdMappings()
    {
        lock (_taskIndexMapGate)
        {
            _taskIndexByCoreTaskId.Clear();
            _taskIndexMappingsBelongToProfileBatch = true;
        }
    }

    private void SetTaskIdMapping(int coreTaskId, int queueIndex)
    {
        lock (_taskIndexMapGate)
        {
            _taskIndexByCoreTaskId[coreTaskId] = queueIndex;
        }
    }

    private bool HasMappedCoreTasks()
    {
        lock (_taskIndexMapGate)
        {
            return _taskIndexByCoreTaskId.Count > 0;
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
            NormalizeConnectionAddress(address),
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
                _activeCallbackConnectionInfo = null;
                _previousConnectionTarget = _lastRequestedConnectionTarget;
                _lastRequestedConnectionTarget = connectionInfo;
                _activeConnectionGeneration = generation;
                _ignoreIdleConnectionCallbacks = false;
            }

            var pendingNativeStop = PendingNativeStopSnapshot;
            if (!pendingNativeStop.IsCompleted)
            {
                _logService.Info(
                    $"Session.Connect waiting for pending native stop from a previous disconnect before connect: {BuildConnectionSummary(connectionInfo)}");
                await pendingNativeStop.WaitAsync(operationToken).ConfigureAwait(false);
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

            // A previous interrupted append may have left the native queue
            // tainted with recovery incomplete (e.g. the core was still
            // running). Retry the reset before connecting so a successful
            // reconnect also clears the taint instead of failing the next
            // append with a stale reconnect-required error. Recovery failure
            // must not block the connect itself: connecting is still required
            // to stop a core that is still running.
            var queueRecoveryPending = _taskQueueTainted;
            if (queueRecoveryPending)
            {
                var queueRecovery = await RecoverTaintedTaskQueueCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (queueRecovery.Success)
                {
                    queueRecoveryPending = false;
                    _logService.Warn("Tainted task queue was reset during reconnect.");
                }
                else
                {
                    _logService.Warn(
                        $"Tainted task queue recovery failed during reconnect; connecting anyway so a running core can be stopped: {queueRecovery.Error?.Code} {queueRecovery.Error?.Message}");
                }
            }

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
                            // The canceled attempt may still complete inside the core
                            // (the abort stop budget can expire before AsstStop runs);
                            // ignore its late Connected callbacks while the session is idle.
                            MarkIdleConnectionCallbacksStale();
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
                    // Same late-Connected protection as the cancellation branch above.
                    MarkIdleConnectionCallbacksStale();
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
                _taskQueueTainted = queueRecoveryPending;
                _lastSuccessfulConnectionInfo = CompleteActiveCallbackConnectionInfo(connectionInfo);
                MoveToState(SessionState.Connected, "Session.Connect", "connected");
                _logService.Info($"Connected: {BuildConnectionSummary(_lastSuccessfulConnectionInfo)}");
            }
            else
            {
                ClearActiveConnectionTarget(connectionInfo);
                // The failed attempt may still complete inside the core (the abort stop
                // budget can expire before AsstStop runs); ignore its late Connected
                // callbacks while the session is idle.
                MarkIdleConnectionCallbacksStale();
                // Probe with None: a caller/budget cancellation escaping here would
                // strand the session in Connecting after a definitive connect failure.
                var fallbackState = await ResolveFailureStateAsync("Session.Connect", SessionState.Idle, CancellationToken.None);
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
                    _activeCallbackConnectionInfo = null;
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

    public void InvalidateConnectionAfterExternalExit()
    {
        _lastSuccessfulConnectionInfo = null;
        ClearActiveConnectionTarget();
        MarkIdleConnectionCallbacksStale();
        MoveToState(SessionState.Idle, "Session.ExternalExit", "external-device-exit");
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
        cancellationToken.ThrowIfCancellationRequested();
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var oneShotRecruitSnapshots = new List<OneShotRecruitExpediteSnapshot>();
            try
            {
                var appendResult = await AppendTasksFromCurrentProfileCoreAsync(
                    cancellationToken,
                    oneShotRecruitSnapshots).ConfigureAwait(false);
                if (!appendResult.Success)
                {
                    RestoreOneShotRecruitExpedited(oneShotRecruitSnapshots);
                }

                return appendResult;
            }
            catch
            {
                try
                {
                    if (HasMappedCoreTasks())
                    {
                        await ResetTaintedTaskQueueAsync("profile append preparation failed after append").ConfigureAwait(false);
                    }
                }
                finally
                {
                    RestoreOneShotRecruitExpedited(oneShotRecruitSnapshots);
                }

                throw;
            }
        }
        finally
        {
            _connectionOperationLock.Release();
        }
    }

    private async Task<CoreResult<int>> AppendTasksFromCurrentProfileCoreAsync(
        CancellationToken cancellationToken,
        List<OneShotRecruitExpediteSnapshot> oneShotRecruitSnapshots)
    {
        if (_taskQueueTainted)
        {
            return CoreResult<int>.Fail(CreateReconnectRequiredError());
        }

        if (!_configService.CurrentConfig.Profiles.TryGetValue(_configService.CurrentConfig.CurrentProfile, out var profile))
        {
            _logService.Warn($"Current profile `{_configService.CurrentConfig.CurrentProfile}` not found");
            return CoreResult<int>.Fail(new CoreError(CoreErrorCode.InvalidRequest, "Current profile was not found."));
        }

        BeginProfileTaskIdMappings();

        int appended = 0;
        var resetOneShotRecruitExpedited = false;
        for (var queueIndex = 0; queueIndex < profile.TaskQueue.Count; queueIndex++)
        {
            await ThrowIfAppendCanceledAsync(appended, cancellationToken, "profile append canceled").ConfigureAwait(false);
            var task = profile.TaskQueue[queueIndex];
            var effectiveEnabled = TaskQueueEnabledState.IsEffectivelyEnabled(task, _configService.CurrentConfig);
            if (!effectiveEnabled)
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
                var error = new CoreError(
                    CoreErrorCode.InvalidRequest,
                    $"Task `{task.Name}` validation failed: {details}");
                return appended > 0
                    ? await FailAppendBatchAsync(error, "profile append validation failed").ConfigureAwait(false)
                    : CoreResult<int>.Fail(error);
            }

            foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
            {
                _logService.Warn($"Task warning `{task.Name}`: {warning.Code}:{warning.Field}:{warning.Message}");
            }

            if (compiled.Issues.Any(i => string.Equals(i.Code, "TaskCompileSkipAppend", StringComparison.Ordinal)))
            {
                _logService.Info($"Skipped task `{task.Name}` because compile-time parameters resolved to no runnable task.");
                continue;
            }

            task.Type = compiled.NormalizedType;
            if (string.Equals(compiled.NormalizedType, TaskModuleTypes.Roguelike, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the full-fidelity stored form so queuing a run does not strip
                // hidden mode/investment-gated Roguelike settings.
                task.Params = TaskParamCompiler
                    .CompileRoguelikeForPersistence(task, profile, _configService.CurrentConfig, strict: true)
                    .Params;
            }
            else if (!string.Equals(compiled.NormalizedType, TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(compiled.NormalizedType, TaskModuleTypes.StartUp, StringComparison.OrdinalIgnoreCase))
                {
                    // WPF parity: running the queue must not rewrite the stored StartUp
                    // account name; only the appended Core JSON blanks it for
                    // non-Official/Bilibili clients. Keep compiled.Params untouched so the
                    // appended payload below still carries the blanked account name.
                    var storedParams = compiled.Params.DeepClone() as JsonObject ?? new JsonObject();
                    TaskParamCompiler.PreserveStoredStartUpAccountName(task, storedParams);
                    task.Params = storedParams;
                }
                else
                {
                    task.Params = compiled.Params;
                }
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
                    // MaaCore exposes no remove/clear API. Once the first append is issued,
                    // finish the batch so cancellation cannot leave a partial native queue.
                    await ThrowIfAppendCanceledAsync(appended, cancellationToken, "profile append canceled").ConfigureAwait(false);
                    CoreResult<int> appendCoreResult;
                    try
                    {
                        appendCoreResult = await _bridge.AppendTaskAsync(userDataTask, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        await ResetTaintedTaskQueueAsync("profile append threw").ConfigureAwait(false);
                        throw;
                    }
                    if (!appendCoreResult.Success)
                    {
                        _logService.Warn($"Append task failed `{task.Name}`: {appendCoreResult.Error?.Code} {appendCoreResult.Error?.Message}");
                        return await FailAppendBatchAsync(appendCoreResult.Error!, "profile append failed").ConfigureAwait(false);
                    }

                    SetTaskIdMapping(appendCoreResult.Value, queueIndex);
                    appended += 1;
                    _logService.Info($"Appended task #{appendCoreResult.Value}: {userDataTask.Name}");
                    await ThrowIfAppendCanceledAsync(appended, cancellationToken, "profile append canceled").ConfigureAwait(false);
                }

                continue;
            }

            var appendParams = TaskParamCompiler.BuildCoreParams(compiled.NormalizedType, compiled.Params);
            await ThrowIfAppendCanceledAsync(appended, cancellationToken, "profile append canceled").ConfigureAwait(false);
            CoreResult<int> appendResult;
            try
            {
                appendResult = await _bridge.AppendTaskAsync(
                    new CoreTaskRequest(compiled.NormalizedType, task.Name, effectiveEnabled, appendParams.ToJsonString()),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("profile append threw").ConfigureAwait(false);
                throw;
            }
            if (!appendResult.Success)
            {
                _logService.Warn($"Append task failed `{task.Name}`: {appendResult.Error?.Code} {appendResult.Error?.Message}");
                return await FailAppendBatchAsync(appendResult.Error!, "profile append failed").ConfigureAwait(false);
            }

            SetTaskIdMapping(appendResult.Value, queueIndex);

            appended += 1;
            if (ResetOneShotRecruitExpedited(task, oneShotRecruitSnapshots))
            {
                resetOneShotRecruitExpedited = true;
            }

            _logService.Info($"Appended task #{appendResult.Value}: {task.Name}");
            await ThrowIfAppendCanceledAsync(appended, cancellationToken, "profile append canceled").ConfigureAwait(false);
        }

        if (resetOneShotRecruitExpedited)
        {
            await _configService.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        if (appended == 0)
        {
            _logService.Warn("No enabled tasks in current profile to append");
        }

        return CoreResult<int>.Ok(appended);
    }

    private static bool ResetOneShotRecruitExpedited(
        UnifiedTaskItem task,
        List<OneShotRecruitExpediteSnapshot> oneShotRecruitSnapshots)
    {
        if (!string.Equals(TaskParamCompiler.NormalizeTaskType(task.Type), TaskModuleTypes.Recruit, StringComparison.OrdinalIgnoreCase)
            || task.Params["expedite"] is not JsonValue expediteValue
            || !expediteValue.TryGetValue(out bool expedite)
            || !expedite)
        {
            return false;
        }

        oneShotRecruitSnapshots.Add(new OneShotRecruitExpediteSnapshot(
            task,
            HadExpedite: task.Params.ContainsKey("expedite"),
            Expedite: task.Params["expedite"]?.DeepClone(),
            HadExpediteTimes: task.Params.ContainsKey("expedite_times"),
            ExpediteTimes: task.Params["expedite_times"]?.DeepClone()));
        task.Params["expedite"] = false;
        task.Params.Remove("expedite_times");
        return true;
    }

    private static void RestoreOneShotRecruitExpedited(
        IReadOnlyList<OneShotRecruitExpediteSnapshot> oneShotRecruitSnapshots)
    {
        for (var index = oneShotRecruitSnapshots.Count - 1; index >= 0; index--)
        {
            var snapshot = oneShotRecruitSnapshots[index];
            if (snapshot.HadExpedite)
            {
                snapshot.Task.Params["expedite"] = snapshot.Expedite?.DeepClone();
            }
            else
            {
                snapshot.Task.Params.Remove("expedite");
            }

            if (snapshot.HadExpediteTimes)
            {
                snapshot.Task.Params["expedite_times"] = snapshot.ExpediteTimes?.DeepClone();
            }
            else
            {
                snapshot.Task.Params.Remove("expedite_times");
            }
        }
    }

    public async Task<CoreResult<int>> AppendCoreTasksAsync(
        IEnumerable<CoreTaskRequest> coreTasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreTasks);
        cancellationToken.ThrowIfCancellationRequested();
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AppendCoreTasksCoreAsync(coreTasks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionOperationLock.Release();
        }
    }

    public async Task<CoreResult<int>> AppendTasksFromCurrentProfileAndStartAsync(
        CoreInstanceOptions instanceOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var oneShotRecruitSnapshots = new List<OneShotRecruitExpediteSnapshot>();
            // Task-ID mappings survive a completed batch and are only cleared when
            // the next append batch starts, so before this batch's append phase runs
            // they may still belong to a previous batch. Track the phase boundary:
            // an exception escaping the options phase must not taint a native queue
            // this batch never touched.
            var appendPhaseStarted = false;
            try
            {
                var currentState = CurrentState;
                if (currentState is SessionState.Running or SessionState.Stopping)
                {
                    return CreateAppendStartWhileRunningError(currentState);
                }

                if (_taskQueueTainted)
                {
                    return CoreResult<int>.Fail(CreateReconnectRequiredError());
                }

                var prepare = await ApplyStartInstanceOptionsCoreAsync(instanceOptions, cancellationToken).ConfigureAwait(false);
                if (!prepare.Success)
                {
                    return CoreResult<int>.Fail(prepare.Error!);
                }

                appendPhaseStarted = true;
                var appendResult = await AppendTasksFromCurrentProfileCoreAsync(
                    cancellationToken,
                    oneShotRecruitSnapshots).ConfigureAwait(false);
                if (!appendResult.Success)
                {
                    RestoreOneShotRecruitExpedited(oneShotRecruitSnapshots);
                    return appendResult;
                }

                var startResult = await StartAppendedTasksCoreAsync(
                    appendResult.Value,
                    "profile",
                    cancellationToken).ConfigureAwait(false);
                if (!startResult.Success)
                {
                    RestoreOneShotRecruitExpedited(oneShotRecruitSnapshots);
                }

                return startResult;
            }
            catch
            {
                try
                {
                    if (appendPhaseStarted && HasMappedCoreTasks())
                    {
                        await ResetTaintedTaskQueueAsync("profile start preparation failed after append").ConfigureAwait(false);
                    }
                }
                finally
                {
                    RestoreOneShotRecruitExpedited(oneShotRecruitSnapshots);
                }

                throw;
            }
        }
        finally
        {
            _connectionOperationLock.Release();
        }
    }

    public async Task<CoreResult<int>> AppendCoreTasksAndStartAsync(
        IEnumerable<CoreTaskRequest> coreTasks,
        CoreInstanceOptions instanceOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreTasks);
        cancellationToken.ThrowIfCancellationRequested();
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentState = CurrentState;
            if (currentState is SessionState.Running or SessionState.Stopping)
            {
                return CreateAppendStartWhileRunningError(currentState);
            }

            if (_taskQueueTainted)
            {
                return CoreResult<int>.Fail(CreateReconnectRequiredError());
            }

            var prepare = await ApplyStartInstanceOptionsCoreAsync(instanceOptions, cancellationToken).ConfigureAwait(false);
            if (!prepare.Success)
            {
                return CoreResult<int>.Fail(prepare.Error!);
            }

            var appendResult = await AppendCoreTasksCoreAsync(coreTasks, cancellationToken).ConfigureAwait(false);
            return appendResult.Success
                ? await StartAppendedTasksCoreAsync(appendResult.Value, "core batch", cancellationToken).ConfigureAwait(false)
                : appendResult;
        }
        finally
        {
            _connectionOperationLock.Release();
        }
    }

    public async Task<CoreResult<int>> AppendAndStartCoreTaskAsync(
        CoreTaskRequest coreTask,
        CancellationToken cancellationToken = default,
        Action<int>? onAppended = null,
        CoreInstanceOptions? instanceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(coreTask);
        cancellationToken.ThrowIfCancellationRequested();
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentState = CurrentState;
            if (currentState is SessionState.Running or SessionState.Stopping)
            {
                return CreateAppendStartWhileRunningError(currentState);
            }

            if (_taskQueueTainted)
            {
                return CoreResult<int>.Fail(CreateReconnectRequiredError());
            }

            if (instanceOptions is not null)
            {
                var prepare = await ApplyStartInstanceOptionsCoreAsync(instanceOptions, cancellationToken).ConfigureAwait(false);
                if (!prepare.Success)
                {
                    return CoreResult<int>.Fail(prepare.Error!);
                }
            }

            ClearTaskIdMappings();
            CoreResult<int> appendResult;
            try
            {
                appendResult = await _bridge.AppendTaskAsync(coreTask, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("core append threw").ConfigureAwait(false);
                throw;
            }
            if (!appendResult.Success)
            {
                return await FailAppendBatchAsync(appendResult.Error!, "core append failed").ConfigureAwait(false);
            }

            // Ad-hoc runs reuse queue index 0 for callback task-id resolution only;
            // the mapping is not marked as a profile batch, so param edits via
            // SetQueuedTaskParamsAsync never target this task.
            SetTaskIdMapping(appendResult.Value, 0);
            try
            {
                onAppended?.Invoke(appendResult.Value);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("core start preparation failed after append").ConfigureAwait(false);
                throw;
            }

            CoreResult<bool> startResult;
            try
            {
                CaptureRunBoundaryCallbackWatermark();
                startResult = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("core start threw after append").ConfigureAwait(false);
                throw;
            }

            if (!startResult.Success)
            {
                await ResetTaintedTaskQueueAsync("core start failed after append").ConfigureAwait(false);
                return CoreResult<int>.Fail(startResult.Error!);
            }

            MoveToState(SessionState.Running, "Session.AppendAndStart", "started");
            _logService.Info($"Appended and started task #{appendResult.Value}: {coreTask.Name}");
            return CoreResult<int>.Ok(appendResult.Value);
        }
        finally
        {
            _connectionOperationLock.Release();
        }
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
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pendingNativeStop = PendingNativeStopSnapshot;
            if (!pendingNativeStop.IsCompleted)
            {
                _logService.Info("Session.Start waiting for pending native stop from a previous disconnect before starting tasks.");
                await pendingNativeStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_taskQueueTainted)
            {
                return CoreResult<bool>.Fail(CreateReconnectRequiredError());
            }

            CoreResult<bool> result;
            try
            {
                CaptureRunBoundaryCallbackWatermark();
                result = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch when (HasMappedCoreTasks())
            {
                await ResetTaintedTaskQueueAsync("core start threw after append").ConfigureAwait(false);
                throw;
            }

            if (result.Success)
            {
                MoveToState(SessionState.Running, "Session.Start", "started");
            }
            else
            {
                if (HasMappedCoreTasks())
                {
                    await ResetTaintedTaskQueueAsync("core start failed after append").ConfigureAwait(false);
                }
                else
                {
                    // Probe with None: a caller cancellation escaping here would skip
                    // the failure-state resolution after a definitive start failure.
                    var fallbackState = await ResolveFailureStateAsync("Session.Start", SessionState.Connected, CancellationToken.None);
                    MoveToState(fallbackState, "Session.Start", "start-failed-fallback");
                }
            }

            _logService.Info(result.Success ? "Task execution started" : $"Task execution failed to start: {result.Error?.Code} {result.Error?.Message}");
            return result;
        }
        finally
        {
            _connectionOperationLock.Release();
        }
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

            CoreResult<bool> result;
            try
            {
                result = await _bridge.StopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The bridge re-checks the caller token in its post-stop not-running
                // probe, so a cancellation landing after AsstStop already succeeded
                // surfaces here with the batch already stopped. Never strand the
                // session in Stopping: resolve the real core state exactly like a
                // failed stop does. No run-boundary watermark is captured because the
                // stop may not have been issued at all; a still-running batch's
                // genuine callbacks must keep flowing.
                var canceledFallback = await ResolveFailureStateAsync("Session.Stop", SessionState.Connected, CancellationToken.None);
                MoveToState(canceledFallback, "Session.Stop", "stop-canceled-fallback");
                throw;
            }

            if (result.Success)
            {
                // Every callback of the stopped batch is enqueued before AsstStop
                // returns; any of them still waiting in the pump must not resurrect
                // the run this stop just ended.
                CaptureRunBoundaryCallbackWatermark();
                // Probe with None: a caller cancellation escaping here would strand the
                // session in Stopping after the stop already succeeded, and the stopped
                // batch's filtered terminal callbacks could not heal it.
                var fallbackState = await ResolveFailureStateAsync("Session.Stop", SessionState.Connected, CancellationToken.None);
                MoveToState(fallbackState == SessionState.Running ? SessionState.Connected : fallbackState, "Session.Stop", "stopped");
            }
            else if (IsAbandonedNativeStopFailure(result.Error))
            {
                _lastSuccessfulConnectionInfo = null;
                ClearActiveConnectionTarget();
                MarkIdleConnectionCallbacksStale();
                CaptureRunBoundaryCallbackWatermark();
                _logService.Warn(
                    $"Session.Stop stop-abandoned-invalidated: {result.Error?.Code} {result.Error?.Message}");
                MoveToState(SessionState.Idle, "Session.Stop", "stop-abandoned-invalidated");
            }
            else
            {
                // Probe with None: same stranded-Stopping hazard as the success branch.
                var fallbackState = await ResolveFailureStateAsync("Session.Stop", SessionState.Connected, CancellationToken.None);
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

    public async Task<CoreResult<bool>> StartCloseDownAsync(
        string clientType,
        CancellationToken cancellationToken = default)
    {
        await _connectionOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stop = await _bridge.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!stop.Success)
            {
                return stop;
            }

            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["client_type"] = clientType?.Trim() ?? string.Empty,
            });
            CoreResult<int> append;
            try
            {
                append = await _bridge.AppendTaskAsync(
                    new CoreTaskRequest("CloseDown", "CloseDown", true, payload),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("CloseDown append threw").ConfigureAwait(false);
                throw;
            }
            if (!append.Success)
            {
                return CoreResult<bool>.Fail(append.Error!);
            }

            CoreResult<bool> result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureRunBoundaryCallbackWatermark();
                result = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("CloseDown start threw after append").ConfigureAwait(false);
                throw;
            }

            if (!result.Success)
            {
                await ResetTaintedTaskQueueAsync("CloseDown start failed after append").ConfigureAwait(false);
            }
            else
            {
                MoveToState(SessionState.Running, "Session.CloseDown", "started");
            }

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
        CancellationToken cancellationToken = default,
        Func<bool>? isStillRequested = null)
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
        var deadlineAt = DateTimeOffset.UtcNow + effectiveTimeout;
        while (true)
        {
            var remaining = deadlineAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return ReloadTimeoutFailure();
            }

            var waitResult = await WaitUntilReloadSafeAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!waitResult.Success)
            {
                _logService.Warn(
                    $"Session.ReloadResource aborted: {waitResult.Error?.Code} {waitResult.Error?.Message}");
                return waitResult;
            }

            // Serialize concurrent reload requests and re-validate the supersede gate while
            // holding the lock. Without this an older request could pass the gate, then complete
            // its native reload after a newer request, leaving the previous client's resources
            // loaded as the terminal state.
            remaining = deadlineAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero
                || !await _resourceReloadLock.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                return ReloadTimeoutFailure();
            }

            try
            {
                if (isStillRequested is not null && !isStillRequested())
                {
                    _logService.Debug(
                        $"Session.ReloadResource skipped because a newer reload request superseded it before the idle window opened: client={clientType ?? "<current>"}");
                    return CoreResult<bool>.Ok(true);
                }

                remaining = deadlineAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero
                    || !await _connectionOperationLock.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                {
                    return ReloadTimeoutFailure();
                }

                try
                {
                    if (!await IsReloadSafeSnapshotAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var reloadResult = await _bridge.ReloadResourceAsync(clientType, cancellationToken).ConfigureAwait(false);
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
                finally
                {
                    _connectionOperationLock.Release();
                }
            }
            finally
            {
                _resourceReloadLock.Release();
            }
        }
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
            if (IsStaleRunBoundaryCallback(callback, "run-start"))
            {
                return;
            }

            MoveToState(SessionState.Running, "Session.Callback", "TaskChainStart");
            return;
        }

        if (string.Equals(callback.MsgName, "TaskChainStopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callback.MsgName, "TaskChainError", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callback.MsgName, "AllTasksCompleted", StringComparison.OrdinalIgnoreCase))
        {
            if (IsStaleRunBoundaryCallback(callback, "terminal"))
            {
                return;
            }

            MoveToState(SessionState.Connected, "Session.Callback", callback.MsgName);
            return;
        }

        if (string.Equals(callback.MsgName, "InitFailed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callback.MsgName, "Destroyed", StringComparison.OrdinalIgnoreCase))
        {
            if (IsStaleRunBoundaryCallback(callback, "lifecycle"))
            {
                return;
            }

            _lastSuccessfulConnectionInfo = null;
            ClearActiveConnectionTarget();
            MoveToState(SessionState.Idle, "Session.Callback", callback.MsgName);
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
            PublishConnectionCallbackStatus(envelope, what);
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

            PublishConnectionCallbackStatus(envelope, what);
            if (CurrentState == SessionState.Connecting)
            {
                if (hasCallbackConnectionInfo)
                {
                    lock (_connectionOperationGate)
                    {
                        _activeCallbackConnectionInfo = callbackConnectionInfo;
                    }
                }

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
            CoreConnectionInfo? activeTarget;
            lock (_connectionOperationGate)
            {
                activeTarget = _activeConnectionTarget;
            }

            if (TryGetCallbackConnectionInfo(envelope.Payload, out var disconnectConnectionInfo))
            {
                var matchesActiveTarget = activeTarget is not null
                    && CallbackConnectionIdentityMatches(activeTarget, disconnectConnectionInfo);
                var matchesLastSuccessful = _lastSuccessfulConnectionInfo is not null
                    && CallbackConnectionIdentityMatches(_lastSuccessfulConnectionInfo, disconnectConnectionInfo);
                if (!matchesActiveTarget
                    && !matchesLastSuccessful
                    && (activeTarget is not null || _lastSuccessfulConnectionInfo is not null))
                {
                    _logService.Warn(
                        $"Session.Callback ignored stale ConnectionInfo:Disconnect for address={disconnectConnectionInfo.Address}, config={disconnectConnectionInfo.ConnectConfig}.");
                    return;
                }
            }
            else if (activeTarget is not null && CurrentState == SessionState.Connecting)
            {
                _logService.Warn(
                    "Session.Callback ignored ConnectionInfo:Disconnect without connection details while a new connect is pending.");
                return;
            }

            PublishConnectionCallbackStatus(envelope, what);
            var shouldStopNative = CurrentState is SessionState.Running or SessionState.Stopping;
            _lastSuccessfulConnectionInfo = null;
            ClearActiveConnectionTarget();
            if (CurrentState is SessionState.Running or SessionState.Stopping)
            {
                _ = StopAsync();
            }
            MoveToState(SessionState.Idle, "Session.Callback", "ConnectionInfo:Disconnect");
            if (shouldStopNative)
            {
                TrackPendingNativeStop();
            }
            return;
        }

        if (string.Equals(what, "UnsupportedResolution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "ResolutionError", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "TouchModeNotAvailable", StringComparison.OrdinalIgnoreCase))
        {
            CoreConnectionInfo? activeTarget;
            lock (_connectionOperationGate)
            {
                activeTarget = _activeConnectionTarget;
            }

            if (TryGetCallbackConnectionInfo(envelope.Payload, out var failureConnectionInfo)
                && !IsCallbackConnectionCurrent(activeTarget, failureConnectionInfo))
            {
                _logService.Warn(
                    $"Session.Callback ignored stale ConnectionInfo:{what} for address={failureConnectionInfo.Address}, config={failureConnectionInfo.ConnectConfig}.");
                return;
            }

            PublishConnectionCallbackStatus(envelope, what);
            _lastSuccessfulConnectionInfo = null;
            if (CurrentState == SessionState.Connected)
            {
                MoveToState(SessionState.Idle, "Session.Callback", $"ConnectionInfo:{what}");
            }

            _logService.Warn(
                $"Session.Callback connection invalidated by ConnectionInfo:{what}; the next link start will reconnect.");
            return;
        }

        if (string.Equals(what, "UnsupportedResolution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "ResolutionError", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "TouchModeNotAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _lastSuccessfulConnectionInfo = null;
            ClearActiveConnectionTarget();
            if (CurrentState is SessionState.Running or SessionState.Stopping)
            {
                _ = StopAsync();
            }
            MoveToState(SessionState.Idle, "Session.Callback", $"ConnectionInfo:{what}");
            return;
        }

        if (string.Equals(what, "ScreencapCost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "FastestWayToScreencap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "Reconnecting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(what, "ScreencapFailed", StringComparison.OrdinalIgnoreCase))
        {
            PublishConnectionCallbackStatus(envelope, what);
            return;
        }

        PublishConnectionCallbackStatus(envelope, what);
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

    // Run-lifecycle callbacks enqueued before the latest run boundary (start, stop, or
    // tainted-queue reset) are stale: terminal events of a stopped batch precede its
    // AsstStop return, and tainted-queue recovery destroys the old instance before the
    // bridge bumps its callback generation, so a Destroyed emitted by that AsstDestroy
    // passes the generation filter of the bridge. A stale TaskChainStart processed after
    // a stop or reset would likewise resurrect a phantom Running state. None of them may
    // move the session out of a run that started after they were enqueued, nor back into
    // a run that already ended. Bridges that do not tag sequences (Sequence 0) keep
    // previous behavior.
    private bool IsStaleRunBoundaryCallback(CoreCallbackEvent callback, string category)
    {
        var runBoundaryWatermark = Interlocked.Read(ref _runBoundaryCallbackWatermark);
        if (callback.Sequence <= 0 || callback.Sequence > runBoundaryWatermark)
        {
            return false;
        }

        _logService.Warn(
            $"Session.Callback ignored stale {category} callback {callback.MsgName}({callback.MsgId}) enqueued before the latest run boundary; seq={callback.Sequence}, watermark={runBoundaryWatermark}.");
        return true;
    }

    private void CaptureRunBoundaryCallbackWatermark()
    {
        // All callbacks of a previously stopped batch are enqueued before its AsstStop
        // returned, hence before this boundary; snapshotting the enqueue counter here lets
        // the callback pump recognize those stale events even when pump latency delays
        // their processing past the boundary's state transition.
        Interlocked.Exchange(ref _runBoundaryCallbackWatermark, _bridge.EnqueuedCallbackSequence);
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

    private static string NormalizeConnectionText(string? value) => (value ?? string.Empty).Trim();

    private static string NormalizeConnectionAddress(string? value)
        => NormalizeConnectionText(value).Replace(" ", string.Empty, StringComparison.Ordinal);

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
            NormalizeConnectionAddress(connectionInfo.Address),
            NormalizeConnectionText(connectionInfo.ConnectConfig),
            NormalizeConnectionPath(connectionInfo.AdbPath),
            NormalizeConnectionExtras(connectionInfo.Extras),
            connectionInfo.Timeout ?? fallbackTimeout,
            connectionInfo.DiscoverRuntimeCandidates);
    }

    private static bool ConnectionInfoMatches(
        CoreConnectionInfo left,
        CoreConnectionInfo right,
        bool compareExtras)
    {
        var rightConfig = NormalizeConnectionText(right.ConnectConfig);
        var rightAdbPath = NormalizeConnectionPath(right.AdbPath);
        return string.Equals(
                NormalizeConnectionAddress(left.Address),
                NormalizeConnectionAddress(right.Address),
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

        var address = NormalizeConnectionAddress(GetJsonString(details, "address"));
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

    private static string? GetJsonString(JsonObject? payload, string key)
    {
        if (payload is null || !payload.TryGetPropertyValue(key, out var node) || node is null)
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
            if (await IsReloadSafeSnapshotAsync(cancellationToken).ConfigureAwait(false))
            {
                return CoreResult<bool>.Ok(true);
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

    private async Task<bool> IsReloadSafeSnapshotAsync(CancellationToken cancellationToken)
    {
        var currentState = CurrentState;
        if (IsReloadHardBlockedState(currentState))
        {
            return false;
        }

        var runtimeResult = await _bridge.GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (runtimeResult.Success)
        {
            return runtimeResult.Value is { Running: false };
        }

        // Runtime probes can fail transiently during reconnect/dispose windows.
        // Preserve the existing best-effort behavior only for idle/connected states.
        return !IsReloadSoftBlockedState(currentState);
    }

    private CoreResult<bool> ReloadTimeoutFailure()
        => CoreResult<bool>.Fail(new CoreError(
            CoreErrorCode.ConnectTimeout,
            $"Timed out waiting for session to become idle before resource reload. state={CurrentState}"));

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

    private async Task<CoreResult<int>> AppendCoreTasksCoreAsync(
        IEnumerable<CoreTaskRequest> coreTasks,
        CancellationToken cancellationToken)
    {
        if (_taskQueueTainted)
        {
            return CoreResult<int>.Fail(CreateReconnectRequiredError());
        }

        ClearTaskIdMappings();
        var index = 0;
        foreach (var task in coreTasks)
        {
            await ThrowIfAppendCanceledAsync(index, cancellationToken, "core append canceled").ConfigureAwait(false);
            CoreResult<int> appendResult;
            try
            {
                appendResult = await _bridge.AppendTaskAsync(task, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await ResetTaintedTaskQueueAsync("core append threw").ConfigureAwait(false);
                throw;
            }
            if (!appendResult.Success)
            {
                return await FailAppendBatchAsync(appendResult.Error!, "core append failed").ConfigureAwait(false);
            }

            // Batch ordinals double as queue indices for callback task-id
            // resolution only; not marked as a profile batch (see
            // AppendAndStartCoreTaskAsync).
            SetTaskIdMapping(appendResult.Value, index);
            index += 1;
            await ThrowIfAppendCanceledAsync(index, cancellationToken, "core append canceled").ConfigureAwait(false);
        }

        return CoreResult<int>.Ok(index);
    }

    private async Task<CoreResult<bool>> ApplyStartInstanceOptionsCoreAsync(
        CoreInstanceOptions instanceOptions,
        CancellationToken cancellationToken)
    {
        if (instanceOptions.IsEmpty)
        {
            return CoreResult<bool>.Ok(true);
        }

        var apply = await ApplyInstanceOptionsAsync(instanceOptions, cancellationToken).ConfigureAwait(false);
        return apply.Success || apply.Error?.Code is CoreErrorCode.NotSupported
            ? CoreResult<bool>.Ok(true)
            : apply;
    }

    private async Task<CoreResult<int>> StartAppendedTasksCoreAsync(
        int appended,
        string operation,
        CancellationToken cancellationToken)
    {
        if (appended <= 0)
        {
            return CoreResult<int>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "No runnable tasks were appended."));
        }

        CoreResult<bool> startResult;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureRunBoundaryCallbackWatermark();
            startResult = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ResetTaintedTaskQueueAsync($"{operation} start threw after append").ConfigureAwait(false);
            throw;
        }

        if (!startResult.Success)
        {
            await ResetTaintedTaskQueueAsync($"{operation} start failed after append").ConfigureAwait(false);
            return CoreResult<int>.Fail(startResult.Error!);
        }

        MoveToState(SessionState.Running, "Session.AppendAndStart", $"{operation}-started");
        return CoreResult<int>.Ok(appended);
    }

    private static CoreError CreateReconnectRequiredError()
        => new(
            CoreErrorCode.StartFailed,
            "Task queue was reset after an interrupted append; reconnect before starting.");

    private static CoreResult<int> CreateAppendStartWhileRunningError(SessionState state)
        => CoreResult<int>.Fail(new CoreError(
            CoreErrorCode.StartFailed,
            $"Session state `{state}` cannot append and start new tasks; stop the current run first."));

    private async Task ThrowIfAppendCanceledAsync(int appended, CancellationToken cancellationToken, string reason)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (appended > 0)
        {
            await ResetTaintedTaskQueueAsync(reason).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<CoreResult<int>> FailAppendBatchAsync(CoreError error, string reason)
    {
        await ResetTaintedTaskQueueAsync(reason).ConfigureAwait(false);
        return CoreResult<int>.Fail(error);
    }

    private async Task ResetTaintedTaskQueueAsync(string reason)
    {
        _taskQueueTainted = true;
        ClearTaskIdMappings();
        _lastSuccessfulConnectionInfo = null;
        ClearActiveConnectionTarget();
        MarkIdleConnectionCallbacksStale();
        CaptureRunBoundaryCallbackWatermark();
        MoveToState(SessionState.Idle, "Session.Append", reason);

        var reset = await RecoverTaintedTaskQueueCoreAsync(CancellationToken.None).ConfigureAwait(false);
        if (reset.Success)
        {
            _logService.Warn("Session append batch was reset; reconnect before starting tasks.");
        }
        else
        {
            _logService.Warn(
                $"Session append batch remains tainted after cleanup failure: {reset.Error?.Code} {reset.Error?.Message}");
        }
    }

    private Task<CoreResult<bool>> RecoverAbandonedStopAsync(CancellationToken cancellationToken)
        => _bridge is IMaaCoreBridgeRecovery recovery
            ? recovery.RecoverAbandonedStopAsync(cancellationToken)
            : _bridge.RecoverFromAbandonedStopAsync(cancellationToken);

    private Task<CoreResult<bool>> RecoverTaintedTaskQueueCoreAsync(CancellationToken cancellationToken)
        => _bridge is IMaaCoreBridgeQueueRecovery recovery
            ? recovery.RecoverTaintedTaskQueueAsync(cancellationToken)
            : _bridge.RecoverFromTaintedTaskQueueAsync(cancellationToken);

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
            _activeCallbackConnectionInfo = null;
            _activeConnectionGeneration = 0;
        }
    }

    private CoreConnectionInfo CompleteActiveCallbackConnectionInfo(CoreConnectionInfo requested)
    {
        CoreConnectionInfo? callbackInfo;
        lock (_connectionOperationGate)
        {
            callbackInfo = _activeCallbackConnectionInfo;
        }

        if (callbackInfo is null)
        {
            return requested;
        }

        return requested with
        {
            Address = string.IsNullOrWhiteSpace(callbackInfo.Address) ? requested.Address : callbackInfo.Address,
            ConnectConfig = string.IsNullOrWhiteSpace(callbackInfo.ConnectConfig) ? requested.ConnectConfig : callbackInfo.ConnectConfig,
            AdbPath = callbackInfo.AdbPath ?? requested.AdbPath,
        };
    }

    private Task PendingNativeStopSnapshot
    {
        get
        {
            lock (_connectionOperationGate)
            {
                return _pendingNativeStopTask;
            }
        }
    }

    private void TrackPendingNativeStop()
    {
        lock (_connectionOperationGate)
        {
            _pendingNativeStopTask = RunPendingNativeStopAfterAsync(_pendingNativeStopTask);
        }
    }

    private async Task RunPendingNativeStopAfterAsync(Task previous)
    {
        if (!previous.IsCompleted)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.Warn($"Chained native stop after disconnect observed a previous stop failure: {ex.Message}");
            }
        }

        await StopNativeAfterDisconnectAsync().ConfigureAwait(false);
    }

    private async Task StopNativeAfterDisconnectAsync()
    {
        try
        {
            var result = await _bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                _logService.Warn($"Native stop after disconnect failed: {result.Error?.Code} {result.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Native stop after disconnect threw: {ex.Message}");
        }
    }

    private void PublishConnectionCallbackStatus(SessionCallbackEnvelope envelope, string what)
    {
        var details = envelope.Payload?["details"] as JsonObject;
        var status = new ConnectionCallbackStatus(
            what,
            GetJsonString(details, "address"),
            GetJsonString(details, "adb"),
            GetJsonString(details, "config"),
            GetJsonInt(details, "times") is { } times ? times + 1 : null,
            GetJsonInt(details, "width"),
            GetJsonInt(details, "height"),
            GetJsonString(details, "method"));
        LastConnectionCallbackStatus = status;
        ConnectionCallbackStatusChanged?.Invoke(status);
    }

    private static int? GetJsonInt(JsonObject? payload, string key)
    {
        if (payload is null || !payload.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out int number))
        {
            return number;
        }

        return value.TryGetValue(out string? text) && int.TryParse(text, out number) ? number : null;
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
        CoreConnectionInfo? previousTarget;
        CoreConnectionInfo? rejectedTarget;
        lock (_connectionOperationGate)
        {
            activeTarget = _activeConnectionTarget;
            previousTarget = _previousConnectionTarget;
            rejectedTarget = _lastRejectedConnectionTarget;
        }

        if (callbackConnectionInfo is not null)
        {
            if (activeTarget is not null && !CallbackConnectionIdentityMatches(activeTarget, callbackConnectionInfo))
            {
                var isActualPendingTarget = CurrentState == SessionState.Connecting
                    && CallbackConnectionConfigMatches(activeTarget, callbackConnectionInfo)
                    && (previousTarget is null
                        || !CallbackConnectionIdentityMatches(previousTarget, callbackConnectionInfo))
                    && (rejectedTarget is null
                        || !CallbackConnectionIdentityMatches(rejectedTarget, callbackConnectionInfo));
                if (!isActualPendingTarget)
                {
                    RememberRejectedConnectionTarget(callbackConnectionInfo);
                    _logService.Warn(
                        $"Session.Callback ignored stale ConnectionInfo:{what} for address={callbackConnectionInfo.Address}, config={callbackConnectionInfo.ConnectConfig}; active address={activeTarget.Address}, config={activeTarget.ConnectConfig}.");
                    return false;
                }
            }

            if (_lastSuccessfulConnectionInfo is not null
                && !CallbackConnectionIdentityMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo))
            {
                var isActualCompletedTarget = CurrentState == SessionState.Connecting
                    || ((string.Equals(what, "Connected", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(what, "Reconnected", StringComparison.OrdinalIgnoreCase))
                        && CallbackConnectionConfigMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo)
                        && (previousTarget is null
                            || !CallbackConnectionIdentityMatches(previousTarget, callbackConnectionInfo))
                        && (rejectedTarget is null
                            || !CallbackConnectionIdentityMatches(rejectedTarget, callbackConnectionInfo)));
                if (!isActualCompletedTarget)
                {
                    RememberRejectedConnectionTarget(callbackConnectionInfo);
                    _logService.Warn(
                        $"Session.Callback ignored stale ConnectionInfo:{what} for address={callbackConnectionInfo.Address}, config={callbackConnectionInfo.ConnectConfig}; current address={_lastSuccessfulConnectionInfo.Address}, config={_lastSuccessfulConnectionInfo.ConnectConfig}.");
                    return false;
                }
            }

            if (_lastSuccessfulConnectionInfo is not null || activeTarget is not null)
            {
                return true;
            }

            RememberRejectedConnectionTarget(callbackConnectionInfo);
            return false;
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

    private void RememberRejectedConnectionTarget(CoreConnectionInfo connectionInfo)
    {
        lock (_connectionOperationGate)
        {
            _lastRejectedConnectionTarget = connectionInfo;
        }
    }

    private CoreConnectionInfo CompleteCallbackConnectionInfo(CoreConnectionInfo callbackConnectionInfo)
    {
        CoreConnectionInfo? activeTarget;
        lock (_connectionOperationGate)
        {
            activeTarget = _activeConnectionTarget;
        }

        if (activeTarget is not null
            && CallbackConnectionIdentityMatches(activeTarget, callbackConnectionInfo))
        {
            return activeTarget with
            {
                Address = callbackConnectionInfo.Address,
                ConnectConfig = string.IsNullOrWhiteSpace(callbackConnectionInfo.ConnectConfig)
                    ? activeTarget.ConnectConfig
                    : callbackConnectionInfo.ConnectConfig,
                AdbPath = callbackConnectionInfo.AdbPath ?? activeTarget.AdbPath,
            };
        }

        if (_lastSuccessfulConnectionInfo is not null
            && CallbackConnectionConfigMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo))
        {
            return _lastSuccessfulConnectionInfo with
            {
                Address = callbackConnectionInfo.Address,
                ConnectConfig = string.IsNullOrWhiteSpace(callbackConnectionInfo.ConnectConfig)
                    ? _lastSuccessfulConnectionInfo.ConnectConfig
                    : callbackConnectionInfo.ConnectConfig,
                AdbPath = callbackConnectionInfo.AdbPath ?? _lastSuccessfulConnectionInfo.AdbPath,
            };
        }

        return callbackConnectionInfo;
    }

    private bool IsCallbackConnectionCurrent(CoreConnectionInfo? activeTarget, CoreConnectionInfo callbackConnectionInfo)
    {
        if (activeTarget is not null
            && CallbackConnectionIdentityMatches(activeTarget, callbackConnectionInfo))
        {
            return true;
        }

        if (_lastSuccessfulConnectionInfo is not null
            && CallbackConnectionIdentityMatches(_lastSuccessfulConnectionInfo, callbackConnectionInfo))
        {
            return true;
        }

        return activeTarget is null && _lastSuccessfulConnectionInfo is null;
    }

    private static bool CallbackConnectionIdentityMatches(CoreConnectionInfo expected, CoreConnectionInfo callback)
    {
        return string.Equals(
                NormalizeConnectionAddress(expected.Address),
                NormalizeConnectionAddress(callback.Address),
                StringComparison.Ordinal)
            && CallbackConnectionConfigMatches(expected, callback);
    }

    private static bool CallbackConnectionConfigMatches(CoreConnectionInfo expected, CoreConnectionInfo callback)
    {
        var callbackConfig = NormalizeConnectionText(callback.ConnectConfig);
        return string.IsNullOrWhiteSpace(callbackConfig)
            || string.Equals(
                NormalizeConnectionText(expected.ConnectConfig),
                callbackConfig,
                StringComparison.Ordinal);
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
