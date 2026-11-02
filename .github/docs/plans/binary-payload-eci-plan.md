# Binary payloads, ECI and the encoder's choices

## Purpose

FeatherQR writes and reads text only. A payload that is not text cannot be written as it is, and a symbol whose bytes are not in one of three charsets is read as converted text or not at all. This plan adds bytes in and bytes out to all three symbologies, sets one ECI policy against ISO/IEC 18004:2015 and ISO/IEC 23941, reports the error correction level and mask the encoder chose, and makes the builders refuse encoding settings they cannot apply.

It records where things stand, the problems, the design, the decisions the maintainer rules on, and the phases. When it completes, its durable content moves into the specs and this file is deleted.

## Scope

| In | Out |
|---|---|
| Bytes in: Standard QR (with Structured Append), Micro QR and rMQR | Payload helpers (Wi-Fi, vCard), still deferred |
| Bytes out: the matrix and single-image calls of the three decoders, and the SkiaSharp `SKBitmap` call | Bytes from the several-symbol, `Stream` and async calls, and from `SymbolDecoder` (additive, on request) |
| ECI: writing every assignment number, reporting the assignment on decode, one written policy for both directions | Text decoding of more charsets (B9) |
| ECC level and mask on `QRCodeData`, `MicroQRCodeData` and `RmQRCodeData` | ECC level on the sizing result (`*CalculatedSize`) |
| Builders refuse encoding setters on a ready-made code | Kanji mode for bytes input (B5) |

## Where things stand

### Writing

- The three generators take `ReadOnlySpan<char>` only. `EciMode` has three values. `Default` writes no header over ASCII, ECI 3 over text ISO-8859-1 holds, and ECI 26 over the rest. `Iso8859_1` and `Utf8` force one of the two.
- The header writer holds an 8-bit assignment, so it can write 0 to 127 (`QRBinaryEncoder.WriteSegments`, `EciModeExtensions.GetStandardQrHeaderBits`, `RmQRBinaryEncoder.WriteEciHeader`). Only 3 and 26 are written today, so this is a limit, not a defect.
- Micro QR has no ECI. Text outside ISO-8859-1 is written as UTF-8 with nothing to declare it, and so is ISO-8859-1 text whose bytes this library's decoder would read as UTF-8 or Shift_JIS (`MicroQRCodeGenerator.AsTheDecoderReadsIt`).
- The generator chooses the version, the final error correction level (raised under `BoostEccLevel`) and the mask, and writes the last two only into the format information. `QRCodeData` and `MicroQRCodeData` report `Version` and `Size`, and `RmQRCodeData` reports `Version`, `Width` and `Height`. The span `Create` returns a byte count.
- `QRCodeImageBuilder` made from a `QRCodeData` accepts `WithErrorCorrection` and `WithEciMode` and ignores them, as its documentation says, while its other encoding setters throw `InvalidOperationException`. The Micro QR and rMQR builders throw for the same calls.

### Reading

- Every decode call returns `string` or writes `char`.
- ECI 1, 3 and 27 are read as ISO-8859-1, 20 as Shift_JIS and 26 as UTF-8. Every other assignment ends the decode with `UnsupportedContent`, and the number is not reported (2.0.0 plan, D7). The designator reader handles all three lengths.
- A Byte segment with no ECI is read as UTF-8 when it validates, as Shift_JIS when its bytes give it away, and as ISO-8859-1 otherwise, which is the standard's default ([standardqr-decoder.md](../specs/standardqr-decoder.md#decisions), Byte-mode charset heuristic).
- Under ECI 26 or behind a byte order mark, an ill-formed UTF-8 sequence becomes U+FFFD.
- A Kanji segment is read as JIS X 0208 whatever ECI precedes it.
- A Structured Append part is text, so a multi-byte character cut at a symbol boundary shows U+FFFD on both sides.
- `QRCodeDecodeInfo` and `MicroQRCodeDecodeInfo` carry `EccLevel` and `MaskPattern`. rMQR has one fixed mask, so `RmQRCodeDecodeInfo` carries `EccLevel` alone. Nothing is missing on the decode side.

## Problems

