# Kanji encoding: code inventory

Research for [kanji-encoding-plan.md](../kanji-encoding-plan.md), surveyed on 2026-09-28 at `cab60b1`, so that a later session can start a phase without surveying the code again. Components are named by type and member rather than line, because lines move. This file is deleted with the plan.

## What exists for decoding and is reused

| Piece | Where | State |
|---|---|---|
| Forward table, 13-bit value → UTF-16 | `ShiftJisKanjiTable` (`Lookup`, `IsStructurallyValid`) | 8,192 entries, 6,879 assigned, 16 KB of RVA data; generated, golden digest in `ShiftJisKanjiTableUnitTest` |
| Generator | `tools/QRInteropFixtures/KanjiTableGenerator.cs` (`generate-kanji-table`) | Reads `kanji-sweep.tsv` and takes the zxing-cpp column. `Validate` refuses unless 8,023 swept cells, 6,879 assigned, and exactly the seven known divergences (`KnownDivergences`); `ToIndex13` is the compaction |
| Sweep data | `tools/QRInteropFixtures/kanji-sweep.tsv` | Columns `sjis index13 zxingcpp cp932`, 8,023 rows; qrtool wrote them and zxing-cpp read them back (`KanjiSweepProbe`) |
| Count widths | `EncodingModeExtensions.GetKanjiCountIndicatorLength`, `MicroQRConstants.GetKanjiCountIndicatorLength`, `RmQRConstants.GetKanjiCountIndicatorLength` | Standard QR 8/10/12; Micro QR 3/4 (throws below M3); rMQR 2-7, pinned by derivation, with only widths 4, 5 and 7 read from a qrtool fixture |
| Mode indicator values | `QRBinaryDecoder.ModeKanji` (`1000`), `MicroQRBinaryDecoder.ModeKanji` (3, written in `version-1` bits), `RmQRConstants.KanjiModeIndicatorValue` (`100`) | Decode side only |
| Capacity | `QRCodeConstants.capacityBaseValues` has a Kanji column (v40-L 1,817); `CreateCapacityTable` drops it, and `CapacityTable` / `CapacityDict` are unused | Standard QR only; the Micro QR and rMQR tables have no Kanji column |
| Payload decoder | `SegmentDecoders.DecodeKanjiPayload` | Ignores the current ECI charset. No test puts a Kanji segment after an ECI header |

## Insertion points

- **`EncodingMode`** (internal): `Numeric = 1, Alphanumeric = 2, Byte = 4, ECI = 7`, written raw as Standard QR's indicator. It gains `Kanji = 8`. Sites that switch on it: `EncodingModeExtensions.GetCountIndicatorLength`; `ModeSegment.Mode`; `ModeSegmenter.PayloadBits`; `TextAnalyzer.DetermineEncoding` / `CalculateLength`; `QRBinaryEncoder.WriteMode` / `WriteData` / `WriteSegments`; `QRCodeGenerator.TryGetVersionInRange` and the `Utf8Bom && Byte` checks; `QRSegmentPlanner.CanPlanBeatSingleMode` / `PricePlan` / `MeasurePlan`; `StructuredAppendPlanner` (single-mode chunk bits, `TakesPlan`); `MicroQRConstants.GetModeIndicatorValue` / `IsModeSupported` / `GetCountIndicatorLength`; `MicroQRBinaryEncoder(.Segmented)`; `MicroQRSegmentPlanner`; `MicroQRCodeGenerator` (version selection, `GetRequiredBits`, `GetMaxDataLength`); `RmQRConstants.GetModeIndicatorValue` / `GetModeIndex` / `ModeCount = 3`; `RmQRBinaryEncoder(.Segmented)`, whose headers are literal constants rather than a lookup; `RmQRVersionSelector.FitCapacities`, indexed by `ModeCount`; `RmQRSegmentPlanner`.
- **`ModeSegment`**: `ModeIndex` 0/1/2, with a `Debug.Assert(modeIndex <= 2)`, and `Mode` defaults to Byte.
- **`ModeSegmenter`**: seven states (`StateNumeric0-2`, `StateAlnum0-1`, `StateByte`, `StateStart`); keys are `cost << 3 | state`, and ties go to the lowest state; the parent table is 2 bytes a character, with 6 of 8 bits used (a Kanji predecessor needs 3 more); the keyed loops use increments that encode the state numbering. Entry points: `ComputeCosts` (with `allowAlnum` / `allowByte`, passed only by Micro QR), `LongestPrefixWithinBudget` (no flags), `Reconstruct` / `WalkBack`, `CountRuns`, `FillUnitCounts`. `LongestDenseRuns` has no caller.
- **Lanes**: `ModeSegmenter.Lanes.Vector256.RunLanes`, `ModeSegmenter.Lanes.Arm64.RunLanesAdvSimd`, `StructuredAppendPlanner.Lanes.Vector256`, `StructuredAppendPlanner.Lanes.Arm64` (saturating 16-bit). All hard-code the six cost states with every mode allowed.
- **`TextAnalyzer`**: scalar, SSE2, AVX2 and AdvSimd copies of one flag scan (`hasNonNumeric`, `hasNonAlphanumeric`, `hasNonAscii`, `hasNonIso88591`), then the shared helpers. The eligibility pass goes after `DetermineEciMode` resolves UTF-8, so the vector scans do not change.
- **Generators**: `Create` branches on `Segmentation` to `CreateOptimal` / `CreateOptimalTo`, in all three. Micro QR always analyses with `EciMode.Default`. rMQR validates an explicit ECI in `RmQRCodeGenerator.ValidateEci`.
- **Structured Append**: `QRCodeGenerator.CreateStructuredAppend` decides the charset from the whole text and forces it on each chunk. `StructuredAppendPlanner.Parity` XORs Latin-1 or UTF-8 bytes plus the BOM (there is a NEON copy). `SingleModeLength` is the Numeric → Alphanumeric → Byte closed form over `StructuredAppendScanner.ModeBoundaries` / `Utf8PrefixLength`.
- **Sizing and refusals**: all three `TryGetRequiredBufferSize` run the full analysis and selection. `DoesNotFitMessage`, `DoesNotFitSetMessage`, `NotFittingError` / `FormatDataLength` / `GetMaxDataLength` format by mode.

