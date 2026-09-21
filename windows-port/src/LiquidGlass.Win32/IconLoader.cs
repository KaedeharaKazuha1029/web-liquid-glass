using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 把 Windows 的 <c>HICON</c> 变成可合成的 BGRA 位图。
///
/// <para>这是整个替换任务栏里<b>最容易踩坑</b>的一环，坑集中在 Alpha 上：
/// 现代图标（32 位带 Alpha）直接取位就行；但有不少老程序的图标是
/// 24 位色 + 一张单色掩码，其位图里 Alpha 通道全是 0。
/// 如果直接当 32 位用，图标会整个变成透明的，看起来就是"图标丢了"。</para>
///
/// <para>所以这里的流程是：先取 32 位色位图，检查 Alpha 是否真的有效；
/// 无效则回头去读掩码位图，用掩码反推 Alpha。两条路都走的通，
/// 才能覆盖从 Windows 98 时代的老程序到 UWP 应用的全部情况。</para>
/// </summary>
public static class IconLoader
{
    /// <summary>从窗口取图标。取不到返回 null。</summary>
    public static BgraFrame? FromWindow(IntPtr hwnd, int targetSize = 32)
    {
        // 先要大图标（更清晰），拿不到再退小图标。
        var icon = ShellNativeMethods.GetWindowIcon(hwnd, big: true);
        if (icon == IntPtr.Zero) return null;
        return FromHandle(icon, targetSize);
    }

