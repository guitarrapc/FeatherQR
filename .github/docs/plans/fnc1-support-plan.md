# FNC1: first and second position, Standard QR and rMQR

## Purpose

This plan details Phase 6c of the [2.0.0 plan](featherqr-2.0.0-plan.md) (GS1 / FNC1, D9-D12): reading and writing both FNC1 modes in Standard QR and rMQR. It settles the rMQR indicator question, corrects two premises of D10, adds what the investigation found about the `%` rule, and orders the work so each step is checked against an outside reader. Decode ships on its own, in 2.0.0. Encode ships in the same release as the GS1 layer (D12, decided 2026-10-10).

The layer above the bit stream (the GS1 AI table, check digits, `(AI)` text, Digital Link) is in [gs1-support-plan.md](gs1-support-plan.md). This plan gives that layer an encoder that takes an element string with U+001D separators and a decoder that returns one with the mode beside it. The research behind the plan (standard clauses, the oracle probe, the code inventory) is in [references/fnc1-support-research.md](references/fnc1-support-research.md).

## What FNC1 is

ISO/IEC 18004 (read in its 2006 edition) defines two modes that change how the whole symbol is read:

- FNC1 in first position (`0101`) marks GS1 data.
- FNC1 in second position (`1001`) marks data in an industry format registered with AIM. An 8-bit application indicator follows: 00-99 as its value, or one letter as its ASCII value plus 100.
- Either appears once, before the first data segment and after any ECI or Structured Append header.
- While a mode is active, a `%` in an Alphanumeric segment is the field separator (GS, 0x1D) and a literal `%` is written `%%`. A Byte segment carries GS as 0x1D. Numeric and Kanji segments are unchanged.
- A reader signals the mode with the symbology identifier (`]Q3` / `]Q4` first, `]Q5` / `]Q6` second, the even ones under the ECI protocol) and transmits the application indicator before the data.
- Micro QR has neither mode. rMQR has both, as `101` and `110` (below).

## Where it stands (2026-10-13, branch `fnc1`)

- Phases 0 to 2 are built: both decoders read both indicators and report `Fnc1Mode` on `QRCodeDecodeInfo.Fnc1` and `RmQRCodeDecodeInfo.Fnc1`, 28 fixtures from three writers are committed, and the rMQR records are corrected. Before Phase 1 the Standard QR decoder returned `UnsupportedContent` for both indicators and the rMQR decoder refused `101` and `110` as reserved.
- The Micro QR decoder refuses M4 indicators 4-7, which is correct.
- Both generators write both modes, through `QRCodeGeneratorOptions.Fnc1` (Phase 3, 2026-10-12) and `RmQRCodeGeneratorOptions.Fnc1` (Phase 4, 2026-10-12).
- The standard's text behind the decode half: ISO/IEC 18004:2006 is on hand (public copy, clauses read and the two examples checked), ISO/IEC 23941:2022 only as a table of contents, so the rMQR mode table rows `101` and `110` still rest on the implementations ([fnc1-support-research.md](references/fnc1-support-research.md), Sources obtained after the merge).
- The real-image corpus holds one GS1 photograph (`qrcode-2/gs1-figure-4.15.1-2.png`), which the sweep counts in the content column in all four rotations. Its expected text is the element string with raw GS and no identifier, the form this plan returns.
- `spot-check-fnc1` in QRInteropFixtures reads every FNC1 symbol this library writes with zxing-cpp and ZXing.Net (Phase 3).

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

- D10 says a misplaced FNC1 is `InvalidBitstream` "as a misplaced Structured Append header is". A Structured Append header is read wherever it sits, and two tests pin that. The FNC1 rule would be the decoder's first placement rule. Decision 6 keeps it for its own reason.
- D10 names `RmQRDecodeInfo`. The type is `RmQRCodeDecodeInfo`.

