using System.Diagnostics;
using System.Runtime.InteropServices;
using LiquidGlass.Win32;

namespace LiquidGlass.App;

/// <summary>当前引擎状态，供托盘菜单与日志展示。</summary>
public sealed record EngineStatus(
    bool Running,
    int AttachedCount,
    int PausedCount,
    double LastTickMs,
    IReadOnlyList<string> SurfaceLines);

/// <summary>
/// 主循环：发现系统界面 → 接管 → 持续跟踪几何与场景变化 → 退出时还原。
///
/// 设计要点：
/// <list type="bullet">
///   <item>用独立后台线程而不是 UI 计时器 —— 渲染是 CPU 密集型的，
///         放进 UI 线程会让托盘菜单卡顿。</item>
///   <item>每 tick 只做"廉价检查"，真正昂贵的 <c>Compose()</c> 在
///         累积收敛后被自动跳过。空闲时 CPU 占用接近 0。</item>
///   <item>窗口发现按秒级重扫而不是每 tick 重扫 ——
///         开始菜单/搜索这类界面只在被打开时才存在。</item>
/// </list>
/// </summary>
public sealed class GlassEngine : IDisposable
{
    private readonly Action<string> _log;
    private readonly List<GlassSurfaceHost> _hosts = [];
    private readonly object _sync = new();

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile string _pauseReason = "";
    private AppConfig _config;
    private double _lastTickMs;
    private long _tickCount;
    private TaskbarReplacementService? _replacement;

    /// <summary>当前是否为"完全隐藏系统任务栏 + 自建任务栏"模式。</summary>
    public bool IsReplacementMode => _config.IsReplacementMode;

    /// <summary>每 N 次 tick 重扫一次窗口列表（按 tick 间隔换算成约 1 秒）。</summary>
    private int RescanIntervalTicks => Math.Max(1, 1000 / Math.Max(8, _config.Performance.TickIntervalMs));

    public GlassEngine(AppConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public bool IsRunning => _running;

    /// <summary>热更新配置：先完整还原，再按新配置重建。</summary>
    public void ApplyConfig(AppConfig config)
    {
        var wasRunning = _running;
        if (wasRunning) Stop();
        _config = config;
        if (wasRunning) Start();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _paused = false;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LiquidGlass.RenderLoop",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();

        if (_config.IsReplacementMode)
        {
            _log("工作模式：replace（完全隐藏系统任务栏，自建液态玻璃任务栏）");
        }
        else
        {
            _log("工作模式：underlay（保留系统任务栏，玻璃垫在其下方）");
        }
    }

    /// <summary>
    /// 停止引擎并把系统组件还原为原生外观。
    /// 这一步极其重要：Accent 修改虽然是会话级的，但不还原会让任务栏
    /// 在程序退出后保持全透明，看起来像系统坏了。
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;

        // 替换任务栏要先撤 —— 它把系统任务栏藏起来了，必须在此之前还原。
        if (_replacement is not null)
        {
            _replacement.Dispose();
            _replacement = null;
        }

        lock (_sync)
        {
            foreach (var host in _hosts)
            {
                if (_config.Behavior.RestoreOnExit) host.RestoreTarget();
                host.Dispose();
            }
            _hosts.Clear();
        }

        _log("引擎已停止，系统组件已还原。");
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 主循环

    private void Loop()
    {
        // ================================================================
        //  ⚠️ 消息循环是必需的，不是可选项。
        //
        //  替换任务栏的窗口建在<b>本线程</b>上，而窗口过程只会在有人泵消息时执行。
        //  没有这一段的后果是：
        //    · 任务栏收不到任何鼠标消息 → 点图标、悬停高亮、右键菜单全部失效；
        //    · SetWinEventHook(WINEVENT_OUTOFCONTEXT) 的事件不会投递 →
        //      "应用开关实时同步"完全不工作（它靠的就是这个钩子）。
        //
        //  首次 PeekMessage 会顺便为线程创建消息队列，所以即使本轮没有消息，
        //  这一步也必须先执行一次。
        // ================================================================
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        var tick = Math.Clamp(_config.Performance.TickIntervalMs, 8, 500);

        while (_running)
        {
            // 非阻塞泵消息：把这一轮到达的鼠标 / WinEvent / 菜单消息全部派发掉。
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log($"渲染循环异常（已跳过本帧）：{ex.Message}");
            }

            _lastTickMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _tickCount++;

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var sleep = Math.Max(1, tick - elapsed);
            Thread.Sleep(sleep);
        }
    }

