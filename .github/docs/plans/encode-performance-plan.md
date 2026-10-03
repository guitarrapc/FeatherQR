# Bringing the encoders up to the decoders' speed

## Purpose

Since 1.2.0 the decoders became 1.5 to 9 times faster and the encoders did not move. Measured in one process on 2026-10-02, a Standard QR encode now takes about 2 to 5 times as long as decoding the same symbol (809 against 252 ns at version 1, 71.9 against 15.7 µs at version 40-L), while Micro QR and rMQR encode at about their decode speed. This plan makes the Standard QR encoder faster where its time goes, and trims the few fixed costs the other two share. The output does not change: every symbol stays byte-identical.

The measurements, how they were taken and a prototype are in [references/encode-performance-measurements.md](references/encode-performance-measurements.md). In short:

- Mask selection is 55 to 83 % of a Standard QR encode. Without it a version 40 encode would take about 13 µs, less than its decode.
- Two small changes (an index for the ECC table, shared finder-window terms in the AVX2 scorers) took the encode to 0.64 to 0.97 of its time with identical output.
- Skipping the zeroing of stack buffers with `[SkipLocalsInit]` gained 1 to 12 % at versions 1 to 10 and nothing measurable on larger symbols. It is not used: see What has to stay true.
- A scorer that also holds the matrix transposed, so that no penalty rule shifts bits across words, ran mask selection at 0.35 to 0.41 of the current time for versions 20 to 40 as an untuned prototype.
- The Alphanumeric and Numeric writers of [standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md) are 3.2 to 9.3 % of an encode. That plan stays open and runs after the scorer work, when its share is larger.

The README's benchmark images still show 1.2.0's numbers, and the repository's encode benchmarks time a matrix with a quiet zone while the decode benchmarks read one without, so the published encode and decode rows do not compare like with like.

## Scope

| In | Out |
|---|---|
| Standard QR mask selection: shared finder-window terms in every scorer tier, and a transposed scorer for versions 12 to 40 | Choosing masks differently (fewer candidates, another penalty, early acceptance). The chosen pattern is part of the output |
| Fixed costs on the Standard QR encode path: the ECC table lookup, the size of the hot methods' stack buffers, the forced mask path (F23 of the [2.0.0 plan](featherqr-2.0.0-plan.md)) | `[SkipLocalsInit]` and any other unsafe feature the library does not use today, whatever it measures |
| Output edges: `QRCodeData` built from the scorer's packed rows, and the quiet-zone span paths of Standard QR and Micro QR | Micro QR and rMQR kernels. Their placement and Reed-Solomon already run at or under decode speed |
| Benchmarks: quiet-zone-free span rows for every encode benchmark, the mid and long shapes the measurements used, a committed stage harness, and the README images regenerated at the end | Public API. Nothing is added or changed |
| Vector128 and ARM64 tiers of the new scorer, measured on their builds | A tier for a build nobody can measure. It is written when a machine is available |
| Leads measured as ceilings first: Reed-Solomon across blocks, placement written as bits, the small-version scorer's popcount and a 512-bit tier | The decoders. The Alphanumeric and Numeric writers, which [standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md) owns, and this plan only places in the order |

## What has to stay true

- Every symbol is byte-identical to the current encoder's, for every version, ECC level, segmentation, forced mask and output overload. A scorer returns the same eight penalty scores as the textbook definition, not only the same winner, so ties still go to the lower pattern.
- The span overloads allocate nothing. Per-version tables are built on first use and published as the placement tables are, and a test prints each table's size per version so its growth is visible.
- An index into the ECC table never reads another level's entry. Every entry point validates the level before it reads the content (F21 of the 2.0.0 plan, closed on `main` by #446 with `ArgumentOutOfRangeException` naming `eccLevel`), and the lookup keeps a range check of its own that throws the same.
- No new unsafe feature. The `ExpandBitsAdvSimd` alignment hint stays the library's only `unsafe` method, and `[SkipLocalsInit]` is not used. Under the updated memory safety rules that C# 15 starts in preview, a `stackalloc` without an initializer in a `[SkipLocalsInit]` member is an unsafe operation. In an encoder a read before a write would not fail either: it would put stale stack bytes into a symbol that leaves the process. Zero-initialization keeps such a bug deterministic, and the measured gain of skipping it (1 to 12 % at versions 1 to 10, nothing measurable on larger symbols) does not pay for losing that.
- A new tier ships only on a build where it beats that build's current code, measured on that build, as in the 128-bit tiers round. netstandard2.0 keeps its scalar paths, and everything stays trim and NativeAOT safe.

