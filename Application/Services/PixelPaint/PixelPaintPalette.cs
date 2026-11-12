namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>
/// 巡展像素画的官方色板与感知颜色距离。色序与游戏右侧色板一致，
/// 最近色使用 OKLab 感知距离（WPF <c>PixelPaintHelper</c>，<c>1f39c29587</c>）。
/// </summary>
public static class PixelPaintPalette
{
    /// <summary>网格边长，游戏像素编辑器为 24×24。</summary>
    public const int GridSize = 24;

    /// <summary>官方色板颜色数。</summary>
    public const int ColorCount = 40;

    /// <summary>纯白在 40 色板中的下标（0-based）。</summary>
    public const int WhiteColorIndex = 3;

    /// <summary>预览图边长（像素），按 <see cref="GridSize"/> 等比放大。</summary>
    public const int PreviewPixelSize = 216;

    /// <summary>
    /// 官方 40 色 RGB（与游戏色板顺序一致）。
    /// </summary>
    public static readonly (byte R, byte G, byte B)[] Colors =
    [
        (34, 34, 34), (180, 180, 180), (234, 231, 223), (255, 255, 255),
        (211, 47, 54), (156, 10, 0), (214, 12, 74), (230, 150, 141),
        (254, 152, 117), (247, 208, 192), (252, 239, 234), (251, 246, 232),
        (220, 210, 200), (226, 206, 171), (213, 99, 34), (212, 140, 66),
        (242, 153, 0), (249, 201, 51), (252, 228, 153), (179, 180, 122),
        (194, 218, 114), (108, 110, 0), (177, 145, 85), (169, 143, 116),
        (170, 146, 40), (63, 43, 18), (116, 73, 31), (83, 70, 88),
        (42, 36, 70), (57, 69, 153), (90, 69, 157), (186, 163, 215),
        (182, 188, 223), (169, 172, 190), (99, 171, 185), (180, 210, 220),
        (145, 216, 230), (71, 174, 160), (182, 211, 200), (39, 56, 100),
    ];

    /// <summary>色板的 OKLab 预转换缓存，避免最近色比对时反复解码。</summary>
    private static readonly (double L, double A, double B)[] PaletteOklab =
        Array.ConvertAll(Colors, color => Srgb8ToOklab(color.R, color.G, color.B));

    /// <summary>
    /// sRGB 0~255 → 线性光 0~1（标准 IEC 61966-2-1 解伽马）。
    /// </summary>
    /// <param name="value8">sRGB 分量 0~255。</param>
    /// <returns>线性光值 0~1。</returns>
    public static double Srgb8ToLinear(double value8)
    {
        var x = value8 / 255.0;
        return x >= 0.04045 ? Math.Pow((x + 0.055) / 1.055, 2.4) : x / 12.92;
    }

    /// <summary>
    /// 线性光 0~1 → sRGB 0~255（反向伽马编码）。
    /// </summary>
    /// <param name="linear">线性光值 0~1。</param>
    /// <returns>sRGB 分量 0~255。</returns>
    public static double LinearToSrgb8(double linear)
    {
        var value = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
        return value * 255.0;
    }

    /// <summary>
    /// sRGB 8 位 → OKLab（Björn 2020 标准矩阵）。
    /// </summary>
    /// <param name="r8">红 0~255。</param>
    /// <param name="g8">绿 0~255。</param>
    /// <param name="b8">蓝 0~255。</param>
    /// <returns>OKLab 感知坐标。</returns>
    public static (double L, double A, double B) Srgb8ToOklab(byte r8, byte g8, byte b8)
    {
        var r = Srgb8ToLinear(r8);
        var g = Srgb8ToLinear(g8);
        var b = Srgb8ToLinear(b8);

        // 线性 sRGB → LMS'
        var l = (0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b);
        var m = (0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b);
        var s = (0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b);

        // 非线性化（立方根）
        var l_ = Math.Cbrt(l);
        var m_ = Math.Cbrt(m);
        var s_ = Math.Cbrt(s);

        return (
            (0.2104542553 * l_) + (0.7936177850 * m_) - (0.0040720468 * s_),
            (1.9779984951 * l_) - (2.4285922050 * m_) + (0.4505937099 * s_),
            (0.0259040371 * l_) + (0.7827717662 * m_) - (0.8086757660 * s_));
    }

