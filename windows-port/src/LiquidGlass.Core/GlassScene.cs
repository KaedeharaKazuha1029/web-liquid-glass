namespace LiquidGlass.Core;

using System.Runtime.CompilerServices;

/// <summary>
/// 折射场景：玻璃背后的世界，已转成线性光空间。
///
/// 上游对应物是 <c>scene-capture.js</c> 重绘出来的 <c>uScene</c> 纹理。
/// 在浏览器里它由 DOM 重绘产生；在 Windows 上它就是<b>屏幕像素本身</b>。
/// </summary>
public sealed class GlassScene
{
    /// <summary>线性空间 RGB，每像素 3 个 float，行主序。</summary>
    public float[] Linear { get; }

    /// <summary>文字保护遮罩，每像素 1 个 float（0..1）。为 null 表示全 0。</summary>
    public float[]? Mask { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>场景纹理左上角，在"画布局部像素坐标"中的位置（通常为负，表示向外扩了边）。</summary>
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }

    /// <summary>
    /// 画布坐标 → 纹理坐标的比例。
    ///
    /// <para>通常为 1（画布与屏幕 1:1）。当启用<b>超采样</b>时，玻璃几何会按
    /// 1.5× 分辨率计算以获得更平滑的边缘，但折射源没必要跟着放大 ——
    /// 屏幕本身就只有那么细，把纹理放大只是白费内存。
    /// 于是把场景留在原始分辨率，用这个比例把画布坐标换算回去即可。</para>
    /// </summary>
    public double Scale { get; private set; }

    // ══════════════════════════════════════════════════════════════════════
    //  金字塔：为什么模糊必须做成"预处理"，而不能写在逐像素核里
    // ══════════════════════════════════════════════════════════════════════
    //
    //  用户报的"重影"，根因是<b>模糊的支撑宽度不够</b>，而不是核的形状不好。
    //
    //  抑制一个周期为 T 的结构，核的支撑（半径）必须达到约一个周期 ——
    //  实测（以 |H| < 0.10 为准）：
    //
    //      背景结构周期      需要的支撑半径
    //        160px            76 屏幕px
    //        320px           148 屏幕px
    //        480px           220 屏幕px
    //
    //  而原来的十字核只有 ±56 屏幕px 的支撑，所以 320px 周期的结构
    //  还剩 62% 透出来。想靠"换个核形状"解决是徒劳的 ——
    //  实测过 3×3 方形核，反而更差（0.624 → 0.926），
    //  因为 28px 间距下的 3×3 网格比十字核采样得更稀疏。
    //
    //  要在逐像素循环里做到半径 148px 的采样，需要 (2×148+1)² ≈ 8.8 万次采样/像素。
    //  这道算术题在 CPU 上没有解。
    //
    //  ── 解法：把"大支撑"搬到预处理 ──
    //
    //  预先对整张场景做一次<b>多级降采样</b>（mip pyramid）：每级边长减半，
    //  用箱式平均（等价于 2×2 箱式模糊）。然后在逐像素循环里，
    //  按需要的半径直接取某一级的一个双线性样本 —— <b>O(1)</b>。
    //
    //  一个 2^k 级的金字塔，第 k 级每个样本已经平均了 4^k 个原始像素，
    //  其支撑半径正好是 2^k 像素。所以取第 k 级 ⇔ 半径 2^k 的箱式模糊。
    //
    //  把相邻两级按小数塔层做<b>三线性插值</b>，就能得到任意半径的连续模糊，
    //  视觉上完全平滑（这就是 GPU 上 mipmap 的标准做法）。
    //
    //  代价：建塔时每个像素只被读一次，总工作量 < 1.34 × 原始像素数，
    //  而且<b>每帧只建一次</b>（不是每像素一次）。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 金字塔的不可变快照。<b>构建完成后整体发布</b>，读侧永远看到自洽的一组
    /// 数组（见 <see cref="EnsurePyramid"/> 关于并发安全的说明）。
    /// </summary>
    private sealed class Pyramid
    {
        public required float[]?[] Levels { get; init; }
        public required int[] Widths { get; init; }
        public required int[] Heights { get; init; }
        public required int Generation { get; init; }
    }

