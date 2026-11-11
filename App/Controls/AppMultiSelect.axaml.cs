using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

internal enum AppMultiSelectKeyboardAction
{
    None,
    Open,
    Close,
}

public partial class AppMultiSelect : UserControl
{
    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<AppMultiSelect, string>(nameof(HeaderText), string.Empty);

    public static readonly StyledProperty<object?> DropDownContentProperty =
        AvaloniaProperty.Register<AppMultiSelect, object?>(nameof(DropDownContent));

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppMultiSelect, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppMultiSelect, double>(nameof(MaxDropDownHeight), 280d);

    public AppMultiSelect()
    {
        InitializeComponent();
        DropDownPopup.PlacementTarget = ShellBorder;

        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(HeaderTextProperty).Subscribe(_ => UpdateHeaderState());
        GotFocus += OnControlGotFocus;
        LostFocus += OnControlLostFocus;
        UpdateHeaderState();
        UpdateShellState();
    }

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public object? DropDownContent
    {
        get => GetValue(DropDownContentProperty);
        set => SetValue(DropDownContentProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    public double MaxDropDownHeight
    {
        get => GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }

    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        TogglePopup();
        e.Handled = true;
    }

    private void OnToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        Focus();
        TogglePopup();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleKeyboardInput(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TryHandleKeyboardInput(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
    }

    internal bool TryHandleKeyboardInput(Key key)
    {
        switch (GetKeyboardAction(IsEnabled, IsDropDownOpen, key))
        {
            case AppMultiSelectKeyboardAction.Open:
                OpenPopupAndFocusFirstOption();
                return true;

            case AppMultiSelectKeyboardAction.Close:
                IsDropDownOpen = false;
                Focus();
                return true;

            default:
                return false;
        }
    }

    internal static AppMultiSelectKeyboardAction GetKeyboardAction(bool isEnabled, bool isDropDownOpen, Key key)
    {
        if (!isEnabled)
        {
            return AppMultiSelectKeyboardAction.None;
        }

        if (key is Key.Enter or Key.Space or Key.F4 or Key.Down)
        {
            return AppMultiSelectKeyboardAction.Open;
        }

        return key == Key.Escape && isDropDownOpen
            ? AppMultiSelectKeyboardAction.Close
            : AppMultiSelectKeyboardAction.None;
    }

    private void TogglePopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
            return;
        }

        OpenPopupAndFocusFirstOption();
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
    }

    private void OnControlGotFocus(object? sender, GotFocusEventArgs e)
    {
        ShellBorder.Classes.Set("focused", true);
    }

    private void OnControlLostFocus(object? sender, RoutedEventArgs e)
    {
        ShellBorder.Classes.Set("focused", false);
    }

    private void UpdateHeaderState()
    {
        HeaderTextBlock.Classes.Set("empty", string.IsNullOrWhiteSpace(HeaderText));
    }

    private void OpenPopupAndFocusFirstOption()
    {
        if (IsDropDownOpen)
        {
            return;
        }

        IsDropDownOpen = true;
        Dispatcher.UIThread.Post(FocusFirstDropDownOption, DispatcherPriority.Input);
    }

    private void FocusFirstDropDownOption()
    {
        if (!IsDropDownOpen || DropDownPopup.Child is not Visual popupChild)
        {
            return;
        }

        popupChild
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.Focusable && control.IsEnabled && control.IsVisible)
            ?.Focus();
    }
}
