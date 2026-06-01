using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

public class AppSelectionList : ListBox
{
    public static readonly StyledProperty<AppSelectionListVisualMode> VisualModeProperty =
        AvaloniaProperty.Register<AppSelectionList, AppSelectionListVisualMode>(
            nameof(VisualMode),
            AppSelectionListVisualMode.Surface);

    public static readonly StyledProperty<bool> ReserveTrailingAccessorySpaceProperty =
        AvaloniaProperty.Register<AppSelectionList, bool>(
            nameof(ReserveTrailingAccessorySpace));

    public static readonly StyledProperty<bool> CanReorderItemsProperty =
        AvaloniaProperty.Register<AppSelectionList, bool>(
            nameof(CanReorderItems));

    public static readonly StyledProperty<bool> CanReorderFromComboBoxProperty =
        AvaloniaProperty.Register<AppSelectionList, bool>(
            nameof(CanReorderFromComboBox));

    public static readonly StyledProperty<IDataTemplate?> ReorderDragPreviewContentTemplateProperty =
        AvaloniaProperty.Register<AppSelectionList, IDataTemplate?>(
            nameof(ReorderDragPreviewContentTemplate));

    private const string RailClassName = "selection-list-rail";
    private const string SurfaceClassName = "selection-list-surface";
    private const string NoneClassName = "selection-list-none";
    private const string RailTrailingAccessorySpaceClassName = "selection-list-rail-trailing-accessory-space";
    private const string ReorderEnabledClassName = "selection-list-reorderable";
    private const string DraggingClassName = "dragging";
    private const string ReorderDropIndicatorPartName = "PART_ReorderDropIndicator";
    private const string ReorderDragPreviewPartName = "PART_ReorderDragPreview";
    private const string ScrollViewerPartName = "PART_ScrollViewer";
    private const double ReorderDragStartThreshold = 4d;
    private const double ReorderAutoScrollEdge = 36d;
    private const double ReorderAutoScrollStep = 18d;
    private const double ReorderDropIndicatorHeight = 3d;

    private Point? _dragStart;
    private object? _dragItem;
    private ListBoxItem? _dragItemContainer;
    private IPointer? _dragPointer;
    private Border? _reorderDropIndicator;
    private ContentControl? _reorderDragPreview;
    private ScrollViewer? _scrollViewer;
    private int _dragInsertionIndex = -1;
    private bool _dragInProgress;

    public AppSelectionList()
    {
        UpdateVisualModeClasses(VisualMode);
        UpdateTrailingAccessorySpaceClass(ReserveTrailingAccessorySpace);
        UpdateReorderClass(CanReorderItems);

        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public event EventHandler<AppSelectionListItemReorderEventArgs>? ItemReorderRequested;

    public AppSelectionListVisualMode VisualMode
    {
        get => GetValue(VisualModeProperty);
        set => SetValue(VisualModeProperty, value);
    }

    public bool ReserveTrailingAccessorySpace
    {
        get => GetValue(ReserveTrailingAccessorySpaceProperty);
        set => SetValue(ReserveTrailingAccessorySpaceProperty, value);
    }

    public bool CanReorderItems
    {
        get => GetValue(CanReorderItemsProperty);
        set => SetValue(CanReorderItemsProperty, value);
    }

    public bool CanReorderFromComboBox
    {
        get => GetValue(CanReorderFromComboBoxProperty);
        set => SetValue(CanReorderFromComboBoxProperty, value);
    }

    public IDataTemplate? ReorderDragPreviewContentTemplate
    {
        get => GetValue(ReorderDragPreviewContentTemplateProperty);
        set => SetValue(ReorderDragPreviewContentTemplateProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _reorderDropIndicator = e.NameScope.Find<Border>(ReorderDropIndicatorPartName);
        _reorderDragPreview = e.NameScope.Find<ContentControl>(ReorderDragPreviewPartName);
        _scrollViewer = e.NameScope.Find<ScrollViewer>(ScrollViewerPartName);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == VisualModeProperty)
        {
            UpdateVisualModeClasses(change.GetNewValue<AppSelectionListVisualMode>());
        }
        else if (change.Property == ReserveTrailingAccessorySpaceProperty)
        {
            UpdateTrailingAccessorySpaceClass(change.GetNewValue<bool>());
        }
        else if (change.Property == CanReorderItemsProperty)
        {
            UpdateReorderClass(change.GetNewValue<bool>());
            if (!change.GetNewValue<bool>())
            {
                ResetReorderState();
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetReorderState();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateVisualModeClasses(AppSelectionListVisualMode mode)
    {
        SetClass(RailClassName, mode == AppSelectionListVisualMode.Rail);
        SetClass(SurfaceClassName, mode == AppSelectionListVisualMode.Surface);
        SetClass(NoneClassName, mode == AppSelectionListVisualMode.None);
    }

    private void UpdateTrailingAccessorySpaceClass(bool enabled)
    {
        SetClass(RailTrailingAccessorySpaceClassName, enabled);
    }

    private void UpdateReorderClass(bool enabled)
    {
        SetClass(ReorderEnabledClassName, enabled);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanReorderItems)
        {
            ResetReorderState();
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetReorderState();
            return;
        }

        var container = TryGetItemContainer(e.Source);
        if (container is null || IsInteractiveDragSource(e.Source, container, CanReorderFromComboBox))
        {
            ResetReorderState();
            return;
        }

        var item = container.DataContext;
        var sourceIndex = IndexOfItem(item);
        if (item is null || sourceIndex < 0)
        {
            ResetReorderState();
            return;
        }

        _dragStart = e.GetPosition(container);
        _dragItem = item;
        _dragItemContainer = container;
        _dragInsertionIndex = sourceIndex;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanReorderItems || _dragItem is null || _dragItemContainer is null || _dragStart is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetReorderState();
            return;
        }

        if (_dragInProgress)
        {
            UpdateReorderDrag(e);
            e.Handled = true;
            return;
        }

        var offset = e.GetPosition(_dragItemContainer) - _dragStart.Value;
        if (Math.Abs(offset.X) < ReorderDragStartThreshold && Math.Abs(offset.Y) < ReorderDragStartThreshold)
        {
            return;
        }

        BeginReorderDrag(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragInProgress)
        {
            ResetReorderState();
            return;
        }

        var item = _dragItem;
        var sourceIndex = item is null ? -1 : IndexOfItem(item);
        var insertionIndex = _dragInsertionIndex;
        var shouldMove = item is not null
            && sourceIndex >= 0
            && insertionIndex >= 0
            && IsPointerInsideList(e);

        ResetReorderState();
        e.Handled = true;

        if (!shouldMove || item is null)
        {
            return;
        }

        var targetIndex = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, ItemCount - 1));
        if (targetIndex == sourceIndex)
        {
            return;
        }

        ItemReorderRequested?.Invoke(this, new AppSelectionListItemReorderEventArgs(item, sourceIndex, targetIndex));
    }

