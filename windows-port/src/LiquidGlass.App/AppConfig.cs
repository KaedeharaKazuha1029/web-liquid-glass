using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LiquidGlass.Core;
using LiquidGlass.Win32;

namespace LiquidGlass.App;

/// <summary>单个目标界面的配置。</summary>
public sealed class TargetConfig
{
    /// <summary>是否接管这个界面。</summary>
    public bool Enabled { get; set; }

    /// <summary>玻璃相对目标矩形的左右内缩（像素）。</summary>
    public double GlassInsetX { get; set; } = 10;

    /// <summary>玻璃相对目标矩形的上下内缩（像素）。</summary>
    public double GlassInsetY { get; set; } = 6;

    /// <summary>
    /// 圆角半径覆盖。<c>-1</c> 表示沿用上游规则
    /// （细长矩形自动变成胶囊形）。
    /// </summary>
    public double CornerRadius { get; set; } = -1;

    /// <summary>
    /// 是否抹掉目标窗口的原生背景。
    /// 关掉它，玻璃层会被系统背景完全遮住 —— 除非你想做纯边缘效果。
    /// </summary>
    public bool MakeTargetTransparent { get; set; } = true;
}

/// <summary>
/// 材质覆盖。所有字段可空，<c>null</c> 表示沿用上游参考值。
/// 之所以用可空而不是直接给默认值，是为了让配置文件能清楚区分
/// "我明确想改成这个值" 与 "我没打算动它"。
/// </summary>
public sealed class MaterialConfig
{
    public double? ReverseDisplacement { get; set; }
    public double? EdgeCurvature { get; set; }
    public double? OpticalThickness { get; set; }
    public double? EdgeBandRatio { get; set; }
    public double? RefractionVisibleRatio { get; set; }
    public double? BlendFeatherPx { get; set; }
    public double? FrostedStrength { get; set; }
    public double? FrostedAttenuation { get; set; }
    public double? BlurSpacingPx { get; set; }

    /// <summary>雾化模糊的支撑半径（纹理像素）。这是"重影"的根治参数。</summary>
    public double? FrostedBlurRadius { get; set; }
    public double? DispersionStrength { get; set; }
    public double? HighlightStrength { get; set; }
    public double? TintMix { get; set; }
    public double? Roughness { get; set; }
    public double? LensMix { get; set; }

    public LiquidGlassMaterial Apply()
    {
        if (!HasAnyOverride) return LiquidGlassMaterial.Reference;

        return LiquidGlassMaterial.Reference.With(b =>
        {
            b.ReverseDisplacement = ReverseDisplacement;
            b.EdgeCurvature = EdgeCurvature;
            b.OpticalThickness = OpticalThickness;
            b.EdgeBandRatio = EdgeBandRatio;
            b.RefractionVisibleRatio = RefractionVisibleRatio;
            b.BlendFeatherPx = BlendFeatherPx;
            b.FrostedStrength = FrostedStrength;
            b.FrostedAttenuation = FrostedAttenuation;
            b.BlurSpacingPx = BlurSpacingPx;
            b.FrostedBlurRadius = FrostedBlurRadius;
            b.DispersionStrength = DispersionStrength;
            b.HighlightStrength = HighlightStrength;
            b.TintMix = TintMix;
            b.Roughness = Roughness;
            b.LensMix = LensMix;
        });
    }

    [JsonIgnore]
    public bool HasAnyOverride =>
        ReverseDisplacement is not null || EdgeCurvature is not null || OpticalThickness is not null
        || EdgeBandRatio is not null || RefractionVisibleRatio is not null || BlendFeatherPx is not null
        || FrostedStrength is not null || FrostedAttenuation is not null || BlurSpacingPx is not null
        || FrostedBlurRadius is not null
        || DispersionStrength is not null || HighlightStrength is not null || TintMix is not null
        || Roughness is not null || LensMix is not null;
}

/// <summary>性能档配置。</summary>
public sealed class PerformanceConfig
{
    /// <summary>每像素路径数。上游桌面参考值 4；调到 2 大约省一半时间。</summary>
    public int PathsPerPixel { get; set; } = 4;

    /// <summary>静止时的累积帧上限（上游参考值 48）。</summary>
    public int IdleAccumulation { get; set; } = 48;

    /// <summary>鼠标划过玻璃时的累积帧上限。数值越低越跟手。</summary>
    public int MovingAccumulation { get; set; } = 12;

