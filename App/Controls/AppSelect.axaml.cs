using System;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public interface IAppSelectItemVisibility
{
    bool IsVisible { get; }
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
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        container.IsVisible = true;
    }
}
