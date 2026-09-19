using System.Linq;
using System.Runtime.InteropServices;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 自研的「液态玻璃开始菜单」，版式对齐 Windows 11 系统开始菜单。
///
/// <para><b>为什么是自研而不是改造系统开始菜单</b>：Windows 11 的开始菜单托管在
/// <c>Windows.UI.Composition.DesktopWindowContentBridge</c> 里，<b>不是经典 HWND</b> ——
/// 实测按 Win 键 / Ctrl+Esc / 点开始按钮都不产生任何可枚举的顶层窗口，
/// <c>StartMenuExperienceHost.exe</c> 一个顶层窗口都没有。
/// 没有窗口句柄，<c>DwmSetWindowAttribute</c> / <c>SetWindowCompositionAttribute</c>
/// 这类"改背景"的手段就没有下手点；何况它的背景本来就是 XAML 自绘的。</para>
///
/// <para><b>所以这里的取舍是</b>：<b>身子照系统的版式，皮肤是我们的玻璃</b> ——
/// 搜索框 / 已固定 / 推荐的项目 / 底部用户与电源，全部按 Windows 11 的视觉语言摆放，
/// 而背景是与任务栏同一套 <see cref="CpuGlassRenderer"/> 算出来的<b>真折射玻璃</b>
/// （不是 <c>backdrop-filter</c> 式的模糊）。</para>
///
/// <para><b>复用</b>：图标来自 <see cref="IconLoader"/>、文字来自 <see cref="TextRasterizer"/>、
/// 应用列表来自 <see cref="PinnedAppsReader"/> 与 <see cref="ShellWindowEnumerator"/>。</para>
/// </summary>
public sealed class GlassStartMenu : IDisposable
{
    /// <summary>开始菜单的外观与性能参数。</summary>
    public sealed record Settings
    {
        /// <summary>「已固定」网格每行几个应用。</summary>
        public int Columns { get; init; } = 6;

        public int TileWidth { get; init; } = 212;
        public int TileHeight { get; init; } = 212;

        /// <summary>网格图标边长。</summary>
        public int IconSize { get; init; } = 82;

        /// <summary>网格标签字号。</summary>
        public int LabelFontSize { get; init; } = 19;

        /// <summary>面板内边距。</summary>
        public int Padding { get; init; } = 45;

        /// <summary>面板圆角半径（<b>最终像素</b>，内部按玻璃倍率换算）。</summary>
        public int CornerRadius { get; init; } = 42;

        /// <summary>面板底边距任务栏顶边的间距。</summary>
        public int GapAboveTaskbar { get; init; } = 12;

        /// <summary>顶部搜索框高度。</summary>
        public int SearchBoxHeight { get; init; } = 68;

        /// <summary>「推荐的项目」最多显示几项（按 2 列铺开）。</summary>
        public int RecommendedMax { get; init; } = 4;

        /// <summary>推荐项的单项高度。</summary>
        public int RecommendedHeight { get; init; } = 102;

        /// <summary>底部（用户 / 电源）那一行的高度。</summary>
        public int BottomBarHeight { get; init; } = 90;

        /// <summary>
        /// 「全部应用」<b>每屏</b>最多列几项（不是总量上限）。
        ///
        /// <para>⚠️ 这里曾经是"总量上限"，配合界面没有翻页入口，
        /// 结果 168 个快捷方式只有 24 个能被看到 —— 用户的原话就是"无法查看所有应用"。
        /// 现在它的语义是<b>一屏的行数预算</b>：超出的部分由滚轮翻页查看，
        /// 完整列表绝不截断。</para>
        /// </summary>
        public int AllAppsPerScreen { get; init; } = 24;

        /// <summary>「全部应用」每行几项（默认与已固定对齐）。</summary>
        public int AllAppsColumns { get; init; } = 6;

        /// <summary>「全部应用」单项高度。</summary>
        public int AllAppsTileHeight { get; init; } = 118;

        /// <summary>「全部应用」图标边长。</summary>
        public int AllAppsIconSize { get; init; } = 48;

        /// <summary>
        /// 滚轮一格翻多少像素。默认 118（正好一项的高度），滚一格走一行。
        /// </summary>
        public int WheelStepPx { get; init; } = 118;

        /// <summary>抓屏边距（折射最远采样到边缘外约 40px，96 很安全）。</summary>
        public int SceneMargin { get; init; } = 96;

        /// <summary>
        /// <b>玻璃的渲染倍率</b>（相对面板最终尺寸）。默认 <c>0.4</c>。
        ///
        /// <para>这是让"大面板也能跑得动"的关键：玻璃是低频效果（雾化 + 折射），
        /// 按面板完整分辨率算是极大的浪费，而耗时与像素数成正比 ——
        /// 1000×1000 的面板在 1.0 倍率下要算 100 万像素，单帧就要 100ms 以上，界面直接卡死。
        /// 降到 0.4 就只剩 16 万像素，代价降 6 倍多。</para>
        ///
        /// <para>放大之后<b>才</b>画图标与文字，所以内容依然是清晰的；
        /// 只有玻璃本身略微变软 —— 而玻璃本来就是模糊的，看不出来。</para>
        /// </summary>
        /// <summary>
        /// 玻璃画布相对面板的缩放比。默认 0.25。
        ///
        /// <para><b>为什么必须压得很低</b>：玻璃是<b>低频</b>效果（雾化 + 折射 + 边缘透镜），
        /// 按面板完整分辨率算纯属浪费，而耗时与像素数成正比。</para>
        ///
        /// <para>更重要的是<b>上屏成本</b>：<c>UpdateLayeredWindow</c> 每帧要把整块面板
        /// 搬给 DWM。面板 1362×1270 = 173 万像素 = 6.9MB/帧 ——
        /// 实测这一次拷贝就要 <b>62ms</b>（见日志「合成 62」）。</para>
        ///
        /// <para>历史上这里放过 0.4，是因为当时面板只有 848×696（约 59 万像素）。
        /// 面板放大到 1362×1270 之后 0.4 倍仍有 27.7 万像素的玻璃计算量，
        /// 打开一次的总耗时涨到 211ms（= 内容 50 + 抓屏 72 + 预热 27 + 合成 62），
        /// 用户感知就是"太卡"。0.25 倍把玻璃计算量降到 10.8 万像素，
        /// 而观感几乎无差别（玻璃本来就没有高频细节）。</para>
        /// </summary>
        public double GlassScale { get; init; } = 0.25;

        /// <summary>每像素路径数。配合时间累积，1~2 条就够。</summary>
        public int PathsPerPixel { get; init; } = 2;

        /// <summary>时间累积帧上限。</summary>
        public int MaxAccumulation { get; init; } = 24;

        /// <summary>
        /// 打开时先算几帧再上屏。单帧采样少，直接显示会偏噪。
        ///
        /// <para>⚠️ 每一帧都是**阻塞**的。实测每帧约 12.5ms，所以 4 帧就是 50ms ——
        /// 这是打开耗时里最大的一块。默认降到 2 帧（约 25ms）：
        /// 剩下的干净度交给后续 tick 继续累积，用户几乎看不出差别，
        /// 但"点了没反应"的体感差别很大。</para>
        /// </summary>
        public int WarmupFrames { get; init; } = 1;

        /// <summary>
        /// 两次点击之间的最小间隔（毫秒）。默认 200。
        ///
        /// <para>为什么需要：菜单打开会阻塞一两百毫秒，用户以为"没反应"就会再点一下，
        /// 于是刚打开又被关掉（日志里能看到两次「点击 → Start」只隔 9ms）。
        /// 这个去抖让一次真实意图只生效一次。</para>
        /// </summary>
        public int ClickDebounceMs { get; init; } = 200;

        /// <summary>玻璃材质。圆角与下面三个"开始菜单专用"参数会覆盖它。</summary>
        public LiquidGlassMaterial? Material { get; init; }

        // ------------------------------------------------------------------
        //  开始菜单的玻璃 = 任务栏那套，但**光学尺度必须重新标定**。
        //
        //  上游所有"光学距离"都是按高度成比例定义的（edgeBand = 高度 × 比例），
        //  而那套比例是为 1204×116 的**细长任务栏**调的。
        //  直接套到 1362×1270 的竖长面板上，每一项都会被放大 10 倍：
        //
        //    EdgeBandRatio 0.26  → 边缘带 1270×0.26 ≈ 330px
        //                          面板四分之一的高度都在做折射+色散，
        //                          背景里任何高对比边缘都被染成贯穿全高的彩虹条纹。
        //    DispersionStrength 0.22 → 在这块大面上色散变成肉眼可见的"彩虹"。
        //
        //  实测证据（验收截图）：面板里能清楚看到背后窗口的按钮、滑块与侧边栏，
        //  并伴随红/青/绿的竖向色带 —— 正是用户说的"底层桌面应用和开始菜单应用重叠"。
        //
        //  修法是**加大模糊 + 压小边缘带 + 收敛色散**，而不是盖一层底色：
        //  模糊只抹掉背景的细节（高频），折射、高光、玻璃感全都留着；
        //  盖底色会把玻璃感一起压死 —— 那是上一版犯的错（用户："现在没有液态玻璃效果了"）。
        // ------------------------------------------------------------------

        /// <summary>
        /// 磨砂强度（向模糊结果混合的权重）。任务栏 0.29，面板 <b>1.0</b>。
        ///
        /// <para><b>为什么敢拉满</b>：面板<b>内部</b>的折射位移本来就接近 0
        /// （上游设计是"中心保持清晰，只有边缘产生透镜卷回"），
        /// 所以内部保留"锐利的折射结果"没有任何意义 —— 它只是把背景原样透出来。
        /// 拉满之后：内部 = 纯雾化（干净可读），边缘 = 折射 + 色散（玻璃感）。
        /// 这与 Windows 11 亚克力的做法一致。</para>
        ///
        /// <para>⚠️ 实际混合量还要乘 <see cref="FrostedAttenuation"/>（0.95）。
        /// 早先只把这一项从 0.29 提到 0.62 而没动衰减，实际只有 0.34 —— 背景照样清晰。</para>
        /// </summary>
        public double FrostedStrength { get; init; } = 1.0;

        /// <summary>
        /// 雾化衰减（材料级整体削弱系数）。任务栏 0.55，面板提到 <b>0.95</b>。
        ///
        /// <para>它与 <see cref="FrostedStrength"/> <b>相乘</b>决定最终混合量：
        /// <c>frostMix = blurWeight × FrostedStrength × FrostedAttenuation</c>。
        /// 只调其中一个很容易被另一个抵消掉，两个都要按面板尺寸重新标定。</para>
        /// </summary>
        public double FrostedAttenuation { get; init; } = 0.95;

        /// <summary>
        /// 模糊采样间距（<b>画布像素</b>）。任务栏 1.4，开始菜单 <b>28.0</b>。
        ///
        /// <para>这是解决"背景和菜单内容重叠"的主力参数。
        /// ⚠️ 注意单位是画布像素，而画布是面板的 <see cref="GlassScale"/> 倍 ——
        /// 在 0.25 倍画布上，28 画布像素 ≈ <b>112 屏幕像素</b>的采样间距，
        /// 9 抽头高斯等效模糊半径约 200px，足以把背景的排版结构也抹平。</para>
        ///
        /// <para>实测（背景区域的标准差，越小越"看不见"）：
        /// 16 → 11.5，28 → 更低。单靠加大 <see cref="FrostedStrength"/> 收益递减，
        /// 因为模糊只抹高频、保留低频，必须把半径真正加大。</para>
        /// </summary>
        public double BlurSpacingPx { get; init; } = 28.0;

        /// <summary>
        /// 雾化模糊的支撑半径（纹理像素）。<b>这是"重影"的根治参数。</b>默认 96。
        ///
        /// <para>用户报的"开始菜单有重影"，根因是模糊的<b>支撑宽度</b>不够：
        /// 旧的十字核只有 ±56 屏幕px 的支撑，而面板背后是 160–480px 周期的
        /// 文字排版，于是文字结构原样透出来。</para>
        ///
        /// <para>实测（512×512 正弦条纹，残余振幅，越小越看不见）：</para>
        /// <code>
        ///   周期T   点采样(旧)  半径24   半径48   半径96   半径128
        ///    40px     0.995     0.354    0.189    0.071    0.046
        ///    80px     0.999     0.676    0.364    0.130    0.091
        ///   160px     0.999     0.912    0.677    0.241    0.179
        ///   320px     0.818     0.952    0.928    0.579    0.424
        /// </code>
        ///
        /// <para>96 是折中：160px 级从 0.999 降到 0.241（<b>4 倍改善</b>），
        /// 同时不会把玻璃糊成一片毫无层次的纯色。</para>
        ///
        /// <para>实现走 <c>GlassScene</c> 的多级降采样金字塔，
        /// 所以调大<b>不会增加逐像素开销</b>。</para>
        /// </summary>
        public double FrostedBlurRadius { get; init; } = 96.0;

        /// <summary>
        /// 着色混合比例（向面板底色混合的权重）。任务栏 0.17，面板 <b>0.34</b>。
        ///
        /// <para>为什么面板需要更高：<b>模糊只抹掉高频，保留低频</b> ——
        /// 一张文档截图模糊之后，文字块的"灰度布局"依然看得见；
        /// 彩色壁纸模糊之后，色块依然在。所以光靠加大模糊半径收益递减。
        /// 提高着色比例能让面板底色更均匀，把残余的低频结构压下去，
        /// 同时保留半透明（不像加不透明底衬那样把玻璃感压死）。</para>
        /// </summary>
        public double TintMix { get; init; } = 0.34;

        /// <summary>
        /// 边缘折射带占玻璃高度的比例。任务栏用默认 <c>0.26</c>，
        /// 开始菜单必须压到 <b>0.05</b>。
        ///
        /// <para>这个比例乘以面板高度就是"边缘带"的像素宽度，而折射与色散
        /// <b>只在这一带里发生</b>。1270px 的面板配 0.26 会得到 330px 的巨带 ——
        /// 那正是彩色竖条纹的来源。0.05 给出约 63px，是一圈正常的玻璃边缘。</para>
        /// </summary>
        public double EdgeBandRatio { get; init; } = 0.05;

