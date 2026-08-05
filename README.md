# Web Liquid Glass

Copyright (C) 2026 Zhuang Zichun (庄仔淳). The component source is licensed under GNU AGPL v3 or later.

一个零运行时依赖、可复用的网页液态玻璃导航组件。核心效果由 **WebGL2 + GLSL ES 3.00** 完成：组件捕获玻璃后方真实网页内容，在 GPU 中执行两次折射、RGB 色散、随机反射、轻高斯雾化与时间累积。

它不是一层 `backdrop-filter`，也不包含 CSS 毛玻璃降级。WebGL2 不可用时，组件会隐藏光学 Canvas、保留原始可访问导航，并通过 `onError` 报告原因。

## 特性

- 两次 GLSL `refract()`：分别模拟光线进入和离开玻璃。
- 圆角矩形 SDF：折射、雾化与高光共享完全相同的几何 coverage。
- 边缘反向位移：中心保持清晰，只有边缘产生透镜卷回效果。
- RGB 三通道折射率：低强度色散，不形成彩色描边。
- 4 paths/pixel + 48 帧 ping-pong 时间累积。
- 自动识别移动端 UA，并只降低光学计算密度；桌面参考画质完全不变。
- 自动采样同源图片、Canvas、页面文字、背景色和边框；导航按钮文字默认排除，避免产生折射副本。
- 鼠标悬停或手机按住拖动胶囊：宽度为导航栏 `1/5`，高度为 `1.2×`。
- 导航文字对背景做 `7 × 3` 下采样，自动选择高对比度亮色或暗色。
- 场景变化、滚动、图片加载、字体加载和尺寸变化后自动刷新。
- 原生 ES Module；不依赖 React、Three.js 或构建工具。

## 目录

```text
web-liquid-glass/
├─ src/                         核心组件
│  ├─ liquid-glass-navigation.js
│  ├─ scene-capture.js
│  ├─ shaders.js
│  ├─ layout.js
│  ├─ liquid-glass.css
│  └─ index.js
├─ types/index.d.ts             TypeScript 类型
├─ demo/                        项目官网与交互演示
├─ docs/                        架构与接入说明
├─ test/                        Node 内置测试
└─ scripts/serve.mjs            零依赖本地服务器
```

## 本地运行

要求 Node.js 18 或更高版本。不需要安装依赖。

```bash
npm run dev
```

浏览器打开：

```text
http://127.0.0.1:4173/
```

运行检查：

```bash
npm run check
npm test
```

## 最简接入（推荐）

导航栏中不需要预先创建 Canvas，也不需要重复填写参考光学参数：

```js
import { liquidGlass } from "web-liquid-glass";
import "web-liquid-glass/styles.css";

const glass = liquidGlass("#nav", {
  sceneRoot: "#app",
});
```

`liquidGlass()` 会自动查找导航栏、创建并插入 Canvas、应用项目内置的参考材质、启用移动 UA 性能档、文字自动对比和触摸拖动，然后立即启动。返回值仍是完整的 `LiquidGlassNavigation` 实例，因此之后可以调用 `setMaterial()`、`refreshScene()` 或 `dispose()`。

也可以使用对象写法：

```js
const glass = liquidGlass({
  nav: "#nav",
  scene: "#app",
});
```

## 完整类接口

### 1. HTML

Canvas 要放在导航容器内，真实按钮继续负责点击、键盘和可访问语义。

```html
<div id="app">
  <!-- 正常页面内容 -->

  <nav id="nav" class="my-nav" aria-label="主导航">
    <canvas id="glass" aria-hidden="true"></canvas>
    <button type="button">首页</button>
    <button type="button">作品</button>
    <button type="button">关于</button>
  </nav>
</div>
```

### 2. JavaScript

```js
import { LiquidGlassNavigation } from "./src/index.js";
import "./src/liquid-glass.css";

const glass = new LiquidGlassNavigation({
  navElement: document.querySelector("#nav"),
  canvas: document.querySelector("#glass"),
  sceneRoot: document.querySelector("#app"),
  onError(error) {
    console.error("WebGL2 液态玻璃不可用", error);
  },
});

glass.start();
```

