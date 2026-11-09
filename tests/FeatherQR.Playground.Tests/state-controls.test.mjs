import test from 'node:test';
import assert from 'node:assert/strict';
import { createStateControls, presetToState, requestError, requestFor } from '../../src/FeatherQR.Playground/wwwroot/state-controls.js';

import { readFileSync } from 'node:fs';
import { PRESETS } from '../../src/FeatherQR.Playground/wwwroot/presets.js';
import { encodeShareState, decodeShareHash } from '../../src/FeatherQR.Playground/wwwroot/share-payload.js';
import { validateShareState } from '../../src/FeatherQR.Playground/wwwroot/share-state.js';

const html = readFileSync(new URL('../../src/FeatherQR.Playground/wwwroot/index.html', import.meta.url), 'utf8');

// Selects reject missing options, unlike a plain object with a value property.
class Select {
  options = [];
  selectedIndex = -1;
  get value() { return this.options[this.selectedIndex]?.value ?? ''; }
  set value(value) { this.selectedIndex = this.options.findIndex(option => option.value === String(value)); }
  replaceChildren() { this.options = []; this.selectedIndex = -1; }
  appendChild(option) {
    this.options.push(option);
    if (this.selectedIndex === -1) this.selectedIndex = 0;
  }
}

function fixture() {
  const elements = new Map();
  for (const match of html.matchAll(/<select\b[^>]*id="([^"]+)"[^>]*>([\s\S]*?)<\/select>/g)) {
    const select = new Select();
    for (const option of match[2].matchAll(/<option\b([^>]*)>([\s\S]*?)<\/option>/g)) {
      const value = /value="([^"]*)"/.exec(option[1])?.[1] ?? option[2];
      select.appendChild({ value });
      if (/\bselected\b/.test(option[1])) select.value = value;
    }
    elements.set(match[1], select);
  }
  for (const match of html.matchAll(/<input\b([^>]+)>/g)) {
    const id = /id="([^"]+)"/.exec(match[1])?.[1];
    if (id) elements.set(id, { value: /value="([^"]*)"/.exec(match[1])?.[1] ?? '', checked: /\bchecked\b/.test(match[1]) });
  }
  const document = {
    createElement: () => ({ value: '', textContent: '' }),
    getElementById(id) {
      if (!elements.has(id)) elements.set(id, { value: '', checked: false, hidden: false, disabled: false, classList: { toggle() {} } });
      return elements.get(id);
    },
  };
  const el = id => document.getElementById(id);
  el('content').value = '12345';
  let colors = ['#000000', '#ffffff'];
  const hooks = {
    getGradientColors() { return [...colors]; },
    renderGradientColors(value) { colors = [...value]; },
  };
  const controls = createStateControls(document, hooks);
  return { controls, el, hooks };
}

test('restores dependent selections after rebuilding them and collects nested state', () => {
  const { controls, el } = fixture();
  controls.applyStateToControls({ content: '日本語', symbology: 'rmqr', ecc: 'H', version: 32,
    height: 17, dataFormat: 'link', gs1Stem: 'https://example.com',
    background: 'transparent', moduleSizePercent: 0.92, moduleCornerRadius: 0.45,
    gradient: { enabled: true, colors: ['#123456', '#abcdef'], direction: 'TopToBottom' },
    logo: { mode: 'none', sizePercent: 18, borderWidth: 6 } });
  const state = controls.collectState();
  assert.equal(state.content, '日本語');
  assert.equal(state.ecc, 'H');
  assert.equal(state.version, 32);
  assert.equal(state.height, 0);
  assert.equal(state.background, 'transparent');
  assert.equal(state.moduleSizePercent, 0.92);
  assert.deepEqual(state.gradient, { enabled: true, colors: ['#123456', '#abcdef'], direction: 'TopToBottom' });
  assert.deepEqual(state.logo, { mode: 'none', sizePercent: 18, borderWidth: 6 });
  assert.equal(el('rmqr-height-select').disabled, true);
  assert.equal(el('logo-panel').hidden, true);
  assert.equal(el('gs1-stem-row').hidden, false);
  assert.equal(el('fg-color').disabled, true);
});

