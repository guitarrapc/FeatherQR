# FNC1 support: research

Research behind [fnc1-support-plan.md](../fnc1-support-plan.md), so a later session can continue without surveying again. It is deleted with the plan. Code references are at `main` f4b74de (2026-10-10).

## Sources

| Source | What was read |
|---|---|
| ISO/IEC 18004:2006, the edition with a public copy (law.resource.org, incorporated by reference) | 6.3.8, 6.4.8, 6.4.8.1, 6.4.8.2, 13.2, 13.3, 13.4, Annex F. The 2015 and 2024 editions were not read. The code comments cite the 2015 numbering, so map the clauses there when the code is written |
| ISO/IEC 23941:2022 (rMQR) | Not read. Its mode table is known only through the implementations below and Wikipedia's rMQR article, which cites §7.4.1 for the table and §7.4.8.2 / §7.4.8.3 for the two FNC1 modes |
| zxing-cpp, master 2c3dcfef in `.references/` | `QRDecoder.cpp`, `QRCodecMode.cpp`, `SymbologyIdentifier.h`, `Content.cpp`, the unit tests `QRDecodedBitStreamParserTest`, `QRModeTest`, `RMQRDecoderTest`, `QREncoderTest`, and the sample metadata (`.toml`) |
| qrcode2 (`.references/qrcode-rust2`) | `bits/mode_indicator.rs`, `bits/fnc1.rs` |
| libzint, `backend/qr.c` on master | Header writing (`qr_binary_segs`), the Alphanumeric cost for `%` under GS1, the GS1 ECI / Structured Append warnings |
| ZXing.Net, `.references/zxing.net` | `DecodedBitStreamParser`, `Encoder`, `MinimalEncoder` |
| CodeGlyphX 3.0.0 source in `.references/`, and the 2.1.0 package the repository pins | `QrEncoder.Data`, `QrPayloadParser`, `RmQrEncoder`, `RmQrPayloadParser`, the public options |

## The standard (ISO/IEC 18004:2006)

- 6.3.8: FNC1 mode applies to the whole symbol and is not affected by later mode indicators. Micro QR has no FNC1 mode.
- 6.4.8: the FNC1 indicators precede the indicators that encode data, and their use requires the decoder to transmit the symbology identifier.
- 6.4.8.1, first position (`0101`): GS1 data. Used once, immediately before the first data mode indicator and after any ECI or Structured Append header. A field separator is `%` in Alphanumeric mode or GS (0x1D) in Byte mode, and a literal `%` is written `%%`. The decoder transmits 0x1D for `%` and one `%` for `%%`. Example 1 gives a full bit sequence (FNC1, a 29-digit Numeric segment, a 9-character Alphanumeric segment that starts with the separator) and its transmission `]Q3…<GS>…`. Example 2 encodes `123%` as `123%%`, five Alphanumeric characters, so the count indicator counts the characters as written.
- 6.4.8.2, second position (`1001`): data in an industry format registered with AIM. The indicator is followed by one 8-bit application indicator: a two-digit number as its value (00-99) or one letter `a-z` / `A-Z` as its ASCII value plus 100. The same placement and `%` rules apply. The decoder transmits the application indicator as the first one or two characters before the data. The example uses indicator 37, an Alphanumeric segment and a Byte segment, and is transmitted `]Q537…`.
- 13.2 and Annex F: the identifier is `]Qm`. m = 0 Model 1, 1 no ECI protocol, 2 ECI protocol, 3 / 4 FNC1 first position without / with ECI protocol, 5 / 6 FNC1 second position without / with ECI protocol. Micro QR is always `]Q1`.
- 13.4: the implied FNC1 has no byte value, so the identifier signals it. Elsewhere in the data it is `%` (Alphanumeric) or GS (Byte), and both are transmitted as 0x1D.

The standard does not say in which order `%%%` is read. Every reader below reads left to right inside one segment.

## rMQR mode indicators

