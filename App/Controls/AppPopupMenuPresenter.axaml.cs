using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

internal enum AppPopupMenuKeyboardAction
{
    None,
    MovePrevious,
    MoveNext,
    MoveToFirst,
    MoveToLast,
    Invoke,
    Dismiss,
}

public partial class AppPopupMenuPresenter : UserControl
{
    private int _activeItemIndex = -1;

    public static readonly StyledProperty<IEnumerable<AppMenuEntry>?> ItemsProperty =
        AvaloniaProperty.Register<AppPopupMenuPresenter, IEnumerable<AppMenuEntry>?>(nameof(Items));

    public AppPopupMenuPresenter()
    {
        InitializeComponent();
    }

    public event EventHandler<AppMenuItemInvokedEventArgs>? ItemInvoked;

    public event EventHandler<AppMenuSubmenuInvokedEventArgs>? SubmenuInvoked;
    public event EventHandler? DismissRequested;

    internal AppMenuActionItem? ActiveItem => GetActionControls()
        .ElementAtOrDefault(_activeItemIndex)
        ?.Tag as AppMenuActionItem;

    internal int EnabledActionCount => GetActionControls().Count;

    internal bool FocusFirstEnabledItem()
    {
        var controls = GetActionControls();
        if (controls.Count == 0)
        {
            _activeItemIndex = -1;
            return false;
        }

        _activeItemIndex = 0;
        return controls[0].Focus(NavigationMethod.Directional, KeyModifiers.None);
    }


    public IEnumerable<AppMenuEntry>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private void OnSubmenuItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: AppMenuSubmenuItem item } control || !item.IsEnabled)
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
        SubmenuInvoked?.Invoke(this, new AppMenuSubmenuInvokedEventArgs(item));
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
        FocusActionControl(control);
        InvokeItem(item);
    }

    private void OnMenuItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { Tag: AppMenuActionItem item } control || !item.IsEnabled)
        {
            return;
        }

        switch (GetKeyboardAction(item.IsEnabled, e.Key))
        {
            case AppPopupMenuKeyboardAction.MovePrevious:
                MoveFocus(-1);
                break;
            case AppPopupMenuKeyboardAction.MoveNext:
                MoveFocus(1);
                break;
            case AppPopupMenuKeyboardAction.MoveToFirst:
                FocusItemAt(0);
                break;
            case AppPopupMenuKeyboardAction.MoveToLast:
                FocusItemAt(GetActionControls().Count - 1);
                break;
            case AppPopupMenuKeyboardAction.Invoke:
                FocusActionControl(control);
                InvokeItem(item);
                break;
            case AppPopupMenuKeyboardAction.Dismiss:
                DismissRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    internal static AppPopupMenuKeyboardAction GetKeyboardAction(bool isEnabled, Key key)
    {
        if (!isEnabled)
        {
            return AppPopupMenuKeyboardAction.None;
        }

        return key switch
        {
            Key.Up => AppPopupMenuKeyboardAction.MovePrevious,
            Key.Down => AppPopupMenuKeyboardAction.MoveNext,
            Key.Home => AppPopupMenuKeyboardAction.MoveToFirst,
            Key.End => AppPopupMenuKeyboardAction.MoveToLast,
            Key.Enter or Key.Space => AppPopupMenuKeyboardAction.Invoke,
            Key.Escape => AppPopupMenuKeyboardAction.Dismiss,
            _ => AppPopupMenuKeyboardAction.None,
        };
    }

    internal static int GetNextActionIndex(int currentIndex, int actionCount, int direction)
    {
        if (actionCount <= 0)
        {
            return -1;
        }

        var step = Math.Sign(direction);
        if (step == 0)
        {
            return currentIndex >= 0 && currentIndex < actionCount ? currentIndex : -1;
        }

        if (currentIndex < 0 || currentIndex >= actionCount)
        {
            return step < 0 ? actionCount - 1 : 0;
        }

        return (currentIndex + step + actionCount) % actionCount;
    }

    private void InvokeItem(AppMenuActionItem item)
    {
        ItemInvoked?.Invoke(this, new AppMenuItemInvokedEventArgs(item));
    }

    private void MoveFocus(int direction)
    {
        var controls = GetActionControls();
        if (controls.Count == 0)
        {
            return;
        }

        FocusItemAt(GetNextActionIndex(ResolveCurrentIndex(controls), controls.Count, direction));
    }

    private void FocusItemAt(int index)
    {
        var controls = GetActionControls();
        if (index < 0 || index >= controls.Count)
        {
            return;
        }

        FocusActionControl(controls[index]);
    }

    private void FocusActionControl(Control control)
    {
        var controls = GetActionControls();
        var index = controls.IndexOf(control);
        if (index >= 0)
        {
            _activeItemIndex = index;
        }

        control.Focus(NavigationMethod.Directional, KeyModifiers.None);
    }

    private int ResolveCurrentIndex(IReadOnlyList<Control> controls)
    {
        for (var i = 0; i < controls.Count; i++)
        {
            if (controls[i].IsFocused)
            {
                _activeItemIndex = i;
                return i;
            }
        }

        return _activeItemIndex >= 0 && _activeItemIndex < controls.Count
            ? _activeItemIndex
            : -1;
    }

    private List<Control> GetActionControls()
    {
        return this.GetVisualDescendants()
            .OfType<Border>()
            .Where(static border => border.Tag is AppMenuActionItem { IsEnabled: true, IsVisible: true }
                                    && border.Focusable
                                    && border.IsEffectivelyVisible)
            .Cast<Control>()
            .ToList();
    }
}

