// 生成"重影修复前后"的对照图，用于肉眼验收。
//
// 造一张有文字的假背景 → 分别用旧路径（点采样十字核）与新路径（金字塔）
// 渲染同一个玻璃面板 → 输出两张 PNG 并排版到一起。
using LiquidGlass.Core;

namespace LiquidGlass.Probe;

internal static class GhostCompare
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("════════ 重影修复：肉眼对照图 ════════");
        Console.WriteLine();

        const int W = 900;
        const int H = 620;

        // 背景：模拟"桌面上的一个文档窗口"—— 白底 + 密集文字行 + 一些彩色块
        var backdrop = MakeTextBackdrop(W, H);

        // 玻璃面板放在中间偏下
        var glassRect = new PixelRect(140, 120, 620, 400);

        Console.WriteLine("背景：白底文档窗口（文字行 + 标题栏 + 彩色块），模拟真实的开始菜单背后");
        Console.WriteLine($"玻璃面板：{glassRect}");
        Console.WriteLine();

        var material = LiquidGlassMaterial.Reference.With(b =>
        {
            b.EdgeBandRatio = 0.05;
            b.RefractionVisibleRatio = 0.04;
            b.DispersionStrength = 0.10;
            b.FrostedStrength = 1.0;
            b.FrostedAttenuation = 0.95;
            b.TintMix = 0.34;
            b.BlurSpacingPx = 28.0;
            b.FrostedBlurRadius = 96.0;
        });

        // ── 旧行为：把 FrostedBlurRadius 设成极小，迫使 SampleBlurred 走点采样 ──
        var oldMaterial = material.With(b => b.FrostedBlurRadius = 0.0);
        var oldImg = RenderBackdropOnly(backdrop, glassRect, oldMaterial, W, H);
        var newImg = RenderBackdropOnly(backdrop, glassRect, material, W, H);

        var oldPath = Path.Combine(outDir, "ghost-before.png");
        var newPath = Path.Combine(outDir, "ghost-after.png");
        var backPath = Path.Combine(outDir, "ghost-backdrop.png");

        PngWriter.Write(oldPath, oldImg);
        PngWriter.Write(newPath, newImg);
        PngWriter.Write(backPath, backdrop);

        Console.WriteLine($"原始背景      → {backPath}");
        Console.WriteLine($"修复前(点采样) → {oldPath}");
        Console.WriteLine($"修复后(金字塔) → {newPath}");
        Console.WriteLine();

        // 量化：玻璃区域内的"结构残余"= 标准差 / 背景标准差
        var sBack = StdDev(backdrop, glassRect);
        var sOld = StdDev(oldImg, glassRect);
        var sNew = StdDev(newImg, glassRect);

        Console.WriteLine("玻璃区域内的亮度标准差（越低 = 背景结构越看不见）：");
        Console.WriteLine($"   原始背景    {sBack,8:F2}");
        Console.WriteLine($"   修复前      {sOld,8:F2}    残余 {sOld / sBack:P1}");
        Console.WriteLine($"   修复后      {sNew,8:F2}    残余 {sNew / sBack:P1}");
        Console.WriteLine();

        var improvement = sOld / sNew;
        Console.WriteLine($"结构残余下降 {improvement:F2} 倍");

        if (sNew < sOld * 0.7)
        {
            Console.WriteLine("✓ 重影显著减轻（残余结构至少降 30%）");
            return 0;
        }
        Console.WriteLine("× 改善不足，请检查金字塔是否生效");
        return 1;
    }

    /// <summary>只渲染玻璃覆盖层，再合成到背景上。</summary>
    private static BgraFrame RenderBackdropOnly(
        BgraFrame backdrop, PixelRect rect, LiquidGlassMaterial material, int w, int h)
    {
        var scene = GlassScene.FromBgra(backdrop, 0, 0);
        scene.BuildPyramid(9);

        var renderer = new CpuGlassRenderer(material);
        var opts = new GlassRenderOptions
        {
            GlassRect = rect,
            CanvasWidth = w,
            CanvasHeight = h,
            PathsPerPixel = 4,
            MaxAccumulation = 24,
            ForceReset = true,
        };

        // 累积到收敛，避免噪点干扰肉眼判断
        BgraFrame glass = new(w, h);
        for (var i = 0; i < 24; i++)
        {
            opts = opts with { ForceReset = i == 0 };
            glass = renderer.Render(scene, opts);
        }

        // 合成：玻璃 over 背景（straight alpha）
        var outF = new BgraFrame(w, h);
        var o = outF.Pixels;
        var g = glass.Pixels;
        var b = backdrop.Pixels;

        for (var i = 0; i < w * h; i++)
        {
            var a = g[i * 4 + 3] / 255f;
            for (var c = 0; c < 3; c++)
            {
                o[i * 4 + c] = (byte)Math.Clamp(g[i * 4 + c] * a + b[i * 4 + c] * (1 - a), 0, 255);
            }
            o[i * 4 + 3] = 255;
        }
        return outF;
    }

    private static double StdDev(BgraFrame f, PixelRect rect)
    {
        var x0 = (int)rect.X + 20;
        var y0 = (int)rect.Y + 20;
        var x1 = (int)rect.Right - 20;
        var y1 = (int)rect.Bottom - 20;

        double sum = 0, sum2 = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var v = f.Pixels[(y * f.Width + x) * 4 + 1];
                sum += v; sum2 += (double)v * v; n++;
            }
        }
        var mean = sum / n;
        return Math.Sqrt(sum2 / n - mean * mean);
    }

    /// <summary>造一个"文档窗口"背景：标题栏 + 密集文字行 + 彩色块。</summary>
    private static BgraFrame MakeTextBackdrop(int w, int h)
    {
        var f = new BgraFrame(w, h);
        var px = f.Pixels;

        // 底色：浅灰（模拟浅色主题的窗口）
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = 245; px[i * 4 + 1] = 245; px[i * 4 + 2] = 247; px[i * 4 + 3] = 255;
        }

        void Rect(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            {
                for (var x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    var i = (y * w + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r;
                }
            }
        }

        // 标题栏（深色条）
        Rect(0, 0, w, 44, 32, 36, 48);
        // 标题栏上的"按钮"
        Rect(w - 130, 14, w - 116, 30, 120, 126, 140);
        Rect(w - 90, 14, w - 76, 30, 120, 126, 140);
        Rect(w - 50, 14, w - 36, 30, 200, 90, 90);

        // 侧边栏
        Rect(0, 44, 170, h, 236, 238, 242);
        // 侧边栏条目
        for (var k = 0; k < 9; k++)
        {
            var y = 70 + k * 42;
            Rect(24, y, 24 + 20, y + 20, 150, 155, 165);
            Rect(56, y + 6, 56 + 84, y + 16, 170, 174, 184);
        }

        // 正文：密集文字行（这是"重影"的来源 —— 高频结构）
        var rnd = new Random(20260919);
        var yTop = 78;
        for (var line = 0; line < 34; line++)
        {
            var y = yTop + line * 15;
            if (y + 10 > h) break;

            // 每行是一串"词"，词与词之间留空
            var x = 200;
            while (x < w - 60)
            {
                var wordW = 18 + rnd.Next(70);
                if (x + wordW > w - 60) break;

                // 灰度在 30..90 之间随机，模拟文字深浅
                var v = (byte)(30 + rnd.Next(60));
                Rect(x, y, x + wordW, y + 8, v, v, v);

                x += wordW + 8 + rnd.Next(10);
            }

            // 段落之间偶尔空一行
            if (line % 7 == 6) yTop += 8;
        }

        // 几个彩色块（模拟图片/图表）
        Rect(220, 150, 380, 250, 90, 140, 220);
        Rect(400, 150, 560, 250, 220, 150, 90);
        Rect(600, 150, 760, 250, 110, 190, 140);

        return f;
    }
}
