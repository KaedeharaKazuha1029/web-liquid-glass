using System.Text;

namespace LiquidGlass.App;

/// <summary>
/// 简单日志。控制台 + 可选文件，线程安全。
/// 刻意不引入日志框架：这个程序的使用者要看的是
/// "任务栏有没有被接管成功、为什么没有"，而不是结构化日志。
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;

    public string? FilePath { get; }

    public Logger(bool writeFile)
    {
        if (!writeFile) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiquidGlass");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "liquidglass.log");

            // 每次运行截断，只保留本次会话的日志，避免无限增长。
            _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
            FilePath = null;
        }
    }

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        lock (_sync)
        {
            // WinExe 没有控制台，写控制台会静默无操作 —— 但用 dotnet run
            // 或者从终端启动时能看到，调试很有用。
            try { Console.WriteLine(line); } catch { /* 无控制台 */ }
            try { _writer?.WriteLine(line); } catch { /* 磁盘故障不该让程序崩 */ }
        }
    }

    public void Dispose()
    {
        lock (_sync) _writer?.Dispose();
    }
}