    /// <summary>已发布的金字塔快照。null 表示尚未构建或已失效。</summary>
    private Pyramid? _pyramidPublished;

    /// <summary>重建金字塔时的互斥锁。</summary>
    private readonly object _pyramidSync = new();

    /// <summary>金字塔内容对应的"代次"。每次场景内容被刷新就自增，用于惰性失效。</summary>
    private int _generation;

    /// <summary>金字塔的级数（含原图那一级）。未构建时为 0。</summary>
    public int PyramidLevels => _pyramidPublished?.Levels.Length ?? 0;

    /// <summary>金字塔是否已构建且与当前内容一致。</summary>
    public bool HasPyramid => _pyramidPublished is { } p && p.Generation == _generation;

    /// <summary>
    /// 惰性构建金字塔。<see cref="SampleBlurred"/> 会自动调用，
    /// 调用方<b>不需要</b>显式调它 —— 除非想控制级数。
    ///
    /// <para>之所以做成惰性 + 自失效：场景的构建点有四五处
    /// （任务栏、开始菜单、预览工具、测试），逐个记得调 <see cref="BuildPyramid"/>
    /// 迟早会漏一个，而漏掉的后果是"那个界面还有重影"这种很难定位的现象。
    /// 由场景自己在内容变化时置脏、在第一次采样时重建，就没有漏的可能。</para>
    /// </summary>
    private void EnsurePyramid()
    {
        if (_pyramidPublished?.Generation == _generation) return;

        // ⚠️ 这段必须串行化。
        //
        // SampleBlurred 是从 CpuGlassRenderer 的 Parallel.For 里被并发调用的
        // （每个像素一次），所以这个方法天然是多线程入口。
        // 之前没有加锁、又直接写 _pyramid / _pyramidWidth 这几个数组字段，
        // 于是：线程 A 正在写 _pyramid[3]，线程 B 已经读到 _pyramidWidth[3]
        // 却发现 _pyramid 只有 2 个元素（或反之）→ NullReferenceException。
        // 实测这个崩溃把整个替换任务栏的启动都打挂了
        // （ReplacementTaskbar.RunStartupProbe → CpuGlassRenderer.Render）。
        //
        // 这里用双检锁：先无锁读一次已发布的实例（热路径，零开销），
        // 未命中才进锁重建。锁定期间其他线程会短暂等待 ——
        // 但这只发生在每帧第一次采样时（约几毫秒），可以接受。
        lock (_pyramidSync)
        {
            if (_pyramidPublished?.Generation == _generation) return;

            var built = BuildPyramidCore();
            // 用一次引用赋值"原子发布"：读侧要么看到完整的旧实例，
            // 要么看到完整的新实例，绝不会看到半成品。
            Volatile.Write(ref _pyramidPublished, built);
        }
    }

    /// <summary>标记内容已变，金字塔需要重建。</summary>
    private void InvalidatePyramid() => _pyramidPublished = null;

    /// <summary>
    /// 构建多级降采样金字塔并发布。
    ///
    /// <para>每级用 2×2 箱式平均，因此第 k 级的每个样本等于
    /// 原始图像上一个 2^k 边长方块的均值 —— 支撑半径正好 2^k 像素，
    /// 且是<b>方形</b>支撑（对角线方向同样被抑制）。</para>
    ///
    /// <para>可以手动调用来控制级数；不过 <see cref="SampleBlurred"/> 也会
    /// 按需自动调用，所以调用方忘了调也不会退化成点采样。</para>
    /// </summary>
    /// <param name="levels">
    /// 要构建的级数。默认 8 级 → 最高半径 2^7 = 128 纹理像素。
    /// </param>
    public void BuildPyramid(int levels = 8)
    {
        lock (_pyramidSync)
        {
            Volatile.Write(ref _pyramidPublished, BuildPyramidCore(levels));
        }
    }

