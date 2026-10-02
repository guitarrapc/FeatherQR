# rMQR Encoder

This is the design record for the rMQR Code (ISO/IEC 23941) encoder, `RmQRCodeGenerator`: its behaviour, symbol parameters, pipeline rationale, and the up-front decisions that gave the implementation phases one shared understanding. The [spec-to-code map](rmqr-spec-map.md) indexes the normative details and their implementation. The implementation order came from the rMQR implementation plan, since retired into this record and that map. The decoder has its own record, [rMQR Decoder](rmqr-decoder.md).

Status: shipped (Phase 5 on 2026-08-15, adversarial review on 2026-08-16). This record was written on 2026-08-15, before any `src/` code existed. Phase 5 is complete. Its parts are:

- Phase 5.1b: the tables (`Internals/RmQR/RmQRConstants`).
- 5.2: the data model (`RmQRCodeData`).
- 5.3: the bit stream (`RmQRBinaryEncoder`) and the fit logic (`RmQRVersionSelector`).
- 5.4: Reed-Solomon and interleaving (`RmQRCodewordEncoder`).
- 5.5: placement (`RmQRModulePlacer`).
- 5.6: the public `RmQRCodeGenerator`, which met the encoder MVT (zxing-cpp read 256/256 symbols).
- 5.7: rendering (`RmQRCodeImageBuilder` / `SymbolRenderer`).

