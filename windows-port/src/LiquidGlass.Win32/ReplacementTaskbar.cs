using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>替换任务栏上的元素类型。</summary>
public enum TaskbarItemKind
{
    Start,
    App,
    Clock,
    QuickSettings,

    /// <summary>托盘图标（网络 / 音量 / 电池 / 隐藏图标…）。字形放在 Label 里。</summary>
    TrayIcon,
}

/// <summary>一个已排好版的任务栏元素。</summary>
public sealed class TaskbarItem
{
    public required TaskbarItemKind Kind { get; init; }

    /// <summary>应用项才有。</summary>
    public ShellApp? App { get; init; }

    /// <summary>相对玻璃面板左上角的矩形（像素）。</summary>
    public PixelRect Rect { get; set; }

    /// <summary>要显示的文字（应用名 / 时间 / 日期）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>栅格化后的图标。Start / Clock / QuickSettings 为 null。</summary>
    public BgraFrame? Icon { get; set; }

    /// <summary>栅格化后的文字位图（白色 + 覆盖率 Alpha）。</summary>
    public TextRasterizer.RasterizedText? LabelBitmap { get; set; }

    /// <summary>
    /// 第二行小字（时钟项的日期行，画在 <see cref="LabelBitmap"/> 下方）。
    /// 与真实任务栏一致：时间在上、日期在下。
    /// </summary>
    public TextRasterizer.RasterizedText? SubLabelBitmap { get; set; }

    /// <summary>正在被鼠标悬停。</summary>
    public bool IsHovered { get; set; }
}

/// <summary>
/// 替换任务栏的 Z 序层级。
///
/// <para>用户需求是"桌面之上、窗口之下"，但"窗口之下"有两种截然不同的实现，
/// 视觉结果差别巨大 —— v2.2.0 曾因选错而让任务栏彻底看不见：</para>
/// </summary>
public enum TaskbarZOrder
{
    /// <summary>
    /// 桌面之上、所有普通窗口之下，且 <b>始终可见</b>（默认）。
    /// 做法：保持普通顶层窗口，仅把 Z 序沉到普通窗口带最底部。
    /// </summary>
    DesktopBottom = 0,

    /// <summary>
    /// 寄生到桌面壁纸所在的 <c>WorkerW</c>。层级在桌面图标/壁纸<b>之下</b>，
    /// 视觉上通常等同于"任务栏消失了"。仅适合"把自己当壁纸"的用途。
    /// </summary>
    BehindDesktopIcons = 1,

    /// <summary>始终置顶，会盖住所有普通窗口（v2.1 的行为）。</summary>
    TopMost = 2,

    /// <summary>停在普通窗口带里不动：既不下沉也不置顶，可能被后开的窗口盖住。</summary>
    Normal = 3,
}

/// <summary>替换任务栏的全部可调项。</summary>
public sealed record ReplacementTaskbarSettings
{
    // ---- 层级 ----
    /// <summary>
    /// Z 序层级。默认 <see cref="TaskbarZOrder.DesktopBottom"/>：
    /// 桌面之上、所有普通窗口之下，但**始终可见**。
    /// </summary>
    public TaskbarZOrder ZOrder { get; init; } = TaskbarZOrder.DesktopBottom;

    /// <summary>
    /// 在屏幕底部**预留工作区**的高度（像素）。<c>&gt; 0</c> 时本程序会注册一个
    /// AppBar 把底部这么高的一条从工作区里挖掉，于是**最大化窗口不会越过任务栏**：
    /// 任务栏永远可见，且不会遮挡任何窗口。
    ///
    /// <para>推荐值 = <see cref="Height"/> + <see cref="BottomMargin"/>（即整条玻璃占据的高度）。
    /// <c>0</c> 表示不预留 —— 工作区被释放为整屏，窗口延伸到玻璃下方
    /// （玻璃能折射窗口内容，但被最大化窗口盖住时任务栏就看不见了）。</para>
    /// </summary>
    public int ReserveWorkAreaHeight { get; init; }

    /// <summary>
    /// 抓屏的最小间隔（毫秒）。<c>0</c> = 自动：
    /// 预留工作区时 15 秒（背后只有壁纸、基本不动），否则 400 毫秒（背后一直在变）。
    ///
    /// <para>为什么这条值得单独可调：在不启用 `WDA_EXCLUDEFROMCAPTURE` 的前提下，
    /// 每次抓屏都要把任务栏<b>隐藏几毫秒</b>。频率越高，"用户截图正好拍到空档"的概率越大。
    /// 想彻底不闪就把 <c>scene.excludeFromCapture</c> 打开（代价：截图里也看不到玻璃）。</para>
    /// </summary>
    public int CaptureMinIntervalMs { get; init; }

    /// <summary>
    /// 是否用**自研的液态玻璃开始菜单**替换系统开始菜单。
    ///
    /// <para>系统开始菜单是 <c>StartMenuExperienceHost.exe</c> 的 XAML 窗口，
    /// 背景由 XAML 自绘，外部改不动（详见 <see cref="GlassStartMenu"/> 的说明）。
    /// 置 <c>false</c> 则「开始」按钮回退到 Win 键唤起系统菜单。</para>
    /// </summary>
    public bool UseGlassStartMenu { get; init; } = true;

    /// <summary>自研开始菜单的外观参数。</summary>
    public GlassStartMenu.Settings? StartMenu { get; init; }

    // ---- 悬停胶囊（上游的 hover capsule）----
    /// <summary>
    /// 是否启用**悬停胶囊**：一块跟着指针滑行的玻璃镜头。
    ///
    /// <para>这是上游最有辨识度的交互：宽度 = 导航的 1/5、高度 = 1.2×，
    /// 有自己的折射与独立累积预算。Windows 移植初期刻意跳过了它
    /// （理由见 <c>docs/PORTING-NOTES.md</c> §七），v2.4 起补上。</para>
    /// </summary>
    public bool CapsuleEnabled { get; init; } = true;

    /// <summary>胶囊宽度占导航宽度的比例。上游默认 <c>0.2</c>（即 1/5）。</summary>
    public double CapsuleWidthRatio { get; init; } = 0.2;

    /// <summary>胶囊高度相对导航高度的倍率。上游默认 <c>1.2</c>（上下各溢出 10%）。</summary>
    public double CapsuleHeightScale { get; init; } = 1.2;

    /// <summary>
    /// 胶囊的每像素路径数。刻意低于导航本体 —— 它是独立元素，
    /// 上游也给它独立（更小）的预算，否则移动时会吃掉整条任务栏的性能。
    /// </summary>
    public int CapsulePathsPerPixel { get; init; } = 2;

    /// <summary>
    /// 胶囊**移动时**的每像素路径数。
    ///
    /// <para>胶囊一动，渲染器的包围盒就变、时间累积被清空，那一帧只有一次采样。
    /// 传随机菲涅耳反射在这种密度下会变成肉眼可见的噪点（"花"）。
    /// 所以移动时临时抬到这里（默认 6）让单帧就够干净；停下后立刻回到
    /// <see cref="CapsulePathsPerPixel"/> 靠累积收敛，不白烧 CPU。</para>
    /// </summary>
    public int CapsuleMovingPathsPerPixel { get; init; } = 6;

    /// <summary>胶囊独立的时间累积帧上限（比导航短，移动后能更快收敛）。</summary>
    public int CapsuleAccumulation { get; init; } = 16;

    /// <summary>
    /// 胶囊跟随指针的平滑系数：每个 tick 把当前中心向目标推进这个比例。
    /// <c>1</c> = 瞬时贴合指针；<c>0.35</c> = 略带惯性的滑动（上游的"动画跟随"观感）。
    /// </summary>
    public double CapsuleFollow { get; init; } = 0.35;

    // ---- 尺寸（用户要求整体再大一倍：高度与宽度均翻倍）----
    /// <summary>玻璃胶囊高度（v2.2 起按用户要求整体翻倍）。</summary>
    public int Height { get; init; } = 116;

    /// <summary>每个应用占的宽度。</summary>
    public int ItemWidth { get; init; } = 120;

    /// <summary>开始按钮宽度。</summary>
    public int StartButtonWidth { get; init; } = 96;

    /// <summary>时钟区域宽度。</summary>
    public int ClockWidth { get; init; } = 148;

    /// <summary>快捷设置按钮宽度。</summary>
    public int QuickSettingsWidth { get; init; } = 80;

    /// <summary>胶囊左右内边距。</summary>
    public int HorizontalPadding { get; init; } = 20;

    /// <summary>胶囊距屏幕底边的距离。</summary>
    public int BottomMargin { get; init; } = 16;

    /// <summary>应用图标边长。</summary>
    public int IconSize { get; init; } = 52;

    /// <summary>图标下方文字的字号（像素）。</summary>
    public int LabelFontSize { get; init; } = 20;

    /// <summary>文字允许的最大绘制宽度，超出省略。</summary>
    public int LabelMaxWidth { get; init; } = 108;

    /// <summary>是否显示开始按钮。隐藏系统任务栏后它是唯一的开始入口，建议保持开启。</summary>
    public bool ShowStartButton { get; init; } = true;

    /// <summary>是否显示时钟。</summary>
    public bool ShowClock { get; init; } = true;

    /// <summary>是否显示快捷设置按钮（Win+A）。</summary>
    public bool ShowQuickSettings { get; init; } = true;

    /// <summary>
    /// 是否显示**托盘**（通知区域）。
    ///
    /// <para>⚠️ 说明：Windows 11 的托盘是 XAML 渲染的
    /// （实测 <c>TrayNotifyWnd</c> 下没有任何 <c>ToolbarWindow32</c>），
    /// 所以**第三方托盘图标（OneDrive 之类）拿不到** ——
    /// 经典那套"跨进程读工具栏按钮"在 Windows 11 上不成立。
    /// 这里给出的是系统托盘的标准几项：隐藏图标 / 网络 / 音量 / 电池。</para>
    /// </summary>
    public bool ShowTray { get; init; } = true;

    /// <summary>托盘每一项的宽度。</summary>
    public int TrayItemWidth { get; init; } = 48;

    /// <summary>水平对齐：true = 居中（贴近原项目的悬浮导航栏），false = 靠左（贴近 Windows 任务栏习惯）。</summary>
    public bool CenterHorizontally { get; init; } = true;

    // ---- 玻璃材质与性能 ----
    public LiquidGlassMaterial Material { get; init; } = LiquidGlassMaterial.Reference;
    public int PathsPerPixel { get; init; } = 4;
    public int IdleAccumulation { get; init; } = 48;
    public int MovingAccumulation { get; init; } = 16;
    public bool MouseParallax { get; init; } = true;
    public double SceneMargin { get; init; } = 96;
    // 默认不再自排除：这样任务栏能出现在截图里（用户要求"截图时可以截到"）。
    // 抓屏时改走 Hide→Capture→Show，玻璃不会采到自己、也无需 WDA_EXCLUDEFROMCAPTURE。
    public bool ExcludeFromCapture { get; init; } = false;

    /// <summary>
    /// 渲染循环的期望节拍（毫秒）。它同时决定单帧渲染预算
    /// （预算 = 节拍 × 0.8），因此也是画质调节器的目标帧时间。
    /// </summary>
    public int TickIntervalMs { get; init; } = 33;

    /// <summary>
    /// 强制指定画质档位。<c>null</c> 表示交给性能调节器自动决定
    /// （启动实测 + 运行时闭环）。用户想要稳定观感时可以锁死某一档。
    /// </summary>
    public QualityTier? ForcedQualityTier { get; init; }

    /// <summary>目标显示器；<see cref="IntPtr.Zero"/> 表示主显示器。</summary>
    public IntPtr Monitor { get; init; } = IntPtr.Zero;
}