    /// <summary>是否启用鼠标视差（对应上游的 uMouse 相机射线倾斜）。</summary>
    public bool MouseParallax { get; set; } = true;

    /// <summary>渲染循环的轮询间隔（毫秒）。33ms ≈ 30Hz。</summary>
    public int TickIntervalMs { get; set; } = 33;

    /// <summary>
    /// 画质档位：
    /// <list type="bullet">
    ///   <item><c>"auto"</c>（默认）：启动时用真实负载实测一次定初始档位，
    ///         之后运行时闭环按实测帧耗时动态升降档。</item>
    ///   <item><c>"minimal" / "low" / "balanced" / "high" / "ultra"</c>：锁死档位，不做自适应。</item>
    /// </list>
    /// 各档位的具体参数见 <c>docs/PERFORMANCE.md</c>。
    /// </summary>
    public string QualityMode { get; set; } = "auto";
}

/// <summary>场景采样配置。</summary>
public sealed class SceneConfig
{
    /// <summary>折射源纹理向外扩的边距（像素）。折射最远约 40px，96 很安全。</summary>
    public double Margin { get; set; } = 96;

    /// <summary>是否把玻璃层排除出屏幕捕获。v2.2 起默认关闭——这样任务栏能出现在截图里；抓屏时走 Hide→Capture→Show，不会采到自己。</summary>
    public bool ExcludeFromCapture { get; set; } = false;

    /// <summary>
    /// 抓屏的最小间隔（毫秒）。0 = 自动：
    /// 预留工作区时 15 秒（背后只有壁纸、基本不动），否则 400 毫秒（背后一直变）。
    ///
    /// <para>这个值直接决定任务栏"闪不闪"以及"用户截图能不能稳定拍到它"：
    /// 每次抓屏都要把任务栏隐藏几毫秒（因为没开 `excludeFromCapture`）。</para>
    /// </summary>
    public int CaptureMinIntervalMs { get; set; }

    /// <summary>
    /// 折射源取法：
    /// <list type="bullet">
    ///   <item><c>"direct"</c>（默认）：直接采样玻璃背后的屏幕像素，等价于上游的 1:1 DOM 捕获。</item>
    ///   <item><c>"extendFromAbove"</c>：把玻璃上方最后一行未被遮挡的像素向下延伸。
    ///         当背景是平滑壁纸、折射看不出效果时改用这个。</item>
    /// </list>
    /// </summary>
    public string Source { get; set; } = "direct";

    /// <summary>把 <see cref="Source"/> 解析成枚举，无法识别时退回 <c>direct</c>。</summary>
    public SceneSourceMode ResolveSource() =>
        Source?.Trim().ToLowerInvariant() switch
        {
            "extendfromabove" or "extend_from_above" or "extend" or "above" =>
                SceneSourceMode.ExtendFromAbove,
            _ => SceneSourceMode.Direct,
        };
}

/// <summary>
/// 替换任务栏配置（v2 默认模式）。
///
/// 该模式会<b>完全隐藏系统任务栏</b>，改由本程序绘制一条液态玻璃任务栏。
/// 高度恒定、宽度随应用数量变化；每个应用图标在上、名字在下。
/// </summary>
public sealed class TaskbarReplacementConfig
{
    /// <summary>
    /// Z 序层级。可选值：
    /// <list type="bullet">
    ///   <item><c>"desktopBottom"</c>（默认）桌面之上、所有普通窗口之下，且<b>始终可见</b>；</item>
    ///   <item><c>"behindDesktopIcons"</c> 寄生桌面壁纸 WorkerW —— 会落到桌面图标/壁纸<b>之下</b>，通常看不见（v2.2.0 的踩坑项）；</item>
    ///   <item><c>"topMost"</c> 始终置顶，会盖住所有普通窗口（v2.1 行为）；</item>
    ///   <item><c>"normal"</c> 普通窗口层，不下沉也不置顶。</item>
    /// </list>
    /// </summary>
    public string ZOrder { get; set; } = "desktopBottom";

