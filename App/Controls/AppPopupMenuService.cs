using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

public static class AppPopupMenuService
{
    private static readonly HashSet<AppPopupMenu> ActivePopups = [];

    public static AppPopupMenu Open(
        Control owner,
        IEnumerable<AppMenuEntry> items,
        EventHandler<AppMenuItemInvokedEventArgs> itemInvoked,
        PlacementMode placement = PlacementMode.Pointer,
        double verticalOffset = 0d)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemInvoked);

        var popup = new AppPopupMenu
        {
            PlacementTarget = owner,
            Placement = placement,
            VerticalOffset = verticalOffset,
            Items = items,
        };

        var host = ResolvePopupHost(owner);
        if (host is not null)
        {
            host.Children.Add(popup);
        }

        ActivePopups.Add(popup);

        void OnClosed(object? sender, EventArgs e)
        {
            popup.ItemInvoked -= itemInvoked;
            popup.Closed -= OnClosed;
            popup.Items = null;
            if (host is not null)
            {
                host.Children.Remove(popup);
            }

            ActivePopups.Remove(popup);
        }

        popup.ItemInvoked += itemInvoked;
        popup.Closed += OnClosed;
        popup.IsOpen = true;
        return popup;
    }

    private static Panel? ResolvePopupHost(Control owner)
    {
        if (TopLevel.GetTopLevel(owner) is ContentControl { Content: Panel topLevelPanel })
        {
            return topLevelPanel;
        }

        return owner.GetVisualAncestors().OfType<Panel>().LastOrDefault();
    }
}
