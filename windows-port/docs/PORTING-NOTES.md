# 移植对照笔记：WebGL2 → Windows

这份文档记录**每一个决策的出处**。凡是改了上游行为的地方，都在这里有明确交代；
没有列在这里的，就是逐行照搬。

上游版本：`web-liquid-glass` @ `main`（2026-08-06 快照）

---

## 一、整体映射

| 上游概念 | 浏览器实现 | Windows 对应物 |
|----------|------------|----------------|
| 导航容器 `<nav>` | DOM 元素，`getBoundingClientRect()` | 系统组件窗口 HWND，`DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)` |
| 玻璃 Canvas | `<canvas>` 位于按钮之上 | `WS_EX_LAYERED` 覆盖窗口，位于组件**之下** |
| `uScene` 纹理 | Canvas2D 重绘的 DOM 快照 | `BitBlt` 抓取的屏幕像素 |
| `uTextMask` | Canvas2D 重绘的文字遮罩 | 默认全 0（见下文 §六） |
| `uPrevious` (RGBA16F) | WebGL ping-pong framebuffer | `float[]` × 2，手工乒乓 |
| `uNavRect` | 导航栏在 Canvas 里的矩形 | 玻璃矩形（画布局部坐标） |
| `uMouse` | `pointermove` 归一化坐标 | `GetCursorPos()` 归一化到玻璃矩形 |
| display pass | 第二个 GLSL program | CPU 端 sRGB 查找表 |
| WebGL2 不可用的降级 | 隐藏 Canvas，保留原始导航 | Accent 失败时明确报错，不伪装 |

---

## 二、几何坐标

上游在 shader 里先把 `vUv` 翻成 `topPixel`：

```glsl
vec2 topPixel = vec2(vUv.x * uResolution.x, (1.0 - vUv.y) * uResolution.y);
```

**本移植直接在 `topPixel` 空间工作**（y 轴向下、原点左上、单位像素），
省掉一次翻转。所有 SDF、法线、位移计算因此完全不涉及 uv 概念。
HLSL 版本则保留翻转，因为它仍然要吃 `SV_Position`。

---

## 三、材质参数的逐值出处

全部来自上游 `src/layout.js` 的 `LIQUID_GLASS_REFERENCE`：

| 参数 | 值 | 上游字段 |
|------|-----|----------|
| `ReverseDisplacement` | 2.19 | `material.reverseDisplacement` |
| `EdgeCurvature` | 0.67 | `material.edgeCurvature` |
| `OpticalThickness` | 1.71 | `material.opticalThickness` |
| `EdgeBandRatio` | 0.26 | `material.edgeBandRatio` |
| `RefractionVisibleRatio` | 0.16 | `material.refractionVisibleRatio` |
| `BlendFeatherPx` | 12 | `material.blendFeatherPx` |
| `FrostedStrength` | 0.29 | `material.frostedStrength` |
| `FrostedAttenuation` | 0.55 | `material.frostedAttenuation` |
| `BlurSpacingPx` | 1.4 | `material.blurSpacingPx` |
| `DispersionStrength` | 0.22 | `material.dispersionStrength` |
| `HighlightStrength` | 0.055 | `material.highlightStrength` |
| `TintColor` | 0.035 / 0.035 / 0.045 | `material.tintColor` |
| `TintMix` | 0.17 | `material.tintMix` |
| 折射率 | 1.514 / 1.520 / 1.528 | `optics.refractiveIndices` |
| `Roughness` | 0.014 | `optics.roughness` |
| `CameraRayScale` | 0.045 | `optics.cameraRayScale` |
| `MinimumRayZ` | 0.08 | `optics.minimumRayZ` |
| `InsideTravelBasePx` | 6.0 | `optics.insideTravelBasePx` |
| `InsideTravelInteriorScale` | 0.28 | `optics.insideTravelInteriorScale` |
| `ExitTravelBasePx` | 6.5 | `optics.exitTravelBasePx` |
| `ReverseMaxPixels` | 36 | `optics.reverseMaxPixels` |
| `LensMix` | 0.35 | `optics.lensMix` |
| `TextMaskProtection` | 0.025 | `optics.textMaskProtection` |
| `FrostTintColor` | 0.055 / 0.062 / 0.075 | `optics.frostTintColor` |
| 对角高光 A | 宽 0.22 / 中心 0.22 | `optics.highlightA*` |
| 对角高光 B | 宽 0.20 / 中心 1.78 | `optics.highlightB*` |
| `SurfaceNormalEdgeBoost` | 1.35 | shaders.js 中**硬编码**的 GLSL 常量 |
| `ReflectionBaseWeight` | 0.34 | 同上 |
| `LightDir` | `normalize(-0.58, -0.74, 0.62)` | 同上 |

