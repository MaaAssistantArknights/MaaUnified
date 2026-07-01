using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using MAAUnified.App.Controls;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.Root;

public partial class TaskQueueView : UserControl
{
    private const double TaskRowDragStartThreshold = 4d;
    private const double TaskRowAutoScrollEdge = 36d;
    private const double TaskRowAutoScrollStep = 18d;
    private const double TaskSelectionIndicatorHeight = 22d;
    private const double TaskDropIndicatorHeight = 3d;
    private const string TaskRowDraggingClass = "dragging";
    private static readonly Transitions TaskSelectionIndicatorTransitions =
    [
        new DoubleTransition
        {
            Property = TranslateTransform.YProperty,
            Duration = TimeSpan.FromSeconds(0.16),
            Easing = new CubicEaseOut(),
        },
    ];
    private static readonly TimeSpan LogThumbnailPreviewShowDelay = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan LogThumbnailPreviewFadeDuration = TimeSpan.FromSeconds(0.5);

    private readonly Dictionary<Control, LogThumbnailPreviewState> _logThumbnailPreviewStates = [];
    private readonly AppSlidingSegmentController _settingsModeSlider;
    private readonly TranslateTransform _taskSelectionIndicatorTransform = new();
    private TaskQueuePageViewModel? _observedVm;
    private ScrollViewer? _taskListScrollViewer;
    private Point? _taskRowDragStart;
    private TaskQueueItemViewModel? _taskRowDragSource;
    private Control? _taskRowDragSourceControl;
    private IPointer? _taskRowDragPointer;
    private int _taskRowDragSourceIndex = -1;
    private int _taskRowInsertionIndex = -1;
    private bool _taskRowDragInProgress;
    private bool _taskSelectionIndicatorUpdateQueued;
    private bool _taskSelectionIndicatorPendingAnimation;
    private double _taskSelectionIndicatorLeft = double.NaN;
    private double _taskSelectionIndicatorTop = double.NaN;
    private Control? _openTaskQueuePopupOwner;
    private Control? _suppressNextTaskQueuePopupOpenOwner;

    public TaskQueueView()
    {
        InitializeComponent();
        _settingsModeSlider = new AppSlidingSegmentController(
            SettingsModeTrack,
            SettingsModeSelectionSlider,
            () => VM?.IsAdvancedSettingsSelected == true ? AdvancedSettingsModeButton : GeneralSettingsModeButton);
        TaskSelectionIndicator.RenderTransform = _taskSelectionIndicatorTransform;
        TaskListBox.SelectionChanged += OnTaskListSelectionChanged;
        TaskListBox.ContainerPrepared += OnTaskListContainerPrepared;
        TaskListBox.ContainerClearing += OnTaskListContainerClearing;
        TaskListBox.SizeChanged += OnTaskListMetricChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SettingsModeTrack.SizeChanged += OnSettingsModeSliderLayoutMetricChanged;
        GeneralSettingsModeButton.SizeChanged += OnSettingsModeSliderLayoutMetricChanged;
        AdvancedSettingsModeButton.SizeChanged += OnSettingsModeSliderLayoutMetricChanged;
        UpdateVmSubscription();
        QueueTaskSelectionIndicatorUpdate(animate: false);
        QueueSettingsModeSliderSync();
    }

