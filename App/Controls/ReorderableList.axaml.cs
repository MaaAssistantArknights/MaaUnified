using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public sealed class ReorderableListReorderedEventArgs(object? item, int oldIndex, int newIndex, bool applied) : EventArgs
{
    public object? Item { get; } = item;

    public int OldIndex { get; } = oldIndex;

    public int NewIndex { get; } = newIndex;

    public bool Applied { get; } = applied;
}

public partial class ReorderableList : UserControl
{
    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<ReorderableList, string>(nameof(HeaderText), string.Empty);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ReorderableList, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<ReorderableList, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ReorderableList, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<bool> CanReorderProperty =
        AvaloniaProperty.Register<ReorderableList, bool>(nameof(CanReorder), true);

    public ReorderableList()
    {
        InitializeComponent();
        this.GetObservable(SelectedItemProperty).Subscribe(SyncSelectionFromProperty);
        this.GetObservable(ItemsSourceProperty).Subscribe(_ => UpdateMoveButtonState());
        this.GetObservable(CanReorderProperty).Subscribe(_ => UpdateMoveButtonState());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => UpdateMoveButtonState());
        UpdateMoveButtonState();
    }

    public event EventHandler<ReorderableListReorderedEventArgs>? Reordered;

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
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

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public bool CanReorder
    {
        get => GetValue(CanReorderProperty);
        set => SetValue(CanReorderProperty, value);
    }

    public bool MoveSelectedItemUp() => TryMoveSelectedItem(-1);

    public bool MoveSelectedItemDown() => TryMoveSelectedItem(1);

    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!Equals(SelectedItem, ItemsListBox.SelectedItem))
        {
            SelectedItem = ItemsListBox.SelectedItem;
        }

        UpdateMoveButtonState();
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedItemUp();
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedItemDown();
    }

    private void SyncSelectionFromProperty(object? selectedItem)
    {
        if (ItemsListBox is not null && !Equals(ItemsListBox.SelectedItem, selectedItem))
        {
            ItemsListBox.SelectedItem = selectedItem;
        }

        UpdateMoveButtonState();
    }

    private bool TryMoveSelectedItem(int offset)
    {
        if (offset == 0 || !CanReorder || !IsEnabled || ItemsSource is not IList list || SelectedItem is null)
        {
            return false;
        }

        var oldIndex = IndexOf(list, SelectedItem);
        if (oldIndex < 0)
        {
            return false;
        }

        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= list.Count)
        {
            return false;
        }

        var item = list[oldIndex];
        list.RemoveAt(oldIndex);
        list.Insert(newIndex, item);
        SelectedItem = item;
        Reordered?.Invoke(this, new ReorderableListReorderedEventArgs(item, oldIndex, newIndex, applied: true));
        UpdateMoveButtonState();
        return true;
    }

    private void UpdateMoveButtonState()
    {
        if (MoveUpButton is null || MoveDownButton is null)
        {
            return;
        }

        if (!CanReorder || !IsEnabled || ItemsSource is not IList list || SelectedItem is null)
        {
            MoveUpButton.IsEnabled = false;
            MoveDownButton.IsEnabled = false;
            return;
        }

        var index = IndexOf(list, SelectedItem);
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < list.Count - 1;
    }

    private static int IndexOf(IList list, object selectedItem)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (Equals(list[index], selectedItem))
            {
                return index;
            }
        }

        return -1;
    }
}
