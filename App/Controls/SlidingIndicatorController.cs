using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

public sealed class SlidingIndicatorController : IDisposable
{
    private const int MaxRetryCount = 6;

    private readonly AppSelectionIndicatorPresenter _presenter;
    private SelectingItemsControl? _target;
    private Border? _indicator;
    private Border? _indicatorGlow;
    private ScrollViewer? _scrollViewer;
    private Thickness _indicatorBaseMargin;
    private Thickness _indicatorGlowBaseMargin;
    private bool _updateQueued;
    private int _retryAttempts;
    private bool _disposed;

    public SlidingIndicatorController(AppSelectionIndicatorPresenter presenter)
    {
        _presenter = presenter;
    }

    public void SetTarget(SelectingItemsControl? target)
    {
        if (ReferenceEquals(_target, target))
        {
            return;
        }

        DetachTarget();
        _target = target;
        AttachTarget();
        QueueUpdate();
    }

    public void SetVisuals(Border? indicator, Border? indicatorGlow)
    {
        _indicator = indicator;
        _indicatorGlow = indicatorGlow;
        _indicatorBaseMargin = indicator?.Margin ?? default;
        _indicatorGlowBaseMargin = indicatorGlow?.Margin ?? default;

        Reset();
        QueueUpdate();
    }

    public void QueueUpdate(bool resetRetryBudget = true)
    {
        QueueUpdate(resetRetryBudget, DispatcherPriority.Loaded);
    }

