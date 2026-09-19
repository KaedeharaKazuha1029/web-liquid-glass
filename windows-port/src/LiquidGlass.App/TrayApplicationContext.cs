using System.Diagnostics;
using System.Runtime.InteropServices;
using LiquidGlass.Win32;

namespace LiquidGlass.App;

/// <summary>
/// 托盘应用上下文：拥有引擎、托盘图标、全局热键与菜单。
///
/// 暂停的语义是"完全停止并还原系统外观"，而不是"停止渲染但保留透明任务栏"——
/// 后者会让用户看到一个没有背景的裸任务栏，等于把系统弄坏了。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyIdToggle = 0xA17;
    private const int HotkeyIdEmergency = 0xA18;
    private const uint ModShift = 0x0004;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkG = 0x47;
    private const uint VkR = 0x52;

    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly Logger _logger;
    private readonly GlassEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly HotkeyWindow _hotkey;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;
    private readonly ToolStripMenuItem _statusItem;

    private bool _paused;
    private bool _disposed;

    public TrayApplicationContext(
        AppConfig config, string configPath, Logger logger, bool attachOnStartup)
    {
        _config = config;
        _configPath = configPath;
        _logger = logger;

        _engine = new GlassEngine(config, logger.Write);

        _activeIcon = TrayIconFactory.Create(32, active: true);
        _pausedIcon = TrayIconFactory.Create(32, active: false);

        _statusItem = new ToolStripMenuItem("（读取状态中…）") { Enabled = false };
        _menu = BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = _activeIcon,
            Text = "LiquidGlass",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => TogglePause();
        _menu.Opening += (_, _) => RefreshStatus();

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += id => OnHotkey(id);

        if (!_hotkey.Register(HotkeyIdToggle, ModControl | ModAlt | ModNoRepeat, VkG))
        {
            logger.Write("全局热键 Ctrl+Alt+G 注册失败（可能已被其它程序占用），可用托盘菜单操作。");
        }

        // 紧急逃生键：不管程序处于什么状态，一键还原系统任务栏并退出接管。
        // 隐藏系统任务栏的功能必须有这么一个出口 —— 万一界面卡死或逻辑出错，
        // 用户不能连任务栏都找不回来。
        if (!_hotkey.Register(HotkeyIdEmergency,
                ModControl | ModAlt | ModShift | ModNoRepeat, VkR))
        {
            logger.Write("紧急热键 Ctrl+Alt+Shift+R 注册失败（可能已被其它程序占用）。");
        }
        else
        {
            logger.Write("紧急还原热键：Ctrl+Alt+Shift+R（立即还原系统任务栏并停止接管）");
        }

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshTooltip();
        _statusTimer.Start();

        if (attachOnStartup)
        {
            EnableGlass();
        }
        else
        {
            logger.Write("以 --no-attach 启动，未接管任何界面。可从托盘菜单启用。");
        }

        RefreshStatus();
    }

    // ---------------------------------------------------------------- 菜单

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var title = new ToolStripMenuItem("LiquidGlass · 液态玻璃") { Enabled = false };
        menu.Items.Add(title);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var toggle = new ToolStripMenuItem("暂停并还原系统外观", null, (_, _) => TogglePause())
        {
            Name = "toggle",
        };
        menu.Items.Add(toggle);

        menu.Items.Add(new ToolStripMenuItem("重新接管", null, (_, _) => Reattach()));
        menu.Items.Add(new ToolStripMenuItem("立即还原系统任务栏", null, (_, _) => EmergencyRestore()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenPath(_configPath)));
        menu.Items.Add(new ToolStripMenuItem("重新载入配置", null, (_, _) => ReloadConfig()));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenPath(_logger.FilePath)));
        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup())
        {
            Name = "startup",
            CheckOnClick = false,
        };
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));

        return menu;
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyIdEmergency)
        {
            EmergencyRestore();
            return;
        }
        TogglePause();
    }

    /// <summary>
    /// 紧急还原：立即停掉引擎（含还原系统任务栏与工作区）并退出程序。
    /// 这个动作<b>不做任何确认</b>，因为它就是为"程序已经不正常了"准备的。
    /// </summary>
    private void EmergencyRestore()
    {
        _logger.Write("⚠ 用户触发紧急还原热键，立即停止接管。");

        try
        {
            _engine.Stop();
        }
        finally
        {
            // 再补一次全局修复，兜住任何遗漏。
            SessionStateGuard.RecoverIfNeeded(_logger.Write);
            foreach (var hwnd in TaskbarVisibilityController.FindTaskbarWindows())
            {
                SessionStateGuard.NudgeTaskbarIntoPlace(hwnd);
            }

            _tray.Visible = false;
            ExitThread();
        }
    }

    // ---------------------------------------------------------------- 动作

    private void TogglePause()
    {
        if (!_paused)
        {
            DisableGlass("用户暂停");
        }
        else
        {
            EnableGlass();
        }
    }

    private void EnableGlass()
    {
        _engine.Start();
        _paused = false;
        _tray.Icon = _activeIcon;
        UpdateToggleLabel();
        _logger.Write("液态玻璃已启用。");
    }

    private void DisableGlass(string reason)
    {
        _engine.Stop();
        _paused = true;
        _tray.Icon = _pausedIcon;
        UpdateToggleLabel();
        _logger.Write($"液态玻璃已停用（{reason}），系统外观已还原。");
    }

    private void Reattach()
    {
        if (_paused) { EnableGlass(); return; }
        _logger.Write("请求重新接管所有目标界面。");
        _engine.Reattach();
    }

    private void ReloadConfig()
    {
        _logger.Write("重新载入配置…");
        var fresh = AppConfig.Load(_configPath, _logger.Write);

        // 配置对象是引用共享的：就地覆盖字段，避免其它持有者看到旧对象。
        _config.Material = fresh.Material;
        _config.Performance = fresh.Performance;
        _config.Scene = fresh.Scene;
        _config.Behavior = fresh.Behavior;
        _config.Taskbar = fresh.Taskbar;
        _config.SecondaryTaskbars = fresh.SecondaryTaskbars;
        _config.StartMenu = fresh.StartMenu;
        _config.Search = fresh.Search;
        _config.ActionCenter = fresh.ActionCenter;
        _config.Widgets = fresh.Widgets;
        _config.TaskView = fresh.TaskView;

        if (!_paused) _engine.ApplyConfig(_config);
        RefreshStatus();
    }

    private void ToggleStartup()
    {
        try
        {
            var ok = StartupRegistration.Toggle(out var nowEnabled, out var detail);
            _logger.Write(ok
                ? $"开机自启已{(nowEnabled ? "开启" : "关闭")}。"
                : $"开机自启设置失败：{detail}");
        }
        catch (Exception ex)
        {
            _logger.Write($"开机自启设置异常：{ex.Message}");
        }
        RefreshStatus();
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"文件还不存在：\n{path}", "LiquidGlass",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开失败：\n{ex.Message}", "LiquidGlass",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApplication()
    {
        _logger.Write("用户请求退出。");
        _engine.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    // ------------------------------------------------------------ 状态刷新

    private void RefreshStatus()
    {
        var status = _engine.GetStatus();

        _statusItem.Text = _paused
            ? "状态：已暂停（系统外观已还原）"
            : status.Running
                ? $"状态：运行中 · {(_engine.IsReplacementMode ? "替换任务栏" : $"接管 {status.AttachedCount} 个界面")}"
                  + $" · 上帧 {status.LastTickMs:F1}ms"
                : "状态：未运行";

        foreach (ToolStripItem item in _menu.Items)
        {
            switch (item.Name)
            {
                case "toggle":
                    item.Text = _paused ? "启用液态玻璃" : "暂停并还原系统外观";
                    break;
                case "startup":
                    ((ToolStripMenuItem)item).Checked = StartupRegistration.IsEnabled();
                    break;
            }
        }

        RefreshTooltip();
    }

    private void UpdateToggleLabel() => RefreshStatus();

    private void RefreshTooltip()
    {
        var status = _engine.GetStatus();
        var text = _paused
            ? "LiquidGlass · 已暂停"
            : _engine.IsReplacementMode
                ? "LiquidGlass · 液态玻璃任务栏运行中"
                : $"LiquidGlass · 接管 {status.AttachedCount} 个界面";

        // NotifyIcon.Text 上限 63 字符，超出会抛异常。
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _statusTimer.Dispose();
            _hotkey.Dispose();
            _engine.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _activeIcon.Dispose();
            _pausedIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------ 全局热键

    /// <summary>只用来接收 WM_HOTKEY 的消息窗口。</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private static readonly IntPtr HwndMessage = new(-3);

        /// <summary>参数是热键 ID，便于用同一个处理函数分发多个热键。</summary>
        public event Action<int>? Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "LiquidGlass.Hotkey",
                Parent = HwndMessage,   // 消息专用窗口，不出现在任何枚举里
            });
        }

        public bool Register(int id, uint modifiers, uint virtualKey) =>
            RegisterHotKey(Handle, id, modifiers, virtualKey);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                try { Pressed?.Invoke(m.WParam.ToInt32()); }
                catch { /* 热键回调异常不该杀掉消息循环 */ }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { UnregisterHotKey(Handle, HotkeyIdToggle); } catch { }
            try { UnregisterHotKey(Handle, HotkeyIdEmergency); } catch { }
            DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
