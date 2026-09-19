using System.Runtime.InteropServices;
using LiquidGlass.Core;

namespace LiquidGlass.Win32;

/// <summary>系统托盘里的一个图标。</summary>
public sealed record TrayIcon(
    string Tooltip,
    BgraFrame? Icon,
    IntPtr OwnerWindow,
    uint CallbackMessage,
    uint IconId);

/// <summary>
/// 读取**系统托盘**（通知区域）里的图标。
///
/// <para><b>为什么必须跨进程读</b>：托盘图标没有公开的枚举 API。
/// 它们实际是 <c>Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32</c>
/// 这个工具栏上的按钮，而那个工具栏属于 <b>explorer.exe</b>。
/// 所以只能：在自己的进程里分配一块 explorer 的内存（<c>VirtualAllocEx</c>），
/// 用 <c>TB_GETBUTTON</c> 让它把按钮结构写进去，再 <c>ReadProcessMemory</c> 读回来。</para>
///
/// <para><b>图标怎么拿</b>：按钮只给出图标在工具栏图像列表里的下标。
/// 图像列表句柄（HIMAGELIST）是用户对象、可跨进程使用，
/// 所以 <c>TB_GETIMAGELIST</c> + <c>ImageList_GetIcon</c> 就能拿到真正的 HICON。</para>
///
/// <para><b>失败是常态</b>：不同 Windows 版本、权限不足、explorer 重启……
/// 都可能拿不到。所有失败都只返回空列表，绝不抛异常 —— 托盘是附加功能，
/// 它坏掉不能影响任务栏。</para>
/// </summary>
public static class TrayIconReader
{
    // ---- 工具栏消息 ----
    private const int WM_USER = 0x0400;
    private const int TB_GETBUTTON = WM_USER + 23;
    private const int TB_BUTTONCOUNT = WM_USER + 24;
    private const int TB_GETIMAGELIST = WM_USER + 49;
    private const int TB_GETBUTTONTEXTW = WM_USER + 75;

    // ---- 进程权限 ----
    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;

    private const int MEM_COMMIT = 0x1000;
    private const int MEM_RELEASE = 0x8000;
    private const int PAGE_READWRITE = 0x04;
    private const int ILD_NORMAL = 0x0000;

    /// <summary>TBBUTTON 结构（x64 下 32 字节，含对齐填充）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TBBUTTON
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        private readonly ushort _reserved;
        public IntPtr dwData;
        public IntPtr iString;
    }

    /// <summary>读一次托盘。任何一步失败都返回空列表。</summary>
    public static IReadOnlyList<TrayIcon> Read(int iconSize = 22)
    {
        var result = new List<TrayIcon>();

        try
        {
            var toolbar = FindTrayToolbar();
            if (toolbar == IntPtr.Zero) return result;

            GetWindowThreadProcessId(toolbar, out var pid);
            if (pid == 0) return result;

            var process = OpenProcess(
                PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
                false, pid);
            if (process == IntPtr.Zero) return result;

            var bufferSize = Math.Max(Marshal.SizeOf<TBBUTTON>(), 512);
            var remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)bufferSize,
                MEM_COMMIT, PAGE_READWRITE);

            if (remote == IntPtr.Zero)
            {
                CloseHandle(process);
                return result;
            }

            try
            {
                var count = SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                var imageList = SendMessage(toolbar, TB_GETIMAGELIST, IntPtr.Zero, IntPtr.Zero);
                var local = new byte[bufferSize];

                for (var i = 0; i < count && i < 64; i++)
                {
                    if (SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, remote) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(process, remote, local, (IntPtr)bufferSize, out _)) continue;

                    var button = MemoryMarshal.Read<TBBUTTON>(local);

                    var tooltip = ReadButtonText(toolbar, process, remote, bufferSize, i);
                    var icon = LoadButtonIcon(imageList, button.iBitmap, iconSize);

                    result.Add(new TrayIcon(
                        tooltip,
                        icon,
                        button.dwData,
                        (uint)button.idCommand,
                        (uint)(button.iBitmap & 0xFFFF)));
                }
            }
            finally
            {
                VirtualFreeEx(process, remote, IntPtr.Zero, MEM_RELEASE);
                CloseHandle(process);
            }
        }
        catch (Exception)
        {
            // 托盘是附加功能：任何异常都只当作"没读到"，绝不向上抛。
        }

        return result;
    }

    /// <summary>取「通知区域」那个工具栏窗口。新系统里它在 TrayNotifyWnd → SysPager 之下。</summary>
    private static IntPtr FindTrayToolbar()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return IntPtr.Zero;

        var notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return IntPtr.Zero;

        var pager = FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
        var parent = pager != IntPtr.Zero ? pager : notify;

        var toolbar = FindWindowExW(parent, IntPtr.Zero, "ToolbarWindow32", null);
        return toolbar;
    }

    /// <summary>取按钮文字（工具提示）。要用 explorer 的内存当缓冲区，所以还是那一套跨进程读。</summary>
    private static string ReadButtonText(
        IntPtr toolbar, IntPtr process, IntPtr remote, int bufferSize, int index)
    {
        try
        {
            var chars = SendMessage(toolbar, TB_GETBUTTONTEXTW, (IntPtr)index, remote);
            if (chars <= 0 || chars > 512) return string.Empty;

            var local = new byte[bufferSize];
            if (!ReadProcessMemory(process, remote, local, (IntPtr)bufferSize, out _))
                return string.Empty;

            var text = System.Text.Encoding.Unicode.GetString(local);
            var terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>把工具栏图像列表里的第 <paramref name="bitmapIndex"/> 个图标取出来。</summary>
    private static BgraFrame? LoadButtonIcon(IntPtr imageList, int bitmapIndex, int size)
    {
        if (imageList == IntPtr.Zero || bitmapIndex < 0) return null;

        var icon = ImageList_GetIcon(imageList, bitmapIndex, ILD_NORMAL);
        if (icon == IntPtr.Zero) return null;

        try
        {
            return IconLoader.FromHandle(icon, size);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int index, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr process, IntPtr address, IntPtr size, int allocationType, int protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, int freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
