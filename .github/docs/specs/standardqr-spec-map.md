# Standard QR Spec-to-Code Map (ISO/IEC 18004)

An index of where each part of the Standard QR symbology specification (ISO/IEC 18004, versions 1-40) is implemented in this library. Micro QR and rMQR get their own maps as they are implemented, see [QR Symbology Architecture](qrcode-symbologies.md) for the document set and shared components.

This document is intentionally a **map, not a spec copy**. The normative details, bit layouts, formulas, edge-case constraints, and the reasoning behind implementation choices, live in code comments next to the implementation, where they stay in sync with the code. Section numbers below follow the citations in the code comments (the codebase references both ISO/IEC 18004:2015 `7.x` numbering and earlier-edition `8.x` numbering).

## Encoding Pipeline Overview

```
Text ──> Mode analysis ──> Data encoding ──> ECC ──> Interleaving ──> Module placement ──> Masking
```

## Text Analysis and Encoding Modes

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 7.4.1 | Encoding modes (Numeric / Alphanumeric / Byte) | [EncodingMode.cs](../../../src/FeatherQR/Internals/EncodingMode.cs), [TextAnalyzer.Analyze](../../../src/FeatherQR/Internals/TextAnalyzer.cs), scalar classification plus SIMD tiers (x64 AVX2 / SSE2, ARM AdvSimd) selected at runtime; parity: [TextAnalyzerAdvSimdParityTest](../../../tests/FeatherQR.Tests/Shared/TextAnalyzerAdvSimdParityTest.cs), [TextAnalyzerX64ParityTest](../../../tests/FeatherQR.Tests/Shared/TextAnalyzerX64ParityTest.cs) (each tier entered directly, the dispatcher reaches only one per machine) |
| Section 7.4.3 | Alphanumeric character set (45 characters) and values | [CharacterSets.alphanumericLookup / GetAlphanumericValue](../../../src/FeatherQR/Internals/CharacterSets.cs), shared across symbologies |
| - | Numeric / Alphanumeric / Byte mode bit stream encoding | [QRBinaryEncoder](../../../src/FeatherQR/Internals/StandardQR/QRBinaryEncoder.cs) (`WriteNumericData`, `WriteAlphanumericData`, `WriteByteData`) |
| Section 7.4.4 | Character count indicator widths (version 1-9 / 10-26 / 27-40) | [EncodingModeExtensions.GetCountIndicatorLength](../../../src/FeatherQR/Internals/StandardQR/EncodingModeExtensions.cs) |
| Section 7.4.5 | UTF-8 BOM bytes count toward the character count indicator | [QRCodeGenerator.cs](../../../src/FeatherQR/QRCodeGenerator.cs), [QRBinaryEncoder.GetUtf8Data](../../../src/FeatherQR/Internals/StandardQR/QRBinaryEncoder.cs) |
| - | ECI header structure (indicator 4 bits + assignment number) | [EciModeExtensions](../../../src/FeatherQR/EciMode.cs) |
| Section 7.4 (segment sequence) | Several segments in one symbol, each with its own mode and count indicator | [QRSegmentPlanner](../../../src/FeatherQR/Internals/StandardQR/QRSegmentPlanner.cs) (minimal-bit split, opt-in via public [QRSegmentation](../../../src/FeatherQR/QRSegmentation.cs); the version scan lives there, the cost model is [ModeSegmenter](../../../src/FeatherQR/Internals/ModeSegmenter.cs), shared with Micro QR and rMQR) and [QRBinaryEncoder.WriteSegments](../../../src/FeatherQR/Internals/StandardQR/QRBinaryEncoder.cs); rationale and bounds in [Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation) |

Reference tests: [QRBinaryEncoderUnitTest](../../../tests/FeatherQR.Tests/StandardQr/QRBinaryEncoderUnitTest.cs) (includes the canonical "HELLO WORLD" version 1-M example), [QRCodeDecodabilityTest](../../../tests/FeatherQR.Tests/StandardQr/QRCodeDecodabilityTest.cs) (BOM handling, mixed-mode symbols decoded by ZXing); [QRSegmentPlannerUnitTest](../../../tests/FeatherQR.Tests/StandardQr/QRSegmentPlannerUnitTest.cs) (the planner optimum against an independent exhaustive mode assignment, the band widths the scan assumes, the 7,089-character bound with its 4-bit margin, and plan shape invariants), [QRSegmentationTest](../../../tests/FeatherQR.Tests/StandardQr/QRSegmentationTest.cs) (end to end: never a larger version than single mode, always decodes, byte-identical to single mode when the version does not move, plus the requested-version / range / boost / mask / BOM branches).

