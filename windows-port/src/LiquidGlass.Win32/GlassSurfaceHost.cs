using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>折射源的取法。</summary>
public enum SceneSourceMode
{
    /// <summary>
    /// 直接取玻璃背后的屏幕像素。等价于上游的 1:1 DOM 捕获，是默认值。
    /// </summary>
    /// <remarks>
    /// ⚠️ 在 Windows 上有一个必须知道的物理限制：
    /// 窗口不会延伸到工作区之外，所以任务栏背后<b>永远只是壁纸的最下面那一条</b>。
    /// 如果壁纸是平滑渐变，折射就几乎没有内容可弯折，玻璃看上去只会"变透明"。
    /// 这不是实现缺陷，而是场景本身的频率不足——
    /// 详细壁纸（照片、纹理）下效果立即显现。想要更明显的折射请改用
    /// <see cref="ExtendFromAbove"/>。
    /// </remarks>
    Direct,

    /// <summary>
    /// 把紧贴玻璃上方那一行像素向下延伸，作为折射源。
    /// </summary>
    /// <remarks>
    /// 这是一条针对 Windows 的适配：既然任务栏遮挡的内容无法直接观测，
    /// 就用"未被遮挡的最后一行"去重建它。对水平连续的壁纸几乎无损，
    /// 对最大化窗口则能把窗口底部的内容（文字、图片）带到玻璃下面，
    /// 让折射真正有东西可以弯折。
    /// </remarks>
    ExtendFromAbove,
}

/// <summary>接管某个系统界面时的配置。</summary>
public sealed record GlassSurfaceSettings
{
    /// <summary>玻璃左右相对目标矩形的内缩（像素）。任务栏左右内缩会得到一条悬浮玻璃条。</summary>
    public double GlassInsetX { get; init; } = 10;

    /// <summary>玻璃上下相对目标矩形的内缩（像素）。</summary>
    public double GlassInsetY { get; init; } = 6;

    /// <summary>场景纹理向外扩的边距（像素）。折射最远会采样到边缘外约 40px，留 96px 很安全。</summary>
    public double SceneMargin { get; init; } = 96;

    /// <summary>折射源的取法。默认 <see cref="SceneSourceMode.Direct"/>，与上游 1:1 映射一致。</summary>
    public SceneSourceMode SceneSource { get; init; } = SceneSourceMode.Direct;

    /// <summary>
    /// 是否把目标窗口的原生背景抹掉。
    /// 这是"背景接管"模式的前提：不抹掉，紧贴下方的玻璃层根本看不见。
    /// </summary>
    public bool MakeTargetTransparent { get; init; } = true;

    /// <summary>Accent 的渐变颜色（0xAABBGGRR）。全透明即 <c>0x00000000</c>。</summary>
    public uint AccentColorAbgr { get; init; } = 0x00000000;

    /// <summary>
    /// 是否把玻璃层排除在屏幕捕获之外。
    /// 开：稳态零抖动、零反馈（推荐）。
    /// 关：每帧需要"临时隐藏 → 抓屏 → 恢复"，会有极低概率抓到中间帧。
    /// </summary>
    public bool ExcludeOverlayFromCapture { get; init; } = true;

    /// <summary>每像素路径数。上调更细腻、更慢。</summary>
    public int PathsPerPixel { get; init; } = 4;

    /// <summary>静止时的累积帧上限。</summary>
    public int IdleAccumulation { get; init; } = 48;

    /// <summary>
    /// 鼠标在玻璃上移动时的累积帧上限。
    /// 上游没有这个概念（浏览器里胶囊有独立的 1 path × 8 帧预算）；
    /// 这里用它来换取"鼠标划过时仍然跟手"——否则每次移动都要重跑 48 帧。
    /// </summary>
    public int MovingAccumulation { get; init; } = 12;

    /// <summary>是否让鼠标位置驱动相机射线的视差倾斜（对应上游的 uMouse）。</summary>
    public bool MouseParallax { get; init; } = true;

    public LiquidGlassMaterial Material { get; init; } = LiquidGlassMaterial.Reference;
}

