const MATERIAL_CONTROLS = [
  ["reverseDisplacement", "反向位移", 0, 6, 0.01, "×"],
  ["edgeCurvature", "边缘曲率", 0, 2, 0.01, "×"],
  ["opticalThickness", "光学厚度", 0, 4, 0.01, "×"],
  ["edgeBandRatio", "折射带宽", 0, 0.6, 0.005, "%", 100],
  ["refractionVisibleRatio", "折射可见区", 0, 0.5, 0.005, "%", 100],
  ["blendFeatherPx", "融合柔化", 0, 36, 0.25, "px"],
  ["frostedStrength", "毛玻璃强度", 0, 1, 0.01, "%", 100],
  ["frostedAttenuation", "毛玻璃衰减", 0, 1, 0.01, "%", 100],
  ["blurSpacingPx", "高斯采样间距", 0, 6, 0.05, "px"],
  ["dispersionStrength", "RGB 色散", 0, 1, 0.01, "%", 100],
  ["highlightStrength", "对角高光", 0, 0.25, 0.001, ""],
  ["tintMix", "微黑染色混合", 0, 0.6, 0.005, "%", 100],
  ["tintR", "染色 R", 0, 0.25, 0.001, ""],
  ["tintG", "染色 G", 0, 0.25, 0.001, ""],
  ["tintB", "染色 B", 0, 0.25, 0.001, ""],
];

const CAPSULE_CONTROLS = [
  ["capsuleWidthRatio", "胶囊宽度", 0.05, 1, 0.01, "%", 100],
  ["capsuleHeightScale", "胶囊高度", 0.5, 2, 0.01, "×"],
];

const LAYOUT_CONTROLS = [
  ["navWidthVw", "导航栏视口宽度", 30, 95, 1, "vw"],
  ["navMaxWidthPx", "导航栏最大宽度", 320, 1100, 10, "px"],
  ["navHeightPx", "导航栏高度", 40, 120, 1, "px"],
  ["zoneHeightPx", "鼠标感应区高度", 50, 180, 1, "px"],
  ["navTopPx", "导航栏区内偏移", 0, 60, 1, "px"],
  ["navBottomPx", "距离窗口底部", 0, 180, 1, "px"],
];

const RENDER_CONTROLS = [
  ["maxDpr", "DPR 上限", 0.5, 2.5, 0.05, "×"],
  ["pathsPerPixel", "每像素路径数", 1, 12, 1, ""],
  ["maxAccumulation", "时间累积帧", 1, 128, 1, "帧"],
];

function decimals(step) {
  const text = String(step);
  return text.includes(".") ? text.length - text.indexOf(".") - 1 : 0;
}

function formatValue(value, step, unit, multiplier = 1) {
  return `${(value * multiplier).toFixed(decimals(step * multiplier))}${unit}`;
}

function createRange(definition, value, onInput) {
  const [id, label, min, max, step, unit, multiplier = 1] = definition;
  const row = document.createElement("label");
  row.className = "tuning-row";
  row.htmlFor = `tuning-${id}`;

  const header = document.createElement("span");
  header.className = "tuning-row-header";
  const name = document.createElement("span");
  name.textContent = label;
  const output = document.createElement("output");
  output.htmlFor = `tuning-${id}`;
  header.append(name, output);

  const input = document.createElement("input");
  input.id = `tuning-${id}`;
  input.type = "range";
  input.min = String(min);
  input.max = String(max);
  input.step = String(step);
  input.value = String(value);

  const setValue = (next, emit = true) => {
    const numeric = Math.min(max, Math.max(min, Number(next)));
    input.value = String(numeric);
    output.value = formatValue(numeric, step, unit, multiplier);
    if (emit) onInput(numeric);
  };
  input.addEventListener("input", () => setValue(input.value));
  setValue(value, false);
  row.append(header, input);
  return { id, row, input, setValue, getValue: () => Number(input.value) };
}

function createGroup(title) {
  const section = document.createElement("section");
  section.className = "tuning-group";
  const heading = document.createElement("h3");
  heading.textContent = title;
  section.append(heading);
  return section;
}

