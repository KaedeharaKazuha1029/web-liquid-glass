import {
  DEFAULT_MATERIAL,
  DEFAULT_OPTIONS,
  clamp,
  chooseReadableTextTone,
  computeCapsuleRect,
  rectRelativeToViewport,
  relativeLuminance,
  resolvePerformanceOptions,
} from "./layout.js";
import { DomSceneCapture } from "./scene-capture.js";
import {
  createTraceFragmentShader,
  displayFragmentShaderSource,
  vertexShaderSource,
} from "./shaders.js";

function mergeOptions(options) {
  const performance = resolvePerformanceOptions(options);
  return {
    ...DEFAULT_OPTIONS,
    ...options,
    ...performance,
    material: {
      ...DEFAULT_MATERIAL,
      ...(options.material || {}),
    },
  };
}

function createShader(gl, type, source) {
  const shader = gl.createShader(type);
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const message = gl.getShaderInfoLog(shader) || "Shader compilation failed.";
    gl.deleteShader(shader);
    throw new Error(message);
  }
  return shader;
}

function createProgram(gl, vertexSource, fragmentSource) {
  const vertex = createShader(gl, gl.VERTEX_SHADER, vertexSource);
  const fragment = createShader(gl, gl.FRAGMENT_SHADER, fragmentSource);
  const program = gl.createProgram();
  gl.attachShader(program, vertex);
  gl.attachShader(program, fragment);
  gl.linkProgram(program);
  gl.deleteShader(vertex);
  gl.deleteShader(fragment);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const message = gl.getProgramInfoLog(program) || "Program linking failed.";
    gl.deleteProgram(program);
    throw new Error(message);
  }
  return program;
}

function createSourceTexture(gl, pixel = [17, 17, 23, 255]) {
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(
    gl.TEXTURE_2D,
    0,
    gl.RGBA,
    1,
    1,
    0,
    gl.RGBA,
    gl.UNSIGNED_BYTE,
    new Uint8Array(pixel),
  );
  return texture;
}

const TRACE_UNIFORM_NAMES = [
  "uScene",
  "uTextMask",
  "uPrevious",
  "uResolution",
  "uNavRect",
  "uCapsuleRect",
  "uPreviousCapsuleRect",
  "uCapsuleVisible",
  "uCapsuleOnly",
  "uFrame",
  "uReset",
  "uMouse",
  "uReverseDisplacement",
  "uEdgeCurvature",
  "uOpticalThickness",
  "uEdgeBandRatio",
  "uRefractionVisibleRatio",
  "uBlendFeather",
  "uFrostedStrength",
  "uFrostedAttenuation",
  "uBlurSpacing",
  "uDispersionStrength",
  "uHighlightStrength",
  "uTintColor",
  "uTintMix",
];

const HIDDEN_CAPSULE_RECT = Object.freeze([-10000, -10000, 1, 1]);

/**
 * Reusable WebGL2 liquid-glass navigation renderer.
 *
 * The component renders only the optical layer. DOM buttons remain responsible
 * for semantics and pointer interaction and stay out of uScene, preventing a
 * second refracted copy of navigation labels.
 */
