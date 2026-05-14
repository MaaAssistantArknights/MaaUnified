using Avalonia;
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
}
