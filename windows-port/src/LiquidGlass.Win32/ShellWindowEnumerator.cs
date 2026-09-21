using System.Diagnostics;
using LiquidGlass.Core;
using static LiquidGlass.Win32.NativeMethods;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>一个应用在任务栏上的身份（可能同时拥有多个窗口）。</summary>
public sealed record ShellApp
{
    /// <summary>
    /// 稳定身份。优先用 AppUserModelID，退回可执行文件全路径。
    /// 用它来跨帧去重、判断"是同一个应用"。
    /// </summary>
    public required string Identity { get; init; }

    /// <summary>展示名。来自 exe 的 FileDescription，退回快捷方式名 / 进程名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>可执行文件全路径。UWP 应用可能为空。</summary>
    public string? ProcessPath { get; init; }

    /// <summary>.lnk 快捷方式路径（来自"固定到任务栏"文件夹）。</summary>
    public string? ShortcutPath { get; init; }

    /// <summary>是否固定到任务栏。</summary>
    public bool IsPinned { get; init; }

    /// <summary>该应用当前拥有的窗口（按 Z 序）。固定但未运行时为空。</summary>
    public IReadOnlyList<IntPtr> Windows { get; init; } = [];

    /// <summary>窗口数量，用于显示 "Chrome (3)"。</summary>
    public int WindowCount => Windows.Count;

    /// <summary>是否为当前前台应用。</summary>
    public bool IsForeground { get; init; }

    /// <summary>
    /// 任务栏上显示的文字：应用名；多窗口时加窗口数。
    /// 用户明确要求"图标在上、文字在下、文字较小"，所以这个字符串直接进渲染。
    /// </summary>
    public string Label => WindowCount > 1 && IsRunning ? $"{DisplayName} ({WindowCount})" : DisplayName;

    public bool IsRunning => Windows.Count > 0;

    public override string ToString() =>
        $"{DisplayName,-28} 固定={IsPinned,-6} 运行={IsRunning,-6} 窗口={WindowCount}"
        + (IsForeground ? " [前台]" : "");
}

/// <summary>枚举结果：一段可枚举的窗口记录（诊断用）。</summary>
public sealed record ShellWindowInfo(
    IntPtr Handle,
    string Title,
    uint ProcessId,
    string? ProcessPath,
    string? AppUserModelId,
    DwmCloakFlags Cloaked,
    bool IsToolWindow,
    bool HasOwner,
    bool HasAppWindowStyle,
    bool PassedFilter,
    string RejectReason);

