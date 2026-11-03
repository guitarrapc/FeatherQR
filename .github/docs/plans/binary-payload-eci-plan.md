# Binary payloads, ECI and the encoder's choices

## Purpose

FeatherQR writes and reads text only. A payload that is not text cannot be written as it is, and a symbol whose bytes are not in one of three charsets is read as converted text or not at all. This plan adds bytes in and bytes out to all three symbologies, sets one ECI policy against ISO/IEC 18004 and ISO/IEC 23941, reports the error correction level and mask the encoder chose, and makes the builders refuse encoding settings they cannot apply.

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

The standard models every mode as producing bytes. Numeric and Alphanumeric give ASCII, Byte gives its bytes, and Kanji gives the Shift_JIS byte pairs it compacts. An ECI says how the bytes after it are interpreted, and with none the interpretation is ISO-8859-1. The reader passes the assignment on with the bytes (the ECI protocol) rather than having to know every charset. The subclauses that state these are not public, so Phase 0 checked each statement against the reference implementations instead (Progress log).

This plan adopts three rules.

1. Writing is always valid and declares only what is true. Text gets the charset it was converted into, as today. Bytes get the assignment the caller names, or no header.
2. Reading bytes is literal. Every mode yields the bytes the standard defines, no charset is applied, and the assignment is reported. An assignment this library has no charset for is not an error.
3. Reading text follows the standard except where the literal reading cannot give the text the writer meant, and each such deviation is named in the decoder's Decisions. There are two. A Byte segment with no ECI is read by its bytes, which is already recorded. A Kanji segment under an ECI is read as JIS X 0208 (B8). An assignment with no charset here stays `UnsupportedContent` for text, and is now reported.

| Topic | ISO/IEC 18004 and ISO/IEC 23941 | Today | After this plan |
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

The parts of a Structured Append set, joined in index order, give the bytes the set was made from. `GetMaxDecodedLength` bounds the byte count too, since Numeric is the densest mode in both readings, and the span forms' documentation says so.

### The assignment on the decode info

`QRCodeDecodeInfo` and `RmQRCodeDecodeInfo` gain `int? EciAssignment`, `null` for none. A bytes read reports the assignment the bytes of its Byte and Kanji segments are under, and the first one declared when no such segment holds a byte (ruled 2026-11-03: the first may cover only digits and letters while the bytes sit under a later one). The text calls report the first assignment the stream declares, on success and on the two reads whose content is no text, `UnsupportedContent` and `UnmappedCharacter`, so a caller that gets either with an assignment knows it can read the bytes and decode them with its own `Encoding`. Results of the several-symbol calls carry it through their `Info`. `MicroQRCodeDecodeInfo` gets nothing.

A stream whose Byte and Kanji segments hold bytes under two different assignments, no ECI counting as one, has no single interpretation for its bytes. The bytes calls refuse it (B7). The text calls read it as today when every assignment has a charset here.

### ECC level and mask on the encode result

