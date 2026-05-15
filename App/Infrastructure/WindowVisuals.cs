using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace MAAUnified.App.Infrastructure;

internal static class WindowVisuals
{
    private static readonly Uri AppIconUri = new("avares://MAAUnified/Assets/Brand/newlogo.ico");
    private const double MacCustomChromeTitleBarHeight = 32d;

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

    internal static bool ShouldApplyMacNativeWindowShadow(
        IBrush? background,
        SystemDecorations systemDecorations,
        bool extendClientAreaToDecorationsHint,
        ExtendClientAreaChromeHints extendClientAreaChromeHints,
        bool canResize,
        bool isMacOS)
    {
        if (!isMacOS || !canResize)
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

    public static bool TryApplyMacNativeWindowShadow(Window window)
    {
        if (!ShouldApplyMacNativeWindowShadow(
                window.Background,
                window.SystemDecorations,
                window.ExtendClientAreaToDecorationsHint,
                window.ExtendClientAreaChromeHints,
                window.CanResize,
                OperatingSystem.IsMacOS()))
        {
            return false;
        }

        if (window.TryGetPlatformHandle() is not { Handle: not 0 } platformHandle)
        {
            return false;
        }

        try
        {
            MacNativeWindowShadowInterop.Apply(platformHandle.Handle);
            return true;
        }
        catch (Exception ex)
        {
            Program.RecordStartupStage(
                "WindowVisuals.MacNativeWindowShadow.Fail",
                $"Failed to apply native macOS window shadow; descriptor={platformHandle.HandleDescriptor ?? string.Empty}.",
                ex);
            return false;
        }
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

    private static class MacNativeWindowShadowInterop
    {
        private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        private static readonly nint SetHasShadowSelector = sel_registerName("setHasShadow:");
        private static readonly nint InvalidateShadowSelector = sel_registerName("invalidateShadow");

        public static void Apply(nint nsWindow)
        {
            if (nsWindow == 0)
            {
                return;
            }

            objc_msgSend(nsWindow, SetHasShadowSelector, true);
            objc_msgSend(nsWindow, InvalidateShadowSelector);
        }

        [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
        private static extern nint sel_registerName(string selectorName);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(
            nint receiver,
            nint selector,
            [MarshalAs(UnmanagedType.I1)] bool value);
    }
}
