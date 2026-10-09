# GS1: element strings, `(AI)` text and Digital Link

## Purpose

The maintainer asked for GS1 support on 2026-10-10. [fnc1-support-plan.md](fnc1-support-plan.md) gives the encoders and decoders FNC1, so a GS1 element string can go into a symbol and come back out. This plan covers the layer above it: the AI table, the checks GS1 defines, `(01)…(17)…` text in both directions, Digital Link URIs, and how GS1's carrier rules meet the three symbologies. It also revisits a row of the [2.0.0 plan](featherqr-2.0.0-plan.md), which puts "GS1 syntax validation and human-readable `(AI)` formatting, which need the GS1 AI table" out of scope. This plan is that table and what it costs.

The research behind it (GS1 sections, the dictionary format, other libraries, the oracles) is in [references/gs1-support-research.md](references/gs1-support-research.md). When the plan completes, its durable content moves into the specs, and the plan and its research file are deleted.

## What GS1 asks of a QR library

GS1 uses two QR carriers, and they need different things:

- GS1 QR Code carries an element string behind FNC1 in first position and is transmitted as `]Q3`. It is used in healthcare, logistics, assets, tobacco and with AI 8200. GS1 does not support ECI, Structured Append or Kanji mode in it.
- QR Code with a GS1 Digital Link URI is plain QR with no FNC1, transmitted as `]Q1`. It is the retail carrier: GS1's 2D migration at the point of sale (goal end of 2027) lists it with GS1 DataMatrix, and GS1 QR Code is not a retail option.
- Micro QR is excluded by the General Specifications, and rMQR is not listed as a carrier in Release 26.0.

Both carriers rest on the same data rules, so a caller needs three jobs from this layer:

1. Build: turn `(AI)` text or a Digital Link URI into an element string, or an element string into a Digital Link URI, with the separators and checks GS1 requires.
2. Read: split a decoded element string into AIs, show it as `(AI)` text, and turn a scanned Digital Link URI into an element string.
3. Check: lengths, character sets, check digits, dates, the other linters, and the AI pairing rules.

## Where it stands (2026-10-10, `main` at f4b74de)

- No GS1 code exists. FNC1 is planned in [fnc1-support-plan.md](fnc1-support-plan.md): `Fnc1Mode` as `Fnc1` on the Standard QR and rMQR options and decode info, an element string with U+001D separators in and out, no GS1 syntax checks, landing before the 2.0.0 freeze. Its decision 1 (the shape of `Fnc1Mode`) is still open, and the recipes below follow its current proposal.
- A Digital Link URI already encodes and decodes as plain text. What is missing is building one correctly and reading GS1 data out of one.
- The real-image corpus holds one GS1 symbol (`qrcode-2/gs1-figure-4.15.1-2`), whose element string uses AIs 01, 21, 10, 17 and 8200.

## What the investigation found

### Every implementation that checks GS1 data generates its table from the Syntax Dictionary

libzint, BWIPP and CodeGlyphX generate their AI tables from the GS1 Barcode Syntax Dictionary. zxing-cpp keeps a hand-maintained, length-only table that cites an older dictionary release. The dictionary is Apache-2.0, released about once a year with the General Specifications, and holds 224 entry lines (about 541 AIs): component types and lengths, the predefined-length flag, the Digital Link attribute flag, 34 linter names, the pairing rules and the Digital Link key qualifiers. A hand-maintained table drifts. A generated one changes only when the pinned release does.

### Reading needs the table too, but it can tolerate newer AIs

An element string cannot be split without lengths, because a predefined-length element carries no separator. So formatting a decoded symbol as `(AI)` text needs the table, as the 2.0.0 plan said. A reader can still accept AIs newer than its table. The AI's own length follows from its first two digits (Table 7-5), and the predefined-length prefixes (Table 7-6) "will remain unchanged", so an unknown AI can be read as variable length up to the next separator. The Syntax Engine offers the same as "permit unknown AIs". When building, an unknown AI is more likely a typo than a new AI, so building should refuse it.

### Pairing rules belong to the item, not to one symbol

The dictionary's `req` and `ex` rules (AI 10 requires 01, 02, 03, 8006 or 8026, and AI 21 excludes 235) are to be evaluated "over the combined data received from all GS1 carriers marking a physical item". A per-symbol check can refuse data that is valid on the item, for example a QR with batch and expiry beside a linear barcode with the GTIN. The Syntax Engine, BWIPP and CodeGlyphX check pairings by default. libzint does not.

