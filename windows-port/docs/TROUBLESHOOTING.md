# 故障排查

排查任何问题之前，先看日志：

```
%LOCALAPPDATA%\LiquidGlass\liquidglass.log
```

日志会如实记录每一个决策："已请求抹除原生背景"、"覆盖层已排除出屏幕捕获"、
"目标未透明化"……**绝大多数问题在日志里已经有答案了**。

---

## 一、任务栏没变化

### 症状
运行 `liquidglass.exe` 后任务栏和之前一模一样，托盘图标在，但看不到玻璃。

### 诊断
```bat
dist\probe\liquidglass-probe.exe --test-accent --capture
```

看输出里的"相对基线变化"列：

| 结果 | 含义 | 处理 |
|------|------|------|
| 四种模式都是 `被忽略（外观未变）` | 本机 Shell 不接受 Accent 修改 | 见下方 §1.3 |
| 有模式 `已生效 ✓` | Accent 通道正常，问题在别处 | 见 §1.2 |
| 全部 `调用被拒` | 会话或权限问题 | 见 §1.4 |

### 1.1 先确认玻璃本身是对的
```bat
dist\preview\liquidglass-preview.exe
```
打开 `preview-output\panel-crop.png`。如果棋盘格被弯成桶形 → 光学核心没问题，
问题在系统集成层。

### 1.2 Accent 生效但仍看不到玻璃

| 可能原因 | 检查方法 | 处理 |
|----------|----------|------|
| 背景太平坦 | 壁纸是不是纯色或柔和渐变？ | **这是最常见的情况**，见 README「一个必须知道的物理限制」 |
| 有别的美化工具在抢 | 任务管理器里有没有 TranslucentTB / StartAllBack / RoundedTB？ | 退出它们，程序会冲突 |
| 任务栏自动隐藏 | 设置 → 个性化 → 任务栏 → 自动隐藏有没有开？ | 关掉 |
| 玻璃被遮住 | 日志里有没有"目标未透明化"？ | 见 §1.3 |

### 1.3 日志出现「目标未透明化，玻璃层会被系统背景完全遮住」

`SetWindowCompositionAttribute` 返回了 0，说明系统静默拒绝了这次修改。

1. 确认 Windows 版本 ≥ 10.0.17763：
   ```bat
   winver
   ```
2. 如果是 Windows 11 的某个新版本移除了这个未公开入口，本程序**没有替代方案** ——
   请开一个 Issue 附上 `winver` 输出与 `liquidglass-probe --test-accent --capture`
   产生的截图目录，我们来适配。
3. **不要把 `makeTargetTransparent` 改成 `false` 再指望有效果** ——
   那样玻璃会被系统原生背景完全盖住，反而更看不到。

### 1.4 全部「调用被拒」

| 原因 | 处理 |
|------|------|
| 已在运行的实例占用了资源 | 检查托盘有没有第二个玻璃图标 |
| 不是交互式桌面会话 | 远程/服务会话下不能跑；`--diagnose` 会报告找不到任务栏 |
| 沙箱 / 容器环境 | 该环境下 Shell 组件不存在 |

---

## 二、玻璃位置错位（偏移一半 / 只覆盖一部分）

### 症状
玻璃比任务栏小一圈、偏左、或者在高 DPI 屏上整体错位。

### 原因
**进程的 DPI 感知没有生效。**

### 处理
```bat
dist\app\liquidglass.exe --diagnose
```
看"DIP 感知"一行。如果是 `UNAWARE` 或 `SYSTEM_AWARE`：

1. 确认 `liquidglass.exe.manifest` 在输出目录里（`dotnet publish` 会带上）
2. 如果是自己改过项目文件的，确认 `<ApplicationManifest>app.manifest</ApplicationManifest>` 还在
3. 混合缩放的多显示器环境必须用 `PER_MONITOR_AWARE`，`SYSTEM_AWARE` 只会按主屏缩放，
   副屏必然错位

---

## 三、任务栏出现水平条纹 / 波纹 / 递归残影

### 原因
**玻璃层采到了自己。** 这是上游专门警告过的失败模式：

> *"严禁把玻璃 Canvas 自己重新采样进场景……否则会产生水平条纹、曲线波纹和递归残影。"*

### 处理
1. 两种自排除方式**都**能避免自采：`excludeFromCapture: true`（WDA 自排除）
   或 `false`（v2.2 默认，抓屏时临时隐藏）。先确认当前用的是哪一种。
2. 看日志里这一行：
   ```
   [Taskbar] 覆盖层已排除出屏幕捕获（无回授、无抖动）。
   ```
   如果是 `本机不支持 WDA_EXCLUDEFROMCAPTURE，改用临时隐藏抓屏`，
   说明你在 Windows 10 2004 之前，或者有另一个程序抢占了 display affinity。
