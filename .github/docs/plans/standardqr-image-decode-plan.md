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

Phase 1 put it at 8 to 33 % of a version 40 decode and nearly all of a no-symbol noise image that the threshold is not, and proposed nothing until the code had been read and its own stages timed. That was done after phase 3 shipped (2026-09-22), and the ranking had moved: with the threshold and the mesh sampler out of the way, the search is the largest stage of every rendered version 40 decode.

One version 40 Structured Append symbol (the first of a 45,000 character byte set at level L), the phase 1 harness on the tree after phase 3, x64, us, minimum per arm:

| | Total | Otsu | Finder | Mesh build | Sampling | Matrix | Finder share |
|---|---:|---:|---:|---:|---:|---:|---:|
| 3 px hard | 128 | 11 | 72 | 10 | 19 | 14 | 56 % |
| 3.4 px fractional | 152 | 14 | 89 | 12 | 18 | 14 | 59 % |
| 4 px hard | 173 | 19 | 111 | 7 | 19 | 14 | 64 % |
| 4.4 px fractional | 208 | 23 | 138 | 9 | 19 | 14 | 66 % |
| 8 px hard | 285 | 77 | 160 | 9 | 19 | 14 | 56 % |
| 4 px soft | 367 | 170 | 127 | 7 | 19 | 14 | 35 % |
| 4 px rot | 453 | 124 | 160 | 9 | 19 | 21 | 35 %, 50 % timed inside a decode |
| No symbol, noise 740 x 740 | 2,584 | 159 x 2 | 1,194 x 2 | | | | 92 % |

The label-sized set (version 10, the global transform) reads 14 to 45 us on the rendered classes with the search at 47 to 64 %. The threshold is still the largest stage on the soft class at every size and on the 8 px rotated image, where it runs the scalar bins; phase 2 closed that with its refutations and nothing here reopens it.

The isolated finder arm under-reads the rotated class. With timestamps between the stages of one decode body, the search costs 1.4x to 1.7x its isolated figure on the three rotated images (79 to 135, 152 to 224, 162 to 251 us) and 1.2x on the 8 px soft one, while the hard and fractional classes read within 10 % either way. Looped on its own, the search runs the same rows in the same order and its branches are learnt; in a decode they are not. The ladder below is judged end to end per class for that reason, and a kernel ratio alone does not close a rung.

What the search does, counted through an instrumented copy (counts are exact; its timestamps inflate the stage by a third, so the times are from uninstrumented copies of each part):

- Only the stride pass runs. The complementary rescan fired on no symbol input, only on the gradient. A third of the rows are scanned, a sixth at 8 px a module.
- A version 40 symbol is 7,800 to 12,200 dark runs over 185 to 309 rows. 280 to 800 windows pass the 1:1:3:1:1 check and go to the cross-checks, and 6 to 21 survive: 97 to 98 % of cross-checks are refusals, all of them on data modules.
- Version 40, hard, 3 / 4 / 8 px, us: the row masks 3 / 5 / 9 (already vector compares, about 5 % of the search); the run walk with its ratio checks 29 / 40 / 41; the cross-checks and what follows them the remaining 40 / 67 / 110. The vertical check alone is 25 / 44 / 82, walking 32 / 43 / 86 pixels a hit for a window 21 / 28 / 56 pixels long.
- The noise image is 45,700 runs and 2,350 cross-checks a polarity: 554 us of walk at 12 ns a run, against 4 ns a run on a symbol, which is what unpredictable run lengths cost the run-by-run walk and its five chained float compares.

The ladder, in the order the measured ceilings put it. Each rung is one hypothesis on its parent, gated for identity before it is timed:

| # | Hypothesis | Evidence so far | Open |
|---|---|---|---|
| 1 | The cross-check walks pay for their generality, not for the pixels: one method serves both axes and picks the axis per pixel, every pixel is a checked two-dimensional index, and the runs live in a stack buffer. One walker per axis over a stepped reference, the runs in locals, reads the same pixels in the same order | Prototype, vertical check only, runs identical on every hit of every input: 0.37 to 0.52 at 3 and 4 px a module (version 40: 24.5 to 9.1 us, 44.4 to 21.4), 0.58 to 0.66 at 8 px (81.8 to 52.2), noise 0.60 | Shipped, see Progress log: the search gained 0.83 to 0.92 on rendered symbols, about two thirds of what this prototype's walk promised, and nothing on noise |
| 2 | As first written: most of a refused walk is past the point where it could still be accepted, so a tighter cap on the side runs shortens it. Counted before it was built, that was wrong: the false hits are ordinary data, side runs of about two modules around a centre of about three, and the tighter cap alone read 15 % fewer pixels. As shipped: the accept conditions order the runs (a side run is shorter than the centre run under the strict ratio and tied to a third of it under the near-miss one), so the centre run is measured first and the walk is given up at the first run that passes what the centre and the expected total leave | Pixels read a hit 0.55 to 0.60 on symbols and 0.67 on noise, the verdict identical on every hit, before any library code | Shipped, see Progress log: the search 0.81 to 0.87 of rung 1 at 4 and 8 px a module, 0.94 on noise, and 1.03 to 1.06 on a label-sized symbol at 3 and 4 px |
| 3 | The run walk is bound by its branches. All edges of a row taken from the mask at once, then the windows classified eight at a time (strict ratio, near miss, small crisp runs) with only the flagged ones handled, in order, by the code that handles them now | Prototype: 0.53 to 0.58 on every symbol input (version 40: 29 to 51 us down to 17 to 27), 0.19 on noise (554 to 107), the flagged windows identical in count and checksum on every input. It rests on the ratio check in integers, which agreed with the float form on all 40^5 run combinations up to 40 px and on 40 million windows placed at the tolerance edge: the two can only differ where the float division is inexact, and equality in the check needs a total divisible by 14, where it is exact | Shipped, see Progress log: the row pass 0.36 to 0.58 of the mask walk and 0.20 on noise, the image decode 0.66 to 0.80 at version 40 and 0.58 on no-symbol noise. The scalar edge list measured level with the mask walk, so every target without 256-bit vectors keeps the walk it had; a 128-bit form is ARM64's to measure in phase 5 |

Refuted before the phase starts, with the numbers:

- The same classification in scalar code with non-short-circuit integer compares: 1.22x to 2.04x slower than the shipped walk on every input. The shipped float check refuses most windows on its first compare, and doing all of them to avoid the branch costs more than the branch.
- Remembering refused cross-checks across rows: the stride scales with the image height, so a render at 8 px a module hits exactly the windows a render at 4 px does (683 and 683), and no row revisits a module row.
- Refusing early on the centre run alone: a centre one module long already satisfies the loosest bound the accept conditions give.

Out of scope by the rule in "What has to stay true": a different stride, or a search region narrowed after the first finders. Either changes which rows confirm a candidate, the candidate's averaged centre moves with them, and the finder positions are part of the result held bit for bit.

Ceiling if rungs 1 and 3 hold end to end, from isolated parts and therefore an upper bound: version 40 at 3 px 128 to about 100 us, at 4 px 173 to about 135, the noise image 2,584 to about 1,650. The not-found path gains the most, which is the path this plan only promised not to slow. `FindCandidates` shares the row scan and the cross-checks, so Micro QR and rMQR image decodes are arms of every measurement here.

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
| 3 | P1 | Piecewise sampling | A vector form of `SampleGridPiecewise` after the global sampler's, the scalar loop kept as the reference; parity tests first | Done, see Progress log. The sampled grid identical to the scalar loop's, module for module, over every version that uses the mesh, upright, rotated and under keystone, with mesh nodes found and with nodes left at their predictions, and at image edges where a sample clamps; planted faults each red; kernel ratio and end-to-end delta per class |
| 4 | **P0** | Finder search | Read and timed, see Approach 4: the search is 56 to 66 % of a rendered version 40 decode and 92 % of a no-symbol noise image. The ladder of Approach 4 in its order: per-axis cross-check walkers, exact run caps, the edge list with vector window classification; the shipped walk and cross-checks kept as the reference; parity tests first | Done, see the three Progress log entries; one gap is recorded there (three faults that only over-report are held by no test in this repository). The candidate list identical to the reference's, every centre, module size and count bit for bit, and so the same three patterns, over the image fixtures, the clean-image sweep, the perspective and rotation tests, noise, gradients, rows narrower than a vector step and runs that reach either end of a row; for the run caps, the verdict of every cross-check identical over random and adversarial cross sections; planted faults each red; no input class slower end to end, the not-found inputs and the rotated class timed inside a decode included; Micro QR and rMQR image decodes as arms; kernel ratio and end-to-end delta per class against `QRCodeImageDecodeEndToEnd` |
| 5 | P1 | ARM64 measurement | The phase 1 profile and the shipped candidates on the ARM64 machine, same harness; then the Otsu fill's ARM64 tier, which the profile ranks first | Done, see the four Progress log entries: nothing loses, nothing gated or reverted; the Otsu fill, the finder search's row kernel and the mesh sampler have ARM64 tiers. A number per arm and class; anything that loses there is gated or reverted |
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

### Phase 3, Piecewise sampling (2026-09-22)

Done: `QRImageDecoder.SampleGridPiecewise` is a dispatcher over two tiers in `QRImageDecoder.PiecewiseSampling.cs`, and the per-module loop is `SampleGridPiecewiseScalar`, off the decode path and the reference both tiers are held to. The portable tier (`SampleGridPiecewiseColumnTable`, every target) tables the cell a column falls in and the fraction across it once a call, so the division and the cell walk leave the module loop. The AVX2 tier (net8.0+) takes eight modules a step: a cell's start and span once a row, broadcast into the step, a per-lane select on the steps that straddle two cells (which steps, and at which lane, depends on the version only), the lerps as separate multiply and add, the float-to-int conversion in the form the reference's cast has on that runtime, eight scalar loads packed into one word, one compare, one store. Both check the two buffer lengths once and index unchecked after that; a mesh the stack tables cannot hold, or a lattice with cells narrower than a vector step, goes to the reference or the portable tier. Tests first: `SampleGridPiecewiseParityTest` failed to compile against the old code, then passed except where it found a bug (below). Full suite green on net8.0 and net10.0, Release and Debug. Spec and spec map updated.

