# 128-bit tiers for kernels that run scalar

## Purpose

`SimdTiers.Expected` ([SimdTiers.cs](../../../src/FeatherQR/Internals/SimdTiers.cs)) states which tier each of 28 kernels takes per build class. On 2026-09-28, 11 ran scalar on x64 without AVX (a default NativeAOT publish), 19 on WebAssembly and 1 on ARM64. More run scalar on CPUs without GFNI, fast PEXT or the ARM64 dot product. Almost all of them are SIMD kernels whose only vector tiers are 256-bit, x64 SSE or ARM64 AdvSimd. A rough first measurement put a default NativeAOT publish 2.6x behind the JIT on a URL-sized encode, and about 4x on image decode and rMQR matrix decode ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured)).

This plan lists every such cell, what the kernel's wider tier relies on and what a 128-bit form must replace. It adds a tier where one beats what that build runs today, measured on that build.

It was split out of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (Phase 6b), which keeps the timing: the tiers are internal, land before 2.0.0 to be in it, and move to 2.1.0 if they slip. When this plan completes, its durable content goes into the [SIMD tier inventory](../specs/qrcode-symbologies.md#simd-tier-inventory) and the per-symbology records, and this file is deleted.

## Scope

| In | Out |
|---|---|
| Portable `Vector128` tiers for the scalar cells of `SimdTiers.Expected` on x64 without AVX and on WebAssembly | The instruction set a NativeAOT publish targets, and README guidance on it (phase 3 of the cross-language plan). Even with these tiers, a default publish runs no 256-bit tier |
| The same tier where it also fills ARM64's scalar cells (rMQR value writers; luminance conversion without the dot product) and x64 AVX2's (Structured Append parity and scanner; syndromes without GFNI; rMQR extraction without fast PEXT) | New 256-bit or 512-bit tiers, tiers for AVX without AVX2 (`x86-64-v2,avx`), separate x86-only tiers (D2) and a 128-bit GFNI tier (D4) |
| A timing mode that runs on a default NativeAOT build and on WebAssembly, interpreted and AOT-compiled | The netstandard builds, which have no intrinsics |
| The nine portable tiers WebAssembly already runs, timed there against scalar for the first time, and the existing tiers that lose to scalar on a build they run on | A .NET 8 WebAssembly app: the WebAssembly report builds net10.0 |
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

- **`ModulePlacerMaskCode`.** No SSE or 128-bit x64 tier was ever measured. The AVX2 tier predates the finding that a default NativeAOT publish has no AVX. The ARM64 128-bit tier runs 2.4-3x scalar at v1-10 and 1.14-1.2x at v20-40 (Apple M2). Under AVX2, lane-per-pattern beat lane-per-row 1.6-1.9x at v1-10; at 128 bits it holds two patterns a vector. Popcount is the cost. Neither .NET 8 nor .NET 10 has a portable vector popcount. A nibble-table popcount needs a variable shuffle: `ShuffleNative` on .NET 10, a software loop on .NET 8. `i8x16.popcnt` exists only through `PackedSimd`, so this kernel is the likeliest case for D3.
- **`Binarizer`.** A 128-bit tier was measured on x64 under the JIT, where it never runs. At 16 pixels a step it ran gradients 1.85x slower than scalar. The 32-pixel cadence fixed that and was still 8 % behind on gradients. No other image kind was recorded. The spec summary said it was measured against the 256-bit tier, not scalar; corrected in phase 1.
- **`FinderRowEdges`.** Its scalar cell does not run scalar code. Without the edge-list kernel, rows go to `FinderRowMask`'s 128-bit mask walk, so a new tier must beat that walk. A scalar edge list measured level with it. Sixteen windows as two eight-lane halves lost 5-30 % on ARM64.
- **`LuminanceConverter`.** Scalar is 14-30x slower than the AVX2 tier. Before any tier, conversion was 69 % of `TryDecode(SKBitmap)`. A candidate form, unmeasured: keep pixels as 32-bit lanes, mask R/B and G into 16-bit lanes, multiply there, fold. All of those ops are native on SSE2 and WebAssembly. It would also fill ARM64 cores without the dot product, a case the rMQR decoder record names for a revisit.
- **`QRSampleGridPiecewise`.** Since 2026-09-27 the mesh runs only when the alignment search finds nothing, or after the anchored grid fails. Over 9,720 intact renders it decoded 12 times instead of 4,515, and every recorded share predates that. It likely stays scalar unless phase 1 finds a shape where it still counts (bowed, alignment lost).
- **`EccBinaryDecoder`.** Each lane's multiplier is fixed, so the carried multiply is GF(2)-linear in the accumulator. Eight masked XORs can replace `PMULL` with no reduction step (a candidate, unmeasured). A corrected block runs the pass twice.
- **`RmQRExtractCodewords`.** The ARM64 pair planes run 0.30x (R17x139) to 0.92-0.94x (R7x43, R11x27) of the portable walk, against a fixed cost of about 35 ns. A portable form pays that floor plus the emulated `SLI` and `RBIT`.
- **`ModeSegmenterLanes`, `StructuredAppendLanes`.** The dispatch falls through to the 256-bit body without a check of its own. The caller's gate and the length thresholds (×128 for 256-bit, ×20 for ARM64) are measured again on each build. Vectorizing one chunk's step lost, because the broadcast and horizontal minimum sit on the serial chain.
- **`TextAnalyzer`.** The recorded signed-threshold defect applies to a new tier too: thresholds unsigned or bit tests, and a parity test through a direct entry.
- **`ModuleBitPacker`, `EccBinaryEncoder`.** A non-constant `Vector128.Shuffle` lowers to `i8x16.swizzle` on WebAssembly (phase 1), and to a software fallback on a default NativeAOT publish.
- **Expand steps (`ModulePlacerExpandBits`, `RmQRModulePlacer`, `MicroQRModulePlacer`).** A constant-index `Vector128.Shuffle` should compile to the SSSE3 step's `pshufb`. Strided byte scatter is store-issue bound on x64.
- **`MicroQRByteSegment`, `MicroQRModulePlacer`.** One vector step per symbol of at most 15 characters or 17 rows. Expected under the bar.
- **`RmQRValueSegments`.** Numeric is 0.3 % of an rMQR encode, alphanumeric 5.4 %. On ARM64 a batched alphanumeric writer won 19-28 % alone and lost end to end, because the three writers share one `switch`. Expected to stay scalar with that reason.
- **The nine tiers WebAssembly already runs** (luminance inverter, regional binarizer, finder and alignment row masks, three samplers, the sub-finder lattice, the Latin-1 writer): four lost to scalar on WebAssembly AOT and on a default NativeAOT publish (phase 1) and win since phase 1b; the alignment row mask loses 16-21 % interpreted and stays, under the bar.

### What the rough numbers point at

With AVX but not AVX2 (`x86-64-v2,avx`), `Avx2` reads true and `Vector256.IsHardwareAccelerated` false. That build recovered most of the encode gap and almost none of the image decode gap. Read against the table: the encode gap sits in kernels gated on `Avx2`, where only `ModulePlacerMaskCode` falls to scalar rather than SSE. The image decode gap sits in kernels gated on `Vector256`, mainly `Binarizer` and `FinderRowEdges`. The rMQR matrix decode gap has two scalar kernels, the syndrome pass and the extraction. This is an inference from one rough run. Phase 1 confirmed the encode and matrix readings and split the image decode gap in two (Progress log).

## What has to stay true

- **A tier ships only if it beats what the build runs today, on that build.** That is scalar, except for `FinderRowEdges` (the mask walk). It must win where the kernel matters and lose nowhere beyond noise. A kernel under about 3 % of every benchmark shape on a build stays as it is there, with the measured reason in a comment beside its row.
- **Each build is measured on itself.** x64 without AVX is measured on a real default NativeAOT build. `DOTNET_EnableAVX=0` reads the same flags but lowers portable vectors as `x86-64-v2` does, so it ranks explicit x86 tiers only. WebAssembly is timed on WebAssembly. A form measured on x64 says nothing about WebAssembly.
- **Interpreted and AOT-compiled WebAssembly take the same tier.** One flag gates both, so a tier is judged on both (D1).
- **New tiers are portable `Vector128`.** One tier then serves x64 without AVX, WebAssembly, and any ARM64 or AVX2 cell it reaches. On a default NativeAOT publish it compiles for SSE2 only, and an operation SSE2 lacks becomes a sequence or a software fallback, so each operation a tier uses is timed on that build. A slow operation may use the platform's own instruction behind its check (D2, D3).
- **Existing tiers stay.** A portable step goes after an SSE or AdvSimd tier. It replaces one only where both compile to the same code on that architecture.
- **Output identical to scalar.** Each tier gets a parity test through a direct entry, since the dispatch hides a lower tier on a machine with a higher one. Planted faults in it must fail the tests. A tier is also added to `--parity` of the report projects, which CI runs on every NativeAOT build and on WebAssembly, where the test suite never runs; out-of-range and NaN inputs included, since what a runtime's cast does with them differs (phase 1b).
- **Refactoring never costs instructions.** Splitting a method, sharing a helper between kernels or moving code between files must leave each kernel's machine code no worse on every build it runs on, checked by disassembly (JIT on .NET 8 and 10, ILC for default x64, `x86-64-v3` and ARM64) and not by timing alone. A shared helper is inlined into every kernel; where it is not, the kernel keeps its own copy. Sharing code is a convenience; the instructions are the product.
- **Unchanged cells compile as before.** Where a class's cell stays, the dispatch's disassembly stays identical: JIT on .NET 8 and 10 with and without AVX, ILC for default x64, `x86-64-v3` and ARM64. An added branch can change what the JIT inlines.
- **A step only WebAssembly needs is gated on `PackedSimd.IsSupported`.** Where x64 and ARM64 keep their own tiers, a portable step behind `Vector128.IsHardwareAccelerated` would still be compiled into a default NativeAOT publish, whose SSSE3 check runs at run time, so that cell would not compile as before. The `PackedSimd` flag is a constant false on x64 and ARM64. The table names such a tier `PackedSimd`, for what it reads.
- **The table and the files follow.** Each new tier gets its row in `SimdTiers`, its files in `SimdTiersTest.KernelFiles`, and a file named for its family (`{stem}.Vector128.cs`). A tier added to an inline stem file updates `SimdLayoutTest.InlineTiers`.
- **Both .NET targets compile every tier.** `ShuffleNative`, `AddSaturate`, `NarrowWithSaturation` and `MinNative`/`MaxNative` exist on .NET 10 only. A tier that uses them has a .NET 8 form, or leaves .NET 8 on its current tier. Shuffle lowering is checked on every target a tier runs on.
- **No public API, no allocation.** The zero-allocation tests stay green in Release.

## Measuring

- **x64 without AVX under the JIT.** The existing `FeatherQR.Benchmark` with `--envVars DOTNET_EnableAVX:0` ranks explicit x86 variants, with the same statistics as every other benchmark number. Portable variants are ranked on a default NativeAOT build.
- **Real builds.** An opt-in timing mode in `tests/FeatherQR.AotAnalysis` (NativeAOT) and `tests/FeatherQR.WasmReport` (WebAssembly under Node.js), from one source file shared like `SimdReport.cs` (D5). It times the benchmark shapes end to end, and kernels alone, each through its dispatch and its scalar entry. Inputs are built from module matrices without SkiaSharp, since the WebAssembly report has only the core.
- **Shares.** WebAssembly AOT: V8's sampler (`node --cpu-prof`), self time of each kernel's methods. x64: kernel-alone time over end-to-end time for kernels called once per operation, and a knob that moves one kernel alone (`DOTNET_EnableGFNI=0` for the syndrome pass). Not the .NET sampler on Windows: it stops threads only at GC-safe points, so a call-free kernel loop hands its samples to the caller.
- **Variants for WebAssembly are ranked on WebAssembly.** Measurements so far: [references/simd-128bit-tiers-measurements.md](references/simd-128bit-tiers-measurements.md).

## Phases

Each phase appends a Progress log entry: Done, Lessons, and the benchmark delta per build.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Harness and ranking | The timing mode; baselines on x64 without AVX (knob and NativeAOT) and WebAssembly (interpreted and AOT); per-shape kernel shares; the nine existing WebAssembly tiers against scalar; the D3 probe; `Binarizer`'s record measured again and the spec corrected | A ranked list per build. Kernels under the bar named with their numbers. Any WebAssembly tier that loses to scalar reported |
| 1b | Tiers that lose | The QR, Micro QR and rMQR samplers and the rMQR sub-finder lattice: a float-to-int conversion that holds on a default NativeAOT publish and on WebAssembly AOT and interpreted, within D2 and D3, or scalar on a build where none does. The alignment row mask's interpreted loss checked against D1 | Each tier wins or ties scalar on every build it runs on; parity with scalar held |
| 2 | Near ports | Kernels whose ARM64 or SSE tier is close to portable: `FinderRowEdges`, `StructuredAppendParity`, `StructuredAppendScanner`, `TextAnalyzer`, `ModuleBitPacker`, and the expand steps (`ModulePlacerExpandBits`, `RmQRModulePlacer`, `MicroQRModulePlacer`, `MicroQRByteSegment`) | Each cell raised, or carrying its reason |
| 3 | Image decode | `Binarizer`, `LuminanceConverter` (also ARM64 without the dot product), `QRSampleGridPiecewise` | As phase 2 |
| 4 | Encode lanes and mask scoring | `ModulePlacerMaskCode`, `ModeSegmenterLanes`, `StructuredAppendLanes` | As phase 2 |
| 5 | GF(256) and bit planes | `EccBinaryEncoder` (WebAssembly), `EccBinaryDecoder`, `RmQRExtractCodewords` | As phase 2 |
| 6 | rMQR value writers | `RmQRValueSegments`, decided from phase 1's shares; a rewrite only if it clears the bar | As phase 2 |
| 7 | Confirm and fold | End-to-end before and after on default NativeAOT (linux-x64, win-x64) and WebAssembly (interpreted, AOT). Spec inventory, scope row and per-symbology records updated. Plan deleted | Every scalar cell in the x64-without-AVX and WebAssembly columns raised, or carrying its measured reason. Public API unchanged |

Phases 2-6 group kernels by shared work. Phase 1's ranking sets the order, so the kernel behind most of a build's gap goes first, whatever its group. A group whose kernels all fall under the bar closes with their reasons. Each phase can be its own PR.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | A tier wins on AOT-compiled WebAssembly and loses interpreted, or the reverse | **Decided 2026-09-29: ship only if it loses on neither beyond noise.** Blazor WebAssembly publishes interpreted unless the app opts into AOT, so the interpreted build is what most users run |
| D2, D3 | A platform's own instruction where the portable form is slow: x86 on a default NativeAOT publish (D2), `PackedSimd` on WebAssembly (D3) | **Decided 2026-09-29, one rule for both:** a tier is portable `Vector128`, and where one of its operations is slow on a build, that operation alone may use the platform's instruction behind its `IsSupported` check (`Ssse3.IsSupported ? Ssse3.Shuffle(...) : Vector128.Shuffle(...)`), kept only where it wins, measured on each build. ILC keeps the x86 check as a run-time test; ARM64 and WebAssembly fall through to the portable operation, and x64 to it where the check fails. The table names the tier for what it reads. The x86 exceptions are tested by the suite on x64; a `PackedSimd` exception runs only on WebAssembly, so its parity is checked inside the WebAssembly report. Why: phase 1 found a default NativeAOT publish lowers portable vectors for SSE2 only (the earlier "no x86 tier" assumed SSE4.1), and `PackedSimd` popcount, 16-bit dot product and pairwise widening add beat their portable forms 2.9-4.8x on WebAssembly. Variable shuffle and movemask already lower to the native instruction there and need no exception. Likely users: the conversion in the samplers (phase 1b), mask scoring's popcount, luminance's multiply-add |
| D4 | A 128-bit GFNI syndrome tier | **Decided 2026-09-29: no.** GFNI is x64 only, so it does nothing for WebAssembly, and on x64 it fills the no-AVX cell only on GFNI CPUs: a separate x86-only tier, not an exception inside a portable one. The portable syndrome tier fills that cell on every CPU and on WebAssembly |
| D5 | Where the timing harness lives | **Decided 2026-09-29:** BenchmarkDotNet under `--envVars DOTNET_EnableAVX:0` for JIT ranking (explicit x86 variants only, as phase 1 found), and an opt-in timing mode shared by the two report projects for the real builds (Measuring). They already build every class needed (default NativeAOT on five RIDs, `x86-64-v3`, WebAssembly interpreted and AOT), already see the internals, and already run in CI; a new project would repeat that for two SDKs and add two `InternalsVisibleTo` grants. BenchmarkDotNet 0.15.8 has NativeAOT and WebAssembly toolchains, but the benchmark project references SkiaSharp and other readers, and building it for WebAssembly is unverified. If the cross-language benchmark CLI lands first, its end-to-end numbers replace the timing mode's; kernel timing still needs the internals |

## Progress log

### Phase 1, harness and ranking (2026-09-29)

**Done.** `--time` in both report projects (`tests/FeatherQR.AotAnalysis/TierTiming.cs`, linked into the WebAssembly report):
- 42 end-to-end shapes after the benchmark project's;
- kernels alone: the nine existing WebAssembly tiers beside their scalar forms, and the kernels called once per operation;
- a probe of portable operations against their `PackedSimd` forms;
- `--loop` for a sampling profiler.

Timed on seven builds: JIT with and without AVX; NativeAOT default, `x86-64-v2` and `x86-64-v3`; WebAssembly AOT and interpreted. Tables are in [references/simd-128bit-tiers-measurements.md](references/simd-128bit-tiers-measurements.md). The spec's `Binarizer` sentence is corrected. No `src/` change, so there is no benchmark delta.

**A default NativeAOT publish lowers portable vectors for SSE2.**
- **Why.** ILC's default x64 target is the SSE2 baseline. SSSE3 to SSE4.2 and POPCNT are checked at run time. Explicit intrinsics behind `IsSupported` use them; portable operations cannot.
- **Cost.** Per 4,096 vectors, saturating `Vector128.ConvertToInt32` takes 52 µs, against 3 µs under `x86-64-v2`. A variable `Vector128.Shuffle` takes 43 µs, against 3.
- **The knob is not a stand-in.** `DOTNET_EnableAVX=0` reads the same flags but lowers like `x86-64-v2`.
- **The gap splits in two.** SSE2-only lowering (default against `x86-64-v2`) costs image decode up to 1.44x and matrix decode nothing. Missing 256-bit tiers (`x86-64-v2` against `x86-64-v3`) cost matrix decode 1.3-6.1x, image decode 1.2-4.7x and Standard QR encode up to 2.3x.

**Four existing 128-bit tiers lose to scalar.** They are the QR, Micro QR and rMQR samplers and the rMQR sub-finder lattice.
- **Default NativeAOT:** 3.2-3.9x slower than scalar.
- **WebAssembly AOT:** 1.5-2.4x slower.
- **WebAssembly interpreted:** they win, by 1.3-22x.
- **Cause.** The saturating conversion falls back to software on both AOT builds. `ConvertToInt32Native` is fast on both but is the slowest form interpreted (201 against 110 µs). That makes phase 1b.
- **Interpreted only.** The alignment row mask loses 16 %, on a kernel under 10 µs. The finder row mask loses 2 % on noise.
- The other five tiers win everywhere.

**Ranking on x64 without AVX (default NativeAOT).** Each kernel's share of the shapes where it matters:

| Kernel | Share |
|---|---|
| `ModulePlacerMaskCode` | 84-100 % of a Standard QR encode |
| `EccBinaryDecoder` (syndromes) | 35-83 % of a matrix decode, 82 % of a Structured Append set decode, 12 % of a version 40 image decode |
| `LuminanceConverter` | 29 % and 69 % of the two bitmap decodes |
| `Binarizer` | 26-37 % of a version 40 image decode. 23 % blurred and 2 % on noise, where the 256-bit tier is no faster than scalar either |
| `FinderRowEdges` | The mask walk it would replace is 20-53 % of an image decode on WebAssembly; x64 could not be sampled reliably |
| `RmQRExtractCodewords` | About 21 % of an R17x139 matrix decode |
| `StructuredAppendLanes`, `ModeSegmenterLanes` | Sets on mixed content take 1.23-1.41x longer than with AVX2; plain Byte 1.00 |

Under the bar: `StructuredAppendParity` (about 1 %), `StructuredAppendScanner` (0.2 % or less), `QRSampleGridPiecewise` (never ran).

**Ranking on WebAssembly (AOT, sampled).**

| Kernel | Share |
|---|---|
| syndromes | 13-77 % of a matrix decode |
| `ModulePlacerMaskCode` | 18-63 % of a Standard QR encode |
| `LuminanceConverter` | 27-61 % of a bitmap decode |
| the mask walk | 20-53 % of an image decode |
| `EccBinaryEncoder` | 3-48 % of an encode |
| `MicroQRModulePlacer` | 31-36 % of a Micro QR encode |
| `Binarizer` | up to 33 % of an image decode |
| `RmQRModulePlacer` | 27-30 % of an rMQR encode |
| `RmQRExtractCodewords` | 14-27 % of an rMQR matrix decode |
| `ModeSegmenterLanes` | 13-18 % of a Structured Append Optimal set |
| `TextAnalyzer` | up to 4.9 % |
| the Micro QR writer, `MicroQRByteSegment` included | up to 3.9 % |
| `StructuredAppendLanes` | 2.4 % |

Under the bar: `ModulePlacerExpandBits` (0.9 %), `StructuredAppendParity` (0.8 %), `StructuredAppendScanner` (0.1 %). Not seen at all: `ModuleBitPacker` and `RmQRValueSegments`, which are inlined into their callers, and `QRSampleGridPiecewise`. The interpreted build is 2.6-11x slower than AOT and cannot be split by kernel.

**D3 probe.** On WebAssembly AOT, the three `PackedSimd` operations beat their portable forms by 2.9-4.8x (D3). Variable shuffle and movemask already lower to the native instructions.

**Lessons.**
- A tier's flags do not say how its portable operations are lowered. Only a timing on the build does.
- The .NET sampler on Windows (`dotnet-sampled-thread-time`) stops threads at GC-safe points. On a 2 µs shape, 94 % of samples landed in a GC poll frame. V8's sampler on WebAssembly AOT is exact. On x64, kernel-alone timings and single-kernel knobs gave the shares.
- `DOTNET_EnableBMI2=0` does not move the PEXT tiers in .NET 10.
- WebAssembly AOT still interprets part of the library (generic value-type sharing, e.g. `QRCodeConstants.GetEccInfo`). That is a third of a version 40 encode, and not a SIMD matter.

**Plan changes.**
- The x64 measuring rule is corrected.
- D2 is reopened.
- D3 is measured, with a recommendation.
- Phase 1b is added for the four losing tiers.

### Phase 1b, tiers that lose (2026-09-29)

**Done.**
- **`VectorCast.ToInt32`** (`ImageDecoders/VectorCast.Simd.cs`, replaced in the follow-up below) converts the four tiers' coordinates. x64: `cvttps2dq` with the positive overflow flipped to `int.MaxValue`, all SSE2, so a default NativeAOT publish keeps it. WebAssembly: `PackedSimd.ConvertToInt32Saturate`, fed only values it converts exactly (`pmax` with 0, `pmin` with the largest float under 2^31). ARM64: `Vector128.ConvertToInt32` as before.
- **No tier depends on the runtime's cast outside the int range any more.** The scalar samplers and the vector tiers' scalar tails take the far edge before converting (`PixelIndex.Clamp`); the sub-finder lattice skips a NaN point in both tiers.
- **Table:** a `PackedSimd` tier. The four kernels take `Sse2` on x64 and `PackedSimd` on WebAssembly; ARM64 keeps `Vector128`. The alignment row mask keeps its tier, with the reason beside its row.
- **`--parity`** in both report projects: the conversion and the four kernels against their scalar forms on random scenes, NaN and out-of-range coordinates included. CI runs it on every NativeAOT leg and both WebAssembly modes.
- **Tests:** `VectorCastParityTest`, and `PixelCoordinateEdgeTest` (each coordinate class through every tier of the three samplers, and the lattice at NaN). The layout test knows the WebAssembly family. Planted faults (the overflow flip removed, the NaN step removed while it existed) failed the parity test.

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-1b-the-samplers-and-the-lattice-after-the-fix)):

