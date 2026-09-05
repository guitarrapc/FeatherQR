# Standard QR Encoder

Design record for the Standard QR encode feature (`QRCodeGenerator`): what it does, why the pipeline is structured this way, and what was learned while making the implementation spec-compatible and fast. Normative details and implementation locations are indexed in the [spec-to-code map](standardqr-spec-map.md). The inverse pipeline is documented in [Standard QR Decoder](standardqr-decoder.md).

---

## What

`QRCodeGenerator` converts text into a Standard QR module matrix through the complete ISO/IEC 18004 encoding pipeline:

```
Text
  -> mode / ECI analysis
  -> version selection
  -> data bit stream and padding
  -> Reed-Solomon ECC per block
  -> data / ECC interleaving
  -> function-pattern and data placement
  -> best-of-8 mask selection
  -> format and version information
  -> QRCodeData or byte-per-module matrix
```

### Public entry points

The encoder exposes two output models.

#### `QRCodeData`

`Create(ReadOnlySpan<char>, ...)` returns a `QRCodeData` object (a `string` converts implicitly).

- The core matrix is stored bit-packed, one bit per module.
- The quiet zone is virtual: it changes the public coordinate space but consumes no payload storage.
- The matrix is deterministic for the same text and options.
- This is the convenient object model used by renderers and serialization.

#### Caller-provided matrix buffer

`Create(ReadOnlySpan<char>, ..., Span<byte> destination, ...)` writes:

- one byte per module;
- `0` for light and `1` for dark;
- flat row-major order;
- quiet zone included.

`TryGetRequiredBufferSize` returns the required matrix side, byte count, and selected version, and returns `false` when the content exceeds the capacity of every version in range; argument errors (a negative or overflowing quiet zone) still throw, so `false` means "does not fit" and nothing else. It is the only sizing method on the current surface: the released `GetRequiredBufferSize` is `[Obsolete]` and goes in 2.0.0. The rationale for that split, and why it is a `Try` rather than a dedicated exception type, is recorded once in [rmqr-encoder.md](rmqr-encoder.md) — Standard QR follows the same rule so the three symbologies present one surface. The encoder overwrites every byte of the written region (the core comes from a per-version template copy; only a non-zero quiet zone is cleared), accepts a dirty pooled destination, and leaves any tail beyond the returned byte count untouched. After JIT and pool warm-up, the span path is allocation-free in Release builds.

### Supported

