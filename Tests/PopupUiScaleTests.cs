using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

public sealed class PopupUiScaleTests
{
    [Fact]
    public void ApplyScale_WrapsUnparentedPopupChild_WithLayoutTransform()
    {
        var popup = new Popup();
        var child = new Border();

        popup.Child = child;
        PopupUiScale.ApplyScale(popup, 1d);

        var wrapper = Assert.IsType<LayoutTransformControl>(popup.Child);
        Assert.Same(child, wrapper.Child);
        var transform = Assert.IsType<ScaleTransform>(wrapper.LayoutTransform);
        Assert.Equal(1d, transform.ScaleX);
        Assert.Equal(1d, transform.ScaleY);
    }

    [Fact]
    public void ApplyScale_UpdatesExistingWrapperTransform()
    {
        var popup = new Popup();
        popup.Child = new Border();

        PopupUiScale.ApplyScale(popup, 1d);
        PopupUiScale.ApplyScale(popup, 1.25d);

        var wrapper = Assert.IsType<LayoutTransformControl>(popup.Child);
        var transform = Assert.IsType<ScaleTransform>(wrapper.LayoutTransform);
        Assert.Equal(1.25d, transform.ScaleX);
        Assert.Equal(1.25d, transform.ScaleY);
    }
}
