# 架构与渲染管线

## 一、总览

```
        ┌──────────────────────── 系统组件（任务栏等）────────────────────────┐
        │  原生背景被 Accent 策略抹除，只剩图标与文字                          │
        └───────────────────────────────┬───────────────────────────────────┘
                                        │  explorer 绘制，天然在最上层
   ═════════════════════════════════════╪════════════════════════════════════
                                        │
        ┌───────────────────────────────▼───────────────────────────────────┐
        │  GlassSurfaceHost  ·  WS_EX_LAYERED 覆盖窗口（置顶、鼠标穿透）      │
        │                                                                    │
        │   ScreenSampler ──► uScene（sRGB，越界边界延续）                    │
        │        │                                                           │
        │        ▼                                                           │
        │   GlassScene：sRGB → 线性（256 项查找表）                           │
        │        │                                                           │
        │        ▼                                                           │
        │   CpuGlassRenderer / GlassOptics                                    │
        │   ├─ 圆角矩形 SDF                                                   │
        │   ├─ 两次 refract()（进入 / 离开）                                  │
        │   ├─ RGB 三通道色散 1.514 / 1.520 / 1.528                           │
        │   ├─ 随机菲涅耳反射（18 次幂定向光）                                 │
        │   ├─ 9 抽样高斯雾化 + 冷色偏移                                       │
        │   └─ 两条对角弱高光                                                  │
        │        │                                                           │
        │        ▼                                                           │
        │   双 float[] 乒乓 · 最多 48 帧时间累积                              │
        │        │                                                           │
        │        ▼                                                           │
        │   display pass：线性 → sRGB（4096 项查找表）                        │
        │        │                                                           │
        │        ▼                                                           │
        │   UpdateLayeredWindow（BGRA 预乘 Alpha）                            │
        └────────────────────────────────────────────────────────────────────┘
                                        │
   ═════════════════════════════════════╪════════════════════════════════════
        ┌───────────────────────────────▼───────────────────────────────────┐
        │  桌面壁纸 / 最大化窗口                                              │
        └───────────────────────────────────────────────────────────────────┘
```

关键点：**玻璃层在系统组件之下**。这是它与"顶层覆盖"方案的根本区别——
我们不去重绘任务栏的图标，而是把任务栏变透明、把玻璃垫在它下面，
让图标自然浮在玻璃上。

---

## 二、三个模式：为什么必须"垫在下面"

Windows 上让系统组件显示液态玻璃，理论上只有三条路：

| 方案 | 做法 | 结论 |
|------|------|------|
| **A. 背景接管**（本仓库采用） | 抹掉组件原生背景 + 玻璃层垫在其下方 | ✅ 图标天然清晰、零重绘、零输入拦截 |
| B. 顶层覆盖 | 玻璃层盖在组件之上 | ❌ 会遮住图标；点击穿透后图标仍被玻璃的折射副本干扰 |
| C. 系统材质替换 | 用更高强度的 Accent | ❌ Accent 只能做模糊/亚克力，无法做折射与色散 |

方案 A 成立的前提是 **Accent 透明化被系统接受**。
`liquidglass-probe --test-accent` 就是用来实测这一点的——
它逐一切换 Accent 模式并截图做像素对比，用**外观是否真的变化**作为判据，
而不是相信 API 的返回值（Win11 上 `SetWindowCompositionAttribute`
经常"返回成功但静默忽略"）。

---

## 三、抓屏回授与自排除

**这是整个 Windows 侧最容易踩坏的地方。**

玻璃层在组件下方 → 抓屏会抓到玻璃自己 → 玻璃采样到自己的上一帧 →
递归残影、水平条纹、曲线波纹。

上游对此的表述：

> *"严禁把玻璃 Canvas 自己重新采样进场景；组件已经通过 `data-liquid-glass-canvas` 自动排除它。否则会产生水平条纹、曲线波纹和递归残影。"*

### 解法一（首选）：`WDA_EXCLUDEFROMCAPTURE`

```csharp
SetWindowDisplayAffinity(overlayHandle, WDA_EXCLUDEFROMCAPTURE);
```

