# QR Test Fixtures

Design record for the committed fixture corpus and its generator (`tools/QRInteropFixtures`): what it is, why it exists, and the external-oracle landscape it draws from. The testing strategy it serves is recorded under [Why](#why) below.

---

## What

### Purpose

The corpus is a set of symbols produced by encoders **other than FeatherQR**, committed to the repository so that PR CI can verify this library's decoder against independent implementations without requiring external toolchains at test time. It exists to break the shared-bug blind spot of round-trip-only testing: if our encoder and decoder agree on the same mistake, round-trips still pass, externally generated fixtures do not.

### Layout

```
tests/FeatherQR.Tests/Fixtures/
├── StandardQr/
│   └── zxing-net/              (one directory per generator)
│       ├── case-name.json       manifest
│       ├── case-name.matrix.txt core module matrix, '1' dark / '0' light, row-major, LF, no quiet zone
│       └── case-name.png        clean black-on-white render (quiet zone 4, 8 px/module)
├── StandardQrStructuredAppend/
│   ├── qrcodegenerator/        (same three files per symbol; one set = case-name-{index}of{count})
│   ├── codeglyphx/
│   └── zxing-cpp-samples/      (third-party captures: json + png only, no matrix; PROVENANCE.md + LICENSE beside them)
├── MicroQR/
│   ├── zint-libzint/           (same three files; PNG quiet zone 2 per the Micro QR spec)
│   └── qrtool/
└── RmQr/
    ├── zint-libzint/           (same three files; rectangular matrix / PNG, quiet zone 2 per ISO/IEC 23941)
    └── qrtool/
```

### Manifest schema (case-name.json, camelCase)

| Field | Meaning |
|---|---|
| `id` | Case name (= file stem) |
| `generator`, `generatorVersion` | Producing implementation and its pinned version |
| `symbolType` | `StandardQR`, `MicroQR` or `rMQR` |
| `version`, `width`, `height` | Symbol version and core module dimensions (rMQR: `version` = ISO/IEC 23941 version index + 1 = `RmQRVersion` value = libzint version number; `width` ≠ `height`) |
| `versionName` | Human-readable version where it is not a plain integer (rMQR: `R7x43`); omitted otherwise |
| `errorCorrectionLevel` | `L` / `M` / `Q` / `H` (requested and honored by the generator) |
| `mode` | Data segment mode reported by the generator |
| `maskPattern` | Mask chosen by the generator, `-1` when unknown (Micro QR / rMQR: reader-sourced by the sanity gate; rMQR is always `4`, the single rMQR mask expressed as a Standard QR pattern number) |
| `payloadText`, `payloadUtf8Hex` | Expected decode result (text and UTF-8 bytes) |
| `eciCharset` | `UTF-8` when the generator was asked to emit an ECI segment, else null |
| `quietZoneModules`, `pixelsPerModule` | PNG render parameters (`0` for third-party captures, which are not renders) |
| `structuredAppend` | Present only on a symbol of a Structured Append set: `setId` (the case name), `index` (0-based, as on the wire), `count` (1..16), `parity` (0..255). Every value is what the zxing-cpp reader reports for the symbol, never what the encoder was asked for |

The loader (`FixtureLoader` in the test project) mirrors this schema; the two must change together.

`mode` is `Unknown` where no generator self-reports it: the Structured Append lineages and the third-party captures. The one exception is `Kanji`, which the gate sets when the bytes the set carries are the case text in Shift_JIS and no ECI header is present. A Byte segment carrying raw Shift_JIS bytes without an ECI would look the same to the reader, which exposes segment bytes and not modes; no Structured Append lineage writes one, and the gate refuses the other unnameable case, an ECI header over ASCII-only bytes, rather than guess which ECI it was.

### Corpus contents (Standard QR, 26 cases)

Every mode (Numeric / Alphanumeric / Byte) × every ECC level at small versions, plus: version 1-L alphanumeric capacity boundary (25 chars), mid (v10/v15/v25), maximum (v40-L at exactly 7089 digits), full alphanumeric charset, and UTF-8/ECI payloads (Japanese, emoji). Since the Kanji decode work there are also 5 Kanji-mode cases (ISO/IEC 18004 8.4.5): ZXing.Net emits them when the requested charset is Shift_JIS and the payload is entirely JIS X 0208 double-byte, and its encoder self-reports `Mode.KANJI`, so the manifest mode is externally sourced rather than assumed. Because ZXing.Net encodes through .NET CP932, these payloads deliberately avoid the seven cells where CP932 and JIS X 0208 disagree; those live in the qrtool rMQR lineage, which takes raw Shift_JIS bytes. All payloads are fixed literals or fixed repetitions, no randomness or timestamps, so regeneration is byte-reproducible for a given generator version.

### Corpus contents (Standard QR Structured Append, 7 sets × 2 lineages + 1 third-party set)

Seven sets from two in-process encoder lineages, 95 symbols: every mode (numeric, alphanumeric, byte), ISO-8859-1 and UTF-8 text, the smallest set (2, at version 1) and the largest (16, at version 2), and set sizes in between at versions 3 to 9. The `qrcodegenerator` lineage is a balancing encoder that picks the count and a shared version itself and boosts ECC per symbol (the version-1 pair comes out Q from a requested L; the sixteen-set mixes L and M); the `codeglyphx` lineage takes the text pre-split into equal character runs and versions each symbol on its own. Both lineages write the same wire header for the same input, so their ASCII sets agree on the parity byte (51, 105, 27, 22, 43). The two texts with non-ASCII characters are the discriminating cases: the same Latin-1 sentence is carried as ISO-8859-1 without ECI by one lineage (parity 139) and as UTF-8 with ECI by the other (parity 8), and the same Japanese sentence as UTF-8 with ECI (parity 6) and as Kanji mode with no ECI (parity 176). A decoder has to report whichever is on the wire, and a fixture set whose parity was computed over the wrong bytes would fail the gate, which is why every repetition count in the corpus is odd (an even repetition XORs to 0 in every charset) and the Latin-1 sentence carries an odd number of accented characters (with an even number the ISO-8859-1 and UTF-8 parities coincide).

The third-party set (`zxing-cpp-samples/sample01`, 4 captures, version 15-L, parity 95, `PROVENANCE.md` and the Apache-2.0 `LICENSE` beside the files) is the one set this repository's tooling did not produce: grayscale photographs of about 267 px with no module matrix, so they exercise the image path only, and every manifest field is reader-sourced. On import this library's image decoder reached `UnsupportedContent` on all four, so detection and error correction were already sound and only the mode indicator stood in the way.

The corpus also surfaced one image-path defect unrelated to Structured Append: `codeglyphx/sixteen-symbols-max-v2-l-2of16` reads through the matrix path but not from its PNG, because the finder locator picks a false candidate three modules inside the real top-right finder at 5 px/module and up (follow-up F10 in the 2.0.0 plan, with the numbers). The fixture stays; `StructuredAppendFixtureTest` lists it as a known image-path defect with a guard test that fails once the locator is fixed.

**Sanity gate** (`StructuredAppendSanityGate`): every symbol is rendered and read with the pinned zxing-cpp reader before it is written; index and count must match the encoder's claim, version, ECC and mask the manifest, and the parity must equal the XOR of the bytes the set actually carries (the reader's raw `Bytes`, concatenated in index order), which must themselves be one known encoding of the case text. The per-symbol text is taken from the reader where the encoder does not expose it, and the texts concatenated in index order must equal the case text. Fourteen sets passed on first generation once the `Net.Codecrete.QrCodeGenerator` pin moved from 3.1.0 to 3.2.1: 3.1.0 wrote parity 0 on every set, the defect its 3.2.0 fixed, and the gate is what caught it.