        /// <summary>
        /// 折射带占玻璃高度的比例 —— <b>这一带内雾化权重为 0</b>，只有折射。
        ///
        /// <para>⚠️ 它和 <see cref="EdgeBandRatio"/> 是<b>两个独立</b>的带：
        /// 前者控制"折射与色散的强度曲线"，这一项控制"从哪里开始有雾化"。
        /// 任务栏默认 0.16（116px → 18px，无所谓）；面板 1270px 下 0.16 = <b>203px</b> ——
        /// 意味着外圈 200 多像素全是"无雾化的清晰折射"，背景在那里完全裸露。
        /// 验收合成图里看到背景从边缘大片清晰透进来，就是它造成的。</para>
        ///
        /// <para>压到 <b>0.04</b>（约 51px），只留一圈很窄的纯折射边缘。</para>
        /// </summary>
        public double RefractionVisibleRatio { get; init; } = 0.04;

        /// <summary>
        /// 色散（RGB 三通道折射率差）强度。任务栏 0.22，开始菜单压到 <b>0.10</b>。
        ///
        /// <para>色散在细条上是精致的边缘彩边；在大面板上，背景的高对比边缘
        /// 会被拉成肉眼可见的彩虹竖带，喧宾夺主。</para>
        /// </summary>
        public double DispersionStrength { get; init; } = 0.10;

        /// <summary>
        /// 鼠标光感：是否让高光跟着鼠标走。
        ///
        /// <para>对应上游的 <c>setPointer(x, y)</c> —— 指针位置驱动镜面高光，
        /// 玻璃会像真的被一束光扫过。渲染器本来就支持（<c>MouseX/MouseY</c>），
        /// 任务栏用来做视差，这里用来做"光感"。</para>
        /// </summary>
        public bool MouseLight { get; init; } = true;

        /// <summary>鼠标光感的刷新间隔（毫秒）。玻璃渲染不便宜，必须节流。</summary>
        public int MouseLightIntervalMs { get; init; } = 120;

        /// <summary>
        /// 抓屏时是否藏掉**桌面图标**那一层（<c>SHELLDLL_DefView</c>）。默认 true。
        ///
        /// <para>用户要求："开始菜单只透出桌面背景和壁纸，不要透出桌面应用图标"。
        /// 桌面图标是独立的一层窗口，藏掉它再抓屏，玻璃里就只剩壁纸与窗口。</para>
        /// </summary>
        public bool HideDesktopIcons { get; init; } = true;

        /// <summary>
        /// 背景重抓间隔（毫秒）。默认 400。
        ///
        /// <para><b>为什么必须定期重抓</b>：抓屏只在打开时做一次的话，
        /// 玻璃里就是一张<b>冻住的快照</b> —— 用户的壁纸是 Wallpaper Engine 的动态壁纸，
        /// 桌面在动而玻璃不动，看起来就是"只渲染了一下"。
        /// 定期重抓才能让玻璃跟着活的桌面走。</para>
        ///
        /// <para>也不能太频繁：每次重抓都要把桌面图标临时藏一下，
        /// 抓完立刻恢复，太快会看出来在闪。400ms 是"够活"与"不闪"的平衡点。</para>
        /// </summary>
        public int BackdropRefreshMs { get; init; } = 600;

        /// <summary>
        /// 面板**底衬**的不透明度（0 = 纯玻璃全透，1 = 完全不透明）。默认 0.62。
        ///
        /// <para><b>为什么非有不可</b>：上游的玻璃模型是"边缘透镜 + 中间近乎透明"，
        /// 那是为任务栏那种 1204×116 的细长条设计的 —— 中间那块小得看不出来。
        /// 但开始菜单是一整块 1362×1168 的大面板，<b>中间那一大片"近乎透明"的区域
        /// 会把底下的窗口、桌面图标清晰地透上来，和菜单自己的文字图标叠在一起</b>，
        /// 根本读不了（用户原话："底层桌面应用和开始菜单应用重叠"）。</para>
        ///
        /// <para>底衬压在玻璃之上、内容之下：既保住了玻璃的折射与高光，
        /// 又把背景压成一片柔和底色。</para>
        ///
        /// <para><b>⚠️ 0.0（v3.8.0）</b>：历史上经历了 0.14 → 0.30 → 0.72 的反复 ——
        /// 0.72 是为了压住背景重影，但代价是菜单变成一片奶白，
        /// 和任务栏的纯玻璃观感完全脱节。用户明确要求
        /// "开始菜单要和任务栏一样的液态玻璃效果"，故归零；
        /// 文字可读性交给 <see cref="DrawTextAuto"/> 的亮度自适应
        /// （浅背景自动画深字、深背景自动画白字 + 反色微描边），
        /// 不再依赖底衬兜底。若某台机器背景过于杂乱，
        /// 可在配置 glassStartMenu.scrimOpacity 里调回 0.2~0.4。</para>
        /// </summary>
        public double ScrimOpacity { get; init; } = 0.0;

        /// <summary>底衬是否用浅色（浅色主题 true）。文字颜色会自动跟着它选深/浅。</summary>
        public bool ScrimIsLight { get; init; } = true;
    }

    private enum EntryKind
    {
        AppTile,
        Recommended,
        AllApp,
        User,
        Power,
    }

    /// <summary>面板上的一个可交互元素。</summary>
    private sealed class Entry
    {
        public required EntryKind Kind { get; init; }
        public PixelRect Rect { get; set; }
        public string Label { get; init; } = string.Empty;
        public BgraFrame? Icon { get; set; }
        public TextRasterizer.RasterizedText? LabelBitmap { get; set; }
        public TextRasterizer.RasterizedText? SubtitleBitmap { get; set; }
        public PinnedApp? App { get; init; }

        /// <summary>「全部应用」项：要启动的快捷方式路径。</summary>
        public string? ShortcutPath { get; init; }

        /// <summary>推荐项：点它就把那个窗口切到前台。</summary>
        public IntPtr Window { get; init; }
    }

    private readonly Settings _settings;
    private readonly Action<string>? _log;
    private readonly LayeredOverlayWindow _window;
    private readonly CpuGlassRenderer _renderer;
    private readonly ScreenSampler _sampler;
    private readonly List<Entry> _entries = [];

    // 静态文案的位图（每次打开重建，成本可忽略）。
    private TextRasterizer.RasterizedText? _searchPlaceholder;
    private TextRasterizer.RasterizedText? _pinnedHeader;
    private TextRasterizer.RasterizedText? _allAppsHint;
    private TextRasterizer.RasterizedText? _recommendedHeader;
    private TextRasterizer.RasterizedText? _allAppsHeader;
    private TextRasterizer.RasterizedText? _userName;
    private TextRasterizer.RasterizedText? _userSubtitle;
    private TextRasterizer.RasterizedText? _searchGlyph;
    private TextRasterizer.RasterizedText? _powerGlyph;
    private TextRasterizer.RasterizedText? _allAppsCount;
    private TextRasterizer.RasterizedText? _scrollHint;

    /// <summary>完整「全部应用」列表（全量，不截断）。</summary>
    private IReadOnlyList<MenuApp> _allApps = [];

    /// <summary>当前登录账户（微软账户 / 本地账户 + 头像）。</summary>
    private UserAccount? _account;

    /// <summary>底部头像的边长（像素）。</summary>
    private const int BottomAvatarSize = 40;

    private PixelRect _searchRect;
    private int _pinnedHeaderY;
    private int _recommendedHeaderY = -1;
    private int _allAppsHeaderY = -1;

    private GlassScene? _scene;
    private BgraFrame? _glassFrame;
    private Win32Rect _panelBounds;
    private int _canvasWidth = 1;
    private int _canvasHeight = 1;

    private bool _glassDirty = true;
    private bool _contentDirty = true;
    private int _hoverIndex = -1;

    // ---- 「全部应用」滚动 ----
    //
    // ⚠️ 为什么必须有它：本机有 168 个快捷方式，而面板一屏只放得下 24 个。
    // 早先把 AllAppsMax 当"总量上限"直接把数据砍到 24 个，又没有翻页入口 ——
    // 用户的原话就是"无法查看所有应用"。
    //
    // 现在的模型：**完整列表全在内存里**，_allAppsScroll 是一屏的滚动偏移（像素）。
    // 每个可见项的 Rect 都平移到 [列表区] 内，超出的项 Rect 落在这个区之外 ——
    // 于是既画不出来（绘制时按可见区裁剪），也点不到（命中测试会因坐标落在区外而失败）。
    private int _allAppsScroll;
    private int _allAppsContentHeight;
    private PixelRect _allAppsViewport;
    private bool _hasAllAppsViewport;

    // ---- 鼠标光感 ----
    private double _mouseX = 0.5;
    private double _mouseY = 0.5;
    private long _lastMouseLightTicks;
    private long _lastBackdropTicks;

    // ---- 轮询式输入 ----
    private bool _lastLeftDown;
    private bool _lastEscDown;
    private int _pressIndex = -1;

    /// <summary>上一次生效的点击时刻，用于去抖（见 <see cref="Settings.ClickDebounceMs"/>）。</summary>
    private long _lastClickTicks;

    /// <summary>
    /// 图标缓存，键为「路径|尺寸」。
    ///
    /// <para>为什么要缓存：每次打开菜单都要取几十个图标，而 <see cref="IconLoader.FromFile"/>
    /// 是实打实的文件 + 解码开销。不缓存的话 <c>Open()</c> 会阻塞几百毫秒，
    /// 用户感知就是"点了没反应"。</para>
    /// </summary>
    private static readonly Dictionary<string, BgraFrame> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 文字位图缓存。静态文案（段标题、占位文字、图标字形）每次打开都一样，
    /// 而 <see cref="TextRasterizer.Render"/> 每次都要建 DC + 位图 + 选字体，
    /// 实测 20 多次调用累计约 30ms —— 缓存掉之后接近免费。
    /// </summary>
    private static readonly Dictionary<string, TextRasterizer.RasterizedText> TextCache = [];

    private static TextRasterizer.RasterizedText? Text(
        string text, int size, int maxWidth, bool bold = false, string family = "Segoe UI")
    {
        var key = $"{text}|{size}|{maxWidth}|{bold}|{family}";
        lock (TextCache)
        {
            if (TextCache.TryGetValue(key, out var cached)) return cached;

            // ⚠️ 这里必须写全 TextRasterizer.Render —— 写成 Text 就是递归调用自己，
            // 会直接栈溢出（StackOverflowException 无法被 catch，进程当场消失）。
            var raster = TextRasterizer.Render(text, size, maxWidth, bold, family);
            if (raster is not null) TextCache[key] = raster;
            return raster;
        }
    }

    private static BgraFrame? LoadIconCached(string? path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var key = $"{path}|{size}";
        lock (IconCache)
        {
            if (IconCache.TryGetValue(key, out var cached)) return cached;

            var icon = IconLoader.FromFile(path, size);
            if (icon is not null) IconCache[key] = icon;
            return icon;
        }
    }

    /// <summary>
    /// 按需给「全部应用」项补上图标与文字位图（懒加载）。
    ///
    /// <para><b>为什么必须懒加载</b>：完整列表有 168 项，每项都要
    /// <c>SHGetFileInfo</c> + 位图解码 + 缩放，实测 20 多个图标就要 30~50ms；
    /// 168 个就是三四百毫秒，直接把"打开开始菜单"变成一次卡顿。</para>
    ///
    /// <para>而现在一屏只需要 24 个，滚动时才补下一屏 ——
    /// 于是"能看全部应用"和"打开要快"这两件事不再冲突。
    /// 已经补过的项不会再重算（<c>LabelBitmap is not null</c> 即已就绪）。</para>
    /// </summary>
    private void EnsureAllAppsVisuals(Entry entry)
    {
        if (entry.Kind != EntryKind.AllApp || entry.LabelBitmap is not null) return;

        var labelMax = Math.Max(40, _settings.TileWidth - 20);
        entry.Icon = LoadIconCached(entry.ShortcutPath, _settings.AllAppsIconSize);
        entry.LabelBitmap = Text(entry.Label, 12, labelMax);
    }

    private bool _disposed;

    /// <summary>覆盖层窗口是否已创建（<b>只创建一次</b>，之后复用）。</summary>
    private bool _windowCreated;

    public bool IsOpen { get; private set; }

    public Win32Rect PanelBounds => _panelBounds;

    /// <summary>面板上的元素总数（诊断用）。</summary>
    public int TileCount => _entries.Count;

