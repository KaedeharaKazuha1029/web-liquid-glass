# 项目接入指南

## 一行挂载（推荐）

```js
import { liquidGlass } from "web-liquid-glass";
import "web-liquid-glass/styles.css";

const glass = liquidGlass("#nav", { sceneRoot: "#app" });
```

该函数会自动创建 Canvas，并直接使用组件源码内置的参考光学参数与移动性能档。它返回完整组件实例，需要时仍可调用所有高级方法。

## 让自己的内容被折射

液态玻璃不能直接读取浏览器已经合成好的 DOM 像素。组件会把 `sceneRoot` 中、与玻璃 Canvas 可视区域相交的内容重绘到离屏 Canvas，再把它作为 `uScene` 纹理交给折射 Shader。因此，想让某个背景、图片或文字被折射，必须同时满足下面的条件。

### 1. 把内容放进 `sceneRoot`

`sceneRoot` 应指向包含页面背景、正文和图片的共同父元素。最简接口可以使用选择器或真实元素：

```html
<main id="app">
  <section class="hero">
    <h1>这段文字会进入折射纹理</h1>
    <img src="/assets/hero.jpg" alt="">
  </section>

  <nav id="nav" aria-label="主导航">
    <button type="button">介绍</button>
    <button type="button">折射</button>
    <button type="button">分层</button>
  </nav>
</main>
```

```js
const glass = liquidGlass("#nav", {
  sceneRoot: "#app",
});
```

如果使用了错误的 `sceneRoot`，或者目标元素在它的外部，即使页面上能看见该元素，它也不会出现在玻璃纹理里。目标内容还必须在屏幕坐标上经过导航栏；组件只捕获与玻璃 Canvas 相交的区域，不会截图整张网页。

### 2. 使用捕获器能够重绘的内容

默认 DOM 捕获器支持：

- CSS `background-color`、普通单色边框、圆角和元素 `opacity`。
- 同源 `<img>`，包括 `object-fit: cover` 和 `contain`。
- `<canvas>` 当前画面。
- 真实 DOM 文本节点，并按页面中的实际换行位置、字体、字号、字重和颜色重绘。

下面这些内容不能保证与浏览器最终画面一致：CSS 背景图片和复杂渐变、`::before`/`::after`、阴影、滤镜、视频帧、复杂 SVG、`clip-path`、复杂 transform，以及没有 CORS 许可的跨域图片。要求像素级准确时，应把视觉绘制到同源 `<canvas>`，或在 WebGL 应用中直接把场景颜色 Render Target 作为 `uScene`，不要依赖 DOM 重绘。

### 3. 让图片可被 Canvas 读取

优先使用项目自身 `public/assets` 中的同源图片。跨域图片必须在开始加载前设置 `crossorigin="anonymous"`，组件启用 `allowCrossOriginImages: true`，并且图片服务器返回正确的 `Access-Control-Allow-Origin` 响应头；三项缺一，图片都可能正常显示但无法进入折射纹理。

### 4. 区分页面文字和导航标签

页面正文只要是真实文本节点、位于 `sceneRoot` 内并经过玻璃区域，就会被画入 `uScene` 并参与折射。组件同时生成 `uTextMask`，只轻微抑制文字边缘的色散，不会取消文字位移。

导航按钮标签默认是例外：`captureNavigationContent` 默认为 `false`，真实 DOM 标签位于光学 Canvas 上层，以保证清晰、可点击且没有折射副本。如果设置为 `true`，导航标签也会被画入场景，但页面上原来的 DOM 标签仍然存在，通常会形成两份文字；只有在应用同时处理视觉副本时才应开启。

### 5. 内容变化后刷新场景

组件默认监听滚动、窗口尺寸、图片加载、字体加载以及 `sceneRoot` 内相关 DOM 变化。若关闭了观察器，或内容由 Canvas、WebGL、视频和自定义动画在 DOM 属性不变的情况下更新，需要在画面发生变化后主动调用：

```js
glass.refreshScene();
```

不要把液态玻璃自己的 Canvas 重新捕获进 `uScene`，否则会形成上一帧采样下一帧的反馈，表现为条纹、波纹、颜色变脏或背景消失。组件创建的 Canvas 会自动标记并排除；自定义捕获器也必须遵守这一规则。

### 接入验收清单

- 目标内容位于 `sceneRoot` 内，并在屏幕坐标上经过玻璃区域。
- 图片同源，或已完整配置 CORS。
- 需要折射的文字是真实 DOM 文本，不是伪元素生成内容。
- 动态内容更新后触发了自动捕获或 `refreshScene()`。
- 玻璃 Canvas 没有被重新采样。
- 导航标签按需求保留在最上层；不要为了折射正文而开启 `captureNavigationContent`。

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
