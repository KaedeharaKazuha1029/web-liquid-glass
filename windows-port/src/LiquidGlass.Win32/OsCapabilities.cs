using System.Runtime.InteropServices;

namespace LiquidGlass.Win32;

/// <summary>
/// Windows 各代 Shell 的能力探测。
///
/// 为什么本移植要专门做这一层：v1 依赖 <c>SetWindowCompositionAttribute</c> 抹掉任务栏背景，
/// 而那是 Windows 8 才出现、且从未公开的入口——<b>Windows 7 上根本没有</b>。
/// v2 改成"整个藏掉系统任务栏 + 自建任务栏"，这个依赖就消失了，
/// 于是 Win7 也能跑。但 Win7 仍缺另外几样东西（见下表），必须逐个降级。
///
/// | 能力 | 最低版本 | 缺失时的降级 |
/// |------|----------|--------------|
/// | Per-Monitor V2 DPI | Win10 1703 | 退回 System DPI 感知 |
/// | DWM 窗口隐身属性 | Win8 | 跳过 cloaked 检测（可能多出幽灵窗口） |
/// | <c>WDA_EXCLUDEFROMCAPTURE</c> | Win10 2004 | 退回"隐藏→抓屏→恢复" |
/// | <c>SetWindowCompositionAttribute</c> | Win8 | 不需要（v2 不再用它） |
/// </summary>
public enum ShellGeneration
{
    Unknown,
    Windows7,
    Windows8,
    Windows10,
    Windows11,
}

/// <summary>一次性探测本机能力，结果缓存。</summary>
public static class OsCapabilities
{
    public static Version Version { get; } = Environment.OSVersion.Version;

    public static int Build => Version.Build;

    public static bool Is64BitProcess { get; } = Environment.Is64BitProcess;

    public static bool Is64BitOs { get; } = Environment.Is64BitOperatingSystem;

    public static ShellGeneration Shell { get; } = DetectShell();

    /// <summary>Windows 7（含 SP1）与 Server 2008 R2。</summary>
    public static bool IsWindows7 => Shell == ShellGeneration.Windows7;

    public static bool IsAtLeastWindows8 => Build >= 9200;

    public static bool IsAtLeastWindows10 => Build >= 10240;

    public static bool IsWindows11 => Shell == ShellGeneration.Windows11;

    /// <summary>DWM 组合是否可用。Win7 在"基本/经典"主题下会关掉它——此时不能用任何 DWM 相关检测。</summary>
    public static bool IsDwmCompositionEnabled
    {
        get
        {
            if (!IsAtLeastWindows8)
            {
                try { return DwmIsCompositionEnabled(out var enabled) == 0 && enabled; }
                catch { return false; }
            }
            // Win8 起 DWM 常开（Win8 可以关，但极罕见），直接返回 true 省一次调用。
            return true;
        }
    }

    /// <summary>
    /// 是否支持 <c>DwmGetWindowAttribute(DWMWA_CLOAKED)</c>。
    /// Win8 起可用。Win7 上调用会返回错误码，此时无法识别"被挂起的 UWP 幽灵窗口"。
    /// </summary>
    public static bool SupportsCloakedDetection => IsAtLeastWindows8;

    /// <summary>
    /// 是否支持 <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>。
    /// Windows 10 2004（Build 19041）引入。缺它就只能"隐藏→抓屏→恢复"。
    /// </summary>
    public static bool SupportsCaptureExclusion => Build >= 19041;

    /// <summary>是否支持 Per-Monitor V2 DPI 感知（Win10 1703 / Build 15063 起）。</summary>
    public static bool SupportsPerMonitorV2 => Build >= 15063;

    /// <summary>宿主 Shell 的显示名，用于日志与诊断输出。</summary>
    public static string ShellName => Shell switch
    {
        ShellGeneration.Windows7 => "Windows 7",
        ShellGeneration.Windows8 => "Windows 8 / 8.1",
        ShellGeneration.Windows10 => "Windows 10",
        ShellGeneration.Windows11 => "Windows 11",
        _ => $"未知（Build {Build}）",
    };

    /// <summary>本机能力摘要，一行一条，供 <c>--diagnose</c> 使用。</summary>
    public static IEnumerable<string> DescribeCapabilities()
    {
        yield return $"Shell 世代            {ShellName}（Build {Build}）";
        yield return $"进程 / 系统位数       {(Is64BitProcess ? "64" : "32")} 位 / {(Is64BitOs ? "64" : "32")} 位";
        yield return $"DWM 组合              {(IsDwmCompositionEnabled ? "可用" : "不可用")}";
        yield return $"Per-Monitor V2 DPI    {(SupportsPerMonitorV2 ? "支持" : "不支持 → 退回系统 DPI 感知")}";
        yield return $"窗口隐身检测          {(SupportsCloakedDetection ? "支持" : "不支持 → 可能列出被挂起的幽灵窗口")}";
        yield return $"抓屏自排除            {(SupportsCaptureExclusion ? "支持（WDA_EXCLUDEFROMCAPTURE）"
            : "不支持 → 退回「隐藏→抓屏→恢复」")}";
    }

    private static ShellGeneration DetectShell()
    {
        var build = Environment.OSVersion.Version.Build;
        return build switch
        {
            // 7600 = Win7 RTM，7601 = Win7 SP1，7602 = Server 2008 R2 SP1
            >= 7600 and <= 7602 => ShellGeneration.Windows7,
            // 9200 = Win8，9600 = Win8.1
            >= 9200 and <= 9600 => ShellGeneration.Windows8,
            // 10240 = Win10 1507 … 19045 = Win10 22H2（含 LTSC 2021）
            >= 10240 and <= 21999 => ShellGeneration.Windows10,
            // 22000 = Win11 21H2 起
            >= 22000 => ShellGeneration.Windows11,
            _ => ShellGeneration.Unknown,
        };
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