    /// <summary>
    /// 是否在屏幕底部**预留工作区**（高度 = height + bottomMargin），
    /// 让最大化窗口在任务栏上方停住。
    ///
    /// <para><c>true</c>（默认）：任务栏**永远可见、且不遮挡任何窗口**；</para>
    ///
    /// <para><c>false</c>：工作区释放为整屏，窗口延伸到底 —— 玻璃能折射窗口内容，
    /// 但当任务栏位于窗口之下（zOrder = "desktopBottom"）时，会被最大化窗口整个盖住。</para>
    ///
    /// <para>仅当 <c>zOrder = "desktopBottom"</c> 时才需要预留；置顶模式不需要。</para>
    /// </summary>
    public bool ReserveWorkArea { get; set; } = true;

    /// <summary>
    /// 是否启用**悬停胶囊**：一块跟着指针滑行的玻璃镜头。
    ///
    /// <para>这是上游最有辨识度的交互 —— 宽度 = 导航的 1/5、高度 = 1.2×（上下各凸出 10%），
    /// 有自己的折射与独立累积预算。Windows 移植初期刻意跳过了它
    /// （理由见 <c>docs/PORTING-NOTES.md</c> §七），v2.4 起补上。</para>
    /// </summary>
    public bool CapsuleEnabled { get; set; } = true;

    /// <summary>胶囊宽度占导航宽度的比例。上游默认 <c>0.2</c>（即 1/5）。</summary>
    public double CapsuleWidthRatio { get; set; } = 0.2;

    /// <summary>胶囊高度相对导航高度的倍率。上游默认 <c>1.2</c>（上下各溢出 10%）。</summary>
    public double CapsuleHeightScale { get; set; } = 1.2;

    /// <summary>
    /// 胶囊的每像素路径数。它是<b>独立元素，有独立预算</b>：
    /// 调低它只影响胶囊的干净度，不会拖累导航本体。上游桌面默认 4、移动端 1。
    /// </summary>
    public int CapsulePathsPerPixel { get; set; } = 2;

    /// <summary>
    /// 胶囊**移动时**的每像素路径数（默认 6，必须 ≥ <see cref="CapsulePathsPerPixel"/>）。
    ///
    /// <para>胶囊一动，时间累积就会被清空（包围盒变了），那一帧只有一次采样。
    /// 路径数太低时随机反射会变成肉眼可见的噪点 —— 观感就是"花"。
    /// 移动时抬高它、停下后回落，是把"干净"和"省 CPU"同时拿到的办法。</para>
    /// </summary>
    public int CapsuleMovingPathsPerPixel { get; set; } = 6;

    /// <summary>胶囊独立的时间累积帧上限（比导航短，移动后收敛更快）。</summary>
    public int CapsuleAccumulation { get; set; } = 16;

    /// <summary>
    /// 胶囊跟随指针的平滑系数：每帧把中心向目标推进这个比例。
    /// <c>1</c> = 瞬时贴合；<c>0.35</c> = 略带惯性的滑动（上游的"动画跟随"观感）。
    /// </summary>
    public double CapsuleFollow { get; set; } = 0.35;

    /// <summary>玻璃胶囊高度（像素）。v2.2 起按用户要求整体翻倍（58→116）。</summary>
    public int Height { get; set; } = 116;

    /// <summary>每个应用占的宽度。高度不变、宽度随应用数线性增长。</summary>
    public int ItemWidth { get; set; } = 120;

    /// <summary>应用图标边长。</summary>
    public int IconSize { get; set; } = 52;

    /// <summary>图标下方文字的字号（像素）。</summary>
    public int LabelFontSize { get; set; } = 20;

    /// <summary>开始按钮宽度。隐藏系统任务栏后它是唯一可见的开始入口，建议保留。</summary>
    public int StartButtonWidth { get; set; } = 96;

    /// <summary>时钟区域宽度。</summary>
    public int ClockWidth { get; set; } = 148;

    /// <summary>快捷设置按钮宽度（点它等于按 Win+A）。</summary>
    public int QuickSettingsWidth { get; set; } = 80;

    /// <summary>胶囊左右内边距。</summary>
    public int HorizontalPadding { get; set; } = 20;

    /// <summary>胶囊距屏幕底边的距离。</summary>
    public int BottomMargin { get; set; } = 16;

