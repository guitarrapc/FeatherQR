# rMQR Encoder

Design record for the rMQR Code (ISO/IEC 23941) encode feature (`RmQRCodeGenerator`): what it does, the symbol parameters it is built on, why the pipeline is structured this way, and the decisions made up front so implementation phases share one understanding. Normative details and implementation locations are indexed in the [spec-to-code map](rmqr-spec-map.md); the implementation order was the rMQR implementation plan, now retired into this record and the [spec-to-code map](rmqr-spec-map.md). The decoder design record is [rMQR Decoder](rmqr-decoder.md).

Status: **shipped (Phase 5, 2026-08-15; adversarial review 2026-08-16)**. Written spec-first on 2026-08-15 before any `src/` code existed; the tables live in `Internals/RmQR/RmQRConstants` (Phase 5.1b), the data model in `RmQRCodeData` (5.2), the bit stream in `RmQRBinaryEncoder` and the fit logic in `RmQRVersionSelector` (5.3), RS + interleave in `RmQRCodewordEncoder` (5.4), placement in `RmQRModulePlacer` (5.5) and the public `RmQRCodeGenerator` (5.6, encoder MVT met: 256/256 symbols read by zxing-cpp) and rendering in `RmQRCodeImageBuilder` / `SymbolRenderer` (5.7); Phase 5 is complete and every parameter below is pinned by `RmQRConstantsUnitTest` (structural invariants) and `RmQRConstantsOracleTest` (the committed two-lineage corpus), see the [Verification record](#verification-record). Measured performance and lessons learned are consolidated below.

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

Names and overload sets mirror the shipped `MicroQR*` family member for member (reviewed against `MicroQRCodeGenerator`, `MicroQRCodeDecoder`, `MicroQRCodeImageBuilder`, `MicroQRCodeData`, the renderer overloads and `QrImageBuilderApiParityTest`); the only additions are the rectangular geometry and the fit options. Names below are the contract the implementation phases code against; a deviation is a spec change first.

**Options are carried by `RmQRCodeGeneratorOptions`, not by a parameter list.** The original shape put every option in the signature, which failed twice before it shipped: `EciMode` could not be positioned without disturbing the existing order, so it escaped into method names (`CreateRmQRCodeWithEci` and two siblings, doubling the method count), and `RmQRSegmentation` had to be appended to four signatures because trailing is the only source-compatible position. The surface went from 10 methods with up to 9 parameters to 5 with up to 4. Because no rMQR API had ever been released, the old shape was deleted outright rather than obsoleted.

`options` carried a `= default` here from the start, and could not on Standard QR or Micro QR until 2.0.0: while those two still had their released parameter list overloads, a defaulted options parameter would have made `Create(text, ecc)` ambiguous between the two sets. rMQR never had that collision, and since the 2.0.0 removal neither do the other two.

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

`Version` and `Height` together are accepted only when they agree (else `ArgumentException`); `FitStrategy` is ignored when `Version` is given.

**`default(RmQRCodeGeneratorOptions)` has to be the complete default configuration**, because it is what every unadorned call sends. Any member whose documented default is not the zero value therefore encodes an offset; `QuietZoneSize` is the only one today (2, with 0 a legitimate choice that cannot double as "unset"). It stores the offset from 2 rather than a `value + 1` sentinel so the canonical form stays unique: writing 2 explicitly must produce the same value as not writing it, or the generated equality reports two identical option sets as different.

**There is no version *range* here, unlike Standard QR and Micro QR.** Those two are totally ordered by capacity, so "at least" and "at most" mean something. rMQR's 32 versions are not: R7x43, R9x43 and R7x59 have no min/max relation. Fit is constrained by `FitStrategy` and `Height` instead, which is the vocabulary that fits a two-dimensional size space. If a bound is ever wanted here it is a width in modules, not a version range.

### Reporting "does not fit" without an exception

**What.** `TryGetRequiredBufferSize` is the *only* way to ask how big a symbol will be. It returns `false` when the content does not fit, and `false` means that and nothing else: argument errors throw, with the same type, message and precedence the `Create` overloads use.

**Why there is no throwing twin.** There was one, and it was deleted before release (2026-08-29). Pairing `Get` with `Try` copies the `Parse` / `TryParse` shape, which is the precedent for *parsing*, not for sizing a caller-supplied buffer. Where the BCL sizes or formats into a caller buffer it is `Try`-first and usually has no throwing twin at all: `Utf8Formatter.TryFormat`, `Utf8Parser.TryParse`, `IUtf8SpanFormattable.TryFormat`, `Base64.EncodeToUtf8` (which returns `OperationStatus`). Offering both would also make the shorter, worse-behaved name the one a hurried caller reaches for. Standard QR and Micro QR carried an `[Obsolete]` throwing overload through 1.2.0 because 1.1.1 released one, and lost it in 2.0.0; rMQR never had one.

**Why a `Try` and not a dedicated exception type.** rMQR holds 5 to 150 Byte-mode characters, so for user-supplied content overflow is an ordinary branch rather than a defect, and .NET exceptions cost one to two orders of magnitude more than the encode itself. A dedicated exception type would only narrow the `catch`; it would not remove the throw, and the two are alternatives rather than complements. The decoder already treats its failure path as a first-class outcome (`TryDecode`), so this is the encoder side of the same rule.

**Why argument errors still throw.** The split follows what the BCL does with its own configurable `Try` overloads: `int.TryParse(s, NumberStyles, ...)` throws `ArgumentException` for an undefined `NumberStyles` value or for `AllowHexSpecifier` combined with other flags, `Dictionary.TryGetValue` throws for a null key, and `Uri.TryCreate` throws for an undefined `UriKind` — `false` is reserved for the input failing, not for the options being malformed. Folding an invalid ECC level into `false` would make the caller report "content too long" for content that is nothing of the sort, and would silently change behaviour for anyone moving from `Get` to `Try`. Declaring `EciMode.Iso8859_1` for content that is not Latin-1 throws for the same reason: it is a broken promise about the text, not a capacity outcome.

**What it does not promise.** Only that no *length-related* exception follows. Passing the returned `Version` back through the options struct removes the fit from the subsequent `Create`, but a destination buffer that is too small still throws.

**Where the fit lives.** `RmQRVersionSelector.TrySelect` (single mode) and `RmQRSegmentPlanner.TrySelectVersion` (mixed mode) are the non-throwing cores; the throwing `Select` / `SelectVersion` are wrappers that add the message. One selection path, and it is the same one the encode takes, so the reported version cannot disagree with the version an encode produces — the property `TryGetRequiredBufferSizeTest.RmQR_ReportedSize_MatchesTheEncodeItDescribes` asserts exactly that (reported buffer filled exactly, reported version chosen, `false` implies the encode throws) over content × ECC × strategy × height × segmentation.

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

The `SKBitmap` overloads are not here. `RmQRCodeDecoder` lives in the dependency-free core; bitmap decoding is `RmQRCodeImageDecoder` in the rendering package, reachable as `RmQRCodeDecoder.TryDecode(bitmap, …)` only through C# 14 extension members.

Rendering:

```csharp
public sealed class RmQRCodeImageBuilder : SymbolImageBuilderBase<RmQRCodeImageBuilder>
  RmQRCodeImageBuilder(string content);  RmQRCodeImageBuilder(RmQRCodeData data);          // default quiet zone 2
  RmQRCodeImageBuilder WithErrorCorrection(RmQREccLevel eccLevel);  WithVersion(RmQRVersion version);
  RmQRCodeImageBuilder WithFitStrategy(RmQRFitStrategy fitStrategy);  WithHeight(RmQRHeight height);  WithWidth(int width);   // rMQR-only, listed in the parity test's allowed differences (WithWidth: image width in pixels, height from the aspect ratio, background over the whole image)
  // static helpers exactly as MicroQRCodeImageBuilder (GetPngBytes / GetImageBytes / SavePng / GetSvgBytes / SaveSvg / GetSvgString / WriteSvg / WritePng / WriteImage,
  // string + RmQREccLevel eccLevel = RmQREccLevel.M and RmQRCodeData overloads); their `int size = 512` is the image WIDTH, height follows the symbol aspect ratio
SymbolRenderer.Render(SKCanvas canvas, SKRect area, RmQRCodeData data, SKColor? codeColor, SKColor? backgroundColor, ModuleShape? moduleShape = null, float moduleSizePercent = 1.0f, GradientOptions? gradientOptions = null);
SKCanvas.Render(this SKCanvas canvas, RmQRCodeData data, int width, int height, SKColor? clearColor = null, SKColor? codeColor = null, SKColor? backgroundColor = null, ModuleShape? moduleShape = null, float moduleSizePercent = 1.0f, GradientOptions? gradientOptions = null);
SKCanvas.Render(this SKCanvas canvas, RmQRCodeData data, SKRect area, …same tail…);
```

Rectangular geometry rule shared by every rendering entry: the symbol (quiet zone included) is drawn with a uniform module scale, centered in the target area or canvas (letterbox); `WithModulePixelSize` yields exactly `Width × Height` modules × pixels; `WithSize(w, h)` letterboxes into `w × h` (pad in the clear colour when one is set, the background otherwise); `WithWidth(w)` (the static helpers' `size`, default 512) makes the image `w` wide with the height from the aspect ratio rounded to whole pixels, the background covering the whole image and the symbol drawn at a uniform module scale inside it. The square symbologies' builders follow the same rule since 2026-09-10 and their renderer entries since 2026-09-11: the fill behaviour they inherited from the 2019 renderer produced non-square modules, which readers find or miss depending on the aspect ratio, payload, styling, orientation and reader; the measurements are in [qrcode-symbologies.md](qrcode-symbologies.md).

### Supported

| Area | Coverage |
|---|---|
| Symbology | rMQR |
| Versions | All 32 (R7x43 … R17x139) |
| ECC levels | M, H |
| Data modes | Numeric, Alphanumeric, Byte (ECI 3 for ISO-8859-1, ECI 26 for UTF-8; ASCII omits ECI) |
| Segmentation | One segment in a single mode (default), or the minimal-bit mixed-mode split (opt-in `RmQRSegmentation.Optimal`) |
| Version selection | Exact version, or automatic fit by strategy, optionally within a fixed height |
| Quiet zone | Configurable non-negative size, default 2 (the ISO/IEC 23941 quiet zone) |
| Output | Bit-packed `RmQRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- Kanji mode, intentionally for ENCODING (the decoder reads it since the Kanji decode work; the tables' Kanji column is load-bearing there;
  Japanese text uses Byte mode with UTF-8 ECI, matching the Standard QR product policy)
- FNC1, Structured Append (rMQR does not define Structured Append)

### Symbol parameters (verified)

Version index is height-major (all widths of height 7, then 9, …); it is the 5-bit value carried in the format information. Alignment columns are the 0-based columns of the vertical timing patterns, each capped by a 3×3 alignment pattern at the top and bottom edge. Data codewords are split across blocks with sizes differing by at most one (the smaller blocks first), and every block carries the same ECC codeword count.

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

Kanji count-indicator widths are not in this table: the mode is not encoded.
`RmQRConstants.GetKanjiCountIndicatorLength` carries them (values 2-7, monotone below the byte
widths), pinned by the narrowest-field derivation below and, since the decoder shipped, read for
real by the qrtool Kanji fixtures. That exercise is partial: the four Kanji fixtures cover
R11x43, R13x59, R15x59 and R17x139, i.e. widths 4, 5 and 7, so widths 2, 3 and 6 still rest on
the derivation alone.

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

Other symbol facts the pipeline is built on (all verified, see the record below): a single data mask `((row ⁄ 2) + (col ⁄ 3)) mod 2 = 0`; format information = 6 data bits (ECC bit, M = 0 / H = 1, above the 5-bit version index) BCH-extended to 18 bits, two copies with distinct XOR masks (finder side, sub-finder side); 3-bit mode indicators, terminator `000`, pad codewords 0xEC / 0x11; standard block interleaving; two-column zigzag placement starting at the column pair left of the right-edge timing column, upward first, right column first; quiet zone 2 modules.

---

## Pipeline

### 1. Validate the request

Reject an unknown version, an unknown ECC level, a `height` constraint combined with a `requestedVersion` of a different height, and negative quiet zones. Span sizing / output additionally reject dimensions that overflow `int`, exactly as `MicroQRCodeGenerator` does.

### 2. Analyze text

Shared `TextAnalyzer` (Numeric / Alphanumeric / Byte, single segment). The default charset policy matches Standard QR: ASCII omits ECI, ISO-8859-1 text emits assignment 3, and other Unicode text is encoded as UTF-8 with assignment 26. An explicit `EciMode` can select ISO-8859-1 or UTF-8; explicit ISO-8859-1 rejects unrepresentable input instead of narrowing it.

The analyzer decides the charset for every path. It also decides the mode for the default single-segment path; `RmQRSegmentation.Optimal` decides modes per run instead (see [Mixed-mode segmentation](#mixed-mode-segmentation)) but takes the charset from the same analysis, because the charset is a property of the content and not of the split.

### Mixed-mode segmentation

**What.** `RmQRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a candidate version, and fits the version against that cost instead of the single-mode cost. `RmQRSegmentation.Single` (the default) keeps one run in one mode.

**Why.** rMQR data capacities are small (5 Byte-mode characters at R7x43-M, 150 at R17x139-M), so the modes a payload mixes decide the symbol size far more often than in Standard QR. A URL followed by a numeric identifier is the common case: `https://example.com/p/1234567890123456` needs 313 bits as one Byte run (R11x77, 847 modules) and 249 bits as Byte + Numeric (R15x43, 645 modules).

**Why opt-in, and why the ceiling.** Changing the default would move the emitted bit stream, and therefore the rendered symbol, for existing callers. When the content fits in a single mode, that fit bounds the search from above: only versions the strategy ranks strictly better than it are tried, so the plan is emitted only when it actually shrinks the symbol, and the single-mode stream is emitted byte for byte in every other case. This is the property the end-to-end tests assert for every content, ECC level and strategy.

**What "shrinks" means, and the trap in it.** Core modules, the quantity `RmQRFitStrategy` ranks by. The quiet zone adds a fixed four modules to each dimension, so minimising `height × width` does not minimise `(height + 4) × (width + 4)`: a flatter, wider symbol can have fewer core modules and a larger rendered grid. Measured, 24 characters at ECC H go from R15x59 (885 core, 63×19 rendered) to R11x77 (847 core, 81×15 rendered) — fewer modules, a wider image, and a `TryGetRequiredBufferSize` computed under `Single` that is now too small. This is a property of the fit strategy rather than of segmentation, but segmentation is what makes callers meet it, so it is documented on `RmQRSegmentation.Optimal`, in the README and in the migration notes, and pinned by `Optimal_FewerCoreModulesCanStillRenderLarger`.

**When no single mode fits.** The ceiling does not exist, so the scan runs to the end. This is the one place where `Optimal` accepts input `Single` rejects rather than merely shrinking it, and it is the case the option is worth the most in: 100 lowercase letters followed by 100 digits is 200 Byte-mode characters, 50 over the 150 R17x139-M holds, but 1157 bits of the 1216 available once the digits split off. (Lowercase matters: uppercase letters are in the Alphanumeric set, so the same shape in uppercase is 200 Alphanumeric characters, fits a single mode, and never reaches this path.) Only when a mixed plan fails as well does `RmQRVersionSelector` produce the capacity error, so a genuinely oversized payload reports exactly what it reports today.

**Content that cannot benefit.** All-Numeric content skips planning: digits are the cheapest characters in the cheapest mode, splitting a Numeric run never lowers its payload, and every extra run adds a header, so one run is provably the optimum. Without the shortcut a 361-digit payload paid 11.9x a `Single` encode to rediscover it; with it, 1.05x.

**Bounding the scan.** The scan is best-first, i.e. smallest-version-first, so for mixed content most early ranks are hopeless. Three filters answer in increasing order of price, and a candidate only reaches an expensive one when the cheap ones could not decide.

- *Trivial bound.* One O(n) pass and no table, pricing each character at the cheapest rate any mode could give it. A partial group only ever costs more per character, so this cannot exceed a real plan. When no better-ranked version holds even this, no split can move the symbol and nothing further runs — which is what makes `Optimal` roughly free on content it cannot help.
- *Floor.* A cost run at the narrowest count indicator widths any version uses, deferred until a candidate clears the trivial bound. Widening a count indicator only raises the price of the run carrying it, and the minimum over plans of a pointwise larger cost is itself larger, so this is a lower bound everywhere.
- *Ceiling.* That same run also yields a plan, and re-pricing it at a version costs the floor plus one count indicator delta per run it contains — arithmetic, no second cost run. Being an actual plan, its price bounds the optimum from above, so a version holding it is known to fit.

Only versions between floor and ceiling are priced for real. Against the Single encode of the same content in the same benchmark run: the floor alone took the 150-byte worst case from 13.2x to 3.0x; the ceiling cut a further 26-42%, most where the scan hurt most; the trivial bound then collapsed the never-wins cases to roughly free (150 lowercase 2.3x to 1.1x, 120 alphanumeric 3.4x to 1.1x) while leaving the winning cases untouched.

The bound is deliberately crude, and its one blind spot is worth recording: it prices each character at its best possible rate regardless of where it sits, so finely alternating content (`a7a7…`) looks far cheaper than it is and clears the bound, only for the split to lose after planning. Seeing that switching modes every character never pays requires modelling the switch cost, which is the dynamic program itself.

A ceiling taken from a *second* cost run at the widest widths was tried first and reverted: that band widens about 5 bits per run, so for the many-run content needing the most help it was wide rather than empty, and the extra run cost more than it saved (alternating 10-character groups regressed 9.0 us to 11.2 us). Deriving it from the floor plan costs no extra run and is tighter, which is what makes it pay.

**What it costs, and what drives it.** Planning allocates nothing. Its cost is driven by *how much the split helps*, not by how mixed the content looks: the lower a split drives the bit cost, the more candidate versions clear the floor and get priced. `RmQRSegmentationEncode` pins this by varying content shape with a same-run Single partner on every row, so its Ratio column is the multiplier directly. Sorting that column separates the populations exactly: content no split can help sits at 1.0-1.1x (all digits short-circuited, 150 lowercase and 120 alphanumeric ruled out by the trivial bound), the one blind spot at 2.2x (alternating every character), and content the split wins on above it (60 lowercase + 60 digits 4.8x, 150 half and half 5.5x, alternating in tens 6.4x). Where planning runs, cost is linear in length at a fixed shape (20 / 60 / 120 / 150 characters of half letters half digits: 0.9 / 2.4 / 4.5 / 5.7 us). The consequence worth stating plainly is that the expensive inputs are the rewarding ones.

The corollary is why this cannot be made free: whether a split helps is only knowable by planning it. All-Numeric is the one shape where "no gain" is provable up front. A payload of a known shape — a URL followed by a numeric identifier — therefore wins predictably, while arbitrary user input trades a few microseconds per symbol against the chance of a smaller one.

**How the optimum is exact.** A run does not cost a constant per character (Numeric packs 3 digits into 10 bits, Alphanumeric 2 characters into 11), so the dynamic program carries the group remainder in its state rather than rounding a per-character average, which would misprice the tail of every run. The state layout and transitions are in `ModeSegmenter`, shared with the Standard QR and Micro QR planners: the symbologies differ in header widths (3-bit vs 4-bit vs version-dependent mode indicators, their own count indicator tables) and Micro QR additionally in per-version mode availability, all taken as parameters — a single implementation keeps the cost models from drifting apart, which mattered while the Standard QR and rMQR planners each carried their own copy of the UTF-8 surrogate cost rules (the Micro QR planner was born after the extraction and never had one). The version scan, its bounds and the selector integration stay in `RmQRSegmentPlanner`: they depend on rMQR's version space, not on the cost model.

**Bounds.** Content longer than the largest character count any rMQR symbol holds in any mode (361, Numeric at R17x139-M) is rejected before any cost run, so pathological input never pays for planning. Because a mixed plan can encode content no single mode holds, that limit is a rejection rule and not merely a work cap, and its margin is three bits — the derivation sits with the constant in `RmQRSegmentPlanner`. A plan is also capped at a run count above what the largest capacity could hold. The reconstructed plan is re-costed from the byte counts the encoder will actually emit and rejected on disagreement, because the bit-stream writers store without per-flush bounds checks.

**ECI.** One prefix ahead of the first run: an rMQR decoder carries the declared charset across the runs that follow, so a plan needs no repetition. Its 11 bits are part of the cost the version scan compares. The ECI does not shield everything, though: the shared byte-segment decoder consumes a leading EF BB BF of every segment even behind an explicit UTF-8 declaration, so a plan that relocates a mid-content U+FEFF to a Byte-run start is rejected (`ModeSegmenter.HasBomRelocatedToARunStart`) and the single-mode stream — which keeps the character interior — is emitted instead; when no single mode fits either, the ordinary does-not-fit answer is given (rationale: specs/standardqr-encoder.md, "Plans the byte-segment decoder would misread are rejected").

**Kanji.** Still not encoded, so a Japanese payload mixes Byte (UTF-8) with Numeric runs rather than reaching for 13-bit Kanji. The decoder reads Kanji segments other encoders produce.

### 3. Fit the version

Required bits = optional 11-bit ECI prefix (`111` + 8-bit assignment) + 3 (data mode) + count indicator (per version, table above) + payload bits. The terminator may shrink to the remaining capacity, including zero bits. Automatic fit is a table scan (versions pre-ordered best-first per strategy with their capacity per mode × ECC × ECI-presence, height as a bitmask); it selects exactly what the definitional "best fitting version" scan selects, and a test pins the two for every input.

Under `RmQRSegmentation.Optimal` the required bits are the planned mixed-mode cost for the candidate version rather than the single-mode cost. The candidate set, the strategy ordering, the height constraint and the error text are all unchanged; the one behavioural difference is that input the single mode overflows at every version can now succeed instead of throwing (see [Mixed-mode segmentation](#mixed-mode-segmentation)).

- `requestedVersion` given: use it or fail with an actionable capacity error (actual length, applicable maximum in mode units, remedy: shorten, lower ECC, choose a larger version, or use Standard QR). Under `Optimal` a requested version that the single mode overflows is still accepted when the mixed-mode plan fits it.
- Otherwise the candidate set is all 32 versions, or the versions of the constrained `height`; keep those whose data-codeword capacity holds the required bits; choose by `fitStrategy`:
  - `MinimizeArea`: fewest modules (height × width); ties toward the smaller height (i.e. the wider symbol).
  - `MinimizeWidth`: smallest width; ties toward the smaller height.
  - `MinimizeHeight`: smallest height; ties toward the smaller width.
- No candidate fits: capacity error stating the maximum for the most capacious candidate in the set.

### 4. Build the data codewords

Optional ECI mode `111` plus an 8-bit assignment, then per run a 3-bit data-mode indicator, count indicator and payload bits (one run by default, the planned runs in order under `Optimal`), terminator `000` (shortened at capacity), zero bits to a byte boundary, alternating 0xEC / 0x11 pads to the data-codeword count. The stream is written straight into the caller's buffer (no intermediate copy); vectorized value kernels exist per mode on x64 (see Decisions), all producing the identical stream. UTF-8 stays in a separate cold writer so adding ECI does not address-expose the hot writer locals.

### 5. Reed-Solomon per block, 6. interleave

Blocks per the table (smaller data blocks first, sizes differ by at most one), ECC per block via shared `EccBinaryEncoder`, then Standard-QR-style interleaving: all data codewords column-wise across blocks, then all ECC codewords. Remainder bits (free modules − 8 × total codewords, 0..7 per version) are light.

### 7. Place function patterns and data

Finder (7×7 with separators), sub-finder (5×5), four edge timing patterns, the two corner patterns, vertical timing columns with 3×3 alignment patterns at both ends, and both format regions are function modules; data fills the rest in zigzag order. Coordinates live in code comments in Phase 5.5. The fast placer reproduces the reference module for module from cached per-version tables (see Decisions).

### 8. Fixed mask, 9. format information

The single mask is applied to data modules while placing. Both format copies come from a static 64-entry table indexed by (version, ECC).

---

## Rendering

`RmQRCodeImageBuilder` derives from `SymbolImageBuilderBase<TSelf>` and adds `WithErrorCorrection(RmQREccLevel)`, `WithEciMode(EciMode)`, `WithVersion(RmQRVersion)`, `WithFitStrategy(RmQRFitStrategy)`, `WithHeight(RmQRHeight)`; quiet zone default 2; no icon overlay (no ECC headroom to spend); the single finder takes a shape. Canvas layout is rectangular: with a module pixel size the content is `width × height` modules at that size; with only an explicit canvas size the symbol is fitted with a uniform module scale and centered on whole pixels (letterbox), never stretched non-uniformly. Standard and Micro QR now share it (2026-09-10). Shipped in Phase 5.7 exactly so; additionally, `WithWidth(int)` (public since the 2026-08-16 review; the static helpers use it with their `size`, and 512 is the default when no size option is given) makes the image that wide with the height following the symbol aspect ratio rounded to whole pixels, the background covering the whole image and the symbol drawn at a uniform module scale inside it (no clear-colour pad, so the image is opaque with an opaque background; the review found that letterboxing this aspect-derived canvas again left 1-3 transparent columns on 12 of the 32 versions), and the low-level `SymbolRenderer.Render(canvas, area, RmQRCodeData, …)` / `SKCanvas.Render` overloads letterbox into the given area with the background covering the whole area.

---

## Why

- Separate `RmQR*` entry points, not `Create` overloads: version, ECC and fit semantics differ per symbology; see [QR Symbology Architecture](qrcode-symbologies.md).
- Two-dimensional fit exposed as strategy + optional height constraint: rMQR exists to fit narrow print lanes; "fixed height, auto width" is the dominant real-world request (libzint's `R<h>xauto`), and area/width/height minimization covers the rest without a free-form size search that would mostly select non-existent sizes.
- Letterbox instead of stretch for explicit canvas sizes: a rectangular symbol drawn into an arbitrary rectangle at non-uniform scale is not the same symbol; module aspect ratio must survive.
- Fixed mask means the placer is a static permutation per version; no mask scoring machinery is designed in.
- Superseded 2026-08-18: emitting UTF-8 without ECI made decoding depend on reader heuristics.
  rMQR supports ECI unlike Micro QR, so the encoder will explicitly emit ISO-8859-1 assignment
  3 or UTF-8 assignment 26, following Standard QR's policy. Kanji mode remains intentionally
  un-encoded (the decoder reads it); ECI + Byte mode is the interoperable Unicode path.

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Naming | `RmQR*` family, `RmQRVersion` with 32 named members | Never (mirrors shipped `MicroQR*`) |
| Version fit API | `RmQRFitStrategy` + `RmQRHeight?` | User demand for width constraints (would add `RmQRWidth?` symmetric to height) |
| Default fit strategy | `MinimizeArea` (fewest modules), **confirmed in Phase 5.6**: both reference encoders choose the same versions automatically (libzint and qrtool with no version option: 12 digits at M → R11x27, 15 → R13x27, 100 → R11x77, measured by `probe-rmqr`), so the default keeps interoperability parity and the printable-area argument; the surprise case (12 digits at M: R11x27 (297) rather than the flatter R7x43 (301)) is documented in the generator XML docs and pinned by `RmQRCodeGeneratorUnitTest`; users wanting the flattest symbol use `MinimizeHeight` or a fixed `RmQRHeight` (README example lands with the rendering surface in 5.7) | User feedback after release |
| Explicit-canvas layout | Uniform scale, centered (letterbox) | - |
| ECI on encode | Implemented 2026-08-18: none for ASCII; assignment 3 for ISO-8859-1; assignment 26 for UTF-8. Only R7x43-H cannot hold an ECI header plus a one-byte payload | Additional charset demand (cross-symbology decision) |
| Kanji | Not encoded; use Byte mode with UTF-8 ECI. Decoding is supported (JIS X 0208), so the tables' Kanji column is load-bearing | Encoding: a policy change backed by concrete demand |
| Mixed-mode segmentation | Opt-in via `RmQRSegmentation.Optimal`, defaulting to `Single`. Changing the default would move the emitted bit stream and therefore the rendered symbol for every existing caller, and planning is a search that cannot be free (whether a split helps is only knowable by planning it). The bounds make it roughly free where it cannot help, which weakens but does not remove the argument | A major version allows changing the default, at which point `Single` is only ever better by accident; the breakage would be callers relying on the "too long" exception, plus any caller pinned to today's rendered dimensions (see the quiet-zone note above) |
| Segmentation surface | Two values rather than three. A `WhenNeeded` middle value ("plan only when the single mode does not fit") was evaluated and rejected: for a requested version `Optimal` already costs nothing when the single mode fits, so the value only added the top-end rescue, and the bounds later made the ordinary case roughly free as well | A concrete caller needs the rescue without ever wanting a smaller symbol |
| Interleaver | Lifted `BinaryInterleaver` to `Internals.BinaryEncoders` (Phase 5.4): it never used the version, only the `ECCInfo` block structure; the remainder-bit count became a parameter | - |
| Placer performance | Reference per-module placer first (Phase 5.5), then the benchmark-driven fast path (follow-up, 2026-08-16): per-version tables built once by the reference painters (painted template per version × ECC, zigzag order as core indices, mask per position, column-pair segmentation), vector bit expansion fused with the mask, 16-bit pair stores + index scatter; the reference stays the source of truth (tables, decoder predicate) and the parity test pins both. ARM64 gained a second store tier (2026-08-20, `RmQRModulePlacer.Simd.Arm.cs`): eight consecutive columns are transposed in registers so one symbol row is one 8-byte store instead of one 16-bit store per two modules, and the leftovers are segmented by row RUN rather than by whole column pair — the pair test disqualified 56 % of R11x27's modules although 91.8 % of those sit in stretches where both columns are ordinary data, leaving only 4-12 % genuinely isolated. Encode E2E improved an honest 15-43 % after accounting for a -4.5 % control drift. The portable expand became branch-free SWAR in the same round, which is what netstandard2.0/2.1 and non-SIMD targets run for the whole message | Placement stops being about half of the encode pipeline, or a profile names the template copy (33 ns of 287 ns at R17x139) |
| Bit-stream performance | Reference shape first (Phase 5.3), then the benchmark-driven fast path (follow-up, 2026-08-16): raw-local writer, SWAR / SSE numeric and alphanumeric value kernels, SSE2 byte narrowing, capability-gated with scalar fallbacks; kernel-level parity tests pin vector vs scalar, the naive-reference parity pins the stream. Latin-1 gained a portable `Vector128.Narrow` tier (2026-08-20) for targets with 128-bit vectors and no SSE2 (ARM64 NEON, WASM), 16 characters per iteration: 9.2x on the writer and -11 % on byte-mode encode E2E. What was slow was the writer-state update rate, not character decoding — every gain in that round came from making one append cover more characters | The numeric and alphanumeric writers were measured and DECLINED, so their missing ARM tiers are a decision rather than an oversight: post-placement shares are byte 10.4 %, Latin-1 ECI 8.5 %, alphanumeric 5.4 %, numeric **0.3 %**. Alphanumeric batching won 19-28 % in isolation and measured worse end to end (331.6/304.7 → 339.9/348.2 ns) because production keeps all three writers in one `switch`, so enlarging one arm changes the whole method's codegen; it becomes available for free if encode is ever restructured so each mode compiles independently. Numeric is -9 % at 361 digits but +6 % at 12, the only numeric payload in the E2E set |

## Verification record

Performed 2026-08-15 with the pinned qrtool 0.13.2 binary (`--variant rmqr`, `--type ascii` module-exact output; second encoder lineage per the [fixture record](qrcode-test-fixtures.md)), before any implementation existed. Each item is now a permanent test (Phase 5.1b): the structural rows in `RmQRConstantsUnitTest`, the oracle rows in `RmQRConstantsOracleTest` over the committed corpus (both lineages, all 32 versions × M/H; 96 single-character libzint symbols for the count widths).

| Fact | How verified | Result |
|---|---|---|
| Dimensions and version index order | ASCII output size for all 32 `-v H W` combinations | 32/32 |
| Data capacities (N/A/B × M/H) | Binary search of the longest accepted payload per version × ECC × forced mode | 192/192 match the table above (also matches the published Denso capacities) |
| Data codewords per version × ECC | Reproduce all 192 capacities from data codewords + count widths | 192/192 |
| Total codewords | Free-module count from an independent function-pattern painter must equal 8 × total + remainder (0..7) | 32/32 after correcting R17x59 (88, not the initially recalled 90; 90 would also require 31 ECC per block at H, above the 30 maximum) |
| Count indicator widths (N/A/B) | Read the first data codewords of one-character payloads from oracle matrices (inverse zigzag + unmask + deinterleave), width = position of the count's leading 1 | 96/96 (three numeric widths of the initial recall were off by one and corrected) |
| Count indicator widths (Kanji) | No oracle emitted Kanji when this column was written, so it is pinned by derivation: Table 3 takes the narrowest count field that still expresses the largest count the version's M-level data capacity allows. The rule is validated by reproducing all 96 measured N/A/B widths above, then applied to Kanji | 32/32 after correcting R17x99 (6, not the transcribed 7) |
| Format information | Both 18-bit copies of all 64 version × ECC symbols equal BCH(18,6) of (ECC bit, version index) XOR the copy's mask | 128/128 |
| Mask, zigzag start and direction, interleaving | The R7x43-M "1" symbol yields exactly the predicted codewords `22 20 EC 11` and multi-block versions deinterleave to the predicted streams | Confirmed |
| Alignment column positions, sub-finder and corner patterns | Visual inspection of R7x43 / R9x59 / R11x27 plus the free-module count agreement above | Consistent |

Not verified here: the ISO/IEC 23941 misdecode-protection question (whether ECC counts reserve codewords beyond
the correction capacity). The decoder resolves it indirectly — the block structure verified
above leaves at most one unused ECC codeword per block, and zxing-cpp corrects rMQR at full
Reed-Solomon strength — but the Table 8 capacity column itself is still unread; see the Correction
cap decision in [rMQR Decoder](rmqr-decoder.md).

## Lessons Learned

Pre-implementation (from the verification itself):

- Published capacity tables cannot pin count-indicator widths: byte alignment slack lets several widths reproduce the same capacity. Reading the width directly from an oracle's bit stream does pin it, and doing so also validates the mask, the zigzag start, and the interleaving order in one step.
- A recalled table can be internally consistent and still wrong: the R17x59 total-codeword error passed the "ECC divisible by blocks" check and was only caught by the geometric free-module count. Structural invariants that connect independent tables (geometry ↔ codewords ↔ ECC bounds) are the transcription guard, not per-table plausibility.
- On multi-block versions the leading data bytes in placement order are interleaved; any "read the first bits" check must deinterleave first or it silently reads block-2 codewords.

Implementation lessons, consolidated from the retired phase-by-phase progress log.

- Placement, ARM64 store tier (2026-08-20). Measured against the shipped portable path on Apple M2: 0.29x at R17x139, 0.34x at R13x99, 0.50x at R11x59, 0.56x at R7x43, 0.68x at R11x27 — it wins every size, so no size switch is needed. Three designs were measured and rejected. **Generalized blocks** ("four adjacent pairs × their common data row range") raised in-block coverage to 89 % and lost at every size: the ZIP network costs a fixed ~40 instructions per block whatever its height, so short blocks pay a tall block's price and take modules away from the cheaper run path; raising the minimum block height to 8 rows recovered a tie at the largest symbol and still lost everywhere else. **Branch-free split lists** (per-version clean-up / clean-down / irregular lists, removing both per-pair branches) lost at R17x139 because splitting the pairs out of walk order destroys the bit array's read locality, and at 1,858 bytes that costs more than the branches saved — branch count was not the right objective. A **wider expand step** (4 message bytes per iteration) lost via `CreateScalarUnsafe`, where the GPR→SIMD FMOV is paid every iteration and LD1R replicates straight from memory instead, and merely tied via two independent LD1R broadcasts, because the expand is not load-bound.

- Bit-stream fast path (follow-up, 2026-08-16): a memory-backed writer's cost is the per-flush range check, not the struct; a fixed-lane horizontal instruction (`pmaddwd`) cannot express a 3-digit group that straddles its lane pairs, so the group must own the load; at 10-50 ns per encode, code-layout noise (±30 % on identical code) is the measurement floor, and only same-run, same-mode deltas above it count.

- Mixed-mode segmentation (2026-08-22). Four lessons, three of them found only by trying:

  - **A bound computed by a throwing function is silently a rejection rule.** Using the single-mode fit as a ceiling meant calling the throwing selector first, which rejected content that overflows every version in one mode but fits once split — failing exactly where the feature is worth the most. Whenever an optimisation bounds itself by an existing result, ask what happens when that result does not exist.
  - **Derive a second bound from work already done, not from a second pass.** A ceiling taken from an extra cost run at the widest count indicator widths is sound but slower: the band it opens widens by about 5 bits per run (widest minus narrowest count indicator, which is 5 in every mode), so for the many-run content that needs the most help it is wide rather than empty, and the extra run costs more than it saves. Re-pricing the floor's own plan gives a tighter ceiling for no additional run, and that is what pays.
  - **"Is this optimisation reachable at all?" is usually far cheaper to answer than the optimisation.** The per-character trivial bound costs one O(n) pass and removed the entire never-wins population from roughly 2-3.5x down to 1.1x. Its blind spot is the price of being cheap: it prices each character at its best rate regardless of position, so finely alternating content clears it and still loses after planning. Seeing that requires the switch cost, which is the dynamic program itself.
  - **Pin the decision, not the timing.** Every bound here is an optimisation whose failure mode is a wrong version rather than a slow one, so the guard is a parity test against a no-pruning, no-memo, no-ceiling reference scan, not a benchmark. Benchmarks caught none of the three real defects in this work; the parity and soundness tests caught all of them.

## Validation

Per phase (every exit met): structural table tests + oracle format/dimension tests (5.1), naive-reference parity for the bit stream (5.3), interleave reference (5.4), extraction test over all 64 combinations (5.5), the `spot-check-rmqr` zxing-cpp gate over every version × ECC × mode (5.6), module-to-pixel rendering parity (5.7); Standard and Micro QR benchmarks flat at every step. The 2026-08-18 ECI follow-up adds exact assignment-3/26 streams, ECI capacity-boundary and exhaustive selector parity tests, public class/span/sizing round trips, unsupported-charset validation, and a 318-symbol zxing-cpp text/bytes/version/ECC gate with 63 ECI-3 and 63 ECI-26 symbols.
- A table column no oracle in this repo could reach at the time still had a check available: derive it. The Kanji count widths sat in the tables for months marked "spec-transcribed, unverified" because no oracle in this repo was known to emit Kanji at the time (qrtool can, found later) — but Table 3's own rule (narrowest count field that still holds the largest count the version's data capacity allows) reproduces all 96 measured Numeric/Alphanumeric/Byte widths exactly, which validates the rule on evidence and then applies it to the column that had none. It found R17x99 transcribed as 7 where the rule gives 6, latent since Phase 5.1b and, until the decoder shipped, invisible to every test because nothing read the column. The bound tests that did cover the column (`b >= k >= 2`) passed on the wrong value; a loose invariant on an unverified column is not coverage.
