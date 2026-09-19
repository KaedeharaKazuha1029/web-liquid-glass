namespace LiquidGlass.Win32;

/// <summary>开始菜单里的一个程序快捷方式。</summary>
public sealed record MenuApp(string ShortcutPath, string DisplayName, string? TargetPath);

/// <summary>
/// 枚举「所有应用」—— 读开始菜单 Programs 文件夹里的 <c>.lnk</c>。
///
/// <para><b>数据来源</b>：两个目录，从 Windows 7 一直沿用到 Windows 11：</para>
/// <list type="bullet">
///   <item><c>%ProgramData%\Microsoft\Windows\Start Menu\Programs</c>（全机器）</item>
///   <item><c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>（当前用户）</item>
/// </list>
///
/// <para>这正是系统开始菜单「所有应用」列表的来源，所以列出来的东西和系统一致。
/// 只取一层到两层的子目录（卸载工具、Windows 管理工具之类），避免把深层目录里
/// 一堆说明性快捷方式也拉进来。</para>
///
/// <para><b>⚠️ 不要在这里截断</b>：早先 <c>Read(max: 24)</c> 把 168 个快捷方式砍到 24 个，
/// 而界面上又没有翻页入口 —— 用户的原话就是"无法查看所有应用"。
/// <b>截断是界面层的滚动逻辑该管的事，不该藏在数据读取里</b> ——
/// 藏在这里的话，调用方看代码时只会看到"读了 24 个"，根本想不到还有 144 个被丢了。
/// 现在 <paramref name="max"/> 默认 <see cref="int.MaxValue"/>（全量返回）。</para>
/// </summary>
public static class StartMenuAppsReader
{
    /// <summary>
    /// 读全部快捷方式（按名称排序）。
    ///
    /// <para><paramref name="max"/> 只在调用方<b>确实</b>需要限量时才传；
    /// 界面的滚动应该用 <see cref="Page"/> 之类的分页方法，而不是砍数据源。</para>
    /// </summary>
    public static IReadOnlyList<MenuApp> Read(int max = int.MaxValue)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<MenuApp>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                continue;   // 个别目录没权限就跳过，不要因为一个目录失败就整块空掉
            }

            foreach (var file in files)
            {
                // 只认浅层（根目录 + 一层子目录）。深层多半是"卸载 xxx""帮助"这类噪音。
                var relative = Path.GetRelativePath(root, file);
                if (relative.Count(c => c is '\\' or '/') > 1) continue;

                var name = Path.GetFileNameWithoutExtension(file);

                // 系统自带的一堆维护类快捷方式不进列表，否则"所有应用"会被淹没。
                if (IsNoise(name)) continue;
                if (!seen.Add(name)) continue;

                found.Add(new MenuApp(file, name, null));
            }
        }

        return found
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCulture)
            .Take(max <= 0 ? int.MaxValue : max)
            .ToList();
    }

    /// <summary>
    /// 取一页。
    ///
    /// <para>为什么要"读全部再切页"而不是"读的时候就跳过前 N 个"：
    /// <see cref="Read"/> 的排序是<b>按名称</b>的，必须先把全部收齐才能定序 ——
    /// 跳过前 N 个再排序会得到错误的分页。</para>
    ///
    /// <para>全量读取的成本很低：只枚举目录 + 读文件名，<b>不解码任何图标</b>
    /// （图标解码才是贵的那部分，由界面按需懒加载）。实测 168 个快捷方式的枚举在 10ms 以内。</para>
    /// </summary>
    public static IReadOnlyList<MenuApp> Page(int pageIndex, int pageSize)
    {
        pageSize = Math.Max(1, pageSize);
        var all = Read();
        var skip = Math.Max(0, pageIndex) * pageSize;
        if (skip >= all.Count) return [];

        return all.Skip(skip).Take(pageSize).ToList();
    }

    /// <summary>全部快捷方式的总数（给界面算页数用）。</summary>
    public static int Count() => Read().Count;

    private static bool IsNoise(string name)
    {
        ReadOnlySpan<string> noise =
        [
            "卸载", "帮助", "说明", "readme", "uninstall", "help", "文档", "documentation",
            "网站", "website", "官网", "更新", "update", "修复", "repair",
        ];

        foreach (var token in noise)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