`QRCodeData` and `MicroQRCodeData` gain `EccLevel` and `MaskPattern`, and `RmQRCodeData` gains `EccLevel`. They are read from the symbol's own format information when asked. Nothing is added to `Create`, so it allocates and runs as before. The values survive `GetRawData` and a restore, because the format information is part of the matrix. The span `Create` writes a module matrix, not a `QRCodeData`, so it gains nothing here: decoding that matrix reports the level and mask, as it did before.

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
| 0 | Premises and rulings | Done 2026-11-02. The subclauses are not public, so each statement of the ECI policy was checked against the reference implementations, and the section numbers wait until the text is at hand. B1 to B11 were ruled the same day. No change to `src` | Each statement of the ECI policy is marked as one the reference implementations agree on or one they split on |
| 1 | Builders and the encoder's choices | Done 2026-11-02 (Progress log). B11 and B10, then `EccLevel` and `MaskPattern` on the three data types | The properties equal what decoding reports over every version, level and mask, with and without boost, and after a raw-data round trip. `Create` allocation and time unchanged |
| 2 | Bytes out | Done 2026-11-02 (Progress log). One internal bytes entry over the three bitstream decoders, the assignment on the info, the unknown assignment reported, the `TryDecodeBytes` and `TryDecodeImageBytes` calls and the SkiaSharp overloads | Bytes are right for every mode, under FNC1, behind a byte order mark and across Structured Append parts. The interop oracle's raw bytes agree on the fixture corpus. Text results unchanged on the whole corpus |
| 3 | ECI writer and bytes in, Standard QR and rMQR | Done 2026-11-02, the phone check closed on the iPhone 2026-11-03 (Progress log). A designator of one to three codewords, the bytes `Create` and sizing calls with their `eciAssignment` parameter, and Structured Append from bytes. Then the Android and iPhone cameras scan undeclared binary and ECI 899 symbols from this phase, and what each shows is recorded for the Phase 5 docs. The check waits for this phase because nothing here could write either symbol before it | All 256 byte values and random payloads round-trip through Phase 2 and through the oracle. Under single-segment encoding the largest payload at each version and level equals the standard's Byte-mode capacity. The designator grows at 127/128 and 16383/16384, and 1,000,000 is refused. Text output byte-identical. The phone results are recorded |
| 4 | Bytes in, Micro QR | Done 2026-11-03 (Progress log). The same without ECI | As Phase 3, without the ECI rows |
| 5 | Docs, Playground, fold | The encoder and decoder specs (the not-implemented and unsupported rows, the ECI policy and both deviations in Decisions), D7 amended in `qrcode-symbologies.md`, README, migration notes for Phase 1, the API page, and in the Playground a hex view of decoded bytes with the assignment and a hex or file input. Fold this plan into the specs and delete it | The specs describe what was built, and this file is gone |

Phase 1 is independent of the rest. Phase 2 comes before Phase 3 so that bytes input is tested against this library's own bytes reader as well as the oracle.

## Progress log

### Phase 0, premises (2026-11-02)

Done:

- B1 to B11 were ruled, all as recommended. B2 and B5 were refined in discussion first (the assignment as a parameter of the bytes calls, Kanji mode for bytes left for a request).
- ISO/IEC 18004:2015 is withdrawn. The fourth edition, ISO/IEC 18004:2024, replaced it in August 2024. Its Foreword lists two main changes: fixed pattern damage is graded continuously under ISO/IEC 15415, and the reference decoding algorithm is clarified. Neither touches data encodation. The specs still cite the 2015 subclause numbers.
- The public parts of both standards pin clauses, not subclauses. In ISO/IEC 18004:2024 they are clause 7 (Requirements), clause 14 (Transmitted data), Annex F (Symbology identifier), Annex H (JIS8 and Shift JIS character sets) and ISO/IEC 15424 as a normative reference. Definition 3.15 defines a segment as data under one ECI or one encoding mode. The public sample of ISO/IEC 23941 is its DIS text: clause 7 (Requirements), clause 13 (Transmitted data), Annex E (Symbology identifier), Annex G (ISO/IEC 8859-1 character sets), Annex H (JIS8 and Shift JIS character sets), ISO/IEC 8859-1 as a normative reference, and the same definition of a segment as 4.11.
- The subclauses for the designator lengths, the default interpretation, what an ECI governs and the ECI protocol are not public, and neither is AIM's ECI register (ECI Part 3), which lists 899. They were not pinned.
- Instead each statement was checked against the reference implementations kept for interop work, four readers and three writers:
  - Designator: all agree. One to three codewords marked by the leading bits 0, 10 and 110 carry 0 to 127, 128 to 16383 and 16384 to 999999, and a larger number is refused. This library's reader agrees, and Phase 3's writer follows it.
  - No ECI: they split. One reads ISO-8859-1. Two guess from the bytes. One guesses only when the symbol declares no ECI anywhere, and otherwise reads the data before the first ECI as ISO-8859-1. This library guesses, a recorded deviation. The last rule is closer to the standard and is a lead outside this plan.
  - What an ECI governs: they split. One applies it to the bytes of every mode, Kanji included. Three read Kanji as JIS X 0208 whatever the ECI, as this library does (B8).
  - An unknown assignment: they split. Two fail the symbol, one ignores the ECI and keeps its charset, and one reads assignments up to 899 as binary and flags those above. The implementations that name 899 call it binary data, as secondary sources do. One writer declares it over bytes input by default, and the others write no header (B3).
  - Transmission: two of the readers report a symbology identifier and agree on its modifier, 1 without an ECI and 2 with one (3 to 6 with FNC1). One of them also offers the ECI protocol, each assignment as a backslash and six digits and a literal backslash doubled, as a separate output. This library reports neither (D10).