### The retail half is Digital Link and needs no FNC1

A plan that stops at FNC1 misses retail. Digital Link URI Syntax 1.7.0 (August 2026) fixes the path (one primary key, its qualifiers in dictionary order), the query (data attributes, AIs flagged `?`), percent-encoding and a canonical form (`https://id.gs1.org`, query keys sorted). Retail requires the uncompressed form, and compression has moved to a separate EPC binary standard.

### GS1's rules on the symbol hold by construction

GS1 data is ASCII (CSET 82, 39 and 64). With `EciMode.Default` the encoders write no ECI header for ASCII text and never write Kanji mode for it, and the FNC1 plan refuses Structured Append with any FNC1 mode. So an element string from this layer, encoded with FNC1 in first position and default options, is a compliant GS1 QR Code. Only an `EciMode` the caller names breaks that. A test can pin the property, so no API has to refuse anything.

### The oracles disagree by dictionary release

CodeGlyphX 2.1.0 (already pinned) uses dictionary 2026-01-27, the release this plan pins. libzint 2.16.0 and zxing-cpp 3.1.0 (both through the pinned ZXingCpp 0.5.2) use older releases. The Syntax Engine 1.4.1 is the reference, but it ships only as prebuilt release assets (a Windows .NET wrapper among them), with no NuGet package. Fixtures therefore record each oracle's verdict and its dictionary release, and a disagreement is explained by release before it is called a defect.

## Scope

| In | Out |
|---|---|
| An AI table generated from a pinned dictionary release, checked for drift by a test | A hand-maintained table |
| Reading an element string: split into AIs, tolerant of unknown AIs and of a redundant or trailing separator | Scanner transmission strings (a `]Q3` prefix, keyboard-wedge forms). The decoder reports the mode instead |
| Checks: component lengths and character sets, all 34 linters including the ISO lists, `req` and `ex` | Data titles and non-HRI text (`GTIN`, `BEST BEFORE`), which would add the titles to the table |
| `(AI)` text in both directions, with the `\(` escape for a literal parenthesis | Compressed Digital Link URIs, resolvers and `linkType` |
| Digital Link URIs in both directions: uncompressed, URI Syntax 1.7.0, any stem on input, the canonical form on output | GS1 DataMatrix, GS1-128 and DataBar, which this library does not draw |
| Documentation: which carrier to use, the two-line recipes, GS1's no-ECI rule | Print guidance (X-dimension, print grade). The README says only that the default quiet zone is GS1's 4X |
| The Playground's GS1 input and decoded-AI view | A GS1 overload on the generators (G4) |
| | GS1 on Micro QR, which has no FNC1 and is excluded by GS1 |

## Design

### The element string is the hub

```text
(AI) text ── FromHri ──┐                       ┌── ToHri ──> (AI) text
                       ▼                       │
DL URI ── FromDigitalLink ──> element string (GS as U+001D) ── ToDigitalLink ──> DL URI
                                  │           ▲
              Fnc1 = FirstPosition│           │ info.Fnc1.IsFirstPosition
                                  ▼           │
                QRCodeGenerator / RmQRCodeGenerator / decoders   (fnc1-support-plan.md)
```

Every conversion is one hop, and the hub is the form the FNC1 encoder takes and the decoder returns. A Digital Link URI goes into a symbol as plain text, with no FNC1.

### API sketch