| Build | The four tiers, old → new | Tier against scalar after | Image decode, old → new |
|---|---|---|---|
| NativeAOT default | 7-13x faster | 1.6-3.3x faster | 3-31 % faster |
| WebAssembly AOT | 5.5-8x faster | 2.5-5x faster | 6-24 % faster |
| WebAssembly interpreted | 6-34 % slower (the lattice, with four conversions a step, most) | 1.5-13x faster | within noise |
| JIT, with and without AVX | within noise | unchanged | within noise |

BenchmarkDotNet under the JIT (the three image decode classes, ShortRun, old and new alternated): every row inside the spread between runs of one build, which reached 195 to 310 µs on `R17x139_Keystone15_Span`; allocations unchanged. The interpreter pays for the two guards on the conversion (122 against 105 µs per 4,096 vectors) and 4 % more for the cap constant the helper builds on every call (first read as a call it only partly inlines; the follow-up found the constant). The tiers still win there, so D1 holds. On WebAssembly AOT the samplers are up to 11 % of an image decode.

**Machine code.** Every method has as many instructions as before or fewer on ILC x64 (default and `x86-64-v3`) and the JIT. On ARM64 the vector tiers are within two instructions (Standard QR's +2, which the follow-up removes), and the scalar samplers gain two to four instructions before the loop (the limits converted to float); their loop bodies are the same length. `VectorCast` and `PixelIndex` are inlined wherever they were checked.

**Found on the way.**
- **The WebAssembly interpreter's cast writes `int.MinValue` for NaN and past 2^31; AOT-compiled code saturates; an AOT build interprets some methods.** So the scalar tiers' output past the image depended on how they ran, and the scalar sub-finder lattice, reading a NaN point at `int.MinValue`, threw `IndexOutOfRangeException` under the interpreter. A decode never reached that: `TryClassifySubFinderLattice` refuses NaN and infinite parameters first, and only the direct entry the parity check calls took them. CoreCLR from .NET 9 on behaves as before. .NET 8 on x64 and the interpreter now take the far edge for coordinates past 2^31, as CoreCLR does, and a NaN lattice point is skipped, not read.
- **An AOT-compiled WebAssembly caller got `int.MinValue` from `PackedSimd.ConvertToInt32Saturate` past 2^31.** A standalone check did; the kernels did not. So the conversion is only given values it converts exactly.
- **WebAssembly's `f32x4.min` costs several instructions in V8.** `pmin` is one; with NaN sent to 0 first, it is what the guard needs.
- **ILC retargeted to ARM64 with x64 references compiles x86 intrinsics as calls.** A codegen comparison needs the ARM64 runtime pack as references; `--parallelism:1` keeps ILC's listings from interleaving.

**Lessons.**
- The parity tests run under the JIT on x64 and ARM64. The WebAssembly semantics and the lattice crash were found only by running the same comparison on the build.
- Measure a helper's cost on every build. It was free under the JIT and ILC; interpreted, the constant it built cost 4 %.
- On this machine, runs move by up to 40 % between speed states. Compare old and new alternately, and read pairs from the same state before trusting a normalized ratio.

### Phase 1b follow-up, the interpreter (2026-09-29)

**Question.** After phase 1b the four tiers ran 6-34 % slower interpreted, and the shared `VectorCast` seemed to cost 4 % of that. Can the code stay shared at no cost?

**Done.**
- **The clamp comes before the conversion** (`VectorCast.ToPixel(coordinate, last)`). The samplers clamped right after converting, so the conversion's guards were a second clamp. One clamp in front leaves the conversion only values it takes exactly.
  - x64: `maxps`, `minps`, `cvttps2dq`.
  - WebAssembly and ARM64: a min with the last pixel, then the unsigned saturating conversion, which sends NaN and the near side to 0 itself (`pmin` + `i32x4.trunc_sat_f32x4_u`, `fmin` + `fcvtzu`). No zero constant.
- **The lattice converts plainly** (`VectorCast.ToInt32Native`). It reads only lanes inside the image, so it needs no guard.
- `ToPixel` now equals `PixelIndex.Clamp` for every float, in `VectorCastParityTest` and `--parity`. The old contract allowed a range.
- Harness: the `pixel-*` and `clamp-*` probes.

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-1b-follow-up-the-interpreter)). Interpreted, from one speed state, five alternations:

| Kernel, µs | Before 1b | 1b | Now |
|---|---|---|---|
| QR sampler | 240.8-243.3 | 257.3-259.8 | 236.1-238.7 |
| Micro QR sampler | 2.29-2.30 | 2.73-2.77 | 2.32-2.34 |
| rMQR sampler | 10.79-10.90 | 11.86-11.95 | 10.26-10.37 |
| rMQR lattice | 1.29-1.30 | 1.66-1.68 | 1.29-1.31 |

The four tiers are 11-15 % faster than 1b on default NativeAOT and 1-14 % on WebAssembly AOT. Image decodes: default NativeAOT within noise of 1b; WebAssembly AOT at most 1.5 % slower (version 40, where the unsigned conversion costs the sampler 1 %); interpreted within noise of before 1b.

**Machine code.** Every vector kernel has fewer instructions than in 1b on every build: ILC x64 -16 to -68, `x86-64-v3` -6 to -15, ARM64 -2 to -5 (the lattice 0), JIT -7 to -33. ARM64 is now at or below its count before 1b. The scalar kernels are unchanged.

**Found on the way.**
- **Sharing costs the interpreter nothing.** It inlines `VectorCast` and `PixelIndex`; its compiled code has no call. Through the helper and written inline time the same, the helper no slower. 1b's 4 % was the cap `Vector128.Create(2147483520f)` inside the helper, rebuilt on every call: the interpreter hoists nothing out of a loop. Micro QR's four-lane core took 17 interpreter ops before 1b, 27 in 1b and 13 now.
- **The WebAssembly runtime compiles hot interpreter code in traces** (the jiterpreter). A call ends a trace, and branch layout decides where the next one starts. A scalar clamp form moved QR's vector tier by 7 % through its one-module row tail, and rMQR's scalar sampler by 35 %, far more than their op counts explain.
- **Scalar clamp forms tried:** 1b's float-first form, one unsigned test, and the old integer tests with a sign check. On default NativeAOT the unsigned test is about 20 % faster in the scalar samplers, but interpreted it slowed QR's vector tier 7 %. The scalar samplers run only as one-module tails on x64 and ARM64, so 1b's form stays.
- **An overlapping last vector step for Micro QR**, as rMQR does: 2.88 against 2.48 µs interpreted. A vector step costs about three scalar modules there. Rejected.
- **Unsigned against signed conversion on WebAssembly:** interpreted 5-8 % faster (Micro QR, rMQR), AOT 1-3 % slower. Unsigned, by D1's reasoning.
- Micro QR's remaining 1-2 % interpreted lies outside its vector core, which now takes fewer ops than before 1b; the scalar row tail is the other code that changed.

