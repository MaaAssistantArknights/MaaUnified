using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;
using MAAUnified.Platform;

namespace MAAUnified.Application.Services.Features;

public sealed class ConnectFeatureService : IConnectFeatureService
{
    private readonly UnifiedSessionService _sessionService;
    private readonly UnifiedConfigurationService _configService;
    private readonly UiLogService? _logService;
    private readonly IMaaCoreBridge? _bridge;
    private readonly string? _runtimeBaseDirectory;
    private readonly bool _enableQuickConnectionPrecheck;
    private IMacRawByNcRiskConnectionPromptService _macRawByNcRiskPromptService;
    private readonly SemaphoreSlim _lifecycleOperationLock = new(1, 1);
    private readonly object _lifecycleOperationGate = new();
    private readonly object _macRawByNcRiskDecisionGate = new();
    private readonly AsyncLocal<int> _lifecycleOperationDepth = new();
    private CancellationTokenSource? _activeLifecycleOperationCts;
    private string? _activeLifecycleOperation;
    private MacRawByNcRiskForceRunCacheEntry? _macRawByNcRiskForceRunCache;
    private const string DefaultTouchMode = "MaaFwAdb";
    private static readonly TimeSpan DefaultConnectBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LifecycleOperationWaitTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan QuickTcpProbeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan QuickAdbDevicesTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MacRawByNcForceRunCacheTtl = TimeSpan.FromMinutes(5);
    private const string MaaFwAdbTouchMode = "MaaFwAdb";
    private const string MacRawByNcGuardAppliedRecommendedStrategy = "temporary-macos-rawbync-guard:applied-recommended-profile";
    private const string MacRawByNcGuardForceRunStrategy = "temporary-macos-rawbync-guard:force-run";
    private const string MacRawByNcGuardReasonUserForced = "user-forced-risk-combination";
    private const string MacRawByNcGuardReasonUserAppliedRecommendation = "user-applied-recommended-combination";
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultAddressByConnectConfig =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = [string.Empty],
            ["BlueStacks"] = ["127.0.0.1:5555", "127.0.0.1:5556", "127.0.0.1:5565", "127.0.0.1:5575", "127.0.0.1:5585", "127.0.0.1:5595", "127.0.0.1:5554"],
            ["MuMuEmulator12"] = ["127.0.0.1:16384", "127.0.0.1:16416", "127.0.0.1:16448", "127.0.0.1:16480", "127.0.0.1:16512", "127.0.0.1:16544", "127.0.0.1:16576"],
            ["LDPlayer"] = ["emulator-5554", "emulator-5556", "emulator-5558", "emulator-5560", "127.0.0.1:5555", "127.0.0.1:5557", "127.0.0.1:5559", "127.0.0.1:5561"],
            ["AVD"] = ["emulator-5554", "emulator-5556"],
            ["Nox"] = ["127.0.0.1:62001", "127.0.0.1:59865"],
            ["XYAZ"] = ["127.0.0.1:21503"],
            ["WSA"] = ["127.0.0.1:58526"],
            ["MacPlayTools"] = [PlayCoverConnectConfigResolver.DefaultPlayToolsAddress],
            ["CompatMac"] = [PlayCoverConnectConfigResolver.DefaultPlayToolsAddress],
            ["MacSCK"] = [PlayCoverConnectConfigResolver.DefaultPlayToolsAddress],
            ["MacBGR"] = [PlayCoverConnectConfigResolver.DefaultPlayToolsAddress],
        };

    public ConnectFeatureService(
        UnifiedSessionService sessionService,
        UnifiedConfigurationService configService,
        UiLogService? logService = null,
        IMaaCoreBridge? bridge = null,
        string? runtimeBaseDirectory = null,
        IMacRawByNcRiskConnectionPromptService? macRawByNcRiskPromptService = null,
        bool enableQuickConnectionPrecheck = true)
    {
        _sessionService = sessionService;
        _configService = configService;
        _logService = logService;
        _bridge = bridge;
        _runtimeBaseDirectory = string.IsNullOrWhiteSpace(runtimeBaseDirectory)
            ? null
            : RuntimeLayout.NormalizeDirectory(runtimeBaseDirectory);
        _macRawByNcRiskPromptService = macRawByNcRiskPromptService
            ?? NoOpMacRawByNcRiskConnectionPromptService.Instance;
        _enableQuickConnectionPrecheck = enableQuickConnectionPrecheck;
    }

    public IMacRawByNcRiskConnectionPromptService MacRawByNcRiskPromptService
    {
        get => _macRawByNcRiskPromptService;
        set => _macRawByNcRiskPromptService = value ?? NoOpMacRawByNcRiskConnectionPromptService.Instance;
    }

    public Task<CoreResult<bool>> ValidateAndConnectAsync(string address, string config, string? adbPath, CancellationToken cancellationToken = default)
        => ValidateAndConnectAsync(address, config, adbPath, instanceOptions: null, cancellationToken);

    public async Task<CoreResult<bool>> ValidateAndConnectAsync(
        string address,
        string config,
        string? adbPath,
        CoreInstanceOptions? instanceOptions,
        CancellationToken cancellationToken = default)
    {
        var connectionInfo = new CoreConnectionInfo(
            address,
            config,
            adbPath,
            BuildConnectionExtras(instanceOptions, connectionExtras: null),
            DefaultConnectBudget);
        return await ValidateAndConnectAsync(connectionInfo, cancellationToken: cancellationToken);
    }

    public async Task<CoreResult<bool>> ValidateAndConnectAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default)
    {
        using var operation = await TryBeginLifecycleOperationAsync("Connect", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                BuildLifecycleOperationBusyMessage("Connect")));
        }

        try
        {
            return await ValidateAndConnectCoreAsync(connectionInfo, "Connect", operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && operation.Token.IsCancellationRequested)
        {
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.ConnectTimeout,
                "Connect was superseded by a newer lifecycle operation."));
        }
    }

    private async Task<CoreResult<bool>> ValidateAndConnectCoreAsync(
        CoreConnectionInfo connectionInfo,
        string sourceScope,
        CancellationToken cancellationToken,
        MacRawByNcRiskPromptSession? riskPromptSession = null)
    {
        if (string.IsNullOrWhiteSpace(connectionInfo.Address))
        {
            return CoreResult<bool>.Fail(new CoreError(CoreErrorCode.InvalidRequest, "Address cannot be empty."));
        }

        var requestedConnectConfig = PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(
            connectionInfo.ConnectConfig,
            playCoverScreencapMode: null);
        var requestedConnectionInfo = connectionInfo with
        {
            ConnectConfig = requestedConnectConfig,
            AdbPath = PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(requestedConnectConfig)
                ? null
                : connectionInfo.AdbPath,
        };

        if (MacBundledAdbPolicy.IsBundledAdbPath(requestedConnectionInfo.AdbPath)
            && !MacBundledAdbPolicy.IsCurrentTermsAccepted(_configService.CurrentConfig))
        {
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "macOS bundled ADB requires Android SDK Platform-Tools terms acceptance before use."));
        }

        if (!MacBundledAdbPolicy.TryResolveAdbPathForConnect(requestedConnectionInfo.AdbPath, out var effectiveAdbPath, out var adbDiagnostic))
        {
            _logService?.Warn(adbDiagnostic ?? "ADB path is invalid for the current platform.");
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                adbDiagnostic ?? "ADB path is invalid for the current platform."));
        }

        if (MacBundledAdbPolicy.IsSupportedPlatform)
        {
            _logService?.Debug(MacBundledAdbPolicy.BuildResolutionContext(requestedConnectionInfo.AdbPath, effectiveAdbPath));
        }

        var normalized = NormalizeConnectionInfo(
            requestedConnectionInfo with
            {
                Extras = BuildConnectionExtras(instanceOptions: null, requestedConnectionInfo.Extras),
            },
            effectiveAdbPath);

        var riskResolution = await ResolveMacRawByNcRiskConnectionAsync(normalized, sourceScope, cancellationToken, riskPromptSession)
            .ConfigureAwait(false);
        if (!riskResolution.Success)
        {
            return CoreResult<bool>.Fail(riskResolution.Error!);
        }

        normalized = riskResolution.Value!;
        LogEffectiveConnectionConfiguration(normalized);

        var quickPrecheckPassed = false;
        if (_enableQuickConnectionPrecheck && ShouldRunQuickConnectionPrecheck(normalized))
        {
            var quickScreen = await QuickScreenConnectionAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (!quickScreen.Success)
            {
                return quickScreen;
            }

            quickPrecheckPassed = true;
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(NormalizeConnectBudget(normalized.Timeout));
        try
        {
            var connect = await _sessionService.ConnectAsync(normalized, budgetCts.Token).ConfigureAwait(false);
            return await EnrichEarlyCoreConnectFailureAsync(connect, normalized, quickPrecheckPassed, budgetCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.ConnectTimeout,
                $"Connect timed out after {NormalizeConnectBudget(normalized.Timeout).TotalSeconds:N0}s. "
                + "Possible causes: emulator not running, wrong address/port, ADB unreachable, touch/controller fallback unavailable, or MaaCore screencap negotiation stuck."));
        }
    }

    private async Task<CoreResult<CoreConnectionInfo>> ResolveMacRawByNcRiskConnectionAsync(
        CoreConnectionInfo connectionInfo,
        string sourceScope,
        CancellationToken cancellationToken,
        MacRawByNcRiskPromptSession? riskPromptSession)
    {
        // Temporary product-side guard while MaaCore's macOS RawByNc/POSIX path is not fully fixed:
        // warn the user at the shared connection boundary instead of silently rewriting their profile.
        var extras = connectionInfo.Extras ?? CoreConnectionExtras.Empty;
        if (!ShouldPromptMacRawByNcRisk(connectionInfo, extras))
        {
            return CoreResult<CoreConnectionInfo>.Ok(connectionInfo);
        }

        var riskKey = MacRawByNcRiskConnectionKey.Create(connectionInfo, extras, sourceScope);
        MacRawByNcRiskConnectionDecision decision;
        if (riskPromptSession?.HasDecision == true)
        {
            decision = riskPromptSession.Decision;
        }
        else if (TryGetRememberedMacRawByNcForceRun(riskKey))
        {
            decision = MacRawByNcRiskConnectionDecision.ForceRun;
        }
        else
        {
            var prompt = new MacRawByNcRiskConnectionPrompt(
                sourceScope,
                connectionInfo.Address,
                connectionInfo.ConnectConfig,
                extras.TouchMode,
                extras.AdbLiteEnabled,
                MaaFwAdbTouchMode,
                RecommendedAdbLiteEnabled: true,
                ReadCurrentLanguage());
            decision = await _macRawByNcRiskPromptService.ConfirmAsync(prompt, cancellationToken).ConfigureAwait(false);
            riskPromptSession?.Remember(decision);
            if (decision == MacRawByNcRiskConnectionDecision.ForceRun)
            {
                RememberMacRawByNcForceRun(riskKey);
            }
        }

        return decision switch
        {
            MacRawByNcRiskConnectionDecision.ApplyRecommended => await ApplyMacRawByNcRecommendedConnectionAsync(
                connectionInfo,
                extras,
                cancellationToken).ConfigureAwait(false),
            MacRawByNcRiskConnectionDecision.ForceRun => CoreResult<CoreConnectionInfo>.Ok(MarkMacRawByNcForceRun(connectionInfo, extras)),
            _ => CoreResult<CoreConnectionInfo>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "Connection canceled because the macOS RawByNc risk prompt was closed.")),
        };
    }

    private async Task<CoreResult<CoreConnectionInfo>> ApplyMacRawByNcRecommendedConnectionAsync(
        CoreConnectionInfo connectionInfo,
        CoreConnectionExtras configuredExtras,
        CancellationToken cancellationToken)
    {
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return CoreResult<CoreConnectionInfo>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "Current profile is missing; cannot apply the recommended macOS RawByNc connection settings."));
        }

        var configuredTouch = configuredExtras.TouchMode;
        var configuredAdbLite = configuredExtras.AdbLiteEnabled;
        profile.Values["AdbLiteEnabled"] = JsonValue.Create(true);
        _configService.RevalidateCurrentConfig(logIssues: false);
        await _configService.SaveAsync(cancellationToken).ConfigureAwait(false);
        ClearRememberedMacRawByNcForceRun();

        _logService?.Warn(
            "macOS RawByNc temporary prompt applied recommendation: "
            + $"profile={_configService.CurrentConfig.CurrentProfile}, touch={configuredTouch ?? "<default>"}, adbLite=True. "
            + "This is a temporary measure until MaaCore fully fixes the macOS RawByNc/POSIX path.");

        var effectiveExtras = configuredExtras with
        {
            AdbLiteEnabled = true,
            FallbackStrategy = MacRawByNcGuardAppliedRecommendedStrategy,
            ConfiguredTouchMode = configuredTouch,
            ConfiguredAdbLiteEnabled = configuredAdbLite,
            FallbackReason = MacRawByNcGuardReasonUserAppliedRecommendation,
        };
        return CoreResult<CoreConnectionInfo>.Ok(connectionInfo with { Extras = effectiveExtras });
    }

    private CoreConnectionInfo MarkMacRawByNcForceRun(
        CoreConnectionInfo connectionInfo,
        CoreConnectionExtras configuredExtras)
    {
        _logService?.Warn(
            "macOS RawByNc temporary prompt force-run selected: "
            + $"touch={configuredExtras.TouchMode ?? "<default>"}, adbLite={configuredExtras.AdbLiteEnabled}. "
            + "Core may hit the known macOS RawByNc/POSIX issue; this does not modify the saved profile.");

        return connectionInfo with
        {
            Extras = configuredExtras with
            {
                FallbackStrategy = MacRawByNcGuardForceRunStrategy,
                ConfiguredTouchMode = configuredExtras.TouchMode,
                ConfiguredAdbLiteEnabled = configuredExtras.AdbLiteEnabled,
                FallbackReason = MacRawByNcGuardReasonUserForced,
            },
        };
    }

    public async Task<UiOperationResult> ConnectAsync(string address, string config, string? adbPath, CancellationToken cancellationToken = default)
        => await ConnectAsync(address, config, adbPath, instanceOptions: null, cancellationToken);

    public async Task<UiOperationResult> ConnectAsync(
        string address,
        string config,
        string? adbPath,
        CoreInstanceOptions? instanceOptions,
        CancellationToken cancellationToken = default)
    {
        var result = await ValidateAndConnectAsync(address, config, adbPath, instanceOptions, cancellationToken);
        return UiOperationResult.FromCore(result, $"Connected to {address}");
    }

    public async Task<UiOperationResult> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CoreInstanceOptions? instanceOptions,
        CancellationToken cancellationToken = default)
    {
        var result = await ValidateAndConnectAsync(
            connectionInfo with
            {
                Extras = BuildConnectionExtras(instanceOptions, connectionInfo.Extras),
            },
            cancellationToken);
        return UiOperationResult.FromCore(result, $"Connected to {connectionInfo.Address}");
    }

    public async Task<UiOperationResult> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default)
    {
        return await ConnectAsync(connectionInfo, instanceOptions: null, cancellationToken);
    }

    public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
        CoreInstanceOptions? instanceOptions = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveEffectiveInstanceOptions(instanceOptions);
        if (resolved.IsEmpty)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        return _sessionService.ApplyInstanceOptionsAsync(resolved, cancellationToken);
    }

    public async Task<UiOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await TryBeginLifecycleOperationAsync("Start", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, BuildLifecycleOperationBusyMessage("Start"));
        }

        var apply = await ApplyResolvedInstanceOptionsAsync(instanceOptions: null, operation.Token);
        if (!apply.Success)
        {
            return UiOperationResult.FromCore(apply, "Core instance options updated.");
        }

        var result = await _sessionService.StartAsync(operation.Token);
        return UiOperationResult.FromCore(result, "Task execution started.");
    }

    public async Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionService.CurrentState == SessionState.Connecting)
        {
            var activeOperation = GetActiveLifecycleOperation();
            if (!string.IsNullOrWhiteSpace(activeOperation)
                && !string.Equals(activeOperation, "Connect", StringComparison.Ordinal)
                && !string.Equals(activeOperation, "Stop", StringComparison.Ordinal))
            {
                return UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, BuildLifecycleOperationBusyMessage("Stop"));
            }

            CancelActiveLifecycleOperation("Stop during Connecting");
            var connectingStopResult = await _sessionService.StopAsync(cancellationToken).ConfigureAwait(false);
            return UiOperationResult.FromCore(connectingStopResult, "Task execution stopped.");
        }

        using var operation = await TryBeginLifecycleOperationAsync("Stop", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, BuildLifecycleOperationBusyMessage("Stop"));
        }

        var result = await _sessionService.StopAsync(operation.Token);
        return UiOperationResult.FromCore(result, "Task execution stopped.");
    }

    public UiOperationResult<IReadOnlyList<CoreConnectionInfo>> BuildCurrentProfileConnectionCandidates(
        bool includeConfiguredAddress = true)
    {
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return UiOperationResult<IReadOnlyList<CoreConnectionInfo>>.Fail(
                UiErrorCode.ProfileMissing,
                "Current profile is missing.");
        }

        var storedConnectConfig = ReadProfileString(profile, "ConnectConfig", ConfigurationKeys.ConnectConfig) ?? "General";
        var playCoverScreencapMode = ReadProfileString(profile, "PlayCoverScreencapMode", "PlayCoverScreencapMode");
        var connectConfig = PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(storedConnectConfig, playCoverScreencapMode);
        var configuredAddress = ReadProfileString(profile, "ConnectAddress", ConfigurationKeys.ConnectAddress)
            ?? PlayCoverConnectConfigResolver.ResolveDefaultConnectAddress(connectConfig, "127.0.0.1:5555");
        var adbPath = PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(connectConfig)
            ? null
            : ResolveProfileAdbPath(profile);
        var extras = NormalizeConnectionExtrasForConnectConfig(connectConfig, ResolveConnectionExtrasFromConfig());
        return BuildConnectionCandidates(
            configuredAddress,
            connectConfig,
            adbPath,
            extras,
            ReadProfileBool(profile, "AutoDetect", ConfigurationKeys.AutoDetect, fallback: true),
            ReadProfileBool(profile, "AlwaysAutoDetect", ConfigurationKeys.AlwaysAutoDetect),
            includeConfiguredAddress,
            DefaultConnectBudget);
    }

    public UiOperationResult<IReadOnlyList<CoreConnectionInfo>> BuildConnectionCandidates(
        string configuredAddress,
        string connectConfig,
        string? adbPath,
        CoreConnectionExtras? extras = null,
        bool autoDetect = true,
        bool alwaysAutoDetect = false,
        bool includeConfiguredAddress = true,
        TimeSpan? timeout = null)
    {
        var normalizedConfiguredAddress = NormalizeText(configuredAddress);
        var normalizedConnectConfig = PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(
            NormalizeText(connectConfig),
            playCoverScreencapMode: null);
        var normalizedAdbPath = PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(normalizedConnectConfig)
            ? null
            : NormalizeNullableText(adbPath);
        var normalizedExtras = NormalizeConnectionExtrasForConnectConfig(
            normalizedConnectConfig,
            extras ?? CoreConnectionExtras.Empty);
        var addresses = BuildConnectAddressCandidates(
            normalizedConfiguredAddress,
            normalizedConnectConfig,
            autoDetect,
            alwaysAutoDetect);
        var candidates = new List<CoreConnectionInfo>(addresses.Count);
        foreach (var address in addresses)
        {
            if (!includeConfiguredAddress
                && string.Equals(address, normalizedConfiguredAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(new CoreConnectionInfo(
                address,
                normalizedConnectConfig,
                normalizedAdbPath,
                normalizedExtras,
                timeout ?? DefaultConnectBudget));
        }

        return UiOperationResult<IReadOnlyList<CoreConnectionInfo>>.Ok(candidates, "Connection candidates built.");
    }

    public async Task<ConnectionConnectOperationResult> ConnectCandidatesAsync(
        IReadOnlyList<CoreConnectionInfo> candidates,
        CancellationToken cancellationToken = default)
    {
        using var operation = await TryBeginLifecycleOperationAsync("Connect", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return new ConnectionConnectOperationResult(
                UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, BuildLifecycleOperationBusyMessage("Connect")),
                null,
                []);
        }

        return await ConnectCandidatesCoreAsync(candidates, "Connect", operation.Token).ConfigureAwait(false);
    }

    public async Task<ConnectionScreenshotTestOperationResult> RunScreenshotTestAsync(
        IReadOnlyList<CoreConnectionInfo> candidates,
        int sampleCount = 3,
        CancellationToken cancellationToken = default)
    {
        using var operation = await TryBeginLifecycleOperationAsync("ScreenshotTest", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return new ConnectionScreenshotTestOperationResult(
                UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, BuildLifecycleOperationBusyMessage("ScreenshotTest")),
                null,
                null,
                []);
        }

        if (_bridge is null)
        {
            return new ConnectionScreenshotTestOperationResult(
                UiOperationResult.Fail(UiErrorCode.ConnectFailed, "Core bridge is unavailable for screenshot test."),
                null,
                null,
                []);
        }

        sampleCount = Math.Clamp(sampleCount, 1, 10);
        var connect = await ConnectCandidatesCoreAsync(candidates, "ScreenshotTest", operation.Token).ConfigureAwait(false);
        if (!connect.Success)
        {
            return new ConnectionScreenshotTestOperationResult(
                connect.Result,
                null,
                connect.SuccessfulAddress,
                connect.CandidateFailures);
        }

        var samples = new List<long>(sampleCount);
        byte[]? latestImage = null;
        for (var index = 0; index < sampleCount; index++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var imageResult = await _bridge.GetImageBgrAsync(forceScreencap: true, operation.Token).ConfigureAwait(false);
            watch.Stop();
            if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
            {
                var failure = UiOperationResult.Fail(
                    imageResult.Error?.Code.ToString() ?? UiErrorCode.ConnectFailed,
                    imageResult.Error?.Message ?? "Failed to capture screenshot.",
                    imageResult.Error?.NativeDetails ?? imageResult.Error?.Exception);
                return new ConnectionScreenshotTestOperationResult(
                    failure,
                    null,
                    connect.SuccessfulAddress,
                    connect.CandidateFailures);
            }

            latestImage = imageResult.Value;
            samples.Add(watch.ElapsedMilliseconds);
        }

        return new ConnectionScreenshotTestOperationResult(
            UiOperationResult.Ok("Screenshot test completed."),
            new ConnectionScreenshotTestResult(samples, latestImage ?? []),
            connect.SuccessfulAddress,
            connect.CandidateFailures);
    }

    public async Task<UiOperationResult> WaitAndStopAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (wait <= TimeSpan.Zero)
        {
            return UiOperationResult.Fail(UiErrorCode.InvalidWaitTime, "Wait time must be greater than zero.");
        }

        var initialState = _sessionService.CurrentState;
        if (!IsExecutionActiveState(initialState))
        {
            return UiOperationResult.Ok($"Session already stopped (state={initialState}).");
        }

        var stateExitedTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleSessionStateChanged(SessionState state)
        {
            if (!IsExecutionActiveState(state))
            {
                stateExitedTask.TrySetResult();
            }
        }

        _sessionService.SessionStateChanged += HandleSessionStateChanged;
        try
        {
            if (!IsExecutionActiveState(_sessionService.CurrentState))
            {
                return UiOperationResult.Ok("Task execution already stopped during wait.");
            }

            var delayTask = Task.Delay(wait, cancellationToken);
            var completed = await Task.WhenAny(delayTask, stateExitedTask.Task);
            if (completed == stateExitedTask.Task)
            {
                return UiOperationResult.Ok("Task execution already stopped during wait.");
            }

            await delayTask;
        }
        finally
        {
            _sessionService.SessionStateChanged -= HandleSessionStateChanged;
        }

        if (!IsExecutionActiveState(_sessionService.CurrentState))
        {
            return UiOperationResult.Ok("Task execution already stopped during wait.");
        }

        return await StopAsync(cancellationToken);
    }

    private static bool IsExecutionActiveState(SessionState state)
    {
        return state is SessionState.Running or SessionState.Stopping;
    }

    private async Task<ConnectionConnectOperationResult> ConnectCandidatesCoreAsync(
        IReadOnlyList<CoreConnectionInfo> candidates,
        string sourceScope,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new ConnectionConnectOperationResult(
                UiOperationResult.Fail(UiErrorCode.ConnectFailed, "No connection candidates were available."),
                null,
                []);
        }

        UiOperationResult? lastFailure = null;
        var candidateFailures = new List<ConnectionCandidateAttempt>();
        var riskPromptSession = new MacRawByNcRiskPromptSession();
        foreach (var candidate in candidates)
        {
            UiOperationResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = UiOperationResult.FromCore(
                    await ValidateAndConnectCoreAsync(candidate, sourceScope, cancellationToken, riskPromptSession).ConfigureAwait(false),
                    $"Connected to {candidate.Address}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = UiOperationResult.Fail(
                    UiErrorCode.ConnectFailed,
                    "Connect was superseded by a newer lifecycle operation.");
            }

            if (result.Success)
            {
                return new ConnectionConnectOperationResult(result, candidate.Address, candidateFailures);
            }

            lastFailure = result;
            candidateFailures.Add(new ConnectionCandidateAttempt(candidate.Address, result));
            if (riskPromptSession.HasDecision
                && riskPromptSession.Decision == MacRawByNcRiskConnectionDecision.Cancel)
            {
                break;
            }
        }

        return new ConnectionConnectOperationResult(
            lastFailure ?? UiOperationResult.Fail(UiErrorCode.ConnectFailed, "Connection failed."),
            null,
            candidateFailures);
    }

    private sealed class MacRawByNcRiskPromptSession
    {
        public bool HasDecision { get; private set; }

        public MacRawByNcRiskConnectionDecision Decision { get; private set; }

        public void Remember(MacRawByNcRiskConnectionDecision decision)
        {
            HasDecision = true;
            Decision = decision;
        }
    }

    private bool TryGetRememberedMacRawByNcForceRun(MacRawByNcRiskConnectionKey key)
    {
        lock (_macRawByNcRiskDecisionGate)
        {
            if (_macRawByNcRiskForceRunCache is not { } cache)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - cache.CreatedAtUtc > MacRawByNcForceRunCacheTtl)
            {
                _macRawByNcRiskForceRunCache = null;
                return false;
            }

            return cache.Key.Equals(key);
        }
    }

    private void RememberMacRawByNcForceRun(MacRawByNcRiskConnectionKey key)
    {
        lock (_macRawByNcRiskDecisionGate)
        {
            _macRawByNcRiskForceRunCache = new MacRawByNcRiskForceRunCacheEntry(key, DateTimeOffset.UtcNow);
        }
    }

    private void ClearRememberedMacRawByNcForceRun()
    {
        lock (_macRawByNcRiskDecisionGate)
        {
            _macRawByNcRiskForceRunCache = null;
        }
    }

    private readonly record struct MacRawByNcRiskForceRunCacheEntry(
        MacRawByNcRiskConnectionKey Key,
        DateTimeOffset CreatedAtUtc);

    private readonly record struct MacRawByNcRiskConnectionKey(
        string SourceScope,
        string Address,
        string ConnectConfig,
        string AdbPath,
        string TouchMode,
        bool AdbLiteEnabled)
    {
        public static MacRawByNcRiskConnectionKey Create(
            CoreConnectionInfo connectionInfo,
            CoreConnectionExtras extras,
            string sourceScope)
        {
            return new MacRawByNcRiskConnectionKey(
                NormalizeKeyPart(sourceScope),
                NormalizeKeyPart(connectionInfo.Address),
                NormalizeKeyPart(connectionInfo.ConnectConfig),
                NormalizeKeyPart(connectionInfo.AdbPath),
                NormalizeKeyPart(extras.TouchMode),
                extras.AdbLiteEnabled);
        }

        private static string NormalizeKeyPart(string? value)
            => (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private async Task<LifecycleOperationLease?> TryBeginLifecycleOperationAsync(
        string operationName,
        CancellationToken cancellationToken)
    {
        if (_lifecycleOperationDepth.Value > 0)
        {
            CancellationTokenSource? activeCts;
            lock (_lifecycleOperationGate)
            {
                activeCts = _activeLifecycleOperationCts;
            }

            _lifecycleOperationDepth.Value++;
            return new LifecycleOperationLease(this, activeCts, nested: true);
        }

        if (!await _lifecycleOperationLock.WaitAsync(LifecycleOperationWaitTimeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lifecycleOperationGate)
        {
            _activeLifecycleOperationCts = cts;
            _activeLifecycleOperation = operationName;
        }

        _lifecycleOperationDepth.Value = 1;
        return new LifecycleOperationLease(this, cts, nested: false);
    }

    private void EndLifecycleOperation(CancellationTokenSource? operationCts, bool nested)
    {
        if (nested)
        {
            _lifecycleOperationDepth.Value = Math.Max(0, _lifecycleOperationDepth.Value - 1);
            return;
        }

        var shouldRelease = false;
        lock (_lifecycleOperationGate)
        {
            if (ReferenceEquals(_activeLifecycleOperationCts, operationCts))
            {
                _activeLifecycleOperationCts = null;
                _activeLifecycleOperation = null;
                shouldRelease = true;
            }
        }

        _lifecycleOperationDepth.Value = 0;
        operationCts?.Dispose();
        if (shouldRelease)
        {
            _lifecycleOperationLock.Release();
        }
    }

    private void CancelActiveLifecycleOperation(string reason)
    {
        CancellationTokenSource? cts;
        lock (_lifecycleOperationGate)
        {
            cts = _activeLifecycleOperationCts;
            _activeLifecycleOperationCts = null;
            _activeLifecycleOperation = null;
        }

        try
        {
            cts?.Cancel();
            if (cts is not null)
            {
                _logService?.Info($"Connection lifecycle operation canceled: {reason}.");
            }
        }
        catch (ObjectDisposedException)
        {
            // Operation already completed.
        }

        if (cts is not null)
        {
            _lifecycleOperationLock.Release();
        }
    }

    private string? GetActiveLifecycleOperation()
    {
        lock (_lifecycleOperationGate)
        {
            return _activeLifecycleOperation;
        }
    }

    private string BuildLifecycleOperationBusyMessage(string requestedOperation)
    {
        lock (_lifecycleOperationGate)
        {
            return string.IsNullOrWhiteSpace(_activeLifecycleOperation)
                ? $"Connection lifecycle already running; `{requestedOperation}` could not start."
                : $"Connection lifecycle already running `{_activeLifecycleOperation}`; `{requestedOperation}` could not start.";
        }
    }

    private sealed class LifecycleOperationLease : IDisposable
    {
        private readonly ConnectFeatureService _owner;
        private readonly CancellationTokenSource? _cts;
        private readonly bool _nested;
        private bool _disposed;

        public LifecycleOperationLease(ConnectFeatureService owner, CancellationTokenSource? cts, bool nested)
        {
            _owner = owner;
            _cts = cts;
            _nested = nested;
        }

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndLifecycleOperation(_cts, _nested);
        }
    }

    private CoreConnectionInfo NormalizeConnectionInfo(
        CoreConnectionInfo connectionInfo,
        string? effectiveAdbPath)
    {
        var normalizedConnectConfig = PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(
            NormalizeText(connectionInfo.ConnectConfig),
            playCoverScreencapMode: null);
        var normalized = connectionInfo with
        {
            Address = NormalizeText(connectionInfo.Address),
            ConnectConfig = normalizedConnectConfig,
        };
        var extras = normalized.Extras ?? CoreConnectionExtras.Empty;
        var resolvedAdbPath = PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(normalizedConnectConfig)
            ? null
            : effectiveAdbPath;

        return new CoreConnectionInfo(
            NormalizeText(normalized.Address),
            normalizedConnectConfig,
            NormalizeNullableText(resolvedAdbPath),
            NormalizeConnectionExtrasForConnectConfig(normalizedConnectConfig, extras),
            NormalizeConnectBudget(normalized.Timeout));
    }

    private static CoreConnectionExtras NormalizeConnectionExtrasForConnectConfig(
        string? connectConfig,
        CoreConnectionExtras extras)
    {
        var normalized = NormalizeConnectionExtras(extras);
        return PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(connectConfig)
            ? normalized with
            {
                MacUseBundledAdb = false,
                TouchMode = PlayCoverConnectConfigResolver.ResolveTouchMode(connectConfig, normalized.TouchMode ?? DefaultTouchMode),
            }
            : normalized;
    }

    private static CoreConnectionExtras NormalizeConnectionExtras(CoreConnectionExtras extras)
        => new(
            MacUseBundledAdb: extras.MacUseBundledAdb,
            TouchMode: NormalizeNullableText(extras.TouchMode),
            AdbLiteEnabled: extras.AdbLiteEnabled,
            KillAdbOnExit: extras.KillAdbOnExit,
            MuMu12ExtrasEnabled: extras.MuMu12ExtrasEnabled,
            MuMu12EmulatorPath: NormalizeNullableText(extras.MuMu12EmulatorPath),
            MuMuBridgeConnection: extras.MuMuBridgeConnection,
            MuMu12Index: NormalizeNullableText(extras.MuMu12Index),
            LdPlayerExtrasEnabled: extras.LdPlayerExtrasEnabled,
            LdPlayerEmulatorPath: NormalizeNullableText(extras.LdPlayerEmulatorPath),
            LdPlayerManualSetIndex: extras.LdPlayerManualSetIndex,
            LdPlayerIndex: NormalizeNullableText(extras.LdPlayerIndex),
            AttachWindowScreencapMethod: NormalizeNullableText(extras.AttachWindowScreencapMethod),
            AttachWindowMouseMethod: NormalizeNullableText(extras.AttachWindowMouseMethod),
            AttachWindowKeyboardMethod: NormalizeNullableText(extras.AttachWindowKeyboardMethod),
            ClientType: NormalizeNullableText(extras.ClientType),
            FallbackStrategy: NormalizeNullableText(extras.FallbackStrategy),
            ConfiguredTouchMode: NormalizeNullableText(extras.ConfiguredTouchMode),
            ConfiguredAdbLiteEnabled: extras.ConfiguredAdbLiteEnabled,
            FallbackReason: NormalizeNullableText(extras.FallbackReason),
            FallbackRequiredLibrary: NormalizeNullableText(extras.FallbackRequiredLibrary),
            FallbackRequiredLibraryExists: extras.FallbackRequiredLibraryExists);

    private CoreConnectionExtras BuildConnectionExtras(
        CoreInstanceOptions? instanceOptions,
        CoreConnectionExtras? connectionExtras)
    {
        var fromConfig = ResolveConnectionExtrasFromConfig();
        var fallback = instanceOptions ?? new CoreInstanceOptions();
        var incoming = connectionExtras ?? CoreConnectionExtras.Empty;
        var hasIncoming = connectionExtras is not null;
        return new CoreConnectionExtras(
            MacUseBundledAdb: hasIncoming ? incoming.MacUseBundledAdb : fromConfig.MacUseBundledAdb,
            TouchMode: PreferText(incoming.TouchMode, fallback.TouchMode, fromConfig.TouchMode),
            AdbLiteEnabled: hasIncoming ? incoming.AdbLiteEnabled : (fallback.AdbLiteEnabled ?? fromConfig.AdbLiteEnabled),
            KillAdbOnExit: hasIncoming ? incoming.KillAdbOnExit : (fallback.KillAdbOnExit ?? fromConfig.KillAdbOnExit),
            MuMu12ExtrasEnabled: hasIncoming ? incoming.MuMu12ExtrasEnabled : fromConfig.MuMu12ExtrasEnabled,
            MuMu12EmulatorPath: PreferText(incoming.MuMu12EmulatorPath, fromConfig.MuMu12EmulatorPath),
            MuMuBridgeConnection: hasIncoming ? incoming.MuMuBridgeConnection : fromConfig.MuMuBridgeConnection,
            MuMu12Index: PreferText(incoming.MuMu12Index, fromConfig.MuMu12Index),
            LdPlayerExtrasEnabled: hasIncoming ? incoming.LdPlayerExtrasEnabled : fromConfig.LdPlayerExtrasEnabled,
            LdPlayerEmulatorPath: PreferText(incoming.LdPlayerEmulatorPath, fromConfig.LdPlayerEmulatorPath),
            LdPlayerManualSetIndex: hasIncoming ? incoming.LdPlayerManualSetIndex : fromConfig.LdPlayerManualSetIndex,
            LdPlayerIndex: PreferText(incoming.LdPlayerIndex, fromConfig.LdPlayerIndex),
            AttachWindowScreencapMethod: PreferText(incoming.AttachWindowScreencapMethod, fromConfig.AttachWindowScreencapMethod),
            AttachWindowMouseMethod: PreferText(incoming.AttachWindowMouseMethod, fromConfig.AttachWindowMouseMethod),
            AttachWindowKeyboardMethod: PreferText(incoming.AttachWindowKeyboardMethod, fromConfig.AttachWindowKeyboardMethod),
            ClientType: PreferText(incoming.ClientType, fallback.ClientType, fromConfig.ClientType),
            FallbackStrategy: incoming.FallbackStrategy,
            ConfiguredTouchMode: incoming.ConfiguredTouchMode,
            ConfiguredAdbLiteEnabled: incoming.ConfiguredAdbLiteEnabled,
            FallbackReason: incoming.FallbackReason,
            FallbackRequiredLibrary: incoming.FallbackRequiredLibrary,
            FallbackRequiredLibraryExists: incoming.FallbackRequiredLibraryExists);
    }

    private CoreConnectionExtras ResolveConnectionExtrasFromConfig()
    {
        var instanceOptions = ResolveInstanceOptionsFromConfig();
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return new CoreConnectionExtras(
                TouchMode: instanceOptions.TouchMode,
                AdbLiteEnabled: instanceOptions.AdbLiteEnabled ?? false,
                KillAdbOnExit: instanceOptions.KillAdbOnExit ?? false,
                ClientType: instanceOptions.ClientType);
        }

        return new CoreConnectionExtras(
            MacUseBundledAdb: MacBundledAdbPolicy.ReadUseBundledAdb(profile),
            TouchMode: instanceOptions.TouchMode,
            AdbLiteEnabled: instanceOptions.AdbLiteEnabled ?? false,
            KillAdbOnExit: instanceOptions.KillAdbOnExit ?? false,
            MuMu12ExtrasEnabled: ReadProfileBool(profile, "MuMu12ExtrasEnabled", ConfigurationKeys.MuMu12ExtrasEnabled),
            MuMu12EmulatorPath: ReadProfileString(profile, "MuMu12EmulatorPath", ConfigurationKeys.MuMu12EmulatorPath),
            MuMuBridgeConnection: ReadProfileBool(profile, "MuMuBridgeConnection", ConfigurationKeys.MumuBridgeConnection),
            MuMu12Index: ReadProfileString(profile, "MuMu12Index", ConfigurationKeys.MuMu12Index),
            LdPlayerExtrasEnabled: ReadProfileBool(profile, "LdPlayerExtrasEnabled", ConfigurationKeys.LdPlayerExtrasEnabled),
            LdPlayerEmulatorPath: ReadProfileString(profile, "LdPlayerEmulatorPath", ConfigurationKeys.LdPlayerEmulatorPath),
            LdPlayerManualSetIndex: ReadProfileBool(profile, "LdPlayerManualSetIndex", ConfigurationKeys.LdPlayerManualSetIndex),
            LdPlayerIndex: ReadProfileString(profile, "LdPlayerIndex", ConfigurationKeys.LdPlayerIndex),
            AttachWindowScreencapMethod: ReadProfileString(profile, "AttachWindowScreencapMethod", ConfigurationKeys.AttachWindowScreencapMethod),
            AttachWindowMouseMethod: ReadProfileString(profile, "AttachWindowMouseMethod", ConfigurationKeys.AttachWindowMouseMethod),
            AttachWindowKeyboardMethod: ReadProfileString(profile, "AttachWindowKeyboardMethod", ConfigurationKeys.AttachWindowKeyboardMethod),
            ClientType: ReadProfileString(profile, "ClientType", ConfigurationKeys.ClientType));
    }

    private static string? PreferText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private void LogEffectiveConnectionConfiguration(CoreConnectionInfo connectionInfo)
    {
        var extras = connectionInfo.Extras ?? CoreConnectionExtras.Empty;
        var fallbackSummary = DescribeFallbackStrategy(connectionInfo, extras, ResolveRuntimeBaseDirectory());
        _logService?.Info(
            "Effective connection config: "
            + $"adb={connectionInfo.AdbPath ?? "<system adb>"}, address={connectionInfo.Address}, config={connectionInfo.ConnectConfig}, "
            + $"fallback={fallbackSummary}, macBundledAdb={extras.MacUseBundledAdb}, touch={extras.TouchMode ?? "<default>"}, adbLite={extras.AdbLiteEnabled}, killAdbOnExit={extras.KillAdbOnExit}, "
            + $"mumuExtras={extras.MuMu12ExtrasEnabled}:{extras.MuMu12EmulatorPath ?? "<empty>"}:{extras.MuMuBridgeConnection}:{extras.MuMu12Index ?? "<empty>"}, "
            + $"ldExtras={extras.LdPlayerExtrasEnabled}:{extras.LdPlayerEmulatorPath ?? "<empty>"}:{extras.LdPlayerManualSetIndex}:{extras.LdPlayerIndex ?? "<empty>"}, "
            + $"attach={extras.AttachWindowScreencapMethod ?? "<empty>"}:{extras.AttachWindowMouseMethod ?? "<empty>"}:{extras.AttachWindowKeyboardMethod ?? "<empty>"}, "
            + $"clientType={extras.ClientType ?? "<empty>"}.");

        if (MacBundledAdbPolicy.IsSupportedPlatform
            && !string.IsNullOrWhiteSpace(extras.FallbackStrategy)
            && extras.FallbackStrategy.StartsWith("temporary-macos-rawbync-guard:", StringComparison.Ordinal))
        {
            _logService?.Warn(
                "macOS temporary RawByNc/POSIX connection prompt decision recorded: "
                + $"{extras.FallbackStrategy}. This is a temporary measure until the Core macOS RawByNc/POSIX issue is fully fixed.");
        }
    }

    private async Task<CoreResult<bool>> QuickScreenConnectionAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken)
    {
        var isTcpAddress = LooksLikeTcpAddress(connectionInfo.Address, out var host, out var port);
        var isPlayCoverConnection = PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(connectionInfo.ConnectConfig);
        if (isTcpAddress
            && !await CanOpenTcpAsync(host, port, QuickTcpProbeTimeout, cancellationToken).ConfigureAwait(false))
        {
            var adbDevices = isPlayCoverConnection
                ? null
                : await TryListAdbDevicesAsync(connectionInfo.AdbPath, cancellationToken).ConfigureAwait(false);
            return CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.ConnectFailed,
                BuildQuickTcpProbeFailureMessage(connectionInfo, isPlayCoverConnection),
                BuildQuickPrecheckFailureDetails(
                    connectionInfo,
                    host,
                    port,
                    "tcp-probe-failed",
                    adbDevices)));
        }

        if (ShouldRunQuickAdbTargetPrecheck(connectionInfo))
        {
            var adbPrecheck = await QuickAdbTargetPrecheckAsync(
                connectionInfo,
                isTcpAddress,
                cancellationToken).ConfigureAwait(false);
            if (!adbPrecheck.Success)
            {
                return CoreResult<bool>.Fail(new CoreError(
                    CoreErrorCode.ConnectFailed,
                    adbPrecheck.Message,
                    BuildQuickPrecheckFailureDetails(
                        connectionInfo,
                        isTcpAddress ? host : null,
                        isTcpAddress ? port : null,
                        "adb-target-unavailable",
                        adbPrecheck.Details)));
            }
        }

        return CoreResult<bool>.Ok(true);
    }

    private static bool ShouldRunQuickAdbTargetPrecheck(CoreConnectionInfo connectionInfo)
    {
        if (PlayCoverConnectConfigResolver.IsPlayCoverConnectConfig(connectionInfo.ConnectConfig))
        {
            return false;
        }

        if (!UsesAdbTransport(connectionInfo.ConnectConfig, connectionInfo.Address)
            || string.IsNullOrWhiteSpace(connectionInfo.Address))
        {
            return false;
        }

        return true;
    }

    private static string BuildQuickTcpProbeFailureMessage(
        CoreConnectionInfo connectionInfo,
        bool isPlayCoverConnection)
    {
        return isPlayCoverConnection
            ? $"PlayCover MaaTools address `{connectionInfo.Address}` failed a quick TCP probe. Candidate causes: the game is not running, MaaTools is disabled, or the PlayTools address/port is wrong."
            : $"Connection address `{connectionInfo.Address}` failed a quick TCP probe. Candidate causes: emulator is not running, port is wrong, ADB debugging is disabled, or the address belongs to another emulator.";
    }

    private static async Task<AdbTargetPrecheckResult> QuickAdbTargetPrecheckAsync(
        CoreConnectionInfo connectionInfo,
        bool isTcpAddress,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ADB target precheck.");

        var stateBefore = await RunAdbPrecheckCommandAsync(
            connectionInfo.AdbPath,
            "adb serial get-state",
            ["-s", connectionInfo.Address, "get-state"],
            cancellationToken).ConfigureAwait(false);
        builder.AppendLine(stateBefore.Format());
        if (IsAdbDeviceStateReady(stateBefore))
        {
            return AdbTargetPrecheckResult.Ok(builder.ToString());
        }

        if (isTcpAddress)
        {
            var connect = await RunAdbPrecheckCommandAsync(
                connectionInfo.AdbPath,
                "adb connect",
                ["connect", connectionInfo.Address],
                cancellationToken).ConfigureAwait(false);
            builder.AppendLine(connect.Format());
            if (!LooksLikeAdbConnectSucceeded(connect))
            {
                var devices = await RunAdbPrecheckCommandAsync(
                    connectionInfo.AdbPath,
                    "adb devices -l",
                    ["devices", "-l"],
                    cancellationToken).ConfigureAwait(false);
                builder.AppendLine(devices.Format());
                return AdbTargetPrecheckResult.Fail(
                    $"ADB could not connect to `{connectionInfo.Address}` during quick precheck. Check the address, port, emulator ADB setting, and network reachability.",
                    builder.ToString());
            }

            var stateAfterConnect = await RunAdbPrecheckCommandAsync(
                connectionInfo.AdbPath,
                "adb serial get-state",
                ["-s", connectionInfo.Address, "get-state"],
                cancellationToken).ConfigureAwait(false);
            builder.AppendLine(stateAfterConnect.Format());
            if (IsAdbDeviceStateReady(stateAfterConnect))
            {
                return AdbTargetPrecheckResult.Ok(builder.ToString());
            }
        }

        var devicesAfter = await RunAdbPrecheckCommandAsync(
            connectionInfo.AdbPath,
            "adb devices -l",
            ["devices", "-l"],
            cancellationToken).ConfigureAwait(false);
        builder.AppendLine(devicesAfter.Format());
        if (AdbDevicesContainsReadySerial(devicesAfter.StandardOutput, connectionInfo.Address))
        {
            return AdbTargetPrecheckResult.Ok(builder.ToString());
        }

        return AdbTargetPrecheckResult.Fail(
            $"ADB did not report target device `{connectionInfo.Address}` as connected during quick precheck. Check the address and port; common ADB port is 5555.",
            builder.ToString());
    }

    private static bool IsAdbDeviceStateReady(AdbPrecheckCommandResult result)
        => result.Success
           && string.Equals(result.StandardOutput.Trim(), "device", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAdbConnectSucceeded(AdbPrecheckCommandResult result)
    {
        var text = string.Join('\n', result.StandardOutput, result.StandardError, result.ExceptionMessage);
        return result.Success
               && ContainsAny(text, "connected to", "already connected to")
               && !ContainsAny(text, "failed to connect", "unable to connect", "cannot connect", "No route to host", "Connection refused", "timed out");
    }

    private static bool AdbDevicesContainsReadySerial(string devicesOutput, string serial)
    {
        if (string.IsNullOrWhiteSpace(devicesOutput) || string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }

        foreach (var line in devicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2
                && string.Equals(columns[0], serial.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(columns[1], "device", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<AdbPrecheckCommandResult> RunAdbPrecheckCommandAsync(
        string? adbPath,
        string commandName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath.Trim();
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = executable;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start())
            {
                return new AdbPrecheckCommandResult(
                    commandName,
                    executable,
                    arguments,
                    null,
                    string.Empty,
                    string.Empty,
                    "process did not start");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(QuickAdbDevicesTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new AdbPrecheckCommandResult(
                commandName,
                executable,
                arguments,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                null);
        }
        catch (Exception ex)
        {
            return new AdbPrecheckCommandResult(
                commandName,
                executable,
                arguments,
                null,
                string.Empty,
                string.Empty,
                ex.Message);
        }
    }

    private sealed record AdbTargetPrecheckResult(bool Success, string Message, string Details)
    {
        public static AdbTargetPrecheckResult Ok(string details)
            => new(true, "ADB target precheck passed.", details);

        public static AdbTargetPrecheckResult Fail(string message, string details)
            => new(false, message, details);
    }

    private sealed record AdbPrecheckCommandResult(
        string CommandName,
        string Executable,
        IReadOnlyList<string> Arguments,
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        string? ExceptionMessage)
    {
        public bool Success => ExitCode == 0 && string.IsNullOrWhiteSpace(ExceptionMessage);

        public string Format()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{CommandName}: {Executable} {string.Join(' ', Arguments)} exit={ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}");
            if (!string.IsNullOrWhiteSpace(ExceptionMessage))
            {
                builder.AppendLine($"exception: {ExceptionMessage.Trim()}");
            }

            builder.AppendLine("stdout:");
            builder.AppendLine(StandardOutput.Trim());
            builder.AppendLine("stderr:");
            builder.AppendLine(StandardError.Trim());
            return builder.ToString().Trim();
        }
    }

    private static async Task<CoreResult<bool>> EnrichEarlyCoreConnectFailureAsync(
        CoreResult<bool> result,
        CoreConnectionInfo connectionInfo,
        bool quickPrecheckPassed,
        CancellationToken cancellationToken)
    {
        if (result.Success || !LooksLikeEarlyCoreConnectFailure(result.Error))
        {
            return result;
        }

        var adbState = await TryCollectAdbConnectionStateAsync(
            connectionInfo,
            quickPrecheckPassed,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(adbState))
        {
            return result;
        }

        var error = result.Error!;
        return CoreResult<bool>.Fail(error with
        {
            NativeDetails = AppendDiagnosticDetails(error.NativeDetails ?? error.Exception, adbState),
        });
    }

    private static bool LooksLikeEarlyCoreConnectFailure(CoreError? error)
    {
        if (error is null || error.Code != CoreErrorCode.ConnectFailed)
        {
            return false;
        }

        var text = string.Join('\n', error.Message, error.NativeDetails, error.Exception);
        return ContainsAny(
            text,
            "ret=false",
            "\"ret\":false",
            "invalid async call id",
            "\"cost\":0",
            "\"cost\": 0",
            "\"uuid\":\"\"",
            "\"uuid\": \"\"");
    }

    private static bool ContainsAny(string? text, params string[] fragments)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return fragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string AppendDiagnosticDetails(string? existing, string diagnostic)
    {
        var normalizedDiagnostic = diagnostic.Trim();
        if (string.IsNullOrWhiteSpace(existing))
        {
            return normalizedDiagnostic;
        }

        return existing.Trim() + Environment.NewLine + Environment.NewLine + normalizedDiagnostic;
    }

    private static async Task<string?> TryCollectAdbConnectionStateAsync(
        CoreConnectionInfo connectionInfo,
        bool quickPrecheckPassed,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("ADB state after core connect failure.");
            builder.AppendLine($"trigger=core-early-failure precheck={(quickPrecheckPassed ? "passed" : "not-run")}");
            builder.AppendLine($"adb={connectionInfo.AdbPath ?? "<system adb>"}");
            builder.AppendLine($"address={connectionInfo.Address}");
            builder.AppendLine($"config={connectionInfo.ConnectConfig}");
            AppendConnectionEffectiveConfigurationDetails(builder, connectionInfo);
            builder.AppendLine($"serial={connectionInfo.Address}");

            if (LooksLikeTcpAddress(connectionInfo.Address, out _, out _))
            {
                builder.AppendLine(await RunAdbDiagnosticCommandAsync(
                    connectionInfo.AdbPath,
                    "adb connect",
                    ["connect", connectionInfo.Address],
                    cancellationToken).ConfigureAwait(false));
            }

            builder.AppendLine(await RunAdbDiagnosticCommandAsync(
                connectionInfo.AdbPath,
                "adb devices -l",
                ["devices", "-l"],
                cancellationToken).ConfigureAwait(false));

            if (!string.IsNullOrWhiteSpace(connectionInfo.Address))
            {
                builder.AppendLine(await RunAdbDiagnosticCommandAsync(
                    connectionInfo.AdbPath,
                    "adb serial get-state",
                    ["-s", connectionInfo.Address, "get-state"],
                    cancellationToken).ConfigureAwait(false));
            }

            return builder.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"ADB state after core connect failure failed: {ex.Message}";
        }
    }

    private static void AppendConnectionEffectiveConfigurationDetails(StringBuilder builder, CoreConnectionInfo connectionInfo)
    {
        var extras = connectionInfo.Extras ?? CoreConnectionExtras.Empty;
        builder.AppendLine($"fallback={DescribeFallbackStrategy(connectionInfo, extras)}");
        if (!string.IsNullOrWhiteSpace(extras.FallbackReason))
        {
            builder.AppendLine($"reason={extras.FallbackReason}");
        }

        if (!string.IsNullOrWhiteSpace(extras.FallbackRequiredLibrary))
        {
            builder.AppendLine($"requiredLibrary={extras.FallbackRequiredLibrary}");
            builder.AppendLine($"requiredLibraryExists={extras.FallbackRequiredLibraryExists}");
        }

        builder.AppendLine(
            $"configured=touch={extras.ConfiguredTouchMode ?? extras.TouchMode ?? DefaultTouchMode},adbLite={extras.ConfiguredAdbLiteEnabled ?? extras.AdbLiteEnabled},killAdbOnExit={extras.KillAdbOnExit}");
        builder.AppendLine(
            $"effective=touch={extras.TouchMode ?? DefaultTouchMode},adbLite={extras.AdbLiteEnabled},killAdbOnExit={extras.KillAdbOnExit}");
        builder.AppendLine(
            $"extras=macBundledAdb={extras.MacUseBundledAdb},touch={extras.TouchMode ?? "<default>"},adbLite={extras.AdbLiteEnabled},killAdbOnExit={extras.KillAdbOnExit},mumu={extras.MuMu12ExtrasEnabled}:{extras.MuMu12EmulatorPath ?? "<empty>"}:{extras.MuMuBridgeConnection}:{extras.MuMu12Index ?? "<empty>"},ld={extras.LdPlayerExtrasEnabled}:{extras.LdPlayerEmulatorPath ?? "<empty>"}:{extras.LdPlayerManualSetIndex}:{extras.LdPlayerIndex ?? "<empty>"},attach={extras.AttachWindowScreencapMethod ?? "<empty>"}:{extras.AttachWindowMouseMethod ?? "<empty>"}:{extras.AttachWindowKeyboardMethod ?? "<empty>"},clientType={extras.ClientType ?? "<empty>"}");
    }

    private static async Task<string> RunAdbDiagnosticCommandAsync(
        string? adbPath,
        string commandName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath.Trim();
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = executable;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start())
            {
                return $"{commandName} did not start.";
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(QuickAdbDevicesTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return $"{commandName}: {executable} {string.Join(' ', arguments)} exit={process.ExitCode}"
                + $"\nstdout:\n{await stdoutTask.ConfigureAwait(false)}"
                + $"\nstderr:\n{await stderrTask.ConfigureAwait(false)}";
        }
        catch (Exception ex)
        {
            return $"{commandName} failed: {ex.Message}";
        }
    }

    private static bool LooksLikeTcpAddress(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var normalized = NormalizeText(address);
        var separator = normalized.LastIndexOf(':');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            return false;
        }

        host = normalized[..separator];
        return int.TryParse(normalized[(separator + 1)..], out port)
            && port is > 0 and <= 65535;
    }

    private static async Task<bool> CanOpenTcpAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, cancellationToken).AsTask();
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                return false;
            }

            await connectTask.ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> TryListAdbDevicesAsync(string? adbPath, CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath.Trim();
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = executable;
            process.StartInfo.Arguments = "devices";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start())
            {
                return "adb devices did not start.";
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(QuickAdbDevicesTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return $"adb devices exit={process.ExitCode}\nstdout:\n{await stdoutTask.ConfigureAwait(false)}\nstderr:\n{await stderrTask.ConfigureAwait(false)}";
        }
        catch (Exception ex)
        {
            return $"adb devices quick screen failed: {ex.Message}";
        }
    }

    private bool ShouldPromptMacRawByNcRisk(CoreConnectionInfo connectionInfo, CoreConnectionExtras extras)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!UsesAdbTransport(connectionInfo.ConnectConfig, connectionInfo.Address))
        {
            return false;
        }

        if (LooksLikeCompatMacConfig(connectionInfo.ConnectConfig))
        {
            return false;
        }

        if (!MayUseLegacyRawByNetcat(extras.TouchMode))
        {
            return false;
        }

        return !extras.AdbLiteEnabled;
    }

    private string ReadCurrentLanguage()
    {
        if (_configService.CurrentConfig.GlobalValues.TryGetValue(ConfigurationKeys.Localization, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }

    private string ResolveRuntimeBaseDirectory()
        => _runtimeBaseDirectory ?? RuntimeLayout.ResolveRuntimeBaseDirectory();

    private static bool UsesAdbTransport(string connectConfig, string address)
    {
        if (LooksLikeTcpAddress(address, out _, out _))
        {
            return true;
        }

        return string.Equals(connectConfig, "General", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "BlueStacks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "LDPlayer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "MuMuEmulator12", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "AVD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "Nox", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "WSA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "XYAZ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCompatMacConfig(string connectConfig)
        => string.Equals(connectConfig, "CompatMac", StringComparison.OrdinalIgnoreCase);

    private static bool MayUseLegacyRawByNetcat(string? touchMode)
    {
        return string.IsNullOrWhiteSpace(touchMode)
            || string.Equals(touchMode, "minitouch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(touchMode, "maatouch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(touchMode, "adb", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeFallbackStrategy(
        CoreConnectionInfo connectionInfo,
        CoreConnectionExtras extras,
        string? runtimeBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(extras.FallbackStrategy))
        {
            return extras.FallbackStrategy;
        }

        return $"configured:{extras.TouchMode ?? DefaultTouchMode}/adbLite={extras.AdbLiteEnabled}";
    }

    private static string BuildQuickPrecheckFailureDetails(
        CoreConnectionInfo connectionInfo,
        string? host,
        int? port,
        string stage,
        string? adbDetails)
    {
        var extras = connectionInfo.Extras ?? CoreConnectionExtras.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("Quick connect precheck failed.");
        builder.AppendLine($"stage={stage}");
        if (!string.IsNullOrWhiteSpace(host) && port is int tcpPort)
        {
            builder.AppendLine($"probe=tcp host={host} port={tcpPort} timeoutMs={(int)QuickTcpProbeTimeout.TotalMilliseconds}");
        }

        builder.AppendLine($"adb={connectionInfo.AdbPath ?? "<system adb>"}");
        builder.AppendLine($"address={connectionInfo.Address}");
        builder.AppendLine($"config={connectionInfo.ConnectConfig}");
        AppendConnectionEffectiveConfigurationDetails(builder, connectionInfo);
        builder.AppendLine($"clientType={extras.ClientType ?? "<empty>"}");
        if (!string.IsNullOrWhiteSpace(adbDetails))
        {
            builder.AppendLine("adb details:");
            builder.AppendLine(adbDetails.Trim());
        }

        return builder.ToString().Trim();
    }

    private static bool ShouldRunQuickConnectionPrecheck(CoreConnectionInfo connectionInfo)
        => connectionInfo.Timeout is { } timeout && timeout > TimeSpan.Zero && timeout < DefaultConnectBudget;

    private string? ResolveProfileAdbPath(UnifiedProfile profile)
    {
        if (MacBundledAdbPolicy.ShouldUseBundledAdb(MacBundledAdbPolicy.ReadUseBundledAdb(profile)))
        {
            return MacBundledAdbPolicy.ResolveBundledAdbPath();
        }

        return ReadProfileString(profile, "AdbPath", ConfigurationKeys.AdbPath);
    }

    private static IReadOnlyList<string> BuildConnectAddressCandidates(
        string configuredAddress,
        string connectConfig,
        bool autoDetect,
        bool alwaysAutoDetect)
    {
        var candidates = new List<string>();
        AddAddressCandidate(candidates, configuredAddress);
        if (autoDetect || alwaysAutoDetect)
        {
            foreach (var fallbackAddress in GetDefaultAddresses(connectConfig))
            {
                AddAddressCandidate(candidates, fallbackAddress);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var fallbackAddress in GetDefaultAddresses(connectConfig))
            {
                AddAddressCandidate(candidates, fallbackAddress);
            }
        }

        return candidates;
    }

    private static void AddAddressCandidate(List<string> candidates, string? address)
    {
        var normalized = NormalizeText(address);
        if (string.IsNullOrWhiteSpace(normalized)
            || candidates.Any(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static IReadOnlyList<string> GetDefaultAddresses(string connectConfig)
    {
        return NormalizeText(connectConfig).ToLowerInvariant() switch
        {
            "bluestacks" => ["127.0.0.1:5555", "127.0.0.1:5556", "127.0.0.1:5565", "127.0.0.1:5575", "127.0.0.1:5585", "127.0.0.1:5595", "127.0.0.1:5554"],
            "mumuemulator12" => ["127.0.0.1:16384", "127.0.0.1:16416", "127.0.0.1:16448", "127.0.0.1:16480", "127.0.0.1:16512", "127.0.0.1:16544", "127.0.0.1:16576"],
            "ldplayer" => ["emulator-5554", "emulator-5556", "emulator-5558", "emulator-5560", "127.0.0.1:5555", "127.0.0.1:5557", "127.0.0.1:5559", "127.0.0.1:5561"],
            "avd" => ["emulator-5554", "emulator-5556"],
            "nox" => ["127.0.0.1:62001", "127.0.0.1:59865"],
            "xyaz" => ["127.0.0.1:21503"],
            "wsa" => ["127.0.0.1:58526"],
            "macplaytools" or "compatmac" or "macsck" or "macbgr" => [PlayCoverConnectConfigResolver.DefaultPlayToolsAddress],
            _ => [],
        };
    }

    private static TimeSpan NormalizeConnectBudget(TimeSpan? requested)
        => requested is { } timeout && timeout > TimeSpan.Zero && timeout < DefaultConnectBudget
            ? timeout
            : DefaultConnectBudget;

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

    private static string? NormalizeNullableText(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task<CoreResult<bool>> ApplyResolvedInstanceOptionsAsync(
        CoreInstanceOptions? instanceOptions,
        CancellationToken cancellationToken)
    {
        var apply = await ApplyInstanceOptionsAsync(instanceOptions, cancellationToken);
        if (apply.Success || apply.Error?.Code is CoreErrorCode.NotSupported)
        {
            return CoreResult<bool>.Ok(true);
        }

        return apply;
    }

    private CoreInstanceOptions ResolveEffectiveInstanceOptions(CoreInstanceOptions? instanceOptions)
    {
        var resolvedFromConfig = ResolveInstanceOptionsFromConfig();
        return instanceOptions is null
            ? resolvedFromConfig
            : instanceOptions.MergeWith(resolvedFromConfig);
    }

    private CoreInstanceOptions ResolveInstanceOptionsFromConfig()
    {
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return new CoreInstanceOptions(
                TouchMode: DefaultTouchMode,
                DeploymentWithPause: false,
                AdbLiteEnabled: false,
                KillAdbOnExit: false);
        }

        var storedConnectConfig = ReadProfileString(profile, "ConnectConfig", ConfigurationKeys.ConnectConfig);
        var playCoverScreencapMode = ReadProfileString(profile, "PlayCoverScreencapMode", "PlayCoverScreencapMode");
        var effectiveConnectConfig = PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(
            storedConnectConfig,
            playCoverScreencapMode);
        var configuredTouchMode = ReadProfileString(profile, "TouchMode", ConfigurationKeys.TouchMode);

        return new CoreInstanceOptions(
            TouchMode: PlayCoverConnectConfigResolver.ResolveTouchMode(
                effectiveConnectConfig,
                configuredTouchMode,
                DefaultTouchMode),
            DeploymentWithPause: ReadProfileBoolFlexible(profile, ConfigurationKeys.RoguelikeDeploymentWithPause),
            AdbLiteEnabled: ReadProfileBool(profile, "AdbLiteEnabled", ConfigurationKeys.AdbLiteEnabled),
            KillAdbOnExit: ReadProfileBool(profile, "KillAdbOnExit", ConfigurationKeys.KillAdbOnExit));
    }

    private bool ReadProfileBoolFlexible(UnifiedProfile profile, string key)
    {
        if (profile.Values.TryGetValue(key, out var profileNode)
            && TryReadBool(profileNode, out var profileValue))
        {
            return profileValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(key, out var globalNode)
            && TryReadBool(globalNode, out var globalValue))
        {
            return globalValue;
        }

        return false;
    }

    private bool ReadProfileBool(UnifiedProfile profile, string key, string legacyKey)
    {
        if (profile.Values.TryGetValue(key, out var currentNode)
            && TryReadBool(currentNode, out var currentValue))
        {
            return currentValue;
        }

        if (profile.Values.TryGetValue(legacyKey, out var legacyNode)
            && TryReadBool(legacyNode, out var legacyValue))
        {
            return legacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(legacyKey, out var globalLegacyNode)
            && TryReadBool(globalLegacyNode, out var globalLegacyValue))
        {
            return globalLegacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(key, out var globalCurrentNode)
            && TryReadBool(globalCurrentNode, out var globalCurrentValue))
        {
            return globalCurrentValue;
        }

        return false;
    }

    private bool ReadProfileBool(UnifiedProfile profile, string key, string legacyKey, bool fallback)
    {
        if (profile.Values.TryGetValue(key, out var currentNode)
            && TryReadBool(currentNode, out var currentValue))
        {
            return currentValue;
        }

        if (profile.Values.TryGetValue(legacyKey, out var legacyNode)
            && TryReadBool(legacyNode, out var legacyValue))
        {
            return legacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(legacyKey, out var globalLegacyNode)
            && TryReadBool(globalLegacyNode, out var globalLegacyValue))
        {
            return globalLegacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(key, out var globalCurrentNode)
            && TryReadBool(globalCurrentNode, out var globalCurrentValue))
        {
            return globalCurrentValue;
        }

        return fallback;
    }

    private string? ReadProfileString(UnifiedProfile profile, string key, string legacyKey)
    {
        if (profile.Values.TryGetValue(key, out var currentNode)
            && TryReadString(currentNode, out var currentValue))
        {
            return currentValue;
        }

        if (profile.Values.TryGetValue(legacyKey, out var legacyNode)
            && TryReadString(legacyNode, out var legacyValue))
        {
            return legacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(legacyKey, out var globalLegacyNode)
            && TryReadString(globalLegacyNode, out var globalLegacyValue))
        {
            return globalLegacyValue;
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(key, out var globalCurrentNode)
            && TryReadString(globalCurrentNode, out var globalCurrentValue))
        {
            return globalCurrentValue;
        }

        return null;
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        if (node is JsonValue currentValue)
        {
            if (currentValue.TryGetValue(out bool boolValue))
            {
                value = boolValue;
                return true;
            }

            if (currentValue.TryGetValue(out string? stringValue)
                && bool.TryParse(stringValue, out boolValue))
            {
                value = boolValue;
                return true;
            }

            if (currentValue.TryGetValue(out string? numericString)
                && int.TryParse(numericString, out var parsedNumeric))
            {
                value = parsedNumeric != 0;
                return true;
            }

            if (currentValue.TryGetValue(out int intValue))
            {
                value = intValue != 0;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryReadString(JsonNode? node, out string? value)
    {
        if (node is JsonValue currentValue && currentValue.TryGetValue(out string? stringValue))
        {
            value = string.IsNullOrWhiteSpace(stringValue) ? null : stringValue.Trim();
            return true;
        }

        value = null;
        return false;
    }

    public async Task<UiOperationResult<ImportReport>> ImportLegacyConfigAsync(
        ImportSource source,
        bool manualImport,
        CancellationToken cancellationToken = default)
    {
        var report = await _configService.ImportLegacyAsync(source, manualImport, cancellationToken);
        if (!report.AppliedConfig)
        {
            var message = report.Errors.Count > 0
                ? string.Join("; ", report.Errors)
                : ImportReportTextFormatter.BuildStatusMessage(report, manualImport);
            return UiOperationResult<ImportReport>.Fail(UiErrorCode.ImportFailed, message);
        }

        var successMessage = report.Success
            ? report.Summary
            : $"{ImportReportTextFormatter.BuildStatusMessage(report, manualImport)} {report.Summary}";
        return UiOperationResult<ImportReport>.Ok(report, successMessage);
    }
}

public sealed class ShellFeatureService : IShellFeatureService
{
    private readonly IConnectFeatureService _connectFeatureService;

    public ShellFeatureService(IConnectFeatureService connectFeatureService)
    {
        _connectFeatureService = connectFeatureService;
    }

    public Task<UiOperationResult> ConnectAsync(
        string address,
        string config,
        string? adbPath,
        CancellationToken cancellationToken = default)
    {
        return _connectFeatureService.ConnectAsync(address, config, adbPath, cancellationToken);
    }

    public Task<UiOperationResult> ConnectAsync(
        string address,
        string config,
        string? adbPath,
        CoreInstanceOptions? instanceOptions,
        CancellationToken cancellationToken = default)
    {
        return _connectFeatureService.ConnectAsync(address, config, adbPath, instanceOptions, cancellationToken);
    }

    public Task<UiOperationResult> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default)
    {
        return _connectFeatureService.ConnectAsync(connectionInfo, cancellationToken: cancellationToken);
    }

    public Task<UiOperationResult> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CoreInstanceOptions? instanceOptions = null,
        CancellationToken cancellationToken = default)
    {
        return _connectFeatureService.ConnectAsync(connectionInfo, instanceOptions, cancellationToken);
    }

    public Task<UiOperationResult<ImportReport>> ImportLegacyConfigAsync(
        ImportSource source,
        bool manualImport,
        CancellationToken cancellationToken = default)
    {
        return _connectFeatureService.ImportLegacyConfigAsync(source, manualImport, cancellationToken);
    }

    public Task<UiOperationResult<string>> SwitchLanguageAsync(
        string currentLanguage,
        string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            if (!UiLanguageCatalog.IsSupported(targetLanguage))
            {
                return Task.FromResult(
                    UiOperationResult<string>.Fail(
                        UiErrorCode.LanguageNotSupported,
                        $"Unsupported language: {targetLanguage}."));
            }

            var normalizedTarget = UiLanguageCatalog.Normalize(targetLanguage);
            return Task.FromResult(
                UiOperationResult<string>.Ok(
                    normalizedTarget,
                    $"Language switched to {normalizedTarget}."));
        }

        var next = UiLanguageCatalog.NextInQuickCycle(currentLanguage);

        return Task.FromResult(
            UiOperationResult<string>.Ok(
                next,
                $"Language switched to {next}."));
    }

    public IReadOnlyList<string> GetSupportedLanguages()
    {
        return UiLanguageCatalog.Ordered;
    }
}

public sealed class TaskQueueFeatureService : ITaskQueueFeatureService
{
    private readonly UnifiedSessionService _sessionService;
    private readonly UnifiedConfigurationService _configService;

    public TaskQueueFeatureService(UnifiedSessionService sessionService, UnifiedConfigurationService configService)
    {
        _sessionService = sessionService;
        _configService = configService;
    }

    public Task<CoreResult<int>> QueueEnabledTasksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(CoreResult<int>.Fail(new CoreError(CoreErrorCode.InvalidRequest, error)));
        }

        ApplyMallCreditFightGuard(profile);
        return _sessionService.AppendTasksFromCurrentProfileAsync(cancellationToken);
    }

    public Task<UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>> GetStartPrecheckWarningsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>.Fail(UiErrorCode.ProfileMissing, error));
        }

        var warnings = CollectMallCreditFightWarnings(profile, mutate: false);
        return Task.FromResult(UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>.Ok(
            warnings,
            BuildPrecheckStatusMessage(warnings.Count)));
    }

    public Task<UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>> ApplyStartPrecheckDowngradesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>.Fail(UiErrorCode.ProfileMissing, error));
        }

        var warnings = CollectMallCreditFightWarnings(profile, mutate: true);
        return Task.FromResult(UiOperationResult<IReadOnlyList<TaskQueuePrecheckWarning>>.Ok(
            warnings,
            BuildPrecheckDowngradeStatusMessage(warnings.Count)));
    }

    public Task<UiOperationResult<IReadOnlyList<UnifiedTaskItem>>> GetCurrentTaskQueueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult<IReadOnlyList<UnifiedTaskItem>>.Fail(UiErrorCode.ProfileMissing, error));
        }

        IReadOnlyList<UnifiedTaskItem> copied = profile.TaskQueue
            .Select(CloneTask)
            .ToList();

        return Task.FromResult(UiOperationResult<IReadOnlyList<UnifiedTaskItem>>.Ok(copied, BuildLoadedTasksMessage(copied.Count)));
    }

    public Task<UiOperationResult> AddTaskAsync(string type, string name, bool enabled = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(type))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMissing, BuildTaskTypeMissingMessage()));
        }

        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        var (normalizedType, managedDefaults) = TaskParamCompiler.NormalizeTypeAndCreateDefaultParams(
            type,
            profile,
            _configService.CurrentConfig);
        var defaultParams = managedDefaults.Count > 0 || IsManagedType(normalizedType)
            ? managedDefaults
            : TaskModuleParameterDefaults.Create(normalizedType, ResolveLanguage());

        var task = new UnifiedTaskItem
        {
            Type = normalizedType,
            Name = string.IsNullOrWhiteSpace(name) ? normalizedType : name.Trim(),
            IsEnabled = enabled,
            Params = defaultParams,
        };
        profile.TaskQueue.Add(task);

        return Task.FromResult(UiOperationResult.Ok(BuildTaskAddedMessage(task)));
    }

    public Task<UiOperationResult> RenameTaskAsync(int index, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNameMissing, BuildTaskNameMissingMessage()));
        }

        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        task.Name = newName.Trim();
        return Task.FromResult(UiOperationResult.Ok(BuildTaskRenamedMessage(task)));
    }

    public Task<UiOperationResult> RemoveTaskAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (index < 0 || index >= profile.TaskQueue.Count)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, BuildTaskIndexOutOfRangeMessage(index)));
        }

        var task = profile.TaskQueue[index];
        profile.TaskQueue.RemoveAt(index);
        return Task.FromResult(UiOperationResult.Ok(BuildTaskRemovedMessage(task)));
    }

    public Task<UiOperationResult> MoveTaskAsync(int fromIndex, int toIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (fromIndex < 0 || fromIndex >= profile.TaskQueue.Count || toIndex < 0 || toIndex >= profile.TaskQueue.Count)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskMoveOutOfRange, BuildTaskMoveOutOfRangeMessage()));
        }

        if (fromIndex == toIndex)
        {
            return Task.FromResult(UiOperationResult.Ok(BuildTaskOrderUnchangedMessage()));
        }

        var item = profile.TaskQueue[fromIndex];
        profile.TaskQueue.RemoveAt(fromIndex);
        profile.TaskQueue.Insert(toIndex, item);
        return Task.FromResult(UiOperationResult.Ok(BuildTaskMovedMessage(item, toIndex)));
    }

    public Task<UiOperationResult> SetTaskEnabledAsync(int index, bool? enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        task.IsEnabled = enabled ?? false;
        return Task.FromResult(UiOperationResult.Ok(BuildTaskEnabledMessage(task)));
    }

    public Task<UiOperationResult> SetAllTasksEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        var affected = 0;
        foreach (var task in profile.TaskQueue)
        {
            if (task.IsEnabled == enabled)
            {
                continue;
            }

            task.IsEnabled = enabled;
            affected++;
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTasksEnabledBatchMessage(affected, enabled)));
    }

    public Task<UiOperationResult> InvertTasksEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetProfile(out var profile, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        foreach (var task in profile.TaskQueue)
        {
            task.IsEnabled = !task.IsEnabled;
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTasksEnabledInvertedMessage(profile.TaskQueue.Count)));
    }

    public Task<UiOperationResult<JsonObject>> GetTaskParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<JsonObject>.Fail(UiErrorCode.TaskNotFound, error));
        }

        var parameters = task.Params.DeepClone() as JsonObject ?? new JsonObject();
        return Task.FromResult(UiOperationResult<JsonObject>.Ok(parameters, BuildTaskParamsLoadedMessage(task)));
    }

    public async Task<UiOperationResult> UpdateTaskParamsAsync(
        int index,
        JsonObject parameters,
        bool persistImmediately = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters is null)
        {
            return UiOperationResult.Fail(UiErrorCode.TaskParamsMissing, BuildTaskParamsMissingMessage());
        }

        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return UiOperationResult.Fail(UiErrorCode.TaskNotFound, error);
        }

        task.Params = parameters.DeepClone() as JsonObject ?? new JsonObject();
        if (persistImmediately)
        {
            await _configService.SaveAsync(cancellationToken);
            return UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: true));
        }

        return UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false));
    }

    public Task<UiOperationResult<StartUpTaskParamsDto>> GetStartUpParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<StartUpTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult<StartUpTaskParamsDto>.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.StartUp))
        {
            return Task.FromResult(UiOperationResult<StartUpTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.StartUp)));
        }

        var (dto, issues) = TaskParamCompiler.ReadStartUp(task, profile, _configService.CurrentConfig, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<StartUpTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<StartUpTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult<FightTaskParamsDto>> GetFightParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<FightTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Fight))
        {
            return Task.FromResult(UiOperationResult<FightTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Fight)));
        }

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<FightTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<FightTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult<RecruitTaskParamsDto>> GetRecruitParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<RecruitTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Recruit))
        {
            return Task.FromResult(UiOperationResult<RecruitTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Recruit)));
        }

        var (dto, issues) = TaskParamCompiler.ReadRecruit(task, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<RecruitTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<RecruitTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult<RoguelikeTaskParamsDto>> GetRoguelikeParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<RoguelikeTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Roguelike))
        {
            return Task.FromResult(UiOperationResult<RoguelikeTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Roguelike)));
        }

        var (dto, issues) = TaskParamCompiler.ReadRoguelike(task, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<RoguelikeTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<RoguelikeTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult<ReclamationTaskParamsDto>> GetReclamationParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<ReclamationTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Reclamation))
        {
            return Task.FromResult(UiOperationResult<ReclamationTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Reclamation)));
        }

        var (dto, issues) = TaskParamCompiler.ReadReclamation(task, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<ReclamationTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<ReclamationTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult<CustomTaskParamsDto>> GetCustomParamsAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<CustomTaskParamsDto>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Custom))
        {
            return Task.FromResult(UiOperationResult<CustomTaskParamsDto>.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Custom)));
        }

        var (dto, issues) = TaskParamCompiler.ReadCustom(task, strict: false);
        if (issues.Any(i => i.Blocking))
        {
            return Task.FromResult(UiOperationResult<CustomTaskParamsDto>.Fail(UiErrorCode.TaskParamsCorrupted, BuildIssueMessage(issues)));
        }

        return Task.FromResult(UiOperationResult<CustomTaskParamsDto>.Ok(dto, BuildTaskParamsLoadedMessage(task)));
    }

    public Task<UiOperationResult> SaveStartUpParamsAsync(int index, StartUpTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.StartUp))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.StartUp)));
        }

        var compiled = TaskParamCompiler.CompileStartUp(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;
        TaskParamCompiler.ApplyStartUpSharedProfileValues(profile, new StartUpTaskParamsDto
        {
            AccountName = compiled.Params["account_name"]?.GetValue<string>() ?? string.Empty,
            ClientType = compiled.Params["client_type"]?.GetValue<string>() ?? dto.ClientType,
            StartGameEnabled = compiled.Params["start_game_enabled"]?.GetValue<bool>() ?? dto.StartGameEnabled,
            ConnectConfig = dto.ConnectConfig,
            ConnectAddress = dto.ConnectAddress,
            AdbPath = dto.AdbPath,
            MacUseBundledAdb = dto.MacUseBundledAdb,
            TouchMode = dto.TouchMode,
            AutoDetectConnection = dto.AutoDetectConnection,
            AttachWindowScreencapMethod = dto.AttachWindowScreencapMethod,
            AttachWindowMouseMethod = dto.AttachWindowMouseMethod,
            AttachWindowKeyboardMethod = dto.AttachWindowKeyboardMethod,
        });

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult> SaveFightParamsAsync(int index, FightTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Fight))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Fight)));
        }

        var compiled = TaskParamCompiler.CompileFight(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;
        foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
        {
            _configService.LogService.Warn($"{warning.Code}: {warning.Message}");
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult> SaveRecruitParamsAsync(int index, RecruitTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Recruit))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Recruit)));
        }

        var compiled = TaskParamCompiler.CompileRecruit(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult> SaveRoguelikeParamsAsync(int index, RoguelikeTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Roguelike))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Roguelike)));
        }

        var compiled = TaskParamCompiler.CompileRoguelike(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;
        foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
        {
            _configService.LogService.Warn($"{warning.Code}: {warning.Message}");
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult> SaveReclamationParamsAsync(int index, ReclamationTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Reclamation))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Reclamation)));
        }

        var compiled = TaskParamCompiler.CompileReclamation(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;
        foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
        {
            _configService.LogService.Warn($"{warning.Code}: {warning.Message}");
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult> SaveCustomParamsAsync(int index, CustomTaskParamsDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.ProfileMissing, error));
        }

        if (!IsTaskType(task, TaskModuleTypes.Custom))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskTypeMismatch, BuildTaskTypeMismatchMessage(TaskModuleTypes.Custom)));
        }

        var compiled = TaskParamCompiler.CompileCustom(dto, profile, _configService.CurrentConfig);
        if (compiled.HasBlockingIssues)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, BuildIssueMessage(compiled.Issues)));
        }

        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params;
        foreach (var warning in compiled.Issues.Where(i => !i.Blocking))
        {
            _configService.LogService.Warn($"{warning.Code}: {warning.Message}");
        }

        return Task.FromResult(UiOperationResult.Ok(BuildTaskParamsUpdatedMessage(task, persisted: false)));
    }

    public Task<UiOperationResult<TaskValidationReport>> ValidateTaskAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTaskByIndex(index, out var task, out var error))
        {
            return Task.FromResult(UiOperationResult<TaskValidationReport>.Fail(UiErrorCode.TaskNotFound, error));
        }

        if (!TryGetProfile(out var profile, out error))
        {
            return Task.FromResult(UiOperationResult<TaskValidationReport>.Fail(UiErrorCode.ProfileMissing, error));
        }

        var compiled = TaskParamCompiler.CompileTask(task, profile, _configService.CurrentConfig, strict: true);
        var report = new TaskValidationReport
        {
            TaskIndex = index,
            TaskName = task.Name,
            NormalizedType = compiled.NormalizedType,
            CompiledParams = compiled.Params.DeepClone() as JsonObject ?? new JsonObject(),
            Issues = compiled.Issues.ToList(),
        };
        var message = report.Issues.Count == 0
            ? BuildTaskValidationPassedMessage(task)
            : BuildIssueMessage(report.Issues);
        return Task.FromResult(UiOperationResult<TaskValidationReport>.Ok(report, message));
    }

    public async Task<UiOperationResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok(BuildTaskQueueSavedMessage());
    }

    public async Task<UiOperationResult> FlushTaskParamWritesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok(BuildTaskParamsFlushedMessage());
    }

    private bool TryGetTaskByIndex(int index, out UnifiedTaskItem task, out string error)
    {
        task = default!;
        if (!TryGetProfile(out var profile, out error))
        {
            return false;
        }

        if (index < 0 || index >= profile.TaskQueue.Count)
        {
            error = BuildTaskIndexOutOfRangeMessage(index);
            return false;
        }

        task = profile.TaskQueue[index];
        return true;
    }

    private bool TryGetProfile(out UnifiedProfile profile, out string error)
    {
        if (!_configService.TryGetCurrentProfile(out profile))
        {
            error = BuildProfileMissingMessage();
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static UnifiedTaskItem CloneTask(UnifiedTaskItem source)
    {
        var parameters = source.Params.DeepClone() as JsonObject ?? new JsonObject();
        return new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Normalize(source.Type),
            Name = source.Name,
            IsEnabled = source.IsEnabled,
            Params = parameters,
            LegacyRawTask = source.LegacyRawTask?.DeepClone() as JsonObject,
        };
    }

    private string BuildTaskParamsLoadedMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.ParamsLoaded",
            "Loaded params for `{0}`.",
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildPrecheckStatusMessage(int warningCount)
    {
        var localizer = CreateTaskQueueLocalizer();
        return warningCount == 0
            ? FormatTaskQueueMessage(localizer, "TaskQueue.Status.PrecheckPassed", "TaskQueue precheck passed.")
            : FormatTaskQueueMessage(localizer, "TaskQueue.Status.PrecheckWarnings", "TaskQueue precheck returned {0} warning(s).", warningCount);
    }

    private string BuildPrecheckDowngradeStatusMessage(int warningCount)
    {
        var localizer = CreateTaskQueueLocalizer();
        return warningCount == 0
            ? FormatTaskQueueMessage(localizer, "TaskQueue.Status.PrecheckDowngradeNotRequired", "TaskQueue precheck downgrade not required.")
            : FormatTaskQueueMessage(localizer, "TaskQueue.Status.PrecheckDowngradesApplied", "TaskQueue precheck applied {0} downgrade(s).", warningCount);
    }

    private string BuildLoadedTasksMessage(int count)
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Status.LoadedTasks",
            "Loaded {0} task(s).",
            count);
    }

    private string BuildTaskTypeMissingMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.TaskTypeMissing",
            "Task type cannot be empty.");
    }

    private string BuildTaskNameMissingMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.TaskNameMissing",
            "Task name cannot be empty.");
    }

    private string BuildTaskAddedMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.TaskAdded",
            "Added task `{0}`.",
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTaskRenamedMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.TaskRenamed",
            "Task renamed to `{0}`.",
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTaskRemovedMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.TaskRemoved",
            "Removed task `{0}`.",
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTaskIndexOutOfRangeMessage(int index)
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.TaskIndexOutOfRange",
            "Task index {0} is out of range.",
            index);
    }

    private string BuildTaskMoveOutOfRangeMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.MoveOutOfRange",
            "Task move index is out of range.");
    }

    private string BuildTaskOrderUnchangedMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Status.TaskOrderUnchanged",
            "Task order unchanged.");
    }

    private string BuildTaskMovedMessage(UnifiedTaskItem task, int toIndex)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.TaskMoved",
            "Moved task `{0}` to position {1}.",
            ResolveTaskDisplayName(task, localizer),
            toIndex + 1);
    }

    private string BuildTaskEnabledMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        var key = task.IsEnabled ? "TaskQueue.Status.TaskEnabled.True" : "TaskQueue.Status.TaskEnabled.False";
        var fallback = task.IsEnabled ? "Task `{0}` enabled." : "Task `{0}` disabled.";
        return FormatTaskQueueMessage(localizer, key, fallback, ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTasksEnabledBatchMessage(int affected, bool enabled)
    {
        var localizer = CreateTaskQueueLocalizer();
        var key = enabled ? "TaskQueue.Status.TasksEnabledBatch.True" : "TaskQueue.Status.TasksEnabledBatch.False";
        var fallback = enabled ? "Enabled {0} task(s)." : "Disabled {0} task(s).";
        return FormatTaskQueueMessage(localizer, key, fallback, affected);
    }

    private string BuildTasksEnabledInvertedMessage(int count)
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Status.TasksEnabledInverted",
            "Inverted enabled state for {0} task(s).",
            count);
    }

    private string BuildTaskParamsMissingMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.ParamsMissing",
            "Task params cannot be null.");
    }

    private string BuildTaskParamsUpdatedMessage(UnifiedTaskItem task, bool persisted)
    {
        var localizer = CreateTaskQueueLocalizer();
        var key = persisted
            ? "TaskQueue.Status.ParamsUpdatedPersisted"
            : "TaskQueue.Status.ParamsUpdated";
        var fallback = persisted
            ? "Updated params for `{0}` and persisted."
            : "Updated params for `{0}`.";
        return FormatTaskQueueMessage(
            localizer,
            key,
            fallback,
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTaskTypeMismatchMessage(string expectedType)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Error.TaskTypeMismatch",
            "Selected task is not a `{0}` task.",
            ResolveModuleDisplayName(expectedType, localizer));
    }

    private string BuildTaskValidationPassedMessage(UnifiedTaskItem task)
    {
        var localizer = CreateTaskQueueLocalizer();
        return FormatTaskQueueMessage(
            localizer,
            "TaskQueue.Status.ValidationPassed",
            "Task `{0}` passed validation.",
            ResolveTaskDisplayName(task, localizer));
    }

    private string BuildTaskQueueSavedMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Status.Saved",
            "Task queue saved.");
    }

    private string BuildTaskParamsFlushedMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Status.ParamsFlushed",
            "Task params flushed.");
    }

    private string BuildProfileMissingMessage()
    {
        return FormatTaskQueueMessage(
            CreateTaskQueueLocalizer(),
            "TaskQueue.Error.ProfileMissing",
            "Current profile `{0}` not found.",
            _configService.CurrentConfig.CurrentProfile);
    }

    private IUiLocalizer CreateTaskQueueLocalizer()
    {
        return UiLocalizer.Create(ResolveLanguage());
    }

    private static string FormatTaskQueueMessage(
        IUiLocalizer localizer,
        string key,
        string fallback,
        params object[] args)
    {
        var template = localizer.GetOrDefault(key, fallback, "TaskQueue.Status");
        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private string ResolveTaskDisplayName(UnifiedTaskItem task, IUiLocalizer localizer)
    {
        var normalizedType = TaskModuleTypes.Normalize(task.Type);
        var localizedModuleName = ResolveModuleDisplayName(normalizedType, localizer);
        var taskName = (task.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return localizedModuleName;
        }

        if (TryResolveLegacyLocalizedTaskTitle(taskName, normalizedType, localizer, out var localizedTitle))
        {
            return localizedTitle;
        }

        return IsDefaultTaskName(taskName, normalizedType, localizedModuleName)
            ? localizedModuleName
            : taskName;
    }

    private bool IsDefaultTaskName(string taskName, string normalizedType, string localizedModuleName)
    {
        if (string.Equals(taskName, normalizedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, localizedModuleName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var candidate in EnumerateLocalizedTaskAliases(normalizedType))
        {
            if (string.Equals(taskName, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveLegacyLocalizedTaskTitle(
        string taskName,
        string normalizedType,
        IUiLocalizer localizer,
        out string localizedTitle)
    {
        localizedTitle = string.Empty;
        if (!string.Equals(normalizedType, TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase)
            || !IsLegacyLocalizedTaskTitleAlias(taskName, "RemainingSanityStage"))
        {
            return false;
        }

        localizedTitle = AchievementTextCatalog.GetString("RemainingSanityStage", localizer.Language, taskName);
        return true;
    }

    private static bool IsLegacyLocalizedTaskTitleAlias(string taskName, string key)
    {
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            if (string.Equals(language, "pallas", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var alias = AchievementTextCatalog.GetString(key, language, key);
            if (string.Equals(taskName, alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateLocalizedTaskAliases(string normalizedType)
    {
        var titleKey = GetTaskTitleKey(normalizedType);
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            var localizer = UiLocalizer.Create(language);
            yield return localizer.GetOrDefault($"TaskQueue.Module.{normalizedType}", normalizedType, "TaskQueue.Status");
            if (!string.IsNullOrWhiteSpace(titleKey))
            {
                yield return localizer.GetOrDefault(titleKey, normalizedType, "TaskQueue.Status");
            }

            if (string.Equals(normalizedType, TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
            {
                yield return AchievementTextCatalog.GetString("RemainingSanityStage", language, "Remaining Sanity");
            }
        }
    }

    private static string ResolveModuleDisplayName(string normalizedType, IUiLocalizer localizer)
    {
        var titleKey = GetTaskTitleKey(normalizedType);
        if (!string.IsNullOrWhiteSpace(titleKey))
        {
            return localizer.GetOrDefault(titleKey, normalizedType, "TaskQueue.Status");
        }

        return localizer.GetOrDefault($"TaskQueue.Module.{normalizedType}", normalizedType, "TaskQueue.Status");
    }

    private static string? GetTaskTitleKey(string normalizedType)
    {
        return normalizedType switch
        {
            TaskModuleTypes.StartUp => "StartUp.Title",
            TaskModuleTypes.Fight => "Fight.Title",
            TaskModuleTypes.Infrast => "Infrast.Title",
            TaskModuleTypes.Recruit => "Recruit.Title",
            TaskModuleTypes.Mall => "Mall.Title",
            TaskModuleTypes.Award => "Award.Title",
            TaskModuleTypes.Roguelike => "Roguelike.Title",
            TaskModuleTypes.Reclamation => "Reclamation.Title",
            TaskModuleTypes.Custom => "Custom.Title",
            TaskModuleTypes.PostAction => "PostAction.Title",
            _ => null,
        };
    }

    private string ResolveLanguage()
    {
        if (_configService.CurrentConfig.GlobalValues.TryGetValue("GUI.Localization", out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }

    private void ApplyMallCreditFightGuard(UnifiedProfile profile)
    {
        _ = CollectMallCreditFightWarnings(profile, mutate: true);
    }

    private IReadOnlyList<TaskQueuePrecheckWarning> CollectMallCreditFightWarnings(UnifiedProfile profile, bool mutate)
    {
        var warnings = new List<TaskQueuePrecheckWarning>();
        var localizer = CreateTaskQueueLocalizer();
        var fightDisplayName = ResolveModuleDisplayName(TaskModuleTypes.Fight, localizer);
        var enabledFightTasks = profile.TaskQueue
            .Where(t => t.IsEnabled && string.Equals(TaskModuleTypes.Normalize(t.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enabledFightTasks.Count == 0)
        {
            return warnings;
        }

        var hasCurrentOrLastFightStage = enabledFightTasks.Any(t => !HasSpecificFightStage(t.Params));
        if (!hasCurrentOrLastFightStage)
        {
            return warnings;
        }

        foreach (var mallTask in profile.TaskQueue.Where(t =>
                     t.IsEnabled && string.Equals(TaskModuleTypes.Normalize(t.Type), TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase)))
        {
            var mallParams = mallTask.Params;
            if (!TryReadBool(mallParams, "credit_fight", out var enabledCreditFight) || !enabledCreditFight)
            {
                continue;
            }

            var warningMessage = FormatTaskQueueMessage(
                localizer,
                "TaskQueue.Warning.MallCreditFightDowngraded",
                "Disabled Mall credit fight for `{0}` because an enabled `{1}` task uses the current/last stage selector.",
                ResolveTaskDisplayName(mallTask, localizer),
                fightDisplayName);
            if (mutate)
            {
                mallParams["credit_fight"] = false;
                _configService.LogService.Warn(warningMessage);
            }
            warnings.Add(new TaskQueuePrecheckWarning(
                Code: UiErrorCode.MallCreditFightDowngraded,
                Message: warningMessage,
                Scope: "TaskQueue.Precheck.MallCreditFight",
                Blocking: false));
        }

        return warnings;
    }

    private static bool HasSpecificFightStage(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("stage", out var stageNode) || stageNode is not JsonValue value)
        {
            return false;
        }

        if (!value.TryGetValue(out string? stage))
        {
            return false;
        }

        return !FightStageSelection.IsCurrentOrLast(stage);
    }

    private static bool TryReadBool(JsonObject obj, string key, out bool value)
    {
        value = false;
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool b))
        {
            value = b;
            return true;
        }

        if (jsonValue.TryGetValue(out int i))
        {
            value = i != 0;
            return true;
        }

        if (jsonValue.TryGetValue(out string? s) && bool.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool IsTaskType(UnifiedTaskItem task, string expectedType)
    {
        return string.Equals(
            TaskParamCompiler.NormalizeTaskType(task.Type),
            expectedType,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedType(string type)
    {
        return string.Equals(type, TaskModuleTypes.StartUp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, TaskModuleTypes.Recruit, StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, TaskModuleTypes.Roguelike, StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, TaskModuleTypes.Reclamation, StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, TaskModuleTypes.Custom, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildIssueMessage(IEnumerable<TaskValidationIssue> issues)
    {
        return string.Join(
            "; ",
            issues.Select(i => $"{i.Field}: {i.Message}"));
    }
}

public sealed class CopilotFeatureService : ICopilotFeatureService
{
    private const string CopilotIdPrefix = "maa://";
    private const string PrtsPlusCopilotGet = "https://prts.maa.plus/copilot/get/";
    private const string PrtsPlusCopilotSetGet = "https://prts.maa.plus/set/get?id=";
    private const string PrtsPlusCopilotRating = "https://prts.maa.plus/copilot/rating";
    private static readonly HttpClient DefaultCopilotHttpClient = CreateCopilotHttpClient();
    private readonly HttpClient _copilotHttpClient;

    public CopilotFeatureService(HttpClient? copilotHttpClient = null)
    {
        _copilotHttpClient = copilotHttpClient ?? DefaultCopilotHttpClient;
    }

    public async Task<UiOperationResult<CopilotRemotePayload>> LoadFromCodeAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseCopilotCode(source, out var copilotId))
        {
            return UiOperationResult<CopilotRemotePayload>.Fail(
                UiErrorCode.CopilotIdMissing,
                "作业码无效。请输入 `maa:///123456` 或纯数字作业码。");
        }

        var remote = await GetRemoteJsonObjectAsync($"{PrtsPlusCopilotGet}{copilotId}", cancellationToken);
        if (!remote.Success || remote.Value is null)
        {
            return UiOperationResult<CopilotRemotePayload>.Fail(
                remote.Error?.Code ?? UiErrorCode.CoreUnknown,
                remote.Message,
                remote.Error?.Details);
        }

        var root = remote.Value;
        var statusCode = ReadJsonInt(root, "status_code") ?? 0;
        if (statusCode != 200)
        {
            var error = ReadJsonString(root, "message");
            return UiOperationResult<CopilotRemotePayload>.Fail(
                UiErrorCode.CopilotFileNotFound,
                string.IsNullOrWhiteSpace(error)
                    ? $"作业不存在：{copilotId}。"
                    : error);
        }

        if (!TryGetPropertyCaseInsensitive(root, "data", out var dataNode) || dataNode is not JsonObject data)
        {
            return UiOperationResult<CopilotRemotePayload>.Fail(
                UiErrorCode.CopilotPayloadInvalidType,
                "作业站返回结构异常：缺少 data 字段。");
        }

        if (!TryExtractRemotePayloadJson(data, out var payloadJson, out var payloadError))
        {
            return UiOperationResult<CopilotRemotePayload>.Fail(
                UiErrorCode.CopilotPayloadInvalidType,
                payloadError);
        }

        if (!TryValidateCopilotPayload(payloadJson, out var errorCode, out var errorMessage))
        {
            return UiOperationResult<CopilotRemotePayload>.Fail(errorCode, errorMessage);
        }

        var resolvedId = ReadJsonInt(data, "id") ?? copilotId;
        var title = ReadJsonString(data, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = ReadJsonString(data, "name");
        }

        var description = ReadJsonString(data, "description");
        return UiOperationResult<CopilotRemotePayload>.Ok(
            new CopilotRemotePayload(resolvedId, payloadJson, title, description),
            $"已加载作业码 {resolvedId}。");
    }

    public async Task<UiOperationResult<CopilotRemoteSetPayload>> LoadSetFromCodeAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseCopilotCode(source, out var setId))
        {
            return UiOperationResult<CopilotRemoteSetPayload>.Fail(
                UiErrorCode.CopilotIdMissing,
                "作业集码无效。请输入 `maa:///123456` 或纯数字作业集码。");
        }

        var remote = await GetRemoteJsonObjectAsync($"{PrtsPlusCopilotSetGet}{setId}", cancellationToken);
        if (!remote.Success || remote.Value is null)
        {
            return UiOperationResult<CopilotRemoteSetPayload>.Fail(
                remote.Error?.Code ?? UiErrorCode.CoreUnknown,
                remote.Message,
                remote.Error?.Details);
        }

        var root = remote.Value;
        var statusCode = ReadJsonInt(root, "status_code") ?? 0;
        if (statusCode != 200)
        {
            var error = ReadJsonString(root, "message");
            return UiOperationResult<CopilotRemoteSetPayload>.Fail(
                UiErrorCode.CopilotFileNotFound,
                string.IsNullOrWhiteSpace(error)
                    ? $"作业集不存在：{setId}。"
                    : error);
        }

        if (!TryGetPropertyCaseInsensitive(root, "data", out var dataNode) || dataNode is not JsonObject data)
        {
            return UiOperationResult<CopilotRemoteSetPayload>.Fail(
                UiErrorCode.CopilotPayloadInvalidType,
                "作业站返回结构异常：作业集缺少 data 字段。");
        }

        var name = ReadJsonString(data, "name");
        var description = ReadJsonString(data, "description");
        var items = new List<CopilotRemotePayload>();
        var failedIds = new List<int>();

        if (TryGetPropertyCaseInsensitive(data, "copilot_ids", out var idsNode) && idsNode is JsonArray idsArray)
        {
            foreach (var idNode in idsArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadInt(idNode, out var itemId) || itemId <= 0)
                {
                    continue;
                }

                var item = await LoadFromCodeAsync(itemId.ToString(), cancellationToken);
                if (item.Success && item.Value is not null)
                {
                    items.Add(item.Value);
                }
                else
                {
                    failedIds.Add(itemId);
                }
            }
        }

        if (items.Count == 0)
        {
            return UiOperationResult<CopilotRemoteSetPayload>.Fail(
                UiErrorCode.CopilotPayloadInvalidType,
                "作业集中没有可用作业。");
        }

        var resolvedName = string.IsNullOrWhiteSpace(name) ? $"Set-{setId}" : name;
        return UiOperationResult<CopilotRemoteSetPayload>.Ok(
            new CopilotRemoteSetPayload(setId, resolvedName, description, items, failedIds),
            failedIds.Count == 0
                ? $"已加载作业集 {resolvedName}（{items.Count} 个作业）。"
                : $"已加载作业集 {resolvedName}（成功 {items.Count}，失败 {failedIds.Count}）。");
    }

    public Task<string> ImportCopilotAsync(string source, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"Copilot import queued from {source}");
    }

    public async Task<UiOperationResult> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = (filePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return UiOperationResult.Fail(
                UiErrorCode.CopilotFileMissing,
                "作业文件路径为空。请粘贴本地 JSON 文件路径后重试。");
        }

        if (!File.Exists(normalizedPath))
        {
            return UiOperationResult.Fail(
                UiErrorCode.CopilotFileNotFound,
                $"作业文件不存在：{normalizedPath}。请检查路径是否正确。");
        }

        string payload;
        try
        {
            payload = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return UiOperationResult.Fail(
                UiErrorCode.CopilotFileReadFailed,
                $"读取作业文件失败：{normalizedPath}。请确认文件可访问且内容为 UTF-8 JSON。",
                ex.Message);
        }

        if (!TryValidateCopilotPayload(payload, out var errorCode, out var errorMessage))
        {
            return UiOperationResult.Fail(errorCode, errorMessage);
        }

        return UiOperationResult.Ok($"已导入作业文件：{normalizedPath}");
    }

    public Task<UiOperationResult> ImportFromClipboardAsync(string payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = (payload ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotClipboardEmpty,
                "剪贴板内容为空。请复制本地路径或 JSON 内容后重试。"));
        }

        var pathCandidate = NormalizePotentialPathText(normalized);
        if (File.Exists(pathCandidate))
        {
            return ImportFromFileAsync(pathCandidate, cancellationToken);
        }

        if (!LooksLikeJsonPayload(normalized) && LooksLikePathText(pathCandidate))
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotFileNotFound,
                $"剪贴板内容看起来是文件路径，但文件不存在：{pathCandidate}。请检查路径后重试。"));
        }

        if (!TryValidateCopilotPayload(normalized, out var errorCode, out var errorMessage))
        {
            return Task.FromResult(UiOperationResult.Fail(errorCode, errorMessage));
        }

        return Task.FromResult(UiOperationResult.Ok("已接受剪贴板作业内容。"));
    }

    public async Task<UiOperationResult> SubmitFeedbackAsync(string copilotId, bool like, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = (copilotId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return UiOperationResult.Fail(UiErrorCode.CopilotIdMissing, "Copilot id cannot be empty.");
        }

        if (!TryParseCopilotCode(normalized, out var resolvedId))
        {
            return UiOperationResult.Fail(
                UiErrorCode.CopilotIdMissing,
                "作业码无效。请输入 `maa:///123456` 或纯数字作业码。");
        }

        var body = JsonSerializer.Serialize(new
        {
            id = resolvedId,
            rating = like ? "Like" : "Dislike",
        });

        try
        {
            using var response = await _copilotHttpClient.PostAsync(
                PrtsPlusCopilotRating,
                new StringContent(body, Encoding.UTF8, "application/json"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadResponseBodySafeAsync(response, cancellationToken);
                return UiOperationResult.Fail(
                    UiErrorCode.CoreUnknown,
                    $"提交反馈失败（HTTP {(int)response.StatusCode}）。",
                    details);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return UiOperationResult.Fail(
                UiErrorCode.CoreUnknown,
                "提交反馈失败：网络不可用或请求超时。",
                ex.Message);
        }

        return UiOperationResult.Ok(
            like
                ? $"Feedback submitted for {resolvedId}: like"
                : $"Feedback submitted for {resolvedId}: dislike");
    }

    private static bool TryValidateCopilotPayload(
        string payload,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (Exception ex)
        {
            errorCode = UiErrorCode.CopilotPayloadInvalidJson;
            errorMessage = $"剪贴板/文件内容不是合法 JSON：{ex.Message}。请检查括号、引号和逗号。";
            return false;
        }

        if (node is null)
        {
            errorCode = UiErrorCode.CopilotPayloadInvalidType;
            errorMessage = "作业内容为空 JSON 节点。请提供 JSON 对象或数组。";
            return false;
        }

        if (node is JsonArray array)
        {
            if (array.Count == 0)
            {
                errorCode = UiErrorCode.CopilotPayloadEmptyArray;
                errorMessage = "作业数组为空。请至少提供一个作业对象。";
                return false;
            }

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject item)
                {
                    errorCode = UiErrorCode.CopilotPayloadInvalidType;
                    errorMessage = $"第{i + 1}个作业不是 JSON 对象。请改为对象结构。";
                    return false;
                }

                if (!TryValidateCopilotObject(item, i, out errorCode, out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        if (node is JsonObject obj)
        {
            if (!TryValidateCopilotObject(obj, null, out errorCode, out errorMessage))
            {
                return false;
            }

            return true;
        }

        errorCode = UiErrorCode.CopilotPayloadInvalidType;
        errorMessage = "作业内容必须是 JSON 对象或数组。";
        return false;
    }

    private static bool TryValidateCopilotObject(
        JsonObject obj,
        int? index,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;

        var position = index.HasValue ? $"第{index.Value + 1}个作业" : "作业对象";
        if (!TryGetRequiredStringProperty(obj, "stage_name", out _))
        {
            errorCode = UiErrorCode.CopilotPayloadMissingFields;
            errorMessage = $"{position}缺少必填字段 `stage_name`（非空字符串）。";
            return false;
        }

        if (!TryGetRequiredStringProperty(obj, "minimum_required", out _))
        {
            errorCode = UiErrorCode.CopilotPayloadMissingFields;
            errorMessage = $"{position}缺少必填字段 `minimum_required`（非空字符串）。";
            return false;
        }

        if (TryGetPropertyCaseInsensitive(obj, "type", out var typeNode)
            && !TryGetStringValue(typeNode, out var typeValue))
        {
            errorCode = UiErrorCode.CopilotPayloadInvalidType;
            errorMessage = $"{position}字段 `type` 必须是字符串。";
            return false;
        }

        var isSss = TryGetPropertyCaseInsensitive(obj, "type", out var resolvedTypeNode)
                    && TryGetStringValue(resolvedTypeNode, out var resolvedType)
                    && string.Equals(resolvedType, "SSS", StringComparison.OrdinalIgnoreCase);
        if (isSss)
        {
            return true;
        }

        if (!TryGetPropertyCaseInsensitive(obj, "actions", out var actionsNode))
        {
            errorCode = UiErrorCode.CopilotPayloadMissingFields;
            errorMessage = $"{position}缺少必填字段 `actions`（非空数组）。";
            return false;
        }

        if (actionsNode is not JsonArray actionsArray)
        {
            errorCode = UiErrorCode.CopilotPayloadInvalidType;
            errorMessage = $"{position}字段 `actions` 必须是数组。";
            return false;
        }

        if (actionsArray.Count == 0)
        {
            errorCode = UiErrorCode.CopilotPayloadMissingFields;
            errorMessage = $"{position}字段 `actions` 不能为空数组。";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredStringProperty(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node) || !TryGetStringValue(node, out var raw))
        {
            return false;
        }

        value = raw.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetStringValue(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? raw))
        {
            return false;
        }

        value = raw ?? string.Empty;
        return true;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonObject obj, string key, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool LooksLikeJsonPayload(string text)
    {
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("{".AsSpan(), StringComparison.Ordinal)
               || span.StartsWith("[".AsSpan(), StringComparison.Ordinal);
    }

    private static string NormalizePotentialPathText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool LooksLikePathText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (Path.IsPathRooted(text))
        {
            return true;
        }

        return text.Contains('\\')
               || text.Contains('/')
               || text.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("./", StringComparison.Ordinal)
               || text.StartsWith(".\\", StringComparison.Ordinal)
               || text.StartsWith("../", StringComparison.Ordinal)
               || text.StartsWith("..\\", StringComparison.Ordinal);
    }

    private static HttpClient CreateCopilotHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("MAAUnified-Copilot/1.0");
        return client;
    }

    private static bool TryParseCopilotCode(string source, out int copilotId)
    {
        copilotId = 0;
        var normalized = (source ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.StartsWith(CopilotIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[CopilotIdPrefix.Length..].TrimStart('/');
        }

        return int.TryParse(normalized, out copilotId) && copilotId > 0;
    }

    private async Task<UiOperationResult<JsonObject>> GetRemoteJsonObjectAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _copilotHttpClient.GetAsync(url, cancellationToken);
            var body = await ReadResponseBodySafeAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return UiOperationResult<JsonObject>.Fail(
                    UiErrorCode.CoreUnknown,
                    $"请求失败（HTTP {(int)response.StatusCode}）。",
                    body);
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(body);
            }
            catch (Exception ex)
            {
                return UiOperationResult<JsonObject>.Fail(
                    UiErrorCode.CopilotPayloadInvalidJson,
                    "作业站返回了无效 JSON。",
                    ex.Message);
            }

            if (root is not JsonObject obj)
            {
                return UiOperationResult<JsonObject>.Fail(
                    UiErrorCode.CopilotPayloadInvalidType,
                    "作业站返回结构异常，预期为 JSON 对象。");
            }

            return UiOperationResult<JsonObject>.Ok(obj, "ok");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return UiOperationResult<JsonObject>.Fail(
                UiErrorCode.CoreUnknown,
                "网络请求失败，请稍后重试。",
                ex.Message);
        }
    }

    private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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

    private static string ReadJsonString(JsonObject obj, string key)
    {
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node)
            || !TryGetStringValue(node, out var raw))
        {
            return string.Empty;
        }

        return raw.Trim();
    }

    private static int? ReadJsonInt(JsonObject obj, string key)
    {
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node))
        {
            return null;
        }

        if (!TryReadInt(node, out var value))
        {
            return null;
        }

        return value;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt;
            return true;
        }

        if (jsonValue.TryGetValue(out string? raw) && int.TryParse(raw, out parsedInt))
        {
            value = parsedInt;
            return true;
        }

        return false;
    }

    private static bool TryExtractRemotePayloadJson(
        JsonObject data,
        out string payloadJson,
        out string errorMessage)
    {
        payloadJson = string.Empty;
        errorMessage = string.Empty;

        if (!TryGetPropertyCaseInsensitive(data, "content", out var contentNode) || contentNode is null)
        {
            errorMessage = "作业站返回结构异常：缺少 content 字段。";
            return false;
        }

        if (contentNode is JsonObject or JsonArray)
        {
            payloadJson = contentNode.ToJsonString();
            return true;
        }

        if (!TryGetStringValue(contentNode, out var rawContent))
        {
            errorMessage = "作业站返回结构异常：content 不是 JSON 对象或 JSON 字符串。";
            return false;
        }

        var normalized = rawContent.Trim();
        if (LooksLikeJsonPayload(normalized))
        {
            payloadJson = normalized;
            return true;
        }

        try
        {
            var parsed = JsonNode.Parse(normalized);
            if (parsed is JsonObject or JsonArray)
            {
                payloadJson = parsed.ToJsonString();
                return true;
            }
        }
        catch
        {
            // Keep original normalized payload for final validation.
        }

        payloadJson = normalized;
        return true;
    }
}

public sealed class ToolboxFeatureService : IToolboxFeatureService
{
    private readonly IMaaCoreBridge? _bridge;
    private readonly IConnectFeatureService? _connectFeatureService;

    public ToolboxFeatureService()
        : this(null, null)
    {
    }

    public ToolboxFeatureService(IMaaCoreBridge? bridge, IConnectFeatureService? connectFeatureService = null)
    {
        _bridge = bridge;
        _connectFeatureService = connectFeatureService;
    }

    public async Task<UiOperationResult<ToolboxDispatchResult>> DispatchToolAsync(
        ToolboxDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return UiOperationResult<ToolboxDispatchResult>.Fail(
                UiErrorCode.ToolboxInvalidParameters,
                "Toolbox dispatch request cannot be null.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult<ToolboxDispatchResult>.Fail(
                UiErrorCode.ToolboxExecutionCancelled,
                $"Tool `{request.Tool}` dispatch cancelled by caller.");
        }

        if (_bridge is null)
        {
            return UiOperationResult<ToolboxDispatchResult>.Fail(
                UiErrorCode.ToolboxExecutionFailed,
                "Toolbox core bridge is not configured.");
        }

        if (!TryBuildCoreTask(request, out var coreTask, out var taskType, out var parameterSummary, out var validationError))
        {
            return UiOperationResult<ToolboxDispatchResult>.Fail(
                validationError?.Code ?? UiErrorCode.ToolboxInvalidParameters,
                validationError?.Message ?? "Invalid toolbox request.",
                validationError?.Details);
        }

        var appendResult = await _bridge.AppendTaskAsync(coreTask, cancellationToken);
        if (!appendResult.Success)
        {
            return UiOperationResult<ToolboxDispatchResult>.Fail(
                UiErrorCode.ToolboxExecutionFailed,
                $"Tool `{request.Tool}` append failed: {appendResult.Error?.Message ?? "unknown error"}.",
                JsonSerializer.Serialize(new
                {
                    tool = request.Tool.ToString(),
                    taskType,
                    coreTask = coreTask.Name,
                    parameterSummary,
                    appendError = appendResult.Error?.Code.ToString(),
                    appendResult.Error?.Message,
                    appendResult.Error?.NativeDetails,
                }));
        }

        if (_connectFeatureService is not null)
        {
            var startResult = await _connectFeatureService.StartAsync(cancellationToken);
            if (!startResult.Success)
            {
                return UiOperationResult<ToolboxDispatchResult>.Fail(
                    startResult.Error?.Code ?? UiErrorCode.ToolboxExecutionFailed,
                    startResult.Message,
                    startResult.Error?.Details);
            }
        }

        return UiOperationResult<ToolboxDispatchResult>.Ok(
            new ToolboxDispatchResult(
                request.Tool,
                parameterSummary,
                DateTimeOffset.Now,
                appendResult.Value,
                taskType),
            $"Tool `{request.Tool}` dispatched.");
    }

    public async Task<UiOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connectFeatureService is null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.ToolboxExecutionFailed,
                "Toolbox stop service is not configured.");
        }

        return await _connectFeatureService.StopAsync(cancellationToken);
    }

    private static bool TryBuildCoreTask(
        ToolboxDispatchRequest request,
        out CoreTaskRequest coreTask,
        out string taskType,
        out string parameterSummary,
        out (string Code, string Message, string? Details)? error)
    {
        coreTask = new CoreTaskRequest(string.Empty, string.Empty, true, "{}");
        taskType = string.Empty;
        parameterSummary = string.Empty;
        error = null;

        switch (request.Tool)
        {
            case ToolboxToolKind.Recruit:
            {
                if (request.Recruit is null)
                {
                    error = (
                        UiErrorCode.ToolboxInvalidParameters,
                        "Recruit request is missing structured parameters.",
                        null);
                    return false;
                }

                var levels = request.Recruit.SelectLevels
                    .Where(level => level is >= 3 and <= 6)
                    .Distinct()
                    .OrderBy(level => level)
                    .ToArray();
                if (levels.Length == 0)
                {
                    error = (
                        UiErrorCode.ToolboxInvalidParameters,
                        "Recruit request must include at least one selected level.",
                        null);
                    return false;
                }

                var payload = new JsonObject
                {
                    ["refresh"] = false,
                    ["force_refresh"] = false,
                    ["select"] = new JsonArray(levels.Select(level => JsonValue.Create(level)).ToArray()),
                    ["confirm"] = new JsonArray(JsonValue.Create(-1)),
                    ["times"] = 0,
                    ["set_time"] = request.Recruit.AutoSetTime,
                    ["expedite"] = false,
                    ["skip_robot"] = false,
                    ["extra_tags_mode"] = 0,
                    ["first_tags"] = new JsonArray(),
                    ["recruitment_time"] = new JsonObject
                    {
                        ["3"] = request.Recruit.Level3Time,
                        ["4"] = request.Recruit.Level4Time,
                        ["5"] = request.Recruit.Level5Time,
                    },
                    ["report_to_penguin"] = false,
                    ["report_to_yituliu"] = false,
                    ["server"] = string.IsNullOrWhiteSpace(request.Recruit.ServerType)
                        ? "CN"
                        : request.Recruit.ServerType.Trim(),
                };

                taskType = TaskModuleTypes.Recruit;
                parameterSummary = request.ParameterSummary
                    ?? $"select={string.Join(',', levels)}; autoSetTime={request.Recruit.AutoSetTime.ToString().ToLowerInvariant()}; level3={request.Recruit.Level3Time}; level4={request.Recruit.Level4Time}; level5={request.Recruit.Level5Time}; server={payload["server"]}";
                coreTask = new CoreTaskRequest(taskType, "Toolbox.Recruit", true, payload.ToJsonString());
                return true;
            }
            case ToolboxToolKind.OperBox:
                taskType = "OperBox";
                parameterSummary = request.ParameterSummary ?? "mode=owned";
                coreTask = new CoreTaskRequest(taskType, "Toolbox.OperBox", true, "{}");
                return true;
            case ToolboxToolKind.Depot:
                taskType = "Depot";
                parameterSummary = request.ParameterSummary ?? "format=summary";
                coreTask = new CoreTaskRequest(taskType, "Toolbox.Depot", true, "{}");
                return true;
            case ToolboxToolKind.Gacha:
            {
                if (request.Gacha is null)
                {
                    error = (
                        UiErrorCode.ToolboxInvalidParameters,
                        "Gacha request is missing structured parameters.",
                        null);
                    return false;
                }

                var taskName = request.Gacha.Once ? "GachaOnce" : "GachaTenTimes";
                var payload = new JsonObject
                {
                    ["task_names"] = new JsonArray(JsonValue.Create(taskName)),
                };
                taskType = TaskModuleTypes.Custom;
                parameterSummary = request.ParameterSummary ?? $"drawCount={(request.Gacha.Once ? 1 : 10)}";
                coreTask = new CoreTaskRequest(taskType, $"Toolbox.{taskName}", true, payload.ToJsonString());
                return true;
            }
            case ToolboxToolKind.MiniGame:
            {
                if (request.MiniGame is null || string.IsNullOrWhiteSpace(request.MiniGame.TaskName))
                {
                    error = (
                        UiErrorCode.ToolboxInvalidParameters,
                        "MiniGame request is missing task name.",
                        null);
                    return false;
                }

                var taskName = request.MiniGame.TaskName.Trim();
                var payload = new JsonObject
                {
                    ["task_names"] = new JsonArray(JsonValue.Create(taskName)),
                };
                taskType = TaskModuleTypes.Custom;
                parameterSummary = request.ParameterSummary ?? $"taskName={taskName}";
                coreTask = new CoreTaskRequest(taskType, "Toolbox.MiniGame", true, payload.ToJsonString());
                return true;
            }
            case ToolboxToolKind.VideoRecognition:
                error = (
                    UiErrorCode.ToolNotSupported,
                    "Peep does not append a toolbox task.",
                    null);
                return false;
            default:
                error = (
                    UiErrorCode.ToolNotSupported,
                    $"Tool `{request.Tool}` is not supported.",
                    null);
                return false;
        }
    }
}