| # | Problem | Effect |
|---|---|---|
| P1 | No bytes input | Compressed data, a key or a file cannot be written as it is. Mapping each byte to a character from U+0000 to U+00FF writes the right bytes in Standard QR and rMQR, but declares them ISO-8859-1 text with an ECI 3 header whenever a byte is 0x80 or above. In Micro QR the bytes change, because the generator rewrites as UTF-8 any text its decoder would not read back as ISO-8859-1 |
| P2 | The header writer stops at assignment 127 | Assignments such as 899 (binary data) cannot be written, and bytes input needs them |
| P3 | No bytes output | A binary payload comes back as text through a charset chosen by declaration or by guess. A declared or guessed UTF-8 reading loses bytes to U+FFFD. A guessed Shift_JIS reading can fail as `UnmappedCharacter`. Only an ISO-8859-1 reading keeps every byte, and the caller cannot ask for it |
| P4 | An unknown assignment fails the whole symbol | A corrected, parsed symbol is discarded though its bytes are intact, and the caller is not told which charset it needed. This covers 899 and every charset but the three this library maps |
| P5 | The decode info does not report the ECI | A caller cannot tell a declared charset from a guessed one |
| P6 | Structured Append parts are text | A set split inside a multi-byte character cannot be joined back exactly |
| P7 | The encoder's choices are not reported | The level written under `BoostEccLevel` and the automatic mask can be learned only by decoding the symbol |
| P8 | The builders disagree on a ready-made code | The same `WithErrorCorrection` call is ignored on Standard QR and throws on Micro QR and rMQR, so a caller can believe a level was applied that was not |

P1 to P6 are what [standardqr-encoder.md](../specs/standardqr-encoder.md) lists as not implemented (arbitrary ECI assignment numbers, arbitrary binary payload input) and [standardqr-decoder.md](../specs/standardqr-decoder.md) as unsupported (other ECI charsets). P7 and P8 are settings whose effect cannot be seen.

## ECI policy

The standard models every mode as producing bytes. Numeric and Alphanumeric give ASCII, Byte gives its bytes, and Kanji gives the Shift_JIS byte pairs it compacts. An ECI says how the bytes after it are interpreted, and with none the interpretation is ISO-8859-1. The reader passes the assignment on with the bytes (the ECI protocol) rather than having to know every charset. Phase 0 pins the section for each of these statements.

This plan adopts three rules.

1. Writing is always valid and declares only what is true. Text gets the charset it was converted into, as today. Bytes get the assignment the caller names, or no header.
2. Reading bytes is literal. Every mode yields the bytes the standard defines, no charset is applied, and the assignment is reported. An assignment this library has no charset for is not an error.
3. Reading text follows the standard except where the literal reading cannot give the text the writer meant, and each such deviation is named in the decoder's Decisions. There are two. A Byte segment with no ECI is read by its bytes, which is already recorded. A Kanji segment under an ECI is read as JIS X 0208 (B8). An assignment with no charset here stays `UnsupportedContent` for text, and is now reported.

| Topic | ISO/IEC 18004:2015 and ISO/IEC 23941 | Today | After this plan |
|---|---|---|---|
| Assignment range | 000000 to 999999, in a designator of one to three codewords | Read: all. Written: 0 to 127 | Written: all |
| No ECI | ISO-8859-1 | Text: by the bytes (recorded deviation) | Text: unchanged. Bytes: no interpretation, no assignment reported |
| What an ECI governs | The bytes of every mode after it | Byte segments only | Bytes: every mode. Text: Kanji stays JIS X 0208 (B8) |
| Unknown assignment | Passed on with the data | `UnsupportedContent`, number dropped | Bytes: read, number reported. Text: `UnsupportedContent`, number reported |
| Binary data | Assignment 899 in the ECI register | Not written, not read | Written on request (B3), read as bytes |
| Micro QR | No ECI | Undeclared UTF-8 for text outside ISO-8859-1 | Unchanged. Bytes are written undeclared |

## Design

### Bytes in

```csharp
// QRCodeGenerator (names under B4, the assignment under B2)
public static QRCodeData Create(ReadOnlySpan<byte> data, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default, int? eciAssignment = null);
public static int Create(ReadOnlySpan<byte> data, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = default, int? eciAssignment = null);
public static bool TryGetRequiredBufferSize(ReadOnlySpan<byte> data, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = default, int? eciAssignment = null);
public static QRCodeData[] CreateStructuredAppend(ReadOnlySpan<byte> data, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default, int? eciAssignment = null); // B6

// Calls
QRCodeGenerator.Create(payload, QREccLevel.M);                         // no ECI header
QRCodeGenerator.Create(payload, QREccLevel.M, eciAssignment: 899);     // declared binary
QRCodeGenerator.Create(sjisBytes, QREccLevel.M, options, eciAssignment: 20);
```

