using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Platform;

namespace MAAUnified.App.Views;

public partial class OverlayHostWindow : Window
{
    private const double OverlayPanelMargin = 8d;
    private const double OverlayPanelMaxWidth = 250d;
    private const double NativeAttachedLogicalWidth = OverlayPanelMaxWidth + (OverlayPanelMargin * 2d);
    private const double NativeAttachedLogicalHeight = 240d;
    private const int PreviewWindowWidth = 320;
    private const int PreviewWindowHeight = 240;
    private const int PreviewWindowMargin = 24;
    private OverlayPresentationViewModel? _presentation;
    private INotifyCollectionChanged? _currentLogCollection;
    private Border? _overlayPanel;
    private ScrollViewer? _overlayScroller;
    private bool _nativeAttached;

    public OverlayHostWindow()
    {
        InitializeComponent();
        Width = 1;
        Height = 1;
        MinWidth = 1;
        MinHeight = 1;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Focusable = false;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        Opacity = 1d;
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
        };
        ShowActivated = false;
        _overlayPanel = this.FindControl<Border>("OverlayPanel");
        _overlayScroller = this.FindControl<ScrollViewer>("OverlayScroller");
        if (_overlayPanel is not null)
        {
            _overlayPanel.IsVisible = false;
        }
        DataContextChanged += OnOverlayDataContextChanged;
        Opened += (_, _) =>
        {
            UpdatePanelConstraints();
            ScheduleScrollToEnd();
        };
        SizeChanged += (_, _) => UpdatePanelConstraints();
    }

    public void SetOverlayActive(bool active, OverlayRuntimeMode mode = OverlayRuntimeMode.Preview)
    {
        _nativeAttached = active && mode == OverlayRuntimeMode.Native;
        if (_overlayPanel is not null)
        {
            _overlayPanel.IsVisible = active;
        }

        if (!active)
        {
            Width = 1d;
            Height = 1d;
            UpdatePanelConstraints();
            return;
        }

        if (_nativeAttached)
        {
            Width = Math.Max(Width, NativeAttachedLogicalWidth);
            Height = Math.Max(Height, NativeAttachedLogicalHeight);
        }

        UpdatePanelConstraints();
        ScheduleScrollToEnd();
    }

    public void ApplyPreviewBounds(PixelRect workingArea, PixelRect? anchorBounds = null)
    {
        var scale = Math.Max(0.01d, RenderScaling);
        var usableWidth = Math.Max(1, workingArea.Width - (PreviewWindowMargin * 2));
        var usableHeight = Math.Max(1, workingArea.Height - (PreviewWindowMargin * 2));
        var width = Math.Min(PreviewWindowWidth, usableWidth);
        var height = Math.Min(PreviewWindowHeight, usableHeight);
        var maxX = workingArea.X + workingArea.Width - width - PreviewWindowMargin;
        var minX = workingArea.X + PreviewWindowMargin;
        var minY = workingArea.Y + PreviewWindowMargin;
        var maxY = workingArea.Y + workingArea.Height - height - PreviewWindowMargin;

        var x = Math.Max(minX, maxX);
        var y = minY;
        if (anchorBounds is { Width: > 0, Height: > 0 } anchor)
        {
            x = Clamp(anchor.X + anchor.Width - width - PreviewWindowMargin, minX, maxX);
            y = Clamp(anchor.Y + PreviewWindowMargin, minY, maxY);
        }

        Width = width / scale;
        Height = height / scale;
        Position = new PixelPoint(x, y);
        UpdatePanelConstraints();
        ScheduleScrollToEnd();
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    private void OnOverlayDataContextChanged(object? sender, EventArgs e)
    {
        if (_presentation is not null)
        {
            _presentation.PropertyChanged -= OnPresentationPropertyChanged;
        }

        UnsubscribeCurrentLogCollection();

        _presentation = DataContext as OverlayPresentationViewModel;
        if (_presentation is null)
        {
            return;
        }

        _presentation.PropertyChanged += OnPresentationPropertyChanged;
        SubscribeCurrentLogCollection();
        ScheduleScrollToEnd();
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(OverlayPresentationViewModel.CurrentLogs), StringComparison.Ordinal))
        {
            return;
        }

        SubscribeCurrentLogCollection();
        ScheduleScrollToEnd();
    }

    private void SubscribeCurrentLogCollection()
    {
        UnsubscribeCurrentLogCollection();
        _currentLogCollection = _presentation?.CurrentLogs as INotifyCollectionChanged;
        if (_currentLogCollection is not null)
        {
            _currentLogCollection.CollectionChanged += OnCurrentLogCollectionChanged;
        }
    }

    private void UnsubscribeCurrentLogCollection()
    {
        if (_currentLogCollection is not null)
        {
            _currentLogCollection.CollectionChanged -= OnCurrentLogCollectionChanged;
            _currentLogCollection = null;
        }
    }

    private void OnCurrentLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleScrollToEnd();
    }

    private void ScheduleScrollToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayScroller is null)
            {
                return;
            }

            _overlayScroller.Offset = new Vector(
                _overlayScroller.Offset.X,
                Math.Max(0d, _overlayScroller.Extent.Height - _overlayScroller.Viewport.Height));
        }, DispatcherPriority.Background);
    }

    private void UpdatePanelConstraints()
    {
        if (_overlayPanel is null || _overlayScroller is null)
        {
            return;
        }

        var logicalWidth = _nativeAttached ? Math.Max(Bounds.Width, NativeAttachedLogicalWidth) : Bounds.Width;
        var logicalHeight = _nativeAttached ? Math.Max(Bounds.Height, NativeAttachedLogicalHeight) : Bounds.Height;
        var availableWidth = Math.Max(1d, logicalWidth - (OverlayPanelMargin * 2d));
        var availableHeight = Math.Max(1d, logicalHeight - (OverlayPanelMargin * 2d));
        _overlayPanel.MaxWidth = Math.Min(OverlayPanelMaxWidth, availableWidth);
        _overlayScroller.MaxHeight = availableHeight;
    }
}
