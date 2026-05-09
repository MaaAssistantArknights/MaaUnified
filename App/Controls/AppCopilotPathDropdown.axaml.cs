using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public sealed class AppCopilotPathDropdownSelectionCommittedEventArgs(object? selectedItem) : EventArgs
{
    public object? SelectedItem { get; } = selectedItem;
}

public partial class AppCopilotPathDropdown : UserControl
{
    private const double RowIndentStep = 18d;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppCopilotPathDropdown, double>(nameof(MaxDropDownHeight), 280d);

    private readonly ObservableCollection<VisiblePathRow> _visibleRows = [];
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    private IEnumerable? _subscribedItemsSource;
    private bool _isEditorFocused;
    private bool _ignoreNextEditorLostFocusCommit;

    public AppCopilotPathDropdown()
    {
        InitializeComponent();
        VisibleRowsControl.ItemsSource = _visibleRows;

        this.GetObservable(ItemsSourceProperty).Subscribe(OnItemsSourceChanged);
        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(SelectedItemProperty).Subscribe(_ => RefreshCurrentRow());
        this.GetObservable(TextProperty).Subscribe(_ => RefreshCurrentRow());
        UpdateShellState();
    }

    public event EventHandler<AppCopilotPathDropdownSelectionCommittedEventArgs>? SelectionCommitted;

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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeItemsSource();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(ShellBorder);
        if (point.X >= ShellBorder.Bounds.Width - ToggleButton.Bounds.Width)
        {
            return;
        }

