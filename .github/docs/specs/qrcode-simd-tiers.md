# SIMD tiers by build

This page lists the SIMD code path (tier) each kernel runs on each kind of build. The tables marked `GENERATED` come from [SimdTiers.cs](../../../src/FeatherQR/Internals/SimdTiers.cs), so don't edit them by hand: change `SimdTiers.cs` and run `SimdTiersDocTest`, which rewrites them outside CI and fails in CI when they are out of date. For why the table has this shape and how CI keeps it accurate, see the [SIMD tier inventory](qrcode-symbologies.md#simd-tier-inventory).

## Builds

### What .NET gives each build

| Build | SIMD available |
|---|---|
| JIT, x64 | Every instruction set the CPU has. GFNI needs .NET 10 |
| JIT, x64, with `DOTNET_EnableAVX=0` | The same as a default NativeAOT publish |
| NativeAOT, x64, default | Up to SSE4.2, and GFNI, each checked at run time. No AVX, `Avx2`, `Bmi2` and `Vector256` always report false, even on CPUs that have them |
| NativeAOT, x64, `IlcInstructionSet=x86-64-v3` | AVX2 and BMI2 required, GFNI checked at run time. On a CPU without AVX2 the app stops at startup with "The required instruction sets are not supported by the current CPU." |
| NativeAOT, x64, `IlcInstructionSet=x86-64-v2,avx` | AVX required. AVX2, BMI2 and GFNI checked at run time, but `Vector256` always reports false. On a CPU without AVX the app stops at startup |
| JIT or NativeAOT, ARM64 | AdvSimd always available, the dot-product instructions (`Dp`) checked at run time. Cortex-A53 and A72-class cores lack them |
| WebAssembly, interpreted or AOT | 128-bit `PackedSimd` with `WasmEnableSIMD`, on by default. Both modes see the same flags |
| .NET Framework | No hardware intrinsics: `System.Runtime.Intrinsics` starts with .NET Core 3.0 |

Portable `Vector128` code in a default NativeAOT publish is limited to SSE2, because it gets no run-time check. An operation SSE2 lacks, such as a variable shuffle, becomes a slower sequence of SSE2 instructions, even on a CPU with SSSE3.

### How FeatherQR uses them

FeatherQR ships net8.0, net10.0 and netstandard2.0/2.1 builds. It groups the .NET builds above into four build classes, and `SimdTiers.cs` states the tier each kernel takes in each class:

| Build class | Builds |
|---|---|
| x64 with AVX2 | JIT on an x64 CPU with AVX2, or NativeAOT with `x86-64-v3` |
| x64 without AVX | Default NativeAOT, or JIT with `DOTNET_EnableAVX=0` |
| ARM64 | JIT or NativeAOT |
| WebAssembly | Interpreted or AOT |

- The netstandard2.0/2.1 builds have no SIMD tiers: every kernel runs scalar. .NET Framework, and .NET 7 and earlier, load them.
- GFNI tiers run only in the net10.0 build. .NET 8 has no GFNI API, so the net8.0 build, which apps on .NET 8 and 9 use, runs the next tier instead.
- Two builds are in none of the classes, and no CI run checks them: a JIT on an x64 CPU without AVX2, and NativeAOT with `x86-64-v2,avx`, which runs the `Avx2` tiers but not the `Vector256` ones.

The tiers each build class always has, and those that depend on the CPU. A tier not listed for a class is never available there. Tiers joined by `+` come together: a CPU has both or neither.

<!-- BEGIN GENERATED build-classes: SimdTiersDocTest renders it from SimdTiers.cs -->
| Build class | Always available | Depends on the CPU |
|---|---|---|
| x64 without AVX | `Vector128`, `Sse2`, `Ssse3`, `Sse41` | `Gfni` |
| x64 with AVX2 | `Vector128`, `Vector256`, `Sse2`, `Ssse3`, `Sse41`, `Avx2` | `Gfni` + `GfniV256`, `Avx2Pext` |
| ARM64 | `Vector128`, `AdvSimd` | `AdvSimdDp` |
| WebAssembly | `Vector128`, `PackedSimd` | None |
<!-- END GENERATED build-classes -->

## Tiers

A tier is named after the minimum it requires, and some kernels require more: `EccBinaryEncoder`, for example, uses `GfniV256` only when AVX2 is also available. Each kernel's row in `SimdTiers.cs` states what it requires.

