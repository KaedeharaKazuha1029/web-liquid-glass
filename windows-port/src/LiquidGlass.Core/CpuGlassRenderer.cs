namespace LiquidGlass.Core;

/// <summary>一次渲染的输入参数。</summary>
public sealed record GlassRenderOptions
{
    /// <summary>玻璃矩形，相对画布左上角（画布局部像素坐标）。</summary>
    public required PixelRect GlassRect { get; init; }

    public required int CanvasWidth { get; init; }
    public required int CanvasHeight { get; init; }

    /// <summary>每像素路径数。上游桌面参考值 4。调小可线性降低 CPU 开销。</summary>
    public int PathsPerPixel { get; init; } = 4;

    /// <summary>最大时间累积帧数。上游桌面参考值 48。</summary>
    public int MaxAccumulation { get; init; } = 48;

    /// <summary>鼠标位置，归一化到玻璃矩形内的 [0,1]。驱动相机射线的视差倾斜。</summary>
    public float MouseX { get; init; } = 0.5f;

    public float MouseY { get; init; } = 0.5f;

    /// <summary>强制丢弃历史帧，从零重新累积。场景变化 / 几何变化时必须置位。</summary>
    public bool ForceReset { get; init; }

    /// <summary>
    /// 即使已经收敛也重新算一遍，但仍然保留历史累积（与 <see cref="ForceReset"/> 不同）。
    ///
    /// <para>用途：性能调节器需要持续的耗时样本才能判断机器还有多少余量，
    /// 而"收敛后直接返回缓存"会让它再也拿不到样本。这个开关让调用方能
    /// 每隔几秒主动跑一次真实计算来采样，画面几乎不变（此时历史权重高达 47/48）。</para>
    /// </summary>
    public bool ForceRecompute { get; init; }
}

/// <summary>一次渲染的统计。</summary>
public readonly record struct GlassRenderStats(
    int Frame,
    bool Converged,
    bool UsedCache,
    double ElapsedMs,
    long PixelsTraced);

/// <summary>
/// 液态玻璃的 CPU 参考渲染器。
///
/// 上游把结果累积在两块 <c>RGBA16F</c> framebuffer 里乒乓读写；
/// 这里用两个 <c>float[]</c> 做同样的事——线性光空间、每像素 3 个 float，
/// 外加一张覆盖率缓冲充当 Alpha。
///
/// 时间累积是"每像素只有 4 条路径"却看不出噪点的关键：
/// 每帧的随机反射事件不同，按 <c>frame / (frame + 1)</c> 与历史融合，
/// 48 帧后收敛；收敛后<b>整个渲染直接冻结</b>（上游同样如此），
/// 因此稳态 CPU 开销为零——这正是 CPU 后端在系统级场景下可行的原因。
/// </summary>
public sealed class CpuGlassRenderer
{
    private readonly GlassOptics _optics;
    private readonly object _sync = new();

    private float[] _current = [];
    private float[] _previous = [];
    private float[] _coverage = [];
    private int _width;
    private int _height;
    private int _frame;
    private bool _needsReset = true;
    private BgraFrame? _cachedOutput;
    private PixelTile _tile;

    /// <summary>是否已经累积到上限（此时继续渲染是纯浪费）。</summary>
    public bool IsConverged { get; private set; }

    /// <summary>当前帧号。</summary>
    public int Frame => _frame;

    public CpuGlassRenderer(LiquidGlassMaterial? material = null)
    {
        _optics = new GlassOptics(material ?? LiquidGlassMaterial.Reference);
    }

    public LiquidGlassMaterial Material => _optics.Material;

