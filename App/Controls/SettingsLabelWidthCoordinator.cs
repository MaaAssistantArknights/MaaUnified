using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

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

        var measuredWidths = rows
            .Select(FindSettingsLabel)
            .Where(label => label is not null)
            .Select(label => label!.MeasureNaturalLabelWidth());

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

    private static SettingsLabel? FindSettingsLabel(Grid row)
    {
        foreach (var child in row.Children)
        {
            if (Grid.GetColumn(child) == 0 && child is SettingsLabel label)
            {
                return label;
            }
        }

        return null;
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