    public bool ShowStartButton { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowQuickSettings { get; set; } = true;

    /// <summary>
    /// 水平对齐：true = 居中（贴近原项目的悬浮导航栏），false = 靠左（贴近 Windows 习惯）。
    /// </summary>
    public bool CenterHorizontally { get; set; } = true;

    /// <summary>是否完全隐藏系统任务栏。</summary>
    public bool HideSystemTaskbar { get; set; } = true;

    /// <summary>
    /// 是否释放工作区，让最大化窗口延伸到玻璃下方。
    ///
    /// <para>开启后折射源是窗口底部的真实界面，效果最好；
    /// 关闭则工作区保留任务栏高度，玻璃背后只剩壁纸。</para>
    ///
    /// <para>⚠️ 实现上必须先试"不改用户设置"的手段并实测工作区，
    /// 失败会自动升级为开启任务栏"自动隐藏"——那时系统设置里会短暂出现
    /// 一个被打开的开关，退出时精确还原。详见 docs/PORTING-NOTES.md。</para>
    /// </summary>
    public bool ReleaseWorkArea { get; set; } = true;

    /// <summary>模型重扫的安全网间隔（毫秒）。WinEvent 会即时触发，这个只是兜底。</summary>
    public int SafetyRescanMs { get; set; } = 2500;
}

/// <summary>行为配置。</summary>
/// <summary>
/// 自研的**液态玻璃开始菜单**。
///
/// <para>⚠️ 不要和上面那个 <c>StartMenu</c>（<see cref="TargetConfig"/>）搞混：
/// 那个是"底衬模式"用来指定要贴玻璃的系统开始菜单窗口；
/// 本段是"我们自己做一块开始菜单出来"，因为它把系统开始菜单替换掉了。</para>
///
/// <para>背景：Windows 11 的系统开始菜单是 XAML 窗口，背景由 XAML 自绘，
/// <c>DwmSetWindowAttribute</c> 够不到它，所以想让开始菜单变成液态玻璃只能自研。</para>
/// </summary>
public sealed class GlassStartMenuConfig
{
    /// <summary>是否启用。为 false 时「开始」按钮回退到 Win 键唤起系统菜单。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>每行几个应用。</summary>
    public int Columns { get; set; } = 6;

    public int TileWidth { get; set; } = 212;
    public int TileHeight { get; set; } = 212;

    /// <summary>图标边长（像素）。</summary>
    public int IconSize { get; set; } = 82;

    public int LabelFontSize { get; set; } = 19;

    /// <summary>面板内边距。</summary>
    public int Padding { get; set; } = 45;

    /// <summary>面板圆角半径（最终像素）。</summary>
    public int CornerRadius { get; set; } = 42;

    /// <summary>面板底边与任务栏顶边的间距。</summary>
    public int GapAboveTaskbar { get; set; } = 12;

    /// <summary>顶部搜索框高度。</summary>
    public int SearchBoxHeight { get; set; } = 68;

    /// <summary>「推荐的项目」最多显示几项（按 2 列铺开）。</summary>
    public int RecommendedMax { get; set; } = 4;

    /// <summary>推荐项的单项高度。</summary>
    public int RecommendedHeight { get; set; } = 102;

    /// <summary>底部（用户 / 电源）那一行的高度。</summary>
    public int BottomBarHeight { get; set; } = 90;

    /// <summary>
    /// 「全部应用」<b>每屏</b>显示几项（不再有总量上限）。
    ///
    /// <para>⚠️ 这一项以前叫 <c>AllAppsMax</c>，语义是"总量上限"，默认 24。
    /// 结果：本机 168 个快捷方式只有 24 个能被看到，而界面没有翻页入口 ——
    /// 用户的原话就是"开始菜单无法查看所有应用"。现在改成"一屏的项数预算"，
    /// 完整列表由滚轮翻页查看，绝不截断。</para>
    /// </summary>
    public int AllAppsPerScreen { get; set; } = 24;

    /// <summary>「全部应用」每行几项。</summary>
    public int AllAppsColumns { get; set; } = 6;

    /// <summary>「全部应用」单项高度。</summary>
    public int AllAppsTileHeight { get; set; } = 118;

    /// <summary>「全部应用」图标边长。</summary>
    public int AllAppsIconSize { get; set; } = 48;

    /// <summary>滚轮一格翻多少像素（默认正好一项高，一格一行）。</summary>
    public int WheelStepPx { get; set; } = 118;

