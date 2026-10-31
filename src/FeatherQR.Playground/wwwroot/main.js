import { RMQR_VERSIONS } from './symbol-options.js';
import { PRESETS } from './presets.js';
import { createStateControls, presetToState } from './state-controls.js';
import { dotnet } from './_framework/dotnet.js';
import {
  decodeShareHash,
  encodeShareState,
  isShareWithinLimits,
} from './share-payload.js';

/* ─── Theme ─── */

const THEME_STORAGE_KEY = 'skqr-playground-color-mode';
const THEME_CYCLE_ORDER = ['system', 'light', 'dark'];

function colorSchemeDarkQuery() {
  return window.matchMedia('(prefers-color-scheme: dark)');
}

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
// System-mode visuals follow prefers-color-scheme via CSS; refresh the button label on change.
colorSchemeDarkQuery().addEventListener('change', updateThemeCycleButton);

/* ─── Toasts ─── */

/** @typedef {'error'|'success'|'info'} ToastVariant */
/** @type {Record<ToastVariant, number>} */
const TOAST_DURATION_MS = { error: 8000, success: 3800, info: 4200 };
const toastStack = document.getElementById('toast-stack');

/**
 * @param {string} message
 * @param {ToastVariant} [variant]
 * @param {number} [durationMs]
 */
function showToast(message, variant = 'info', durationMs) {
  if (!toastStack) return;
  const ms = durationMs ?? TOAST_DURATION_MS[variant] ?? TOAST_DURATION_MS.info;

  const wrap = document.createElement('div');
  wrap.className = `toast toast--${variant}`;
  wrap.setAttribute('role', variant === 'error' ? 'alert' : 'status');

  const bodyEl = document.createElement('div');
  bodyEl.className = 'toast__body';
  bodyEl.textContent = message ?? '';

  const dismissBtn = document.createElement('button');
  dismissBtn.type = 'button';
  dismissBtn.className = 'toast__dismiss';
  dismissBtn.setAttribute('aria-label', 'Dismiss notification');
  dismissBtn.textContent = '✕';

  wrap.append(bodyEl, dismissBtn);
  toastStack.appendChild(wrap);
  requestAnimationFrame(() => wrap.classList.add('toast--show'));

  const hideTimer = window.setTimeout(() => removeToastElement(wrap), ms);
  dismissBtn.addEventListener('click', () => {
    window.clearTimeout(hideTimer);
    removeToastElement(wrap);
  });
}

/** @param {HTMLElement} el */
function removeToastElement(el) {
  if (!el?.parentElement || el.dataset.toastClosing) return;
  el.dataset.toastClosing = '1';
  el.classList.remove('toast--show');
  el.classList.add('toast--out');
  window.setTimeout(() => el.remove(), 240);
}

/* ─── Element references ─── */

const loading = document.getElementById('loading');
const versionEl = document.getElementById('playground-version');
const contentEl = document.getElementById('content');
const presetSelect = document.getElementById('preset-select');
const symbologySelect = document.getElementById('symbology-select');
const eccSelect = document.getElementById('ecc-select');
const eccBoostRow = document.getElementById('ecc-boost-row');
const eccBoostCheck = document.getElementById('ecc-boost-check');
const allowKanjiCheck = document.getElementById('allow-kanji-check');
const appIndicatorRow = document.getElementById('app-indicator-row');
const appIndicatorInput = document.getElementById('app-indicator-input');
const separatorRow = document.getElementById('separator-row');
const insertSeparatorBtn = document.getElementById('insert-separator-btn');
const formatRow = document.getElementById('format-row');
const formatSelect = document.getElementById('format-select');
const gs1StemRow = document.getElementById('gs1-stem-row');
const gs1StemInput = document.getElementById('gs1-stem-input');
const gs1ExampleBtn = document.getElementById('gs1-example-btn');
const gs1EncodedEl = document.getElementById('gs1-encoded');
const decodeGs1El = document.getElementById('decode-gs1');
const versionSelect = document.getElementById('version-select');
const rmqrFitRow = document.getElementById('rmqr-fit-row');
const rmqrFitSelect = document.getElementById('rmqr-fit-select');
const rmqrHeightRow = document.getElementById('rmqr-height-row');
const rmqrHeightSelect = document.getElementById('rmqr-height-select');
const sizeRange = document.getElementById('size-range');
const sizeOut = document.getElementById('size-out');
const quietRange = document.getElementById('quiet-range');
const quietOut = document.getElementById('quiet-out');
const finderRow = document.getElementById('finder-row');
const logoPanel = document.getElementById('logo-panel');
const moduleShapeSelect = document.getElementById('module-shape-select');
const moduleSizeRange = document.getElementById('module-size-range');
const moduleSizeOut = document.getElementById('module-size-out');
const cornerRow = document.getElementById('corner-row');
const cornerRange = document.getElementById('corner-range');
const cornerOut = document.getElementById('corner-out');
const finderSelect = document.getElementById('finder-select');
const fgRow = document.getElementById('fg-row');
const fgColor = document.getElementById('fg-color');
const bgColor = document.getElementById('bg-color');
const bgTransparent = document.getElementById('bg-transparent');
const gradientToggle = document.getElementById('gradient-toggle');
const gradientPanel = document.getElementById('gradient-panel');
const gradientColorsEl = document.getElementById('gradient-colors');
const gradientAddBtn = document.getElementById('gradient-add-btn');
const gradientDirection = document.getElementById('gradient-direction');
const logoModeSelect = document.getElementById('logo-mode-select');
const logoFileRow = document.getElementById('logo-file-row');
const logoFile = document.getElementById('logo-file');
const logoSizeRange = document.getElementById('logo-size-range');
const logoSizeOut = document.getElementById('logo-size-out');
const logoBorderRange = document.getElementById('logo-border-range');
const logoBorderOut = document.getElementById('logo-border-out');
const previewImg = document.getElementById('qr-preview');
const statsEl = document.getElementById('qr-stats');
const generateErrorEl = document.getElementById('generate-error');
const downloadBtn = document.getElementById('download-btn');
const downloadSvgBtn = document.getElementById('download-svg-btn');
const copyImageBtn = document.getElementById('copy-image-btn');
const permalinkBtn = document.getElementById('permalink-btn');
const decodeFileEl = document.getElementById('decode-file');
const decodePanelEl = document.getElementById('decode-panel');
const decodeDropEl = document.getElementById('decode-drop');
const decodeCornersCheck = document.getElementById('decode-corners-check');
const decodeEveryCheck = document.getElementById('decode-every-check');
const decodeMixedCheck = document.getElementById('decode-mixed-check');
const decodeResultEl = document.getElementById('decode-result');
const decodeSymbolsEl = document.getElementById('decode-symbols');
const decodePreviewEl = document.getElementById('decode-preview');
const decodeCanvasEl = document.getElementById('decode-canvas');
const decodePreviewCaptionEl = document.getElementById('decode-preview-caption');
/** Where a preview of a read is drawn: the decode panel's, or the photograph panel's. */
const decodeView = { canvas: decodeCanvasEl, figure: decodePreviewEl, caption: decodePreviewCaptionEl, corners: decodeCornersCheck };
const decodeVerifyEl = document.getElementById('decode-verify');
const benchModeSelect = document.getElementById('bench-mode-select');
const benchCountSelect = document.getElementById('bench-count-select');
const benchRunBtn = document.getElementById('bench-run-btn');
const benchCancelBtn = document.getElementById('bench-cancel-btn');
const benchProgress = document.getElementById('bench-progress');
const benchResult = document.getElementById('bench-result');

/* ─── Symbology-dependent selects ─── */

function isMicroSymbology() {
  return symbologySelect.value === 'microqr';
}

function isRmqrSymbology() {
  return symbologySelect.value === 'rmqr';
}

/** Symbology name shown in stats / decode results. */
function symbolLabel(symbology, qrVersion) {
  if (symbology === 'microqr') return `Micro QR M${qrVersion}`;
  if (symbology === 'rmqr') {
    const v = RMQR_VERSIONS[qrVersion - 1];
    return v ? `rMQR R${v[0]}x${v[1]}` : `rMQR version ${qrVersion}`;
  }
  return `QR version ${qrVersion}`;
}