`RmQRCodeGenerator` gets the same `Create` and `TryGetRequiredBufferSize` overloads. `MicroQRCodeGenerator` gets them without `eciAssignment`, since Micro QR has no ECI.

The bytes given are the bytes in the symbol. `Version`, `QuietZoneSize`, `MaskPattern`, `BoostEccLevel` and `Segmentation` apply as they do to text. Optimal segmentation may write a run of digits in Numeric mode or a run of uppercase letters in Alphanumeric mode, since both yield the same ASCII bytes. Options that mean something only for text are refused with `ArgumentException` (B5), and so is an assignment outside 0 to 999999.

The assignment is a parameter, not an option, because it describes the bytes rather than how to encode them. The generator options keep `EciMode` as their only ECI member, and a text call has no assignment parameter, so neither can be given to the wrong kind of call. An assignment read from a symbol passes straight back: `Create(bytes, info.EccLevel, eciAssignment: info.EciAssignment)` writes the declaration the symbol had.

### Bytes out

```csharp
// QRCodeDecoder and RmQRCodeDecoder, and MicroQRCodeDecoder with its own info type
public static bool TryDecodeBytes(ReadOnlySpan<byte> modules, int size, Span<byte> destination, out int bytesWritten, out QRCodeDecodeInfo info);
public static bool TryDecodeBytes(ReadOnlySpan<byte> modules, int size, out byte[] bytes, out QRCodeDecodeInfo info);
public static bool TryDecodeBytes(QRCodeData data, out byte[] bytes, out QRCodeDecodeInfo info);
public static bool TryDecodeImageBytes(ReadOnlySpan<byte> luminance, int width, int height, Span<byte> destination, out int bytesWritten, out QRCodeDecodeInfo info);
public static bool TryDecodeImageBytes(ReadOnlySpan<byte> luminance, int width, int height, out byte[] bytes, out QRCodeDecodeInfo info);

// QRCodeImageDecoder, MicroQRCodeImageDecoder and RmQRCodeImageDecoder (FeatherQR.SkiaSharp)
public static bool TryDecodeBytes(SKBitmap bitmap, out byte[] bytes, out QRCodeDecodeInfo info);
```

The name is not `TryDecode`, because an `out byte[]` overload beside `out string` would make every `TryDecode(data, out var text)` ambiguous.

What the bytes are:

- A Byte segment gives its bytes as written, a byte order mark included.
- Numeric and Alphanumeric segments give the ASCII bytes of their characters.
- A Kanji segment gives each character's two Shift_JIS bytes, the pair Kanji mode compacted.
- Under FNC1 the separator is 0x1D, by the rule the text follows. The application indicator is on the info, not in the bytes.
- ECI and Structured Append headers are not bytes. They are on the info.

The parts of a Structured Append set, joined in index order, give the bytes the set was made from. Phase 2 checks whether `GetMaxDecodedLength` already bounds the byte count (Numeric is the densest mode in both readings). The documentation then says so, or a byte bound is added.

### The assignment on the decode info

`QRCodeDecodeInfo` and `RmQRCodeDecodeInfo` gain `int? EciAssignment`, the first assignment the stream declares, or `null` when it declares none. The text calls report it too, on success and on `UnsupportedContent`, so a caller that gets `UnsupportedContent` with an assignment knows it can read the bytes and decode them with its own `Encoding`. Results of the several-symbol calls carry it through their `Info`. `MicroQRCodeDecodeInfo` gets nothing.

A stream whose Byte and Kanji segments fall under two different assignments, no ECI counting as one, has no single interpretation for its bytes. The bytes calls refuse it (B7). The text calls read it as today when every assignment has a charset here.

### ECC level and mask on the encode result

`QRCodeData` and `MicroQRCodeData` gain `EccLevel` and `MaskPattern`, and `RmQRCodeData` gains `EccLevel`. They are read from the symbol's own format information when asked. Nothing is added to `Create`, so it allocates and runs as before. The values survive `GetRawData`, and a code restored from the bytes the span `Create` wrote reports them too, because the format information is part of the matrix.

