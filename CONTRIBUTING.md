# Contributing

感谢参与 Web Liquid Glass。

## 开发流程

```bash
npm run dev
npm run check
npm test
```

项目没有运行时依赖。提交新依赖前，请先说明它解决的不可替代问题。

## 变更原则

- 保持核心组件与框架无关。
- 不加入 CSS 毛玻璃作为视觉降级。
- 不把玻璃 Canvas 捕获回 `uScene`。
- 光学混色必须在线性色彩空间进行。
- 新增材质参数时同步更新类型、README、架构文档和测试。
- 不提交无明确来源和许可的演示图片。

## Pull Request

PR 应说明：

1. 修改了哪一段渲染管线。
2. 是否改变默认视觉参数。
3. 在何种浏览器和 GPU 上验证。
4. 是否影响 DOM 捕获、CORS 或移动端性能。

