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
| D2 | The guess rule | Decided 2026-10-10: rules, as zxing-cpp's, to start with. UTF-8 when valid, Shift_JIS when valid and the bytes show Japanese runs, ISO-8859-1 otherwise. Whether all undeclared Byte segments of one symbol take one charset, and whether a Kanji segment in the symbol counts toward Shift_JIS, is settled in Phase 3 against the sets. A scoring model only if the rules misguess a measured case |
| D3 | Undeclared single-byte text: ISO-8859-1 or Windows-1252 | ISO-8859-1, the standard's default, until a set holds a symbol that needs Windows-1252 |
| D4 | CP932 extensions (NEC row 13, the IBM rows) | No, until a measured symbol needs one. They report `UnmappedCharacter`, as in Kanji mode |
| D5 | ECI 20 decode | Yes, through the same table. The scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions) changes with it |
| D6 | BoofCV's photographs (`qrcodes_v3.zip`, about 200 MB, licence not stated) | Decided 2026-10-10: never committed. The maintainer downloads them from `https://boofcv.org/notwiki/regression/fiducial/qrcodes_v3.zip`, the tool takes the path, and only numbers enter the records |
| D7 | ZXing's QR photographs (Apache-2.0) | Decided 2026-10-10: not imported. Of the 179, 30 are pixel for pixel zxing-cpp samples and 49 more are the same photographs re-encoded (same size and name, a mean difference under 15 grey levels), so 100 are new. They are 10.5 MB as PNG and 9.2 MB as lossless WebP, eight to ten times the committed corpus (1.1 MB). `photos` reads them from a ZXing.Net checkout, as BoofCV's are read, and a photograph is committed only when a test needs it, as the five corpus decode tests needed theirs |

## Phases

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Measure photographs | Done 2026-10-10 (Progress log): the `photos` and `boofcv` commands, the ZXing set read from a checkout (D7), zxing-cpp's samples at 2c3dcfe checked against the import, eight camera kinds drawn by `CameraRenderer`, BoofCV per category with a cause class for each of its 77 gap photographs | A table per set with gap and reverse, a recorded cause for every gap image of the ZXing and zxing-cpp sets, BoofCV per category, and the candidates of the technique table ranked by the reads they are aimed at |
| 2 | Documented scope from the measurement | The documents in "Documents to change" state the measured envelope on photographs, with no stage added yet | Every place listed says what the measurement shows, and none says "out of scope" for a class the decoder reads |
| 3 | Charset | D2 to D5. The shared Byte segment decoder, the encoder rule that writes ECI 3 where the guess would fire, ECI 20 decode | The 11 ZXing images and the 44 corpus reads return their texts. A round trip over Latin-1 payloads, including text whose bytes look like half-width katakana, misreads nothing. The guess's rate on foreign Latin-1 symbols is measured and recorded. Micro QR and rMQR share it |
| 4 | Image stages | One sub-phase per class Phase 1 ranks, in its order. Each starts from the failing images, finds the stage that loses them (the true-transform column on synthetic kinds, zxing-cpp's corners on photographs), and follows the accuracy-change rules | Each sub-phase's own exit: the reads gained per set, nothing lost, the failure-path multiple, and the test renders with their mechanism switched off |
| 5 | Fold | Durable results into the decoder records, [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) and [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the README and XML docs final, this file deleted and the index updated | No completed plan remains |

Phase 2 runs before any stage is built, because today's documents are wrong about today's decoder. Phases 3 and 4 are independent. Phase 3 has to land before the 2.0.0 freeze if D1 holds, and each sub-phase of Phase 4 updates the documents for its own change.

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
