# Structured Append for Standard QR

## Purpose

Phase 5 of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md). Today the Standard QR decoder rejects a Structured Append symbol as `UnsupportedContent` the moment it sees the mode indicator, and the generator has no way to produce one. A caller whose data does not fit in one symbol of the size they can print has no path through the library in either direction. This plan fixes WHAT ships, in WHICH order, and WHY each rule is what it is. HOW each piece is verified follows the mandatory test-first workflow. When it completes, its durable content graduates into [standardqr-decoder.md](../specs/standardqr-decoder.md), [standardqr-encoder.md](../specs/standardqr-encoder.md) and the shared contract in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the 2.0.0 plan's Phase 5 row gets its Progress log entry, and this file is deleted.

## Scope

| In | Out |
|---|---|
| Decode: read the header, report it on `QRCodeDecodeInfo`, return the symbol's own text | Micro QR and rMQR: neither symbology defines Structured Append; both keep `UnsupportedContent` and their `*DecodeInfo` types gain nothing |
| Encode: one entry point that splits text across the fewest symbols the version cap allows, balanced so every symbol has the same version | A combine helper. Reassembly policy (missing symbols, duplicates, a parity mismatch) is the caller's; a helper that decides it cannot be removed later, and the four rules below are what a caller needs |
| Committed fixtures and oracle cross-checks in both directions | An explicit symbol-count overload ("always four symbols"). Additive in a minor if a request arrives; the version cap covers the case that motivates the feature |
| Spec, README and migration-guide updates in the same changes | A sizing API for "how many symbols would this take". Same reason; `TryGetRequiredBufferSize` stays single-symbol |
| | Playground and BlazorWasm sample. Untouched, as Phase 4 left them |

## The wire format and what it fixes

ISO/IEC 18004 places a 20-bit header in the bit stream: mode indicator `0011`, a 4-bit symbol position (0-based), a 4-bit total count minus one (so 1 to 16 symbols), and an 8-bit parity byte. Three consequences shape everything below.

**Every symbol decodes on its own.** The header is followed by ordinary mode segments, and each symbol carries its own segment plan, its own ECI header and its own terminator. So a split must fall on a character boundary, never inside a surrogate pair, or both halves decode to garbage; and the per-symbol capacity is the data capacity of the version minus the 20-bit header minus the ECI header the symbol repeats, so a set of N symbols holds less than N times one symbol.

**The parity byte identifies the set, not the content.** It is the XOR of the bytes of the whole input taken once, before splitting, in the one charset the set is written in, and every symbol carries the same value. A reader uses it to tell that symbols from two different sets have been mixed; it cannot verify the reassembled content with it, because it does not know the charset the encoder chose. That is why the charset is decided once for the whole text and repeated as an ECI header in every symbol: it is the only way "the bytes of the whole input" names one byte sequence.

**The header is a prefix, and a reader should not care where it is.** The encoder writes it first, before any ECI or data segment, so a decoder that stops at the first data segment can still find it. The decoder accepts it anywhere in the stream, because rejecting a symbol whose header sits after its ECI would refuse a symbol every other reader accepts, and the rejection buys nothing.

## Design

### Decode

`QRStructuredAppend` is a library-built `readonly record struct` in the core, non-positional with an internal constructor and get-only properties, the Phase 3 shape, and Standard QR-only by the naming rule (short `QR` prefix; it is not "a code").

| Member | Meaning |
|---|---|
| `int Index` | Position in the set, 0-based, as on the wire. `Index < Count` is the invariant a caller can rely on |
| `int Count` | Symbols in the set, 1 to 16 |
| `byte Parity` | The parity byte as read |
| `bool IsEmpty` | True when the symbol carried no header. `default` is empty, because a real header has `Count >= 1` |

