using MAAUnified.App.Views;
using Avalonia.Controls;

namespace MAAUnified.Tests;

public sealed class MainWindowSizingTests
{
    [Theory]
    [InlineData(false, 1380)]
    [InlineData(true, 1380)]
    public void ComputeDefaultWindowWidth_ShouldMatchMacOSDefault(bool isMacOS, double expected)
    {
        var actual = MainWindow.ComputeDefaultWindowWidth(isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(false, 900)]
    [InlineData(true, 900)]
    public void ComputeDefaultWindowHeight_ShouldMatchMacOSDefault(bool isMacOS, double expected)
    {
        var actual = MainWindow.ComputeDefaultWindowHeight(isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(0.9, 0.0, true, 0.9)]
    [InlineData(0.9, 1.0, true, 0.9)]
    [InlineData(0.9, 2.0, true, 1.008)]
    [InlineData(1.0, 2.5, true, 1.18)]
    [InlineData(0.9, 2.0, false, 0.9)]
    public void ComputeWindowHeightScale_ShouldApplyMacHiDpiBoost(
        double effectiveUiScaleFactor,
        double renderScaling,
        bool isMacOS,
        double expected)
    {
        var actual = MainWindow.ComputeWindowHeightScale(effectiveUiScaleFactor, renderScaling, isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(0.9, 0.9)]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    public void ComputeWindowWidthScale_ShouldFollowUiScaleOnly(double effectiveUiScaleFactor, double expected)
    {
        var actual = MainWindow.ComputeWindowWidthScale(effectiveUiScaleFactor);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(1380, 1.0, 0.9, 1380, 972, false, false, 1242)]
    [InlineData(1380, 1.0, 0.81, 1380, 874.8, false, true, 1380)]
    [InlineData(1117.8, 0.81, 1.0, 1380, 1080, true, false, 1380)]
    public void ResolveWindowSizeTarget_ShouldRespectMacPlatformDefaultWhenRequested(
        double currentSize,
        double previousScale,
        double nextScale,
        double defaultSize,
        double minSize,
        bool preserveLogicalSize,
        bool keepMacPlatformDefaultSize,
        double expected)
    {
        var actual = MainWindow.ResolveWindowSizeTarget(
            currentSize,
            previousScale,
            nextScale,
            defaultSize,
            minSize,
            preserveLogicalSize,
            keepMacPlatformDefaultSize);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(3024, 2.0, 1512)]
    [InlineData(1512, 1.0, 1512)]
    [InlineData(200, 2.0, 320)]
    [InlineData(3024, 0.0, 3024)]
    public void ConvertScreenPixelsToWindowUnits_ShouldUseDesktopScaling(
        double pixelLength,
        double desktopScaling,
        double expected)
    {
        var actual = MainWindow.ConvertScreenPixelsToWindowUnits(pixelLength, desktopScaling);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(WindowResizeReason.User, true)]
    [InlineData(WindowResizeReason.Application, false)]
    [InlineData(WindowResizeReason.Layout, false)]
    [InlineData(WindowResizeReason.DpiChange, false)]
    [InlineData(WindowResizeReason.Unspecified, false)]
    public void ShouldTreatResizeAsLiveInteraction_ShouldOnlyTrackUserResize(
        WindowResizeReason reason,
        bool expected)
    {
        var actual = MainWindow.ShouldTreatResizeAsLiveInteraction(reason);

        Assert.Equal(expected, actual);
    }
}
