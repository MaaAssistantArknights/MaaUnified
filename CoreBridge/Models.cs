namespace MAAUnified.CoreBridge;

public enum CoreErrorCode
{
    LibraryNotFound = 1,
    LibraryLoadFailed = 2,
    SymbolMissing = 3,
    ResourceNotFound = 4,
    ResourceLoadFailed = 5,
    CoreInstanceCreateFailed = 6,
    NotInitialized = 7,
    Disposed = 8,
    InvalidRequest = 9,
    ConnectFailed = 10,
    ConnectTimeout = 11,
    AppendTaskFailed = 12,
    StartFailed = 13,
    StopFailed = 14,
    GetImageFailed = 15,
    PlatformNotSupported = 16,
    NotSupported = 17,
    NotImplemented = 18,
}

public sealed record CoreError(
    CoreErrorCode Code,
    string Message,
    string? NativeDetails = null,
    string? Exception = null);

public sealed record CoreResult<T>(bool Success, T? Value, CoreError? Error)
{
    public static CoreResult<T> Ok(T value) => new(true, value, null);

    public static CoreResult<T> Fail(CoreError error) => new(false, default, error);
}

public enum CoreGpuRequestMode
{
    Default = 0,
    Cpu = 1,
    Gpu = 2,
}

public enum CoreGpuAppliedMode
{
    Default = 0,
    Cpu = 1,
    Gpu = 2,
}

public sealed record CoreGpuInitializeRequest(
    CoreGpuRequestMode Mode = CoreGpuRequestMode.Default,
    uint? GpuIndex = null,
    string? AdapterName = null);

public sealed record CoreGpuInitializeInfo(
    CoreGpuRequestMode RequestedMode,
    CoreGpuAppliedMode AppliedMode,
    uint? RequestedGpuIndex = null,
    uint? AppliedGpuIndex = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record CoreInitializeRequest(
    string BaseDirectory,
    string? ClientType = null,
    CoreGpuInitializeRequest? Gpu = null);

public sealed record CoreInitializeInfo(
    string BaseDirectory,
    string LibraryPath,
    string CoreVersion,
    string? ClientType,
    CoreGpuInitializeInfo? Gpu = null);

public sealed record CoreInstanceOptions(
    string? TouchMode = null,
    bool? DeploymentWithPause = null,
    bool? AdbLiteEnabled = null,
    bool? KillAdbOnExit = null,
    string? ClientType = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(TouchMode)
        && DeploymentWithPause is null
        && AdbLiteEnabled is null
        && KillAdbOnExit is null
        && ClientType is null;

    public CoreInstanceOptions MergeWith(CoreInstanceOptions fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        return new CoreInstanceOptions(
            string.IsNullOrWhiteSpace(TouchMode) ? fallback.TouchMode : TouchMode,
            DeploymentWithPause ?? fallback.DeploymentWithPause,
            AdbLiteEnabled ?? fallback.AdbLiteEnabled,
            KillAdbOnExit ?? fallback.KillAdbOnExit,
            ClientType ?? fallback.ClientType);
    }
}

public sealed record CoreConnectionExtras(
    bool MacUseBundledAdb = false,
    string? TouchMode = null,
    bool AdbLiteEnabled = false,
    bool KillAdbOnExit = false,
    bool MuMu12ExtrasEnabled = false,
    string? MuMu12EmulatorPath = null,
    bool MuMuBridgeConnection = false,
    string? MuMu12Index = null,
    bool LdPlayerExtrasEnabled = false,
    string? LdPlayerEmulatorPath = null,
    bool LdPlayerManualSetIndex = false,
    string? LdPlayerIndex = null,
    string? AttachWindowScreencapMethod = null,
    string? AttachWindowMouseMethod = null,
    string? AttachWindowKeyboardMethod = null,
    string? ClientType = null,
    string? FallbackStrategy = null,
    string? ConfiguredTouchMode = null,
    bool? ConfiguredAdbLiteEnabled = null,
    string? FallbackReason = null,
    string? FallbackRequiredLibrary = null,
    bool? FallbackRequiredLibraryExists = null)
{
    public static CoreConnectionExtras Empty { get; } = new();
}

public sealed record CoreConnectionInfo(
    string Address,
    string ConnectConfig,
    string? AdbPath,
    CoreConnectionExtras? Extras = null,
    TimeSpan? Timeout = null);

public sealed record CoreTaskRequest(string Type, string Name, bool IsEnabled, string ParamsJson);

public sealed record CoreRuntimeStatus(bool Initialized, bool Connected, bool Running);

public sealed record CoreAttachWindowRequest(
    nint WindowHandle,
    ulong ScreencapMethod,
    ulong MouseMethod,
    ulong KeyboardMethod);

public sealed record CoreCallbackEvent(
    int MsgId,
    string MsgName,
    string PayloadJson,
    DateTimeOffset Timestamp);
