import { LIQUID_GLASS_REFERENCE } from "./layout.js";

const REFERENCE_OPTICS = LIQUID_GLASS_REFERENCE.optics;
const REFERENCE_RENDERING = LIQUID_GLASS_REFERENCE.rendering;

function glslFloat(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    throw new TypeError(`Cannot encode non-finite GLSL float: ${value}`);
  }
  return Number.isInteger(numeric) ? `${numeric}.0` : String(numeric);
}

function glslVec(values) {
  return values.map(glslFloat).join(", ");
}

/*
 * The default component shader is intentionally tied to the reference tuning
 * in layout.js: IOR 1.514/1.520/1.528, roughness 0.014, reverse cap 36px,
 * 2.19x reverse displacement, 0.67x curvature, 1.71x thickness, 26% edge
 * band, 16% visible refraction band, 12px feather, 1.4px blur, 29% frost,
 * 22% dispersion, 0.055 diagonal highlight, 4 paths and 48 accumulated
 * frames. Material fields remain uniforms so callers can tune them explicitly.
 */

export const vertexShaderSource = `#version 300 es
layout(location = 0) in vec2 aPosition;
out vec2 vUv;

void main() {
  vUv = aPosition * 0.5 + 0.5;
  gl_Position = vec4(aPosition, 0.0, 1.0);
}
`;

function createGaussianBlurSource(sampleCount) {
  if (sampleCount <= 5) {
    return `vec3 gaussianBlur(vec2 topPixel, float spacing) {
  vec3 color = sceneSample(topPixel) * 0.28;
  color += sceneSample(topPixel + vec2(spacing, 0.0)) * 0.18;
  color += sceneSample(topPixel - vec2(spacing, 0.0)) * 0.18;
  color += sceneSample(topPixel + vec2(0.0, spacing)) * 0.18;
  color += sceneSample(topPixel - vec2(0.0, spacing)) * 0.18;
  return color;
}`;
  }

  // Keep the reference desktop kernel byte-for-byte equivalent to the
  // original nine-sample implementation.
  return `vec3 gaussianBlur(vec2 topPixel, float spacing) {
  vec3 color = sceneSample(topPixel) * 0.204164;
  color += sceneSample(topPixel + vec2(spacing, 0.0)) * 0.180174;
  color += sceneSample(topPixel - vec2(spacing, 0.0)) * 0.180174;
  color += sceneSample(topPixel + vec2(spacing * 2.0, 0.0)) * 0.123832;
  color += sceneSample(topPixel - vec2(spacing * 2.0, 0.0)) * 0.123832;
  color += sceneSample(topPixel + vec2(0.0, spacing)) * 0.066282;
  color += sceneSample(topPixel - vec2(0.0, spacing)) * 0.066282;
  color += sceneSample(topPixel + vec2(0.0, spacing * 2.0)) * 0.027631;
  color += sceneSample(topPixel - vec2(0.0, spacing * 2.0)) * 0.027631;
  return color;
}`;
}

