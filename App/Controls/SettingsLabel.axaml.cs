using Avalonia;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public partial class SettingsLabel : UserControl
{
    private const double FallbackHintGlyphWidth = 16d;
    private bool _hasTip;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SettingsLabel, string?>(nameof(Text));

    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<SettingsLabel, string?>(nameof(Tip));

    public static readonly DirectProperty<SettingsLabel, bool> HasTipProperty =
        AvaloniaProperty.RegisterDirect<SettingsLabel, bool>(
            nameof(HasTip),
            label => label.HasTip);

    public SettingsLabel()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
        UpdateTextMaxWidth();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    public bool HasTip
    {
        get => _hasTip;
        private set => SetAndRaise(HasTipProperty, ref _hasTip, value);
    }

    internal double MeasureNaturalLabelWidth()
    {
        if (PART_LabelText is null || string.IsNullOrEmpty(Text))
        {
            return HasTip ? MeasureHintReserveWidth() : 0d;
        }

        var previousMaxWidth = PART_LabelText.MaxWidth;
        PART_LabelText.MaxWidth = double.PositiveInfinity;
        PART_LabelText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = PART_LabelText.DesiredSize.Width;
        PART_LabelText.MaxWidth = previousMaxWidth;
        return Math.Ceiling(textWidth + (HasTip ? MeasureHintReserveWidth() : 0d));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TipProperty)
        {
            HasTip = !string.IsNullOrWhiteSpace(Tip);
            UpdateTextMaxWidth();
            SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
        }
        else if (change.Property == TextProperty)
        {
            SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateTextMaxWidth();
        }
    }

    private void UpdateTextMaxWidth()
    {
        if (PART_LabelText is null)
        {
            return;
        }

        var reserveWidth = HasTip ? MeasureHintReserveWidth() : 0d;
        var maxWidth = Math.Max(0d, Bounds.Width - reserveWidth);
        PART_LabelText.MaxWidth = maxWidth > 0d
            ? maxWidth
            : double.PositiveInfinity;
    }

    private double MeasureHintReserveWidth()
    {
        if (!HasTip)
        {
            return 0d;
        }

        var gapWidth = 0d;
        if (PART_HintGap is not null)
        {
            var explicitWidth = !double.IsNaN(PART_HintGap.Width) && PART_HintGap.Width > 0d
                ? PART_HintGap.Width
                : 0d;
            gapWidth = explicitWidth + PART_HintGap.Margin.Left + PART_HintGap.Margin.Right;
        }

        var tipWidth = 0d;
        if (PART_Tip is not null)
        {
            PART_Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tipWidth = PART_Tip.DesiredSize.Width;
            if (tipWidth <= 0d || double.IsNaN(tipWidth))
            {
                tipWidth = PART_Tip.Bounds.Width;
            }
        }

        if (tipWidth <= 0d || double.IsNaN(tipWidth))
        {
            tipWidth = FallbackHintGlyphWidth;
        }

        return Math.Ceiling(gapWidth + tipWidth);
    }
}
