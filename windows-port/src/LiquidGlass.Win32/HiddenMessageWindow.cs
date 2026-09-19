using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 一个从不显示的顶层消息窗口，用于接收 Win32 通知消息。
///
/// <para>它存在的意义：AppBar 协议、全局热键、Shell 钩子都要求"把消息发到某个窗口"。
/// 我们不希望这些基础设施把消息搅进真正承载渲染的那个窗口里，
/// 于是单开一个从不 ShowWindow、从不参与渲染的窗口专门收消息。</para>
///
/// <para>尺寸给 1×1 且永不显示，因此不会出现在屏幕上、Alt+Tab 里，
/// 也不会被 <c>EnumWindows</c> 当成可见窗口。</para>
/// </summary>
public sealed class HiddenMessageWindow : IDisposable
{
    private const string ClassName = "LiquidGlassMessageWnd";
    private const int GWLP_USERDATA = -21;

    private static readonly object ClassLock = new();
    private static ushort _classAtom;
    private static WndProcDelegate? _keepAlive;

    private IntPtr _handle;
    private GCHandle _selfHandle;
    private bool _disposed;

    /// <summary>收到消息时回调。返回非 null 即作为窗口过程结果返回。</summary>
    public event Func<uint, IntPtr, IntPtr, IntPtr?>? MessageReceived;

    public IntPtr Handle => _handle;

    public HiddenMessageWindow(string name = "message")
    {
        EnsureClassRegistered();

        _handle = CreateWindowEx(
            0, ClassName, name, WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"消息窗口创建失败（Win32 错误码 {Marshal.GetLastWin32Error()}）。");
        }

        // 把 this 挂到窗口上，供静态窗口过程找回实例。
        _selfHandle = GCHandle.Alloc(this);
        SetWindowLongPtr(_handle, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
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
            };

            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx 失败（Win32 错误码 {Marshal.GetLastWin32Error()}）。");
            }
            _classAtom = 1;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var ptr = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (ptr != IntPtr.Zero)
        {
            var target = GCHandle.FromIntPtr(ptr).Target as HiddenMessageWindow;
            if (target?.MessageReceived is { } handler)
            {
                foreach (Func<uint, IntPtr, IntPtr, IntPtr?> callback in handler.GetInvocationList())
                {
                    try
                    {
                        if (callback(msg, wParam, lParam) is { } value) return value;
                    }
                    catch
                    {
                        // 回调异常不该杀掉消息循环。
                    }
                }
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            if (IsWindow(_handle)) DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
}
