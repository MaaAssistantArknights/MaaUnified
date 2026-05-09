using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public sealed class AppSlidingSegmentController
{
    private const string AnimatedClassName = "animate";

    private static readonly Transitions AnimatedTransformTransitions =
    [
        new DoubleTransition
        {
            Property = TranslateTransform.XProperty,
            Duration = TimeSpan.FromSeconds(0.15),
            Easing = new CubicEaseOut(),
        },
    ];

    private readonly Control _track;
    private readonly Border _slider;
    private readonly Func<Control?> _activeControlProvider;
    private readonly TranslateTransform _sliderTransform;
    private bool _syncQueued;
    private bool _pendingAnimate;
    private double _sliderX = double.NaN;
    private double _sliderWidth = double.NaN;

    public AppSlidingSegmentController(Control track, Border slider, Func<Control?> activeControlProvider)
    {
        _track = track;
        _slider = slider;
        _activeControlProvider = activeControlProvider;
        _sliderTransform = slider.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _slider.RenderTransform = _sliderTransform;
    }

    public void QueueSync(bool animate = false, bool resetMetrics = true)
    {
        if (resetMetrics)
        {
            ResetMetrics();
        }

        _pendingAnimate |= animate;
        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _syncQueued = false;
                var shouldAnimate = _pendingAnimate;
                _pendingAnimate = false;
                Sync(shouldAnimate);
            },
            DispatcherPriority.Loaded);
    }

    public void Sync(bool animate)
    {
        if (!_track.IsVisible)
        {
            Hide();
            return;
        }

        var activeControl = _activeControlProvider();
        if (activeControl is null || activeControl.Bounds.Width <= 0 || _track.Bounds.Width <= 0)
        {
            return;
        }

        var origin = activeControl.TranslatePoint(new Point(0, 0), _track);
        if (origin is null)
        {
            return;
        }

        var targetX = origin.Value.X;
        var targetWidth = activeControl.Bounds.Width;
        if (_slider.IsVisible
            && Math.Abs(_sliderX - targetX) < 0.5
            && Math.Abs(_sliderWidth - targetWidth) < 0.5)
        {
            return;
        }

        _slider.Classes.Set(AnimatedClassName, animate);
        _sliderTransform.Transitions = animate ? AnimatedTransformTransitions : null;
        _slider.Width = targetWidth;
        _sliderTransform.X = targetX;
        _slider.IsVisible = true;
        _sliderX = targetX;
        _sliderWidth = targetWidth;
    }

    public void Hide()
    {
        _slider.IsVisible = false;
        _slider.Classes.Set(AnimatedClassName, false);
        _sliderTransform.Transitions = null;
        _pendingAnimate = false;
        ResetMetrics();
    }

    private void ResetMetrics()
    {
        _sliderX = double.NaN;
        _sliderWidth = double.NaN;
    }
}