/**
 * The FNC1 mode of a decoded symbol, or '' without one: GS1 data in first position, or an AIM
 * industry format in second position with its application indicator as the standard transmits
 * it (two digits for 0-99, one letter for a letter's code plus 100).
 */
function fnc1Label(result) {
  if (result.fnc1 === 'first') return ' · GS1 (FNC1 first position)';
  if (result.fnc1 !== 'second') return '';
  const n = result.applicationIndicator;
  const indicator = n < 100 ? String(n).padStart(2, '0') : String.fromCharCode(n - 100);
  return ` · FNC1 second position, application indicator ${indicator}`;
}

/** Decoded text for display: the GS1 separator (U+001D) is invisible, so it is shown as ␝ (U+241D). */
function displayText(text) {
  return text.replace(/\u001d/g, '␝');
}

/**
 * The WASM request for a playground state. The data format names the route: a GS1 format goes through
 * the GS1 entry points, which check the data and set the mode, and an AIM format is plain text under
 * FNC1 in second position. First position is written through the GS1 entry point alone, so a symbol
 * that claims GS1 data has passed the checks. The page shows the separator as ␝, so in every format but
 * plain text each ␝ is sent as U+001D, the separator a reader returns. Micro QR has neither FNC1 nor a
 * GS1 carrier, so its request is plain text whatever the hidden control holds.
 */
function requestFor(state) {
  const format = state.symbology === 'microqr' ? 'text' : state.dataFormat;
  const gs1 = format === 'gs1' ? 'qr' : format === 'link' ? 'link' : 'none';
  const fnc1 = format === 'aim' ? 'second' : 'none';
  const content = format === 'text' ? state.content : state.content.replace(/␝/g, '\u001d');
  return { ...state, gs1, fnc1, content };
}

/**
 * What the self-check expects a generated symbol to decode to: for GS1 data, the text the generator
 * wrote (the element string under FNC1 in first position, or the Digital Link URI with no mode), and
 * otherwise the content under the request's own mode.
 */
function expectedDecode(request, encoded) {
  if (request.gs1 === 'none' || !encoded) return request;
  return { ...request, content: encoded, fnc1: request.gs1 === 'qr' ? 'first' : 'none' };
}

/**
 * The GS1 view of a decoded symbol: its data as (AI) text with a row for each element, or, for a symbol
 * under FNC1 in first position whose data fails the checks, why. Hidden for a symbol with no GS1 data.
 */
function renderDecodedGs1(result) {
  decodeGs1El.replaceChildren();
  decodeGs1El.hidden = !fillGs1View(decodeGs1El, result);
}

/** Writes a symbol's GS1 view into `container`. Returns false, with nothing written, for a symbol with no GS1 data. */
function fillGs1View(container, result) {
  if (!(result?.gs1Hri || result?.gs1Error)) return false;
  const heading = document.createElement('div');
  if (result.gs1Error) {
    heading.textContent = `⚠ FNC1 in first position, but the text does not read as GS1 data: ${result.gs1Error}`;
    container.appendChild(heading);
    return true;
  }
  const carrier = result.fnc1 === 'first' ? 'GS1 element string' : 'GS1 Digital Link URI';
  heading.textContent = `${carrier}, as (AI) text: ${result.gs1Hri}`;
  container.appendChild(heading);
  // Reading takes data the checks refuse, such as a wrong check digit, so the verdict is shown apart.
  const check = document.createElement('div');
  check.textContent = result.gs1Check
    ? `⚠ The element string fails the GS1 checks: ${result.gs1Check}`
    : '✓ Passes the GS1 checks.';
  container.appendChild(check);
  if (!result.gs1Elements?.length) return true;
  const table = document.createElement('table');
  const head = table.createTHead().insertRow();
  for (const label of ['AI', 'Value']) {
    const th = document.createElement('th');
    th.textContent = label;
    head.appendChild(th);
  }
  const body = table.createTBody();
  for (const { ai, value } of result.gs1Elements) {
    const row = body.insertRow();
    row.insertCell().textContent = `(${ai})`;
    row.insertCell().textContent = value;
  }
  container.appendChild(table);
  return true;
}

/**
 * Whether a decoded symbol carries the mode a request asked for: the position, and in second
 * position the indicator as the decoder reports it (0-99 as its value, a letter as its code plus 100).
 */
function decodedModeMatches(result, request) {
  if (request.fnc1 === 'none') return !result.fnc1;
  if (request.fnc1 === 'first') return result.fnc1 === 'first';
  const indicator = request.applicationIndicator.trim();
  const expected = /^\d{1,2}$/.test(indicator) ? Number(indicator) : indicator.charCodeAt(0) + 100;
  return result.fnc1 === 'second' && result.applicationIndicator === expected;
}

/* ─── State <-> DOM ─── */

const EMPTY_BYTES = new Uint8Array(0);
/** Uploaded logo image bytes (not part of share links). */
let customLogoBytes = EMPTY_BYTES;
const stateControls = createStateControls(document, {
  getGradientColors, renderGradientColors,
});
const { collectState, applyStateToControls, syncDerivedControls } = stateControls;

/* ─── Gradient color list ─── */

const GRADIENT_MIN_COLORS = 2;
const GRADIENT_MAX_COLORS = 6;

function getGradientColors() {
  return [...gradientColorsEl.querySelectorAll('input[type="color"]')].map((i) => i.value);
}

/** @param {string[]} colors */
function renderGradientColors(colors) {
  gradientColorsEl.replaceChildren();
  for (const color of colors.slice(0, GRADIENT_MAX_COLORS)) {
    gradientColorsEl.appendChild(createGradientColorRow(color));
  }
  syncGradientButtons();
}

/** @param {string} color */
function createGradientColorRow(color) {
  const row = document.createElement('div');
  row.className = 'gradient-color-row';

  const input = document.createElement('input');
  input.type = 'color';
  input.value = normalizeHexColor(color);

  const remove = document.createElement('button');
  remove.type = 'button';
  remove.className = 'small-btn gradient-color-row__remove';
  remove.textContent = '✕';
  remove.setAttribute('aria-label', 'Remove this gradient color');
  remove.addEventListener('click', () => {
    if (gradientColorsEl.children.length <= GRADIENT_MIN_COLORS) return;
    row.remove();
    syncGradientButtons();
    markCustomPreset();
    scheduleGenerate();
  });

  row.append(input, remove);
  return row;
}

function syncGradientButtons() {
  const count = gradientColorsEl.children.length;
  gradientAddBtn.disabled = count >= GRADIENT_MAX_COLORS;
  for (const btn of gradientColorsEl.querySelectorAll('.gradient-color-row__remove')) {
    btn.disabled = count <= GRADIENT_MIN_COLORS;
  }
}

/** <input type=color> requires #rrggbb; anything unparsable falls back to black. */
function normalizeHexColor(value) {
  return typeof value === 'string' && /^#[0-9a-fA-F]{6}$/.test(value) ? value.toLowerCase() : '#000000';
}

gradientAddBtn.addEventListener('click', () => {
  if (gradientColorsEl.children.length >= GRADIENT_MAX_COLORS) return;
  const colors = getGradientColors();
  gradientColorsEl.appendChild(createGradientColorRow(colors[colors.length - 1] ?? '#833ab4'));
  syncGradientButtons();
  markCustomPreset();
  scheduleGenerate();
});

/* ─── WASM runtime ─── */

let exports = null;
let runtimeReady = false;
let runtimeAlive = true;

const RELEASE_TAG_BASE_URL = 'https://github.com/guitarrapc/FeatherQR/releases/tag/';

function isRuntimeDeadError(err) {
  if (!err) return false;
  const msg = String(err?.message ?? err).toLowerCase();
  return msg.includes('.net runtime already exited')
    || msg.includes('runtime already exited')
    || msg.includes('runtime has already exited');
}

function handleRuntimeDeath() {
  runtimeAlive = false;
  runtimeReady = false;
  // Stop an in-flight benchmark loop or limit search at its next batch boundary.
  benchCancelled = true;
  limitCancelled = true;
  // Everything below either needs the runtime or would act on stale output.
  downloadBtn.disabled = true;
  downloadSvgBtn.disabled = true;
  copyImageBtn.disabled = true;
  permalinkBtn.disabled = true;
  benchRunBtn.disabled = true;
  limitRunBtn.disabled = true;
  showToast('The WebAssembly runtime has crashed. Please reload the page to continue.', 'error', 60000);
  generateErrorEl.hidden = false;
  generateErrorEl.textContent = 'Runtime crashed, please reload the page.';
}

