# Reading a large Standard QR symbol from an image faster

## Purpose

The matrix decode plan took a version 40 symbol from 170 us to 15 us and was folded into [standardqr-decoder.md](../specs/standardqr-decoder.md). It stopped where it did for one reason, measured on its last day: a symbol is read from an image far more often than from a module matrix, and the matrix decode is 2 to 4 % of an image decode. Whatever is left in the matrix path is worth at most that.

Where an image decode goes, x64, one version 40 Structured Append symbol and one version 6 URL, rendered synthetically (k x k pixels a module, hard edges, 4-module quiet zone, no rotation: the image path's best case), `QRCodeDecoder.TryDecodeImage` on the luminance, us:

| | Image decode | Otsu threshold | Finder search | Rest: dimension, mesh, sampling, retries | Matrix decode |
|---|---:|---:|---:|---:|---:|
| Version 40, 3 px a module, 555 x 555 | 430 to 490 | 220 to 232 | 65 to 125 | 78 to 146 | 14.5 |
| Version 40, 4 px a module, 740 x 740 | 420 to 600 | 123 to 147 | 101 to 178 | 148 to 362 | 14.5 |
| Version 40, 8 px a module, 1480 x 1480 | 480 to 730 | 195 to 221 | 150 to 181 | 120 to 317 | 14.5 |
| Version 6, 3 px a module, 111 x 111 | 11.8 to 19.6 | 7.0 to 8.9 | 2.5 to 5.1 | 0 to 6.9 | 0.6 |
| Version 6, 4 px a module, 148 x 148 | 10.7 to 19.7 | 4.6 to 7.5 | 3.7 to 5.1 | 1.8 to 6.4 | 0.6 |

These are ranges over three runs on a loaded machine, minimum of 15 rounds each, and they are rough: one row per run read 1.3x to 1.5x high, a different row each time. They are here to rank stages, not to be quoted. What reproduced in every run: the matrix decode is 2 to 4 %; the Otsu threshold is the largest or second largest stage everywhere and half of a small clean image; and Otsu costs more on the 555 x 555 image than on the 740 x 740 one, which has 1.8x the pixels.

That last one has a cause. `Binarizer.ComputeOtsuThreshold` folds eight equal pixels into one `+= 8`, which is what made it 8x to 10x on QR-like input. At 3 pixels a module almost no group of eight is uniform, so the walk falls back to eight increments on an image with two hot bins, the store-forwarding case the fold was written to avoid. Three and four pixels a module are the common sizes for a rendered symbol.

The Structured Append benchmark (`QRCodeStructuredAppendDecode`) decodes `QRCodeData`, and the only Standard QR image decode under measurement is a version 6 URL in `QRCodeDecodeEndToEnd`. Nothing tracks the path a set is actually read through.

## Scope

| In | Out |
|---|---|
| `QRImageDecoder.DecodeLuminance` for Tier 1 and Tier 2 inputs, large symbols first: threshold, finder search, dimension estimate, sample mesh, grid sampling, the retry ladder | Tier 3 robustness. This plan makes the supported envelope faster and does not move it |
| `Binarizer.ComputeOtsuThreshold` and the `GreyLevels` it now also produces. Shared with the Micro QR and rMQR image decoders (the type's own summary names only the first), which gain or lose with it | Any change to what decodes. A candidate that alters a threshold, a finder position or a sampled module on any fixture is a different plan |
| The `SKBitmap` entry: `LuminanceConverter` in front of the decode, measured so the luminance figures are not mistaken for the whole | The matrix decode. Closed at 2 to 4 % of an image decode; its deferred candidates (a bit-plane extract, five numeric groups a window load, the deinterleave loop) stay deferred on that number |
| A large-symbol image decode shape in the benchmark project, hard and anti-aliased edges | The rMQR and Micro QR detection stages, which have their own designs and measured profiles. They enter only as arms of the shared threshold's measurements |
| The not-found path (`NotDetected` on noise and gradients), as a cost that may not rise | Sharing work across the symbols of a Structured Append set. Each is its own image |
| `specs/standardqr-decoder.md`: Decisions, Lessons, Performance | Public API. Nothing is added or changed |

## What has to stay true

- Same result, bit for bit: the Otsu threshold and the `GreyLevels` for every image, the finder patterns found, the module grid sampled, and therefore the decoded text, status and `QRCodeDecodeInfo` (corners included) over the image fixtures, the clean-image sweep and the perspective and rotation tests. A faster stage is held to the current one as its reference, the way every kernel in this library is.
- Steady-state decode allocates nothing, and nothing resident grows with the image sizes or versions seen. A fixed table is fine; a cache keyed by input is not.
- The failure path does not get slower. An image with no symbol pays the threshold and the whole finder scan, and that cost was brought down deliberately; a candidate that speeds up clean symbols by making noise slower is a loss.
- netstandard2.0 runs the best portable form, not the old loop.
- A win is stated per input class. Hard-edged renders, anti-aliased renders and photo-like input stress different branches of the same stage; one number for "the image decode" hides a regression in two of the three.

## Approach

Measure first, then one hypothesis a variant, each gated for identity against the shipped stage before it is timed, with a byte-identical copy of one arm in every run as the noise canary. The matrix plan's last two phases showed why: on this class of machine a 500 us body needs the canary to turn a gap into a verdict.

### 1. What phase 1 has to split

"Rest" above is four things and an unknown count: the dimension estimate, the sample mesh (46 alignment patterns at version 40, each a window search), the grid sampling (already SIMD, 29 us at version 40 when last measured), and however many times `SampleAndDecode` runs before one attempt is terminal. The count matters more than any kernel in it: a second attempt doubles the mesh and the sampling. Phase 1 reports attempts per input class before it reports a time.

### 2. Otsu histogram (prototype measured)

An exact form: count the pixels equal to 0 and to 255 with a vector compare and a popcount, and send only the other pixels to the scalar bins. The histogram is identical by construction, and the two hot bins never go through memory. Prototype, histogram fill only, x64, histograms checked equal on every image:

| us | Shipped | Extremes counted | Ratio |
|---|---:|---:|---:|
| Version 40, 3 px a module, hard edges | 184 | 10 | 0.05 |
| Version 40, 4 px, hard | 122 | 18 | 0.15 |
| Version 40, 8 px, hard | 194 | 69 | 0.35 |
| Version 40, 3 px, soft edges (71 % pure 0 or 255) | 161 | 95 | 0.59 |
| Version 40, 4 px, soft (78 %) | 165 | 137 | 0.83 |
| Version 40, 8 px, soft (89 %) | 518 | 351 | 0.68 |
| Random noise 740 x 740, no 0 or 255 | 179 | 320 | 1.79 |

Soft edges here are a half-pixel box filter, a stand-in for an anti-aliased render and not one. Open questions, each a variant:

1. The noise row. A block with few extremes has to go to the shipped walk, and the cut-over is a measurement.
2. Two values that are not 0 and 255 (a symbol rendered #222 on #EEE has no pure extremes at all). Whether the two counted values can be picked from the image without a second pass.
3. The rest pixels of an anti-aliased render still collide in a few grey bins. Whether the shipped uniform-group fold and the extremes count compose, or fight.
4. Whether any of it survives without a movemask: ARM64 has none, and the library already carries a bulk-movemask shape for it.
5. Added by phase 1: the prototype's inputs were the flattering ones. The shipped fold is fast only when module boundaries land on multiples of eight pixels; at a fractional module size the same two-valued image costs 0.52 to 0.70 ns a pixel against 0.09, and a smooth gradient 1.56. The variants are measured on the fractional, soft, rotated and gradient classes first, and the hard renders last.
6. Added by phase 1: the second polarity. When no symbol is read the image is inverted and thresholded again, and the inverted image's histogram is the first one mirrored, so its threshold and `GreyLevels` follow without touching a pixel. Exact by construction, no kernel, and half of the threshold's cost on every image that is not decoded.

Subsampling the histogram would be far faster than any of this and is out: it changes the threshold.

### 3. Piecewise sampling (phase 3)

Versions 14 and up sample through the piecewise mesh, and `SampleGridPiecewise` is a scalar loop per module: 28 us at version 20 and 94 to 137 at version 40, against 8.5 us for the global sampler's SIMD rows on the same version 20 grid. It is 5 to 34 % of a version 40 decode and the largest stage on a soft 3 px image. The global sampler is the precedent for the kernel and the scalar loop is its reference: the sampled grid has to come out identical, module for module, which for a sampler means the same pixel chosen for every module, not a close one.

### 4. Finder search (phase 4)

8 to 33 % of a version 40 decode, and nearly all of a no-symbol noise image that the threshold is not (1,190 to 1,210 of 1,350 us a polarity at 740 x 740). It is already a strided SIMD mask walk, so what is left in it is not known, and nothing is proposed here until the code has been read and its own stages timed. The matrix plan's fifth phase is on record for what a hypothesis written before that is worth.

## Risks

| Risk | Why it matters | Answer |
|---|---|---|
| Synthetic images flatter the histogram work | A hard-edged two-valued render is the best case for counting extremes and the worst for the shipped walk | Every figure carries its input class. Phase 1 measured it: the renderer does not anti-alias, a whole number of pixels a module is the histogram's best case by up to 6x, and the fractional, soft, rotated and gradient classes are the ones a variant is judged on |
| Identity is harder to state for an image stage than for a kernel | A finder or sampling change can move a float by an ulp and still decode | The reference is the shipped stage's output, compared exactly; a candidate that cannot meet that is out of scope by the table above |
| A shared stage regresses a symbology this plan does not measure | `Binarizer` serves Micro QR and rMQR, whose images are small and wide, not large and square | A Micro QR and an rMQR image decode are arms of every Otsu measurement |
| The not-found path | It runs the threshold and the full scan and exits; it is also the adversarial input | `NotDetected` on noise and on a gradient are arms of every measurement |
| Machine noise | The ranges in Purpose spread 1.5x | Canary arm, alternating A/B against the committed tree, a quiet machine for anything quoted |
| ARM64 and the browser | The matrix plan's stage ranking changed on ARM64 (Reed-Solomon went from 1 % of a decode to 28 %) | A measurement phase on the ARM64 machine before the fold; the Playground for the browser |

## Phases

Each phase follows the test-first workflow, updates the decoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Measure | A stage harness over `DecodeLuminance` with a canary arm: threshold, finder search, dimension estimate, mesh, sampling, matrix decode, and the number of `SampleAndDecode` attempts; versions 6, 20 and 40; 3, 4 and 8 px a module; hard edges, the real renderer's anti-aliased output, a photo-like degradation; upright and rotated; the `SKBitmap` entry beside the luminance one; `NotDetected` on noise and a gradient; a large-symbol image shape added to the benchmark project | Done, see Progress log. A ceiling per stage per input class, stated as a number with its spread; the attempts count explained wherever it is above one; a stage under about 5 % everywhere is dropped from the plan with that number recorded |
| 2 | **P0** | Otsu histogram | The variant ladder of Approach 2, the mirrored histogram for the second polarity as a variant of its own; the winner per tier with runtime dispatch; parity tests first | Done, see the two Progress log entries. Histogram, threshold and `GreyLevels` identical to the shipped walk over random images, two-valued images at every module size from 1 to 16 px, images with no extremes, all-one-value images and lengths that are not a multiple of the vector width; planted faults each red; no input class slower, noise and the gradient included; the second polarity's threshold and `GreyLevels` identical to a recomputation over the inverted pixels; kernel ratio and end-to-end delta per class, `QRCodeImageDecodeEndToEnd` against its phase 1 baselines |
| 3 | P1 | Piecewise sampling | A vector form of `SampleGridPiecewise` after the global sampler's, the scalar loop kept as the reference; parity tests first | The sampled grid identical to the scalar loop's, module for module, over every version that uses the mesh, upright, rotated and under keystone, with mesh nodes found and with nodes left at their predictions, and at image edges where a sample clamps; planted faults each red; kernel ratio and end-to-end delta per class |
| 4 | P1 | Finder search | Read `FinderPatternFinder.TryFind`, time its own stages on the large and the no-symbol inputs, and only then write the hypotheses into this row | A stage profile of the search with its spread, and either a variant ladder or a recorded reason there is none |
| 5 | P1 | ARM64 measurement | The phase 1 profile and the shipped candidates on the ARM64 machine, same harness | A number per arm and class; anything that loses there is gated or reverted |
| 6 | P2 | Fold | Decisions and measurements into `specs/standardqr-decoder.md`, the shared binarizer's into `specs/qrcode-symbologies.md`; this plan deleted | The spec carries what was decided and why |

Phase 1 can end the plan early in one way: if the attempts count explains most of "rest", the work is a decision about the retry ladder, which changes what decodes first and belongs with the accuracy work, not here.

## Verification notes

- The harness compiles the library sources, so internal stages are called directly; private ones are made `internal` only when a phase ships a change to them, never for the measurement.
- Rendered inputs for the benchmark shape come from the library's own renderer at fixed sizes, so the anti-aliasing measured is the anti-aliasing users produce.
- The baseline for any end-to-end delta is the committed tree exported beside the working one and built into a second binary of the same harness, the two checked to differ, run alternately.

## Progress log

### Phase 1, Measure (2026-09-21)

Done: a stage harness that compiles both library source trees and replays the decode ladder stage by stage (the two private stages through delegates), so the path that decoded and the number of `SampleAndDecode` attempts are observed rather than inferred; 36 symbol inputs (versions 6, 20 and 40 x 3, 4 and 8 px a module x four classes) and four no-symbol inputs, every image made by the library's own renderer into an RGBA bitmap; nine rounds an input, the arm order rotated every round, every arm warmed by time, minimum per arm; the whole run twice. `total` is entered twice per input as the first and last arm: the two read within 1 % of each other on 31 and 36 of the 40 inputs in the two runs and within 3.7 % on all, and the two runs' totals agree within 2 % on 26 inputs and within 5.5 % on all. `QRCodeImageDecodeEndToEnd` added to the benchmark project (seven shapes, the span overload and the `SKBitmap` entry, a setup guard that fails a shape which stops decoding). No library source changed.

The four classes, and one correction to this plan's vocabulary: **hard** is a whole number of pixels a module; **fractional** is k + 0.4 px a module through the same renderer, which does not anti-alias: it stays 100 % pure black and white with module widths alternating between two values; **soft** is the hard render blurred 3 x 3, contrast cut to 40..215, a brightness ramp and +-8 noise, no pure extreme left; **rot** is the hard render rotated 17 degrees with a linear filter, 81 to 94 % pure. What this plan called "anti-aliased" exists only in the last.

Every input decoded on the first attempt from the dimension estimate: attempts = 1 on all 36. The early exit this phase could have taken (the retry ladder explains "rest") did not fire. Version 6 samples through the global transform, versions 20 and 40 through the piecewise mesh.

Version 40, x64, us, the two runs as a range:

| | Image | Total | Otsu | Finder | Mesh build | Sampling | Matrix | Bitmap entry adds |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| 3 px hard | 555 x 555 | 432 to 444 | 219 | 71 to 73 | 10 | 94 to 96 | 14 | 27 to 36 |
| 3.4 px fractional | 629 x 629 | 546 to 570 | 276 to 277 | 86 to 93 | 11 | 94 to 97 | 14 | 41 to 42 |
| 3 px soft | 555 x 555 | 274 to 279 | 90 to 91 | 58 to 62 | 10 | 94 | 14 | 30 to 31 |
| 3 px rot | 694 x 694 | 480 to 483 | 173 | 81 | 13 | 95 | 70 to 71 | 41 to 46 |
| 4 px hard | 740 x 740 | 440 to 442 | 123 to 124 | 88 to 112 | 7 | 94 to 95 | 14 | 51 to 55 |
| 4.4 px fractional | 814 x 814 | 820 to 823 | 432 to 453 | 135 to 139 | 9 | 95 to 97 | 14 | 62 to 65 |
| 4 px soft | 740 x 740 | 438 to 462 | 162 to 166 | 125 to 133 | 7 | 94 to 98 | 14 | 49 to 61 |
| 4 px rot | 925 x 925 | 730 to 769 | 361 to 363 | 108 to 172 | 9 | 95 to 101 | 21 to 22 | 53 to 83 |
| 8 px hard | 1480 x 1480 | 491 to 501 | 196 | 160 to 167 | 8 | 96 to 97 | 14 | 194 to 211 |
| 8.4 px fractional | 1554 x 1554 | 1,652 to 1,666 | 1,251 to 1,256 | 184 to 190 | 9 | 96 to 100 | 14 | 253 to 278 |
| 8 px soft | 1480 x 1480 | 1,011 to 1,026 | 667 to 675 | 160 to 187 | 8 | 95 to 97 | 14 | 197 to 228 |
| 8 px rot | 1849 x 1849 | 1,887 to 1,926 | 1,460 to 1,506 | 158 to 164 | 10 | 128 to 137 | 14 | 400 to 452 |

The dimension estimate and the grid transform read 0.2 to 0.5 us on every input. Version 20 has the same shape at a quarter of the size (total 85 to 612 us, Otsu 30 to 425, sampling 28 through the mesh); version 6 is total 15 to 97 us with Otsu 7 to 75 and the finder 4 to 21.

No symbol, us: 740 x 740 noise 2,724 to 2,786, gradient 1,765 to 1,769; 1480 x 1480 noise 6,217 to 6,328, gradient 3,732 to 3,737. Each is two passes of the threshold and the finder search, one per polarity, to within 2 %: noise is 2 x (157 Otsu + 1,190 to 1,210 finder), the gradient 2 x (854 to 858 Otsu + 24 finder).

Ceilings, as a share of the version 40 total:

| Stage | Share | Verdict |
|---|---|---|
| Otsu threshold | 28 to 78 %; 33 % on the soft 3 px image, 66 to 78 % on the 8 px images that are not pixel aligned | Phase 2, as planned, and larger than the Purpose table guessed |
| Piecewise sampling | 5 to 34 %; the largest stage on the soft 3 px image | Phase 3 |
| Finder search | 8 to 33 %; all of a no-symbol noise image that the threshold is not | Phase 4, read before anything is proposed |
| `SKBitmap` conversion | Of the bitmap entry's own total: 6 to 13 % at 3 and 4 px, 13 to 28 % at 8 px | Over the 5 % line and left alone for now: 0.09 to 0.13 ns a pixel is four input bytes at 30 to 45 GB/s from a kernel that was already taken 14x to 30x; reopened only if it still ranks after phases 2 to 4 |
| Matrix decode | 1 to 5 %, 14 % on one input | Dropped. The 14 % is Reed-Solomon correcting sampled errors on the rotated 3 px image (70 us against 14), which is an accuracy observation |
| Mesh build | 1 to 4 % | Dropped |
| Dimension estimate, grid transform | under 0.2 % | Dropped |

What Otsu costs a pixel is the finding of the phase, ns: hard 8 px 0.09, hard 4 px 0.23, hard 3 px 0.71; fractional 0.52 to 0.70 at every size; soft 0.29 to 0.31; rot 0.36 to 0.43; noise 0.29; **the smooth gradient 1.56**. The 8-equal-pixels fold is fast exactly when module boundaries land on multiples of eight pixels, which is what a hard 8 px render in an image whose width is a multiple of eight does and nothing else does. Move the same symbol to 8.4 px a module, still pure black and white, and the threshold goes from 196 to 1,251 us. The gradient is the other end: neighbouring pixels fall in the same or the next bin, so every increment waits on the last one, and the image with no symbol in it pays that twice.

Lessons:
- The Purpose table's inputs were the flattering case and it took the fractional class to show by how much: 3, 4 and 8 px hard renders are the only images here whose Otsu cost is under 0.25 ns a pixel at some size. A benchmark shape at a whole number of pixels a module measures an alignment, not a stage. The new benchmark class carries a fractional shape for that reason and says so.
- Isolated stage arms are floors. Their sum under-read the total by up to 26 % (328 against 442 us at version 40, 4 px hard) while an arm that runs the same stages back to back in one body read within 3 % of the total on 33 of the 34 inputs it applies to and within 3.6 % on the last. Nothing was missing; a stage looped on its own runs with its part of the image in cache, and in a decode it does not. Shares above are therefore of the total, and any stage's own figure is a lower bound.
- The second polarity recomputes a histogram the first already has. The inverted image's histogram is the first one mirrored (bin i and bin 255 - i swap), so its threshold and its `GreyLevels` follow exactly without touching a pixel. On the gradient image that is 854 of 1,765 us. It goes into phase 2 as a variant of its own, since it is exact and needs no kernel.
- The piecewise sampler is scalar and the global one is not: 28 us against 8.5 for the same version 20 grid, 94 to 137 at version 40. The global sampler's SIMD rows were the large-symbol win of an earlier round, and large symbols are precisely the ones that stopped using it when the mesh arrived.
- Two things seen and deliberately not pursued here, because they change what decodes first: at version 20, rotated, 3 and 4 px, the mesh samples, fails to decode and the global transform then succeeds, about 45 us of a 140 to 207 us decode spent on the failed attempt; and the rotated 3 px version 40 image decodes only through error correction.
- The `SampleAndDecode` count was the first thing this phase was told to report and the answer was one, everywhere. Asking it first still paid: it is what licenses reading every other column as the cost of a decode rather than of a search.

Benchmark delta: none, nothing shipped. `QRCodeImageDecodeEndToEnd`, first run, BenchmarkDotNet means on the same loaded machine (they sit 10 to 20 % above the harness minimums, as a mean over a minimum should): version 40 at 3 px 527 us, at 3.4 px 652, rotated 831, soft 506, the version 6 URL 20.6, no-symbol noise 2,685, gradient 1,776; the `SKBitmap` entry 1.02x to 1.17x of the span one. The span rows allocate nothing on every shape; the bitmap rows allocate the result string and nothing else. These are the baselines phase 2 is measured against.

### Phase 2, Otsu histogram: the fill (2026-09-21)

Scope of this entry: the histogram fill only. The second polarity's reuse of the first histogram (Approach 2, item 6) has its own entry below.

Done: `Binarizer.FillHistogram` with two tiers behind it, and `ComputeOtsuThreshold` reduced to calling it; the threshold search and `GreyLevels.FromHistogram` untouched. The 256-bit tier (`FillHistogramVector256`, net8.0+) compares 32 pixels against 0 and against 255, counts the two masks in registers and sends only the other pixels to the bins; a block with more than 12 other pixels goes to the scalar groups; after two blocks in a row holding neither counted value the next 31 blocks are taken without the test. The scalar tier is the old walk through unchecked references. Tests first: `OtsuHistogramParityTest` failed to compile against the old code, then passed: every tier entered directly, all 256 bins against a per-pixel count written in the test, over inputs chosen by what a block holds (two-valued symbols at module sizes 1.0 to 16.0 so boundaries fall at every block phase, the pairs 22/EE and 01/FE, sparse intruders, exactly k other pixels a block for k = 0..32, plateaus, a gradient, noise, six constants, every length 0 to 100, photo-like stretches ending at every distance from the end of the buffer and running on into a two-valued region), onto a dirty histogram. Full suite green on net8.0 and net10.0, Release and Debug. Spec, spec map and the architecture record's ARM64 note updated.

Planted faults, a counted baseline first: 21 in all. 16 red (the zero compare against 1, either count dropped or sent to the wrong bin, a dense block counting its extremes twice or taking three groups, the walk skipping its lowest bit, the fold adding 7, a group dropping its top byte, the bins not cleared, the slice that bounds the bins removed, either tier's tail dropping the last pixel, the stretch's end not clamped to the buffer, its resume step not undone, the stretch restarting on the current block, the stretch stepping 16). Five survived and all five are equivalent for the bins. Three are performance policy, held by the measurements below and not by a test: the dense cut-over at 32 instead of 12, a stretch starting on any dense block, a stretch ending three pixels early. Two were code the change did not need, and were deleted with their mutants: an explicit "histogram too short" check that the slice right after it already enforced, and a rounding of the stretch's end to a block boundary, which the vector loop never needed because it resumes from any offset.

What answered the open questions of Approach 2:

1. The noise row: a per-block cut-over (more than 12 of 32 other pixels) plus the untested stretch. The cut-over alone left a photo-like threshold 8 to 11 % slower end to end.
2. Two values that are not 0 and 255: not addressed. Such an image has no counted pixel, runs as photo-like through the scalar groups, and costs what it cost before.
3. Composing with the uniform-group fold: they compose by construction, because the dense path is the fold's own group.
4. No movemask on ARM64: not shipped there. A 128-bit form was measured on x64 only (below), and ARM64 and WASM keep the scalar tier.

Benchmark delta, x64. Image decode through the phase 1 stage harness built against the committed tree and against the change, alternating, minimum of 7 rounds an arm, two passes each; version 40, us:

| | Otsu before | after | Image decode before | after |
|---|---:|---:|---:|---:|
| 3 px hard | 216 | 11 | 394 | 188 |
| 3.4 px fractional | 274 | 14 | 549 | 204 |
| 4 px hard | 122 | 19 | 433 | 222 |
| 4.4 px fractional | 442 | 22 | 797 | 241 |
| 8 px hard (the old fold's best case) | 193 | 75 | 463 | 337 |
| 8.4 px fractional | 1,209 | 83 | 1,594 | 392 |
| 3 px rot | 170 | 71 | 477 | 430 |
| 4 px rot | 357 | 115 | 716 | 523 |
| 8 px rot | 1,442 | 533 | 1,860 | 953 |
| soft (photo-like), 3 / 4 / 8 px | 89 / 161 / 657 | 90 / 163 / 663 | 268 / 425 / 984 | 269 / 381 / 993 |
| no symbol, gradient 740 x 740 | 844 | 249 | 1,741 | 555 |
| no symbol, noise 740 x 740 | 155 | 154 | 2,710 | 2,687 |

Versions 6 and 20 follow the same ratios (total 0.34x to 0.77x on hard, fractional and rot). The photo-like class over all nine inputs and two separate runs read 0.97x to 1.03x on the threshold and 0.97x to 1.01x on the decode, inside the harness's spread (one soft row's baseline total read 12 % high in one pass; its threshold did not move). Micro QR and rMQR through their bitmap entries, same A/B: Micro QR M4 5.55 → 3.86 us at 3 px and 8.46 → 7.22 at 8 px; rMQR R7x43 5.07 → 2.99 and 9.61 → 8.13; R17x139 25.5 → 10.1 and 46.5 → 37.5. `QRCodeImageDecodeEndToEnd` against its phase 1 baselines: version 40 at 3 px 527 → 211 us, 3.4 px 652 → 233, rotated 831 → 531, soft 506 → 391 (the baseline row was the noisy one), version 6 20.6 → 11.1, gradient 1,776 → 507, noise 2,685 → 2,630; Allocated unchanged on every row, the span rows at zero.

Lessons:
- The kernel benchmark's baseline was the shipped loop copied verbatim, and it was not the shipped loop. The copy took the histogram as a span parameter; the library's is a `stackalloc int[256]` in the same method, so the JIT knows its length and drops the bounds check of every `histogram[(byte)v]`. The copy paid that check per increment. It was 15 % slower than the real code on noise and, because the check spaced out same-bin increments, 27 to 75 % faster on gradients and two-valued images. Eight rounds were ranked against it. The order between candidates survived; every "times the shipped code" figure and the claim "wins on photo-like images" did not. A faithful copy is one whose disassembly matches.
- The end-to-end A/B per input class is a detector, not a confirmation. The first port read 0.24x to 0.90x on every other class and +8 to +11 % on the threshold of all nine photo-like inputs, which the kernel benchmark had as a win. One class, nine inputs, all the same sign: that is what separated it from noise, and a single "image decode" number would have averaged it away.
- The repair came in three measured steps, and the first idea made it worse: skipping the test after any dense block sent resampled images' extreme-rich blocks through memory (rot 0.28x to 0.35x) and ran the stretch as a loop slower than the four groups it replaced (photo-like 1.10x). Starting only on blocks with no counted value at all, and running the stretch as the scalar tier's own loop, brought the threshold to +2 to +3 % with 7 untested blocks and level with 31.
- "The same code at half the width" is not the same code. A 128-bit tier at 16 pixels a step ran a gradient 1.85x slower than the scalar walk, three times over, with a dense path whose disassembly matched the 256-bit tier's instruction for instruction and nothing spilled. Keeping the 32-pixel cadence (two loads, the four masks paired) brought it to 1.08x. Not explained; not shipped, because the targets it would serve are the ones it was not measured on.
- Lanes lose even after their stated obstacle is gone. The earlier round refused sub-histograms because a symbol's two hot bins collide in every lane; with those two bins out of memory the objection no longer held, and four arrangements were tried. A gradient gained 8 to 12 %, noise and photo-like images lost 5 to 20 % to the L1 footprint, every time.
- Two wins were real and not taken. A fast path for wholly two-valued blocks (27 to 35 % on rendered symbols) lost 4 to 17 % on resampled ones, a different amount each run, because the JIT lays out the three paths by each workload's profile. A 512-bit tier halved the two-valued images again and would be the library's only one, on hardware no CI runner is sure to have. Both are recorded with their numbers in the spec.
- Equivalent mutants were deletable code twice in one file. The mutation run is also a review of what the change did not need.

### Phase 2, Otsu histogram: the second polarity (2026-09-21)

Done: the three image decoders (`QRImageDecoder`, `MicroQRImageDecoder`, `RmQRImageDecoder`) count the histogram once in `DecodeLuminance`, hand it to `DecodeLuminanceCore`, and before the inverted retry turn it into the negative's with `Binarizer.InvertHistogram` (bin i to 255 - i, a reverse in place). `ComputeOtsuThreshold` is split: the fill, and `ComputeOtsuThresholdFromHistogram` for the search and the grey levels, which reads its total from the bins instead of the pixel count. The histogram is a `stackalloc` in the caller, so nothing is allocated and nothing is resident. Tests first: `OtsuInvertedHistogramTest` failed to compile, then passed with the decoders still unwired (the behaviour it pins is the behaviour before the change), then stayed green through the wiring. Threshold and `GreyLevels` from the mirrored bins against a per-pixel count of the negative and the search as it stood before the split, both written in the test, over two-valued pairs, soft edges with uneven levels, skewed spreads, a gradient, noise, constants, the empty image and ragged lengths; and one light-on-dark symbol per decoder with modules at 150 on a background of 25. Full suite green on net8.0 and net10.0.

Planted faults, a counted baseline first: six, all red. The mirror left out of each decoder in turn (three), the mirror leaving bin 0 in place, the total missing the top bin, the top split clamped one bin low. The last one survived until the pair 254/255 was added, whose only split is at the top bin.

Benchmark delta, x64, the phase 1 harness built against the committed tree and against the change, alternating, minimum of 15 rounds an arm, three passes; image decode, us:

| | before | after |
|---|---:|---:|
| no symbol, gradient 740 x 740 | 530 to 535 | 287 to 309 |
| no symbol, gradient 1480 x 1480 | 1,863 to 1,985 | 1,045 to 1,144 |
| no symbol, noise 740 x 740 | 2,666 to 2,690 | 2,561 to 2,568 |
| no symbol, noise 1480 x 1480 | 6,136 to 6,201 | 5,520 to 5,686 |
| version 40, 3 px hard / 3.4 px / soft / rot | 206 / 231 / 280 / 446 | 210 / 229 / 281 / 445 |
| version 6, 4 px | 11.1 | 11.3 |

Micro QR and rMQR on a 400 x 400 gradient with no symbol: 155 to 156 → 93 to 94 us and 155 to 156 → 93 us; their decodes of a symbol (six shapes, 3 to 40 us) level to within 2 %. A decode that succeeds in the first polarity does the same work as before, the fill having only moved up one frame.

Lessons:
- The threshold is searched again on the mirrored bins, not mirrored itself. The search keeps the first of equal splits, and two values with nothing between them tie at every split between them, so `256 - threshold` is a different threshold on exactly the images this library reads most. The search is 256 steps against a fill of every pixel, and running it again makes the result exact by construction instead of by argument.
- The existing inverted-decode tests could not see the wiring. They render black on white reversed, and on 0 and 255 the first polarity's bins, unmirrored, give a threshold that also splits the negative. A retry that forgot the mirror read all three. The test symbol needs levels that are not mirror images of each other.
- Restoring a planted fault with `mv` from a backup restores the old timestamp, and the incremental build then keeps the faulted binary: the run after the last fault stayed red until the file was touched. A mutation sweep ends with a rebuilt, counted green baseline or it has not ended.
