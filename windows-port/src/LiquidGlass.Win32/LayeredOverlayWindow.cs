using System.Runtime.InteropServices;
using System.Text;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 逐像素 Alpha 的置顶分层覆盖窗口——液态玻璃在 Windows 上的"画布"。
///
/// 关键性质：
/// <list type="bullet">
///   <item><c>WS_EX_LAYERED</c>：启用 <c>UpdateLayeredWindow</c>，支持每像素透明度。</item>
///   <item><c>WS_EX_TRANSPARENT</c> + <c>WS_EX_NOACTIVATE</c>：鼠标与键盘完全穿透，
///         玻璃层永远不会抢走任务栏图标的点击，也不会打断用户的前台窗口。</item>
///   <item><c>WS_EX_TOOLWINDOW</c>：不出现在 Alt+Tab 和任务栏里。</item>
///   <item>Z 序可精确插到任一系统组件窗口的<b>正下方</b>（<see cref="PlaceBelow"/>），
///         这是"背景接管"模式的核心——系统图标浮在玻璃之上，玻璃采样其背后的桌面。</item>
///   <item><see cref="ClickThrough"/> 可关闭，使窗口变成<b>可交互</b>的：
///         替换任务栏需要接收鼠标点击来切换应用。分层窗口的命中测试
///         基于 Alpha 通道，所以胶囊之外的透明像素会自动把点击透传给桌面，
///         不需要我们手动做命中裁剪。</item>
/// </list>
/// </summary>
public sealed class LayeredOverlayWindow : IDisposable
{
    private const string WindowClassName = "LiquidGlassOverlayWnd";

    private static readonly object ClassLock = new();
    private static ushort _classAtom;
    private static WndProcDelegate? _wndProcKeepAlive;

    private readonly object _sync = new();
    private IntPtr _handle;
    private GCHandle _selfHandle;
    private bool _disposed;
    private bool _shown;
    private bool _trackingMouseLeave;

    /// <summary>
    /// 鼠标消息回调，参数为 (消息号, 客户区 X, 客户区 Y)。
    /// 仅在 <see cref="ClickThrough"/> 为 <c>false</c> 时会收到消息。
    /// </summary>
    public event Action<uint, int, int>? MouseMessage;

    /// <summary>鼠标离开窗口时触发（由内部 TrackMouseEvent 驱动）。</summary>
    public event Action? MouseLeft;

    /// <summary>窗口句柄。仅在 <see cref="Create"/> 成功后有效。</summary>
    public IntPtr Handle
    {
        get { lock (_sync) return _handle; }
    }

    /// <summary>本窗口将鼠标消息直接放行给下方窗口（HTTRANSPARENT）。</summary>
    public bool ClickThrough { get; set; } = true;

    /// <summary>是否属于置顶带（topmost band）。</summary>
    public bool TopMost { get; init; } = true;

    /// <summary>用于日志的友好名字。</summary>
    public string Name { get; init; } = "glass";

    /// <summary>诊断日志（桌面寄生等内部操作上报用）。</summary>
    public Action<string>? Log { get; set; }

    // 寄生到桌面 WorkerW 后，本窗口的父坐标原点的屏幕位置。
    // Present / SetBounds / MoveTo 都会减去它，把"屏幕坐标"换算成"父相对坐标"。
    // 未寄生时为 (0,0)，即退化为普通顶层窗口、直接使用屏幕坐标。
    private int _parentOriginX;
    private int _parentOriginY;
    private bool _parentedToDesktop;

    // ------------------------------------------------------------- 生命周期

    /// <summary>在当前线程创建窗口。同一线程可创建多个实例。</summary>
    public bool Create(int x = 0, int y = 0, int width = 1, int height = 1)
    {
        EnsureClassRegistered();

        var exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (ClickThrough) exStyle |= WS_EX_TRANSPARENT;
        if (TopMost) exStyle |= WS_EX_TOPMOST;

        _handle = CreateWindowEx(
            exStyle, WindowClassName, Name, WS_POPUP,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"CreateWindowEx 失败（Win32 错误码 {err}）。" +
                "若错误码为 1400（无效窗口句柄），通常是父窗口或实例句柄参数异常。");
        }

