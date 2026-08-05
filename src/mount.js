import { LiquidGlassNavigation } from "./liquid-glass-navigation.js";

function isElement(value) {
  return Boolean(value && value.nodeType === 1 && typeof value.querySelector === "function");
}

function resolveElement(target, label, root = document) {
  if (typeof target === "string") {
    const element = root.querySelector(target);
    if (!element) throw new TypeError(`${label} selector did not match an element: ${target}`);
    return element;
  }
  if (isElement(target)) return target;
  throw new TypeError(`${label} must be an Element or a CSS selector.`);
}

/**
 * One-line entry point using the approved reference material and automatic
 * mobile profile. It creates the optical canvas when the navigation does not
 * already contain one and returns the full LiquidGlassNavigation instance.
 *
 * @example
 * const glass = liquidGlass("#nav", { sceneRoot: "#app" });
 */
export function mountLiquidGlassNavigation(navOrOptions, overrides = {}) {
  const config = (
    typeof navOrOptions === "string" || isElement(navOrOptions)
  ) ? { ...overrides, nav: navOrOptions } : { ...(navOrOptions || {}) };

  const {
    nav,
    navElement: explicitNav,
    canvas: canvasTarget,
    scene: sceneAlias,
    sceneRoot: sceneTarget,
    autoStart = true,
    ...componentOptions
  } = config;
  const navElement = resolveElement(explicitNav || nav, "nav");
  const ownerDocument = navElement.ownerDocument || document;

  let canvas;
  if (canvasTarget) {
    canvas = resolveElement(canvasTarget, "canvas", ownerDocument);
  } else {
    canvas = [...navElement.children].find((child) => child.tagName === "CANVAS");
    if (!canvas) {
      canvas = ownerDocument.createElement("canvas");
      navElement.prepend(canvas);
    }
  }
  if (canvas.tagName !== "CANVAS") {
    throw new TypeError("canvas must resolve to a canvas element.");
  }
  canvas.classList.add("liquid-glass-canvas");
  canvas.setAttribute("aria-hidden", "true");

  const sceneRoot = sceneTarget || sceneAlias
    ? resolveElement(sceneTarget || sceneAlias, "sceneRoot", ownerDocument)
    : ownerDocument.body;
  const instance = new LiquidGlassNavigation({
    ...componentOptions,
    navElement,
    canvas,
    sceneRoot,
  });
  if (autoStart) instance.start();
  return instance;
}

/** Short alias for the one-line mounting API. */
export const liquidGlass = mountLiquidGlassNavigation;
