using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiquidGlass.App;

/// <summary>
/// 运行时绘制托盘图标。
///
/// 为什么不塞一个 .ico 进仓库：这个图标就是一块"液态玻璃"本身
/// （圆角矩形 + 左上高光 + 边缘折射感），用代码画出来只有几十行，
/// 而且改配色时不需要重新导出二进制资源。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>生成指定尺寸的图标。调用方负责 Dispose。</summary>
    public static Icon Create(int size = 32, bool active = true)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var pad = size * 0.08f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            var radius = rect.Height * 0.32f;

            // 玻璃体：半透明冷色，未激活时降低饱和度与亮度
            var top = active ? Color.FromArgb(190, 148, 196, 255) : Color.FromArgb(150, 130, 136, 150);
            var bottom = active ? Color.FromArgb(170, 88, 126, 200) : Color.FromArgb(130, 84, 90, 104);

            using (var path = RoundedRect(rect, radius))
            using (var brush = new LinearGradientBrush(rect, top, bottom, 62f))
            {
                g.FillPath(brush, path);
            }

            // 左上对角高光 —— 对应上游 shader 里的 diagonalA / diagonalB
            using (var path = RoundedRect(rect, radius))
            using (var pen = new Pen(Color.FromArgb(active ? 235 : 120, 255, 255, 255), Math.Max(1f, size / 24f)))
            {
                g.DrawPath(pen, path);
            }

            using (var highlight = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * 0.55f),
                Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            using (var path = RoundedRect(rect, radius))
            {
                g.FillPath(highlight, path);
            }

            if (!active)
            {
                // 暂停态：画一道斜杠
                using var pen = new Pen(Color.FromArgb(235, 255, 96, 96), Math.Max(1.6f, size / 14f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(pen, rect.Left + rect.Width * 0.22f, rect.Bottom - rect.Height * 0.22f,
                    rect.Right - rect.Width * 0.22f, rect.Top + rect.Height * 0.22f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle 不接管所有权，必须克隆一份再销毁原句柄。
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