| Area | Coverage |
|---|---|
| Symbology | Standard QR |
| Versions | 1–40 |
| ECC levels | L, M, Q, H |
| Data modes | Numeric, Alphanumeric, Byte |
| ECI | Default/no header, ISO-8859-1 (assignment 3), UTF-8 (assignment 26) |
| UTF-8 BOM | Optional in UTF-8 Byte mode |
| Version selection | Automatic minimum-fit, caller-requested version, or a version range (options overloads) |
| ECC boost | Optional (options overloads): the requested level becomes the minimum and is raised as far as the chosen version's capacity allows, never changing the version |
| Segmentation | One segment in one mode by default; opt-in mixed-mode segmentation (`QRSegmentation.Optimal`) splits the content into the minimal-bit Numeric / Alphanumeric / Byte runs |
| Quiet zone | Configurable non-negative size; span sizing/output rejects dimensions that cannot fit an `int`-sized matrix |
| Output | Bit-packed `QRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- Kanji mode
- FNC1
- Structured Append
- Arbitrary ECI assignment numbers
- Arbitrary binary payload input
- Micro QR and rMQR

By default the encoder analyzes the complete input once and emits one data segment; `QRSegmentation.Optimal` (options overloads) opts into the globally minimal mixed-mode split instead (see [Mixed-mode segmentation](#mixed-mode-segmentation-options-overloads)).

---

## Pipeline

### 1. Validate the matrix request

All generation overloads reject:

- requested versions outside `1..40` (except `-1`, meaning automatic);
- negative quiet-zone sizes.

`TryGetRequiredBufferSize` and the span-output overload additionally reject quiet zones whose resulting side or squared byte count exceeds `int.MaxValue`. The span overload also rejects caller-provided buffers smaller than the calculated matrix. These paths compute dimensions with `long` arithmetic before narrowing to `int`, preventing overflow in `coreSize + 2 * quietZoneSize` and `totalSize * totalSize`.

### 2. Analyze text and choose mode / ECI

`TextAnalyzer` performs a single pass over the UTF-16 input and classifies the entire payload:

1. Numeric when every character is `0..9`.
2. Alphanumeric when every character belongs to the 45-character QR alphabet.
3. Byte otherwise.

Empty input is deliberately represented as a zero-length Byte segment. The standard does not define a special empty-data mode, and Byte mode gives the least surprising representation.

With `EciMode.Default`, Byte-mode character encoding is selected as follows:

| Input | Effective ECI | Header |
|---|---|---|
| ASCII only | Default | none |
| Contains non-ASCII, but every character is in U+0000..U+00FF | ISO-8859-1 | ECI 3 |
| Any character above U+00FF | UTF-8 | ECI 26 |

Numeric and Alphanumeric payloads do not need character-set conversion, although an explicitly requested non-default ECI is still emitted before the data-mode indicator.

Explicit ECI is a caller constraint. In particular, forcing `Iso8859_1` is only semantically correct for text in U+0000..U+00FF; `Default` avoids an incompatible choice by upgrading such input to UTF-8.

On supported x86/x64 runtimes, analysis uses AVX2 or SSE2 for character-class checks; on ARM64 it uses a NEON tier (16 chars per step with 8-wide vector remainder blocks). A scalar path covers short inputs and other targets.

### 3. Select the version

Automatic selection scans versions 1 through 40 and picks the first whose data-codeword capacity can hold:

```
optional ECI header
+ 4-bit mode indicator
+ version-dependent character-count indicator
+ encoded payload bits
```

The character-count width changes at versions 10 and 27:

| Version range | Numeric | Alphanumeric | Byte |
|---|---:|---:|---:|
| 1–9 | 10 | 9 | 8 |
| 10–26 | 12 | 11 | 16 |
| 27–40 | 14 | 13 | 16 |

Byte-mode capacity is calculated from encoded byte count, not UTF-16 `char` count. UTF-8 BOM contributes three bytes to both capacity selection and the Byte-mode character-count indicator.

The version calculation does not reserve four mandatory terminator bits: the terminator is allowed to shrink to the remaining capacity, including zero bits for an exact fit. If no version can hold the required header and payload bits, generation fails instead of truncating.

When `requestedVersion` is supplied, automatic selection is bypassed. It is intended for callers that need a fixed symbol size and already know the payload fits.

#### Version ranges (options overloads)

`QRCodeGeneratorOptions.Version` is a `QRVersionRange` rather than a single version, and the scan runs over `[Min, Max]` instead of 1 to 40. A pinned version is the degenerate `Exactly(n)` case, so there is one concept rather than a requested version and a range that could contradict each other. The range's bounds are validated when it is constructed, before any generator is called, and both are **inclusive** — which is why this is a domain type and not C#'s `..`, whose end is exclusive and would make `1..40` mean 1 through 39.

Two behaviours differ from the `requestedVersion` parameter, and both are confined to the options overloads:

- **A range narrower than 1-40 is checked for fit.** `Exactly(n)` reports content that does not fit version *n* as `false` from `TryGetRequiredBufferSize` (or an `ArgumentException` from `Create`), where the parameter hands the version straight to the encoder and fails deep inside with `ArgumentOutOfRangeException (Parameter 'length')` from a span slice. The parameter's behaviour is unchanged; only the new surface checks.
- **Sizing honours the version.** The released `GetRequiredBufferSize` has no `requestedVersion` parameter, so an ignored `Version` would have been a silent trap. `TryGetRequiredBufferSize` reports the version the range resolves to, matching what Micro QR and rMQR already do.

`QRVersionRange.Any` short-circuits to the same automatic path the parameter list overloads take, so the default costs nothing extra; only a constrained range pays for the additional text analysis its resolution needs.

**The scan does not assume the fit predicate is monotone in the version**, even though it is. It could plausibly not be: the character-count indicator widens at versions 10 and 27, so a larger version costs more header bits. Scanning `[Min, Max]` is correct either way, and `VersionRangeTest.StandardQr_FitsIsMonotoneInVersion` sweeps 3 modes × 4 ECC levels × 3 ECI modes × 58 lengths × 40 versions to keep the monotonicity a checked fact rather than an assumption the code rests on.

#### ECC boost (options overloads)

`QRCodeGeneratorOptions.BoostEccLevel` reinterprets the requested ECC level as a minimum: the version is chosen for that level exactly as above, then the level is raised while the next one still fits the chosen version. Because the version is fixed before the boost starts, boosting **never grows the symbol** — it converts padding the symbol would carry anyway into error-correction capacity. The main audience is symbols with an icon overlay, where the spare capacity absorbs the covered modules.

- **Off by default.** A raised level rewrites the format information and can change the winning mask, so a default of on would silently change every existing symbol; existing tests, golden pixels and playground permalinks all assume a requested level is the emitted level.
- **Sizing ignores the flag.** The buffer size depends only on the version and the quiet zone, and the boost cannot change the version, so `TryGetRequiredBufferSize` reports the same answer either way. This is documented on the API rather than left implicit.
- **The error contract is unchanged.** Content that fits no version in the range fails with the same exception, message included, as the boost-free path — the unconstrained overflow stays `InvalidOperationException` (the released contract), a constrained range stays `ArgumentException`. Turning boost on must not reclassify an error.
- **Standard QR only.** Micro QR ties its legal levels to the version (M1 has none, only M4 offers Q), so a boost there would interact with version selection instead of following it; rMQR has a single M→H step. Either can adopt the same contract later.

`EccBoostTest` pins the headroom classes (boost to H, stop at an intermediate level, no headroom, already at H), the version invariance, the sizing indifference and the error parity.

#### Mixed-mode segmentation (options overloads)

**What.** `QRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a candidate version, and fits the version against that cost instead of the single-mode cost. `QRSegmentation.Single` (the default) keeps one run in one mode.