    /// <summary>
    /// 两 OKLab 坐标的欧氏距离平方。
    /// </summary>
    /// <param name="left">颜色一。</param>
    /// <param name="right">颜色二。</param>
    /// <returns>距离平方，越小越接近。</returns>
    public static double OklabSqDistance((double L, double A, double B) left, (double L, double A, double B) right)
    {
        var dL = left.L - right.L;
        var dA = left.A - right.A;
        var dB = left.B - right.B;
        return (dL * dL) + (dA * dA) + (dB * dB);
    }

    /// <summary>
    /// OKLab 感知色差（欧氏距离平方），逐次转换，便于单色比较。
    /// </summary>
    /// <param name="first">颜色一。</param>
    /// <param name="second">颜色二。</param>
    /// <returns>两色在 OKLab 空间的欧氏距离平方，越小越接近。</returns>
    public static double ColorDistance((byte R, byte G, byte B) first, (byte R, byte G, byte B) second)
    {
        return OklabSqDistance(
            Srgb8ToOklab(first.R, first.G, first.B),
            Srgb8ToOklab(second.R, second.G, second.B));
    }

    /// <summary>
    /// 给定 sRGB 颜色，在 40 色板中找 OKLab 最近项。
    /// </summary>
    /// <param name="r">红 0~255。</param>
    /// <param name="g">绿 0~255。</param>
    /// <param name="b">蓝 0~255。</param>
    /// <returns>最近色板下标 0~39。</returns>
    public static int NearestPaletteIndex(byte r, byte g, byte b)
    {
        return NearestPaletteIndex(Srgb8ToOklab(r, g, b));
    }

    /// <summary>
    /// 给定 OKLab 坐标，在预转换缓存中找最近色板项。
    /// </summary>
    /// <param name="color">OKLab 坐标。</param>
    /// <returns>最近色板下标 0~39。</returns>
    public static int NearestPaletteIndex((double L, double A, double B) color)
    {
        var best = 0;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < PaletteOklab.Length; i++)
        {
            var distance = OklabSqDistance(color, PaletteOklab[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// 取色板某项的 OKLab 坐标（预转换缓存），避免热循环里重复解码。
    /// </summary>
    /// <param name="index">色板下标 0~39。</param>
    /// <returns>该颜色的 OKLab 坐标。</returns>
    public static (double L, double A, double B) GetOklabColor(int index)
    {
        return PaletteOklab[index];
    }

    /// <summary>
    /// 给定 OKLab 坐标到色板某项的欧氏距离平方（使用预转换缓存）。
    /// </summary>
    /// <param name="color">OKLab 坐标。</param>
    /// <param name="index">色板下标 0~39。</param>
    /// <returns>距离平方，越小越接近。</returns>
    public static double OklabSqDistanceToPalette((double L, double A, double B) color, int index)
    {
        return OklabSqDistance(color, PaletteOklab[index]);
    }

    /// <summary>
    /// 取色板下标对应的颜色；越界时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="index">色板下标 0~39。</param>
    /// <returns>色板颜色。</returns>
    public static (byte R, byte G, byte B)? GetColor(int index)
    {
        return index >= 0 && index < Colors.Length ? Colors[index] : null;
    }

    /// <summary>
    /// 取色板下标对应的 #RRGGBB 颜色字符串，供日志等场景使用；越界返回 <see langword="null"/>。
    /// </summary>
    /// <param name="index">色板下标 0~39。</param>
    /// <returns>#RRGGBB 字符串；越界时为 <see langword="null"/>。</returns>
    public static string? GetPaletteColorHex(int index)
    {
        if (GetColor(index) is not { } color)
        {
            return null;
        }

        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