Every public instance must then hold readable format information (B10). Two kinds do not today. The blank constructors (`QRCodeData(int, int)`, `MicroQRCodeData(MicroQRVersion, int)`, `RmQRCodeData(RmQRVersion, int)`) make an all-light matrix that nothing public can draw into, since no indexer has a public setter. Inside `src` only the generators call them. The raw-data constructors accept any modules behind a valid header. An all-light format area reads as no format in Standard QR and rMQR, five bits from the nearest valid pattern. In Micro QR it is three bits from M4-L with mask 1, inside the correction distance, so a property read from a blank Micro QR matrix would report a level and a mask the matrix never had. With the blank constructors internal and the raw-data constructors checking, the generators and a checked restore are the only public ways to an instance. All three types are sealed classes, so `default` is `null` rather than an empty matrix, and nothing public changes a module after construction, so the format information an instance starts with stays readable.

### Builders

`QRCodeImageBuilder.WithErrorCorrection` and `WithEciMode` throw `InvalidOperationException` on a builder made from a `QRCodeData`, as its other encoding setters and the Micro QR and rMQR builders do (B11).

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| B1 | Release | Decided 2026-11-02: 2.0.0, as Phase 6h of the [2.0.0 plan](featherqr-2.0.0-plan.md), with the freeze waiting for it. Phase 1 had to land before the freeze in any case, because a setter that starts to throw and constructors that leave the public surface can change only in a major. Bytes and the assignment are additive, but in 2.0.0 callers migrate once |
| B2 | How an assignment number is represented, and where it goes | Decided 2026-11-02: an `int?`, passed as the last parameter of the bytes calls and reported as `EciAssignment` on the decode info. The `EciMode` enum stays as it is and remains the only ECI member of the generator options. `EciMode` is a policy for converting text, where `Default` means "choose". An assignment is a fact about the bytes, found in a symbol or written to one, where `null` means none. `int` is the smallest built-in type that holds 0 to 999999, and 899 already exceeds a byte. `null` needs no sentinel, so D7's collision with ECI 0 does not arise, and `Fnc1Mode.ApplicationIndicator` is an `int?` for the same reason. Bytes in and out remove D7's second reason ("values nothing can produce or consume"), so D7 is amended, not reversed. It is the one parameter outside the options struct that Phase 1 of the 2.0.0 plan moved settings into, which holds because it is data, not a setting. Names for common numbers can come later as constants without changing the type. Rejected: an options member beside `EciMode`, which puts two ECI members on one struct, each refused by the other kind of call. The enum widened by casts (`(EciMode)899`): ECI 0 collides with `Default`, `Default` has no number, and undefined values would pass again where F24 made them throw. `EciMode.None` alone expresses no number, and `EciMode.FromAssignment` can be written only as a C# 14 static extension member, which callers on older compilers reach through the extension class. `EciMode` as a value type before the freeze reopens D7, breaks `switch` statements and casts on the enum, and still has to split `Default` |
| B3 | What bytes input declares by default | Decided 2026-11-02: no header, which is what a binary payload is expected to carry. An undeclared Byte segment is valid, and every reader returns its bytes in some form. Assignment 899 is the exact declaration, but a reader that does not know it refuses the symbol, as this library's decoder does today. `eciAssignment: 899` writes it. The phone check in Phase 3 only informs what the documentation says about 899 |
| B4 | Names for bytes input | Decided 2026-11-02: `Create` overloads. The 2.0.0 renames made `Create` the one verb, and the parameter type says bytes. `Create([], ecc)` and `Create(default, ecc)` become ambiguous and need a typed argument. `CreateFromBytes` is the alternative if the verb should say it |
| B5 | Options that mean nothing for bytes | Decided 2026-11-02: refused with `ArgumentException`: an `EciMode` other than `Default`, `Utf8Bom`, `AllowKanji` and an FNC1 mode. Ignoring them would repeat P8, and a refusal can become acceptance later without a break. Kanji mode for Shift_JIS bytes is valid under the standard and waits for a request. Byte mode already writes those bytes exactly, CP932 ones included, so Kanji mode would add density only: 13 bits for a double-byte character instead of 16, and 1,817 characters at 40-L instead of 1,476. Japanese text JIS X 0208 holds already gets Kanji mode through the text call. Some phone scanners read no Kanji mode (2.0.0 plan, D5). On bytes, `AllowKanji` would also have to mean "these bytes are Shift_JIS", which the encoder cannot check. Revisit when someone asks for denser Shift_JIS bytes, or together with the text call's all-or-nothing rule, under which one character without a Kanji cell (one of the seven CP932 reads differently, or an extension character such as ㈱) keeps the whole text in UTF-8 |
| B6 | Structured Append from bytes | Decided 2026-11-02: yes, for Standard QR. Splitting a file is a main use of a set, the parity is defined over bytes, and the bytes calls join a set exactly (P6) |
| B7 | Bytes of a stream under two assignments | Decided 2026-11-02: refused as `UnsupportedContent`, with the first assignment on the info. Bytes in two charsets joined with no boundary cannot be interpreted, and such streams are rare. A call that returns segments with their assignments is the alternative, on request |
| B8 | Text of a Kanji segment under an ECI | Decided 2026-11-02: JIS X 0208, as today, recorded as the second deviation. Under ECI 26 the literal reading turns the pairs into U+FFFD where the writer meant Japanese: a lead byte from 0x81 to 0x9F, which holds the kana and most kanji, never starts a UTF-8 sequence. The bytes calls give the literal pairs and the assignment |
| B9 | More charsets for text | Decided 2026-11-02: none. Built-in tables for the single-byte charsets wait for a request. `Encoding.GetEncoding` is out, because the same symbol would read differently depending on whether the application registered a provider. The bytes and the assignment let a caller use any charset |
| B10 | Instances without readable format information | Decided 2026-11-02: the three blank constructors become internal, and the raw-data constructors throw `InvalidDataException` unless the format information is exactly a valid pattern, so the new properties are plain values. Exact, not within the decoder's correction distance, because a generated code always carries the exact pattern and a blank Micro QR matrix is within that distance of one. Nullable properties would keep the constructors but make every caller unwrap a value a generated code always has. `out` info overloads on `Create` need neither change but add three overloads per symbology |
| B11 | Builders | Decided 2026-11-02: throw, as in Design. Making Micro QR and rMQR ignore the calls instead would hide the same mistake in three places |

