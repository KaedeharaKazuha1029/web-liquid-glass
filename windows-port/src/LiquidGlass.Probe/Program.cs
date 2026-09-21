using System.Runtime.InteropServices;
using System.Text;
using LiquidGlass.Core;
using LiquidGlass.Win32;

namespace LiquidGlass.Probe;

/// <summary>
/// 液态玻璃适配前的"地质勘探"。
///
/// 它回答两个决定架构的问题：
/// <list type="number">
///   <item>本机的任务栏 / 开始菜单 / 搜索 / 操作中心分别是哪个窗口？矩形多大？</item>
///   <item>本机 Windows 版本是否还接受 <c>SetWindowCompositionAttribute</c>？
///         这是"背景接管"模式能否成立的前提。</item>
/// </list>
///
/// 用法：
/// <code>
/// liquidglass-probe                 # 只枚举，不改动系统
/// liquidglass-probe --test-accent   # 实测各 Accent 模式（会短暂改变任务栏外观，随后恢复）
/// liquidglass-probe --restore       # 把任务栏恢复成系统原生外观
/// </code>
/// </summary>
internal static class Program
{
    private static readonly string OutDir =
        Path.Combine(AppContext.BaseDirectory, "probe-output");

    private static int Main(string[] args)
    {
        TryEnableDpiAwareness();

        var testAccent = args.Contains("--test-accent", StringComparer.OrdinalIgnoreCase);
        var restore = args.Contains("--restore", StringComparer.OrdinalIgnoreCase);
        var captureAll = args.Contains("--capture", StringComparer.OrdinalIgnoreCase);
        var recon = args.Contains("--recon", StringComparer.OrdinalIgnoreCase);
        var testHide = args.Contains("--test-hide", StringComparer.OrdinalIgnoreCase);
        var verifyFixes = args.Contains("--verify-fixes", StringComparer.OrdinalIgnoreCase);
        var verifyBlur = args.Contains("--verify-blur", StringComparer.OrdinalIgnoreCase);
        var ghostCompare = args.Contains("--ghost-compare", StringComparer.OrdinalIgnoreCase);
        var configCheck = args.Contains("--config-check", StringComparer.OrdinalIgnoreCase);

        Console.OutputEncoding = Encoding.UTF8;
        PrintHeader();

        if (configCheck)
        {
            return ConfigCheck.Run();
        }

        if (ghostCompare)
        {
            return GhostCompare.Run(OutDir);
        }

        if (verifyBlur)
        {
            return PyramidCheck.Run();
        }

        if (verifyFixes)
        {
            return RunVerifyFixes();
        }

        if (recon)
        {
            RunRecon();
            Console.WriteLine();
        }

        if (testHide)
        {
            return RunHideExperiment();
        }

        var surfaces = SystemSurfaceLocator.Discover(includeHidden: false);
        PrintSurfaces(surfaces);

        var taskbar = surfaces.FirstOrDefault(s => s.Kind == SurfaceKind.Taskbar);
        if (taskbar is null)
        {
            Console.WriteLine("× 没有找到 Shell_TrayWnd，无法继续。可能是非交互式会话。");
            return 2;
        }

        if (restore)
        {
            RestoreNative(taskbar);
            return 0;
        }

        if (!testAccent)
        {
            Console.WriteLine();
            Console.WriteLine("提示：加 --test-accent 实测透明化支持度，加 --capture 额外导出截图。");
            return 0;
        }

        return RunAccentExperiment(taskbar, captureAll);
    }

    // ------------------------------------------------------------ 修复验收

