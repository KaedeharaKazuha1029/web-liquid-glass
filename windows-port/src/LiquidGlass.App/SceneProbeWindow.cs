using System.Runtime.InteropServices;

namespace LiquidGlass.App;

/// <summary>
/// 一块可切换图案的普通窗口，专门用来验证"玻璃是不是真的在实时跟着背后画面走"。
///
/// <para>它<b>不是</b>置顶窗口，所以永远待在替换任务栏的下面 ——
/// 正好就是"玻璃背后的场景"这个角色。切换图案后，
/// 如果玻璃是实时渲染的，面板区域的平均像素值应该跟着变。</para>
///
/// <para>它带 <c>WS_EX_TOOLWINDOW</c> 且没有标题，因此会被替换任务栏的
/// 窗口筛选规则排除，不会污染应用列表。</para>
/// </summary>
internal sealed class SceneProbeWindow : IDisposable
{
    private const string ClassName = "LiquidGlassSceneProbe";

    private static readonly object ClassLock = new();
    private static ushort _classAtom;
    private static WndProcDelegate? _keepAlive;

    private IntPtr _handle;
    private int _seed;
    private bool _disposed;

    public IntPtr Handle => _handle;

    public SceneProbeWindow(int x, int y, int width, int height)
    {
        EnsureClassRegistered();

        _handle = CreateWindowEx(
            WS_EX_TOOLWINDOW,
            ClassName, "LiquidGlass Scene Probe",
            WS_POPUP,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"探测窗口创建失败（Win32 错误码 {Marshal.GetLastWin32Error()}）。");
        }

        SetWindowLongPtr(_handle, GWLP_WNDPROC_INDEX, GCHandle.ToIntPtr(GCHandle.Alloc(this)));

        // SW_SHOWNA：显示但不激活，避免抢走前台焦点影响其它检测。
        ShowWindow(_handle, unchecked((int)SW_SHOWNA));
    }

    /// <summary>换一套图案。图案是高对比棋盘 + 彩色条纹，方便肉眼和像素统计同时看出区别。</summary>
    public void SetPattern(int seed)
    {
        _seed = seed;
        if (_handle != IntPtr.Zero && IsWindow(_handle))
        {
            InvalidateRect(_handle, IntPtr.Zero, false);
            UpdateWindow(_handle);
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classAtom != 0) return;
            _keepAlive = StaticWndProc;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
                hbrBackground = IntPtr.Zero,
            };

            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException("探测窗口类注册失败。");
            }
            _classAtom = 1;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_PAINT)
        {
            var instance = Resolve(hwnd);
            if (instance is not null) instance.Paint(hwnd);
            return IntPtr.Zero;
        }
        if (msg == WM_ERASEBKGND) return new IntPtr(1);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static SceneProbeWindow? Resolve(IntPtr hwnd)
    {
        var ptr = GetWindowLongPtr(hwnd, GWLP_WNDPROC_INDEX);
        return ptr == IntPtr.Zero ? null : GCHandle.FromIntPtr(ptr).Target as SceneProbeWindow;
    }

    /// <summary>用 GDI 画一组高对比色块。图案随 seed 变化，肉眼一看就能分辨。</summary>
    private void Paint(IntPtr hwnd)
    {
        var dc = BeginPaint(hwnd, out var ps);
        if (dc == IntPtr.Zero) return;

        try
        {
            if (!GetClientRect(hwnd, out var rect)) return;

            // 四套差异极大的配色，切换时玻璃后面会明显换色
            (uint A, uint B)[] palettes =
            [
                (0x0000FFFF, 0x00000000),   // 黄 / 黑
                (0x00FF00FF, 0x00FFFFFF),   // 洋红 / 白
                (0x00FFFFFF, 0x00FF0000),   // 白 / 蓝
                (0x00FF8000, 0x00008000),   // 青 / 绿
            ];

            var palette = palettes[Math.Abs(_seed) % palettes.Length];
            var cellW = Math.Max(8, rect.Width / 40);
            var cellH = Math.Max(8, rect.Height / 4);

            for (var y = 0; y < rect.Height; y += cellH)
            {
                for (var x = 0; x < rect.Width; x += cellW)
                {
                    var on = ((x / cellW) + (y / cellH) + _seed) % 2 == 0;
                    var brush = CreateSolidBrush(on ? palette.A : palette.B);
                    if (brush == IntPtr.Zero) continue;

                    var block = new RECT
                    {
                        Left = x,
                        Top = y,
                        Right = Math.Min(rect.Right, x + cellW),
                        Bottom = Math.Min(rect.Bottom, y + cellH),
                    };
                    FillRect(dc, ref block, brush);
                    DeleteObject(brush);
                }
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            var ptr = GetWindowLongPtr(_handle, GWLP_WNDPROC_INDEX);
            if (ptr != IntPtr.Zero) GCHandle.FromIntPtr(ptr).Free();
            if (IsWindow(_handle)) DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // 自带的 Win32 常量与结构 —— 这是自检工具，刻意不依赖 Win32 层的内部类型，
    // 免得为了一个探测窗口把整个 P/Invoke 面暴露出去。
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        [MarshalAs(UnmanagedType.Bool)] public bool fErase;
        public RECT rcPaint;
        [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
        [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    private const int GWLP_WNDPROC_INDEX = -21;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