D9's premise has a precedent. It says an indicator without second position throws "as contradictions do today", and rMQR refuses a `Version` and a `Height` that disagree, in the generator at the call. No accessor refuses a combination: the accessors validate single values. Decision 1 below makes the combination impossible to express instead.

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
| 1 | API shape (refines D9, D10) | One value type, `Fnc1Mode`, as `Fnc1` on `QRCodeGeneratorOptions`, `RmQRCodeGeneratorOptions`, `QRCodeDecodeInfo` and `RmQRCodeDecodeInfo`. It is built as `Fnc1Mode.None` (the default), `Fnc1Mode.FirstPosition`, `Fnc1Mode.SecondPosition(int number)` for 00-99 or `Fnc1Mode.SecondPosition(char letter)` for A-Z and a-z, and read as `IsFirstPosition`, `IsSecondPosition` and `ApplicationIndicator`, an `int?` (decision 2). No contradiction can be expressed, a letter cannot be passed as a number (`(byte)'A'` is 65, a valid number), and a decoded mode goes back into the options unchanged. D9's enum plus an integer is one type fewer but needs a runtime refusal and keeps the 65 trap. Unprefixed like `EciMode`, because it applies to two symbologies and Micro QR has no member. Under the release split (D12) the decode half publishes the type with its read members only, because nothing in that release takes a mode as input. The construction members and the options property become public with the encode half, which adds them without a break. `None` waits with them. In the decode half a symbol without FNC1 reports `default`, where `IsFirstPosition` and `IsSecondPosition` are both false, so a caller needs no name for it, and a name added later breaks nothing. That holds because the option structs have `set` accessors and no constructor ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#public-api-direction)). With the constructor they had until 2026-10-10, a new option would have changed its signature or made calls to it ambiguous. The shape is still settled before Phase 1, because the decode info freezes with 2.0.0 |
| 2 | The application indicator on decode | `ApplicationIndicator` is an `int?`: the codeword (00-99, or the letter plus 100) in second position and `null` in any other mode. It is not in the text (D10). As an `int` its default 0 would also be the indicator 00. `null` is chosen over -1, the value the decode info gives an unknown `MaskPattern`, because the indicator is a value a caller formats: `(-1).ToString("D2")` is `-01`, and `ToString("D2")` on an `int?` does not compile (CS1501), so the caller has to take the value out first, for example with `is int indicator`. `null` for a value that is absent is also the rule on the builder options ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#public-api-direction)). `null` does not tell no mode from first position, so `IsFirstPosition` stays, and `IsSecondPosition` says what `ApplicationIndicator.HasValue` says. The docs show how to build the standard's transmission: identifier, the indicator as two digits or one letter, text. A formatting member can be added later |
| 3 | How `%` is read | Within one segment, left to right, `%%` first. Every reader probed does this, and it is the only reading an encoder can target |
| 4 | GS before GS or `%` in an Alphanumeric segment | Never written. `Single` treats such a text as not Alphanumeric. `Optimal` prices a segment break or a Byte segment there |
| 5 | Header order on encode | The standard's: ECI, FNC1, the application indicator, data. The decoder reads FNC1 before or after ECI |
| 6 | Placement on decode (D10) | `InvalidBitstream` for an FNC1 indicator after a data segment, a second FNC1 indicator, and an application indicator outside 0-99, 165-190 and 197-222. ECI and Structured Append headers may stand on either side. The `%` rule covers the whole symbol, so a late indicator would change how earlier segments read, and no probed writer puts it there. zxing-cpp accepts a late first-position indicator, so this is stricter than one reader |
| 7 | rMQR indicators | `101` and `110` are FNC1 first and second position, and the records are corrected in the rMQR decode phase. If the maintainer has ISO/IEC 23941, its mode table confirms the two rows before that phase ships |
| 8 | FNC1 with ECI | Allowed, as the standard allows. An ECI header is written exactly where it is today (non-ASCII text, or an `EciMode` the caller names). GS1 data is ASCII, so a GS1 symbol never carries one. The GS1 General Specifications do not support ECI, Structured Append or Kanji mode in GS1 QR Code, and that rule belongs to the GS1 layer, because second position has no such limit |
| 9 | FNC1 in Structured Append (D11) | `CreateStructuredAppend` throws `ArgumentException` for any mode but `None` until a reader is measured to read such a set. Decode reads it |
| 10 | Other options under FNC1 | Unchanged. A requested byte order mark follows the FNC1 header as data |
| 11 | The route without FNC1 | The FNC1 check rides a compare that already exists, for example one field holding `Segmentation` and the mode, so one test sends both to the cold route. FNC1 then runs through the resolved and `Optimal` code with its header bits as a parameter |
| 12 | `Optimal` under FNC1 | The scalar cost walk, with FNC1 character classes (GS is Alphanumeric, a literal `%` costs two Alphanumeric characters) and the Alphanumeric states split into "after a separator" and not (two more states rather than one, since the pair position has to be kept too, built 2026-10-12), with its own parent table of one byte a character and state, because the shared table's three bits hold its eight states and no more. Kanji runs are not planned under FNC1. The lane kernels and the default class table stay as they are. GS1 strings are tens of characters, so a lane variant waits for a measured need |

