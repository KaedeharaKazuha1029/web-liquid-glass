namespace LiquidGlass.Core;

/// <summary>画质档位。数值越大越精致、越吃性能。</summary>
public enum QualityTier
{
    /// <summary>最省。给上网本 / 虚拟机 / 远程桌面用。</summary>
    Minimal,
    /// <summary>省电档。对应上游移动端的 2 paths × 24 帧。</summary>
    Low,
    /// <summary>均衡档。</summary>
    Balanced,
    /// <summary>参考档。paths 与累积帧数与上游桌面参考值一致。</summary>
    High,
    /// <summary>加料档。超出上游参考值，给高配机器用。</summary>
    Ultra,
}

/// <summary>
/// 一个画质档位的全部工作量参数。
///
/// <para>⚠️ <b>光学材质不在这里</b>。上游对此有明确纪律：
/// 性能档只调"工作量"，折射率、色散、边缘带宽这些"看起来像不像玻璃"的参数
/// 在任何档位下都保持一致 —— 否则低配机器上的玻璃根本不像同一块玻璃。
/// 这条纪律在 <c>DEFAULT_MOBILE_PERFORMANCE</c> 的注释里写得很清楚。</para>
/// </summary>
public sealed record QualityProfile(
    QualityTier Tier,
    int PathsPerPixel,
    int IdleAccumulation,
    int DynamicAccumulation,
    double Supersample,
    int CaptureIntervalMs,
    string Note)
{
    /// <summary>
    /// 相对开销的粗略度量。逐像素路径数与画布像素数成正比，
    /// 因此开销 ∝ 路径数 × 超采样倍数的平方。
    /// 累积帧数<b>不</b>计入 —— 累积是跨帧摊开的，单帧成本与它无关。
    /// </summary>
    public double RelativeCost => PathsPerPixel * Supersample * Supersample;

    public override string ToString() =>
        $"{Tier,-9} {PathsPerPixel} paths × {IdleAccumulation} 帧"
        + $"（动态 {DynamicAccumulation}）· 超采样 {Supersample:0.##}× · 抓屏 {CaptureIntervalMs}ms";
}

/// <summary>预置档位表。</summary>
public static class QualityProfiles
{
    private static readonly QualityProfile[] Table =
    [
        new(QualityTier.Minimal, PathsPerPixel: 1, IdleAccumulation: 12, DynamicAccumulation: 2,
            Supersample: 1.0, CaptureIntervalMs: 100,
            Note: "极省电，噪点靠时间累积压住，静止后仍然干净"),

        // 上游 DEFAULT_MOBILE_PERFORMANCE 的 pathsPerPixel=2 / maxAccumulation=24 / captureMinIntervalMs=50
        new(QualityTier.Low, PathsPerPixel: 2, IdleAccumulation: 24, DynamicAccumulation: 4,
            Supersample: 1.0, CaptureIntervalMs: 66,
            Note: "对应上游移动端档位（2 paths × 24 帧）"),

        new(QualityTier.Balanced, PathsPerPixel: 3, IdleAccumulation: 32, DynamicAccumulation: 6,
            Supersample: 1.0, CaptureIntervalMs: 50,
            Note: "均衡"),

        // 上游 LIQUID_GLASS_REFERENCE.rendering：pathsPerPixel=4 / maxAccumulation=48 / maxDpr=1.5
        new(QualityTier.High, PathsPerPixel: 4, IdleAccumulation: 48, DynamicAccumulation: 8,
            Supersample: 1.25, CaptureIntervalMs: 33,
            Note: "上游桌面参考档（4 paths × 48 帧 × maxDpr 1.5）"),

        new(QualityTier.Ultra, PathsPerPixel: 8, IdleAccumulation: 64, DynamicAccumulation: 12,
            Supersample: 1.5, CaptureIntervalMs: 33,
            Note: "超出上游参考值，给高配机器"),
    ];

    public static QualityProfile For(QualityTier tier) =>
        Table.First(p => p.Tier == tier);

    public static IReadOnlyList<QualityProfile> All => Table;

