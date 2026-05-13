using Avalonia;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

public sealed class AppWindowFrameTests
{
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