## Equivalence classes

Decode:

- Mode: none, first, second. Indicators 00, 99, `A` and `z`, and one value from each invalid range.
- `ApplicationIndicator`: `null` for no mode and for first position, and 0 for the indicator 00.
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

The two halves ship separately (D12, decided 2026-10-10). Decode (Phases 0-2) ships in 2.0.0: before them every GS1 QR Code read as `UnsupportedContent`, and reading one needs no GS1 layer. Encode (Phases 3-6) ships in the same release as the builders of [gs1-support-plan.md](gs1-support-plan.md), because an encoder that takes any element string lets a missing separator write a wrong symbol with no error. That release is 2.0.0 if both land before the Phase 7 freeze. Otherwise the encode phases wait on a branch and merge after 2.0.0 is released. All of it is additive and changes no default output.

[gs1-support-plan.md](gs1-support-plan.md) builds on this plan. Its end-to-end phase follows Phases 3 and 4 (built), its Playground work Phase 5, its API sketch uses the shape of decision 1, and its invariant (a GS1 element string written with default options has no ECI header and no Kanji segment) relies on decisions 8 and 9. A change to any of those decisions updates that plan in the same change.

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
- D10 cites a precedent, a Structured Append placement rule, that the code does not have. A decision's "as X does today" is a claim to check like any other.
- A correction is a claim too. This plan first said that no option combination is refused today, and rMQR refuses a `Version` and a `Height` that disagree.

### Release order (2026-10-10)

Done: the maintainer split the release. Decode ships in 2.0.0 on its own, and encode ships in the same release as the GS1 layer. Phase 2 now carries the decode half of the documents, the API listing and the Playground's decode panel. Phase 5 keeps the encode selector. [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (scope row, D12, Phase 6c) and [gs1-support-plan.md](gs1-support-plan.md) (G1) were updated in the same change. No code changed.

Lesson: an encoder option that writes whatever data it is given is only half a feature without the layer that builds the data correctly. Reading needs no such layer, so the two halves need not ship together.

### Two points of the API shape (2026-10-11)

Done: decisions 1 and 2 now say that `ApplicationIndicator` is an `int?` and that `Fnc1Mode.None` ships with the encode half. Both were worked out in the discussion of decision 1 on 2026-10-10, where the first proposal was -1 for an indicator that does not apply and `None` in the decode release, and the maintainer had them written in. Decision 1 as a whole is still settled at the end of Phase 0. No code changed.

Lesson: the plan gave the indicator a member and did not say what it reads outside second position. A member whose zero is a real value needs its absent case decided with it.

### Phase 0: fixtures and probes (2026-10-11)

Done: `probe-fnc1` reproduces the 2026-10-10 probe as a QRInteropFixtures command, and `regenerate-fnc1` writes 28 fixtures (21 Standard QR under `StandardQrFnc1/`, 7 rMQR under `RmQrFnc1/`) from libzint, ZXing.Net and CodeGlyphX, each read by zxing-cpp in `TextMode.Plain` before it is written. The manifest and `FixtureLoader` gained `fnc1` (position, the application indicator codeword, the reader's symbology identifier, and the writer's input where it is not the text). The writer defects and the oracle capabilities are in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md). Decision 1 is settled as recommended: `Fnc1Mode` with `IsFirstPosition`, `IsSecondPosition` and `ApplicationIndicator` in the decode half, and construction internal until the encode half. Decision 7's premise held in the probe: both rMQR writers' GS1 symbols read as `]Q3` in zxing-cpp.

The probe added to the research. CodeGlyphX's rMQR validator also takes the element string with separators, and refuses only a separator after a fixed-length AI. CodeGlyphX writes an application indicator outside the standard's ranges (164) without an error, and zxing-cpp then returns no result, which decision 6 refuses as `InvalidBitstream`. ZXing.Net's reader reports `]Q4` for first position under an ECI where zxing-cpp's property reports `]Q3` with `HasECI`. libzint needs 32 data codewords for the 50-character element string at level H, so the rMQR case that zxing-cpp's unit test holds in R15x59-H is at R17x77-H here.

