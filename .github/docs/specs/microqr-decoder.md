# Micro QR Decoder

This records what the Micro QR decoder (`MicroQRCodeDecoder`, ISO/IEC 18004, M1 to M4) does, why, and what was learned. Code locations are in the [spec-to-code map](microqr-spec-map.md), and what the three symbologies share is in [QR Symbology Architecture](qrcode-symbologies.md).

---

## What

`MicroQRCodeDecoder` decodes Micro QR symbols into text.

### Matrix level

The input is `MicroQRCodeData` or a byte-per-module span with its size, with or without a light quiet zone.

The decoder locates the core, takes the version from its size, reads the format information, extracts and unmasks the codewords, corrects them with Reed-Solomon up to the ISO capacity, and parses the bitstream. `MicroQRCodeDecodeInfo` reports the status, version, ECC level, mask and corrected codewords. The [spec-to-code map](microqr-spec-map.md#decoding-pipeline-overview-matrix-level) draws the stages.

### Image level

The input is an `SKBitmap` or a grayscale luminance span with its width and height (`TryDecodeImage`). An allocation-free variant writes the text to a span destination.

Each [shared image decode pass](qrcode-symbologies.md#image-decode-passes) tries the ranked finder candidates one at a time in the order the [shared candidate scan](qrcode-symbologies.md#single-finder-candidate-scan) gives them. For each it measures the module sizes and the centre, then tries two paths:

- The axis-aligned path, in each right-angle orientation, samples one grid per size from M4 down to M1, then a grid fitted to the timing patterns, and then, at low density, reads a table of module boundaries off the timing patterns.
- The arbitrary-orientation path finds the finder's axes with an angular sweep and samples one grid per size in each frame and orientation. Around any grid that gets past its format information, it runs a scale search and a perspective search.

The matrix level reads each grid and keeps a read only when the grid's structure justifies its corrections. A grid that does not read is read again transposed. A read that does not fit the destination counts as a read: it ends its candidate, and the scan once the other candidates of its finder scan have run ([single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan)).

In an image with grey levels, a per-size or search grid whose format word reads exactly is also read again by coverage, unless it read or gave a read too long for the destination. The [spec-to-code map](microqr-spec-map.md#image-detection-and-sampling) draws the stages and their order.

### Image decode figures

These figures show how the decoder reads each kind of input. [decode_figures.cs](../../../tools/decode_figures.cs) draws them from the library's own symbols and decodes each input through the public API, so every figure shows a real read. Rerun it when the pipeline changes.

#### Stage by stage

The figure shows a clean M3 on the main path.

![Stage by stage: luminance, global threshold, finder candidates, module sizes and centre, sizes M4 to M1, matrix decode](../images/microqr/decode-overview.svg)

- Luminance: the grey input.
- Global threshold: one Otsu split of the histogram into ink and paper.
- Finder candidates: patterns that show the 1:1:3:1:1 ratio along a line through their centre and pass the cross-checks, ranked most confirmed first.
- Module sizes and centre: one size along the row and one along the column, each from a dark-light-dark run through the centre that spans six modules, from the ring's inner edge on one side to its outer edge on the other.
- Sizes M4 to M1: one grid per size, anchored on the finder centre, largest first. The first grid that decodes ends the search.
- Matrix decode: the format information gives the version, which must match the grid's size. Unmasking, Reed-Solomon and the text follow.

#### Reading the input figures

Each figure shows the input, overlaid with what the decoder finds, next to its path through the [image-level stages](microqr-spec-map.md#image-detection-and-sampling). Every pass runs this path: the global threshold, the inverted image, the regional pass, then the midpoint sweep ([image decode passes](qrcode-symbologies.md#image-decode-passes)). The bar at the bottom is the matrix decode, with the rules given under Image level.

Green and grey boxes run for the input. Green marks the stages it depends on.

- Key (green): without this stage, the input would not read, or would read only after more failed grids than with it. A grid is one sampling, and its transposed and coverage reads count as the same grid.
- Runs (grey): runs as for any input.
- Only if needed (dashed): runs only when the grids before it fail.
- Skipped (faded): does not run for this input.

The numbers on the boxes match the numbered notes.

#### Clean

The input is an upright, crisp M3.

![Clean M3 and its path through the pipeline](../images/microqr/decode-input-clean.svg)

The M4 grid comes first and fails at its format information both ways round, then the M3 grid reads. The image has only two grey levels, so the grey-level stages stay off, and at this density the boundary table is not used.

#### Rotated or mirrored

The symbol is at any angle, either way round.

![Rotated and mirrored M4 and its path through the pipeline](../images/microqr/decode-input-rotated.svg)

1. Arbitrary orientation: the axis-aligned grids follow the image's rows and columns, so they fail on a turned symbol. Through a square finder's centre the dark-light-dark span is shortest along the finder's own axes, so an angular sweep finds them, and a grid is sampled along each frame it gives.
2. Matrix decode: a mirrored capture has the same finder and a transposed grid, so a grid that does not read is read again transposed.

Here the sweep's first frame lies along the symbol's axes, and its first grid reads transposed.

#### Keystone distortion

The input is a flat M4 tilted away at the bottom, its far edge inset by 4 % of the width at each end.

![Keystoned M4 and its path through the pipeline](../images/microqr/decode-input-keystone.svg)

1. Coverage re-read: a grid whose format word reads exactly is read again, each module's luminance interpolated at its centre and thresholded halfway between the two levels. Here the scale search's grid reads this way. Without the re-read, the search's next grid reads.
2. Arbitrary orientation: the axis-aligned grids fail. The sweep's first frame gives a grid that gets past its format information, fails, and starts the searches around it.
3. Scale search: the grid is resampled with the centre moved slightly and each axis's module size changed by a few percent. Here the first variant, a slightly smaller grid, reads.
4. Perspective search: the grid is sampled through a projective transform over a small range of the two coefficients one finder leaves unknown. It does not run here because the scale search reads first, but without the scale search it reads the symbol.
5. Matrix decode: Reed-Solomon corrects the data modules the grid still gets wrong. Without correction, a later grid reads.

One finder cannot measure perspective, so every grid starts from the finder's axes and module sizes, correct at the finder and drifting toward the far edge (dashed).

#### Grey edges

The input is an anti-aliased M4 at a little under two pixels per module.

![Anti-aliased M4 and its path through the pipeline](../images/microqr/decode-input-grey-edges.svg)

1. Coverage re-read: the first grid fails, but its format word reads exactly, so the grid is read again by coverage and reads.
2. Matrix decode: Reed-Solomon corrects the few data modules the coverage read still gets wrong. Without correction, a later grid reads.

Grey levels also turn on the finder centroid, which here leaves the centre unchanged. The midpoint sweep runs only for a polarity whose global threshold found no finder, and here it found one.

#### Snapped scale

The input is a crisp M4 at a non-integer scale, with modules snapped to whole pixels.

![M4 at a snapped scale and its path through the pipeline](../images/microqr/decode-input-snapped.svg)

1. Timing frame: the finder measures a whole number of pixels per module, a few percent more than the symbol's pitch, so the grids scaled from it drift and fail. The timing patterns on row 0 and column 0 reach the far edge: their dark runs give the size, a line fitted to all their module boundaries gives the pitch and the corner, and the grid fitted this way reads.

The M4, M3 and M2 grids fail at their format information, and the M1 grid in Reed-Solomon.

#### Low density

The input is a crisp M4 at barely over one pixel per module.

![M4 at low density and its path through the pipeline](../images/microqr/decode-input-low-density.svg)

1. Low density: each module is one or two whole pixels wide, which no fitted grid follows, so the module boundaries are read off the timing patterns on row 0 and column 0 and each module is sampled between its own boundaries. Here one module on each timing line is two pixels wide (marked).

The grids scaled from the finder fail first, and no timing frame is fitted.

### Supported

| Area | Coverage |
|---|---|
| Versions | M1 to M4. The matrix size gives the version, and the format information must agree with it |
| ECC levels | L, M and Q, as each version defines them. M1 only detects errors |
| Data modes | Numeric, Alphanumeric, Byte (UTF-8, Shift_JIS or ISO-8859-1, chosen by the [shared heuristic](standardqr-decoder.md#decisions) because Micro QR has no ECI), Kanji (M3 and M4, JIS X 0208). The generator writes Kanji on request (`AllowKanji`) for text JIS X 0208 can represent |
| Quiet zone | Matrix: any uniform light border, with the core located from the finder's corner |
| Error correction | Reed-Solomon, capped at the capacity t of ISO Table 9. Corrections are reported |
| Format information | One 15-bit copy, with up to 3 bit errors corrected |
| Output | `string` (allocates only the result) or `Span<char>` (allocation-free, image path included) |
| Image envelope | See below |

The measured image envelope (Tier 1 to 2) is kept conservative because a single finder gives few correspondences.

- Clean screen-rendered or scanned images, with arbitrary rotation, mirroring and reflectance reversal.
- A shading gradient or a soft-edged shadow over part of the symbol, in images at least 33 px a side (measured in [standardqr-decoder.md](standardqr-decoder.md)).
- Non-integer uniform scaling, including fixed-size renders down to 1.5 px/module. When the grid scaled from the finder fails, the grid is measured instead on the timing patterns, which reach the far edge.
- Crisp M2 to M4 renders between 1 and 1.5 px/module, read through a table of module boundaries instead of a fitted grid.
- Anti-aliased and resampled renders from 1.5 px/module. For these the finder centre is the centroid of its darkness, grids are read again by coverage as described under Image level, and after every other attempt a finder scan runs at the midpoint of the two levels for each polarity where the global threshold found no finder (image detection lessons in [standardqr-decoder.md](standardqr-decoder.md)).
- Non-square modules, within the envelope the symbologies share ([qrcode-symbologies.md](qrcode-symbologies.md)).
- Rings printed or read thicker or thinner than their modules (ink spread, a thin print, blur), found by the shared finder check through the distances between same-polarity edges ([qrcode-symbologies.md](qrcode-symbologies.md), drawn in [standardqr-decoder.md](standardqr-decoder.md#thin-or-thick-rings)). With all dark edges moved by 0.1, 0.15, 0.2 and 0.24 of a module, 600, 586, 463 and 208 of 600 symbols read (M1 to M4, 3-6 px/module, turned 0-60°, grey or binarized).
- Translation and quiet-zone variants.
- Mild optical degradation: JPEG artifacts, low contrast, additive noise.
- Keystone: any one edge inset by up to 8 % of the symbol's width (quiet zone included) at each end, at any rotation, mirrored or not, from 5 px/module drawn crisp and from 3 px/module with grey edges.

Photographs have no envelope. On renders through a pinhole camera (2026-10-10, [qrcode-test-fixtures.md](qrcode-test-fixtures.md#where-the-gap-stands)), blur, noise, a bow, barrel distortion and a small symbol in a scene read 390 to 398 of 400, each more than zxing-cpp reads. Tilt past the keystone envelope does not: 267 of 400 read at 15-35° and 9 at 35-55°, where zxing-cpp reads 377 and 386. The 16 corpus photographs read at every right angle.

On the [image decode sweep](qrcode-test-fixtures.md#image-decode-sweep) of 2026-09-28, this library reads 17,887 of 18,400 images and zxing-cpp 0.5.2 reads 12,524 of them, none that this library misses. The sweep has 400 cases, a hundred a version, each encoded by this library and by libzint, with 800 renders a kind.

This library reads 798 to 800 of 800 in every kind except anti-aliased edges at 1.0-1.25 px/module (299) and 1.25-1.5 (794). Bilinear upscales at 2-2.5, where zxing-cpp led most when the sweep began on 2026-09-21 (54 renders only it read, of 1,200 a kind, when three encoders drew each case), read 798. zxing-cpp's kinds range from 30 (anti-aliased, 1.0-1.25) to 798 (crisp, 3-6). On the real images this library reads 64 of 64 and zxing-cpp 59.

### Not supported

- Strong perspective, because one finder cannot give the independent correspondences of Standard QR's three finders and alignment patterns.

## Why

- Micro QR has its own explicitly typed decoder, and `QRCodeDecoder` stays Standard QR only, so default Standard QR scanning performance is unaffected.
- Every size is tried instead of reading the size first. Micro QR has four sizes, and the format word must match the size its grid was sampled at, so a wrong-size grid almost always fails at the format word. Larger sizes go first, M4 down to M1. Each grid is anchored on the finder, so one smaller than the symbol samples a garbled sub-grid of it (a real M4 sampled as M2), and from M4 down a symbol's own size is tried before its sub-grids, the search ending at the first success.
- The axes come from the finder alone. One finder cannot give the orientation the way Standard QR's three finders do, so its local axes come from an angular sweep of dark-light-dark runs, and the decoder searches over the two projective coefficients it leaves unknown. This covers arbitrary rotation and mild perspective, but not strong perspective.
- The scale and perspective searches turn one grid into hundreds, so they start only once a grid's format information decodes, either way round. Wrong grids overwhelmingly fail before that point.

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Correction capacity | Reed-Solomon corrections are capped at ISO Table 9's t, enforced after correction. The misdecode-protection codewords p make full Reed-Solomon strength wrong: for M2-L it could correct 2 errors, but the standard allows 1 | - |
| Grid evidence | In every pass, a sampled grid's read is kept only when the grid's structure justifies its corrections. The evidence is summed in bits from Reed-Solomon's strength at the correction count, the format word's distance, the timing modules and, when those fall short, the quiet-zone modules along the right and bottom edges. A grid below the bar fails like a correction failure. M1 corrects nothing and is never kept on its format word and timing alone. This rule replaced refusals by level (Lessons) | M1 near-miss misreads are a class the structure cannot tell apart from the real symbol (Lessons). The lead is a check on the data that keeps an M1 read only when a second grid, its frame nudged by a fraction of a module, reads the same codewords |
| Format gate before the searches | A frame starts the scale and perspective searches when its format copy reads within 3 bits. A 1-bit gate cut a failing decode on 1 px noise from 108 to 18 ms (2026-09-23) but lost 215 sweep renders (a 2-bit gate lost 57): anti-aliased and bilinear renders below 2.5 px/module and keystones past 6 %, whose copy reads 2-3 bits off at the unrefined frame and which the searches' refinement reads. A second test on those frames' timing modules was no better: over frames that went on to read, the unrefined frame's timing was up to 71 % wrong, overlapping noise end to end. The real images lose nothing either way, so the sweep decides this setting | - |
| Image search budgets | The shared scan's first eight candidates ([single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan)), and at most 10,000 matrix decodes per candidate on the arbitrary-orientation path, not counting coverage re-reads. That leaves room for one complete frame (four axis assignments and four sizes) and keeps the result independent of CPU speed | A measured input class needs more |
| Too-small destination is terminal per finder candidate | The rule, its reason, its earlier cost and the skip of a candidate inside the symbol that read are in the [single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan). In Micro QR, a read that does not fit also ends the transposed read of the same grid. Reading the transpose after such a read used to let one scale search run, at about 40 times the cost of a sized call on a process's first calls and 70 times once warm (2026-10-01) | As in the shared record's residuals |
| Coverage re-read gate | A grid is read again by coverage only when the image has grey levels and the grid read its format word exactly. A real symbol's format modules sit next to the finder, where the frame is most accurate. Texture reads a format word within 3 bits about half the time, and an exact one about 1 in 1,000 | - |

## Lessons learned

- The Micro QR terminator has the structure of a Numeric mode indicator followed by an all-zero count field: mode bits (v−1) + numeric count bits (v+2) = 2v+1 terminator bits. Decoding it as "a zero-count Numeric segment ends the stream" needs no special terminator scan and handles terminators truncated at capacity at no extra cost.
- The ECC codeword counts include the misdecode-protection codewords p (ISO Table 9), so a decoder wired directly to full Reed-Solomon strength would silently correct ⌊ecc/2⌋ errors where the standard allows only t (for example 2 instead of 1 for M2-L). The capacity cap is enforced after correction and has its own equivalence-class tests.
- A clean symbol exits after its syndromes, so the clean scenarios cannot rank the correction path. `MicroQR_*_Corrected_Decode` flips modules and keeps a draw only when the decoder reports exactly that many corrected errors, because a flip on a function pattern carries no codeword. Each sits at its ISO capacity t: M2-L with 1 error went 194 → 300 ns, and M4-M with 5 errors 366 → 1,013 ns (x64, 2026-10-02).
- Quiet-zone stripping cannot reuse Standard QR's dark bounding box: with a single finder, the right and bottom edges are data modules, with no guarantee that any is dark. The top-left dark module is the finder's corner instead, and a uniform border gives the core size.
- libzint (through the ZXingCpp wrapper) rejects UTF-8 Micro QR payloads ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics came back from the round trip transliterated ("naïve café" became "naive cafe"). UTF-8 fixture coverage therefore comes only from the qrtool lineage, and the sanity gate's payload comparison catches such silent drift before a fixture is committed.
- From about 8 % keystone a side, the perspective search reads what the scale search cannot, so the envelope was widened to the measured range instead of dropping the search. Inside the range first stated (2 % a side for M1 and M2, 4 % for M3 and M4, top edge only), it read nothing the scale search missed, at 4 to 8 px/module drawn crisp and 2 to 8 with grey edges.

  Below 4 px/module drawn crisp, the two searches compete for the attempt budget: without the perspective search, 17 renders were lost at 3 px/module, and at 2 px/module 85 were lost and 61 gained. At 8 px/module, over 19 symbols, 4 edges, 36 angles and both mirrorings (5,472 renders), the decoder reads all at 8 % a side and 5,392 at 10 %, and without the perspective search 5,131 and 2,522.

  The perspective search costs about a quarter of the grids a failing image decodes. Without it, failures ran 16 to 29 % faster where the attempt budget holds and 4 to 17 % faster on noise, which exhausts the budget on every candidate (measured 2026-09-27).
- A grid uses only the corrections its structure justifies, judged by summed evidence instead of refusals by level (2026-09-24). A failing image sends about 19,000 grids through Reed-Solomon, and a random word passes a level with `e` corrections with probability `V(n, e) / 256^ecc`: in bits, 16 for M1, 20.8 for M3-M at its limit of 4, 24.7 to 26.5 for M4-M, M3-L and M2-M at theirs, 28.7 and 29.0 for M2-L and M4-L, and 37.6 for M4-Q. Texture was read in that order too.

  Before the rule, 1,000,000 Standard QR and rMQR images decoded as Micro QR (1.8-4 px/module, turned or not, crisp or anti-aliased, evenly lit, under a ramp or a shadow, half negated) gave 1,088 M1 reads and 22 at a level's limit, evenly lit ones included, and with the regional pass also 25 verdicts at a limit. The regional pass at first refused by level what texture passed as: any M1 read, an empty read that needed correction, and M3-M and M4-M at their limit. Each refusal cost real reads, and texture then landed on the next level. Refusing by error count cannot tell a real symbol at its limit from a false one.

  `MicroQRGridEvidence` instead adds up these terms, in bits:

  - Reed-Solomon's strength at the read's correction count.
  - The format word's distance: the share of the words within 3 bits of a candidate that lie that close.
  - The timing modules' mismatches: their binomial tail at even odds.
  - When those fall short, the dark count on the quiet-zone ring next to the symbol: its tail at 0.3.

  It keeps a read whose sum reaches 33 bits, a bar of about 2^-19 an image at 2^14 grids before the bitstream's own validity is checked.

  Each term was calibrated on those grids. The format distances matched the combinatorics: exact words were 9.6e-4 measured against 9.8e-4 predicted, and words within 1 bit 1.57 % against 1.56 %. The timing modules matched no better than even odds. The quiet-zone line was light far more often than random because the grid hung over a margin, except beside a clean timing line, where about one module in three was dark. The finder and separator count as no evidence, because a Standard QR corner has them too. A symbol that shows its structure can use every correction, and M1 never reads on its format and timing alone.

  False results on the foreign set fell from 1,110 to 2. M1 under a 40 % shadow became readable: 3,265, 101 and 0 of 10,000 renders became 9,987, 9,996 and 9,998. The rule lost 0.6 % of M1 reads at 1-2.5 px/module, anti-aliased or resampled.

  The structure is counted after Reed-Solomon reads a grid. M2-M4 already reach 33 bits at zero corrections, M1's Reed-Solomon check is cheaper than reading its quiet zone, and the check first placed in front of Reed-Solomon made noise 8 % slower. The cost falls on symbols past the lighting bounds: a turned anti-aliased M1 under a 55 % shadow fails in 2.6 ms, where the refusal gave up at its first M1 grid in 0.5 ms.
- The bar and the ring were measured, and texture and a near-miss grid are different adversaries. At bars of 29, 31, 33 and 35 bits, low-density M1 losses and misreads were 77 and 25, 174 and 20, 274 and 16, and 516 and 11, while over the same bars the predicted foreign false results fall from 7.7 to 0.3 a million (measured before the quiet zone moved to its final sampler). Both are small at a bar of 33. A bar of 36, which excludes the one foreign verdict traced, lost 21 of 200,000 mixed renders where 33 lost 8, both with the second ring described below.

  The ring 1.5 modules out, clear of anti-aliased grey, cut low-density M1 losses from 256 to 63, but near-miss misreads rose from 17 to 21, and two real M1 misreads and an M4-M foreign verdict came back. A grid a little too small puts the first ring on the symbol's own edge, and only that ring sees it. Both rings together gave 34 losses and 28 misreads. The decoder counts only the ring next to the symbol. Counting the worse timing line twice kept the verdict it was aimed at and doubled the losses.

  Structure evidence rejects texture, and only the ring next to the symbol sees a near-miss grid. A change that helps against one adversary can make the other worse, so each has its own measuring set.
- Against the other readers, each reader's design explains its results (2026-09-24, zxing-cpp 0.5.2 with `TryHarder`, CodeGlyphX 2.1.0 on RGBA input):

  | Set | This library | zxing-cpp | CodeGlyphX |
  |---|---|---|---|
  | 1,000,000 foreign images, false reads | 1 | 274 | 2,306 |
  | 200,000 mixed renders (M1-M4, 1.5-6 px/module, any turn, lit or shaded, 15 % negated), read / misread | 194,520 / 3 | 166,280 / 18 | 148,116 / 853 |
  | 200,000 evenly lit at 1-2.5 px/module, read / misread | 169,606 / 17 | 54,397 / 64 | 62,817 / 1,214 |
  | M1 under a 40 % shadow: crisp along the axes, crisp turned, anti-aliased | 9,987, 9,996, 9,998 of 10,000 | 9,821, 9,936, 9,890 | 3,237, 100, 0 |

  zxing-cpp samples one grid a finder (the best of four orientations by format distance), checks two timing modules, rejects only a quiet zone more than two-thirds dark, and corrects past the misdecode-protection cap. Sampling few grids keeps its false reads low and costs it reads. CodeGlyphX checks finder, separator and timing modules against fixed budgets whatever the correction count, and a Standard QR corner passes those checks. Neither refuses by error count.
- M1 near-miss misreads remain unfixed, and structure cannot find them. After the grey-level frame, 8 remain in about 395,000 renders: 3 in the evenly lit set at 1-2.5 px/module and 5 in the mixed set, three of those at 1.63, 1.66 and 4.29 px/module under 19-20 % shadows. Each is a grid near the real symbol with the right finder, timing, format and quiet zone but data mis-sampled in 3 or more codewords. M1's 16 bits of detection pass such a grid once in 65,536, across the thousands of grids a hard render gets.

  The refined finder centre is not biased on them (0.04-0.08 module from the true centre). A different starting point changes which grids the search tries, and so which such grid gets through. The lead is the check on the data in Decisions. Re-sampling nudged grids from the kept read's corners measured nothing, because the corner transform is not faithful at 1-2 px/module.

  The two foreign results left are both rMQR images read at M3-L's limit. The one traced reached 35.0 bits, with its timing 1 of 14 off and its format 3 bits off. Two in the 1,000,000 foreign images are the rate the bar is set for, about 2^-19 an image, and two images do not establish a cause.

  Two misreads seen before the rule have not been checked since: a Micro QR symbol turned 30° holding the unmapped Kanji cell 0x8794 read as "95549", and an M4-L holding an unmapped cell, anti-aliased at 1.60 px/module, read as M1 "0824".
- Each grid is sampled once and read in both orientations (2026-09-24). A failing decode on 512 × 512 noise-like images spent 90 % of its time in the scale and perspective searches: sampling 32 %, codeword extraction 27 %, Reed-Solomon 13 %, the format read 9 % and the transposed copy for the mirrored read 5 %.

  The mirrored read now goes through a view that transposes the module index, and codeword extraction through a per-size placement table built from the encoder's own predicates. The affine sampler has a 128-bit tier. The format word is looked up in a 32 KB table of the words within 3 bits of each candidate, and each entry carries the distance. The groups around different candidates never overlap, so one lookup is the whole search.

  Those parts went from 132 to 46 ms a failing decode (2.9x), giving 1.5-1.7x end to end. Skipping sweep candidates the strided scan already decoded ([image decode passes](qrcode-symbologies.md#image-decode-passes)) raised the end-to-end gain to 1.9x on 8-bit noise, more than expected, because on texture the sweep's top eight are largely the strided scan's. Every result is unchanged.

  These were refuted:

  - Reading modules on demand through the transform, format first (1.2x). Both orientations read about 190 data modules each, more than one whole-grid sample's 289, and each read paid a sample's float work.
  - Remembering grids already decoded. On 8-bit noise, 523 of 197,432 grids repeated the one before and 993 an earlier one. On a Standard QR image, 19 % did.
  - Stopping Reed-Solomon once the error locator's degree passes the capacity. The result is not identical, because a block the cap rejects reports its corrected-error count, which only the full correction computes.

  A module read on demand is cheaper only when fewer modules are read.
- Codeword extraction gathers each byte's bits in a register instead of branching on each module (2026-10-03). The branch hid behind benchmarks that decode one symbol over and over. The branch predictor learns that symbol's modules, and on symbols that change from call to call it misses about half the time. It surfaced when the cross-language benchmark's NativeAOT build ran M4's matrix decode about 1.6 times slower than the JIT on an Apple M2. Both compilers emitted the same loop body, but ILC's block layout, with one more taken branch per module, let the predictor learn the repeated symbol less well. On 1,024 symbols in random order the two builds were close, at 0.94 µs under the JIT and 1.07 µs under NativeAOT.

  Without the branch, and with each byte stored once instead of one bit at a time, M4's matrix decode takes 0.33 µs on changing symbols under either build, and M2 and M3 decode changing symbols 2.3 to 2.8 times faster. On the repeated symbol the JIT went from 0.31 to 0.28 µs and NativeAOT from 0.55 to 0.30 µs. A failing image decode reads a grid of texture in each attempt, so failures got faster too: `StandardQRImage_ImageDecode_Fails` from 17.5 to 13.0 ms and `M1_TurnedUnderShadow_ImageDecode_Fails` from 3.07 to 2.72 ms (BenchmarkDotNet, same M2). Every result is unchanged, held by a parity test against a per-module walk that reads no placement table.
- Every search uses the refined centre and the coverage re-read. Without the coverage re-read in the scale and perspective searches, 34 bilinear and 17 anti-aliased reads at 1.25-2 px/module were lost. Without the refined finder centre in the arbitrary-orientation search, bilinear upscales at 1.5-2 read 761 of 800 instead of 798. Each orientation frame refines its own copy of the centre along its own axes, starting from the already refined centre. Starting from the raw candidate read 6 renders fewer at 1.25-1.5. The refined centre costs time on another symbology's image: the searches go deeper on rMQR's identical finder, and an rMQR image read as Micro QR went from 0.75 to 1.7 ms.
