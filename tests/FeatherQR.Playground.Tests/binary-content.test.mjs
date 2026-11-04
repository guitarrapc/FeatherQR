import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_ECI_ASSIGNMENT, convertContent, eciAssignmentError, formatHex, hexDump, hexToBytes, parseHex, readEciAssignmentField,
} from '../../src/FeatherQR.Playground/wwwroot/binary-content.js';

for (const [input, hex] of [
  ['48656c6c6f', '48656C6C6F'],
  ['48 65 6C 6C 6F', '48656C6C6F'],
  ['48:65:6c:6C:6f', '48656C6C6F'],
  ['48-65-6c-6c-6f', '48656C6C6F'],
  [' 4865\n6c6c\t6f\r\n', '48656C6C6F'],
  ['de:ad-BE ef', 'DEADBEEF'],
  ['', ''],
  [' :-\n', ''],
]) {
  test(`hex ${JSON.stringify(input)} reads as ${hex || 'no bytes'}`, () => {
    const parsed = parseHex(input);
    assert.equal(parsed.ok, true, parsed.error);
    assert.equal(parsed.hex, hex);
    assert.equal(parsed.length, hex.length / 2);
  });
}

for (const [input, pattern] of [
  ['abc', /odd/],
  ['4 8 6', /3 digits/],
  ['48 6g', /'g' at position 5 is not a hex digit/],
  ['0x48', /'x' at position 2/],
  ['48,65', /',' at position 3/],
  ['48_65', /'_'/],
  ['48\u000065', /U\+0000 at position 3/],
  ['ab😀cd', /'😀' at position 3/],
  ['ＡＢ', /'Ａ' at position 1/],
]) {
  test(`hex ${JSON.stringify(input)} is refused with a reason`, () => {
    const parsed = parseHex(input);
    assert.equal(parsed.ok, false);
    assert.match(parsed.error, pattern);
  });
}

test('a refused digit is reported before an odd count', () => {
  assert.match(parseHex('abz').error, /'z'/);
});

test('hex turns into its bytes', () => {
  assert.deepEqual([...hexToBytes('00FF7f80')], [0x00, 0xff, 0x7f, 0x80]);
  assert.deepEqual([...hexToBytes('')], []);
});

test('bytes format as spaced uppercase pairs, sixteen a line', () => {
  assert.equal(formatHex(new Uint8Array([0x0a, 0xbc, 0xde])), '0A BC DE');
  assert.equal(formatHex(new Uint8Array(0)), '');
  const line = Array.from({ length: 16 }, (_, i) => i.toString(16).toUpperCase().padStart(2, '0')).join(' ');
  assert.equal(formatHex(Uint8Array.from({ length: 17 }, (_, i) => i)), `${line}\n10`);
  // Formatted hex reads back as the same bytes.
  const bytes = Uint8Array.from({ length: 300 }, (_, i) => (i * 37) & 0xff);
  assert.deepEqual(hexToBytes(parseHex(formatHex(bytes)).hex), bytes);
});

test('hex dump shows offsets, sixteen bytes a row and printable ASCII', () => {
  const hex = Buffer.from('Hello, world!\nThis is row two.').toString('hex');
  assert.equal(hexDump(hex),
    '0000  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A 54 68  |Hello, world!.Th|\n'
    + '0010  69 73 20 69 73 20 72 6F  77 20 74 77 6F 2E        |is is row two.|');
});

test('hex dump pads a short row and shows bytes outside printable ASCII as dots', () => {
  // Seven bytes leave three columns of the first group, the group gap and the whole second group empty.
  assert.equal(hexDump('00207E7F80FF41'), `0000  00 20 7E 7F 80 FF 41${' '.repeat(3 + 2 + 23 + 2)}|. ~...A|`);
  assert.equal(hexDump(''), '');
  // A short row lines its ASCII column up with a full one.
  const full = hexDump('00'.repeat(16)).indexOf('|');
  assert.equal(full, 4 + 2 + 23 + 2 + 23 + 2);
  assert.equal(hexDump('41').indexOf('|'), full);
  assert.equal(hexDump('00207E7F80FF41').indexOf('|'), full);
  assert.equal(hexDump('00'.repeat(9)).indexOf('|'), full);
});

