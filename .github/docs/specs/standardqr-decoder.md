# Standard QR Decoder

Design record for the Standard QR decode feature (`QRCodeDecoder`): what it does, why it is scoped the way it is, and what was learned during implementation. Implementation details live in code comments next to the code (see the [spec-to-code map](standardqr-spec-map.md)). Micro QR and rMQR decoders get their own design records as they are implemented, see [QR Symbology Architecture](qrcode-symbologies.md).

---

## What

`QRCodeDecoder` decodes QR codes back into text at two levels.

### Matrix level

Input: `QRCodeData` or a byte-per-module span (the same format `QRCodeGenerator.Create(Span<byte>)` produces).

Behavior: runs the full inverse pipeline, format info → unmask → codeword extraction → deinterleave → Reed-Solomon correction → bit stream parsing. The `Span<char>` overloads complete allocation-free in steady state.

### Image level

Input: `SKBitmap` or a grayscale luminance span.

Behavior: adds binarization (Otsu), finder pattern detection, orientation resolution, bottom-right alignment pattern search (version 2+), and projective grid sampling (4-point perspective transform) in front of the matrix pipeline. Version 14+ symbols additionally build a piecewise-bilinear mesh over the full alignment-pattern lattice (wavefront detection with iterated homography refinement). A partially-detected mesh falls back to the global transform, so the mesh can never decode worse than it.

On success the result carries the symbol's four corners in image coordinates (`QRCodeDecodeInfo.Corners`; contract in [qrcode-symbologies.md](qrcode-symbologies.md#public-api-direction)). They come from the mapping that decoded. On the global path that is the 4-point transform. On the mesh path (version 14 and up, four or more alignment rows) the global transform is a sibling estimate the decode never validated, and it drifted 18 to 30 modules at the bottom-right on a keystoned version 16 while the mesh decoded cleanly; the corners there come instead from a transform anchored on the three finder centres and the mesh's diagonal-last lattice node (at its last grid coordinate). That node is a detected alignment pattern in the ordinary case and a refinement-round prediction when that pattern was missed; anchoring instead on the nearest node the mesh actually detected was tried and produced byte-identical corners on every mesh version and tilt measured, with the pattern erased included, so the simpler form is what ships. Bilinear extrapolation of the outer mesh cell was tried first and left the top-left 0.6 to 0.75 module short, because the finder side of the mesh is only as straight as its first alignment row. Version 1 has no alignment pattern, so under perspective its bottom-right corner is the parallelogram estimate the sampler also uses, which is why the corner tests hold a version-2+ keystone to half a module and cover version 1 only flat and rotated (measured: v1 stays within 0.18 module over a full rotation sweep, its gap is keystone alone). That parallelogram fallback is not version 1's alone: `BuildGridTransform` reaches it on any version once all three `allowance` windows fail to find the bottom-right alignment pattern, and a review round measured that one erased module there is enough — the symbol still decodes on error correction and reports that corner up to about a module and a half out (v2 at 3 % keystone: 1.00 module, and v10 at 1 %: 1.11). The corners themselves are also only half-module accurate from about 8 px per module up, because the alignment centre is pixel-resolved: its error holds near 4 px at 6, 8 and 12 px per module (4.09, 3.63, 3.94), which is 0.68 module at the coarse end and 0.33 at the fine one. Both facts are in the public contract; tightening either means giving the alignment anchor sub-pixel refinement, which is the follow-up recorded below.

### Support tiers

"Tier" is this project's own shorthand for image-input difficulty, it is not standard QR terminology. The tiers staged the implementation and now name the scope line:

| Tier | Input class | Status |
|---|---|---|
| Tier 1 | Clean images: screenshots, rendered QR codes, flatbed scans, arbitrary rotation, mirroring, reflectance reversal; no meaningful perspective distortion | Supported |
| Tier 2 | Tier-1-clean images with mild perspective (keystone): global 4-point homography, plus the piecewise mesh for version 14+ | Supported within the measured envelope, see [Input envelope](#input-envelope) |
| Tier 3 | Real-world camera photos: strong perspective, uneven lighting, blur, heavily damaged symbols | Out of scope by design, use ZXing.Net (see [Why](#why)) |

Matrix-level decoding takes an exact module matrix as input, so the tiers apply only to the image level.

### Supported

| Area | Coverage |
|---|---|
| Versions | 1–40 |
| ECC levels | All |
| Segment modes | Numeric, Alphanumeric, Byte, Kanji (JIS X 0208; decode only, the generator never emits it) |
| ECI | ISO-8859-1, UTF-8 |
| Text handling | UTF-8 BOM, multi-segment streams |
| Image transforms | Arbitrary rotation, mirroring, reflectance reversal (light-on-dark) |
| Perspective (Tier 2) | Mild keystone, see [Input envelope](#input-envelope) |

### Unsupported

The following are detected and reported; they are never misdecoded:

- FNC1
- Structured Append
- Other ECI charsets

### Input envelope

Image support targets clean inputs: screenshots, rendered QR codes, scans, with arbitrary rotation, mirroring, reflectance reversal (light-on-dark, one inverted retry), and mild perspective.

Measured keystone envelope (Tier 2):

| Version range | Approx. keystone limit |
|---|---|
| 1 | ~2% (no alignment pattern) |
| 2–5 | ~12–15% of the edge |
| 10–15 | ~6–10% |
| 14–25 | ~8% via the mesh (content-marginal around version 20) |

Real-world photo robustness (strong perspective, uneven lighting, blur) is deliberately out of scope; the API docs point such users to ZXing.Net.

---

## Why

- **Tier-1/2 scope by design.** Image support targets clean inputs rather than unconstrained camera photos. Real-world photo robustness is a computer-vision problem that ZXing spent years on. Keeping this library dependency-free and auditable requires drawing the line here.
- **Encoder-decoder parity by construction.** The decoder reads the encoder's own per-version placement tables (`ModulePlacer.GetLayout`, built by the reference function-pattern painters) for its blocked-module map, and the format-information decoder matches against patterns generated by the encoder's own `GetFormatBits`. The two sides cannot drift apart.
- **Round-trip testing strengthens the encoder.** Every `Create` output is verifiable in-process (`encode → decode == input`), independent of ZXing. Combined with the existing ZXing-decodes-our-output tests and the ours-decodes-ZXing-output tests, both directions are cross-validated against an independent implementation.
- **Fail closed.** Reed-Solomon correction re-verifies syndromes after applying corrections, and format decoding requires Hamming distance ≤ 3 (the BCH(15,5) correction bound). A wrong decode is strictly worse than a failed decode.
- **Zero allocation in steady state.** Matrix decoding uses stackalloc below 512 working bytes and `ArrayPool` above; the blocked-module mask comes from the encoder's per-version placement tables (built once per version actually used, shared with the encoder). The `Span<char>` overloads complete without any heap allocation (verified by a Release-only test).

---

## Decisions

- **Version comes from the matrix size, not the version information blocks.** At the matrix level the size is exact; at the image level the dimension estimate is snapped to the nearest valid size. Reading the v7+ version info blocks would only matter for heavily damaged symbols, which are Tier-3 inputs (out of scope).
- **Format decoding by exhaustive Hamming match, not BCH syndrome decoding.** There are only 32 valid patterns; comparing against all of them is simpler, branch-free, and naturally yields the minimum-distance decision.
- **Byte-mode charset heuristic.** Without an ECI header the spec default is ISO-8859-1, but UTF-8 payloads are common in the wild. The decoder validates the payload as UTF-8 (strict RFC 3629) and falls back to ISO-8859-1, ASCII decodes identically either way, so the heuristic can only affect high-byte payloads.
- **Kanji mode is decoded but never encoded.** Japanese-market encoders emit it, and a decoder that rejects those symbols leaves callers no way through; emitting it would change shipped generator output, which is a separate decision. The Shift_JIS conversion runs off a generated 16 KB JIS X 0208 table rather than `System.Text.Encoding.CodePages`, so no dependency is added, and the mapping is JIS X 0208 rather than CP932 (they disagree on seven cells). See [QR Symbology Architecture](qrcode-symbologies.md).

---

## Lessons Learned

### Image detection and sampling

- **Horizontal-scan module size breaks under rotation.** The finder detector's run widths are measured along image rows; at 45° they inflate by √2 and the dimension estimate misses by an entire version step (first implementation returned `NotDetected` for every 45° input). Module size must be measured along the actual finder-to-finder lines (dark-light-dark runs through the pattern centers), which is rotation-invariant. The scan-derived value survives only as a fallback when those runs leave the image.
- **The light-dark-light alignment signature matches every isolated dark data module.** A single dark module with light neighbors on all four sides, extremely common in data areas, passes both the horizontal triple and the vertical cross-check, and one false positive shears the whole sampling transform into ~50% mis-sampled garbage. Only the 5×5 dark border ring distinguishes the real pattern: all eight ring samples at ±2 modules must be dark.
- **Ring validation must follow the grid axes, not the image axes.** With image-axis offsets the ±2-module ring corners land outside a rotated pattern and reject the true alignment pattern, the fix that rescued keystone-only inputs broke rotation+keystone combinations until the ring samples were taken along the finder-derived grid axis vectors.
- **Coordinate-6 lattice lines lie on the timing patterns**, their perfect 1-module alternation floods the light-dark-light alignment signature with false candidates, so edge lattice nodes are never searched, only derived.
- **Pixel-quantized module-size measurement biases the dimension estimate one whole version low.** The dark-light-dark runs walk in 1-pixel steps and originally returned the position of the first light pixel, the boundary rounded *up*, a systematic +0.07..+0.25 px overestimate of the module size. Divided into the finder distance, that undershoots the module count by several modules at small pixels-per-module: a fixed 512 px canvas read version 14 (6.32 px/module) as version 13, and versions 17+ as one lower, while 15/16 (near-integer px/module) happened to survive, a *non-monotonic* failure pattern that looked style-dependent but reproduced with plain black-and-white renders. Fixes: report run boundaries at step − 0.5 (centers the quantization error), and keep the second-nearest valid dimension as a one-shot retry when the primary fails. Regression-tested by decoding versions 12–25 on a fixed 512 px canvas.

### Piecewise mesh (version 14+)

- **The piecewise mesh must not trust any single upstream estimate.** Every naive seeding strategy failed in a measured way before the shipped design emerged: (a) predicting all lattice nodes through the *global transform* fails exactly when the mesh is needed, its fourth anchor carries the parallelogram error (≈ 2·keystone-shrink, ~9 modules at v20/4%); (b) a *finder-only affine* seed drifts up to ~1.5 modules mid-edge because projective foreshortening makes lattice spacing non-uniform; (c) *linear* extrapolation of the prediction-only edge nodes fails for the same reason (lattice points are collinear but their spacing along the line is projective, measured 12 px off where quadratic Lagrange left 1 px); (d) *wide search windows* turn data-area false positives into cascading mesh corruption, because the wavefront propagates every accepted node into downstream predictions. The shipped combination: wavefront parallelogram prediction from processed neighbors, tight (2.5-module) windows with a wider window only for starters, iterated homography refinement anchored on the farthest detected node, quadratic edge extrapolation, and an unconditional global-transform fallback on decode failure so the mesh can never regress below the single homography.

### Matrix and format decoding

- **A mirrored matrix does not reliably fail at the format stage.** The transposed format bit sequence can land within BCH correction distance of a *different* valid format pattern, so a mirrored image can surface as `DataUncorrectable` (wrong mask garbles everything downstream) instead of `FormatInformationInvalid`. The mirror retry (transpose + re-decode) must therefore trigger on any failure, not just on format errors.
- **Testing "corrupted format info" needs care for the same reason.** Flipping all 15 format bits produces the complement, which may be close to another valid codeword. The negative test searches for a pattern with distance > 3 from all 32 candidates instead of assuming any corruption is rejected.
- **The RS decoder's convention must be read off the encoder, not assumed.** QR uses generator roots α^0..α^(n-1) (b = 0), which puts an X_k factor in the Forney formula that b = 1 conventions omit. Deriving it from `EccBinaryEncoder`'s documented generator polynomial made the round-trip pass on the first run.

### Performance

End-to-end numbers live in `src/FeatherQR.Benchmark` (`QrCodeDecodeEndToEnd`), with matrix scenarios mirroring the encode scenarios for direct comparison.

- **Syndrome pass dominates steady-state cost.** The syndrome pass is the cost every decode pays (clean blocks exit early after it); Berlekamp-Massey/Chien/Forney run only on blocks that actually contain errors. A GFNI kernel (net10.0+ x64, `EccBinaryDecoder.Simd.cs`) measured ~284× per version-40 block; end-to-end version-40 matrix decode dropped 369 µs → 131 µs, decode is now faster than encode. An AdvSimd kernel (net8.0+ ARM64, `EccBinaryDecoder.Simd.Arm.cs`, 2026-08-20) closes the same gap on ARM: 61× per version-40 block, and Standard QR v1 matrix decode 1,586 → 1,103 ns on Apple M2. It is not a port of the GFNI design — GF2P8MULB is hardwired to the AES polynomial, so x64 spends its design on a field isomorphism to borrow it, whereas PMULL carries no fixed modulus and ARM instead pays for the reduction mod 0x11D (by nibble-split table lookup) and reads the per-byte data terms straight out of 24 KB of lazily built tables. The scalar tier was improved in the same round to run four syndromes per pass (2.2-2.9× on large blocks) — that is what netstandard2.0/2.1 and any CPU without GFNI or AdvSimd runs. Berlekamp-Massey/Chien/Forney stay scalar on every target.
- **An unstarted Standard-QR-only syndrome win is on the table.** Splitting the ARM accumulator chain four ways measured a real **-21.5 %**, but only at 149 codewords: no rMQR block exceeds 68, and at rMQR sizes it loses badly, so it was not shipped with the shared kernel. Standard QR v40-L is the workload where it pays. A 2-chain split did not reproduce outside the drift band. The private findings log carries a stride-16 redesign needing 160 B of baked constants instead of the 8 KB table the measured version used.
- **A flat ns-per-iteration across sizes means one kernel is the whole cost.** ARM matrix decode ran at a constant 3.07-3.15 ns per syndrome inner-loop iteration across a 50x size range, which is what proved the syndrome pass was essentially all of it before the AdvSimd tier — the same item was already under 1 % on x64 *after* GFNI, so the ARM position was the pre-GFNI one. Worth re-running that ratio before assuming a component still dominates.
- **The syndrome destination is 32 bytes wide, not `eccCount`.** Both vector tiers keep every syndrome in lanes and store whole registers, so `ComputeSyndromes` requires an `EccBinaryDecoder.SyndromeLanes`-length span regardless of the ECC count; lanes past `eccCount` hold syndromes of unused roots and are meaningless. This is a cross-symbology caller contract — a caller sizing the span to `eccCount` throws on x64 (the GFNI store is bounds-checked) but silently corrupts memory on ARM64, so x64 CI cannot catch the ARM failure mode; each kernel pins its own store width (`GfniKernel_WritesExactlySyndromeLanes`, `AdvSimdKernel_WritesExactlySyndromeLanes`).
- **Isomorphism root choice matters.** β = 0x03 as the isomorphism root makes φ its own inverse, so one matrix constant serves both directions. Unrolling beyond ×4 stalls, the loop moves from latency-bound (accumulator chain) to GFNI-port throughput-bound. Pre-mapping the whole codeword to remove the in-loop affine measured *slower*: that affine was off the carried dependency chain, so removing it saved nothing while the extra pass cost real time.
- **Perspective sampling SIMD is a large-symbol win only.** An AVX2 kernel (net8.0+) achieved 2.7× at version 40 (79 µs → 29 µs), scalar row-hoisted fallback elsewhere. The loop is *not* division-bound, module computations are independent, so out-of-order execution hides division latency (halving the division count measured no gain); the cost was scalar conversion/clamp/branch overhead. Sampling is ~0.8% of a typical (version 4) image decode, the win matters for large symbols only. The overhead finding transfers to 128-bit lanes: a Vector128 kernel (two independent 4-lane chains per iteration plus a 4-lane cleanup tail, same exact scalar op sequence so lanes stay bit-identical) gave Apple M2 1.84–1.93× across versions 4–40; the two-chain unroll and the cleanup tail are each worth only 2–5%, confirming fdiv latency is mostly hidden even at 128 bits and the win is amortized fixed per-iteration work. frecpe reciprocal estimate and FMA are excluded by contract, not cost, both break the SIMD-equals-scalar byte identity that keeps the fallback testable.
- **Alignment search is failure-path-dominated.** The expanding window sweep (4→8→16 modules) runs to completion when no pattern exists, up to 4× per failed decode (secondary dimension × inversion), baseline ~40% of an image decode. An AVX2 kernel (32-px SIMD dark-mask classification + tzcnt run walk × a ⌊moduleSize/2⌋ row stride) achieved 23× on the not-found sweep; the vertical cross-check recenters exactly, so striding is result-identical. The finder scan's walk-vs-build split (below) transfers intact to this kernel's much shorter windows (65–257 px vs full rows): the same NEON 64-px fold + portable 16-px tier gave Apple M2 1.4× at the prediction, 3.8× at 3 modules off and 5.8× on the not-found sweep, with the same caveat that the walk restructure alone regresses the happy path (a single ~65 px row scan is too short to pay the mask setup without the vector classify). Successful-decode E2E is unmoved (alignment is <1% of `Image_Url_M`), the gain lands on failed decodes of non-QR input.
- **Finder scan striding must be envelope-safe, not module-size-based.** The finder scan reuses the same SIMD mask-walk kernel shape, but its row stride cannot come from the module size, the module size is unknown before detection. The bound comes from the worst case instead: the 1:1:3:1:1 band is 3 modules tall for an axis-aligned symbol (under rotation the surviving band narrows — the rMQR follow-up measured a floor of 5 rows at 3 px/module, which is why `TryFind` relies on its complementary pass rather than on this arithmetic) and a version-40 symbol filling the frame has the smallest possible modules, so stride 3·height/(4·177) still hits every supported symbol's band several times. A failed stride pass rescans *only the skipped rows*, keeping its candidates, the union covers exactly the rows of a full scan for exactly one sweep of total work, so detection can never regress (a naive full-rescan fallback measured 0.87×, i.e. a regression, in the scalar variant). The best-3 selection before that fallback must run on a copy: it compacts and sorts the candidate list in place. Combined result 9.6–11.4× on the scan, image E2E (`Image_Url_M`) 309 µs → 132 µs (−57%); the SIMD walk alone is bit-identical to the scalar walk and parity-tested.
- **The finder scan's SIMD split is walk-vs-build, and only the build is per-ISA.** The tzcnt run walk is platform-neutral; the ~15-line dark-bitmask build is the only ISA-specific piece (AVX2 vpmovmskb / NEON / portable 128-bit `ExtractMostSignificantBits`, gated on `Vector128.IsHardwareAccelerated` since Vector256 acceleration implies it). NEON has no movemask: folding 64 pixels into one mask word (4 unsigned compares AND per-byte bit weights + 3 pairwise adds, the simdjson bulk-movemask shape) beat per-16 `ExtractMostSignificantBits` by 8–11% and the scalar walk by 3.3–4.1× on Apple M2; the walk restructure alone (scalar mask build) was only 1.1–1.4×, so the vector classify is where the win lives. Byte-lane `Vector128.LessThan` is already an unsigned compare (cmhi), so the x64 `min(v, t−1) == v` trick is unnecessary on the 128-bit path. Image E2E (`Image_Url_M`, M2) 129 µs → 76 µs (−41%).
- **Otsu histogram fill favors run aggregation over per-pixel increments.** On QR-like images (long runs of two values), `histogram[value]++` serializes on store-forwarding when consecutive pixels hit the same bin, measured ~8× slower per pixel than random input. Reading 8 pixels as one ulong and folding uniform groups (`v == ror(v, 8)`) into a single `+= 8` is 8–10× on QR-like input; the textbook multi-lane fix managed only ~2.4× here because two hot bins keep colliding even across four lanes. The winner is pure scalar C#, so every TFM gets it (no SIMD, no `#if`), and the threshold is byte-identical (`OtsuThresholdParityTest`). Accepted trade-off: uniform random noise, never-uniform groups, the adversarial floor that even real photos don't hit, pays up to ~1.3×. Image E2E (`Image_Url_M`) 132 µs → 38 µs; with the finder scan work, 309 → 38 µs (8.0×) total.