### Corpus contents (Micro QR, 23 cases × 2 lineages)

Every version × legal ECC combination (M1 detection-only, M2/M3 L+M, M4 L+M+Q), every supported mode, capacity-boundary payloads (padding-free) plus short payloads (terminator + pad paths), and a UTF-8 byte-mode case (qrtool lineage only, libzint rejects UTF-8 input). Both lineages share the case list (`MicroQRCorpus`): 17 fixtures from zint-libzint, 23 from qrtool. The 5 Kanji cases (M3 and M4 only, the versions whose mode indicator is wide enough) are qrtool-only, libzint emits Byte mode for the same input. Version and ECC are pinned per case; the mask pattern in the manifest comes from the zxing-cpp READER during the sanity gate, so it is externally sourced.

**Sanity gate**: every generated Micro QR fixture is rendered and decoded with the pinned zxing-cpp reader before it is written, payload, version and ECC level must match the manifest, so a broken generator cannot poison the committed corpus.

### Corpus contents (rMQR, 108 zint-libzint + 40 qrtool cases)

Built in implementation-plan Phase 5.1a, BEFORE the tables (its symbols are the oracle for the table tests). The two lineages carry different case lists (`RmQRCorpus`) on purpose:

- **zint-libzint** (ASCII-only): the systematic sweep, every one of the 32 versions × {Numeric `1`, Alphanumeric `A`, Byte `a`} single-character cases (96; ECC alternates so every version appears at both M and H; the leading data codewords pin the count-indicator width per version × mode), plus per-height capacity boundaries (numeric max at M on the widest width, byte max at H on the narrowest, 12).
- **qrtool**: one capacity-boundary case per version with rotating mode / ECC (32) plus UTF-8 / Japanese byte-mode cases (4; libzint rejects non-ASCII) and Kanji-mode cases (4; libzint cannot emit Kanji at all). One Kanji case, `r15x59-m-kanji-jisx0208-divergent`, carries exactly the seven Shift_JIS cells where JIS X 0208 and CP932 disagree, with its bytes pinned on the case because .NET cannot encode the JIS X 0208 readings; it is the fixture that fails if the decode table is ever rebuilt from CP932. No ECI header in either lineage.

