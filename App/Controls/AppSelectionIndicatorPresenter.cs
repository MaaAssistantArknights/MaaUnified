using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MAAUnified.App.Controls;

public class AppSelectionIndicatorPresenter : TemplatedControl
{
    public static readonly StyledProperty<SelectingItemsControl?> TargetProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, SelectingItemsControl?>(nameof(Target));

    public static readonly StyledProperty<AppIndicatorOrientation> OrientationProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, AppIndicatorOrientation>(
            nameof(Orientation),
            AppIndicatorOrientation.Vertical);

    public static readonly StyledProperty<double> LeadingInsetProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, double>(nameof(LeadingInset));

    public static readonly StyledProperty<double> TrailingInsetProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, double>(nameof(TrailingInset));

    public static readonly StyledProperty<double> MinimumLengthProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, double>(nameof(MinimumLength));

    public static readonly StyledProperty<double> MaximumLengthProperty =
        AvaloniaProperty.Register<AppSelectionIndicatorPresenter, double>(
            nameof(MaximumLength),
            double.NaN);

    private const string IndicatorPartName = "PART_Indicator";
    private const string IndicatorGlowPartName = "PART_IndicatorGlow";
    private const string HorizontalClassName = "indicator-horizontal";
    private const string VerticalClassName = "indicator-vertical";

    private readonly SlidingIndicatorController _controller;

    public AppSelectionIndicatorPresenter()
    {
        _controller = new SlidingIndicatorController(this);
        UpdateOrientationClasses(Orientation);
        SizeChanged += OnSizeChanged;
    }

    public SelectingItemsControl? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public AppIndicatorOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double LeadingInset
    {
        get => GetValue(LeadingInsetProperty);
        set => SetValue(LeadingInsetProperty, value);
    }

    public double TrailingInset
    {
        get => GetValue(TrailingInsetProperty);
        set => SetValue(TrailingInsetProperty, value);
    }

    public double MinimumLength
    {
        get => GetValue(MinimumLengthProperty);
        set => SetValue(MinimumLengthProperty, value);
    }

    public double MaximumLength
    {
        get => GetValue(MaximumLengthProperty);
        set => SetValue(MaximumLengthProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _controller.SetVisuals(
            e.NameScope.Find<Border>(IndicatorPartName),
            e.NameScope.Find<Border>(IndicatorGlowPartName));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetProperty)
        {
            _controller.SetTarget(change.GetNewValue<SelectingItemsControl?>());
            return;
        }

        if (change.Property == OrientationProperty)
        {
            UpdateOrientationClasses(change.GetNewValue<AppIndicatorOrientation>());
        }

        if (change.Property == OrientationProperty
            || change.Property == LeadingInsetProperty
            || change.Property == TrailingInsetProperty
            || change.Property == MinimumLengthProperty
            || change.Property == MaximumLengthProperty)
        {
            _controller.QueueUpdate();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _controller.Reset();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _controller.QueueUpdate(resetRetryBudget: false);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _controller.QueueUpdate(resetRetryBudget: false);
    }

    private void UpdateOrientationClasses(AppIndicatorOrientation orientation)
    {
        SetClass(HorizontalClassName, orientation == AppIndicatorOrientation.Horizontal);
        SetClass(VerticalClassName, orientation == AppIndicatorOrientation.Vertical);
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