## Premises that stop holding in the eligible path

Each is a place where a stale bound skips a winning plan without failing.

- `ModeSegmenter`: the Byte-only fast paths for a character outside the alphanumeric class (`CostsLatin`, `TrackedLatin`, `TrackedGeneral`, `PrefixLatin`, and both lane families). `ClassOf` returns `ClassOther` for every character ≥ 128.
- `ModeSegmenter.CheapestSixths`: prices a non-alphanumeric character at `48 × ByteCost` sixths. Kanji is 78, UTF-8 kana 144.
- `QRSegmentPlanner.PlanCouldBeatSingleMode` / `PlanIsOneByteRun` (and `CanPlanBeatSingleMode`): look only at digit and alphanumeric runs. A Kanji run pays for its header too.
- `MicroQRSegmentPlanner` trivial bound: assumes the narrowest count indicator is `version + 1`. Kanji's is `version`.
- `RmQRSegmentPlanner`: `MinCountBitsAny = 3`, `ComputeFloor`, `UpperBound` and the `PlanFits` memo key cover three modes. The narrowest Kanji width is 2 (R7x43).
- `StructuredAppendPlanner`: `CanPlanHelp`, `oneRunPlans`, `SingleModeLength` ("a prefix's mode changes at most twice"), `CheapestPayloadBits`.

## Tests that pin today's UTF-8 output for Japanese

Each needs a decision, not a blind update. A test about UTF-8 keeps testing UTF-8, by forcing `EciMode.Utf8` or by using a text outside the eligibility rule; a test about Japanese text changes its expectation.

- `QRCodeGeneratorUnitTest` (about 74 Japanese literals), `QRCodeGeneratorVersionBoundaryTest`, `QRBinaryEncoderUnitTest`, `TryGetRequiredBufferSizeTest`
- `Rendering/QRCodeVisualCompatibilityTest`: `Create_Default_PixelsMatchSample("脂至肢", L)` moves; its `Create_Utf8_*` twin does not. The other literals there carry halfwidth katakana and stay ineligible.
- `MicroQRSegmentationTest`, `RmQRCodeGeneratorUnitTest`, the `StructuredAppend*` tests on Japanese text
- Structured Append S8: the encoder-side parity is pinned on ASCII, Latin-1 and non-Japanese UTF-8 only, and 6.5 adds the Japanese cases.
- rMQR oracle tests that assume no Kanji: `RmQRBinaryEncoderUnitTest.FixtureIdsWithoutEci` excludes the Kanji fixtures. `RmQRCodewordEncoderUnitTest` and `RmQRModulePlacerUnitTest` map every mode other than N and A to Byte. `RmQRConstantsOracleTest` parses `manifest.Mode` into `EncodingMode`, which a single-character Kanji fixture breaks today.

