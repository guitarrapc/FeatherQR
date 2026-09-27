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

Kernels whose x64 dispatch goes from AVX2 or `Vector256` straight to scalar, read from the gates on `main` at 2ad606a (the table in `SimdTiers.cs` replaces this list):

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

ARM64 is in better shape by construction: AdvSimd is the ARM64 baseline, so NativeAOT keeps every AdvSimd tier, and every kernel above has one. Two things were unverified until the phase 1 report ran on osx-arm64: whether `Dp` (ARMv8.2, required by `LuminanceConverter`'s tier) reads true under a default NativeAOT publish, which it does there and is still open on linux-arm64 and win-arm64, and whether any kernel has a `Vector256`-only tier with no AdvSimd sibling (`Vector256` is never accelerated on ARM64), which none has.

## Scope

| In | Out |
|---|---|
| Every hardware-intrinsic dispatch in `src/FeatherQR` | `netstandard2.0` / `2.1` (scalar by construction, no intrinsics) |
| Build classes: x64 without AVX (default NativeAOT, `DOTNET_EnableAVX=0`), x64 with AVX2 (JIT, NativeAOT `x86-64-v3`), ARM64 (JIT, NativeAOT), and WebAssembly (phase 4) | Which instruction set a NativeAOT publish should target, and anything the README or user docs say about it |
| The tier declarations, the report, the table and its CI check | Adding, removing or re-measuring a tier, including the 128-bit tiers the table shows missing |
| The source layout of tier code, and a check that keeps it | The end-to-end numbers ([cross-language-benchmark-plan.md](cross-language-benchmark-plan.md) phase 3) |

## Decisions

- **No public API.** The report reaches the internal declarations through `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which CI already publishes as NativeAOT and runs; the test assembly already has it.
- **One table declares every kernel's tiers; the dispatch does not read it.** `Internals/SimdTiers.cs` lists each kernel with its tiers, most preferred first, and each tier's condition; the report is built from it. The dispatch keeps its own `IsSupported` reads, because that is the only form the JIT inlines as it does today (measured above). A lower tier also finishes the tail of a higher one or takes inputs too small for it, so a tier the table marks runnable is one the dispatch can take, and the report's active tier is the most preferred of them. Data-dependent cut-overs (a minimum length, a size limit) stay in the dispatch: the tier is what the hardware and the build allow, not what one call takes.
- **A source test holds the table and the code together.** Every instruction-set flag read in a kernel's files is the condition of a tier the table declares for that kernel; every declared tier's flags are read in those files; each tier's own condition in the table reads that tier's flags; and no other file in `src/FeatherQR` reads a flag, apart from `HardwareCapabilities` for the CPU facts `IsSupported` does not express. So a gate added, removed or changed without the table fails before review. What the check does not see is the order the dispatch tries its tiers in; that stays with review, one line of the table against one `if` chain.
- **Three build classes, each defined by what it always has, never has and leaves to the CPU.** x64 without AVX (a default NativeAOT publish, or the JIT under `DOTNET_EnableAVX=0`; GFNI left to the CPU), x64 with AVX2 (the JIT on an AVX2 CPU, or NativeAOT for `x86-64-v3`; GFNI and fast PEXT left to the CPU), and ARM64 (the JIT or a default NativeAOT publish; the dot product left to the CPU). The six legs of the first draft are these three classes seen from different builds, and a leg that is not its class (a toolchain whose default gains AVX, a runner without SSE4.1) fails on that alone.
- **The table states the tier, not a floor, and names the CPU's choices where the class leaves one.** A floor passes a table that understates: a kernel that gains a tier, or one that loses its GFNI tier on a runner that has GFNI, goes through unrecorded, and the table stops being the answer to "which kernel runs which tier". A plain snapshot flaps instead, because hosted runners change CPU between jobs. So a cell is one tier, or where the class leaves an instruction set to the CPU, the tiers the CPU decides between, most preferred first: the first whose instruction set the process has is expected, the last when it has none. On each machine that pins exactly one tier.
- **The table lives in `SimdTiers.cs`, under the declarations,** one row per kernel and one column per build class, and it is the only copy: the report, the check and a reader all take it from there. `FeatherQR.AotAnalysis --simd-class` holds a build to its column and fails on a disagreement; CI runs it on every NativeAOT leg and under the JIT on the same runners. A unit test holds the process it runs in to its class, so every test leg checks its JIT too, and it checks the table itself on any machine by simulating every CPU a class allows, including ARM64 on an x64 box.
- **Layout follows the check, not the other way round.** Splitting tier code into partial files shows what is implemented, not what runs: `ModulePlacer.Masking.X86.cs` exists and never runs under a default NativeAOT publish. So the declarations and the table come first, and the layout is a navigation aid that a source check keeps from drifting.
- **Files are split by tier family, not only by ISA.** An X86 / ARM split leaves no home for portable `Vector128` code, which runs on x64, ARM64 and WebAssembly alike. The stem is the type, or the type and a feature (`ModulePlacer.Masking`); `{stem}.cs` holds the entry, the dispatch and the scalar tier and uses no vector instruction; `{stem}.X86.cs` holds x86 intrinsics with the portable vectors they work on, `{stem}.Arm64.cs` ARM intrinsics with 128-bit vectors, `{stem}.Vector256.cs` and `{stem}.Vector128.cs` portable vectors of that width or narrower (`Vector64` counts as 128-bit). A family file uses its own family. Reading a flag (`IsSupported`, `IsHardwareAccelerated`) or a vector's lane count is not using the instruction set, so the dispatch stays where the JIT needs it. `SimdLayoutTest` checks every file under `src/FeatherQR`.

## Phases

Each phase follows the test-first workflow and appends a Progress log entry with Done / Lessons. No phase changes which tier any kernel runs; every phase's outputs are byte-identical to the previous commit.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Declared tiers and the report | The table of every kernel's tiers; a report listing every kernel with its tiers, the one it takes and the build's instruction sets; the source test that holds the table and the code together; `InternalsVisibleTo` for `FeatherQR.AotAnalysis`, which prints the report | Each direction of the source test fails on a planted fault (an undeclared read in a kernel file, a read in a file of no kernel, a declared tier whose read is removed, a declared tier the code lacks, a tier condition reading another tier's flag); no kernel file changes; the test suite passes under the default, `DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0` with the same skips as the previous commit; the default NativeAOT build prints the report |
| 2 | **P0** | The table and its CI check | The three build classes and the table in `SimdTiers.cs`; `--simd-class` in `FeatherQR.AotAnalysis`; the `aot-analysis` job as a matrix over the default linux-x64 publish, the `x86-64-v3` publish and the default publish on the three ARM64 runners, each also checking the JIT (`DOTNET_EnableAVX=0` on x64, `DOTNET_EnableArm64Dp=0` on ARM64); a unit test holding the test process to its class and checking the table in every state of every class | A tier removed from the code and from the declarations together, which the phase 1 check lets through, fails the check in each build class and CPU state where it was the tier taken (simulated for every kernel and state; planted in real builds on x64); every row of the list above confirmed or corrected by the table; the `Dp` question answered per ARM64 OS |
| 3 | **P1** | Layout | The naming rule applied kernel by kernel, one area per change; the source check that only files of the matching family name ISA types; spec-map links moved in the same change | The source check passes with no exemptions and fails a planted misplaced reference; outputs byte-identical; every spec-map implementation link resolves |
| 4 | P2 | WebAssembly | The report run in the AOT-compiled Playground build, which runs the portable `Vector128` tiers through PackedSimd; a WebAssembly column in the table | The column filled from the report, or marked unverified with the reason |
| 5 | P2 | Fold | The rule, the build classes and the decisions into [qrcode-symbologies.md](../specs/qrcode-symbologies.md), replacing the prose tier inventory with a pointer to the table in `SimdTiers.cs` rather than a copy of it; this plan deleted | Nothing is only here |

Phases 1 and 2 come first because they are the guarantee asked for: after them, the tier every kernel runs on every build class is stated in one committed table, and a change that makes a kernel run anything else fails CI. Phase 3 is P1 because it helps reading but guarantees nothing that phases 1 and 2 do not already check.

## Open decisions

- **WebAssembly in CI.** A headless-browser run of the AOT-compiled Playground would put the WebAssembly column under the same check; a recorded manual run is cheaper and may be enough.

## Verification notes

- The table's check is proved by planting a lowered tier and seeing it fail, not by it passing on the current tree: in every build class and CPU state by simulation, and in real builds wherever a machine for the class is at hand.
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

### 2026-09-28: phase 1 on ARM64

Done:

- Phase 1's exit re-run on osx-arm64 (Apple M2, SDK 10.0.301, ILCompiler 10.0.9) on .NET 8.0.31 and 10.0.9.
- The suite (28,953 tests over both frameworks) passes under the default, `DOTNET_EnableHWIntrinsic=0` and `DOTNET_EnableArm64Dp=0`, skipping 387, 953 and 403, the same tests the previous commit skips; the 19 `SimdTiersTest` tests pass under all three on both frameworks. `DOTNET_EnableAVX=0` is an x64 knob and has no ARM64 counterpart (Lessons).
- The default NativeAOT publish prints the report, and the JIT's agrees with it line for line on .NET 8 and 10 (.NET 8's from a scratch build of the report, since `FeatherQR.AotAnalysis` targets `net10.0` only):

| Build | Scalar | AdvSimd, AdvSimd + `Dp` or 128-bit | 256-bit |
|---|---|---|---|
| NativeAOT and JIT, default | 1 | 27 | 0 |
| JIT, `DOTNET_EnableArm64Dp=0` | 2 | 26 | 0 |
| JIT, `DOTNET_EnableHWIntrinsic=0` | 28 | 0 | 0 |

The default scalar kernel is `RmQRValueSegments`, whose only tier is SSE4.1; `DOTNET_EnableArm64Dp=0` adds `LuminanceConverter`. `Vector256` reads false, and every kernel that has a `Vector256` or AVX2 tier takes its AdvSimd or `Vector128` tier instead, in the order the table gives (the finder and alignment row masks try AdvSimd before `Vector128`, which finishes the tail). `Dp` reads true under the default publish, and still does with `IlcInstructionSet=armv8-a`, a baseline without the dot product, which suggests ILC checks it at run time rather than taking it from the target; linux-arm64 and win-arm64 stay for phase 2's reports.

Lessons:

- **On ARM64 the one knob short of turning everything off is `Dp`.** `DOTNET_EnableArm64AdvSimd=0` gives nothing in between: .NET 8 reads exactly as under `DOTNET_EnableHWIntrinsic=0`, and .NET 10 accepts and ignores it (AdvSimd and `Vector128` still read true), so a leg that sets it on .NET 10 runs the default while its name says otherwise. `DOTNET_EnableArm64Dp=0` does take effect, and it is the one that matters: it shows on an M2 what a Cortex-A72-class core, which lacks the dot product, runs. A NativeAOT binary ignores `DOTNET_EnableArm64Dp=0` and `DOTNET_EnableHWIntrinsic=0` alike, so that view is the JIT's only.
- **The ARM64 report found a kernel with no ARM64 tier**, the converse of the two with no x64 tier: `RmQRValueSegments`, SSE4.1 only. It was in the table from the start and in no list.

### 2026-09-28: phase 2, local

Done:

- The three build classes (`SimdBuildClass`, `SimdTiers.Definition`) and the table (`SimdTiers.Expected`), one row per kernel, with `SimdTiers.Check` holding a process to a class.
- `FeatherQR.AotAnalysis --simd-class`, which exits 1 listing the disagreements and 2 on an unknown class.
- `SimdTierTableTest`: every cell names only the kernel's own tiers, most preferred first, with nothing before the last entry that the class decides rather than the CPU; the table agrees with the declared tiers on every CPU each class allows (two for x64 without AVX, four with AVX2, two for ARM64); removing the tier a kernel takes fails the check for every kernel in every state where it was taken; a process missing an instruction set its class always has, or having one it never has, is refused; the test process is held to its own class.
- The `aot-analysis` job as five legs. Not run yet: it needs a push.
- On win-x64: the JIT as x64 with AVX2, the JIT under `DOTNET_EnableAVX=0` and the default NativeAOT publish as x64 without AVX, and the `x86-64-v3` publish as x64 with AVX2 all match the table, and each is refused when told the other x64 class, on the class definition alone.
- Planted in real builds: `Binarizer`'s 256-bit tier and `LocalBinarizer`'s 128-bit tier removed from the code and the declarations together. The phase 1 check (19 tests) let both through; the table check failed both under x64 with AVX2, and only `LocalBinarizer` under x64 without AVX, where `Binarizer` was scalar already.
- The suite (28,963 tests) passes under the default, `DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0`; the live table test passes under the first two and skips under the third, which is no build class.
- The list under What was already measured stands as corrected in phase 1: the table's x64-without-AVX column has its nine rows scalar, `EccBinaryEncoder` on GFNI or SSSE3, and the two kernels with no x64 tier scalar in both x64 columns.

Lessons:

- **Every subset of what a class leaves to the CPU is not a CPU.** The first simulation gave x64 with AVX2 GFNI without its 256-bit form, and the table failed there on a machine that cannot exist: with AVX present, a CPU with GFNI has both. A class now leaves instruction sets to the CPU in groups that come and go together.

Open: the CI run, which answers the `Dp` question on linux-arm64 and win-arm64 and runs the JIT legs on the runners' CPUs.

### 2026-09-28: phase 2 on CI

Done:

- [Run 36344497429](https://github.com/guitarrapc/FeatherQR/actions/runs/36344497429) of guitarrapc/FeatherQR#435 at 73c8905: all five `aot-analysis` legs pass, and each of their eleven checks reports that the tiers match the table (five NativeAOT builds, three JIT runs, three JIT runs under a knob).
- `Dp` reads true under the default NativeAOT publish on linux-arm64, win-arm64 and osx-arm64 alike, and under the JIT on all three, so `LuminanceConverter` takes its dot-product tier on every ARM64 runner. `DOTNET_EnableArm64Dp=0` moves it to scalar under the JIT on all three, the table's second choice. The `Dp` question is answered for the runners; a core without the dot product is covered by the knob only.
- The linux-x64 runner had no GFNI and fast PEXT, so the x64 cells ran their no-GFNI side there: `EccBinaryEncoder` on SSSE3, `EccBinaryDecoder` scalar with AVX2, and the two PEXT kernels on their PEXT tier. The GFNI side has run only on the Windows box.

Phase 2's exit is met. The simulated plants cover every class and state; the real-build plants ran on x64 only.

### 2026-09-28: phase 3, renames and whole-member moves

Done:

- `SimdLayoutTest`: the rule above as a source check, with the files not laid out yet listed by name; the list fails when a file on it follows the rule, so it only shrinks. The scanner is tested on planted lines (an instruction against a flag read, a lane count, a comment, a string) and planted files (an x86 call in an `.Arm64.cs`, a vector in a stem file, a family file without its family).
- Fifteen tier files renamed into the rule (`.Simd.cs` to `.X86.cs`, `.Simd.Arm.cs` and `.Neon.cs` to `.Arm64.cs`, `HardwareCapabilities.cs` to `HardwareCapabilities.X86.cs` for its CPUID reads), and the tiers of eleven files moved whole into fifteen new family files: `TextAnalyzer`, `Binarizer`, `LocalBinarizer`, the `ModeSegmenter` and Structured Append lanes, the Structured Append parity and scanner, the Standard QR four-point and piecewise samplers, and the Micro QR and rMQR samplers. Six types became `partial` for it.
- The moves are line ranges cut from one file and pasted into another; a removed line that did not land in a new file had to be blank, `#if` or `#endif`, and none was anything else. No static field with an initializer moved, so no initialization order changed. Every method body is the text it was, so the code the compilers see is the code they saw.
- References to the old file names in code comments, tests, specs and this plan rewritten; all 595 relative links under `.github/docs`, `docs/` and the README resolve. `SimdTiersTest`'s kernel files follow the moves.

Not laid out yet, ten files whose methods mix families or keep a tier inline, which a move cannot split: `ModuleBitPacker`, the finder and alignment row masks, `FinderPatternFinder.RowEdges`, `LuminanceInverter`, `MicroQRBinaryEncoder`, `MicroQRModulePlacer.PlaceSymbol`, `RmQRBinaryEncoder`, `RmQRModulePlacer` and `ModulePlacer.ExpandBits`. Splitting them changes method boundaries on hot paths, so each is its own change with disassembly behind it, one area per change as the phase says. The renames and whole-member moves went in as one change across the four areas because they change no code.