## Findings that set the order

Each finding has its numbers in the references file.

Mask selection is the encode. It is 66 % of a version 1 encode, 55 % at version 6, 83 % at version 19 and 76 to 81 % at versions 39 and 40. The rest of a version 40-L encode is about 13 µs, under its 16 µs decode. A phase that does not touch mask selection is worth at most the remaining share.

The finder-like windows of rule 3 repeat work. Each window is checked as an 11-term AND chain, forward and backward, in rows and in columns. Both orientations are a four-module light run, which rule 1 already computes, next to a seven-module core, so sharing the two cuts each chain to two ANDs over shared terms. The bits are the same by construction. Measured with the ECC index, the mask stage went to 0.93 to 0.98 at versions 1 to 10, 0.53 to 0.60 at version 19 and 0.72 to 0.86 at versions 39 and 40. The two-word tier (versions 12 to 29) gained the most.

Version selection scans. `QRCodeConstants.GetEccInfo` walks a 160-entry table through `IReadOnlyList<ECCInfo>`, and automatic selection calls it once per version it tries, which is 2.35 µs of a version 40 encode. An index takes 65 ns. Sizing, the planners, Structured Append and the decoder call the same method.

The hot methods zero their stack, and that stays. The single-word mask tier zeroes about 6.5 KB per call, because its buffers are sized for 64 rows whatever the symbol. `[SkipLocalsInit]` on four hot methods gained 8 to 12 % end to end at version 1, 1 to 2 % at versions 6 and 10, and nothing measurable at versions 19 to 40, where the tiers rent their scratch, and it is not used. Sizing the buffers to the symbol, which is safe code, recovered part of that at version 1 (a third on one shape, most of it on the other) and nothing at versions 6 and 10, where a variable-size `stackalloc` zeroes less efficiently than a constant one. Two safe levers were left to measure: a constant 32-row size for versions 1 to 3, and one buffer fewer, with the complement rows computed where they are read. Phase 2 measured both, and together they won (Progress log).

The forced mask path is a per-module loop. `ApplyMaskPattern` evaluates the mask predicate for every module: 28 µs at version 40 for one pattern, where the cached packed templates need about one XOR per 64 modules. In stage timing it is slower than automatic selection only around version 6, which does not reproduce F23's end-to-end figures, so its phase measures F23's shape first. Once mask selection is faster, the forced path would be the slower one at every version.

Row rules on wide rows pay for cross-word shifts, and column rules do not. From version 12 a row spans two or three words, and every shifted term of a row-direction rule pulls bits across them (five shifts and two ORs per term in the three-word tier). Column-direction rules combine whole row words. Holding each candidate also transposed turns every row rule into a column rule on the transpose. Masking is an XOR, so the transposed candidate is the transposed data XOR a per-version transposed template, and the data is transposed once per symbol. The untuned prototype ran mask selection at 0.35 to 0.41 of the current time for versions 20 to 40 and lost below version 12, where a row is one word and the lane-per-pattern tier stays. Its operations are all whole-word and vertical, so it may also give the Vector128 builds a tier for versions 12 to 40, where the current SoA tiers did not beat scalar.

The output edges are a few percent each. The class API packs the byte matrix into bits after the scorer has already held it as bits (46 ns at version 1, 2.8 µs at version 40, 4 to 6 %). The Standard QR span path with a quiet zone rents a buffer, clears the whole destination and copies rows (7 to 8 % at version 1). Micro QR's span path does the same and pays 19 to 23 %, where rMQR writes into the strided window and clears only the margins.

The writers are a ceiling that grows. They are 3.2 to 9.3 % of a single-symbol encode and 3.6 to 4.1 % of the Structured Append sets measured, above the 3 % bar of their plan. Their share roughly doubles once mask selection runs at the prototype's speed, so their plan runs after the scorer phases.

## Relation to the writer plan

[standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md) and this plan change different code: that plan the Alphanumeric and Numeric writers in `QRBinaryEncoder`, this one mask selection, the ECC table lookup and the output paths. They stay separate plans with separate branches and PRs, and neither needs the other to be correct. They meet in three places.

