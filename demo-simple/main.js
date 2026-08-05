import { liquidGlass } from "../src/index.js";

const nav = document.querySelector("#bottomNav");
const status = document.querySelector("#renderStatus");
const statusText = document.querySelector("#renderStatusText");
const navItems = [...document.querySelectorAll(".nav-btn")];
const sections = [...document.querySelectorAll("[data-section]")];

function showReadyStatus() {
  const canvas = nav.querySelector("canvas[data-liquid-glass-canvas]");
  const mobile = canvas?.dataset.performanceProfile === "mobile";
  status.classList.remove("is-error");
  status.classList.add("is-ready");
  statusText.textContent = mobile
    ? "简化接口 · 自动 Canvas · 移动性能档"
    : "简化接口 · 自动 Canvas · 桌面参考档";
}

function showErrorStatus(error) {
  status.classList.remove("is-ready");
  status.classList.add("is-error");
  statusText.textContent = `简化接口初始化失败：${error.message}`;
}

let glass;
try {
  // This is the entire optical integration. The function creates the Canvas,
  // uses the approved material preset and starts the renderer automatically.
  glass = liquidGlass("#bottomNav", {
    sceneRoot: "#sceneRoot",
    onReady: showReadyStatus,
    onError: showErrorStatus,
  });
  glass.canvas.dataset.simpleApiTest = "true";
} catch (error) {
  showErrorStatus(error instanceof Error ? error : new Error(String(error)));
}

function setActiveSection(id) {
  navItems.forEach((item) => {
    const active = item.dataset.target === id;
    item.classList.toggle("active", active);
    item.setAttribute("aria-pressed", String(active));
  });
}

navItems.forEach((item) => {
  item.addEventListener("click", () => {
    document.getElementById(item.dataset.target)?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
    setActiveSection(item.dataset.target);
  });
});

const sectionObserver = new IntersectionObserver(
  (entries) => {
    const visible = entries
      .filter((entry) => entry.isIntersecting)
      .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
    if (visible) setActiveSection(visible.target.dataset.section);
  },
  { threshold: [0.35, 0.55, 0.75] },
);

sections.forEach((section) => sectionObserver.observe(section));

window.addEventListener("beforeunload", () => {
  sectionObserver.disconnect();
  glass?.dispose();
});