    /// <summary>
    /// <b>玻璃的渲染倍率</b>（相对面板最终尺寸）。默认 0.4。
    ///
    /// <para>这是"大面板也能跑得动"的关键：玻璃是低频效果，按面板完整分辨率算是极大浪费，
    /// 而耗时与像素数成正比。</para>
    ///
    /// <para>⚠️ 0.4 是面板还只有 848×696（约 59 万像素）时定的。面板放大到
    /// 1362×1270（173 万像素）后，0.4 倍仍留下 27.7 万像素的玻璃计算量，
    /// 实测打开一次 211ms（其中"合成"一段就 62ms）。降到 <b>0.25</b> 后
    /// 玻璃降到 10.8 万像素，打开耗时降到约 100ms —— 而玻璃是低频效果，
    /// 这一档在观感上看不出差别。</para>
    /// </summary>
    public double GlassScale { get; set; } = 0.25;

    /// <summary>
    /// 雾化模糊的支撑半径（纹理像素）。默认 <b>96</b>。这是"重影"的根治参数。
    ///
    /// <para>背景文字的排版结构周期在 160–480px 量级；抑制周期 T 的结构
    /// 需要约 T/2 的模糊支撑半径。旧的十字核只有 ±56 屏幕px 的支撑，
    /// 于是 160px 周期的结构有 <b>99.9%</b> 原样透出 —— 那就是重影。</para>
    ///
    /// <para>实测残余振幅（512×512 正弦条纹，越小越看不见）：</para>
    /// <code>
    ///   周期T   点采样(旧)  半径24   半径48   半径96   半径128
    ///    40px     0.995     0.354    0.189    0.071    0.046
    ///    80px     0.999     0.676    0.364    0.130    0.091
    ///   160px     0.999     0.912    0.677    0.241    0.179
    ///   320px     0.818     0.952    0.928    0.579    0.424
    /// </code>
    ///
    /// <para>96 是折中：160px 级从 0.999 降到 0.241（4 倍改善），
    /// 同时不会把玻璃糊成一片纯色。调大不会增加逐像素开销（走金字塔）。</para>
    /// </summary>
    public double FrostedBlurRadius { get; set; } = 96.0;

    /// <summary>
    /// 边缘折射带占面板高度的比例。默认 <b>0.05</b>（任务栏那套是 0.26）。
    ///
    /// <para>折射与色散<b>只在这一带里发生</b>。0.26 是为 116px 高的细长任务栏调的，
    /// 套到 1270px 高的面板上会得到 330px 的巨带 ——
    /// 面板四分之一的高度都在做折射，背景的高对比边缘被染成贯穿全高的彩色竖条纹。</para>
    /// </summary>
    public double EdgeBandRatio { get; set; } = 0.05;

    /// <summary>
    /// 色散（RGB 三通道折射率差）强度。默认 <b>0.10</b>（任务栏那套是 0.22）。
    ///
    /// <para>色散在细条上是精致的边缘彩边；在大面板上会把背景边缘拉成
    /// 肉眼可见的彩虹竖带，喧宾夺主。</para>
    /// </summary>
    public double DispersionStrength { get; set; } = 0.10;

    /// <summary>
    /// 折射带占面板高度的比例（<b>这一带内雾化权重为 0</b>，只有折射）。
    /// 默认 <b>0.04</b>（任务栏那套是 0.16）。
    ///
    /// <para>0.16 是为 116px 高的细长任务栏调的（18px 带，无所谓）；
    /// 1270px 的面板下它是 <b>203px</b> —— 外圈两百多像素全是"无雾化的清晰折射"，
    /// 背景在那里完全裸露，正是"底层应用和菜单内容重叠"的主要来源。</para>
    /// </summary>
    public double RefractionVisibleRatio { get; set; } = 0.04;

    /// <summary>
    /// 雾化衰减（材料级整体削弱系数）。默认 <b>0.95</b>（任务栏那套是 0.55）。
    ///
    /// <para>它与 <c>FrostedStrength</c> <b>相乘</b>决定最终雾化混合量：
    /// <c>frostMix = blurWeight × FrostedStrength × FrostedAttenuation</c>。
    /// 只调其中一个会被另一个抵消 —— 只把 FrostedStrength 从 0.29 提到 0.62
    /// 而衰减仍是 0.55，实际混合只有 0.34，背景照样清晰。</para>
    /// </summary>
    public double FrostedAttenuation { get; set; } = 0.95;

    /// <summary>每像素路径数。配合时间累积，1~2 条就够。</summary>
    public int PathsPerPixel { get; set; } = 2;

    /// <summary>时间累积帧上限。</summary>
    public int MaxAccumulation { get; set; } = 24;

