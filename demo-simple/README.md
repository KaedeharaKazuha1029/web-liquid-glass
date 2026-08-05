# Simple API test

这个目录完整复制 `demo/` 的页面和资源，但不预先创建液态玻璃 Canvas，也不加载独立的演示渲染脚本。

唯一的组件接入代码位于 `main.js`：

```js
const glass = liquidGlass("#bottomNav", {
  sceneRoot: "#sceneRoot",
});
```

访问 `/demo-simple/` 可以验证 Canvas 自动创建、参考参数、移动性能档、文字明暗切换和手机触摸拖动。
