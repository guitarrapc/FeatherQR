# Micro QR Decoder

Design record for the Micro QR decoder (`MicroQRCodeDecoder`, ISO/IEC 18004, M1 to M4): what it does, why, and what was learned. Code locations are in the [spec-to-code map](microqr-spec-map.md); what the three symbologies share is in [QR Symbology Architecture](qrcode-symbologies.md).

---

## What

`MicroQRCodeDecoder` decodes Micro QR symbols back into text.

### Matrix level

Input: `MicroQRCodeData`, or a byte-per-module span with its size, with or without a light quiet zone.

Behavior: locate the core, take the version from its size, read the format information, extract and unmask the codewords, correct them with Reed-Solomon up to the ISO capacity, and parse the bitstream. `MicroQRCodeDecodeInfo` reports the status, version, ECC level, mask and corrected codewords. The stages are drawn in the [spec-to-code map](microqr-spec-map.md#decoding-pipeline-overview-matrix-level).

### Image level

Input: an `SKBitmap`, or a grayscale luminance span with its width and height (`TryDecodeImage`), with an allocation-free span-destination variant.

Behavior: each of the [shared image decode passes](qrcode-symbologies.md#image-decode-passes) takes the ranked finder candidates in turn. For each, it measures the module sizes and centre, then tries:

- the axis-aligned path, in each right-angle orientation: a grid per size from M4 down to M1, then a grid fitted to the timing patterns, then, at low density, a table of module boundaries read off them;
- the arbitrary-orientation path: the finder's axes from an angular sweep, a grid per size in each frame and orientation, and a scale search and a perspective search around any grid that gets past its format information.

Each grid goes through the matrix level, keeps only the corrections its structure earns, and is read again transposed if it does not decode. In an image with grey levels, a per-size or search grid whose format word reads exactly is also read again by coverage. The stages and their order are drawn in the [spec-to-code map](microqr-spec-map.md#image-detection-and-sampling).

### Image decode figures

These figures show how the decoder reads each kind of input. [decode_figures.cs](../../../tools/decode_figures.cs) draws them from the library's own symbols and decodes each input through the public API, so no figure can show a read that does not happen. Rerun it when the pipeline changes.

#### Stage by stage

A clean M3 on the main path.

![Stage by stage: luminance, global threshold, finder candidates, module sizes and centre, sizes M4 to M1, matrix decode](../images/microqr/decode-overview.svg)

- **Luminance:** the grey input.
- **Global threshold:** one Otsu split of the histogram into ink and paper.
- **Finder candidates:** patterns that read 1:1:3:1:1 along a line through their centre and pass the cross-checks, most confirmed first.
- **Module sizes and centre:** one size along the row and one along the column, each from a dark-light-dark run through the centre spanning six modules, from the ring's inner edge on one side to its outer edge on the other.
- **Sizes M4 to M1:** a grid anchored on the finder centre for each size, largest first; the first that decodes ends the search.
- **Matrix decode:** the format information names the version, which must match the grid's size; then unmasking, Reed-Solomon and the text.

#### Reading the input figures

Each figure shows the input, with what the decoder finds drawn over it, next to the path through the [image-level stages](microqr-spec-map.md#image-detection-and-sampling). Every pass runs this path: the global threshold, the inverted image, the regional pass, then the midpoint sweep (see [image decode passes](qrcode-symbologies.md#image-decode-passes)). The matrix decode (the bar at the bottom) keeps only the corrections a grid's structure earns, and reads the grid again transposed if it does not decode.

Green and grey boxes run for this input; green marks the stages it depends on.

- **Key** (green): without this stage, the input would not read, or would read only after more grids fail than with it. A grid is one sampling; its transposed and coverage reads are the same grid.
- **Runs** (grey): runs as for any input.
- **Only if needed** (dashed): runs only when the grids before it fail.
- **Skipped** (faded): does not run for this input.

The numbers on the boxes match the numbered notes.

#### Clean

An upright, crisp M3.

![Clean M3 and its path through the pipeline](../images/microqr/decode-input-clean.svg)

The M4 grid comes first and fails at its format information both ways round; the M3 grid then reads. With only two grey levels, the grey-level stages stay off, and at this density the boundary table is not used.

#### Rotated or mirrored

Any angle, either way round.

![Rotated and mirrored M4 and its path through the pipeline](../images/microqr/decode-input-rotated.svg)

1. **Arbitrary orientation:** the axis-aligned grids follow the image's rows and columns, so they fail on a turned symbol. Through a square finder's centre, the dark-light-dark span is shortest along the finder's own axes, so an angular sweep finds them, and a grid is sampled along each frame it gives.
2. **Matrix decode:** a mirrored capture has the same finder and a transposed grid, so a grid that does not decode is read again transposed.

Here the sweep's first frame lies along the symbol's axes, and its first grid reads transposed.

#### Keystone distortion

A flat M4 tilted away at the bottom, its far edge inset by 4 % of the width at each end.

![Keystoned M4 and its path through the pipeline](../images/microqr/decode-input-keystone.svg)

1. **Coverage re-read:** a grid whose format word reads exactly is read again with each module's luminance interpolated at its centre and split halfway between the two levels. Here the scale search's grid reads this way; without it, the search's next grid reads.
2. **Arbitrary orientation:** the axis-aligned grids fail. The sweep's first frame gives a grid that gets past its format information and then fails, which starts the searches around it.
3. **Scale search:** the grid again with the centre moved slightly and each axis's module size changed by a few percent. The first variant, a slightly smaller grid, reads here.
4. **Perspective search:** the grid through a projective transform, over a small range of the two coefficients one finder leaves unknown. It does not run here, since the scale search reads first; without the scale search, it reads the symbol.
5. **Matrix decode:** Reed-Solomon corrects the data modules the grid still gets wrong; without correction, a later grid reads.

One finder cannot measure perspective, so every grid starts from the finder's axes and module sizes: correct at the finder, drifting toward the far edge (dashed).

#### Grey edges

An anti-aliased M4 at a little under two pixels per module.

![Anti-aliased M4 and its path through the pipeline](../images/microqr/decode-input-grey-edges.svg)

1. **Coverage re-read:** the first grid fails, but its format word reads exactly, so the grid is read again by coverage, which succeeds.
2. **Matrix decode:** the coverage read still has a few data modules wrong, which Reed-Solomon corrects; without correction, a later grid reads.

Grey levels also turn on the finder centroid, which leaves the centre in place here. The midpoint sweep runs only for a polarity whose global threshold found no finder; here it found one.

#### Snapped scale

A crisp M4 at a non-integer scale, with modules snapped to whole pixels.

![M4 at a snapped scale and its path through the pipeline](../images/microqr/decode-input-snapped.svg)

1. **Timing frame:** the finder measures a whole number of pixels per module, a few percent over the symbol's pitch, so the grids scaled from it drift and fail. The timing patterns on row 0 and column 0 reach the far edge: their dark runs give the size, and a line fitted to all their module boundaries gives the pitch and the corner. That grid reads.

The M4, M3 and M2 grids fail at their format information, and the M1 grid in Reed-Solomon.

#### Low density

A crisp M4 at barely over one pixel per module.

![M4 at low density and its path through the pipeline](../images/microqr/decode-input-low-density.svg)

1. **Low density:** each module is one or two whole pixels, which no fitted grid follows. The module boundaries are read off the timing patterns on row 0 and column 0, and each module is sampled between its own boundaries. Here one module on each timing line is two pixels wide (marked).

The grids scaled from the finder fail first, and no timing frame is fitted.

### Supported

| Area | Coverage |
|---|---|
| Versions | M1 to M4, named by the matrix size, which the format information must agree with |
| ECC levels | L, M and Q, as each version defines them; M1 only detects errors |
| Data modes | Numeric, Alphanumeric, Byte (UTF-8 or ISO-8859-1 by heuristic, since Micro QR has no ECI), Kanji (M3 and M4, JIS X 0208; decode only) |
| Quiet zone | Matrix: any uniform light border; the finder's corner locates the core |
| Error correction | Reed-Solomon, capped at the capacity t of ISO Table 9; corrections reported |
| Format information | One 15-bit copy, up to 3 bit errors corrected |
| Output | `string` (allocates the result only) or `Span<char>` (allocation-free, image path included) |
| Image envelope | See below |

Image envelope, measured (Tier 1 to 2, kept conservative because a single finder gives few correspondences):

- clean screen-rendered or scanned images, with arbitrary rotation, mirroring and reflectance reversal;
- a shading gradient or a soft-edged shadow over part of the symbol, in images at least 33 px a side (measured in [standardqr-decoder.md](standardqr-decoder.md));
- non-integer uniform scaling, including fixed-size renders down to 1.5 px/module: when the grid scaled from the finder fails, the grid is measured on the timing patterns instead, which reach the far edge;
- crisp M2 to M4 renders between 1 and 1.5 px/module, read through a table of module boundaries instead of a fitted grid;
- anti-aliased and resampled renders from 1.5 px/module: the finder centre is the centroid of its darkness, a grid whose format word reads exactly is read again by coverage, and, after every other attempt, a finder scan runs at the midpoint of the two levels for a polarity where the global threshold found none (see the image detection lessons in [standardqr-decoder.md](standardqr-decoder.md));
- non-square modules within the envelope the symbologies share ([qrcode-symbologies.md](qrcode-symbologies.md));
- rings printed or read thicker or thinner than their modules (ink spread, a thin print, blur), found by the shared finder check through the distances between edges of the same polarity ([qrcode-symbologies.md](qrcode-symbologies.md), drawn in [standardqr-decoder.md](standardqr-decoder.md#thin-or-thick-rings)): symbols whose dark edges all moved by 0.1, 0.15, 0.2 and 0.24 of a module (M1 to M4, 3-6 px/module, turned 0-60°, grey or binarized) read 600, 586, 463 and 208 of 600;
- translation and quiet-zone variants;
- mild optical degradation: JPEG artifacts, low contrast, additive noise;
- keystone: any one edge inset by up to 8 % of the symbol's width (quiet zone included) at each end, at any rotation, mirrored or not, from 5 px/module drawn crisp and from 3 px/module with grey edges.

Against zxing-cpp 0.5.2, the [image decode sweep](qrcode-test-fixtures.md#image-decode-sweep) on the same images (2026-09-28; 400 cases, a hundred a version, each encoded by this library and libzint, 800 renders a kind): this library reads 17,887 of 18,400 and zxing-cpp 12,524, and zxing-cpp reads no render this library does not. Every kind reads 798 to 800 of 800 but anti-aliased edges at 1.0-1.25 px/module (299) and at 1.25-1.5 (794); bilinear upscales at 2-2.5, where zxing-cpp's lead was largest when the sweep began (54 of 1,200), read 798. zxing-cpp's kinds range from 30 (anti-aliased, 1.0-1.25) to 798 (crisp, 3-6). On the real images this library reads 64 of 64 and zxing-cpp 59.

### Not supported

- Strong perspective: one finder cannot provide the independent correspondences that Standard QR's three finders and alignment patterns give.

## Why

- **An explicitly typed decoder.** `QRCodeDecoder` stays Standard QR only and Micro QR has its own entry point, so default Standard QR scanning performance is unaffected.
- **Every size is tried rather than read first.** Micro QR has four sizes, and the format word must name the size its grid was sampled at, so a grid of the wrong size almost always fails there. Larger sizes go first: a real M4 sampled as M2 reads a garbled sub-grid, while trying the real sizes first exits at the first success.
- **The axes come from the finder alone.** One finder cannot give the orientation the way Standard QR's three do. Its local axes come from an angular sweep of dark-light-dark runs, and the two projective coefficients it leaves unknown are searched. That covers arbitrary rotation and mild perspective, not strong perspective.
- **The searches start only after a format word.** The scale and perspective searches turn one grid into hundreds, so they start only once a grid's format information decodes either way round; wrong grids overwhelmingly fail before that.

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Correction capacity | Reed-Solomon corrections are capped at ISO Table 9's t, since the misdecode-protection codewords p make full Reed-Solomon strength wrong (M2-L: Reed-Solomon could correct 2, the standard allows 1). The cap is enforced after correction | - |
| Grid evidence | One rule in every pass: a sampled grid keeps only the corrections its structure earns. Reed-Solomon's strength at the correction count, the format word's distance, the timing modules and, when those fall short, the quiet-zone modules along the right and bottom edges are summed in bits, and a grid below the bar fails as a correction failure does. M1, which corrects nothing, is never kept on its format word and timing alone. It replaced refusals by level (see Lessons) | M1 near-miss misreads are a class the structure cannot tell from the real symbol (Lessons); the lead is a check on the data, an M1 read kept only when a second grid, the frame nudged by a fraction of a module, reads the same codewords |
| Format gate before the searches | A frame opens the scale and perspective searches when its format copy reads within 3 bits. Within 1 bit cut a failing decode on 1 px noise from 108 to 18 ms (2026-09-23) and lost 215 sweep renders (within 2 bits, 57): anti-aliased and bilinear renders below 2.5 px/module and keystones past 6 %, whose copy reads 2-3 bits off at the unrefined frame and which the searches' refinement reads. The timing modules as a second key for those frames were no better: over frames that went on to read, the unrefined frame's timing was up to 71 % wrong, overlapping noise end to end. The real images lose nothing either way, which is why the sweep decides here | - |
| Image search budgets | At most eight finder candidates per scan, most confirmed first; at most 10,000 matrix decodes per candidate on the arbitrary-orientation path (coverage re-reads not counted), which leaves room for one complete frame (four axis assignments and four sizes) and keeps the result independent of CPU speed | A measured input class needs more |
| Too-small destination is terminal per finder candidate | As in rMQR ([rmqr-decoder.md](rmqr-decoder.md#decisions)): a read that does not fit the destination ends the candidate that made it, the transposed read of the same grid and every later grid and search included, and the other candidates are still tried, so another symbol that fits is read. The grids are tried in the same order whatever the destination, so the read that does not fit is the one a sized call returns, and a later grid of the candidate could only read something else. Until 2026-09-30 only a read that fit ended a candidate: on 577 renders (turned, 2.5 to 8 px/module, noise), a destination one character short cost 105 times a sized call at the median and up to 1,095 times; it now costs 1.00 times at the median and at most 5.8 times, the sized reads unchanged and every short one `DestinationTooSmall` either way. Each candidate keeps its own result, since a read that did not fit on one must not end the next at its first search. A later candidate inside the symbol that read is skipped: symbols do not overlap, so it is a finder-like pattern in that symbol's own data, and it was searched in full after the read, what the 5.8 times left. With it skipped the worst of the 577 was 1.5 times (2026-10-01) | A false finder candidate outside the symbol that read is still searched in full after it: revisit if a caller probing its buffer size meets one |
| Coverage re-read gate | A grid is read again by coverage only when the image has grey levels and the grid read its format word exactly. A real symbol's format modules sit next to the finder, where the frame is best; texture reads a word within 3 bits about half the time and an exact one about 1 in 1,000 | - |

## Lessons learned

- The Micro QR terminator is structurally a Numeric mode indicator followed by an all-zero count field (mode bits (v−1) + numeric count bits (v+2) = 2v+1 terminator bits). Decoding it as "a zero-count Numeric segment ends the stream" needs no special terminator scanning and handles terminators truncated at capacity for free.
- The ECC codeword counts include the misdecode-protection codewords p (ISO Table 9): a decoder wired directly to full Reed-Solomon strength would silently correct ⌊ecc/2⌋ errors where the standard allows only t (for example 2 against 1 for M2-L). The capacity cap is enforced after correction and has its own equivalence-class tests.
- Quiet-zone stripping cannot reuse Standard QR's dark-bounding-box trick: Micro QR has a single finder, so the right and bottom edges are data modules with no darkness guarantee. The top-left dark module is the finder's corner, and a uniform border gives the core size.
- libzint (through the ZXingCpp wrapper) rejects UTF-8 Micro QR payloads outright ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics came back from the round trip transliterated ("naïve café" became "naive cafe"). UTF-8 fixture coverage therefore comes from the qrtool lineage only, and the sanity gate's payload comparison is what catches such silent drift before a fixture is committed.
- The perspective search reads what the scale search cannot from about 8 % keystone a side, so the envelope was widened to where it was measured instead of the search being dropped. Inside the range first stated (2 % a side for M1 and M2, 4 % for M3 and M4, top edge only) it read nothing the scale search missed, at 4 to 8 px/module drawn crisp and 2 to 8 with grey edges. Below 4 px/module drawn crisp the two searches compete for the attempt budget: without it, 17 renders were lost at 3 px/module, and 85 lost and 61 gained at 2. At 8 px/module, over 19 symbols, 4 edges, 36 angles and both mirrorings (5,472 renders), it reads all of them at 8 % a side and 5,392 at 10 %; without it, 5,131 and 2,522. It costs about a quarter of the grids a failing image decodes: without it, failures ran 16 to 29 % faster where the attempt budget holds, and 4 to 17 % on noise, which exhausts the budget on every candidate (measured 2026-09-27).
- **A grid uses only the corrections its structure earns, and the evidence is summed, not refused by level** (2026-09-24). A failing image sends about 19,000 grids through Reed-Solomon, and a random word passes a level with `e` corrections with probability `V(n, e) / 256^ecc`: 16 bits for M1, 20.8 for M3-M at its limit of 4, 24.7 to 26.5 for M4-M, M3-L and M2-M at theirs, 28.7 and 29.0 for M2-L and M4-L, 37.6 for M4-Q. That was the order in which texture was read. Over 1,000,000 Standard QR and rMQR images decoded as Micro QR (1.8-4 px/module, turned or not, crisp or anti-aliased, evenly lit, under a ramp or a shadow, half negated), the decoder before the rule read 1,088 as M1 and 22 at a level's limit, evenly lit ones included, and with the regional pass it also gave 25 verdicts at a limit. The regional pass first refused by level what texture passed as (any M1 read, an empty read that needed correction, M3-M and M4-M at their limit), and each refusal cost real reads and left the next level texture landed on; refusing by error count cannot tell a real symbol at its limit from a false one. `MicroQRGridEvidence` instead adds, in bits, Reed-Solomon's strength at the read's correction count, the format word's distance (the share of the words within 3 bits of a candidate that lie that close), the timing modules' mismatches (their binomial tail at even odds) and, when those fall short, the dark count on the quiet-zone ring next to the symbol (its tail at 0.3), and keeps the read at 33 bits, about 2^-19 an image at 2^14 grids before the bitstream's own validity. Each term was calibrated on those grids: the format distances fell where the combinatorics put them (exact: 9.6e-4 measured, 9.8e-4 predicted; within 1 bit: 1.57 % and 1.56 %), the timing modules matched no better than even odds, and the quiet-zone line was light far more often than random, the grid hanging over a margin, except beside a clean timing line, where about one module in three was dark. Finder and separator are no evidence: a Standard QR corner has a Micro QR's. A symbol showing its structure uses every correction; M1 never reads on its format and timing alone. The foreign set went 1,110 false results to 2, M1 under a 40 % shadow became readable (3,265, 101 and 0 of 10,000 renders to 9,987, 9,996 and 9,998), and 0.6 % of M1 reads at 1-2.5 px/module, anti-aliased or resampled, were lost. The structure is counted after Reed-Solomon reads a grid, not before: M2-M4 at zero corrections already reach 33 bits, and M1's correction is cheaper than reading its quiet zone (the check first placed in front of Reed-Solomon made noise 8 % slower). The price is on symbols past the lighting bounds: a turned anti-aliased M1 under a 55 % shadow fails in 2.6 ms, where the refusal gave up at its first M1 grid in 0.5.
- **The bar and the ring were measured, and texture and a near-miss grid are different adversaries.** Low-density M1 losses and misreads at bars of 29, 31, 33 and 35 bits were 77 and 25, 174 and 20, 274 and 16, 516 and 11, while the predicted foreign false results fall from 7.7 to 0.3 a million (measured before the quiet zone moved to its final sampler); 33 is where both are small, and 36, which excludes the one foreign verdict traced, lost 21 of 200,000 mixed renders where 33 lost 8, both measured with the second ring below. The ring 1.5 modules out, clear of anti-aliased grey, cut low-density M1 losses from 256 to 63 and let near-miss misreads rise from 17 to 21, with two real M1 misreads and an M4-M foreign verdict back: a grid a little too small puts the first ring on the symbol's own edge, and only that ring sees it. Both rings: 34 losses, 28 misreads. Counting the worse timing line twice kept the verdict it was aimed at and doubled the losses. Structure beats texture, only the ring next to the symbol sees a near-miss grid, and a change that helps against one can arm the other, so each has its own measuring set.
- **Against the other readers, the designs explain the rows** (2026-09-24, zxing-cpp 0.5.2 with `TryHarder`, CodeGlyphX 2.1.0 on RGBA input):

  | Set | This library | zxing-cpp | CodeGlyphX |
  |---|---|---|---|
  | 1,000,000 foreign images, false reads | 1 | 274 | 2,306 |
  | 200,000 mixed renders (M1-M4, 1.5-6 px/module, any turn, lit or shaded, 15 % negated), read / misread | 194,520 / 3 | 166,280 / 18 | 148,116 / 853 |
  | 200,000 evenly lit at 1-2.5 px/module, read / misread | 169,606 / 17 | 54,397 / 64 | 62,817 / 1,214 |
  | M1 under a 40 % shadow: crisp along the axes, crisp turned, anti-aliased | 9,987, 9,996, 9,998 of 10,000 | 9,821, 9,936, 9,890 | 3,237, 100, 0 |

  zxing-cpp samples one grid a finder (the best of four orientations by format distance), checks two timing modules, rejects only a quiet zone more than two-thirds dark and corrects past the misdecode-protection cap: few grids keep its false reads low and cost it reads. CodeGlyphX checks finder, separator and timing modules against fixed budgets whatever the correction count, which a Standard QR corner passes. Neither refuses by error count.
- **M1 near-miss misreads are left, and structure cannot find them.** After the grey-level frame, 8 in about 395,000 renders: 3 of the evenly lit set at 1-2.5 px/module and 5 of the mixed set, three of those at 1.63, 1.66 and 4.29 px/module under 19-20 % shadows. Each is a grid near the real symbol whose finder, timing, format and quiet zone are right and whose data is mis-sampled in 3 or more codewords, which M1's 16 bits of detection pass one time in 65,536 across the thousands of grids a hard render gets. The refined finder centre is not biased on them (0.04-0.08 module from the true centre); a different starting point changes which grids the search tries, and so which such grid gets through. The lead is the check on the data in Decisions. Re-sampling nudged grids from the kept read's corners measured nothing, because the corner transform is not faithful at 1-2 px/module. The two foreign results left are both rMQR images read at M3-L's limit (the one traced reached 35.0 bits, its timing 1 of 14 off and its format 3 bits off), the rate the bar is set for; two images do not make a cause. Seen before the rule and not checked since: a Micro QR symbol turned 30° holding the unmapped Kanji cell 0x8794 read as "95549", and an M4-L holding an unmapped cell, anti-aliased at 1.60 px/module, read as M1 "0824".
- **Each grid is sampled once and read in both orientations** (2026-09-24). A failing decode on 512 × 512 noise-like images spent 90 % in the scale and perspective searches: sampling 32 %, codeword extraction 27 %, Reed-Solomon 13 %, the format read 9 %, the transposed copy for the mirrored read 5 %. The mirrored read now goes through a view that transposes the module index, the codewords through a placement table per size built from the encoder's own predicates, the affine sampler has a 128-bit tier, and the format word is looked up in a 32 KB table of the words within 3 bits of each candidate (the balls never overlap, so the table is the search; each entry also carries the distance). Those parts went from 132 to 46 ms a failing decode (2.9x) for 1.5-1.7x end to end, and skipping sweep candidates the strided scan already decoded ([image decode passes](qrcode-symbologies.md#image-decode-passes)) took it to 1.9x on 8-bit noise, more than expected, because the sweep's top eight on texture are largely the strided scan's. Every result is unchanged. Refuted: modules read on demand through the transform, format first (1.2x: both orientations read about 190 data modules each, more than one whole-grid sample's 289, and each read paid a sample's float work); remembering grids already decoded (523 of 197,432 grids on 8-bit noise repeated the one before and 993 an earlier one; 19 % on a Standard QR image); stopping Reed-Solomon once the error locator's degree passes the capacity (not identical: a block the cap rejects reports its corrected-error count, which only the full correction computes). A module read on demand is cheaper only when fewer modules are read.
- **The refined centre and the coverage re-read belong in every search.** Kept out of the scale and perspective searches, the coverage re-read lost 34 bilinear and 17 anti-aliased reads at 1.25-2 px/module; without the refined finder centre in the arbitrary-orientation search, bilinear upscales at 1.5-2 read 761 of 800 instead of 798. Each orientation frame refines its own copy of the centre along its own axes, from the centre already refined (from the raw candidate, 6 renders fewer at 1.25-1.5). The refined centre costs another symbology's image: the searches on rMQR's identical finder go deeper, and an rMQR image read as Micro QR went from 0.75 to 1.7 ms.