    /// <summary>按档位从低到高枚举，用于升降档。</summary>
    public static QualityTier Next(QualityTier tier, int delta)
    {
        var index = Math.Clamp((int)tier + delta, 0, Table.Length - 1);
        return Table[index].Tier;
    }
}

/// <summary>
/// 画质调节器：<b>启动时实测基准 + 运行时闭环</b>。
///
/// <para>为什么不能只看硬件参数：同样 8 核 CPU，插电和用电池、
/// 有没有别的程序在抢、集成显卡还是独显，实际渲染速度能差三五倍。
/// 唯一可靠的做法就是<b>拿真实的工作负载去测</b>。</para>
///
/// <para>闭环部分仿照游戏里的动态画质：每帧回报实测耗时，
/// 与预算比较，长期超标就降档、长期富余就升档。带滞回与冷却，
/// 避免在档位边界来回横跳（那比一直待在低档还难看）。</para>
/// </summary>
public sealed class PerformanceGovernor
{
    private readonly Action<string>? _log;
    private readonly double _budgetMs;

    private readonly double[] _frameTimes = new double[16];
    private int _frameCount;
    private int _frameCursor;

    private int _overBudgetStreak;
    private int _underBudgetStreak;
    private int _cooldown;
    private int _warmupFrames;

    /// <summary>当前档位。</summary>
    public QualityTier Tier { get; private set; } = QualityTier.High;

    /// <summary>当前档位对应的工作量参数。</summary>
    public QualityProfile Profile => QualityProfiles.For(Tier);

    /// <summary>最近帧耗时的滑动平均（毫秒）。</summary>
    public double SmoothedFrameMs { get; private set; }

    /// <summary>单帧渲染预算（毫秒）。</summary>
    public double BudgetMs => _budgetMs;

    /// <summary>是否被用户锁定（<c>qualityMode</c> 指定了具体档位时不做自适应）。</summary>
    public bool IsLocked { get; }

    /// <summary>自上次报告以来档位是否发生过变化。</summary>
    public bool TierChanged { get; private set; }

    public PerformanceGovernor(double budgetMs, QualityTier? forced = null, Action<string>? log = null)
    {
        _budgetMs = Math.Max(1, budgetMs);
        _log = log;

        if (forced is { } tier)
        {
            Tier = tier;
            IsLocked = true;
            _log?.Invoke($"画质档位被配置锁定为 {tier}（{QualityProfiles.For(tier)}）。");
        }
    }

    /// <summary>
    /// 用一次真实渲染的实测耗时确定初始档位。
    /// </summary>
    /// <param name="measuredMs">在基准配置（4 paths、超采样 1.0）下渲染一帧的实测耗时。</param>
    /// <param name="width">画布宽（像素），用于日志。</param>
    /// <param name="height">画布高（像素）。</param>
    public void InitializeFromProbe(double measuredMs, int width, int height)
    {
        if (IsLocked) return;

        // 基准配置的相对开销：4 paths × 1.0² = 4
        const double baselineCost = 4.0;

        // 安全系数：微型基准测的是"干净环境下单次渲染"，
        // 而真实循环里还有抓屏、场景转换、合成的固定开销，
        // 以及缓存压力与别的线程抢占。不乘这个系数就会把档位定得偏乐观，
        // 然后闭环又要花半秒才降下来 —— 用户会看到一段明显的卡顿。
        const double safetyFactor = 1.4;
        var perCostUnit = measuredMs / baselineCost * safetyFactor;

        QualityTier chosen = QualityTier.Minimal;
        var chosenEstimate = double.MaxValue;

        // 从高到低挑第一个能在预算内跑完的档位。
        foreach (var profile in QualityProfiles.All.Reverse())
        {
            var estimate = perCostUnit * profile.RelativeCost;
            if (estimate <= _budgetMs)
            {
                chosen = profile.Tier;
                chosenEstimate = estimate;
                break;
            }
        }

        Tier = chosen;
        TierChanged = true;

        _log?.Invoke($"性能实测：基准 {measuredMs:F1}ms（4 paths、1.0× 超采样、{width}×{height}），"
            + $"含 {safetyFactor:0.##}× 安全系数后 {perCostUnit * baselineCost:F1}ms，"
            + $"单帧预算 {_budgetMs:F0}ms");
        _log?.Invoke($"初始画质档位 → {Profile}（预估 {chosenEstimate:F1}ms/帧）");
    }

