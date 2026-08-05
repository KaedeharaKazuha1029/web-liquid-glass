import test from "node:test";
import assert from "node:assert/strict";
import {
  DEFAULT_MATERIAL,
  DEFAULT_MOBILE_PERFORMANCE,
  DEFAULT_OPTIONS,
  LIQUID_GLASS_REFERENCE,
  chooseReadableTextTone,
  computeCapsuleRect,
  detectMobileDevice,
  resolvePerformanceOptions,
  relativeLuminance,
} from "../src/layout.js";

const nav = {
  navLeft: 100,
  navTop: 500,
  navWidth: 600,
  navHeight: 60,
};

test("center capsule uses one fifth of the navigation width", () => {
  const capsule = computeCapsuleRect({ ...nav, pointerX: 0.5 });
  assert.equal(capsule.x, 340);
  assert.equal(capsule.width, 120);
  assert.equal(capsule.height, 72);
  assert.equal(capsule.y, 494);
});

test("capsule clips the overflowing left side without shifting the other side", () => {
  const capsule = computeCapsuleRect({ ...nav, pointerX: 0 });
  assert.equal(capsule.x, 100);
  assert.equal(capsule.width, 60);
  assert.equal(capsule.height, 72);
});

test("capsule clips the overflowing right side", () => {
  const capsule = computeCapsuleRect({ ...nav, pointerX: 1 });
  assert.equal(capsule.x, 640);
  assert.equal(capsule.width, 60);
});

test("pointer input is clamped before layout", () => {
  assert.deepEqual(
    computeCapsuleRect({ ...nav, pointerX: -5 }),
    computeCapsuleRect({ ...nav, pointerX: 0 }),
  );
  assert.deepEqual(
    computeCapsuleRect({ ...nav, pointerX: 5 }),
    computeCapsuleRect({ ...nav, pointerX: 1 }),
  );
});

test("published defaults preserve the approved optical material", () => {
  assert.equal(DEFAULT_MATERIAL.reverseDisplacement, 2.19);
  assert.equal(DEFAULT_MATERIAL.edgeCurvature, 0.67);
  assert.equal(DEFAULT_MATERIAL.opticalThickness, 1.71);
  assert.equal(DEFAULT_MATERIAL.edgeBandRatio, 0.26);
  assert.equal(DEFAULT_MATERIAL.refractionVisibleRatio, 0.16);
  assert.equal(DEFAULT_MATERIAL.blendFeatherPx, 12);
  assert.equal(DEFAULT_MATERIAL.frostedStrength, 0.29);
  assert.equal(DEFAULT_OPTIONS.capsuleWidthRatio, 0.2);
  assert.equal(DEFAULT_OPTIONS.capsuleHeightScale, 1.2);
  assert.equal(DEFAULT_OPTIONS.maxDpr, 1.5);
  assert.equal(DEFAULT_OPTIONS.captureNavigationContent, false);
  assert.equal(LIQUID_GLASS_REFERENCE.optics.refractiveIndices.green, 1.52);
  assert.equal(LIQUID_GLASS_REFERENCE.optics.reverseMaxPixels, 36);
  assert.equal(LIQUID_GLASS_REFERENCE.navigation.widthVw, 85);
  assert.equal(LIQUID_GLASS_REFERENCE.navigation.canvasHeightScale, 1.4);
});

test("mobile UA activates only the workload profile", () => {
  const navigatorLike = {
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Mobile/15E148",
    platform: "iPhone",
    maxTouchPoints: 5,
  };
  assert.equal(detectMobileDevice(navigatorLike), true);
  const resolved = resolvePerformanceOptions({}, navigatorLike);
  assert.equal(resolved.mobileProfile, true);
  assert.equal(resolved.maxDpr, 1);
  assert.equal(resolved.pathsPerPixel, 2);
  assert.equal(resolved.maxAccumulation, 24);
  assert.equal(resolved.blurSampleCount, 5);
  assert.equal(resolved.captureMinIntervalMs, 50);
  assert.equal(resolved.preferFloatTargets, false);
  assert.equal(resolved.capsuleOnTouch, true);
  assert.equal(resolved.capsulePathsPerPixel, 1);
  assert.equal(resolved.capsuleMaxAccumulation, 8);
});

test("desktop UA preserves the exact reference workload", () => {
  const navigatorLike = {
    userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140 Safari/537.36",
    platform: "Win32",
    maxTouchPoints: 0,
  };
  const resolved = resolvePerformanceOptions({}, navigatorLike);
  assert.equal(resolved.mobileProfile, false);
  assert.equal(resolved.maxDpr, 1.5);
  assert.equal(resolved.pathsPerPixel, 4);
  assert.equal(resolved.maxAccumulation, 48);
  assert.equal(resolved.blurSampleCount, 9);
  assert.equal(resolved.captureMinIntervalMs, 0);
  assert.equal(resolved.preferFloatTargets, true);
  assert.equal(resolved.capsulePathsPerPixel, 4);
  assert.equal(resolved.capsuleMaxAccumulation, 48);
});

test("mobile profile can be disabled or selectively tuned", () => {
  const navigatorLike = { userAgentData: { mobile: true } };
  const disabled = resolvePerformanceOptions({ mobilePerformance: false }, navigatorLike);
  assert.equal(disabled.mobileProfile, false);
  assert.equal(disabled.pathsPerPixel, 4);

  const tuned = resolvePerformanceOptions({
    mobilePerformance: { maxDpr: 0.8, pathsPerPixel: 1 },
  }, navigatorLike);
  assert.equal(tuned.maxDpr, 0.8);
  assert.equal(tuned.pathsPerPixel, 1);
  assert.equal(tuned.maxAccumulation, DEFAULT_MOBILE_PERFORMANCE.maxAccumulation);
});

test("iPadOS desktop UA is recognized through its touch platform signature", () => {
  assert.equal(detectMobileDevice({
    userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15)",
    platform: "MacIntel",
    maxTouchPoints: 5,
  }), true);
});

test("navigation label tone chooses the higher-contrast foreground", () => {
  assert.equal(relativeLuminance(0, 0, 0), 0);
  assert.equal(relativeLuminance(255, 255, 255), 1);
  assert.equal(chooseReadableTextTone(relativeLuminance(20, 24, 32)), "light");
  assert.equal(chooseReadableTextTone(relativeLuminance(235, 238, 242)), "dark");
});
