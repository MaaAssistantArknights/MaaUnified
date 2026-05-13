using MAAUnified.Application.Services.Localization;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Infrastructure;

public static class PlatformCapabilityTextMap
{
    private const string Scope = "PlatformCapabilityText";

    public static string FormatCapabilityLine(
        string language,
        string name,
        PlatformCapabilityStatus status,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        var localizer = UiLocalizer.Create(language);
        var mode = status.Supported
            ? localizer.GetOrDefault("PlatformCapability.Status.Supported", "Supported", Scope, fallbackReporter)
            : localizer.GetOrDefault("PlatformCapability.Status.Fallback", "Fallback", Scope, fallbackReporter);
        var fallback = status.HasFallback && !string.IsNullOrWhiteSpace(status.FallbackMode)
            ? string.Format(
                localizer.GetOrDefault("PlatformCapability.Capability.FallbackSuffix", ", fallback={0}", Scope, fallbackReporter),
                status.FallbackMode)
            : string.Empty;
        return string.Format(
            localizer.GetOrDefault("PlatformCapability.Capability.Line", "{0}: {1} (provider={2}{3})", Scope, fallbackReporter),
            name,
            mode,
            status.Provider,
            fallback);
    }

    public static string FormatCapabilityLine(
        string language,
        PlatformCapabilityId capability,
        PlatformCapabilityStatus status,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        return FormatCapabilityLine(
            language,
            GetCapabilityName(language, capability, fallbackReporter),
            status,
            fallbackReporter);
    }

    public static string FormatSnapshotUnavailable(
        string language,
        string message,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        return string.Format(
            UiLocalizer.Create(language).GetOrDefault(
                "PlatformCapability.Snapshot.Unavailable",
                "Capability snapshot unavailable: {0}",
                Scope,
                fallbackReporter),
            message);
    }

    public static string FormatAutostartStatus(
        string language,
        bool enabled,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        var localizer = UiLocalizer.Create(language);
        return enabled
            ? localizer.GetOrDefault("PlatformCapability.Autostart.Enabled", "Autostart: enabled", Scope, fallbackReporter)
            : localizer.GetOrDefault("PlatformCapability.Autostart.Disabled", "Autostart: disabled", Scope, fallbackReporter);
    }

    public static string FormatErrorCode(
        string language,
        string? errorCode,
        string fallbackMessage,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return fallbackMessage;
        }

        var localizer = UiLocalizer.Create(language);
        var (key, defaultMessage) = GetErrorLookup(errorCode);
        LocalizationFallbackInfo? fallbackInfo = null;
        var resolved = localizer.GetText(
            key,
            Scope,
            info => fallbackInfo = info);

        if (fallbackInfo is { } info)
        {
            fallbackReporter?.Invoke(info);
        }