**Why.** Mixed payloads pay the whole-content mode for every character under a single segment: a URL prefix followed by a long numeric identifier is all Byte, so the digits cost 8 bits each instead of 3⅓. Splitting the digits off routinely drops the symbol a version or more (`https://example.com/item?id=` + 30 digits: version 4-M as one Byte run, version 3-M split).

**Why opt-in, and why the ceiling.** Changing the default would move the emitted bit stream, and therefore the rendered symbol, for existing callers. When the content fits in a single mode, that fit caps the scan from above: only strictly smaller versions are tried, so a plan is emitted only when it lowers the version, and the single-mode stream is emitted byte for byte in every other case. The end-to-end tests assert both properties for every corpus entry.

**Why the scan needs almost no bounding machinery.** The character-count indicator widths are constant within the three version bands (1–9 / 10–26 / 27–40), so the optimal cost is itself constant within a band: the scan computes it at most once per band — three O(n) cost runs in the worst case, no reconstruction table — and compares it against each candidate capacity. rMQR needed a trivial bound, a floor and a re-priced ceiling because its 32 versions carry 13 distinct width triples across a strategy-ordered ranking; a totally ordered version set with banded widths makes the floor and the ceiling unnecessary, which is a lesson worth keeping next to the rMQR one rather than porting the bounds by reflex. The trivial bound alone did carry over — one O(n) pass pricing each character at the cheapest rate any mode could give it — because without it, content no split can shrink still paid for a band cost run: measured on 120 single-mode characters, the Optimal arm went from 1.8x the Single encode to roughly parity, while the winning shapes were untouched. Its blind spot is the same as rMQR's: finely alternating content clears the bound and pays for planning that then gains nothing, because seeing that switching modes every character never pays *is* the dynamic program.