组件会自动添加 `.liquid-glass-nav` 和 `.liquid-glass-canvas`。你只需要定义导航栏的位置、尺寸以及按钮排版：

```css
.my-nav {
  left: 50%;
  bottom: 28px;
  width: min(760px, 90vw);
  height: 66px;
  transform: translateX(-50%);
  display: flex;
}

.my-nav button {
  flex: 1;
  border: 0;
  background: transparent;
  color: white;
}
```

不要给导航容器添加 `background`、`border`、`box-shadow` 或额外 `backdrop-filter`，否则会遮挡真实折射。

## API

### `new LiquidGlassNavigation(options)`

必要参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `navElement` | `HTMLElement` | 导航容器 |
| `canvas` | `HTMLCanvasElement` | WebGL2 光学 Canvas |
| `sceneRoot` | `HTMLElement` | 需要采样的页面根节点；默认 `document.body` |

常用可选参数：

| 参数 | 默认值 | 说明 |
|---|---:|---|
| `maxDpr` | `1.5` | 内部渲染 DPR 上限，与参考项目一致 |
| `pathsPerPixel` | `4` | 每像素路径数；修改后会重新编译 shader |
| `maxAccumulation` | `48` | 最大时间累积帧数 |
| `capsulePathsPerPixel` | `4`（移动端 `1`） | 胶囊每像素独立路径数 |
| `capsuleMaxAccumulation` | `48`（移动端 `8`） | 胶囊独立累积帧上限 |
| `capsuleWidthRatio` | `0.2` | 悬停胶囊宽度比例 |
| `capsuleHeightScale` | `1.2` | 悬停胶囊高度倍率 |
| `captureBackground` | `#111117` | 场景捕获画布的底色 |
| `observe` | `true` | 是否自动监听 DOM 和尺寸变化 |
| `allowCrossOriginImages` | `false` | 是否尝试采样带 `crossorigin="anonymous"` 的跨域图片 |
| `captureNavigationContent` | `false` | 是否把导航按钮内容画入折射纹理；默认关闭以保持文字清晰 |
| `autoLabelContrast` | `true` | 根据按钮下方场景亮度自动选择亮色/暗色文字 |
| `labelSelector` | 按钮、链接 | 在导航容器内参与自动对比度的元素选择器 |
| `mobilePerformance` | `true` | 移动 UA 自动启用性能档；传 `false` 可禁用，或传对象覆盖单项 |
| `material` | 见下表 | 覆盖材质参数 |
| `onReady` | — | WebGL2 组件开始运行时调用 |
| `onError` | — | 初始化或运行错误回调 |
| `onStats` | — | 最多每 250ms 返回一次渲染统计 |

实例方法：

| 方法 | 说明 |
|---|---|
| `start()` | 初始化监听并开始捕获和渲染，返回是否支持 WebGL2 |
| `refreshScene()` | 页面内容变化后请求重新捕获 |
| `resize()` | 重新计算 DPR、framebuffer 和玻璃矩形 |
| `setPointer(x, y)` | 使用 0–1 局部坐标显示并移动胶囊 |
| `hideCapsule()` | 立即隐藏胶囊 |
| `getStats()` | 读取渲染器、累计帧、帧耗时和 DPR |
| `isSupported()` | 当前浏览器是否成功初始化 WebGL2 |
| `dispose()` | 释放事件、Observer、shader、纹理和 framebuffer |

## 移动端性能档

组件优先读取 `navigator.userAgentData.mobile`，并以常见移动 UA 以及 iPadOS 的触控平台特征作补充判断。命中后只替换渲染负载参数，折射率、反向位移、边缘曲率、光学厚度、色散、雾化强度与高光参数均保持参考值：

| 项目 | 桌面端 | 移动端 |
|---|---:|---:|
| 内部 DPR 上限 | `1.5` | `1.0` |
| paths/pixel | `4` | `2` |
| 最大累积帧 | `48` | `24` |
| 胶囊 paths / 累积帧 | `4 / 48` | `1 / 8` |
| 高斯核采样 | `9` | `5` |
| DOM 场景捕获间隔 | 每动画帧可刷新 | 最短 `50ms` |
| 累积纹理 | 优先 `RGBA16F` | `RGBA8` |
| 触摸胶囊 | — | 按住显示并可横向精细拖动；松手隐藏；只更新新旧胶囊区域 |

