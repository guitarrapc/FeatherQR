# SIMD tier coverage across JIT, NativeAOT, ARM64 and WebAssembly

## Purpose

The library dispatches its hot kernels to SIMD tiers, and nothing states which tier a kernel runs under which build. The answer depends on the build as much as the CPU: a default NativeAOT publish on x64 reads `Avx2`, `Bmi2` and `Vector256` as unsupported on every CPU (see the probe in [cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured)), so every kernel whose only x64 tier is AVX2 or 256-bit runs scalar there, and WebAssembly runs scalar for the same kernels. Today the only way to find out is to read every gate in `src/`, which is how the list below was made, and the hand-kept [SIMD tier inventory](../specs/qrcode-symbologies.md#simd-tier-inventory) does not answer it: it counts "an x64 tier" as one thing, and for five of its thirteen kernels that tier is AVX2 or 256-bit only.

This plan makes the answer easy to read and keeps it true: every kernel's tiers declared in one place, a report of the tier each kernel runs on a given build, a committed table of the tier each kernel runs per build class that CI checks, and a file layout that shows the tiers at a glance. It has no goal beyond that. It adds no tier and removes none. Two later decisions read its table and are not made here: which instruction set a NativeAOT publish should target, and how much of the AVX2 territory the 128-bit (SSE-family) tiers should cover.

## What was already measured

The ISA probe, the rough end-to-end gap and the `x86-64-v2,avx` observation are in [cross-language-benchmark-plan.md](cross-language-benchmark-plan.md#what-was-already-measured) and are not repeated here. Added on 2026-09-27, same Windows box, SDK 10.0.301, ILCompiler 10.0.9:

- **`DOTNET_EnableAVX=0` under the JIT reads exactly what a default NativeAOT publish reads**: Vector128 accelerated, Vector256 and Vector512 not, SSE2 to SSE4.2, POPCNT and GFNI supported, AVX, AVX2, FMA, BMI1/2 and LZCNT not, `Vector<byte>.Count` 16. A JIT process can therefore show the default-AOT dispatch without an AOT compile. Only dispatch is equal: codegen inside a tier is the JIT's, not ILC's.
- **SSSE3 and SSE4.1 gates survive the default publish.** The drop starts at AVX, so kernels with an SSE-family tier keep SIMD.
- **ILCompiler 10.0.9 rejects `x86-x64-v3`** ("Unrecognized instruction set"); the name is `x86-64-v3`. Older documentation and blog posts carry the old spelling.
- **Only a dispatch that reads `IsSupported` itself compiles as it does today.** Probes compiled one three-tier dispatch (AVX2, SSSE3, AdvSimd, scalar) several ways. For ILC's default x64 target, where AVX2 and AdvSimd are compile-time false and SSSE3 a run-time check, direct reads and small `bool` properties wrapping them both become one flag test and the SSSE3 call; a tier enum selected and then compared, in an `if` chain or a `switch`, keeps a `cmove`, a compare per branch or a jump table, and every unreachable branch with its call. The JIT is stricter: it inlined a dispatch into its caller only when the dispatch read `IsSupported` itself; behind a property, an aggressively inlined property or a `static readonly` field it did not. Moving every dispatch in the library onto `bool` properties confirmed it at scale: ILC's code for fourteen dispatches stayed identical, and so did the JIT's machine code for the kernels, but `ModulePlacer.MaskCode` stopped being inlined into its caller and `TextAnalyzer.Analyze` inlined 167 methods instead of 9 before dropping its dead tiers. So the declarations cannot be what the dispatch branches on; they are checked against it instead (Decisions).

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

Kept under the default publish: `TextAnalyzer` (SSE2), `ModuleBitPacker`, `ModulePlacer` bit expansion and `RmQRModulePlacer` (SSSE3), `RmQRBinaryEncoder` (SSE4.1, SSSE3), `EccBinaryEncoder` (128-bit GFNI; this list said SSSE3 until the phase 1 report corrected it), `MicroQRBinaryEncoder` (SSE2), and the portable `Vector128` kernels (`LuminanceInverter`, `LocalBinarizer`, the finder and alignment row masks, the Micro QR affine sampler, the rMQR sub-finder lattice). The BMI2 PEXT paths (`MicroQRModulePlacer`, `RmQRMatrixDecoder`) never run there. Two kernels have no x64 tier at all and run scalar on every x64 build, JIT included: the Structured Append parity and the single-mode boundary scanner (AdvSimd only).

Some kernels record that a portable 128-bit tier was measured and left out (`Binarizer.cs`). Those measurements compared it with the 256-bit tier under the JIT, where the 256-bit tier always runs. The table makes such kernels visible per build class; revisiting them belongs to the later decisions above.

ARM64 is in better shape by construction: AdvSimd is the ARM64 baseline, so NativeAOT keeps every AdvSimd tier, and every kernel above has one. Two things are unverified: whether `Dp` (ARMv8.2, required by `LuminanceConverter`'s tier) reads true under a default NativeAOT publish on linux-arm64, win-arm64 and osx-arm64, and whether any kernel has a `Vector256`-only tier with no AdvSimd sibling (`Vector256` is never accelerated on ARM64).

## Scope

| In | Out |
|---|---|
| Every hardware-intrinsic dispatch in `src/FeatherQR` | `netstandard2.0` / `2.1` (scalar by construction, no intrinsics) |
| Build classes: x64 JIT, x64 128-bit (default NativeAOT, `DOTNET_EnableAVX=0`), x64 NativeAOT `x86-64-v3`, ARM64 JIT and NativeAOT, WebAssembly | Which instruction set a NativeAOT publish should target, and anything the README or user docs say about it |
| The tier declarations, the report, the table and its CI check | Adding, removing or re-measuring a tier, including the 128-bit tiers the table shows missing |
| The source layout of tier code, and a check that keeps it | The end-to-end numbers ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md) phase 3) |

## Decisions

- **No public API.** The report reaches the internal declarations through `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which CI already publishes as NativeAOT and runs; the test assembly already has it.
- **One table declares every kernel's tiers; the dispatch does not read it.** `Internals/SimdTiers.cs` lists each kernel with its tiers, most preferred first, and each tier's condition; the report is built from it. The dispatch keeps its own `IsSupported` reads, because that is the only form the JIT inlines as it does today (measured above). A lower tier also finishes the tail of a higher one or takes inputs too small for it, so a tier the table marks runnable is one the dispatch can take, and the report's active tier is the most preferred of them. Data-dependent cut-overs (a minimum length, a size limit) stay in the dispatch: the tier is what the hardware and the build allow, not what one call takes.
- **A source test holds the table and the code together.** Every instruction-set flag read in a kernel's files is the condition of a tier the table declares for that kernel; every declared tier's flags are read in those files; each tier's own condition in the table reads that tier's flags; and no other file in `src/FeatherQR` reads a flag, apart from `HardwareCapabilities` for the CPU facts `IsSupported` does not express. So a gate added, removed or changed without the table fails before review. What the check does not see is the order the dispatch tries its tiers in; that stays with review, one line of the table against one `if` chain.
- **The table is a floor per build class, not a snapshot.** Hosted runners change CPU model between jobs, so an exact per-machine snapshot of JIT tiers would flap on GFNI or AVX-512. The committed table states, per kernel and build class, the tier the kernel runs; CI fails when a build runs below it. This table is the answer to "which kernel, which build, which tier", and it is the only copy.
- **Layout follows the check, not the other way round.** Splitting tier code into partial files shows what is implemented, not what runs: `ModulePlacer.Masking.Simd.cs` exists and never runs under a default NativeAOT publish. So the declarations and the table come first, and the layout is a navigation aid that a source check keeps from drifting.
- **Files are split by tier family, not only by ISA.** An X86 / ARM split leaves no home for portable `Vector128` code, which runs on x64, ARM64 and WebAssembly alike. Proposed: `{Type}.cs` holds the entry, the one dispatch and the scalar tier; `{Type}.Vector128.cs` / `{Type}.Vector256.cs` the portable tiers; `{Type}.X86.cs` and `{Type}.Arm64.cs` the ISA-specific ones. The current `.Simd.cs`, `.Simd.Arm.cs`, `.Neon.cs` and `.Lanes.Arm.cs` names are renamed into it.

## Phases

Each phase follows the test-first workflow and appends a Progress log entry with Done / Lessons. No phase changes which tier any kernel runs; every phase's outputs are byte-identical to the previous commit.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Declared tiers and the report | The table of every kernel's tiers; a report listing every kernel with its tiers, the one it takes and the build's instruction sets; the source test that holds the table and the code together; `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which prints the report | Each direction of the source test fails on a planted fault (an undeclared read in a kernel file, a read in a file of no kernel, a declared tier whose read is removed, a declared tier the code lacks, a tier condition reading another tier's flag); no kernel file changes; the test suite passes under the default, `DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0` with the same skips as the previous commit; the default NativeAOT build prints the report |
| 2 | **P0** | The table and its CI check | The table committed with today's tiers per build class; the report checked against it in CI under x64 JIT, x64 with `DOTNET_EnableAVX=0`, default NativeAOT on linux-x64, NativeAOT `x86-64-v3`, and default NativeAOT on the three ARM64 runners the build matrix already has | A lowered tier (a gate removed or narrowed) fails the check under each build class it affects (planted); every row of the list above confirmed or corrected by the report; the `Dp` question answered per ARM64 OS |
| 3 | **P1** | Layout | The naming rule applied kernel by kernel, one area per change; the source check that only files of the matching family name ISA types; spec-map links moved in the same change | The source check passes with no exemptions and fails a planted misplaced reference; outputs byte-identical; every spec-map implementation link resolves |
| 4 | P2 | WebAssembly | The report run in the AOT-compiled Playground build, which runs the portable `Vector128` tiers through PackedSimd; a WebAssembly column in the table | The column filled from the report, or marked unverified with the reason |
| 5 | P2 | Fold | The table and its rule into [qrcode-symbologies.md](../specs/qrcode-symbologies.md), replacing the prose tier inventory; this plan deleted | Nothing is only here |

Phases 1 and 2 come first because they are the guarantee asked for: after them, the tier every kernel runs on every build class is stated in one committed table, and a change that makes a kernel run below it fails CI. Phase 3 is P1 because it helps reading but guarantees nothing that phases 1 and 2 do not already check.

## Open decisions

- **WebAssembly in CI.** A headless-browser run of the AOT-compiled Playground would put the WebAssembly column under the same check; a recorded manual run is cheaper and may be enough.

## Verification notes

- The table's check is proved by planting a lowered tier under each build class and seeing CI fail, not by it passing on the current tree.
- A run under `DOTNET_EnableAVX=0` proves it took the 128-bit dispatch by printing the report beside its results, not by the environment variable being set.
- Tier parity tests call each tier directly and skip on the kernel's own tier property where it has one (`IsAvx2TierSupported` and the like); the dispatch reads the same property, so it cannot drift from the dispatch and make a test compare scalar with itself.
- Any claim that a build runs a tier comes from that build's report, not from reading the gates; the list in this plan is the last one made the second way.

## Progress log

### 2026-09-27: phase 1

Done:

- `Internals/SimdTiers.cs`: 28 kernels with their tiers, most preferred first, and each tier's condition; the `SimdTier` enum; the instruction-set conditions of the 11 tiers.
- `SimdTiersTest`: the source check in both directions and the tier conditions, and each direction failing on its planted fault (an undeclared SSE4.1 read in `Binarizer.cs`, a `Vector256` read in a new file, the `Vector128` read removed from `MicroQRImageDecoder.cs`, an SSE2 tier declared for `LocalBinarizer`, `Isa.Ssse3` reading SSE2).
- `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which prints the instruction sets and every kernel's tiers with the one it takes.
- No kernel file changed. The suite (28,817 tests) passes under the default, `DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0`, skipping 378, 638 and 953, as the previous commit does.

The reports, win-x64 on the same box:

| Build | Scalar | 128-bit or SSE family | AVX2, 256-bit, PEXT or 256-bit GFNI |
|---|---|---|---|
| NativeAOT, default | 11 | 17 | 0 |
| NativeAOT, `x86-64-v3` | 2 | 7 | 19 |

The eleven default scalar kernels are the nine of the list above and the two with no x64 tier; `x86-64-v3` leaves only those two.

Lessons:

- **The first form moved every dispatch onto the table, and the JIT refused it.** With each dispatch branching on `bool` properties declared in the table, ILC's code for fourteen dispatches matched the previous commit's once two predicates were written exactly as the dispatch had them (an SSSE3 check added in front of GFNI is one more run-time flag test under NativeAOT, and a combined predicate holding a run-time check stayed a call until marked for inlining). The JIT's inlining did not match, and no form of indirection a probe tried restored it (see What was already measured). The dispatch went back to its own reads, unchanged, and the table became something the source test checks rather than something the code reads.
- **The report corrected the list made by reading the code**, in the two places it could: `EccBinaryEncoder` runs its 128-bit GFNI tier under a default publish, not SSSE3, and the Structured Append parity and boundary scanner have no x64 tier at all. Both were in the code; neither was visible without running it.
- **A tier's name is the least it needs, and a kernel can ask more.** `EccBinaryDecoder` runs 256-bit GFNI on `Gfni.V256` alone, `EccBinaryEncoder` also asks for AVX2, and `Gfni.V256` stays true with `DOTNET_EnableAVX2=0`, so the two differ on a real configuration. The table keeps each kernel's own condition, and the test lets a condition read only the flags its tier allows.