**When no single mode fits.** The ceiling does not exist, so the scan runs to the window's end. This is the one place `Optimal` accepts input `Single` rejects: 1,000 lowercase letters followed by 4,500 digits is 5,500 Byte-mode characters, far over the 2,953 version 40-L holds, but well inside its 23,648 bits once the digits split off. Only when a mixed plan fails as well does the path throw, with the single-mode path's exact exception type per constraint shape, so turning segmentation on cannot reclassify an error.

**Content that cannot benefit.** All-Numeric content skips planning: splitting a Numeric run never lowers its payload and every extra run adds a header, so one run is provably the optimum.

**How the optimum is exact.** A run does not cost a constant per character (Numeric packs 3 digits into 10 bits, Alphanumeric 2 characters into 11), so the dynamic program carries the packing-group remainder in its state rather than rounding a per-character average. The state layout and transitions are in `ModeSegmenter`, shared with the rMQR and Micro QR planners — the symbologies differ only in header widths, taken as parameters, so one implementation keeps the cost models (the UTF-8 surrogate rules included) from drifting apart. Micro QR additionally passes per-version mode availability (M1 is Numeric-only, M2 has no Byte mode), which disables the missing transitions. `QRSegmentPlannerUnitTest` holds the program to an independent exhaustive mode-assignment optimum on short content across the bands and charsets, and the rMQR suite holds the same code to its own independent oracle across all 32 versions.

**Bounds.** Content longer than the largest character count any version holds in any mode (7,089, Numeric at 40-L, an exact fit) is rejected before any cost run; the margin is 4 bits, and the derivation sits with the constant in `QRSegmentPlanner` so a capacity-table change re-derives rather than nudges it. The plan buffer is stack-allocated for content up to 64 characters and pooled at text length above that — a plan cannot hold more runs than the content has characters, so the pooled path can never fail for space. The reconstructed plan is re-costed from the byte counts the encoder will actually emit and rejected on disagreement, because the bit-stream writers store without per-flush bounds checks.

**Composition with the other options.** The BOM is a stream-level prefix, and a split would relocate it into the middle of the decoded text, so `Utf8Bom` falls back to the single-mode stream exactly when a BOM would actually be written — a UTF-8 Byte-mode stream. Content whose single mode is Numeric or Alphanumeric never carries a BOM (even under an explicitly requested UTF-8 charset) and still splits; the first cut of the gate suppressed those too, and code review caught it costing a full version for nothing. A version range narrows the scan window; a pinned version that only a mixed plan fits succeeds where `Single` throws. ECC boost runs after the plan is fixed and compares the exact planned stream bits against the higher level's capacity, keeping the version-invariance contract. A pinned mask applies at the matrix stage, orthogonally. Argument validation keeps the quiet-zone-first precedence of the other surfaces, and an undefined segmentation value reports the same `segmentation` parameter name on the generators and the builder.