function syncVersionBadge() {
  if (!exports || !versionEl) return;
  try {
    const v = exports.FeatherQR.Playground.QrInterop.GetProductVersion();
    if (typeof v === 'string' && v.length > 0 && v !== 'unknown') {
      versionEl.textContent = `v${v}`;
      versionEl.href = RELEASE_TAG_BASE_URL + encodeURIComponent(v);
      versionEl.setAttribute('aria-label', `Release ${v}, open on GitHub`);
      versionEl.hidden = false;
    }
  } catch {
    /* ignore, older bundles or trimmed exports */
  }
}

/* ─── Generation ─── */

const utf8Decoder = new TextDecoder();
/** Most recent successful PNG, backing Download / Copy image. */
let lastPngBytes = null;
let lastObjectUrl = null;
let generateTimer = null;

// setTimeout (not requestAnimationFrame): rAF does not fire in hidden/background tabs,
// which would silently stall regeneration. 60ms coalesces slider drags nicely.
const GENERATE_DEBOUNCE_MS = 60;

function scheduleGenerate() {
  if (generateTimer !== null) return;
  generateTimer = window.setTimeout(() => {
    generateTimer = null;
    generate();
  }, GENERATE_DEBOUNCE_MS);
}

function generate() {
  if (!runtimeAlive || !runtimeReady || !exports) return;

  const state = collectState();
  if (!state.content.trim()) {
    showGenerateError('Enter content to encode.');
    return;
  }

  const request = requestFor(state);
  const logoBytes = state.logo.mode === 'custom' ? customLogoBytes : EMPTY_BYTES;
  let bytes;
  const t0 = performance.now();
  try {
    bytes = exports.FeatherQR.Playground.QrInterop.Generate(JSON.stringify(request), logoBytes);
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    showGenerateError(err?.message ?? String(err));
    return;
  }
  const interopMs = performance.now() - t0;

  // Error envelope is UTF-8 JSON starting with '{'; PNG always starts with 0x89.
  if (bytes.length === 0 || bytes[0] === 0x7b) {
    let message = 'QR generation failed.';
    try {
      const envelope = JSON.parse(utf8Decoder.decode(bytes));
      message = envelope.error ?? message;
      // Data the GS1 checks refuse names its place in the content, so the characters can be quoted.
      if (Number.isInteger(envelope.offset)) {
        const fault = request.content.slice(envelope.offset, envelope.offset + envelope.length);
        if (fault) message += ` Characters at fault: “${displayText(fault)}”.`;
      }
    } catch {
      /* keep generic message */
    }
    showGenerateError(message);
    return;
  }

  lastPngBytes = bytes;
  const blob = new Blob([bytes], { type: 'image/png' });
  const url = URL.createObjectURL(blob);
  if (lastObjectUrl) URL.revokeObjectURL(lastObjectUrl);
  lastObjectUrl = url;
  previewImg.src = url;
  previewImg.hidden = false;
  generateErrorEl.hidden = true;
  downloadBtn.disabled = false;
  downloadSvgBtn.disabled = false;
  copyImageBtn.disabled = false;

  let encoded = null;
  photoSource = null;
  try {
    const meta = JSON.parse(exports.FeatherQR.Playground.QrInterop.GetLastMeta());
    photoSource = {
      symbology: meta.symbology,
      matrixWidth: meta.matrixSize,
      matrixHeight: meta.matrixHeight ?? meta.matrixSize,
      quietZone: Math.min(Math.max(request.quietZone, 0), 10),
      expected: expectedDecode(request, meta.encoded ?? null),
    };
    const versionLabel = symbolLabel(meta.symbology, meta.qrVersion);
    const matrixLabel = `${meta.matrixSize}×${meta.matrixHeight ?? meta.matrixSize}`;
    statsEl.textContent =
      `${versionLabel} · ${matrixLabel} modules · `
      + `${meta.totalMs} ms in WASM (${interopMs.toFixed(1)} ms total) · ${formatBytes(meta.bytes)}`;
    encoded = meta.encoded ?? null;
  } catch {
    statsEl.textContent = '';
  }
  gs1EncodedEl.hidden = encoded === null;
  gs1EncodedEl.textContent = encoded === null
    ? ''
    : `${request.gs1 === 'link' ? 'Digital Link URI' : 'Element string, under FNC1 in first position'}: ${displayText(encoded)}`;

  scheduleVerifyDecode(bytes, expectedDecode(request, encoded));
  photoCardStale = true;
  if (photoPanelEl.open) schedulePhotograph();
}

/* ─── Decode (self-verify + image upload) ─── */

// Self-verify runs the library's own decoder over the freshly generated PNG.
// Debounced independently of generation so slider drags do not pay the decode cost.
const VERIFY_DEBOUNCE_MS = 250;
let verifyTimer = null;
let pendingVerify = null;

/** @param {object} request what the image should decode to: the content and the FNC1 mode (see expectedDecode) */
function scheduleVerifyDecode(pngBytes, request) {
  // Clear immediately so the line never describes a previous image
  if (decodeVerifyEl) decodeVerifyEl.textContent = '';
  pendingVerify = { pngBytes, request };
  if (verifyTimer !== null) return;
  verifyTimer = window.setTimeout(() => {
    verifyTimer = null;
    const job = pendingVerify;
    pendingVerify = null;
    if (job) runVerifyDecode(job.pngBytes, job.request);
  }, VERIFY_DEBOUNCE_MS);
}

function runVerifyDecode(pngBytes, request) {
  if (!runtimeAlive || !runtimeReady || !exports || !decodeVerifyEl) return;
  const result = callDecode(pngBytes);
  if (!result || result.error) {
    decodeVerifyEl.textContent = '';
    return;
  }
  // The mode changes how the text reads, so a symbol is only right when both come back.
  if (result.ok && result.text === request.content && decodedModeMatches(result, request)) {
    const corrected = result.errorsCorrected > 0 ? ` · ${result.errorsCorrected} codewords corrected` : '';
    // The decoded ECC is shown rather than the requested one: with "Boost ECC"
    // enabled the two can differ, and this line is where the boost is observable.
    const ecc = result.ecc ? ` · ECC ${result.ecc}` : '';
    decodeVerifyEl.textContent = `✓ Decodes back to the input (self-check${ecc}${fnc1Label(result)}${corrected} · ${result.totalMs} ms)`;
  } else if (result.ok && result.text === request.content) {
    const mode = fnc1Label(result).replace(/^ · /, '') || 'no FNC1';
    decodeVerifyEl.textContent = `⚠ Self-check read the text under another mode (${mode}), please test-scan this code.`;
  } else if (result.ok) {
    decodeVerifyEl.textContent = '⚠ Self-check decoded different text, please test-scan this code.';
  } else {
    decodeVerifyEl.textContent =
      `Self-check: ${result.status}, the built-in decoder could not read this styling; a camera app may still scan it.`;
  }
}

/**
 * Calls the WASM decoder for one symbol. With `every` it reads each QR Code of the image, and with
 * `mixed` too, those of every symbology together. Returns the parsed result, or null when the runtime died.
 */
function callDecode(bytes, every = false, mixed = false) {
  try {
    const interop = exports.FeatherQR.Playground.QrInterop;
    return JSON.parse(every ? interop.DecodeAll(bytes, mixed) : interop.Decode(bytes));
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return null;
    }
    return { error: err?.message ?? String(err) };
  }
}

/** Box the preview canvas is fitted into, in CSS pixels. Small symbols are scaled up into it. */
const DECODE_PREVIEW_MAX_WIDTH = 460;
const DECODE_PREVIEW_MAX_HEIGHT = 460;

/** The image and result the preview is showing, so the corners toggle can redraw without decoding again. */
let lastDecodeImage = null;
let lastDecodeResult = null;

/** The file the panel last read, so ticking "Read every QR Code" reads it again under the new choice. */
let lastDecodeFile = null;

/**
 * One shape for both calls: the symbols read (none, one, or with "Read every QR Code" any number),
 * and the image they were read from.
 */
function decodedSymbols(result) {
  if (Array.isArray(result.symbols)) return result;
  return { ...result, symbols: result.ok ? [result] : [] };
}

