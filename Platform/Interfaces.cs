using System.Diagnostics.CodeAnalysis;

namespace MAAUnified.Platform;

public sealed record PlatformOperationResult(
    bool Success,
    string Message,
    string? ErrorCode = null,
    bool UsedFallback = false,
    string Provider = "unknown",
    string? OperationId = null,
    PlatformExecutionMode ExecutionMode = PlatformExecutionMode.Native);

public sealed record PlatformOperationResult<T>(
    bool Success,
    T? Value,
    string Message,
    string? ErrorCode = null,
    bool UsedFallback = false,
    string Provider = "unknown",
    string? OperationId = null,
    PlatformExecutionMode ExecutionMode = PlatformExecutionMode.Native);

public static class PlatformOperation
{
    public static PlatformOperationResult NativeSuccess(string provider, string message, string? operationId = null)
        => new(true, message, null, false, provider, operationId, PlatformExecutionMode.Native);

    public static PlatformOperationResult<T> NativeSuccess<T>(string provider, T value, string message, string? operationId = null)
        => new(true, value, message, null, false, provider, operationId, PlatformExecutionMode.Native);

    public static PlatformOperationResult FallbackSuccess(
        string provider,
        string message,
        string? operationId = null,
        string? errorCode = null)
        => new(true, message, errorCode, true, provider, operationId, PlatformExecutionMode.Fallback);

    public static PlatformOperationResult<T> FallbackSuccess<T>(
        string provider,
        T value,
        string message,
        string? operationId = null,
        string? errorCode = null)
        => new(true, value, message, errorCode, true, provider, operationId, PlatformExecutionMode.Fallback);

    public static PlatformOperationResult Failed(
        string provider,
        string message,
        string errorCode,
        string? operationId = null,
        bool usedFallback = false)
        => new(false, message, errorCode, usedFallback, provider, operationId, PlatformExecutionMode.Failed);

    public static PlatformOperationResult<T> Failed<T>(
        string provider,
        string message,
        string errorCode,
        string? operationId = null,
        bool usedFallback = false,
        T? value = default)
        => new(false, value, message, errorCode, usedFallback, provider, operationId, PlatformExecutionMode.Failed);
}

public sealed record TrayMenuState(
    bool StartEnabled,
    bool StopEnabled,
    bool OverlayEnabled,
    bool ForceShowEnabled,
    bool HideTrayEnabled,
    bool HideTrayVisible = true);

public sealed record TrayLanguageMenuItem(string Language, string Text);

public sealed record TrayMenuText(
    string Start,
    string Stop,
    string ForceShow,
    string HideTray,
    string ToggleOverlay,
    string SwitchLanguage,
    string Restart,
    string Exit,
    IReadOnlyList<TrayLanguageMenuItem>? LanguageItems = null)
{
    public static TrayMenuText Default { get; } = new(
        Start: "Start",
        Stop: "Stop",
        ForceShow: "Force Show",
        HideTray: "Hide Tray",
        ToggleOverlay: "Toggle Overlay",
        SwitchLanguage: "Switch Language",
        Restart: "Restart",
        Exit: "Exit");
}

public enum TrayCommandId
{
    Start = 0,
    Stop = 1,
    ForceShow = 2,
    HideTray = 3,
    ToggleOverlay = 4,
    Exit = 5,
    // Reserved for compatibility; tray cycle-language entry is currently disabled.
    SwitchLanguage = 6,
    Restart = 7,
}

public sealed record TrayCommandEvent(
    TrayCommandId Command,
    string Source,
    DateTimeOffset Timestamp,
    string? TargetLanguage = null);

/// <summary>
/// Raised when the user activates the tray icon itself (left click / double click),
/// matching the WPF NotifyIcon behavior of showing the main window.
/// </summary>
public sealed record TrayActivationEvent(
    string Source,
    DateTimeOffset Timestamp);

public sealed record TrayMenuRequestEvent(
    int ScreenX,
    int ScreenY,
    string Source,
    DateTimeOffset Timestamp,
    int? AnchorLeft = null,
    int? AnchorTop = null,
    int? AnchorRight = null,
    int? AnchorBottom = null);

