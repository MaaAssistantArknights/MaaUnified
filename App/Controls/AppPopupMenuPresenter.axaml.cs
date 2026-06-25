using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MAAUnified.App.Controls;

public partial class AppPopupMenuPresenter : UserControl
{
    public static readonly StyledProperty<IEnumerable<AppMenuEntry>?> ItemsProperty =
        AvaloniaProperty.Register<AppPopupMenuPresenter, IEnumerable<AppMenuEntry>?>(nameof(Items));

    public AppPopupMenuPresenter()
    {
        InitializeComponent();
    }

    public event EventHandler<AppMenuItemInvokedEventArgs>? ItemInvoked;

    public IEnumerable<AppMenuEntry>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private void OnMenuItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: AppMenuActionItem item } control || !item.IsEnabled)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed
            && point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        ItemInvoked?.Invoke(this, new AppMenuItemInvokedEventArgs(item));
    }
}