## Phases

Each phase follows the test-first workflow, regenerates both `PublicAPI.approved.txt` files, updates the affected specs and `docs/migration.md` in the same change, and appends a Progress log entry. Text encode and decode keep their output, speed and allocation, checked against goldens and by an interleaved before-and-after measurement in Release.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 0 | Premises and rulings | Pin in `standardqr-spec-map.md` and `rmqr-spec-map.md` the sections for the designator, the default interpretation, what an ECI governs, the ECI protocol and assignment 899. B1 to B11 were ruled on 2026-11-02. No change to `src` | Every statement in the ECI policy has a section number |
| 1 | Builders and the encoder's choices | B11 and B10, then `EccLevel` and `MaskPattern` on the three data types | The properties equal what decoding reports over every version, level and mask, with and without boost, and after a raw-data round trip. `Create` allocation and time unchanged |
| 2 | Bytes out | One internal bytes entry over the three bitstream decoders, the assignment on the info, the unknown assignment reported, the `TryDecodeBytes` and `TryDecodeImageBytes` calls and the SkiaSharp overloads | Bytes are right for every mode, under FNC1, behind a byte order mark and across Structured Append parts. The interop oracle's raw bytes agree on the fixture corpus. Text results unchanged on the whole corpus |
| 3 | ECI writer and bytes in, Standard QR and rMQR | A designator of one to three codewords, the bytes `Create` and sizing calls with their `eciAssignment` parameter, and Structured Append from bytes. Then the Android and iPhone cameras scan undeclared binary and ECI 899 symbols from this phase, and what each shows is recorded for the Phase 5 docs. The check waits for this phase because nothing here could write either symbol before it | All 256 byte values and random payloads round-trip through Phase 2 and through the oracle. Under single-segment encoding the largest payload at each version and level equals the standard's Byte-mode capacity. The designator grows at 127/128 and 16383/16384, and 1,000,000 is refused. Text output byte-identical. The phone results are recorded |
| 4 | Bytes in, Micro QR | The same without ECI | As Phase 3, without the ECI rows |
| 5 | Docs, Playground, fold | The encoder and decoder specs (the not-implemented and unsupported rows, the ECI policy and both deviations in Decisions), D7 amended in `qrcode-symbologies.md`, README, migration notes for Phase 1, the API page, and in the Playground a hex view of decoded bytes with the assignment and a hex or file input. Fold this plan into the specs and delete it | The specs describe what was built, and this file is gone |

Phase 1 is independent of the rest. Phase 2 comes before Phase 3 so that bytes input is tested against this library's own bytes reader as well as the oracle.

## Progress log

Nothing yet.
