# Decode: photographs, Shift_JIS without ECI, and the documented scope

## Purpose

The decoder records, the README and the image decoders' XML docs say real camera photographs are out of scope (Tier 3). A measurement on 2026-10-10 found that on ZXing's own QR photographs this library reads all but four of the images zxing-cpp reads, once one text rule is set aside: Byte segments in Shift_JIS without an ECI header, which this library returns as ISO-8859-1 text. The synthetic sweep that drove the last accuracy round reads every Standard QR render kind in full except anti-aliased edges below 1.5 px/module, and no longer separates the readers. On the harder public set, BoofCV's photographs, it reads 322 of 536 against zxing-cpp's 390, and most of that gap is symbols it does not find at full size and reads once the image is scaled down.

This plan measures where the image decoders stand on photographs, closes the charset gap, works on the photograph classes the measurement ranks, and rewrites the documented scope from the numbers. "Tier 3 is out of scope" becomes a measured envelope, as Tiers 1 and 2 already have.

## Where it stands (2026-10-10, `main` at 774e245)

### Synthetic renders

The sweep with its default cases and six Standard QR encoders (qrtool was not on the machine) reads exactly as on 2026-09-28:

| Synthetic renders | This library | zxing-cpp | ZXing.Net | Gap | Reverse |
|---|---|---|---|---|---|
| Standard QR, 54,400 | 52,327 | 48,739 | 32,545 | 18 | 3,606 |
| Micro QR, 18,400 | 17,887 | 12,524 | | 0 | 5,363 |
| rMQR, 29,440 | 28,656 | 7,771 | | 0 | 20,885 |

21 of the 23 Standard QR rows (kind and density) read 100 %. The other two are anti-aliased paths at 1.0-1.25 and 1.25-1.5 px/module (356 and 2,371 of 2,400). No kind draws blur, sensor noise, a bowed surface under a small version, tilt past the keystone envelope, a small symbol in a textured scene, or glare, so the sweep cannot rank photograph work.

### Photographs

| Set | Images | This library | zxing-cpp | Gap | Reverse | Charset |
|---|---|---|---|---|---|---|
| Committed corpus (zxing-cpp samples at 7e51e7b), four turns | 548 | 425 | 489 | 28 | 8 | 44 |
| ZXing's QR photographs (blackbox `qrcode-1` to `-6`), upright | 179 | 151 | 165 | 4 | 1 | 11 |
| The same, four turns | 716 | 606 | 660 | 15 | 5 | 44 |
| BoofCV `qrcodes_v3`, photographs with an agreed read | 536 | 322 | 390 | 77 | 9 | 0 |

The table is where the plan started. Since Phase 3 the Charset column is 4, 0, 0 and 0, and the first three rows read 465, 162 and 650 (Progress log).

