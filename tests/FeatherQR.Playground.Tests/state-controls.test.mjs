import test from 'node:test';
import assert from 'node:assert/strict';
import { createStateControls, presetToState } from '../../src/FeatherQR.Playground/wwwroot/state-controls.js';

function fixture() {
  const elements = new Map();
  const document = { getElementById(id) {
    if (!elements.has(id)) elements.set(id, { value: '', checked: false, hidden: false, disabled: false, classList: { toggle() {} } });
    return elements.get(id);
  }};
  const el = id => document.getElementById(id);
  el('symbology-select').value = 'qr';
  el('version-select').value = '-1';
  let colors = ['#000000', '#ffffff'];
  const order = [];
  const hooks = {
    populateEccSelect() { order.push(el('symbology-select').value); el('ecc-select').value = 'M'; },
    populateVersionSelect() { el('version-select').value = '-1'; },
    getGradientColors() { return [...colors]; },
    renderGradientColors(value) { colors = [...value]; },
  };
  const controls = createStateControls(document, hooks);
  return { controls, el, order, hooks };
}

test('restores dependent selections after rebuilding them and collects nested state', () => {
  const { controls, el, order } = fixture();
  controls.applyStateToControls({ content: '日本語', symbology: 'rmqr', ecc: 'H', version: 32,
    height: 17, dataFormat: 'link', gs1Stem: 'https://example.com',
    background: 'transparent', moduleSizePercent: 0.92, moduleCornerRadius: 0.45,
    gradient: { enabled: true, colors: ['#123456', '#abcdef'], direction: 'TopToBottom' },
    logo: { mode: 'none', sizePercent: 18, borderWidth: 6 } });
  const state = controls.collectState();
  assert.deepEqual(order, ['rmqr']);
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