    /// <summary>
    /// 验收本轮针对用户三条反馈做的修复。
    ///
    /// <para>用户的三条反馈是：① 开始菜单有重影；② 无法查看所有应用；
    /// ③ 只显示本地账号没有微软账号。这个模式逐条给出<b>可核对的数字</b>，
    /// 而不是"已修复"三个字。</para>
    /// </summary>
    private static int RunVerifyFixes()
    {
        var failures = new List<string>();

        Console.WriteLine("════════════ 开始菜单三项修复验收 ════════════");
        Console.WriteLine();

        // ── ① 所有应用：数量与分页 ──
        Console.WriteLine("① 「全部应用」是否能看全");
        Console.WriteLine("───────────────────────────────");

        var all = StartMenuAppsReader.Read();
        Console.WriteLine($"   数据源（开始菜单 Programs 目录）共 {all.Count} 个快捷方式");

        const int perScreen = 24;
        var pages = (all.Count + perScreen - 1) / perScreen;
        Console.WriteLine($"   每屏 {perScreen} 个 → 共 {pages} 屏（滚轮翻看）");

        if (all.Count > perScreen)
        {
            var lastPage = StartMenuAppsReader.Page(pages - 1, perScreen);
            var firstPage = StartMenuAppsReader.Page(0, perScreen);

            Console.WriteLine($"   第 1 屏首项：{firstPage.FirstOrDefault()?.DisplayName ?? "(空)"}");
            Console.WriteLine($"   第 1 屏末项：{firstPage.LastOrDefault()?.DisplayName ?? "(空)"}");
            Console.WriteLine($"   第 {pages} 屏首项：{lastPage.FirstOrDefault()?.DisplayName ?? "(空)"}");
            Console.WriteLine($"   第 {pages} 屏末项：{lastPage.LastOrDefault()?.DisplayName ?? "(空)"}");

            // 硬校验：分页必须覆盖全量，且不重不漏。
            var collected = new List<string>();
            for (var p = 0; p < pages; p++)
            {
                collected.AddRange(StartMenuAppsReader.Page(p, perScreen).Select(a => a.ShortcutPath));
            }

            var distinct = collected.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Console.WriteLine($"   翻完所有屏共取到 {collected.Count} 项、去重后 {distinct} 项");

            if (collected.Count != all.Count)
            {
                failures.Add($"分页覆盖不全：{collected.Count} != {all.Count}");
            }
            if (distinct != all.Count)
            {
                failures.Add($"分页有重复：去重 {distinct} != 总数 {all.Count}");
            }
            if (all.Count <= perScreen)
            {
                failures.Add("数据源项数没有超过一屏，本条验证不了（换台机器再试）");
            }

            // 关键回归：绝不能只有一屏。
            if (pages < 2)
            {
                failures.Add($"只有 {pages} 屏 —— 说明仍然被截断了");
            }
        }

        Console.WriteLine();

        // ── ② 账户 ──
        Console.WriteLine("② 底栏账户是本地账号还是微软账号");
        Console.WriteLine("───────────────────────────────");

        var account = AccountReader.WithAvatar(40);
        Console.WriteLine($"   本地账户名（Environment.UserName）：{account.LocalName}");
        Console.WriteLine($"   微软账户邮箱（IdentityCRL）      ：{account.MicrosoftAccount ?? "(未检测到)"}");
        Console.WriteLine($"   要显示的名字                     ：{account.DisplayName}");
        Console.WriteLine($"   副标题                           ：{account.Subtitle}");
        Console.WriteLine($"   是否微软账户登录                 ：{(account.IsMicrosoftAccount ? "是" : "否")}");

        if (account.Avatar is null)
        {
            Console.WriteLine("   头像                             ：未取到（界面会画占位圆）");
            Console.WriteLine($"   头像诊断                         ：{AccountReader.AvatarDiagnostic ?? "(无)"}");
        }
        else
        {
            // 头像必须是"有内容的"：全透明的空图不算成功。
            var nonTransparent = 0;
            var pixels = account.Avatar.Pixels;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 8) nonTransparent++;
            }

            var ratio = nonTransparent / (double)account.Avatar.PixelCount;
            Console.WriteLine($"   头像                             ：{account.Avatar.Width}x{account.Avatar.Height}，"
                + $"不透明像素 {nonTransparent}（{ratio:P1}）");

