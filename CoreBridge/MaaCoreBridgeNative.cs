using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.CoreBridge;

public sealed class MaaCoreBridgeNative : IMaaCoreBridge, IMaaCoreBridgeRecovery
{
    private const int MsgInitFailed = 1;
    private const int MsgConnectionInfo = 2;
    private const int MsgAsyncCallInfo = 4;
    private const int AsstStaticOptionCpuOcr = 1;
    private const int AsstStaticOptionGpuOcr = 2;
    private const int AsstInstanceOptionTouchMode = 2;
    private const int AsstInstanceOptionDeploymentWithPause = 3;
    private const int AsstInstanceOptionAdbLiteEnabled = 4;
    private const int AsstInstanceOptionKillAdbOnExit = 5;
    private const int AsstInstanceOptionClientType = 6;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultStopCallTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultScreencapTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AbandonedConnectStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);
    private const int DefaultScreencapWidth = 1280;
    private const int DefaultScreencapHeight = 720;
    private const int DefaultScreencapChannels = 3;
    private const ulong DefaultBgrFrameBufferSize = (ulong)DefaultScreencapWidth * DefaultScreencapHeight * DefaultScreencapChannels;
    private const ulong MaxImageBufferSize = 64UL * 1024UL * 1024UL;
    private static readonly HashSet<string> DefaultClientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "Official",
        "Bilibili",
    };
    private static readonly HashSet<string> ClientTypeConnectionConfigs = new(StringComparer.OrdinalIgnoreCase)
    {
        "WSA",
        "Androws",
    };
    private static readonly IReadOnlyDictionary<string, string> ClientTypeAliasMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Official"] = "Official",
            ["Bilibili"] = "Bilibili",
            ["Txwy"] = "txwy",
            ["YoStarEN"] = "YoStarEN",
            ["YoStarJP"] = "YoStarJP",
            ["YoStarKR"] = "YoStarKR",
        };


    private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _nativeCallLock = new(1, 1);
    private readonly object _sync = new();

    private nint _nativeLibrary;
    private nint _instance;
    private string? _libraryPath;
    private string? _baseDirectory;
    private string? _loadedClientType;
    private string _coreVersion = string.Empty;
    private CoreGpuInitializeInfo? _gpuInitializeInfo;
    private bool _disposed;
    private bool _nativeStopAbandoned;
    private AsstExports? _exports;
    private AsstApiCallbackDelegate? _callbackDelegate;
    private ConnectPendingState? _pendingConnect;
    private readonly List<nint> _abandonedInstances = [];

    public bool SupportsBackToHome => true;

    public bool SupportsStartCloseDown => true;

    public async Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
        CoreInitializeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.BaseDirectory))
        {
            return Fail<CoreInitializeInfo>(CoreErrorCode.InvalidRequest, "Base directory is empty.");
        }

        var runtimeBaseDirectory = RuntimeLayout.NormalizeDirectory(request.BaseDirectory);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                return Fail<CoreInitializeInfo>(CoreErrorCode.Disposed, "Bridge is already disposed.");
            }

            if (_nativeStopAbandoned)
            {
                return Fail<CoreInitializeInfo>(
                    CoreErrorCode.StopFailed,
                    "Bridge native stop timed out; instance was abandoned to avoid use-after-free.");
            }

            if (_instance != nint.Zero && _exports is not null && _libraryPath is not null && _baseDirectory is not null)
            {
                if (!string.IsNullOrWhiteSpace(request.ClientType)
                    && !string.Equals(_loadedClientType, request.ClientType, StringComparison.OrdinalIgnoreCase))
                {
                    var clientLoad = await RunNativeCallAsync(
                        () => LoadClientResource(request.ClientType, _baseDirectory, _exports),
                        cancellationToken).ConfigureAwait(false);
                    if (!clientLoad.Success)
                    {
                        return CoreResult<CoreInitializeInfo>.Fail(clientLoad.Error!);
                    }
                }

                return CoreResult<CoreInitializeInfo>.Ok(BuildInitializeInfo(_baseDirectory, _libraryPath, request.ClientType, _gpuInitializeInfo));
            }

            var libraryNameResult = ResolveLibraryName();
            if (!libraryNameResult.Success)
            {
                return CoreResult<CoreInitializeInfo>.Fail(libraryNameResult.Error!);
            }

            var libraryPath = Path.Combine(runtimeBaseDirectory, libraryNameResult.Value!);
            if (!File.Exists(libraryPath))
            {
                return Fail<CoreInitializeInfo>(CoreErrorCode.LibraryNotFound, $"MaaCore library was not found: {libraryPath}");
            }

            var resourceDir = Path.Combine(runtimeBaseDirectory, "resource");
            if (!Directory.Exists(resourceDir))
            {
                return Fail<CoreInitializeInfo>(CoreErrorCode.ResourceNotFound, $"Resource directory was not found: {resourceDir}");
            }

            nint loadedLibrary;
            try
            {
                loadedLibrary = NativeLibrary.Load(libraryPath);
            }
            catch (Exception ex)
            {
                return Fail<CoreInitializeInfo>(CoreErrorCode.LibraryLoadFailed, $"Failed to load MaaCore library at `{libraryPath}`.", ex: ex);
            }

            if (!TryLoadExports(loadedLibrary, out var exports, out var missingSymbol))
            {
                NativeLibrary.Free(loadedLibrary);
                return Fail<CoreInitializeInfo>(CoreErrorCode.SymbolMissing, $"Required MaaCore symbol is missing: {missingSymbol}");
            }

            _callbackDelegate = OnNativeCallback;
            var nativeInitialize = await RunNativeCallAsync(
                () =>
                {
                    var gpuInitializeInfo = ApplyGpuInitialization(request.Gpu, exports);

                    if (!AsBool(exports.AsstSetUserDir(runtimeBaseDirectory)))
                    {
                        NativeLibrary.Free(loadedLibrary);
                        return Fail<NativeInitializeResult>(
                            CoreErrorCode.ResourceLoadFailed,
                            "AsstSetUserDir returned false.");
                    }

                    if (!AsBool(exports.AsstLoadResource(runtimeBaseDirectory)))
                    {
                        NativeLibrary.Free(loadedLibrary);
                        return Fail<NativeInitializeResult>(
                            CoreErrorCode.ResourceLoadFailed,
                            "AsstLoadResource(baseDir) returned false.");
                    }

                    if (!string.IsNullOrWhiteSpace(request.ClientType))
                    {
                        var clientLoad = LoadClientResource(request.ClientType, runtimeBaseDirectory, exports);
                        if (!clientLoad.Success)
                        {
                            NativeLibrary.Free(loadedLibrary);
                            return CoreResult<NativeInitializeResult>.Fail(clientLoad.Error!);
                        }
                    }

                    var instance = exports.AsstCreateEx(_callbackDelegate, nint.Zero);
                    if (instance == nint.Zero)
                    {
                        NativeLibrary.Free(loadedLibrary);
                        return Fail<NativeInitializeResult>(
                            CoreErrorCode.CoreInstanceCreateFailed,
                            "AsstCreateEx returned null.");
                    }

                    var version = Marshal.PtrToStringUTF8(exports.AsstGetVersion()) ?? string.Empty;
                    return CoreResult<NativeInitializeResult>.Ok(new NativeInitializeResult(
                        instance,
                        gpuInitializeInfo,
                        version));
                },
                cancellationToken).ConfigureAwait(false);
            if (!nativeInitialize.Success || nativeInitialize.Value is null)
            {
                return CoreResult<CoreInitializeInfo>.Fail(nativeInitialize.Error!);
            }

            _nativeLibrary = loadedLibrary;
            _exports = exports;
            _instance = nativeInitialize.Value.Instance;
            _libraryPath = libraryPath;
            _baseDirectory = runtimeBaseDirectory;
            _loadedClientType = request.ClientType;
            _gpuInitializeInfo = nativeInitialize.Value.GpuInitializeInfo;
            _coreVersion = nativeInitialize.Value.Version;

            return CoreResult<CoreInitializeInfo>.Ok(BuildInitializeInfo(runtimeBaseDirectory, libraryPath, request.ClientType, nativeInitialize.Value.GpuInitializeInfo));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<CoreResult<bool>> ConnectAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionInfo.Address))
        {
            return Fail<bool>(CoreErrorCode.InvalidRequest, "Connection address is empty.");
        }

        if (string.IsNullOrWhiteSpace(connectionInfo.ConnectConfig))
        {
            return Fail<bool>(CoreErrorCode.InvalidRequest, "Connection config is empty.");
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = NormalizeTimeout(connectionInfo.Timeout, DefaultConnectTimeout);
            var prepare = await PrepareConnectionAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
            if (!prepare.Success)
            {
                return prepare;
            }

            ConnectPendingState? pending = null;
            var startResult = await RunNativeCallAsync(
                () =>
                {
                    var status = EnsureReady();
                    if (!status.Success)
                    {
                        return CoreResult<bool>.Fail(status.Error!);
                    }

                    var exports = _exports!;
                    var handle = _instance;
                    pending = new ConnectPendingState(asyncCallId: null);
                    lock (_sync)
                    {
                        _pendingConnect = pending;
                    }

                    var asyncCallId = exports.AsstAsyncConnect(
                        handle,
                        connectionInfo.AdbPath ?? string.Empty,
                        connectionInfo.Address,
                        connectionInfo.ConnectConfig,
                        block: 0);
                    if (asyncCallId <= 0)
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_pendingConnect, pending))
                            {
                                _pendingConnect = null;
                            }
                        }

                        return CompleteInvalidAsyncConnectStart(pending, asyncCallId);
                    }

                    pending.SetAsyncCallId(asyncCallId);
                    return CoreResult<bool>.Ok(true);
                },
                cancellationToken).ConfigureAwait(false);

            if (!startResult.Success)
            {
                return startResult;
            }

            try
            {
                var completed = await Task.WhenAny(
                    pending!.Completion.Task,
                    Task.Delay(timeout, CancellationToken.None),
                    WaitForCancellationAsync(cancellationToken)).ConfigureAwait(false);
                if (completed == pending.Completion.Task)
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await AbortPendingConnectAsync(
                        pending,
                        CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectTimeout, "Connect was canceled.")),
                        AbandonedConnectStopTimeout).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var timeoutResult = CoreResult<bool>.Fail(new CoreError(
                    CoreErrorCode.ConnectTimeout,
                    $"Connect timed out after {timeout.TotalSeconds:N0}s."));
                await AbortPendingConnectAsync(pending, timeoutResult, AbandonedConnectStopTimeout).ConfigureAwait(false);
                return timeoutResult;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pendingConnect, pending))
                    {
                        _pendingConnect = null;
                    }
                }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
        CoreInstanceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options is null || options.IsEmpty)
        {
            return CoreResult<bool>.Ok(true);
        }

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<bool>.Fail(status.Error!);
                }

                var exports = _exports!;
                if (exports.AsstSetInstanceOption is null)
                {
                    return Fail<bool>(CoreErrorCode.NotSupported, "AsstSetInstanceOption export is unavailable.");
                }

                if (!string.IsNullOrWhiteSpace(options.TouchMode))
                {
                    var normalizedTouchMode = options.TouchMode.Trim();
                    if (!AsBool(exports.AsstSetInstanceOption(_instance, AsstInstanceOptionTouchMode, normalizedTouchMode)))
                    {
                        return Fail<bool>(
                            CoreErrorCode.InvalidRequest,
                            $"Failed to set touch mode to `{normalizedTouchMode}`.");
                    }
                }

                if (options.DeploymentWithPause is bool deploymentWithPause
                    && !AsBool(exports.AsstSetInstanceOption(
                        _instance,
                        AsstInstanceOptionDeploymentWithPause,
                        deploymentWithPause ? "1" : "0")))
                {
                    return Fail<bool>(
                        CoreErrorCode.InvalidRequest,
                        $"Failed to set deployment with pause to `{deploymentWithPause}`.");
                }

                if (options.AdbLiteEnabled is bool adbLiteEnabled
                    && !AsBool(exports.AsstSetInstanceOption(
                        _instance,
                        AsstInstanceOptionAdbLiteEnabled,
                        adbLiteEnabled ? "1" : "0")))
                {
                    return Fail<bool>(
                        CoreErrorCode.InvalidRequest,
                        $"Failed to set ADB Lite enabled to `{adbLiteEnabled}`.");
                }

                if (options.KillAdbOnExit is bool killAdbOnExit
                    && !AsBool(exports.AsstSetInstanceOption(
                        _instance,
                        AsstInstanceOptionKillAdbOnExit,
                        killAdbOnExit ? "1" : "0")))
                {
                    return Fail<bool>(
                        CoreErrorCode.InvalidRequest,
                        $"Failed to set kill ADB on exit to `{killAdbOnExit}`.");
                }

                if (options.ClientType is not null
                    && !AsBool(exports.AsstSetInstanceOption(
                        _instance,
                        AsstInstanceOptionClientType,
                        options.ClientType.Trim())))
                {
                    return Fail<bool>(
                        CoreErrorCode.InvalidRequest,
                        $"Failed to set client type to `{options.ClientType.Trim()}`.");
                }

                return CoreResult<bool>.Ok(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<CoreResult<bool>> RecoverFromAbandonedStopAsync(CancellationToken cancellationToken = default)
        => RecoverAbandonedStopAsync(cancellationToken);

    public async Task<CoreResult<bool>> RecoverAbandonedStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var connectLockTaken = false;
        try
        {
            if (_disposed)
            {
                return Fail<bool>(CoreErrorCode.Disposed, "Bridge is already disposed.");
            }

            if (!_nativeStopAbandoned)
            {
                return Fail<bool>(
                    CoreErrorCode.InvalidRequest,
                    "Bridge recovery is only available after an abandoned native stop.");
            }

            if (_exports is null || _callbackDelegate is null || _baseDirectory is null)
            {
                return Fail<bool>(
                    CoreErrorCode.NotInitialized,
                    "Bridge cannot recover abandoned native stop before initialization.");
            }

            await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            connectLockTaken = true;

            ConnectPendingState? pending;
            lock (_sync)
            {
                pending = _pendingConnect;
                _pendingConnect = null;
            }

            pending?.TryComplete(Fail<bool>(
                CoreErrorCode.StopFailed,
                "Bridge recovered from abandoned native stop; pending connect was discarded."));

            var exports = _exports;
            var baseDirectory = _baseDirectory;
            var clientType = _loadedClientType;
            var callbackDelegate = _callbackDelegate;

            await _nativeCallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var createResult = await Task.Run(
                    () =>
                    {
                        if (!AsBool(exports.AsstLoadResource(baseDirectory)))
                        {
                            return Fail<nint>(
                                CoreErrorCode.ResourceLoadFailed,
                                "AsstLoadResource(baseDir) returned false during abandoned stop recovery.");
                        }

                        if (!string.IsNullOrWhiteSpace(clientType))
                        {
                            var clientLoad = LoadClientResource(clientType, baseDirectory, exports);
                            if (!clientLoad.Success)
                            {
                                return CoreResult<nint>.Fail(clientLoad.Error!);
                            }
                        }

                        var instance = exports.AsstCreateEx(callbackDelegate, nint.Zero);
                        return instance == nint.Zero
                            ? Fail<nint>(
                                CoreErrorCode.CoreInstanceCreateFailed,
                                "AsstCreateEx returned null during abandoned stop recovery.")
                            : CoreResult<nint>.Ok(instance);
                    },
                    CancellationToken.None).ConfigureAwait(false);

                if (!createResult.Success)
                {
                    return CoreResult<bool>.Fail(createResult.Error!);
                }

                var abandonedInstance = _instance;
                if (abandonedInstance != nint.Zero)
                {
                    _abandonedInstances.Add(abandonedInstance);
                }

                _instance = createResult.Value;
                _nativeStopAbandoned = false;
                return CoreResult<bool>.Ok(true);
            }
            finally
            {
                _nativeCallLock.Release();
            }
        }
        finally
        {
            if (connectLockTaken)
            {
                _connectLock.Release();
            }

            _lifecycleLock.Release();
        }
    }

    public async Task<CoreResult<bool>> SetConnectionExtrasAsync(
        string name,
        string extrasJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail<bool>(CoreErrorCode.InvalidRequest, "Connection extras name is empty.");
        }

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<bool>.Fail(status.Error!);
                }

                var exports = _exports!;
                if (exports.AsstSetConnectionExtras is null)
                {
                    return Fail<bool>(CoreErrorCode.NotSupported, "AsstSetConnectionExtras export is unavailable.");
                }

                exports.AsstSetConnectionExtras(name.Trim(), extrasJson ?? "{}");
                return CoreResult<bool>.Ok(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoreResult<bool>> PrepareConnectionAsync(
        CoreConnectionInfo connectionInfo,
        CancellationToken cancellationToken)
    {
        var extras = connectionInfo.Extras ?? CoreConnectionExtras.Empty;
        var options = BuildConnectionInstanceOptions(connectionInfo, extras);
        if (!options.IsEmpty)
        {
            var apply = await ApplyInstanceOptionsAsync(options, cancellationToken).ConfigureAwait(false);
            if (!apply.Success)
            {
                return apply;
            }
        }

        var muMuExtras = BuildMuMu12ExtrasJson(connectionInfo, extras);
        var setMuMu = await SetConnectionExtrasIfSupportedAsync("MuMuEmulator12", muMuExtras, cancellationToken)
            .ConfigureAwait(false);
        if (!setMuMu.Success)
        {
            return setMuMu;
        }

        var ldExtras = BuildLdPlayerExtrasJson(connectionInfo, extras);
        var setLd = await SetConnectionExtrasIfSupportedAsync("LDPlayer", ldExtras, cancellationToken)
            .ConfigureAwait(false);
        if (!setLd.Success)
        {
            return setLd;
        }

        return CoreResult<bool>.Ok(true);
    }

    private async Task<CoreResult<bool>> SetConnectionExtrasIfSupportedAsync(
        string name,
        string extrasJson,
        CancellationToken cancellationToken)
    {
        var set = await SetConnectionExtrasAsync(name, extrasJson, cancellationToken).ConfigureAwait(false);
        return set.Success || set.Error?.Code is CoreErrorCode.NotSupported
            ? CoreResult<bool>.Ok(true)
            : set;
    }

    private static CoreInstanceOptions BuildConnectionInstanceOptions(
        CoreConnectionInfo connectionInfo,
        CoreConnectionExtras extras)
    {
        return new CoreInstanceOptions(
            TouchMode: NormalizeText(extras.TouchMode),
            AdbLiteEnabled: extras.AdbLiteEnabled,
            KillAdbOnExit: extras.KillAdbOnExit,
            ClientType: ResolveConnectionClientType(connectionInfo.ConnectConfig, extras.ClientType));
    }

    private static string ResolveConnectionClientType(string connectConfig, string? clientType)
        => ClientTypeConnectionConfigs.Contains(connectConfig)
            ? NormalizeClientType(clientType)
            : string.Empty;

    private static string BuildMuMu12ExtrasJson(CoreConnectionInfo connectionInfo, CoreConnectionExtras extras)
    {
        if (!string.Equals(connectionInfo.ConnectConfig, "MuMuEmulator12", StringComparison.OrdinalIgnoreCase)
            || !extras.MuMu12ExtrasEnabled)
        {
            return "{}";
        }

        var payload = new Dictionary<string, object?>
        {
            ["path"] = NormalizeText(extras.MuMu12EmulatorPath) ?? string.Empty,
        };

        if (extras.MuMuBridgeConnection)
        {
            payload["index"] = ParseNonNegativeInt(extras.MuMu12Index);
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildLdPlayerExtrasJson(CoreConnectionInfo connectionInfo, CoreConnectionExtras extras)
    {
        if (!string.Equals(connectionInfo.ConnectConfig, "LDPlayer", StringComparison.OrdinalIgnoreCase)
            || !extras.LdPlayerExtrasEnabled)
        {
            return "{}";
        }

        var index = extras.LdPlayerManualSetIndex
            ? ParseNonNegativeInt(extras.LdPlayerIndex)
            : GetLdPlayerIndexFromAddress(connectionInfo.Address);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["path"] = NormalizeText(extras.LdPlayerEmulatorPath) ?? string.Empty,
            ["index"] = index,
            ["pid"] = GetLdPlayerPid(NormalizeText(extras.LdPlayerEmulatorPath), index),
        });
    }

    internal static string BuildMuMu12ExtrasJsonForTest(CoreConnectionInfo connectionInfo)
        => BuildMuMu12ExtrasJson(connectionInfo, connectionInfo.Extras ?? CoreConnectionExtras.Empty);

    internal static string BuildLdPlayerExtrasJsonForTest(CoreConnectionInfo connectionInfo)
        => BuildLdPlayerExtrasJson(connectionInfo, connectionInfo.Extras ?? CoreConnectionExtras.Empty);

    internal static string ResolveConnectionClientTypeForTest(CoreConnectionInfo connectionInfo)
        => ResolveConnectionClientType(
            connectionInfo.ConnectConfig,
            (connectionInfo.Extras ?? CoreConnectionExtras.Empty).ClientType);

    private static int ParseNonNegativeInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;

    private static int GetLdPlayerIndexFromAddress(string? address)
    {
        var normalized = NormalizeText(address);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 0;
        }

        const int baseEmulatorPort = 5554;
        const int baseAdbPort = 5555;
        if (normalized.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalized["emulator-".Length..], out var emulatorPort))
        {
            return Math.Max(0, (emulatorPort - baseEmulatorPort) / 2);
        }

        if (normalized.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalized["127.0.0.1:".Length..], out var adbPort))
        {
            return Math.Max(0, (adbPort - baseAdbPort) / 2);
        }

        return 0;
    }

    private static int GetLdPlayerPid(string? emulatorPath, int index)
    {
        if (string.IsNullOrWhiteSpace(emulatorPath))
        {
            return 0;
        }

        var consolePath = Path.Combine(emulatorPath, "ldconsole.exe");
        if (!File.Exists(consolePath))
        {
            return 0;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = consolePath,
                Arguments = "list2",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return 0;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }

                return 0;
            }

            foreach (var line in output.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length >= 6
                    && int.TryParse(parts[0], out var currentIndex)
                    && currentIndex == index
                    && int.TryParse(parts[5], out var pid))
                {
                    return pid;
                }
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private static string? NormalizeText(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public async Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(task.Type))
        {
            return Fail<int>(CoreErrorCode.InvalidRequest, "Task type is empty.");
        }

        if (string.IsNullOrWhiteSpace(task.ParamsJson))
        {
            return Fail<int>(CoreErrorCode.InvalidRequest, "Task params are empty.");
        }

        try
        {
            using var _ = JsonDocument.Parse(task.ParamsJson);
        }
        catch (Exception ex)
        {
            return Fail<int>(CoreErrorCode.InvalidRequest, "Task params json is invalid.", ex: ex);
        }

        var connectReady = await WaitForPendingConnectReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!connectReady.Success)
        {
            return CoreResult<int>.Fail(connectReady.Error!);
        }

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<int>.Fail(status.Error!);
                }

                var taskId = _exports!.AsstAppendTask(_instance, task.Type, task.ParamsJson);
                if (taskId <= 0)
                {
                    return Fail<int>(CoreErrorCode.AppendTaskFailed, $"AsstAppendTask returned invalid task id for `{task.Type}`.");
                }

                return CoreResult<int>.Ok(taskId);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectReady = await WaitForPendingConnectReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!connectReady.Success)
        {
            return CoreResult<bool>.Fail(connectReady.Error!);
        }

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<bool>.Fail(status.Error!);
                }

                var ok = AsBool(_exports!.AsstStart(_instance));
                if (!ok)
                {
                    return Fail<bool>(CoreErrorCode.StartFailed, "AsstStart returned false.");
                }

                return CoreResult<bool>.Ok(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stopCall = await RunNativeCallWithTimeoutAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<bool>.Fail(status.Error!);
                }

                return AsBool(_exports!.AsstStop(_instance))
                    ? CoreResult<bool>.Ok(true)
                    : Fail<bool>(CoreErrorCode.StopFailed, "AsstStop returned false.");
            },
            DefaultStopCallTimeout,
            "AsstStop",
            CoreErrorCode.StopFailed,
            cancellationToken).ConfigureAwait(false);
        if (!stopCall.Success)
        {
            if (IsAsstStopTimeout(stopCall.Error))
            {
                MarkNativeStopAbandoned();
            }

            return CoreResult<bool>.Fail(stopCall.Error!);
        }

        var stopped = await WaitUntilNotRunningAsync(DefaultStopTimeout, cancellationToken).ConfigureAwait(false);
        if (!stopped.Success)
        {
            return stopped;
        }

        return CoreResult<bool>.Ok(true);
    }

    public async Task<CoreResult<bool>> BackToHomeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<bool>.Fail(status.Error!);
                }

                var ok = AsBool(_exports!.AsstBackToHome(_instance));
                if (!ok)
                {
                    return Fail<bool>(CoreErrorCode.NotImplemented, "AsstBackToHome returned false.");
                }

                return CoreResult<bool>.Ok(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreResult<bool>> StartCloseDownAsync(string clientType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = EnsureReady();
        if (!status.Success)
        {
            return CoreResult<bool>.Fail(status.Error!);
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_type"] = clientType?.Trim() ?? string.Empty,
        });
        var append = await AppendTaskAsync(new CoreTaskRequest("CloseDown", "CloseDown", true, payload), cancellationToken);
        if (!append.Success)
        {
            return CoreResult<bool>.Fail(append.Error!);
        }

        return await StartAsync(cancellationToken);
    }

    public async Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<CoreRuntimeStatus>.Fail(status.Error!);
                }

                var connected = AsBool(_exports!.AsstConnected(_instance));
                var running = AsBool(_exports.AsstRunning(_instance));
                return CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, connected, running));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreResult<bool>> ReloadResourceAsync(
        string? clientType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var status = EnsureReady();
            if (!status.Success)
            {
                return CoreResult<bool>.Fail(status.Error!);
            }

            if (string.IsNullOrWhiteSpace(_baseDirectory))
            {
                return Fail<bool>(CoreErrorCode.NotInitialized, "Bridge base directory is unavailable.");
            }

            return await RunNativeCallAsync(
                () =>
                {
                    var exports = _exports!;
                    var baseDirectory = _baseDirectory!;
                    if (!AsBool(exports.AsstLoadResource(baseDirectory)))
                    {
                        return Fail<bool>(
                            CoreErrorCode.ResourceLoadFailed,
                            "AsstLoadResource(baseDir) returned false during resource reload.");
                    }

                    var effectiveClientType = string.IsNullOrWhiteSpace(clientType)
                        ? _loadedClientType
                        : clientType;
                    if (!string.IsNullOrWhiteSpace(effectiveClientType))
                    {
                        var clientLoad = LoadClientResource(effectiveClientType, baseDirectory, exports);
                        if (!clientLoad.Success)
                        {
                            return CoreResult<bool>.Fail(clientLoad.Error!);
                        }
                    }

                    return CoreResult<bool>.Ok(true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task<CoreResult<bool>> AttachWindowAsync(
        CoreAttachWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Fail<bool>(CoreErrorCode.NotSupported, "AttachWindow is not implemented in MAAUnified bridge yet."));
    }

    public async Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectReady = await WaitForPendingConnectReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!connectReady.Success)
        {
            return CoreResult<byte[]>.Fail(connectReady.Error!);
        }

        return await RunNativeCallAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<byte[]>.Fail(status.Error!);
                }

                var exports = _exports!;
                var nullSize = exports.AsstGetNullSize();
                return ReadImageBytesWithDynamicBuffer(
                    nullSize,
                    DefaultBgrFrameBufferSize,
                    (buffer, bufferSize) => exports.AsstGetImage(_instance, buffer, bufferSize),
                    "AsstGetImage");
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static CoreResult<byte[]> ReadImageBytesWithDynamicBufferForTest(
        ulong nullSize,
        ulong initialBufferSize,
        Func<nint, ulong, ulong> getImage)
        => ReadImageBytesWithDynamicBuffer(nullSize, initialBufferSize, getImage, "AsstGetImage");

    private static CoreResult<byte[]> ReadImageBytesWithDynamicBuffer(
        ulong nullSize,
        ulong initialBufferSize,
        Func<nint, ulong, ulong> getImage,
        string apiName)
    {
        var bufferSize = initialBufferSize == 0 ? DefaultBgrFrameBufferSize : initialBufferSize;
        while (true)
        {
            if (bufferSize > MaxImageBufferSize)
            {
                return Fail<byte[]>(CoreErrorCode.GetImageFailed, "Image buffer size exceeds supported limit.");
            }

            var buffer = Marshal.AllocHGlobal((nint)bufferSize);
            try
            {
                var imageSize = getImage(buffer, bufferSize);
                if (imageSize == 0)
                {
                    return Fail<byte[]>(CoreErrorCode.GetImageFailed, $"{apiName} returned empty image.");
                }

                if (imageSize == nullSize)
                {
                    if (bufferSize >= MaxImageBufferSize)
                    {
                        return Fail<byte[]>(
                            CoreErrorCode.GetImageFailed,
                            $"{apiName} returned null image after buffer grew to {MaxImageBufferSize} bytes.");
                    }

                    bufferSize = GrowImageBuffer(bufferSize);
                    continue;
                }

                if (imageSize > bufferSize)
                {
                    if (imageSize > MaxImageBufferSize)
                    {
                        return Fail<byte[]>(CoreErrorCode.GetImageFailed, $"Image size `{imageSize}` exceeds supported limit.");
                    }

                    bufferSize = imageSize;
                    continue;
                }

                if (imageSize > int.MaxValue)
                {
                    return Fail<byte[]>(CoreErrorCode.GetImageFailed, "Image size is too large.");
                }

                var data = new byte[(int)imageSize];
                Marshal.Copy(buffer, data, 0, data.Length);
                return CoreResult<byte[]>.Ok(data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static ulong GrowImageBuffer(ulong bufferSize)
    {
        if (bufferSize >= MaxImageBufferSize / 2)
        {
            return MaxImageBufferSize;
        }

        return bufferSize * 2;
    }

    public async Task<CoreResult<byte[]>> GetImageBgrAsync(bool forceScreencap = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectReady = await WaitForPendingConnectReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!connectReady.Success)
        {
            return CoreResult<byte[]>.Fail(connectReady.Error!);
        }

        return await RunNativeCallWithTimeoutAsync(
            () =>
            {
                var status = EnsureReady();
                if (!status.Success)
                {
                    return CoreResult<byte[]>.Fail(status.Error!);
                }

                var exports = _exports!;
                if (exports.AsstGetImageBgr is null)
                {
                    return Fail<byte[]>(CoreErrorCode.NotSupported, "AsstGetImageBgr is unavailable in current MaaCore.");
                }

                if (forceScreencap && exports.AsstAsyncScreencap is not null)
                {
                    _ = exports.AsstAsyncScreencap(_instance, 1);
                }

                var nullSize = exports.AsstGetNullSize();
                return ReadImageBytesWithDynamicBuffer(
                    nullSize,
                    DefaultBgrFrameBufferSize,
                    (buffer, bufferSize) => exports.AsstGetImageBgr(_instance, buffer, bufferSize),
                    "AsstGetImageBgr");
            },
            forceScreencap ? DefaultScreencapTimeout : Timeout.InfiniteTimeSpan,
            forceScreencap ? "AsstAsyncScreencap/AsstGetImageBgr" : "AsstGetImageBgr",
            CoreErrorCode.GetImageFailed,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return callback;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();
        var connectLockTaken = false;
        var nativeLockTaken = false;
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ConnectPendingState? pending;
            lock (_sync)
            {
                pending = _pendingConnect;
                _pendingConnect = null;
            }

            pending?.TryComplete(
                CoreResult<bool>.Fail(new CoreError(CoreErrorCode.Disposed, "Bridge disposed while waiting for connect.")));

            connectLockTaken = await _connectLock.WaitAsync(DisposeWaitTimeout).ConfigureAwait(false);
            nativeLockTaken = await _nativeCallLock.WaitAsync(DisposeWaitTimeout).ConfigureAwait(false);
            if (!nativeLockTaken)
            {
                MarkNativeStopAbandoned();
                _callbackChannel.Writer.TryComplete();
                return;
            }

            if (_instance != nint.Zero && _exports is not null && !_nativeStopAbandoned)
            {
                try
                {
                    var stopCall = await InvokeAsstStopWithTimeoutAsync(_exports, _instance, TimeSpan.FromSeconds(1))
                        .ConfigureAwait(false);
                    if (!stopCall.Success)
                    {
                        MarkNativeStopAbandoned();
                        _callbackChannel.Writer.TryComplete();
                        return;
                    }

                    _ = await Task.Run(
                            async () => await WaitUntilNotRunningCoreAsync(
                                    _exports,
                                    _instance,
                                    TimeSpan.FromSeconds(1),
                                    CancellationToken.None)
                                .ConfigureAwait(false),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignored during dispose
                }

                if (!_nativeStopAbandoned)
                {
                    try
                    {
                        await Task.Run(() => _exports.AsstDestroy(_instance), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored during dispose
                    }

                    _instance = nint.Zero;
                }
            }

            if (!_nativeStopAbandoned && _abandonedInstances.Count == 0)
            {
                _exports = null;
                _callbackDelegate = null;
                _gpuInitializeInfo = null;
            }

            if (!_nativeStopAbandoned && _abandonedInstances.Count == 0 && _nativeLibrary != nint.Zero)
            {
                try
                {
                    NativeLibrary.Free(_nativeLibrary);
                }
                catch
                {
                    // ignored during dispose
                }

                _nativeLibrary = nint.Zero;
            }

            _callbackChannel.Writer.TryComplete();
        }
        finally
        {
            if (nativeLockTaken)
            {
                _nativeCallLock.Release();
            }

            if (connectLockTaken)
            {
                _connectLock.Release();
            }

            _lifecycleLock.Release();
        }
    }

    private void OnNativeCallback(int msg, nint detailsJson, nint customArg)
    {
        var payloadJson = Marshal.PtrToStringUTF8(detailsJson) ?? "{}";
        var callback = new CoreCallbackEvent(msg, ResolveMessageName(msg), payloadJson, DateTimeOffset.UtcNow);
        _callbackChannel.Writer.TryWrite(callback);
        TryHandlePendingConnect(callback);
    }

    private void TryHandlePendingConnect(CoreCallbackEvent callback)
    {
        ConnectPendingState? pending;
        lock (_sync)
        {
            pending = _pendingConnect;
        }

        if (pending is null)
        {
            return;
        }

        TryHandlePendingConnectCallback(pending, callback);
    }

    internal static CoreResult<bool>? ApplyConnectCallbacksForTest(
        int? asyncCallId,
        params CoreCallbackEvent[] callbacks)
    {
        var pending = new ConnectPendingState(asyncCallId);
        foreach (var callback in callbacks)
        {
            TryHandlePendingConnectCallback(pending, callback);
            if (pending.TryGetCompletedResult(out var result))
            {
                return result;
            }
        }

        return null;
    }

    internal static CoreResult<bool> CompleteInvalidAsyncConnectStartForTest(int asyncCallId)
    {
        var pending = new ConnectPendingState(null);
        return CompleteInvalidAsyncConnectStart(pending, asyncCallId);
    }

    private static CoreResult<bool> CompleteInvalidAsyncConnectStart(ConnectPendingState pending, int asyncCallId)
    {
        var result = Fail<bool>(CoreErrorCode.ConnectFailed, $"AsstAsyncConnect returned invalid async call id `{asyncCallId}`.");
        pending.TryComplete(result);
        return result;
    }

    private static void TryHandlePendingConnectCallback(ConnectPendingState pending, CoreCallbackEvent callback)
    {
        if (!TryParsePayload(callback.PayloadJson, out var root))
        {
            if (callback.MsgId == MsgInitFailed)
            {
                pending.TryComplete(
                    Fail<bool>(CoreErrorCode.ConnectFailed, "InitFailed callback received while connecting.", callback.PayloadJson));
            }

            return;
        }

        if (callback.MsgId == MsgAsyncCallInfo)
        {
            var asyncCallId = GetInt(root, "async_call_id");
            var what = GetString(root, "what");
            if (asyncCallId is not int callbackAsyncCallId
                || !string.Equals(what, "Connect", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (pending.AsyncCallId is int pendingAsyncCallId)
            {
                if (callbackAsyncCallId != pendingAsyncCallId)
                {
                    return;
                }
            }
            else
            {
                pending.SetAsyncCallId(callbackAsyncCallId);
            }

            var details = GetObject(root, "details");
            var ret = details is not null && GetBool(details.Value, "ret");

            pending.MarkAsyncCall(ret);
            if (ret)
            {
                pending.TryCompleteSuccessfulConnect();
            }
            else
            {
                pending.TryComplete(
                    Fail<bool>(CoreErrorCode.ConnectFailed, "AsstAsyncConnect callback reported ret=false.", callback.PayloadJson));
            }

            return;
        }

        if (callback.MsgId == MsgConnectionInfo)
        {
            var what = GetString(root, "what");
            if (string.Equals(what, "Connected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(what, "Reconnected", StringComparison.OrdinalIgnoreCase))
            {
                pending.MarkConnected();
                pending.TryCompleteSuccessfulConnect();
                return;
            }

            if (string.Equals(what, "ConnectFailed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(what, "TouchModeNotAvailable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(what, "Disconnect", StringComparison.OrdinalIgnoreCase))
            {
                pending.TryComplete(Fail<bool>(
                    CoreErrorCode.ConnectFailed,
                    BuildConnectionFailureMessage(root, what),
                    callback.PayloadJson));
            }
        }
    }

    private CoreResult<bool> EnsureReady()
    {
        if (_disposed)
        {
            return Fail<bool>(CoreErrorCode.Disposed, "Bridge is already disposed.");
        }

        if (_nativeStopAbandoned)
        {
            return Fail<bool>(
                CoreErrorCode.StopFailed,
                "Bridge native stop timed out; instance was abandoned to avoid use-after-free.");
        }

        if (_exports is null || _instance == nint.Zero)
        {
            return Fail<bool>(CoreErrorCode.NotInitialized, "Bridge is not initialized.");
        }

        return CoreResult<bool>.Ok(true);
    }

    private async Task AbortPendingConnectAsync(
        ConnectPendingState pending,
        CoreResult<bool> result,
        TimeSpan stopTimeout)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pendingConnect, pending))
            {
                _pendingConnect = null;
            }
        }

        pending.TryComplete(result);

        using var stopCts = new CancellationTokenSource(stopTimeout);
        CoreResult<bool> stopCall;
        try
        {
            stopCall = await RunNativeCallWithTimeoutAsync(
                () =>
                {
                    if (_exports is null || _instance == nint.Zero || _disposed || _nativeStopAbandoned)
                    {
                        return CoreResult<bool>.Ok(true);
                    }

                    return AsBool(_exports.AsstStop(_instance))
                        ? CoreResult<bool>.Ok(true)
                        : Fail<bool>(CoreErrorCode.StopFailed, "AsstStop returned false.");
                },
                stopTimeout,
                "AsstStop",
                CoreErrorCode.StopFailed,
                stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkNativeStopAbandoned();
            return;
        }

        if (!stopCall.Success)
        {
            if (IsAsstStopTimeout(stopCall.Error))
            {
                MarkNativeStopAbandoned();
            }

            return;
        }

        using var timeoutCts = new CancellationTokenSource(stopTimeout);
        _ = await WaitUntilNotRunningAsync(stopTimeout, timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task<CoreResult<bool>> WaitForPendingConnectReadyAsync(CancellationToken cancellationToken)
    {
        ConnectPendingState? pending;
        lock (_sync)
        {
            pending = _pendingConnect;
        }

        if (pending is null)
        {
            return CoreResult<bool>.Ok(true);
        }

        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunNativeCallAsync<T>(
        Func<T> nativeCall,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _nativeCallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(nativeCall, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _nativeCallLock.Release();
        }
    }

    private async Task<CoreResult<T>> RunNativeCallWithTimeoutAsync<T>(
        Func<CoreResult<T>> nativeCall,
        TimeSpan timeout,
        string apiName,
        CoreErrorCode timeoutCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _nativeCallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var releaseLock = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeTask = Task.Run(nativeCall, CancellationToken.None);
            Task completed;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                completed = await Task.WhenAny(nativeTask, WaitForCancellationAsync(cancellationToken)).ConfigureAwait(false);
                if (completed != nativeTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                completed = await Task.WhenAny(
                    nativeTask,
                    Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            }

            if (completed != nativeTask)
            {
                MarkNativeStopAbandoned();
                releaseLock = true;
                return CoreResult<T>.Fail(new CoreError(
                    timeoutCode,
                    $"{apiName} did not return within {timeout.TotalSeconds:N1}s; native instance was abandoned."));
            }

            return await nativeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CoreResult<T>.Fail(new CoreError(
                timeoutCode,
                $"{apiName} threw an exception.",
                Exception: ex.ToString()));
        }
        finally
        {
            if (releaseLock)
            {
                _nativeCallLock.Release();
            }
        }
    }

    private static async Task<CoreResult<bool>> InvokeAsstStopWithTimeoutAsync(
        AsstExports exports,
        nint handle,
        TimeSpan timeout)
        => await InvokeNativeStopWithTimeoutAsync(() => AsBool(exports.AsstStop(handle)), timeout)
            .ConfigureAwait(false);

    internal static async Task<CoreResult<bool>> InvokeNativeStopWithTimeoutForTestAsync(
        Func<bool> stopCall,
        TimeSpan timeout)
        => await InvokeNativeStopWithTimeoutAsync(stopCall, timeout).ConfigureAwait(false);

    internal static async Task<CoreResult<T>> InvokeNativeCallWithTimeoutForTestAsync<T>(
        Func<CoreResult<T>> nativeCall,
        TimeSpan timeout,
        string apiName,
        CoreErrorCode timeoutCode)
    {
        try
        {
            var nativeTask = Task.Run(nativeCall);
            var completed = await Task.WhenAny(nativeTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != nativeTask)
            {
                return CoreResult<T>.Fail(new CoreError(
                    timeoutCode,
                    $"{apiName} did not return within {timeout.TotalSeconds:N1}s; native instance was abandoned."));
            }

            return await nativeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CoreResult<T>.Fail(new CoreError(
                timeoutCode,
                $"{apiName} threw an exception.",
                Exception: ex.ToString()));
        }
    }

    private static async Task<CoreResult<bool>> InvokeNativeStopWithTimeoutAsync(
        Func<bool> stopCall,
        TimeSpan timeout)
    {
        try
        {
            var stopTask = Task.Run(stopCall);
            var completed = await Task.WhenAny(stopTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != stopTask)
            {
                return CoreResult<bool>.Fail(new CoreError(
                    CoreErrorCode.StopFailed,
                    $"AsstStop did not return within {timeout.TotalSeconds:N1}s; native operation was abandoned."));
            }

            var ok = await stopTask.ConfigureAwait(false);
            return ok
                ? CoreResult<bool>.Ok(true)
                : Fail<bool>(CoreErrorCode.StopFailed, "AsstStop returned false.");
        }
        catch (Exception ex)
        {
            return Fail<bool>(CoreErrorCode.StopFailed, "AsstStop threw an exception.", ex: ex);
        }
    }

    private static bool IsAsstStopTimeout(CoreError? error)
        => error?.Code == CoreErrorCode.StopFailed
           && (error.Message.Contains("AsstStop did not return", StringComparison.Ordinal)
               || error.Message.Contains("AsstStop did not return within", StringComparison.Ordinal));

    private void MarkNativeStopAbandoned()
    {
        _nativeStopAbandoned = true;
    }

    private async Task<CoreResult<bool>> WaitUntilNotRunningAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runningResult = await RunNativeCallAsync(
                () =>
                {
                    var status = EnsureReady();
                    if (!status.Success)
                    {
                        return CoreResult<bool>.Fail(status.Error!);
                    }

                    return CoreResult<bool>.Ok(AsBool(_exports!.AsstRunning(_instance)));
                },
                cancellationToken).ConfigureAwait(false);
            if (!runningResult.Success)
            {
                return CoreResult<bool>.Fail(runningResult.Error!);
            }

            if (!runningResult.Value)
            {
                return CoreResult<bool>.Ok(true);
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= deadline)
            {
                return Fail<bool>(
                    CoreErrorCode.StopFailed,
                    $"Timed out waiting for MaaCore to stop after {timeout.TotalSeconds:N0}s.");
            }

            var delay = deadline - now;
            if (delay > TimeSpan.FromMilliseconds(100))
            {
                delay = TimeSpan.FromMilliseconds(100);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitUntilNotRunningCoreAsync(
        AsstExports exports,
        nint handle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AsBool(exports.AsstRunning(handle)))
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= deadline)
            {
                return false;
            }

            var delay = deadline - now;
            if (delay > TimeSpan.FromMilliseconds(100))
            {
                delay = TimeSpan.FromMilliseconds(100);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan NormalizeTimeout(TimeSpan? requested, TimeSpan fallback)
    {
        if (requested is not { } timeout || timeout <= TimeSpan.Zero)
        {
            return fallback;
        }

        return timeout < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : timeout;
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        return tcs.Task;
    }

    private CoreResult<bool> LoadClientResource(string clientType, string baseDirectory, AsstExports exports)
    {
        var normalizedClientType = NormalizeClientType(clientType);
        if (DefaultClientTypes.Contains(normalizedClientType))
        {
            _loadedClientType = normalizedClientType;
            return CoreResult<bool>.Ok(true);
        }

        var expectedPath = Path.Combine(baseDirectory, "resource", "global", normalizedClientType, "resource");
        if (!TryResolveClientResourcePath(baseDirectory, normalizedClientType, out var resolvedClientType, out var clientResourcePath))
        {
            return Fail<bool>(CoreErrorCode.ResourceNotFound, $"Client resource directory was not found: {expectedPath}");
        }

        if (!AsBool(exports.AsstLoadResource(clientResourcePath)))
        {
            return Fail<bool>(CoreErrorCode.ResourceLoadFailed, $"AsstLoadResource failed for client resource: {clientResourcePath}");
        }

        _loadedClientType = resolvedClientType;
        return CoreResult<bool>.Ok(true);
    }

    private static string NormalizeClientType(string? clientType)
    {
        var normalized = (clientType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return ClientTypeAliasMap.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    private static bool TryResolveClientResourcePath(
        string baseDirectory,
        string normalizedClientType,
        out string resolvedClientType,
        out string clientResourcePath)
    {
        var globalRoot = Path.Combine(baseDirectory, "resource", "global");
        resolvedClientType = normalizedClientType;
        clientResourcePath = Path.Combine(globalRoot, normalizedClientType, "resource");

        if (!Directory.Exists(globalRoot))
        {
            return false;
        }

        var matchingDirectory = Directory.EnumerateDirectories(globalRoot)
            .Select(static directory => new
            {
                Directory = directory,
                ClientType = Path.GetFileName(directory),
            })
            .FirstOrDefault(candidate =>
                string.Equals(candidate.ClientType, normalizedClientType, StringComparison.OrdinalIgnoreCase));
        if (matchingDirectory is null)
        {
            return false;
        }

        var matchingResourcePath = Path.Combine(matchingDirectory.Directory, "resource");
        if (!Directory.Exists(matchingResourcePath))
        {
            return false;
        }

        resolvedClientType = matchingDirectory.ClientType;
        clientResourcePath = matchingResourcePath;
        return true;
    }

    private static CoreGpuInitializeInfo? ApplyGpuInitialization(
        CoreGpuInitializeRequest? request,
        AsstExports exports)
    {
        if (request is null || request.Mode == CoreGpuRequestMode.Default)
        {
            return null;
        }

        var warnings = new List<string>();
        var appliedMode = CoreGpuAppliedMode.Default;
        uint? appliedGpuIndex = null;

        if (exports.AsstSetStaticOption is null)
        {
            warnings.Add("AsstSetStaticOption is unavailable; continuing with MaaCore default OCR backend.");
            return new CoreGpuInitializeInfo(request.Mode, appliedMode, request.GpuIndex, appliedGpuIndex, warnings);
        }

        if (request.Mode == CoreGpuRequestMode.Cpu)
        {
            if (AsBool(exports.AsstSetStaticOption(AsstStaticOptionCpuOcr, string.Empty)))
            {
                appliedMode = CoreGpuAppliedMode.Cpu;
            }
            else
            {
                warnings.Add("Failed to apply CpuOCR static option; continuing with MaaCore default OCR backend.");
            }

            return new CoreGpuInitializeInfo(request.Mode, appliedMode, request.GpuIndex, appliedGpuIndex, warnings);
        }

        if (request.GpuIndex is uint gpuIndex
            && AsBool(exports.AsstSetStaticOption(AsstStaticOptionGpuOcr, gpuIndex.ToString())))
        {
            appliedMode = CoreGpuAppliedMode.Gpu;
            appliedGpuIndex = gpuIndex;
            return new CoreGpuInitializeInfo(request.Mode, appliedMode, request.GpuIndex, appliedGpuIndex, warnings);
        }

        warnings.Add(request.GpuIndex is null
            ? "GPU OCR requested without a valid GPU index; falling back to CPU OCR."
            : $"Failed to apply GpuOCR static option for GPU index {request.GpuIndex.Value}; falling back to CPU OCR.");

        if (AsBool(exports.AsstSetStaticOption(AsstStaticOptionCpuOcr, string.Empty)))
        {
            appliedMode = CoreGpuAppliedMode.Cpu;
        }
        else
        {
            warnings.Add("Failed to apply CpuOCR fallback static option; continuing with MaaCore default OCR backend.");
        }

        return new CoreGpuInitializeInfo(request.Mode, appliedMode, request.GpuIndex, appliedGpuIndex, warnings);
    }

    private CoreInitializeInfo BuildInitializeInfo(
        string baseDirectory,
        string libraryPath,
        string? clientType,
        CoreGpuInitializeInfo? gpuInitializeInfo)
    {
        return new CoreInitializeInfo(baseDirectory, libraryPath, _coreVersion, clientType, gpuInitializeInfo);
    }

    private static CoreResult<string> ResolveLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CoreResult<string>.Ok("MaaCore.dll");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CoreResult<string>.Ok("libMaaCore.so");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return CoreResult<string>.Ok("libMaaCore.dylib");
        }

        return Fail<string>(CoreErrorCode.PlatformNotSupported, $"Unsupported platform: {RuntimeInformation.OSDescription}");
    }

    private static bool TryLoadExports(nint library, out AsstExports exports, out string missingSymbol)
    {
        missingSymbol = string.Empty;
        TryLoadExport<AsstSetStaticOptionDelegate>(library, "AsstSetStaticOption", out var asstSetStaticOption);
        TryLoadExport<AsstSetInstanceOptionDelegate>(library, "AsstSetInstanceOption", out var asstSetInstanceOption);
        TryLoadExport<AsstSetConnectionExtrasDelegate>(library, "AsstSetConnectionExtras", out var asstSetConnectionExtras);

        if (!TryLoadExport<AsstSetUserDirDelegate>(library, "AsstSetUserDir", out var asstSetUserDir))
        {
            missingSymbol = "AsstSetUserDir";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstLoadResourceDelegate>(library, "AsstLoadResource", out var asstLoadResource))
        {
            missingSymbol = "AsstLoadResource";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstCreateExDelegate>(library, "AsstCreateEx", out var asstCreateEx))
        {
            missingSymbol = "AsstCreateEx";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstDestroyDelegate>(library, "AsstDestroy", out var asstDestroy))
        {
            missingSymbol = "AsstDestroy";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstConnectDelegate>(library, "AsstConnect", out var asstConnect))
        {
            missingSymbol = "AsstConnect";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstAsyncConnectDelegate>(library, "AsstAsyncConnect", out var asstAsyncConnect))
        {
            missingSymbol = "AsstAsyncConnect";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstAppendTaskDelegate>(library, "AsstAppendTask", out var asstAppendTask))
        {
            missingSymbol = "AsstAppendTask";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstStartDelegate>(library, "AsstStart", out var asstStart))
        {
            missingSymbol = "AsstStart";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstBackToHomeDelegate>(library, "AsstBackToHome", out var asstBackToHome))
        {
            missingSymbol = "AsstBackToHome";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstStopDelegate>(library, "AsstStop", out var asstStop))
        {
            missingSymbol = "AsstStop";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstRunningDelegate>(library, "AsstRunning", out var asstRunning))
        {
            missingSymbol = "AsstRunning";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstConnectedDelegate>(library, "AsstConnected", out var asstConnected))
        {
            missingSymbol = "AsstConnected";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstGetImageDelegate>(library, "AsstGetImage", out var asstGetImage))
        {
            missingSymbol = "AsstGetImage";
            exports = null!;
            return false;
        }

        _ = TryLoadExport<AsstAsyncScreencapDelegate>(library, "AsstAsyncScreencap", out var asstAsyncScreencap);
        _ = TryLoadExport<AsstGetImageBgrDelegate>(library, "AsstGetImageBgr", out var asstGetImageBgr);

        if (!TryLoadExport<AsstGetNullSizeDelegate>(library, "AsstGetNullSize", out var asstGetNullSize))
        {
            missingSymbol = "AsstGetNullSize";
            exports = null!;
            return false;
        }

        if (!TryLoadExport<AsstGetVersionDelegate>(library, "AsstGetVersion", out var asstGetVersion))
        {
            missingSymbol = "AsstGetVersion";
            exports = null!;
            return false;
        }

        exports = new AsstExports(
            asstSetStaticOption,
            asstSetInstanceOption,
            asstSetConnectionExtras,
            asstSetUserDir!,
            asstLoadResource!,
            asstCreateEx!,
            asstDestroy!,
            asstConnect!,
            asstAsyncConnect!,
            asstAppendTask!,
            asstStart!,
            asstBackToHome!,
            asstStop!,
            asstRunning!,
            asstConnected!,
            asstAsyncScreencap,
            asstGetImage!,
            asstGetImageBgr,
            asstGetNullSize!,
            asstGetVersion!);

        return true;
    }

    private static bool TryLoadExport<T>(nint library, string symbol, out T? function)
        where T : Delegate
    {
        function = null;
        if (!NativeLibrary.TryGetExport(library, symbol, out var export))
        {
            return false;
        }

        function = Marshal.GetDelegateForFunctionPointer<T>(export);
        return true;
    }

    private static bool AsBool(byte value) => value != 0;

    private static CoreResult<T> Fail<T>(
        CoreErrorCode code,
        string message,
        string? nativeDetails = null,
        Exception? ex = null)
    {
        return CoreResult<T>.Fail(new CoreError(code, message, nativeDetails, ex?.ToString()));
    }

    private static string ResolveMessageName(int msgId)
    {
        return msgId switch
        {
            0 => "InternalError",
            1 => "InitFailed",
            2 => "ConnectionInfo",
            3 => "AllTasksCompleted",
            4 => "AsyncCallInfo",
            5 => "Destroyed",
            10000 => "TaskChainError",
            10001 => "TaskChainStart",
            10002 => "TaskChainCompleted",
            10003 => "TaskChainExtraInfo",
            10004 => "TaskChainStopped",
            20000 => "SubTaskError",
            20001 => "SubTaskStart",
            20002 => "SubTaskCompleted",
            20003 => "SubTaskExtraInfo",
            20004 => "SubTaskStopped",
            30000 => "ReportRequest",
            _ => $"Unknown({msgId})",
        };
    }

    private static bool TryParsePayload(string payload, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => false,
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static JsonElement? GetObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.Object ? value : null;
    }

    private static string BuildConnectionFailureMessage(JsonElement root, string? what)
    {
        var details = GetObject(root, "details");
        var rawOutput = details is JsonElement detailObject
            ? NormalizeFailureOutput(GetString(detailObject, "raw_output"))
            : null;

        if (!string.IsNullOrWhiteSpace(rawOutput))
        {
            return rawOutput;
        }

        var reason = GetString(root, "why");
        if (!string.IsNullOrWhiteSpace(reason))
        {
            return reason.Trim();
        }

        if (string.Equals(what, "TouchModeNotAvailable", StringComparison.OrdinalIgnoreCase))
        {
            return "Touch mode is not available. Switch to a different touch mode in Settings > Connect.";
        }

        return string.IsNullOrWhiteSpace(what) ? "ConnectionInfo" : what.Trim();
    }

    private static string? NormalizeFailureOutput(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return null;
        }

        return rawOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\n');
    }

    private sealed class ConnectPendingState
    {
        private int _completed;

        public ConnectPendingState(int? asyncCallId)
        {
            AsyncCallId = asyncCallId;
            Completion = new TaskCompletionSource<CoreResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public int? AsyncCallId { get; private set; }

        public bool AsyncCallReceived { get; private set; }

        public bool AsyncCallSucceeded { get; private set; }

        public bool ConnectionEstablished { get; private set; }

        public TaskCompletionSource<CoreResult<bool>> Completion { get; }

        public void SetAsyncCallId(int asyncCallId)
        {
            AsyncCallId ??= asyncCallId;
        }

        public void MarkAsyncCall(bool succeeded)
        {
            AsyncCallReceived = true;
            AsyncCallSucceeded = succeeded;
        }

        public void MarkConnected()
        {
            ConnectionEstablished = true;
        }

        public void TryCompleteSuccessfulConnect()
        {
            if (ConnectionEstablished && AsyncCallReceived && AsyncCallSucceeded)
            {
                TryComplete(CoreResult<bool>.Ok(true));
            }
        }

        public void TryComplete(CoreResult<bool> result)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                return;
            }

            Completion.TrySetResult(result);
        }

        public bool TryGetCompletedResult(out CoreResult<bool> result)
        {
            if (!Completion.Task.IsCompletedSuccessfully)
            {
                result = null!;
                return false;
            }

            result = Completion.Task.Result;
            return true;
        }
    }

    private sealed record NativeInitializeResult(
        nint Instance,
        CoreGpuInitializeInfo? GpuInitializeInfo,
        string Version);

    private sealed record AsstExports(
        AsstSetStaticOptionDelegate? AsstSetStaticOption,
        AsstSetInstanceOptionDelegate? AsstSetInstanceOption,
        AsstSetConnectionExtrasDelegate? AsstSetConnectionExtras,
        AsstSetUserDirDelegate AsstSetUserDir,
        AsstLoadResourceDelegate AsstLoadResource,
        AsstCreateExDelegate AsstCreateEx,
        AsstDestroyDelegate AsstDestroy,
        AsstConnectDelegate AsstConnect,
        AsstAsyncConnectDelegate AsstAsyncConnect,
        AsstAppendTaskDelegate AsstAppendTask,
        AsstStartDelegate AsstStart,
        AsstBackToHomeDelegate AsstBackToHome,
        AsstStopDelegate AsstStop,
        AsstRunningDelegate AsstRunning,
        AsstConnectedDelegate AsstConnected,
        AsstAsyncScreencapDelegate? AsstAsyncScreencap,
        AsstGetImageDelegate AsstGetImage,
        AsstGetImageBgrDelegate? AsstGetImageBgr,
        AsstGetNullSizeDelegate AsstGetNullSize,
        AsstGetVersionDelegate AsstGetVersion);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstSetStaticOptionDelegate(int key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstSetInstanceOptionDelegate(
        nint handle,
        int key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void AsstSetConnectionExtrasDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extras);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstSetUserDirDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstLoadResourceDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint AsstCreateExDelegate(AsstApiCallbackDelegate callback, nint customArg);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void AsstDestroyDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstConnectDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string adbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string config);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AsstAsyncConnectDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string adbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string config,
        byte block);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AsstAppendTaskDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string @params);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstStartDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstBackToHomeDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstStopDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstRunningDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte AsstConnectedDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AsstAsyncScreencapDelegate(nint handle, byte block);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong AsstGetImageDelegate(nint handle, nint buffer, ulong bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong AsstGetImageBgrDelegate(nint handle, nint buffer, ulong bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong AsstGetNullSizeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint AsstGetVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void AsstApiCallbackDelegate(int msg, nint detailsJson, nint customArg);
}
