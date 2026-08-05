import test from "node:test";
import assert from "node:assert/strict";
import {
  LiquidGlassNavigation,
  liquidGlass,
  mountLiquidGlassNavigation,
} from "../src/index.js";

test("package exports both the full class and one-line mounting API", () => {
  assert.equal(typeof LiquidGlassNavigation, "function");
  assert.equal(typeof mountLiquidGlassNavigation, "function");
  assert.equal(liquidGlass, mountLiquidGlassNavigation);
});