Payloads are fixed literals or fixed cyclic patterns; the tool keeps its own copy of the version / capacity table (`RmQRVersionTable`, values from [rMQR Encoder](rmqr-encoder.md)) so it never depends on the library it produces oracles for.

**Sanity gate** (`RmQRSanityGate`): every fixture is rendered and decoded with the pinned zxing-cpp reader before it is written; the payload is compared on raw bytes (`Bytes` vs `payloadUtf8Hex`, because UTF-8 without ECI is exposed with a legacy-charset `Text` guess), `Extra("Version")` must equal `R{H}x{W}` and `Extra("EcLevel")` the manifest ECC; the reader's `DataMask` (4) is recorded. Kanji fixtures are the exception to the raw-byte comparison: a Kanji segment puts the Shift_JIS bytes in `Bytes`, so the gate compares `Text` instead, which is the reader's JIS X 0208 reading and exactly what this library's decoder has to reproduce. All 144 cases passed on first generation (the corpus has since grown to 148 with the Kanji cases).

### Consuming tests

`StandardQrFixtureTest` decodes every fixture twice, matrix path (`TryDecode(modules, size, …)`) and image path (`TryDecode(SKBitmap, …)`), asserting payload, version, ECC level, and (matrix path) the generator's mask pattern. `MicroQRFixtureTest` likewise exercises both the matrix path and the PNG image path (`MicroQRCodeDecoder.TryDecode(SKBitmap, …)`, Phase 4b) and additionally asserts zero corrected errors and (matrix path) the reader-sourced mask pattern. `RmQrFixtureTest` (Phase 5.1a scaffolding) pins the rMQR corpus shape: both lineages, every version in both lineages and at both ECC levels, one single-character libzint case per version × mode, manifest ↔ matrix ↔ PNG consistency and table-independent structural invariants (finder / sub-finder corners, edge timing rows); since Phase 6.4 it also decodes every fixture through the public matrix path (payload, version, ECC; `ErrorsCorrected` 0 for libzint, ≤ 1 for the qrtool tail defect) and re-decodes the module centers sampled from the PNG; since Phase 7 it also decodes every PNG through the public image path (`RmQRCodeDecoder.TryDecode(SKBitmap, …)`, `Decode_PngFixture_*`) with the same payload / version / ECC / corrections expectations. `KanjiFixtureTest` states the Kanji guarantee once, across all three symbologies: it decodes every Kanji fixture through both the matrix and the image path, asserts a minimum Kanji case count per symbology so a regeneration cannot silently drop them, and pins the divergent-cell fixture to its JIS X 0208 readings.

`StructuredAppendFixtureTest` decodes every Structured Append symbol through the matrix path (where a matrix exists) and the image path, asserting its own text, version, ECC, mask, zero corrected codewords (matrix path, with the flipped-module negative control below) and the three header values; decodes every matrix fixture into a one-character destination and requires the header to be absent from that failed result, which is the "reported on success only" rule at the point it is enforced; pins the reassembly rules on the manifests themselves (one count and one parity per set, indices 0..count-1 each present once); and, like `KanjiFixtureTest`, pins the corpus shapes a regeneration could silently drop: both encoder lineages and the captures, a 2-set and a 16-set, a Kanji-mode set, and a different parity per lineage for the two charset-divergent texts.

