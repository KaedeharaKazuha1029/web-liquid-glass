namespace LiquidGlass.Tests;

/// <summary>
/// 极简断言框架。
/// 刻意不引入 xunit / NUnit：整个仓库的承诺是
/// "任何装了 .NET 10 的机器，dotnet build 之后就能跑测试"，
/// 多一个 NuGet 包就多一道离线环境会卡住的门。
/// </summary>
internal static class Check
{
    private static int _passed;
    private static int _failed;
    private static string _currentGroup = "";

    public static void Group(string name)
    {
        _currentGroup = name;
        Console.WriteLine();
        Console.WriteLine($"── {name} " + new string('─', Math.Max(0, 62 - name.Length)));
    }

    public static void True(bool condition, string description, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ✓ {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  ✗ {description}");
            if (detail is not null) Console.WriteLine($"      {detail}");
        }
    }

    public static void Equal(double expected, double actual, double tolerance, string description)
    {
        var delta = Math.Abs(expected - actual);
        True(delta <= tolerance, description,
            $"期望 {expected:G9}，实际 {actual:G9}，偏差 {delta:E3} > 容差 {tolerance:E3}");
    }

    public static void EqualBits(float expected, float actual, string description) =>
        True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            description,
            $"期望 {expected:R}（0x{BitConverter.SingleToInt32Bits(expected):X8}），" +
            $"实际 {actual:R}（0x{BitConverter.SingleToInt32Bits(actual):X8}）");

    public static int Summarize()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 68));
        if (_failed == 0)
        {
            Console.WriteLine($"全部通过：{_passed} 项断言");
        }
        else
        {
            Console.WriteLine($"失败 {_failed} 项 / 通过 {_passed} 项");
        }
        Console.WriteLine(new string('═', 68));
        return _failed == 0 ? 0 : 1;
    }
}
