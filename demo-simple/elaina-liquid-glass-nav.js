(() => {
  "use strict";

  const nav = document.getElementById("bottomNav");
  const canvas = document.getElementById("liquidGlassNavCanvas");
  if (!nav || !(canvas instanceof HTMLCanvasElement)) return;

  const mobileUa = (
    navigator.userAgentData?.mobile === true ||
    /Android|iPhone|iPad|iPod|Mobile|Windows Phone|IEMobile|BlackBerry|Opera Mini|Silk|Kindle|webOS/i.test(navigator.userAgent) ||
    (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)
  );
  const performanceProfile = mobileUa ? {
    maxDpr: 1,
    pathsPerPixel: 2,
    maxAccumulation: 24,
    blurSampleCount: 5,
    captureMinIntervalMs: 50,
    preferFloatTargets: false,
    capsuleOnTouch: true,
    capsulePathsPerPixel: 1,
    capsuleMaxAccumulation: 8,
  } : {
    maxDpr: 1.5,
    pathsPerPixel: 4,
    maxAccumulation: 48,
    blurSampleCount: 9,
    captureMinIntervalMs: 0,
    preferFloatTargets: true,
    capsuleOnTouch: true,
    capsulePathsPerPixel: 4,
    capsuleMaxAccumulation: 48,
  };
  canvas.dataset.performanceProfile = mobileUa ? "mobile" : "desktop";

  const gl = canvas.getContext("webgl2", {
    alpha: true,
    antialias: false,
    depth: false,
    stencil: false,
    premultipliedAlpha: false,
    powerPreference: "high-performance",
  });

  if (!gl) {
    nav.classList.add("liquid-glass-css-only");
    return;
  }

  const PATHS_PER_PIXEL = performanceProfile.pathsPerPixel;
  const MAX_ACCUMULATION = performanceProfile.maxAccumulation;
  const CAPSULE_PATHS_PER_PIXEL = performanceProfile.capsulePathsPerPixel;
  const CAPSULE_MAX_ACCUMULATION = performanceProfile.capsuleMaxAccumulation;
  const CAPSULE_WIDTH_RATIO = 1 / 5;
  const CAPSULE_HEIGHT_SCALE = 1.2;
  const captureCanvas = document.createElement("canvas");
  const maskCanvas = document.createElement("canvas");
  const captureContext = captureCanvas.getContext("2d", { alpha: false });
  const maskContext = maskCanvas.getContext("2d");
  const readabilityCanvas = document.createElement("canvas");
  readabilityCanvas.width = 7;
  readabilityCanvas.height = 3;
  const readabilityContext = readabilityCanvas.getContext("2d", {
    alpha: false,
    willReadFrequently: true,
  });
  if (!captureContext || !maskContext) {
    nav.classList.add("liquid-glass-css-only");
    return;
  }

  const gaussianBlurSource = performanceProfile.blurSampleCount <= 5 ? `
    vec3 gaussianBlur(vec2 topPixel, float spacing) {
      vec3 color = sceneSample(topPixel) * 0.28;
      color += sceneSample(topPixel + vec2(spacing, 0.0)) * 0.18;
      color += sceneSample(topPixel - vec2(spacing, 0.0)) * 0.18;
      color += sceneSample(topPixel + vec2(0.0, spacing)) * 0.18;
      color += sceneSample(topPixel - vec2(0.0, spacing)) * 0.18;
      return color;
    }
  ` : `
    vec3 gaussianBlur(vec2 topPixel, float spacing) {
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
    }
  `;

  const vertexSource = `#version 300 es
    layout(location = 0) in vec2 aPosition;
    out vec2 vUv;

    void main() {
      vUv = aPosition * 0.5 + 0.5;
      gl_Position = vec4(aPosition, 0.0, 1.0);
    }
  `;

  const traceSource = `#version 300 es
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

    const float PI = 3.141592653589793;
    const int PATHS = ${PATHS_PER_PIXEL};
    const int CAPSULE_PATHS = ${CAPSULE_PATHS_PER_PIXEL};
    const float CAPSULE_MAX_ACCUMULATION = ${CAPSULE_MAX_ACCUMULATION}.0;

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
      float edgeBand = max(5.0, rect.w * 0.26);
      float u = saturateValue(depth / edgeBand);
      float smoothInterior = u * u * (3.0 - 2.0 * u);
      float edgeFactor = 1.0 - smoothInterior;
      vec2 outlineNormal = rectNormal(pixel, rect);

      float roughness = 0.014;
      vec2 roughOffset = vec2(
        cos(randomValue * PI * 2.0),
        sin(randomValue * PI * 2.0)
      ) * roughness;
      vec3 surfaceNormal = normalize(vec3(
        outlineNormal * edgeFactor * 1.35 * 0.67 + roughOffset,
        1.0
      ));
      vec3 cameraRay = normalize(vec3(
        (uMouse.x - 0.5) * 0.045,
        (uMouse.y - 0.5) * 0.045,
        -1.0
      ));

      vec3 insideRay = refract(cameraRay, surfaceNormal, 1.0 / indexOfRefraction);
      float thickness = 1.71 * (6.0 + smoothInterior * rect.w * 0.28);
      float insideTravel = thickness / max(0.08, -insideRay.z);
      vec2 hit = pixel + insideRay.xy * insideTravel;

      vec3 exitRay = refract(
        insideRay,
        vec3(0.0, 0.0, 1.0),
        indexOfRefraction
      );
      float exitTravel = (6.5 * 1.71) / max(0.08, -exitRay.z);
      hit += exitRay.xy * exitTravel;

      float lensCurve = 1.0 - sqrt(max(0.0, 1.0 - edgeFactor * edgeFactor));
      float reversePixels = min(36.0, edgeBand * 2.19);
      return pixel
        - outlineNormal * reversePixels * lensCurve
        + (hit - pixel) * lensCurve * 0.35;
    }

    vec3 tracePath(vec2 pixel, vec4 rect, float seed) {
      vec2 redPosition = refractedPosition(pixel, rect, 1.514, seed);
      vec2 greenPosition = refractedPosition(pixel, rect, 1.520, seed);
      vec2 bluePosition = refractedPosition(pixel, rect, 1.528, seed);

      vec3 neutral = sceneSample(greenPosition);
      vec3 dispersed = vec3(
        sceneSample(redPosition).r,
        neutral.g,
        sceneSample(bluePosition).b
      );

      float depth = max(0.0, -rectDistance(pixel, rect));
      float edgeBand = max(5.0, rect.w * 0.26);
      float edgeFactor = 1.0 - smoothstep(0.0, edgeBand, depth);
      float dispersionAmount = edgeFactor * 0.22;
      vec3 transmitted = mix(neutral, dispersed, dispersionAmount);

      float textPresence = max(maskSample(greenPosition), maskSample(pixel));
      transmitted = mix(transmitted, neutral, textPresence * 0.025);

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
      float edgeBand = max(5.0, rect.w * 0.26);
      float edgeFactor = 1.0 - smoothstep(0.0, edgeBand, depth);

      vec3 traced = vec3(0.0);
      for (int sampleIndex = 0; sampleIndex < PATHS; ++sampleIndex) {
        if (sampleIndex >= pathCount) break;
        float seed = hash12(
          pixel
          + vec2(float(sampleIndex) * 17.13, uFrame * 0.754 + 3.7)
        );
        traced += tracePath(pixel, rect, seed);
      }
      traced /= float(pathCount);

      float refractionBand = max(5.0, rect.w * 0.16);
      float blurWeight = smoothstep(
        max(0.0, refractionBand - 12.0),
        refractionBand + 12.0,
        depth
      );

      vec2 refractedAnchor = refractedPosition(pixel, rect, 1.520, 0.37);
      vec2 blurAnchor = mix(refractedAnchor, pixel, blurWeight);
      vec3 frosted = gaussianBlur(blurAnchor, 1.4);
      float frostedStrength = blurWeight * 0.29 * 0.55;
      vec3 layered = mix(traced, frosted, frostedStrength);

      vec2 localUv = (pixel - rect.xy) / rect.zw;
      layered = mix(layered, vec3(0.035, 0.035, 0.045), 0.17);
      layered += vec3(0.055, 0.062, 0.075) * blurWeight * 0.29;

      float diagonalA = smoothstep(
        0.22,
        0.0,
        abs(localUv.x + localUv.y - 0.22)
      );
      float diagonalB = smoothstep(
        0.20,
        0.0,
        abs(localUv.x + localUv.y - 1.78)
      );
      float highlight = (diagonalA + diagonalB) * edgeFactor * 0.055;
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
      bool insideCapsule =
        uCapsuleVisible > 0.5 && capsuleDistance <= 1.5;
      bool insidePreviousCapsule =
        uCapsuleOnly > 0.5 && previousCapsuleDistance <= 1.5;
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
        : ${MAX_ACCUMULATION - 1}.0;
      float accumulation = min(uFrame, accumulationLimit);
      float historyWeight = uReset > 0.5
        ? 0.0
        : accumulation / (accumulation + 1.0);
      outColor = mix(current, previous, historyWeight);
    }
  `;

  const displaySource = `#version 300 es
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

  function compileShader(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const message = gl.getShaderInfoLog(shader) || "shader compile failed";
      gl.deleteShader(shader);
      throw new Error(message);
    }
    return shader;
  }

  function createProgram(fragmentSource) {
    const program = gl.createProgram();
    const vertex = compileShader(gl.VERTEX_SHADER, vertexSource);
    const fragment = compileShader(gl.FRAGMENT_SHADER, fragmentSource);
    gl.attachShader(program, vertex);
    gl.attachShader(program, fragment);
    gl.linkProgram(program);
    gl.deleteShader(vertex);
    gl.deleteShader(fragment);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      const message = gl.getProgramInfoLog(program) || "program link failed";
      gl.deleteProgram(program);
      throw new Error(message);
    }
    return program;
  }

  let traceProgram;
  let displayProgram;
  try {
    traceProgram = createProgram(traceSource);
    displayProgram = createProgram(displaySource);
  } catch (error) {
    console.warn("[liquid-glass-nav] WebGL fallback:", error);
    nav.classList.add("liquid-glass-css-only");
    return;
  }

  const vertexArray = gl.createVertexArray();
  const vertexBuffer = gl.createBuffer();
  gl.bindVertexArray(vertexArray);
  gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([-1, -1, 3, -1, -1, 3]),
    gl.STATIC_DRAW,
  );
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

  const traceUniforms = {
    scene: gl.getUniformLocation(traceProgram, "uScene"),
    textMask: gl.getUniformLocation(traceProgram, "uTextMask"),
    previous: gl.getUniformLocation(traceProgram, "uPrevious"),
    resolution: gl.getUniformLocation(traceProgram, "uResolution"),
    navRect: gl.getUniformLocation(traceProgram, "uNavRect"),
    capsuleRect: gl.getUniformLocation(traceProgram, "uCapsuleRect"),
    previousCapsuleRect: gl.getUniformLocation(traceProgram, "uPreviousCapsuleRect"),
    capsuleVisible: gl.getUniformLocation(traceProgram, "uCapsuleVisible"),
    capsuleOnly: gl.getUniformLocation(traceProgram, "uCapsuleOnly"),
    frame: gl.getUniformLocation(traceProgram, "uFrame"),
    reset: gl.getUniformLocation(traceProgram, "uReset"),
    mouse: gl.getUniformLocation(traceProgram, "uMouse"),
  };
  const displayTexture = gl.getUniformLocation(displayProgram, "uTexture");

  function createSourceTexture() {
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.RGBA,
      1,
      1,
      0,
      gl.RGBA,
      gl.UNSIGNED_BYTE,
      new Uint8Array([17, 17, 17, 255]),
    );
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    return texture;
  }

  const sceneTexture = createSourceTexture();
  const maskTexture = createSourceTexture();
  const supportsFloatTargets = Boolean(
    gl.getExtension("EXT_color_buffer_float") &&
    gl.getExtension("OES_texture_float_linear"),
  );
  const useFloatTargets = performanceProfile.preferFloatTargets && supportsFloatTargets;
  let targetFormat = useFloatTargets ? gl.RGBA16F : gl.RGBA8;
  let targetType = useFloatTargets ? gl.HALF_FLOAT : gl.UNSIGNED_BYTE;
  canvas.dataset.targetPrecision = useFloatTargets ? "rgba16f" : "rgba8";
  let targets = [];
  let readTarget = 0;
  let accumulatedFrames = 0;
  let animationFrame = 0;
  let captureFrame = 0;
  let captureTimer = 0;
  let lastCaptureTime = 0;
  let pixelRatio = 1;
  let capsuleVisible = false;
  let pointerX = 0.5;
  let pointerY = 0.5;
  let navRect = [0, 0, 1, 1];
  let capsuleRect = [0, 0, 1, 1];
  let previousCapsuleRect = [-10000, -10000, 1, 1];
  let capsuleAccumulatedFrames = 0;
  let capsuleOnlyUpdate = false;
  let activeTouchPointerId = null;
  let pendingTouchPoint = null;
  let touchMoveFrame = 0;

  function deleteTargets() {
    targets.forEach(({ texture, framebuffer }) => {
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
    });
    targets = [];
  }

  function createTarget(width, height) {
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      targetFormat,
      width,
      height,
      0,
      gl.RGBA,
      targetType,
      null,
    );
    const framebuffer = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.framebufferTexture2D(
      gl.FRAMEBUFFER,
      gl.COLOR_ATTACHMENT0,
      gl.TEXTURE_2D,
      texture,
      0,
    );
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
      throw new Error("liquid glass framebuffer is incomplete");
    }
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    return { texture, framebuffer };
  }

  function allocateTargets(width, height) {
    deleteTargets();
    try {
      targets = [createTarget(width, height), createTarget(width, height)];
    } catch (error) {
      if (targetFormat === gl.RGBA8) throw error;
      targetFormat = gl.RGBA8;
      targetType = gl.UNSIGNED_BYTE;
      targets = [createTarget(width, height), createTarget(width, height)];
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }

  function intersects(rect, viewport) {
    return (
      rect.right > viewport.left &&
      rect.left < viewport.right &&
      rect.bottom > viewport.top &&
      rect.top < viewport.bottom
    );
  }

  function isTransparent(color) {
    return (
      !color ||
      color === "transparent" ||
      color === "rgba(0, 0, 0, 0)"
    );
  }

  function roundedPath(context, x, y, width, height, radius) {
    const safeRadius = Math.max(0, Math.min(radius, width * 0.5, height * 0.5));
    context.beginPath();
    if (typeof context.roundRect === "function") {
      context.roundRect(x, y, width, height, safeRadius);
    } else {
      context.rect(x, y, width, height);
    }
  }

  function localRect(rect, viewport) {
    return {
      x: rect.left - viewport.left,
      y: rect.top - viewport.top,
      width: rect.width,
      height: rect.height,
    };
  }

  function paintElementBox(element, style, rect, viewport) {
    const local = localRect(rect, viewport);
    const radius = Number.parseFloat(style.borderTopLeftRadius) || 0;
    if (!isTransparent(style.backgroundColor)) {
      roundedPath(
        captureContext,
        local.x,
        local.y,
        local.width,
        local.height,
        radius,
      );
      captureContext.fillStyle = style.backgroundColor;
      captureContext.fill();
    }

    const borderWidth = Number.parseFloat(style.borderTopWidth) || 0;
    if (borderWidth > 0 && !isTransparent(style.borderTopColor)) {
      roundedPath(
        captureContext,
        local.x + borderWidth * 0.5,
        local.y + borderWidth * 0.5,
        Math.max(0, local.width - borderWidth),
        Math.max(0, local.height - borderWidth),
        radius,
      );
      captureContext.strokeStyle = style.borderTopColor;
      captureContext.lineWidth = borderWidth;
      captureContext.stroke();
    }
  }

  function drawImageElement(image, rect, viewport, style) {
    if (!image.complete || !image.naturalWidth || !image.naturalHeight) return;
    const local = localRect(rect, viewport);
    const sourceWidth = image.naturalWidth;
    const sourceHeight = image.naturalHeight;
    const fit = style.objectFit || "fill";
    let sx = 0;
    let sy = 0;
    let sw = sourceWidth;
    let sh = sourceHeight;
    let dx = local.x;
    let dy = local.y;
    let dw = local.width;
    let dh = local.height;

    if (fit === "cover") {
      const scale = Math.max(dw / sourceWidth, dh / sourceHeight);
      sw = dw / scale;
      sh = dh / scale;
      sx = (sourceWidth - sw) * 0.5;
      sy = (sourceHeight - sh) * 0.5;
    } else if (fit === "contain") {
      const scale = Math.min(dw / sourceWidth, dh / sourceHeight);
      dw = sourceWidth * scale;
      dh = sourceHeight * scale;
      dx += (local.width - dw) * 0.5;
      dy += (local.height - dh) * 0.5;
    }

    const radius = Number.parseFloat(style.borderTopLeftRadius) || 0;
    captureContext.save();
    if (radius > 0) {
      roundedPath(captureContext, local.x, local.y, local.width, local.height, radius);
      captureContext.clip();
    }
    try {
      captureContext.drawImage(image, sx, sy, sw, sh, dx, dy, dw, dh);
    } catch {
      // An image that cannot be read is skipped instead of tainting the scene.
    }
    captureContext.restore();
  }

  function paintTextNode(node, viewport) {
    const text = node.nodeValue || "";
    if (!text.trim() || !node.parentElement) return;
    const style = getComputedStyle(node.parentElement);
    if (
      style.display === "none" ||
      style.visibility === "hidden" ||
      Number.parseFloat(style.opacity) === 0
    ) return;

    const wholeRange = document.createRange();
    wholeRange.selectNodeContents(node);
    const wholeRect = wholeRange.getBoundingClientRect();
    if (!wholeRect.width || !wholeRect.height || !intersects(wholeRect, viewport)) {
      wholeRange.detach();
      return;
    }
    wholeRange.detach();

    const lines = [];
    const characterRange = document.createRange();
    for (let index = 0; index < text.length; index += 1) {
      characterRange.setStart(node, index);
      characterRange.setEnd(node, index + 1);
      const rect = characterRange.getBoundingClientRect();
      if (!rect.width && !rect.height) continue;
      let line = lines.find((candidate) => Math.abs(candidate.top - rect.top) < 2);
      if (!line) {
        line = { top: rect.top, bottom: rect.bottom, left: rect.left, text: "" };
        lines.push(line);
      }
      line.left = Math.min(line.left, rect.left);
      line.bottom = Math.max(line.bottom, rect.bottom);
      line.text += text[index];
    }
    characterRange.detach();

    const fontSize = Number.parseFloat(style.fontSize) || 16;
    const font = [
      style.fontStyle,
      style.fontVariant,
      style.fontWeight,
      style.fontSize,
      style.fontFamily,
    ].join(" ");
    captureContext.font = font;
    captureContext.textAlign = "left";
    captureContext.textBaseline = "alphabetic";
    captureContext.fillStyle = style.color;
    maskContext.font = font;
    maskContext.textAlign = "left";
    maskContext.textBaseline = "alphabetic";
    maskContext.fillStyle = "#fff";

    lines.forEach((line) => {
      const lineRect = {
        left: line.left,
        right: line.left + 1,
        top: line.top,
        bottom: line.bottom,
      };
      if (!intersects(lineRect, viewport)) return;
      const x = line.left - viewport.left;
      const y = line.bottom - viewport.top - Math.max(0, (line.bottom - line.top - fontSize) * 0.2);
      captureContext.fillText(line.text, x, y);
      maskContext.fillText(line.text, x, y);
    });
  }

  function paintTree(node, viewport) {
    if (node === canvas) return;
    if (node.nodeType === Node.TEXT_NODE) {
      paintTextNode(node, viewport);
      return;
    }
    if (!(node instanceof Element)) return;
    const style = getComputedStyle(node);
    if (
      style.display === "none" ||
      style.visibility === "hidden" ||
      Number.parseFloat(style.opacity) === 0
    ) return;
    const rect = node.getBoundingClientRect();
    if (!intersects(rect, viewport)) return;

    captureContext.save();
    maskContext.save();
    const opacity = Number.parseFloat(style.opacity);
    if (Number.isFinite(opacity)) {
      captureContext.globalAlpha *= opacity;
      maskContext.globalAlpha *= opacity;
    }
    paintElementBox(node, style, rect, viewport);
    if (node instanceof HTMLImageElement) {
      drawImageElement(node, rect, viewport, style);
    } else {
      node.childNodes.forEach((child) => paintTree(child, viewport));
    }
    captureContext.restore();
    maskContext.restore();
  }

  function relativeLuminance(red, green, blue) {
    const linear = (channel) => {
      const value = channel / 255;
      return value <= 0.04045
        ? value / 12.92
        : ((value + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue);
  }

  function updateNavigationContrast(viewport) {
    if (!readabilityContext) return;
    nav.querySelectorAll(".nav-btn:not([hidden])").forEach((button) => {
      const rect = button.getBoundingClientRect();
      const sourceX = Math.max(0, (rect.left - viewport.left) * pixelRatio);
      const sourceY = Math.max(0, (rect.top - viewport.top) * pixelRatio);
      const sourceWidth = Math.min(
        captureCanvas.width - sourceX,
        rect.width * pixelRatio,
      );
      const sourceHeight = Math.min(
        captureCanvas.height - sourceY,
        rect.height * pixelRatio,
      );
      if (sourceWidth <= 0 || sourceHeight <= 0) return;

      readabilityContext.clearRect(0, 0, 7, 3);
      readabilityContext.drawImage(
        captureCanvas,
        sourceX,
        sourceY,
        sourceWidth,
        sourceHeight,
        0,
        0,
        7,
        3,
      );
      const pixels = readabilityContext.getImageData(0, 0, 7, 3).data;
      let luminance = 0;
      for (let index = 0; index < pixels.length; index += 4) {
        luminance += relativeLuminance(
          pixels[index],
          pixels[index + 1],
          pixels[index + 2],
        );
      }
      luminance /= pixels.length / 4;
      const lightContrast = 1.05 / (luminance + 0.05);
      const darkContrast = (luminance + 0.05) / 0.05;
      button.dataset.liquidGlassTone = darkContrast >= lightContrast
        ? "dark"
        : "light";
      button.style.setProperty(
        "--liquid-glass-label-luminance",
        luminance.toFixed(4),
      );
    });
  }

  function paintScene() {
    if (!canvas.width || !canvas.height) return;
    const viewport = canvas.getBoundingClientRect();
    captureContext.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    maskContext.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    captureContext.clearRect(0, 0, viewport.width, viewport.height);
    maskContext.clearRect(0, 0, viewport.width, viewport.height);

    const background = captureContext.createRadialGradient(
      viewport.width * 0.28,
      viewport.height * 0.2,
      0,
      viewport.width * 0.5,
      viewport.height * 0.5,
      viewport.width,
    );
    background.addColorStop(0, "#1b1428");
    background.addColorStop(0.45, "#14121a");
    background.addColorStop(1, "#101010");
    captureContext.fillStyle = background;
    captureContext.fillRect(0, 0, viewport.width, viewport.height);

    const activePage = document.querySelector(".page.active");
    if (activePage) paintTree(activePage, viewport);
    // Navigation labels are real DOM above the optical result. Sampling them
    // here would create a second, refracted copy inside the glass texture.
    updateNavigationContrast(viewport);

    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    gl.bindTexture(gl.TEXTURE_2D, sceneTexture);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.RGBA,
      gl.RGBA,
      gl.UNSIGNED_BYTE,
      captureCanvas,
    );
    gl.bindTexture(gl.TEXTURE_2D, maskTexture);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.RGBA,
      gl.RGBA,
      gl.UNSIGNED_BYTE,
      maskCanvas,
    );
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    resetAccumulation();
  }

  function scheduleCapture() {
    if (captureFrame || captureTimer) return;
    captureFrame = requestAnimationFrame((timestamp) => {
      captureFrame = 0;
      const delay = Math.max(
        0,
        performanceProfile.captureMinIntervalMs - (timestamp - lastCaptureTime),
      );
      if (delay > 0) {
        captureTimer = window.setTimeout(() => {
          captureTimer = 0;
          scheduleCapture();
        }, delay);
        return;
      }
      lastCaptureTime = timestamp;
      paintScene();
    });
  }

  function updateRects() {
    const canvasBounds = canvas.getBoundingClientRect();
    const navBounds = nav.getBoundingClientRect();
    navRect = [
      (navBounds.left - canvasBounds.left) * pixelRatio,
      (navBounds.top - canvasBounds.top) * pixelRatio,
      navBounds.width * pixelRatio,
      navBounds.height * pixelRatio,
    ];

    const navLeft = navBounds.left - canvasBounds.left;
    const navTop = navBounds.top - canvasBounds.top;
    const desiredWidth = navBounds.width * CAPSULE_WIDTH_RATIO;
    const capsuleHeight = navBounds.height * CAPSULE_HEIGHT_SCALE;
    const capsuleCenter = navLeft + pointerX * navBounds.width;
    const capsuleLeft = Math.max(
      navLeft,
      capsuleCenter - desiredWidth * 0.5,
    );
    const capsuleRight = Math.min(
      navLeft + navBounds.width,
      capsuleCenter + desiredWidth * 0.5,
    );
    capsuleRect = [
      capsuleLeft * pixelRatio,
      (navTop - (capsuleHeight - navBounds.height) * 0.5) * pixelRatio,
      Math.max(2, capsuleRight - capsuleLeft) * pixelRatio,
      capsuleHeight * pixelRatio,
    ];
  }

  function resize() {
    pixelRatio = Math.min(window.devicePixelRatio || 1, performanceProfile.maxDpr);
    const width = Math.max(1, Math.round(canvas.clientWidth * pixelRatio));
    const height = Math.max(1, Math.round(canvas.clientHeight * pixelRatio));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      captureCanvas.width = width;
      captureCanvas.height = height;
      maskCanvas.width = width;
      maskCanvas.height = height;
      allocateTargets(width, height);
    }
    updateRects();
    scheduleCapture();
  }

  function drawFrame() {
    animationFrame = 0;
    if (targets.length !== 2) return;
    const writeTarget = 1 - readTarget;
    gl.disable(gl.BLEND);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.bindVertexArray(vertexArray);

    gl.bindFramebuffer(gl.FRAMEBUFFER, targets[writeTarget].framebuffer);
    gl.useProgram(traceProgram);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, sceneTexture);
    gl.uniform1i(traceUniforms.scene, 0);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, maskTexture);
    gl.uniform1i(traceUniforms.textMask, 1);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, targets[readTarget].texture);
    gl.uniform1i(traceUniforms.previous, 2);
    gl.uniform2f(traceUniforms.resolution, canvas.width, canvas.height);
    gl.uniform4fv(traceUniforms.navRect, navRect);
    gl.uniform4fv(traceUniforms.capsuleRect, capsuleRect);
    gl.uniform4fv(traceUniforms.previousCapsuleRect, previousCapsuleRect);
    gl.uniform1f(traceUniforms.capsuleVisible, capsuleVisible ? 1 : 0);
    gl.uniform1f(traceUniforms.capsuleOnly, capsuleOnlyUpdate ? 1 : 0);
    const activeFrame = capsuleOnlyUpdate
      ? capsuleAccumulatedFrames
      : accumulatedFrames;
    gl.uniform1f(traceUniforms.frame, activeFrame);
    gl.uniform1f(traceUniforms.reset, activeFrame === 0 ? 1 : 0);
    gl.uniform2f(traceUniforms.mouse, pointerX, pointerY);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.useProgram(displayProgram);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, targets[writeTarget].texture);
    gl.uniform1i(displayTexture, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    readTarget = writeTarget;
    if (capsuleOnlyUpdate) {
      capsuleAccumulatedFrames = Math.min(
        CAPSULE_MAX_ACCUMULATION,
        capsuleAccumulatedFrames + 1,
      );
    } else {
      accumulatedFrames = Math.min(MAX_ACCUMULATION, accumulatedFrames + 1);
    }
    canvas.dataset.accumulatedFrames = String(accumulatedFrames);
    const needsMoreFrames = capsuleOnlyUpdate
      ? capsuleAccumulatedFrames < CAPSULE_MAX_ACCUMULATION
      : accumulatedFrames < MAX_ACCUMULATION;
    if (needsMoreFrames) {
      animationFrame = requestAnimationFrame(drawFrame);
    } else if (capsuleOnlyUpdate) {
      capsuleOnlyUpdate = false;
      previousCapsuleRect = [-10000, -10000, 1, 1];
    }
  }

  function resetAccumulation() {
    capsuleOnlyUpdate = false;
    capsuleAccumulatedFrames = 0;
    previousCapsuleRect = [-10000, -10000, 1, 1];
    accumulatedFrames = 0;
    if (animationFrame) cancelAnimationFrame(animationFrame);
    animationFrame = requestAnimationFrame(drawFrame);
  }

  function resetCapsuleAccumulation() {
    capsuleOnlyUpdate = true;
    capsuleAccumulatedFrames = 0;
    if (animationFrame) cancelAnimationFrame(animationFrame);
    animationFrame = requestAnimationFrame(drawFrame);
  }

  function setCapsuleFromPoint(clientX, clientY) {
    previousCapsuleRect = capsuleVisible
      ? [...capsuleRect]
      : [-10000, -10000, 1, 1];
    const bounds = nav.getBoundingClientRect();
    pointerX = Math.min(1, Math.max(0, (clientX - bounds.left) / bounds.width));
    pointerY = Math.min(1, Math.max(0, (clientY - bounds.top) / bounds.height));
    capsuleVisible = true;
    updateRects();
    resetCapsuleAccumulation();
  }

  nav.addEventListener("pointerdown", (event) => {
    if (!mobileUa || !performanceProfile.capsuleOnTouch || event.pointerType === "mouse") return;
    activeTouchPointerId = event.pointerId;
    nav.setPointerCapture?.(event.pointerId);
    setCapsuleFromPoint(event.clientX, event.clientY);
  });

  nav.addEventListener("pointermove", (event) => {
    if (mobileUa && event.pointerType !== "mouse") {
      if (event.pointerId !== activeTouchPointerId) return;
      pendingTouchPoint = { x: event.clientX, y: event.clientY };
      if (!touchMoveFrame) {
        touchMoveFrame = requestAnimationFrame(() => {
          touchMoveFrame = 0;
          const point = pendingTouchPoint;
          pendingTouchPoint = null;
          if (!point || activeTouchPointerId === null) return;
          setCapsuleFromPoint(point.x, point.y);
        });
      }
      return;
    }
    setCapsuleFromPoint(event.clientX, event.clientY);
  });

  function hideCapsule(event) {
    if (
      mobileUa &&
      performanceProfile.capsuleOnTouch &&
      event?.pointerType &&
      event.pointerType !== "mouse"
    ) return;
    if (!capsuleVisible) return;
    previousCapsuleRect = [...capsuleRect];
    capsuleVisible = false;
    resetCapsuleAccumulation();
  }

  nav.addEventListener("pointerleave", hideCapsule);
  function endTouchDrag(event) {
    if (event.pointerId !== activeTouchPointerId) return;
    if (nav.hasPointerCapture?.(event.pointerId)) {
      nav.releasePointerCapture(event.pointerId);
    }
    activeTouchPointerId = null;
    pendingTouchPoint = null;
    cancelAnimationFrame(touchMoveFrame);
    touchMoveFrame = 0;
    hideCapsule();
  }
  window.addEventListener("pointerup", endTouchDrag);
  window.addEventListener("pointercancel", endTouchDrag);
  nav.addEventListener("click", () => window.setTimeout(scheduleCapture, 0));
  window.addEventListener("scroll", scheduleCapture, { passive: true });
  window.addEventListener("resize", resize, { passive: true });
  document.addEventListener(
    "load",
    (event) => {
      if (event.target instanceof HTMLImageElement) scheduleCapture();
    },
    true,
  );

  const mutationObserver = new MutationObserver((mutations) => {
    const relevant = mutations.some((mutation) => {
      const element = mutation.target instanceof Element
        ? mutation.target
        : mutation.target.parentElement;
      return Boolean(
        element &&
        !element.closest(".maple-particles") &&
        (element.closest(".page") || element.closest("#bottomNav")),
      );
    });
    if (relevant) scheduleCapture();
  });
  mutationObserver.observe(document.body, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["class", "hidden", "src"],
  });

  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(nav);
  resizeObserver.observe(canvas);
  canvas.dataset.renderer = "WebGL2 analytic path tracing";

  resize();
  if (document.fonts && document.fonts.ready) {
    document.fonts.ready.then(scheduleCapture).catch(() => {});
  }
})();
