using MAAUnified.App.Views;

namespace MAAUnified.Tests;

public sealed class MainWindowSizingTests
{
    [Theory]
    [InlineData(false, 1380)]
    [InlineData(true, 1794)]
    public void ComputeDefaultWindowWidth_ShouldEnlargeMacOSDefault(bool isMacOS, double expected)
    {
        var actual = MainWindow.ComputeDefaultWindowWidth(isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(false, 900)]
    [InlineData(true, 1170)]
    public void ComputeDefaultWindowHeight_ShouldEnlargeMacOSDefault(bool isMacOS, double expected)
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
}