Lessons:

- A fixture of a writer defect is still a fixture of a valid symbol. The symbol means what the standard says it does, every reader agrees, and recording the writer's input beside the reader's text keeps the defect visible without making it the expectation.
- A corpus case pinned to another encoder's version is a claim about that encoder's segmentation. zxing-cpp's test symbol fits 50 characters in R15x59-H and libzint cannot, because libzint spends more codewords on the same text.

### Phases 1 and 2: decode (2026-10-11)

Done: `Fnc1Mode` (`IsFirstPosition`, `IsSecondPosition`, `ApplicationIndicator` as an `int?` codeword, construction internal) on `QRCodeDecodeInfo.Fnc1` and `RmQRCodeDecodeInfo.Fnc1`. The Standard QR and rMQR bit stream decoders read both indicators (`0101` and `1001`, `101` and `110`) once before the first data segment, with ECI and Structured Append headers on either side, read and range-check the application indicator, and apply the `%` rule through one shared Alphanumeric payload decoder (`SegmentDecoders.DecodeAlphanumericPayloadFnc1`), kept apart from the plain one so the route without FNC1 keeps its code. The destination is checked per character written under the rule. The 28 fixtures, zxing-cpp's eight FNC1 bit stream vectors (six it reads, one it rejects, one it disabled), of which this library reads five and refuses three (the late first-position one by decision 6, the rejected one and the disabled one) and the standard's two examples read as expected in 420 FNC1 tests on both target frameworks, the whole suite passes (37,770), and the GS1 figure of the real-image corpus reads at four right angles. The rMQR records and `Decode_ReservedModes_AreInvalidBitstream` are replaced. The decode half of the documents (both decoder records, both spec maps, the architecture record, the fixture spec, the README and the migration guide), the approved API listing (`Fnc1Mode` and the two `Fnc1` properties, the API page regenerated) and the Playground's decode panel (the mode beside the symbol, the separator shown as U+241D) landed with it. The rMQR sweep is identical on every paired render (29,440 renders, 0 gained, 0 lost, 0 misread), and the real-image corpus gained the GS1 figure's four reads with nothing else moved. Decode benchmarks (`QRCodeDecodeEndToEnd`, `RmQRDecodeEndToEnd`, pinned to one CCD, main and branch alternating twice, each run holding the project's ShortRun job in child processes and an in-process job of 8 iterations of 250 ms, with `MicroQRDecodeEndToEnd` as an untouched control): in the second round's ShortRun job the branch reads 0.87 to 1.04 of main on Standard QR and 0.94 to 1.05 on rMQR, and the control 0.90 to 1.05. In-process the three read 0.93 to 1.07, 0.96 to 1.05 and 0.94 to 1.02. The only row above 1.05 in each round's ShortRun job is the rMQR Alphanumeric decode (1.09 and 1.05, and 1.04 and 1.05 in-process), against a control that reached 1.36 in the first round and 1.05 in the second, so this method resolves no change smaller than the control's own swing (10 % down, 5 % up in the second round). A finer answer needs more alternations or a benchmark of the bit stream decoder alone. Allocations are identical on every ShortRun row (the in-process job jitters by 1 byte on a few Standard QR rows, in both directions). An unpinned first attempt had read every Standard QR row 30 to 60 % slower on the branch, the untouched image and codeword-extraction paths included, which is the CCD placement this machine is known for and not the code. The Playground was published (Debug, no AOT) and checked by hand in the browser: a second-position fixture reads with its indicator (37), a GS1 fixture reads as GS1 with the separator shown as U+241D, and an rMQR GS1 fixture reads as GS1.

Lessons:

- A hand-computed bit constant is a claim too. Two rMQR test streams were written with wrong Alphanumeric pair values, and the decoder, correct, failed them. A vector copied from another reader's test suite (zxing-cpp's eight) is worth more than one derived by hand, so both kinds are in the tests.
- A test that pins a record is not a test of the record. `Decode_ReservedModes_AreInvalidBitstream` pinned the decoder to the "reserved" statement, so correcting the record meant deleting the test rather than failing it.
- The destination check cannot be the count read. The `%` rule only shrinks the text, and a check for the count read would refuse a destination sized for the text as it reads, so the FNC1 payload decoder checks per character written.

