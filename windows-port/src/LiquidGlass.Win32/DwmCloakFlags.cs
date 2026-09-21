namespace LiquidGlass.Win32;

/// <summary>
/// DWM 的窗口"隐身"状态（<c>DWMWA_CLOAKED</c>）。
///
/// 为什么这个属性对任务栏至关重要：<b>被挂起的 UWP 应用和位于其它虚拟桌面上的窗口，
/// <c>IsWindowVisible</c> 依然返回 true</b>。只用可见性判断的话，
/// 任务栏上会冒出一堆早已关闭的幽灵图标。只有 DWM 的 cloaked 属性能把它们识别出来。
/// </summary>
[Flags]
public enum DwmCloakFlags
{
    None = 0,

    /// <summary>UWP 应用已被挂起（用户关掉了它的窗口，但进程仍在）。</summary>
    App = 0x1,

    /// <summary>窗口被 Shell 主动隐藏。</summary>
    Shell = 0x2,

    /// <summary>窗口位于另一个虚拟桌面上。</summary>
    Inherited = 0x4,
}
