# Windows 移植分支（`windows-port`）

本分支在**原项目完全保持原样**的基础上，新增一个把液态玻璃光学效果
移植到 **Windows 系统界面**的实现，全部代码位于 [`windows-port/`](./windows-port)。

原项目（本分支的上游）：
**[KaedeharaKazuha1029/web-liquid-glass](https://github.com/KaedeharaKazuha1029/web-liquid-glass)**
—— 跑在浏览器里的 Canvas / DOM 折射实现。

---

## 这个分支加了什么

`windows-port/` 是一份 **C# / .NET 10** 的独立实现，把同一套光学模型
（边缘透镜折射、RGB 色散、菲涅耳反射、双对角高光、时间累积去噪）
搬到 Windows 上，并直接接管系统界面：

| 组成 | 说明 |
|------|------|
| **液态玻璃任务栏** | 用真折射的玻璃覆盖层替换系统任务栏（宽度随应用数伸缩，高度恒定） |
| **液态玻璃开始菜单** | 自研开始菜单：固定项 / 推荐 / 全部应用滚动视口 / 账户与电源 |
| **液态玻璃快捷面板** | 点托盘箭头或齿轮弹出的玻璃浮层（网络·蓝牙·飞行模式·音量·专注·所有设置） |
| **悬停胶囊** | 上游最有辨识度的交互：宽 = 任务栏 1/5、高 1.2× 的玻璃镜头跟随指针 |

当前版本 **v3.8.0**，变更见 [`windows-port/CHANGELOG.md`](./windows-port/CHANGELOG.md)。

---

## 快速开始

```bat
cd windows-port
build.cmd            :: 构建 Release
build.cmd test       :: 构建并跑回归测试（57 项断言）
```

前置条件：**只需要 .NET 10 SDK** —— 整份实现零第三方 NuGet 依赖。

命令行自检（会短暂接管任务栏，结束自动还原）：

```bat
liquidglass.exe --verify-taskbar   输出目录     :: 任务栏接管 + 保真度截图
liquidglass.exe --verify-startmenu 输出目录     :: 开始菜单 + 重影量化
liquidglass.exe --config-check                  :: 核对实际生效的配置
```

保命键：**Ctrl + Alt + Shift + R** —— 任何时候按下立即还原系统任务栏。

---

## 移植中值得记录的三件事

1. **折射源从 DOM 换成屏幕像素。** 上游在浏览器里把导航栏背后的 DOM 重绘进
   Canvas；Windows 上没有 DOM，等价物就是屏幕本身（抓屏 + 自排除，
   避免抓到自己形成折射递归）。

2. **模糊必须做成多级降采样金字塔，而不是逐像素核。**
   抑制周期为 `T` 的背景结构需要约 `T/2` 的支撑半径，与核形状无关；
   逐像素算大核的代价随半径平方增长，金字塔把它变成 O(1)。
   （细节见 `windows-port/docs/PORTING-NOTES.md`）

3. **文字与图标永远画在玻璃之后（之上），不参与折射。**
   与上游把真实 DOM 按钮留在最上层的做法一致，
   避免出现"清晰的 + 折射变形的"两份文字。

---

## 许可

**GNU AGPL-3.0-or-later**，与上游一致。
上游版权：Copyright (C) 2026 Zhuang Zichun（庄仔淳）。
完整许可见 [`LICENSE`](./LICENSE) 与 [`windows-port/NOTICE`](./windows-port/NOTICE)。
