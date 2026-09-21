using System.Diagnostics;
using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 进程资源快照，用来抓"用一阵就卡退"这类**累积型**资源泄漏。
///
/// <para><b>为什么需要它</b>：窗口数不变并不代表没泄漏。
/// GDI 对象（画笔/位图/字体/DC）和 USER 对象（窗口/菜单/图标）都有**每进程上限**
/// （默认各 10000），泄漏到顶之后程序会出现各种"莫名其妙"的失败直至卡死退出，
/// 而且**不会产生崩溃转储、也不会写事件日志** —— 表现就是用户说的"静默卡退"。
/// 私有内存与句柄总数则能抓到托管/非托管内存的持续增长。</para>
/// </summary>
public static class ResourceProbe
{
    /// <summary>取一份当前进程的资源快照。</summary>
    public static Snapshot Take()
    {
        var process = Process.GetCurrentProcess();
        var self = GetCurrentProcess();

        return new Snapshot(
            GdiObjects: (int)GetGuiResources(self, 0),
            UserObjects: (int)GetGuiResources(self, 1),
            Handles: process.HandleCount,
            PrivateBytes: process.PrivateMemorySize64);
    }

    /// <summary>一次资源快照。</summary>
    public readonly record struct Snapshot(
        int GdiObjects, int UserObjects, int Handles, long PrivateBytes)
    {
        /// <summary>人类可读的一行，便于直接写进日志。</summary>
        public override string ToString() =>
            $"GDI={GdiObjects} USER={UserObjects} 句柄={Handles} "
            + $"私有内存={PrivateBytes / 1024.0 / 1024.0:F1}MB";

        /// <summary>与另一次快照的差值，用来判断"有没有在涨"。</summary>
        public string Diff(in Snapshot before) =>
            $"GDI {before.GdiObjects}→{GdiObjects}（{(GdiObjects - before.GdiObjects):+#;-#;0}）"
            + $"，USER {before.UserObjects}→{UserObjects}（{(UserObjects - before.UserObjects):+#;-#;0}）"
            + $"，句柄 {before.Handles}→{Handles}（{(Handles - before.Handles):+#;-#;0}）"
            + $"，私有内存 {before.PrivateBytes / 1024.0 / 1024.0:F1}→{PrivateBytes / 1024.0 / 1024.0:F1}MB"
            + $"（{((PrivateBytes - before.PrivateBytes) / 1024.0 / 1024.0):+#.#;-#.#;0}MB）";
    }
}