Lessons:

- A subclause behind a purchase cannot be an exit criterion. Behaviour shared by independent implementations is the evidence at hand, and where they split it shows which choice is a deviation and which side the field takes.
- The edition a spec cites can be withdrawn without anything in the repository noticing. It was found only by opening the standard's catalogue page.

No change to `src`, so no benchmark delta.

### Phase 1, builders and the encoder's choices (2026-11-02)

Done:

- `QRCodeData` and `MicroQRCodeData` have `EccLevel` and `MaskPattern`, and `RmQRCodeData` has `EccLevel`, read from the format information when asked. Standard QR reads both copies where the placer writes them and matches them exactly against the 32 words. Micro QR reads through the matrix decoder's own format reader, over a view of the packed core, and accepts only distance 0 for its own version. rMQR reads both copies where `RmQRConstants.GetFormatModule` places them and matches the two words its version has.
- The three empty-matrix constructors are internal. The three restore constructors throw `InvalidDataException` unless the format information is exact.
- `QRCodeImageBuilder.WithErrorCorrection` and `WithEciMode` throw `InvalidOperationException` on a builder made from a `QRCodeData`. The three builders' docs name the data's `EccLevel` (and `MaskPattern`) as where the level and mask are.
- Tests: `SymbolDataFormatInformationTest`, 24 cases. They cover the properties against what decoding reads over every level and mask at versions 1, 7 and 40, every Micro QR symbol and mask, rMQR at three versions and both levels, boost and serialization, and the refusals: an empty matrix, one format module off where the decoder corrects it, copies that disagree, and another version's word. Two builder cases. The pattern round trips in the three data test classes stamp a format word before they serialize.
- The approved listing lost three constructors and gained five properties. The SkiaSharp listing is unchanged. The Playground's API page was regenerated, which also brought in doc changes earlier commits had left out of it.
- The design's claim that the span `Create`'s bytes restore into a `QRCodeData` was wrong. They are a module matrix. Corrected above.
- Suites: Debug 61,270 on net8.0 and net10.0, 804 skipped. Release 61,636, 802 skipped. None failed.

Lessons:

- An all-light Micro QR format area is three bits from M4-L with mask 1, inside the decoder's correction distance. A reader with the decoder's tolerance gives a matrix nobody wrote a level and a mask. Computing the distances before ruling B10 is what found it.
- A claim about an output's shape needs the output looked at. The span `Create`'s buffer was assumed to be the serialized form while the design was written.

No generator file changed, so `Create` was not benchmarked.

The readers were rewritten later the same day, after the maintainer called them inefficient. The first version computed the 32 Standard QR words one by one until one matched, went through the decoder's 32 KB table of every Micro QR word within three bits, and called `GetFormatModule` for each rMQR bit. The level and mask are the word's top five bits under the XOR mask, so the one word they name is the only candidate, and a single table load confirms it. The runs along a row are read in one window (`ModuleBitPacker.ReadRun`). The Micro QR words moved to `MicroQRConstants`, so reading a level no longer builds the 32 KB table. They are a literal table there, as the tables beside them are, so nothing is built at run time, and the test over every 15-bit word holds them to `GetFormatBits` (a planted wrong byte failed it). Measured in Release through the public API, before and after:

| Operation | Before | After |
|---|---|---|
| Version 40, `EccLevel` then `MaskPattern` (level H, mask 7) | 328 ns | 27 ns |
| Version 40 restore | 436 ns | 268 ns (the rest is the array) |
| Version 1 restore | 184 ns | 40 ns |
| Micro QR M4, both properties | 28 ns | 17 ns |
| Micro QR M4 restore | 42 ns | 31 ns |
| rMQR R17x139, `EccLevel` | 56 ns | 27 ns |
| rMQR R17x139 restore | 105 ns | 75 ns |

