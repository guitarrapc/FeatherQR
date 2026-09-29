# Decode pipeline structure

## Purpose

The three generators run one pipeline shape. A fixed order of stages (prepare, encode, error correction, interleave, place), one record carrying what was resolved (`QRConfiguration`), and buffers owned by the method that runs the stages, each stage taking spans in and out. That shape is what lets a stage change without its neighbours noticing, and what keeps the allocation-free overloads allocation-free.

A review on 2026-09-28 held the decoders against that shape. The matrix level matches it: a fixed chain from the version to the bit stream, buffers taken once at the entry, and for Standard QR the encoder's own placement tables, so the two sides cannot disagree about which modules carry data. The image level does not, and is not meant to: it is a cascade of attempts, each tried when the one before it did not settle. Its stages are separate methods and the decoder classes list them in order. What each image decoder grew on its own is the scaffolding around the detection logic: the outer passes, the rule that decides which result is reported, the mirror retry, buffer ownership and the parameter lists that carry the image through every stage.

This plan first makes the allocation-free promise of every span overload a tested invariant, on each path the image level reads through and on failure, so that nothing after it can move an allocation onto a path unnoticed. It then shares the scaffolding the three image decoders already agree on, and the inverse matrix steps the encoders already share. Detection logic, read rates and the public API stay as they are, apart from one contract fix (phase 6).

When the plan completes, the durable decisions go into [qrcode-symbologies.md](../specs/qrcode-symbologies.md) (the shared decode scaffolding) and the three decoder records, and this file is deleted.

## Findings (2026-09-28)

| Area | Standard QR | Micro QR | rMQR |
|---|---|---|---|
| Outer passes (positive, inverted, regional, midpoint) | `QRImageDecoder.DecodeLuminanceAttempts`; no midpoint pass | `MicroQRImageDecoder.DecodeLuminanceAttempts` | The Micro QR method line for line, comments and one `NotDetected` aside |
| Which result is reported on failure | The first attempt's, by hand at each level: 16 `IsSettled` checks, about half copying `charsWritten` and `info` out | The best-ranked failure (`TrackBestFailure`, `Rank`) | The same ranking, a second copy |
| Mirror retry | One method transposes the grid in place and decodes again | A transposed view over the same grid, pasted beside each of five grids | The frame's axes swapped and the grid sampled again |
| Module buffer | Rented by each grid attempt (five sites), up to three of one size out at once. The thread's pool slot holds one array a size, so the others come from the per-core stacks every thread shares, and a thread decoding beside it can make the failure path allocate | One per scan, on the stack | One per scan, rented |
| What carries the image | No record: 29 signatures take `luminance, width, height, threshold` apiece, up to 16 parameters | The same | The same, plus an edge level |
| Matrix level | Deinterleave and the Reed-Solomon block loop | One block, no deinterleave | Its own fused deinterleave and block loop over the same `ECCInfo` |
| Inverse placement | The encoder's placement tables | A table built from the placer's predicates | A table built from the placer's predicates; the format bit positions written twice, matrix and image level |
| Dependencies | | Calls `QRImageDecoder.SampleGrid` directly | |
| `charsWritten` on failure | Image overloads promise 0 and keep it; matrix span overloads can return `false` with the segments decoded so far counted | The same | The same |
| Zero-allocation tests | Matrix span overload only | None | Matrix, image (clean), image (strong keystone) |

Measured in Release before phase 1, 1,000 calls after warm-up, every span overload of every symbology allocated nothing on clean inputs of several versions and on a failing blank image. rMQR averaged 3.7 B a call, sporadically; its existing single-call tests pass. So phase 1 adds guards only and changes no library code.

## Scope

| In | Out |
|---|---|
| Zero-allocation tests over every allocation-free decode overload, each path the image level reads through, and failure | Finder search, grids, budgets, thresholds: anything that changes which images read |
| One outer-pass driver, one result rule and one mirror retry, shared by the three image decoders where they already behave alike | A midpoint pass for Standard QR, or any other accuracy change (D1) |
| A record carrying the image and its threshold through the stages; one module buffer per scan | Decoder options on the public API (D3) |
| A shared deinterleave and Reed-Solomon block loop for Standard QR and rMQR; the rMQR format bit positions in one place; Micro QR no longer reaching into the Standard QR decoder | The generators, and SIMD tiers ([simd-128bit-tiers-plan.md](simd-128bit-tiers-plan.md)) |
| `charsWritten` of 0 on failure from the matrix span overloads | |