export class LiquidGlassNavigation {
  constructor(options) {
    if (!options?.navElement || !options?.canvas) {
      throw new TypeError("navElement and canvas are required.");
    }
    if (!(options.canvas instanceof HTMLCanvasElement)) {
      throw new TypeError("canvas must be an HTMLCanvasElement.");
    }

    this.options = mergeOptions(options);
    this.navElement = options.navElement;
    this.pointerElement = options.pointerElement || options.navElement;
    this.canvas = options.canvas;
    this.sceneRoot = options.sceneRoot || document.body;
    this.canvas.dataset.liquidGlassCanvas = "";
    this.canvas.classList.add("liquid-glass-canvas");
    this.navElement.classList.add("liquid-glass-nav");

    this.gl = null;
    this.supported = false;
    this.running = false;
    this.disposed = false;
    this.pixelRatio = 1;
    this.readTarget = 0;
    this.targets = [];
    this.accumulatedFrames = 0;
    this.pointerX = 0.5;
    this.pointerY = 0.5;
    this.capsuleVisible = false;
    this.navRect = [0, 0, 1, 1];
    this.capsuleRect = [0, 0, 1, 1];
    this.previousCapsuleRect = [...HIDDEN_CAPSULE_RECT];
    this.capsuleAccumulatedFrames = 0;
    this.capsuleOnlyUpdate = false;
    this.animationFrame = 0;
    this.captureFrame = 0;
    this.captureTimer = 0;
    this.lastCaptureTime = 0;
    this.lastFrameMs = 0;
    this.lastStatsTime = 0;
    this.targetFormat = null;
    this.targetType = null;
    this.observers = [];
    this.activeTouchPointerId = null;
    this.pendingTouchPoint = null;
    this.touchMoveFrame = 0;
    this.readabilityCanvas = document.createElement("canvas");
    this.readabilityCanvas.width = 7;
    this.readabilityCanvas.height = 3;
    this.readabilityContext = this.readabilityCanvas.getContext("2d", {
      alpha: false,
      willReadFrequently: true,
    });
    this.canvas.dataset.performanceProfile = this.options.mobileProfile ? "mobile" : "desktop";

    try {
      this.initializeWebGL();
      this.capture = new DomSceneCapture({
        sceneRoot: this.sceneRoot,
        glassCanvas: this.canvas,
        background: this.options.captureBackground,
        allowCrossOriginImages: this.options.allowCrossOriginImages,
        ignoredElements: this.options.captureNavigationContent
          ? []
          : [this.navElement],
      });
      this.supported = true;
      this.navElement.classList.remove("liquid-glass--unsupported");
    } catch (error) {
      this.markUnsupported(error);
    }
  }

  initializeWebGL() {
    const gl = this.canvas.getContext("webgl2", {
      alpha: true,
      antialias: false,
      depth: false,
      stencil: false,
      premultipliedAlpha: false,
      powerPreference: "high-performance",
    });
    if (!gl) throw new Error("WebGL2 is not available in this browser.");
    this.gl = gl;

    this.traceProgram = createProgram(
      gl,
      vertexShaderSource,
      createTraceFragmentShader(
        this.options.pathsPerPixel,
        this.options.blurSampleCount,
        this.options.capsulePathsPerPixel,
        this.options.capsuleMaxAccumulation,
      ),
    );
    this.displayProgram = createProgram(
      gl,
      vertexShaderSource,
      displayFragmentShaderSource,
    );
    this.traceUniforms = Object.fromEntries(
      TRACE_UNIFORM_NAMES.map((name) => [name, gl.getUniformLocation(this.traceProgram, name)]),
    );
    this.displayTextureUniform = gl.getUniformLocation(this.displayProgram, "uTexture");

    this.vertexArray = gl.createVertexArray();
    this.vertexBuffer = gl.createBuffer();
    gl.bindVertexArray(this.vertexArray);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
    gl.bufferData(
      gl.ARRAY_BUFFER,
      new Float32Array([-1, -1, 3, -1, -1, 3]),
      gl.STATIC_DRAW,
    );
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    this.sceneTexture = createSourceTexture(gl);
    this.maskTexture = createSourceTexture(gl, [0, 0, 0, 255]);
    const supportsFloatTargets = Boolean(
      gl.getExtension("EXT_color_buffer_float") &&
      gl.getExtension("OES_texture_float_linear"),
    );
    const useFloatTargets = this.options.preferFloatTargets && supportsFloatTargets;
    this.targetFormat = useFloatTargets ? gl.RGBA16F : gl.RGBA8;
    this.targetType = useFloatTargets ? gl.HALF_FLOAT : gl.UNSIGNED_BYTE;
    this.canvas.dataset.targetPrecision = useFloatTargets ? "rgba16f" : "rgba8";
  }

  markUnsupported(error) {
    this.supported = false;
    this.navElement.classList.add("liquid-glass--unsupported");
    this.canvas.hidden = true;
    const reason = error instanceof Error ? error.message : String(error);
    this.options.onError?.(new Error(reason));
    console.warn("[web-liquid-glass] WebGL2 renderer unavailable:", reason);
  }

  isSupported() {
    return this.supported;
  }

