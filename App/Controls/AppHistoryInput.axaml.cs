using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public sealed class AppHistoryInputItemEventArgs(object? item) : EventArgs
{
    public object? Item { get; } = item;
}

public sealed class AppHistoryInputEditorCommittedEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public partial class AppHistoryInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppHistoryInput, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AppHistoryInput, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<AppHistoryInput, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppHistoryInput, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<AppHistoryInput, double>(nameof(MaxDropDownHeight), 280d);

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<AppHistoryInput, ICommand?>(nameof(DeleteCommand));

    public AppHistoryInput()
    {
        InitializeComponent();

        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ => UpdateShellState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateShellState());
    }

    public event EventHandler<AppHistoryInputItemEventArgs>? ItemDeleted;

    public event EventHandler<AppHistoryInputItemEventArgs>? SelectionCommitted;

    public event EventHandler<AppHistoryInputEditorCommittedEventArgs>? EditorCommitted;

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

    public ICommand? DeleteCommand
    {
        get => GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    private bool _isEditorFocused;
    private bool _ignoreNextEditorLostFocusCommit;
    private bool _suppressSelectionCommit;

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

            CommitEditorText();
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
        CommitEditorText();
        IsDropDownOpen = false;
    }

    private void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionCommit)
        {
            HistoryListBox.SelectedItem = null;
            _suppressSelectionCommit = false;
            return;
        }

        if (HistoryListBox.SelectedItem is not { } selectedItem)
        {
            return;
        }

        CommitSelection(selectedItem);
        HistoryListBox.SelectedItem = null;
    }

    private void OnDeleteButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressSelectionCommit = true;
        _ignoreNextEditorLostFocusCommit = true;
        e.Handled = true;
    }

    private void OnDeleteButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnDeleteButtonClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: { } item })
        {
            return;
        }

        ItemDeleted?.Invoke(this, new AppHistoryInputItemEventArgs(item));
        if (DeleteCommand?.CanExecute(item) == true)
        {
            DeleteCommand.Execute(item);
        }

        HistoryListBox.SelectedItem = null;
        IsDropDownOpen = true;
    }

    private void TogglePopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsDropDownOpen = !IsDropDownOpen;
    }

    private void OpenPopup()
    {
        if (IsEnabled)
        {
            IsDropDownOpen = true;
        }
    }

    private void CommitSelection(object selectedItem)
    {
        _ignoreNextEditorLostFocusCommit = true;
        SetCurrentValue(TextProperty, BuildItemText(selectedItem));
        IsDropDownOpen = false;
        SelectionCommitted?.Invoke(this, new AppHistoryInputItemEventArgs(selectedItem));
    }

    private void CommitEditorText()
    {
        var text = EditorTextBox.Text ?? string.Empty;
        SetCurrentValue(TextProperty, text);
        EditorCommitted?.Invoke(this, new AppHistoryInputEditorCommittedEventArgs(text));
    }

    private void UpdateShellState()
    {
        ShellBorder.Classes.Set("open", IsDropDownOpen);
        ShellBorder.Classes.Set("focused", _isEditorFocused);
    }

    private static string BuildItemText(object? item)
    {
        return item?.ToString() ?? string.Empty;
    }
}
