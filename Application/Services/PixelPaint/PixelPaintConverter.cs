namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>
/// 巡展像素画转换：原图 → 24×24 × 官方 40 色（WPF <c>PixelPaintHelper</c>，<c>1f39c29587</c>）。
/// 流程：去边 → 线性光面积采样 → CSS 风格滤镜 → 按抖动方式量化 → 分组/预览。
/// 纯算法层，不依赖 UI 框架；图像解码与预览位图包装在 App 层。
/// </summary>
public static class PixelPaintConverter
{
    /// <summary>
    /// 裁掉图像四周的透明（alpha &lt; 16）与近白（RGB ≥ 250）像素，返回内容包围盒。
    /// 恰好 24×24 视为用户外部已处理好的像素画，不去边；整图空白时返回原图。
    /// </summary>
    /// <param name="source">原始 BGRA 像素图。</param>
    /// <param name="trimEmptyBorder">是否裁掉透明/近白边，默认 <see langword="true"/>。</param>
    /// <returns>去边后的图像缓冲。</returns>
    public static PixelPaintImage Prepare(PixelPaintImage source, bool trimEmptyBorder = true)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 恰好 24×24 视为用户外部已处理好的像素画，不去边
        if (!trimEmptyBorder || (source.Width == PixelPaintPalette.GridSize && source.Height == PixelPaintPalette.GridSize))
        {
            return source;
        }

