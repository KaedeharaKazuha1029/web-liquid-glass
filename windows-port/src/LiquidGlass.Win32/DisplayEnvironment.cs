using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>一个显示器的描述。</summary>
public sealed record MonitorDescriptor(
    IntPtr Handle,
    Win32Rect Bounds,
    Win32Rect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public double Scale => DpiX / 96.0;
    public override string ToString() =>
        $"物理={Bounds} 工作区={WorkArea} DPI={DpiX}x{DpiY} 缩放={Scale:P0} 主={IsPrimary}";
}

/// <summary>进程 DPI 感知级别。</summary>
public enum DpiAwareness
{
    Unaware,
    SystemAware,
    PerMonitorAware,
    Unknown,
}

/// <summary>
/// 显示环境（DPI 感知 + 显示器枚举）。
///
/// 为什么这件事必须最先做：Windows 给了两套坐标——逻辑像素和物理像素。
/// 如果进程还是 DPI 不感知状态，<c>GetWindowRect</c> 返回的是被系统拉伸过的
/// 逻辑坐标，玻璃层就会和任务栏错位（典型的"高 DPI 下玻璃偏移一半"故障）。
/// </summary>
public static class DisplayEnvironment
{
    /// <summary>
    /// 开启 Per-Monitor V2 DPI 感知。<b>必须在创建任何窗口之前调用</b>，
    /// 否则系统会锁定旧的感知级别并返回失败。
    /// </summary>
    public static bool EnablePerMonitorDpiAwareness()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return true;
        }
        catch (EntryPointNotFoundException) { /* Win10 1703 以下没有这个入口 */ }
        catch (DllNotFoundException) { }

        try
        {
            // PROCESS_PER_MONITOR_DPI_AWARE = 2
            return SetProcessDpiAwareness(2) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取当前线程的 DPI 感知级别（诊断用）。</summary>
    public static DpiAwareness GetDpiAwareness()
    {
        try
        {
            var ctx = GetThreadDpiAwarenessContext();
            // 注意：GetAwarenessFromDpiAwarenessContext 返回的是 DPI_AWARENESS
            // 枚举值（-1 / 0 / 1 / 2），不是传入的那个 CONTEXT 值，
            // 这两套常量容易混用，这里按 DPI_AWARENESS 解释。
            return GetAwarenessFromDpiAwarenessContext(ctx) switch
            {
                -1 => DpiAwareness.Unknown,          // DPI_AWARENESS_INVALID
                0 => DpiAwareness.Unaware,           // DPI_AWARENESS_UNAWARE
                1 => DpiAwareness.SystemAware,       // DPI_AWARENESS_SYSTEM_AWARE
                2 => DpiAwareness.PerMonitorAware,   // DPI_AWARENESS_PER_MONITOR_AWARE
                _ => DpiAwareness.Unknown,
            };
        }
        catch
        {
            return DpiAwareness.Unknown;
        }
    }

    /// <summary>枚举所有显示器（物理坐标）。</summary>
    public static IReadOnlyList<MonitorDescriptor> EnumerateMonitors()
    {
        var list = new List<MonitorDescriptor>();
        var primary = MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTONEAREST);

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var dpiX = 96u;
                var dpiY = 96u;
                try { GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY); }
                catch { /* 老系统缺 shcore.dll 时按 100% 处理 */ }

                list.Add(new MonitorDescriptor(
                    hMonitor,
                    new Win32Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                    new Win32Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                    dpiX, dpiY,
                    hMonitor == primary));
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>整个虚拟桌面的包围盒（多显示器可能为负坐标）。</summary>
    public static Win32Rect GetVirtualDesktopBounds()
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0) return new Win32Rect(0, 0, 1920, 1080);

        var left = monitors.Min(m => m.Bounds.Left);
        var top = monitors.Min(m => m.Bounds.Top);
        var right = monitors.Max(m => m.Bounds.Right);
        var bottom = monitors.Max(m => m.Bounds.Bottom);
        return new Win32Rect(left, top, right, bottom);
    }

    /// <summary>光标位置（物理坐标）。</summary>
    public static (int X, int Y) GetCursorPosition() =>
        GetCursorPos(out var pt) ? (pt.X, pt.Y) : (0, 0);

    /// <summary>取窗口所在的显示器句柄（最近的）。</summary>
    public static IntPtr MonitorFromWindowHandle(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>主显示器句柄。</summary>
    public static IntPtr GetPrimaryMonitor() =>
        MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTONEAREST);

    /// <summary>显示器的物理边界。</summary>
    public static Win32Rect GetMonitorBounds(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return default;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;
        return new Win32Rect(info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    /// <summary>
    /// 显示器的工作区（即排除任务栏等 AppBar 之后可用于窗口的区域）。
    /// 隐藏任务栏前后对比这个值，就能判断 Shell 有没有重算布局。
    /// </summary>
    public static Win32Rect GetMonitorWorkArea(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return default;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;
        return new Win32Rect(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right, info.rcWork.Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
}
