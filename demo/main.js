const nav = document.querySelector("#bottomNav");
const status = document.querySelector("#renderStatus");
const statusText = document.querySelector("#renderStatusText");
const glassCanvas = document.querySelector("#liquidGlassNavCanvas");
const navItems = [...document.querySelectorAll(".nav-btn")];
const sections = [...document.querySelectorAll("[data-section]")];

if (nav.classList.contains("liquid-glass-css-only")) {
  status.classList.add("is-error");
  statusText.textContent = "此浏览器不支持 WebGL2";
} else {
  status.classList.add("is-ready");
  statusText.textContent = glassCanvas?.dataset.performanceProfile === "mobile"
    ? "WebGL2 移动性能档 · 2 paths"
    : "WebGL2 参考导航运行中";
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
});
