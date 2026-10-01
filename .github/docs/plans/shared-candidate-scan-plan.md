# Shared candidate scan for Micro QR and rMQR

## Purpose

Micro QR and rMQR read an image the same way around their single finder:
- a strided finder scan, then a full sweep when nothing read;
- the first eight candidates, most confirmed first;
- for each candidate, every grid decoded through the matrix level, and a terminal result ending the candidate;
- a read that does not fit the destination keeping its corners, so that later candidates inside it are skipped;
- on an image with grey levels, a grid past its format information that neither read nor read too long for the destination read again by coverage.

Since 2026-09-29 the passes around the scan are shared (`ImageDecodePasses`), and so are the status classes and the report rule (`AttemptStatus`, `SearchResult`). The scan and the per-grid rules are not:
- Each decoder has its own `DecodeLuminanceCore` and `DecodeLuminanceScan`. They match line for line except for their scratch buffers and their info type.
- Each decoder handles its grids in its own way.

The copies drifted. The adversarial review of the shared pipeline (#442, 2026-10-01) found:
- rMQR still read a grid again by coverage after a read that did not fit, which Micro QR had stopped doing on 2026-09-30. It cost up to 2.8 times a sized call, and on a crafted image it returned another text as a success.
- Micro QR had no test for that rule at all.
- Several rules were tested in one decoder and not in the other.

Each of these was fixed by hand, in both decoders.

This plan does three things:
1. It states every per-candidate rule once, as a contract that both decoders are tested against.
2. It moves the scan into one place.
3. It moves the per-grid rules into routines that both decoders call.

When it completes, the rules are recorded once in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the per-symbology records keep only what differs, and this file is deleted.

## Scope

| In | Out |
|---|---|
| The scan: strided then swept, tried candidates not decoded again, the ranking, the first eight, the skip inside a read that did not fit, a result per candidate, corners stripped before reporting | Standard QR: three finders, its triple selection and its main-path report rule (D6) |
| The per-grid rules: a terminal read ends the candidate and carries its corners, failures are kept under the furthest rule, the coverage gate and the coverage re-read | Each decoder's own grid search. Micro QR: sizes, the timing frame, module boundaries, the arbitrary-orientation path, the scale and perspective searches. rMQR: frames, the outline, format copies, the sub-finder, the perimeter trace, the perspective search |
| A contract test that both decoders run, and planted faults for every rule in both | Any rule change. No decode result changes anywhere |
| A short-destination measurement in `tools/QRImageDecodeSweep`, so that the cost figures in the specs can be reproduced | The budgets keep their units (D4) |
| | Public API, output and allocations: none change |

## Inventory

As of 2026-10-01 (3e1f29d).

| Piece | Micro QR | rMQR | Differs in |
|---|---|---|---|
| Strided scan, then a sweep when nothing read. The sweep skips the candidates already tried. `noFinder` for the midpoint pass | `DecodeLuminanceCore` | `DecodeLuminanceCore` | The info type only |
| Find and rank. Take the first eight, skipping candidates already tried and candidates inside a read that did not fit. A `SearchResult` per candidate. Corners stripped | `DecodeLuminanceScan` | `DecodeLuminanceScan` | Scratch: Micro QR stack-allocates a 17 × 17 grid and two 18-entry boundary tables per scan. rMQR rents a 2,363 B grid per scan and stack-allocates its orientation candidates. Also the not-detected info |
| A terminal read ends the candidate | Six grid sites (per size, timing frame, module boundaries, arbitrary orientation, scale search, perspective search) and four coverage sites | `Attempt`, `TryBoundaryFrame`, and the return of each frame stage | The shape of each pipeline |
| A terminal read carries its corners | `DecodeBothWays`, as sampled, and transposed for a mirrored capture | `Attempt` (the grid, and its coverage re-read), `TryBoundaryFrame` (the boundary outline) | Micro QR's transposed corners |
| The coverage gate | `ShouldReadByCoverage`: grey levels, past the format information, and the format word read exactly in an orientation that got there. A terminal read is excluded by the caller returning first | `Attempt`: grey levels, past the format information, not terminal, budget left | The exact-word condition. Micro QR's single format copy lets 7 to 21 % of random grids past; rMQR's two copies let about 1.5 % past |
| The coverage re-read | `DecodeByCoverage`: resample at the module centres, then decode both ways only when a module changed | Inline in `Attempt`: the same, in one orientation | The orientations |
| The budget | 10,000 decodes on the arbitrary-orientation path, two per grid. The re-read is not counted | 256 decodes per candidate. The re-read is counted whether or not it decodes | The units |

Since phase 2 the first two rows are `CandidateScan` (`Decode`, `Scan`, `DecodeRanked`) in `Internals.ImageDecoders`, and each decoder's part is a `CandidateDecoder` struct: its candidate decode, its not-detected info, its corner access and its module buffer length.

Tests: since phase 1, `DestinationContractTest` runs each rule on both decoders (the Progress log has what it replaced), beside `SymbolGeometryContainsTest` and `SearchResultTest`. No known input reaches the terminal return in Micro QR's scale and perspective searches, and rMQR's frame and scale loops have a bounded cost (Progress log, phase 1).

## What has to stay true

- **No read, status, version or corners change.**
  - Check the image decode sweep (`micro`, `rmqr`) and the real-image corpus with `compare`, image for image.
  - Check the short-destination render sets (Measuring) the same way: status and version for a sized destination, one character short, and two characters.
- **No short-destination cost regresses.** Over each render set, the median and the 95th percentile stay within noise of phase 1's figures, and the worst within the spread two runs of one tree show (1.05 to 1.25 times a sized call for Micro QR, 1.15 to 1.60 for rMQR, one character short).
- **Calls stay direct and nothing allocates.**
  - What a decoder supplies is a struct behind a generic constraint, as in `ImageDecodePasses`. There are no delegates, and nothing is boxed.
  - `DecodeAllocationTest` stays green in Release.
  - The image decode benchmarks (`MicroQRImageEndToEnd`, `RmQRImageEndToEnd`) stay within noise, and never exceed the repository's +10 % bar.
- **All four targets compile.**
  - netstandard2.0 has no `allows ref struct` and no static abstract members. The decoder's part is therefore instance members on a struct.
  - A workspace passed by `ref` beside spans must not be a `ref struct` (CS8350; [standardqr-decoder.md](../specs/standardqr-decoder.md)).
- **One module buffer at a time.** A scan holds one module buffer at a time, or the pool's shared stacks decide whether it allocates (the same record).
- **The shared code names no symbology.** It lives in `Internals.ImageDecoders` and reaches each decoder through generics. `SymbologyDependencyTest` stays green.
- **Each decoder keeps what is its own.** The grid searches, Micro QR's transposed retry and exact-word gate, rMQR's mirrored frames, and both budgets stay with their decoders.
- **Every rule is pinned in both decoders.** A planted fault in either decoder fails the contract test for that decoder.

## Measuring

- **Reads.** `tools/QRImageDecodeSweep` `sweep micro`, `sweep rmqr` and `corpus`, before and after, with `compare`.
- **Short destinations.** `destination` in the same tool, and `compare-destination` for two trees (phase 1; [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#image-decode-sweep)).
  - It renders the sets behind the figures in the specs: 600 Micro QR and 600 rMQR renders from fixed seeds, turned and noisy, of which 577 and 585 have a symbol, plus the renders known to rank a finder-like pattern inside the symbol.
  - Each render is decoded with a sized destination, one character short and two characters.
  - It records the status, version and time of each decode, and the ratio of each short decode to the sized one.
  - Until phase 1 these figures came from a script outside the repository, and a reviewer could not reproduce "the mean went from 1.05 to 1.00".
- **Planted faults.**
  - [shared-candidate-scan-mutants.tsv](references/shared-candidate-scan-mutants.tsv), for `tools/mutation_check.cs` with `--filter "/*/*/DestinationContractTest/*"`, holds one fault per rule in each decoder: no corners on a read that did not fit; only the first read's corners kept; a read that did not fit not ending the candidate at each grid site; a coverage re-read after such a read; the skip removed; corners reported on a failure; the scan stopping at such a read; one result shared by every candidate.
  - It runs before and after each phase. Every fault must be caught, apart from the ones phase 1 records with their reason.
- **Benchmarks.** `--filter "*MicroQRImageEndToEnd*Decode*" "*RmQRImageEndToEnd*Decode*"`, against the commit before each phase, run in alternation.

## Phases

Each phase appends a Progress log entry: what was done, the lessons, the `compare` results and the benchmark delta.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Contract and measurement | One contract test class, parameterized by symbology through a small adapter per decoder (D5). It covers: one character short reports the sized call's read; the cost is about a sized call's; a read that does not fit is not read again by coverage, at each grid kind that can reach it; the skip, for one symbol and for two; another symbol that fits is still read; corners only on a read. It replaces the per-decoder duplicates. Also the `destination` command, and the mutants file | Every rule × decoder has a test. Every planted fault is caught, or recorded with its reason: unreachable, or bounded below what a timing test can hold. Baseline figures are in the log |
| 2 | Shared scan | `DecodeLuminanceCore` and `DecodeLuminanceScan` move into one generic scan in `Internals.ImageDecoders`. Each decoder supplies its candidate decode, its not-detected info and its corner access on a struct (D1, D2) | What has to stay true holds. Both decoders' scan methods are gone |
| 3 | Shared grid read | One routine decodes a sampled grid, keeps a failure under the furthest rule, applies the coverage gate (a shared core, plus a predicate supplied by the decoder: D3) and re-reads by coverage, decoding only when a module changed. It reports what it spent, so each decoder charges its own budget (D4). Micro QR's coverage sites and rMQR's `Attempt` call it | As phase 2. Neither decoder has its own coverage loop or gate core left |
| 4 | Fold | The rules move to [qrcode-symbologies.md](../specs/qrcode-symbologies.md), stated once. The decoder records and spec maps keep the differences and link the shared code. The plan is deleted | The specs, the code and the contract test agree |

Phase 2 can stop early: if the generic scan costs more than noise on the benchmarks, or a decoder has to supply more than its candidate decode, its not-detected info and its corner access, phase 1's contract remains as the guard and the reason is recorded. Phase 3 is judged the same way on its own. Each phase can be its own PR.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Who owns the scratch buffers | **Decided 2026-10-01 (phase 2).** The shared scan holds one module buffer per scan, sized by a constant the decoder supplies: stack-allocated for Micro QR's 289 B, rented for rMQR's 2,363 B, as each is today. The small tables (Micro QR's boundary tables, rMQR's orientation candidates) are stack-allocated inside each decoder's candidate decode. A struct cannot hold a span without being a `ref struct`, and netstandard2.0 cannot pass one as a generic argument. The image decode benchmarks held within their error bars (Progress log, phase 2) |
| D2 | How the shared scan reads and strips a result's corners | **Decided 2026-10-01 (phase 2).** The decoder's struct supplies both. An internal interface on the public info records would also work, but it changes public types to serve internals |
| D3 | Whether the coverage gate becomes one condition | The shared core is grey levels, past the format information and not terminal. Micro QR adds its exact-word predicate, whose reason is measured: texture reads a word within 3 bits about half the time, and an exact one about 1 in 1,000. rMQR adds none |
| D4 | Whether the budgets are unified | **No.** They are tuned in different units to measured envelopes: Micro QR's to fit one complete frame, rMQR's per candidate across all frames. The shared routines report what they spent, so each decoder charges as it does now. Revisit only with a measured reason |
| D5 | How the contract test reaches each decoder | **Decided 2026-10-01 (phase 1).** Through the public `TryDecodeImage`, since every rule is observable there, with an adapter that renders and decodes per symbology. Internals are used only for premises, such as the ranking and the crafted grids. Timing rules keep their bounds of 3 or 5 times a sized call plus 30 ms, with the margins measured in phase 1. A deterministic observable is used wherever one exists |
| D6 | Standard QR | **Out of scope.** Its coverage re-read sits behind its own triple and mesh paths and its main-path rule. Revisit only if phase 3's routine fits it with no change to that rule |

## Progress log

### Phase 1: contract and measurement (2026-10-01)

**Done**
- `DestinationContractTest`: seven rules, each one test method run on both decoders through `SingleFinderDecoder`, 152 cases.
  - The renders live in `MicroQRDestinationRenders` and `RmQRDestinationRenders`.
  - It replaces `MicroQRCodeDecoderDestinationTest`, `RmQRCodeDecoderDestinationTest` and the four destination tests in `RmQRCodeDecoderImageTest`.
  - New for rMQR: a destination one character short, over every version at 0°, 30° and 90°. Before, only Micro QR had this check over a set of renders.
  - New for Micro QR: the cost of a read made through the timing frame, the module boundaries and the coverage re-read.
- `tools/QRImageDecodeSweep`: the `destination` and `compare-destination` commands.
- `tools/mutation_check.cs`: a `␤` in a fault's text stands for a line break, so that a line repeated elsewhere in its file is found together with the line beside it.
- [shared-candidate-scan-mutants.tsv](references/shared-candidate-scan-mutants.tsv): 28 faults, 15 in Micro QR and 13 in rMQR.

**Baseline** (`destination`, this machine, one character short against a sized call)

| Set | Read | As the rules say | Short / sized: median, 95 %, worst | 2 characters / sized: median, 95 %, worst |
|---|---|---|---|---|
| Micro QR, random | 577 | 577 | 0.99, 1.02, 1.05 to 1.25 | 0.99, 1.01, 1.49 |
| Micro QR, known | 4 | 4 | 0.98 to 1.00 each | 0.96 to 1.00 each |
| rMQR, random | 585 | 585 | 0.99, 1.01 to 1.09, 1.15 to 1.60 | 0.99, 1.09, 1.77 |
| rMQR, known | 3 | 3 | 0.98 to 1.03 each | 0.99 to 1.46 each |

Ranges are the spread of two runs of one tree. The "2 characters" columns leave out the texts of 2 characters, which fit.

**Planted faults**

24 of the 28 are caught, each by the contract test. The baseline run catches nothing. The 4 that are not:
- **`MQ-site-scale` and `MQ-site-perspective`: no input found that reaches them.**
  - These are Micro QR's scale and perspective searches. They open only after an affine grid gets past its format information without reading.
  - With either fault planted, no render showed a cost above the spread of the unchanged decoder:
    - 5,760 turned renders (0 to 28 grey levels of noise);
    - 4,320 renders with a keystone of 3 to 15 %;
    - the two `destination` sets.
- **`RQ-site-frame` and `RQ-site-scale`: reached, but bounded.**
  - A read that does not fit, let go on by rMQR's frame loop or by a frame's scale loop, costs at most 3.4 and 2.7 times a sized call (the R13x27 grid render; 1.9 on the module-boundary render).
  - The other frames of that pass, or the other scales of that frame, read no format copy, and the next return ends the candidate at the same read.
  - A timing bound would have to sit under 3 times a sized call, and such a bound flakes.

**Lessons**
- **A premise holds per case, or the case tests nothing.**
  - rMQR's "another symbol fits" with the small symbol drawn first had been passing without testing the rule: the small symbol was tried first, and read.
  - Drawn at 4 px/module against the big one's 6, the small one is tried second in both orders.
  - Every case now asserts that the big symbol's candidate is tried first.
- **A fault at one grid site is masked by the next site.**
  - When a later site of the same candidate reads the symbol again, it ends the candidate there, so the fault costs a few grids rather than the search.
  - Catching it needs a render that reads only at that site. Planting the fault and timing a set of renders found one for each Micro QR site that had none:
    - the timing frame, 27 times a sized call;
    - the module boundaries, 58;
    - the coverage re-read, 56.
- **The tool's sets are the records' sets.** It draws the same 577 and 585 symbols the decoder records' figures were measured on.
  - Their worst one-character-short ratio moves between runs of one tree, by as much as 1.05 against 1.25.
  - What has to stay true therefore holds the median and the 95th percentile, and holds the worst only to that spread.

### Phase 2: shared scan (2026-10-01)

**Done**
- `CandidateScan` in `Internals.ImageDecoders` has three parts: the strided scan and then the sweep (`Decode`), one scan (`Scan`), and the decode of the ranked candidates (`DecodeRanked`).
  - It replaces both decoders' `DecodeLuminanceScan` and the body of their `DecodeLuminanceCore`.
  - `DecodeLuminanceCore` stays as a one-line entry, for the regional pass and the tests.
- Each decoder's `CandidateDecoder` struct (`ICandidateDecoder<TInfo>`) supplies four things: its candidate decode, its not-detected info, its corner access, and its module buffer length (D1, D2).
- The scratch buffers:
  - The scan holds one module buffer, as before: 289 B from the stack for Micro QR, 2,363 B rented for rMQR.
  - Micro QR's boundary tables and rMQR's orientation candidates moved into the candidate decode, so they are now taken per candidate instead of per scan.
- `CandidateScanTest`: the scan's rules over ranked candidates and a scripted decoder, 15 cases. It was red before `CandidateScan` existed.
- The mutants file now has 25 faults:
  - The five scan faults that each decoder had are now one each, in `CandidateScan`.
  - Two faults are new: trying more than the first eight, and trying a candidate again.
  - The run adds `CandidateScanTest` to its filter.
- The spec maps link `CandidateScan` and `CandidateScanTest`, and `qrcode-symbologies.md` lists `CandidateScan` among the shared components.

**What has to stay true**
- **Reads.** The image decode sweeps wrote byte-identical result files: 18,400 Micro QR images and 29,440 rMQR images. In the real-image corpus, only zxing-cpp's column differs, and it moves between runs.
- **Short destinations.** No status, version or text moved over 581 Micro QR and 588 rMQR renders. One character short:
  - the median was 0.99 times a sized call for Micro QR and 1.00 for rMQR, before and after;
  - the worst was 1.19 and 1.25, within the spread.
- **Planted faults.** 21 of 25 were caught, including all seven in the scan. The four recorded in phase 1 are unchanged, and the baseline run caught nothing.
- **Benchmarks** (net10.0, three rounds each with alternating order, allocations unchanged):

  | Benchmark | Before (µs) | After (µs) | Change |
  |---|---|---|---|
  | rMQR R7x43 | 5.43 | 5.48 | +0.9 % |
  | rMQR R17x139 | 21.76 | 21.92 | +0.7 % |
  | Micro QR M4 | 4.91 | 4.72 | −3.9 % |
  | Micro QR M1 under shadow | 12.48 | 12.91 | +3.4 % |
  | Micro QR, turned M1 failing | 2,799 | 2,773 | −0.9 % |
  | Micro QR on a Standard QR image | 14,165 | 13,813 | −2.5 % |

  Every change is within the error bars of 0.1 to 1.3 µs and 2 to 4 %.
- **Builds and tests.** The library builds for all four targets with no warning. The full suite passes on both, `DecodeAllocationTest` and `SymbologyDependencyTest` included.
- **The stop conditions did not apply.** Each decoder supplies its candidate decode, its not-detected info and its corner access, plus the buffer length D1 called for.

**Lessons**
- **The order of an A/B run can manufacture a regression.**
  - Two rounds that ran the base first each time put rMQR's decode 15 to 21 % behind. Even Micro QR's failing paths moved 16 to 25 %.
  - Three rounds with error bars, the order alternated, put every benchmark within them.
  - A machine that drifts during a run favours whichever tree runs first.
- **The mutation tool refuses a file with changes of its own,** since `git checkout` would not restore it.
  - A phase's own change is checked with `--allow-dirty`, after copying the files aside.
  - Checksums after the run confirm the copies match.