        EditorTextBox.Focus();
    }

    private void OnToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        TogglePopup();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
    }

    private void OnEditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        _isEditorFocused = true;
        UpdateShellState();
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        _isEditorFocused = false;
        UpdateShellState();

        Dispatcher.UIThread.Post(() =>
        {
            if (_ignoreNextEditorLostFocusCommit)
            {
                _ignoreNextEditorLostFocusCommit = false;
                return;
            }

            CommitEditorText(closePopup: false);
        });
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.F4)
        {
            e.Handled = true;
            OpenPopup();
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
        CommitEditorText(closePopup: true);
    }

    private void OnPathRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ignoreNextEditorLostFocusCommit = true;
        }
    }

    private void OnPathRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VisiblePathRow row } || row.Item is null)
        {
            return;
        }

        e.Handled = true;
        if (row.IsFolder)
        {
            ToggleExpanded(row);
            return;
        }

        if (!row.CanSelect)
        {
            return;
        }

        CommitSelection(row);
    }

    private void OnItemsSourceChanged(IEnumerable? nextItemsSource)
    {
        UnsubscribeItemsSource();
        _subscribedItemsSource = nextItemsSource;
        if (_subscribedItemsSource is INotifyCollectionChanged notifyCollectionChanged)
        {
            notifyCollectionChanged.CollectionChanged += OnItemsSourceCollectionChanged;
        }

        RebuildVisibleRows();
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildVisibleRows();
    }

    private void UnsubscribeItemsSource()
    {
        if (_subscribedItemsSource is INotifyCollectionChanged notifyCollectionChanged)
        {
            notifyCollectionChanged.CollectionChanged -= OnItemsSourceCollectionChanged;
        }

        _subscribedItemsSource = null;
    }

    private void TogglePopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (!IsDropDownOpen)
        {
            RebuildVisibleRows();
        }

        IsDropDownOpen = !IsDropDownOpen;
    }

    private void OpenPopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        RebuildVisibleRows();
        IsDropDownOpen = true;
    }

    private void CommitEditorText(bool closePopup)
    {
        SetCurrentValue(TextProperty, EditorTextBox.Text ?? string.Empty);
        if (closePopup)
        {
            IsDropDownOpen = false;
        }

        EditorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void CommitSelection(VisiblePathRow row)
    {
        _ignoreNextEditorLostFocusCommit = true;
        SetCurrentValue(SelectedItemProperty, row.Item);
        SetCurrentValue(TextProperty, BuildSelectionText(row.Item));
        IsDropDownOpen = false;
        RefreshCurrentRow();
        SelectionCommitted?.Invoke(this, new AppCopilotPathDropdownSelectionCommittedEventArgs(row.Item));
    }

    private void ToggleExpanded(VisiblePathRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Key) || !row.HasChildren)
        {
            return;
        }

        if (!_expandedKeys.Add(row.Key))
        {
            _expandedKeys.Remove(row.Key);
        }

        RebuildVisibleRows();
    }

    private void RebuildVisibleRows()
    {
        _visibleRows.Clear();
        if (ItemsSource is not { } itemsSource)
        {
            return;
        }

        foreach (var item in itemsSource)
        {
            AddVisibleRow(item, depth: 0);
        }

        RefreshCurrentRow();
    }

    private void AddVisibleRow(object? item, int depth)
    {
        if (item is null)
        {
            return;
        }

        var key = BuildItemKey(item);
        var isFolder = ReadBoolProperty(item, "IsFolder", fallback: false);
        var children = ReadChildren(item);
        var hasChildren = children.Count > 0;
        var isExpanded = hasChildren && _expandedKeys.Contains(key);
        var canSelect = ReadBoolProperty(item, "CanSelect", fallback: !isFolder);
        _visibleRows.Add(new VisiblePathRow(
            item,
            key,
            BuildDisplayText(item),
            depth,
            isFolder,
            hasChildren,
            isExpanded,
            canSelect));

        if (!isExpanded)
        {
            return;
        }

        foreach (var child in children)
        {
            AddVisibleRow(child, depth + 1);
        }
    }

    private void RefreshCurrentRow()
    {
        var selectedItem = SelectedItem;
        var text = Text ?? string.Empty;
        foreach (var row in _visibleRows)
        {
            row.IsCurrent = ReferenceEquals(row.Item, selectedItem)
                || (!string.IsNullOrWhiteSpace(text)
                    && !row.IsFolder
                    && string.Equals(BuildSelectionText(row.Item), text.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
        ShellBorder.Classes.Set("focused", _isEditorFocused);
    }

    private static string BuildItemKey(object item)
    {
        if (TryGetStringProperty(item, "FullPath", out var fullPath) && !string.IsNullOrWhiteSpace(fullPath))
        {
            return fullPath;
        }

        if (TryGetStringProperty(item, "RelativePath", out var relativePath) && !string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        return item.GetHashCode().ToString("X");
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

    private static string BuildDisplayText(object item)
    {
        if (TryGetStringProperty(item, "DisplayName", out var displayName) && !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        if (TryGetStringProperty(item, "Name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (TryGetStringProperty(item, "RelativePath", out var relativePath) && !string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        return item.ToString() ?? string.Empty;
    }

    private static IReadOnlyList<object> ReadChildren(object item)
    {
        if (!TryGetProperty(item, "Children", out var value) || value is not IEnumerable children)
        {
            return [];
        }

        return children.Cast<object>().ToList();
    }

    private static bool ReadBoolProperty(object item, string propertyName, bool fallback)
    {
        return TryGetProperty(item, propertyName, out var value) && value is bool boolValue
            ? boolValue
            : fallback;
    }

    private static bool TryGetStringProperty(object item, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(item, propertyName, out var propertyValue) || propertyValue is not string stringValue)
        {
            return false;
        }

        value = stringValue;
        return true;
    }

    private static bool TryGetProperty(object item, string propertyName, out object? value)
    {
        value = null;
        var property = item.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return false;
        }

        value = property.GetValue(item);
        return true;
    }

    private sealed class VisiblePathRow : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public VisiblePathRow(
            object item,
            string key,
            string displayName,
            int depth,
            bool isFolder,
            bool hasChildren,
            bool isExpanded,
            bool canSelect)
        {
            Item = item;
            Key = key;
            DisplayName = displayName;
            Indent = Math.Max(0, depth) * RowIndentStep;
            IsFolder = isFolder;
            HasChildren = hasChildren;
            IsExpanded = isExpanded;
            CanSelect = canSelect;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public object Item { get; }

        public string Key { get; }

        public string DisplayName { get; }

        public double Indent { get; }

        public bool IsFolder { get; }

        public bool HasChildren { get; }

        public bool IsExpanded { get; }

        public bool CanSelect { get; }

        public bool ShowCollapsedChevron => IsFolder && HasChildren && !IsExpanded;

        public bool ShowExpandedChevron => IsFolder && HasChildren && IsExpanded;

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }

                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }
}
