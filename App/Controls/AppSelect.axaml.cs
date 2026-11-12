using System;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public interface IAppSelectItemVisibility
{
    bool IsVisible { get; }
}

public interface IAppSelectItemEnablement
{
    bool IsEnabled { get; }
}

public class AppSelect : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public AppSelect()
    {
        Classes.Set("settings-select", true);
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        container.IsVisible = item is not IAppSelectItemVisibility visibility || visibility.IsVisible;
        container.IsEnabled = item is not IAppSelectItemEnablement enablement || enablement.IsEnabled;
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        container.IsVisible = true;
        container.IsEnabled = true;
    }
}
