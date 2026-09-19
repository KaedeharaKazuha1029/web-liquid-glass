using System.Diagnostics;
using LiquidGlass.Core;

namespace LiquidGlass.Preview;

/// <summary>
/// 离线预览：把液态玻璃渲染成 PNG，用于在没有 GPU 后端的情况下做视觉验收。
///
/// 用法：
/// <code>
/// liquidglass-preview [输出目录]
/// </code>
/// 产物：
/// <code>
/// scene.png            合成测试场景（对照基线）
/// bar-*.png            任务栏形态（2560x72 的极端长宽比）
/// panel-*.png          开始菜单形态（竖长矩形）
/// edge-zoom.png        左边缘 4 倍放大，检查透镜卷回与色散
/// converge-*.png       时间累积收敛过程
/// </code>
/// </summary>
internal static class Program
{
    private const int SceneWidth = 1400;
    private const int SceneHeight = 900;

    private static int Main(string[] args)
    {
        var outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "preview-output");
        Directory.CreateDirectory(outDir);

        Console.WriteLine("液态玻璃 · 离线预览渲染");
        Console.WriteLine($"输出目录：{outDir}");
        Console.WriteLine();

        var sceneFrame = TestPatterns.DesktopLike(SceneWidth, SceneHeight);
        PngWriter.Write(Path.Combine(outDir, "scene.png"), sceneFrame);
        Console.WriteLine($"  场景已生成 {SceneWidth}x{SceneHeight}");

        // 场景转线性空间，作为玻璃的折射源。
        var scene = GlassScene.FromBgra(sceneFrame, originX: 0, originY: 0);

        // ---------------------------------------------------------- 任务栏形态
        // 真实任务栏在 150% 缩放下是 2560x72 —— 高度只有宽度的 1/36。
        // 上游所有按比例定义的光学距离都以"高度"为基准，这里正是压力测试。
        var barRect = new PixelRect(60, 760, 1280, 54);
        RenderShape(outDir, scene, sceneFrame, "bar", barRect, paths: 4);

        // -------------------------------------------------------- 开始菜单形态
        var panelRect = new PixelRect(460, 130, 480, 560);
        RenderShape(outDir, scene, sceneFrame, "panel", panelRect, paths: 4);

        // -------------------------------------------------------- 收敛过程
        RenderConvergence(outDir, scene, sceneFrame, barRect);

        // ---------------------------------------------------- 左边缘放大检查
        RenderEdgeZoom(outDir, scene, sceneFrame, barRect);

        // ------------------------------------------------------------ 性能测量
        Benchmark(scene, barRect, panelRect);

