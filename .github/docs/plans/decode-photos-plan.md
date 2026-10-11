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

## Phases

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Measure photographs | Done 2026-10-10 (Progress log): the `photos` and `boofcv` commands, the ZXing set read from a checkout (D7), zxing-cpp's samples at 2c3dcfe checked against the import, eight camera kinds drawn by `CameraRenderer`, BoofCV per category with a cause class for each of its 77 gap photographs | A table per set with gap and reverse, a recorded cause for every gap image of the ZXing and zxing-cpp sets, BoofCV per category, and the candidates of the technique table ranked by the reads they are aimed at |
| 2 | Documented scope from the measurement | Done 2026-10-10 (Progress log): the documents in "Documents to change" state the measured envelope on photographs, with no stage added | Every place listed says what the measurement shows, and none says "out of scope" for a class the decoder reads |
| 3 | Charset | Done 2026-10-10 (Progress log): the Shift_JIS guess and ECI 20 in the shared Byte segment decoder, and Micro QR writing UTF-8 where its ISO-8859-1 bytes would read as another text. Standard QR and rMQR already declared ECI 3. The rule and its costs were accepted as measured (D2 to D4) | The 11 ZXing images and the 44 corpus reads return their texts. A round trip over Latin-1 payloads, including text whose bytes look like half-width katakana, misreads nothing. The guess's rate on foreign Latin-1 symbols is measured and recorded. Micro QR and rMQR share it |
| 4 | Image stages | The first, a search at reduced scale, done 2026-10-10 (Progress log): BoofCV 322 to 371 of 536, the gap 77 to 40. Next is the gap that is left, classified again. One sub-phase per class Phase 1 ranks, in its order. Each starts from the failing images, finds the stage that loses them (the true-transform column on synthetic kinds, zxing-cpp's corners on photographs), and follows the accuracy-change rules | Each sub-phase's own exit: the reads gained per set, nothing lost, the failure-path multiple, and the test renders with their mechanism switched off |
| 4b | Vector tiers for the halving | Done 2026-10-11 on x64, WebAssembly and ARM64 (Progress log, the ARM64 timing in its own entry): `LuminanceHalver` has a 256-bit tier, a portable 128-bit tier and a WebAssembly tier under the rules of the 128-bit round, with the word-at-a-time loop as the scalar tier and the tail ("The halving's vector tiers" below). It changed no read | Every build class runs the tier it measured faster on, or keeps the word-at-a-time loop with its measured reason beside its row. The failing photographs' multiple is measured again and replaces 1.53 in the records |
| 5 | Fold | Durable results into the decoder records, [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) and [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the README and XML docs final, this file deleted and the index updated | No completed plan remains |

Phase 2 runs before any stage is built, because today's documents are wrong about today's decoder. Phases 3 and 4 are independent. Phase 3 has to land before the 2.0.0 freeze if D1 holds, and each sub-phase of Phase 4 updates the documents for its own change.

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
- Not done here: ARM64. The table declares the portable 128-bit tier for it, which the dispatch takes there, and the kernel pairs are ready for `--time` on the Apple M2 (`kernel/LuminanceHalver-740`, `-740-scalar`, `-12mp`, `-12mp-scalar`), beside the figures the ARM64 entry above recorded for the scalar tier.

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