**Plans the byte-segment decoder would misread are rejected.** The shared byte-segment decoder consumes a leading EF BB BF of every segment without an explicit ISO-8859-1 declaration — even behind an explicit UTF-8 ECI — so a split that relocates a mid-content U+FEFF to a Byte-run start would decode with the character silently dropped, where the single-mode stream keeps it interior and intact. All three planners reject such plans (`ModeSegmenter.HasBomRelocatedToARunStart`) and fall back to the single-mode stream; a run at offset 0 is exempt because the single-mode stream starts with the same bytes and loses it identically. Micro QR, which has no ECI to pin a charset, additionally rejects plans containing a non-ASCII Latin-1 Byte run whose narrowed bytes the decoder's unspecified-charset resolution would read as UTF-8 (`SegmentDecoders.ResolvesToUtf8WhenUnspecified`, kept beside the resolution it mirrors): isolating such a run from its disambiguating invalid neighbours would decode it as different text. Lesson: a mixed-mode plan is only as good as the decode it produces — cost optimality had to be constrained by the decoder's per-segment charset heuristics, which adversarial review caught by reproduction, not inspection. The consequence is accepted deliberately, with a known limitation: only the minimal-bit plan is checked, so content whose optimum is misread-prone reports "does not fit" when no single mode holds it, even though a slightly costlier safe split can exist (adversarial review demonstrated one: 3,000 digits + U+FEFF + 10 letters has a safe fitting plan the fallback never searches). Teaching the dynamic program itself to avoid opening a Byte run on a mid-content U+FEFF would make the cheapest safe plan the optimum and remove the gap; it is deferred until the input class matters, because it changes the DP's transition structure and every optimality oracle with it.

**ECI.** One prefix ahead of the first run: a decoder carries the declared charset across the runs that follow, so a plan needs no repetition. Its 12 bits are part of the cost the version scan compares.

**Kanji.** Still not encoded, so a Japanese payload mixes Byte (UTF-8) with Numeric runs rather than reaching for 13-bit Kanji. The decoder reads Kanji segments other encoders produce.

### 4. Build the data codewords

`QRBinaryEncoder` writes MSB-first through `BitWriter`:

1. Optional ECI indicator `0111` and 8-bit assignment number.
2. Data mode indicator.
3. Character-count indicator.
4. Mode-specific payload.
5. Up to four zero terminator bits.
6. Zero bits to the next byte boundary.
7. Alternating pad codewords `0xEC`, `0x11` until the data capacity is full.

Mode-specific packing is:

| Mode | Packing |
|---|---|
| Numeric | 3 digits → 10 bits; final 2 → 7 bits; final 1 → 4 bits |
| Alphanumeric | 2 values → `first * 45 + second` in 11 bits; final 1 → 6 bits |
| Byte | 8 bits per encoded byte |

Byte mode uses ISO-8859-1 narrowing or UTF-8 encoding. Temporary charset buffers use `stackalloc` up to 256 bytes and `ArrayPool<byte>` above that threshold. `BitWriter` stages bits in a 64-bit accumulator and bulk-writes big-endian words; Byte-mode data is copied eight bytes at a time where possible.

### 5. Generate Reed-Solomon error correction

The selected version and ECC level identify:

- total data codewords;
- ECC codewords per block;
- Group 1 and Group 2 block counts;
- data codewords per block in each group.

Data codewords are partitioned by that table, and `EccBinaryEncoder` calculates the Reed-Solomon remainder independently for each block over GF(256), using primitive polynomial `0x11D` and generator roots `alpha^0..alpha^(n-1)`.

The public dispatch selects the fastest available kernel while preserving byte-identical results:

- GFNI / SSSE3 on supported x86/x64 targets;
- AdvSimd on ARM64;
- cached log-domain scalar implementation elsewhere.

Every optimized kernel is parity-tested against a deliberately naive polynomial-division reference.

### 6. Interleave the final message

`BinaryInterleaver` emits:

1. data codeword 0 from every block, then data codeword 1 from every block, and so on;
2. the extra final data row from the longer Group 2 blocks, when present;
3. ECC codeword 0 from every block, then ECC codeword 1, and so on;
4. zero remainder bits for the selected version.

The implementation writes the output sequentially and accepts strided source reads. A one-block symbol takes an identity fast path. Remainder-bit storage is cleared explicitly so uninitialized stack or pooled memory cannot affect the matrix.

### 7. Place function patterns and data

The encoder places or reserves (the byte-per-module core is fully written by the placer; no zeroing is required):