`LiquidGlass.Tests` 里的「材质契约」测试会在任何一个数字被改动时立刻失败。
这不是形式主义——上游文档明确要求 *"建议先使用默认值完成视觉验收"*。

---

## 四、**唯一**新增的性能优化：深内部快速路径

这是整个移植里唯一一处上游没有的东西，因此需要给出证明。

### 命题

当像素到玻璃边界的深度 `depth >= edgeBand` 时：

```
edgeFactor  = 1 - smoothstep(0, edgeBand, depth) = 1 - 1 = 0
smoothInterior = 1 - edgeFactor = 1
```

于是 `refractedPosition()` 中的：

```
lensCurve      = 1 - sqrt(max(0, 1 - edgeFactor²)) = 1 - sqrt(1) = 0
reversePixels  = ...
返回值 pixel - outlineNormal * reversePixels * 0 + (hit - pixel) * 0 * LensMix
              = pixel
```

**三条通道的采样坐标全部恒等于原像素。**

### 推论

```
neutral   = sceneSample(pixel)
dispersed = vec3(sceneSample(pixel).r, neutral.g, sceneSample(pixel).b) = neutral
transmitted = mix(neutral, dispersed, 0 * DispersionStrength) = neutral
textPresence = max(maskSample(pixel), maskSample(pixel)) = maskSample(pixel)
transmitted = mix(neutral, neutral, ...) = neutral
```

只剩随机菲涅耳反射项仍逐路径独立。而 **反射项本身与 seed 无关**
（`rectNormal` 与 `topLight` 都不吃 seed），所以可以整体提到路径循环之外。

### 结论

快速路径与完整路径**逐位相同**，不是近似。
`LiquidGlass.Tests` 的「深内部快速路径：逐位精确性」用四种形态
（任务栏 1280×54 / 开始菜单 300×420 / 近正方形 200×200 / 迷你条 64×18）
逐像素对比 `bit` 并与全路径比较，**零处不一致**。

### 收益

对 2560×72 的任务栏，边缘带 = 72 × 0.26 = 18.7px，即上下各 18.7px 之外
的全部像素（超过一半）都命中快速路径，省掉全部 SDF 求值与 `refract()`。

配合以下两项优化，实测从 **145 ms/帧降到 6.6 ms/帧（1280×54）**，约 22 倍：

| 优化 | 说明 |
|------|------|
| 只栅格化玻璃包围盒 | 原本对整张画布跑逐像素逻辑；现在只跑 `rectDistance < 1` 的那一块 |
| sRGB 双向查找表 | `srgbToLinear` 输入是 8 位整数 → 256 项表（逐位相同）；`linearToSrgb` 用 4096 项表（误差 ≤ 1 色阶） |

---

## 五、CPU 与 GPU 的取舍

上游是 WebGL2。本移植默认走 **CPU 参考渲染器**，理由是工程性的：

| 维度 | CPU（本仓库默认） | GPU（`shaders/glass.hlsl`） |
|------|-------------------|------------------------------|
| 第三方依赖 | **零** | 需要 D3D11/12 宿主与 COM 绑定 |
| 可验证性 | 可以逐像素断言、可以离线出图 | 需要真机跑 |
| 任务栏 2560×72 | 13.5 ms/帧 | 远快于 CPU |
| 开始菜单 480×560 | 25.7 ms/帧 | 远快于 CPU |
| 稳态开销 | **0**（48 帧后冻结） | 0 |
| 可移植性 | 任何 .NET 10 环境 | 需要 GPU |