    private void BeginReorderDrag(PointerEventArgs e)
    {
        if (_dragItemContainer is null)
        {
            return;
        }

        TryGetAncestor<ComboBox>(e.Source, _dragItemContainer)?.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);

        _dragInProgress = true;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(_dragItemContainer);

        ShowDragPreview();
        _dragItemContainer.Classes.Set(DraggingClassName, true);
        UpdateReorderDrag(e);
    }

    private void UpdateReorderDrag(PointerEventArgs e)
    {
        if (!_dragInProgress)
        {
            return;
        }

        AutoScroll(e);
        UpdateDragPreviewPosition(e);

        if (!TryResolveInsertion(e, out var insertionIndex, out var indicatorLeft, out var indicatorTop, out var indicatorWidth))
        {
            _dragInsertionIndex = -1;
            HideDropIndicator();
            return;
        }

        _dragInsertionIndex = insertionIndex;
        UpdateDropIndicator(indicatorLeft, indicatorTop, indicatorWidth);
    }

    private void ShowDragPreview()
    {
        if (_reorderDragPreview is null || _dragItemContainer is null)
        {
            return;
        }

        _reorderDragPreview.Content = _dragItem;
        _reorderDragPreview.ContentTemplate = ReorderDragPreviewContentTemplate ?? ItemTemplate;
        _reorderDragPreview.Width = _dragItemContainer.Bounds.Width;
        _reorderDragPreview.Height = _dragItemContainer.Bounds.Height;
        _reorderDragPreview.IsVisible = true;
    }

    private void UpdateDragPreviewPosition(PointerEventArgs e)
    {
        if (_reorderDragPreview is null || _dragItemContainer is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var dragStart = _dragStart ?? new Point();
        Canvas.SetLeft(_reorderDragPreview, position.X - dragStart.X);
        Canvas.SetTop(_reorderDragPreview, position.Y - dragStart.Y);
    }

    private bool TryResolveInsertion(
        PointerEventArgs e,
        out int insertionIndex,
        out double indicatorLeft,
        out double indicatorTop,
        out double indicatorWidth)
    {
        insertionIndex = -1;
        indicatorLeft = 0d;
        indicatorTop = 0d;
        indicatorWidth = 0d;

        if (ItemCount == 0)
        {
            return false;
        }

        var rows = GetVisibleItemContainers()
            .Select(container => new
            {
                Container = container,
                Index = IndexOfItem(container.DataContext),
            })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToArray();
        if (rows.Length == 0)
        {
            return false;
        }

        var pointerY = e.GetPosition(this).Y;
        var selectedRow = rows[^1];
        insertionIndex = selectedRow.Index + 1;

        foreach (var row in rows)
        {
            var rowPoint = row.Container.TranslatePoint(new Point(0d, 0d), this);
            if (rowPoint is null)
            {
                continue;
            }

            var centerY = rowPoint.Value.Y + row.Container.Bounds.Height / 2d;
            if (pointerY < centerY)
            {
                selectedRow = row;
                insertionIndex = row.Index;
                break;
            }
        }

        var selectedRowPoint = selectedRow.Container.TranslatePoint(new Point(0d, 0d), this);
        if (selectedRowPoint is null)
        {
            return false;
        }

        indicatorLeft = selectedRowPoint.Value.X;
        indicatorTop = insertionIndex <= selectedRow.Index
            ? selectedRowPoint.Value.Y
            : selectedRowPoint.Value.Y + selectedRow.Container.Bounds.Height;
        indicatorWidth = selectedRow.Container.Bounds.Width;
        insertionIndex = Math.Clamp(insertionIndex, 0, ItemCount);
        return true;
    }

    private IEnumerable<ListBoxItem> GetVisibleItemContainers()
    {
        return this.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Where(item => item.IsEffectivelyVisible && item.Bounds.Width > 0d && item.Bounds.Height > 0d);
    }

    private void AutoScroll(PointerEventArgs e)
    {
        if (_scrollViewer is null || _scrollViewer.Viewport.Height <= 0d)
        {
            return;
        }

        var position = e.GetPosition(_scrollViewer);
        var delta = 0d;
        if (position.Y < ReorderAutoScrollEdge)
        {
            delta = -ReorderAutoScrollStep;
        }
        else if (position.Y > _scrollViewer.Bounds.Height - ReorderAutoScrollEdge)
        {
            delta = ReorderAutoScrollStep;
        }

        if (Math.Abs(delta) < 0.01d)
        {
            return;
        }

        var maxOffset = Math.Max(0d, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        var nextY = Math.Clamp(_scrollViewer.Offset.Y + delta, 0d, maxOffset);
        if (Math.Abs(nextY - _scrollViewer.Offset.Y) > 0.01d)
        {
            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, nextY);
        }
    }

    private void UpdateDropIndicator(double left, double top, double width)
    {
        if (_reorderDropIndicator is null)
        {
            return;
        }

        _reorderDropIndicator.Width = Math.Max(0d, width);
        _reorderDropIndicator.Height = ReorderDropIndicatorHeight;
        _reorderDropIndicator.Margin = new Thickness(left, top - ReorderDropIndicatorHeight / 2d, 0d, 0d);
        _reorderDropIndicator.IsVisible = true;
    }

    private void HideDropIndicator()
    {
        if (_reorderDropIndicator is not null)
        {
            _reorderDropIndicator.IsVisible = false;
        }
    }

    private void HideDragPreview()
    {
        if (_reorderDragPreview is null)
        {
            return;
        }

        _reorderDragPreview.IsVisible = false;
        _reorderDragPreview.Content = null;
        _reorderDragPreview.ContentTemplate = null;
    }

    private bool IsPointerInsideList(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        return position.X >= 0d
            && position.Y >= 0d
            && position.X <= Bounds.Width
            && position.Y <= Bounds.Height;
    }

    private int IndexOfItem(object? item)
    {
        return item is null ? -1 : Items.IndexOf(item);
    }

    private static ListBoxItem? TryGetItemContainer(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                return item;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    private static bool IsInteractiveDragSource(object? source, ListBoxItem container, bool allowComboBoxDrag)
    {
        if (allowComboBoxDrag && TryGetAncestor<ComboBox>(source, container) is not null)
        {
            return false;
        }

        var current = source as Visual;
        while (current is not null)
        {
            if (ReferenceEquals(current, container))
            {
                return false;
            }

            if (current is Button button && button is not ToggleButton)
            {
                return true;
            }

            if (current is ToggleButton toggleButton && toggleButton is not CheckBox)
            {
                return true;
            }

            if (current is TextBox or Slider or ComboBox)
            {
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private static TControl? TryGetAncestor<TControl>(object? source, Control stopAt)
        where TControl : Control
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (ReferenceEquals(current, stopAt))
            {
                return null;
            }

            if (current is TControl control)
            {
                return control;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    private void ResetReorderState()
    {
        if (_dragItemContainer is not null)
        {
            _dragItemContainer.Classes.Set(DraggingClassName, false);
        }

        if (_dragPointer is not null)
        {
            _dragPointer.Capture(null);
        }

        HideDropIndicator();
        HideDragPreview();
        _dragStart = null;
        _dragItem = null;
        _dragItemContainer = null;
        _dragPointer = null;
        _dragInsertionIndex = -1;
        _dragInProgress = false;
    }

    private void SetClass(string className, bool enabled)
    {
        if (enabled)
        {
            if (!Classes.Contains(className))
            {
                Classes.Add(className);
            }

            return;
        }

        Classes.Remove(className);
    }
}

public sealed class AppSelectionListItemReorderEventArgs : EventArgs
{
    public AppSelectionListItemReorderEventArgs(object item, int sourceIndex, int targetIndex)
    {
        Item = item;
        SourceIndex = sourceIndex;
        TargetIndex = targetIndex;
    }

    public object Item { get; }

    public int SourceIndex { get; }

    public int TargetIndex { get; }
}