test('partial application preserves content, clamps sliders and synchronizes outputs', () => {
  const { controls, el } = fixture();
  el('content').value = 'keep';
  controls.applyStateToControls({ size: 2000, quietZone: -1, moduleSizePercent: 0,
    moduleCornerRadius: 2, logo: { sizePercent: 100, borderWidth: -5 }, height: 7 });
  const state = controls.collectState();
  assert.equal(state.content, 'keep');
  assert.equal(state.size, 1024);
  assert.equal(state.quietZone, 0);
  assert.equal(state.moduleSizePercent, 0.5);
  assert.equal(state.moduleCornerRadius, 1);
  assert.equal(state.logo.sizePercent, 35);
  assert.equal(state.logo.borderWidth, 0);
  assert.equal(state.height, 7);
  assert.equal(el('size-out').textContent, '1024');
});

for (const symbology of ['qr', 'microqr', 'rmqr']) {
  test(`derived visibility for ${symbology}`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ symbology, dataFormat: 'aim', moduleShape: 'circle', background: '#ffffff' });
    assert.equal(el('format-row').hidden, symbology === 'microqr');
    assert.equal(el('app-indicator-row').hidden, symbology === 'microqr');
    assert.equal(el('ecc-boost-row').hidden, symbology !== 'qr');
    assert.equal(el('rmqr-fit-row').hidden, symbology !== 'rmqr');
    assert.equal(el('corner-row').hidden, true);
    assert.equal(el('bg-color').disabled, false);
  });
}

test('application guard is active during callbacks and clears after exceptions', () => {
  const { controls, hooks } = fixture();
  hooks.renderGradientColors = () => {
    assert.equal(controls.applyingState, true);
    throw new Error('render failed');
  };
  assert.throws(() => controls.applyStateToControls({ gradient: { colors: ['#000000', '#ffffff'] } }), /render failed/);
  assert.equal(controls.applyingState, false);
  controls.applyStateToControls({ content: 'recovered' });
  assert.equal(controls.collectState().content, 'recovered');
});

test('preset conversion preserves the shared nested state representation', () => {
  const state = presetToState({ ecc: 'H', gradientEnabled: true, gradientColors: ['#000000', '#ffffff'],
    gradientDirection: 'LeftToRight', logoMode: 'builtin', logoSizePercent: 18, logoBorderWidth: 6 });
  assert.equal(state.ecc, 'H');
  assert.deepEqual(state.gradient, { enabled: true, colors: ['#000000', '#ffffff'], direction: 'LeftToRight' });
  assert.deepEqual(state.logo, { mode: 'builtin', sizePercent: 18, borderWidth: 6 });
  assert.equal(Object.hasOwn(state, 'content'), false);
});

test('a preset with unsupported ECC retains the current legal selection', () => {
  const { controls } = fixture();
  controls.applyStateToControls({ symbology: 'microqr', ecc: 'L' });
  controls.applyStateToControls({ ecc: 'H' });
  assert.equal(controls.collectState().ecc, 'L');
});

for (const symbology of ['qr', 'microqr', 'rmqr']) {
  for (const [name, preset] of Object.entries(PRESETS)) {
    test(`${symbology} + ${name}: preset, manual edit, share restore and next preset`, async () => {
      const { controls, el } = fixture();
      el('symbology-select').value = symbology;
      controls.syncSymbologyControls();
      controls.applyStateToControls(presetToState(preset));
      const expectedEcc = ({ qr: ['L', 'M', 'Q', 'H'], microqr: ['EDO', 'L', 'M', 'Q'], rmqr: ['M', 'H'] })[symbology].includes(preset.ecc) ? preset.ecc : 'M';
      assert.equal(el('ecc-select').value, expectedEcc);
      el('bg-transparent-check').checked = true;
      el('gradient-check').checked = false;
      controls.syncDerivedControls();
      assert.equal(el('bg-color').disabled, true);
      assert.equal(el('fg-color').disabled, false);
      const state = controls.collectState();
      assert.equal(validateShareState(state), null);
      const decoded = await decodeShareHash(await encodeShareState(state));
      assert.equal(decoded.ok, true);
      const restored = fixture();
      restored.controls.applyStateToControls(decoded.state);
      assert.deepEqual(restored.controls.collectState(), state);
      for (const next of Object.values(PRESETS)) {
        restored.controls.applyStateToControls(presetToState(next));
        assert.equal(validateShareState(restored.controls.collectState()), null);
      }
    });
  }
}

