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
    [InlineData(true, 12, 10, 8, 6, 8, 6, 4, 2)]
    [InlineData(true, 3, 2, 1, 0, 0, 0, 0, 0)]
    public void ResolveResizeGripMargin_ShouldMatchPlatformResizeStrategy(
        bool isMacOS,
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

        var actual = AppWindowFrame.ResolveResizeGripMargin(shellMargin, isMacOS);

        var expected = new Thickness(expectedLeft, expectedTop, expectedRight, expectedBottom);
        Assert.Equal(expected, actual);
    }

}