        // 把 this 挂到窗口上，供静态窗口过程找回实例（鼠标交互要用）。
        _selfHandle = GCHandle.Alloc(this);
        SetWindowLongPtr(_handle, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));

        return true;
    }

    /// <summary>把一帧 BGRA 像素（<b>非</b>预乘）推送到窗口。<paramref name="frame"/> 会被就地预乘。</summary>
    /// <summary>
    /// 预乘输出的复用缓冲。
    ///
    /// <para><b>为什么不能就地预乘</b>：<c>PremultiplyInPlace</c> 会把 RGB 乘以 alpha，
    /// 同一个帧被预乘两次就会变暗一次。调用方一旦复用上屏帧（为了避免每次分配
    /// 6.9MB 的大对象），就地预乘就会让画面逐帧变黑。</para>
    ///
    /// <para><b>为什么用复用缓冲而不是每次新建</b>：面板帧是 1362×1270×4 ≈ 6.9MB，
    /// 超过 85KB 就会进 <b>LOH（大对象堆）</b>，而 LOH 默认不压缩 ——
    /// 累积去噪期间 10Hz 上屏 = 约 70MB/s 的 LOH 分配，会造成碎片化与私有内存虚高。
    /// 复用一张缓冲即可彻底消除这块churn。</para>
    /// </summary>
    private byte[]? _premultiplyScratch;

    public void Present(BgraFrame frame, int screenX, int screenY)
    {
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero) return;

            // 预乘到复用缓冲，**不改动调用方的帧** —— 它可能是被复用的上屏缓冲。
            // （变量名避开下面 UpdateLayeredWindow 用的 src/dst POINT，它们在同一作用域。）
            var framePixels = frame.Pixels;
            var scratch = _premultiplyScratch;
            if (scratch is null || scratch.Length != framePixels.Length)
            {
                scratch = new byte[framePixels.Length];
                _premultiplyScratch = scratch;
            }
            PremultiplyInto(framePixels, scratch);

            // 寄生到桌面 WorkerW 后，分层窗口的坐标是父相对坐标，
            // 必须减去父窗口的屏幕原点，否则画面会出现在错误位置。
            var x = screenX - _parentOriginX;
            var y = screenY - _parentOriginY;

            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            try
            {
                var info = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = frame.Width,
                        biHeight = -frame.Height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    },
                    bmiColors = new uint[256],
                };

                var bitmap = CreateDIBSection(memoryDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero) return;

                var oldBitmap = SelectObject(memoryDc, bitmap);
                try
                {
                    Marshal.Copy(scratch, 0, bits, scratch.Length);

                    var dst = new POINT(x, y);
                    var size = new SIZE(frame.Width, frame.Height);
                    var src = new POINT(0, 0);
                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA,   // 逐像素 Alpha
                    };

                    UpdateLayeredWindow(
                        _handle, IntPtr.Zero, ref dst, ref size,
                        memoryDc, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memoryDc, oldBitmap);
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>只移动窗口位置，不重传像素（成本极低，适合跟随系统组件移动）。</summary>
    public void MoveTo(int x, int y) =>
        SetWindowPos(SafeHandle, IntPtr.Zero,
            x - _parentOriginX, y - _parentOriginY, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>改变窗口几何尺寸。像素内容需随后重新 <see cref="Present"/>。</summary>
    public void SetBounds(int x, int y, int width, int height) =>
        SetWindowPos(SafeHandle, IntPtr.Zero,
            x - _parentOriginX, y - _parentOriginY,
            Math.Max(1, width), Math.Max(1, height),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>
    /// 把本窗口插到 <paramref name="anchor"/> 的<b>正下方</b>。
    /// Windows 中 <c>hWndInsertAfter</c> 的语义是"排在它之后（Z 序更低）"，
    /// 因此传入锚点窗口即可让玻璃层贴在其下方，同时保持在其它窗口之上。
    /// </summary>
    public void PlaceBelow(IntPtr anchor)
    {
        if (anchor == IntPtr.Zero) return;
        SetWindowPos(SafeHandle, anchor, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 把窗口寄生到桌面壁纸所在的 <c>WorkerW</c>（<c>Progman</c> 派生）。
    ///
    /// <para>⚠️ <b>实测结论（v2.2.0）：这个层级在桌面图标/壁纸之下，视觉上等同于"任务栏不见了"。</b>
    /// 它只适合"把自己当作壁纸"的场景（Wallpaper Engine 就是用它，因为那张图本来就要当壁纸）。
    /// 想要"桌面之上、窗口之下但<b>看得见</b>"，请用 <see cref="PlaceOnDesktopBottom"/>。</para>
    ///
    /// <para>保留此方法仅为需要"藏在桌面图标后面"的特殊用途。寄生后坐标变成父相对，
    /// <see cref="Present"/> / <see cref="SetBounds"/> / <see cref="MoveTo"/>
    /// 已统一减去 <see cref="_parentOriginX/Y"/>。任一步失败则保持普通顶层窗口作为降级。</para>
    /// </summary>
    public void PlaceOnDesktop()
    {
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero || _parentedToDesktop) return;

            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                Log?.Invoke("PlaceOnDesktop：找不到 Progman，退回普通非置顶窗口。");
                return;
            }

            // 让 Progman 派生出一个可寄生的 WorkerW（壁纸/图标所在层）。
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero,
                SMTO_NORMAL, 1000, out _);

            var monitor = MonitorFromWindow(_handle, MONITOR_DEFAULTTONEAREST);
            var worker = FindDesktopWorkerW(progman, monitor);
            if (worker == IntPtr.Zero)
            {
                Log?.Invoke("PlaceOnDesktop：找不到桌面 WorkerW，退回普通非置顶窗口。");
                return;
            }

            SetParent(_handle, worker);
            _parentedToDesktop = true;

            // WorkerW 的屏幕原点（多显示器下不一定是 0,0）。
            if (GetWindowRect(worker, out var r))
            {
                _parentOriginX = r.Left;
                _parentOriginY = r.Top;
            }

            // SetParent 后，已有的屏幕坐标会被当成父相对坐标，必须重新以父相对坐标定位。
            if (GetWindowRect(_handle, out var self))
            {
                SetWindowPos(_handle, IntPtr.Zero,
                    self.Left - _parentOriginX, self.Top - _parentOriginY,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }

            Log?.Invoke("PlaceOnDesktop：已寄生到桌面 WorkerW（桌面之上、窗口之下）。");
        }
    }

    /// <summary>在 Progman 的子窗口里找覆盖目标显示器的 WorkerW。</summary>
    private static IntPtr FindDesktopWorkerW(IntPtr progman, IntPtr monitor)
    {
        var found = IntPtr.Zero;

        RECT monRect = default;
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi)) monRect = mi.rcMonitor;
        }

        EnumChildWindows(progman, (hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == "WorkerW")
            {
                if (GetWindowRect(hwnd, out var r))
                {
                    // 选覆盖目标显示器中心的那个 WorkerW（多显示器时各占一块）。
                    var cx = monRect.Left + monRect.Width / 2;
                    var cy = monRect.Top + monRect.Height / 2;
                    if (cx >= r.Left && cx <= r.Right && cy >= r.Top && cy <= r.Bottom)
                    {
                        found = hwnd;
                        return false;   // 命中即停止枚举
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>把本窗口插到 <paramref name="anchor"/> 之上。</summary>
    public void PlaceAbove(IntPtr anchor)
    {
        if (anchor == IntPtr.Zero) return;
        var target = GetWindow(anchor, GW_HWNDPREV);
        SetWindowPos(SafeHandle, target == IntPtr.Zero ? HWND_TOP : target, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>压到置顶带顶端（不改变尺寸与位置）。</summary>
    public void BringToTop() =>
        SetWindowPos(SafeHandle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>
    /// 沉到普通窗口带的最底部：位于**桌面之上**、**所有普通应用窗口之下**，
    /// 并且**始终可见**（不会被壁纸或桌面图标盖住）。
    ///
    /// <para>这是"桌面小组件层级"最稳的实现：<b>不改变父子关系</b>，只用 Z 序沉底。
    /// 配合窗口自身的 <c>WS_EX_NOACTIVATE</c>，点击不会把它抬起来，
    /// 于是它会稳定停在底部，直到某个普通窗口覆盖它 —— 这正是"窗口之下"的语义。</para>
    ///
    /// <para>⚠️ 与 <see cref="PlaceOnDesktop"/> 的区别：那个会把窗口寄生到
    /// 壁纸所在的 <c>WorkerW</c>，落到桌面图标/壁纸**之下**，视觉上等同于消失
    /// （v2.2.0 实测踩过的坑）。除非你确实想"藏在图标后面"，否则用本方法。</para>
    /// </summary>
    public void PlaceOnDesktopBottom()
    {
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero) return;
            SetWindowPos(_handle, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public void Show()
    {
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero || _shown) return;
            ShowWindow(_handle, 8 /* SW_SHOWNA：显示但不激活 */);
            _shown = true;
        }
    }

    public void Hide()
    {
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero || !_shown) return;
            ShowWindow(_handle, 0 /* SW_HIDE */);
            _shown = false;
        }
    }

    /// <summary>该窗口当前是否存在于窗口管理器（用于容错重建）。</summary>
    public bool IsAlive
    {
        get { lock (_sync) return !_disposed && _handle != IntPtr.Zero && IsWindow(_handle); }
    }

    private IntPtr SafeHandle
    {
        get { lock (_sync) return _disposed ? IntPtr.Zero : _handle; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero && IsWindow(_handle))
            {
                DestroyWindow(_handle);
            }
            _handle = IntPtr.Zero;
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }
    }

    // ------------------------------------------------------------- 内部实现

    /// <summary>
    /// BGRA → 预乘 Alpha，写入 <paramref name="dest"/>（<b>不改动源</b>）。
    ///
    /// <para><c>UpdateLayeredWindow</c> 要求预乘 alpha。做成"读源写目标"而不是就地修改，
    /// 是为了让调用方能安全复用上屏帧 —— 就地修改会让复用的帧被反复预乘、
    /// 画面逐帧变暗。</para>
    /// </summary>
    private static void PremultiplyInto(byte[] src, byte[] dest)
    {
        var n = Math.Min(src.Length, dest.Length);
        for (var i = 0; i + 3 < n; i += 4)
        {
            var a = src[i + 3];
            switch (a)
            {
                case 255:
                    dest[i] = src[i];
                    dest[i + 1] = src[i + 1];
                    dest[i + 2] = src[i + 2];
                    dest[i + 3] = 255;
                    continue;

                case 0:
                    dest[i] = 0; dest[i + 1] = 0; dest[i + 2] = 0; dest[i + 3] = 0;
                    continue;

                default:
                    dest[i] = (byte)(src[i] * a / 255);
                    dest[i + 1] = (byte)(src[i + 1] * a / 255);
                    dest[i + 2] = (byte)(src[i + 2] * a / 255);
                    dest[i + 3] = a;
                    continue;
            }
        }
    }

    private void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classAtom != 0) return;
            _wndProcKeepAlive = StaticWndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero,
            };
            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                {
                    throw new InvalidOperationException($"RegisterClassEx 失败（Win32 错误码 {err}）。");
                }
                _classAtom = 1;
            }
        }
    }

    /// <summary>
    /// [诊断] 窗口过程累计收到的 <c>WM_NCHITTEST</c> 次数。
    ///
    /// <para>⚠️ 实测（Windows 11 Build 26200）：把窗口沉到普通窗口带最底部之后，
    /// 这两个计数<b>恒为 0</b> —— 即便 <c>WindowFromPoint</c> 在该点返回本窗口、
    /// 窗口也确实可见、exStyle 里没有 <c>WS_EX_TRANSPARENT</c>。
    /// 也就是说鼠标输入根本没走到窗口过程，<b>悬停与点击都不会生效</b>。
    /// 目前悬停胶囊靠轮询 <c>GetCursorPos</c> 绕开了这个问题
    /// （见 <c>ReplacementTaskbar.UpdatePointerFromCursor</c>），
    /// 但"点图标启动应用"仍然依赖鼠标消息，尚未解决。</para>
    /// </summary>
    public static long HitTestSeen;

    /// <summary>[诊断] 窗口过程累计收到的 <c>WM_MOUSEMOVE</c> 次数（见 <see cref="HitTestSeen"/>）。</summary>
    public static long MouseMoveSeen;

    /// <summary>
    /// 统计本进程里还活着几个 <c>LiquidGlassOverlayWnd</c> 窗口。
    ///
    /// <para><b>为什么需要它</b>：<see cref="Create"/> 每次调用都会
    /// <c>CreateWindowEx</c> 一个新窗口并 <c>GCHandle.Alloc</c> 一个新句柄，
    /// 旧的既不销毁也不解绑。所以"重复 Create"＝每调用一次泄漏一个分层窗口
    /// （吃的是 DWM 资源），攒够会把整个程序拖死。
    /// 任何需要反复开关的覆盖层，都应该用这个数字做回归判据：
    /// 开关 30 次之后它不该增长。</para>
    /// </summary>
    public static int CountLiveWindows()
    {
        var count = 0;
        var self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != self) return true;
            var cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() == WindowClassName) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var instance = Resolve(hwnd);

        switch (msg)
        {
            case WM_NCHITTEST:
                Interlocked.Increment(ref HitTestSeen);
                // 穿透模式下双保险：即使 WS_EX_TRANSPARENT 失效（部分外壳窗口会），
                // 也让命中测试直接穿透到下层窗口。
                // 可交互模式则交回默认处理 —— 分层窗口的命中测试基于 Alpha，
                // 胶囊之外的透明像素本来就会透传。
                return instance is { ClickThrough: true }
                    ? new IntPtr(HTTRANSPARENT)
                    : DefWindowProc(hwnd, msg, wParam, lParam);

            case WM_MOUSEACTIVATE:
                // 即使可交互，也不要在点击任务栏时把焦点从用户的应用上抢走太久；
                // MA_ACTIVATE 让窗口正常获得前台权限（激活应用时需要它）。
                return instance is { ClickThrough: false }
                    ? new IntPtr(MA_ACTIVATE)
                    : new IntPtr(MA_NOACTIVATE);

            case WM_ERASEBKGND:
                return new IntPtr(1);   // 内容由 UpdateLayeredWindow 提供，禁止擦背景

            case WM_NCCALCSIZE:
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                Interlocked.Increment(ref MouseMoveSeen);
                instance?.RaiseMouse(msg, lParam);
                instance?.EnsureMouseLeaveTracking(hwnd);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                instance?.ResetMouseLeaveTracking();
                instance?.MouseLeft?.Invoke();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
                instance?.RaiseMouse(msg, lParam);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static LayeredOverlayWindow? Resolve(IntPtr hwnd)
    {
        var ptr = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (ptr == IntPtr.Zero) return null;
        return GCHandle.FromIntPtr(ptr).Target as LayeredOverlayWindow;
    }

    private void RaiseMouse(uint msg, IntPtr lParam)
    {
        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try { MouseMessage?.Invoke(msg, x, y); }
        catch { /* 交互回调异常不该杀掉消息循环 */ }
    }

    private void EnsureMouseLeaveTracking(IntPtr hwnd)
    {
        if (_trackingMouseLeave || ClickThrough) return;

        var tme = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = hwnd,
        };
        if (TrackMouseEvent(ref tme)) _trackingMouseLeave = true;
    }

    private void ResetMouseLeaveTracking() => _trackingMouseLeave = false;

    private const int GWLP_USERDATA = -21;
    private const uint TME_LEAVE = 0x00000002;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const int MA_ACTIVATE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;

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
