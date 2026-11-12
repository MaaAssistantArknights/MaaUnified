namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>原图到 24×24 网格的构图方式（WPF <c>PixelPaintHelper.FitMode</c>）。</summary>
public enum PixelPaintFitMode
{
    /// <summary>裁剪填充（cover）：取源图最大 1:1 区域铺满网格，裁掉超出部分。</summary>
    Crop,

    /// <summary>完整包含（letterbox）：整张源图等比装入网格，两侧留白。</summary>
    Contain,

    /// <summary>拉伸：忽略宽高比，强行铺满网格。</summary>
    Stretch,
}

/// <summary>量化抖动方式（WPF <c>PixelPaintHelper.DitherMode</c>）。</summary>
public enum PixelPaintDitherMode
{
    /// <summary>不抖动：纯最近邻量化，无误差扩散。</summary>
    None,

    /// <summary>Floyd-Steinberg（蛇形扫描，部分误差扩散）：适合照片等渐变内容。</summary>
    FloydSteinberg,

    /// <summary>Atkinson：仅扩散 6/8 误差，噪点较 FS 更柔和。</summary>
    Atkinson,

    /// <summary>插画优先：medoid 代表色 + 边缘感知 MRF，保留干净色块、压交界噪点。</summary>
    Illustration,
}

/// <summary>
/// 取景区：相对 ｢去边后内容图｣ 的归一化矩形（0~1），左上为原点。
/// 滚轮缩放、拖动平移均以该矩形表达（WPF <c>ToolboxViewModel._pixelPaintView</c>）。
/// </summary>
/// <param name="X">左边界（0~1）。</param>
/// <param name="Y">上边界（0~1）。</param>
/// <param name="Width">宽度（0~1）。</param>
/// <param name="Height">高度（0~1）。</param>
public readonly record struct PixelPaintViewRect(double X, double Y, double Width, double Height)
{
    /// <summary>取景区最小边长比例，与 WPF 的滚轮缩放上下限一致。</summary>
    public const double MinSize = 0.05;

    /// <summary>完整取景（整张内容图）。</summary>
    public static PixelPaintViewRect Full => new(0, 0, 1, 1);

    /// <summary>
    /// 把取景矩形钳制到合法范围 [0,1]，并保证宽高下限不为零。
    /// </summary>
    /// <returns>钳制后的合法矩形。</returns>
    public PixelPaintViewRect Normalize()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var w = Math.Clamp(Width, 1e-4, Math.Max(1e-4, 1 - x));
        var h = Math.Clamp(Height, 1e-4, Math.Max(1e-4, 1 - y));
        return new PixelPaintViewRect(x, y, w, h);
    }

    /// <summary>
    /// 以中心为锚点缩放取景，向上滚动（<paramref name="factor"/> &lt; 1）放大取景区域。
    /// 宽高钳制在 [<see cref="MinSize"/>, 1]，越界后平移回图内。
    /// </summary>
    /// <param name="factor">缩放系数，1 表示不变。</param>
    /// <returns>缩放后的取景矩形。</returns>
    public PixelPaintViewRect Zoom(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return Normalize();
        }

        var current = Normalize();
        var centerX = current.X + (current.Width / 2);
        var centerY = current.Y + (current.Height / 2);
        var width = Math.Clamp(current.Width * factor, MinSize, 1.0);
        var height = Math.Clamp(current.Height * factor, MinSize, 1.0);
        var x = Math.Clamp(centerX - (width / 2), 0, 1 - width);
        var y = Math.Clamp(centerY - (height / 2), 0, 1 - height);
        return new PixelPaintViewRect(x, y, width, height);
    }

    /// <summary>
    /// 拖动平移：内容跟随指针移动，<paramref name="deltaX"/>/<paramref name="deltaY"/> 为
    /// ｢指针位移 / 预览边长｣ 的归一化位移，内部按取景宽度换算回图内坐标。
    /// </summary>
    /// <param name="deltaX">指针水平位移（预览边长的比例，右为正）。</param>
    /// <param name="deltaY">指针垂直位移（预览边长的比例，下为正）。</param>
    /// <returns>平移后的取景矩形。</returns>
    public PixelPaintViewRect Pan(double deltaX, double deltaY)
    {
        var current = Normalize();
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            return current;
        }

        var x = Math.Clamp(current.X - (deltaX * current.Width), 0, 1 - current.Width);
        var y = Math.Clamp(current.Y - (deltaY * current.Height), 0, 1 - current.Height);
        return new PixelPaintViewRect(x, y, current.Width, current.Height);
    }
}

/// <summary>
/// 一次转换的参数（WPF <c>PixelPaintHelper.ConvertOptions</c>）。
/// </summary>
public sealed class PixelPaintConvertOptions
{
    /// <summary>Gets 原图到 24×24 的构图方式，默认裁剪填充。</summary>
    public PixelPaintFitMode Fit { get; init; } = PixelPaintFitMode.Crop;

    /// <summary>Gets 量化抖动方式，默认插画优先。</summary>
    public PixelPaintDitherMode Dither { get; init; } = PixelPaintDitherMode.Illustration;

    /// <summary>Gets 对比度百分比，100 为原图。</summary>
    public double ContrastPercent { get; init; } = 100;

    /// <summary>Gets 亮度百分比，100 为原图。</summary>
    public double BrightnessPercent { get; init; } = 100;

    /// <summary>Gets 饱和度百分比，100 为原图。</summary>
    public double SaturationPercent { get; init; } = 100;

    /// <summary>
    /// Gets 取景区：相对 ｢去边后内容图｣ 的归一化矩形（0~1）。
    /// <see langword="null"/> 表示用完整去边结果再按 <see cref="Fit"/> 适配。
    /// </summary>
    public PixelPaintViewRect? ViewNormalized { get; init; }

    /// <summary>Gets a value indicating whether 导入时裁掉透明/近白边。</summary>
    public bool TrimEmptyBorder { get; init; } = true;
}

/// <summary>按色板下标分组的绘制点列，供 Core 逐色绘制。</summary>
public sealed class PixelPaintColorGroup
{
    /// <summary>Gets 色板下标 0~39。</summary>
    public required int Color { get; init; }

    /// <summary>Gets 格子坐标，左上为 (0,0)，x 右 y 下。</summary>
    public required IReadOnlyList<PixelPaintPoint> Points { get; init; }
}

/// <summary>单个格子的坐标。</summary>
/// <param name="X">列（0~23）。</param>
/// <param name="Y">行（0~23）。</param>
public readonly record struct PixelPaintPoint(int X, int Y);

/// <summary>一次转换的结果：色号矩阵 + 分组 + 预览像素。</summary>
public sealed class PixelPaintConvertResult
{
    /// <summary>Gets 24×24 色号，行优先，值域 0~39。</summary>
    public required int[,] Matrix { get; init; }

    /// <summary>Gets 按色分组后的点列，供 Core 使用（已按 skipWhite 过滤）。</summary>
    public required IReadOnlyList<PixelPaintColorGroup> Groups { get; init; }

    /// <summary>Gets 需绘制的格子总数（即 <see cref="Groups"/> 中所有点数之和）。</summary>
    public int PaintedCellCount { get; init; }

    /// <summary>Gets 等比放大的预览 BGRA32 像素（<see cref="PixelPaintPalette.PreviewPixelSize"/> 见方）。</summary>
    public required byte[] PreviewBgra { get; init; }
}