- three 7×7 finder patterns;
- one-module separators;
- alignment patterns for version 2+;
- horizontal and vertical timing patterns;
- the fixed dark module;
- both format-information areas;
- both version-information areas for version 7+.

Reserved modules are represented by a compact bit mask. Both the painted function modules and the bit mask are built once per version by the reference `PlaceFunctionModulesReference` painters and cached (`ModulePlacer.PlacementLayout`); the encoder copies them per symbol and the decoder reads the same cached mask when it needs to distinguish function modules from data modules, keeping both directions structurally identical.

Interleaved bits are then consumed MSB-first in the standard two-column zigzag from bottom-right to top-left, skipping column 6 and every reserved module. The reference walk keeps up to 64 pending stream bits in a register and handles both modules of a strip row together; the production placement uses the cached walk (core index per stream bit, rows where both strip modules are free as runs): the stream is expanded to one byte per bit and each run row is a single 16-bit store, everything else an index-table scatter (parity-tested against the reference walk for every version).

### 8. Evaluate all eight masks

The encoder tests every Standard QR mask pattern and chooses the lowest ISO/IEC 18004 penalty score:

1. long same-color runs;
2. 2×2 same-color blocks;
3. finder-like `1:1:3:1:1` patterns with the required light margin;
4. deviation from 50% dark modules.

Each candidate is scored as the final symbol will appear:

- the mask is applied only to data modules;
- candidate-specific format bits are inserted before scoring;
- version bits are included for version 7+.

This matters because format modules participate in the visual penalty rules and can change which mask wins. Ties are deterministic: the lower mask index wins because candidates are visited in order and only a strictly lower score replaces the current best.

Masking and scoring operate on packed rows rather than byte-per-module loops:

- versions 1–11 fit each row in one `ulong`;
- versions 12–40 use a fixed 192-bit row made from three `ulong` values.

The eight formulas are precomputed as 12-row periodic templates. XOR, shifts, and popcount implement both masking and all four penalty rules without changing the reference result. On AVX2 the versions 1-11 tier scores four candidates per vector (lane = pattern) with per-version tables of pre-masked templates and format-bit overlays; the larger tiers score four rows per vector. Parity tests compare every representation against straightforward textbook formulas.

**Pinned mask.** `QRCodeGeneratorOptions.MaskPattern` (0-7, `null` = automatic) skips the evaluation entirely and applies that one pattern via a scalar per-module loop. Any pattern is a legal symbol — the specification only recommends the best scorer — so pinning exists for byte-exact reproduction of symbols produced elsewhere (the decoder reports the pattern in `QRCodeDecodeInfo.MaskPattern`) and for exercising decoders against all eight patterns. Invalid values are rejected when the option is set, like `Version`. Micro QR offers the same option over its four patterns (`MicroQRCodeGeneratorOptions.MaskPattern`, an unrelated numbering — see the Micro QR spec map); rMQR has a single fixed mask, so it has no such option.


### 9. Write format / version information and expose output

After the winning mask is applied:

- BCH(15,5) format information encodes ECC level and mask index, applies the standard format mask, and is written twice;
- BCH(18,6) version information is written twice for versions 7–40.

For `QRCodeData`, the temporary core matrix is packed into the object's one-bit-per-module payload; the quiet zone remains virtual.

For span output:

- with quiet zone 0, the core pipeline writes directly into the destination;
- with a quiet zone, the contiguous core is built in a pooled temporary and copied row-by-row into the centered destination.

The encoder produces a module matrix, not an image. Color, pixels-per-module, shapes, gradients, icons, PNG/SVG encoding, and other presentation concerns belong to `SymbolRenderer` / `QRCodeImageBuilder`.

---

## Why

