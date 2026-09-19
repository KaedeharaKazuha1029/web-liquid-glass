using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>替换任务栏的运行参数。</summary>
public sealed record TaskbarReplacementOptions
{
    /// <summary>是否把系统任务栏完全隐藏。</summary>
    public bool HideSystemTaskbar { get; init; } = true;

    /// <summary>
    /// 是否让工作区回到整屏（最大化窗口铺到屏幕底边，玻璃浮在窗口之上）。
    ///
    /// <para>实现手段是开启任务栏"自动隐藏"——这是<b>公开 API</b>
    /// （<c>ABM_SETSTATE</c>），语义也正是"任务栏不再占用工作区"。
    /// 实测这是三代系统上唯一可靠的办法：
    /// 单纯 <c>ShowWindow(SW_HIDE)</c> 不会释放 AppBar 预留，
    /// 而 <c>SPI_SETWORKAREA</c> 会被 explorer 立刻改回去。
    /// 详见 docs/PORTING-NOTES.md。</para>
    ///
    /// <para>关掉它则保留任务栏高度，最大化窗口停在玻璃上方，
    /// 但玻璃背后只剩壁纸，折射内容会非常贫乏。</para>
    /// </summary>
    public bool ReleaseWorkArea { get; init; } = true;

    /// <summary>模型（应用列表）的重扫间隔上限。WinEvent 会即时触发，这里只是安全网。</summary>
    public int SafetyRescanMs { get; init; } = 2500;

    /// <summary>WinEvent 触发后的合并窗口，避免应用启动时连续重扫十几次。</summary>
    public int EventCoalesceMs { get; init; } = 180;
}

/// <summary>
/// 替换任务栏的编排层。
///
/// <para>它负责"把系统任务栏换成我们的"这一整套动作，并保证<b>任何路径下都能还原</b>：</para>
/// <list type="number">
///   <item>记录自动隐藏的原始状态（用户可能本来就开着）；</item>
///   <item>开启自动隐藏 → 工作区变成整屏；</item>
///   <item>隐藏 <c>Shell_TrayWnd</c> 与 <c>Shell_SecondaryTrayWnd</c>；</item>
///   <item>创建并驱动替换任务栏；</item>
///   <item>退出 / 崩溃 / 用户按紧急热键时，逆序完整还原。</item>
/// </list>
///
/// <para><b>安全性</b>：所有改动都是<b>会话级</b>的，不写注册表、不改系统文件。
/// 即使进程被强杀，注销或重启一定恢复原状。</para>
/// </summary>
public sealed class TaskbarReplacementService : IDisposable
{
    private readonly ReplacementTaskbarSettings _settings;
    private readonly TaskbarReplacementOptions _options;
    private readonly Action<string> _log;
    private readonly TaskbarVisibilityController _visibility;
    // 我们自己的 AppBar 预留（"窗口不越过任务栏"模式）。
    // 非 readonly：只有在 StartCore 里确认要预留时才注册。
    private AppBarController? _appBar;

    private ReplacementTaskbar? _taskbar;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventCallback;
    private volatile bool _modelDirty = true;
    private long _lastRefreshTicks;
    private long _lastEventTicks;
    private bool _autoHideChanged;
    private int _originalAutoHideState = -1;
    private TaskbarReservationOverride? _reservationOverride;
    private bool _disposed;
    private bool _stopped;

    /// <summary>替换任务栏是否在运行。</summary>
    public bool IsRunning => _taskbar is not null;

    /// <summary>系统任务栏当前是否已被隐藏。</summary>
    public bool SystemTaskbarHidden => _visibility.IsHidden;

    /// <summary>工作区是否已释放为整屏。</summary>
    public bool WorkAreaReleased { get; private set; }

    /// <summary>
    /// 工作区是否已按任务栏高度**预留**（最大化窗口不会越过任务栏，
    /// 因此任务栏始终可见、且不遮挡任何窗口）。
    /// </summary>
    public bool WorkAreaReserved { get; private set; }

    /// <summary>当前面板矩形。</summary>
    public Win32Rect PanelBounds => _taskbar?.PanelBounds ?? default;

