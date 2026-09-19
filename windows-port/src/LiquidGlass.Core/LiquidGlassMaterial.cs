namespace LiquidGlass.Core;

/// <summary>
/// 液态玻璃的参考光学材质。
///
/// 本类中的每一个数值都逐字对应上游项目
/// <c>web-liquid-glass/src/layout.js</c> 的 <c>LIQUID_GLASS_REFERENCE</c>，
/// 没有任何"凭感觉调过"的参数。上游文档明确要求：
/// <i>"可以通过 material 局部覆盖，但建议先使用默认值完成视觉验收。"</i>
/// 移植到 Windows 时同样遵循这条纪律。
/// </summary>
public sealed record LiquidGlassMaterial
{
    // ----------------------------------------------------- 表面几何与位移

    /// <summary>反向位移倍率。边缘像素沿外法线向外"卷回"的强度。上游默认 2.19。</summary>
    public double ReverseDisplacement { get; init; } = 2.19;

    /// <summary>边缘曲率。表面法线的倾斜强度，决定透镜的"陡峭度"。上游默认 0.67。</summary>
    public double EdgeCurvature { get; init; } = 0.67;

    /// <summary>光学厚度。玻璃的"厚"程度，直接缩放内部传播距离。上游默认 1.71。</summary>
    public double OpticalThickness { get; init; } = 1.71;

    /// <summary>边缘带宽比例，相对玻璃<b>高度</b>。上游默认 0.26。</summary>
    /// <remarks>
    /// 上游 ARCHITECTURE.md 特别警告：所有按比例定义的光学距离都以
    /// <c>rect.w</c>（高度）为基准，"使用宽度 rect.z 会把效果错误放大"。
    /// 任务栏是 2560×72 的极端长宽比，这一点不遵守会直接炸掉效果。
    /// </remarks>
    public double EdgeBandRatio { get; init; } = 0.26;

    /// <summary>折射可见区比例，相对玻璃高度。上游默认 0.16。</summary>
    public double RefractionVisibleRatio { get; init; } = 0.16;

    /// <summary>折射区与雾化区之间的平滑过渡宽度（像素）。上游默认 12。</summary>
    public double BlendFeatherPx { get; init; } = 12.0;

    // ------------------------------------------------------------ 雾化

    /// <summary>毛玻璃基础强度。上游默认 0.29。</summary>
    public double FrostedStrength { get; init; } = 0.29;

    /// <summary>毛玻璃整体衰减。上游默认 0.55。</summary>
    public double FrostedAttenuation { get; init; } = 0.55;

    /// <summary>高斯采样间距（像素）。上游默认 1.4。</summary>
    /// <remarks>
    /// ⚠️ 只被 <c>GlassOptics.GaussianBlur</c> 使用，而那条路径<b>已不是</b>
    /// 主要的雾化来源。默认雾化走 <see cref="FrostedBlurRadius"/> + 金字塔。
    /// 保留此参数是为了让上游 9 抽头核仍可作为"轻量模式"启用。
    /// </remarks>
    public double BlurSpacingPx { get; init; } = 1.4;

    /// <summary>
    /// 雾化模糊的<b>支撑半径</b>（纹理像素）。默认 96。
    ///
    /// <para>这是真正决定"背景结构能不能被抹掉"的参数。
    /// 抑制周期 T 的结构需要约 T/2 的支撑半径，而面板背后常见的是
    /// 160–480px 周期的文字排版。</para>
    ///
    /// <para>实测（512×512 正弦条纹，残余振幅/原振幅，越小越好）：</para>
    /// <code>
    ///   周期T    半径24   半径64   半径96   半径128
    ///    40px    0.354    0.172      —      0.046
    ///    80px    0.676    0.217      —      0.091
    ///   160px    0.912    0.693      —      0.179
    ///   320px    0.952    0.902      —      0.424
    ///   480px    0.856    0.688      —      0.484
    /// </code>
    ///
    /// <para>半径 24 时 160px 周期几乎原样透出（0.912）—— 那就是"重影"。
    /// 96 是折中：160px 级被压到约 0.35，同时不会把玻璃糊成一片纯色。</para>
    ///
    /// <para>实现方式是从 <c>GlassScene</c> 的多级降采样金字塔取三线性样本，
    /// 因此<b>把它调大不会增加逐像素开销</b>，只是受金字塔级数上限约束
    /// （默认 8 级，最高见 <c>GlassScene.BuildPyramid</c>）。</para>
    /// </summary>
    public double FrostedBlurRadius { get; init; } = 96.0;

    // ------------------------------------------------------ 色散 / 高光 / 染色

    /// <summary>色散最大权重。上游默认 0.22，目标是"低强度色散，不形成彩色描边"。</summary>
    public double DispersionStrength { get; init; } = 0.22;