- **One canonical binary pipeline.** String and span inputs, object and span outputs, and all rendering APIs ultimately depend on the same mode, ECC, interleaving, placement, and masking logic.
- **Tables drive structural correctness.** Capacity, block grouping, alignment centers, and remainder counts come from centralized Standard QR tables rather than duplicated conditionals.
- **Encoder-decoder parity by construction.** Function-module layout, format generation, and block conventions are shared or tested in both directions.
- **Independent validation.** ZXing decodes generated symbols across modes, ECI choices, ECC levels, boundary capacities, and large versions. The in-process decoder adds all-version round trips and error-injection coverage.
- **Optimizations are representation changes, not algorithm changes.** The production kernels are guarded by parity tests against simple reference implementations for ECC, interleaving, placement, and mask scoring.
- **Deterministic output.** Version scan order, block order, interleaving order, mask tie-breaking, zeroed remainder bits, and clean destination handling make repeated calls byte-for-byte stable.

---

## Decisions

- **Single segment per input, by default.** It keeps the default path auditable and makes mode selection a single pass; the trade-off, non-minimal symbols for mixed-mode payloads, is answered by the opt-in `QRSegmentation.Optimal`, which never changes the emitted stream unless it lowers the version.
- **No Kanji mode when encoding.** Unicode input is represented as UTF-8 Byte mode with ECI 26, at the cost of lower capacity for Japanese text. This originally also avoided shipping a Shift_JIS table; that argument lapsed when Kanji DECODING shipped and the assembly gained the 16 KB JIS X 0208 table, so the remaining reasons are output stability and not adding an encoding dependency.
- **ASCII omits ECI by default.** This minimizes overhead and maximizes compatibility. Latin-1 and wider Unicode receive explicit ECI declarations under automatic selection.
- **BOM is explicit and UTF-8-only.** `utf8BOM` affects the stream only when the selected data mode is Byte and the effective ECI is UTF-8.
- **Version can be forced.** Fixed-size applications need control over symbol dimensions, so `requestedVersion` bypasses minimum-fit selection rather than acting as a lower bound.
- **Quiet zone is output policy, not core symbol data.** Core encoding is always performed on the `21 + 4 * (version - 1)` matrix. Quiet-zone storage differs by output model without changing encoded modules.
- **Mask scoring includes final metadata.** Scoring a data-only candidate can choose a different winner from scoring the actual final matrix, so format and version information are part of candidate evaluation.

---

## Lessons Learned

### Capacity and text encoding

- **Byte-mode length means encoded bytes, not UTF-16 characters.** This is the central boundary condition for UTF-8 input; using `text.Length` would under-size every non-ASCII payload and make version transitions wrong.
- **The UTF-8 BOM belongs in the Byte character count.** Treating it as out-of-band metadata creates symbols that some readers reject because the declared count is three bytes short.
- **ECI overhead can change the version and the mask.** Twelve header bits can cross a version boundary; even when they do not, they shift every following bit, which changes ECC, interleaving, placed data, and often the selected mask.
- **Exact-capacity inputs need no full terminator.** Padding must add `min(remaining, 4)` terminator bits rather than assuming four bits are always available.
- **A branch test whose fixture cannot reach the branch asserts nothing.** The first ECC-boost-under-segmentation test used content whose planned stream (350 bits) could never fit the next level's capacity (272), so its `>= requested level` assertion held with the boost never firing; adversarial review caught it by computing the headroom. Boost and threshold tests must pin the exact resulting level on a fixture whose arithmetic is worked out in the test comment.
- **Sub-microsecond benchmark tables need a noise disclaimer.** Rows where an arm doing strictly more work measures "faster" (Optimal beating Single on all-numeric content that never plans) are code-layout and run-to-run jitter; single-iteration BenchmarkDotNet jobs on this class of benchmark swing ±30%, so ratios are only trusted from MediumRun jobs and deltas within a few percent are not signal.

### Matrix construction

