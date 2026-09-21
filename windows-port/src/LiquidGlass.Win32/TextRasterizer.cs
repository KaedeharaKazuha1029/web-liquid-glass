using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 用 GDI 把文字栅格化成带 Alpha 的位图。
///
/// <para>为什么用 GDI 而不是引入字体库：整个仓库的承诺是零第三方依赖，
/// 而 GDI 从 Windows 2000 就在，跨 Win7/10/11 行为完全一致，
/// 还能白拿系统自带的字体回退与中文渲染（Segoe UI + 微软雅黑回退）。
/// 代价是每个字号要建一次字体对象，所以这里做了缓存。</para>
///
/// <para><b>Alpha 从哪里来</b>：GDI 不写 Alpha 通道。做法是在全 0 的底上
/// 用纯白画字，于是每个像素的亮度就等于字形覆盖率，
/// 直接拿来当 Alpha 即可。这样后续可以染成任意颜色（悬停变亮、非悬停偏灰），
/// 而且抗锯齿边缘保持正确。</para>
/// </summary>
public static class TextRasterizer
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Family, int Size, bool Bold), IntPtr> FontCache = [];

    /// <summary>栅格化结果：白色文字 + 覆盖率 Alpha。</summary>
    public sealed record RasterizedText(BgraFrame Bitmap, int Width, int Height);

    /// <summary>
    /// 栅格化一行文字。
    /// </summary>
    /// <param name="text">要画的文字。</param>
    /// <param name="fontSizePx">字号（像素高度，已含 DPI 缩放）。</param>
    /// <param name="maxWidth">最大宽度；超出部分尾部省略号。</param>
    /// <param name="bold">是否加粗。</param>
    /// <param name="family">字体族，默认 Segoe UI（Win7 起自带，中文自动回退到微软雅黑）。</param>
    public static RasterizedText? Render(
        string text, int fontSizePx, int maxWidth, bool bold = false, string family = "Segoe UI")
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || fontSizePx <= 0) return null;

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        var font = IntPtr.Zero;
        var oldFont = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(IntPtr.Zero);
            if (memoryDc == IntPtr.Zero) return null;

            font = GetOrCreateFont(family, fontSizePx, bold);
            if (font == IntPtr.Zero) return null;

            // 高度多留 4px 给下伸部（g/j/p/q/y）与抗锯齿边缘。
            var canvasHeight = fontSizePx + 6;
            var canvasWidth = maxWidth;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = canvasWidth,
                    biHeight = -canvasHeight,   // 负值 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
                bmiColors = new uint[256],
            };

            bitmap = CreateDIBSection(memoryDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memoryDc, bitmap);
            oldFont = SelectObject(memoryDc, font);

            // GDI 文字渲染：透明底 + 纯白字。
            // ⚠️ 必须先 SetBkMode(TRANSPARENT)，否则 GDI 会用背景色填满整个矩形，
            //    我们就拿不到"只有字形"的覆盖率了。
            SetBkMode(memoryDc, TRANSPARENT);
            SetTextColor(memoryDc, 0x00FFFFFF);   // COLORREF 是 0x00BBGGRR，白色无所谓顺序

            var rect = new RECT { Left = 0, Top = 0, Right = maxWidth, Bottom = canvasHeight };
            var flags = DT_LEFT | DT_TOP | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX;

            var drawOk = DrawTextW(memoryDc, text, text.Length, ref rect, flags);
            if (drawOk == 0) return null;

            // 读回像素。白字的亮度即覆盖率 → 直接当作 Alpha。
            var pixelCount = canvasWidth * canvasHeight;
            var raw = new byte[pixelCount * 4];
            Marshal.Copy(bits, raw, 0, raw.Length);

            var frame = new BgraFrame(canvasWidth, canvasHeight);
            var dst = frame.Pixels;
            var maxUsedX = 0;

            for (var i = 0; i < pixelCount; i++)
            {
                var s = i * 4;
                var coverage = raw[s];   // B/G/R 相同，取任意一路
                var d = i * 4;
                dst[d] = 255;
                dst[d + 1] = 255;
                dst[d + 2] = 255;
                dst[d + 3] = coverage;

                if (coverage > 8)
                {
                    var x = i % canvasWidth;
                    if (x > maxUsedX) maxUsedX = x;
                }
            }

            // 裁到实际用到的宽度，让布局计算不必按最大宽度算。
            var usedWidth = Math.Clamp(maxUsedX + 2, 1, canvasWidth);
            var cropped = usedWidth == canvasWidth
                ? frame
                : Crop(frame, usedWidth);

            return new RasterizedText(cropped, cropped.Width, cropped.Height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero)
            {
                if (oldFont != IntPtr.Zero) SelectObject(memoryDc, oldFont);
                if (oldBitmap != IntPtr.Zero) SelectObject(memoryDc, oldBitmap);
            }
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            // 字体进缓存，不在这里删。
        }
    }

    /// <summary>测量文字宽度（不产生位图）。用于布局预算。</summary>
    public static int MeasureWidth(string text, int fontSizePx, bool bold = false, string family = "Segoe UI")
    {
        var raster = Render(text, fontSizePx, Math.Max(fontSizePx * text.Length + 8, 64), bold, family);
        return raster?.Width ?? 0;
    }

    /// <summary>释放字体缓存。进程退出前调用。</summary>
    public static void ClearFontCache()
    {
        lock (Sync)
        {
            foreach (var font in FontCache.Values)
            {
                if (font != IntPtr.Zero) DeleteObject(font);
            }
            FontCache.Clear();
        }
    }

    private static IntPtr GetOrCreateFont(string family, int sizePx, bool bold)
    {
        var key = (family, sizePx, bold);
        lock (Sync)
        {
            if (FontCache.TryGetValue(key, out var cached) && cached != IntPtr.Zero) return cached;

            // 字体高度用负值 = 指定"字符高度"而不是"单元格高度"。
            // 用正值会让实际字形比请求的小一圈，小字号下差别很明显。
            var font = CreateFontW(
                -sizePx, 0, 0, 0,
                bold ? FW_SEMIBOLD : FW_NORMAL,
                0, 0, 0,
                DEFAULT_CHARSET,               // 让中文能正确回退
                OUT_TT_PRECIS,                 // 优先 TrueType，保证小字号可读
                CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY,             // 亚像素抗锯齿
                DEFAULT_PITCH | FF_DONTCARE,
                family);

            if (font != IntPtr.Zero) FontCache[key] = font;
            return font;
        }
    }

    private static BgraFrame Crop(BgraFrame source, int width)
    {
        var result = new BgraFrame(width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            Array.Copy(source.Pixels, y * source.Stride, result.Pixels, y * result.Stride, width * 4);
        }
        return result;
    }

    // ------------------------------------------------------------ GDI 声明

    private const int TRANSPARENT = 1;
    private const int FW_NORMAL = 400;
    private const int FW_SEMIBOLD = 600;
    private const int DEFAULT_CHARSET = 1;
    private const int OUT_TT_PRECIS = 4;
    private const int CLIP_DEFAULT_PRECIS = 0;
    private const int CLEARTYPE_QUALITY = 5;
    private const int DEFAULT_PITCH = 0;
    private const int FF_DONTCARE = 0;

    private const uint DT_LEFT = 0x00000000;
    private const uint DT_TOP = 0x00000000;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_END_ELLIPSIS = 0x00008000;
    private const uint DT_NOPREFIX = 0x00000800;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality,
        uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);
}