| Tier | Requires |
|---|---|
| `Scalar` | Nothing, a plain loop |
| `Vector128` | Portable 128-bit vectors. SSE2 on x64, AdvSimd on ARM64, PackedSimd on WebAssembly |
| `Vector256` | Portable 256-bit vectors. AVX2 on x64 |
| `Sse2` | x64 SSE2 |
| `Ssse3` | x64 SSSE3 |
| `Sse41` | x64 SSE4.1 |
| `Avx2` | x64 AVX2 |
| `Avx2Pext` | x64 AVX2, plus BMI2 PEXT/PDEP running in hardware (not on AMD before Zen 3) |
| `Gfni` | x64 GFNI on 128-bit vectors, .NET 10 and later |
| `GfniV256` | x64 GFNI on 256-bit vectors, .NET 10 and later |
| `AdvSimd` | ARM64 AdvSimd (NEON) |
| `AdvSimdDp` | ARM64 AdvSimd plus the ARMv8.2 dot-product `Dp` instructions |
| `PackedSimd` | WebAssembly SIMD instructions, used where the portable `Vector128` code compiles poorly |

There is no AVX-512 (`Vector512`) tier, I've tried but it gained nothing. 512-bit versions of the rMQR codeword extraction and the Standard QR mask scoring were measured on Zen 4, which runs a 512-bit operation as two 256-bit halves, and neither beat the 256-bit tier beyond noise. Scoring all eight Standard QR candidates of versions 1-11 in one 512-bit pass, without the 256-bit tier's early abort, took 1.05 to 1.15 times that tier's time in one run (2026-10-07). A default NativeAOT publish can't use AVX-512 anyway. The JIT on an AVX-512 CPU still uses some AVX-512 instructions inside the 256-bit tiers, such as vector compares through mask registers.

## Kernels

For each kernel, its tiers in the order its dispatch tries them, and the tier the dispatch prefers on each build class. Where the CPU decides, the cell lists the options in order: "`Gfni` or `Ssse3`" means `Gfni` if the CPU has it, otherwise `Ssse3`. A cell is the tier the dispatch prefers, not the only one it takes: a lower tier can still finish the tail of an input, or take inputs too small for the preferred tier or inputs it does not pay on. What each kernel does, and why a cell stays scalar, is noted next to its row in `SimdTiers.cs`, and the figures behind a reason are below the table. How fast a tier runs is recorded in the design record of its symbology.

<!-- BEGIN GENERATED kernels: SimdTiersDocTest renders it from SimdTiers.cs -->
| Kernel | Tiers, in dispatch order | x64 without AVX | x64 with AVX2 | ARM64 | WebAssembly |
|---|---|---|---|---|---|
| `TextAnalyzer` | `Avx2`, `Sse2`, `AdvSimd`, `PackedSimd` | `Sse2` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `ModuleBitPacker` | `Avx2`, `Ssse3`, `AdvSimd`, `PackedSimd` | `Ssse3` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `ModeSegmenterLanes` | `Vector256`, `AdvSimd`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Vector256` | `AdvSimd` | `PackedSimd` |
| `ModeSegmenterSixths` | `Avx2`, `Vector128` | `Vector128` | `Avx2` | `Vector128` | `Vector128` |
| `ModeSegmenterCosts` | `Sse41`, `AdvSimd` | `Sse41` | `Sse41` | `AdvSimd` | `Scalar` |
| `EccBinaryEncoder` | `GfniV256`, `Gfni`, `Ssse3`, `AdvSimd`, `PackedSimd` | `Gfni` or `Ssse3` | `GfniV256` or `Ssse3` | `AdvSimd` | `PackedSimd` |
| `EccBinaryDecoder` | `GfniV256`, `AdvSimd`, `Vector128` | `Vector128` | `GfniV256` or `Vector128` | `AdvSimd` | `Vector128` |
| `LuminanceConverter` | `Avx2`, `AdvSimdDp`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Avx2` | `AdvSimdDp` or `Vector128` | `PackedSimd` |
| `LuminanceInverter` | `Vector256`, `Vector128` | `Vector128` | `Vector256` | `Vector128` | `Vector128` |
| `LuminanceHalver` | `Vector256`, `PackedSimd`, `Vector128` | `Vector128` | `Vector256` | `Vector128` | `PackedSimd` |
| `Binarizer` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `LocalBinarizer` | `Vector256`, `Vector128` | `Vector128` | `Vector256` | `Vector128` | `Vector128` |
| `FinderRowMask` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `FinderRowEdges` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `PerspectiveGridSampler` | `Vector256`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Vector256` | `Vector128` | `PackedSimd` |
| `ModulePlacerExpandBits` | `AdvSimd`, `Avx2`, `Ssse3` | `Ssse3` | `Avx2` | `AdvSimd` | `Scalar` |
| `ModulePlacerMaskCode` | `Avx2`, `AdvSimd`, `Ssse3`, `PackedSimd`, `Vector128` | `Ssse3` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `QRAlphanumericWriter` | `Sse41`, `AdvSimd`, `PackedSimd` | `Sse41` | `Sse41` | `AdvSimd` | `PackedSimd` |
| `QRNumericWriter` | `Ssse3`, `AdvSimd` | `Ssse3` | `Ssse3` | `AdvSimd` | `Scalar` |
| `AlignmentRowMask` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `QRSampleGridPiecewise` | `Avx2`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Avx2` | `Vector128` | `PackedSimd` |
| `StructuredAppendLanes` | `Vector256`, `AdvSimd`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Vector256` | `AdvSimd` | `PackedSimd` |
| `StructuredAppendParity` | `AdvSimd` | `Scalar` | `Scalar` | `AdvSimd` | `Scalar` |
| `StructuredAppendScanner` | `AdvSimd` | `Scalar` | `Scalar` | `AdvSimd` | `Scalar` |
| `MicroQRByteSegment` | `AdvSimd`, `Sse2` | `Sse2` | `Sse2` | `AdvSimd` | `Scalar` |
| `MicroQRModulePlacer` | `Avx2Pext`, `Ssse3`, `AdvSimd` | `Ssse3` | `Avx2Pext` or `Ssse3` | `AdvSimd` | `Scalar` |
| `MicroQRSampleGrid` | `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Sse2` | `Vector128` | `PackedSimd` |
| `RmQRValueSegments` | `Sse41` | `Sse41` | `Sse41` | `Scalar` | `Scalar` |
| `RmQRLatin1Segment` | `Sse2`, `Vector128` | `Sse2` | `Sse2` | `Vector128` | `Vector128` |
| `RmQRModulePlacer` | `Avx2`, `Ssse3`, `AdvSimd`, `PackedSimd` | `Ssse3` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `RmQRExtractCodewords` | `Avx2Pext`, `AdvSimd`, `PackedSimd`, `Vector128` | `Vector128` | `Avx2Pext` or `Vector128` | `AdvSimd` | `PackedSimd` |
| `RmQRSubFinderLattice` | `Avx2`, `Vector256`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Avx2` | `Vector128` | `PackedSimd` |
| `RmQRSampleGrid` | `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Sse2` | `Vector128` | `PackedSimd` |
<!-- END GENERATED kernels -->