## Capacity and Symbol Tables

| Spec reference | Topic | Implementation |
|---|---|---|
| Table 7-11 | Data capacity per version / ECC level | [QRCodeConstants.capacityBaseValues](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs), practical capacities are documented in [Data Capacity Reference](../../../docs/data-capacity.md) |
| Table 9 | ECC characteristics (block counts, codewords per block) | [QRCodeConstants.capacityECCBaseValues](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs) |
| Table 1 | Remainder bits per version | [QRCodeConstants.remainderBits](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs) |
| Annex E | Alignment pattern center coordinates | [QRCodeConstants.alignmentPatternBaseValues](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs) |
| - | Version fit: smallest version holding the analyzed content at the ECC level, including the ECI and UTF-8 BOM header overhead | [QRCodeGenerator.TryGetVersion](../../../src/FeatherQR/QRCodeGenerator.cs) is the scan; `GetVersion` wraps it and owns the "exceeds Version 40" message. Bit pricing is `long` so an oversized Byte payload cannot wrap into a false fit, and the ECC level keeps being validated inside the scan rather than short-cut by a length test. Surfaced publicly as `TryGetRequiredBufferSize` (contract in [rmqr-encoder.md](rmqr-encoder.md)) |
| - | ECC boost: the requested level as a minimum, raised to the chosen version's capacity without changing the version (`QRCodeGeneratorOptions.BoostEccLevel`, design in [standardqr-encoder.md](standardqr-encoder.md)) | [QRCodeGenerator.ResolveVersionAndEcc](../../../src/FeatherQR/QRCodeGenerator.cs), builder surface [QRCodeImageBuilder.WithErrorCorrectionBoost](../../../src/FeatherQR.SkiaSharp/QRCodeImageBuilder.cs) |

Reference tests: [TryGetRequiredBufferSizeTest](../../../tests/FeatherQR.Tests/Shared/TryGetRequiredBufferSizeTest.cs) (`Try` reports the size the encode then fills, over content × ECC × ECI × UTF-8 BOM; through 1.2.0 this was an agreement check against the released throwing `Get`, re-pointed at the encode before that overload was removed in 2.0.0), [CapacityOverflowGuardTest](../../../tests/FeatherQR.Tests/Shared/CapacityOverflowGuardTest.cs) (a length that wraps the bit count is rejected, argument errors still throw at that length, and the published maxima still fit), [EccBoostTest](../../../tests/FeatherQR.Tests/StandardQr/EccBoostTest.cs) (headroom classes, version invariance, sizing indifference, error parity).

## Error Correction (Reed–Solomon)

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 8.5 | Generator polynomial and polynomial division over GF(2^8) | [EccBinaryEncoder.CalculateECC](../../../src/FeatherQR/Internals/BinaryEncoders/EccBinaryEncoder.cs), scalar kernel plus SIMD variants ([SSSE3 / GFNI](../../../src/FeatherQR/Internals/BinaryEncoders/EccBinaryEncoder.Simd.cs), [ARM AdvSimd](../../../src/FeatherQR/Internals/BinaryEncoders/EccBinaryEncoder.Simd.Arm.cs)), shared across symbologies |
| Annex I | Worked encoding example | Used as test vectors in [EccBinaryEncoderUnitTest](../../../tests/FeatherQR.Tests/Shared/EccBinaryEncoderUnitTest.cs) |

Reference tests: [EccBinaryEncoderKernelParityTest](../../../tests/FeatherQR.Tests/Shared/EccBinaryEncoderKernelParityTest.cs), every SIMD kernel is checked against a naive Section 8.5 reference implementation.

## Final Message Construction

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 7.6 | Data / ECC codeword interleaving across blocks, trailing remainder bits | [BinaryInterleaver.InterleaveCodewords](../../../src/FeatherQR/Internals/BinaryEncoders/BinaryInterleaver.cs), shared across symbologies (lifted from `Internals.StandardQR` when rMQR became the second consumer; the Standard-QR-specific remainder-bit count is passed in) |