    /// <summary>对角高光强度。上游默认 0.055。</summary>
    public double HighlightStrength { get; init; } = 0.055;

    /// <summary>整体染色（线性空间）。上游默认 [0.035, 0.035, 0.045]。</summary>
    public double[] TintColor { get; init; } = [0.035, 0.035, 0.045];

    /// <summary>染色混合比例。上游默认 0.17。</summary>
    public double TintMix { get; init; } = 0.17;

    // ------------------------------------------------------------- 光学常数

    /// <summary>红通道折射率。上游默认 1.514。</summary>
    public double IndexOfRefractionRed { get; init; } = 1.514;

    /// <summary>绿通道折射率。上游默认 1.520。</summary>
    public double IndexOfRefractionGreen { get; init; } = 1.520;

    /// <summary>蓝通道折射率。上游默认 1.528。</summary>
    public double IndexOfRefractionBlue { get; init; } = 1.528;

    /// <summary>表面粗糙度，作为法线扰动半径。上游默认 0.014。</summary>
    public double Roughness { get; init; } = 0.014;

    /// <summary>相机射线倾斜比例，由鼠标位置驱动。上游默认 0.045。</summary>
    public double CameraRayScale { get; init; } = 0.045;

    /// <summary>光线 z 分量的下限，防止除零。上游默认 0.08。</summary>
    public double MinimumRayZ { get; init; } = 0.08;

    /// <summary>入口段基础传播距离（像素）。上游默认 6.0。</summary>
    public double InsideTravelBasePx { get; init; } = 6.0;

    /// <summary>内部传播距离随内部平滑度缩放的比例。上游默认 0.28。</summary>
    public double InsideTravelInteriorScale { get; init; } = 0.28;

    /// <summary>出口段基础传播距离（像素）。上游默认 6.5。</summary>
    public double ExitTravelBasePx { get; init; } = 6.5;

    /// <summary>反向位移的像素上限。上游默认 36。</summary>
    /// <remarks>任务栏高 72px 时，边缘带 18.7px，36px 上限不会触发；但在高 DPI 下会。</remarks>
    public double ReverseMaxPixels { get; init; } = 36.0;

    /// <summary>透镜混合：折射偏移有多少最终作用到采样坐标上。上游默认 0.35。</summary>
    public double LensMix { get; init; } = 0.35;

    /// <summary>
    /// 文字遮罩保护强度。上游默认 0.025——刻意做得极轻，
    /// 注释原话是 "The mask only protects text very lightly. It never enables strong blur."
    /// </summary>
    public double TextMaskProtection { get; init; } = 0.025;

    /// <summary>雾化层的冷色偏移。上游默认 [0.055, 0.062, 0.075]。</summary>
    public double[] FrostTintColor { get; init; } = [0.055, 0.062, 0.075];

    /// <summary>第一条对角高光的宽度。上游默认 0.22。</summary>
    public double HighlightAWidth { get; init; } = 0.22;

    /// <summary>第一条对角高光的中心位置。上游默认 0.22。</summary>
    public double HighlightACenter { get; init; } = 0.22;

    /// <summary>第二条对角高光的宽度。上游默认 0.20。</summary>
    public double HighlightBWidth { get; init; } = 0.20;

    /// <summary>第二条对角高光的中心位置。上游默认 1.78。</summary>
    public double HighlightBCenter { get; init; } = 1.78;

    /// <summary>
    /// 圆角半径覆盖值（像素）。<c>-1</c> 表示沿用上游的自动规则
    /// <c>max(1, min(halfW, halfH) - 1)</c>。
    /// </summary>
    /// <remarks>
    /// 这是<b>唯一</b>一个上游没有的材质参数，为 Windows 场景而加。
    /// 上游的导航栏接近正方形端头，自动规则天然合理；
    /// 但 Windows 任务栏是 2560×72 的极端长宽比，自动规则会得到
    /// 半径 35 的完整胶囊。有些人想要那个胶囊，有些人想要 12px 的圆角矩形，
    /// 所以这里开放覆盖。默认 <c>-1</c> 保证默认行为与上游逐位一致。
    /// </remarks>
    public double CornerRadius { get; init; } = -1;

    /// <summary>完全使用上游参考值。</summary>
    public static LiquidGlassMaterial Reference { get; } = new();

    /// <summary>
    /// 克隆并局部覆盖。对应上游 API 的 <c>material</c> 选项：
    /// 只改你明确指定的字段，其余保持参考值。
    /// </summary>
    public LiquidGlassMaterial With(Action<MaterialBuilder> configure)
    {
        var builder = new MaterialBuilder(this);
        configure(builder);
        return builder.Build();
    }
}