public sealed record SystemNotificationAction(string Label, string Tag)
{
    private static readonly string OpenUrlPrefix = $"OpenUrl:{Guid.NewGuid():N}:";

    public static SystemNotificationAction ForOpenUrl(string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("URL must start with http:// or https://", nameof(url));
        }

        return new SystemNotificationAction(label, OpenUrlPrefix + uri.AbsoluteUri);
    }

    public static bool TryGetOpenUrl(string argument, [NotNullWhen(true)] out string? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(argument)
            || !argument.StartsWith(OpenUrlPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = argument[OpenUrlPrefix.Length..];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }
}

public sealed record SystemNotificationRequest(
    string Title,
    string Message,
    IReadOnlyList<SystemNotificationAction> Actions,
    bool UseSystemNotification = true,
    InAppNotificationSeverity InAppSeverity = InAppNotificationSeverity.Information)
{
    public SystemNotificationRequest(string title, string message)
        : this(title, message, [], true, InAppNotificationSeverity.Information)
    {
    }
}

public enum InAppNotificationSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public enum NotificationAvailabilityReason
{
    None = 0,
    DisabledForApplication = 1,
    DisabledForUser = 2,
    DisabledByGroupPolicy = 3,
    DisabledByManifest = 4,
    BackendUnavailable = 5,
    Unknown = 6,
}

public sealed record NotificationAvailabilityStatus(
    bool IsAvailable,
    string Detail,
    NotificationAvailabilityReason Reason = NotificationAvailabilityReason.None);

public sealed class NotificationActionActivatedEventArgs(string argument) : EventArgs
{
    public string Argument { get; } = System.Net.WebUtility.UrlDecode(argument ?? string.Empty);
}

public sealed class InAppNotificationRequestedEventArgs(SystemNotificationRequest notification, string reason) : EventArgs
{
    public SystemNotificationRequest Notification { get; } = notification;

    public string Reason { get; } = reason;
}

public sealed record GlobalHotkeyTriggeredEvent(
    string Name,
    string Gesture,
    DateTimeOffset Timestamp);

public sealed record HotkeyBindingRequest(
    string Name,
    string Gesture);

public sealed record HotkeyRegistrationOutcome(
    string Name,
    string Gesture,
    PlatformOperationResult Result,
    string? EffectiveGestureDisplay = null);

public sealed record RegisteredHotkeyState(
    string Name,
    string Gesture,
    string DisplayGesture,
    string Provider,
    PlatformExecutionMode ExecutionMode);

public sealed record HotkeyHostContext(
    nint WindowHandle,
    string ParentWindowIdentifier,
    string SessionType);

public enum OverlayRuntimeMode
{
    Hidden = 0,
    Preview = 1,
    Native = 2,
}

public sealed record OverlayStateChangedEvent(
    OverlayRuntimeMode Mode,
    bool Visible,
    string TargetId,
    string Action,
    string Message,
    DateTimeOffset Timestamp,
    string Provider = "unknown",
    bool UsedFallback = false,
    string? ErrorCode = null);

public sealed record OverlayTarget(
    string Id,
    string DisplayName,
    bool IsPrimary,
    long? NativeHandle = null,
    int? ProcessId = null,
    string? ProcessName = null,
    string? WindowTitle = null);

/// <summary>
/// Windows taskbar progress projection (WPF parity: RootViewModel.TaskProgress ->
/// TaskbarManager.SetProgressValue). Only supported on Windows; other platforms no-op.
/// </summary>
public interface ITaskbarProgressService
{
    PlatformCapabilityStatus Capability { get; }

    Task<PlatformOperationResult> SetProgressAsync(nint windowHandle, int current, int maximum, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> ClearProgressAsync(nint windowHandle, CancellationToken cancellationToken = default);
}

public interface ITrayService
{
    PlatformCapabilityStatus Capability { get; }

    event EventHandler<TrayCommandEvent>? CommandInvoked;

    event EventHandler<TrayMenuRequestEvent>? MenuRequested;

    event EventHandler<TrayActivationEvent>? ActivationRequested;

