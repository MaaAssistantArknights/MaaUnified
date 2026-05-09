using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public partial class AppMultiSelectDropdown : UserControl
{
    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<AppMultiSelectDropdown, string>(nameof(HeaderText), string.Empty);

    public static readonly StyledProperty<object?> DropDownContentProperty =
        AvaloniaProperty.Register<AppMultiSelectDropdown, object?>(nameof(DropDownContent));

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppMultiSelectDropdown, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppMultiSelectDropdown, double>(nameof(MaxDropDownHeight), 280d);

    public AppMultiSelectDropdown()
    {
        InitializeComponent();
        DropDownPopup.PlacementTarget = ShellBorder;

        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(HeaderTextProperty).Subscribe(_ => UpdateHeaderState());
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

        TogglePopup();
        e.Handled = true;
    }

    private void OnToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        TogglePopup();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
    }

    private void TogglePopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsDropDownOpen = !IsDropDownOpen;
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
    }

    private void UpdateHeaderState()
    {
        HeaderTextBlock.Classes.Set("empty", string.IsNullOrWhiteSpace(HeaderText));
    }
}
