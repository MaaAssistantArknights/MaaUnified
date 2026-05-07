using System;
using Avalonia;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public class SettingsInlineRow : Grid
{
    private const int LabelColumn = 0;
    private const int FieldColumn = 2;
    private const string InlineLabelClass = "settings-inline-label";

    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<SettingsInlineRow, double>(nameof(LabelWidth), 100);

    public static readonly StyledProperty<double> GapWidthProperty =
        AvaloniaProperty.Register<SettingsInlineRow, double>(nameof(GapWidth), 6);

    public static readonly StyledProperty<double> FieldWidthProperty =
        AvaloniaProperty.Register<SettingsInlineRow, double>(nameof(FieldWidth), 180);

    public static readonly StyledProperty<double> NumberFieldWidthProperty =
        AvaloniaProperty.Register<SettingsInlineRow, double>(nameof(NumberFieldWidth), 130);

    private static readonly AttachedProperty<bool> UsesInlineWidthProperty =
        AvaloniaProperty.RegisterAttached<SettingsInlineRow, Control, bool>("UsesInlineWidth", false);

    static SettingsInlineRow()
    {
        AffectsMeasure<SettingsInlineRow>(
            LabelWidthProperty,
            GapWidthProperty,
            FieldWidthProperty,
            NumberFieldWidthProperty);
    }

    public SettingsInlineRow()
    {
        Classes.Set("task-settings-inline-row", true);
        UpdateColumnDefinitions();
    }

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public double GapWidth
    {
        get => GetValue(GapWidthProperty);
        set => SetValue(GapWidthProperty, value);
    }

    public double FieldWidth
    {
        get => GetValue(FieldWidthProperty);
        set => SetValue(FieldWidthProperty, value);
    }

    public double NumberFieldWidth
    {
        get => GetValue(NumberFieldWidthProperty);
        set => SetValue(NumberFieldWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelWidthProperty || change.Property == GapWidthProperty)
        {
            UpdateColumnDefinitions();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var fieldSlotWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - Math.Max(0, LabelWidth) - Math.Max(0, GapWidth));

        PlaceDirectChildren(fieldSlotWidth);
        return base.MeasureOverride(availableSize);
    }

    private void UpdateColumnDefinitions()
    {
        ColumnDefinitions =
        [
            new ColumnDefinition(new GridLength(Math.Max(0, LabelWidth))),
            new ColumnDefinition(new GridLength(Math.Max(0, GapWidth))),
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
        ];
    }

    private void PlaceDirectChildren(double fieldSlotWidth)
    {
        var visibleIndex = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            var isLabel = visibleIndex % 2 == 0;
            SetColumn(child, isLabel ? LabelColumn : FieldColumn);
            ApplyVerticalDefaults(child);
            if (isLabel)
            {
                ApplyLabelDefaults(child);
            }
            else
            {
                ApplyFieldDefaults(child, fieldSlotWidth);
            }

            visibleIndex++;
        }
    }

    private static void ApplyLabelDefaults(Control child)
    {
        if (child.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Left)
        {
            child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        }

        if (child is TextBlock textBlock)
        {
            textBlock.Classes.Set(InlineLabelClass, true);
        }
    }

    private static void ApplyVerticalDefaults(Control child)
    {
        if (child.VerticalAlignment != Avalonia.Layout.VerticalAlignment.Center)
        {
            child.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        }

        if (child is TextBlock && child.Margin.Top == 0 && child.Margin.Bottom > 0)
        {
            child.Margin = new Thickness(child.Margin.Left, 0, child.Margin.Right, 0);
        }
    }

    private void ApplyFieldDefaults(Control child, double fieldSlotWidth)
    {
        var targetWidth = child is AppNumberInput
            ? NumberFieldWidth
            : FieldWidth;
        var effectiveWidth = double.IsInfinity(fieldSlotWidth)
            ? targetWidth
            : Math.Min(targetWidth, fieldSlotWidth);

        if (child.GetValue(UsesInlineWidthProperty))
        {
            SetWidthIfChanged(child, Math.Max(0, effectiveWidth));
            return;
        }

        if (!double.IsNaN(child.Width))
        {
            return;
        }

        child.MinWidth = 0;
        child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SetWidthIfChanged(child, Math.Max(0, effectiveWidth));
        child.SetValue(UsesInlineWidthProperty, true);
    }

    private static void SetWidthIfChanged(Control child, double width)
    {
        if (double.IsNaN(child.Width) || Math.Abs(child.Width - width) > 0.5)
        {
            child.Width = width;
        }
    }
}
