import test from "node:test";
import assert from "node:assert/strict";
import { createTraceFragmentShader } from "../src/shaders.js";

test("mobile capsule can use an independent path and frame budget", () => {
  const source = createTraceFragmentShader(2, 5, 1, 8);
  assert.match(source, /const int PATHS = 2;/);
  assert.match(source, /const int CAPSULE_PATHS = 1;/);
  assert.match(source, /const float CAPSULE_MAX_ACCUMULATION = 8\.0;/);
  assert.match(source, /uCapsuleOnly/);
  assert.match(source, /insidePreviousCapsule/);
});

test("capsule paths cannot exceed the base navigation path loop", () => {
  const source = createTraceFragmentShader(2, 5, 9, 8);
  assert.match(source, /const int CAPSULE_PATHS = 2;/);
});

test("reference optical constants are emitted as GLSL floats", () => {
  const source = createTraceFragmentShader();
  assert.match(source, /uOpticalThickness \* \(6\.0 \+ smoothInterior/);
  assert.match(source, /min\(36\.0, edgeBand \* uReverseDisplacement\)/);
  assert.doesNotMatch(source, /uOpticalThickness \* \(6 \+/);
  assert.doesNotMatch(source, /min\(36, edgeBand/);
});

test("component uses the approved height-based optical geometry", () => {
  const source = createTraceFragmentShader();
  assert.equal(
    (source.match(/rect\.w \* uEdgeBandRatio/g) || []).length,
    3,
  );
  assert.match(source, /smoothInterior \* rect\.w \* 0\.28/);
  assert.match(source, /rect\.w \* uRefractionVisibleRatio/);
  assert.doesNotMatch(source, /rect\.z \* uEdgeBandRatio/);
  assert.doesNotMatch(source, /rect\.z \* uRefractionVisibleRatio/);
});