    /// <summary>请求丢弃历史、重新累积。场景或几何发生变化时必须调用。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _needsReset = true;
            IsConverged = false;
        }
    }

    /// <summary>最近一次渲染统计。</summary>
    public GlassRenderStats LastStats { get; private set; }

    /// <summary>
    /// 渲染一帧。返回的画布像素为 sRGB、straight alpha。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>返回的 <see cref="BgraFrame"/> 是内核复用的缓冲</b>，
    /// 其内容在下一次调用 <see cref="Render"/> 时会被覆盖。
    /// 需要跨帧持有或比较时，请先调用 <see cref="BgraFrame.Clone"/>。
    /// 这样设计是为了避免每帧分配一块画布大小的托管数组——
    /// 2560×72 的画布每帧就是 737KB，30fps 下会产生持续的中等代 GC 压力。
    /// </remarks>
    public BgraFrame Render(GlassScene scene, GlassRenderOptions options)
    {
        lock (_sync)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            EnsureBuffers(options.CanvasWidth, options.CanvasHeight);

            // 已经收敛且没有新的重置请求 → 原样返回上一帧（上游：outColor = previous）。
            if (IsConverged && !options.ForceReset && !options.ForceRecompute
                && !_needsReset && _cachedOutput is not null)
            {
                LastStats = new GlassRenderStats(_frame, true, true, 0, 0);
                return _cachedOutput;
            }

            var reset = options.ForceReset || _needsReset;
            if (reset)
            {
                _frame = 0;
                _needsReset = false;
                Array.Clear(_previous);
            }

            var pathCount = Math.Clamp(options.PathsPerPixel, 1, 12);
            var frameValue = _frame;
            var rect = options.GlassRect;
            var mouseX = options.MouseX;
            var mouseY = options.MouseY;
            var width = options.CanvasWidth;
            var height = options.CanvasHeight;

            // ---- 只栅格化玻璃所在的包围盒 ----
            // 这是本后端最重要的性能杠杆：一块玻璃通常只占画布的百分之几，
            // 但朴素实现会对整张画布跑一遍所有逐像素逻辑（包括 3 通道 pow 转换）。
            var tile = ComputeTile(rect, width, height);
            if (tile.Width <= 0 || tile.Height <= 0 || _tile != tile)
            {
                // 包围盒变了 ⇒ 历史累积不再可信，必须重新开始。
                if (_tile.Width != 0 && _tile != tile)
                {
                    Array.Clear(_previous);
                    Array.Clear(_coverage);
                    _frame = 0;
                    frameValue = 0;
                    // 输出帧也不能复用：否则旧包围盒里的玻璃会残留成鬼影。
                    _cachedOutput = null;
                }
                _tile = tile;
            }

            if (tile.Width <= 0 || tile.Height <= 0)
            {
                var empty = new BgraFrame(width, height);
                _cachedOutput = empty;
                IsConverged = true;
                LastStats = new GlassRenderStats(_frame, true, false, 0, 0);
                return empty;
            }

            var accumulationLimit = options.MaxAccumulation - 1;
            var accumulation = Math.Min(_frame, accumulationLimit);
            var historyWeight = reset ? 0f : accumulation / (float)(accumulation + 1);

            long tracedPixels = 0;

            Parallel.For(tile.Y, tile.Bottom, y =>
            {
                var py = y + 0.5;
                var rowBase = y * width;
                var localTraced = 0L;

                for (var x = tile.X; x < tile.Right; x++)
                {
                    var px = x + 0.5;
                    _optics.EvaluatePixel(scene, rect, px, py, mouseX, mouseY,
                        pathCount, frameValue,
                        out var r, out var g, out var b, out var coverage);

                    var i = (rowBase + x) * 3;
                    _current[i] = r;
                    _current[i + 1] = g;
                    _current[i + 2] = b;
                    _coverage[rowBase + x] = coverage;

                    if (coverage > 0f) localTraced++;
                }

                Interlocked.Add(ref tracedPixels, localTraced);
            });

            // ---- 时间累积：out = mix(current, previous, accumulation / (accumulation + 1)) ----
            if (historyWeight > 0f)
            {
                var oneMinus = 1f - historyWeight;
                Parallel.For(tile.Y, tile.Bottom, y =>
                {
                    var rowStart = y * width * 3;
                    for (var i = tile.X * 3; i < tile.Right * 3; i++)
                    {
                        var idx = rowStart + i;
                        _current[idx] = _current[idx] * oneMinus + _previous[idx] * historyWeight;
                    }
                });
            }

            // 交换缓冲 —— 上游的 ping-pong。
            (_previous, _current) = (_current, _previous);

            // ---- Display pass：线性 → sRGB（仅包围盒）----
            var output = _cachedOutput is { } cached
                         && cached.Width == width && cached.Height == height
                ? cached
                : new BgraFrame(width, height);

            var px4 = output.Pixels;
            var prev = _previous;
            var cov = _coverage;
            for (var y = tile.Y; y < tile.Bottom; y++)
            {
                var row3 = y * width * 3;
                var row4 = y * width * 4;
                for (var x = tile.X; x < tile.Right; x++)
                {
                    var s = row3 + x * 3;
                    var d = row4 + x * 4;
                    px4[d] = GlassScene.LinearToSrgbByte(prev[s + 2]);      // B
                    px4[d + 1] = GlassScene.LinearToSrgbByte(prev[s + 1]);  // G
                    px4[d + 2] = GlassScene.LinearToSrgbByte(prev[s]);      // R
                    var a = cov[y * width + x];
                    px4[d + 3] = a <= 0f ? (byte)0 : a >= 1f ? (byte)255 : (byte)(a * 255f + 0.5f);
                }
            }

            _frame++;
            _cachedOutput = output;
            IsConverged = _frame >= options.MaxAccumulation;

            stopwatch.Stop();
            LastStats = new GlassRenderStats(
                _frame, IsConverged, false, stopwatch.Elapsed.TotalMilliseconds, tracedPixels);

            return output;
        }
    }

    /// <summary>
    /// 计算需要栅格化的像素包围盒。
    /// 覆盖率在 <c>rectDistance &lt; 1</c> 时归零（抗锯齿宽度恒为 1px），
    /// 所以向外扩 2px 即可确保不遗漏任何非零覆盖率像素。
    /// </summary>
    private static PixelTile ComputeTile(in PixelRect rect, int canvasWidth, int canvasHeight)
    {
        var x0 = (int)Math.Floor(rect.X) - 2;
        var y0 = (int)Math.Floor(rect.Y) - 2;
        var x1 = (int)Math.Ceiling(rect.Right) + 2;
        var y1 = (int)Math.Ceiling(rect.Bottom) + 2;

        x0 = Math.Clamp(x0, 0, canvasWidth);
        y0 = Math.Clamp(y0, 0, canvasHeight);
        x1 = Math.Clamp(x1, 0, canvasWidth);
        y1 = Math.Clamp(y1, 0, canvasHeight);

        return new PixelTile(x0, y0, x1, y1);
    }

    /// <summary>像素包围盒（半开区间）。</summary>
    private readonly record struct PixelTile(int X, int Y, int Right, int Bottom)
    {
        public int Width => Right - X;
        public int Height => Bottom - Y;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_width == width && _height == height && _previous.Length == width * height * 3) return;

        _width = width;
        _height = height;
        _previous = new float[width * height * 3];
        _current = new float[width * height * 3];
        _coverage = new float[width * height];
        _frame = 0;
        _needsReset = true;
        IsConverged = false;
        _cachedOutput = null;
        _tile = default;
    }

    /// <summary>为离线预览重置全部状态。</summary>
    public void Invalidate() => Reset();
}
