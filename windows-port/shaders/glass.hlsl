// ============================================================================
//  glass.hlsl —— 液态玻璃光学核心的 HLSL (Shader Model 5.0) 实现
// ============================================================================
//
//  这是上游 web-liquid-glass 中 GLSL ES 3.00 trace fragment shader 的等价移植，
//  供希望走 GPU 路径的使用者接入 Direct3D 11 / 12。
//
//  ⚠️ 重要说明
//  ---------------------------------------------------------------------------
//  本仓库随附的应用程序 (LiquidGlass.App) 使用的是 **CPU 参考渲染器**
//  （LiquidGlass.Core/CpuGlassRenderer.cs），原因见 docs/ARCHITECTURE.md
//  "为什么默认走 CPU"。这份 HLSL 是给下面两种情况的：
//    · 你需要把累积帧数、路径数或分辨率推到远高于 CPU 能达到的水平；
//    · 你要把液态玻璃嵌进自己的 D3D11/12 渲染管线。
//  它没有随附 D3D11 宿主的 C# 绑定代码 —— 请按自己的管线接入，
//  uniform 与资源布局在下方 <常量缓冲> 一节里完整列出。
//
//  移植纪律：所有数值一律取自 LiquidGlass.Core/LiquidGlassMaterial.cs，
//  与上游 layout.js 的 LIQUID_GLASS_REFERENCE 逐字对应。若改动材质默认值，
//  必须同步修改本文件与 LiquidGlass.Tests 的材质契约测试。
//
//  数值类型：全部使用 float（对应 GLSL highp float）。
//  ⚠️ 不要把 float 改成 double —— HLSL 里没有 double 的等价精度语义，
//     而且上游的 hash 函数依赖 float 的舍入行为。
// ============================================================================

// ---------------------------------------------------------------- 常量缓冲

cbuffer GlassMaterial : register(b0)
{
    float4 uResolution;              // (画布宽, 画布高, 1/宽, 1/高)
    float4 uGlassRect;               // (x, y, w, h) 画布局部像素坐标
    float  uFrame;                   // 累积帧号
    float  uReset;                   // >0.5 表示丢弃历史
    float2 uMouse;                   // 归一化到玻璃矩形内的 [0,1]

    float  uReverseDisplacement;     // 2.19
    float  uEdgeCurvature;           // 0.67
    float  uOpticalThickness;        // 1.71
    float  uEdgeBandRatio;           // 0.26
    float  uRefractionVisibleRatio;  // 0.16
    float  uBlendFeather;            // 12
    float  uFrostedStrength;         // 0.29
    float  uFrostedAttenuation;      // 0.55
    float  uBlurSpacing;             // 1.4
    float  uDispersionStrength;      // 0.22
    float  uHighlightStrength;       // 0.055
    float  uTintMix;                 // 0.17
    float3 uTintColor;               // (0.035, 0.035, 0.045)
    float  uCornerRadius;            // <0 表示沿用上游自动规则
    float  uPadding0;
}

// 与上游 layout.js 中的 optics 段一致，编译期常量。
#define REFRACTIVE_RED     1.514
#define REFRACTIVE_GREEN   1.520
#define REFRACTIVE_BLUE    1.528
#define ROUGHNESS          0.014
#define CAMERA_RAY_SCALE   0.045
#define MINIMUM_RAY_Z      0.08
#define INSIDE_TRAVEL_BASE 6.0
#define INSIDE_INTERIOR_SCALE 0.28
#define EXIT_TRAVEL_BASE   6.5
#define REVERSE_MAX_PIXELS 36.0
#define LENS_MIX           0.35
#define TEXT_MASK_PROTECTION 0.025
#define HIGHLIGHT_A_WIDTH  0.22
#define HIGHLIGHT_A_CENTER 0.22
#define HIGHLIGHT_B_WIDTH  0.20
#define HIGHLIGHT_B_CENTER 1.78

// 上游 shaders.js 中硬编码在 GLSL 里的两个系数（非 uniform）
#define SURFACE_NORMAL_EDGE_BOOST 1.35
#define REFLECTION_BASE_WEIGHT    0.34

#define PI 3.141592653589793

#define FROST_TINT float3(0.055, 0.062, 0.075)

// ---------------------------------------------------------------- 资源绑定

Texture2D<float4>  uScene      : register(t0);   // 线性空间折射源 (RGBA16F 推荐)
Texture2D<float>   uTextMask   : register(t1);   // 文字保护遮罩 (R8)
Texture2D<float4>  uPrevious   : register(t2);   // 上一帧结果 (乒乓读写)
SamplerState       uSceneSampler : register(s0); // CLAMP / LINEAR