public sealed class OverlayFeatureService : IOverlayFeatureService
{
    private readonly IPlatformCapabilityService _platformCapabilities;

    public OverlayFeatureService(IPlatformCapabilityService platformCapabilities)
    {
        _platformCapabilities = platformCapabilities;
    }

    public Task<string> GetOverlayModeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("runtime-detected");
    }

    public async Task<UiOperationResult<IReadOnlyList<OverlayTarget>>> GetOverlayTargetsAsync(CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.QueryOverlayTargetsAsync(cancellationToken);
    }

    public async Task<UiOperationResult> SelectOverlayTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.SelectOverlayTargetAsync(targetId, cancellationToken);
    }

    public async Task<UiOperationResult> ToggleOverlayVisibilityAsync(bool visible, CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.SetOverlayVisibleAsync(visible, cancellationToken);
    }
}

public sealed class PostActionFeatureService : IPostActionFeatureService
{
    private const string PostActionConfigKey = "TaskQueue.PostAction";
    private const string WarnKeyIfNoOtherNeedsSystemAction = "PostAction.Warn.IfNoOtherNeedsSystemAction";
    private const string WarnKeyUnsupportedDowngrade = "PostAction.Warn.UnsupportedDowngrade";
    private static readonly TimeSpan DeviceActionSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PowerActionPromptCountdown = TimeSpan.FromSeconds(60);
    private readonly UnifiedConfigurationService _configService;
    private readonly UiDiagnosticsService _diagnostics;
    private readonly IPostActionExecutorService _executor;
    private readonly IMaaCoreBridge? _coreBridge;
    private readonly IAppLifecycleService? _appLifecycleService;
    private readonly IPostActionPromptService _promptService;