    /// <summary>打开时先算几帧再上屏（每帧都是阻塞的，别设大）。默认 1。</summary>
    public int WarmupFrames { get; set; } = 1;

    /// <summary>
    /// 两次点击之间的最小间隔（毫秒），默认 200。
    /// 菜单打开会阻塞一两百毫秒，用户以为没反应就会再点一下 —— 这个去抖防止"开了又关"。
    /// </summary>
    public int ClickDebounceMs { get; set; } = 200;

    /// <summary>
    /// 面板**底衬**的不透明度（0 = 纯玻璃全透，1 = 完全不透明）。默认 0.72。
    ///
    /// <para>这是**可读性**参数，不是观感参数：上游的玻璃模型是"边缘透镜 + 中间近乎透明"，
    /// 那是给 1204×116 的细长任务栏设计的。开始菜单是一整块大面板，
    /// 中间那片"近乎透明"的区域会把底下的窗口和桌面图标清晰透上来，
    /// 和菜单自己的文字图标叠在一起根本没法读（用户原话："底层桌面应用和开始菜单应用重叠"）。</para>
    ///
    /// <para>⚠️ 必须与 <c>GlassStartMenu.Settings.ScrimOpacity</c> 的默认值一致 ——
    /// 配置里缺这一项时（老配置文件 + 补段迁移）就会落到那个值上。
    /// 两处不一致会造成"配置明明写了 0.72、实机却在用 0.30"这种极难定位的现象。</para>
    ///
    /// <para>调小更通透但更花；调大更清晰但更不像玻璃。</para>
    /// </summary>
    public double ScrimOpacity { get; set; } = 0.0;

    /// <summary>底衬是否用浅色（浅色主题 true）。文字颜色会自动跟着选深/浅。</summary>
    public bool ScrimIsLight { get; set; } = true;

    // （ScrimOpacity 默认值随 v3.8.0 归零：用户要求"开始菜单和任务栏一样的
    //   液态玻璃效果"，白色底衬已撤；文字可读性由亮度自适应 DrawTextAuto 兜底。
    //   ⚠️ 若将来再调此默认值，GlassStartMenu.Settings.ScrimOpacity 必须同步改，
    //   且 MigrateMissingSections 里要加一条对应旧值的 Upgrade。）

    /// <summary>鼠标光感：让玻璃高光跟着鼠标走（对应上游的 setPointer）。</summary>
    public bool MouseLight { get; set; } = true;
}

public sealed class BehaviorConfig
{
    /// <summary>退出时把系统组件还原为原生外观。</summary>
    public bool RestoreOnExit { get; set; } = true;

    /// <summary>前台窗口全屏时暂停渲染（打游戏、看视频时不要抢 GPU/CPU）。</summary>
    public bool PauseWhenFullscreen { get; set; } = true;

    /// <summary>启动后立即接管，不等托盘菜单。</summary>
    public bool AttachOnStartup { get; set; } = true;

    /// <summary>写日志到 <c>%LOCALAPPDATA%\LiquidGlass\liquidglass.log</c>。</summary>
    public bool WriteLogFile { get; set; } = true;
}

/// <summary>全局配置。</summary>
public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 1;

    public TargetConfig Taskbar { get; set; } = new()
    {
        Enabled = true,
        GlassInsetX = 10,
        GlassInsetY = 6,
    };

    public TargetConfig SecondaryTaskbars { get; set; } = new()
    {
        Enabled = true,
        GlassInsetX = 10,
        GlassInsetY = 6,
    };

    public TargetConfig StartMenu { get; set; } = new()
    {
        Enabled = true,
        GlassInsetX = 6,
        GlassInsetY = 6,
        CornerRadius = 14,
    };

    public TargetConfig Search { get; set; } = new()
    {
        Enabled = false,
        GlassInsetX = 6,
        GlassInsetY = 6,
        CornerRadius = 14,
    };

    public TargetConfig ActionCenter { get; set; } = new()
    {
        Enabled = false,
        GlassInsetX = 6,
        GlassInsetY = 6,
        CornerRadius = 14,
    };

    public TargetConfig Widgets { get; set; } = new()
    {
        Enabled = false,
        GlassInsetX = 6,
        GlassInsetY = 6,
        CornerRadius = 14,
    };

    public TargetConfig TaskView { get; set; } = new()
    {
        Enabled = false,
        GlassInsetX = 0,
        GlassInsetY = 0,
        MakeTargetTransparent = false,
        CornerRadius = 0,
    };