- The ceiling. That plan's gain is bounded by the writers' share of an encode, and mask selection is most of the rest. The share is 3.2 to 9.3 % today and roughly doubles after phase 5, so phase 7 runs that plan then. Run earlier, its end-to-end delta would be measured against a denominator this plan is about to shrink.
- The measurement. Its phase 1 asks for the writers' share by stage timing. The references file answers that for the current code, and this plan's phase 1 commits the stage harness and the long alphanumeric and numeric shapes it needs. Its numbers are taken again when it starts, because phases 2 to 5 move them. Its premise of 2.1 ns a character (63 µs on 30,000 characters) measured about 1.0 ns here (29.7 µs), which is one more reason to re-measure first.
- The fold. Both fold into the Performance section of `specs/standardqr-encoder.md`, so whichever folds second merges into what the first wrote.

## Phases

Each phase follows the test-first workflow, updates the affected spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. No public API changes. Each phase reports its kernel ratio and end-to-end delta against the previous commit, from interleaved rounds with the same benchmark file, and reads the arms it does not touch as the day's noise.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | P0 | Baseline | Quiet-zone-free span rows in the Standard QR, Micro QR and rMQR encode benchmarks, so encode and decode compare like with like. The V10-M alphanumeric and V40-L alphanumeric and numeric shapes. The stage harness as `stage/` shapes of the timing mode in `tests/FeatherQR.AotAnalysis`, which already reaches the internals and runs on every build. Baseline numbers from `main` | The references file's stage table reproduced on the committed harness within its stated spread |
| 2 | P0 | Lookups and stack buffers | `GetEccInfo` as an index. The single-word tier's stack buffers made smaller by safe means only (a constant 32-row size for versions 1 to 3, one buffer fewer, sized to the symbol), each measured on its own | Version selection under 100 ns at version 40. An undefined level throws on every entry point. A buffer change ships only with a measured win at versions 1 to 11 and no loss elsewhere. The corpus byte-identical. End-to-end delta stated |
| 3 | P0 | Shared finder-window terms | The shared core and light-run terms in every scorer tier: scalar single- and triple-word, AVX2 single-, two- and three-word, Vector128 and ARM64. Phase 2's smaller stack buffers carried to the Vector128, ARM64 and scalar single-word tiers, each only where its build measures a win | Each tier's parity test against the textbook scorer passes and catches the planted faults below. AVX2 delta stated. Vector128 measured on default NativeAOT and WebAssembly, ARM64 where a machine is available |
| 4 | P1 | Forced mask (F23) | F23's shape measured first, then the pattern applied from the cached packed templates | Forced output byte-identical for all eight patterns at every version. The forced path no slower than automatic selection at any version, now and after phase 5. F23 closed in the 2.0.0 plan |
| 5 | P1 | Transposed scorer, AVX2 | The transposed scorer for versions 12 to 40, starting from the prototype in [references/encode-performance-transposed-scorer.cs](references/encode-performance-transposed-scorer.cs): per-version tables (the periodic form measured against full tables), a vectorized transpose, the early-abort checkpoint. Versions 1 to 11 keep the lane-per-pattern tier | All eight scores equal the textbook scorer at every version 12 to 40 and every ECC level, on random and degenerate data. A measured win over phase 3 at every version 12 to 40, stated per version. Table memory per version stated |
| 6 | P1 | Transposed scorer, 128-bit builds | The same scorer on Vector128 (x64 without AVX2, WebAssembly) and ARM64 | Ships per build only where it beats that build's current code. A loss is recorded with its numbers |
| 7 | P2 | Writers | [standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md), run as written, starting from its phase 1 with this plan's numbers | That plan's exits |
| 8 | P2 | Output edges | `QRCodeData` built from the winner's packed rows. The Standard QR quiet-zone span path without the rent and the full clear. Micro QR written into the strided window as rMQR is | Byte-identical. The class and quiet-zone rows measured against the quiet-zone-free span row before and after, per symbology |
| 9 | P3 | Leads | Reed-Solomon across independent blocks (4 % at V40-L). Placement written as bits into the transposed matrix, since the zigzag fills column pairs. For versions 1 to 11, a deferred popcount reduction and a 512-bit lane-per-pattern tier on AVX-512 hardware | Each measured as a ceiling first, and dropped with its number recorded if under about 3 % of its encode |
| 10 | P2 | Fold | Decisions, measurements and lessons into `specs/standardqr-encoder.md`, the Micro QR spec map and `SimdTiers.cs`. README benchmark images regenerated, encode and decode from one run. F23 marked done. This plan and its two references files deleted | Nothing is only here |

