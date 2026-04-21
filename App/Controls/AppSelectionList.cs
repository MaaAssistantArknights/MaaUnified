using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

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

    private const string AccentHostPartName = "PART_AccentHost";
    private const string FollowAccentPartName = "PART_FollowAccent";
    private const string FollowAccentGlowPartName = "PART_FollowAccentGlow";
    private const string ScrollViewerPartName = "PART_ScrollViewer";
    private const string RailClassName = "selection-list-rail";
    private const string SurfaceClassName = "selection-list-surface";
    private const string NoneClassName = "selection-list-none";
    private const string RailTrailingAccessorySpaceClassName = "selection-list-rail-trailing-accessory-space";
    private const double FollowAccentMinHeight = 20d;
    private const double FollowAccentMaxHeight = 24d;
    private const double FollowAccentGlowLeft = 2d;
    private const double FollowAccentLeft = 6d;

    private Control? _accentHost;
    private Border? _followAccent;
    private Border? _followAccentGlow;
    private ScrollViewer? _scrollViewer;
    private bool _followAccentInitialized;
    private bool _followAccentUpdateQueued;

    public AppSelectionList()
    {
        UpdateVisualModeClasses(VisualMode);
        UpdateTrailingAccessorySpaceClass(ReserveTrailingAccessorySpace);
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        SelectionChanged += OnSelectionChanged;
        ContainerPrepared += OnContainerPrepared;
        ContainerClearing += OnContainerClearing;
    }

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

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DetachScrollViewerTracking();

        _accentHost = e.NameScope.Find<Control>(AccentHostPartName);
        _followAccent = e.NameScope.Find<Border>(FollowAccentPartName);
        _followAccentGlow = e.NameScope.Find<Border>(FollowAccentGlowPartName);
        _scrollViewer = e.NameScope.Find<ScrollViewer>(ScrollViewerPartName);

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
        }

        ResetFollowAccent();
        UpdateVisualModeClasses(VisualMode);
        QueueFollowAccentUpdate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == VisualModeProperty)
        {
            UpdateVisualModeClasses(change.GetNewValue<AppSelectionListVisualMode>());
            QueueFollowAccentUpdate();
        }
        else if (change.Property == ReserveTrailingAccessorySpaceProperty)
        {
            UpdateTrailingAccessorySpaceClass(change.GetNewValue<bool>());
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachScrollViewerTracking();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_followAccentInitialized && VisualMode == AppSelectionListVisualMode.Rail)
        {
            QueueFollowAccentUpdate();
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueFollowAccentUpdate();
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateFollowAccent();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        QueueFollowAccentUpdate();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        QueueFollowAccentUpdate();
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        QueueFollowAccentUpdate();
    }

    private void UpdateVisualModeClasses(AppSelectionListVisualMode mode)
    {
        SetClass(RailClassName, mode == AppSelectionListVisualMode.Rail);
        SetClass(SurfaceClassName, mode == AppSelectionListVisualMode.Surface);
        SetClass(NoneClassName, mode == AppSelectionListVisualMode.None);

        if (mode != AppSelectionListVisualMode.Rail)
        {
            ResetFollowAccent();
        }
    }

    private void UpdateTrailingAccessorySpaceClass(bool enabled)
    {
        SetClass(RailTrailingAccessorySpaceClassName, enabled);
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

    private void QueueFollowAccentUpdate()
    {
        if (_followAccentUpdateQueued)
        {
            return;
        }

        _followAccentUpdateQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _followAccentUpdateQueued = false;
                UpdateFollowAccent();
            },
            DispatcherPriority.Loaded);
    }

    private void UpdateFollowAccent()
    {
        if (VisualMode != AppSelectionListVisualMode.Rail
            || _accentHost is null
            || _followAccent is null
            || _followAccentGlow is null)
        {
            ResetFollowAccent();
            return;
        }

        if (SelectedItem is null || ContainerFromItem(SelectedItem) is not Control container)
        {
            ResetFollowAccent();
            return;
        }

        var point = container.TranslatePoint(new Point(0d, 0d), _accentHost);
        if (point is null)
        {
            return;
        }

        var height = Math.Clamp(container.Bounds.Height - 18d, FollowAccentMinHeight, FollowAccentMaxHeight);
        var top = point.Value.Y + ((container.Bounds.Height - height) / 2d);

        _followAccent.IsVisible = true;
        _followAccentGlow.IsVisible = true;
        _followAccent.Height = height;
        _followAccentGlow.Height = height;
        _followAccent.Margin = new Thickness(FollowAccentLeft, top, 0d, 0d);
        _followAccentGlow.Margin = new Thickness(FollowAccentGlowLeft, top, 0d, 0d);
        _followAccentInitialized = true;
    }

    private void ResetFollowAccent()
    {
        if (_followAccent is not null)
        {
            _followAccent.IsVisible = false;
            _followAccent.Margin = new Thickness(FollowAccentLeft, 0d, 0d, 0d);
        }

        if (_followAccentGlow is not null)
        {
            _followAccentGlow.IsVisible = false;
            _followAccentGlow.Margin = new Thickness(FollowAccentGlowLeft, 0d, 0d, 0d);
        }

        _followAccentInitialized = false;
    }

    private void DetachScrollViewerTracking()
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        _scrollViewer = null;
    }
}