    /// <summary>
    /// 工作模式：
    /// <list type="bullet">
    ///   <item><c>"replace"</c>（默认）：完全隐藏系统任务栏，自建液态玻璃任务栏。</item>
    ///   <item><c>"underlay"</c>：保留系统任务栏，把玻璃垫在它下面（v1 行为）。</item>
    /// </list>
    /// </summary>
    public string Mode { get; set; } = "replace";

    public TaskbarReplacementConfig TaskbarReplacement { get; set; } = new();

    /// <summary>自研的液态玻璃开始菜单（与上面的 StartMenu 目标配置不是一回事）。</summary>
    public GlassStartMenuConfig GlassStartMenu { get; set; } = new();

    public MaterialConfig Material { get; set; } = new();

    public PerformanceConfig Performance { get; set; } = new();

    public SceneConfig Scene { get; set; } = new();

    public BehaviorConfig Behavior { get; set; } = new();

    // ------------------------------------------------------------------ IO

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>默认配置路径：<c>%APPDATA%\LiquidGlass\liquidglass.json</c>。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiquidGlass", "liquidglass.json");

    /// <summary>
    /// 读取配置。文件不存在时写出带完整注释的默认配置并返回默认值 ——
    /// 这样用户第一次运行时就能直接打开文件看到所有可调项。
    ///
    /// <para><b>⚠️ 这里会做一次"缺段补齐"</b>，原因见 <see cref="MigrateMissingSections"/>：
    /// 旧版本生成的配置文件里没有后来新增的段落，反序列化后那些段落会静默地
    /// 回退到<b>代码默认值</b> —— 而代码默认值与本项目精心调过的
    /// <c>config/liquidglass.json</c> 并不相同。实测这就是"重影修好了但实机还有重影"
    /// 的直接原因（运行时的配置里根本没有 <c>glassStartMenu</c> 段，
    /// 于是 <c>scrimOpacity</c> 用的是 0.30 而不是调过的 0.72）。</para>
    /// </summary>
    public static AppConfig Load(string path, Action<string>? log = null)
    {
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            try
            {
                fresh.Save(path);
                log?.Invoke($"已生成默认配置：{path}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"默认配置写入失败（将只用内存配置）：{ex.Message}");
            }
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions) ?? new AppConfig();
            log?.Invoke($"已载入配置：{path}");

            config.MigrateMissingSections(json, path, log);
            return config;
        }
        catch (Exception ex)
        {
            log?.Invoke($"配置解析失败（将使用默认值）：{ex.Message}");
            return new AppConfig();
        }
    }

    /// <summary>
    /// 把配置文件里<b>缺失的段落</b>补成"当前版本认为正确的值"，并回写。
    ///
    /// <para><b>为什么必须有这一步</b>：配置文件的读取是"反序列化到已有默认实例"，
    /// 所以 JSON 里没有的键 = 保留代码里的 <c>= new()</c> 默认值。
    /// 一旦某个版本往配置里新增一段（比如本轮的 <c>glassStartMenu</c>），
    /// 老用户的配置文件里就没有它 —— 程序不会报错，
    /// 但那一整段的所有调优参数全部退回硬编码默认值，
    /// 表现成"改了没用""修了还在"。</para>
    ///
    /// <para>实测踩的坑：运行时的 <c>%APPDATA%</c> 配置停留在旧版本，
    /// 里面没有 <c>glassStartMenu</c> 段，于是开始菜单的底衬一直是 0.30
    /// 而不是调过的 0.72 —— 背景文字就这样一直透上来，也就是用户说的重影。</para>
    ///
    /// <para>补齐策略：只补<b>整个段落级的缺失</b>，不碰用户已经写过的任何键。
    /// 这样既能让新增段生效，又绝不会覆盖用户的显式配置。</para>
    /// </summary>
    private void MigrateMissingSections(string originalJson, string path, Action<string>? log)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(originalJson,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) as JsonObject;
        }
        catch
        {
            return;   // 解析不了就别动了，反正已经拿到了内存默认值
        }

        if (root is null) return;

        var filled = new List<string>();

        // 每个"段落名 → 该段的当前默认实例"。
        // 新增段落时在这里登记一行即可，Load 会自动帮老配置补齐。
        void Need(string name, object section)
        {
            // 大小写不敏感地找 —— 配置文件历史上有 camelCase / PascalCase 两种写法。
            foreach (var key in root.Select(kv => kv.Key).ToList())
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return;
            }

            root[name] = JsonSerializer.SerializeToNode(section, WriteOptions);
            filled.Add(name);
        }

        Need("glassStartMenu", GlassStartMenu);
        Need("taskbarReplacement", TaskbarReplacement);
        Need("performance", Performance);
        Need("scene", Scene);

        // ── 数值升级：把"已知是错的旧默认值"改成当前值 ──
        //
        // 只补段落还不够：老配置里**已经有**的键不会被动。
        // 如果某个键的旧默认值本身就是 bug（而不是用户的选择），就得显式升级。
        // 这里逐条登记，且带上"旧值"这个前提条件 —— 一旦用户自己改成了别的值，
        // 条件不成立，就绝不覆盖（尊重用户的显式配置）。
        var upgraded = new List<string>();

        void Upgrade(string section, string key, double badValue, double goodValue)
        {
            var sec = FindSection(root, section);
            if (sec is null) return;

            var actualKey = sec.Select(kv => kv.Key)
                .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (actualKey is null) return;

            if (sec[actualKey] is not JsonValue v) return;
            if (!v.TryGetValue<double>(out var current)) return;
            if (Math.Abs(current - badValue) > 1e-9) return;   // 用户改过 → 不动

            sec[actualKey] = goodValue;
            upgraded.Add($"{section}.{key} {badValue} → {goodValue}");
        }

        // scrimOpacity 的旧默认值历史：0.14 → 0.30 → 0.72 →（v3.8.0）0.0。
        // 0.72 是 v3.7.x 的"压重影"值，代价是菜单一片奶白；
        // v3.8.0 按用户要求归零（"开始菜单要和任务栏一样的液态玻璃效果"）。
        Upgrade("glassStartMenu", "scrimOpacity", 0.30, 0.0);
        Upgrade("glassStartMenu", "scrimOpacity", 0.14, 0.0);
        Upgrade("glassStartMenu", "scrimOpacity", 0.72, 0.0);

        if (upgraded.Count > 0)
        {
            filled.Add($"数值升级 {string.Join("、", upgraded)}");
        }

        if (filled.Count == 0) return;

        try
        {
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            log?.Invoke($"配置补全：{path} 缺少或过期的项已按当前版本修正"
                + $"（{string.Join("、", filled)}）—— 否则这些参数会静默沿用代码默认值。");
        }
        catch (Exception ex)
        {
            log?.Invoke($"配置补全写回失败（内存里已生效，仅未落盘）：{ex.Message}");
        }
    }

    /// <summary>大小写不敏感地找一段。</summary>
    private static JsonObject? FindSection(JsonObject root, string name)
    {
        foreach (var kv in root)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value as JsonObject;
            }
        }
        return null;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    /// <summary>
    /// 解析 <see cref="PerformanceConfig.QualityMode"/>。
    /// 返回 <c>null</c> 表示交给性能调节器自动决定；否则锁定到指定档位。
    /// </summary>
    public QualityTier? ResolveForcedQualityTier() =>
        Performance.QualityMode?.Trim().ToLowerInvariant() switch
        {
            "minimal" or "min" or "最低" => QualityTier.Minimal,
            "low" or "低" => QualityTier.Low,
            "balanced" or "medium" or "均衡" => QualityTier.Balanced,
            "high" or "高" => QualityTier.High,
            "ultra" or "极致" => QualityTier.Ultra,
            _ => null,   // "auto" 或无法识别 → 自动
        };

    /// <summary>当前是否为"完全隐藏系统任务栏 + 自建任务栏"模式。</summary>
    [JsonIgnore]
    public bool IsReplacementMode =>
        !string.Equals(Mode, "underlay", StringComparison.OrdinalIgnoreCase);

    /// <summary>按界面类别取对应的配置节。</summary>
    public TargetConfig For(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Taskbar => Taskbar,
        SurfaceKind.SecondaryTaskbar => SecondaryTaskbars,
        SurfaceKind.StartMenu => StartMenu,
        SurfaceKind.Search => Search,
        SurfaceKind.ActionCenter => ActionCenter,
        SurfaceKind.Widgets => Widgets,
        SurfaceKind.TaskView => TaskView,
        _ => new TargetConfig { Enabled = false },
    };
}