/// <summary>
/// 液态玻璃替换任务栏本体。
///
/// <para>它做四件事，按渲染顺序：</para>
/// <list type="number">
///   <item>抓取玻璃背后的桌面像素作为折射源；</item>
///   <item>渲染液态玻璃胶囊；</item>
///   <item>在玻璃之上叠应用图标、文字、时钟与指示条；</item>
///   <item>接收鼠标交互（切换 / 启动应用、右键菜单）。</item>
/// </list>
///
/// <para><b>为什么图标与文字不被折射</b>：它们在玻璃渲染完成之后才画上去，
/// 相当于浮在玻璃表面。这与真实任务栏里"图标画在任务栏背景之上"的层次一致；
/// 上游组件也是同样处理（真实 DOM 按钮留在最上层，避免文字出现折射副本）。</para>
///
/// <para><b>胶囊宽度随应用数变化</b>：每多一个应用就加一个 <see cref="ReplacementTaskbarSettings.ItemWidth"/>，
/// 高度恒定。宽度变化时窗口尺寸与累积缓冲都会重建。</para>
/// </summary>
public sealed class ReplacementTaskbar : IDisposable
{
    private readonly ReplacementTaskbarSettings _settings;
    private readonly Action<string> _log;
    private readonly CpuGlassRenderer _renderer;
    private readonly ScreenSampler _sampler = new();
    private readonly LayeredOverlayWindow _window;
    private readonly object _sync = new();
    private readonly List<TaskbarItem> _items = [];
    private readonly Dictionary<string, BgraFrame> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextRasterizer.RasterizedText> _labelCache = new();

    private Win32Rect _panelBounds;

    // ---- 画布尺寸（超采样后）----
    private int _canvasWidth;
    private int _canvasHeight;
    private PixelRect _canvasGlassRect;

    // ---- 折射源缓存与变化检测 ----
    private GlassScene? _scene;
    private ulong _sceneSignature;

    /// <summary>免隐藏窄带探测的指纹（见 <see cref="BackdropBandChanged"/>）。</summary>
    private ulong _backdropBandSignature;

    /// <summary>累计"隐藏→抓屏→恢复"的次数。静止的桌面下它应该一直是 0。</summary>
    private long _captureHideShowCount;
    private long _lastCaptureTicks;
    private long _sceneChangedTicks;
    private long _lastIdleProbeTicks;
    private bool _probed;

    // ---- 画质调节 ----
    private readonly PerformanceGovernor _governor;
    private double _supersample = 1.0;
    private readonly Action<string>? _qualityLog;

    /// <summary>当前画质档位的可读描述，供托盘与日志展示。</summary>
    public string QualityDescription => _governor.Describe();
    private BgraFrame? _glassFrame;
    private bool _glassDirty = true;
    private bool _windowSized;
    private bool _captureExclusionActive;

    // ---- 悬停胶囊（独立的第二个玻璃元素）----
    /// <summary>胶囊的独立渲染器：自己的累积缓冲，与导航本体互不干扰。</summary>
    private readonly CpuGlassRenderer? _capsuleRenderer;

    private BgraFrame? _capsuleFrame;
    private bool _capsuleDirty = true;

    /// <summary>
    /// 玻璃条上下各留出的溢出像素。胶囊比导航高 20%，所以画布必须比玻璃条更高——
    /// 否则 1.2× 的胶囊会被裁成和导航一样高，丢掉"凸出来"的观感。
    /// </summary>
    private readonly int _capsuleOverflow;

    /// <summary>指针在导航内的归一化 x（0..1）；鼠标不在导航上时保持最后位置。</summary>
    private double _pointerX = 0.5;

    private bool _pointerInside;

    /// <summary>胶囊当前的归一化中心，平滑跟随 <see cref="_pointerX"/>。</summary>
    private double _capsuleCenter = 0.5;

    /// <summary>胶囊的显隐进度 0..1（淡入淡出，避免进出时突兀）。</summary>
    private double _capsuleVisibility;

    /// <summary>上一次渲染用的胶囊矩形（面板局部坐标），用于判断是否需要重算。</summary>
    private PixelRect _capsuleRect;

    /// <summary>胶囊上一帧是否在移动（决定用低预算靠累积、还是高预算单帧即干净）。</summary>
    private bool _capsuleMoving;

    /// <summary>自研的液态玻璃开始菜单；为 null 时「开始」回退到系统菜单。</summary>
    private readonly GlassStartMenu? _startMenu;

    /// <summary>
    /// 液态玻璃快捷面板（托盘点击弹出的小玻璃浮层）。
    ///
    /// <para><b>为什么它取代了"拉系统设置页"</b>：此前点托盘的展开箭头/齿轮
    /// 会直接打开 <c>ms-settings:quiethours</c>（系统的"专注"页），用户看到的是
    /// "点小托盘冒出系统设置"。现在改为弹出本面板 —— 真正的液态玻璃小托盘。</para>
    /// </summary>
    private readonly GlassQuickPanel? _quickPanel;

    // ---- 轮询式鼠标输入 ----
    // 覆盖层沉到 Z 序底部后收不到鼠标消息，点击与悬停都得靠自己轮询按键状态。
    private bool _pollLeftDown;
    private bool _pollRightDown;
    private bool _pollMiddleDown;

    /// <summary>左键按下时落在哪一项上（松开时仍在这一项才算一次点击）。</summary>
    private int _pollPressIndex = -1;

    /// <summary>
    /// 两次点击之间的最小间隔（毫秒）。
    ///
    /// <para><b>为什么必须有</b>：点击「开始」会同步执行 <c>Open()</c>，
    /// 它要抓屏 + 预热渲染，实测阻塞 <b>~100ms</b>。用户在这 100ms 里得不到任何反馈，
    /// 本能地再点一下 —— 而这一下会在阻塞结束后被处理，于是<b>刚打开就被关掉</b>。
    /// 实测日志里两次「点击 → Start」只隔 <b>10ms</b>。</para>
    ///
    /// <para>⚠️ 去抖必须加在<b>任务栏</b>这一层：点「开始」按钮的是任务栏的轮询，
    /// 开始菜单自己那时还没打开、根本没在轮询。（第一版就加错了地方，等于没加。）</para>
    /// </summary>
    private const int DefaultClickDebounceMs = 250;

    /// <summary>胶囊可见度日志的节流时间戳。</summary>
    private long _lastCapsuleLogTicks;
    private bool _disposed;

    private int _hoverIndex = -1;
    private float _lastMouseX = 0.5f;
    private float _lastMouseY = 0.5f;
    private string _lastClockText = string.Empty;

    /// <summary>面板当前的屏幕矩形。宽度随元素数量变化。</summary>
    public Win32Rect PanelBounds => _panelBounds;

    /// <summary>当前任务栏上的应用数量（诊断用）。</summary>
    public int AppCount => _items.Count(i => i.Kind == TaskbarItemKind.App);

    /// <summary>最近一次渲染统计。</summary>
    public GlassRenderStats Stats => _renderer.LastStats;

    /// <summary>用户点击了应用项。App 层可用来记日志或扩展行为。</summary>
    public event Action<ShellApp>? AppInvoked;

    /// <summary>右键菜单里选择了"任务栏设置"。</summary>
    public event Action? TaskbarSettingsRequested;

    private ReplacementTaskbar(ReplacementTaskbarSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
        _qualityLog = log;
        _renderer = new CpuGlassRenderer(settings.Material);

        // 单帧渲染预算：留出两成余量给抓屏、合成与上屏，
        // 否则渲染刚好卡满 tick 间隔时，整个循环会因为抖动而掉帧。
        var budget = Math.Max(2.0, settings.TickIntervalMs * 0.8);
        _governor = new PerformanceGovernor(budget, settings.ForcedQualityTier, log);

        // ---- 悬停胶囊 ----
        // 胶囊比导航高 heightScale 倍，多出来的部分上下均分 → 画布要相应加高。
        _capsuleOverflow = settings.CapsuleEnabled && settings.CapsuleHeightScale > 1.0
            ? (int)Math.Ceiling(settings.Height * (settings.CapsuleHeightScale - 1.0) / 2.0)
            : 0;
        _capsuleRenderer = settings.CapsuleEnabled ? new CpuGlassRenderer(settings.Material) : null;

        // 自研开始菜单：与任务栏共用同一套玻璃渲染器，但各自独立累积。
        _startMenu = settings.UseGlassStartMenu
            ? new GlassStartMenu(settings.StartMenu, log)
            : null;

        // 快捷面板：与开始菜单同一开关（都是"自研玻璃 UI"的一部分）。
        // ⚠️ StartMenu 可能为 null（部分验证模式只开开关不配菜单）—— 那就落到默认值。
        _quickPanel = settings.UseGlassStartMenu
            ? new GlassQuickPanel(new GlassQuickPanel.Settings
            {
                Material = settings.Material,
                FrostedBlurRadius = settings.StartMenu?.FrostedBlurRadius ?? 96.0,
            }, log)
            : null;

        _window = new LayeredOverlayWindow
        {
            Name = "liquidglass-taskbar",
            // 只有"始终置顶"档才在创建时带 WS_EX_TOPMOST；其余档保持普通窗口，
            // 具体层级由 CreateWindow 末尾的 ApplyZOrder() 统一施加。
            TopMost = settings.ZOrder == TaskbarZOrder.TopMost,
            ClickThrough = false,   // 必须接收鼠标：这是用户唯一的应用切换入口
            Log = log,
        };
    }

    /// <summary>创建并显示替换任务栏。</summary>
    public static ReplacementTaskbar? Create(ReplacementTaskbarSettings settings, Action<string> log)
    {
        var bar = new ReplacementTaskbar(settings, log);
        try
        {
            bar.RefreshModel();
            bar.Layout();
            bar.CreateWindow();
            bar._log($"替换任务栏已创建：{bar._panelBounds}，共 {bar.AppCount} 个应用。");
            return bar;
        }
        catch (Exception ex)
        {
            log($"替换任务栏创建失败：{ex.Message}");
            bar.Dispose();
            return null;
        }
    }

    private void CreateWindow()
    {
        var wb = ComputeWindowBounds();
        _window.Create(wb.X, wb.Y, wb.Width, wb.Height);
        _window.MouseMessage += OnMouseMessage;
        _window.MouseLeft += OnMouseLeft;

        if (_settings.ExcludeFromCapture && OsCapabilities.SupportsCaptureExclusion)
        {
            _captureExclusionActive = SetWindowDisplayAffinity(
                _window.Handle, WDA_EXCLUDEFROMCAPTURE);
        }

        _window.Show();
        ApplyZOrder();
        _windowSized = true;
        _windowBounds = wb;

        _log(_capsuleRenderer is null
            ? "悬停胶囊：已关闭。"
            : $"悬停胶囊：已启用（画布上下各留 {_capsuleOverflow}px 溢出给 1.2× 的胶囊；"
              + $"宽 {_settings.CapsuleWidthRatio:0.##}×、高 {_settings.CapsuleHeightScale:0.##}×、"
              + $"{Math.Clamp(_settings.CapsulePathsPerPixel, 1, 12)} paths（移动时 "
              + $"{Math.Max(_settings.CapsulePathsPerPixel, _settings.CapsuleMovingPathsPerPixel)}）"
              + $" × {Math.Max(4, _settings.CapsuleAccumulation)} 帧）。");
    }

    /// <summary>
    /// 覆盖窗口的矩形 = 玻璃条 <see cref="_panelBounds"/> 上下各扩 <see cref="_capsuleOverflow"/>。
    ///
    /// <para>多出来的这条是给 1.2× 的悬停胶囊"凸出来"用的：画布必须比玻璃条高，
    /// 否则胶囊的上下两端会被画布裁掉，退化成和导航一样高。</para>
    ///
    /// <para>溢出量 = <c>高度 × (倍率 − 1) / 2</c>，默认 116 × 0.1 ≈ 12px，
    /// 而底部边距是 16px，所以向下溢出仍在屏幕内。</para>
    /// </summary>
    private Win32Rect ComputeWindowBounds() => _capsuleOverflow <= 0
        ? _panelBounds
        : new Win32Rect(_panelBounds.X, _panelBounds.Y - _capsuleOverflow,
                        _panelBounds.Right, _panelBounds.Bottom + _capsuleOverflow);

    /// <summary>玻璃条在画布里的 y 偏移（画布像素）——上面那段是给胶囊的溢出区。</summary>
    private int CanvasBarOffsetY => (int)Math.Round(_capsuleOverflow * _supersample);

    /// <summary>合成用的最终帧里，图标要往下画的偏移（面板像素）。</summary>
    private int FrameDrawOffsetY => _capsuleOverflow;