    private TaskQueuePageViewModel? VM => DataContext as TaskQueuePageViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UpdateVmSubscription();
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        RefreshTaskListScrollViewer();
        QueueTaskSelectionIndicatorUpdate(animate: false);
        QueueSettingsModeSliderSync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ResetTaskRowDragState();
        CloseTaskQueueActionPopup();
        CloseLogThumbnailPreviewsImmediately();
        HideTaskSelectionIndicator();
        HideSettingsModeSlider();
        DetachTaskListScrollViewer();
        SetObservedVm(null);
    }

    private async void OnLogThumbnailPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control thumbnail)
        {
            return;
        }

        var state = GetLogThumbnailPreviewState(thumbnail);
        state.PointerOverThumbnail = true;
        state.CloseVersion++;
        var openVersion = ++state.OpenVersion;

        await Task.Delay(LogThumbnailPreviewShowDelay);
        if (openVersion != state.OpenVersion || !state.PointerOverThumbnail)
        {
            return;
        }

        ShowLogThumbnailPreview(thumbnail, state);
    }

    private void OnLogThumbnailPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control thumbnail || !_logThumbnailPreviewStates.TryGetValue(thumbnail, out var state))
        {
            return;
        }

        state.PointerOverThumbnail = false;
        state.OpenVersion++;
        ScheduleLogThumbnailPreviewClose(thumbnail, state);
    }

    private LogThumbnailPreviewState GetLogThumbnailPreviewState(Control thumbnail)
    {
        if (_logThumbnailPreviewStates.TryGetValue(thumbnail, out var state))
        {
            return state;
        }

        state = new LogThumbnailPreviewState();
        _logThumbnailPreviewStates[thumbnail] = state;
        return state;
    }

    private static bool TryResolveLogThumbnailPreview(Control thumbnail, out Popup popup, out Control preview)
    {
        popup = null!;
        preview = null!;

        if (thumbnail.Parent is not Panel panel)
        {
            return false;
        }

        foreach (var child in panel.Children)
        {
            if (child is Popup candidate && candidate.Classes.Contains("task-queue-log-image-popup"))
            {
                popup = candidate;
                preview = candidate.Child as Control ?? candidate;
                return true;
            }
        }

        return false;
    }

    private static void ShowLogThumbnailPreview(Control thumbnail, LogThumbnailPreviewState state)
    {
        if (!TryResolveLogThumbnailPreview(thumbnail, out var popup, out var preview))
        {
            return;
        }

        state.CloseVersion++;
        if (!popup.IsOpen)
        {
            preview.Opacity = 0d;
            popup.IsOpen = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (popup.IsOpen && state.PointerOverThumbnail)
                {
                    preview.Opacity = 1d;
                }
            }, DispatcherPriority.Render);
            return;
        }

        preview.Opacity = 1d;
    }

    private static async void ScheduleLogThumbnailPreviewClose(Control thumbnail, LogThumbnailPreviewState state)
    {
        if (!TryResolveLogThumbnailPreview(thumbnail, out var popup, out var preview) || !popup.IsOpen)
        {
            return;
        }

        preview.Opacity = 0d;
        var closeVersion = ++state.CloseVersion;

        await Task.Delay(LogThumbnailPreviewFadeDuration);
        if (closeVersion == state.CloseVersion && !state.PointerOverThumbnail)
        {
            popup.IsOpen = false;
        }
    }

    private void CloseLogThumbnailPreviewsImmediately()
    {
        foreach (var (thumbnail, state) in _logThumbnailPreviewStates)
        {
            state.PointerOverThumbnail = false;
            state.OpenVersion++;
            state.CloseVersion++;

            if (!TryResolveLogThumbnailPreview(thumbnail, out var popup, out var preview))
            {
                continue;
            }

            preview.Opacity = 0d;
            popup.IsOpen = false;
        }

        _logThumbnailPreviewStates.Clear();
    }

    private void UpdateVmSubscription()
    {
        SetObservedVm(VM);
    }

    private void SetObservedVm(TaskQueuePageViewModel? nextVm)
    {
        if (ReferenceEquals(_observedVm, nextVm))
        {
            return;
        }

        if (_observedVm is not null)
        {
            _observedVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _observedVm = nextVm;
        if (_observedVm is not null)
        {
            _observedVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var shouldUpdateSelectionIndicator = string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.SelectedTask), StringComparison.Ordinal);
        var shouldUpdateSettingsModeSlider = string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.IsGeneralSettingsSelected), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.IsAdvancedSettingsSelected), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.ShowSettingsModeSwitch), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.CanUseAdvancedSettings), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.GeneralSettingsButtonText), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.AdvancedSettingsButtonText), StringComparison.Ordinal);

        if (shouldUpdateSelectionIndicator)
        {
            QueueTaskSelectionIndicatorUpdate(animate: string.Equals(
                e.PropertyName,
                nameof(TaskQueuePageViewModel.SelectedTask),
                StringComparison.Ordinal));
        }

        if (shouldUpdateSettingsModeSlider)
        {
            QueueSettingsModeSliderSync();
        }
    }

    private void OnSettingsModeSliderLayoutMetricChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueSettingsModeSliderSync(resetMetrics: false);
    }

    private void OnTaskListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        QueueTaskSelectionIndicatorUpdate(animate: true);
    }

    private void OnTaskListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        RefreshTaskListScrollViewer();
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void OnTaskListContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void OnTaskListMetricChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void OnTaskListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void OnOpenButtonContextMenuClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        if (ReferenceEquals(_suppressNextTaskQueuePopupOpenOwner, control))
        {
            _suppressNextTaskQueuePopupOpenOwner = null;
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(_openTaskQueuePopupOwner, control) && TaskQueueActionPopup.IsOpen)
        {
            CloseTaskQueueActionPopup();
            e.Handled = true;
            return;
        }

        OpenTaskQueueActionPopup(control, BuildAddTaskMenuItems(), PlacementMode.BottomEdgeAlignedLeft);
        e.Handled = true;
    }

    private void OnTaskGearPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (VM is null || sender is not Control { DataContext: TaskQueueItemViewModel task })
        {
            return;
        }

        VM.SelectedTask = task;
        e.Handled = true;
    }

    private async void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.SelectAllAsync(true);
        }
    }

    private async void OnBatchActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && ReferenceEquals(_suppressNextTaskQueuePopupOpenOwner, control))
        {
            _suppressNextTaskQueuePopupOpenOwner = null;
            e.Handled = true;
            return;
        }

        if (VM is not null)
        {
            await VM.ExecuteBatchActionAsync();
        }
    }

    private void OnBatchActionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsRightButtonPressed || !VM.ShowBatchModeToggle)
        {
            return;
        }

        OpenTaskQueueActionPopup(control, BuildBatchMenuItems(), PlacementMode.Pointer);
        e.Handled = true;
    }

    private async void OnTaskQueueActionPopupItemInvoked(object? sender, AppMenuItemInvokedEventArgs e)
    {
        if (VM is null || e.Parameter is not TaskQueuePopupMenuItem item || !item.IsEnabled)
        {
            return;
        }

        switch (item.Action)
        {
            case TaskQueuePopupAction.AddTask:
                if (!string.IsNullOrWhiteSpace(item.TaskType))
                {
                    await VM.AddTaskAsync(item.TaskType);
                }
                break;
            case TaskQueuePopupAction.ToggleBatchMode:
                await VM.ToggleSelectionBatchModeAsync();
                break;
            case TaskQueuePopupAction.RunTaskOnce:
                await ExecuteTaskItemActionAsync(item.Task, static vm => vm.RunSelectedTaskOnceAsync());
                break;
            case TaskQueuePopupAction.MoveTaskUp:
                await ExecuteTaskItemActionAsync(item.Task, static vm => vm.MoveSelectedTaskAsync(-1));
                break;
            case TaskQueuePopupAction.MoveTaskDown:
                await ExecuteTaskItemActionAsync(item.Task, static vm => vm.MoveSelectedTaskAsync(1));
                break;
            case TaskQueuePopupAction.RenameTask:
                await ExecuteTaskItemActionAsync(item.Task, static vm => vm.RenameSelectedTaskWithDialogAsync());
                break;
            case TaskQueuePopupAction.RemoveTask:
                await ExecuteTaskItemActionAsync(item.Task, static vm => vm.RemoveSelectedTaskAsync());
                break;
        }
    }

    private async Task ExecuteTaskItemActionAsync(
        TaskQueueItemViewModel? task,
        Func<TaskQueuePageViewModel, Task> action)
    {
        if (VM is null || task is null)
        {
            return;
        }

        VM.SelectedTask = task;
        await action(VM);
    }

    private IReadOnlyList<TaskQueuePopupMenuItem> BuildAddTaskMenuItems()
    {
        if (VM is null)
        {
            return [];
        }

        return
        [
            new(VM.AddTaskMenuStartUpText, TaskQueuePopupAction.AddTask, TaskType: "StartUp"),
            new(VM.AddTaskMenuFightText, TaskQueuePopupAction.AddTask, TaskType: "Fight"),
            new(VM.AddTaskMenuInfrastText, TaskQueuePopupAction.AddTask, TaskType: "Infrast"),
            new(VM.AddTaskMenuRecruitText, TaskQueuePopupAction.AddTask, TaskType: "Recruit"),
            new(VM.AddTaskMenuMallText, TaskQueuePopupAction.AddTask, TaskType: "Mall"),
            new(VM.AddTaskMenuAwardText, TaskQueuePopupAction.AddTask, TaskType: "Award"),
            new(VM.AddTaskMenuRoguelikeText, TaskQueuePopupAction.AddTask, TaskType: "Roguelike"),
            new(VM.AddTaskMenuReclamationText, TaskQueuePopupAction.AddTask, TaskType: "Reclamation"),
            new(VM.AddTaskMenuUserDataUpdateText, TaskQueuePopupAction.AddTask, TaskType: "UserDataUpdate"),
            new(VM.AddTaskMenuSingleStepText, TaskQueuePopupAction.AddTask, TaskType: "SingleStep"),
            new(VM.AddTaskMenuCustomText, TaskQueuePopupAction.AddTask, TaskType: "Custom"),
        ];
    }

    private IReadOnlyList<TaskQueuePopupMenuItem> BuildBatchMenuItems()
    {
        if (VM is null)
        {
            return [];
        }

        return
        [
            new(VM.BatchToggleMenuText, TaskQueuePopupAction.ToggleBatchMode, IsVisible: VM.ShowBatchModeToggle),
        ];
    }

    private IReadOnlyList<TaskQueuePopupMenuItem> BuildTaskMenuItems(TaskQueueItemViewModel task)
    {
        if (VM is null)
        {
            return [];
        }

        return
        [
            new(VM.TaskMenuRunOnceText, TaskQueuePopupAction.RunTaskOnce, Task: task, IsEnabled: VM.CanEdit),
            new(VM.TaskMenuMoveUpText, TaskQueuePopupAction.MoveTaskUp, Task: task, IsEnabled: VM.CanEdit),
            new(VM.TaskMenuMoveDownText, TaskQueuePopupAction.MoveTaskDown, Task: task, IsEnabled: VM.CanEdit),
            new(VM.TaskMenuRenameText, TaskQueuePopupAction.RenameTask, Task: task, IsEnabled: VM.CanEdit),
            new(VM.TaskMenuDeleteText, TaskQueuePopupAction.RemoveTask, Task: task, IsEnabled: VM.CanEdit),
        ];
    }

    private void OpenTaskQueueActionPopup(
        Control owner,
        IReadOnlyList<TaskQueuePopupMenuItem> items,
        PlacementMode placement)
    {
        CloseTaskQueueActionPopup();

        TaskQueueActionPopup.Items = items
            .Where(static item => item.IsVisible)
            .Select(static item => item.ToMenuItem())
            .ToArray();
        TaskQueueActionPopup.PlacementTarget = owner;
        TaskQueueActionPopup.Placement = placement;
        TaskQueueActionPopup.VerticalOffset = placement == PlacementMode.Pointer ? 0d : 4d;
        TaskQueueActionPopup.IsOpen = true;
        _openTaskQueuePopupOwner = owner;
    }

    private void CloseTaskQueueActionPopup()
    {
        if (TaskQueueActionPopup.IsOpen)
        {
            TaskQueueActionPopup.IsOpen = false;
        }

        TaskQueueActionPopup.Items = null;
        _openTaskQueuePopupOwner = null;
    }

    private void OnTaskQueueActionPopupClosed(object? sender, EventArgs e)
    {
        var closedOwner = _openTaskQueuePopupOwner;
        TaskQueueActionPopup.Items = null;
        _openTaskQueuePopupOwner = null;

        if (closedOwner is not null)
        {
            _suppressNextTaskQueuePopupOpenOwner = closedOwner;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_suppressNextTaskQueuePopupOpenOwner, closedOwner))
                {
                    _suppressNextTaskQueuePopupOpenOwner = null;
                }
            }, DispatcherPriority.Background);
        }
    }

    private async void OnToggleRunClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.ToggleRunAsync();
        }
    }

    private void OnRunButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        VM?.SetRunButtonHover(true);
    }

    private void OnRunButtonPointerExited(object? sender, PointerEventArgs e)
    {
        VM?.SetRunButtonHover(false);
    }

    private async void OnWaitAndStopClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.WaitAndStopAsync();
        }
    }

    private void OnSelectGeneralSettingsClick(object? sender, RoutedEventArgs e)
    {
        VM?.SelectGeneralSettingsMode();
        QueueSettingsModeSliderSync(animate: true);
    }

    private void OnSelectAdvancedSettingsClick(object? sender, RoutedEventArgs e)
    {
        VM?.SelectAdvancedSettingsMode();
        QueueSettingsModeSliderSync(animate: true);
    }

    private void QueueSettingsModeSliderSync(bool animate = false, bool resetMetrics = true)
    {
        _settingsModeSlider.QueueSync(animate, resetMetrics);
    }

    private void HideSettingsModeSlider()
    {
        _settingsModeSlider.Hide();
    }

    private void OnOpenPostActionClick(object? sender, RoutedEventArgs e)
    {
        VM?.OpenPostActionPanel();
    }

    private async void OnToggleOverlayClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetShellViewModel(out var shell))
        {
            await shell.ToggleOverlayFromTaskQueueAsync();
            return;
        }

        if (VM is not null)
        {
            await VM.ToggleOverlayAsync();
        }
    }

    private async void OnOverlayButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (TryGetShellViewModel(out var shell))
        {
            await shell.PickOverlayTargetFromTaskQueueAsync();
            return;
        }

        await VM.PickOverlayTargetWithDialogAsync();
    }

    private void OnTaskRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not TaskQueueItemViewModel source)
        {
            ResetTaskRowDragState();
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsRightButtonPressed)
        {
            ResetTaskRowDragState();
            if (VM is not null)
            {
                VM.SelectedTask = source;
                if (!VM.CanEdit)
                {
                    e.Handled = true;
                    return;
                }
            }

            OpenTaskRowContextMenu(control);
            e.Handled = true;
            return;
        }

        if (VM is not null && !VM.CanEdit)
        {
            ResetTaskRowDragState();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetTaskRowDragState();
            return;
        }

        if (IsInteractiveTaskRowDragSource(e.Source, control))
        {
            ResetTaskRowDragState();
            return;
        }

        _taskRowDragSource = source;
        _taskRowDragSourceControl = control;
        _taskRowDragStart = e.GetPosition(control);
    }

    private void OnTaskRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (VM is null
            || !VM.CanEdit
            || _taskRowDragSource is null
            || _taskRowDragStart is null
            || sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetTaskRowDragState();
            return;
        }

        if (_taskRowDragInProgress)
        {
            UpdateTaskRowDrag(e);
            e.Handled = true;
            return;
        }

        var offset = e.GetPosition(control) - _taskRowDragStart.Value;
        if (Math.Abs(offset.X) < TaskRowDragStartThreshold && Math.Abs(offset.Y) < TaskRowDragStartThreshold)
        {
            return;
        }

        var sourceIndex = VM.Tasks.IndexOf(_taskRowDragSource);
        if (sourceIndex < 0)
        {
            ResetTaskRowDragState();
            return;
        }

        BeginTaskRowDrag(control, sourceIndex, e);
        e.Handled = true;
    }

    private async void OnTaskRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_taskRowDragInProgress || VM is null)
        {
            ResetTaskRowDragState();
            return;
        }

        var source = _taskRowDragSource;
        var sourceIndex = _taskRowDragSourceIndex;
        var insertionIndex = _taskRowInsertionIndex;
        var shouldMove = source is not null
            && VM.CanEdit
            && IsPointerInsideTaskList(e)
            && sourceIndex >= 0
            && sourceIndex < VM.Tasks.Count
            && insertionIndex >= 0;

        ResetTaskRowDragState();
        e.Handled = true;

        if (!shouldMove || source is null)
        {
            return;
        }

        var finalIndex = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
        finalIndex = Math.Clamp(finalIndex, 0, VM.Tasks.Count - 1);
        if (finalIndex == sourceIndex)
        {
            return;
        }

        VM.SelectedTask = source;
        await VM.MoveSelectedTaskToAsync(finalIndex);
    }

    private void OnTaskRowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTaskRowDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private void BeginTaskRowDrag(Control sourceControl, int sourceIndex, PointerEventArgs e)
    {
        _taskRowDragInProgress = true;
        _taskRowDragSourceControl = sourceControl;
        _taskRowDragSourceIndex = sourceIndex;
        _taskRowInsertionIndex = sourceIndex;

        sourceControl.Classes.Set(TaskRowDraggingClass, true);
        sourceControl.PointerReleased += OnTaskRowPointerReleased;
        _taskRowDragPointer = e.Pointer;
        e.Pointer.Capture(sourceControl);

        HideTaskSelectionIndicator();
        ShowTaskDragPreview();
        UpdateTaskRowDrag(e);
    }

    private void UpdateTaskRowDrag(PointerEventArgs e)
    {
        if (!_taskRowDragInProgress)
        {
            return;
        }

        AutoScrollTaskList(e);
        UpdateTaskDragPreview(e);

        if (!TryResolveTaskInsertion(e, out var insertionIndex, out var indicatorLeft, out var indicatorTop, out var indicatorWidth))
        {
            HideTaskDropIndicator();
            _taskRowInsertionIndex = -1;
            return;
        }

        _taskRowInsertionIndex = insertionIndex;
        UpdateTaskDropIndicator(indicatorLeft, indicatorTop, indicatorWidth);
    }

    private bool TryResolveTaskInsertion(
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

        var listBox = GetTaskListBox();
        var indicator = GetTaskDropIndicator();
        var indicatorParent = indicator?.Parent as Visual ?? listBox;
        if (VM is null || listBox is null || indicatorParent is null)
        {
            return false;
        }

        var rows = GetVisibleTaskRows(listBox)
            .Select(row => new
            {
                Row = row,
                Index = row.DataContext is TaskQueueItemViewModel task ? VM.Tasks.IndexOf(task) : -1,
            })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToArray();

        if (rows.Length == 0)
        {
            return false;
        }

        var pointerY = e.GetPosition(listBox).Y;
        var selectedRow = rows[^1];
        insertionIndex = selectedRow.Index + 1;

        foreach (var item in rows)
        {
            var rowPointInList = item.Row.TranslatePoint(new Point(0d, 0d), listBox);
            if (rowPointInList is null)
            {
                continue;
            }

            var centerY = rowPointInList.Value.Y + item.Row.Bounds.Height / 2d;
            if (pointerY < centerY)
            {
                selectedRow = item;
                insertionIndex = item.Index;
                break;
            }
        }

        var rowPoint = selectedRow.Row.TranslatePoint(new Point(0d, 0d), indicatorParent);
        if (rowPoint is null)
        {
            return false;
        }

        var rowBottom = rowPoint.Value.Y + selectedRow.Row.Bounds.Height;
        indicatorLeft = rowPoint.Value.X;
        indicatorTop = insertionIndex <= selectedRow.Index ? rowPoint.Value.Y : rowBottom;
        indicatorWidth = selectedRow.Row.Bounds.Width;
        insertionIndex = Math.Clamp(insertionIndex, 0, VM.Tasks.Count);
        return true;
    }

    private static IEnumerable<Control> GetVisibleTaskRows(ListBox listBox)
    {
        return listBox.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Classes.Contains("task-queue-item-card")
                && control.DataContext is TaskQueueItemViewModel
                && control.IsEffectivelyVisible
                && control.Bounds.Width > 0d
                && control.Bounds.Height > 0d);
    }

    private void AutoScrollTaskList(PointerEventArgs e)
    {
        var scrollViewer = GetTaskListScrollViewer();
        if (scrollViewer is null || scrollViewer.Viewport.Height <= 0d)
        {
            return;
        }

        var position = e.GetPosition(scrollViewer);
        var delta = 0d;
        if (position.Y < TaskRowAutoScrollEdge)
        {
            delta = -TaskRowAutoScrollStep;
        }
        else if (position.Y > scrollViewer.Bounds.Height - TaskRowAutoScrollEdge)
        {
            delta = TaskRowAutoScrollStep;
        }

        if (Math.Abs(delta) < 0.01d)
        {
            return;
        }

        var maxOffset = Math.Max(0d, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextY = Math.Clamp(scrollViewer.Offset.Y + delta, 0d, maxOffset);
        if (Math.Abs(nextY - scrollViewer.Offset.Y) > 0.01d)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextY);
        }
    }

    private bool IsPointerInsideTaskList(PointerEventArgs e)
    {
        var listBox = GetTaskListBox();
        if (listBox is null)
        {
            return false;
        }

        var position = e.GetPosition(listBox);
        return position.X >= 0d
            && position.Y >= 0d
            && position.X <= listBox.Bounds.Width
            && position.Y <= listBox.Bounds.Height;
    }

    private void ShowTaskDragPreview()
    {
        var preview = GetTaskDragPreview();
        if (preview is null)
        {
            return;
        }

        preview.DataContext = _taskRowDragSource;
        preview.Width = _taskRowDragSourceControl?.Bounds.Width ?? preview.Width;
        preview.Height = _taskRowDragSourceControl?.Bounds.Height ?? preview.Height;
        preview.IsVisible = true;
    }

    private void UpdateTaskDragPreview(PointerEventArgs e)
    {
        var preview = GetTaskDragPreview();
        if (preview?.Parent is not Visual parent)
        {
            return;
        }

        var position = e.GetPosition(parent);
        var dragStart = _taskRowDragStart ?? new Point(0d, 0d);
        Canvas.SetLeft(preview, position.X - dragStart.X);
        Canvas.SetTop(preview, position.Y - dragStart.Y);
    }

    private void UpdateTaskDropIndicator(double left, double top, double width)
    {
        var indicator = GetTaskDropIndicator();
        if (indicator is null)
        {
            return;
        }

        indicator.Width = Math.Max(0d, width);
        indicator.Height = TaskDropIndicatorHeight;
        indicator.IsVisible = true;
        indicator.Margin = new Thickness(left, top - TaskDropIndicatorHeight / 2d, 0d, 0d);
    }

    private void HideTaskDropIndicator()
    {
        var indicator = GetTaskDropIndicator();
        if (indicator is not null)
        {
            indicator.IsVisible = false;
        }
    }

    private void HideTaskDragPreview()
    {
        var preview = GetTaskDragPreview();
        if (preview is not null)
        {
            preview.IsVisible = false;
            preview.DataContext = null;
        }
    }

    private ListBox? GetTaskListBox()
    {
        return this.FindControl<ListBox>("TaskListBox") ?? _taskRowDragSourceControl?.FindAncestorOfType<ListBox>();
    }

    private ScrollViewer? GetTaskListScrollViewer()
    {
        var listBox = GetTaskListBox();
        return listBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void RefreshTaskListScrollViewer()
    {
        var next = GetTaskListScrollViewer();
        if (ReferenceEquals(_taskListScrollViewer, next))
        {
            return;
        }

        DetachTaskListScrollViewer();
        _taskListScrollViewer = next;
        if (_taskListScrollViewer is not null)
        {
            _taskListScrollViewer.ScrollChanged += OnTaskListScrollChanged;
        }
    }

    private void DetachTaskListScrollViewer()
    {
        if (_taskListScrollViewer is null)
        {
            return;
        }

        _taskListScrollViewer.ScrollChanged -= OnTaskListScrollChanged;
        _taskListScrollViewer = null;
    }

    private Border? GetTaskDropIndicator()
    {
        return this.FindControl<Border>("TaskDropIndicator");
    }

    private Border? GetTaskSelectionIndicator()
    {
        return this.FindControl<Border>("TaskSelectionIndicator");
    }

    private Border? GetTaskDragPreview()
    {
        return this.FindControl<Border>("TaskDragPreview");
    }

    private static bool IsInteractiveTaskRowDragSource(object? source, Control rowControl)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        var current = visual;
        while (current is not null)
        {
            if (ReferenceEquals(current, rowControl))
            {
                return false;
            }

            if (current is Button or CheckBox)
            {
                return true;
            }

            if (current is Control control && control.Classes.Contains("task-queue-row-action"))
            {
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private void ResetTaskRowDragState()
    {
        if (_taskRowDragSourceControl is not null)
        {
            _taskRowDragSourceControl.Classes.Set(TaskRowDraggingClass, false);
            _taskRowDragSourceControl.PointerReleased -= OnTaskRowPointerReleased;
        }

        HideTaskDropIndicator();
        HideTaskDragPreview();
        _taskRowDragPointer?.Capture(null);
        _taskRowDragStart = null;
        _taskRowDragSource = null;
        _taskRowDragSourceControl = null;
        _taskRowDragPointer = null;
        _taskRowDragSourceIndex = -1;
        _taskRowInsertionIndex = -1;
        _taskRowDragInProgress = false;
        QueueTaskSelectionIndicatorUpdate(animate: false);
    }

    private void QueueTaskSelectionIndicatorUpdate(bool animate)
    {
        _taskSelectionIndicatorPendingAnimation |= animate;
        if (_taskSelectionIndicatorUpdateQueued)
        {
            return;
        }

        _taskSelectionIndicatorUpdateQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _taskSelectionIndicatorUpdateQueued = false;
                var shouldAnimate = _taskSelectionIndicatorPendingAnimation;
                _taskSelectionIndicatorPendingAnimation = false;
                UpdateTaskSelectionIndicator(shouldAnimate);
            },
            DispatcherPriority.Loaded);
    }

    private void UpdateTaskSelectionIndicator(bool animate)
    {
        if (_taskRowDragInProgress)
        {
            HideTaskSelectionIndicator();
            return;
        }

        var indicator = GetTaskSelectionIndicator();
        var listBox = GetTaskListBox();
        var selectedTask = VM?.SelectedTask;
        var indicatorParent = indicator?.Parent as Visual ?? listBox;
        if (indicator is null || listBox is null || selectedTask is null || indicatorParent is null)
        {
            HideTaskSelectionIndicator();
            return;
        }

        var selectedRow = GetTaskRowForItem(listBox, selectedTask);
        if (selectedRow is null)
        {
            HideTaskSelectionIndicator();
            return;
        }

        var rowPoint = selectedRow.TranslatePoint(new Point(0d, 0d), indicatorParent);
        if (rowPoint is null)
        {
            HideTaskSelectionIndicator();
            return;
        }

        var leftInset = selectedRow is Border border ? border.Padding.Left : 0d;
        var top = rowPoint.Value.Y + Math.Max(0d, selectedRow.Bounds.Height - TaskSelectionIndicatorHeight) / 2d;
        var left = rowPoint.Value.X + leftInset;

        indicator.Width = 3d;
        indicator.Height = TaskSelectionIndicatorHeight;
        indicator.Opacity = 1d;
        Canvas.SetTop(indicator, 0d);

        if (double.IsNaN(_taskSelectionIndicatorLeft) || Math.Abs(_taskSelectionIndicatorLeft - left) > 0.1d)
        {
            Canvas.SetLeft(indicator, left);
            _taskSelectionIndicatorLeft = left;
        }

        var shouldAnimate = animate && indicator.IsVisible;
        _taskSelectionIndicatorTransform.Transitions = shouldAnimate ? TaskSelectionIndicatorTransitions : null;
        if (double.IsNaN(_taskSelectionIndicatorTop) || Math.Abs(_taskSelectionIndicatorTop - top) > 0.1d)
        {
            _taskSelectionIndicatorTransform.Y = top;
            _taskSelectionIndicatorTop = top;
        }

        indicator.IsVisible = true;
    }

    private void HideTaskSelectionIndicator()
    {
        var indicator = GetTaskSelectionIndicator();
        if (indicator is not null)
        {
            indicator.IsVisible = false;
        }

        _taskSelectionIndicatorTransform.Transitions = null;
        _taskSelectionIndicatorLeft = double.NaN;
        _taskSelectionIndicatorTop = double.NaN;
    }

    private static Control? GetTaskRowForItem(ListBox listBox, TaskQueueItemViewModel selectedTask)
    {
        if (listBox.ContainerFromItem(selectedTask) is Control container)
        {
            var row = ResolveTaskRow(container, selectedTask);
            if (row is not null)
            {
                return row;
            }
        }

        return GetVisibleTaskRows(listBox)
            .FirstOrDefault(row => ReferenceEquals(row.DataContext, selectedTask));
    }

    private static Control? ResolveTaskRow(Control root, TaskQueueItemViewModel selectedTask)
    {
        if (IsTaskRow(root, selectedTask))
        {
            return root;
        }

        return root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(row => IsTaskRow(row, selectedTask));
    }

    private static bool IsTaskRow(Control control, TaskQueueItemViewModel selectedTask)
    {
        return control.Classes.Contains("task-queue-item-card")
            && ReferenceEquals(control.DataContext, selectedTask)
            && control.IsEffectivelyVisible
            && control.Bounds.Width > 0d
            && control.Bounds.Height > 0d;
    }

    private void OpenTaskRowContextMenu(Control rowControl)
    {
        if (VM is null || !VM.CanEdit || rowControl.Tag is not TaskQueueItemViewModel task)
        {
            return;
        }

        OpenTaskQueueActionPopup(rowControl, BuildTaskMenuItems(task), PlacementMode.Pointer);
    }

    private bool TryGetShellViewModel(out MainShellViewModel shell)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainShellViewModel currentShell)
        {
            shell = currentShell;
            return true;
        }

        shell = null!;
        return false;
    }

    private sealed class LogThumbnailPreviewState
    {
        public bool PointerOverThumbnail { get; set; }

        public int OpenVersion { get; set; }

        public int CloseVersion { get; set; }
    }

    private sealed record TaskQueuePopupMenuItem(
        string Header,
        TaskQueuePopupAction Action,
        TaskQueueItemViewModel? Task = null,
        string? TaskType = null,
        bool IsEnabled = true,
        bool IsVisible = true)
    {
        public AppMenuActionItem ToMenuItem()
        {
            return new AppMenuActionItem(Header, Action, this, IsEnabled, IsVisible);
        }
    }

    private enum TaskQueuePopupAction
    {
        AddTask,
        ToggleBatchMode,
        RunTaskOnce,
        MoveTaskUp,
        MoveTaskDown,
        RenameTask,
        RemoveTask,
    }
}