Gap counts images zxing-cpp reads and this library does not get through, and Charset counts images this library decodes with the ISO-8859-1 reading of a Shift_JIS Byte segment. Both readers get the same grey image (SkiaSharp's Gray8 conversion), and a read counts only when the text equals the expected text.

The ZXing set is the one go-qr publishes as photographs its decoder was not tuned on. Through the ZXingCpp package zxing-cpp reads 165 of them upright, the number go-qr published for zxing-cpp 3.1, so the two harnesses agree. go-qr published 166 for itself, 155 for WeChat's reader and 154 for ZBar.

What zxing-cpp reads there and this library does not, upright:

- Charset, 11 images (`qrcode-2/10`, `16` to `21`, `24`, `26`, `27`, `29`). They decode, with the wrong text.
- `qrcode-2/5`: a small symbol on a billboard in a 480 × 360 photograph of a station hall. Not detected.
- `qrcode-4/14`, `19`, `29`: 240 × 240 photographs of a version 3 symbol printed on a bowed card. `14` and `29` fail Reed-Solomon and `19` is not detected. The corpus already records `qrcode-4/29` as curved paper where no alignment pattern reads.

This library alone reads `qrcode-2/28`. Neither reads 13 images, ten of them in `qrcode-4`.

### Photograph-like renders

Phase 1 added eight camera kinds to the sweep (a pinhole camera over a printed card, [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#image-decode-sweep)). The table and the cause of each new gap photograph are recorded in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands). Two classes hold the gap. Micro QR loses tilt past its keystone envelope (9 of 400 at 35-55° against zxing-cpp's 386). Standard QR loses barrel distortion from about version 10, which is also the whole phone-mix gap (189 of 400 against 328). Tilt to 55°, blur, noise, a bow and a small symbol in a scene read as zxing-cpp reads them or better, and rMQR reads every kind far ahead.

### Cost

On the upright ZXing set (Ryzen 9 7950X3D, .NET 10 JIT, warm, fastest of five), this library decoded 162 images at a median 0.090 ms (90th percentile 0.24 ms) and failed 17 at a median 0.34 ms (0.63 ms). zxing-cpp with `TryHarder` took a median 0.14 ms (0.73 ms). The whole set took 27 ms against 52 ms. On clean renders the cross-language benchmark puts zxing-cpp's image decode at 3.0 to 7.1 times this library's time on the CI runners.

### Why the documents say otherwise

The tier table was in the decoder record by 2026-07-16. The accuracy round of 2026-09-21 to 28 measured the decoders on the corpus photographs and recorded the result in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands), while the tier table, the README and the XML docs kept the earlier scope (the XML wording was last set on 2026-09-24, for uneven lighting). A comparison on 2026-10-10 took "clean images and keystone" from those documents and understated the decoder.

## The charset gap

A Byte segment with no ECI header reads as UTF-8 when it validates as UTF-8, else as ISO-8859-1, the standard's default since its 2006 edition ([standardqr-decoder.md](../specs/standardqr-decoder.md#decisions), Byte-mode charset heuristic). Japanese encoders write Shift_JIS there. The decode reports `Success` with the ISO-8859-1 reading: モバイル comes back as `oC`, and half-width ﾀﾞﾌﾞﾙ as `ÀÞÌÞÙ`. In `qrcode-2/16` the Kanji segments read correctly and the Byte segment beside them does not.

The eleven texts need ASCII, JIS X 0201 half-width katakana (64 characters) and JIS X 0208 (15 characters), and no CP932 extension. The JIS X 0208 table the Kanji mode decoder reads (`ShiftJisKanjiTable`, indexed by the value a Shift_JIS pair compacts to) covers the double-byte part, and JIS X 0201 katakana are one byte range. The scope row on ECI 20 says it needs the full CP932 range, roughly twice the Kanji table. That holds for CP932 as a whole, not for these symbols.

Two readers show the prior art:

- zxing-cpp keeps UTF-8 when the bytes validate and contain a multi-byte sequence, picks Shift_JIS when the bytes are valid Shift_JIS and hold a run of three half-width katakana or three double-byte characters, rules out ISO-8859-1 on any byte from 0x80 to 0x9F, and falls back to ISO-8859-1.
- go-qr scores each candidate reading with a model of how character classes follow each other, reads all undeclared Byte segments of one symbol in one charset, takes a Kanji segment in the symbol as the mark of a Japanese encoder, and reads undeclared single-byte text as Windows-1252.

The encoder constrains the guess. `SegmentDecoders.ResolvesToUtf8WhenUnspecified` mirrors the decoder's rule so that a mixed-mode plan never writes a Latin-1 run the decoder would read as UTF-8. A Shift_JIS guess adds the same hazard for Latin-1 text written without an ECI whose bytes look like Shift_JIS. Half-width katakana occupy 0xA1-0xDF, where ISO-8859-1 has characters such as Ã, Þ and ». Such a symbol has to carry ECI 3, or the guess must never fire on this library's own output. The readers that already guess misread such symbols today, so ECI 3 there also helps them.

## What other readers do for photographs

Each row is a technique another reader uses that this library's records do not describe, with the images or classes it is aimed at. None is a lead until the measurement in Phase 1 ranks it.

| Technique | Who | This library today | Aimed at |
|---|---|---|---|
| A downscaled search when the full-size one finds nothing (go-qr halves to 150 px, zxing-cpp has a configurable pyramid) | go-qr, zxing-cpp | Reads at full size only. A 2× bilinear upsample of a failed image was measured on 2026-09-20 and rejected for its cost on unreadable images (2.5 to 18 times). A downscale was not tried | Texture finer than a module (screens, halftone), large photographs with small modules |
| A homography fitted by least squares to many points (12 finder-edge corners per finder plus every alignment pattern) | go-qr | A 4-point transform anchored on one alignment pattern, the finders' frame, the mesh from version 7 | Perspective with noisy finder centres |
| A curvature model: a cubic lens correction kept only when the fixed patterns read back better, or a local grid between alignment patterns | go-qr, zxing-cpp | No model below version 7, which has no alignment lattice | Bowed small symbols (`qrcode-4/14`, `29`), barrel distortion |
| A third finder inferred from two well-confirmed ones | go-qr | Standard QR needs three finders | Glare, damage, a symbol cut by the image edge |
| Each module read as the mean of nine samples against a window measured in modules | go-qr | One sample against the threshold, then a coverage re-read against the global midpoint when the grid fails | Blur, uneven lighting inside the symbol |
| Several symbols per image | go-qr, zxing-cpp | One per call. D8 of the 2.0.0 plan records that this needs a new result type | Photographs of many codes |

Ranked by the reads Phase 1 found them aimed at ([qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands) has the tables):

1. A search at reduced scale when the full-size one reads nothing: 41 of BoofCV's 77 gap photographs read scaled down by 2, 4 or 8, all 10 of `monitor` among them, and in 47 of the 77 the symbol is 8 px/module or more. A pyramid of halves adds a third to the pixels searched, where the rejected 2× upsample added four times.
2. The search over a whole image whose crop reads: 10 more BoofCV photographs (6 in `brightness`), and ZXing's `qrcode-2/5` and `qrcode-4/19`. The scene kind reads 400 of 400, so no synthetic kind reproduces it yet.
3. The frame: 17 more BoofCV photographs read only as a grid through zxing-cpp's corners, as ZXing's `qrcode-4/14` does.
4. A grid that follows curvature on Standard QR from version 10: 146 phone-mix renders and 54 barrel renders, and none of `high_version`'s 7 gap photographs reads through one homography. The mesh exists from version 7 and does not follow it. A local grid between alignment patterns is the technique zxing-cpp uses there. `qrcode-4/29` and 9 bowed renders are the small-version form.
5. Micro QR past its keystone envelope: 123 renders at 15-35° and 377 at 35-55°. No photograph set holds Micro QR in number, so this rests on renders.
6. Blur and noise: 2 and 3 renders, and 3 `blurred` photographs that read scaled down.

Several symbols per image is the difference in codes read (322 against zxing-cpp's 900), not in photographs, and stays out of scope. A third finder inferred from two has no measured case of its own: `damaged` and `glare` hold 10 gap photographs, 6 of which read scaled down.

The records list what was tried and refuted on this decoder ([standardqr-decoder.md](../specs/standardqr-decoder.md#lessons-learned), [microqr-decoder.md](../specs/microqr-decoder.md), [rmqr-decoder.md](../specs/rmqr-decoder.md)). Each phase reads them before it builds, as "Read a lead from another reader's code up to the call that gates it" asks.

## Scope

| In | Out |
|---|---|
| Photograph sets and a photograph-like synthetic yardstick in `tools/QRImageDecodeSweep` | Several symbols per image (a new result type, decided separately) |
| A charset guess for Byte segments without an ECI header, and ECI 20 decode, in all three symbologies | Learned detectors, or any dependency |
| The encoder rule that keeps its own Latin-1 output out of the guess | Decoder options (D8 of the 2.0.0 plan, decided against). Revisited only if a technique cannot ship without a switch |
| Image stages for the photograph classes Phase 1 ranks | Time budgets and cancellation (2.1.0) |
| The README, the specs and the XML docs rewritten from the measurements | GS1 / FNC1 (2.0.0 plan, Phase 6c) |

## What has to stay true

- No read returns anything but the encoded text, over every set. For a Byte segment without an ECI header the encoded text is what its encoder wrote, so the guess is measured on Latin-1 symbols as well as Japanese ones, and this library's own symbols never misread.
- An image the global pass reads runs nothing new. The success-path benchmarks (`SimpleDecode`, `QRCodeImageDecodeEndToEnd` and the Micro QR and rMQR classes) stay level.
- Every new stage states its cost on failures (two-level and blurred noise, a damaged symbol, another symbology's image, the failing photographs) as a multiple of today's, beside the reads it gains.
- Steady-state decoding allocates nothing.
- Nothing read today is lost, image for image, on the sweep, the keystone envelope and every photograph set, under the rules in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#image-decode-sweep).
- No production switch exists only so a test can turn a stage off. Switch-off probes run as mutants or probes.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Which release takes the charset phase | Decided 2026-10-10: 2.0.0. Writing ECI 3 for Latin-1 text that would read as Shift_JIS changes the encoder's default output, which the 2.0.0 plan accepts only in a major (D5 there). The image stages change no output and ship in any 2.x |
| D2 | The guess rule | Decided 2026-10-10: rules, as zxing-cpp's, to start with. Settled in Phase 3: UTF-8 when valid, then Shift_JIS when the bytes are well formed and hold three half-width katakana in a row or a byte from 0x80 to 0x9F, then ISO-8859-1. Each Byte segment is resolved on its own, as before, and a Kanji segment beside it is not counted, since no measured symbol needed either. Three of zxing-cpp's clauses are left out and tab counts as text, on the measurement in the Progress log, which the maintainer accepted on 2026-10-10. A scoring model only if the rules misguess a measured case |
| D3 | Undeclared single-byte text: ISO-8859-1 or Windows-1252 | Decided 2026-10-10 on the Phase 3 measurement: ISO-8859-1, and the rule stays as it is. Of 1,866 Windows-1252 messages with a byte from 0x80 to 0x9F, 345 (338 of them French) now read as Japanese or are refused where they read one character a byte, as in zxing-cpp. Reading Windows-1252 would not help them: a typographic apostrophe before a letter is well formed Shift_JIS under either choice. What would is an exception for a lone pair that begins 0x84 or 0x91 to 0x94 and ends in an ASCII letter, at the cost of a lone kanji with those bytes, which is not measured. It is taken up again when a symbol shows up |
| D4 | CP932 extensions (NEC row 13, the IBM rows) | Decided 2026-10-10: no. None of the eleven photographs needs one. A pair with no cell is `UnmappedCharacter`, as in Kanji mode, in a declared segment and in a guessed one |
| D5 | ECI 20 decode | Done in Phase 3 for Standard QR and rMQR, through the Kanji table. The scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions) changed with it |
| D6 | BoofCV's photographs (`qrcodes_v3.zip`, about 200 MB, licence not stated) | Decided 2026-10-10: never committed. The maintainer downloads them from `https://boofcv.org/notwiki/regression/fiducial/qrcodes_v3.zip`, the tool takes the path, and only numbers enter the records |
| D7 | ZXing's QR photographs (Apache-2.0) | Decided 2026-10-10: not imported. Of the 179, 30 are pixel for pixel zxing-cpp samples and 49 more are the same photographs re-encoded (same size and name, a mean difference under 15 grey levels), so 100 are new. They are 10.5 MB as PNG and 9.2 MB as lossless WebP, eight to ten times the committed corpus (1.1 MB). `photos` reads them from a ZXing.Net checkout, as BoofCV's are read, and a photograph is committed only when a test needs it, as the five corpus decode tests needed theirs |
| D8 | go-qr as a measured reader | Proposed by the maintainer 2026-10-11 and run that day as recommended; the maintainer has not yet ruled on the details. As run: its own test, unchanged, run with the Go toolchain on the maintainer's machine from the checkout at `.references/go-qr`, the workspace file kept outside the checkout; never in CI and never a test's dependency; no Go code and no result file in this repository, only numbers and causes in the records, as with BoofCV's photographs. The toolchain policy in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) is written for oracles a test depends on and names prebuilt binaries; a `go test` run on one machine for a measurement is outside what it rules on, which is why this is a decision. Which grey image go-qr reads was settled by the comparison in Phase 1b: reads move with the conversion, so the records use this tool's grey images, and its own conversion is recorded as the reproduction of its published figures |

## Phases

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Measure photographs | Done 2026-10-10 (Progress log): the `photos` and `boofcv` commands, the ZXing set read from a checkout (D7), zxing-cpp's samples at 2c3dcfe checked against the import, eight camera kinds drawn by `CameraRenderer`, BoofCV per category with a cause class for each of its 77 gap photographs | A table per set with gap and reverse, a recorded cause for every gap image of the ZXing and zxing-cpp sets, BoofCV per category, and the candidates of the technique table ranked by the reads they are aimed at |
| 1b | go-qr in its own harness | Done 2026-10-11 (Progress log; "go-qr in its own harness" below, D8): go-qr's `TestBoofCV` run unchanged on its checkout over BoofCV's photographs and ZXing's, its lines read by `boofcv` and `photos` as one more reader's column. 412 of 536 against this library's 416, 23 photographs only it reads and 27 only this library; 165 of 179 against 164 | The published 413 of 536 and 166 of 179 reproduced, or the difference explained. Gap and reverse against go-qr per category, a cause class for every gap photograph, and the candidates ranked again |
| 2 | Documented scope from the measurement | Done 2026-10-10 (Progress log): the documents in "Documents to change" state the measured envelope on photographs, with no stage added | Every place listed says what the measurement shows, and none says "out of scope" for a class the decoder reads |
| 3 | Charset | Done 2026-10-10 (Progress log): the Shift_JIS guess and ECI 20 in the shared Byte segment decoder, and Micro QR writing UTF-8 where its ISO-8859-1 bytes would read as another text. Standard QR and rMQR already declared ECI 3. The rule and its costs were accepted as measured (D2 to D4) | The 11 ZXing images and the 44 corpus reads return their texts. A round trip over Latin-1 payloads, including text whose bytes look like half-width katakana, misreads nothing. The guess's rate on foreign Latin-1 symbols is measured and recorded. Micro QR and rMQR share it |
| 4 | Image stages | The first, a search at reduced scale, done 2026-10-10 (Progress log): BoofCV 322 to 371 of 536, the gap 77 to 40. The second, a third finder inferred from two, done 2026-10-11 (Progress log): 371 to 400, the gap 40 to 28, the reverse 20 to 37. The third, the selection on a crowded or 12-megapixel scene, done 2026-10-11 (Progress log): 400 to 406, the gap 28 to 22. The fourth, the fourth point from the finders' edges for a symbol nothing anchors, done 2026-10-11 (Progress log). The fifth, the alignment mesh of a version 25 and up at 5 to 8 px/module, investigated and not built (Progress log): the lattice is found, the mesh's extrapolated edge nodes are what fail. The sixth, those edge nodes measured, done 2026-10-11 (Progress log): 413 to 415, the gap 18 to 16. The seventh, a grid that follows a lens, planned ("A grid that follows a lens" below): the phone-mix renders, 235 of 400 against zxing-cpp's 328. Each starts from the failing images, finds the stage that loses them (the true-transform column on synthetic kinds, zxing-cpp's corners on photographs), and follows the accuracy-change rules | Each sub-phase's own exit: the reads gained per set, nothing lost, the failure-path multiple, and the test renders with their mechanism switched off |
| 4b | Vector tiers for the halving | Done 2026-10-11 on x64, WebAssembly and ARM64 (Progress log, the ARM64 timing in its own entry): `LuminanceHalver` has a 256-bit tier, a portable 128-bit tier and a WebAssembly tier under the rules of the 128-bit round, with the word-at-a-time loop as the scalar tier and the tail ("The halving's vector tiers" below). It changed no read | Every build class runs the tier it measured faster on, or keeps the word-at-a-time loop with its measured reason beside its row. The failing photographs' multiple is measured again and replaces 1.53 in the records |
| 5 | Fold | Durable results into the decoder records, [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) and [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the README and XML docs final, this file deleted and the index updated | No completed plan remains |

Phase 2 runs before any stage is built, because today's documents are wrong about today's decoder. Phases 3 and 4 are independent. Phase 3 has to land before the 2.0.0 freeze if D1 holds, and each sub-phase of Phase 4 updates the documents for its own change. Phase 1b builds nothing in the library and can change the ranking, so it runs before the seventh stage of Phase 4 is built; that stage's diagnosis does not wait for it.

## The halving's vector tiers

Phase 4b. The reduced-scale search shipped with a scalar halving, four pixels a step in one 64-bit word. The maintainer asked on 2026-10-10 for the vector tiers as a phase of their own.

Where it stands, on one 12-megapixel photograph (Ryzen 9 7950X3D, .NET 10 JIT):

| Step | Time |
|---|---|
| Halving, a byte at a time (the first cut) | 3.7 ms |
| Halving, a word at a time (shipped) | 1.6 ms |
| `LuminanceInverter` over the same bytes, 256-bit | 0.23 ms |

The inverter reads and writes 12 megabytes where the halving reads 12 and writes 3, so 0.23 ms is about what memory allows and the target is a few tenths of a millisecond. The step from 3.7 to 1.6 ms took the multiple on BoofCV's failing photographs from 1.68 to 1.53. By the same proportion a tier at the inverter's speed is worth about 0.09 more, an estimate the phase replaces with a measurement. The halving runs only on an image nothing read, so it is no part of any read.

What to build:

- `LuminanceHalver.Vector256.cs`, 32 pixels of each row a step into 16, and `LuminanceHalver.Vector128.cs`, 16 into 8. The sums of a block fit sixteen bits, so a row's bytes viewed as 16-bit lanes give the pair sums with a mask and a shift, the two rows add, and the means narrow to bytes. No platform instruction is expected to be needed, and `Narrow` exists on .NET 8.
- The stem file keeps the entry point, the dispatch and the word-at-a-time loop, which is the scalar tier, the netstandard builds' only tier and every row's tail.
- The search halves a level into the buffer it sits in. A step loads both rows before it stores, and a row ends in the scalar tail, not in a last vector step laid over pixels already written. `LuminanceHalverTest` checks in place against the definition, and each tier is called through its own entry there.

What the rules of [the 128-bit round](../specs/qrcode-symbologies.md#the-128-bit-round) ask of it:

- The kernel's row in `SimdTiers.cs` and its expected tier for each build class, its files in `SimdTiersTest`, and the rendered table in [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md).
- A `--parity` case in both report projects, through each tier's own entry point, in place and out of place, with planted faults that make it fail.
- A `--time` kernel pair (through the dispatch, and the scalar entry) and a shape that reaches the halving. No shape of the report projects fails to read today, so the phase adds one, an image with grey levels and no symbol.
- Each build measured as itself: the JIT on x64 with and without AVX on .NET 8 and 10, a default NativeAOT publish, `x86-64-v3`, ARM64, and WebAssembly interpreted and AOT-compiled. A tier ships on a build only where it beats the word-at-a-time loop there and loses nowhere beyond noise, and interpreted and AOT-compiled WebAssembly have to agree.
- The 3 % bar is on the kernel's share of a shape. From the 12-megapixel timing the halving is about 6 % of a failing photograph and about 8 % of a 740 px ramp with no symbol, both over the bar, and both estimates until the new shape is timed.
- The machine code is read: the search's own method compiles as before on every build, and the word-at-a-time loop costs no instruction for having tiers above it.

What it does not touch: the levels, the floor and which images are searched stay as the first stage of Phase 4 left them, so the sweep, the corpus and both photograph sets read image for image as before, which `compare` shows. The histogram on photographs (4.6 ms on the same photograph, five times a symbol pass) is a lead of its own and not part of this phase.

## go-qr in its own harness

Phase 1b, proposed by the maintainer on 2026-10-11. go-qr has been compared through its published figures alone: 77.1 % of BoofCV's photographs and 166 of ZXing's 179 upright. They give a total and a share per category, not which photographs.

Why it is worth a phase:

- The zxing-cpp gap is nearly spent as a source of leads: 16 photographs on BoofCV's set, no class of more than four.
- go-qr reads other photographs than this library does. Its published shares, turned into counts (they sum to 413, the 77.1 %), put it ahead in seven categories by 12 photographs: `damaged` 16 against 12, `pathological` 19 against 17, `perspective` 25 against 23, and one each in `glare`, `blurred`, `curved` and `noncompliant`. This library is ahead in four by 14: `bright_spots` 22 against 17, `high_version` 27 against 22, `brightness` 25 against 22, `nominal` 60 against 59. The 12 are a floor: the photographs go-qr reads and this library does not are at least that many, and only a run names them.
- Three of its techniques are still rows of the technique table with nothing built (the homography fitted to many points, the cubic lens correction, the module read as nine samples). A gap photograph says which of them a measured image needs.

How:

- The checkout at `.references/go-qr` (v2.6.0, d8a5d8d) and the Go toolchain on the maintainer's machine (1.26.4). `tools/bench` holds `TestBoofCV`, which reads every photograph of a directory of directories through `Decode` and writes one line of JSON a decoder and photograph: the directory, the file, the texts and the milliseconds. It is run unchanged.
- `tools/go.mod` requires the released v2.5.1, so the checkout is measured only through a workspace. go-qr's own instructions write that file into the checkout's root; here it goes outside (`GOWORK`), and the checkout stays as cloned. The module fetches gozxing and two encoders from the module proxy.
- The published figures come first, as zxing-cpp's did in Phase 1: 413 of 536 and the share of each category. A harness that reproduces them is one whose other columns compare with this tool's.
- `boofcv` reads the lines as an input and gives go-qr a column, scored by the rule every reader is scored by (a text another read confirms), with gap and reverse against it per category and the photographs named.
- The same test pointed at ZXing's black-box directory walks `qrcode-1` to `-6`; `photos` scores those lines against the expected texts, upright, where 166 is the figure to reproduce.

To settle on the way:

- The grey image. go-qr's test converts with Go's image package, this tool gives every reader SkiaSharp's Gray8. The two are compared on a sample of photographs first. If a read moves with the conversion, the tool writes its grey images out and go-qr reads those, so that the column means what zxing-cpp's does.
- The time. Its milliseconds are Go's on this machine beside .NET's, a call each, and are recorded beside each other without a ratio the records lean on. go-qr publishes 8.7 s for the set against zxing-cpp's 5.5 s; this library takes 2.5 s here and zxing-cpp 5.9 s.
- go-qr states that its thresholds were tuned on BoofCV's set, so ZXing's is the fairer column. Both are recorded.
- `DecodeAll`, several symbols an image, stays out as the codes column does.

Exit: the row in the phase table. Each gap photograph gets its cause by the probes Phase 4 used (the finders, the selection, the grid), and the ranking is written again with the seventh stage among its rows.

## A grid that follows a lens

The seventh stage of Phase 4: candidate 4 of the first ranking, which no stage has taken.

Where it stands, on the sweep of 2026-10-11 (400 renders a kind, both readers on the same image):

| Kind | Versions | Renders | This library | zxing-cpp | Gap | Reverse |
|---|---|---|---|---|---|---|
| Phone mix | 1 to 6 | 60 | 56 | 51 | 0 | 5 |
| | 7 to 13 | 70 | 33 | 68 | 35 | 0 |
| | 14 to 24 | 110 | 65 | 93 | 29 | 1 |
| | 25 to 40 | 160 | 81 | 116 | 41 | 6 |
| | All | 400 | 235 | 328 | 105 | 12 |
| Barrel distortion 5-20 % | 1 to 6 | 60 | 34 | 20 | 0 | 14 |
| | 7 to 13 | 70 | 3 | 25 | 22 | 0 |
| | 14 to 24 | 110 | 17 | 23 | 7 | 1 |
| | 25 to 40 | 160 | 17 | 18 | 5 | 4 |
| | All | 400 | 71 | 86 | 34 | 19 |

What the table says:

- The phone mix is the largest gap left on any set, 105 renders. 98 of them fail in the data and 6 are not detected: the finders are found and the grid is what loses them.
- Versions 7 to 13 are the worst band, 33 of 70 against 68. Theirs is the 3 × 3 lattice whose mesh one fitted plane places, and the sixth stage's lesson is that no plane fits a bend. Its two alignment patterns on the timing lines are placed by that plane, not searched, where the large lattice's are searched since the sixth stage.
- Below version 7 this library is ahead on both kinds, so that band is not in the stage.
- The barrel kind at 5 to 20 % is past both readers (zxing-cpp reads 86 of 400). It is the yardstick for a mechanism, and the phone mix, whose lens term is 0 to 10 %, is the target.
- Phase 1 measured the lens term as the cause: without it the phone mix read 40 of 40 at versions 2, 5, 10 and 20, and with it 21 and 10 of 40 at versions 10 and 20, against zxing-cpp's 40 and 36. That was before the mesh stages, so the diagnosis repeats it.
- The sixth stage moved both kinds without aiming at them (the phone mix 191 to 235, the barrel kind 47 to 71): the mesh is where these renders are decided.

Diagnosis before anything is built, by a probe on the 105 and the 34:

1. Each gap render drawn again with one effect taken out (the lens, the bow, the blur, the tilt) and read: which effect loses it, per band.
2. Against the camera's own truth (`Photograph.ToPixel`): how far the finder centres and each lattice node are from where they were drawn, which nodes are found, and the mesh grid's wrong modules per cell, as the fifth stage's map. It has to split the failures four ways: nodes not found; nodes found and the cells between them too coarse for the curvature; the strip outside the lattice, six modules extrapolated from the nearest cell; the dimension or the finders.
3. The grid through the true nodes, sampled as the mesh samples. If it does not read, bilinear cells are not enough and the model changes. If it reads, the nodes are what to measure.

Candidates, each read in the other reader's code up to the call that gates it:

- The small lattice's two alignment patterns on the timing lines searched, as the large lattice's are. The nearest to what the sixth stage built.
- zxing-cpp's local grid between each pair of alignment patterns, to which go-qr's own record credits zxing-cpp's lead at high versions.
- go-qr's cubic lens correction on a homography fitted to many points, kept only when the fixed patterns read back better. It needs more points than three centres and an anchor, which the mesh now measures.
- The strip outside the lattice measured on the timing patterns and the finders' edges, as the fourth stage follows an edge.

Exit: the reads gained per band on both kinds with nothing lost, every set image for image under the accuracy-change rules, the failing photographs' multiple and the no-symbol shapes, test renders with their premises asserted, and a round of planted faults. Renders are the target because no photograph set holds the class in number: BoofCV's gap is 5 in `high_version` and 1 in `perspective`. If Phase 1b's gap holds photographs of this class, they join it.

## Documents to change

- [README.md](../../../README.md):
  - "Decoders" says image decoding is for screenshots, generated images and clean scans and that camera images with strong perspective, hard-edged shadows or blur are outside its scope.
  - The FAQ entry "Any plan to support QR code scanning?" has a heading from before decoding shipped and says real-world photos are outside the library's scope.
  - The FAQ entry on encodings says ECI 20 Shift_JIS Byte segments are not read (Phase 3).
  - The README's own rules hold: no timing tables and no pointer to another reader.
- XML docs: `QRCodeImageDecoder`, `MicroQRCodeImageDecoder` and `RmQRCodeImageDecoder` in `FeatherQR.SkiaSharp` ("Made for clean images … Photos with strong perspective, hard-edged shadows or blur are out of scope"), and `MicroQRCodeDecoder` in the core ("clean screen or scanner images … mild perspective"). The Playground's API page is generated from them by `tools/public_api.cs` at release.
- [standardqr-decoder.md](../specs/standardqr-decoder.md):
  - The support tiers, whose Tier 3 row says photographs are out of scope.
  - The first bullet of Why.
  - The paragraph saying the README names no other reader "for the out-of-scope real-world photos".
  - The Unsupported list ("Other ECI charsets").
  - The Byte-mode charset decision, whose revisit condition this plan meets.
- [microqr-decoder.md](../specs/microqr-decoder.md) and [rmqr-decoder.md](../specs/rmqr-decoder.md): the Tier 1 to 2 envelope statements.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md):
  - The symbology status table ("Tier 1–2").
  - The ECI 20 scope row and the sentence on Kanji mode that says ECI 20 Byte segments need the wider CP932 range.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): "Where the gap stands", with the new sets, and the content column, whose Shift_JIS images become reads.
- The user documents under `docs/` hold no such statement today (checked 2026-10-10). Phase 3 adds the charset behaviour to `docs/migration.md` if D1 puts it in 2.0.0.
- Every item above was changed in Phase 2 or Phase 3 (Progress log), `docs/migration.md` included. Phase 5 has the final pass.

## Progress log

Entries are appended per phase: what was done, what was learned, and the read and cost deltas or an explicit statement that nothing moved.

### Investigation (2026-10-10)

Done: the measurements above, from scratch harnesses outside the repository (the sweep tool's `sweep all` and `corpus` on `main`, and a reader loop over ZXing.Net's checkout of the ZXing blackbox photographs). No code changed.

Lessons:

- A scope statement is a claim about the decoder like any other, and it was never measured. The photograph numbers were in the fixture record for two weeks while three other documents kept the earlier wording, and a comparison read the wording.
- A yardstick saturates. The synthetic sweep found the last round's work and now reads 100 % on 21 of its 23 Standard QR rows. A sweep where both readers read everything ranks nothing, so the next round needs sets where they fail.
- The largest photograph gap was a text rule, not image work. Separating "decoded with another text" from "not decoded" before ranking image stages kept it from being read as a detection gap.
- Reproducing a published number validates a harness. zxing-cpp read 165 of the ZXing set here, the figure go-qr published for it, which makes go-qr's other columns on that set comparable with this library's.

### Phase 1: the sets and the camera kinds (2026-10-10)

Done:

- `CameraRenderer` in `tests/FeatherQR.Tests/Shared`: a pinhole camera over a printed card, tilted about any axis, turned, bent round a cylinder and seen through a lens with radial distortion, 3 × 3 rays a pixel, then Gaussian blur and sensor noise, on a plain table or in a scene of clutter. `CameraRendererTest` checks its geometry where it has a closed form (front-on scaling, the far edge's foreshortening to 1e-6, the bow's curvature, the barrel factor at the corners), that one shot draws the same pixels twice, and that the decoder reports the corners it drew for three tilted Standard QR photographs and reads a Micro QR and an rMQR one.
- `tools/QRImageDecodeSweep`: eight camera kinds after the existing ones, so their seeds did not move (the own-writer kind is now found by name), `photos` for sets read from a checkout, `boofcv`, and every gap and reverse image listed by name for `corpus` and `photos`. The tool also looks for the repository above the working directory, for a build whose artifacts are outside the tree.
- Measured on `main` at f4b74de. Against a run of the tool before this change, `compare` paired all 102,240 renders of the existing kinds with the same pixels and the same reads, so their seeds did not move, and the 11,520 camera renders had no partner. The corpus read as its 2026-09-28 file, image for image. The camera kinds, ZXing's set and the cause of each new gap photograph are in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands), and the ranking is under "What other readers do for photographs".
- zxing-cpp's samples at 2c3dcfe hold every committed corpus image pixel for pixel and one new one, a photograph of four Structured Append symbols. The rest is renames, merged sets and `.toml` expectations that allow several symbols an image. A re-import would add no single-symbol photograph and would move the files five decode tests read, so the import stays at 7e51e7b.
- The full suite passes on net8.0 and net10.0. No library code changed, so no read or cost moved. The sweep takes 1 min 33 s for Standard QR with the camera kinds, against about 40 s without.

Lessons:

- A pooled row hides where a limit lies. Barrel distortion of 5 to 20 % read 46 of 400 here and 86 in zxing-cpp, which looks like a class both readers fail. Split by strength and version, the limit moves with the version (version 2 holds to 10 %, version 20 fails at 2.5 %), and from version 10 zxing-cpp holds about twice the distortion.
- Switching one parameter off in a mixed kind names its cause. The phone mix read 189 of 400. With its lens term at zero it read every render tried.
- A renderer's geometry is tested where it has a closed form before its read rates are believed. The first tilt test expected the tilted card's top edge farther. The renderer brings it nearer, a tilt either way, and the test now states that convention. The first bounds check counted the grey table under the card as ink.
- A duplicate is not always pixel for pixel. A digest found 30 of ZXing's photographs among zxing-cpp's samples, and size with a mean difference found 49 more that zxing-cpp re-encoded.
- Tests that find the source tree from their own binary fail when the build's artifacts live outside it (20 here: golden images, the core build, the file-family check). The suite is run in the tree.

### Phase 1, BoofCV: the gap is at full size (2026-10-10)

Done: `boofcv` over the 536 photographs of `qrcodes_v3` (the maintainer's download, under `.references`, not committed), with timing, and a probe of the 77 gap photographs outside the repository: each scaled by 0.5, 0.25, 0.125 and 2, cropped to a symbol zxing-cpp found, and sampled as a grid through zxing-cpp's corners. D7 was decided against an import. Phase 1 is complete. No library code changed, so no read or cost moved.

Result: 322 photographs against zxing-cpp's 390 (gap 77, reverse 9), with no read that disagreed with another reader's and none unconfirmed. zxing-cpp's 72.8 % is the 73.1 % go-qr publishes for it, so this harness and go-qr's agree on a second set. This library took a median 1.3 ms a photograph against zxing-cpp's 6.2 ms.

Lessons:

- A small set ranked the candidates the wrong way round. On ZXing's four gap photographs, all under 500 px a side, scaling down read none, and the first ranking put a reduced-scale search last. On BoofCV, where 39 of the 77 gap photographs are over 1,600 px a side, scaling down reads 41. A candidate is ranked on the set that holds its class.
- Most of the gap is finding the symbol, not reading it. The grid through zxing-cpp's corners, one homography and a plain threshold, reads 69 of the 77. What the decoder is behind on in photographs is the search at full size, and in 47 of the 77 a module is 8 pixels or more.
- "On par on photographs" held on one set and not on the next. On ZXing's set the image-level gap was 4 photographs, and on BoofCV it is 77 of 536. The documents of Phase 2 state the envelope per set and class, not one sentence about photographs.
- An expected text can be wrong in its line ends. Nine of the 26 rendered symbols in BoofCV's `decoding` directory read as another text in all three readers, CRLF in the symbol against LF in the file.

### Phase 2: the documents say what was measured (2026-10-10)

Done: every place under "Documents to change" that states a scope. The README's Decoders paragraph and its FAQ entry, now headed "Does it read QR codes from images?". The XML docs of the three image decoders and of `MicroQRCodeDecoder`. In [standardqr-decoder.md](../specs/standardqr-decoder.md) the tier table, the first Why bullet and a new [Photographs](../specs/standardqr-decoder.md#photographs) section that gives each set and the classes this library is behind in. A photographs paragraph in the Micro QR and rMQR records, and the status cell in [qrcode-symbologies.md](../specs/qrcode-symbologies.md). No code changed, so no read or cost moved.

Lessons:

- One sentence about photographs is wrong whichever way it leans. A decoder at 162 of 179 on one set and 322 of 536 on the next neither "reads photographs" nor has them "out of scope", so the records give the sets and the classes, and the README names the classes without numbers.
- A scope sentence is written per symbology from what was measured for it. No rMQR photograph set exists and its camera renders are the only evidence, so its XML doc says tilted, blurred or noisy images and does not say photographs.

### Phase 3: Shift_JIS in Byte segments (2026-10-10)

Done:

- `SegmentDecoders.DecodeBytePayload` reads Shift_JIS, declared by ECI 20 (Standard QR and rMQR) or guessed for a segment with no ECI header that is not valid UTF-8. ASCII stands, the 63 half-width katakana are one range, and pairs go through `ShiftJisKanjiTable`, so no table was added. A pair with no cell is `UnmappedCharacter`, a declared segment that is not well formed is `InvalidBitstream`, and the destination is counted in characters before anything is written. All three symbologies share it.
- The rule (D2) is zxing-cpp's with three clauses left out and tab counted as text. It is recorded with its costs in [standardqr-decoder.md](../specs/standardqr-decoder.md#decisions), Byte-mode charset heuristic.
- The encoder needed less than planned. Standard QR and rMQR already write ECI 3 wherever a byte is past ASCII, so the guess never sees their output, and no ECI 3 rule was added. Micro QR has no ECI: text whose ISO-8859-1 bytes the decoder would read as UTF-8 or Shift_JIS is now written as UTF-8, and the planner refuses a Latin-1 run that reads otherwise once a split isolates it. `SegmentDecoders.ResolvesToIso8859_1WhenUnspecified` is the mirror both ask.
- Tests, written before the code: `ByteSegmentShiftJisTest` (each class of the rule, the cases that must stay ISO-8859-1, ECI 20 in Standard QR and rMQR, another encoder's symbols, this library's own Standard QR and rMQR round trips), `MicroQRUndeclaredCharsetTest` (every pair of characters from U+0080 to U+00FF, alone and between ASCII letters), and `ByteSegmentCharsetParityTest` with the Shift_JIS rule written out a second time. Two tests that used ECI 20 as their unsupported ECI use ECI 4.
- The full suite passes on net8.0 and net10.0 (19,090 and 19,184 tests). The netstandard2.0 and netstandard2.1 builds, which no test project targets and whose UTF-8 step is other code, pass a probe through the public API: Shift_JIS with and without ECI 20, French and UTF-8 text without one, and the three symbologies' round trips.
- `tools/QRImageDecodeSweep` pins the pixel buffer and holds the ZXingCpp wrapper's objects through each read ([qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#lessons-learned)).
- Documents: the decision row, coverage table and Photographs section of the Standard QR decoder record, the Micro QR and rMQR records and the Micro QR spec map, the scope row and status rule in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the Micro QR rule in [standardqr-encoder.md](../specs/standardqr-encoder.md#mixed-mode-segmentation), the tables in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands), the README's encoding FAQ, the decoder XML docs, and two sections in `docs/migration.md`.

Reads:

| Set | Before | After | zxing-cpp |
|---|---|---|---|
| ZXing's photographs, upright | 151 of 179 | 162 | 165 |
| The same, four turns | 606 of 716, 44 with another text | 650, none | 660 |
| Committed corpus, Standard QR, four turns | 425 of 548, 44 with another text | 465, 4 | 489 |
| Sweep, 113,760 renders | | the same pixels and the same reads | |
| BoofCV | 322 of 536 | 322, photograph for photograph | 390 |

The corpus's 4 are `qrcode-2/16` at its four turns, an image of two symbols. This library reads the one the expected text does not name, with the text zxing-cpp gives for it. What is left on ZXing's set upright is four image failures and one read zxing-cpp misses.

The guess on other encoders' symbols, written by ZXing.Net with no ECI header from the .NET SDK's own messages (10.0.301, about 7,600 a language):

| Text | Units | Not read as written | zxing-cpp |
|---|---|---|---|
| ISO-8859-1, five languages, messages | 24,555 | 0 | 19 |
| The same, lines | 25,995 | 0 | 26 |
| The same, words | 7,156 | 1 | 68 |
| Shift_JIS, Japanese, messages / lines / runs between ASCII | 7,375 / 7,995 / 11,705 | 0 | 2 / 0 / 0 |
| Windows-1252 with a byte from 0x80 to 0x9F, messages | 1,866 | 345 turned from one character a byte into Japanese, 21 of them refused | the same 345 |

The one word is `é`, a no-break space and `»`, whose bytes E9 A0 BB are valid UTF-8, read so before this phase as well. With zxing-cpp's three-in-a-row clause this library misread 13 French messages, 18 lines and 13 words more. zxing-cpp's two Japanese messages hold tabs.

Cost (Ryzen 9 7950X3D, .NET 10 JIT, a probe on two pinned cores, the fastest of 600 batches in each of four runs a tree, the trees taken in turn, nanoseconds):

| Operation | Before | After |
|---|---|---|
| Micro QR encode, numeric, M2 | 127 to 138 | 126 to 131 |
| Micro QR encode, ASCII in Byte mode, M4 | 169 to 172 | 170 to 177 |
| Micro QR encode, `café à Zürich`, M4 | 165 to 168 | 178 to 185 |
| Micro QR decode, ASCII in Byte mode, M4 | 284 to 287 | 280 to 283 |
| Micro QR decode, `café à Zürich` | 291 to 299 | 291 to 295 |
| Standard QR decode, a URL, version 6 | 982 to 1,015 | 992 to 1,019 |
| Standard QR decode, accented text under ECI 3 | 671 to 705 | 677 to 700 |
| Standard QR decode, ASCII, version 40 | 15,818 to 16,455 | 16,064 to 16,600 |

One row moved: Micro QR text with a character past ASCII pays about 13 ns for the question it now asks the decoder's rule. The rest are level within the runs' spread. BenchmarkDotNet in process could not settle it that day, its runs of one tree differing by up to 20 % with other sessions on the machine. No allocation was added: `MemoryDiagnoser` reports none on the four span benchmarks run, and `ByteSegmentShiftJisTest` holds the guess and the Shift_JIS reading to none.

Lessons:

- A rule taken from another reader brings that reader's misreads. zxing-cpp's "three double-byte characters in a row" reads French `générée` as kanji, since é before a letter is a well formed pair. A corpus in the other language found it at once (13 of 5,354 French messages), and leaving the clause out lost none of 27,075 Japanese units. "As zxing-cpp's" was where the rule started, and each clause stayed only where a measurement kept it.
- The corpus was already on the machine. The .NET SDK ships its messages in thirteen languages as resource assemblies, which gave real prose in five ISO-8859-1 languages and Japanese with nothing downloaded.
- The exit's round trip found a misread older than the guess. Micro QR wrote `Ã©` as C3 A9 and its own decoder returned `é`, under the UTF-8 rule that had been there from the start. The plan had assumed the encoder work was an ECI 3 rule, which Standard QR and rMQR turned out to have already.
- A new reading can break the inequality behind an early return. The one-pass transcoder could answer "too small" before validating, because UTF-16 units never outnumber UTF-8 bytes or ISO-8859-1 bytes. A Shift_JIS reading needs a character a pair, so that answer now holds only where the guess does not.
- A conversion at a rarely run call is not inlined, and every call of the method pays for it. The first build decoded a Standard QR Byte symbol 2.5 to 3.5 % slower with nothing new on its path. `Span<byte>` to `ReadOnlySpan<byte>` at the five new cold calls had stayed five calls, the bit-stream decoder's frame grew from 536 to 648 bytes, and its prolog zeroed the frame in a loop. Converting once, where the bytes are read, put the frame at 552 bytes and the times back.
- A probe times nothing until every operation in it has reached its last tier. Timed after its own warm-up alone, the first operation read 630 ns in both trees against BenchmarkDotNet's 127, because the methods it shares with the others were still instrumented. All operations are warmed, then all are timed.
- A total that moves between runs of one build is a fault, not noise. zxing-cpp's BoofCV code count read 900, 960, 965 and 964 while every per-photograph figure held, and the cause was this tool's handling of the wrapper's lifetimes. The published 900 was wrong by 75.
- A residual gets its cause confirmed like any other. The four corpus reads still "another text" were taken for a second symbol, and were checked: zxing-cpp returns two symbols from that image, one with this library's text.
- A measurement can reopen a decision the plan had closed on a guess. D3 kept ISO-8859-1 "until a set holds a symbol that needs Windows-1252". The same SDK messages hold 1,866 such texts, and the 0x80 to 0x9F clause, the one that reads `100円`, turns 345 of them into Japanese or a refusal. Neither reading is the text, and zxing-cpp does the same, so the rule stands, and D3 was decided again with its numbers.

### Phase 4, first stage: the reduced-scale search (2026-10-10)

Done:

- `ImageDecodePasses` reads a Standard QR image again at reduced scale when nothing settles at full size: the image halved by `LuminanceHalver` (each pixel the rounded mean of a two by two block, four pixels a step in one 64-bit word), every pass on it, and halved again until its shorter side would be under 64 px. A read reports its corners in the pixels of the image given. The levels share the inverted image's buffer, so nothing more is rented.
- An image of only 0 and 255 is not searched, and Micro QR and rMQR do not have the search. Both are measured choices, recorded with their numbers in [standardqr-decoder.md](../specs/standardqr-decoder.md#decisions), Reduced-scale search.
- Tests, written before the code: `LuminanceHalverTest` (the definition at every width, rounding, odd sides, in place), the search's rules in `ImageDecodePassesTest` through its recording pass, `ReducedScaleDecodeTest` on a renderer of texture finer than a module (`FineTextureRenderer`), and a scene in `DecodeAllocationTest`. Sixteen planted faults, run in a copy of the tree, were all caught.
- Documents: the passes in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#image-decode-passes), the decision row, the image-level text and Photographs in the Standard QR record, its spec map, the tables in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands), and the README and XML sentences that named screens.
- The full suite passes on net8.0 and net10.0 (19,141 and 19,235 tests).

Reads, against the build before it:

| Set | Before | After | zxing-cpp |
|---|---|---|---|
| BoofCV, photographs with an agreed read | 322 of 536 | 371 | 391 |
| The same, gap and reverse | 77 and 9 | 40 and 20 | |
| ZXing's photographs, upright | 162 of 179 | 164 | 165 |
| Committed corpus, Standard QR, four turns | 465 of 548 | 470 | 489 |
| Sweep, Standard QR | | 7 more camera renders of 57,600, none lost | |
| Sweep, Micro QR and rMQR | | the same pixels and the same reads | |

No read returned a text other than the expected one or one another reader contradicted. ZXing's `13.txt` is one letter short of its symbol, so the tool counts 163 there.

Cost: nothing on a photograph that read before, 1.28 times on one gained, 1.53 times on one that still fails (BoofCV, both builds timed in turn). The other multiples are in the decision row.

Lessons:

- Most of a failing photograph's time is not in the symbol passes. On a 12-megapixel photograph the histogram took 4.6 ms and the two regional binarizations 16.6 ms, against 0.9 ms for a symbol pass. The estimate "a third more pixels, a third more time" was wrong because of it: the search costs its levels' image-wide steps and the halving, and a halver 2.3 times faster took the failing multiple from 1.68 to 1.53.
- What a failing image pays depends on why it fails. An image where nothing is found pays by the pixel. One where a symbol is found and not corrected pays a decode at every level, whatever its size: a damaged symbol drawn crisp cost 2.4 to 2.9 times with the search.
- An exclusion is checked against the gains before it is written down. "No black and white image gained anything" was assumed from the sweep, and the photograph set had two that did, drawn symbols with altered finders that read at half scale through a regional pass. The rule stayed, for its cost, and the two are its stated price.
- Which pass reads a gained image decides what the stage needs. Through the public API a level only reads or does not. A recording pass around the decoder's own core showed 28 of the 51 reads coming from a level's regional pass, so a search of global passes alone would have gained less than half.
- A set's expected text can be wrong where no reader had read the image. ZXing's `13.txt` says "photograph", the symbol says "photography", and zxing-cpp's copy of the file had already been corrected. The other two readers confirmed it once given the reduced image.
- Timings taken hours apart do not compare. Two timed runs of the final build put the photographs that read both ways at 1.14 times, with zxing-cpp's own total up by a fifth: the machine, not the change. Both builds timed in turn, each photograph at its faster of two runs, gave 1.00.

What the next stage starts from: the 40 photographs left in BoofCV's gap, classified again on this build (2026-10-10). Each was read scaled, cropped to a symbol zxing-cpp found and as a grid through zxing-cpp's corners, and its finders were looked for among the candidates of the global pass at full size:

- 32 read as the grid through zxing-cpp's corners, 13 cropped to the symbol, and 19 at some other scale (0.66, 0.75, 1.5 or 2), which the halves do not reach.
- In 17 the global pass has two of the symbol's three finders among its candidates (5 in `glare`, 4 in `pathological`, 3 in `high_version`), and in 4 it has one. A third finder inferred from two is the technique aimed at them.
- In 10 it has none (7 in `brightness`, small symbols of 3 to 4 px a module in 12-megapixel scenes, 5 of which read cropped). The threshold of the whole scene is the suspect, and why the regional pass does not read them is not looked at yet.
- In 9 it has all three. In the 4 of `curved` they are the selected triple and the grid fails, on version 1 symbols at 10 to 46 px a module. In the 2 of `lots` the candidate list is full (32) and the triple mixes symbols.

The probe looked at the global positive pass only, so the counts say where to look first, not what each stage will gain.

### ARM64: the branch as it stands (2026-10-11)

Done: the branch at 5b6884c was run on an Apple M2 (osx-arm64, SDK 10.0.401, runtimes 8.0.31 and 10.0.12). Phase 4b is not built, so the halving has no tier to measure yet. What was checked is Phase 3 and the first stage of Phase 4 as they stand, and the figures Phase 4b will be measured against on this build. No code changed, so no read or cost moved.

- The full suite passes on net8.0 and net10.0 in Release (19,141 and 19,235 tests, the counts of Phase 4's first stage), in Release with `DOTNET_EnableArm64Dp=0`, and in Debug (19,055 and 19,149). Of the 569 and 661 skips in Release, 564 and 656 are tests of x64 and WebAssembly tiers, and the other 5 have nothing to check on this build.
- `tests/FeatherQR.AotAnalysis --simd-class Arm64 --parity` passes as four builds: the JIT, the JIT with `DOTNET_EnableArm64Dp=0`, a default NativeAOT publish and one for `armv8-a,-dotprod`. All 21 parity cases match in each, and both publishes pass the trim and AOT analysis with no warning.
- `corpus` reads the committed photographs as the record has them, with the same result file in five runs. `sweep all` (six Standard QR encoders, no qrtool, 7 min 27 s on eight cores) returns no text other than the expected one, and this library's column equals the x64 record in every row the records hold: the 23 Standard QR rows of the kinds before the camera, the totals of all three symbologies, and all 24 camera rows as first measured once Standard QR's 7 gained renders are added.
- Through the netstandard2.0 and netstandard2.1 builds of both packages, put in place of the net10.0 assemblies under the tool, `corpus` writes the same file byte for byte. That runs the Shift_JIS guess on the corpus's photographs and the reduced-scale read of `qrcode-2/13` on the builds no test project targets.
- Against the library at aa004d5, the build before the reduced-scale search, put under the same tool, `compare` pairs every image with the same pixels and finds the gains of the x64 record and no read lost.

Reads, on this build:

| Set | Before the search | With it | zxing-cpp |
|---|---|---|---|
| Committed corpus, Standard QR, four turns | 465 of 548 | 470: `qrcode-2/13` at its four turns and one more turn of `qrcode-4/15` | 489 |
| Committed corpus, Micro QR and rMQR | 64 of 64 and 12 of 12 | the same | 60 and 12 |
| Sweep, Standard QR, 57,600 | 54,946 | 54,953: 7 more camera renders (blur 1, barrel 1, noise 3, phone mix 2) | 51,508 |
| Sweep, Micro QR, 21,600 | 20,450 | the same pixels and the same reads | 14,762 |
| Sweep, rMQR, 34,560 | 33,149 | the same pixels and the same reads | 8,143 |

Cost (Apple M2, .NET 10 JIT, the fastest of 15 batches in each of three processes, the two builds taken in turn for the multiples). The halving alone on 12 megapixels, beside the table under "The halving's vector tiers":

| Step | M2 | Ryzen 9 7950X3D |
|---|---|---|
| Halving, a byte at a time | 2.4 ms | 3.7 ms |
| Halving, a word at a time (shipped) | 1.3 ms | 1.6 ms |
| `LuminanceInverter` over the same bytes | 0.28 to 0.47 ms, its 128-bit tier | 0.23 ms, its 256-bit tier |

An image with no symbol, as a multiple of the build before the search:

| Image | M2 | x64 record |
|---|---|---|
| 740 px, uniform noise | 1.66 | 1.65 |
| 740 px, blurred noise | 1.68 | 1.80 |
| 740 px, a ramp | 1.53 | 1.49 |
| 12 megapixels, a blurred scene | 1.60 | 1.53 on BoofCV's failing photographs |

The images are this probe's own, not those of the x64 probe, so a row compares a class and not an image. The machine carried other load (a load average of 5), and the cycle counts give the same multiples within 0.02.

The halving's share, its time alone scaled to the pixels of each level over the image's time, is about 5.5 % of the 12-megapixel scene and 7.6 % of the ramp, against the estimates of 6 % and 8 % above. Both are over the 3 % bar on ARM64 too. The word-at-a-time loop takes 2.7 to 4.6 times the inverter's time here, where x64 has 7 times, so a tier has less room on this build.

Not covered: ZXing's photographs and BoofCV's are not on this machine, so `photos` and `boofcv` were not run. linux-arm64 and win-arm64 are CI's legs, and no run exists for this commit. The NativeAOT gate is net10.0 only, so no .NET 8 publish was checked.

Lessons:

- A managed reader's column says whether two machines drew the same images. zxing-cpp's column differs from the x64 record here: 60 of the Micro QR corpus against 59, five Standard QR rows by 1 to 6 renders, three rMQR camera rows by 1 to 3, and the totals of the kinds before the camera by 21 for Micro QR and 26 for rMQR. ZXing.Net's column equals the record in all 23 Standard QR rows, and so does this library's, which points at the native reader's build and not at the renders. The cause was not looked for, and without the x64 result file the pixel digests could not be compared.
- A skip count is read before a pass is believed. The first run skipped 12 tests more, each saying the core's netstandard builds were older than the source, because building the test project builds the core for its own two frameworks only. Built for every framework, they ran and passed.
- The tool that compares readers also runs another build of the library. It calls only the public API, so the netstandard assemblies, or an earlier commit's, take the place of the net10.0 ones under it. Phase 3 wrote a probe by hand for the netstandard builds, where `corpus` holds them to 624 reads of real images.

What this leaves open in the records, as found: the corpus gap in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands) is 27 in its table and 28 in the causes below it, which still list `qrcode-4/15` at 270° that the search now reads. The sweep's zxing-cpp total and reverse for Standard QR are 48,739 and 3,606 at the top of this plan and 48,738 and 3,607 there. This build gave 48,739 and 3,606.

### Phase 4b: the halving's vector tiers (2026-10-11)

Done:

- `LuminanceHalver` has a 256-bit tier (`LuminanceHalver.Vector256.cs`, 64 pixels of each row a step), a portable 128-bit tier (`.Vector128.cs`, 32) and a WebAssembly tier (`.Wasm.cs`, the same step on the platform's shift and narrowing), dispatched in that order ahead of the scalar word loop, which ends every row and is the netstandard builds' tier. Each step loads both rows before it stores, and no row ends on a step laid over pixels already written, so a level halved in place reads nothing it wrote.
- The kernel's row in `SimdTiers.cs` (`Vector256`, `PackedSimd`, `Vector128`; x64 without AVX `Vector128`, with AVX2 `Vector256`, ARM64 `Vector128`, WebAssembly `PackedSimd`), its file in `SimdTiersTest`, and the rendered row in [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md).
- Tests, written before the code: each tier through its own entry against the definition at every width to 200, out of place and in place, and each held to its spans at a page end (`PageEndMemory` gained byte spans for it). A `--parity` case in both report projects, each tier against the scalar one, out of place and in place. The halving's kernel pairs in `--time`, at 740 px and 12 megapixels.
- Measured on x64 (JIT with and without AVX, a default NativeAOT publish, `x86-64-v3`) and WebAssembly (interpreted and AOT-compiled), each build as itself, the kernel alone and the no-symbol shapes against the build before; the numbers are in the Standard QR record ([Lessons learned](../specs/standardqr-decoder.md#performance), the halving). Every build class this machine runs takes a tier that beats its scalar form by 3.2 to 8.7 times and loses nowhere.
- The search's own method compiles as before on every build read, and the scalar tier's word loop is one instruction shorter than the shipped method's.
- The photograph set, timed with three builds in turn (before the search, the word loop, the tiers): the 163 failing photographs at 1.59 and 1.58 times the build before, where the first stage recorded 1.53 from a two-build run. The 322 that read at full size, which the halving never touches, moved 6 % between the word loop and the tiers, so the tiers' expected 4 % on a failing photograph is under what the set resolves. The 1.53 stands as the search's cost, and the tiers' gain is the kernel pairs' figure.
- The full suite passes on net8.0 and net10.0, and `--simd-class` with `--parity` on the JIT with and without AVX, both native publishes and both WebAssembly modes.
- ARM64 was not timed on this machine. The table declares the portable 128-bit tier for it, and the next entry, the maintainer's run on the Apple M2, times it at 0.21 to 0.22 of the word loop and folds it into the records.

Lessons:

- A portable operation can be the slowest thing on one build and fine on the next. The first 128-bit tier ran 6.5 times faster than scalar on the WebAssembly interpreter and 4.4 times slower compiled ahead of time, where `Vector128.ShiftRightLogical` on 16-bit lanes is a lane walk. One copy per swapped operation found it in one publish each, and the rule that a tier wins on both WebAssembly modes is what made the measurement happen.
- A file is named for the families it uses, and the layout test holds a mixed file to that: a 128-bit method choosing the platform's shift inside, written as `.Simd.cs`, used only WebAssembly's family beside portable vectors and so belongs in `.Wasm.cs`. The split, one method a family, left each file honest and the dispatch one test longer.
- A comparison of the halved image does not see a step one pixel past the row. The fault wrote a byte past the destination and read two past the source, invisible on the heap, and was caught only by spans that end where readable memory ends, a page-end check the writers already had and the halver did not.
- The plan said no report shape fails to read, so a new one would be needed. Two did, the no-symbol noise and ramp, which the sweep's camera work had never looked for. The shapes were read before anything was added.
- Timings of a 12-megapixel step on this machine vary by a fifth to a half between runs, the buffers being larger than the cache of whichever core the process lands on. The 740 px pair, which fits, holds within 7 % on all but one build, so the kernel's tier ratio is read off the small pair and the large one gives the size.

### Phase 4b on ARM64: the 128-bit tier timed (2026-10-11)

Done: the branch at 867cc95 was run on the Apple M2 (osx-arm64, SDK 10.0.401, runtimes 8.0.31 and 10.0.12). ARM64 runs the portable 128-bit tier on every build read, the tier takes 0.21 to 0.22 of the word loop's time with the kernel alone, and no read moved. No code changed. The 12-megapixel scene with no symbol costs 1.54 times the build before the search, where the word loop cost 1.61.

- The full suite passes on net8.0 and net10.0 in Release (19,149 and 19,243 tests), in Release with `DOTNET_EnableArm64Dp=0`, and in Debug (19,063 and 19,157).
- `tests/FeatherQR.AotAnalysis --simd-class Arm64 --parity` passes as four builds: the JIT, the JIT with `DOTNET_EnableArm64Dp=0`, a default NativeAOT publish and one for `armv8-a,-dotprod`. Each reports `Vector128` for the halving and matches all 22 parity cases, the halving's among them.
- Reads: `corpus` and `sweep all` on this build write the files the word-loop build at 5b6884c wrote, byte for byte, 624 reads of real images and 113,760 renders.
- .NET 8's JIT was timed and read through a copy of the report project retargeted to net8.0 in a scratch tree. The project is net10.0 only, and two of its other probes call `Vector128.ShuffleNative` and `ConvertToInt32Native`, which the copy swaps for `Shuffle` and `ConvertToInt32`.

The kernel alone, the tier through the dispatch against the scalar entry (`--time`, the pairs in turn within a process, the fastest of five processes of eleven rounds):

| Build | 740 px, tier | Scalar | Ratio | 12 megapixels, tier | Scalar | Ratio |
|---|---|---|---|---|---|---|
| JIT, .NET 10 | 13.9 µs | 65.4 µs | 0.21 | 0.31 ms | 1.40 ms | 0.22 |
| JIT, .NET 8 | 14.0 µs | 66.5 µs | 0.21 | 0.31 ms | 1.42 ms | 0.22 |
| NativeAOT, default | 13.9 µs | 66.1 µs | 0.21 | 0.31 ms | 1.41 ms | 0.22 |

The ratio of one process is 0.20 to 0.22 at 740 px and 0.21 to 0.23 at 12 megapixels in all fifteen. A process's median is up to 18 % over its fastest round and within 10 % in 56 of the 60 timings, the machine carrying other load.

The same entries by cycle count (4,000 by 3,000 pixels of noise, .NET 10 JIT, a probe reading `proc_pid_rusage`, three processes within 4 %):

| Step | Cycles | Instructions |
|---|---|---|
| Halving, a byte at a time | 9.2 M | 69.1 M |
| Halving, the word loop | 4.7 M | 30.8 M |
| Halving, the 128-bit tier | 1.0 M | 6.3 M |
| `LuminanceInverter` over the same bytes | 0.88 M | 5.3 M |
| A copy of the same bytes | 0.82 M | 1.5 M |

Images with no symbol, three builds in turn (before the search at aa004d5, the word loop at 5b6884c, the tiers), as cycles a call against the build before the search, the least of three processes, .NET 10 JIT. The images are the ones of the ARM64 entry above:

| Image | Word loop | Tiers | Tiers over word loop |
|---|---|---|---|
| 740 px, a ramp | 1.53 | 1.43 | 0.93 |
| 740 px, uniform noise | 1.64 | 1.63 | 0.99 to 1.00 |
| 740 px, blurred noise | 1.68 | 1.66 | 0.98 |
| 12 megapixels, a blurred scene | 1.61 | 1.54 | 0.96 |

On every image each process of the tier build counts fewer cycles than any process of the word-loop build. The gain is what the kernel pair predicts: the cycles the tier saves on each level's pixels are 6.6 % of the ramp and 4.5 % of the scene. The report project's own shapes, by time, give 0.94 to 0.95 on `image/none-gradient` and 0.99 on `image/none-noise` on the three builds, each build at the fastest of five processes.

By the same counts the halving is 2.0 % of the ramp and 1.3 % of the scene with the tier, where the word loop was 8.5 % and 5.8 %. The ARM64 entry above gave 7.6 % and 5.5 % from times and the 12-megapixel kernel alone.

The machine code, read on the JIT of .NET 10 and .NET 8 (tiered compilation off) and on ILC:

- The search's own method, `DecodeReduced`, compiles to the same listing as before on all three: 776 bytes on .NET 10, 812 on .NET 8, 146 instructions on ILC.
- The scalar tier's word loop is 41 instructions on all three, the same ones in `HalveScalar` as in the shipped `Halve`, branch labels aside. It is not the one instruction shorter that x64 recorded, and it costs none for the tiers above it.
- The tier's loop is 31 instructions for 16 output pixels on all three: four loads, four `and`, six `ushr`, eight `add`, `xtn` and `xtn2`, a store, and six for the index and the branch. `Vector128.Narrow` and each 16-bit shift compile to their instructions on .NET 8 as on .NET 10, so nothing falls back as the shift did on WebAssembly.

Not covered: BoofCV's and ZXing's photographs are not on this machine, so the failing photographs' multiple was not measured again here, and the scene stands in for them. linux-arm64 and win-arm64 are CI's legs. No NativeAOT publish for .NET 8 was built. The tier uses no dot-product instruction, so the builds without it were held to parity and not timed.

Lessons:

- Cycle counts resolve what time between two processes does not on this machine. The report project's ramp, paired process by process, read 0.85 to 1.04 on the NativeAOT build for a gain of 0.94, the clock of the core a process lands on moving more than the tiers do. Cycles a call held within 3 % between processes and separated the two builds on every image. x64 found the tiers' 4 % under what its photograph set resolves, and this is the method that showed it here.
- A portable tier is held against a copy of its input before a platform form is thought about. The tier passes 12 megapixels in 1.2 times the cycles of a copy and is 2 % of a failing image at most, under the 3 % bar. A step on ARM64's pairwise widening add and rounding narrowing shift would be shorter, and no measurement could put it over the bar, so it was not built.
- An operation one build compiled badly is read on the next build, not assumed. `Vector128.ShiftRightLogical` on 16-bit lanes was a lane walk on WebAssembly compiled ahead of time, and .NET 8 had run `Vector128.Shuffle` through a fallback on ARM64 before. The loop was read on .NET 8, .NET 10 and ILC before its time was believed: the same 31 instructions on each.

### Phase 4, second stage: the third finder inferred from two (2026-10-11)

The 40 photographs of the gap were read again stage by stage, in every pass of both polarities at every level, with zxing-cpp's corners as the truth of each symbol's three finder centres (a temporary probe test, deleted):

- 16 had two of a symbol's three finders among the full sweep's candidates and never the third, in every pass and level: four under glare (the finder under the glare), two of the drawn `pathological` symbols, two `nominal`, and one each of `curved`, `perspective`, `brightness` and `high_version`; and four with one finder or none (`brightness/image012`, 17 px/module in a 12-megapixel scene, found nothing at any level).
- 14 selected the right triple and failed in the grid, every one `DataUncorrectable`: the four curved version 1 symbols at 10 to 46 px/module (the grid through zxing-cpp's four corners reads each), five of version 25 to 40 at 5.5 to 8 px/module (that grid does not read them: the alignment mesh is what reads them), and five more.
- 8 had all three finders among the candidates and selected a triple of another shape: five `brightness` scenes of symbols at 3 to 4.5 px/module in 12 megapixels, where the stride pass (17 rows) confirms a finder on one row or none and takes a triple of three candidates without scanning the rows between, and the two `lots` scenes of 58 and 60 symbols at the 32-candidate cap.
- 2 are the drawn black-and-white symbols the reduced-scale search leaves out by design.

The premise, before anything was built: each two-finder photograph was decoded from its two candidates and a third finder placed three ways. From the true centre, 11 of 12 read. From the construction (the perpendicular of the pair's edge from the top-left finder, or half of it from the diagonal's midpoint), 6: exact on the drawn symbols and within 0.7 module elsewhere, 1.5 to 1.9 modules out on the four glare photographs, past the 0.75 the centre refinement reaches. From the position within 2.5 modules of the construction whose two timing patterns read best through the frame, 10, the four glare photographs among them. go-qr's `completeSymbol` was read up to the call that gates it (its fit score on timing and alignment patterns); the constructions are its, the search is not.

Done:

- `QRImageDecoder.DecodeInferred` (`QRImageDecoder.InferredFinder.cs`): after the selected triple and the alternatives fail, or when the scan selects none, and every row was scanned, the four candidates confirmed over the most rows are paired; each pair's six constructions are searched a module at a time to two modules out, then a quarter module within half a module, for the centre whose timing patterns read best through the frame; the two under an eighth wrong are decoded through the grids from the finders, the inferred finder's sizes taken from the real finder across the symbol's edge, its centre neither measured nor refined. `FinderPatternFinder.TryFind` now says whether every row was scanned.
- Tests, written before the code (`InferredFinderDecodeTest`): each finder blanked in turn, upright and turned, with the premises asserted (the two others are candidates, the blanked one is not, no triple is selected) and the corners within half a module; under 0.18 keystone, where the construction is over 0.75 module out, the read through the search, the corner at the inferred finder held to three modules; the negative, the data destroyed, reads nothing. The allocation test's `InferredFinder` scene. `UnevenLightingDecodeTest`'s premise list moved: three ramps read at the global threshold now.
- Records: the Standard QR decoder record (image level, Decisions, Lessons), the spec map, the passes in the symbologies record, the fixtures record's tables.

Measured against the commit before (11d0ff9), image for image:

- BoofCV: 371 to 400 of 536 (zxing-cpp 391); the gap 40 to 28, the reverse 20 to 37; 29 gained and none lost: glare 9, `pathological` 10, damaged 3, curved 2, `noncompliant` 2, and one each of `bright_spots`, `brightness` and `nominal`. Ten of the 16 two-finder photographs read; the rest of the 29 are photographs neither reader read before.
- ZXing's photographs: 653 to 657 of 716 (`qrcode-4/19` at 0° and 180°, `qrcode-2/12` at 90° and 270°); upright 164 to 165 by the record's count.
- The corpus: 470 to 474 of 548 (`qrcode-3/30` at 270°, `qrcode-2/12` at 0° and 180°, `qrcode-4/14` at 180°).
- The sweep: 46 more anti-aliased renders at 1.0 to 1.25 px/module, nothing else moved, nothing lost, no text other than the expected one anywhere.
- The keystone envelope on the test's version 3 at 6 px/module: reads to 0.20, not past 0.25, every finder and turn measured at 0.05 steps.
- Cost, the BoofCV photographs timed on both trees in turn (`boofcv --time`, each call the fastest of five, two rounds; the baseline's two rounds within 3 % of each other): the 371 that read before 0.94 to 0.97 (median 1.00), the 29 gained 0.64 to 0.67 (median 0.79 to 0.82: they read at full size where every pass and level ran before), the 136 that still fail 1.01 to 1.04 (median 1.02 to 1.04, 90th percentile 1.17 to 1.21, worst 2.6 on `nominal/image001`, 0.7 to 1.7 ms). The set: 2.5 s against 2.5 to 2.6 s, zxing-cpp 5.7 s. The report project's no-symbol shapes at 740 px (`image/none-noise`, `image/none-gradient`, .NET 10 JIT with AVX2, five processes each): 1.00 and 1.01.

Lessons:

- The construction alone read 6 of the 12; the search read 10. Where the other reader's code was read up to its gate, its fit was not copied: the timing patterns through this decoder's own frame are the gate it already had for a triple, and they rank the positions too.
- A size ratio for the lines through the inferred finder, searched together with the position, was built, measured and removed: the envelope and the corners did not move. A ratio at a fixed position never improved the timing share either, so the first attempt, a ratio pass after the position pass, could not have been told from nothing.
- The first classification, at the global pass alone (end of the first-stage entry), counted 17 two-finder photographs; per pass and level the count is 16 with other members, and the largest class was the grid with the right triple (14), which the global-pass count had hidden. A class is counted from the pass that holds it.
- A stage that reads more moves the premises of tests pinned to a later pass. The three ramps are the inference's case at the global threshold (the dark corner's finder lost, two kept), and the list of what the global threshold reads is where they went.
- Ten faults planted in the bounds and gates (`tools/mutation_check.cs`, the inference's and the uneven-lighting tests as the filter), each moved so the inference does less: seven caught (the four paired, the pair distance, the coarse search, the timing bar, the confirmation floor, the decode count, the tie rule). Three survived. The every-row gate inverted (the inference run on a stride list and not on a complete one): no test has a triple that failed on a complete list, and the only photograph that does is `brightness/image026`. The diagonal's handedness inverted: top-right and bottom-left swapped is the transposed symbol, which the mirror retry reads and whose corners it reports the right way round, so the handedness only saves that retry. The quarter-module search removed: no test told, and the photographs did, 394 of 536 against 400 (seven lost, one gained); a sweep of every finder, turn and keystone found 46 renders that read only with it, and three of them are the test now.

What is left, the 28, by the stage that loses them: the selection on a crowded or 12-megapixel scene, 8 (`brightness` 5, `lots` 2, `high_version/image030`); the grid with the right triple where the four-corner grid reads, 7 (`curved` 4, `nominal/image004`, `noncompliant/image008`, `shadows/image013`); the alignment mesh of version 25 and up at 5.5 to 8 px/module, 5; two finders found but not read from the true third centre either, 4 (`brightness/image020`, `high_version/image010`, `nominal/image001`, `perspective/image013`); one finder, 3; none, 1. The next sub-phase takes the largest.

### Phase 4, third stage: the selection on a crowded or 12-megapixel scene (2026-10-11)

The eight photographs of the selection class, read again stage by stage with zxing-cpp's corners as the truth (a temporary probe, deleted): the strided scan's list and triple, the full sweep's list and its own selection, the alternatives in their order, and each triple decoded through the decoder's corner logic.

- Five `brightness` scenes, symbols of 3.2 to 4.5 px/module in 3,024 by 4,032 px: the stride of 17 rows crosses a finder's 10 to 13 px centre band once or not at all, so the strided scan selects from three to six candidates seen on a row or none, takes a triple (three candidates are taken unscored, and more than three select on counts of one) and returns without the rows between; the triple fails, the two alternatives fail, and nothing scans the rows. The full sweep's list (8 or 9 candidates, all confirmed) holds every finder, and its own selection reads four of the five at the regional pass. The fifth (`image021`) selects a false triple from that list too, with the real triple the 35th alternative.
- Two `lots` scenes, 58 and 60 symbols of 4.2 to 9.9 px/module: the 32-candidate list holds 10 complete symbols, and the selection takes a triple spanning three of them in every pass and level; among the alternatives in their order the first real triple sits 11th, 20th or past 40. Of the 4,960 triples of the 32 confirmed candidates, 291 score under 0.5, and ordered by their longest side the ten real triples are the first ten: every one passes the timing-pattern gate and reads, and no triple spanning symbols passes it in the first 256.
- `high_version/image030`, a version 40 at 5.4 px/module in 3,345 by 1,299 px: the full list holds the symbol and selects another triple; the real one is the 20th alternative and no corner of it passes the gate. Not read.

Done:

- `FinderPatternFinder.RowStride`, `IsUndersampled` (a triple whose smallest finder's 3-module band is shorter than two strides, a candidate under 2 px/module taken at 2: the first form had no floor and doubled the report project's 740 px noise, whose candidates are 1.0 to 1.9 px/module at stride 3) and `TryFindSkippedRows` (the strided scan's complementary rescan, for a caller whose strided triple failed; the scan itself is unchanged, the loop moved into `ScanSkippedRows`).
- `QRImageDecoder.DecodeLuminanceCore`: when the strided triple and its alternatives fail and the list is the stride's, the skipped rows are scanned if the triple is undersampled or the list crowded (nine confirmed candidates), the completed list is selected from again and that triple and its alternatives decoded, then the crowded stage, then the inference.
- `QRImageDecoder.DecodeCrowded` (`QRImageDecoder.Crowded.cs`): on a list of nine or more confirmed candidates, the triples scoring to 0.5 kept most compact first (256), walked through the timing-pattern gate (64), the first four that pass decoded.
- Tests, written before the code (`TripleSelectionDecodeTest`): a 600 by 4,000 px two-level image with a 4 px/module symbol whose top finders the stride crosses once and whose bottom-left one it misses, beside a lone finder pattern it crosses, so the strided scan selects the two and the lone one unscored (premises asserted: every row not scanned, the triple not the symbol's, the full sweep holding all three) and the symbol reads after the skipped rows; its negative with the data destroyed. A 3 by 3 and a 4 by 2 grid of version 1 symbols under 0.12 and 0.15 keystone, where both the full sweep's and the strided scan's selections span symbols (asserted) and the image reads.
- A compactness tie rule in the selection itself was built first, read the `lots` photographs, failed the keystoned grids (the crossroads triple of three symbols is more compact than any symbol's own, and under 0.15 keystone the own triples score outside the tie band), and was removed with its unit test.

Measured against the commit before (0cccf23), image for image:

- BoofCV: 400 to 406 of 536 (zxing-cpp 391); the gap 28 to 22, the reverse 37; six gained and none lost: `brightness/image013`, `015`, `017`, `019`, `lots/image003`, `lots/image006`. `brightness` 21 to 25 of 28 (zxing-cpp 27), `lots` 5 to 7 of 7.
- ZXing's photographs and the corpus: unchanged (657 of 716, 474 of 548).
- The sweep: unchanged, nothing lost, no text other than the expected one. Before the 2 px floor it had gained 43 anti-aliased renders at 1.0 to 1.25 px/module, 6 at 1.25 to 1.5 and one phone-mix camera render: the floorless rule swept every failing pass whose candidates were under 2 px/module, which read those sub-envelope renders and doubled the noise shape. The floor gives both up.
- Cost, the BoofCV photographs timed on both trees (`boofcv --time`, each call the fastest of five; the build before in two rounds within 1 % of each other, the final build in one): the 400 that read before 0.99 to 1.00 (median 1.00 to 1.01), the six gained 0.37 to 0.42 (they read at the first regional pass where every pass and level ran before), the 130 that still fail 1.07 to 1.09 (median 1.02 to 1.03, 90th percentile 1.32 to 1.37, worst 1.8 on `damaged/image032`, 1,299 by 1,176 px of texture crowded on every pass, 10.9 to 17.8 ms, and 1.5 on `curved/image021`, 12 megapixels whose small texture candidates are undersampled at stride 17 on every failing pass, 28 to 41 ms). The set: 2.9 s as before, zxing-cpp 6.7 s. The report project's no-symbol shapes at 740 px (`image/none-noise`, `image/none-gradient`, .NET 10 JIT with AVX2, five processes each): 1.01 and 1.01; before the floor, 2.13 and 1.00.

Lessons:

- A cue that holds on the photographs can fail on the render built to test it. Compactness picked the symbol's own triple on both `lots` photographs and the crossroads of three symbols on the keystoned grids, so it is an order for the gate and not a choice. The grid was built as the test of the tie rule and refuted it before any set was run; the lesson is in the Standard QR record.
- The alternatives are ordered by the least confirmed member, which on a crowd of equally confirmed finders is no order: the first real triple sat 11th, 20th or past 40th. An order fit for a crowd needs the image's gate behind it, and that gate (the timing patterns through the triple's own frame) passed every real triple and no crossroads one in the first 256.
- The failing stride triple's question is whether the stride could have confirmed it. Completing every failed strided list had doubled 740 px noise before; the undersampled rule charges only lists the stride cannot have confirmed (a band shorter than two strides), which 740 px noise, stride 3, never is.
- The probe listed, for each pass and level, the strided list, the full list, both selections, the alternatives' order and each triple's decode through the corner logic, before anything was built: four of the five `brightness` scenes were known to read from the full list's own selection, and the `lots` ranks were known, so the two mechanisms were built to those numbers.
- The failure-path shapes caught what the photographs did not: the undersampled rule without a floor fired on every pass of the report project's 740 px noise, whose candidates are 1.0 to 1.9 px/module at stride 3, and doubled it (6.9 to 14.7 ms) while the photographs' still-failing median stayed at 1.00. A candidate under the finder's 2 px floor is taken at the floor, and the shape was timed again.
- Eight faults planted in the stage's bounds and gates, each moved so the stage does less, with the two new tests as the filter: seven caught (the crowded floor of nine, the score cap, the gate and decode counts, the undersampled rule, the gate on the skipped rows, the compact order). The eighth, the comparison that keeps or drops a triple when the 256 kept are full, survived: the grids hold fewer well-shaped triples than that, and only the `lots` photographs (291 under 0.5) reach the bound.

What is left, the 22: the grid with the right triple where the four-corner grid reads, 7 (`curved` 4, `nominal/image004`, `noncompliant/image008`, `shadows/image013`); the alignment mesh of version 25 and up at 5.5 to 8 px/module, 5; two finders found but not read from the true third centre either, 4; one finder, 3; none, 1; `brightness/image021`, whose full list of eight confirmed candidates selects a false triple with the real one 35th among the alternatives; `high_version/image030`, a version 40 at 5.4 px/module whose real triple is the 20th alternative and passes the timing gate from no corner. The next sub-phase takes the largest.

### Phase 4, fourth stage: the fourth point from the finders' edges (2026-10-11)

The seven photographs whose right triple is selected and whose grid fails, read again at every pass and level where the full sweep's selection is the right triple (a temporary probe, deleted), each grid sampled at the true dimension and decoded: the decoder's own (the alignment anchor where found, else the parallelogram or the frame), the grid through zxing-cpp's four corners, the three refined centres with the true fourth point at the alignment position, the parallelogram and the frame.

- The four curved version 1 symbols (10 to 46 px/module): the centres are within 0.03 to 0.2 module of the truth, nothing anchors the fourth corner, and the parallelogram's and the frame's fourth point lands 0.74 to 1.06 modules out; the same centres with the true fourth point read with 4 or 5 corrections at 24 of 29 readings. The fourth point is the whole gap.
- `nominal/image004` (version 4 at 2.4 px/module) and `noncompliant/image008` (version 3 at 17.5 px/module, centres 0.27 to 0.34 module out on its styled finders): anchored, the anchor within 0.25 module, and the truth reads with 15 and 4 corrections where the centres with the true fourth point need 23 and 13. Precision at the anchor and at the centres, not the fourth point.
- `shadows/image013` (version 4 at 8.9 px/module): the alignment pattern is not found under the shadow, the fourth point 0.29 out, and even the truth needs 23 corrections.
- Sensitivity: the true fourth point moved a tenth of a module in eight directions reads 2 to 8 of the 8 on the curved photographs, a quarter 2 to 4. A symbol bent round a cylinder is read by a flat grid at the edge of its correction, so where the fourth point sits within a tenth of a module decides.
- The lead read in another reader up to its gate: zxing-cpp's `SampleQR` traces, from the bottom-left and top-right finders, the ring edge facing the symbol's interior at its inner and outer crossings, intersects the two lines and takes the midpoint as the alignment position, refined to the alignment pattern from version 2. Prototyped in the probe with the outer edges alone, the point lands within 0.02 to 0.13 module of the truth on the curved photographs, and reads at one pass or level of each.

Done:

- `QRImageDecoder.TryFourthPointFromEdges` (`QRImageDecoder.EdgePoint.cs`): from the bottom-left finder along the top-left to top-right direction and from the top-right finder along the top-left to bottom-left direction, the outer ring edge found on the centre line by the dark-light-dark walk, followed at half-module steps over the finder (13 points, the corners left out) by a half-pixel walk out of the ring's middle to the first light pixel, fitted by its principal direction, and the two lines intersected at module (dimension − 7, dimension − 7). In `SampleAndDecode`, the last grid tried when nothing anchored the fourth corner: after the frame, which used to end the attempt when the frame was the parallelogram.
- Tests, written before the code (`EdgeFourthPointDecodeTest`): a version 1 at level L and 8 px/module on a card tilted 15 to 25° about its columns' axis and bent 3 to 4 modules round a cylinder, at distance 2.5 and 1.5 symbol sides, turned 0 and 200°, with the premises asserted (the drawn finders selected within half a module, the parallelogram and the frame grids not decoding, the fourth point from the edges within a fifth of a module of the drawn one); its negative with the data destroyed. The allocation test's `EdgePoint` scene.

Measured against the commit before (30c319a), image for image:

- BoofCV: 406 to 413 of 536 (zxing-cpp 391); the gap 22 to 18, the reverse 37 to 40; seven gained and none lost: `curved/image018`, `019`, `021`, `023`, `024`, `nominal/image042`, `shadows/image011`; the four curved version 1 symbols of the class and three photographs neither reader read before. `curved` 30 to 35 of 50 (zxing-cpp 34).
- ZXing's photographs and the corpus: unchanged (657 of 716, 474 of 548). The sweep: one more bowed camera render of 66,800, nothing lost, no misread.
- Cost, the BoofCV photographs timed on both trees (`boofcv --time`, each call the fastest of five, one round each): the 406 that read before 0.99 (median 0.99), the seven gained 0.16 (median 0.20), the 123 that still fail 0.99 (median 1.01, 90th percentile 1.47, worst 1.61 on `glare/image001`, 12 megapixels, 27 to 43 ms). The set: 3.0 s against 3.1 s, zxing-cpp 7.1 s. The report project's no-symbol shapes at 740 px (five processes each, .NET 10 JIT with AVX2): noise 1.03, ramp 0.98.

Lessons:

- The renderer built for a bow is not the photograph's bend. The bowed renderer sags every row as a sine, so a finder's edge followed over seven modules and extrapolated fourteen lands 0.5 to 3 modules out, worse than the parallelogram; the camera's cylinder seen square on keeps the parallelogram's point within 0.05 module, so nothing fails; the camera's cylinder seen at a tilt about the columns' axis is the photograph: the parallelogram's point 0.5 to 1.6 modules out, the edges' within 0.2. The shots were chosen from that sweep, at level L, since at level M the parallelogram reads every bend to five modules.
- The first edge walk started on the ring's inner boundary and found no crossing on crisp renders at 0°, where that boundary is a pixel edge. It starts in the ring's middle now.
- Eight faults planted in the stage's bounds and steps, the new test as the filter: six caught (the points across the finder cut to three, the walk started outside the ring, the walk given no reach, the point's module coordinate moved a module, the crossing's half-pixel convention moved a pixel, the fitted line's direction turned a right angle). One mutant did not build (the stage removed with its variables still referenced; the test's red run before the stage existed is that check). One survived: the floor of six crossings of thirteen raised to all thirteen, which the renders meet, with no edge partly hidden; a photograph with a finder's edge under glare would tell.

What is left, the 18: the alignment mesh of version 25 and up at 5.5 to 8 px/module, 5; two finders found but not read from the true third centre either, 4; one finder, 3; none, 1; `brightness/image021` (a false triple from eight confirmed candidates); `high_version/image030` (a version 40 whose real triple passes the timing gate from no corner); `nominal/image004` (version 4 at 2.4 px/module, anchored a fifth of a module out, the truth reading with 15 corrections); `noncompliant/image008` (styled finders whose centres measure a third of a module out); `shadows/image013` (the alignment pattern lost under the shadow, the truth reading with 23 corrections). The next sub-phase takes the largest.

### Phase 4, fifth stage: the alignment mesh, investigated and not built (2026-10-11)

The five `high_version` photographs (four version 25 at 5.5 to 6.2 px/module, one version 30 at 7.9), read again at the passes where the full sweep's selection is the right triple (a temporary probe, deleted), with each grid sampled at the true dimension and decoded, and every interior lattice node searched where the frame puts it:

- The alignment lattice is found: 16 of 16 nodes on `image024`, 25 of 25 on `image020`, 11 to 15 of 16 on the other three. The found nodes drift from the frame's prediction by 0.3 module near the top-left to 3 or 4 at the bottom-right: the frame, a plane through the finders, is not the surface, and the wavefront finds the nodes anyway.
- The mesh's grid reads once, `image024` at the global pass with 80 corrections, and fails everywhere else; the grid through zxing-cpp's four corners reads none of the five, nor does the anchored transform. On `image024` the decoder never samples that grid: at the global pass the strided scan's list holds exactly three candidates, two of the symbol's finders and a false one, taken unscored, and neither the undersampled rule (5.9 px/module at stride 4) nor the crowded one completes the list; the full sweep's own selection is the right triple.
- Where the mesh grid goes wrong, measured on `image020` against the symbol re-encoded from its text (the text this decoder reads from `image024`, 648 characters, where zxing-cpp's returned 572): the interior cells sample 7 to 15 % of their modules wrong, the outer bands, between the symbol's edge and the first lattice line, 45 to 50 %. The coordinate-6 row and column are not searched and are extrapolated along their lattice lines, 24 modules out from nodes 24 modules apart at version 30, and that extrapolation is what fails.
- A prototype that replaced the extrapolated edge nodes, the three finder corners from each finder's centre and the frame's axes there and the others from the dark timing module nearest the prediction, sampled every cell about half wrong, the interior included: either its node indexing or its construction is wrong, and the probe was stopped there rather than debugged.
- The symbol re-encoded from the text matches the reading grid of `image024` on only 71 % of its modules at every level, mask and segmentation the generator offers, where a grid read with 80 corrections cannot be more than 5 % wrong; the five photographs may not hold one symbol, or their encoder's stream differs from this generator's. The maps of the version 25 photographs are therefore not trusted; `image020`'s, consistent with its interior reading, are.

Nothing was built. The lead this leaves, for a sub-phase of its own: the mesh's coordinate-6 nodes measured rather than extrapolated (the finder corners from the finders, the rest from the timing patterns), checked first on `image020`'s map, and a strided list of exactly three candidates taken unscored completed on failure (`image024`). A ground truth for the version 25 photographs has to come from a corrected read, not from re-encoding.

### Phase 4, sixth stage: the mesh's coordinate-6 nodes measured (2026-10-11)

The fifth stage's probe, extended to print each coordinate-6 node's distance from the bilinear truth and from its own extrapolation, run on `image020`'s map after each change:

- The first try placed the three corner nodes on the plane that best fits the three finder centres and every node found. It read `image020` and `image024` at one pass each, but its corners sat a module out on the bend (`image020`'s top band 38 to 45 % wrong, the left 29 to 53 %) and it broke two bowed renders of `LargeVersionGridOrderTest` (versions 25 and 40), whose columns are straight and whose extrapolation is exact. No one plane fits a bend.
- The second try interpolated each corner between its finder's centre and the nearest found diagonal node, three modules along the way. The top-left corner's band fell to 0 %, the top-right's and bottom-left's rose to 52 %: the lattice is not symmetric about the symbol's centre (the first gap, 6 to the second coordinate, is shorter than the rest), so at those corners the corner is not on that diagonal. The comment that claimed the symmetry was written before it was checked.
- The corner placed by the local affine of its finder's centre and two found nodes a cell in, the nearest diagonal node and one on the corner's own lattice line (the top-left corner, whose own lines are the edges, takes a node a cell further along the row or the column), put every corner band at 0 to 6 %.
- The edge nodes between the corners searched as the interior ones are, around the extrapolation in its 2.5-module window: every one of `image020`'s eight found, 0.5 to 0.9 module from the extrapolation, none false on the timing modules (the row scan's light-dark-light runs are everywhere on a timing line, and the column cross-check and both rings refuse them). The bands fell from 45 to 50 % wrong to 0 to 17 %, the corrections from 271 to 20, and `image024` from 100 to 22.

Done:

- `QRImageDecoder.TryBuildSampleMesh`, from version 14, after the extrapolation of the coordinate-6 row and column: the edge nodes between the corners searched through `TryFindAlignment` around the extrapolation, a miss keeping it; the three corners placed by the local affine of the finder's centre and two found nodes a cell in, a pair not found keeping the extrapolation. The signature is as before; the small mesh of versions 7 to 13 is untouched.
- Tests, written before the rule was final and red on the build before (`MeshEdgeNodeDecodeTest`): versions 25 and 30 at levels L and M, 3 to 4 px/module, on a card tilted 0 to 25° about its columns' axis and bent 2 to 4 modules, blurred and with sensor noise, at distance 1.5 and 2.5 symbol sides, with the premises asserted (the drawn finders selected within half a module, the mesh built, the same mesh with its coordinate-6 nodes extrapolated along the lattice lines not decoding); its negative with the data destroyed. Four of the six shots read only with the edge nodes searched, two with the corners alone, chosen from a sweep of 192 shots run with the search off and on (20 read only with it, none lost). `LargeVersionGridOrderTest`'s intact renders changed: the mesh now reads three of its four former renders with no corrections, as exactly as the four-point transform, so the premise that tells the two grids apart holds on four others (versions 14, 20, 25, 30 at 3 to 3.5 px/module, 0.12 to 0.2 keystone).

Measured against the commit before (b26dd19), image for image:

- BoofCV: 413 to 415 of 536 (zxing-cpp 391); the gap 18 to 16, the reverse 40; two gained and none lost: `high_version/image020` (version 30 at 7.9 px/module) and `image024` (version 25 at 6.2), both read by zxing-cpp. `high_version` 25 to 27 of 33 (zxing-cpp 32).
- ZXing's photographs and the corpus: unchanged (657 of 716, 474 of 548). The sweep: 72 more camera renders of 66,800, nothing lost, no misread: 24 with barrel distortion of 5 to 20 % (47 to 71 of 400), 4 bowed (390 to 394) and 44 of the phone mix (191 to 235).
- Cost, the BoofCV photographs timed on both trees (`boofcv --time`, each call the fastest of five, one round each): the 413 that read before 1.00 (median 1.00, 90th percentile 1.04), the two gained 0.23, the 121 that still fail 1.00 (median 1.00, 90th percentile 1.03, worst 1.12 on `glare/image004`). The set: 2.5 s on both, zxing-cpp 5.9 s. The report project's no-symbol shapes at 740 px (five processes each, .NET 10 JIT with AVX2): noise 1.00, ramp 0.98.

Lessons:

- The extrapolation's fault is amplification, not the model: for a lattice of 26-module cells the quadratic through the three nearest nodes weights them 2.8, −2.7 and 0.9, so a fifth of a module of node noise is a module at the edge. On a crisp render the nodes have no noise and the extrapolation is exact, which is why the bowed renders read before and the photographs did not.
- A rule that is exact for one distortion fails on the other: the plane fits the perspective and not the bend, the extrapolation along a straight column fits the bend and not the noise. The measurement fits both, and where it is not available, the local interpolation between measured points (the finder centre and the nodes a cell in) is the next best, since it never reaches past what was measured.
- The comment about the lattice's symmetry was wrong and the top-right band said so within one probe run. A geometric claim in a comment is checked against the table it is about before it is written.
- Seven faults planted in the stage's search, bounds and weights, the new test, `LargeVersionGridOrderTest` and `SampleMeshTest` as the filter: six caught (the search window shut, the left column left unsearched, the found node's y dropped, the found check inverted, the two nodes' weights swapped, the finder's grid position moved a module). The first test rows, chosen only for the extrapolated mesh failing, caught the corner faults and none of the search's: on those shots the corners alone read, so the rows were rechosen from the sweep run with the search off and on. One survived: the top-left corner left at its extrapolation, which the renders place within the read's tolerance, both of that corner's lattice gaps being equal; `image020`'s top-left band, 0 % by the interpolation against 22 to 50 % by the extrapolation on the probe's runs, is the evidence, and a committed photograph would be the test.

What is left, the 16: three `high_version` photographs (version 25 at 5.5 to 6 px/module) whose lattice is found 11 to 15 of 16 and whose cells, interior included, sample half their modules wrong; two finders found but not read from the true third centre either, 4; one finder, 3; none, 1; `brightness/image021`; `high_version/image030`; `nominal/image004`; `noncompliant/image008`; `shadows/image013`. The strided list of exactly three candidates taken unscored (`image024` at the global pass) is still not completed on failure; the photograph reads at the regional pass.

### Phase 1b: go-qr in its own harness (2026-10-11)

Done:

- go-qr's `TestBoofCV`, unchanged, on the checkout at `.references/go-qr` (v2.6.0, d8a5d8d) with Go 1.26.4 on Windows x64, over BoofCV's detection directory and over a copy of ZXing's `qrcode-1` to `-6`. The workspace file sits in the scratch directory (`GOWORK`) and the checkout is as cloned after every run.
- `tools/QRImageDecodeSweep`: `--lines <file>` on `boofcv` and `photos` (`ReaderLines`), which scores each reader of a file of result lines as one more reader with its own table, gap, reverse and names; and `grey <root> <outDir>`, which writes a set as the grey images the readers here are handed. Without `--lines` both commands write what they wrote before.
- Both sets read twice by go-qr: from its own grey conversion, to reproduce its figures, and from this tool's grey images, for the records.

Measured:

- The published figures. BoofCV: 411 of 536 by this tool's rule against the published 413, every category as published but `damaged` (15 for 16) and `lots` (6 for 7). ZXing upright: 166 of 179, as published. gozxing, in the same lines: 182 and 155 against 183 and 155.
- The grey image. From this tool's grey images go-qr reads 412 and 165. Five BoofCV photographs move (`bright_spots/image016`, `damaged/image010`, `lots/image006` read; `brightness/image026`, `glare/image026` lost), six more read another symbol of the photograph, and ZXing's `qrcode-2/12` is lost. The two published reads its own conversion misses are in the categories the other conversion adds to, so the 411 is the image and not the scoring. The records use the shared image (D8).
- Against this library on the shared image. BoofCV: 412 against 416, gap 23, reverse 27; by category in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#where-the-gap-stands). With go-qr's reads in the agreement, `damaged/image020` is confirmed and this library's 415 becomes 416; no text of either reader is unconfirmed. ZXing upright: 165 against 164, gap 4 (`qrcode-2/5`, `qrcode-4/09`, `29`, `34`), reverse 3 (`qrcode-2/12`, `qrcode-3/22`, `40`).
- All three readers on BoofCV: 34 photographs one of the other two reads and this library does not (zxing-cpp alone 11, go-qr alone 18, both 5: `glare/image037`, `high_version/image030`, `nominal/image003`, `noncompliant/image008`, `shadows/image013`), 11 this library alone, 86 none.
- Time, each reader's own clock on this machine: go-qr 9.2 to 9.5 s for BoofCV's set (published 8.7 s), this library 2.5 s, zxing-cpp 5.9 s.

The cause of each of the 27 gap photographs, by a temporary probe (deleted) with go-qr's version and corners as the truth, taken from a scratch program on the same workspace: the full sweep's candidates at each scale to an eighth under both thresholds, the selection, the decoder's first grid from the right triple at the true dimension, and the flat grid through go-qr's four corners, each read by the threshold and in module space (go-qr's module read, written into the probe: nine samples over the module's central 30 %, against the mean of the modules within max(3, dimension / 8)):

- Finders, 8. None: `pathological/image009`, `010`. One: `bright_spots/image016`, `glare/image037`, `nominal/image003`, `perspective/image030`. Two: `glare/image039`, `perspective/image029`.
- The grid with the right triple, the flat grid through go-qr's corners reading by the threshold, 9, versions 1 to 3: `blurred/image027`, `038`, `curved/image032`, `034`, `damaged/image009`, `010`, `noncompliant/image008`, `qrcode-2/5`, `qrcode-4/09`.
- The same, the flat grid reading only in module space, 5: `damaged/image008`, `011`, `glare/image011`, `015`, `qrcode-4/29`.
- The same, the flat grid reading neither way, 5: `glare/image013`, `016`, `high_version/image030`, `shadows/image013`, `qrcode-4/34`.
- The decoder's own first grid read in module space reads `curved/image034`, `shadows/image013` and `qrcode-4/09`, with 5, 24 and 3 corrections. From go-qr's corners, `blurred/image027` and `damaged/image009` read by the threshold and not in module space.

The ranking, written again from what is measured now:

1. A grid that follows a lens (the seventh stage, planned above): 105 phone-mix renders against zxing-cpp, versions 7 and up. No photograph of either gap is of this class but `high_version/image030`, and go-qr was not run on the renders.
2. The grid of a small symbol fitted to more than three centres and one anchor: 9 photographs whose flat grid through four outer corners reads by the threshold where the decoder's own does not. `curved/image032` is the plain case: version 2 at 18 px/module, anchored, the centres within 0.15 module, and the outer corners read with one correction. go-qr's fit over each finder's corners is the technique-table row.
3. A module read in module space as one more re-read of a failed grid: 3 photographs from the decoder's own first grid, one of them (`shadows/image013`) in both gaps, and 5 more that also need the grid of row 2. It joins the coverage re-read and does not replace the threshold, which reads two photographs it does not.
4. Finders: 8 photographs of four kinds (drawn symbols with no candidate at all, perspective, glare, a 3 px/module symbol), no class of more than two.
5. Nothing measured reads them: `glare/image013`, `016`, `qrcode-4/34`.

Lessons:

- Level totals hide the leads. 416 against 412 is 23 and 27, and the published shares turned into counts had promised a gap of at least 12.
- A reader in its own harness reads its own image. Go's conversion and SkiaSharp's differ enough to move 5 of 536 photographs and 1 of 179 and to change which symbol is read on 6. A column means what the others mean only when the image is handed over, which is what `grey` is for.
- A workspace file written with forward slashes on Windows resolves the modules and then refuses the package ("directory is contained in a module that is not one of the workspace modules"); with the system's separators it works.
- The reported status is not the stage. Six of the 19 grid photographs report `NotDetected`, and the full sweep selects their right triple only under the regional threshold or at a reduced scale. Whether the pipeline's strided and staged search reaches that triple was not measured, and a stage built for any of them measures it first.
- A probe that carries the other reader's rule answers "which of its techniques" before anything is built: the module-space read written into the probe split the 19 three ways in one run.

What is left open: go-qr on this tool's camera renders (its `Decode` takes an image, so the phone-mix kind can be written out and read as the photographs were), which would say whether its lens correction or zxing-cpp's local grid is the lead for row 1; and the maintainer's ruling on D8.