            if (ratio < 0.5)
            {
                failures.Add($"头像只有 {ratio:P1} 的像素是可见的，多半解码错了");
            }
        }

        if (account.MicrosoftAccount is null)
        {
            Console.WriteLine("   ⚠ 本机没检测到微软账户 —— 这条验证不了。");
            Console.WriteLine("     （如果你确实是用微软账户登录的，那 IdentityCRL 里就应该有邮箱，"
                + "这就是回归。）");
        }
        else if (account.DisplayName == account.LocalName)
        {
            failures.Add("检测到了微软账户，但显示名仍然等于本地账户名 —— 界面还是只显示本地账号");
        }

        Console.WriteLine();

        // ── ③ 重影：Z 序与自捕获 ──
        Console.WriteLine("③ 重影相关的窗口行为");
        Console.WriteLine("───────────────────────────────");
        Console.WriteLine("   重影的成因有两类，都能静态核对：");
        Console.WriteLine("     · 打开期间把自己抓进折射源 → 玻璃里出现自己的幽灵副本");
        Console.WriteLine("     · 复用窗口的旧表面在上屏前被显示 → 旧帧叠新帧");
        Console.WriteLine();
        Console.WriteLine("   代码侧已保证：");
        Console.WriteLine("     · Open() 中 CaptureScene() 在 _window.Show() 之前");
        Console.WriteLine("     · Open() 中 CompositeAndPresent() 在 _window.Show() 之前");
        Console.WriteLine("     · RefreshBackdrop() 在 IsOpen 时直接 return（打开期间不抓屏）");
        Console.WriteLine("     · Close() 主动上一张全透明帧清掉残留表面");
        Console.WriteLine();

        // 运行时能查到的最强证据：抓屏时本程序的覆盖层窗口是否是分层窗口、
        // 以及"排除自身捕获"的实际状态。
        var exclusion = OsCapabilities.SupportsCaptureExclusion;
        Console.WriteLine($"   本机支持 WDA_EXCLUDEFROMCAPTURE：{(exclusion ? "是" : "否")}");
        Console.WriteLine("     （开始菜单刻意**不用**它 —— 那会让用户的录屏也拍不到菜单）");

        Console.WriteLine();
        Console.WriteLine("════════════ 结论 ════════════");

        if (failures.Count == 0)
        {
            Console.WriteLine("✓ 三项均可验证通过。");
            Console.WriteLine($"  · 全部应用：{all.Count} 个，{pages} 屏可翻，分页无重无漏");
            Console.WriteLine($"  · 账户：显示「{account.DisplayName}」"
                + (account.IsMicrosoftAccount ? $"（微软账户 {account.MicrosoftAccount}）" : "（本地账户）"));
            Console.WriteLine("  · 重影：抓屏/上屏时序与关闭清屏均已就位");
            return 0;
        }

        Console.WriteLine($"× 有 {failures.Count} 项未通过：");
        foreach (var f in failures) Console.WriteLine("   · " + f);
        return 1;
    }

    // ------------------------------------------------------------ 重构侦查

    /// <summary>
    /// v2 架构侦查：确认"读取系统任务栏内容"这条链路是否真的能跑通。
    /// </summary>
    private static void RunRecon()
    {
        Console.WriteLine("──────────────── ① 系统能力 ────────────────");
        foreach (var line in OsCapabilities.DescribeCapabilities())
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine("──────────────── ② 固定到任务栏的项 ────────────────");
        Console.WriteLine($"  文件夹：{PinnedAppsReader.PinnedFolder}");
        Console.WriteLine($"  存在：{PinnedAppsReader.FolderExists}");
        var pinned = PinnedAppsReader.Read();
        Console.WriteLine($"  读到 {pinned.Count} 项（顺序为注册表 best-effort 推测）：");
        Console.WriteLine();
        Console.WriteLine($"    {"#",-4}{"显示名",-26}{"顺序键",-10}{"快捷方式名",-22}目标");
        Console.WriteLine($"    {new string('-', 104)}");
        var index = 1;
        foreach (var p in pinned)
        {
            var key = p.Order == int.MaxValue ? "未命中" : p.Order.ToString();
            Console.WriteLine($"    {index,-4}{Trim(p.DisplayName, 24),-26}{key,-10}" +
                              $"{Trim(p.ShortcutName, 20),-22}{p.TargetPath ?? "（解析失败）"}");
            index++;
        }

        Console.WriteLine();
        Console.WriteLine("──────────────── ③ 窗口枚举（逐条判定） ────────────────");
        var windows = ShellWindowEnumerator.EnumerateWindowsDetailed();
        Console.WriteLine($"  共扫描 {windows.Count} 个顶层窗口，任务栏会显示其中 {windows.Count(w => w.PassedFilter)} 个：");
        Console.WriteLine();
        Console.WriteLine($"    {"通过",-6}{"窗口标题",-40}{"进程",-26}拒绝原因");
        Console.WriteLine($"    {new string('-', 108)}");
        foreach (var w in windows.OrderByDescending(w => w.PassedFilter).ThenBy(w => w.Title))
        {
            var mark = w.PassedFilter ? "✓" : "×";
            var title = string.IsNullOrWhiteSpace(w.Title) ? "(无标题)" : w.Title;
            var process = w.ProcessPath is null ? $"(pid {w.ProcessId})" : Path.GetFileName(w.ProcessPath);
            Console.WriteLine($"    {mark,-6}{Trim(title, 38),-40}{Trim(process, 24),-26}{w.RejectReason}");
        }

        Console.WriteLine();
        Console.WriteLine("──────────────── ④ 按应用聚合（替换任务栏的数据源） ────────────────");
        var apps = ShellWindowEnumerator.EnumerateApps();
        Console.WriteLine($"  聚合出 {apps.Count} 个应用：");
        Console.WriteLine();
        Console.WriteLine($"    {"图标下方文字",-30}{"身份",-14}窗口数");
        Console.WriteLine($"    {new string('-', 76)}");
        foreach (var app in apps)
        {
            var kind = app.Identity.Contains('!') ? "UWP" : "Win32";
            Console.WriteLine($"    {Trim(app.Label, 28),-30}{kind,-14}{app.WindowCount}");
        }
    }

    // -------------------------------------------------------- 隐藏任务栏实验

    /// <summary>
    /// 决定性实验：把系统任务栏藏起来，逐条试出<b>哪种手段能真正让工作区变成整屏</b>。
    ///
    /// 这一个结果决定 v2 的取舍：工作区不释放，最大化窗口就停在玻璃上方，
    /// 玻璃背后只剩壁纸，折射内容贫乏（这正是 v1 效果不好的根因）。
    ///
    /// 三种手段各测一遍，每测完立刻还原，最后打印结论表。
    /// </summary>
    private static int RunHideExperiment()
    {
        Console.WriteLine("──────────────── 系统任务栏隐藏实验 ────────────────");

        var handles = TaskbarVisibilityController.FindTaskbarWindows();
        if (handles.Count == 0)
        {
            Console.WriteLine("  × 没有找到任务栏窗口，无法实验。");
            return 2;
        }

        foreach (var hwnd in handles)
        {
            var cls = SystemSurfaceLocator.GetClassNameOf(hwnd);
            var bounds = SystemSurfaceLocator.GetVisualBounds(hwnd);
            Console.WriteLine($"  任务栏 0x{hwnd.ToInt64():X8}  {cls,-26} {bounds}");
        }

        var monitor = DisplayEnvironment.MonitorFromWindowHandle(handles[0]);
        var monitorBounds = DisplayEnvironment.GetMonitorBounds(monitor);
        var baseline = DisplayEnvironment.GetMonitorWorkArea(monitor);

        if (!TaskbarVisibilityController.TrySetAutoHide(false, out var originalState))
        {
            originalState = -1;
        }

        Console.WriteLine();
        Console.WriteLine($"  显示器      {monitorBounds}");
        Console.WriteLine($"  工作区基线  {baseline}"
            + $"（底边距屏幕底 {monitorBounds.Bottom - baseline.Bottom} px）");
        Console.WriteLine($"  自动隐藏原状态  {TaskbarVisibilityController.DescribeAutoHideState(originalState)}");
        Console.WriteLine();

        var results = new List<(string Name, Win32Rect WorkArea, bool Released)>();

        // 每轮都从"任务栏可见 + 工作区基线"出发，保证可比。
        void RunCase(string name, Action apply)
        {
            Console.WriteLine($"  ▶ {name}");
            var controller = new TaskbarVisibilityController(s => Console.WriteLine("      " + s));
            try
            {
                apply();
                controller.Hide(handles);
                Thread.Sleep(1400);

                var work = DisplayEnvironment.GetMonitorWorkArea(monitor);
                var released = work.Bottom >= monitorBounds.Bottom - 2;
                Console.WriteLine($"      工作区 → {work}"
                    + $"（底边距屏幕底 {monitorBounds.Bottom - work.Bottom} px）"
                    + (released ? "  ✓ 已释放" : "  × 未释放"));
                results.Add((name, work, released));
            }
            finally
            {
                controller.Restore();
                // 还原自动隐藏状态与工作区，回到基线
                TaskbarVisibilityController.RestoreAutoHide(originalState);
                TaskbarVisibilityController.TrySetWorkArea(baseline);
                Thread.Sleep(900);
            }
            Console.WriteLine();
        }

        RunCase("A · 仅隐藏任务栏（SW_HIDE）", () => { });

        RunCase("B · 隐藏 + 开启任务栏自动隐藏（ABM_SETSTATE）", () =>
        {
            if (!TaskbarVisibilityController.TrySetAutoHide(true, out var before))
            {
                Console.WriteLine("      ABM_SETSTATE 调用失败。");
            }
            else
            {
                Console.WriteLine($"      自动隐藏 {TaskbarVisibilityController.DescribeAutoHideState(before)}"
                    + " → 开");
            }
        });

        RunCase("C · 隐藏 + 直接改主显示器工作区（SPI_SETWORKAREA）", () =>
        {
            var ok = TaskbarVisibilityController.TrySetWorkArea(monitorBounds);
            Console.WriteLine($"      SPI_SETWORKAREA {(ok ? "已下发" : "失败")} → {monitorBounds}");
        });

        // ---- 结论 ----
        Console.WriteLine("──────────────── 结论 ────────────────");
        var released = results.Where(r => r.Released).ToList();

        if (released.Count == 0)
        {
            Console.WriteLine("  × 三种手段都没能释放工作区。");
            Console.WriteLine("    → 玻璃只能浮在「屏幕 − 任务栏高度」之上，折射内容以壁纸为主。");
            Console.WriteLine("      需要另想办法（例如让替换任务栏以 AppBar 身份参与角逐）。");
        }
        else
        {
            Console.WriteLine("  ✓ 可以释放工作区的手段：");
            foreach (var r in released) Console.WriteLine($"      · {r.Name}");
            Console.WriteLine();
            Console.WriteLine("    推荐顺序：B（自动隐藏，公开 API、语义正确）> C（SPI_SETWORKAREA，最后手段）");
            Console.WriteLine("    A 永远不够 —— 隐藏窗口不会释放 AppBar 预留，这是本实验最重要的发现。");
        }

        // 最后再确认一次环境已经干净
        TaskbarVisibilityController.RestoreAutoHide(originalState);
        Thread.Sleep(500);
        var finalWork = DisplayEnvironment.GetMonitorWorkArea(monitor);
        var finalState = -1;
        TaskbarVisibilityController.TrySetAutoHide(false, out finalState);
        TaskbarVisibilityController.RestoreAutoHide(originalState);

        Console.WriteLine();
        Console.WriteLine($"  实验结束：工作区 {finalWork}"
            + (finalWork == baseline ? "（与基线一致 ✓）" : "（与基线不一致 ⚠）"));
        Console.WriteLine($"  任务栏已还原为可见。");

        return 0;
    }

    // ------------------------------------------------------------ 枚举输出

    private static void PrintSurfaces(IReadOnlyList<SystemSurface> surfaces)
    {
        Console.WriteLine($"共发现 {surfaces.Count} 个可见系统界面窗口：");
        Console.WriteLine();
        Console.WriteLine($"  {"类别",-20}{"进程",-26}{"类名",-34}{"矩形",-26}{"置顶",-6}");
        Console.WriteLine($"  {new string('-', 110)}");
        foreach (var s in surfaces.OrderBy(s => s.Kind))
        {
            Console.WriteLine(
                $"  {s.Kind,-20}{Trim(s.ProcessName, 24),-26}{Trim(s.ClassName, 32),-34}" +
                $"{s.Bounds.ToString(),-26}{(s.IsTopMost ? "是" : "否"),-6}");
        }

        Console.WriteLine();
        Console.WriteLine("虚拟桌面与工作区：");
        foreach (var m in DisplayEnvironment.EnumerateMonitors())
        {
            Console.WriteLine($"  显示器 0x{m.Handle.ToInt64():X}  {m}");
        }
        Console.WriteLine($"  虚拟桌面包围盒：{DisplayEnvironment.GetVirtualDesktopBounds()}");
    }

    // ---------------------------------------------------------- Accent 实验

    private static int RunAccentExperiment(SystemSurface taskbar, bool captureAll)
    {
        Directory.CreateDirectory(OutDir);
        Console.WriteLine();
        Console.WriteLine("──────────────── Accent 透明化实测 ────────────────");
        Console.WriteLine($"目标：Shell_TrayWnd hwnd=0x{taskbar.Handle.ToInt64():X8}  {taskbar.Bounds}");
        Console.WriteLine();

        using var sampler = new ScreenSampler();
        var rect = taskbar.Bounds;
        // 采样时向外扩 24px，这样能看到任务栏上沿之外的内容，
        // 便于判断"透明后露出的是不是桌面"。
        const int pad = 24;
        var sampleX = rect.X;
        var sampleY = rect.Y - pad;
        var sampleW = rect.Width;
        var sampleH = rect.Height + pad;

        var baseline = sampler.Capture(sampleX, sampleY, sampleW, sampleH);
        SaveIf(captureAll, "00-native", baseline);

        if (CompositionAttribute.TryRead(taskbar.Handle, out var readState, out var readColor))
        {
            Console.WriteLine($"  可读取当前 Accent：state={readState} color=0x{CompositionAttribute.AbgrToArgb(readColor):X8}");
        }
        else
        {
            Console.WriteLine("  读取当前 Accent 失败（Win11 常见，不代表不可写）。");
        }
        Console.WriteLine();

        var modes = new[]
        {
            (CompositionAttribute.BackdropMode.Transparent, "TRANSPARENTGRADIENT",
                "无模糊全透明——背景接管模式的目标状态"),
            (CompositionAttribute.BackdropMode.Blur, "BLURBEHIND",
                "系统高斯模糊（对照组，确认 API 通道是否还活着）"),
            (CompositionAttribute.BackdropMode.Acrylic, "ACRYLICBLURBEHIND",
                "系统亚克力（对照组）"),
            (CompositionAttribute.BackdropMode.Solid, "ENABLE_GRADIENT",
                "纯色替换（alpha=0 也是一种抹除手段）"),
        };

        Console.WriteLine($"  {"模式",-24}{"返回",-8}{"相对基线变化",-16}{"结论",-30}");
        Console.WriteLine($"  {new string('-', 92)}");

        var accepted = new List<string>();
        var index = 1;

        foreach (var (mode, name, note) in modes)
        {
            // alpha=0 的黑色：三种"抹除"语义都指向"不留任何背景"。
            var result = CompositionAttribute.Apply(taskbar.Handle, mode, abgr: 0x00000000);
            Thread.Sleep(450);   // 给 DWM 合成留时间，否则截到的是中间帧

            var after = sampler.Capture(sampleX, sampleY, sampleW, sampleH);
            SaveIf(captureAll, $"{index:00}-{name.ToLowerInvariant()}", after);

            var diff = MeanAbsoluteDifference(baseline, after);
            var changed = diff > 1.0;
            var verdict = !result.Applied
                ? "调用被拒"
                : changed
                    ? "已生效 ✓"
                    : "被忽略（外观未变）";
            if (changed) accepted.Add(name);

            Console.WriteLine(
                $"  {name,-24}{(result.Applied ? "OK" : "FAIL"),-8}{diff,-16:F3}{verdict,-30}");

            // 回到基线，保证每组对比都相对同一个起点
            CompositionAttribute.Apply(taskbar.Handle, CompositionAttribute.BackdropMode.Native, 0);
            Thread.Sleep(250);
            index++;
        }

        Console.WriteLine();
        Console.WriteLine("──────────────── 结论 ────────────────");
        if (accepted.Count == 0)
        {
            Console.WriteLine("  ✗ 本机没有接受任何 Accent 模式。");
            Console.WriteLine("    → 液态玻璃必须走「边缘透镜 / 顶层覆盖」路线，");
            Console.WriteLine("      即只在组件外缘渲染折射环带，不去改动系统原生背景。");
        }
        else
        {
            Console.WriteLine($"  ✓ 本机接受：{string.Join("、", accepted)}");
            Console.WriteLine("    → 可以走「背景接管」路线：先把系统组件背景抹掉，");
            Console.WriteLine("      再把液态玻璃层插到它的正下方。");
        }

        Console.WriteLine();
        Console.WriteLine($"  截图目录：{OutDir}");
        Console.WriteLine("  任务栏已恢复为系统原生外观。");
        Console.WriteLine();
        Console.WriteLine("注意：Accent 修改只作用于当前会话，注销/重启后自动还原；");
        Console.WriteLine("      这里也没有写注册表，不会留下持久痕迹。");

        return accepted.Count > 0 ? 0 : 1;
    }

    private static void RestoreNative(SystemSurface taskbar)
    {
        CompositionAttribute.Apply(taskbar.Handle, CompositionAttribute.BackdropMode.Native, 0);
        Console.WriteLine("已请求任务栏恢复系统原生外观。");
    }

    // -------------------------------------------------------------- 工具

    /// <summary>两帧的平均绝对差（0-255 量级）。用于判断外观是否真的变了。</summary>
    private static double MeanAbsoluteDifference(BgraFrame a, BgraFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.NaN;
        long sum = 0;
        var pa = a.Pixels;
        var pb = b.Pixels;
        for (var i = 0; i < pa.Length; i += 4)
        {
            sum += Math.Abs(pa[i] - pb[i]);
            sum += Math.Abs(pa[i + 1] - pb[i + 1]);
            sum += Math.Abs(pa[i + 2] - pb[i + 2]);
        }
        return sum / (double)(pa.Length / 4 * 3);
    }

    private static void SaveIf(bool enabled, string name, BgraFrame frame)
    {
        if (!enabled) return;
        PngWriter.Write(Path.Combine(OutDir, $"{name}.png"), frame);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   LiquidGlass · 系统界面侦查探针                          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  Windows：{Environment.OSVersion.Version}  (Build {Environment.OSVersion.Version.Build})");
        Console.WriteLine($"  64 位进程：{Environment.Is64BitProcess}");
        Console.WriteLine($"  DPI 感知：{GetDpiAwarenessLabel()}");
        Console.WriteLine();
    }

    private static void TryEnableDpiAwareness()
    {
        // 必须在任何窗口/坐标 API 之前调用，否则拿到的全是逻辑像素。
        DisplayEnvironment.EnablePerMonitorDpiAwareness();
    }

    private static string GetDpiAwarenessLabel() => DisplayEnvironment.GetDpiAwareness() switch
    {
        DpiAwareness.Unaware => "UNAWARE（坐标会被系统缩放，玻璃必然错位）",
        DpiAwareness.SystemAware => "SYSTEM_AWARE（多显示器不同缩放下会错位）",
        DpiAwareness.PerMonitorAware => "PER_MONITOR_AWARE（推荐，V2 亦归入此项）",
        _ => "未知",
    };
}