    /// <summary>真正干活的部分，返回一个自洽的不可变快照。</summary>
    private Pyramid BuildPyramidCore(int levels = 8)
    {
        if (levels < 1) levels = 1;

        // 内存保护：金字塔总大小 < 1.34 × 原图，但原图本身就可能很大。
        // 这里按 3 通道 float 估算，超过 512MB 就砍级数。
        const long MaxBytes = 512L * 1024 * 1024;
        var bytes = (long)Width * Height * 3 * 4;
        var allow = 1;
        var w = Width;
        var h = Height;
        while (allow < levels && w > 4 && h > 4)
        {
            var next = bytes + (long)(w / 2) * (h / 2) * 3 * 4;
            if (next > MaxBytes) break;
            bytes = next;
            w /= 2;
            h /= 2;
            allow++;
        }
        levels = allow;

        var pyr = new float[]?[levels];
        var pyrW = new int[levels];
        var pyrH = new int[levels];

        pyr[0] = Linear;
        pyrW[0] = Width;
        pyrH[0] = Height;

        for (var k = 1; k < levels; k++)
        {
            var pw = pyrW[k - 1];
            var ph = pyrH[k - 1];
            var nw = pw > 1 ? pw / 2 : 1;
            var nh = ph > 1 ? ph / 2 : 1;

            var src = pyr[k - 1]!;
            var dst = new float[nw * nh * 3];

            for (var y = 0; y < nh; y++)
            {
                var y0 = y * 2;
                var y1 = y0 + 1 < ph ? y0 + 1 : y0;
                var row0 = y0 * pw;
                var row1 = y1 * pw;
                var drow = y * nw;

                for (var x = 0; x < nw; x++)
                {
                    var x0 = x * 2;
                    var x1 = x0 + 1 < pw ? x0 + 1 : x0;

                    var i00 = (row0 + x0) * 3;
                    var i10 = (row0 + x1) * 3;
                    var i01 = (row1 + x0) * 3;
                    var i11 = (row1 + x1) * 3;

                    var d = (drow + x) * 3;
                    dst[d] = (src[i00] + src[i10] + src[i01] + src[i11]) * 0.25f;
                    dst[d + 1] = (src[i00 + 1] + src[i10 + 1] + src[i01 + 1] + src[i11 + 1]) * 0.25f;
                    dst[d + 2] = (src[i00 + 2] + src[i10 + 2] + src[i01 + 2] + src[i11 + 2]) * 0.25f;
                }
            }

            pyr[k] = dst;
            pyrW[k] = nw;
            pyrH[k] = nh;
        }

        return new Pyramid
        {
            Levels = pyr,
            Widths = pyrW,
            Heights = pyrH,
            Generation = _generation,
        };
    }

    /// <summary>
    /// 从金字塔取一个<b>指定支撑半径</b>的模糊样本。半径单位是纹理像素。
    ///
    /// <para>先在连续的"塔层坐标"上定位（<c>log2(radius)</c>），
    /// 再对相邻两级各做双线性采样并按小数部分混合 —— 即三线性插值。
    /// 这样任意半径都是连续的，不会出现"跨级跳变"的台阶感。</para>
    ///
    /// <para><b>线程安全</b>：本方法会被 <c>CpuGlassRenderer</c> 从
    /// <c>Parallel.For</c> 里并发调用，所以只读一次已发布的金字塔快照，
    /// 不做任何共享状态写入。</para>
    /// </summary>
    public void SampleBlurred(double pixelX, double pixelY, double radiusPixels,
        out float r, out float g, out float b)
    {
        EnsurePyramid();

        // 取本地引用：即使另一个线程此刻把 _pyramidPublished 换掉，
        // 我们手里的这个快照也是自洽的（数组长度与内容匹配）。
        var pyr = Volatile.Read(ref _pyramidPublished);
        if (pyr is null)
        {
            Sample(pixelX, pixelY, out r, out g, out b);
            return;
        }

        var maxLevel = pyr.Levels.Length - 1;

        // 半径 2^k 对应第 k 级。level = log2(radius)，夹到可用范围。
        double level;
        if (radiusPixels <= 1.0) level = 0;
        else level = Math.Log2(radiusPixels);

        if (level <= 0) level = 0;
        if (level >= maxLevel) level = maxLevel;

        var lo = (int)Math.Floor(level);
        var hi = lo + 1 <= maxLevel ? lo + 1 : maxLevel;
        var frac = (float)(level - lo);
        if (hi == lo) frac = 0f;

        SampleLevel(pyr, lo, pixelX, pixelY, out var r0, out var g0, out var b0);
        if (frac <= 0f)
        {
            r = r0; g = g0; b = b0;
            return;
        }

        SampleLevel(pyr, hi, pixelX, pixelY, out var r1, out var g1, out var b1);

        r = r0 + (r1 - r0) * frac;
        g = g0 + (g1 - g0) * frac;
        b = b0 + (b1 - b0) * frac;
    }

