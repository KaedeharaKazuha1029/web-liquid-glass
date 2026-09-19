using System.Runtime.CompilerServices;

namespace LiquidGlass.Core;

/// <summary>画布局部坐标系中的矩形（像素）。</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width * 0.5;
    public double CenterY => Y + Height * 0.5;
    public double HalfWidth => Width * 0.5;
    public double HalfHeight => Height * 0.5;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public override string ToString() => $"({X:F1},{Y:F1}) {Width:F1}x{Height:F1}";
}

/// <summary>
/// 液态玻璃光学核心——上游 <c>shaders.js</c> 中 trace fragment shader 的逐行移植。
///
/// 所有几何量都是<b>画布局部像素</b>坐标，原点在画布左上角、y 轴向下
/// （上游是在 GPU 里先把 vUv 翻成 topPixel，这里直接在 topPixel 空间工作）。
///
/// 数值类型刻意使用 <see cref="float"/> 而非 <c>double</c>：
/// 上游是 GLSL <c>highp float</c>，用 double 反而会算出与参考实现不一致的结果。
/// </summary>
public sealed class GlassOptics
{
    private const float Pi = 3.141592653589793f;

    // 上游 shaders.js 中写死在 GLSL 里的两个常量（非 uniform）。
    private const float SurfaceNormalEdgeBoost = 1.35f;
    private const float ReflectionBaseWeight = 0.34f;

    private readonly LiquidGlassMaterial _m;

    public GlassOptics(LiquidGlassMaterial material) => _m = material;

    public LiquidGlassMaterial Material => _m;

    /// <summary>
    /// 是否启用"深内部快速路径"。
    ///
    /// 这是移植过程中<b>唯一</b>新增的优化，上游没有。当像素到玻璃边界的深度
    /// 超过边缘带宽时，<c>edgeFactor</c> 恰为 0，于是：
    /// <code>
    /// lensCurve = 1 - sqrt(1 - 0²) = 0
    /// ⇒ refractedPosition(pixel) ≡ pixel  （三条路径的坐标完全相同）
    /// ⇒ dispersed ≡ neutral，色散与遮罩混合全部退化为恒等
    /// </code>
    /// 因此可以跳过全部 SDF 求值与两次 <c>refract()</c>，结果<b>逐位不变</b>。
    ///
    /// 默认开启。回归测试会把它关掉，逐像素比对两条路径的输出，
    /// 以此证明这不是"看起来差不多"的近似。
    /// </summary>
    public static bool EnableDeepInteriorFastPath { get; set; } = true;