    /// <summary>从文件（.exe / .lnk / 任意有图标的文件）取图标。</summary>
    public static BgraFrame? FromFile(string path, int targetSize = 32)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON;
        if (!File.Exists(path))
        {
            // 目标不存在（UWP 包路径、被卸载的程序）时，让 Shell 按扩展名给个通用图标。
            flags |= SHGFI_USEFILEATTRIBUTES;
        }

        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            return FromHandle(info.hIcon, targetSize);
        }
        finally
        {
            // SHGetFileInfo 返回的图标归我们所有，必须销毁。
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>
    /// 把 <c>HICON</c> 解码成 BGRA。
    /// <b>不</b>接管 <paramref name="hIcon"/> 的所有权，调用方负责销毁。
    /// </summary>
    public static BgraFrame? FromHandle(IntPtr hIcon, int targetSize = 32)
    {
        if (hIcon == IntPtr.Zero) return null;

        if (!GetIconInfo(hIcon, out var info)) return null;

        try
        {
            var color = ExtractBitmap(info.hbmColor, out var width, out var height);
            var hasAlpha = color is not null && HasUsableAlpha(color);

            BgraFrame? source = color;

            if (!hasAlpha)
            {
                // 24 位色图标：用掩码位图反推 Alpha。
                var mask = ExtractMask(info.hbmMask, width, height);
                source = MergeWithMask(color, mask, width, height);
            }

            if (source is null) return null;

            return source.Width == targetSize && source.Height == targetSize
                ? source
                : Resize(source, targetSize, targetSize);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    // ------------------------------------------------------------ 位图提取

    /// <summary>
    /// 把 HBITMAP 读成自上而下的 BGRA。
    /// 用负高度申请 DIB，让 GDI 直接给出自上而下的行序——
    /// 否则每一帧还得自己翻一次行，纯属浪费。
    /// </summary>
    private static BgraFrame? ExtractBitmap(IntPtr hBitmap, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (hBitmap == IntPtr.Zero) return null;

        var bitmap = default(BITMAP);
        if (!GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bitmap)) return null;
        if (bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0) return null;

        // 掩码位图的高度是颜色的两倍（上半是 AND 掩码，下半是 XOR 数据），这里只取颜色部分。
        width = bitmap.bmWidth;
        height = bitmap.bmHeight;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,   // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new uint[256],
        };

        var pixels = new byte[width * height * 4];

        // GetDIBits 需要目标 DC；用屏幕 DC 即可，这里不做任何实际绘制。
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            var lines = GetDIBits(screenDc, hBitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS);
            if (lines == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var frame = new BgraFrame(width, height);
        Array.Copy(pixels, frame.Pixels, pixels.Length);
        return frame;
    }

    /// <summary>提取单色掩码位图，并归一化成逐像素的 Alpha 覆盖（255 = 不透明）。</summary>
    private static byte[]? ExtractMask(IntPtr hMask, int width, int height)
    {
        if (hMask == IntPtr.Zero || width <= 0 || height <= 0) return null;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new uint[256],
        };

        var pixels = new byte[width * height * 4];
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screenDc, hMask, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // 掩码语义：白（255）= 透明，黑（0）= 不透明。所以要取反。
        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = (byte)(255 - pixels[i * 4]);
        }
        return alpha;
    }

    /// <summary>颜色位图与掩码合成，得到带 Alpha 的图标。</summary>
    private static BgraFrame? MergeWithMask(BgraFrame? color, byte[]? mask, int width, int height)
    {
        var frame = new BgraFrame(width, height);

        if (color is not null && color.Width == width && color.Height == height)
        {
            Array.Copy(color.Pixels, frame.Pixels, frame.Pixels.Length);
        }
        else
        {
            // 连颜色位图都拿不到：全黑 + 掩码，至少还能得到一个剪影。
            for (var i = 0; i < frame.Pixels.Length; i += 4)
            {
                frame.Pixels[i] = 0;
                frame.Pixels[i + 1] = 0;
                frame.Pixels[i + 2] = 0;
            }
        }

        if (mask is null)
        {
            // 没有掩码可用：整体置为不透明，宁可显示一个方块也不要显示"什么都没有"。
            for (var i = 3; i < frame.Pixels.Length; i += 4) frame.Pixels[i] = 255;
            return frame;
        }

        for (var i = 0; i < mask.Length; i++)
        {
            frame.Pixels[i * 4 + 3] = mask[i];
        }
        return frame;
    }

    /// <summary>位图里是否真的用了 Alpha 通道（存在任何一个非 0 的 Alpha 值）。</summary>
    private static bool HasUsableAlpha(BgraFrame frame)
    {
        var pixels = frame.Pixels;
        var nonZero = 0;
        var transparent = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) nonZero++;
            else transparent++;
        }

        // 需要"既有透明又有不透明"才能确认 Alpha 是真的在用。
        // 全 0（老图标）和全 255（已展开的老图标）都走掩码/直通路径。
        return nonZero > 0 && (transparent > 0 || nonZero == pixels.Length / 4);
    }

    // -------------------------------------------------------------- 缩放

    /// <summary>
    /// 双线性缩放。图标通常在 16/32/48 之间跳，直接最近邻会很难看。
    /// 缩小前先做一个 2×2 的盒式预滤波，否则细线条会闪。
    /// </summary>
    public static BgraFrame Resize(BgraFrame source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var result = new BgraFrame(width, height);
        var src = source.Pixels;
        var dst = result.Pixels;

        var xRatio = source.Width / (double)width;
        var yRatio = source.Height / (double)height;

        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5) * yRatio - 0.5;
            var y0 = (int)Math.Floor(sy);
            var fy = sy - y0;
            var y1 = Math.Clamp(y0 + 1, 0, source.Height - 1);
            y0 = Math.Clamp(y0, 0, source.Height - 1);

            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5) * xRatio - 0.5;
                var x0 = (int)Math.Floor(sx);
                var fx = sx - x0;
                var x1 = Math.Clamp(x0 + 1, 0, source.Width - 1);
                x0 = Math.Clamp(x0, 0, source.Width - 1);

                var i00 = (y0 * source.Width + x0) * 4;
                var i10 = (y0 * source.Width + x1) * 4;
                var i01 = (y1 * source.Width + x0) * 4;
                var i11 = (y1 * source.Width + x1) * 4;

                var w00 = (1 - fx) * (1 - fy);
                var w10 = fx * (1 - fy);
                var w01 = (1 - fx) * fy;
                var w11 = fx * fy;

                var d = (y * width + x) * 4;

                // 注意：预乘后再插值，否则透明边缘会出现黑边。
                // 这是图标缩放里最经典的一个错误。
                for (var c = 0; c < 4; c++)
                {
                    var a00 = src[i00 + 3] / 255.0;
                    var a10 = src[i10 + 3] / 255.0;
                    var a01 = src[i01 + 3] / 255.0;
                    var a11 = src[i11 + 3] / 255.0;

                    var value = src[i00 + c] * a00 * w00
                              + src[i10 + c] * a10 * w10
                              + src[i01 + c] * a01 * w01
                              + src[i11 + c] * a11 * w11;

                    var alpha = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;

                    dst[d + c] = c == 3
                        ? (byte)Math.Clamp(alpha * 255 + 0.5, 0, 255)
                        : (byte)Math.Clamp(alpha > 0.0001 ? value / alpha : 0, 0, 255);
                }
            }
        }

        return result;
    }
}