Each fixture class also carries a **negative control** for its clean-decode corrections assertion: the same fixture with one data module flipped must decode with exactly one corrected codeword. Without the control, a dead `ErrorsCorrected` counter (always 0) would leave every zero-corrections assertion green while proving nothing; the control fails in that world. The flipped module is the first codeword module in placement order (the bottom-right corner for Standard and Micro QR; for rMQR, found by the placement zigzag over the naive function-module map). Micro QR excludes M1 (detection-only, rejects any data damage) and rMQR runs the control on the libzint lineage only (qrtool has no zero-corrections baseline due to its tail defect).

### Regeneration

```bash
# qrtool binary (pinned version + SHA-256), one-time per machine:
pwsh tools/QRInteropFixtures/get-qrtool.ps1

dotnet run --project tools/QRInteropFixtures -- regenerate

# Structured Append sets only (also part of regenerate):
dotnet run --project tools/QRInteropFixtures -- regenerate-structured-append

# Third-party captures, never regenerated: re-import from a zxing-cpp checkout, recording its commit
dotnet run --project tools/QRInteropFixtures -- import-structured-append-samples <zxing-cpp-root> <commit>
```

The same tool carries the oracle probes, which are run by hand and whose findings are recorded in the matrix below rather than asserted in CI (they need the native oracle on the machine): `spot-check-microqr`, `spot-check-rmqr`, `probe-creator`, `probe-rmqr`, `probe-rmqr-capacity`, `probe-kanji` (which lineage can emit Kanji mode, per symbology), `probe-kanji-sweep` (every Kanji-mode cell through qrtool + zxing-cpp, diffed against CP932; writes `kanji-sweep.tsv`) and `generate-kanji-table` (emits the library's `ShiftJisKanjiTable` from that sweep, refusing unless the data satisfies the JIS X 0208 published totals and its CP932 delta is exactly the seven documented cells). `kanji-sweep.tsv` is committed alongside the tool: it is the provenance for a generated table that ships in `src/`, it carries the CP932 column that the divergence claim rests on, and committing it means the table can be regenerated and reviewed without the native oracles on hand.

The tool wipes and rewrites each available generator's directory. Fixture updates must be committed as an explicit, reviewed change, a generator-version bump that silently alters fixtures is exactly what the corpus is meant to catch.

## Oracle capability matrix

Status meaning, **verified**: exercised in this repository; **documented**: capability confirmed from project documentation, not yet run here.

| Oracle | Standard QR | Micro QR | rMQR | Status | Notes |
|---|---|---|---|---|---|
| ZXing.Net 0.16.11 (NuGet, pinned) | encode + decode | - | - | verified | Fixture generator + `QRCodeDecoderZXingCrossTest`; in-process, no toolchain |
| Net.Codecrete.QrCodeGenerator 3.2.1 (NuGet, pinned) | encode, Structured Append | - | - | verified | Balancing Structured Append encoder (`EncodeTextInMultipleBalancedCodes`), the `qrcodegenerator` fixture lineage; in-process. Decides the charset once for the whole text (ISO-8859-1 when lossless, else UTF-8 with an ECI in every symbol) and XORs those bytes once, which is the parity definition this library follows. **3.1.0 wrote parity 0 on every set**, fixed in 3.2.0; do not pin below it |
| CodeGlyphX 2.1.0 (NuGet, pinned) | encode + decode, Structured Append | - | - | verified | Explicit-parts Structured Append encoder (`EncodeStructuredAppend`), the `codeglyphx` fixture lineage; in-process. Versions each part on its own, emits Kanji mode for JIS X 0208 text with no ECI, and writes one parity per set, the XOR of the whole text's bytes as it encodes them (Shift_JIS for a Kanji set, UTF-8 with an ECI for Latin-1 text), so the same text carries a different parity here than in the balancing lineage (176 against 6 for the Japanese set, 8 against 139 for the Latin-1 one); the gate checks both against the bytes actually carried |
| zxing-cpp reader, Structured Append | decode | - | - | verified | `SequenceIndex` (0-based), `SequenceSize`, `SequenceId` (the parity as a decimal string); `Bytes` holds the raw segment bytes (Shift_JIS for a Kanji segment), which is what the Structured Append gate XORs. Its writer cannot produce Structured Append: the bundled libzint has no `structapp` |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) (via [ZXingCpp](https://www.nuget.org/packages/ZXingCpp) 0.5.2, pinned) | read + write | read | read | verified | Micro QR reading exercised by `tools/QRInteropFixtures -- spot-check-microqr` against this library's encoder (all versions × ECC, UTF-8) and as the fixture sanity gate. Its reader exposes `Extra("Version"/"EcLevel"/"DataMask")`, which supplies externally-sourced metadata for the Micro QR manifests (note: reports M1's implicit level as "L"). The official .NET wrapper bundles native binaries, so no external toolchain is needed. rMQR (`probe-rmqr`, 2026-08-15): the reader reports `Extra("Version")` as `"R7x43"`…`"R17x139"` (same spelling as the planned `RmQRVersion` members), `Extra("EcLevel")` as `"M"`/`"H"`, and `Extra("DataMask")` as `"4"` (rMQR's single mask is Standard QR mask pattern 4), verified on all 64 libzint version × ECC symbols and on qrtool rMQR output. Byte-mode UTF-8 content without ECI (qrtool Japanese payload) is exposed with `Text` decoded as a legacy charset guess (mojibake) while `Bytes` holds the exact UTF-8 bytes and `HasECI = false`; the wrapper offers no charset hint, so the rMQR sanity gate must compare `Bytes` against the manifest's `payloadUtf8Hex`, not `Text` |
| [Zint](https://zint.org.uk/) (libzint via ZXingCpp `BarcodeCreator`) | encode | encode | encode | verified | zxing-cpp's writer is libzint compiled into the same pinned native binary; Micro QR encoding exercised as a fixture lineage (`Options = "version=N,ecLevel=X"` honored; `ToImage(Scale=1, AddQuietZones=false)` is module-exact). Limits found: rejects UTF-8 Micro QR input ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics round-tripped transliterated, keep zint-lineage payloads ASCII. As an ENCODER lineage this counts as zint, independent of both this library and the Rust crates. rMQR (`probe-rmqr`, 2026-08-15): `version=1..32` maps to R7x43…R17x139 in height-major order (= the ISO version index + 1, 32/32 verified by output dimensions), `version=33..38` = fixed height 7/9/11/13/15/17 with automatic width, `ecLevel=M|H` honored; all 64 version × ECC symbols round-trip through the zxing-cpp reader |
| [qrcode2 / qrtool (Rust)](https://docs.rs/qrcode2) | encode | encode | encode | verified | `qrtool` 0.13.2 prebuilt binary pinned by version + SHA-256 (`get-qrtool.ps1`); Micro QR encoding exercised as a fixture lineage (all versions × ECC × modes incl. UTF-8, `--variant micro` with pinned `--symbol-version`/`--error-correction-level`/`--mode`). The `--type ascii` output is module-exact, so no image parsing is involved. M1's detection-only level is requested as `l` (the qrcode crate models it as L). rMQR encoding verified 2026-08-15 (`--variant rmqr -v <H> <W>`, all 32 versions × M/H, `--mode` honored): dimensions, capacities, format information and leading bit streams agree with the parameter tables in [rMQR Encoder](rmqr-encoder.md); qrtool does have a Kanji mode (see the Kanji row below; the earlier reading of this was wrong); its automatic version choice with no `-v` is fewest-modules (`probe-rmqr`: 12 digits → R11x27, 15 → R13x27, 100 → R11x77), the same as libzint and this library's default. **Known defect (found in rMQR Phase 5.4):** on versions 11 modules high or taller, qrtool 0.13.2 never writes the last h − 10 modules of the placement walk (column 1, rows 8..h−3), so the final ECC codeword of the last block loses its lowest (h − 10 − remainder) bits; zxing-cpp still reads every such symbol (one codeword error, corrected). Data codewords, all other ECC codewords, format copies and remainder bits match the spec-derived encoding exactly, and libzint symbols match byte for byte. Encoder-side oracle tests tolerate exactly this defect for the qrtool lineage; decoder tests must expect `ErrorsCorrected` = 1 (not 0) on the affected qrtool fixtures (R11x59, R13x43, R13x139, R15x43, R15x59, R15x139, R17x43, R17x59, R17x77, R17x99, R17x139 cases) |
| rmqrcode-python | - | - | encode | claimed | Capability not independently confirmed yet, verify before relying on it |
| Kanji mode, per lineage (`probe-kanji`, 2026-08-23) | zxing-net + qrtool | qrtool | qrtool | verified | ZXing.Net emits Kanji when the requested charset is Shift_JIS and every character is JIS X 0208 double-byte; its encoder self-reports `Mode.KANJI`, so the manifest mode needs no reader. .NET resolves Shift_JIS only after `CodePagesEncodingProvider` is registered, and ZXing.Net caches the lookup in static initializers, so the tool registers it before any encoding. qrtool has `--mode kanji` for **all three** variants (correcting the earlier claim below) and requires raw Shift_JIS bytes via `--read-from`; UTF-8 input fails with "invalid character", and it correctly refuses `--mode kanji` for M1/M2. libzint cannot produce Kanji at all: `From(string)` rejects every non-ASCII input ("Error 245: Invalid UTF-8 in input", a wrapper marshalling defect), and the previously unknown `From(byte[])` overload works but makes libzint emit **Byte mode with ECI 20**, not Kanji, confirmed by symbol size (46 kanji → R17x99 / v5-L, one version above the Kanji-mode fit). On the read side, zxing-cpp applies JIS X 0208 and exposes a Kanji segment's `Bytes` as the **raw Shift_JIS bytes**, not UTF-8, so the sanity gate compares against Shift_JIS for these fixtures rather than against `payloadUtf8Hex` |
| BoofCV (Java) | decode | decode | - | claimed | Candidate additional decode oracle; not evaluated |

Independence caveat: ZXing.Net and zxing-cpp descend from the same ZXing lineage, count them as one independent implementation family, not two. Zint and the Rust crates are separate lineages. Note that zxing-cpp's READER and the libzint WRITER ship in one native binary but are algorithmically independent codebases; a created-then-read round-trip within that binary still exercises two lineages.

rMQR correction capacity (`probe-rmqr-capacity`, 2026-08-21): ISO/IEC 23941 Table 8's capacity column is paywalled, so whether rMQR reserves misdecode-protection codewords `p` the way ISO/IEC 18004 Table 9 does for Micro QR was measured against zxing-cpp instead. For each of the 64 version × ECC combinations the probe damages a symbol one module at a time, keeping only flips this library's decoder reports as exactly one more corrected codeword — so the damage saturates at our capacity in every Reed-Solomon block without consulting a block-structure table — and then checks zxing-cpp in both directions: it must still decode at saturation (else it stops below us, i.e. a reserved `p` we ignore) and must not decode one error past it (else it reaches further, i.e. our capacity is too low). All 64 agree, and every saturation count equals `blocks × ⌊ecc per block / 2⌋` exactly, so zxing-cpp corrects rMQR at full Reed-Solomon strength with no reserved `p`. This is evidence from the reference implementation, not a reading of the standard; the Correction cap decision in [rMQR Decoder](rmqr-decoder.md) stays open until someone reads Table 8.

Oracle scarcity, decode direction: zxing-cpp is the only maintained OSS decoder for Micro QR and rMQR (ZXing Java/.NET, rqrr (Rust) and gozxing (Go) do not read them; BoofCV (Java) reads Micro QR only). Encoder verification therefore rests on one external decode lineage plus specification-derived vectors and the in-repo extraction tests, this is a structural limit, not a tooling gap. The decode direction has no such limit: multiple independent encoder lineages (zint, Rust qrcode2) generate the fixture corpus that exercises our decoder.

Toolchain policy: oracles must be pinned and acquirable without fragile environment-dependent builds, NuGet packages and prebuilt static binaries qualify; building C++/Python toolchains on dev machines or CI does not. Rust tools qualify via prebuilt release binaries. Under this policy the fixture generators for Phase 3 are libzint (via the pinned ZXingCpp package) and qrtool (prebuilt binary, pinned by version + checksum); Docker-pinned builds remain a fallback.

## Why

### Why the corpus sits where it does among the test layers

The corpus is one layer of four, and each exists because a specific failure mode is invisible to the others.

- **Module-matrix conformance** is the primary oracle: fixed matrices with pinned masks, compared before any image is involved. It is the only layer that can fail when the encoder and the decoder agree on the same wrong interpretation, which no round-trip test can detect by construction.
- **This corpus** covers external symbol → our decoder. Its counterpart, our encoder → external decoder, is a separate direction and is tested separately, because a single round trip through one external tool proves neither half on its own.
- **Image degradation** tests exercise detection and sampling deterministically (rotation, blur, noise, perspective), against symbols whose matrix is already known-good from the layers above.
- **Physical scanners** are acceptance, never proof. A phone that fails to read a spec-conformant symbol is an interoperability fact, not a specification violation, so it can inform a decision but must never gate one. This is why no physical test suite exists; see the scope table in [qrcode-symbologies.md](qrcode-symbologies.md#scope-decisions).

Two structural rules follow. **Sampling and payload decoding need a testable boundary between them** — the matrix-level decoder entry points exist partly so an image failure can be localized to "sampled the wrong matrix" or "decoded the right matrix wrongly" instead of being one opaque false. And **the coverage cross-product has to be cut deliberately**: version × mode × ECC × payload × degradation is unbounded, so matrix-level conformance is exhaustive while payload and degradation coverage is boundary-focused, with anything larger left to manual runs rather than pull-request CI.

### Why ZXing.Net is the first generator

It is already a pinned test dependency, runs in-process, and needs zero external toolchain, so the harness (manifest schema, loader, writer, tests, CI wiring) could be built and proven against the shipped Standard QR implementation immediately. The plug-in generator interface (`IFixtureGenerator`) exists precisely so Zint / qrtool / zxing-cpp can be added for Micro QR and rMQR without touching the harness.

### Why fixtures assert the decode direction only

Two conformant encoders may legitimately produce different final matrices: mask selection and mode segmentation are implementation choices, not normative outputs. Matrix equality against an external fixture is therefore **not** a valid encoder-conformance test. The corpus verifies our *decoder* against external symbols; our *encoder* is verified by external decoders (ZXing.Net cross tests today, zxing-cpp in interop CI later) and, for Micro QR / rMQR, by spec-derived matrix tests where the mask is pinned by the manifest.

### Why fixtures are committed rather than generated in CI

PR CI stays self-contained and deterministic (no Rust/C++/Python toolchains), and fixture drift becomes a reviewable diff instead of a silent dependency-upgrade side effect.

## Lessons learned

- ZXing.Net's `ZXing.QrCode.Internal.Encoder` (rather than the public `BarcodeWriter`) returns the core `ByteMatrix` plus version, mode, and **mask pattern**, the mask metadata lets fixture tests assert that our format-information decode reproduces the generator's mask choice exactly, a much stronger check than payload equality alone.
- For Micro QR the equivalent mask metadata comes from the zxing-cpp READER during the sanity gate (`Extra("DataMask")`), not from the encoders, neither libzint (through the wrapper) nor qrtool reports its mask choice. Reader-sourced metadata is equally external and additionally proves the value is on the wire.
- qrtool's `--type ascii` output (two characters per module) makes the fixture matrix extraction exact and image-free, but trailing light modules are trimmed from each line, pad when parsing. `--mode` requires an explicit `--symbol-version`.
- Regenerating fixtures on Windows rewrites working-copy files with LF endings; when content is unchanged the only diff is EOL churn (`git diff --ignore-cr-at-eol` is empty), restore rather than commit such no-op rewrites.
- The public `QRCodeWriter.encode` path scales and pads to a requested pixel size; extracting the core matrix from it is lossy. Generating from the internal encoder and rendering PNGs ourselves keeps the matrix and the image pixel-exact for a known quiet zone and module size.
- Requested ECC is honored (never downgraded) by ZXing's encoder, so the manifest can record the requested level as the expected decode result without reading it back from the symbol.
- A committed external symbol is not automatically a byte-exact oracle: qrtool's rMQR output carries a systematic tail defect (last h − 10 placement modules never written) that zxing-cpp corrects silently, so the sanity gate (payload + metadata equality) passed 36/36 while the encoder-side final-message comparison found 12 mismatches. Where the reader can correct, cross-lineage agreement (libzint byte-exact, qrtool differing only in the last codeword's low bits, both consistent with the ISO codeword counts that qrtool's layout could not even hold) is what arbitrates; the corpus keeps the qrtool symbols as real-world "one corrupted ECC codeword" decoder cases with the defect documented, rather than dropping them.
