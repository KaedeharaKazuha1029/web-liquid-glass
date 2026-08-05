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

function localRect(rect, viewport) {
  return {
    x: rect.left - viewport.left,
    y: rect.top - viewport.top,
    width: rect.width,
    height: rect.height,
  };
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

function isVisible(style) {
  return !(
    style.display === "none" ||
    style.visibility === "hidden" ||
    Number.parseFloat(style.opacity) === 0
  );
}

export class DomSceneCapture {
  constructor({
    sceneRoot,
    glassCanvas,
    background = "#111117",
    allowCrossOriginImages = false,
    ignoredElements = [],
  }) {
    this.sceneRoot = sceneRoot;
    this.glassCanvas = glassCanvas;
    this.background = background;
    this.allowCrossOriginImages = allowCrossOriginImages;
    this.ignoredElements = new Set(ignoredElements);
    this.sceneCanvas = document.createElement("canvas");
    this.maskCanvas = document.createElement("canvas");
    this.sceneContext = this.sceneCanvas.getContext("2d", { alpha: false });
    this.maskContext = this.maskCanvas.getContext("2d", { alpha: false });

    if (!this.sceneContext || !this.maskContext) {
      throw new Error("Canvas 2D is required to capture the DOM scene.");
    }
  }

  resize(cssWidth, cssHeight, pixelRatio) {
    const width = Math.max(1, Math.round(cssWidth * pixelRatio));
    const height = Math.max(1, Math.round(cssHeight * pixelRatio));
    if (this.sceneCanvas.width !== width || this.sceneCanvas.height !== height) {
      this.sceneCanvas.width = width;
      this.sceneCanvas.height = height;
      this.maskCanvas.width = width;
      this.maskCanvas.height = height;
    }
  }

  capture(viewport, pixelRatio) {
    const scene = this.sceneContext;
    const mask = this.maskContext;
    const width = viewport.width;
    const height = viewport.height;

    scene.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    mask.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    scene.clearRect(0, 0, width, height);
    mask.clearRect(0, 0, width, height);
    scene.fillStyle = this.background;
    scene.fillRect(0, 0, width, height);
    mask.fillStyle = "#000";
    mask.fillRect(0, 0, width, height);

    this.paintTree(this.sceneRoot, viewport);
    return {
      sceneCanvas: this.sceneCanvas,
      maskCanvas: this.maskCanvas,
    };
  }

  paintElementBox(element, style, rect, viewport) {
    const context = this.sceneContext;
    const local = localRect(rect, viewport);
    const radius = Number.parseFloat(style.borderTopLeftRadius) || 0;

    if (!isTransparent(style.backgroundColor)) {
      roundedPath(context, local.x, local.y, local.width, local.height, radius);
      context.fillStyle = style.backgroundColor;
      context.fill();
    }

    const borderWidth = Number.parseFloat(style.borderTopWidth) || 0;
    if (borderWidth > 0 && !isTransparent(style.borderTopColor)) {
      roundedPath(
        context,
        local.x + borderWidth * 0.5,
        local.y + borderWidth * 0.5,
        Math.max(0, local.width - borderWidth),
        Math.max(0, local.height - borderWidth),
        radius,
      );
      context.strokeStyle = style.borderTopColor;
      context.lineWidth = borderWidth;
      context.stroke();
    }
  }

  canDrawImage(image) {
    const source = image.currentSrc || image.src;
    if (!source) return false;
    try {
      const url = new URL(source, document.baseURI);
      return (
        url.origin === window.location.origin ||
        (this.allowCrossOriginImages && image.crossOrigin === "anonymous")
      );
    } catch {
      return false;
    }
  }

  paintImage(image, rect, viewport, style) {
    if (
      !image.complete ||
      !image.naturalWidth ||
      !image.naturalHeight ||
      !this.canDrawImage(image)
    ) return;

    const context = this.sceneContext;
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
    context.save();
    if (radius > 0) {
      roundedPath(context, local.x, local.y, local.width, local.height, radius);
      context.clip();
    }
    context.drawImage(image, sx, sy, sw, sh, dx, dy, dw, dh);
    context.restore();
  }

  paintCanvas(sourceCanvas, rect, viewport, style) {
    if (!sourceCanvas.width || !sourceCanvas.height) return;
    const context = this.sceneContext;
    const local = localRect(rect, viewport);
    const radius = Number.parseFloat(style.borderTopLeftRadius) || 0;
    context.save();
    if (radius > 0) {
      roundedPath(context, local.x, local.y, local.width, local.height, radius);
      context.clip();
    }
    context.drawImage(sourceCanvas, local.x, local.y, local.width, local.height);
    context.restore();
  }

  paintTextNode(node, viewport) {
    const text = node.nodeValue || "";
    if (!text.trim() || !node.parentElement) return;
    const style = getComputedStyle(node.parentElement);
    if (!isVisible(style)) return;

    const wholeRange = document.createRange();
    wholeRange.selectNodeContents(node);
    const wholeRect = wholeRange.getBoundingClientRect();
    wholeRange.detach();
    if (!wholeRect.width || !wholeRect.height || !intersects(wholeRect, viewport)) {
      return;
    }

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

    const scene = this.sceneContext;
    const mask = this.maskContext;
    const fontSize = Number.parseFloat(style.fontSize) || 16;
    const font = [
      style.fontStyle,
      style.fontVariant,
      style.fontWeight,
      style.fontSize,
      style.fontFamily,
    ].join(" ");
    scene.font = font;
    scene.textAlign = "left";
    scene.textBaseline = "alphabetic";
    scene.fillStyle = style.color;
    mask.font = font;
    mask.textAlign = "left";
    mask.textBaseline = "alphabetic";
    mask.fillStyle = "#fff";
    if ("letterSpacing" in scene) scene.letterSpacing = style.letterSpacing;
    if ("letterSpacing" in mask) mask.letterSpacing = style.letterSpacing;

    lines.forEach((line) => {
      const x = line.left - viewport.left;
      const y = line.bottom - viewport.top
        - Math.max(0, (line.bottom - line.top - fontSize) * 0.2);
      scene.fillText(line.text, x, y);
      mask.fillText(line.text, x, y);
    });
  }

  paintTree(node, viewport) {
    if (node === this.glassCanvas) return;
    if (node.nodeType === Node.TEXT_NODE) {
      this.paintTextNode(node, viewport);
      return;
    }
    if (!(node instanceof Element)) return;
    if (this.ignoredElements.has(node)) return;
    if (node.matches("[data-liquid-glass-canvas]")) return;
    if (node.matches("[data-liquid-glass-ignore]")) return;

    const style = getComputedStyle(node);
    if (!isVisible(style)) return;
    const rect = node.getBoundingClientRect();
    if (!intersects(rect, viewport)) return;

    const scene = this.sceneContext;
    const mask = this.maskContext;
    scene.save();
    mask.save();
    const opacity = Number.parseFloat(style.opacity);
    if (Number.isFinite(opacity)) {
      scene.globalAlpha *= opacity;
      mask.globalAlpha *= opacity;
    }

    this.paintElementBox(node, style, rect, viewport);
    if (node instanceof HTMLImageElement) {
      this.paintImage(node, rect, viewport, style);
    } else if (node instanceof HTMLCanvasElement) {
      this.paintCanvas(node, rect, viewport, style);
    } else {
      node.childNodes.forEach((child) => this.paintTree(child, viewport));
    }
    scene.restore();
    mask.restore();
  }
}