## What has to stay true

- **Reads unchanged.** Phases 2 to 5 are refactors. Every render and real image in `tools/QRImageDecodeSweep` reads as before, with the same status and corners, compared image for image against the commit before ([Image decode sweep](../specs/qrcode-test-fixtures.md)).
- **Diagnostics unchanged.** Which failure is reported, and its fields, is observable. A shared result rule carries each decoder's current rule (D2).
- **No allocation.** The phase 1 guards stay green in Release on both targets.
- **No slower.** `QRCodeImageDecodeEndToEnd`, `MicroQRDecodeEndToEnd`, `RmQRDecodeEndToEnd` and `QRCodeDecodeEndToEnd` stay within noise. Shared scaffolding takes the decoder's attempt as a struct type argument, as `RegionalRetry` does, so its calls stay direct.
- **Diagrams follow the code.** A refactor that keeps the stages and their order leaves the spec maps' decode diagrams, the pipeline remarks and the decode figures as they are. One that moves a stage updates all three in the same change ([authoring guidelines](../docs_authoring_guidelines.md)).

## Phases

1. **Allocation guards (done 2026-09-29).** One test class over the three decoders: the matrix span overloads with and without a quiet zone; the image span overloads on each path the image level reads through (upright, turned, mirrored, keystone, the next triple, the mesh first and after the anchored grid, the timing frame, low density, the anisotropic grid, the perspective search, light on dark, uneven light), and on failing inputs (noise, another symbology's symbol). Each input is one another test already pins to its path where one exists. Planted allocations, one per stage, show which stages the guard reaches (`tools/mutation_check.cs`).
2. **Shared outer passes (done 2026-09-29).** One driver for positive, inverted, content verdict, regional and midpoint, over an attempt struct. The midpoint pass is a per-decoder switch, off for Standard QR (D1).
3. **Shared result rule and mirror retry (done 2026-09-29).** One accumulator decides what settles and which failure is reported, holding each decoder's current rule (D2). One mirror retry, or a stated reason per decoder where the three strategies must stay apart: rMQR samples again because its grid is not square.
4. **Image context (done 2026-09-29).** A `readonly ref struct` carrying the image, its size, threshold and grey levels (and rMQR's edge level) through the stages, and the module buffer owned by the scan, as Micro QR and rMQR already do.
5. **Matrix level (done 2026-09-29).** The deinterleave and block loop shared by Standard QR and rMQR, as the inverse of the interleaver the encoders share. rMQR's format bit positions stated once. The sampler Micro QR borrows moves to `ImageDecoders/` if both keep it.
6. **`charsWritten` on failure (done 2026-09-30).** The matrix span overloads report 0 when they return `false`, as the image overloads do, with a test per symbology and a line in the migration notes.

The guards come first because every later phase is judged against them. Phase 6 was independent of the others and landed last, before 2.0.0 (D4). D5, if taken, follows phase 3 as a change of its own.

## Decisions

- **D1, no midpoint pass for Standard QR (2026-09-29).**
  - What: Micro QR and rMQR scan once more at the midpoint of the two grey levels, last, for a polarity whose global pass found no finder candidate. Phase 2's shared driver takes that pass as a per-decoder switch, off for Standard QR.
  - Why: nothing measured needs it. Standard QR's sweep read 4,799 of 4,800 bilinear renders at 2 to 3.5 px/module without it, and the one left was unread on `main` too; among Micro QR and rMQR only M1, R7x43 and R7x77 needed it ([standardqr-decoder.md](../specs/standardqr-decoder.md), image detection lessons; [image decode passes](../specs/qrcode-symbologies.md#image-decode-passes)).
  - Why not in this plan: adding it is an accuracy change. Mixed into a refactor, a difference in the sweep could not be told apart from a defect the refactor made.
  - Reopened only as its own change, measured by the sweep, for an input class that turns out to need it.
- **D2, both reporting rules kept through phase 3 (2026-09-29).**
  - What: when every attempt of a pass fails, Standard QR reports the first attempt of its main path (the selected triple, the corner its shape names, the estimated dimension, the first grid). Micro QR and rMQR report the failure that went furthest: a read too long for the destination, then a verdict on the content, then a failure past the format information, then one before it, then not detected; the first on a tie. Across passes, and across Micro QR's and rMQR's two scans, all three already report the first. Phase 3's shared accumulator holds both rules as two policies.
  - Why: this plan is a refactor, and which failure is reported is observable. A change of behavior in the same diff as a refactor cannot be traced to either.
  - Whether Standard QR moves to the furthest failure is D5, decided apart from this plan.
- **D4, phase 6 before 2.0.0 (2026-09-30).**
  - What: the matrix span overloads report no characters written on failure from 2.0.0, rather than waiting for 3.0.0.
  - Why: it changes what a public `out` parameter holds, which a major version is for, and 2.0.0 was still in preview (2.0.0-preview.3). No signature changes, so it is a behavior change with a section in the migration guide, not a break a compiler reports.

## Open decisions

- **D3, decoder options.** The generators take `in XxxGeneratorOptions`; the decoders take none. An options parameter placed before 2.0.0 freezes the API would let a caller trade passes for speed later without new overloads. It belongs to [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md), not here.
- **D5, one reporting rule before 2.0.0.**
  - What it would change: Standard QR would report the failure that went furthest, as Micro QR and rMQR do, so `info` on failure means the same thing in all three decoders. Where a first grid fails at the format information and a later one reaches Reed-Solomon, the caller would get `DataUncorrectable` with the version and level, "a symbol is there and damaged", instead of `FormatInformationInvalid`.
  - Cost: it changes public behavior, so it needs a line in the migration notes, and the tests that pin a Standard QR failure status reviewed. Standard QR's coverage re-read reports a skipped re-read as `DataUncorrectable` with no version, which has to become not tried first (see the phase 3 log).
  - When: its own change after phase 3, where the shared accumulator makes it a choice of policy. Worth taking before 2.0.0 fixes the API.
  - Not considered: moving Micro QR and rMQR to the first attempt, which reports less.

## Measuring

- **Release only.** A Debug build of the library allocates where Release does not: a span initialized from a list of `int` or `float` values (`stackalloc float[] { … }`, `ReadOnlySpan<int> x = [ … ]`) allocated 72 B a call unoptimized and nothing optimized. A file-based script (`dotnet run script.cs`) referencing the library was measured Release only with `-c Release` on the command line; a `#:property Configuration=Release` directive alone still gave the 72 B.
- **Steady state.** Warm up first (the JIT, the static tables, the pools), then take the quietest of a few rounds of calls. The shared array pool can drop a buffer when a collection runs, so a single round can see one rent allocate. A call that allocates every time allocates in every round.
- **Alone.** A test that measures a decoder's allocations runs `[NotInParallel]`, as the repository's other allocation tests already did. The pool's thread slot holds one array a size, and a second array of that size comes from per-core stacks that tests running beside it drain.
- **A planted allocation has to escape.** `GC.KeepAlive(new byte[1])` is caught where `new byte[1].Length` at the same stage was not, on .NET 10: an array that never leaves the method need not be allocated at all.
- **The baseline in a worktree of its own.** The commit before is checked out with `git worktree add --detach`, and the sweep, the corpus and the timings run there, so nothing built from the tree being changed leaks into the baseline.
- **Timing a refactor: the fastest of interleaved runs, not one short benchmark.** On this machine (Ryzen 9 7950X3D, 2026-09-29), measured as follows:
  - The repository's `ShortRun` job (three iterations) reported error bars as large as the mean on some shapes (Micro QR `M2_512px`, 5,613 ± 15,843 µs).
  - Three launches of fifteen iterations still moved one tree up to 23 % between two runs (Standard QR v40 at 3 px, 112.7 and 91.4 µs).
  - One process of the same build could be 2.5 times another on a 4 µs decode (Micro QR M4: 3.79 to 9.36 µs over eight processes).
  - A small harness run against both trees, alternating eight times and keeping each shape's fastest round, is what this plan judges by.
- **The corpus file is not byte-identical between runs.** zxing-cpp's column moved on 9 of 624 rows, all at rotation 0, with this library unchanged. Compare this library's columns (`compare` counts only them), not the whole file.

## Progress log

### Phase 1, allocation guards (2026-09-29)

- `DecodeAllocationTest` ([tests/FeatherQR.Tests/Shared](../../../tests/FeatherQR.Tests/Shared/DecodeAllocationTest.cs)), 57 cases a target:
  - the matrix span overloads of the three decoders at the smallest and a large version, with and without a quiet zone;
  - a symbol whose data in the lower half of the core is inverted, which fails at the Reed-Solomon stage;
  - the image span overloads on 10 Standard QR, 8 Micro QR and 9 rMQR scenes that read;
  - 4 rejected images for each decoder.
- The inputs pinned elsewhere are reused as drawn. Five test members were widened from private to internal for that: two in `KeystoneFinderDecodeTest` (its render and its content), and one each in `LargeVersionGridOrderTest`, `RmQRStrongKeystoneDecodeTest` and `UnevenLightingDecodeTest`.
- No library change. Every case allocated nothing in Release on .NET 8 and .NET 10, and the full suite passes on both.
- Reach. 47 allocations were planted with `tools/mutation_check.cs` on net10.0, one per stage: the passes, grids, mirror retries, coverage re-reads, the meshes, the other corners and triples, the other dimensions, the matrix level's bordered copy, rented work buffer and Reed-Solomon exit. All 47 were caught, 13 of them by a single test method.
- The first round caught 42 of the 47:
  - four stages had no input reaching them: the triple after the selected one, Micro QR's module boundaries, rMQR's anisotropic grid and its perspective search;
  - the fifth miss was the escape lesson under Measuring.
- The tests pinning the first, third and fourth supplied inputs.
- Micro QR's boundaries are reached by crisp M4 renders at 1.10 to 1.15 px/module at every offset tried. At 1.4 px/module the timing frame reads first, so that density stays as its own scene. Found by a probe over 1.05 to 1.70 px/module and four offsets.
- Running beside its own cases, the Standard QR decoder allocated 4,120 B on noise in every round, in one of six runs on .NET 8. Alone, it allocated nothing in every run. This is the nested module buffers of the findings table showing up, and it is why the class runs `[NotInParallel]`.
- The zero-allocation tests that predate the class stay where they are.

### D1 and D2 decided (2026-09-29)

- D1: no midpoint pass for Standard QR; phase 2's driver switches it off per decoder.
- D2: phase 3 keeps both reporting rules. Moving Standard QR to the furthest failure became D5, a change of its own after phase 3.

### Phase 2, shared outer passes (2026-09-29)

- `ImageDecodePasses` ([src/FeatherQR/Internals/ImageDecoders](../../../src/FeatherQR/Internals/ImageDecoders/ImageDecodePasses.cs)) runs the passes for the three decoders. Each decoder gives it `ISymbolPass<TInfo>` as a struct, which has four parts:
  - the global pass, which also returns the threshold, the grey levels and whether it found no finder candidate;
  - the regional pass, the `ILuminanceAttempt` that `RegionalRetry` already took;
  - the midpoint sweep;
  - `HasMidpointPass`, the D1 switch, off for Standard QR.
- Moved in with the driver: the midpoint's two conditions (grey levels, a midpoint off the threshold) and the rule that a decode that did not read reports no characters.
- Gone from the decoders: the three `DecodeLuminanceAttempts`, Micro QR's and rMQR's `DecodeAtMidpoint`, and the three `RegionalAttempt` structs. That is 278 lines out and 58 in across the three decoders, against 161 in the driver.
- The code differs in one place and the behavior in none. The negative's buffer is inverted again before its midpoint sweep only when that sweep runs; before, it was inverted whenever that polarity found no finder.
- `ImageDecodePassesTest` holds the architecture record's rules one by one against a recording pass, 30 cases a target:
  - the terminal results and verdicts of each pass, and which one is reported;
  - the regional pass after both global ones;
  - the midpoint pass per polarity: its switch, and a finder found, no grey levels or a midpoint on the threshold each skipping it;
  - the negative's midpoint read from its own pixels after the regional pass overwrote them;
  - the image's dimensions.
- The test did not compile without the driver, and passed against the driver before any decoder moved onto it. The case whose midpoint rounds to its threshold was found by a probe (levels 118 and 160, a ramp 38 px wide). The first family searched, 1,024 images with a ramp 8 px wide, had none.
- Reads unchanged:
  - The sweep's result files for all three symbologies (102,240 images) are byte-identical to those of the commit before.
  - The corpus is identical in every column this library writes, over 624 images.
  - The sweep records status but not corners. The corners come from the same functions as before, and the corner tests pass.
- The allocation guards pass, and so does the full suite (29,513 tests, both targets).
- Time, measured as the fastest of eight alternating runs of a harness over 18 shapes (success, light on dark, shadow, noise, gradient, another symbology), against both trees: every shape within ±2.5 %.
  - Micro QR M4 upright: 3.79 → 3.87 µs.
  - Standard QR noise 740 × 740: 2,891 → 2,938 µs.
  - rMQR noise: 25,243 → 24,887 µs.
- The repository's benchmarks with three launches of fifteen iterations, run twice each way, showed no direction beyond their spread.

### Phase 3, shared result rule and mirror retry (2026-09-29)

- `AttemptStatus` and `SearchResult<TInfo>` ([src/FeatherQR/Internals/ImageDecoders](../../../src/FeatherQR/Internals/ImageDecoders/SearchResult.cs)):
  - `AttemptStatus` holds the status classes once: terminal, content verdict, settled, past the format information, and how far an attempt got. Before, they were written out in the three decoders, the shared passes and `RegionalRetry`, with the furthest-failure ranking copied twice.
  - `SearchResult` holds which result a search reports, under the two rules of D2. `ReportRule.MainPath` is Standard QR's: at each level the main path's attempt, unless another settles. `ReportRule.Furthest` is Micro QR's and rMQR's: the failure that went furthest, the first on a tie.
  - Where a search stops stays with the decoder, since the decoders stop at different results: Standard QR at a settled one, Micro QR's and rMQR's scans only at a read.
  - D5 is now a change of one constant (`QRImageDecoder.Reporting`) plus its tests and migration note, and one detail. Standard QR's coverage re-read reports a re-read it skipped, because the grid samples what already failed, as `DataUncorrectable` with no version. A furthest rule would take that as the furthest failure, so D5 has to report the skip as not tried first.
- Standard QR marks each level's main path as such. The 16 hand-written "if this settles, copy it out and return" blocks became `Main` and `Other` calls on one result per level.
  - Two levels try an attempt ahead of their main path: the timing frame before the grids from the finders, and the mesh before the grid through the transform when nothing anchored the fourth corner. They pass it as `Other`, so it is reported only when it settles, as before.
  - `DecodeOtherGrid` now adds its grid and its coverage re-read to the caller's result, rather than taking the caller's status in and handing one back.
- Micro QR and rMQR pass one `ref SearchResult` where they passed a status and an info.
  - Micro QR's seven pasted "as sampled, then transposed" blocks became one helper, `DecodeBothWays`, over a grid type that carries its quiet zone and corners: affine, projective, or read off the module boundaries.
  - rMQR's two copies of the eight frames an axis pair makes became one function, `Frame`.
- The mirror retry stays three strategies, each for its own reason, now stated in the code and the architecture record:
  - Standard QR transposes in place, since its matrix level reads modules two at a time along the placement runs.
  - Micro QR reads through a transposed view, since its matrix level reads every module through one.
  - rMQR samples again with the axes swapped, since a transposed rMQR grid is no rMQR grid.
- `QRImageDecoder.DecodeWithMirrorRetry`'s `transposed` flag now means that the transposed grid read. It is used only to place the corners of a read, so the only change is to what the flag says about a verdict, which no caller read.
- `SearchResultTest` holds the two rules and the status classes, 28 cases a target. The rules:
  - the main path over any other failure, before or after it;
  - the first other failure when there is no main path;
  - a settled result taken, and the first settled one kept;
  - the furthest of every pair of statuses in both orders.
- The test failed to compile first. One expectation of mine was wrong: `Furthest` keeps the decoder's own not-detected diagnostics over a not-detected attempt, as Micro QR's and rMQR's rankings did. The test now says so.
- Reads unchanged:
  - The sweep's result files for all three symbologies (102,240 images) are byte-identical to the commit before, status included.
  - The corpus is identical in every column this library writes.
- The full suite passes (29,569 tests, both targets), allocation guards included.
- Decoders and shared code: 475 lines out and 276 in, against 137 for `SearchResult`.
- Time, against a worktree of the commit before, in the fastest of alternating runs:
  - Twelve runs over the 18 shapes: every shape within ±5 %.
  - Their medians moved up to ±36 % in both directions, from load outside the process: the slow runs were slow on one shape and not on the others.
  - Sixteen runs of the Standard QR shapes alone, where the medians had leaned 1 to 2 % slower: the fastest, the first quartile and the median all within ±1.2 %.
  - Version 6 at 4 px/module: 9.94 → 10.00 µs, median.

### Phase 4, image context and one module buffer a scan (2026-09-29)

- `ImageView` ([src/FeatherQR/Internals/ImageDecoders](../../../src/FeatherQR/Internals/ImageDecoders/ImageView.cs)) is a `readonly ref struct`: the pixels, their size, the threshold, the grey levels, and the level an edge is located at.
  - 35 stage signatures take it where they took five or six arguments: 14 in Standard QR (`ICornerAttempt` and its implementation included), 11 in Micro QR, 10 in rMQR.
  - The stages that measure keep their pieces, since tests call them and each says what it reads.
  - Standard QR's `SampleAndDecode` went from 16 parameters to 13; the rest are the grid's own and the results.
- rMQR's `level` parameter is gone. The view computes the edge level once from its threshold and grey levels. That is the value every pass passed: the midpoint pass passed the midpoint, which is what the edge level is whenever there are grey levels, and that pass runs only then.
- Standard QR's module buffers:
  - `ModuleWorkspace` holds one rented array a scan: three grids of the largest dimension reserved so far (first sampling, other grid, coverage re-read). It rents again only for a larger dimension, returning the old array first.
  - The timing frame, both meshes, the other grid and the coverage re-read take their grids from it.
  - It is a plain struct, not a `ref struct`; see the lesson in [standardqr-decoder.md](../specs/standardqr-decoder.md), Performance.
- Test first. `DecodeAllocationTest` counts the module buffers out at once per image (`QRImageDecoder.ModuleBuffersPeak`), 14 cases a target: 10 reads and 4 rejections.
  - On the old code, with only the counting added, the peak was 2 or 3 on the bowed symbol, the next triple and noise.
  - After the change it is 1, and 0 where no triple is found.
- `FinderTripleCornersTest`'s recording stand-in follows `ICornerAttempt`'s new signature.
- Reads unchanged: the sweep's result files are byte-identical and the corpus is identical in this library's columns. The full suite passes (29,597 tests, both targets).
- Time, fastest, first quartile and median of twelve alternating runs against a worktree of the commit before, on a quiet machine:
  - Micro QR and rMQR: all 11 shapes within ±1.2 % on the first quartile and the median. One fastest was 6 % faster (rMQR on a Standard QR symbol), with its median 0.7 % faster.
  - Standard QR's failing shapes: within ±0.6 %.
  - Standard QR's four reads: +0.3 to +1.3 %. Version 6 at 4 px/module went 9.75 → 9.88 µs and version 40 at 3 px/module 87.3 → 88.4 µs, fastest.
- Two experiments on the reads, eight alternations each:
  - without the thread-static counters;
  - with the workspace renting one grid instead of three, enough for a read.
  - Each still measured +0.5 to +1.0 % fastest, so neither is the cause. It was not traced further. It is within what moving code between methods shifts, and recorded here as this change's cost.

### Phase 5, matrix level (2026-09-29)

- `EccBlockDecoder` ([src/FeatherQR/Internals/BinaryDecoders](../../../src/FeatherQR/Internals/BinaryDecoders/EccBlockDecoder.cs)) is the Reed-Solomon block stage of Standard QR's and rMQR's matrix decoders:
  - it deinterleaves the stream into its blocks, the exact inverse of `BinaryInterleaver`;
  - it corrects each block up to a capacity the decoder passes, and stops at the first block that does not read;
  - it gathers the corrected data codewords over the front of the stream, in block order.
- What moved into it:
  - Standard QR's private deinterleave, unchanged, and its block loop.
  - rMQR's fused loop, which gathered each block from the stream by index and corrected it before gathering the next. The stream and its blocks now both sit on the stack, 464 bytes where the stream, one block and the data took 458. `RmQRMatrixDecoder.MaxBlockCodewords` and `MaxDataCodewords` went with the loop.
- The error count on failure is rMQR's rule: the blocks before the failing one, plus that block's own count when the capacity refused it. Standard QR's reported count does not change:
  - Reed-Solomon reports no count for a block it cannot correct.
  - Standard QR passes the full strength, ⌊ecc/2⌋, which Reed-Solomon never exceeds, so its capacity never refuses a block.
- rMQR's format information positions are stated once, in `RmQRConstants`:
  - `GetFormatBlock` gives the top-left module of the block of five rows by three columns that carries bits 0-14, and `GetFormatTail` the three modules beyond it;
  - `GetFormatModule` composes them into the module of one bit, and `IsFormatModule` gives the same modules as regions;
  - four places wrote the positions out before and now read them there: the placer, the function-module predicate, the matrix decoder and the image decoder.
- The two readers, the image decoder's and the matrix decoder's, walk the block with loops, as they did before. `GetFormatModule` a bit at a time measured slower:
  - The image decoder's reader runs for every frame tried. On rMQR noise, twelve alternating runs of the rMQR shapes: +0.9 % on the fastest, the first quartile and the median. With the helper marked for aggressive inlining: +0.9 %, +0.7 % and +0.8 %. With the reader's old loops back: −0.3 %, −0.1 % and −0.2 %. With loops over `GetFormatBlock` and `GetFormatTail`: −0.4 %, 0.0 % and −0.1 %.
  - The matrix decoder's reader runs for every grid the image decoder tries. A clean R17x139 matrix, 0.95 µs: +1.1 %, +2.1 % and +1.0 % a bit at a time; −1.1 %, −1.0 % and 0.0 % with the loops.
  - The placer writes the copies once per version and level, into a cached template, and asks for each bit.
- `PerspectiveGridSampler` ([src/FeatherQR/Internals/ImageDecoders](../../../src/FeatherQR/Internals/ImageDecoders/PerspectiveGridSampler.cs)) is `QRImageDecoder.SampleGrid` and its 256-bit and 128-bit tiers.
  - The methods moved whole, with their bodies unchanged, and the tier files moved with `git mv`.
  - Standard QR and Micro QR call it there, so Micro QR no longer references the Standard QR namespace.
  - Its row in the SIMD table is `PerspectiveGridSampler`, among the shared kernels; it was `QRSampleGrid`.
  - rMQR keeps its own sampler, since its grid is not square and it has an affine tier.
- Test first; each new test failed to compile before its code:
  - `EccBlockDecoderTest`, 465 cases a target. The deinterleave inverts the interleaver, and a clean stream reads, over 228 block structures: every Standard QR and rMQR version and level, and four outside the tables. Errors in every block are counted, an uncorrectable block reports the blocks before it, and a block over the capacity adds its own.
  - `RmQRFormatPositionTest`, 96 cases a target. Each bit's module is held against the naive reader in the tests, the regions against the bits, and the image decoder's reader bit by bit.
  - `SymbologyDependencyTest`, 12 cases a target, holds the dependency rule of the architecture record over the source.
  - The sampler's parity test moved with the sampler (`PerspectiveGridSamplerParityTest`).
- Reach, measured by planting faults by hand:
  - `tools/mutation_check.cs` needs the files it touches clean in git, and these were not, so a loop applied each fault, built and ran the relevant classes.
  - 12 faults: the deinterleave's group 2 offsets (2), the failure count, the capacity comparison, where the data is gathered, the format positions (4), the capacity Standard QR passes, and a reference to another symbology through a second `using` on one line and through an alias (2).
  - All 12 are caught. Two tests had to grow first:
    - Standard QR passing one less than the full strength was caught by nothing, since no test gave a Standard QR block exactly ⌊ecc/2⌋ errors. `QRCodeDecoderRoundTripTest.Decode_FullStrengthErrorsInEveryBlock_AreCorrected` does now, on versions 5-Q and 10-H, and catches it.
    - The dependency test first read only a `using` at the start of a line, which both reference faults get past. It now reads the namespace spelled out anywhere in code.
  - Five more faults after the block and tail split, all caught:
    - three by `RmQRFormatPositionTest`: the tail's row and column swapped in the image decoder's reader, the sub-finder block one column off, and that reader walking the block by rows;
    - two in the matrix decoder's reader, the sub-finder block walked by rows and the finder side's tail read on the sub-finder side, by `RmQRCodeDecoderRobustnessTest` alone. Either copy reads a symbol, so only a test that damages the other copy sees a fault in reading one.
- Reads unchanged:
  - The sweep's result files for all three symbologies (102,240 images) are byte-identical to those of phase 4 and of phase 1's baseline.
  - The corpus is identical in every column this library writes, over 624 images.
  - After the matrix decoder's reader moved to the loops above, the rMQR sweep and the corpus were run again: identical again.
- The full suite passes on the final tree, allocation guards included: 15,379 cases on .NET 10 and 15,366 on .NET 8. On each, 189 are skipped: the ARM64 tiers, on an x64 machine.
- Time, against a worktree of the commit before: the fastest, first quartile and median of twelve alternating runs of a harness over 23 shapes. The shapes are the 18 image shapes of phases 2 to 4, and five matrix decodes: Standard QR versions 6 and 40, version 40 with 150 modules flipped, and rMQR R17x139-H clean and with 20 flipped.
  - Image shapes: every one within ±1.4 % on all three measures, 15 of 18 within ±0.7 %.
  - rMQR gradient measured +0.4 to +1.4 % in all six rMQR-only runs, including one with the old matrix loop back and one with the old reader back, and −1.0 to −1.4 % in both full runs. It follows which shapes the harness runs, not the code.
  - Standard QR matrix decodes: within ±0.8 %, but for version 40's fastest, +1.9 %, whose first quartile and median were +0.4 and +0.5 %. Version 40 with 150 flips, where every block is corrected: −0.2 %, +0.1 % and 0.0 %.
  - rMQR matrix decodes, after the loops above: within ±1.1 %. Before them, the matrix with 20 flips measured +0.3 %, +0.9 % and +0.6 % with the shared stage, and +1.9 %, +2.5 % and +3.4 % with rMQR's old fused loop put back and nothing else changed: the shared stage is not slower than the loop it replaced.
  - The full run preceded the matrix decoder's reader moving to the loops; the rMQR numbers here are from a run of the rMQR shapes after it, and the rest of the code did not change between them.
- Stages and their order are unchanged, so the decode diagrams stay as they were. The spec maps' rows point at the shared code, and the architecture record lists `EccBlockDecoder` and `PerspectiveGridSampler` among the shared primitives, with the dependency test beside the rule.
- Library code: 274 lines in, 176 of them in the two new files, and 228 out.

### Phase 6, `charsWritten` on failure (2026-09-30)

- D4 decided: before 2.0.0, while the library is at 2.0.0-preview.3 (see Decisions).
- Before, found by a probe over every destination shorter than the text, the matrix span overloads failed with the first segment's characters counted:
  - Standard QR, "hello" then 60 digits (a Byte and a Numeric segment): 5 for every destination of 5 to 64 characters.
  - Micro QR, "1234567890ABCDEF": 10. rMQR, 20 digits then 8 capitals: 20.
- Each matrix decoder now sets `charsWritten` to 0 when the bit stream does not read, one line after `DecodeBitStream` in `QRMatrixDecoder`, `MicroQRMatrixDecoder` and `RmQRMatrixDecoder`.
  - Every earlier exit already wrote 0, so the rule holds on every failure.
  - The public matrix span overloads, the `string` overloads and the image decoders' grid decodes all go through those three methods.
  - The bit stream decoders still count what they wrote before a failure. Their tests read it, and the contract is the matrix level's.
- Test first: `MatrixDecodeCharsWrittenTest`, 8 cases a target.
  - For each symbology, a two-segment text: every destination shorter than it fails with `DestinationTooSmall` and 0 written, and a destination of its length reads. Each runs with and without a quiet zone, which is the copied core and the matrix in place.
  - For Standard QR, a Kanji cell with no mapping after a Byte segment fails with `UnmappedCharacter` and 0 written; a mappable cell in its place reads.
  - Seven cases failed before the change, each with the first segment's characters counted (5, 10, 20, and 2 for the Kanji cell).
- Docs:
  - The `charsWritten` of the three public matrix overloads says 0 on failure, as the image overloads' does, and that the destination may still hold what was read before it.
  - The architecture record's rule on characters written covers matrix decodes, with the date it changed.
  - The migration guide has a section of its own, and its 2.0.0 summary and table count three decode changes.
- Reads unchanged: the sweep's result files for all three symbologies are byte-identical to phase 4's, and the corpus is identical in this library's columns.
- The full suite passes: 15,387 cases on .NET 10 and 15,374 on .NET 8, 189 skipped on each (the ARM64 tiers).
- Time, matrix decodes only, since the change is one branch after the bit stream: twelve alternating runs against a worktree of the commit before, with a Micro QR M4 matrix added to the harness. All six shapes within ±1.2 % on the fastest, the first quartile and the median.