3. 如果开了"临时隐藏抓屏"路径又看到条纹，说明抓到了中间帧 ——
   把 `performance.tickIntervalMs` 调大（比如 100）可以降低抓到的概率。

---

## 四、鼠标划过任务栏时卡顿

### 原因
鼠标视差会重算整块玻璃。

### 处理
按影响从大到小：

```jsonc
"performance": {
  "mouseParallax": false,       // ① 彻底关掉视差，鼠标划过零开销
  "movingAccumulation": 8,      // ② 或者只降低移动时的累积上限
  "pathsPerPixel": 2,           // ③ 或者全局减半路径数
  "tickIntervalMs": 50          // ④ 或者降低轮询频率
}
```

改完在托盘菜单点「重新载入配置」。

---

## 五、CPU 占用一直很高

**正常情况下空闲时 CPU 应该接近 0**，因为 48 帧收敛后渲染会完全冻结。

如果持续占用高，按顺序检查：

| 检查 | 说明 |
|------|------|
| 是不是一直有东西在变？ | 视频播放器、动画壁纸、桌面时钟 → 每次都触发重新累积。用 `pauseWhenFullscreen` 或直接暂停 |
| 日志里有没有刷屏？ | 某个目标在"接管 → 目标消失 → 再接管"之间循环，说明窗口句柄不稳定。把那个目标设成 `enabled: false` |
| 玻璃尺寸是不是特别大？ | 开始菜单这种 480×560 的面板比任务栏贵 4 倍。必要时调低 `pathsPerPixel` |

日志里如果出现连续的
```
[StartMenu] 目标窗口已消失，解除接管。
```
那就是窗口句柄抖动，属于需要上报的 bug。

---

## 五′、替换任务栏专属问题

### 任务栏完全看不到（最重要的一个坑）

先跑 `liquidglass.exe --verify-taskbar`：它会打印**层级**与**工作区**，并留下截图。

三种成因，按可能性排序：

| 日志/现象 | 成因 | 处理 |
|---|---|---|
| 日志有 `Z 序：`，但屏幕上和截图里都找不到玻璃 | **寄生到了壁纸 `WorkerW`** —— 玻璃落在桌面图标与壁纸**之下**，渲染全对但被壁纸盖住 | 把 `taskbarReplacement.zOrder` 设回 `"desktopBottom"`（默认） |
| 日志有 `Z 序：桌面之上…`，但被最大化窗口挡住 | `reserveWorkArea` 为 `false`：工作区被释放为整屏，窗口延伸到底 | 设 `taskbarReplacement.reserveWorkArea: true`（默认） |
| 日志里根本没有 `Z 序：` 这一行 | 用的是 v2.2.0 或更早的版本 | 升级到 **v2.3.0+** |

> ⚠️ 核心矛盾要记住：**「窗口之下」与「永远可见」互斥**，
> 除非像默认配置那样**在底部预留工作区**，让最大化窗口停在任务栏上方。
> 三者（桌面层 / 不挡窗口 / 永远可见）只能同时满足两个 —— 默认配置选了后两个 + 桌面层。

### 任务栏一直闪 / 截图拍不到它

**症状**：任务栏像在轻微闪烁；用 `Win+Shift+S` 截出来的图里偶尔**完全没有任务栏**，
只剩壁纸。

**原因**：`scene.excludeFromCapture` 关掉之后（默认，为的是"截图能拍到任务栏"），
抓屏只能靠"**隐藏自己 → BitBlt → 恢复**"。而抓屏是**每个 tick 都做一次**的，
高画质档位下最高每秒 30 次 —— 于是任务栏每秒从屏幕上消失几十次。
隐藏的那几毫秒正好撞上截图，就是"拍不到"。

**v2.4.1 已修**，分三层：

| 机制 | 作用 |
|---|---|
| 免隐藏窄带探测 | 先只采"覆盖层盖不到的那条窄带"做指纹，静止桌面下**一次都不隐藏** |
| 间隔自适应 | 预留工作区（背后只有壁纸）→ 15 秒兜一次；未预留 → 400ms |
| `scene.captureMinIntervalMs` | 手动指定最小间隔（0 = 自动） |

**还能更彻底**：把 `scene.excludeFromCapture` 打开 —— 抓屏不再需要隐藏自己，
屏幕上永远不闪、截图也永远不会拍空。
代价是**任何截图（含 `Win+Shift+S`）都不会包含玻璃效果**。二选一。

**自检**：跑一次 `liquidglass.exe --verify-taskbar`，结尾那行会打印
`抓屏时隐藏/恢复 N 次` —— 静止桌面下应该是 **0~2**。
如果它一直在涨，说明背后有东西在持续变化（比如紧挨着任务栏上方有个视频/动画窗口）。