## Fixtures and oracles

| Symbology | Writes Kanji | Reads Kanji | Committed Kanji fixtures |
|---|---|---|---|
| Standard QR | ZXing.Net (Shift_JIS hint, CP932, so never the seven cells), qrtool, CodeGlyphX (no ECI) | zxing-cpp (JIS X 0208); ZXing.Net (CP932) | 5 from ZXing.Net at v1-v12, the 8- and 10-bit bands; since 6.6 also 27-M and 40-L (12 bits) |
| Micro QR | qrtool (M3/M4; raw Shift_JIS via `--read-from`) | zxing-cpp only | 5 from qrtool |
| rMQR | qrtool | zxing-cpp only | 4 from qrtool (R11x43, R13x59, R15x59 with the seven divergent cells, R17x139); since 6.6 also R7x43, R7x59, R9x43 and R9x139 (widths 2, 3 and 6) |
| Structured Append | CodeGlyphX (explicit parts) | zxing-cpp, ZXing.Net, CodeGlyphX | `codeglyphx/byte-utf8-japanese-v5-m-*of4`: Kanji with no ECI, parity 176; `qrcodegenerator/...-*of3`: the same text as UTF-8, parity 6 |

- libzint cannot write Kanji: from `byte[]` it writes Byte mode under ECI 20.
- ZXing.Net cannot read Micro QR or rMQR.
- `tests/FeatherQR.Tests` references only ZXing.Net, so zxing-cpp checks run through `tools/QRInteropFixtures` by hand (`spot-check-microqr`, `spot-check-rmqr`, `spot-check-structured-append`).
- qrtool is fetched per machine by `get-qrtool.ps1`, and it was absent when this was surveyed.
- `tools/QRImageDecodeSweep` has no Kanji cases.

## Statements that go stale

- Specs: [qrcode-symbologies.md](../../specs/qrcode-symbologies.md) ("Why Kanji mode is read but never written", the Kanji and ECI 20 scope rows, the `EncodingMode` row of the component table); [standardqr-encoder.md](../../specs/standardqr-encoder.md) (the data modes, "Not implemented", "Kanji. Still not encoded", the "No Kanji mode when encoding" decision, and the segmenter's seven-state, Byte-run and "Japanese hold neither run" premises); [rmqr-encoder.md](../../specs/rmqr-encoder.md) (the Kanji decision row and paragraphs, and the widths resting on derivation); [microqr-spec-map.md](../../specs/microqr-spec-map.md), [rmqr-spec-map.md](../../specs/rmqr-spec-map.md), [standardqr-decoder.md](../../specs/standardqr-decoder.md), [microqr-decoder.md](../../specs/microqr-decoder.md) ("decode only"); [qrcode-test-fixtures.md](../../specs/qrcode-test-fixtures.md) (the Kanji oracle row); the index description of `standardqr-encoder.md`.
- Parent plan: the "non-issue" sentence under Kanji encoding (see K3).
- User docs: `README.md` (encoding FAQ and its table row, the mode table's "decode only" row and note, the mixed-mode sections); `docs/migration.md` (the 2.0.0 overview's "unchanged but for two things", and a new section; the 1.2.0 and 0.9.0 sections are history and stay); `docs/data-capacity.md` (no Kanji column; 'あ' as the UTF-8 test character).
- XML docs: `QRCodeGenerator`, `MicroQRCodeGenerator` and `RmQRCodeGenerator` ("Kanji is never written"), then the committed `src/FeatherQR.Playground/wwwroot/api/index.html`, regenerated by `tools/public_api.cs --html`.
- Code comments: `SegmentDecoders` (Kanji payload), `QRBinaryDecoder`, `EncodingModeExtensions`, `MicroQRConstants`, `MicroQRBinaryDecoder`, `RmQRConstants`.
- Test remarks: `KanjiFixtureTest`, `KanjiUnmappedCharacterEndToEndTest` ("no generator in this library emits Kanji"), `ContentVerdictImageDecodeTest`. Also stale today, unrelated to this plan: a remark in `ShiftJisKanjiTableUnitTest` still says `InvalidBitstream` / `UnsupportedContent` where the code returns `UnmappedCharacter`.