Reference tests: [BinaryInterleaverParityTest](../../../tests/FeatherQR.Tests/Shared/BinaryInterleaverParityTest.cs), [BinaryInterleaverUnitTest](../../../tests/FeatherQR.Tests/Shared/BinaryInterleaverUnitTest.cs).

## Module Placement

| Spec reference | Topic | Implementation |
|---|---|---|
| - | Finder patterns, separators, timing patterns, dark module | [ModulePlacer](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs) (`PlaceFinderPatterns`, `ReserveSeparatorAreas`, `PlaceTimingPatterns`, `PlaceDarkModule`) |
| Annex E | Alignment pattern placement | [ModulePlacer.PlaceAlignmentPatterns](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs) |
| Section 7.7.3 | Zigzag data placement (bottom-right, 2-column strips) | [ModulePlacer.PlaceDataWords](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs) (reference walk), [ModulePlacer.Layout](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.Layout.cs) (cached per-version template / mask / walk order, production path) |
| Section 7.9 | Format information placement (two redundant copies) | [ModulePlacer.PlaceFormat](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs) |
| Section 7.8.2 | Format information bits and mask (BCH + XOR mask) | [QRCodeConstants.GetFormatBits](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs) |
| Section 7.10 | Version information (version 7+, two 3×6 patterns) | [ModulePlacer.PlaceVersion](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs), [QRCodeConstants.GetVersionBits](../../../src/FeatherQR/Internals/StandardQR/QRCodeConstants.cs) |

Reference tests: [ModulePlacerPlaceDataWordsParityTest](../../../tests/FeatherQR.Tests/StandardQr/ModulePlacerPlaceDataWordsParityTest.cs), bit-parallel placement vs. naive per-module Section 7.7.3 reference; [ModulePlacerLayoutParityTest](../../../tests/FeatherQR.Tests/StandardQr/ModulePlacerLayoutParityTest.cs), the cached-table placer (function modules, blocked mask, data placement) vs. the reference painters and walk for every version.

## Data Masking

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 7.8.2 | The 8 mask pattern formulas (patterns 0–7) | [ModulePlacer.MaskCode / Pattern0–Pattern7](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.cs) |
| Section 8.8.2 | Penalty scoring rules 1–4 for mask selection | [ModulePlacer.Masking.cs](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.Masking.cs), bit-parallel implementation of all four rules, plus SIMD variants ([x64 AVX2](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.Masking.Simd.cs), [ARM AdvSimd](../../../src/FeatherQR/Internals/StandardQR/ModulePlacer.Masking.Simd.Arm.cs)) selected at runtime |

Reference tests: [ModulePlacerMaskPackedParityTest](../../../tests/FeatherQR.Tests/StandardQr/ModulePlacerMaskPackedParityTest.cs), packed masking/scoring vs. naive byte-per-module reference formulas; [ModulePlacerMaskSimdParityTest](../../../tests/FeatherQR.Tests/StandardQr/ModulePlacerMaskSimdParityTest.cs) / [ModulePlacerMaskAdvSimdParityTest](../../../tests/FeatherQR.Tests/StandardQr/ModulePlacerMaskAdvSimdParityTest.cs), vectorized tiers vs. the scalar bit-packed kernels.

## Decoding Pipeline

```
Image ──> Binarization ──> Finder detection ──> Alignment pattern ──> Perspective sampling ──┐
                                                                                             ├──> Format info ──> Unmask/Extract ──> Deinterleave ──> RS correction ──> Bit stream ──> Text
Module matrix (QRCodeData / span) ───────────────────────────────────────────────────────────┘
```

Design notes (WHAT/WHY and scope tiers) are in [Standard QR Decoder](standardqr-decoder.md).

