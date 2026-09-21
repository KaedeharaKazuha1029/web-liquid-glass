using System.Runtime.InteropServices;
using System.Text;
using LiquidGlass.Core;

namespace LiquidGlass.Win32;

/// <summary>当前登录用户的账户信息（用于开始菜单左下角）。</summary>
/// <param name="DisplayName">要显示的名字。<b>优先微软账户的邮箱/显示名</b>，取不到才退回本地账户名。</param>
/// <param name="LocalName">本地账户名（<c>Environment.UserName</c>）。</param>
/// <param name="MicrosoftAccount">微软账户的邮箱（如 <c>xxx@qq.com</c>）。没有绑定则为 null。</param>
/// <param name="Avatar">头像位图，取不到为 null（此时界面画一个占位圆）。</param>
public sealed record UserAccount(
    string DisplayName,
    string LocalName,
    string? MicrosoftAccount,
    BgraFrame? Avatar)
{
    /// <summary>是否用的微软账户登录。</summary>
    public bool IsMicrosoftAccount => !string.IsNullOrWhiteSpace(MicrosoftAccount);

    /// <summary>
    /// 次要行：微软账户显示邮箱，本地账户显示"本地账户"。
    ///
    /// <para>⚠️ 必须排除"和主标题一模一样"的情况：没设 DisplayName 时
    /// 主标题取的是邮箱，副标题若也取邮箱，界面上就会出现两行完全相同的字
    /// （实测过，很难看）。这种情况下副标题改为说明性的"Microsoft 账户"。</para>
    /// </summary>
    public string Subtitle
    {
        get
        {
            if (!IsMicrosoftAccount) return "本地账户";
            return string.Equals(DisplayName, MicrosoftAccount, StringComparison.OrdinalIgnoreCase)
                ? "Microsoft 账户"
                : MicrosoftAccount!;
        }
    }
}

/// <summary>
/// 读当前登录用户是谁、用什么账户登录、头像长什么样。
///
/// <para><b>为什么不能只用 <c>Environment.UserName</c></b>：那读到的是<b>本地账户名</b>。
/// 本机实测：<c>Environment.UserName</c> = <c>1</c>，而用户实际是用微软账户
/// <c>3259344218@qq.com</c> 登录的 —— 于是开始菜单左下角只显示"1"，
/// 用户的原话就是"只显示本地账号没有微软账号"。</para>
///
/// <para><b>微软账户信息在哪</b>：<c>HKCU\Software\Microsoft\IdentityCRL\UserExtendedProperties</c>
/// 下面挂着已连接的身份，子键名<b>就是微软账户邮箱</b>（本机实测子键名 = <c>3259344218@qq.com</c>）。
/// 这是 Windows 自己维护的连接身份列表，不需要联网、不需要调用任何 Windows Live API，
/// 只读注册表即可 —— 对"只是想在开始菜单上显示出来"这个需求是最轻的做法。</para>
///
/// <para><b>头像在哪</b>：<c>%PUBLIC%\AccountPictures\&lt;SID&gt;\*-Image&lt;N&gt;.jpg</c>。
/// 同一张头像会有多个边长（32/40/48/64/96/192/208/240/424/448/1080）的副本，
/// 文件名里的 <c>-ImageN</c> 就是边长。挑一个略大于目标尺寸的（避免放大糊）再缩到目标尺寸。</para>
/// </summary>
public static class AccountReader
{
    private static UserAccount? _cached;
    private static readonly object Sync = new();

    /// <summary>读取当前账户信息（结果会缓存）。</summary>
    public static UserAccount Current()
    {
        lock (Sync)
        {
            return _cached ??= Read();
        }
    }

    /// <summary>
    /// 取带头像的账户信息。头像解码有成本（读 jpg + WIC 解码 + 缩放），
    /// 所以只在真正要画的时候调一次，并在缓存里留住。
    /// </summary>
    public static UserAccount WithAvatar(int avatarSize)
    {
        var account = Current();
        if (account.Avatar is not null) return account;

        var avatar = LoadAvatar(avatarSize);
        if (avatar is null) return account;

        var updated = account with { Avatar = avatar };
        lock (Sync) { _cached = updated; }
        return updated;
    }

    /// <summary>清掉缓存（诊断 / 配置变更时用）。</summary>
    public static void Invalidate()
    {
        lock (Sync) { _cached = null; }
    }