    Task<PlatformOperationResult> InitializeAsync(string appTitle, TrayMenuText? menuText, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> InitializeAsync(string appTitle, CancellationToken cancellationToken = default)
        => InitializeAsync(appTitle, null, cancellationToken);

    Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    PlatformCapabilityStatus Capability { get; }

    NotificationAvailabilityStatus GetAvailability()
        => new(Capability.Supported, Capability.Message);

    Task<PlatformOperationResult> NotifyAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> NotifyAsync(SystemNotificationRequest notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return NotifyAsync(notification.Title, notification.Message, cancellationToken);
    }
}

public interface INotificationInteractionSource
{
    event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated;

    event EventHandler<InAppNotificationRequestedEventArgs>? InAppNotificationRequested;
}

public interface IGlobalHotkeyService
{
    PlatformCapabilityStatus Capability { get; }

    event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<HotkeyRegistrationOutcome>> RegisterBatchAsync(
        IReadOnlyList<HotkeyBindingRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new List<HotkeyRegistrationOutcome>(requests.Count);
        foreach (var request in requests)
        {
            PlatformOperationResult operation;
            string? displayGesture;
            if (string.IsNullOrWhiteSpace(request.Gesture))
            {
                operation = await UnregisterAsync(request.Name, cancellationToken);
                if (!operation.Success && string.Equals(operation.ErrorCode, PlatformErrorCodes.HotkeyNotFound, StringComparison.Ordinal))
                {
                    operation = operation.UsedFallback
                        ? PlatformOperation.FallbackSuccess(
                            Capability.Provider,
                            $"Hotkey `{request.Name}` was already unregistered.",
                            "hotkey.unregister",
                            PlatformErrorCodes.HotkeyFallback)
                        : PlatformOperation.NativeSuccess(
                            Capability.Provider,
                            $"Hotkey `{request.Name}` was already unregistered.",
                            "hotkey.unregister");
                }
                displayGesture = string.Empty;
            }
            else
            {
                operation = await RegisterAsync(request.Name, request.Gesture, cancellationToken);
                displayGesture = TryGetRegisteredHotkey(request.Name, out var registeredState)
                    ? registeredState.DisplayGesture
                    : null;
            }

            results.Add(new HotkeyRegistrationOutcome(request.Name, request.Gesture, operation, displayGesture));
        }

        return results;
    }

    Task<PlatformOperationResult> ConfigureHostContextAsync(
        HotkeyHostContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            "Hotkey host context ignored by current provider.",
            "hotkey.configure-host"));

    bool TryDispatchWindowScopedHotkey(HotkeyGesture gesture) => false;

    bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        state = default!;
        return false;
    }
}

public interface IAutostartService
{
    PlatformCapabilityStatus Capability { get; }

    Task<PlatformOperationResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IFileDialogService
{
    PlatformCapabilityStatus Capability { get; }
}

public interface IOverlayCapabilityService
{
    PlatformCapabilityStatus Capability { get; }

    event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

