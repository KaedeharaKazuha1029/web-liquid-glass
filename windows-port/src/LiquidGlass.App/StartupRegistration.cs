using Microsoft.Win32;

namespace LiquidGlass.App;

/// <summary>
/// 开机自启的注册表读写（HKCU\...\Run）。
///
/// 只用当前用户分支，不碰 HKLM：
/// 本程序是 asInvoker 的普通用户程序，写 HKLM 需要提权，
/// 而为了一个视觉特效去弹 UAC 是完全不合理的交换。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiquidGlass";

    /// <summary>当前是否已注册自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>切换自启状态。</summary>
    public static bool Toggle(out bool nowEnabled, out string detail)
    {
        nowEnabled = false;
        detail = "";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                detail = "无法打开 HKCU 的 Run 键。";
                return false;
            }

            if (IsEnabled())
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                nowEnabled = false;
                detail = "已从启动项移除。";
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                detail = "无法确定当前可执行文件路径。";
                return false;
            }

            // 用引号包裹以支持带空格的路径。
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            nowEnabled = true;
            detail = exe;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>移除自启项（卸载时用）。</summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 卸载流程不因为这一项失败而中断。
        }
    }
}
