using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 任务栏按钮被点击时该做的事。
///
/// <para>行为完全对齐真实任务栏：</para>
/// <list type="bullet">
///   <item><b>左键</b>：有窗口 → 切到它；已经是前台窗口 → 最小化（这是任务栏最经典的"开关"手感）；
///         没有窗口（固定但未运行）→ 启动它。</item>
///   <item><b>中键</b>：新开一个实例。</item>
///   <item>不提供"固定/取消固定"，引导用户去 Windows 设置 —— 见 <see cref="OpenTaskbarSettings"/>。</item>
/// </list>
/// </summary>
public static class ShellActions
{
    /// <summary>左键点击：切换 / 启动。</summary>
    public static bool ActivateOrLaunch(ShellApp app)
    {
        if (app.Windows.Count == 0) return Launch(app);

        var target = PickBestWindow(app);

        // 已经是前台 → 最小化。真实任务栏就是这个行为。
        if (target == GetForegroundWindow() && !IsIconic(target))
        {
            ShowWindow(target, SW_MINIMIZE);
            return true;
        }

        return ActivateWindow(target);
    }

    /// <summary>把指定窗口带到前台。处理最小化恢复与前台权限限制。</summary>
    public static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        // Windows 只允许"当前前台进程"或"持有前台权限的进程"抢占前台。
        // 用户点击我们的任务栏时，我们的窗口就是前台窗口，所以这里是合法的；
        // 但为了兼容"用热键切换"等场景，仍然走一遍 AttachThreadInput 的保险流程。
        var foreground = GetForegroundWindow();
        uint foregroundThread = 0;
        if (foreground != IntPtr.Zero)
        {
            foregroundThread = GetWindowThreadProcessId(foreground, out _);
        }
        var targetThread = GetWindowThreadProcessId(hwnd, out _);

