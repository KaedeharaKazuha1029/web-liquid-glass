using LiquidGlass.Core;

namespace LiquidGlass.Win32;

/// <summary>
/// 液态玻璃快捷面板 —— 点任务栏托盘（展开箭头 / 齿轮）弹出的小玻璃面板。
///
/// <para><b>为什么必须有它</b>：此前托盘点击直接拉起系统设置页
/// （齿轮 → <c>ms-settings:quiethours</c> = "专注"，用户看到的就是
/// "点小托盘弹出系统的专注设置"）。真实 Windows 11 的托盘点击弹出的是
/// 一块**快捷设置浮层** —— 本类就是它的液态玻璃版：
/// 与任务栏/开始菜单同一套 <see cref="CpuGlassRenderer"/> 光学管线，
/// 六个磁贴各通向对应的系统设置页。</para>
///
/// <para>生命周期：打开时抓一次背景 → 预热渲染 → 上屏 → 显示；
/// 面板是瞬态的（存活几秒），背景刷新不做（与开始菜单的持续刷新不同，
/// 换来的是打开成本只有一次抓屏 + 几帧预热）。</para>
/// </summary>
public sealed class GlassQuickPanel : IDisposable
{
    /// <summary>一块磁贴：字形 + 标签 + 点击后打开的设置页。</summary>
    private sealed record Tile(string Glyph, string Label, string SettingsUri);

    /// <summary>
    /// 六个磁贴。字形来自 Segoe MDL2 Assets；
    /// 个别字形取不到时 <see cref="DrawTile"/> 会退化为只画标签，不会留空洞。
    /// </summary>
    private static readonly Tile[] Tiles =
    {
        new("\uE701", "网络", "ms-settings:network"),
        new("\uE702", "蓝牙", "ms-settings:bluetooth"),
        new("\uE709", "飞行模式", "ms-settings:airplanemode"),
        new("\uE767", "音量", "ms-settings:sound"),
        new("\uE708", "专注", "ms-settings:quiethours"),
        new("\uE713", "所有设置", "ms-settings:"),
    };

    public sealed class Settings
    {
        /// <summary>玻璃材质（与任务栏共用同一份配置）。</summary>
        public LiquidGlassMaterial Material { get; init; } = new();

        /// <summary>玻璃低倍率画布的缩放。面板小，0.5 足够又便宜。</summary>
        public double GlassScale { get; init; } = 0.5;

        /// <summary>打开前先渲染几帧再上屏（单帧只有 2 条路径，直接显示会偏噪）。</summary>
        public int WarmupFrames { get; init; } = 8;

        public int PathsPerPixel { get; init; } = 2;
        public int MaxAccumulation { get; init; } = 24;

        /// <summary>面板圆角半径（面板像素）。</summary>
        public int CornerRadius { get; init; } = 18;

        /// <summary>面板底边与任务栏顶边的间距。</summary>
        public int GapAboveTaskbar { get; init; } = 10;

        /// <summary>
        /// 打开后多少毫秒内忽略外部点击。
        ///
        /// <para><b>为什么必须有</b>：打开面板的那一下点击（任务栏轮询判定）
        /// 与面板自己的轮询看到的是**同一次按键** —— 不去抖的话
        /// 刚打开就被自己的打开点击关掉了。</para>
        /// </summary>
        public int ClickDebounceMs { get; init; } = 250;

        public int TileWidth { get; init; } = 150;
        public int TileHeight { get; init; } = 78;
        public int Columns { get; init; } = 2;
        public int Padding { get; init; } = 14;

        /// <summary>磁贴之间的间距。</summary>
        public int Gap { get; init; } = 10;

        /// <summary>抓背景时四周多取的边距（给边缘折射留素材）。</summary>
        public int SceneMargin { get; init; } = 24;

        public double FrostedBlurRadius { get; init; } = 96.0;

        /// <summary>字形/标签字号。</summary>
        public int GlyphSize { get; init; } = 26;
        public int LabelFontSize { get; init; } = 12;
    }

    private readonly Settings _settings;
    private readonly Action<string> _log;
    private readonly CpuGlassRenderer _renderer;
    private readonly LayeredOverlayWindow _window;
    private readonly ScreenSampler _sampler = new();

    private readonly List<(Tile Tile, PixelRect Rect)> _tiles = new();
    private int _hoverIndex = -1;