    /// <summary>
    /// 按 <see cref="ReplacementTaskbarSettings.ZOrder"/> 施加窗口层级。
    ///
    /// <para>⚠️ v2.2.0 的教训：把"桌面之上、窗口之下"实现成"寄生到壁纸 WorkerW"，
    /// 会让任务栏落到桌面图标/壁纸之下 —— 渲染一切正常，但用户<b>什么都看不到</b>。
    /// 现在默认改用 Z 序沉底（<see cref="LayeredOverlayWindow.PlaceOnDesktopBottom"/>），
    /// 语义相同而始终可见。</para>
    /// </summary>
    private void ApplyZOrder()
    {
        switch (_settings.ZOrder)
        {
            case TaskbarZOrder.TopMost:
                _window.BringToTop();
                _log("Z 序：始终置顶（会盖住所有普通窗口）。");
                break;

            case TaskbarZOrder.BehindDesktopIcons:
                _window.PlaceOnDesktop();
                _log("Z 序：寄生桌面壁纸 WorkerW —— 会落在桌面图标/壁纸之下，通常看不见。");
                break;

            case TaskbarZOrder.Normal:
                _log("Z 序：普通窗口层（不下沉、不置顶，可能被后开的窗口盖住）。");
                break;

            case TaskbarZOrder.DesktopBottom:
            default:
                _window.PlaceOnDesktopBottom();
                _log("Z 序：桌面之上、所有普通窗口之下（Z 序沉底，保持可见）。");
                break;
        }
    }

    // ============================================================ 模型

    /// <summary>重新读取系统任务栏内容（固定的应用 + 正在运行的应用）。</summary>
    public void RefreshModel()
    {
        lock (_sync)
        {
            var pinned = PinnedAppsReader.Read();
            var running = ShellWindowEnumerator.EnumerateApps();

            var merged = Merge(pinned, running);

            // 保留已有的悬停态与已缓存的资源，避免每轮重扫都重新栅格化。
            var hoveredIdentity = _hoverIndex >= 0 && _hoverIndex < _items.Count
                ? _items[_hoverIndex].App?.Identity
                : null;

            _items.Clear();

            if (_settings.ShowStartButton)
            {
                _items.Add(new TaskbarItem { Kind = TaskbarItemKind.Start, Label = "开始" });
            }

            foreach (var app in merged)
            {
                _items.Add(new TaskbarItem
                {
                    Kind = TaskbarItemKind.App,
                    App = app,
                    Label = app.Label,
                    Icon = LoadIcon(app),
                    LabelBitmap = LoadLabel(app),
                });
            }

            if (_settings.ShowTray)
            {
                // 与系统托盘一致：隐藏图标折叠符 → 网络 → 音量 → 电池，时钟在它们右边。
                // 字形来自 Segoe MDL2 Assets；取不到就自动跳过（TextRasterizer 会返回 null）。
                foreach (var glyph in new[]
                {
                    "\uE70E",   // ChevronUp   —— 隐藏的图标
                    "\uE701",   // Network     —— 网络
                    "\uE767",   // Volume      —— 音量
                    "\uE83F",   // Battery     —— 电池
                })
                {
                    _items.Add(new TaskbarItem
                    {
                        Kind = TaskbarItemKind.TrayIcon,
                        Label = glyph,
                    });
                }
            }

            if (_settings.ShowClock)
            {
                var (time, date) = FormatClock();
                _items.Add(new TaskbarItem
                {
                    Kind = TaskbarItemKind.Clock,
                    Label = time,
                    LabelBitmap = LoadLabelText(time, _settings.ClockWidth - 8, bold: true),
                    SubLabelBitmap = LoadSubLabelText(date, _settings.ClockWidth - 8),
                    Icon = null,
                });
                _lastClockText = time + "|" + date;
            }

            if (_settings.ShowQuickSettings)
            {
                _items.Add(new TaskbarItem { Kind = TaskbarItemKind.QuickSettings, Label = "快捷设置" });
            }

            // 恢复悬停索引
            _hoverIndex = hoveredIdentity is null
                ? -1
                : _items.FindIndex(i => i.App?.Identity == hoveredIdentity);
        }
    }

    /// <summary>把"固定的"与"正在运行的"两份列表合并成任务栏项的最终顺序。</summary>
    private static List<ShellApp> Merge(
        IReadOnlyList<PinnedApp> pinned, IReadOnlyList<ShellApp> running)
    {
        var result = new List<ShellApp>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 固定项按固定顺序排在最前面。
        foreach (var pin in pinned)
        {
            var match = FindRunningForPin(pin, running);
            if (match is not null)
            {
                consumed.Add(match.Identity);
                result.Add(match with
                {
                    IsPinned = true,
                    DisplayName = string.IsNullOrWhiteSpace(match.DisplayName)
                        ? pin.DisplayName : match.DisplayName,
                    ShortcutPath = pin.ShortcutPath,
                });
            }
            else
            {
                // 固定但没运行：仍然要出现在任务栏上，点击即启动。
                result.Add(new ShellApp
                {
                    Identity = pin.TargetPath ?? $"pinned:{pin.ShortcutName}",
                    DisplayName = pin.DisplayName,
                    ProcessPath = pin.TargetPath,
                    ShortcutPath = pin.ShortcutPath,
                    IsPinned = true,
                });
            }
        }

        // ② 未固定但正在运行的应用追加在后面（与真实任务栏一致）。
        foreach (var app in running)
        {
            if (consumed.Contains(app.Identity)) continue;
            result.Add(app);
        }

        return result;
    }

