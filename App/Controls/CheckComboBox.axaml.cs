using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public sealed class CheckComboBoxSelectionCommittedEventArgs(object? selectedItem) : EventArgs
{
    public object? SelectedItem { get; } = selectedItem;
}

public partial class CheckComboBox : UserControl
{
    private const string SettingsInputClassName = "settings-input";

    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<CheckComboBox, string>(nameof(HeaderText), string.Empty);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CheckComboBox, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<CheckComboBox, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<CheckComboBox, bool>(nameof(IsEditable), false);

    public static readonly StyledProperty<bool> UseCompactChevronProperty =
        AvaloniaProperty.Register<CheckComboBox, bool>(nameof(UseCompactChevron), false);

    public static readonly StyledProperty<object?> DropDownContentProperty =
        AvaloniaProperty.Register<CheckComboBox, object?>(nameof(DropDownContent));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<CheckComboBox, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<CheckComboBox, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<object?> SelectedValueProperty =
        AvaloniaProperty.Register<CheckComboBox, object?>(
            nameof(SelectedValue),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBinding?> SelectedValueBindingProperty =
        AvaloniaProperty.Register<CheckComboBox, IBinding?>(nameof(SelectedValueBinding));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<CheckComboBox, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<bool> IsTreeModeProperty =
        AvaloniaProperty.Register<CheckComboBox, bool>(nameof(IsTreeMode), false);

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<CheckComboBox, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<CheckComboBox, double>(nameof(MaxDropDownHeight), 280d);

    public CheckComboBox()
    {
        InitializeComponent();
        DropDownPopup.PlacementTarget = ShellBorder;

        AttachedToVisualTree += (_, _) => SyncVariantClasses();
        DetachedFromVisualTree += (_, _) => SetAutoSettingsInputVariant(false);

        this.GetObservable(IsEditableProperty).Subscribe(_ => UpdateVisualState());
        this.GetObservable(ItemsSourceProperty).Subscribe(_ => UpdateVisualState());
        this.GetObservable(ItemsSourceProperty).Subscribe(_ =>
        {
            SyncSelectedItemFromSelectedValue();
            SyncSelectedItemFromText();
        });
        this.GetObservable(IsTreeModeProperty).Subscribe(_ => UpdateVisualState());
        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(HeaderTextProperty).Subscribe(_ => UpdateHeaderText());
        this.GetObservable(SelectedItemProperty).Subscribe(_ =>
        {
            SyncSelectedValueFromSelectedItem();
            UpdateHeaderText();
        });
        this.GetObservable(SelectedValueProperty).Subscribe(_ => SyncSelectedItemFromSelectedValue());
        this.GetObservable(TextProperty).Subscribe(_ =>
        {
            SyncSelectedItemFromText();
            UpdateHeaderText();
        });
        this.GetObservable(SelectedValueBindingProperty).Subscribe(_ =>
        {
            SyncSelectedValueFromSelectedItem();
            SyncSelectedItemFromSelectedValue();
        });
        SyncVariantClasses();
        UpdateVisualState();
    }

    public event EventHandler<CheckComboBoxSelectionCommittedEventArgs>? SelectionCommitted;

    public event EventHandler<EventArgs>? EditorCommitted;

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

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

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool UseCompactChevron
    {
        get => GetValue(UseCompactChevronProperty);
        set => SetValue(UseCompactChevronProperty, value);
    }

    public object? DropDownContent
    {
        get => GetValue(DropDownContentProperty);
        set => SetValue(DropDownContentProperty, value);
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

    public object? SelectedValue
    {
        get => GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    public IBinding? SelectedValueBinding
    {
        get => GetValue(SelectedValueBindingProperty);
        set => SetValue(SelectedValueBindingProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public bool IsTreeMode
    {
        get => GetValue(IsTreeModeProperty);
        set => SetValue(IsTreeModeProperty, value);
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

    private void UpdateVisualState()
    {
        EditableTextBox.IsVisible = IsEditable;
        HeaderTextBlock.IsVisible = !IsEditable;

        var useTreeMode = IsTreeMode;
        var useItemsSourceMode = !useTreeMode && ItemsSource is not null;
        var useCustomContentMode = !useTreeMode && ItemsSource is null;

        TreeModeView.IsVisible = useTreeMode;
        FlatListBox.IsVisible = useItemsSourceMode;
        CustomContentPresenter.IsVisible = useCustomContentMode;
        UpdateHeaderText();
        UpdateShellState();
    }

    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = e.GetPosition(ShellBorder);
            if (point.X >= ShellBorder.Bounds.Width - ToggleButton.Bounds.Width)
            {
                return;
            }

            if (IsEditable)
            {
                EditableTextBox.Focus();
                e.Handled = true;
                return;
            }

            TogglePopup();
            e.Handled = true;
        }
    }

    private void OnToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        TogglePopup();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
    }

    private void OnFlatListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedItem is null)
        {
            return;
        }

        CommitSelection(SelectedItem);
    }

    private void OnTreeViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TreeModeView.SelectedItem is not { } selectedItem)
        {
            return;
        }

        if (!CanCommitSelection(selectedItem))
        {
            return;
        }