  start() {
    if (!this.supported || this.running || this.disposed) return this.supported;
    try {
      this.running = true;
      this.attachEvents();
      this.resize();
      this.refreshScene();
      this.options.onReady?.();
      return true;
    } catch (error) {
      this.running = false;
      this.detachEvents();
      this.markUnsupported(error);
      return false;
    }
  }

  attachEvents() {
    this.pointerElement.addEventListener("pointerdown", this.handlePointerDown);
    this.pointerElement.addEventListener("pointermove", this.handlePointerMove);
    this.pointerElement.addEventListener("pointerleave", this.hideCapsule);
    window.addEventListener("pointerup", this.handlePointerEnd);
    window.addEventListener("pointercancel", this.handlePointerEnd);
    this.navElement.addEventListener("click", this.handleNavigationClick);
    window.addEventListener("scroll", this.refreshScene, { passive: true });
    window.addEventListener("resize", this.resize, { passive: true });
    document.addEventListener("load", this.handleResourceLoad, true);

    if (this.options.observe && "ResizeObserver" in window) {
      const observer = new ResizeObserver(this.resize);
      observer.observe(this.navElement);
      observer.observe(this.canvas);
      this.observers.push(observer);
    }

    if (this.options.observe && "MutationObserver" in window) {
      const observer = new MutationObserver((mutations) => {
        const relevant = mutations.some((mutation) => {
          const element = mutation.target instanceof Element
            ? mutation.target
            : mutation.target.parentElement;
          return Boolean(
            element &&
            element !== this.canvas &&
            !this.navElement.contains(element) &&
            !element.closest("[data-liquid-glass-canvas]") &&
            !element.closest("[data-liquid-glass-ignore]"),
          );
        });
        if (relevant) this.refreshScene();
      });
      observer.observe(this.sceneRoot, {
        childList: true,
        subtree: true,
        characterData: true,
        attributes: true,
        attributeFilter: ["class", "hidden", "src", "style"],
      });
      this.observers.push(observer);
    }

    if (document.fonts?.ready) {
      document.fonts.ready.then(() => {
        if (!this.disposed) this.refreshScene();
      });
    }
  }

  detachEvents() {
    this.pointerElement.removeEventListener("pointerdown", this.handlePointerDown);
    this.pointerElement.removeEventListener("pointermove", this.handlePointerMove);
    this.pointerElement.removeEventListener("pointerleave", this.hideCapsule);
    window.removeEventListener("pointerup", this.handlePointerEnd);
    window.removeEventListener("pointercancel", this.handlePointerEnd);
    this.navElement.removeEventListener("click", this.handleNavigationClick);
    window.removeEventListener("scroll", this.refreshScene);
    window.removeEventListener("resize", this.resize);
    document.removeEventListener("load", this.handleResourceLoad, true);
    this.observers.forEach((observer) => observer.disconnect());
    this.observers.length = 0;
  }

  handlePointerDown = (event) => {
    if (
      !this.options.mobileProfile ||
      !this.options.capsuleOnTouch ||
      event.pointerType === "mouse"
    ) return;
    this.activeTouchPointerId = event.pointerId;
    this.pointerElement.setPointerCapture?.(event.pointerId);
    const bounds = this.pointerElement.getBoundingClientRect();
    this.setPointer(
      (event.clientX - bounds.left) / Math.max(1, bounds.width),
      (event.clientY - bounds.top) / Math.max(1, bounds.height),
    );
  };

  handlePointerMove = (event) => {
    if (this.options.mobileProfile && event.pointerType !== "mouse") {
      if (event.pointerId !== this.activeTouchPointerId) return;
      this.pendingTouchPoint = { x: event.clientX, y: event.clientY };
      if (!this.touchMoveFrame) {
        this.touchMoveFrame = requestAnimationFrame(() => {
          this.touchMoveFrame = 0;
          const point = this.pendingTouchPoint;
          this.pendingTouchPoint = null;
          if (!point || this.activeTouchPointerId === null) return;
          const bounds = this.pointerElement.getBoundingClientRect();
          this.setPointer(
            (point.x - bounds.left) / Math.max(1, bounds.width),
            (point.y - bounds.top) / Math.max(1, bounds.height),
          );
        });
      }
      return;
    }
    if (!this.options.capsuleOnTouch && event.pointerType !== "mouse") {
      this.hideCapsule();
      return;
    }
    const bounds = this.pointerElement.getBoundingClientRect();
    this.setPointer(
      (event.clientX - bounds.left) / Math.max(1, bounds.width),
      (event.clientY - bounds.top) / Math.max(1, bounds.height),
    );
  };