    public PostActionFeatureService(
        UnifiedConfigurationService configService,
        UiDiagnosticsService diagnostics,
        IPostActionExecutorService executor)
        : this(configService, diagnostics, executor, null, null, null)
    {
    }

    public PostActionFeatureService(
        UnifiedConfigurationService configService,
        UiDiagnosticsService diagnostics,
        IPostActionExecutorService executor,
        IMaaCoreBridge? coreBridge,
        IAppLifecycleService? appLifecycleService,
        IPostActionPromptService? promptService)
    {
        _configService = configService;
        _diagnostics = diagnostics;
        _executor = executor;
        _coreBridge = coreBridge;
        _appLifecycleService = appLifecycleService;
        _promptService = promptService ?? new NoOpPostActionPromptService();
    }

    public async Task<UiOperationResult<PostActionConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return UiOperationResult<PostActionConfig>.Fail(
                UiErrorCode.ProfileMissing,
                $"Current profile `{_configService.CurrentConfig.CurrentProfile}` not found.");
        }

        if (profile.Values.TryGetValue(PostActionConfigKey, out var node) && node is not null)
        {
            var parsed = PostActionConfig.FromJson(node);
            var normalized = NormalizeForPersistentStorage(parsed, out var changed);
            if (changed)
            {
                profile.Values[PostActionConfigKey] = normalized.ToJson();
                await _configService.SaveAsync(cancellationToken);
            }

            return UiOperationResult<PostActionConfig>.Ok(normalized, "Loaded structured post action config.");
        }

        if (_configService.CurrentConfig.GlobalValues.TryGetValue(PostActionConfigKey, out var globalStructuredNode) && globalStructuredNode is not null)
        {
            var parsed = PostActionConfig.FromJson(globalStructuredNode);
            var normalized = NormalizeForPersistentStorage(parsed, out _);
            profile.Values[PostActionConfigKey] = normalized.ToJson();
            _configService.CurrentConfig.GlobalValues.Remove(PostActionConfigKey);
            await _configService.SaveAsync(cancellationToken);
            return UiOperationResult<PostActionConfig>.Ok(normalized, "Loaded structured post action config.");
        }

        var config = _configService.CurrentConfig;
        var hasProfileLegacy = profile.Values.TryGetValue(ConfigurationKeys.PostActions, out var profileLegacyNode) && profileLegacyNode is not null;
        var hasGlobalLegacy = config.GlobalValues.TryGetValue(ConfigurationKeys.PostActions, out var globalLegacyNode) && globalLegacyNode is not null;
        var hasProfileLegacyAction = profile.Values.TryGetValue(ConfigurationKeys.ActionAfterCompleted, out var profileLegacyActionNode) && profileLegacyActionNode is not null;
        var hasGlobalLegacyAction = config.GlobalValues.TryGetValue(ConfigurationKeys.ActionAfterCompleted, out var globalLegacyActionNode) && globalLegacyActionNode is not null;
        var hasLegacyPostActions = hasProfileLegacy || hasGlobalLegacy;
        var hasLegacyActionAfterCompleted = hasProfileLegacyAction || hasGlobalLegacyAction;
        if (!hasLegacyPostActions && !hasLegacyActionAfterCompleted)
        {
            return UiOperationResult<PostActionConfig>.Ok(PostActionConfig.Default, "Post action config is empty.");
        }

        var parsedLegacyPostActions = false;
        var legacyPostActionsConfig = PostActionConfig.Default;
        if (hasLegacyPostActions)
        {
            var legacyNode = hasProfileLegacy ? profileLegacyNode : globalLegacyNode;
            if (TryReadLegacyFlags(legacyNode!, out var flags))
            {
                parsedLegacyPostActions = true;
                legacyPostActionsConfig = MapLegacyFlags(flags);
            }
        }

        var parsedLegacyActionAfterCompleted = false;
        var legacyActionAfterCompletedConfig = PostActionConfig.Default;
        if (hasLegacyActionAfterCompleted)
        {
            var legacyActionNode = hasProfileLegacyAction ? profileLegacyActionNode : globalLegacyActionNode;
            if (TryReadLegacyActionAfterCompleted(legacyActionNode!, out var parsedActionConfig))
            {
                parsedLegacyActionAfterCompleted = true;
                legacyActionAfterCompletedConfig = parsedActionConfig;
            }
        }

        PostActionConfig migratedConfig;
        bool migratedFromFlags;
        if (parsedLegacyPostActions && legacyPostActionsConfig.HasAnyAction())
        {
            migratedConfig = legacyPostActionsConfig;
            migratedFromFlags = true;
        }
        else if (parsedLegacyActionAfterCompleted)
        {
            migratedConfig = legacyActionAfterCompletedConfig;
            migratedFromFlags = false;
        }
        else if (parsedLegacyPostActions)
        {
            migratedConfig = legacyPostActionsConfig;
            migratedFromFlags = true;
        }
        else
        {
            return UiOperationResult<PostActionConfig>.Fail(
                UiErrorCode.PostActionLegacyParseFailed,
                hasLegacyPostActions && hasLegacyActionAfterCompleted
                    ? "Failed to parse legacy completion action config."
                    : hasLegacyPostActions
                        ? "Failed to parse legacy post action flags."
                        : "Failed to parse legacy completion action.");
        }

        var normalizedMigratedConfig = NormalizeForPersistentStorage(migratedConfig, out _);
        var migrated = normalizedMigratedConfig.ToJson();
        profile.Values[PostActionConfigKey] = migrated;
        profile.Values.Remove(ConfigurationKeys.PostActions);
        config.GlobalValues.Remove(ConfigurationKeys.PostActions);
        profile.Values.Remove(ConfigurationKeys.ActionAfterCompleted);
        config.GlobalValues.Remove(ConfigurationKeys.ActionAfterCompleted);
        await _configService.SaveAsync(cancellationToken);
        _configService.LogService.Info(
            migratedFromFlags
                ? "Migrated legacy post actions bitmask to structured TaskQueue.PostAction."
                : "Migrated legacy completion action to structured TaskQueue.PostAction.");
        return UiOperationResult<PostActionConfig>.Ok(
            normalizedMigratedConfig,
            migratedFromFlags ? "Legacy post action config migrated." : "Legacy completion action migrated.");
    }

    public async Task<UiOperationResult> SaveAsync(PostActionConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return UiOperationResult.Fail(
                UiErrorCode.ProfileMissing,
                $"Current profile `{_configService.CurrentConfig.CurrentProfile}` not found.");
        }

        var persistentConfig = NormalizeForPersistentStorage(config, out _);
        persistentConfig.Once = false;
        profile.Values[PostActionConfigKey] = persistentConfig.ToJson();
        _configService.CurrentConfig.GlobalValues.Remove(PostActionConfigKey);
        profile.Values.Remove(ConfigurationKeys.PostActions);
        _configService.CurrentConfig.GlobalValues.Remove(ConfigurationKeys.PostActions);
        profile.Values.Remove(ConfigurationKeys.ActionAfterCompleted);
        _configService.CurrentConfig.GlobalValues.Remove(ConfigurationKeys.ActionAfterCompleted);
        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok("Post action config saved.");
    }

    public Task<UiOperationResult<PostActionPreview>> GetCapabilityPreviewAsync(PostActionConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveConfig = NormalizeForRuntime(config, out _);
        var warnings = new List<string>();
        var unsupported = new List<string>();

        AddUnsupported(effectiveConfig.ExitArknights, PostActionType.ExitArknights, nameof(PostActionType.ExitArknights));
        AddUnsupported(effectiveConfig.BackToAndroidHome, PostActionType.BackToAndroidHome, nameof(PostActionType.BackToAndroidHome));
        AddUnsupported(effectiveConfig.ExitEmulator, PostActionType.ExitEmulator, nameof(PostActionType.ExitEmulator));
        AddUnsupported(effectiveConfig.ExitSelf, PostActionType.ExitSelf, nameof(PostActionType.ExitSelf));
        AddUnsupported(effectiveConfig.Hibernate, PostActionType.Hibernate, nameof(PostActionType.Hibernate));
        AddUnsupported(effectiveConfig.Shutdown, PostActionType.Shutdown, nameof(PostActionType.Shutdown));
        AddUnsupported(effectiveConfig.Sleep, PostActionType.Sleep, nameof(PostActionType.Sleep));

        if (unsupported.Count > 0)
        {
            warnings.Add(WarnKeyUnsupportedDowngrade);
        }

        var preview = new PostActionPreview(false, warnings, unsupported);
        return Task.FromResult(UiOperationResult<PostActionPreview>.Ok(preview, "Post action selection validated."));

        void AddUnsupported(bool selected, PostActionType action, string actionName)
        {
            if (!selected)
            {
                return;
            }

            var capability = GetCapability(config, action);
            if (!capability.Supported)
            {
                unsupported.Add(actionName);
            }
        }
    }

    public async Task<UiOperationResult<PostActionPreview>> ValidateSelectionAsync(PostActionConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveConfig = NormalizeForRuntime(config, out _);
        var warnings = new List<string>();
        if (effectiveConfig.IfNoOtherMaa && !(effectiveConfig.Hibernate || effectiveConfig.Shutdown || effectiveConfig.Sleep))
        {
            warnings.Add(WarnKeyIfNoOtherNeedsSystemAction);
        }

        var capability = await GetCapabilityPreviewAsync(effectiveConfig, cancellationToken);
        if (!capability.Success || capability.Value is null)
        {
            return UiOperationResult<PostActionPreview>.Fail(
                capability.Error?.Code ?? UiErrorCode.PostActionSelectionInvalid,
                capability.Message);
        }

        warnings.AddRange(capability.Value.Warnings);
        var preview = new PostActionPreview(
            HasBlockingError: false,
            Warnings: warnings,
            UnsupportedActions: capability.Value.UnsupportedActions);
        return UiOperationResult<PostActionPreview>.Ok(preview, "Post action selection validated.");
    }

    public async Task<UiOperationResult> ExecuteAfterCompletionAsync(
        PostActionExecutionContext context,
        PostActionConfig? configOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PostActionConfig config;
        if (configOverride is null)
        {
            var loadResult = await LoadAsync(cancellationToken);
            if (!loadResult.Success || loadResult.Value is null)
            {
                return UiOperationResult.Fail(loadResult.Error?.Code ?? UiErrorCode.PostActionLoadFailed, loadResult.Message);
            }

            config = loadResult.Value;
        }
        else
        {
            config = configOverride.Clone();
        }

        config = NormalizeForRuntime(config, out _);

        if (!config.HasAnyAction())
        {
            return UiOperationResult.Ok("No post actions selected.");
        }

        var validate = await ValidateSelectionAsync(config, cancellationToken);
        if (!validate.Success)
        {
            return UiOperationResult.Fail(validate.Error?.Code ?? UiErrorCode.PostActionSelectionInvalid, validate.Message);
        }

        var summary = new List<string>();
        var executedActions = new List<string>();
        var skippedActions = new List<string>();
        var failures = new List<string>();
        var skipSystemActionsForOtherMaa = config.IfNoOtherMaa && HasOtherMaaProcess();
        if (skipSystemActionsForOtherMaa)
        {
            summary.Add("Detected another MAA process, skipped system-level post actions.");
        }

        if (config.BackToAndroidHome)
        {
            await ExecuteActionAsync(PostActionType.BackToAndroidHome, cancellationToken);
        }

        if (config.ExitArknights)
        {
            await ExecuteActionAsync(PostActionType.ExitArknights, cancellationToken);
        }

        if (config.ExitEmulator)
        {
            await ExecuteActionAsync(PostActionType.ExitEmulator, cancellationToken);
        }

        if (config.Hibernate)
        {
            if (skipSystemActionsForOtherMaa)
            {
                skippedActions.Add(nameof(PostActionType.Hibernate));
                await RecordEventAsync(context, nameof(PostActionType.Hibernate), UiErrorCode.PostActionUnsupported, "Skipped by IfNoOtherMaa.");
            }
            else
            {
                await ExecuteActionAsync(PostActionType.Hibernate, cancellationToken);
            }
        }

        if (config.Shutdown)
        {
            if (skipSystemActionsForOtherMaa)
            {
                skippedActions.Add(nameof(PostActionType.Shutdown));
                await RecordEventAsync(context, nameof(PostActionType.Shutdown), UiErrorCode.PostActionUnsupported, "Skipped by IfNoOtherMaa.");
            }
            else
            {
                await ExecuteActionAsync(PostActionType.Shutdown, cancellationToken);
            }
        }

        if (config.Sleep)
        {
            if (skipSystemActionsForOtherMaa)
            {
                skippedActions.Add(nameof(PostActionType.Sleep));
                await RecordEventAsync(context, nameof(PostActionType.Sleep), UiErrorCode.PostActionUnsupported, "Skipped by IfNoOtherMaa.");
            }
            else
            {
                await ExecuteActionAsync(PostActionType.Sleep, cancellationToken);
            }
        }

        if (config.ExitSelf)
        {
            await ExecuteActionAsync(PostActionType.ExitSelf, cancellationToken);
        }

        var plan = new PostActionExecutionPlan(
            PlannedActions: executedActions,
            SkippedActions: skippedActions,
            SkippedSystemActionsForOtherMaa: skipSystemActionsForOtherMaa);
        if (plan.SkippedActions.Count > 0)
        {
            summary.Add($"Skipped: {string.Join(", ", plan.SkippedActions)}.");
        }

        if (plan.PlannedActions.Count > 0)
        {
            summary.Add($"Executed: {string.Join(", ", plan.PlannedActions)}.");
        }

        if (failures.Count > 0)
        {
            summary.Add($"Failed: {string.Join(", ", failures)}.");
            return UiOperationResult.Fail(UiErrorCode.PostActionExecutionFailed, string.Join(" ", summary));
        }

        return UiOperationResult.Ok(summary.Count == 0 ? "Post actions executed." : string.Join(" ", summary));

        async Task ExecuteActionAsync(PostActionType action, CancellationToken token)
        {
            var request = BuildExecutorRequest();
            var capability = GetCapability(action, request);
            if (!capability.Supported)
            {
                skippedActions.Add(action.ToString());
                await RecordEventAsync(context, action.ToString(), UiErrorCode.PostActionUnsupported, capability.Message);
                return;
            }

            if (IsPowerAction(action))
            {
                var promptResult = await _promptService.ConfirmPowerActionAsync(
                    new PostActionPromptRequest(action, PowerActionPromptCountdown, ResolveCurrentLanguage()),
                    token);
                if (!promptResult.Success)
                {
                    if (promptResult.UserCancelled)
                    {
                        skippedActions.Add(action.ToString());
                        await RecordEventAsync(context, action.ToString(), UiErrorCode.PostActionCancelled, promptResult.Message);
                        return;
                    }

                    failures.Add($"{action}:{promptResult.Error?.Code ?? UiErrorCode.PostActionExecutionFailed}");
                    await RecordErrorAsync(
                        context,
                        action.ToString(),
                        promptResult.Error?.Code ?? UiErrorCode.PostActionExecutionFailed,
                        promptResult.Message,
                        token);
                    return;
                }
            }

            try
            {
                var result = await ExecuteResolvedActionAsync(action, request, token);
                if (!result.Success)
                {
                    failures.Add($"{action}:{result.ErrorCode ?? UiErrorCode.PostActionExecutionFailed}");
                    await RecordErrorAsync(
                        context,
                        action.ToString(),
                        result.ErrorCode ?? UiErrorCode.PostActionExecutionFailed,
                        result.Message);
                    return;
                }

                executedActions.Add(action.ToString());
                await RecordEventAsync(context, action.ToString(), result.ErrorCode, result.Message);
                if (RequiresSettleDelay(action))
                {
                    await Task.Delay(DeviceActionSettleDelay, token);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{action}:{UiErrorCode.PostActionExecutionFailed}");
                await RecordErrorAsync(
                    context,
                    action.ToString(),
                    UiErrorCode.PostActionExecutionFailed,
                    ex.Message);
            }
        }
    }

    private PlatformCapabilityStatus GetCapability(PostActionConfig config, PostActionType action)
        => GetCapability(action, BuildExecutorRequest());

    private PlatformCapabilityStatus GetCapability(PostActionType action, PostActionExecutorRequest request)
    {
        var fallback = _executor.GetCapabilityMatrix(request).Get(action);
        return action switch
        {
            PostActionType.BackToAndroidHome => ResolveNativeCapability(
                _coreBridge?.SupportsBackToHome == true,
                "maa-core",
                "Back to Android home is available via MaaCore.",
                fallback),
            PostActionType.ExitArknights => ResolveNativeCapability(
                _coreBridge?.SupportsStartCloseDown == true,
                "maa-core",
                "Exit Arknights is available via MaaCore CloseDown.",
                fallback),
            PostActionType.ExitSelf => ResolveNativeCapability(
                _appLifecycleService?.SupportsExit == true,
                "app-lifecycle",
                "Exit MAA is available via application lifecycle.",
                fallback),
            _ => fallback,
        };
    }

    private async Task<PlatformOperationResult> ExecuteResolvedActionAsync(
        PostActionType action,
        PostActionExecutorRequest request,
        CancellationToken cancellationToken)
    {
        return action switch
        {
            PostActionType.BackToAndroidHome => await ExecuteBackToHomeAsync(request, cancellationToken),
            PostActionType.ExitArknights => await ExecuteCloseDownAsync(request, cancellationToken),
            PostActionType.ExitSelf => await ExecuteExitSelfAsync(request, cancellationToken),
            _ => await _executor.ExecuteAsync(action, request, cancellationToken),
        };
    }

    private async Task<PlatformOperationResult> ExecuteBackToHomeAsync(
        PostActionExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (_coreBridge?.SupportsBackToHome == true)
        {
            var result = await _coreBridge.BackToHomeAsync(cancellationToken);
            if (result.Success)
            {
                return PlatformOperation.NativeSuccess("maa-core", "Back to Android home executed via MaaCore.", "post-action.BackToAndroidHome");
            }

            return MapCoreFailure(result, "maa-core", PostActionType.BackToAndroidHome);
        }

        return await _executor.ExecuteAsync(PostActionType.BackToAndroidHome, request, cancellationToken);
    }

    private async Task<PlatformOperationResult> ExecuteCloseDownAsync(
        PostActionExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (_coreBridge?.SupportsStartCloseDown == true)
        {
            var result = await _coreBridge.StartCloseDownAsync(request.ClientType ?? string.Empty, cancellationToken);
            if (result.Success)
            {
                return PlatformOperation.NativeSuccess("maa-core", "Exit Arknights executed via MaaCore CloseDown.", "post-action.ExitArknights");
            }

            return MapCoreFailure(result, "maa-core", PostActionType.ExitArknights);
        }

        return await _executor.ExecuteAsync(PostActionType.ExitArknights, request, cancellationToken);
    }

    private async Task<PlatformOperationResult> ExecuteExitSelfAsync(
        PostActionExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (_appLifecycleService?.SupportsExit == true)
        {
            var result = await _appLifecycleService.ExitAsync(cancellationToken);
            if (result.Success)
            {
                return PlatformOperation.NativeSuccess("app-lifecycle", result.Message, "post-action.ExitSelf");
            }

            return PlatformOperation.Failed(
                "app-lifecycle",
                result.Message,
                result.Error?.Code ?? UiErrorCode.PostActionExecutionFailed,
                "post-action.ExitSelf");
        }

        return await _executor.ExecuteAsync(PostActionType.ExitSelf, request, cancellationToken);
    }

    private PostActionExecutorRequest BuildExecutorRequest()
    {
        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return new PostActionExecutorRequest();
        }

        var globalValues = _configService.CurrentConfig.GlobalValues;
        return new PostActionExecutorRequest(
            ConnectAddress: ReadStringSetting(profile, globalValues, "ConnectAddress", ConfigurationKeys.ConnectAddress),
            ConnectConfig: ReadStringSetting(profile, globalValues, "ConnectConfig", ConfigurationKeys.ConnectConfig),
            AdbPath: ReadStringSetting(profile, globalValues, "AdbPath", ConfigurationKeys.AdbPath),
            ClientType: ReadStringSetting(profile, globalValues, "ClientType", ConfigurationKeys.ClientType),
            MuMu12ExtrasEnabled: ReadBooleanSetting(profile, globalValues, false, "MuMu12ExtrasEnabled", ConfigurationKeys.MuMu12ExtrasEnabled),
            MuMu12EmulatorPath: ReadStringSetting(profile, globalValues, "MuMu12EmulatorPath", ConfigurationKeys.MuMu12EmulatorPath),
            MuMuBridgeConnection: ReadBooleanSetting(profile, globalValues, false, "MuMuBridgeConnection", ConfigurationKeys.MumuBridgeConnection),
            MuMu12Index: ReadStringSetting(profile, globalValues, "MuMu12Index", ConfigurationKeys.MuMu12Index),
            LdPlayerExtrasEnabled: ReadBooleanSetting(profile, globalValues, false, "LdPlayerExtrasEnabled", ConfigurationKeys.LdPlayerExtrasEnabled),
            LdPlayerEmulatorPath: ReadStringSetting(profile, globalValues, "LdPlayerEmulatorPath", ConfigurationKeys.LdPlayerEmulatorPath),
            LdPlayerManualSetIndex: ReadBooleanSetting(profile, globalValues, false, "LdPlayerManualSetIndex", ConfigurationKeys.LdPlayerManualSetIndex),
            LdPlayerIndex: ReadStringSetting(profile, globalValues, "LdPlayerIndex", ConfigurationKeys.LdPlayerIndex));
    }

    private string? ResolveCurrentLanguage()
    {
        if (_configService.TryGetCurrentProfile(out var profile))
        {
            var profileLanguage = ReadStringSetting(
                profile,
                _configService.CurrentConfig.GlobalValues,
                ConfigurationKeys.Localization);
            if (!string.IsNullOrWhiteSpace(profileLanguage))
            {
                return profileLanguage;
            }
        }

        return ReadStringSetting(null, _configService.CurrentConfig.GlobalValues, ConfigurationKeys.Localization);
    }

    private static bool HasOtherMaaProcess()
    {
        try
        {
            var processName = Environment.ProcessPath is null
                ? "MAA"
                : Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            return processes.Length > 1;
        }
        catch
        {
            return false;
        }
    }

    private async Task RecordEventAsync(
        PostActionExecutionContext context,
        string action,
        string? errorCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _diagnostics.RecordEventAsync(
            "PostAction.Execute",
            BuildDiagnosticPayload(
                context,
                action,
                errorCode,
                message),
            cancellationToken);
    }

    private async Task RecordErrorAsync(
        PostActionExecutionContext context,
        string action,
        string errorCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _diagnostics.RecordErrorAsync(
            "PostAction.Execute",
            BuildDiagnosticPayload(context, action, errorCode, message),
            cancellationToken: cancellationToken);
    }

    private static string BuildDiagnosticPayload(
        PostActionExecutionContext context,
        string action,
        string? errorCode,
        string message)
    {
        var runId = string.IsNullOrWhiteSpace(context.RunId) ? "-" : context.RunId;
        var taskIndex = context.TaskIndex?.ToString() ?? "-";
        var code = string.IsNullOrWhiteSpace(errorCode) ? "-" : errorCode;
        return $"runId={runId} taskIndex={taskIndex} module=PostAction action={action} errorCode={code} message={message}";
    }

    private static PlatformCapabilityStatus ResolveNativeCapability(
        bool nativeSupported,
        string provider,
        string message,
        PlatformCapabilityStatus fallback)
    {
        return nativeSupported
            ? new PlatformCapabilityStatus(
                true,
                message,
                provider,
                fallback.Supported || fallback.HasFallback,
                fallback.Supported ? "legacy-command" : fallback.FallbackMode)
            : fallback;
    }

    private static PlatformOperationResult MapCoreFailure(
        CoreResult<bool> result,
        string provider,
        PostActionType action)
    {
        var errorCode = result.Error?.Code is CoreErrorCode.NotSupported or CoreErrorCode.NotImplemented or CoreErrorCode.NotInitialized or CoreErrorCode.Disposed
            ? PlatformErrorCodes.PostActionUnsupported
            : PlatformErrorCodes.PostActionExecutionFailed;
        return PlatformOperation.Failed(
            provider,
            result.Error?.Message ?? $"Core post action failed: {action}.",
            errorCode,
            $"post-action.{action}");
    }

    private static bool RequiresSettleDelay(PostActionType action)
        => action is PostActionType.BackToAndroidHome or PostActionType.ExitArknights or PostActionType.ExitEmulator;

    private static bool IsPowerAction(PostActionType action)
        => action is PostActionType.Hibernate or PostActionType.Shutdown or PostActionType.Sleep;

    private static PostActionConfig NormalizeForPersistentStorage(PostActionConfig source, out bool changed)
    {
        var normalized = source.Clone();
        changed = false;

        if (OperatingSystem.IsMacOS() && normalized.Hibernate)
        {
            normalized.Hibernate = false;
            if (!normalized.Sleep)
            {
                normalized.Sleep = true;
            }

            changed = true;
        }

        return normalized;
    }

    private static PostActionConfig NormalizeForRuntime(PostActionConfig source, out bool changed)
    {
        var normalized = NormalizeForPersistentStorage(source, out changed);

        if (!OperatingSystem.IsWindows() && normalized.ExitEmulator)
        {
            normalized.ExitEmulator = false;
            changed = true;
        }

        return normalized;
    }

    private static string? ReadStringSetting(
        UnifiedProfile? profile,
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        params string[] keys)
    {
        if (profile is not null && TryReadString(profile.Values, out var profileValue, keys))
        {
            return profileValue;
        }

        return TryReadString(globalValues, out var globalValue, keys) ? globalValue : null;
    }

    private static bool ReadBooleanSetting(
        UnifiedProfile? profile,
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        bool fallback,
        params string[] keys)
    {
        if (profile is not null && TryReadBoolean(profile.Values, out var profileValue, keys))
        {
            return profileValue;
        }

        return TryReadBoolean(globalValues, out var globalValue, keys) ? globalValue : fallback;
    }

    private static bool TryReadString(
        IReadOnlyDictionary<string, JsonNode?> values,
        out string? value,
        params string[] keys)
    {
        value = null;
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
            {
                value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, JsonNode?> values,
        out bool value,
        params string[] keys)
    {
        value = false;
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var node) || node is null || node is not JsonValue jsonValue)
            {
                continue;
            }

            if (jsonValue.TryGetValue(out bool boolValue))
            {
                value = boolValue;
                return true;
            }

            if (jsonValue.TryGetValue(out int intValue))
            {
                value = intValue != 0;
                return true;
            }

            if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static PostActionConfig MapLegacyFlags(LegacyPostActionFlags flags)
    {
        return new PostActionConfig
        {
            ExitArknights = flags.HasFlag(LegacyPostActionFlags.ExitArknights),
            BackToAndroidHome = flags.HasFlag(LegacyPostActionFlags.BackToAndroidHome),
            ExitEmulator = flags.HasFlag(LegacyPostActionFlags.ExitEmulator),
            ExitSelf = flags.HasFlag(LegacyPostActionFlags.ExitSelf),
            IfNoOtherMaa = flags.HasFlag(LegacyPostActionFlags.IfNoOtherMaa),
            Hibernate = flags.HasFlag(LegacyPostActionFlags.Hibernate),
            Shutdown = flags.HasFlag(LegacyPostActionFlags.Shutdown),
            Sleep = flags.HasFlag(LegacyPostActionFlags.Sleep),
        };
    }

    private static bool TryReadLegacyFlags(JsonNode node, out LegacyPostActionFlags flags)
    {
        flags = LegacyPostActionFlags.None;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int intValue))
        {
            flags = (LegacyPostActionFlags)intValue;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (int.TryParse(text, out intValue))
            {
                flags = (LegacyPostActionFlags)intValue;
                return true;
            }

            if (Enum.TryParse(text, out LegacyPostActionFlags enumValue))
            {
                flags = enumValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLegacyActionAfterCompleted(JsonNode node, out PostActionConfig config)
    {
        config = PostActionConfig.Default;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int intValue))
        {
            return TryMapLegacyCompletionAction(intValue, out config);
        }

        if (!jsonValue.TryGetValue(out string? text))
        {
            return false;
        }

        if (int.TryParse(text, out intValue))
        {
            return TryMapLegacyCompletionAction(intValue, out config);
        }

        var normalized = NormalizeLegacyCompletionActionName(text);
        return TryMapLegacyCompletionAction(normalized, out config);
    }

    private static string NormalizeLegacyCompletionActionName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool TryMapLegacyCompletionAction(int value, out PostActionConfig config)
    {
        LegacyCompletionAction? action = value switch
        {
            0 => LegacyCompletionAction.DoNothing,
            1 => LegacyCompletionAction.StopGame,
            2 => LegacyCompletionAction.ExitSelf,
            3 => LegacyCompletionAction.ExitEmulator,
            4 => LegacyCompletionAction.ExitEmulatorAndSelf,
            5 => LegacyCompletionAction.Suspend,
            6 => LegacyCompletionAction.Hibernate,
            7 => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate,
            8 => LegacyCompletionAction.Shutdown,
            9 => LegacyCompletionAction.HibernateWithoutPersist,
            10 => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist,
            11 => LegacyCompletionAction.ShutdownWithoutPersist,
            12 => LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate,
            13 => LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown,
            14 => LegacyCompletionAction.BackToAndroidHome,
            _ => null,
        };

        if (action is null)
        {
            config = PostActionConfig.Default;
            return false;
        }

        config = MapLegacyCompletionAction(action.Value);
        return true;
    }

    private static bool TryMapLegacyCompletionAction(string normalizedAction, out PostActionConfig config)
    {
        LegacyCompletionAction? action = normalizedAction switch
        {
            "" or "none" or "noaction" or "donothing" or "nothing" => LegacyCompletionAction.DoNothing,
            "stopgame" or "exitarknights" or "closearknights" => LegacyCompletionAction.StopGame,
            "backtoandroidhome" or "backtohome" or "returntoandroidhome" or "returntohome" => LegacyCompletionAction.BackToAndroidHome,
            "exitemulator" or "closeemulator" => LegacyCompletionAction.ExitEmulator,
            "exitself" or "exitmaa" or "closemaa" or "quitmaa" => LegacyCompletionAction.ExitSelf,
            "exitemulatorandself" => LegacyCompletionAction.ExitEmulatorAndSelf,
            "hibernate" => LegacyCompletionAction.Hibernate,
            "hibernatewithoutpersist" => LegacyCompletionAction.HibernateWithoutPersist,
            "shutdown" or "poweroff" => LegacyCompletionAction.Shutdown,
            "shutdownwithoutpersist" => LegacyCompletionAction.ShutdownWithoutPersist,
            "sleep" or "suspend" or "standby" => LegacyCompletionAction.Suspend,
            "exitemulatorandselfandhibernate" => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate,
            "exitemulatorandselfandhibernatewithoutpersist" => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist,
            "exitemulatorandselfifothermaaelseexitemulatorandselfandhibernate" => LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate,
            "exitselfifothermaaelseshutdown" => LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown,
            _ => null,
        };

        if (action is null)
        {
            config = PostActionConfig.Default;
            return false;
        }

        config = MapLegacyCompletionAction(action.Value);
        return true;
    }

    private static PostActionConfig MapLegacyCompletionAction(LegacyCompletionAction action)
    {
        return action switch
        {
            LegacyCompletionAction.DoNothing => PostActionConfig.Default,
            LegacyCompletionAction.StopGame => new PostActionConfig { ExitArknights = true },
            LegacyCompletionAction.ExitSelf => new PostActionConfig { ExitSelf = true },
            LegacyCompletionAction.ExitEmulator => new PostActionConfig { ExitEmulator = true },
            LegacyCompletionAction.ExitEmulatorAndSelf => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
            },
            LegacyCompletionAction.Suspend => new PostActionConfig { Sleep = true },
            LegacyCompletionAction.Hibernate => new PostActionConfig { Hibernate = true },
            LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                Hibernate = true,
            },
            LegacyCompletionAction.Shutdown => new PostActionConfig { Shutdown = true },
            LegacyCompletionAction.HibernateWithoutPersist => new PostActionConfig { Hibernate = true },
            LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                Hibernate = true,
            },
            LegacyCompletionAction.ShutdownWithoutPersist => new PostActionConfig { Shutdown = true },
            LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                IfNoOtherMaa = true,
                Hibernate = true,
            },
            // The structured model cannot express "exit self only when other MAA exists";
            // this preserves the shutdown branch and keeps ExitSelf for the other-MAA branch.
            LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown => new PostActionConfig
            {
                ExitSelf = true,
                IfNoOtherMaa = true,
                Shutdown = true,
            },
            LegacyCompletionAction.BackToAndroidHome => new PostActionConfig { BackToAndroidHome = true },
            _ => PostActionConfig.Default,
        };
    }

    [Flags]
    private enum LegacyPostActionFlags
    {
        None = 0,
        ExitArknights = 1 << 0,
        BackToAndroidHome = 1 << 1,
        ExitEmulator = 1 << 2,
        ExitSelf = 1 << 3,
        IfNoOtherMaa = 1 << 4,
        Hibernate = 1 << 5,
        Shutdown = 1 << 6,
        Sleep = 1 << 7,
    }

    private enum LegacyCompletionAction
    {
        DoNothing,
        StopGame,
        ExitSelf,
        ExitEmulator,
        ExitEmulatorAndSelf,
        Suspend,
        Hibernate,
        ExitEmulatorAndSelfAndHibernate,
        Shutdown,
        HibernateWithoutPersist,
        ExitEmulatorAndSelfAndHibernateWithoutPersist,
        ShutdownWithoutPersist,
        ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate,
        ExitSelfIfOtherMaaElseShutdown,
        BackToAndroidHome,
    }
}

