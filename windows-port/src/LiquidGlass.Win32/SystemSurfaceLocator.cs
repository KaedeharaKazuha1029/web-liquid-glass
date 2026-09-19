using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>可被液态玻璃接管的系统界面类别。</summary>
public enum SurfaceKind
{
    /// <summary>主显示器任务栏（Shell_TrayWnd）。</summary>
    Taskbar,
    /// <summary>副显示器任务栏（Shell_SecondaryTrayWnd）。</summary>
    SecondaryTaskbar,
    /// <summary>开始菜单（StartMenuExperienceHost）。</summary>
    StartMenu,
    /// <summary>搜索面板（SearchHost）。</summary>
    Search,
    /// <summary>操作中心 / 快速设置（ShellExperienceHost / ControlCenter）。</summary>
    ActionCenter,
    /// <summary>小组件面板（Widgets）。</summary>
    Widgets,
    /// <summary>任务视图（explorer 的 MultitaskingViewFrame）。</summary>
    TaskView,
    /// <summary>桌面图标层（Progman / WorkerW），用于整体桌面玻璃化。</summary>
    Desktop,
    /// <summary>用户通过窗口类名/进程名自定义的目标。</summary>
    Custom,
}

/// <summary>一个具体的系统界面窗口，及其当前屏幕矩形。</summary>
public sealed class SystemSurface
{
    public required SurfaceKind Kind { get; init; }
    public required IntPtr Handle { get; init; }
    public required string ClassName { get; init; }
    public required string ProcessName { get; init; }
    public required Win32Rect Bounds { get; init; }
    public bool IsVisible { get; init; }
    public bool IsTopMost { get; init; }

    public override string ToString() =>
        $"{Kind,-18} hwnd=0x{Handle.ToInt64():X8} proc={ProcessName,-26} class={ClassName,-34} {Bounds}";
}

/// <summary>与 WPF / System.Drawing 无关的矩形，避免 Core 层引入平台依赖。</summary>
public readonly record struct Win32Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int X => Left;
    public int Y => Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

/// <summary>
/// 系统界面窗口的发现与定位。
/// 全部基于"窗口类名 + 宿主进程名"匹配，可在 Windows 10 / 11 上工作。
/// </summary>
public static class SystemSurfaceLocator
{
    /// <summary>进程名 → 界面类别的映射表（下划线小写比较，不含 .exe）。</summary>
    private static readonly (string Process, SurfaceKind Kind)[] ProcessMap =
    [
        ("startmenuexperiencehost", SurfaceKind.StartMenu),
        ("searchhost", SurfaceKind.Search),
        ("shellexperiencehost", SurfaceKind.ActionCenter),
        ("controlcenter", SurfaceKind.ActionCenter),
        ("widgets", SurfaceKind.Widgets),
        ("windowswidgets", SurfaceKind.Widgets),
        ("widgetservice", SurfaceKind.Widgets),
    ];

    /// <summary>枚举当前所有可见的系统界面窗口。</summary>
    public static IReadOnlyList<SystemSurface> Discover(bool includeHidden = false)
    {
        var results = new List<SystemSurface>();
        var seen = new HashSet<IntPtr>();

        // 1) 任务栏：类名固定，且不依赖进程名查询（explorer 有多个同进程窗口）。
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && seen.Add(tray))
        {
            results.Add(Create(tray, SurfaceKind.Taskbar, "Shell_TrayWnd", "explorer", includeHidden));
        }

        // 2) 副显示器任务栏：可能有多条，用 EnumWindows 按类名收集。
        EnumWindows((hwnd, _) =>
        {
            var cls = GetClassNameOf(hwnd);
            if (cls == "Shell_SecondaryTrayWnd" && seen.Add(hwnd))
            {
                results.Add(Create(hwnd, SurfaceKind.SecondaryTaskbar, cls, "explorer", includeHidden));
            }
            return true;
        }, IntPtr.Zero);

        // 3) 开始菜单 / 搜索 / 操作中心 / 小组件：按宿主进程名识别。
        EnumWindows((hwnd, _) =>
        {
            if (seen.Contains(hwnd)) return true;
            if (!IsWindowVisible(hwnd)) return true;

            var cls = GetClassNameOf(hwnd);
            // 这些 UWP/XAML 宿主统一用 CoreWindow / WinUIDesktopWin32WindowClass 承载，
            // 只有进程名能区分彼此，所以先按类名快速筛掉绝大多数窗口。
            if (cls is not ("Windows.UI.Core.CoreWindow"
                            or "WinUIDesktopWin32WindowClass"
                            or "Microsoft.UI.Content.DesktopChildSiteBridge"
                            or "ApplicationFrameWindow"))
            {
                return true;
            }

            var process = GetProcessNameOf(hwnd);
            var key = process.Replace(".exe", string.Empty).ToLowerInvariant();
            foreach (var (mapped, kind) in ProcessMap)
            {
                if (key == mapped || key.StartsWith(mapped, StringComparison.Ordinal))
                {
                    seen.Add(hwnd);
                    results.Add(Create(hwnd, kind, cls, process, includeHidden));
                    break;
                }
            }
            return true;
        }, IntPtr.Zero);