Phase 1 comes first because every later exit is a delta against it. Phases 2 and 3 are small, already measured (0.64 to 0.97 end to end without any stack change) and checkable by parity tests, so they ship before the large change, and phase 3's tiers become the baseline phase 5 has to beat. Phase 4 precedes phase 5 because a faster automatic path would leave the forced one slower at every version. The writers wait for phase 5 because their ceiling roughly doubles by then.

## Verification notes

- Parity is on scores, not winners. A scorer that picks the right pattern for a wrong reason can pass a winner test, so each tier is compared with a textbook byte-matrix scorer on all eight candidates, on random data and on degenerate data (all light, all dark, stripes along rows and along columns). The prototype passed this on 240 matrices across versions 1 to 40.
- The public API's output over a corpus is hashed in the previous commit and in the change, and each side prints which tree it loaded. The measurement's corpus (7,647 symbols across lengths, alphabets, ECC levels, quiet zones, the class API, Micro QR and rMQR) is the floor.
- Planted faults each fail a test: a finder window offset (the core one module early, the light run at 6 modules instead of 7), a transposed format or version overlay one module off, an ECC table index one version or one level off, and a stack buffer one row shorter than the symbol.
- A/B runs alternate base and change in one session and report the mean of at least two rounds per side. A single short run of the same row moved by up to 30 % during the measurement.
- A stage is compared in the same timing mode on both sides. Run with every other shape interleaved in one process, placement and interleave read 23 to 63 % higher than run alone, while the compute-bound stages and the end-to-end rows did not move.

## Open decisions

- Whether the transposed scorer replaces the two-word and three-word AVX2 tiers outright. The prototype won from version 20 up, and versions 12 to 19 were not measured.
- The table form: full per-pattern tables in both orientations (about 75 KB per version at version 40) or 12-periodic templates ANDed with per-version allowed rows.

## Progress log

Entries are appended per phase: what was done, what was learned, and the benchmark delta or an explicit statement that no hot path moved.

### Phase 1, baseline (2026-10-02)

No hot path moved: the library is unchanged, and only benchmarks, the timing mode and these documents changed.

Done.
- `QRCodeEncodeEndToEnd`, `MicroQREncodeEndToend` and `RmQREncodeEndToEnd` have a "(Span, QZ0)" row per decode-comparable shape (11, 4 and 4), so each encode row now has a quiet-zone-free twin that compares with its decode row like for like. `QRCodeEncodeEndToEnd` has three new shapes: 300 alphanumeric characters (10-M), 4,296 alphanumeric characters (40-L) and 7,089 digits (40-L), with a class row and a quiet-zone-free row each. They are also the long writer shapes [standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md) asks for.
- The stage harness is the `stage/` shapes of the timing mode in `tests/FeatherQR.AotAnalysis` (`--time --shape stage/`), linked into the WebAssembly report too: 180 shapes over nine Standard QR, three Micro QR and three rMQR symbols. Each symbol has its end-to-end rows (default quiet zone, quiet zone 0, class API) and each stage alone on the input the stage before it produced. The open decision went this way because the timing mode already reaches the internals through its `InternalsVisibleTo`, already holds the mask kernel shapes, and runs on the JIT, NativeAOT and WebAssembly builds phase 6 has to measure. The library's `InternalsVisibleTo` list is unchanged.
- Each symbol's stage composition is checked once against the generator's own matrix, byte for byte, and against the version the shape is named for, and a shape is never built if either fails. A planted fault (version information left out of the composition) stopped the version 40 shapes as it should.
- On `main` the committed harness reproduced the references file's stage table: every end-to-end and mask selection row within 12 %, placement and interleave within 3 % when run alone. The new BenchmarkDotNet rows all run, checked with a dry job, and their first numbers are in the references file ("Phase 1 baseline").
- The timing mode publishes as NativeAOT without trim or AOT warnings and runs the stage shapes there. The WebAssembly report builds with the new file linked. The test suite passes (34,859 tests, 396 skipped).

Lessons.
- The repository's benchmark labels were wrong for two shapes, and the version check found it on its first run. `QR_Byte_V20_M` (620 bytes) encodes version 19-M and `QR_Byte_V40_H` (1,200 bytes) version 39-H, with 77 blocks rather than the 81 a comment claimed, and the timing mode's `encode/qr-v6-url-M` URL fits version 5. Both tiers' boundaries are unaffected (19 and 20 share the two-word tier, 39 and 40 the three-word one), so no conclusion moves. The benchmark comments now name the true versions, the method names stay so earlier results remain comparable, and the references file uses the true versions. A shape that names its version asserts it.
- The timing mode interleaves every selected shape in one process. That leaves compute-bound stages where BenchmarkDotNet puts them but costs memory-bound ones their warm caches (placement and interleave read 23 to 63 % higher). So a stage's change is read against the same stage in the same mode, and a share of an encode from shapes run alone.

