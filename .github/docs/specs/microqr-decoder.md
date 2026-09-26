# Micro QR Decoder

Design record for the Micro QR (ISO/IEC 18004, M1 to M4) decode feature (`MicroQRCodeDecoder`): what it does, why it is scoped the way it is, and what was learned. Implementation locations are indexed in the [spec-to-code map](microqr-spec-map.md); what the three symbologies share is in [QR Symbology Architecture](qrcode-symbologies.md).

---

## What

`MicroQRCodeDecoder` decodes Micro QR symbols back into text.

### Matrix level

Input: `MicroQRCodeData`, or a byte-per-module span with its size, with or without a light quiet zone.

Behavior: the core is located, the version follows from its size, then the format information, codeword extraction and unmasking, Reed-Solomon correction capped at the ISO capacity, and the bitstream. Diagnostics are in `MicroQRCodeDecodeInfo` (status, version, ECC level, mask, corrected codewords). The stages are drawn in the [spec-to-code map](microqr-spec-map.md#decoding-pipeline-overview-matrix-level).

### Image level

Input: an `SKBitmap`, or a grayscale luminance span with its width and height (`TryDecodeImage`), with an allocation-free span-destination variant.

Behavior: in each of the [shared image decode passes](qrcode-symbologies.md#image-decode-passes), the ranked finder candidates are tried one by one. For each, the module sizes and centre are measured, then:

- the axis-aligned path, in each right-angle orientation: a grid for each size from M4 down to M1, then the grid fitted to the timing patterns, then, at low density, a table of module boundaries read off them;
- the arbitrary-orientation path: the finder's axes from an angular sweep, a grid for each size in each frame and orientation, and, once a grid gets past its format information, a scale search and a perspective search around it.

Every grid goes through the matrix level, keeping only the corrections its structure earns, and is read again transposed unless it decoded. On an image with grey levels, a size's grid or a search's grid whose format word reads exactly is also read again by coverage. The stages and their order are drawn in the [spec-to-code map](microqr-spec-map.md#image-detection-and-sampling).

### Image decode figures

These figures show how the image decoder reads each kind of input. [decode_figures.cs](../../../tools/decode_figures.cs) draws them from the library's own symbols. It also renders every input and decodes it through the public API, so a figure cannot show a read that does not happen. Rerun it when the pipeline changes.

#### Stage by stage

A clean M3 through the main path.

![Stage by stage: luminance, global threshold, finder candidates, module sizes and centre, sizes M4 to M1, matrix decode](../images/microqr/decode-overview.svg)

- **Luminance:** a grey image comes in.
- **Global threshold:** one Otsu split of the histogram separates ink from paper.
- **Finder candidates:** every pattern that reads 1:1:3:1:1 along a line through its centre and passes the cross-checks. The most confirmed are tried first.
- **Module sizes and centre:** a module size along the row and one along the column, each from a dark-light-dark run through the centre that spans six modules, from the ring's inner edge on one side to its outer edge on the other.
- **Sizes M4 to M1:** a grid anchored on the finder centre is sampled for each size, largest first, and the first that decodes ends the search.
- **Matrix decode:** the format information names the version, which must match the grid's size; then unmasking, Reed-Solomon and the text.

#### Reading the input figures

Each figure shows the input, with what the decoder finds drawn over it, beside the path through the [image-level stages](microqr-spec-map.md#image-detection-and-sampling). Every binarization pass runs this path: the global threshold, the inverted image, the regional pass, then the midpoint sweep (see [image decode passes](qrcode-symbologies.md#image-decode-passes)). The matrix decode at the bottom keeps only the corrections a grid's structure earns, and reads the grid again transposed unless it decoded.

Green and grey boxes run for this input; green marks the stages it depends on.

- **Key** (green): without this stage, the input would not read, or would read only after more grids fail than with it. A grid is one sampling of the symbol: reading it again transposed or by coverage is the same grid.
- **Runs** (grey): the stage runs as it does for any input.
- **Only if needed** (dashed): the stage runs only when the grids before it fail.
- **Skipped** (faded): the stage does not run for this input.

Numbers on the boxes match the numbered notes under each figure.

#### Clean

An upright, crisp M3.

![Clean M3 and its path through the pipeline](../images/microqr/decode-input-clean.svg)

The M4 grid is tried first and fails at its format information in both orientations; the M3 grid then reads. With only two grey levels, the stages that need grey levels stay off, and at this density the module-boundary table is not used.

#### Rotated or mirrored

Any angle, either way round.

![Rotated and mirrored M4 and its path through the pipeline](../images/microqr/decode-input-rotated.svg)

1. **Arbitrary orientation:** the axis-aligned grids follow the image's rows and columns, which a turned symbol does not, so they fail. A ray through the centre of a square finder crosses the shortest dark-light-dark span along one of the finder's own axes, so an angular sweep finds them, and a grid is sampled along each frame it gives.
2. **Matrix decode:** a mirrored capture has the same finder but a transposed grid, so a grid that does not decode is read again transposed.

Here the sweep's first frame lies along the symbol's axes, and its first grid reads transposed.

#### Keystone distortion

A flat M4 tilted away at the bottom. Its far edge is inset as much as the measured envelope's, which insets the edge on the finder's side instead.

![Keystoned M4 and its path through the pipeline](../images/microqr/decode-input-keystone.svg)

1. **Coverage re-read:** a grid whose format word reads exactly is read again, with each module's luminance interpolated at its centre and split halfway between the two levels. Here that is how the scale search's grid reads; without it, the next grid of the search reads instead.
2. **Arbitrary orientation:** the axis-aligned grids fail. The first frame of the sweep gives a grid that gets past its format information and then fails, which starts the searches around it.
3. **Scale search:** the grid again with the centre moved slightly and each axis's module size changed by a few percent. Here the first of them, a slightly smaller grid, reads.
4. **Perspective search:** the grid through a projective transform, over a small range of the two coefficients a single finder leaves unknown. Here the scale search reads first, so it does not run; without the scale search, it reads the symbol instead.
5. **Matrix decode:** Reed-Solomon corrects the modules the grid still gets wrong; without correction, a later grid reads.

A single finder cannot measure perspective, so every grid starts from the finder's axes and module sizes. They are right at the finder and drift toward the far edge (dashed).

#### Snapped scale

A crisp M4 at a non-integer scale, each module snapped to whole pixels.

![M4 at a snapped scale and its path through the pipeline](../images/microqr/decode-input-snapped.svg)

1. **Timing frame:** the finder measures a whole number of pixels per module, a few percent more than the symbol's own pitch, and every grid scaled from it drifts across the symbol and fails. The timing patterns along row 0 and column 0 reach the far edge, their dark runs give the size, and a line fitted to all their module boundaries gives the pitch and the corner. That grid reads.

The M4, M3 and M2 grids fail at their format information, and the M1 grid fails in Reed-Solomon.

#### Low density

A crisp M4 at barely more than one pixel per module.

![M4 at low density and its path through the pipeline](../images/microqr/decode-input-low-density.svg)

1. **Low density:** each module is one or two whole pixels, which no fitted grid follows. The module boundaries are read off the timing patterns along row 0 and column 0, and each module is sampled between its own boundaries. Here one module on each timing line is two pixels wide (marked).

The grids scaled from the finder fail first, and no timing frame is fitted.

#### Grey edges

An anti-aliased M4 at a little under two pixels per module.

![Anti-aliased M4 and its path through the pipeline](../images/microqr/decode-input-grey-edges.svg)

1. **Coverage re-read:** the first grid fails, but its format word reads exactly, so the grid is read again with each module's luminance interpolated at its centre and split halfway between the two levels. That read succeeds.
2. **Matrix decode:** the coverage read still has a few modules wrong, and Reed-Solomon corrects them; without correction, a later grid reads.

Grey levels also turn on the finder centroid, which here leaves the centre where it was. The midpoint sweep runs only for a polarity whose global threshold found no finder; here the global threshold finds it.

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
- translation and quiet-zone variants;
- mild optical degradation: JPEG artifacts, low contrast, additive noise;
- keystone as measured: a symmetric top-edge inset of 2 % of the symbol width for M1 and M2, and 4 % for M3 and M4, with representative combinations of keystone and rotation, and of keystone and mirroring.

### Not supported

- Strong perspective: one finder cannot provide the independent correspondences that Standard QR's three finders and alignment patterns give.

## Why

- **An explicitly typed decoder.** `QRCodeDecoder` stays Standard QR only, and Micro QR scanning is its own entry point, so default Standard QR scanning performance is unaffected.
- **Every size is tried rather than read first.** Micro QR has four sizes, and the format word must name the size its grid was sampled at, so a grid of the wrong size almost always fails there. Larger sizes go first: a real M4 sampled as M2 reads a garbled sub-grid, while trying the real sizes first exits at the first success.
- **The axes come from the finder alone.** With a single finder, orientation cannot come from finder geometry as it does with Standard QR's three. The finder's local axes are recovered from dark-light-dark runs in an angular sweep, and the two projective coefficients a single finder leaves unknown are searched. That supports arbitrary rotation and mild perspective, and leaves strong perspective out of scope.
- **The searches start only after a format word.** The scale and perspective searches multiply one grid into hundreds, so they run only once a grid's format information decodes in either orientation; wrong grids overwhelmingly fail before that.

## Decisions

| Decision | Choice | Revisit when |
|---|---|---|
| Correction capacity | Reed-Solomon corrections are capped at ISO Table 9's t, since the misdecode-protection codewords p make full Reed-Solomon strength wrong (M2-L: Reed-Solomon could correct 2, the standard allows 1). The cap is enforced after correction | - |
| Grid evidence | One rule in every pass: a sampled grid keeps only the corrections its structure earns. Reed-Solomon's strength at the correction count, the format word's distance, the timing modules and, when those fall short, the quiet-zone modules along the right and bottom edges are summed in bits, and a grid below the bar fails as a correction failure does. M1, which corrects nothing, is never kept on its format word and timing alone. It replaced refusals by level | A misread class that a grid's structure cannot tell from the real symbol |
| Image search budgets | At most eight finder candidates per scan, most confirmed first; at most 10,000 grid decodes per candidate on the arbitrary-orientation path, which leaves room for one complete frame (four axis assignments and four sizes) and keeps the result independent of CPU speed | A measured input class needs more |
| Coverage re-read gate | A grid is read again by coverage only when the image has grey levels and the grid read its format word exactly. A real symbol's format modules sit next to the finder, where the frame is best; texture reads a word within 3 bits about half the time and an exact one about 1 in 1,000 | - |

## Lessons learned

- The Micro QR terminator is structurally a Numeric mode indicator followed by an all-zero count field (mode bits (v−1) + numeric count bits (v+2) = 2v+1 terminator bits). Decoding it as "a zero-count Numeric segment ends the stream" needs no special terminator scanning and handles terminators truncated at capacity for free.
- The ECC codeword counts include the misdecode-protection codewords p (ISO Table 9): a decoder wired directly to full Reed-Solomon strength would silently correct ⌊ecc/2⌋ errors where the standard allows only t (for example 2 against 1 for M2-L). The capacity cap is enforced after correction and has its own equivalence-class tests.
- Quiet-zone stripping cannot reuse Standard QR's dark-bounding-box trick: Micro QR has a single finder, so the right and bottom edges are data modules with no darkness guarantee. The top-left dark module is the finder's corner, and a uniform border gives the core size.
- libzint (through the ZXingCpp wrapper) rejects UTF-8 Micro QR payloads outright ("Invalid UTF-8 in input"), and a Latin-1 payload with diacritics came back from the round trip transliterated ("naïve café" became "naive cafe"). UTF-8 fixture coverage therefore comes from the qrtool lineage only, and the sanity gate's payload comparison is what catches such silent drift before a fixture is committed.