        return TrimBorder(source) ?? source;
    }

    /// <summary>
    /// 用预处理好的图像转换，适合需要多次调参实时预览的场景。
    /// </summary>
    /// <param name="prepared">已去边的预处理图像，可复用避免重复解码。</param>
    /// <param name="options">转换参数（构图、抖动、滤镜等）。</param>
    /// <param name="skipWhite">是否跳过纯白格（不画），默认 <see langword="true"/>。</param>
    /// <returns>色号矩阵 + 分组 + 预览像素。</returns>
    public static PixelPaintConvertResult Convert(
        PixelPaintImage prepared,
        PixelPaintConvertOptions options,
        bool skipWhite = true)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(options);

        var sample = SampleToGrid(prepared, options);
        ApplyCssLikeFilters(
            sample,
            options.ContrastPercent / 100.0,
            options.BrightnessPercent / 100.0,
            options.SaturationPercent / 100.0);

        var matrix = options.Dither == PixelPaintDitherMode.Illustration
            ? QuantizeIllustration(sample)
            : Quantize(sample, options.Dither);
        var groups = BuildGroups(matrix, skipWhite);
        var painted = 0;
        foreach (var group in groups)
        {
            painted += group.Points.Count;
        }

        return new PixelPaintConvertResult
        {
            Matrix = matrix,
            Groups = groups,
            PaintedCellCount = painted,
            PreviewBgra = RenderPreviewBgra(matrix),
        };
    }

    /// <summary>
    /// 把色号矩阵按色板下标分桶，输出供 Core 逐色绘制的点列。
    /// </summary>
    /// <param name="matrix">24×24 色号矩阵，值域 0~39。</param>
    /// <param name="skipWhite">是否跳过纯白格（<see cref="PixelPaintPalette.WhiteColorIndex"/>）。</param>
    /// <returns>非空分组列表，每组含色板下标与该色所有格子坐标。</returns>
    public static IReadOnlyList<PixelPaintColorGroup> BuildGroups(int[,] matrix, bool skipWhite)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.GetLength(0) != PixelPaintPalette.GridSize || matrix.GetLength(1) != PixelPaintPalette.GridSize)
        {
            throw new ArgumentException("Pixel paint matrix must be 24×24.", nameof(matrix));
        }

        var buckets = new List<PixelPaintPoint>[PixelPaintPalette.ColorCount];
        for (var color = 0; color < PixelPaintPalette.ColorCount; color++)
        {
            buckets[color] = [];
        }

        for (var y = 0; y < PixelPaintPalette.GridSize; y++)
        {
            for (var x = 0; x < PixelPaintPalette.GridSize; x++)
            {
                var index = matrix[y, x];
                if (index < 0 || index >= PixelPaintPalette.ColorCount)
                {
                    throw new ArgumentException($"Pixel paint matrix contains out-of-range color index {index}.", nameof(matrix));
                }

                if (skipWhite && index == PixelPaintPalette.WhiteColorIndex)
                {
                    continue;
                }

                buckets[index].Add(new PixelPaintPoint(x, y));
            }
        }

        var groups = new List<PixelPaintColorGroup>();
        for (var color = 0; color < PixelPaintPalette.ColorCount; color++)
        {
            if (buckets[color].Count == 0)
            {
                continue;
            }

            groups.Add(new PixelPaintColorGroup { Color = color, Points = buckets[color] });
        }

        return groups;
    }

    /// <summary>
    /// 把色号矩阵渲染成等比放大的预览 BGRA32 像素（每格一个色块）。
    /// </summary>
    /// <param name="matrix">24×24 色号矩阵，值域 0~39。</param>
    /// <returns><see cref="PixelPaintPalette.PreviewPixelSize"/> 见方的 BGRA32 像素。</returns>
    public static byte[] RenderPreviewBgra(int[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var size = PixelPaintPalette.PreviewPixelSize;
        var cell = size / PixelPaintPalette.GridSize;
        var pixels = new byte[size * size * PixelPaintImage.BytesPerPixel];

        for (var y = 0; y < PixelPaintPalette.GridSize; y++)
        {
            for (var x = 0; x < PixelPaintPalette.GridSize; x++)
            {
                var (r, g, b) = PixelPaintPalette.Colors[matrix[y, x]];
                for (var dy = 0; dy < cell; dy++)
                {
                    for (var dx = 0; dx < cell; dx++)
                    {
                        var px = (x * cell) + dx;
                        var py = (y * cell) + dy;
                        var index = ((py * size) + px) * PixelPaintImage.BytesPerPixel;
                        pixels[index] = b;
                        pixels[index + 1] = g;
                        pixels[index + 2] = r;
                        pixels[index + 3] = 255;
                    }
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// 裁掉透明与近白边，返回内容包围盒；整图无内容返回 <see langword="null"/>。
    /// </summary>
    /// <param name="source">原始 BGRA 像素图。</param>
    /// <returns>裁剪后的像素图；若整图无内容返回 <see langword="null"/>，无变化时返回原对象。</returns>
    private static PixelPaintImage? TrimBorder(PixelPaintImage source)
    {
        var w = source.Width;
        var h = source.Height;
        var pixels = source.Pixels;

        static bool IsContent(byte b, byte g, byte r, byte a)
        {
            if (a < 16)
            {
                return false;
            }

            // 近白视为空白边
            return r < 250 || g < 250 || b < 250;
        }

        var minX = w;
        var minY = h;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var index = ((y * w) + x) * PixelPaintImage.BytesPerPixel;
                if (!IsContent(pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]))
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
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

        var nw = maxX - minX + 1;
        var nh = maxY - minY + 1;
        if (nw == w && nh == h)
        {
            return source;
        }

        return source.Crop(minX, minY, nw, nh);
    }

    /// <summary>
    /// 一格的采样结果：均值（线性光平均后编码回 sRGB）+ 格内子样（sRGB，白底已合成）。
    /// 子样供插画优先模式的 medoid 选取使用。
    /// </summary>
    private struct CellSample
    {
        /// <summary>Gets or sets 格代表色红分量（线性光平均后编码回 sRGB，0~255）。</summary>
        public double R { get; set; }

        /// <summary>Gets or sets 格代表色绿分量（线性光平均后编码回 sRGB，0~255）。</summary>
        public double G { get; set; }

        /// <summary>Gets or sets 格代表色蓝分量（线性光平均后编码回 sRGB，0~255）。</summary>
        public double B { get; set; }

        /// <summary>Gets or sets 格内子样扁平数组 [r,g,b, r,g,b, ...]，sRGB 0~255。</summary>
        public double[] Subs { get; set; }
    }

    /// <summary>
    /// 将内容图按 Fit + 可选归一化取景，采样到 24×24 格。
    /// 每格在线性光空间对格内多点做面积平均（BOX 滤波），再编码回 sRGB；
    /// 同时保留格内子样的 sRGB 原值，供插画优先模式的 medoid 选取。
    /// </summary>
    /// <param name="src">去边后的 BGRA 像素图。</param>
    /// <param name="options">转换参数（构图、取景等）。</param>
    /// <returns>24×24 采样网格，每格含均值与子样。</returns>
    private static CellSample[,] SampleToGrid(PixelPaintImage src, PixelPaintConvertOptions options)
    {
        const int grid = PixelPaintPalette.GridSize;

        // 源图恰好 24×24：逐像素直接取色，不做插值/面积采样，仅最近色匹配
        if (src.Width == grid && src.Height == grid)
        {
            var exact = new CellSample[grid, grid];
            for (var y = 0; y < grid; y++)
            {
                for (var x = 0; x < grid; x++)
                {
                    var (r, g, b) = src.GetRgb(x, y);
                    exact[y, x] = new CellSample { R = r, G = g, B = b, Subs = [r, g, b] };
                }
            }

            return exact;
        }

        var view = (options.ViewNormalized ?? PixelPaintViewRect.Full).Normalize();

        var srcX0 = view.X * src.Width;
        var srcY0 = view.Y * src.Height;
        var srcW = Math.Max(1e-6, view.Width * src.Width);
        var srcH = Math.Max(1e-6, view.Height * src.Height);

        // 在取景矩形内再按 Fit 映射到 24×24
        double mapX0, mapY0, mapW, mapH;
        switch (options.Fit)
        {
            case PixelPaintFitMode.Stretch:
                mapX0 = srcX0;
                mapY0 = srcY0;
                mapW = srcW;
                mapH = srcH;
                break;
            case PixelPaintFitMode.Contain:
            {
                // 外接矩形包含整张源图（Max），等比装入目标并留白
                var scale = Math.Max(srcW / grid, srcH / grid);
                mapW = grid * scale;
                mapH = grid * scale;
                mapX0 = srcX0 + ((srcW - mapW) / 2.0);
                mapY0 = srcY0 + ((srcH - mapH) / 2.0);
                break;
            }

            default: // Crop = cover
            {
                // 源图内最大的 1:1 采样矩形（Min），裁掉多余边并铺满
                var scale = Math.Min(srcW / grid, srcH / grid);
                mapW = grid * scale;
                mapH = grid * scale;
                mapX0 = srcX0 + ((srcW - mapW) / 2.0);
                mapY0 = srcY0 + ((srcH - mapH) / 2.0);
                break;
            }
        }

        // 格内子样数：按源图覆盖面积自适应，最多 4×4。格子像素数越大采样越密。
        var cellW = mapW / grid;
        var cellH = mapH / grid;
        var sxN = Math.Clamp((int)Math.Ceiling(cellW), 1, 4);
        var syN = Math.Clamp((int)Math.Ceiling(cellH), 1, 4);

        var samples = new CellSample[grid, grid];
        for (var gy = 0; gy < grid; gy++)
        {
            for (var gx = 0; gx < grid; gx++)
            {
                // 该格在源图中的矩形
                var gx0 = mapX0 + (gx / (double)grid * mapW);
                var gy0 = mapY0 + (gy / (double)grid * mapH);
                var gw = mapW / grid;
                var gh = mapH / grid;

                // 在格内取 sxN×syN 个均匀子样的格心
                var n = sxN * syN;
                var subs = new double[n * 3];
                double sumLR = 0, sumLG = 0, sumLB = 0; // 线性光累加，用于面积平均
                var k = 0;
                for (var iy = 0; iy < syN; iy++)
                {
                    for (var ix = 0; ix < sxN; ix++)
                    {
                        var sx = gx0 + ((ix + 0.5) / sxN * gw);
                        var sy = gy0 + ((iy + 0.5) / syN * gh);
                        var (cr, cg, cb) = src.SampleBilinear(sx, sy);

                        subs[k++] = cr;
                        subs[k++] = cg;
                        subs[k++] = cb;

                        // 线性光空间累加（避免 sRGB 直接平均导致交界偏暗）
                        sumLR += PixelPaintPalette.Srgb8ToLinear(cr);
                        sumLG += PixelPaintPalette.Srgb8ToLinear(cg);
                        sumLB += PixelPaintPalette.Srgb8ToLinear(cb);
                    }
                }

                // 线性光平均后编码回 sRGB，作为该格代表色（供 None/FS/Atkinson 使用）
                samples[gy, gx] = new CellSample
                {
                    R = PixelPaintPalette.LinearToSrgb8(sumLR / n),
                    G = PixelPaintPalette.LinearToSrgb8(sumLG / n),
                    B = PixelPaintPalette.LinearToSrgb8(sumLB / n),
                    Subs = subs,
                };
            }
        }

        return samples;
    }

    /// <summary>
    /// 在 sRGB 0~255 上近似 CSS filter（固定顺序：亮度 → 对比度 → 饱和度）。
    /// 对每格的均值与全部子样同步施用，使调参对插画优先模式同样生效。
    /// </summary>
    /// <param name="samples">24×24 采样网格，原地修改。</param>
    /// <param name="contrast">对比度系数，1 为原图。</param>
    /// <param name="brightness">亮度系数，1 为原图。</param>
    /// <param name="saturation">饱和度系数，1 为原图。</param>
    private static void ApplyCssLikeFilters(CellSample[,] samples, double contrast, double brightness, double saturation)
    {
        if (Math.Abs(contrast - 1) < 1e-6 && Math.Abs(brightness - 1) < 1e-6 && Math.Abs(saturation - 1) < 1e-6)
        {
            return;
        }

        static (double R, double G, double B) Filter(
            double r,
            double g,
            double b,
            double contrast,
            double brightness,
            double saturation)
        {
            // brightness
            r *= brightness;
            g *= brightness;
            b *= brightness;

            // contrast around 0.5 mid gray in 0~1 space then back
            r = ((((r / 255.0) - 0.5) * contrast) + 0.5) * 255.0;
            g = ((((g / 255.0) - 0.5) * contrast) + 0.5) * 255.0;
            b = ((((b / 255.0) - 0.5) * contrast) + 0.5) * 255.0;

            // saturate via luma
            var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            r = luma + ((r - luma) * saturation);
            g = luma + ((g - luma) * saturation);
            b = luma + ((b - luma) * saturation);

            return (ClampByte(r), ClampByte(g), ClampByte(b));
        }

        for (var y = 0; y < PixelPaintPalette.GridSize; y++)
        {
            for (var x = 0; x < PixelPaintPalette.GridSize; x++)
            {
                var cell = samples[y, x];

                // 均值
                var (ar, ag, ab) = Filter(cell.R, cell.G, cell.B, contrast, brightness, saturation);

                // 子样（插画优先模式会用）
                var subs = cell.Subs;
                for (var i = 0; i < subs.Length; i += 3)
                {
                    var (fr, fg, fb) = Filter(subs[i], subs[i + 1], subs[i + 2], contrast, brightness, saturation);
                    subs[i] = fr;
                    subs[i + 1] = fg;
                    subs[i + 2] = fb;
                }

                samples[y, x] = new CellSample
                {
                    R = ar,
                    G = ag,
                    B = ab,
                    Subs = subs,
                };
            }
        }
    }

    /// <summary>
    /// 把数值钳制到字节范围 [0, 255]。
    /// </summary>
    /// <param name="value">任意浮点值。</param>
    /// <returns>钳制后的值。</returns>
    private static double ClampByte(double value) => Math.Clamp(value, 0, 255);

    /// <summary>
    /// 最近邻量化（含可选误差扩散抖动）。FS 采用蛇形扫描 + 部分误差扩散以减弱噪点；
    /// Atkinson 对称扩散 6/8 误差；None 不扩散。
    /// </summary>
    /// <param name="sample">24×24 采样网格（取均值参与量化）。</param>
    /// <param name="dither">抖动方式（None / FloydSteinberg / Atkinson）。</param>
    /// <returns>24×24 色号矩阵，值域 0~39。</returns>
    private static int[,] Quantize(CellSample[,] sample, PixelPaintDitherMode dither)
    {
        const int grid = PixelPaintPalette.GridSize;

        var work = new (double R, double G, double B)[grid, grid];
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var cell = sample[y, x];
                work[y, x] = (cell.R, cell.G, cell.B);
            }
        }

        var result = new int[grid, grid];

        void AddError(int x, int y, double er, double eg, double eb, double factor)
        {
            if (x < 0 || y < 0 || x >= grid || y >= grid)
            {
                return;
            }

            var p = work[y, x];
            work[y, x] = (
                ClampByte(p.R + (er * factor)),
                ClampByte(p.G + (eg * factor)),
                ClampByte(p.B + (eb * factor)));
        }

        for (var y = 0; y < grid; y++)
        {
            // 蛇形扫描：奇数行反向遍历，消除误差长期偏向一侧的条纹（仅对 FS 生效）
            var serpentine = dither == PixelPaintDitherMode.FloydSteinberg && (y % 2 == 1);
            var fwd = serpentine ? -1 : 1;

            for (var i = 0; i < grid; i++)
            {
                var x = serpentine ? grid - 1 - i : i;
                var old = work[y, x];
                var or = (byte)Math.Clamp((int)Math.Round(old.R), 0, 255);
                var og = (byte)Math.Clamp((int)Math.Round(old.G), 0, 255);
                var ob = (byte)Math.Clamp((int)Math.Round(old.B), 0, 255);
                var index = PixelPaintPalette.NearestPaletteIndex(or, og, ob);
                result[y, x] = index;

                if (dither == PixelPaintDitherMode.None)
                {
                    continue;
                }

                var (nr, ng, nb) = PixelPaintPalette.Colors[index];
                var er = old.R - nr;
                var eg = old.G - ng;
                var eb = old.B - nb;

                if (dither == PixelPaintDitherMode.FloydSteinberg)
                {
                    // 蛇形 FS：行内前向 + 下一行按行向镜像，权重不变。
                    // 24×24 下整图仅 576 格，全强度误差扩散（系数 1.0）噪点过密、观感脏；
                    // 这里仅扩散部分误差（系数 < 1），衰减高频噪点但仍能压住渐变色带。
                    const double fsStrength = 0.6;
                    AddError(x + fwd, y, er, eg, eb, 7.0 / 16.0 * fsStrength);
                    AddError(x - fwd, y + 1, er, eg, eb, 3.0 / 16.0 * fsStrength);
                    AddError(x, y + 1, er, eg, eb, 5.0 / 16.0 * fsStrength);
                    AddError(x + fwd, y + 1, er, eg, eb, 1.0 / 16.0 * fsStrength);
                }
                else
                {
                    // Atkinson：对称扩散，无需蛇形
                    const double f = 1.0 / 8.0;
                    AddError(x + 1, y, er, eg, eb, f);
                    AddError(x + 2, y, er, eg, eb, f);
                    AddError(x - 1, y + 1, er, eg, eb, f);
                    AddError(x, y + 1, er, eg, eb, f);
                    AddError(x + 1, y + 1, er, eg, eb, f);
                    AddError(x, y + 2, er, eg, eb, f);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 插画优先量化：medoid 代表色 + 边缘感知 MRF 平滑（对齐参考实现）。
    /// 每格在子样里选 ｢最接近格内 OKLab 均值｣ 的真实颜色为代表色，
    /// 再用 ICM 迭代 + 指数衰减软约束让相邻且原图颜色接近的格子倾向同色。
    /// </summary>
    /// <param name="sample">24×24 采样网格（取格内子样参与 medoid 选取）。</param>
    /// <returns>24×24 色号矩阵，值域 0~39。</returns>
    private static int[,] QuantizeIllustration(CellSample[,] sample)
    {
        const int grid = PixelPaintPalette.GridSize;

        // 1. 每格选代表色（medoid）：最接近格内 OKLab 均值的真实子样
        var repr = new (double L, double A, double B)[grid, grid];

        // 子样数固定 ≤4×4=16，循环外一次性分配复用，避免循环内 stackalloc（CA2014）
        const int maxSubs = 16;
        Span<(double L, double A, double B)> lab = stackalloc (double, double, double)[maxSubs];

        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var subs = sample[y, x].Subs;
                var n = subs.Length / 3;

                if (n == 0)
                {
                    repr[y, x] = PixelPaintPalette.Srgb8ToOklab(255, 255, 255);
                    continue;
                }

                // 预转 OKLab，并累加格内均值
                double sumL = 0, sumA = 0, sumB = 0;
                for (var i = 0; i < n; i++)
                {
                    var v = PixelPaintPalette.Srgb8ToOklab(
                        (byte)Math.Clamp((int)Math.Round(subs[i * 3]), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(subs[(i * 3) + 1]), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(subs[(i * 3) + 2]), 0, 255));
                    lab[i] = v;
                    sumL += v.L;
                    sumA += v.A;
                    sumB += v.B;
                }

                var mL = sumL / n;
                var mA = sumA / n;
                var mB = sumB / n;

                // 选离均值最近的真实子样
                var best = 0;
                var bestD = double.PositiveInfinity;
                for (var i = 0; i < n; i++)
                {
                    var dL = lab[i].L - mL;
                    var dA = lab[i].A - mA;
                    var dB = lab[i].B - mB;
                    var d = (dL * dL) + (dA * dA) + (dB * dB);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }

                repr[y, x] = lab[best];
            }
        }

        // 2. 预计算数据项：每格每色板的 OKLab 平方距离 [y,x,c]
        //    OKLab 平方距离量级 ~0.01，与下面的平滑项同尺度
        var dataCost = new double[grid, grid, PixelPaintPalette.ColorCount];
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var m = repr[y, x];
                for (var c = 0; c < PixelPaintPalette.ColorCount; c++)
                {
                    dataCost[y, x, c] = PixelPaintPalette.OklabSqDistanceToPalette(m, c);
                }
            }
        }

        // 3. 初始标号：数据项最小的色板
        var labels = new int[grid, grid];
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var bestC = 0;
                var bestE = double.PositiveInfinity;
                for (var c = 0; c < PixelPaintPalette.ColorCount; c++)
                {
                    if (dataCost[y, x, c] < bestE)
                    {
                        bestE = dataCost[y, x, c];
                        bestC = c;
                    }
                }

                labels[y, x] = bestC;
            }
        }

        // 4. ICM 迭代：每格选使 ｢数据项 + 平滑项｣ 最小的标号
        //    平滑项 = 指数衰减软约束：相邻格原图颜色越接近，惩罚越高（越倾向同色）
        //      w = strength * exp(-oklab_dist² / 0.0025)
        //    邻居标号不同时 energy += w；颜色相差大时 w→0，自然允许分裂（无需显式边缘）
        //    strength 对齐参考实现（0.0012），可按实测微调
        const double strength = 0.0012;
        const double sigma2 = 0.0025;
        for (var iter = 0; iter < 3; iter++)
        {
            var changed = false;
            for (var y = 0; y < grid; y++)
            {
                for (var x = 0; x < grid; x++)
                {
                    var m = repr[y, x];
                    var bestLabel = labels[y, x];
                    var bestEnergy = double.PositiveInfinity;

                    // 预计算各邻居的平滑权重（基于原图 OKLab 距离）
                    double wL = 0, wR = 0, wU = 0, wD = 0; // 左右上下
                    if (x > 0)
                    {
                        wL = strength * Math.Exp(-PixelPaintPalette.OklabSqDistance(m, repr[y, x - 1]) / sigma2);
                    }

                    if (x < grid - 1)
                    {
                        wR = strength * Math.Exp(-PixelPaintPalette.OklabSqDistance(m, repr[y, x + 1]) / sigma2);
                    }

                    if (y > 0)
                    {
                        wU = strength * Math.Exp(-PixelPaintPalette.OklabSqDistance(m, repr[y - 1, x]) / sigma2);
                    }

                    if (y < grid - 1)
                    {
                        wD = strength * Math.Exp(-PixelPaintPalette.OklabSqDistance(m, repr[y + 1, x]) / sigma2);
                    }

                    for (var c = 0; c < PixelPaintPalette.ColorCount; c++)
                    {
                        var energy = dataCost[y, x, c];

                        // 平滑项：邻居标号不同则加权惩罚
                        if (x > 0 && labels[y, x - 1] != c)
                        {
                            energy += wL;
                        }

                        if (x < grid - 1 && labels[y, x + 1] != c)
                        {
                            energy += wR;
                        }

                        if (y > 0 && labels[y - 1, x] != c)
                        {
                            energy += wU;
                        }

                        if (y < grid - 1 && labels[y + 1, x] != c)
                        {
                            energy += wD;
                        }

                        if (energy < bestEnergy)
                        {
                            bestEnergy = energy;
                            bestLabel = c;
                        }
                    }

                    if (bestLabel != labels[y, x])
                    {
                        labels[y, x] = bestLabel;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return labels;
    }
}
