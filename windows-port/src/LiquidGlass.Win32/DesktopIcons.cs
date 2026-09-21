using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// 桌面图标那一层窗口（<c>SHELLDLL_DefView</c>）的开关。
///
/// <para><b>为什么要动它</b>：用户要求"开始菜单只透出桌面背景和壁纸，
/// 不要透出桌面应用图标"。桌面图标并不是壁纸的一部分，它们是
/// <c>Progman → SHELLDLL_DefView</c> 这个窗口画出来的独立一层，
/// 正好可以单独藏掉。</para>
///
/// <para><b>为什么不去解码壁纸文件</b>：<c>SPI_GETDESKWALLPAPER</c> 拿到的是一个
/// 静态 jpg/png 路径，而用户装了 Wallpaper Engine（壁纸是动态的），
/// 解码文件只会得到一张与真实桌面不符的死图。
/// 藏掉图标再抓屏，拿到的是**活的**桌面背景。</para>
///
/// <para><b>用法必须成对</b>：<see cref="Hide"/> 之后一定要在 finally 里
/// <see cref="Restore"/>，否则用户的桌面图标就永久消失了。</para>
/// </summary>
public static class DesktopIcons
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>找到桌面图标那一层窗口。找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr FindView()
    {
        // 现代 Windows 上它可能挂在 Progman 下，也可能挂在某个 WorkerW 下，
        // 所以两条路都试。
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }

        // 在顶层窗口里找带 SHELLDLL_DefView 子窗口的 WorkerW。
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "WorkerW") return true;

            var view = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;

            found = view;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>藏起桌面图标，返回"原本是否可见"（用于 <see cref="Restore"/>）。</summary>
    public static bool Hide()
    {
        var view = FindView();
        if (view == IntPtr.Zero) return false;

        var wasVisible = IsWindowVisible(view);
        if (wasVisible) ShowWindow(view, SW_HIDE);
        return wasVisible;
    }

    /// <summary>恢复桌面图标。</summary>
    public static void Restore(bool wasVisible)
    {
        if (!wasVisible) return;

        var view = FindView();
        if (view != IntPtr.Zero) ShowWindow(view, SW_SHOW);
    }
}