The kernel search ran as a micro-benchmark outside this repository, four rounds, every variant gated byte for byte against a verbatim copy of the loop over 442 scenes before anything was timed, a byte-identical canary in every run (0.99 to 1.02 at version 40). Same-run ratios to the baseline, version 40 at 3 px a module (109 to 114 us):

| Variant, each one change on its parent | Ratio | Verdict |
|---|---:|---|
| Column table for the cell and the fraction | 0.78 | Confirmed; the portable tier, 0.70 to 0.75 once unchecked |
| A start and a span spread to all 177 columns every row | 1.03 | Refuted: 125k stores a symbol cost more than the indirection they removed |
| Branchless clamps | 1.00 | Refuted: a symbol inside its image never takes those branches |
| 256-bit rows on the spread arrays | 0.72 to 0.93 | Measured its refuted parent's cost, not its own |
| 256-bit rows, per-cell broadcast, select on straddling steps | 0.30 | Confirmed |
| Unrolled lanes, no bounds checks | 0.24 | Confirmed |
| Float minimum, then the raw conversion | 0.23 | Confirmed, small: the saturating `ConvertToInt32` is four instructions, twice a step |
| Indices read as unsigned | 0.20 | Confirmed: a sign extension per lane, on each lane's path to its load |
| Eight pixels packed, one compare, one store | 0.18 | Confirmed |
| Index vector split into halves once | 0.17 | Confirmed: the JIT re-extracted the upper half four times and copied the vector before every extract |
| AVX2 gather | 0.22 against 0.20 | Refuted on this CPU, and it would read three bytes past the last pixel |
| Per-cell start and span in the scalar tier | 0.68, but 0.82 on the 3.5 MB rotated image | Refuted per scenario, cause not found |
| Per-cell arrays interleaved to remove a spilled base | 0.19 against 0.17 | Refuted: the spill was one load |

Version 40 113.7 -> 19.3 us (5.9x), version 20 33.0 -> 6.3, version 14 18.2 -> 3.7, and 135 -> 28 on a 1,849 x 1,849 rotated image, where the scattered loads had been expected to bind and did not. The mesh sampler is now cheaper than the global transform's vector rows on the same grid (8.5 us at version 20), which cannot hoist their division because the denominator changes per module.

Planted faults, a counted green baseline on each runtime first: 17 in all, every one red in the end, 15 of them on the tests as first written: the NaN operand order, either clamp against the other side's limit (both runtimes' forms), a clamp removed (three of these end the test host with an access violation rather than a failed assertion, which is red but not graceful), the lane mask inverted, the select skipped, start and span swapped, the upper half's lanes reordered, the compare inverted in either tier, the row tail dropped, the three-cell guard removed, the column table's fraction off one cell, the scalar tier's upper clamp off by one, the stride taken from the height, the length check removed. Two survived at first, and both were the tests: a y clamp against the x limit (every test image was square) and `<` for `<=` on the threshold (every pixel was 0 or 255). The scenes are now 37 rows taller than wide and their blocks take the threshold and its two neighbours as well as the extremes.

Lessons:
- The parity test found what the kernel search could not see. The micro-benchmark ran on net10.0 only; the library also targets net8.0, where the scalar `(int)` cast is still the raw x64 conversion (INT_MIN for NaN and for anything out of range, 0 after the clamp) and not the saturating one. The float-minimum form matched the reference on net10.0 and picked the far edge of the image instead of pixel 0 on net8.0, for an infinite or out-of-range node. The tier now carries one conversion form per runtime, the same three instructions each. When the benchmark's targets are a subset of the library's, identity has to be gated again where it ships.
- A variant built on a refuted parent measures the parent. The first vector form read 0.72 because it inherited the per-column spreading, and that number was one decision away from closing the question on SIMD; with the parent's change taken out it read 0.30.
- A precedent transfers only for the same change. The global sampler's log says its loop is not division-bound, from halving the divisions. Here the division and the cell walk left the loop altogether and that alone was 0.78.
- For NaN, the operand order of a float minimum is the semantics. `min(limit, x)` lets NaN through to become 0 as the cast makes it; `min(x, limit)` turns it into the limit. Every scene with finite nodes passes either way, so the gate and the tests carry NaN, both infinities and magnitudes past int range as nodes.
- A spill that is one load is not worth removing. The winner still reloads a table base from the stack once a step; the variant that freed the register by interleaving the per-cell arrays lost 5 to 10 %.
- Not done, and why: a 128-bit tier for ARM64 and WASM (not measurable here, and phase 2 showed that the same code at half the width is not the same code, so those targets run the portable tier until phase 5); caching the column table per version (177 divisions, about 1.5 % of the winner, against the rule that nothing resident grows with the versions seen); software prefetch for the rotated class (sampling is 3 % of that decode).

