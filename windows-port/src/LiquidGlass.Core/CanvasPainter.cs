namespace LiquidGlass.Core;

/// <summary>
/// 在 <see cref="BgraFrame"/> 上做 Alpha 合成的轻量绘制工具。
///
/// <para>只实现替换任务栏真正需要的几种图元：圆角矩形、图像贴放、胶囊指示条。
/// 刻意不引入任何 2D 绘图库 —— 整个仓库的承诺是零第三方依赖，
/// 而这几个操作加起来不到 200 行。</para>
///
/// <para>所有颜色参数都是 <c>(B, G, R)</c> 顺序或 <see cref="byte"/> 分量，
/// 与 <see cref="BgraFrame"/> 的内存布局一致。</para>
/// </summary>
public static class CanvasPainter
{
    /// <summary>在帧上叠一层半透明纯色圆角矩形。</summary>
    /// <param name="alpha">0–255 的叠加强度，会与像素自身的 Alpha 相乘。</param>
    public static void FillRoundedRect(
        BgraFrame frame, double x, double y, double width, double height,
        double radius, byte b, byte g, byte r, byte alpha)
    {
        if (alpha == 0 || width <= 0 || height <= 0) return;

        radius = Math.Clamp(radius, 0, Math.Min(width, height) * 0.5);

        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(frame.Width, (int)Math.Ceiling(x + width));
        var y1 = Math.Min(frame.Height, (int)Math.Ceiling(y + height));

        for (var py = y0; py < y1; py++)
        {
            var row = py * frame.Stride;
            for (var px = x0; px < x1; px++)
            {
                // 圆角覆盖率：对四个角做解析计算，中心区域直接满覆盖。
                var coverage = CornerCoverage(px + 0.5, py + 0.5, x, y, width, height, radius);
                if (coverage <= 0) continue;

                var blendAlpha = coverage * (alpha / 255.0);
                var index = row + px * 4;

                var dstA = frame.Pixels[index + 3] / 255.0;
                var outA = blendAlpha + dstA * (1 - blendAlpha);
                if (outA <= 0) continue;

                frame.Pixels[index] = Blend(frame.Pixels[index], b, blendAlpha, dstA, outA);
                frame.Pixels[index + 1] = Blend(frame.Pixels[index + 1], g, blendAlpha, dstA, outA);
                frame.Pixels[index + 2] = Blend(frame.Pixels[index + 2], r, blendAlpha, dstA, outA);
                frame.Pixels[index + 3] = (byte)Math.Clamp(outA * 255 + 0.5, 0, 255);
            }
        }
    }

    /// <summary>
    /// 把一个带 Alpha 的图像贴到帧上。<paramref name="tint"/> 为 null 时按原色贴，
    /// 否则用 tint 替换颜色、只取源图的 Alpha 作覆盖率（用来染图标与文字）。
    /// </summary>
    /// <summary>
    /// 双线性放大。
    ///
    /// <para><b>为什么需要它</b>：玻璃是<b>低频</b>效果（雾化 + 折射），
    /// 把它按面板的完整分辨率算一遍是极大的浪费 —— 而耗时与像素数成正比，
    /// 面板一大就卡死。正确做法是<b>把玻璃按低分辨率算完再放大</b>，
    /// 而图标与文字等<b>高频内容在放大之后才画</b>，从而既便宜又不糊。</para>
    ///
    /// <para>这里用双线性而不是最近邻：玻璃的圆角边缘与渐变很多，
    /// 最近邻会在 2~3 倍放大下露出明显的方块。</para>
    /// </summary>
    public static BgraFrame UpscaleBilinear(BgraFrame source, int width, int height)
    {
        var result = new BgraFrame(Math.Max(1, width), Math.Max(1, height));
        UpscaleBilinearInto(source, result);
        return result;
    }

    /// <summary>
    /// 双线性放大，结果写入 <paramref name="dest"/>（尺寸由它决定）。
    ///
    /// <para><b>为什么要有"写入已有缓冲"的版本</b>：面板尺寸的上屏帧是
    /// 1362×1270×4 ≈ 6.9MB，超过 85KB 就会进 <b>LOH（大对象堆）</b>，
    /// 而 LOH 默认不压缩 —— 累积去噪期间 10Hz 上屏就是约 70MB/s 的 LOH 分配，
    /// 会造成碎片化、私有内存虚高，以及间歇性的 GC 卡顿。
    /// 调用方复用一张缓冲即可彻底消除这块 churn。</para>
    /// </summary>
    public static void UpscaleBilinearInto(BgraFrame source, BgraFrame dest)
    {
        var width = dest.Width;
        var height = dest.Height;
        if (width <= 0 || height <= 0) return;

        var result = dest;
        var sw = source.Width;
        var sh = source.Height;
        var src = source.Pixels;
        var dst = result.Pixels;
        var srcStride = source.Stride;
        var dstStride = result.Stride;

        // ⚠️ 性能关键：**把 x 方向的插值系数预先算成整数表**。
        //
        // 旧实现把 (x + 0.5) * sx - 0.5 这类 double 运算放在最内层，
        // 而它**只与 x 有关、与 y 无关** —— 面板 1362×1270 时等于把同一组
        // 系数重复算了 1270 遍，总共约 7000 万次浮点操作，实测要 62ms。
        //
        // 这里改成：x 系数算一次存表，内层循环只做整数乘加。
        // 16 位定点足够（误差 ≤ 1/65536，肉眼绝无可能分辨），
        // 而整数运算比 double 快一个数量级。
        const int One = 1 << 16;

        var x0Arr = new int[width];
        var x1Arr = new int[width];
        var wxArr = new int[width];
        var sx = (double)sw / width;
        for (var x = 0; x < width; x++)
        {
            var fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, sw - 1);
            var ix0 = (int)fx;
            x0Arr[x] = ix0 * 4;
            x1Arr[x] = Math.Min(ix0 + 1, sw - 1) * 4;
            wxArr[x] = (int)((fx - ix0) * One + 0.5);
        }

