# 128-bit tiers for kernels that run scalar

## Purpose

`SimdTiers.Expected` ([SimdTiers.cs](../../../src/FeatherQR/Internals/SimdTiers.cs)) states which tier each of 28 kernels takes per build class. On 2026-09-28, 11 ran scalar on x64 without AVX (a default NativeAOT publish), 19 on WebAssembly and 1 on ARM64. More run scalar on CPUs without GFNI, fast PEXT or the ARM64 dot product. Almost all of them are SIMD kernels whose only vector tiers are 256-bit, x64 SSE or ARM64 AdvSimd. A rough first measurement put a default NativeAOT publish 2.6x behind the JIT on a URL-sized encode, and about 4x on image decode and rMQR matrix decode ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured)).

This plan lists every such cell, what the kernel's wider tier relies on and what a 128-bit form must replace. It adds a tier where one beats what that build runs today, measured on that build.

It was split out of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (Phase 6b), which keeps the timing: the tiers are internal, land before 2.0.0 to be in it, and move to 2.1.0 if they slip. When this plan completes, its durable content goes into the [SIMD tier inventory](../specs/qrcode-symbologies.md#simd-tier-inventory) and the per-symbology records, and this file is deleted.

## Scope

| In | Out |
|---|---|
| Portable `Vector128` tiers for the scalar cells of `SimdTiers.Expected` on x64 without AVX and on WebAssembly | The instruction set a NativeAOT publish targets, and README guidance on it (phase 3 of the cross-language plan). Even with these tiers, a default publish runs no 256-bit tier |
| The same tier where it also fills ARM64's scalar cells (rMQR value writers; luminance conversion without the dot product) and x64 AVX2's (Structured Append parity and scanner; syndromes without GFNI; rMQR extraction without fast PEXT) | New 256-bit or 512-bit tiers, tiers for AVX without AVX2 (`x86-64-v2,avx`), and new x86-specific tiers, SSE-family or 128-bit GFNI (D2, D4) |
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
- **The nine tiers WebAssembly already runs** (luminance inverter, regional binarizer, finder and alignment row masks, three samplers, the sub-finder lattice, the Latin-1 writer): four lose to scalar on WebAssembly AOT and on a default NativeAOT publish (phase 1).

### What the rough numbers point at

With AVX but not AVX2 (`x86-64-v2,avx`), `Avx2` reads true and `Vector256.IsHardwareAccelerated` false. That build recovered most of the encode gap and almost none of the image decode gap. Read against the table: the encode gap sits in kernels gated on `Avx2`, where only `ModulePlacerMaskCode` falls to scalar rather than SSE. The image decode gap sits in kernels gated on `Vector256`, mainly `Binarizer` and `FinderRowEdges`. The rMQR matrix decode gap has two scalar kernels, the syndrome pass and the extraction. This is an inference from one rough run. Phase 1 confirmed the encode and matrix readings and split the image decode gap in two (Progress log).

## What has to stay true

- **A tier ships only if it beats what the build runs today, on that build.** That is scalar, except for `FinderRowEdges` (the mask walk). It must win where the kernel matters and lose nowhere beyond noise. A kernel under about 3 % of every benchmark shape on a build stays as it is there, with the measured reason in a comment beside its row.
- **Each build is measured on itself.** x64 without AVX is measured on a real default NativeAOT build. `DOTNET_EnableAVX=0` reads the same flags but lowers portable vectors as `x86-64-v2` does, so it ranks explicit x86 tiers only. WebAssembly is timed on WebAssembly. A form measured on x64 says nothing about WebAssembly.
- **Interpreted and AOT-compiled WebAssembly take the same tier.** One flag gates both, so a tier is judged on both (D1).
- **New tiers are portable `Vector128`.** One tier then serves x64 without AVX, WebAssembly, and any ARM64 or AVX2 cell it reaches. On a default NativeAOT publish it compiles for SSE2 only, and an operation SSE2 lacks becomes a sequence or a software fallback, so each operation a tier uses is timed on that build. Whether an x86 instruction may stand in for one is D2; a `PackedSimd` tier only as D3 allows.
- **Existing tiers stay.** A portable step goes after an SSE or AdvSimd tier. It replaces one only where both compile to the same code on that architecture.
- **Output identical to scalar.** Each tier gets a parity test through a direct entry, since the dispatch hides a lower tier on a machine with a higher one. Planted faults in it must fail the tests.
- **Unchanged cells compile as before.** Where a class's cell stays, the dispatch's disassembly stays identical: JIT on .NET 8 and 10 with and without AVX, ILC for default x64, `x86-64-v3` and ARM64. An added branch can change what the JIT inlines.
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
| D2 | An x86 instruction where the portable form loses on x64 without AVX | **Decided 2026-09-29: no new x86-specific tier. Reopened the same day by phase 1:** the reason given, that the portable tier compiles to SSE2 to SSE4.1 there, holds under the JIT and not on a default NativeAOT publish, which lowers it for SSE2 only. There the portable form also loses SSSE3's shuffle and SSE4.1's widening and float conversion, not just `psadbw` and `pmaddubsw`. Options: (a) keep no, and limit portable tiers to operations SSE2 lowers well; (b) allow a separate SSSE3/SSE4.1 tier where the portable one loses on a default publish; (c) inside the portable tier, the x86 instruction behind its `IsSupported` check where SSE2 lacks it (`Ssse3.IsSupported ? Ssse3.Shuffle(...) : Vector128.Shuffle(...)`), which ILC keeps as a run-time check. **Recommended: (c)**, one tier with its x86 exceptions timed on each build |
| D3 | A WebAssembly `PackedSimd` tier | **Measured by phase 1; recommendation pending a decision.** Against the portable form on WebAssembly AOT: popcount 2.0 against 6.4 µs (4,096 vectors), 16-bit dot product 1.3 against 6.4, pairwise widening add 1.2 against 3.4; interpreted, the same order. A variable `Vector128.Shuffle` already lowers to `i8x16.swizzle` and `ExtractMostSignificantBits` to `bitmask`, so neither needs `PackedSimd`. The cost: a new `SimdTier` and flag family, and code the test suite never runs (tests run on x64 and ARM64), so its parity tests run inside the WebAssembly report. **Recommended:** allow it where a kernel above the bar needs exactly one of those three operations, decided against the kernel's portable form in its phase: mask scoring (popcount, 18-63 % of a Standard QR encode on WebAssembly AOT) and luminance conversion (dot product, 27-61 % of a bitmap decode) |
| D4 | A 128-bit GFNI syndrome tier | **Decided 2026-09-29: no.** GFNI is x64 only, so it does nothing for WebAssembly; on x64 it fills the no-AVX cell only on GFNI CPUs, which D2 rules out as an x86-specific tier. The portable syndrome tier fills that cell on every CPU and on WebAssembly |
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
