namespace LiquidGlass.Core;

/// <summary>
/// 合成测试场景。
///
/// 为什么要专门造场景而不是随便找张壁纸：
/// 液态玻璃的每一个光学特征都需要<b>特定频率的输入</b>才能被看见——
/// 折射需要高对比直边，色散需要细密纹理，雾化需要高频噪声。
/// 用一张平滑渐变去验收玻璃，等于用白纸验收打印机。
/// </summary>
public static class TestPatterns
{
    /// <summary>
    /// 造一张"像桌面壁纸但更好用"的场景：
    /// 柔和渐变打底，叠上高对比几何块、细密条纹与文字状的微结构。
    /// </summary>
    public static BgraFrame DesktopLike(int width, int height)
    {
        var frame = new BgraFrame(width, height);
        var p = frame.Pixels;

        // ---- 1. 斜向多色渐变底 ----
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var u = x / (double)(width - 1);
                var v = y / (double)(height - 1);
                var t = Math.Clamp(u * 0.62 + v * 0.38, 0, 1);

                // 深蓝紫 → 洋红 → 暖橙，模拟一张常见的高饱和壁纸
                var (r, g, b) = t < 0.5
                    ? Lerp3((28, 22, 64), (168, 46, 132), t * 2)
                    : Lerp3((168, 46, 132), (246, 158, 46), (t - 0.5) * 2);

                var i = (y * width + x) * 4;
                p[i] = (byte)b; p[i + 1] = (byte)g; p[i + 2] = (byte)r; p[i + 3] = 255;
            }
        }

        // ---- 2. 大块几何形状：给折射提供清晰的轮廓参照 ----
        FillRect(frame, 0, 0, width, (int)(height * 0.20), (16, 18, 30));
        FillCircle(frame, (int)(width * 0.18), (int)(height * 0.40), 96, (252, 252, 255));
        FillCircle(frame, (int)(width * 0.18), (int)(height * 0.40), 62, (232, 64, 96));
        FillCircle(frame, (int)(width * 0.18), (int)(height * 0.40), 30, (255, 226, 118));

        // ---- 3. 竖向棋盘：位移量一眼可测 ----
        var checkerX = (int)(width * 0.36);
        var checkerW = (int)(width * 0.22);
        var cell = 14;
        for (var y = (int)(height * 0.24); y < (int)(height * 0.72); y += cell)
        {
            for (var x = checkerX; x < checkerX + checkerW; x += cell)
            {
                var on = ((x / cell) + (y / cell)) % 2 == 0;
                FillRect(frame, x, y,
                    Math.Min(cell, checkerX + checkerW - x),
                    Math.Min(cell, (int)(height * 0.72) - y),
                    on ? (250, 250, 250) : (18, 20, 34));
            }
        }

        // ---- 4. 细密水平条纹：专门用来暴露色散（RGB 分离）----
        var stripeX = (int)(width * 0.62);
        var stripeW = (int)(width * 0.34);
        for (var y = (int)(height * 0.22); y < (int)(height * 0.74); y += 3)
        {
            var on = ((y / 3) % 2) == 0;
            FillRect(frame, stripeX, y, stripeW, 2, on ? (255, 255, 255) : (10, 10, 14));
        }

        // ---- 5. 文字状微结构：验证雾化对高频细节的抑制 ----
        var textRowY = (int)(height * 0.80);
        var rand = new Random(20260915);
        for (var line = 0; line < 4; line++)
        {
            var y = textRowY + line * 22;
            var x = (int)(width * 0.08);
            while (x < width * 0.92)
            {
                var wordLength = rand.Next(24, 90);
                for (var k = 0; k < wordLength && x < width * 0.92; k += 9)
                {
                    var h = rand.Next(4, 11);
                    FillRect(frame, x, y, 6, h, (238, 240, 248));
                    x += 9;
                }
                x += 14;
            }
        }

        return frame;
    }

    /// <summary>纯色场景，用于几何/覆盖率自检。</summary>
    public static BgraFrame Solid(int width, int height, byte r, byte g, byte b)
    {
        var frame = new BgraFrame(width, height);
        frame.Fill(b, g, r);
        return frame;
    }

    // ------------------------------------------------------------------ 绘制

    private static (int R, int G, int B) Lerp3((int R, int G, int B) a, (int R, int G, int B) b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private static void FillRect(BgraFrame frame, int x, int y, int w, int h, (int R, int G, int B) color)
    {
        var p = frame.Pixels;
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(frame.Width, x + w);
        var y1 = Math.Min(frame.Height, y + h);
        for (var yy = y0; yy < y1; yy++)
        {
            var row = yy * frame.Stride;
            for (var xx = x0; xx < x1; xx++)
            {
                var i = row + xx * 4;
                p[i] = (byte)color.B; p[i + 1] = (byte)color.G; p[i + 2] = (byte)color.R; p[i + 3] = 255;
            }
        }
    }

    private static void FillCircle(BgraFrame frame, int cx, int cy, int radius, (int R, int G, int B) color)
    {
        var p = frame.Pixels;
        var r2 = radius * radius;
        var y0 = Math.Max(0, cy - radius);
        var y1 = Math.Min(frame.Height, cy + radius);
        var x0 = Math.Max(0, cx - radius);
        var x1 = Math.Min(frame.Width, cx + radius);
        for (var y = y0; y < y1; y++)
        {
            var dy = y - cy;
            var row = y * frame.Stride;
            for (var x = x0; x < x1; x++)
            {
                var dx = x - cx;
                if (dx * dx + dy * dy > r2) continue;
                var i = row + x * 4;
                p[i] = (byte)color.B; p[i + 1] = (byte)color.G; p[i + 2] = (byte)color.R; p[i + 3] = 255;
            }
        }
    }
}