        var sy = (double)sh / height;

        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, sh - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, sh - 1);
            var wy = (int)((fy - y0) * One + 0.5);
            var invWy = One - wy;

            var row0 = y0 * srcStride;
            var row1 = y1 * srcStride;
            var drow = y * dstStride;

            for (var x = 0; x < width; x++)
            {
                var ox0 = x0Arr[x];
                var ox1 = x1Arr[x];
                var wx = wxArr[x];
                var invWx = One - wx;

                var i00 = row0 + ox0;
                var i01 = row0 + ox1;
                var i10 = row1 + ox0;
                var i11 = row1 + ox1;

                var o = drow + x * 4;

                // 定点双线性：先横向插值再纵向插值。
                //
                // ⚠️ 中间量必须用 long：
                //   横向 top ≈ 255 × 65536 × 2 ≈ 3.3e7（int 装得下），
                //   但再乘纵向系数 65536 就是 2.2e12 —— **int 会溢出**，
                //   画面会出现随机彩点（而且只在特定亮度处出现，极难查）。
                for (var c = 0; c < 4; c++)
                {
                    long top = (long)src[i00 + c] * invWx + (long)src[i01 + c] * wx;
                    long bottom = (long)src[i10 + c] * invWx + (long)src[i11 + c] * wx;
                    var v = (top * invWy + bottom * wy) >> 32;
                    dst[o + c] = (byte)v;
                }
            }
        }
    }

    public static void Blit(
        BgraFrame frame, BgraFrame source, int destX, int destY,
        (byte B, byte G, byte R)? tint = null, double alphaScale = 1.0)
    {
        for (var sy = 0; sy < source.Height; sy++)
        {
            var dy = destY + sy;
            if (dy < 0 || dy >= frame.Height) continue;

            for (var sx = 0; sx < source.Width; sx++)
            {
                var dx = destX + sx;
                if (dx < 0 || dx >= frame.Width) continue;

                var si = sy * source.Stride + sx * 4;
                var srcA = source.Pixels[si + 3] / 255.0 * alphaScale;
                if (srcA <= 0.001) continue;

                var di = dy * frame.Stride + dx * 4;

                byte sb = source.Pixels[si];
                byte sg = source.Pixels[si + 1];
                byte sr = source.Pixels[si + 2];

                if (tint is { } t)
                {
                    sb = t.B; sg = t.G; sr = t.R;
                }

                var dstA = frame.Pixels[di + 3] / 255.0;
                var outA = srcA + dstA * (1 - srcA);
                if (outA <= 0) continue;

                frame.Pixels[di] = Blend(frame.Pixels[di], sb, srcA, dstA, outA);
                frame.Pixels[di + 1] = Blend(frame.Pixels[di + 1], sg, srcA, dstA, outA);
                frame.Pixels[di + 2] = Blend(frame.Pixels[di + 2], sr, srcA, dstA, outA);
                frame.Pixels[di + 3] = (byte)Math.Clamp(outA * 255 + 0.5, 0, 255);
            }
        }
    }

    /// <summary>
    /// 盒式缩小。用于把<b>超采样</b>渲染出来的画布降回面板尺寸。
    ///
    /// <para>缩小时做的是区域平均，这正是超采样抗锯齿的原理：
    /// 玻璃的圆角边缘、对角高光、图标轮廓在 1.5× 下算出更细的覆盖率，
    /// 平均回 1× 之后就得到平滑的过渡，比直接 1× 渲染锐利得多。</para>
    ///
    /// <para>按 Alpha 加权平均颜色，否则透明区域的黑色会渗进边缘、形成一圈暗边。</para>
    /// </summary>
    public static BgraFrame DownscaleBox(BgraFrame source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (source.Width == width && source.Height == height) return source;

        var result = new BgraFrame(width, height);
        DownscaleBoxInto(source, result);
        return result;
    }

    /// <summary>
    /// 区域平均降采样，结果写入 <paramref name="dest"/>（尺寸由它决定）。
    ///
    /// <para>与 <see cref="UpscaleBilinearInto"/> 同理：上屏帧常达数百 KB ~ 数 MB，
    /// 超过 85KB 就进 LOH，逐帧分配会造成碎片化。调用方复用缓冲可彻底避免。</para>
    /// </summary>
    public static void DownscaleBoxInto(BgraFrame source, BgraFrame dest)
    {
        var width = dest.Width;
        var height = dest.Height;
        if (width <= 0 || height <= 0) return;

        // 尺寸一致时无需计算（调用方通常已提前判断，这里再兜一次）。
        if (source.Width == width && source.Height == height)
        {
            Array.Copy(source.Pixels, dest.Pixels, dest.Pixels.Length);
            return;
        }

        var result = dest;
        var src = source.Pixels;
        var dst = result.Pixels;

        var xRatio = source.Width / (double)width;
        var yRatio = source.Height / (double)height;

        for (var y = 0; y < height; y++)
        {
            var sy0 = (int)(y * yRatio);
            var sy1 = Math.Max(sy0 + 1, (int)((y + 1) * yRatio));
            sy1 = Math.Min(sy1, source.Height);

            for (var x = 0; x < width; x++)
            {
                var sx0 = (int)(x * xRatio);
                var sx1 = Math.Max(sx0 + 1, (int)((x + 1) * xRatio));
                sx1 = Math.Min(sx1, source.Width);

                double sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                var count = 0;

                for (var yy = sy0; yy < sy1; yy++)
                {
                    var row = yy * source.Stride;
                    for (var xx = sx0; xx < sx1; xx++)
                    {
                        var i = row + xx * 4;
                        var a = src[i + 3] / 255.0;
                        // 预乘后累加，避免透明区域的颜色污染边缘。
                        sumB += src[i] * a;
                        sumG += src[i + 1] * a;
                        sumR += src[i + 2] * a;
                        sumA += a;
                        count++;
                    }
                }

                var d = (y * width + x) * 4;
                if (count == 0 || sumA <= 0.0001)
                {
                    dst[d] = 0; dst[d + 1] = 0; dst[d + 2] = 0; dst[d + 3] = 0;
                    continue;
                }

                dst[d] = (byte)Math.Clamp(sumB / sumA + 0.5, 0, 255);
                dst[d + 1] = (byte)Math.Clamp(sumG / sumA + 0.5, 0, 255);
                dst[d + 2] = (byte)Math.Clamp(sumR / sumA + 0.5, 0, 255);
                dst[d + 3] = (byte)Math.Clamp(sumA / count * 255 + 0.5, 0, 255);
            }
        }
    }

    /// <summary>画一条胶囊形指示条（任务栏上表示"这个应用有窗口"的小横线）。</summary>
    public static void FillCapsule(
        BgraFrame frame, double centerX, double centerY, double width, double height,
        byte b, byte g, byte r, byte alpha)
    {
        FillRoundedRect(frame, centerX - width * 0.5, centerY - height * 0.5,
            width, height, height * 0.5, b, g, r, alpha);
    }

    /// <summary>画一个四格方块（Windows 徽标）。四代系统都没有稳定的"开始图标"索引可提取，自己画最稳。</summary>
    public static void DrawWindowsLogo(
        BgraFrame frame, double x, double y, double size, byte b, byte g, byte r, byte alpha)
    {
        var gap = Math.Max(1.0, size * 0.11);
        var cell = (size - gap) * 0.5;
        var radius = Math.Max(0.5, cell * 0.18);

        FillRoundedRect(frame, x, y, cell, cell, radius, b, g, r, alpha);
        FillRoundedRect(frame, x + cell + gap, y, cell, cell, radius, b, g, r, alpha);
        FillRoundedRect(frame, x, y + cell + gap, cell, cell, radius, b, g, r, alpha);
        FillRoundedRect(frame, x + cell + gap, y + cell + gap, cell, cell, radius, b, g, r, alpha);
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>圆角矩形内某点的覆盖率（0..1），带 1px 抗锯齿。</summary>
    private static double CornerCoverage(
        double px, double py, double x, double y, double w, double h, double radius)
    {
        if (radius <= 0) return 1.0;

        // 只在四个角区域内需要算距离，其余区域直接满覆盖。
        var cx = px < x + radius ? x + radius : (px > x + w - radius ? x + w - radius : px);
        var cy = py < y + radius ? y + radius : (py > y + h - radius ? y + h - radius : py);

        if (cx == px && cy == py) return 1.0;

        var dx = px - cx;
        var dy = py - cy;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        // distance <= radius-0.5 → 1；>= radius+0.5 → 0；中间线性过渡。
        var coverage = (radius + 0.5 - distance) / 1.0;
        return Math.Clamp(coverage, 0, 1);
    }

    private static byte Blend(byte dst, byte src, double srcAlpha, double dstAlpha, double outAlpha)
    {
        var value = (src * srcAlpha + dst * dstAlpha * (1 - srcAlpha)) / outAlpha;
        return (byte)Math.Clamp(value + 0.5, 0, 255);
    }
}