### 系统任务栏没回来 / 工作区变大

先按**紧急还原热键 `Ctrl+Alt+Shift+R`**。不行就：

```bat
:: 直接跑探针的还原（会读状态文件并修复）
dist\app\liquidglass.exe --restore
```

再不行，注销或重启一定恢复（所有改动都是会话级的）。

**为什么会这样**：程序异常退出时进程内的还原逻辑不会执行。
`%LOCALAPPDATA%\LiquidGlass\session-state.json` 记录了改动前的状态，
下次启动会自动读它修复；手动删掉这个文件也不会让问题恶化，只是失去自动修复。

### 最大化窗口没有延伸到玻璃下方

说明工作区没被释放。看日志里这一段：

```
✓ 工作区已释放为整屏（(0,0) 2560x1600）
```
或
```
✗ 压低预留未能释放工作区（仍为 ...），回滚并改用任务栏自动隐藏。
```

如果两条都失败了，把 `taskbarReplacement.releaseWorkArea` 设为 `true` 重试；
仍不行就接受"玻璃背后是壁纸"的现状（把 `releaseWorkArea` 设为 `false` 以免误导）。

### 任务栏上的应用和真实情况对不上

跑 `probe --recon`，它会**逐条打印每个窗口的判定结果与拒绝原因**，
一眼就能看出是哪个窗口多了或少了：

```
✓     API Key.txt - Notepad      Notepad.exe
×     (无标题)                    explorer.exe   进程在黑名单（explorer）
×     (无标题)                    msedge.exe     不可见
```

常见原因：
- 某个窗口被 DWM 隐身（UWP 已关闭但进程还在）→ 这是**正确**的过滤
- 某个应用没图标 → 该程序用了非标准图标来源，日志里会有说明
- 顺序和系统不一致 → 固定项顺序是 best-effort 推测的，见 TASKBAR-REPLACEMENT.md 第四节

### 任务栏宽度一直在变 / 闪烁

说明应用列表在抖动，通常是某个后台程序反复创建销毁窗口。
看日志里有没有连续的「模型刷新」。必要时把 `safetyRescanMs` 调大。

### 点图标没反应

- 目标应用以管理员身份运行 → Windows 不允许普通权限进程激活提权窗口，
  这是系统的安全设计，不是 bug。
- 应用已退出但图标还在 → 见上面「对不上」一节。

### 系统托盘（音量/网络/电池）不见了

这是**已知限制**。时钟与快捷设置入口已补上，但那几个图标需要从 explorer 进程内部
抠 `ToolbarWindow32` 的按钮，跨版本极不稳定，没有做。
日常请用 **Win+A**（快捷设置）与 **Win+N**（通知中心）。

---

## 六、截图里看不到玻璃效果

### 原因
你把 `scene.excludeFromCapture` 设成了 `true`。
**v2.2 起该项默认是 `false`（截图能看到玻璃）**；只有显式改成 `true` 才会启用
`WDA_EXCLUDEFROMCAPTURE`，代价是玻璃同时也从任何截图里消失。

### 处理
```jsonc
"scene": {
  "excludeFromCapture": false    // v2.2 默认值：截图能拍到玻璃
}
```
`false` 时抓屏走"临时隐藏 → 抓屏 → 恢复"：玻璃不采到自己，但普通截图能拍到它。
代价是每帧多一次隐藏/恢复，极低概率看到一帧闪烁。
只有在你**不希望玻璃出现在任何截图里**（例如录屏/直播不希望玻璃被录进去）时，
才把它设成 `true`。

---

## 七、程序退出后任务栏一直透明

### 处理
```bat
dist\probe\liquidglass-probe.exe --restore
```

### 预防
- 用托盘菜单的「退出」，不要用任务管理器强杀
- 保持 `behavior.restoreOnExit: true`

### 兜底
Accent 修改只作用于**当前会话**。注销或重启一定会还原，不会残留。

---

## 八、开始菜单 / 搜索出现异常

Windows 11 的 `StartMenuExperienceHost` 与 `SearchHost` 对
`SetWindowCompositionAttribute` 的支持随版本波动。表现可能是：
背景没被抹掉、抹掉后菜单内容不可见、或者菜单整体变黑。

### 处理
把它们单独关掉，只保留任务栏：

```jsonc
"startMenu":   { "enabled": false },
"search":      { "enabled": false },
"actionCenter":{ "enabled": false },
"widgets":     { "enabled": false }
```

然后托盘菜单 →「重新接管」。

日志里会如实记录每个目标的接管结果，失败时程序不会产生残缺视觉 ——
它只是不做，而不是硬画。

---

## 八′、改了配置却没效果 / 开始菜单一直有重影