关键在于**上游自己在 48 帧后就冻结输出**：

```glsl
float accumulation = min(uFrame, 48.0 - 1.0);
float historyWeight = uReset > 0.5 ? 0.0 : accumulation / (accumulation + 1.0);
outColor = mix(current, previous, historyWeight);
```

也就是说渲染是**脉冲式**的：场景一变就冲 48 帧，然后彻底静止。
CPU 后端在这种负载下完全够用，而零依赖带来的可部署性优势是决定性的。

需要更高吞吐的场景请自行接入 `shaders/glass.hlsl`——它的 uniform 布局、
常量、以及四条接入注意事项都在文件里写清楚了。

### 五′、为什么模糊必须做成"预处理金字塔"而不是逐像素核

这是移植过程中**唯一一处从原理上重写**的地方，值得单独记。

上游的雾化是一个 9 抽头十字核（`BlurSpacingPx = 28`）。
它的支撑半径只有 `±2 × 28 = ±56` 屏幕px。

而"重影"（背景文字透过玻璃被看见）的根因就是**支撑宽度不够**：

> 抑制一个周期为 `T` 的结构，模糊核的支撑半径需要达到约 `T/2`，
> **与核的形状无关。**

桌面背景的文字排版周期在 160–480px 量级，所以 56px 的支撑远远不够 ——
160px 周期的结构会以 **99.9%** 的振幅原样透出来。

| 背景结构周期 | 点采样（旧） | 半径 24 | 半径 48 | 半径 96 | 半径 128 |
|---|---|---|---|---|---|
| 40px | 0.995 | 0.354 | 0.189 | 0.071 | 0.046 |
| 80px | 0.999 | 0.676 | 0.364 | 0.130 | 0.091 |
| 160px | **0.999** | 0.912 | 0.677 | 0.241 | 0.179 |
| 320px | 0.818 | 0.952 | 0.928 | 0.579 | 0.424 |

**试过的弯路**：把十字核换成 3×3 方形核，以为"采样更密就能抹平"。
**实测反而更差**（320px 从 0.624 → 0.926）——28px 间距下的 3×3 网格
比十字核的采样点**更稀疏**。教训：换形状是徒劳的。

**为什么不能直接在逐像素里放大核**：半径 148px 的核需要
`(2×148+1)² ≈ 8.8 万`次采样/像素。开始菜单玻璃画布 340×318 ≈ 10.8 万像素
→ 单帧 95 亿次采样。CPU 上没有解。

**解法 —— 多级降采样金字塔（mip pyramid）**：

| 步骤 | 做法 | 效果 |
|---|---|---|
| 建塔 | 每级 2×2 箱式平均 | 第 k 级每个样本已平均 `4^k` 个原始像素，支撑半径 = `2^k` 纹理px |
| 采样 | 按 `level = log2(radius)` 取**一次**双线性样本 | **逐像素 O(1)** |
| 去台阶 | 相邻两级按小数塔层做**三线性插值** | 任意半径连续，无跨级跳变 |
| 建造成本 | 每像素只被读一次 | 总工作量 `< 1.34 ×` 原图，且**每帧只建一次** |

实现见 `GlassScene.SampleBlurred` / `BuildPyramidCore`，
参数见 `LiquidGlassMaterial.FrostedBlurRadius`（默认 96）。

⚠️ **并发陷阱**：金字塔是**惰性构建**的，而采样入口
`SampleBlurred` 是从 `CpuGlassRenderer` 的 `Parallel.For` 里被**并发**调用的。
第一版直接写共享的 `_pyramid` / `_pyramidWidth` 字段，
于是线程 A 正在写 `_pyramid[3]`、线程 B 已读到 `_pyramidWidth[3]`
却发现 `_pyramid` 只有 2 个元素 → `NullReferenceException`，
而且**打挂了整个替换任务栏的启动**。

正确做法：双检锁 + **不可变 `Pyramid` 快照** + `Volatile.Write`/`Volatile.Read`
原子发布。读侧只取一次本地引用，永远看到自洽的一组数组。

---