`QRCodeDecodeInfo` gains `QRStructuredAppend StructuredAppend`, attached through an internal `WithStructuredAppend` the way `Corners` is attached through `WithCorners`, so the member order stays `Status, Version, EccLevel, MaskPattern, ErrorsCorrected, Corners, StructuredAppend`. All six `TryDecode` / `TryDecodeImage` overloads and the `SKBitmap` extension populate it; the matrix decoders read it from the bit stream, which is the one place the header exists.

Rules the bit-stream decoder enforces:

- The 20 bits are read and the segment loop continues. The text returned is this symbol's own.
- `Index >= Count` is `InvalidBitstream`. The two nibbles cannot encode a position outside the set; a symbol that does is malformed in the same class as a character count that overruns the stream, and the library refuses that class everywhere.
- A second header in one stream is `InvalidBitstream`.
- Fewer than 16 bits after the mode indicator is `InvalidBitstream`, as any truncated segment is.
- A header claiming a set of one is accepted and reported as `Index 0, Count 1`. Nothing in the standard forbids it, and the encoder below never writes it.

Reassembly rules for the caller, documented on the type and in the migration guide, and the whole of what a combine helper would have encoded:

1. All symbols report the same `Count` and the same `Parity`.
2. The indices `0 .. Count-1` are each present exactly once.
3. Text is concatenated in `Index` order.
4. `Parity` is a set identity. It is not a checksum of the concatenation, and a caller should not try to recompute it.

### Encode

```csharp
public static QRCodeData[] CreateStructuredAppend(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default)
```

One method on `QRCodeGenerator`, beside `Create`. It takes the same options record, and the split is driven by `options.Version`, which already exists: "fewest symbols" is meaningless without a cap (version 40 holds 2,953 bytes, so the uncapped answer is always one symbol), and the cap is what a caller actually has, in the form "the largest symbol my label or my camera distance allows". `QRVersionRange.AtMost(n)` is that cap. No new parameter is added.

| Situation | Result |
|---|---|
| The text fits in one symbol within `options.Version` | One element, byte-identical to `Create(text, eccLevel, options)`. No header: a set of one costs 20 bits and tells a reader nothing |
| It needs 2 to 16 symbols | That many, all at one version, balanced, each carrying the header |
| It needs more than 16 at `Version.Max` | `ArgumentException` on `options`, as `Create` throws when the text does not fit |

**Balanced, not greedy.** Filling each symbol to capacity and letting the last one take the remainder gives a set of three version-10 symbols and one version-2 symbol; on the same label that reads as a defect. The split minimises the fill of the fullest symbol, so all symbols come out at one version, in three steps that each reuse the segment planner rather than adding a second capacity model:

1. Count the fewest symbols at `Version.Max`. Each symbol's payload capacity is the data capacity at that version and ECC level, less 20 bits, less the ECI header when the set carries one. Chunks are taken greedily at character boundaries, the boundary found by binary search on "does this prefix plan into the capacity", which is monotone in the prefix length. One symbol collapses to `Create`; more than sixteen throws.
2. Take the smallest version in `[Version.Min, Version.Max]` whose capacity still fits that count. Every symbol uses it.
3. Binary-search the smallest per-symbol capacity, at that version, that still fits the count, and split at that capacity. That is the balanced split.

Every chunk is planned by the existing segmenter at the actual version, so the count-indicator band is exact rather than approximated. Two symbols is the smallest set, so there is no version-1 edge case: a chunk that does not fit at all under the cap is the throw in step 1.

**Rules the encoder holds, and why:**

| Rule | Reason |
|---|---|
| The charset is decided once, from the whole text, by the existing analysis; every chunk is encoded with that charset forced, and the ECI header is written in every symbol | "The bytes of the whole input" must name one byte sequence. A pure-ASCII chunk of a UTF-8 set still carries the UTF-8 ECI |
| Parity is the XOR of that one byte sequence, computed from the whole text before splitting | Any per-chunk computation can diverge from it when a chunk would have chosen a different charset on its own |
| Header order is Structured Append, then ECI, then the segments | The encoder writes the ECI header first today; the Structured Append header goes in front of it |
| `Utf8Bom` is written in symbol 0 only and counted in the parity | It is a prefix of the data, as in the single-symbol path; repeating it would insert a BOM mid-text on reassembly |
| `BoostEccLevel` boosts the whole set to the level every symbol can take, or not at all | Boosting per symbol produces a set with mixed ECC levels, and a set that should look identical has no reason to differ there |
| `MaskPattern` applies to every symbol; `Segmentation` and `QuietZoneSize` apply per symbol | Same option, same meaning |
| Splits never fall inside a surrogate pair | The split operates on `char`s, so this is the only boundary hazard; UTF-8 byte boundaries follow from it |

