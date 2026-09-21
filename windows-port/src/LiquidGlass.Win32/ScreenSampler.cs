using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 屏幕采样器。
/// 在浏览器里，液态玻璃把 DOM 重绘进一张离屏 Canvas 当作折射源；
/// 在 Windows 上等价物就是桌面像素本身。这里用 GDI BitBlt 抓屏，
/// 零依赖、任何会话都能用；若需要 60fps 级别的持续捕获，
/// 可换成 Desktop Duplication (DXGI) —— 见 docs/ARCHITECTURE.md 的"采样后端"一节。
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _capacityWidth;
    private int _capacityHeight;

    /// <summary>
    /// 抓屏时是否把<b>分层窗口</b>也包含进来（<c>CAPTUREBLT</c>）。
    ///
    /// <para><b>默认 false，因为玻璃的折射源不该包含分层窗口</b> —— 我们自己的覆盖层
    /// 就是分层窗口，把它自己拍进去会形成"折射自己"的条纹递归。</para>
    ///
    /// <para><b>但验收截图必须置 true</b>：不加 <c>CAPTUREBLT</c> 时
    /// <c>BitBlt</c> 会<b>整块跳过分层窗口</b>，于是截出来的图里根本没有我们的菜单，
    /// 只有它背后的窗口 —— 拿这种图判断"菜单是不是被遮挡"会得出完全相反的结论
    /// （本项目就因此误判过一次：明明 Z 序在最前，却以为被前台窗口压住了）。</para>
    /// </summary>
    public bool IncludeLayeredWindows { get; set; }

    /// <summary>BitBlt 的"包含分层窗口"标志。</summary>
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>
    /// 抓取屏幕区域。坐标使用虚拟桌面坐标系（左上角可为负，多显示器安全）。
    /// 区域会被裁剪到虚拟桌面范围内，返回的位图可能比请求的小。
    /// </summary>
    public BgraFrame Capture(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        EnsureBuffers(width, height);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            var fallback = new BgraFrame(width, height);
            fallback.Fill(0x11, 0x11, 0x17);
            return fallback;
        }

        try
        {
            var flags = IncludeLayeredWindows ? SRCCOPY | CAPTUREBLT : SRCCOPY;
            var ok = BitBlt(_memoryDc, 0, 0, width, height, screenDc, x, y, flags);
            if (!ok)
            {
                // BitBlt 在跨会话/受保护内容（DRM 全屏视频等）上会失败，
                // 此时退回底色，保证上层渲染管线不崩。
                var fallback = new BgraFrame(width, height);
                fallback.Fill(0x11, 0x11, 0x17);
                return fallback;
            }

            var result = new BgraFrame(width, height);
            Marshal.Copy(_bits, result.Pixels, 0, result.Pixels.Length);
            return result;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 抓取并把整个区域缩放到目标尺寸（用于低成本采样：玻璃只需要低频背景）。
    /// </summary>
    public BgraFrame CaptureScaled(int x, int y, int width, int height, int outWidth, int outHeight)
    {
        var full = Capture(x, y, width, height);
        if (full.Width == outWidth && full.Height == outHeight) return full;

        var scaled = new BgraFrame(outWidth, outHeight);
        for (var ty = 0; ty < outHeight; ty++)
        {
            var sy = ty * full.Height / outHeight;
            var srcRow = sy * full.Stride;
            var dstRow = ty * scaled.Stride;
            for (var tx = 0; tx < outWidth; tx++)
            {
                var sx = tx * full.Width / outWidth;
                var si = srcRow + sx * 4;
                var di = dstRow + tx * 4;
                scaled.Pixels[di] = full.Pixels[si];
                scaled.Pixels[di + 1] = full.Pixels[si + 1];
                scaled.Pixels[di + 2] = full.Pixels[si + 2];
                scaled.Pixels[di + 3] = 255;
            }
        }
        return scaled;
    }

    /// <summary>
    /// 抓取屏幕区域，并把落在虚拟桌面之外的部分用<b>边界像素延续</b>补齐。
    ///
    /// 为什么必须有这个方法：任务栏贴着屏幕底边，而玻璃的折射会向下采样到矩形之外。
    /// 直接 BitBlt 一块越界区域会得到黑边，于是玻璃下缘会浮出一条突兀的暗线。
    /// 用最靠近的有效像素向外延续，视觉上等价于"壁纸继续延伸下去"。
    /// </summary>
    public BgraFrame CapturePadded(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var desktop = DisplayEnvironment.GetVirtualDesktopBounds();
        var ix0 = Math.Max(x, desktop.Left);
        var iy0 = Math.Max(y, desktop.Top);
        var ix1 = Math.Min(x + width, desktop.Right);
        var iy1 = Math.Min(y + height, desktop.Bottom);

        var result = new BgraFrame(width, height);

        // 整个请求区域都在屏幕外：给一个中性的深底色，避免整块黑。
        if (ix1 <= ix0 || iy1 <= iy0)
        {
            result.Fill(0x17, 0x11, 0x11);
            return result;
        }

        var inner = Capture(ix0, iy0, ix1 - ix0, iy1 - iy0);
        var offsetX = ix0 - x;
        var offsetY = iy0 - y;

        // ① 先把有效区域放进去
        for (var row = 0; row < inner.Height; row++)
        {
            Array.Copy(
                inner.Pixels, row * inner.Stride,
                result.Pixels, (offsetY + row) * result.Stride + offsetX * 4,
                inner.Width * 4);
        }

        // ② 每行左右向外延展（用最近的有效像素）
        var stride = result.Stride;
        for (var row = 0; row < inner.Height; row++)
        {
            var rowStart = (offsetY + row) * stride;
            var leftSrc = rowStart + offsetX * 4;
            for (var cx = 0; cx < offsetX; cx++)
            {
                var d = rowStart + cx * 4;
                result.Pixels[d] = result.Pixels[leftSrc];
                result.Pixels[d + 1] = result.Pixels[leftSrc + 1];
                result.Pixels[d + 2] = result.Pixels[leftSrc + 2];
                result.Pixels[d + 3] = 255;
            }

            var rightSrc = rowStart + (offsetX + inner.Width - 1) * 4;
            for (var cx = offsetX + inner.Width; cx < width; cx++)
            {
                var d = rowStart + cx * 4;
                result.Pixels[d] = result.Pixels[rightSrc];
                result.Pixels[d + 1] = result.Pixels[rightSrc + 1];
                result.Pixels[d + 2] = result.Pixels[rightSrc + 2];
                result.Pixels[d + 3] = 255;
            }
        }

        // ③ 上下按整行延续
        var firstRow = offsetY * stride;
        for (var row = 0; row < offsetY; row++)
        {
            Array.Copy(result.Pixels, firstRow, result.Pixels, row * stride, stride);
        }

        var lastRow = (offsetY + inner.Height - 1) * stride;
        for (var row = offsetY + inner.Height; row < height; row++)
        {
            Array.Copy(result.Pixels, lastRow, result.Pixels, row * stride, stride);
        }

        // 保证 Alpha 恒为不透明：这是折射源，不是要合成的图层。
        for (var i = 3; i < result.Pixels.Length; i += 4) result.Pixels[i] = 255;

        return result;
    }

    /// <summary>抓屏时"内层有效区域"的复用缓冲（避免每次抓屏多分配一块大帧）。</summary>
    private BgraFrame? _innerFrame;

    /// <summary>
    /// 抓屏并<b>写入已有的帧</b>（尺寸由 <paramref name="dest"/> 决定）。
    ///
    /// <para><b>为什么需要</b>：折射源的抓屏帧通常有几 MB（开始菜单场景 1554×1462 ≈ 9MB），
    /// 超过 85KB 就进 <b>LOH（大对象堆）</b>且不被压缩。每次抓屏都新分配会造成
    /// 碎片化、私有内存虚高与间歇性 GC 卡顿。</para>
    ///
    /// <para>失败时把目标填成中性深色并返回 false（与原 <see cref="Capture"/> 的降级一致）。</para>
    /// </summary>
    public bool CaptureInto(BgraFrame dest, int x, int y)
    {
        var width = dest.Width;
        var height = dest.Height;
        if (width <= 0 || height <= 0) return false;

        EnsureBuffers(width, height);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            dest.Fill(0x17, 0x11, 0x11);
            return false;
        }

        try
        {
            var flags = IncludeLayeredWindows ? SRCCOPY | CAPTUREBLT : SRCCOPY;
            var ok = BitBlt(_memoryDc, 0, 0, width, height, screenDc, x, y, flags);
            if (!ok)
            {
                dest.Fill(0x17, 0x11, 0x11);
                return false;
            }

            Marshal.Copy(_bits, dest.Pixels, 0, dest.Pixels.Length);
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// <see cref="CapturePadded"/> 的复用版本：结果写入 <paramref name="dest"/>，
    /// 内层有效区域复用 <see cref="_innerFrame"/>。
    ///
    /// <para>行为与 <see cref="CapturePadded"/> 逐像素一致（含越界时的边缘延展）。</para>
    /// </summary>
    public void CapturePaddedInto(BgraFrame dest, int x, int y)
    {
        var width = dest.Width;
        var height = dest.Height;
        if (width <= 0 || height <= 0) return;

        var desktop = DisplayEnvironment.GetVirtualDesktopBounds();
        var ix0 = Math.Max(x, desktop.Left);
        var iy0 = Math.Max(y, desktop.Top);
        var ix1 = Math.Min(x + width, desktop.Right);
        var iy1 = Math.Min(y + height, desktop.Bottom);

        // 整个请求区域都在屏幕外：给一个中性的深底色，避免整块黑。
        if (ix1 <= ix0 || iy1 <= iy0)
        {
            dest.Fill(0x17, 0x11, 0x11);
            return;
        }

        var iw = ix1 - ix0;
        var ih = iy1 - iy0;
        if (_innerFrame is null || _innerFrame.Width != iw || _innerFrame.Height != ih)
        {
            _innerFrame = new BgraFrame(iw, ih);
        }

        var inner = _innerFrame;
        CaptureInto(inner, ix0, iy0);

        var offsetX = ix0 - x;
        var offsetY = iy0 - y;

        // ① 先把有效区域放进去
        for (var row = 0; row < inner.Height; row++)
        {
            Array.Copy(
                inner.Pixels, row * inner.Stride,
                dest.Pixels, (offsetY + row) * dest.Stride + offsetX * 4,
                inner.Width * 4);
        }

        // ② 每行左右向外延展（用最近的有效像素）
        var stride = dest.Stride;
        for (var row = 0; row < inner.Height; row++)
        {
            var rowStart = (offsetY + row) * stride;
            var leftSrc = rowStart + offsetX * 4;
            for (var cx = 0; cx < offsetX; cx++)
            {
                var d = rowStart + cx * 4;
                dest.Pixels[d] = dest.Pixels[leftSrc];
                dest.Pixels[d + 1] = dest.Pixels[leftSrc + 1];
                dest.Pixels[d + 2] = dest.Pixels[leftSrc + 2];
                dest.Pixels[d + 3] = 255;
            }

            var rightSrc = rowStart + (offsetX + inner.Width - 1) * 4;
            for (var cx = offsetX + inner.Width; cx < width; cx++)
            {
                var d = rowStart + cx * 4;
                dest.Pixels[d] = dest.Pixels[rightSrc];
                dest.Pixels[d + 1] = dest.Pixels[rightSrc + 1];
                dest.Pixels[d + 2] = dest.Pixels[rightSrc + 2];
                dest.Pixels[d + 3] = 255;
            }
        }

        // ③ 上下按整行延续
        var firstRow = offsetY * stride;
        for (var row = 0; row < offsetY; row++)
        {
            Array.Copy(dest.Pixels, firstRow, dest.Pixels, row * stride, stride);
        }

        var lastRow = (offsetY + inner.Height - 1) * stride;
        for (var row = offsetY + inner.Height; row < height; row++)
        {
            Array.Copy(dest.Pixels, lastRow, dest.Pixels, row * stride, stride);
        }

        // 保证 Alpha 恒为不透明：这是折射源，不是要合成的图层。
        for (var i = 3; i < dest.Pixels.Length; i += 4) dest.Pixels[i] = 255;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && width <= _capacityWidth && height <= _capacityHeight) return;

        ReleaseBuffers();

        _capacityWidth = Math.Max(width, _capacityWidth);
        _capacityHeight = Math.Max(height, _capacityHeight);

        var screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _capacityWidth,
                biHeight = -_capacityHeight,   // 负数 = 自上而下，避免翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new uint[256],
        };

        _bitmap = CreateDIBSection(_memoryDc, ref info, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memoryDc, _bitmap);
    }

    private void ReleaseBuffers()
    {
        if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero)
        {
            DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
        if (_memoryDc != IntPtr.Zero)
        {
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }
        _bits = IntPtr.Zero;
    }

    public void Dispose() => ReleaseBuffers();
}
