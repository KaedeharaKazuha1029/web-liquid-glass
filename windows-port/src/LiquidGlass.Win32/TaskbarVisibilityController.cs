using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 系统任务栏的隐藏与还原。
///
/// 这是 v2 架构的基石：<b>把系统任务栏整个藏起来</b>，
/// 于是"玻璃要折射的内容"从"壁纸最下面一条"变成了"整块屏幕"，
/// 同时也彻底摆脱了对 <c>SetWindowCompositionAttribute</c> 的依赖
/// （那个入口 Windows 7 没有，这是 v1 无法适配 Win7 的根本原因）。
///
/// <para><b>安全性设计</b>：隐藏任务栏会让用户失去开始按钮、系统托盘与时钟，
/// 一旦程序崩溃而没还原，用户会以为系统坏了。因此这里做了三重保险：</para>
/// <list type="number">
///   <item>进程退出 / 未处理异常时无条件还原（见 <see cref="Restore"/> 与 App 层钩子）；</item>
///   <item><see cref="Tick"/> 看门狗：explorer 在某些操作（改设置、重启 shell）后
///         会把任务栏重新显示出来，这里会再次藏掉；</item>
///   <item>全局紧急热键（App 层实现）：一键还原并退出接管。</item>
/// </list>
///
/// 另外必须说明：任务栏可见性是<b>会话级</b>的，不写注册表、不改系统文件。
/// 即使最坏情况下程序被强杀，注销或重启一定恢复。
/// </summary>
public sealed class TaskbarVisibilityController : IDisposable
{
    private readonly Action<string> _log;
    private readonly HashSet<IntPtr> _targets = [];
    private bool _hidden;
    private bool _disposed;

    /// <summary>任务栏主窗口类名（Win7 起一直没变）。</summary>
    private const string PrimaryTrayClass = "Shell_TrayWnd";

    /// <summary>副显示器任务栏类名（Win8 起）。</summary>
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    public TaskbarVisibilityController(Action<string> log) => _log = log;

    /// <summary>当前是否处于"已隐藏"状态。</summary>
    public bool IsHidden => _hidden;

    /// <summary>本次会话里被我们藏起来的任务栏窗口。</summary>
    public IReadOnlyCollection<IntPtr> HiddenHandles => _targets;

    /// <summary>
    /// 枚举当前存在的所有系统任务栏窗口（主 + 副）。
    /// 副显示器任务栏可能有多条，且只在多显示器时存在。
    /// </summary>
    public static IReadOnlyList<IntPtr> FindTaskbarWindows()
    {
        var result = new List<IntPtr>();

        var primary = FindWindow(PrimaryTrayClass, null);
        if (primary != IntPtr.Zero) result.Add(primary);

        if (OsCapabilities.IsAtLeastWindows8)
        {
            EnumWindows((hwnd, _) =>
            {
                var cls = SystemSurfaceLocator.GetClassNameOf(hwnd);
                if (cls == SecondaryTrayClass && !result.Contains(hwnd)) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
        }

        return result;
    }

    /// <summary>
    /// 隐藏系统任务栏。<paramref name="handle"/> 为空时自动枚举。
    /// 返回是否至少成功隐藏了一个窗口。
    /// </summary>
    public bool Hide(IReadOnlyList<IntPtr>? handles = null)
    {
        if (_disposed) return false;

        var targets = handles ?? FindTaskbarWindows();
        if (targets.Count == 0)
        {
            _log("没有找到系统任务栏窗口，无法隐藏。");
            return false;
        }

        var hiddenCount = 0;
        foreach (var hwnd in targets)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) continue;

            _targets.Add(hwnd);

            // SW_HIDE(0) 直接隐藏。不用 SW_MINIMIZE / 移出屏幕，
            // 因为那两种都会让窗口仍然参与命中测试与 Alt+Tab。
            if (ShowWindow(hwnd, SW_HIDE)) hiddenCount++;
            else hiddenCount++;   // ShowWindow 的返回值是"之前是否可见"，不是成功与否

            _log($"已隐藏系统任务栏 0x{hwnd.ToInt64():X8}"
                + $"（{SystemSurfaceLocator.GetClassNameOf(hwnd)}）。");
        }

        _hidden = hiddenCount > 0;
        return _hidden;
    }

    /// <summary>把系统任务栏还原成可见。可重复调用。</summary>
    public void Restore()
    {
        var restored = 0;

        // 重新枚举一次，兜住"隐藏之后 explorer 重建了任务栏"的情况。
        foreach (var hwnd in _targets.Concat(FindTaskbarWindows()).Distinct())
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) continue;

            // SW_SHOWNA(8)：显示但不激活。用 SW_SHOW 会把焦点抢到任务栏上，
            // 用户正在打字的窗口会失焦——这是很讨厌的副作用。
            if (ShowWindow(hwnd, SW_SHOWNA)) restored++;