| Evidence | 101 | 110 |
|---|---|---|
| zxing-cpp `CodecModeForBits` rMQR table, unit-tested in `QRModeTest` | FNC1 first position | FNC1 second position |
| qrcode2 `push_mode_indicator` for `RectMicro` | FNC1 first position | FNC1 second position |
| libzint `qr_binary_segs` writes FNC1 as 5 in 3 bits for rMQR | FNC1 (GS1 only) | not written |
| CodeGlyphX rMQR writer (`AppendBits(5, 3)`) and parser | FNC1 first position | not written |
| Wikipedia, table citing ISO/IEC 23941 §7.4.1 | FNC1 first position | FNC1 second position (the prose says 111, which contradicts the same article's table and ECI row) |
| Probe: libzint and CodeGlyphX rMQR GS1 symbols read by zxing-cpp | `]Q3`, ContentType GS1 | not tested (no writer) |

zxing-cpp's unit tests also hold an R15x59-H GS1 module matrix (`RMQRDecoderTest.RMQRCodeR15x59H_GS1`). It is Apache-2.0 test data and can be imported with provenance, as the real-image corpus was, if a second rMQR reference is wanted.

## Probe (2026-10-10)

A scratch file-based app with ZXingCpp 0.5.2, ZXing.Net 0.16.11 and CodeGlyphX 2.1.0 wrote symbols with each writer, rendered them at 4 px per module with a 4-module quiet zone, and read them with zxing-cpp (`TextMode.Plain` and the default `HRI`) and, for Standard QR, ZXing.Net. Phase 0 of the plan turns it into a QRInteropFixtures command.

| Writer and input | zxing-cpp reads | ZXing.Net reads |
|---|---|---|
| libzint `gs1` (also `gs1=true`), QR and rMQR, `[01]…[10]…[17]…` or `(01)…(10)…(17)…` | `]Q3`, GS1, the element string with GS after the variable-length field only | QR: same |
| libzint `gs1` with raw GS separators | refused at creation: error 251, control characters are not supported by GS1 | |
| libzint `gs1,eci=26`, QR and rMQR | GS1, `HasECI` true, identifier property still `]Q3` | |
| libzint `gs1`, Micro QR | the option is ignored: the bracketed text is written as plain text (`]Q1`) | |
| ZXing.Net `GS1_FORMAT`, element string with GS | `]Q3`, GS1, Byte mode, exact | `]Q3`, exact |
| ZXing.Net `GS1_FORMAT` with `CHARACTER_SET` UTF-8 and `é` (ECI then FNC1) | GS1, `HasECI`, exact. `BytesECI` starts `]Q4` | |
| ZXing.Net `GS1_FORMAT`, `123%` | `123<GS>`: the literal `%` was not doubled | `123<GS>` |
| CodeGlyphX `EncodeGs1`, element string with GS | `]Q3`, GS1, exact | `]Q3`, exact |
| CodeGlyphX `EncodeGs1`, `123%` | `123%` | `123%` |
| CodeGlyphX `EncodeGs1`, `A<GS>%B` | `A%<GS>B` | `A%<GS>B` |
| CodeGlyphX `EncodeGs1`, `A<GS><GS>B` | `A%B` | `A%B` |
| CodeGlyphX second position, indicators 0, 37, 99, `'A'+100`, `'a'+100`, the 6.4.8.2 example text | `]Q5`, ContentType Text, the indicator as `00`, `37`, `99`, `A`, `a` before the text, exact | indicator 0: `]Q5` and an empty text. The others: NullReferenceException. It does not read the application indicator |
| CodeGlyphX second position 37, `AB<GS>%CD`, `AB<GS><GS>CD`, `AB%<GS>CD` | `37AB%<GS>CD`, `37AB%CD`, `37AB%<GS>CD` (the first two wrong, the third right) | exception |
| CodeGlyphX first position, UTF-8 ECI, `10é<GS>17140704` (FNC1 then ECI) | GS1, `HasECI`, exact | `]Q4`, exact |
| CodeGlyphX `RmQrCodeEncoder.EncodeGs1`, `(01)…` | `]Q3`, GS1, R17x43, exact (Byte mode) | |
| CodeGlyphX `RmQrCodeEncoder.EncodeGs1`, raw GS after a fixed-length AI | refused by its GS1 validator | |

What the probe establishes:

- A lone GS followed by a GS or a literal `%` cannot share an Alphanumeric segment: GS GS is written `%%` and read as one `%`, and GS `%` is written `%%%` and read as `%` GS. Both readers agree, so the reading is fixed and the encoder has to avoid the pair.
- Writer output is not evidence of the `%` rule. Two of the three writers get it wrong for some input.
- Header order differs: ZXing.Net writes ECI then FNC1 (the standard's order), libzint and CodeGlyphX write FNC1 then ECI. zxing-cpp reads both.
- zxing-cpp is the reader to compare against for both positions and both symbologies. Its `SymbologyIdentifier` property does not add the ECI offset. Use `HasECI` or `BytesECI` for that. Its default `TextMode` is `HRI`, which formats GS1 data as `(AI)` text, so read with `TextMode.Plain`.
- ZXing.Net is a first-position-only reader and writer, for Standard QR.

## Code inventory

### Decode

- [QRBinaryDecoder.DecodeBitStream](../../../../src/FeatherQR/Internals/StandardQR/QRBinaryDecoder.cs): one pass into the caller's span. `ModeFnc1First` / `ModeFnc1Second` return `UnsupportedContent`. ECI may appear anywhere and sets the charset for later Byte segments. A Structured Append header is read anywhere, and only a second header, a truncated one or an index past the count is `InvalidBitstream`. Each segment checks room before writing, which over-estimates under FNC1 because `%%` only shrinks the text.
- [RmQRBinaryDecoder](../../../../src/FeatherQR/Internals/RmQR/RmQRBinaryDecoder.cs): no constants for 101 / 110, both fall to the `default` arm (`InvalidBitstream`, "reserved").
- [MicroQRBinaryDecoder](../../../../src/FeatherQR/Internals/MicroQR/MicroQRBinaryDecoder.cs): M4 indicators 4-7 are `InvalidBitstream`. No change.
- [SegmentDecoders](../../../../src/FeatherQR/Internals/BinaryDecoders/SegmentDecoders.cs): `DecodeAlphanumericPayload` is shared by the three symbologies and writes table characters straight into the destination. `DecodeBytePayload` already turns 0x1D into U+001D under ISO-8859-1 and UTF-8. `ReadEciDesignator` is shared.
- [QRMatrixDecoder](../../../../src/FeatherQR/Internals/StandardQR/QRMatrixDecoder.cs) and [RmQRMatrixDecoder](../../../../src/FeatherQR/Internals/RmQR/RmQRMatrixDecoder.cs) build the decode info and zero `charsWritten` on failure.
- [QRCodeDecodeInfo](../../../../src/FeatherQR/QRCodeDecodeInfo.cs) (`Status`, `Version`, `EccLevel`, `MaskPattern`, `ErrorsCorrected`, `Corners`, `StructuredAppend`) and [RmQRCodeDecodeInfo](../../../../src/FeatherQR/RmQRCodeDecodeInfo.cs) (`Status`, `Version`, `EccLevel`, `ErrorsCorrected`, `Corners`) are `readonly record struct`s with internal constructors. Neither reports ECI. [QRStructuredAppend](../../../../src/FeatherQR/QRStructuredAppend.cs) is the pattern for a library-built value in the info.
- Docs that call FNC1 unsupported: [DecodeStatus.UnsupportedContent](../../../../src/FeatherQR/DecodeStatus.cs), the remarks of [QRCodeDecoder](../../../../src/FeatherQR/QRCodeDecoder.cs), the class comment of `QRBinaryDecoder`, and the generated Playground API page.

### Encode

- [QRCodeGeneratorOptions](../../../../src/FeatherQR/QRCodeGeneratorOptions.cs): `EciMode`, `Utf8Bom`, `Version`, `QuietZoneSize`, `MaskPattern`, `BoostEccLevel`, `Segmentation`, `AllowKanji`, and a C# 7.3 constructor whose parameters follow the shared order. Single values are validated in `init` (`MaskPattern`). No option pair is refused today, although the generators' XML docs list "options contradict each other" under `ArgumentException`.
- [QRCodeGenerator](../../../../src/FeatherQR/QRCodeGenerator.cs): `Create` sends any non-`Single` segmentation to `CreateOptimal` with one compare, then takes `CreateAutomatic` when the version is open and no boost is asked. `TryGetRequiredBufferSize`, `CreateStructuredAppend` (validates, then plans the set), `TryGetVersionInRange` (header bits from `EciMode.GetStandardQrHeaderBits` plus mode, count and data), the `QRConfiguration` record, `EncodeData` (Structured Append, mode with ECI, count, data, padding) and `EncodeDataSegmented`.
- [QRBinaryEncoder](../../../../src/FeatherQR/Internals/StandardQR/QRBinaryEncoder.cs): `WriteStructuredAppend`, `WriteMode` (ECI then mode), `WriteSegments` (ECI once, then each run), and the Alphanumeric writers (SSSE3, AdvSimd, PackedSimd, scalar) over `CharacterSets.AlphanumericValues`. U+001D is outside the table everywhere.
- [TextAnalyzer](../../../../src/FeatherQR/Internals/TextAnalyzer.cs), shared, SIMD-tiered: picks one mode and the ECI for the whole text and counts `DataLength`. GS makes a text Byte today.
- [ModeSegmenter](../../../../src/FeatherQR/Internals/ModeSegmenter.cs), shared by the three planners: the character-class table (`%` is Alphanumeric, 0x1D is Byte-only), the costs in sixths of a bit, and the lane kernels in the `ModeSegmenter.Lanes*` partials. [QRSegmentPlanner](../../../../src/FeatherQR/Internals/StandardQR/QRSegmentPlanner.cs) prices ECI as a header. [StructuredAppendPlanner](../../../../src/FeatherQR/Internals/StandardQR/StructuredAppendPlanner.cs) uses `GetStandardQrHeaderBits` throughout and is not reached with FNC1 (the set is refused).
- [RmQRCodeGeneratorOptions](../../../../src/FeatherQR/RmQRCodeGeneratorOptions.cs) and [RmQRCodeGenerator](../../../../src/FeatherQR/RmQRCodeGenerator.cs): the same split between `Single` and `CreateOptimal`. [RmQRBinaryEncoder](../../../../src/FeatherQR/Internals/RmQR/RmQRBinaryEncoder.cs) writes ECI then mode and count in one append, has an ASCII path kept separate for JIT layout, a UTF-8 path that writes its ECI on its own, and an unchecked byte writer that relies on an exact fit. [RmQRBinaryEncoder.Segmented](../../../../src/FeatherQR/Internals/RmQR/RmQRBinaryEncoder.Segmented.cs) throws unless a run's unit count equals its length outside UTF-8, which `%%` breaks.
- [RmQRVersionSelector](../../../../src/FeatherQR/Internals/RmQR/RmQRVersionSelector.cs): `GetRequiredBits`, `GetMaxDataLength`, and the auto-fit tables (`FitOrders`, `FitCapacities`, `FitHeightMasks`) indexed by mode, level, ECI and strategy. FNC1's 3 or 11 header bits are not a dimension of those tables.

### Tests and tools that pin today's behaviour

- `QRBinaryDecoderUnitTest.UnsupportedModes_ReturnUnsupportedContent` (`[Arguments(ModeFnc1First)]`, `[Arguments(ModeFnc1Second)]`).
- `RmQRBinaryDecoderUnitTest.Decode_ReservedModes_AreInvalidBitstream` (101 and 110).
- `StructuredAppendDecodeTest.HeaderAfterASegment_IsReported` and `HeaderAfterEci_IsReported`, which the FNC1 placement rule must not change.
- The unit tests reference ZXing.Net only. ZXingCpp, CodeGlyphX and libzint are reached from `tools/QRInteropFixtures` and `tools/QRImageDecodeSweep`. No test or tool asks a writer for FNC1, and `ContentType` / `SymbologyIdentifier` appear only in two diagnostic printouts.
- The fixture manifest (`payloadText`, `payloadUtf8Hex`, `eciCharset`, `mode`, `structuredAppend`, mirrored by `FixtureLoader`) has no FNC1 field.
- The real-image corpus keeps expected text in a `.txt` beside each image (`-text` in `.gitattributes`). `qrcode-2/gs1-figure-4.15.1-2.txt` holds the element string with two raw 0x1D bytes and no identifier. The sweep's content column (`Report.IsContent`) counts it four times, once per rotation.
- Playground: `QrInterop.CreateStandardData` / `CreateRmData` map the request, the benchmark paths repeat the options, `index.html` and `main.js` carry the checkboxes (`AllowKanji` and `EccBoost` are the model), and `share-payload.js` carries new state fields in share links.
