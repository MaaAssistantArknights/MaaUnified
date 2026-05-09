using System.Collections;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public sealed class AppSuggestInputSelectionCommittedEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public partial class AppSuggestInput : UserControl
{
    private const string KeyboardCurrentClass = "keyboard-current";
    private const string PointerHighlightClass = "pointer-highlight";
    private const string PointerCurrentClass = "pointer-current";
    private const string SelectionHighlightClass = "selection-highlight";

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppSuggestInput, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AppSuggestInput, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<AppSuggestInput, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppSuggestInput, double>(nameof(MaxDropDownHeight), 280d);

    public static readonly StyledProperty<int> MinimumPrefixLengthProperty =
        AvaloniaProperty.Register<AppSuggestInput, int>(nameof(MinimumPrefixLength));

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppSuggestInput, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    private readonly AvaloniaList<string> _filteredItems = [];
    private bool _isEditorFocused;
    private int _keyboardHighlightedIndex = -1;
    private int _pointerHighlightedIndex = -1;
    private bool _suppressTextRefresh;

    public AppSuggestInput()
    {
        InitializeComponent();
        SuggestionItemsControl.ItemsSource = _filteredItems;
        SetHighlightMode(pointerDriven: true);
        SuggestionItemsControl.AddHandler(InputElement.PointerMovedEvent, OnSuggestionPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        SuggestionItemsControl.AddHandler(InputElement.PointerReleasedEvent, OnSuggestionPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        this.GetObservable(TextProperty).Subscribe(_ => OnTextChanged());
        this.GetObservable(ItemsSourceProperty).Subscribe(_ => RefreshFilteredItems(preserveHighlights: true));
        this.GetObservable(MinimumPrefixLengthProperty).Subscribe(_ => RefreshFilteredItems(preserveHighlights: false));
        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateShellState());
    }

    public event EventHandler<AppSuggestInputSelectionCommittedEventArgs>? SelectionCommitted;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public double MaxDropDownHeight
    {
        get => GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }

    public int MinimumPrefixLength
    {
        get => GetValue(MinimumPrefixLengthProperty);
        set => SetValue(MinimumPrefixLengthProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
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
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
            return;
        }

        OpenPopup();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
        SetHighlightMode(pointerDriven: true);
        ClearPointerHighlight();
        ClearSuggestionSelection();
    }

    private void OnEditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        _isEditorFocused = true;
        UpdateShellState();
        OpenPopup();
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        _isEditorFocused = false;
        UpdateShellState();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsDropDownOpen)
        {
            e.Handled = true;
            IsDropDownOpen = false;
            return;
        }

        if (e.Key == Key.F4)
        {
            e.Handled = true;
            if (IsDropDownOpen)
            {
                IsDropDownOpen = false;
                return;
            }

            OpenPopup();
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            ClearPointerHighlight();
            SetHighlightMode(pointerDriven: false);
            MoveSelection(e.Key == Key.Down ? 1 : -1);
            return;
        }

        if (e.Key == Key.Enter && IsDropDownOpen && ResolveCommittedSuggestion() is { } selectedText)
        {
            e.Handled = true;
            CommitSelection(selectedText);
        }
    }

    private void OnSuggestionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsEnabled || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (ResolveSuggestionIndexAtPointer(e.GetPosition(SuggestionItemsControl)) is not { } index)
        {
            return;
        }

        if (index < 0 || index >= _filteredItems.Count)
        {
            return;
        }

        e.Handled = true;
        CommitSelection(_filteredItems[index]);
    }

    private void OnSuggestionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsEnabled || !IsDropDownOpen)
        {
            return;
        }

        if (ResolveSuggestionIndexAtPointer(e.GetPosition(SuggestionItemsControl)) is not { } index)
        {
            ClearPointerHighlight();
            return;
        }

        if (index < 0 || index >= _filteredItems.Count)
        {
            ClearPointerHighlight();
            return;
        }

        SetHighlightMode(pointerDriven: true);
        SetPointerHighlight(index);
    }

    private void OnTextChanged()
    {
        if (_suppressTextRefresh)
        {
            return;
        }

        RefreshFilteredItems(preserveHighlights: false);
        SetHighlightMode(pointerDriven: true);
        ClearPointerHighlight();
        ClearSuggestionSelection();
        if (!_isEditorFocused)
        {
            return;
        }

        IsDropDownOpen = _filteredItems.Count > 0;
    }

    private void OpenPopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        RefreshFilteredItems(preserveHighlights: false);
        if (_filteredItems.Count == 0)
        {
            ClearPointerHighlight();
            ClearSuggestionSelection();
            IsDropDownOpen = false;
            return;
        }

        SetHighlightMode(pointerDriven: true);
        ClearPointerHighlight();
        IsDropDownOpen = _filteredItems.Count > 0;
    }

    private void CommitSelection(string selectedText)
    {
        _suppressTextRefresh = true;
        try
        {
            SetCurrentValue(TextProperty, selectedText);
        }
        finally
        {
            _suppressTextRefresh = false;
        }

        IsDropDownOpen = false;
        ClearSuggestionSelection();
        SelectionCommitted?.Invoke(this, new AppSuggestInputSelectionCommittedEventArgs(selectedText));
    }

    private void RefreshFilteredItems(bool preserveHighlights)
    {
        var previousPointerText = preserveHighlights ? ResolveHighlightedText(_pointerHighlightedIndex) : null;
        var previousKeyboardText = preserveHighlights ? ResolveHighlightedText(_keyboardHighlightedIndex) : null;
        var nextItems = new List<string>();
        var prefix = Text ?? string.Empty;
        if (prefix.Length >= Math.Max(0, MinimumPrefixLength) && ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                if (item is not string candidate)
                {
                    continue;
                }

                if (prefix.Length == 0 || candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    nextItems.Add(candidate);
                }
            }
        }

        if (_filteredItems.SequenceEqual(nextItems, StringComparer.Ordinal))
        {
            if (preserveHighlights)
            {
                RestoreHighlights(previousPointerText, previousKeyboardText);
            }

            return;
        }

        ClearPointerHighlight();
        ClearSuggestionSelection();
        _filteredItems.Clear();
        foreach (var item in nextItems)
        {
            _filteredItems.Add(item);
        }

        if (preserveHighlights)
        {
            RestoreHighlights(previousPointerText, previousKeyboardText);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_filteredItems.Count == 0)
        {
            IsDropDownOpen = false;
            ClearSuggestionSelection();
            return;
        }

        if (!IsDropDownOpen)
        {
            OpenPopup();
        }

        SetHighlightMode(pointerDriven: false);
        var currentIndex = _keyboardHighlightedIndex;
        var nextIndex = currentIndex < 0
            ? (delta > 0 ? 0 : _filteredItems.Count - 1)
            : Math.Clamp(currentIndex + delta, 0, _filteredItems.Count - 1);

        SetHighlightedIndex(nextIndex, scrollIntoView: true);
        IsDropDownOpen = true;
    }

    private void SetHighlightedIndex(int index, bool scrollIntoView)
    {
        if (index < 0 || index >= _filteredItems.Count)
        {
            ClearSuggestionSelection();
            return;
        }

        SetKeyboardHighlight(index);
        if (scrollIntoView)
        {
            BringContainerIntoView(index);
        }
    }

    private void ClearSuggestionSelection()
    {
        SetKeyboardHighlight(-1);
    }

    private void ClearPointerHighlight()
    {
        SetPointerHighlight(-1);
    }

    private string? ResolveCommittedSuggestion()
    {
        if (_pointerHighlightedIndex >= 0 && _pointerHighlightedIndex < _filteredItems.Count)
        {
            return _filteredItems[_pointerHighlightedIndex];
        }

        if (_keyboardHighlightedIndex >= 0 && _keyboardHighlightedIndex < _filteredItems.Count)
        {
            return _filteredItems[_keyboardHighlightedIndex];
        }

        return null;
    }

    private void SetHighlightMode(bool pointerDriven)
    {
        SuggestionItemsControl.Classes.Set(PointerHighlightClass, pointerDriven);
        SuggestionItemsControl.Classes.Set(SelectionHighlightClass, !pointerDriven);
    }

    private void SetKeyboardHighlight(int index)
    {
        if (_keyboardHighlightedIndex == index)
        {
            return;
        }

        if (_keyboardHighlightedIndex >= 0)
        {
            SetContainerClass(_keyboardHighlightedIndex, KeyboardCurrentClass, false);
        }

        _keyboardHighlightedIndex = index;

        if (_keyboardHighlightedIndex >= 0)
        {
            SetContainerClass(_keyboardHighlightedIndex, KeyboardCurrentClass, true);
        }
    }

    private void SetPointerHighlight(int index)
    {
        if (_pointerHighlightedIndex == index)
        {
            return;
        }

        if (_pointerHighlightedIndex >= 0)
        {
            SetContainerClass(_pointerHighlightedIndex, PointerCurrentClass, false);
        }

        _pointerHighlightedIndex = index;

        if (_pointerHighlightedIndex >= 0)
        {
            SetContainerClass(_pointerHighlightedIndex, PointerCurrentClass, true);
        }
    }

    private void SetContainerClass(int index, string className, bool value)
    {
        if (SuggestionItemsControl.ContainerFromIndex(index) is Control item)
        {
            item.Classes.Set(className, value);
        }
    }

    private int? ResolveSuggestionIndexAtPointer(Point pointerPosition)
    {
        for (var index = 0; index < _filteredItems.Count; index++)
        {
            if (SuggestionItemsControl.ContainerFromIndex(index) is not Control item)
            {
                continue;
            }

            if (!item.IsVisible)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), SuggestionItemsControl);
            if (topLeft is null)
            {
                continue;
            }

            var itemBounds = new Rect(topLeft.Value, item.Bounds.Size);
            if (itemBounds.Contains(pointerPosition))
            {
                return index;
            }
        }

        return null;
    }

    private void BringContainerIntoView(int index)
    {
        if (SuggestionItemsControl.ContainerFromIndex(index) is Control item)
        {
            item.BringIntoView();
        }
    }

    private string? ResolveHighlightedText(int index)
    {
        return index >= 0 && index < _filteredItems.Count
            ? _filteredItems[index]
            : null;
    }

    private void RestoreHighlights(string? pointerText, string? keyboardText)
    {
        var pointerIndex = ResolveFilteredIndex(pointerText);
        var keyboardIndex = ResolveFilteredIndex(keyboardText);
        _pointerHighlightedIndex = -1;
        _keyboardHighlightedIndex = -1;

        Dispatcher.UIThread.Post(() =>
        {
            SetPointerHighlight(pointerIndex);
            SetKeyboardHighlight(keyboardIndex);
        }, DispatcherPriority.Loaded);
    }

    private int ResolveFilteredIndex(string? text)
    {
        if (text is null)
        {
            return -1;
        }

        for (var index = 0; index < _filteredItems.Count; index++)
        {
            if (string.Equals(_filteredItems[index], text, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
        ShellBorder.Classes.Set("focused", _isEditorFocused);
    }
}
