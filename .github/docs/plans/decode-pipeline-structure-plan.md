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
2. **Shared outer passes.** One driver for positive, inverted, content verdict, regional and midpoint, over an attempt struct. The midpoint pass is a per-decoder switch, off for Standard QR (D1).
3. **Shared result rule and mirror retry.** One accumulator decides what settles and which failure is reported, holding each decoder's current rule (D2). One mirror retry, or a stated reason per decoder where the three strategies must stay apart: rMQR samples again because its grid is not square.
4. **Image context.** A `readonly ref struct` carrying the image, its size, threshold and grey levels (and rMQR's edge level) through the stages, and the module buffer owned by the scan, as Micro QR and rMQR already do.
5. **Matrix level.** The deinterleave and block loop shared by Standard QR and rMQR, as the inverse of the interleaver the encoders share. rMQR's format bit positions stated once. The sampler Micro QR borrows moves to `ImageDecoders/` if both keep it.
6. **`charsWritten` on failure.** The matrix span overloads report 0 when they return `false`, as the image overloads do, with a test per symbology and a line in the migration notes.

The guards come first because every later phase is judged against them. Phase 6 is independent and can land at any point before 2.0.0 (D4). D5, if taken, follows phase 3 as a change of its own.

## Decisions (2026-09-29)

- **D1, no midpoint pass for Standard QR.**
  - What: Micro QR and rMQR scan once more at the midpoint of the two grey levels, last, for a polarity whose global pass found no finder candidate. Phase 2's shared driver takes that pass as a per-decoder switch, off for Standard QR.
  - Why: nothing measured needs it. Standard QR's sweep read 4,799 of 4,800 bilinear renders at 2 to 3.5 px/module without it, and the one left was unread on `main` too; among Micro QR and rMQR only M1, R7x43 and R7x77 needed it ([standardqr-decoder.md](../specs/standardqr-decoder.md), image detection lessons; [image decode passes](../specs/qrcode-symbologies.md#image-decode-passes)).
  - Why not in this plan: adding it is an accuracy change. Mixed into a refactor, a difference in the sweep could not be told apart from a defect the refactor made.
  - Reopened only as its own change, measured by the sweep, for an input class that turns out to need it.
- **D2, both reporting rules kept through phase 3.**
  - What: when every attempt of a pass fails, Standard QR reports the first attempt of its main path (the selected triple, the corner its shape names, the estimated dimension, the first grid). Micro QR and rMQR report the failure that went furthest: a read too long for the destination, then a verdict on the content, then a failure past the format information, then one before it, then not detected; the first on a tie. Across passes, and across Micro QR's and rMQR's two scans, all three already report the first. Phase 3's shared accumulator holds both rules as two policies.
  - Why: this plan is a refactor, and which failure is reported is observable. A change of behavior in the same diff as a refactor cannot be traced to either.
  - Whether Standard QR moves to the furthest failure is D5, decided apart from this plan.

## Open decisions

- **D3, decoder options.** The generators take `in XxxGeneratorOptions`; the decoders take none. An options parameter placed before 2.0.0 freezes the API would let a caller trade passes for speed later without new overloads. It belongs to [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md), not here.
- **D4, when phase 6 lands.** It changes what a public `out` parameter holds on failure, so before 2.0.0 or not until 3.0.0.
- **D5, one reporting rule before 2.0.0.**
  - What it would change: Standard QR would report the failure that went furthest, as Micro QR and rMQR do, so `info` on failure means the same thing in all three decoders. Where a first grid fails at the format information and a later one reaches Reed-Solomon, the caller would get `DataUncorrectable` with the version and level, "a symbol is there and damaged", instead of `FormatInformationInvalid`.
  - Cost: it changes public behavior, so it needs a line in the migration notes, and the tests that pin a Standard QR failure status reviewed.
  - When: its own change after phase 3, where the shared accumulator makes it a choice of policy. Worth taking before 2.0.0 fixes the API.
  - Not considered: moving Micro QR and rMQR to the first attempt, which reports less.

## Measuring

- **Release only.** A Debug build of the library allocates where Release does not: a span initialized from a list of `int` or `float` values (`stackalloc float[] { … }`, `ReadOnlySpan<int> x = [ … ]`) allocated 72 B a call unoptimized and nothing optimized. A file-based script (`dotnet run script.cs`) referencing the library was measured Release only with `-c Release` on the command line; a `#:property Configuration=Release` directive alone still gave the 72 B.
- **Steady state.** Warm up first (the JIT, the static tables, the pools), then take the quietest of a few rounds of calls. The shared array pool can drop a buffer when a collection runs, so a single round can see one rent allocate. A call that allocates every time allocates in every round.
- **Alone.** A test that measures a decoder's allocations runs `[NotInParallel]`, as the repository's other allocation tests already did. The pool's thread slot holds one array a size, and a second array of that size comes from per-core stacks that tests running beside it drain.
- **A planted allocation has to escape.** `GC.KeepAlive(new byte[1])` is caught where `new byte[1].Length` at the same stage was not, on .NET 10: an array that never leaves the method need not be allocated at all.

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
