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
    private const double OverlayPanelMaxWidth = 440d;
    private const double NativeAttachedLogicalWidth = OverlayPanelMaxWidth + (OverlayPanelMargin * 2d);
    private const double NativeAttachedLogicalHeight = 260d;
    private const double PreviewWindowLogicalWidth = 480d;
    private const double PreviewWindowLogicalHeight = 260d;
    private const double PreviewWindowLogicalMargin = 24d;
    private OverlayPresentationViewModel? _presentation;
    private INotifyCollectionChanged? _currentLogCollection;
    private Border? _overlayPanel;
    private ScrollViewer? _overlayScroller;
    private bool _nativeAttached;
    private bool _previewResizeQueued;

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
            SchedulePreviewResizeToContent();
            ScheduleScrollToEnd();
        };
        SizeChanged += (_, _) =>
        {
            UpdatePanelConstraints();
            SchedulePreviewResizeToContent();
        };
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
        SchedulePreviewResizeToContent();
        ScheduleScrollToEnd();
    }

    public void ApplyPreviewBounds(PixelRect workingArea, PixelRect? anchorBounds = null)
    {
        var scale = Math.Max(0.01d, RenderScaling);
        var margin = ResolvePreviewMarginPixels(scale);
        var pixelSize = ResolvePreviewPixelSize(workingArea, scale, margin);
        var position = ResolvePreviewPosition(workingArea, pixelSize, anchorBounds, margin);

        Width = pixelSize.Width / scale;
        Height = pixelSize.Height / scale;
        Position = position;
        UpdatePanelConstraints();
        SchedulePreviewResizeToContent();
        ScheduleScrollToEnd();
    }

    internal static PixelSize ResolvePreviewPixelSize(PixelRect workingArea, double renderScaling, int marginPixels)
    {
        var scale = Math.Max(0.01d, renderScaling);
        var usableWidthPixels = Math.Max(1, workingArea.Width - (marginPixels * 2));
        var usableHeightPixels = Math.Max(1, workingArea.Height - (marginPixels * 2));
        var widthPixels = Math.Min(
            Math.Max(1, (int)Math.Round(PreviewWindowLogicalWidth * scale)),
            usableWidthPixels);
        var heightPixels = Math.Min(
            Math.Max(1, (int)Math.Round(PreviewWindowLogicalHeight * scale)),
            usableHeightPixels);

        return new PixelSize(widthPixels, heightPixels);
    }

    internal static PixelPoint ResolvePreviewPosition(
        PixelRect workingArea,
        PixelSize pixelSize,
        PixelRect? anchorBounds,
        int marginPixels)
    {
        var maxX = workingArea.X + workingArea.Width - pixelSize.Width - marginPixels;
        var minX = workingArea.X + marginPixels;
        var minY = workingArea.Y + marginPixels;
        var maxY = workingArea.Y + workingArea.Height - pixelSize.Height - marginPixels;

        var x = Math.Max(minX, maxX);
        var y = minY;
        if (anchorBounds is { Width: > 0, Height: > 0 } anchor)
        {
            x = Clamp(anchor.X + marginPixels, minX, maxX);
            y = Clamp(anchor.Y + marginPixels, minY, maxY);
        }

        return new PixelPoint(x, y);
    }

    internal static int ResolvePreviewMarginPixels(double renderScaling)
        => Math.Max(0, (int)Math.Round(PreviewWindowLogicalMargin * Math.Max(0.01d, renderScaling)));

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
        SchedulePreviewResizeToContent();
        ScheduleScrollToEnd();
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(OverlayPresentationViewModel.CurrentLogs), StringComparison.Ordinal))
        {
            return;
        }

        SubscribeCurrentLogCollection();
        SchedulePreviewResizeToContent();
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
        SchedulePreviewResizeToContent();
        ScheduleScrollToEnd();
    }

    private void SchedulePreviewResizeToContent()
    {
        if (_previewResizeQueued)
        {
            return;
        }

        _previewResizeQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _previewResizeQueued = false;
            ResizePreviewHeightToContent();
        }, DispatcherPriority.Background);
    }

    private void ResizePreviewHeightToContent()
    {
        if (_nativeAttached
            || _overlayPanel is null
            || _overlayScroller is null
            || !_overlayPanel.IsVisible)
        {
            return;
        }

        var logicalWidth = Math.Max(1d, Bounds.Width);
        var availableWidth = Math.Max(1d, logicalWidth - (OverlayPanelMargin * 2d));
        _overlayPanel.MaxWidth = Math.Min(OverlayPanelMaxWidth, availableWidth);
        _overlayScroller.MaxHeight = Math.Max(1d, PreviewWindowLogicalHeight - (OverlayPanelMargin * 2d));
        _overlayPanel.Measure(new Size(_overlayPanel.MaxWidth, double.PositiveInfinity));

        var desiredHeight = Math.Ceiling(Math.Clamp(_overlayPanel.DesiredSize.Height, 1d, PreviewWindowLogicalHeight));
        if (Math.Abs(Height - desiredHeight) > 0.5d)
        {
            Height = desiredHeight;
        }

        UpdatePanelConstraints();
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