/// <summary>
/// 把一个系统界面（任务栏 / 开始菜单 / 搜索 …）接管成一块液态玻璃。
///
/// 工作链条：
/// <code>
///   ① SetWindowCompositionAttribute(TRANSPARENTGRADIENT, α=0)
///        抹掉系统组件的原生背景
///   ② 创建 WS_EX_LAYERED 覆盖窗口，SetWindowPos 插到组件的正下方
///   ③ BitBlt 抓取该区域的桌面像素（玻璃层已被排除在捕获之外）
///   ④ 光学核心渲染 → UpdateLayeredWindow 逐像素 Alpha 上屏
///   ⑤ 系统图标与文字由 explorer 绘制，天然浮在玻璃之上
/// </code>
///
/// 第 ⑤ 步是这套方案最漂亮的地方：我们完全不需要重绘任务栏上的任何图标，
/// 它们本来就画在组件窗口里，而那个窗口现在已经透明了。
/// </summary>
public sealed class GlassSurfaceHost : IDisposable
{
    private readonly Action<string> _log;
    private readonly GlassSurfaceSettings _settings;
    private readonly CpuGlassRenderer _renderer;
    private readonly LayeredOverlayWindow _overlay;
    private readonly ScreenSampler _sampler = new();

    private Win32Rect _targetBounds;
    private Win32Rect _canvasRect;
    private PixelRect _glassRect;
    private bool _lastVisible;
    private bool _composedOnce;
    private bool _captureExclusionActive;
    private bool _disposed;

    /// <summary>目标窗口句柄。窗口被销毁后需要重新 Attach。</summary>
    public IntPtr TargetHandle { get; private set; }

    public SurfaceKind Kind { get; }

    /// <summary>Accent 是否成功下发（不代表系统真的接受了，见液态玻璃探针的说明）。</summary>
    public bool TargetMadeTransparent { get; private set; }

    /// <summary>玻璃层当前是否可见。</summary>
    public bool IsVisible => _lastVisible;

    /// <summary>玻璃矩形（屏幕坐标）。</summary>
    public Win32Rect GlassBounds { get; private set; }

    /// <summary>最近一次渲染的统计。</summary>
    public GlassRenderStats Stats => _renderer.LastStats;

    private GlassSurfaceHost(
        SurfaceKind kind, IntPtr target, GlassSurfaceSettings settings, Action<string> log)
    {
        Kind = kind;
        TargetHandle = target;
        _settings = settings;
        _log = log;
        _renderer = new CpuGlassRenderer(settings.Material);
        _overlay = new LayeredOverlayWindow { Name = $"liquidglass-{kind}", TopMost = true };
    }

    /// <summary>接管一个系统界面。失败时返回 null 并写出原因。</summary>
    public static GlassSurfaceHost? Attach(
        SystemSurface surface, GlassSurfaceSettings settings, Action<string> log)
    {
        if (surface.Handle == IntPtr.Zero || !IsWindow(surface.Handle))
        {
            log($"[{surface.Kind}] 窗口句柄无效，跳过。");
            return null;
        }

        var host = new GlassSurfaceHost(surface.Kind, surface.Handle, settings, log);
        try
        {
            host.Initialize();
            return host;
        }
        catch (Exception ex)
        {
            log($"[{surface.Kind}] 接管失败：{ex.Message}");
            host.Dispose();
            return null;
        }
    }

    private void Initialize()
    {
        SyncGeometry(force: true);

        if (!_overlay.Create(_canvasRect.X, _canvasRect.Y, _canvasRect.Width, _canvasRect.Height))
        {
            throw new InvalidOperationException("覆盖窗口创建失败。");
        }

        // 排除自身捕获：必须在窗口建立后立刻设置，且要在第一次抓屏之前。
        if (_settings.ExcludeOverlayFromCapture)
        {
            _captureExclusionActive = SetWindowDisplayAffinity(
                _overlay.Handle, WDA_EXCLUDEFROMCAPTURE);
            _log(_captureExclusionActive
                ? $"[{Kind}] 覆盖层已排除出屏幕捕获（无回授、无抖动）。"
                : $"[{Kind}] 本机不支持 WDA_EXCLUDEFROMCAPTURE，改用临时隐藏抓屏。");
        }

        if (_settings.MakeTargetTransparent)
        {
            var result = CompositionAttribute.Apply(
                TargetHandle, CompositionAttribute.BackdropMode.Transparent, _settings.AccentColorAbgr);
            TargetMadeTransparent = result.Applied;
            _log(result.Applied
                ? $"[{Kind}] 已请求抹除原生背景（TRANSPARENTGRADIENT, α=0）。"
                : $"[{Kind}] 抹除原生背景失败：{result.Description}");

            if (!result.Applied)
            {
                _log($"[{Kind}] ⚠ 目标未透明化，玻璃层会被系统背景完全遮住。"
                   + "请运行 liquidglass-probe --test-accent 确认本机支持情况。");
            }
        }

        _overlay.PlaceBelow(TargetHandle);
        _overlay.Show();
        _log($"[{Kind}] 已接管 hwnd=0x{TargetHandle.ToInt64():X8}  目标={_targetBounds}  玻璃={GlassBounds}");
    }