    /// <summary>在给定金字塔快照的第 <paramref name="level"/> 级做一次双线性采样。</summary>
    private void SampleLevel(Pyramid pyr, int level, double pixelX, double pixelY,
        out float r, out float g, out float b)
    {
        var data = pyr.Levels[level];
        if (data is null)
        {
            Sample(pixelX, pixelY, out r, out g, out b);
            return;
        }

        var w = pyr.Widths[level];
        var h = pyr.Heights[level];

        // 画布坐标 → 本级坐标：先按 Scale 折回纹理空间，再除以 2^level。
        var inv = 1.0 / (1 << level);
        var sx = (pixelX / Scale - OriginX) * inv;
        var sy = (pixelY / Scale - OriginY) * inv;

        if (sx <= 0) sx = 0; else if (sx >= w - 1) sx = w - 1;
        if (sy <= 0) sy = 0; else if (sy >= h - 1) sy = h - 1;

        var x0 = (int)sx;
        var y0 = (int)sy;
        var x1 = x0 + 1 < w ? x0 + 1 : x0;
        var y1 = y0 + 1 < h ? y0 + 1 : y0;

        var fx = (float)(sx - x0);
        var fy = (float)(sy - y0);

        var i00 = (y0 * w + x0) * 3;
        var i10 = (y0 * w + x1) * 3;
        var i01 = (y1 * w + x0) * 3;
        var i11 = (y1 * w + x1) * 3;

        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;

        r = data[i00] * w00 + data[i10] * w10 + data[i01] * w01 + data[i11] * w11;
        g = data[i00 + 1] * w00 + data[i10 + 1] * w10 + data[i01 + 1] * w01 + data[i11 + 1] * w11;
        b = data[i00 + 2] * w00 + data[i10 + 2] * w10 + data[i01 + 2] * w01 + data[i11 + 2] * w11;
    }

    private GlassScene(float[] linear, float[]? mask, int width, int height,
        double originX, double originY, double scale)
    {
        Linear = linear;
        Mask = mask;
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
        Scale = scale <= 0 ? 1 : scale;
    }

    /// <summary>
    /// 用新的屏幕帧**原地刷新**本场景。尺寸不一致时返回 false（调用方应改为新建）。
    ///
    /// <para><b>为什么需要它</b>：<see cref="Linear"/> 的大小是 <c>像素数 × 3 × 4 字节</c>。
    /// 开始菜单的场景是 1554×1462，也就是 <b>27MB</b> —— 远超 85KB，
    /// 必然进 <b>LOH（大对象堆）</b>，而 LOH 默认不压缩。
    /// 每打开一次菜单就分配一块 27MB，反复开合会造成严重的碎片化与内存虚高。
    /// 尺寸不变时复用同一块数组即可彻底消除这块 churn。</para>
    ///
    /// <para>与 <see cref="FromBgra"/> 逐像素等价（同一套 sRGB→线性转换）。</para>
    /// </summary>
    public bool TryUpdateFromBgra(BgraFrame frame, double originX, double originY, double scale)
    {
        if (frame.Width != Width || frame.Height != Height) return false;

        var count = Width * Height;
        if (Linear.Length < count * 3) return false;

        var src = frame.Pixels;
        var dst = 0;

        for (var i = 0; i < count; i++)
        {
            var si = i * 4;
            Linear[dst] = SrgbToLinear(src[si + 2]);      // R
            Linear[dst + 1] = SrgbToLinear(src[si + 1]);  // G
            Linear[dst + 2] = SrgbToLinear(src[si]);      // B
            dst += 3;
        }

        OriginX = originX;
        OriginY = originY;
        Scale = scale <= 0 ? 1 : scale;
        _generation++;
        InvalidatePyramid();
        return true;
    }