/// <summary>
/// "读取系统任务栏内容"的核心。
///
/// 任务栏上会出现什么，规则是 Windows 内部决定的，没有公开 API 直接列举。
/// 这里复刻 Explorer 自己的判定链，并且把<b>每一步的判定结果都保留下来</b>，
/// 因为这条链在不同 Windows 版本上细节不同，出问题时必须能一眼看出是哪一步拒掉的。
///
/// 判定顺序（任何一步不通过就排除）：
/// <list type="number">
///   <item><c>IsWindowVisible</c> —— 窗口本身可见；</item>
///   <item>不是 <c>WS_EX_TOOLWINDOW</c> —— 工具窗口（浮动面板、输入法候选框）不上任务栏；</item>
///   <item>没有 owner —— 有 owner 的是对话框 / 弹出窗口，归到宿主窗口去；</item>
///   <item>不是 cloaked —— <b>这一条最容易被漏掉</b>。已关闭的 UWP 应用
///         和位于其它虚拟桌面上的窗口，<c>IsWindowVisible</c> 依然返回 true，
///         只有 DWM 的 cloaked 属性能把它们识别出来；</item>
///   <item>标题非空，或者带 <c>WS_EX_APPWINDOW</c>；</item>
///   <item>不属于 Shell 自身（Progman / WorkerW / 任务栏 / 本程序）。</item>
/// </list>
/// </summary>
public static class ShellWindowEnumerator
{
    /// <summary>永远不该出现在任务栏上的窗口类名。</summary>
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                    // 桌面
        "WorkerW",                    // 桌面背景层
        "Shell_TrayWnd",              // 任务栏本体
        "Shell_SecondaryTrayWnd",     // 副屏任务栏
        "Shell_InputSwitchTopLevelWindow",
        "MultitaskingViewFrame",      // 任务视图
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "TaskListThumbnailWnd",
        "TaskListOverlayWnd",
        "Shell_CharmWindow",
        "ApplicationManager_DesktopShellWindow",
    };

    /// <summary>不参与枚举的进程名（小写，不含 .exe）。</summary>
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",                 // 桌面与部分 Shell 窗口
        "searchhost",
        "startmenuexperiencehost",
        "shellexperiencehost",
        "textinputhost",
        "widgets",
        "windowswidgets",
        "widgetservice",
        "dwm",
        "csrss",
        "winlogon",
        "lockapp",
        "sihost",
        "ctfmon",
        "fontdrvhost",
        "securityhealthsystray",
    };

    /// <summary>
    /// 枚举所有属于任务栏的窗口，附带完整判定记录（诊断用）。
    /// </summary>
    public static IReadOnlyList<ShellWindowInfo> EnumerateWindowsDetailed()
    {
        var results = new List<ShellWindowInfo>();
        var foreground = GetForegroundWindow();
        var ownProcessId = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            var className = SystemSurfaceLocator.GetClassNameOf(hwnd);
            var title = SystemSurfaceLocator.GetWindowTextOf(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);

            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            var isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
            var hasAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
            var owner = GetWindow(hwnd, GW_OWNER);
            var hasOwner = owner != IntPtr.Zero;
            var cloaked = GetCloakedState(hwnd);
            var processPath = pid == ownProcessId ? null : GetProcessPath(pid);
            var processName = processPath is null ? "" : Path.GetFileNameWithoutExtension(processPath);
            var aumid = TryGetAppUserModelId(hwnd);

            var reason = "";
            var passed = true;

            bool Reject(string why) { passed = false; reason = why; return true; }

            if (pid == ownProcessId) { Reject("属于本程序"); }
            else if (ExcludedClasses.Contains(className)) { Reject($"类名在黑名单（{className}）"); }
            else if (ExcludedProcesses.Contains(processName)) { Reject($"进程在黑名单（{processName}）"); }
            else if (!IsWindowVisible(hwnd)) { Reject("不可见"); }
            else if (isToolWindow) { Reject("WS_EX_TOOLWINDOW"); }
            else if (cloaked != DwmCloakFlags.None) { Reject($"DWM 隐身（{cloaked}）"); }
            else if (hasOwner) { Reject("有 owner（对话框/弹出窗口）"); }
            else if (string.IsNullOrWhiteSpace(title) && !hasAppWindow) { Reject("标题为空且无 WS_EX_APPWINDOW"); }

            results.Add(new ShellWindowInfo(
                hwnd, title, pid, processPath, aumid, cloaked,
                isToolWindow, hasOwner, hasAppWindow, passed, reason));

            _ = foreground;
            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// 按应用聚合窗口，得到"任务栏上应该有哪些项"。
    /// 这是替换任务栏的数据源。
    /// </summary>
    public static IReadOnlyList<ShellApp> EnumerateApps()
    {
        var windows = EnumerateWindowsDetailed().Where(w => w.PassedFilter).ToList();
        var foregroundPid = GetForegroundWindowPid();

        var groups = new Dictionary<string, AppAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            // 注意：AppIdentity 是结构体，AppIdentity? 是 Nullable<>，
            // C# 不会自动解包 —— 必须用 `is { } id` 模式取出非空值。
            if (ResolveIdentity(window) is not { } identity) continue;

            if (!groups.TryGetValue(identity.Key, out var accumulator))
            {
                accumulator = new AppAccumulator(identity.Key, identity.DisplayName, identity.ProcessPath);
                groups[identity.Key] = accumulator;
            }

            accumulator.Windows.Add(window.Handle);
            if (window.ProcessId == foregroundPid) accumulator.IsForeground = true;
        }

        return groups.Values
            .Select(a => new ShellApp
            {
                Identity = a.Identity,
                DisplayName = a.DisplayName,
                ProcessPath = a.ProcessPath,
                Windows = a.Windows,
                IsForeground = a.IsForeground,
            })
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 把一个窗口映射到"应用身份"。
    /// UWP 应用优先用 AppUserModelID，这样 Windows 计算器 /
    /// 照片 / 商店不会被混成同一个 ApplicationFrameHost。
    /// </summary>
    private static AppIdentity? ResolveIdentity(ShellWindowInfo window)
    {
        // UWP：AUMID 形如 "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
        if (!string.IsNullOrWhiteSpace(window.AppUserModelId))
        {
            var aumid = window.AppUserModelId!;
            var name = AumidToDisplayName(aumid, window.Title);
            return new AppIdentity(aumid, name, window.ProcessPath);
        }

        if (!string.IsNullOrWhiteSpace(window.ProcessPath))
        {
            var path = window.ProcessPath!;
            var name = GetFriendlyAppName(path) ?? Path.GetFileNameWithoutExtension(path);
            return new AppIdentity(path, name, path);
        }

        // 既没有 AUMID 也没有进程路径（受保护进程）：退回窗口标题。
        if (!string.IsNullOrWhiteSpace(window.Title))
        {
            return new AppIdentity($"title:{window.Title}", window.Title, null);
        }

        return null;
    }

    /// <summary>
    /// 从 AUMID 推一个像样的展示名。
    /// AUMID 的前半段是包名（<c>Microsoft.WindowsCalculator_8wekyb3d8bbwe</c>），
    /// 直接展示太丑，所以只取最后一个点号之后的部分，且要求它够像个名字。
    /// </summary>
    private static string AumidToDisplayName(string aumid, string fallbackTitle)
    {
        var package = aumid.Split('!')[0];
        var underscore = package.IndexOf('_');
        if (underscore > 0) package = package[..underscore];

        var lastDot = package.LastIndexOf('.');
        var tail = lastDot >= 0 && lastDot < package.Length - 1
            ? package[(lastDot + 1)..]
            : package;

        // "WindowsCalculator" / "WindowsStore" 这类驼峰名插空格后更好读
        var spaced = System.Text.RegularExpressions.Regex.Replace(
            tail, "(?<=[a-z0-9])(?=[A-Z])", " ");

        return spaced.Length >= 3 ? spaced : (string.IsNullOrWhiteSpace(fallbackTitle) ? package : fallbackTitle);
    }

    /// <summary>取可执行文件的 FileDescription，也就是资源管理器里显示的那个名字。</summary>
    public static string? GetFriendlyAppName(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var description = info.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description)
                && !description.StartsWith('@')       // 未解析的本地化占位符
                && description.Length <= 48)
            {
                return description;
            }
            var product = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product) && !product.StartsWith('@') && product.Length <= 48)
            {
                return product;
            }
        }
        catch
        {
            // 受保护或损坏的 PE 会抛异常，静默退回进程名。
        }
        return null;
    }

    /// <summary>
    /// PID → 可执行文件路径的缓存。
    ///
    /// 这个缓存不是"优化"，而是<b>必需</b>：一次枚举要扫描 600+ 个顶层窗口，
    /// 而 explorer 一个进程就贡献几十个。不做缓存就是几百次 OpenProcess，
    /// 每次都要走一遍内核对象查询 —— 实测这让整轮枚举慢了将近一个数量级，
    /// 而我们必须每秒重扫几轮才能跟上应用的开关。
    /// </summary>
    [ThreadStatic]
    private static Dictionary<uint, string?>? _processPathCache;

    private static string? GetProcessPath(uint pid)
    {
        if (pid == 0) return null;

        _processPathCache ??= [];
        if (_processPathCache.TryGetValue(pid, out var cached)) return cached;

        var resolved = QueryProcessPath(pid);
        _processPathCache[pid] = resolved;
        return resolved;
    }

    /// <summary>清空缓存。每次完整枚举开始前调用，避免 PID 复用导致的错误映射。</summary>
    public static void ResetProcessCache() => _processPathCache?.Clear();

    private static string? QueryProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return buffer.ToString(0, (int)size);
            }
        }
        catch { }
        finally { CloseHandle(handle); }
        return null;
    }

    private static uint GetForegroundWindowPid()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid;
    }

    private readonly record struct AppIdentity(string Key, string DisplayName, string? ProcessPath);

    private sealed class AppAccumulator(string identity, string displayName, string? processPath)
    {
        public string Identity { get; } = identity;
        public string DisplayName { get; } = displayName;
        public string? ProcessPath { get; } = processPath;
        public List<IntPtr> Windows { get; } = [];
        public bool IsForeground { get; set; }
    }

    private const int GW_OWNER = 4;
}