        SelectedItem = selectedItem;
        CommitSelection(selectedItem);
        TreeModeView.SelectedItem = null;
    }

    private void OnEditableTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        _isEditorFocused = false;
        UpdateShellState();
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
        EditorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditableTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        _isEditorFocused = true;
        UpdateShellState();
    }

    private void TogglePopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsDropDownOpen = !IsDropDownOpen;
    }

    private void CommitSelection(object selectedItem)
    {
        if (IsEditable)
        {
            SetCurrentValue(TextProperty, BuildSelectionText(selectedItem));
        }

        IsDropDownOpen = false;
        SelectionCommitted?.Invoke(this, new CheckComboBoxSelectionCommittedEventArgs(selectedItem));
    }

    private bool _syncingSelectionValue;
    private bool _syncingTextSelection;
    private bool _isEditorFocused;
    private bool _autoSettingsInputVariant;

    private void SyncSelectedValueFromSelectedItem()
    {
        if (_syncingSelectionValue)
        {
            return;
        }

        var nextValue = ResolveSelectedValue(SelectedItem);
        if (Equals(SelectedValue, nextValue))
        {
            return;
        }

        _syncingSelectionValue = true;
        try
        {
            SetCurrentValue(SelectedValueProperty, nextValue);
        }
        finally
        {
            _syncingSelectionValue = false;
        }
    }

    private void SyncSelectedItemFromSelectedValue()
    {
        if (_syncingSelectionValue)
        {
            return;
        }

        var matchedItem = FindItemBySelectedValue(SelectedValue);
        if (Equals(SelectedItem, matchedItem))
        {
            return;
        }

        _syncingSelectionValue = true;
        try
        {
            SetCurrentValue(SelectedItemProperty, matchedItem);
        }
        finally
        {
            _syncingSelectionValue = false;
        }
    }

    private void SyncSelectedItemFromText()
    {
        if (_syncingSelectionValue || _syncingTextSelection || !IsEditable || ItemsSource is null)
        {
            return;
        }

        var text = Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (SelectedItem is null)
            {
                return;
            }

            _syncingTextSelection = true;
            try
            {
                SetCurrentValue(SelectedItemProperty, null);
            }
            finally
            {
                _syncingTextSelection = false;
            }

            return;
        }

        object? matchedItem = null;
        foreach (var item in EnumerateItems(ItemsSource))
        {
            if (!string.Equals(BuildSelectionText(item, includePlaceholder: false), text, StringComparison.Ordinal))
            {
                continue;
            }

            matchedItem = item;
            break;
        }

        if (Equals(SelectedItem, matchedItem))
        {
            return;
        }

        _syncingTextSelection = true;
        try
        {
            SetCurrentValue(SelectedItemProperty, matchedItem);
        }
        finally
        {
            _syncingTextSelection = false;
        }
    }

    private object? FindItemBySelectedValue(object? selectedValue)
    {
        if (ItemsSource is null)
        {
            return null;
        }

        foreach (var item in EnumerateItems(ItemsSource))
        {
            if (Equals(ResolveSelectedValue(item), selectedValue))
            {
                return item;
            }
        }

        return null;
    }

    private object? ResolveSelectedValue(object? item)
    {
        if (item is null)
        {
            return null;
        }

        var path = ResolveSelectedValuePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return item;
        }

        return ResolvePathValue(item, path);
    }

    private string? ResolveSelectedValuePath()
    {
        var binding = SelectedValueBinding;
        if (binding is null)
        {
            return null;
        }

        var pathProperty = binding.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
        return pathProperty?.GetValue(binding) as string;
    }

    private static object? ResolvePathValue(object source, string path)
    {
        object? current = source;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is null)
            {
                return null;
            }

            var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
        ShellBorder.Classes.Set("focused", _isEditorFocused);
    }

    private void SyncVariantClasses()
    {
        SetAutoSettingsInputVariant(HasSettingsPageAncestor());
        var useSettingsInputVariant = _autoSettingsInputVariant || Classes.Contains(SettingsInputClassName);

        ShellBorder.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        HeaderTextBlock.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        EditableTextBox.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        DropDownPopup.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        DropDownPanelBorder.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        ToggleButton.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        ToggleIconPath.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        TreeModeView.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        FlatListBox.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
        CustomContentPresenter.Classes.Set(SettingsInputClassName, useSettingsInputVariant);
    }

    private void SetAutoSettingsInputVariant(bool enabled)
    {
        _autoSettingsInputVariant = enabled;
    }

    private bool HasSettingsPageAncestor()
    {
        StyledElement? current = this;
        while (current is not null)
        {
            if (current.Classes.Contains("settings-page"))
            {
                return true;
            }

            current = current.Parent as StyledElement;
        }

        return false;
    }

    private void UpdateHeaderText()
    {
        var headerText = string.IsNullOrWhiteSpace(HeaderText)
            ? BuildSelectionText(SelectedItem, includePlaceholder: false)
            : HeaderText;

        HeaderTextBlock.Text = headerText;
        HeaderTextBlock.Classes.Set("empty", string.IsNullOrWhiteSpace(headerText));
    }

    private string BuildSelectionText(object? item, bool includePlaceholder = true)
    {
        if (item is null)
        {
            return includePlaceholder ? Watermark : string.Empty;
        }

        if (IsTreeMode && TryGetStringProperty(item, "RelativePath", out var relativePath) && !string.IsNullOrWhiteSpace(relativePath))
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

    private static IEnumerable<object?> EnumerateItems(IEnumerable items)
    {
        foreach (var item in items)
        {
            yield return item;

            if (!TryGetChildrenEnumerable(item, out var children))
            {
                continue;
            }

            foreach (var child in EnumerateItems(children))
            {
                yield return child;
            }
        }
    }

    private static bool CanCommitSelection(object? item)
    {
        if (item is null)
        {
            return false;
        }

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

    private static bool TryGetChildrenEnumerable(object? instance, out IEnumerable children)
    {
        children = Array.Empty<object>();
        if (instance is null)
        {
            return false;
        }

        var property = instance.GetType().GetProperty("Children", BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(instance) is not IEnumerable enumerable)
        {
            return false;
        }

        children = enumerable;
        return true;
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