/** A decoded symbol on one line: its text, symbology, mode, level, mask and corrections. */
function symbolSummary(symbol) {
  const decodedLabel = symbolLabel(symbol.symbology, symbol.qrVersion);
  // rMQR has one fixed mask, so no mask pattern is reported for it.
  const maskLabel = symbol.maskPattern >= 0 ? ` · mask ${symbol.maskPattern}` : '';
  return `“${displayText(symbol.text)}” · ${decodedLabel}${fnc1Label(symbol)} · ECC ${symbol.ecc}${maskLabel}`
    + ` · ${symbol.errorsCorrected} codewords corrected`;
}

/** Which decode is current. Without it a small image pasted second finishes first and wins the result line while the big one wins the canvas. */
let decodeGeneration = 0;

/**
 * Maps corners (TL/TR/BR/BL, x then y) from the file's stored pixels onto the picture the browser
 * shows. No `imageOrientation` value suppresses EXIF, so the page follows the tag instead.
 */
function orientCorners(corners, orientation, width, height) {
  // One line per EXIF tag, so it reads against the spec table.
  const map = {
    1: (x, y) => [x, y],
    2: (x, y) => [width - x, y],
    3: (x, y) => [width - x, height - y],
    4: (x, y) => [x, height - y],
    5: (x, y) => [y, x],
    6: (x, y) => [height - y, x],
    7: (x, y) => [height - y, width - x],
    8: (x, y) => [y, width - x],
  }[orientation];
  if (!map) return corners.slice();

  const out = new Array(8);
  for (let i = 0; i < 8; i += 2) {
    const [x, y] = map(corners[i], corners[i + 1]);
    out[i] = x;
    out[i + 1] = y;
  }
  return out;
}

/** Displayed size of an image with `orientation` applied; tags 5-8 transpose it. */
function orientedSize(orientation, width, height) {
  return orientation >= 5 && orientation <= 8 ? [height, width] : [width, height];
}

/**
 * Draws the decoded image with the reported outline over it. Sized in CSS pixels and scaled by the
 * device pixel ratio, because letting CSS resize the canvas leaves the outline soft. A size that
 * still disagrees after applying the EXIF tag means the overlay would land wrong, so it is dropped.
 */
/** Leaves the canvas taking no space, so a caption is not shown beside a stale picture. */
function collapseDecodeCanvas(view = decodeView) {
  view.canvas.width = 0;
  view.canvas.height = 0;
  view.canvas.style.width = '';
  view.canvas.style.height = '';
}

function drawDecodePreview(bitmap, result, view = decodeView) {
  const scale = Math.min(
    DECODE_PREVIEW_MAX_WIDTH / bitmap.width,
    DECODE_PREVIEW_MAX_HEIGHT / bitmap.height);
  // A very wide or tall image would otherwise round to a zero-sized canvas.
  const cssWidth = Math.max(1, Math.round(bitmap.width * scale));
  const cssHeight = Math.max(1, Math.round(bitmap.height * scale));
  const dpr = window.devicePixelRatio || 1;

  // Null where the browser blocks canvas; the rest of the page does not need one, so say so
  // rather than throwing out of an event handler.
  const ctx = view.canvas.getContext('2d');
  if (!ctx) {
    collapseDecodeCanvas(view);
    view.figure.hidden = false;
    view.caption.textContent = 'This browser did not give the page a 2D canvas, so the image cannot be previewed.';
    return;
  }

  view.canvas.width = Math.round(cssWidth * dpr);
  view.canvas.height = Math.round(cssHeight * dpr);
  view.canvas.style.width = `${cssWidth}px`;
  view.canvas.style.height = `${cssHeight}px`;

  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssWidth, cssHeight);
  // Upscaling a symbol: keep the module edges hard. Downscaling a photo: let it average.
  ctx.imageSmoothingEnabled = cssWidth * dpr < bitmap.width;
  ctx.drawImage(bitmap, 0, 0, cssWidth, cssHeight);

  view.figure.hidden = false;

  const orientation = result.orientation || 1;
  const [shownWidth, shownHeight] = orientedSize(orientation, result.imageWidth, result.imageHeight);
  const aligned = shownWidth === bitmap.width && shownHeight === bitmap.height;
  const { symbols } = result;
  // Numbered as the list numbers them, so a symbol with no corners keeps the numbers of the rest in step.
  const outlines = symbols
    .map((symbol, index) => ({ symbol, number: index + 1 }))
    .filter(({ symbol }) => symbol.corners?.length === 8);
  if (outlines.length === 0 || !aligned) {
    view.caption.textContent = !aligned
      ? 'The browser decoded this image at a different size than the library did, so the outline is not drawn.'
      : symbols.length > 0
        ? 'The decoder reported no corner geometry for this symbol, so there is no outline to draw.'
        : 'No symbol was located, so there is no outline to draw.';
    return;
  }

  for (const outline of outlines) {
    // Corners arrive in stored pixels, and the canvas shows the oriented picture.
    const corners = orientCorners(outline.symbol.corners, orientation, result.imageWidth, result.imageHeight);
    // y grows downward, so a printed symbol winds clockwise and a mirrored one reverses it.
    // Computed after orienting, so it describes the picture on screen.
    const cross = (corners[2] - corners[0]) * (corners[7] - corners[1])
      - (corners[3] - corners[1]) * (corners[6] - corners[0]);
    outline.corners = corners;
    outline.mirrored = cross < 0;
  }

  const several = symbols.length > 1;
  const mirrored = outlines.filter((o) => o.mirrored).length;
  const winding = mirrored > 0
    ? 'counter-clockwise, so this capture is mirrored'
    : 'clockwise, so this capture is not mirrored';
  const severalWinding = mirrored === 0 || mirrored === outlines.length
    ? `Every outline winds ${winding}.`
    : `${mirrored} of ${outlines.length} outlines wind counter-clockwise, so those codes are mirrored.`;

  if (!view.corners.checked) {
    view.caption.textContent = several ? severalWinding : `Corner winding is ${winding}.`;
    return;
  }

  // Everything below is in CSS pixels, so the stroke is a constant on-screen weight rather than
  // something the source image's resolution decides.
  let unnumbered = 0;
  for (const { corners, number } of outlines) {
    const at = (i) => [corners[i] * scale, corners[i + 1] * scale];
    ctx.lineWidth = 2;
    ctx.strokeStyle = '#ff3b30';
    ctx.lineJoin = 'round';
    ctx.beginPath();
    ctx.moveTo(...at(0));
    for (let i = 2; i < 8; i += 2) ctx.lineTo(...at(i));
    ctx.closePath();
    ctx.stroke();

    // A photograph of many codes shows each one small, so the marks are sized to the outline
    // rather than covering it.
    const side = shortestSide(at);

    // TopLeft is the corner beside the finder that defines the symbol's own top-left, so marking it
    // is what shows a rotation or a mirror at a glance.
    ctx.fillStyle = '#007aff';
    ctx.beginPath();
    ctx.arc(...at(0), Math.min(5, Math.max(2, side * 0.15)), 0, Math.PI * 2);
    ctx.fill();

    if (several && !drawOutlineNumber(ctx, number, at, side)) unnumbered++;
  }

  const numbering = unnumbered === 0
    ? 'numbered as listed'
    : unnumbered === outlines.length
      ? 'in the order listed, too small in this preview to number'
      : 'numbered as listed where the preview has room';
  view.caption.textContent = several
    ? `Outlines are the reported Corners, ${numbering}; each dot is a TopLeft. ${severalWinding}`
    : `Outline is the reported Corners; the dot is TopLeft. Winding is ${winding}.`;
}

/** The shortest of an outline's four sides, in the canvas's CSS pixels. */
function shortestSide(at) {
  let shortest = Infinity;
  for (let i = 0; i < 8; i += 2) {
    const [x0, y0] = at(i);
    const [x1, y1] = at((i + 2) % 8);
    shortest = Math.min(shortest, Math.hypot(x1 - x0, y1 - y0));
  }
  return shortest;
}

