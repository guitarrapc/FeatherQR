// The color theme button in the footer. It imports and exports nothing, because the API page's generator embeds this file as it is.

const THEME_STORAGE_KEY = 'skqr-playground-color-mode';
const THEME_CYCLE_ORDER = ['system', 'light', 'dark'];

function getStoredColorMode() {
  try {
    const v = localStorage.getItem(THEME_STORAGE_KEY);
    if (v === 'light' || v === 'dark') return v;
  } catch (_) {
    /* ignore */
  }
  return 'system';
}

function setStoredColorMode(mode) {
  try {
    if (mode === 'system') localStorage.removeItem(THEME_STORAGE_KEY);
    else localStorage.setItem(THEME_STORAGE_KEY, mode);
  } catch (_) {
    /* ignore */
  }
}

function applyColorModeToDocument(mode) {
  const root = document.documentElement;
  if (mode === 'light') root.setAttribute('data-theme', 'light');
  else if (mode === 'dark') root.setAttribute('data-theme', 'dark');
  else root.removeAttribute('data-theme');
  const meta = document.getElementById('meta-color-scheme');
  if (meta) {
    if (mode === 'light') meta.setAttribute('content', 'light');
    else if (mode === 'dark') meta.setAttribute('content', 'dark');
    else meta.setAttribute('content', 'light dark');
  }
}

function themeAccessibilityLabel(mode) {
  const suffix = 'Click to cycle: System, Light, Dark.';
  if (mode === 'light') return `Color theme: Light. ${suffix}`;
  if (mode === 'dark') return `Color theme: Dark. ${suffix}`;
  return `Color theme: System. ${suffix}`;
}

const themeCycleBtn = document.getElementById('theme-cycle-btn');

function updateThemeCycleButton() {
  if (!themeCycleBtn) return;
  const mode = getStoredColorMode();
  themeCycleBtn.dataset.themeMode = mode;
  themeCycleBtn.setAttribute('aria-label', themeAccessibilityLabel(mode));
}

if (themeCycleBtn) {
  themeCycleBtn.addEventListener('click', () => {
    const cur = getStoredColorMode();
    const i = Math.max(0, THEME_CYCLE_ORDER.indexOf(cur));
    const next = THEME_CYCLE_ORDER[(i + 1) % THEME_CYCLE_ORDER.length];
    setStoredColorMode(next);
    applyColorModeToDocument(next);
    updateThemeCycleButton();
  });
}
updateThemeCycleButton();
