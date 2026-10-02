# Bringing the encoders up to the decoders' speed

## Purpose

Since 1.2.0 the decoders became 1.5 to 9 times faster and the encoders did not move. Measured in one process on 2026-10-02, a Standard QR encode now takes about 2 to 5 times as long as decoding the same symbol (809 against 252 ns at version 1, 71.9 against 15.7 µs at version 40-L), while Micro QR and rMQR encode at about their decode speed. This plan makes the Standard QR encoder faster where its time goes, and trims the few fixed costs the other two share. The output does not change: every symbol stays byte-identical.

The measurements, how they were taken and a prototype are in [references/encode-performance-measurements.md](references/encode-performance-measurements.md). In short:

- Mask selection is 55 to 83 % of a Standard QR encode. Without it a version 40 encode would take about 13 µs, less than its decode.
- Two small changes (an index for the ECC table, shared finder-window terms in the AVX2 scorers) took the encode to 0.64 to 0.97 of its time with identical output.
- Skipping the zeroing of stack buffers with `[SkipLocalsInit]` gained 1 to 12 % below version 20 and nothing above. It is not used: see What has to stay true.
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
- An undefined ECC level still throws `ArgumentException`. An index into the ECC table must not read another level's entry instead. F21 of the 2.0.0 plan adds the up-front check that names the parameter, and it lands with that change.
- No new unsafe feature. The `ExpandBitsAdvSimd` alignment hint stays the library's only `unsafe` method, and `[SkipLocalsInit]` is not used. Under the updated memory safety rules that C# 15 starts in preview, a `stackalloc` without an initializer in a `[SkipLocalsInit]` member is an unsafe operation. In an encoder a read before a write would not fail either: it would put stale stack bytes into a symbol that leaves the process. Zero-initialization keeps such a bug deterministic, and the measured gain of skipping it (1 to 12 % below version 20, nothing above) does not pay for losing that.
- A new tier ships only on a build where it beats that build's current code, measured on that build, as in the 128-bit tiers round. netstandard2.0 keeps its scalar paths, and everything stays trim and NativeAOT safe.

## Findings that set the order

Each finding has its numbers in the references file.

Mask selection is the encode. It is 66 % of a version 1 encode, 55 % at version 6, 83 % at version 20 and 76 to 81 % at version 40. The rest of a version 40 encode is about 13 µs, under its 16 µs decode. A phase that does not touch mask selection is worth at most the remaining share.

The finder-like windows of rule 3 repeat work. Each window is checked as an 11-term AND chain, forward and backward, in rows and in columns. Both orientations are a four-module light run, which rule 1 already computes, next to a seven-module core, so sharing the two cuts each chain to two ANDs over shared terms. The bits are the same by construction. Measured with the ECC index, the mask stage went to 0.93 to 0.98 at versions 1 to 10, 0.53 to 0.60 at version 20 and 0.72 to 0.86 at version 40. The two-word tier (versions 12 to 29) gained the most.

Version selection scans. `QRCodeConstants.GetEccInfo` walks a 160-entry table through `IReadOnlyList<ECCInfo>`, and automatic selection calls it once per version it tries, which is 2.35 µs of a version 40 encode. An index takes 65 ns. Sizing, the planners, Structured Append and the decoder call the same method.

