// 验收「配置文件到底有没有生效」。
//
// 起因：用户报开始菜单有重影。在合成图上已经把模糊半径调到 96 并证明显著改善，
// 但实机截图里重影依旧 —— 因为**实机读的根本不是仓库里那份 config/liquidglass.json**。
//
// 本模式存在的意义就是让"配置有没有被真正读进去"变成一个可核对的数字，
// 而不是靠"我以为改了"。
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidGlass.Probe;

internal static class ConfigCheck
{
    /// <summary>运行时真正会读的那份配置（与 AppConfig.DefaultPath 一致）。</summary>
    public static string RuntimeConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiquidGlass", "liquidglass.json");

    public static int Run()
    {
        Console.WriteLine("════════════ 配置生效性验收 ════════");
        Console.WriteLine();

        var runtime = RuntimeConfigPath;
        Console.WriteLine($"运行时配置路径：{runtime}");

        if (!File.Exists(runtime))
        {
            Console.WriteLine("  × 文件不存在 —— 程序会用内存默认值跑，任何改动都不会生效。");
            return 1;
        }

        var info = new FileInfo(runtime);
        Console.WriteLine($"  大小        ：{info.Length} 字节");
        Console.WriteLine($"  最后修改    ：{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  距今        ：{(DateTime.Now - info.LastWriteTime).TotalDays:F1} 天");

        var json = File.ReadAllText(runtime);
        var root = JsonNode.Parse(json) as JsonObject;

        if (root is null)
        {
            Console.WriteLine("  × 无法解析为 JSON 对象。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("顶层段落：");
        foreach (var kv in root)
        {
            var kind = kv.Value is JsonObject ? "对象" : kv.Value is JsonArray ? "数组" : "标量";
            Console.WriteLine($"    {kv.Key,-24}{kind}");
        }

        // ── 逐条核对"本轮依赖的参数"到底有没有被读到 ──
        Console.WriteLine();
        Console.WriteLine("本轮依赖的开始菜单参数（缺一项就说明那份配置管不到它）：");
        Console.WriteLine();
        Console.WriteLine($"    {"参数",-30}{"配置里有？",-14}{"取值 / 回退到的默认值"}");
        Console.WriteLine($"    {new string('─', 78)}");

        var failures = new List<string>();

        var glass = root["glassStartMenu"] as JsonObject
                 ?? root["GlassStartMenu"] as JsonObject;

        if (glass is null)
        {
            Console.WriteLine($"    {"glassStartMenu 整段",-30}{"× 没有",-14}"
                + "→ 全部参数回退到代码默认值！");
            failures.Add("配置里没有 glassStartMenu 段，开始菜单的所有可调参数都不生效");
        }

        // (参数名, 配置里的键, 期望值, 缺省时的回退值)
        //
        // ⚠️ 期望值必须与 `GlassStartMenu.Settings` 的类默认值一致 ——
        // 配置里没有这一项时，程序用的是**类默认值**，不是仓库模板里的值。
        // 这两处一旦不一致，就会出现"配置写了 0.72、实机跑 0.30"这种鬼故事。
        var checks = new (string Name, string Key, string Expected, string Fallback)[]
        {
            ("底衬不透明度", "scrimOpacity", "0.72", "0.30"),
            ("雾化模糊半径", "frostedBlurRadius", "96", "96"),
            ("玻璃渲染倍率", "glassScale", "0.25", "0.25"),
            ("预热帧数", "warmupFrames", "1", "1"),
            ("每像素路径数", "pathsPerPixel", "2", "2"),
        };

        foreach (var (name, key, expected, fallback) in checks)
        {
            var node = glass?[key] ?? glass?[ToPascal(key)];
            if (node is not null)
            {
                var actual = node.ToJsonString();
                var ok = actual == expected;
                Console.WriteLine($"    {name,-30}{(ok ? "✓ 有" : "⚠ 有"),-14}{actual}"
                    + (ok ? "" : $"   ← 期望 {expected}"));
                if (!ok) failures.Add($"{name}（{key}）是 {actual}，期望 {expected}");
            }
            else
            {
                Console.WriteLine($"    {name,-30}{"× 没有",-14}{fallback}   ← 期望 {expected}");
                failures.Add($"{name}（{key}）没配置，实际用 {fallback}"
                    + (fallback == expected ? "" : $"，期望 {expected}"));
            }
        }

        // ── 仓库里那份 config/liquidglass.json 与运行时那份是不是同一个 ──
        Console.WriteLine();
        var repoConfig = FindRepoConfig();
        if (repoConfig is null)
        {
            Console.WriteLine("仓库模板 config/liquidglass.json：未找到（跳过对比）");
        }
        else
        {
            var repoInfo = new FileInfo(repoConfig);
            Console.WriteLine("仓库模板 vs 运行时：");
            Console.WriteLine($"    仓库  {repoConfig}");
            Console.WriteLine($"          {repoInfo.Length} 字节，改于 {repoInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    运行时 {runtime}");
            Console.WriteLine($"          {info.Length} 字节，改于 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

            var same = File.ReadAllBytes(repoConfig).SequenceEqual(File.ReadAllBytes(runtime));
            Console.WriteLine();
            if (same)
            {
                Console.WriteLine("    ✓ 两份内容相同 —— 改仓库那份就是改运行时那份。");
            }
            else
            {
                Console.WriteLine("    × 两份**不是**同一个文件：改仓库里的 config/liquidglass.json"
                    + "不会影响实机行为。");
                failures.Add("仓库模板与运行时配置不同步，改了模板实机不生效");
            }
        }

        // ── 结论 ──
        Console.WriteLine();
        Console.WriteLine("════════════ 结论 ════════");
        if (failures.Count == 0)
        {
            Console.WriteLine("✓ 本轮依赖的参数在运行时配置里都存在。");
            return 0;
        }

        Console.WriteLine($"× 有 {failures.Count} 项不对：");
        foreach (var f in failures) Console.WriteLine("   · " + f);
        return 1;
    }

    private static string ToPascal(string camel) =>
        camel.Length == 0 ? camel : char.ToUpperInvariant(camel[0]) + camel[1..];

    /// <summary>从当前目录往上找仓库里的 config/liquidglass.json。</summary>
    private static string? FindRepoConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config", "liquidglass.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