    // ------------------------------------------------------------ 基础工具

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fract(float v) => v - MathF.Floor(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    /// <summary>上游 <c>smoothstep()</c>。允许 edge0 &gt; edge1（上游确实这样用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var denom = edge1 - edge0;
        if (denom == 0f) return x < edge0 ? 0f : 1f;
        var t = Saturate((x - edge0) / denom);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// 上游 <c>hash12()</c>：把像素坐标与帧号搅成 [0,1) 的伪随机数。
    /// 它是"每像素 4 条路径"这种蒙特卡洛采样能收敛的前提。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hash12(float x, float y)
    {
        var p0 = Fract(x * 0.1031f);
        var p1 = Fract(y * 0.1031f);
        var p2 = Fract(x * 0.1031f);

        var dot = p0 * (p1 + 33.33f) + p1 * (p2 + 33.33f) + p2 * (p0 + 33.33f);

        p0 += dot; p1 += dot; p2 += dot;

        return Fract((p0 + p1) * p2);
    }

    /// <summary>
    /// 解析圆角半径。
    /// 默认（<see cref="LiquidGlassMaterial.CornerRadius"/> = -1）沿用上游规则
    /// <c>max(1, min(halfW, halfH) - 1)</c>，即"取较短边的一半再减 1"，
    /// 因此细长矩形会自然变成胶囊形。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ResolveRadius(float halfWidth, float halfHeight)
    {
        var auto = MathF.Max(1f, MathF.Min(halfWidth, halfHeight) - 1f);
        if (_m.CornerRadius < 0) return auto;
        // 不允许超过半宽/半高，否则 SDF 会退化成退化形状。
        return MathF.Min((float)_m.CornerRadius, MathF.Min(halfWidth, halfHeight));
    }

    // ------------------------------------------------------------ 几何：SDF

    /// <summary>上游 <c>roundedRectSdf()</c>。负值在内部，绝对值即到边界的距离。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RoundedRectSdf(float px, float py, float hx, float hy, float radius)
    {
        var qx = MathF.Abs(px) - hx + radius;
        var qy = MathF.Abs(py) - hy + radius;
        var mx = qx > 0f ? qx : 0f;
        var my = qy > 0f ? qy : 0f;
        var outside = MathF.Sqrt(mx * mx + my * my);
        var inner = MathF.Max(qx, qy);
        if (inner > 0f) inner = 0f;
        return outside + inner - radius;
    }

    /// <summary>
    /// 上游 <c>rectDistance()</c>。
    /// 圆角半径取 <c>max(1, min(halfW, halfH) - 1)</c>——注意用的是
    /// <b>两者较小值</b>，所以 2560×72 的任务栏会得到"胶囊形"（半径 35）而不是矩形。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float RectDistance(double px, double py, in PixelRect rect)
    {
        var hx = (float)rect.HalfWidth;
        var hy = (float)rect.HalfHeight;
        var radius = ResolveRadius(hx, hy);
        return RoundedRectSdf((float)px - (float)rect.CenterX, (float)py - (float)rect.CenterY, hx, hy, radius);
    }

    /// <summary>
    /// 上游 <c>rectNormal()</c>：用 SDF 的中心差分求外轮廓法线。
    /// 步长固定 0.8px；内部平坦区域会产生一个近似对角的方向（上游同样如此）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RectNormal(double px, double py, in PixelRect rect, out float nx, out float ny)
    {
        var hx = (float)rect.HalfWidth;
        var hy = (float)rect.HalfHeight;
        var radius = ResolveRadius(hx, hy);
        var lx = (float)px - (float)rect.CenterX;
        var ly = (float)py - (float)rect.CenterY;

        var dx = RoundedRectSdf(lx + 0.8f, ly, hx, hy, radius)
               - RoundedRectSdf(lx - 0.8f, ly, hx, hy, radius);
        var dy = RoundedRectSdf(lx, ly + 0.8f, hx, hy, radius)
               - RoundedRectSdf(lx, ly - 0.8f, hx, hy, radius);

        var vx = dx + 0.00001f;
        var vy = dy + 0.00001f;
        var len = MathF.Sqrt(vx * vx + vy * vy);
        if (len < 1e-12f) { nx = 0f; ny = 0f; return; }
        nx = vx / len;
        ny = vy / len;
    }

    // -------------------------------------------------------------- 折射

    /// <summary>GLSL 内建 <c>refract()</c> 的逐行实现。全反射时返回零向量。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refract(
        float ix, float iy, float iz,
        float nx, float ny, float nz,
        float eta,
        out float rx, out float ry, out float rz)
    {
        var dotNI = nx * ix + ny * iy + nz * iz;
        var k = 1f - eta * eta * (1f - dotNI * dotNI);
        if (k < 0f)
        {
            rx = 0f; ry = 0f; rz = 0f;
            return;
        }
        var scale = eta * dotNI + MathF.Sqrt(k);
        rx = eta * ix - scale * nx;
        ry = eta * iy - scale * ny;
        rz = eta * iz - scale * nz;
    }

