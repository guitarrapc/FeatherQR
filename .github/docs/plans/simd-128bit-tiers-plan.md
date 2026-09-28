# 128-bit tiers for kernels that run scalar

## Purpose

`SimdTiers.Expected` ([SimdTiers.cs](../../../src/FeatherQR/Internals/SimdTiers.cs)) states which tier each of 28 kernels takes per build class. On 2026-09-28, 11 ran scalar on x64 without AVX (a default NativeAOT publish), 19 on WebAssembly and 1 on ARM64. More run scalar on CPUs without GFNI, fast PEXT or the ARM64 dot product. Almost all of them are SIMD kernels whose only vector tiers are 256-bit, x64 SSE or ARM64 AdvSimd. A rough first measurement put a default NativeAOT publish 2.6x behind the JIT on a URL-sized encode, and about 4x on image decode and rMQR matrix decode ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured)).

This plan lists every such cell, what the kernel's wider tier relies on and what a 128-bit form must replace. It adds a tier where one beats what that build runs today, measured on that build.

It was split out of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (Phase 6b), which keeps the timing: the tiers are internal, land before 2.0.0 to be in it, and move to 2.1.0 if they slip. When this plan completes, its durable content goes into the [SIMD tier inventory](../specs/qrcode-symbologies.md#simd-tier-inventory) and the per-symbology records, and this file is deleted.

## Scope

| In | Out |
|---|---|
| Portable `Vector128` tiers for the scalar cells of `SimdTiers.Expected` on x64 without AVX and on WebAssembly | The instruction set a NativeAOT publish targets, and README guidance on it (phase 3 of the cross-language plan). Even with these tiers, a default publish runs no 256-bit tier |
| The same tier where it also fills ARM64's scalar cells (rMQR value writers; luminance conversion without the dot product) and x64 AVX2's (Structured Append parity and scanner; syndromes without GFNI; rMQR extraction without fast PEXT) | New 256-bit or 512-bit tiers, and tiers for AVX without AVX2 (`x86-64-v2,avx`) |
| A timing mode that runs on a default NativeAOT build and on WebAssembly, interpreted and AOT-compiled | The netstandard builds, which have no intrinsics |
| The nine portable tiers WebAssembly already runs, timed there against scalar for the first time | A .NET 8 WebAssembly app: the WebAssembly report builds net10.0 |
| | Public API, output bytes and allocations: none change |

## Inventory

Cells as of 2026-09-28. "Start from" is the existing tier whose structure a portable form takes. "No portable form" is what it has to replace. No 128-bit form of these kernels has been measured on x64 without AVX or on WebAssembly.

### No x64 tier below AVX2: scalar on x64 without AVX and on WebAssembly

| Kernel | Also scalar on | Start from | No portable form | Benchmarks |
|---|---|---|---|---|
| `ModulePlacerMaskCode` | | AdvSimd, lane-per-row | Vector popcount (`cnt`, `psadbw`), pairwise widening adds; the unpack's table lookup becomes a constant shuffle | Standard QR encode |
| `Binarizer` | | AdvSimd, 32 pixels a step, mostly portable | `AddAcross`; the movemask is native on SSE2 and WebAssembly | every image class |
| `FinderRowEdges` | | AdvSimd; its `ClassifyWindows8` is already portable | The NEON fold in `DarkWord`. A 16-pixel `LessThan` + `ExtractMostSignificantBits` path already exists for partial words | every image class |
| `LuminanceConverter` | ARM64 without the dot product | New. The ARM64 tier's row modes and compositing are portable | `pmaddubsw`, `pmaddwd`, `UDOT` | `SKBitmap` decode rows only (premultiplied RGBA) |
| `QRSampleGridPiecewise` | | AdvSimd, four lanes | `MultiplyAdd` only. The risk is matching the scalar float-to-int cast on each runtime | `QRCodeImageDecodeEndToEnd`, when the mesh runs |
| `EccBinaryDecoder` | x64 AVX2 without GFNI | AdvSimd, two accumulators over 24 KB tables | `PMULL` | matrix decode, all three symbologies |
| `RmQRExtractCodewords` | x64 AVX2 without fast PEXT | AdvSimd pair planes | `SLI`, `RBIT`, two-input unzip; PEXT/PDEP | rMQR matrix decode |
| `ModeSegmenterLanes` | | AdvSimd, four 32-bit lanes a group | The 4-register class lookup (the 256-bit tier classifies in scalar), `SLI` | Structured Append encode, Optimal |
| `StructuredAppendLanes` | | AdvSimd, eight 16-bit saturating lanes | `UQADD` (`Vector128.AddSaturate` is .NET 10 only), `UMAXV` | Structured Append encode, Optimal |

### ARM64 only: scalar on every x64 and on WebAssembly

| Kernel | Also scalar on | Start from | No portable form | Benchmarks |
|---|---|---|---|---|
| `StructuredAppendParity` | x64 AVX2 | AdvSimd, nearly portable | `UMAXV`; the `Vector64` fold and tail (`Vector64` is not accelerated on x64 or WebAssembly) | Structured Append encode |
| `StructuredAppendScanner` | x64 AVX2 | AdvSimd | `UMAXV`/`UMINV`, `ADDV`, `EXT`; the alphanumeric membership test (`TBL` + per-lane shift), which range compares replace as in `TextAnalyzer`'s ARM64 tier | Structured Append encode, chunk ends |

### x64 SSE tier only: scalar on WebAssembly

| Kernel | Also scalar on | Start from | No portable form | Benchmarks |
|---|---|---|---|---|
| `TextAnalyzer` | | SSE2 | None | every encode |
| `ModuleBitPacker` | | The 16-module step SSSE3 and AdvSimd share | The byte-reverse shuffle: a non-constant `Vector128.Shuffle` index is a software loop on .NET 8 | Micro QR and rMQR encode and matrix decode |
| `EccBinaryEncoder` | | AdvSimd, a port of the SSSE3 tier | Variable-index byte shuffle (`ShuffleNative` is .NET 10 only) | every encode, v40 heaviest |
| `ModulePlacerExpandBits` | | SSSE3 | None (ARM64 keeps `ushl`) | Standard QR encode, `TryDecode(QRCodeData)` |
| `RmQRModulePlacer` | | SSSE3 expand step | None for the expand. The ARM64 store transpose needs two-input zip/unzip; other builds keep the portable pair stores | rMQR encode |
| `MicroQRModulePlacer` | | SSSE3 `WriteExpand16` | None | Micro QR encode |
| `MicroQRByteSegment` | | AdvSimd | None | Micro QR Byte encode |
| `RmQRValueSegments` | ARM64 | SSE4.1 | `pmaddwd`, `pmaddubsw`, `phaddd`, a variable nibble shuffle: a rewrite, not a port | rMQR Numeric and Alphanumeric encode |

### What is already known

- **`ModulePlacerMaskCode`.** No SSE or 128-bit x64 tier was ever measured. The AVX2 tier predates the finding that a default NativeAOT publish has no AVX. The ARM64 128-bit tier runs 2.4-3x scalar at v1-10 and 1.14-1.2x at v20-40 (Apple M2). Under AVX2, lane-per-pattern beat lane-per-row 1.6-1.9x at v1-10; at 128 bits it holds two patterns a vector. Popcount is the cost, and neither `psadbw` nor `i8x16.popcnt` is portable, so this kernel is the likeliest case for D2 and D3.
- **`Binarizer`.** A 128-bit tier was measured on x64 under the JIT, where it never runs. At 16 pixels a step it ran gradients 1.85x slower than scalar. The 32-pixel cadence fixed that and was still 8 % behind on gradients. No other image kind was recorded. The spec summary ("against the 256-bit tier, not scalar", [qrcode-symbologies.md](../specs/qrcode-symbologies.md#simd-tier-inventory)) is inaccurate; phase 1 corrects it with the new numbers.
- **`FinderRowEdges`.** Its scalar cell does not run scalar code. Without the edge-list kernel, rows go to `FinderRowMask`'s 128-bit mask walk, so a new tier must beat that walk. A scalar edge list measured level with it. Sixteen windows as two eight-lane halves lost 5-30 % on ARM64.
- **`LuminanceConverter`.** Scalar is 14-30x slower than the AVX2 tier. Before any tier, conversion was 69 % of `TryDecode(SKBitmap)`. A candidate form, unmeasured: keep pixels as 32-bit lanes, mask R/B and G into 16-bit lanes, multiply there, fold. All of those ops are native on SSE2 and WebAssembly. It would also fill ARM64 cores without the dot product, a case the rMQR decoder record names for a revisit.
- **`QRSampleGridPiecewise`.** Since 2026-09-27 the mesh runs only when the alignment search finds nothing, or after the anchored grid fails. Over 9,720 intact renders it decoded 12 times instead of 4,515, and every recorded share predates that. It likely stays scalar unless phase 1 finds a shape where it still counts (bowed, alignment lost).
- **`EccBinaryDecoder`.** Each lane's multiplier is fixed, so the carried multiply is GF(2)-linear in the accumulator. Eight masked XORs can replace `PMULL` with no reduction step (a candidate, unmeasured). A corrected block runs the pass twice.
- **`RmQRExtractCodewords`.** The ARM64 pair planes run 0.30x (R17x139) to 0.92-0.94x (R7x43, R11x27) of the portable walk, against a fixed cost of about 35 ns. A portable form pays that floor plus the emulated `SLI` and `RBIT`.
- **`ModeSegmenterLanes`, `StructuredAppendLanes`.** The dispatch falls through to the 256-bit body without a check of its own. The caller's gate and the length thresholds (×128 for 256-bit, ×20 for ARM64) are measured again on each build. Vectorizing one chunk's step lost, because the broadcast and horizontal minimum sit on the serial chain.
- **`TextAnalyzer`.** The recorded signed-threshold defect applies to a new tier too: thresholds unsigned or bit tests, and a parity test through a direct entry.
- **`ModuleBitPacker`, `EccBinaryEncoder`.** Whether a non-constant `Vector128.Shuffle` lowers to `i8x16.swizzle` on WebAssembly is unverified.
- **Expand steps (`ModulePlacerExpandBits`, `RmQRModulePlacer`, `MicroQRModulePlacer`).** A constant-index `Vector128.Shuffle` should compile to the SSSE3 step's `pshufb`. Strided byte scatter is store-issue bound on x64.
- **`MicroQRByteSegment`, `MicroQRModulePlacer`.** One vector step per symbol of at most 15 characters or 17 rows. Expected under the bar.
- **`RmQRValueSegments`.** Numeric is 0.3 % of an rMQR encode, alphanumeric 5.4 %. On ARM64 a batched alphanumeric writer won 19-28 % alone and lost end to end, because the three writers share one `switch`. Expected to stay scalar with that reason.
- **The nine tiers WebAssembly already runs** (luminance inverter, regional binarizer, finder and alignment row masks, three samplers, the sub-finder lattice, the Latin-1 writer) have never been timed there.

### What the rough numbers point at

With AVX but not AVX2 (`x86-64-v2,avx`), `Avx2` reads true and `Vector256.IsHardwareAccelerated` false. That build recovered most of the encode gap and almost none of the image decode gap. Read against the table: the encode gap sits in kernels gated on `Avx2`, where only `ModulePlacerMaskCode` falls to scalar rather than SSE. The image decode gap sits in kernels gated on `Vector256`, mainly `Binarizer` and `FinderRowEdges`. The rMQR matrix decode gap has two scalar kernels, the syndrome pass and the extraction. This is an inference from one rough run. Phase 1 replaces it.

## What has to stay true

- **A tier ships only if it beats what the build runs today, on that build.** That is scalar, except for `FinderRowEdges` (the mask walk). It must win where the kernel matters and lose nowhere beyond noise. A kernel under about 3 % of every benchmark shape on a build stays as it is there, with the measured reason in a comment beside its row.
- **Each build is measured on itself.** x64 without AVX is ranked under `DOTNET_EnableAVX=0` and confirmed on a real default NativeAOT build, since the knob runs the JIT's code, not ILC's. WebAssembly is timed on WebAssembly. A form measured on x64 says nothing about WebAssembly.
- **Interpreted and AOT-compiled WebAssembly take the same tier.** One flag gates both, so a tier is judged on both (D1).
- **Portable `Vector128` first.** One tier then serves x64 without AVX, WebAssembly, and any ARM64 or AVX2 cell it reaches.
- **Existing tiers stay.** A portable step goes after an SSE or AdvSimd tier. It replaces one only where both compile to the same code on that architecture.
- **Output identical to scalar.** Each tier gets a parity test through a direct entry, since the dispatch hides a lower tier on a machine with a higher one. Planted faults in it must fail the tests.
- **Unchanged cells compile as before.** Where a class's cell stays, the dispatch's disassembly stays identical: JIT on .NET 8 and 10 with and without AVX, ILC for default x64, `x86-64-v3` and ARM64. An added branch can change what the JIT inlines.
- **The table and the files follow.** Each new tier gets its row in `SimdTiers`, its files in `SimdTiersTest.KernelFiles`, and a file named for its family (`{stem}.Vector128.cs`). A tier added to an inline stem file updates `SimdLayoutTest.InlineTiers`.
- **Both .NET targets compile every tier.** `ShuffleNative`, `AddSaturate`, `NarrowWithSaturation` and `MinNative`/`MaxNative` exist on .NET 10 only. A tier that uses them has a .NET 8 form, or leaves .NET 8 on its current tier. Shuffle lowering is checked on every target a tier runs on.
- **No public API, no allocation.** The zero-allocation tests stay green in Release.

## Measuring

- **Timing mode.** Phase 1 adds a timing mode to `tests/FeatherQR.AotAnalysis` (NativeAOT) and `tests/FeatherQR.WasmReport` (WebAssembly under Node.js), from one source file shared like `SimdReport.cs` (D5). It times the benchmark shapes' inputs end to end, and each kernel alone on the inputs those shapes feed it. Each kernel's tier and its scalar entry are called directly. Both projects already see the internals.
- **Per build and shape:** end-to-end time, each kernel's share, and the kernel's time against scalar.
- **Where variants are ranked.** BenchmarkDotNet under `DOTNET_EnableAVX=0` ranks variants for x64 without AVX; the timing mode confirms them on the real builds. Variants for WebAssembly are ranked on WebAssembly.

## Phases

Each phase appends a Progress log entry: Done, Lessons, and the benchmark delta per build.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Harness and ranking | The timing mode; baselines on x64 without AVX (knob and NativeAOT) and WebAssembly (interpreted and AOT); per-shape kernel shares; the nine existing WebAssembly tiers against scalar; `Binarizer`'s record measured again and the spec corrected | A ranked list per build. Kernels under the bar named with their numbers. Any WebAssembly tier that loses to scalar reported |
| 2 | Near ports | Kernels whose ARM64 or SSE tier is close to portable: `FinderRowEdges`, `StructuredAppendParity`, `StructuredAppendScanner`, `TextAnalyzer`, `ModuleBitPacker`, and the expand steps (`ModulePlacerExpandBits`, `RmQRModulePlacer`, `MicroQRModulePlacer`, `MicroQRByteSegment`) | Each cell raised, or carrying its reason |
| 3 | Image decode | `Binarizer`, `LuminanceConverter` (also ARM64 without the dot product), `QRSampleGridPiecewise` | As phase 2 |
| 4 | Encode lanes and mask scoring | `ModulePlacerMaskCode`, `ModeSegmenterLanes`, `StructuredAppendLanes` | As phase 2 |
| 5 | GF(256) and bit planes | `EccBinaryEncoder` (WebAssembly), `EccBinaryDecoder` with D4, `RmQRExtractCodewords` | As phase 2 |
| 6 | rMQR value writers | `RmQRValueSegments`, decided from phase 1's shares; a rewrite only if it clears the bar | As phase 2 |
| 7 | Confirm and fold | End-to-end before and after on default NativeAOT (linux-x64, win-x64) and WebAssembly (interpreted, AOT). Spec inventory, scope row and per-symbology records updated. Plan deleted | Every scalar cell in the x64-without-AVX and WebAssembly columns raised, or carrying its measured reason. Public API unchanged |

Phases 2-6 group kernels by shared work. Phase 1's ranking sets the order, so the kernel behind most of a build's gap goes first, whatever its group. A group whose kernels all fall under the bar closes with their reasons. Each phase can be its own PR.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | A tier wins on AOT-compiled WebAssembly and loses interpreted, or the reverse | Ship only if it loses on neither beyond noise. Blazor WebAssembly publishes interpreted unless the app opts into AOT, so the interpreted build is what most users run |
| D2 | An x86 SSSE3/SSE4.1 tier where the portable form loses on x64 without AVX (the `psadbw` popcount for mask scoring, `pmaddubsw` for luminance) | Allowed under the same bar, after the portable tier, and only by a measured margin over it: it serves no other build |
| D3 | A WebAssembly `PackedSimd` tier where the portable form lacks an instruction WebAssembly has (`i8x16.popcnt`, `i32x4.dot_i16x8_s`, `i8x16.swizzle`) | Not by default. It needs a new `SimdTier` and a new flag family in the tests. Consider it only where phase 1 shows the portable form losing for exactly that instruction |
| D4 | A 128-bit GFNI syndrome tier | After the portable tier, and only if it beats it on a GFNI CPU without AVX. It fills that cell on GFNI CPUs only, and CI's x64 runner had no GFNI on 2026-09-28, so only a developer machine would run it |
| D5 | Where the timing harness lives | The shared timing mode above. If the cross-language benchmark CLI lands first, use it instead: it times the same builds |

## Progress log

(none yet)
