# 运行环境与安装

## 一、运行环境要求

### 最低配置

| 项目 | 要求 | 说明 |
|------|------|------|
| 操作系统 | Windows 10 1809（Build 17763）及以上 | `SetWindowCompositionAttribute` 可用 |
| 推荐的 OS | **Windows 10 2004（Build 19041）及以上** | 才有 `WDA_EXCLUDEFROMCAPTURE`，可避免抓屏回授 |
| 运行时 | **.NET 10 Desktop Runtime**（x64） | 见下文下载地址 |
| 权限 | **普通用户即可，不需要管理员** | 程序清单里是 `asInvoker` |
| 架构 | x64 | 项目只声明了 `x64` 平台 |
| 桌面会话 | 必须是交互式桌面会话 | 服务/远程无桌面时会话下无法工作 |

### 不需要的东西

- ❌ **不需要** Visual Studio
- ❌ **不需要** Windows App SDK / WinUI
- ❌ **不需要** 任何第三方 NuGet 包（整个仓库零第三方依赖）
- ❌ **不需要** 管理员权限
- ❌ **不需要** 修改注册表（除非你主动开启"开机自启"）
- ❌ **不需要** 替换系统文件 / 打补丁 / 禁用驱动签名

### 运行时的获取

程序是框架依赖发布（`--self-contained false`）。如果目标机器没有 .NET 10：

```
https://dotnet.microsoft.com/download/dotnet/10.0
→ .NET Desktop Runtime 10.x → x64
```

**只做构建的话**需要的是 **.NET 10 SDK**（包含运行时）。

### 构建环境

| 项目 | 要求 |
|------|------|
| .NET SDK | 10.0 或更高 |
| 目标框架 | `net10.0`（Core）/ `net10.0-windows`（Win32、App、Probe） |
| 平台 | 构建时无需 GPU、无需显示器 |

---

## 二、安装步骤

### 方式 A：从源码构建（推荐）

```bat
git clone <本仓库地址>
cd LiquidGlassWin
build.cmd
```

产物落在 `dist\`：

```
dist\app\liquidglass.exe             主程序（托盘常驻）
dist\probe\liquidglass-probe.exe     系统界面侦查工具
dist\preview\liquidglass-preview.exe 离线出图预览工具
dist\config\liquidglass.json         配置模板
dist\docs\                           文档
```

### 方式 B：直接运行

```bat
dist\app\liquidglass.exe
```

首次运行会：

1. 在 `%APPDATA%\LiquidGlass\liquidglass.json` 生成默认配置
2. 在 `%LOCALAPPDATA%\LiquidGlass\liquidglass.log` 开始写日志
3. 在系统托盘放上玻璃图标
4. 接管任务栏

---

## 三、建议的上手顺序

**不要一上来就接管**。这套东西会修改系统组件的外观，按以下顺序走一遍最稳妥：

```bat
:: ① 先看看本机有哪些系统界面，它们的矩形、进程、置顶状态
dist\probe\liquidglass-probe.exe

:: ② 实测任务栏能否被透明化（会短暂改变外观，结束时自动还原）
dist\probe\liquidglass-probe.exe --test-accent --capture

:: ③ 离线看玻璃本身长什么样（完全不碰系统）
dist\preview\liquidglass-preview.exe
::    然后打开 preview-output\panel-crop.png —— 棋盘格被弯成桶形就是对的

:: ④ 端到端自检：真实接管 → 渲染 → 截图 → 还原，并给出前后像素差
dist\app\liquidglass.exe --verify

:: ⑤ 一切正常后正式运行
dist\app\liquidglass.exe
```

---

## 四、日常操作

| 操作 | 方式 |
|------|------|
| 暂停并还原系统外观 | 双击托盘图标，或 `Ctrl+Alt+G` |
| 恢复 | 同上 |
| 重新接管 | 托盘菜单 →「重新接管」 |
| 改配置后生效 | 托盘菜单 →「重新载入配置」 |
| 完全退出并还原 | 托盘菜单 →「退出」 |
| 开机自启 | 托盘菜单 →「开机自启」（写 `HKCU\...\Run`） |

> ⚠️ **不要用任务管理器强杀进程**。正常退出才会把任务栏还原成原生外观。
> 万一强杀后任务栏一直透明，跑 `dist\probe\liquidglass-probe.exe --restore` 即可恢复。
> （Accent 修改本身是会话级的，注销或重启也一定会还原。）

---

## 五、卸载

1. 托盘菜单 →「退出」（这一步会还原系统外观）
2. 托盘菜单 →「开机自启」取消勾选，或者手动删除注册表项：
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `LiquidGlass`
3. 删除程序目录
4. 可选：删除 `%APPDATA%\LiquidGlass\` 与 `%LOCALAPPDATA%\LiquidGlass\`

**没有任何系统级残留**：不写 HKLM、不装驱动、不改系统文件。

---

## 六、兼容性说明

| 环境 | 状态 |
|------|------|
| Windows 11 22H2 / 23H2 / 24H2 / 25H2 | ✅ 已在 Build 26200 实测 |
| Windows 11 21H2 | ✅ 预期可用 |
| Windows 10 2004+ | ✅ 预期可用（有 `WDA_EXCLUDEFROMCAPTURE`） |
| Windows 10 1809–1903 | ⚠️ 可用，但会退回"隐藏→抓屏→恢复"路径 |
| Windows 10 1803 以下 | ❌ 不支持 |
| 多显示器 | ✅ 支持（含副显示器任务栏） |
| 高 DPI / 混合缩放 | ✅ 支持（Per-Monitor V2） |
| 任务栏自动隐藏 | ⚠️ 可跟随，但隐藏时会短暂闪烁，建议关闭自动隐藏 |
| 第三方任务栏美化工具（TranslucentTB 等） | ⚠️ **会冲突**，两者都在改同一个 Accent 策略，同时运行会互相覆盖 |
| 全屏独占游戏 | ✅ 自动暂停 |

---

## 七、已知限制

1. **截图能看到玻璃**（v2.2 默认）。若你反而**不想**让玻璃出现在截图/录屏里，
   把 `scene.excludeFromCapture` 设为 `true`（启用 `WDA_EXCLUDEFROMCAPTURE` 自排除）。

2. **背景越平坦，效果越不明显**。任务栏背后永远只是壁纸最下面一条，
   详见 README 的「一个必须知道的物理限制」。

3. **开始菜单的接管依赖其窗口透明化**。Windows 11 的
   `StartMenuExperienceHost` 对 `SetWindowCompositionAttribute` 的支持
   随版本波动；程序会在日志里如实报告是否成功，失败时不会产生残缺视觉。
   默认配置里开始菜单是**开启**的，如果发现它变奇怪，把它设为 `false` 即可。

4. **没有悬停胶囊**。这是刻意的设计取舍，理由见 `docs/PORTING-NOTES.md` §七。