    private GlassScene? _scene;
    private BgraFrame? _glassFrame;
    private BgraFrame? _captureScratch;
    private BgraFrame? _presentScratch;
    private Win32Rect _panelBounds;
    private int _canvasWidth = 1;
    private int _canvasHeight = 1;
    private bool _windowCreated;

    private bool _isOpen;
    public bool IsOpen => _isOpen;
    public Win32Rect PanelBounds => _panelBounds;

    // ---- 轮询式输入（与任务栏同一套：GetAsyncKeyState 轮询而非窗口消息）----
    private bool? _lastLeftDown;
    private bool? _lastEscDown;
    private int _pressIndex = -1;
    private long _openedAtTicks;
    private long _lastInvokeTicks;
    private bool _disposed;

    public GlassQuickPanel(Settings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
        _renderer = new CpuGlassRenderer(settings.Material with
        {
            FrostedBlurRadius = settings.FrostedBlurRadius,
        });
        _window = new LayeredOverlayWindow
        {
            Name = "liquidglass-quickpanel",
            ClickThrough = false,
            Log = log,
        };
    }

    // ============================================================ 开关

    /// <summary>
    /// 打开面板。
    ///
    /// <param name="anchorRightX">任务栏托盘簇右缘的屏幕 X —— 面板右缘与之对齐。</param>
    /// <param name="bottomLimit">任务栏顶边的屏幕 Y —— 面板底边停在它上方。</param>
    /// </summary>
    public void Open(int anchorRightX, int bottomLimit)
    {
        if (_disposed || _isOpen) return;

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Layout(anchorRightX, bottomLimit);
        CaptureScene();

        for (var i = 0; i < Math.Clamp(_settings.WarmupFrames, 0, 16); i++)
        {
            RenderGlass();
        }

        if (!_windowCreated)
        {
            _window.Create(_panelBounds.X, _panelBounds.Y,
                _panelBounds.Width, _panelBounds.Height);
            _windowCreated = true;
        }

        _isOpen = true;
        _hoverIndex = -1;
        _pressIndex = -1;
        _lastLeftDown = null;       // 重新学习按键状态，防"打开点击"误关
        _lastEscDown = null;
        _openedAtTicks = Environment.TickCount64;

        CompositeAndPresent();
        _window.BringToTop();
        _window.Show();

        _log($"快捷面板：已打开（{_tiles.Count} 个磁贴，面板 {_panelBounds.Width}x{_panelBounds.Height}）。"
            + $"耗时 {watch.ElapsedMilliseconds}ms。");
    }

    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _window.Hide();
        _hoverIndex = -1;
        _pressIndex = -1;
        _log("快捷面板：已关闭。");
    }

    /// <summary>
    /// 每轮驱动：悬停高亮 + 磁贴点击 + Esc 关闭 + 点击面板外部关闭。
    /// 由任务栏的渲染循环调用（同一频率）。
    /// </summary>
    public void Tick()
    {
        if (_disposed || !_isOpen) return;

        var now = Environment.TickCount64;

        // ---- Esc 关闭 ----
        var esc = NativeMethods.GetAsyncKeyState(VK_ESCAPE) < 0;
        var escEdge = _lastEscDown is not null && esc && !_lastEscDown.Value;
        _lastEscDown = esc;
        if (escEdge)
        {
            Close();
            return;
        }

        // ---- 光标与悬停 ----
        var inside = NativeMethods.GetCursorPos(out var pt)
            && pt.X >= _panelBounds.X && pt.X < _panelBounds.Right
            && pt.Y >= _panelBounds.Y && pt.Y < _panelBounds.Bottom;

        var hover = -1;
        if (inside)
        {
            for (var i = 0; i < _tiles.Count; i++)
            {
                var r = _tiles[i].Rect;
                if (pt.X >= r.X && pt.X < r.Right && pt.Y >= r.Y && pt.Y < r.Bottom)
                {
                    hover = i;
                    break;
                }
            }
        }

        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            CompositeAndPresent();      // 悬停变了就重画（玻璃层直接复用，只重画磁贴层）
        }

        // ---- 点击 ----
        var left = NativeMethods.GetAsyncKeyState(VK_LBUTTON) < 0;
        var pressEdge = _lastLeftDown is not null && left && !_lastLeftDown.Value;
        var releaseEdge = _lastLeftDown is not null && !left && _lastLeftDown.Value;
        _lastLeftDown = left;

        // 面板外点击 → 关闭（去抖窗口内不动作：那可能是打开面板的那一下点击）。
        if (pressEdge && !inside && now - _openedAtTicks > _settings.ClickDebounceMs)
        {
            Close();
            return;
        }

        // 磁贴点击：按下要在面板内，松开也在同一块磁贴上（与任务栏同一套手势）。
        if (releaseEdge && _pressIndex >= 0
            && now - _lastInvokeTicks > _settings.ClickDebounceMs)
        {
            var tile = _tiles[_pressIndex].Tile;
            _lastInvokeTicks = now;
            _pressIndex = -1;
            _log($"快捷面板：点击 → 「{tile.Label}」");
            Close();
            ShellActions.OpenSettingsPage(tile.SettingsUri);
            return;
        }

        if (pressEdge)
        {
            _pressIndex = hover;
        }
        else if (!left)
        {
            _pressIndex = -1;
        }
    }

    // ============================================================ 布局

    /// <summary>面板右缘对齐托盘右缘，底边悬在任务栏上方，整体夹在屏幕内。</summary>
    private void Layout(int anchorRightX, int bottomLimit)
    {
        var cols = Math.Clamp(_settings.Columns, 1, 3);
        var rows = (Tiles.Length + cols - 1) / cols;
        var pad = _settings.Padding;
        var gap = _settings.Gap;

        var width = pad * 2 + cols * _settings.TileWidth + (cols - 1) * gap;
        var height = pad * 2 + rows * _settings.TileHeight + (rows - 1) * gap;

        var monitor = DisplayEnvironment.GetPrimaryMonitor();
        var bounds = DisplayEnvironment.GetMonitorBounds(monitor);
        if (bounds.IsEmpty) bounds = DisplayEnvironment.GetVirtualDesktopBounds();
        if (bottomLimit <= bounds.Top) bottomLimit = bounds.Bottom;

        var bottom = Math.Min(bottomLimit - _settings.GapAboveTaskbar,
                              bounds.Bottom - _settings.GapAboveTaskbar);
        var top = bottom - height;
        if (top < bounds.Top) top = bounds.Top;

        // 右缘对齐托盘，但整体不许出屏。
        var left = anchorRightX - width;
        if (left < bounds.Left) left = bounds.Left;
        if (left + width > bounds.Right) left = bounds.Right - width;

        _panelBounds = new Win32Rect(left, top, left + width, top + height);

        // 磁贴矩形（面板局部坐标）。
        _tiles.Clear();
        for (var i = 0; i < Tiles.Length; i++)
        {
            var c = i % cols;
            var r = i / cols;
            _tiles.Add((Tiles[i], new PixelRect(
                pad + c * (_settings.TileWidth + gap),
                pad + r * (_settings.TileHeight + gap),
                _settings.TileWidth,
                _settings.TileHeight)));
        }
    }

    // ============================================================ 渲染

    /// <summary>
    /// 抓一次背景。⚠️ 必须在窗口 Show 之前调用：
    /// 窗口一旦可见，抓到自己会形成折射递归。
    /// </summary>
    private void CaptureScene()
    {
        var margin = _settings.SceneMargin;
        var w = _panelBounds.Width + margin * 2;
        var h = _panelBounds.Height + margin * 2;

        if (_captureScratch is null
            || _captureScratch.Width != w || _captureScratch.Height != h)
        {
            _captureScratch = new BgraFrame(w, h);
        }

        _sampler.CapturePaddedInto(_captureScratch, _panelBounds.X - margin, _panelBounds.Y - margin);

        var scale = (float)_settings.GlassScale;
        _scene = GlassScene.FromBgra(_captureScratch, -margin, -margin, null, scale);
        _renderer.Reset();

        _canvasWidth = Math.Max(1, (int)Math.Round(_panelBounds.Width * scale));
        _canvasHeight = Math.Max(1, (int)Math.Round(_panelBounds.Height * scale));
        _glassFrame = null;
    }

    private void RenderGlass()
    {
        if (_scene is null) return;

        _glassFrame = _renderer.Render(_scene, new GlassRenderOptions
        {
            GlassRect = new PixelRect(0, 0, _canvasWidth, _canvasHeight),
            CanvasWidth = _canvasWidth,
            CanvasHeight = _canvasHeight,
            PathsPerPixel = _settings.PathsPerPixel,
            MaxAccumulation = _settings.MaxAccumulation,
            MouseX = 0.5f,
            MouseY = 0.5f,
        });
    }

    /// <summary>玻璃层复用，重画磁贴层后上屏。打开时与悬停变化时调用。</summary>
    private void CompositeAndPresent()
    {
        if (_glassFrame is null)
        {
            RenderGlass();
            if (_glassFrame is null) return;
        }

        // ⚠️ 玻璃画布是 GlassScale（0.5×）的低倍率帧，而 ULW 会把窗口缩成
        // 位图的尺寸 —— 直接上屏低倍率帧，窗口就只有面板的一半大
        //（实测 338x282 的面板变成了 169x141 的"迷你玻璃"）。
        // 必须先双线性放大到面板尺寸；磁贴与文字画在放大后的高分辨率层上才清晰。
        var canvas = _presentScratch;
        if (canvas is null || canvas.Width != _panelBounds.Width
            || canvas.Height != _panelBounds.Height)
        {
            canvas = new BgraFrame(_panelBounds.Width, _panelBounds.Height);
            _presentScratch = canvas;
        }
        CanvasPainter.UpscaleBilinearInto(_glassFrame, canvas);

        for (var i = 0; i < _tiles.Count; i++)
        {
            DrawTile(canvas, _tiles[i].Tile, _tiles[i].Rect, i == _hoverIndex);
        }

        _window.Present(canvas, _panelBounds.X, _panelBounds.Y);
    }

    private void DrawTile(BgraFrame frame, Tile tile, PixelRect rect, bool hovered)
    {
        var radius = Math.Min(rect.Width, rect.Height) * 0.22;

        // 磁贴底：亮度自适应 —— 底下玻璃亮就垫白、暗就垫黑，保证字形/标签可读。
        var bright = AverageLuminance(frame, rect.X, rect.Y, rect.Width, rect.Height) > 0.55;
        var (fb, fg, fr, fa) = hovered
            ? ((byte)255, (byte)255, (byte)255, (byte)90)
            : bright
                ? ((byte)255, (byte)255, (byte)255, (byte)46)
                : ((byte)255, (byte)255, (byte)255, (byte)30);
        CanvasPainter.FillRoundedRect(frame, rect.X, rect.Y, rect.Width, rect.Height,
            radius, fb, fg, fr, fa);

        var glyph = TextRasterizer.Render(tile.Glyph, _settings.GlyphSize,
            _settings.GlyphSize * 3, bold: false, family: "Segoe MDL2 Assets");
        var label = TextRasterizer.Render(tile.Label, _settings.LabelFontSize,
            Math.Max(40, (int)rect.Width - 16));

        // 字形与标签垂直排布；字形取不到（字体缺字）就整体居中只画标签。
        var contentH = (glyph?.Height ?? 0) + 4 + (label?.Height ?? 0);
        var y = rect.Y + (rect.Height - contentH) / 2.0;

        if (glyph is not null)
        {
            DrawTextAuto(frame, glyph.Bitmap,
                rect.X + (rect.Width - glyph.Width) / 2.0, y);
            y += glyph.Height + 4;
        }

        if (label is not null)
        {
            DrawTextAuto(frame, label.Bitmap,
                rect.X + (rect.Width - label.Width) / 2.0, y, dim: true);
        }
    }

    /// <summary>画文字，颜色跟着该处玻璃的亮度走（与开始菜单同一套自适应策略）。</summary>
    private static void DrawTextAuto(BgraFrame frame, BgraFrame text,
        double x, double y, bool dim = false)
    {
        var xi = (int)Math.Round(x);
        var yi = (int)Math.Round(y);

        var bright = AverageLuminance(frame, xi, yi, text.Width, text.Height) > 0.55;

        var shadow = bright ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
        CanvasPainter.Blit(frame, text, xi + 1, yi + 1, shadow, 0.40);

        var ink = bright
            ? ((byte)28, (byte)28, (byte)34)
            : ((byte)242, (byte)242, (byte)248);
        CanvasPainter.Blit(frame, text, xi, yi, ink, dim ? 0.72 : 1.0);
    }

    private static double AverageLuminance(BgraFrame frame, double x, double y,
        double width, double height)
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
                sum += (frame.Pixels[i + 2] * 2126
                        + frame.Pixels[i + 1] * 7152
                        + frame.Pixels[i] * 722) / 10000;
                count++;
            }
        }

        return count > 0 ? sum / (double)count / 255.0 : 0.5;
    }

    // ============================================================ 释放

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Dispose();
        _sampler.Dispose();
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_ESCAPE = 0x1B;
}