    private static ShellApp? FindRunningForPin(PinnedApp pin, IReadOnlyList<ShellApp> running)
    {
        // 优先按可执行文件全路径匹配（最可靠）。
        if (!string.IsNullOrWhiteSpace(pin.TargetPath))
        {
            var exact = running.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.ProcessPath)
                && string.Equals(r.ProcessPath, pin.TargetPath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        // 退一步按展示名匹配 —— 覆盖"快捷方式指向启动器、真正跑的是另一个 exe"的情况，
        // 例如 WPS 的 ksolaunch.exe 与真正干活的 wps.exe。
        var byName = running.FirstOrDefault(r =>
            string.Equals(r.DisplayName, pin.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        if (byName is not null) return byName;

        return null;
    }

    private BgraFrame? LoadIcon(ShellApp app)
    {
        var key = ($"{app.Identity}|{_settings.IconSize}");

        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        BgraFrame? icon = null;

        if (app.Windows.Count > 0)
        {
            icon = IconLoader.FromWindow(app.Windows[0], _settings.IconSize);
        }

        if (icon is null & !string.IsNullOrWhiteSpace(app.ShortcutPath))
        {
            icon = IconLoader.FromFile(app.ShortcutPath!, _settings.IconSize);
        }

        if (icon is null && !string.IsNullOrWhiteSpace(app.ProcessPath))
        {
            icon = IconLoader.FromFile(app.ProcessPath!, _settings.IconSize);
        }

        if (icon is not null) _iconCache[key] = icon;
        return icon;
    }

    private TextRasterizer.RasterizedText? LoadLabel(ShellApp app) =>
        LoadLabelText(app.Label, _settings.LabelMaxWidth);

    private TextRasterizer.RasterizedText? LoadLabelText(string text, int maxWidth, bool bold = false)
    {
        var key = $"{text}|{_settings.LabelFontSize}|{maxWidth}|{bold}";
        if (_labelCache.TryGetValue(key, out var cached)) return cached;

        var raster = TextRasterizer.Render(text, _settings.LabelFontSize, maxWidth, bold);
        if (raster is not null) _labelCache[key] = raster;
        return raster;
    }

    /// <summary>
    /// 第二行小字（时钟的日期行）。字号比主标签小 3px（下限 9px），
    /// 缓存键独立前缀，避免与主标签互相覆盖。
    /// </summary>
    private TextRasterizer.RasterizedText? LoadSubLabelText(string text, int maxWidth)
    {
        var size = Math.Max(9, _settings.LabelFontSize - 3);
        var key = $"sub|{text}|{size}|{maxWidth}";
        if (_labelCache.TryGetValue(key, out var cached)) return cached;

        var raster = TextRasterizer.Render(text, size, maxWidth, bold: false);
        if (raster is not null) _labelCache[key] = raster;
        return raster;
    }

    private static (string Time, string Date) FormatClock()
    {
        var now = DateTime.Now;
        // 日期行与真实任务栏一致用完整日期（2026/9/19）；宽度放不下时由栅格化截断。
        return (now.ToString("HH:mm"), now.ToString("yyyy/M/d"));
    }

    // ============================================================ 布局

    /// <summary>
    /// 重算面板尺寸与每个元素的位置。
    /// <b>宽度随元素数量变化，高度恒定</b>——这是用户明确要求的行为。
    /// </summary>
    private bool Layout()
    {
        lock (_sync)
        {
            var height = _settings.Height;
            var cursor = (double)_settings.HorizontalPadding;

            foreach (var item in _items)
            {
                var width = item.Kind switch
                {
                    TaskbarItemKind.Start => _settings.StartButtonWidth,
                    TaskbarItemKind.Clock => _settings.ClockWidth,
                    TaskbarItemKind.QuickSettings => _settings.QuickSettingsWidth,
                    TaskbarItemKind.TrayIcon => _settings.TrayItemWidth,
                    _ => _settings.ItemWidth,
                };

                item.Rect = new PixelRect(cursor, 0, width, height);
                cursor += width;
            }

            var totalWidth = (int)Math.Ceiling(cursor + _settings.HorizontalPadding);
            if (totalWidth <= 0) totalWidth = 1;

            var monitor = _settings.Monitor != IntPtr.Zero
                ? _settings.Monitor
                : DisplayEnvironment.GetPrimaryMonitor();
            var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
            if (bounds.IsEmpty) bounds = DisplayEnvironment.GetVirtualDesktopBounds();

            var left = _settings.CenterHorizontally
                ? bounds.Left + (bounds.Width - totalWidth) / 2
                : bounds.Left + 12;

            var top = bounds.Bottom - _settings.BottomMargin - height;

            var newBounds = new Win32Rect(left, top, left + totalWidth, top + height);
            var changed = newBounds != _panelBounds;

            // ⚠️ 顺序很关键：先把标志立起来，再更新 bounds。
            _panelBounds = newBounds;

            if (changed)
            {
                // 面板尺寸变了 → 画布、折射源、累积缓冲全部要重建。
                _log($"任务栏布局变化：{_panelBounds.Width} → {newBounds.Width}px"
                    + $"（{AppCount} 个应用 + 托盘/时钟），y={newBounds.Y}。");

                RebuildCanvas();

                // 并且**本帧不许上屏**：这一瞬间窗口还是旧尺寸、内容还是旧内容，
                // 强行 Present 会把两张不同宽度的图重叠在一起 —— 正是"任务栏缩小时
                // 出现乱横线"的直接成因。跳过一帧，等重算完成后自然对齐。
                _skipPresentUntilTicks = Environment.TickCount64;
            }

            return changed;
        }
    }

    // ============================================================ 渲染

    /// <summary>
    /// 每帧驱动。这是一个<b>真正的实时循环</b>，而不是"渲染一次就冻结"：
    ///
    /// <list type="number">
    ///   <item>按当前档位规定的间隔抓取折射源，并做变化检测；</item>
    ///   <item>场景变了 → 立刻重新累积并重画，玻璃始终跟着背后的画面走；</item>
    ///   <item>场景没变但还没收敛 → 继续累积下一帧，逐步变干净；</item>
    ///   <item>场景没变且已收敛 → 玻璃层直接复用，只重画图标文字这些轻量层。</item>
    /// </list>
    ///
    /// 这样既能"实时"，又能在画面静止时把开销压到接近 0 ——
    /// 上游同样是 48 帧后冻结，区别在于它冻结之后就不再醒来，
    /// 而我们每帧都在检测场景有没有变。
    /// </summary>
    public void Tick(bool forceModelRefresh = false)
    {
        if (_disposed) return;

        var needRefresh = forceModelRefresh;

        // 时钟每分钟变一次，变了就重排重画（并触发模型刷新让时间生效）。
        var (time, date) = FormatClock();
        if (time + "|" + date != _lastClockText) needRefresh = true;

        if (needRefresh)
        {
            RefreshModel();
            Layout();
            _itemLayerDirty = true;
        }

        if (!_windowSized)
        {
            CreateWindow();
        }
        else if (ComputeWindowBounds() != _windowBounds)
        {
            // 宽度随应用数变化（或胶囊溢出量变化）→ 画布与累积缓冲全部作废。
            //
            // ⚠️ 这里**故意不调 SetBounds**：`Present` 走的 UpdateLayeredWindow 会
            // 同时设置**位置与尺寸**，而且与位图是原子的。
            // 若先用 SetBounds 改尺寸，窗口会拿着**旧位图**被拉伸显示若干帧 ——
            // 那正是"开/关一个应用时闪一下花屏"的成因。位置与尺寸统一交给 Present。
            _windowBounds = ComputeWindowBounds();
            RebuildCanvas();
        }

        var now = Environment.TickCount64;

        // ---- Z 序自愈：定期把窗口重新沉底（仅"桌面之上、窗口之下"档）----
        // WS_EX_NOACTIVATE 已保证点击不会把它抬起来，但 explorer 重启、
        // 某些以特殊方式创建的顶层窗口仍可能把它挤上去。每几秒重设一次代价极低，
        // 却能保证层级长期稳定。
        if (_settings.ZOrder == TaskbarZOrder.DesktopBottom
            && now - _lastZOrderTicks >= ZOrderReassertMs)
        {
            _lastZOrderTicks = now;
            _window.PlaceOnDesktopBottom();
        }

        // ---- ① 首次：用真实负载测一次，据此定初始画质档位 ----
        if (!_probed)
        {
            RunStartupProbe();
            _lastCaptureTicks = now;
        }
        // ---- ② 抓屏 + 变化检测（按档位规定的间隔节流）----
        else if (now - _lastCaptureTicks >= CaptureIntervalMs())
        {
            _lastCaptureTicks = now;
            UpdateScene();
        }

        // ---- ③ 指针位置 + 悬停胶囊 ----
        //
        // 为什么这里"轮询 GetCursorPos"而不是只依赖 WM_MOUSEMOVE：
        // 实测本机的外壳窗口层级下，覆盖层窗口收不到鼠标输入
        // （窗口过程里的 WM_NCHITTEST 计数恒为 0），而胶囊必须跟随指针。
        // 一次 GetCursorPos 的开销可以忽略，而且天然不受消息路由影响。
        // WM_MOUSEMOVE 那条路仍然保留，谁先到就用谁。
        UpdatePointerFromCursor();
        PollPointerInput();          // 悬停高亮与"点击"也一并在这里驱动
        var capsuleRedraw = UpdateCapsule();

        // ---- ④ 玻璃层 ----
        var idleProbe = false;
        if (!_glassDirty && _renderer.IsConverged
            && now - _lastIdleProbeTicks >= IdleProbeIntervalMs)
        {
            // 静默期探针。
            //
            // 收敛后我们就不再渲染了，于是调节器也收不到新的耗时样本 ——
            // 升档判据永远攒不够，机器明明有富余却会一直待在偏低档位。
            // 每隔几秒故意重画一帧，既让调节器保持对当前性能的感知，
            // 又几乎不影响观感（此时历史权重高达 47/48，画面几乎不变）。
            _lastIdleProbeTicks = now;
            idleProbe = true;
        }

        if (_glassDirty || !_renderer.IsConverged || idleProbe)
        {
            // 画布重建/换档后 _scene 会被清空，必须立刻补一帧折射源，
            // 否则 RenderGlass 拿不到场景、_glassFrame 仍是 null，
            // 合成阶段会跳过——极端情况下会闪一帧空白。这里先补源再渲染。
            if (_scene is null) UpdateScene();

            RenderGlass(forceRecompute: idleProbe);
            _glassDirty = false;
            _itemLayerDirty = true;

            // 档位刚变化 → 超采样倍数可能不同，画布要重建并重画。
            if (_governor.TierChanged)
            {
                ApplyProfile();
                if (_scene is null) UpdateScene();   // 重建后同样先补源
            }
        }

        // ---- ⑤ 悬停胶囊那一层（独立渲染器，只重算它自己）----
        if (capsuleRedraw)
        {
            if (_scene is null) UpdateScene();
            RenderCapsule(forceRecompute: idleProbe);
            _itemLayerDirty = true;   // 合成帧含胶囊，必须重画
        }

        // ---- ⑥ 合成上屏 ----
        // ⚠️ 只有真的上屏了才能清脏标记 —— 跳帧窗口 / 玻璃未就绪 / 尺寸未对齐
        // 都会跳过上屏，此时保留标记，下一个 tick 自动重试（见 CompositeAndPresent）。
        if (_itemLayerDirty)
        {
            if (CompositeAndPresent()) _itemLayerDirty = false;
        }

        // ---- ⑦ 开始菜单 ----
        // 放在最后：本 tick 的点击判定已经先跑过了（见 PollPointerInput），
        // 所以"菜单开着时再点『开始』"会先被上面处理成关闭，不会被这里重新打开。
        SafeStartMenu(() => _startMenu!.Tick(), "渲染");

        // ---- ⑧ 快捷面板 ----
        _quickPanel?.Tick();
    }

    /// <summary>静默期探针的间隔。太短会白白耗电，太长则升档反应迟钝。</summary>
    private const int IdleProbeIntervalMs = 1500;

    /// <summary>"沉底"Z 序的自愈间隔（毫秒）。</summary>
    private const int ZOrderReassertMs = 2000;

    private long _lastZOrderTicks;

    private Win32Rect _windowBounds;
    private bool _itemLayerDirty = true;
    private int _sceneChangeCount;

    /// <summary>
    /// 启动时的性能实测。
    ///
    /// <para>为什么不看 CPU 核数猜档位：同样 8 核，插电和用电池、
    /// 集显轻薄本和台式机、后台有没有别的程序在抢，实际渲染速度能差三五倍。
    /// 唯一可靠的办法就是<b>拿真实的工作负载去测</b>。</para>
    ///
    /// <para>基准配置刻意选成上游桌面参考值（4 paths、1.0× 超采样），
    /// 这样日志里的毫秒数可以直接和上游的性能档位表对照。</para>
    /// </summary>
    private void RunStartupProbe()
    {
        var margin = (int)Math.Ceiling(_settings.SceneMargin);
        var capture = CaptureScenePixels(margin);

        var probeRect = new PixelRect(0, 0, _panelBounds.Width, _panelBounds.Height);
        var scene = GlassScene.FromBgra(capture, -margin, -margin);

        var options = new GlassRenderOptions
        {
            GlassRect = probeRect,
            CanvasWidth = _panelBounds.Width,
            CanvasHeight = _panelBounds.Height,
            PathsPerPixel = 4,
            MaxAccumulation = 48,
        };

        // 先空跑一帧把 JIT、内存分配、纹理转换的开销排除掉，
        // 否则弱机上测到的是"第一次运行"而不是"稳定运行"的耗时。
        _renderer.Render(scene, options);
        _renderer.Reset();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _renderer.Render(scene, options);
        stopwatch.Stop();

        _governor.InitializeFromProbe(
            stopwatch.Elapsed.TotalMilliseconds, _panelBounds.Width, _panelBounds.Height);

        _scene = scene;
        _sceneSignature = ComputeSignature(capture);
        _sceneChangedTicks = Environment.TickCount64;
        _probed = true;
        ApplyProfile();
    }

    /// <summary>
    /// 抓屏节流间隔。
    ///
    /// <para>走"隐藏→抓屏→恢复"这条路时会被抬到一个下限：隐藏期间任务栏是从屏幕上
    /// **消失**的，每一次都是一次闪烁 —— 既是观感问题，也正是用户截图拍不到任务栏的原因。
    /// 所以宁可反应慢一点，也不要每秒闪几十下。</para>
    /// </summary>
    private int CaptureIntervalMs()
    {
        var baseMs = _governor.Profile.CaptureIntervalMs;

        // 有自排除（WDA_EXCLUDEFROMCAPTURE）时不闪，按画质档位正常节流即可。
        if (_captureExclusionActive) return baseMs;

        if (_settings.CaptureMinIntervalMs > 0) return Math.Max(_settings.CaptureMinIntervalMs, baseMs);

        // 自动档：关键看"玻璃背后到底动不动"。
        //
        //  · 预留了工作区（默认）→ 窗口不会延伸到任务栏那一条，玻璃背后**只有壁纸**，
        //    也就是基本静止。那就几乎不需要重新抓屏：15 秒兜一次足够。
        //    这一条极其重要 —— 每次"隐藏→抓屏→恢复"都会让任务栏从屏幕上消失几毫秒，
        //    既是闪烁，也是用户截图偶尔拍到空档的原因（实测 2 秒内 40 张截图有 2 张拍空）。
        //
        //  · 没预留（窗口会延伸到玻璃下方）→ 背后一直在变，那就得跟得上，给 400ms。
        return Math.Max(_settings.ReserveWorkAreaHeight > 0 ? 15000 : 400, baseMs);
    }

    /// <summary>
    /// 廉价地问一句"玻璃背后变了没有"。
    ///
    /// <para>只采一条<b>覆盖层窗口盖不到</b>的窄带（窗口上边界以上、整块抓屏区域以内）
    /// 来做指纹。因为这条带里不可能有我们自己，所以<b>不需要隐藏窗口</b> ——
    /// 桌面静止时一次隐藏/恢复都不会发生，既不闪，也不会跟用户的截图抢时间。
    /// 只有这条带真的变了，才值得付出"隐藏→抓屏→恢复"的代价。</para>
    ///
    /// <para>为什么这条带能代表玻璃背后：任务栏坐在<b>预留出来的那条带</b>里，
    /// 它背后本来就只有壁纸；紧邻其上的像素与它高度相关。</para>
    /// </summary>
    /// <returns>自上次检查以来是否发生变化（首次调用恒为 true）。</returns>
    private bool BackdropBandChanged(int margin)
    {
        var bandTop = _panelBounds.Y - margin;
        var overlayTop = _panelBounds.Y - _capsuleOverflow;   // 覆盖层窗口的顶边
        var bandHeight = overlayTop - bandTop;

        // 带太窄就判断不了，老实走完整抓屏。
        if (bandHeight < 8) return true;

        // 复用窄带缓冲：这个探测每 tick 都可能跑，逐次分配会在 LOH 上持续堆垃圾。
        var bandWidth = _panelBounds.Width + margin * 2;
        if (_bandScratch is null
            || _bandScratch.Width != bandWidth || _bandScratch.Height != bandHeight)
        {
            _bandScratch = new BgraFrame(bandWidth, bandHeight);
        }

        _sampler.CapturePaddedInto(_bandScratch, _panelBounds.X - margin, bandTop);
        var band = _bandScratch;

        var signature = ComputeSignature(band);
        var changed = signature != _backdropBandSignature;
        _backdropBandSignature = signature;
        return changed;
    }

    /// <summary>窄带探测的复用缓冲。</summary>
    private BgraFrame? _bandScratch;

    /// <summary>抓屏 + 变化检测。场景真的变了才重建折射源并触发重画。</summary>
    private void UpdateScene()
    {
        var margin = (int)Math.Ceiling(_settings.SceneMargin);

        // 先做免隐藏的窄带探测：没变就直接返回，一次隐藏/恢复都不做。
        if (!_captureExclusionActive && !BackdropBandChanged(margin)) return;

        var capture = CaptureScenePixels(margin);
        var signature = ComputeSignature(capture);

        // 量化后的签名：低于量化台阶的细微变化（压缩噪声、1 个色阶的抖动）不会触发重画，
        // 否则一个正在播放的视频会把我们钉死在"每帧重置"上，白白丢掉累积带来的干净度。
        if (signature == _sceneSignature) return;

        _sceneSignature = signature;
        _sceneChangedTicks = Environment.TickCount64;
        _sceneChangeCount++;

        // 尺寸不变时**原地刷新**，复用那块巨大的线性缓冲（任务栏 1516×308 的场景
        // 约 5.6MB —— 远超 85KB，必进 LOH 且不压缩）。
        if (_scene is null || !_scene.TryUpdateFromBgra(capture, -margin, -margin, _supersample))
        {
            _scene = GlassScene.FromBgra(capture, -margin, -margin, null, _supersample);
        }

        // 场景变了就重新累积。上游同样在场景变化时置 uReset ——
        // 不重置的话，历史帧的权重高达 47/48，新画面要 1.6 秒才能透出来，
        // 看起来就像玻璃"卡住了"。
        _renderer.Reset();
        _capsuleRenderer?.Reset();   // 胶囊采的是同一张场景，同样要重新累积
        _capsuleDirty = true;
        _glassDirty = true;
    }

    /// <summary>抓取玻璃背后的屏幕像素，窗口自排除失败时走"隐藏→抓屏→恢复"。</summary>
    /// <summary>
    /// 抓取折射源像素，写入<b>复用缓冲</b>。
    ///
    /// <para>⚠️ 返回的是复用缓冲，调用方<b>不得长期持有</b> ——
    /// 下一次抓屏会覆盖它。两个调用点都是"立刻用完"，符合这个约束。</para>
    ///
    /// <para><b>为什么必须复用</b>：任务栏场景是 1516×308 ≈ 1.9MB，而 <c>CapturePadded</c>
    /// 内部还会再分配一块同尺寸的"内层有效区域" —— 每次抓屏实际分配约 3.8MB。
    /// 超过 85KB 即进 LOH 且不被压缩，长期运行会造成碎片化与内存虚高。</para>
    /// </summary>
    private BgraFrame CaptureScenePixels(int margin)
    {
        var panel = _panelBounds;
        var x = panel.X - margin;
        var y = panel.Y - margin;
        var width = panel.Width + margin * 2;
        var height = panel.Height + margin * 2;

        if (_captureScratch is null
            || _captureScratch.Width != width || _captureScratch.Height != height)
        {
            _captureScratch = new BgraFrame(width, height);
        }

        if (_captureExclusionActive)
        {
            _sampler.CapturePaddedInto(_captureScratch, x, y);
            return _captureScratch;
        }

        // 回退路径：不使用 WDA_EXCLUDEFROMCAPTURE（那样用户截图也拍不到任务栏）。
        // 隐藏 → 抓屏 → 立刻恢复。
        //
        // ⚠️ 这条路**每次调用都会让任务栏从屏幕上消失一下**。所以调用方必须先做
        // 免隐藏的窄带探测（BackdropBandChanged），只有背后真的变了才走到这里；
        // 单次频率也被 CaptureIntervalMs() 抬了下限。
        // v2.2~v2.4 曾在这里每 tick 无条件隐藏/恢复（最高 30 次/秒），
        // 表现为任务栏持续闪烁 + 用户截图拍不到它。
        _captureHideShowCount++;
        _window.Hide();
        try
        {
            _sampler.CapturePaddedInto(_captureScratch, x, y);
            return _captureScratch;
        }
        finally
        {
            _window.Show();
        }
    }

    /// <summary>
    /// 场景指纹：把抓到的画面降采样成 32×12 的小块并量化，再算一个哈希。
    ///
    /// <para>为什么不用逐像素比较：那样每帧要搬运上兆字节，还要遍历一遍；
    /// 而这里只读 384 个采样点，成本可以忽略。</para>
    ///
    /// <para>为什么要量化：不量化的话，显卡驱动或视频解码带来的 1 个色阶抖动
    /// 会让指纹每帧都变，于是每帧都重置累积 —— 白白丢掉干净度。</para>
    /// </summary>
    private static ulong ComputeSignature(BgraFrame frame)
    {
        const int gridX = 32;
        const int gridY = 12;

        ulong hash = 1469598103934665603UL;   // FNV-1a 64 位偏移基

        for (var gy = 0; gy < gridY; gy++)
        {
            var y0 = gy * frame.Height / gridY;
            var y1 = Math.Max(y0 + 1, (gy + 1) * frame.Height / gridY);

            for (var gx = 0; gx < gridX; gx++)
            {
                var x0 = gx * frame.Width / gridX;
                var x1 = Math.Max(x0 + 1, (gx + 1) * frame.Width / gridX);

                int sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * frame.Stride;
                    for (var x = x0; x < x1; x++)
                    {
                        var i = row + x * 4;
                        sumB += frame.Pixels[i];
                        sumG += frame.Pixels[i + 1];
                        sumR += frame.Pixels[i + 2];
                        count++;
                    }
                }

                if (count == 0) continue;

                // 量化到 5 位（每级 8 个色阶）：低于这个幅度的变化直接忽略。
                var qb = (ulong)(sumB / count >> 3);
                var qg = (ulong)(sumG / count >> 3);
                var qr = (ulong)(sumR / count >> 3);

                hash = (hash ^ qb) * 1099511628211UL;
                hash = (hash ^ qg) * 1099511628211UL;
                hash = (hash ^ qr) * 1099511628211UL;
            }
        }

        return hash;
    }

    /// <summary>应用当前档位的超采样倍数并重建画布。</summary>
    private void ApplyProfile()
    {
        var profile = _governor.Profile;
        _supersample = profile.Supersample;
        RebuildCanvas();
    }

    /// <summary>按当前超采样倍数重算画布尺寸与玻璃矩形。</summary>
    private void RebuildCanvas()
    {
        var ss = _supersample;
        var wb = ComputeWindowBounds();
        _canvasWidth = Math.Max(1, (int)Math.Round(wb.Width * ss));
        _canvasHeight = Math.Max(1, (int)Math.Round(wb.Height * ss));

        // 玻璃条只占画布中间那条；上下留出的溢出区专给 1.2× 的胶囊。
        var barOffset = CanvasBarOffsetY;
        _canvasGlassRect = new PixelRect(
            0, barOffset, _canvasWidth,
            Math.Max(1, (int)Math.Round(_panelBounds.Height * ss)));

        // 画布尺寸变了，累积缓冲与折射源全部作废。
        _renderer.Reset();
        _capsuleRenderer?.Reset();
        _capsuleFrame = null;
        _capsuleDirty = true;
        _scene = null;
        _sceneSignature = 0;
        _glassFrame = null;   // 关键：丢弃上一帧旧尺寸的玻璃，避免被 DownscaleBox 拉伸成花屏
        _glassDirty = true;
        _itemLayerDirty = true;

        // 换画布后的头几帧要背缓冲分配与缓存预热的开销，实测能比稳态高好几倍。
        // 把它们排除在调节器的判据之外，避免"刚换档就被自己吓回低档"。
        _governor.BeginWarmup(4);
    }

    /// <summary>重新计算玻璃层（抓屏已经在 UpdateScene 里做过，这里只做光学渲染）。</summary>
    /// <param name="forceRecompute">
    /// 静默期探针用：即使已收敛也真算一遍，好让性能调节器拿到耗时样本。
    /// 它<b>不</b>重置累积，所以画面几乎不变。
    /// </param>
    private void RenderGlass(bool forceRecompute = false)
    {
        if (_scene is null) return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 鼠标位置驱动相机射线视差（对应上游的 uMouse）。
        var mouseX = 0.5f;
        var mouseY = 0.5f;
        if (_settings.MouseParallax)
        {
            mouseX = _lastMouseX;
            mouseY = _lastMouseY;
        }

        _glassFrame = _renderer.Render(_scene, new GlassRenderOptions
        {
            GlassRect = _canvasGlassRect,
            CanvasWidth = _canvasWidth,
            CanvasHeight = _canvasHeight,
            PathsPerPixel = _governor.Profile.PathsPerPixel,
            MaxAccumulation = _governor.Profile.IdleAccumulation,
            MouseX = mouseX,
            MouseY = mouseY,
            ForceRecompute = forceRecompute,
        });

        // 收敛时渲染器会直接返回缓存，那时不该把 0 毫秒当成"这台机器很快"，
        // 否则调节器会误判为性能富余而一路升档。
        if (!_renderer.LastStats.UsedCache)
        {
            _governor.ReportFrame(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // ============================================================ 悬停胶囊

    /// <summary>
    /// 计算胶囊矩形（面板局部坐标），逐行移植上游 <c>layout.js</c> 的
    /// <c>computeCapsuleRect()</c>：
    /// <code>
    /// desired = navWidth * widthRatio        // 默认 0.2，即 1/5
    /// center  = navLeft + pointerX * navWidth
    /// left    = max(navLeft, center - desired/2)      // 夹在导航内，不会滑出去
    /// right   = min(navLeft + navWidth, center + desired/2)
    /// height  = navHeight * heightScale      // 默认 1.2，上下各溢出 10%
    /// y       = navTop - (height - navHeight) / 2
    /// </code>
    /// </summary>
    private PixelRect ComputeCapsuleRect()
    {
        var navW = Math.Max(1, _panelBounds.Width);
        var navH = Math.Max(1, _panelBounds.Height);

        var desired = navW * Math.Clamp(_settings.CapsuleWidthRatio, 0.05, 1.0);
        var center = _capsuleCenter * navW;

        var left = Math.Max(0.0, center - desired * 0.5);
        var right = Math.Min((double)navW, center + desired * 0.5);

        var h = navH * Math.Max(1.0, _settings.CapsuleHeightScale);

        // y 为负表示向上溢出玻璃条——画布的溢出区正是为它准备的。
        return new PixelRect(left, (navH - h) * 0.5, Math.Max(2, right - left), h);
    }

    /// <summary>
    /// 轮询指针位置，更新"指针是否在任务栏上"与归一化 x。
    ///
    /// <para>之所以轮询而不是等鼠标消息：实测在把窗口沉到 Z 序最底部之后，
    /// 覆盖层收不到任何鼠标输入（窗口过程的 WM_NCHITTEST 计数恒为 0），
    /// 而悬停胶囊必须跟随指针。轮询一次 <c>GetCursorPos</c> 的代价可以忽略。</para>
    /// </summary>
    private void UpdatePointerFromCursor()
    {
        if (_capsuleRenderer is null) return;
        if (!GetCursorPos(out var pt)) return;

        var wb = ComputeWindowBounds();
        var inside = pt.X >= wb.X && pt.X < wb.Right && pt.Y >= wb.Y && pt.Y < wb.Bottom;

        _pointerInside = inside;
        if (!inside) return;

        _pointerX = Math.Clamp(
            (pt.X - _panelBounds.X) / (double)Math.Max(1, _panelBounds.Width), 0, 1);
    }

    /// <summary>
    /// 轮询鼠标按键，自己实现"悬停 + 点击"。
    ///
    /// <para>为什么不用 WM_MOUSEMOVE / WM_LBUTTONUP：把覆盖层沉到 Z 序最底部之后，
    /// 窗口过程**收不到任何鼠标消息**（<see cref="LayeredOverlayWindow.HitTestSeen"/> 恒为 0），
    /// 于是悬停高亮和"点图标启动应用"全都失效。主动轮询系统按键状态可以绕开消息路由，
    /// 代价只是最多一个 tick 的延迟。</para>
    ///
    /// <para>判定与 Windows 语义对齐：<b>按下与松开落在同一项上才算一次点击</b>，
    /// 中途滑开就取消（和真实的按钮行为一致）。</para>
    /// </summary>
    private void PollPointerInput()
    {
        if (!GetCursorPos(out var pt)) return;

        var wb = ComputeWindowBounds();
        var insideWindow = pt.X >= wb.X && pt.X < wb.Right && pt.Y >= wb.Y && pt.Y < wb.Bottom;

        // 屏幕坐标 → 面板局部坐标。窗口比玻璃条上下各多一条胶囊溢出区，所以要减去面板原点。
        var index = insideWindow ? HitTest(pt.X - _panelBounds.X, pt.Y - _panelBounds.Y) : -1;

        // 悬停高亮与鼠标视差（原先只在 WM_MOUSEMOVE 里更新，现在一样能工作）。
        UpdateHover(index);

        var now = Environment.TickCount64;

        var rawLeft = GetAsyncKeyState(VK_LBUTTON);
        var right = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        var middle = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

        var left = (rawLeft & 0x8000) != 0;

        // 左键主路径：按下记住落点，松开时仍在同一项 → 点击。
        if (left && !_pollLeftDown)
        {
            _pollPressIndex = index;
        }
        else if (!left && _pollLeftDown)
        {
            // 去抖在 InvokeItem 里统一做（那里是所有点击路径的汇合点），这里不重复。
            if (index >= 0 && index == _pollPressIndex) InvokeItem(index);
            _pollPressIndex = -1;
        }
        // 这里刻意**不做**"低位兜底"（GetAsyncKeyState 的 0x0001 位）：
        // MSDN 明确说它不可靠，而且实测会在主路径刚处理完的那一帧仍然为热，
        // 导致**一次点击触发两次**（激活应用后立刻又被最小化）。
        // 主路径是确定性的：轮询约 30Hz，而人的点击至少几十毫秒，必然能采到"按下"这一态。

        // 中键：新开实例（与真实任务栏一致）。
        if (!middle && _pollMiddleDown && index >= 0
            && GetItem(index)?.App is { IsRunning: true } middleApp)
        {
            ShellActions.Launch(middleApp);
        }

        // 右键：原生上下文菜单。
        if (!right && _pollRightDown && index >= 0) ShowContextMenu(index);

        _pollLeftDown = left;
        _pollRightDown = right;
        _pollMiddleDown = middle;
    }

    /// <summary>
    /// 推进胶囊的跟随动画，返回"胶囊是否需要在本次 tick 重新渲染"。
    ///
    /// <para>上游是"动画跟随指针"，所以这里不做瞬时吸附，而是每帧向目标推进
    /// <see cref="ReplacementTaskbarSettings.CapsuleFollow"/>。接近目标时直接吸附，
    /// 否则永远差一点点、会让整条任务栏停不下来。</para>
    /// </summary>
    private bool UpdateCapsule()
    {
        if (_capsuleRenderer is null) return false;

        var follow = Math.Clamp(_settings.CapsuleFollow, 0.05, 1.0);
        var targetVis = _pointerInside ? 1.0 : 0.0;

        var prevCenter = _capsuleCenter;
        var prevVis = _capsuleVisibility;

        _capsuleCenter += (_pointerX - _capsuleCenter) * follow;
        _capsuleVisibility += (targetVis - _capsuleVisibility) * Math.Min(1.0, follow * 1.6);

        // 吸附到目标，杜绝"永远在动"的持续重绘。
        if (Math.Abs(_capsuleCenter - _pointerX) < 0.0008) _capsuleCenter = _pointerX;
        if (Math.Abs(_capsuleVisibility - targetVis) < 0.005) _capsuleVisibility = targetVis;

        if (Math.Abs(prevCenter - _capsuleCenter) > 1e-9
            || Math.Abs(prevVis - _capsuleVisibility) > 1e-9)
        {
            // 胶囊是合成帧的一部分 → 动画期间必须持续重画图标层。
            _itemLayerDirty = true;
        }

        // 完全隐去就整层不渲染（省掉一整块玻璃的开销）。
        if (_capsuleVisibility <= 0.004) return false;

        var rect = ComputeCapsuleRect();
        var prev = _capsuleRect;
        var moved = Math.Abs(rect.X - prev.X) > 0.25
                 || Math.Abs(rect.Y - prev.Y) > 0.25
                 || Math.Abs(rect.Width - prev.Width) > 0.25
                 || Math.Abs(rect.Height - prev.Height) > 0.25;

        if (!moved && !_capsuleDirty) return false;

        _capsuleRect = rect;
        _capsuleDirty = false;
        _capsuleMoving = moved;
        return true;
    }

    /// <summary>
    /// 渲染胶囊这一层。它有自己的渲染器与累积缓冲，
    /// 因此移动胶囊不会让整条任务栏重新累积（上游同样只重绘新旧胶囊区域）。
    /// </summary>
    private void RenderCapsule(bool forceRecompute = false)
    {
        if (_capsuleRenderer is null || _scene is null) return;

        var ss = _supersample;
        var cap = ComputeCapsuleRect();
        var barOffset = CanvasBarOffsetY;

        // 面板坐标 → 画布坐标：x 直接缩放，y 还要加上玻璃条在画布里的偏移。
        var canvasRect = new PixelRect(
            cap.X * ss,
            cap.Y * ss + barOffset,
            cap.Width * ss,
            cap.Height * ss);

        // 胶囊在移动时**拿不到时间累积**：渲染器的包围盒一变就会清空历史
        // （见 CpuGlassRenderer 里对 _tile 的处理）。静止时 2 paths 靠 16 帧累积
        // 足够干净，但移动时每帧只有一次采样 —— 随机菲涅耳反射会在这种采样密度下
        // 变成肉眼可见的噪点，也就是"花"。所以移动时临时抬高路径数，
        // 让单帧本身就够干净；停下后立刻回到低预算靠累积收敛。
        var settledPaths = Math.Clamp(_settings.CapsulePathsPerPixel, 1, 12);
        var paths = _capsuleMoving
            ? Math.Clamp(_settings.CapsuleMovingPathsPerPixel, settledPaths, 16)
            : settledPaths;

        _capsuleFrame = _capsuleRenderer.Render(_scene, new GlassRenderOptions
        {
            GlassRect = canvasRect,
            CanvasWidth = _canvasWidth,
            CanvasHeight = _canvasHeight,
            PathsPerPixel = paths,
            MaxAccumulation = Math.Max(4, _settings.CapsuleAccumulation),
            MouseX = _settings.MouseParallax ? _lastMouseX : 0.5f,
            MouseY = _settings.MouseParallax ? _lastMouseY : 0.5f,
            ForceRecompute = forceRecompute,
        });

        LogCapsuleVisibility(canvasRect);
    }

    /// <summary>
    /// 量化"胶囊到底改变了多少画面"。
    ///
    /// <para>用<b>同一帧内</b>「玻璃条」与「胶囊层」在同一批像素上的差异来衡量 ——
    /// 这样完全不受场景变化干扰，是可以写进日志的硬证据：
    /// 数字为 0 就说明胶囊根本没画上去。</para>
    /// </summary>
    private void LogCapsuleVisibility(PixelRect canvasRect)
    {
        var now = Environment.TickCount64;
        if (now - _lastCapsuleLogTicks < 2000) return;
        _lastCapsuleLogTicks = now;

        if (_capsuleFrame is null || _glassFrame is null) return;
        if (_capsuleFrame.Width != _glassFrame.Width) return;

        var bar = _glassFrame.Pixels;     // 只有玻璃条
        var cap = _capsuleFrame.Pixels;   // 只有胶囊
        long n = 0;
        long sum = 0;

        for (var i = 0; i < cap.Length; i += 4)
        {
            if (cap[i + 3] == 0) continue;
            n++;
            sum += Math.Abs(cap[i] - bar[i])
                 + Math.Abs(cap[i + 1] - bar[i + 1])
                 + Math.Abs(cap[i + 2] - bar[i + 2]);
        }

        var mean = n > 0 ? sum / (double)n / 3.0 : 0;

        _log($"悬停胶囊：已渲染（rect {_capsuleRect}，覆盖 {n}px，"
            + $"与玻璃条的平均像素差 {mean:F1}/通道，可见度 {(_capsuleVisibility * 100):F0}%）。");
    }

    /// <summary>
    /// 把玻璃条 + 悬停胶囊 + 图标文字合成成最终帧并上屏。
    ///
    /// <para>层次自下而上：<b>玻璃条 → 悬停胶囊 → 图标与文字</b>。
    /// 文字画在胶囊之上，与上游"导航文字排除在折射之外、永远清晰"的处理一致。</para>
    ///
    /// <para>返回<b>这一帧是否真的上屏了</b>。调用方必须以上屏与否来决定
    /// 是否清掉 <c>_itemLayerDirty</c> —— v3.8.0 之前脏标记被无条件消费，
    /// 而布局变化后的 60ms 跳帧窗口内上屏是被跳过的：标记清了、帧没上，
    /// 若接下来没有场景变化来重新置脏（静止壁纸 + 渲染已收敛），
    /// 任务栏会一直停留在旧尺寸旧内容上 —— 用户看到的就是
    /// "图标一减少任务栏就坏掉"。这是缩小时各种怪象的总闸门。</para>
    /// </summary>
    private bool CompositeAndPresent()
    {
        // 布局刚变化过：窗口尺寸 / 画布尺寸 / 内容坐标正处在不同世代，
        // 这一帧上屏必然错位（乱横线）。等它们对齐。
        // 60ms 足够走完 RebuildCanvas 并渲染出第一帧，用户感知不到这一跳。
        if (Environment.TickCount64 - _skipPresentUntilTicks < 60) return false;

        // 画布刚重建过（比如任务栏因为应用数变化而缩短）时 _glassFrame 会被丢弃。
        // 此时若直接 return，窗口就**不会**被 Present —— 于是它还停留在旧尺寸、
        // 显示着旧内容，看起来就是一堆错位的横线。
        // 所以这里必须先把玻璃补渲染出来，保证每次合成都能上屏、窗口尺寸始终跟得上。
        if (_glassFrame is null)
        {
            RenderGlass();
            if (_glassFrame is null) return false;
        }

        // 玻璃帧是渲染器复用的缓冲，必须复制后再叠加，否则会污染累积状态。
        // 复制到**复用缓冲**而不是每次 Clone —— 任务栏每 33ms 上屏一次，
        // 每次分配 700KB+ 会全部落进 LOH（>85KB 即入大对象堆，且不压缩），
        // 持续跑下去就是碎片化与内存虚高的来源。
        var canvas = CopyGlassToScratch();

        // 胶囊叠在玻璃条之上（它自己的 coverage 会限制作用范围）。
        CompositeCapsule(canvas);

        // 超采样 → 缩回窗口尺寸。区域平均天然做了抗锯齿，
        // 玻璃的圆角边缘与图标轮廓都会比 1:1 渲染平滑。
        var wb = ComputeWindowBounds();

        // ⚠️ 这里曾经是：
        //     var frame = _supersample > 1.001 ? DownscaleBox(canvas, wb.Width, wb.Height) : canvas;
        //
        // 超采样为 1（Balanced 档就是 1×）时直接拿 canvas 当上屏帧，跳过了尺寸校正。
        // 而 canvas 的尺寸是 _canvasWidth/Height = round(wb.Width/Height * ss)，
        // 在任务栏因应用增减而**缩短**的那一瞬间，坐标重算与画布重建会短暂错开一帧 ——
        // 此时 canvas 的步幅与 wb 的宽度不一致，上屏就是一条条错位的横线（乱横线）。
        //
        // 现在无论超采样是几，都强制做一次尺寸校正确保逐像素对齐。
        var frame = AlignToPanel(canvas, wb.Width, wb.Height);
        if (frame is null) return false;   // 尺寸对不齐就这一帧不上屏，下一帧画布重建完自然会好

        lock (_sync)
        {
            // 图标是"面板坐标"，而帧是"窗口坐标"（多了胶囊溢出区），故要下移。
            var dy = FrameDrawOffsetY;

            foreach (var item in _items)
            {
                DrawItemDecoration(frame, item, dy);
            }

            foreach (var item in _items)
            {
                DrawItemContent(frame, item, dy);
            }
        }

        _window.Present(frame, wb.X, wb.Y);
        return true;
    }

    /// <summary>
    /// 把画布对齐到窗口尺寸。
    ///
    /// <para><b>为什么不能直接返回 canvas</b>：<see cref="BgraFrame"/> 的步幅等于
    /// <c>Width * 4</c>，一旦帧宽与要上屏的窗口宽不等，<c>UpdateLayeredWindow</c>
    /// 就会按错误的步长逐行读取，把画面斜切/撕裂成横线。
    /// 超采样为 1 时两者本该相等，但 <c>Math.Round</c> 取整与窗口坐标的重算
    /// 会在任务栏尺寸刚变化的那一帧出现短暂不一致。</para>
    ///
    /// <para>返回 <c>null</c> 表示"这一帧别上屏"（尺寸到了下界等病态情况），
    /// 调用方应当直接 return，等下一帧画布重建完成。</para>
    /// </summary>
    private BgraFrame? AlignToPanel(BgraFrame canvas, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        // 完全一致：直接复用，最省。
        if (canvas.Width == width && canvas.Height == height) return canvas;

        // 不等：按区域平均缩过去。写进复用缓冲，避免每帧分配。
        if (_presentScratch is null
            || _presentScratch.Width != width || _presentScratch.Height != height)
        {
            _presentScratch = new BgraFrame(width, height);
        }

        CanvasPainter.DownscaleBoxInto(canvas, _presentScratch);
        return _presentScratch;
    }

    /// <summary>玻璃层画布的复用副本（替代每帧 <c>_glassFrame.Clone()</c>）。</summary>
    private BgraFrame? _glassScratch;

    /// <summary>上屏帧的复用缓冲（尺寸校正后的结果）。</summary>
    private BgraFrame? _presentScratch;

    /// <summary>抓屏帧的复用缓冲（折射源）。见 <see cref="CaptureScenePixels"/>。</summary>
    private BgraFrame? _captureScratch;

    /// <summary>把玻璃层复制到复用的画布缓冲。</summary>
    private BgraFrame CopyGlassToScratch()
    {
        var src = _glassFrame!;

        if (_glassScratch is null
            || _glassScratch.Width != src.Width || _glassScratch.Height != src.Height)
        {
            _glassScratch = new BgraFrame(src.Width, src.Height);
        }

        Array.Copy(src.Pixels, _glassScratch.Pixels, src.Pixels.Length);
        return _glassScratch;
    }

    /// <summary>
    /// 把胶囊那一层按 alpha 叠加到画布上。
    ///
    /// <para>胶囊是<b>独立的第二个玻璃元素</b>：它有自己的渲染器、自己的累积缓冲，
    /// 因此移动它不会让整条任务栏重新累积（上游同样只重绘新旧胶囊区域）。
    /// 这里按 <c>src-over</c> 用胶囊的覆盖率做 alpha 混合。</para>
    /// </summary>
    private void CompositeCapsule(BgraFrame canvas)
    {
        if (_capsuleFrame is null || _capsuleVisibility <= 0.004) return;

        var src = _capsuleFrame;

        // 尺寸不一致说明这一层是上一次画布留下的（重建画布与胶囊渲染之间会短暂错开），
        // 硬叠会把两张不同尺寸的图按错误的步长互相覆盖 —— 正是"花屏"的成因。
        if (src.Width != canvas.Width || src.Height != canvas.Height) return;

        var sp = src.Pixels;
        var dp = canvas.Pixels;
        var vis = (float)Math.Clamp(_capsuleVisibility, 0, 1);

        for (var i = 0; i < dp.Length; i += 4)
        {
            var a = sp[i + 3];
            if (a == 0) continue;

            var alpha = (a / 255f) * vis;
            if (alpha <= 0f) continue;

            var inv = 1f - alpha;
            dp[i] = (byte)(sp[i] * alpha + dp[i] * inv);          // B
            dp[i + 1] = (byte)(sp[i + 1] * alpha + dp[i + 1] * inv); // G
            dp[i + 2] = (byte)(sp[i + 2] * alpha + dp[i + 2] * inv); // R
            var da = dp[i + 3];
            dp[i + 3] = (byte)Math.Min(255f, (alpha * 255f) + da * inv);
        }
    }

    /// <summary>强制下一次 Tick 重画图标层（前台切换、菜单关闭等场景）。</summary>
    public void InvalidateItemLayer() => _itemLayerDirty = true;

    /// <summary>场景变化计数（诊断用）。实时渲染是否真的在工作，看它增长就知道。</summary>
    public int SceneChangeCount => _sceneChangeCount;

    /// <summary>自研的液态玻璃开始菜单是否正开着。</summary>
    public bool StartMenuOpen => _startMenu?.IsOpen ?? false;

    /// <summary>自研开始菜单的面板矩形（未打开时为空矩形）。</summary>
    public Win32Rect StartMenuBounds => _startMenu is { IsOpen: true } menu ? menu.PanelBounds : default;

    /// <summary>切换自研开始菜单。菜单贴在任务栏上方，所以要把面板顶边传下去。</summary>
    public void ToggleStartMenu() =>
        SafeStartMenu(() =>
        {
            _quickPanel?.Close();     // 两块浮层互斥：开菜单先收面板
            _startMenu!.Toggle(_panelBounds.Y);
        }, "切换");

    /// <summary>
    /// 开始菜单是"附加功能"，它的任何异常都**不该拖垮整条任务栏**。
    ///
    /// <para>为什么必须有这层：开始菜单的 Tick 是在任务栏的渲染循环里调的，
    /// 异常会一路冒到主循环、直接把整个程序带走 —— 用户看到的就是"卡退"。
    /// 这里统一兜底并记日志，让它自己坏掉即可。</para>
    /// </summary>
    private void SafeStartMenu(Action action, string what)
    {
        if (_startMenu is null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"开始菜单异常（{what}）：{ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>开始菜单的玻璃层副本（不含图标文字），供自检取证。</summary>
    public BgraFrame? SnapshotStartMenuGlass() => _startMenu?.SnapshotGlassLayer();

    /// <summary>开始菜单的最终合成帧副本（含图标文字），供自检取证。</summary>
    public BgraFrame? SnapshotStartMenuComposite() => _startMenu?.SnapshotComposite();

    /// <summary>诊断：用指定雾化半径重渲染开始菜单的玻璃层（量化重影用）。</summary>
    public BgraFrame? RenderStartMenuGlassWithBlurRadius(double radius)
        => _startMenu?.RenderGlassWithBlurRadius(radius);

    /// <summary>
    /// 取一份<b>玻璃层</b>的副本，供离线取证。
    ///
    /// <para>为什么需要它：正常运行时会开启"排除自身捕获"，
    /// 那样截图里根本看不到玻璃，没法用截图证明玻璃有没有跟着场景变。
    /// 直接导出玻璃层本身，就绕开了这个矛盾 —— 这是渲染管线的真实输出，
    /// 不是自报的统计数字。</para>
    /// </summary>
    public BgraFrame? SnapshotGlassLayer() => _glassFrame?.Clone();

    /// <summary>
    /// 画悬停高光与"有窗口 / 前台"指示条。
    /// <paramref name="dy"/> 把"面板局部坐标"换算到"合成帧坐标"（胶囊溢出区的高度）。
    /// </summary>
    private void DrawItemDecoration(BgraFrame frame, TaskbarItem item, int dy)
    {
        var rect = item.Rect with { Y = item.Rect.Y + dy };

        if (item.IsHovered)
        {
            CanvasPainter.FillRoundedRect(frame,
                rect.X + 3, rect.Y + 4, rect.Width - 6, rect.Height - 8,
                Math.Min(12, rect.Height * 0.24),
                255, 255, 255, 34);
        }

        if (item.Kind != TaskbarItemKind.App || item.App is null) return;

        var app = item.App;

        // 指示条：有窗口 = 暗一点；前台窗口 = 亮 + 更宽。真实任务栏就是这个语汇。
        if (app.IsRunning)
        {
            var active = app.IsForeground;
            var width = active ? 16.0 : 8.0;
            var alpha = (byte)(active ? 235 : 130);
            var centerX = rect.X + rect.Width * 0.5;
            var centerY = rect.Bottom - 5.5;

            CanvasPainter.FillCapsule(frame, centerX, centerY, width, 2.5,
                255, 255, 255, alpha);
        }
    }

    /// <summary>
    /// 画图标与文字（图标在上、文字在下）。
    /// <paramref name="dy"/> 同 <see cref="DrawItemDecoration"/>：面板坐标 → 合成帧坐标。
    /// </summary>
    private void DrawItemContent(BgraFrame frame, TaskbarItem item, int dy)
    {
        var rect = item.Rect with { Y = item.Rect.Y + dy };
        var labelHeight = item.LabelBitmap?.Bitmap.Height ?? 0;
        var contentHeight = _settings.IconSize + 2 + labelHeight;
        var top = rect.Y + (rect.Height - contentHeight) * 0.5;

        switch (item.Kind)
        {
            case TaskbarItemKind.Start:
                CanvasPainter.DrawWindowsLogo(frame,
                    rect.X + (rect.Width - _settings.IconSize) * 0.5,
                    rect.Y + (rect.Height - _settings.IconSize) * 0.5,
                    _settings.IconSize,
                    235, 235, 240, 235);
                return;

            case TaskbarItemKind.TrayIcon:
                // 托盘项就是画一个字形，复用与快捷设置齿轮同一条路径。
                // ⚠️ 必须带深色描边：字形是浅色（232,232,238），玻璃条在亮色壁纸上
                // 也偏亮，浅字叠亮底几乎不可见 —— 这正是"托盘加上了却看不见"的
                // 第二次发作（第一次是 v3.4 的字体写死，这次是对比度不足）。
                DrawGlyph(frame, rect, item.Label, (int)(_settings.IconSize * 0.62),
                    "Segoe MDL2 Assets", outline: true);
                break;

            case TaskbarItemKind.QuickSettings:
                DrawGlyph(frame, rect, "\u2699", _settings.IconSize);   // ⚙
                return;

            case TaskbarItemKind.Clock:
                DrawClock(frame, item, rect);
                return;
        }

        // 图标
        if (item.Icon is not null)
        {
            var iconX = (int)(rect.X + (rect.Width - item.Icon.Width) * 0.5);
            var iconY = (int)(top + (_settings.IconSize - item.Icon.Height) * 0.5);
            CanvasPainter.Blit(frame, item.Icon, iconX, iconY,
                alphaScale: item.IsHovered ? 1.0 : 0.94);
        }

        // 文字：图标正下方，居中，字号很小。
        if (item.LabelBitmap is { } label)
        {
            var labelX = (int)(rect.X + (rect.Width - label.Bitmap.Width) * 0.5);
            var labelY = (int)(top + _settings.IconSize + 2);

            var tone = item.IsHovered || item.App?.IsForeground == true
                ? ((byte)255, (byte)255, (byte)255)
                : ((byte)214, (byte)216, (byte)222);

            // 先描一圈深色，保证在浅色玻璃上也能读清。
            CanvasPainter.Blit(frame, label.Bitmap, labelX, labelY + 1,
                ((byte)0, (byte)0, (byte)0), 0.45);
            CanvasPainter.Blit(frame, label.Bitmap, labelX, labelY, tone);
        }
    }

    /// <summary>用文字字形画一个符号（用于快捷设置按钮）。</summary>
    /// <summary>
    /// 在条目中央画一个字形。
    ///
    /// <para>⚠️ <paramref name="family"/> 必须与字形的编码匹配 ——
    /// 这里踩过坑：托盘用的是 <b>Segoe MDL2 Assets</b> 的私用区编码（E701 等），
    /// 而本方法原先写死 <c>Segoe UI Symbol</c>，那些编码在该字体里没有对应字符，
    /// 于是<b>画出来是空白</b>，表现为"托盘加上了但什么都看不见"。</para>
    ///
    /// <para><paramref name="outline"/>：先叠一圈深色偏移副本再画浅色本体。
    /// 玻璃条在亮色壁纸上整体偏亮，浅色字形不带描边就融进背景
    /// （实测 3× 放大截图里 ^ 网络 音量 电池几乎辨认不出）。</para>
    /// </summary>
    private void DrawGlyph(BgraFrame frame, PixelRect rect, string glyph, int size,
        string family = "Segoe UI Symbol", bool outline = false)
    {
        var raster = TextRasterizer.Render(glyph, size, size * 3, bold: false, family: family);
        if (raster is null) return;

        var x = (int)(rect.X + (rect.Width - raster.Bitmap.Width) * 0.5);
        var y = (int)(rect.Y + (rect.Height - raster.Bitmap.Height) * 0.5);
        if (outline)
        {
            CanvasPainter.Blit(frame, raster.Bitmap, x, y + 1,
                ((byte)20, (byte)22, (byte)30), 0.55);
        }
        CanvasPainter.Blit(frame, raster.Bitmap, x, y, ((byte)232, (byte)232, (byte)238));
    }

    /// <summary>
    /// 画时钟：时间在上（加粗）、日期在下（小字），整体在条目内垂直水平居中。
    ///
    /// <para>⚠️ 曾经只画时间一行且走通用文字路径 —— 通用路径把文字放在
    /// "图标下方"，而时钟没有图标，文字位置偏；加上浅色文字直接叠在亮玻璃上，
    /// 实测 3× 放大截图里"15:03"淡得几乎认不出。现在两行都带深色描边。</para>
    /// </summary>
    private void DrawClock(BgraFrame frame, TaskbarItem item, PixelRect rect)
    {
        var time = item.LabelBitmap;
        var date = item.SubLabelBitmap;
        if (time is null) return;

        var lineGap = 2;
        var blockHeight = time.Bitmap.Height + lineGap + (date?.Bitmap.Height ?? 0);
        var y = (int)(rect.Y + (rect.Height - blockHeight) * 0.5);

        var timeX = (int)(rect.X + (rect.Width - time.Bitmap.Width) * 0.5);
        CanvasPainter.Blit(frame, time.Bitmap, timeX, y + 1,
            ((byte)18, (byte)20, (byte)28), 0.6);
        CanvasPainter.Blit(frame, time.Bitmap, timeX, y, ((byte)248, (byte)250, (byte)255));

        if (date is not null)
        {
            var dateY = y + time.Bitmap.Height + lineGap;
            var dateX = (int)(rect.X + (rect.Width - date.Bitmap.Width) * 0.5);
            CanvasPainter.Blit(frame, date.Bitmap, dateX, dateY + 1,
                ((byte)18, (byte)20, (byte)28), 0.5);
            CanvasPainter.Blit(frame, date.Bitmap, dateX, dateY,
                ((byte)226, (byte)229, (byte)238));
        }
    }

    // ============================================================ 交互

    private void OnMouseMessage(uint msg, int x, int y)
    {
        if (_disposed) return;

        // 窗口比玻璃条上下各多了一条"胶囊溢出区"，所以客户区坐标并不等于面板坐标，
        // 要先把 y 换算回来，否则命中测试会整体偏上。
        var panelY = y - FrameDrawOffsetY;

        // 指针的归一化 x 驱动悬停胶囊 —— 对应上游的 setPointer(x, y)。
        // 注意：这条路当前在本机拿不到消息（见 UpdatePointerFromCursor 的说明），
        // 真正让胶囊动起来的是那里对 GetCursorPos 的轮询；这里留着不占便宜也不吃亏。
        if (_capsuleRenderer is not null)
        {
            _pointerInside = true;
            _pointerX = Math.Clamp(x / (double)Math.Max(1, _panelBounds.Width), 0, 1);
        }

        var index = HitTest(x, panelY);

        switch (msg)
        {
            case WM_MOUSEMOVE:
                UpdateHover(index);
                break;

            case WM_LBUTTONUP:
                InvokeItem(index);
                break;

            case WM_MBUTTONUP:
                // 中键：新开实例（与真实任务栏一致）
                var middle = GetItem(index);
                if (middle?.App is { } mApp && mApp.IsRunning) ShellActions.Launch(mApp);
                break;

            case WM_RBUTTONUP:
                ShowContextMenu(index);
                break;
        }
    }

    private void UpdateHover(int index)
    {
        if (index == _hoverIndex) return;

        _hoverIndex = index;
        _itemLayerDirty = true;   // 悬停高光只影响图标层；玻璃层要不要重算另说

        // 鼠标位置参与视差时才需要重算玻璃，否则省下这一大笔开销。
        if (_settings.MouseParallax) _glassDirty = true;

        // 悬停位置也参与视差，转成归一化坐标。
        if (index >= 0 && index < _items.Count)
        {
            var rect = _items[index].Rect;
            _lastMouseX = (float)((rect.X + rect.Width * 0.5) / Math.Max(1, _panelBounds.Width));
            _lastMouseY = 0.5f;
        }
        else
        {
            _lastMouseX = 0.5f;
            _lastMouseY = 0.5f;
        }
    }

    private void OnMouseLeft()
    {
        // 指针离开 → 胶囊淡出（即使没有悬停项也要切这个标志）。
        _pointerInside = false;

        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        _lastMouseX = 0.5f;
        _lastMouseY = 0.5f;
        _itemLayerDirty = true;
        if (_settings.MouseParallax) _glassDirty = true;
    }

    private int HitTest(int x, int y)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var r = _items[i].Rect;
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return i;
        }
        return -1;
    }

    private TaskbarItem? GetItem(int index) =>
        index >= 0 && index < _items.Count ? _items[index] : null;

    /// <summary>上一次真正生效的条目点击时刻，用于去抖。</summary>
    private long _lastInvokeTicks;

    /// <summary>
    /// 布局刚变化过的时间戳。晚于它的若干帧允许跳过上屏。
    ///
    /// <para>任务栏宽度/高度改变时，窗口尺寸、画布尺寸、内容坐标三者会在极短时间内
    /// 处于不同的世代。此期间上屏就会把不同世代的图叠在一起 ——
    /// 用户看到的就是"任务栏缩小时出现乱横线"。</para>
    /// </summary>
    private long _skipPresentUntilTicks;

    private void InvokeItem(int index)
    {
        var item = GetItem(index);
        if (item is null) return;

        // ⚠️ 去抖必须放在**这里** —— 这是所有点击路径的唯一汇合点。
        //
        // 踩过的坑：先去抖加在 PollPointerInput（轮询路径）上，以为够了；
        // 但 OnMouseMessage（鼠标消息路径）也会调 InvokeItem，**绕过了那道去抖**，
        // 于是用户日志里仍然是"打开 116ms 后又被 125ms 后的第二次点击关掉"。
        // 加在路径上就会漏路径；加在汇合点上才没有漏网之鱼。
        //
        // 为什么需要去抖：点击「开始」会同步执行 Open()，它抓屏 + 预热渲染，
        // 实测阻塞约 100ms。用户在这段时间里得不到反馈，本能再点一下 ——
        // 那一下会把刚打开的菜单立刻关掉。
        var now = Environment.TickCount64;
        if (now - _lastInvokeTicks < DefaultClickDebounceMs) return;
        _lastInvokeTicks = now;

        // 记一笔。点击走的是轮询而不是鼠标消息，日志是唯一能确认"动作真的被触发"的证据。
        _log($"点击 → {item.Kind}"
            + (string.IsNullOrEmpty(item.Label) ? string.Empty : $"「{item.Label}」"));

        switch (item.Kind)
        {
            case TaskbarItemKind.Start:
                // 有自研菜单就用它；面板顶边传进去，菜单永远贴在任务栏上方，不盖住它。
                // 注意这里是 Toggle：菜单开着时再点「开始」应当关掉。
                // 打开开始菜单的同时关掉快捷面板 —— 两块浮层互斥。
                if (_startMenu is not null)
                {
                    SafeStartMenu(() =>
                    {
                        _quickPanel?.Close();
                        _startMenu!.Toggle(_panelBounds.Y);
                    }, "切换");
                }
                else ShellActions.OpenStartMenu();
                break;

            case TaskbarItemKind.QuickSettings:
                // 齿轮 → 液态玻璃快捷面板（不再直接拉系统设置页）。
                ToggleQuickPanel();
                break;

            case TaskbarItemKind.TrayIcon:
                // 每个托盘图标做**它自己该做的事**，而不是统统丢给快捷设置。
                //
                // ⚠️ 之前这里一律调 OpenQuickSettings()，而它内部是合成 Win+A ——
                // 合成的按键会被本程序自己的 WH_KEYBOARD_LL 钩子吞掉，
                // 于是用户看到的就是"小托盘只有标志但点了没反应"。
                // 现在改成按图标分流到各自的系统页面（见 ShellActions 的说明）。
                InvokeTrayIcon(item);
                break;

            case TaskbarItemKind.Clock:
                // 点时钟打开日历/通知中心，跟真实任务栏一致。
                ShellActions.OpenNotificationCenter();
                break;

            case TaskbarItemKind.App when item.App is { } app:
                ShellActions.ActivateOrLaunch(app);
                AppInvoked?.Invoke(app);
                break;
        }
    }

    /// <summary>
    /// 托盘图标点击分流。
    ///
    /// <para>图标是用 <c>Segoe MDL2 Assets</c> 的字形画的（<c>\uE70E</c> 展开箭头、
    /// <c>\uE701</c> 网络、<c>\uE767</c> 音量、<c>\uE83F</c> 电池），
    /// 这里按字形把点击送到系统里对应的设置页面 —— 每个图标都有明确的反应，
    /// 而不是"点谁都弹同一个东西"。</para>
    /// </summary>
    private void InvokeTrayIcon(TaskbarItem item)
    {
        switch (item.Label)
        {
            case "\uE701":   // 网络
                ShellActions.OpenSettingsPage("ms-settings:network");
                break;

            case "\uE767":   // 音量
                ShellActions.OpenSettingsPage("ms-settings:sound");
                break;

            case "\uE83F":   // 电池
                ShellActions.OpenSettingsPage("ms-settings:batterysaver");
                break;

            default:         // "\uE70E" 展开箭头，以及未来新增的图标
                // 展开箭头对应真实任务栏的"显示隐藏的图标" ——
                // v3.8.0 起弹出液态玻璃快捷面板，不再直接拉系统设置页
                //（此前落到 ms-settings:quiethours，用户看到的是"点托盘冒出专注设置"）。
                ToggleQuickPanel();
                break;
        }
    }

    /// <summary>
    /// 开合快捷面板。开面板前先关开始菜单（两块浮层互斥）；
    /// 锚点用时钟项右缘（面板挂在托盘正上方）。
    /// </summary>
    private void ToggleQuickPanel()
    {
        if (_quickPanel is null) return;

        if (_quickPanel.IsOpen)
        {
            _quickPanel.Close();
            return;
        }

        SafeStartMenu(() => _startMenu?.Close(), "关闭");

        // 面板右缘对齐托盘簇右缘：时钟项的右缘；没有时钟就贴任务栏右缘。
        var clock = _items.FirstOrDefault(i => i.Kind == TaskbarItemKind.Clock);
        var anchorX = clock is not null
            ? _panelBounds.X + (int)clock.Rect.Right
            : _panelBounds.Right;

        _quickPanel.Open(anchorX, _panelBounds.Y);
    }

    /// <summary>
    /// 右键菜单。用原生 <c>TrackPopupMenu</c> 而不是自绘菜单 ——
    /// 原生菜单自带键盘导航、可访问性与系统主题，成本还低得多。
    /// </summary>
    private void ShowContextMenu(int index)
    {
        var item = GetItem(index);
        var isApp = item is { Kind: TaskbarItemKind.App, App: not null };

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            var cmdClose = 1;
            var cmdCloseAll = 2;
            var cmdReveal = 3;
            var cmdSettings = 4;
            var cmdStart = 5;

            if (isApp)
            {
                var app = item!.App!;
                AppendMenuW(menu, MF_STRING, (UIntPtr)cmdClose,
                    app.WindowCount > 1 ? $"关闭窗口（{app.WindowCount} 个中的 1 个）" : "关闭窗口");
                if (app.WindowCount > 1)
                {
                    AppendMenuW(menu, MF_STRING, (UIntPtr)cmdCloseAll, "关闭所有窗口");
                }
                if (!string.IsNullOrWhiteSpace(app.ProcessPath))
                {
                    AppendMenuW(menu, MF_STRING, (UIntPtr)cmdReveal, "打开文件所在位置");
                }
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            }
            else
            {
                AppendMenuW(menu, MF_STRING, (UIntPtr)cmdStart, "开始菜单（Win）");
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            }

            AppendMenuW(menu, MF_STRING, (UIntPtr)cmdSettings, "任务栏设置（固定 / 取消固定请在此操作）");

            var screenX = _panelBounds.X + (GetItem(index)?.Rect.X ?? 0);
            var screenY = _panelBounds.Y - 4;

            // TrackPopupMenu 要求调用方是前台窗口，否则菜单不会因为点到别处而消失。
            SetForegroundWindow(_window.Handle);

            var choice = TrackPopupMenu(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                (int)screenX, (int)screenY, 0, _window.Handle, IntPtr.Zero);

            if (choice == 0) return;   // 用户取消

            switch ((int)choice)
            {
                case 1 when isApp:
                    ShellActions.CloseWindow(PickFirstWindow(item!.App!));
                    break;
                case 2 when isApp:
                    ShellActions.CloseAllWindows(item!.App!);
                    break;
                case 3 when isApp:
                    ShellActions.ShowInFolder(item!.App!.ProcessPath);
                    break;
                case 4:
                    ShellActions.OpenTaskbarSettings();
                    TaskbarSettingsRequested?.Invoke();
                    break;
                case 5:
                    ShellActions.OpenStartMenu();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr PickFirstWindow(ShellApp app) =>
        app.Windows.Count > 0 ? app.Windows[0] : IntPtr.Zero;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _log($"替换任务栏收尾：{_governor.Describe()}，"
            + $"共处理 {_sceneChangeCount} 次场景变化；"
            + $"抓屏时隐藏/恢复 {_captureHideShowCount} 次"
            + $"{(_captureExclusionActive ? "（已启用自排除，不闪）" : "（静止桌面下应为 0）")}。");

        _startMenu?.Dispose();
        _quickPanel?.Dispose();
        _window.MouseMessage -= OnMouseMessage;
        _window.MouseLeft -= OnMouseLeft;
        _window.Dispose();
        _sampler.Dispose();
        _iconCache.Clear();
        _labelCache.Clear();
    }

    // ------------------------------------------------------------ 菜单声明

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
}