// ---------------------------------------------------------------- 基础工具

float saturateValue(float v) { return saturate(v); }

float smoothStep(float edge0, float edge1, float x)
{
    float t = saturate((x - edge0) / (edge1 - edge0));
    return t * t * (3.0 - 2.0 * t);
}

// 上游 hash12 的逐行等价实现。
// 注意 p0 与 p2 同源（fract(value.x * 0.1031)），这是上游的设计，不要"顺手修好"。
float hash12(float2 value)
{
    float3 p3 = frac(float3(value.x, value.y, value.x) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// 上游的 sRGB -> 线性。uScene 必须是线性空间纹理；
// 若你直接把 sRGB 纹理接进来，这里会转换一次（此时上面的场景纹理请用 R8G8B8A8_UNORM_SRGB = 否）。
float3 srgbToLinear(float3 c)
{
    return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c));
}

float3 sceneSample(float2 topPixel)
{
    float2 uv = float2(
        saturate(topPixel.x / uResolution.x),
        saturate(1.0 - topPixel.y / uResolution.y));
    return uScene.SampleLevel(uSceneSampler, uv, 0).rgb;
}

float maskSample(float2 topPixel)
{
    float2 uv = float2(
        saturate(topPixel.x / uResolution.x),
        saturate(1.0 - topPixel.y / uResolution.y));
    return uTextMask.SampleLevel(uSceneSampler, uv, 0).r;
}

// ---------------------------------------------------------------- 几何 SDF

