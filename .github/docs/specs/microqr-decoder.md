# Micro QR Decoder

This is the design record for the Micro QR decoder (`MicroQRCodeDecoder`, ISO/IEC 18004, M1 to M4). It covers what the decoder does, why, and what was learned. Code locations are in the [spec-to-code map](microqr-spec-map.md). What the three symbologies share is in [QR Symbology Architecture](qrcode-symbologies.md).

---

## What

`MicroQRCodeDecoder` decodes Micro QR symbols back into text.

### Matrix level

Input: `MicroQRCodeData`, or a byte-per-module span with its size, with or without a light quiet zone.

Behavior: the decoder locates the core and works out the version from its size. It then reads the format information, extracts and unmasks the codewords, corrects them with Reed-Solomon up to the ISO capacity, and parses the bitstream. `MicroQRCodeDecodeInfo` reports the status, version, ECC level, mask and corrected codewords. The stages are drawn in the [spec-to-code map](microqr-spec-map.md#decoding-pipeline-overview-matrix-level).

### Image level

Input: an `SKBitmap`, or a grayscale luminance span with its width and height (`TryDecodeImage`). An allocation-free variant writes the text to a span destination.

Behavior: each of the [shared image decode passes](qrcode-symbologies.md#image-decode-passes) tries the ranked finder candidates one at a time, in the order the [shared candidate scan](qrcode-symbologies.md#single-finder-candidate-scan) gives them. For each candidate, it measures the module sizes and the centre, then tries two paths:

- The axis-aligned path, in each right-angle orientation. It samples one grid per size from M4 down to M1, then a grid fitted to the timing patterns. At low density, it then reads a table of module boundaries off the timing patterns.
- The arbitrary-orientation path. An angular sweep finds the finder's axes, and the path samples one grid per size in each frame and orientation. Around any grid that gets past its format information, it runs a scale search and a perspective search.

Each grid goes through the matrix level, which keeps a read only when the grid's structure justifies the corrections it needed. A grid that does not read is read again transposed. A read that does not fit the destination counts as a read here. Such a read ends its candidate, and it ends the scan once the other candidates of its finder scan have run ([single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan)).

In an image with grey levels, a per-size grid or a search grid is also read again by coverage when its format word reads exactly and it neither read nor gave a read too long for the destination. The stages and their order are drawn in the [spec-to-code map](microqr-spec-map.md#image-detection-and-sampling).

### Image decode figures

These figures show how the decoder reads each kind of input. [decode_figures.cs](../../../tools/decode_figures.cs) draws them from the library's own symbols and decodes each input through the public API. No figure can therefore show a read that does not happen. Rerun the tool when the pipeline changes.

#### Stage by stage

A clean M3 on the main path.

![Stage by stage: luminance, global threshold, finder candidates, module sizes and centre, sizes M4 to M1, matrix decode](../images/microqr/decode-overview.svg)

- **Luminance:** the grey input.
- **Global threshold:** one Otsu split of the histogram into ink and paper.
- **Finder candidates:** patterns that show the 1:1:3:1:1 ratio along a line through their centre and pass the cross-checks, ranked most confirmed first.
- **Module sizes and centre:** one size along the row and one along the column. Each comes from a dark-light-dark run through the centre that spans six modules, from the ring's inner edge on one side to its outer edge on the other.
- **Sizes M4 to M1:** one grid per size, anchored on the finder centre, largest first. The first grid that decodes ends the search.
- **Matrix decode:** the format information gives the version, which must match the grid's size. Then come unmasking, Reed-Solomon and the text.

#### Reading the input figures

Each figure shows the input, with what the decoder finds drawn over it, next to the path through the [image-level stages](microqr-spec-map.md#image-detection-and-sampling). Every pass runs this path: the global threshold, the inverted image, the regional pass, then the midpoint sweep (see [image decode passes](qrcode-symbologies.md#image-decode-passes)). The matrix decode (the bar at the bottom) keeps a read only when the grid's structure justifies its corrections. It reads a grid again transposed unless the grid read, and a read that does not fit the destination counts as a read.

Green and grey boxes run for this input. Green marks the stages the input depends on.

- **Key** (green): without this stage, the input would not read, or would read only after more grids fail than with it. A grid is one sampling. Its transposed read and its coverage read count as the same grid.
- **Runs** (grey): runs as for any input.
- **Only if needed** (dashed): runs only when the grids before it fail.
- **Skipped** (faded): does not run for this input.

The numbers on the boxes match the numbered notes.

#### Clean

An upright, crisp M3.

![Clean M3 and its path through the pipeline](../images/microqr/decode-input-clean.svg)

The M4 grid comes first and fails at its format information both ways round. The M3 grid then reads. The image has only two grey levels, so the grey-level stages stay off. At this density the boundary table is not used.

#### Rotated or mirrored

Any angle, either way round.

![Rotated and mirrored M4 and its path through the pipeline](../images/microqr/decode-input-rotated.svg)

1. **Arbitrary orientation:** the axis-aligned grids follow the image's rows and columns, so they fail on a turned symbol. Through a square finder's centre, the dark-light-dark span is shortest along the finder's own axes. An angular sweep therefore finds those axes, and a grid is sampled along each frame the sweep gives.
2. **Matrix decode:** a mirrored capture has the same finder and a transposed grid, so a grid that does not read is read again transposed.

Here the sweep's first frame lies along the symbol's axes, and its first grid reads transposed.

#### Keystone distortion

A flat M4 tilted away at the bottom. Its far edge is inset by 4 % of the width at each end.

![Keystoned M4 and its path through the pipeline](../images/microqr/decode-input-keystone.svg)

1. **Coverage re-read:** a grid whose format word reads exactly is read again. Each module's luminance is interpolated at its centre and thresholded halfway between the two levels. Here the scale search's grid reads this way. Without the re-read, the search's next grid reads.
2. **Arbitrary orientation:** the axis-aligned grids fail. The sweep's first frame gives a grid that gets past its format information and then fails. That grid starts the searches around it.
3. **Scale search:** samples the grid again with the centre moved slightly and each axis's module size changed by a few percent. Here the first variant, a slightly smaller grid, reads.
4. **Perspective search:** samples the grid through a projective transform. It tries a small range of the two coefficients that one finder leaves unknown. It does not run here, because the scale search reads first. Without the scale search, it reads the symbol.
5. **Matrix decode:** Reed-Solomon corrects the data modules the grid still gets wrong. Without correction, a later grid reads.

One finder cannot measure perspective, so every grid starts from the finder's axes and module sizes. Such a grid is correct at the finder and drifts toward the far edge (dashed).

#### Grey edges

An anti-aliased M4 at a little under two pixels per module.

![Anti-aliased M4 and its path through the pipeline](../images/microqr/decode-input-grey-edges.svg)

1. **Coverage re-read:** the first grid fails, but its format word reads exactly. The grid is therefore read again by coverage, and that read succeeds.
2. **Matrix decode:** the coverage read still has a few data modules wrong, and Reed-Solomon corrects them. Without correction, a later grid reads.

Grey levels also turn on the finder centroid, which here leaves the centre where it was. The midpoint sweep runs only for a polarity whose global threshold found no finder. Here the global threshold found one.

#### Snapped scale

A crisp M4 at a non-integer scale, with modules snapped to whole pixels.

![M4 at a snapped scale and its path through the pipeline](../images/microqr/decode-input-snapped.svg)

1. **Timing frame:** the finder measures a whole number of pixels per module, a few percent more than the symbol's pitch. The grids scaled from it therefore drift and fail. The timing patterns on row 0 and column 0 reach the far edge. Their dark runs give the size, and a line fitted to all their module boundaries gives the pitch and the corner. The grid fitted this way reads.

The M4, M3 and M2 grids fail at their format information, and the M1 grid fails in Reed-Solomon.

#### Low density

A crisp M4 at barely over one pixel per module.

![M4 at low density and its path through the pipeline](../images/microqr/decode-input-low-density.svg)

1. **Low density:** each module is one or two whole pixels wide, and no fitted grid follows that. Instead, the module boundaries are read off the timing patterns on row 0 and column 0, and each module is sampled between its own boundaries. Here one module on each timing line is two pixels wide (marked).

The grids scaled from the finder fail first, and no timing frame is fitted.

### Supported

| Area | Coverage |
|---|---|
| Versions | M1 to M4. The matrix size gives the version, and the format information must agree with it |
| ECC levels | L, M and Q, as each version defines them. M1 only detects errors |
| Data modes | Numeric, Alphanumeric, Byte (UTF-8 or ISO-8859-1, chosen by heuristic because Micro QR has no ECI), Kanji (M3 and M4, JIS X 0208). The generator writes Kanji on request (`AllowKanji`) for text that JIS X 0208 can represent |
| Quiet zone | Matrix: any uniform light border. The finder's corner locates the core |
| Error correction | Reed-Solomon, capped at the capacity t of ISO Table 9. Corrections are reported |
| Format information | One 15-bit copy, with up to 3 bit errors corrected |
| Output | `string` (allocates only the result) or `Span<char>` (allocation-free, image path included) |
| Image envelope | See below |

Measured image envelope (Tier 1 to 2). It is kept conservative because a single finder gives few correspondences.

- Clean screen-rendered or scanned images, with arbitrary rotation, mirroring and reflectance reversal.
- A shading gradient or a soft-edged shadow over part of the symbol, in images at least 33 px a side (measured in [standardqr-decoder.md](standardqr-decoder.md)).
- Non-integer uniform scaling, including fixed-size renders down to 1.5 px/module. When the grid scaled from the finder fails, the grid is measured instead on the timing patterns, which reach the far edge.
- Crisp M2 to M4 renders between 1 and 1.5 px/module, read through a table of module boundaries instead of a fitted grid.
- Anti-aliased and resampled renders from 1.5 px/module. For these, the finder centre is the centroid of its darkness. A grid whose format word reads exactly is read again by coverage, unless it read or read too long for the destination. After every other attempt, a finder scan runs at the midpoint of the two levels for each polarity where the global threshold found no finder (see the image detection lessons in [standardqr-decoder.md](standardqr-decoder.md)).
- Non-square modules, within the envelope the symbologies share ([qrcode-symbologies.md](qrcode-symbologies.md)).
- Rings printed or read thicker or thinner than their modules (ink spread, a thin print, blur). The shared finder check finds them through the distances between edges of the same polarity ([qrcode-symbologies.md](qrcode-symbologies.md), drawn in [standardqr-decoder.md](standardqr-decoder.md#thin-or-thick-rings)). With all dark edges moved by 0.1, 0.15, 0.2 and 0.24 of a module, 600, 586, 463 and 208 of 600 symbols read (M1 to M4, 3-6 px/module, turned 0-60°, grey or binarized).
- Translation and quiet-zone variants.
- Mild optical degradation: JPEG artifacts, low contrast, additive noise.
- Keystone: any one edge inset by up to 8 % of the symbol's width (quiet zone included) at each end, at any rotation, mirrored or not. This holds from 5 px/module when drawn crisp and from 3 px/module with grey edges.

On the [image decode sweep](qrcode-test-fixtures.md#image-decode-sweep), this library reads 17,887 of 18,400 and zxing-cpp 0.5.2 reads 12,524 of the same images. zxing-cpp reads no render that this library does not. The sweep, run on 2026-09-28, has 400 cases, a hundred a version, each encoded by this library and by libzint, with 800 renders a kind.

In every kind this library reads 798 to 800 of 800, except anti-aliased edges at 1.0-1.25 px/module (299) and at 1.25-1.5 (794). Bilinear upscales at 2-2.5 read 798. That is the kind where zxing-cpp's lead was largest when the sweep began (54 of 1,200). zxing-cpp's kinds range from 30 (anti-aliased, 1.0-1.25) to 798 (crisp, 3-6). On the real images this library reads 64 of 64 and zxing-cpp 59.

### Not supported

- Strong perspective. One finder cannot provide the independent correspondences that Standard QR's three finders and alignment patterns give.

## Why

- **An explicitly typed decoder.** `QRCodeDecoder` stays Standard QR only, and Micro QR has its own entry point. Default Standard QR scanning performance is therefore unaffected.
- **Every size is tried rather than read first.** Micro QR has four sizes, and the format word must match the size its grid was sampled at. A grid of the wrong size therefore almost always fails at the format word. Larger sizes are tried first. A real M4 sampled as M2 reads a garbled sub-grid, while trying the real sizes first ends the search at the first success.
- **The axes come from the finder alone.** One finder cannot give the orientation the way Standard QR's three finders do. Its local axes come from an angular sweep of dark-light-dark runs, and the decoder searches over the two projective coefficients that one finder leaves unknown. This covers arbitrary rotation and mild perspective, but not strong perspective.
- **The searches start only after a format word.** The scale and perspective searches turn one grid into hundreds of grids. They therefore start only once a grid's format information decodes, either way round. Wrong grids overwhelmingly fail before that point.

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Correction capacity | Reed-Solomon corrections are capped at ISO Table 9's t. The misdecode-protection codewords p make full Reed-Solomon strength wrong: for M2-L, Reed-Solomon could correct 2 errors, but the standard allows 1. The cap is enforced after correction | - |
| Grid evidence | One rule applies in every pass: a sampled grid's read is kept only when the grid's structure justifies its corrections. The evidence is summed in bits from Reed-Solomon's strength at the correction count, the format word's distance, the timing modules and, when those fall short, the quiet-zone modules along the right and bottom edges. A grid below the bar fails, as a correction failure does. M1 corrects nothing and is never kept on its format word and timing alone. This rule replaced refusals by level (see Lessons) | M1 near-miss misreads are a class the structure cannot tell apart from the real symbol (Lessons). The lead is a check on the data: an M1 read would be kept only when a second grid, with the frame nudged by a fraction of a module, reads the same codewords |
| Format gate before the searches | A frame starts the scale and perspective searches when its format copy reads within 3 bits. A gate of 1 bit cut a failing decode on 1 px noise from 108 to 18 ms (2026-09-23), but lost 215 sweep renders (a gate of 2 bits lost 57). The lost renders were anti-aliased and bilinear renders below 2.5 px/module and keystones past 6 %. Their copy reads 2-3 bits off at the unrefined frame, and the searches' refinement reads them. Using the timing modules as a second test for those frames was no better. Over frames that went on to read, the unrefined frame's timing was up to 71 % wrong, which overlaps noise end to end. The real images lose nothing either way, so the sweep decides this setting | - |
| Image search budgets | The shared scan's first eight candidates ([single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan)). At most 10,000 matrix decodes per candidate on the arbitrary-orientation path, not counting coverage re-reads. That leaves room for one complete frame (four axis assignments and four sizes) and keeps the result independent of CPU speed | A measured input class needs more |
| Too-small destination is terminal per finder candidate | The rule, the reason for it, what it cost before, and the skip of a candidate inside the symbol that read are in the [single-finder candidate scan](qrcode-symbologies.md#single-finder-candidate-scan). Micro QR's own part: a read that does not fit also ends the transposed read of the same grid. A grid that went on to read its transpose after such a read let one scale search run. That cost about 40 times a sized call on a process's first calls, and 70 times once warm (2026-10-01) | As in the shared record's residuals |
| Coverage re-read gate | A grid is read again by coverage only when the image has grey levels and the grid read its format word exactly. A real symbol's format modules sit next to the finder, where the frame is most accurate. Texture reads a format word within 3 bits about half the time, and an exact one about 1 time in 1,000 | - |

## Lessons learned

- The Micro QR terminator has the structure of a Numeric mode indicator followed by an all-zero count field: mode bits (v−1) + numeric count bits (v+2) = 2v+1 terminator bits. Decoding it as "a zero-count Numeric segment ends the stream" needs no special terminator scanning. It also handles terminators truncated at capacity at no extra cost.
- The ECC codeword counts include the misdecode-protection codewords p (ISO Table 9). A decoder wired directly to full Reed-Solomon strength would silently correct ⌊ecc/2⌋ errors where the standard allows only t (for example 2 instead of 1 for M2-L). The capacity cap is enforced after correction and has its own equivalence-class tests.
- Quiet-zone stripping cannot reuse Standard QR's dark-bounding-box trick. Micro QR has a single finder, so the right and bottom edges are data modules, with no guarantee that any of them is dark. Instead, the top-left dark module is the finder's corner, and a uniform border gives the core size.
- libzint (through the ZXingCpp wrapper) rejects UTF-8 Micro QR payloads outright ("Invalid UTF-8 in input"). A Latin-1 payload with diacritics came back from the round trip transliterated ("naïve café" became "naive cafe"). UTF-8 fixture coverage therefore comes only from the qrtool lineage. The sanity gate's payload comparison is what catches such silent drift before a fixture is committed.
- The perspective search reads what the scale search cannot from about 8 % keystone a side. The envelope was therefore widened to the measured range, instead of the search being dropped. Inside the range first stated (2 % a side for M1 and M2, 4 % for M3 and M4, top edge only), it read nothing the scale search missed, at 4 to 8 px/module drawn crisp and 2 to 8 with grey edges.

  Below 4 px/module drawn crisp, the two searches compete for the attempt budget. Without the perspective search, 17 renders were lost at 3 px/module, and at 2 px/module 85 were lost and 61 gained. At 8 px/module, over 19 symbols, 4 edges, 36 angles and both mirrorings (5,472 renders), the decoder reads all of them at 8 % a side and 5,392 at 10 %. Without the perspective search, it reads 5,131 and 2,522.

  The perspective search costs about a quarter of the grids a failing image decodes. Without it, failures ran 16 to 29 % faster where the attempt budget holds, and 4 to 17 % faster on noise, which exhausts the budget on every candidate (measured 2026-09-27).
- **A grid uses only the corrections its structure earns, and the evidence is summed, not refused by level** (2026-09-24). A failing image sends about 19,000 grids through Reed-Solomon. A random word passes a level with `e` corrections with probability `V(n, e) / 256^ecc`. In bits, that is 16 for M1, 20.8 for M3-M at its limit of 4, 24.7 to 26.5 for M4-M, M3-L and M2-M at theirs, 28.7 and 29.0 for M2-L and M4-L, and 37.6 for M4-Q. That was also the order in which texture was read.

  Before the rule, the decoder was run on 1,000,000 Standard QR and rMQR images decoded as Micro QR (1.8-4 px/module, turned or not, crisp or anti-aliased, evenly lit, under a ramp or a shadow, half negated). It read 1,088 as M1 and 22 at a level's limit, evenly lit ones included. With the regional pass, it also gave 25 verdicts at a limit. The regional pass at first refused, by level, what texture passed as: any M1 read, an empty read that needed correction, and M3-M and M4-M at their limit. Each refusal cost real reads, and texture then landed on the next level. Refusing by error count cannot tell a real symbol at its limit from a false one.

  `MicroQRGridEvidence` instead adds up these terms, in bits:

  - Reed-Solomon's strength at the read's correction count.
  - The format word's distance: the share of the words within 3 bits of a candidate that lie that close.
  - The timing modules' mismatches: their binomial tail at even odds.
  - When those fall short, the dark count on the quiet-zone ring next to the symbol: its tail at 0.3.

  It keeps a read whose sum reaches 33 bits. That bar is about 2^-19 an image at 2^14 grids, before the bitstream's own validity is checked.

  Each term was calibrated on those grids. The format distances fell where the combinatorics put them: exact words were 9.6e-4 measured against 9.8e-4 predicted, and words within 1 bit 1.57 % against 1.56 %. The timing modules matched no better than even odds. The quiet-zone line was light far more often than random, because the grid hung over a margin. The exception was beside a clean timing line, where about one module in three was dark. The finder and separator count as no evidence, because a Standard QR corner has a Micro QR's finder and separator. A symbol that shows its structure can use every correction, and M1 never reads on its format and timing alone.

  The foreign set went from 1,110 false results to 2. M1 under a 40 % shadow became readable: 3,265, 101 and 0 of 10,000 renders became 9,987, 9,996 and 9,998. The rule lost 0.6 % of M1 reads at 1-2.5 px/module, anti-aliased or resampled.

  The structure is counted after Reed-Solomon reads a grid, not before. M2-M4 already reach 33 bits at zero corrections, and M1's correction is cheaper than reading its quiet zone. When the check was first placed in front of Reed-Solomon, it made noise 8 % slower. The cost falls on symbols past the lighting bounds. A turned anti-aliased M1 under a 55 % shadow fails in 2.6 ms, where the refusal gave up at its first M1 grid in 0.5 ms.
- **The bar and the ring were measured, and texture and a near-miss grid are different adversaries.** At bars of 29, 31, 33 and 35 bits, low-density M1 losses and misreads were 77 and 25, 174 and 20, 274 and 16, and 516 and 11. Over the same bars, the predicted foreign false results fall from 7.7 to 0.3 a million. These were measured before the quiet zone moved to its final sampler. A bar of 33 is where both are small. A bar of 36, which excludes the one foreign verdict traced, lost 21 of 200,000 mixed renders where 33 lost 8, both measured with the second ring described below.

  The ring 1.5 modules out, clear of anti-aliased grey, cut low-density M1 losses from 256 to 63. But it let near-miss misreads rise from 17 to 21, and it brought back two real M1 misreads and an M4-M foreign verdict. A grid a little too small puts the first ring on the symbol's own edge, and only that ring sees it. With both rings: 34 losses, 28 misreads. Counting the worse timing line twice kept the verdict it was aimed at and doubled the losses.

  Structure beats texture, and only the ring next to the symbol sees a near-miss grid. A change that helps against one adversary can help the other, so each has its own measuring set.
- **Against the other readers, the designs explain the rows** (2026-09-24, zxing-cpp 0.5.2 with `TryHarder`, CodeGlyphX 2.1.0 on RGBA input):

  | Set | This library | zxing-cpp | CodeGlyphX |
  |---|---|---|---|
  | 1,000,000 foreign images, false reads | 1 | 274 | 2,306 |
  | 200,000 mixed renders (M1-M4, 1.5-6 px/module, any turn, lit or shaded, 15 % negated), read / misread | 194,520 / 3 | 166,280 / 18 | 148,116 / 853 |
  | 200,000 evenly lit at 1-2.5 px/module, read / misread | 169,606 / 17 | 54,397 / 64 | 62,817 / 1,214 |
  | M1 under a 40 % shadow: crisp along the axes, crisp turned, anti-aliased | 9,987, 9,996, 9,998 of 10,000 | 9,821, 9,936, 9,890 | 3,237, 100, 0 |

  zxing-cpp samples one grid a finder (the best of four orientations by format distance) and checks two timing modules. It rejects only a quiet zone more than two-thirds dark, and it corrects past the misdecode-protection cap. Sampling few grids keeps its false reads low and costs it reads. CodeGlyphX checks finder, separator and timing modules against fixed budgets, whatever the correction count, and a Standard QR corner passes those checks. Neither refuses by error count.
- **M1 near-miss misreads are left, and structure cannot find them.** After the grey-level frame, 8 remain in about 395,000 renders: 3 in the evenly lit set at 1-2.5 px/module and 5 in the mixed set. Three of the mixed-set misreads are at 1.63, 1.66 and 4.29 px/module under 19-20 % shadows. Each is a grid near the real symbol whose finder, timing, format and quiet zone are right, but whose data is mis-sampled in 3 or more codewords. M1's 16 bits of detection pass such a grid one time in 65,536, across the thousands of grids a hard render gets.

  The refined finder centre is not biased on them (0.04-0.08 module from the true centre). A different starting point changes which grids the search tries, and so which such grid gets through. The lead is the check on the data in Decisions. Re-sampling nudged grids from the kept read's corners measured nothing, because the corner transform is not faithful at 1-2 px/module.

  The two foreign results left are both rMQR images read at M3-L's limit. The one traced reached 35.0 bits, with its timing 1 of 14 off and its format 3 bits off. Two foreign results are the rate the bar is set for, and two images do not make a cause.

  Two misreads were seen before the rule and have not been checked since. A Micro QR symbol turned 30° holding the unmapped Kanji cell 0x8794 read as "95549". An M4-L holding an unmapped cell, anti-aliased at 1.60 px/module, read as M1 "0824".
- **Each grid is sampled once and read in both orientations** (2026-09-24). A failing decode on 512 × 512 noise-like images spent 90 % of its time in the scale and perspective searches. Sampling took 32 %, codeword extraction 27 %, Reed-Solomon 13 %, the format read 9 % and the transposed copy for the mirrored read 5 %.

  The mirrored read now goes through a view that transposes the module index. Codeword extraction goes through a placement table per size, built from the encoder's own predicates. The affine sampler has a 128-bit tier. The format word is looked up in a 32 KB table of the words within 3 bits of each candidate. The groups of words around different candidates never overlap, so one lookup is the whole search. Each entry also carries the distance.

  Those parts went from 132 to 46 ms a failing decode (2.9x), for 1.5-1.7x end to end. Skipping sweep candidates the strided scan already decoded ([image decode passes](qrcode-symbologies.md#image-decode-passes)) took the end-to-end gain to 1.9x on 8-bit noise. That was more than expected, because on texture the sweep's top eight are largely the strided scan's. Every result is unchanged.

  Refuted:

  - Reading modules on demand through the transform, format first (1.2x). Both orientations read about 190 data modules each, more than one whole-grid sample's 289, and each read paid a sample's float work.
  - Remembering grids already decoded. On 8-bit noise, 523 of 197,432 grids repeated the one before and 993 an earlier one. On a Standard QR image, 19 % did.
  - Stopping Reed-Solomon once the error locator's degree passes the capacity. The result is not identical: a block the cap rejects reports its corrected-error count, which only the full correction computes.

  A module read on demand is cheaper only when fewer modules are read.
- **The refined centre and the coverage re-read belong in every search.** Keeping the coverage re-read out of the scale and perspective searches lost 34 bilinear and 17 anti-aliased reads at 1.25-2 px/module. Without the refined finder centre in the arbitrary-orientation search, bilinear upscales at 1.5-2 read 761 of 800 instead of 798. Each orientation frame refines its own copy of the centre along its own axes, starting from the centre already refined. Starting from the raw candidate read 6 renders fewer at 1.25-1.5. The refined centre costs time on another symbology's image: the searches on rMQR's identical finder go deeper, and an rMQR image read as Micro QR went from 0.75 to 1.7 ms.
