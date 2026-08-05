# 项目接入指南

## 一行挂载（推荐）

```js
import { liquidGlass } from "web-liquid-glass";
import "web-liquid-glass/styles.css";

const glass = liquidGlass("#nav", { sceneRoot: "#app" });
```

该函数会自动创建 Canvas，并直接使用组件源码内置的参考光学参数与移动性能档。它返回完整组件实例，需要时仍可调用所有高级方法。

## 最小步骤

1. 把 `src/` 复制到项目中，或从 GitHub 仓库安装。
2. 引入 `src/liquid-glass.css`。
3. 在导航栏内部增加一个 Canvas。
4. 创建 `LiquidGlassNavigation`，传入导航、Canvas 和场景根节点。
5. 调用 `start()`。

## 图层顺序

组件的 Canvas 必须覆盖导航栏以及胶囊上下溢出的区域。默认样式让 Canvas 高度为导航栏的 140%，而胶囊高度为 120%，因此不会被裁切。

导航按钮保持为真实 DOM 交互层，默认不会被画入 `uScene`。这能让标签文字始终清晰，同时避免玻璃纹理内出现第二份被折射的文字。只有明确需要折射导航内容时才设置 `captureNavigationContent: true`。

默认开启 `autoLabelContrast`：组件会对每个按钮下方的场景进行低分辨率采样，并写入 `data-liquid-glass-tone="light"` 或 `"dark"`。可通过 `--liquid-glass-label-color` 覆盖最终文字颜色。

Canvas 使用 `pointer-events: none`。点击事件会穿过它，到达真实 DOM 按钮。

## 页面切换

如果框架在切页后更新 DOM，但 MutationObserver 被关闭，需要手动调用：

```js
glass.refreshScene();
```

在 React 中应在组件挂载后创建实例，并在 effect cleanup 中调用 `dispose()`。不要在每次 render 时重复创建。

## 图片

最稳定的方案是把需要折射的图片放进站点自身的 public/assets 目录。跨域图片即使能显示，也可能因为没有正确 CORS 响应而污染 Canvas。

如果确实要采样跨域图片：

```html
<img crossorigin="anonymous" src="https://example.com/image.jpg" alt="">
```

并设置：

```js
allowCrossOriginImages: true
```

远端服务器还必须返回允许当前来源读取的 CORS 头。

## 高频状态

FPS、时钟或实时数据若每帧修改 DOM，会导致 MutationObserver 不断重新捕获。给这些节点添加：

```html
<span data-liquid-glass-ignore>60 FPS</span>
```

## 性能

- 默认 DPR 上限 1.5，不建议在手机上使用完整设备 DPR。
- 默认 4 paths/pixel；2 可以换取更低功耗，6–8 适合高性能桌面 GPU。
- 移动 UA 默认让基础导航使用 `2 paths × 24 帧`，胶囊使用独立的 `1 path × 8 帧`，且胶囊移动只重绘新旧覆盖区域。
- 手机端使用 Pointer Capture：按住后可横向拖动，移动事件按动画帧合并，松手立即隐藏胶囊。
- 时间累积结束后组件停止主动渲染，不会无限占用 60 FPS。
- 页面滚动会刷新捕获和累计；复杂长页面应减少导航栏覆盖区域内的 DOM 数量。
- 组件 Canvas 应只覆盖导航区域，不要无必要地扩展到全屏。

## 不支持 WebGL2

本项目不提供 CSS 毛玻璃降级。使用 `onError` 给用户显示清晰提示：

```js
onError(error) {
  compatibilityMessage.hidden = false;
  compatibilityMessage.textContent = "需要支持 WebGL2 的现代浏览器";
}
```

真实导航仍然可点击，只是不显示液态玻璃。

## 常见故障

### 出现水平条纹或递归波纹

检查是否把 WebGL Canvas 画回了场景纹理。确保 Canvas 带 `data-liquid-glass-canvas`，自定义捕获器也必须排除它。

### 折射区域看起来只有模糊

检查 `blurWeight` 在玻璃外缘是否为 0，以及是否额外给导航栏添加了 `backdrop-filter`。

### 折射和内部毛玻璃有硬边

确认模糊中心使用 `mix(refractedAnchor, pixel, blurWeight)`，而不是直接混合两个坐标完全无关的画面。

### 图片能显示但玻璃里没有

检查图片是否同源、是否已完成加载，以及自定义场景根节点是否包含该图片。

### 颜色发灰或过亮

确认 trace pass 使用 sRGB → Linear，display pass 使用 Linear → sRGB，并保持 `premultipliedAlpha: false`。