            // 任务栏有时以"最小化"状态被还原，补一次恢复。
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        }

        _targets.Clear();
        _hidden = false;

        if (restored > 0) _log($"已还原系统任务栏（{restored} 个窗口）。");
    }

    /// <summary>
    /// 看门狗。每轮渲染周期调用一次。
    /// explorer 会在修改任务栏设置、重启 shell、切换显示器等场景下
    /// 把任务栏重新显示出来，这里负责再藏回去。
    /// </summary>
    /// <returns>本轮是否执行了重新隐藏。</returns>
    public bool Tick()
    {
        if (_disposed || !_hidden) return false;

        // 目标窗口被销毁（explorer 重启）→ 重新枚举一次。
        if (_targets.All(h => !IsWindow(h)))
        {
            _log("检测到 explorer 可能已重启，重新枚举任务栏窗口。");
            _targets.Clear();
            _hidden = false;
            return Hide();
        }

        var reHidden = false;

        foreach (var hwnd in _targets.ToArray())
        {
            if (!IsWindow(hwnd)) continue;

            // 被 explorer 重新显示出来了 → 再藏一次。
            if (IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SW_HIDE);
                reHidden = true;
            }
        }

        // 新出现的任务栏（比如刚接了第二块显示器）也一并处理。
        foreach (var hwnd in FindTaskbarWindows())
        {
            if (_targets.Contains(hwnd)) continue;
            _targets.Add(hwnd);
            ShowWindow(hwnd, SW_HIDE);
            _log($"发现新的任务栏窗口 0x{hwnd.ToInt64():X8}，已隐藏。");
            reHidden = true;
        }

        return reHidden;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// 读取主显示器的当前工作区。
    /// </summary>
    public static Win32Rect GetPrimaryWorkArea()
    {
        var rect = default(RECT);
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0))
        {
            return new Win32Rect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        return default;
    }

    // ==============================================================
    //  释放任务栏占用的工作区
    // ==============================================================
    //
    //  ⚠️ 核心问题：把 Shell_TrayWnd 用 ShowWindow(SW_HIDE) 藏起来，
    //  Shell **不会**因此释放它在 AppBar 里预留的那 72px。
    //  结果是屏幕最底部留一条死区，最大化窗口停在玻璃上方，
    //  玻璃背后只剩壁纸 —— 折射内容贫乏。
    //
    //  下面两种手段都能真正让工作区回到整屏，实测对比见 Probe 的 --test-hide。
    //  ------------------------------------------------------------------

    /// <summary>
    /// 开关任务栏的"自动隐藏"。
    ///
    /// <para>这是最干净的一条路：自动隐藏是<b>受支持的公开行为</b>，
    /// 一旦开启，Shell 就会把工作区还给全屏（这正是"自动隐藏任务栏"的语义）。
    /// 我们再把任务栏窗口 SW_HIDE 掉，它就永远不会因为鼠标碰到底边而冒出来。</para>
    ///
    /// <para>返回值：<c>previousState</c> 是修改前 <c>ABM_GETSTATE</c> 的原始值，
    /// 退出时必须用它精确还原——不能简单地设回"不自动隐藏"，
    /// 因为用户原本可能就是开着的。</para>
    /// </summary>
    public static bool TrySetAutoHide(bool enable, out int previousState)
    {
        previousState = -1;

        var probe = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        var state = (int)SHAppBarMessage(ABM_GETSTATE, ref probe);
        if (state < 0) return false;
        previousState = state;

        var desired = enable
            ? (state | AbsAutoHide)
            : (state & ~AbsAutoHide);

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            lParam = new IntPtr(desired),
        };
        SHAppBarMessage(ABM_SETSTATE, ref data);

        return true;
    }

    /// <summary>还原 <see cref="TrySetAutoHide"/> 保存的原始状态。</summary>
    public static void RestoreAutoHide(int previousState)
    {
        if (previousState < 0) return;
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            lParam = new IntPtr(previousState),
        };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    /// <summary>
    /// 直接把主显示器的工作区设成指定矩形。
    ///
    /// <para>这是最后手段：<c>SPI_SETWORKAREA</c> 是给老式全屏程序准备的接口，
    /// 简单粗暴但对多显示器无能为力（它只作用于主显示器），
    /// 而且 explorer 在某些操作后会把它重置回去，所以必须配合周期性的看门狗。</para>
    /// </summary>
    public static bool TrySetWorkArea(Win32Rect rect)
    {
        if (rect.IsEmpty) return false;
        var native = new RECT
        {
            Left = rect.Left,
            Top = rect.Top,
            Right = rect.Right,
            Bottom = rect.Bottom,
        };
        // SPIF_SENDCHANGE(0x02)：立刻广播 WM_SETTINGCHANGE，
        // 让正在运行的程序（含最大化窗口）马上重算布局。
        return SystemParametersInfo(SPI_SETWORKAREA, 0, ref native, SpifSendChange);
    }

    /// <summary>ABS_AUTOHIDE 位。</summary>
    private const int AbsAutoHide = 0x1;

    /// <summary>ABS_ALWAYSONTOP 位。</summary>
    private const int AbsAlwaysOnTop = 0x2;

    private const uint SpifSendChange = 0x02;

    /// <summary>把自动隐藏状态翻译成可读文本（诊断用）。</summary>
    public static string DescribeAutoHideState(int state)
    {
        if (state < 0) return "读取失败";
        var autoHide = (state & AbsAutoHide) != 0;
        var onTop = (state & AbsAlwaysOnTop) != 0;
        return $"autohide={(autoHide ? "开" : "关")} alwaysOnTop={(onTop ? "开" : "关")} (0x{state:X})";
    }
}

