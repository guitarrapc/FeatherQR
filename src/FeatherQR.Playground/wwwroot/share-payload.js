/**
 * Share-link payload codec.
 *
 * Hash segment format: "<mode>.<base64url>"
 *   mode "1", deflate-raw compressed UTF-8 JSON (via native CompressionStream)
 *   mode "0", plain UTF-8 JSON (fallback for browsers without CompressionStream)
 *
 * The JSON payload is the playground state object plus a "v" schema version.
 */

import { SHARE_PAYLOAD_VERSION, validateShareState } from './share-state.js';
export { SHARE_PAYLOAD_VERSION } from './share-state.js';
export const MAX_SHARE_HASH_LENGTH = 16384;
export const MAX_SHARE_URL_LENGTH = 8192;
export const MAX_SHARE_JSON_BYTES = 256 * 1024;

/**
 * @param {object} state playground state (must be JSON-serializable)
 * @returns {Promise<string>} hash segment (no leading #)
 */
export async function encodeShareState(state) {
  const error = validateShareState(state);
  if (error) throw new Error(error);
  const payload = { ...state, v: SHARE_PAYLOAD_VERSION };
  const jsonUtf8 = new TextEncoder().encode(JSON.stringify(payload));
  if (jsonUtf8.length > MAX_SHARE_JSON_BYTES) throw new Error('share payload too large');
  if (typeof CompressionStream === 'function') {
    try {
      const compressed = await pipeThrough(jsonUtf8, new CompressionStream('deflate-raw'));
      return `1.${uint8ToBase64Url(compressed)}`;
    } catch {
      /* fall through to uncompressed */
    }
  }
  return `0.${uint8ToBase64Url(jsonUtf8)}`;
}

/**
 * @param {string} hashSegment hash without the leading #
 * @returns {Promise<{ ok: true, state: object } | { ok: false, error: string }>}
 */
export async function decodeShareHash(hashSegment) {
  if (!hashSegment || !hashSegment.length) {
    return { ok: false, error: 'empty hash' };
  }
  // Reject oversized input before any decode work: the hash is attacker-controlled
  // (arbitrary URL), and base64 + inflate of a huge payload burns CPU/memory on page load.
  if (hashSegment.length > MAX_SHARE_HASH_LENGTH) {
    return { ok: false, error: 'share payload too large' };
  }
  const dot = hashSegment.indexOf('.');
  if (dot !== 1) {
    return { ok: false, error: 'unknown payload format' };
  }
  const mode = hashSegment.slice(0, dot);
  let bytes;
  try {
    bytes = base64UrlToUint8(hashSegment.slice(dot + 1));
  } catch (e) {
    return { ok: false, error: e?.message ?? String(e) };
  }

  if (mode === '1') {
    if (typeof DecompressionStream !== 'function') {
      return { ok: false, error: 'this browser cannot decompress the shared link' };
    }
    try {
      bytes = await pipeThrough(bytes, new DecompressionStream('deflate-raw'), MAX_SHARE_JSON_BYTES);
    } catch (e) {
      return { ok: false, error: `corrupted share payload: ${e?.message ?? e}` };
    }
  } else if (mode !== '0') {
    return { ok: false, error: `unknown payload mode '${mode}'` };
  }
  if (bytes.length > MAX_SHARE_JSON_BYTES) return { ok: false, error: 'share payload too large' };

  let state;
  try {
    state = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch (e) {
    return { ok: false, error: `invalid share JSON: ${e?.message ?? e}` };
  }
  const error = validateShareState(state);
  if (error) return { ok: false, error };
  return { ok: true, state };
}

/**
 * @param {string} hashSegment
 * @param {string} fullUrl
 * @returns {boolean}
 */
export function isShareWithinLimits(hashSegment, fullUrl) {
  return hashSegment.length <= MAX_SHARE_HASH_LENGTH && fullUrl.length <= MAX_SHARE_URL_LENGTH;
}

/**
 * @param {Uint8Array} input
 * @param {CompressionStream | DecompressionStream} transform
 * @param {number} maxBytes maximum output bytes retained
 * @returns {Promise<Uint8Array>}
 */
async function pipeThrough(input, transform, maxBytes = Infinity) {
  const stream = new Blob([input]).stream().pipeThrough(transform);
  const reader = stream.getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.length;
      if (length > maxBytes) {
        await reader.cancel();
        throw new Error('share payload too large');
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const result = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

/** @param {Uint8Array} bytes */
function uint8ToBase64Url(bytes) {
  let binary = '';
  const CHUNK = 0x8000;
  for (let i = 0; i < bytes.length; i += CHUNK) {
    binary += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** @param {string} text */
function base64UrlToUint8(text) {
  const base64 = text.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}
