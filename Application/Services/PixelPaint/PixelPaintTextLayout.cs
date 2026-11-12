using System.Globalization;

namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>文本位图中的一个字素及其目标格（24×24 内的像素矩形）。</summary>
/// <param name="Text">单个字素（可能为代理对/组合字符）。</param>
/// <param name="Column">列号（0 起）。</param>
/// <param name="Row">行号（0 起）。</param>
/// <param name="CellWidth">格宽（像素）。</param>
/// <param name="CellHeight">格高（像素）。</param>
public readonly record struct PixelPaintTextGlyph(string Text, int Column, int Row, int CellWidth, int CellHeight)
{
    /// <summary>Gets 该格左上角 x 坐标（像素）。</summary>
    public int X => Column * CellWidth;

    /// <summary>Gets 该格左上角 y 坐标（像素）。</summary>
    public int Y => Row * CellHeight;
}

/// <summary>
/// 最多 4 个字的文本排版（WPF <c>PixelPaintHelper.RenderTextToBitmap</c> 的排版部分）：
/// 中文等宽字素按自适应网格摆放（1 字占满、2~4 字分 2 列），
/// 英文 / 数字单行横排并放大字号（由渲染层按 ｢墨迹正好放下｣ 二分字号）。
/// </summary>
public sealed class PixelPaintTextLayout
{
    /// <summary>最多处理的字素数。</summary>
    public const int MaxTextElements = 4;

    private PixelPaintTextLayout(bool isWide, IReadOnlyList<PixelPaintTextGlyph> glyphs)
    {
        IsWide = isWide;
        Glyphs = glyphs;
        Columns = glyphs.Count == 0 ? 0 : glyphs.Max(glyph => glyph.Column) + 1;
        Rows = glyphs.Count == 0 ? 0 : glyphs.Max(glyph => glyph.Row) + 1;
    }

    /// <summary>Gets a value indicating whether 文本含非 ASCII 字素，按网格逐字排版。</summary>
    public bool IsWide { get; }

    /// <summary>Gets 列数。</summary>
    public int Columns { get; }

    /// <summary>Gets 行数。</summary>
    public int Rows { get; }

    /// <summary>Gets 各字素的摆放位置；空文本时为空列表。</summary>
    public IReadOnlyList<PixelPaintTextGlyph> Glyphs { get; }

    /// <summary>Gets a value indicating whether 文本为空（无字素）。</summary>
    public bool IsEmpty => Glyphs.Count == 0;

    /// <summary>Gets 由字素拼回的整串文本（英文/数字单行排版使用）。</summary>
    public string Text => string.Concat(Glyphs.Select(glyph => glyph.Text));

    /// <summary>
    /// 计算文本排版：按字素拆分并只取前 <see cref="MaxTextElements"/> 个。
    /// </summary>
    /// <param name="text">输入文本，允许 <see langword="null"/>。</param>
    /// <returns>排版结果；空文本时 <see cref="IsEmpty"/> 为 <see langword="true"/>。</returns>
    public static PixelPaintTextLayout Create(string? text)
    {
        var elements = TakeTextElements(text);
        const int size = PixelPaintPalette.GridSize;

        if (elements.Count == 0)
        {
            return new PixelPaintTextLayout(false, []);
        }

        if (!elements.Any(ContainsNonAscii))
        {
            // 英文 / 数字：整串单行横排，占满 24×24 后再由渲染层裁墨迹、二分字号
            return new PixelPaintTextLayout(false, [new PixelPaintTextGlyph(string.Concat(elements), 0, 0, size, size)]);
        }

        // 中文：自适应网格，1 字占满整张、2~4 字分 2 列
        var count = elements.Count;
        var columns = Math.Min(count, 2);
        var rows = (count + columns - 1) / columns;
        var cellWidth = size / columns;
        var cellHeight = size / rows;
        var glyphs = new List<PixelPaintTextGlyph>(count);
        for (var i = 0; i < count; i++)
        {
            glyphs.Add(new PixelPaintTextGlyph(elements[i], i % columns, i / columns, cellWidth, cellHeight));
        }

        return new PixelPaintTextLayout(true, glyphs);
    }

    /// <summary>
    /// 按字素（grapheme cluster）拆分并取前 <paramref name="max"/> 个，避免拆开代理对或组合字符。
    /// </summary>
    /// <param name="text">输入文本，允许 <see langword="null"/>。</param>
    /// <param name="max">最多取多少个字素。</param>
    /// <returns>前若干个字素；空文本返回空列表。</returns>
    public static IReadOnlyList<string> TakeTextElements(string? text, int max = MaxTextElements)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return [];
        }

        var elements = new List<string>(Math.Min(max, MaxTextElements));
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext() && elements.Count < max)
        {
            elements.Add((string)enumerator.Current);
        }

        return elements;
    }

    /// <summary>
    /// 判断字素中是否含非 ASCII 字符（如中文），用于决定网格排版还是单行横排。
    /// </summary>
    /// <param name="textElement">单个字素。</param>
    /// <returns>含非 ASCII 字符返回 <see langword="true"/>。</returns>
    public static bool ContainsNonAscii(string textElement)
    {
        foreach (var c in textElement)
        {
            if (c >= 128)
            {
                return true;
            }
        }

        return false;
    }
}