    /// <summary>
    /// 声明接下来若干帧是冷启动，不要计入判据。
    ///
    /// <para>画布尺寸刚变化（超采样倍率切换、任务栏宽度变化）时，
    /// 头几帧要承担缓冲分配与缓存预热的开销，实测能比稳态高好几倍。
    /// 把它们算进去会让调节器误判为"这台机器跑不动"，白白降档。</para>
    /// </summary>
    public void BeginWarmup(int frames) => _warmupFrames = Math.Max(_warmupFrames, frames);

    /// <summary>每渲染一帧后回报耗时，驱动闭环调节。</summary>
    public void ReportFrame(double elapsedMs)
    {
        TierChanged = false;

        // 冷启动帧只丢弃，不参与任何统计。
        if (_warmupFrames > 0)
        {
            _warmupFrames--;
            return;
        }

        _frameTimes[_frameCursor] = elapsedMs;
        _frameCursor = (_frameCursor + 1) % _frameTimes.Length;
        if (_frameCount < _frameTimes.Length) _frameCount++;

        var sum = 0.0;
        for (var i = 0; i < _frameCount; i++) sum += _frameTimes[i];
        SmoothedFrameMs = sum / _frameCount;

        if (IsLocked || _frameCount < 8) return;
        if (_cooldown > 0) { _cooldown--; return; }

        // 滞回：超标要明显超标才降，富余要明显富余才升。
        // 单侧的连击计数避免偶发抖动（GC、系统抢占）触发误判。
        if (SmoothedFrameMs > _budgetMs * 1.15)
        {
            _overBudgetStreak++;
            _underBudgetStreak = 0;
        }
        else if (SmoothedFrameMs < _budgetMs * 0.5)
        {
            _underBudgetStreak++;
            _overBudgetStreak = 0;
        }
        else
        {
            _overBudgetStreak = 0;
            _underBudgetStreak = 0;
        }

        if (_overBudgetStreak >= 4 && Tier != QualityTier.Minimal)
        {
            ChangeTier(QualityProfiles.Next(Tier, -1), "持续超出预算");
        }
        else if (_underBudgetStreak >= 6 && Tier != QualityTier.Ultra)
        {
            // 升档的门槛比降档低，理由是它<b>自我纠正</b>：
            // 万一升上去发现跑不动，降档路径 4 帧内就会把它拉回来，
            // 用户最多看到零点几秒的轻微掉帧。
            // 反过来，门槛太高会让机器长期停在偏低档位 ——
            // 收敛后渲染变稀疏，样本本来就攒得慢。
            ChangeTier(QualityProfiles.Next(Tier, +1), "长期富余");
        }
    }

    private void ChangeTier(QualityTier next, string reason)
    {
        if (next == Tier) return;

        var direction = next > Tier ? "升" : "降";
        Tier = next;
        TierChanged = true;

        _overBudgetStreak = 0;
        _underBudgetStreak = 0;
        _cooldown = 16;              // 换档后冷却约 0.5 秒再评估
        _frameCount = 0;             // 旧档位的耗时样本对新档位没有参考价值

        _log?.Invoke($"画质{direction}档 → {Profile}"
            + $"（{reason}，实测 {SmoothedFrameMs:F1}ms / 预算 {_budgetMs:F0}ms）");
    }

    /// <summary>一句话状态，供托盘与日志展示。</summary>
    public string Describe()
    {
        var lockNote = IsLocked ? "（已锁定）" : "";
        return $"{Tier}{lockNote} · {Profile.PathsPerPixel} paths · "
             + $"{SmoothedFrameMs:F1}ms/{_budgetMs:F0}ms";
    }
}