test('hex dump widens the offset past 64 KiB', () => {
  const rows = hexDump('00'.repeat(0x10001)).split('\n');
  assert.equal(rows.length, 0x1001);
  assert.match(rows[0], /^00000000  00/);
  assert.match(rows.at(-1), /^00010000  00  +\|\.\|$/);
  assert.match(hexDump('00'.repeat(0x10000)).split('\n').at(-1), /^FFF0  /);
});

for (const [field, value] of [['', null], ['  ', null], ['0', 0], ['899', 899], [' 26 ', 26], ['999999', 999999]]) {
  test(`assignment field ${JSON.stringify(field)} reads as ${value}`, () => {
    assert.equal(readEciAssignmentField(field), value);
    assert.equal(eciAssignmentError(readEciAssignmentField(field)), null);
  });
}

for (const field of ['1000000', '-1', '1.5', 'abc', '1e9']) {
  test(`assignment field ${JSON.stringify(field)} is refused`, () => {
    assert.match(eciAssignmentError(readEciAssignmentField(field)), /0 to 999999/);
  });
}

test('assignment limits are the ECI range', () => {
  assert.equal(MAX_ECI_ASSIGNMENT, 999999);
  assert.equal(eciAssignmentError(undefined), null);
  assert.match(eciAssignmentError(NaN), /whole number/);
});

test('switching text to bytes writes the UTF-8 bytes of the text as hex', () => {
  assert.equal(convertContent('Hello', 'text', 'bytes'), '48 65 6C 6C 6F');
  assert.equal(convertContent('日本', 'text', 'bytes'), 'E6 97 A5 E6 9C AC');
  assert.equal(convertContent('', 'text', 'bytes'), '');
});

test('switching to bytes keeps text that is already hex', () => {
  // Hex pasted before the switch is what the person means to write.
  assert.equal(convertContent('DE:AD:BE:EF', 'text', 'bytes'), 'DE:AD:BE:EF');
  assert.equal(convertContent('48656c6c6f', 'text', 'bytes'), '48656c6c6f');
  // Odd or empty hex is not taken as hex.
  assert.equal(convertContent('abc', 'text', 'bytes'), '61 62 63');
  assert.equal(convertContent(' ', 'text', 'bytes'), '20');
});

test('switching bytes to text reads hex that is UTF-8 text and keeps anything else', () => {
  assert.equal(convertContent('48 65 6C 6C 6F', 'bytes', 'text'), 'Hello');
  assert.equal(convertContent('e6:97:a5', 'bytes', 'text'), '日');
  assert.equal(convertContent('61 09 62 0D 0A 63 1D 64', 'bytes', 'text'), 'a\tb\r\nc\u001dd');
  // Not UTF-8, a control character text would not hold, or not hex: the field keeps what it holds, so nothing is lost.
  assert.equal(convertContent('FF FE 00', 'bytes', 'text'), 'FF FE 00');
  assert.equal(convertContent('12 34', 'bytes', 'text'), '12 34');
  assert.equal(convertContent('41 00', 'bytes', 'text'), '41 00');
  assert.equal(convertContent('41 7F', 'bytes', 'text'), '41 7F');
  assert.equal(convertContent('xyz', 'bytes', 'text'), 'xyz');
  assert.equal(convertContent('abc', 'bytes', 'text'), 'abc');
  assert.equal(convertContent('', 'bytes', 'text'), '');
});

test('a round trip through bytes gives the text back', () => {
  for (const text of ['https://github.com/guitarrapc/FeatherQR', 'こんにちは 👋', 'a\u001db', 'line one\nline two', '1234', 'CAFE']) {
    assert.equal(convertContent(convertContent(text, 'text', 'bytes'), 'bytes', 'text'), text);
  }
});

test('the same kind converts nothing', () => {
  assert.equal(convertContent('xyz', 'bytes', 'bytes'), 'xyz');
  assert.equal(convertContent('Hello', 'text', 'text'), 'Hello');
});