### Review of the decode half (2026-10-11)

Done: an adversarial review in rounds, each round four or two independent reviewers over the diff and three independent verifiers over every claim. Round 1 (decode correctness, fixtures and tests, documentation and API, allocation and .NET discipline) raised 17 claims. One was refuted by all three verifiers (the rMQR 7.4.8 citation does have a source, the research file's record of Wikipedia). The other 16 were fixed: the corpus-shape test now reads each fixture's stream from its matrix with the reference extraction (`Fnc1StreamShape`) and asserts the header order of the ECI fixtures, the ECI assignment, the data modes and the two separator forms, which the manifest does not record, and every fixture's headers are checked against its manifest that way. The gate requires assignment 26 in zxing-cpp's ECI-protocol bytes where a case asked for UTF-8 (the corpus regenerates byte-identically). The tests gained the texts without FNC1, second position beside an ECI header in both orders, an indicator after an empty segment in both positions, and on rMQR Kanji under FNC1, Byte 0x1D and 0x25 under ECI 3 and 26, `%` at a segment's start and end, `%%%%`, the segment-boundary pair and the indicator 255, and the two ISO examples are pinned as byte strings. The documents lost a wrong fixture split (20 and 8 for 21 and 7), a wrong string length (51 for 50), a wrong fixture count (three for four), a wrong vector count (five for eight), a wrong `dataBitCount` contract, an ECI list that omitted assignments 1 and 27, a wrong reading of the `]Q4` modifier (the reader's ECI protocol, not the symbol's header), a benchmark figure quoted from the wrong round, a stray space in three parameter docs, a README variable, and a migration count. Round 2 raised one low finding (the digits-only corpus assertion did not require first position) and four wording errors in round 1's own replacements, all fixed. The decode-correctness lens of round 1 found nothing, and round 2 reviewed only the tests and the documents.

Lessons:

- A reviewer's replacement text is a claim too. Four of round 2's five findings were sentences round 1 had just written, each with a figure or a scope word taken from memory of the data rather than from the data.
- A shape the corpus is said to hold needs a reader that can see it. Header order and segment modes were claimed by file names and asserted through fields that cannot tell them apart, so a writer change could have made the claims false while every test passed.
- A benchmark comparison keyed by method name alone merged two jobs. The reports held a child-process job and an in-process job, and the first comparison read whichever came last, so the record described one job's method with the other job's numbers until the rows were keyed by job as well.

### Spec sources after the merge (2026-10-12)

Done: the public copy of ISO/IEC 18004:2006 and the public previews of ISO/IEC 23941:2022 were obtained into `.references/specs/` and read against the shipped decode. The two examples of 6.4.8 match the tests' framing, except that the second example ends in `<CR>`, not a line feed, so `Fnc1DecodeTest` and the `aim-37-iso-example-m` fixture were corrected. Annex F's wording for the modifiers 3 to 6 ("ECI protocol not implemented / implemented") backs the review's correction of the `]Q4` remark. The 23941 contents confirm the clause numbers 7.3.8 and 7.4.8 that the code and specs cite, and leave its mode table unread. The sources, their locations and what each settled are in [fnc1-support-research.md](references/fnc1-support-research.md).

Lesson: a research summary of a standard is one reading away from the text. The summary said the second example ends in a line feed, the text says `<CR>`, and a test built from the summary pinned the wrong byte. Read the clause, not the summary, before pinning a vector as the standard's.

### Phase 3: Standard QR encode (2026-10-12)

Done: `QRCodeGeneratorOptions.Fnc1`, with the construction members of `Fnc1Mode` public (`None`, `FirstPosition`, `SecondPosition(int)` for 00 to 99 and `SecondPosition(char)` for A-Z and a-z, each refusing the rest), on `Create`, the span `Create` and `TryGetRequiredBufferSize`, under `Single` and `Optimal` and beside every other option. The stream is the standard's: the ECI header, the FNC1 indicator with its application indicator in second position, the data (decision 5), and a requested byte order mark follows the indicator as data (decision 10). Under `Single` a scalar analysis of the FNC1 rule (`TextAnalyzer.AnalyzeFnc1`) picks the mode: U+001D is an Alphanumeric character, a literal `%` costs two, and a separator before a separator or a literal `%` makes the text Byte (decision 4), and the Alphanumeric writer applies the rule in reverse (`QRBinaryEncoder.Fnc1.cs`). Under `Optimal` the program has its own scalar walk and parent table (`ModeSegmenter.Fnc1.cs`): nine states, the Alphanumeric pair states split into "after a separator" and not, so a separator pair prices a break against a Byte run (decision 12, amended to two states), one byte a character and state, and no Kanji run. The single-mode route under FNC1 is its own set of methods (`QRCodeGenerator.Fnc1.cs`, the version fit with the indicator's bits, the stream with its headers, the core writers), copied from the default route rather than parameterised, because the default route's methods are inlined into `Create`. The options struct keeps the segmentation and the mode in one field, so `Create` routes on the one compare it had (decision 11). `CreateStructuredAppend` refuses the option (decision 9). The approved listing gained the four construction members and the option.

