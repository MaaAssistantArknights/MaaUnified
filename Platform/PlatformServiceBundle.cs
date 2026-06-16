namespace MAAUnified.Platform;

public sealed class PlatformServiceBundle
{
    public required ITrayService TrayService { get; init; }

    public required INotificationService NotificationService { get; init; }

    public required IGlobalHotkeyService HotkeyService { get; init; }

    public required IAutostartService AutostartService { get; init; }

    public required IFileDialogService FileDialogService { get; init; }

    public required IOverlayCapabilityService OverlayService { get; init; }

    public required IPostActionExecutorService PostActionExecutorService { get; init; }

    public IGpuCapabilityService GpuCapabilityService { get; init; } = new UnsupportedGpuCapabilityService();
}

public static class PlatformServicesFactory
{
    public static PlatformServiceBundle CreateDefaults()
    {
        var forceFallback = string.Equals(
            Environment.GetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        ITrayService trayService;
        INotificationService notificationService;
        IGlobalHotkeyService hotkeyService;
        IAutostartService autostartService;
        IOverlayCapabilityService overlayService;
        IPostActionExecutorService postActionExecutorService;
        IGpuCapabilityService gpuCapabilityService;

        try
        {
            if (!forceFallback
                && OperatingSystem.IsMacOS()
                && MacStatusItemTrayService.TryCreate(out var macStatusItemTray))
            {
                trayService = macStatusItemTray;
            }
            else if (!forceFallback
                && OperatingSystem.IsWindows()
                && WindowsNotifyIconTrayService.TryCreate(out var windowsTray))
            {
                trayService = windowsTray;
            }
            else if (!forceFallback
                     && AvaloniaTrayIconTrayService.TryCreate(out var nativeAvaloniaTray))
            {
                trayService = nativeAvaloniaTray;
            }
            else
            {
                trayService = new WindowMenuTrayService();
            }
        }
        catch
        {
            trayService = new NoOpTrayService();
        }

        try
        {
            if (!forceFallback && DesktopNotificationService.TryCreate(out var nativeNotification))
            {
                notificationService = nativeNotification;
            }
            else
            {
                notificationService = new CommandNotificationService();
            }
        }
        catch
        {
            notificationService = new NoOpNotificationService();
        }

        try
        {
            if (!forceFallback
                && OperatingSystem.IsLinux()
                && LinuxDesktopSessionDetector.Detect() == LinuxDesktopSessionKind.Wayland
                && LinuxPortalGlobalHotkeyService.TryCreate(out var waylandPortalHotkey))
            {
                hotkeyService = new CompositeGlobalHotkeyService(
                    waylandPortalHotkey,
                    new WindowScopedHotkeyService());
            }
            else if (!forceFallback
                     && OperatingSystem.IsMacOS()
                     && MacCarbonGlobalHotkeyService.TryCreate(out var macCarbonHotkey))
            {
                hotkeyService = new CompositeGlobalHotkeyService(
                    macCarbonHotkey,
                    new WindowScopedHotkeyService());
            }
            else if (!forceFallback && SharpHookGlobalHotkeyService.TryCreate(out var nativeHotkey))
            {
                hotkeyService = new CompositeGlobalHotkeyService(
                    nativeHotkey,
                    new WindowScopedHotkeyService());
            }
            else
            {
                hotkeyService = new WindowScopedHotkeyService();
            }
        }
        catch
        {
            hotkeyService = new NoOpGlobalHotkeyService();
        }

        try
        {
            autostartService = new CrossPlatformAutostartService();
        }
        catch
        {
            autostartService = new NoOpAutostartService();
        }

        try
        {
            if (!forceFallback && OperatingSystem.IsWindows())
            {
                overlayService = new WindowsOverlayCapabilityService();
            }
            else if (!forceFallback && MacOverlayCapabilityService.TryCreate(out var macOverlay))
            {
                overlayService = macOverlay;
            }
            else if (!forceFallback && LinuxOverlayCapabilityService.TryCreate(out var linuxOverlay))
            {
                overlayService = linuxOverlay;
            }
            else
            {
                overlayService = new NoOpOverlayCapabilityService();
            }
        }
        catch
        {
            overlayService = new NoOpOverlayCapabilityService();
        }

        try
        {
            postActionExecutorService = new CommandPostActionExecutorService();
        }
        catch
        {
            postActionExecutorService = new NoOpPostActionExecutorService();
        }

        try
        {
            gpuCapabilityService = OperatingSystem.IsWindows()
                ? new WindowsGpuCapabilityService()
                : new UnsupportedGpuCapabilityService();
        }
        catch
        {
            gpuCapabilityService = new UnsupportedGpuCapabilityService();
        }

        return new PlatformServiceBundle {
            TrayService = trayService,
            NotificationService = notificationService,
            HotkeyService = hotkeyService,
            AutostartService = autostartService,
            FileDialogService = new NoOpFileDialogService(),
            OverlayService = overlayService,
            PostActionExecutorService = postActionExecutorService,
            GpuCapabilityService = gpuCapabilityService,
        };
    }
}
