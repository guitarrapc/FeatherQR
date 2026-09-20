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

Subsampling the histogram would be far faster than any of this and is out: it changes the threshold.

### 3. Everything else

Named after phase 1 ranks it, not before. The finder search is already a strided SIMD mask walk and the alignment search a SIMD window sweep; what is left in them at 1480 x 1480 is not known, and the matrix plan's fifth phase is on record for what a hypothesis written before the code was read is worth.

## Risks

| Risk | Why it matters | Answer |
|---|---|---|
| Synthetic images flatter the histogram work | A hard-edged two-valued render is the best case for counting extremes and the worst for the shipped walk | Every figure carries its input class; anti-aliased renders through the real renderer and photo-like input are arms of phase 1, not afterthoughts |
| Identity is harder to state for an image stage than for a kernel | A finder or sampling change can move a float by an ulp and still decode | The reference is the shipped stage's output, compared exactly; a candidate that cannot meet that is out of scope by the table above |
| A shared stage regresses a symbology this plan does not measure | `Binarizer` serves Micro QR and rMQR, whose images are small and wide, not large and square | A Micro QR and an rMQR image decode are arms of every Otsu measurement |
| The not-found path | It runs the threshold and the full scan and exits; it is also the adversarial input | `NotDetected` on noise and on a gradient are arms of every measurement |
| Machine noise | The ranges in Purpose spread 1.5x | Canary arm, alternating A/B against the committed tree, a quiet machine for anything quoted |
| ARM64 and the browser | The matrix plan's stage ranking changed on ARM64 (Reed-Solomon went from 1 % of a decode to 28 %) | A measurement phase on the ARM64 machine before the fold; the Playground for the browser |

## Phases

Each phase follows the test-first workflow, updates the decoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Measure | A stage harness over `DecodeLuminance` with a canary arm: threshold, finder search, dimension estimate, mesh, sampling, matrix decode, and the number of `SampleAndDecode` attempts; versions 6, 20 and 40; 3, 4 and 8 px a module; hard edges, the real renderer's anti-aliased output, a photo-like degradation; upright and rotated; the `SKBitmap` entry beside the luminance one; `NotDetected` on noise and a gradient; a large-symbol image shape added to the benchmark project | A ceiling per stage per input class, stated as a number with its spread; the attempts count explained wherever it is above one; a stage under about 5 % everywhere is dropped from the plan with that number recorded |
| 2 | **P0** | Otsu histogram | The variant ladder of Approach 2; the winner per tier with runtime dispatch; parity tests first | Histogram, threshold and `GreyLevels` identical to the shipped walk over random images, two-valued images at every module size from 1 to 16 px, images with no extremes, all-one-value images and lengths that are not a multiple of the vector width; planted faults each red; no input class slower, noise included; kernel ratio and end-to-end delta per class |
| 3 | P1 | The next stage | Whatever phase 1 ranks second, written into this row when phase 1 is done | Set with the row |
| 4 | P1 | ARM64 measurement | The phase 1 profile and the shipped candidates on the ARM64 machine, same harness | A number per arm and class; anything that loses there is gated or reverted |
| 5 | P2 | Fold | Decisions and measurements into `specs/standardqr-decoder.md`, the shared binarizer's into `specs/qrcode-symbologies.md`; this plan deleted | The spec carries what was decided and why |

Phase 1 can end the plan early in one way: if the attempts count explains most of "rest", the work is a decision about the retry ladder, which changes what decodes first and belongs with the accuracy work, not here.

## Verification notes

- The harness compiles the library sources, so internal stages are called directly; private ones are made `internal` only when a phase ships a change to them, never for the measurement.
- Rendered inputs for the benchmark shape come from the library's own renderer at fixed sizes, so the anti-aliasing measured is the anti-aliasing users produce.
- The baseline for any end-to-end delta is the committed tree exported beside the working one and built into a second binary of the same harness, the two checked to differ, run alternately.

## Progress log

(empty)
