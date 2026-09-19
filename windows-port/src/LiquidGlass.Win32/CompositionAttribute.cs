using System.Runtime.InteropServices;
using static LiquidGlass.Win32.NativeMethods;

namespace LiquidGlass.Win32;

/// <summary>
/// <c>SetWindowCompositionAttribute</c> 的托管封装。
/// 这是让系统组件（任务栏等）真正"变透明"的唯一入口——
/// 只有把原生背景抹掉，紧贴其下方的液态玻璃层才能被看见。
/// </summary>
public static class CompositionAttribute
{
    /// <summary>任务栏可用的背景模式。</summary>
    public enum BackdropMode
    {
        /// <summary>保留系统原生外观（不修改）。</summary>
        Native = 0,
        /// <summary>纯色替换，GradientColor 的 alpha 生效。</summary>
        Solid = AccentState.ACCENT_ENABLE_GRADIENT,
        /// <summary>
        /// 无模糊的透明渐变。alpha=0 时把原生背景完全抹掉，
        /// 是"背景接管"模式的首选：成本为零，且不引入系统模糊。
        /// </summary>
        Transparent = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT,
        /// <summary>系统高斯模糊（无噪点）。</summary>
        Blur = AccentState.ACCENT_ENABLE_BLURBEHIND,
        /// <summary>系统亚克力材质（含噪点，Win10 1803+）。</summary>
        Acrylic = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
        /// <summary>Win11 的 Mica / HostBackdrop。</summary>
        HostBackdrop = AccentState.ACCENT_ENABLE_HOSTBACKDROP,
    }

    /// <summary>应用结果，供探针与日志使用。</summary>
    public readonly record struct ApplyResult(
        bool Applied, int HResult, string Description, BackdropMode Mode, uint GradientColor);

    /// <summary>
    /// 给窗口设置背景策略。<paramref name="abgr"/> 为 0xAABBGGRR 布局。
    /// </summary>
    /// <remarks>
    /// 注意：Windows 11 对 <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> 与
    /// <c>HOSTBACKDROP</c> 在任务栏上的支持随版本波动；未被支持时系统会
    /// 静默忽略并保留原生外观，因此调用方必须用截图回归来确认实际效果，
    /// 不能只看返回值。探针 <c>LiquidGlass.Probe</c> 做的就是这件事。
    /// </remarks>
    public static ApplyResult Apply(IntPtr hwnd, BackdropMode mode, uint abgr = 0x00000000, int accentFlags = 0)
    {
        if (hwnd == IntPtr.Zero) return new ApplyResult(false, -1, "无效窗口句柄", mode, abgr);

        var accent = new ACCENTPOLICY
        {
            AccentState = (int)mode,
            AccentFlags = accentFlags,
            GradientColor = abgr,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<ACCENTPOLICY>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            var hr = SetWindowCompositionAttribute(hwnd, ref data);
            if (hr == 0)
            {
                // Win11 的 SetWindowCompositionAttribute 不通过 GetLastError 报错，
                // 返回 0 只代表"调用未抛异常"，不代表系统真的接受了这次修改。
                return new ApplyResult(false, 0, "调用返回 0（该版本可能已移除该入口）", mode, abgr);
            }
            return new ApplyResult(true, hr, "已下发", mode, abgr);
        }
        catch (Exception ex)
        {
            return new ApplyResult(false, ex.HResult, ex.Message, mode, abgr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 读取窗口当前的 Accent 策略。用于诊断（Win11 上常返回失败，属正常现象）。
    /// </summary>
    public static bool TryRead(IntPtr hwnd, out AccentState state, out uint gradientColor)
    {
        state = AccentState.ACCENT_DISABLED;
        gradientColor = 0;
        if (hwnd == IntPtr.Zero) return false;

        var size = Marshal.SizeOf<ACCENTPOLICY>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            if (!GetWindowCompositionAttribute(hwnd, ref data)) return false;
            var accent = Marshal.PtrToStructure<ACCENTPOLICY>(ptr);
            state = (AccentState)accent.AccentState;
            gradientColor = accent.GradientColor;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>把 0xAARRGGBB 形式的常见 ARGB 值转成 Accent API 需要的 0xAABBGGRR。</summary>
    public static uint ArgbToAbgr(uint argb)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>还原成 0xAARRGGBB，便于日志可读。</summary>
    public static uint AbgrToArgb(uint abgr)
    {
        var a = (abgr >> 24) & 0xFF;
        var b = (abgr >> 16) & 0xFF;
        var g = (abgr >> 8) & 0xFF;
        var r = abgr & 0xFF;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