/** The list number at the outline's centre, sized to fit inside it. Returns false when the outline is too small to hold it legibly. */
function drawOutlineNumber(ctx, number, at, side) {
  const size = Math.min(13, Math.floor(side * 0.45));
  if (size < 8) return false;
  let x = 0;
  let y = 0;
  for (let i = 0; i < 8; i += 2) {
    const [px, py] = at(i);
    x += px / 4;
    y += py / 4;
  }
  const label = String(number);
  ctx.font = `700 ${size}px system-ui, sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  // A light halo keeps the number readable on dark modules without hiding the symbol under a badge.
  ctx.lineWidth = 3;
  ctx.lineJoin = 'round';
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.9)';
  ctx.strokeText(label, x, y);
  ctx.fillStyle = '#d70015';
  ctx.fillText(label, x, y);
  return true;
}

/**
 * Resolves once what the page just wrote has been painted, before a call that blocks the main thread.
 * One rAF resolves before the frame it schedules paints, so two are nested. The timer covers a hidden tab, where rAF never fires.
 */
function afterPaint() {
  return Promise.race([
    new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))),
    new Promise((resolve) => setTimeout(resolve, 50)),
  ]);
}

/** Runs one image through the decoder and shows the result; shared by the file input, paste and drop. */
async function decodeImageFile(file) {
  const generation = ++decodeGeneration;
  /** True once a newer decode started; this one must then touch nothing shared. */
  const superseded = () => generation !== decodeGeneration;

  decodePreviewEl.hidden = true;
  renderDecodedGs1(null);
  decodeSymbolsEl.replaceChildren();
  decodeSymbolsEl.hidden = true;
  lastDecodeImage?.close();
  lastDecodeImage = null;
  lastDecodeResult = null;
  lastDecodeFile = file;

  let bytes;
  try {
    bytes = new Uint8Array(await file.arrayBuffer());
  } catch (e) {
    if (superseded()) return;
    decodeResultEl.textContent = `Could not read the file: ${e?.message ?? e}`;
    return;
  }
  if (superseded()) return;

  if (!runtimeAlive || !runtimeReady || !exports) {
    decodeResultEl.textContent = 'The WebAssembly runtime is still loading, try again in a moment.';
    return;
  }

  // The decode blocks the main thread for seconds, so the status line is painted first.
  decodeResultEl.textContent = 'Decoding…';
  await afterPaint();
  if (superseded()) return;

  const every = decodeEveryCheck.checked;
  const raw = callDecode(bytes, every, every && decodeMixedCheck.checked);
  if (!raw || superseded()) return;
  if (raw.error) {
    decodeResultEl.textContent = `Decode failed: ${raw.error}`;
    return;
  }

  const result = decodedSymbols(raw);
  const { symbols } = result;
  if (symbols.length === 0) {
    // The call for every code reports no status: it returns only the symbols it read.
    const status = result.status ? ` (${result.status})` : '';
    decodeResultEl.textContent =
      `No QR code decoded${status}. The built-in decoder targets clean, screen-rendered images.`;
  } else if (symbols.length === 1) {
    decodeResultEl.textContent = `${symbolSummary(symbols[0])} · ${result.totalMs} ms`;
    renderDecodedGs1(symbols[0]);
  } else {
    decodeResultEl.textContent = `${symbols.length} QR codes decoded · ${result.totalMs} ms`;
    for (const symbol of symbols) {
      const item = document.createElement('li');
      item.textContent = symbolSummary(symbol);
      const gs1 = document.createElement('div');
      gs1.className = 'decode-gs1';
      if (fillGs1View(gs1, symbol)) item.appendChild(gs1);
      decodeSymbolsEl.appendChild(item);
    }
    decodeSymbolsEl.hidden = false;
  }

  let bitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    if (superseded()) return;
    // Collapse it first: it still holds the previous image and its outline.
    collapseDecodeCanvas();
    decodePreviewEl.hidden = false;
    decodePreviewCaptionEl.textContent = 'The browser could not render this image for preview.';
    return;
  }
  // A newer decode owns the panel; close this one's bitmap rather than leaking it.
  if (superseded()) {
    bitmap.close();
    return;
  }

  lastDecodeImage = bitmap;
  lastDecodeResult = result;
  drawDecodePreview(bitmap, result);
}

decodeFileEl.addEventListener('change', async () => {
  const file = decodeFileEl.files?.[0];
  if (file) await decodeImageFile(file);
});

decodeCornersCheck.addEventListener('change', () => {
  if (lastDecodeImage && lastDecodeResult) drawDecodePreview(lastDecodeImage, lastDecodeResult);
});

decodeEveryCheck.addEventListener('change', async () => {
  // Reading every symbology together is a way of reading every code, so it waits on that option
  decodeMixedCheck.disabled = !decodeEveryCheck.checked;
  if (lastDecodeFile) await decodeImageFile(lastDecodeFile);
});

decodeMixedCheck.addEventListener('change', async () => {
  if (lastDecodeFile) await decodeImageFile(lastDecodeFile);
});

/** Opens the decode panel so a pasted or dropped image is not decoded out of sight. */
function revealDecodePanel() {
  decodePanelEl.open = true;
  decodePanelEl.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

// Page-wide, so "Copy image" round-trips without a file on disk. Excel and Word put a bitmap on
// the clipboard alongside the text, so an image item alone is not enough to claim the paste.
document.addEventListener('paste', async (event) => {
  const item = [...(event.clipboardData?.items ?? [])].find((i) => i.type.startsWith('image/'));
  const file = item?.getAsFile();
  if (!file) return;

  // Only a target that takes typed text keeps its paste; a checkbox or range does not.
  const target = event.target instanceof Element ? event.target : null;
  const typedInto = target?.isContentEditable
    || target?.closest('textarea') != null
    || (target instanceof HTMLInputElement
      && !/^(button|checkbox|color|file|image|radio|range|reset|submit)$/.test(target.type));
  if (typedInto) return;

  event.preventDefault();
  revealDecodePanel();
  decodeFileEl.value = '';
  await decodeImageFile(file);
});

// Drop is scoped to this panel, not the page: the logo field takes dropped images too, and a
// page-wide target would swallow those.
for (const type of ['dragenter', 'dragover']) {
  decodeDropEl.addEventListener(type, (event) => {
    if (![...(event.dataTransfer?.types ?? [])].includes('Files')) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    decodeDropEl.classList.add('is-dragover');
  });
}

for (const type of ['dragleave', 'dragend']) {
  decodeDropEl.addEventListener(type, (event) => {
    // dragleave also fires when the pointer crosses into a child, which is still inside the zone.
    if (type === 'dragleave' && decodeDropEl.contains(event.relatedTarget)) return;
    decodeDropEl.classList.remove('is-dragover');
  });
}

decodeDropEl.addEventListener('drop', async (event) => {
  const file = [...(event.dataTransfer?.files ?? [])].find((f) => f.type.startsWith('image/'));
  event.preventDefault();
  decodeDropEl.classList.remove('is-dragover');
  if (!file) {
    decodeResultEl.textContent = 'That was not an image file.';
    return;
  }
  decodeFileEl.value = '';
  await decodeImageFile(file);
});

/* ─── Photograph the code ─── */

const photoPanelEl = document.getElementById('photo-panel');
const photoResultEl = document.getElementById('photo-result');
const photoSeedInput = document.getElementById('photo-seed');
const photoLightSelect = document.getElementById('photo-light');
const photoJpegSelect = document.getElementById('photo-jpeg');
const photoSceneCheck = document.getElementById('photo-scene');
const photoView = {
  canvas: document.getElementById('photo-canvas'),
  figure: document.getElementById('photo-preview'),
  caption: document.getElementById('photo-preview-caption'),
  corners: document.getElementById('photo-corners-check'),
};

/** The sliders: element id, the photograph request's field, and the default. */
const PHOTO_SLIDERS = [
  ['photo-ppm', 'pixelsPerModule', 4],
  ['photo-tilt', 'tilt', 0],
  ['photo-tilt-axis', 'tiltAxis', 0],
  ['photo-roll', 'roll', 0],
  ['photo-barrel', 'barrel', 0],
  ['photo-bow', 'bow', 0],
  ['photo-blur', 'blur', 0],
  ['photo-noise', 'noise', 0],
  ['photo-light-depth', 'lightDepth', 50],
  ['photo-light-direction', 'lightDirection', 0],
].map(([id, key, value]) => ({ input: document.getElementById(id), output: document.getElementById(`${id}-out`), key, value }));

/** What the last generated code is, for the card: its symbology, matrix and quiet zone, and the text it must read as. */
let photoSource = null;
/** Whether the WASM side's card is older than the last generated code. */
let photoCardStale = true;
let photoTimer = null;
/** Which photograph is current, so a slow one finishing late does not overwrite a newer one. */
let photoGeneration = 0;
let lastPhotoBitmap = null;
let lastPhotoResult = null;
const PHOTO_DEBOUNCE_MS = 150;

/** The photograph request the controls describe (see PhotoRequest in PhotoSimulation.cs). */
function photoRequest() {
  const request = {
    light: photoLightSelect.value,
    jpeg: Number(photoJpegSelect.value),
    scene: photoSceneCheck.checked,
    seed: Math.max(0, Math.trunc(Number(photoSeedInput.value) || 0)),
  };
  for (const slider of PHOTO_SLIDERS) request[slider.key] = Number(slider.input.value);
  return request;
}

/** Whether a read is the text the generated code holds, under the mode it was written with. */
function readsAsGenerated(read, expected) {
  return Boolean(read?.ok) && read.text === expected.content && decodedModeMatches(read, expected);
}

/** Hands the last generated image to the WASM side as the card. Returns false, with the reason shown, when it cannot. */
function ensurePhotoCard(statusEl) {
  if (!photoCardStale) return true;
  if (!lastPngBytes || !photoSource) {
    statusEl.textContent = 'Generate a code first.';
    return false;
  }
  const { symbology, matrixWidth, matrixHeight, quietZone } = photoSource;
  const error = exports.FeatherQR.Playground.QrInterop.SetPhotoCard(lastPngBytes, symbology, matrixWidth, matrixHeight, quietZone);
  if (error) {
    statusEl.textContent = JSON.parse(error).error;
    return false;
  }
  photoCardStale = false;
  return true;
}

function schedulePhotograph() {
  if (photoTimer !== null) window.clearTimeout(photoTimer);
  photoTimer = window.setTimeout(() => {
    photoTimer = null;
    takePhotograph();
  }, PHOTO_DEBOUNCE_MS);
}

async function takePhotograph() {
  if (!runtimeAlive || !runtimeReady || !exports || limitRunning) return;
  const generation = ++photoGeneration;
  // The camera blocks the main thread, so the status line is painted first.
  photoResultEl.textContent = 'Photographing…';
  await afterPaint();
  if (generation !== photoGeneration || !ensurePhotoCard(photoResultEl)) return;

  const request = photoRequest();
  let bytes;
  let payload;
  try {
    const interop = exports.FeatherQR.Playground.QrInterop;
    bytes = interop.Photograph(JSON.stringify(request));
    if (bytes.length === 0 || bytes[0] === 0x7b) {
      photoResultEl.textContent = `Could not photograph the code: ${JSON.parse(utf8Decoder.decode(bytes)).error}`;
      return;
    }
    payload = JSON.parse(interop.GetLastPhoto());
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    photoResultEl.textContent = `Could not photograph the code: ${err?.message ?? err}`;
    return;
  }
  if (generation !== photoGeneration) return;

  const { read } = payload;
  const verdict = readsAsGenerated(read, photoSource.expected)
    ? `✓ Reads · ${symbolSummary(read)}`
    : read.ok
      ? `⚠ Reads another text · ${symbolSummary(read)}`
      : `✗ Does not read (${read.status})`;
  const capped = payload.pixelsPerModule < request.pixelsPerModule ? ' (capped)' : '';
  photoResultEl.textContent = `${verdict} · photograph ${payload.width}×${payload.height} px at `
    + `${payload.pixelsPerModule} px/module${capped} · camera ${payload.renderMs} ms, decoder ${payload.readMs} ms`;

  let bitmap;
  try {
    bitmap = await createImageBitmap(new Blob([bytes], { type: request.jpeg > 0 ? 'image/jpeg' : 'image/png' }));
  } catch {
    if (generation !== photoGeneration) return;
    collapseDecodeCanvas(photoView);
    photoView.figure.hidden = false;
    photoView.caption.textContent = 'The browser could not render the photograph.';
    return;
  }
  if (generation !== photoGeneration) {
    bitmap.close();
    return;
  }
  lastPhotoBitmap?.close();
  lastPhotoBitmap = bitmap;
  lastPhotoResult = decodedSymbols(read);
  drawDecodePreview(bitmap, lastPhotoResult, photoView);
}

function syncPhotoOutputs() {
  for (const slider of PHOTO_SLIDERS) {
    if (slider.output) slider.output.textContent = slider.input.value;
  }
}

for (const slider of PHOTO_SLIDERS) {
  slider.input.addEventListener('input', () => {
    syncPhotoOutputs();
    schedulePhotograph();
  });
}
for (const control of [photoLightSelect, photoJpegSelect, photoSceneCheck, photoSeedInput]) {
  control.addEventListener('change', schedulePhotograph);
}
photoView.corners.addEventListener('change', () => {
  if (lastPhotoBitmap && lastPhotoResult) drawDecodePreview(lastPhotoBitmap, lastPhotoResult, photoView);
});
photoPanelEl.addEventListener('toggle', () => {
  if (photoPanelEl.open) schedulePhotograph();
});
document.getElementById('photo-reset-btn').addEventListener('click', () => {
  for (const slider of PHOTO_SLIDERS) slider.input.value = String(slider.value);
  photoLightSelect.value = 'none';
  photoJpegSelect.value = '0';
  photoSceneCheck.checked = false;
  photoSeedInput.value = '1';
  syncPhotoOutputs();
  schedulePhotograph();
});
syncPhotoOutputs();

/* ─── Find the limit ─── */

const limitEffectSelect = document.getElementById('limit-effect');
const limitSeedsSelect = document.getElementById('limit-seeds');
const limitRunBtn = document.getElementById('limit-run-btn');
const limitCancelBtn = document.getElementById('limit-cancel-btn');
const limitProgressEl = document.getElementById('limit-progress');
const limitSummaryEl = document.getElementById('limit-summary');
const limitTableEl = document.getElementById('limit-table');
const limitValueHeadEl = document.getElementById('limit-value-head');

/** The values a step takes, from mild to strong, rounded so that 0.1 + 0.2 prints as 0.3. */
function steps(from, to, step) {
  const values = [];
  const count = Math.round((to - from) / step);
  for (let i = 0; i <= count; i++) values.push(Math.round((from + i * step) * 1000) / 1000);
  return values;
}

/** Each effect the search can raise: the request field, its steps from mild to strong, and how a value reads. */
const LIMIT_EFFECTS = {
  tilt: { key: 'tilt', head: 'Tilt', values: steps(0, 80, 5), show: (v) => `${v}°` },
  barrel: { key: 'barrel', head: 'Barrel lens', values: steps(0, 40, 2.5), show: (v) => `${v} %` },
  bow: { key: 'bow', head: 'Curved card', values: steps(0, 8, 0.5), show: (v) => `${v} modules` },
  blur: { key: 'blur', head: 'Blur', values: steps(0, 1.5, 0.05), show: (v) => `${v} module` },
  noise: { key: 'noise', head: 'Noise', values: steps(0, 100, 5), show: (v) => `${v} grey levels` },
  // Light lost changes nothing under even light, so the search draws a shadow edge unless a light is chosen.
  lightDepth: {
    key: 'lightDepth', head: 'Light lost', values: steps(0, 100, 5), show: (v) => `${v} %`,
    adjust: (request) => (request.light === 'none' ? { ...request, light: 'shadow' } : request),
  },
  pixelsPerModule: { key: 'pixelsPerModule', head: 'Module size', values: steps(6, 1, -0.25), show: (v) => `${v} px` },
  jpeg: { key: 'jpeg', head: 'JPEG quality', values: steps(95, 5, -5), show: (v) => `${v}` },
};

let limitRunning = false;
let limitCancelled = false;

function setLimitRunning(running) {
  limitRunning = running;
  limitRunBtn.disabled = running || !runtimeReady;
  limitCancelBtn.hidden = !running;
  limitCancelBtn.disabled = false;
  limitProgressEl.hidden = !running;
}

limitCancelBtn.addEventListener('click', () => {
  limitCancelled = true;
  limitCancelBtn.disabled = true;
});

/**
 * Raises one effect step by step over the other settings as they are, photographs each step once a seed and reads it.
 * Each read is a blocking call, so the loop yields between them to keep the page and Cancel responsive.
 */
async function runLimitSearch() {
  if (limitRunning || !runtimeAlive || !runtimeReady || !exports) return;
  if (!ensurePhotoCard(limitSummaryEl)) return;

  const effect = LIMIT_EFFECTS[limitEffectSelect.value];
  const base = effect.adjust ? effect.adjust(photoRequest()) : photoRequest();
  // Seeds only change the noise and the scene: without either, every seed takes the same photograph.
  const varied = base.noise > 0 || base.scene || effect.key === 'noise';
  const seeds = varied ? Number(limitSeedsSelect.value) : 1;
  const source = photoSource;
  const interop = exports.FeatherQR.Playground.QrInterop;

  limitCancelled = false;
  setLimitRunning(true);
  limitValueHeadEl.textContent = effect.head;
  const body = limitTableEl.tBodies[0];
  body.replaceChildren();
  limitTableEl.hidden = false;
  limitSummaryEl.textContent = '';
  limitProgressEl.max = effect.values.length * seeds;
  limitProgressEl.value = 0;

  const results = [];
  let failure = null;
  try {
    outer: for (const value of effect.values) {
      let reads = 0;
      let marks = '';
      for (let k = 0; k < seeds; k++) {
        if (limitCancelled || photoSource !== source) break outer;
        const request = { ...base, [effect.key]: value, seed: base.seed + k };
        const payload = JSON.parse(interop.PhotographRead(JSON.stringify(request)));
        if (payload.error) {
          failure = payload.error;
          break outer;
        }
        const ok = readsAsGenerated(payload.read, source.expected);
        reads += ok ? 1 : 0;
        marks += ok ? '●' : '○';
        limitProgressEl.value += 1;
        await new Promise((resolve) => setTimeout(resolve, 0));
      }
      results.push({ value, reads });
      const row = body.insertRow();
      row.className = reads === seeds ? '' : reads === 0 ? 'is-none' : 'is-partial';
      row.insertCell().textContent = effect.show(value);
      row.insertCell().textContent = `${reads}/${seeds}`;
      const cells = row.insertCell();
      cells.className = 'limit-cells';
      cells.textContent = marks;
    }
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    failure = err?.message ?? String(err);
  } finally {
    setLimitRunning(false);
  }

  const stopped = failure
    ? `Stopped: ${failure}`
    : photoSource !== source
      ? 'Stopped: the code was generated again.'
      : limitCancelled ? 'Cancelled.' : '';
  limitSummaryEl.textContent = [stopped, limitSummary(effect, results, seeds, varied)].filter(Boolean).join(' ');
}

/** Where reading stops: the last step every seed read before the first miss, the first miss, and the step from which nothing reads. */
function limitSummary(effect, results, seeds, varied) {
  if (results.length === 0) return '';
  const firstMiss = results.findIndex((r) => r.reads < seeds);
  const sameSeeds = varied ? '' : ' Every seed takes the same photograph without noise or clutter, so each step was taken once.';
  if (firstMiss < 0) {
    return `Reads at every step, up to ${effect.show(results[results.length - 1].value)}.${sameSeeds}`;
  }
  let noneFrom = results.length;
  while (noneFrom > 0 && results[noneFrom - 1].reads === 0) noneFrom--;
  const upTo = firstMiss === 0 ? '' : `Reads every seed up to ${effect.show(results[firstMiss - 1].value)}`;
  const parts = [];
  if (noneFrom === firstMiss) {
    parts.push(firstMiss === 0
      ? `Reads nothing at any step with the other settings as they are.`
      : `${upTo}, and none from ${effect.show(results[noneFrom].value)} on.`);
  } else {
    parts.push(firstMiss === 0
      ? `Misses already at ${effect.show(results[0].value)} with the other settings as they are.`
      : `${upTo}; first miss at ${effect.show(results[firstMiss].value)}.`);
    if (noneFrom < results.length) parts.push(`Reads none from ${effect.show(results[noneFrom].value)} on.`);
  }
  if (results.slice(firstMiss + 1, noneFrom).some((r) => r.reads > 0)) parts.push('Some photographs past the first miss still read.');
  return parts.join(' ') + sameSeeds;
}

limitRunBtn.addEventListener('click', runLimitSearch);

/** Shows an inline error while keeping the last good image visible. */
function showGenerateError(message) {
  generateErrorEl.hidden = false;
  generateErrorEl.textContent = message;
}

function formatBytes(n) {
  if (!Number.isFinite(n)) return '';
  if (n < 1024) return `${n} B`;
  return `${(n / 1024).toFixed(1)} KB`;
}

/* ─── Control events ─── */

function markCustomPreset() {
  if (!stateControls.applyingState) presetSelect.value = 'custom';
}

for (const el of [
  contentEl, eccSelect, eccBoostCheck, allowKanjiCheck, formatSelect, appIndicatorInput, gs1StemInput,
  rmqrFitSelect, rmqrHeightSelect, sizeRange, quietRange,
  moduleShapeSelect, moduleSizeRange, cornerRange, finderSelect,
  fgColor, bgColor, bgTransparent,
  gradientToggle, gradientDirection,
  logoModeSelect, logoSizeRange, logoBorderRange,
]) {
  el.addEventListener('input', () => {
    syncDerivedControls();
    markCustomPreset();
    scheduleGenerate();
  });
}

versionSelect.addEventListener('input', () => {
  stateControls.syncVersionControls();
  markCustomPreset();
  scheduleGenerate();
});

// Symbology switch rebuilds the dependent selects and resets the quiet zone to
// the symbology's specification default (QR: 4 modules, Micro QR: 2 modules).
symbologySelect.addEventListener('input', () => {
  stateControls.syncSymbologyControls();
  markCustomPreset();
  scheduleGenerate();
});

// A textarea cannot take U+001D from a keyboard, so the button puts its visible stand-in at the cursor.
insertSeparatorBtn.addEventListener('click', () => {
  const start = contentEl.selectionStart ?? contentEl.value.length;
  const end = contentEl.selectionEnd ?? start;
  contentEl.setRangeText('␝', start, end, 'end');
  contentEl.focus();
  contentEl.dispatchEvent(new Event('input', { bubbles: true }));
});

// The retail label the GS1 benchmarks use: a GTIN, an expiry date and a batch.
gs1ExampleBtn.addEventListener('click', () => {
  contentEl.value = '(01)09520123456788(17)251231(10)ABC123';
  contentEl.dispatchEvent(new Event('input', { bubbles: true }));
});

// Color inputs inside the dynamic gradient list (event delegation).
gradientColorsEl.addEventListener('input', () => {
  markCustomPreset();
  scheduleGenerate();
});

presetSelect.addEventListener('input', () => {
  const preset = PRESETS[presetSelect.value];
  if (!preset) return;
  applyStateToControls(presetToState(preset));
  scheduleGenerate();
});

logoFile.addEventListener('change', async () => {
  const file = logoFile.files?.[0];
  if (!file) return;
  try {
    customLogoBytes = new Uint8Array(await file.arrayBuffer());
    logoModeSelect.value = 'custom';
    syncDerivedControls();
    markCustomPreset();
    scheduleGenerate();
  } catch (e) {
    showToast(`Could not read the image file: ${e?.message ?? e}`, 'error');
  }
});

/* ─── Download / copy image ─── */

downloadBtn.addEventListener('click', () => {
  if (!lastPngBytes) return;
  const blob = new Blob([lastPngBytes], { type: 'image/png' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'qrcode.png';
  document.body.appendChild(a);
  a.click();
  a.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
});

// SVG is rendered on demand (not on every preview regeneration) with the current
// settings; the preview <img> itself stays PNG-backed.
downloadSvgBtn.addEventListener('click', () => {
  if (!runtimeAlive || !runtimeReady || !exports) return;

  const state = collectState();
  if (!state.content.trim()) return;

  const logoBytes = state.logo.mode === 'custom' ? customLogoBytes : EMPTY_BYTES;
  let bytes;
  try {
    bytes = exports.FeatherQR.Playground.QrInterop.GenerateSvg(JSON.stringify(requestFor(state)), logoBytes);
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    showToast(`Could not generate SVG: ${err?.message ?? err}`, 'error');
    return;
  }

  // Error envelope is UTF-8 JSON starting with '{'; SVG always starts with '<'.
  if (bytes.length === 0 || bytes[0] === 0x7b) {
    let message = 'SVG generation failed.';
    try {
      message = JSON.parse(utf8Decoder.decode(bytes)).error ?? message;
    } catch {
      /* keep generic message */
    }
    showToast(message, 'error');
    return;
  }

  const blob = new Blob([bytes], { type: 'image/svg+xml' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'qrcode.svg';
  document.body.appendChild(a);
  a.click();
  a.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
});

copyImageBtn.addEventListener('click', async () => {
  if (!lastPngBytes) return;
  try {
    const blob = new Blob([lastPngBytes], { type: 'image/png' });
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]);
    showToast('Image copied to clipboard.', 'success');
  } catch (e) {
    showToast(`Could not copy image: ${e?.message ?? e}`, 'error');
  }
});

/* ─── Share link ─── */

/**
 * Synchronous clipboard fallback (helps while the originating click is still a "user gesture").
 * @param {string} text
 * @returns {boolean}
 */
function tryClipboardCopyViaTextArea(text) {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.setAttribute('readonly', '');
  ta.style.position = 'fixed';
  ta.style.left = '-9999px';
  ta.style.top = '0';
  document.body.appendChild(ta);
  try {
    ta.focus();
    ta.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    ta.remove();
  }
}

/** @param {string} text */
async function copyTextToClipboard(text) {
  if (tryClipboardCopyViaTextArea(text)) return true;
  const w = navigator.clipboard?.writeText;
  if (!w) return false;
  try {
    await w.call(navigator.clipboard, text);
    return true;
  } catch {
    return false;
  }
}

permalinkBtn.addEventListener('click', async () => {
  try {
    const state = collectState();
    if (state.logo.mode === 'custom') {
      // Uploaded image bytes do not fit in a URL; the link falls back to the built-in logo.
      state.logo = { ...state.logo, mode: 'default' };
      showToast('Uploaded logo images are not included in share links, the link uses the built-in logo instead.', 'info');
    }
    const hash = await encodeShareState(state);
    const url = `${location.pathname}${location.search}#${hash}`;
    const fullUrl = `${location.origin}${url}`;
    if (!isShareWithinLimits(hash, fullUrl)) {
      showToast('Content is too long to fit in a share URL. Shorten the QR content and try again.', 'error');
      return;
    }
    history.replaceState(null, '', url);
    const copied = await copyTextToClipboard(location.href);
    showToast(
      copied ? 'Link copied to clipboard.' : 'URL updated, copy from the address bar if clipboard was blocked.',
      copied ? 'success' : 'info',
    );
  } catch (e) {
    showToast(e?.message ?? String(e), 'error');
  }
});

