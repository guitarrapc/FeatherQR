# SIMD tiers by build

This page lists the SIMD code path (tier) each kernel runs on each kind of build. The tables marked `GENERATED` come from [SimdTiers.cs](../../../src/FeatherQR/Internals/SimdTiers.cs), so don't edit them by hand: change `SimdTiers.cs` and run `SimdTiersDocTest`, which rewrites them outside CI and fails in CI when they are out of date. For why the table has this shape and how CI keeps it accurate, see the [SIMD tier inventory](qrcode-symbologies.md#simd-tier-inventory).

## Builds

Each build falls into one of four build classes, except the netstandard builds, which run no SIMD at all:

| Build | Build class |
|---|---|
| JIT (x64 CPU with AVX2) | x64 with AVX2 |
| JIT (x64 with `DOTNET_EnableAVX=0`) | x64 without AVX |
| JIT (ARM64) | ARM64 |
| NativeAOT (x64 with `IlcInstructionSet=x86-64-v3`) | x64 with AVX2 |
| NativeAOT (x64, default) | x64 without AVX |
| NativeAOT (ARM64) | ARM64 |
| WebAssembly, interpreted or AOT (`WasmEnableSIMD`, on by default) | WebAssembly |
| netstandard2.0/2.1 (.NET Framework, .NET 7 and earlier) | None: every kernel runs scalar |

- A default NativeAOT publish on x64 is limited to SSE4.2 and GFNI. Both are checked at run time, so tiers written with explicit intrinsics such as `Ssse3` and `Gfni` run on CPUs that have them. AVX2 and the rest of AVX are not available, even on CPUs that have them: `Avx2`, `Bmi2` and `Vector256` always report false.
- Portable `Vector128` code in a default NativeAOT publish is limited to SSE2, because it gets no run-time check. An operation SSE2 lacks, such as a variable shuffle, becomes a slower sequence of SSE2 instructions, even on a CPU with SSSE3.
- GFNI tiers run only in the net10.0 build. .NET 8 has no GFNI API, so the net8.0 build, which apps on .NET 8 and 9 use, runs the next tier instead.
- On ARM64 the dot product (`AdvSimdDp`) depends on the CPU: the JIT and NativeAOT both check for it at run time. Cortex-A53 and A72-class cores don't have it.
- Interpreted and AOT-compiled WebAssembly run the same tiers, because they see the same flags.

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
| `Scalar` | Nothing: a plain loop |
| `Vector128` | Portable 128-bit vectors: SSE2 on x64, AdvSimd on ARM64, PackedSimd on WebAssembly |
| `Vector256` | Portable 256-bit vectors: AVX2 on x64 |
| `Sse2` | x64 SSE2 |
| `Ssse3` | x64 SSSE3 |
| `Sse41` | x64 SSE4.1 |
| `Avx2` | x64 AVX2 |
| `Avx2Pext` | x64 AVX2, plus BMI2 PEXT/PDEP running in hardware (not on AMD before Zen 3) |
| `Gfni` | x64 GFNI on 128-bit vectors, .NET 10 and later |
| `GfniV256` | x64 GFNI on 256-bit vectors, .NET 10 and later |
| `AdvSimd` | ARM64 AdvSimd (NEON) |
| `AdvSimdDp` | ARM64 AdvSimd plus the ARMv8.2 dot product |
| `PackedSimd` | WebAssembly SIMD instructions, used where the portable `Vector128` code compiles poorly |

There is no AVX-512 (`Vector512`) tier, because it gained nothing where it was tried. 512-bit versions of the rMQR codeword extraction and the Standard QR mask scoring were measured on Zen 4, which runs a 512-bit operation as two 256-bit halves, and neither beat the 256-bit tier beyond noise. A default NativeAOT publish can't use AVX-512 anyway. The JIT on an AVX-512 CPU still uses some AVX-512 instructions inside the 256-bit tiers, such as vector compares through mask registers.

## Kernels

For each kernel: its tiers in the order its dispatch tries them, and the tier it runs on each build class. Where the CPU decides, the cell lists the options in order: "`Gfni` or `Ssse3`" means `Gfni` if the CPU has it, otherwise `Ssse3`. A cell is the tier for large inputs; small inputs, and the tail of a large one, may run a lower tier. What each kernel does, and why a cell stays scalar, is noted next to its row in `SimdTiers.cs`.

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