  handlePointerEnd = (event) => {
    if (event.pointerId !== this.activeTouchPointerId) return;
    if (this.pointerElement.hasPointerCapture?.(event.pointerId)) {
      this.pointerElement.releasePointerCapture(event.pointerId);
    }
    this.activeTouchPointerId = null;
    this.pendingTouchPoint = null;
    cancelAnimationFrame(this.touchMoveFrame);
    this.touchMoveFrame = 0;
    this.hideCapsule();
  };

  handleNavigationClick = () => {
    window.setTimeout(this.refreshScene, 0);
  };

  handleResourceLoad = (event) => {
    if (
      event.target instanceof HTMLImageElement ||
      event.target instanceof HTMLVideoElement
    ) {
      this.refreshScene();
    }
  };

  setPointer(x, y = 0.5) {
    if (!this.supported || this.disposed) return;
    this.previousCapsuleRect = this.capsuleVisible
      ? [...this.capsuleRect]
      : [...HIDDEN_CAPSULE_RECT];
    this.pointerX = clamp(x, 0, 1);
    this.pointerY = clamp(y, 0, 1);
    this.capsuleVisible = true;
    this.updateRects();
    this.resetCapsuleAccumulation();
  }

  hideCapsule = (event) => {
    if (
      this.options.mobileProfile &&
      this.options.capsuleOnTouch &&
      event?.pointerType &&
      event.pointerType !== "mouse"
    ) return;
    if (!this.capsuleVisible || this.disposed) return;
    this.previousCapsuleRect = [...this.capsuleRect];
    this.capsuleVisible = false;
    this.updateRects();
    this.resetCapsuleAccumulation();
  };

  /** Update optical material uniforms without recreating the renderer. */
  setMaterial(patch = {}) {
    if (!patch || typeof patch !== "object" || this.disposed) return;
    this.options.material = {
      ...this.options.material,
      ...patch,
    };
    this.resetAccumulation();
  }

  /** Update the hover capsule geometry and redraw immediately. */
  setCapsuleGeometry({ widthRatio, heightScale } = {}) {
    if (Number.isFinite(widthRatio)) {
      this.options.capsuleWidthRatio = Math.max(0.01, widthRatio);
    }
    if (Number.isFinite(heightScale)) {
      this.options.capsuleHeightScale = Math.max(0.01, heightScale);
    }
    this.updateRects();
    this.resetAccumulation();
  }

  /** Change the internal resolution cap. This reallocates render targets. */
  setMaxDpr(value) {
    if (!Number.isFinite(value) || this.disposed) return;
    this.options.maxDpr = Math.max(0.25, value);
    this.resize();
  }

  setMaxAccumulation(value) {
    if (!Number.isFinite(value) || this.disposed) return;
    this.options.maxAccumulation = Math.max(1, Math.round(value));
    this.resetAccumulation();
  }

  /** Recompile only the trace shader; textures and capture state are preserved. */
  setPathsPerPixel(value) {
    if (!this.supported || this.disposed || !Number.isFinite(value)) return false;
    const paths = Math.max(1, Math.min(12, Math.round(value)));
    if (paths === this.options.pathsPerPixel) return true;

    const gl = this.gl;
    const nextProgram = createProgram(
      gl,
      vertexShaderSource,
      createTraceFragmentShader(
        paths,
        this.options.blurSampleCount,
        this.options.capsulePathsPerPixel,
        this.options.capsuleMaxAccumulation,
      ),
    );
    const previousProgram = this.traceProgram;
    this.traceProgram = nextProgram;
    this.traceUniforms = Object.fromEntries(
      TRACE_UNIFORM_NAMES.map((name) => [name, gl.getUniformLocation(nextProgram, name)]),
    );
    this.options.pathsPerPixel = paths;
    gl.deleteProgram(previousProgram);
    this.resetAccumulation();
    return true;
  }