Names and signatures are settled in Phase 1. The shape follows the existing pair: builders look like the generators (a one-line form that throws, and a span form), readers look like the decoders (`Try`, with a string or a caller's span).

```csharp
// Build a GS1 QR Code (healthcare, logistics)
var qr = QRCodeGenerator.Create(Gs1.FromHri("(01)09506000134352(17)201225(10)ABC123"), QREccLevel.M, new() { Fnc1 = Fnc1Mode.FirstPosition });

// Build a QR Code with a Digital Link URI (retail)
var dl = QRCodeGenerator.Create(Gs1.ToDigitalLink(Gs1.FromHri("(01)09506000134352(10)ABC123")), QREccLevel.M);

// Read
if (QRCodeDecoder.TryDecode(data, out var text, out var info))
{
    if (info.Fnc1.IsFirstPosition && Gs1.TryToHri(text, out var hri, out _)) { /* (01)09506000134352(17)201225(10)ABC123 */ }
    else if (Gs1.TryFromDigitalLink(text, out var elementString, out _)) { /* the same element string */ }
}

// Check data from elsewhere, and look at single elements without allocating
if (!Gs1.TryValidate(elementString, out Gs1Error error)) { /* error.Kind, error.Offset, error.Length */ }
foreach (var element in Gs1.EnumerateElements(elementString)) { /* element.Ai, element.Value */ }
```

| Member | From → to | Checks |
|---|---|---|
| `FromHri`, `TryFromHri` | `(AI)` text → element string | every check, pairings included |
| `ToDigitalLink`, `TryToDigitalLink` | element string → URI, with an optional stem (default `https://id.gs1.org`) | every check, pairings included, plus what a URI can carry |
| `TryToHri` | element string → `(AI)` text | what splitting needs, unknown AIs read |
| `TryFromDigitalLink` | URI → element string | what reading needs (G5): the URI's structure. Key order and unknown keys follow the Syntax Engine (Phase 5) |
| `TryValidate` | element string | every check |
| `EnumerateElements` | element string → `(Ai, Value)` spans | what splitting needs |

Each `Try` form exists twice, returning a `string` or writing into a caller's span with `charsWritten`, and the span form allocates nothing. The public surface is five types: the static class `Gs1`, `Gs1Error` and its kind enum, and the ref struct element and enumerator that keep reading allocation-free. They sit in the core's `FeatherQR` namespace (G2).

### The table

- Source: the dictionary file of the pinned release, committed with its license and a provenance note under the test data. The generator lives in the test project, as `SimdTiersDocTest` does: it fails when the generated source differs from what the pinned release produces and rewrites it outside CI.
- Output: RVA data and a lookup by AI, with no allocation and no static constructor, like `ShiftJisKanjiTable`. Titles are not stored.
- The list linters (`iso3166`, `iso3166alpha2`, `iso3166999`, `iso4217`, `iso5218`, `mediatype`, `packagetype`) need data the dictionary does not hold. Phase 0 finds and pins its source the same way.
- The generator refuses a dictionary that names a linter the code does not implement, so a release that adds one cannot be pinned without it.
- Update: once a year, after GS1's January release, as its own pull request that lists the release's GSCNs and the fixtures it changes.

### Separators and order

Building writes a separator only where the General Specifications require one (§7.8): after every element except the last, unless its AI's first two digits are in the predefined-length table. It never reorders elements. A caller who wants fewer separators puts the variable-length element last, and the docs say so. Reading accepts a redundant separator after a predefined-length element and a trailing one, both of which occur in transmitted data. Whether `TryValidate` reports them is settled against the Syntax Engine's verdict in Phase 3.

## What has to stay true

- An application that does not call `Gs1` trims to the same size as before.
- The encoders and decoders are unchanged by this layer. Their benchmarks stay within noise.
- Every element string this layer builds, encoded with FNC1 in first position and default options, carries no ECI header and no Kanji segment.
- The span forms allocate nothing. Checked in Release only, since Debug builds allocate for `stackalloc` initializers.
- No `Try` form throws for any input text. Only a malformed option, such as a stem that is not a URI prefix, throws, as the existing `Try` APIs do.
- The generated table is exactly what the pinned release produces.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| G1 | Release | It does not block 2.0.0. It is additive and changes no output, so it ships in the first release after it is done: 2.0.0 if it lands before the Phase 7 freeze, 2.1.0 otherwise. The 2.0.0 plan's Out row points here once the maintainer accepts this plan |
| G2 | Package | The core, as static members of `Gs1` in the `FeatherQR` namespace. An application that does not call it trims it away, and the core split record already rejected packages that would save untrimmed consumers 100-130 KB. Phase 0 measures the table, and the recommendation holds while it stays well under that. A `FeatherQR.Gs1` package would avoid an assembly-size cost for untrimmed consumers and could ship table updates without a core release, at the cost of a fourth package to version, validate and document. Revisit when someone asks for GS1 without FeatherQR, or when table updates must ship more often than the core |
| G3 | Table source and update | Generated from dictionary release 2026-01-27 by a test, as described above, updated once a year |
| G4 | Encode entry point | No GS1 overload on the generators. The conversion and the FNC1 option compose with every existing overload (span, destination, sizing) on both symbologies, where an overload would need a twin of each |
| G5 | Check strength per member | Members that build data for a symbol run every check, pairings included. Members that read data from a symbol check only what they need and accept unknown AIs. `TryValidate` runs every check. Revisit when a caller needs to build data split across carriers, which the pairing check refuses |
| G6 | Unknown AIs | Read as variable length up to the next separator. Refused when building |
| G7 | Error shape | `Gs1Error` with a kind, an offset and a length into the input, so a caller can point at the bad element. The throwing forms throw `FormatException` with the same information, as parsers in the BCL do. No new exception type |
| G8 | License of the generated table | A third-party notice file in the repository and in the core package, with the dictionary's Apache-2.0 text and attribution, and a header in the generated source naming the release. `PackageLicenseExpression` stays `MIT`, as .NET packages that carry third-party notices do. The maintainer confirms this before Phase 2 |
| G9 | Digital Link | Uncompressed, URI Syntax 1.7.0. Output in the canonical form. Input with any stem, percent-decoded, a repeated query key resolved last-wins. Non-numeric extension keys are ignored. A compressed URI is refused with its own kind. When an element string holds two primary keys, the choice follows the Syntax Engine, settled in Phase 5 |
| G10 | rMQR | No refusal. The layer does not see the symbol, and readers do read GS1 rMQR. The docs say GS1 does not list rMQR as a carrier (General Specifications Release 26.0), so a GS1 scanner may not accept it |
| G11 | Where the record lives | A "GS1 data" section in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), since shared content lives only there. If it outgrows a section, a `gs1-data.md` design record and a guidelines change, decided with the maintainer then |

