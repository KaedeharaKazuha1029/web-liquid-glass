using System.Runtime.InteropServices;

namespace LiquidGlass.Win32;

/// <summary>
/// 构建"任务栏内容模型"所需的 Shell / 窗口站原生声明。
/// 与 <see cref="NativeMethods"/> 分开，是因为这批 API 围绕的是
/// "窗口 → 应用身份 → 图标与名字"这条链路，职责清晰一些。
/// </summary>
internal static class ShellNativeMethods
{
    // ---------------------------------------------------------- 窗口属性

    internal const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>
    /// 窗口是否被 DWM 隐身。Win8 起可用。
    /// 这是把"已关闭的 UWP 应用"和"藏在其它虚拟桌面上的窗口"从任务栏剔除的关键——
    /// 只看 <c>IsWindowVisible</c> 是不够的，它们依然"可见"。
    /// </summary>
    internal static DwmCloakFlags GetCloakedState(IntPtr hwnd)
    {
        if (!OsCapabilities.SupportsCloakedDetection) return DwmCloakFlags.None;
        var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var value, sizeof(int));
        return hr == 0 ? (DwmCloakFlags)value : DwmCloakFlags.None;
    }

    // -------------------------------------------------------- 窗口图标

    internal const int WM_GETICON = 0x007F;
    internal const int ICON_SMALL = 0;
    internal const int ICON_BIG = 1;
    internal const int ICON_SMALL2 = 2;
    internal const int GCLP_HICON = -14;
    internal const int GCLP_HICONSM = -34;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// 依次尝试三种途径拿窗口图标，全部失败返回 <see cref="IntPtr.Zero"/>。
    /// 顺序与 Explorer 自身一致：先问窗口，再问窗口类，最后才去问文件系统。
    /// </summary>
    internal static IntPtr GetWindowIcon(IntPtr hwnd, bool big)
    {
        // ① WM_GETICON：最准确（应用自己设置的图标）。
        //    必须用 SendMessageTimeout —— 目标进程若正在忙，
        //    SendMessage 会把我们自己也卡死（这是很常见的挂死原因）。
        var which = big ? ICON_BIG : ICON_SMALL2;
        if (SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(which), IntPtr.Zero,
                SMTO_ABORTIFHUNG, 200, out var icon) != IntPtr.Zero && icon != IntPtr.Zero)
        {
            return icon;
        }

        // ② 窗口类的图标：绝大多数传统 Win32 程序把图标挂在这里。
        var classIcon = NativeMethods.GetWindowLongPtr(hwnd, big ? GCLP_HICON : GCLP_HICONSM);
        if (classIcon != IntPtr.Zero) return classIcon;

        // ③ 小图标位置也可能只在小尺寸里。双向兜一次。
        classIcon = NativeMethods.GetWindowLongPtr(hwnd, big ? GCLP_HICONSM : GCLP_HICON);
        return classIcon;
    }

    // ------------------------------------------------------------ 图标解码

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, ref NativeMethods.BITMAPINFO lpbmi, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    // ------------------------------------------------------- SHGetFileInfo

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_LARGEICON = 0x000000000;
    internal const uint SHGFI_SMALLICON = 0x000000001;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    internal const uint SHGFI_DISPLAYNAME = 0x000000200;
    internal const uint SHGFI_TYPENAME = 0x000000400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // ------------------------------------------- 窗口的 AppUserModelID

    internal static readonly Guid IID_IPropertyStore =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PKEY_AppUserModel_ID：UWP 应用与"同一 exe 不同入口"的稳定身份来源。
    /// 靠它能正确区分 Windows 商店应用，而不是把一堆 UWP 应用
    /// 统统归到 ApplicationFrameHost 名下。
    /// </summary>
    internal static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    internal const ushort VT_EMPTY = 0;
    internal const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        public string? AsString()
        {
            if (vt != VT_LPWSTR || pointerValue == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(pointerValue);
        }
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>读取窗口的 AppUserModelID，失败返回 null。绝不抛异常。</summary>
    internal static string? TryGetAppUserModelId(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null)
            {
                return null;
            }

            var key = PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var pv) != 0) return null;

            try
            {
                var value = pv.AsString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    // ------------------------------------------------------ IShellLink

    internal static readonly Guid CLSID_ShellLink =
        new("00021401-0000-0000-C000-000000000046");

    internal static readonly Guid IID_IShellLinkW =
        new("000214F9-0000-0000-C000-000000000046");

    internal static readonly Guid IID_IPersistFile =
        new("0000010B-0000-0000-C000-000000000046");

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
    }

    internal const uint SLGP_RAWPATH = 0x0004;
    internal const uint SLGP_UNCPRIORITY = 0x0002;

    /// <summary>解析 .lnk 的目标路径（用于固定项）。失败返回 null。</summary>
    internal static string? ResolveShortcut(string lnkPath)
    {
        object? link = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_ShellLink, throwOnError: false);
            if (type is null) return null;
            link = Activator.CreateInstance(type);
            if (link is not IPersistFile persist || link is not IShellLinkW shellLink) return null;

            persist.Load(lnkPath, 0);   // STGM_READ

            var buffer = new System.Text.StringBuilder(260);
            shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SLGP_UNCPRIORITY);
            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }

    // --------------------------------------------------------- 激活窗口

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    internal const int ASFW_ANY = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    // ------------------------------------------------------------ 工作区

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeMethods.RECT rc;
        public IntPtr lParam;
    }

    internal const uint ABM_NEW = 0x00000000;
    internal const uint ABM_REMOVE = 0x00000001;
    internal const uint ABM_QUERYPOS = 0x00000002;
    internal const uint ABM_SETPOS = 0x00000003;
    internal const uint ABM_GETTASKBARPOS = 0x00000005;
    internal const uint ABM_ACTIVATE = 0x00000006;
    internal const uint ABM_GETSTATE = 0x00000004;
    internal const uint ABM_SETSTATE = 0x0000000A;
    internal const uint ABM_WINDOWPOSCHANGED = 0x00000009;

    internal const uint ABE_LEFT = 0;
    internal const uint ABE_TOP = 1;
    internal const uint ABE_RIGHT = 2;
    internal const uint ABE_BOTTOM = 3;

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
