using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace MAAUnified.App.Infrastructure;

internal static class WindowVisuals
{
    private static readonly Uri AppIconUri = new("avares://MAAUnified/Assets/Brand/newlogo.ico");

    public static void ApplyDefaultIcon(Window window)
    {
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

        if (systemDecorations != SystemDecorations.None || !extendClientAreaToDecorationsHint)
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
}
