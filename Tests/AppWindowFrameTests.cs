using Avalonia;
using Avalonia.Controls;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

public sealed class AppWindowFrameTests
{
    [Fact]
    public void AppWindowFrameResizeGrips_ShouldUseNonTransparentHitTargets()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var styles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));
        var resizeGripTemplateStart = styles.IndexOf("x:Name=\"PART_ResizeNorth\"", StringComparison.Ordinal);
        var resizeGripTemplateEnd = styles.IndexOf("</Grid>", resizeGripTemplateStart, StringComparison.Ordinal);
        var resizeGripTemplate = styles.Substring(resizeGripTemplateStart, resizeGripTemplateEnd - resizeGripTemplateStart);

        Assert.Equal(8, resizeGripTemplate.Split("Background=\"#01000000\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Background=\"Transparent\"", resizeGripTemplate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveResizeGripMargin_ShouldMatchPlatformResizeStrategy(bool preferOuterResizeGrips)
    {
        var shellMargin = new Thickness(12, 10, 8, 6);

        var actual = AppWindowFrame.ResolveResizeGripMargin(shellMargin, preferOuterResizeGrips);

        var expected = preferOuterResizeGrips
            ? default(Thickness)
            : shellMargin;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeManualResizeDrag_ShouldResizeFromSouthEastWithoutMovingOrigin()
    {
        var state = new AppWindowFrame.ManualResizeDragState(
            WindowEdge.SouthEast,
            new PixelPoint(1000, 500),
            new PixelPoint(320, 240),
            new PixelSize(2000, 1400),
            MinWidth: 1200,
            MinHeight: 800,
            MaxWidth: 0,
            MaxHeight: 0,
            RenderScaling: 2d);

        var actual = AppWindowFrame.ComputeManualResizeDrag(state, new PixelPoint(1160, 600));

        Assert.Equal(new Size(1080, 750), actual.Size);
        Assert.Equal(new PixelPoint(320, 240), actual.Position);
    }

    [Fact]
    public void ComputeManualResizeDrag_ShouldClampWestResizeAndMoveWindowOriginByAppliedDelta()
    {
        var state = new AppWindowFrame.ManualResizeDragState(
            WindowEdge.West,
            new PixelPoint(1000, 500),
            new PixelPoint(300, 120),
            new PixelSize(1800, 1400),
            MinWidth: 1680,
            MinHeight: 800,
            MaxWidth: 0,
            MaxHeight: 0,
            RenderScaling: 2d);

        var actual = AppWindowFrame.ComputeManualResizeDrag(state, new PixelPoint(1240, 500));

        Assert.Equal(new Size(840, 700), actual.Size);
        Assert.Equal(new PixelPoint(420, 120), actual.Position);
    }

    [Fact]
    public void ComputeManualResizeDrag_ShouldClampNorthWestResizeToMinSizeAndMoveBothAxes()
    {
        var state = new AppWindowFrame.ManualResizeDragState(
            WindowEdge.NorthWest,
            new PixelPoint(1000, 500),
            new PixelPoint(500, 400),
            new PixelSize(1800, 1400),
            MinWidth: 1720,
            MinHeight: 1320,
            MaxWidth: 0,
            MaxHeight: 0,
            RenderScaling: 2d);

        var actual = AppWindowFrame.ComputeManualResizeDrag(state, new PixelPoint(1160, 640));

        Assert.Equal(new Size(860, 660), actual.Size);
        Assert.Equal(new PixelPoint(580, 480), actual.Position);
    }

    [Fact]
    public void ResolveManualResizeAnchoredPosition_ShouldKeepOppositeEdgesFixedUsingActualWindowSize()
    {
        var state = new AppWindowFrame.ManualResizeDragState(
            WindowEdge.NorthWest,
            new PixelPoint(1000, 500),
            new PixelPoint(500, 400),
            new PixelSize(1800, 1400),
            MinWidth: 1720,
            MinHeight: 1320,
            MaxWidth: 0,
            MaxHeight: 0,
            RenderScaling: 2d);

        var actual = AppWindowFrame.ResolveManualResizeAnchoredPosition(
            state,
            new PixelSize(1760, 1360),
            new PixelPoint(999, 999));

        Assert.Equal(new PixelPoint(540, 440), actual);
    }
}