    private const uint PM_NOREMOVE = 0x0000;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    private void Tick()
    {
        // ---- 替换模式：整个任务栏由我们托管，v1 的"垫层"目标全部跳过 ----
        //  ⚠️ 这个分支必须放在最前面。曾经因为一次静默失败的文本替换，
        //  这个 if 从未被插入，结果是程序一边打印"工作模式：replace"，
        //  一边照着 v1 的 underlay 路径接管任务栏 —— 系统任务栏自然没被隐藏。
        if (_config.IsReplacementMode)
        {
            TickReplacement();
            return;
        }

        // ---- 全屏检测：前台窗口占满整个显示器时暂停 ----
        if (_config.Behavior.PauseWhenFullscreen)
        {
            var fullscreen = IsForegroundFullscreen();
            if (fullscreen != _paused)
            {
                _paused = fullscreen;
                _pauseReason = fullscreen ? "前台窗口全屏" : "";
                _log(fullscreen ? "检测到全屏前台窗口，渲染已暂停。" : "全屏结束，渲染已恢复。");
            }
        }

        if (_paused) return;

        // ---- 定期重扫：发现新出现的界面（开始菜单等），并清理已消失的 ----
        if (_tickCount % RescanIntervalTicks == 0)
        {
            DiscoverAndAttach();
        }

        // ---- 跟踪 + 合成 ----
        lock (_sync)
        {
            for (var i = _hosts.Count - 1; i >= 0; i--)
            {
                var host = _hosts[i];

                // 目标窗口被销毁（例如开始菜单进程重启）→ 拆掉，下次重扫时重建。
                if (!IsTargetAlive(host))
                {
                    _log($"[{host.Kind}] 目标窗口已消失，解除接管。");
                    host.Dispose();
                    _hosts.RemoveAt(i);
                    continue;
                }

                host.Sync();
                host.Compose();
            }
        }
    }

    /// <summary>
    /// 把配置里的 <c>taskbarReplacement.zOrder</c> 字符串解析成枚举。
    /// 无法识别时回落到默认的"桌面之上、窗口之下且始终可见"。
    /// </summary>
    private static TaskbarZOrder ParseZOrder(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "topmost" or "top" => TaskbarZOrder.TopMost,
            "behinddesktopicons" or "workerw" or "behind" => TaskbarZOrder.BehindDesktopIcons,
            "normal" => TaskbarZOrder.Normal,
            _ => TaskbarZOrder.DesktopBottom,
        };