    public void QueueUpdate(bool resetRetryBudget, DispatcherPriority priority)
    {
        if (_disposed)
        {
            return;
        }

        if (resetRetryBudget)
        {
            _retryAttempts = 0;
        }

        if (_updateQueued)
        {
            return;
        }

        _updateQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _updateQueued = false;
                Update();
            },
            priority);
    }

    public void Reset()
    {
        if (_indicator is not null)
        {
            _indicator.IsVisible = false;
            _indicator.Margin = _indicatorBaseMargin;
        }

        if (_indicatorGlow is not null)
        {
            _indicatorGlow.IsVisible = false;
            _indicatorGlow.Margin = _indicatorGlowBaseMargin;
        }

        _retryAttempts = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachTarget();
        _indicator = null;
        _indicatorGlow = null;
    }

    private void Update()
    {
        if (_disposed
            || _target is null
            || _indicator is null
            || _indicatorGlow is null
            || _presenter.Bounds.Width <= 0d
            || _presenter.Bounds.Height <= 0d)
        {
            Reset();
            return;
        }

        if (_target.SelectedItem is null || _target.ContainerFromItem(_target.SelectedItem) is not Control container)
        {
            Reset();
            return;
        }

        var layoutTarget = ResolveLayoutTarget(container);
        if (layoutTarget.Bounds.Width <= 0d || layoutTarget.Bounds.Height <= 0d)
        {
            TryQueueRetry();
            return;
        }

        var point = layoutTarget.TranslatePoint(default, _presenter);
        if (point is null)
        {
            RefreshScrollViewer();
            TryQueueRetry();
            return;
        }

        ApplyIndicatorLayout(layoutTarget.Bounds, point.Value);
        _retryAttempts = 0;
    }

    private Control ResolveLayoutTarget(Control container)
    {
        if (_presenter.Orientation != AppIndicatorOrientation.Horizontal)
        {
            return container;
        }

        return container
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(static control => string.Equals(control.Name, "PART_NavChrome", StringComparison.Ordinal))
            ?? container;
    }

    private void ApplyIndicatorLayout(Rect containerBounds, Point point)
    {
        var leadingInset = Math.Max(0d, _presenter.LeadingInset);
        var trailingInset = Math.Max(0d, _presenter.TrailingInset);

        if (_presenter.Orientation == AppIndicatorOrientation.Horizontal)
        {
            var width = ClampLength(containerBounds.Width - leadingInset - trailingInset);
            var left = point.X + leadingInset;
            var bottom = point.Y + containerBounds.Height;
            ApplyHorizontal(_indicator, _indicatorBaseMargin, left, width, bottom);
            ApplyHorizontal(_indicatorGlow, _indicatorGlowBaseMargin, left, width, bottom);
            return;
        }

        var availableHeight = Math.Max(0d, containerBounds.Height - leadingInset - trailingInset);
        var height = ClampLength(availableHeight);
        var top = point.Y + leadingInset + Math.Max(0d, (availableHeight - height) / 2d);
        ApplyVertical(_indicator, _indicatorBaseMargin, top, height);
        ApplyVertical(_indicatorGlow, _indicatorGlowBaseMargin, top, height);
    }

    private double ClampLength(double length)
    {
        var clamped = Math.Max(0d, length);
        var min = Math.Max(0d, _presenter.MinimumLength);
        if (!double.IsNaN(_presenter.MaximumLength) && _presenter.MaximumLength > 0d)
        {
            clamped = Math.Min(clamped, _presenter.MaximumLength);
        }

        return Math.Max(clamped, min);
    }

    private static void ApplyHorizontal(Border? border, Thickness baseMargin, double left, double width, double bottom)
    {
        if (border is null)
        {
            return;
        }

        border.IsVisible = true;

        if (Math.Abs(border.Width - width) > 0.01d)
        {
            border.Width = width;
        }

        var height = ResolveIndicatorHeight(border);
        var top = Math.Max(0d, bottom - height - Math.Max(0d, baseMargin.Bottom));
        var margin = new Thickness(baseMargin.Left + left, baseMargin.Top + top, baseMargin.Right, baseMargin.Bottom);
        if (!border.Margin.Equals(margin))
        {
            border.Margin = margin;
        }
    }

    private static double ResolveIndicatorHeight(Border border)
    {
        if (!double.IsNaN(border.Height) && border.Height > 0d)
        {
            return border.Height;
        }

        if (border.Bounds.Height > 0d)
        {
            return border.Bounds.Height;
        }

        return Math.Max(0d, border.DesiredSize.Height);
    }

    private static void ApplyVertical(Border? border, Thickness baseMargin, double top, double height)
    {
        if (border is null)
        {
            return;
        }

        border.IsVisible = true;

        if (Math.Abs(border.Height - height) > 0.01d)
        {
            border.Height = height;
        }

        var margin = new Thickness(baseMargin.Left, baseMargin.Top + top, baseMargin.Right, baseMargin.Bottom);
        if (!border.Margin.Equals(margin))
        {
            border.Margin = margin;
        }
    }

    private void TryQueueRetry()
    {
        if (_retryAttempts >= MaxRetryCount)
        {
            return;
        }

        _retryAttempts++;
        QueueUpdate(resetRetryBudget: false, priority: DispatcherPriority.Render);
    }

    private void AttachTarget()
    {
        if (_target is null)
        {
            return;
        }

        _target.SelectionChanged += OnSelectionChanged;
        _target.ContainerPrepared += OnContainerPrepared;
        _target.ContainerClearing += OnContainerClearing;
        _target.SizeChanged += OnTargetSizeChanged;
        RefreshScrollViewer();
    }

    private void DetachTarget()
    {
        if (_target is not null)
        {
            _target.SelectionChanged -= OnSelectionChanged;
            _target.ContainerPrepared -= OnContainerPrepared;
            _target.ContainerClearing -= OnContainerClearing;
            _target.SizeChanged -= OnTargetSizeChanged;
        }

        DetachScrollViewer();
        _target = null;
    }

    private void RefreshScrollViewer()
    {
        var nextScrollViewer = _target?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(_scrollViewer, nextScrollViewer))
        {
            return;
        }

        DetachScrollViewer();
        _scrollViewer = nextScrollViewer;

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
        }
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        _scrollViewer = null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        QueueUpdate();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        RefreshScrollViewer();
        QueueUpdate();
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        QueueUpdate();
    }

    private void OnTargetSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueUpdate();
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        QueueUpdate(resetRetryBudget: false, priority: DispatcherPriority.Render);
    }
}
