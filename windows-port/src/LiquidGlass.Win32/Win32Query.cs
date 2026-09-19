using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 少量常用的窗口查询，包成公开 API，
/// 免得上层为了一次 <c>IsWindow</c> 就去 <c>using static</c> 整个 P/Invoke 列表。
/// </summary>
public static class Win32Query
{
    /// <summary>句柄是否仍然指向一个存在的窗口。</summary>
    public static bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);

    /// <summary>窗口是否可见。</summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd);

    /// <summary>窗口是否最小化。</summary>
    public static bool IsMinimized(IntPtr hwnd) => IsWindow(hwnd) && NativeMethods.IsIconic(hwnd);

    /// <summary>前台窗口句柄。</summary>
    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>桌面窗口句柄。</summary>
    public static IntPtr GetDesktopWindow() => NativeMethods.GetDesktopWindow();

    /// <summary>窗口是否带 WS_EX_TOPMOST。</summary>
    public static bool IsTopMost(IntPtr hwnd) =>
        IsWindow(hwnd) && (NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>窗口所在显示器句柄。</summary>
    public static IntPtr MonitorFromWindow(IntPtr hwnd) =>
        NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>取显示器矩形（含任务栏区域）。</summary>
    public static Win32Rect? GetMonitorBounds(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        return new Win32Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    /// <summary>把句柄置前并激活（仅用于托盘菜单里的"打开配置"等交互）。</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        NativeMethods.SetForegroundWindow(hwnd);
    }
}