for (const from of ['qr', 'microqr', 'rmqr']) {
  for (const to of ['qr', 'microqr', 'rmqr']) {
    test(`manual symbology switch ${from} -> ${to} preserves valid selections`, () => {
      const { controls, el } = fixture();
      controls.applyStateToControls({ symbology: from, ecc: from === 'microqr' ? 'EDO' : 'H', version: from === 'qr' ? 40 : from === 'microqr' ? 4 : 32 });
      const previous = controls.collectState();
      el('symbology-select').value = to;
      controls.syncSymbologyControls();
      const state = controls.collectState();
      const eccs = ({ qr: ['L', 'M', 'Q', 'H'], microqr: ['EDO', 'L', 'M', 'Q'], rmqr: ['M', 'H'] })[to];
      assert.equal(state.ecc, eccs.includes(previous.ecc) ? previous.ecc : 'M');
      assert.equal(state.version, previous.version <= ({ qr: 40, microqr: 4, rmqr: 32 })[to] ? previous.version : -1);
      assert.equal(state.quietZone, to === 'qr' ? 4 : 2);
      assert.equal(validateShareState(state), null);
    });
  }
}

for (const format of ['text', 'gs1', 'link', 'aim']) {
  test(`format ${format} survives hiding for Micro QR and returning to QR`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ dataFormat: format, gs1Stem: 'https://example.com', applicationIndicator: 'A' });
    el('symbology-select').value = 'microqr';
    controls.syncSymbologyControls();
    assert.equal(el('format-row').hidden, true);
    assert.equal(el('gs1-stem-row').hidden, true);
    assert.equal(el('app-indicator-row').hidden, true);
    el('symbology-select').value = 'qr';
    controls.syncSymbologyControls();
    assert.equal(el('format-select').value, format);
    assert.equal(el('gs1-stem-row').hidden, format !== 'link');
    assert.equal(el('app-indicator-row').hidden, format !== 'aim');
    assert.equal(controls.collectState().applicationIndicator, 'A');
  });
}

test('fixed rMQR version suppresses height, returning to Auto restores it', async () => {
  const { controls, el } = fixture();
  controls.applyStateToControls({ symbology: 'rmqr', height: 17, version: -1 });
  el('version-select').value = '1';
  controls.syncDerivedControls();
  assert.equal(controls.collectState().height, 0);
  assert.equal(el('rmqr-height-select').disabled, true);
  const decoded = await decodeShareHash(await encodeShareState(controls.collectState()));
  const restored = fixture();
  restored.controls.applyStateToControls(decoded.state);
  assert.equal(restored.controls.collectState().height, 0);
  el('version-select').value = '-1';
  controls.syncDerivedControls();
  assert.equal(controls.collectState().height, 17);
  assert.equal(el('rmqr-height-select').disabled, false);
});

test('Micro QR M1 restores with detection-only ECC instead of M', () => {
  const { controls, el } = fixture();
  controls.applyStateToControls({ symbology: 'microqr', version: 1, ecc: 'M' });
  assert.deepEqual(el('ecc-select').options.map(option => option.value), ['EDO']);
  assert.equal(controls.collectState().ecc, 'EDO');
});

