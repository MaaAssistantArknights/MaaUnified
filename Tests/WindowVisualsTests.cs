using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.Tests;

public sealed class WindowVisualsTests
{
    [Fact]
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnTrue_ForTransparentBorderlessMacWindow()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.Transparent,
            SystemDecorations.None,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 0,
            isMacOS: true);

        Assert.True(actual);
    }

    [Fact]
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnFalse_WhenNotRunningOnMacOS()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.Transparent,
            SystemDecorations.None,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 0,
            isMacOS: false);

        Assert.False(actual);
    }

    [Fact]
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnFalse_WhenWindowAlreadyHasTransparencyHint()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.Transparent,
            SystemDecorations.None,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 1,
            isMacOS: true);

        Assert.False(actual);
    }

    [Fact]
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnFalse_ForOpaqueWindowBackground()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.White,
            SystemDecorations.None,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 0,
            isMacOS: true);

        Assert.False(actual);
    }
}
