using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MAAUnified.App.Controls;

public sealed class CircularCountdownRing : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<CircularCountdownRing, double>(nameof(Progress), 1d);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<CircularCountdownRing, double>(nameof(StrokeThickness), 2d);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<CircularCountdownRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<CircularCountdownRing, IBrush?>(nameof(ProgressBrush));

    static CircularCountdownRing()
    {
        AffectsRender<CircularCountdownRing>(
            ProgressProperty,
            StrokeThicknessProperty,
            TrackBrushProperty,
            ProgressBrushProperty);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var strokeThickness = Math.Clamp(StrokeThickness, 0.5d, 64d);
        var diameter = Math.Min(Bounds.Width, Bounds.Height);
        if (diameter <= strokeThickness)
        {
            return;
        }

        var center = new Point(Bounds.X + (Bounds.Width / 2d), Bounds.Y + (Bounds.Height / 2d));
        var radius = (diameter - strokeThickness) / 2d;

        if (TrackBrush is not null)
        {
            context.DrawEllipse(
                brush: null,
                pen: CreatePen(TrackBrush, strokeThickness),
                center: center,
                radiusX: radius,
                radiusY: radius);
        }

        var progress = Math.Clamp(Progress, 0d, 1d);
        if (progress <= 0d || ProgressBrush is null)
        {
            return;
        }

        var progressPen = CreatePen(ProgressBrush, strokeThickness);
        if (progress >= 0.999d)
        {
            context.DrawEllipse(
                brush: null,
                pen: progressPen,
                center: center,
                radiusX: radius,
                radiusY: radius);
            return;
        }

        var start = PointOnCircle(center, radius, -90d);
        var end = PointOnCircle(center, radius, -90d + (progress * 360d));
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(start, isFilled: false);
            geometryContext.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0d,
                isLargeArc: progress > 0.5d,
                sweepDirection: SweepDirection.Clockwise);
        }

        context.DrawGeometry(brush: null, pen: progressPen, geometry: geometry);
    }

    private static Pen CreatePen(IBrush brush, double strokeThickness)
    {
        return new Pen(brush, strokeThickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + (Math.Cos(angleRadians) * radius),
            center.Y + (Math.Sin(angleRadians) * radius));
    }
}