### Phase 2, lookups and stack buffers (2026-10-03)

Against `main`, from the timing mode (five interleaved rounds per build, the median of each run's median, quiet zone 0), end to end: version 1-L 0.93, 1-M 0.92, 6-M 0.91, 10-M 0.92, 19-M 0.96 (lowest run median, see Lessons), 39-H 0.93, 40-L 0.96, 40-L alphanumeric 0.97 and 40-L numeric 0.96. Version selection went from 2.40 µs to 65 ns at version 40-L, 544 to 36 ns at 19-M and 73 to 11 ns at 6-M. The single-word mask kernel ran at 0.92 (version 1), 0.95 (6) and 0.97 (10). Micro QR and rMQR, which the phase does not touch, read 1.00 to 1.03, and the untouched version 40 mask kernel 0.98.

Done.
- `QRCodeConstants.GetEccInfo` is an index into a table built in `[version - 1][L, M, Q, H]` order, held in a nested class so it is built on first use after the base values it reads. The version scan also prices the payload once rather than once per version, which took the rest of version 40's selection from about 105 to 65 ns.
- `QRCodeConstantsEccInfoTest` holds the index to its order: all 160 entries against the codeword count of the placement layout, ISO anchors, and versions outside the table.
- F21 was first closed here at the lookup, keeping `ArgumentException` and adding the parameter name. `main` closed it at the same time with #446, which validates the level, the ECI and the quiet zone at every entry point and throws `ArgumentOutOfRangeException`, and the merge took that contract. The lookup's range check stays as an internal guard and throws what the entry points throw, and this phase's own validation test was dropped for #446's route test, which covers the same entry points.
- The AVX2 single-word mask tier allocates 32 rows for versions 1 to 3 and 64 above, and computes the row complements where the column finder windows read them, dropping its third buffer. Measured on their own against Part A (the index alone): the 32-row size helped only versions 1 to 3, the dropped buffer helped versions 1 to 10, and both together were best, 0.93 to 0.94 (version 1), 0.96 (6) and 0.97 (10) end to end, with nothing moving from version 19 up. Each variant matched the scalar scorer in the parity mode before it was timed.
- The corpus hash (7,647 symbols) is unchanged, the parity mode matches, and the test suite passes (34,889 tests, 396 skipped).
- `specs/standardqr-encoder.md` has the indexed lookup and the undefined level's failure under version selection, the decision against `[SkipLocalsInit]`, and two performance lessons.
- Not changed: the Vector128, ARM64 and scalar single-word tiers keep their 64-row buffers. Each needs its own build's measurement, which phase 3 takes for those tiers anyway, so the change moved to phase 3.

Lessons.
- On this machine a whole run of one shape lands in one of two speeds, on either build: version 19-M ran at about 21 µs or at 27 to 29 µs per run, likely from where the process lands on the two-CCD 7950X3D. A median over five runs still flips when two runs on one side land slow, so a change smaller than that gap is read from the lowest run median, from more runs, or with the process pinned to one CCD.
- Background load is bursty here. An early three-round A/B read Part A 50 % slower on a mask kernel it does not touch, and five rounds with run medians put it level. A single round decides nothing.
- Indexing the table left a version-independent cost inside the scan: the payload's bit count was recomputed for every version tried. A loop over versions is read for what does not depend on the version.

### Phase 2 follow-up, the ECC table's build (2026-10-03)

No hot path moved: only the table's one-time build changed, and a lookup still allocates nothing.

Done.
- `CreateCapacityECCTable` fills its 160-entry array in place. Phase 2 kept the old build, a `List<ECCInfo>` grown by a four-entry collection expression per version, and added `ToArray`, so the table's first use allocated 17,360 B for a 5,144 B array: the list's array, 40 temporary arrays with their wrappers (7,040 B), and the copy. It now allocates the array alone, and the first Standard QR encode in a process went from 90,256 to 78,040 B. The retained memory is that array either way, and the `Lazy<T>`, its delegate and the list that phase 2 removed were about 100 B.
- `QRCodeConstantsEccInfoTest` fails on each of five planted faults in the new build: the version or the level label one off, the data one version or one level off, and a field read from its neighbour.

Lessons.
- A table's first-use allocation read against the table's own size shows what its build wastes. Swapping `Lazy<List<T>>` for an array by appending `ToArray` kept the whole list build and added a copy.

### Phase 3, shared finder-window terms (2026-10-03)

Against the commit before, from the timing mode (seven interleaved rounds per build, each process pinned to one CCD, the median of the run medians, quiet zone 0), end to end on the JIT with AVX2: version 1-L 0.96, 1-M 0.94, 6-M 0.94, 10-M 0.97, 19-M 0.52, 39-H 0.71, 40-L 0.75, 40-L alphanumeric 0.76 and 40-L numeric 0.76. The mask kernel ran at 0.92 to 0.94 at versions 1 to 10, 0.40 to 0.42 at versions 12 to 27 and 0.69 to 0.71 at versions 28 and 40, and the scalar tiers at 0.81 to 0.87. On default NativeAOT versions 1 to 10 ran at 0.90 to 0.92 end to end, 19-M at 0.82 and 40-L at 0.83, and on WebAssembly AOT at 0.93 to 0.95, 0.80 and 0.84. Micro QR and rMQR, which the phase does not touch, read 0.98 to 1.03 on every build. The tables are in the references file ("Phase 3").

Done.
- Rule 3 is built from two shared terms in every scorer tier: scalar single- and triple-word, AVX2 single-, two- and three-word, Vector128 and ARM64. In the row direction the core is one AND of six terms and the light run is rule 1's, and the two windows never share a start, so one popcount of their OR counts both. In the column direction each tier writes the light run and the core once per row, into buffers column rule 1 is done with or into the rows themselves, which every caller rebuilds per candidate, and reads each twice. `MatchFinderRow` serves the scalar triple-word tier and the vector tiers' tail rows, and `MatchFinderRow64` the scalar single-word tier and the ARM64 tail, so the AVX2 file's copy is gone.
- The Vector128 tier also lost its complement buffer, phase 2's second change. Measured as three variants on its three builds, that beat the shared terms alone by 2 to 6 % on default NativeAOT and WebAssembly and read level end to end on the JIT without AVX2. The 32-row size won on no 128-bit build and not on the scalar single-word tier, so it ships nowhere new. The scalar single-word tier's complement buffer now holds the light runs, so it has none to drop.
- `ModulePlacerMaskScoreParityTest` compares every tier's score with the textbook score: every single-word size, the two- and three-word tiers at their ends and between, on random matrices at three densities, degenerate ones, windows planted at a row's first and last start and across the 64-bit word boundaries in both orientations, and near misses with one module flipped. It holds the early abort to its contract (the score, or `int.MaxValue` only when the bound is below the score). Its ARM64 cases run on CI's ARM64 legs.
- Planted faults: the core one module early and the light run at 6 modules instead of 7, in each tier's rows and columns and in the vector tiers' scalar tails (28 faults), and two in the Vector128 tier's new column terms. Each fails a test, and each tier's score test catches its own tier's. Three, the tail windows of the two- and three-word AVX2 tiers, fail only the score test.
- The corpus hash (7,647 symbols) is unchanged under the AVX2 tiers, with AVX off and with hardware intrinsics off. The timing mode's parity check matches for every variant on the JIT with and without AVX2, on default NativeAOT and on WebAssembly AOT. The test suite passes (34,949 tests, 402 skipped).
- `specs/standardqr-encoder.md` has the shared terms under mask evaluation, score parity under Validation, two performance lessons, and the 128-bit builds' buffer result beside the stack-zeroing one.
- Not measured: ARM64 is changed the same way, but no ARM64 machine was available, so it was compiled and not run here. Its score cases and `ModulePlacerMaskAdvSimdParityTest` run on CI's ARM64 legs, and its timing waits for a machine.

Lessons.
- Pinning and interleaving do not protect against a burst. A first five-round run had two rounds on which every row of one side, the untouched ones included, read up to 58 % slower. A control shape the change cannot reach, read beside every A/B, tells such a round from a result.
- One buffer change can win on one build and lose on another. Dropping the Vector128 tier's third buffer won on default NativeAOT and WebAssembly and cost its kernel about 3 % on the JIT without AVX2, so a buffer change is measured on each build that runs the tier.
- A shared term gains what it saves. On two-word rows each dropped shift was two shifts and an OR per word, and selection more than halved. On one-word rows a shift is one instruction, and selection moved 6 to 8 %.
- A WebAssembly AOT publish from a deep directory fails. From the scratch directory the emscripten step's paths passed 260 characters, and the publish stopped on a missing compiled-methods file. It worked from a short directory.
