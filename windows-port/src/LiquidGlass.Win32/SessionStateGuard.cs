using System.Runtime.InteropServices;
using System.Text.Json;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 跨进程的系统状态恢复保障。
///
/// <para>为什么需要它：替换任务栏会改动两样系统状态——任务栏的可见性和它在 AppBar 里的预留。
/// 如果进程被强杀（任务管理器结束进程、蓝屏、断电），进程内的一切 <c>finally</c> 都不会执行，
/// 用户的任务栏就会停在异常状态。</para>
///
/// <para>对策是把"改动前是什么样"落盘到一个状态文件：
/// 每次启动先读它，如果发现上次留下了未清理的状态，就先把系统修回去再继续。
/// 这样即使连续崩溃，也不会累积出"工作区永久变大 + 任务栏消失"这种糟糕局面。</para>
///
/// <para>状态文件的位置与日志同目录，内容是纯文本 JSON，用户可以自己看。</para>
/// </summary>
public static class SessionStateGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>状态文件路径：<c>%LOCALAPPDATA%\LiquidGlass\session-state.json</c>。</summary>
    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiquidGlass", "session-state.json");

    /// <summary>一次会话里我们对系统做过的改动记录。</summary>
    public sealed class SessionState
    {
        /// <summary>开启自动隐藏之前的原始 ABM_GETSTATE 值。仅当真的用了这条路径才有值。</summary>
        public int OriginalAutoHideState { get; set; } = -1;

        /// <summary>是否真的改过自动隐藏。</summary>
        public bool AutoHideChanged { get; set; }

        /// <summary>任务栏原本的 AppBar 矩形。为空表示没改过预留。</summary>
        public int[]? OriginalTaskbarRect { get; set; }

        /// <summary>会话开始时间，仅供人类阅读。</summary>
        public string StartedAt { get; set; } = "";

        /// <summary>写入状态的进程 ID，便于排查。</summary>
        public int ProcessId { get; set; }
    }

    /// <summary>读取上次遗留的状态；不存在或已损坏返回 null。</summary>
    public static SessionState? Load()
    {
        try
        {
            var path = StatePath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionState>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>落盘当前会话状态。失败静默（磁盘满 / 无权限都不该挡住主流程）。</summary>
    public static void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // 静默：状态文件只是安全网，写不进去也不能让程序起不来。
        }
    }

    /// <summary>清理状态文件（正常退出时调用）。</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>
    /// 如果上次留下了未清理的状态，把它修回去。返回是否真的做了修复。
    /// <b>每次启动接管之前都应先调用它。</b>
    /// </summary>
    public static bool RecoverIfNeeded(Action<string> log)
    {
        var state = Load();
        if (state is null) return false;

        // 状态文件存在但进程已经不在了 → 上次是异常退出。
        log($"检测到上次会话（PID {state.ProcessId}，{state.StartedAt}）留下了未清理的系统状态，正在修复…");

        var repaired = false;

        // ① 还原 AppBar 预留
        if (state.OriginalTaskbarRect is { Length: 4 } rect)
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && ApplyTaskbarRect(tray, rect))
            {
                log($"  已还原任务栏 AppBar 预留为 ({rect[0]},{rect[1]})-({rect[2]},{rect[3]})。");
                repaired = true;
            }
        }

        // ② 还原自动隐藏开关
        if (state.AutoHideChanged && state.OriginalAutoHideState >= 0)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                lParam = new IntPtr(state.OriginalAutoHideState),
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
            log($"  已还原任务栏自动隐藏开关为 {TaskbarVisibilityController.DescribeAutoHideState(state.OriginalAutoHideState)}。");
            repaired = true;
        }

        // ③ 确保任务栏窗口可见并回到正确位置
        var trayWindow = FindWindow("Shell_TrayWnd", null);
        if (trayWindow != IntPtr.Zero)
        {
            if (!IsWindowVisible(trayWindow)) ShowWindow(trayWindow, 8 /* SW_SHOWNA */);
            NudgeTaskbarIntoPlace(trayWindow);
            repaired = true;
        }

        Clear();
        if (repaired) log("上次遗留状态已修复。");
        return repaired;
    }

    /// <summary>
    /// 把任务栏强行摆回它自己的 AppBar 矩形。
    ///
    /// <para>为什么需要"推一把"：把 AppBar 预留改小之后，explorer 会把任务栏窗口
    /// 挪到贴近屏幕边缘的隐藏位（y 超出屏幕底）以配合自动隐藏。
    /// 只还原预留矩形，窗口位置不会自动跟着回来，必须显式 SetWindowPos。</para>
    /// </summary>
    public static void NudgeTaskbarIntoPlace(IntPtr taskbarHandle)
    {
        if (taskbarHandle == IntPtr.Zero || !IsWindow(taskbarHandle)) return;

        var rect = ReadTaskbarRect(taskbarHandle);
        if (rect.IsEmpty)
        {
            // 读不到就用"屏幕底部一条标准任务栏"兜底。
            var monitor = MonitorFromWindow(taskbarHandle, MONITOR_DEFAULTTONEAREST);
            var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
            var height = Math.Max(40, (int)(bounds.Height * 0.045));   // 经验值：1080p → 48px
            rect = new Win32Rect(bounds.Left, bounds.Bottom - height, bounds.Right, bounds.Bottom);
        }

        SetWindowPos(taskbarHandle, IntPtr.Zero,
            rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    /// <summary>提交任务栏的 AppBar 矩形。</summary>
    private static bool ApplyTaskbarRect(IntPtr taskbarHandle, int[] rect) =>
        ApplyTaskbarRect(taskbarHandle, new Win32Rect(rect[0], rect[1], rect[2], rect[3]));

    /// <summary>提交任务栏的 AppBar 矩形。</summary>
    internal static bool ApplyTaskbarRect(IntPtr taskbarHandle, Win32Rect rect)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = taskbarHandle,
            uEdge = ABE_BOTTOM,
            rc = new RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom },
        };
        return SHAppBarMessage(ABM_SETPOS, ref data) != IntPtr.Zero;
    }

    /// <summary>读取任务栏当前的 AppBar 矩形。</summary>
    internal static Win32Rect ReadTaskbarRect(IntPtr taskbarHandle)
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = taskbarHandle,
            };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
            {
                var rect = new Win32Rect(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
                if (!rect.IsEmpty) return rect;
            }
        }
        catch
        {
            // 落到下面的窗口矩形兜底。
        }

        if (GetWindowRect(taskbarHandle, out var windowRect))
        {
            return new Win32Rect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
        }

        return default;
    }
}