## 六、文字遮罩：为什么默认全 0

上游有 `uTextMask`，因为它的场景纹理里**烘焙了导航按钮的文字**，
需要轻量保护避免文字出现"一份清晰的 + 一份折射变形的"两个副本
（上游注释：*"The mask only protects text very lightly. It never enables strong blur."*，
保护强度只有 0.025）。

Windows 侧不存在这个问题：透明化之后，任务栏图标与文字由 explorer
绘制在**玻璃层之上**，根本没有进入折射源。所以遮罩保持全 0 是正确且充分的。

`GlassScene` 仍然支持传入遮罩——如果你把某个窗口整体烘焙进折射源
（比如用 `PrintWindow` 抓一个富文本窗口当场景），那就该把它的文字区域标出来。

---

## 七、刻意未实现的功能

### 悬停胶囊（hover capsule）—— v2.4 起已实现

> **历史**：v1~v2.3 刻意跳过了它，理由是"Windows 任务栏本身有指针反馈，
> 再叠一层游走的胶囊会抢戏"。**v2.4 按用户要求补上了**，下面保留原始理由供对照，
> 其后记录最终实现。

<details>
<summary>当初判定"不实现"的三条理由（已被推翻）</summary>

1. Windows 11 任务栏本身就有指针反馈（图标 hover 高亮、悬停预览）。
2. 胶囊的价值在于给扁平网页导航提供点击区域反馈；任务栏的点击目标位置固定。
3. 上游的复杂度（独立 rect + 独立累积预算 + 新旧区只重绘）在任务栏上换不到收益。

</details>

**最终实现**（`ReplacementTaskbar`，约 120 行）：

- **几何逐行移植**上游 `layout.js` 的 `computeCapsuleRect()`：
  宽 = 导航宽 × 0.2（1/5）、高 = × 1.2（上下各凸出 10%）、
  中心跟随指针归一化 x 并**夹在导航左右边界内**（不会滑出去）。
- **第二个 `CpuGlassRenderer` 实例**：胶囊有<b>自己的累积缓冲</b>，
  所以移动它不会让整条任务栏重新累积 —— 对应上游把胶囊拆成
  `uCapsuleOnly` 一遍 pass、只重绘新旧胶囊区域的做法。
- **画布加高**：因为胶囊比玻璃条高 20%，覆盖窗口要上下各扩
  `height × 0.1`（默认 12px）作为溢出区，否则胶囊两端会被裁掉、
  退化成和导航一样高。玻璃条在画布内下移同样的量，图标绘制与鼠标命中同步换算。
- **动画跟随**：每 tick 向目标推进 `capsuleFollow`（默认 0.35），
  接近后吸附，避免"永远差一点"导致停不下来。
- **独立性能预算**：`capsulePathsPerPixel`（默认 2）/ `capsuleAccumulation`（默认 16），
  调低只影响胶囊自己的干净度，不拖累导航本体。

**实测**（Windows 11 Build 26200，任务栏 2284×116、16 个应用）：

```
悬停胶囊：已渲染（rect 456.8x139.2，覆盖 60208px，
                 与玻璃条的平均像素差 16.5/通道，可见度 100%）
```

456.8 = 2284 × 0.2、139.2 = 116 × 1.2，与上游比例一致；
"与玻璃条的平均像素差"是**同一帧内**胶囊层与玻璃条层的差异，
因此不受场景变化干扰，可以直接当作"胶囊到底看不看得见"的硬指标。

> ⚠️ 一个尚未解决的连带问题：把窗口沉到 Z 序最底部后，
> 覆盖层**收不到鼠标消息**（窗口过程的 `WM_NCHITTEST` 计数恒为 0，
> 见 `LayeredOverlayWindow.HitTestSeen`）。胶囊因此改用
> **轮询 `GetCursorPos`** 来跟踪指针（`UpdatePointerFromCursor`），
> 但"点图标启动应用"仍依赖鼠标消息 —— 这一条待修。

### DOM 场景重绘