        return string.Equals(resolved, key, StringComparison.Ordinal)
            ? defaultMessage ?? fallbackMessage
            : resolved;
    }

    public static string GetCapabilityName(
        string language,
        PlatformCapabilityId capability,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        var key = capability switch
        {
            PlatformCapabilityId.Tray => "PlatformCapability.CapabilityName.Tray",
            PlatformCapabilityId.Notification => "PlatformCapability.CapabilityName.Notification",
            PlatformCapabilityId.Hotkey => "PlatformCapability.CapabilityName.Hotkey",
            PlatformCapabilityId.Autostart => "PlatformCapability.CapabilityName.Autostart",
            PlatformCapabilityId.Overlay => "PlatformCapability.CapabilityName.Overlay",
            _ => "PlatformCapability.CapabilityName.Unknown",
        };
        return UiLocalizer.Create(language).GetOrDefault(key, capability.ToString(), Scope, fallbackReporter);
    }

    public static TrayMenuText CreateTrayMenuText(
        string language,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        var localizer = UiLocalizer.Create(language);
        return new TrayMenuText(
            Start: localizer.GetOrDefault("PlatformCapability.TrayMenu.Start", "Start", Scope, fallbackReporter),
            Stop: localizer.GetOrDefault("PlatformCapability.TrayMenu.Stop", "Stop", Scope, fallbackReporter),
            ForceShow: localizer.GetOrDefault("PlatformCapability.TrayMenu.ForceShow", "Show Main Window", Scope, fallbackReporter),
            HideTray: localizer.GetOrDefault("PlatformCapability.TrayMenu.HideTray", "Hide Tray", Scope, fallbackReporter),
            ToggleOverlay: localizer.GetOrDefault("PlatformCapability.TrayMenu.ToggleOverlay", "Toggle Overlay", Scope, fallbackReporter),
            SwitchLanguage: localizer.GetOrDefault("PlatformCapability.TrayMenu.SwitchLanguage", "Cycle Language", Scope, fallbackReporter),
            Restart: localizer.GetOrDefault("PlatformCapability.TrayMenu.Restart", "Restart", Scope, fallbackReporter),
            Exit: localizer.GetOrDefault("PlatformCapability.TrayMenu.Exit", "Exit", Scope, fallbackReporter));
    }

    public static string GetUiText(
        string language,
        string key,
        string fallback,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        return UiLocalizer.Create(language).GetOrDefault(
            $"PlatformCapability.{key}",
            fallback,
            Scope,
            WrapFallbackReporter(key, fallbackReporter));
    }

    private static Action<LocalizationFallbackInfo>? WrapFallbackReporter(
        string key,
        Action<LocalizationFallbackInfo>? fallbackReporter)
    {
        if (fallbackReporter is null)
        {
            return null;
        }

        return info => fallbackReporter(
            new LocalizationFallbackInfo(
                info.Scope,
                info.Language,
                key,
                info.FallbackSource));
    }

    private static (string Key, string? DefaultMessage) GetErrorLookup(string errorCode)
    {
        return errorCode switch
        {
            PlatformErrorCodes.TrayUnsupported => ("PlatformCapability.Error.TrayUnsupported", "Tray is unsupported in the current environment"),
            PlatformErrorCodes.TrayFallback => ("PlatformCapability.Error.TrayFallback", "Tray downgraded to the window menu"),
            PlatformErrorCodes.TrayInitFailed => ("PlatformCapability.Error.TrayInitFailed", "Tray initialization failed"),
            PlatformErrorCodes.TrayMenuDispatchFailed => ("PlatformCapability.Error.TrayMenuDispatchFailed", "Tray menu dispatch failed"),
            PlatformErrorCodes.TrayNotInitialized => ("PlatformCapability.Error.TrayNotInitialized", "Tray is not initialized"),

            PlatformErrorCodes.NotificationUnsupported => ("PlatformCapability.Error.NotificationUnsupported", "Notifications are unsupported in the current environment"),
            PlatformErrorCodes.NotificationFallback => ("PlatformCapability.Error.NotificationFallback", "Notifications are using a fallback provider"),
            PlatformErrorCodes.NotificationSendFailed => ("PlatformCapability.Error.NotificationSendFailed", "Notification dispatch failed"),

            PlatformErrorCodes.HotkeyUnsupported => ("PlatformCapability.Error.HotkeyUnsupported", "Global hotkeys are unsupported in the current environment"),
            PlatformErrorCodes.HotkeyFallback => ("PlatformCapability.Error.HotkeyFallback", "Global hotkeys downgraded to window-scoped shortcuts"),
            PlatformErrorCodes.HotkeyNameMissing => ("PlatformCapability.Error.HotkeyNameMissing", "Hotkey name is required"),
            PlatformErrorCodes.HotkeyInvalidGesture => ("PlatformCapability.Error.HotkeyInvalidGesture", "Hotkey gesture is invalid"),
            PlatformErrorCodes.HotkeyConflict => ("PlatformCapability.Error.HotkeyConflict", "Hotkey gesture conflicts with an existing registration"),
            PlatformErrorCodes.HotkeyNotFound => ("PlatformCapability.Error.HotkeyNotFound", "Hotkey registration was not found"),
            PlatformErrorCodes.HotkeyPermissionDenied => ("PlatformCapability.Error.HotkeyPermissionDenied", "Hotkey permission was denied"),
            PlatformErrorCodes.HotkeyNativeRegistrationFailed => ("PlatformCapability.Error.HotkeyNativeRegistrationFailed", "Failed to register the native global hotkey"),
            PlatformErrorCodes.HotkeyHookStartFailed => ("PlatformCapability.Error.HotkeyHookStartFailed", "Failed to start the global hotkey hook"),
            PlatformErrorCodes.HotkeyTriggerDispatchFailed => ("PlatformCapability.Error.HotkeyTriggerDispatchFailed", "Failed to dispatch the hotkey trigger"),
            PlatformErrorCodes.HotkeyPortalUnavailable => ("PlatformCapability.Error.HotkeyPortalUnavailable", "Desktop portal hotkey support is unavailable"),
            PlatformErrorCodes.HotkeyPortalUnsupported => ("PlatformCapability.Error.HotkeyPortalUnsupported", "Desktop portal hotkeys are unsupported in the current environment"),
            PlatformErrorCodes.HotkeyPortalCancelled => ("PlatformCapability.Error.HotkeyPortalCancelled", "Desktop portal hotkey registration was cancelled"),

            PlatformErrorCodes.AutostartUnsupported => ("PlatformCapability.Error.AutostartUnsupported", "Autostart is unsupported in the current environment"),
            PlatformErrorCodes.AutostartQueryFailed => ("PlatformCapability.Error.AutostartQueryFailed", "Failed to query autostart status"),
            PlatformErrorCodes.AutostartSetFailed => ("PlatformCapability.Error.AutostartSetFailed", "Failed to update autostart"),
            PlatformErrorCodes.AutostartVerificationFailed => ("PlatformCapability.Error.AutostartVerificationFailed", "Autostart verification failed"),
            PlatformErrorCodes.AutostartExecutableMissing => ("PlatformCapability.Error.AutostartExecutableMissing", "Autostart target executable is missing"),

            PlatformErrorCodes.OverlayUnsupported => ("PlatformCapability.Error.OverlayUnsupported", "Overlay is unsupported in the current environment"),
            PlatformErrorCodes.OverlayPreviewMode => ("PlatformCapability.Error.OverlayPreviewMode", "Overlay is running in preview mode"),
            PlatformErrorCodes.OverlayTargetInvalid => ("PlatformCapability.Error.OverlayTargetInvalid", "Overlay target is invalid"),
            PlatformErrorCodes.OverlayTargetGone => ("PlatformCapability.Error.OverlayTargetGone", "Overlay target is no longer available"),
            PlatformErrorCodes.OverlayQueryFailed => ("PlatformCapability.Error.OverlayQueryFailed", "Failed to query overlay targets"),
            PlatformErrorCodes.OverlayHostNotBound => ("PlatformCapability.Error.OverlayHostNotBound", "Overlay host window is not bound"),
            PlatformErrorCodes.OverlayAttachFailed => ("PlatformCapability.Error.OverlayAttachFailed", "Failed to attach overlay to the host window"),

            PlatformErrorCodes.PostActionUnsupported => ("PlatformCapability.Error.PostActionUnsupported", "Post action is unsupported in the current environment"),
            PlatformErrorCodes.PostActionExecutionFailed => ("PlatformCapability.Error.PostActionExecutionFailed", "Post action execution failed"),
            PlatformErrorCodes.PostActionPowerActionsDisabled => ("PlatformCapability.Error.PostActionPowerActionsDisabled", "Power actions are disabled for post action execution"),

            _ => ($"PlatformCapability.Error.{errorCode}", null),
        };
    }
}