/// <summary>
/// 临时压低任务栏在 AppBar 里的预留高度，让工作区回到整屏——<b>而且不改动用户的任何设置</b>。
///
/// <para>这是比"开启自动隐藏"更干净的一条路：它不碰 <c>ABM_SETSTATE</c>，
/// 因此不会在「设置 → 个性化 → 任务栏」里留下任何可见痕迹，
/// 退出时只要把原来的矩形提交回去即可。</para>
///
/// <para>做法是直接以任务栏窗口的身份调用 <c>ABM_SETPOS</c>：
/// 告诉 Shell"这个 AppBar 现在只占屏幕底部 1px"。
/// Shell 随即重算工作区，最大化窗口铺到屏幕底边，玻璃浮在它之上。</para>
/// </summary>
public sealed class TaskbarReservationOverride : IDisposable
{
    private readonly Action<string> _log;
    private readonly IntPtr _taskbarHandle;
    private readonly Win32Rect _originalRect;
    private bool _applied;
    private bool _disposed;

    /// <summary>是否已成功压低预留。</summary>
    public bool IsApplied => _applied;

    /// <summary>任务栏原本的 AppBar 矩形。</summary>
    public Win32Rect OriginalRect => _originalRect;

    private TaskbarReservationOverride(IntPtr taskbarHandle, Win32Rect originalRect, Action<string> log)
    {
        _taskbarHandle = taskbarHandle;
        _originalRect = originalRect;
        _log = log;
    }

    /// <summary>
    /// 压低预留。<paramref name="taskbarHandle"/> 传主任务栏窗口句柄。
    /// 失败时返回的对象 <see cref="IsApplied"/> 为 false，调用方应改用自动隐藏方案。
    /// </summary>
    public static TaskbarReservationOverride? Apply(IntPtr taskbarHandle, Action<string> log)
    {
        if (taskbarHandle == IntPtr.Zero || !NativeMethods.IsWindow(taskbarHandle)) return null;

        var original = ReadTaskbarRect(taskbarHandle);
        var instance = new TaskbarReservationOverride(taskbarHandle, original, log);
        instance.Collapse();
        return instance;
    }

    private void Collapse()
    {
        var monitor = NativeMethods.MonitorFromWindow(_taskbarHandle, MONITOR_DEFAULTTONEAREST);
        var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
        if (bounds.IsEmpty) return;

        // 1px 而不是 0：完全退化的矩形会被 Shell 忽略，1px 视觉上不可见但足以触发重算。
        var collapsed = new Win32Rect(bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom);

        if (Submit(collapsed))
        {
            _applied = true;
            _log($"已压低任务栏的 AppBar 预留：{_originalRect} → {collapsed}（工作区应回到整屏）。");
        }
        else
        {
            _log("压低任务栏 AppBar 预留失败，将退回自动隐藏方案。");
        }
    }

    /// <summary>把预留还原成任务栏原本的矩形。</summary>
    public void Restore()
    {
        if (_disposed || !_applied) return;

        if (_originalRect.IsEmpty)
        {
            // 读不到原矩形（Win7 上偶发）时，用"屏幕底部一条标准任务栏高度"兜底，
            // 这比什么都不做要好得多 —— 什么都不做的话用户的工作区会永久变大。
            var monitor = NativeMethods.MonitorFromWindow(_taskbarHandle, MONITOR_DEFAULTTONEAREST);
            var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
            var fallbackHeight = 48;
            var fallback = new Win32Rect(bounds.Left, bounds.Bottom - fallbackHeight, bounds.Right, bounds.Bottom);
            _log($"未能读到任务栏原矩形，使用兜底矩形 {fallback} 还原。");
            Submit(fallback);
        }
        else
        {
            Submit(_originalRect);
        }

        // ⚠️ 只还原预留矩形是不够的。explorer 在预留被压小之后会把任务栏窗口
        //    挪到贴近屏幕边缘的"隐藏位"（y 坐标超出屏幕底），
        //    单靠 ABM_SETPOS 不会把它拽回来 —— 必须显式 SetWindowPos，
        //    否则用户会看到任务栏依然处于半隐藏状态。
        SessionStateGuard.NudgeTaskbarIntoPlace(_taskbarHandle);

        _applied = false;
        _log("已还原任务栏的 AppBar 预留与窗口位置。");
    }

    private bool Submit(Win32Rect rect) =>
        SessionStateGuard.ApplyTaskbarRect(_taskbarHandle, rect);

    private static Win32Rect ReadTaskbarRect(IntPtr taskbarHandle) =>
        SessionStateGuard.ReadTaskbarRect(taskbarHandle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }
}