        Console.WriteLine();
        Console.WriteLine("完成。");
        return 0;
    }

    private static void RenderShape(
        string outDir, GlassScene scene, BgraFrame sceneFrame,
        string name, PixelRect rect, int paths)
    {
        var renderer = new CpuGlassRenderer();
        var options = new GlassRenderOptions
        {
            GlassRect = rect,
            CanvasWidth = SceneWidth,
            CanvasHeight = SceneHeight,
            PathsPerPixel = paths,
            MaxAccumulation = 48,
            MouseX = 0.5f,
            MouseY = 0.5f,
        };

        BgraFrame glass = null!;
        for (var i = 0; i < 48; i++) glass = renderer.Render(scene, options);

        var composed = Composite(sceneFrame, glass);
        PngWriter.Write(Path.Combine(outDir, $"{name}-glass.png"), glass, forceAlpha: true);
        PngWriter.Write(Path.Combine(outDir, $"{name}-composed.png"), composed);

        // 单独裁出玻璃区域，方便近距离观察
        var crop = Crop(composed, (int)rect.X - 40, (int)rect.Y - 40,
            (int)rect.Width + 80, (int)rect.Height + 80);
        PngWriter.Write(Path.Combine(outDir, $"{name}-crop.png"), crop);

        Console.WriteLine($"  {name}: {renderer.LastStats.Frame} 帧收敛，"
            + $"单帧 {renderer.LastStats.ElapsedMs:F0}ms，"
            + $"追踪像素 {renderer.LastStats.PixelsTraced:N0}");
    }

    private static void RenderConvergence(
        string outDir, GlassScene scene, BgraFrame sceneFrame, PixelRect rect)
    {
        var renderer = new CpuGlassRenderer();
        var options = new GlassRenderOptions
        {
            GlassRect = rect,
            CanvasWidth = SceneWidth,
            CanvasHeight = SceneHeight,
            PathsPerPixel = 4,
            MaxAccumulation = 48,
        };

        var targets = new[] { 1, 2, 4, 8, 16, 48 };
        var targetSet = new HashSet<int>(targets);
        var frame = 1;

        while (true)
        {
            var glass = renderer.Render(scene, options);
            if (targetSet.Contains(frame))
            {
                var crop = Crop(Composite(sceneFrame, glass),
                    (int)rect.X - 30, (int)rect.Y - 26, 520, (int)rect.Height + 52);
                PngWriter.Write(Path.Combine(outDir, $"converge-f{frame:D3}.png"), crop);
                Console.WriteLine($"  收敛快照 f{frame:D3}  (累积权重 {frame - 1}/{frame})");
            }
            frame++;
            if (frame > 48) break;
        }
    }

    private static void RenderEdgeZoom(
        string outDir, GlassScene scene, BgraFrame sceneFrame, PixelRect rect)
    {
        var renderer = new CpuGlassRenderer();
        var options = new GlassRenderOptions
        {
            GlassRect = rect,
            CanvasWidth = SceneWidth,
            CanvasHeight = SceneHeight,
            PathsPerPixel = 4,
            MaxAccumulation = 48,
        };
        BgraFrame glass = null!;
        for (var i = 0; i < 48; i++) glass = renderer.Render(scene, options);

        var composed = Composite(sceneFrame, glass);
        var crop = Crop(composed, (int)rect.X - 24, (int)rect.Y - 24, 300, (int)rect.Height + 48);
        PngWriter.Write(Path.Combine(outDir, "edge-zoom.png"), Magnify(crop, 4));

        // 右边缘
        var rightCrop = Crop(composed, (int)rect.Right - 300 + 24, (int)rect.Y - 24, 300, (int)rect.Height + 48);
        PngWriter.Write(Path.Combine(outDir, "edge-zoom-right.png"), Magnify(rightCrop, 4));
    }

    private static void Benchmark(GlassScene scene, PixelRect barRect, PixelRect panelRect)
    {
        Console.WriteLine();
        Console.WriteLine("性能测量（4 paths/pixel，单帧冷启动）：");

        foreach (var (label, rect) in new[] { ("任务栏 1280x54", barRect), ("开始菜单 480x560", panelRect) })
        {
            var renderer = new CpuGlassRenderer();
            var options = new GlassRenderOptions
            {
                GlassRect = rect,
                CanvasWidth = SceneWidth,
                CanvasHeight = SceneHeight,
                PathsPerPixel = 4,
            };

            // 预热
            renderer.Render(scene, options);
            renderer.Reset();

            var sw = Stopwatch.StartNew();
            const int frames = 8;
            for (var i = 0; i < frames; i++) renderer.Render(scene, options);
            sw.Stop();

            var perFrame = sw.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"  {label,-20} {perFrame,7:F1} ms/帧  ≈ {1000 / perFrame,5:F0} fps  "
                + $"（{rect.Width * rect.Height:N0} 像素）");
        }
    }

    // ---------------------------------------------------------------- 图像工具

    /// <summary>把带 Alpha 的玻璃层合成到场景上。</summary>
    private static BgraFrame Composite(BgraFrame background, BgraFrame glass)
    {
        var result = background.Clone();
        var a = glass.Pixels;
        var d = result.Pixels;
        for (var i = 0; i < a.Length; i += 4)
        {
            var alpha = a[i + 3];
            if (alpha == 0) continue;
            if (alpha == 255)
            {
                d[i] = a[i]; d[i + 1] = a[i + 1]; d[i + 2] = a[i + 2];
                continue;
            }
            var ia = 255 - alpha;
            d[i] = (byte)((a[i] * alpha + d[i] * ia) / 255);
            d[i + 1] = (byte)((a[i + 1] * alpha + d[i + 1] * ia) / 255);
            d[i + 2] = (byte)((a[i + 2] * alpha + d[i + 2] * ia) / 255);
        }
        return result;
    }

    private static BgraFrame Crop(BgraFrame source, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        width = Math.Clamp(width, 1, source.Width - x);
        height = Math.Clamp(height, 1, source.Height - y);

        var result = new BgraFrame(width, height);
        for (var row = 0; row < height; row++)
        {
            Array.Copy(source.Pixels, (y + row) * source.Stride + x * 4,
                result.Pixels, row * result.Stride, width * 4);
        }
        return result;
    }

    /// <summary>最近邻放大：用来肉眼检查亚像素级的透镜卷回。</summary>
    private static BgraFrame Magnify(BgraFrame source, int factor)
    {
        var result = new BgraFrame(source.Width * factor, source.Height * factor);
        for (var y = 0; y < result.Height; y++)
        {
            var sy = y / factor;
            for (var x = 0; x < result.Width; x++)
            {
                var sx = x / factor;
                var si = sy * source.Stride + sx * 4;
                var di = y * result.Stride + x * 4;
                result.Pixels[di] = source.Pixels[si];
                result.Pixels[di + 1] = source.Pixels[si + 1];
                result.Pixels[di + 2] = source.Pixels[si + 2];
                result.Pixels[di + 3] = 255;
            }
        }
        return result;
    }
}
