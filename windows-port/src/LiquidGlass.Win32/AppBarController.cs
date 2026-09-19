using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// AppBar 协议封装 —— 用来<b>向 Shell 申领屏幕底部的矩形</b>，从而控制工作区。
///
/// <para>为什么必须做这件事：把系统任务栏藏起来之后，工作区<b>不一定</b>自动
/// 变回整屏。如果工作区仍停"屏幕 − 任务栏高度"，最大化窗口就会停在玻璃上方，
/// 玻璃背后只剩壁纸 —— 折射内容贫乏，正是 v1 效果不好的根因。
/// 让替换任务栏以 AppBar 身份申领一块矩形，Shell 会据此重算工作区。</para>
///
/// <para>AppBar 协议从 Windows 95 一直用到 Windows 11，是少数几个
/// 真正跨三代的稳定接口，也是 v2 能自动适配 Win7/10/11 的关键之一。</para>
/// </summary>
public sealed class AppBarController : IDisposable
{
    private readonly Action<string> _log;
    private readonly HiddenMessageWindow? _ownedWindow;
    private readonly IntPtr _window;
    private bool _registered;
    private bool _disposed;

    /// <summary>AppBar 回调消息 ID（ABN_*）。</summary>
    public static readonly uint CallbackMessage = RegisterWindowMessage("LiquidGlassAppBarMessage");

    /// <summary>当前申领的高度（像素）。</summary>
    public int ReservedHeight { get; private set; }

    /// <summary>是否成功注册（失败时后续 UpdatePosition 全部无效）。</summary>
    public bool IsRegistered => _registered;

    private AppBarController(IntPtr window, HiddenMessageWindow? owned, Action<string> log)
    {
        _window = window;
        _ownedWindow = owned;
        _log = log;
    }

    /// <summary>
    /// 以"几乎不预留高度"的方式注册，使工作区回到整屏。
    /// 最大化窗口因此会铺到屏幕底边，玻璃浮在它之上。
    /// </summary>
    /// <param name="ownerWindow">
    /// 接收 AppBar 回调的窗口。<see cref="IntPtr.Zero"/> 表示内部自建隐藏消息窗口。
    /// </param>
    public static AppBarController ReserveNothing(IntPtr ownerWindow, Action<string> log) =>
        Register(ownerWindow, 0, log);

    /// <summary>以指定高度注册（"保留任务栏高度"的兼容模式）。</summary>
    public static AppBarController Reserve(IntPtr ownerWindow, int height, Action<string> log) =>
        Register(ownerWindow, height, log);

    private static AppBarController Register(IntPtr ownerWindow, int height, Action<string> log)
    {
        HiddenMessageWindow? owned = null;
        var window = ownerWindow;

        if (window == IntPtr.Zero)
        {
            owned = new HiddenMessageWindow("liquidglass-appbar");
            window = owned.Handle;
        }

        var controller = new AppBarController(window, owned, log);
        try
        {
            controller.RegisterCore(height);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
        return controller;
    }

    private void RegisterCore(int height)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _window,
            uCallbackMessage = CallbackMessage,
        };

        _registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        if (!_registered)
        {
            _log($"AppBar 注册失败（错误码 {Marshal.GetLastWin32Error()}）。"
                + "工作区可能无法变成整屏，最大化窗口会停在玻璃上方。");
            return;
        }

        _log("AppBar 已注册。");
        UpdatePositionInternal(MonitorFromWindow(_window, MONITOR_DEFAULTTONEAREST), height);
    }

    /// <summary>把 AppBar 位置更新到指定显示器与保留高度。</summary>
    public void UpdatePosition(IntPtr monitor, int height)
    {
        if (!_registered || _disposed) return;
        UpdatePositionInternal(monitor, height);
    }

    private void UpdatePositionInternal(IntPtr monitor, int height)
    {
        var bounds = GetMonitorBoundsFor(monitor);
        if (bounds.IsEmpty)
        {
            bounds = DisplayEnvironment.GetVirtualDesktopBounds();
            if (bounds.IsEmpty) return;
        }

        // 完全 0 高度的矩形会被 Shell 当作退化矩形而忽略，
        // 所以至少给 1px。1px 在视觉上不可见，但足以触发工作区重算。
        var effective = Math.Max(1, height);
        var rect = new RECT
        {
            Left = bounds.Left,
            Top = bounds.Bottom - effective,
            Right = bounds.Right,
            Bottom = bounds.Bottom,
        };

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _window,
            uEdge = ABE_BOTTOM,
            rc = rect,
        };

        // 标准三段式：先问 Shell 建议位置，再用它修正后的值提交。
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Left = rect.Left;
        data.rc.Right = rect.Right;
        data.rc.Top = rect.Top;
        data.rc.Bottom = rect.Bottom;
        SHAppBarMessage(ABM_SETPOS, ref data);

        ReservedHeight = height;
        _log($"AppBar 位置已提交：底部保留 {height}px（实际矩形高 {effective}px）。");
    }

    private static Win32Rect GetMonitorBoundsFor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return default;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;
        return new Win32Rect(info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = _window,
            };
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
            _log("AppBar 已注销，工作区交还 Shell。");
        }

        _ownedWindow?.Dispose();
    }
}
