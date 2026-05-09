using Avalonia;
using Avalonia.Controls;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class AdaptiveWrapPanelTests
{
    [Fact]
    public void Arrange_ShouldDistributeRemainingWidthBetweenCards()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 100,
            MinColumnGap = 12,
            MaxColumnGap = 40,
            RowGap = 16,
            EdgeInset = new Thickness(8, 4, 8, 12),
        };

        var first = new FixedBorder(100, 40);
        var second = new FixedBorder(100, 40);
        var third = new FixedBorder(100, 40);
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(third);

        Layout(panel, 356);

        var firstGap = second.Bounds.Left - first.Bounds.Right;
        var secondGap = third.Bounds.Left - second.Bounds.Right;

        Assert.Equal(firstGap, secondGap);
        Assert.InRange(firstGap, 20, 40);
        Assert.Equal(8, first.Bounds.Left);
    }

    [Fact]
    public void Measure_ShouldReserveBottomInsetForCardShadow()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 100,
            MinColumnGap = 12,
            RowGap = 16,
            EdgeInset = new Thickness(8, 4, 8, 18),
        };

        panel.Children.Add(new FixedBorder(100, 40));
        panel.Children.Add(new FixedBorder(100, 40));
        panel.Children.Add(new FixedBorder(100, 40));

        Layout(panel, 230);

        Assert.True(panel.DesiredSize.Height >= 118);
        Assert.True(panel.Bounds.Height >= panel.DesiredSize.Height);
    }

    [Fact]
    public void Arrange_ShouldNotCompressCardsBelowDesiredWidth()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 120,
            MinColumnGap = 16,
            RowGap = 16,
            EdgeInset = new Thickness(8, 4, 8, 12),
        };

        var first = new FixedBorder(120, 40) { MinWidth = 178 };
        var second = new FixedBorder(120, 40) { MinWidth = 178 };
        panel.Children.Add(first);
        panel.Children.Add(second);

        Layout(panel, 420);

        Assert.True(first.Bounds.Width >= 178);
        Assert.True(second.Bounds.Width >= 178);
        Assert.True(second.Bounds.Left - first.Bounds.Right >= 16);
    }

    [Fact]
    public void Arrange_ShouldKeepRowsLeftAligned()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 100,
            MinColumnGap = 12,
            RowGap = 16,
            EdgeInset = new Thickness(10, 4, 10, 12),
        };

        panel.Children.Add(new FixedBorder(100, 40));
        panel.Children.Add(new FixedBorder(100, 40));

        Layout(panel, 360);

        Assert.Equal(10, panel.Children[0].Bounds.Left);
    }

    [Fact]
    public void Arrange_ShouldKeepFixedGapWhenMinAndMaxGapMatch()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 100,
            MinColumnGap = 16,
            MaxColumnGap = 16,
            RowGap = 16,
            EdgeInset = new Thickness(8, 4, 8, 12),
        };

        var first = new FixedBorder(100, 40);
        var second = new FixedBorder(100, 40);
        var third = new FixedBorder(100, 40);
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(third);

        Layout(panel, 380);

        Assert.Equal(16, second.Bounds.Left - first.Bounds.Right);
        Assert.Equal(16, third.Bounds.Left - second.Bounds.Right);
    }

    [Fact]
    public void Measure_ShouldUseFixedItemHeightWhenProvided()
    {
        var panel = new AdaptiveWrapPanel
        {
            ItemWidth = 100,
            ItemHeight = 48,
            MinColumnGap = 16,
            MaxColumnGap = 16,
            RowGap = 12,
            EdgeInset = new Thickness(8, 4, 8, 10),
        };

        panel.Children.Add(new FixedBorder(100, 30));
        panel.Children.Add(new FixedBorder(100, 70));
        panel.Children.Add(new FixedBorder(100, 30));

        Layout(panel, 240);

        Assert.Equal(122, panel.DesiredSize.Height);
        Assert.Equal(48, panel.Children[0].Bounds.Height);
        Assert.Equal(48, panel.Children[2].Bounds.Height);
    }

    private static void Layout(Control control, double width)
    {
        var size = new Size(width, 500);
        control.Measure(size);
        control.Arrange(new Rect(size));
    }

    private sealed class FixedBorder : Border
    {
        private readonly double _width;
        private readonly double _height;

        public FixedBorder(double width, double height)
        {
            _width = width;
            _height = height;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(_width, _height);
        }
    }
}