        // 4) 任务视图（explorer 内部的全屏 XAML 窗口，标题为 "Task View"）
        EnumWindows((hwnd, _) =>
        {
            if (seen.Contains(hwnd) || !IsWindowVisible(hwnd)) return true;
            var title = GetWindowTextOf(hwnd);
            if (title is "Task View" or "任务视图")
            {
                seen.Add(hwnd);
                results.Add(Create(hwnd, SurfaceKind.TaskView, GetClassNameOf(hwnd), GetProcessNameOf(hwnd), includeHidden));
            }
            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>按类别取第一个匹配项（通常用于任务栏 / 开始菜单）。</summary>
    public static SystemSurface? Find(SurfaceKind kind, bool includeHidden = false) =>
        Discover(includeHidden).FirstOrDefault(s => s.Kind == kind);

    /// <summary>取某个显示器上的任务栏（主显示器 → Shell_TrayWnd）。</summary>
    public static SystemSurface? FindTaskbarForMonitor(IntPtr hMonitor, bool includeHidden = false)
    {
        var primary = MonitorFromWindow(FindWindow("Shell_TrayWnd", null), MONITOR_DEFAULTTONEAREST);
        if (primary == hMonitor) return Find(SurfaceKind.Taskbar, includeHidden);

        return Discover(includeHidden).FirstOrDefault(s =>
            s.Kind == SurfaceKind.SecondaryTaskbar &&
            MonitorFromWindow(s.Handle, MONITOR_DEFAULTTONEAREST) == hMonitor);
    }

    /// <summary>按进程名（不含 .exe，大小写不敏感）查找可见窗口。</summary>
    public static IReadOnlyList<SystemSurface> FindByProcess(string processName)
    {
        var target = processName.Replace(".exe", string.Empty).ToLowerInvariant();
        var results = new List<SystemSurface>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            if (rect.Width <= 0 || rect.Height <= 0) return true;
            var p = GetProcessNameOf(hwnd).Replace(".exe", string.Empty).ToLowerInvariant();
            if (p == target)
            {
                results.Add(Create(hwnd, SurfaceKind.Custom, GetClassNameOf(hwnd), GetProcessNameOf(hwnd), false));
            }
            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <summary>按窗口类名（大小写不敏感）查找可见窗口。</summary>
    public static IReadOnlyList<SystemSurface> FindByClass(string className)
    {
        var results = new List<SystemSurface>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!string.Equals(GetClassNameOf(hwnd), className, StringComparison.OrdinalIgnoreCase)) return true;
            results.Add(Create(hwnd, SurfaceKind.Custom, className, GetProcessNameOf(hwnd), false));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    // ------------------------------------------------------------- 辅助取数

    /// <summary>
    /// 取窗口的"视觉"边界。优先使用 DWM 扩展边框，
    /// 因为它排除了 Win10/11 时代窗口四周的隐形拖拽边框（对任务栏尤其关键）。
    /// </summary>
    public static Win32Rect GetVisualBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dwmRect, Marshal.SizeOf<RECT>()) == 0)
        {
            var r = new Win32Rect(dwmRect.Left, dwmRect.Top, dwmRect.Right, dwmRect.Bottom);
            if (!r.IsEmpty) return r;
        }

        GetWindowRect(hwnd, out var rect);
        return new Win32Rect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    public static string GetWindowTextOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        var len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    /// <summary>取窗口宿主进程名（形如 explorer.exe）。失败返回空串，不抛异常。</summary>
    public static string GetProcessNameOf(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return string.Empty;

            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size))
                {
                    var full = sb.ToString(0, (int)size);
                    return Path.GetFileName(full);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            // 受保护进程（如 System）查询会失败，静默返回空串。
        }
        return string.Empty;
    }

    private static SystemSurface Create(
        IntPtr hwnd, SurfaceKind kind, string className, string processName, bool includeHidden)
    {
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return new SystemSurface
        {
            Kind = kind,
            Handle = hwnd,
            ClassName = className,
            ProcessName = processName,
            Bounds = GetVisualBounds(hwnd),
            IsVisible = IsWindowVisible(hwnd),
            IsTopMost = (exStyle & WS_EX_TOPMOST) != 0,
        };
    }
}