    public GlassStartMenu(Settings? settings = null, Action<string>? log = null)
    {
        _settings = settings ?? new Settings();
        _log = log;

        // 圆角必须换算到画布像素 —— SDF 是在画布坐标系里算的，而画布是面板的 GlassScale 倍。
        // 磨砂 / 模糊 / 着色三项按开始菜单的需要加强（原因见 Settings 里的说明）。
        var material = (_settings.Material ?? LiquidGlassMaterial.Reference) with
        {
            CornerRadius = Math.Max(0, _settings.CornerRadius) * _settings.GlassScale,
            FrostedStrength = _settings.FrostedStrength,
            BlurSpacingPx = _settings.BlurSpacingPx,

            // 重影的根治项：雾化走大支撑的金字塔模糊，而不是 ±56px 的十字核。
            // 详见 GlassStartMenu.Settings.FrostedBlurRadius 里的实测表格。
            FrostedBlurRadius = _settings.FrostedBlurRadius,
            TintMix = _settings.TintMix,

            // ⚠️ 下面几项必须为大面板单独重新标定 —— 上游是按**细长任务栏**调的，
            //    而这类光学距离都写成"相对高度"的比例，面板高 10 倍就被放大 10 倍：
            //      · EdgeBandRatio 0.26 → 边缘带 = 1270 × 0.26 ≈ 330px，
            //        面板四分之一的高度都在做折射+色散，
            //        背景里任何高对比边缘都被染成贯穿全高的彩色条纹。
            //      · RefractionVisibleRatio 0.16 → 折射带 203px，而**这一带内雾化权重为 0** ——
            //        背景在外圈 200 多像素里完全清晰裸露。
            //      · FrostedAttenuation 与 FrostedStrength 相乘决定实际雾化量，
            //        只调后者会被前者抵消（0.62 × 0.55 = 0.34，背景依然清楚）。
            //      · DispersionStrength 0.22 在这么大的面积上会把色散放大成"彩虹"。
            EdgeBandRatio = _settings.EdgeBandRatio,
            RefractionVisibleRatio = _settings.RefractionVisibleRatio,
            FrostedAttenuation = _settings.FrostedAttenuation,
            DispersionStrength = _settings.DispersionStrength,
        };

        _renderer = new CpuGlassRenderer(material);
        _sampler = new ScreenSampler();

        _window = new LayeredOverlayWindow
        {
            Name = "liquidglass-start",
            TopMost = true,        // 必须浮在普通窗口之上
            ClickThrough = false,  // 自己收点击
            Log = log,
        };

        // 后台预热图标与文案缓存。
        //
        // 为什么值得做：首次打开菜单要冷启动解码二十多个图标，
        // 实测「内容」这一段就要 **510ms**（之后是 35ms）。这 0.5 秒用户是实打实要等的，
        // 而且他会以为"点了没反应"从而重复点击。启动时在后台线程先焐热，首次打开就快了。
        // 失败无所谓 —— 只是没有预热而已，不能因此影响主流程。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Prewarm();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"开始菜单：预热失败（不影响使用）—— {ex.Message}");
            }
        });

        // 滚轮翻页要靠全局钩子（覆盖层收不到 WM_MOUSEWHEEL，见 EnsureMouseWheelHook）。
        EnsureMouseWheelHook();
    }

    /// <summary>在后台把图标与文字位图先算一遍，填进缓存。</summary>
    private void Prewarm()
    {
        var labelMax = Math.Max(16, _settings.TileWidth - 12);

        // 固定应用
        foreach (var app in PinnedAppsReader.Read())
        {
            var iconFrom = !string.IsNullOrWhiteSpace(app.TargetPath) ? app.TargetPath : app.ShortcutPath;
            LoadIconCached(iconFrom, _settings.IconSize);
            Text(app.DisplayName, _settings.LabelFontSize, labelMax);
        }

        // 全部应用：⚠️ **只预热第一屏**，不是全部。
        //
        // 168 个图标全解码要三四百毫秒，而一屏只有 24 个可见 ——
        // 剩下的等滚到了再按需解码（见 EnsureAllAppsVisuals）。
        // 这里刻意只焐"首屏"，因为用户看到的永远是首屏。
        var firstScreen = Math.Max(1, _settings.AllAppsPerScreen);
        var allLabelMax = Math.Max(40, _settings.TileWidth - 20);
        foreach (var app in StartMenuAppsReader.Read(firstScreen))
        {
            LoadIconCached(app.ShortcutPath, _settings.AllAppsIconSize);
            Text(app.DisplayName, 12, allLabelMax);
        }

        // 推荐项（运行中应用，图标尺寸不同）
        foreach (var app in ShellWindowEnumerator.EnumerateApps().Where(a => a.Windows.Count > 0))
        {
            var iconFrom = !string.IsNullOrWhiteSpace(app.ProcessPath) ? app.ProcessPath : app.ShortcutPath;
            LoadIconCached(iconFrom, Math.Max(16, _settings.IconSize * 3 / 4));
            break;
        }

        // 账户（含头像解码 —— 这是后台线程的主要价值：WIC 解 1080px 的 jpg 要几十毫秒）
        try { AccountReader.WithAvatar(BottomAvatarSize); }
        catch { /* 预热失败无所谓 */ }

        // 静态文案
        var fullInner = Math.Max(80, _settings.Columns * _settings.TileWidth - 40);
        Text("在此输入以搜索", 12, fullInner);
        Text("已固定", 13, 200, bold: true);
        Text("所有应用  >", 12, 200);
        Text("推荐的项目", 13, 200, bold: true);
        Text("全部", 13, 200, bold: true);
        Text("\uE721", 14, 64, family: "Segoe MDL2 Assets");
        Text("\uE7E8", 15, 64, family: "Segoe MDL2 Assets");
    }

    // ============================================================ 开关

    public void Toggle(int bottomLimit)
    {
        if (IsOpen) Close();
        else Open(bottomLimit);
    }

    public void Open(int bottomLimit)
    {
        if (_disposed || IsOpen) return;

        // 计时：打开动作是**阻塞**渲染循环的，用户感知到的"点了没反应"就是它。
        // 分阶段计时，否则只能看到一个总数、不知道该优化哪一段。
        //
        // ⚠️ 五个阶段必须**互斥且穷尽**（内容+等待+抓屏+预热+合成 = 总耗时）。
        // 早先版本把"等 DWM 撤图标"的睡眠算进了预热，日志上写着"预热 65ms"，
        // 让人以为是渲染变慢了 —— 实际是 40ms 睡眠 + 25ms 渲染。
        // 日志是唯一的诊断依据，算错账等于把自己引向错误的方向。
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // ---- ① 先把桌面图标藏起来 ----
        //
        // 越早藏越好：DWM 把 SHELLDLL_DefView 撤下去需要一两帧（几十毫秒），
        // 而紧接着的内容枚举（读开始菜单快捷方式 + 解码图标）本来就要花约 30ms。
        // **让这两件事并行**，比"先干完活再睡 40ms 等它"省下整整一次等待。
        var hideWatch = System.Diagnostics.Stopwatch.StartNew();
        if (_settings.HideDesktopIcons && !_iconsHidden)
        {
            var view = DesktopIcons.FindView();
            _iconsHidden = DesktopIcons.Hide();

            // 这条日志很关键：桌面图标层找不到时，图标会原样透进玻璃，
            // 而用户明确要求"只透出桌面背景和壁纸，不要透出桌面应用图标"。
            _log?.Invoke("开始菜单：桌面图标层 hwnd=0x" + view.ToInt64().ToString("X")
                + "，" + (_iconsHidden ? "已隐藏"
                    : view == IntPtr.Zero ? "未找到（图标会透进玻璃）"
                    : "本就不可见"));
        }

        RefreshContent();
        if (_entries.Count == 0)
        {
            _log?.Invoke("开始菜单：没有可显示的应用（固定项为空），已取消打开。");
            return;
        }

        Layout(bottomLimit);
        RebuildCanvas();
        var contentMs = watch.ElapsedMilliseconds;

        // ---- ② 补足 DWM 撤图标所需的剩余等待 ----
        // 内容枚举通常已经吃掉了大部分时间，这里只补差额（常常是 0）。
        var settleMs = 0L;
        if (_iconsHidden)
        {
            var remaining = IconSettleMs - hideWatch.ElapsedMilliseconds;
            if (remaining > 0)
            {
                Thread.Sleep((int)remaining);
                settleMs = remaining;
            }
        }

        // ⚠️ 必须在 Show 之前抓屏：窗口一旦显示出来，抓到的就会包含我们自己，
        // 玻璃折射自己的画面会形成条纹递归。
        CaptureScene();   // 首次必然"有变化"（_scene 还是 null）
        var captureMs = watch.ElapsedMilliseconds - contentMs - settleMs;

        // 先算几帧再上屏：单帧只有 2 条路径，直接显示会偏噪。
        // 每一帧都是**阻塞**的，所以帧数要小 —— 配合低倍率玻璃，这几帧只要几毫秒。
        for (var i = 0; i < Math.Clamp(_settings.WarmupFrames, 0, 16); i++) RenderGlass();
        var warmupMs = watch.ElapsedMilliseconds - contentMs - settleMs - captureMs;

        // ⚠️ 顺序至关重要 —— 这是"重影"的根因（见下方长注释）。
        //
        //   先把**第一帧画好并上屏**，再让窗口可见。
        //   反过来做（先 Show 后 Present）会露出一个空窗期：
        //   窗口已经可见，但它的分层表面里还是**上一次打开时的旧像素**
        //   （窗口是复用的，Hide 并不清空 UpdateLayeredWindow 的内容），
        //   于是用户在那一瞬间会看到：
        //       旧的一帧（上次的内容）叠在 新的一帧（马上要画的）上面
        //   这就是"开始菜单有重影"。
        //
        //   而且这个空窗期不是零：紧接着的 CompositeAndPresent()
        //   要放大到面板尺寸（1362×1270 ≈ 6.9MB）+ 画全部内容 + 预乘 + UpdateLayeredWindow，
        //   实测 30-60ms。在 60Hz 上那是 2-4 帧的旧画面 —— 眼睛完全看得见。
        if (!_windowCreated)
        {
            _window.Create(_panelBounds.X, _panelBounds.Y, _panelBounds.Width, _panelBounds.Height);
            _windowCreated = true;
        }

        IsOpen = true;
        _glassDirty = true;
        _contentDirty = true;
        _hoverIndex = -1;
        _pressIndex = -1;
        _lastLeftDown = false;

        // 每次打开都从「全部应用」的顶部开始 —— 上次滚到一半的位置留到下次会很怪。
        _allAppsScroll = 0;

        // 先画好第一帧（此时窗口还不可见，所以抓屏抓不到自己）。
        CompositeAndPresent();

        // 有了内容才显示。注意 BringToTop 也要放在 Show 之前 ——
        // 否则窗口会先在"可能被别的窗口盖住的层级"上闪一下。
        _window.BringToTop();
        _window.Show();

        watch.Stop();

        var totalMs = watch.ElapsedMilliseconds;
        var compositeMs = totalMs - contentMs - settleMs - captureMs - warmupMs;

        _log?.Invoke($"开始菜单：已打开（{CountOf(EntryKind.AppTile)} 个固定 + "
            + $"{CountOf(EntryKind.Recommended)} 个推荐 + {CountOf(EntryKind.AllApp)} 个全部，"
            + $"面板 {_panelBounds.Width}x{_panelBounds.Height}，玻璃 {_canvasWidth}x{_canvasHeight}）。"
            + $"耗时 {totalMs}ms = 内容 {contentMs} + 等待 {settleMs} + 抓屏 {captureMs}"
            + $" + 预热 {warmupMs} + 合成 {compositeMs}");
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;

        // ⚠️ 关闭时**把分层表面擦干净**，别留着上一帧。
        //
        // 原因同 Open() 里的顺序问题：窗口是复用的，SW_HIDE 只是不显示，
        // **不会清掉 UpdateLayeredWindow 写入的那份像素**。
        // 下次打开时如果 Show 早于第一帧 Present（比如别处又改了顺序、
        // 或者 DWM 提前把它合成出来），显示的就是这次关闭时残留的旧画面 ——
        // 观感正是"重影"。
        //
        // 这里主动上一张全透明帧：即使将来顺序被谁改动，残留内容也是"什么都没有"，
        // 而不是"上一次菜单的样子"。这是**防御性**的，成本只有一次 memset。
        ClearSurface();

        _window.Hide();

        // 把桌面图标还回去。这是 Open() 里藏的那一次的唯一配对恢复点，
        // 漏掉的话用户的图标就永久消失了。
        if (_iconsHidden)
        {
            DesktopIcons.Restore(true);
            _iconsHidden = false;
        }

        _hoverIndex = -1;
        _log?.Invoke("开始菜单：已关闭。");
    }

    /// <summary>把分层表面清成全透明（防止复用窗口残留上一帧造成"重影"）。</summary>
    private void ClearSurface()
    {
        try
        {
            var w = _panelBounds.Width;
            var h = _panelBounds.Height;
            if (w <= 0 || h <= 0) return;

            if (_clearFrame is null || _clearFrame.Width != w || _clearFrame.Height != h)
            {
                _clearFrame = new BgraFrame(w, h);
            }

            // 全 0 = 全透明（BGRA 四通道都是 0）。Present 会预乘一次，
            // 而 0 乘任何数还是 0，所以它上屏后依然是"完全看不见"。
            Array.Clear(_clearFrame.Pixels);
            _window.Present(_clearFrame, _panelBounds.X, _panelBounds.Y);
        }
        catch
        {
            // 清理失败无所谓 —— 它只是防御措施，不该影响关闭流程。
        }
    }

    /// <summary>清屏用的复用缓冲。</summary>
    private BgraFrame? _clearFrame;

    private int CountOf(EntryKind kind) => _entries.Count(e => e.Kind == kind);

    // ============================================================ 内容

    /// <summary>采集内容：已固定的应用（网格）+ 最近使用的应用（推荐）+ 底部用户与电源。</summary>
    private void RefreshContent()
    {
        _entries.Clear();

        var iconSize = _settings.IconSize;
        var labelMax = Math.Max(16, _settings.TileWidth - 12);

        // ---- 已固定 ----
        try
        {
            foreach (var app in PinnedAppsReader.Read())
            {
                // 图标优先取 exe（比 .lnk 清晰），取不到再退回快捷方式。
                var iconFrom = !string.IsNullOrWhiteSpace(app.TargetPath) ? app.TargetPath : app.ShortcutPath;

                _entries.Add(new Entry
                {
                    Kind = EntryKind.AppTile,
                    Label = app.DisplayName,
                    App = app,
                    Icon = LoadIconCached(iconFrom, iconSize),
                    LabelBitmap = Text(app.DisplayName, _settings.LabelFontSize, labelMax),
                });
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"开始菜单：读取固定应用失败 —— {ex.Message}");
        }

        // ---- 推荐的项目：正在运行、且有窗口的应用 ----
        try
        {
            var running = ShellWindowEnumerator.EnumerateApps()
                .Where(a => a.Windows.Count > 0)
                .Take(Math.Max(0, _settings.RecommendedMax))
                .ToList();

            var recIcon = Math.Max(16, _settings.IconSize * 3 / 4);
            var recTextMax = Math.Max(60, _settings.TileWidth - recIcon - 20);

            foreach (var app in running)
            {
                var iconFrom = !string.IsNullOrWhiteSpace(app.ProcessPath) ? app.ProcessPath : app.ShortcutPath;

                _entries.Add(new Entry
                {
                    Kind = EntryKind.Recommended,
                    Label = app.DisplayName,
                    Window = app.Windows[0],
                    Icon = LoadIconCached(iconFrom, recIcon),
                    LabelBitmap = Text(app.DisplayName, 16, recTextMax),
                    SubtitleBitmap = Text("最近使用", 14, recTextMax),
                });
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"开始菜单：读取最近使用失败 —— {ex.Message}");
        }

        // ---- 全部应用：开始菜单 Programs 目录里的快捷方式 ----
        //
        // ⚠️ **读全量、不截断**。本机实测有 168 个快捷方式（120 个全机器 + 48 个当前用户，
        // 去掉重名与噪音后进入列表）。早先这里传的是 AllAppsMax=24，等于把 144 个直接丢掉，
        // 而界面上没有任何翻页入口 —— 用户看到的"所有应用"永远只有 24 个，
        // 原话就是"无法查看所有应用"。
        //
        // 全量读入的成本可以忽略：只枚举目录 + 取文件名（实测 <10ms），
        // **不解码任何图标** —— 图标是懒加载的（见 EnsureIcon），
        // 这是"能全量加载"的前提，否则 168 次 SHGetFileInfo + 位图解码要几秒。
        try
        {
            _allApps = StartMenuAppsReader.Read();

            // 一屏放得下多少项：由面板高度决定（见 Layout）。
            // 这里先把 Entry 建出来但**不填 Rect** —— Rect 要等布局算完才有值。
            foreach (var app in _allApps)
            {
                _entries.Add(new Entry
                {
                    Kind = EntryKind.AllApp,
                    Label = app.DisplayName,
                    ShortcutPath = app.ShortcutPath,
                });
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"开始菜单：读取全部应用失败 —— {ex.Message}");
        }

        // ---- 底部：用户与电源 ----
        //
        // ⚠️ 不能用 Environment.UserName 就完事 —— 那是**本地账户名**。
        // 本机实测 Environment.UserName = "1"，而用户实际是用微软账户
        // 3259344218@qq.com 登录的，于是左下角只显示"1"，
        // 用户的原话就是"只显示本地账号没有微软账号"。
        // 正确的名字与头像由 AccountReader 从 IdentityCRL 注册表 + AccountPictures 读取。
        var account = AccountReader.WithAvatar(BottomAvatarSize);
        _account = account;

        _userName = Text(account.DisplayName, 16, 240);
        _userSubtitle = Text(account.Subtitle, 13, 240);

        if (!account.IsMicrosoftAccount)
        {
            _log?.Invoke("开始菜单：当前是本地账户登录（未检测到微软账户）。"
                + $"显示名「{account.DisplayName}」。");
        }
        else
        {
            _log?.Invoke($"开始菜单：检测到微软账户「{account.MicrosoftAccount}」，"
                + $"显示名「{account.DisplayName}」，"
                + $"头像{(account.Avatar is null ? "未取到（用占位圆）" : $"{account.Avatar.Width}x{account.Avatar.Height} 已就绪")}。");
        }

        _entries.Add(new Entry { Kind = EntryKind.User, Label = account.DisplayName });
        _entries.Add(new Entry { Kind = EntryKind.Power, Label = "电源" });

        // ---- 静态文案 ----
        var fullInner = Math.Max(80, _settings.Columns * _settings.TileWidth - 40);
        _searchPlaceholder = Text("在此输入以搜索", 12, fullInner);
        _pinnedHeader = Text("已固定", 13, 200, bold: true);
        _allAppsHint = Text("所有应用  >", 12, 200);
        _recommendedHeader = Text("推荐的项目", 13, 200, bold: true);
        _allAppsHeader = Text("全部", 13, 200, bold: true);
        _allAppsCount = Text($"共 {_allApps.Count} 个应用 · 滚轮翻看", 12, 320);
        _scrollHint = Text("滚动查看更多", 12, 200);

        // Segoe MDL2 Assets 是 Windows 自带的图标字体：E721 = 搜索，E7E8 = 电源。
        _searchGlyph = Text("\uE721", 14, 64, family: "Segoe MDL2 Assets");
        _powerGlyph = Text("\uE7E8", 15, 64, family: "Segoe MDL2 Assets");
    }

    // （AvailablePanelHeight 已删除：它按 98% 屏幕高估算可用空间，没扣任务栏预留，
    //   数值偏大约 132px，是"面板盖住任务栏"回归的帮凶之一。
    //   现在可用高度在 Layout 里按"面板底边 − 屏幕顶边"精确计算。）

    /// <summary>把滚动偏移夹到合法范围（0 ~ 内容高 - 视口高）。</summary>
    private void ClampScroll()
    {
        var viewport = _hasAllAppsViewport ? _allAppsViewport.Height : 0;
        var max = Math.Max(0.0, _allAppsContentHeight - viewport);
        _allAppsScroll = Math.Clamp(_allAppsScroll, 0, (int)max);
    }

    /// <summary>「全部应用」当前是否可以滚动（内容比视口高）。</summary>
    private bool CanScrollAllApps =>
        _hasAllAppsViewport && _allAppsContentHeight > _allAppsViewport.Height;

    /// <summary>
    /// 滚动「全部应用」。返回是否真的动了（没动就不必重画）。
    ///
    /// <para>滚动后必须<b>重新布局</b>：每个项的 Rect 都依赖 <c>_allAppsScroll</c>，
    /// 只改数字不动 Rect 的话，画出来的还是旧位置。</para>
    /// </summary>
    private bool ScrollAllApps(int deltaPixels)
    {
        if (!CanScrollAllApps) return false;

        var before = _allAppsScroll;
        _allAppsScroll += deltaPixels;
        ClampScroll();

        if (_allAppsScroll == before) return false;

        RelayoutAllAppsRects();
        return true;
    }

    /// <summary>
    /// 只重算「全部应用」各项的 Y 坐标（滚动时用）。
    ///
    /// <para>不走整个 <see cref="Layout"/>：那个会重算面板位置与尺寸，
    /// 而滚动<b>不该改变面板大小</b> —— 面板高度由"一屏"决定，滚动只是换内容。
    /// 重跑 Layout 会引入无谓的计算，还可能因为 bottomLimit 的取整让面板轻微跳动。</para>
    /// </summary>
    private void RelayoutAllAppsRects()
    {
        if (!_hasAllAppsViewport) return;

        var allApps = _entries.Where(e => e.Kind == EntryKind.AllApp).ToList();
        var allColumns = Math.Clamp(_settings.AllAppsColumns, 1, 12);
        if (allColumns <= 0) return;

        // 视口左边 = 面板内边距；这里用第一项的 X 反推，避免再存一份状态。
        var baseX = _settings.Padding;
        var cellWidth = _allAppsViewport.Width / allColumns;
        var baseY = _allAppsViewport.Y - _allAppsScroll;

        for (var i = 0; i < allApps.Count; i++)
        {
            var c = i % allColumns;
            var r = i / allColumns;
            allApps[i].Rect = new PixelRect(
                baseX + c * cellWidth,
                baseY + r * _settings.AllAppsTileHeight,
                cellWidth,
                _settings.AllAppsTileHeight);
        }
    }

    /// <summary>按 Windows 11 的版式排布：搜索框 → 已固定 → 推荐的项目 → 全部 → 底部行。</summary>
    private void Layout(int bottomLimit)
    {
        var pad = _settings.Padding;
        var columns = Math.Clamp(_settings.Columns, 1, 12);

        // ---- 先算"面板底边能到哪"（必须在视口收纳之前）----
        //
        // bottomLimit 是调用方传入的任务栏顶边：面板底边必须停在它上方，
        // 这是"开始菜单不遮住任务栏"的物理保证。调用方可能给出无效值
        // （0 / default），此时退回"屏幕底边"。
        //
        // ⚠️ 这段计算原来在 Layout 末尾 —— 末尾才算就意味着视口收纳用的
        // "可用高度"是错的（用的是 98% 屏幕高，没扣掉任务栏那 132px），
        // 内容会比真实空间多出一条，最终只能靠末尾的钳制硬拗，把面板推进任务栏。
        var monitor = DisplayEnvironment.GetPrimaryMonitor();
        var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
        if (bounds.IsEmpty) bounds = DisplayEnvironment.GetVirtualDesktopBounds();
        if (bottomLimit <= bounds.Top) bottomLimit = bounds.Bottom;

        var bottom = Math.Min(bottomLimit - _settings.GapAboveTaskbar,
                              bounds.Bottom - _settings.GapAboveTaskbar);

        // 面板顶边最低能到屏幕顶边 —— 可用总高就这么多。视口收纳以此为准。
        var availableHeight = bottom - bounds.Top;

        var apps = _entries.Where(e => e.Kind == EntryKind.AppTile).ToList();
        var recs = _entries.Where(e => e.Kind == EntryKind.Recommended).ToList();

        var appRows = Math.Max(1, (apps.Count + columns - 1) / columns);
        const int recColumns = 2;
        var recRows = recs.Count == 0 ? 0 : (recs.Count + recColumns - 1) / recColumns;

        var width = pad * 2 + columns * _settings.TileWidth;

        var y = pad;
        _searchRect = new PixelRect(pad, y, width - pad * 2, _settings.SearchBoxHeight);
        y += _settings.SearchBoxHeight + 18;

        _pinnedHeaderY = y;
        y += 24;

        for (var i = 0; i < apps.Count; i++)
        {
            var c = i % columns;
            var r = i / columns;
            apps[i].Rect = new PixelRect(
                pad + c * _settings.TileWidth,
                y + r * _settings.TileHeight,
                _settings.TileWidth,
                _settings.TileHeight);
        }
        y += appRows * _settings.TileHeight + 18;

        _recommendedHeaderY = recRows > 0 ? y : -1;
        if (recRows > 0)
        {
            y += 24;
            var recWidth = (width - pad * 2) / recColumns;

            for (var i = 0; i < recs.Count; i++)
            {
                var c = i % recColumns;
                var r = i / recColumns;
                recs[i].Rect = new PixelRect(
                    pad + c * recWidth,
                    y + r * _settings.RecommendedHeight,
                    recWidth,
                    _settings.RecommendedHeight);
            }
            y += recRows * _settings.RecommendedHeight + 12;
        }

        // ---- 全部 ----
        //
        // ⚠️ 这里是"看不到所有应用"的修复核心。
        //
        // 早先的写法：把 168 项**全部**平铺成一长条（20 行 × 118px = 2360px），
        // 而面板高度是由"内容总高"倒推出来的 —— 于是得到一块 1362×3370 的面板，
        // 比屏幕还高。Layout 末尾的 bottom/top 计算把它顶出屏幕，用户只能看到最上面一小截。
        //
        // 现在改成**固定高度的滚动视口**：
        //   · 视口高度 = 一屏的项数（AllAppsPerScreen）× 单项高度，且**受屏幕剩余高度限制**
        //   · 完整列表全在内存里，_allAppsScroll 是一屏的偏移
        //   · 每个项的 Rect 都加上视口基点再减去滚动量
        //   · 滚轮改 _allAppsScroll，超出的项 Rect 落在视口外 → 既不画也点不到
        var allApps = _entries.Where(e => e.Kind == EntryKind.AllApp).ToList();
        var allColumns = Math.Clamp(_settings.AllAppsColumns, 1, 12);
        var allRows = allApps.Count == 0 ? 0 : (allApps.Count + allColumns - 1) / allColumns;

        _allAppsHeaderY = allRows > 0 ? y : -1;
        if (allRows > 0)
        {
            y += 26;

            // 视口里放几行：AllAppsPerScreen 是**项数**预算，换算成行。
            var perScreenRows = Math.Max(1, _settings.AllAppsPerScreen / allColumns);
            var visibleRows = Math.Min(allRows, perScreenRows);

            // ⚠️ 再按"屏幕还剩多少地方"收一次。
            //
            // 面板总高 = 视口上方的一切 + 视口 + 底部栏。可用高度是**真实的**
            // "面板底边 − 屏幕顶边"（已扣掉任务栏预留），而不是 98% 屏幕高。
            // 不做这一步收纳，面板会超出可用空间，底部的账户与电源就永远点不到。
            var available = availableHeight - y - _settings.BottomBarHeight - pad;
            var maxRows = Math.Max(1, available / Math.Max(1, _settings.AllAppsTileHeight));

            if (visibleRows > maxRows)
            {
                visibleRows = maxRows;
            }

            var viewportHeight = visibleRows * _settings.AllAppsTileHeight;
            var cellWidth = (width - pad * 2) / allColumns;

            _allAppsViewport = new PixelRect(pad, y, width - pad * 2, viewportHeight);
            _hasAllAppsViewport = true;

            // 内容总高（全部项铺完有多高）与可滚动范围。
            _allAppsContentHeight = allRows * _settings.AllAppsTileHeight;
            ClampScroll();

            var baseY = y - _allAppsScroll;

            for (var i = 0; i < allApps.Count; i++)
            {
                var c = i % allColumns;
                var r = i / allColumns;
                allApps[i].Rect = new PixelRect(
                    pad + c * cellWidth,
                    baseY + r * _settings.AllAppsTileHeight,
                    cellWidth,
                    _settings.AllAppsTileHeight);
            }

            y += viewportHeight + 12;
        }
        else
        {
            _hasAllAppsViewport = false;
            _allAppsContentHeight = 0;
        }

        var user = _entries.First(e => e.Kind == EntryKind.User);
        var power = _entries.First(e => e.Kind == EntryKind.Power);
        user.Rect = new PixelRect(pad, y + 8, 300, _settings.BottomBarHeight - 16);
        power.Rect = new PixelRect(width - pad - 60, y + 8, 60, _settings.BottomBarHeight - 16);

        y += _settings.BottomBarHeight + pad;

        var top = bottom - y;

        // ⚠️ 兜底：面板顶边绝不能跑到屏幕上边之外。**只**保护顶边。
        //
        // ⚠️⚠️ 这里曾经写成 `ceiling = Math.Max(bounds.Top, bounds.Bottom - y)`，
        // 那不是"保护顶边"，而是"强制面板底边 = 屏幕底边"：
        // 只要 bottom < bounds.Bottom（永远成立 —— 底边停在任务栏上方），
        // top = bottom − y 就必然小于 bounds.Bottom − y，钳制**每次打开都触发**，
        // 把面板整体推到底边贴住屏幕。后果是 GapAboveTaskbar 成了死代码，
        // 开始菜单直接盖住任务栏（v3.7.1 修复的回归，实测面板底边 = 屏幕底边 1600）。
        //
        // 正确的语义只有一条：top 越过屏幕顶边时夹回来。
        // "内容放不下"的正确解法是缩小视口（上面 available 已用真实可用高度算过），
        // 不是把面板整体推下去。
        if (top < bounds.Top) top = bounds.Top;

        var left = bounds.Left + (bounds.Width - width) / 2;

        // 同理，横向也不能超出屏幕。
        if (left < bounds.Left) left = bounds.Left;
        if (left + width > bounds.Right) left = Math.Max(bounds.Left, bounds.Right - width);

        _panelBounds = new Win32Rect(left, top, left + width, top + y);
    }

    private void RebuildCanvas()
    {
        var ss = _settings.GlassScale;
        _canvasWidth = Math.Max(1, (int)Math.Round(_panelBounds.Width * ss));
        _canvasHeight = Math.Max(1, (int)Math.Round(_panelBounds.Height * ss));
        _renderer.Reset();

        // ⚠️ **不要**在这里丢弃 _scene。
        //
        // _scene 是"玻璃背后的世界"这张纹理，与画布尺寸无关 —— 画布重建
        // （面板尺寸变化 / 缩放比变化）并不代表背景变了。而 Open() 每次都会调
        // 本方法，早先这里置 _scene = null 就等于**每打开一次菜单就重新分配
        // 一块 27MB 的线性缓冲**（1554×1462×3×4），全部落进 LOH。
        //
        // 现在的处理：保留 _scene，由 CaptureScene 用 TryUpdateFromBgra 原地刷新
        // （尺寸不符时它会返回 false，那时才真正新建）。
        _glassFrame = null;
        _glassDirty = true;
        _contentDirty = true;
    }

    /// <summary>上一次抓到的背景签名，用来判断"背景到底变了没有"。</summary>
    private long _sceneSignature;

    /// <summary>
    /// 藏起桌面图标后，DWM 需要多久才真正把它从画面上撤下去（毫秒）。
    ///
    /// <para>不等的话第一帧抓到的还是带图标的旧画面，玻璃里会出现图标残影。
    /// 40ms 约等于 2–3 帧 vsync，是实测够用的值。</para>
    ///
    /// <para>⚠️ 但**不要**直接睡满 40ms：<see cref="Open"/> 会先发起隐藏，
    /// 再去干内容枚举那约 30ms 的活，最后只补差额。
    /// 串行"先睡 40ms 再干活"会白白多花 40ms。</para>
    /// </summary>
    private const int IconSettleMs = 40;

    /// <summary>
    /// 桌面图标是否已被本菜单藏起（打开时藏、关闭时恢复）。
    ///
    /// <para>它是一个<b>菜单生命周期级别</b>的开关，不是每次抓屏一次的开关。
    /// 每次抓屏都藏一遍会让图标疯狂闪烁并把 CPU 烧在 SHELLDLL_DefView 的重绘上。</para>
    /// </summary>
    private bool _iconsHidden;

    /// <summary>
    /// 抓一次背景。
    ///
    /// <para>返回<b>背景是否真的变了</b>。没变的话调用方不应该重置累积缓冲、
    /// 也不应该重画内容 —— 否则每 600ms 就会重来一遍，
    /// 用户看到的是"开始菜单在反复渲染自己的内容"，同时 CPU 被吃光。</para>
    /// </summary>
    private bool CaptureScene()
    {
        var margin = _settings.SceneMargin;

        // ⚠️ 桌面图标**不再在这里藏**。
        //
        // 旧写法是"抓屏前藏、抓完立刻恢复"，每次抓屏都强制 Sleep(30ms) 等 DWM 撤下图标。
        // 后果是灾难性的：BackdropRefreshMs=600 时，桌面图标每 0.6 秒闪一次，
        // 而且每次 ShowWindow(SW_SHOW) 都会让 SHELLDLL_DefView 整层重绘 ——
        // 用户看到的就是"开始菜单在反复渲染自己的内容"，同时整机发卡。
        //
        // 正确的做法是由 Open()/Close() 在菜单生命周期的两端各动一次
        // （见 _iconsHidden 字段），抓屏期间背景保持不变，也就不用等、不用闪。
        // 抓屏到复用缓冲（场景 1554×1462 ≈ 9MB，逐次分配会持续冲击 LOH）。
        var cw = _panelBounds.Width + margin * 2;
        var ch = _panelBounds.Height + margin * 2;
        if (_captureScratch is null
            || _captureScratch.Width != cw || _captureScratch.Height != ch)
        {
            _captureScratch = new BgraFrame(cw, ch);
        }

        _sampler.CapturePaddedInto(
            _captureScratch, _panelBounds.X - margin, _panelBounds.Y - margin);
        var frame = _captureScratch;

        // 廉价签名：隔行隔列采样，足够判断"背景动没动"。
        var signature = ComputeSignature(frame);
        if (signature == _sceneSignature && _scene is not null)
        {
            return false;   // 背景一模一样，什么都不用做
        }

        _sceneSignature = signature;

        // 尺寸不变时**原地刷新**场景，复用那块 27MB 的线性缓冲
        // （1554×1462×3×4 字节 —— 远超 85KB，必进 LOH 且不压缩，
        //   每次打开都新分配会造成碎片化与内存虚高）。
        var scale = (float)_settings.GlassScale;
        if (_scene is null || !_scene.TryUpdateFromBgra(frame, -margin, -margin, scale))
        {
            _scene = GlassScene.FromBgra(frame, -margin, -margin, null, scale);
        }

        _renderer.Reset();
        _glassDirty = true;
        return true;
    }

    /// <summary>背景的廉价签名（隔行隔列采样求校验和）。</summary>
    private static long ComputeSignature(BgraFrame frame)
    {
        long sum = 0;
        var pixels = frame.Pixels;
        var stride = frame.Stride;

        for (var y = 0; y < frame.Height; y += 16)
        {
            var row = y * stride;
            for (var x = 0; x < frame.Width; x += 16)
            {
                var i = row + x * 4;
                sum += pixels[i] + (pixels[i + 1] << 1) + (pixels[i + 2] << 2) + (i * 31L);
            }
        }

        return sum;
    }

    // ============================================================ 渲染

    private void RenderGlass()
    {
        if (_scene is null) return;

        _glassFrame = _renderer.Render(_scene, new GlassRenderOptions
        {
            GlassRect = new PixelRect(0, 0, _canvasWidth, _canvasHeight),
            CanvasWidth = _canvasWidth,
            CanvasHeight = _canvasHeight,
            PathsPerPixel = Math.Clamp(_settings.PathsPerPixel, 1, 12),
            MaxAccumulation = Math.Max(4, _settings.MaxAccumulation),
            // 鼠标光感：指针位置驱动镜面高光（上游的 setPointer）。
            MouseX = (float)_mouseX,
            MouseY = (float)_mouseY,
        });

        _glassDirty = false;
    }

    /// <summary>
    /// 累积期间的上屏间隔（毫秒）。
    ///
    /// <para>玻璃在累积去噪时画面在逐帧趋稳，不需要每次都搬给 DWM ——
    /// 面板 1362×1270 时单次上屏 62ms，每帧都上就是纯粹的浪费。
    /// 100ms（10Hz）足够让收敛过程看起来是平滑的，却省下 90% 的上屏成本。</para>
    /// </summary>
    private const int PresentIntervalWhileAccumulating = 100;

    /// <summary>上一次上屏的时间戳，用于累积期间的限频。</summary>
    private long _lastPresentTicks;

    /// <summary>是否已经为"当前这次收敛"上过屏（避免收敛后反复上同一帧）。</summary>
    private bool _presentedConverged;

    /// <summary>
    /// 内容层缓存（尺寸 = 上屏尺寸）。
    ///
    /// <para><b>为什么要有它</b>：<see cref="DrawContent"/> 要光栅化十几个应用名、
    /// 缩放二十多个图标、画几十个圆角矩形，一次要十几毫秒。而鼠标悬停时
    /// 只有"哪一项被高亮"变了，玻璃和其余内容**一个像素都没动**。
    /// 每帧把整块面板重画一遍是纯浪费 —— 这正是"开始菜单太卡了"的主因。</para>
    ///
    /// <para>策略：内容没变就复用这张缓存，只把玻璃重算的结果与它合成。</para>
    /// </summary>
    private BgraFrame? _contentLayer;

    /// <summary>内容绘制时用来判断"底下亮不亮"的参考帧（带底色的合成帧，不是透明内容层）。</summary>
    private BgraFrame? _luminanceRef;

    /// <summary>低分辨率画布的复用副本（避免每次上屏都 Clone 一份）。</summary>
    private BgraFrame? _glassScratch;

    /// <summary>
    /// 上屏帧的复用缓冲。
    ///
    /// <para><b>为什么必须复用</b>：面板 1362×1270×4 ≈ <b>6.9MB</b>，超过 85KB 就会进
    /// <b>LOH（大对象堆）</b>，而 LOH 默认不压缩。累积去噪期间 10Hz 上屏
    /// = 约 70MB/s 的 LOH 分配 —— 会造成碎片化、私有内存虚高、以及间歇性 GC 卡顿。
    /// 实测 100 次开合后的私有内存波动在 -288MB ~ +271MB 之间，正是这种大对象 churn 的典型表现。</para>
    ///
    /// <para>⚠️ 复用它的前提是 <c>Present</c> <b>不会修改传入的帧</b> ——
    /// 为此 <see cref="LayeredOverlayWindow"/> 的预乘已改成"读源写目标"。</para>
    /// </summary>
    /// <summary>上屏帧的复用缓冲。</summary>
    private BgraFrame? _panelFrame;

    /// <summary>抓屏帧的复用缓冲（折射源，尺寸 = 面板 + 两倍场景边距）。</summary>
    private BgraFrame? _captureScratch;

    private void CompositeAndPresent()
    {
        if (_glassFrame is null) return;

        // ① 先在**低分辨率**画布上压底衬。
        //
        // 底衬是逐通道仿射变换 out = in·k + c，而放大是双线性插值（权重和为 1），
        // 两者**可交换**：
        //     scrim(interp(x)) = k·Σwᵢxᵢ + c = Σwᵢ(k·xᵢ + c) = interp(scrim(x))
        // 所以在 340x292 上做底衬，和放大到 1362x1168 再做，结果一致（仅差舍入 1），
        // 但计算量少 **16 倍**（面板/玻璃 = 4×4）。
        var canvas = CopyGlassToScratch();
        ApplyScrim(canvas);

        // ② 放大到面板尺寸（写入复用缓冲）
        var frame = ScaleToPanel(canvas);
        if (frame is null) return;

        // ③ 内容层：**只在尺寸变化时分配**，重画时清空而不是重新 new。
        //
        // ⚠️ 原来是"每次 _contentDirty 就 new 一张 6.9MB" —— 而 _contentDirty
        // 每次打开菜单都会被置位，于是 100 次开合就分配了 690MB 进 LOH。
        // 新建的 BgraFrame 内容全 0，等价于"清空"，所以复用时显式清一次即可。
        if (_contentLayer is null
            || _contentLayer.Width != frame.Width || _contentLayer.Height != frame.Height)
        {
            _contentLayer = new BgraFrame(frame.Width, frame.Height);
            _contentDirty = true;
        }

        if (_contentDirty)
        {
            Array.Clear(_contentLayer.Pixels);
            _luminanceRef = frame;
            try { DrawContent(_contentLayer); }
            finally { _luminanceRef = null; }
            _contentDirty = false;
        }

        BlendOver(frame, _contentLayer);
        _window.Present(frame, _panelBounds.X, _panelBounds.Y);
    }

    /// <summary>把玻璃层复制到复用的低分辨率缓冲（替代 <c>_glassFrame.Clone()</c>）。</summary>
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
    /// 把画布缩放到面板尺寸。
    ///
    /// <para>玻璃按 <see cref="Settings.GlassScale"/> 低倍率渲染，这里放大回去。
    /// 放大用的是定点实现的 <see cref="CanvasPainter.UpscaleBilinear"/> ——
    /// 它的 x 方向系数只算一次，内层是纯整数运算。</para>
    /// </summary>
    private BgraFrame? ScaleToPanel(BgraFrame canvas)
    {
        var w = _panelBounds.Width;
        var h = _panelBounds.Height;
        if (w <= 0 || h <= 0) return null;

        // 尺寸一致就原样返回，省掉一次无谓的拷贝。
        // （Present 不会修改传入的帧，所以直接给复用缓冲是安全的。）
        if (canvas.Width == w && canvas.Height == h) return canvas;

        // 缩小是罕见路径（GlassScale > 1 才会走到），允许它分配。
        if (canvas.Width > w && canvas.Height > h)
        {
            return CanvasPainter.DownscaleBox(canvas, w, h);
        }

        // 放大是常态：写进复用缓冲，避免每次上屏都分配 6.9MB 的大对象。
        if (_panelFrame is null || _panelFrame.Width != w || _panelFrame.Height != h)
        {
            _panelFrame = new BgraFrame(w, h);
        }

        CanvasPainter.UpscaleBilinearInto(canvas, _panelFrame);
        return _panelFrame;
    }

    /// <summary>
    /// 把 <paramref name="top"/> 以它的 alpha 叠加到 <paramref name="bottom"/> 上（source-over）。
    ///
    /// <para>内容层是带 alpha 的透明画布，所以必须按 alpha 混合，
    /// 不能直接逐字节覆盖 —— 否则每块文字周围会出现黑色方块。</para>
    /// </summary>
    private static void BlendOver(BgraFrame bottom, BgraFrame top)
    {
        var count = Math.Min(bottom.Pixels.Length, top.Pixels.Length);
        var b = bottom.Pixels;
        var t = top.Pixels;

        for (var i = 0; i + 3 < count; i += 4)
        {
            var a = t[i + 3];
            if (a == 0) continue;                 // 全透明：跳过，省下绝大部分像素
            if (a == 255)
            {
                b[i] = t[i]; b[i + 1] = t[i + 1]; b[i + 2] = t[i + 2];
                continue;
            }

            var inv = 255 - a;
            b[i] = (byte)((t[i] * a + b[i] * inv) / 255);
            b[i + 1] = (byte)((t[i + 1] * a + b[i + 1] * inv) / 255);
            b[i + 2] = (byte)((t[i + 2] * a + b[i + 2] * inv) / 255);
        }
    }

    /// <summary>
    /// 在玻璃之上压一层半透明底衬。
    ///
    /// <para>顺序是「玻璃 → 底衬 → 内容」：底衬必须压在玻璃之上（否则压不住透上来的背景），
    /// 又必须在内容之下（否则会把图标文字一起糊掉）。</para>
    ///
    /// <para>只改 RGB、不动 alpha —— alpha 由玻璃的覆盖率决定，
    /// 那是圆角与边缘抗锯齿的依据，改了会让面板边缘出锯齿。</para>
    /// </summary>
    private void ApplyScrim(BgraFrame frame)
    {
        var opacity = Math.Clamp(_settings.ScrimOpacity, 0, 1);
        if (opacity <= 0.001) return;

        // 浅色底衬用接近白的暖灰，深色底衬用接近黑的冷灰。
        var (b, g, r) = _settings.ScrimIsLight
            ? ((byte)250, (byte)249, (byte)248)
            : ((byte)22, (byte)23, (byte)26);

        var keep = 1.0 - opacity;
        var pixels = frame.Pixels;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * keep + b * opacity);
            pixels[i + 1] = (byte)(pixels[i + 1] * keep + g * opacity);
            pixels[i + 2] = (byte)(pixels[i + 2] * keep + r * opacity);
        }
    }

    /// <summary>把 Windows 11 开始菜单的版式画到玻璃帧之上。</summary>
    private void DrawContent(BgraFrame frame)
    {
        // 内容现在画在一张**透明**的缓存层上（alpha 全 0），
        // 而"这个区域亮不亮"必须看**底下的合成帧**才有意义 ——
        // 在透明层上求亮度会永远得到 0，搜索框就会永远走深色分支。
        // 所以亮度参考帧单独传入：合成路径传上屏帧，取证路径也传上屏帧。
        var refFrame = _luminanceRef ?? frame;

        // ---- 搜索框：一块比玻璃略亮的圆角底 + 放大镜 + 占位文字 ----
        var brightBackdrop = AverageLuminance(refFrame, _searchRect.X, _searchRect.Y,
            _searchRect.Width, _searchRect.Height) > 0.55;

        var fill = brightBackdrop
            ? ((byte)255, (byte)255, (byte)255, (byte)150)
            : ((byte)255, (byte)255, (byte)255, (byte)38);

        CanvasPainter.FillRoundedRect(frame,
            _searchRect.X, _searchRect.Y, _searchRect.Width, _searchRect.Height,
            _searchRect.Height / 2.0, fill.Item1, fill.Item2, fill.Item3, fill.Item4);

        if (_searchGlyph is not null)
        {
            CanvasPainter.Blit(frame, _searchGlyph.Bitmap,
                (int)Math.Round(_searchRect.X + 14),
                (int)Math.Round(_searchRect.Y + (_searchRect.Height - _searchGlyph.Height) / 2),
                (30, 30, 36), 0.85);
        }

        if (_searchPlaceholder is not null)
        {
            DrawTextAuto(frame, _searchPlaceholder,
                _searchRect.X + 40,
                _searchRect.Y + (_searchRect.Height - _searchPlaceholder.Height) / 2,
                dim: true);
        }

        // ---- 段标题 ----
        if (_pinnedHeader is not null) DrawTextAuto(frame, _pinnedHeader, _settings.Padding, _pinnedHeaderY);
        if (_allAppsHint is not null)
        {
            DrawTextAuto(frame, _allAppsHint,
                _panelBounds.Width - _settings.Padding - _allAppsHint.Width, _pinnedHeaderY, dim: true);
        }

        if (_recommendedHeaderY >= 0 && _recommendedHeader is not null)
        {
            DrawTextAuto(frame, _recommendedHeader, _settings.Padding, _recommendedHeaderY);
        }

        if (_allAppsHeaderY >= 0 && _allAppsHeader is not null)
        {
            DrawTextAuto(frame, _allAppsHeader, _settings.Padding, _allAppsHeaderY);

            // 「共 168 个应用 · 滚轮翻看」——把"总共有多少"直接写出来。
            // 用户报"无法查看所有应用"时，连"一共该有多少个"都是未知的；
            // 把总数摊在标题行上，他立刻能判断是不是全都在。
            if (_allAppsCount is not null)
            {
                var hintX = _panelBounds.Width - _settings.Padding - _allAppsCount.Width;
                DrawTextAuto(frame, _allAppsCount, hintX, _allAppsHeaderY, dim: true);
            }
        }

        // ---- 条目 ----
        for (var i = 0; i < _entries.Count; i++)
        {
            var hovered = i == _hoverIndex;
            var entry = _entries[i];

            // ⚠️ 「全部应用」必须按**视口裁剪**：完整列表有 168 项，
            // 超出视口的那部分 Rect 落在视口之外，不裁的话会画到面板的其他区域上
            // （盖住"已固定"、压到底部栏上）。
            if (entry.Kind == EntryKind.AllApp)
            {
                if (!VisibleInAllAppsViewport(entry.Rect)) continue;

                // 懒加载：滚到哪儿才解码到哪儿（见 EnsureAllAppsVisuals）。
                EnsureAllAppsVisuals(entry);
            }

            switch (entry.Kind)
            {
                case EntryKind.AppTile:
                    DrawAppTile(frame, entry, hovered);
                    break;
                case EntryKind.Recommended:
                    DrawRecommended(frame, entry, hovered);
                    break;
                case EntryKind.AllApp:
                    DrawAllAppTile(frame, entry, hovered);
                    break;
            }
        }

        // ---- 滚动提示与滚动条 ----
        DrawAllAppsScrollIndicator(frame);

        // ---- 底部行 ----
        DrawBottomBar(frame);
    }

    /// <summary>该项是否落在「全部应用」视口内（含一点点余量，避免边缘项被硬切）。</summary>
    private bool VisibleInAllAppsViewport(PixelRect rect)
    {
        if (!_hasAllAppsViewport) return false;

        var v = _allAppsViewport;

        // 只要有一像素落在视口高度范围内就画（绘制内部还会被 FillRoundedRect 等裁剪）。
        return rect.Bottom > v.Y && rect.Y < v.Bottom;
    }

    /// <summary>
    /// 画「全部应用」的滚动指示：右侧一条细滚动条 + 顶部/底部"还有更多"的提示。
    ///
    /// <para><b>为什么非画不可</b>：一台机器可能有 168 个应用而一屏只显示 24 个。
    /// 没有任何指示的话，用户根本不知道下面还有东西 —— 这正是"无法查看所有应用"
    /// 这个反馈的另一半（前半是数据被截断，后半是没有入口也看不出来）。</para>
    /// </summary>
    private void DrawAllAppsScrollIndicator(BgraFrame frame)
    {
        if (!CanScrollAllApps) return;

        var v = _allAppsViewport;
        var max = _allAppsContentHeight - v.Height;
        if (max <= 0) return;

        // ---- 右侧滚动条 ----
        const int barWidth = 5;
        const int barGap = 4;
        var barX = v.Right - barWidth - barGap;

        // 轨道（很淡），让用户知道"这里有东西"。
        CanvasPainter.FillRoundedRect(frame,
            barX, v.Y + 4, barWidth, v.Height - 8,
            barWidth / 2.0, 0, 0, 0, 22);

        // 滑块：高度按"视口 / 内容"比例，位置按滚动比例。
        var thumbHeight = Math.Max(36, (int)(v.Height * (v.Height / (double)_allAppsContentHeight)));
        var travel = v.Height - 8 - thumbHeight;
        var thumbY = v.Y + 4 + (int)Math.Round(travel * (_allAppsScroll / (double)max));

        var (tb, tg, tr) = _settings.ScrimIsLight
            ? ((byte)30, (byte)30, (byte)36)
            : ((byte)235, (byte)235, (byte)240);

        CanvasPainter.FillRoundedRect(frame,
            barX, thumbY, barWidth, thumbHeight,
            barWidth / 2.0, tb, tg, tr, 110);

        // ---- 顶部/底部渐隐提示：告诉用户"上面/下面还有" ----
        var hint = _scrollHint;
        if (hint is null) return;

        var dimTop = _allAppsScroll > 4;
        var dimBottom = _allAppsScroll < max - 4;

        if (dimTop)
        {
            DrawTextAuto(frame, hint, v.X + 4, v.Y + 4, dim: true);
        }

        if (dimBottom)
        {
            var x = (int)Math.Round(v.X + (v.Width - hint.Width) / 2.0 - 30);
            DrawTextAuto(frame, hint, x, v.Bottom - hint.Height - 4, dim: true);
        }
    }

    private void DrawBottomBar(BgraFrame frame)
    {
        var userIndex = _entries.FindIndex(e => e.Kind == EntryKind.User);
        var powerIndex = _entries.FindIndex(e => e.Kind == EntryKind.Power);

        if (userIndex >= 0)
        {
            var user = _entries[userIndex];
            if (_hoverIndex == userIndex)
            {
                FillHighlight(frame, user.Rect.X - 6, user.Rect.Y,
                    user.Rect.Width + 12, user.Rect.Height, 8);
            }

            // ---- 头像 ----
            //
            // 优先用账户的真实头像（%PUBLIC%\AccountPictures 里的 jpg，
            // 由 AccountReader 解码）。取不到时画一个占位圆 ——
            // 占位圆里放名字首字母，比一个空白圆更有信息量。
            var avatarSize = BottomAvatarSize;
            var avatarX = user.Rect.X;
            var avatarY = user.Rect.Y + (user.Rect.Height - avatarSize) / 2.0;
            var avatar = _account?.Avatar;

            if (avatar is not null && avatar.Width == avatarSize)
            {
                // 头像本身是矩形的 jpg，裁成圆形才像系统开始菜单。
                BlitCircular(frame, avatar, (int)Math.Round(avatarX), (int)Math.Round(avatarY));
            }
            else
            {
                CanvasPainter.FillRoundedRect(frame,
                    avatarX, avatarY, avatarSize, avatarSize, avatarSize / 2.0,
                    255, 255, 255, 46);

                // 占位：名字首字母（中文取第一个字）。
                var initial = FirstGlyph(_account?.DisplayName ?? "?");
                var glyph = Text(initial, 18, 40);
                if (glyph is not null)
                {
                    CanvasPainter.Blit(frame, glyph.Bitmap,
                        (int)Math.Round(avatarX + (avatarSize - glyph.Width) / 2.0),
                        (int)Math.Round(avatarY + (avatarSize - glyph.Height) / 2.0),
                        (255, 255, 255), 0.85);
                }
            }

            // ---- 名字 + 副标题（微软账户邮箱 / "本地账户"） ----
            var textX = avatarX + avatarSize + 12;
            var name = _userName;
            var subtitle = _userSubtitle;

            var totalHeight = (name?.Height ?? 0) + (subtitle is null ? 0 : subtitle.Height + 2);
            var textY = user.Rect.Y + (user.Rect.Height - totalHeight) / 2.0;

            if (name is not null)
            {
                DrawTextAuto(frame, name, textX, textY);
                textY += name.Height + 2;
            }

            if (subtitle is not null)
            {
                DrawTextAuto(frame, subtitle, textX, textY, dim: true);
            }
        }

        if (powerIndex >= 0)
        {
            var power = _entries[powerIndex];
            if (_hoverIndex == powerIndex)
            {
                FillHighlight(frame, power.Rect.X, power.Rect.Y,
                    power.Rect.Width, power.Rect.Height, 8);
            }

            if (_powerGlyph is not null)
            {
                CanvasPainter.Blit(frame, _powerGlyph.Bitmap,
                    (int)Math.Round(power.Rect.X + (power.Rect.Width - _powerGlyph.Width) / 2),
                    (int)Math.Round(power.Rect.Y + (power.Rect.Height - _powerGlyph.Height) / 2),
                    (30, 30, 36), 0.85);
            }
        }
    }

    /// <summary>取名字的第一个字形（中文取第一个字，英文取第一个字母）。</summary>
    private static string FirstGlyph(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "?";

        var trimmed = text.Trim();

        // 跳过代理对的一半（emoji 之类的名字），取第一个完整的字符。
        if (trimmed.Length >= 2 && char.IsSurrogatePair(trimmed[0], trimmed[1]))
        {
            return trimmed[..2];
        }

        return trimmed[..1].ToUpperInvariant();
    }

    /// <summary>
    /// 把一张矩形位图按<b>圆形</b>画出来（圆形外的不画）。
    ///
    /// <para>账户头像是方形的 jpg，直接贴上去是个方块；系统开始菜单里是圆的。
    /// 这里逐像素判断到圆心的距离，边缘一像素做抗锯齿 —— 因为头像的源尺寸通常
    /// 大于目标尺寸（我们用 448 的源缩到 40），直接硬切会有明显锯齿。</para>
    /// </summary>
    private static void BlitCircular(BgraFrame dest, BgraFrame source, int x, int y)
    {
        var size = Math.Min(source.Width, source.Height);
        var radius = size / 2.0;
        var center = radius - 0.5;

        var dp = dest.Pixels;
        var sp = source.Pixels;
        var dStride = dest.Stride;
        var sStride = source.Stride;

        for (var sy = 0; sy < size; sy++)
        {
            var dy = y + sy;
            if (dy < 0 || dy >= dest.Height) continue;

            for (var sx = 0; sx < size; sx++)
            {
                var dx = x + sx;
                if (dx < 0 || dx >= dest.Width) continue;

                // 到圆心的距离；r 是采样点到圆边的距离（正 = 在外面）。
                var distX = sx - center;
                var distY = sy - center;
                var r = Math.Sqrt(distX * distX + distY * distY) - radius;

                // 边缘 1 像素做抗锯齿：r 在 [-1, 0] 之间时覆盖率线性过渡。
                double coverage = r <= -1 ? 1.0 : r >= 0 ? 0.0 : 1.0 + r;
                if (coverage <= 0) continue;

                var si = sy * sStride + sx * 4;
                var di = dy * dStride + dx * 4;

                var srcAlpha = sp[si + 3] / 255.0;
                var a = coverage * srcAlpha;

                if (a >= 1.0)
                {
                    dp[di] = sp[si]; dp[di + 1] = sp[si + 1]; dp[di + 2] = sp[si + 2];
                    dp[di + 3] = 255;
                    continue;
                }

                if (a <= 0) continue;

                var inv = 1.0 - a;
                dp[di] = (byte)(sp[si] * a + dp[di] * inv);
                dp[di + 1] = (byte)(sp[si + 1] * a + dp[di + 1] * inv);
                dp[di + 2] = (byte)(sp[si + 2] * a + dp[di + 2] * inv);
                dp[di + 3] = (byte)Math.Min(255, dp[di + 3] + 255 * a);
            }
        }
    }

    /// <summary>
    /// 悬停高亮。颜色跟着底衬走 —— 浅色底衬上用深色、深色底衬上用亮色，
    /// 否则"浅底 + 白色高亮"等于没有高亮。
    /// </summary>
    private void FillHighlight(BgraFrame frame, double x, double y, double w, double h, double radius)
    {
        var (b, g, r) = _settings.ScrimIsLight
            ? ((byte)16, (byte)16, (byte)20)
            : ((byte)255, (byte)255, (byte)255);

        CanvasPainter.FillRoundedRect(frame, x, y, w, h, radius, b, g, r, 34);
    }

    private void DrawAppTile(BgraFrame frame, Entry tile, bool hovered)
    {
        var r = tile.Rect;

        if (hovered)
        {
            FillHighlight(frame, r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8,
                Math.Min(12, r.Height * 0.2));
        }

        var icon = tile.Icon;
        var label = tile.LabelBitmap;
        var gap = label is null ? 0 : 6;
        var contentH = (icon?.Height ?? 0) + gap + (label?.Height ?? 0);
        var top = r.Y + (r.Height - contentH) * 0.5;

        if (icon is not null)
        {
            CanvasPainter.Blit(frame, icon,
                (int)Math.Round(r.X + (r.Width - icon.Width) * 0.5), (int)Math.Round(top));
        }

        if (label is not null)
        {
            DrawTextAuto(frame, label,
                (int)Math.Round(r.X + (r.Width - label.Width) * 0.5),
                (int)Math.Round(r.Y + r.Height - label.Height - 8));
        }
    }

    /// <summary>「全部」区的小格子：图标在上、名字在下，比「已固定」紧凑。</summary>
    private void DrawAllAppTile(BgraFrame frame, Entry item, bool hovered)
    {
        var r = item.Rect;

        if (hovered)
        {
            FillHighlight(frame, r.X + 2, r.Y + 2, r.Width - 6, r.Height - 4, 8);
        }

        var icon = item.Icon;
        var label = item.LabelBitmap;
        var gap = label is null ? 0 : 4;
        var contentH = (icon?.Height ?? 0) + gap + (label?.Height ?? 0);
        var top = r.Y + (r.Height - contentH) / 2.0;

        if (icon is not null)
        {
            CanvasPainter.Blit(frame, icon,
                (int)Math.Round(r.X + (r.Width - icon.Width) / 2.0), (int)Math.Round(top));
            top += icon.Height + gap;
        }

        if (label is not null)
        {
            DrawTextAuto(frame, label,
                r.X + (r.Width - label.Width) / 2.0, top, dim: true);
        }
    }

    private void DrawRecommended(BgraFrame frame, Entry item, bool hovered)
    {
        var r = item.Rect;

        if (hovered)
        {
            FillHighlight(frame, r.X + 2, r.Y + 2, r.Width - 8, r.Height - 6, 8);
        }

        var iconSize = Math.Max(16, _settings.IconSize * 3 / 4);
        var iconX = r.X + 8;
        var iconY = r.Y + (r.Height - iconSize) / 2.0;

        if (item.Icon is not null)
        {
            CanvasPainter.Blit(frame, item.Icon, (int)Math.Round(iconX), (int)Math.Round(iconY));
        }

        var textX = iconX + iconSize + 12;
        var label = item.LabelBitmap;
        var sub = item.SubtitleBitmap;
        var total = (label?.Height ?? 0) + (sub is null ? 0 : sub.Height + 2);
        var ty = r.Y + (r.Height - total) / 2;

        if (label is not null)
        {
            DrawTextAuto(frame, label, textX, ty);
            ty += label.Height + 2;
        }

        if (sub is not null) DrawTextAuto(frame, sub, textX, ty, dim: true);
    }

    /// <summary>
    /// 画文字，颜色跟着该处玻璃的亮度走。
    ///
    /// <para>为什么必须这样：玻璃折射的是背后的真实内容，背后是浅色（比如资源管理器的空白区）
    /// 时白字会糊掉，背后是深色时深字会糊掉 —— <b>单一颜色的文字必然在某一种背景上失败</b>。
    /// 上游对导航文字做 7×3 下采样、自动挑高对比度的亮色或暗色，这里照搬同一套思路，
    /// 再垫一层反色微描边兜住"亮度刚好在阈值附近"的中灰背景。</para>
    /// </summary>
    private static void DrawTextAuto(BgraFrame frame, TextRasterizer.RasterizedText text,
        double x, double y, bool dim = false)
    {
        var xi = (int)Math.Round(x);
        var yi = (int)Math.Round(y);

        var bright = AverageLuminance(frame, xi, yi, text.Width, text.Height) > 0.55;

        var shadow = bright ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
        CanvasPainter.Blit(frame, text.Bitmap, xi + 1, yi + 1, shadow, 0.40);

        var ink = bright
            ? ((byte)28, (byte)28, (byte)34)
            : ((byte)242, (byte)242, (byte)248);

        CanvasPainter.Blit(frame, text.Bitmap, xi, yi, ink, dim ? 0.72 : 1.0);
    }

    /// <summary>取一块区域的归一化平均亮度（0..1），用来决定文字该用亮色还是暗色。</summary>
    private static double AverageLuminance(BgraFrame frame, double x, double y, double width, double height)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(frame.Width, (int)Math.Ceiling(x + width));
        var y1 = Math.Min(frame.Height, (int)Math.Ceiling(y + height));
        if (x1 <= x0 || y1 <= y0) return 0.5;

        long sum = 0;
        long count = 0;

        for (var sy = y0; sy < y1; sy += 3)
        {
            var row = sy * frame.Stride;
            for (var sx = x0; sx < x1; sx += 3)
            {
                var i = row + sx * 4;
                sum += (frame.Pixels[i + 2] * 2126 + frame.Pixels[i + 1] * 7152 + frame.Pixels[i] * 722) / 10000;
                count++;
            }
        }

        return count == 0 ? 0.5 : sum / (double)count / 255.0;
    }

    // ============================================================ 输入

    // ---- 全局滚轮计数 ----
    //
    // 覆盖层窗口是 WS_EX_NOACTIVATE，永远不获得焦点，而 WM_MOUSEWHEEL 发给焦点窗口 ——
    // 我们收不到。所以挂一个 WH_MOUSE_LL 钩子，把滚轮事件"数一笔"存下来，
    // 由 PollInput 取走。钩子**原样放行**（一定 CallNextHookEx），
    // 所以别的程序照常滚动，我们只是顺便知道"用户刚滚了一格"。
    //
    // 计数器用 Interlocked 累加：钩子回调在安装它的线程上跑（我们的渲染循环线程），
    // 但保守地用原子操作，免得日后改成别的线程时踩坑。
    private static int _wheelAccumulator;
    private static IntPtr _mouseHook;
    private static LowLevelMouseProc? _mouseProc;

    /// <summary>取走累积的滚轮量（正数 = 向上/向前，负数 = 向下/向后）并清零。</summary>
    private static int ConsumeWheelDelta()
    {
        var value = Interlocked.Exchange(ref _wheelAccumulator, 0);
        return value;
    }

    /// <summary>
    /// 装全局滚轮钩子（进程内只装一次）。
    ///
    /// <para>⚠️ 钩子失败不能影响菜单：装不上就装不上，最多是滚轮翻不了页，
    /// 用户还能靠"全部应用"列表本身可见的那一屏。所以这里吞掉所有异常。</para>
    /// </summary>
    private static void EnsureMouseWheelHook()
    {
        if (_mouseHook != IntPtr.Zero) return;

        try
        {
            _mouseProc = MouseHookCallback;
            _mouseHook = SetMouseHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        }
        catch
        {
            _mouseHook = IntPtr.Zero;
        }
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // mouseData 高 16 位是带符号的滚轮增量：+120 = 向前一格，-120 = 向后一格。
                var delta = (short)((info.mouseData >> 16) & 0xFFFF);
                if (delta != 0) Interlocked.Add(ref _wheelAccumulator, delta);
            }
            catch
            {
                // 解析失败就当作没滚，绝不能在钩子里抛出去（会连带影响全系统输入）。
            }
        }

        // ⚠️ 必须原样放行：我们只是"旁听"，不消费事件。
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static void ReleaseMouseWheelHook()
    {
        var hook = _mouseHook;
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
        if (hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(hook); } catch { /* 退出路径，忽略 */ }
        }
    }

    /// <summary>
    /// 轮询输入。
    ///
    /// <para>与任务栏用同一套理由：不赌鼠标消息。而且"点击面板外部关闭"本来就要看整屏光标，
    /// 轮询比消息更直接；这个窗口是 <c>WS_EX_NOACTIVATE</c>，键盘（Esc）同样收不到焦点。</para>
    /// </summary>
    private bool PollInput()
    {
        var dirty = false;

        if (!GetCursorPos(out var pt)) return false;

        var inside = pt.X >= _panelBounds.X && pt.X < _panelBounds.Right
                  && pt.Y >= _panelBounds.Y && pt.Y < _panelBounds.Bottom;

        // ---- 滚轮：翻「全部应用」 ----
        //
        // 本窗口是 WS_EX_NOACTIVATE，收不到焦点，所以 WM_MOUSEWHEEL 指望不上
        // （和鼠标移动一样，实测本机外壳层级下覆盖层收不到鼠标输入）。
        // 改用一个**全局滚轮计数器**：注册 WH_MOUSE_LL 钩子，只数滚轮事件、不改动也不吞掉它们，
        // 于是滚轮在其他程序里照样正常工作，我们的面板开着时就顺便拿来翻页。
        var wheel = ConsumeWheelDelta();
        if (wheel != 0 && inside && CanScrollAllApps)
        {
            // 面板上滚动才翻页。一格 = WheelStepPx（默认正好一项高）。
            if (ScrollAllApps(wheel > 0 ? -_settings.WheelStepPx : _settings.WheelStepPx))
            {
                _contentDirty = true;
                dirty = true;
            }
        }

        var index = inside ? HitTest(pt) : -1;
        if (index != _hoverIndex)
        {
            _hoverIndex = index;

            // ⚠️ 悬停项变了 = 高亮方块要换位置 = **内容层必须重画**。
            //
            // 这里必须同时置 _contentDirty：旧代码靠"每次 RenderGlass 后无条件
            // _contentDirty = true"顺带刷新了高亮，而那正是"反复渲染"的元凶。
            // 拆掉它之后如果不在这里显式置位，鼠标划过时高亮就会卡住不动 ——
            // 这是把性能问题修好却引入功能回归的典型陷阱。
            _contentDirty = true;
            dirty = true;
        }

        var left = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var esc = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

        if (esc && !_lastEscDown)
        {
            _lastEscDown = esc;
            Close();
            return true;
        }
        _lastEscDown = esc;

        if (left && !_lastLeftDown)
        {
            _pressIndex = index;
        }
        else if (!left && _lastLeftDown)
        {
            // 去抖：打开菜单会阻塞一两百毫秒，用户以为"没反应"就会再点一下，
            // 于是刚打开又被关掉（实测日志里两次「点击 → Start」只隔 9ms）。
            // 这里保证一次真实意图只生效一次。
            var now = Environment.TickCount64;
            if (now - _lastClickTicks >= Math.Max(0, _settings.ClickDebounceMs))
            {
                if (index >= 0 && index == _pressIndex) Activate(index);
                else if (index < 0) Close();   // 点面板外部 → 关闭（与真实开始菜单一致）
                _lastClickTicks = now;
            }
            _pressIndex = -1;
        }

        _lastLeftDown = left;
        return dirty;
    }

    private int HitTest(POINT pt)
    {
        var x = pt.X - _panelBounds.X;
        var y = pt.Y - _panelBounds.Y;

        // ⚠️ 「全部应用」的项可能被滚动到视口外，而它们的 Rect 仍然存在于列表中
        // （只是坐标落在视口之外）。不先做视口判定的话，滚动到第 5 页时
        // 鼠标划过视口顶部会命中"第 1 页那些已经看不见的项" ——
        // 表现为"点到了看不到的应用"。这类 bug 极难从截图上看出来。
        var viewportTop = _hasAllAppsViewport ? _allAppsViewport.Y : 0;
        var viewportBottom = _hasAllAppsViewport ? _allAppsViewport.Bottom : 0;

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var r = entry.Rect;
            if (r.Width <= 0 || r.Height <= 0) continue;

            // 「全部应用」项必须落在滚动视口内才算命中。
            if (entry.Kind == EntryKind.AllApp && _hasAllAppsViewport)
            {
                if (r.Bottom <= viewportTop || r.Y >= viewportBottom) continue;
            }

            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return i;
        }
        return -1;
    }

    private void Activate(int index)
    {
        var entry = _entries[index];

        switch (entry.Kind)
        {
            case EntryKind.AppTile:
                LaunchShortcut(entry);
                Close();
                break;

            case EntryKind.AllApp:
                _log?.Invoke($"开始菜单：启动「{entry.Label}」");
                LaunchPath(entry.ShortcutPath);
                Close();
                break;

            case EntryKind.Recommended:
                _log?.Invoke($"开始菜单：切到「{entry.Label}」");
                if (entry.Window != IntPtr.Zero) ShellActions.ActivateWindow(entry.Window);
                Close();
                break;

            case EntryKind.User:
                _log?.Invoke("开始菜单：打开用户文件夹");
                ShellActions.ShowInFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                Close();
                break;

            case EntryKind.Power:
                // 刻意不在这里真的关机/重启 —— 一个误触代价太大。
                // 快捷设置面板里就有真正的电源按钮，把用户送到那儿去。
                _log?.Invoke("开始菜单：电源 → 打开快捷设置（那里有真正的电源按钮）");
                ShellActions.OpenQuickSettings();
                Close();
                break;
        }
    }

    private void LaunchShortcut(Entry entry)
    {
        _log?.Invoke($"开始菜单：启动「{entry.Label}」");

        var app = entry.App;
        LaunchPath(!string.IsNullOrWhiteSpace(app?.ShortcutPath) ? app!.ShortcutPath : app?.TargetPath);
    }

    /// <summary>用 Shell 打开一个路径（.lnk 或 .exe）。失败只记日志，不抛。</summary>
    private void LaunchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"开始菜单：启动失败 —— {ex.Message}");
        }
    }

    // ============================================================ 每帧

    /// <summary>
    /// 定期重抓背景，让玻璃跟着**活的**桌面走。
    ///
    /// <para>只在打开时抓一次的话，玻璃里是一张冻住的快照 ——
    /// 用户的壁纸是 Wallpaper Engine 的动态壁纸，桌面在动而玻璃不动，
    /// 看起来就是"只渲染了一下，之后再也不更新"。</para>
    /// </summary>
    /// <summary>
    /// 定期重抓背景，让玻璃跟着"活的桌面"走。
    ///
    /// <para>⚠️ <b>菜单打开期间必须完全不抓屏。</b>
    ///
    /// <para>原因：本窗口在菜单打开时是<b>可见</b>的，而抓屏用的是屏幕像素 ——
    /// 于是会把<b>菜单自己</b>拍进折射源。玻璃再把这帧画面（模糊 + 位移）画回面板，
    /// 叠在当帧内容之上，就形成一层"模糊重影"：用户看到的每个图标都有个幽灵副本。</para>
    ///
    /// <para>实测证据：验收合成图里 <c>已固定</c> 一行的 Chrome / Edge / WPS 图标
    /// 各自带一个偏移、模糊的副本，<c>全部</c> 网格同样如此。</para>
    ///
    /// <para><b>为什么不在打开期间"隐藏→抓屏→恢复"</b>（任务栏用的那招）：
    /// 那会让菜单每 600ms 闪一下。也<b>不用</b> <c>WDA_EXCLUDEFROMCAPTURE</c>，
    /// 因为那会让用户的截图和录屏里看不到菜单 —— 而用户正是靠录屏来反馈问题的。</para>
    ///
    /// <para><b>代价</b>：菜单打开期间玻璃里是"打开那一刻"的背景快照。
    /// 对动态壁纸来说背景会静止几秒 —— 但面板的模糊半径高达 ~64 屏幕像素，
    /// 背景本来就糊成一片柔和色块，静止与缓慢变化肉眼无法分辨。</para>
    /// </summary>
    private void RefreshBackdrop()
    {
        // 打开期间一律不抓屏（见上方说明）。
        if (IsOpen) return;

        var interval = Math.Max(80, _settings.BackdropRefreshMs);

        var now = Environment.TickCount64;
        if (now - _lastBackdropTicks < interval) return;
        _lastBackdropTicks = now;

        // 只有背景真的变了才重置累积 / 重画内容（见 CaptureScene 的说明）。
        CaptureScene();
    }

    /// <summary>
    /// 让玻璃的高光跟着鼠标走（对应上游的 <c>setPointer(x, y)</c>）。
    ///
    /// <para>指针的归一化坐标喂给渲染器的 <c>MouseX/MouseY</c>，镜面高光就会像
    /// 一束光被鼠标扫过。任务栏用同一组参数做视差，这里用来做"光感"。</para>
    ///
    /// <para>⚠️ 必须节流：玻璃渲染一次要几十毫秒，鼠标每移动一像素就重算会把 CPU 吃光。
    /// 所以限制到 <see cref="Settings.MouseLightIntervalMs"/> 一次，
    /// 并且移动幅度小于 0.4% 面板尺寸时直接跳过。</para>
    /// </summary>
    private void UpdateMouseLight()
    {
        if (!_settings.MouseLight) return;

        var now = Environment.TickCount64;
        if (now - _lastMouseLightTicks < Math.Max(16, _settings.MouseLightIntervalMs)) return;
        _lastMouseLightTicks = now;

        if (!GetCursorPos(out var pt)) return;

        var nx = Math.Clamp((pt.X - _panelBounds.X) / (double)Math.Max(1, _panelBounds.Width), 0, 1);
        var ny = Math.Clamp((pt.Y - _panelBounds.Y) / (double)Math.Max(1, _panelBounds.Height), 0, 1);

        // 移动幅度太小就跳过：玻璃重算一次是几毫秒起步，鼠标在面板上划过
        // 会产生成百上千次微小位移，逐一重算没有意义 ——
        // 光感的观感是"有束光在扫"，1.2% 的步进已经足够顺滑。
        //
        // ⚠️ 阈值曾经是 0.004（0.4%）。面板宽 1362px 时那只有 5.4px，
        // 鼠标轻微抖动就会触发重算，是"开始菜单太卡"的次要贡献者。
        if (Math.Abs(nx - _mouseX) < 0.012 && Math.Abs(ny - _mouseY) < 0.012) return;

        _mouseX = nx;
        _mouseY = ny;
        _glassDirty = true;   // 高光变了，玻璃要重算
    }

    /// <summary>由任务栏的渲染循环驱动（菜单打开期间每 tick 调一次）。</summary>
    public void Tick()
    {
        if (_disposed || !IsOpen) return;

        var inputDirty = PollInput();
        if (!IsOpen) return;   // PollInput 可能已经把它关掉了

        UpdateMouseLight();
        RefreshBackdrop();

        // ⚠️ 这里曾经是致命的一行：
        //     if (_glassDirty || !_renderer.IsConverged) { RenderGlass(); _contentDirty = true; }
        // _renderer.IsConverged 在没收敛时为 false，而鼠标光感每 120ms 就把 _glassDirty 置真，
        // 于是**每一帧都 RenderGlass + 重画全部内容** ——
        // 用户看到的就是"开始菜单在反复渲染自己的内容"，CPU 被吃光、界面发卡。
        //
        // 现在严格分层：
        //   · 玻璃变了 → 只重算玻璃 + 重新合成（内容绘制被复用，见 _contentLayer）
        //   · 只有内容变了（悬停高亮等）→ 不碰玻璃，直接合成
        var needsRender = _glassDirty || !_renderer.IsConverged;
        if (needsRender && IsOpen)
        {
            RenderGlass();
        }

        // ⚠️ 收敛期间**不要每帧上屏**。
        //
        // 玻璃靠时间累积去噪，要攒够 MaxAccumulation 帧才"收敛"。这段时间里
        // 每帧画面都在变（噪声在减少），但每帧都 Present 就要付 32 次上屏成本。
        // 实测面板 1362×1270 时单次上屏 = 62ms → **32 帧 = 2 秒的重负载**，
        // 用户感知就是"打开开始菜单后卡一段"。
        //
        // 策略（三者取"或"，但要限频）：
        //   · 内容变了（悬停/翻页）→ 立刻上屏，否则鼠标划过没反馈
        //   · 已收敛 → 上屏一次就够，之后不动
        //   · 累积中 → 按 PresentIntervalWhileAccumulating 限频上屏
        //     （画面在趋稳，10Hz 已经看不出跳跃，却省下 90% 的上屏）
        var converged = _renderer.IsConverged;
        var contentChanged = _contentDirty || inputDirty;

        var now = Environment.TickCount64;
        var shouldPresent = contentChanged
            || converged && !_presentedConverged
            || !converged && now - _lastPresentTicks >= PresentIntervalWhileAccumulating;

        if (shouldPresent)
        {
            CompositeAndPresent();
            _lastPresentTicks = now;
            _contentDirty = false;
            _presentedConverged = converged;
        }

        // 收敛后玻璃不再变（除非背景/光感把它标脏），把标志清掉，
        // 下一次被标脏时又能立即上屏一帧。
        if (converged && _glassDirty) _presentedConverged = false;
    }

    /// <summary>
    /// 取一份<b>玻璃层</b>（尚未叠加内容）的副本，供离线取证。
    ///
    /// <para>用途：面板出现异常时，先分清"是玻璃本身错了"还是"内容绘制错了" ——
    /// 只看上屏截图分不出来。</para>
    /// </summary>
    public BgraFrame? SnapshotGlassLayer() => _glassFrame?.Clone();

    /// <summary>
    /// 诊断用：用指定的雾化模糊半径重渲染一帧玻璃层（不改动实例状态）。
    ///
    /// <para><b>为什么需要它</b>：要量化"重影到底压掉了多少"，唯一干净的做法是
    /// 拿<b>同一张真实桌面</b>分别用"修复前"（半径 0 → 退化成点采样）与
    /// "修复后"（金字塔模糊）各渲染一次，再比结构残余能量。
    /// 只看一张图给不出比例，只能凭肉眼说"好一点了"。</para>
    /// </summary>
    public BgraFrame? RenderGlassWithBlurRadius(double radius)
    {
        if (_scene is null) return null;

        var probe = new CpuGlassRenderer(_renderer.Material with
        {
            FrostedBlurRadius = radius,
        });

        GlassRenderOptions Options() => new()
        {
            GlassRect = new PixelRect(0, 0, _canvasWidth, _canvasHeight),
            CanvasWidth = _canvasWidth,
            CanvasHeight = _canvasHeight,
            PathsPerPixel = Math.Clamp(_settings.PathsPerPixel, 1, 12),
            MaxAccumulation = Math.Max(4, _settings.MaxAccumulation),
            MouseX = (float)_mouseX,
            MouseY = (float)_mouseY,
        };

        var frame = probe.Render(_scene, Options());
        // 累积到收敛，避免时间性噪点干扰"结构残余"的读数。
        for (var i = 0; i < 8; i++) frame = probe.Render(_scene, Options());
        return frame;
    }

    /// <summary>取一份<b>含内容</b>的最终合成帧副本，供离线取证。</summary>
    public BgraFrame? SnapshotComposite()
    {
        if (_glassFrame is null) return null;

        // ⚠️ 必须和 CompositeAndPresent 走**完全一样**的步骤。
        // 之前这里漏了 ApplyScrim，结果取证图看不出底衬、和用户实际看到的不是一回事 ——
        // 用这种图去判断"改好了没有"会得出完全错误的结论。
        // 后来又改了顺序（底衬移到放大之前），这里也必须跟着改，
        // 否则两张图会差一个"底衬被放大插值"的细微差别。
        var canvas = CopyGlassToScratch();
        ApplyScrim(canvas);

        var frame = ScaleToPanel(canvas);
        if (frame is null) return null;

        // 走同一条内容缓存路径，保证取证图和上屏图逐像素一致。
        // （复用同一张内容层；尺寸不符时才重新分配，重画前清空。）
        if (_contentLayer is null
            || _contentLayer.Width != frame.Width || _contentLayer.Height != frame.Height)
        {
            _contentLayer = new BgraFrame(frame.Width, frame.Height);
            _contentDirty = true;
        }

        if (_contentDirty)
        {
            Array.Clear(_contentLayer.Pixels);
            _luminanceRef = frame;
            try { DrawContent(_contentLayer); }
            finally { _luminanceRef = null; }
            _contentDirty = false;
        }

        BlendOver(frame, _contentLayer);

        // ⚠️ 必须克隆再返回：frame 现在是**复用的上屏缓冲**，
        // 下一次 Present 会把它覆盖掉。调用方（PngWriter）虽然通常立即写完，
        // 但取证接口不能依赖调用时序 —— 返回一份独立副本才安全。
        return frame.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsOpen = false;

        // 兜底：即使用户在菜单打开时直接退出程序，桌面图标也必须还回去。
        if (_iconsHidden)
        {
            DesktopIcons.Restore(true);
            _iconsHidden = false;
        }

        _contentLayer = null;
        _allApps = [];
        _window.Dispose();
        _sampler.Dispose();
        _entries.Clear();

        // 滚轮钩子是**进程级**的（用了静态字段），所以只在最后一个实例销毁时才摘。
        // 不过本程序里开始菜单只会有一个实例，这里直接摘掉即可 ——
        // 忘了摘的话钩子会一直挂在系统上，退出后继续占用资源。
        ReleaseMouseWheelHook();
    }
}