    /// <summary>
    /// 从 BGRA 屏幕帧构建场景。
    /// sRGB → 线性转换在此<b>一次性</b>完成，避免每像素重复转换——
    /// 上游在 shader 里逐次 <c>srgbToLinear()</c>，语义等价，这里只是把它提前。
    /// </summary>
    /// <param name="frame">屏幕采样结果，BGRA、straight alpha、自上而下。</param>
    /// <param name="originX">该帧左上角在画布局部像素坐标中的 X。</param>
    /// <param name="originY">该帧左上角在画布局部像素坐标中的 Y。</param>
    /// <param name="mask">可选的文字保护遮罩（0..1），长度须等于像素数。</param>
    /// <param name="scale">画布坐标与纹理坐标的比例，1 表示 1:1（默认）。</param>
    public static GlassScene FromBgra(BgraFrame frame, double originX, double originY,
        float[]? mask = null, double scale = 1)
    {
        var count = frame.Width * frame.Height;
        var linear = new float[count * 3];
        var src = frame.Pixels;
        var dst = 0;

        for (var i = 0; i < count; i++)
        {
            var si = i * 4;
            linear[dst] = SrgbToLinear(src[si + 2]);      // R
            linear[dst + 1] = SrgbToLinear(src[si + 1]);  // G
            linear[dst + 2] = SrgbToLinear(src[si]);      // B
            dst += 3;
        }

        if (mask is not null && mask.Length != count)
        {
            throw new ArgumentException($"遮罩长度 {mask.Length} 与像素数 {count} 不符。", nameof(mask));
        }

        return new GlassScene(linear, mask, frame.Width, frame.Height, originX, originY, scale);
    }

    /// <summary>从纯色构建场景（用于自检与预览工具）。</summary>
    public static GlassScene FromSolid(int width, int height, float r, float g, float b)
    {
        var linear = new float[width * height * 3];
        for (var i = 0; i < linear.Length; i += 3)
        {
            linear[i] = r; linear[i + 1] = g; linear[i + 2] = b;
        }
        return new GlassScene(linear, null, width, height, 0, 0, 1);
    }

    /// <summary>
    /// 上游 <c>sceneSample()</c> 的对应物：把画布局部像素坐标映射到纹理并双线性采样。
    ///
    /// 上游用 <c>topPixelToUv()</c> 把像素转成 uv 后交给 <c>texture()</c>，
    /// 且 uv 会被 clamp 到 [0,1]——等价于 CLAMP_TO_EDGE。这里保持一致。
    /// </summary>
    public void Sample(double pixelX, double pixelY, out float r, out float g, out float b)
    {
        // 画布坐标 → 纹理坐标。超采样时 Scale > 1，纹理保持屏幕原始分辨率。
        var sx = pixelX / Scale - OriginX;
        var sy = pixelY / Scale - OriginY;

        // 上游：clamp(pixel.x / uResolution.x, 0, 1)，再 clamp 采样 →
        // 越界取边缘像素，与 CLAMP_TO_EDGE 一致。
        if (sx <= 0) sx = 0;
        else if (sx >= Width - 1) sx = Width - 1;
        if (sy <= 0) sy = 0;
        else if (sy >= Height - 1) sy = Height - 1;

        var x0 = (int)sx;
        var y0 = (int)sy;
        var x1 = x0 + 1 < Width ? x0 + 1 : x0;
        var y1 = y0 + 1 < Height ? y0 + 1 : y0;

        var fx = (float)(sx - x0);
        var fy = (float)(sy - y0);

        var i00 = (y0 * Width + x0) * 3;
        var i10 = (y0 * Width + x1) * 3;
        var i01 = (y1 * Width + x0) * 3;
        var i11 = (y1 * Width + x1) * 3;

        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;

        r = Linear[i00] * w00 + Linear[i10] * w10 + Linear[i01] * w01 + Linear[i11] * w11;
        g = Linear[i00 + 1] * w00 + Linear[i10 + 1] * w10 + Linear[i01 + 1] * w01 + Linear[i11 + 1] * w11;
        b = Linear[i00 + 2] * w00 + Linear[i10 + 2] * w10 + Linear[i01 + 2] * w01 + Linear[i11 + 2] * w11;
    }

