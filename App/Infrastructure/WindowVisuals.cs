using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace MAAUnified.App.Infrastructure;

internal static class WindowVisuals
{
    private static readonly Uri AppIconUri = new("avares://MAAUnified/Assets/Brand/newlogo.ico");
    private const double MacCustomChromeTitleBarHeight = 32d;
    private const ulong MacResizableWindowStyleMask = 1UL << 3;

    public static void ApplyDefaultIcon(Window window)
    {
        ApplyMacCustomChromeHints(window);

        if (window.Icon is not null)
        {
            ApplyMacTransparentCustomChromeHint(window);
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(AppIconUri);
            window.Icon = new WindowIcon(stream);
        }
        catch
        {
            // Keep window creation resilient when the embedded icon is unavailable.
        }

        ApplyMacTransparentCustomChromeHint(window);
    }

    internal static bool ShouldApplyMacTransparentCustomChromeHint(
        IBrush? background,
        SystemDecorations systemDecorations,
        bool extendClientAreaToDecorationsHint,
        ExtendClientAreaChromeHints extendClientAreaChromeHints,
        int transparencyLevelHintCount,
        bool isMacOS)
    {
        if (!isMacOS || transparencyLevelHintCount > 0)
        {
            return false;
        }

        if ((systemDecorations != SystemDecorations.None
                && systemDecorations != SystemDecorations.BorderOnly)
            || !extendClientAreaToDecorationsHint)
        {
            return false;
        }

        if (extendClientAreaChromeHints != ExtendClientAreaChromeHints.NoChrome)
        {
            return false;
        }

        return background is ISolidColorBrush solidColorBrush && solidColorBrush.Color.A == 0;
    }

    private static void ApplyMacTransparentCustomChromeHint(Window window)
    {
        if (!ShouldApplyMacTransparentCustomChromeHint(
                window.Background,
                window.SystemDecorations,
                window.ExtendClientAreaToDecorationsHint,
                window.ExtendClientAreaChromeHints,
                window.TransparencyLevelHint.Count,
                OperatingSystem.IsMacOS()))
        {
            return;
        }

        // Avalonia custom-chrome windows default to a white transparency fallback
        // unless we explicitly request an alpha-capable top-level.
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
    }

    private static void ApplyMacCustomChromeHints(Window window)
    {
        ApplyMacResizableCustomChrome(window);

        if (!ShouldApplyMacCustomChromeHints(
                window.SystemDecorations,
                window.ExtendClientAreaToDecorationsHint,
                window.ExtendClientAreaChromeHints,
                OperatingSystem.IsMacOS()))
        {
            return;
        }

        window.ExtendClientAreaTitleBarHeightHint = MacCustomChromeTitleBarHeight;
    }

    public static void ApplyMacResizableCustomChrome(Window window)
    {
        if (!ShouldApplyMacResizableCustomChrome(
                window.SystemDecorations,
                window.CanResize,
                OperatingSystem.IsMacOS()))
        {
            return;
        }

        // BorderOnly keeps the app-drawn title row while letting macOS expose
        // native resize behavior around borderless, extended-client windows.
        window.SystemDecorations = SystemDecorations.BorderOnly;
        EnsureMacNativeResizableStyleMask(window);
    }

    internal static bool ShouldApplyMacCustomChromeHints(
        SystemDecorations systemDecorations,
        bool extendClientAreaToDecorationsHint,
        ExtendClientAreaChromeHints extendClientAreaChromeHints,
        bool isMacOS)
    {
        return isMacOS
            && extendClientAreaToDecorationsHint
            && extendClientAreaChromeHints == ExtendClientAreaChromeHints.NoChrome
            && (systemDecorations == SystemDecorations.None
                || systemDecorations == SystemDecorations.BorderOnly);
    }

    internal static bool ShouldApplyMacResizableCustomChrome(
        SystemDecorations systemDecorations,
        bool canResize,
        bool isMacOS)
    {
        return isMacOS
            && canResize
            && systemDecorations == SystemDecorations.None;
    }

    internal static bool ShouldApplyMacNativeResizableStyleMask(
        SystemDecorations systemDecorations,
        bool canResize,
        bool isMacOS)
    {
        return isMacOS
            && canResize
            && systemDecorations == SystemDecorations.BorderOnly;
    }

    internal static ulong AddMacResizableStyleMask(ulong styleMask)
    {
        return styleMask | MacResizableWindowStyleMask;
    }

    internal static bool IsMacWindowHandleDescriptor(string? handleDescriptor)
    {
        return string.Equals(handleDescriptor, "NSWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureMacNativeResizableStyleMask(Window window)
    {
        if (!ShouldApplyMacNativeResizableStyleMask(
                window.SystemDecorations,
                window.CanResize,
                OperatingSystem.IsMacOS()))
        {
            return;
        }

        if (TryApplyMacNativeResizableStyleMask(window))
        {
            return;
        }

        window.Opened -= OnMacResizableWindowOpened;
        window.Opened += OnMacResizableWindowOpened;
    }

    private static void OnMacResizableWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Opened -= OnMacResizableWindowOpened;
        TryApplyMacNativeResizableStyleMask(window);
    }

    private static bool TryApplyMacNativeResizableStyleMask(Window window)
    {
        if (!ShouldApplyMacNativeResizableStyleMask(
                window.SystemDecorations,
                window.CanResize,
                OperatingSystem.IsMacOS()))
        {
            return false;
        }

        var platformHandle = window.TryGetPlatformHandle();
        var nativeHandle = platformHandle?.Handle ?? nint.Zero;
        if (nativeHandle == nint.Zero || !IsMacWindowHandleDescriptor(platformHandle?.HandleDescriptor))
        {
            return false;
        }

        var styleMask = MacNativeWindowInterop.GetStyleMask(nativeHandle);
        var updatedStyleMask = AddMacResizableStyleMask(styleMask);
        if (updatedStyleMask == styleMask)
        {
            return true;
        }

        MacNativeWindowInterop.SetStyleMask(nativeHandle, updatedStyleMask);
        return true;
    }

    private static class MacNativeWindowInterop
    {
        private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        private static readonly nint StyleMaskSelector = sel_registerName("styleMask");
        private static readonly nint SetStyleMaskSelector = sel_registerName("setStyleMask:");

        public static ulong GetStyleMask(nint windowHandle)
        {
            return objc_msgSend_ulong(windowHandle, StyleMaskSelector);
        }

        public static void SetStyleMask(nint windowHandle, ulong styleMask)
        {
            objc_msgSend_void_ulong(windowHandle, SetStyleMaskSelector, styleMask);
        }

        [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
        private static extern nint sel_registerName(string name);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern ulong objc_msgSend_ulong(nint receiver, nint selector);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void_ulong(nint receiver, nint selector, ulong value);
    }
}