Benchmark delta, end to end: `QRCodeImageDecodeEndToEnd` in `src/FeatherQR.Benchmark`, the committed tree exported beside the working one, the same class run from each, two runs a side, BenchmarkDotNet means in us (the project's ShortRun job, whose error bars on this machine are a third of the mean or more, so the two runs of each side are shown rather than averaged):

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 268 / 239 | 132 / 136 | 280 / 252 | 158 / 166 |
| v40-3.4px | 272 / 257 | 152 / 164 | 306 / 306 | 186 / 197 |
| v40-4px-rot17 | 616 / 569 | 447 / 477 | 657 / 657 | 519 / 544 |
| v40-4px-soft | 480 / 499 | 335 / 371 | 550 / 543 | 451 / 410 |
| v6-4px (global transform, not this path) | 11.9 / 12.0 | 12.6 / 11.8 | 16.1 / 15.8 | 16.3 / 15.9 |
| none-gradient (no sampling) | 338 / 347 | 291 / 324 | 417 / 402 | 356 / 368 |
| none-noise (no sampling) | 2,646 / 2,622 | 2,569 / 2,652 | 2,680 / 2,713 | 2,643 / 2,699 |

The three rows that never reach the mesh sampler are the run's noise: within 1 to 14 % of each other across sides, in both directions. The four version 40 rows moved by 100 to 135 us, 17 to 47 %, which is more than the 75 to 95 us the kernel gave up on its own; a sampler that leaves less of the image and its own tables in the way of the matrix decode behind it is the likely reason, and it was not separated out. Allocated unchanged on every row, the span rows at zero.

### Phase 4, Finder search: the cross-check walk (2026-09-22)

Scope of this entry: rung 1 of Approach 4, the run measurement of the cross-checks. The run caps and the row walk are not started.

Done: `FinderPatternFinder.MeasureRuns` (new file `FinderPatternFinder.RunWalk.cs`), one walk for the column, the row and the diagonal, and the two cross-checks reduced to calling it and judging the runs as before. The line's step and the pixels available on each side of the centre are worked out once, the loops count down over an integer offset that becomes a reference only after the count says the pixel exists, the runs are locals, and the one check in front of the walk is that the centre is inside the image and the image inside its buffer. The loops it replaced were moved verbatim into `MeasureAxisRunsReference` and `MeasureDiagonalRunsReference`, and `TryFindScalar` now runs them, so it is the reference for the whole search and not only for the row scan. Tests first: `FinderRunWalkParityTest` failed to compile (four missing members); then passed with the loops moved and `MeasureRuns` still delegating to them; then stayed green with the stepped walk. It compares the runs and the end of the walk, not the verdict, from every pixel of block scenes with levels on both sides of the threshold and next to it, along all three lines, under caps from 0 to 1,000, on images taller than wide, wider than tall and one pixel across, on uniform images, on finders from 1 to 8 px a module, onto dirty buffers; and `TryFind` against `TryFindScalar`, all four fields of the three patterns, on real symbols placed off-centre on canvases that are not square, crisp and blurred. Full suite green on net8.0 and net10.0 (25,163 run, none failed), the finder classes again in Debug for the assertion in the walk. Spec and spec map updated.

Planted faults, a counted green baseline first: 18, every one red in the end, 17 on the tests as first written: the back step's sign, either cap compared with `<`, the centre pixel not counted, the pixels before the centre taken from the far side, either diagonal limit taken from one dimension only, either border refusal removed, the light compare as `>`, the end one short, the row step taken from the height, the row limit taken from the height, a run not written, the forward walk starting on the centre, and in the callers the two axes swapped and the end added to the wrong coordinate. One survived: the diagonal walk given a cap of 7. No test symbol was large enough for a finder's diagonal side runs to pass a cap, and at 10 px a module the capped runs still hold the ratio; a version 1 symbol at 20 px a module was added and the fault is red.

Hits that go past the first walk, counted on the twelve version 40 inputs: 6 to 9 % of the windows that pass the row check also pass the column check. The row and diagonal walks are therefore a tenth of the walking, nearly all of this rung's gain is the column, and the vector form of the row walk that was held in reserve is not worth a rung.

Benchmark delta, x64. The search alone, the committed tree exported beside the working one and one harness built against each, the two binaries checked to differ, alternating, minimum of 21 rounds an arm over two passes a side for the search and 7 rounds over two passes for the decode; us:

| | Finder before | after | Image decode before | after |
|---|---:|---:|---:|---:|
| Version 40, 3 px hard | 69 | 58 | 126 | 104 to 123 |
| 3.4 px fractional | 88 | 76 | 153 | 138 to 141 |
| 4 px hard | 110 | 97 | 172 | 157 to 161 |
| 4.4 px fractional | 134 | 117 | 209 | 193 to 204 |
| 8 px hard | 158 | 140 | 286 | 258 to 266 |
| 8.4 px fractional | 176 | 157 | 313 | 284 to 297 |
| 3 / 4 / 8 px soft | 60 / 120 / 188 | 55 / 109 / 171 | 203 / 345 / 930 | 197 / 308 to 349 / 896 to 932 |
| 3 / 4 / 8 px rot | 78 / 153 / 162 | 74 / 152 / 157 | 348 / 444 / 855 | 346 to 351 / 441 to 444 / 854 to 863 |
| Version 10 (the label-sized set), 3 to 8 px, rendered | 6.1 to 27.9 | 5.6 to 23.1 | 13.7 to 45.1 | 13.0 to 39.7 |
| No symbol, noise 740 x 740 | 1,156 | 1,155 | 2,514 | 2,490 to 2,505 |
| No symbol, gradient 740 x 740 | 24.0 | 25.1 | 315 | 317 to 320 |

The search 0.83 to 0.92 on the rendered classes, 0.84 to 0.95 soft, 0.89 to 0.99 rotated; the decode 0.83 to 0.95 rendered, 0.89 to 0.98 soft, 0.94 to 1.00 rotated; the no-symbol inputs level. On the rotated class the search timed inside a decode read 0.96 to 1.06 where its isolated arm read 0.89 to 0.99, and the totals did not move: that class is bound by what the rung did not touch. `QRCodeImageDecodeEndToEnd` was not re-run for one rung; it is the phase's closing measurement.

Lessons:
- The prototype's baseline was not the shipped code, again. It timed the old and the new walk as two functions of nine and eight arguments, 0.37 to 0.52 at 3 and 4 px a module. The shipped loops were inline in the cross-check and paid no call, so the copy was slower than what it stood for, and the search gained about two thirds of what the walk alone promised (version 40 at 4 px: 23 us promised, 13 to 15 delivered). Phase 2 wrote the rule after the same mistake with a span parameter: a faithful copy is one whose disassembly matches. It was read and not applied, because the copy looked too simple to get wrong.
- Moving the old loops first, with the new entry point delegating to them, was worth its one build. Two of the new tests failed at that step, against code that had not changed: a payload too long for version 1, and a 3 x 3 blur at 2 px a module, which the shipped finder does not read either. Found one step later, both would have been read as faults of the walk.
- A plain call against inlining the walk into its two callers: within 2 % on every row over four passes, the inlined form ahead on most (0.96 to 0.99), so it is the one kept. The throw moved into a helper either way.
- One reading is worth little on this machine: four passes of the same binary on the same input read 56 to 66 us, one row read 84 once and 109 to 114 three times, and the gradient, which makes no cross-check at all, read 24 against 25. Every figure above is the minimum over passes, and a class is called slower only when all its inputs agree.
- The count of hits reaching the second walk was promised at the start of the rung and taken at its end. Taken first it would have settled the row walk's vector form before it was discussed.

### Phase 4, Finder search: the bounded walk (2026-09-22)

Scope of this entry: rung 2 of Approach 4. The row walk (rung 3) is not started.

Done: `FinderPatternFinder.MeasureRunsBounded` and `AxisRunBounds` (in `FinderPatternFinder.RunWalk.cs`), used by both modes of the axis cross-checks; the diagonal, which has no expected total, keeps `MeasureRuns`. The centre run is measured first, in both directions, and refused outside the range the expected total allows; each side run is then allowed the smaller of the longest side run that centre admits and what is left of the largest total it admits, less one pixel for each side run still to come, and the walk is given up at the first run about to pass its allowance. A walk that finishes returns the runs and the end the unbounded walk returns. `CrossCheck` and the three ratio checks became internal for the tests. Tests first, three layers: `FinderRunBoundsTest` holds the bounds against the verdict's own checks (the 40 % total window and the strict, near-miss or small-crisp ratio), every run vector for expected totals up to 40 and four million vectors at the tolerance edges up to 400 px a module, plus that the bounds refuse something and that no side cap exceeds the reference's; `FinderCrossCheckParityTest` compares `CrossCheck` through the reference walk and the bounded one from every pixel, the returned centre bit for bit and the total and handed-back runs when accepted, in both modes, on columns and rows of finder-like cross sections pushed across every tolerance, crisp and blurred with the image's grey levels, and the walk itself (equal runs when it finishes, a reference the verdict refuses when it gives up); the existing `TryFind` against `TryFindScalar` tests are the third. The tests failed to compile, then passed against `MeasureRunsBounded` delegating to the unbounded walk except for the one assertion that asks for walks given up, then passed whole. Full suite green on net8.0 and net10.0 (25,177 run, none failed). Spec updated.

Planted faults, a counted green baseline first: 33. All 17 faults of the walk and both of the callers red, the last of them after a repair to the tests: the light compare as `>` survived because the new scenes held only 0 and 255, and they now carry levels on both sides of the threshold and next to it, as the first rung's did. Of the 11 faults that tighten a bound by one, 8 red and 3 survived, all three through the same value: the lower bound on the centre run (the total's lower end one higher, the near-miss form one higher, the smallest total 8). The exhaustive test says the tightened bound also holds up to an expected total of 40, so this is slack in the derived bound and not a blind test (the strict form one higher is red). A tight bound would come from solving the side caps and the centre together; the derivable one is kept and costs a pixel. Two faults that loosen a bound were planted to show they survive, since a looser bound is slower and not wrong; one of them was red anyway, against the test that the bounds refuse something.

Two terms were deleted before the sweep because they could never bind: the near-miss forms of the centre's upper bound and of the total's, below the strict forms from a total of 7 and a centre of 2 up. And the small-crisp mode, first left on the unbounded walk, turned out to accept only vectors inside the same bounds (the exhaustive test with its check added), so the mode branch went too.

