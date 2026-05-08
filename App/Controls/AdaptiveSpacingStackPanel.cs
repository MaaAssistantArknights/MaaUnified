using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace MAAUnified.App.Controls;

public class AdaptiveSpacingStackPanel : StackPanel
{
    private const double DefaultTargetPitch = 52;
    private const double CompactSpacingThreshold = 6;
    private const double CompactTargetPitchOffset = 8;
    private const double DefaultCompactGap = 6;
    private const double MaxCompactGap = 8;
    private const double MaxToggleCompactGap = 10;
    private const double DefaultMixedInteractiveGap = 8;
    private const double MaxMixedInteractiveGap = 12;
    private const double DependentRowGapReduction = 11;
    private const double MinDependentRowGap = -6;
    private const double DefaultSupplementaryGap = 4;
    private const double MaxSupplementaryGap = 6;
    private const double CaptionSlot = 16;
    private const double SupplementaryTextSlot = 16;
    private const double BodyTextSlot = 20;
    private const double ToggleSlot = 20;
    private const double FieldControlSlot = 38;
    private const double ActionSlot = 32;

    public static readonly StyledProperty<double> MinGapProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(MinGap),
            8);

    public static readonly StyledProperty<double> TargetPitchProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(TargetPitch),
            double.NaN);

    public static readonly StyledProperty<double> MaxGapProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(MaxGap),
            20);

    public static readonly StyledProperty<double> ShortRowSlotProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(ShortRowSlot),
            30);

    public static readonly StyledProperty<double> SupplementaryTextGapProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(SupplementaryTextGap),
            double.NaN);

    public static readonly StyledProperty<double> AfterSupplementaryTextGapProperty =
        AvaloniaProperty.Register<AdaptiveSpacingStackPanel, double>(
            nameof(AfterSupplementaryTextGap),
            double.NaN);

    public double MinGap
    {
        get => GetValue(MinGapProperty);
        set => SetValue(MinGapProperty, value);
    }

    public double TargetPitch
    {
        get => GetValue(TargetPitchProperty);
        set => SetValue(TargetPitchProperty, value);
    }

    public double MaxGap
    {
        get => GetValue(MaxGapProperty);
        set => SetValue(MaxGapProperty, value);
    }

    public double ShortRowSlot
    {
        get => GetValue(ShortRowSlotProperty);
        set => SetValue(ShortRowSlotProperty, value);
    }

    public double SupplementaryTextGap
    {
        get => GetValue(SupplementaryTextGapProperty);
        set => SetValue(SupplementaryTextGapProperty, value);
    }

    public double AfterSupplementaryTextGap
    {
        get => GetValue(AfterSupplementaryTextGapProperty);
        set => SetValue(AfterSupplementaryTextGapProperty, value);
    }

    static AdaptiveSpacingStackPanel()
    {
        AffectsMeasure<AdaptiveSpacingStackPanel>(
            MinGapProperty,
            TargetPitchProperty,
            MaxGapProperty,
            ShortRowSlotProperty,
            SupplementaryTextGapProperty,
            AfterSupplementaryTextGapProperty,
            SpacingProperty,
            OrientationProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!UseAdaptiveSpacing)
        {
            return base.MeasureOverride(availableSize);
        }

        var width = 0d;
        var height = 0d;
        var hasPrevious = false;
        var previousMetrics = ChildMetrics.Empty;
        var childConstraint = new Size(availableSize.Width, double.PositiveInfinity);

        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            child.Measure(childConstraint);

            var desired = child.DesiredSize;
            var metrics = ResolveMetrics(child);

            if (hasPrevious)
            {
                height += CalculateGap(previousMetrics, metrics);
            }

            width = Math.Max(width, desired.Width);
            height += desired.Height;
            previousMetrics = metrics;
            hasPrevious = true;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!UseAdaptiveSpacing)
        {
            return base.ArrangeOverride(finalSize);
        }

        var y = 0d;
        var hasPrevious = false;
        var previousMetrics = ChildMetrics.Empty;

        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            var desired = child.DesiredSize;
            var metrics = ResolveMetrics(child);

            if (hasPrevious)
            {
                y += CalculateGap(previousMetrics, metrics);
            }

            child.Arrange(new Rect(0, y, finalSize.Width, desired.Height));
            y += desired.Height;
            previousMetrics = metrics;
            hasPrevious = true;
        }

        return finalSize;
    }

    private double GetEffectiveHeight(double height)
    {
        return Math.Max(height, ShortRowSlot);
    }

    private bool UseAdaptiveSpacing => Orientation == Orientation.Vertical;

    private double CalculateGap(ChildMetrics previous, ChildMetrics next)
    {
        if (next.LeadingRole == AdaptiveSpacingRole.SupplementaryText)
        {
            return ResolveSupplementaryGap();
        }

        if (previous.TrailingRole is AdaptiveSpacingRole.Toggle or AdaptiveSpacingRole.FieldControl or AdaptiveSpacingRole.Action
            && next.LeadingRole == AdaptiveSpacingRole.Caption
            && !next.IsComposite)
        {
            return ResolveSupplementaryGap();
        }

        if (IsCaptionLike(previous.TrailingRole)
            && next.LeadingRole == AdaptiveSpacingRole.Divider)
        {
            return ResolveSupplementaryGap();
        }

        if (previous.TrailingRole == AdaptiveSpacingRole.SupplementaryText
            && next.LeadingRole is AdaptiveSpacingRole.Toggle or AdaptiveSpacingRole.FieldControl
            && !double.IsNaN(AfterSupplementaryTextGap))
        {
            var explicitGapFloor = -SanitizeNonNegative(ShortRowSlot);
            return Math.Max(AfterSupplementaryTextGap, explicitGapFloor);
        }

        if (previous.TrailingRole == AdaptiveSpacingRole.Caption
            && next.LeadingRole is AdaptiveSpacingRole.FieldControl or AdaptiveSpacingRole.Toggle)
        {
            return ResolveCompactGap(MaxCompactGap);
        }

        if ((previous.TrailingRole == AdaptiveSpacingRole.Toggle
                && next.LeadingRole == AdaptiveSpacingRole.FieldControl)
            || (previous.TrailingRole == AdaptiveSpacingRole.FieldControl
                && next.LeadingRole == AdaptiveSpacingRole.Toggle))
        {
            return ResolveMixedInteractiveGap();
        }

        if (previous.TrailingRole == AdaptiveSpacingRole.Toggle
            && next.LeadingRole == AdaptiveSpacingRole.Toggle)
        {
            return ResolveDependentLeadingGap(next, ResolveCompactGap(MaxToggleCompactGap));
        }

        if (previous.TrailingRole == AdaptiveSpacingRole.Divider)
        {
            return ResolveCompactGap(MaxCompactGap);
        }

        if (IsCaptionLike(previous.TrailingRole)
            && next.LeadingRole == AdaptiveSpacingRole.BodyText)
        {
            return ResolveCompactGap(MaxCompactGap);
        }

        var targetPitch = double.IsNaN(TargetPitch)
            ? ResolveImplicitTargetPitch()
            : Math.Max(0, TargetPitch);
        var gap = targetPitch - ((previous.BottomSlot + next.TopSlot) / 2);

        var min = Math.Max(0, MinGap);
        var max = Math.Max(min, MaxGap);

        return Math.Clamp(gap, min, max);
    }

    private static double ResolveDependentLeadingGap(ChildMetrics next, double gap)
    {
        return next.IsDependentLeading
            ? Math.Max(MinDependentRowGap, gap - DependentRowGapReduction)
            : gap;
    }

    private double ResolveCompactGap(double maxGap)
    {
        var spacing = Math.Max(0, Spacing);
        if (spacing <= 0)
        {
            return DefaultCompactGap;
        }

        return Math.Clamp(spacing, DefaultCompactGap, maxGap);
    }

    private double ResolveMixedInteractiveGap()
    {
        var spacing = Math.Max(0, Spacing);
        if (spacing <= 0)
        {
            return DefaultMixedInteractiveGap;
        }

        return Math.Clamp(spacing + 2, DefaultMixedInteractiveGap, MaxMixedInteractiveGap);
    }

    private double ResolveSupplementaryGap()
    {
        if (!double.IsNaN(SupplementaryTextGap))
        {
            var explicitGapFloor = -SanitizeNonNegative(ShortRowSlot);
            return Math.Max(SupplementaryTextGap, explicitGapFloor);
        }

        var spacing = SanitizeNonNegative(Spacing);
        if (spacing <= 0)
        {
            return DefaultSupplementaryGap;
        }

        return Math.Clamp(spacing, DefaultSupplementaryGap, MaxSupplementaryGap);
    }

    private double ResolveImplicitTargetPitch()
    {
        var spacing = SanitizeNonNegative(Spacing);
        if (spacing <= 0)
        {
            return DefaultTargetPitch;
        }

        if (spacing <= CompactSpacingThreshold)
        {
            return SanitizeNonNegative(ShortRowSlot) + spacing + CompactTargetPitchOffset;
        }

        return DefaultTargetPitch;
    }

    private static double SanitizeNonNegative(double value)
    {
        if (double.IsNaN(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }

    private ChildMetrics ResolveMetrics(Control child)
    {
        if (TryResolveLeafMetrics(child, out var metrics))
        {
            return metrics;
        }

        if (TryResolveCompositeMetrics(child, out metrics))
        {
            return metrics;
        }

        var slot = GetEffectiveHeight(child.DesiredSize.Height);
        return new ChildMetrics(AdaptiveSpacingRole.Generic, AdaptiveSpacingRole.Generic, slot, slot, false, false);
    }

    private bool TryResolveLeafMetrics(Control child, out ChildMetrics metrics)
    {
        if (child is Border border && border.Classes.Contains("settings-page-subtle-divider"))
        {
            metrics = new ChildMetrics(AdaptiveSpacingRole.Divider, AdaptiveSpacingRole.Divider, 0, 0, false, false);
            return true;
        }

        if (child is SettingsInlineRow)
        {
            var slot = Math.Max(FieldControlSlot, child.DesiredSize.Height);
            metrics = new ChildMetrics(AdaptiveSpacingRole.FieldControl, AdaptiveSpacingRole.FieldControl, slot, slot, false, false);
            return true;
        }

        if (child is Grid grid && IsFieldGrid(grid))
        {
            var slot = Math.Max(FieldControlSlot, child.DesiredSize.Height);
            metrics = new ChildMetrics(AdaptiveSpacingRole.FieldControl, AdaptiveSpacingRole.FieldControl, slot, slot, false, false);
            return true;
        }

        if (child is TextBox or ComboBox or NumericUpDown
            or AppTextInput or AppSelect or AppNumberInput
            or AppSuggestInput or AppHistoryInput or AppActionInput
            or AppMultiSelect or AppMultiSelectDropdown
            or VerticalSpinNumberBox)
        {
            var slot = Math.Max(FieldControlSlot, child.DesiredSize.Height);
            metrics = new ChildMetrics(AdaptiveSpacingRole.FieldControl, AdaptiveSpacingRole.FieldControl, slot, slot, false, false);
            return true;
        }

        if (child is CheckBox or AppHintedCheckBox or NullableCheckBox)
        {
            var slot = Math.Max(ToggleSlot, child.DesiredSize.Height);
            metrics = new ChildMetrics(AdaptiveSpacingRole.Toggle, AdaptiveSpacingRole.Toggle, slot, slot, child is SettingsDependentRow, false);
            return true;
        }

        if (child is Button)
        {
            var slot = Math.Max(ActionSlot, child.DesiredSize.Height);
            metrics = new ChildMetrics(AdaptiveSpacingRole.Action, AdaptiveSpacingRole.Action, slot, slot, false, false);
            return true;
        }

        if (child is TextBlock textBlock)
        {
            var role = ResolveTextRole(textBlock);
            var slot = role switch
            {
                AdaptiveSpacingRole.Caption => Math.Max(CaptionSlot, child.DesiredSize.Height),
                AdaptiveSpacingRole.SupplementaryText => Math.Max(SupplementaryTextSlot, child.DesiredSize.Height),
                AdaptiveSpacingRole.BodyText => Math.Max(BodyTextSlot, child.DesiredSize.Height),
                _ => GetEffectiveHeight(child.DesiredSize.Height),
            };

            metrics = new ChildMetrics(role, role, slot, slot, false, false);
            return true;
        }

        metrics = ChildMetrics.Empty;
        return false;
    }

    private bool TryResolveCompositeMetrics(Control child, out ChildMetrics metrics)
    {
        metrics = ChildMetrics.Empty;
        ChildMetrics? first = null;
        ChildMetrics? last = null;

        switch (child)
        {
            case SettingsDependentRow { Content: Control innerChild }:
                if (!innerChild.IsVisible)
                {
                    return false;
                }

                first = ResolveMetrics(innerChild);
                first = first.Value with { IsDependentLeading = true };
                last = first;
                break;

            case Border { Child: Control innerChild }:
                if (!innerChild.IsVisible)
                {
                    return false;
                }

                first = ResolveMetrics(innerChild);
                last = first;
                break;

            case Decorator { Child: Control innerChild }:
                if (!innerChild.IsVisible)
                {
                    return false;
                }

                first = ResolveMetrics(innerChild);
                last = first;
                break;

            case AdaptiveSpacingStackPanel panel:
                ResolvePanelMetrics(panel.Children, ref first, ref last);
                break;

            case StackPanel panel:
                ResolvePanelMetrics(panel.Children, ref first, ref last);
                break;
        }

        if (first is null || last is null)
        {
            return false;
        }

        metrics = new ChildMetrics(
            first.Value.LeadingRole,
            last.Value.TrailingRole,
            first.Value.TopSlot,
            last.Value.BottomSlot,
            first.Value.IsDependentLeading,
            true);
        return true;
    }

    private void ResolvePanelMetrics(global::Avalonia.Controls.Controls children, ref ChildMetrics? first, ref ChildMetrics? last)
    {
        foreach (var child in children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            var metrics = ResolveMetrics(child);
            first ??= metrics;
            last = metrics;
        }
    }

    private static AdaptiveSpacingRole ResolveTextRole(TextBlock textBlock)
    {
        var classes = textBlock.Classes;
        if (classes.Contains("settings-note")
            || classes.Contains("settings-apply-note")
            || classes.Contains("subtle")
            || classes.Contains("settings-centered-text")
            || classes.Contains("state-warning")
            || classes.Contains("state-error"))
        {
            return AdaptiveSpacingRole.SupplementaryText;
        }

        if (classes.Contains("settings-field-caption")
            || classes.Contains("settings-label")
            || classes.Contains("settings-inline-label")
            || classes.Contains("app-caption"))
        {
            return AdaptiveSpacingRole.Caption;
        }

        if (classes.Contains("section-title")
            || classes.Contains("settings-page-title")
            || classes.Contains("app-window-title")
            || classes.Contains("app-window-subtitle")
            || classes.Contains("app-body"))
        {
            return AdaptiveSpacingRole.BodyText;
        }

        return AdaptiveSpacingRole.Generic;
    }

    private static bool IsCaptionLike(AdaptiveSpacingRole role)
    {
        return role is AdaptiveSpacingRole.Caption or AdaptiveSpacingRole.SupplementaryText;
    }

    private static bool IsFieldGrid(Grid grid)
    {
        var classes = grid.Classes;
        return classes.Contains("settings-page-labeled-row")
            || classes.Contains("settings-page-labeled-action-row")
            || classes.Contains("settings-page-paired-row")
            || classes.Contains("settings-page-action-pair");
    }

    private enum AdaptiveSpacingRole
    {
        Generic,
        Caption,
        SupplementaryText,
        BodyText,
        Divider,
        Toggle,
        FieldControl,
        Action,
    }

    private readonly record struct ChildMetrics(
        AdaptiveSpacingRole LeadingRole,
        AdaptiveSpacingRole TrailingRole,
        double TopSlot,
        double BottomSlot,
        bool IsDependentLeading,
        bool IsComposite)
    {
        public static ChildMetrics Empty => new(AdaptiveSpacingRole.Generic, AdaptiveSpacingRole.Generic, 0, 0, false, false);
    }
}
