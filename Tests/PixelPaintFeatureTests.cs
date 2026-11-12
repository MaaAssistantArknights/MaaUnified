using System.Text.Json.Nodes;
using MAAUnified.Application.Services.PixelPaint;
using Xunit;

namespace MAAUnified.Tests;

/// <summary>
/// 自动像素画（WPF 1f39c29587 / d74158a121 / 03386a0ed5 等）的纯函数契约：
/// 色板 OKLab、文本排版、构图/抖动、参数序列化与视口变换。
/// </summary>
public sealed class PixelPaintFeatureTests
{
    private static PixelPaintImage SolidImage(byte r, byte g, byte b, int width = 48, int height = 48)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return new PixelPaintImage(width, height, pixels);
    }

    [Fact]
    public void Palette_ShouldExposeWpfColorCountAndOklabDistance()
    {
        Assert.Equal(40, PixelPaintPalette.ColorCount);
        Assert.Equal(24, PixelPaintPalette.GridSize);
        Assert.Equal(3, PixelPaintPalette.WhiteColorIndex);

        var white = PixelPaintPalette.Srgb8ToOklab(255, 255, 255);
        var black = PixelPaintPalette.Srgb8ToOklab(0, 0, 0);
        Assert.InRange(white.L, 0.99, 1.01);
        Assert.InRange(black.L, -0.01, 0.01);
        Assert.True(PixelPaintPalette.OklabSqDistance(white, black) > 0.9);

        var near = PixelPaintPalette.ColorDistance((255, 0, 0), (250, 4, 2));
        var far = PixelPaintPalette.ColorDistance((255, 0, 0), (0, 0, 255));
        Assert.True(near < far);
    }

    [Fact]
    public void TextLayout_ShouldKeepAtMostFourGraphemesAndSplitWideTextIntoGrid()
    {
        var ascii = PixelPaintTextLayout.Create("Ab 1");
        Assert.False(ascii.IsWide);
        Assert.Equal("Ab 1", ascii.Text);
        Assert.Single(ascii.Glyphs);

        var wide = PixelPaintTextLayout.Create("中文测试");
        Assert.True(wide.IsWide);
        Assert.Equal(4, wide.Glyphs.Count);
        Assert.Equal(2, wide.Columns);
        Assert.Equal(2, wide.Rows);
        Assert.All(wide.Glyphs, glyph => Assert.Equal(12, glyph.CellWidth));

        var truncated = PixelPaintTextLayout.Create("😀😀😀😀😀");
        Assert.Equal(PixelPaintTextLayout.MaxTextElements, truncated.Glyphs.Count);
        Assert.Equal("😀😀😀😀", truncated.Text);
        Assert.True(PixelPaintTextLayout.Create(null).IsEmpty);
    }

    [Fact]
    public void Converter_ShouldEmit24GridAndSkipWhite()
    {
        var white = PixelPaintConverter.Convert(
            PixelPaintConverter.Prepare(SolidImage(255, 255, 255)),
            new PixelPaintConvertOptions { Dither = PixelPaintDitherMode.Illustration });
        Assert.Equal(24, white.Matrix.GetLength(0));
        Assert.Equal(24, white.Matrix.GetLength(1));
        Assert.Empty(white.Groups);
        Assert.Equal(0, white.PaintedCellCount);
        Assert.Equal(PixelPaintPalette.PreviewPixelSize * PixelPaintPalette.PreviewPixelSize * 4, white.PreviewBgra.Length);

        var red = PixelPaintConverter.Convert(
            PixelPaintConverter.Prepare(SolidImage(220, 20, 20)),
            new PixelPaintConvertOptions { Dither = PixelPaintDitherMode.Illustration });
        Assert.NotEmpty(red.Groups);
        Assert.Equal(576, red.PaintedCellCount);
        Assert.All(red.Groups, group =>
        {
            Assert.InRange(group.Color, 0, PixelPaintPalette.ColorCount - 1);
            Assert.NotEmpty(group.Points);
        });
    }

    [Theory]
    [InlineData(PixelPaintFitMode.Crop)]
    [InlineData(PixelPaintFitMode.Contain)]
    [InlineData(PixelPaintFitMode.Stretch)]
    public void Converter_ShouldHonorFitModes(PixelPaintFitMode fit)
    {
        var image = PixelPaintConverter.Prepare(SolidImage(10, 120, 200, width: 96, height: 32));
        var result = PixelPaintConverter.Convert(
            image,
            new PixelPaintConvertOptions { Fit = fit, Dither = PixelPaintDitherMode.Illustration, TrimEmptyBorder = false });
        Assert.Equal(24, result.Matrix.GetLength(0));
        Assert.NotEmpty(result.Groups);
    }

    [Fact]
    public void TaskPayload_ShouldBuildCoreCustomTaskAndValidateBounds()
    {
        var groups = new[]
        {
            new PixelPaintColorGroup
            {
                Color = 4,
                Points = [new PixelPaintPoint(0, 0), new PixelPaintPoint(1, 0)],
            },
        };

        Assert.Equal(PixelPaintPayloadIssue.None, PixelPaintTaskPayload.Validate(groups, 20));
        var payload = PixelPaintTaskPayload.Build(groups, swipe: true, gridDelay: 20);
        var taskNames = Assert.IsType<JsonArray>(payload["task_names"]);
        Assert.Equal("MiniGame@PixelPaint@Begin", taskNames[0]!.GetValue<string>());
        var paint = Assert.IsType<JsonObject>(payload["params"]!["pixel_paint"]);
        Assert.True(paint["swipe"]!.GetValue<bool>());
        Assert.Equal(20, paint["grid_delay"]!.GetValue<int>());
        var groupsJson = Assert.IsType<JsonArray>(paint["groups"]);
        Assert.Single(groupsJson);

        Assert.Equal(PixelPaintPayloadIssue.NoGroups, PixelPaintTaskPayload.Validate([], 0));
        Assert.Equal(
            PixelPaintPayloadIssue.GridDelayOutOfRange,
            PixelPaintTaskPayload.Validate(groups, PixelPaintTaskPayload.MaxGridDelay + 1));
        Assert.Equal(
            PixelPaintPayloadIssue.ColorOutOfRange,
            PixelPaintTaskPayload.Validate([new PixelPaintColorGroup { Color = 40, Points = [new PixelPaintPoint(0, 0)] }], 0));
        Assert.Equal(
            PixelPaintPayloadIssue.PointOutOfRange,
            PixelPaintTaskPayload.Validate([new PixelPaintColorGroup { Color = 1, Points = [new PixelPaintPoint(24, 0)] }], 0));
    }

    [Fact]
    public void ViewRect_ShouldNormalizeZoomAndPanWithinBounds()
    {
        Assert.Equal(PixelPaintViewRect.Full, new PixelPaintViewRect(0, 0, 1, 1).Normalize());
        Assert.Equal(PixelPaintViewRect.Full, new PixelPaintViewRect(-1, -1, 2, 2).Normalize());
        Assert.True(new PixelPaintViewRect(0.5, 0.5, 0.2, 0.2).Zoom(0.01).Width >= PixelPaintViewRect.MinSize);

        var zoomed = PixelPaintViewRect.Full.Zoom(0.5);
        Assert.Equal(0.5, zoomed.Width, 6);
        var panned = zoomed.Pan(10, 10);
        Assert.True(panned.X >= 0 && panned.X <= 1 - panned.Width);
        Assert.True(panned.Y >= 0 && panned.Y <= 1 - panned.Height);
    }
}
