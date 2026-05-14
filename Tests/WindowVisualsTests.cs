using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.Tests;

public sealed class WindowVisualsTests
{
    [Theory]
    [InlineData(SystemDecorations.None, true, ExtendClientAreaChromeHints.NoChrome, true, true)]
    [InlineData(SystemDecorations.BorderOnly, true, ExtendClientAreaChromeHints.NoChrome, true, true)]
    [InlineData(SystemDecorations.Full, true, ExtendClientAreaChromeHints.NoChrome, true, false)]
    [InlineData(SystemDecorations.None, false, ExtendClientAreaChromeHints.NoChrome, true, false)]
    [InlineData(SystemDecorations.None, true, ExtendClientAreaChromeHints.Default, true, false)]
    [InlineData(SystemDecorations.None, true, ExtendClientAreaChromeHints.NoChrome, false, false)]
    public void ShouldApplyMacCustomChromeHints_ShouldMatchExtendedNoChromeMacWindows(
        SystemDecorations systemDecorations,
        bool extendClientAreaToDecorationsHint,
        ExtendClientAreaChromeHints extendClientAreaChromeHints,
        bool isMacOS,
        bool expected)
    {
        var actual = WindowVisuals.ShouldApplyMacCustomChromeHints(
            systemDecorations,
            extendClientAreaToDecorationsHint,
            extendClientAreaChromeHints,
            isMacOS);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SystemDecorations.None, true, true, true)]
    [InlineData(SystemDecorations.None, false, true, false)]
    [InlineData(SystemDecorations.BorderOnly, true, true, false)]
    [InlineData(SystemDecorations.None, true, false, false)]
    public void ShouldApplyMacResizableCustomChrome_ShouldOnlyApplyToResizableBorderlessMacWindows(
        SystemDecorations systemDecorations,
        bool canResize,
        bool isMacOS,
        bool expected)
    {
        var actual = WindowVisuals.ShouldApplyMacResizableCustomChrome(
            systemDecorations,
            canResize,
            isMacOS);

        Assert.Equal(expected, actual);
    }

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
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnTrue_ForTransparentMacBorderOnlyWindow()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.Transparent,
            SystemDecorations.BorderOnly,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 0,
            isMacOS: true);

        Assert.True(actual);
    }

    [Fact]
    public void ShouldApplyMacTransparentCustomChromeHint_ShouldReturnFalse_ForFullSystemDecorations()
    {
        var actual = WindowVisuals.ShouldApplyMacTransparentCustomChromeHint(
            Brushes.Transparent,
            SystemDecorations.Full,
            extendClientAreaToDecorationsHint: true,
            ExtendClientAreaChromeHints.NoChrome,
            transparencyLevelHintCount: 0,
            isMacOS: true);

        Assert.False(actual);
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