上游的 `scene-capture.js` 用 Canvas2D 重绘 DOM。Windows 上不需要——
我们直接抓真实像素，保真度天然更高（上游的 DOM 重绘**不支持**复杂 CSS 渐变、
伪元素、视频帧、滤镜、阴影与跨域图片，而屏幕像素没有这些限制）。

---

## 八、Windows 特有的三个坑

### 1. 玻璃层必须排除在屏幕捕获之外

上游用 `data-liquid-glass-canvas` 把玻璃 Canvas 排除出 DOM 捕获，
并明确警告：

> *"严禁把玻璃 Canvas 自己重新采样进场景；否则会产生水平条纹、曲线波纹和递归残影。"*

Windows 的对应物是 `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`（Win10 2004+）。
它让窗口"屏幕上可见、截图不可见"，正好满足需求。

不支持时自动退回"隐藏 → 抓屏 → 恢复"路径，在 `docs/ARCHITECTURE.md` 里有说明。

### 2. 越界采样必须补边

任务栏贴着屏幕底边，而折射会向下采样到矩形之外。
直接 BitBlt 越界区域会得到黑边，玻璃下缘会浮出一条突兀的暗线。
`ScreenSampler.CapturePadded()` 用**边界像素延续**填充越界部分，
视觉上等价于"壁纸继续延伸下去"。

### 3. DPI 感知必须在进程启动那一刻就位

如果进程是 DPI 不感知状态，`GetWindowRect` 返回的是被系统拉伸过的**逻辑坐标**，
玻璃层会与任务栏错开整整一半——这是系统级覆盖窗口最常见的翻车点。

本程序在 `app.manifest` 里声明 `PerMonitorV2`（清单在 CLR 启动前生效），
同时在 `DisplayEnvironment.EnablePerMonitorDpiAwareness()` 里再调一次作为双保险。
这正是 WinForms 的 `WFO0003` 告警想让我们删掉清单声明、改用运行时 API 时
我们**故意不采纳**的原因——运行时代码跑起来之前那段空隙里坐标已经是错的。

---

## 九、行为差异一览

| 项 | 上游 | 本移植 | 原因 |
|----|------|--------|------|
| 每像素路径数 | 4（移动端 2） | 4 | 一致 |
| 最大累积帧 | 48（移动端 24） | 48（鼠标移动时降到 12） | 见下 |
| DPR 上限 | 1.5 | 跟随系统物理分辨率 | 覆盖窗口不做缩放，天然 1:1 |
| 玻璃矩形 | 85vw × 56px | 组件矩形内缩（默认 10 / 6 px） | 适配任务栏几何 |
| 圆角 | `max(1, min(halfW,halfH)-1)` | 同左，可覆盖 | 新增 `cornerRadius`，默认 -1 保持上游行为 |
| 指针在玻璃上时的质量 | 胶囊独立预算 | 累积上限降到 12 | 替代方案，换取跟手感 |

关于最后一条：上游用一个独立的胶囊预算（1 path × 8 帧）来实现"跟手"，
因为胶囊的更新是增量的。本移植没有胶囊，鼠标视差需要重算整块玻璃，
所以在指针位于玻璃上时主动把累积上限降到 12，离开后自然升回 48。
这是行为差异，不是精度损失——收敛后的图像仍然是完整的 4 paths × 48 帧累积。

---

## 十、单元测试覆盖了什么

`liquidglass-tests.exe` 共 57 项断言：

| 分组 | 覆盖内容 |
|------|----------|
| 材质契约 | 30 个参数逐值与上游对照；局部覆盖的稀疏语义；参考实例不被污染 |
| 几何 SDF | 中心覆盖率、边界 0.5 覆盖率、过渡带衰减、胶囊端头、圆角生效 |
| 颜色空间 | 正向表逐位一致、反向表 ≤ 1 色阶、端点与钳位 |
| 哈希质量 | 均值、值域覆盖、十等分桶均匀性、确定性 |
| 快速路径精确性 | 四种形态下与全路径**逐位相同** |
| 光学行为 | 折射确实发生、中心比边缘清晰、无 NaN/Inf、无负色、染色方向正确 |
| 时间累积 | 收敛态、帧号、帧间差异衰减、冻结、缓存命中、重置 |