Verified: 166 tests per framework (167 in Release, with the allocation test) in `Fnc1EncodeTest` and `Fnc1ModeTest`: the two examples of ISO/IEC 18004 6.4.8 reproduced bit for bit under `Optimal` (the first at level M, where the split is what makes version 2 fit), each data mode under `Single` with its stream shape read back through the reference extraction, the same texts unchanged without FNC1, the ECI header before the indicator in both charsets, the byte order mark after it, each indicator form, the separator pair resolved by a break or by Byte under `Optimal`, mixed content smaller than `Single`, the indicator's bits and a literal `%` moving a text across a version edge, the version range, boost and mask under FNC1, the Structured Append refusal, the too-long errors, sizing against `Create` and the span form against the data form over 84 option combinations, every Phase 0 Standard QR fixture's data re-encoded here and read back with its mode under both segmentations, and the span form allocating nothing (Release). `spot-check-fnc1` wrote 192 symbols (24 texts, the Phase 0 corpus's data, the two ISO examples and adversarial `%` and separator sequences, under both segmentations at the four levels): zxing-cpp read all 192 with the mode's identifier, GS1 content for first position, the ECI flag and the text (the indicator before it in second position). ZXing.Net read 124 of the 128 first-position symbols, and the four it missed are two symbols it reads under every mask but the chosen one, at 4 and 8 pixels a module, so the misses are its reader's. The JIT's listings of `Create` and the span `Create` (inlined into the harness) of both symbologies are byte-identical to the listings before the change, apart from one immediate operand (a metadata token) in rMQR's span `Create`. `TryGetRequiredBufferSize` of Standard QR moved its `Optimal` branches to the cold route and shrank from 442 to 385 bytes of code, which is the one departure from the "keeps its listing" exit, taken because its `Optimal` branches were inline and no FNC1 check could ride them. The full suite passes on both frameworks. No benchmark was run: identical machine code measures noise (the architecture record's lesson), and the sizing method has no benchmark.

Lessons:

- `Optimal`'s contract (never a version the single-mode stream already fits) hides a split the standard's example shows: at level L both streams fit version 2, so the example reproduces at level M, where only the split fits.
- A parent table that packs its states into three bits cannot take two more. A table of one byte a character and state for the FNC1 program was cheaper than repacking the shared one, and keeps the shared walk untouched.
- Keeping a hot method's listing means copying, not parameterising, whatever is inlined into it. One field for two options rode the existing compare in `Create`. The sizing method's `Optimal` branches were inline, so it could not keep its listing and was restructured instead.
- A reader's miss on a clean symbol is not evidence of a symbol defect until the other masks are tried. ZXing.Net reads the same stream under seven masks and misses the eighth.

Benchmarks: not run, see above.

### Phase 4: rMQR encode (2026-10-12)

