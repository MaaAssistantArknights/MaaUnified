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
    [InlineData(false, 12, 10, 8, 6, 12, 10, 8, 6)]
    [InlineData(true, 12, 10, 8, 6, 0, 0, 0, 0)]
    public void ResolveResizeGripMargin_ShouldMatchNativeShadowStrategy(
        bool usesNativeWindowShadow,
        double left,
        double top,
        double right,
        double bottom,
        double expectedLeft,
        double expectedTop,
        double expectedRight,
        double expectedBottom)
    {
        var shellMargin = new Thickness(left, top, right, bottom);

        var actual = AppWindowFrame.ResolveResizeGripMargin(shellMargin, usesNativeWindowShadow);

        var expected = new Thickness(expectedLeft, expectedTop, expectedRight, expectedBottom);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void ShouldUseNativeWindowShadow_ShouldOnlyUseNormalResizableMacWindows(
        bool isMacOS,
        bool isResizableDialog,
        bool isHostNormalState,
        bool canResize,
        bool expected)
    {
        var actual = AppWindowFrame.ShouldUseNativeWindowShadow(
            isMacOS,
            isResizableDialog,
            isHostNormalState,
            canResize);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppWindowFrameStyles_ShouldExposeNativeWindowShadowState()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var control = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppWindowFrame.cs"));
        var styles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));

        Assert.Contains("\":native-window-shadow\"", control, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppWindowFrame:native-window-shadow\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ShellMargin\" Value=\"0\" />", styles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppWindowFrame:native-window-shadow /template/ Border#PART_FrameSurface\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BoxShadow\" Value=\"0 0 0 0 #00000000\" />", styles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppWindowFrame /template/ Border#PART_FrameSurface\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BoxShadow\" Value=\"0 0 24 0 #29000000\" />", styles, StringComparison.Ordinal);
    }
}