### 症状
改了 `config/liquidglass.json`（或代码里的默认值），程序行为却毫无变化。
典型表现：**重影一直存在，怎么调模糊半径都没用。**

### 根因：运行时配置与仓库模板根本不是同一个文件

程序读的是：

```
%APPDATA%\LiquidGlass\liquidglass.json
```

而仓库里的 `config/liquidglass.json` **只是模板**（供参考/分发用），
程序从来不读它。两者不是同一个文件。

更隐蔽的是：如果 `%APPDATA%` 那份停留在旧版本，
**新版本新增的段落它根本没有** —— 反序列化时那些段落会**静默**保留
代码里的硬编码默认值，不报错、不警告。于是整整一段的参数全部失效。

实测踩过：`%APPDATA%` 那份没有 `glassStartMenu` 段，
于是 `scrimOpacity` 一直是 **0.30**（而不是调过的 0.72），
大面板压不住背景 —— 重影顽固存在，而模糊半径怎么调都没用。

### 诊断

```bat
liquidglass-probe.exe --config-check
```

它会打印运行时配置路径、修改时间、顶层段落，
并逐条核对关键参数"配置里有没有"以及实际取值。
最后还会对比仓库模板与运行时那份是否同一个文件。

### 处理

**自 v3.7.0 起程序会自动补齐**：`AppConfig.Load` 检测到缺失的段落
（`glassStartMenu` / `taskbarReplacement` / `performance` / `scene`）
会按当前版本默认值补入并回写，日志里会打：

```
配置补全：...\liquidglass.json 缺少或过期的项已按当前版本修正（...）
```

如果看到这一行，说明你的配置确实缺段落，且已被修好。

想手动覆盖为仓库模板：

```bat
copy /Y config\liquidglass.json "%APPDATA%\LiquidGlass\liquidglass.json"
```

⚠️ 这会丢掉你对旧配置做的所有自定义（模板里没有 `//` 以外的差异）。

### 给开发者的教训

> **配置项"没读到"和"读到了但是这个值"在运行时长得一模一样。**
> 任何会导致参数静默回退到默认值的机制，都必须有一条可核对的输出。
> `--config-check` 就是为此存在的。

---

## 九、构建失败

### `error MSB1009: 项目文件不存在`
解决方案文件是 `.slnx`（新格式），要用 `dotnet build` 或 `dotnet build LiquidGlassWin.slnx`，
不要写成 `.sln`。

### `error : Value cannot be null. (Parameter 'path1')`
**这是沙箱/特殊环境导致的，不是代码问题。** NuGet 的 `ConfigurationDefaults`
静态构造依赖 `APPDATA` / `SystemRoot` 等 Windows 环境变量。若这些变量缺失：

```bash
export SystemRoot='C:\Windows'
export APPDATA="$USERPROFILE\\AppData\\Roaming"
export ProgramData='C:\ProgramData'
export ProgramFiles='C:\Program Files'
export NUGET_PACKAGES="$(pwd)/.nuget/packages"
```

（本仓库的构建环境还额外把 NuGet 包目录指到了工作区内，
因为该环境禁止写入 `%USERPROFILE%\.nuget`。普通 Windows 上不需要这一步。）

### `dotnet` 找不到
装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0

---

## 十、收集诊断信息

上报问题时请附上：

```bat
:: 系统版本
winver

:: 环境体检
dist\app\liquidglass.exe --diagnose

:: 配置到底有没有被读进去（重影/调参无效时第一个该跑的）
dist\probe\liquidglass-probe.exe --config-check

:: 模糊的"结构抹除能力"表 + 金字塔惰性构建 + 并发安全
dist\probe\liquidglass-probe.exe --verify-blur

:: 重影对照图（假背景，修复前 vs 修复后）
dist\probe\liquidglass-probe.exe --ghost-compare

:: Accent 支持度实测（会生成截图目录）
dist\probe\liquidglass-probe.exe --test-accent --capture

:: 开始菜单实机验收（含重影量化：同一张桌面，只差一个模糊半径）
dist\app\liquidglass.exe --verify-startmenu

:: 日志
%LOCALAPPDATA%\LiquidGlass\liquidglass.log

:: 回归测试
dist\app\..\..\src\LiquidGlass.Tests\bin\Release\net10.0\liquidglass-tests.exe
```

`--verify-startmenu` 会输出重影的决定性数字，形如：

```
修复前（半径 0，点采样）结构能量 2.378
修复后（半径 96，金字塔）结构能量 0.286
→ 残余 12.0 %，抹除 88.0 %，下降 8.30 倍
```

并留下 `06-ghost-before.png` / `07-ghost-after.png` 对照图 ——
前者能看到桌面文字与任务栏图标透过玻璃，后者已被抹平。

这些加起来基本可以定位到具体环节。
