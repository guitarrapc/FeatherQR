import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';

const repo = new URL('../../', import.meta.url);
const root = new URL('src/FeatherQR.Playground/wwwroot/', repo);
// A file that is not there yet reads as empty, so its test fails on its own assertion.
const read = (name, base = root) => (existsSync(new URL(name, base)) ? readFileSync(new URL(name, base), 'utf8') : '');
const html = read('index.html');
const siteCss = read('site.css');
const css = siteCss + read('style.css');
const apiHtml = read('api/index.html');
const generator = read('tools/public_api.cs', repo);
const scripts = readdirSync(root).filter(name => name.endsWith('.js')).map(name => [name, read(name)]);

const sample = new URL('samples/BlazorWasm/', repo);
const sampleCss = read('wwwroot/css/app.css', sample);
// A Razor file's markup ends where its code block starts.
const markup = name => read(name, sample).split(/^@code\b/m)[0];
const pages = [
  ['index.html', html],
  ['Home.razor', markup('Pages/Home.razor')],
  ['MainLayout.razor', markup('Layout/MainLayout.razor')],
];

/** The value of one attribute in a tag's text, or null. */
function attribute(tag, name) {
  return new RegExp(`\\s${name}="([^"]*)"`).exec(tag)?.[1] ?? null;
}

/** Text with each line's indentation and the blank lines dropped, so an embedded copy compares whatever its indent. */
function flat(text) {
  return text.split(/\r?\n/).map(line => line.trim()).filter(Boolean).join('\n');
}

/**
 * Each single selector's declarations, a later rule over an earlier one.
 * A rule inside a media query is keyed with the query.
 */
function rules(source) {
  const found = new Map();
  const walk = (block, context) => {
    for (let i = 0; ;) {
      const open = block.indexOf('{', i);
      if (open < 0) return;
      let close = open + 1;
      for (let depth = 1; depth > 0; close++) depth += block[close] === '{' ? 1 : block[close] === '}' ? -1 : 0;
      const head = block.slice(i, open).trim().replace(/\s+/g, ' ');
      const body = block.slice(open + 1, close - 1);
      if (head.startsWith('@media')) walk(body, `${head} `);
      else if (!head.startsWith('@')) {
        for (const selector of head.split(',')) {
          const key = context + selector.trim();
          const declarations = found.get(key) ?? new Map();
          for (const declaration of body.split(';')) {
            const colon = declaration.indexOf(':');
            if (colon > 0) declarations.set(declaration.slice(0, colon).trim(), declaration.slice(colon + 1).trim().replace(/\s+/g, ' '));
          }
          found.set(key, declarations);
        }
      }
      i = close;
    }
  };
  walk(source.replace(/\/\*[\s\S]*?\*\//g, ''), '');
  return found;
}

test('every typed input takes the shared field style', () => {
  // A kind the rule leaves out draws with the browser's own border and background.
  const drawnByOwnRule = new Set(['range', 'color', 'checkbox']);
  const kinds = new Set([...(html + apiHtml).matchAll(/<input\b[^>]*>/g)].map(match => attribute(match[0], 'type')));
  for (const kind of kinds) {
    if (drawnByOwnRule.has(kind)) continue;
    assert.ok(css.includes(`input[type="${kind}"]`), `no rule for input[type="${kind}"]`);
  }
});

for (const [name, page] of pages) {
  test(`${name}: a field carries its tooltip itself, not a part of it`, () => {
    // A tooltip on the control alone or on the text alone shows over only half of the field.
    for (const match of page.matchAll(/<label class="field[^>]*>([\s\S]*?)<\/label>/g)) {
      const inner = /\stitle="([^"]*)"/.exec(match[1]);
      assert.equal(inner, null, `tooltip inside a field: ${inner?.[1].slice(0, 50)}`);
    }
  });

  test(`${name}: a tooltip ends as a sentence`, () => {
    for (const match of page.matchAll(/\stitle="([^"]*)"/g)) {
      assert.match(match[1], /\.$/, `no full stop: ${match[1].slice(0, 50)}`);
    }
  });

  test(`${name}: a link to another site opens in a new tab`, () => {
    for (const match of page.matchAll(/<a\b[^>]*>/g)) {
      if (!/^https?:/.test(attribute(match[0], 'href') ?? '')) continue;
      assert.equal(attribute(match[0], 'target'), '_blank', match[0]);
      assert.equal(attribute(match[0], 'rel'), 'noopener noreferrer', match[0]);
    }
  });

  test(`${name}: a percentage takes no space before its sign`, () => {
    assert.doesNotMatch(page, /<\/output> %/);
  });

  test(`${name}: the page text is in American spelling`, () => {
    assert.doesNotMatch(page, /\b(centre|grey|colour)/i);
  });

  test(`${name}: an ellipsis is one character`, () => {
    assert.doesNotMatch(page.replace(/<script[\s\S]*?<\/script>/g, ''), /\.\.\./);
  });
}

