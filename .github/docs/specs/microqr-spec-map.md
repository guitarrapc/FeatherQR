# Micro QR Spec-to-Code Map (ISO/IEC 18004)

An index of where each part of the Micro QR symbology specification (ISO/IEC 18004, versions M1-M4) is implemented in this library. See [QR Symbology Architecture](qrcode-symbologies.md) for the document set and the shared/per-symbology component split.

This document is intentionally a **map, not a spec copy**. The normative details, bit layouts, formulas, edge-case constraints, and the reasoning behind implementation choices, live in code comments next to the implementation, where they stay in sync with the code.

## Encoding Pipeline Overview

```
Text ──> Mode analysis ──> Data encoding ──> ECC ──> Module placement ──> Masking ──> Format info
```

Micro QR has a single Reed-Solomon block and no codeword interleaving; the interleaving stage of the Standard QR pipeline has no Micro QR counterpart.

## Decoding Pipeline Overview (matrix level)

```
Matrix ──> Version from size ──> Format info ──> Unmask + extract ──> RS correction ──> Bitstream ──> Text
```

Same internal boundary as the Standard QR `QRMatrixDecoder`. Public entry: [MicroQRCodeDecoder](../../../src/FeatherQR/MicroQRCodeDecoder.cs) (`MicroQRCodeData` / module-matrix / zero-allocation span overloads, uniform quiet-zone stripping; image overloads below), diagnostics in [MicroQRCodeDecodeInfo](../../../src/FeatherQR/MicroQRCodeDecodeInfo.cs).

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Matrix → payload orchestration (version from size 11/13/15/17, single RS block, no deinterleaving) | [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| - | Format information decode: single 15-bit copy matched against all 32 valid patterns, ≤ 3 bit errors correctable (BCH(15,5) min distance 7); format version must agree with the physical matrix size | [MicroQRFormatInformationDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRFormatInformationDecoder.cs), cross-check in [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Table 9 | Error correction capacity t (2t + p = ecc codewords; M1 p=2 detection-only, M2-L p=3, M2-M/M3-L/M4-L p=2): the decoder must reject corrections beyond t even where Reed-Solomon could correct more | [MicroQRConstants.GetErrorCorrectionCapacity](../../../src/FeatherQR/Internals/MicroQR/MicroQRConstants.cs), enforced in [MicroQRMatrixDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Section 7.7.3 | Inverse zigzag codeword extraction with on-the-fly unmasking (reuses the encoder's own `IsFunctionModule` / `GetMaskBit`, so both sides always agree) | [MicroQRMatrixDecoder.ExtractCodewords](../../../src/FeatherQR/Internals/MicroQR/MicroQRMatrixDecoder.cs) |
| Table 2/3 | Bitstream decode: mode indicator (version − 1 bits, M1 implicit Numeric), count indicators, terminator = Numeric mode with zero count (possibly truncated at capacity), stream bounded by the bit capacity (M1/M3 half codeword), Kanji (M3/M4) decoded through the shared JIS X 0208 table | [MicroQRBinaryDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryDecoder.cs) |
| Section 7.4.3-7.4.5 | Segment payload decoding (numeric 10/7/4-bit groups, alphanumeric 11/6, byte with UTF-8/Latin-1 heuristic), shared with the Standard QR decoder | [SegmentDecoders](../../../src/FeatherQR/Internals/BinaryDecoders/SegmentDecoders.cs) |
| Section 8.5 | Reed-Solomon correction | [EccBinaryDecoder](../../../src/FeatherQR/Internals/BinaryDecoders/EccBinaryDecoder.cs), shared across symbologies |

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
| Section 7.4 (segment sequence) | Several segments in one symbol, each with its own mode and count indicator | [MicroQRSegmentPlanner](../../../src/FeatherQR/Internals/MicroQR/MicroQRSegmentPlanner.cs) (minimal-bit split, opt-in via public [MicroQRSegmentation](../../../src/FeatherQR/MicroQRSegmentation.cs); the version scan and the per-version mode-availability handling live there, the cost model is [ModeSegmenter](../../../src/FeatherQR/Internals/ModeSegmenter.cs), shared with Standard QR and rMQR) and [MicroQRBinaryEncoder.EncodeDataCodewordsSegmented](../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryEncoder.Segmented.cs); plans the ECI-less charset heuristics would misread (a relocated byte order mark, or a Latin-1 run that reads as UTF-8 once isolated) are rejected in favor of the single-mode stream — rationale in [Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation) |

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

```
Luminance ──> Otsu threshold ──> Finder candidates (shared 1:1:3:1:1 scan, every cross-checked candidate)
          ──> Axis-aligned fast path
          ──> Angular finder-axis recovery ──> Center / scale / bounded projective refinement
          ──> Shared projective grid sampling (sizes M4..M1 × orientations × transpose)
          ──> Matrix decoding (format/RS/capacity checks arbitrate the right grid)
```

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Detection pipeline orchestration; inverted (reflectance-reversed) retry, whose inversion pass is the shared [LuminanceInverter](../../../src/FeatherQR/Internals/ImageDecoders/LuminanceInverter.cs) | [MicroQRImageDecoder](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Binarization (Otsu), shared with Standard QR (lifted to `Internals.ImageDecoders` when Micro QR became the second consumer; whole-buffer histogram, not strided) | [Binarizer.ComputeOtsuThreshold](../../../src/FeatherQR/Internals/ImageDecoders/Binarizer.cs) |
| Section 6.3.1 | Finder pattern candidates: the shared 1:1:3:1:1 run scan collecting every cross-checked candidate (Standard QR keeps its best-three selection; lifted to `Internals.ImageDecoders` when Micro QR became the second consumer; row-strided since the rMQR follow-up, with the strideless sweep re-run by the image decoders when nothing decoded, so the detection envelope is never narrower than a full sweep's) | [FinderPatternFinder.FindCandidates](../../../src/FeatherQR/Internals/ImageDecoders/FinderPatternFinder.cs) |
| - | Independent horizontal/vertical module sizes from dark-light-dark runs through the single finder center (7-module span per axis) | [MicroQRImageDecoder.RefineModuleSize](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Arbitrary-rotation recovery from separated angular finder-axis candidates; local center/scale refinement handles pixel quantization, while a bounded two-parameter projective search supplies the correspondences unavailable from a single finder | [MicroQRImageDecoder.TryDecodeArbitraryOrientation](../../../src/FeatherQR/Internals/MicroQR/MicroQRImageDecoder.cs) |
| - | Projective transform and module-center sampler shared with Standard QR | [PerspectiveTransform](../../../src/FeatherQR/Internals/ImageDecoders/PerspectiveTransform.cs), [QRImageDecoder.SampleGrid](../../../src/FeatherQR/Internals/StandardQR/QRImageDecoder.cs) |
| - | Public image entry points (SKBitmap / luminance span / zero-allocation destination) | [MicroQRCodeImageDecoder.TryDecode(SKBitmap, …)](../../../src/FeatherQR.SkiaSharp/MicroQRCodeImageDecoder.cs) (rendering package) / [MicroQRCodeDecoder.TryDecodeImage](../../../src/FeatherQR/MicroQRCodeDecoder.cs) (core) |

Supported envelope (Tier 1–2, conservative single-finder envelope): clean screen-rendered or scanned images with arbitrary rotation, mirroring, reflectance reversal, non-integer uniform scaling, non-square modules within the envelope shared with the other symbologies ([qrcode-symbologies.md](qrcode-symbologies.md)), translation and quiet zone variants, plus mild optical degradation (JPEG artifacts, low contrast, additive noise). The measured keystone envelope uses a symmetric top-edge inset of 2% of symbol width for M1/M2 and 4% for M3/M4; representative rotation-plus-keystone and mirror-plus-keystone combinations are covered. Strong perspective is **out of scope** because one finder cannot provide the independent correspondences available from three Standard QR finders and alignment patterns. `QRCodeDecoder` remains Standard QR-only, Micro QR image scanning is its own explicitly-typed entry point, so default Standard QR scanning performance is unaffected.

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

## Lessons Learned (decoder)

- The Micro QR terminator is structurally a Numeric mode indicator followed by an all-zero count field (mode bits (v−1) + numeric count bits (v+2) = 2v+1 terminator bits), decoding it as "zero-count Numeric segment ends the stream" needs no special terminator scanning and handles capacity-truncated terminators for free.
- The ECC codeword counts include misdecode-protection codewords p (ISO Table 9): a decoder wired directly to full Reed-Solomon strength would silently correct ⌊ecc/2⌋ errors where the spec allows only t (e.g. 2 vs 1 for M2-L). The capacity cap is enforced after correction and is covered by its own equivalence-class tests.
- Quiet-zone stripping cannot reuse the Standard QR dark-bounding-box trick: Micro QR has a single finder, so the right/bottom edges are data modules with no darkness guarantee. The top-left dark module is the finder corner, and a uniform border gives the core size.
- libzint (via the ZXingCpp wrapper) rejects UTF-8 Micro QR payloads outright ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics came back from the round-trip transliterated ("naïve café" → "naive cafe"), UTF-8 fixture coverage comes from the qrtool lineage only, and the sanity gate's payload comparison is what catches such silent drift before a fixture is committed.
