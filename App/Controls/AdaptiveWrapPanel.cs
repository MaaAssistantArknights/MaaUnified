using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public sealed class AdaptiveWrapPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, double>(nameof(ItemWidth), 160);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, double>(nameof(ItemHeight), double.NaN);

    public static readonly StyledProperty<double> MinColumnGapProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, double>(nameof(MinColumnGap), 12);

    public static readonly StyledProperty<double> MaxColumnGapProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, double>(nameof(MaxColumnGap), 28);

    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, double>(nameof(RowGap), 14);

    public static readonly StyledProperty<Thickness> EdgeInsetProperty =
        AvaloniaProperty.Register<AdaptiveWrapPanel, Thickness>(nameof(EdgeInset), new Thickness(8, 4, 8, 12));

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double MinColumnGap
    {
        get => GetValue(MinColumnGapProperty);
        set => SetValue(MinColumnGapProperty, value);
    }

    public double MaxColumnGap
    {
        get => GetValue(MaxColumnGapProperty);
        set => SetValue(MaxColumnGapProperty, value);
    }

    public double RowGap
    {
        get => GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public Thickness EdgeInset
    {
        get => GetValue(EdgeInsetProperty);
        set => SetValue(EdgeInsetProperty, value);
    }

    private double _measuredItemWidth = 1d;

    static AdaptiveWrapPanel()
    {
        AffectsMeasure<AdaptiveWrapPanel>(
            ItemWidthProperty,
            ItemHeightProperty,
            MinColumnGapProperty,
            MaxColumnGapProperty,
            RowGapProperty,
            EdgeInsetProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visibleChildren = Children.Where(child => child.IsVisible).ToArray();
        if (visibleChildren.Length == 0)
        {
            _measuredItemWidth = ResolveBaseItemWidth();
            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, EdgeInset.Top + EdgeInset.Bottom);
        }

        var itemWidth = MeasureChildrenAndResolveItemWidth(visibleChildren);
        var fixedItemHeight = ResolveFixedItemHeight();
        _measuredItemWidth = itemWidth;
        var availableWidth = double.IsInfinity(availableSize.Width)
            ? itemWidth + EdgeInset.Left + EdgeInset.Right
            : availableSize.Width;
        var columns = ResolveColumnCount(availableWidth, itemWidth);
        if (fixedItemHeight is { } itemHeight)
        {
            var rows = (int)Math.Ceiling(visibleChildren.Length / (double)columns);
            var fixedTotalHeight = EdgeInset.Top
                + EdgeInset.Bottom
                + (rows * itemHeight)
                + (Math.Max(0, rows - 1) * Math.Max(0, RowGap));
            return new Size(availableWidth, fixedTotalHeight);
        }

        var rowCount = 0;
        var column = 0;
        var rowHeight = 0d;
        var totalHeight = EdgeInset.Top + EdgeInset.Bottom;

        foreach (var child in visibleChildren)
        {
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            column++;

            if (column < columns)
            {
                continue;
            }

            totalHeight += rowHeight;
            rowCount++;
            column = 0;
            rowHeight = 0;
        }

        if (column > 0)
        {
            totalHeight += rowHeight;
            rowCount++;
        }

        totalHeight += Math.Max(0, rowCount - 1) * Math.Max(0, RowGap);
        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visibleChildren = Children.Where(child => child.IsVisible).ToArray();
        if (visibleChildren.Length == 0)
        {
            return finalSize;
        }

        var itemWidth = ResolveMeasuredItemWidth(visibleChildren);
        var columns = ResolveColumnCount(finalSize.Width, itemWidth);
        var columnGap = ResolveColumnGap(finalSize.Width, itemWidth, columns);
        var rowGap = Math.Max(0, RowGap);
        var fixedItemHeight = ResolveFixedItemHeight();
        var y = EdgeInset.Top;

        for (var start = 0; start < visibleChildren.Length; start += columns)
        {
            var count = Math.Min(columns, visibleChildren.Length - start);
            var rowHeight = fixedItemHeight ?? 0d;
            if (fixedItemHeight is null)
            {
                for (var i = 0; i < count; i++)
                {
                    rowHeight = Math.Max(rowHeight, visibleChildren[start + i].DesiredSize.Height);
                }
            }

            var x = EdgeInset.Left;

            for (var i = 0; i < count; i++)
            {
                var child = visibleChildren[start + i];
                child.Arrange(new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth + columnGap;
            }

            y += rowHeight + rowGap;
        }

        return finalSize;
    }

    private double ResolveBaseItemWidth()
    {
        return Math.Max(1, ItemWidth);
    }

    private double? ResolveFixedItemHeight()
    {
        return double.IsFinite(ItemHeight) && ItemHeight > 0d
            ? ItemHeight
            : null;
    }

    private double MeasureChildrenAndResolveItemWidth(IReadOnlyCollection<Control> visibleChildren)
    {
        var itemWidth = ResolveBaseItemWidth();
        var itemHeight = ResolveFixedItemHeight() ?? double.PositiveInfinity;
        var constraint = new Size(itemWidth, itemHeight);
        var expanded = false;
        foreach (var child in visibleChildren)
        {
            child.Measure(constraint);
            var resolvedWidth = Math.Max(child.MinWidth, child.DesiredSize.Width);
            if (resolvedWidth > itemWidth)
            {
                expanded = true;
            }

            itemWidth = Math.Max(itemWidth, child.MinWidth);
            itemWidth = Math.Max(itemWidth, child.DesiredSize.Width);
        }

        if (expanded)
        {
            constraint = new Size(itemWidth, itemHeight);
            foreach (var child in visibleChildren)
            {
                child.Measure(constraint);
            }
        }

        return itemWidth;
    }

    private double ResolveMeasuredItemWidth(IReadOnlyCollection<Control> visibleChildren)
    {
        var itemWidth = Math.Max(ResolveBaseItemWidth(), _measuredItemWidth);
        foreach (var child in visibleChildren)
        {
            itemWidth = Math.Max(itemWidth, child.MinWidth);
            itemWidth = Math.Max(itemWidth, child.DesiredSize.Width);
        }

        return itemWidth;
    }

    private int ResolveColumnCount(double availableWidth, double itemWidth)
    {
        var contentWidth = Math.Max(1, availableWidth - EdgeInset.Left - EdgeInset.Right);
        var minGap = Math.Max(0, MinColumnGap);
        return Math.Max(1, (int)Math.Floor((contentWidth + minGap) / (itemWidth + minGap)));
    }

    private double ResolveColumnGap(double availableWidth, double itemWidth, int columns)
    {
        if (columns <= 1)
        {
            return 0;
        }

        var minGap = Math.Max(0, MinColumnGap);
        var maxGap = Math.Max(minGap, MaxColumnGap);
        if (Math.Abs(maxGap - minGap) < 0.01d)
        {
            return minGap;
        }

        var contentWidth = Math.Max(1, availableWidth - EdgeInset.Left - EdgeInset.Right);
        var naturalGap = (contentWidth - (columns * itemWidth)) / (columns - 1);
        return Math.Clamp(naturalGap, minGap, maxGap);
    }
}
