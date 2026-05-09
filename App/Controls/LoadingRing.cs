using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

public sealed class LoadingRing : Control
{
    private bool _animationFramePending;
    private int _animationGeneration;
    private TimeSpan? _lastFrameTimestamp;
    private double _rotationAngle;
    private TopLevel? _animationTopLevel;

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<LoadingRing, IBrush?>(nameof(DotBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LoadingRing, double>(nameof(StrokeThickness), 4d);

    public static readonly StyledProperty<double> SweepAngleProperty =
        AvaloniaProperty.Register<LoadingRing, double>(nameof(SweepAngle), 285d);

    static LoadingRing()
    {
        AffectsRender<LoadingRing>(
            DotBrushProperty,
            StrokeThicknessProperty,
            SweepAngleProperty);
    }

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double SweepAngle
    {
        get => GetValue(SweepAngleProperty);
        set => SetValue(SweepAngleProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            UpdateAnimationState();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var strokeThickness = Math.Clamp(StrokeThickness, 1d, 12d);
        var diameter = Math.Min(Bounds.Width, Bounds.Height);
        if (diameter <= strokeThickness || DotBrush is null)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var radius = Math.Max(1d, (diameter - strokeThickness) / 2d);
        var sweepAngle = Math.Clamp(SweepAngle, 45d, 330d);
        var startAngle = _rotationAngle - 90d;
        var endAngle = startAngle + sweepAngle;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(start, isFilled: false);
            geometryContext.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0d,
                isLargeArc: sweepAngle > 180d,
                sweepDirection: SweepDirection.Clockwise);
        }

        var pen = new Pen(DotBrush, strokeThickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(brush: null, pen: pen, geometry: geometry);
    }

    private void StartAnimation()
    {
        if (_animationFramePending)
        {
            return;
        }

        _animationTopLevel = TopLevel.GetTopLevel(this);
        RequestNextFrame();
    }

    private void UpdateAnimationState()
    {
        if (VisualRoot is not null && IsVisible)
        {
            StartAnimation();
            return;
        }

        StopAnimation();
    }

    private void StopAnimation()
    {
        _animationGeneration++;
        _animationFramePending = false;
        _lastFrameTimestamp = null;
        _animationTopLevel = null;
    }

    private void RequestNextFrame()
    {
        if (_animationFramePending)
        {
            return;
        }

        _animationTopLevel ??= TopLevel.GetTopLevel(this);
        if (_animationTopLevel is null)
        {
            return;
        }

        _animationFramePending = true;
        var generation = _animationGeneration;
        _animationTopLevel.RequestAnimationFrame(timestamp => OnAnimationFrame(timestamp, generation));
    }

    private void OnAnimationFrame(TimeSpan timestamp, int generation)
    {
        if (generation != _animationGeneration)
        {
            return;
        }

        _animationFramePending = false;
        if (VisualRoot is null || !IsVisible)
        {
            StopAnimation();
            return;
        }

        if (_lastFrameTimestamp is { } lastTimestamp)
        {
            var elapsed = timestamp - lastTimestamp;
            var elapsedMilliseconds = Math.Clamp(elapsed.TotalMilliseconds, 0d, 100d);
            _rotationAngle = (_rotationAngle + (elapsedMilliseconds * 0.36d)) % 360d;
        }

        _lastFrameTimestamp = timestamp;
        InvalidateVisual();
        RequestNextFrame();
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + (Math.Cos(angleRadians) * radius),
            center.Y + (Math.Sin(angleRadians) * radius));
    }
}
