using Avalonia.Input;

namespace MAAUnified.App.Controls;

public abstract record AppMenuEntry;

public sealed record AppMenuActionItem(
    string Header,
    object Command,
    object? Parameter = null,
    bool IsEnabled = true,
    bool IsVisible = true) : AppMenuEntry;

public sealed record AppMenuSeparatorEntry() : AppMenuEntry;

public sealed class AppMenuItemInvokedEventArgs(AppMenuActionItem item) : EventArgs
{
    public AppMenuActionItem Item { get; } = item;

    public object Command => Item.Command;

    public object? Parameter => Item.Parameter;
}

