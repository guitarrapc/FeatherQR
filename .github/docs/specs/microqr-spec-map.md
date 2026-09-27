# Micro QR Spec-to-Code Map (ISO/IEC 18004)

An index of where each part of the Micro QR symbology specification (ISO/IEC 18004, versions M1-M4) is implemented in this library. The decoder's scope, decisions and lessons are in the [design record](microqr-decoder.md). See [QR Symbology Architecture](qrcode-symbologies.md) for the document set and the shared/per-symbology component split.

This document is intentionally a **map, not a spec copy**. The normative details, bit layouts, formulas, edge-case constraints, and the reasoning behind implementation choices, live in code comments next to the implementation, where they stay in sync with the code.

## Encoding Pipeline Overview

```
Text ──> Mode analysis ──> Data encoding ──> ECC ──> Module placement ──> Masking ──> Format info
```

Micro QR has a single Reed-Solomon block and no codeword interleaving; the interleaving stage of the Standard QR pipeline has no Micro QR counterpart.

## Decoding Pipeline Overview (matrix level)

```
Module matrix ──> core located ──> version from size ──> format information ──> codeword extraction and unmasking
              ──> Reed-Solomon correction ──> bitstream decode ──> text
```

Same internal boundary as the Standard QR `QRMatrixDecoder`. Public entry: [MicroQRCodeDecoder](../../../src/FeatherQR/MicroQRCodeDecoder.cs) (`MicroQRCodeData` / module-matrix / zero-allocation span overloads, uniform quiet-zone stripping; image overloads below), diagnostics in [MicroQRCodeDecodeInfo](../../../src/FeatherQR/MicroQRCodeDecodeInfo.cs).

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Core located: a module span may carry a light quiet zone; the top-left dark module is the finder's corner and a uniform border gives the core size, since the right and bottom edges carry data and are not guaranteed dark. `MicroQRCodeData` input is the core already | [MicroQRCodeDecoder.TryLocateCore](../../../src/FeatherQR/MicroQRCodeDecoder.cs) |
| - | Version from size (11/13/15/17), then the stages below in order: a single RS block, so no deinterleaving | [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| - | Format information decode: single 15-bit copy matched against all 32 valid patterns, ≤ 3 bit errors correctable (BCH(15,5) min distance 7); format version must agree with the physical matrix size. A 15-bit copy is looked up in a 32 KB table of the word within 3 bits of each candidate (the balls never overlap, so the table is the search; the image searches decode hundreds of thousands of grids on a failing image) | [MicroQRFormatInformationDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRFormatInformationDecoder.cs), cross-check in [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Section 7.7.3 | Codeword extraction and unmasking: the inverse zigzag with on-the-fly unmasking, through a per-size placement table built from the encoder's own `IsFunctionModule` / `GetMaskBit` (so both sides always agree), reading the grid through a view that the image decoder turns into a transposed one for a mirrored capture instead of copying the grid transposed | [MicroQRMatrixDecoder.ExtractCodewords](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Section 8.5 | Reed-Solomon correction | [EccBinaryDecoder](../../../src/FeatherQR/Internals/BinaryDecoders/EccBinaryDecoder.cs), shared across symbologies |
| Table 9 | Error correction capacity t (2t + p = ecc codewords; M1 p=2 detection-only, M2-L p=3, M2-M/M3-L/M4-L p=2): the decoder must reject corrections beyond t even where Reed-Solomon could correct more | [MicroQRConstants.GetErrorCorrectionCapacity](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs), enforced in [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Table 2/3 | Bitstream decode: mode indicator (version − 1 bits, M1 implicit Numeric), count indicators, terminator = Numeric mode with zero count (possibly truncated at capacity), stream bounded by the bit capacity (M1/M3 half codeword), Kanji (M3/M4) decoded through the shared JIS X 0208 table | [MicroQRBinaryDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryDecoder.cs) |
| Section 7.4.3-7.4.5 | Segment payload decoding (numeric 10/7/4-bit groups, alphanumeric 11/6, byte with UTF-8/Latin-1 heuristic), shared with the Standard QR decoder | [SegmentDecoders](../../../src/FeatherQR/Internals/BinaryDecoders/SegmentDecoders.cs) |

Reference tests: [MicroQRFormatInformationDecoderUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRFormatInformationDecoderUnitTest.cs) (exhaustive 15-bit space vs a naive nearest-candidate reference, ISO Table 9 capacities), [MicroQRBinaryDecoderUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRBinaryDecoderUnitTest.cs) (M1 golden vectors, the ISO "01234567" M2-L example, encoder round-trips, malformed-stream negatives), [MicroQRCodeDecoderRoundTripTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeDecoderRoundTripTest.cs) (all versions × ECC × modes, quiet zones, span parity), [MicroQRCodeDecoderRobustnessTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeDecoderRobustnessTest.cs) (per-equivalence-class damage: within t, the t&lt;errors≤⌊ecc/2⌋ misdecode-protection class, beyond RS range, M1 detection-only, format damage, cross-symbology rejection), [MicroQRFixtureTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRFixtureTest.cs) (committed external-encoder corpus, two lineages).

## Text Analysis and Encoding Modes

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 7.4.1 | Mode detection (Numeric / Alphanumeric / Byte) | [TextAnalyzer.Analyze](../../../src/FeatherQR/Internals/TextAnalyzer.cs), shared across symbologies |
| Table 2 | Mode indicator widths (M1: none, M2-M4: version − 1 bits) and values | [MicroQRConstants.GetModeIndicatorLength / GetModeIndicatorValue](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| Table 3 | Character count indicator widths (Numeric = version + 2, Alphanumeric/Byte = version + 1) | [MicroQRConstants.GetCountIndicatorLength](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| Section 7.4.3-7.4.5 | Numeric / Alphanumeric / Byte segment bit streams (128-bit accumulator; byte-mode uses platform-specific SIMD fast paths with a scalar fallback) | [MicroQRBinaryEncoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryEncoder.cs) |
| Table 2 | Terminator (3/5/7/9 zero bits, shortened at capacity) and pad codewords (0xEC/0x11, final 4-bit pad = 0000) | [MicroQRBinaryEncoder.EncodeDataCodewords](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryEncoder.cs) |
| - | Mode availability per version for ENCODING (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte; Kanji is decode-only, so it is absent here and its count width lives in `GetKanjiCountIndicatorLength`) | [MicroQRConstants.IsModeSupported](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| Section 7.4 (segment sequence) | Several segments in one symbol, each with its own mode and count indicator | [MicroQRSegmentPlanner](../../../src/FeatherQR/Internals/MicroQR/MicroQRSegmentPlanner.cs) (minimal-bit split, opt-in via public [MicroQRSegmentation](../../../src/FeatherQR/MicroQRSegmentation.cs); the version scan and the per-version mode-availability handling live there, the cost model is [ModeSegmenter](../../../src/FeatherQR/Internals/ModeSegmenter.cs), shared with Standard QR and rMQR) and [MicroQRBinaryEncoder.EncodeDataCodewordsSegmented](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryEncoder.Segmented.cs); a plan the ECI-less charset heuristics would misread (a Latin-1 run that reads as UTF-8 once isolated) is rejected in favor of the single-mode stream, and the shared program opens no Byte run at a mid-content U+FEFF, which a reader would drop as a byte order mark — rationale in [Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation) |

Reference tests: [MicroQRSegmentPlannerUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRSegmentPlannerUnitTest.cs) (the planner optimum against an independent exhaustive mode assignment that respects each version's mode set, the 35-character bound with its margin, and a pinned three-run plan), [MicroQRSegmentationTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRSegmentationTest.cs) (end to end: never a larger version than single mode, always decodes, byte-identical to single mode when the version does not move, the M2-without-Byte win, the no-single-mode rescue, and the requested-version / mask / precedence branches); [MicroQRBinaryEncoderUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRBinaryEncoderUnitTest.cs) (M1 golden vectors, the ISO "01234567" M2-L example, naive bit-string references for alphanumeric/byte/half-codeword padding), [MicroQRBinaryEncoderParityTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRBinaryEncoderParityTest.cs) (optimized encoder vs an independent naive reference across all 8 version/ECC combinations, every supported mode and length, full Latin-1 range, UTF-8 fallbacks including surrogate-pair / lone-surrogate handling, and a single non-Latin-1 char probed at every position of every length so each SIMD tier's overlapped-window Latin-1 detector is proven to see it), [MicroQRBitAccumulatorUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRBitAccumulatorUnitTest.cs) (Append / Append64 / AppendWide boundary positions vs an independent bit-string reference).

## Capacity and Symbol Tables

| Spec reference | Topic | Implementation |
|---|---|---|
| Table 7 | Data capacity in bits per version/ECC (M1/M3 end on a 4-bit half codeword) | [MicroQRConstants.dataBitCapacities](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| Table 9 | Data / ECC codeword counts (single block, no interleaving) | [MicroQRConstants.dataCodewordCounts / eccCodewordCounts](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| - | Version/ECC legality (M1: detection only; M2/M3: L, M; M4: L, M, Q) and smallest-version auto-selection | [MicroQRConstants.IsValidCombination](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs), [MicroQRCodeGenerator.TrySelectVersion](../../../src/FeatherQR/MicroQRCodeGenerator.cs) is the fit; `PrepareConfiguration` wraps it and `NotFittingError` builds the message, which is why the version must be validated before the fit can report failure. Bit pricing is `long` so an oversized Byte payload cannot wrap into a false fit. Surfaced publicly as `TryGetRequiredBufferSize` (contract in [rmqr-encoder.md](rmqr-encoder.md)); `false` here also covers a mode the version or ECC level does not offer, because the text picks the mode |

| - | Version *range* selection (`MicroQRVersionRange`): the smallest version inside `[Min, Max]` that holds the content | [MicroQRCodeGenerator.TrySelectVersionInRange](../../../src/FeatherQR/MicroQRCodeGenerator.cs). A range can rule out every version for two reasons and they are not the same answer: no version in it offering the requested ECC level is a contradiction no content could satisfy and **throws**, exactly as pinning such a version does; versions being available but none carrying the mode the text requires, or none long enough, is an ordinary "does not fit" and returns `false`, because the text is what picks the mode. `MicroQRVersionRange.Any` short-circuits to the existing automatic path |

Reference tests: [MicroQRConstantsUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRConstantsUnitTest.cs) (table values), [MicroQRCodeGeneratorUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeGeneratorUnitTest.cs) (capacity boundaries per mode × ECC, illegal-combination rejection), [TryGetRequiredBufferSizeTest](../../../tests/FeatherQR.Tests/Shared/TryGetRequiredBufferSizeTest.cs) (`Try` reports the size the encode then fills, over content × ECC × requested version; through 1.2.0 this was an agreement check against the released throwing `Get`, re-pointed at the encode before that overload was removed in 2.0.0), [VersionRangeTest](../../../tests/FeatherQR.Tests/Shared/VersionRangeTest.cs) (`Any` and `Exactly` reproduce the released automatic and pinned behaviour byte for byte; the ECC-contradiction and mode-exclusion split above), [CapacityOverflowGuardTest](../../../tests/FeatherQR.Tests/Shared/CapacityOverflowGuardTest.cs) (a length that wraps the bit count is rejected, argument errors still throw at that length).

## Error Correction (Reed-Solomon)

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 8.5 | Reed-Solomon over GF(256), single block | [EccBinaryEncoder.CalculateECC](../../../src/FeatherQR/Internals/BinaryEncoders/EccBinaryEncoder.cs), shared across symbologies (generator polynomials for the Micro QR ECC counts 2/5/6/8/10/14 are built and cached on demand) |
| Section 7.5 | M1/M3 final 4-bit data codeword participates as its high-nibble byte value | [MicroQRBinaryEncoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryEncoder.cs) (packing), [MicroQRCodeGenerator.WriteCoreModules](../../../src/FeatherQR/MicroQRCodeGenerator.cs) |

Reference tests: [MicroQRMatrixExtractionTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRMatrixExtractionTest.cs) (ECC recomputation over matrix-extracted codewords, all 8 version/ECC combinations).

## Module Placement

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 6.3 | Single finder pattern, separators, edge timing patterns (row 0 / column 0) | [MicroQRModulePlacer.PlaceFunctionModules](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) |
| - | Function region predicate: `row == 0 ‖ col == 0 ‖ (row ≤ 8 ∧ col ≤ 8)` | [MicroQRModulePlacer.IsFunctionModule](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) |
| Section 7.7.3 | Two-column zigzag data placement; M1/M3 half codeword emits its high nibble only | [MicroQRModulePlacer.PlaceDataCodewords](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) |
| - | Fused production pipeline (function patterns + data + mask + format in one call, packed-row representation for all sizes; four runtime tiers: BMI2+AVX2 with placement as a static per-row PEXT/PDEP permutation, gated on fast-PEXT hardware (Intel or AMD Zen 3+), SSSE3 and ARM64 NEON sharing the serial placement + 16-module unpack pipeline (only the bit-expand idiom differs: PSHUFB+PAND+PCMPEQB vs TBL+CMTST), and a portable scalar fallback), the per-module methods above remain as the readable reference | [MicroQRModulePlacer.PlaceSymbol](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.PlaceSymbol.cs) |

Reference tests: [MicroQRCodeGeneratorUnitTest.Create_M2_MatrixStructure](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeGeneratorUnitTest.cs) (finder/separator/timing invariants), [MicroQRMatrixExtractionTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRMatrixExtractionTest.cs) (independent inverse-zigzag extraction), [MicroQRModulePlacerParityTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRModulePlacerParityTest.cs) (fused pipeline vs naive reference: byte-identical matrix and mask across all 8 version/ECC combinations, random/all-zero/all-one streams; every tier is exercised explicitly via the named internal entry points `PlaceSymbolBmi2`, `PlaceSymbolSsse3`, `PlaceSymbolAdvSimd` and `PlaceSymbolScalar`).

## Data Masking

| Spec reference | Topic | Implementation |
|---|---|---|
| Table 10 | The 4 Micro QR mask conditions (Standard QR patterns 1/4/6/7) | [MicroQRModulePlacer.GetMaskBit](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) |
| Section 7.8.3 | Edge-based mask evaluation (dark counts of right/lower edges, min·16 + max, highest wins), evaluated on the two edges only, no trial matrices | [MicroQRModulePlacer.SelectAndApplyMask](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) (reference); the production path scores both edges bit-packed in [MicroQRModulePlacer.PlaceSymbol](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.PlaceSymbol.cs) |
| - | Pinned mask: `MicroQRCodeGeneratorOptions.MaskPattern` (0-3, `null` = automatic) skips the edge evaluation and rides the same fused pipeline (templates and format bits are mask-index-driven). Exists for byte-exact reproduction of symbols produced elsewhere; the spec only recommends the best scorer, so any pattern is a legal symbol | [MicroQRModulePlacer.PlaceSymbol](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.PlaceSymbol.cs) (`forcedMask`) |

## Format Information

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Symbol number (3 bits from version + ECC) + mask (2 bits), BCH(15,5), XOR mask 0x4445 | [MicroQRConstants.GetFormatBits](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs) |
| - | Placement: bits 14…8 along row 8 cols 1-7, bit 7 at (8,8), bits 6…0 down col 8 rows 7-1 | [MicroQRModulePlacer.PlaceFormat](../../../src/FeatherQR/Internals/MicroQR/MicroQRModulePlacer.cs) |

Reference tests: [MicroQRConstantsUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRConstantsUnitTest.cs) (all 32 format patterns against the ISO-derived table plus a naive BCH reference), [MicroQRCodeGeneratorUnitTest.Create_FormatInfo_RoundTripsFromMatrix](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeGeneratorUnitTest.cs).

## Image Rendering

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 9.1 | Quiet zone: 2 modules (narrower than Standard QR's 4), the builder default | [MicroQRCodeImageBuilder.WithQuietZone](../../../src/FeatherQR.SkiaSharp/MicroQRCodeImageBuilder.cs) |
| - | High-level image builder (PNG/JPEG/WEBP/SVG, fluent options; no icon overlay, no ECC headroom; the single finder takes a shape); shared options and the output surface come from the common `SymbolImageBuilderBase<TSelf>` | [MicroQRCodeImageBuilder](../../../src/FeatherQR.SkiaSharp/MicroQRCodeImageBuilder.cs), base in [SymbolImageBuilderBase](../../../src/FeatherQR.SkiaSharp/SymbolImageBuilderBase.cs) |
| - | Low-level canvas rendering (module-run merging shared with Standard QR through the internal `IModuleMatrixView` struct views). A non-square area gets the symbol at a uniform module scale, centered, with the background over the whole area, as rMQR's overload always did (2026-09-11; the area used to be filled on both axes, the builders' old defect one layer down) | [SymbolRenderer.Render (MicroQRCodeData overload)](../../../src/FeatherQR.SkiaSharp/SymbolRenderer.cs), views in [ModuleMatrixView](../../../src/FeatherQR/Internals/ModuleMatrixView.cs) |
| - | Canvas fit: an explicit canvas size letterboxes the symbol at a uniform module scale rather than stretching it across both axes, the rule rMQR always followed (2026-09-10; the square symbologies' fill behaviour dated to the 2019 renderer and produced non-square modules, which readers find or miss depending on the aspect ratio, payload, styling, orientation and reader; the measurements are in [qrcode-symbologies.md](qrcode-symbologies.md)). Leftover canvas takes `clearColor` when set and the background otherwise | [QRImageLayout](../../../src/FeatherQR.SkiaSharp/Internals/QRImageLayout.cs), pad in [SymbolImageBuilderBase](../../../src/FeatherQR.SkiaSharp/SymbolImageBuilderBase.cs) |
| - | SKCanvas extension entry points | [SKCanvasExtensions.Render (MicroQRCodeData overloads)](../../../src/FeatherQR.SkiaSharp/SKCanvasExtensions.cs) |
| - | Canvas layout math (explicit size / module pixel size / centering), shared with the Standard QR builder | [QRImageLayout](../../../src/FeatherQR.SkiaSharp/Internals/QRImageLayout.cs) |

Reference tests: [MicroQRCodeImageBuilderUnitTest](../../../tests/FeatherQR.Tests/Rendering/MicroQRCodeImageBuilderUnitTest.cs) (full-matrix module-to-pixel parity for every version × ECC, every module center sampled against `MicroQRCodeData`, stronger than golden hashes, plus quiet zone defaults, layout, SVG structure, validation negatives), [QrImageBuilderApiParityTest](../../../tests/FeatherQR.Tests/Rendering/QrImageBuilderApiParityTest.cs) (the two builders' public surfaces must correspond 1:1 modulo the documented Standard QR-only options), [SymbolRendererAreaFitTest](../../../tests/FeatherQR.Tests/Rendering/SymbolRendererAreaFitTest.cs) (the low-level renderer fits a non-square area through every entry point, shared with Standard QR).

## Image Detection and Sampling

Each scan of the [shared image decode passes](qrcode-symbologies.md#image-decode-passes) runs this path on its ranked finder candidates; the passes and the scans are drawn there.

```
Ranked finder candidates: the scan's most confirmed first
└─ Each candidate
   ├─ Module sizes and centre
   ├─ Axis-aligned path, each right-angle orientation
   │  ├─ Each size, M4 down to M1: affine grid ──> matrix decode; coverage re-read (grey levels) ──> matrix decode
   │  ├─ Timing frame: the grid fitted to the timing patterns ──> matrix decode
   │  └─ Low density: module boundaries read off the timing patterns ──> matrix decode
   └─ Arbitrary orientation: each axis frame from an angular sweep, each right-angle orientation,
      within an attempt budget
      └─ Each size, M4 down to M1: affine grid ──> matrix decode; coverage re-read (grey levels) ──> matrix decode
         └─ Once a grid gets past its format information: scale search, then perspective search,
            each grid ──> matrix decode; coverage re-read (grey levels) ──> matrix decode

Matrix decode: the grid through the matrix level from version from size on, keeping only the corrections its
structure earns (grid evidence); then, unless it decoded successfully, the grid transposed (mirror)
Only a successful decode ends the scan early; otherwise its ranked candidates all run (in the sweep, less
those the strided scan tried, unless it ended in a verdict), and the scan reports the result that went furthest
```

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Passes, shared across symbologies ([Image decode passes](qrcode-symbologies.md#image-decode-passes)); the inverted pass's inversion is the shared [LuminanceInverter](../../../src/FeatherQR/Internals/ImageDecoders/LuminanceInverter.cs) | [MicroQRImageDecoder.DecodeLuminance](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Global threshold (Otsu), shared with Standard QR (lifted to `Internals.ImageDecoders` when Micro QR became the second consumer; whole-buffer histogram, not strided; the same histogram also yields the grey levels of the two classes it separates, and, mirrored, the threshold and grey levels of the inverted retry) | [Binarizer.FillHistogram, ComputeOtsuThresholdFromHistogram](../../../src/FeatherQR/Internals/ImageDecoders/Binarizer.cs) |
| - | Regional binarization when the global threshold reads nothing, and gives no verdict on the content, in either polarity (8 × 8 blocks, each pixel against the mean black point of the 5 × 5 around its block), tried on the positive and then the negative, each from its own pixels and decoded only when it moves a pixel out of the class that polarity's global threshold gave it; an image of only 0 and 255, or with a side of 32 px or less, is not binarized. Shared across symbologies, with a 128-bit tier for its three passes; each attempt is the decoder's own finder search, as for the global threshold (Micro QR and rMQR: a strided scan, then a full sweep when nothing was read) | [LocalBinarizer](../../../src/FeatherQR/Internals/ImageDecoders/LocalBinarizer.cs), [RegionalRetry](../../../src/FeatherQR/Internals/ImageDecoders/RegionalRetry.cs); parity: [LocalBinarizerParityTest](../../../tests/FeatherQR.Tests/Shared/LocalBinarizerParityTest.cs), retry: [RegionalRetryTest](../../../tests/FeatherQR.Tests/Shared/RegionalRetryTest.cs), decode: [UnevenLightingDecodeTest](../../../tests/FeatherQR.Tests/Shared/UnevenLightingDecodeTest.cs) |
| Section 6.3.1 | Ranked finder candidates: each scan decodes the first eight candidates, most confirmed first; only a successful decode ends it early, and otherwise it reports the result that went furthest (a read that did not fit or a verdict on the content, then a failure past the format information, then one before it). The candidates come from the shared 1:1:3:1:1 run scan collecting every cross-checked candidate (Standard QR keeps its best-three selection; lifted to `Internals.ImageDecoders` when Micro QR became the second consumer; row-strided since the rMQR follow-up, with the strideless sweep re-run by the image decoders when nothing was read, so the detection envelope is never narrower than a full sweep's, and the candidates the strided scan decoded not decoded again in the sweep, since they decode the same way, unless that scan ended in a verdict on the content; runs that miss the ratio by about a pixel are measured again from the grey levels before they are refused, which is what reads anti-aliased edges at about 2 px/module) | [FinderPatternFinder.FindCandidates](../../../src/FeatherQR/Internals/ImageDecoders/FinderPatternFinder.cs); the ranking and the result: [MicroQRImageDecoder.DecodeLuminanceScan, TrackBestFailure](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Module sizes and centre: horizontal and vertical module sizes from dark-light-dark runs through the finder centre, pairing the dark ring's inner edge on one side with its outer edge on the other, six modules apart (a candidate under one pixel per module is dropped); on an image with grey levels, the centre moved to the centroid of its centre square's darkness | [FinderAxisEstimator.RefineModuleSize](../../../src/FeatherQR/Internals/ImageDecoders/FinderAxisEstimator.cs), [ConcentricCentroid](../../../src/FeatherQR/Internals/ImageDecoders/ConcentricCentroid.cs), shared across symbologies |
| - | Axis-aligned path: in each right-angle orientation and each size from M4 down to M1, a grid anchored on the finder centre at (3.5, 3.5) with the two module sizes, skipped when it does not fit the image; sampled by the affine module-centre sampler (a 128-bit tier, lane for lane the scalar pixels) | [MicroQRImageDecoder.DecodeLuminanceScan, SampleGrid](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Coverage re-read: on an image with grey levels, a grid one of whose orientations got past its format information and read that word exactly is read again with each module's luminance interpolated at its centre and split halfway between the two levels, and decoded in both orientations only when that changes a module. It follows each size's grid in both paths and each grid of both searches, not the timing frame or the module-boundary table | [MicroQRImageDecoder.ShouldReadByCoverage, DecodeByCoverage](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs), [LuminanceSampler](../../../src/FeatherQR/Internals/ImageDecoders/LuminanceSampler.cs), shared across symbologies |
| - | Timing frame: after the sizes of an orientation, the grid fitted by least squares to every module boundary along row 0 and column 0, from the finder's outer edge to the symbol's far edge, instead of extrapolated from the finder's module size; the dark runs give the size | [MicroQRImageDecoder.TryTimingFrame](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Low density: in an orientation whose larger module size is under 1.75 px, a table of module boundaries along row 0 and column 0 instead of a fitted grid, both lines reading as timing patterns of the same size | [MicroQRImageDecoder.TryReadModuleBoundaries](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs), [ModuleBoundaryReader](../../../src/FeatherQR/Internals/ImageDecoders/ModuleBoundaryReader.cs), shared across symbologies; decode: [ModuleBoundaryReaderTest](../../../tests/FeatherQR.Tests/Shared/ModuleBoundaryReaderTest.cs) (`MicroQR_CrispTurnedOrMirrored_Decodes`); crisp M2-M4 renders between 1 and 1.5 px/module: [FractionalLowDensityDecodeTest](../../../tests/FeatherQR.Tests/Shared/FractionalLowDensityDecodeTest.cs) |
| - | Arbitrary orientation: after the axis-aligned path, the finder's local axes recovered from an angular sweep (an axis fitted over the sweep, then its separated minima, up to sixteen frames), each frame's centre refined along its own axes, then each right-angle orientation and each size from M4 down to M1 as in the axis-aligned path, within a budget of sampled-grid decodes per candidate (the coverage re-reads are not counted; every grid spends two, one per orientation, so the budget ends the path between grids) | [MicroQRImageDecoder.TryDecodeArbitraryOrientation](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs), [FinderAxisEstimator.FindOrientationCandidates](../../../src/FeatherQR/Internals/ImageDecoders/FinderAxisEstimator.cs), shared with rMQR |
| - | Scale search: once an affine grid of the arbitrary-orientation path gets past its format information in either orientation, the centre moved by half a pixel and each axis's module size by a few percent | [MicroQRImageDecoder.TryDecodeScaleVariants](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Perspective search: then the two projective coefficients a single finder leaves unknown, over a small grid of mild values, each grid sampled through the projective transform and sampler shared with Standard QR | [MicroQRImageDecoder.TryDecodePerspectiveVariants](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs), [PerspectiveTransform.FromLocalFrame](../../../src/FeatherQR/Internals/ImageDecoders/PerspectiveTransform.cs), [QRImageDecoder.SampleGrid](../../../src/FeatherQR/Internals/StandardQR/QRImageDecoder.cs) |
| - | Matrix decode and grid evidence: each sampled grid goes through the matrix level from version from size on, then, unless it decoded successfully, transposed. It uses only the corrections its structure earns: Reed-Solomon's strength at the correction count, the format word's distance, the timing modules and, when those fall short, the quiet-zone modules along the right and bottom edges, summed in bits to 33, so M1, which corrects nothing, is never kept on its format word and timing alone. The evidence is counted after Reed-Solomon has corrected the grid, and a read beyond what it earns fails as a correction failure does. One rule in every pass, in place of refusals by level | [MicroQRGridEvidence](../../../src/FeatherQR/Internals/MicroQR/MicroQRGridEvidence.cs), [MicroQRMatrixDecoder.DecodeSampledGrid](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs), [MicroQRQuietZone](../../../src/FeatherQR/Internals/MicroQR/MicroQRQuietZone.cs); rule: [MicroQRGridEvidenceTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRGridEvidenceTest.cs), decode: [UnevenLightingDecodeTest](../../../tests/FeatherQR.Tests/Shared/UnevenLightingDecodeTest.cs) |
| - | Public image entry points (SKBitmap / luminance span / zero-allocation destination) | [MicroQRCodeImageDecoder.TryDecode(SKBitmap, …)](../../../src/FeatherQR.SkiaSharp/MicroQRCodeImageDecoder.cs) (rendering package) / [MicroQRCodeDecoder.TryDecodeImage](../../../src/FeatherQR/MicroQRCodeDecoder.cs) (core) |

The supported envelope, its measurements and the reasons behind its limits are in the [design record](microqr-decoder.md#supported).

Reference tests: [MicroQRCodeDecoderImageTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeDecoderImageTest.cs) (clean renders for every version × ECC, arbitrary rotation, uniform scale, non-square modules drawn through a canvas scale, mirror/inversion/translation/quiet-zone variants, deterministic degradation subset per test strategy §7, negative cases both symbology directions), [MicroQRCodeDecoderPerspectiveTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeDecoderPerspectiveTest.cs) (measured keystone envelope and rotation/mirror combinations), [MicroQRFixtureTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRFixtureTest.cs) (committed external-encoder PNG corpus through the image path).

## Data Model and Serialization

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Bit-packed core matrix with virtual quiet zone (spec quiet zone: 2 modules) | [MicroQRCodeData](../../../src/FeatherQR/MicroQRCodeData.cs) |
| - | "QRX" serialization container (magic + symbol type + width + height + packed bits) | [MicroQRCodeData.GetRawData](../../../src/FeatherQR/MicroQRCodeData.cs) |

Reference tests: [MicroQRCodeDataUnitTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRCodeDataUnitTest.cs).

## Maintenance Notes

- When adding or moving a spec-referenced implementation, update this map, but keep the detailed explanation (bit layouts, formulas, constraints) in the code comment next to the implementation, not here.
- [MicroQRMatrixExtractionTest](../../../tests/FeatherQR.Tests/MicroQR/MicroQRMatrixExtractionTest.cs) remains as an encoder-side consistency guard with its own independent extraction code (it predates the decoder and does not depend on it). External-encoder fixtures and the oracle matrix are tracked in the [fixture record](qrcode-test-fixtures.md).
- Components marked "shared across symbologies" live outside `Internals/MicroQR`; the split is defined in [QR Symbology Architecture](qrcode-symbologies.md).
- Adding, removing or reordering a decode stage or retry updates, in the same change, this map's decode diagrams, the image decoder's pipeline remarks and the decode figures in the [design record](microqr-decoder.md#image-decode-figures): rerun `dotnet run tools/decode_figures.cs -- .github/docs/images`, then recheck each figure's box states and notes against the code. A change inside a stage, such as a tolerance or a gate, can flip a box's state too, and the tool checks the decodes, not the box states.