**Lessons.**
- Judge the interpreter per kernel, not by op counts or probes. `MONO_VERBOSE_METHOD` prints the interpreter's code and the jiterpreter's traces; read both before trusting a probe.
- A constant built inside a helper costs the interpreter on every call. The caller hoists it.
- A guard next to a clamp belongs in the clamp.

### Phase 2, near ports (2026-09-30)

**Done.**
- **`FinderRowEdges` runs on every 128-bit target.** x64 without AVX and WebAssembly had kept the mask walk. The row's word there is four 16-byte compares with a movemask each; the rest is the ARM64 form. Cells: `Vector128` on x64 without AVX and on WebAssembly.
- **WebAssembly steps, gated on `PackedSimd`** (the rule above):
  - `TextAnalyzer`: a portable tier, 16 chars a step (`TextAnalyzer.Vector128.cs`). A block with a char above U+00FF settles every flag, so where the tier narrows to bytes the narrowing is exact. The alphanumeric set is four ranges and a space.
  - `ModuleBitPacker`: pack and unpack, 16 modules a step, WebAssembly's swizzle for the byte shuffle.
  - `RmQRModulePlacer`: the masked expand, 16 modules a step, the swizzle and a min with 1 for the compare and AND.
- **Left scalar, the measured reason beside each row:** `MicroQRModulePlacer` and `MicroQRByteSegment` on WebAssembly, `ModulePlacerExpandBits` on WebAssembly, `StructuredAppendParity` and `StructuredAppendScanner` on x64 and WebAssembly.
- **`--parity`** holds the edge list, the rMQR placer, the bit packer and the text analysis to their scalar forms. CI's JIT run under `DOTNET_EnableAVX=0` (and `DOTNET_EnableArm64Dp=0`) passes `--parity` too, since the test suite runs only the side of a class the runner's CPU picks.
- **Tests:** `TextAnalyzerVector128ParityTest` enters the tier directly on every 128-bit machine, with chars above U+00FF whose low byte is a digit or alphanumeric. The finder tests pass with AVX off, where they used to skip.
- **Planted faults**, each caught: a shift in the edge list's movemask word (unit tests and `--parity`); the settle check, the `-` to `:` range and the digit bound of the text analysis (unit test); a bit weight in the rMQR expand, the pack reversal and the unpack's min (`--parity`, WebAssembly).
- Harness: `kernel/` shapes for each kernel beside its scalar path, `data/` shapes for the data-object API, `probe/expand-*`.

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-2-near-ports)):