`RmQRConstantsUnitTest` (structural invariants) and `RmQRConstantsOracleTest` (the committed two-lineage corpus) check every parameter below. See the [Verification record](#verification-record). Measured performance and lessons learned follow.

---

## What

`RmQRCodeGenerator` converts text into an rMQR module matrix through the ISO/IEC 23941 encoding pipeline:

```
Text
  -> mode analysis
  -> version fit (exact version, or fit strategy within an optional height constraint)
  -> data bit stream and padding
  -> Reed-Solomon ECC per block
  -> data / ECC interleaving
  -> function-pattern and data placement
  -> fixed data mask
  -> format information (two copies)
  -> RmQRCodeData or byte-per-module matrix
```

### Public entry points (reshaped 2026-08-28, see [qrcode-symbologies.md](qrcode-symbologies.md#public-api-direction))

The names and overload sets match the shipped `MicroQR*` family member for member (reviewed against `MicroQRCodeGenerator`, `MicroQRCodeDecoder`, `MicroQRCodeImageBuilder`, `MicroQRCodeData`, the renderer overloads and `QrImageBuilderApiParityTest`), adding only the rectangular geometry and the fit options. These names are the contract for the implementation phases. Any deviation must first change this spec.

Options travel in `RmQRCodeGeneratorOptions`, not a parameter list. The original shape put every option in the signature and failed twice before release. `EciMode` had no position that kept the parameter order, so it moved into method names (`CreateRmQRCodeWithEci` and two siblings), doubling the method count. `RmQRSegmentation` then had to be appended to four signatures, since only the end of the list keeps source compatibility. The struct cut the surface from 10 methods with up to 9 parameters to 5 with up to 4. No rMQR API had been released, so the old shape was deleted, not made obsolete.

`options` has had `= default` from the start. Standard QR and Micro QR could not until 2.0.0: alongside their released parameter-list overloads, a defaulted options parameter would have made `Create(text, ecc)` ambiguous between the two overload sets. rMQR never had that collision, and since 2.0.0 removed those overloads, the other two do not either.

Enumerations:

```csharp
public enum RmQREccLevel { M = 0, H = 1 }            // own domain like MicroQREccLevel; value = the ECC bit in the format information
public enum RmQRVersion  { R7x43 = 1, R7x59, R7x77, R7x99, R7x139, R9x43, …, R17x139 = 32 }   // height-major, value = version index + 1 = libzint version number
public enum RmQRFitStrategy { MinimizeArea = 0, MinimizeWidth = 1, MinimizeHeight = 2 }
public enum RmQRHeight { H7 = 7, H9 = 9, H11 = 11, H13 = 13, H15 = 15, H17 = 17 }
```

Generator (`public static class RmQRCodeGenerator`, `DefaultQuietZone = 2`):

```csharp
public readonly record struct RmQRCodeGeneratorOptions
{
    static RmQRCodeGeneratorOptions Default { get; }        // == default
    EciMode EciMode { get; init; }                          // Default; only Default / Iso8859_1 / Utf8 accepted
    RmQRVersion? Version { get; init; }                     // null = fit automatically
    RmQRFitStrategy FitStrategy { get; init; }              // MinimizeArea
    RmQRHeight? Height { get; init; }                       // null = every height
    int QuietZoneSize { get; init; }                        // 2 (ISO/IEC 23941)
    RmQRSegmentation Segmentation { get; init; }            // Single
}

RmQRCodeData Create(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options = default);
int Create(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, Span<byte> destination, in RmQRCodeGeneratorOptions options = default);   // byte per module, row-major, quiet zone included, returns bytes written
bool TryGetRequiredBufferSize(ReadOnlySpan<char> text, RmQREccLevel eccLevel, out RmQRCodeCalculatedSize size, in RmQRCodeGeneratorOptions options = default);
public readonly record struct RmQRCodeCalculatedSize { int BufferSize; int Width; int Height; RmQRVersion Version; }   // Width/Height include the quiet zone
```

`Version` and `Height` may be given together only when they agree. Otherwise the call throws `ArgumentException`. `FitStrategy` is ignored when `Version` is given.

`default(RmQRCodeGeneratorOptions)` must be the complete default configuration, because every call without options sends it. So a member whose documented default is not zero stores an offset from that default. Only `QuietZoneSize` does today: its default is 2, and 0 is a legitimate value that cannot also mean "unset". It stores the offset from 2, not a `value + 1` sentinel, so each configuration has a single stored form. Writing 2 explicitly must equal omitting it, or the generated equality would call two identical option sets different.

Unlike Standard QR and Micro QR, the options have no version range. Their versions are totally ordered by capacity, so "at least" and "at most" mean something. rMQR's 32 are not (R7x43, R9x43 and R7x59 have no min/max relation). `FitStrategy` and `Height` constrain the fit instead, because they suit a two-dimensional size space. Any bound added later would be a width in modules, not a version range.

### Reporting "does not fit" without an exception

`TryGetRequiredBufferSize` is the only way to ask a symbol's size. It returns `false` only when the content does not fit. Argument errors throw with the same exception type, message and precedence as the `Create` overloads.

There is no throwing twin. One was deleted before release (2026-08-29). A `Get` paired with a `Try` copies `Parse` / `TryParse`, the precedent for parsing, not for sizing a caller-supplied buffer. Where the BCL sizes or formats into a caller's buffer, the API is `Try`-first and usually has no throwing twin: `Utf8Formatter.TryFormat`, `Utf8Parser.TryParse`, `IUtf8SpanFormattable.TryFormat`, and `Base64.EncodeToUtf8` (which returns `OperationStatus`). Offering both would also steer a hurried caller to the shorter name, which behaves worse. Standard QR and Micro QR kept an `[Obsolete]` throwing overload through 1.2.0, because 1.1.1 had released one, and dropped it in 2.0.0. rMQR never had one.

The API uses a `Try`, not a dedicated exception type. rMQR holds 5 to 150 Byte-mode characters, so an overflow of user content is an ordinary outcome, not a defect, and a .NET exception costs one to two orders of magnitude more than the encode. A dedicated exception type would only narrow the `catch`, not remove the throw. The designs are alternatives, not complements. The decoder already treats failure as a first-class outcome (`TryDecode`). This is the encoder side of that rule.

Argument errors still throw. The BCL's configurable `Try` overloads draw the same line, reserving `false` for failing input, not malformed options: `int.TryParse(s, NumberStyles, ...)` throws `ArgumentException` for an undefined `NumberStyles` value or `AllowHexSpecifier` combined with other flags, `Dictionary.TryGetValue` for a null key, and `Uri.TryCreate` for an undefined `UriKind`. If an invalid ECC level returned `false`, the caller would report "content too long" for content that is not, and a caller moving from `Get` to `Try` would get a silent behaviour change. Declaring `EciMode.Iso8859_1` for non-Latin-1 content throws for the same reason: it is a broken promise about the text, not a capacity outcome.

The method promises only that no length-related exception follows: passing the returned `Version` back through the options removes the fit from the next `Create`, but a too-small destination buffer still throws.

The non-throwing cores are `RmQRVersionSelector.TrySelect` (single mode) and `RmQRSegmentPlanner.TrySelectVersion` (mixed mode). The throwing `Select` / `SelectVersion` wrap them and add the message. Sizing and encode share this one selection path, so the reported version cannot differ from the encoded one. `TryGetRequiredBufferSizeTest.RmQR_ReportedSize_MatchesTheEncodeItDescribes` asserts this over content × ECC × strategy × height × segmentation: the encode fills the reported buffer exactly and chooses the reported version, and `false` implies the encode throws.

Data model (`public sealed class RmQRCodeData`):

```csharp
RmQRCodeData(RmQRVersion version, int quietZoneSize);
RmQRCodeData(byte[] rawData, int quietZoneSize);
RmQRCodeData(ReadOnlySpan<byte> rawData, int quietZoneSize);
int Width { get; }   int Height { get; }   RmQRVersion Version { get; }     // quiet zone included
bool this[int row, int col] { get; }                                          // quiet zone reads false
int GetRawDataSize();  byte[] GetRawData();  int GetRawData(IBufferWriter<byte> writer);   // "QRX" + type 2 + width + height + packed core bits
```

Decoder (`public static class RmQRCodeDecoder`):

```csharp
bool TryDecode(RmQRCodeData data, out string text);
bool TryDecode(RmQRCodeData data, out string text, out RmQRCodeDecodeInfo info);
bool TryDecode(ReadOnlySpan<byte> modules, int width, int height, out string text, out RmQRCodeDecodeInfo info);                          // byte per module, any light border (uniform or not: the dark bounding box is the core)
bool TryDecode(ReadOnlySpan<byte> modules, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info);
bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, out string text, out RmQRCodeDecodeInfo info);
bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info);
int GetMaxDecodedLength(RmQRVersion version);
public readonly record struct RmQRCodeDecodeInfo { DecodeStatus Status; RmQRVersion Version; RmQREccLevel EccLevel; int ErrorsCorrected; }   // no MaskPattern: rMQR has one mask
```

The `SKBitmap` overloads are not in this list: `RmQRCodeDecoder` lives in the dependency-free core. Bitmap decoding is `RmQRCodeImageDecoder` in the rendering package, callable as `RmQRCodeDecoder.TryDecode(bitmap, …)` only through C# 14 extension members.

Rendering:

```csharp
public sealed class RmQRCodeImageBuilder : SymbolImageBuilderBase<RmQRCodeImageBuilder>
  RmQRCodeImageBuilder(string content);  RmQRCodeImageBuilder(RmQRCodeData data);          // default quiet zone 2
  RmQRCodeImageBuilder WithErrorCorrection(RmQREccLevel eccLevel);  WithVersion(RmQRVersion version);
  RmQRCodeImageBuilder WithFitStrategy(RmQRFitStrategy fitStrategy);  WithHeight(RmQRHeight height);  WithWidth(int width);   // rMQR-only, listed in the parity test's allowed differences (WithWidth: image width in pixels, height from the aspect ratio, background over the whole image)
  // static helpers exactly as MicroQRCodeImageBuilder (GetPngBytes / GetImageBytes / SavePng / GetSvgBytes / SaveSvg / GetSvgString / WriteSvg / WritePng / WriteImage,
  // string + RmQREccLevel eccLevel = RmQREccLevel.M and RmQRCodeData overloads); their `int size = 512` is the image WIDTH, height follows the symbol aspect ratio
SymbolRenderer.Render(SKCanvas canvas, SKRect area, RmQRCodeData data, SKColor codeColor, SKColor backgroundColor, ModuleShape? moduleShape = null, float moduleSizePercent = 1.0f, GradientOptions? gradientOptions = null, FinderPatternShape? finderPatternShape = null);
SKCanvas.Render(this SKCanvas canvas, RmQRCodeData data, int width, int height, SKColor? clearColor = null, SKColor? codeColor = null, SKColor? backgroundColor = null, ModuleShape? moduleShape = null, float moduleSizePercent = 1.0f, GradientOptions? gradientOptions = null, FinderPatternShape? finderPatternShape = null);
SKCanvas.Render(this SKCanvas canvas, RmQRCodeData data, SKRect area, …same tail…);
```

Every rendering entry draws the symbol, quiet zone included, at a uniform module scale, centered in the target area or canvas (letterbox):

- `WithModulePixelSize` yields exactly `Width × Height` modules × pixels.
- `WithSize(w, h)` letterboxes the symbol into `w × h`, padded with the clear colour when one is set and the background otherwise.
- `WithWidth(w)` makes the image `w` wide, with the height from the aspect ratio rounded to whole pixels, and the background fills the image. The static helpers' `size` (default 512) is this width.

The square symbologies follow the same rule, their builders since 2026-09-10 and their renderer entries since 2026-09-11. The fill they inherited from the 2019 renderer produced non-square modules, which readers find or miss depending on aspect ratio, payload, styling, orientation and reader ([qrcode-symbologies.md](qrcode-symbologies.md) has the measurements).

### Supported

| Area | Coverage |
|---|---|
| Symbology | rMQR |
| Versions | All 32 (R7x43 … R17x139) |
| ECC levels | M, H |
| Data modes | Numeric, Alphanumeric, Byte (ECI 3 for ISO-8859-1, ECI 26 for UTF-8, no ECI for ASCII), and Kanji on request (`AllowKanji`, no ECI header) |
| Segmentation | One segment in a single mode (default), or the minimal-bit mixed-mode split (opt-in `RmQRSegmentation.Optimal`) |
| Version selection | Exact version, or automatic fit by strategy, optionally within a fixed height |
| Quiet zone | Configurable non-negative size, default 2 (the ISO/IEC 23941 quiet zone) |
| Output | Bit-packed `RmQRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- Kanji beside an ECI header. Kanji mode is written only when the library chooses the charset ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)).
- FNC1 and Structured Append (rMQR does not define Structured Append).

### Symbol parameters (verified)

The version index is height-major (all widths of height 7, then of height 9, and so on) and is the 5-bit value stored in the format information. The alignment columns are the 0-based columns of the vertical timing patterns, each with a 3×3 alignment pattern at its top and bottom edge. Data codewords are split across blocks whose sizes differ by at most one, smaller blocks first. Every block has the same number of ECC codewords.

| Index | Version | Modules | Alignment columns | Total codewords | M: data / blocks / ECC per block | H: data / blocks / ECC per block | Count indicator bits N / A / B |
|---|---|---|---|---|---|---|---|
| 0 | R7x43 | 7 x 43 | 21 | 13 | 6 / 1 / 7 | 3 / 1 / 10 | 4 / 3 / 3 |
| 1 | R7x59 | 7 x 59 | 19, 39 | 21 | 12 / 1 / 9 | 7 / 1 / 14 | 5 / 5 / 4 |
| 2 | R7x77 | 7 x 77 | 25, 51 | 32 | 20 / 1 / 12 | 10 / 1 / 22 | 6 / 5 / 5 |
| 3 | R7x99 | 7 x 99 | 23, 49, 75 | 44 | 28 / 1 / 16 | 14 / 1 / 30 | 7 / 6 / 5 |
| 4 | R7x139 | 7 x 139 | 27, 55, 83, 111 | 68 | 44 / 1 / 24 | 24 / 2 / 22 | 7 / 6 / 6 |
| 5 | R9x43 | 9 x 43 | 21 | 21 | 12 / 1 / 9 | 7 / 1 / 14 | 5 / 5 / 4 |
| 6 | R9x59 | 9 x 59 | 19, 39 | 33 | 21 / 1 / 12 | 11 / 1 / 22 | 6 / 5 / 5 |
| 7 | R9x77 | 9 x 77 | 25, 51 | 49 | 31 / 1 / 18 | 17 / 2 / 16 | 7 / 6 / 5 |
| 8 | R9x99 | 9 x 99 | 23, 49, 75 | 66 | 42 / 1 / 24 | 22 / 2 / 22 | 7 / 6 / 6 |
| 9 | R9x139 | 9 x 139 | 27, 55, 83, 111 | 99 | 63 / 2 / 18 | 33 / 3 / 22 | 8 / 7 / 6 |
| 10 | R11x27 | 11 x 27 | - | 15 | 7 / 1 / 8 | 5 / 1 / 10 | 4 / 4 / 3 |
| 11 | R11x43 | 11 x 43 | 21 | 31 | 19 / 1 / 12 | 11 / 1 / 20 | 6 / 5 / 5 |
| 12 | R11x59 | 11 x 59 | 19, 39 | 47 | 31 / 1 / 16 | 15 / 2 / 16 | 7 / 6 / 5 |
| 13 | R11x77 | 11 x 77 | 25, 51 | 67 | 43 / 1 / 24 | 23 / 2 / 22 | 7 / 6 / 6 |
| 14 | R11x99 | 11 x 99 | 23, 49, 75 | 89 | 57 / 2 / 16 | 29 / 2 / 30 | 8 / 7 / 6 |
| 15 | R11x139 | 11 x 139 | 27, 55, 83, 111 | 132 | 84 / 2 / 24 | 42 / 3 / 30 | 8 / 7 / 7 |
| 16 | R13x27 | 13 x 27 | - | 21 | 12 / 1 / 9 | 7 / 1 / 14 | 5 / 5 / 4 |
| 17 | R13x43 | 13 x 43 | 21 | 41 | 27 / 1 / 14 | 13 / 1 / 28 | 6 / 6 / 5 |
| 18 | R13x59 | 13 x 59 | 19, 39 | 60 | 38 / 1 / 22 | 20 / 2 / 20 | 7 / 6 / 6 |
| 19 | R13x77 | 13 x 77 | 25, 51 | 85 | 53 / 2 / 16 | 29 / 2 / 28 | 7 / 7 / 6 |
| 20 | R13x99 | 13 x 99 | 23, 49, 75 | 113 | 73 / 2 / 20 | 35 / 3 / 26 | 8 / 7 / 7 |
| 21 | R13x139 | 13 x 139 | 27, 55, 83, 111 | 166 | 106 / 3 / 20 | 54 / 4 / 28 | 8 / 8 / 7 |
| 22 | R15x43 | 15 x 43 | 21 | 51 | 33 / 1 / 18 | 15 / 2 / 18 | 7 / 6 / 6 |
| 23 | R15x59 | 15 x 59 | 19, 39 | 74 | 48 / 1 / 26 | 26 / 2 / 24 | 7 / 7 / 6 |
| 24 | R15x77 | 15 x 77 | 25, 51 | 103 | 67 / 2 / 18 | 31 / 3 / 24 | 8 / 7 / 7 |
| 25 | R15x99 | 15 x 99 | 23, 49, 75 | 136 | 88 / 2 / 24 | 48 / 4 / 22 | 8 / 7 / 7 |
| 26 | R15x139 | 15 x 139 | 27, 55, 83, 111 | 199 | 127 / 3 / 24 | 69 / 5 / 26 | 9 / 8 / 7 |
| 27 | R17x43 | 17 x 43 | 21 | 61 | 39 / 1 / 22 | 21 / 2 / 20 | 7 / 6 / 6 |
| 28 | R17x59 | 17 x 59 | 19, 39 | 88 | 56 / 2 / 16 | 28 / 2 / 30 | 8 / 7 / 6 |
| 29 | R17x77 | 17 x 77 | 25, 51 | 122 | 78 / 2 / 22 | 38 / 3 / 28 | 8 / 7 / 7 |
| 30 | R17x99 | 17 x 99 | 23, 49, 75 | 160 | 100 / 3 / 20 | 56 / 4 / 26 | 8 / 8 / 7 |
| 31 | R17x139 | 17 x 139 | 27, 55, 83, 111 | 232 | 152 / 4 / 20 | 76 / 6 / 26 | 9 / 8 / 8 |

The table predates Kanji support and omits the Kanji count-indicator widths, which `RmQRConstants.GetKanjiCountIndicatorLength` holds: values 2-7, monotone, below the byte widths. They follow from the narrowest-field derivation below, and the qrtool Kanji fixtures have exercised them since the decoder shipped.

Since Kanji encoding phase 6.6 the fixtures cover every width: R11x43, R13x59, R15x59 and R17x139 carry widths 4, 5 and 7 between them, and R7x43-M, R7x59-M, R9x43-H and R9x139-M, each filled to its Kanji capacity, carry 2, 3, 3 and 6. `KanjiEncoderOracleTest` checks that this library's symbol for each fixture's text equals qrtool's module for module (rMQR has one mask) and that each filled count equals the last count this library holds.

Before that phase, widths 2, 3 and 6 rested on the derivation and a second check. A transcription of ISO/IEC 23941 Table 3 in the English Wikipedia article on rMQR agrees with all 32 Kanji widths (and all 96 others), and the Kanji column of the article's Table 7 transcription agrees with the capacity those widths give on 63 of 64 cells. The 64th, R11x77-M, is a corrupt row, not a disagreement: its Numeric, Alphanumeric and Byte cells are R11x59-H's. Both checks are in `RmQRBinaryEncoderKanjiTest`, with the Kanji writer tests.

With `AllowKanji`, the generator writes Kanji for text whose every character has an encoder cell ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)).

Data capacity in characters (Numeric / Alphanumeric / Byte), single segment, no ECI header:

| Version | M: Numeric / Alphanumeric / Byte | H: Numeric / Alphanumeric / Byte |
|---|---|---|
| R7x43 | 12 / 7 / 5 | 5 / 3 / 2 |
| R7x59 | 26 / 16 / 11 | 14 / 8 / 6 |
| R7x77 | 45 / 27 / 19 | 21 / 13 / 9 |
| R7x99 | 64 / 39 / 27 | 30 / 18 / 13 |
| R7x139 | 102 / 62 / 42 | 54 / 33 / 22 |
| R9x43 | 26 / 16 / 11 | 14 / 8 / 6 |
| R9x59 | 47 / 29 / 20 | 23 / 14 / 10 |
| R9x77 | 71 / 43 / 30 | 37 / 23 / 16 |
| R9x99 | 97 / 59 / 40 | 49 / 30 / 20 |
| R9x139 | 147 / 89 / 61 | 75 / 46 / 31 |
| R11x27 | 14 / 8 / 6 | 9 / 6 / 4 |
| R11x43 | 42 / 26 / 18 | 23 / 14 / 10 |
| R11x59 | 71 / 43 / 30 | 33 / 20 / 14 |
| R11x77 | 100 / 60 / 41 | 52 / 31 / 21 |
| R11x99 | 133 / 81 / 55 | 66 / 40 / 27 |
| R11x139 | 198 / 120 / 82 | 97 / 59 / 40 |
| R13x27 | 26 / 16 / 11 | 14 / 8 / 6 |
| R13x43 | 62 / 37 / 26 | 28 / 17 / 12 |
| R13x59 | 88 / 53 / 36 | 45 / 27 / 18 |
| R13x77 | 124 / 75 / 51 | 66 / 40 / 27 |
| R13x99 | 171 / 104 / 71 | 80 / 49 / 33 |
| R13x139 | 251 / 152 / 104 | 126 / 76 / 52 |
| R15x43 | 76 / 46 / 31 | 33 / 20 / 13 |
| R15x59 | 112 / 68 / 46 | 59 / 36 / 24 |
| R15x77 | 157 / 95 / 65 | 71 / 43 / 29 |
| R15x99 | 207 / 126 / 86 | 111 / 68 / 46 |
| R15x139 | 301 / 182 / 125 | 162 / 98 / 67 |
| R17x43 | 90 / 55 / 37 | 47 / 28 / 19 |
| R17x59 | 131 / 79 / 54 | 63 / 38 / 26 |
| R17x77 | 183 / 111 / 76 | 87 / 53 / 36 |
| R17x99 | 236 / 143 / 98 | 131 / 79 / 54 |
| R17x139 | 361 / 219 / 150 | 178 / 108 / 74 |

The pipeline also relies on these symbol facts, all verified (see the record below):

- A single data mask, `((row ⁄ 2) + (col ⁄ 3)) mod 2 = 0`.
- Format information: 6 data bits (the ECC bit, M = 0 / H = 1, above the 5-bit version index), BCH-extended to 18 bits, in two copies (finder side and sub-finder side), each with its own XOR mask.
- 3-bit mode indicators, the terminator `000`, and the pad codewords 0xEC / 0x11.
- Standard block interleaving.
- Two-column zigzag placement, starting at the column pair left of the right-edge timing column, upward first, right column first.
- A quiet zone of 2 modules.

---

## Pipeline

### 1. Validate the request

The generator rejects an unknown version, an unknown ECC level, a `height` constraint combined with a `requestedVersion` of a different height, and a negative quiet zone. Span sizing and span output also reject dimensions that overflow `int`, as `MicroQRCodeGenerator` does.

### 2. Analyze text

The shared `TextAnalyzer` analyzes the text (Numeric / Alphanumeric / Byte, single segment, Kanji on request). The default charset policy matches Standard QR: no ECI for ASCII, assignment 3 for ISO-8859-1 text, and UTF-8 with assignment 26 for other Unicode. An explicit `EciMode` can select ISO-8859-1 or UTF-8. Explicit ISO-8859-1 rejects input it cannot represent instead of narrowing it.

With `AllowKanji`, when the library chooses the charset, a text whose every character has an encoder cell becomes one Kanji segment with no ECI header, and under `Optimal` a Kanji-eligible text that contains ASCII is also priced as a Kanji plan ([Mixed-mode segmentation](#mixed-mode-segmentation)).

The analyzer chooses the charset for every path and the mode for the default single-segment path. `RmQRSegmentation.Optimal` chooses each run's mode instead but keeps the analyzer's charset, because the charset is a property of the content, not of the split.

### Mixed-mode segmentation

`RmQRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs with the lowest total bit cost for a candidate version and fits the version against that cost. `RmQRSegmentation.Single`, the default, keeps one run in one mode.

rMQR data capacities are small (5 Byte-mode characters at R7x43-M, 150 at R17x139-M), so the mode mix decides the symbol size far more often than in Standard QR. The common case is a URL followed by a numeric identifier: `https://example.com/p/1234567890123456` needs 313 bits as one Byte run (R11x77, 847 modules) but 249 as Byte + Numeric (R15x43, 645 modules).

It is opt-in because changing the default would change the emitted bit stream, and so the rendered symbol, for existing callers. A single-mode fit, when one exists, is the search's ceiling: only versions the strategy ranks strictly better are tried. So the mixed plan is emitted only when it shrinks the symbol. Otherwise the single-mode stream is emitted byte for byte, as the end-to-end tests assert for every content, ECC level and strategy.

Shrinking means fewer core modules, which `RmQRFitStrategy` ranks by. The quiet zone adds four modules to each dimension, so minimising `height × width` does not minimise `(height + 4) × (width + 4)`, and a flatter, wider symbol can have fewer core modules but a larger rendered grid. In a measured case, 24 characters at ECC H go from R15x59 (885 core, 63×19 rendered) to R11x77 (847 core, 81×15 rendered), a wider image, and a `TryGetRequiredBufferSize` computed under `Single` is now too small.

The fit strategy causes this, not segmentation, but segmentation exposes callers to it, so it is documented on `RmQRSegmentation.Optimal`, in the README and in the migration notes, and `Optimal_FewerCoreModulesCanStillRenderLarger` checks it.

When no single mode fits, there is no ceiling and the scan runs to the end. Only here does `Optimal` accept input that `Single` rejects instead of only shrinking the symbol, and here it is worth the most. 100 lowercase letters followed by 100 digits are 200 Byte-mode characters, 50 more than the 150 R17x139-M holds. With the digits split off, they need 1157 bits of the 1216 available. Uppercase letters would not reach this path: they are Alphanumeric, so the uppercase form is 200 Alphanumeric characters, which fit a single mode.

`RmQRVersionSelector` reports the capacity error only when a mixed plan also fails, so a too-large payload reports the same error as today.

All-Numeric content skips planning: digits are the cheapest characters in the cheapest mode, splitting a Numeric run never lowers its payload, and every extra run adds a header, so one run is provably optimal. Without this shortcut a 361-digit payload cost 11.9x a `Single` encode. With it, 1.05x.

The scan is best-first (smallest version first), so for mixed content most early candidates cannot fit. Three filters, cheapest first, decide each candidate. A costlier filter runs only when the cheaper ones could not decide:

- The trivial bound, one table-free O(n) pass, prices each character at the cheapest rate any mode could give it. A partial group only costs more per character, so the bound never exceeds a real plan. When no better-ranked version holds even this many bits, no split can change the symbol and nothing further runs.
- The floor, a cost run at the narrowest count indicator widths any version uses, runs only after a candidate clears the trivial bound. Widening a count indicator only raises its run's price, and the minimum over all plans of a cost larger at every point is itself larger, so the floor is a lower bound everywhere.
- The ceiling re-prices the plan that the same cost run yields: at a version it costs the floor plus one count indicator delta per run, which is arithmetic, not a second cost run. As a real plan, its price is an upper bound on the optimum, so a version that holds it is known to fit.

Only versions between floor and ceiling are fully priced. Each filter was measured against the Single encode of the same content in the same benchmark run:

- The floor alone took the 150-byte worst case from 13.2x to 3.0x.
- The ceiling cut a further 26-42%, most where the scan was slowest.
- The trivial bound then made the never-wins cases roughly free (150 lowercase 2.3x to 1.1x, 120 alphanumeric 3.4x to 1.1x) without changing the winning cases.

The trivial bound is crude and has one blind spot: it prices each character at its best rate wherever it sits, so finely alternating content (`a7a7…`) looks far cheaper than it is, clears the bound, and loses after planning. Seeing that switching modes at every character never pays requires modelling the switch cost, which is the dynamic program itself.

A ceiling from a second cost run at the widest widths was tried first and reverted. Its band above the floor widens by about 5 bits per run, so for many-run content, which needs the most help, the band was wide rather than empty and the extra run cost more than it saved: alternating 10-character groups regressed from 9.0 us to 11.2 us. The floor-plan ceiling needs no extra run and is tighter, so it is faster.

Planning allocates nothing. Its cost depends on how much the split helps, not how mixed the content looks: the lower a split brings the bit cost, the more candidate versions clear the floor and get priced.

The `RmQRSegmentationEncode` benchmark shows this: it varies the content shape, and each row has a Single partner in the same run, so its Ratio column is the multiplier. Sorted by that column, the groups separate exactly:

- Content no split can help: 1.0-1.1x for all digits (short-circuited), and 150 lowercase and 120 alphanumeric (ruled out by the trivial bound).
- The blind spot, alternating at every character: 2.2x.
- Content the split wins on: 60 lowercase + 60 digits at 4.8x, 150 half and half at 5.5x, alternating in tens at 6.4x.

Where planning runs, its cost is linear in length for a fixed shape: 20 / 60 / 120 / 150 characters of half letters, half digits take 0.9 / 2.4 / 4.5 / 5.7 us.

Planning therefore cannot be free: only planning shows whether a split helps, and all-Numeric is the one shape where "no gain" can be proven up front. A payload of known shape, such as a URL followed by a numeric identifier, wins predictably. Arbitrary user input trades a few microseconds per symbol for the chance of a smaller one.

The optimum is exact because the dynamic program keeps the group remainder in its state: Numeric packs 3 digits into 10 bits and Alphanumeric 2 characters into 11, so a per-character average would misprice the tail of every run.

`ModeSegmenter`, shared with the Standard QR and Micro QR planners, holds the state layout and transitions and takes what differs as parameters: header widths (3-bit, 4-bit or version-dependent mode indicators, and each symbology's count indicator tables) and, for Micro QR, which modes each version allows. One implementation keeps the cost models from drifting apart, which mattered while the Standard QR and rMQR planners each had their own copy of the UTF-8 surrogate cost rules (the Micro QR planner came after the extraction and never had one). The version scan, its bounds and the selector integration stay in `RmQRSegmentPlanner`, because they depend on rMQR's version space, not the cost model.

Content longer than the largest character count any rMQR symbol holds in any mode (361, Numeric at R17x139-M) is rejected before any cost run, so pathological input never pays for planning. Since a mixed plan can encode content no single mode holds, this limit is a rejection rule, not only a cap on work. Its margin is three bits, with the derivation beside the constant in `RmQRSegmentPlanner`. A plan is also capped at a run count above what the largest capacity could hold. The reconstructed plan is re-costed from the byte counts the encoder will emit and rejected on disagreement, because the bit-stream writers store without a bounds check on each flush.

A plan has one ECI prefix, ahead of the first run, because an rMQR decoder applies the declared charset to all following runs. Its 11 bits count in the cost the version scan compares.

The shared byte-segment decoder consumes a leading EF BB BF of every segment, even behind an explicit UTF-8 declaration, so the shared segmentation program never opens a Byte run at a mid-content U+FEFF. The run opens one character earlier and keeps the mark inside (rationale: specs/standardqr-encoder.md, "Plans the byte-segment decoder would misread are never built").

A text whose every character has a Kanji cell is one Kanji run under both segmentations, and the planner returns before any cost run: no such character fits another mode more cheaply, and Byte would need an ECI header ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)).