Allocation is unchanged. New tests hold the exact match to a search over all 2^15 words for Standard QR and Micro QR and `ReadRun` to a read per module, and refuse a change to each format module one at a time. A planted skip of one module failed them. Debug suite 61,290, none failed.

Lesson: an exact match is not a search. The data bits of a format word sit under its XOR mask, so the candidate is computed, not found. The first version had carried over the shape of a decoder that corrects errors, which has to try every word.

### Phase 2, bytes out (2026-11-02)

Done:

- `TryDecodeBytes` (code, matrix and matrix into a span) and `TryDecodeImageBytes` (array and span) on the three decoders, `TryDecodeBytes(SKBitmap)` on the three SkiaSharp image decoders, and `EciAssignment` (`int?`) on `QRCodeDecodeInfo` and `RmQRCodeDecodeInfo`. The approved listings gained exactly these, and the API page was regenerated.
- The three bitstream decoders take a `bytes` flag. Under it a Byte segment is copied as written, Kanji gives its Shift_JIS pairs, every assignment reads, and Byte and Kanji segments under two assignments are `UnsupportedContent`. Numeric and Alphanumeric need no change, since their characters are their bytes.
- The bytes travel through the char destination the text calls already use, one byte a char, and are narrowed at the end. A thread-static flag (`PayloadOutput`) the bytes calls set around the text call tells the matrix decoders, which read it once after Reed-Solomon. The image search passes its destination through dozens of stage signatures, and only that last step differs between the outputs.
- The assignment is reported on `UnmappedCharacter` too, not only on `UnsupportedContent`: both are reads whose content is no text, and the bytes calls read both.
- `GetMaxDecodedLength` bounds the bytes, as the design expected. Numeric gives a byte a digit and is the densest in both readings. A test fills versions 1 and 40 at level L with digits and with kanji and reads them into a destination of that length.
- Tests: `BitStreamBytesTest` (every mode in all three decoders, assignments of every designator length, both Kanji lead-byte ranges, streams under two assignments, destination and truncation order) and a bytes test per symbology (matrix, image and bitmap paths, `EciAssignment` on both outputs, the Structured Append parts joined). `TypeShapeTest` now pins `EciAssignment` on the two info types that have ECI. Twenty-four existing calls took the new `out` argument.
- Planted faults: a one-assignment check that always passes failed four tests. A Kanji pair computed from 0x81 for every lead failed none at first, because every Kanji test used a lead below 0xA0. The test over both ranges was added, and it fails two cases.
- Interop: all 343 committed matrix fixtures (Standard QR, FNC1, Structured Append, Micro QR, rMQR) read as bytes equal to the interop oracle's raw bytes. 337 are identical. The 6 in FNC1 second position match once the oracle's leading application indicator is set aside, which this library reports on `Fnc1` instead.
- Text decode speed: the 22 span-form decodes (21 of matrices, one of an image), run twice each against the commit before, alternated, with 10 iterations: mean ratios 0.87 to 1.06, and the rows above 1.0 overlap the baseline from run to run. No allocation on either side.
- Suites: Debug 61,414 on net8.0 and net10.0, 804 skipped. Release 61,780, 802 skipped. None failed.

Lessons:

- A test of a two-range mapping needs a case in each range. The first Kanji tests used 0x82 and 0x87 leads, so the arithmetic for 0xE0 to 0xEB went unseen until a fault was planted there.
- An output that differs only in the last step of a deep search is cheaper to select there than to pass down: a flag the outer call sets reached the one branch that needed it, where a parameter would have touched every stage.
- A benchmark comparison must not switch the checkout under a script. A stopped script went on to stash the source mid-run. The baseline now comes from the commit extracted into a separate folder.

### Phase 3, ECI writer and bytes in (2026-11-02)

Done:

- `Create`, the span `Create` and `TryGetRequiredBufferSize` over `ReadOnlySpan<byte>`, with `int? eciAssignment = null` after the options, on `QRCodeGenerator` and `RmQRCodeGenerator`, and `CreateStructuredAppend` over bytes on `QRCodeGenerator`. The approved listing gained exactly these seven, the SkiaSharp listing is unchanged, and the API page was regenerated.
- The designator takes one, two or three codewords (`EciModeExtensions.Designator` and `DesignatorBits`) in both stream writers and in every price of the header: the single-mode fit, both planners, the set planner and rMQR's selector. rMQR's automatic fit reads tables built for an 11-bit prefix, so bytes under an assignment from 128 walk the strategy's order and price each version, as FNC1 does.
- Bytes take the text pipeline widened one to a char, on the stack up to 256 bytes and rented above (rMQR always on the stack), and are analyzed for their mode as text is, under a charset of their own: `Default` for none, or an `EciMode` value no caller can pass that carries the assignment below a flag bit. The writers' Byte path became "UTF-8 transcodes, anything else narrows". The Optimal and Structured Append routes were split after their analysis, so the bytes calls enter with theirs. A payload longer than any symbol holds (7,089 digits, 361 in rMQR) is refused before it is widened.
- B5's refusals with `ArgumentException`, and an assignment outside 0 to 999999 with `ArgumentOutOfRangeException`, in the text calls' validation order.
- `SizingSurfaceTest` held every `Create` to end with the options and allowed one sizing overload. It now allows one sizing overload per kind of content, and the assignment after the options of a bytes overload, which B2 put there.
- `Create(default, ecc)` and `Create([], ecc)` no longer compile (CS0121, both overloads fit), which a probe compiled against the build confirmed. A `null` literal is ambiguous the same way, and with options any of the three takes the text overload (found in the review of 2026-11-03). Every other call shape resolves as before. The migration notes say so.
- Tests: `QRCodeGeneratorBytesTest` (227 cases a framework) and `RmQRCodeGeneratorBytesTest` (111). They cover every byte value under both segmentations, undeclared and under 899, and seeded payloads of digit, letter and byte runs under assignments of every designator length, with the span form and the sizing held to `Create`. They read the stream bit for bit with no header and with each designator form at 0, 127, 128, 899, 16383, 16384 and 999999, and the one header ahead of an Optimal plan. They pin the Byte capacity of every version and level (rMQR's also under a two-codeword designator) against its published values and the designator's cost at 40-L (2,953, 2,952, 2,951 and 2,950 bytes), 7,089 digits at 40-L, the refusals from every call, ASCII bytes writing the text call's symbol, a read assignment passed back writing the symbol again, the boost and a pinned mask, Structured Append parts joined with the parity and the assignment on each, and empty payloads.
- Planted faults: a header priced at 12 bits whatever its designator failed 7 Standard QR tests. rMQR's table fit for every assignment failed 5 rMQR tests, and assignment 127, which the table prices right, passed.
- Interop: 541 symbols from the bytes calls (every byte value in both symbologies and segmentations, 300 seeded payloads each, a 1,500-byte set under 899). In all 538 the oracle read, its raw bytes are the payload and its ECI-protocol output names the declared assignment, or none. The 3 it did not read are empty payloads: it reads no empty symbol, the text call's included, so those rest on this library's decoder.
- Text output: 1,587 text symbols and 190 refusals hash the same built from the commit before and from this change, the refusals' types and messages included. They are 23 texts (empty, each mode, Latin-1, Japanese, emoji, a mid-text U+FEFF, FNC1 content) under 11 Standard QR option sets at every level, through both `Create` forms and as a set, and under 7 rMQR option sets at both levels.
- Text encode speed: 87 benchmarks (the two end-to-end classes, and the Optimal rows of the segmentation and set benchmarks) against the commit before, alternated twice with 10 iterations of 200 ms: mean ratios 0.84 to 1.17. Each of the three rows at 1.10 or more had one high run of this change, so the two rMQR classes and the largest Standard QR row ran again, the commit before first, with 20 iterations: 0.95 to 1.07, and those three at 0.95, 0.98 and 1.03. Allocation is the same in every row but for one-byte readings.
- Suites: Debug 61,286 on net8.0 and net10.0, 804 skipped. Release 61,654, 802 skipped. None failed.
- Phone check, iPhone (2026-11-03, the maintainer): the camera recognizes the Standard QR symbols as QR codes and shows no content, and a QR reader app on it reports that it cannot read them. The four symbols are one 19-byte payload (the PNG signature, 0x00, and bytes that are no valid UTF-8), undeclared and under 899, in Standard QR and rMQR. The maintainer closed the check on this result without Android, and kept bytes as they are: the library adds no text form such as Base45.

Lessons:

- A value recalled for a test needs its source. 26-L's Byte capacity is 1,367, and the 1,370 first written into the test is that version's data codeword count.
- A set's version is the smallest that holds its count of symbols, so a boost case needs a pinned version to have room for a higher level.
- The oracle's ECI-protocol bytes open with the symbology identifier, `]Q1` without an ECI and `]Q2` with one, before the first designator's backslash.
- A phone's camera and a general reader app treat a QR code's content as text, so a payload that is no text shows as nothing, declared or not. Bytes are for readers an application controls, and that does not change B3. Phase 5's docs say so, and point a payload meant for general readers to text, such as Base45 in Alphanumeric mode, which costs about 3 % over the bytes where Base64 in Byte mode costs 33 %.
- A benchmark filter over classes with parameters multiplies by their shapes. The five encode classes are 304 benchmarks, about six hours for four runs. The end-to-end classes and the Optimal rows, at 200 ms iterations, cover the routes that changed in about 40 minutes.

### Review of the branch (2026-11-03)

Done: the branch against main, reviewed on five lenses, with every claim put to three verifiers who tried to refute it. What survived was fixed, each code fix behind a test that failed first:

- A Structured Append set from bytes under an assignment with `BoostEccLevel` threw `ArgumentOutOfRangeException`. The boost analyzed each chunk again under the internal charset value, which the analyzer's byte count does not know. The chunk is now analyzed under `Default`, as the set's own chunks are.
- A bytes read reported the first assignment, which may cover only digits while the bytes sit under a later one, so the bytes and the number passed back wrote another reading. Ruled with the maintainer: a bytes read that succeeds reports the assignment the bytes of its Byte and Kanji segments are under, and the first declared when no such segment holds a byte. The text calls keep the first. Design amended.
- A designator number past 999999 read as a success in bytes mode, and `Create` refuses that number. Both calls now give `UnsupportedContent`.
- An empty Byte segment counted toward the one-assignment rule in Standard QR but not in rMQR, and an empty Kanji segment in both. An empty segment no longer counts.
- `int? EciAssignment` grew `QRCodeDecodeResult` from 80 to 88 bytes, `RmQRCodeDecodeResult` from 64 to 72 and `SymbolDecodeResult` from 216 to 232, so the text calls that return arrays of results allocated 8 or 16 bytes more a symbol. The two infos store the number plus one in an `int`, which gives back 80, 64 and 216.
- The bytes copy read one byte at a time and the narrowing was a scalar loop, about 3 µs of a 2,900-byte version 40 read by the verifiers' measurements. Both now go through a whole-segment read, and through Latin-1 on .NET 5 and later, where the netstandard builds keep their loops.
- The span bytes calls rented a char buffer as long as the destination. They rent at most what the largest version holds.
- `TryDecodeBytes` over a data type searched the matrix border again, so a restored matrix with a light edge that the text call reads failed. It decodes the core as it stands, as the text call does.
- The bytes set's too-long guard named `data` where the planner names `options`.
- Docs: the bytes calls' remarks on the assignment, the image search and the pass-back, the exceptions of the bytes `Create`, header widths stated as 12 or 11 bits, the "every reader takes" claim the phone check disproved, the migration notes (a `null` literal, the decode additions, Shift_JIS written as bytes), and the README and `qrcode-symbologies.md` lines on encodings.

Tests: the assignment cases above, the boost under 899 and 16384, an Optimal plan and a set at a capacity edge under each designator length, light-edge restores in all three symbologies, a destination of 16 MiB, a text call after bytes calls on one thread, a short destination left as it was, null guards, rMQR's Kanji under two assignments, rMQR's span form with a quiet zone, 361 digits in rMQR, a parity test for the bytes copy, the netstandard builds' widening and narrowing, and an independent .NET library reading the bytes calls' symbols and writing bytes they read. A planted mispricing at the Optimal check or the set budget is caught only by the new edge tests.

Lessons:

- A premise worded "only" needs the grep of every comparison. "The encoders compare a charset only with `Default` and `Utf8`" held where the header and the byte form are decided. The writers' charset checks and rMQR's header price admitted the internal value by name, the analyzer did not, and the one site that analyzed a chunk under the set's charset threw.
- A fixture's name is a premise. `Single = new() { QuietZoneSize = 0 }` left the segmentation at its default, and `Optimal` is 0, so the single-segment tests ran Optimal.
- One assignment is a summary of a stream that may declare several. The first matches what the text calls report, but for bytes the number has to be the one the bytes are under, or the pass-back writes another reading.

### Phase 4, bytes in for Micro QR (2026-11-03)

Done:

- `Create`, the span `Create` and `TryGetRequiredBufferSize` over `ReadOnlySpan<byte>` on `MicroQRCodeGenerator`, with no assignment, since Micro QR has no ECI. The approved listing gained exactly these three, the SkiaSharp listing is unchanged, and the API page was regenerated.
- Bytes take the text pipeline widened one to a char on the stack (35 at most, the digits M4-L holds) and analyzed under `Default`, which every Micro QR writer narrows back to the byte. The text route's rewrite into UTF-8 of text the decoder would read as another text is left out, and the planner's check of Latin-1 runs never applies, since it runs under `Iso8859_1` alone. Past the analysis the bytes take a route of their own through the same version fit, planner and writers, so the text routes are the source they were.
- A payload longer than 35 bytes is refused before it is widened, after the selector's checks of the level and the range, so a bad argument still throws first. The message counts bytes in Byte mode, as Standard QR's and rMQR's do.
- `AllowKanji` is refused with `ArgumentException`. Micro QR's options have no `EciMode`, `Utf8Bom` or FNC1 to refuse.
- Empty content written as `default`, `[]` or `null` is ambiguous (CS0121) on Micro QR with options too, and in the span and sizing forms, because its bytes overloads have no assignment to tell them apart. A probe compiled against the build confirmed it, and every other call shape resolves as before. The migration note says so.
- Tests: `MicroQRCodeGeneratorBytesTest`, 40 cases a framework and one more in Release. They cover every byte value under both segmentations, seeded payloads of digit, letter and byte runs over every level, four version ranges, both quiet zones and pinned masks, with the span form and the sizing held to `Create`, the stream's mode indicator, count and bytes read from the symbol, bytes the text call would write as UTF-8 written as given, the Byte capacity of M3 and M4 at each level against the published values, Byte refused on M1 and M2 where digits fit, 35 digits at M4-L, the refusals and their order, the text call's symbols from one-byte-a-character text, read bytes passed back, and a span form and sizing that allocate nothing.
- Planted faults: the text route's analysis on bytes failed 8 cases, a plan never written failed 5, the too-long refusal without the selector's checks failed 1, and `AllowKanji` accepted failed 2.
- Phase 3's boost tests, completed: a single symbol at 1-L with 12 and 13 bytes, where each designator length moves the level between M and L, and a set at 5-L of 237 and 246 bytes, where the header does. Chunks priced bare for the set's boost failed none of the earlier set cases, which used 220 bytes and take M whatever the header costs, and fail 2 now. The single boost priced bare fails 3.
- Interop: 336 symbols from the bytes calls (every byte value under both segmentations, 300 seeded payloads over every level, both segmentations and pinned masks). In all 319 the oracle read, its raw bytes are the payload. The 17 it did not read are empty payloads, and it does not read the text call's empty symbol either.
- Text output: the encode digest of 4,000 texts through the three generators, 72,928 lines and 4,256 of them Micro QR, is identical built from the commit before and from this change.
- Text encode speed: twelve Micro QR text benchmarks (Optimal data, span and sizing, a pinned version, a pinned single-mode route, and a single-mode control) against the commit before, three rounds a side alternated, with 10 iterations of 200 ms. Median ratios 0.99 to 1.03, and allocation the same in every row. The first five benchmarks of one round of this change read high (170 ns where the other five rounds read 125 to 130), which puts those rows' mean ratios at up to 1.12.
- Suites: Debug 61,586 on net8.0 and net10.0, 804 skipped. Release 61,956, 802 skipped. None failed.

Lessons:

- A route split after its analysis costs the text a call. The first version gave the bytes the text's Optimal and narrowed routes through methods that take the analysis, and Optimal sizing of a short text read 1.5 ns slower (3 %) in all three rounds. The bytes now have their own route, which is also shorter, since bytes never take a Kanji plan.
- A test of a price needs a size where the price changes the answer. The set's boost test checked M at a size every header leaves at M.

### Review of Phase 4 (2026-11-03)

Done: the branch against main, reviewed on five lenses in five rounds, the last with no finding, with every claim put to three verifiers who tried to refute it. What survived was fixed, each code fix behind a test that failed first or a planted fault it now catches:

- A bytes payload longer than any symbol holds was refused with an error built in Byte mode. In Micro QR, 36 digits at `ErrorDetectionOnly`, in M1-M2 or with M2 pinned read as Byte mode not being available there, and at L as "at most 15 bytes" where 35 digits fit. Standard QR named Byte mode for digits and letters, and rMQR also its Byte capacity. The error now names the mode the bytes take (`TextAnalyzer.ModeOfBytes`). In Micro QR it is the text call's error for the same bytes read one to a character, and in Standard QR and rMQR it is for ASCII bytes.
- The first form of that fix passed the payload to the error helpers as a span. Under full optimization on x64 its copy took 16 bytes of frame, zeroed on every call, in four of the six bytes `Create` methods. The helpers take a reference and a length, and the frames are back to what they were, Standard QR's span form 16 bytes smaller. On .NET 8 and later the mode is read with vectorized searches: the error for 1,000,000 digits takes 16 to 30 µs once warm, against 280 to 300 µs with the first form's loop, 2.3 to 5 µs before the fix, and 130 to 175 µs for the text call's own error.
- Tests: Optimal splits beside digits at M3, where five 0xFF bytes leave one bit of M3-L free, and C3A9 and C4D6DC are runs the planner refuses under ISO-8859-1. An analysis left under ISO-8859-1 failed no test before and fails 2 cases now. Byte runs priced as UTF-8 by the version scan failed none and fail 3. `ModeOfBytes` in both its forms against the analysis on every byte value after each mode and on seeded payloads, the too-long errors against the text call's in all three symbologies, and the netstandard builds' loop against this host's through Standard QR's span call, with the deciding byte first or last. `MicroQRCodeGeneratorBytesTest` has 46 cases a framework and one more in Release.
- Docs: comments the bytes route made false (the Micro QR text analysis named as every entry point's, and the planner's Latin-1 refusal stated for every charset), the public remark on `MicroQRSegmentation.Optimal` (a plan read in another charset gives way to the single-mode stream rather than reporting "does not fit", and bytes skip that check), the spec map's bytes row naming both text checks it leaves out and its segment row scoped to text, private summaries reworded, and test summaries stating their premises.

Open, for the maintainer:

- Under `Single` the Micro QR bytes `Create` methods zero the 288-byte plan buffer they never use. Allocating it under `Optimal` alone measured no difference on the span form. The class form was about 3 % faster under tiered compilation in all three measurements, and no faster in the one under full optimization. `Optimal`, the default, was not measured with it.
- `Create(default, ecc, options)` stays ambiguous on Micro QR, as B4 accepted. `OverloadResolutionPriority` on the text overloads would bind the empty shapes to text for callers on C# 13 and later, on every target.

Lessons:

- A fix on an error path can cost the success path through its arguments. A span handed to a throw helper takes a frame slot that the prolog zeroes on every call, and a reference and a length do not.
- Phase 4's lesson on prices held again. The Optimal test of high bytes had 16 bits to spare, so a byte priced as UTF-8 left its answer alone.
- A test of a refusal needs content the refusal applies to. The decoder's guess reads a lone 0xFF run as ISO-8859-1, so the planner's Latin-1 check never refused the old test's run, whatever charset its analysis carried.