float roundedRectSdf(float2 point, float2 halfSize, float radius)
{
    float2 q = abs(point) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// 圆角半径：uCornerRadius < 0 时沿用上游规则 max(1, min(halfW, halfH) - 1)。
float resolveRadius(float2 halfSize)
{
    float autoRadius = max(1.0, min(halfSize.x, halfSize.y) - 1.0);
    return uCornerRadius < 0.0 ? autoRadius : min(uCornerRadius, min(halfSize.x, halfSize.y));
}

float rectDistance(float2 pixel, float4 rect)
{
    float2 center   = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    return roundedRectSdf(pixel - center, halfSize, resolveRadius(halfSize));
}

float2 rectNormal(float2 pixel, float4 rect)
{
    float2 center   = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float  radius   = resolveRadius(halfSize);
    float2 local    = pixel - center;

    float dx = roundedRectSdf(local + float2(0.8, 0.0), halfSize, radius)
             - roundedRectSdf(local - float2(0.8, 0.0), halfSize, radius);
    float dy = roundedRectSdf(local + float2(0.0, 0.8), halfSize, radius)
             - roundedRectSdf(local - float2(0.0, 0.8), halfSize, radius);

    return normalize(float2(dx, dy) + float2(0.00001, 0.00001));
}

// ---------------------------------------------------------------- 雾化

// 上游桌面端 9 抽样高斯核（1 + 4 + 4），权重逐个对应。
float3 gaussianBlur(float2 topPixel, float spacing)
{
    float3 color = sceneSample(topPixel) * 0.204164;
    color += sceneSample(topPixel + float2(spacing, 0.0)) * 0.180174;
    color += sceneSample(topPixel - float2(spacing, 0.0)) * 0.180174;
    color += sceneSample(topPixel + float2(spacing * 2.0, 0.0)) * 0.123832;
    color += sceneSample(topPixel - float2(spacing * 2.0, 0.0)) * 0.123832;
    color += sceneSample(topPixel + float2(0.0, spacing)) * 0.066282;
    color += sceneSample(topPixel - float2(0.0, spacing)) * 0.066282;
    color += sceneSample(topPixel + float2(0.0, spacing * 2.0)) * 0.027631;
    color += sceneSample(topPixel - float2(0.0, spacing * 2.0)) * 0.027631;
    return color;
}

// ---------------------------------------------------------------- 折射

float2 refractedPosition(float2 pixel, float4 rect, float ior, float randomValue)
{
    float distanceToEdge = rectDistance(pixel, rect);
    float depth      = max(0.0, -distanceToEdge);
    float edgeBand   = max(5.0, rect.w * uEdgeBandRatio);
    float u          = saturate(depth / edgeBand);
    float smoothInterior = u * u * (3.0 - 2.0 * u);
    float edgeFactor = 1.0 - smoothInterior;
    float2 outlineNormal = rectNormal(pixel, rect);

    float2 roughOffset = float2(cos(randomValue * PI * 2.0), sin(randomValue * PI * 2.0)) * ROUGHNESS;

    float3 surfaceNormal = normalize(float3(
        outlineNormal * edgeFactor * SURFACE_NORMAL_EDGE_BOOST * uEdgeCurvature + roughOffset,
        1.0));

    float3 cameraRay = normalize(float3(
        (uMouse.x - 0.5) * CAMERA_RAY_SCALE,
        (uMouse.y - 0.5) * CAMERA_RAY_SCALE,
        -1.0));

    float3 insideRay = refract(cameraRay, surfaceNormal, 1.0 / ior);

    float thickness = uOpticalThickness *
        (INSIDE_TRAVEL_BASE + smoothInterior * rect.w * INSIDE_INTERIOR_SCALE);
    float insideTravel = thickness / max(MINIMUM_RAY_Z, -insideRay.z);
    float2 hit = pixel + insideRay.xy * insideTravel;

    float3 exitRay = refract(insideRay, float3(0.0, 0.0, 1.0), ior);
    float exitTravel = (EXIT_TRAVEL_BASE * uOpticalThickness) / max(MINIMUM_RAY_Z, -exitRay.z);
    hit += exitRay.xy * exitTravel;

    // 透镜曲线：只在边缘生效，中心保持清晰。
    float lensCurve = 1.0 - sqrt(max(0.0, 1.0 - edgeFactor * edgeFactor));
    float reversePixels = min(REVERSE_MAX_PIXELS, edgeBand * uReverseDisplacement);

    return pixel
         - outlineNormal * reversePixels * lensCurve
         + (hit - pixel) * lensCurve * LENS_MIX;
}

// 随机菲涅耳反射。
float3 fresnelReflection(float2 pixel, float4 rect)
{
    float2 normal = rectNormal(pixel, rect);
    float3 surface = normalize(float3(normal, 0.72));
    // normalize(float3(-0.58, -0.74, 0.62))
    float3 lightDir = float3(-0.51503104, -0.65711519, 0.55057279);

    float topLight = pow(saturate(dot(surface, lightDir)), 18.0);
    float intensity = 0.24 + topLight * 0.72;
    return float3(0.52, 0.58, 0.68) * intensity;
}

float3 tracePath(float2 pixel, float4 rect, float seed, float edgeFactor)
{
    float2 redPos   = refractedPosition(pixel, rect, REFRACTIVE_RED,   seed);
    float2 greenPos = refractedPosition(pixel, rect, REFRACTIVE_GREEN, seed);
    float2 bluePos  = refractedPosition(pixel, rect, REFRACTIVE_BLUE,  seed);

    float3 neutral  = sceneSample(greenPos);
    float3 dispersed = float3(sceneSample(redPos).r, neutral.g, sceneSample(bluePos).b);

    float transmitted0 = lerp(neutral, dispersed, edgeFactor * uDispersionStrength);
    float transmitted  = transmitted0;

    // 文字遮罩只做极轻的保护，绝不允许变成强模糊。
    float textPresence = max(maskSample(greenPos), maskSample(pixel));
    transmitted = lerp(transmitted, neutral, textPresence * TEXT_MASK_PROTECTION);

    float3 reflected = fresnelReflection(pixel, rect);
    float fresnel = 0.04 + 0.96 * pow(1.0 - saturate(1.0 - edgeFactor * 0.36), 5.0);
    float reflectionEvent = step(seed, fresnel * REFLECTION_BASE_WEIGHT);

    return lerp(transmitted, reflected, reflectionEvent);
}

float4 liquidGlass(float2 pixel, float4 rect, int pathCount, float pathsPerPixelMax)
{
    float distanceToEdge = rectDistance(pixel, rect);
    float antialiasWidth = max(1.0, fwidth(distanceToEdge));
    float coverage = 1.0 - smoothStep(-antialiasWidth, antialiasWidth, distanceToEdge);
    if (coverage <= 0.0) return float4(0.0, 0.0, 0.0, 0.0);

    float depth    = max(0.0, -distanceToEdge);
    float edgeBand = max(5.0, rect.w * uEdgeBandRatio);
    float edgeFactor = 1.0 - smoothStep(0.0, edgeBand, depth);

    float3 traced = 0.0;
    [loop] for (int i = 0; i < pathCount; ++i)
    {
        float seed = hash12(pixel + float2(float(i) * 17.13, uFrame * 0.754 + 3.7));
        traced += tracePath(pixel, rect, seed, edgeFactor);
    }
    traced /= pathsPerPixelMax;

    float refractionBand = max(5.0, rect.w * uRefractionVisibleRatio);
    float blurWeight = smoothStep(max(0.0, refractionBand - uBlendFeather),
                                  refractionBand + uBlendFeather, depth);

    // 关键：模糊的采样锚点沿着与折射同一条路径走，再平滑回到原像素。
    // 这是消除"折射区 / 雾化区"之间可见接缝的唯一办法。
    float2 refractedAnchor = refractedPosition(pixel, rect, REFRACTIVE_GREEN, 0.37);
    float2 blurAnchor = lerp(refractedAnchor, pixel, blurWeight);
    float3 frosted = gaussianBlur(blurAnchor, uBlurSpacing);

    float frostMix = blurWeight * uFrostedStrength * uFrostedAttenuation;
    float3 layered = lerp(traced, frosted, frostMix);

    float2 localUv = (pixel - rect.xy) / rect.zw;
    layered = lerp(layered, uTintColor, uTintMix);
    layered += FROST_TINT * blurWeight * uFrostedStrength;

    // 两条对角弱高光
    float diagonalA = smoothStep(HIGHLIGHT_A_WIDTH, 0.0, abs(localUv.x + localUv.y - HIGHLIGHT_A_CENTER));
    float diagonalB = smoothStep(HIGHLIGHT_B_WIDTH, 0.0, abs(localUv.x + localUv.y - HIGHLIGHT_B_CENTER));
    float highlight = (diagonalA + diagonalB) * edgeFactor * uHighlightStrength;
    layered += highlight;

    return float4(max(layered, 0.0), coverage);
}

// ---------------------------------------------------------------- 入口

struct VSOutput
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

// 每像素路径数在 HLSL 里必须编译期展开（SM5.0 不允许真正的动态循环上界），
// 所以这里按 4 条路径写死，与上游桌面参考值一致。
// 若需要移动端档的 2 条路径，复制本函数改常量即可。
#define PATHS_PER_PIXEL 4

float4 PSMainTrace(VSOutput input) : SV_Target
{
    // vUv 原点在左上，y 向下翻转成上游的 topPixel 坐标。
    float2 topPixel = float2(input.uv.x * uResolution.x,
                             (1.0 - input.uv.y) * uResolution.y);

    float navDistance = rectDistance(topPixel, uGlassRect);
    bool insideNav = navDistance <= 1.5;

    float4 previous = uPrevious.SampleLevel(uSceneSampler, input.uv, 0);

    float4 current = float4(0.0, 0.0, 0.0, 0.0);
    if (insideNav)
    {
        current = liquidGlass(topPixel, uGlassRect, PATHS_PER_PIXEL, (float)PATHS_PER_PIXEL);
    }

    // 时间累积：out = mix(current, previous, frame / (frame + 1))，最多 48 帧。
    float accumulation = min(uFrame, 47.0);
    float historyWeight = uReset > 0.5 ? 0.0 : accumulation / (accumulation + 1.0);
    return lerp(current, previous, historyWeight);
}

// Display pass：线性 -> sRGB（单独一遍，输出到交换链）
float4 PSMainDisplay(VSOutput input) : SV_Target
{
    float4 sampleValue = uScene.SampleLevel(uSceneSampler, input.uv, 0);
    float3 c = sampleValue.rgb;
    float3 srgb = lerp(c * 12.92,
                       1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055,
                       step(0.0031308, c));
    return float4(saturate(srgb), sampleValue.a);
}

// ============================================================================
//  接入要点（务必遵守，否则视觉效果会明显不对）
// ----------------------------------------------------------------------------
//  1. 累积纹理优先用 DXGI_FORMAT_R16G16B16A16_FLOAT（对应上游 RGBA16F），
//     不支持时退回 R8G8B8A8_UNORM。这只是纹理格式回退，不是视觉降级。
//  2. trace pass 输出线性空间，display pass 才转 sRGB。
//     若把两步合并或在错误的环节做 gamma，边缘会发灰、过亮或过饱和。
//  3. uScene 必须是 <b>位于玻璃背后的</b> 内容。
//     ⚠️ 绝不要把玻璃层自身采进 uScene。上游用 data-liquid-glass-canvas
//     排除 Canvas，Windows 侧对应 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)。
//     违反这一条会产生水平条纹、曲线波纹与递归残影。
//  4. 场景或几何变化时把 uReset 置 1 重新累积；稳态下 uFrame >= 48 后
//     可以整帧跳过（上游同样冻结输出）。
// ============================================================================