Done: `RmQRCodeGeneratorOptions.Fnc1`, the same `Fnc1Mode`, on `Create`, the span `Create` and `TryGetRequiredBufferSize`, under `Single` and `Optimal` and beside every other option (a version, a strategy, a height, a charset, Kanji, the quiet zone). The stream is the standard's: the 11-bit ECI prefix, the indicator `101` or `110` with its application indicator, the data (decision 5), with the same analysis, `%` rule and separator-pair rule as Standard QR (decision 4, `TextAnalyzer.AnalyzeFnc1` shared). What rMQR adds is the fit: the indicator's 3 or 11 bits sit beside the ECI prefix, and the capacity tables of the automatic fit have no FNC1 axis, so the FNC1 route prices each version of the strategy's order (`RmQRVersionSelector.TrySelectFnc1`), a walk of at most 32 versions on a cold route, and the "too long" error states the maximum under FNC1. The mixed-mode scan under FNC1 (`RmQRSegmentPlanner.Fnc1.cs`) keeps the trivial bound and the memoised cost run of the scan without FNC1 and drops its floor filter, whose run counting reads the other program's packed parent table, and builds the FNC1 program's plan with its own unit fill (a literal `%` twice, a Byte run's bytes). The stream writers are their own methods (`RmQRBinaryEncoder.Fnc1.cs`, the single run and the segmented run, both reusing the run writers). The options struct holds the segmentation and the mode in one field, declared where the segmentation's field was so the struct keeps its layout, and `Create` routes on the one compare it had (decision 11). The cold route (`RmQRCodeGenerator.Fnc1.cs`) reads both out, `Single` under FNC1 to its own methods and a segmentation to the mixed-mode route with the mode as a parameter. The approved listing gained the option.

Verified: 135 tests per framework in `Fnc1RmQREncodeTest` (136 in Release, with the allocation test): the two smallest streams bit for bit (`101 001 0001 0111 000` and `110 00100101 010 001 001011 000`, the bits the decode tests read), each data mode under `Single` with its stream shape read back through the naive reference extraction, the same texts unchanged without FNC1, Kanji mode under `AllowKanji`, the ECI prefix before the indicator in both charsets, each indicator form, the separator pair resolved by a break or by Byte under `Optimal`, `Optimal` never a larger symbol than `Single` and smaller for mixed content, a Kanji-eligible text with ASCII planned over UTF-8, a requested version the single-mode stream overflows held by a plan (R7x99-M, 222 of 224 bits), the capacity edges of R7x43-M (12 digits fit without the indicator and 11 with it, 9 digits fill it exactly in second position, seven Alphanumeric characters fill it under first position and a literal `%` overflows it), the automatic fit moving 14 digits off R11x27, each fit strategy checked against every version that holds the stream, a height and a version, the disagreeing pair and the undefined values refused, Latin-1 refused over content it cannot hold, 361 digits too long with the indicator, sizing against `Create` and the span form against the data form over 84 option combinations, sizing false where nothing fits, the seven Phase 0 rMQR fixtures' data re-encoded here (`Single` on the automatic fit, `Optimal` at the fixture's version) and read back with the mode, and the span form allocating nothing. `GeneratorOptionsAssignmentTest` gained the option's row. The full suite passes on both frameworks (56,986 tests). `spot-check-fnc1` now writes the same 24 texts as rMQR symbols under both segmentations at M and H: zxing-cpp read all 94 with the mode's identifier, GS1 content for first position, the ECI flag and the text (the two symbols of the longest text at H fit no rMQR symbol and are reported as skipped). ZXing.Net has no rMQR reader. The Standard QR half is unchanged (192 symbols, the same four ZXing.Net mask misses). The JIT's listings of Standard QR's `Create` and `TryGetRequiredBufferSize` are what Phase 3 left (byte-identical apart from relocation addresses). rMQR's three entry points shrank: `Create` from 496 to 449 bytes, the span `Create` from 1438 to 1386, `TryGetRequiredBufferSize` from 603 to 572 and from an rbp-based to an rsp-based frame. The differences are the cold call's option arguments gone (seven, six for sizing) (the cold route reads the options by reference), a 16-byte smaller frame and the register names that follow, with the hot instructions otherwise unchanged. The encode benchmark is below.

Lessons:

- A table with no axis for a cold route's option is a walk, not a wider table: the two indicator widths would have added 96 capacity tables (6 KB) to buy one compare on a route that prices tens of characters.
- A filter that reads the other program's table cannot be shared. The rMQR scan's floor filter counts runs from the packed parent table. Under FNC1 the exact cost run decides on its own, which a cold route can afford.
- "The same data at the same version" is not a `Single` round trip. The fixture writers split modes, so a mixed-case serial fits a version its single-mode stream does not. The fixture test fits `Single` automatically and holds `Optimal` to the fixture's version.
- The cold stub's arguments are the hot path's cost. Spreading the options (seven, six for sizing) to the mixed-mode route was inline in all three rMQR entry points. Reading the options by reference there shrank each of them, the one departure from "keeps its listing" in this phase, and a shrink.
- The every-option assignment test compares the table's value with the initializer's: a different value in the two is a failure of the whole class, not of the option.