Under `Optimal`, a Kanji-eligible text that contains ASCII has two plans: the Kanji plan (Kanji runs beside ASCII runs, no ECI header, from the shared program's eighth state) and its earlier UTF-8 plan ([Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation) gives the rationale). Candidates are visited in the strategy's order. At each one ranked ahead of the single-mode fit, the Kanji plan is taken where it fits and the UTF-8 plan otherwise.

Each plan has its own three filters. The Kanji plan's are re-derived for its fourth mode:

- The screen prices a character with a cell at 13 bits and adds a header for every stretch of such characters (3 + 2 bits) and every stretch of ASCII (3 + 3), because no run of the plan crosses between the two.
- The floor runs the Kanji program at each of the four modes' narrowest width, for Kanji 2 bits (R7x43 and R11x27). At 3 bits, the floor of 「あい123」 would be 49 against R7x43-M's 48, and its Kanji plan, which fills R7x43 exactly, would be skipped.
- The upper bound re-prices the floor plan's Kanji runs along with its other runs.
- The memo key includes Kanji's width, because R13x77 and R15x59 share their other three widths, as do R13x139 and R17x99.

When the single stream does not fit a requested version, the Kanji plan is priced at that version, because the scan must say which plan to build. Where it does not fit, plan building handles the UTF-8 plan as for any other text.

The plan is built by its own run at the chosen version, whereas Standard QR and Micro QR walk back the run that accepted the version ([Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation)). Here the version is usually accepted on the floor plan's upper bound, which runs nothing at that version: 「日本7777」×10, for example, is skipped by the screen up to R13x77, priced and rejected at R15x77 and R13x99, and taken at R17x77 on the upper bound. A table kept from a priced candidate would seldom belong to the version taken, and the memo gives a candidate a cost without a table.

That run is most of this Kanji plan's extra cost over the same text with UTF-8 requested (R17x77 against R13x139): 6-25 % longer, depending on the measurement. As on Standard QR, the user accepted this cost ([Standard QR Encoder](standardqr-encoder.md#mixed-mode-segmentation), "Speed").

### 3. Fit the version

Required bits = optional 11-bit ECI prefix (`111` + 8-bit assignment) + 3 (data mode) + count indicator (per version, table above) + payload bits. The terminator may shrink to the remaining capacity, down to zero bits.

Automatic fit scans a table listing the versions in best-first order per strategy, with their capacity per mode × ECC × ECI presence and the height as a bitmask. A test checks that it selects the same version as the definitional "best fitting version" scan for every input.

Under `RmQRSegmentation.Optimal`, the required bits are the planned mixed-mode cost for the candidate version. The candidate set, strategy ordering, height constraint and error text are unchanged. The one behaviour difference is that input the single mode overflows at every version can succeed instead of throwing (see [Mixed-mode segmentation](#mixed-mode-segmentation)).

- With `requestedVersion`, use it or fail with an actionable capacity error: the actual length, the applicable maximum in mode units, and the remedies (shorten the text, lower the ECC, choose a larger version, or use Standard QR). Under `Optimal`, a requested version the single mode overflows is still accepted when the mixed-mode plan fits it.
- Otherwise the candidates are all 32 versions, or those of the constrained `height`. Keep those whose data-codeword capacity holds the required bits and choose by `fitStrategy`:
  - `MinimizeArea`: fewest modules (height × width). A tie goes to the smaller height (the wider symbol).
  - `MinimizeWidth`: smallest width. A tie goes to the smaller height.
  - `MinimizeHeight`: smallest height. A tie goes to the smaller width.
- If no candidate fits, a capacity error states the maximum of the largest-capacity candidate in the set.

### 4. Build the data codewords

The data codewords are written in this order:

- An optional ECI mode `111` plus an 8-bit assignment.
- For each run, a 3-bit data-mode indicator, the count indicator and the payload bits. There is one run by default. Under `Optimal` the planned runs follow in order.
- The terminator `000`, shortened at capacity.
- Zero bits up to a byte boundary.
- Alternating 0xEC / 0x11 pads up to the data-codeword count.

The stream is written straight into the caller's buffer with no intermediate copy. On x64 each mode has vectorized value kernels (see Decisions) that produce the identical stream. UTF-8 has a separate cold writer, so adding ECI does not make the hot writer's locals address-exposed.

### 5. Reed-Solomon per block, 6. interleave

The data codewords are split into blocks as the table gives (smaller blocks first, sizes differing by at most one), and the shared `EccBinaryEncoder` computes each block's ECC. Interleaving follows Standard QR: all data codewords column-wise across the blocks, then all ECC codewords. The remainder bits (free modules − 8 × total codewords, 0..7 per version) are light.

### 7. Place function patterns and data

The function modules are the finder (7×7 with separators), the sub-finder (5×5), the four edge timing patterns, the two corner patterns, the vertical timing columns with 3×3 alignment patterns at both ends, and both format regions. Data fills the rest in zigzag order. The coordinates are in code comments from Phase 5.5. The fast placer reproduces the reference module for module from cached per-version tables (see Decisions).

### 8. Fixed mask, 9. format information

The single mask is applied to the data modules during placement. Both format copies come from a static 64-entry table indexed by (version, ECC).

---

## Rendering

`RmQRCodeImageBuilder` derives from `SymbolImageBuilderBase<TSelf>` and adds `WithErrorCorrection(RmQREccLevel)`, `WithEciMode(EciMode)`, `WithVersion(RmQRVersion)`, `WithFitStrategy(RmQRFitStrategy)` and `WithHeight(RmQRHeight)`, with a default quiet zone of 2. It has no icon overlay, because there is no ECC headroom for one. The finder shape option applies to rMQR's single finder.

The canvas layout is rectangular. With a module pixel size, the content is `width × height` modules at that size. With only an explicit canvas size, the symbol is fitted at a uniform module scale and centered on whole pixels (letterbox), never stretched non-uniformly. Standard and Micro QR have shared this layout since 2026-09-10.

It shipped this way in Phase 5.7, with two additions:

- `WithWidth(int)`, public since the 2026-08-16 review, makes the image that wide, the height following the symbol's aspect ratio rounded to whole pixels. The background fills the image, with the symbol drawn inside at a uniform module scale and no clear-colour padding, so an opaque background gives an opaque image. The review found that letterboxing this aspect-derived canvas again left 1-3 transparent columns on 12 of the 32 versions. The static helpers use it with their `size`, default 512 when no size option is given.
- The low-level `SymbolRenderer.Render(canvas, area, RmQRCodeData, …)` / `SKCanvas.Render` overloads letterbox into the given area, with the background filling the area.

---

## Why

- Separate `RmQR*` entry points, not `Create` overloads: version, ECC and fit semantics differ between symbologies ([QR Symbology Architecture](qrcode-symbologies.md)).
- The two-dimensional fit is a strategy plus an optional height constraint. rMQR exists to fit narrow print lanes, and "fixed height, auto width" is the dominant request in practice (libzint's `R<h>xauto`). Minimizing area, width or height covers the rest without a free-form size search, which would mostly select sizes that do not exist.
- Explicit canvas sizes letterbox instead of stretching: a rectangular symbol drawn into an arbitrary rectangle at a non-uniform scale is not the same symbol, so the module aspect ratio must be kept.
- The mask is fixed, so the placer is a static permutation per version, and no mask-scoring machinery is designed in.
- Emitting UTF-8 without ECI was superseded on 2026-08-18, because it made decoding depend on reader heuristics. Unlike Micro QR, rMQR supports ECI, so the encoder emits ISO-8859-1 assignment 3 or UTF-8 assignment 26, following Standard QR's policy. ECI + Byte mode is the interoperable path for Unicode. Kanji mode was not encoded until 2.0.0, which writes it on request (see the Kanji row under Decisions).

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Naming | `RmQR*` family, `RmQRVersion` with 32 named members | Never (mirrors shipped `MicroQR*`) |
| Version fit API | `RmQRFitStrategy` + `RmQRHeight?` | User demand for width constraints (would add `RmQRWidth?` symmetric to height) |
| Default fit strategy | `MinimizeArea` (fewest modules), confirmed in Phase 5.6. With no version option, both reference encoders, libzint and qrtool, choose the same versions automatically: 12 digits at M → R11x27, 15 → R13x27, 100 → R11x77 (measured by `probe-rmqr`). So the default keeps interoperability parity and the printable-area argument. The surprising case, 12 digits at M giving R11x27 (297) rather than the flatter R7x43 (301), is documented in the generator XML docs and checked by `RmQRCodeGeneratorUnitTest`. Users wanting the flattest symbol use `MinimizeHeight` or a fixed `RmQRHeight` (the README example lands with the rendering surface in 5.7) | User feedback after release |
| Explicit-canvas layout | Uniform scale, centered (letterbox) | - |
| ECI on encode | Implemented 2026-08-18: no ECI for ASCII, assignment 3 for ISO-8859-1, assignment 26 for UTF-8. Only R7x43-H cannot hold an ECI header plus a one-byte payload | Additional charset demand (cross-symbology decision) |
| Kanji | Written on request (`AllowKanji`) when the charset is the library's choice: a text whose every character has an encoder cell as one Kanji run with no ECI header ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)), and under `Optimal` a Kanji-eligible text as Kanji runs beside runs of its ASCII where that plan is smaller ([Mixed-mode segmentation](#mixed-mode-segmentation)). Kanji is read with JIS X 0208. The Kanji capacity column is now an encoding capacity | Kanji beside an ECI header, if readers are measured to apply JIS X 0208 there ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)) |
| Mixed-mode segmentation | Opt-in via `RmQRSegmentation.Optimal`. `Single` stays the default, because changing it would change the emitted bit stream, and so the rendered symbol, for every existing caller. Planning is also a search that cannot be free, since only planning shows whether a split helps. The bounds make it roughly free where it cannot help, which weakens but does not remove this argument | A major version allows changing the default. `Single` is then only ever better by accident. The breakage would be callers relying on the "too long" exception and callers that depend on today's rendered dimensions (see the quiet-zone note above) |
| Segmentation surface | Two values. A middle value, `WhenNeeded` ("plan only when the single mode does not fit"), was evaluated and rejected: for a requested version, `Optimal` already costs nothing when the single mode fits, so the extra value only added the rescue at the top end. The bounds later made the ordinary case roughly free too | A concrete caller needs the rescue without ever wanting a smaller symbol |
| Interleaver | Moved `BinaryInterleaver` up to `Internals.BinaryEncoders` (Phase 5.4). It used only the `ECCInfo` block structure, never the version. The remainder-bit count became a parameter | - |
| Placer performance | A reference per-module placer came first (Phase 5.5), then the benchmark-driven fast path (follow-up, 2026-08-16). The reference painters build per-version tables once: a painted template per version × ECC, the zigzag order as core indices, the mask per position, and the column-pair segmentation. The fast path expands bits with vectors, fused with the mask, and writes with 16-bit pair stores plus an index scatter. The reference stays the source of truth (for the tables and the decoder predicate), and the parity test checks both. ARM64's second store tier (2026-08-20, `RmQRModulePlacer.Arm64.cs`) transposes eight consecutive columns in registers, so one symbol row is one 8-byte store instead of one 16-bit store per two modules, and segments the leftovers by row run rather than by whole column pair: the pair test had disqualified 56 % of R11x27's modules, but 91.8 % of those sit in stretches where both columns are ordinary data, leaving only 4-12 % isolated. Encode E2E improved by 15-43 % after accounting for a -4.5 % drift in the control. In the same round the portable expand became branch-free SWAR, which netstandard2.0/2.1 and non-SIMD targets run for the whole message. WebAssembly runs the masked expand 16 modules per step on its own swizzle (2026-09-30): 0.93 of the scalar expand at R17x139 AOT-compiled, 0.96 interpreted | Placement stops being about half of the encode pipeline, or a profile points at the template copy (33 ns of 287 ns at R17x139) |
| Bit-stream performance | The reference shape came first (Phase 5.3), then the benchmark-driven fast path (follow-up, 2026-08-16): a raw-local writer, SWAR / SSE numeric and alphanumeric value kernels, and SSE2 byte narrowing, each gated on CPU capability with a scalar fallback. Kernel-level parity tests check vector against scalar, and the naive-reference parity checks the stream. Latin-1 gained a portable `Vector128.Narrow` tier (2026-08-20) for targets with 128-bit vectors and no SSE2 (ARM64 NEON, WASM), handling 16 characters per iteration: 9.2x on the writer and -11 % on byte-mode encode E2E. The bottleneck was the rate of writer-state updates, not character decoding, and every gain in that round came from making one append cover more characters | The numeric and alphanumeric writers were measured and declined, so their missing ARM tiers are a decision, not an oversight. The post-placement shares are byte 10.4 %, Latin-1 ECI 8.5 %, alphanumeric 5.4 %, numeric 0.3 %. Alphanumeric batching won 19-28 % in isolation but measured worse end to end (331.6/304.7 → 339.9/348.2 ns). Production keeps all three writers in one `switch`, so enlarging one arm changes the codegen of the whole method. Batching comes free if encode is ever restructured so each mode compiles independently. Numeric is -9 % at 361 digits but +6 % at 12, the only numeric payload in the E2E set. On WebAssembly they were declined the same way (2026-10-01): the writers are 2.5-6.8 % of an encode, and a 16-character alphanumeric step ran at 0.62-0.86 of the table loop alone, so it could save 2.5 % at most. Inlined, it slowed the numeric encode by 6 % AOT-compiled. As its own encode method, which removes the switch effect, it moved encodes by 0.97-1.02, inside the spread between runs. A 24-digit numeric step ran 1.26x the SWAR loop when interpreted. ARM64 was measured again (2026-10-01, Apple M2, the JIT and NativeAOT): there the writers are 3.8 to 11 % of an encode (numeric at 361 digits 10.6 to 11.2 %, alphanumeric at 120 characters 8.6 to 9.5 %), above the bar, but the end-to-end loss above still keeps them declined. A numeric step gated on length was not tried |

## Verification record

The verification ran on 2026-08-15, before any implementation, with the pinned qrtool 0.13.2 binary (`--variant rmqr`, `--type ascii` for module-exact output). qrtool is the second encoder lineage, per the [fixture record](qrcode-test-fixtures.md). Each item is now a permanent test (Phase 5.1b): the structural rows in `RmQRConstantsUnitTest`, and the oracle rows in `RmQRConstantsOracleTest` over the committed corpus (both lineages and all 32 versions × M/H, plus 96 single-character libzint symbols for the count widths).

| Fact | How verified | Result |
|---|---|---|
| Dimensions and version index order | ASCII output size for all 32 `-v H W` combinations | 32/32 |
| Data capacities (N/A/B × M/H) | Binary search for the longest accepted payload per version × ECC × forced mode | 192/192 match the table above and the published Denso capacities |
| Data codewords per version × ECC | Reproduce all 192 capacities from data codewords + count widths | 192/192 |
| Total codewords | The free-module count from an independent function-pattern painter must equal 8 × total + remainder (0..7) | 32/32 after correcting R17x59 to 88 from the initially recalled 90, which would also require 31 ECC per block at H, above the 30 maximum |
| Count indicator widths (N/A/B) | Read the first data codewords of one-character payloads from oracle matrices (inverse zigzag + unmask + deinterleave). The width is the position of the count's leading 1 | 96/96 (three numeric widths in the initial recall were off by one and corrected) |
| Count indicator widths (Kanji) | No oracle emitted Kanji when this column was written, so it was derived: Table 3 takes the narrowest count field that can still express the largest count the version's M-level data capacity allows, a rule validated by reproducing all 96 measured N/A/B widths above and then applied to Kanji. Since Kanji encoding phase 6.6, every width (2-7) is read from a qrtool Kanji symbol, and this library's symbol of the same text equals it module for module | 32/32 after correcting R17x99 (6, not the transcribed 7). Widths 2-7 confirmed against qrtool on 2026-10-01 |
| Format information | Both 18-bit copies of all 64 version × ECC symbols equal BCH(18,6) of (ECC bit, version index) XOR the copy's mask | 128/128 |
| Mask, zigzag start and direction, interleaving | The R7x43-M symbol for "1" yields the predicted codewords `22 20 EC 11`, and multi-block versions deinterleave to the predicted streams | Confirmed |
| Alignment column positions, sub-finder and corner patterns | Visual inspection of R7x43 / R9x59 / R11x27 plus the free-module count agreement above | Consistent |

This record does not verify the ISO/IEC 23941 misdecode-protection question, whether the ECC counts reserve codewords beyond the correction capacity. The decoder resolves it indirectly: the block structure verified above leaves at most one unused ECC codeword per block, and zxing-cpp corrects rMQR at full Reed-Solomon strength. The Table 8 capacity column itself is still unread (see the Correction cap decision in [rMQR Decoder](rmqr-decoder.md)).

## Lessons Learned

Lessons from the verification, before implementation:

- Published capacity tables cannot determine count-indicator widths, because the slack from byte alignment lets several widths reproduce one capacity. Reading the width from an oracle's bit stream can, and validates the mask, zigzag start and interleaving order at once.
- A recalled table can be internally consistent and still wrong: the R17x59 total-codeword error passed the "ECC divisible by blocks" check, and only the geometric free-module count caught it. Structural invariants that connect independent tables (geometry ↔ codewords ↔ ECC bounds) guard a transcription. Per-table plausibility checks do not.
- On multi-block versions the leading data bytes in placement order are interleaved, so a "read the first bits" check must deinterleave first or it silently reads block-2 codewords.

Implementation lessons, consolidated from the retired phase-by-phase progress log:

- The ARM64 placement store tier (2026-08-20) measured 0.29x at R17x139, 0.34x at R13x99, 0.50x at R11x59, 0.56x at R7x43 and 0.68x at R11x27 against the shipped portable path on Apple M2. It wins at every size, so it needs no size switch. Three designs were measured and rejected.

  Generalized blocks ("four adjacent pairs × their common data row range") raised in-block coverage to 89 % and lost at every size. The ZIP network costs a fixed ~40 instructions per block whatever its height, so short blocks pay a tall block's price and take modules from the cheaper run path. A minimum block height of 8 rows tied at the largest symbol and lost elsewhere.

  Branch-free split lists (per-version clean-up / clean-down / irregular lists, removing both per-pair branches) lost at R17x139: splitting the pairs out of walk order destroys the bit array's read locality, which at 1,858 bytes costs more than the branches saved, so branch count was the wrong objective.

  A wider expand step (4 message bytes per iteration) lost when built on `CreateScalarUnsafe`, which pays the GPR→SIMD FMOV on every iteration, whereas LD1R replicates straight from memory. Built on two independent LD1R broadcasts, it tied, because the expand is not load-bound.

- In the bit-stream fast path (follow-up, 2026-08-16), a memory-backed writer's cost is the range check on each flush, not the struct. A fixed-lane horizontal instruction (`pmaddwd`) cannot express a 3-digit group that straddles its lane pairs, so the group must own the load. At 10-50 ns per encode, code-layout noise (±30 % on identical code) is the measurement floor, and only same-run, same-mode deltas above it count.

- Mixed-mode segmentation (2026-08-22) gave four lessons, three of them found only by trying:

  - A bound computed by a throwing function is silently a rejection rule. Using the single-mode fit as a ceiling meant calling the throwing selector first, which rejected content that overflows every version in one mode but fits once split, so the feature failed where it is worth the most. When an optimisation bounds itself by an existing result, ask what happens when that result does not exist.
  - Derive a second bound from work already done, not a second pass. A ceiling from an extra cost run at the widest count indicator widths is sound but slower: its band widens by about 5 bits per run (the widest minus the narrowest count indicator, 5 in every mode), so for the many-run content that needs the most help the band is wide rather than empty and the extra run costs more than it saves. Re-pricing the floor's own plan gives a tighter ceiling with no additional run, which is faster.
  - Asking whether an optimisation can help at all is usually far cheaper than running it. The per-character trivial bound costs one O(n) pass and brought the never-wins population from roughly 2-3.5x to 1.1x. Being cheap gives it one blind spot: it prices each character at its best rate regardless of position, so finely alternating content clears it and still loses after planning. Seeing that requires the switch cost, which is the dynamic program itself.
  - Test the decision, not the timing. Every bound here fails by choosing a wrong version, not by running slowly, so the guard is a parity test against a reference scan with no pruning, no memo and no ceiling, not a benchmark. Benchmarks caught none of the three real defects in this work. The parity and soundness tests caught all of them.

## Validation

Every phase met its exit criteria, with these checks:

- 5.1: structural table tests and oracle format/dimension tests.
- 5.3: naive-reference parity for the bit stream.
- 5.4: an interleave reference.
- 5.5: an extraction test over all 64 combinations.
- 5.6: the `spot-check-rmqr` zxing-cpp gate over every version × ECC × mode.
- 5.7: module-to-pixel rendering parity.

Standard and Micro QR benchmarks stayed flat at every step. The 2026-08-18 ECI follow-up adds exact assignment-3/26 streams, ECI capacity-boundary and exhaustive selector parity tests, public class/span/sizing round trips, unsupported-charset validation, and a 318-symbol zxing-cpp gate on text, bytes, version and ECC, with 63 ECI-3 and 63 ECI-26 symbols.
- A table column no oracle in this repo could reach can still be checked by derivation. The Kanji count widths sat in the tables for months marked "spec-transcribed, unverified", because no oracle in this repo was known to emit Kanji at the time (qrtool can, which was found later). Table 3's own rule (the narrowest count field that still holds the largest count the version's data capacity allows) reproduces all 96 measured Numeric/Alphanumeric/Byte widths exactly, which validates it on evidence, and was then applied to the column that had none. It found R17x99 transcribed as 7 where the rule gives 6, an error latent since Phase 5.1b that no test could see until the decoder shipped, because nothing read the column. The bound tests that did cover the column (`b >= k >= 2`) passed on the wrong value: a loose invariant on an unverified column is not coverage.