| Spec reference | Topic | Implementation |
|---|---|---|
| Section 7.8.2 | Format information decoding (Hamming match against all 32 valid patterns) | [FormatInformationDecoder](../../../src/FeatherQR/Internals/StandardQR/FormatInformationDecoder.cs) |
| Section 7.7.3 | Codeword extraction (inverse zigzag walk + unmasking on the fly) | [QRMatrixDecoder.ExtractCodewords](../../../src/FeatherQR/Internals/StandardQR/QRMatrixDecoder.cs) |
| Section 7.6 | Codeword deinterleaving into RS blocks | [QRMatrixDecoder.DeinterleaveCodewords](../../../src/FeatherQR/Internals/StandardQR/QRMatrixDecoder.cs) |
| Section 8.5 | Reed-Solomon error correction (syndromes → Berlekamp-Massey → Chien → Forney) | [EccBinaryDecoder.TryCorrect](../../../src/FeatherQR/Internals/BinaryDecoders/EccBinaryDecoder.cs), shared across symbologies |
| Section 7.4 | Bit stream decoding (mode segments, character count, ECI, Kanji via the shared JIS X 0208 table) | [QRBinaryDecoder.DecodeBitStream](../../../src/FeatherQR/Internals/StandardQR/QRBinaryDecoder.cs); segment payload groups and byte-charset heuristics are shared with the other symbology decoders via [SegmentDecoders](../../../src/FeatherQR/Internals/BinaryDecoders/SegmentDecoders.cs) |
| Section 6.3.3 | Finder pattern detection (1:1:3:1:1 ratio scan + cross checks) | [FinderPatternFinder](../../../src/FeatherQR/Internals/ImageDecoders/FinderPatternFinder.cs), scalar row walk plus a bit-identical SIMD mask walk (AVX2 / ARM NEON / portable 128-bit tiers) selected at runtime; parity: [FinderPatternFinderParityTest](../../../tests/FeatherQR.Tests/StandardQr/FinderPatternFinderParityTest.cs) |
| Section 6.3.6 / Annex E | Alignment pattern detection (light-dark-light core + grid-axis border-ring validation) | [AlignmentPatternFinder](../../../src/FeatherQR/Internals/StandardQR/AlignmentPatternFinder.cs), scalar row walk plus a result-identical SIMD mask walk (AVX2 / ARM NEON / portable 128-bit tiers) selected at runtime; parity: [AlignmentPatternFinderParityTest](../../../tests/FeatherQR.Tests/StandardQr/AlignmentPatternFinderParityTest.cs) |
| - | Projective grid sampling (4-point perspective transform) | [PerspectiveTransform](../../../src/FeatherQR/Internals/ImageDecoders/PerspectiveTransform.cs), shared across symbologies, [QRImageDecoder.SampleGrid](../../../src/FeatherQR/Internals/StandardQR/QRImageDecoder.cs), scalar loop plus bit-identical SIMD row kernels (AVX2 / portable 128-bit for ARM NEON and WASM) selected at runtime; parity: [SampleGridParityTest](../../../tests/FeatherQR.Tests/StandardQr/SampleGridParityTest.cs) |
| - | Binarization (Otsu), module-size measurement, dimension estimation | [QRImageDecoder](../../../src/FeatherQR/Internals/StandardQR/QRImageDecoder.cs) |

Reference tests: [QRCodeDecoderRoundTripTest](../../../tests/FeatherQR.Tests/StandardQr/QRCodeDecoderRoundTripTest.cs) (encode→decode round-trips, error injection), [EccBinaryDecoderUnitTest](../../../tests/FeatherQR.Tests/Shared/EccBinaryDecoderUnitTest.cs) (correction capacity boundaries), [QRCodeDecoderZXingCrossTest](../../../tests/FeatherQR.Tests/StandardQr/QRCodeDecoderZXingCrossTest.cs) (independent-encoder cross-validation), [QRCodeDecoderImageTest](../../../tests/FeatherQR.Tests/StandardQr/QRCodeDecoderImageTest.cs) (rotation/mirror/scale/inversion image cases), [QRCodeDecoderPerspectiveTest](../../../tests/FeatherQR.Tests/StandardQr/QRCodeDecoderPerspectiveTest.cs) (keystone envelope, rotation+perspective combinations).

## Maintenance Notes

- When adding or moving a spec-referenced implementation, update this map, but keep the detailed explanation (bit layouts, formulas, constraints) in the code comment next to the implementation, not here.
- Parity tests deliberately keep a *naive* reference implementation of the spec text; they are the executable form of the specification and should stay simple even when the production kernels change.
- Components marked "shared across symbologies" live outside `Internals/StandardQR`; the shared/per-symbology split is defined in [QR Symbology Architecture](qrcode-symbologies.md).
