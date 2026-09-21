# HANDOVER — LiquidGlass for Windows v2.2.0

**任务**：将 [web-liquid-glass](https://github.com/KaedeharaKazuha1029/web-liquid-glass)
的液态玻璃视觉效果应用到 Windows 系统组件。

**v2 变更**：默认工作模式改为**完全隐藏系统任务栏 + 自建液态玻璃任务栏**
（高度固定、宽度随应用数量变化、每个应用图标在上名字在下、实时同步应用开关）。
v1 的"垫层"行为保留为 `"mode": "underlay"`。

**v2.2 变更**：① 任务栏改为**"桌面之上、普通窗口之下"**；
② 尺寸**整体翻倍**（高 116px / 单项宽 120px / 图标 52px）；③ 修复重建画布时的**渲染花屏**；
④ `excludeFromCapture` 默认改 `false`，**截图能拍到任务栏**。

**v2.3 变更**：① 🔴 修掉 v2.2.0 的**"任务栏看不见"事故** ——
当时用"寄生壁纸 `WorkerW`"实现"窗口之下"，结果玻璃被画到壁纸底下，
改为 **Z 序沉底**（始终可见）；② 新增 `zOrder` 配置（四档）；
③ 新增 `reserveWorkArea`（默认 `true`）—— **底部预留 132px 工作区**，
最大化窗口停在任务栏上方，于是**任务栏永远可见且不遮挡窗口**；
④ `--verify-taskbar` 自检改为与生产默认完全一致。

**v2.4 变更**：补上上游最有辨识度的交互 —— **悬停胶囊**：
鼠标在任务栏上移动时，一块宽 = 任务栏 1/5、高 = 1.2× 的**玻璃镜头**跟着指针滑行
（上下各凸出 10%，因此画布比玻璃条高 12px）。它是独立的第二个玻璃元素，
有自己的累积预算；可用 `taskbarReplacement.capsuleEnabled` 关闭。

**v2.4.1 变更**：修掉两个直接影响体感的问题 ——
① **任务栏持续闪烁、截图常常拍不到它**（抓屏路径每 tick 都隐藏自己，最高 30 次/秒；
改为"免隐藏窄带探测 + 间隔自适应"，实测 40 张连拍 0 缺失）；
② **开关应用时闪一下花屏**（`Present` 之外还多做了一次 `SetBounds`，
导致旧位图被拉伸若干帧；去掉后位置与尺寸由 `UpdateLayeredWindow` 原子设置）。

**交付日期**：2026-09-16

---

## 一、这个包是什么

| 目录 | 内容 |
|------|------|
| `README.md` | 总入口：效果说明、上手顺序、与上游的能力对照 |
| `docs/` | **TASKBAR-REPLACEMENT（v2 架构与全部实测数据）** / PORTING-NOTES（逐项移植对照）/ ARCHITECTURE（渲染管线）/ INSTALL / TROUBLESHOOTING |
| `config/liquidglass.json` | 带完整中文注释的配置模板 |
| `dist/app/liquidglass.exe` | **主程序**：托盘常驻，接管任务栏等系统界面 |
| `dist/probe/liquidglass-probe.exe` | 侦查工具：枚举系统界面 + 实测 Accent 透明化支持度 |
| `dist/preview/liquidglass-preview.exe` | 离线出图：不碰系统，直接看玻璃长什么样 |
| `dist/tests/liquidglass-tests.exe` | 57 项零依赖回归测试 |
| `shaders/glass.hlsl` | GPU 加速路径的 HLSL 实现（可选，需自行接入 D3D11 宿主） |
| 源码 | 见 `src/`（`LiquidGlass.*` 六个项目） |
| `LICENSE` / `NOTICE` | 上游 AGPL-3.0-or-later，已一并继承 |

---

## 二、怎么跑起来

```bat
:: ① 看环境
dist\probe\liquidglass-probe.exe
dist\probe\liquidglass-probe.exe --test-accent --capture

:: ② 离线看效果（安全，不碰系统）
dist\preview\liquidglass-preview.exe
::    打开 preview-output\panel-crop.png

:: ③ 端到端自检（接管 → 渲染 → 截图 → 还原）
dist\app\liquidglass.exe --verify

:: ④ 正式运行
dist\app\liquidglass.exe
```

托盘图标双击或 `Ctrl+Alt+G` 暂停/恢复。**请用托盘菜单退出**，
正常退出才会还原系统外观。

---

## 三、实现方案（一句话）

**把系统任务栏整个藏起来，自己造一条液态玻璃任务栏；玻璃直接折射它背后的桌面。**

```
① 读取系统任务栏内容
     固定项 ← %APPDATA%\...\User Pinned\TaskBar\*.lnk
     运行中 ← 六步判定链筛顶层窗口（含 DWM cloaked 检测，剔除幽灵窗口）
        ↓
② 隐藏 Shell_TrayWnd + 释放工作区
     实测：只隐藏不释放；ABM_SETPOS 是假成功；SPI_SETWORKAREA 被改回；
     只有 ABM_SETSTATE（自动隐藏）真正有效 → 采用"先试、再量、后升级"
        ↓
③ 可交互的分层窗口：宽度 = f(应用数)，高度恒定；
       SetParent 到桌面 WorkerW ⇒ 桌面之上、窗口之下
        ↓
④ BitBlt 抓桌面像素（抓屏时临时隐藏自己，不采到自己；
       默认不开 WDA_EXCLUDEFROMCAPTURE ⇒ 截图能看到玻璃）
        ↓
⑤ 光学核心：SDF → 双重 refract → RGB 色散 → 菲涅耳 → 雾化 → 48 帧累积
        ↓
⑥ 叠图标 + 小字标签 + 时钟 + 指示条 → UpdateLayeredWindow 上屏
        ↓
⑦ WinEvent 钩子 → 应用开关实时增删项
```

细节见 `docs/TASKBAR-REPLACEMENT.md`（含全部实测数据）与 `docs/ARCHITECTURE.md`。

---

## 四、实测结果

| 项目 | 结果 |
|------|------|
| 测试机 | Windows 11 Build 26200，2560×1600 @150% |
| 任务栏识别 | `Shell_TrayWnd` @ (0,1528) 2560×72 置顶 ✓ |
| Accent 支持度 | `TRANSPARENTGRADIENT` / `BLURBEHIND` / `ACRYLICBLURBEHIND` / `ENABLE_GRADIENT` **四种全部生效** |
| 端到端接管 | **替换任务栏 1084~1204×116（v2.2 尺寸翻倍），6~7 个应用** |
| **动态同步** | **新开应用 6→7 个、宽 1084→1204px（+120）；关闭回到 6 / 1084px。高度全程不变** ✓ |
| **Z 序** | **寄生桌面 `WorkerW`：桌面图标/壁纸之上、所有普通窗口之下** ✓ |
| **可截图** | **`excludeFromCapture=false` 默认，普通截图能看到玻璃** ✓ |
| **还原完整性** | 工作区 / 任务栏位置 / 自动隐藏开关 / 状态文件**全部复原** ✓ |
| 回归测试 | **57 / 57 通过** |
| 构建 | 0 警告 0 错误，**零第三方 NuGet 依赖** |

性能（Release，4 paths/pixel）：

| 形态 | 尺寸 | 优化前 | 优化后 |
|------|------|--------|--------|
| 任务栏 | 1280×54 | 145.0 ms | **6.6 ms**（22×） |
| 开始菜单 | 480×560 | 173.7 ms | **25.7 ms**（6.8×） |
| 任务栏（实机） | 2560×72 | — | **13.5 ms** |

稳态开销 **0** —— 上游在 48 帧后冻结输出，本移植同样。

---

## 五、移植保真度

| 上游能力 | 状态 |
|----------|------|
| 圆角矩形 SDF（折射/雾化/高光共享 coverage） | ✅ 逐行 |
| 两次 `refract()` | ✅ 逐行 |
| RGB 折射率 1.514 / 1.520 / 1.528 | ✅ 逐值 |
| 边缘反向位移 + 透镜曲线 | ✅ 逐行 |
| 随机菲涅耳反射（18 次幂定向光） | ✅ 逐行 |
| 9 抽样高斯雾化 + 冷色偏移 | ✅ 权重逐个 |
| 两条对角弱高光 | ✅ 逐行 |
| 4 paths/pixel + 48 帧时间累积 | ✅ 逐行 |
| 线性空间 / sRGB 双向转换 | ✅ 一致（CPU 用查找表） |
| 文字遮罩保护 | ✅ 机制保留（Windows 上图标在玻璃之上，遮罩默认全 0） |
| 鼠标驱动相机射线视差 | ✅ 保留（可关） |
| 悬停胶囊（跟随指针的玻璃镜头） | ✅ v2.4 起实现（1/5 宽 × 1.2 高，独立累积预算，见 `docs/PORTING-NOTES.md` §七） |

**新增的唯一优化**（深内部快速路径）已证明与全路径**逐位相同**，
证明过程见 `docs/PORTING-NOTES.md` §四，回归测试覆盖四种形态。

---

## 六、需要你知道的四件事

### 0. ⚠️ 紧急还原热键 Ctrl+Alt+Shift+R

这个功能会**隐藏你的系统任务栏**。万一界面卡死，按 **Ctrl+Alt+Shift+R**
立即还原并退出。所有改动都是会话级的，注销或重启也一定恢复。
程序还会把改动前的状态写进 `%LOCALAPPDATA%\LiquidGlass\session-state.json`，
即使被强杀，下次启动也会自动修复。

### 1. 任务栏背后永远只是"壁纸最下面那一条"

Windows 的窗口不会延伸到工作区之外，所以任务栏盖住的那 72px 背后只可能是壁纸。
**壁纸越平坦，折射越没有内容可弯折，玻璃看起来就越接近"只是变透明"。**

这不是缺陷，是场景频率不够。三种应对（README 有表格）：
换细节丰富的壁纸 / 开 `scene.source = "extendFromAbove"` / 调低 `material.tintMix`。

想看这块玻璃在**频率充足**场景下的真实样子，跑 `liquidglass-preview.exe`，
看 `preview-output\panel-crop.png` —— 棋盘格被弯成桶形的样子就是签名特征。

### 2. 默认配置下截图**能**拍到玻璃（v2.2 起）

`scene.excludeFromCapture` 默认 `false`：抓屏瞬间临时隐藏玻璃层，因此不会采到自己；
而普通截图（含 `Win+Shift+S`）里**能看到玻璃**。
想恢复"截图里看不到玻璃"的旧行为，把该项设为 `true`。

### 2′. 任务栏的层级："桌面之上、窗口之下"，且窗口不会越过它

任务栏待在**普通窗口带的最底部**（桌面之上），并且**在底部预留工作区**（默认 132px）——
最大化窗口因此停在它上方。结果是它**永远可见、也不遮挡任何窗口**。

| 想要的效果 | 配置 |
|---|---|
| 现在这样（桌面层 + 永远可见 + 不挡窗口） | `zOrder: "desktopBottom"` + `reserveWorkArea: true`（默认） |
| 浮在所有窗口之上（v2.1 行为） | `zOrder: "topMost"` |
| 纯桌面小组件（被窗口盖住，窗口延伸到底） | `reserveWorkArea: false` |
| ⚠️ 别用 | `zOrder: "behindDesktopIcons"` —— 寄生壁纸 `WorkerW`，会落到桌面图标/壁纸**之下**、看起来完全不见（v2.2.0 的真实事故） |

### 3. 与第三方任务栏美化工具冲突

TranslucentTB / StartAllBack / RoundedTB 之类都在改同一个 Accent 策略，
同时运行会互相覆盖。**先退出它们。**

### 4. 系统托盘（音量/网络/电池图标）没有还原

隐藏系统任务栏后它们会一起消失。已补上**时钟 + 快捷设置入口**，
但那几个图标需要从 explorer 进程内部抠 `ToolbarWindow32` 的按钮，
跨 Win7/10/11 极不稳定，没有做。日常请用 **Win+A**（快捷设置）与 **Win+N**（通知中心）。

---

## 七、还原与卸载

| 情况 | 做法 |
|------|------|
| 正常 | 托盘菜单 →「退出」 |
| 进程被强杀 | `dist\probe\liquidglass-probe.exe --restore` |
| 任何情况 | 注销或重启 —— Accent 是会话级的，必还原 |
| 开机自启 | 托盘菜单取消勾选，或删 `HKCU\...\Run` 下的 `LiquidGlass` |

**没有系统级残留**：不写 HKLM、不装驱动、不改系统文件。

---

## 八、构建

```bat
build.cmd
```

需要 **.NET 10 SDK**（https://dotnet.microsoft.com/download/dotnet/10.0）。
没有其它前置条件 —— 整个仓库零第三方 NuGet 依赖，
`dotnet build` 之后就能直接跑测试与程序。

> 如果你在沙箱 / 受限容器里构建遇到
> `error : Value cannot be null. (Parameter 'path1')`，
> 那是 NuGet 的 `ConfigurationDefaults` 拿不到 `APPDATA` / `SystemRoot` 环境变量，
> 与代码无关，解决方案见 `docs/TROUBLESHOOTING.md` §九。

---

## 九、许可

**GNU AGPL-3.0-or-later**，与上游一致。完整许可见 `LICENSE`。

上游版权：Copyright (C) 2026 Zhuang Zichun（庄仔淳）
上游项目：https://github.com/KaedeharaKazuha1029/web-liquid-glass

⚠️ AGPL 的关键义务：**把本程序作为网络服务提供给他人时，必须向使用者提供完整源码。**
本程序是本地桌面应用，通常不触发该条款；但若你把它改造成服务端渲染或远程桌面方案，
请留意这条。
