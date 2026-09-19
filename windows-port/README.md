# LiquidGlass for Windows

把 [web-liquid-glass](https://github.com/KaedeharaKazuha1029/web-liquid-glass) 的液态玻璃光学效果移植到 **Windows 系统界面**。

**默认工作模式是「替换任务栏」**：完全隐藏系统任务栏，由本程序绘制一条液态玻璃任务栏——
高度固定、宽度随应用数量变化，每个应用**图标在上、名字在下**，
点击可切换 / 启动应用，新开或关闭应用时实时跟着变。
任务栏待在**「桌面之上、所有普通窗口之下」**这一层，并且**在屏幕底部预留工作区**
（默认 132px）—— 于是最大化窗口会停在任务栏上方，**任务栏永远可见、也不遮挡任何窗口**。
玻璃直接折射它背后的桌面内容。
鼠标划过时，一块**宽 = 任务栏 1/5、高 = 1.2 倍的玻璃镜头**（上游的「悬停胶囊」）
会跟着指针滑行 —— 它上下各凸出 10%，是独立渲染的第二个玻璃元素。
尺寸、层级与胶囊都可在 `config/liquidglass.json` 调整（**默认高度 116px、单项宽 120px**）。

不是 `backdrop-filter`，不是系统亚克力，也不是贴一张模糊图。玻璃层会**真实采样它背后的像素**，在 CPU（或可选 GPU）上执行两次 `refract()`、RGB 三通道色散、随机菲涅耳反射、轻高斯雾化，并做最多 48 帧的时间累积。

> This is not `backdrop-filter`, not the system acrylic material, and not a blurred snapshot. The glass slab genuinely samples the pixels behind it, runs a double `refract()`, RGB dispersion, stochastic Fresnel reflection and a light Gaussian frost, then accumulates up to 48 frames.

> `backdrop-filter` ではありません。システムのアクリル素材でも、ぼかし画像の貼り付けでもありません。ガラス板は背後のピクセルを実際にサンプリングし、二度の `refract()`、RGB 分散、確率的フレネル反射、軽いガウスぼかしを実行し、最大 48 フレーム分を時間積分します。

---

## 一分钟上手

```bat
:: 1. 构建（只需 .NET 10 SDK，无需任何第三方包）
build.cmd

:: 2. 先看本机环境与系统能力
dist\probe\liquidglass-probe.exe --recon

:: 3. 离线看玻璃长什么样（不碰系统，纯出图）
dist\preview\liquidglass-preview.exe

:: 4. 端到端自检：接管 → 渲染 → 截图 → 还原（约 12 秒，会自动还原）
dist\app\liquidglass.exe --verify-taskbar

:: 5. 验证动态同步：新开/关闭应用时任务栏是否跟着变
dist\app\liquidglass.exe --verify-dynamic

:: 5′. 开始菜单实机验收（含重影量化：同一张桌面，只差一个模糊半径）
dist\app\liquidglass.exe --verify-startmenu

:: 6. 正式运行（托盘常驻）
dist\app\liquidglass.exe
```

### 效果不好时，先跑这三条

```bat
:: 配置到底有没有被读进去（改了参数没效果 / 重影顽固时第一个该跑的）
dist\probe\liquidglass-probe.exe --config-check

:: 模糊的"结构抹除能力"表 + 金字塔惰性构建 + 并发安全
dist\probe\liquidglass-probe.exe --verify-blur

:: 重影对照图（假背景，修复前 vs 修复后）
dist\probe\liquidglass-probe.exe --ghost-compare
```

> ⚠️ **配置陷阱**：程序读的是 `%APPDATA%\LiquidGlass\liquidglass.json`，
> 而仓库里的 `config\liquidglass.json` **只是模板**，两者不是同一个文件。
> 更隐蔽的是老配置可能**缺少新版本新增的段落** —— 那不会报错，
> 只会让整段参数静默回退到代码默认值。
> v3.7.0 起程序会自动补齐并写日志，用 `--config-check` 可以随时核对。

| 操作 | 快捷键 |
|------|--------|
| 暂停 / 恢复 | 双击托盘图标，或 **Ctrl+Alt+G** |
| **紧急还原系统任务栏并退出** | **Ctrl+Alt+Shift+R** |
| 开始菜单 | 点玻璃条最左的 Windows 徽标，或 Win 键 |
| 快捷设置（音量/亮度/WiFi） | 点最右的齿轮，或 Win+A |
| 通知中心 | 点时钟，或 Win+N |

> ⚠️ **请务必记住 Ctrl+Alt+Shift+R**。这个功能会隐藏你的系统任务栏，
> 万一界面卡死，它是你拿回任务栏的保命键。所有改动都是会话级的，
> 注销或重启也一定会恢复。

---

## 它是怎么做到的

上游组件在浏览器里把导航栏背后的 DOM 重绘进一张 Canvas 当作折射源。Windows 上没有 DOM，但有等价物——**屏幕像素本身**。

```
① 读取系统任务栏的内容
     固定项 ← %APPDATA%\...\User Pinned\TaskBar\*.lnk
     运行中 ← 枚举顶层窗口，按 Explorer 的规则逐条筛选
              （可见 / 非工具窗口 / 无 owner / 非 DWM 隐身 / 有标题）
                    ↓
② 隐藏系统任务栏 + 释放工作区
     ShowWindow(Shell_TrayWnd, SW_HIDE)
     实测工作区是否释放，没释放就自动升级为开启任务栏「自动隐藏」
     ⇒ 最大化窗口铺到屏幕底边，玻璃浮在窗口之上
                    ↓
③ 创建可交互的 WS_EX_LAYERED 覆盖窗口，宽度 = f(应用数量)，高度恒定
                    ↓
④ BitBlt 抓取玻璃背后的桌面像素（抓屏瞬间玻璃层临时隐藏，不会采到自己；
   默认**不**启用 WDA_EXCLUDEFROMCAPTURE，所以普通截图能拍到玻璃）
                    ↓
⑤ 光学核心：SDF → 双重折射 → 色散 → 菲涅耳 → 雾化 → 48 帧累积
                    ↓
⑥ 在玻璃之上叠图标、名字、时钟与指示条
                    ↓
⑦ UpdateLayeredWindow 逐像素 Alpha 上屏
                    ↓
⑧ 应用开关 → WinEvent 钩子 → 重扫 → 任务栏实时增删项
```

**图标与文字画在玻璃之后**，相当于浮在玻璃表面——这与上游把真实 DOM 按钮留在最上层的做法一致，也避免了文字出现"清晰的 + 折射变形的"两个副本。

---

## 已验证的效果

| 项目 | 结果 |
|------|------|
| 测试机 | Windows 11 Build 26200，2560×1600 @150%，单显示器 |
| 任务栏识别 | `Shell_TrayWnd` @ (0,1528) 2560×72，置顶 ✓ |
| 系统任务栏隐藏 | ✓ 完全隐藏，工作区释放为整屏 2560×1600 |
| **层级** | **桌面之上、所有普通窗口之下，且底部预留 132px 工作区**（窗口不越过它 → 任务栏始终可见）✓ |
| **替换任务栏渲染** | **玻璃胶囊 1084~1204×116（v2.2 尺寸翻倍），6~7 个应用图标 + 标签 + 时钟** |
| **动态同步** | **新开应用 → 应用数 6→7、面板宽 1084→1204px（+120px）；关闭 → 回到 1084px。高度全程不变** ✓ |
| **可截图** | **默认 `excludeFromCapture=false`，`Win+Shift+S` 等普通截图能拍到玻璃** ✓ |
| **悬停胶囊** | **鼠标划过时一块「宽 1/5、高 1.2×」的玻璃镜头跟着指针滑行** ✓ |
| **实时渲染** | **玻璃背后的画面每变一次就重画一次**（四次变化实测像素差 123 / 94 / 31 / 62）✓ |
| **性能自适应** | **启动实测 14.6ms → 自动选定 High 档（4 paths × 48 帧 × 1.25× 超采样），实测稳定 7.7ms/帧** ✓ |
| 还原完整性 | 工作区、任务栏位置、自动隐藏开关、状态文件**全部复原** ✓ |
| 回归测试 | **57 项断言全部通过** |
| 光学保真 | 深内部快速路径与全路径**逐位相同**（已证明） |

性能实测（Release，4 paths/pixel，单帧冷启动）：

| 形态 | 尺寸 | 耗时 | 等效帧率 |
|------|------|------|----------|
| 任务栏（本机真实尺寸） | 2560×72 | 13.5 ms | ~74 fps |
| 任务栏（预览场景） | 1280×54 | 6.6 ms | ~151 fps |
| 开始菜单 | 480×560 | 25.7 ms | ~39 fps |

> 稳态开销是 **0**：上游在 48 帧后冻结输出（`outColor = previous`），我们也一样。
> 只有场景变化、几何变化或鼠标划过玻璃时才会重新计算。

---

## 实时渲染与画质自适应

玻璃**不是渲染一次就冻住的**。每帧抓取背后的画面并算场景指纹，
变了就立刻重画 —— 窗口在玻璃后面移动、视频播放、切换壁纸，玻璃都跟着走。
静止时玻璃层直接复用，开销接近 0。

画质按电脑性能自动分档，参数直接对齐上游的桌面参考值与移动降级档：

| 档位 | 路径数 | 静止累积 | 超采样 | 说明 |
|------|-------|---------|--------|------|
| minimal | 1 | 12 帧 | 1.00× | 上网本 / 远程桌面 |
| low | **2** | **24 帧** | 1.00× | **上游移动端档位** |
| balanced | 3 | 32 帧 | 1.00× | 均衡 |
| high | **4** | **48 帧** | **1.25×** | **上游桌面参考档** |
| ultra | 8 | 64 帧 | 1.50× | 高配机 |

**启动时用真实负载实测一次**定初始档位（不看 CPU 核数猜 —— 同样 8 核，
插电与用电池、集显轻薄本与台式机能差三五倍），之后运行时闭环按实测帧耗时动态升降档。

> ⚠️ 无论哪一档，**光学材质完全一致** —— 折射率、色散、边缘带宽不变，
> 变的只是"工作量"。这是上游定下的纪律：低配机器上的玻璃和顶配机器上的必须是同一块玻璃。

细节见 `docs/PERFORMANCE.md`。

---

## ⚠️ 一个必须知道的物理限制

**任务栏背后永远是"壁纸的最下面那一条"。**

Windows 的窗口不会延伸到工作区之外，所以无论桌面上开着什么，任务栏盖住的那 72 像素背后只可能是壁纸。如果壁纸是平滑渐变（比如纯色底或柔和渐变），折射就**没有内容可以弯折**——玻璃看起来只会"变透明"。

这不是实现缺陷，是场景频率不够。三种应对：

| 做法 | 效果 |
|------|------|
| 换一张细节丰富的壁纸（照片、纹理） | 折射立刻显现，边缘卷回清晰可见 |
| `scene.source = "extendFromAbove"` | 把玻璃上方未被遮挡的内容延伸到玻璃下面，最大化窗口时特别有效 |
| 调低 `material.tintMix`（如 0 或 0.05） | 亮色壁纸下发灰的问题会明显改善 |

想看这块玻璃在**频率充足**的场景下是什么样，跑 `liquidglass-preview.exe`——它用合成测试场景渲染，`panel-crop.png` 里棋盘格被弯成桶形的样子就是液态玻璃的签名特征。

---

## 目录结构

```
LiquidGlassWin/
├─ src/
│  ├─ LiquidGlass.Core/          零依赖光学核心
│  │   ├─ LiquidGlassMaterial.cs   材质（逐值对应上游 layout.js）
│  │   ├─ GlassOptics.cs           SDF / 双重折射 / 色散 / 菲涅耳 / 雾化
│  │   ├─ CpuGlassRenderer.cs      参考渲染器 + 双缓冲时间累积
│  │   ├─ GlassScene.cs            线性空间折射源与 sRGB 双向转换
│  │   ├─ BgraFrame.cs / PngWriter.cs
│  │   └─ TestPatterns.cs
│  ├─ LiquidGlass.Win32/          Win32 互操作层
│  │   ├─ SystemSurfaceLocator.cs  任务栏 / 开始菜单 / 搜索… 的发现与定位
│  │   ├─ CompositionAttribute.cs  SetWindowCompositionAttribute 封装
│  │   ├─ LayeredOverlayWindow.cs  逐像素 Alpha 的点击穿透置顶窗口
│  │   ├─ GlassSurfaceHost.cs      ★ 把三者焊在一起的中枢
│  │   ├─ ScreenSampler.cs         屏幕采样（含越界边界延续）
│  │   └─ DisplayEnvironment.cs    DPI 感知与显示器枚举
│  ├─ LiquidGlass.App/            托盘常驻主程序
│  ├─ LiquidGlass.Probe/          系统界面侦查与 Accent 实测工具
│  ├─ LiquidGlass.Preview/        离线出图预览工具
│  └─ LiquidGlass.Tests/          57 项回归测试（零依赖）
├─ shaders/glass.hlsl             GPU 路径的 HLSL 实现（可选）
├─ config/liquidglass.json        带完整注释的配置模板
├─ docs/
│  ├─ INSTALL.md                  运行环境与安装
│  ├─ ARCHITECTURE.md             渲染管线与设计取舍
│  ├─ PORTING-NOTES.md            ★ WebGL2 → Win32 的逐项移植对照
│  └─ TROUBLESHOOTING.md          故障排查
├─ build.cmd / build.sh
└─ HANDOVER.md                    交付说明
```

---

## 与上游的关系

| 上游能力 | 本移植的状态 |
|----------|--------------|
| 悬停高光 + 前台指示条 | ✅ **v2 新增**：应用项悬停有玻璃高光，前台应用有更亮的指示条 |
| 圆角矩形 SDF（折射/雾化/高光共享 coverage） | ✅ 逐行移植 |
| 两次 `refract()`（进入 / 离开玻璃） | ✅ 逐行移植 |
| RGB 三通道折射率 1.514 / 1.520 / 1.528 | ✅ 逐值一致 |
| 边缘反向位移 + 透镜曲线 | ✅ 逐行移植 |
| 随机菲涅耳反射（18 次幂定向光） | ✅ 逐行移植 |
| 9 抽样高斯雾化 + 冷色偏移 | ✅ 权重逐个一致 |
| 两条对角弱高光 | ✅ 逐行移植 |
| 4 paths/pixel + 48 帧时间累积 | ✅ 逐行移植 |
| 线性空间计算 / sRGB 显示 | ✅ 一致（CPU 端用查找表加速） |
| 文字遮罩保护 | ✅ 机制保留（Windows 上图标在玻璃之上，故遮罩默认全 0） |
| 鼠标驱动相机射线视差 | ✅ 保留（可关） |
| 悬停胶囊（跟随指针的玻璃胶囊） | ⛔ **刻意未实现**，理由见 PORTING-NOTES |
| WebGL2 不可用时的降级 | ✅ 对应为 Accent 失败时的明确报错与回退 |

材质参数的来源与出处见 `docs/PORTING-NOTES.md`，那里有每一个数字对应的上游行号。

---

## 许可

本移植基于 **GNU AGPL-3.0-or-later** 许可，与上游一致。完整许可见 `LICENSE`。

上游版权：Copyright (C) 2026 Zhuang Zichun（庄仔淳）。
上游项目：https://github.com/KaedeharaKazuha1029/web-liquid-glass

再次提醒 AGPL 的关键义务：**如果你把它作为网络服务提供给他人，必须向使用者提供完整源码。**
