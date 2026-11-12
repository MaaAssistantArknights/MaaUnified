using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MAAUnified.Application.Services.PixelPaint;

namespace MAAUnified.App.Services.PixelPaint;

/// <summary>
/// 像素画的 Avalonia 侧图像 I/O：解码图片、把 BGRA 缓冲包装成预览位图、把文本渲染成 24×24 源图。
/// 纯算法见 <see cref="PixelPaintConverter"/>；这里只做与 UI 框架相关的位图进出。
/// </summary>
public static class PixelPaintImaging
{
    /// <summary>文本渲染的基础字号下限（像素）。</summary>
    private const float MinTextFontSize = 8f;

    /// <summary>文本渲染的基础字号上限（像素）。</summary>
    private const float MaxTextFontSize = 128f;

    /// <summary>墨迹判定阈值（alpha），与 WPF 的 16 一致。</summary>
    private const byte InkAlphaThreshold = 16;

    /// <summary>
    /// 从文件解码图像为 BGRA 缓冲。
    /// </summary>
    /// <param name="path">图片路径。</param>
    /// <returns>BGRA32 像素缓冲。</returns>
    public static PixelPaintImage LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    /// <summary>
    /// 从流解码图像为 BGRA 缓冲。
    /// </summary>
    /// <param name="stream">图片数据流。</param>
    /// <returns>BGRA32 像素缓冲。</returns>
    public static PixelPaintImage LoadFromStream(Stream stream)
    {
        using var bitmap = new Bitmap(stream);
        return FromBitmap(bitmap);
    }

    /// <summary>
    /// 把 Avalonia 位图拷成 BGRA 缓冲（非预乘 alpha），供纯算法取样。
    /// </summary>
    /// <param name="bitmap">已解码的位图。</param>
    /// <returns>BGRA32 像素缓冲。</returns>
    public static PixelPaintImage FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * PixelPaintImage.BytesPerPixel;
        var pixels = new byte[stride * height];