The hot methods zero their stack, and that stays. The single-word mask tier zeroes about 6.5 KB per call, because its buffers are sized for 64 rows whatever the symbol. `[SkipLocalsInit]` on four hot methods gained 8 to 12 % end to end at version 1, 1 to 2 % at versions 6 and 10, and nothing from version 20 up, where the tiers rent their scratch, and it is not used. Sizing the buffers to the symbol, which is safe code, recovered part of that at version 1 (a third on one shape, most of it on the other) and nothing at versions 6 and 10, where a variable-size `stackalloc` zeroes less efficiently than a constant one. Two safe levers are unmeasured: a constant 32-row size for versions 1 to 3, and one buffer fewer, since the shared finder terms reuse the equality and row buffers and the complement rows can be computed where they are read.

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
| 1 | P0 | Baseline | Quiet-zone-free span rows in the Standard QR, Micro QR and rMQR encode benchmarks, so encode and decode compare like with like. The V10-M alphanumeric and V40-L alphanumeric and numeric shapes. A stage harness, committed where the open decision puts it. Baseline numbers from `main` | The references file's stage table reproduced on the committed harness within its stated spread |
| 2 | P0 | Lookups and stack buffers | `GetEccInfo` as an index, with F21's up-front check. The single-word tier's stack buffers made smaller by safe means only (a constant 32-row size for versions 1 to 3, one buffer fewer, sized to the symbol), each measured on its own | Version selection under 100 ns at version 40. An undefined level throws on every entry point. A buffer change ships only with a measured win at versions 1 to 11 and no loss elsewhere. The corpus byte-identical. End-to-end delta stated |
| 3 | P0 | Shared finder-window terms | The shared core and light-run terms in every scorer tier: scalar single- and triple-word, AVX2 single-, two- and three-word, Vector128 and ARM64 | Each tier's parity test against the textbook scorer passes and catches the planted faults below. AVX2 delta stated. Vector128 measured on default NativeAOT and WebAssembly, ARM64 where a machine is available |
| 4 | P1 | Forced mask (F23) | F23's shape measured first, then the pattern applied from the cached packed templates | Forced output byte-identical for all eight patterns at every version. The forced path no slower than automatic selection at any version, now and after phase 5. F23 closed in the 2.0.0 plan |
| 5 | P1 | Transposed scorer, AVX2 | The transposed scorer for versions 12 to 40, starting from the prototype in [references/encode-performance-transposed-scorer.cs](references/encode-performance-transposed-scorer.cs): per-version tables (the periodic form measured against full tables), a vectorized transpose, the early-abort checkpoint. Versions 1 to 11 keep the lane-per-pattern tier | All eight scores equal the textbook scorer at every version 12 to 40 and every ECC level, on random and degenerate data. A measured win over phase 3 at every version 12 to 40, stated per version. Table memory per version stated |
| 6 | P1 | Transposed scorer, 128-bit builds | The same scorer on Vector128 (x64 without AVX2, WebAssembly) and ARM64 | Ships per build only where it beats that build's current code. A loss is recorded with its numbers |
| 7 | P2 | Writers | [standardqr-binary-encoder-plan.md](standardqr-binary-encoder-plan.md), run as written, starting from its phase 1 with this plan's numbers | That plan's exits |
| 8 | P2 | Output edges | `QRCodeData` built from the winner's packed rows. The Standard QR quiet-zone span path without the rent and the full clear. Micro QR written into the strided window as rMQR is | Byte-identical. The class and quiet-zone rows measured against the quiet-zone-free span row before and after, per symbology |
| 9 | P3 | Leads | Reed-Solomon across independent blocks (4 % at V40-L). Placement written as bits into the transposed matrix, since the zigzag fills column pairs. For versions 1 to 11, a deferred popcount reduction and a 512-bit lane-per-pattern tier on AVX-512 hardware | Each measured as a ceiling first, and dropped with its number recorded if under about 3 % of its encode |
| 10 | P2 | Fold | Decisions, measurements and lessons into `specs/standardqr-encoder.md`, the Micro QR spec map and `SimdTiers.cs`. README benchmark images regenerated, encode and decode from one run. F21 and F23 marked done. This plan and its two references files deleted | Nothing is only here |

Phase 1 comes first because every later exit is a delta against it. Phases 2 and 3 are small, already measured (0.64 to 0.97 end to end without any stack change) and checkable by parity tests, so they ship before the large change, and phase 3's tiers become the baseline phase 5 has to beat. Phase 4 precedes phase 5 because a faster automatic path would leave the forced one slower at every version. The writers wait for phase 5 because their ceiling roughly doubles by then.

## Verification notes

- Parity is on scores, not winners. A scorer that picks the right pattern for a wrong reason can pass a winner test, so each tier is compared with a textbook byte-matrix scorer on all eight candidates, on random data and on degenerate data (all light, all dark, stripes along rows and along columns). The prototype passed this on 240 matrices across versions 1 to 40.
- The public API's output over a corpus is hashed in the previous commit and in the change, and each side prints which tree it loaded. The measurement's corpus (7,647 symbols across lengths, alphabets, ECC levels, quiet zones, the class API, Micro QR and rMQR) is the floor.
- Planted faults each fail a test: a finder window offset (the core one module early, the light run at 6 modules instead of 7), a transposed format or version overlay one module off, an ECC table index one version or one level off, and a stack buffer one row shorter than the symbol.
- A/B runs alternate base and change in one session and report the mean of at least two rounds per side. A single short run of the same row moved by up to 30 % during the measurement.

## Open decisions

- Where the stage harness lives. `InternalsVisibleTo` for `FeatherQR.Benchmark` would let it time stages directly, but every entry in that list carries a reason, and the benchmark project also loads other libraries. Pinning the version and the mask through the public options isolates some stages without internals. The measurement used a scratch project, which is not a pattern to commit.
- Whether the transposed scorer replaces the two-word and three-word AVX2 tiers outright. The prototype won from version 20 up, and versions 12 to 19 were not measured.
- The table form: full per-pattern tables in both orientations (about 75 KB per version at version 40) or 12-periodic templates ANDed with per-version allowed rows.

## Progress log

(none yet)