test('a percentage in a script takes no space before its sign', () => {
  for (const [name, source] of scripts) assert.doesNotMatch(source, /\} %`/, name);
});

test('numbers format the same in every browser locale', () => {
  for (const [name, source] of scripts) assert.doesNotMatch(source, /toLocaleString\(\)/, name);
});

test('a theme token is defined in the shared stylesheet alone', () => {
  const tokens = [...(rules(siteCss).get(':root') ?? new Map()).keys()].filter(name => name.startsWith('--'));
  assert.ok(tokens.length > 0, 'site.css defines no tokens');
  for (const token of tokens) {
    assert.ok(!read('style.css').includes(`${token}:`), `style.css defines ${token}`);
    assert.ok(!generator.includes(`${token}:`), `public_api.cs defines ${token}`);
  }
});

// The API page is generated and committed. These fail when a shared part changed and the page was not generated again.
test('the API page carries the shared stylesheet as it stands', () => {
  assert.ok(siteCss.length > 0 && flat(apiHtml).includes(flat(siteCss)));
});

test('the API page carries the theme script as it stands', () => {
  const theme = read('theme.js');
  assert.ok(theme.length > 0 && flat(apiHtml).includes(flat(theme)));
});

test('the API page ends with the footer of the Playground', () => {
  const footer = page => flat(/<footer>[\s\S]*<\/footer>/.exec(page)?.[0] ?? '');
  assert.ok(footer(html).length > 0);
  assert.equal(footer(apiHtml), footer(html));
});

test('the API page names the release as the Playground does', () => {
  // The assembly version has four numbers and no prerelease label, so it names no release.
  const badge = /<a class="version-badge"[^>]*>([^<]*)<\/a>/.exec(apiHtml);
  const version = /^v(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)$/.exec(badge?.[1] ?? '')?.[1];
  assert.ok(version, `badge reads ${badge?.[1]}`);
  assert.equal(attribute(badge[0], 'href'), `https://github.com/guitarrapc/FeatherQR/releases/tag/${version}`);
});

test('a rule the sample shares with the Playground says the same', () => {
  // The sample keeps its own stylesheet so that it can be copied alone. What it shares must not drift.
  const playground = rules(css);
  // The sample has no theme button, so its dark tokens sit on the plain root.
  const twin = selector => selector.replace(/\) :root$/, ') :root:not([data-theme="light"])');
  const shared = [...rules(sampleCss)]
    .map(([selector, declarations]) => [twin(selector), declarations])
    .filter(([selector]) => playground.has(selector));
  assert.ok(shared.length > 20, `only ${shared.length} shared selectors`);
  for (const [selector, declarations] of shared) {
    for (const [property, value] of declarations) {
      assert.equal(value, playground.get(selector).get(property), `${selector} { ${property} }`);
    }
  }
});
