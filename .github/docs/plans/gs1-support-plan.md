# GS1: element strings, `(AI)` text and Digital Link

## Purpose

The maintainer asked for GS1 support on 2026-10-10. [fnc1-support-plan.md](fnc1-support-plan.md) gives the encoders and decoders FNC1, so a GS1 element string can go into a symbol and come back out. This plan covers the layer above it: the AI table, the checks GS1 defines, `(01)…(17)…` text in both directions, Digital Link URIs, and how GS1's carrier rules meet the three symbologies. The [2.0.0 plan](featherqr-2.0.0-plan.md) first put "GS1 syntax validation and human-readable `(AI)` formatting, which need the GS1 AI table" out of scope. This plan is that table and what it costs, and since 2026-10-10 it ships with FNC1 encode (G1).

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

- No GS1 code exists. FNC1 is planned in [fnc1-support-plan.md](fnc1-support-plan.md): `Fnc1Mode` as `Fnc1` on the Standard QR and rMQR options and decode info, an element string with U+001D separators in and out, no GS1 syntax checks. Its decode ships in 2.0.0 on its own, and its encode ships with this layer (G1). Its decision 1 (the shape of `Fnc1Mode`) is still open, and the recipes below follow its current proposal.
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

Names and signatures are settled in Phase 1. The recipes use the FNC1 plan's decision 1. Its read members (`info.Fnc1.IsFirstPosition`) are public from FNC1 decode in 2.0.0. Its construction members (`Fnc1Mode.FirstPosition`, the options' `Fnc1`) go public with FNC1 encode, which is the release this layer ships in (G1). The shape follows the existing pair: builders look like the generators (a one-line form that throws, and a span form), readers look like the decoders (`Try`, with a string or a caller's span).

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

Each `Try` form exists twice, returning a `string` or writing into a caller's span with `charsWritten`, and the span form allocates nothing. The public surface is five types: the static class `Gs1`, `Gs1Error` and its kind enum, and the ref struct element and enumerator that keep reading allocation-free. They ship in their own package, `FeatherQR.Gs1` (G2, decided 2026-10-12). Its namespace and the entry type's name are settled in Phase 1: namespaces follow packages in this repository, and a type named `Gs1` inside a namespace that ends in `Gs1` collides with the namespace for callers in any `FeatherQR` namespace, so one of the two names moves.

### The table

- Source: the dictionary file of the pinned release, committed with its license and a provenance note under the test data. The generator lives in the test project, as `SimdTiersDocTest` does: it fails when the generated source differs from what the pinned release produces and rewrites it outside CI.
- Output: RVA data and a lookup by AI, with no allocation and no static constructor, like `ShiftJisKanjiTable`. Titles are not stored.
- The list linters (`iso3166`, `iso3166alpha2`, `iso3166999`, `iso4217`, `iso5218`, `mediatype`, `packagetype`) need data the dictionary does not hold. Phase 0 found it in the same repository at the same tag, `src/lint_*.c`: bit fields for the ISO 3166 numeric, alpha-2 and ISO 4217 codes and the media types, a 431-entry string list for the package types, and the `iso5218` and `iso3166999` rules in code. The seven files are committed beside the dictionary with the same provenance note.
- The generator refuses a dictionary that names a linter the code does not implement, so a release that adds one cannot be pinned without it.
- Update: once a year, after GS1's January release, as its own pull request that lists the release's GSCNs and the fixtures it changes.

### Separators and order

Building writes a separator only where the General Specifications require one (§7.8): after every element except the last, unless its AI's first two digits are in the predefined-length table. It never reorders elements. A caller who wants fewer separators puts the variable-length element last, and the docs say so. Reading accepts a redundant separator after a predefined-length element and a trailing one, both of which occur in transmitted data. Whether `TryValidate` reports them is settled against the Syntax Engine's verdict in Phase 3.

## What has to stay true

- The core packages are untouched: `FeatherQR` and `FeatherQR.SkiaSharp` gain no type, no dependency and no notice. An application that does not reference `FeatherQR.Gs1` is unaffected.
- The encoders and decoders are unchanged by this layer. Their benchmarks stay within noise.
- Every element string this layer builds, encoded with FNC1 in first position and default options, carries no ECI header and no Kanji segment.
- The span forms allocate nothing. Checked in Release only, since Debug builds allocate for `stackalloc` initializers.
- No `Try` form throws for any input text. Only a malformed option, such as a stem that is not a URI prefix, throws, as the existing `Try` APIs do.
- The generated table is exactly what the pinned release produces.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| G1 | Release | Decided 2026-10-10 (the 2.0.0 plan's D12): this layer ships in the same release as FNC1 encode ([fnc1-support-plan.md](fnc1-support-plan.md) Phases 3-6), not before it and not after it. That is 2.0.0 if both land before the Phase 7 freeze, 2.1.0 otherwise, and it does not block 2.0.0. An encoder that takes any element string without these builders lets a missing separator write a wrong symbol with no error. FNC1 decode ships in 2.0.0 on its own, since reading needs no GS1 layer |
| G2 | Package | Decided 2026-10-12: a fourth package, `FeatherQR.Gs1`, holding the whole data layer (the table, the checks, `(AI)` text and Digital Link in both directions). The maintainer chose it to keep the core's license posture untouched: the generated table is Apache-2.0-derived data, and in its own package the notice (G8) travels with the data and the core packages stay plain MIT. The package has no dependency on `FeatherQR` unless Phase 1 finds one it needs, since every member works on text, and the recipes compose it with the core's FNC1 option. FNC1 itself stays in the core, because the mode indicator and the `%` rule are bit-stream behaviour of the encoders and decoders, and FNC1 decode already shipped there. The costs the core split record weighed (a fourth package to version in lockstep, validate in `check_package_deps`, list in `PublicAPI.approved.txt`, reference from the Playground and document) are accepted. The size argument that first put the layer in the core (about 6 KB of data, measured in Phase 0) no longer decides anything. The earlier text of this row recommended the core and listed the package as the alternative, with a size threshold and two revisit conditions. License isolation was the third condition, unwritten |
| G3 | Table source and update | Decided 2026-10-12: generated from dictionary release 2026-01-27 by a test, as described above, updated once a year. A hand-written table was asked about and not chosen. The plan's scope table had placed it in the Out column as a recommendation, not a decision, and the Phase 0 record misread that as a rejection. Transcribing the same dictionary by hand produces the same derivative work under the same license, so it saves no notice. Transcribing the General Specifications instead would change the source to GS1's standards terms, but the ISO code lists and the 34 linter rules would then have to be re-sourced from the standard's text, and the 5,656 linter vectors the tests use are the dictionary project's, so the attribution stays either way. With the layer in its own package the notice costs the core nothing, which settled it |
| G4 | Encode entry point | No GS1 overload on the generators. The conversion and the FNC1 option compose with every existing overload (span, destination, sizing) on both symbologies, where an overload would need a twin of each |
| G5 | Check strength per member | Members that build data for a symbol run every check, pairings included. Members that read data from a symbol check only what they need and accept unknown AIs. `TryValidate` runs every check. Revisit when a caller needs to build data split across carriers, which the pairing check refuses |
| G6 | Unknown AIs | Read as variable length up to the next separator. Refused when building |
| G7 | Error shape | `Gs1Error` with a kind, an offset and a length into the input, so a caller can point at the bad element. The throwing forms throw `FormatException` with the same information, as parsers in the BCL do. No new exception type |
| G8 | License of the generated table | Decided 2026-10-12, with G2. `FeatherQR.Gs1` is MIT like the other packages (`PackageLicenseExpression` `MIT`) and carries a `THIRD-PARTY-NOTICES.md` packed into its nupkg, the way `Directory.Build.props` packs the README. The same file sits at the repository root, since the generated table lives in `src/`. It holds what Apache-2.0 section 4 asks for: the dictionary's three copyright lines (BWIPP project 2020-2021, Zint Project 2020-2021, GS1 AISBL 2021-2025), the release, the commit, the URL, a statement of what is derived (the AI table and the ISO 3166, ISO 4217, media and package type lists are generated from the dictionary and its reference linters, the linter rules are implemented in managed code and copy no C source) and the full Apache-2.0 text, because this repository's own LICENSE is MIT and cannot stand in for it. The dictionary repository has no NOTICE file (checked in Phase 0), so nothing else is owed. The generated source's header names the release, the commit and the notice file. `FeatherQR` and `FeatherQR.SkiaSharp` carry no notice. The Playground compiles the package into its WebAssembly bundle and links the notice from its page (Phase 7). The test fixtures' copy of the dictionary and the linter sources keep their own LICENSE and PROVENANCE.md beside them. The file is created in Phase 2 with the table it describes |
| G9 | Digital Link | Uncompressed, URI Syntax 1.7.0. Output in the canonical form. Input with any stem, percent-decoded, a repeated query key resolved last-wins. Non-numeric extension keys are ignored. A compressed URI is refused with its own kind. When an element string holds two primary keys, the choice follows the Syntax Engine, settled in Phase 5. Phase 0 found that 1.7.0 lists (7041) and (8040) to (8043) as data attributes while dictionary 2026-01-27 does not flag them `?`, and both dictionary-driven oracles refuse them in a URI. Phase 5 decides whether the table or the standard wins there. Every other key, qualifier and attribute of 1.7.0 is in the dictionary with the same flags, so 1.7.0 needs no data newer than the pinned release |
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
| 1 | The API: names, signatures, XML summaries and the recipes for the three jobs, reviewed by the maintainer before code. The package's namespace and entry type name (G2), G4, G5 and G7 settled | The approved sketch recorded here |
| 2 | The `FeatherQR.Gs1` project and package (lockstep version, the `check_package_deps` graph, its own `PublicAPI.approved.txt`, the notice file of G8), then the table: the generator test, the generated table, the AI lookup, all internal | Drift test green. Size measured. The `FeatherQR` and `FeatherQR.SkiaSharp` packages unchanged apart from the version |
| 3 | Reading and checks: the split, the linters, the pairings, `TryValidate`, `EnumerateElements` | Verdicts match the Syntax Engine fixtures, disagreements listed by cause. No allocation. A fuzz run never throws |
| 4 | `(AI)` text: `TryToHri`, `FromHri` / `TryFromHri` | The `(AI)` text and separator checks above. Round trips |
| 5 | Digital Link: both directions | The Syntax Engine's Digital Link vectors and fixtures match. CodeGlyphX agrees where URI Syntax 1.6.0 and 1.7.0 agree |
| 6 | Symbols, after the FNC1 plan's Phases 3 and 4: the end-to-end tests, the compliance property, the corpus photograph, the benchmark class | zxing-cpp's reads match. Encode and decode benchmarks within noise |
| 7 | Playground (GS1 input as `(AI)` text, the Digital Link builder, the decoded AIs, on top of the FNC1 plan's encode selector), the documents below, the approved API listing, then this plan folded and deleted with its research file | The Playground publish green and checked by hand |

Phases 0-5 touch no symbol code and can run beside the FNC1 plan. Only Phase 6 waits for it. This layer and the FNC1 plan's encode phases are released together (G1). If the 2.0.0 freeze comes first, both wait on a branch and merge after 2.0.0 is released.

## Documents to change

- [README.md](../../../README.md): the `FeatherQR.Gs1` install line beside the others, and a GS1 section. Which carrier to use (Digital Link for retail, GS1 QR Code elsewhere), the two-line recipes, leave `EciMode` at its default, rMQR's carrier status. No numbers.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the "GS1 data" section (the table and its update, the check strength per member, the carrier rules), the package topology (its scope row, the packing paragraph and "Why three packages" become four, with the license isolation as the reason the fourth exists), scope rows (GS1 data in, compressed Digital Link, titles and scanner strings out, each with a revisit condition) and lessons.
- `tools/check_package_deps.cs`: the fourth package's dependency group per target framework.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the GS1 oracles, the Syntax Engine under the toolchain policy, the GS1 fixtures and what each oracle's release explains.
- [standardqr-encoder.md](../specs/standardqr-encoder.md), [rmqr-encoder.md](../specs/rmqr-encoder.md) and the two decoder records: a link to the GS1 section where they describe FNC1.
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

### Release order (2026-10-10)

Done: the maintainer decided G1. This layer ships in the same release as FNC1 encode, and FNC1 decode ships in 2.0.0 on its own. [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (scope row, D12, Phase 6c) and [fnc1-support-plan.md](fnc1-support-plan.md) (phase exits, release paragraph) were updated in the same change.

Lesson: what decides the release is where a caller can go wrong. Reading GS1 data is safe without this layer. Writing it is not, because the encoder cannot tell a missing separator from a long value.

Benchmarks: not applicable, no `src/` change.

### Phase 0: premises and fixtures (2026-10-12)

Done: `get-gs1-syntax-engine.ps1` fetches the Syntax Engine 1.4.1 console app (pinned by SHA-256) and shallow clones of its source and of Syntax Dictionary release 2026-01-27 (pinned by commit). `import-gs1-syntax-dictionary` copies the dictionary, its LICENSE and the seven list-linter sources into `Fixtures/Gs1/syntax-dictionary/` with a provenance note that hashes each file. `regenerate-gs1` writes 1,248 cases (779 of this repository's corpus, 469 imported from the engine's unit tests) and 5,656 linter unit-test vectors, each case carrying the verdict of the Syntax Engine (run with the committed dictionary through `--syndict`), CodeGlyphX, libzint and the zxing-cpp reader. `Gs1FixtureTest` (17 tests, green) pins the dictionary's hash and release, the corpus shapes, one engine-valid case per AI and one engine-refused mutation per linter, the vector counts, and the imported vectors' agreement with their source or a named cause. The oracle disagreements are listed by cause in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md), and that record's claim that libzint validates check digits was corrected there.

The premises, item by item:

- G8: settled the same day with G2, in the next entry. Phase 0 found that the dictionary repository has no NOTICE file, so the Apache-2.0 attribution is its LICENSE text and the copyright lines of the dictionary header, which the provenance note already carries.
- The list linters' source is the dictionary repository at the same tag (the table section above). The generator of Phase 2 reads the bit fields and the string list from the committed sources, and the drift test can hash them.
- The Syntax Engine 1.4.1 console app embeds dictionary 2026-01-27 (its source copy differs from the release in one copyright-year line). The app also loads a dictionary file on the command line, so the fixtures never depend on the embedded table. The library reports its build date, May 28 2026, as its version string. Its console form is a stdin menu, one process per input, which `regenerate-gs1` drives in parallel in about ten seconds.
- Digital Link 1.7.0 needs no data newer than dictionary 2026-01-27, with the five attribute flags noted in G9.
- The table: 224 entry lines, 541 AIs, 79 distinct components, 70 distinct specifications, 32 distinct `req` and 79 distinct `ex` strings, 34 linters. As data: an AI index of about 1.1 KB, entries 1.1 KB, components 0.9 KB, specifications 0.2 KB, the pairing strings 0.9 KB as text, the ISO 3166, ISO 4217 and media type bit fields 0.35 KB and the package types 1.7 KB, about 6 KB in all, against the 16 KB Kanji table. The linters' code is not in that figure. G2 holds.
- The dictionary names 34 linters. An earlier count of 31 missed the three that sit on optional components (`hyphen`, `mi`, `ss`), whose bracket precedes the comma.

Lessons:

- An oracle's "validates" is a claim about its return value. libzint runs its linters and writes the symbol anyway, reporting a warning the ZXingCpp wrapper drops. The FNC1 corpus never gave it a bad check digit, so the claim stood until this corpus did.
- A reference implementation's unit tests test a layer. 49 of the 469 engine vectors get the opposite verdict from its console app, every one because the test calls the parser below the requisite check or sets a flag the app does not. Keeping the source's expectation beside the binary's verdict made the layer visible.
- The pairing rule, applied per symbol, refuses the General Specifications' own figures. Five of the 21 GenSpecs cases show an attribute without its key, as a second symbol on the same item would, and the engine refuses all five. That is the evidence for G5's revisit condition.
- A clone's working tree is not the upstream file. The first import hashed a CRLF copy that `core.autocrlf` had made, 344 bytes longer than the release.

Benchmarks: not applicable, no `src/` change.

### Package and license (2026-10-12)

Done: the maintainer decided G2, G3 and G8 together. The layer ships as a fourth package, `FeatherQR.Gs1`, so the Apache-2.0 notice for the generated table travels with that package and the core packages keep their plain MIT posture. The table stays generated from the pinned dictionary, and the notice is a `THIRD-PARTY-NOTICES.md` in the package and at the repository root with the attribution and the full license text, created in Phase 2 with the table. The decision rows, the design paragraph on the public surface, the invariant on the core packages, the Phase 1 and 2 rows and the documents list were rewritten in this change. No code changed.

The question that settled it was whether a hand-written table would avoid the notice. It would not: a transcription of the same file is the same derivative work, and re-sourcing from the General Specifications would move the lists and the 34 linter rules back to the standard's text while the tests still use the dictionary project's vectors. Isolating the notice in its own package was the cheaper answer.

Lessons:

- A scope table's Out column reads as a decision. The hand-written table sat there as the investigation's recommendation, the Phase 0 record then called it rejected, and the maintainer had never been asked. A recommendation in a scope row needs the word, or a decision row of its own.
- The size threshold in G2 answered a question nobody was asking. The core split had rejected a package for a saving in kilobytes, so the row argued kilobytes, and the reason that decided the package was the license posture, which the row listed nowhere.

Benchmarks: not applicable, no `src/` change.
