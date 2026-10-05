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

There is no AVX-512 (`Vector512`) tier, I've tried but it gained nothing. 512-bit versions of the rMQR codeword extraction and the Standard QR mask scoring were measured on Zen 4, which runs a 512-bit operation as two 256-bit halves, and neither beat the 256-bit tier beyond noise. A default NativeAOT publish can't use AVX-512 anyway. The JIT on an AVX-512 CPU still uses some AVX-512 instructions inside the 256-bit tiers, such as vector compares through mask registers.

## Kernels

For each kernel, its tiers in the order its dispatch tries them, and the tier the dispatch prefers on each build class. Where the CPU decides, the cell lists the options in order: "`Gfni` or `Ssse3`" means `Gfni` if the CPU has it, otherwise `Ssse3`. The dispatch can still take a lower tier for some inputs: small inputs, the tail of a large one, or inputs the tier does not pay on, such as rMQR symbols with fewer than 44 stream bits per eight columns, which `RmQRExtractCodewords` reads with the scalar walk on WebAssembly. `ModulePlacerMaskCode`'s tiers score every version: the 128-bit tier took versions 12 to 40 too once its transposed scorer beat the scalar paths on every 128-bit build (2026-10-05). ARM64's `AdvSimd` tier keeps its own scorer for those versions, which no ARM64 machine was at hand to measure against the transposed one. What each kernel does, and why a cell stays scalar, is noted next to its row in `SimdTiers.cs`.

<!-- BEGIN GENERATED kernels: SimdTiersDocTest renders it from SimdTiers.cs -->
| Kernel | Tiers, in dispatch order | x64 without AVX | x64 with AVX2 | ARM64 | WebAssembly |
|---|---|---|---|---|---|
| `TextAnalyzer` | `Avx2`, `Sse2`, `AdvSimd`, `PackedSimd` | `Sse2` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `ModuleBitPacker` | `Avx2`, `Ssse3`, `AdvSimd`, `PackedSimd` | `Ssse3` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `ModeSegmenterLanes` | `Vector256`, `AdvSimd`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Vector256` | `AdvSimd` | `PackedSimd` |
| `EccBinaryEncoder` | `GfniV256`, `Gfni`, `Ssse3`, `AdvSimd`, `PackedSimd` | `Gfni` or `Ssse3` | `GfniV256` or `Ssse3` | `AdvSimd` | `PackedSimd` |
| `EccBinaryDecoder` | `GfniV256`, `AdvSimd`, `Vector128` | `Vector128` | `GfniV256` or `Vector128` | `AdvSimd` | `Vector128` |
| `LuminanceConverter` | `Avx2`, `AdvSimdDp`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Avx2` | `AdvSimdDp` or `Vector128` | `PackedSimd` |
| `LuminanceInverter` | `Vector256`, `Vector128` | `Vector128` | `Vector256` | `Vector128` | `Vector128` |
| `Binarizer` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `LocalBinarizer` | `Vector128` | `Vector128` | `Vector128` | `Vector128` | `Vector128` |
| `FinderRowMask` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `FinderRowEdges` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `PerspectiveGridSampler` | `Vector256`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Vector256` | `Vector128` | `PackedSimd` |
| `ModulePlacerExpandBits` | `AdvSimd`, `Avx2`, `Ssse3` | `Ssse3` | `Avx2` | `AdvSimd` | `Scalar` |
| `ModulePlacerMaskCode` | `Avx2`, `AdvSimd`, `Ssse3`, `PackedSimd`, `Vector128` | `Ssse3` | `Avx2` | `AdvSimd` | `PackedSimd` |
| `AlignmentRowMask` | `Vector256`, `AdvSimd`, `Vector128` | `Vector128` | `Vector256` | `AdvSimd` | `Vector128` |
| `QRSampleGridPiecewise` | `Avx2`, `AdvSimd`, `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Avx2` | `AdvSimd` | `PackedSimd` |
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
| `RmQRSubFinderLattice` | `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Sse2` | `Vector128` | `PackedSimd` |
| `RmQRSampleGrid` | `Sse2`, `PackedSimd`, `Vector128` | `Sse2` | `Sse2` | `Vector128` | `PackedSimd` |
<!-- END GENERATED kernels -->
