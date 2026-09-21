using Microsoft.Win32;
using static LiquidGlass.Win32.ShellNativeMethods;

namespace LiquidGlass.Win32;

/// <summary>一个"固定到任务栏"的项。</summary>
public sealed record PinnedApp(
    string ShortcutPath,
    string ShortcutName,
    string? TargetPath,
    string DisplayName,
    int Order);

/// <summary>
/// 读取"固定到任务栏"的应用列表。
///
/// <para><b>数据来源与它的局限</b>：固定的项目以 <c>.lnk</c> 形式存在
/// <c>%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\</c>。
/// 这个路径从 Windows 7 一直沿用到 Windows 11，是本移植能"自动适配三代系统"的基础之一。
/// 用户在「设置 → 个性化 → 任务栏」里增删固定项时，explorer 就是在改这个文件夹，
/// 所以监听它即可实现实时同步。</para>
///
/// <para><b>顺序问题</b>：文件夹里的顺序是按文件名排序的，<b>不是</b>任务栏上的真实顺序。
/// 真实顺序存在注册表 <c>HKCU\...\Explorer\Taskband</c> 的二进制值里。
/// 这里做 best-effort 解析：把二进制按 UTF-16LE 解码后，
/// 在文本里查找每个固定项的文件名，用第一次出现的偏移量作为排序键。
/// 解析不到的项排在最后。这是不完美但不会出错的策略——
/// 顺序错误只影响观感，不会导致功能异常。</para>
/// </summary>
public static class PinnedAppsReader
{
    private const string TaskbandKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    /// <summary>固定项文件夹路径（Win7 起未变）。</summary>
    public static string PinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>读取全部固定项，按推测的真实顺序排列。</summary>
    public static IReadOnlyList<PinnedApp> Read()
    {
        var folder = PinnedFolder;
        if (!Directory.Exists(folder)) return [];

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }

        if (files.Length == 0) return [];

        var order = ReadOrderHints();

        var items = new List<PinnedApp>(files.Length);
        foreach (var file in files)
        {
            var shortcutName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(shortcutName)) continue;

            var target = ResolveShortcut(file);
            var displayName = ShellWindowEnumerator.GetFriendlyAppName(target ?? "") ?? shortcutName;

            items.Add(new PinnedApp(
                ShortcutPath: file,
                ShortcutName: shortcutName,
                TargetPath: target,
                DisplayName: displayName,
                Order: order.TryGetValue(shortcutName, out var position) ? position : int.MaxValue));
        }

        return items
            .OrderBy(i => i.Order)
            .ThenBy(i => i.ShortcutName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 从注册表 Taskband 二进制里推测固定项的先后顺序。
    /// 返回"快捷方式名（不含 .lnk）→ 首次出现偏移"的映射；失败返回空表。
    /// </summary>
    private static Dictionary<string, int> ReadOrderHints()
    {
        var hints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var valueName in (string[])["FavoritesResolve", "Favorites"])
        {
            byte[]? blob = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey, writable: false);
                blob = key?.GetValue(valueName) as byte[];
            }
            catch
            {
                // 注册表被策略锁定时静默跳过。
            }

            if (blob is null || blob.Length == 0) continue;

            // shell item 清单是 UTF-16LE 的，直接按 UTF-16 解码后做子串查找。
            var text = System.Text.Encoding.Unicode.GetString(blob);

            foreach (var hint in new[] { ".lnk" })
            {
                var searchStart = 0;
                while (true)
                {
                    var index = text.IndexOf(hint, searchStart, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;

                    // 往前回溯出完整的文件名：以文件名里的非控制字符为准。
                    var nameStart = index;
                    while (nameStart > 0 && !IsNameBoundary(text[nameStart - 1])) nameStart--;
                    var candidate = text[nameStart..index];

                    if (candidate.Length > 0 && !hints.ContainsKey(candidate))
                    {
                        hints[candidate] = index;
                    }

                    searchStart = index + hint.Length;
                }
            }
        }

        return hints;
    }

    /// <summary>文件名与 shell item 结构分隔符的分界判定。</summary>
    private static bool IsNameBoundary(char c) =>
        char.IsControl(c) || c is '\\' or '/' or '"' or '<' or '>' or '|' or ':' or '*' or '?';

    /// <summary>固定项文件夹是否存在，用于诊断输出。</summary>
    public static bool FolderExists => Directory.Exists(PinnedFolder);
}