/* ─── Performance benchmark ─── */

/** Target wall-clock per BenchmarkBatch call; keeps the UI responsive between batches. */
const BENCH_BATCH_TARGET_MS = 150;

let benchRunning = false;
let benchCancelled = false;

function formatRate(count, ms) {
  if (ms <= 0) return '—';
  const rate = (count / ms) * 1000;
  return `${rate >= 100 ? Math.round(rate).toLocaleString() : rate.toFixed(1)} codes/s`;
}

function setBenchControlsRunning(running) {
  benchRunBtn.disabled = running || !runtimeReady || !runtimeAlive;
  benchCancelBtn.hidden = !running;
  benchModeSelect.disabled = running;
  benchCountSelect.disabled = running;
}

benchCancelBtn.addEventListener('click', () => {
  benchCancelled = true;
  benchCancelBtn.disabled = true;
});

benchRunBtn.addEventListener('click', () => void runBenchmark());

async function runBenchmark() {
  if (benchRunning || !runtimeReady || !runtimeAlive) return;

  const state = collectState();
  if (!state.content.trim()) {
    showToast('Enter content to encode before running the benchmark.', 'info');
    return;
  }
  // Snapshot options once so mid-run control edits do not skew results.
  const optionsJson = JSON.stringify(requestFor(state));
  const mode = benchModeSelect.value;
  const total = Number(benchCountSelect.value);
  const logoBytes = mode === 'render' && state.logo.mode === 'custom' ? customLogoBytes : EMPTY_BYTES;

  benchRunning = true;
  benchCancelled = false;
  benchCancelBtn.disabled = false;
  setBenchControlsRunning(true);
  benchProgress.hidden = false;
  benchProgress.max = total;
  benchProgress.value = 0;
  benchResult.textContent = 'Running…';

  let done = 0;
  let wasmMs = 0;
  let meta = null;
  // Encode mode is orders of magnitude faster than render mode; start small and adapt.
  let batch = mode === 'encode' ? 128 : 4;
  const wallStart = performance.now();

  try {
    while (done < total && !benchCancelled) {
      const count = Math.min(batch, total - done);
      const json = exports.FeatherQR.Playground.QrInterop.BenchmarkBatch(optionsJson, mode, done, count, logoBytes);
      const result = JSON.parse(json);
      if (result.error) {
        benchResult.textContent = `Failed: ${result.error}`;
        break;
      }
      done += result.count;
      wasmMs += result.elapsedMs;
      meta = result;

      benchProgress.value = done;
      benchResult.textContent = `${done.toLocaleString()} / ${total.toLocaleString()}, ${formatRate(done, wasmMs)}`;

      // Resize the next batch toward the wall-clock target (bounded so one batch cannot stall the tab).
      const perItem = Math.max(result.elapsedMs / result.count, 0.0001);
      batch = Math.max(1, Math.min(50000, Math.round(BENCH_BATCH_TARGET_MS / perItem)));

      // Yield to the event loop so progress paints and Cancel stays clickable.
      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    if (done > 0 && meta && !benchResult.textContent.startsWith('Failed')) {
      const wallSeconds = ((performance.now() - wallStart) / 1000).toFixed(2);
      const avgMs = wasmMs / done;
      const prefix = benchCancelled ? `Cancelled, ${done.toLocaleString()} of ${total.toLocaleString()}` : done.toLocaleString();
      benchResult.textContent =
        `${prefix} codes in ${(wasmMs / 1000).toFixed(2)} s of WASM time, ${formatRate(done, wasmMs)} `
        + `(avg ${avgMs >= 1 ? avgMs.toFixed(2) : avgMs.toFixed(3)} ms/code, ${symbolLabel(meta.symbology ?? symbologySelect.value, meta.qrVersion)} ${meta.matrixSize}×${meta.matrixHeight ?? meta.matrixSize}, wall ${wallSeconds} s)`;
    }
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
    } else {
      benchResult.textContent = `Failed: ${err?.message ?? err}`;
    }
  } finally {
    benchRunning = false;
    setBenchControlsRunning(false);
    benchProgress.hidden = true;
  }
}