    /// <summary>
    /// 跟踪目标几何与可见性。返回 true 表示几何发生变化（需要重置累积）。
    /// 每个渲染周期都应调用——任务栏会随显示器切换、DPI 变更、自动隐藏而移动。
    /// </summary>
    public bool Sync()
    {
        if (_disposed) return false;
        if (!IsWindow(TargetHandle))
        {
            _lastVisible = false;
            _overlay.Hide();
            return false;
        }

        var visible = IsWindowVisible(TargetHandle) && !IsIconic(TargetHandle);
        if (visible != _lastVisible)
        {
            _lastVisible = visible;
            if (visible) _overlay.Show(); else _overlay.Hide();
            if (visible) _composedOnce = false;
        }
        if (!visible) return false;

        var changed = SyncGeometry(force: false);

        // Z 序可能被别的置顶窗口打乱（比如弹出菜单、UAC 提示），每次重新贴回目标下方。
        _overlay.PlaceBelow(TargetHandle);

        return changed;
    }

    private bool SyncGeometry(bool force)
    {
        var bounds = SystemSurfaceLocator.GetVisualBounds(TargetHandle);
        if (bounds.IsEmpty) return false;

        if (!force && bounds == _targetBounds) return false;

        _targetBounds = bounds;

        // 画布 = 目标矩形本身。玻璃是画布内缩之后的一块。
        _canvasRect = bounds;

        var gx = bounds.X + _settings.GlassInsetX;
        var gy = bounds.Y + _settings.GlassInsetY;
        var gw = bounds.Width - _settings.GlassInsetX * 2;
        var gh = bounds.Height - _settings.GlassInsetY * 2;
        if (gw < 4 || gh < 4)
        {
            // 内缩过度，退回不内缩。
            gx = bounds.X; gy = bounds.Y; gw = bounds.Width; gh = bounds.Height;
        }

        // 玻璃矩形换算到画布局部坐标
        _glassRect = new PixelRect(gx - _canvasRect.X, gy - _canvasRect.Y, gw, gh);
        GlassBounds = new Win32Rect((int)gx, (int)gy, (int)(gx + gw), (int)(gy + gh));

        if (!force)
        {
            _overlay.SetBounds(_canvasRect.X, _canvasRect.Y, _canvasRect.Width, _canvasRect.Height);
            _renderer.Reset();
            _composedOnce = false;
        }

        return !force;
    }

    /// <summary>
    /// 合成一帧。返回 true 表示这一帧真的有内容上屏。
    /// </summary>
    /// <param name="forceReset">强制重新累积（配置变更、材质热更新等）。</param>
    public bool Compose(bool forceReset = false)
    {
        if (_disposed || !_lastVisible) return false;

        // 收敛后无需再算——上游同样在 48 帧后冻结（outColor = previous）。
        if (!forceReset && _composedOnce && _renderer.IsConverged)
        {
            return false;
        }

        // ---- 鼠标位置 → 相机射线视差 ----
        var mouseX = 0.5f;
        var mouseY = 0.5f;

        var accumulation = _settings.IdleAccumulation;

        if (_settings.MouseParallax)
        {
            var (cursorX, cursorY) = DisplayEnvironment.GetCursorPosition();
            if (cursorX >= GlassBounds.Left && cursorX < GlassBounds.Right
                && cursorY >= GlassBounds.Top && cursorY < GlassBounds.Bottom)
            {

                mouseX = (float)((cursorX - GlassBounds.Left) / (double)Math.Max(1, GlassBounds.Width));
                mouseY = (float)((cursorY - GlassBounds.Top) / (double)Math.Max(1, GlassBounds.Height));
                // 指针在玻璃上时降低累积上限，换取跟手；离开后自然升回高质量档。
                accumulation = Math.Max(_settings.MovingAccumulation, 4);
            }
        }

        // ---- 抓取折射源 ----
        var scene = CaptureScene();

        var options = new GlassRenderOptions
        {
            GlassRect = _glassRect,
            CanvasWidth = _canvasRect.Width,
            CanvasHeight = _canvasRect.Height,
            PathsPerPixel = _settings.PathsPerPixel,
            MaxAccumulation = accumulation,
            MouseX = mouseX,
            MouseY = mouseY,
            ForceReset = forceReset || !_composedOnce,
        };

        var frame = _renderer.Render(scene, options);

        // 上屏。UpdateLayeredWindow 会就地预乘 Alpha，所以传副本。
        _overlay.Present(frame.Clone(), _canvasRect.X, _canvasRect.Y);
        _composedOnce = true;

        return true;
    }