    Task<PlatformOperationResult> BindHostWindowAsync(nint hostWindowHandle, bool clickThrough, double opacity, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional exact-title window finder used by PC client window binding (AttachWindow).
/// Mirrors WPF AsstProxy.FindWindowsByName (freeze 23ddd27): enumerates visible top-level
/// windows, compares raw (untrimmed) titles with an exact ordinal match, has no result cap
/// and no own-process exclusion, and returns every match in enumeration (Z) order.
/// </summary>
public interface IAttachWindowTargetFinder
{
    IReadOnlyList<nint> FindVisibleWindowsByExactTitle(string exactTitle);
}

/// <summary>
/// Identity of a single post-completion action. This is not a flags mask: each member is
/// dispatched separately and diagnostics print the member name. The <c>LockScreen</c> and
/// <c>ExitPCClient</c> values intentionally mirror the WPF <c>PostActions</c> bit positions
/// (1&lt;&lt;9 / 1&lt;&lt;10, d58cb0825b / 3b4f06dd86) so the persisted legacy bitmask and the
/// runtime action identity can be cross-checked; earlier members keep their original values.
/// </summary>
public enum PostActionType
{
    ExitArknights = 0,
    BackToAndroidHome = 1,
    ExitEmulator = 2,
    ExitSelf = 3,
    Hibernate = 4,
    Shutdown = 5,
    Sleep = 6,

    /// <summary>Lock the workstation (WPF <c>PostActions.LockScreen</c>).</summary>
    LockScreen = 1 << 9,

    /// <summary>Close the PC client (WPF <c>PostActions.ExitPCClient</c>, PC/window-binding connection only).</summary>
    ExitPCClient = 1 << 10,
}

public sealed record PostActionExecutorRequest(
    string? CommandLine = null,
    string? ConnectAddress = null,
    string? ConnectConfig = null,
    string? AdbPath = null,
    string? ClientType = null,
    bool MuMu12ExtrasEnabled = false,
    string? MuMu12EmulatorPath = null,
    bool MuMuBridgeConnection = false,
    string? MuMu12Index = null,
    bool LdPlayerExtrasEnabled = false,
    string? LdPlayerEmulatorPath = null,
    bool LdPlayerManualSetIndex = false,
    string? LdPlayerIndex = null);

public sealed record PostActionCapabilityMatrix(
    PlatformCapabilityStatus ExitArknights,
    PlatformCapabilityStatus BackToAndroidHome,
    PlatformCapabilityStatus ExitEmulator,
    PlatformCapabilityStatus ExitSelf,
    PlatformCapabilityStatus Hibernate,
    PlatformCapabilityStatus Shutdown,
    PlatformCapabilityStatus Sleep,
    PlatformCapabilityStatus? LockScreen = null,
    PlatformCapabilityStatus? ExitPCClient = null)
{
    public PlatformCapabilityStatus Get(PostActionType action)
    {
        return action switch
        {
            PostActionType.ExitArknights => ExitArknights,
            PostActionType.BackToAndroidHome => BackToAndroidHome,
            PostActionType.ExitEmulator => ExitEmulator,
            PostActionType.ExitSelf => ExitSelf,
            PostActionType.Hibernate => Hibernate,
            PostActionType.Shutdown => Shutdown,
            PostActionType.Sleep => Sleep,
            // Platforms that predate the P1-04 additions pass null; degrade explicitly instead
            // of silently reporting a supported/unknown action.
            PostActionType.LockScreen => LockScreen ?? new PlatformCapabilityStatus(
                false,
                "Lock screen is unsupported on current platform.",
                Provider: "unknown"),
            PostActionType.ExitPCClient => ExitPCClient ?? new PlatformCapabilityStatus(
                false,
                "Exit PC client is unsupported on current platform.",
                Provider: "unknown"),
            _ => new PlatformCapabilityStatus(false, "Unknown action.", Provider: "unknown"),
        };
    }
}

public interface IPostActionExecutorService
{
    PostActionCapabilityMatrix CapabilityMatrix { get; }

    PostActionCapabilityMatrix GetCapabilityMatrix(PostActionExecutorRequest? request = null)
        => CapabilityMatrix;

    Task<PlatformOperationResult> ExecuteAsync(
        PostActionType action,
        PostActionExecutorRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> ExecuteUserScriptAsync(
        string scriptPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Keep-awake lease provider used by the post-action countdown. WPF parity:
/// <c>SleepManagement.CreateKeepAwakeRequest</c> (0b048d5e94) — the post-action countdown runs
/// after the task queue already returned to idle, so an unattended machine may fall asleep and
/// freeze the countdown unless an independently owned power request is held.
/// </summary>
public interface IKeepAwakeService
{
    PlatformCapabilityStatus Capability { get; }

    /// <summary>
    /// Creates an independently owned keep-awake lease. Disposing the lease clears only this
    /// request (it never clears a keep-awake window owned by a running task), and disposing it
    /// twice is a no-op.
    /// </summary>
    /// <param name="reason">Human readable reason surfaced by the OS power request.</param>
    /// <returns>The lease, or <see langword="null"/> when the current platform cannot keep awake.</returns>
    IDisposable? CreateKeepAwakeRequest(string reason);
}