The figures behind the reasons the rows give, by kernel. Where a kernel's record already holds them, the bullet names the record.

- `Binarizer` on WebAssembly keeps the 128-bit tier though AOT-compiled it counts dense input slower than scalar. The ratios by input, for the histogram alone and for a decode, are in the Performance lessons of [standardqr-decoder.md](standardqr-decoder.md).
- `ModulePlacerExpandBits` on WebAssembly stays scalar: the expand is 0.5 % of a version 40 encode AOT-compiled and 0.3 % interpreted.
- `QRAlphanumericWriter` on x64 with AVX2 keeps the SSE4.1 tier: 32 characters a step took 0.91 to 0.92 of that tier's time at 300 and 4,296 characters, 1.00 at 40 and 1.09 at 9, and holding the writer in registers took as much off the sixteen-character loop. The ARM64 step's figures are in the Performance lessons of [standardqr-encoder.md](standardqr-encoder.md).
- `QRNumericWriter` on x64 with AVX2 keeps the SSSE3 tier: 24 digits a step read 0.97 to 1.08 of that tier's time at 10 to 7,089 digits. WebAssembly stays scalar: twelve digits a step on its SIMD took 0.95 to 0.96 of the portable writer's time AOT-compiled and 1.10 to 1.65 interpreted, at 40 to 7,089 digits. The ARM64 step's figures are in the Performance lessons of [standardqr-encoder.md](standardqr-encoder.md).
- `AlignmentRowMask` on WebAssembly keeps the 128-bit tier though the interpreter runs it 16-21 % slower than scalar: AOT-compiled it is 1.5x faster, and the search is under 2 % of any shape there.
- `StructuredAppendParity` on x64 and WebAssembly stays scalar: the parity pass is 1.2 % of a 45,000-character set on a default NativeAOT publish, 0.7 to 0.8 % with AVX2 and 0.8 % on WebAssembly AOT.
- `StructuredAppendScanner` on x64 and WebAssembly stays scalar: the scanner is 0.2 % or less of a Structured Append encode on a default NativeAOT publish (0.3 % with AVX2, the same scalar time over a shorter encode) and 0.1 % on WebAssembly AOT.
- `MicroQRByteSegment` on WebAssembly stays scalar: a vector step for the 8 to 15 chars saves 1.5 % of an M4 Byte encode AOT-compiled and 2.5 % interpreted.
- `MicroQRModulePlacer` on WebAssembly stays scalar: the 16-module vector unpack places an M4 symbol in 0.25 µs against 0.27 AOT-compiled, and in 1.59 against 1.43 interpreted.
- `RmQRValueSegments` on ARM64 and WebAssembly stays scalar. The writers' shares of an encode, the vector steps' ratios and the end-to-end loss are in the bit-stream performance row of the Decisions of [rmqr-encoder.md](rmqr-encoder.md). One figure is only here: on ARM64 the Alphanumeric batch's saving, 19 to 28 % of the writer alone, is at most 2.7 % of an encode.
