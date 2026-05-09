using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

public sealed class AppSelectionListStateTests
{
    [Fact]
    public void AppSelectionList_ShouldDefaultToSurfaceModeClasses()
    {
        var list = new AppSelectionList();

        Assert.Equal(AppSelectionListVisualMode.Surface, list.VisualMode);
        Assert.Contains("selection-list-surface", list.Classes);
        Assert.DoesNotContain("selection-list-rail", list.Classes);
        Assert.DoesNotContain("selection-list-none", list.Classes);
        Assert.DoesNotContain("selection-list-rail-trailing-accessory-space", list.Classes);
    }

    [Fact]
    public void AppSelectionList_ShouldSwitchVisualModeClassesWithoutLeavingStaleModeFlags()
    {
        var list = new AppSelectionList();

        list.VisualMode = AppSelectionListVisualMode.Rail;

        Assert.Contains("selection-list-rail", list.Classes);
        Assert.DoesNotContain("selection-list-surface", list.Classes);
        Assert.DoesNotContain("selection-list-none", list.Classes);

        list.VisualMode = AppSelectionListVisualMode.None;

        Assert.Contains("selection-list-none", list.Classes);
        Assert.DoesNotContain("selection-list-rail", list.Classes);
        Assert.DoesNotContain("selection-list-surface", list.Classes);
    }

    [Fact]
    public void AppSelectionList_ShouldToggleTrailingAccessoryReservationClassFromState()
    {
        var list = new AppSelectionList();

        list.ReserveTrailingAccessorySpace = true;
        Assert.Contains("selection-list-rail-trailing-accessory-space", list.Classes);

        list.ReserveTrailingAccessorySpace = false;
        Assert.DoesNotContain("selection-list-rail-trailing-accessory-space", list.Classes);
    }

    [Fact]
    public void AppSelectionList_ShouldToggleReorderClassFromState()
    {
        var list = new AppSelectionList();

        list.CanReorderItems = true;
        Assert.Contains("selection-list-reorderable", list.Classes);

        list.CanReorderItems = false;
        Assert.DoesNotContain("selection-list-reorderable", list.Classes);
    }
}