| Kernel | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| `FinderRowEdges` against the mask walk, v40 3 px / noise | 0.43 / 0.54 | 0.25 / 0.49 | 0.28 / 0.47 |
| `TextAnalyzer`, 2,900 chars, Byte / digits | x64 tier unchanged | 12x / 10x faster | 7.6x / 2.7x faster |
| `ModuleBitPacker`, R17x139, pack / unpack | x64 tier unchanged | 2.2x / 3.8x faster | 2.3x / 4.1x faster |
| `RmQRModulePlacer`, R17x139 | x64 tier unchanged | 0.93 | 0.96 |

Image decode with the edge list: 0.58 to 0.96 on default NativeAOT, 0.36 to 0.80 on WebAssembly AOT, 0.38 to 0.94 interpreted. The data-object API, where the bit packer runs: rMQR R17x139 decode 0.93 and encode 0.97 interpreted, Micro QR M4 decode 0.94. The whole-phase run against the build before phase 2 was taken while this machine swung between speed states, runs of one build spreading 30 to 50 % on shapes whose code did not change, so it reads only the image rows. They agree with the edge list's run above: version 40 at 3 px, 666-671 → 392-501 µs on default NativeAOT, 692-832 → 451-515 on WebAssembly AOT, 3,147-4,704 → 1,702-2,155 interpreted. The text analysis and the rMQR placer can move their encodes by at most their shares, under 5 %, inside that spread; their evidence is the kernel runs.

**Machine code.** Every cell that stays compiles as before: `Analyze`, `Pack`, `Unpack`, the rMQR `PlaceSymbol` and its expand, and the finder's row methods are identical instruction for instruction on the JIT with and without AVX and ILC for default x64, `x86-64-v3` and ARM64. ILC ARM64's `RentEdgeBuffer` is 2 instructions shorter, its gate now one constant flag. On the changed cells, default NativeAOT's `ExtractRowEdges` went from 303 to 290 instructions: the NEON fold it compiled there, never run, became the movemask.

**Found on the way.**
- **A `Vector128` gate after an SSSE3 step compiles into a default NativeAOT publish.** SSSE3 is a run-time check there, so the step would sit in x64 code that never runs it. Hence the `PackedSimd` gate.
- **The Micro QR placer's vector unpack loses interpreted, though its expand wins alone.** The expand is 2.6x faster than the SWAR spread in a probe, and the jiterpreter traces both placers whole, yet the vector core took 1.58 to 1.74 µs against 1.43. The cause was not found; the kernel stays scalar there.
- **The harness's encode and matrix shapes never call the bit packer.** They use the span API; only the data-object API packs and unpacks. A 4x kernel showed nothing end to end until the `data/` shapes were added.
- **WebAssembly's swizzle beats the portable constant shuffle interpreted** (21.6-22.2 against 24.5-24.6 µs a probe) and ties AOT, so the WebAssembly-only steps use it.
- **Restoring a planted fault with `mv` kept the faulted binary again**, the trap the decoder spec's lessons already name; `cp` or a `touch` after the move.

**Lessons.**
- Take a kernel's share on the entry that runs it. The span API and the data-object API run different code.
- On the interpreter a probe that wins can still lose inside the kernel. Ship on the kernel and end to end.

### Phase 3, image decode (2026-09-30)

**Done.**
- **`LuminanceConverter` on every vector target without its AVX2 or dot-product tier** (`LuminanceConverter.Simd.cs`): x64 without AVX2, WebAssembly, ARM64 without `Dp`. A pixel is a 32-bit lane: R and B share one lane's 16-bit halves, G has its own, and a 16-bit multiply-add weights them. The multiply-add and the narrowing are SSE2's `pmaddwd` and packs on x64, `PackedSimd` on WebAssembly, portable elsewhere. Straight alpha takes the ARM64 tier's row modes; on WebAssembly a partially transparent block takes the per-pixel formula, and a row in the composite mode the scalar loop. Cells: `Sse2` on x64 without AVX2, `PackedSimd` on WebAssembly, `Vector128` on ARM64 without the dot product.
- **`Binarizer`**: the 256-bit tier's 32-pixel blocks on two 128-bit loads (`Binarizer.Vector128.cs`). Cells: `Vector128` on x64 without AVX and on WebAssembly.
- **`QRSampleGridPiecewise`**: the ARM64 tier's four-lane step on portable vectors, each coordinate to its pixel through `VectorCast.ToPixel` (`QRImageDecoder.PiecewiseSampling.Vector128.cs`). Cells: `Sse2` and `PackedSimd`, the conversion's reads. Measured before the port: 9.8 % and 11.4 % of a bowed version 25 and 40 decode on WebAssembly AOT, where only the mesh reads the symbol.
- **One clamp for the mesh sampler on every runtime.** The reference's `(int)` cast differed between .NET 8 and 9 on x64, and between interpreted and AOT-compiled WebAssembly. The reference and the tiers' row tails now take `PixelIndex.Clamp`, and the AVX2 tier drops its .NET 8 form. The ARM64 tail keeps the plain cast, which already lands there.
- **`--parity`** holds the histogram, the luminance (every channel against every alpha, straight and premultiplied; the row modes across widths and padding) and the mesh sampler (Annex E lattices of versions 7 to 40, upright, bent and pushed past an edge; NaN, infinite and out-of-range nodes) to scalar.
- **Tests:** `LuminanceConverterVector128ParityTest` enters the new tier directly on every 128-bit machine, `OtsuHistogramParityTest` and `SampleGridPiecewiseParityTest` too.
- **Planted faults**, each caught by the unit tests and by `--parity`: the histogram's high-half mask, the premultiplied white term, the composite's ceiling division, the mesh index's row limit.
- Harness: `kernel/` shapes beside each scalar entry, two bowed image shapes, a gradient.

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-3-image-decode)), each shape in its own process:

| Kernel against scalar | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| Histogram, rendered v40 at 3 px / rotated / gradient | 0.11 / 0.48 / 0.93 | 0.09 / 0.31 / 1.11 | 0.16 / 0.51 / 0.99 |
| Histogram, soft / noise | 0.99 / 1.01 | 1.04 / 1.07 | 0.99 / 0.99 |
| Luminance, opaque / straight alpha | 0.21 / 0.41 | 0.62 / 1.00 | 0.54 / 1.01 |
| Mesh sampler, v40 at 3 px | 0.50 | 0.30 | 0.41 |

Image decode against the build before phase 3: 0.54 to 0.96 on default NativeAOT, 0.53 to 0.78 on WebAssembly AOT, 0.65 to 0.93 interpreted on every symbol shape; bitmaps 0.33 to 0.42, 0.61, 0.56 to 0.70. The soft render, noise and the gradient read 0.99 to 1.02 on every build, inside their runs' spread.

**Decided.** WebAssembly keeps the histogram tier. AOT-compiled it counts dense input 4 to 11 % slower than scalar, up to 2 % of a decode and inside the runs' spread, against 0.09 to 0.31 on rendered symbols; interpreted it is level on that input. The reason sits beside the table row. Two ways to move the dense walk out of the vector method lost (measurements).

**Machine code.**
- Cells that stay compile as before, line for line: ILC `x86-64-v3` (`Convert`, `ConvertRgba`, `FillHistogram`, `ComputeOtsuThreshold`, `DecodeThroughMesh`, `SampleGridPiecewise`), the .NET 10 JIT with AVX2 (`Convert`, `FillHistogram`, `DecodeThroughMesh`), ILC ARM64 (`FillHistogram`, `SampleGridPiecewise`, `DecodeThroughMesh`, `SampleGridPiecewiseAdvSimd`). On the .NET 8 JIT the histogram methods and the scalar conversion keep their sizes.
- The AVX2 mesh tier: 593 → 587 instructions on ILC `x86-64-v3`, 565 → 552 on the .NET 10 JIT, 566 → 580 on .NET 8. Its loop body is the same length there; the rest is the float limits before the loop and the tail's clamp, which is now the reference's.
- The column table is shorter on x64 (ILC 358 → 352 and 354 → 350, the .NET 10 JIT 350 → 346) and 2 longer on ARM64 and 1 on .NET 8, its limits converted before the loop. The reference gains 0 to 4 (ILC x64 294 → 294, `x86-64-v3` 290 → 292, ARM64 238 → 240, .NET 10 JIT 288 → 290, .NET 8 235 → 239): before the loop on ARM64 and .NET 8, inside it on `x86-64-v3`, where hoisting the limits by hand made the method longer (292 → 298, ILC x64 294 → 299), so it stays. The reference runs as the fallback for a mesh the tiers' tables cannot hold, and its clamp is the definition every tier matches.
- ILC ARM64 `ConvertRgba`: the dot-product test moved from `IsVectorTierTaken` into the dispatch, and the 128-bit entry is inlined behind it (105 → 156 instructions); a core with the dot product runs the same tests as before.

**Found on the way.**
- **A branch that folds away can still stop an inline.** With the 128-bit route added, the JIT stopped inlining `ConvertRgba` into `Convert` under AVX2 (`Convert` 318 → 269 instructions plus a call), though the new branch folds away there: the inliner judges the IL before it folds. The choice between NEON and 128-bit now sits one call down, and `Convert` is identical again.
- **ILC ARM64 left `PixelIndex.Clamp`'s limit conversion inside two loops**, the ARM64 tier's tail and the column table, while the reference's loop hoisted it. The ARM64 tail keeps its plain cast, and the column table converts its limits once, through a `PixelIndex.Clamp` overload that takes the float limit.
- **The interpreter carries state between shapes.** In one process, an unchanged kernel ran 1.8x slower after the base build's image shapes than after the new build's; alone, both timed the same. A shape to a process since.
- **WebAssembly runs the luminance tier at a third of x64's gain**: opaque 0.62 on WebAssembly AOT against 0.21 on default NativeAOT, with the same operations.
- Another Claude session's test runs loaded this machine during the variant measurements (60 % when checked); the variants' refusals rest on differences that held across alternations.

**Lessons.**
- After adding a tier, read the disassembly of the dispatch's callers, not only of the dispatch.
- On the interpreter, time a shape alone in its process; what ran before it changes its time.

### Phase 4, encode lanes and mask scoring (2026-09-30)

**Done.**
- **`ModulePlacerMaskCode`, versions 1-11** (`ModulePlacer.Masking.Simd.cs`): the AVX2 tier's lane-per-pattern scorer with two candidates a vector, four groups a call, its checkpoint included (column rule 3 skipped when both candidates are already past the best total). Popcounts go to 16-bit accumulators, reduced once a group: WebAssembly's byte popcount and pairwise widening add, the SSSE3 nibble table and psadbw on x64, a SWAR count of each 16-bit lane elsewhere. The 64-bit lane shifts are `PackedSimd`'s on WebAssembly. The per-version rows are built by one function the AVX2 tier now shares. Cells: `Ssse3` on x64 without AVX2, `PackedSimd` on WebAssembly.
- **Versions 12-40 stay scalar.** The AVX2 tier's two- and three-word SoA scorers were ported to two rows a vector and lost on both targets: 0.94 to 1.68 of scalar on default NativeAOT, 1.40 to 5.80 on WebAssembly.
- **`ModeSegmenterLanes`** (`ModeSegmenter.Lanes.Simd.cs`): the ARM64 tier's four-lane groups on portable vectors, the class looked up per lane in scalar code, the parent entries narrowed by SSE2's packssdw or WebAssembly's i16x8.narrow_i32x4_s into one 8-byte store. `LanesAccelerated` is now `Vector128.IsHardwareAccelerated`, so the generator plans a set's symbols together on these builds too. Cells: `Sse2`, `PackedSimd`.
- **`StructuredAppendLanes`** (`StructuredAppendPlanner.Lanes.Simd.cs`): the NEON walk's eight saturating 16-bit lanes on portable vectors, the saturating add SSE2's paddusw or WebAssembly's i16x8.add_sat_u. It takes chunks averaging 40 characters or more on x64 without AVX and 80 on WebAssembly, measured per build (below). The NEON walk's two vector-building helpers moved to the new file under neutral names. Cells: `Sse2`, `PackedSimd`.
- **`--parity`** holds the mask selection (every one-word version and two larger, every ECC level, random, all-light and all-dark data), the segmenter's lanes (cost, final state and walk-back of each lane against the per-piece program) and the walks (chunk counts and ends against the scalar walk) to scalar.
- **Tests:** `ModulePlacerMaskVector128ParityTest` and `StructuredAppendVector128ParityTest` enter the new tiers directly; `ModeSegmenterLaneParityTest` runs every case through the 128-bit entry too.
- **Planted faults**, each caught: the WebAssembly popcount (`--parity`, 52 mismatches), the checkpoint's both-candidates condition (tests, 11 failures), the SWAR popcount (.NET 8 with SSSE3 off, 8), an alphanumeric step cost in the segmenter groups (tests), the walk's saturating add (tests, 9 of 14).
- Harness: scalar mask shapes, the planner alone, chunk-length probes, and an exact-name `--shape` filter (`name$`).

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-4-encode-lanes-and-mask-scoring)), each shape in its own process:

| Against scalar | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| Mask selection, versions 1 / 6 / 10 | 0.59 / 0.63 / 0.59 | 0.49 / 0.49 / 0.46 | 0.70 / 0.71 / 0.69 |
| The planner, mixed 40k / UTF-8 15k | 0.53 / 0.31 | 0.69 / 0.61 | 0.75 / 0.26 |

Encode against the build before phase 4: version 1 0.61 to 0.68 on default NativeAOT, 0.67 to 0.69 on WebAssembly AOT, 0.76 to 0.80 interpreted; version 6 0.69, 0.83, 0.94; versions 20 and 40 0.97 to 1.02. Optimal sets 0.76 to 0.83, 0.91 to 0.92, 0.70 to 0.95. Single sets and the Micro QR and rMQR encodes, whose code did not change, 0.99 to 1.01, but for one default NativeAOT rMQR row at 0.90 on 1.2 µs.

**Machine code.** Cells that stay compile as before: the .NET 8 and .NET 10 JIT with AVX2 line for line (the mask kernels, `WriteQRMatrix`, `CreateStructuredAppend`, `TryNarrowWithLanes`, `ComputeCostsLanes` inlined where it was), ILC `x86-64-v3` and ARM64 instruction for instruction, apart from displacements: static vector fields moved when `ModulePlacer` gained statics, and the 32-bit walk's shared generic body, which never runs (its two instantiations are structs), reads a dictionary slot at a new offset. ILC ARM64's `CreateStructuredAppend` is 12 instructions shorter: `LanesAccelerated` read `Vector256.IsHardwareAccelerated`, which ILC ARM64 left as a call, and `Vector128.IsHardwareAccelerated` folds.

**Found on the way.**
- **Phase 1's share of the walks was wrong.** Sampled, the walks were 2.4 % of an Optimal set on WebAssembly AOT and the segmenter 13-18 %; timed alone, the planner is 30 % of that set on default NativeAOT, 23 % on WebAssembly AOT and 17 to 49 % interpreted, and the per-symbol plans the segmenter's lanes serve moved the set 1 to 3 %. The scalar walks run in the segmenter's `LongestPrefixWithinBudget` and `ComputeCosts`, so a per-function sample counted them under the segmenter.
- **Mono's WebAssembly AOT compiles `Vector128.ShiftRightLogical` and `ShiftLeft` on 64-bit lanes as calls into corlib's software fallback.** The mask tier ran 8x slower than scalar there, slower than the interpreter, until its shifts went through `PackedSimd`.
- **SoA lane-per-row loses at 128 bits**, on x64 without AVX and on WebAssembly alike; at `x86-64-v3` the AVX2 SoA tier already did not beat scalar at version 40 (phase 1).
- **The interpreter needs longer chunks than AOT-compiled code before the walk pays** (80 characters against 32), and one flag gates both WebAssembly builds, so WebAssembly takes the interpreter's threshold.
- **Adding a branch to a dispatch changed its callers' code on builds whose cell did not change, five times.** The JIT with AVX2 stopped inlining `ComputeCostsLanes` and `WalkLanes` into their callers when each gained a block. ILC `x86-64-v3` called a property that read `Vector256.IsHardwareAccelerated` until it was `AggressiveInlining`, and stopped inlining `WalkLanes` into `TryNarrowWithLanes` with the condition written inline too, so the 128-bit branch moved into the 32-bit walk the dispatch falls back to. A three-flag `LanesAccelerated` changed register allocation in `CreateStructuredAppend` until it was one flag.
- **`DOTNET_EnableSSSE3=0` reaches only the .NET 8 JIT.** The first no-SSSE3 test run passed without running the SWAR branch; a planted fault showed which runs reached it.

**Lessons.**
- Rank a stage by timing it alone; a sampled share attributes a callee's time to the callee, not to the stage it serves.
- For every new WebAssembly kernel, read the AOT profile for corlib calls: a portable operation can be a software fallback at one lane width only.
- Check a dispatch change by its callers' disassembly on every unchanged build: the JIT and ILC inline differently, and a fix for one can leave the other changed.

### Phase 5, GF(256) and bit planes (2026-10-01)

**Done.**
- **`EccBinaryDecoder`, the syndrome pass** (`EccBinaryDecoder.Vector128.cs`): the ARM64 kernel's structure on portable vectors (two 16-lane accumulator groups, its step tables for the data terms, eight bytes a step as a tree). Its one multiply with no portable instruction, `PMULL` by each lane's power of α, is done by linearity: a·K is the XOR of K·x^b over the set bits b of a, so a multiply is eight rounds of a sign-bit mask, an AND with a plane of K·x^b and an XOR, the next bit brought up by a + a, with no reduction. The planes take 768 bytes, built on first use. Cells: `Vector128` on x64 without the GFNI tier (no 256-bit GFNI, or .NET 8, which has no GFNI API) and on WebAssembly.
- **`EccBinaryEncoder` on WebAssembly** (`EccBinaryEncoder.Wasm.cs`): the NEON kernel with WebAssembly's swizzle for `TBL` (both zero a lane indexed past 15). The register's byte shift is a swizzle by constant indices, and two swizzles and an OR across the halves of a 32-byte register. Its entry sends blocks under 90 data bytes × ECC codewords to the scalar kernel: under that the interpreter's setup costs more than the division. Cell: `PackedSimd`.
- **`RmQRExtractCodewords`** (`RmQRMatrixDecoder.Vector128.cs`): the ARM64 pair planes on portable vectors, with NEON's three instructions replaced. A pair's two columns are adjacent bytes, so one 16-bit lane holds both and `x | x >> 7` merges them where NEON unzips, and the lanes stay 16-bit, so nothing widens into the accumulator. The row insert is a shift and an OR (`SLI`), the row-reversed word four swap steps (`RBIT`, `REV32`), once a block. x64 runs it on every symbol; WebAssembly from 44 stream bits per eight columns, the interpreter's break-even. Cells: `Vector128` on x64 without fast PEXT, `PackedSimd` on WebAssembly (the gate reads the flag). The tier's flag is false on ARM64, whose own pair planes run first, so its dispatch drops the branch.
- **`--parity`** holds the syndromes (random blocks and fills, every ECC count), the encoder's kernel and its gated entry (lengths 0 to 160, every ECC count to 32), and the extraction (every version, pinned and through the dispatch, four grids) to scalar.
- **Tests:** `EccBinaryDecoderKernelParityTest` enters the syndrome tier directly and pins its store width; `EccBinaryEncoderKernelParityTest` has the WebAssembly kernel (skipped elsewhere, like the NEON one on x64); `RmQRExtractCodewordsParityTest` runs the 128-bit pair planes pinned on every x64 machine, the overread check included.
- **Planted faults**, each caught: a dropped plane in the syndrome multiply (tests; `--parity` 6,379 mismatches), an index of the encoder's cross-half shift (`--parity`, 1,050) and of its one-byte shift (`--parity`, 138), a dropped byte swap in the row reversal and the pair merge's mask (tests 64 each; `--parity` 156 each).
- Harness: `kernel/EccSyndromes-*`, `kernel/EccEncode-*` and `kernel/RmQRExtract-*` beside their scalar paths; `probe/ecc-encode-*` (the kernel past its gate, 30 block shapes) and `probe/rmqr-extract-*` (every version).

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-5-gf256-and-bit-planes)), each shape in its own process:

| Against scalar | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| Syndromes, 148 / 45 / 24 bytes | 0.06 / 0.07 / 0.18 | 0.06 / 0.08 / 0.23 | 0.14 / 0.16 / 0.36 |
| Encoder, 118 / 15 / 16 data bytes (30 / 30 / 8 ECC) | x64 tier unchanged | 0.12 / 0.23 / 0.54 | 0.11 / 0.34 / 0.64 |
| Extraction, R17x139 / R13x77 / R7x43 | 0.20 / 0.27 / 0.72 | 0.41 / 0.56 / walk | 0.55 / 0.72 / walk |

