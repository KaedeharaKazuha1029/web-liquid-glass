namespace LiquidGlass.Win32;

/// <summary>
/// <c>SetWindowCompositionAttribute</c> 的 <c>AccentState</c> 取值。
/// 这是未公开 API，取值来自社区逆向（与 Windows 10 1809+ 行为一致）。
/// </summary>
public enum AccentState
{
    /// <summary>关闭 Accent，回到系统原生外观。</summary>
    ACCENT_DISABLED = 0,
    /// <summary>纯色渐变填充，GradientColor 的 RGB 生效、Alpha 被忽略。</summary>
    ACCENT_ENABLE_GRADIENT = 1,
    /// <summary>透明渐变填充，GradientColor 的 Alpha 生效。</summary>
    ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
    /// <summary>高斯模糊（无噪点）。</summary>
    ACCENT_ENABLE_BLURBEHIND = 3,
    /// <summary>亚克力材质（模糊 + 噪点），Windows 10 1803 起。</summary>
    ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    /// <summary>宿主背景（Mica 系）。</summary>
    ACCENT_ENABLE_HOSTBACKDROP = 5,
    /// <summary>无效状态哨兵。</summary>
    ACCENT_INVALID_STATE = 6,
}
