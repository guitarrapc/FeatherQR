# QR Test Fixtures

This design record covers what the committed fixture corpus is, why it exists, its generator (`tools/QRInteropFixtures`) and the external oracles it draws from. [Why](#why) covers the testing strategy it serves.

---

## What

### Purpose

The corpus holds symbols from encoders other than FeatherQR. It is committed so PR CI can check this library's decoder against independent implementations with no external toolchain at test time.

It exists because round-trip tests alone cannot catch a bug the encoder and decoder share, and fixtures from other encoders can.

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
├── RmQr/
│   ├── zint-libzint/           (same three files; rectangular matrix / PNG, quiet zone 2 per ISO/IEC 23941)
│   └── qrtool/
└── RealImages/
    └── zxing-cpp-samples/      (third-party photographs, scans and renders: image + .txt with its text, one directory per sample set; PROVENANCE.md + LICENSE beside them; measured by the image decode sweep, a few images pinned by the tests that read them)
```

### Manifest schema (case-name.json, camelCase)

| Field | Meaning |
|---|---|
| `id` | Case name (= file stem) |
| `generator`, `generatorVersion` | Producing implementation and its pinned version |
| `symbolType` | `StandardQR`, `MicroQR` or `rMQR` |
| `version`, `width`, `height` | Symbol version and core module dimensions. For rMQR, `version` is the ISO/IEC 23941 version index + 1 (the `RmQRVersion` value and the libzint version number), and `width` ≠ `height` |
| `versionName` | Human-readable version, present only where the version is not a plain integer (rMQR: `R7x43`) |
| `errorCorrectionLevel` | `L` / `M` / `Q` / `H`, as requested (the generator honors the request) |
| `mode` | Data segment mode reported by the generator |
| `maskPattern` | Generator's mask, or `-1` when unknown. For Micro QR and rMQR the sanity gate takes it from the reader. rMQR is always `4`, its single mask as a Standard QR pattern number |
| `payloadText`, `payloadUtf8Hex` | Expected decode result (text and UTF-8 bytes) |
| `eciCharset` | `UTF-8` when the generator was asked for an ECI segment, else null |
| `quietZoneModules`, `pixelsPerModule` | PNG render parameters (`0` for third-party captures, which are not renders) |
| `structuredAppend` | Only on Structured Append symbols: `setId` (the case name), `index` (0-based, as on the wire), `count` (1..16) and `parity` (0..255), all as the zxing-cpp reader reports them, not as the encoder was asked |

The loader (`FixtureLoader` in the test project) mirrors this schema, so the two must change together.

`mode` is `Unknown` where no generator reports it (the Structured Append lineages and the third-party captures), except `Kanji`, which the gate sets when the set's bytes are the case text in Shift_JIS with no ECI header.

The reader exposes segment bytes, not modes, so a Byte segment of raw Shift_JIS bytes without an ECI would look the same. No Structured Append lineage writes one. The gate refuses the other case it cannot name, an ECI header over ASCII-only bytes, rather than guess the ECI.

### Corpus contents (Standard QR, 28 cases)

The cases cover every mode (Numeric / Alphanumeric / Byte) × ECC level at small versions, the version 1-L alphanumeric capacity boundary (25 chars), mid versions (v10/v15/v25), the maximum (v40-L, 7089 digits), the full alphanumeric charset, and UTF-8/ECI payloads (Japanese, emoji).

7 Kanji-mode cases (ISO/IEC 18004 8.4.5) have been added since the Kanji decode work. Two, added when Kanji encoding was built, cover the 12-bit count band (versions 27-40): one character past 26-M (653 at 27-M) and 40-L filled (1,817), both cycling through cells including the edges of both Shift_JIS ranges (0x8140, 0x9FFC, 0xE040, 0xEAA4).

ZXing.Net emits Kanji mode when the requested charset is Shift_JIS and the payload is all JIS X 0208 double-byte, and reports `Mode.KANJI` itself, so the manifest mode is the generator's own report. It encodes through .NET CP932, so these payloads avoid the seven cells where CP932 and JIS X 0208 disagree. Those cells are in the qrtool rMQR lineage, which takes raw Shift_JIS bytes.

Payloads are fixed literals or repetitions with no randomness or timestamps, so regeneration is byte-reproducible for a given generator version.

### Corpus contents (Standard QR Structured Append, 7 sets × 2 lineages + 1 third-party set)

Two in-process encoder lineages write seven sets each (95 symbols in all), covering every mode (numeric, alphanumeric, byte), ISO-8859-1 and UTF-8 text, the smallest set (2, at version 1), the largest (16, at version 2), and sizes between at versions 3 to 9. They build sets differently:

- `qrcodegenerator` is a balancing encoder: it picks the count and a shared version itself and boosts ECC per symbol (the version-1 pair comes out Q from a requested L, and the sixteen-set mixes L and M).
- `codeglyphx` takes the text pre-split into equal character runs and picks each symbol's version independently.

Both write the same wire header for the same input, so their ASCII sets agree on the parity byte (51, 105, 27, 22, 43).

The two non-ASCII texts tell the lineages apart. The Latin-1 sentence is ISO-8859-1 without ECI in one (parity 139) and UTF-8 with ECI in the other (parity 8). The Japanese sentence is UTF-8 with ECI (parity 6) and Kanji mode with no ECI (parity 176).

A decoder must report whichever is on the wire, and a parity computed over the wrong bytes fails the gate. So every repetition count is odd (an even one XORs to 0 in every charset), and the Latin-1 sentence has an odd number of accented characters (an even number makes the ISO-8859-1 and UTF-8 parities coincide).

The third-party set (`zxing-cpp-samples/sample01`: 4 captures, version 15-L, parity 95, with `PROVENANCE.md` and the Apache-2.0 `LICENSE` beside the files) is the only set not made by this repository's tooling. Its grayscale photographs of about 267 px have no module matrix, so they exercise only the image path and every manifest field comes from the reader.

On import, this library's image decoder ended with `UnsupportedContent` on all four: detection and error correction worked, and only the mode indicator was unsupported.

The corpus also found an image-path defect unrelated to Structured Append: `codeglyphx/sixteen-symbols-max-v2-l-2of16` read from its matrix but not its PNG. The alignment pattern's ring and two data modules form a false finder that the row scan confirms at 5 px/module and up, and finder selection then scored module size alone, equal for every candidate, so it chose the false finder over the real bottom-left one. The fix (2026-09-19) selects the finder triple by shape (see the decoder spec's image detection lessons). The fixture now reads, and `FinderPatternSelectionTest` renders it at 3 to 16 px/module.

Before writing a symbol, the sanity gate (`StructuredAppendSanityGate`) reads its render with the pinned zxing-cpp reader and checks that:

- index and count match the encoder's claim
- version, ECC and mask match the manifest
- the parity is the XOR of the bytes the set carries (the reader's raw `Bytes`, concatenated in index order), and those bytes are a known encoding of the case text
- the per-symbol texts, concatenated in index order, equal the case text (from the reader where the encoder does not expose a symbol's text).

Fourteen sets passed on first generation once the `Net.Codecrete.QrCodeGenerator` pin moved from 3.1.0 to 3.2.1. The gate had caught 3.1.0 writing parity 0 on every set, fixed in 3.2.0.

### Corpus contents (Micro QR, 17 zint-libzint + 23 qrtool cases)

The cases cover every version × legal ECC combination (M1 detection-only, M2/M3 L+M, M4 L+M+Q) and every supported mode, with capacity-boundary payloads (padding-free), short payloads (terminator + pad paths), and a UTF-8 byte-mode case, qrtool-only because libzint rejects UTF-8 input.

Both lineages share the case list (`MicroQRCorpus`): 17 fixtures from zint-libzint and 23 from qrtool. The 5 Kanji cases are qrtool-only, because libzint emits Byte mode for the same input, and at M3 and M4 only, whose mode indicator is wide enough.

Version and ECC are pinned per case. The mask pattern comes from the zxing-cpp reader during the sanity gate, so it is also external.

Before writing a fixture, the sanity gate decodes its render with the pinned zxing-cpp reader. Payload, version and ECC level must match the manifest, so a broken generator cannot corrupt the committed corpus.

### Corpus contents (rMQR, 108 zint-libzint + 44 qrtool cases)

The rMQR corpus was built when the rMQR encoder was written (2026-08-15), before its tables, because its symbols are the table tests' oracle. The two lineages carry different case lists (`RmQRCorpus`):

- zint-libzint (ASCII-only) is the systematic sweep: a single-character case for each of the 32 versions × {Numeric `1`, Alphanumeric `A`, Byte `a`} (96), ECC alternating so every version appears at M and H, with leading data codewords showing each version × mode's count-indicator width. Per-height capacity boundaries add 12: numeric max at M on the widest width, byte max at H on the narrowest.
- qrtool has one capacity-boundary case per version with rotating mode / ECC (32), UTF-8 / Japanese byte-mode cases (4, because libzint rejects non-ASCII) and Kanji-mode cases (8, because libzint cannot emit Kanji). Four Kanji cases added when Kanji encoding was built fill a version at each count width the first four left to derivation: 2 bits (R7x43-M, 3 characters), 3 (R7x59-M with 6 and R9x43-H with 3) and 6 (R9x139-M, 38), all under 11 modules high and so clear of qrtool's tail defect. One Kanji case, `r15x59-m-kanji-jisx0208-divergent`, carries exactly the seven Shift_JIS cells where JIS X 0208 and CP932 disagree, with bytes pinned on the case because .NET cannot encode the JIS X 0208 readings. It fails if the decode table is ever rebuilt from CP932.

Neither lineage writes an ECI header. Payloads are fixed literals or cyclic patterns. The tool keeps its own version / capacity table (`RmQRVersionTable`, values from [rMQR Encoder](rmqr-encoder.md)) so it does not depend on the library it produces oracles for.

Before writing a fixture, the sanity gate (`RmQRSanityGate`) decodes its render with the pinned zxing-cpp reader. It compares payloads on raw bytes (`Bytes` vs `payloadUtf8Hex`), since the reader gives UTF-8 without ECI a legacy-charset guess in `Text`. `Extra("Version")` must equal `R{H}x{W}` and `Extra("EcLevel")` the manifest ECC, and the reader's `DataMask` (4) is recorded. For Kanji fixtures it compares `Text`, the reader's JIS X 0208 reading that this library's decoder must reproduce, because a Kanji segment puts Shift_JIS bytes in `Bytes`.

All 144 cases passed on first generation. The Kanji cases have since grown the corpus to 152.

### Consuming tests

`StandardQrFixtureTest` decodes every fixture through the matrix path (`TryDecode(modules, size, …)`) and the image path (`TryDecode(SKBitmap, …)`), asserting payload, version, ECC level and, on the matrix path, the generator's mask pattern.

`MicroQRFixtureTest` likewise exercises the matrix and PNG image paths (`MicroQRCodeDecoder.TryDecode(SKBitmap, …)`, since the Micro QR image decoder of 2026-07-18), also asserting zero corrected errors and, on the matrix path, the reader's mask pattern.

`RmQrFixtureTest`, first written with the corpus, checks the rMQR corpus shape:

- both lineages
- every version in both lineages and at both ECC levels
- one single-character libzint case per version × mode
- manifest ↔ matrix ↔ PNG consistency
- structural invariants that do not depend on the tables (finder / sub-finder corners, edge timing rows).

Since the rMQR matrix decoder (2026-08-15) it also decodes every fixture through the public matrix path, checking payload, version and ECC with `ErrorsCorrected` 0 for libzint and ≤ 1 for the qrtool tail defect, and re-decodes the module centers sampled from the PNG. Since the rMQR image decoder (2026-08-16) it also decodes every PNG through the public image path (`RmQRCodeDecoder.TryDecode(SKBitmap, …)`, `Decode_PngFixture_*`) with the same expectations.

`KanjiFixtureTest` checks the Kanji guarantee once for all three symbologies: it decodes every Kanji fixture through the matrix and image paths, asserts a minimum Kanji case count per symbology so a regeneration cannot silently drop them, and checks the divergent-cell fixture against its JIS X 0208 readings.

`KanjiEncoderOracleTest`, added when Kanji encoding was built, uses the same fixtures as an encoder oracle: a single Kanji segment at a fixed version and level has one valid stream, so this library's symbol of the same text, written with `AllowKanji` and the manifest's mask (pinned for Standard QR and Micro QR, and rMQR has one mask), must equal the fixture module for module, except qrtool's rMQR tail modules. It also requires the Kanji fixtures to cover every Kanji count width (Standard QR 8, 10 and 12 bits, Micro QR 3 and 4, and rMQR 2 to 7), and those filled to capacity to hold exactly this library's count there. It skips only the divergent-cell fixture, by name, whose text this library does not write in Kanji mode.

The sweep measures the real images. No test asserts them as a set. Five Standard QR decode tests read the few a decoder change was built for, each at the right angles it reads:

- `KeystoneFrameDecodeTest`: five keystoned captures.
- `TwoAxisTiltDecodeTest`: `qrcode-2/fix-finderpattern-order`.
- `SpreadRingDecodeTest`: nine turns of captures whose rings are printed or read thin or thick.
- `ParallelogramCoverageDecodeTest`: `qrcode-2/qr-circles-1`.
- `SmallLatticeMeshDecodeTest`: `qrcode-3/22`.

`StructuredAppendFixtureTest` checks the Structured Append corpus in four ways:

- It decodes every symbol through the matrix path (where a matrix exists) and the image path, asserting its own text, version, ECC, mask, zero corrected codewords (matrix path, with the flipped-module negative control below) and the three header values.
- It decodes every matrix fixture into a one-character destination and requires no header on the failed result, checking the "reported on success only" rule where it is enforced.
- It checks the reassembly rules on the manifests: one count and one parity per set, and indices 0..count-1 each present once.
- Like `KanjiFixtureTest`, it checks the corpus shapes a regeneration could silently drop: both encoder lineages and the captures, a 2-set and a 16-set, a Kanji-mode set, and a different parity per lineage for the two charset-divergent texts.

The encode direction has its own cross test, `StructuredAppendZXingCrossTest`: ZXing.Net reads in-process each symbol of nine sets from `CreateStructuredAppend` (every charset, `Optimal`, boost, a BOM, the sixteen-symbol set), which must carry the position, count, parity and text this library's decoder reports. The `spot-check-structured-append` command is the wider three-reader check with the native oracle.

Each fixture class also has a negative control for its clean-decode corrections assertion: the fixture with one data module flipped must decode with exactly one corrected codeword. A dead `ErrorsCorrected` counter (always 0) would pass every zero-corrections assertion but fails this control.

The flipped module is the first codeword module in placement order: the bottom-right corner in Standard and Micro QR, and in rMQR the one the placement zigzag finds over the naive function-module map. Micro QR excludes M1, which is detection-only and rejects any data damage. rMQR runs the control on the libzint lineage only, since qrtool's tail defect leaves no zero-corrections baseline.

### Regeneration

```bash
# qrtool binary (pinned version + SHA-256), one-time per machine:
pwsh tools/QRInteropFixtures/get-qrtool.ps1

dotnet run --project tools/QRInteropFixtures -- regenerate

# Structured Append sets only (also part of regenerate):
dotnet run --project tools/QRInteropFixtures -- regenerate-structured-append

# Third-party captures, never regenerated: re-import from a zxing-cpp checkout, recording its commit
dotnet run --project tools/QRInteropFixtures -- import-structured-append-samples <zxing-cpp-root> <commit>

# Structured Append encode interop: sets this library writes, read by the three pinned readers,
# parity compared with the pinned balancing encoder (findings in the oracle matrix)
dotnet run --project tools/QRInteropFixtures -- spot-check-structured-append

# Kanji encode interop: every encoder cell, ASCII \ and ~ beside Kanji runs, and a Kanji segment
# after ECI 26, read by every pinned reader of each symbology (findings in the oracle matrix)
dotnet run --project tools/QRInteropFixtures -- spot-check-kanji
```

The same tool holds the oracle probes. They need the native oracle on the machine, so they run by hand and their findings go in the matrix below rather than CI assertions:

- `spot-check-microqr`, `spot-check-rmqr` and `spot-check-structured-append`.
- `spot-check-kanji`: this library's Kanji output through every pinned reader (below).
- `probe-creator`, `probe-rmqr` and `probe-rmqr-capacity`.
- `probe-kanji`: which lineage can emit Kanji mode, per symbology.
- `probe-kanji-sweep`: every Kanji-mode cell through qrtool + zxing-cpp, diffed against CP932 and written to `kanji-sweep.tsv`.
- `generate-kanji-table`: emits the library's `ShiftJisKanjiTable` from that sweep, and refuses unless the data matches the published JIS X 0208 totals and its CP932 delta is exactly the seven documented cells.

`kanji-sweep.tsv` is committed beside the tool: it is the provenance for a generated table that ships in `src/`, it carries the CP932 column the divergence claim rests on, and it lets the table be regenerated and reviewed without the native oracles.

The tool wipes and rewrites each available generator's directory. Commit fixture updates as an explicit, reviewed change, because the corpus exists to catch a generator-version bump that silently alters fixtures.

### Image decode sweep

The fixtures above prove a foreign symbol decodes from its matrix and from a clean render at 8 px/module, not how the image decoders cope with harder images. Until 2026-09-21 every sweep measuring that drew this library's own symbols.

`tools/QRImageDecodeSweep` measures it against other readers, image for image. Its main number is the gap, the images zxing-cpp reads and this library does not get through. The reverse counts images this library reads and zxing-cpp does not.

```bash
# Synthetic renders: every encoder's symbol of the same payload through the same render, 23 kinds
dotnet run -c Release --project tools/QRImageDecodeSweep -- sweep [qr|micro|rmqr|all] [cases] [outDir]

# The committed real images, each at the four right angles
dotnet run -c Release --project tools/QRImageDecodeSweep -- corpus [outDir]

# Two result files of the same run from two trees, image for image: gained, lost, and every lost image by name
dotnet run -c Release --project tools/QRImageDecodeSweep -- compare <before.csv> <after.csv>

# What a destination too short costs Micro QR and rMQR, render for render; and two such files from two trees
dotnet run -c Release --project tools/QRImageDecodeSweep -- destination [micro|rmqr|all] [count] [outDir]
dotnet run -c Release --project tools/QRImageDecodeSweep -- compare-destination <before.csv> <after.csv>

# Third-party images, never regenerated: re-import from a zxing-cpp checkout, recording its commit
dotnet run -c Release --project tools/QRImageDecodeSweep -- import-corpus <zxing-cpp-root> <commit>
```

- The encoders are this library, ZXing.Net, QRCoder, QrCodeGenerator, CodeGlyphX, libzint and qrtool for Standard QR, and this library, libzint and qrtool for Micro QR and rMQR. Each case fixes payload, level and version for every encoder. The payload is ASCII so a failure is an image failure, not a character-set convention. Micro QR and rMQR pin the version in every encoder. Standard QR sizes the payload above the capacity of the version below and within the requested one, so the requested version is the smallest that fits. The first cut took 75-100 % of the version's capacity, which from version 6 up also fits the version below: 230 of 400 cases landed under their version (two at version 40, three at 39). Another encoder's own segmentation may still choose a neighbouring version, as part of its output, so each row carries its symbol's version. The run prints how many cases put this library's symbol off the requested version (zero). The first cut's error was a claim about the generator that nothing counted. A claim about a sample is cheap to check.
- The kinds are the test suite's renderers, linked from `tests/FeatherQR.Tests/Shared` (crisp nearest-neighbour and anti-aliased path at a fractional scale and random sub-pixel offset, supersampled at any rotation with and without keystone), plus bilinear upscales, mip-mapped downscales, right-angle turns, mirrors, a JPEG round trip, and each library's own image writer. Every kind measured is one a test draws, including the supersampled renderer's four-module quiet zone round Micro QR and rMQR symbols (other kinds draw the two modules their specifications ask for). It is kept as the rotation tests have it, and a wider quiet zone is within either specification.
- The readers are this library, zxing-cpp and ZXing.Net, each given the same grayscale buffer and told the symbology.
- Render parameters are seeded by arithmetic on the case and kind indices (never `GetHashCode`, which is randomized per process), so two runs write byte-identical result files and two trees pair render for render. Each row carries its key (how its image was made) and a pixel digest. `compare` puts a pair whose digests differ in its own column, not in gained or lost: if an encoder picks another mask or a renderer changes between the trees, the pair is two different images and a moved read says nothing about the decoder. Every recorded table uses the default case counts (400 / 400 / 640: ten per Standard QR version, a hundred per Micro QR version, twenty per rMQR version).
- A content failure is not gap: an image this library decodes into another text, or whose bit stream it refuses (`InvalidBitstream`, `UnsupportedContent`, `UnmappedCharacter`), was still located, sampled and error-corrected, and has its own column. In the real-image sets that column holds Byte-mode Shift_JIS without an ECI header (eleven images, which zxing-cpp reads by guessing the character set), one GS1 symbol, and one symbol whose bit stream is refused and which neither other reader reads.
- `destination` decodes each Micro QR and rMQR render into three destinations (sized, one character short, and 2 characters), timing each call as the fastest of five rounds, each round making the three calls in turn.
  - The renders are 600 draws per symbology from a fixed seed (random version, level and text, turned, 2 × 2 supersampled, uniform noise), the sets behind the cost figures of the [single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan). Each draw tries texts from a random length down to 2 characters, and a draw where none fits its version and level is skipped, leaving 577 Micro QR renders and 600 rMQR.
  - Until 2026-10-01 the rMQR draws counted versions from 0, which is not a version number, so those draws were dropped and R17x139 was never drawn.
  - The set also holds the destination contract test's renders with a finder-like pattern inside the symbol.
  - The summary lists the random renders by version, 0 for a version the set lacks. Each row carries a digest of the render's pixels.
- `compare-destination` pairs two trees' renders by name and, like `compare`, by digest. It lists every status, version or text that moved within a same-image pair and the renders whose image differs between the files, and reports the cost over the same-image pairs and their count.
  - A file from before the digest (2026-10-01) pairs by how each render was made (kind, ppm, angle, noise), which detects a redrawn set but not a changed renderer. The comparison says so.
  - Times depend on the machine, so only their ratio to the sized call compares between runs.

The sweep is a hand-run measurement: it needs the native oracles, and its result is a table to compare, not a condition to assert. On 2026-09-27, without qrtool, a default `sweep all` took about 40 s on 32 hardware threads, nearly all Standard QR (Micro QR and rMQR a few seconds each), and `corpus` about a second.

A decoder change that moves reads is measured with the sweep against the commit before it, image for image, under these rules:

- No read may return anything but the encoded text, over every sweep and the corpus (the baseline is zero misreads).
- Nothing is lost silently: every lost render is listed by kind, and the reverse does not shrink without a stated reason.
- Failure paths are measured on failures: two-level and blurred noise, a damaged symbol of each symbology and another symbology's image, each timed as the fastest of interleaved runs and reported beside the success-path benchmarks. A read that multiplies the cost of every unreadable image is a trade-off to state, not a side effect.
- The finder's candidate counts on the two-level noise set stay identical unless the change is to the finder, in which case they are the change's headline number.
- Every report gives the corpus beside the sweep. A change that moves the threshold or the sampled grid says so and takes a new baseline. Only speed work and refactors are held to bit-identical results.
- The baseline is built apart: the commit before is checked out with `git worktree add --detach` and the sweep and corpus run there, so nothing built from the changed tree reaches it. Sweep result files are byte-identical between runs, but the corpus file is not (zxing-cpp's column moved on 9 of 624 rows, all at rotation 0, with this library unchanged, 2026-09-29), so a refactor is held to this library's columns, the ones `compare` counts.

Before anything is built, the true-transform column decodes each failing render as sampled through the geometry it was drawn with. A render that then reads failed in the frame (finder centres, dimension, perspective). One that does not failed in the pixels (threshold, where a module is sampled). The sweep knows every render's geometry, so the column is cheap. It pointed at the frame first for grey edges below 2 px/module and for keystone in both Standard QR and rMQR.

The change must then add a test render of the failing class with its drawn corners asserted, and the negative the repair must not admit (the same class with its data destroyed), each failing with the repair removed. Every new bound or gate must fail a test when moved. A guard no sweep or test can tell from its absence is deleted.

The encoder does not decide whether a symbol reads: over the first sweep's 135,000 decodes, a kind's read rate differed between encoders by at most 10 of 400 cases, from mask choice (40 to 50 % of QrCodeGenerator's, libzint's and qrtool's matrices differ from this library's, against 6 % of ZXing.Net's and 1.5 % of QRCoder's and CodeGlyphX's), and every library's own image writer read 100 %. The encoder columns stay because a failure that does not move with the render is the encoder's (qrtool's R17x43, below).

#### Where the gap stands

These figures were measured 2026-09-28 on `main` at 8ba2178, with the default cases and six Standard QR encoders (qrtool was not on the machine). The per-kind table is in the Standard QR record's envelope. The Micro QR and rMQR records list the kinds under 99 %.

| Synthetic renders | This library | zxing-cpp | ZXing.Net | Gap | Reverse |
|---|---|---|---|---|---|
| Standard QR, 54,400 | 52,327 | 48,738 | 32,545 | 18 | 3,607 |
| Micro QR, 18,400 | 17,887 | 12,524 | | 0 | 5,363 |
| rMQR, 29,440 | 28,656 | 7,771 | | 0 | 20,885 |

On the first baseline (2026-09-21, seven encoders, payloads sized as above), the Standard QR gap was 2,002 renders, 1,907 of them in five kinds: anti-aliased edges at 1.25-1.5 and 1.5-2 px/module, bilinear upscales at 1.5-2, any rotation at 2-3 and keystone at 6-20 %. The 18 left are anti-aliased renders at 1.0-1.25 px/module, below this library's envelope, where zxing-cpp reads 41 of 2,400 and this library 356.

Micro QR and rMQR had a gap of a few renders per kind, plus 54 in Micro QR bilinear upscales at 2-2.5 px/module. zxing-cpp reads far fewer of both, so they are tracked by their own read rate.

The real images were each read at four right angles:

| Set | Images | This library | zxing-cpp | ZXing.Net | Gap | Reverse | Content |
|---|---|---|---|---|---|---|---|
| `qrcode-1` | 8 | 32/32 | 32 | 24 | 0 | 0 | 0 |
| `qrcode-2` | 56 | 148/224 | 201 | 100 | 16 | 7 | 52 |
| `qrcode-3` | 18 | 67/72 | 72 | 56 | 5 | 0 | 0 |
| `qrcode-4` | 24 | 54/96 | 60 | 64 | 7 | 1 | 0 |
| `qrcode-5` | 16 | 64/64 | 64 | 64 | 0 | 0 | 0 |
| `qrcode-6` | 15 | 60/60 | 60 | 60 | 0 | 0 | 0 |
| Standard QR | 137 | 425/548 | 489 | 368 | 28 | 8 | 52 |
| `microqrcode-1` | 16 | 64/64 | 59 | | 0 | 5 | |
| `rmqrcode-1` | 3 | 12/12 | 12 | | 0 | 0 | |

On the first run this library read 295 of the 548 Standard QR images (gap 153) and 60 of the 64 Micro QR ones. The largest cause was a shading gradient across the symbol, which one global threshold splits into two solid halves (42 of the 43 gap reads in `qrcode-5` and `qrcode-1`). The regional pass reads them ([image decode passes](qrcode-symbologies.md#image-decode-passes)). Each image left in the gap has a recorded cause:

- 12 reads are out of scope. `qrcode-2/#940` and `qr-model-1` are Model 1 symbols (zxing-cpp reports `]Q0`): their format word reads under Model 1's mask, and their data, placed as Model 1 places it, fails. `#258` draws its finder rings as dots, and its diagonals read only with each run moved by 0.52 to 0.68 of a module, past the half-module bound.
- 14 fail in the grid. `high-res-1` is a version 34 on curled paper at 2.2 px/module. Its mesh interior reads, and the bands along its edges have about half their function modules wrong. `qrcode-4/29` is on curved paper and no alignment pattern is found: of 3,721 fourth anchors within 3 modules, 12 read, none with fewer than 15 corrections (20 through zxing-cpp's corners). `qrcode-3/03` is binarized with heavy ink spread, and the anchors that read need 23 or 24 corrections. `qrcode-4/12` at 0° and 90° has all four corners within 0.12 module of zxing-cpp's plane (a knife edge).
- 2 fail at a finder's floor: `qrcode-3/30` at 270° has diagonals at the 2 px floor, and `qrcode-4/15` at 270° has a falling diagonal rounded at one outer corner (runs of 5, 5, 15, 5 and 2 px).

## Oracle capability matrix

In the Status column, verified means exercised in this repository, and claimed means the capability is reported elsewhere but not yet confirmed or run here.

| Oracle | Standard QR | Micro QR | rMQR | Status | Notes |
|---|---|---|---|---|---|
| ZXing.Net 0.16.11 (NuGet, pinned) | encode + decode | - | - | verified | Fixture generator and `QRCodeDecoderZXingCrossTest`. It runs in-process with no toolchain |
| Net.Codecrete.QrCodeGenerator 3.2.1 (NuGet, pinned) | encode, Structured Append | - | - | verified | Balancing Structured Append encoder (`EncodeTextInMultipleBalancedCodes`) of the `qrcodegenerator` lineage, run in-process. It picks one charset for the whole text (ISO-8859-1 when lossless, else UTF-8 with an ECI in every symbol) and XORs those bytes once, the parity definition this library follows. 3.1.0 wrote parity 0 on every set (fixed in 3.2.0). Do not pin below 3.2.0 |
| CodeGlyphX 2.1.0 (NuGet, pinned) | encode + decode, Structured Append | - | - | verified | Explicit-parts Structured Append encoder (`EncodeStructuredAppend`) of the `codeglyphx` lineage, run in-process. It picks each part's version independently and emits Kanji mode for JIS X 0208 text with no ECI. Its one parity per set is the XOR of the whole text's bytes as it encodes them (Shift_JIS for a Kanji set, UTF-8 with an ECI for Latin-1 text), so the same text gets a different parity than in the balancing lineage: 176 against 6 for the Japanese set, 8 against 139 for Latin-1. The gate checks both against the bytes carried |
| zxing-cpp reader, Structured Append | decode | - | - | verified | Reports `SequenceIndex` (0-based), `SequenceSize` and `SequenceId` (the parity as a decimal string). `Bytes` holds the raw segment bytes that the Structured Append gate XORs. Its writer cannot produce Structured Append: the bundled libzint has no `structapp` |
| Structured Append encode interop (`spot-check-structured-append`, 2026-09-15) | encode, read back by three lineages | - | - | verified | The seven corpus texts × five option variants (single, `Optimal`, boost, forced UTF-8 ECI, UTF-8 with BOM) give 33 sets and 196 symbols. zxing-cpp (luminance), ZXing.Net (luminance) and CodeGlyphX (module matrix) each read all 196 with the position, count, parity and text this library's decoder reports. Parity matched `Net.Codecrete.QrCodeGenerator 3.2.1`'s balanced sets for the same text and level byte for byte, 21/21, where both pick the charset from the text. A forced ECI or a BOM is not comparable. Forcing UTF-8 on the Latin-1 text legitimately gives parity 8 where the oracle's ISO-8859-1 set carries 139, the pair the decode corpus holds. ZXing.Net keeps a byte order mark in `Text` that this library's decoder consumes, so the text comparison trims U+FEFF. This library correctly refuses two of the 35 combinations: sixteen symbols at version 2-L leave no room for an ECI header in every symbol. A 2026-09-30 re-run, after sets could use Kanji mode, gave 189 symbols, all read by the three readers. The Japanese text's `Optimal` set became a Kanji set of 2 symbols at version 4-M, read by all three, with parity 176: the XOR of its Shift_JIS bytes, and the parity of CodeGlyphX's own Kanji set of that text, where the UTF-8 oracle writes 6. The other 17 comparable parities are byte-identical to the oracle. A 2026-10-01 re-run, after Kanji mode became opt-in, added two variants that set `AllowKanji` (`kanji` under `Single`, `kanji-opt` under `Optimal`): 275 symbols read by the three readers, 0 mismatches in 30 parity comparisons. Under the default variants the Japanese text is the UTF-8 set again, parity 6 as in the oracle. Under `kanji-opt` it is the Kanji set of 2 symbols at 4-M with parity 176. Which set a variant wrote is read from zxing-cpp's stream, not from the parity under test. A Kanji set declares no ECI, carries the text's Shift_JIS bytes and is held to their XOR. Every other set is held to the oracle's parity. The gate fails a planted UTF-8 set carrying 176 or Kanji set carrying 6 |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) (via [ZXingCpp](https://www.nuget.org/packages/ZXingCpp) 0.5.2, pinned) | read + write | read | read | verified | Micro QR reading is exercised against this library's encoder by `tools/QRInteropFixtures -- spot-check-microqr` (all versions × ECC, UTF-8) and as the fixture sanity gate. The reader's `Extra("Version"/"EcLevel"/"DataMask")` supplies external metadata for the Micro QR manifests (M1's implicit level reads as "L"). The official .NET wrapper bundles native binaries, so no external toolchain is needed. For rMQR (`probe-rmqr`, 2026-08-15) it reports `Extra("Version")` as `"R7x43"`…`"R17x139"` (the planned `RmQRVersion` member spelling), `Extra("EcLevel")` as `"M"`/`"H"`, and `Extra("DataMask")` as `"4"` (rMQR's single mask is Standard QR mask pattern 4), verified on all 64 libzint version × ECC symbols and on qrtool rMQR output. For Byte-mode UTF-8 without ECI (the qrtool Japanese payload), `Text` gets a legacy charset guess (mojibake) while `Bytes` holds the exact UTF-8 bytes and `HasECI = false`. The wrapper offers no charset hint, so the rMQR sanity gate must compare `Bytes` with the manifest's `payloadUtf8Hex`, not `Text` |
| [Zint](https://zint.org.uk/) (libzint via ZXingCpp `BarcodeCreator`) | encode | encode | encode | verified | zxing-cpp's writer is libzint, compiled into the same pinned native binary. It is a Micro QR fixture lineage: `Options = "version=N,ecLevel=X"` is honored and `ToImage(Scale=1, AddQuietZones=false)` is module-exact. It rejects UTF-8 Micro QR input ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics came back transliterated after a round trip, so zint-lineage payloads are ASCII. As an encoder lineage it counts as zint, independent of this library. For rMQR (`probe-rmqr`, 2026-08-15), `version=1..32` maps to R7x43…R17x139 in height-major order (= the ISO version index + 1, 32/32 verified by output dimensions), `version=33..38` gives fixed height 7/9/11/13/15/17 with automatic width, `ecLevel=M|H` is honored, and all 64 version × ECC symbols round-trip through the zxing-cpp reader |
| [qrcode2 / qrtool (Rust)](https://docs.rs/qrcode2) | encode | encode | encode | verified | The `qrtool` 0.13.2 prebuilt binary, pinned by version + SHA-256 (`get-qrtool.ps1`). It is a Micro QR fixture lineage (all versions × ECC × modes incl. UTF-8, `--variant micro` with pinned `--symbol-version`/`--error-correction-level`/`--mode`) whose `--type ascii` output is module-exact, so no image parsing is involved. M1's detection-only level is requested as `l` because the qrcode crate models it as L. rMQR encoding was verified 2026-08-15 (`--variant rmqr -v <H> <W>`, all 32 versions × M/H, `--mode` honored): dimensions, capacities, format information and leading bit streams agree with the parameter tables in [rMQR Encoder](rmqr-encoder.md). It has a Kanji mode (see the Kanji row below). An earlier reading that it had none was wrong. With no `-v` it picks the version with the fewest modules (`probe-rmqr`: 12 digits → R11x27, 15 → R13x27, 100 → R11x77), as libzint and this library's default do. The rMQR encoder's final-message comparison found a defect (2026-08-15): on versions 11 modules high or taller, qrtool 0.13.2 never writes the last h − 10 modules of the placement walk (column 1, rows 8..h−3), so the last block's final ECC codeword loses its lowest (h − 10 − remainder) bits. zxing-cpp still reads every such symbol, correcting the one codeword error. Data codewords, all other ECC codewords, format copies and remainder bits match the spec-derived encoding, and libzint symbols match byte for byte. Encoder-side oracle tests tolerate exactly this defect for the qrtool lineage, and decoder tests must expect `ErrorsCorrected` = 1 (not 0) on the affected qrtool fixtures (R11x59, R13x43, R13x139, R15x43, R15x59, R15x139, R17x43, R17x59, R17x77, R17x99, R17x139 cases) |
| rmqrcode-python | - | - | encode | claimed | Not independently confirmed yet. Verify before relying on it |
| Kanji mode, per lineage (`probe-kanji`, 2026-08-23) | zxing-net + qrtool | qrtool | qrtool | verified | ZXing.Net emits Kanji when the requested charset is Shift_JIS and every character is JIS X 0208 double-byte, and its encoder reports `Mode.KANJI`, so the manifest mode needs no reader. .NET resolves Shift_JIS only after `CodePagesEncodingProvider` is registered, and ZXing.Net caches the lookup in static initializers, so the tool registers the provider before any encoding. qrtool has `--mode kanji` for all three variants (correcting the earlier reading noted in the qrtool row above), requires raw Shift_JIS bytes via `--read-from` (UTF-8 input fails with "invalid character"), and correctly refuses `--mode kanji` for M1/M2. libzint cannot produce Kanji: `From(string)` rejects every non-ASCII input ("Error 245: Invalid UTF-8 in input", a wrapper marshalling defect), and the previously unknown `From(byte[])` overload works but emits Byte mode with ECI 20, as symbol size confirms (46 kanji → R17x99 / v5-L, one version above the Kanji-mode fit). On the read side, zxing-cpp applies JIS X 0208 and exposes a Kanji segment's `Bytes` as raw Shift_JIS, not UTF-8, so for these fixtures the sanity gate compares against Shift_JIS, not `payloadUtf8Hex` |
| Kanji encode interop (`spot-check-kanji`, 2026-10-01) | encode, read back by four readers | encode, read back by zxing-cpp | encode, read back by zxing-cpp | verified | Run while Kanji encoding was built. This library (with `AllowKanji`) wrote all 6,872 encoder cells, in chunks. Standard QR at 1-L, 13-L and 27-L (the 8-, 10- and 12-bit counts, 18 symbols) was read by this library, zxing-cpp, ZXing.Net and CodeGlyphX. Micro QR at M3-L and M4-L (917 symbols) and rMQR at R7x43-M, R7x59-M, R9x139-M and R17x139-M (counts of 2, 3, 6 and 7 bits, 200 symbols) were read by this library and zxing-cpp. Every reader returned every symbol's text exactly. ZXing.Net found one 27-L symbol in its image only with the pure-barcode hint, a detection miss, not a misread. So no JIS X 0208 row loses its cells: the rule is about what can be represented, not about script. ASCII `\` and `~` in Byte runs beside Kanji runs (`Optimal`, no ECI) were read by every reader of every symbology as `\` and `~`, never as the JIS X 0201 ¥ and ‾, and zxing-cpp's raw bytes for each text were its Shift_JIS encoding, confirming a Kanji plan. A Kanji segment after ECI 26 splits the readers. QrCodeGenerator wrote such symbols from explicit segments (ECI 26 then Kanji, ECI 26 then UTF-8 Byte then Kanji, ECI 26 then Kanji then UTF-8 Byte, and a Kanji segment between two UTF-8 Byte ones). This library, ZXing.Net and CodeGlyphX read JIS X 0208 there. zxing-cpp 0.5.2 decodes the Kanji segment's Shift_JIS bytes as UTF-8 and returns U+FFFD, U+FFFD, `{`, U+FFFD, U+FFFD for 「日本語」 (the `{` is 0x7B, the second byte of 本's 0x967B). Putting the Kanji segment first does not help: before the ECI, zxing-cpp reads its bytes as ISO-8859-1. Its `Content::switchEncoding` drops every charset a mode set (as a Kanji segment sets Shift_JIS) once the symbol has an ECI, and bytes ahead of the first ECI default to ISO-8859-1, so a symbol with an ECI anywhere loses its Kanji segments. All four readers read the same segment with no ECI, which is what this library writes. That, with ISO/IEC 18004:2015 on zxing-cpp's side (an ECI governs the bytes of every mode) and readers split on it, decided that Kanji is never written beside an ECI header (2026-10-01). The same day, by hand, the iPhone Camera app (iOS 26.5) read all six phone-test symbols as written (Kanji with no ECI, ECI 26 over UTF-8 alone, and the four ECI 26 and Kanji arrangements), reading Kanji as JIS X 0208 whatever the ECI. An Android 17 phone's own QR scanner and Google Lens read none of the Kanji symbols but read ECI 26 UTF-8. So Kanji mode is written only on request ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)) |
| BoofCV (Java) | decode | decode | - | claimed | Candidate additional decode oracle. Not evaluated |

ZXing.Net and zxing-cpp descend from the same ZXing lineage, so they count as one independent implementation family, not two. Zint and the Rust crates are separate lineages. zxing-cpp's reader and the libzint writer share one native binary but are algorithmically independent codebases, so a create-then-read round trip within that binary still exercises two lineages.

zxing-cpp corrects rMQR at full Reed-Solomon strength, with no reserved misdecode-protection codewords `p` (`probe-rmqr-capacity`, 2026-08-21). The question was whether rMQR reserves `p` as ISO/IEC 18004 Table 9 does for Micro QR. ISO/IEC 23941 Table 8's capacity column is paywalled, so it was measured against zxing-cpp.

For each of the 64 version × ECC combinations, the probe damages a symbol one module at a time, keeping only flips this library's decoder reports as exactly one more corrected codeword, so the damage saturates this library's capacity in every Reed-Solomon block without a block-structure table. It then checks zxing-cpp both ways:

- It must still decode at saturation. Otherwise it stops below this library, implying a reserved `p` this library ignores.
- It must not decode one error past saturation. Otherwise it reaches further, implying this library's capacity is too low.

All 64 agree, and every saturation count equals `blocks × ⌊ecc per block / 2⌋`. This is evidence from the reference implementation, not a reading of the standard, so the Correction cap decision in [rMQR Decoder](rmqr-decoder.md) stays open until someone reads Table 8.

zxing-cpp is the only maintained OSS decoder for Micro QR and rMQR: ZXing Java/.NET, rqrr (Rust) and gozxing (Go) do not read them, and BoofCV (Java) reads Micro QR only. Encoder verification therefore rests on one external decode lineage plus specification-derived vectors and the in-repo extraction tests, a structural limit rather than a tooling gap. Decoder verification has no such limit: several independent encoder lineages (zint, Rust qrcode2) generate its fixture corpus.

Oracles must be pinned and obtainable without fragile, environment-dependent builds: NuGet packages, prebuilt static binaries and Rust tools' prebuilt release binaries qualify, and building C++/Python toolchains on dev machines or CI does not. Under this toolchain policy the fixture generators added with the Micro QR decoder (2026-07-17) are libzint (via the pinned ZXingCpp package) and qrtool (prebuilt binary, pinned by version + checksum). Docker-pinned builds remain a fallback.

## Why

### Why the corpus sits where it does among the test layers

The corpus is one of four test layers, each catching a failure mode the others cannot.

- Module-matrix conformance is the primary oracle: fixed matrices with pinned masks, compared before any image is involved. It is the only layer that can fail when encoder and decoder share a wrong interpretation, which no round-trip test can detect by construction.
- This corpus covers external symbol → this library's decoder. The other direction, this library's encoder → external decoder, is tested separately. A single round trip through one external tool proves neither half on its own.
- Image degradation tests exercise detection and sampling deterministically (rotation, blur, noise, perspective) on symbols whose matrix the layers above have shown to be good.
- Physical scanners are acceptance, never proof: a phone failing to read a spec-conformant symbol is an interoperability fact, not a specification violation, so it can inform a decision but must never gate one. That is why no physical test suite exists (see the scope table in [qrcode-symbologies.md](qrcode-symbologies.md#scope-decisions)).

Two structural rules follow.

First, sampling and payload decoding need a testable boundary. The matrix-level decoder entry points exist partly for this, so an image failure traces to "sampled the wrong matrix" or "decoded the right matrix wrongly" instead of one opaque false.

Second, the coverage cross-product is cut by design, because version × mode × ECC × payload × degradation is unbounded: matrix-level conformance is exhaustive, payload and degradation coverage focuses on boundaries, and anything larger is left to manual runs rather than pull-request CI.

### Why ZXing.Net is the first generator

ZXing.Net is already a pinned test dependency, runs in-process and needs no external toolchain, so the harness (manifest schema, loader, writer, tests, CI wiring) could be built and proven against the shipped Standard QR implementation at once. The plug-in generator interface (`IFixtureGenerator`) lets Zint / qrtool / zxing-cpp join for Micro QR and rMQR without touching the harness.

### Why fixtures assert the decode direction only

Two conformant encoders may produce different final matrices, because mask selection and mode segmentation are implementation choices, not normative outputs, so matrix equality with an external fixture is not a valid encoder-conformance test. `KanjiEncoderOracleTest` is the exception: a single Kanji segment at a fixed version and level has one valid stream, and with the manifest's mask (rMQR has one) one valid matrix. The corpus verifies this library's decoder against external symbols. Its encoder is verified by external decoders (ZXing.Net cross tests in CI, and zxing-cpp through the hand-run spot checks above, since no CI job runs it) and, for Micro QR / rMQR, by spec-derived matrix tests where the manifest pins the mask.

### Why fixtures are committed rather than generated in CI

Committed fixtures keep PR CI self-contained and deterministic (no Rust/C++/Python toolchains), and turn fixture drift into a reviewable diff instead of a silent side effect of a dependency upgrade.

## Lessons learned

- ZXing.Net's `ZXing.QrCode.Internal.Encoder` (not the public `BarcodeWriter`) returns the core `ByteMatrix` with version, mode and mask pattern. The mask lets fixture tests assert that this library's format-information decode reproduces the generator's mask choice, a much stronger check than payload equality.
- Neither Micro QR encoder (libzint through the wrapper, qrtool) reports its mask, so the mask comes from the zxing-cpp reader during the sanity gate (`Extra("DataMask")`). Reader metadata is just as external and also proves the value is on the wire.
- qrtool's `--type ascii` output (two characters per module) makes matrix extraction exact and image-free, but it trims trailing light modules from each line, so the parser must pad them back. `--mode` requires an explicit `--symbol-version`.
- Regenerating fixtures on Windows rewrites working-copy files with LF endings. When content is unchanged the only diff is line-ending churn (`git diff --ignore-cr-at-eol` is empty). Restore such no-op rewrites instead of committing them.
- The public `QRCodeWriter.encode` path scales and pads to a requested pixel size, so extracting the core matrix from it is lossy. Generating from the internal encoder and rendering PNGs in the tool keeps matrix and image pixel-exact for a known quiet zone and module size.
- ZXing's encoder honors the requested ECC (it never downgrades it), so the manifest can record the requested level as the expected decode result without reading it back.
- A committed external symbol is not automatically a byte-exact oracle. qrtool's rMQR output has a systematic tail defect (the last h − 10 placement modules are never written) that zxing-cpp silently corrects, so the sanity gate (payload + metadata equality) passed 36/36 while the encoder-side final-message comparison found 12 mismatches. Where the reader corrects errors, cross-lineage agreement decides: libzint is byte-exact, qrtool differs only in the last codeword's low bits, and both fit the ISO codeword counts that qrtool's layout could not hold. The corpus keeps the qrtool symbols, defect documented, as real-world "one corrupted ECC codeword" decoder cases.
- qrtool 0.13.2 has a second, uncorrectable rMQR defect: its R17x43-M differs from libzint's and this library's matrices (which agree byte for byte) in about 88 modules, and no reader decodes it. R17x43-H has only the tail defect. The image decode sweep found it as ten cases failing in every render kind, including the library's own 8 px/module writer, and a failure that does not move with the render is the encoder's. The sweep leaves R17x43 out of the qrtool lineage.
- libzint's creator in the pinned ZXingCpp package sometimes dies with an access violation on some payloads. Standard QR sweep case 301 (523 byte-mode characters at level Q) kills it about one run in three, in a process doing nothing else.
  - It was first taken for a threading fault: the first crashes came while readers ran on other threads, and a serial scan of the same cases passed. But neither a lock nor a run with a collector thread forcing finalizers changed anything, and finally a serial pass crashed on the same case.
  - A managed process cannot catch the crash, so the sweep runs the creator in a worker process, retries the case the worker died on, and gives a case up only after eight deaths in a row.
  - A dropped case is then rare but possible, so it is never silent: it gets a row saying so, the table lists it as not produced, and the run exits with a failure, because its images are no longer the set other runs have.
  - The fixture generator has not hit the crash (its payloads are short), but generate a new long-payload libzint case more than once before trusting it.
  - A worker killed this way can linger and hold its files. Build to another output directory instead of waiting.
- The zxing-cpp sample sets are small because that repository recompressed them (240 px WebP for most photographs). All eight sets are 1.1 MB, so they are committed rather than fetched.
- Failing classes found with one's own renders grow by one a sweep. A gap to another reader closes. Before the sweep, each round drew this library's symbols, found a failing class and fixed it, without showing whether the decoder had improved or the tests had got easier. No test change moves the gap. It shows where the decoder is behind, not merely imperfect, and it ends at zero or at a residual with a cause.
- A corpus someone else collected finds the class nobody here drew. The synthetic kinds came from failure classes this project knew, so none drew a lighting gradient, the largest cause on photographs: the real-image gap was 28 % of Standard QR reads against 3 % in the sweep. ZXing.Net, behind in every synthetic kind, at first read more photographs than this library (368 against 295) with the hybrid binarizer the regional pass now uses. A class the corpus shows once can be much larger when drawn: one bent version 7 photograph became a set of bowed renders with a gap of about 1,200 in 1,400.
- A stand-in renderer is checked against real readers before a test trusts it. The uneven-lighting renderer's first shadow depth (75 % across a three-module edge) was picked to make this library fail. At that depth every reader measured reads only some directions (zxing-cpp and ZXing.Net 2 of 8), and at 85 % none reads any. As a test assertion it would have set a target no reader meets.
- The oracle's column is specific to its run. zxing-cpp's corpus reads moved by one to three between runs of one build. In one of three runs of one build on 2026-09-28 it read 17 fewer Standard QR images, 16 at 0° (472 against 489), while this library's columns were identical in all three. Its native reader also died once with an access violation in `ZXing_ReadBarcodes`. The rerun was clean. Compare this library's columns between trees, and take the oracle's from more than one run.
- A brute-force reader needs a time budget. CodeGlyphX's reader, used in the keystone and Micro QR comparisons, spent over 25 CPU-seconds per keystoned render and never finished a cell, so it runs with a stated budget (1 s) and its column is what it reads in that time. Stopping one cell's process did not stop the loop over cells: the next cell ran unbudgeted beside the timing runs and doubled their times.
- Expected texts must reach the tool as committed. The corpus's `.txt` files are marked `-text` in `.gitattributes`, but a checkout older than that line keeps CRLF working copies (autocrlf) until the files are rewritten, and every reader's read of a multi-line text is then graded as another text. This hit 16 `qrcode-5` reads and zxing-cpp's four reads of `high-res-1`, keeping that image out of the gap and its triage. `git ls-files --eol` shows such files as `i/lf w/crlf`. Delete and check them out again.
- A premise must hold per case, or the case tests nothing. When the two single-finder decoders' destination tests became one contract test (`DestinationContractTest`, 2026-10-01), each case got the premise its rule needs, and rMQR's case "another symbol that fits is still read", with the small symbol drawn first, failed it: the small symbol had been tried first and read, so the case passed whatever the rule did. Drawn at 4 px/module against the big symbol's 6, the small symbol is now tried second in both orders.
- A fault planted at one grid site can be masked by the next. One fault let a read that does not fit continue at one site of a Micro QR candidate but cost only a few grids where a later site of the same candidate read the symbol again and ended it, which no timing bound catches. Planting the fault and timing a render set found, for each site without one, a render that reads only at that site. These cost about 27, 30 to 38 and 53 times a sized call through the timing frame, the module boundaries and the coverage re-read (per-call medians, 2026-10-02). A fault still uncaught is recorded with the reason the code gives ([single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan)).
- Every place a candidate ends needs a planted fault. The first list planted one per rule, covering 7 of Micro QR's 9 places that return on a terminal result and 4 of rMQR's 13. Planting at every such place found more uncaught faults, some costing tens of times a sized call. Each was then caught by a render that reads through it or recorded with the code's reason (2026-10-02).
- Moving a rule into shared code moves its planted fault too. Micro QR's per-size and turned site faults had been caught through the coverage re-read the site let run. Once the gate refusing a re-read after a read that does not fit moved into `GridRead`, its fault was planted and caught there for both decoders. The site faults are still caught through what the candidate's later grids read.
- A render built to read one way is read the way the decoder samples it. A Micro QR render drawn so its grid fails at the module centres but reads by coverage (`TwoTextRenderer`, data inverted under each centre) still read with both Micro QR coverage conditions switched off: another grid of the decoder read it with no re-read. A test of a read made one way is shown to fail with that way removed.
- A worst case over a render set moves between runs of one tree.
  - With a call timed several times in a row, the worst one-character-short ratio of the 577 Micro QR `destination` renders was 1.05, 1.10, 1.25 and up to 1.50 across runs of one tree, a different render each time, while the median held.
  - With the three calls taken in turn each round, nine runs gave a median of 1.00 and a 95th percentile of 1.01 to 1.03, while the worst moved from 1.02 to 1.46, a different render each run (2026-10-01 and 02).
  - So a comparison checks the median and 95th percentile, and the worst only against its spread.
- A draw that throws is a draw the set lacks. The rMQR `destination` set cast `random.Next(32)` to a version numbered from 1. The 0 it drew made the generator throw, the generator skips a draw no text fits, and the largest version went missing unnoticed. A set states what it holds, version by version, before its figures are used, and a draw skips only a text that does not fit: the `destination` draw catches only the generator's error for such a text. The sweep's cases size their text with `TryGetRequiredBufferSize`, so an out-of-range version or level stops the run. Before, they caught every exception, and an rMQR level drawn out of range gave 322 of the 640 cases a one-character text while the run finished.
- A timing test compares calls, not loops. Timing a loop of sized calls and then a loop of short ones lets one machine stall land in one loop and move the ratio. Taking the calls in turn and comparing medians puts a stall in one call, which moves neither median.
- `tools/mutation_check.cs` refuses a file with local changes, since `git checkout` would not restore it. A change's own files are checked with `--allow-dirty`: they are copied aside first, and checksums after the run confirm the copies match. A line repeated elsewhere in its file is found with the line beside it, a `␤` in the fault's text standing for the line break.
- A published table is a second reading, not an oracle. The Wikipedia transcription of ISO/IEC 23941 Table 3 agrees with all 128 rMQR count widths, and its Table 7 agrees with the Kanji capacity those widths give on 63 of 64 cells. The 64th, R11x77-M, is a corrupt row holding R11x59-H's Numeric, Alphanumeric and Byte cells. Kanji widths 2, 3 and 6 rested on that reading and on derivation until qrtool's fixtures tested them on real symbols.
- An oracle that does not write what is asked about can still build it. No pinned encoder writes a Kanji segment after ECI 26, but QrCodeGenerator 3.x takes explicit segments, which gave `spot-check-kanji` that symbol without using this library's internals and with a Kanji table other than this library's.
- A reader check must confirm what it is reading. The first texts holding ASCII `\` and `~` beside kanji were short, and in Standard QR and Micro QR they came out UTF-8, since a Kanji plan did not lower the version. They read back correctly and proved nothing about Kanji plans. The check now requires zxing-cpp's raw bytes to be the text's Shift_JIS encoding, and each symbology to carry both characters in a Kanji plan.
- An image reader's miss can be a failure to find the symbol. ZXing.Net's one miss in the Kanji spot check was a 27-L image it found only with the pure-barcode hint. The tool retries with the hint and reports how often, so a detection miss is not counted as a misread cell.