Matrix decode against the build before phase 5: version 40 0.21-0.22 on default NativeAOT, 0.25-0.32 on WebAssembly AOT, 0.33-0.34 interpreted; version 6 0.35, 0.54, 0.58; rMQR R17x139 0.24, 0.28, 0.49 (corrected 0.34, 0.41, 0.52); the Structured Append set 0.21, 0.31, 0.35; Micro QR M4 0.65, 0.67, 0.82; the smallest symbols 0.68 to 0.96. The version 40 image 0.64, 0.60, 0.80, the rMQR image 0.85 to 0.95. WebAssembly encodes 0.53 to 1.00 where the encoder's kernel runs (rMQR R17x139 0.58 AOT, 0.53 interpreted; version 40-L 0.89, 0.77), and 0.99 to 1.01 on blocks under its gate over repeated runs (Micro QR M2 and M3, rMQR R7x43). Default NativeAOT encodes, whose code did not change, 0.97 to 1.05.

**Machine code.** `EccBinaryEncoder`'s cells all stay on x64 and ARM64: `CalculateECC` and its callers compile as before on ILC for default x64, `x86-64-v3` and ARM64, and on the .NET 8 and .NET 10 JIT apart from the string tokens of the throw paths, which moved as the assembly gained literals. The syndrome pass: `TryCorrect` and the matrix decoders that call it are unchanged but for those tokens; the pass's fallback call moved from the scalar pass to the 128-bit one where the cell changed, ILC `x86-64-v3` on a CPU without GFNI and the .NET 8 JIT. The extraction: on ILC ARM64 the dispatch runs the same instructions but for one compare's immediate (the pinned-kernel range) and the new flag's name in the throw path; `BuildExtractLayout`, run once a version, is one instruction shorter with its registers allocated differently. With AVX2 the cell changed (CPUs without fast PEXT), and the bit-plane path through the dispatch runs about five more instructions, once a decode. `DecodeMatrix` and the other callers are identical everywhere.

**Found on the way.**
- **A span's length read in a dispatch changed the JIT's x64 code under a branch that folds away.** The encoder's size gate, first written `PackedSimd.IsSupported && data.Length * eccCount >= 90` in `CalculateECC`, made the .NET 10 JIT copy `data` to the stack before each GFNI call, 12 instructions longer, though the flag is a constant false there: the JIT marks an argument whose address the IL takes (`ldarga`) before it drops unreachable code. The gate moved into the WebAssembly entry, made `AggressiveInlining` so the interpreter does not pay a call for it (Micro QR M2 encode 1.03 out of line, 1.01 inlined).
- **ILC ARM64 kept `IsPairPlaneVector128TierSupported` a call, and with it the dead branch,** until the property was `AggressiveInlining`: phase 4's trap, on a property reading `Vector128.IsHardwareAccelerated` and another flag.
- **Taking the new branch out of line traded x64 instructions for an interpreted call.** A `NoInlining` helper held the x64 bit-plane path to about two more instructions, against five inline, and cost the interpreter 0.13 µs a call, 6 % of the R17x139 kernel; the branch stays inline, and ARM64 drops it by its flag instead.
- **The pair planes pay by the block, the walk by the bit.** On WebAssembly the sparse symbols lose: R7x43 1.45 AOT and 1.47 interpreted, R11x27 1.08 and 1.22. The interpreter's break-even is 44 stream bits per eight columns, AOT's between 27 and 33; on default NativeAOT every version wins (0.21 to 0.71).
- **The encoder's kernel costs 0.35 µs interpreted before its first step, against 0.04 AOT-compiled**, so the interpreter loses on small blocks the AOT-compiled build wins (3 × 5: 2.05 against 0.98). Swizzle indices loaded from constant data instead of `Vector128.Create` ran slower interpreted.
- **The first WebAssembly gate for the extraction sat in the layout builder,** so a pinned call on a gated version built the layout on every call (16 to 50 times the walk in the harness). The gate is in the dispatch, and the layout holds the pair planes wherever a pair-plane tier can run.
- **.NET 8 had no syndrome tier on x64.** GFNI is a .NET 10 API, so the .NET 8 JIT ran the scalar pass even with AVX2; it now runs the 128-bit one.

**Lessons.**
- A branch that folds away still changes its method when its operands take an argument's address: keep a new condition's operands out of a dispatch that unchanged builds share, one call down.
- Put a size gate in the tier's entry and point the parity check and the probe at the kernel behind it, or the kernel's small inputs go unchecked and unmeasured.
- Every flag property a dispatch reads is `AggressiveInlining`, whichever flag it reads.

### Phase 6, rMQR value writers (2026-10-01)

**Done.**
- **Shares measured.** The Numeric and Alphanumeric writers are inlined into the encode, so phase 1's sampler never saw them; timed alone, they are 2.5 to 6.8 % of an rMQR encode on WebAssembly and 3.8 to 6.1 % on default NativeAOT, which already runs their SSE4.1 tier. On WebAssembly that clears the bar.
- **WebAssembly steps written, measured and left out**; `RmQRValueSegments` stays scalar there, the reason beside its row. A 16-character Alphanumeric step (the value table's rows by swizzle, 45·a + b in 16-bit lanes, two pairs a lane by the 16-bit dot product, two 44-bit appends) ran 0.62 to 0.86 of the table loop alone, which bounds its gain at 2.5 % of an encode. Inlined into the writer it slowed the Numeric encode 6 % on WebAssembly AOT; out of line, with the writer's state passed in and returned, the call took the gain; as an encode method of its own it moved the encodes 0.97 to 1.02, inside the runs' spread. A 24-digit Numeric step (the three digits of eight groups gathered by swizzles of two byte vectors, multiplied and added in 16-bit lanes) tied AOT-compiled (0.92) and ran 1.26x the SWAR loop interpreted.
- **ARM64 not measured.** Its cell keeps the rMQR encoder record's reason: an Alphanumeric batch won alone and lost end to end, to the same switch.
- **`--parity`** holds the writers, the tier each build takes against the SWAR and table loops, from every pending-bit count a header leaves. On default NativeAOT that is the SSE4.1 tier, which the test suite reaches only on its own CPU; planted faults in its letter offset and its Numeric pair weight gave 14,872 mismatches.
- Harness: `kernel/RmQRNumeric-*` and `kernel/RmQRAlphanumeric-*` beside their loops, and encodes at a fitted version (`encode/rmqr-numeric-361`, `encode/rmqr-alnum-120`).

**Numbers** (tables in the [measurements](references/simd-128bit-tiers-measurements.md#phase-6-rmqr-value-writers)): the three forms of the Alphanumeric step moved the five rMQR encodes 0.94 to 1.07 on WebAssembly AOT and 0.97 to 1.05 interpreted. No `src/` change but the row's comment, so there is no benchmark delta.

**Found on the way.**
- **On WebAssembly AOT, a step inlined into one arm of the encode's switch slowed another arm 6 %** (the Numeric encode, 1.926-1.948 → 2.005-2.068 µs), as the rMQR encoder record found on ARM64. The 43-character Alphanumeric encode read 1.04 though its own writer ran 0.78.
- **The interpreter charges a vector step by the operation.** The Numeric step, which tied AOT-compiled, lost 26 % interpreted to a loop that turns three digits into one multiply.
- **Passing the writer's state to an out-of-line step and back costs a call per segment**, which a 43-character segment did not repay (1.05 AOT-compiled, 1.25 interpreted alone).

**Lessons.**
- Bound a step's end-to-end gain by its share times its saving before trying forms of it: 2.5 % was under the bar from the first kernel timing.
- Judge a step inlined into a shared method by every caller's shape, not by its own.