Benchmarks: `SimpleRmQREncode` (the three fixed-version encodes, in-process, pinned to one CCD, build servers shut down, two alternating rounds a side, the CodeGlyphX rows as the untouched control): Numeric R7x43 read 1.01 to 1.02 of the baseline and Alphanumeric R11x59 1.01 to 1.04, inside the control rows' drift of 0.97 to 1.03, and Byte R17x139 read 0.93 to 0.96, past the control band in the direction of the smaller listing. Allocations are unchanged.

### Review of Phases 3 and 4 (2026-10-13)

Four reviewers (Standard QR correctness, rMQR correctness, tests and API and performance, docs and comments) read the encode commit, and three verifiers voted on each of the fifteen claims that survived ranking. Two were refuted on the contract and left as they are: `Single` picks the mode by character class, so a percent-heavy text under FNC1 can take Alphanumeric where Byte would be smaller (a literal `%` is two Alphanumeric units, 11 bits, against 8 in Byte), which is what `QRSegmentation.Single` promises and what `Optimal` is for. `ApplicationIndicator` reports the wire codeword that `SecondPosition(int)` refuses above 99, because a decoded `Fnc1Mode` goes back into the options as a value and the two construction forms are documented.

Fixed:

- Under `Optimal` with FNC1 and no version range, content that fits version 40 only without the indicator's bits threw `ArgumentException` where `Single` and the `Create` docs give `InvalidOperationException`: the throw helper recomputed the single-mode fit without those bits. It now recomputes the FNC1 fit.
- The 22-bit segmentation field stored an undefined enum value masked, so a value with no low bits read back as `Single` and took the default route unrefused. The setter saturates a value the field cannot hold to the mask, which the cold route refuses.
- rMQR's "too long" message under FNC1 could read "0 bytes, but the maximum is 0 bytes" where the ECI prefix and the second-position indicator alone exceed R7x43-H, and counted Alphanumeric units as characters. It now names the headers' bits against the capacity, and says units.
- The FNC1 parent table (nine bytes a character) shared the two-byte table's 512-byte stack budget, so a text over 56 characters rented from the pool. It has its own budget of 112 characters (1008 bytes).
- Tests sharpened or added: 7089 digits at version 40-L's exact edge (7090 overflowed without the indicator too) and the `Optimal` overflow's exception type, the requested-version plan at R7x99-M pinned to the one split that fits (224 of 224 bits), two adjacent literal percents in an Alphanumeric run in both symbologies (bit for bit in rMQR), undefined segmentation values, the headers-alone message, and two Because texts and a comment whose arithmetic was wrong with the right conclusion (151 of 152 bits, 203 against 268, the R7x99-M plan at 224). The classes now hold 148 and 21 tests for Standard QR (one more in Release) and 141 for rMQR (142 in Release). The full suite passes on both frameworks.
- Docs: ZXing.Net's spot-check count was 92 of 96 and is 124 of 128 (16 first-position texts under two segmentations at four levels). A tool comment gave R17x139-H 69 data codewords (76), an FNC1 capacity axis would be 96 tables (6 KB) for its two indicator widths rather than 48, and the old cold call spread seven options (six for sizing) rather than nine scalars. The sentences that said FNC1 encode was not yet written (README, migration, the symbology topology, the Standard QR decoder record, this plan's status) now say what ships. Plan and phase identifiers in code comments became pointers to the encoder records.

Lessons:

- A throw helper that recomputes a fit to choose the exception must recompute the same fit the route used. The Optimal route's helper priced the stream without the indicator's bits and so chose the narrowed-range exception for an unconstrained overflow.
- A field narrower than the value it stores must refuse what it cannot hold, not mask it. Masking turned an undefined value into the default.
- A Because text's arithmetic is a claim like any other constant. Two Because texts and a comment were wrong with the right conclusion, and one test could not tell which of two plans the program built.
