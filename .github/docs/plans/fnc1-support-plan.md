# FNC1: first and second position, Standard QR and rMQR

## Purpose

This plan details Phase 6c of the [2.0.0 plan](featherqr-2.0.0-plan.md) (GS1 / FNC1, D9-D12): reading and writing both FNC1 modes in Standard QR and rMQR. It settles the rMQR indicator question, corrects three premises of D9 and D10, adds what the investigation found about the `%` rule, and orders the work so each step is checked against an outside reader. Decode ships on its own, in 2.0.0. Encode ships in the same release as the GS1 layer (D12, decided 2026-10-10).

The layer above the bit stream (the GS1 AI table, check digits, `(AI)` text, Digital Link) is in [gs1-support-plan.md](gs1-support-plan.md). This plan gives that layer an encoder that takes an element string with U+001D separators and a decoder that returns one with the mode beside it. The research behind the plan (standard clauses, the oracle probe, the code inventory) is in [references/fnc1-support-research.md](references/fnc1-support-research.md).

## What FNC1 is

ISO/IEC 18004 (read in its 2006 edition) defines two modes that change how the whole symbol is read:

- FNC1 in first position (`0101`) marks GS1 data.
- FNC1 in second position (`1001`) marks data in an industry format registered with AIM. An 8-bit application indicator follows: 00-99 as its value, or one letter as its ASCII value plus 100.
- Either appears once, before the first data segment and after any ECI or Structured Append header.
- While a mode is active, a `%` in an Alphanumeric segment is the field separator (GS, 0x1D) and a literal `%` is written `%%`. A Byte segment carries GS as 0x1D. Numeric and Kanji segments are unchanged.
- A reader signals the mode with the symbology identifier (`]Q3` / `]Q4` first, `]Q5` / `]Q6` second, the even ones under the ECI protocol) and transmits the application indicator before the data.
- Micro QR has neither mode. rMQR has both, as `101` and `110` (below).

## Where it stands (2026-10-10, `main` at f4b74de)

- The Standard QR decoder recognises both indicators and returns `UnsupportedContent`.
- The rMQR decoder treats `101` and `110` as reserved (`InvalidBitstream`). [rmqr-decoder.md](../specs/rmqr-decoder.md) and [rmqr-spec-map.md](../specs/rmqr-spec-map.md) say rMQR has no FNC1 indicators, without a source, and a test pins the decoder to that statement.
- The Micro QR decoder refuses M4 indicators 4-7, which is correct.
- No generator writes FNC1, and no option asks for it.
- The real-image corpus holds one GS1 photograph (`qrcode-2/gs1-figure-4.15.1-2.png`), which the sweep counts in the content column in all four rotations. Its expected text is the element string with raw GS and no identifier, the form this plan returns.
- No test or tool asks a writer for FNC1.

## What the investigation found

### rMQR defines both modes

zxing-cpp's and qrcode2's rMQR tables read `101` as first position and `110` as second position. libzint and CodeGlyphX write first position as `101` and do not write second position. Wikipedia's rMQR table, citing ISO/IEC 23941 §7.4.1, agrees. In the probe, zxing-cpp read libzint's and CodeGlyphX's rMQR GS1 symbols as GS1. The rMQR records are wrong. ISO/IEC 23941 itself was not read. The GS1 General Specifications do not list rMQR as a GS1 carrier ([gs1-support-plan.md](gs1-support-plan.md)), which does not change what the symbology defines.

### A separator before a separator or a literal `%` cannot share an Alphanumeric segment

Readers apply the `%` rule left to right within one segment, taking `%%` first. GS GS is written `%%` and reads back as one `%`. GS `%` is written `%%%` and reads back as `%` GS. Both readers probed agree, so the encoder must avoid the pair: end the segment between the two, or write one of them in Byte mode. GS1 data never contains the pair, because a separator is always followed by an AI's digits, but second-position data and arbitrary input can. The standard does not mention the case.

### Other writers get the `%` rule wrong

CodeGlyphX 2.1.0 writes both pairs into one segment (`A<GS>%B` reads back `A%<GS>B`). ZXing.Net 0.16.11 does not double a literal `%` (`123%` reads back `123<GS>`). A fixture's expected text therefore comes from the writer's input only where zxing-cpp reads exactly that.

### Header order differs between writers

The standard puts ECI before FNC1, and ZXing.Net follows it. libzint and CodeGlyphX write FNC1 before ECI. zxing-cpp reads both, so this decoder must too.