public sealed class PlatformCapabilityFeatureService : IPlatformCapabilityService
{
    private readonly PlatformServiceBundle _platform;
    private readonly UiDiagnosticsService _diagnostics;

    public event EventHandler<TrayCommandEvent>? TrayCommandInvoked;

    public event EventHandler<TrayMenuRequestEvent>? TrayMenuRequested;

    public event EventHandler<GlobalHotkeyTriggeredEvent>? GlobalHotkeyTriggered;

    public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

    public PlatformCapabilityFeatureService(PlatformServiceBundle platform, UiDiagnosticsService diagnostics)
    {
        _platform = platform;
        _diagnostics = diagnostics;
        _platform.TrayService.CommandInvoked += OnTrayCommandInvoked;
        _platform.TrayService.MenuRequested += OnTrayMenuRequested;
        _platform.HotkeyService.Triggered += OnGlobalHotkeyTriggered;
        _platform.OverlayService.OverlayStateChanged += OnOverlayStateChanged;
    }

    public Task<UiOperationResult<PlatformCapabilitySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = PlatformCapabilitySnapshotFactory.FromBundle(_platform);
        return Task.FromResult(UiOperationResult<PlatformCapabilitySnapshot>.Ok(snapshot, "Platform capability snapshot loaded."));
    }

    public Task<UiOperationResult> InitializeTrayAsync(string appTitle, CancellationToken cancellationToken = default)
    {
        return InitializeTrayAsync(appTitle, null, cancellationToken);
    }

    public async Task<UiOperationResult> InitializeTrayAsync(
        string appTitle,
        TrayMenuText? menuText,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteTrayOperationAsync(
            "initialize",
            "tray.initialize",
            PlatformErrorCodes.TrayInitFailed,
            cancellationToken,
            ct => _platform.TrayService.InitializeAsync(appTitle, menuText, ct));
    }

    public async Task<UiOperationResult> ShutdownTrayAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteTrayOperationAsync(
            "shutdown",
            "tray.shutdown",
            PlatformErrorCodes.TrayInitFailed,
            cancellationToken,
            ct => _platform.TrayService.ShutdownAsync(ct));
    }

    public async Task<UiOperationResult> ShowTrayMessageAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        return await ExecuteTrayOperationAsync(
            "show",
            "tray.show",
            PlatformErrorCodes.TrayMenuDispatchFailed,
            cancellationToken,
            ct => _platform.TrayService.ShowAsync(title, message, ct));
    }

    public async Task<UiOperationResult> SetTrayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        return await ExecuteTrayOperationAsync(
            "set-visible",
            "tray.setVisible",
            PlatformErrorCodes.TrayMenuDispatchFailed,
            cancellationToken,
            ct => _platform.TrayService.SetVisibleAsync(visible, ct));
    }

    public async Task<UiOperationResult> SetTrayMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
    {
        return await ExecuteTrayOperationAsync(
            "set-menu",
            "tray.setMenuState",
            PlatformErrorCodes.TrayMenuDispatchFailed,
            cancellationToken,
            ct => _platform.TrayService.SetMenuStateAsync(state, ct));
    }

    public async Task<UiOperationResult> SendSystemNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var result = await _platform.NotificationService.NotifyAsync(title, message, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Notification, "notify", result, cancellationToken);
    }

    public async Task<UiOperationResult> RegisterGlobalHotkeyAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        var batch = await RegisterGlobalHotkeysAsync(
            [new HotkeyBindingRequest(name, gesture)],
            cancellationToken);
        if (!batch.Success || batch.Value is null)
        {
            return UiOperationResult.Fail(
                batch.Error?.Code ?? UiErrorCode.HotkeyRegistrationFailed,
                batch.Message,
                batch.Error?.Details);
        }

        var result = batch.Value.FirstOrDefault();
        if (result is null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.HotkeyRegistrationFailed,
                "Global hotkey registration batch returned no result.");
        }

        if (!result.Result.Success)
        {
            return UiOperationResult.Fail(
                result.Result.ErrorCode ?? UiErrorCode.HotkeyRegistrationFailed,
                result.Result.Message);
        }

        return UiOperationResult.Ok(result.Result.Message);
    }

    public async Task<UiOperationResult<IReadOnlyList<HotkeyRegistrationOutcome>>> RegisterGlobalHotkeysAsync(
        IReadOnlyList<HotkeyBindingRequest> requests,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _platform.HotkeyService.RegisterBatchAsync(requests, cancellationToken);
            foreach (var result in results)
            {
                await _diagnostics.RecordPlatformEventAsync(
                    PlatformCapabilityId.Hotkey,
                    "register",
                    result.Result,
                    cancellationToken);
                if (!result.Result.Success)
                {
                    await _diagnostics.RecordFailedResultAsync(
                        $"PlatformCapability.Hotkey.register.{result.Name}",
                        UiOperationResult.Fail(
                            result.Result.ErrorCode ?? UiErrorCode.HotkeyRegistrationFailed,
                            result.Result.Message),
                        cancellationToken);
                }
            }

            return UiOperationResult<IReadOnlyList<HotkeyRegistrationOutcome>>.Ok(
                results,
                "Global hotkey batch registration completed.");
        }
        catch (Exception ex)
        {
            await _diagnostics.RecordErrorAsync(
                "PlatformCapability.Hotkey.register-batch",
                "Global hotkey batch registration failed unexpectedly.",
                ex,
                cancellationToken);
            return UiOperationResult<IReadOnlyList<HotkeyRegistrationOutcome>>.Fail(
                UiErrorCode.HotkeyRegistrationFailed,
                $"Global hotkey batch registration failed: {ex.Message}",
                ex.ToString());
        }
    }

    public async Task<UiOperationResult> UnregisterGlobalHotkeyAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _platform.HotkeyService.UnregisterAsync(name, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Hotkey, "unregister", result, cancellationToken);
    }

    public async Task<UiOperationResult> ConfigureHotkeyHostContextAsync(
        HotkeyHostContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _platform.HotkeyService.ConfigureHostContextAsync(context, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Hotkey, "configure-host", result, cancellationToken);
    }

    public bool TryDispatchWindowScopedHotkey(HotkeyGesture gesture)
    {
        return _platform.HotkeyService.TryDispatchWindowScopedHotkey(gesture);
    }

    public async Task<UiOperationResult<bool>> GetAutostartEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await _platform.AutostartService.IsEnabledAsync(cancellationToken);
        await _diagnostics.RecordPlatformEventAsync(PlatformCapabilityId.Autostart, "query", result, cancellationToken);
        if (!result.Success)
        {
            await _diagnostics.RecordFailedResultAsync(
                "PlatformCapability.Autostart.query",
                UiOperationResult.Fail(result.ErrorCode ?? UiErrorCode.AutostartQueryFailed, result.Message),
                cancellationToken);
            return UiOperationResult<bool>.Fail(result.ErrorCode ?? UiErrorCode.AutostartQueryFailed, result.Message);
        }

        return result.Value
            ? UiOperationResult<bool>.Ok(true, result.Message)
            : UiOperationResult<bool>.Ok(false, result.Message);
    }

    public async Task<UiOperationResult> SetAutostartEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var result = await _platform.AutostartService.SetEnabledAsync(enabled, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Autostart, "set-enabled", result, cancellationToken);
    }

    private async Task<UiOperationResult> ExecuteTrayOperationAsync(
        string action,
        string operationId,
        string errorCode,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<PlatformOperationResult>> operation)
    {
        try
        {
            var result = await operation(cancellationToken);
            return await ToUiResultAsync(PlatformCapabilityId.Tray, action, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = PlatformOperation.Failed(
                _platform.TrayService.Capability.Provider,
                $"Tray {action} failed unexpectedly: {ex.Message}",
                errorCode,
                operationId);
            await _diagnostics.RecordErrorAsync(
                $"PlatformCapability.Tray.{action}",
                failure.Message,
                ex,
                cancellationToken);
            return await ToUiResultAsync(PlatformCapabilityId.Tray, action, failure, cancellationToken);
        }
    }

    public async Task<UiOperationResult> BindOverlayHostAsync(
        nint hostWindowHandle,
        bool clickThrough,
        double opacity,
        CancellationToken cancellationToken = default)
    {
        var result = await _platform.OverlayService.BindHostWindowAsync(hostWindowHandle, clickThrough, opacity, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Overlay, "bind-host", result, cancellationToken);
    }

    public async Task<UiOperationResult<IReadOnlyList<OverlayTarget>>> QueryOverlayTargetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _platform.OverlayService.QueryTargetsAsync(cancellationToken);
            await _diagnostics.RecordPlatformEventAsync(PlatformCapabilityId.Overlay, "query-targets", result, cancellationToken);
            if (!result.Success)
            {
                await _diagnostics.RecordFailedResultAsync(
                    "PlatformCapability.Overlay.query-targets",
                    UiOperationResult.Fail(result.ErrorCode ?? PlatformErrorCodes.OverlayQueryFailed, result.Message),
                    cancellationToken);
                return UiOperationResult<IReadOnlyList<OverlayTarget>>.Fail(result.ErrorCode ?? PlatformErrorCodes.OverlayQueryFailed, result.Message);
            }

            return UiOperationResult<IReadOnlyList<OverlayTarget>>.Ok(
                result.Value ?? Array.Empty<OverlayTarget>(),
                result.Message);
        }
        catch (Exception ex)
        {
            var failed = PlatformOperation.Failed(
                _platform.OverlayService.Capability.Provider,
                $"Overlay target query failed: {ex.Message}",
                PlatformErrorCodes.OverlayQueryFailed,
                "overlay.query-targets");
            await _diagnostics.RecordPlatformEventAsync(PlatformCapabilityId.Overlay, "query-targets", failed, cancellationToken);
            await _diagnostics.RecordFailedResultAsync(
                "PlatformCapability.Overlay.query-targets",
                UiOperationResult.Fail(PlatformErrorCodes.OverlayQueryFailed, failed.Message),
                cancellationToken);
            return UiOperationResult<IReadOnlyList<OverlayTarget>>.Fail(PlatformErrorCodes.OverlayQueryFailed, failed.Message);
        }
    }

    public async Task<UiOperationResult> SelectOverlayTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var result = await _platform.OverlayService.SelectTargetAsync(targetId, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Overlay, "select-target", result, cancellationToken);
    }

    public async Task<UiOperationResult> SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        var result = await _platform.OverlayService.SetVisibleAsync(visible, cancellationToken);
        return await ToUiResultAsync(PlatformCapabilityId.Overlay, "set-visible", result, cancellationToken);
    }

    private async Task<UiOperationResult> ToUiResultAsync(
        PlatformCapabilityId capability,
        string action,
        PlatformOperationResult result,
        CancellationToken cancellationToken)
    {
        await _diagnostics.RecordPlatformEventAsync(capability, action, result, cancellationToken);
        if (!result.Success)
        {
            await _diagnostics.RecordFailedResultAsync(
                $"PlatformCapability.{capability}.{action}",
                UiOperationResult.Fail(result.ErrorCode ?? UiErrorCode.PlatformOperationFailed, result.Message),
                cancellationToken);
            return UiOperationResult.Fail(result.ErrorCode ?? UiErrorCode.PlatformOperationFailed, result.Message);
        }

        return UiOperationResult.Ok(result.Message);
    }

    private void OnTrayCommandInvoked(object? sender, TrayCommandEvent e)
    {
        _ = _diagnostics.RecordEventAsync(
            "PlatformCapability.TrayCommand",
            $"command={e.Command} source={e.Source} ts={e.Timestamp:O}");
        try
        {
            TrayCommandInvoked?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _ = _diagnostics.RecordErrorAsync(
                "PlatformCapability.TrayCommand",
                "Tray command callback failed.",
                ex);
        }
    }

    private void OnTrayMenuRequested(object? sender, TrayMenuRequestEvent e)
    {
        _ = _diagnostics.RecordEventAsync(
            "PlatformCapability.TrayMenuRequested",
            $"source={e.Source} x={e.ScreenX} y={e.ScreenY} ts={e.Timestamp:O}");
        try
        {
            TrayMenuRequested?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _ = _diagnostics.RecordErrorAsync(
                "PlatformCapability.TrayMenuRequested",
                "Tray menu request callback failed.",
                ex);
        }
    }

    private void OnGlobalHotkeyTriggered(object? sender, GlobalHotkeyTriggeredEvent e)
    {
        _ = _diagnostics.RecordEventAsync(
            "PlatformCapability.HotkeyTriggered",
            $"name={e.Name} gesture={e.Gesture} ts={e.Timestamp:O}");
        try
        {
            GlobalHotkeyTriggered?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _ = _diagnostics.RecordErrorAsync(
                "PlatformCapability.HotkeyTriggered",
                "Hotkey callback failed.",
                ex);
        }
    }

    private void OnOverlayStateChanged(object? sender, OverlayStateChangedEvent e)
    {
        var result = BuildOverlayStateResult(e);
        _ = _diagnostics.RecordPlatformEventAsync(PlatformCapabilityId.Overlay, e.Action, result);
        try
        {
            OverlayStateChanged?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _ = _diagnostics.RecordErrorAsync(
                "PlatformCapability.OverlayStateChanged",
                "Overlay state callback failed.",
                ex);
        }
    }

    private static PlatformOperationResult BuildOverlayStateResult(OverlayStateChangedEvent e)
    {
        return e.Mode switch
        {
            OverlayRuntimeMode.Preview => PlatformOperation.FallbackSuccess(
                e.Provider,
                e.Message,
                operationId: $"overlay.{e.Action}",
                errorCode: e.ErrorCode),
            _ => PlatformOperation.NativeSuccess(
                e.Provider,
                e.Message,
                operationId: $"overlay.{e.Action}"),
        };
    }
}

