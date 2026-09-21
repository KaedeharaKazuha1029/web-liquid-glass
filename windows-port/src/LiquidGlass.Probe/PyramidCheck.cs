// 直接验证 GlassScene 金字塔的"结构抹除"能力。
//
// 方法：造一张有已知周期条纹的背景 → 建金字塔 → 用 SampleBlurred
// 取模糊结果 → 量残余条纹振幅。对比"不开金字塔"（直接点采样）。
using LiquidGlass.Core;

namespace LiquidGlass.Probe;

internal static class PyramidCheck
{
    public static int Run()
    {
        Console.WriteLine("════════ 背景结构抹除能力验收（金字塔 vs 点采样）════════");
        Console.WriteLine();

        const int W = 512;
        const int H = 512;

        Console.WriteLine("背景：512×512 的正弦条纹，明暗对比 ±0.5（模拟文字排版的灰度起伏）");
        Console.WriteLine("指标：残余振幅 / 原振幅。0 = 完全抹平，1 = 原样透出。");
        Console.WriteLine();
        Console.WriteLine("周期T    点采样(旧)     半径24    半径48    半径96    半径128   半径192");
        Console.WriteLine("─────────────────────────────────────────────────────────────────────────────");

        var fail = 0;
        foreach (var period in new[] { 40, 80, 160, 320, 480, 640 })
        {
            var frame = MakeStripes(W, H, period);
            var scene = GlassScene.FromBgra(frame, 0, 0);
            scene.BuildPyramid(9);

            var rPoint = Measure(frame, (x, y) => SampleDirect(scene, x, y));
            var r24 = Measure(frame, (x, y) => SampleBlur(scene, x, y, 24));
            var r48 = Measure(frame, (x, y) => SampleBlur(scene, x, y, 48));
            var r96 = Measure(frame, (x, y) => SampleBlur(scene, x, y, 96));
            var r128 = Measure(frame, (x, y) => SampleBlur(scene, x, y, 128));
            var r192 = Measure(frame, (x, y) => SampleBlur(scene, x, y, 192));

            Console.WriteLine($"{period,5}px  {rPoint,10:F3}  {r24,7:F3}  {r48,7:F3}  {r96,7:F3}  {r128,7:F3}  {r192,7:F3}");

            // 验收线：默认半径（96）要把 160px 及以下的结构压到 40% 以下
            if (period <= 160 && r96 > 0.40) fail++;
            // 点采样必须几乎不衰减 —— 否则说明基准本身就有问题
            if (period <= 320 && rPoint < 0.80) fail++;
        }

        Console.WriteLine();
        Console.WriteLine("对照说明：");
        Console.WriteLine("  · 「点采样」= 不建金字塔时 SampleBlurred 的退化路径（等价旧行为）");
        Console.WriteLine("    —— 它对所有周期都接近 1.0，证明「结构抹不掉」不是参数没调好，");
        Console.WriteLine("       而是【支撑宽度不够】这个结构性缺陷。");
        Console.WriteLine("  · 半径 96 = 默认 FrostedBlurRadius");
        Console.WriteLine();
        Console.WriteLine("注：条纹周期 T 需要大概 T/2 的支撑半径才能压平（|H|<0.1），");
        Console.WriteLine("    所以 320px 以上的结构需要半径 150+ 才明显 —— 这是物理限制。");
        Console.WriteLine("    好在这类大周期结构在视觉上更像「整体明暗」，不像文字重影。");

        Console.WriteLine();
        if (fail == 0)
        {
            Console.WriteLine("✓ 160px 及以下的背景结构，默认半径 96 下残余 < 40%；");
            Console.WriteLine("  而旧的点采样路径在任何周期下都 > 80% —— 重影的根因已被消除。");
        }
        else
        {
            Console.WriteLine($"× 有 {fail} 项未达标");
        }

        // ── 惰性构建验证：金字塔必须自己长出来，不需要调用方记得建 ──
        Console.WriteLine();
        Console.WriteLine("── 惰性构建（漏建保护）──");
        var fresh = GlassScene.FromBgra(MakeStripes(256, 256, 160), 0, 0);
        Console.WriteLine($"   刚建好的场景 HasPyramid = {fresh.HasPyramid}（应为 False）");
        var lazyOk = true;
        if (fresh.HasPyramid) { Console.WriteLine("   × 不该一建好就有金字塔"); lazyOk = false; }

        fresh.SampleBlurred(128, 128, 96, out _, out _, out _);
        Console.WriteLine($"   取一次模糊样本后 HasPyramid = {fresh.HasPyramid}（应为 True）");
        if (!fresh.HasPyramid) { Console.WriteLine("   × 金字塔没有被惰性创建"); lazyOk = false; }

        // 内容刷新后必须自失效，否则会用到旧背景的模糊 → 又变成重影
        fresh.TryUpdateFromBgra(MakeStripes(256, 256, 160), 0, 0, 1);
        Console.WriteLine($"   刷新内容后 HasPyramid = {fresh.HasPyramid}（应为 False，已自失效）");
        if (fresh.HasPyramid) { Console.WriteLine("   × 内容变了金字塔没失效，会用到旧背景 → 重影"); lazyOk = false; }

        Console.WriteLine(lazyOk ? "   ✓ 惰性构建与自失效均正常" : "   × 惰性构建有问题");
        if (!lazyOk) fail++;

        // ── 并发安全：这才是真正把替换任务栏打挂的那个 bug ──
        //
        // SampleBlurred 是从 CpuGlassRenderer 的 Parallel.For 里被并发调用的。
        // 最初实现直接写 _pyramid 等共享数组字段，多线程下读到半成品 →
        // NullReferenceException → ReplacementTaskbar.RunStartupProbe 抛异常 →
        // 整个替换任务栏启动失败。
        //
        // 这里用高并发反复触发"未建 → 建"的窗口，检验修复。
        Console.WriteLine();
        Console.WriteLine("── 并发安全（多重线程首帧同时触建金字塔）──");

        var raceFail = 0;
        for (var round = 0; round < 12; round++)
        {
            // 每一轮都用全新场景，保证是"未建"状态起步
            var s = GlassScene.FromBgra(MakeStripes(220, 220, 160), 0, 0);
            try
            {
                Parallel.For(0, 64, i =>
                {
                    // 全部线程都在金字塔还没建好时冲进来
                    s.SampleBlurred(100 + (i % 20), 100 + (i % 17), 96,
                        out _, out _, out _);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   第 {round + 1} 轮抛异常：{ex.GetType().Name}: {ex.Message}");
                raceFail++;
            }
        }

        if (raceFail == 0)
        {
            Console.WriteLine("   12 轮 × 64 并发首帧采样：全部无异常 ✓");
        }
        else
        {
            Console.WriteLine($"   × 有 {raceFail} 轮抛异常 —— 并发保护没生效");
            fail += raceFail;
        }

        // 顺带验证：并发下结果必须一致（不能因竞争而算出不同值）
        var refScene = GlassScene.FromBgra(MakeStripes(220, 220, 160), 0, 0);
        refScene.BuildPyramid(9);
        refScene.SampleBlurred(110, 110, 96, out _, out var expect, out _);

        var s2 = GlassScene.FromBgra(MakeStripes(220, 220, 160), 0, 0);
        var results = new double[64];
        Parallel.For(0, 64, i =>
        {
            s2.SampleBlurred(110, 110, 96, out _, out var g, out _);
            results[i] = g;
        });
        var spread = results.Max() - results.Min();
        Console.WriteLine($"   并发结果离散度 = {spread:E2}（应为 0，说明无竞争写入）");
        if (spread > 1e-6) { Console.WriteLine("   × 并发结果不一致"); fail++; }

        Console.WriteLine();

        return fail == 0 ? 0 : 1;
    }

    /// <summary>生成竖直正弦条纹。</summary>
    private static BgraFrame MakeStripes(int w, int h, double period)
    {
        var f = new BgraFrame(w, h);
        var px = f.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = Math.Sin(2 * Math.PI * x / period);
                var g = (byte)Math.Clamp(128 + v * 127, 0, 255);
                var i = (y * w + x) * 4;
                px[i] = g; px[i + 1] = g; px[i + 2] = g; px[i + 3] = 255;
            }
        }
        return f;
    }

    private static double SampleDirect(GlassScene s, int x, int y)
    {
        s.Sample(x + 0.5, y + 0.5, out _, out var g, out _);
        return g;
    }

    private static double SampleBlur(GlassScene s, int x, int y, double radius)
    {
        s.SampleBlurred(x + 0.5, y + 0.5, radius, out _, out var g, out _);
        return g;
    }

    /// <summary>沿扫描线量残余振幅（中间 100px 的平均起伏）。</summary>
    private static double Measure(BgraFrame src, Func<int, int, double> sample)
    {
        // 原振幅：从源图直接量
        var original = Amplitude((x, y) =>
        {
            var i = (y * src.Width + x) * 4;
            return src.Pixels[i + 1] / 255.0;
        });

        var blurred = Amplitude((x, y) => sample(x, y));

        return original <= 1e-9 ? 0 : blurred / original;
    }

    private static double Amplitude(Func<int, int, double> f)
    {
        // 取中间一条扫描线，去掉两端各 30% 避免 clamp 边缘影响
        const int y = 256;
        var lo = double.MaxValue;
        var hi = double.MinValue;
        for (var x = 150; x < 360; x++)
        {
            var v = f(x, y);
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        return (hi - lo) * 0.5;
    }
}