/// <summary>
/// <see cref="LiquidGlassMaterial"/> 的可变构建器。
/// C# 的 <c>record with</c> 无法表达"只覆盖我指定的字段"，
/// 所以这里用一个轻量构建器承接配置文件里的稀疏覆盖。
/// </summary>
public sealed class MaterialBuilder
{
    private readonly LiquidGlassMaterial _source;

    internal MaterialBuilder(LiquidGlassMaterial source) => _source = source;

    public double? ReverseDisplacement { get; set; }
    public double? EdgeCurvature { get; set; }
    public double? OpticalThickness { get; set; }
    public double? EdgeBandRatio { get; set; }
    public double? RefractionVisibleRatio { get; set; }
    public double? BlendFeatherPx { get; set; }
    public double? FrostedStrength { get; set; }
    public double? FrostedAttenuation { get; set; }
    public double? BlurSpacingPx { get; set; }
    public double? FrostedBlurRadius { get; set; }
    public double? DispersionStrength { get; set; }
    public double? HighlightStrength { get; set; }
    public double[]? TintColor { get; set; }
    public double? TintMix { get; set; }
    public double? IndexOfRefractionRed { get; set; }
    public double? IndexOfRefractionGreen { get; set; }
    public double? IndexOfRefractionBlue { get; set; }
    public double? Roughness { get; set; }
    public double? CameraRayScale { get; set; }
    public double? MinimumRayZ { get; set; }
    public double? InsideTravelBasePx { get; set; }
    public double? InsideTravelInteriorScale { get; set; }
    public double? ExitTravelBasePx { get; set; }
    public double? ReverseMaxPixels { get; set; }
    public double? LensMix { get; set; }
    public double? TextMaskProtection { get; set; }
    public double[]? FrostTintColor { get; set; }
    public double? HighlightAWidth { get; set; }
    public double? HighlightACenter { get; set; }
    public double? HighlightBWidth { get; set; }
    public double? HighlightBCenter { get; set; }
    public double? CornerRadius { get; set; }

    internal LiquidGlassMaterial Build() => new()
    {
        ReverseDisplacement = ReverseDisplacement ?? _source.ReverseDisplacement,
        EdgeCurvature = EdgeCurvature ?? _source.EdgeCurvature,
        OpticalThickness = OpticalThickness ?? _source.OpticalThickness,
        EdgeBandRatio = EdgeBandRatio ?? _source.EdgeBandRatio,
        RefractionVisibleRatio = RefractionVisibleRatio ?? _source.RefractionVisibleRatio,
        BlendFeatherPx = BlendFeatherPx ?? _source.BlendFeatherPx,
        FrostedStrength = FrostedStrength ?? _source.FrostedStrength,
        FrostedAttenuation = FrostedAttenuation ?? _source.FrostedAttenuation,
        BlurSpacingPx = BlurSpacingPx ?? _source.BlurSpacingPx,
        FrostedBlurRadius = FrostedBlurRadius ?? _source.FrostedBlurRadius,
        DispersionStrength = DispersionStrength ?? _source.DispersionStrength,
        HighlightStrength = HighlightStrength ?? _source.HighlightStrength,
        TintColor = TintColor ?? _source.TintColor,
        TintMix = TintMix ?? _source.TintMix,
        IndexOfRefractionRed = IndexOfRefractionRed ?? _source.IndexOfRefractionRed,
        IndexOfRefractionGreen = IndexOfRefractionGreen ?? _source.IndexOfRefractionGreen,
        IndexOfRefractionBlue = IndexOfRefractionBlue ?? _source.IndexOfRefractionBlue,
        Roughness = Roughness ?? _source.Roughness,
        CameraRayScale = CameraRayScale ?? _source.CameraRayScale,
        MinimumRayZ = MinimumRayZ ?? _source.MinimumRayZ,
        InsideTravelBasePx = InsideTravelBasePx ?? _source.InsideTravelBasePx,
        InsideTravelInteriorScale = InsideTravelInteriorScale ?? _source.InsideTravelInteriorScale,
        ExitTravelBasePx = ExitTravelBasePx ?? _source.ExitTravelBasePx,
        ReverseMaxPixels = ReverseMaxPixels ?? _source.ReverseMaxPixels,
        LensMix = LensMix ?? _source.LensMix,
        TextMaskProtection = TextMaskProtection ?? _source.TextMaskProtection,
        FrostTintColor = FrostTintColor ?? _source.FrostTintColor,
        HighlightAWidth = HighlightAWidth ?? _source.HighlightAWidth,
        HighlightACenter = HighlightACenter ?? _source.HighlightACenter,
        HighlightBWidth = HighlightBWidth ?? _source.HighlightBWidth,
        HighlightBCenter = HighlightBCenter ?? _source.HighlightBCenter,
        CornerRadius = CornerRadius ?? _source.CornerRadius,
    };
}
