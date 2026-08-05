/**
 * Canonical optical tuning used by the reference navigation and by the
 * reusable component. Keep this block in component source, not only in the
 * demo, so consumers who import LiquidGlassNavigation get the same starting
 * image as the published example.
 */
export const LIQUID_GLASS_REFERENCE = Object.freeze({
  material: Object.freeze({
    reverseDisplacement: 2.19,
    edgeCurvature: 0.67,
    opticalThickness: 1.71,
    edgeBandRatio: 0.26,
    refractionVisibleRatio: 0.16,
    blendFeatherPx: 12,
    frostedStrength: 0.29,
    frostedAttenuation: 0.55,
    blurSpacingPx: 1.4,
    dispersionStrength: 0.22,
    highlightStrength: 0.055,
    tintColor: Object.freeze([0.035, 0.035, 0.045]),
    tintMix: 0.17,
  }),
  optics: Object.freeze({
    refractiveIndices: Object.freeze({ red: 1.514, green: 1.520, blue: 1.528 }),
    roughness: 0.014,
    cameraRayScale: 0.045,
    minimumRayZ: 0.08,
    insideTravelBasePx: 6.0,
    insideTravelInteriorScale: 0.28,
    exitTravelBasePx: 6.5,
    reverseMaxPixels: 36,
    lensMix: 0.35,
    textMaskProtection: 0.025,
    frostTintColor: Object.freeze([0.055, 0.062, 0.075]),
    highlightAWidth: 0.22,
    highlightACenter: 0.22,
    highlightBWidth: 0.20,
    highlightBCenter: 1.78,
  }),
  rendering: Object.freeze({
    pathsPerPixel: 4,
    maxAccumulation: 48,
    maxDpr: 1.5,
    blurSampleCount: 9,
    captureMinIntervalMs: 0,
    preferFloatTargets: true,
    capsulePathsPerPixel: 4,
    capsuleMaxAccumulation: 48,
  }),
  capsule: Object.freeze({
    widthRatio: 1 / 5,
    heightScale: 1.2,
  }),
  navigation: Object.freeze({
    widthVw: 85,
    heightPx: 56,
    bottomPx: 32,
    mobileWidthVw: 92,
    mobileHeightPx: 58,
    mobileBottomPx: 24,
    canvasHeightScale: 1.4,
  }),
});

export const DEFAULT_MATERIAL = LIQUID_GLASS_REFERENCE.material;

export const DEFAULT_OPTIONS = Object.freeze({
  maxDpr: LIQUID_GLASS_REFERENCE.rendering.maxDpr,
  pathsPerPixel: LIQUID_GLASS_REFERENCE.rendering.pathsPerPixel,
  maxAccumulation: LIQUID_GLASS_REFERENCE.rendering.maxAccumulation,
  capsuleWidthRatio: LIQUID_GLASS_REFERENCE.capsule.widthRatio,
  capsuleHeightScale: LIQUID_GLASS_REFERENCE.capsule.heightScale,
  captureBackground: "#111117",
  observe: true,
  allowCrossOriginImages: false,
  captureNavigationContent: false,
  autoLabelContrast: true,
  labelSelector: "button:not([hidden]), [role='button']:not([hidden]), a:not([hidden])",
  mobilePerformance: true,
});

/**
 * Mobile-only workload reductions. Optical/material values are deliberately
 * absent: refraction, dispersion, tint and edge geometry stay identical to the
 * desktop reference. Set `mobilePerformance: false` to opt out, or pass a
 * partial object to tune this profile without changing desktop rendering.
 */
export const DEFAULT_MOBILE_PERFORMANCE = Object.freeze({
  maxDpr: 1,
  pathsPerPixel: 2,
  maxAccumulation: 24,
  blurSampleCount: 5,
  captureMinIntervalMs: 50,
  preferFloatTargets: false,
  capsuleOnTouch: true,
  capsulePathsPerPixel: 1,
  capsuleMaxAccumulation: 8,
});

const MOBILE_UA_PATTERN = /Android|iPhone|iPad|iPod|Mobile|Windows Phone|IEMobile|BlackBerry|Opera Mini|Silk|Kindle|webOS/i;