    /// <summary>任务栏上的应用数量。</summary>
    public int AppCount => _taskbar?.AppCount ?? 0;

    /// <summary>最近一次玻璃渲染统计。</summary>
    public GlassRenderStats Stats => _taskbar?.Stats ?? default;

    /// <summary>场景变化计数，用于验证实时渲染是否在工作。</summary>
    public int SceneChangeCount => _taskbar?.SceneChangeCount ?? 0;

    /// <summary>当前画质档位的可读描述。</summary>
    public string QualityDescription => _taskbar?.QualityDescription ?? "（未启动）";

    /// <summary>导出玻璃层副本，供离线取证。</summary>
    public BgraFrame? SnapshotGlassLayer() => _taskbar?.SnapshotGlassLayer();

    /// <summary>自研的液态玻璃开始菜单是否正开着。</summary>
    public bool StartMenuOpen => _taskbar?.StartMenuOpen ?? false;

    /// <summary>自研开始菜单的面板矩形（未打开时为空矩形）。</summary>
    public Win32Rect StartMenuBounds => _taskbar?.StartMenuBounds ?? default;

    /// <summary>切换自研开始菜单（自检与托盘用）。</summary>
    public void ToggleStartMenu() => _taskbar?.ToggleStartMenu();

    /// <summary>开始菜单的玻璃层副本（不含图标文字），供自检取证。</summary>
    public BgraFrame? SnapshotStartMenuGlass() => _taskbar?.SnapshotStartMenuGlass();

    /// <summary>开始菜单的最终合成帧副本（含图标文字），供自检取证。</summary>
    public BgraFrame? SnapshotStartMenuComposite() => _taskbar?.SnapshotStartMenuComposite();

    /// <summary>诊断：用指定雾化半径重渲染开始菜单玻璃层（量化重影用）。</summary>
    public BgraFrame? RenderStartMenuGlassWithBlurRadius(double radius)
        => _taskbar?.RenderStartMenuGlassWithBlurRadius(radius);

    /// <summary>用户点击了应用（用于日志与扩展）。</summary>
    public event Action<ShellApp>? AppInvoked;

    private TaskbarReplacementService(
        ReplacementTaskbarSettings settings, TaskbarReplacementOptions options, Action<string> log)
    {
        _settings = settings;
        _options = options;
        _log = log;
        _visibility = new TaskbarVisibilityController(log);
    }

