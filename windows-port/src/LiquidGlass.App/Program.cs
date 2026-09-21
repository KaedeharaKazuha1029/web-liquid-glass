using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LiquidGlass.Core;
using LiquidGlass.Win32;

namespace LiquidGlass.App;

/// <summary>
/// 托盘常驻程序入口。
///
/// 支持的命令行：
/// <code>
/// liquidglass                     正常启动（托盘常驻）
/// liquidglass --config &lt;路径&gt;     使用指定的配置文件
/// liquidglass --diagnose          只做一次环境体检并退出，不修改任何系统外观
/// liquidglass --verify [目录]     端到端自检：接管任务栏、渲染、截图存证、还原
/// liquidglass --no-attach         启动但不接管（只驻留托盘，便于先看菜单）
/// liquidglass --help              显示帮助
/// </code>
/// </summary>
internal static class Program
{
    private const string SingleInstanceName = "LiquidGlass.App.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase)
            || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        // 必须在任何窗口/坐标 API 之前。app.manifest 里也声明了，
        // 但通过 dotnet run 或某些宿主启动时清单可能不生效，这里是双保险。
        //
        // 注意：清单先生效时，这次调用会因为"已经设置过"而返回 false，
        // 那是正常现象。所以判据是**最终感知级别够不够**，而不是返回值。
        DisplayEnvironment.EnablePerMonitorDpiAwareness();
        var dpiOk = DisplayEnvironment.GetDpiAwareness() is DpiAwareness.PerMonitorAware;

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            return RunDiagnose(dpiOk);
        }

        var verifyIndex = Array.FindIndex(args, a => a.Equals("--verify", StringComparison.OrdinalIgnoreCase));
        if (verifyIndex >= 0)
        {
            var dir = verifyIndex + 1 < args.Length && !args[verifyIndex + 1].StartsWith('-')
                ? args[verifyIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "verify-output");
            return RunVerify(dir);
        }

        var realtimeIndex = Array.FindIndex(args, a => a.Equals("--verify-realtime", StringComparison.OrdinalIgnoreCase));
        if (realtimeIndex >= 0)
        {
            var dir = realtimeIndex + 1 < args.Length && !args[realtimeIndex + 1].StartsWith('-')
                ? args[realtimeIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "realtime-verify-output");
            return RunRealtimeVerification(dir);
        }

        var dynamicIndex = Array.FindIndex(args, a => a.Equals("--verify-dynamic", StringComparison.OrdinalIgnoreCase));
        if (dynamicIndex >= 0)
        {
            var dir = dynamicIndex + 1 < args.Length && !args[dynamicIndex + 1].StartsWith('-')
                ? args[dynamicIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "dynamic-verify-output");
            return RunDynamicVerification(dir);
        }

        if (args.Any(a => a.Equals("--tray-probe", StringComparison.OrdinalIgnoreCase)))
        {
            return RunTrayProbe();
        }

        var startMenuIndex = Array.FindIndex(args, a => a.Equals("--verify-startmenu", StringComparison.OrdinalIgnoreCase));
        if (startMenuIndex >= 0)
        {
            var dir = startMenuIndex + 1 < args.Length && !args[startMenuIndex + 1].StartsWith('-')
                ? args[startMenuIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "startmenu-verify-output");
            return RunStartMenuVerification(dir);
        }

        var taskbarIndex = Array.FindIndex(args, a => a.Equals("--verify-taskbar", StringComparison.OrdinalIgnoreCase));
        if (taskbarIndex >= 0)
        {
            var dir = taskbarIndex + 1 < args.Length && !args[taskbarIndex + 1].StartsWith('-')
                ? args[taskbarIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "taskbar-verify-output");
            return RunTaskbarVerification(dir);
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "LiquidGlass 已经在运行了。\n\n请在系统托盘里找到那块玻璃图标。",
                "LiquidGlass", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var configPath = ResolveConfigPath(args);

        // 先用只写控制台的日志完成配置加载，拿到配置后再决定是否写文件。
        var bootstrapLog = new Logger(writeFile: false);
        var config = AppConfig.Load(configPath, bootstrapLog.Write);

        using var logger = new Logger(config.Behavior.WriteLogFile);
        logger.Write("══════════════════════════════════════════════════");
        logger.Write("LiquidGlass 启动");
        logger.Write($"Windows {Environment.OSVersion.Version}  64位进程={Environment.Is64BitProcess}");
        logger.Write($"DPI 感知 = {DisplayEnvironment.GetDpiAwareness()}"
            + (dpiOk ? "" : "（感知级别不足，高 DPI 下玻璃会错位）"));
        logger.Write($"配置文件 = {configPath}");
        logger.Write($"日志文件 = {logger.FilePath ?? "（未启用）"}");

        // 全局兜底：把**任何**未处理异常写进日志。
        //
        // 为什么必须有：主循环的 try/catch 只能接住 UI 线程上的异常。
        // **后台线程（线程池、引擎循环）上的未处理异常会让 .NET 直接杀掉进程**，
        // 既不进那个 catch、也不产生崩溃转储 —— 用户看到的就是"闪退"，
        // 而我们事后一点线索都没有。这个钩子是唯一能留下证据的地方。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                logger.Write($"⚠ 未处理异常（后台线程，进程即将退出）：{e.ExceptionObject}");
            }
            catch (Exception)
            {
                // 日志都写不进去就只能认了，至少别在崩溃处理器里再崩一次。
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.Write($"⚠ 未观察的任务异常（已忽略，不影响运行）：{e.Exception}");
            e.SetObserved();
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var context = new TrayApplicationContext(
            config, configPath, logger, attachOnStartup: !args.Contains("--no-attach", StringComparer.OrdinalIgnoreCase));

        try
        {
            Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.Write($"未处理异常，程序退出：{ex}");
            return 2;
        }

        return 0;
    }

    // ------------------------------------------------------------ 端到端自检

    /// <summary>
    /// 端到端自检：真实接管任务栏 → 渲染到收敛 → 截图存证 → 完整还原。
    ///
    /// 这是"到底有没有生效"的唯一硬证据。之所以要单独一条路径，是因为
    /// 正常运行时会开启"排除自身捕获"（<c>WDA_EXCLUDEFROMCAPTURE</c>），
    /// 那会让我们的玻璃层在截图里消失 —— 自检里必须临时关掉它才能拍到。
    /// 代价是此刻玻璃会轻微采到自己上一帧（内省式反馈），
    /// 这是诊断模式的可接受近似，正常运行不受影响。
    /// </summary>
    private static int RunVerify(string outputDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(outputDir);

        using var log = new Logger(writeFile: false);
        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 端到端自检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine($"输出目录：{Path.GetFullPath(outputDir)}");
        Console.WriteLine();

        var taskbar = SystemSurfaceLocator.Find(SurfaceKind.Taskbar);
        if (taskbar is null)
        {
            Console.WriteLine("✗ 没找到任务栏（Shell_TrayWnd）。非交互式会话下无法自检。");
            return 2;
        }

        Console.WriteLine($"  任务栏：hwnd=0x{taskbar.Handle.ToInt64():X8}  {taskbar.Bounds}");

        // 自检截图必须**包含分层窗口**，否则拍不到我们自己的覆盖层，
        // 只拍到它背后的窗口 —— 据此判断"菜单被遮挡"会得出完全相反的结论。
        using var sampler = new ScreenSampler { IncludeLayeredWindows = true };

        // 采样范围：任务栏向上扩 40px，便于对比"玻璃内"与"玻璃外"
        const int pad = 40;
        var shotX = taskbar.Bounds.X;
        var shotY = taskbar.Bounds.Y - pad;
        var shotW = taskbar.Bounds.Width;
        var shotH = taskbar.Bounds.Height + pad;

        var before = sampler.CapturePadded(shotX, shotY, shotW, shotH);
        PngWriter.Write(Path.Combine(outputDir, "01-before.png"), before);
        Console.WriteLine("  ① 已保存接管前截图 01-before.png");
        Console.WriteLine();

        // 两种折射源各跑一遍：direct 是上游的 1:1 忠实映射，
        // extendFromAbove 是"用未被遮挡的最后一行重建被遮挡区域"。
        // 并排对比可以直观看出任务栏背景频率对效果的影响。
        var results = new List<(SceneSourceMode Mode, double Diff)>();

        var cases = new (SceneSourceMode Mode, string Tag, string Label)[]
        {
            (SceneSourceMode.Direct, "02-direct", "direct（上游 1:1 忠实映射）"),
            (SceneSourceMode.ExtendFromAbove, "03-extend-above", "extendFromAbove（重建被遮挡区域）"),
        };

        foreach (var (mode, tag, label) in cases)
        {
            Console.WriteLine($"  ▶ 折射源：{label}");
            var diff = VerifyOnce(mode, tag, outputDir, sampler, taskbar, before, shotX, shotY, shotW, shotH);
            results.Add((mode, diff));
            Console.WriteLine();
        }

        Console.WriteLine("  ── 结果汇总 ──");
        foreach (var (mode, diff) in results)
        {
            Console.WriteLine($"    {mode,-18} 接管前后平均像素差 {diff,7:F2}");
        }

        var maxDiff = results.Max(r => r.Diff);
        if (maxDiff < 1.0)
        {
            Console.WriteLine();
            Console.WriteLine("  ⚠ 变化极小。可能原因：");
            Console.WriteLine("     · 本机 Shell 未接受 Accent 透明化 → 请跑 liquidglass-probe --test-accent");
            Console.WriteLine("     · 任务栏开启了自动隐藏，或被其它工具接管");
            Console.WriteLine("     · 当前会话不是交互式桌面会话");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  ✓ 任务栏外观已发生显著变化，液态玻璃接管成功。");
            Console.WriteLine("    提示：背景越平坦，折射越「无物可弯」，效果就越接近「只是变透明」。");
            Console.WriteLine("          换成细节丰富的壁纸，或把 scene.source 设为 extendFromAbove 会更明显。");
        }

        Console.WriteLine();
        Console.WriteLine($"截图目录：{Path.GetFullPath(outputDir)}");
        return 0;
    }

    private static double VerifyOnce(
        SceneSourceMode mode, string tag, string outputDir,
        ScreenSampler sampler, SystemSurface taskbar, BgraFrame before,
        int shotX, int shotY, int shotW, int shotH)
    {
        var settings = new GlassSurfaceSettings
        {
            GlassInsetX = 10,
            GlassInsetY = 6,
            ExcludeOverlayFromCapture = false,   // 自检必须让玻璃出现在截图里
            MakeTargetTransparent = true,
            SceneSource = mode,
            PathsPerPixel = 4,
            IdleAccumulation = 48,
            MouseParallax = false,               // 固定相机，保证截图可复现
        };

        var host = GlassSurfaceHost.Attach(taskbar, settings, s => Console.WriteLine($"     {s}"));
        if (host is null)
        {
            Console.WriteLine("     ✗ 接管失败。");
            return -1;
        }

        try
        {
            var frames = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (frames < 80 && DateTime.UtcNow < deadline)
            {
                host.Sync();
                host.Compose();
                frames++;
                if (host.Stats.Converged) break;
                Thread.Sleep(6);
            }

            var stats = host.Stats;
            Console.WriteLine($"     渲染 {frames} 帧后收敛={stats.Converged}，末帧 {stats.ElapsedMs:F1}ms");

            Thread.Sleep(400);   // 等 DWM 合成最后一帧

            var after = sampler.CapturePadded(shotX, shotY, shotW, shotH);
            PngWriter.Write(Path.Combine(outputDir, $"{tag}.png"), after);

            // 玻璃区域特写（放大 2 倍便于看边缘折射）
            var cropY = (int)(host.GlassBounds.Y - shotY) - 20;
            var cropX = (int)Math.Max(0, host.GlassBounds.X - shotX);
            var cropW = Math.Min(shotW - cropX, 700);
            var cropH = Math.Min(shotH - cropY, (int)host.GlassBounds.Height + 40);
            var crop = CropRegion(after, cropX, cropY, cropW, cropH);
            PngWriter.Write(Path.Combine(outputDir, $"{tag}-crop.png"), Magnify(crop, 2));

            return MeanDiff(before, after);
        }
        finally
        {
            host.Dispose();   // 内部会还原 Accent
            Thread.Sleep(300);
            Console.WriteLine("     已还原任务栏原生外观。");
        }
    }

    private static BgraFrame Magnify(BgraFrame source, int factor)
    {
        var result = new BgraFrame(source.Width * factor, source.Height * factor);
        for (var y = 0; y < result.Height; y++)
        {
            var sy = y / factor;
            for (var x = 0; x < result.Width; x++)
            {
                var sx = x / factor;
                var si = sy * source.Stride + sx * 4;
                var di = y * result.Stride + x * 4;
                result.Pixels[di] = source.Pixels[si];
                result.Pixels[di + 1] = source.Pixels[si + 1];
                result.Pixels[di + 2] = source.Pixels[si + 2];
                result.Pixels[di + 3] = 255;
            }
        }
        return result;
    }

    private static BgraFrame CropRegion(BgraFrame source, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Math.Max(0, source.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, source.Height - 1));
        width = Math.Clamp(width, 1, source.Width - x);
        height = Math.Clamp(height, 1, source.Height - y);

        var result = new BgraFrame(width, height);
        for (var row = 0; row < height; row++)
        {
            Array.Copy(source.Pixels, (y + row) * source.Stride + x * 4,
                result.Pixels, row * result.Stride, width * 4);
        }
        return result;
    }

    private static double MeanDiff(BgraFrame a, BgraFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return -1;
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
            sum += Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]);
            sum += Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
        }
        return sum / (double)(a.Pixels.Length / 4 * 3);
    }

    // ------------------------------------------------- 实时渲染端到端自检

    /// <summary>
    /// 验证"玻璃是不是真的在<b>实时</b>跟着背后的画面走"。
    ///
    /// <para>做法：在玻璃背后放一块会切换图案的高对比窗口，每换一次就
    /// <b>导出玻璃层本身</b>并比对像素。之所以导出玻璃层而不是屏幕截图，
    /// 是因为正常运行开启了"排除自身捕获"，截图里根本看不到玻璃 ——
    /// 那是为了让玻璃不采到自己而必须付的代价。导出渲染管线的真实输出
    /// 既能证明它变了，又不破坏生产配置。</para>
    ///
    /// <para>同时打印场景变化计数与最终画质档位，作为第二重证据。</para>
    /// </summary>
    private static int RunRealtimeVerification(string outputDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 实时渲染自检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine("验证：玻璃背后的画面变了，玻璃会不会跟着重画（而不是渲染一次就冻住）");
        Console.WriteLine();
        Console.WriteLine("⚠ 系统任务栏会在这段时间内被隐藏，结束时自动还原。");
        Console.WriteLine();

        var monitorHandle = DisplayEnvironment.GetPrimaryMonitor();
        var baselineWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);

        var settings = new ReplacementTaskbarSettings
        {
            Height = 58,
            ItemWidth = 60,
            IconSize = 26,
            LabelFontSize = 10,
            // 隔离掉性能波动，让这个自检只回答"实时性"这一个问题。
            ForcedQualityTier = QualityTier.High,
            // 与生产配置一致：玻璃会出现在截图里（不再自排除）。
            ExcludeFromCapture = false,
            MouseParallax = false,
        };

        var service = TaskbarReplacementService.Start(
            settings,
            new TaskbarReplacementOptions { HideSystemTaskbar = true, ReleaseWorkArea = true },
            s => Console.WriteLine($"     {s}"));

        if (service is null)
        {
            Console.WriteLine("  ✗ 替换任务栏启动失败。");
            return 3;
        }

        var steps = new List<(string Label, BgraFrame Glass, int SceneChanges)>();

        try
        {
            Console.WriteLine();
            Console.WriteLine("  ① 等玻璃收敛…");
            PumpFor(service, TimeSpan.FromSeconds(2.5));
            Console.WriteLine($"     画质档位：{service.QualityDescription}");

            BgraFrame Snapshot(string label)
            {
                var glass = service.SnapshotGlassLayer();
                if (glass is null) throw new InvalidOperationException("玻璃层尚未生成。");
                steps.Add((label, glass, service.SceneChangeCount));
                PngWriter.Write(Path.Combine(outputDir, $"{steps.Count:D2}-{label}.png"), glass, forceAlpha: true);
                return glass;
            }

            Snapshot("baseline");

            // 探测窗口放在玻璃背后：普通窗口、非置顶，因此永远在替换任务栏下面。
            var panel = service.PanelBounds;
            var probeHeight = panel.Height + 120 + 20;
            var probeTop = panel.Y - 120;

            Console.WriteLine();
            Console.WriteLine("  ② 在玻璃背后放一块高对比图案…");
            using var probe = new SceneProbeWindow(panel.X, probeTop, panel.Width, probeHeight);
            probe.SetPattern(0);
            PumpFor(service, TimeSpan.FromSeconds(1.8));
            Snapshot("probe-pattern-0");

            Console.WriteLine("  ③ 切换图案 1…");
            probe.SetPattern(1);
            PumpFor(service, TimeSpan.FromSeconds(1.8));
            Snapshot("probe-pattern-1");

            Console.WriteLine("  ④ 切换图案 2…");
            probe.SetPattern(2);
            PumpFor(service, TimeSpan.FromSeconds(1.8));
            Snapshot("probe-pattern-2");

            Console.WriteLine("  ⑤ 关掉图案窗口，恢复成桌面…");
            probe.Dispose();
            PumpFor(service, TimeSpan.FromSeconds(2.2));
            Snapshot("probe-closed");

            // ---- 结论 ----
            Console.WriteLine();
            Console.WriteLine("  ── 玻璃层逐次对比 ──");
            Console.WriteLine($"     {"阶段",-22}{"与上一阶段平均像素差",-24}场景变化计数");
            Console.WriteLine($"     {new string('-', 62)}");

            var allChanged = true;
            for (var i = 1; i < steps.Count; i++)
            {
                var diff = MeanDiff(steps[i - 1].Glass, steps[i].Glass);
                var changed = diff > 2.0;
                if (!changed) allChanged = false;

                Console.WriteLine($"     {steps[i].Label,-22}{diff,-24:F2}{steps[i].SceneChanges}"
                    + (changed ? "  ✓ 已重画" : "  ✗ 没变化"));
            }

            Console.WriteLine();
            if (allChanged)
            {
                Console.WriteLine("  ✓ 实时渲染正常工作：玻璃背后的画面每变一次，玻璃就重画一次。");
                Console.WriteLine($"     共检测到 {service.SceneChangeCount} 次场景变化。");
            }
            else
            {
                Console.WriteLine("  ⚠ 有阶段没检测到变化。可能是场景指纹的量化阈值偏高，");
                Console.WriteLine("     或者变化检测 / 重画链路有一环没接上。");
            }

            Console.WriteLine();
            Console.WriteLine($"  ⑥ 还原前的画质档位：{service.QualityDescription}");
        }
        finally
        {
            service.Dispose();
            Thread.Sleep(900);
            var restoredWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);
            Console.WriteLine();
            Console.WriteLine($"  ⑦ 已还原：工作区 {restoredWork}"
                + (restoredWork == baselineWork ? "（与基线一致 ✓）" : "（与基线不一致 ⚠）"));
            Console.WriteLine($"     系统任务栏可见：{TaskbarVisibilityController.FindTaskbarWindows().Any(Win32Query.IsVisible)}");
        }

        Console.WriteLine();
        Console.WriteLine($"玻璃层截图目录：{Path.GetFullPath(outputDir)}");
        return 0;
    }

    // ------------------------------------------------- 动态同步端到端自检

    /// <summary>
    /// 验证用户最关心的那条需求：<b>应用开关时，替换任务栏要实时跟着变</b>。
    ///
    /// <para>流程：启动替换任务栏 → 记录基线 → 启动一个记事本 →
    /// 观察任务栏是否多出对应项 → 关掉记事本 → 观察是否移除。</para>
    ///
    /// <para>同时抓三张截图，肉眼可核对图标与文字是否真的出现/消失。</para>
    /// </summary>
    private static int RunDynamicVerification(string outputDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 替换任务栏「动态同步」自检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine("验证：新开应用 → 任务栏出现图标+文字；关闭应用 → 任务栏移除对应项");
        Console.WriteLine();
        Console.WriteLine("⚠ 系统任务栏会在这段时间内被隐藏，结束时自动还原。");
        Console.WriteLine();

        var monitorHandle = DisplayEnvironment.GetPrimaryMonitor();
        var monitorBounds = DisplayEnvironment.GetMonitorBounds(monitorHandle);
        var baselineWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);

        // 自检截图必须**包含分层窗口**，否则拍不到我们自己的覆盖层，
        // 只拍到它背后的窗口 —— 据此判断"菜单被遮挡"会得出完全相反的结论。
        using var sampler = new ScreenSampler { IncludeLayeredWindows = true };

        var settings = new ReplacementTaskbarSettings
        {
            Height = 58,
            ItemWidth = 60,
            IconSize = 26,
            LabelFontSize = 10,
            ExcludeFromCapture = false,   // 自检要能拍到玻璃
            MouseParallax = false,
        };

        var service = TaskbarReplacementService.Start(
            settings,
            new TaskbarReplacementOptions { HideSystemTaskbar = true, ReleaseWorkArea = true },
            s => Console.WriteLine($"     {s}"));

        if (service is null)
        {
            Console.WriteLine("  ✗ 替换任务栏启动失败。");
            return 3;
        }

        var results = new List<(string Phase, int Count, int PanelWidth)>();
        Process? notepad = null;

        try
        {
            Console.WriteLine();
            Console.WriteLine("  ① 建立基线…");
            PumpFor(service, TimeSpan.FromSeconds(1.2));

            var before = new { Count = service.AppCount, Width = service.PanelBounds.Width };
            results.Add(("启动前", before.Count, before.Width));
            Console.WriteLine($"     应用数 {before.Count}，面板宽 {before.Width}px");
            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "01-baseline.png"));

            // ---- 启动一个记事本 ----
            Console.WriteLine();
            Console.WriteLine("  ② 启动记事本…");
            try
            {
                notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     启动失败：{ex.Message}");
            }

            PumpFor(service, TimeSpan.FromSeconds(4));
            var afterLaunch = new { Count = service.AppCount, Width = service.PanelBounds.Width };
            results.Add(("新开应用后", afterLaunch.Count, afterLaunch.Width));
            Console.WriteLine($"     应用数 {afterLaunch.Count}，面板宽 {afterLaunch.Width}px");
            Console.WriteLine($"     {DescribeDelta(before.Count, before.Width, afterLaunch.Count, afterLaunch.Width)}");
            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "02-after-launch.png"));

            // ---- 关掉记事本 ----
            Console.WriteLine();
            Console.WriteLine("  ③ 关闭记事本…");
            CloseNotepad(notepad);
            PumpFor(service, TimeSpan.FromSeconds(4));

            var afterClose = new { Count = service.AppCount, Width = service.PanelBounds.Width };
            results.Add(("关闭应用后", afterClose.Count, afterClose.Width));
            Console.WriteLine($"     应用数 {afterClose.Count}，面板宽 {afterClose.Width}px");
            Console.WriteLine($"     {DescribeDelta(afterLaunch.Count, afterLaunch.Width, afterClose.Count, afterClose.Width)}");
            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "03-after-close.png"));

            // ---- 结论 ----
            Console.WriteLine();
            Console.WriteLine("  ── 结果 ──");
            Console.WriteLine($"     {"阶段",-16}{"应用数",-10}面板宽度");
            Console.WriteLine($"     {new string('-', 42)}");
            foreach (var (phase, count, width) in results)
            {
                Console.WriteLine($"     {phase,-16}{count,-10}{width}px");
            }

            var grew = afterLaunch.Count > before.Count && afterLaunch.Width > before.Width;
            var shrank = afterClose.Count < afterLaunch.Count && afterClose.Width < afterLaunch.Width;

            Console.WriteLine();
            if (grew && shrank)
            {
                Console.WriteLine("  ✓ 动态同步正常：新开应用 → 任务栏增加图标与文字且变长；");
                Console.WriteLine("                    关闭应用 → 任务栏移除该项且缩短。高度始终不变。");
            }
            else if (!grew)
            {
                Console.WriteLine("  ⚠ 新开应用后任务栏没有增加项。可能原因：");
                Console.WriteLine("     · 记事本窗口还没出现在枚举里（尝试调大等待时间）");
                Console.WriteLine("     · 该窗口被过滤规则排除了（跑 probe --recon 看逐条判定）");
            }
            else
            {
                Console.WriteLine("  ⚠ 关闭应用后任务栏没有移除项。可能原因：");
                Console.WriteLine("     · 记事本进程没有真正退出（弹了保存对话框之类）");
                Console.WriteLine("     · DWM cloaked 检测未生效，残留了幽灵窗口");
            }
        }
        finally
        {
            CloseNotepad(notepad);
            service.Dispose();
            Thread.Sleep(900);

            var restoredWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);
            Console.WriteLine();
            Console.WriteLine($"  ④ 已还原：工作区 {restoredWork}"
                + (restoredWork == baselineWork ? "（与基线一致 ✓）" : "（与基线不一致 ⚠）"));
            Console.WriteLine($"     系统任务栏可见：{TaskbarVisibilityController.FindTaskbarWindows().Any(Win32Query.IsVisible)}");
        }

        Console.WriteLine();
        Console.WriteLine($"截图目录：{Path.GetFullPath(outputDir)}");
        return 0;
    }

    private static void PumpFor(TaskbarReplacementService service, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            service.Tick();
            Thread.Sleep(33);
        }
    }

    private static string DescribeDelta(int beforeCount, int beforeWidth, int afterCount, int afterWidth)
    {
        var dc = afterCount - beforeCount;
        var dw = afterWidth - beforeWidth;
        var sign = dc >= 0 ? "+" : "";
        return $"变化：应用数 {sign}{dc}，面板宽 {sign}{dw}px"
            + (dw == 0 && dc == 0 ? "（无变化）" : "");
    }

    /// <summary>
    /// 关闭记事本。
    ///
    /// <para>⚠️ 不能用 <c>Process.CloseMainWindow()</c>：Windows 11 的记事本是打包应用，
    /// <c>Process.Start("notepad.exe")</c> 拿到的是启动器，真正的窗口属于另一个进程，
    /// 那个调用等于打空炮 —— 这是本自检第一版失败的真实原因。</para>
    ///
    /// <para>所以改成走我们自己的 <see cref="ShellActions"/>：按可执行文件路径找到
    /// 任务栏上的那个应用，对它的每个窗口发 WM_CLOSE。这同时也顺带验证了
    /// 右键菜单里"关闭窗口"走的是同一条代码路径。</para>
    /// </summary>
    private static void CloseNotepad(Process? notepad)
    {
        var exePath = TryGetProcessPath(notepad);

        // ① 按路径找到任务栏上的那个应用，正常关闭它的窗口
        try
        {
            var apps = ShellWindowEnumerator.EnumerateApps();
            var target = exePath is null
                ? apps.FirstOrDefault(a => a.DisplayName.Contains("Notepad", StringComparison.OrdinalIgnoreCase)
                                        || a.DisplayName.Contains("记事本", StringComparison.Ordinal))
                : apps.FirstOrDefault(a => string.Equals(a.ProcessPath, exePath, StringComparison.OrdinalIgnoreCase));

            if (target is not null)
            {
                var closed = ShellActions.CloseAllWindows(target);
                Console.WriteLine($"     已向 {target.DisplayName} 的 {closed} 个窗口发送关闭请求。");
            }
        }
        catch
        {
            // 落到下面的强杀兜底。
        }

        // ② 等窗口自己消失
        for (var i = 0; i < 30; i++)
        {
            if (!AnyNotepadWindow()) return;
            Thread.Sleep(100);
        }

        // ③ 兜底：强杀。自检不能因为应用不听话就卡住。
        try
        {
            if (notepad is { HasExited: false })
            {
                notepad.Kill(entireProcessTree: true);
                notepad.WaitForExit(2000);
            }
        }
        catch { }

        try
        {
            var kill = new ProcessStartInfo("taskkill", "/F /IM Notepad.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(kill)?.WaitForExit(3000);
        }
        catch { }
    }

    private static string? TryGetProcessPath(Process? process)
    {
        try { return process?.MainModule?.FileName; }
        catch { return null; }
    }

    private static bool AnyNotepadWindow() =>
        ShellWindowEnumerator.EnumerateApps().Any(a =>
            a.ProcessPath is not null &&
            Path.GetFileName(a.ProcessPath).StartsWith("notepad", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------- 替换任务栏端到端自检

    /// <summary>
    /// 替换任务栏的端到端自检：真实隐藏系统任务栏 → 建替换任务栏 → 渲染 → 截图 → 完整还原。
    ///
    /// <para>它会短暂接管你的任务栏（约 12 秒），结束后<b>无条件还原</b>。
    /// 任何异常路径都走 <c>finally</c>，且所有改动都是会话级的。</para>
    /// </summary>
    private static int RunTaskbarVerification(string outputDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 替换任务栏端到端自检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine($"输出目录：{Path.GetFullPath(outputDir)}");
        Console.WriteLine();
        Console.WriteLine("⚠ 接下来约 12 秒内系统任务栏会被隐藏，结束时自动还原。");
        Console.WriteLine("  如果想中途放弃，直接关掉这个窗口即可（Ctrl+C）。");
        Console.WriteLine();

        var monitorHandle = DisplayEnvironment.GetPrimaryMonitor();
        var monitorBounds = DisplayEnvironment.GetMonitorBounds(monitorHandle);
        var baselineWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);

        Console.WriteLine($"  显示器      {monitorBounds}");
        Console.WriteLine($"  工作区基线  {baselineWork}");
        Console.WriteLine();

        // 自检截图必须**包含分层窗口**，否则拍不到我们自己的覆盖层，
        // 只拍到它背后的窗口 —— 据此判断"菜单被遮挡"会得出完全相反的结论。
        using var sampler = new ScreenSampler { IncludeLayeredWindows = true };

        SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "01-before.png"));
        Console.WriteLine("  ① 已保存接管前截图 01-before.png");

        // 刻意与生产默认完全一致，这样自检就能回答"用户实际会看到什么"。
        var settings = new ReplacementTaskbarSettings
        {
            ZOrder = TaskbarZOrder.DesktopBottom,   // 桌面之上、窗口之下
            Height = 116,                           // v2.2 起尺寸翻倍
            ItemWidth = 120,
            IconSize = 52,
            LabelFontSize = 20,
            ShowStartButton = true,
            ShowClock = true,
            ShowQuickSettings = true,
            CenterHorizontally = true,
            ExcludeFromCapture = false,   // 生产默认：截图能看到玻璃
            MouseParallax = false,
            // 生产默认：按"整条玻璃的高度"预留工作区，
            // 于是最大化窗口停在任务栏上方 —— 任务栏永远可见且不遮挡窗口。
            ReserveWorkAreaHeight = 116 + 16,
        };

        var options = new TaskbarReplacementOptions
        {
            HideSystemTaskbar = true,
            ReleaseWorkArea = false,   // 预留模式下不再释放工作区（二者互斥）
        };

        var service = TaskbarReplacementService.Start(settings, options, s => Console.WriteLine($"     {s}"));
        if (service is null)
        {
            Console.WriteLine("  ✗ 替换任务栏启动失败。");
            return 3;
        }

        try
        {
            Console.WriteLine();
            Console.WriteLine("  ② 已接管，渲染中…");
            var deadline = DateTime.UtcNow.AddSeconds(9);
            var ticks = 0;
            while (DateTime.UtcNow < deadline)
            {
                service.Tick();
                ticks++;
                Thread.Sleep(33);
            }

            var stats = service.Stats;
            var work = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);
            Console.WriteLine($"     共 {ticks} 帧；玻璃末帧 {stats.ElapsedMs:F1}ms，收敛={stats.Converged}");
            Console.WriteLine($"     面板 {service.PanelBounds}");
            Console.WriteLine($"     应用数 {service.AppCount}");
            var workNote = service.WorkAreaReserved
                ? "（已按任务栏高度预留 ✓ —— 最大化窗口不会越过它）"
                : work.Bottom >= monitorBounds.Bottom - 2
                    ? "（已释放为整屏 ✓）"
                    : "（未预留也未释放 ⚠）";
            Console.WriteLine($"     工作区 {work}{workNote}");

            Thread.Sleep(300);
            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "02-replaced.png"));
            SaveBottomStrip(sampler, monitorBounds, Path.Combine(outputDir, "03-taskbar-zoom.png"),
                service.PanelBounds);
            Console.WriteLine("  ③ 已保存接管后截图 02-replaced.png / 03-taskbar-zoom.png");

            var before = LoadOrNull(Path.Combine(outputDir, "01-before.png"));
            _ = before;
            Console.WriteLine();
            Console.WriteLine("     提示：截图里玻璃是**可见**的（默认不开「抓屏自排除」）。");
            Console.WriteLine("           02-replaced.png 看它和窗口的层级关系；");
            Console.WriteLine("           03-taskbar-zoom.png 是底部长条的特写。");
        }
        finally
        {
            service.Dispose();
            Thread.Sleep(900);

            var restoredWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);
            Console.WriteLine();
            Console.WriteLine($"  ④ 已还原：工作区 {restoredWork}"
                + (restoredWork == baselineWork ? "（与基线一致 ✓）" : "（与基线不一致 ⚠）"));
            var trayVisible = TaskbarVisibilityController.FindTaskbarWindows()
                .Any(Win32Query.IsVisible);
            Console.WriteLine($"     系统任务栏可见：{trayVisible}");

            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "04-restored.png"));
        }

        Console.WriteLine();
        Console.WriteLine($"截图目录：{Path.GetFullPath(outputDir)}");
        return 0;
    }

    /// <summary>
    /// 系统托盘读取自检。
    ///
    /// <para>托盘图标要跨进程从 explorer 的工具栏里读，容易受版本/权限影响，
    /// 所以单独给一个<b>不需要接管任务栏</b>的入口 —— 改一行就能验一次，迭代快。</para>
    /// </summary>
    private static int RunTrayProbe()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 系统托盘读取自检");
        Console.WriteLine(new string('─', 66));

        var icons = TrayIconReader.Read(22);
        Console.WriteLine($"读到 {icons.Count} 个托盘图标：");
        Console.WriteLine();

        for (var i = 0; i < icons.Count; i++)
        {
            var item = icons[i];
            var size = item.Icon is null ? "（无图标）" : $"{item.Icon.Width}x{item.Icon.Height}";
            Console.WriteLine($"  [{i,2}] 图标 {size,-10} id={item.IconId,-4} "
                + $"owner=0x{item.OwnerWindow:X}  提示=「{item.Tooltip}」");
        }

        Console.WriteLine();
        Console.WriteLine(icons.Count == 0
            ? "⚠ 一个都没读到 —— 可能这台机器的托盘结构与预期不同。"
            : "✓ 读取成功");
        return 0;
    }

    /// <summary>
    /// 自研「液态玻璃开始菜单」的端到端自检。
    ///
    /// <para>为什么要单独一个自检：开始菜单是本项目里<b>唯一一块面积大、又必须可交互</b>
    /// 的玻璃。它的失败方式（不显示、不响应点击、折射采到自己形成条纹）
    /// 都只能靠实机画面看出来，日志不会报错。</para>
    /// </summary>
    private static int RunStartMenuVerification(string outputDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 液态玻璃开始菜单自检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine($"输出目录：{Path.GetFullPath(outputDir)}");
        Console.WriteLine();
        Console.WriteLine("⚠ 自检期间系统任务栏会被隐藏，结束时自动还原。");
        Console.WriteLine();

        var monitorHandle = DisplayEnvironment.GetPrimaryMonitor();
        var monitorBounds = DisplayEnvironment.GetMonitorBounds(monitorHandle);
        var baselineWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);

        Console.WriteLine($"  显示器      {monitorBounds}");

        // 自检截图必须**包含分层窗口**，否则拍不到我们自己的覆盖层，
        // 只拍到它背后的窗口 —— 据此判断"菜单被遮挡"会得出完全相反的结论。
        using var sampler = new ScreenSampler { IncludeLayeredWindows = true };
        SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "01-before.png"));
        Console.WriteLine("  ① 已保存接管前截图 01-before.png");

        var settings = new ReplacementTaskbarSettings
        {
            ZOrder = TaskbarZOrder.DesktopBottom,
            Height = 116,
            ItemWidth = 120,
            IconSize = 52,
            LabelFontSize = 20,
            ShowStartButton = true,
            ShowClock = true,
            ShowQuickSettings = true,
            CenterHorizontally = true,
            ExcludeFromCapture = false,
            MouseParallax = false,
            ReserveWorkAreaHeight = 116 + 16,
            UseGlassStartMenu = true,   // ← 本自检的主角
        };

        var service = TaskbarReplacementService.Start(settings,
            new TaskbarReplacementOptions { HideSystemTaskbar = true, ReleaseWorkArea = false },
            s => Console.WriteLine($"     {s}"));

        if (service is null)
        {
            Console.WriteLine("  ✗ 替换任务栏启动失败。");
            return 3;
        }

        try
        {
            Console.WriteLine();
            Console.WriteLine("  ② 任务栏已接管，先把玻璃条渲染到收敛…");
            Pump(service, 4.0);

            Console.WriteLine("  ③ 打开「开始」…");
            service.ToggleStartMenu();
            Console.WriteLine($"     打开状态 = {service.StartMenuOpen}，面板 = {service.StartMenuBounds}");

            // 菜单要累积到收敛（默认 32 帧），同时把图标与文字画完。
            Pump(service, 4.0);

            // ⚠️ 自检期间用户很可能正在用这台电脑。任务栏的指针轮询会把
            // "鼠标点在开始按钮上"当成一次 toggle —— 于是菜单在等待期间被**外部输入关掉**。
            // 那样后面拍的"全屏"图里根本没有菜单，拿它去和合成帧比对会得出
            // "菜单没显示/被遮挡"这种完全错误的结论（本项目已经因此误判过两次）。
            // 所以这里先确认菜单还开着，被关掉就重新打开。
            if (!service.StartMenuOpen)
            {
                Console.WriteLine("  ⚠ 菜单在等待期间被外部输入关掉了，重新打开（否则截图里没有菜单）。");
                service.ToggleStartMenu();
                Pump(service, 2.0);
            }

            var bounds = service.StartMenuBounds;
            if (bounds.IsEmpty)
            {
                Console.WriteLine("  ⚠ 面板矩形为空 —— 多半是这台机器没有「固定到任务栏」的应用。");
            }
            else
            {
                SaveRegion(sampler, bounds, Path.Combine(outputDir, "02-startmenu.png"), 40);
                Console.WriteLine($"  ④ 已保存 02-startmenu.png（面板特写，{bounds.Width}x{bounds.Height}）");
            }

            SaveOverview(sampler, monitorBounds, Path.Combine(outputDir, "03-full.png"));
            Console.WriteLine("     已保存 03-full.png（全屏，看它与任务栏/窗口的层级）");

            // 分层取证：玻璃层与合成层分开导出，才能判断异常出在光学还是出在图标绘制。
            if (service.SnapshotStartMenuGlass() is { } glassLayer)
            {
                PngWriter.Write(Path.Combine(outputDir, "04-glass-layer.png"), glassLayer);
                Console.WriteLine($"     已保存 04-glass-layer.png（纯玻璃层 {glassLayer.Width}x{glassLayer.Height}）");
            }
            if (service.SnapshotStartMenuComposite() is { } composite)
            {
                PngWriter.Write(Path.Combine(outputDir, "05-composite.png"), composite);
                Console.WriteLine($"     已保存 05-composite.png（含图标的合成层 {composite.Width}x{composite.Height}）");
            }

            // ── 重影的**决定性**量化 ──
            //
            // 前面几张图只能"看"，看不出"到底压掉了多少"。这里用同一张真实桌面
            // 各渲染一次：一次走正常路径，一次把 FrostedBlurRadius 强制归零
            // （退化成点采样，也就是修复前的行为），然后比较玻璃区内的
            // 结构残余能量。
            //
            // 这是对用户第一条反馈「开始菜单有重影」唯一可比的量化证据 ——
            // 同一背景、同一版式，只差一个模糊参数。
            ReportGhostResidual(service, outputDir, display: s => Console.WriteLine("     " + s));

            Console.WriteLine("  ⑤ 再点一次「开始」应当关闭…");
            service.ToggleStartMenu();
            Pump(service, 1.0);
            Console.WriteLine($"     关闭状态 = {service.StartMenuOpen}"
                + (service.StartMenuOpen ? " ⚠ 没关掉" : " ✓"));

            // 压力测试：反复开关。
            //
            // 这一段是专门为"打开开始菜单会卡退"那个 bug 加的回归测试：
            // 当时 GlassStartMenu 每次打开都调 LayeredOverlayWindow.Create()，
            // 而 Create 每次都会新建一个分层窗口 + 分配一个 GCHandle，
            // 旧的既不解绑也不销毁 —— 每开一次泄漏一个窗口（吃 DWM 资源），
            // 攒够就把整个程序拖死。窗口计数是最直接的判据。
            // 压力测试：反复开关。
            //
            // 这一段是为"打开开始菜单会卡退"加的回归测试，而且**必须同时看窗口数与
            // GDI / USER / 句柄 / 私有内存** —— 上一轮只看了窗口数，于是漏掉了别的累积泄漏。
            // GDI 与 USER 各有每进程 10000 的上限，泄漏到顶就是静默卡退
            // （不产生崩溃转储、不写事件日志），窗口数完全正常。
            const int rounds = 100;
            Console.WriteLine($"  ⑥ 压力测试：反复开关 {rounds} 次，观察窗口数 / GDI / USER / 句柄 / 内存…");

            var windowsBefore = CountOverlayWindows();
            var resourcesBefore = ResourceProbe.Take();

            for (var i = 0; i < rounds; i++)
            {
                service.ToggleStartMenu();
                Pump(service, 0.06);
            }

            var windowsAfter = CountOverlayWindows();
            var resourcesAfter = ResourceProbe.Take();

            Console.WriteLine($"     覆盖层窗口数：{windowsBefore} → {windowsAfter}"
                + (windowsAfter > windowsBefore + 1
                    ? "  ⚠ 有泄漏！每开一次菜单就会多一个窗口"
                    : "  ✓ 没有泄漏（窗口被复用）"));
            Console.WriteLine($"     资源：{resourcesAfter.Diff(resourcesBefore)}");
            Console.WriteLine($"     程序仍然存活：{(service.IsRunning ? "是 ✓" : "否 ⚠")}");
        }
        finally
        {
            service.Dispose();
            Thread.Sleep(900);
        }

        var restoredWork = DisplayEnvironment.GetMonitorWorkArea(monitorHandle);
        Console.WriteLine();
        Console.WriteLine($"  ⑥ 已还原：工作区 {restoredWork}"
            + (restoredWork == baselineWork ? "（与基线一致 ✓）" : "（与基线不一致 ⚠）"));
        Console.WriteLine($"     系统任务栏可见：{TaskbarVisibilityController.FindTaskbarWindows().Any(Win32Query.IsVisible)}");
        Console.WriteLine();
        Console.WriteLine($"截图目录：{Path.GetFullPath(outputDir)}");
        return 0;
    }

    /// <summary>
    /// 统计本进程里 <c>LiquidGlassOverlayWnd</c> 类窗口的数量。
    ///
    /// <para>用来验证"覆盖层窗口有没有被复用" —— 窗口每次都被重新创建的话，
    /// 这个数字会随着开关次数一路涨上去。</para>
    /// </summary>
    private static int CountOverlayWindows() => LayeredOverlayWindow.CountLiveWindows();

    /// <summary>在给定秒数内持续驱动渲染循环。</summary>
    private static void Pump(TaskbarReplacementService service, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            service.Tick();
            Thread.Sleep(33);
        }
    }

    /// <summary>把屏幕上某块矩形（可留边距）存成 PNG。</summary>
    private static void SaveRegion(ScreenSampler sampler, Win32Rect rect, string path, int pad)
    {
        var frame = sampler.CapturePadded(
            rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);
        PngWriter.Write(path, frame);
    }

    /// <summary>把整屏缩到 1280 宽存下来，便于一眼看全局。</summary>
    private static void SaveOverview(ScreenSampler sampler, Win32Rect monitor, string path)
    {
        var full = sampler.CapturePadded(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        var scaled = full.Scaled(Math.Min(1280, full.Width), full.Height * Math.Min(1280, full.Width) / full.Width);
        PngWriter.Write(path, scaled);
    }

    /// <summary>
    /// 量化"重影"：拿同一张真实桌面，分别用「修复前」与「修复后」的雾化半径
    /// 渲染同一块玻璃，比较玻璃区内的结构残余能量。
    ///
    /// <para>这是对用户第一条反馈（开始菜单有重影）<b>唯一可比的量化证据</b> ——
    /// 背景、版式、材质全部相同，只差一个模糊参数。
    /// 光看单张截图只能凭肉眼说"好一点了"，给不出比例。</para>
    ///
    /// <para>度量口径：玻璃区内部每行<b>相邻像素亮度差的绝对值均值</b>，
    /// 也就是横向高频能量。背景的文字排版是典型的高频结构，
    /// 它透出多少，这个数就剩多少。</para>
    /// </summary>
    private static void ReportGhostResidual(
        TaskbarReplacementService service, string outputDir, Action<string> display)
    {
        display("── 重影量化：同一张桌面，只差一个模糊半径 ──");

        // 半径 0 = 退化成点采样，等价于"修复前"的旧行为。
        var before = service.RenderStartMenuGlassWithBlurRadius(0);
        var after = service.RenderStartMenuGlassWithBlurRadius(96);

        if (before is null || after is null)
        {
            display("玻璃层取不到（场景为空），跳过。");
            return;
        }

        if (before.Width != after.Width || before.Height != after.Height)
        {
            display($"两次渲染尺寸不一致（{before.Width}x{before.Height} vs "
                + $"{after.Width}x{after.Height}），跳过。");
            return;
        }

        PngWriter.Write(Path.Combine(outputDir, "06-ghost-before.png"), before);
        PngWriter.Write(Path.Combine(outputDir, "07-ghost-after.png"), after);

        // 取内部区域，避开边缘折射带（那一带本来就该有结构）。
        var x0 = (int)(before.Width * 0.15);
        var x1 = (int)(before.Width * 0.85);
        var y0 = (int)(before.Height * 0.15);
        var y1 = (int)(before.Height * 0.85);

        var hfBefore = HorizontalHighFrequency(before, x0, y0, x1, y1);
        var hfAfter = HorizontalHighFrequency(after, x0, y0, x1, y1);

        display($"玻璃画布 {before.Width}x{before.Height}，度量区 "
            + $"({x0},{y0})-({x1},{y1})");
        display($"  修复前（半径 0，点采样）结构能量 {hfBefore:F3}");
        display($"  修复后（半径 96，金字塔）结构能量 {hfAfter:F3}");

        if (hfBefore <= 0.01)
        {
            display("  背景太平坦，量不出比例（换张有文字的桌面再测）。");
            return;
        }

        var ratio = hfAfter / hfBefore;
        display($"  → 残余 {ratio:P1}，抹除 {1 - ratio:P1}，"
            + $"下降 {(ratio > 0.001 ? 1 / ratio : 0):F2} 倍");

        if (ratio < 0.5)
        {
            display($"  ✓ 结构残余低于 50%，重影已显著压掉");
        }
        else
        {
            display($"  × 结构残余仍有 {ratio:P1}，改善不足");
        }
        display($"  对照图：06-ghost-before.png / 07-ghost-after.png");
    }

    /// <summary>区域内横向相邻像素亮度差的绝对值均值（结构能量）。</summary>
    private static double HorizontalHighFrequency(BgraFrame frame, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(frame.Width, x1); y1 = Math.Min(frame.Height, y1);
        if (x1 - x0 < 2 || y1 - y0 < 2) return 0;

        long total = 0;
        long n = 0;
        var stride = frame.Stride;
        var px = frame.Pixels;

        for (var y = y0; y < y1; y++)
        {
            var row = y * stride;
            var prev = Luma(px, row + x0 * 4);
            for (var x = x0 + 1; x < x1; x++)
            {
                var cur = Luma(px, row + x * 4);
                total += Math.Abs(cur - prev);
                n++;
                prev = cur;
            }
        }
        return n == 0 ? 0 : total / (double)n;
    }

    private static int Luma(byte[] px, int i) =>
        (px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) / 1000;

    /// <summary>只截任务栏那一条，并放大 2 倍，用来检查图标 / 文字 / 玻璃边缘。</summary>
    private static void SaveBottomStrip(ScreenSampler sampler, Win32Rect monitor, string path, Win32Rect panel)
    {
        var stripHeight = Math.Min(200, monitor.Height);
        var stripY = monitor.Bottom - stripHeight;

        var strip = sampler.CapturePadded(monitor.X, stripY, monitor.Width, stripHeight);

        // 以面板为中心裁一段 1000px 宽
        var centerX = panel.IsEmpty ? monitor.Width / 2 : panel.X + panel.Width / 2;
        var cropWidth = Math.Min(1100, monitor.Width);
        var cropX = Math.Clamp(centerX - cropWidth / 2, 0, monitor.Width - cropWidth);

        var crop = CropRegion(strip, cropX, 0, cropWidth, stripHeight);

        // 2 倍最近邻放大
        var magnified = new BgraFrame(crop.Width * 2, crop.Height * 2);
        for (var y = 0; y < magnified.Height; y++)
        {
            var sy = y / 2;
            for (var x = 0; x < magnified.Width; x++)
            {
                var sx = x / 2;
                var si = sy * crop.Stride + sx * 4;
                var di = y * magnified.Stride + x * 4;
                magnified.Pixels[di] = crop.Pixels[si];
                magnified.Pixels[di + 1] = crop.Pixels[si + 1];
                magnified.Pixels[di + 2] = crop.Pixels[si + 2];
                magnified.Pixels[di + 3] = 255;
            }
        }
        PngWriter.Write(path, magnified);
    }

    private static BgraFrame? LoadOrNull(string path)
    {
        _ = path;
        return null;   // 仅用于占位，当前不做前后对比
    }

    // ------------------------------------------------------------ 命令行

    private static string ResolveConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c") return Path.GetFullPath(args[i + 1]);
        }
        return AppConfig.DefaultPath;
    }

    private static void PrintHelp()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("""
            LiquidGlass —— 把液态玻璃效果应用到 Windows 系统界面

            用法：
              liquidglass                正常启动，托盘常驻并接管任务栏
              liquidglass --no-attach    只驻留托盘，不接管（先用菜单确认环境）
              liquidglass --diagnose     环境体检：枚举系统界面并实测透明化支持度
              liquidglass --config <路径> 使用指定配置文件
              liquidglass --help         显示本帮助

            配置文件（首次运行自动生成）：
              %APPDATA%\LiquidGlass\liquidglass.json

            日志：
              %LOCALAPPDATA%\LiquidGlass\liquidglass.log

            想先看看玻璃长什么样，不需要接管系统：
              liquidglass-preview.exe
            """);
    }

    /// <summary>环境体检：不修改任何系统外观（除了探针里显式说明的 Accent 实测）。</summary>
    private static int RunDiagnose(bool dpiOk)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine("LiquidGlass · 环境体检");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine($"  Windows          {Environment.OSVersion.Version}（Build {Environment.OSVersion.Version.Build}）");
        Console.WriteLine($"  64 位进程        {Environment.Is64BitProcess}");
        Console.WriteLine($"  DPI 感知         {DisplayEnvironment.GetDpiAwareness()}{(dpiOk ? "" : "   ⚠ 不足，高 DPI 下玻璃会错位")}");
        Console.WriteLine($".NET             {Environment.Version}");
        Console.WriteLine();

        Console.WriteLine("  显示器：");
        foreach (var m in DisplayEnvironment.EnumerateMonitors())
        {
            Console.WriteLine($"    {m}");
        }
        Console.WriteLine();

        var surfaces = SystemSurfaceLocator.Discover();
        Console.WriteLine($"  发现 {surfaces.Count} 个系统界面窗口：");
        foreach (var s in surfaces)
        {
            Console.WriteLine($"    {s.Kind,-18} hwnd=0x{s.Handle.ToInt64():X8}  {s.ProcessName,-26} {s.Bounds}");
        }
        Console.WriteLine();

        var taskbar = surfaces.FirstOrDefault(s => s.Kind == SurfaceKind.Taskbar);
        if (taskbar is null)
        {
            Console.WriteLine("  ✗ 没有找到 Shell_TrayWnd。若这是非交互式会话，属于正常现象。");
            return 2;
        }

        Console.WriteLine("  Accent 通道探测（只读，不改变外观）：");
        var readable = CompositionAttribute.TryRead(taskbar.Handle, out var state, out var color);
        Console.WriteLine(readable
            ? $"    可读取：state={state} color=0x{CompositionAttribute.AbgrToArgb(color):X8}"
            : "    读取失败（Win11 上属正常，不代表不可写）");

        Console.WriteLine();
        Console.WriteLine("  若要看透明化是否真的生效，请另行运行：");
        Console.WriteLine("    liquidglass-probe --test-accent --capture");
        Console.WriteLine();
        return 0;
    }
}