public sealed class SettingsFeatureService : ISettingsFeatureService
{
    private readonly UnifiedConfigurationService _configService;
    private readonly IPlatformCapabilityService _platformCapabilities;
    private readonly UiDiagnosticsService _diagnostics;

    public SettingsFeatureService(
        UnifiedConfigurationService configService,
        IPlatformCapabilityService platformCapabilities,
        UiDiagnosticsService diagnostics)
    {
        _configService = configService;
        _platformCapabilities = platformCapabilities;
        _diagnostics = diagnostics;
    }

    public async Task<UiOperationResult> SaveGlobalSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return UiOperationResult.Fail(UiErrorCode.SettingKeyMissing, "Setting key cannot be empty.");
        }

        return await SaveGlobalSettingsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = value,
            },
            cancellationToken);
    }

    public async Task<UiOperationResult> SaveGlobalSettingsAsync(
        IReadOnlyDictionary<string, string> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
        {
            return UiOperationResult.Fail(UiErrorCode.SettingBatchEmpty, "No settings were provided.");
        }

        var oldValues = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var existedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in updates)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return UiOperationResult.Fail(UiErrorCode.SettingKeyMissing, "Setting key cannot be empty.");
            }

            if (_configService.CurrentConfig.GlobalValues.TryGetValue(key, out var oldValue))
            {
                existedKeys.Add(key);
            }

            oldValues[key] = oldValue?.DeepClone();
            _configService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        }

        try
        {
            await _configService.SaveAsync(cancellationToken);
            await _diagnostics.RecordEventAsync(
                "Settings",
                $"Saved settings batch: {string.Join(", ", updates.Keys.OrderBy(static k => k, StringComparer.Ordinal))}",
                cancellationToken);
            return UiOperationResult.Ok($"Saved {updates.Count} settings.");
        }
        catch (Exception ex)
        {
            // Rollback in-memory config to keep a consistent read model on failed save.
            foreach (var (key, oldValue) in oldValues)
            {
                if (!existedKeys.Contains(key))
                {
                    _configService.CurrentConfig.GlobalValues.Remove(key);
                    continue;
                }

                _configService.CurrentConfig.GlobalValues[key] = oldValue?.DeepClone();
            }

            await _diagnostics.RecordErrorAsync("Settings.SaveBatch", "Failed to save settings batch.", ex, cancellationToken);
            return UiOperationResult.Fail(UiErrorCode.SettingsSaveFailed, $"Failed to save settings: {ex.Message}");
        }
    }

    public async Task<UiOperationResult> TestNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.SendSystemNotificationAsync(title, message, cancellationToken);
    }

    public async Task<UiOperationResult> RegisterHotkeyAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.RegisterGlobalHotkeyAsync(name, gesture, cancellationToken);
    }

    public async Task<UiOperationResult<bool>> GetAutostartStatusAsync(CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.GetAutostartEnabledAsync(cancellationToken);
    }

    public async Task<UiOperationResult> SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return await _platformCapabilities.SetAutostartEnabledAsync(enabled, cancellationToken);
    }

    public async Task<UiOperationResult<string>> BuildIssueReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var baseDirectory = ResolveIssueReportBaseDirectory();
            var bundlePath = await _diagnostics.BuildIssueReportBundleAsync(baseDirectory, cancellationToken);
            return UiOperationResult<string>.Ok(bundlePath, "Issue report bundle generated.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _diagnostics.RecordErrorAsync("Settings.IssueReport.Build", "Failed to generate issue report bundle.", ex, cancellationToken);
            return UiOperationResult<string>.Fail(
                UiErrorCode.IssueReportBundleBuildFailed,
                $"Failed to generate issue report bundle: {ex.Message}",
                ex.Message);
        }
    }

    private string ResolveIssueReportBaseDirectory()
    {
        var debugDirectory = Path.GetDirectoryName(_diagnostics.EventLogPath);
        if (!string.IsNullOrWhiteSpace(debugDirectory))
        {
            var parent = Directory.GetParent(debugDirectory);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return global::MAAUnified.Compat.Runtime.RuntimeLayout.ResolveRuntimeBaseDirectory();
    }
}