    /// <summary>启动替换任务栏。失败时返回 null，并保证系统状态已还原。</summary>
    public static TaskbarReplacementService? Start(
        ReplacementTaskbarSettings settings, TaskbarReplacementOptions options, Action<string> log)
    {
        var service = new TaskbarReplacementService(settings, options, log);
        try
        {
            service.StartCore();
            return service;
        }
        catch (Exception ex)
        {
            // ⚠️ 只打 Message 是不够的：NullReferenceException 的 Message 永远是
            // "Object reference not set to an instance of an object."，
            // 完全看不出是哪个字段是 null、在哪一行。必须把堆栈一起打出来，
            // 否则这个失败在现场是完全不可诊断的。
            log($"替换任务栏启动失败：{ex.Message}");
            log($"  异常类型：{ex.GetType().FullName}");
            if (ex.StackTrace is { Length: > 0 } trace)
            {
                foreach (var line in trace.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length > 0) log($"    {t}");
                }
            }
            if (ex.InnerException is { } inner)
            {
                log($"  内层异常：{inner.GetType().Name}: {inner.Message}");
            }
            service.Dispose();
            return null;
        }
    }

    private void StartCore()
    {
        _log("════ 启动液态玻璃替换任务栏 ════");

        // ---- ⓪ 先处理上次崩溃可能留下的烂摊子 ----
        SessionStateGuard.RecoverIfNeeded(_log);

        // ---- ① 释放工作区 ----
        //
        //  ⚠️ 这里必须"先做、后量、再决定"，不能只看 API 返回值。
        //  实测（Windows 11 Build 26200）：
        //    · 只 SW_HIDE 隐藏任务栏        → 工作区不释放
        //    · 代任务栏提交 ABM_SETPOS 收缩   → 返回值是成功，但工作区**依然不释放**（假成功！）
        //    · SPI_SETWORKAREA 直接改         → 被 explorer 立刻改回去
        //    · ABM_SETSTATE 开启自动隐藏      → ✅ 工作区真的回到整屏
        //
        //  所以策略是：先试"不改用户设置"的那条路，然后**实测工作区**，
        //  没释放就自动升级到自动隐藏。自动隐藏的原始状态会记进状态文件，
        //  退出时精确还原。
        //  两种互斥模式（由 ReplacementTaskbarSettings.ReserveWorkAreaHeight 决定）：
        //    A. 预留（> 0）：把底部 N px 从工作区里挖掉 → 窗口不越过任务栏，
        //       任务栏永远可见、也不遮挡任何窗口。由本程序自己的 AppBar 实现。
        //    B. 释放（= 0）：工作区回到整屏 → 窗口延伸到底，玻璃能折射窗口内容；
        //       代价是任务栏在窗口之下，被最大化窗口盖住时就看不见了。
        var reserving = _settings.ReserveWorkAreaHeight > 0;

        if (!reserving && _options.ReleaseWorkArea)
        {
            var trays = TaskbarVisibilityController.FindTaskbarWindows();
            if (trays.Count > 0)
            {
                _reservationOverride = TaskbarReservationOverride.Apply(trays[0], _log);
                _log("先尝试压低任务栏的 AppBar 预留（不改动用户设置）…");
            }
        }
        else if (reserving)
        {
            _log($"工作区预留模式：底部保留 {_settings.ReserveWorkAreaHeight}px，"
                + "最大化窗口不会越过任务栏（任务栏始终可见）。");
        }

        // ---- ② 隐藏系统任务栏 ----
        if (_options.HideSystemTaskbar)
        {
            if (!_visibility.Hide())
            {
                _log("⚠ 未能隐藏系统任务栏。替换任务栏仍会显示，但会和系统任务栏叠在一起。");
            }
        }

        // ---- ②″ 预留工作区（"窗口不越过任务栏"模式）----
        if (reserving)
        {
            try
            {
                // 传 IntPtr.Zero → 内部自建隐藏消息窗口来接收 AppBar 回调，
                // 因此不依赖替换任务栏窗口的句柄，注册时机不受建窗顺序限制。
                _appBar = AppBarController.Reserve(IntPtr.Zero, _settings.ReserveWorkAreaHeight, _log);
                Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                _log($"⚠ AppBar 预留失败：{ex.Message}（任务栏仍会显示，但最大化窗口可能盖住它）。");
                _appBar = null;
            }
        }

        // ---- ②′ 实测工作区，必要时升级到自动隐藏 ----
        if (!reserving && _options.ReleaseWorkArea)
        {
            Thread.Sleep(600);
            var workMonitor = _settings.Monitor != IntPtr.Zero
                ? _settings.Monitor
                : DisplayEnvironment.GetPrimaryMonitor();
            var bounds = DisplayEnvironment.GetMonitorBounds(workMonitor);
            var work = DisplayEnvironment.GetMonitorWorkArea(workMonitor);

            if (work.Bottom >= bounds.Bottom - 2)
            {
                _log($"✓ 工作区已释放为整屏（{work}）。");
            }
            else
            {
                if (_reservationOverride is not null)
                {
                    _log($"✗ 压低预留未能释放工作区（仍为 {work}），"
                        + "回滚并改用任务栏自动隐藏。");
                    _reservationOverride.Dispose();
                    _reservationOverride = null;
                }

                if (TaskbarVisibilityController.TrySetAutoHide(true, out var previous))
                {
                    _originalAutoHideState = previous;
                    _autoHideChanged = true;
                    _log($"已开启任务栏自动隐藏（原状态 {TaskbarVisibilityController.DescribeAutoHideState(previous)}）。");
                    Thread.Sleep(700);
                    work = DisplayEnvironment.GetMonitorWorkArea(workMonitor);
                    _log(work.Bottom >= bounds.Bottom - 2
                        ? $"✓ 工作区已释放为整屏（{work}）。"
                        : $"⚠ 工作区仍未释放（{work}），最大化窗口会停在玻璃上方。");
                }
                else
                {
                    _log("⚠ 自动隐藏也失败了，最大化窗口会停在玻璃上方。");
                }
            }
        }

        // ---- ③ 建替换任务栏 ----
        _taskbar = ReplacementTaskbar.Create(_settings, _log);
        if (_taskbar is null)
        {
            throw new InvalidOperationException("替换任务栏创建失败。");
        }

        _taskbar.AppInvoked += app => AppInvoked?.Invoke(app);

        // ---- ④ 挂 WinEvent 钩子，让应用开关立刻反映到任务栏 ----
        InstallWinEventHook();
        InstallKeyboardHook();   // Win 键 → 开始菜单

        // ---- ⑤ 把"改动前是什么样"落盘，供崩溃后恢复 ----
        SessionStateGuard.Save(new SessionStateGuard.SessionState
        {
            OriginalAutoHideState = _originalAutoHideState,
            AutoHideChanged = _autoHideChanged,
            OriginalTaskbarRect = _reservationOverride is { IsApplied: true, OriginalRect: var original }
                ? [original.Left, original.Top, original.Right, original.Bottom]
                : null,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ProcessId = Environment.ProcessId,
        });

        _modelDirty = true;
        Tick(force: true);

        var monitor = _settings.Monitor != IntPtr.Zero
            ? _settings.Monitor
            : DisplayEnvironment.GetPrimaryMonitor();
        var workArea = DisplayEnvironment.GetMonitorWorkArea(monitor);
        var monitorBounds = DisplayEnvironment.GetMonitorBounds(monitor);
        WorkAreaReleased = workArea.Bottom >= monitorBounds.Bottom - 2;

        if (reserving)
        {
            // 预留生效的判据：工作区底边被抬高到"屏幕底边 − 预留高度"附近。
            var expected = monitorBounds.Bottom - _settings.ReserveWorkAreaHeight;
            WorkAreaReserved = workArea.Bottom <= expected + 4 && workArea.Bottom >= expected - 8;
            _log(WorkAreaReserved
                ? $"✓ 工作区已预留 {_settings.ReserveWorkAreaHeight}px（{workArea}）"
                  + " —— 窗口不会越过任务栏。"
                : $"⚠ 工作区预留未按预期生效（{workArea}，期望底边 ≈ {expected}），"
                  + "最大化窗口仍可能盖住任务栏。");
        }
        else
        {
            _log($"工作区：{workArea}（{(WorkAreaReleased ? "已释放为整屏 ✓" : "仍保留任务栏高度")}）");
        }

        _log($"替换任务栏：{PanelBounds}，{AppCount} 个应用。");
        _log("════ 启动完成 ════");
    }

    /// <summary>
    /// 每轮渲染周期调用。
    /// </summary>
    /// <param name="force">强制重扫模型（配置变更、用户手动刷新）。</param>
    public void Tick(bool force = false)
    {
        if (_disposed) return;
        if (_options.HideSystemTaskbar) _visibility.Tick();

        var now = Environment.TickCount64;

        // 事件驱动的重扫：WinEvent 一到就置脏，合并 180ms 内的连续事件。
        var shouldRefresh = force;
        if (!shouldRefresh && _modelDirty && now - _lastEventTicks >= _options.EventCoalesceMs)
        {
            shouldRefresh = true;
        }
        // 安全网：即使一个事件都没收到，也定期重扫一次。
        if (!shouldRefresh && now - _lastRefreshTicks >= _options.SafetyRescanMs)
        {
            shouldRefresh = true;
        }

        if (shouldRefresh)
        {
            _modelDirty = false;
            _lastRefreshTicks = now;
        }

        _taskbar?.Tick(shouldRefresh);
    }

    /// <summary>立刻重扫一次应用列表。</summary>
    public void RefreshNow() => Tick(force: true);

    // -------------------------------------------------------- WinEvent 钩子

    private void InstallWinEventHook()
    {
        _winEventCallback = OnWinEvent;

        // 覆盖"窗口被创建 / 销毁 / 显示 / 隐藏 / 改名"五类事件。
        // 它们合起来覆盖了用户能观察到的全部任务栏变化：
        // 开新应用、关窗口、最小化、切换前台、文档标题变化。
        _winEventHook = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero,
            _winEventCallback,
            0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_winEventHook == IntPtr.Zero)
        {
            _log("⚠ WinEvent 钩子安装失败，将只靠定时重扫同步（略有延迟）。");
        }
        else
        {
            _log("已安装窗口事件钩子：应用开关会即时反映到任务栏。");
        }
    }

    // -------------------------------------------------------- Win 键 → 开始菜单

    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _keyboardCallback;

    /// <summary>Win 键当前是否按下（逻辑状态）。</summary>
    private bool _winDown;

    /// <summary>
    /// 放行合成按键的开关（由 <see cref="ShellActions.AllowSyntheticKeys"/> 驱动）。
    /// 为 true 时键盘钩子直接放行、不做任何接管 —— 用于我们自己补发按键的那一瞬间。
    /// </summary>
    private volatile bool _allowSyntheticKeys;

    /// <summary>本次按住 Win 期间是否又按了别的键（即 Win+X 组合）。</summary>
    private bool _winChord;

    /// <summary>我们是否已经把 Win 键补发回系统（组合键时需要）。</summary>
    private bool _winReinjected;

    /// <summary>
    /// 装低级键盘钩子，把「单独按一下 Win 键」接到开始菜单上。
    ///
    /// <para><b>为什么不能用 RegisterHotKey</b>：它要求至少一个修饰键，
    /// <b>不接受"只有 Win 键"</b>这种组合。</para>
    ///
    /// <para><b>为什么必须吞掉 Win 键</b>：不吞的话，系统自己的开始菜单也会弹出来，
    /// 变成两个菜单同时开。但吞掉又会破坏 Win+E / Win+R 这些组合键，
    /// 所以做法是：先吞，一旦发现按住 Win 期间又按了别的键（组合键），
    /// 就把 Win 键<b>补发</b>给系统，让组合键照常工作。</para>
    /// </summary>
    private void InstallKeyboardHook()
    {
        _keyboardCallback = OnKeyboard;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule;

        _keyboardHook = SetWindowsHookEx(
            WH_KEYBOARD_LL, _keyboardCallback,
            GetModuleHandle(module?.ModuleName), 0);

        if (_keyboardHook == IntPtr.Zero)
        {
            _log($"⚠ Win 键接管失败（错误码 {Marshal.GetLastWin32Error()}）："
                + "「开始」按钮仍然可用，但按 Win 键不会打开本菜单。");
        }
        else
        {
            _log("Win 键已接管：单独按一下 Win 打开本菜单；Win+X 组合键照常工作。");
        }

        // 把"放行合成按键"的开关交给 ShellActions。
        // 没有这一步的话，我们自己合成的 Win 键会被自己的钩子吃掉，
        // 托盘 / 菜单里那些"发个组合键"的动作就全部变成点了没反应。
        ShellActions.AllowSyntheticKeys = allow =>
        {
            _allowSyntheticKeys = allow;
            if (!allow)
            {
                // 关闭放行窗口时顺手把状态复位，避免残留的 _winDown 影响下一次真实按键。
                _winDown = false;
                _winChord = false;
                _winReinjected = false;
            }
        };
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _disposed) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // ⚠️ 放行**自己合成的**按键。两种判据都要认：
        //
        //  · LLKHF_INJECTED：由 SendInput / keybd_event 注入的按键。
        //    这是权威判据 —— 我们要做的"补发 Win 键让 Win+E 生效"就是靠它识别，
        //    否则补发的 Win 会被自己再次吞掉，组合键永远修不好。
        //
        //  · _allowSyntheticKeys：由 ShellActions 显式打开的放行窗口，
        //    用于"我们主动发起一个组合键"的场景。
        if ((info.flags & LLKHF_INJECTED) != 0 || _allowSyntheticKeys)
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isUp = message is WM_KEYUP or WM_SYSKEYUP;

        var isWin = info.vkCode is VK_LWIN or VK_RWIN;

        if (isWin)
        {
            if (isDown)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _winChord = false;
                    _winReinjected = false;
                }
                return 1;   // 吞掉，避免系统开始菜单也弹出来
            }

            if (isUp)
            {
                var chord = _winChord;
                var reinjected = _winReinjected;

                _winDown = false;
                _winChord = false;
                _winReinjected = false;

                if (reinjected) SendWinKey(up: true);   // 补发过按下，就得补发抬起
                else if (!chord) _taskbar?.ToggleStartMenu();   // 单独按 Win → 我们的菜单

                return 1;
            }
        }
        else if (_winDown && isDown && !_winChord)
        {
            // 检测到 Win+X：把之前被吞掉的 Win 键补发给系统，组合键才能生效。
            _winChord = true;
            _winReinjected = true;
            SendWinKey(up: false);
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 把 Win 键的按下/抬起补发给系统（用于让 Win+E / Win+R 这类组合键继续工作）。
    ///
    /// <para><b>为什么这里只需要置一个标志</b>：合成的按键会被回调到我们自己的
    /// <see cref="OnKeyboard"/>，而钩子能通过 <c>LLKHF_INJECTED</c> 辨认出它是合成的 ——
    /// 见 <see cref="OnKeyboard"/> 开头的判断。所以不需要"等它回来"，
    /// 也不存在时序竞态（<c>keybd_event</c> 是异步投递的，
    /// 用"置位→发送→复位"的窗口写法反而会因窗口过早关闭而失效）。</para>
    /// </summary>
    private static void SendWinKey(bool up)
    {
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event((byte)VK_LWIN, 0, up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;

        // 只关心顶层窗口（OBJID_WINDOW = 0）。
        // 子控件的创建/销毁事件数量极大，不筛掉会把 CPU 吃满。
        if (idObject != 0) return;

        _modelDirty = true;
        _lastEventTicks = Environment.TickCount64;
    }

    // -------------------------------------------------------------- 还原

    /// <summary>
    /// 完整还原系统状态。<b>可以安全地重复调用。</b>
    /// 顺序与启动时相反：先撤替换任务栏，再恢复系统任务栏，最后还回工作区。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用独立的 <c>_stopped</c> 而不是 <c>_disposed</c> 做幂等判断。
    /// 早先版本用 <c>_disposed</c>，而 <see cref="Dispose"/> 是先置位再调用本方法 ——
    /// 结果是整个还原逻辑变成死代码，用户的任务栏被永久留在异常状态。
    /// 这是本项目踩过的最严重的一个坑，回归测试与状态文件都是为了盯住它。
    /// </remarks>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _taskbar?.Dispose();
        _taskbar = null;

        if (_options.HideSystemTaskbar)
        {
            _visibility.Restore();
        }

        // 先交还我们自己的 AppBar 预留（"窗口不越过任务栏"模式）。
        if (_appBar is not null)
        {
            _appBar.Dispose();
            _appBar = null;
            _log("已交还工作区预留。");
        }

        // 还原顺序与启动相反：先还回 AppBar 预留，再还回自动隐藏状态。
        if (_reservationOverride is not null)
        {
            _reservationOverride.Dispose();
            _reservationOverride = null;
        }

        if (_autoHideChanged)
        {
            TaskbarVisibilityController.RestoreAutoHide(_originalAutoHideState);
            _autoHideChanged = false;
            _log($"已还原任务栏自动隐藏状态（{TaskbarVisibilityController.DescribeAutoHideState(_originalAutoHideState)}）。");
        }

        // 推一把任务栏，让它回到自己的 AppBar 矩形。
        // 只还原预留矩形是不够的 —— explorer 会把任务栏窗口挪到贴近屏幕边缘的隐藏位，
        // 需要显式 SetWindowPos 才能把它拽回来。
        foreach (var hwnd in TaskbarVisibilityController.FindTaskbarWindows())
        {
            SessionStateGuard.NudgeTaskbarIntoPlace(hwnd);
        }

        SessionStateGuard.Clear();
        WorkAreaReleased = false;
        WorkAreaReserved = false;
        _log("替换任务栏已停止，系统状态已还原。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _visibility.Dispose();
        _appBar?.Dispose();
        TextRasterizer.ClearFontCache();
    }
}
