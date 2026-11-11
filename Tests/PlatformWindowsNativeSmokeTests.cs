using MAAUnified.Platform;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.Tests;

public sealed class PlatformWindowsNativeSmokeTests
{
    [Fact]
    public void WindowsNotifyIconTrayService_ShouldStartHiddenAndMapPrimaryActivationToForceShow()
    {
        var service = new WindowsNotifyIconTrayService();

        Assert.False(service.IsVisibleForTesting);
        Assert.True(WindowsNotifyIconTrayService.TryGetActivationCommand(0x0202, out var clickCommand));
        Assert.Equal(TrayCommandId.ForceShow, clickCommand);
        Assert.True(WindowsNotifyIconTrayService.TryGetActivationCommand(0x0203, out var doubleClickCommand));
        Assert.Equal(TrayCommandId.ForceShow, doubleClickCommand);
        Assert.False(WindowsNotifyIconTrayService.TryGetActivationCommand(0x0205, out _));
        Assert.True(WindowsNotifyIconTrayService.ShouldDispatchPrimaryActivation(1_000, -1, 500));
        Assert.False(WindowsNotifyIconTrayService.ShouldDispatchPrimaryActivation(1_200, 1_000, 500));
        Assert.True(WindowsNotifyIconTrayService.ShouldDispatchPrimaryActivation(1_500, 1_000, 500));
    }

    [Fact]
    public void WindowsNotifyIconTrayService_ShouldMapLanguageSubmenuSelectionToTargetLanguage()
    {
        var menuText = TrayMenuText.Default with
        {
            LanguageItems =
            [
                new TrayLanguageMenuItem("zh-cn", "简体中文"),
                new TrayLanguageMenuItem("en-us", "English"),
                new TrayLanguageMenuItem("ja-jp", "日本語"),
            ],
        };

        Assert.True(WindowsNotifyIconTrayService.TryMapNativeMenuCommand(
            11_003,
            menuText,
            out var command,
            out var targetLanguage));
        Assert.Equal(TrayCommandId.SwitchLanguage, command);
        Assert.Equal("ja-jp", targetLanguage);

        var trayText = PlatformCapabilityTextMap.CreateTrayMenuText("zh-cn");
        Assert.DoesNotContain(trayText.LanguageItems!, item => item.Language == "pallas");
    }

    [Fact]
    public void WindowsHealthyEnvironment_ShouldDefaultToNativeProviders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var original = Environment.GetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK", null);
            var bundle = PlatformServicesFactory.CreateDefaults();

            Assert.IsType<WindowsNotifyIconTrayService>(bundle.TrayService);
            Assert.IsType<DesktopNotificationService>(bundle.NotificationService);
            Assert.True(WindowsToastNotificationPoster.TryCreate(out var notificationPoster));
            notificationPoster.Dispose();
            Assert.True(bundle.HotkeyService is SharpHookGlobalHotkeyService or CompositeGlobalHotkeyService);
            Assert.IsType<CrossPlatformAutostartService>(bundle.AutostartService);
            Assert.IsType<WindowsOverlayCapabilityService>(bundle.OverlayService);

            var snapshot = PlatformCapabilitySnapshotFactory.FromBundle(bundle);
            Assert.True(snapshot.Tray.Supported);
            Assert.Equal("windows-shell-notifyicon", snapshot.Tray.Provider);
            Assert.Equal("windows-toast", snapshot.Notification.Provider);
            Assert.True(snapshot.Notification.HasFallback);
            Assert.True(snapshot.Hotkey.Supported);
            Assert.True(snapshot.Autostart.Supported);
            Assert.True(snapshot.Overlay.Supported);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK", original);
        }
    }

    [Fact]
    public void WindowsForcedFallback_ShouldStillExposeFallbackCapabilities_ExceptAutostart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var original = Environment.GetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK", "1");
            var bundle = PlatformServicesFactory.CreateDefaults();

            Assert.IsType<WindowMenuTrayService>(bundle.TrayService);
            Assert.IsType<CommandNotificationService>(bundle.NotificationService);
            Assert.True(bundle.HotkeyService is WindowScopedHotkeyService or CompositeGlobalHotkeyService);
            Assert.IsType<CrossPlatformAutostartService>(bundle.AutostartService);
            Assert.IsType<NoOpOverlayCapabilityService>(bundle.OverlayService);

            var snapshot = PlatformCapabilitySnapshotFactory.FromBundle(bundle);
            Assert.True(snapshot.Tray.HasFallback);
            Assert.True(snapshot.Notification.Supported);
            Assert.True(snapshot.Notification.HasFallback);
            Assert.True(snapshot.Hotkey.HasFallback);
            Assert.True(snapshot.Autostart.Supported);
            Assert.False(snapshot.Autostart.HasFallback);
            Assert.Equal("registry-run", snapshot.Autostart.Provider);
            Assert.True(snapshot.Overlay.HasFallback);

            var notificationAvailability = bundle.NotificationService.GetAvailability();
            Assert.False(notificationAvailability.IsAvailable);
            Assert.Equal(NotificationAvailabilityReason.BackendUnavailable, notificationAvailability.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAA_PLATFORM_FORCE_FALLBACK", original);
        }
    }
}
