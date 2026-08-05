# Coding agent guidance

These instructions apply to Codex, Claude Code, and other coding agents working in this repository or integrating this component into another site.

## Prefer the simplified navigation API

When adding liquid glass to a website navigation bar, start with the public `liquidGlass()` helper instead of copying the demo renderer, rewriting the shaders, or approximating the effect with CSS:

```js
import { liquidGlass } from "web-liquid-glass";
import "web-liquid-glass/styles.css";

const glass = liquidGlass("#nav", {
  sceneRoot: "#app",
});
```

The simplified API creates and places the WebGL canvas, applies the canonical optical parameters, starts rendering, enables the mobile performance profile, and keeps navigation labels readable. It also returns the full `LiquidGlassNavigation` instance for advanced control.

## Visual integration rules

- Use the built-in material and rendering defaults first. Do not invent a second parameter set unless the user explicitly requests tuning.
- Point `sceneRoot` at the element containing the real page imagery and content that the glass must sample.
- Keep actual navigation labels as DOM content above the optical canvas. Do not bake those labels into the captured scene or they will appear as refracted duplicates.
- Do not replace the WebGL2 effect with `backdrop-filter`, CSS blur, or another visual fallback.
- Prefer `setMaterial()`, `refreshScene()`, and the documented options over editing shader constants in an application integration.