  resize = () => {
    if (!this.supported || this.disposed) return;
    const cssWidth = Math.max(1, this.canvas.clientWidth);
    const cssHeight = Math.max(1, this.canvas.clientHeight);
    const nextDpr = Math.min(window.devicePixelRatio || 1, this.options.maxDpr);
    const width = Math.max(1, Math.round(cssWidth * nextDpr));
    const height = Math.max(1, Math.round(cssHeight * nextDpr));
    const changed = (
      this.canvas.width !== width ||
      this.canvas.height !== height ||
      this.pixelRatio !== nextDpr
    );

    this.pixelRatio = nextDpr;
    if (changed) {
      this.canvas.width = width;
      this.canvas.height = height;
      this.capture.resize(cssWidth, cssHeight, this.pixelRatio);
      this.allocateTargets(width, height);
    }
    this.updateRects();
    this.refreshScene();
  };

  updateRects() {
    const canvasBounds = this.canvas.getBoundingClientRect();
    const navBounds = this.navElement.getBoundingClientRect();
    const nav = rectRelativeToViewport(navBounds, canvasBounds, this.pixelRatio);
    this.navRect = [nav.x, nav.y, nav.width, nav.height];

    const localNavLeft = navBounds.left - canvasBounds.left;
    const localNavTop = navBounds.top - canvasBounds.top;
    const capsule = computeCapsuleRect({
      navLeft: localNavLeft,
      navTop: localNavTop,
      navWidth: navBounds.width,
      navHeight: navBounds.height,
      pointerX: this.pointerX,
      widthRatio: this.options.capsuleWidthRatio,
      heightScale: this.options.capsuleHeightScale,
    });
    this.capsuleRect = [
      capsule.x * this.pixelRatio,
      capsule.y * this.pixelRatio,
      capsule.width * this.pixelRatio,
      capsule.height * this.pixelRatio,
    ];
  }