Windows 10 2004（Build 19041）引入。窗口**在屏幕上可见，但对所有捕获 API 不可见**。
零开销、零闪烁、零回授。

代价：你的截图（含 `Win+Shift+S`）也看不到玻璃效果。

> **v2.2 默认改用下面的解法二**：为了让普通截图能拍到玻璃，
> `scene.excludeFromCapture` 默认 `false`，即走"隐藏 → 抓屏 → 恢复"。
> 只有在录屏 / 直播等**不希望玻璃出现在画面里**时才把它设回 `true`，启用解法一。

### 解法二（回退）：隐藏 → 抓屏 → 恢复

```csharp
_overlay.Hide();
var frame = _sampler.CapturePadded(...);
_overlay.Show();
```

整个窗口在 DWM 合成一帧（约 16ms）之内完成，用户通常看不到闪烁。
代价是极小概率抓到中间帧。程序在启动时自动探测，日志里会写明用了哪条路。

---

## 四、几何与 DPI

```
目标窗口矩形  ──(DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS)──►  targetRect
                                                                            │
                            ┌───────────────────────────────────────────────┘
                            ▼
   canvasRect = targetRect                       （覆盖窗口的几何）
   glassRect  = targetRect 内缩 (insetX, insetY)  （玻璃板的几何）
   sceneRect  = canvasRect 向外扩 sceneMargin      （折射源纹理的几何）
```

| 细节 | 处理 |
|------|------|
| 用 DWM 扩展边框而不是 `GetWindowRect` | 后者包含 Win10/11 窗口四周的隐形拖拽边框，会导致玻璃比任务栏大一圈 |
| 玻璃矩形换算到画布局部坐标 | `PixelRect(x - canvas.X, y - canvas.Y, w, h)`，渲染器全程工作在局部像素空间 |
| DPI | 进程在清单里声明 PerMonitorV2，所有坐标即物理像素 |
| 多显示器 | `MonitorFromWindow` + `EnumerateMonitors`，副显示器任务栏按类名单独识别 |
| 越界采样 | `ScreenSampler.CapturePadded()` 用边界像素延续填充屏幕外区域 |

---

## 五、渲染循环

```
每 tick（默认 33ms）
  │
  ├─ 全屏检测 ──► 命中则整轮跳过（打游戏/看视频时）
  │
  ├─ 每约 1 秒重扫一次窗口列表
  │     └─ 发现启用但未接管的目标（开始菜单、搜索只在其打开时存在）
  │
  └─ 对每个已接管的界面：
        ├─ IsWindow(target)? 否 → 拆掉，等下次重扫重建
        ├─ Sync()             → 跟踪几何变化、可见性、重新贴 Z 序
        └─ Compose()          → 已收敛则直接返回（**稳态开销 0**）
```

`Compose()` 内部：

```
读指针位置（若在玻璃上 → 归一化，累积上限降到 movingAccumulation）
   ↓
CaptureScene()：抓屏 → 越界补边 → （可选）extendFromAbove → 转线性
   ↓
Renderer.Render()：命中快速路径的像素跳过 SDF/refract
   ↓
Ping-pong 累积：out = mix(current, previous, accumulation / (accumulation + 1))
   ↓
Display pass：线性 → sRGB
   ↓
UpdateLayeredWindow（就地预乘 Alpha）
```

### 为什么稳态开销是 0

上游：

```glsl
if (uCapsuleOnly < 0.5 && insideCapsule && uFrame >= CAPSULE_MAX_ACCUMULATION) {
  outColor = previous;
  return;
}
```

48 帧之后输出被冻结。本移植用 `IsConverged` + `_cachedOutput` 实现同样效果——
收敛后 `Compose()` 直接返回 `false`，连抓屏都不做。
所以空闲时的 CPU 占用只有"定时器 + 几个 Win32 查询"，接近完全静止。

---

## 六、三层缓冲