export function createTuningPanel({ glass, nav, navZone, defaults }) {
  const values = {
    ...defaults.material,
    tintR: defaults.material.tintColor[0],
    tintG: defaults.material.tintColor[1],
    tintB: defaults.material.tintColor[2],
    capsuleWidthRatio: defaults.capsuleWidthRatio,
    capsuleHeightScale: defaults.capsuleHeightScale,
    navWidthVw: 58,
    navMaxWidthPx: 760,
    navHeightPx: 68,
    zoneHeightPx: 100,
    navTopPx: 16,
    navBottomPx: 74,
    maxDpr: defaults.maxDpr,
    pathsPerPixel: defaults.pathsPerPixel,
    maxAccumulation: defaults.maxAccumulation,
  };
  const initialValues = structuredClone(values);
  const controls = new Map();

  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "tuning-toggle";
  toggle.textContent = "参数";
  toggle.dataset.liquidGlassIgnore = "";

  const panel = document.createElement("aside");
  panel.className = "tuning-panel";
  panel.dataset.liquidGlassIgnore = "";
  panel.setAttribute("aria-label", "液态玻璃实时参数");
  panel.innerHTML = `
    <header class="tuning-panel-header">
      <div><strong>实时参数</strong><small>拖动后立即重新渲染</small></div>
      <button type="button" class="tuning-close" aria-label="隐藏参数面板">×</button>
    </header>
    <div class="tuning-panel-actions">
      <button type="button" data-action="reset">恢复当前默认值</button>
      <button type="button" data-action="copy">复制参数 JSON</button>
    </div>
    <div class="tuning-scroll"></div>
  `;
  const scroll = panel.querySelector(".tuning-scroll");

  const updateTint = () => {
    glass.setMaterial({ tintColor: [values.tintR, values.tintG, values.tintB] });
  };
  const materialGroup = createGroup("光学与毛玻璃");
  MATERIAL_CONTROLS.forEach((definition) => {
    const id = definition[0];
    const control = createRange(definition, values[id], (next) => {
      values[id] = next;
      if (id === "tintR" || id === "tintG" || id === "tintB") updateTint();
      else glass.setMaterial({ [id]: next });
    });
    controls.set(id, control);
    materialGroup.append(control.row);
  });

  const capsuleGroup = createGroup("悬停胶囊");
  CAPSULE_CONTROLS.forEach((definition) => {
    const id = definition[0];
    const control = createRange(definition, values[id], (next) => {
      values[id] = next;
      glass.setCapsuleGeometry({
        widthRatio: values.capsuleWidthRatio,
        heightScale: values.capsuleHeightScale,
      });
    });
    controls.set(id, control);
    capsuleGroup.append(control.row);
  });

  const requestLayoutRefresh = () => {
    requestAnimationFrame(() => glass.resize());
  };
  const layoutSetters = {
    navWidthVw: () => { navZone.style.width = `min(${values.navWidthVw}vw, ${values.navMaxWidthPx}px)`; },
    navMaxWidthPx: () => { navZone.style.width = `min(${values.navWidthVw}vw, ${values.navMaxWidthPx}px)`; },
    navHeightPx: () => { nav.style.height = `${values.navHeightPx}px`; },
    zoneHeightPx: () => { navZone.style.height = `${values.zoneHeightPx}px`; },
    navTopPx: () => { nav.style.top = `${values.navTopPx}px`; },
    navBottomPx: () => { navZone.style.bottom = `${values.navBottomPx}px`; },
  };
  const layoutGroup = createGroup("导航栏布局");
  LAYOUT_CONTROLS.forEach((definition) => {
    const id = definition[0];
    const control = createRange(definition, values[id], (next) => {
      values[id] = next;
      layoutSetters[id]();
      requestLayoutRefresh();
    });
    controls.set(id, control);
    layoutGroup.append(control.row);
  });

  const renderGroup = createGroup("渲染质量");
  RENDER_CONTROLS.forEach((definition) => {
    const id = definition[0];
    const control = createRange(definition, values[id], (next) => {
      values[id] = next;
      if (id === "maxDpr") glass.setMaxDpr(next);
      if (id === "pathsPerPixel") glass.setPathsPerPixel(next);
      if (id === "maxAccumulation") glass.setMaxAccumulation(next);
    });
    controls.set(id, control);
    renderGroup.append(control.row);
  });
  scroll.append(materialGroup, capsuleGroup, layoutGroup, renderGroup);

  const setOpen = (open) => {
    panel.hidden = !open;
    toggle.hidden = open;
    toggle.setAttribute("aria-expanded", String(open));
  };
  toggle.addEventListener("click", () => setOpen(panel.hidden));
  panel.querySelector(".tuning-close").addEventListener("click", () => setOpen(false));

  panel.querySelector('[data-action="reset"]').addEventListener("click", () => {
    Object.entries(initialValues).forEach(([id, value]) => {
      values[id] = value;
      controls.get(id)?.setValue(value);
    });
  });

  panel.querySelector('[data-action="copy"]').addEventListener("click", async (event) => {
    const snapshot = {
      material: {
        reverseDisplacement: values.reverseDisplacement,
        edgeCurvature: values.edgeCurvature,
        opticalThickness: values.opticalThickness,
        edgeBandRatio: values.edgeBandRatio,
        refractionVisibleRatio: values.refractionVisibleRatio,
        blendFeatherPx: values.blendFeatherPx,
        frostedStrength: values.frostedStrength,
        frostedAttenuation: values.frostedAttenuation,
        blurSpacingPx: values.blurSpacingPx,
        dispersionStrength: values.dispersionStrength,
        highlightStrength: values.highlightStrength,
        tintColor: [values.tintR, values.tintG, values.tintB],
        tintMix: values.tintMix,
      },
      capsule: {
        widthRatio: values.capsuleWidthRatio,
        heightScale: values.capsuleHeightScale,
      },
      layout: {
        widthVw: values.navWidthVw,
        maxWidthPx: values.navMaxWidthPx,
        heightPx: values.navHeightPx,
        pointerZoneHeightPx: values.zoneHeightPx,
        topInsideZonePx: values.navTopPx,
        bottomPx: values.navBottomPx,
      },
      rendering: {
        maxDpr: values.maxDpr,
        pathsPerPixel: values.pathsPerPixel,
        maxAccumulation: values.maxAccumulation,
      },
    };
    const button = event.currentTarget;
    try {
      await navigator.clipboard.writeText(JSON.stringify(snapshot, null, 2));
      button.textContent = "已复制";
    } catch {
      button.textContent = "复制失败";
    }
    window.setTimeout(() => { button.textContent = "复制参数 JSON"; }, 1200);
  });

  document.body.append(toggle, panel);
  setOpen(true);
  return { panel, toggle, controls };
}