### What each oracle can do

| Tool | First position | Second position | Notes |
|---|---|---|---|
| zxing-cpp 0.5.2 (reader) | Standard QR, rMQR | Standard QR, rMQR | The reference reader. Read with `TextMode.Plain`, because the default formats GS1 data as `(AI)` text. A second-position text starts with the indicator. The identifier property omits the ECI offset |
| libzint through ZXingCpp (writer) | Standard QR, rMQR | no | The `gs1` option takes bracketed or parenthesized AI input only, validated. It is ignored for Micro QR without an error |
| ZXing.Net 0.16.11 | writes and reads, Standard QR | cannot read | It does not read the application indicator: an exception or an empty text |
| CodeGlyphX 2.1.0 (writer) | Standard QR, rMQR | Standard QR | Writes the GS pairs wrongly. rMQR input must pass its GS1 validator |

No writer produces rMQR second position, so that cell is checked only by zxing-cpp reading this library's symbols.

### Premises of the 2.0.0 decisions that do not hold

- D9 says an indicator without second position throws "as contradictions do today". No option combination is refused today: the init accessors validate single values. Decision 1 below makes the combination impossible to express instead.
- D10 says a misplaced FNC1 is `InvalidBitstream` "as a misplaced Structured Append header is". A Structured Append header is read wherever it sits, and two tests pin that. The FNC1 rule would be the decoder's first placement rule. Decision 6 keeps it for its own reason.
- D10 names `RmQRDecodeInfo`. The type is `RmQRCodeDecodeInfo`.

## Scope

| In | Out |
|---|---|
| Decode both modes, Standard QR and rMQR. The image decoders inherit it | GS1 syntax, the AI table, check digits, `(AI)` text and Digital Link ([gs1-support-plan.md](gs1-support-plan.md)) |
| Encode both modes, Standard QR and rMQR, `Single` and `Optimal`, and sizing | An AIM symbology identifier member (D10) |
| The rMQR records corrected | FNC1 in an encoded Structured Append set (D11) |
| FNC1 fixtures from libzint, ZXing.Net and CodeGlyphX, checked by zxing-cpp | Micro QR, which has no FNC1 mode |
| Playground: the mode on encode and on decode | ECI escape sequences in the decoded text. Charsets are converted as today |

## What has to stay true

- Every symbol written without FNC1 is bit-identical. The golden and fixture tests pass unchanged.
- The encode route without FNC1 keeps its instruction listing for `Create`, the span `Create` and `TryGetRequiredBufferSize` of both symbologies. A listing diff checks it, and the encode benchmarks stay within noise.
- Every existing fixture decodes as before, and the decode benchmarks stay within noise.
- No path allocates more. The zero-allocation tests gain FNC1 inputs.
- The sweep's misread column does not grow on rMQR. Reading `101` and `110` removes a refusal that a wrong grid could have hit after Reed-Solomon.
- Micro QR is untouched.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| 1 | API shape (refines D9, D10) | One value type, `Fnc1Mode`, as `Fnc1` on `QRCodeGeneratorOptions`, `RmQRCodeGeneratorOptions`, `QRCodeDecodeInfo` and `RmQRCodeDecodeInfo`. It is built as `Fnc1Mode.None` (the default), `Fnc1Mode.FirstPosition`, `Fnc1Mode.SecondPosition(int number)` for 00-99 or `Fnc1Mode.SecondPosition(char letter)` for A-Z and a-z, and read as `IsFirstPosition`, `IsSecondPosition` and `ApplicationIndicator`. No contradiction can be expressed, a letter cannot be passed as a number (`(byte)'A'` is 65, a valid number), and a decoded mode goes back into the options unchanged. D9's enum plus an integer is one type fewer but needs a runtime refusal and keeps the 65 trap. Unprefixed like `EciMode`, because it applies to two symbologies and Micro QR has no member. Under the release split (D12) the decode half publishes the type with its read members only, because nothing in that release takes a mode as input. The construction members and the options property become public with the encode half, which adds them without a break. The shape is still settled before Phase 1, because the decode info freezes with 2.0.0 |
| 2 | The application indicator on decode | The codeword (00-99, or the letter plus 100) in `ApplicationIndicator`, not in the text (D10). The docs show how to build the standard's transmission: identifier, the indicator as two digits or one letter, text. A formatting member can be added later |
| 3 | How `%` is read | Within one segment, left to right, `%%` first. Every reader probed does this, and it is the only reading an encoder can target |
| 4 | GS before GS or `%` in an Alphanumeric segment | Never written. `Single` treats such a text as not Alphanumeric. `Optimal` prices a segment break or a Byte segment there |
| 5 | Header order on encode | The standard's: ECI, FNC1, the application indicator, data. The decoder reads FNC1 before or after ECI |
| 6 | Placement on decode (D10) | `InvalidBitstream` for an FNC1 indicator after a data segment, a second FNC1 indicator, and an application indicator outside 0-99, 165-190 and 197-222. ECI and Structured Append headers may stand on either side. The `%` rule covers the whole symbol, so a late indicator would change how earlier segments read, and no probed writer puts it there. zxing-cpp accepts a late first-position indicator, so this is stricter than one reader |
| 7 | rMQR indicators | `101` and `110` are FNC1 first and second position, and the records are corrected in the rMQR decode phase. If the maintainer has ISO/IEC 23941, its mode table confirms the two rows before that phase ships |
| 8 | FNC1 with ECI | Allowed, as the standard allows. An ECI header is written exactly where it is today (non-ASCII text, or an `EciMode` the caller names). GS1 data is ASCII, so a GS1 symbol never carries one. The GS1 General Specifications do not support ECI, Structured Append or Kanji mode in GS1 QR Code, and that rule belongs to the GS1 layer, because second position has no such limit |
| 9 | FNC1 in Structured Append (D11) | `CreateStructuredAppend` throws `ArgumentException` for any mode but `None` until a reader is measured to read such a set. Decode reads it |
| 10 | Other options under FNC1 | Unchanged. A requested byte order mark follows the FNC1 header as data |
| 11 | The route without FNC1 | The FNC1 check rides a compare that already exists, for example one field holding `Segmentation` and the mode, so one test sends both to the cold route. FNC1 then runs through the resolved and `Optimal` code with its header bits as a parameter |
| 12 | `Optimal` under FNC1 | The scalar cost walk, with FNC1 character classes (GS is Alphanumeric, a literal `%` costs two Alphanumeric characters) and one more state (Alphanumeric after GS). The lane kernels and the default class table stay as they are. GS1 strings are tens of characters, so a lane variant waits for a measured need |