const microLevels = { '-1': ['EDO', 'L', 'M', 'Q'], 1: ['EDO'], 2: ['L', 'M'], 3: ['L', 'M'], 4: ['L', 'M', 'Q'] };
for (const [from, levels] of Object.entries(microLevels)) {
  for (const ecc of levels) {
    for (const [to, available] of Object.entries(microLevels)) {
      test(`Micro QR ${from}/${ecc} -> ${to} reconciles ECC and preserves quiet zone`, () => {
        const { controls, el } = fixture();
        controls.applyStateToControls({ symbology: 'microqr', version: Number(from), ecc, quietZone: 5 });
        el('version-select').value = to;
        controls.syncVersionControls();
        assert.deepEqual(el('ecc-select').options.map(option => option.value), available);
        assert.equal(controls.collectState().ecc, available.includes(ecc) ? ecc : to === '1' ? 'EDO' : 'M');
        assert.equal(controls.collectState().version, Number(to));
        assert.equal(controls.collectState().quietZone, 5);
      });
    }
  }
}
for (const [version, available] of Object.entries(microLevels)) {
  for (const [name, preset] of Object.entries(PRESETS)) {
    test(`Micro QR ${version} + ${name} preset and share restoration`, async () => {
      const { controls, el } = fixture();
      controls.applyStateToControls({ symbology: 'microqr', version: Number(version) });
      controls.applyStateToControls(presetToState(preset));
      const state = controls.collectState();
      assert.ok(available.includes(state.ecc));
      assert.equal(state.version, Number(version));
      const decoded = await decodeShareHash(await encodeShareState(state));
      assert.equal(decoded.ok, true);
      const restored = fixture();
      restored.controls.applyStateToControls(decoded.state);
      assert.deepEqual(restored.controls.collectState(), state);
      assert.deepEqual(el('ecc-select').options.map(option => option.value), available);
    });
  }
  for (const ecc of ['EDO', 'L', 'M', 'Q']) {
    test(`legacy share Micro QR ${version}/${ecc} keeps version and normalizes incompatible ECC`, async () => {
      const decoded = await decodeShareHash(await encodeShareState({ symbology: 'microqr', version: Number(version), ecc }));
      assert.equal(decoded.ok, true);
      const { controls } = fixture();
      controls.applyStateToControls(decoded.state);
      assert.equal(controls.collectState().version, Number(version));
      assert.equal(controls.collectState().ecc, available.includes(ecc) ? ecc : version === '1' ? 'EDO' : 'M');
    });
  }
}
for (const symbology of ['qr', 'rmqr']) {
  test(`${symbology} version changes retain all supported ECC options`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ symbology, ecc: 'H' });
    for (const version of ['1', '4', '-1']) {
      el('version-select').value = version;
      controls.syncVersionControls();
      assert.deepEqual(el('ecc-select').options.map(option => option.value), symbology === 'qr' ? ['L', 'M', 'Q', 'H'] : ['M', 'H']);
      assert.equal(controls.collectState().ecc, 'H');
    }
  });
}

for (const version of [1, 2, 4]) {
  test(`switching QR version ${version} to Micro QR filters ECC after version selection`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ symbology: 'qr', version, ecc: 'H' });
    el('symbology-select').value = 'microqr';
    controls.syncSymbologyControls();
    assert.equal(controls.collectState().version, version);
    assert.equal(controls.collectState().ecc, version === 1 ? 'EDO' : 'M');
    assert.deepEqual(el('ecc-select').options.map(option => option.value), microLevels[version]);
    el('symbology-select').value = 'qr';
    controls.syncSymbologyControls();
    assert.equal(controls.collectState().ecc, 'M');
  });
}