        var attached = false;
        try
        {
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
            {
                attached = AttachThreadInput(foregroundThread, targetThread, true);
            }

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThread, targetThread, false);
            }
        }
    }

    /// <summary>中键点击 / 无窗口时：启动应用。</summary>
    public static bool Launch(ShellApp app)
    {
        // 固定项优先走快捷方式 —— 这样启动参数、工作目录、以管理员身份运行等
        // 都跟用户点真实任务栏按钮时完全一致。
        if (!string.IsNullOrWhiteSpace(app.ShortcutPath) && File.Exists(app.ShortcutPath))
        {
            return ShellExecute(app.ShortcutPath, null);
        }

        if (!string.IsNullOrWhiteSpace(app.ProcessPath) && File.Exists(app.ProcessPath))
        {
            return ShellExecute(app.ProcessPath, null);
        }

        return false;
    }

    /// <summary>关闭一个窗口（发 WM_CLOSE，与点标题栏叉一样，应用有机会保存）。</summary>
    public static bool CloseWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        try
        {
            return PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭该应用的全部窗口。</summary>
    public static int CloseAllWindows(ShellApp app)
    {
        var count = 0;
        foreach (var hwnd in app.Windows)
        {
            if (CloseWindow(hwnd)) count++;
        }
        return count;
    }

    /// <summary>在资源管理器中定位该应用的可执行文件。</summary>
    public static bool ShowInFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        // /select, 后面不能有空格，这是 explorer 的历史怪癖。
        return ShellExecute("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>
    /// 打开 Windows 的「任务栏设置」页。
    ///
    /// <para>这是本移植的<b>刻意设计</b>：固定/取消固定、任务栏对齐方式、
    /// 系统托盘图标开关这些"非外观"设置，全部交还给系统自己管理。</para>
    ///
    /// <para>原因是 Windows 10 起微软移除了 <c>taskbarpin</c> / <c>taskbarunpin</c>
    /// 两个 Shell 动词，没有任何公开 API 能以"用户意图"的方式改固定列表。
    /// 自己往固定项文件夹里塞 .lnk 在部分版本上不会被 Explorer 立刻采纳，
    /// 与其做出一个时灵时不灵的功能，不如把用户引导到唯一权威的地方。</para>
    /// </summary>
    public static bool OpenTaskbarSettings() =>
        ShellExecute("explorer.exe", "ms-settings:taskbar");

    /// <summary>打开开始菜单（等价于按下 Win 键）。</summary>
    public static void OpenStartMenu()
    {
        // 直接模拟 Win 键：这是唯一在所有三代系统上都能可靠唤起开始菜单的办法。
        // 不能去点开始按钮 —— 系统任务栏已经被我们藏起来了。
        // 注意走的是"绕过钩子"的版本：否则合成的 Win 会被自己的钩子吃掉。
        SendKeyComboBypassingHook(VK_LWIN);
    }

    /// <summary>
    /// 打开快捷设置（Win+A）。
    ///
    /// <para>⚠️ <b>不能靠合成 Win+A 实现</b>：本程序装了 <c>WH_KEYBOARD_LL</c> 低级键盘钩子
    /// 来接管 Win 键，<see cref="SendInput"/> 合成的按键同样会被这个钩子拦下并吞掉 ——
    /// 表现就是"托盘点了没反应"。所以这里改成直接拉系统设置窗口。</para>
    /// </summary>
    public static void OpenQuickSettings()
    {
        // ms-settings: 是快捷设置的现代入口；它比 Win+A 稳，且不受键盘钩子影响。
        OpenSettingsPage("ms-settings:quiethours");
    }

    /// <summary>打开系统设置里的指定页面（<c>ms-settings:</c> URI）。</summary>
    public static void OpenSettingsPage(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 少数机器上没有 ms-settings 处理程序，退回打开设置首页。
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* 两条路都失败就作罢，调用方会记日志 */ }
        }
    }

    /// <summary>
    /// 打开通知中心。
    ///
    /// <para>走 Win+N 也要绕开自己的钩子，所以直接拉系统通知中心窗口所在的进程入口：
    /// <c>explorer.exe</c> 的 <c>ms-actioncenter:</c> 协议。</para>
    /// </summary>
    public static void OpenNotificationCenter()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-actioncenter:")
            {
                UseShellExecute = true,
            });
            return;
        }
        catch { /* 落到下面的键盘兜底 */ }

        // 兜底：临时挂起钩子再发键，避免被自己拦截。
        SendKeyComboBypassingHook(VK_LWIN, VK_N);
    }

    /// <summary>
    /// 发键盘组合键，并确保<b>不会被本程序自己的低级键盘钩子吞掉</b>。
    ///
    /// <para>做法：请求钩子在接下来的极短窗口期内放行所有按键。
    /// 由 <c>TaskbarReplacementService</c> 提供的委托在钩子回调里读这个标志。</para>
    /// </summary>
    public static void SendKeyComboBypassingHook(params ushort[] virtualKeys)
    {
        AllowSyntheticKeys?.Invoke(true);
        try
        {
            SendKeyCombo(virtualKeys);
        }
        finally
        {
            // 立刻关闭放行窗口，避免用户在这期间真的按了 Win 被漏给系统。
            AllowSyntheticKeys?.Invoke(false);
        }
    }

    /// <summary>
    /// 由宿主注册：开关"放行合成按键"的标志。为 null 时表示没有钩子需要绕开。
    /// </summary>
    public static Action<bool>? AllowSyntheticKeys { get; set; }

    // ------------------------------------------------------------ 内部实现

    private static IntPtr PickBestWindow(ShellApp app)
    {
        // 优先挑"最近用过"的那一个：前台窗口优先，其次非最小化的，最后随便取一个。
        var foreground = GetForegroundWindow();
        if (app.Windows.Contains(foreground)) return foreground;

        foreach (var hwnd in app.Windows)
        {
            if (!IsIconic(hwnd)) return hwnd;
        }

        return app.Windows[0];
    }

    private static bool ShellExecute(string file, string? arguments)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            lpVerb = "open",
            lpFile = file,
            lpParameters = arguments,
            nShow = SW_SHOWNORMAL,
        };

        return ShellExecuteExW(ref info);
    }

    private static void SendKeyCombo(params ushort[] virtualKeys)
    {
        var inputs = new List<INPUT>();

        foreach (var key in virtualKeys)
        {
            inputs.Add(KeyDown(key));
        }
        for (var i = virtualKeys.Length - 1; i >= 0; i--)
        {
            inputs.Add(KeyUp(virtualKeys[i]));
        }

        var array = inputs.ToArray();
        SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyDown(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } },
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
    };

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOWNORMAL = 1;
    private const int WM_CLOSE = 0x0010;

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_A = 0x41;
    private const ushort VK_N = 0x4E;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int SEE_MASK_NOASYNC = 0x00000100;
    private const int SEE_MASK_FLAG_NO_UI = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public IntPtr dummy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