public sealed class AchievementFeatureService : IAchievementFeatureService
{
    private readonly UnifiedConfigurationService? _configService;

    public AchievementFeatureService()
    {
    }

    public AchievementFeatureService(UnifiedConfigurationService configService)
    {
        _configService = configService;
    }

    public Task<UiOperationResult<AchievementPolicy>> LoadPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return Task.FromResult(UiOperationResult<AchievementPolicy>.Fail(
                UiErrorCode.AchievementServiceUnavailable,
                "Achievement service is not initialized."));
        }

        var config = _configService.CurrentConfig;
        var policy = new AchievementPolicy(
            PopupDisabled: ReadProfileBool(config, ConfigurationKeys.AchievementPopupDisabled, false),
            PopupAutoClose: ReadProfileBool(config, ConfigurationKeys.AchievementPopupAutoClose, true));
        return Task.FromResult(UiOperationResult<AchievementPolicy>.Ok(policy, "Loaded achievement policy."));
    }

    public async Task<UiOperationResult> SavePolicyAsync(AchievementPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.AchievementServiceUnavailable, "Achievement service is not initialized.");
        }

        if (!_configService.TryGetCurrentProfile(out var profile))
        {
            return UiOperationResult.Fail(
                UiErrorCode.ProfileMissing,
                $"Current profile `{_configService.CurrentConfig.CurrentProfile}` not found.");
        }

        foreach (var (key, value) in policy.ToProfileSettingUpdates())
        {
            profile.Values[key] = JsonValue.Create(value);
        }

        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok("Achievement policy saved.");
    }

    private static bool ReadProfileBool(UnifiedConfig config, string key, bool fallback)
    {
        if (TryReadBool(config, key, preferProfile: true, out var value))
        {
            return value;
        }

        return fallback;
    }

    private static bool TryReadBool(UnifiedConfig config, string key, bool preferProfile, out bool value)
    {
        if (preferProfile)
        {
            if (TryReadBoolNode(config, key, fromProfile: true, out value))
            {
                return true;
            }

            return TryReadBoolNode(config, key, fromProfile: false, out value);
        }

        if (TryReadBoolNode(config, key, fromProfile: false, out value))
        {
            return true;
        }

        return TryReadBoolNode(config, key, fromProfile: true, out value);
    }

    private static bool TryReadBoolNode(UnifiedConfig config, string key, bool fromProfile, out bool value)
    {
        JsonNode? node = null;
        if (fromProfile)
        {
            if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
                && config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
            {
                profile.Values.TryGetValue(key, out node);
            }
        }
        else
        {
            config.GlobalValues.TryGetValue(key, out node);
        }

        if (node is null)
        {
            value = false;
            return false;
        }

        if (bool.TryParse(node.ToString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            value = parsedInt != 0;
            return true;
        }

        value = false;
        return false;
    }
}