        using var staging = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = staging.Lock())
        {
            bitmap.CopyPixels(framebuffer, AlphaFormat.Unpremul);
            CopyFramebuffer(framebuffer, pixels, stride, height);
        }

        return new PixelPaintImage(width, height, pixels);
    }

    /// <summary>
    /// 把 BGRA 缓冲包装成可显示的预览位图。
    /// </summary>
    /// <param name="bgra">BGRA32 像素。</param>
    /// <param name="width">位图宽度。</param>
    /// <param name="height">位图高度。</param>
    /// <returns>预览位图。</returns>
    public static WriteableBitmap CreateBitmap(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        var stride = width * PixelPaintImage.BytesPerPixel;
        if (bgra.Length < stride * height)
        {
            throw new ArgumentException("Preview buffer is smaller than the declared size.", nameof(bgra));
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        if (framebuffer.RowBytes == stride)
        {
            Marshal.Copy(bgra, 0, framebuffer.Address, stride * height);
            return bitmap;
        }

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgra, row * stride, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), stride);
        }

        return bitmap;
    }

    /// <summary>
    /// 把最多 4 个字的文本渲染成 24×24 黑字白底位图（WPF <c>PixelPaintHelper.RenderTextToBitmap</c>）：
    /// 中文等宽字素按自适应网格逐格居中绘制；英文/数字单行横排，二分字号使墨迹正好放入后 1:1 居中。
    /// </summary>
    /// <param name="text">输入文本，只取前 4 个字素。</param>
    /// <returns>24×24 BGRA32 源图；文本为空时返回 <see langword="null"/>。</returns>
    public static PixelPaintImage? RenderText(string? text)
    {
        var layout = PixelPaintTextLayout.Create(text);
        if (layout.IsEmpty)
        {
            return null;
        }

        return layout.IsWide ? RenderWideText(layout) : RenderSingleLineText(layout.Text);
    }

    private static PixelPaintImage RenderWideText(PixelPaintTextLayout layout)
    {
        const int size = PixelPaintPalette.GridSize;
        var pixels = CreateWhiteCanvas(size, size);

        using var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            foreach (var glyph in layout.Glyphs)
            {
                // WPF 用 CSS 像素字号 = min(格宽, 格高) 直接绘制点阵，这里保持一致
                var fontSize = Math.Min(glyph.CellWidth, glyph.CellHeight);
                var formatted = CreateFormattedText(glyph.Text, fontSize);
                var x = glyph.X + ((glyph.CellWidth - formatted.Width) / 2);
                var y = glyph.Y + ((glyph.CellHeight - formatted.Height) / 2);
                context.DrawText(formatted, new Point(Math.Round(x), Math.Round(y)));
            }
        }

        CopyBitmapToBgra(target, pixels, size, size);
        return new PixelPaintImage(size, size, pixels);
    }

    private static PixelPaintImage RenderSingleLineText(string text)
    {
        const int size = PixelPaintPalette.GridSize;

        // 二分找最大字号，使裁空白后的墨迹外接框放入 24×24
        var lo = MinTextFontSize;
        var hi = MaxTextFontSize;
        byte[]? bestInk = null;
        var bestWidth = 0;
        var bestHeight = 0;
        for (var i = 0; i < 12; i++)
        {
            var mid = (lo + hi) / 2f;
            var probe = RenderInk(text, mid);
            if (probe is { } ink && ink.Width <= size && ink.Height <= size)
            {
                lo = mid;
                bestInk = ink.Pixels;
                bestWidth = ink.Width;
                bestHeight = ink.Height;
            }
            else
            {
                hi = mid;
            }
        }

        var pixels = CreateWhiteCanvas(size, size);
        if (bestInk is null)
        {
            return new PixelPaintImage(size, size, pixels);
        }

        // 1:1 居中贴到白底上（只做 alpha 混合，不重采样，保持锐利）
        var offsetX = (size - bestWidth) / 2;
        var offsetY = (size - bestHeight) / 2;
        for (var y = 0; y < bestHeight; y++)
        {
            for (var x = 0; x < bestWidth; x++)
            {
                var sourceIndex = ((y * bestWidth) + x) * PixelPaintImage.BytesPerPixel;
                var alpha = bestInk[sourceIndex + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var targetIndex = (((y + offsetY) * size) + x + offsetX) * PixelPaintImage.BytesPerPixel;
                BlendInkPixel(pixels, targetIndex, bestInk[sourceIndex], bestInk[sourceIndex + 1], bestInk[sourceIndex + 2], alpha);
            }
        }

        return new PixelPaintImage(size, size, pixels);
    }

    /// <summary>
    /// 以给定字号渲染文本并裁掉四周空白，返回墨迹位图（透明底）。
    /// </summary>
    /// <param name="text">待绘制文本。</param>
    /// <param name="fontSize">字号（像素）。</param>
    /// <returns>裁空白后的墨迹；无墨迹返回 <see langword="null"/>。</returns>
    private static (byte[] Pixels, int Width, int Height)? RenderInk(string text, float fontSize)
    {
        var formatted = CreateFormattedText(text, fontSize);
        var padding = (int)Math.Ceiling(fontSize * 0.2);
        var width = Math.Max(1, (int)Math.Ceiling(formatted.Width) + (padding * 2));
        var height = Math.Max(1, (int)Math.Ceiling(formatted.Height) + (padding * 2));

        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawText(formatted, new Point(padding, padding));
        }

        var pixels = new byte[width * height * PixelPaintImage.BytesPerPixel];
        CopyBitmapToBgra(target, pixels, width, height);
        return TrimInk(pixels, width, height);
    }

    /// <summary>
    /// 扫描 alpha 通道取墨迹外接框并裁掉四周空白。
    /// </summary>
    /// <param name="pixels">BGRA 像素。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <returns>裁剪后的墨迹；无墨迹返回 <see langword="null"/>。</returns>
    private static (byte[] Pixels, int Width, int Height)? TrimInk(byte[] pixels, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[(((y * width) + x) * PixelPaintImage.BytesPerPixel) + 3] <= InkAlphaThreshold)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        var inkWidth = maxX - minX + 1;
        var inkHeight = maxY - minY + 1;
        var ink = new byte[inkWidth * inkHeight * PixelPaintImage.BytesPerPixel];
        for (var y = 0; y < inkHeight; y++)
        {
            Buffer.BlockCopy(
                pixels,
                (((y + minY) * width) + minX) * PixelPaintImage.BytesPerPixel,
                ink,
                y * inkWidth * PixelPaintImage.BytesPerPixel,
                inkWidth * PixelPaintImage.BytesPerPixel);
        }

        return (ink, inkWidth, inkHeight);
    }

    private static FormattedText CreateFormattedText(string text, double fontSize)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            fontSize,
            Brushes.Black);
    }

    private static byte[] CreateWhiteCanvas(int width, int height)
    {
        var pixels = new byte[width * height * PixelPaintImage.BytesPerPixel];
        for (var i = 0; i < pixels.Length; i += PixelPaintImage.BytesPerPixel)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// 把黑色墨迹按 alpha 混合到白底像素上（BGR 三通道同值，直接按 alpha 压暗）。
    /// </summary>
    /// <param name="destination">目标 BGRA 像素。</param>
    /// <param name="index">目标像素下标。</param>
    /// <param name="b">墨迹蓝分量。</param>
    /// <param name="g">墨迹绿分量。</param>
    /// <param name="r">墨迹红分量。</param>
    /// <param name="alpha">墨迹 alpha。</param>
    private static void BlendInkPixel(byte[] destination, int index, byte b, byte g, byte r, byte alpha)
    {
        var a = alpha / 255.0;
        destination[index] = (byte)Math.Clamp((b * a) + (destination[index] * (1 - a)), 0, 255);
        destination[index + 1] = (byte)Math.Clamp((g * a) + (destination[index + 1] * (1 - a)), 0, 255);
        destination[index + 2] = (byte)Math.Clamp((r * a) + (destination[index + 2] * (1 - a)), 0, 255);
        destination[index + 3] = 255;
    }

    private static void CopyBitmapToBgra(Bitmap source, byte[] destination, int width, int height)
    {
        var stride = width * PixelPaintImage.BytesPerPixel;
        using var staging = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = staging.Lock();
        source.CopyPixels(framebuffer, AlphaFormat.Unpremul);
        CopyFramebuffer(framebuffer, destination, stride, height);
    }

    private static void CopyFramebuffer(ILockedFramebuffer framebuffer, byte[] destination, int stride, int height)
    {
        if (framebuffer.RowBytes == stride)
        {
            Marshal.Copy(framebuffer.Address, destination, 0, stride * height);
            return;
        }

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), destination, row * stride, stride);
        }
    }
}