    /// <summary>
    /// 抓取玻璃背后那一层的像素作为折射源。
    ///
    /// 两个必须处理好的细节：
    /// <list type="number">
    ///   <item><b>自身排除</b>：玻璃层绝不能被采进自己的折射纹理，
    ///         否则会产生水平条纹与递归残影（上游在 DOM 捕获里排除 Canvas 是同一道理）。</item>
    ///   <item><b>越界填充</b>：任务栏贴着屏幕底边，向下扩边会落到屏幕之外。
    ///         这里用边界像素延续填充，避免玻璃下缘出现一条突兀的黑边。</item>
    /// </list>
    /// </summary>
    private GlassScene CaptureScene()
    {
        var margin = (int)Math.Ceiling(_settings.SceneMargin);
        var sceneX = _canvasRect.X - margin;
        var sceneY = _canvasRect.Y - margin;
        var sceneW = _canvasRect.Width + margin * 2;
        var sceneH = _canvasRect.Height + margin * 2;

        if (!_captureExclusionActive && _settings.ExcludeOverlayFromCapture)
        {
            // 回退路径：本机没有 WDA_EXCLUDEFROMCAPTURE。
            // 隐藏 → 抓屏 → 立刻恢复。整个窗口在 DWM 合成一帧（约 16ms）之内完成，
            // 因此用户通常看不到闪烁；代价是极小概率抓到中间帧。
            _overlay.Hide();
            var padded = _sampler.CapturePadded(sceneX, sceneY, sceneW, sceneH);
            _overlay.Show();
            ApplySceneSource(padded, margin);
            return GlassScene.FromBgra(padded, -margin, -margin);
        }

        var frame = _sampler.CapturePadded(sceneX, sceneY, sceneW, sceneH);
        ApplySceneSource(frame, margin);
        return GlassScene.FromBgra(frame, -margin, -margin);
    }

    /// <summary>
    /// 按下 <see cref="SceneSourceMode"/> 修正折射源。
    /// </summary>
    /// <param name="scene">场景位图，就地修改。</param>
    /// <param name="margin">场景相对画布向左上扩出的边距，即画布顶边在场景中的行号。</param>
    private void ApplySceneSource(BgraFrame scene, int margin)
    {
        if (_settings.SceneSource != SceneSourceMode.ExtendFromAbove) return;

        // 画布顶边在场景中的行号。这一行之上是未被遮挡的桌面，之下才是被任务栏盖住的区域。
        // 取它上面一行作为"未被遮挡的最后一行"，整行向下复制。
        var sourceRow = margin - 1;
        if (sourceRow < 0) return;

        var stride = scene.Stride;
        var srcOffset = sourceRow * stride;
        for (var y = margin; y < scene.Height; y++)
        {
            Array.Copy(scene.Pixels, srcOffset, scene.Pixels, y * stride, stride);
        }
    }

    /// <summary>把系统组件还原成原生外观。退出前务必调用。</summary>
    public void RestoreTarget()
    {
        if (TargetHandle != IntPtr.Zero && IsWindow(TargetHandle) && _settings.MakeTargetTransparent)
        {
            CompositionAttribute.Apply(TargetHandle, CompositionAttribute.BackdropMode.Native, 0);
            TargetMadeTransparent = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RestoreTarget();
        _overlay.Dispose();
        _sampler.Dispose();
    }
}