/* ─── Startup ─── */

async function restoreFromLocationHash() {
  const raw = window.location.hash;
  if (!raw || raw.length <= 1) return false;
  const previous = collectState();
  try {
    const decoded = await decodeShareHash(raw.slice(1));
    if (!decoded.ok) {
      showToast(`Could not restore from URL: ${decoded.error}. Showing defaults.`, 'info', 6000);
      return false;
    }
    applyStateToControls(decoded.state);
    presetSelect.value = 'custom';
    return true;
  } catch (error) {
    applyStateToControls(previous);
    showToast(`Could not restore from URL: ${error?.message ?? error}. Showing defaults.`, 'info', 6000);
    return false;
  }
}

async function initializeRuntime() {
  try {
    const restored = await restoreFromLocationHash();
    if (!restored) {
      applyStateToControls(presetToState(PRESETS.instagram));
    }

    const runtime = await dotnet
      .withApplicationArguments('playground')
      .create();
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    await runtime.runMain();
    runtimeReady = true;
    syncVersionBadge();
    loading.style.display = 'none';
    permalinkBtn.disabled = false;
    benchRunBtn.disabled = false;
    limitRunBtn.disabled = false;
    generate();
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    runtimeAlive = false;
    runtimeReady = false;
    loading.style.display = 'none';
    showToast(err?.message ?? String(err), 'error', 60000);
  }
}

renderGradientColors(PRESETS.instagram.gradientColors);
syncDerivedControls();
void initializeRuntime();
