using System.Runtime.InteropServices;
using LiquidGlass.Core;

namespace LiquidGlass.Win32;

/// <summary>
/// 从图片文件读一张位图（jpg / png / bmp）到 <see cref="BgraFrame"/>。
///
/// <para><b>怎么读的</b>：走 Shell 的 <c>IShellItemImageFactory::GetImage</c> ——
/// 这是"给我一个 <c>.jpg</c> 的缩略图"的官方接口，资源管理器画缩略图用的就是它。</para>
///
/// <para><b>为什么不用 WIC 直读</b>：试过，而且踩了两个坑，都不值得：
/// <list type="number">
///   <item><c>CoCreateInstance(CLSID_WICImagingFactory)</c> 在本机返回
///         <c>REGDB_E_CLASSNOTREG</c>（WIC 工厂是 WinRT 风格激活，不走传统 CLSID）；</item>
///   <item>改走 <c>WICCreateImagingFactory_Proxy</c> 拿到工厂之后，还得手写
///         <c>IWICBitmapDecoder</c> 的整条继承链占位
///         （<c>IWICBitmapDecoder : IWICBitmapCodecInfo : IWICComponentInfo</c>，
///         <c>GetFrame</c> 是第 26 个方法）。COM 互操作按 vtable <b>序号</b>派发，
///         漏一个占位就会让调用打到错误的函数指针上 ——
///         实测直接 <c>0xC0000005</c> 访问违例。这种"数格子"式的代码极难维护，
///         换台机器多继承一层就崩。</item>
/// </list>
/// 相比之下 <c>IShellItemImageFactory</c> <b>IUnknown 之后只有一个方法</b>，
/// 没有数格子的风险，而且缩略图由 Shell 缓存，实际比我们自己解码更快。</para>
///
/// <para><b>代价</b>：Shell 会给缩略图做等比缩放（不是原图）。
/// 但我们的用途是画一个 40px 的账户头像，缩略图完全够用 ——
/// 而且 Shell 的缩放质量比我们自己写的双线性还好。</para>
/// </summary>
public static class ImageReader
{
    /// <summary>最近一次失败的 HRESULT（0 表示没有失败）。</summary>
    public static int LastHResult { get; private set; }

    /// <summary>最近一次失败的阶段描述（给诊断用）。</summary>
    public static string? LastStage { get; private set; }

    /// <summary>读一张正方形图。失败（文件不存在 / 格式不支持）返回 null。</summary>
    public static BgraFrame? Load(string path, int size)
    {
        LastHResult = 0;
        LastStage = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        size = Math.Clamp(size, 8, 1024);

        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            // ⚠️ 必须先初始化 COM：控制台进程默认不初始化，而 Shell 的接口是 COM。
            // 不初始化时 CoCreateInstance / SHCreateItemFromParsingName 会返回
            // CO_E_NOTINITIALIZED (0x800401F0)。
            var hrInit = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            var comOwned = hrInit is 0 or 1;

            try
            {
                // Shell 的 IShellItemImageFactory IID。
                var iid = IID_IShellItemImageFactory;

                // ⚠️ PreserveSig=false 的签名会**抛异常**而不是返回 HRESULT，
                // 所以这里不能接返回值，要 try/catch（外层已有）。
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                if (factory is null)
                {
                    Fail("SHCreateItemFromParsingName", unchecked((int)0x80004005));
                    return null;
                }

                // SIIGBF_BIGGERSIZEOK：允许 Shell 返回"不小于请求尺寸"的原图，
                // 由我们自己缩 —— 这样能避免 Shell 为了凑精确尺寸而放大一张小图。
                var flags = SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT;

                var hr = factory.GetImage(new SIZE(size, size), flags, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    Fail("GetImage", hr);
                    return null;
                }

                return FromHBitmap(hBitmap, size);
            }
            finally
            {
                if (comOwned) CoUninitialize();
            }
        }
        catch (Exception ex)
        {
            Fail("异常 " + ex.GetType().Name, ex.HResult);
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);

            if (factory is not null && Marshal.IsComObject(factory))
            {
                try { Marshal.ReleaseComObject(factory); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>
    /// 把 <c>HBITMAP</c> 读成 BGRA。
    ///
    /// <para><c>GetImage</c> 返回的是 32 位 DIB（带 alpha），
    /// 但 Shell 有个"老规矩"：如果图片本身没有 alpha 通道，
    /// 返回的位图 alpha 会全是 0。所以要检测这种情况并补成不透明，
    /// 否则整张头像会变成"完全透明"，看起来就是没画出来。</para>
    /// </summary>
    private static BgraFrame? FromHBitmap(IntPtr hBitmap, int size)
    {
        BITMAP bm;
        if (GetObjectW(hBitmap, Marshal.SizeOf<BITMAP>(), out bm) == 0) return null;

        var width = Math.Abs(bm.bmWidth);
        var height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,          // 负数 = 自顶向下，省一次翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new uint[256],
        };

        var stride = width * 4;
        var total = stride * height;
        var buffer = new byte[total];

        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            var lines = GetDIBits(screenDc, hBitmap, 0, (uint)height, buffer, ref info, DIB_RGB_COLORS);
            if (lines == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // 检测 alpha 是否全 0（Shell 对无 alpha 的图会这样返回）。
        var anyAlpha = false;
        for (var i = 3; i < total; i += 4)
        {
            if (buffer[i] != 0) { anyAlpha = true; break; }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < total; i += 4) buffer[i] = 255;
        }

        var frame = new BgraFrame(width, height);
        Array.Copy(buffer, frame.Pixels, Math.Min(total, frame.Pixels.Length));

        // 缩到目标尺寸（Shell 已经尽力匹配，这里补最后一刀）。
        return frame.Width == size && frame.Height == size
            ? frame
            : IconLoader.Resize(frame, size, size);
    }

    private static void Fail(string stage, int hr)
    {
        LastStage = stage;
        LastHResult = hr;
    }

    // ------------------------------------------------------------------ 常量

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>"允许返回比请求更大的图，我自己缩"。</summary>
    private const uint SIIGBF_RESIZETOFIT = 0x00;

    /// <summary>"宁可给大图也不要放大一张小图"。</summary>
    private const uint SIIGBF_BIGGERSIZEOK = 0x01;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly Guid IID_IShellItemImageFactory =
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // ------------------------------------------------------------------ 互操作

    /// <summary>
    /// <c>IShellItemImageFactory</c>。
    ///
    /// <para>它 <b>IUnknown 之后只有一个方法</b>（<c>GetImage</c>），
    /// 所以不存在"数错 vtable 序号"的风险 —— 这正是选它而不选 WIC 的原因。</para>
    /// </summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
}
