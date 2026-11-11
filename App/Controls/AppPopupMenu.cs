using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public sealed class AppPopupMenu : Popup
{
    private readonly AppPopupMenuPresenter _presenter;

    public static readonly StyledProperty<IEnumerable<AppMenuEntry>?> ItemsProperty =
        AvaloniaProperty.Register<AppPopupMenu, IEnumerable<AppMenuEntry>?>(nameof(Items));

    public AppPopupMenu()
    {
        _presenter = new AppPopupMenuPresenter();
        _presenter.ItemInvoked += OnPresenterItemInvoked;
        _presenter.DismissRequested += OnPresenterDismissRequested;
        Opened += OnOpened;

        Classes.Set("app-popup-menu", true);
        IsLightDismissEnabled = true;
        ShouldUseOverlayLayer = true;
        WindowManagerAddShadowHint = false;
        PopupUiScale.SetUseTopLevelUiScale(this, true);
        Child = _presenter;
    }

    public event EventHandler<AppMenuItemInvokedEventArgs>? ItemInvoked;

    public IEnumerable<AppMenuEntry>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            _presenter.Items = change.GetNewValue<IEnumerable<AppMenuEntry>?>();
        }
    }

    private void OnPresenterItemInvoked(object? sender, AppMenuItemInvokedEventArgs e)
    {
        try
        {
            ItemInvoked?.Invoke(this, e);
        }
        finally
        {
            CloseAndRestoreFocus();
        }
    }

    private void OnPresenterDismissRequested(object? sender, EventArgs e)
    {
        CloseAndRestoreFocus();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsOpen)
                {
                    _presenter.FocusFirstEnabledItem();
                }
            },
            DispatcherPriority.Input);
    }

    private void CloseAndRestoreFocus()
    {
        var owner = PlacementTarget as Control;
        IsOpen = false;
        if (owner is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => owner.Focus(NavigationMethod.Directional, KeyModifiers.None),
            DispatcherPriority.Input);
    }
}