| 缓冲 | 类型 | 生命周期 | 说明 |
|------|------|----------|------|
| `uScene` | `float[]`（线性 RGB） | 每帧重建 | 由屏幕像素转换而来，**一次性**完成 sRGB→线性 |
| `_previous` / `_current` | `float[]` × 2 | 跨越 48 帧 | 乒乓累积，对应上游的 `RGBA16F` ping-pong |
| `_coverage` | `float[]` | 随累积 | 充当 Alpha 通道 |

**内存复用是刻意的**：`CpuGlassRenderer.Render()` 返回的 `BgraFrame`
是内核复用的缓冲，内容会在下次调用时被覆盖。2560×72 的画布每帧 737KB，
不做复用会在 30fps 下产生持续的中等代 GC 压力。
需要跨帧持有或比较时请先 `Clone()`——这也是回归测试里踩过的一个坑。

---

## 七、性能工程

| 优化 | 机制 | 收益 |
|------|------|------|
| **深内部快速路径** | `depth >= edgeBand` 时 `lensCurve ≡ 0`，位移恒为 0，可跳过全部 SDF 与 `refract()`。**数学上精确等价**（见 PORTING-NOTES §四） | 任务栏超半数像素命中 |
| **只栅格化包围盒** | 原本对整张 1400×900 画布跑逐像素逻辑，实际只有 1280×54 是玻璃 | ~18× |
| **sRGB 查找表** | 正向 256 项（逐位相同）、反向 4096 项（误差 ≤ 1 色阶），替代逐像素 `pow()` | 消除每帧数百万次 `pow` |
| **稳态冻结** | 收敛后直接返回缓存 | 空闲 CPU ≈ 0 |
| **`Parallel.For` 按行** | 逐像素计算天然无依赖 | 多核线性加速 |

实测（Release，4 paths/pixel）：

| 形态 | 优化前 | 优化后 | 提速 |
|------|--------|--------|------|
| 任务栏 1280×54 | 145.0 ms | **6.6 ms** | 22× |
| 开始菜单 480×560 | 173.7 ms | **25.7 ms** | 6.8× |
| 任务栏 2560×72（本机真实尺寸） | — | **13.5 ms** | — |

---

## 八、失败模式与降级

| 故障 | 检测方式 | 行为 |
|------|----------|------|
| 目标窗口不存在 | `IsWindow()` | 解除接管，下轮重扫时重建 |
| 目标被隐藏（开始菜单关闭） | `IsWindowVisible()` | 隐藏覆盖层，不做渲染 |
| Accent 透明化被拒 | `SetWindowCompositionAttribute` 返回 0 | 写日志 + 明确告知"玻璃会被遮住"，**不做视觉伪装** |
| 不支持 `WDA_EXCLUDEFROMCAPTURE` | `SetWindowDisplayAffinity` 返回 false | 自动切到"隐藏→抓屏→恢复"路径 |
| `BitBlt` 失败（DRM 全屏等） | 返回 false | 用深色底填充，渲染管线不崩 |
| 渲染循环抛异常 | `try/catch` 包住 `Tick()` | 跳过本帧并写日志，循环继续 |
| 程序被强杀 | 无法检测 | Accent 是会话级的，注销/重启必还原；也可手动 `--restore` |

设计原则：**宁可什么都不做，也不产生残缺的视觉**。
Accent 失败时程序不会硬画一块玻璃上去——那会和系统原生背景叠加，
比不做更难看。

---

## 九、目录职责

| 项目 | 职责 | 依赖 |
|------|------|------|
| `LiquidGlass.Core` | 光学、材质、图像、测试图案。零平台依赖 | 无 |
| `LiquidGlass.Win32` | 窗口发现、Accent、覆盖层、抓屏、DPI | Core |
| `LiquidGlass.App` | 托盘、配置、引擎、热键 | Core + Win32 |
| `LiquidGlass.Probe` | 侦查与 Accent 实测 | Core + Win32 |
| `LiquidGlass.Preview` | 离线出图 | Core |
| `LiquidGlass.Tests` | 回归测试 | Core |

`Core` 不引用任何 Windows API 是刻意的——
它因此可以在任何平台上跑测试，也让"光学是否正确"这件事
可以脱离系统环境独立验证。
