import { ECC_OPTIONS, RMQR_VERSIONS } from './symbol-options.js';

/** Binds state collection and application once, without loading the WASM host. */
export function createStateControls(document, hooks) {
  const contentEl = document.getElementById('content');
  const symbologySelect = document.getElementById('symbology-select');
  const eccSelect = document.getElementById('ecc-select');
  const eccBoostRow = document.getElementById('ecc-boost-row');
  const eccBoostCheck = document.getElementById('ecc-boost-check');
  const allowKanjiCheck = document.getElementById('allow-kanji-check');
  const appIndicatorRow = document.getElementById('app-indicator-row');
  const appIndicatorInput = document.getElementById('app-indicator-input');
  const separatorRow = document.getElementById('separator-row');
  const formatRow = document.getElementById('format-row');
  const formatSelect = document.getElementById('format-select');
  const gs1StemRow = document.getElementById('gs1-stem-row');
  const gs1StemInput = document.getElementById('gs1-stem-input');
  const gs1ExampleBtn = document.getElementById('gs1-example-btn');
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
  const gradientDirection = document.getElementById('gradient-direction');
  const logoModeSelect = document.getElementById('logo-mode-select');
  const logoFileRow = document.getElementById('logo-file-row');
  const logoSizeRange = document.getElementById('logo-size-range');
  const logoSizeOut = document.getElementById('logo-size-out');
  const logoBorderRange = document.getElementById('logo-border-range');
  const logoBorderOut = document.getElementById('logo-border-out');
  const DATA_FORMATS = ['text', 'gs1', 'link', 'aim'];
  const isMicroSymbology = () => symbologySelect.value === 'microqr';
  const isRmqrSymbology = () => symbologySelect.value === 'rmqr';

  /** True while applyStateToControls runs, so input events do not flip the preset to (custom). */
  let applyingState = false;

  /** @returns {object} playground state; doubles as the WASM QrRequest payload (camelCase) */
  function collectState() {
    return {
      content: contentEl.value,
      symbology: symbologySelect.value,
      ecc: eccSelect.value,
      eccBoost: eccBoostCheck.checked,
      allowKanji: allowKanjiCheck.checked,
      dataFormat: formatSelect.value,
      applicationIndicator: appIndicatorInput.value,
      gs1Stem: gs1StemInput.value,
      size: Number(sizeRange.value),
      quietZone: Number(quietRange.value),
      version: Number(versionSelect.value),
      fitStrategy: rmqrFitSelect.value,
      // A fixed version already pins the height; sending both would be rejected
      // as contradictory when they disagree, so the height applies to Auto only.
      height: versionSelect.value === '-1' ? Number(rmqrHeightSelect.value) : 0,
      moduleShape: moduleShapeSelect.value,
      moduleSizePercent: Number(moduleSizeRange.value) / 100,
      moduleCornerRadius: Number(cornerRange.value) / 100,
      finderShape: finderSelect.value,
      foreground: fgColor.value,
      background: bgTransparent.checked ? 'transparent' : bgColor.value,
      gradient: {
        enabled: gradientToggle.checked,
        colors: hooks.getGradientColors(),
        direction: gradientDirection.value,
      },
      logo: {
        mode: logoModeSelect.value,
        sizePercent: Number(logoSizeRange.value),
        borderWidth: Number(logoBorderRange.value),
      },
    };
  }

  /** @param {object} state partial state (missing fields keep current control values) */
  function applyStateToControls(state) {
    applyingState = true;
    try {
      if (typeof state.content === 'string') contentEl.value = state.content;
      if (state.symbology === 'qr' || state.symbology === 'microqr' || state.symbology === 'rmqr') symbologySelect.value = state.symbology;
      // Symbology decides the legal ECC / version option sets, rebuild before applying values
      populateEccSelect();
      populateVersionSelect();
      if ([...eccSelect.options].some(option => option.value === state.ecc)) eccSelect.value = state.ecc;
      if (typeof state.eccBoost === 'boolean') eccBoostCheck.checked = state.eccBoost;
      if (typeof state.allowKanji === 'boolean') allowKanjiCheck.checked = state.allowKanji;
      if (DATA_FORMATS.includes(state.dataFormat)) formatSelect.value = state.dataFormat;
      if (typeof state.applicationIndicator === 'string') appIndicatorInput.value = state.applicationIndicator.slice(0, 2);
      if (typeof state.gs1Stem === 'string') gs1StemInput.value = state.gs1Stem;
      if (state.fitStrategy === 'area' || state.fitStrategy === 'height' || state.fitStrategy === 'width') rmqrFitSelect.value = state.fitStrategy;
      if (Number.isFinite(state.height) && [0, 7, 9, 11, 13, 15, 17].includes(state.height)) rmqrHeightSelect.value = String(state.height);
      if (Number.isFinite(state.size)) sizeRange.value = String(clamp(state.size, 128, 1024));
      if (Number.isFinite(state.quietZone)) quietRange.value = String(clamp(state.quietZone, 0, 10));
      if (Number.isFinite(state.version)) versionSelect.value = String(state.version);
      if (state.moduleShape) moduleShapeSelect.value = state.moduleShape;
      if (Number.isFinite(state.moduleSizePercent)) moduleSizeRange.value = String(Math.round(clamp(state.moduleSizePercent, 0.5, 1) * 100));
      if (Number.isFinite(state.moduleCornerRadius)) cornerRange.value = String(Math.round(clamp(state.moduleCornerRadius, 0, 1) * 100));
      if (state.finderShape) finderSelect.value = state.finderShape;
      if (state.foreground) fgColor.value = state.foreground;
      if (state.background === 'transparent') {
        bgTransparent.checked = true;
      } else if (state.background) {
        bgTransparent.checked = false;
        bgColor.value = state.background;
      }
      if (state.gradient) {
        if (typeof state.gradient.enabled === 'boolean') gradientToggle.checked = state.gradient.enabled;
        if (Array.isArray(state.gradient.colors) && state.gradient.colors.length >= 2) {
          hooks.renderGradientColors(state.gradient.colors);
        }
        if (state.gradient.direction) gradientDirection.value = state.gradient.direction;
      }
      if (state.logo) {
        if (state.logo.mode) logoModeSelect.value = state.logo.mode;
        if (Number.isFinite(state.logo.sizePercent)) logoSizeRange.value = String(clamp(state.logo.sizePercent, 5, 35));
        if (Number.isFinite(state.logo.borderWidth)) logoBorderRange.value = String(clamp(state.logo.borderWidth, 0, 24));
      }
      syncDerivedControls();
    } finally {
      applyingState = false;
    }
  }

  /** Repopulates the ECC select for the symbology, keeping the value when still valid. */
  function populateEccSelect() {
    const prev = eccSelect.value;
    eccSelect.replaceChildren();
    for (const [value, label] of ECC_OPTIONS[isRmqrSymbology() ? 'rmqr' : isMicroSymbology() ? 'microqr' : 'qr']) {
      const opt = document.createElement('option');
      opt.value = value;
      opt.textContent = label;
      eccSelect.appendChild(opt);
    }
    eccSelect.value = [...eccSelect.options].some((o) => o.value === prev) ? prev : 'M';
  }

  /** Repopulates the version select for the symbology, keeping the value when still valid. */
  function populateVersionSelect() {
    const prev = versionSelect.value;
    versionSelect.replaceChildren();
    const auto = document.createElement('option');
    auto.value = '-1';
    auto.textContent = 'Auto';
    versionSelect.appendChild(auto);
    if (isRmqrSymbology()) {
      RMQR_VERSIONS.forEach(([h, w], i) => {
        const opt = document.createElement('option');
        opt.value = String(i + 1);
        opt.textContent = `R${h}x${w} (${w}×${h})`;
        versionSelect.appendChild(opt);
      });
    } else if (isMicroSymbology()) {
      for (let v = 1; v <= 4; v++) {
        const side = 9 + 2 * v;
        const opt = document.createElement('option');
        opt.value = String(v);
        opt.textContent = `M${v} (${side}×${side})`;
        versionSelect.appendChild(opt);
      }
    } else {
      for (let v = 1; v <= 40; v++) {
        const opt = document.createElement('option');
        opt.value = String(v);
        opt.textContent = `${v} (${17 + 4 * v}×${17 + 4 * v})`;
        versionSelect.appendChild(opt);
      }
    }
    versionSelect.value = [...versionSelect.options].some((o) => o.value === prev) ? prev : '-1';
  }

  function syncSymbologyControls() {
    populateEccSelect();
    populateVersionSelect();
    quietRange.value = isMicroSymbology() || isRmqrSymbology() ? '2' : '4';
    syncDerivedControls();
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  /** Updates range outputs and conditional row visibility from current control values. */
  function syncDerivedControls() {
    sizeOut.textContent = sizeRange.value;
    quietOut.textContent = quietRange.value;
    moduleSizeOut.textContent = moduleSizeRange.value;
    cornerOut.textContent = cornerRange.value;
    logoSizeOut.textContent = logoSizeRange.value;
    logoBorderOut.textContent = logoBorderRange.value;
    cornerRow.hidden = moduleShapeSelect.value !== 'rounded';
    // Micro QR and rMQR have no ECC headroom for overlays, so the logo options do not apply, and
    // ECC boost is Standard QR only for the same reasons the library scopes it there. The finder
    // shape does apply to them now; wiring the control up for both is still to do.
    eccBoostRow.hidden = isMicroSymbology() || isRmqrSymbology();
    finderRow.hidden = isMicroSymbology() || isRmqrSymbology();
    logoPanel.hidden = isMicroSymbology() || isRmqrSymbology();
    // Micro QR has neither FNC1 nor a GS1 carrier. The stem belongs to a Digital Link and the indicator
    // to an AIM format alone.
    const format = isMicroSymbology() ? 'text' : formatSelect.value;
    formatRow.hidden = isMicroSymbology();
    gs1StemRow.hidden = format !== 'link';
    appIndicatorRow.hidden = format !== 'aim';
    gs1ExampleBtn.hidden = format !== 'gs1' && format !== 'link';
    separatorRow.hidden = format === 'text';
    // rMQR fit options apply only to automatic version selection.
    rmqrFitRow.hidden = !isRmqrSymbology();
    rmqrHeightRow.hidden = !isRmqrSymbology();
    // A fixed version already pins both dimensions: fit strategy and fixed height
    // only apply to automatic selection (the library rejects a disagreeing pair).
    rmqrFitSelect.disabled = versionSelect.value !== '-1';
    rmqrHeightSelect.disabled = versionSelect.value !== '-1';
    gradientPanel.hidden = !gradientToggle.checked;
    fgRow.classList.toggle('field--disabled', gradientToggle.checked);
    fgColor.disabled = gradientToggle.checked;
    bgColor.disabled = bgTransparent.checked;
    logoFileRow.hidden = logoModeSelect.value !== 'custom';
  }

  populateEccSelect();
  populateVersionSelect();

  return { collectState, applyStateToControls, syncDerivedControls, syncSymbologyControls, get applyingState() { return applyingState; } };
}

/** Flat preset entry → state shape used by applyStateToControls / share links. */
export function presetToState(preset) {
  return {
    ecc: preset.ecc,
    moduleShape: preset.moduleShape,
    moduleSizePercent: preset.moduleSizePercent,
    moduleCornerRadius: preset.moduleCornerRadius,
    finderShape: preset.finderShape,
    foreground: preset.foreground,
    background: preset.background,
    gradient: {
      enabled: preset.gradientEnabled,
      colors: preset.gradientColors,
      direction: preset.gradientDirection,
    },
    logo: {
      mode: preset.logoMode,
      sizePercent: preset.logoSizePercent,
      borderWidth: preset.logoBorderWidth,
    },
  };
}