public sealed class AnnouncementFeatureService : IAnnouncementFeatureService
{
    private readonly UnifiedConfigurationService? _configService;

    public AnnouncementFeatureService()
    {
    }

    public AnnouncementFeatureService(UnifiedConfigurationService configService)
    {
        _configService = configService;
    }

    public Task<UiOperationResult<AnnouncementState>> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return Task.FromResult(UiOperationResult<AnnouncementState>.Fail(
                UiErrorCode.AnnouncementServiceUnavailable,
                "Announcement service is not initialized."));
        }

        var config = _configService.CurrentConfig;
        var state = new AnnouncementState(
            AnnouncementInfo: ReadString(config, ConfigurationKeys.AnnouncementInfo, string.Empty),
            DoNotRemindThisAnnouncementAgain: ReadBool(config, ConfigurationKeys.DoNotRemindThisAnnouncementAgain, false),
            DoNotShowAnnouncement: ReadBool(config, ConfigurationKeys.DoNotShowAnnouncement, false));
        return Task.FromResult(UiOperationResult<AnnouncementState>.Ok(state, "Loaded announcement state."));
    }

    public async Task<UiOperationResult> SaveStateAsync(AnnouncementState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.AnnouncementServiceUnavailable, "Announcement service is not initialized.");
        }

        if (state.AnnouncementInfo.Length > 32768)
        {
            return UiOperationResult.Fail(
                UiErrorCode.AnnouncementStateInvalid,
                "Announcement payload is too large.");
        }

        foreach (var (key, value) in state.ToGlobalSettingUpdates())
        {
            _configService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        }

        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok("Announcement state saved.");
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
}