按总累积工作量估算，高 DPR 手机上的基础导航光线路径计算量约降至桌面配置的 `1/9`。胶囊移动时不会重新计算整条导航，只重绘新旧胶囊覆盖区，并使用 `1 path × 8 帧` 的独立预算，同时保留真实页面采样、双重折射、RGB 色散、文字遮罩与边缘透镜。可以按设备继续微调：

```js
const glass = new LiquidGlassNavigation({
  navElement,
  canvas,
  mobilePerformance: {
    maxDpr: 0.85,
    pathsPerPixel: 2,
    maxAccumulation: 18,
  },
});
```

## 默认材质参数

| 参数 | 值 |
|---|---:|
| 反向位移 | `2.19×` |
| 边缘曲率 | `0.67×` |
| 光学厚度 | `1.71×` |
| 光学边缘带宽 | 玻璃宽度的 `26%` |
| 折射可见区 | 玻璃宽度的 `16%` |
| 融合柔化 | 边界两侧各 `12px` |
| 毛玻璃基础强度 | `29%` |
| 毛玻璃整体衰减 | `55%` |
| 高斯采样间距 | `1.4px` |
| RGB 折射率 | `1.514 / 1.520 / 1.528` |
| 色散最大权重 | `22%` |
| 对角高光 | `0.055` |

可以通过 `material` 局部覆盖，但建议先使用默认值完成视觉验收。

组件还导出 `LIQUID_GLASS_REFERENCE`。其中记录了折射率、粗糙度、反向位移上限、光线步进、文字遮罩保护、胶囊尺寸、4 paths、48 帧累积、DPR `1.5` 以及导航栏 `85vw × 56px` 等参考实现参数；这些值位于 `src/layout.js`，不是只存在于演示页。

## 场景采样限制

DOM 不能像 WebGL framebuffer 一样被浏览器直接读取，所以组件使用一个离屏 Canvas 重绘导航栏附近的可见内容。导航容器自身默认从纹理中排除，真实按钮文字留在最上层，避免同一文字同时出现原始版和折射版。目前支持：

- CSS `background-color`。
- 同源 `<img>`，包括 `cover` 和 `contain`。
- `<canvas>`。
- 普通文本节点和真实换行位置。
- 单色边框、圆角和元素 opacity。

不会自动重绘复杂 CSS 渐变、伪元素、视频帧、滤镜、阴影或跨域无 CORS 图片。需要准确采样的复杂视觉建议使用同源 `<img>` 或 `<canvas>` 表达。

给不需要参与捕获、但会频繁变化的状态节点添加：

```html
<div data-liquid-glass-ignore>FPS: 60</div>
```

严禁把玻璃 Canvas 自己重新采样进场景；组件已经通过 `data-liquid-glass-canvas` 自动排除它。否则会产生水平条纹、曲线波纹和递归残影。

## 浏览器要求

- WebGL2。
- `requestAnimationFrame`。
- Canvas 2D。
- 建议支持 `ResizeObserver` 和 `MutationObserver`。

本项目**没有 CSS 毛玻璃降级**。WebGL2 不可用时，真实导航按钮仍然保留，但不会显示伪造的玻璃背景。应用应在 `onError` 中显示兼容提示。

## 图片署名

演示页使用 Lorem Picsum 提供的 Unsplash 图片副本：

- Alexey Topolyanskiy — [Unsplash 原图](https://unsplash.com/photos/-oWyJoSqBRM)（Picsum ID 1015）
- Andrew Ridley — [Unsplash 原图](https://unsplash.com/photos/Kt5hRENuotI)（Picsum ID 1018）
- Christian Joudrey — [Unsplash 原图](https://unsplash.com/photos/mWRR1xj95hg)（Picsum ID 1043）

组件代码使用 GNU AGPL v3 或更高版本；演示图片继续遵循其各自来源许可，图片不属于本项目的 AGPL 授权范围。详见 `LICENSE`。

## 文档

- [架构与渲染管线](./docs/ARCHITECTURE.md)
- [项目接入指南](./docs/INTEGRATION.md)
- [贡献指南](./CONTRIBUTING.md)