export function detectMobileDevice(navigatorLike = globalThis.navigator) {
  if (!navigatorLike) return false;
  if (navigatorLike.userAgentData?.mobile === true) return true;
  if (MOBILE_UA_PATTERN.test(navigatorLike.userAgent || "")) return true;
  // iPadOS can request a desktop UA while still exposing the MacIntel platform.
  return (
    navigatorLike.platform === "MacIntel" &&
    Number(navigatorLike.maxTouchPoints) > 1
  );
}

export function resolvePerformanceOptions(options = {}, navigatorLike = globalThis.navigator) {
  const mobileDetected = detectMobileDevice(navigatorLike);
  const mobileEnabled = options.mobilePerformance !== false && mobileDetected;
  const mobileOverrides = (
    options.mobilePerformance && typeof options.mobilePerformance === "object"
  ) ? options.mobilePerformance : {};
  const desktop = {
    maxDpr: options.maxDpr ?? DEFAULT_OPTIONS.maxDpr,
    pathsPerPixel: options.pathsPerPixel ?? DEFAULT_OPTIONS.pathsPerPixel,
    maxAccumulation: options.maxAccumulation ?? DEFAULT_OPTIONS.maxAccumulation,
    blurSampleCount: options.blurSampleCount ?? LIQUID_GLASS_REFERENCE.rendering.blurSampleCount,
    captureMinIntervalMs: options.captureMinIntervalMs ?? LIQUID_GLASS_REFERENCE.rendering.captureMinIntervalMs,
    preferFloatTargets: options.preferFloatTargets ?? LIQUID_GLASS_REFERENCE.rendering.preferFloatTargets,
    capsuleOnTouch: options.capsuleOnTouch ?? true,
    capsulePathsPerPixel: options.capsulePathsPerPixel ?? LIQUID_GLASS_REFERENCE.rendering.capsulePathsPerPixel,
    capsuleMaxAccumulation: options.capsuleMaxAccumulation ?? LIQUID_GLASS_REFERENCE.rendering.capsuleMaxAccumulation,
  };

  return {
    ...desktop,
    ...(mobileEnabled ? { ...DEFAULT_MOBILE_PERFORMANCE, ...mobileOverrides } : {}),
    mobileDetected,
    mobileProfile: mobileEnabled,
  };
}

export function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}

export function srgbChannelToLinear(channel) {
  const value = clamp(channel, 0, 255) / 255;
  return value <= 0.04045
    ? value / 12.92
    : ((value + 0.055) / 1.055) ** 2.4;
}

export function relativeLuminance(red, green, blue) {
  return (
    0.2126 * srgbChannelToLinear(red) +
    0.7152 * srgbChannelToLinear(green) +
    0.0722 * srgbChannelToLinear(blue)
  );
}

export function chooseReadableTextTone(luminance) {
  const safeLuminance = clamp(luminance, 0, 1);
  const lightContrast = 1.05 / (safeLuminance + 0.05);
  const darkContrast = (safeLuminance + 0.05) / 0.05;
  return darkContrast >= lightContrast ? "dark" : "light";
}

export function computeCapsuleRect({
  navLeft,
  navTop,
  navWidth,
  navHeight,
  pointerX,
  widthRatio = DEFAULT_OPTIONS.capsuleWidthRatio,
  heightScale = DEFAULT_OPTIONS.capsuleHeightScale,
}) {
  const safePointer = clamp(pointerX, 0, 1);
  const desiredWidth = navWidth * widthRatio;
  const center = navLeft + safePointer * navWidth;
  const left = Math.max(navLeft, center - desiredWidth * 0.5);
  const right = Math.min(navLeft + navWidth, center + desiredWidth * 0.5);
  const height = navHeight * heightScale;

  return {
    x: left,
    y: navTop - (height - navHeight) * 0.5,
    width: Math.max(2, right - left),
    height,
  };
}

export function rectRelativeToViewport(rect, viewport, scale = 1) {
  return {
    x: (rect.left - viewport.left) * scale,
    y: (rect.top - viewport.top) * scale,
    width: rect.width * scale,
    height: rect.height * scale,
  };
}
