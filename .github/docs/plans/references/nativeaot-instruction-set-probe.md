# NativeAOT instruction-set probe (2026-10-02)

Research for the NativeAOT phase of [cross-language-benchmark-plan.md](../cross-language-benchmark-plan.md): what each `IlcInstructionSet` does on a CPU that lacks part of it, and which FeatherQR tiers an AVX-only target brings back. Nothing in the library was changed on it.

Setup: Ryzen 9 7950X3D (Zen 4: AVX2, BMI2, GFNI, AVX-512, no AVX10), .NET 10, ILCompiler 10.0.9, win-x64. `tests/FeatherQR.AotAnalysis` published once per target and run with its instruction-set report and `--parity`.

## Findings

**A targeted instruction set is required, not checked per use.** ILC compiles as if the CPU has it. On a CPU without it the app prints "The required instruction sets are not supported by the current CPU." at startup and exits with a non-zero code before any user code runs. Probe: `IlcInstructionSet=x86-64-v3,avx10v1` on this Zen 4, which has no AVX10. The message is in the NativeAOT runtime (`Runtime.WorkstationGC.lib`). So an `x86-64-v3` app stops at startup on a CPU without AVX2; it does not crash later and does not fall back.

**Instruction sets outside the target are checked at run time.** GFNI is not part of `x86-64-v3`, and a v3 build still reads `Gfni` and `Gfni.V256` as true here. The default target does the same for SSSE3 to SSE4.2 and GFNI.

**With AVX in the target (`x86-64-v2,avx`), AVX2 becomes a run-time check, but `Vector256` does not.** AVX is required (the app stops at startup without it). `Avx2`, `Bmi2`, `Gfni` and `Gfni.V256` read true here, checked at run time; `Vector256.IsHardwareAccelerated` reads false. On the command line the comma is passed as `%2C`. `--parity` matched every tier against its scalar form.

What that does to FeatherQR's 28 kernels, against the default and `x86-64-v3` builds:

| Kernels | Default | `x86-64-v2,avx` | `x86-64-v3` |
|---|---|---|---|
| 11 gated on `Avx2`, `Avx2Pext` or `GfniV256`: `TextAnalyzer`, `ModuleBitPacker`, `EccBinaryEncoder`, `EccBinaryDecoder`, `LuminanceConverter`, `ModulePlacerExpandBits`, `ModulePlacerMaskCode`, `QRSampleGridPiecewise`, `MicroQRModulePlacer`, `RmQRModulePlacer`, `RmQRExtractCodewords` | 128-bit or SSE tier | Same as v3 | AVX2, PEXT or 256-bit GFNI tier |
| 8 gated on `Vector256.IsHardwareAccelerated`: `ModeSegmenterLanes`, `LuminanceInverter`, `Binarizer`, `FinderRowMask`, `FinderRowEdges`, `PerspectiveGridSampler`, `AlignmentRowMask`, `StructuredAppendLanes` | 128-bit tier | Same as default | 256-bit tier |

The other 9 kernels take the same tier in all three builds. So the encode side gets its AVX2 tiers back, and most of the image decode (finder search, binarizer, perspective sampler) stays on 128-bit tiers. The rough timing in the plan (2026-09-27: `x86-64-v2,avx` recovers most of the encode gap and almost none of the image decode gap) predates the 128-bit tiers and has not been repeated.

## Open

- No timing of `x86-64-v2,avx` on the current code.
- Gating the eight `Vector256` kernels on `Avx2.IsSupported` instead might let this target run them at 256 bits. How ILC compiles portable `Vector256` operations when `Vector256` is not accelerated is unknown; read the disassembly before trying it. Decided not to switch anything for now (2026-10-02).
- This target is in none of `SimdTiers.cs`'s build classes, so `--simd-class` cannot check it.
- Run-time selection of AVX and `Vector256` themselves needs the JIT (ReadyToRun included), or an app that ships a default and a v3 build and picks one at launch. Both are the application's choice, not the library's.