public sealed class DialogFeatureService : IDialogFeatureService
{
    private readonly UiDiagnosticsService _diagnostics;

    public event EventHandler<DialogErrorRaisedEvent>? ErrorRaised;

    public DialogFeatureService(UiDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public Task<string> PrepareDialogPayloadAsync(string dialogType, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"Dialog payload prepared for {dialogType}");
    }

    public async Task<UiOperationResult> ReportErrorAsync(string context, string message, CancellationToken cancellationToken = default)
    {
        var result = UiOperationResult.Fail(UiErrorCode.UiError, message);
        return await ReportErrorAsync(context, result, cancellationToken);
    }

    public async Task<DialogTraceToken> BeginDialogAsync(
        DialogType dialogType,
        string sourceScope,
        string title,
        CancellationToken cancellationToken = default)
    {
        var token = new DialogTraceToken(
            TraceId: Guid.NewGuid().ToString("N"),
            DialogType: dialogType,
            SourceScope: sourceScope,
            OpenedAtUtc: DateTimeOffset.UtcNow);
        await _diagnostics.RecordEventAsync(
            "Dialog.Open",
            $"trace={token.TraceId}; dialog={dialogType}; source={sourceScope}; title={title}",
            cancellationToken);
        return token;
    }

    public async Task<UiOperationResult> RecordDialogActionAsync(
        DialogTraceToken token,
        string action,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await _diagnostics.RecordEventAsync(
            "Dialog.Action",
            $"trace={token.TraceId}; dialog={token.DialogType}; source={token.SourceScope}; action={action}; detail={detail}",
            cancellationToken);
        return UiOperationResult.Ok("Dialog action recorded.");
    }

    public async Task<UiOperationResult> CompleteDialogAsync(
        DialogTraceToken token,
        DialogReturnSemantic semantic,
        string summary,
        CancellationToken cancellationToken = default)
    {
        await _diagnostics.RecordEventAsync(
            "Dialog.Close",
            $"trace={token.TraceId}; dialog={token.DialogType}; source={token.SourceScope}; return={semantic}; summary={summary}",
            cancellationToken);
        return UiOperationResult.Ok("Dialog completion recorded.");
    }

    public async Task<UiOperationResult> ReportErrorAsync(
        string context,
        UiOperationResult result,
        CancellationToken cancellationToken = default)
    {
        await _diagnostics.RecordFailedResultAsync(context, result, cancellationToken);
        ErrorRaised?.Invoke(this, new DialogErrorRaisedEvent(context, result, DateTimeOffset.UtcNow));
        return result;
    }
}