- **Function areas need one shared source of truth.** Placement, data walking, masking, and decoding all depend on exactly the same blocked-module geometry. Reconstructing it independently is an invitation for one-module drift around format, alignment, or version areas.
- **Remainder bits must be deterministic even though they carry no payload.** Stack and pooled buffers are not guaranteed to be zeroed; leaving the tail untouched makes output depend on prior memory contents.
- **Mask candidates must contain their own format bits.** The 30 format modules affect runs, 2×2 blocks, finder-like windows, and dark balance. Scoring without them is observably not the same algorithm.
- **The quiet zone should not inflate object storage.** Keeping it virtual reduced `QRCodeData` to core bits only while preserving the public matrix coordinate space.

### Performance

- **Bit-packing was the decisive mask optimization.** Parallelizing eight expensive byte-domain candidates still pays the byte-domain cost and adds scheduling/allocation overhead. Packed scalar rows measured roughly 8× at version 1, 44× at version 10, and 30–40× at version 40 over the former per-module implementation.
- **For the small versions the eight candidates are the vector lanes, not the rows.** With one `ulong` per row (versions 1-11), scoring four candidates per vector removes every scalar tail and every per-candidate horizontal reduction, and lets the pre-masked templates and the format-bit overlays be per-version tables (one XOR / OR per row); together with fusing popcounts of provably disjoint bit sets (dark vs light 5-runs, the two finder-like orientations) this halved the mask kernel again (1.6-1.9x) after the lane-per-row round. Fusing all scoring passes into one loop lost (register pressure), and vectorizing the balance score bought nothing measurable.

- **Sequential output wins during interleaving.** Round-robin source reads with a contiguous destination measured better than sequential source reads with scattered writes, despite the strided access.
- **The data placement stream should stay in a register.** Refilling a 64-bit MSB-aligned accumulator removes a byte load and variable shift from each module and enables a two-module fast path for the common unblocked case (the reference walk).
- **Everything the placer derives from the version alone belongs in a per-version table.** Painting the function patterns, building the blocked bit mask and deciding the zigzag order per symbol was ~25-35 % of the encode; a cached template + mask + walk order (built by the reference painters, so correct by construction) turned the placer into a memcpy plus one vector bit expansion and a run/scatter store pass: 9x at version 1 and 4.5x at version 40 in the kernel, -26 % (v1) to -44 % (v40) on the encode E2E, and the decoder shares the cached mask. Strided byte scatter is store-issue bound; wider stores per row do not help (same finding as the rMQR placer).
- **Reed-Solomon setup is reusable.** Generator polynomials depend only on ECC count, so caching their log-domain form removes repeated polynomial construction and reduces the scalar inner loop to table lookup and XOR.
- **Steady-state allocation guarantees require warm-up-aware tests.** Lazy tables, JIT compilation, and `ArrayPool` initialization are one-time effects; the Release-only allocation test warms them before measuring the span API.

---

## Validation

The encoder is covered at several independent layers:

| Layer | Evidence |
|---|---|
| Bit stream | mode, ECI, count widths, Numeric/Alphanumeric/Byte packing, BOM, padding, canonical `HELLO WORLD` codewords |
| Capacity | exact-fit and one-over boundaries across modes, ECC levels, and representative versions |
| ECC | ISO worked examples and scalar/SIMD parity against naive GF(256) division |
| Interleaving | unequal block groups, single-block identity, version-40 block counts, naive-reference parity |
| Placement | binary placement parity against a per-module zigzag reference |
| Masking | all-zero, all-one, and realistic matrices compared with byte-domain reference formulas |
| Output APIs | `QRCodeData` and span matrices compared module-for-module, dirty buffers, quiet-zone sizes, overflow checks, allocation test |
| External compatibility | generated images decoded by ZXing |
| Internal compatibility | encode/decode round trips for all versions and ECC levels |

When the optimized implementation and a reference disagree, the simple reference and external decoder are treated as the specification oracle; performance code is not allowed to define behavior.
