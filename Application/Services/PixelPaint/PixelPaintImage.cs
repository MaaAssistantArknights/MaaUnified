namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>
/// 像素画源图的像素缓冲（BGRA32，stride = <see cref="Width"/> × 4）。
/// 与 UI 框架解耦：App 层把解码结果拷成该缓冲，纯算法只在这里读像素。
/// </summary>
public sealed class PixelPaintImage
{
    /// <summary>每像素字节数（BGRA32）。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Initializes a new instance of the <see cref="PixelPaintImage"/> class.
    /// </summary>
    /// <param name="width">图像宽度（像素）。</param>
    /// <param name="height">图像高度（像素）。</param>
    /// <param name="pixels">BGRA32 像素数据，长度必须为 <paramref name="width"/> × <paramref name="height"/> × 4。</param>
    public PixelPaintImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Pixel paint image size must be positive.");
        }

        var expected = checked(width * height * BytesPerPixel);
        if (pixels.Length != expected)
        {
            throw new ArgumentException(
                $"Pixel paint image buffer must be {expected} bytes for {width}×{height} BGRA32, but was {pixels.Length}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Gets 图像宽度（像素）。
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets 图像高度（像素）。
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets BGRA32 像素数据。
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// Gets 行跨度（字节）。
    /// </summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>
    /// 由 BGRA32 数据构造图像缓冲。
    /// </summary>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="bgra">BGRA32 像素数据。</param>
    /// <returns>图像缓冲。</returns>
    public static PixelPaintImage FromBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        return new PixelPaintImage(width, height, bgra.ToArray());
    }

    /// <summary>
    /// 读取某像素的 sRGB 颜色，透明像素与白底合成（透明→白），与 WPF 取样一致。
    /// </summary>
    /// <param name="x">像素 x 坐标。</param>
    /// <param name="y">像素 y 坐标。</param>
    /// <returns>已与白底合成的 sRGB 颜色。</returns>
    public (double R, double G, double B) GetRgb(int x, int y)
    {
        var index = ((y * Width) + x) * BytesPerPixel;
        var alpha = Pixels[index + 3] / 255.0;
        double b = Pixels[index];
        double g = Pixels[index + 1];
        double r = Pixels[index + 2];

        // 透明与白底合成
        r = (r * alpha) + (255 * (1 - alpha));
        g = (g * alpha) + (255 * (1 - alpha));
        b = (b * alpha) + (255 * (1 - alpha));
        return (r, g, b);
    }

    /// <summary>
    /// 在源图上做双线性插值采样，取景区外返回白色（即未绘制格）。
    /// </summary>
    /// <param name="sx">采样 x（浮点像素坐标）。</param>
    /// <param name="sy">采样 y（浮点像素坐标）。</param>
    /// <returns>插值后的 sRGB 颜色。</returns>
    public (double R, double G, double B) SampleBilinear(double sx, double sy)
    {
        if (sx < 0 || sy < 0 || sx >= Width || sy >= Height)
        {
            return (255, 255, 255);
        }

        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var tx = sx - x0;
        var ty = sy - y0;

        var c00 = GetRgb(x0, y0);
        var c10 = GetRgb(x1, y0);
        var c01 = GetRgb(x0, y1);
        var c11 = GetRgb(x1, y1);

        static double Lerp(double a, double b, double t) => a + ((b - a) * t);

        var r = Lerp(Lerp(c00.R, c10.R, tx), Lerp(c01.R, c11.R, tx), ty);
        var g = Lerp(Lerp(c00.G, c10.G, tx), Lerp(c01.G, c11.G, tx), ty);
        var b = Lerp(Lerp(c00.B, c10.B, tx), Lerp(c01.B, c11.B, tx), ty);
        return (r, g, b);
    }

    /// <summary>
    /// 按包围盒裁剪图像，返回新的像素缓冲。
    /// </summary>
    /// <param name="x">起始 x。</param>
    /// <param name="y">起始 y。</param>
    /// <param name="width">裁剪宽度。</param>
    /// <param name="height">裁剪高度。</param>
    /// <returns>裁剪后的图像。</returns>
    public PixelPaintImage Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);

        var cropped = new byte[width * height * BytesPerPixel];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(Pixels, (((y + row) * Width) + x) * BytesPerPixel, cropped, row * width * BytesPerPixel, width * BytesPerPixel);
        }

        return new PixelPaintImage(width, height, cropped);
    }
}
