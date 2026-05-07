using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public sealed class AppTreeSelectDropdownSelectionCommittedEventArgs(object? selectedItem) : EventArgs
{
    public object? SelectedItem { get; } = selectedItem;
}

public partial class AppTreeSelectDropdown : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppTreeSelectDropdown, double>(nameof(MaxDropDownHeight), 280d);

    private bool _isEditorFocused;

    public AppTreeSelectDropdown()
    {
        InitializeComponent();
        DropDownPopup.PlacementTarget = ShellBorder;

        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateShellState());
        UpdateShellState();
    }

    public event EventHandler<AppTreeSelectDropdownSelectionCommittedEventArgs>? SelectionCommitted;

    public event EventHandler<EventArgs>? EditorCommitted;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
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

        var point = e.GetPosition(ShellBorder);
        if (point.X >= ShellBorder.Bounds.Width - 36)
        {
            return;
        }

        EditableTextBox.Focus();
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

    private void OnTreeViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TreeView.SelectedItem is not { } selectedItem || !CanCommitSelection(selectedItem))
        {
            return;
        }

        SelectedItem = selectedItem;
        Text = BuildSelectionText(selectedItem);
        IsDropDownOpen = false;
        SelectionCommitted?.Invoke(this, new AppTreeSelectDropdownSelectionCommittedEventArgs(selectedItem));
        TreeView.SelectedItem = null;
    }

    private void OnEditableTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        _isEditorFocused = true;
        UpdateShellState();
    }

    private void OnEditableTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        _isEditorFocused = false;
        UpdateShellState();
        Text = EditableTextBox.Text ?? string.Empty;
        EditorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditableTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.F4)
        {
            e.Handled = true;
            IsDropDownOpen = true;
            return;
        }

        if (e.Key == Key.Escape && IsDropDownOpen)
        {
            e.Handled = true;
            IsDropDownOpen = false;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Text = EditableTextBox.Text ?? string.Empty;
        EditorCommitted?.Invoke(this, EventArgs.Empty);
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
        ShellBorder.Classes.Set("focused", _isEditorFocused);
    }

    private static bool CanCommitSelection(object item)
    {
        if (TryReadBoolProperty(item, "CanSelect", out var canSelect))
        {
            return canSelect;
        }

        if (TryReadBoolProperty(item, "IsFolder", out var isFolder))
        {
            return !isFolder;
        }

        return true;
    }

    private static string BuildSelectionText(object item)
    {
        if (TryGetStringProperty(item, "RelativePath", out var relativePath) && !string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        if (TryGetStringProperty(item, "DisplayName", out var displayName) && !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        if (TryGetStringProperty(item, "Name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return item.ToString() ?? string.Empty;
    }

    private static bool TryGetStringProperty(object instance, string propertyName, out string value)
    {
        value = string.Empty;
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(string))
        {
            return false;
        }

        value = (string?)property.GetValue(instance) ?? string.Empty;
        return true;
    }

    private static bool TryReadBoolProperty(object instance, string propertyName, out bool value)
    {
        value = false;
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(bool))
        {
            return false;
        }

        value = (bool)property.GetValue(instance)!;
        return true;
    }
}
