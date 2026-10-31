import test from 'node:test';
import assert from 'node:assert/strict';
import { decodeShareHash } from '../../src/FeatherQR.Playground/wwwroot/share-payload.js';

function plain(state) {
  return `0.${Buffer.from(JSON.stringify(state)).toString('base64url')}`;
}

test('rejects a gradient color array before it reaches the controls', async () => {
  const result = await decodeShareHash(plain({ v: 1, gradient: { colors: [['#ffffff'], '#000000'] } }));
  assert.equal(result.ok, false);
  assert.match(result.error, /gradient.colors/);
});

const { encodeShareState, SHARE_PAYLOAD_VERSION, MAX_SHARE_HASH_LENGTH } = await import('../../src/FeatherQR.Playground/wwwroot/share-payload.js');
const { deflateRawSync } = await import('node:zlib');
function compressed(state) {
  return `1.${deflateRawSync(Buffer.from(JSON.stringify(state))).toString('base64url')}`;
}
const completeState = {
  content: 'こんにちは 👋', symbology: 'qr', ecc: 'H', eccBoost: true, allowKanji: false,
  dataFormat: 'text', applicationIndicator: '', gs1Stem: '', size: 512, quietZone: 4,
  version: -1, fitStrategy: 'area', height: 0, moduleShape: 'rounded', moduleSizePercent: 0.95,
  moduleCornerRadius: 0.3, finderShape: 'roundedCircle', foreground: '#ABCDEF', background: 'transparent',
  gradient: { enabled: true, colors: ['#ffffff', '#000000'], direction: 'TopLeftToBottomRight' },
  logo: { mode: 'default', sizePercent: 18, borderWidth: 6 },
};

for (const [name, encode] of [['plain', plain], ['compressed', compressed]]) {
  test(`${name}: existing v1 full state and versionless partial links still work`, async () => {
    for (const state of [completeState, { v: 1, ...completeState }, {}, { content: 'old link' }]) {
      const result = await decodeShareHash(encode(state));
      assert.equal(result.ok, true, result.error);
      assert.deepEqual(result.state, state);
    }
  });
  for (const [field, value] of [
    ['gradient', null], ['gradient', []], ['gradient', 'x'], ['logo', []], ['logo', 1],
    ['gradient.colors', [['#ffffff'], '#000000']], ['gradient.colors', [null, '#000000']],
    ['gradient.colors', [{ toString: '#ffffff' }, '#000000']], ['gradient.colors', ['#fff', '#000000']],
    ['gradient.colors', ['#ffffff']], ['gradient.colors', Array(7).fill('#ffffff')],
    ['gradient.enabled', 'true'], ['gradient.direction', {}], ['logo.mode', {}], ['logo.sizePercent', '18'],
    ['ecc', { toString: 0 }], ['foreground', ['#ffffff']], ['background', 42], ['content', []],
    ['moduleShape', 'unknown'], ['finderShape', {}], ['size', '512'], ['version', null], ['height', []],
    ['symbology', 'unknown'], ['dataFormat', []], ['gs1Stem', {}], ['eccBoost', 1], ['quietZone', null],
  ]) {
    test(`${name}: refuses invalid ${field} (${JSON.stringify(value)})`, async () => {
      const [parent, child] = field.split('.');
      const state = { v: 1, [parent]: child ? { [child]: value } : value };
      const result = await decodeShareHash(encode(state));
      assert.equal(result.ok, false);
      assert.ok(result.error.includes(field), result.error);
    });
  }
  test(`${name}: root arrays and unsupported versions fail`, async () => {
    for (const state of [[], null, 'text', 42, { v: 0 }, { v: 2 }, { v: '1' }, { v: null }]) {
      assert.equal((await decodeShareHash(encode(state))).ok, false);
    }
  });
}

test('encode and decode round-trip the entire state', async () => {
  const result = await decodeShareHash(await encodeShareState(completeState));
  assert.equal(result.ok, true, result.error);
  assert.deepEqual(result.state, { ...completeState, v: SHARE_PAYLOAD_VERSION });
});

