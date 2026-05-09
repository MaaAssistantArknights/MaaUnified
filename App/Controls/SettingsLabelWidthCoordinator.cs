using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

public sealed class SettingsLabelWidthCoordinator
{
    internal const double DefaultMaxLabelWidth = 220d;
    internal const double DefaultFieldGap = 12d;
    private static readonly List<WeakReference<Grid>> RegisteredRows = [];
    private static readonly HashSet<string> PendingGroups = new(StringComparer.Ordinal);

    public static readonly AttachedProperty<string?> GroupKeyProperty =
        AvaloniaProperty.RegisterAttached<SettingsLabelWidthCoordinator, Grid, string?>("GroupKey");

    public static readonly AttachedProperty<double> MaxLabelWidthProperty =
        AvaloniaProperty.RegisterAttached<SettingsLabelWidthCoordinator, Grid, double>(
            "MaxLabelWidth",
            DefaultMaxLabelWidth);

    public static readonly AttachedProperty<double> FieldGapProperty =
        AvaloniaProperty.RegisterAttached<SettingsLabelWidthCoordinator, Grid, double>(
            "FieldGap",
            DefaultFieldGap);

    static SettingsLabelWidthCoordinator()
    {
        GroupKeyProperty.Changed.AddClassHandler<Grid>((grid, _) => OnRowGroupingChanged(grid));
        MaxLabelWidthProperty.Changed.AddClassHandler<Grid>((grid, _) => ScheduleRecalculate(grid));
        FieldGapProperty.Changed.AddClassHandler<Grid>((grid, _) => ScheduleRecalculate(grid));
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, _) => OnVisibilityChanged(control));
    }

    public static string? GetGroupKey(Grid element)
    {
        return element.GetValue(GroupKeyProperty);
    }

    public static void SetGroupKey(Grid element, string? value)
    {
        element.SetValue(GroupKeyProperty, value);
    }

    public static double GetMaxLabelWidth(Grid element)
    {
        return element.GetValue(MaxLabelWidthProperty);
    }

    public static void SetMaxLabelWidth(Grid element, double value)
    {
        element.SetValue(MaxLabelWidthProperty, value);
    }

    public static double GetFieldGap(Grid element)
    {
        return element.GetValue(FieldGapProperty);
    }

    public static void SetFieldGap(Grid element, double value)
    {
        element.SetValue(FieldGapProperty, value);
    }

    public static void InvalidateNearestGroup(Control control)
    {
        var row = FindGroupedRow(control);
        if (row is not null)
        {
            ScheduleRecalculate(row);
        }
    }

    private static void OnRowGroupingChanged(Grid row)
    {
        EnsureRegistered(row);
        ScheduleRecalculate(row);
    }

    private static void EnsureRegistered(Grid row)
    {
        PruneRegistry();
        if (!RegisteredRows.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, row)))
        {
            RegisteredRows.Add(new WeakReference<Grid>(row));
            row.AttachedToVisualTree += OnRowAttachedToVisualTree;
            row.DetachedFromVisualTree += OnRowDetachedFromVisualTree;
        }
    }

    private static void OnRowAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid row)
        {
            ScheduleRecalculate(row);
        }
    }

    private static void OnRowDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid row)
        {
            ScheduleRecalculate(row);
        }
    }

    private static void OnVisibilityChanged(Control control)
    {
        if (control is Grid row && !string.IsNullOrWhiteSpace(GetGroupKey(row)))
        {
            ScheduleRecalculate(row);
        }

        foreach (var groupedRow in control
                     .GetVisualDescendants()
                     .OfType<Grid>()
                     .Where(row => !string.IsNullOrWhiteSpace(GetGroupKey(row))))
        {
            ScheduleRecalculate(groupedRow);
        }
    }

    private static void ScheduleRecalculate(Grid row)
    {
        var key = GetGroupKey(row);
        var scope = FindScope(row);
        if (string.IsNullOrWhiteSpace(key) || scope is null)
        {
            return;
        }

        var pendingKey = $"{scope.GetHashCode():X}:{key}";
        if (!PendingGroups.Add(pendingKey))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                PendingGroups.Remove(pendingKey);
                Recalculate(scope, key);
            },
            DispatcherPriority.Loaded);
    }

    private static void Recalculate(Control scope, string groupKey)
    {
        PruneRegistry();
        var rows = RegisteredRows
            .Select(reference => reference.TryGetTarget(out var row) ? row : null)
            .OfType<Grid>()
            .Where(row => string.Equals(GetGroupKey(row), groupKey, StringComparison.Ordinal)
                && row.IsEffectivelyVisible
                && ReferenceEquals(FindScope(row), scope))
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        var maxLabelWidth = rows
            .Select(GetMaxLabelWidth)
            .Where(width => width > 0 && !double.IsNaN(width))
            .DefaultIfEmpty(DefaultMaxLabelWidth)
            .Min();

        var measuredWidths = rows.SelectMany(MeasureNaturalLabelWidths);

        var fieldGap = rows
            .Select(GetFieldGap)
            .Where(width => width >= 0 && !double.IsNaN(width))
            .DefaultIfEmpty(DefaultFieldGap)
            .Max();

        var labelWidth = CalculateSharedLabelWidth(measuredWidths, maxLabelWidth, fieldGap);
        foreach (var row in rows)
        {
            ApplyLabelWidth(row, labelWidth);
        }
    }

    internal static double CalculateSharedLabelWidth(IEnumerable<double> measuredWidths, double maxLabelWidth)
    {
        return CalculateSharedLabelWidth(measuredWidths, maxLabelWidth, DefaultFieldGap);
    }

    internal static double CalculateSharedLabelWidth(IEnumerable<double> measuredWidths, double maxLabelWidth, double fieldGap)
    {
        var safeMax = maxLabelWidth > 0 && !double.IsNaN(maxLabelWidth)
            ? maxLabelWidth
            : DefaultMaxLabelWidth;
        var safeGap = fieldGap >= 0 && !double.IsNaN(fieldGap)
            ? fieldGap
            : DefaultFieldGap;
        var measuredWidth = measuredWidths
            .Where(width => width > 0 && !double.IsNaN(width))
            .DefaultIfEmpty(0d)
            .Max();

        return Math.Clamp(Math.Ceiling(measuredWidth), 0d, safeMax) + safeGap;
    }

    private static void ApplyLabelWidth(Grid row, double width)
    {
        if (row is SettingsInlineRow inlineRow)
        {
            var inlineLabelWidth = Math.Max(0d, width - Math.Max(0d, inlineRow.GapWidth));
            if (Math.Abs(inlineRow.LabelWidth - inlineLabelWidth) > 0.5)
            {
                inlineRow.LabelWidth = inlineLabelWidth;
            }

            return;
        }

        if (row.ColumnDefinitions.Count == 0)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }

        var current = row.ColumnDefinitions[0].Width;
        if (!current.IsAbsolute || Math.Abs(current.Value - width) > 0.5)
        {
            row.ColumnDefinitions[0].Width = new GridLength(width);
        }
    }

    private static IEnumerable<double> MeasureNaturalLabelWidths(Grid row)
    {
        var children = row is SettingsInlineRow
            ? GetInlineRowLabelChildren(row)
            : row.Children
                .Where(child => child.IsEffectivelyVisible && Grid.GetColumn(child) == 0)
                .OfType<Control>();

        foreach (var child in children)
        {
            var measuredWidth = MeasureNaturalLabelWidth(child);
            if (measuredWidth > 0d && !double.IsNaN(measuredWidth))
            {
                yield return measuredWidth;
            }
        }
    }

    private static IEnumerable<Control> GetInlineRowLabelChildren(Grid row)
    {
        var visibleIndex = 0;
        foreach (var child in row.Children.OfType<Control>())
        {
            if (!child.IsEffectivelyVisible)
            {
                continue;
            }

            if (visibleIndex % 2 == 0)
            {
                yield return child;
            }

            visibleIndex++;
        }
    }

    private static double MeasureNaturalLabelWidth(Control label)
    {
        if (label is SettingsLabel settingsLabel)
        {
            return settingsLabel.MeasureNaturalLabelWidth();
        }

        if (label is TextBlock textBlock)
        {
            return MeasureTextBlockNaturalWidth(textBlock);
        }

        try
        {
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(label.DesiredSize.Width);
        }
        catch (InvalidOperationException)
        {
            return 0d;
        }
    }

    private static double MeasureTextBlockNaturalWidth(TextBlock textBlock)
    {
        var previousMaxWidth = textBlock.MaxWidth;
        try
        {
            textBlock.MaxWidth = double.PositiveInfinity;
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(textBlock.DesiredSize.Width);
        }
        catch (InvalidOperationException)
        {
            return EstimateTextWidth(textBlock.Text, textBlock.FontSize);
        }
        finally
        {
            textBlock.MaxWidth = previousMaxWidth;
        }
    }

    private static double EstimateTextWidth(string? text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0d;
        }

        var safeFontSize = fontSize > 0d && !double.IsNaN(fontSize) ? fontSize : 13.5d;
        var units = text.Sum(static ch => ch <= 0x007F ? 0.56d : 1d);
        return Math.Ceiling(units * safeFontSize);
    }

    private static Grid? FindGroupedRow(Control control)
    {
        for (var current = control.Parent; current is not null; current = current.Parent)
        {
            if (current is Grid grid && !string.IsNullOrWhiteSpace(GetGroupKey(grid)))
            {
                return grid;
            }
        }

        return null;
    }

    private static Control? FindScope(Control control)
    {
        for (var current = control as Control; current is not null; current = current.Parent as Control)
        {
            if (current is UserControl)
            {
                return current;
            }
        }

        return null;
    }

    private static void PruneRegistry()
    {
        RegisteredRows.RemoveAll(reference => !reference.TryGetTarget(out _));
    }
}
