# NativeAOT instruction-set probe (2026-10-02)

For the NativeAOT phase of [cross-language-benchmark-plan.md](../cross-language-benchmark-plan.md), this probe checks what each `IlcInstructionSet` does on a CPU that lacks part of it, and which FeatherQR tiers an AVX-only target brings back. It changed nothing in the library.

It ran on a Ryzen 9 7950X3D (Zen 4: AVX2, BMI2, GFNI, AVX-512, no AVX10) with .NET 10, ILCompiler 10.0.9 and win-x64. `tests/FeatherQR.AotAnalysis` was published once per target and run with its instruction-set report and `--parity`.

## Findings

A targeted instruction set is required, not checked per use: ILC compiles as if the CPU has it. On a CPU without it, the app prints "The required instruction sets are not supported by the current CPU." at startup and exits with a non-zero code before any user code runs (probed with `IlcInstructionSet=x86-64-v3,avx10v1` on this Zen 4, which has no AVX10). The message comes from the NativeAOT runtime (`Runtime.WorkstationGC.lib`). So an `x86-64-v3` app stops at startup on a CPU without AVX2 instead of crashing later or falling back.

Instruction sets outside the target are checked at run time. GFNI is not part of `x86-64-v3`, yet a v3 build reads `Gfni` and `Gfni.V256` as true here. The default target does the same for SSSE3 to SSE4.2 and GFNI.

With AVX in the target (`x86-64-v2,avx`), AVX is required and the app stops at startup without it. `Avx2`, `Bmi2`, `Gfni` and `Gfni.V256` become run-time checks and read true here, but `Vector256.IsHardwareAccelerated` gets no run-time check and reads false. On the command line the comma is passed as `%2C`. `--parity` matched every tier against its scalar form.

Under this target, FeatherQR's 28 kernels compare with the default and `x86-64-v3` builds as follows:

| Kernels | Default | `x86-64-v2,avx` | `x86-64-v3` |
|---|---|---|---|
| 11 gated on `Avx2`, `Avx2Pext` or `GfniV256`: `TextAnalyzer`, `ModuleBitPacker`, `EccBinaryEncoder`, `EccBinaryDecoder`, `LuminanceConverter`, `ModulePlacerExpandBits`, `ModulePlacerMaskCode`, `QRSampleGridPiecewise`, `MicroQRModulePlacer`, `RmQRModulePlacer`, `RmQRExtractCodewords` | 128-bit or SSE tier | Same as v3 | AVX2, PEXT or 256-bit GFNI tier |
| 8 gated on `Vector256.IsHardwareAccelerated`: `ModeSegmenterLanes`, `LuminanceInverter`, `Binarizer`, `FinderRowMask`, `FinderRowEdges`, `PerspectiveGridSampler`, `AlignmentRowMask`, `StructuredAppendLanes` | 128-bit tier | Same as default | 256-bit tier |

The other 9 kernels take the same tier in all three builds. So the encode side gets its AVX2 tiers back, and most of the image decode (finder search, binarizer, perspective sampler) stays on 128-bit tiers. The plan's rough timing (2026-09-27: `x86-64-v2,avx` recovers most of the encode gap and almost none of the image decode gap) predates the 128-bit tiers and has not been repeated.

## linux-arm64 (2026-10-03)

Repeated on an Apple M2 under Docker Desktop for Mac, which runs the arm64 image natively in its Linux VM (kernel 6.10.14-linuxkit), with the benchmark image's SDK (10.0.401, runtime and ILCompiler 10.0.12). `tests/FeatherQR.AotAnalysis` ran under the JIT and as a default NativeAOT publish for linux-arm64, with `--simd-class Arm64 --parity`.

Both builds see AdvSimd and the dot product (`Dp`), and none of the x64 sets or `Vector256`. All 28 kernels take the same tier in both builds, both match the table for `Arm64`, and `--parity` matched every tier. So on ARM64 a default NativeAOT publish loses no tier, and its gap to the JIT is code generation alone: the JIT recompiles hot methods with what it learned at run time (tiered compilation and dynamic PGO), and ILC compiles each method once, ahead of time. The benchmark CLI's own `isa` member printed the same sets for both builds.

`DOTNET_EnableArm64Dp=0` moves the JIT's `LuminanceConverter` from `AdvSimdDp` to `Vector128` and leaves NativeAOT on `AdvSimdDp`. NativeAOT ignores the knob, so only a publish for `armv8-a,-dotprod` shows a no-`Dp` CPU's tier under NativeAOT.

## Open

- `x86-64-v2,avx` has not been timed on the current code.
- Gating the eight `Vector256` kernels on `Avx2.IsSupported` instead might let this target run them at 256 bits. How ILC compiles portable `Vector256` operations when `Vector256` is not accelerated is unknown, so read the disassembly before trying it. Nothing is switched for now (decided 2026-10-02).
- This target is in none of `SimdTiers.cs`'s build classes, so `--simd-class` cannot check it.
- Selecting AVX and `Vector256` themselves at run time needs the JIT (ReadyToRun included), or an app that ships a default and a v3 build and picks one at launch. Both are the application's choice, not the library's.
