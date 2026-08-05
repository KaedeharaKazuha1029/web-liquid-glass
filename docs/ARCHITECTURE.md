# 架构与渲染管线

## 总览

Web Liquid Glass 把一个普通 DOM 导航拆成两个职责明确的部分：

1. 真实 DOM 按钮负责布局、点击、键盘焦点和无障碍语义。
2. 透明 WebGL2 Canvas 位于按钮视觉层上方，负责重新绘制经过玻璃光学处理的结果。

渲染管线：

```text
DOM / IMG / Canvas / Text
          │
          ▼
  Canvas2D 场景捕获 ──────► uScene (sRGB)
          │
          └───────────────► uTextMask
                                  │
                                  ▼
                    WebGL2 trace fragment shader
                    ├─ 圆角矩形 SDF
                    ├─ 两次 refract()
                    ├─ RGB 色散
                    ├─ 随机菲涅耳反射
                    ├─ 内部轻高斯雾化
                    └─ 对角弱高光
                                  │
                                  ▼
                         RGBA16F ping-pong
                         最多累计 48 帧
                                  │
                                  ▼
                      Linear → sRGB display pass
```

## 为什么不是 CSS

`backdrop-filter: blur()` 只能模糊浏览器已经合成的背景，不能让采样坐标根据曲面法线和折射率改变。本组件在片元着色器中计算折射后的坐标，再重新采样网页纹理，因此文字、图片边缘和高对比色块会在玻璃边缘真实移动。

## 圆角矩形 SDF

导航栏和胶囊都用同一个圆角矩形有符号距离函数。距离小于零代表像素在玻璃内部；距离绝对值同时提供边缘深度。SDF 数值梯度生成二维外轮廓法线。

所有按比例定义的光学距离——26% 边缘带宽、内部传播距离和 16% 可见折射区——都以玻璃矩形高度 `rect.w` 为基准。这与原始参考实现一致；使用宽度 `rect.z` 会把效果错误放大。

折射层、雾化层和高光层不使用三个不同尺寸的 DOM 元素，而是在同一次 `liquidGlass()` 调用中共享 SDF coverage。这是消除层间边界的基础。

## 折射路径

每条路径执行：

1. 根据鼠标位置构造轻微倾斜的相机射线。
2. 在弧形入口法线上执行 `refract(cameraRay, normal, 1 / IOR)`。
3. 根据光学厚度推进光线。
4. 在平面出口执行第二次 `refract(insideRay, backNormal, IOR)`。
5. 仅在边缘乘透镜曲线，加入沿外法线的反向位移。
6. 用最终坐标采样 `uScene`。

R/G/B 使用 `1.514 / 1.520 / 1.528` 三个折射率，色散权重随边缘因子衰减。

## 折射和雾化的无缝合成

折射可见区边界是玻璃宽度的 16%，边界两侧各使用 12px 平滑过渡。外缘 `blurWeight = 0`，内部逐渐变为 1。

关键点不是简单混合一张折射图和一张原图，而是让模糊采样中心连续移动：

```glsl
refractedAnchor = refractedPosition(pixel, rect, 1.520, 0.37);
blurAnchor = mix(refractedAnchor, pixel, blurWeight);
frosted = gaussianBlur(blurAnchor, 1.4);
```

这样两侧始终在追踪同一块背景内容，不会出现明显接缝。

## 时间累积

每像素 4 条路径可以保持实时交互，但随机反射会产生细小噪声。组件用两张 framebuffer 交替读写，按 `frame / (frame + 1)` 与历史结果融合，最多累计 48 帧。场景或尺寸变化时重新累计整条导航；胶囊移动则保留基础导航历史，只更新新旧胶囊覆盖区。移动端胶囊独立使用 `1 path × 8 帧`，避免一次点击让整条导航重新追踪。

优先使用 `RGBA16F`；不支持浮点颜色附件时退回 `RGBA8`。这只是 GPU 纹理格式回退，不是 CSS 视觉降级。

## 颜色空间

Canvas2D 上传的是 sRGB。trace shader 采样后立即转线性空间；折射混色、模糊、染色和累计全部在线性空间完成；最后一个 display pass 再转回 sRGB。否则边缘会发灰、过亮或过饱和。

## DOM 捕获

浏览器没有通用 API 可以直接把任意 DOM 变成可采样纹理，因此组件只重绘导航附近实际可见的部分。玻璃 Canvas 和导航容器自身必须被排除：前者防止递归反馈，后者避免真实按钮文字在折射纹理里产生第二份变形副本。

每次场景捕获后，组件把每个导航按钮下方的场景缩小到 `7 × 3` 像素并计算相对亮度。随后比较浅色和深色前景的对比度，将 `data-liquid-glass-tone="light|dark"` 写回真实 DOM 按钮。该过程读取的是现有 Canvas2D 捕获层，不需要 GPU 回读。

对于复杂应用，可替换 `DomSceneCapture`：

- Canvas/游戏：直接把已有颜色纹理传给 shader。
- WebGL/Three.js：把场景 render target 作为 `uScene`。
- 复杂 DOM：接入能够保证同源和 CORS 安全的截图管线。

## RT Core 说明

该组件是 WebGL2 片元着色器中的分析式路径追踪，不使用 NVIDIA RT Core。它通过限定玻璃形状、使用解析交点和二维场景纹理，使实时光学效果可以在普通桌面与移动 GPU 上运行。
