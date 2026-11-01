/** The v1 shape is shared by compressed and plain links. Missing fields remain optional for older links. */
export const SHARE_PAYLOAD_VERSION = 1;

const record = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const string = (value) => typeof value === 'string';
const boolean = (value) => typeof value === 'boolean';
const color = (value) => string(value) && /^#[0-9a-fA-F]{6}$/.test(value);
const oneOf = (...values) => (value) => values.includes(value);

const fields = {
  content: string,
  symbology: oneOf('qr', 'microqr', 'rmqr'),
  ecc: oneOf('L', 'M', 'Q', 'H', 'EDO'),
  eccBoost: boolean,
  allowKanji: boolean,
  dataFormat: oneOf('text', 'gs1', 'link', 'aim'),
  applicationIndicator: string,
  gs1Stem: string,
  size: Number.isFinite,
  quietZone: Number.isFinite,
  version: Number.isInteger,
  fitStrategy: oneOf('area', 'height', 'width'),
  height: oneOf(0, 7, 9, 11, 13, 15, 17),
  moduleShape: oneOf('rectangle', 'circle', 'rounded'),
  moduleSizePercent: Number.isFinite,
  moduleCornerRadius: Number.isFinite,
  finderShape: oneOf('auto', 'rectangle', 'circle', 'rounded', 'roundedCircle'),
  foreground: color,
  background: (value) => value === 'transparent' || color(value),
  gradient: {
    enabled: boolean,
    colors: (value) => Array.isArray(value) && value.length >= 2 && value.length <= 6 && value.every(color),
    direction: oneOf('LeftToRight', 'RightToLeft', 'TopToBottom', 'BottomToTop',
      'TopLeftToBottomRight', 'TopRightToBottomLeft', 'BottomLeftToTopRight', 'BottomRightToTopLeft'),
  },
  logo: {
    mode: oneOf('none', 'default', 'custom'),
    sizePercent: Number.isFinite,
    borderWidth: Number.isFinite,
  },
};

/** Returns an error before any control is mutated. Unknown fields are ignored for additive compatibility. */
export function validateShareState(state) {
  if (!record(state)) return 'share payload is not an object';
  if (Object.hasOwn(state, 'v') && state.v !== SHARE_PAYLOAD_VERSION) return 'unsupported share schema version';
  const error = validateFields(state, fields, '');
  if (error) return error;
  // A partial link starts from the page's QR controls unless it selects another symbology.
  const symbology = state.symbology ?? 'qr';
  const maximumVersion = symbology === 'microqr' ? 4 : symbology === 'rmqr' ? 32 : 40;
  if (Object.hasOwn(state, 'version') && state.version !== -1 && (state.version < 1 || state.version > maximumVersion)) {
    return "invalid share field 'version'";
  }
  const levels = symbology === 'microqr' ? ['EDO', 'L', 'M', 'Q'] : symbology === 'rmqr' ? ['M', 'H'] : ['L', 'M', 'Q', 'H'];
  if (Object.hasOwn(state, 'ecc') && !levels.includes(state.ecc)) return "invalid share field 'ecc'";
  return null;
}

function validateFields(state, schema, prefix) {
  for (const [key, validator] of Object.entries(schema)) {
    if (!Object.hasOwn(state, key)) continue;
    const path = prefix + key;
    const value = state[key];
    if (typeof validator === 'function') {
      if (!validator(value)) return `invalid share field '${path}'`;
    } else {
      if (!record(value)) return `invalid share field '${path}'`;
      const error = validateFields(value, validator, `${path}.`);
      if (error) return error;
    }
  }
  return null;
}