for (const symbology of ['qr', 'microqr', 'rmqr']) {
  test(`${symbology} bytes mode turns off the options bytes refuse and keeps their values`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ symbology, contentKind: 'bytes', content: '48 65 6C 6C 6F',
      dataFormat: 'gs1', allowKanji: true, applicationIndicator: 'A', eciAssignment: 899 });
    assert.equal(el('allow-kanji-check').disabled, true);
    assert.equal(el('format-select').disabled, true);
    assert.equal(el('separator-row').hidden, true);
    assert.equal(el('gs1-stem-row').hidden, true);
    assert.equal(el('app-indicator-row').hidden, true);
    assert.equal(el('gs1-example-btn').hidden, true);
    assert.equal(el('bytes-mode-hint').hidden, false);
    assert.match(el('bytes-mode-hint').textContent, /Kanji/);
    assert.equal(/no ECI/.test(el('bytes-mode-hint').textContent), symbology === 'microqr');
    assert.equal(el('content-file-btn').hidden, false);
    assert.equal(el('eci-assignment-row').hidden, false);
    assert.equal(el('eci-assignment-input').disabled, symbology === 'microqr');
    const state = controls.collectState();
    assert.equal(state.contentKind, 'bytes');
    assert.equal(state.dataFormat, 'gs1');
    assert.equal(state.allowKanji, true);
    assert.equal(state.eciAssignment, 899);
    const request = requestFor(state);
    assert.equal(request.contentKind, 'bytes');
    assert.equal(request.content, '48656C6C6F');
    assert.equal(request.gs1, 'none');
    assert.equal(request.fnc1, 'none');
    assert.equal(request.allowKanji, false);
    assert.equal(request.eciAssignment, symbology === 'microqr' ? null : 899);
    assert.equal(requestError(state), null);
  });

  test(`${symbology} text mode hides the bytes controls and sends no assignment`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ symbology, contentKind: 'bytes', dataFormat: 'aim', allowKanji: true, eciAssignment: 26 });
    controls.applyStateToControls({ contentKind: 'text' });
    assert.equal(el('allow-kanji-check').disabled, false);
    assert.equal(el('format-select').disabled, false);
    assert.equal(el('app-indicator-row').hidden, symbology === 'microqr');
    assert.equal(el('separator-row').hidden, symbology === 'microqr');
    assert.equal(el('bytes-mode-hint').hidden, true);
    assert.equal(el('content-file-btn').hidden, true);
    assert.equal(el('eci-assignment-row').hidden, true);
    assert.equal(el('content-hex-status').hidden, true);
    const state = controls.collectState();
    // The hidden field keeps its value for a return to bytes, and the request leaves it out.
    assert.equal(state.eciAssignment, 26);
    const request = requestFor(state);
    assert.equal(request.contentKind, 'text');
    assert.equal(request.eciAssignment, null);
    assert.equal(request.allowKanji, true);
    assert.equal(request.fnc1, symbology === 'microqr' ? 'none' : 'second');
  });
}

test('text requests keep the content and data format routes they had', () => {
  const { controls } = fixture();
  controls.applyStateToControls({ content: '(01)09520123456788␝', dataFormat: 'gs1' });
  const request = requestFor(controls.collectState());
  assert.equal(request.content, '(01)09520123456788\u001d');
  assert.equal(request.gs1, 'qr');
  controls.applyStateToControls({ content: 'a␝b', dataFormat: 'text' });
  assert.equal(requestFor(controls.collectState()).content, 'a␝b');
  assert.equal(requestError(controls.collectState()), null);
});

for (const [content, status] of [
  ['de ad be ef', '4 bytes'], ['00', '1 byte'], ['', 'No bytes yet'], ['abc', /odd/], ['zz', /'z' at position 1/],
]) {
  test(`hex status for ${JSON.stringify(content)}`, () => {
    const { controls, el } = fixture();
    controls.applyStateToControls({ contentKind: 'bytes', content });
    assert.equal(el('content-hex-status').hidden, false);
    if (typeof status === 'string') assert.equal(el('content-hex-status').textContent, status);
    else assert.match(el('content-hex-status').textContent, status);
  });
}