Benchmark delta, x64, three trees: before the phase (2f6e43a), rung 1, and this change; one harness built against each, the binaries checked to differ, alternating passes, the median over passes (six for the two rungs' search, three otherwise); us:

| | Finder before the phase | rung 1 | rung 2 | Image decode before | rung 1 | rung 2 |
|---|---:|---:|---:|---:|---:|---:|
| Version 40, 3 px hard | 68 | 59 | 57 | 126 | 118 | 114 |
| 3.4 px fractional | 88 | 75 | 68 | 148 | 142 | 131 |
| 4 px hard | 109 | 96 | 82 | 172 | 160 | 147 |
| 4.4 px fractional | 134 | 117 | 98 | 208 | 188 | 168 |
| 8 px hard | 158 | 139 | 112 | 295 | 269 | 234 |
| 8.4 px fractional | 179 | 157 | 127 | 326 | 299 | 255 |
| 3 / 4 / 8 px soft | 63 / 117 / 188 | 58 / 109 / 170 | 57 / 93 / 139 | 201 / 355 / 982 | 200 / 338 / 931 | 197 / 303 / 892 |
| 3 / 4 / 8 px rot | 79 / 150 / 168 | 72 / 150 / 156 | 73 / 121 / 136 | 348 / 451 / 874 | 347 / 445 / 864 | 338 / 426 / 841 |
| Version 10, 3 and 4 px, all classes | 6.1 to 11.3 | 5.6 to 10.2 | 5.9 to 10.6 | 13.8 to 36.6 | 13.1 to 35.5 | 13.4 to 35.9 |
| Version 10, 8 px, all classes | 25.0 to 28.2 | 20.6 to 25.5 | 19.4 to 25.2 | 41.4 to 120 | 36.8 to 116 | 35.2 to 113 |
| No symbol, noise 740 x 740 | 1,159 | 1,160 | 1,095 | 2,539 | 2,521 | 2,366 |
| No symbol, noise 1480 x 1480 | | | | 5,556 | 5,460 | 5,136 |
| No symbol, gradient 740 x 740 | 23.6 | 25.0 | 24.6 | 327 | 324 | 313 |

Against rung 1 the search reads 0.91 to 1.01 at 3 px a module, 0.81 to 0.86 at 4 px, 0.81 to 0.87 at 8 px and 0.94 on noise, the first gain of this plan's finder work on an image with no symbol in it. Against the code before the phase: the search 0.71 to 0.92 at version 40, the decode 0.78 to 0.98, no-symbol noise 0.92 to 0.93. The 1480 x 1480 gradient read 1,064, 1,063 and 1,119 us, and its three passes show why that is not this change: its finder search held at 88 to 92 us on every tree while its threshold, which nothing here touches, ranged from 809 to 898.

One class is slower than rung 1 and faster than before the phase: version 10 at 3 and 4 px a module, the search 1.03 to 1.06 of rung 1 on all eight inputs (0.90 to 0.99 of the old loops), 0.3 to 0.5 us of a 13 to 36 us decode, which reads 0.99 to 1.02. A fifth of that symbol's hits are real finders (9 of 43, against 10 of 516 at version 40); those are walked in full either way, and the bounded walk costs more to enter. A variant without the centre range check, which is three of its eight divisions, read the same 1.04, so the cost is not the arithmetic; it was not found, and the variant was dropped.

Lessons:
- The rung's hypothesis was wrong as written and the count showed it before any library code: "walks run long" was read off 32 pixels walked against a window of 21, but a false hit's window is nine modules and not seven, and its runs are ordinary. A cap alone saved 15 % of the reads. Counting per run also showed where the rest was: the verdict refuses on the first run out of proportion, and the walk went on to measure four more.
- The order of a walk is free when the walk is a pure function. Measuring the centre first reads the same pixels and turns the verdict's conditions, which are relations between runs, into a bound on each later run while it is being measured.
- Exactness was stated as a property of the verdict ("no vector outside a bound is accepted") and not of the walk, which is what let it be tested exhaustively without an image and let the walk's test be plain parity. It also made the grey second look a non-issue: it is asked only of vectors the near-miss check passes, so it cannot widen the set the bounds were derived for.
- A surviving mutant that tightens a bound is a statement about the bound. Three of them said the centre's lower bound has a pixel of slack; two terms that could never bind were found by asking the same question of the formula before the sweep.
- The minimum over passes rewards the luckiest pass. One pass in six of rung 1 read 118 us on an input where the other five read 163 to 174, and the minimum made rung 1 look 1.15x faster than rung 2 there, against every other pass. Rounds inside a pass share the machine's state and passes do not: across alternating passes the figure is the median, and three passes is the least that has one. Rung 1's table above was taken as minimums of two passes; its ratios were re-read here as medians and hold (0.83 to 0.92 then, 0.85 to 0.88 now, on the rendered version 40 inputs).
- A change can be slower than the change before it and faster than the code before both, and the two comparisons answer different questions. The plan's rule is about the second. The first is recorded because it is where the next variant would have to look.

### Phase 4, Finder search: the edge-list row kernel (2026-09-22)

Scope of this entry: rung 3 of Approach 4, which closes the ladder. The phase's closing measurement is at the end.

Done: `FinderPatternFinder.RowEdges.cs` (net8.0+). With 256-bit vectors, rows of 32 to 4,095 pixels go to `ScanRowEdges`: `ExtractRowEdges` turns 64 pixels into one word that is never stored and writes the word's rising and falling edges into an array of run starts and one of run ends; `ClassifyWindows` judges sixteen windows a step without a branch, strict ratio, near miss and small crisp runs, the ratio checks in integers and sharing one difference a run, everything inside a sixteen-bit lane; only flagged windows run scalar code, in row order, through the follow-ups and `TryAddCandidate` as before. The edge buffer is one `ArrayPool` rental a search, held by `TryFindCore` and `FindCandidatesCore`. Narrower and wider rows, 128-bit targets and netstandard keep the mask walk and the scalar walk, unchanged. `forceScalar` became `FinderRowKernel` (Auto, Scalar, MaskWalk, EdgeList) so that each kernel can be entered from a test; `TryFindScalar` is the Scalar kernel with the reference cross-check walks, as before.

The kernel search ran as a micro-benchmark outside this repository: the shipped `ScanRowMask` and everything it calls copied verbatim, `TryAddCandidate` replaced by a sink of the same signature that is not inlined (so the call site is the shipped one, after two rungs whose prototypes had a call the shipped code did not), one operation the stride pass over an image, a gate on the hit sequence (row, window end, five runs, in order) before anything is timed, a byte-identical canary in every run. Same-run ratios to the baseline over seven inputs (version 10 at 3 px; version 40 at 3, 3.4, 4 rotated, 4 soft and 8 px; noise 740 x 740), canary 0.94 to 1.13:

| Variant, each one change on its parent | Symbols | Noise | Verdict |
|---|---:|---:|---|
| Edge list from the mask (all edges of a row by tzcnt and blsr), float checks kept | 0.96 to 1.15 | 1.00 | Refuted: the walk never paid for finding the next bit |
| The strict ratio in integers, short-circuit kept | 0.94 to 1.02 | 0.89 | Refuted on symbols; the form the next one needs |
| Eight windows classified a step, flagged ones handled in order | 0.46 to 0.73 | 0.25 | Confirmed: the cost was the branches |
| Starts and ends in two arrays, a destination computed for every edge | 0.43 to 0.68 | 0.25 | Refuted: level with its parent |
| The mask held by the pass (no 512-byte stackalloc a row) | 0.91 to 1.17 | 0.99 | Refuted |
| Edges written eight at a time from a popcount | 0.47 to 0.71 | 0.25 | Refuted: level with its parent |
| One difference a run for both ratio checks | 0.41 to 0.68 | 0.26 | Confirmed, small (3 to 10 % at version 40; 5 to 10 % with AVX-512 off) |
| Rising and falling edges from their own bit sets, each array filled by its own loop | 0.38 to 0.65 | 0.21 | Confirmed: 12 to 19 % on its parent |
| Sixteen-bit positions, sixteen windows a step | 0.33 to 0.61 | 0.19 | Confirmed: 6 to 13 % on its parent |
| No mask array: a word built and its edges taken at once | 0.36 to 0.58 | 0.20 | Confirmed: 4 to 14 % on its parent on six inputs of seven, and the least code. Shipped |

With AVX-512 disabled the ranking is the same (the sixteen-bit form 0.33 to 0.56, noise 0.23). A 32-bit tier for rows of 4,096 pixels and more was measured (0.38 to 0.61) and not shipped: a second copy of the classification for images whose decode is the threshold's.

Tests first, and the first set was not enough. `FinderRowKernelParityTest` holds the candidate list of the EdgeList and the MaskWalk kernel to the Scalar kernel's, every centre, module size and count bit for bit, over symbols from 1.3 to 7 px a module crisp and blurred, fields of finders moved across the 64-pixel words and the sixteen-window steps, noise at every width around the block sizes with the threshold at its extremes and the grey second look on and off, rows all dark, all light, starting and ending dark, and rows of 4,095, 4,096 and 4,200 pixels; and `TryFind` under every kernel. It failed to compile, passed with the EdgeList kernel delegating to the mask walk (which showed that the scenes accepted 249 candidates, too few to mean much, before any kernel existed; the finder fields were added then), and stayed green with the kernel. Then 12 of 22 planted faults survived it. Full suite green on net8.0 and net10.0 (25,189 run, none failed).

Planted faults, a counted green baseline first: 22. Against the candidate parity alone 10 red and 12 survived: the run that ends at a width divisible by 64, the last pixel of the scalar tail, the threshold of 0, five faults in the three checks, and the two follow-ups skipped. None of these is a blind spot in the scenes. A row kernel that reports a window too many changes no result, because the cross-checks judge the same row again with the scalar checks; and a row kernel's fault at the far end of a row matters only when a finder sits there. So the kernel's two stages became functions of their own and are held directly (`FinderRowEdgesTest`): `ExtractRowEdges` to a pixel-by-pixel scan at every length from 1 to 200 and around 256, 512, 4,032 and 4,095, six kinds of row, four thresholds; `ClassifyWindows` to `IsFinderRatio`, `IsNearFinderRatio` and `IsSmallCrispFinderRuns` themselves, every run vector up to 14 pixels and three million at the tolerance edges up to the width limit. With those, 19 red. The three that survive are the near-miss flag kept with the grey levels off and the two follow-ups skipped after a flag: each only sends more windows to the cross-checks. They are held by the hit-sequence gate of the micro-benchmark and by nothing in this repository, and that is recorded here as a gap, not as an equivalence.

Benchmark delta, x64. `QRCodeImageDecodeEndToEnd` in `src/FeatherQR.Benchmark`, the committed tree (rung 2) exported beside the working one, the same class run from each, alternating, two runs a side, BenchmarkDotNet means in us:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 112 / 113 | 86 / 84 | 142 / 144 | 114 / 113 |
| v40-3.4px | 128 / 127 | 96 / 96 | 165 / 165 | 132 / 132 |
| v40-4px-rot17 | 421 / 420 | 282 / 270 | 494 / 497 | 352 / 350 |
| v40-4px-soft | 294 / 302 | 238 / 239 | 355 / 359 | 286 / 287 |
| v6-4px | 10.6 / 10.7 | 9.4 / 9.2 | 14.6 / 14.5 | 13.1 / 13.1 |
| none-gradient | 360 / 289 | 347 / 281 | 353 / 355 | 365 / 334 |
| none-noise | 2,472 / 2,383 | 1,471 / 1,346 | 2,417 / 2,419 | 1,402 / 1,402 |

The span decode 0.66 to 0.80 at version 40, 0.88 on the version 6 URL, 0.58 on no-symbol noise, the gradient level (it has no windows). Allocated unchanged on every row, the span rows at zero. The rotated shape gave back about 145 us where the kernel on its own saved about 45: see the first lesson.

The shared scan, same method, `MicroQRImageEndToEnd` and `RmQRImageEndToEnd`: Micro QR M4 image decode 5.05 to 4.77 us; rMQR R7x43 5.47 to 4.95 and R17x139 22.5 to 18.4 (span), 7.70 to 7.12 and 33.7 to 29.3 (bitmap); rMQR no-symbol noise 7,335 to 6,496 and gradient 107.8 to 96.7. The image generation rows of both classes, which do not reach the finder, read level (within 3 %) and are the run's canary.

The phase against the code before it (2f6e43a), from the three rungs' own measurements: a version 40 image decode at 3 px a module 126 to about 85 us, at 4 px rotated 451 to about 280, no-symbol noise 740 x 740 2,539 to about 1,410.

Lessons:
- A branch-free kernel is worth more inside a decode than alone. Phase 4 began by noticing that the search costs 1.4x to 1.7x its isolated figure inside a decode on the rotated class, because a stage looped alone runs one row sequence until its branches are learnt. The same fact now reads the other way: the kernel that has no such branches saved three times its isolated saving end to end. Isolated figures under-read a stage that is branch-bound, and they under-read its repair by the same factor.
- Two hypotheses in a row were wrong, and a stage profile of the variant would have replaced both rounds. After the first vector form the extraction loop's branches and the classification's instruction count were each tried for a round and each refuted. Three probes that cut the kernel short after a stage then gave 12 % mask, 29 % extraction, 36 % classification, 23 % hit handling, explained why the split-array form had been level (its extraction had grown by what its classification had lost), and every variant written after that was confirmed.
- A change that gains nothing can be what the change that gains needs. The edge list alone and the integer ratio alone were refuted, and the confirmed kernel is built on both. A variant's verdict is about that variant as an end point.
- Parity of results cannot hold a stage whose mistakes the next stage forgives. Twelve of twenty-two faults survived a bit-for-bit candidate comparison. The repair was not more scenes but two seams at the stage's own boundaries, tested against what each stage stands for. Three faults still survive, all over-reports, and the log says so.
- A benchmark run that reads 1.5x on code the change does not touch is the machine. The first Micro QR run read its PNG generation rows, identical in both trees, 1.47x to 1.58x slower on the changed side in both passes, and its decode row with them. Run again with the order reversed everything was level or better. The rows a change cannot reach are the canary of an end-to-end class, and they were only looked at because the decode row looked wrong.
- A code generation switch is verified in the disassembly. `DOTNET_EnableAVX512F=0` does nothing on .NET 10 (`DOTNET_EnableAVX512=0` does); the first check without AVX-512 reproduced the round before it and would have passed for the answer. The exported disassembly held 1,435 mask-register instructions, then none.

### Phase 5, ARM64 measurement (2026-09-22)

Scope of this entry: the measurement. The Otsu fill's ARM64 tier, which it points to, has its own entry.

Done: the phase 1 stage harness rebuilt (the first did not outlive the session that wrote it) and run on the ARM64 machine, Apple M2, 8 cores, .NET 10.0.301. There are no 256-bit vectors here, so every tier this plan shipped behind `Vector256.IsHardwareAccelerated` or `Avx2.IsSupported` is absent: the Otsu fill runs its scalar tier, the mesh sampler its column-table tier, the finder rows the mask walk. Seven trees: the code before the plan (fc1cc0c) and the commit of each step of phases 2 to 4. One harness compiled against each tree's library sources, the seven binaries checked to differ; 52 luminance images made once by the library's renderer at HEAD (versions 6, 10, 20 and 40 x 3, 4 and 8 px a module x hard, fractional, soft and rot; noise and a gradient at 740 and 1480, no symbol), the same classes as phase 1; the decode, each stage alone, and the stages stamped inside one decode body. Nine rounds an input with the arm order rotated each round, three passes a tree with the tree order rotated each pass; a figure is the median over passes of each pass's median. The decode arm runs first and last as the canary, and the two read within 1 % on every input. `QRCodeImageDecodeEndToEnd`, `MicroQRImageEndToEnd` and `RmQRImageEndToEnd` from the committed tree and from fc1cc0c with the image decode class copied in, alternating, two runs a side. No library source changed.

Two corrections to the harness before anything was read. Warmed 60 ms an arm, the first inputs were measured at Tier0 (a version 40 decode read 900 us, 715 once warmed 300 ms or more); the first input now warms 400 ms an arm. And version 20 rotated at 3 and 4 px decodes on its second attempt (the mesh samples and fails, the global transform reads it), as phase 1 recorded, so those two have a decode figure and no stage split.

Where a version 40 decode goes on ARM64, HEAD, us, the stages timed inside one decode body:

| | Total | Otsu | Finder | Mesh build | Sampling | Matrix | Otsu share |
|---|---:|---:|---:|---:|---:|---:|---:|
| 3 px hard | 722 | 455 | 197 | 12 | 45 | 17 | 63 % |
| 3.4 px fractional | 850 | 560 | 224 | 16 | 45 | 17 | 66 % |
| 4 px hard | 683 | 332 | 273 | 10 | 45 | 20 | 49 % |
| 4.4 px fractional | 1,180 | 806 | 299 | 13 | 45 | 19 | 68 % |
| 8 px hard | 822 | 468 | 285 | 11 | 45 | 18 | 57 % |
| 8.4 px fractional | 2,515 | 2,117 | 330 | 16 | 45 | 21 | 84 % |
| 3 / 4 / 8 px soft | 372 / 550 / 1,264 | 111 / 205 / 896 | 186 / 281 / 320 | 9 to 13 | 45 | 17 | 30 / 37 / 70 % |
| 3 / 4 / 8 px rot | 728 / 1,137 / 3,103 | 317 / 706 / 2,718 | 235 / 338 / 336 | 11 to 20 | 45 to 47 | 119 / 28 / 20 | 43 / 62 / 87 % |
| No symbol, noise / gradient 740 x 740 | 3,362 / 1,126 | 171 / 1,097 a polarity's fill | 1,551 / 34 a polarity | | | | |

The dimension estimate reads 0.2 to 0.8 us everywhere. The `SKBitmap` conversion adds 46 to 83 us at 3 and 4 px, 325 to 506 at 8 px. Version 20 has the same shape (total 107 to 1,020 us, Otsu 36 to 823, the finder 41 to 160, sampling 14); version 6 is 20 to 204 us with Otsu 42 to 75 % of it.

What each step of the plan does here, each tree against the one before it, version 40 unless named:

| Step | What runs on ARM64 | Ratio to the step before |
|---|---|---|
| Phase 2, the fill | The scalar tier only | Otsu 0.99 to 1.01 on every input: the unchecked references changed nothing here |
| Phase 2, the second polarity | Every target | No-symbol gradient 0.50 and 0.53, noise 0.92 and 0.95; the decodes level |
| Phase 3 | The column-table tier | Sampling 85 to 88 us down to 45 (0.51 to 0.55, version 20 0.55); the decode 0.91 to 0.99 |
| Phase 4, rung 1 | Every target | The search 0.89 to 1.00, noise 0.96 |
| Phase 4, rung 2 | Every target | The search 0.94 to 0.99, noise 0.95 |
| Phase 4, rung 3 | The mask walk; no edge buffer is rented | The search 0.99 to 1.01, noise 0.99: level with rung 2, as it should be |

HEAD against the code before the plan: the decode 0.89 to 0.98 at version 40, 0.91 to 0.97 at version 20, 0.96 to 1.02 at versions 6 and 10, no-symbol noise 0.85 and 0.88, the gradient 0.50 and 0.53. No step loses on any class. Two readings over 1.00 were checked against their own passes: version 6 at 4 px rotated, 55.8 to 56.7 us, which every tree reads between 55.8 and 56.8; and the search on version 20 at 3 px soft, 1.03, which reads 42.2 to 47.0 across trees in no order. Nothing is gated or reverted.

`QRCodeImageDecodeEndToEnd`, BenchmarkDotNet means in us, two runs a side:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 780 / 786 | 722 / 727 | 827 / 835 | 768 / 774 |
| v40-3.4px | 930 / 944 | 868 / 888 | 981 / 1,001 | 929 / 932 |
| v40-4px-rot17 | 1,180 / 1,185 | 1,135 / 1,147 | 1,307 / 1,312 | 1,267 / 1,298 |
| v40-4px-soft | 619 / 626 | 553 / 554 | 703 / 709 | 631 / 659 |
| v6-4px | 34.3 / 34.6 | 34.1 / 34.3 | 40.9 / 41.1 | 40.6 / 42.0 |
| none-noise | 3,774 / 3,804 | 3,294 / 3,338 | 3,838 / 3,886 | 3,382 / 3,469 |
| none-gradient | 2,267 / 2,278 | 1,132 / 1,136 | 2,347 / 2,350 | 1,212 / 1,219 |

Micro QR M4 image decode 11.8 to 11.4 us; rMQR R7x43 13.1 to 12.9, R17x139 71 to 68 (one run after read 87 with an error of 627 and is not counted), no-symbol noise 8,488 to 8,035 and gradient 235 to 146 to 152. The PNG generation rows, which no step reaches, read level within 2 %. Allocated unchanged on every row, the span rows at zero.

Lessons:
- On ARM64 the plan so far is the part of it that is not a kernel. The three kernels that carried the x64 figures (the fill's 256-bit tier, the AVX2 mesh sampler, the edge list) do not run here, and a version 40 decode that x64 took to about a fifth of its old time is 0.89 to 0.98 of it here. What did move it is what every target runs: the mirrored histogram, the column table and the two cross-check rungs.
- The fill's scalar tier costs this core twice what it costs x64 wherever increments to one bin chain: 1.47 ns a pixel on the 3 px hard render against 0.71 on x64 before phase 2, 0.21 against 0.09 at 8 px, 2.0 against 1.56 on the gradient. On noise, where they do not chain, it is 0.31 against 0.29. The load-add-store chain is dearer here and the rest is not. That makes the threshold the largest stage on every input, 30 to 87 % of a version 40 decode, and ARM64's own tier of the fill the next step: Approach 2's item 4, which phase 2 left for this machine.
- A warmup that is enough on one machine is not evidence on another. The harness's 60 ms had been enough on x64 and left this one at Tier0, and a decode at Tier0 reads as a plausible slow machine. A stage figure that moves when the warmup doubles is not a figure.

### Phase 5, the Otsu fill's ARM64 tier (2026-09-22)

Done: `Binarizer.FillHistogramAdvSimd` (net8.0+, taken when `AdvSimd.Arm64.IsSupported` and 256-bit vectors are not), dispatched from `FillHistogram` after the 256-bit tier. It keeps that tier's 32-pixel blocks on two 128-bit loads, its dense cut-over at 12 and its untested stretch of 31 blocks, and differs in four places, each measured on the M2 against the scalar tier copied verbatim (its figures matched the stage harness's Otsu arm to within 2 %) before it was kept:

| Change, each on the one before | Two-valued | 8 px aligned | Rotated | Gradient | Photo-like | Noise | Micro QR sized |
|---|---:|---:|---:|---:|---:|---:|---:|
| The 256-bit tier on two 128-bit loads, masks by `ExtractMostSignificantBits` | 0.08 | 0.54 | 0.52 | 0.98 | 1.03 | 1.05 | 0.29 |
| Masks by a narrowing shift, one bit a byte | 0.06 | 0.39 | 0.46 | 0.98 | 1.02 | 1.05 | 0.21 |
| The zeros in byte counters, not popcounts | 0.04 | 0.27 | 0.45 | 0.97 | 1.03 | 1.05 | 0.15 |
| Dense blocks and the stretch over four lanes, one reference each, zeroed once, merged four bins a step | 0.04 | 0.27 | 0.45 | 0.22 | 0.83 | 0.99 | 0.18 |
| A block's extremes by one add across; the other-pixel mask only when a sparse block has any | 0.03 | 0.19 | 0.46 | 0.23 | 0.83 | 0.98 | 0.14 |

Scalar four lanes on their own read 0.31 to 0.34 two-valued, 0.22 gradient, 0.82 photo-like, 0.96 noise, 0.42 rotated, 1.00 aligned: the lanes are the dense classes' gain, the vector blocks the extremes'. Refused: two lanes (0.45 gradient) and eight (noise 1.14); a dense cut-over of 8 (rotated 0.50) or 16 (level); the sparse walk over two lanes (level); the index written as a native unsigned byte, which the JIT compiled to the same instructions.

The lanes are 4 KB, past the few hundred bytes the library allows on the stack, and are rented from `ArrayPool` once a call. Three placements were measured, a stack buffer, a rental and a 1.5 KB form (lane 0 the caller's histogram, lanes 1 to 3 `ushort`, drained every 32,000 groups); they differed by 2 to 10 %, and by the same amounts on two-valued inputs, which never touch the lanes, and a rental moved 32 or 256 ints in read identically to both offsets. That is code layout under dynamic PGO, as phase 2 found, not the storage. The rental was chosen and the decode A/B below cleared it on every class.

Tests first: `OtsuHistogramParityTest` gained the tier entered directly, inputs long enough that the zero counters drain many times and end at every phase of the drain, and a constant image counted after a noise image, which a lane left uncleared would show in every bin. It failed to compile, passed with the tier delegating to the scalar one, and stayed green with the tier. Full suite green on net10.0 (12,400 run, none failed) and on the net8.0 build rolled forward to the .NET 10 runtime (12,398), since this machine has no .NET 8 runtime: the net8.0 compilation paths ran, its JIT did not. Spec, spec map and the symbologies record's ARM64 note updated.

Planted faults, a counted green baseline first: 18, 17 red. The zero counters drained at 130 blocks, the extremes counted from 31 or not negated, the mask at 0x01, the low walk's index divided by 8, the high walk from 15, the lanes not cleared, the merge without lane 3, the fold adding 7, the last drain dropped, the 255s taken as every extreme, the tail one short, the stretch's step not undone, the stretch unclamped, the zeros counted twice, a dense block's last group dropped, the counters not reset after a drain. The survivor is equivalent: a narrowing shift of 3 instead of 4 leaves each byte's bit at 4i + 1, and the index is the same quarter of it.

Benchmark delta, the ARM64 machine. The stage harness built against the committed tree and the working tree, three alternating passes, the median; image decode, us:

| | before | after | Otsu before | after |
|---|---:|---:|---:|---:|
| Version 40, 3 px hard | 716 | 272 | 455 | 14 |
| 3.4 px fractional | 853 | 297 | 559 | 18 |
| 4 px hard | 675 | 340 | 332 | 24 |
| 4.4 px fractional | 1,179 | 383 | 807 | 29 |
| 8 px hard | 816 | 440 | 465 | 93 |
| 8.4 px fractional | 2,517 | 485 | 2,110 | 103 |
| 3 / 4 / 8 px soft | 366 / 551 / 1,259 | 353 / 521 / 1,088 | 110 / 204 / 888 | 97 / 172 / 705 |
| 3 / 4 / 8 px rot | 727 / 1,140 / 3,122 | 619 / 752 / 1,539 | 313 / 704 / 2,680 | 202 / 335 / 1,137 |
| Versions 6 to 20, all classes | 20 to 1,015 | 12 to 539 | | |
| No symbol, noise 740 / 1480 | 3,347 / 7,209 | 3,356 / 7,182 | 171 / 679 | 170 / 679 |
| No symbol, gradient 740 / 1480 | 1,133 / 2,174 | 324 / 903 | 1,097 / 1,894 | 243 / 630 |

`QRCodeImageDecodeEndToEnd`, the committed tree exported beside the working one, alternating, two runs a side, BenchmarkDotNet means in us:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 720 / 720 | 268 / 267 | 764 / 767 | 312 / 313 |
| v40-3.4px | 857 / 861 | 307 / 305 | 924 / 925 | 373 / 369 |
| v40-4px-rot17 | 1,137 / 1,141 | 753 / 755 | 1,266 / 1,276 | 877 / 882 |
| v40-4px-soft | 553 / 555 | 521 / 528 | 634 / 633 | 604 / 603 |
| v6-4px | 34.1 / 34.0 | 15.4 / 15.4 | 40.7 / 40.6 | 22.0 / 21.9 |
| none-noise | 3,310 / 3,298 | 3,338 / 3,299 | 3,392 / 3,388 | 3,396 / 3,397 |
| none-gradient | 1,121 / 1,134 | 317 / 317 | 1,204 / 1,212 | 400 / 399 |

Micro QR M4 image decode 11.4 to 6.4 us; rMQR R7x43 12.9 to 7.0, R17x139 67.6 to 34.7, no-symbol noise 8,012 to 7,677, gradient 145 to 124. The PNG generation rows, which do not reach the threshold, read level within 2 %. Allocated unchanged on every row, the span rows at zero.

Lessons:
- A refutation belongs to the machine it was made on. Phase 2 refused the lanes four times on x64, each time for L1 footprint, and on a core with four times the L1 they are the whole gain on gradients and photo-like images. The record said why they lost, and the reason was what did not transfer.
- Count the round trips between the vector and the general registers. The second-best form moved each other-pixel mask out to a general register and straight back for a popcount, which on this core is itself a vector instruction, twice a block. Counting the extremes where they already were, and building the mask only where it would be walked, was worth a third on the aligned render.
- The same address written two ways is two costs. One base plus 256·k and four references reach the same bins; the first puts a sign extension on the way to every load, and noise read 1.15 against 1.00.
- Where three placements differ by what a placement cannot cause, the difference is layout. The lanes' storage moved the two-valued inputs, which never touch the lanes, by as much as it moved noise. The choice was made on the rule it had to satisfy and confirmed by the decode A/B, not by the isolated ranking.
- The net8.0 build could only be run rolled forward here. The tier's gates are runtime checks and its code is the same on both frameworks, so this checks the compilation paths and not a .NET 8 JIT; CI runs the real one.

### Phase 5, the finder search's ARM64 row kernel (2026-09-22)

Done: the edge-list row kernel of phase 4 (`FinderPatternFinder.RowEdges.cs`) runs on ARM64. `IsEdgeListKernelSupported` is true under `AdvSimd.Arm64` as well; `DarkWord` builds a row's 64-pixel word per machine (x64's two 32-byte compares through the min identity, or the NEON fold the mask walk already used); on ARM64 the rising and the falling edges of a word are written by one loop, one of each an iteration; `ClassifyWindows` judges `ClassifyWindowLanes` windows a call, sixteen with 256-bit vectors and eight through `ClassifyWindows8` on 128-bit ones, whose lanes become bits by `LaneBits` (narrow, one weight a lane, add across); and `ScanRowEdges` on ARM64 turns a step's three verdicts into bits only after one add across their OR says a lane is flagged. The x64 path keeps its instructions; the mask walk stays the kernel for rows outside 32 to 4,095 pixels and for 128-bit targets without AdvSimd.

After the Otsu tier the search was 10 to 50 % of a version 40 decode on the M2 and 46 % of a no-symbol noise image. The kernel search ran as a micro-benchmark outside this repository: the shipped ARM64 mask walk copied verbatim as the baseline, `TryAddCandidate` a sink of the same signature that is not inlined, one operation the stride pass over an image, the gate the hit sequence (row, window end, five runs) over the seven inputs, symbols and finder fields from 1.3 to 7 px a module crisp and blurred, noise at 25 widths around the block sizes at four thresholds with the grey levels on and off, and rows all dark, all light, starting or ending dark, alternating and at the finder ratio. Two of three planted faults were red (1,092 hits on the 3 px input against 990 with a lane cut from the live mask); the third, a near miss handled with the grey levels off, survives as phase 4's gap does, because the follow-up refuses what the flag over-reports. Same-run ratios to the mask walk, canary within 6 %:

| Variant, each one change on its parent | v40 3 px / 3.4 px / 8 px | 4 px rot / soft | v10 3 px | Noise 740 | Verdict |
|---|---:|---:|---:|---:|---|
| The 256-bit kernel on 128-bit vectors, eight windows a step, bits by `ExtractMostSignificantBits` | 0.15 / 0.17 / 0.21 | 0.20 / 0.15 | 0.25 | 0.38 | Confirmed: the mask walk's cost is its branches here too |
| Verdict bits by narrow, weight and add across, and only once the OR of the three is non-zero | 0.14 / 0.16 / 0.20 | 0.19 / 0.14 | 0.25 | 0.38 | Confirmed, 3 to 8 %: `ExtractMostSignificantBits` on eight shorts is a sequence, three a step, and 364 of 7,455 windows are flagged |
| Sixteen windows a step as two halves | 0.16 / 0.19 / 0.27 | 0.19 / 0.18 | 0.24 | 0.36 | Refuted: the second half is wasted on every row's last step, and an 8 px row has about 40 windows |
| Rising and falling edges written by one loop | 0.14 / 0.16 / 0.19 | 0.16 / 0.13 | 0.26 | 0.37 | Confirmed, 8 to 22 % on symbols: each edge is a two-cycle chain (clear the bit, find the next), and two chains overlap where two loops in a row did not. Shipped |
| The same loop as do-while | 0.14 / 0.15 / 0.21 | 0.16 / 0.14 | 0.25 | 0.37 | Refuted: within the canary spread either way; the JIT decides the loop's rotation |

Probes that cut the kernel short split the shipped form on the 3 px input into the fold 0.02, the bit loops 0.06, the classification 0.03 and the flagged windows 0.03; on noise the flagged windows are 0.29 of 0.37: 7,209 of 45,163 windows are flagged there, 5,228 of them near misses that pay the grey coverage measurement, which the mask walk pays too. That is the shipped follow-up, not the row kernel, and it is the next candidate on an image with no symbol.

Tests first: `FinderRowEdgesTest`'s window checker is sized by `ClassifyWindowLanes` and the two classes' skips no longer name AVX2; the test project failed to compile on the constant, then passed with the kernel, both classes running on ARM64 with nothing skipped. Full suite green on net10.0 (12,404 run, none failed) and on the net8.0 build rolled forward to the .NET 10 runtime (12,402). Spec and spec map updated.

Planted faults in the ARM64 paths, a counted green baseline first (3 and 3 tests): 18, 15 red. The lane weights repeating 64, the bits unweighted, either leftover edge dropped, the falling set cleared twice, the fold's fourth load from 47, its last pairwise add dropped, the tail's pixel loop dropped, the centre's half total unrounded, the centre's difference from the single total, the near limit at 10, `enough` strict, the crisp centre from 2, sixteen lanes claimed on ARM64, the live mask one too wide. Three survived: the tail's shift by `i` instead of `i − x` (C# masks a shift count to six bits and `x` is a multiple of 64: equivalent), the flagged test without the live mask (dead lanes go on to the per-verdict bits, which are masked: slower, not wrong), and the near bits ungated by the grey levels, phase 4's recorded over-report gap.

Found on the way and filed as its own task: `GreyLevels.FromHistogram` enables the grey levels on a pure two-valued image when 255 times the count of 255s passes 2²⁴, because the light mean is computed in float and rounds above 255, so the "any pixel strictly between the levels" loop reaches bin 255 itself. A 555 x 555 render of a version 40 symbol at 3 px a module gets the near-miss second look and the same symbol at 3.4 px does not. It changes which windows reach the cross-checks, so it is outside this plan's rule and not fixed here; the benchmark scenes reproduce it as the library does.

Benchmark delta, the ARM64 machine. The stage harness built against the committed tree (the Otsu tier, 245ba84) and the working tree, three alternating passes, the median; us:

| | Image decode before | after | Finder before | after |
|---|---:|---:|---:|---:|
| Version 40, 3 px hard | 270 | 138 | 182 | 48 |
| 3.4 px fractional | 300 | 164 | 203 | 63 |
| 4 px hard | 343 | 177 | 246 | 80 |
| 4.4 px fractional | 385 | 213 | 280 | 106 |
| 8 px hard | 441 | 302 | 275 | 132 |
| 8.4 px fractional | 481 | 330 | 303 | 149 |
| 3 / 4 / 8 px soft | 356 / 520 / 1,090 | 211 / 326 / 938 | 183 / 277 / 311 | 38 / 75 / 153 |
| 3 / 4 / 8 px rot | 621 / 757 / 1,545 | 471 / 550 / 1,380 | 223 / 326 / 318 | 62 / 106 / 141 |
| Version 20, 3 / 4 / 8 px hard | 69 / 85 / 175 | 42 / 51 / 110 | | |
| Version 10, 3 / 4 / 8 px hard | 21 / 27 / 58 | 15 / 19 / 44 | 12.7 / 17.5 / 40.4 | 6.5 / 9.3 / 26.0 |
| Version 6, 4 px hard / soft / rot | 15.6 / 26.6 / 22.4 | 11.6 / 22.8 / 17.5 | | |
| No symbol, noise 740 / 1480 | 3,377 / 7,203 | 1,778 / 3,997 | 1,574 / 3,205 a polarity | 781 / 1,594 |
| No symbol, gradient 740 / 1480 | 321 / 914 | 315 / 904 | 34 / 114 | 31 / 107 |

The search 0.21 to 0.49 at version 40, 0.50 on noise, the gradient (no windows) level; the decode 0.51 to 0.89 on symbols, 0.53 and 0.55 on noise. `QRCodeImageDecodeEndToEnd`, alternating, two runs a side, BenchmarkDotNet means in us:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 269 / 271 | 137 / 139 | 314 / 317 | 187 / 190 |
| v40-3.4px | 307 / 311 | 161 / 161 | 370 / 370 | 221 / 225 |
| v40-4px-rot17 | 756 / 771 | 549 / 553 | 884 / 891 | 674 / 675 |
| v40-4px-soft | 530 / 524 | 334 / 328 | 605 / 608 | 407 / 414 |
| v6-4px | 15.4 / 15.4 | 11.6 / 11.6 | 22.0 / 22.2 | 18.2 / 18.4 |
| none-noise | 3,308 / 3,317 | 1,723 / 1,726 | 3,391 / 3,427 | 1,802 / 1,802 |
| none-gradient | 317 / 318 | 321 / 310 | 399 / 400 | 393 / 395 |

The shared scan: Micro QR M4 image decode 6.3 to 5.9 us; rMQR R7x43 7.0 to 5.8 and R17x139 34.5 to 25.1 (span), 10.0 to 8.7 and 48.0 to 39.2 (bitmap); rMQR no-symbol noise 7,742 to 6,346, gradient 124 to 108. The PNG generation rows read level within 1 %. Allocated unchanged on every row, the span rows at zero: the edge buffer is the one rental a search the x64 kernel already made.

The ARM64 machine against the code before this plan (fc1cc0c), from the phase 5 harness runs: a version 40 image decode at 3 px a module 782 to 138 us, at 3.4 px 910 to 164, 4 px rotated 1,185 to 550, 4 px soft 618 to 326; no-symbol noise 740 x 740 3,827 to 1,778, a gradient 2,274 to 315.

Lessons:
- The x64 kernel's structure transferred and its instruction choices did not. All edges at once and windows judged without a branch won here by the same margin; the movemask, the sixteen-window step and the bit extraction each had to be replaced, and the one of those tried as a straight transfer (sixteen windows as two halves) lost.
- A probe before a hypothesis, this time. Cutting the kernel short after each stage said the fold was a quarter of the extraction and the bit loops the rest before a variant was written for either; phase 4 had spent two rounds learning that the other way.
- What is left on noise is not the kernel's. Three quarters of the edge list's time on a no-symbol image is the coverage measurement on 5,228 near misses a polarity, the same in every kernel. A count of what the flags fire on, taken once, is what says where the next plan goes.
- A faithful copy reproduces the bugs too. The gate's two-valued 3 px scene came out with the grey levels on, and the copy was right: the library does that at 181,962 light pixels. The scene size was what exposed it.
- Layout again, on the smallest work. The 8 px input, about 40 windows a row, moved a variant by 20 % between runs and its canary by 6 %; every verdict here is a same-run ratio, and the do-while form was refuted for being inside that spread rather than for losing.

### Phase 5, the mesh sampler's ARM64 tier (2026-09-22)

Done: `QRImageDecoder.SampleGridPiecewiseAdvSimd` (net8.0+, taken under `AdvSimd.Arm64` after the AVX2 tier), the AVX2 tier's step on four lanes, two steps a loop body: a cell's start and span once a row, broadcast; a step straddling two cells selects per lane from the version's table (`TryBuildStepTable` now takes the lane count); the lerp as separate multiply and add; the clamp as a float minimum then a saturating conversion (fcvtzs, which is also what the scalar cast is on ARM64 on every runtime, so one form serves both frameworks where the AVX2 tier needs two) then an integer maximum with 0; the pixel index by one integer multiply-add; eight scalar loads packed into one word, one unsigned byte compare, one 8-byte store; every table through a hoisted reference, indexed unchecked. After the threshold and the search had their tiers the sampler was 45 of a 138 us version 40 decode on the M2, the largest stage.

The kernel search ran as a micro-benchmark outside this repository against the shipped column-table tier copied verbatim, gated on the x64 round's 442 scenes plus two images that are not square, onto a dirty buffer, every module against the reference; a planted fault (the two-cell select removed) was red. Same-run ratios to the column-table tier, canary within 1 %:

| Variant, each one change on its parent | v14 3 px | v20 4 px | v40 3 px / rot / 8 px rot | Verdict |
|---|---:|---:|---:|---|
| The AVX2 tier on four lanes | 0.56 | 0.54 | 0.55 / 0.56 / 0.57 | Confirmed, and half of what eight lanes gave x64 (0.24 of the same tier there) |
| Two four-lane steps a body, eight loads packed, one compare and store | 0.54 | 0.51 | 0.51 / 0.53 / 0.54 | Confirmed, 5 to 8 % |
| Tables through hoisted references, unchecked | 0.51 | 0.48 | 0.48 / 0.50 / 0.53 | Confirmed, 3 to 6 %: the step paid three or four bounds checks and frame reloads past the 256-byte immediate range |
| The index vector stored and reloaded as words instead of lane extracts | 0.61 | 0.59 | 0.58 / 0.59 / 0.60 | Refuted: a 16-byte store followed by word loads stalls on partial forwarding |
| The loop's runtime flag removed | 0.50 | 0.47 | 0.47 / 0.48 / 0.50 | 2 to 3 % |
| Cell parameters kept as broadcast vectors a row | 0.51 | 0.47 | 0.46 / 0.48 / 0.50 | Refuted, level: four `ldr q` cost what four `ldr s` + `dup` did |
| The index by one integer multiply-add | 0.50 | 0.47 | 0.46 / 0.47 / 0.50 | Confirmed, 1 to 2 %, exact on integers. Shipped |
| The eight pixels loaded straight into vector lanes (`ld1 {v.b}[i]`) | 0.50 | 0.47 | 0.46 / 0.47 / 0.48 | Refuted, level: `add` + `ld1` for `ldrb` + `orr`, one for one, and it needs a pointer |

The kernel is instruction-bound at eight to nine instructions a module and about four a cycle: two cell broadcasts, thirteen vector operations and the extraction (a lane move, a load and an or a module) for four modules. Nothing in the audit reopened it: the column and step tables are once a call, there is no cliff between sizes, and there is no recurrence. Four lanes are half of the AVX2 tier's gain on this machine and this ISA has no wider form.

Tests first: `SampleGridPiecewiseParityTest` lists the tier where it lists the AVX2 one, so on ARM64 every one of its scenes (63 tests: upright and bent at versions 14 to 40, clamps at each image edge, poison nodes, cells narrower than a step, buffers it cannot index) runs against it. It failed to compile, then passed. Full suite green on net10.0 (12,408 run, none failed) and on the net8.0 build rolled forward (12,406). Spec and spec map updated.

Planted faults, a counted green baseline first: 15, 13 red. The minimum's operands swapped, the maximum with 0 dropped, the multiply-add's operands swapped, the select dropped or fed the wrong table, the step table built for eight lanes, the high step taken from the low step's table entry, a pixel shifted into the next lane, the x limit from the height, the compare not strict, a cell span as the far node, the tail dropped, the fractions read a column on, the lerp fused. Two survived: the minimum's operand order, which mattered for `Avx.Min` and not for `Vector128.Min`, which returns NaN from either side on this runtime, and the four-lane leftover loop after the eight-module loop, which no Annex E dimension (17 + 4·version, 1 mod 4) ever enters; that loop was deleted and the scalar tail covers any other dimension.

Benchmark delta, the ARM64 machine. The stage harness against the committed tree (8907ea8) and the working tree, three alternating passes, the median; us:

| | Image decode before | after | Sampling before | after |
|---|---:|---:|---:|---:|
| Version 40, 3 px hard | 141 | 115 | 45.2 | 20.7 |
| 3.4 px fractional | 163 | 136 | 45.1 | 20.6 |
| 4 px hard | 182 | 155 | 45.0 | 20.7 |
| 4.4 px fractional | 210 | 186 | 45.1 | 20.6 |
| 8 px hard | 293 | 266 | 45.0 | 21.2 |
| 8.4 px fractional | 329 | 304 | 45.2 | 21.0 |
| 3 / 4 / 8 px soft | 211 / 325 / 934 | 186 / 304 / 913 | 45 | 20.5 / 20.7 / 21.0 |
| 3 / 4 / 8 px rot | 462 / 544 / 1,366 | 440 / 521 / 1,344 | 45 / 45 / 47 | 21.4 / 21.8 / 23.9 |
| Version 20, 3 / 4 / 8 px hard | 42 / 51 / 112 | 34 / 44 / 105 | 14.0 | 6.5 |
| No symbol, noise 740 | 1,791 | 1,756 | | |

Sampling 0.46 to 0.51 everywhere; the decode 0.81 to 0.99 on the mesh versions, level on version 6 and 10 (the global transform) and on the no-symbol inputs. `QRCodeImageDecodeEndToEnd`, alternating, two runs a side, BenchmarkDotNet means in us:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 139 / 138 | 114 / 115 | 187 / 187 | 162 / 161 |
| v40-3.4px | 162 / 160 | 138 / 135 | 221 / 222 | 196 / 195 |
| v40-4px-rot17 | 546 / 544 | 527 / 523 | 674 / 674 | 653 / 653 |
| v40-4px-soft | 328 / 324 | 302 / 304 | 407 / 405 | 385 / 384 |
| v6-4px | 11.6 / 11.6 | 11.6 / 11.6 | 18.2 / 18.1 | 18.2 / 18.1 |
| none-noise | 1,723 / 1,726 | 1,718 / 1,721 | 1,802 / 1,810 | 1,815 / 1,806 |
| none-gradient | 309 / 310 | 321 / 324 | 392 / 394 | 395 / 394 |

Micro QR M4 5.9 / 5.9 to 5.8 / 5.9 us, rMQR R7x43 5.7 / 5.8 to 5.8 / 5.7 and R17x139 25.6 / 25.4 to 25.0 / 25.6: level, as they must be, since neither reaches this sampler. The PNG rows read level within 1 %. Allocated unchanged on every row, the span rows at zero. The gradient's 309 to 321 is the run's spread on a row this change cannot reach (its threshold and search are untouched and it never samples); the same row read 317 / 318 and 321 / 310 across the two previous A/Bs.

The ARM64 machine against the code before this plan (fc1cc0c): a version 40 image decode at 3 px a module 782 to 115 us, at 3.4 px 910 to 136, 4 px rotated 1,185 to 521, 4 px soft 618 to 304; no-symbol noise 740 x 740 3,827 to 1,756, a gradient 2,274 to 321.

Lessons:
- Half the lanes is half the gain, once the loop is at the machine's issue width. The four-lane tier settled at 0.46 of the column table where the eight-lane tier is 0.24 of it on x64, and the variants that tried to buy the difference back by trading one instruction for another (cell vectors for broadcasts, lane loads for byte loads) each traded one for one and measured level. The count is what binds, and the count a lane costs is set.
- The conversion is one form here because the scalar cast is. The AVX2 tier carries two forms because x64's raw conversion changed to a saturating one in .NET 9; ARM64's has always been fcvtzs. A tier's shape follows the reference's cast on that machine, not the other machine's history; the parity test on this machine is what says so.
- A survivor can say the guard it defeats belongs to another ISA. The minimum's operand order was written into the AVX2 tier for `Avx.Min`, whose result with NaN depends on it; `Vector128.Min` has no such asymmetry and the fault could not move a pixel. The comment now says which.
- A survivor can also be dead code. The leftover four-lane loop was there for dimensions no caller passes; the sweep, not the reading, found it.

### Phase 5, one edge loop on both machines (2026-09-23)

Done: `ExtractRowEdges` writes a word's rising and falling edges by one loop, one of each an iteration, on x64 as well as ARM64; the `AdvSimd.Arm64.IsSupported` branch and x64's two loops are gone (7 lines in, 26 out). The question was whether the ARM64 form holds on x64, where the reason it won there, a two-cycle bit-clear chain, does not exist: the JIT emits `blsr`, one instruction.

The kernel search ran as a micro-benchmark outside this repository, on the ARM64 round's operation, sink, scenes and gate inputs: the shipped x64 `ScanRowEdges` copied verbatim as the baseline, a byte-identical canary of it and one of the variant, the gate the hit sequence against the mask walk plus both extractions compared edge for edge on every row (a dropped leftover falling edge: 456 hits against 1,092, red). Ryzen 9 7950X3D, .NET 10, three runs, same-run ratios to the baseline:

| Variant | v40 3 / 3.4 / 8 px | 4 px rot / soft | v10 3 px | Noise 740 | Verdict |
|---|---:|---:|---:|---:|---|
| Baseline canary | 0.99-1.04 / 0.94-1.02 / 0.98-1.02 | 1.02-1.03 / 0.96-1.03 | 0.98-1.06 | 0.99-1.05 (1.48 in the first run, which voids that column there) | |
| One loop, and its canary | 0.93-1.00 / 0.91-0.95 / 0.82-0.94 | 0.85-0.97 / 0.89-1.06 | 0.94-1.02 | 0.92-1.04 | Confirmed, small: never behind beyond the canaries, 3 to 10 % on version 40 symbols |
| One loop with native-width counts | 0.92 / 0.88 / 0.81 | 0.97 / 0.90 | 0.99 | 0.93 | Refuted: the sign extension went, and the JIT spilled both array bases to the stack instead, a load an edge; inside the one loop's canary spread |
| Probe: extraction alone, each form | 0.61-0.63 against 0.61-0.63 | 0.62-0.66 against 0.61-0.66 | 0.58 against 0.59 | 0.29 against 0.28 | Level |

The extraction alone is level, and the whole row pass is not: the extraction is about 62 % of the pass, so a pass 8 % faster would need the extraction 13 % faster, which it is not on its own. What the one loop changes is branches, one loop branch for two edges where the two loops paid one for each, and the classification and flagged-window loops of the same row share the predictor with it. That reading is consistent with the rotated input moving most and is not confirmed by counters.

Tests first: with the one loop still behind the ARM64 branch, dropping its leftover falling edge left `FinderRowEdgesTest` green on x64, since x64 never ran that code. After the change it and two more planted faults in the loop (the leftover rising edge dropped, the falling set cleared twice) are red on x64. Full suite green on net8.0 and net10.0 (25,181 run, 378 skipped as before, none failed).

Benchmark delta, x64. `QRCodeImageDecodeEndToEnd` on the committed tree (8907ea8) and the change, each exported outside the working tree, three launches of fifteen iterations, three alternating runs a side, the median; us:

| Shape | Span, before | Span, after | Bitmap, before | Bitmap, after |
|---|---:|---:|---:|---:|
| v40-3px | 95.9 | 94.0 | 125.4 | 125.5 |
| v40-3.4px | 109.8 | 106.9 | 146.8 | 149.5 |
| v40-4px-rot17 | 299.0 | 290.9 | 382.4 | 377.3 |
| v40-4px-soft | 263.0 | 263.0 | 312.0 | 316.8 |
| v6-4px | 10.06 | 10.27 | 14.12 | 14.56 |
| none-noise | 1,432 | 1,404 | 1,477 | 1,462 |
| none-gradient | 336 | 322 | 396 | 371 |

Level: every row within −3 to +3 % but the gradient, which has almost no edges for the change to reach and moved −4 to −6 %, which is the floor here. That is the expected size: a few percent of a row pass that is about a fifth of a version 40 decode. Allocated unchanged, the span rows at zero. The one Micro QR and rMQR run a side read 1.03 to 1.08 slower after, the PNG generation rows, which the change cannot reach, by the same 1.07 to 1.08: a slower pass, not a result. The change stays for what it removes: one code path where there were two, and the ARM64 form now run by x64 CI.

Lessons:
- A reason that does not transfer can still leave a result that does. The chain the ARM64 loop broke is not on x64, and the loop still won there for another reason, its branch count; the variant was worth measuring because the prediction was only "level".
- A stage probe can be blind to what the stage does to its neighbours. The extraction alone read level three times while the pass read 5 to 15 % faster; a branch change has to be judged inside the loop that shares the predictor, the same lesson the rotated decode taught about the row walk.
- The benchmark host found a second copy of the benchmark project in a worktree under `.claude/` and refused to build, and the first E2E pass wrote nothing but NA rows without failing the script. Both trees are now exported outside the repository, and the script stops on a build refusal.