  createTarget(width, height) {
    const gl = this.gl;
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      this.targetFormat,
      width,
      height,
      0,
      gl.RGBA,
      this.targetType,
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
      throw new Error("Liquid-glass framebuffer is incomplete.");
    }
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    return { texture, framebuffer };
  }

  allocateTargets(width, height) {
    const gl = this.gl;
    this.targets.forEach(({ texture, framebuffer }) => {
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
    });
    this.targets = [];

    try {
      this.targets = [this.createTarget(width, height), this.createTarget(width, height)];
    } catch (error) {
      if (this.targetFormat === gl.RGBA8) throw error;
      this.targetFormat = gl.RGBA8;
      this.targetType = gl.UNSIGNED_BYTE;
      this.targets = [this.createTarget(width, height), this.createTarget(width, height)];
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    this.readTarget = 0;
    this.resetAccumulation();
  }

  refreshScene = () => {
    if (
      !this.supported ||
      this.disposed ||
      this.captureFrame ||
      this.captureTimer
    ) return;
    this.captureFrame = requestAnimationFrame((timestamp) => {
      this.captureFrame = 0;
      const delay = Math.max(
        0,
        this.options.captureMinIntervalMs - (timestamp - this.lastCaptureTime),
      );
      if (delay > 0) {
        this.captureTimer = window.setTimeout(() => {
          this.captureTimer = 0;
          this.refreshScene();
        }, delay);
        return;
      }
      this.lastCaptureTime = timestamp;
      this.captureSceneNow();
    });
  };

  captureSceneNow() {
    if (!this.canvas.width || !this.canvas.height) return;
    const gl = this.gl;
    const viewport = this.canvas.getBoundingClientRect();
    const { sceneCanvas, maskCanvas } = this.capture.capture(
      viewport,
      this.pixelRatio,
    );
    this.updateNavigationContrast(sceneCanvas, viewport);

    try {
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      gl.bindTexture(gl.TEXTURE_2D, this.sceneTexture);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        gl.RGBA,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        sceneCanvas,
      );
      gl.bindTexture(gl.TEXTURE_2D, this.maskTexture);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        gl.RGBA,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        maskCanvas,
      );
    } catch (error) {
      this.options.onError?.(error);
      console.error("[web-liquid-glass] Scene texture upload failed.", error);
      return;
    } finally {
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    }
    this.resetAccumulation();
  }

  updateNavigationContrast(sceneCanvas, viewport) {
    if (!this.options.autoLabelContrast || !this.readabilityContext) return;
    const labels = this.navElement.querySelectorAll(this.options.labelSelector);
    const context = this.readabilityContext;

    labels.forEach((label) => {
      const rect = label.getBoundingClientRect();
      const sourceX = Math.max(0, (rect.left - viewport.left) * this.pixelRatio);
      const sourceY = Math.max(0, (rect.top - viewport.top) * this.pixelRatio);
      const sourceWidth = Math.min(
        sceneCanvas.width - sourceX,
        rect.width * this.pixelRatio,
      );
      const sourceHeight = Math.min(
        sceneCanvas.height - sourceY,
        rect.height * this.pixelRatio,
      );
      if (sourceWidth <= 0 || sourceHeight <= 0) return;

      context.clearRect(0, 0, 7, 3);
      context.drawImage(
        sceneCanvas,
        sourceX,
        sourceY,
        sourceWidth,
        sourceHeight,
        0,
        0,
        7,
        3,
      );
      const pixels = context.getImageData(0, 0, 7, 3).data;
      let luminance = 0;
      for (let index = 0; index < pixels.length; index += 4) {
        luminance += relativeLuminance(
          pixels[index],
          pixels[index + 1],
          pixels[index + 2],
        );
      }
      luminance /= pixels.length / 4;
      label.dataset.liquidGlassTone = chooseReadableTextTone(luminance);
      label.style.setProperty(
        "--liquid-glass-label-luminance",
        luminance.toFixed(4),
      );
    });
  }

  applyUniforms() {
    const gl = this.gl;
    const uniforms = this.traceUniforms;
    const material = this.options.material;

    gl.useProgram(this.traceProgram);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.sceneTexture);
    gl.uniform1i(uniforms.uScene, 0);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, this.maskTexture);
    gl.uniform1i(uniforms.uTextMask, 1);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, this.targets[this.readTarget].texture);
    gl.uniform1i(uniforms.uPrevious, 2);

    gl.uniform2f(uniforms.uResolution, this.canvas.width, this.canvas.height);
    gl.uniform4fv(uniforms.uNavRect, this.navRect);
    gl.uniform4fv(uniforms.uCapsuleRect, this.capsuleRect);
    gl.uniform4fv(uniforms.uPreviousCapsuleRect, this.previousCapsuleRect);
    gl.uniform1f(uniforms.uCapsuleVisible, this.capsuleVisible ? 1 : 0);
    gl.uniform1f(uniforms.uCapsuleOnly, this.capsuleOnlyUpdate ? 1 : 0);
    const activeFrame = this.capsuleOnlyUpdate
      ? this.capsuleAccumulatedFrames
      : this.accumulatedFrames;
    gl.uniform1f(uniforms.uFrame, activeFrame);
    gl.uniform1f(uniforms.uReset, activeFrame === 0 ? 1 : 0);
    gl.uniform2f(uniforms.uMouse, this.pointerX, this.pointerY);

    gl.uniform1f(uniforms.uReverseDisplacement, material.reverseDisplacement);
    gl.uniform1f(uniforms.uEdgeCurvature, material.edgeCurvature);
    gl.uniform1f(uniforms.uOpticalThickness, material.opticalThickness);
    gl.uniform1f(uniforms.uEdgeBandRatio, material.edgeBandRatio);
    gl.uniform1f(uniforms.uRefractionVisibleRatio, material.refractionVisibleRatio);
    // Match the previous web implementation exactly: these are shader-space
    // pixel constants and must not be multiplied by devicePixelRatio again.
    gl.uniform1f(uniforms.uBlendFeather, material.blendFeatherPx);
    gl.uniform1f(uniforms.uFrostedStrength, material.frostedStrength);
    gl.uniform1f(uniforms.uFrostedAttenuation, material.frostedAttenuation);
    gl.uniform1f(uniforms.uBlurSpacing, material.blurSpacingPx);
    gl.uniform1f(uniforms.uDispersionStrength, material.dispersionStrength);
    gl.uniform1f(uniforms.uHighlightStrength, material.highlightStrength);
    gl.uniform3fv(uniforms.uTintColor, material.tintColor);
    gl.uniform1f(uniforms.uTintMix, material.tintMix);
  }

  drawFrame = (timestamp = performance.now()) => {
    this.animationFrame = 0;
    if (!this.running || this.disposed || this.targets.length !== 2) return;
    const started = performance.now();
    const gl = this.gl;
    const writeTarget = 1 - this.readTarget;

    gl.disable(gl.BLEND);
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.bindVertexArray(this.vertexArray);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.targets[writeTarget].framebuffer);
    this.applyUniforms();
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.useProgram(this.displayProgram);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.targets[writeTarget].texture);
    gl.uniform1i(this.displayTextureUniform, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    this.readTarget = writeTarget;
    if (this.capsuleOnlyUpdate) {
      this.capsuleAccumulatedFrames = Math.min(
        this.options.capsuleMaxAccumulation,
        this.capsuleAccumulatedFrames + 1,
      );
    } else {
      this.accumulatedFrames = Math.min(
        this.options.maxAccumulation,
        this.accumulatedFrames + 1,
      );
    }
    this.lastFrameMs = performance.now() - started;

    if (timestamp - this.lastStatsTime > 250) {
      this.lastStatsTime = timestamp;
      this.options.onStats?.(this.getStats());
    }
    const needsMoreFrames = this.capsuleOnlyUpdate
      ? this.capsuleAccumulatedFrames < this.options.capsuleMaxAccumulation
      : this.accumulatedFrames < this.options.maxAccumulation;
    if (needsMoreFrames) {
      this.animationFrame = requestAnimationFrame(this.drawFrame);
    } else if (this.capsuleOnlyUpdate) {
      this.capsuleOnlyUpdate = false;
      this.previousCapsuleRect = [...HIDDEN_CAPSULE_RECT];
    }
  };

  resetAccumulation() {
    if (!this.supported || this.disposed) return;
    this.capsuleOnlyUpdate = false;
    this.capsuleAccumulatedFrames = 0;
    this.previousCapsuleRect = [...HIDDEN_CAPSULE_RECT];
    this.accumulatedFrames = 0;
    if (this.animationFrame) cancelAnimationFrame(this.animationFrame);
    if (this.running) this.animationFrame = requestAnimationFrame(this.drawFrame);
  }

  resetCapsuleAccumulation() {
    if (!this.supported || this.disposed) return;
    this.capsuleOnlyUpdate = true;
    this.capsuleAccumulatedFrames = 0;
    if (this.animationFrame) cancelAnimationFrame(this.animationFrame);
    if (this.running) this.animationFrame = requestAnimationFrame(this.drawFrame);
  }

  getStats() {
    return {
      supported: this.supported,
      renderer: "WebGL2 analytic path tracing",
      accumulatedFrames: this.accumulatedFrames,
      pathsPerPixel: this.options.pathsPerPixel,
      capsulePathsPerPixel: this.options.capsulePathsPerPixel,
      capsuleAccumulatedFrames: this.capsuleAccumulatedFrames,
      capsuleMaxAccumulation: this.options.capsuleMaxAccumulation,
      frameMs: this.lastFrameMs,
      pixelRatio: this.pixelRatio,
      performanceProfile: this.options.mobileProfile ? "mobile" : "desktop",
      blurSampleCount: this.options.blurSampleCount,
      targetPrecision: this.targetFormat === this.gl?.RGBA16F ? "rgba16f" : "rgba8",
    };
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    this.running = false;
    this.detachEvents();
    cancelAnimationFrame(this.animationFrame);
    cancelAnimationFrame(this.captureFrame);
    cancelAnimationFrame(this.touchMoveFrame);
    clearTimeout(this.captureTimer);
    if (!this.gl) return;

    const gl = this.gl;
    this.targets.forEach(({ texture, framebuffer }) => {
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
    });
    if (this.sceneTexture) gl.deleteTexture(this.sceneTexture);
    if (this.maskTexture) gl.deleteTexture(this.maskTexture);
    if (this.vertexBuffer) gl.deleteBuffer(this.vertexBuffer);
    if (this.vertexArray) gl.deleteVertexArray(this.vertexArray);
    if (this.traceProgram) gl.deleteProgram(this.traceProgram);
    if (this.displayProgram) gl.deleteProgram(this.displayProgram);
    this.targets = [];
  }
}