for (const [state, pattern] of [
  [{ contentKind: 'bytes', content: 'abc' }, /odd/],
  [{ contentKind: 'bytes', content: 'xy' }, /'x'/],
  [{ contentKind: 'bytes', content: ' : ' }, /Enter hex/],
  [{ contentKind: 'bytes', content: '41', eciAssignment: 1000000 }, /ECI assignment/],
  [{ contentKind: 'bytes', content: '41', eciAssignment: 1.5 }, /ECI assignment/],
  [{ contentKind: 'bytes', content: '41', symbology: 'rmqr', eciAssignment: -1 }, /ECI assignment/],
]) {
  test(`request error for ${JSON.stringify(state)}`, () => {
    assert.match(requestError({ symbology: 'qr', eciAssignment: null, ...state }), pattern);
  });
}

for (const state of [
  { contentKind: 'bytes', content: '41', symbology: 'microqr', eciAssignment: 1000000 },
  { contentKind: 'text', content: 'not hex', symbology: 'qr', eciAssignment: 1000000 },
  { contentKind: 'bytes', content: '41', symbology: 'qr', eciAssignment: 0 },
  { contentKind: 'bytes', content: '41', symbology: 'rmqr', eciAssignment: 999999 },
]) {
  test(`no request error for ${JSON.stringify(state)}`, () => {
    assert.equal(requestError(state), null);
  });
}

test('the assignment field reads blank as none and restores none as blank', () => {
  const { controls, el } = fixture();
  controls.applyStateToControls({ contentKind: 'bytes', eciAssignment: 0 });
  assert.equal(el('eci-assignment-input').value, '0');
  assert.equal(controls.collectState().eciAssignment, 0);
  controls.applyStateToControls({ eciAssignment: null });
  assert.equal(el('eci-assignment-input').value, '');
  assert.equal(controls.collectState().eciAssignment, null);
  el('eci-assignment-input').value = ' 899 ';
  assert.equal(controls.collectState().eciAssignment, 899);
});

test('bytes content, kind and assignment survive a share link', async () => {
  const { controls } = fixture();
  controls.applyStateToControls({ symbology: 'rmqr', contentKind: 'bytes', content: 'DE AD BE EF', eciAssignment: 899 });
  const state = controls.collectState();
  assert.equal(validateShareState(state), null);
  const decoded = await decodeShareHash(await encodeShareState(state));
  assert.equal(decoded.ok, true, decoded.error);
  const restored = fixture();
  restored.controls.applyStateToControls(decoded.state);
  assert.deepEqual(restored.controls.collectState(), state);
  assert.equal(restored.el('eci-assignment-row').hidden, false);
});

test('a new page starts as text with no assignment', () => {
  const { controls, el } = fixture();
  // The page syncs the controls once at startup, and the markup hides the bytes controls before that.
  controls.syncDerivedControls();
  const state = controls.collectState();
  assert.equal(state.contentKind, 'text');
  assert.equal(state.eciAssignment, null);
  assert.equal(el('eci-assignment-row').hidden, true);
  for (const id of ['eci-assignment-row', 'content-file-btn', 'content-hex-status', 'bytes-mode-hint']) {
    assert.match(html, new RegExp(`id="${id}"[^>]*\\shidden\\b`), id);
  }
});

for (const old of [{ content: 'old link' }, { v: 1, content: 'old link', symbology: 'qr', ecc: 'H', dataFormat: 'text' }]) {
  test(`an old link ${JSON.stringify(old)} restores as text with no assignment`, async () => {
    const decoded = await decodeShareHash(`0.${Buffer.from(JSON.stringify(old)).toString('base64url')}`);
    assert.equal(decoded.ok, true, decoded.error);
    const { controls, el } = fixture();
    controls.applyStateToControls(decoded.state);
    const state = controls.collectState();
    assert.equal(state.content, 'old link');
    assert.equal(state.contentKind, 'text');
    assert.equal(state.eciAssignment, null);
    assert.equal(requestFor(state).contentKind, 'text');
    assert.equal(el('eci-assignment-row').hidden, true);
  });
}