    private static UserAccount Read()
    {
        var local = Environment.UserName;
        var ms = ReadMicrosoftAccount();

        // 显示名的优先级：
        //   ① IdentityCRL 里的显示名（有些人设了中文名，更亲切）；
        //   ② 微软账户邮箱 —— 用户明确要看到"微软账号"，邮箱是最直接的标识；
        //   ③ 本地账户名。
        var display = ms?.DisplayName;
        if (string.IsNullOrWhiteSpace(display)) display = ms?.Email;
        if (string.IsNullOrWhiteSpace(display)) display = local;

        return new UserAccount(
            DisplayName: display!,
            LocalName: local,
            MicrosoftAccount: ms?.Email,
            Avatar: null);
    }

    private readonly record struct MsIdentity(string? Email, string? DisplayName);

    /// <summary>
    /// 从 IdentityCRL 读微软账户。
    ///
    /// <para>注册表形状（本机实测）：
    /// <code>
    /// HKCU\Software\Microsoft\IdentityCRL\UserExtendedProperties
    ///     └── 3259344218@qq.com        ← 子键名就是邮箱
    ///             DisplayName  (REG_SZ) ← 可选，用户在"你的信息"里设的显示名
    /// </code>
    /// 这里刻意<b>只读注册表、不做任何网络请求</b> —— 开始菜单要在 100ms 内打开，
    /// 不能因为联网拿 Live 资料而卡住。</para>
    /// </summary>
    private static MsIdentity? ReadMicrosoftAccount()
    {
        const string path = @"Software\Microsoft\IdentityCRL\UserExtendedProperties";

        var open = RegOpenKeyExW(HKEY_CURRENT_USER, path, 0, KEY_READ, out var key);
        if (open != 0 || key == IntPtr.Zero) return null;

        try
        {
            // 子键名 = 邮箱。可能有多个（多账户 / 卸载残留），挑第一个像邮箱的。
            var email = FirstEmailSubKey(key);
            if (email is null) return null;

            var opened = RegOpenKeyExW(key, email, 0, KEY_READ, out var sub);
            if (opened != 0 || sub == IntPtr.Zero) return new MsIdentity(email, null);

            try
            {
                return new MsIdentity(email, ReadString(sub, "DisplayName"));
            }
            finally
            {
                RegCloseKey(sub);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? FirstEmailSubKey(IntPtr key)
    {
        for (var i = 0; i < 32; i++)
        {
            var name = new StringBuilder(256);
            var length = (uint)name.Capacity;
            var result = RegEnumKeyExW(key, (uint)i, name, ref length,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != 0) break;

            var candidate = name.ToString();
            if (candidate.Contains('@')) return candidate;
        }
        return null;
    }

    private static string? ReadString(IntPtr key, string name)
    {
        var buffer = new byte[1024];
        var length = (uint)buffer.Length;
        var result = RegQueryValueExW(key, name, IntPtr.Zero, out var type, buffer, ref length);
        if (result != 0 || length == 0) return null;
        if (type != REG_SZ && type != REG_EXPAND_SZ) return null;

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(length, buffer.Length));
        text = text.TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    // ------------------------------------------------------------------ 头像

    /// <summary>
    /// 读账户头像。找不到就返回 null（界面会画占位圆）。
    ///
    /// <para>目录：<c>%PUBLIC%\AccountPictures\&lt;SID&gt;\*-Image&lt;N&gt;.jpg</c>，
    /// 其中 <c>N</c> 是方形边长。挑 <c>N</c> 最接近但不小于目标尺寸的那张，
    /// 避免放大导致模糊；都小于目标时挑最大的那张。</para>
    /// </summary>
    public static BgraFrame? LoadAvatar(int targetSize)
    {
        targetSize = Math.Max(8, targetSize);

        try
        {
            var best = FindBestAvatarFile(targetSize);
            if (best is null)
            {
                AvatarDiagnostic = "没有找到任何头像文件";
                return null;
            }

            AvatarDiagnostic = "选中 " + best;

            var frame = ImageReader.Load(best, targetSize);
            if (frame is null)
            {
                AvatarDiagnostic = "选中 " + best + "，但读取失败"
                    + $"（阶段 {ImageReader.LastStage ?? "?"}，"
                    + $"HRESULT 0x{ImageReader.LastHResult:X8}）";
                return null;
            }

            AvatarDiagnostic = $"选中 {best}，解出 {frame.Width}x{frame.Height}";

            return frame;
        }
        catch (Exception ex)
        {
            // 头像只是装饰，任何失败都不该影响开始菜单打开。
            AvatarDiagnostic = "异常：" + ex.GetType().Name + " — " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 最近一次头像加载的诊断信息。
    ///
    /// <para>为什么要留它：头像取不到时，"没找到文件"和"找到了但解码失败"是<b>完全不同</b>
    /// 的两个问题（前者是路径错，后者是 WIC 用法错），但从界面上看都只是"一个灰圆"。
    /// 有了这条信息，验收工具就能直接说清是哪一种。</para>
    /// </summary>
    public static string? AvatarDiagnostic { get; private set; }

    /// <summary>在候选目录里挑一张尺寸最合适的头像文件。</summary>
    private static string? FindBestAvatarFile(int targetSize)
    {
        foreach (var dir in CandidateAvatarDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            string? best = null;
            var bestDistance = int.MaxValue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                var size = ParseImageSize(Path.GetFileNameWithoutExtension(file));
                if (size <= 0) continue;

                // "够大且最小"优先：不小于目标时距离 = size - target（0 最好）；
                // 不够大的统统排到最后，其中越大越好。
                var distance = size >= targetSize
                    ? size - targetSize
                    : 100_000 + (targetSize - size);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = file;
                }
            }

            if (best is not null) return best;
        }

        return null;
    }

    /// <summary>头像可能存在的目录（按优先级）。</summary>
    private static IEnumerable<string> CandidateAvatarDirectories()
    {
        var sid = CurrentUserSid();

        // ⚠️ 必须用 Shell 的"公共目录"API，不能靠路径拼接。
        //
        // 曾经写成 Path.Combine(CommonApplicationData, "..", "Public", ...)，
        // 而 CommonApplicationData = C:\ProgramData，于是拼出来
        // C:\ProgramData\..\Public —— 规范化之后是 **C:\Public**，
        // 根本不是 C:\Users\Public，头像就永远找不到。
        // （这个坑在验收里被"头像未取到"如实抓到了。）
        var publicRoot = GetShellFolder(CSIDL_COMMON_DOCUMENTS);
        if (publicRoot is not null)
        {
            // CSIDL_COMMON_DOCUMENTS = C:\Users\Public\Documents，上一级就是 C:\Users\Public。
            publicRoot = Directory.GetParent(publicRoot)?.FullName;
        }

        if (publicRoot is not null && sid is not null)
        {
            yield return Path.Combine(publicRoot, "AccountPictures", sid);
        }

        // 有些系统（或某些账户类型）把头像直接放在这些位置。
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "AccountPictures");

        if (publicRoot is not null)
        {
            yield return Path.Combine(publicRoot, "AccountPictures");
        }
    }

    private const int CSIDL_COMMON_DOCUMENTS = 0x002E;

    /// <summary>取 Shell 特殊目录的真实路径（失败返回 null）。</summary>
    private static string? GetShellFolder(int csidl)
    {
        try
        {
            var buffer = new StringBuilder(260);
            var result = SHGetFolderPathW(IntPtr.Zero, csidl | CSIDL_FLAG_CREATE,
                IntPtr.Zero, 0, buffer);
            if (result != 0) return null;

            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private const int CSIDL_FLAG_CREATE = 0x8000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetFolderPathW(IntPtr hwndOwner, int nFolder,
        IntPtr hToken, uint dwFlags, StringBuilder pszPath);

    /// <summary>从 <c>{GUID}-Image208.jpg</c> 里取出 208。</summary>
    private static int ParseImageSize(string stem)
    {
        var idx = stem.LastIndexOf("-Image", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        return int.TryParse(stem[(idx + 6)..], out var size) ? size : 0;
    }

    private static string? CurrentUserSid()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    private const uint KEY_READ = 0x20019;
    private const uint REG_SZ = 1;
    private const uint REG_EXPAND_SZ = 2;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint sam, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegEnumKeyExW(IntPtr hKey, uint index, StringBuilder name, ref uint nameLength,
        IntPtr reserved, IntPtr classPtr, IntPtr classLength, IntPtr lastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved,
        out uint type, byte[] data, ref uint dataLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