A lead outside the scope: the scheme and host of a Digital Link URI are case-insensitive, so an uppercase stem would let Alphanumeric mode carry it under `Optimal`. It needs the Digital Link standard's view and phone readers checked first.

## Equivalence classes

- AIs: 2-, 3- and 4-digit AIs. Range AIs (the decimal-point digit of 31nn-36nn). Predefined length, fixed length outside the predefined table (needs a separator), variable length. Optional and multi-component specs (`7007`, `8008`).
- Separators: after a variable-length element not last, after a fixed-length element outside the predefined table, none after a predefined-length element, none after the last. On read, a redundant one after a predefined-length element, a trailing one, two in a row (an empty field, refused).
- Character sets: each of `N`, `X`, `Y`, `Z` at its boundary characters, a character outside it, a non-ASCII character, `Z` padding.
- Each of the 34 linters: valid, the boundary, invalid.
- Dates: day `00` with `yymmd0` and without, 29 February in a leap and a common year across the -49 / +50 window, month 00 and 13.
- Pairings: a single requisite, a compound requisite (`+`), an exclusion, a repeated AI with an equal and an unequal value.
- Unknown AIs: read, refused when building.
- `(AI)` text: an escaped `\(`, an unbalanced parenthesis, an empty value, a non-digit AI, whitespace.
- Digital Link: each primary key, qualifiers in and out of order, query attributes, an AI the URI cannot carry, percent-encoded reserved characters, a stem with a path, `http` and `https`, a trailing slash, an unknown numeric query key, a non-numeric extension key, a repeated key, a compressed URI, two primary keys in one element string.
- Every `Try` form with a destination one character short and exactly long enough.
- Input length: empty, one element, longer than any symbol holds.
- Symbols: GS1 QR Code in Standard QR and rMQR, a Digital Link URI in plain QR, the corpus photograph.

## Verification

- Fixtures from the oracles, regenerated by hand and committed, as the interop scope decision requires: the Syntax Engine 1.4.1 console app (prebuilt, pinned by version and SHA-256, fetched by a script as qrtool is), CodeGlyphX 2.1.0's validator and Digital Link builder, and libzint's `gs1` option read back through zxing-cpp with `TextMode.Plain`. Each fixture records the verdict, the output and the oracle's dictionary release.
- The input corpus: the General Specifications' and the Digital Link standard's examples, the Syntax Engine's unit-test vectors and the dictionary's linter tests if it has them (imported with provenance, Apache-2.0), one generated valid value per AI, and one mutation per check.
- `(AI)` text: `TryToHri` matches zxing-cpp's `TextMode.HRI` for every valid fixture whose AIs its table knows.
- Separator placement: `FromHri` matches libzint's element string for every input both accept.
- Round trips: `(AI)` text → element string → `(AI)` text, and element string → Digital Link → element string for every element string a URI can carry.
- Symbols, after the FNC1 plan's encode phases: zxing-cpp reads this library's GS1 QR Code and rMQR as `]Q3` / `ContentType.GS1` with `(AI)` text equal to `TryToHri`'s. The corpus photograph reads as the expected `(AI)` text in all four rotations.
- Costs: a small benchmark class for the six members on three typical element strings, recorded, not tuned. Allocation tests on the span forms. The trimmed size of a QR-encode-only application, unchanged, and of one that calls `FromHri`, recorded. The AOT analysis project calls every member with no warning. The Playground runs it under WebAssembly.

## Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Premises and fixtures, no `src/` change. G8 confirmed. The list linters' data source found and pinned. A pinned fetch of the Syntax Engine 1.4.1 console app, with its embedded dictionary release recorded. The input corpus and a `regenerate-gs1` command in `tools/QRInteropFixtures`. Whether Digital Link 1.7.0 depends on data newer than dictionary 2026-01-27. A size estimate of the table | Fixtures committed. Oracle disagreements listed with their cause |
| 1 | The API: names, signatures, XML summaries and the recipes for the three jobs, reviewed by the maintainer before code. G2, G4, G5 and G7 settled | The approved sketch recorded here |
| 2 | The table: the generator test, the generated table, the AI lookup, all internal | Drift test green. Size measured. The trimmed QR-encode-only application unchanged |
| 3 | Reading and checks: the split, the linters, the pairings, `TryValidate`, `EnumerateElements` | Verdicts match the Syntax Engine fixtures, disagreements listed by cause. No allocation. A fuzz run never throws |
| 4 | `(AI)` text: `TryToHri`, `FromHri` / `TryFromHri` | The `(AI)` text and separator checks above. Round trips |
| 5 | Digital Link: both directions | The Syntax Engine's Digital Link vectors and fixtures match. CodeGlyphX agrees where URI Syntax 1.6.0 and 1.7.0 agree |
| 6 | Symbols, after the FNC1 plan's Phases 3 and 4: the end-to-end tests, the compliance property, the corpus photograph, the benchmark class | zxing-cpp's reads match. Encode and decode benchmarks within noise |
| 7 | Playground (GS1 input as `(AI)` text, the Digital Link builder, the decoded AIs, on top of the FNC1 plan's encode selector), the documents below, the approved API listing, then this plan folded and deleted with its research file | The Playground publish green and checked by hand |

Phases 0-5 touch no symbol code and can run beside the FNC1 plan. Only Phase 6 waits for it.

## Documents to change

- [README.md](../../../README.md): a GS1 section. Which carrier to use (Digital Link for retail, GS1 QR Code elsewhere), the two-line recipes, leave `EciMode` at its default, rMQR's carrier status. No numbers.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the "GS1 data" section (the table and its update, the check strength per member, the carrier rules), scope rows (GS1 data in, compressed Digital Link, titles and scanner strings out, each with a revisit condition) and lessons.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the GS1 oracles, the Syntax Engine under the toolchain policy, the GS1 fixtures and what each oracle's release explains.
- [standardqr-encoder.md](../specs/standardqr-encoder.md), [rmqr-encoder.md](../specs/rmqr-encoder.md) and the two decoder records: a link to the GS1 section where they describe FNC1.
- [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md): the Out row for GS1 syntax and `(AI)` text, once G1 is decided. This plan does not edit it.
- The third-party notice file (G8) and the XML docs of the new members.

## Risks

- The table goes stale between releases. Tolerant reading keeps decoded data readable, and the yearly update is a checklist item.
- Oracles disagree because of their dictionary releases. Fixtures record the release so a disagreement is explained before it is fixed.
- The pairing check refuses data that is valid across carriers (G5). The revisit condition names it.
- Scope grows into titles, scanner strings or other symbologies. Each is an Out row with its condition.

## Progress log

### Investigation (2026-10-10)

Done: surveyed the GS1 General Specifications Release 26.0, Digital Link URI Syntax 1.7.0, the retail 2D guideline, the Syntax Dictionary 2026-01-27 and the Syntax Engine 1.4.1, and how zxing-cpp, libzint, BWIPP, CodeGlyphX, ZXing.Net, go-qr and qrcode2 handle the layer above FNC1. Split the work with the FNC1 plan: that plan owns the bit stream, this one owns everything above the element string.

Lessons:

- A GS1 plan that stops at FNC1 misses retail. The point-of-sale migration uses Digital Link URIs in plain QR, and GS1 QR Code is not a retail carrier.
- Reading GS1 data needs the AI table as much as checking it does, because predefined-length elements carry no separator. Reading can still accept newer AIs, because the tables that give an AI's length and the predefined lengths are frozen.
- A reference implementation's output shows its own dictionary release, not the standard. zxing-cpp's hand table and libzint's generated one are older than CodeGlyphX's, so a disagreement needs its release named before it is called a defect.
- Pairing rules are defined per item, not per symbol, so a per-symbol check can refuse valid data.

Benchmarks: not applicable, no `src/` change.