    /// <summary>
    /// 上游 <c>refractedPosition()</c>——整套效果的心脏。
    ///
    /// 每一步都在做同一件事：把"屏幕上的这个像素"重新解释为
    /// "透过一块有厚度、有曲率的玻璃看到的某个<b>别处</b>的像素"。
    /// </summary>
    /// <param name="seed">
    /// 表面粗糙度扰动用的随机数。上游用 0.37 作为雾化锚点的固定种子，
    /// 其余路径用逐像素 hash。
    /// </param>
    /// <param name="deepInterior">
    /// 快速路径标记：为 true 时 <c>depth &gt;= edgeBand</c>，
    /// 此时 <c>edgeFactor == 0</c> ⇒ <c>lensCurve == 0</c> ⇒ 位移恒为 0。
    /// 这不是近似，是数学上的精确等价（证明见 docs/PORTING-NOTES.md）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefractedPosition(
        double pixelX, double pixelY,
        in PixelRect rect,
        float indexOfRefraction,
        float seed,
        float mouseX, float mouseY,
        bool deepInterior,
        out double outX, out double outY)
    {
        if (deepInterior)
        {
            outX = pixelX;
            outY = pixelY;
            return;
        }

        var distanceToEdge = RectDistance(pixelX, pixelY, rect);
        var depth = distanceToEdge < 0f ? -distanceToEdge : 0f;
        var edgeBand = MathF.Max(5f, (float)rect.Height * (float)_m.EdgeBandRatio);

        var u = Saturate(depth / edgeBand);
        var smoothInterior = u * u * (3f - 2f * u);
        var edgeFactor = 1f - smoothInterior;

        RectNormal(pixelX, pixelY, rect, out var nx, out var ny);

        var roughAngle = seed * Pi * 2f;
        var roughOffsetX = MathF.Cos(roughAngle) * (float)_m.Roughness;
        var roughOffsetY = MathF.Sin(roughAngle) * (float)_m.Roughness;

        // 表面法线：外轮廓法线在边缘处倾斜 1.35 倍曲率，内部退化为平面。
        var snx = nx * edgeFactor * SurfaceNormalEdgeBoost * (float)_m.EdgeCurvature + roughOffsetX;
        var sny = ny * edgeFactor * SurfaceNormalEdgeBoost * (float)_m.EdgeCurvature + roughOffsetY;
        var snz = 1f;
        var snLen = MathF.Sqrt(snx * snx + sny * sny + snz * snz);
        snx /= snLen; sny /= snLen; snz /= snLen;

        // 相机射线：由鼠标位置轻微倾斜，产生"玻璃在跟着你看"的视差。
        var crx = (mouseX - 0.5f) * (float)_m.CameraRayScale;
        var cry = (mouseY - 0.5f) * (float)_m.CameraRayScale;
        var crz = -1f;
        var crLen = MathF.Sqrt(crx * crx + cry * cry + crz * crz);
        crx /= crLen; cry /= crLen; crz /= crLen;

        // 第一次折射：空气 → 玻璃。
        Refract(crx, cry, crz, snx, sny, snz, 1f / indexOfRefraction,
            out var inx, out var iny, out var inz);

        var thickness = (float)_m.OpticalThickness *
            ((float)_m.InsideTravelBasePx + smoothInterior * (float)rect.Height * (float)_m.InsideTravelInteriorScale);
        var insideTravel = thickness / MathF.Max((float)_m.MinimumRayZ, -inz);

        var hitX = pixelX + inx * insideTravel;
        var hitY = pixelY + iny * insideTravel;

        // 第二次折射：玻璃 → 空气（出口是平面）。
        Refract(inx, iny, inz, 0f, 0f, 1f, indexOfRefraction,
            out var ex, out var ey, out var ez);
        var exitTravel = (float)_m.ExitTravelBasePx * (float)_m.OpticalThickness
                       / MathF.Max((float)_m.MinimumRayZ, -ez);

        hitX += ex * exitTravel;
        hitY += ey * exitTravel;

        // 透镜曲线：只在边缘起作用，中心保持清晰。
        var lensCurve = 1f - MathF.Sqrt(MathF.Max(0f, 1f - edgeFactor * edgeFactor));
        var reversePixels = MathF.Min((float)_m.ReverseMaxPixels,
            edgeBand * (float)_m.ReverseDisplacement);

        outX = pixelX
             - nx * reversePixels * lensCurve
             + (hitX - pixelX) * lensCurve * (float)_m.LensMix;
        outY = pixelY
             - ny * reversePixels * lensCurve
             + (hitY - pixelY) * lensCurve * (float)_m.LensMix;
    }

    // ---------------------------------------------------------------- 主入口

    /// <summary>
    /// 上游 <c>liquidGlass()</c>：算出一个像素的线性空间颜色与覆盖率。
    /// </summary>
    /// <param name="pathCount">本像素参与平均的路径数（上游默认 4）。</param>
    /// <param name="frame">积累帧号，参与 hash，让每帧的噪声不同。</param>
    public void EvaluatePixel(
        GlassScene scene,
        in PixelRect rect,
        double pixelX, double pixelY,
        float mouseX, float mouseY,
        int pathCount,
        float frame,
        out float outR, out float outG, out float outB, out float coverage)
    {
        var distanceToEdge = RectDistance(pixelX, pixelY, rect);

        // 上游用 fwidth(distanceToEdge) 作抗锯齿宽度。
        // 我们的 SDF 梯度模长恒为 1（每像素），且画布与屏幕 1:1，故 fwidth ≡ 1。
        const float antialiasWidth = 1f;
        coverage = 1f - SmoothStep(-antialiasWidth, antialiasWidth, distanceToEdge);
        if (coverage <= 0f)
        {
            outR = 0f; outG = 0f; outB = 0f;
            return;
        }

        var depth = distanceToEdge < 0f ? -distanceToEdge : 0f;
        var edgeBand = MathF.Max(5f, (float)rect.Height * (float)_m.EdgeBandRatio);
        var edgeFactor = 1f - SmoothStep(0f, edgeBand, depth);

        // 深度超过边缘带 ⇒ edgeFactor 恰为 0 ⇒ 折射位移恒为 0。
        // 这是精确等价，不是近似：lensCurve = 1 - sqrt(1 - 0²) = 0。
        // 任务栏里超过一半的像素会命中这条路，省掉全部 SDF 与 refract 计算。
        var deepInterior = EnableDeepInteriorFastPath && depth >= edgeBand;

        var tracedR = 0f;
        var tracedG = 0f;
        var tracedB = 0f;

        if (deepInterior)
        {
            // 三个通道的采样坐标完全相同，色散项自然退化为 0：
            //   dispersed == neutral  ⇒ mix(neutral, dispersed, 0) == neutral
            //   transmitted == neutral ⇒ 后续的遮罩混合也仍是 neutral
            // 于是只剩"随机菲涅耳反射"这一项仍然逐路径独立。
            scene.Sample(pixelX, pixelY, out tracedR, out tracedG, out tracedB);

            // 反射项本身与 seed 无关，可以提到路径循环外面——这是精确等价，不是近似。
            var (reflR, reflG, reflB) = FresnelReflection(pixelX, pixelY, rect, edgeFactor: 0f);
            var fresnel = 0.04f + 0.96f * MathF.Pow(1f - Saturate(1f - 0f), 5f);
            var threshold = fresnel * ReflectionBaseWeight;

            var sumR = 0f; var sumG = 0f; var sumB = 0f;
            for (var i = 0; i < pathCount; i++)
            {
                var seed = Hash12((float)pixelX + i * 17.13f, frame * 0.754f + 3.7f);
                var eventWeight = seed <= threshold ? 1f : 0f;
                sumR += Mix(tracedR, reflR, eventWeight);
                sumG += Mix(tracedG, reflG, eventWeight);
                sumB += Mix(tracedB, reflB, eventWeight);
            }
            var invFast = 1f / pathCount;
            tracedR = sumR * invFast;
            tracedG = sumG * invFast;
            tracedB = sumB * invFast;
        }
        else
        {
            for (var i = 0; i < pathCount; i++)
            {
                var seed = Hash12((float)pixelX + i * 17.13f, frame * 0.754f + 3.7f);
                TracePath(scene, rect, pixelX, pixelY, seed, mouseX, mouseY, edgeFactor,
                    out var pr, out var pg, out var pb);
                tracedR += pr; tracedG += pg; tracedB += pb;
            }
            var inv = 1f / pathCount;
            tracedR *= inv; tracedG *= inv; tracedB *= inv;
        }

        // ---- 折射区 → 雾化区的无缝过渡 ----
        var refractionBand = MathF.Max(5f, (float)rect.Height * (float)_m.RefractionVisibleRatio);
        var feather = (float)_m.BlendFeatherPx;
        var blurWeight = SmoothStep(MathF.Max(0f, refractionBand - feather), refractionBand + feather, depth);

        // 关键：模糊的采样中心不是原像素，而是沿着与折射同一条路径走过去的锚点。
        // 上游注释原话："Following the refracted anchor before returning to the
        // original pixel is what removes the visible seam between refraction and frosting."
        RefractedPosition(pixelX, pixelY, rect, (float)_m.IndexOfRefractionGreen, 0.37f,
            mouseX, mouseY, deepInterior, out var anchorX, out var anchorY);
        var blurX = Mix((float)anchorX, (float)pixelX, blurWeight);
        var blurY = Mix((float)anchorY, (float)pixelY, blurWeight);

        // 雾化从"预处理金字塔"取大支撑模糊 —— 见 GlassScene 顶部的长注释。
        // 这里的半径是纹理像素；默认 24 表示取第 log2(24)≈4.58 级，
        // 即相邻的 16px 与 32px 两级做三线性混合，等效支撑半径约 24 纹理px。
        //
        // ⚠️ 不要指望把它调很大：金字塔最高只到 2^(levels-1)，
        // 超出会夹到顶层（效果 = 整张图平均色）。要更大的半径请加 levels。
        var frostRadius = _m.FrostedBlurRadius;
        scene.SampleBlurred(blurX, blurY, frostRadius, out var frostR, out var frostG, out var frostB);

        var frostMix = blurWeight * (float)_m.FrostedStrength * (float)_m.FrostedAttenuation;
        var layeredR = Mix(tracedR, frostR, frostMix);
        var layeredG = Mix(tracedG, frostG, frostMix);
        var layeredB = Mix(tracedB, frostB, frostMix);

        // ---- 整体染色 + 雾化冷色偏移 ----
        var tintMix = (float)_m.TintMix;
        layeredR = Mix(layeredR, (float)_m.TintColor[0], tintMix);
        layeredG = Mix(layeredG, (float)_m.TintColor[1], tintMix);
        layeredB = Mix(layeredB, (float)_m.TintColor[2], tintMix);

        var frostWeight = blurWeight * (float)_m.FrostedStrength;
        layeredR += (float)_m.FrostTintColor[0] * frostWeight;
        layeredG += (float)_m.FrostTintColor[1] * frostWeight;
        layeredB += (float)_m.FrostTintColor[2] * frostWeight;

        // ---- 两条对角弱高光 ----
        var localU = (pixelX - rect.X) / rect.Width;
        var localV = (pixelY - rect.Y) / rect.Height;
        var diagonal = localU + localV;

        var diagonalA = SmoothStep((float)_m.HighlightAWidth, 0f,
            MathF.Abs((float)diagonal - (float)_m.HighlightACenter));
        var diagonalB = SmoothStep((float)_m.HighlightBWidth, 0f,
            MathF.Abs((float)diagonal - (float)_m.HighlightBCenter));
        var highlight = (diagonalA + diagonalB) * edgeFactor * (float)_m.HighlightStrength;

        outR = layeredR + highlight;
        outG = layeredG + highlight;
        outB = layeredB + highlight;
        if (outR < 0f) outR = 0f;
        if (outG < 0f) outG = 0f;
        if (outB < 0f) outB = 0f;
    }

    /// <summary>
    /// 上游 <c>tracePath()</c>：三个通道各用不同折射率，再按边缘因子混合。
    /// 这就是"RGB 色散"——1.514 / 1.520 / 1.528 三个 IOR 让红蓝在边缘轻微分离。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TracePath(
        GlassScene scene,
        in PixelRect rect,
        double pixelX, double pixelY,
        float seed,
        float mouseX, float mouseY,
        float edgeFactor,
        out float r, out float g, out float b)
    {
        RefractedPosition(pixelX, pixelY, rect, (float)_m.IndexOfRefractionRed, seed,
            mouseX, mouseY, false, out var redX, out var redY);
        RefractedPosition(pixelX, pixelY, rect, (float)_m.IndexOfRefractionGreen, seed,
            mouseX, mouseY, false, out var greenX, out var greenY);
        RefractedPosition(pixelX, pixelY, rect, (float)_m.IndexOfRefractionBlue, seed,
            mouseX, mouseY, false, out var blueX, out var blueY);

        scene.Sample(greenX, greenY, out var neutralR, out var neutralG, out var neutralB);
        scene.Sample(redX, redY, out var redSampleR, out _, out _);
        scene.Sample(blueX, blueY, out _, out _, out var blueSampleB);

        // 色散只取红蓝两端，绿通道留作"中性"参考，避免形成彩色描边。
        var dispersedR = redSampleR;
        var dispersedG = neutralG;
        var dispersedB = blueSampleB;

        var dispersion = edgeFactor * (float)_m.DispersionStrength;
        var tx = Mix(neutralR, dispersedR, dispersion);
        var ty = Mix(neutralG, dispersedG, dispersion);
        var tz = Mix(neutralB, dispersedB, dispersion);

        // 文字遮罩只做极轻的保护（0.025），绝不允许变成强模糊。
        var presence = MathF.Max(scene.SampleMask(greenX, greenY), scene.SampleMask(pixelX, pixelY));
        var protection = presence * (float)_m.TextMaskProtection;
        tx = Mix(tx, neutralR, protection);
        ty = Mix(ty, neutralG, protection);
        tz = Mix(tz, neutralB, protection);

        var (reflR, reflG, reflB) = FresnelReflection(pixelX, pixelY, rect, edgeFactor);

        var fresnel = 0.04f + 0.96f * MathF.Pow(1f - Saturate(1f - edgeFactor * 0.36f), 5f);
        var reflectionEvent = seed <= fresnel * ReflectionBaseWeight ? 1f : 0f;

        r = Mix(tx, reflR, reflectionEvent);
        g = Mix(ty, reflG, reflectionEvent);
        b = Mix(tz, reflB, reflectionEvent);
    }

    /// <summary>
    /// 随机菲涅耳反射：一条来自左上方的定向光，只在曲率足够的地方形成反射事件。
    /// 上游用 18 次幂把高光压成很窄的一条，避免整块玻璃发亮。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (float R, float G, float B) FresnelReflection(
        double pixelX, double pixelY, in PixelRect rect, float edgeFactor)
    {
        _ = edgeFactor;   // 上游此处并未使用 edgeFactor，保留参数以对齐签名

        RectNormal(pixelX, pixelY, rect, out var nx, out var ny);

        // normalize(vec3(normal, 0.72))
        const float surfaceZ = 0.72f;
        var snLen = MathF.Sqrt(nx * nx + ny * ny + surfaceZ * surfaceZ);
        var snx = nx / snLen;
        var sny = ny / snLen;
        var snz = surfaceZ / snLen;

        var topLightRaw = Saturate(snx * LightDirX + sny * LightDirY + snz * LightDirZ);
        var topLight = MathF.Pow(topLightRaw, 18f);

        var intensity = 0.24f + topLight * 0.72f;
        return (0.52f * intensity, 0.58f * intensity, 0.68f * intensity);
    }

    // normalize(vec3(-0.58, -0.74, 0.62)) —— 与上游字面值一致，这里预先算好常量。
    private const float LightDirX = -0.51503104f;
    private const float LightDirY = -0.65711519f;
    private const float LightDirZ = 0.55057279f;

    // ---------------------------------------------------------------- 模糊
    //
    //  重影的根因：模糊的【支撑宽度】不够，与核的形状无关
    //  ─────────────────────────────────────────────────────────────
    //  上游 <c>gaussianBlur()</c> 用一组<b>十字形</b>抽样点：除了中心，
    //  只有沿 X 轴与 Y 轴的 ±s、±2s 四个点（各轴合计 5 个点），
    //  支撑半径 ±2s。在开始菜单的量级上 s = 28 画布px（0.25 画布 → 112 屏幕px），
    //  也就是支撑只有 ±56 屏幕px。
    //
    //  实测传递函数（正弦输入的振幅增益，越接近 0 越好）：
    //
    //      背景结构周期     十字核
    //        160px         0.222
    //        320px         0.624      ← 62% 原样透出来，这就是"重影"
    //        480px         0.725
    //
    //  ⚠️ <b>不要试图靠"换个更好的核形状"来解决。</b>实测过 3×3 方形核，
    //  结果<b>更差</b>（320px 从 0.624 恶化到 0.926）—— 因为 28px 间距下
    //  的 3×3 网格比十字核采样得<b>更稀疏</b>。
    //
    //  正确的判断是：<b>抑制力只取决于支撑宽度</b>。要把周期 T 的压到 10%
    //  以下，需要支撑半径约 T/2 —— 160px 周期要 76px，320px 周期要 148px。
    //  在逐像素循环里做半径 148px 的采样是 (2×148+1)² ≈ 8.8 万次/像素，
    //  在 CPU 上无解。
    //
    //  ── 所以真正的解法在 GlassScene 侧，不在这个文件 ──
    //  参见 <see cref="GlassScene.SampleBlurred"/>：预先建一张多级降采样
    //  金字塔，逐像素时只取一次三线性样本即可获得任意大的支撑，O(1)。
    //  本方法是<b>回退路径</b>，只在场景没有金字塔时使用。
    //  ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 上游 <c>gaussianBlur()</c> 的桌面 9 抽样核（1 + 4 + 4）。
    ///
    /// <para><b>这是回退路径，不是主力。</b>它的抑制能力有硬上限
    /// （见上方长注释），默认的雾化走 <see cref="GlassScene.SampleBlurred"/>。
    /// 保留它是为了在没有金字塔的轻量场景下仍能出图。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GaussianBlur(
        GlassScene scene, float centerX, float centerY,
        out float r, out float g, out float b)
    {
        // 上游 blurSampleCount = 9 时的核
        const float w0 = 0.204164f;
        const float w1 = 0.180174f;
        const float w2 = 0.123832f;
        const float w3 = 0.066282f;
        const float w4 = 0.027631f;

        var s = (float)_m.BlurSpacingPx;
        float sr = 0f, sg = 0f, sb = 0f;

        scene.Sample(centerX, centerY, out var c0, out var c1, out var c2);
        sr += c0 * w0; sg += c1 * w0; sb += c2 * w0;

        scene.Sample(centerX + s, centerY, out c0, out c1, out c2);
        sr += c0 * w1; sg += c1 * w1; sb += c2 * w1;

        scene.Sample(centerX - s, centerY, out c0, out c1, out c2);
        sr += c0 * w1; sg += c1 * w1; sb += c2 * w1;

        scene.Sample(centerX + s * 2f, centerY, out c0, out c1, out c2);
        sr += c0 * w2; sg += c1 * w2; sb += c2 * w2;

        scene.Sample(centerX - s * 2f, centerY, out c0, out c1, out c2);
        sr += c0 * w2; sg += c1 * w2; sb += c2 * w2;

        scene.Sample(centerX, centerY + s, out c0, out c1, out c2);
        sr += c0 * w3; sg += c1 * w3; sb += c2 * w3;

        scene.Sample(centerX, centerY - s, out c0, out c1, out c2);
        sr += c0 * w3; sg += c1 * w3; sb += c2 * w3;

        scene.Sample(centerX, centerY + s * 2f, out c0, out c1, out c2);
        sr += c0 * w4; sg += c1 * w4; sb += c2 * w4;

        scene.Sample(centerX, centerY - s * 2f, out c0, out c1, out c2);
        sr += c0 * w4; sg += c1 * w4; sb += c2 * w4;

        r = sr; g = sg; b = sb;
    }
}
