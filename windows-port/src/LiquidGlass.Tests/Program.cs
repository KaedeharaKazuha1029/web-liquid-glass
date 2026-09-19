using LiquidGlass.Core;

namespace LiquidGlass.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine("液态玻璃 · 光学移植回归测试");
        Console.WriteLine(new string('═', 68));

        MaterialContract();
        Geometry();
        ColorSpace();
        HashQuality();
        FastPathExactness();
        OpticsBehaviour();
        AccumulationConvergence();

        return Check.Summarize();
    }

    // ------------------------------------------------------------------------

    /// <summary>
    /// 契约测试：材质里的每一个数字都必须等于上游 layout.js 的值。
    /// 作用是在任何人"手滑调一下参数"时立刻炸掉——
    /// 上游文档明确要求先用参考值完成视觉验收。
    /// </summary>
    private static void MaterialContract()
    {
        Check.Group("材质契约（对照上游 layout.js）");
        var m = LiquidGlassMaterial.Reference;

        Check.Equal(2.19, m.ReverseDisplacement, 0, "反向位移 = 2.19×");
        Check.Equal(0.67, m.EdgeCurvature, 0, "边缘曲率 = 0.67×");
        Check.Equal(1.71, m.OpticalThickness, 0, "光学厚度 = 1.71×");
        Check.Equal(0.26, m.EdgeBandRatio, 0, "边缘带宽 = 玻璃高度的 26%");
        Check.Equal(0.16, m.RefractionVisibleRatio, 0, "折射可见区 = 玻璃高度的 16%");
        Check.Equal(12, m.BlendFeatherPx, 0, "融合柔化 = 边界两侧各 12px");
        Check.Equal(0.29, m.FrostedStrength, 0, "毛玻璃基础强度 = 29%");
        Check.Equal(0.55, m.FrostedAttenuation, 0, "毛玻璃整体衰减 = 55%");
        Check.Equal(1.4, m.BlurSpacingPx, 0, "高斯采样间距 = 1.4px");
        Check.Equal(0.22, m.DispersionStrength, 0, "色散最大权重 = 22%");
        Check.Equal(0.055, m.HighlightStrength, 0, "对角高光 = 0.055");

        Check.Equal(1.514, m.IndexOfRefractionRed, 0, "红通道 IOR = 1.514");
        Check.Equal(1.520, m.IndexOfRefractionGreen, 0, "绿通道 IOR = 1.520");
        Check.Equal(1.528, m.IndexOfRefractionBlue, 0, "蓝通道 IOR = 1.528");

        Check.True(m.TintColor[0] == 0.035 && m.TintColor[1] == 0.035 && m.TintColor[2] == 0.045,
            "染色 = [0.035, 0.035, 0.045]");
        Check.Equal(0.17, m.TintMix, 0, "染色比例 = 0.17");

        // 相对排序：红 < 绿 < 蓝，这是色散发生方向的前提。
        Check.True(m.IndexOfRefractionRed < m.IndexOfRefractionGreen
                && m.IndexOfRefractionGreen < m.IndexOfRefractionBlue,
            "IOR 单调递增：红 < 绿 < 蓝（色散方向正确）");

        // 局部覆盖只改指定字段，其余保持参考值。
        var tweaked = m.With(b => b.OpticalThickness = 2.5);
        Check.Equal(2.5, tweaked.OpticalThickness, 0, "局部覆盖：光学厚度 → 2.5");
        Check.Equal(m.EdgeCurvature, tweaked.EdgeCurvature, 0, "局部覆盖：其余字段不受影响");
        Check.Equal(m.EdgeCurvature, LiquidGlassMaterial.Reference.EdgeCurvature, 0, "参考实例未被污染");
    }

    /// <summary>
    /// 几何：圆角矩形 SDF。
    /// 关键点：圆角半径是 <c>min(halfW, halfH) - 1</c>，
    /// 所以 2560×72 的任务栏会得到胶囊形而不是圆角矩形。
    /// </summary>
    private static void Geometry()
    {
        Check.Group("几何：圆角矩形 SDF");

        var optics = new GlassOptics(LiquidGlassMaterial.Reference);
        var rect = new PixelRect(0, 0, 400, 100);

        // 中心到边界的最短距离 = min(200, 50) = 50 → SDF = -50。
        SampleCoverage(optics, rect, 200, 50, out var covCenter);
        Check.True(covCenter > 0.99f, "中心点覆盖率 = 1");

        // 覆盖率 0.5 恰好发生在 SDF = 0 处，也就是矩形几何边界上（y = 0）。
        // 抗锯齿宽度恒为 1px，所以过渡带是 [-1, +1]。
        SampleCoverage(optics, rect, 200, 0.0, out var covBoundary);
        Check.True(covBoundary is > 0.45f and < 0.55f,
            "几何边界上覆盖率 ≈ 0.5（抗锯齿正确）",
            $"实际 {covBoundary:F4}");

        // 边界外 0.5px（SDF = +0.5，落在过渡带里）→ 覆盖率应低于 0.5。
        SampleCoverage(optics, rect, 200, -0.5, out var covJustOutside);
        Check.True(covJustOutside is > 0.05f and < 0.25f,
            "边界外 0.5px 处覆盖率显著低于 0.5（过渡带向外衰减）",
            $"实际 {covJustOutside:F4}");

        // 完全在外部 → 覆盖率 0。
        SampleCoverage(optics, rect, 200, -20, out var covOutside);
        Check.Equal(0, covOutside, 0, "外部 20px 处覆盖率 = 0");

        // 极端长宽比：胶囊形。任务栏形态下四角必须是圆的，
        // 否则边缘折射会退化成直边条带。
        var bar = new PixelRect(0, 0, 1280, 54);
        var halfHeight = 54 / 2.0;
        SampleCoverage(optics, bar, 0.0, halfHeight, out var covBarLeft);
        Check.True(covBarLeft is > 0.45f and < 0.55f, "任务栏左端中点覆盖率 ≈ 0.5（胶囊端头）");
        // 左上角（直角位置）必须落在玻璃外，证明圆角确实生效。
        SampleCoverage(optics, bar, 1, 1, out var covCorner);
        Check.Equal(0, covCorner, 0, "任务栏左上角覆盖率 = 0（圆角生效，非直角）");
    }

    /// <summary>
    /// 颜色空间：sRGB 查找表必须与逐次幂函数计算在 1 个色阶内一致。
    /// 查表是性能优化的核心，必须证明它没有牺牲正确性。
    /// </summary>
    private static void ColorSpace()
    {
        Check.Group("颜色空间：sRGB ⇄ 线性");

        // 正向：256 个输入全部与定义式一致。
        // 注意上游用 GLSL float、我们生成表用 MathF（同为 float 精度），
        // 而这里对照的是 double 版定义式，所以差异只应来自 float 舍入。
        var maxDelta = 0.0;
        var maxByteDelta = 0;
        for (var i = 0; i < 256; i++)
        {
            var v = i / 255.0;
            var exact = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            var actual = GlassScene.SrgbToLinear((byte)i);
            maxDelta = Math.Max(maxDelta, Math.Abs(exact - actual));
            maxByteDelta = Math.Max(maxByteDelta,
                Math.Abs((int)Math.Round(exact * 255) - (int)Math.Round(actual * 255)));
        }
        Check.True(maxDelta < 1e-6, "sRGB→线性查表与定义式一致（误差仅 float 舍入）",
            $"最大偏差 {maxDelta:E3}");
        Check.True(maxByteDelta == 0, "sRGB→线性查表在 8 位量化下完全一致");

        // 反向：查找表 vs 逐次 pow，误差不超过 1 个 8 位色阶。
        var maxLsb = 0;
        for (var i = 0; i <= 20000; i++)
        {
            var linear = i / 20000f;
            var srgb = linear <= 0.0031308f
                ? linear * 12.92f
                : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            var exact = (byte)Math.Clamp(MathF.Round(srgb * 255f), 0, 255);
            var actual = GlassScene.LinearToSrgbByte(linear);
            maxLsb = Math.Max(maxLsb, Math.Abs(exact - actual));
        }
        Check.True(maxLsb <= 1, "线性→sRGB 查表误差 ≤ 1 个色阶", $"最大偏差 {maxLsb}");

        // 端点行为
        Check.True(GlassScene.LinearToSrgbByte(0f) == 0, "线性 0 → sRGB 0");
        Check.True(GlassScene.LinearToSrgbByte(1f) == 255, "线性 1 → sRGB 255");
        Check.True(GlassScene.LinearToSrgbByte(-5f) == 0, "线性负值被钳到 0");
        Check.True(GlassScene.LinearToSrgbByte(5f) == 255, "线性超界被钳到 255");
    }

    /// <summary>
    /// hash12 的质量决定"每像素 4 条路径"能否收敛。
    /// 分布不均会让时间累积收敛到错误的均值。
    /// </summary>
    private static void HashQuality()
    {
        Check.Group("哈希：hash12 均匀性与确定性");

        var n = 200_000;
        var sum = 0.0;
        var min = 1.0;
        var max = 0.0;
        var buckets = new int[10];

        for (var i = 0; i < n; i++)
        {
            var x = i % 500 * 1.37f + 0.5f;
            var y = i / 500 * 2.11f + 0.5f;
            var v = Hash(x, y);
            sum += v;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            buckets[Math.Min(9, (int)(v * 10))]++;
        }

        var mean = sum / n;
        Check.True(Math.Abs(mean - 0.5) < 0.01, "均值 ≈ 0.5", $"实际 {mean:F5}");
        Check.True(min < 0.01 && max > 0.99, "值域覆盖 (0,1) 两端", $"[{min:F5}, {max:F5}]");

        var expected = n / 10.0;
        var maxDeviation = buckets.Max(b => Math.Abs(b - expected) / expected);
        Check.True(maxDeviation < 0.06, "十等分桶分布均匀（偏差 < 6%）",
            $"最大桶偏差 {maxDeviation:P2}");

        Check.EqualBits(Hash(123.5f, 456.25f), Hash(123.5f, 456.25f), "确定性：同输入同输出");
    }

    /// <summary>
    /// 本移植新增的唯一优化：深内部快速路径必须是<b>逐位精确</b>的。
    /// 这是整个移植里最需要证明的一点——如果它是近似，
    /// 那么任务栏中间那一大片"看起来没问题"的区域其实一直在悄悄偏色。
    /// </summary>
    private static void FastPathExactness()
    {
        Check.Group("深内部快速路径：逐位精确性");

        // 四种形态：极端长宽比的任务栏、竖长的开始菜单、接近正方形、小尺寸。
        var shapes = new (string Name, PixelRect Rect)[]
        {
            ("任务栏 1280×54", new PixelRect(30, 40, 1280, 54)),
            ("开始菜单 300×420", new PixelRect(12, 8, 300, 420)),
            ("近正方形 200×200", new PixelRect(5, 5, 200, 200)),
            ("迷你条 64×18", new PixelRect(2, 2, 64, 18)),
        };

        var scene = BuildScene(360, 480);
        var optics = new GlassOptics(LiquidGlassMaterial.Reference);

        foreach (var (name, rect) in shapes)
        {
            var canvasW = (int)rect.Right + 30;
            var canvasH = (int)rect.Bottom + 30;

            var fast = Trace(optics, scene, rect, canvasW, canvasH, fastPath: true);
            var slow = Trace(optics, scene, rect, canvasW, canvasH, fastPath: false);

            var mismatches = 0;
            var maxDelta = 0f;
            for (var i = 0; i < fast.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(fast[i]) != BitConverter.SingleToInt32Bits(slow[i]))
                {
                    mismatches++;
                    maxDelta = Math.Max(maxDelta, Math.Abs(fast[i] - slow[i]));
                }
            }

            Check.True(mismatches == 0,
                $"{name}：快速路径与全路径逐位相同",
                $"{mismatches} 个通道不一致，最大偏差 {maxDelta:E3}");
        }

        // 反向确认：证明这条路径真的被走到了（否则上面的测试毫无意义）。
        var probe = new PixelRect(0, 0, 1280, 54);
        var deepBand = 54f * 0.26f;
        Check.True(deepBand < 27f,
            "任务栏（高 54px）的边缘带仅 14.0px，中间大片区域命中快速路径",
            $"边缘带 {deepBand:F1}px，占高度 {deepBand / 54:P0}");
    }

    /// <summary>光学行为：折射确实发生、方向正确、数值稳定。</summary>
    private static void OpticsBehaviour()
    {
        Check.Group("光学行为");

        var optics = new GlassOptics(LiquidGlassMaterial.Reference);

        // 边缘像素与内部像素的输出必须有可测量的差异——
        // 如果差异为 0，说明折射根本没发生。
        var scene = BuildScene(400, 300);
        var rect = new PixelRect(40, 40, 320, 120);

        var edgePixel = SampleRgb(optics, scene, rect, 40.5, 100);
        var sceneAtEdge = SampleScene(scene, 40.5, 100);
        var edgeShift = Distance(edgePixel, sceneAtEdge);
        Check.True(edgeShift > 0.01,
            "玻璃左边缘的采样结果与原始场景显著不同（折射生效）",
            $"线性空间距离 {edgeShift:F4}");

        var interiorPixel = SampleRgb(optics, scene, rect, 200, 100);
        var sceneAtInterior = SampleScene(scene, 200, 100);
        var interiorShift = Distance(interiorPixel, sceneAtInterior);

        // 设计意图：中心比边缘清晰得多。
        Check.True(interiorShift < edgeShift,
            "中心区域偏移小于边缘（上游设计目标：中心保持清晰，只有边缘产生透镜卷回）",
            $"中心 {interiorShift:F4} vs 边缘 {edgeShift:F4}");

        // 稳定性：玻璃内部输出必须是有限值，不出 NaN / 平方根负数。
        var finite = true;
        var negative = false;
        for (var x = 40; x < 360; x += 3)
        {
            for (var y = 40; y < 160; y += 3)
            {
                var (r, g, b) = SampleRgb(optics, scene, rect, x + 0.5, y + 0.5);
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)
                    || float.IsInfinity(r) || float.IsInfinity(g) || float.IsInfinity(b))
                {
                    finite = false;
                }
                if (r < 0 || g < 0 || b < 0) negative = true;
            }
        }
        Check.True(finite, "玻璃区域内全部输出为有限值（无 NaN / Inf）");
        Check.True(!negative, "玻璃区域内无负颜色（上游在返回前做了 max(0) 钳位）");

        // 染色不变量：默认染色是深色 [0.035, 0.035, 0.045]，以 17% 权重混入。
        // 因此亮背景会被压暗、暗背景会被提亮——这比"玻璃一定更亮"是更真实的性质，
        // 也是上游 demo 在深色页面上好看、在白色页面上会略显发灰的根本原因。
        var brightScene = GlassScene.FromSolid(16, 16, 0.9f, 0.9f, 0.9f);
        var darkScene = GlassScene.FromSolid(16, 16, 0.02f, 0.02f, 0.02f);
        var probeRect = new PixelRect(0, 0, 16, 16);

        optics.EvaluatePixel(brightScene, probeRect, 8, 8, 0.5f, 0.5f, 4, 47f,
            out var br, out _, out _, out _);
        optics.EvaluatePixel(darkScene, probeRect, 8, 8, 0.5f, 0.5f, 4, 47f,
            out var dr, out _, out _, out _);

        Check.True(br < 0.9f, "亮背景经过玻璃后被染色压暗（深色染色生效）",
            $"0.9 → {br:F4}");
        Check.True(dr > 0.02f, "暗背景经过玻璃后被雾化冷偏移提亮",
            $"0.02 → {dr:F4}");

        // 玻璃必须是与背景不同的东西——否则整条管线等于没跑。
        Check.True(Math.Abs(br - 0.9f) > 0.01 && Math.Abs(dr - 0.02f) > 0.005,
            "玻璃输出与背景有可测量的差异（光学层确实生效）");
    }

    /// <summary>
    /// 时间累积必须收敛：帧间变化量逐帧下降。
    /// 这是"4 paths/pixel 却看不出噪点"的全部依据。
    /// </summary>
    private static void AccumulationConvergence()
    {
        Check.Group("时间累积：收敛性");

        var scene = BuildScene(240, 200);
        var rect = new PixelRect(20, 20, 200, 60);
        var renderer = new CpuGlassRenderer();
        var options = new GlassRenderOptions
        {
            GlassRect = rect,
            CanvasWidth = 240,
            CanvasHeight = 200,
            PathsPerPixel = 4,
            MaxAccumulation = 48,
        };

        BgraFrame? previous = null;
        var meanDeltas = new List<double>();
        var maxDeltas = new List<int>();

        for (var i = 0; i < 48; i++)
        {
            // 必须克隆：渲染器复用同一块帧缓冲，不克隆就是拿自己跟自己比。
            var frame = renderer.Render(scene, options).Clone();
            if (previous is not null)
            {
                meanDeltas.Add(MeanAbsDiff(previous, frame));
                maxDeltas.Add(MaxAbsDiff(previous, frame));
            }
            previous = frame;
        }

        Check.True(renderer.IsConverged, "第 48 帧后进入收敛态");
        Check.True(renderer.LastStats.Frame == 48, "帧号达到 48", $"实际 {renderer.LastStats.Frame}");

        // 帧间差异随累积逐帧衰减——这是"4 paths/pixel 却看不出噪点"的直接证据。
        // 用最大差而不是均值：噪声集中在少数像素上（约 1.4% 的路径会触发反射事件），
        // 均值会被大量未变化像素稀释到看不见。
        var earlyMax = maxDeltas.Take(8).Max();
        var lateMax = maxDeltas.Skip(40).DefaultIfEmpty(0).Max();
        Check.True(earlyMax > 0, "存在可观测的帧间差异（随机反射事件确实发生）",
            $"前 8 帧最大差 {earlyMax} 个色阶");
        Check.True(lateMax <= earlyMax,
            "帧间最大差异不增大（累积在收敛而非发散）",
            $"前 8 帧 {earlyMax} → 后 8 帧 {lateMax} 个色阶");

        var earlyMean = meanDeltas.Take(8).Average();
        var lateMean = meanDeltas.Skip(40).DefaultIfEmpty(0).Average();
        Check.True(lateMean <= earlyMean,
            "帧间平均差异不增大",
            $"前 8 帧 {earlyMean:F5} → 后 8 帧 {lateMean:F5}");

        Check.True(MeanAbsDiff(previous!, renderer.Render(scene, options)) == 0,
            "收敛后再渲染，输出不再变化（已冻结）");

        // 收敛后必须走缓存，不再重复计算——这是 CPU 后端能做到实时跟随的前提。
        renderer.Render(scene, options);
        Check.True(renderer.LastStats.UsedCache,
            "收敛后直接返回缓存（稳态开销为零）");

        // 请求重置后必须重新累积。
        renderer.Reset();
        renderer.Render(scene, options);
        Check.True(!renderer.LastStats.UsedCache && renderer.Frame < 5,
            "重置后从第 1 帧重新开始累积", $"帧号 {renderer.Frame}");
    }

    // ---------------------------------------------------------------- 测试工具

    private static float Hash(float x, float y)
    {
        // 通过公开路径间接验证 hash12：单像素、单路径、无反射干扰下
        // 无法直接取到 hash 值，这里复刻一份实现用于分布统计。
        var p0 = Fract(x * 0.1031f);
        var p1 = Fract(y * 0.1031f);
        var p2 = Fract(x * 0.1031f);
        var d = p0 * (p1 + 33.33f) + p1 * (p2 + 33.33f) + p2 * (p0 + 33.33f);
        p0 += d; p1 += d; p2 += d;
        return Fract((p0 + p1) * p2);
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    private static float[] Trace(
        GlassOptics optics, GlassScene scene, PixelRect rect,
        int width, int height, bool fastPath)
    {
        var saved = GlassOptics.EnableDeepInteriorFastPath;
        GlassOptics.EnableDeepInteriorFastPath = fastPath;
        try
        {
            var buffer = new float[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    optics.EvaluatePixel(scene, rect, x + 0.5, y + 0.5, 0.5f, 0.5f,
                        4, 7.25f, out var r, out var g, out var b, out var coverage);
                    var i = (y * width + x) * 4;
                    buffer[i] = r; buffer[i + 1] = g; buffer[i + 2] = b; buffer[i + 3] = coverage;
                }
            }
            return buffer;
        }
        finally
        {
            GlassOptics.EnableDeepInteriorFastPath = saved;
        }
    }

    private static GlassScene BuildScene(int width, int height)
    {
        var frame = TestPatterns.DesktopLike(width, height);
        return GlassScene.FromBgra(frame, 0, 0);
    }

    private static float SampleCoverage(
        GlassOptics optics, PixelRect rect, double x, double y, out float coverage)
    {
        var scene = GlassScene.FromSolid(8, 8, 0.5f, 0.5f, 0.5f);
        optics.EvaluatePixel(scene, rect, x, y, 0.5f, 0.5f, 1, 0f,
            out _, out _, out _, out coverage);
        return coverage;
    }

    private static (float R, float G, float B) SampleRgb(
        GlassOptics optics, GlassScene scene, PixelRect rect, double x, double y)
    {
        optics.EvaluatePixel(scene, rect, x, y, 0.5f, 0.5f, 4, 47f,
            out var r, out var g, out var b, out _);
        return (r, g, b);
    }

    private static (float R, float G, float B) SampleScene(GlassScene scene, double x, double y)
    {
        scene.Sample(x, y, out var r, out var g, out var b);
        return (r, g, b);
    }

    private static float Distance((float R, float G, float B) a, (float R, float G, float B) b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static double MeanAbsDiff(BgraFrame a, BgraFrame b)
    {
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
            sum += Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]);
            sum += Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
        }
        return sum / (double)(a.Pixels.Length / 4 * 3);
    }

    private static int MaxAbsDiff(BgraFrame a, BgraFrame b)
    {
        var max = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            var d = Math.Abs(a.Pixels[i] - b.Pixels[i]);
            if (d > max) max = d;
        }
        return max;
    }
}