    /// <summary>替换模式的一轮 tick。</summary>
    private void TickReplacement()
    {
        var config = _config.TaskbarReplacement;

        // 首次进入时惰性创建：这样配置文件写错了也不会让托盘起不来。
        if (_replacement is null)
        {
            if (!_config.Behavior.AttachOnStartup) return;

            var zOrder = ParseZOrder(config.ZOrder);

            // "桌面层 + 预留工作区"才需要预留：把整条玻璃占据的高度从工作区里挖掉，
            // 最大化窗口就会停在任务栏上方 —— 任务栏永远可见且不遮挡窗口。
            // 置顶模式不需要（它本来就浮在窗口之上），预留反而浪费屏幕。
            var reserveHeight = config.ReserveWorkArea && zOrder == TaskbarZOrder.DesktopBottom
                ? Math.Max(0, config.Height + config.BottomMargin)
                : 0;

            var settings = new ReplacementTaskbarSettings
            {
                ZOrder = zOrder,
                ReserveWorkAreaHeight = reserveHeight,
                CaptureMinIntervalMs = _config.Scene.CaptureMinIntervalMs,
                UseGlassStartMenu = _config.GlassStartMenu.Enabled,
                StartMenu = new GlassStartMenu.Settings
                {
                    Columns = _config.GlassStartMenu.Columns,
                    TileWidth = _config.GlassStartMenu.TileWidth,
                    TileHeight = _config.GlassStartMenu.TileHeight,
                    IconSize = _config.GlassStartMenu.IconSize,
                    LabelFontSize = _config.GlassStartMenu.LabelFontSize,
                    Padding = _config.GlassStartMenu.Padding,
                    CornerRadius = _config.GlassStartMenu.CornerRadius,
                    GapAboveTaskbar = _config.GlassStartMenu.GapAboveTaskbar,
                    SearchBoxHeight = _config.GlassStartMenu.SearchBoxHeight,
                    RecommendedMax = _config.GlassStartMenu.RecommendedMax,
                    RecommendedHeight = _config.GlassStartMenu.RecommendedHeight,
                    BottomBarHeight = _config.GlassStartMenu.BottomBarHeight,
                    AllAppsPerScreen = _config.GlassStartMenu.AllAppsPerScreen,
                    AllAppsColumns = _config.GlassStartMenu.AllAppsColumns,
                    AllAppsTileHeight = _config.GlassStartMenu.AllAppsTileHeight,
                    AllAppsIconSize = _config.GlassStartMenu.AllAppsIconSize,
                    WheelStepPx = _config.GlassStartMenu.WheelStepPx,
                    GlassScale = _config.GlassStartMenu.GlassScale,
                    FrostedBlurRadius = _config.GlassStartMenu.FrostedBlurRadius,
                    EdgeBandRatio = _config.GlassStartMenu.EdgeBandRatio,
                    RefractionVisibleRatio = _config.GlassStartMenu.RefractionVisibleRatio,
                    FrostedAttenuation = _config.GlassStartMenu.FrostedAttenuation,
                    DispersionStrength = _config.GlassStartMenu.DispersionStrength,
                    PathsPerPixel = _config.GlassStartMenu.PathsPerPixel,
                    MaxAccumulation = _config.GlassStartMenu.MaxAccumulation,
                    WarmupFrames = _config.GlassStartMenu.WarmupFrames,
                    ClickDebounceMs = _config.GlassStartMenu.ClickDebounceMs,
                    ScrimOpacity = _config.GlassStartMenu.ScrimOpacity,
                    ScrimIsLight = _config.GlassStartMenu.ScrimIsLight,
                    MouseLight = _config.GlassStartMenu.MouseLight,
                    Material = _config.Material.Apply(),
                },
                CapsuleEnabled = config.CapsuleEnabled,
                CapsuleWidthRatio = config.CapsuleWidthRatio,
                CapsuleHeightScale = config.CapsuleHeightScale,
                CapsulePathsPerPixel = config.CapsulePathsPerPixel,
                CapsuleMovingPathsPerPixel = config.CapsuleMovingPathsPerPixel,
                CapsuleAccumulation = config.CapsuleAccumulation,
                CapsuleFollow = config.CapsuleFollow,
                Height = config.Height,
                ItemWidth = config.ItemWidth,
                IconSize = config.IconSize,
                LabelFontSize = config.LabelFontSize,
                LabelMaxWidth = Math.Max(20, config.ItemWidth - 6),
                StartButtonWidth = config.StartButtonWidth,
                ClockWidth = config.ClockWidth,
                QuickSettingsWidth = config.QuickSettingsWidth,
                HorizontalPadding = config.HorizontalPadding,
                BottomMargin = config.BottomMargin,
                ShowStartButton = config.ShowStartButton,
                ShowClock = config.ShowClock,
                ShowQuickSettings = config.ShowQuickSettings,
                CenterHorizontally = config.CenterHorizontally,
                Material = _config.Material.Apply(),
                PathsPerPixel = _config.Performance.PathsPerPixel,
                IdleAccumulation = _config.Performance.IdleAccumulation,
                MovingAccumulation = _config.Performance.MovingAccumulation,
                MouseParallax = _config.Performance.MouseParallax,
                SceneMargin = _config.Scene.Margin,
                ExcludeFromCapture = _config.Scene.ExcludeFromCapture,
                TickIntervalMs = _config.Performance.TickIntervalMs,
                ForcedQualityTier = _config.ResolveForcedQualityTier(),
            };

            var options = new TaskbarReplacementOptions
            {
                HideSystemTaskbar = config.HideSystemTaskbar,
                // 预留与释放互斥：已按任务栏高度预留时不再释放工作区。
                ReleaseWorkArea = config.ReleaseWorkArea && reserveHeight == 0,
                SafetyRescanMs = config.SafetyRescanMs,
            };

            _replacement = TaskbarReplacementService.Start(settings, options, _log);
            if (_replacement is null)
            {
                _log("⚠ 替换任务栏启动失败，本程序将不再改动系统外观。");
            }
            return;
        }

        _replacement.Tick();
    }

    // ------------------------------------------------------------ 发现与接管