## Decisions

| # | Decision | Outcome |
|---|---|---|
| S1 | Index base in the public type | 0-based, as on the wire. `Index < Count` holds directly; a 1-based `Index <= Count` is where off-by-one errors come from |
| S2 | Header position on decode | Accepted anywhere in the stream. Written first on encode |
| S3 | A set of one | Never written; accepted on read |
| S4 | `Index >= Count` on decode | `InvalidBitstream` |
| S5 | ECC boost across a set | Uniform |
| S6 | Symbol count driven by | `options.Version` (existing), not a new parameter. An explicit-count overload is additive later |
| S7 | Combine helper | Not shipped; rules documented |
| S8 | Interaction with Phase 6 (Kanji encode) | Parity is over the bytes the set is written in, so when Kanji mode lands the byte representation of Japanese text changes and so does its parity. Within one set the value stays consistent, so nothing breaks across readers, but any fixture that pins a parity byte for Japanese input moves. Fixtures in this plan are ASCII and Latin-1 / UTF-8 non-Japanese only; Phase 6 adds the Japanese ones |

## Phases

Each phase follows the test-first workflow, regenerates `src/FeatherQR/PublicAPI.approved.txt`, updates `docs/migration.md` and the affected specs in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. No phase moves a hot path; each states so.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 5a | **P0** | Decode | `QRStructuredAppend`; `QRCodeDecodeInfo.StructuredAppend` and `WithStructuredAppend`; the bit-stream decoder reads the header and enforces the four rules; all decode overloads populate it; `TypeShapeTest` additions; the decoder spec's scope row flips; migration guide gains an additive section | A committed third-party four-symbol set (provenance recorded beside the files) decodes to four texts with `Index` 0 to 3, `Count` 4 and one shared `Parity`; hand-assembled bit streams cover the header anywhere, `Index >= Count`, a duplicate header, truncation, and a set of one; Micro QR and rMQR still report `UnsupportedContent` |
| 5b | **P1** | Encode | Header writer and parity; the three-step balanced split on the existing planner; `CreateStructuredAppend`; the option rules table above; the encoder spec's scope row flips | Round trip through 5a: for texts at every length up to sixteen symbols' worth, every symbol decodes, concatenation in index order equals the input, all symbols share one version and one parity, and the fullest symbol is no fuller than the balanced bound; one-symbol result byte-identical to `Create`; more than sixteen throws; each option rule has a test that fails when the rule is dropped |
| 5c | **P1** | Interop | Sets produced by 5b read by the pinned reader oracles in `tools/QRInteropFixtures` and by the pinned encoder oracles' own decoders where they have one; sets produced by the pinned encoder oracles read by 5a; committed fixtures; spot-check list for the Phase 8 release checklist | Every reader agrees with 5a on `Index`, `Count` and `Parity` for every generated set; the parity byte matches what the encoder oracles compute for the same input, byte for byte |
| 5d | P2 | Fold | Progress log entry on the 2.0.0 plan's Phase 5 row; README feature line; this plan folded into the three specs and deleted | The specs carry the rules and the decisions; nothing here is only here |

5a ships first because it is the half that can be verified against symbols this library did not make, and it is what 5b's round trip is measured through. 5b and 5c are one PR each. 5d is small and closes the plan.

## Verification notes

