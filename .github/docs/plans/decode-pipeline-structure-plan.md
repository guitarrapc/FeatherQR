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
| Module buffer | Rented by each grid attempt (five sites) | One per scan, on the stack | One per scan, rented |
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
- **Diagnostics unchanged.** Which failure is reported, and its fields, is observable. A shared result rule carries each decoder's current rule until D2 decides otherwise.
- **No allocation.** The phase 1 guards stay green in Release on both targets.
- **No slower.** `QRCodeImageDecodeEndToEnd`, `MicroQRDecodeEndToEnd`, `RmQRDecodeEndToEnd` and `QRCodeDecodeEndToEnd` stay within noise. Shared scaffolding takes the decoder's attempt as a struct type argument, as `RegionalRetry` does, so its calls stay direct.
- **Diagrams follow the code.** A refactor that keeps the stages and their order leaves the spec maps' decode diagrams, the pipeline remarks and the decode figures as they are. One that moves a stage updates all three in the same change ([authoring guidelines](../docs_authoring_guidelines.md)).

## Phases

1. **Allocation guards.** One test class over the three decoders: the matrix span overloads with and without a quiet zone; the image span overloads on each path the image level reads through (upright, turned, mirrored, keystone, the mesh first and after the anchored grid, low density, light on dark, uneven light), and on failing inputs (noise, another symbology's symbol). Each input is one another test already pins to its path where one exists. Planted allocations, one per stage, show which stages the guard reaches (`tools/mutation_check.cs`).
2. **Shared outer passes.** One driver for positive, inverted, content verdict, regional and midpoint, over an attempt struct. Standard QR sits it out of the midpoint pass until D1 says otherwise.
3. **Shared result rule and mirror retry.** One accumulator decides what settles and which failure is reported, holding each decoder's current rule (D2). One mirror retry, or a stated reason per decoder where the three strategies must stay apart: rMQR samples again because its grid is not square.
4. **Image context.** A `readonly ref struct` carrying the image, its size, threshold and grey levels (and rMQR's edge level) through the stages, and the module buffer owned by the scan, as Micro QR and rMQR already do.
5. **Matrix level.** The deinterleave and block loop shared by Standard QR and rMQR, as the inverse of the interleaver the encoders share. rMQR's format bit positions stated once. The sampler Micro QR borrows moves to `ImageDecoders/` if both keep it.
6. **`charsWritten` on failure.** The matrix span overloads report 0 when they return `false`, as the image overloads do, with a test per symbology and a line in the migration notes.

The guards come first because every later phase is judged against them. Phase 6 is independent and can land at any point before 2.0.0 (D4).

## Open decisions

- **D1, a midpoint pass for Standard QR.** Micro QR and rMQR sweep again at the midpoint of the grey levels for a polarity that found no finder; Standard QR does not. Adding it is an accuracy change, measured by the sweep, and outside this plan. Default: the shared driver takes it as a per-decoder switch, off for Standard QR.
- **D2, which failure is reported.** Standard QR reports the first attempt's diagnostics, Micro QR and rMQR the best-ranked failure. Unifying them changes `info` on failure. Default: keep both, one rule with two policies.
- **D3, decoder options.** The generators take `in XxxGeneratorOptions`; the decoders take none. An options parameter placed before 2.0.0 freezes the API would let a caller trade passes for speed later without new overloads. It belongs to [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md), not here.
- **D4, when phase 6 lands.** It changes what a public `out` parameter holds on failure, so before 2.0.0 or not until 3.0.0.

## Measuring

- **Release only.** A Debug build of the library allocates where Release does not: a span initialized from a list of `int` or `float` values (`stackalloc float[] { … }`, `ReadOnlySpan<int> x = [ … ]`) allocated 72 B a call unoptimized and nothing optimized. A file-based script (`dotnet run script.cs`) referencing the library was measured Release only with `-c Release` on the command line; a `#:property Configuration=Release` directive alone still gave the 72 B.
- **Steady state.** Warm up first (the JIT, the static tables, the pools), then take the quietest of a few rounds of calls. Tests run in parallel, and the shared array pool can drop a buffer when a collection runs, so a single round can see one rent allocate. A call that allocates every time allocates in every round.

## Progress log