test('plain fallback remains readable when compression is unavailable', async () => {
  const saved = globalThis.CompressionStream;
  try {
    globalThis.CompressionStream = undefined;
    const hash = await encodeShareState(completeState);
    assert.ok(hash.startsWith('0.'));
    assert.equal((await decodeShareHash(hash)).ok, true);
  } finally {
    globalThis.CompressionStream = saved;
  }
});

test('unknown additive fields do not invalidate older readers', async () => {
  assert.equal((await decodeShareHash(plain({ v: 1, futureSetting: { arbitrary: true } }))).ok, true);
});

test('encoder refuses invalid state and cannot write a false schema version', async () => {
  await assert.rejects(encodeShareState({ v: 2 }), /version/);
  await assert.rejects(encodeShareState({ gradient: { colors: [['#ffffff'], '#000000'] } }), /gradient.colors/);
  await assert.rejects(encodeShareState({ size: Infinity }), /size/);
});

test('corrupt payloads fail without throwing', async () => {
  for (const hash of ['', 'abc', '2.e30', '0.!', '1.e30', '0.' + Buffer.from('{').toString('base64url'), '0.' + 'a'.repeat(MAX_SHARE_HASH_LENGTH)]) {
    assert.equal((await decodeShareHash(hash)).ok, false, hash.slice(0, 40));
  }
});

test('bounds decompressed JSON even when the compressed hash is short', async () => {
  const hash = compressed({ content: 'x'.repeat(300_000) });
  assert.ok(hash.length < MAX_SHARE_HASH_LENGTH);
  const result = await decodeShareHash(hash);
  assert.equal(result.ok, false);
  assert.match(result.error, /too large/);
});

test('decompressed byte limit accepts its boundary and rejects the next byte', async () => {
  const { MAX_SHARE_JSON_BYTES } = await import('../../src/FeatherQR.Playground/wwwroot/share-payload.js');
  const overhead = Buffer.byteLength(JSON.stringify({ content: '' }));
  const state = { content: 'x'.repeat(MAX_SHARE_JSON_BYTES - overhead) };
  assert.equal((await decodeShareHash(compressed(state))).ok, true);
  state.content += 'x';
  assert.equal((await decodeShareHash(compressed(state))).ok, false);
  await assert.rejects(encodeShareState(state), /too large/);
});

test('invalid UTF-8 is refused instead of changing the shared text', async () => {
  const invalid = Buffer.concat([Buffer.from('{"content":"'), Buffer.from([0xff]), Buffer.from('"}')]);
  assert.equal((await decodeShareHash(`0.${invalid.toString('base64url')}`)).ok, false);
});

test('missing decompression support returns a failure without throwing', async () => {
  const saved = globalThis.DecompressionStream;
  try {
    globalThis.DecompressionStream = undefined;
    assert.equal((await decodeShareHash(compressed(completeState))).ok, false);
    assert.equal((await decodeShareHash(plain(completeState))).ok, true);
  } finally {
    globalThis.DecompressionStream = saved;
  }
});

for (const state of [
  { version: 99 }, { version: 1.5 }, { version: 0 }, { version: -2 },
  { symbology: 'qr', ecc: 'EDO' }, { symbology: 'microqr', version: 40 },
  { symbology: 'microqr', ecc: 'H' }, { symbology: 'rmqr', version: 33 },
  { symbology: 'rmqr', ecc: 'L' }, { ecc: 'EDO' }, { height: 8 },
]) {
  test(`rejects values absent from the effective controls: ${JSON.stringify(state)}`, async () => {
    assert.equal((await decodeShareHash(plain(state))).ok, false);
  });
}

test('accepts every version and ECC option of each symbology', async () => {
  for (const [symbology, maximum, levels] of [['qr', 40, ['L', 'M', 'Q', 'H']], ['microqr', 4, ['EDO', 'L', 'M', 'Q']], ['rmqr', 32, ['M', 'H']]]) {
    for (const version of [-1, ...Array.from({ length: maximum }, (_, i) => i + 1)]) {
      for (const ecc of levels) {
        const result = await decodeShareHash(plain({ symbology, version, ecc }));
        assert.equal(result.ok, true, `${symbology}/${version}/${ecc}: ${result.error}`);
      }
    }
  }
});