export function createTraceFragmentShader(
  pathsPerPixel = 4,
  blurSampleCount = 9,
  capsulePathsPerPixel = pathsPerPixel,
  capsuleMaxAccumulation = REFERENCE_RENDERING.maxAccumulation,
) {
  const paths = Math.max(1, Math.min(12, Math.round(pathsPerPixel)));
  const capsulePaths = Math.max(
    1,
    Math.min(paths, Math.round(capsulePathsPerPixel)),
  );
  const capsuleFrames = Math.max(1, Math.round(capsuleMaxAccumulation));
  const gaussianBlurSource = createGaussianBlurSource(blurSampleCount);
  return `#version 300 es
precision highp float;

in vec2 vUv;
out vec4 outColor;

uniform sampler2D uScene;
uniform sampler2D uTextMask;
uniform sampler2D uPrevious;
uniform vec2 uResolution;
uniform vec4 uNavRect;
uniform vec4 uCapsuleRect;
uniform vec4 uPreviousCapsuleRect;
uniform float uCapsuleVisible;
uniform float uCapsuleOnly;
uniform float uFrame;
uniform float uReset;
uniform vec2 uMouse;

uniform float uReverseDisplacement;
uniform float uEdgeCurvature;
uniform float uOpticalThickness;
uniform float uEdgeBandRatio;
uniform float uRefractionVisibleRatio;
uniform float uBlendFeather;
uniform float uFrostedStrength;
uniform float uFrostedAttenuation;
uniform float uBlurSpacing;
uniform float uDispersionStrength;
uniform float uHighlightStrength;
uniform vec3 uTintColor;
uniform float uTintMix;

const float PI = 3.141592653589793;
const int PATHS = ${paths};
const int CAPSULE_PATHS = ${capsulePaths};
const float CAPSULE_MAX_ACCUMULATION = ${glslFloat(capsuleFrames)};

float saturateValue(float value) {
  return clamp(value, 0.0, 1.0);
}

float hash12(vec2 value) {
  vec3 p3 = fract(vec3(value.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

vec2 topPixelToUv(vec2 pixel) {
  return vec2(
    clamp(pixel.x / uResolution.x, 0.0, 1.0),
    clamp(1.0 - pixel.y / uResolution.y, 0.0, 1.0)
  );
}

vec3 srgbToLinear(vec3 color) {
  return mix(
    color / 12.92,
    pow((color + 0.055) / 1.055, vec3(2.4)),
    step(vec3(0.04045), color)
  );
}

vec3 sceneSample(vec2 topPixel) {
  return srgbToLinear(texture(uScene, topPixelToUv(topPixel)).rgb);
}

float maskSample(vec2 topPixel) {
  return texture(uTextMask, topPixelToUv(topPixel)).r;
}

float roundedRectSdf(vec2 point, vec2 halfSize, float radius) {
  vec2 q = abs(point) - halfSize + vec2(radius);
  return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float rectDistance(vec2 pixel, vec4 rect) {
  vec2 center = rect.xy + rect.zw * 0.5;
  vec2 halfSize = rect.zw * 0.5;
  float radius = max(1.0, min(halfSize.x, halfSize.y) - 1.0);
  return roundedRectSdf(pixel - center, halfSize, radius);
}

vec2 rectNormal(vec2 pixel, vec4 rect) {
  vec2 center = rect.xy + rect.zw * 0.5;
  vec2 halfSize = rect.zw * 0.5;
  float radius = max(1.0, min(halfSize.x, halfSize.y) - 1.0);
  vec2 local = pixel - center;
  float dx = roundedRectSdf(local + vec2(0.8, 0.0), halfSize, radius)
    - roundedRectSdf(local - vec2(0.8, 0.0), halfSize, radius);
  float dy = roundedRectSdf(local + vec2(0.0, 0.8), halfSize, radius)
    - roundedRectSdf(local - vec2(0.0, 0.8), halfSize, radius);
  return normalize(vec2(dx, dy) + vec2(0.00001));
}

${gaussianBlurSource}

vec2 refractedPosition(
  vec2 pixel,
  vec4 rect,
  float indexOfRefraction,
  float randomValue
) {
  float distanceToEdge = rectDistance(pixel, rect);
  float depth = max(0.0, -distanceToEdge);
  float edgeBand = max(5.0, rect.w * uEdgeBandRatio);
  float u = saturateValue(depth / edgeBand);
  float smoothInterior = u * u * (3.0 - 2.0 * u);
  float edgeFactor = 1.0 - smoothInterior;
  vec2 outlineNormal = rectNormal(pixel, rect);

  float roughness = ${glslFloat(REFERENCE_OPTICS.roughness)};
  vec2 roughOffset = vec2(
    cos(randomValue * PI * 2.0),
    sin(randomValue * PI * 2.0)
  ) * roughness;
  vec3 surfaceNormal = normalize(vec3(
    outlineNormal * edgeFactor * 1.35 * uEdgeCurvature + roughOffset,
    1.0
  ));
  vec3 cameraRay = normalize(vec3(
    (uMouse.x - 0.5) * ${glslFloat(REFERENCE_OPTICS.cameraRayScale)},
    (uMouse.y - 0.5) * ${glslFloat(REFERENCE_OPTICS.cameraRayScale)},
    -1.0
  ));

  vec3 insideRay = refract(cameraRay, surfaceNormal, 1.0 / indexOfRefraction);
  float thickness = uOpticalThickness * (${glslFloat(REFERENCE_OPTICS.insideTravelBasePx)} + smoothInterior * rect.w * ${glslFloat(REFERENCE_OPTICS.insideTravelInteriorScale)});
  float insideTravel = thickness / max(${glslFloat(REFERENCE_OPTICS.minimumRayZ)}, -insideRay.z);
  vec2 hit = pixel + insideRay.xy * insideTravel;

  vec3 exitRay = refract(
    insideRay,
    vec3(0.0, 0.0, 1.0),
    indexOfRefraction
  );
  float exitTravel = (${glslFloat(REFERENCE_OPTICS.exitTravelBasePx)} * uOpticalThickness) / max(${glslFloat(REFERENCE_OPTICS.minimumRayZ)}, -exitRay.z);
  hit += exitRay.xy * exitTravel;

  float lensCurve = 1.0 - sqrt(max(0.0, 1.0 - edgeFactor * edgeFactor));
  float reversePixels = min(${glslFloat(REFERENCE_OPTICS.reverseMaxPixels)}, edgeBand * uReverseDisplacement);
  return pixel
    - outlineNormal * reversePixels * lensCurve
    + (hit - pixel) * lensCurve * ${glslFloat(REFERENCE_OPTICS.lensMix)};
}

vec3 tracePath(vec2 pixel, vec4 rect, float seed) {
  vec2 redPosition = refractedPosition(pixel, rect, ${glslFloat(REFERENCE_OPTICS.refractiveIndices.red)}, seed);
  vec2 greenPosition = refractedPosition(pixel, rect, ${glslFloat(REFERENCE_OPTICS.refractiveIndices.green)}, seed);
  vec2 bluePosition = refractedPosition(pixel, rect, ${glslFloat(REFERENCE_OPTICS.refractiveIndices.blue)}, seed);

  vec3 neutral = sceneSample(greenPosition);
  vec3 dispersed = vec3(
    sceneSample(redPosition).r,
    neutral.g,
    sceneSample(bluePosition).b
  );

  float depth = max(0.0, -rectDistance(pixel, rect));
  float edgeBand = max(5.0, rect.w * uEdgeBandRatio);
  float edgeFactor = 1.0 - smoothstep(0.0, edgeBand, depth);
  vec3 transmitted = mix(neutral, dispersed, edgeFactor * uDispersionStrength);

  // The mask only protects text very lightly. It never enables strong blur.
  float textPresence = max(maskSample(greenPosition), maskSample(pixel));
  transmitted = mix(transmitted, neutral, textPresence * ${glslFloat(REFERENCE_OPTICS.textMaskProtection)});

  vec2 normal = rectNormal(pixel, rect);
  float topLight = pow(
    saturateValue(dot(
      normalize(vec3(normal, 0.72)),
      normalize(vec3(-0.58, -0.74, 0.62))
    )),
    18.0
  );
  float fresnel = 0.04 + 0.96 * pow(
    1.0 - saturateValue(1.0 - edgeFactor * 0.36),
    5.0
  );
  float reflectionEvent = step(seed, fresnel * 0.34);
  vec3 reflected = vec3(0.52, 0.58, 0.68) * (0.24 + topLight * 0.72);
  return mix(transmitted, reflected, reflectionEvent);
}

vec4 liquidGlass(vec2 pixel, vec4 rect, int pathCount) {
  float distanceToEdge = rectDistance(pixel, rect);
  float antialiasWidth = max(1.0, fwidth(distanceToEdge));
  float coverage = 1.0 - smoothstep(
    -antialiasWidth,
    antialiasWidth,
    distanceToEdge
  );
  if (coverage <= 0.0) return vec4(0.0);

  float depth = max(0.0, -distanceToEdge);
  float edgeBand = max(5.0, rect.w * uEdgeBandRatio);
  float edgeFactor = 1.0 - smoothstep(0.0, edgeBand, depth);

  vec3 traced = vec3(0.0);
  for (int sampleIndex = 0; sampleIndex < PATHS; ++sampleIndex) {
    if (sampleIndex >= pathCount) break;
    float seed = hash12(
      pixel + vec2(float(sampleIndex) * 17.13, uFrame * 0.754 + 3.7)
    );
    traced += tracePath(pixel, rect, seed);
  }
  traced /= float(pathCount);

  float refractionBand = max(5.0, rect.w * uRefractionVisibleRatio);
  float blurWeight = smoothstep(
    max(0.0, refractionBand - uBlendFeather),
    refractionBand + uBlendFeather,
    depth
  );

  // Following the refracted anchor before returning to the original pixel is
  // what removes the visible seam between refraction and frosting.
  vec2 refractedAnchor = refractedPosition(pixel, rect, ${glslFloat(REFERENCE_OPTICS.refractiveIndices.green)}, 0.37);
  vec2 blurAnchor = mix(refractedAnchor, pixel, blurWeight);
  vec3 frosted = gaussianBlur(blurAnchor, uBlurSpacing);
  float frostMix = blurWeight * uFrostedStrength * uFrostedAttenuation;
  vec3 layered = mix(traced, frosted, frostMix);

  vec2 localUv = (pixel - rect.xy) / rect.zw;
  layered = mix(layered, uTintColor, uTintMix);
  layered += vec3(${glslVec(REFERENCE_OPTICS.frostTintColor)}) * blurWeight * uFrostedStrength;

  float diagonalA = smoothstep(${glslFloat(REFERENCE_OPTICS.highlightAWidth)}, 0.0, abs(localUv.x + localUv.y - ${glslFloat(REFERENCE_OPTICS.highlightACenter)}));
  float diagonalB = smoothstep(${glslFloat(REFERENCE_OPTICS.highlightBWidth)}, 0.0, abs(localUv.x + localUv.y - ${glslFloat(REFERENCE_OPTICS.highlightBCenter)}));
  float highlight = (diagonalA + diagonalB) * edgeFactor * uHighlightStrength;
  layered += vec3(highlight);
  return vec4(max(layered, vec3(0.0)), coverage);
}

void main() {
  vec2 topPixel = vec2(
    vUv.x * uResolution.x,
    (1.0 - vUv.y) * uResolution.y
  );

  float navDistance = rectDistance(topPixel, uNavRect);
  float capsuleDistance = rectDistance(topPixel, uCapsuleRect);
  float previousCapsuleDistance = rectDistance(topPixel, uPreviousCapsuleRect);
  bool insideCapsule = uCapsuleVisible > 0.5 && capsuleDistance <= 1.5;
  bool insidePreviousCapsule = uCapsuleOnly > 0.5 && previousCapsuleDistance <= 1.5;
  bool insideNav = navDistance <= 1.5;

  vec4 previous = texture(uPrevious, vUv);
  if (uCapsuleOnly > 0.5 && !insideCapsule && !insidePreviousCapsule) {
    outColor = previous;
    return;
  }
  if (
    uCapsuleOnly < 0.5 &&
    insideCapsule &&
    uFrame >= CAPSULE_MAX_ACCUMULATION
  ) {
    outColor = previous;
    return;
  }

  vec4 current = vec4(0.0);
  if (insideCapsule) {
    current = liquidGlass(topPixel, uCapsuleRect, CAPSULE_PATHS);
  } else if (insideNav) {
    current = liquidGlass(topPixel, uNavRect, PATHS);
  }

  float accumulationLimit = uCapsuleOnly > 0.5
    ? CAPSULE_MAX_ACCUMULATION - 1.0
    : ${glslFloat(REFERENCE_RENDERING.maxAccumulation - 1)};
  float accumulation = min(uFrame, accumulationLimit);
  float historyWeight = uReset > 0.5
    ? 0.0
    : accumulation / (accumulation + 1.0);
  outColor = mix(current, previous, historyWeight);
}
`;
}

export const displayFragmentShaderSource = `#version 300 es
precision highp float;
in vec2 vUv;
out vec4 outColor;
uniform sampler2D uTexture;

vec3 linearToSrgb(vec3 color) {
  return mix(
    color * 12.92,
    1.055 * pow(max(color, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055,
    step(vec3(0.0031308), color)
  );
}

void main() {
  vec4 sampleValue = texture(uTexture, vUv);
  outColor = vec4(
    clamp(linearToSrgb(sampleValue.rgb), 0.0, 1.0),
    sampleValue.a
  );
}
`;