## Equivalence classes

Decode:

- Mode: none, first, second. Indicators 00, 99, `A` and `z`, and one value from each invalid range.
- Placement: first, after ECI, before ECI, after a Structured Append header, after a data segment, twice, first then second.
- Alphanumeric under FNC1: no `%`, `%`, `%%`, `%%%`, `%%%%`, `%` at a segment's start and end, a `%` ending one segment and another starting the next. The same texts without FNC1 stay literal.
- Byte segments with 0x1D and 0x25 under ISO-8859-1 and UTF-8. Numeric and Kanji segments under FNC1.
- The examples of 6.4.8.1 and 6.4.8.2, bit for bit.

Encode:

- `Single` picks Numeric (digits only), Alphanumeric (with GS, with `%`), Byte (lowercase, or a GS pair), Kanji with `AllowKanji`, and UTF-8 behind ECI.
- `Optimal`: the 6.4.8.1 example's input reproduces its bit sequence. A GS pair is resolved by a break or by Byte, whichever is cheaper.
- Capacity: a text at a version edge with and without FNC1 (4 or 12 bits, 3 or 11 in rMQR), and a `%` that crosses the edge.
- Options: each indicator form, each invalid factory argument, the Structured Append refusal.
- Sizing: `TryGetRequiredBufferSize` agrees with `Create` in every class.

## Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | Fixtures and probes, tools only. A `probe-fnc1` and a `regenerate-fnc1` command in QRInteropFixtures: libzint `gs1` (Standard QR, rMQR), ZXing.Net `GS1_FORMAT`, CodeGlyphX first and second position (Standard QR) and `EncodeGs1` (rMQR), each read by zxing-cpp. An `fnc1` field in the manifest and `FixtureLoader` | Fixtures committed. The writer defects recorded in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md). Decision 1 settled |
| 1 | Standard QR decode: the decode classes, then the decoder, `QRCodeDecodeInfo.Fnc1` and the XML docs that call FNC1 unsupported. A test pins the corpus photograph | The Phase 0 fixtures read with the expected text and mode. The photograph reads in all four rotations. Decode benchmarks within noise |
| 2 | rMQR decode: the same classes on rMQR streams, `Decode_ReservedModes_AreInvalidBitstream` replaced, the three rMQR records corrected | The libzint and CodeGlyphX rMQR fixtures read. Sweep misreads unchanged on rMQR. The decode half of the documents below, the approved API listing and the Playground's decode panel land with it, so decode can ship in 2.0.0 on its own |
| 3 | Standard QR encode: the option, `Single`, `Optimal`, header order, sizing, the Structured Append refusal | The encode classes round-trip through this decoder and zxing-cpp (identifier, content type, text). The route without FNC1 keeps its listing. Encode benchmarks within noise |
| 4 | rMQR encode: the same, plus the fit route with FNC1 header bits | The Phase 3 exits for rMQR |
| 5 | Interop sweep and Playground. Generated GS1-like strings and adversarial `%` / GS sequences, encoded here and read by zxing-cpp, and by ZXing.Net for first position. The Playground's encode selector | Every read matches. The Playground publish is green and checked by hand |
| 6 | The documents below and the approved API listing, then this plan folded into the specs and deleted with its research file | Ships with the GS1 layer (D12) |

Phase 0 comes first and Phase 1 next. Phases 2 and 3 depend on 1, Phase 4 on 2 and 3, and Phase 5 on 3 and 4.

The two halves ship separately (D12, decided 2026-10-10). Decode (Phases 0-2) ships in 2.0.0: today every GS1 QR Code reads as `UnsupportedContent`, and reading one needs no GS1 layer. Encode (Phases 3-6) ships in the same release as the builders of [gs1-support-plan.md](gs1-support-plan.md), because an encoder that takes any element string lets a missing separator write a wrong symbol with no error. That release is 2.0.0 if both land before the Phase 7 freeze. Otherwise the encode phases wait on a branch and merge after 2.0.0 is released. All of it is additive and changes no default output.

[gs1-support-plan.md](gs1-support-plan.md) builds on this plan. Its end-to-end phase waits for Phases 3 and 4, its Playground work for Phase 5, its API sketch uses the shape of decision 1, and its invariant (a GS1 element string written with default options has no ECI header and no Kanji segment) relies on decisions 8 and 9. A change to any of those decisions updates that plan in the same change.

## Documents to change

- [standardqr-decoder.md](../specs/standardqr-decoder.md) and [standardqr-encoder.md](../specs/standardqr-encoder.md): FNC1 leaves the unsupported lists for a section with the decisions above.
- [rmqr-decoder.md](../specs/rmqr-decoder.md), [rmqr-encoder.md](../specs/rmqr-encoder.md) and [rmqr-spec-map.md](../specs/rmqr-spec-map.md): the indicator rows and the wrong statement, with the sources.
- [standardqr-spec-map.md](../specs/standardqr-spec-map.md): the FNC1 rows.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the `UnsupportedContent` cases, the scope row, and the `%` rule as shared behaviour.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): FNC1 in the oracle capability matrix, the writer defects, the content column.
- The README (a GS1 / FNC1 section), `docs/migration.md`, and `docs/data-capacity.md` (FNC1 costs 4 or 12 bits, 3 or 11 in rMQR).
- [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md): Phase 6c and D9-D11 point here, with the corrections above.
- The [documentation index](../README.md).

## Progress log

### Investigation (2026-10-10)

Done: the FNC1 clauses of ISO/IEC 18004:2006, the FNC1 code of five other implementations, and the code paths that change were read. libzint, ZXing.Net and CodeGlyphX were probed as writers and zxing-cpp and ZXing.Net as readers. Findings are above and in the research file. `featherqr-2.0.0-plan.md` is not edited yet. No code changed.

Lessons:

- A writer's FNC1 output is not evidence of the `%` rule. Two of the three writers return a different text for some input, and both readers agree on what those symbols say.
- The separator-pair trap is not in the standard. It was found by asking what `%%%` reads as.
- D9 and D10 cite precedents (refused contradictions, a Structured Append placement rule) that the code does not have. A decision's "as X does today" is a claim to check like any other.

### Release order (2026-10-10)

Done: the maintainer split the release. Decode ships in 2.0.0 on its own, and encode ships in the same release as the GS1 layer. Phase 2 now carries the decode half of the documents, the API listing and the Playground's decode panel. Phase 5 keeps the encode selector. [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (scope row, D12, Phase 6c) and [gs1-support-plan.md](gs1-support-plan.md) (G1) were updated in the same change. No code changed.

Lesson: an encoder option that writes whatever data it is given is only half a feature without the layer that builds the data correctly. Reading needs no such layer, so the two halves need not ship together.