    /// <summary>重扫系统界面，接管尚未接管的、配置中启用目标。</summary>
    private void DiscoverAndAttach()
    {
        IReadOnlyList<SystemSurface> surfaces;
        try
        {
            surfaces = SystemSurfaceLocator.Discover();
        }
        catch (Exception ex)
        {
            _log($"窗口枚举失败：{ex.Message}");
            return;
        }

        lock (_sync)
        {
            foreach (var surface in surfaces)
            {
                var target = _config.For(surface.Kind);
                if (!target.Enabled) continue;

                // 已经接管过这个窗口句柄？
                if (_hosts.Any(h => h.TargetHandle == surface.Handle)) continue;

                // 同一类别只接管一个实例（副显示器任务栏除外，它可以有多条）。
                if (surface.Kind != SurfaceKind.SecondaryTaskbar
                    && _hosts.Any(h => h.Kind == surface.Kind))
                {
                    continue;
                }

                var settings = new GlassSurfaceSettings
                {
                    GlassInsetX = target.GlassInsetX,
                    GlassInsetY = target.GlassInsetY,
                    MakeTargetTransparent = target.MakeTargetTransparent,
                    SceneMargin = _config.Scene.Margin,
                    SceneSource = _config.Scene.ResolveSource(),
                    ExcludeOverlayFromCapture = _config.Scene.ExcludeFromCapture,
                    PathsPerPixel = _config.Performance.PathsPerPixel,
                    IdleAccumulation = _config.Performance.IdleAccumulation,
                    MovingAccumulation = _config.Performance.MovingAccumulation,
                    MouseParallax = _config.Performance.MouseParallax,
                    Material = _config.Material.Apply().With(b => b.CornerRadius = target.CornerRadius),
                };

                var host = GlassSurfaceHost.Attach(surface, settings, _log);
                if (host is not null) _hosts.Add(host);
            }
        }
    }

    /// <summary>立刻重新接管（配置变更或用户手动触发）。</summary>
    public void Reattach()
    {
        lock (_sync)
        {
            foreach (var host in _hosts)
            {
                if (_config.Behavior.RestoreOnExit) host.RestoreTarget();
                host.Dispose();
            }
            _hosts.Clear();
        }
        DiscoverAndAttach();
    }

    private static bool IsTargetAlive(GlassSurfaceHost host) =>
        host.TargetHandle != IntPtr.Zero && Win32Query.IsWindow(host.TargetHandle);

    /// <summary>
    /// 前台窗口是否占满其所在显示器。
    /// 命中时暂停渲染：玩游戏或看全屏视频时，玻璃层既看不见也没必要算。
    /// </summary>
    private static bool IsForegroundFullscreen()
    {
        var foreground = Win32Query.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        // 桌面或 Shell 自身不算全屏
        if (foreground == Win32Query.GetDesktopWindow()) return false;

        var bounds = SystemSurfaceLocator.GetVisualBounds(foreground);
        if (bounds.IsEmpty) return false;

        var monitor = Win32Query.MonitorFromWindow(foreground);
        var monitorBounds = Win32Query.GetMonitorBounds(monitor);
        if (monitorBounds is null) return false;

        var m = monitorBounds.Value;
        const int tolerance = 2;
        return bounds.X <= m.X + tolerance
            && bounds.Y <= m.Y + tolerance
            && bounds.Right >= m.Right - tolerance
            && bounds.Bottom >= m.Bottom - tolerance;
    }

    /// <summary>当前状态快照。</summary>
    public EngineStatus GetStatus()
    {
        // 替换模式的进度汇报
        if (_replacement is not null)
        {
            var stats = _replacement.Stats;
            var state = stats.UsedCache ? "已收敛" : $"累积 {stats.Frame}";
            var lines = new List<string>
            {
                $"替换任务栏 · {_replacement.PanelBounds} · {_replacement.AppCount} 个应用 · {state} · {stats.ElapsedMs:F1}ms",
                _replacement.SystemTaskbarHidden ? "系统任务栏：已隐藏" : "系统任务栏：可见",
                _replacement.WorkAreaReleased ? "工作区：已释放为整屏" : "工作区：保留任务栏高度",
            };
            return new EngineStatus(true, 1, 0, _lastTickMs, lines);
        }

        lock (_sync)
        {
            var lines = _hosts.Select(h =>
            {
                var stats = h.Stats;
                var state = !h.IsVisible ? "隐藏"
                    : stats.UsedCache ? "已收敛"
                    : $"累积 {stats.Frame}/{_config.Performance.IdleAccumulation}";
                return $"{h.Kind} · {h.GlassBounds} · {state} · {stats.ElapsedMs:F1}ms";
            }).ToList();

            if (lines.Count == 0) lines.Add("（当前没有接管任何界面）");

            return new EngineStatus(
                _running, _hosts.Count, _paused ? 1 : 0, _lastTickMs, lines);
        }
    }
}