- Bit-stream tests drive `QRBinaryDecoder.DecodeBitStream` directly (internals are visible to the test project), so the header rules are pinned without any image or encoder in the loop.
- The balanced bound is a stated inequality, not "looks even": with `n` symbols and payload `P` bits, the fullest symbol holds at most the smallest capacity `c` for which `n` chunks suffice, and the test asserts exactly that.
- Mutation checks on 5b: drop the ECI repeat in symbols after the first, compute parity per chunk instead of once, split inside a surrogate pair, boost per symbol. Each must fail a test.
- Parity fixtures pin the byte for ASCII, Latin-1 and UTF-8 inputs, and the 5c cross-check compares it with the encoder oracles on the same inputs, which is what proves the definition rather than the implementation.

## Progress log

Entries are appended per phase: what was done, what was learned, and an explicit statement that no hot path moved.

### Phase 5a, decode (2026-09-15)

**Done.** `QRStructuredAppend` (`Index` 0-based, `Count`, `Parity`, `IsEmpty`; a library-built `readonly record struct`, the Phase 3 shape) and `QRCodeDecodeInfo.StructuredAppend`, carried through `WithCorners` so all seven decode entry points report it without the image decoders knowing. The bit-stream decoder reads the 20-bit header wherever it sits and refuses a position past the count, a second header and a truncated one as `InvalidBitstream`; the header is attached on `Success` only, like `Corners`. Micro QR and rMQR are untouched. Core listing 35 to 36 exported types, one member added, nothing else changed. The corpus came first: 95 symbols in 14 sets from two pinned in-process encoder lineages (a balancing one and an explicit-parts one) plus 4 third-party captures, every symbol passed through the zxing-cpp gate, which supplies per-symbol text and the header values and checks the parity against the bytes the set carries. `StructuredAppendFixtureTest` (matrix and image path per symbol, the four reassembly rules per set), `StructuredAppendDecodeTest` (nine hand-assembled streams: header first, after a segment, after an ECI, a set of one, the last of sixteen, no header, position past the count, a second header, a truncated header), `TypeShapeTest` extended, and `UnsupportedModes` loses mode 3. The decoder spec's scope row, the shared spec's two sentences and the migration guide follow. Full suite green on both target frameworks (9,713 tests on net10.0).

**The parity gate has to XOR the bytes the symbols carry, not the bytes a charset guess would produce.** The first gate assumed ISO-8859-1 when the text fitted it and UTF-8 otherwise, and it was wrong twice in one run: the explicit-parts lineage writes Latin-1 text as UTF-8 with an ECI, and Japanese text in Kanji mode with no ECI, both legitimate and both a different parity from the balancing lineage's for the same text (139 against 8; 6 against 176). The reader exposes the raw segment bytes, so the definition itself is checkable, and the corpus now holds those four symbols as its most valuable cases: a decoder has to report what is on the wire.

**A corpus that cannot fail is not a gate.** An even repetition of any text XORs to 0 in every charset, and an even number of accented characters in one 0xC0 block makes the ISO-8859-1 and UTF-8 parities coincide, so three of the seven first-draft sets would have passed a gate computing the parity over the wrong bytes. Every repetition count is odd now and the Latin-1 sentence carries seven accented characters per repetition.

**The gate caught an oracle.** The pinned balancing encoder at 3.1.0 wrote parity 0 on every set, the defect its 3.2.0 fixed; the pin is 3.2.1 now, and the fixture spec's oracle matrix says not to go below it.

**The corpus found a defect in a different component.** One symbol of ninety-nine, `codeglyphx/sixteen-symbols-max-v2-l-2of16`, reads through the matrix path and not from its clean 8 px render: the finder locator picks a false candidate three modules inside the real top-right finder, at 5 px/module and up. Not this phase's problem and not touched here; recorded as F10 in the 2.0.0 plan with the measurements, and the fixture test lists it as a known image-path defect behind a guard that fails when the locator is fixed.

**Benchmark delta.** Not measured: the decode path gains one `switch` case that runs only when the mode indicator is `0011`, and no hot path moved.