    /// <summary>上游 <c>maskSample()</c> 的对应物。无遮罩时恒为 0。</summary>
    public float SampleMask(double pixelX, double pixelY)
    {
        if (Mask is null) return 0f;

        var sx = pixelX / Scale - OriginX;
        var sy = pixelY / Scale - OriginY;
        if (sx <= 0) sx = 0; else if (sx >= Width - 1) sx = Width - 1;
        if (sy <= 0) sy = 0; else if (sy >= Height - 1) sy = Height - 1;

        var x0 = (int)sx;
        var y0 = (int)sy;
        var x1 = x0 + 1 < Width ? x0 + 1 : x0;
        var y1 = y0 + 1 < Height ? y0 + 1 : y0;
        var fx = (float)(sx - x0);
        var fy = (float)(sy - y0);

        var v00 = Mask[y0 * Width + x0];
        var v10 = Mask[y0 * Width + x1];
        var v01 = Mask[y1 * Width + x0];
        var v11 = Mask[y1 * Width + x1];

        return v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
             + v01 * (1 - fx) * fy + v11 * fx * fy;
    }

    /// <summary>上游 <c>srgbToLinear()</c>：单通道 sRGB → 线性。</summary>
    /// <remarks>
    /// 输入是 8 位整数，只有 256 种可能，所以直接查表。
    /// 上游在 shader 里逐次调用 <c>pow()</c>；在 CPU 上那是灾难性的慢，
    /// 而这里的定义域是离散的，查表与逐次计算<b>逐位相同</b>。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SrgbToLinear(byte channel) => SrgbByteToLinear[channel];

    /// <summary>上游 display pass 的 <c>linearToSrgb()</c>：线性 → sRGB 8 位。</summary>
    /// <remarks>
    /// sRGB 传递函数在 0.0031308 以下是直线段，可以直接乘；
    /// 以上是幂函数段，用 4096 级查找表替代 <c>pow()</c>。
    /// 最坏量化误差约 0.03 个 8 位色阶，肉眼与逐次计算不可分辨。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToSrgbByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 1f) return 255;
        if (value < 0.0031308f)
        {
            return (byte)(value * 12.92f * 255f + 0.5f);
        }
        var index = (int)((value - 0.0031308f) * LinearToSrgbScale + 0.5f);
        if (index < 0) index = 0;
        else if (index >= LinearToSrgbLutSize) index = LinearToSrgbLutSize - 1;
        return LinearToSrgbLut[index];
    }

    private const int LinearToSrgbLutSize = 4096;
    private const float LinearToSrgbScale = (LinearToSrgbLutSize - 1) / (1f - 0.0031308f);

    private static readonly float[] SrgbByteToLinear = BuildSrgbByteToLinearTable();
    private static readonly byte[] LinearToSrgbLut = BuildLinearToSrgbTable();

    private static float[] BuildSrgbByteToLinearTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var v = i / 255f;
            table[i] = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
        return table;
    }

    private static byte[] BuildLinearToSrgbTable()
    {
        var table = new byte[LinearToSrgbLutSize];
        for (var i = 0; i < LinearToSrgbLutSize; i++)
        {
            var v = 0.0031308f + i / LinearToSrgbScale;
            var srgb = 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            table[i] = srgb >= 1f ? (byte)255 : (byte)(srgb * 255f + 0.5f);
        }
        return table;
    }
}
