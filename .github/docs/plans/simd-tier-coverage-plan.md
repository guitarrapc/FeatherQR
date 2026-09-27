# SIMD tier coverage across JIT, NativeAOT, ARM64 and WebAssembly

## Purpose

The library dispatches its hot kernels to SIMD tiers, and nothing states which tier a kernel runs under which build. The answer depends on the build as much as the CPU: a default NativeAOT publish on x64 reads `Avx2`, `Bmi2` and `Vector256` as unsupported on every CPU (see the probe in [cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured)), so every kernel whose only x64 tier is AVX2 or 256-bit runs scalar there, and WebAssembly runs scalar for the same kernels. Today the only way to find out is to read every gate in `src/`, which is how the list below was made, and the hand-kept [SIMD tier inventory](../specs/qrcode-symbologies.md#simd-tier-inventory) does not answer it: it counts "an x64 tier" as one thing, and for five of its thirteen kernels that tier is AVX2 or 256-bit only.

[DESIGN.md](../DESIGN.md) treats NativeAOT and WebAssembly as first-class execution environments. This plan makes that checkable for performance: a declared tier per kernel, a committed table of the minimum tier each kernel promises per build class, CI that fails when a build runs below it, a file layout that shows the tiers at a glance, and 128-bit tiers where the promise is currently scalar.

## What was already measured

The ISA probe, the rough end-to-end gap (default NativeAOT 2.6x slower than JIT on encode, about 4x on image decode and rMQR matrix decode) and the `x86-64-v2,avx` observation are in [cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured) and are not repeated here. Added on 2026-09-27, same Windows box, SDK 10.0.301, ILCompiler 10.0.9:

- **`DOTNET_EnableAVX=0` under the JIT reads exactly what a default NativeAOT publish reads**: Vector128 accelerated, Vector256 and Vector512 not, SSE2 to SSE4.2, POPCNT and GFNI supported, AVX, AVX2, FMA, BMI1/2 and LZCNT not, `Vector<byte>.Count` 16. The test suite and BenchmarkDotNet can therefore run the default-AOT dispatch without an AOT compile. Only dispatch is equal: codegen inside a tier is the JIT's, not ILC's, so timings under the knob approximate and the final numbers come from a real NativeAOT build.
- **SSSE3 and SSE4.1 gates survive the default publish.** The drop starts at AVX, so kernels with an SSE-family tier keep SIMD.
- **ILCompiler 10.0.9 rejects `x86-x64-v3`** ("Unrecognized instruction set"); the name is `x86-64-v3`. Older documentation and blog posts carry the old spelling.

Kernels whose x64 dispatch goes from AVX2 or `Vector256` straight to scalar, read from the gates on `main` at 2ad606a (phase 1's report replaces this list):

| Kernel | x64 gate | ARM64 tier |
|---|---|---|
| `Binarizer.FillHistogram` | `Vector256` | AdvSimd |
| `FinderPatternFinder.RowEdges` | `Vector256` | AdvSimd |
| `ModeSegmenter` lanes | `Vector256` | AdvSimd |
| `StructuredAppendPlanner` lanes | `Vector256` | AdvSimd |
| `ModulePlacer.MaskCode` | `Avx2` | AdvSimd |
| `QRImageDecoder.SampleGridPiecewise` | `Avx2` | AdvSimd |
| `LuminanceConverter` | `Avx2` | AdvSimd + `Dp` |
| `RmQRMatrixDecoder` extraction | `Avx2` + BMI2 (`HasFastPext`) | AdvSimd |
| `EccBinaryDecoder.ComputeSyndromes` | `Gfni.V256` (GFNI itself survives; its 256-bit form needs AVX) | AdvSimd |

Kept under the default publish: `TextAnalyzer` (SSE2), `ModuleBitPacker`, `ModulePlacer` bit expansion and `RmQRModulePlacer` (SSSE3), `RmQRBinaryEncoder` (SSE4.1, SSSE3), `EccBinaryEncoder` (SSSE3), `MicroQRBinaryEncoder` (SSE2), and the portable `Vector128` kernels (`LuminanceInverter`, `LocalBinarizer`, the finder and alignment row masks, the Micro QR affine sampler, the rMQR sub-finder lattice). The BMI2 PEXT paths (`MicroQRModulePlacer`, `RmQRMatrixDecoder`) never run there.

`Binarizer.cs` records that "a portable 128-bit tier was measured and left out". That measurement compared it against the 256-bit tier under the JIT, where the 256-bit tier always runs. Under a default publish the comparison is against scalar, and it has not been made for any kernel in the table.

ARM64 is in better shape by construction: AdvSimd is the ARM64 baseline, so NativeAOT keeps every AdvSimd tier, and every kernel above has one. Two things are unverified: whether `Dp` (ARMv8.2, required by `LuminanceConverter`'s tier) reads true under a default NativeAOT publish on linux-arm64, win-arm64 and osx-arm64, and whether any kernel has a `Vector256`-only tier with no AdvSimd sibling (`Vector256` is never accelerated on ARM64).

## Scope

| In | Out |
|---|---|
| Every hardware-intrinsic dispatch in `src/FeatherQR` | `netstandard2.0` / `2.1` (scalar by construction, no intrinsics) |
| Build classes: x64 JIT, x64 128-bit (default NativeAOT, `DOTNET_EnableAVX=0`), x64 NativeAOT `x86-64-v3`, ARM64 JIT and NativeAOT, WebAssembly | Changing what a consumer's publish targets: that is the application's setting, not the library's |
| 128-bit tiers for the kernels above, each measured against scalar under the 128-bit build | AVX-512 tiers (none exist; where measured, left out), SVE, 32-bit x86 |
| The source layout of tier code, and a check that keeps it | The end-to-end cross-language numbers ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md) phase 3) |

## Decisions

- **The dispatch reads the declared tier.** Each kernel exposes its hardware tier as one value, and the dispatch branches on that value rather than on a second copy of the `IsSupported` condition. The report can then never say something the dispatch does not do. `EccBinaryDecoder.ComputeSyndromes` already works this way for its AdvSimd gate ("The property, not a second copy of the condition"), and `LuminanceConverter.IsAvx2TierSupported` is the same idea for the parity tests; this generalizes both. Data-dependent cut-overs (a minimum length, a size limit) stay in the dispatch: the tier is what the hardware and the build allow, not what one call takes.
- **The promise is a floor per build class, not a snapshot.** Hosted runners change CPU model between jobs, so an exact per-machine snapshot of JIT tiers would flap on GFNI or AVX-512. The committed table states, per kernel and build class, the lowest tier the kernel may run; CI fails when a build runs below it. A floor of "scalar" is allowed only with the reason recorded next to it (for example, a 128-bit tier measured and refuted). This table is the answer to "which kernel, which build, which tier", and it is the only copy.
- **Layout follows the check, not the other way round.** Splitting tier code into partial files shows what is implemented, not what runs: `ModulePlacer.Masking.Simd.cs` exists and never runs under a default NativeAOT publish. So the table and its check come first, and the layout is a navigation aid that a source check keeps from drifting.
- **Files are split by tier family, not only by ISA.** An X86 / ARM split leaves no home for portable `Vector128` code, and the portable 128-bit tier is the one that covers default NativeAOT on x64, ARM64 and WebAssembly at once. Proposed: `{Type}.cs` holds the entry, the one dispatch and the scalar tier; `{Type}.Vector128.cs` / `{Type}.Vector256.cs` the portable tiers; `{Type}.X86.cs` and `{Type}.Arm64.cs` the ISA-specific ones. Only a file of the matching family may name `System.Runtime.Intrinsics.X86` or `.Arm` types, apart from the tier declaration. The current `.Simd.cs`, `.Simd.Arm.cs`, `.Neon.cs` and `.Lanes.Arm.cs` names are renamed into it.
- **A 128-bit tier is kept only if it beats scalar under the 128-bit build**, measured the way the other tier rounds were (a worktree at the previous commit, alternating rounds, the untouched arms read as noise), and on ARM64 it does not replace an AdvSimd tier that wins there.

## Phases

Each phase follows the test-first workflow and appends a Progress log entry with Done / Lessons / numbers. No public API moves unless the open decision on the report goes that way.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Declared tiers and the report | A tier value per kernel that its dispatch reads; the existing `Is*TierSupported` properties folded into it; a report that lists every kernel with its current tier and the build's ISA flags | No `IsSupported` / `IsHardwareAccelerated` read in `src/FeatherQR` outside a tier declaration and `HardwareCapabilities`, checked by a source test that a planted read fails; outputs byte-identical to the previous commit across the test suite; the dispatch still folds under the JIT (disassembly of two kernels) and the benchmark arms move within the day's noise |
| 2 | **P0** | Floor table and CI matrix | The floor table committed with today's floors (the scalar rows above included, with their reason); the report run and checked in CI under x64 JIT, x64 with `DOTNET_EnableAVX=0`, default NativeAOT on linux-x64, NativeAOT `x86-64-v3`, and default NativeAOT on the three ARM64 runners the build matrix already has; the full test suite also run once with `DOTNET_EnableAVX=0` | A lowered floor or a removed tier fails the check (planted); every row of the table above confirmed or corrected by the report; the `Dp` question answered per ARM64 OS |
| 3 | **P1** | Where the 128-bit build loses | A stage profile of the benchmark shapes under the 128-bit build (knob first, then a real default NativeAOT build to confirm), per shape: encode, Structured Append, clean and hard image decode, rMQR matrix decode | Kernels ranked by their share of each shape's time under the 128-bit build; a kernel under about 3 % of every shape leaves the queue with that number recorded |
| 4 | **P1** | 128-bit tiers, image decode | The top of phase 3's ranking; expected from the gap above to be the decode path (`LuminanceConverter`, `Binarizer.FillHistogram`, `FinderPatternFinder.RowEdges`, `SampleGridPiecewise`), but the ranking decides. The GFNI syndrome pass at 128 bits belongs here if it ranks | Per kernel: parity with scalar called directly and capability-guarded, planted faults caught, a measured win against scalar under the 128-bit build on the shapes that use it or a refutation recorded as a scalar floor with its reason; floors raised in the table; ARM64 re-measured where the portable tier could replace an AdvSimd one |
| 5 | P2 | 128-bit tiers, the rest | The remaining ranked kernels: `ModulePlacer.MaskCode`, the `ModeSegmenter` and `StructuredAppendPlanner` lanes, the rMQR extraction | As phase 4 |
| 6 | P2 | Layout | The naming rule applied kernel by kernel, one area per change; the source check that only matching files name ISA types; spec-map links moved in the same change | The source check passes with no exemptions and fails a planted misplaced reference; outputs byte-identical; every spec-map implementation link resolves |
| 7 | P2 | WebAssembly | The 128-bit tiers measured in the AOT-compiled Playground build, which runs them through PackedSimd; a WebAssembly column in the floor table | The column filled from a measured run, or marked unmeasured with the reason |
| 8 | P2 | Fold | The floor table and its rule into [qrcode-symbologies.md](../specs/qrcode-symbologies.md), replacing the prose tier inventory; per-kernel decisions and lessons into the owning design records; this plan deleted and the index updated | Nothing is only here |

Phases 1 and 2 come first because they are the guarantee asked for: after them, a change that makes any kernel run below its promise on any build class fails CI, and the table says what each build gets. They also turn the gap list above from a reading of the code into a measured fact. Phase 3 precedes any new tier because the table above says where scalar runs, not where it costs: a kernel can be scalar and cheap. The layout (phase 6) is P2 because, once phase 1 has put every gate behind a declaration and phase 2 checks it, the layout no longer carries the guarantee; it can move while tiers are still being added, but renaming files that phases 4 and 5 are about to change doubles the churn, so it follows them.

## Open decisions

- **How the NativeAOT report reaches the tiers.** They are internal. Proposed: `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which CI already publishes and runs, turned into a matrix over the build classes. The alternative is a small public diagnostic API, which would let consumers check their own publish but adds public surface and an approved-API entry.
- **When the `IlcInstructionSet` note ships.** [cross-language-benchmark-plan.md](cross-language-benchmark-plan.md) phase 3 owns the README and user-doc note with the measured number behind it. It could instead ship after phase 2 here, citing the floor table and the rough measurement, since the README currently calls NativeAOT fully supported and says nothing about the dropped tiers.
- **WebAssembly in CI.** A headless-browser run of the AOT-compiled Playground would put the WebAssembly column under the same check; a recorded manual measurement is cheaper and may be enough.

## Verification notes

- The floor check is proved by planting a lowered tier (a gate removed, a tier declared below its floor) under each build class and seeing CI fail, not by it passing on the current tree.
- A run under `DOTNET_EnableAVX=0` proves it took the 128-bit dispatch by printing the report beside its results, not by the environment variable being set.
- Tier parity tests call each tier directly and skip on the tier declaration, so a declaration that drifts from the dispatch cannot make a test compare scalar with itself.
- Any claim that a build runs a tier comes from that build's report, not from reading the gates; the list in this plan is the last one made the second way.

## Progress log

(none yet)
