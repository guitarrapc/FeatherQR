/* ─── Bytes content: hex in, hex dump out ─── */

/** The ECI assignment range a symbol can declare. */
export const MAX_ECI_ASSIGNMENT = 999999;

/** No symbol holds more bytes than this: 7,089 digits in a version 40-L QR code. */
export const MAX_SYMBOL_BYTES = 7089;

const HEX_DIGIT = /^[0-9a-fA-F]$/;
// Separators people paste between bytes: whitespace, and the ':' and '-' of MAC-style and dashed dumps.
const SEPARATOR = /^[\s:-]$/;

/**
 * Reads the content field as hex. Either case is taken, and whitespace, ':' and '-' between digits are skipped.
 * @param {string} text
 * @returns {{ ok: true, hex: string, length: number } | { ok: false, error: string }} the digits in uppercase with nothing between them, and the byte count
 */
export function parseHex(text) {
  let hex = '';
  let position = 0;
  // By code point, so a position counts what the person sees and an emoji is quoted whole.
  for (const ch of text) {
    position++;
    if (SEPARATOR.test(ch)) continue;
    if (!HEX_DIGIT.test(ch)) {
      return { ok: false, error: `${quoteCharacter(ch)} at position ${position} is not a hex digit. Use 0-9 and A-F, with spaces, ':' or '-' between bytes if you like.` };
    }
    hex += ch;
  }
  if (hex.length % 2 !== 0) {
    return { ok: false, error: `The hex has ${hex.length} digits, an odd number. Each byte takes two digits.` };
  }
  return { ok: true, hex: hex.toUpperCase(), length: hex.length / 2 };
}

/** A character for a message: quoted when it prints, and as U+XXXX when it does not. */
function quoteCharacter(ch) {
  const code = ch.codePointAt(0);
  return code < 0x20 || (code >= 0x7f && code < 0xa0)
    ? `U+${code.toString(16).toUpperCase().padStart(4, '0')}`
    : `'${ch}'`;
}

/** @param {string} hex digits with nothing between them, as parseHex returns them */
export function hexToBytes(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  return bytes;
}

/** Bytes as the content field shows them: uppercase pairs, a space between bytes and sixteen a line. */
export function formatHex(bytes) {
  const lines = [];
  for (let i = 0; i < bytes.length; i += 16) {
    lines.push(Array.from(bytes.subarray(i, i + 16), byteHex).join(' '));
  }
  return lines.join('\n');
}

function byteHex(value) {
  return value.toString(16).toUpperCase().padStart(2, '0');
}

/**
 * A hex dump of decoded bytes: the offset, sixteen bytes in two groups of eight, and the bytes as printable ASCII with a dot for the rest.
 * The offset takes four digits, or eight past 64 KiB, which no symbol holds but the format allows.
 * @param {string} hex digits with nothing between them, as the decoder reports them
 */
export function hexDump(hex) {
  const bytes = hexToBytes(hex);
  const offsetWidth = bytes.length > 0x10000 ? 8 : 4;
  const rows = [];
  for (let offset = 0; offset < bytes.length; offset += 16) {
    const row = bytes.subarray(offset, offset + 16);
    const first = Array.from(row.subarray(0, 8), byteHex).join(' ').padEnd(23);
    const second = Array.from(row.subarray(8), byteHex).join(' ').padEnd(23);
    const ascii = Array.from(row, (b) => (b >= 0x20 && b <= 0x7e ? String.fromCharCode(b) : '.')).join('');
    rows.push(`${offset.toString(16).toUpperCase().padStart(offsetWidth, '0')}  ${first}  ${second}  |${ascii}|`);
  }
  return rows.join('\n');
}

/**
 * The ECI assignment field as the state holds it: null when blank, else the number typed, which eciAssignmentError checks.
 * @param {string} text
 */
export function readEciAssignmentField(text) {
  const trimmed = String(text ?? '').trim();
  return trimmed === '' ? null : Number(trimmed);
}

/** Why an assignment cannot be written, or null for none or one in range. */
export function eciAssignmentError(value) {
  if (value === null || value === undefined) return null;
  return Number.isInteger(value) && value >= 0 && value <= MAX_ECI_ASSIGNMENT
    ? null
    : `The ECI assignment must be a whole number from 0 to ${MAX_ECI_ASSIGNMENT}, or blank for none.`;
}

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder('utf-8', { fatal: true });

// Control characters text keeps: tab, line breaks and the GS1 separator. Any other one marks bytes that are not text.
const NOT_TEXT = /[\u0000-\u0008\u000b\u000c\u000e-\u001c\u001e\u001f\u007f]/;

/**
 * The content field after a switch between text and bytes, so neither direction leaves the field holding what the other kind cannot read.
 * Text that is already hex stays as typed, since hex pasted before the switch is what the person means to write, and other text becomes the hex of its UTF-8 bytes.
 * Hex becomes text when its bytes are UTF-8 text, and anything else stays as it is, so a switch never loses what the field holds.
 */
export function convertContent(content, from, to) {
  if (from === to) return content;
  const parsed = parseHex(content);
  if (to === 'bytes') return parsed.ok && parsed.length > 0 ? content : formatHex(utf8Encoder.encode(content));
  if (!parsed.ok) return content;
  let text;
  try {
    text = utf8Decoder.decode(hexToBytes(parsed.hex));
  } catch {
    return content;
  }
  return NOT_TEXT.test(text) ? content : text;
}
