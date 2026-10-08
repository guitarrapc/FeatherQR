# Encode performance measurements (2026-10-02)

For [encode-performance-plan.md](../encode-performance-plan.md), these measurements locate where the three encoders spend their time and test the candidate changes before any of them is written into the library. Nothing in the repository was changed to take them.

Unless a section names another machine or build, the numbers are from one Windows box (Ryzen 9 7950X3D, Zen 4) under .NET 10.0.9 JIT (x86-64-v4, so the AVX2 tiers and GFNI run), BenchmarkDotNet 0.15.8. A section that also measures the JIT without AVX2, default NativeAOT or WebAssembly names that build, and the ARM64 sections are from an Apple M2.

## How they were taken

- Stage costs come from a scratch BenchmarkDotNet project that reached the internals. Each stage was timed on precomputed input from the stage before it, so a stage row is that stage alone. Mask selection rows include a copy of the unmasked matrix, because the selection masks in place, and the copy is listed separately.
- The job was one launch, 4 warmups and 12 iterations of 150 ms. A/B rows are the mean of two interleaved rounds (base, change, base, change). Single rounds of the same row moved by up to 30 %, for example a version 1 encode at 892 ± 249 ns in one round against 683 in the mean.
- The 1.2.0 comparison loaded the 1.2.0 sources (`SkiaSharp.QrCode`, built from the tag) and the current `FeatherQR` into one process, with the same inputs and the span API at quiet zone 0. The setup checked that both versions encode the Standard QR shapes to identical modules.
- The benchmark rows named `QR_Byte_V20_M` and `QR_Byte_V40_H` encode version 19-M and version 39-H: 620 bytes fit 19-M and 1,200 bytes fit 39-H. Phase 1's version check found it, and the tables here use the true versions.
- Phase 1 committed the stage harness as the `stage/` shapes of the timing mode in `tests/FeatherQR.AotAnalysis` (`--time --shape stage/`). Run there on `main` (2026-10-02, two runs), every end-to-end and mask selection row reproduced the stage table below within 12 %. Placement and interleave read 23 to 63 % higher when every shape ran interleaved in one process, and within 3 % of the table when each ran alone, so a memory-bound stage is compared alone or in the same mode on both sides.
- Every experiment was checked for identical output before it was timed: the public API's output over 7,647 symbols was hashed in both builds. The corpus held lengths 0 to 64 and 70 to 2,997, four alphabets (digits, the alphanumeric set, ASCII, ASCII with Japanese and Latin-1), every ECC level, quiet zone 0 and 4, the class API, and Micro QR and rMQR where the content fits.

## 1.2.0 against the current code

Same process, span API, quiet zone 0. Error columns ran up to ±11 %, so encode differences under about 10 % are noise.

| Shape | Encode 1.2.0 | Encode now | Decode 1.2.0 | Decode now |
|---|---|---|---|---|
| Standard QR V1-L, 10 digits | 756 ns | 809 ns | 990 ns | 252 ns |
| Standard QR V6-M, URL | 2.01 µs | 1.93 µs | 5.58 µs | 1.04 µs |
| Standard QR V19-M, 620 bytes | 23.4 µs | 22.9 µs | 31.0 µs | 4.79 µs |
| Standard QR V40-L, 2,900 bytes | 78.8 µs | 71.9 µs | 144.6 µs | 15.7 µs |
| Standard QR V39-H, 1,200 bytes | 70.4 µs | 79.8 µs | 113.8 µs | 16.2 µs |
| Micro QR M2-L, 10 digits | 133 ns | 130 ns | 321 ns | 156 ns |
| Micro QR M4-M, 13 bytes | 166 ns | 165 ns | 601 ns | 304 ns |
| rMQR R7x43-M, 12 digits | 135 ns | 132 ns | 196 ns | 128 ns |
| rMQR R17x139-M, 150 bytes | 896 ns | 797 ns | 2.37 µs | 671 ns |

The README's benchmark images still show the 1.2.0 numbers. The repository's encode benchmarks time the span API at the default quiet zone, while the decode benchmarks read quiet-zone-free matrices, so their encode and decode rows do not compare like with like.

## Phase 1 baseline: encode rows without a quiet zone

The new "(Span, QZ0)" rows of the encode benchmarks on `main` (BenchmarkDotNet ShortRun, one run), beside the decode of the same symbol from the 1.2.0 comparison above where it has one. Both read and write quiet-zone-free matrices, so the pair compares like with like.

| Shape | Encode, quiet zone 0 | Decode |
|---|---|---|
| Standard QR V1-L, 10 digits | 702 ns | 252 ns |
| Standard QR V1-M, 16 alphanumeric | 747 ns | |
| Standard QR V6-M, URL | 1.79 µs | 1.04 µs |
| Standard QR V6-M, 65 Kanji | 1.99 µs | |
| Standard QR V10-M, 300 alphanumeric | 3.06 µs | |
| Standard QR V15-L, 320 Kanji | 14.1 µs | |
| Standard QR V19-M, 620 bytes | 21.3 µs | 4.79 µs |
| Standard QR V39-H, 1,200 bytes | 64.0 µs | 16.2 µs |
| Standard QR V40-L, 2,900 bytes | 65.8 µs | 15.7 µs |
| Standard QR V40-L, 4,296 alphanumeric | 76.3 µs | |
| Standard QR V40-L, 7,089 digits | 69.0 µs | |
| Micro QR M2-L, 10 digits | 115 ns | 156 ns |
| Micro QR M3-L, 14 alphanumeric | 148 ns | |
| Micro QR M4-M, 13 bytes | 145 ns | 304 ns |
| Micro QR M4-M, 8 Kanji | 164 ns | |
| rMQR R7x43-M, 12 digits | 116 ns | 128 ns |
| rMQR R11x59-M, 43 alphanumeric | 215 ns | |
| rMQR R17x139-M, 150 bytes | 774 ns | 671 ns |
| rMQR R17x139-M, 92 Kanji | 1.08 µs | |

The same run's class row for 7,089 digits read 95 µs, and two re-runs of that row read 71 and 77 µs, so a single ShortRun row is not a baseline on its own.

On a default NativeAOT build (the 128-bit build class, where versions 12 to 40 score masks with the scalar tiers), the timing mode put the V40-L 2,900-byte encode at 94 µs without a quiet zone, with mask selection at 77.5 µs (82 %) and the forced mask path at 65.6 µs. These are phase 6's starting numbers.

## Standard QR stage costs

Current `main`. The E2E rows are the public API. The stage rows, with the copy taken out of the mask row, add up to the quiet-zone-free E2E row within 10 %.

| Stage | V1-L digits | V6-M URL | V19-M bytes | V40-L bytes | V39-H bytes |
|---|---|---|---|---|---|
| E2E, span, quiet zone 4 | 774 ns | 1.92 µs | 21.0 µs | 68.4 µs | 69.3 µs |
| E2E, span, quiet zone 0 | 712 ns | 1.76 µs | 21.3 µs | 68.0 µs | 66.3 µs |
| E2E, class | 768 ns | 2.05 µs | 21.8 µs | 71.6 µs | 66.3 µs |
| Text analysis | 13 ns | 19 ns | 87 ns | 354 ns | 150 ns |
| Version selection | 6 ns | 69 ns | 535 ns | 2.35 µs | 2.31 µs |
| Data codewords | 18 ns | 43 ns | 121 ns | 494 ns | 216 ns |
| Reed-Solomon | 21 ns | 103 ns | 584 ns | 2.68 µs | 1.31 µs |
| Interleave | 6 ns | 56 ns | 243 ns | 1.13 µs | 789 ns |
| Template copy and data placement | 54 ns | 270 ns | 1.52 µs | 5.63 µs | 5.25 µs |
| Copy of the unmasked matrix | 5 ns | 15 ns | 65 ns | 434 ns | 386 ns |
| Mask selection, copy included | 514 ns | 1.07 µs | 17.5 µs | 56.1 µs | 53.3 µs |
| Forced mask (pattern 3), copy included | 290 ns | 1.41 µs | 7.80 µs | 28.3 µs | 26.6 µs |
| Format and version information | 23 ns | 22 ns | 38 ns | 38 ns | 38 ns |
| `QRCodeData` pack (class API only) | 46 ns | 162 ns | 748 ns | 2.78 µs | 2.65 µs |

Mask selection is 66 % of the encode at version 1, 55 % at version 6, 83 % at version 19 and 76 to 81 % at versions 39 and 40. Without it a version 40-L encode would take about 13 µs, under the 16 µs decode.

Version selection is a linear scan: `QRCodeConstants.GetEccInfo` walks the 160-entry table through `IReadOnlyList<ECCInfo>`, and automatic selection calls it once per version it tries.

The forced mask path is slower than automatic selection only around version 6 in this stage timing (1.32x). It is 0.45 to 0.66x elsewhere, but still 28 µs at version 40 for one pattern, against about one XOR per 64 modules with the cached packed templates. F23 in the 2.0.0 plan measured the forced path slower end to end at 1,000 characters, which these stage numbers do not reproduce.

The quiet zone costs the span API a pooled rent, a clear of the whole destination and a copy per row: 51 to 62 ns at version 1 (7 to 8 %) and 165 ns at version 6, and within noise from version 20 up.

## Micro QR and rMQR stage costs

Current `main`, default quiet zone 2.

| Stage | M2-L digits | M3-L alphanumeric | M4-M bytes |
|---|---|---|---|
| E2E, span, quiet zone 2 | 144 ns | 180 ns | 214 ns |
| E2E, span, quiet zone 0 | 117 ns | 144 ns | 165 ns |
| E2E, class | 136 ns | 174 ns | 170 ns |
| Text analysis | 13 ns | 16 ns | 12 ns |
| Version selection | 6 ns | 9 ns | 8 ns |
| Data codewords | 9 ns | 10 ns | 11 ns |
| Reed-Solomon | 5 ns | 11 ns | 11 ns |
| Clear and fused placement | 64 ns | 91 ns | 87 ns |
| `MicroQRCodeData` pack | 15 ns | 15 ns | 15 ns |

| Stage | R7x43-M digits | R11x59-M alphanumeric | R17x139-M bytes |
|---|---|---|---|
| E2E, span, quiet zone 2 | 152 ns | 274 ns | 882 ns |
| E2E, span, quiet zone 0 | 120 ns | 210 ns | 750 ns |
| E2E, class | 152 ns | 247 ns | 843 ns |
| Text analysis | 16 ns | 20 ns | 26 ns |
| Version selection (pinned) | 6 ns | 7 ns | 5 ns |
| Data codewords | 9 ns | 15 ns | 22 ns |
| Reed-Solomon and interleave | 26 ns | 51 ns | 221 ns |
| Placement | 44 ns | 110 ns | 443 ns |
| `RmQRCodeData` pack | 18 ns | 25 ns | 59 ns |

Both encoders run at or under their decoders' speed once the quiet zone is taken out. The Micro QR span path clears the whole destination, builds the core in a separate stack buffer and copies its rows, which is 27 to 49 ns over the quiet-zone-free row, 19 to 23 % of the quiet-zone row. rMQR already writes the core into the strided window and clears only the margins, and its remaining 32 to 132 ns is the per-row margin clears and the strided placement.

## Two small changes, and a third that was dropped

The changes, applied to a copy of the library:

- `GetEccInfo` indexes a flat array in `[version][L, M, Q, H]` order instead of scanning, and still throws for an undefined level.
- The finder-like windows of rule 3 share their terms in the AVX2 tiers (single-word lane-per-pattern, two-word and three-word SoA). A forward window is four light modules followed by the seven-module core (dark, light, three dark, light, dark), and a backward window is the core followed by four light modules. The four-light run is already computed for rule 1, so each orientation is two ANDs over shared terms instead of an 11-term chain. In the column direction the core and the run are computed once per row and reused by the windows that overlap them.
- Dropped: `[SkipLocalsInit]` on `MaskCode64Simd`, `ScoreLanes64`, `PlaceDataWords` and `QRCodeGenerator.WriteCoreModules`, whose stack buffers are all written before they are read. Without it `MaskCode64Simd` zeroes about 6.5 KB per call. It was measured on its own (below) and is not used, for the reasons in the plan's What has to stay true.

Output was identical on the 7,647-symbol corpus for every variant.

The table is the three changes together, two interleaved rounds per side. The stack change does nothing measurable on the version 19 to 40 rows, where the tiers rent their scratch, so those rows stand for the two kept changes. A single round of those two alone gave 0.64 at version 19 and 0.79 to 0.80 at versions 39 and 40 end to end, and a mask stage of 0.60 at version 19 and 0.72 to 0.86 on the four version 39 and 40 shapes.

| Shape | E2E base | E2E change | Ratio | Mask base | Mask change | Ratio |
|---|---|---|---|---|---|---|
| V1-L, 10 digits | 839 ns | 683 ns | 0.81 | 549 ns | 448 ns | 0.82 |
| V1-M, 16 alphanumeric | 859 ns | 727 ns | 0.85 | 599 ns | 523 ns | 0.87 |
| V6-M, URL | 1.95 µs | 1.88 µs | 0.96 | 1.08 µs | 954 ns | 0.89 |
| V10-M, 300 alphanumeric | 3.30 µs | 3.01 µs | 0.91 | 1.65 µs | 1.36 µs | 0.83 |
| V19-M, 620 bytes | 23.0 µs | 13.7 µs | 0.59 | 19.5 µs | 10.3 µs | 0.53 |
| V40-L, 2,900 bytes | 66.5 µs | 54.6 µs | 0.82 | 53.6 µs | 44.6 µs | 0.83 |
| V40-L, 4,296 alphanumeric | 82.0 µs | 67.6 µs | 0.82 | 63.0 µs | 43.9 µs | 0.70 |
| V40-L, 7,089 digits | 71.2 µs | 58.9 µs | 0.83 | 55.2 µs | 45.8 µs | 0.83 |
| V39-H, 1,200 bytes | 75.1 µs | 56.4 µs | 0.75 | 56.9 µs | 47.3 µs | 0.83 |

Version selection alone went from 69 ns to 11 ns at version 6, from 535 ns to 29 ns at version 19 and from 2.35 µs to 65 ns at version 40. The two-word tier (versions 12 to 29) gained the most from the shared windows.

### Stack zeroing on its own

Four builds, two interleaved rounds each, on the versions where the single-word tier runs: base, A (the two kept changes), B (A with `[SkipLocalsInit]` as above) and C (A with the single-word tier's buffers and the placement bit buffer sized to the symbol instead of their fixed maximum, which is safe code). E2E is the span API at quiet zone 0.

| Row | Base | A | B | C |
|---|---|---|---|---|
| E2E, V1-L digits | 781 ns | 686 ns | 621 ns | 665 ns |
| E2E, V1-M alphanumeric | 821 ns | 794 ns | 699 ns | 708 ns |
| E2E, V6-M URL | 1.90 µs | 1.72 µs | 1.71 µs | 1.71 µs |
| E2E, V10-M alphanumeric | 3.43 µs | 2.84 µs | 2.77 µs | 2.92 µs |
| Mask, V1-L digits | 545 ns | 509 ns | 459 ns | 486 ns |
| Mask, V1-M alphanumeric | 590 ns | 578 ns | 540 ns | 551 ns |
| Mask, V6-M URL | 1.12 µs | 1.03 µs | 1.02 µs | 1.06 µs |
| Mask, V10-M alphanumeric | 1.50 µs | 1.44 µs | 1.36 µs | 1.47 µs |

Skipping the zeroing (B against A) saved 65 to 95 ns end to end at version 1, 8 to 12 % of base, and 17 to 70 ns at versions 6 and 10, 1 to 2 %. Sizing the buffers (C against A) won back 21 and 86 ns of that at version 1 and lost at versions 6 and 10. That fits a variable-size `stackalloc` zeroing less efficiently than a constant-size one, which the disassembly has not confirmed. Two safe options were not tried: a constant 32-row size for versions 1 to 3, and one buffer fewer (the shared windows reuse the equality and row buffers, and the complement rows can be computed where they are read).

## Phase 3: shared finder-window terms in every tier

Taken on 2026-10-03 with the timing mode of `tests/FeatherQR.AotAnalysis`, and of `tests/FeatherQR.WasmReport` for WebAssembly. Base is the commit before (ecb5fc8), change is the phase's tree. Each process was pinned to the first eight cores (one CCD), and the two sides alternated for seven rounds. A cell is the median of the seven run medians. Micro QR M4-M and rMQR R17x139-M, which the phase does not touch, read 0.98 to 1.01 on the JIT and 0.98 to 1.03 on the other builds. A first five-round run on the JIT had two rounds on which every row of one side, the untouched ones included, read up to 58 % slower, so its rows were taken again.

JIT, x86-64-v4 (the AVX2 tiers):

| Shape | E2E base | E2E change | Ratio | Mask base | Mask change | Ratio |
|---|---|---|---|---|---|---|
| V1-L, 10 digits | 762 ns | 729 ns | 0.96 | | | |
| V1-M, 16 alphanumeric | 844 ns | 796 ns | 0.94 | | | |
| V6-M, URL | 2.07 µs | 1.95 µs | 0.94 | | | |
| V10-M, 300 alphanumeric | 3.42 µs | 3.32 µs | 0.97 | | | |
| V19-M, 620 bytes | 22.9 µs | 11.9 µs | 0.52 | 19.5 µs | 8.55 µs | 0.44 |
| V39-H, 1,200 bytes | 77.9 µs | 55.4 µs | 0.71 | 67.7 µs | 47.9 µs | 0.71 |
| V40-L, 2,900 bytes | 75.6 µs | 56.8 µs | 0.75 | 64.3 µs | 45.7 µs | 0.71 |
| V40-L, 4,296 alphanumeric | 83.6 µs | 63.4 µs | 0.76 | | | |
| V40-L, 7,089 digits | 80.2 µs | 61.2 µs | 0.76 | | | |

The mask kernel alone (`kernel/MaskCode-vN`, selection on a placed matrix), by tier, base to change:

| Version | AVX2 tier | Ratio | Scalar tier | Ratio |
|---|---|---|---|---|
| 1 | 646 to 606 ns | 0.94 | 1.90 to 1.66 µs | 0.87 |
| 6 | 1.25 to 1.15 µs | 0.92 | 3.73 to 3.03 µs | 0.81 |
| 10 | 1.70 to 1.58 µs | 0.93 | 5.59 to 4.58 µs | 0.82 |
| 12 | 14.4 to 5.72 µs | 0.40 | 25.1 to 21.1 µs | 0.84 |
| 20 | 19.6 to 8.05 µs | 0.41 | 38.4 to 32.6 µs | 0.85 |
| 27 | 27.7 to 11.6 µs | 0.42 | 50.7 to 43.0 µs | 0.85 |
| 28 | 48.4 to 34.5 µs | 0.71 | 52.4 to 44.5 µs | 0.85 |
| 40 | 67.8 to 46.7 µs | 0.69 | 75.3 to 63.0 µs | 0.84 |

The two-word tier (versions 12 to 27) gains the most because each shifted term of a row rule costs it two shifts and an OR per word, and the shared terms drop most of those shifts. The three-word tier gains less, and the single-word tiers, where a shift is one instruction, least.

The 128-bit builds run the Vector128 tier for versions 1 to 11 and the scalar tiers above. Three variants of the Vector128 tier were measured, each against the base:

- Shared: the shared terms with the tier's three buffers.
- One fewer: the shared terms without the complement buffer, the complements computed where they are read. This is the one kept.
- 32 rows: one fewer, and buffers of 32 rows for versions 1 to 3.

| Build | Shape | Shared | One fewer | 32 rows |
|---|---|---|---|---|
| JIT without AVX2 | mask kernel, versions 1, 6, 10 | 0.90 to 0.91 | 0.93 | 0.92 to 0.93 |
| | E2E, V1-L, V1-M, V6-M, V10-M | 0.92 to 0.95 | 0.92 to 0.99 | 0.92 to 0.95 |
| Default NativeAOT | mask kernel, versions 1, 6, 10 | 0.93 to 0.94 | 0.89 to 0.90 | 0.88 to 0.91 |
| | E2E, V1-L, V1-M, V6-M, V10-M | 0.94 to 0.97 | 0.90 to 0.92 | 0.91 to 0.93 |
| WebAssembly AOT | mask kernel, versions 1, 6, 10 | 0.94 to 0.97 | 0.91 to 0.93 | 0.90 to 0.92 |
| | E2E, V1-L, V1-M, V6-M, V10-M | 0.96 to 0.99 | 0.93 to 0.95 | 0.93 to 0.97 |

On default NativeAOT and WebAssembly one buffer fewer beat the shared terms alone by 2 to 6 %. On the JIT without AVX2 (`DOTNET_EnableAVX=0`) its mask kernel read about 3 % slower than the shared terms alone (1 to 2 % on the lowest run medians) and its end-to-end rows level, the lowest run medians favouring neither. The 32-row size added nothing on any build. The scalar single-word tier was measured with 32-row buffers too, against the shared terms alone, and showed no difference outside its runs' spread, so it keeps its 64-row buffers. Its complement buffer now holds the light runs, so it has no buffer to drop.

The scalar tiers, which the 128-bit builds run from version 12, read on those builds:

| Build | Mask kernel, versions 20 and 40 | E2E, V19-M | E2E, V40-L |
|---|---|---|---|
| JIT without AVX2 | 0.84 | 0.85 | 0.86 |
| Default NativeAOT | 0.81 | 0.82 | 0.83 |
| WebAssembly AOT | 0.76 to 0.78 | 0.80 | 0.84 |

Every variant held to the scalar scorer in the timing mode's parity check (`--parity`) on its build before it was timed, and the 7,647-symbol corpus hashed the same before and after under the AVX2 tiers, with AVX off and with hardware intrinsics off (`DOTNET_EnableHWIntrinsic=0`, the scalar tiers everywhere).

## Phase 4: the pinned mask

Taken on 2026-10-03 the way phase 3's were: base is `main` (c20906e), change is the phase's tree, seven alternating rounds per build, each process pinned to one CCD, a cell the median of the run medians. Both sides ran the same harness, with the forced rows taking their pattern as a variable (below). Micro QR M4-M and rMQR R17x139-M read 0.98 to 1.02 on every build, and the automatic selection rows, which the phase does not touch, 0.98 to 1.05.

Where F23's time went, at version 26-M (1,000 characters, quiet zone 0, the JIT with AVX2, on `main`). The encode rows are the base side of the A/B below. The stage rows are one process that timed the selection beside the pinned path, with the pattern given both ways:

| Row | Time |
|---|---|
| Encode, automatic | 16.0 µs |
| Encode, pattern 0 pinned | 34.3 µs |
| Encode, pattern 3 pinned | 33.7 µs |
| Mask selection alone (eight patterns scored, the winner applied) | 8.8 µs |
| `ApplyMaskPattern` alone, pattern given as a literal | 10.9 µs (0), 13.7 µs (3) |
| `ApplyMaskPattern` alone, pattern from a field | 20.6 µs (0), 28.2 µs (3) |

The pinned path was the predicate tested module by module. The stage harness had passed the pattern as a literal, so the JIT inlined the call into the row and folded the predicate for that one pattern, and the row read about half of what an encode pays. That is why phase 1's stage table showed the forced path slower than selection only around version 6. With both patterns in one process the field-fed rows read 26.0 and 26.5 µs, and the encode rows' 18 µs over automatic is that path's cost over the selection's.

JIT, x86-64-v4. The mask kernels on a placed matrix, base to change, and the automatic selection beside them:

| Version | Pinned pattern 3 | Ratio | Automatic selection |
|---|---|---|---|
| 1 | 546 to 144 ns | 0.26 | 611 ns |
| 6 | 2.90 µs to 229 ns | 0.08 | 1.16 µs |
| 10 | 5.75 µs to 444 ns | 0.08 | 1.57 µs |
| 12 | 7.61 µs to 531 ns | 0.07 | 5.80 µs |
| 20 | 17.6 µs to 906 ns | 0.05 | 8.18 µs |
| 27 | 29.6 to 1.98 µs | 0.07 | 11.7 µs |
| 28 | 31.1 to 1.50 µs | 0.05 | 33.4 µs |
| 40 | 59.7 to 3.06 µs | 0.05 | 46.5 µs |

End to end, quiet zone 0:

| Shape | Automatic | Pattern 0, base to change | Pattern 3, base to change |
|---|---|---|---|
| V1-L, 10 digits | 736 ns | 736 to 342 ns | 726 to 336 ns |
| V1-M, 16 alphanumeric | 792 ns | 732 to 331 ns | 719 to 329 ns |
| V6-M, URL | 1.94 µs | 3.70 µs to 985 ns | 3.61 µs to 985 ns |
| V10-M, 300 alphanumeric | 3.38 µs | 7.54 to 2.16 µs | 7.34 to 2.17 µs |
| V19-M, 620 bytes | 12.2 µs | 20.1 to 5.03 µs | 19.7 to 5.10 µs |
| V26-M, 1,000 bytes (F23) | 16.2 µs | 34.3 to 7.90 µs | 33.7 to 7.86 µs |
| V39-H, 1,200 bytes | 56.8 µs | 68.8 to 14.1 µs | 67.8 to 14.2 µs |
| V40-L, 2,900 bytes | 58.2 µs | 74.5 to 16.0 µs | 73.4 to 16.3 µs |

A pinned encode now takes 0.25 to 0.64 of the automatic one. Before, it was slower at every version measured from 6 up and level at version 1.

Default NativeAOT, WebAssembly AOT and the JIT without AVX2 apply the pinned pattern 8 modules a step (SWAR), and select masks with the Vector128 tier for versions 1 to 11 and the scalar tiers above. Pattern 3, base to change:

| Build | Mask kernel, versions 6 to 40 | Mask kernel, version 1 | Pinned encode against base | Pinned encode against automatic |
|---|---|---|---|---|
| JIT without AVX2 | 0.11 to 0.14 | 0.28 | 0.29 to 0.48 | 0.18 to 0.52 |
| Default NativeAOT | 0.09 to 0.12 | 0.22 | 0.24 to 0.41 | 0.16 to 0.47 |
| WebAssembly AOT | 0.13 to 0.17 | 0.38 | 0.37 to 0.69 | 0.24 to 0.60 |

The version 40 kernel took 6.1 to 6.9 µs on these builds, against 61.7 to 67.2 µs for their automatic selection. Before the change the pinned path on these builds was slower than their selection at versions 6 and 10, faster at versions 1, 20 and 27, and within 11 % either way at version 40.

After phase 5. The transposed prototype ran selection at 7.76 µs (version 20), 10.0 µs (27) and 20.9 µs (40), and versions 1 to 11 keep the lane-per-pattern tier, so the pinned kernel (0.14 to 3.06 µs on the JIT with AVX2) stays under selection there too.

The two-word sizes pay for their tails. Version 27's 125 columns leave 29 modules past the last 32-module step and take 1.98 µs, version 28's 129 leave one and take 1.50 µs.

Output was identical before and after on the 7,647-symbol corpus and on a pinned-mask corpus of 6,504 symbols (versions 1 to 40, every level, every pattern, two contents, quiet zone 0 and 4, the class API), under the AVX2 tiers, with AVX off and with hardware intrinsics off.

## Phase 5: the transposed scorer

Taken on 2026-10-04 the way phase 4's were, with the .NET build servers shut down before each A/B: base is `main` (cbe7e57), change is the phase's tree, nine alternating rounds per build on the JIT with AVX2, each process pinned to one CCD, a cell the median of the run medians. The rows the phase does not touch, Micro QR M4-M, rMQR R17x139-M and the version 1 and 10 encodes (the single-word tier), read 0.96 to 1.01.

Mask selection on a placed matrix (`kernel/MaskCode-vN`), the phase 3 SoA tiers against the transposed tier, at every version it serves:

| Version | Phase 3 | Transposed | Ratio |
|---|---|---|---|
| 12 | 5.49 µs | 4.55 µs | 0.83 |
| 13 | 5.87 µs | 4.85 µs | 0.83 |
| 14 | 5.39 µs | 4.74 µs | 0.88 |
| 15 | 6.38 µs | 5.44 µs | 0.85 |
| 16 | 6.75 µs | 5.64 µs | 0.84 |
| 17 | 7.38 µs | 6.41 µs | 0.87 |
| 18 | 7.54 µs | 6.33 µs | 0.84 |
| 19 | 8.26 µs | 7.32 µs | 0.89 |
| 20 | 7.89 µs | 7.14 µs | 0.90 |
| 21 | 8.28 µs | 7.29 µs | 0.88 |
| 22 | 8.54 µs | 7.16 µs | 0.84 |
| 23 | 8.88 µs | 7.80 µs | 0.88 |
| 24 | 9.32 µs | 8.37 µs | 0.90 |
| 25 | 10.5 µs | 8.86 µs | 0.85 |
| 26 | 10.2 µs | 8.40 µs | 0.82 |
| 27 | 11.3 µs | 9.66 µs | 0.85 |
| 28 | 33.4 µs | 12.4 µs | 0.37 |
| 29 | 32.8 µs | 13.6 µs | 0.42 |
| 30 | 34.6 µs | 13.5 µs | 0.39 |
| 31 | 36.4 µs | 14.4 µs | 0.40 |
| 32 | 35.4 µs | 13.9 µs | 0.39 |
| 33 | 35.2 µs | 15.7 µs | 0.44 |
| 34 | 39.1 µs | 15.3 µs | 0.39 |
| 35 | 41.8 µs | 16.5 µs | 0.39 |
| 36 | 39.1 µs | 15.4 µs | 0.39 |
| 37 | 41.0 µs | 16.7 µs | 0.41 |
| 38 | 45.0 µs | 16.4 µs | 0.36 |
| 39 | 43.0 µs | 18.2 µs | 0.42 |
| 40 | 45.4 µs | 18.3 µs | 0.40 |

The two-word versions (12 to 27) run at 0.82 to 0.90 of the phase 3 tier, the three-word ones (28 to 40) at 0.36 to 0.44. Phase 3's three-word tier cost about three times its two-word one at the boundary (33.4 against 11.3 µs), because every shifted term spans three words, and the transposed tier has no such step (12.4 against 9.66 µs).

End to end, quiet zone 0, with the mask stage alone beside it:

| Shape | E2E base | E2E change | Ratio | Mask base | Mask change | Ratio |
|---|---|---|---|---|---|---|
| V19-M, 620 bytes | 12.0 µs | 11.0 µs | 0.92 | 8.17 µs | 7.04 µs | 0.86 |
| V26-M, 1,000 bytes | 16.1 µs | 14.5 µs | 0.90 | 9.92 µs | 8.16 µs | 0.82 |
| V39-H, 1,200 bytes | 56.6 µs | 29.0 µs | 0.51 | 45.1 µs | 18.5 µs | 0.41 |
| V40-L, 2,900 bytes | 56.3 µs | 30.5 µs | 0.54 | 43.2 µs | 17.8 µs | 0.41 |
| V40-L, 4,296 alphanumeric | 62.7 µs | 38.1 µs | 0.61 | | | |
| V40-L, 7,089 digits | 60.2 µs | 34.1 µs | 0.57 | | | |

Each design choice was measured against the tier as shipped, on the mask kernel at versions 12, 16, 20, 24 and 27 and at 28, 32, 36 and 40 (seven or nine rounds):

| Variant | Versions 12 to 27 | Versions 28 to 40 |
|---|---|---|
| Full per-pattern tables, instead of templates ANDed with the allowed rows | 0.96 to 0.98 | 0.97 to 0.99 |
| The prototype's scalar 64x64 transpose | 1.07 to 1.16 | 1.08 to 1.14 |
| No abort checkpoint | 1.02 to 1.07 | 1.00 to 1.02 |
| The checkpoint after the row planes, instead of before the column planes' finder windows | 1.01 to 1.06 | 1.00 to 1.02 |

Full tables take about eight times the memory for 1 to 4 %, so the periodic form ships. A checkpoint after the row planes almost never fires: a candidate stops there only when its row-plane half alone exceeds the best candidate's whole score. Before the last finder windows only a small term is left, as in phase 3's tiers. An abort check after each row word read level with the single check (0.99 to 1.02 on the lowest run medians).

Two earlier tuning steps, from single-process runs of both tiers. The masking pass first ran over every entry of a plane (128 at version 12, for 65 rows), and limiting it to the rows the rules read, the symbol and nine past it, took versions 12 to 27 from 0.90 to 1.00 of phase 3 to about 0.82. The vector transpose and the periodic tables were in from the first build.

Table memory per version, built on first use: the unblocked modules of each row and each column, two words per row and column for versions 12 to 27 and three for 28 to 40, each plane padded to a whole 64-row block. That is 4,128 bytes for versions 12 to 26, 4,640 at 27, 9,264 for 28 to 39 and 9,456 at 40. The template planes, 12 rows and 12 columns per pattern, are 4,608 bytes once. Each call rents its scratch from the array pool, 12.5 to 25.3 KB, and returns it.

Output was identical before and after on the 7,647-symbol corpus and the 6,504-symbol pinned-mask corpus.

## Phase 6: the transposed scorer on 128-bit builds

Taken on 2026-10-05 the way phase 5's were, with the .NET build servers shut down before each run and each process pinned to one CCD, a cell the median of the run medians. Four builds run the 128-bit tier: the JIT without AVX2 (`DOTNET_EnableAVX=0`), a default NativeAOT publish (its baseline has no AVX), and the WebAssembly report AOT-compiled and interpreted under Node.js. Before this phase all four selected masks for versions 12 to 40 with the scalar bit-packed kernel.

Mask selection on a placed matrix, the scalar kernel (`kernel/MaskCode-vN-scalar`) against the 128-bit transposed tier entered directly (`kernel/MaskCode-vN-v128`), both in one process, five rounds (three interpreted), with the NativeAOT times beside the ratios:

| Version | Scalar, NativeAOT | Transposed, NativeAOT | JIT without AVX2 | Default NativeAOT | WebAssembly AOT | Interpreted |
|---|---|---|---|---|---|---|
| 12 | 22.6 µs | 10.6 µs | 0.44 | 0.47 | 0.59 | 0.45 |
| 13 | 24.1 µs | 10.9 µs | 0.43 | 0.45 | 0.56 | 0.43 |
| 14 | 25.4 µs | 11.2 µs | 0.41 | 0.44 | 0.52 | 0.41 |
| 15 | 27.1 µs | 12.2 µs | 0.41 | 0.45 | 0.52 | 0.41 |
| 16 | 28.5 µs | 13.2 µs | 0.42 | 0.46 | 0.52 | 0.41 |
| 17 | 30.2 µs | 14.5 µs | 0.43 | 0.48 | 0.54 | 0.43 |
| 18 | 31.5 µs | 14.7 µs | 0.43 | 0.47 | 0.52 | 0.42 |
| 19 | 33.2 µs | 15.5 µs | 0.43 | 0.47 | 0.52 | 0.42 |
| 20 | 34.6 µs | 16.3 µs | 0.43 | 0.47 | 0.53 | 0.41 |
| 21 | 36.2 µs | 17.2 µs | 0.43 | 0.47 | 0.53 | 0.41 |
| 22 | 37.5 µs | 17.6 µs | 0.43 | 0.47 | 0.52 | 0.40 |
| 23 | 39.4 µs | 17.5 µs | 0.41 | 0.44 | 0.50 | 0.39 |
| 24 | 40.7 µs | 18.9 µs | 0.43 | 0.47 | 0.52 | 0.40 |
| 25 | 42.5 µs | 20.3 µs | 0.44 | 0.48 | 0.53 | 0.41 |
| 26 | 43.8 µs | 20.0 µs | 0.42 | 0.46 | 0.50 | 0.39 |
| 27 | 45.7 µs | 21.3 µs | 0.43 | 0.47 | 0.51 | 0.41 |
| 28 | 47.1 µs | 31.0 µs | 0.61 | 0.66 | 0.71 | 0.57 |
| 29 | 48.8 µs | 32.5 µs | 0.63 | 0.67 | 0.71 | 0.58 |
| 30 | 50.1 µs | 33.2 µs | 0.62 | 0.66 | 0.71 | 0.57 |
| 31 | 52.1 µs | 33.8 µs | 0.61 | 0.65 | 0.69 | 0.56 |
| 32 | 53.4 µs | 34.2 µs | 0.61 | 0.64 | 0.69 | 0.56 |
| 33 | 55.3 µs | 36.4 µs | 0.63 | 0.66 | 0.70 | 0.57 |
| 34 | 56.7 µs | 36.9 µs | 0.63 | 0.65 | 0.69 | 0.56 |
| 35 | 58.6 µs | 37.6 µs | 0.62 | 0.64 | 0.68 | 0.56 |
| 36 | 60.0 µs | 38.8 µs | 0.63 | 0.65 | 0.69 | 0.56 |
| 37 | 62.1 µs | 39.6 µs | 0.62 | 0.64 | 0.68 | 0.56 |
| 38 | 63.4 µs | 40.8 µs | 0.62 | 0.64 | 0.69 | 0.56 |
| 39 | 65.6 µs | 42.4 µs | 0.63 | 0.65 | 0.69 | 0.56 |
| 40 | 67.1 µs | 43.0 µs | 0.62 | 0.64 | 0.68 | 0.56 |

The tier took 0.39 to 0.59 of the scalar kernel's time at versions 12 to 27 and 0.56 to 0.71 at 28 to 40, on every build at every version. The step at version 28 is the tier's: the scalar kernel then worked on three words at every version from 12, so its time grew evenly, while the transposed tier pays for the words a row has, two up to version 27 and three from 28. The scalar kernel that replaced it in the phase 9 follow-up reads two words up to version 27 and steps at 28 too ("Phase 9 follow-up", "From the review"). The SoA scorers ported to the same vectors in the 128-bit tiers round (2026-09-30), two rows per vector, took 0.94 to 1.68 of the scalar kernel's time on default NativeAOT and 1.40 to 5.80 on WebAssembly.

End to end at quiet zone 0 and the mask stage alone, base (`main`, 5444c64) to change, seven alternating rounds per build on the JIT and NativeAOT, five on WebAssembly AOT and three interpreted, with the NativeAOT times beside the ratios:

| Shape | NativeAOT base | NativeAOT change | JIT without AVX2 | Default NativeAOT | WebAssembly AOT | Interpreted |
|---|---|---|---|---|---|---|
| V19-M, 620 bytes | 37.4 µs | 20.0 µs | 0.49 | 0.54 | 0.61 | 0.50 |
| V26-M, 1,000 bytes | 50.9 µs | 26.4 µs | 0.49 | 0.52 | 0.59 | 0.48 |
| V39-H, 1,200 bytes | 78.4 µs | 55.8 µs | 0.68 | 0.71 | 0.76 | 0.64 |
| V40-L, 2,900 bytes | 81.9 µs | 58.4 µs | 0.67 | 0.71 | 0.77 | 0.64 |
| V40-L, 4,296 alphanumeric | 87.6 µs | 64.2 µs | 0.70 | 0.73 | 0.80 | 0.69 |
| V40-L, 7,089 digits | 85.9 µs | 60.9 µs | 0.68 | 0.71 | 0.77 | 0.63 |
| Mask stage, V19-M | 33.1 µs | 15.6 µs | 0.42 | 0.47 | 0.53 | 0.43 |
| Mask stage, V26-M | 43.8 µs | 19.3 µs | 0.40 | 0.44 | 0.49 | 0.39 |
| Mask stage, V39-H | 65.5 µs | 42.4 µs | 0.61 | 0.65 | 0.69 | 0.57 |
| Mask stage, V40-L | 66.8 µs | 43.0 µs | 0.61 | 0.64 | 0.69 | 0.55 |

The rows the phase does not touch, the version 10 encode (the single-word tier), Micro QR M4 and rMQR R17x139, read 0.99 to 1.04. The pinned-mask encodes at versions 19 and 40 read 1.00 to 1.03 and stay under the automatic ones: 0.31 to 0.47 of their time after the change, against 0.17 to 0.34 before. On default NativeAOT the V40-L encode took 94 µs at phase 1 (mask selection 77.5 µs) and 58.4 µs now (43.0 µs).

The timing mode's parity check, which now runs every version 1 to 40 through the dispatch and the 128-bit tier entered directly, matched on all four builds.

These numbers are .NET 10's. On .NET 8 the transpose's one-row lane swap, `Vector128.Shuffle` with its index in a local, was not lowered to an instruction: the .NET 8 JIT does that only for an index it sees as a constant where it imports the call, and called the software `Shuffle` twice a row pair instead (x64, `DOTNET_TieredCompilation=0` disassembly; .NET 10 emits `vpermilpd`). Found in review (2026-10-05) and fixed by writing the index at each call. On .NET 8 x64 without AVX2, where the dispatch takes this tier, a small net8.0 harness (mask selection on a placed matrix, seven alternating rounds of one process per build, pinned) read 0.88 to 0.92 of the time before the fix at versions 12 to 40 through the dispatch; the AVX2 dispatch, which does not run it, read 0.97 to 1.07. ARM64's transposed tier runs the same transpose. Read in the library's own `Transpose64Vector128` on .NET 8.0.31 for ARM64, before the fix and after it (2026-10-05): NativeAOT 8 made the same two calls a row pair (`bl Vector128:Shuffle`), and two `tbl` after the fix. The JIT made no call. Its tier-1 code inlined the software `Shuffle`, storing the vector and its index to the stack and reading each lane back after a check on its index, and after the fix it is two `tbl` as well. Mask selection through the dispatch, the timing mode built for net8.0 on the Apple M2, before the fix against after it, five alternating rounds per build, one process each:

| Version | JIT 8, before | JIT 8, after | NativeAOT 8, before | NativeAOT 8, after |
|---|---|---|---|---|
| 1 | 1.9 µs | 0.98 | 1.8 µs | 1.01 |
| 6 | 3.0 µs | 0.98 | 3.0 µs | 1.01 |
| 10 | 4.3 µs | 0.96 | 4.3 µs | 0.98 |
| 12 | 8.2 µs | 0.95 | 8.8 µs | 0.92 |
| 13 | 9.0 µs | 0.95 | 9.9 µs | 0.91 |
| 14 | 8.7 µs | 0.93 | 9.3 µs | 0.93 |
| 15 | 9.7 µs | 0.96 | 10.6 µs | 0.93 |
| 16 | 9.6 µs | 0.95 | 10.3 µs | 0.95 |
| 17 | 11.4 µs | 0.95 | 12.5 µs | 0.92 |
| 18 | 11.2 µs | 0.95 | 11.9 µs | 0.90 |
| 19 | 12.5 µs | 0.94 | 13.4 µs | 0.93 |
| 20 | 12.2 µs | 0.98 | 13.3 µs | 0.93 |
| 21 | 13.7 µs | 0.97 | 15.5 µs | 0.93 |
| 22 | 13.6 µs | 0.96 | 14.6 µs | 0.94 |
| 23 | 14.3 µs | 0.95 | 15.3 µs | 0.97 |
| 24 | 14.0 µs | 0.97 | 15.8 µs | 0.95 |
| 25 | 16.6 µs | 0.96 | 17.9 µs | 0.95 |
| 26 | 15.2 µs | 1.00 | 16.8 µs | 0.95 |
| 27 | 17.5 µs | 0.97 | 19.5 µs | 0.94 |
| 28 | 24.4 µs | 0.94 | 25.6 µs | 0.94 |
| 29 | 26.3 µs | 0.98 | 28.0 µs | 0.93 |
| 30 | 25.5 µs | 0.97 | 27.7 µs | 0.91 |
| 31 | 27.8 µs | 1.00 | 29.4 µs | 0.93 |
| 32 | 26.7 µs | 0.99 | 28.7 µs | 0.93 |
| 33 | 29.7 µs | 1.00 | 31.3 µs | 0.95 |
| 34 | 28.6 µs | 1.01 | 30.9 µs | 0.95 |
| 35 | 31.6 µs | 0.99 | 32.9 µs | 0.93 |
| 36 | 31.2 µs | 0.97 | 32.3 µs | 0.96 |
| 37 | 32.5 µs | 0.99 | 34.1 µs | 0.98 |
| 38 | 32.9 µs | 0.98 | 34.8 µs | 0.96 |
| 39 | 36.9 µs | 0.97 | 38.7 µs | 0.92 |
| 40 | 34.3 µs | 0.97 | 37.3 µs | 0.93 |

On NativeAOT 8 the fix took 0.90 to 0.98 of the time at versions 12 to 40, the median 0.93, while versions 1, 6 and 10, whose single-word tier has no transpose, read 0.98 to 1.01. On the JIT it read 0.93 to 1.01, the median 0.96 at versions 12 to 27 and 0.98 at 28 to 40, against 0.96 to 0.98 at versions 1, 6 and 10, so its gain there is inside the runs' drift: the inlined fallback cost little beside the calls.

### ARM64 (2026-10-05)

Phase 6 left ARM64 on its two- and three-word SoA tiers for want of a machine. Taken afterwards on an Apple M2 (osx-arm64, .NET 10.0.12) on the JIT and a default NativeAOT publish, with the .NET build servers shut down and base and change alternating one process each, a cell the median of the run medians. The Mac cannot pin a process.

The 128-bit tier already ran on ARM64 through portable vectors, with a SWAR popcount, movemask packing and the scalar eight-module unpack. Entered directly beside the SoA tiers (the dispatch) in one process, three rounds for the first row and four for the others, at versions 12 to 27 and 28 to 40:

| 128-bit transposed tier | JIT, 12-27 | JIT, 28-40 | NativeAOT, 12-27 | NativeAOT, 28-40 |
|---|---|---|---|---|
| As it stands | 1.30 to 1.39 | 0.62 to 0.65 | 1.24 to 1.32 | 0.72 to 0.78 |
| NEON's cnt and uadalp for the popcount | 0.89 to 0.99 | 0.45 to 0.48 | 0.94 to 1.01 | 0.52 to 0.55 |
| The same, rows packed and the winner unpacked sixteen modules a step (the NEON tier's helpers) | 0.85 to 0.92 | 0.43 to 0.45 | 0.88 to 0.96 | 0.51 to 0.53 |

The last row shipped as ARM64's tier for versions 12 to 40, in place of the SoA tiers. Mask selection on a placed matrix, base `encode4` (a740295) to change, five alternating rounds per build:

| Version | JIT, SoA tiers | JIT, transposed | NativeAOT, SoA tiers | NativeAOT, transposed |
|---|---|---|---|---|
| 12 | 8.2 µs | 0.94 | 8.5 µs | 0.97 |
| 13 | 9.2 µs | 0.92 | 9.9 µs | 0.90 |
| 14 | 9.1 µs | 0.91 | 9.7 µs | 0.87 |
| 15 | 10.5 µs | 0.92 | 11.0 µs | 0.88 |
| 16 | 10.2 µs | 0.94 | 11.0 µs | 0.90 |
| 17 | 11.8 µs | 0.95 | 12.3 µs | 0.96 |
| 18 | 11.5 µs | 0.94 | 11.4 µs | 0.98 |
| 19 | 13.1 µs | 0.95 | 13.4 µs | 0.94 |
| 20 | 12.6 µs | 0.92 | 13.1 µs | 0.95 |
| 21 | 14.2 µs | 0.95 | 14.7 µs | 0.95 |
| 22 | 14.0 µs | 0.94 | 14.3 µs | 0.95 |
| 23 | 15.7 µs | 0.90 | 16.2 µs | 0.91 |
| 24 | 14.9 µs | 0.92 | 15.9 µs | 0.93 |
| 25 | 16.4 µs | 0.97 | 17.4 µs | 0.98 |
| 26 | 16.5 µs | 0.91 | 16.9 µs | 0.93 |
| 27 | 18.2 µs | 0.94 | 19.5 µs | 0.93 |
| 28 | 53.5 µs | 0.44 | 46.3 µs | 0.52 |
| 29 | 57.2 µs | 0.44 | 50.2 µs | 0.55 |
| 30 | 57.0 µs | 0.43 | 48.3 µs | 0.53 |
| 31 | 60.1 µs | 0.45 | 51.7 µs | 0.56 |
| 32 | 60.2 µs | 0.42 | 52.3 µs | 0.52 |
| 33 | 63.9 µs | 0.45 | 55.1 µs | 0.57 |
| 34 | 63.7 µs | 0.43 | 54.3 µs | 0.53 |
| 35 | 67.8 µs | 0.45 | 58.8 µs | 0.55 |
| 36 | 67.8 µs | 0.43 | 58.5 µs | 0.53 |
| 37 | 70.6 µs | 0.46 | 62.4 µs | 0.56 |
| 38 | 71.1 µs | 0.45 | 68.9 µs | 0.48 |
| 39 | 74.9 µs | 0.47 | 65.9 µs | 0.57 |
| 40 | 73.1 µs | 0.46 | 64.8 µs | 0.53 |

At every version the change took less time, 0.87 to 0.98 at 12 to 27 and 0.42 to 0.57 at 28 to 40. The SoA tiers stepped by 2.9 times from version 27 to 28 (18.2 to 53.5 µs on the JIT), where the three-word tier begins, and the transposed tier by 1.3 to 1.4 times. Versions 1 and 6, the single-word tier, read 1.00 to 1.04.

End to end at quiet zone 0, seven alternating rounds per build:

| Shape | JIT base | JIT change | NativeAOT base | NativeAOT change |
|---|---|---|---|---|
| V19-M, 620 bytes | 17.9 µs | 0.89 | 17.6 µs | 0.93 |
| V26-M, 1,000 bytes | 25.1 µs | 0.88 | 23.9 µs | 0.92 |
| V39-H, 1,200 bytes | 89.7 µs | 0.55 | 79.0 µs | 0.63 |
| V40-L, 2,900 bytes | 93.8 µs | 0.56 | 82.2 µs | 0.62 |
| V40-L, 2,900 bytes, class API | 98.3 µs | 0.58 | 85.5 µs | 0.64 |
| V40-L, 4,296 alphanumeric | 97.3 µs | 0.55 | 84.4 µs | 0.63 |
| V40-L, 7,089 digits | 96.6 µs | 0.55 | 84.8 µs | 0.60 |
| Set, 30,000 alphanumeric | 826.4 µs | 0.64 | 735.9 µs | 0.70 |

The rows the change does not touch read 0.97 to 1.02: versions 1, 6 and 10, the pinned-mask encodes at versions 26 and 40, Micro QR M4 and rMQR R17x139. A first NativeAOT run was set aside, since those rows read 0.91 to 0.94 in it, and the table is a second one. The timing mode's parity check matched on both builds, and 4,832 Standard QR symbols (lengths 0 to 64 and 70 to 7,329 of digits, the alphanumeric set and ASCII at every level, quiet zone 0 and 4, the class API, every version forced, every pinned pattern at every version) hashed the same before and after, and with hardware intrinsics off.

With the encode shorter, the writers' share of it grew. The payload row over the end-to-end row with the portable writers, from the base side (`encode4`, 0fda781) of the NEON steps' end-to-end run below (Phase 7, ARM64), nine alternating rounds per build, beside phase 7's ARM64 table, taken with the SoA scorers:

| Shape | JIT, SoA scorers | JIT, transposed | NativeAOT, SoA scorers | NativeAOT, transposed |
|---|---|---|---|---|
| V1-M, 16 alphanumeric | 0.7 % | 0.7 % | 0.7 % | 0.7 % |
| V10-M, 300 alphanumeric | 3.0 % | 2.9 % | 3.2 % | 3.1 % |
| V40-L, 4,296 alphanumeric | 2.4 % | 4.2 % | 2.6 % | 4.2 % |
| Set, 30,000 alphanumeric | 2.0 % | 3.0 % | 2.2 % | 3.0 % |
| V1-L, 10 digits | 0.4 % | 0.4 % | 0.4 % | 0.5 % |
| V40-L, 7,089 digits | 1.9 % | 3.5 % | 2.1 % | 3.5 % |
| Set, 50,000 digits | 1.6 % | 2.6 % | 1.7 % | 2.6 % |

At version 10, whose single-word tier did not change, the share did not move. A first run of three rounds, each build in its own process and not alternated, had read 4.2 to 4.3 % at version 40 alphanumeric, 3.5 to 3.7 % numeric, 2.6 to 3.1 % for the sets and 3.1 to 3.3 % at version 10. The version 40 shares are over the 3 % bar, so the NEON steps of phase 7's ARM64 follow-up were measured again, built into the library and end to end ("The NEON steps end to end").

## A transposed scorer for versions 12 to 40

Row-direction penalty rules on rows wider than one word pull bits across words for every shifted term. In the three-word tier each such shift is five shifts and two ORs. Column-direction rules need no shift, because they combine whole row words of neighbouring rows.

The prototype holds the masked candidate twice: as row words and as column words (the transpose). Every run and finder-like rule then runs in the column direction on both, which in the transpose is the row direction of the symbol. Masking is an XOR, so the transposed candidate is the transposed data XOR a per-version transposed template, and the data is transposed once per symbol, not once per pattern. Format and version bits are per-version overlays in both orientations. The 2x2 rule keeps one one-bit shift per row word. Balance is a popcount of the row words.

It was checked on 240 matrices (versions 1 to 40, six inputs each, one of them all light data, every ECC level): all eight scores equal a textbook byte-matrix scorer, and the chosen pattern and masked bytes equal the library's. Its source, with the check and the benchmark, is [encode-performance-transposed-scorer.cs](encode-performance-transposed-scorer.cs), kept for reference and compiled by no project.

| Version | Library mask selection | Prototype | Ratio |
|---|---|---|---|
| 1 | 517 ns | 1.47 µs | 2.85 |
| 6 | 1.05 µs | 1.95 µs | 1.86 |
| 10 | 1.58 µs | 2.58 µs | 1.63 |
| 20 | 18.7 µs | 7.76 µs | 0.41 |
| 27 | 27.3 µs | 10.0 µs | 0.37 |
| 40 | 59.4 µs | 20.9 µs | 0.35 |

The library column is current `main`, without the three small changes. Against the shared-window tiers the prototype is about 0.75 around version 20 (its version 20 against their version 19) and 0.47 at version 40. Below version 12 a row is one word, shifts are single instructions, and the lane-per-pattern tier stays faster. Versions 12 to 19 were not measured.

The prototype is not tuned. It transposes 64x64 blocks with scalar code, has no early-abort checkpoint, and counts bits with the nibble-table popcount, which by operation count is estimated at over half of its work. Its full per-pattern tables take about 75 KB per version at version 40, against about 95 KB for the placement layout. Masks are periodic in 12 rows and 12 columns, so per-version allowed rows ANDed with 12-periodic templates would be much smaller.

## The Alphanumeric and Numeric writers

These answered phase 1 of `standardqr-binary-encoder-plan.md` before the mask work (2026-10-02); phase 7 below has it re-taken. The writer row is the whole data codeword stage (mode, count, payload, padding) for single symbols, and the payload writer alone for Structured Append sets.

| Shape | Writer | Encode | Share |
|---|---|---|---|
| V1-M, 16 alphanumeric | 26.6 ns | 840 ns | 3.2 % |
| V10-M, 300 alphanumeric | 298 ns | 3.22 µs | 9.3 % |
| V40-L, 4,296 alphanumeric | 4.28 µs | 77.7 µs | 5.5 % |
| V40-L, 7,089 digits | 3.66 µs | 70.3 µs | 5.2 % |
| Structured Append, 30,000 alphanumeric (7 symbols at L) | 29.7 µs | 733 µs | 4.1 % |
| Structured Append, 50,000 digits (L) | 25.7 µs | 709 µs | 3.6 % |

The Alphanumeric writer ran at about 1.0 ns a character and the Numeric writer at about 0.52 ns a digit. Every share is above that plan's 3 % bar. Each is a ceiling that grows as mask selection shrinks. With the shared windows alone the V40-L alphanumeric share is about 6 %, and with a scorer at the prototype's speed it would be about 10 % (an estimate from the stage rows, not measured).

## Phase 7: the writers (2026-10-05)

The writer plan, run as phase 7. Its phase 1, re-taken after the mask work: the payload row (the writer alone, no mode, count or padding) over the end-to-end row of the stage harness (span API at quiet zone 0, `CreateStructuredAppend` for sets). Five rounds on the JIT and NativeAOT, three on WebAssembly, every shape in one process pinned to one CCD, a cell the median of the run medians:

| Shape | JIT, AVX2 | JIT, no AVX | Default NativeAOT | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|---|
| V1-M, 16 alphanumeric | 2.9 % | 1.9 % | 1.7 % | 4.4 % | 5.3 % |
| V10-M, 300 alphanumeric | 10.7 % | 7.4 % | 7.0 % | 15.8 % | 17.8 % |
| V40-L, 4,296 alphanumeric | 13.0 % | 9.5 % | 8.6 % | 20.0 % | 21.8 % |
| Set, 30,000 alphanumeric (7 symbols at L) | 6.2 % | 5.5 % | 5.1 % | 11.0 % | 11.2 % |
| V1-L, 10 digits | 1.2 % | 0.8 % | 0.9 % | 2.0 % | 2.6 % |
| V40-L, 7,089 digits | 11.2 % | 7.0 % | 6.4 % | 8.1 % | 6.6 % |
| Set, 50,000 digits (8 symbols at L) | 5.4 % | 4.1 % | 4.0 % | 4.3 % | 3.9 % |

The Alphanumeric writer took 4.81 µs for 4,296 characters on the JIT (1.12 ns a character) and 15.7 µs on WebAssembly AOT (3.66 ns), the Numeric writer 3.96 µs for 7,089 digits (0.56 ns a digit). The payload row read within 1.4 % of the whole data stage at V40-L and 3.8 % at V10-M.

### The variant ladder

BenchmarkDotNet on the JIT with AVX2 (3 warmups, 15 iterations, the process pinned to one CCD), each writer copied verbatim as the baseline with a byte-identical canary beside it, every variant held to the baseline's stream before any timing (lengths 0 to 300 and long runs, every starting alignment, and for Alphanumeric a character outside the alphabet at every position). Ratios to the baseline in the same run, the run starting at a byte boundary (13 bits in read the same):

| Alphanumeric variant | 4,296 | 300 | 40 | 9 |
|---|---|---|---|---|
| Baseline | 4.61 µs | 334 ns | 47.7 ns | 12.7 ns |
| Canary | 1.01 | 1.04 | 0.95 | 1.01 |
| A value table, validity checked a pair | 1.20 | 0.83 | 0.78 | 0.84 |
| Two pairs an append | 0.66 | 0.63 | 0.63 | 0.82 |
| Four pairs a 44-bit append | 0.57 | 0.56 | 0.60 | 0.77 |
| The same, read through a ref | 0.54 | 0.54 | 0.58 | 0.76 |
| Five pairs a 55-bit append | 0.56 | 0.55 | 0.58 | 0.85 |
| SSSE3, 8 characters a step (the rMQR form) | 0.29 | 0.29 | 0.34 | 0.61 |
| SSSE3, 16 characters a step | 0.23 | 0.24 | 0.29 | 0.55 |
| AVX2, 32 characters a step | 0.21 | 0.22 | 0.29 | 0.60 |
| Portable Vector128, 8 characters a step | 0.41 | 0.42 | 0.49 | 0.65 |

| Numeric variant | 7,089 | 500 | 40 | 10 |
|---|---|---|---|---|
| Baseline | 4.03 µs | 293 ns | 24.4 ns | 9.33 ns |
| Canary | 1.03 | 0.98 | 1.00 | 0.92 |
| Three groups a 30-bit append | 0.69 | 0.66 | 0.72 | 0.76 |
| Five groups a 50-bit append | 0.58 | 0.52 | 0.70 | 0.82 |
| The same, each group from one 8-byte load and a multiply | 0.48 | 0.47 | 0.63 | 0.97 |
| SSE4.1, 12 digits a step from four loads (the rMQR form) | 0.38 | 0.41 | 0.70 | 0.89 |
| SSSE3, 12 digits a step from one pack and a pshufb layout | 0.30 | 0.29 | 0.48 | 0.93 |
| AVX2, 24 digits a step | 0.29 | 0.29 | 0.49 | 1.00 |
| Portable Vector128, 12 digits a step | 0.41 | 0.42 | 0.55 | 0.98 |

A second round each. The sixteen-character loop on a local copy of the writer took 0.89 to 0.93 of its time at 300 and 4,296 characters, and the twelve-digit one 0.86 to 0.92 at 500 and 7,089, against 0.3 to 1.3 ns more at 9 to 16 characters for the copy. The same copy in the scalar four-pair loop read 0.95 to 1.05, inside the noise. Nine digits an append after the fifteen-digit loop took 0.84 to 0.92 of the time at 10 and 12 digits, where the fifteen-digit loop never runs, and read level from 40 up.

The value table alone read slower than the baseline at 4,296 characters only. There the baseline writer that handles a bad character was inlined into its loop, the method grew from about 500 to 1,202 bytes, and `BitWriter.Write` was left a call a pair.

### The shipped tiers against the writer they replaced

The timing mode's `kernel/AlnumWriter` and `kernel/NumericWriter` shapes: each tier entered directly beside a copy of the old writer, 13 bits into a word. Ratios at 4,296 / 300 / 40 / 16 characters and 7,089 / 500 / 40 / 12 digits:

| Build | Alphanumeric, vector tier | Alphanumeric, portable | Numeric, vector tier | Numeric, portable |
|---|---|---|---|---|
| JIT, AVX2 | 0.21 / 0.24 / 0.35 / 0.50 | 0.65 / 0.66 / 0.70 / 0.77 | 0.24 / 0.27 / 0.54 / 1.00 | 0.47 / 0.49 / 0.62 / 0.82 |
| JIT, no AVX | 0.18 / 0.20 / 0.31 / 0.44 | 0.51 / 0.52 / 0.57 / 0.60 | 0.24 / 0.27 / 0.52 / 1.10 | 0.44 / 0.45 / 0.56 / 1.00 |
| Default NativeAOT | 0.20 / 0.22 / 0.35 / 0.44 | 0.56 / 0.55 / 0.56 / 0.59 | 0.27 / 0.29 / 0.58 / 1.00 | 0.45 / 0.46 / 0.62 / 0.82 |
| WebAssembly AOT | 0.09 / 0.10 / 0.18 / 0.28 | 0.18 / 0.18 / 0.24 / 0.36 | (0.38 / 0.43 / 0.76 / 1.03) | 0.39 / 0.45 / 0.80 / 1.03 |
| WebAssembly interpreted | 0.14 / 0.16 / 0.29 / 0.43 | 0.18 / 0.19 / 0.34 / 0.46 | (0.71 / 0.76 / 0.96 / 1.10) | 0.43 / 0.49 / 0.87 / 1.12 |

The vector tiers are SSE4.1 (Alphanumeric) and SSSE3 (Numeric) on x64 and WebAssembly's SIMD for Alphanumeric. The bracketed Numeric step on WebAssembly did not ship: it took 0.95 to 0.96 of the portable writer's time AOT-compiled and 1.10 to 1.65 interpreted, from 40 to 7,089 digits, and one flag gates both builds. A twelve-digit run never reaches a Numeric vector tier, and since this run the dispatch writes runs under sixteen digits, and under eight characters, in place with no call. On WebAssembly AOT the old writer's copy ran slower than the library's own old writer (23.3 µs against 15.7 µs at 4,296 characters, the copy taking the writer by reference), so the A/B below is the measure there.

From review (2026-10-05), the timing mode's writer shapes at more lengths, seven rounds of one process per build, SSSE3 Numeric step over the portable writer (the timer reads whole nanoseconds, 9 to 22 ns a call here):

| Build | 16 | 20 | 24 | 28 | 32 | 36 | 40 | 48 | 64 |
|---|---|---|---|---|---|---|---|---|---|
| JIT, AVX2 | 1.22 | 1.09 | 1.08 | 0.80 | 1.08 | 1.00 | 0.81 | 0.94 | 0.77 |
| JIT, no AVX | 1.22 | 1.09 | 1.08 | 0.93 | 1.17 | 1.00 | 0.81 | 1.00 | 0.82 |
| Default NativeAOT | 1.20 | 1.18 | 1.08 | 0.93 | 1.15 | 1.07 | 0.88 | 1.00 | 0.82 |

The portable writer over the old writer's copy in the same runs:

| Build | 16 | 20 | 24 | 28 | 32 | 36 | 40 | 48 | 64 |
|---|---|---|---|---|---|---|---|---|---|
| JIT, AVX2 | 0.69 | 0.79 | 0.71 | 0.79 | 0.62 | 0.65 | 0.64 | 0.57 | 0.56 |
| JIT, no AVX | 0.64 | 0.73 | 0.71 | 0.70 | 0.55 | 0.67 | 0.62 | 0.52 | 0.55 |
| Default NativeAOT | 0.77 | 0.73 | 0.71 | 0.70 | 0.62 | 0.62 | 0.62 | 0.57 | 0.56 |

The step reads sixteen chars to write twelve, so below 40 digits it runs once or twice and hands the rest to the portable writer's call. The dispatch now sends a run to it from 40 digits; shorter runs take the portable writer, which took 0.55 to 0.79 of the old writer's time at 16 to 36 digits.

The portable Alphanumeric writer's body compiled on its own and inlined into its two-parameter entry (the shipped body, its throwing path out of the loop, `NoInlining` removed for the second), against the old writer's copy on the JIT with AVX2, seven alternating rounds: 0.71 against 0.81 at 16 characters, 0.67 against 0.71 at 40, 0.64 against 0.65 at 300, 0.63 at 4,296 for both. During the phase an earlier body, with the throwing path still in its loop, read 1.64 inlined against 0.77 on its own at 16 characters, with its 8-byte store left a call; that run was not kept as a table.

What the declined steps would have saved of an encode, from the tables above: in the variant ladder AVX2's 32 characters a step took 0.21 of the old writer's time against 0.23 for its row "SSSE3, 16 characters a step" at 4,296 characters (a ladder variant, before the local writer copy; the shipped SSE4.1 tier reads 0.21 in the table of shipped tiers), 0.02 of a writer that was 13.0 % of a version 40 alphanumeric encode, so about 0.26 %; AVX2's 24 digits a step 0.29 against 0.30, 0.01 of 11.2 %, about 0.11 %; WebAssembly's Numeric step 0.95 of the portable writer AOT-compiled, which took 0.39 of an old writer that was 8.1 % of a version 40 numeric encode, about 0.16 % (and a loss interpreted). So 0.1 to 0.3 %.

### End to end

Base `main` (9897279) against the change, the same stage harness on both sides, alternating rounds: seven on the JIT and NativeAOT, five on WebAssembly AOT, three interpreted. The median of the run medians, change over base:

| Shape | JIT, AVX2 | JIT, no AVX | Default NativeAOT | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|---|
| V1-M, 16 alphanumeric | 1.00 | 0.98 | 0.96 | 0.99 | 0.96 |
| V1-L, 10 digits | 1.00 | 1.00 | 0.98 | 0.99 | 1.00 |
| V10-M, 300 alphanumeric | 0.91 | 0.96 | 0.92 | 0.86 | 0.84 |
| V40-L, 4,296 alphanumeric | 0.93 | 0.93 | 0.92 | 0.83 | 0.86 |
| V40-L, 7,089 digits | 0.92 | 0.92 | 0.93 | 0.97 | 0.96 |
| Set, 30,000 alphanumeric | 0.97 | 0.95 | 0.92 | 0.91 | 0.92 |
| Set, 50,000 digits | 1.00 | 0.97 | 0.95 | 0.99 | 1.00 |
| Payload, V40-L alphanumeric | 0.20 | 0.18 | 0.19 | 0.12 | 0.14 |
| Payload, V40-L digits | 0.25 | 0.25 | 0.26 | 0.39 | 0.43 |
| Payload, V1-M alphanumeric | 0.50 | 0.46 | 0.52 | 0.45 | 0.56 |
| Payload, V1-L digits | 0.89 | 0.80 | 0.69 | 0.95 | 0.97 |

The rows the change does not touch (V19-M Byte, Micro QR M4, rMQR R17x139) read 0.96 to 1.03. On default NativeAOT the V40-L alphanumeric encode went from 64.9 to 59.8 µs and its payload from 5.63 to 1.06 µs; on WebAssembly AOT from 81.0 to 67.2 µs and from 16.7 to 2.05 µs.

Output was identical before and after on the 7,647-symbol corpus, the 6,504-symbol pinned-mask corpus, and a new pass of 980 symbols: `QRSegmentation.Optimal` encodes of text made of runs of 1 to 40 characters from the three alphabets at every level, and Structured Append sets of one mode and of mixed content under both segmentations. Each was hashed under AVX2, with AVX off and with hardware intrinsics off.

### ARM64 (2026-10-05)

Phase 7 ran without an ARM64 machine. These were taken afterwards on an Apple M2 (MacBook Air, osx-arm64, .NET 10.0.12) on the JIT and a default NativeAOT publish, with the phase's stage harness on both sides. The Mac cannot pin a process and carries background load, so base `main` (9897279) and the change (e463622) alternated one process each, nine rounds per build, a cell the median of the run medians. Only shapes of the two modes and three controls ran in each process.

The writers' share of an encode (the payload row over the end-to-end row), with the old writers and with the portable ones ARM64 now runs:

| Shape | JIT, old | JIT, portable | NativeAOT, old | NativeAOT, portable |
|---|---|---|---|---|
| V1-M, 16 alphanumeric | 1.6 % | 0.7 % | 1.6 % | 0.7 % |
| V10-M, 300 alphanumeric | 7.9 % | 3.0 % | 8.2 % | 3.2 % |
| V40-L, 4,296 alphanumeric | 7.0 % | 2.4 % | 7.8 % | 2.6 % |
| Set, 30,000 alphanumeric | 5.9 % | 2.0 % | 6.3 % | 2.2 % |
| V1-L, 10 digits | 0.5 % | 0.4 % | 0.6 % | 0.4 % |
| V40-L, 7,089 digits | 7.4 % | 1.9 % | 8.2 % | 2.1 % |
| Set, 50,000 digits | 6.2 % | 1.6 % | 6.7 % | 1.7 % |

The timing mode's writer kernels (five rounds, each build in its own process), the portable writer over the old writer's copy at 4,296 / 300 / 40 / 16 characters and 7,089 / 500 / 40 / 12 digits:

| Build | Alphanumeric, portable | Numeric, portable |
|---|---|---|
| JIT | 0.33 / 0.34 / 0.35 / 0.39 | 0.24 / 0.27 / 0.44 / 0.83 |
| Default NativeAOT | 0.33 / 0.36 / 0.37 / 0.44 | 0.24 / 0.28 / 0.45 / 0.83 |

The old Alphanumeric writer took 7.25 µs for 4,296 characters on the JIT (1.69 ns a character, against 1.12 on the x64 JIT) and the old Numeric writer 7.55 µs for 7,089 digits (1.06 ns a digit, against 0.56). The portable writers took 2.36 and 1.84 µs, no more than on x64 (about 3.1 and 1.9 µs there by the ratios above), so against the slower old writers they took a quarter to a third of the time here, where the Alphanumeric one took two thirds on x64.

End to end, change over base:

| Shape | JIT | Default NativeAOT |
|---|---|---|
| V1-M, 16 alphanumeric | 0.94 | 1.01 |
| V1-L, 10 digits | 0.95 | 1.00 |
| V10-M, 300 alphanumeric | 0.92 | 0.94 |
| V40-L, 4,296 alphanumeric | 0.96 | 0.96 |
| V40-L, 7,089 digits | 0.93 | 0.95 |
| Set, 30,000 alphanumeric | 0.97 | 0.97 |
| Set, 50,000 digits | 0.96 | 0.97 |
| Payload, V40-L alphanumeric | 0.32 | 0.33 |
| Payload, V40-L digits | 0.24 | 0.25 |
| Payload, V10-M alphanumeric | 0.35 | 0.36 |
| Payload, V1-M alphanumeric | 0.42 | 0.45 |
| Payload, V1-L digits | 0.73 | 0.75 |

The rows the change does not touch (V19-M Byte, Micro QR M4, rMQR R17x139) read 0.98 to 1.01. At version 1 the payload is under 2 % of the encode, so those rows' 0.94 to 1.01 is the spread, not the writers. ARM64 then still ran the SoA scorers for versions 12 to 40 (the transposed tier replaced them afterwards, "Phase 6", ARM64), and the stage rows of the change put mask selection at 77 to 79 % of a version 40 encode there (72.6 of 92.3 µs on the JIT, 63.9 of 82.5 µs on NativeAOT) and 68 to 71 % of the version 10 one, so the writers' share was smaller than it became.

#### A NEON step

BenchmarkDotNet on the JIT (3 warmups, 15 iterations), each variant held to the old writers' stream before any timing: lengths 0 to 300 and long runs at every alignment, runs of one repeated character, every character outside the alphabet up to 0x17F and six past it at every position of the first 40, and runs as slices of a longer valid text. Ratios to the portable writer in the same run, two rounds where a variant ran in both:

| Alphanumeric variant | 4,296 | 300 | 40 | 16 |
|---|---|---|---|---|
| Portable writer | 2.30 µs | 176 ns | 25.7 ns | 12.3 ns |
| Canary | 0.98 / 1.00 | 0.96 / 1.02 | 0.96 / 1.03 | 0.97 / 0.99 |
| Old writer | 4.41 | 4.06 | 3.55 | 3.18 |
| Sixteen a step: one TBL over 64 bytes of value + 1 does the lookup and the membership, pairs by MLA, fields by USRA | 0.41 / 0.40 | 0.53 / 0.45 | 0.63 / 0.65 | 0.69 / 0.74 |
| The same, pairs by two UDOT and one SLI (needs the dot product) | 0.39 / 0.38 | 0.40 / 0.42 | 0.63 / 1.25 | 0.72 / 0.70 |
| Eight a step, one append | 0.55 | 0.53 | 0.67 | 0.80 |
| Thirty-two a step, one check | 0.42 | 0.43 | 0.64 | 0.71 |
| Sixteen a step, the writer reached through its reference | 0.52 | 0.60 | 0.67 | 0.69 |
| Sixteen a step, then one step of eight | 0.40 / 0.41 | 0.41 / 0.48 | 0.59 / 0.60 | 0.68 / 0.72 |
| The same, pairs by dot product | 0.38 | 0.44 | 0.57 | 0.69 |
| The same, checked by CMEQ and SHRN rather than UMINV | 0.43 | 0.42 | 0.61 | 0.72 |

| Numeric variant | 7,089 | 500 | 40 | 16 |
|---|---|---|---|---|
| Portable writer | 1.83 µs | 143 ns | 15.9 ns | 9.35 ns |
| Canary | 1.00 / 1.01 | 0.99 / 0.98 | 0.99 / 1.79 | 1.01 / 1.05 |
| Old writer | 4.06 | 3.64 | 2.32 | 1.64 |
| Twelve digits from sixteen chars, one 40-bit append (x64's SSSE3 step) | 0.70 | 0.70 | 0.90 | 1.18 |
| Twenty-four from exactly twenty-four chars, groups by three TBL and UMULL / UMLAL / UADDW, two 40-bit appends | 0.55 / 0.56 | 0.57 / 0.65 | 0.75 / 1.83 | 0.97 / 1.12 |
| Fifteen from sixteen chars, one 50-bit append | 0.57 / 0.58 | 0.58 / 0.59 | 0.83 / 0.85 | 1.00 / 1.05 |
| Twenty-four a step, then one of fifteen | 0.56 | 0.58 | 0.69 | 1.01 |
| Thirty from exactly thirty chars (the last load overlapping), two 50-bit appends | 0.58 | 0.60 | 0.86 | 1.09 |
| Thirty a step, then one of fifteen | 0.58 | 0.59 | 0.90 | 1.01 |

At 40 and 16 the process decides more than the variant: the canary, the portable writer's own code, read 1.79 at 40 digits in one round, so those columns are read only where both rounds agree. From 300 characters up the best steps took 0.38 to 0.41 of the portable Alphanumeric writer's time and 0.55 to 0.58 of the Numeric one's. Holding the writer in a local copy took 0.41 against 0.52 at 4,296 characters, as it gained on x64. The dot product gained about 5 % at 4,296 characters, and the one check per thirty-two characters nothing, as the check is off the chain.

With the shares above, the best steps would save 1.2 to 2.0 % of an alphanumeric encode and 0.7 to 0.9 % of a numeric one, estimated as the share times one less the step's ratio, and a step taking no time at all would save the share itself, at most 3.2 %. No NEON step shipped then: ARM64 kept the portable writers. With mask selection at the 0.4 of its time the transposed tier reached on AVX2, a version 40 alphanumeric encode would take about 49 µs, and the step would save about 2.8 % of it, again an estimate from the stage rows. The transposed tier came to ARM64 afterwards ("Phase 6", ARM64), a version 40 encode took 52 to 55 µs, and the steps were built into the library and measured end to end (below).

#### The NEON steps end to end (2026-10-05)

With the transposed scorer the payload was over the 3 % bar at version 40 (4.2 % alphanumeric, 3.5 % numeric, "Phase 6", ARM64), so the best steps above went into the library (`QRBinaryEncoder.Arm64.cs`, behind the dispatch): sixteen characters a step and then one of eight, and fifteen digits from sixteen chars. The timing mode's parity check held them to the old writers on the JIT and NativeAOT of .NET 8 and .NET 10 before any timing.

Each step's cut-over: the timing mode's writer shapes, the step entered directly beside the portable writer in one process, five rounds per build, the step's time over the portable writer's. The .NET 8 columns from 48 characters and 64 digits up are a second run of five rounds, averaged with the first where both ran a length.

| Alphanumeric | 8 | 9 | 10 | 12 | 14 | 15 | 16 | 17 | 20 | 24 | 31 | 32 | 40 | 48 | 64 | 128 | 300 | 4,296 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| .NET 10 JIT | 0.89 | 0.90 | 0.92 | 0.93 | 1.00 | 0.95 | 0.69 | 0.71 | 0.71 | 0.71 | 0.75 | 0.59 | 0.60 | - | - | - | 0.42 | 0.40 |
| .NET 10 NativeAOT | 0.89 | 0.90 | 0.85 | 0.87 | 0.88 | 0.95 | 0.69 | 0.71 | 0.76 | 0.71 | 0.77 | 0.59 | 0.60 | - | - | - | 0.41 | 0.41 |
| .NET 8 JIT | 0.90 | 0.91 | 1.00 | 1.00 | 1.06 | 1.07 | 1.00 | 1.00 | 0.90 | 0.89 | 1.09 | 0.96 | 0.90 | 0.82 | 0.80 | 0.71 | 0.63 | 0.61 |
| .NET 8 NativeAOT | 0.89 | 0.82 | 0.85 | 0.97 | 0.94 | 1.05 | 0.93 | 0.93 | 0.89 | 0.85 | 1.07 | 0.94 | 0.86 | 0.82 | 0.77 | 0.70 | 0.63 | 0.60 |

| Numeric | 16 | 20 | 24 | 28 | 30 | 32 | 36 | 40 | 44 | 48 | 64 | 80 | 96 | 128 | 160 | 192 | 256 | 320 | 400 | 500 | 7,089 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| .NET 10 JIT | 1.00 | 0.92 | 0.85 | 1.06 | 1.06 | 0.85 | 0.88 | 0.83 | 0.95 | 0.88 | 0.83 | - | - | - | - | - | - | - | - | 0.61 | 0.57 |
| .NET 10 NativeAOT | 1.00 | 1.00 | 0.92 | 1.06 | 1.00 | 0.85 | 0.94 | 0.88 | 1.00 | 0.88 | 0.80 | - | - | - | - | - | - | - | - | 0.61 | 0.58 |
| .NET 8 JIT | 1.45 | 1.31 | 1.23 | 1.41 | 1.39 | 1.23 | 1.12 | 1.06 | 1.29 | 1.06 | 1.00 | 0.93 | 0.91 | 1.02 | 0.88 | 0.90 | 0.86 | 0.74 | 0.79 | 0.71 | 0.80 |
| .NET 8 NativeAOT | 1.36 | 1.31 | 1.14 | 1.29 | 1.17 | 1.25 | 1.12 | 1.03 | 1.30 | 1.06 | 1.00 | 0.89 | 0.91 | 1.00 | 0.80 | 0.79 | 0.85 | 0.81 | 0.81 | 0.77 | 0.81 |

On .NET 10 the Alphanumeric step wins at every length the dispatch passes it, from eight characters, and the Numeric step from 40 digits, as the SSSE3 step does on x64: below that a step of sixteen chars read for fifteen written runs once or twice, and it read 1.06 at 28 and 30 digits. On .NET 8 both gain less. Its JIT and NativeAOT keep the step's local copy of the writer on the stack (`BitWriter` is a span and three more fields), where .NET 10 keeps it in registers, so every append goes through memory, as with the variant above that reached the writer through its reference. From 300 characters the Alphanumeric step took 0.60 to 0.63 of the portable writer's time there, and 1.05 to 1.10 at 15 and 31 characters, which end in the step of eight and a tail of seven. The Numeric step lost at 16 to 48 digits, drew at 64 to 128 and won from 160. On .NET 8 the dispatch therefore enters the steps from 32 characters and from 160 digits. One length in each second .NET 8 run read an outlier in one build (96 characters at 1.17 on the JIT, 112 digits at 1.16 on NativeAOT, against 0.76 and 0.87 in the other build) and is left out.

End to end, base `encode4` (0fda781) against the change, the stage harness on both sides, alternating one process each, the median of the per-round ratios. .NET 10 ran twice for nine rounds, the first on the same steps in a scratch copy of the library, entered from sixteen digits (every Numeric shape here is 40 digits or more, or under sixteen), and the table gives the eighteen rounds together. .NET 8 ran five rounds with the cut-overs above.

| Shape | .NET 10 JIT | .NET 10 NativeAOT | .NET 8 JIT | .NET 8 NativeAOT |
|---|---|---|---|---|
| V1-M, 16 alphanumeric | 1.00 | 1.01 | 1.04 | 1.03 |
| V10-M, 300 alphanumeric | 0.97 | 0.98 | 0.98 | 0.98 |
| V40-L, 4,296 alphanumeric | 0.98 | 0.98 | 0.97 | 0.97 |
| Set, 30,000 alphanumeric | 0.95 | 0.97 | 0.99 | 0.97 |
| V1-L, 10 digits | 1.01 | 1.00 | 1.02 | 1.00 |
| V40-L, 7,089 digits | 1.00 | 0.98 | 0.98 | 0.98 |
| Set, 50,000 digits | 0.99 | 0.99 | 1.01 | 1.00 |
| Payload, V1-M alphanumeric | 0.71 | 0.79 | 1.00 | 0.94 |
| Payload, V10-M alphanumeric | 0.46 | 0.52 | 0.66 | 0.65 |
| Payload, V40-L alphanumeric | 0.41 | 0.41 | 0.61 | 0.60 |
| Payload, set of 30,000 alphanumeric | 0.41 | 0.42 | 0.61 | 0.63 |
| Payload, V40-L digits | 0.57 | 0.57 | 0.84 | 0.76 |
| Payload, set of 50,000 digits | 0.58 | 0.58 | 0.82 | 0.80 |
| V19-M, 620 bytes | 1.00 | 1.01 | 1.00 | 1.01 |
| Micro QR M4 | 0.98 | 1.00 | 1.00 | 0.97 |
| rMQR R17x139 | 1.00 | 1.00 | 1.01 | 1.00 |

From the stage rows the steps save 2.5 % of a version 40 alphanumeric encode on .NET 10, 1.6 % at version 10, 1.8 % of the alphanumeric set and 1.1 to 1.5 % of the numeric shapes, and on .NET 8 1.9 %, 1.1 to 1.2 %, 1.3 % and 0.4 to 0.7 %. That is about what a run of nine rounds resolves here. The two .NET 10 runs read 0.95 to 0.96 and 0.98 to 1.00 at version 40 alphanumeric, and one round's ratio for a shape the change does not touch ranged over 0.03 to 0.04 either way, so the end-to-end rows confirm the direction and the stage rows give the size. The payload is then 1.7 to 1.8 % of a version 40 alphanumeric encode on .NET 10 and 2.8 to 3.0 % on .NET 8. On .NET 8 the version 1 alphanumeric row read 1.03 to 1.04 though a run of sixteen characters takes no step there (its payload row 0.94 to 1.00). Nine rounds of the version 1 and 6 shapes alone read it at 1.01 on both builds, with the shapes beside it at 0.96 to 1.03.

## Phase 8: output edges (2026-10-06)

Base `main` (1df5b10) against the change, the stage harness on both sides. The kernel rows are BenchmarkDotNet (3 warmups, 15 iterations) on the JIT with AVX2, from these runs:

- the table, the in-place form clearing each gap by a call as the row after it moved and the 16-byte vector store: 2026-10-05, not pinned
- the copies, the margins measured apart and the in-place 8-byte stores they are set against: a run from 23:55 that day, pinned to one CCD
- the exact-gap stores and the constant clear, with the 8-byte stores' margins each is set against: runs on 2026-10-06, each pinned to one CCD

### The class API's pack

`QRCodeData.SetCoreData` packed eight modules a step with a multiply-gather. It now goes through `ModuleBitPacker.Pack`, the vector packer of the Micro QR and rMQR data models (32 modules a step with AVX2), which writes the same bytes, since all three data models hold one MSB-first stream in row-major order. The stage harness's pack row and the class API's end-to-end row over the quiet-zone-free one, JIT with AVX2, five alternating rounds, the median of the run medians:

| Symbol | Pack, base | Pack, change | Ratio | Class over QZ0, base | Class over QZ0, change |
|---|---|---|---|---|---|
| V1-L, 10 digits | 49 ns | 18 ns | 0.37 | 1.09 | 1.07 |
| V6-M URL | 166 ns | 42 ns | 0.25 | 1.11 | 1.03 |
| V10-M, 300 alphanumeric | 305 ns | 77 ns | 0.25 | 1.11 | 1.05 |
| V19-M bytes | 789 ns | 181 ns | 0.23 | 1.07 | 1.02 |
| V40-L bytes | 2.87 µs | 0.66 µs | 0.23 | 1.10 | 0.99 |

Building `QRCodeData` from the winner's packed rows, the plan's other form of this item, would save at most the pack that is left: 1.6 to 2.5 % of the quiet-zone-free encode. The scorers hold a row with column c at bit c of its words, in one, two or three words or as transposed planes depending on the tier, and the data model is one MSB-first stream without row padding, so each tier would still convert its winner row by row, and every tier would have to hand its rows out. It was not done.

### The quiet zone, kernel

Each variant writes the same destination, checked byte for byte against the old form at quiet zones 1 to 5, 7 to 9 and 25 before timing. The placer is stood in for by one copy of the core into the destination, or one copy per row for the strided forms. The old form clears the destination and builds the core in a zeroed 1,024-byte stack buffer, or above 1,024 modules in a new array. It clears the core's part of the buffer again and copies the core's rows. Neither symbology's path before the change had that buffer: Micro QR's did the same in a 289-byte stack buffer, and Standard QR's rented its core and did not clear it. The review timed the forms against those paths (its subsection below). The in-place forms build the core at the destination's start and move the rows, the last first. The strided forms are rMQR's: the margins cleared, the core written into the window.

| Shape | Old | In place, as first written | In place, 8-byte stores | Strided, a clear per gap | Strided, 8-byte stores |
|---|---|---|---|---|---|
| M1, quiet zone 2 | 34.5 ns | 41.0 ns | 30.4 ns | 44.7 ns | 34.2 ns |
| M2 | 41.3 ns | 49.9 ns | 34.5 ns | 50.3 ns | 39.0 ns |
| M4 | 48.7 ns | 60.9 ns | 42.5 ns | 65.2 ns | 49.5 ns |
| V1, quiet zone 4 | 68.3 ns | 76.5 ns | 52.0 ns | 82.7 ns | 62.4 ns |
| V6 | 223 ns | 160 ns | 108 ns | 162 ns | 124 ns |
| V19 | 926 ns | 418 ns | 301 ns | 389 ns | 296 ns |
| V40 | 2.63 µs | 1.17 µs | 0.96 µs | 0.91 µs | 0.71 µs |
| R7x43, quiet zone 2 | 30.3 ns | 32.1 ns | 24.6 ns | 31.4 ns | 25.5 ns |
| R17x139 | 239 ns | 90.9 ns | 71.1 ns | 86.9 ns | 62.6 ns |

As first written, the in-place form moved every row and then cleared each gap between rows with one `Span.Clear` of 2q bytes. It was slower than the old form from M1 to V1 (1.12 to 1.25), and the strided form slower still (1.21 to 1.34). The calls were most of the cost. With each gap cleared as the row after it moved, still by the call, the in-place move read 1.07 to 1.20 there, and an 8-byte store through `BinaryPrimitives` ending where the gap ends, which at quiet zone 2 also zeroes the 4 bytes of the row in front, took it to 0.76 to 0.88 of this old form on the small symbols and 0.33 to 0.48 from V6. Against each symbology's own path, in two pinned runs of the review, the first form took 1.30 to 1.44 from M1 to V6 and the move with the call made as each row moved 1.21 to 1.32. The stores took 0.95 to 1.04 there, 0.91 at V19 and 0.73 at V40. Those runs read the stores at 0.87 to 0.94 of this old form to V1 and 0.51 to 0.71 from V6, so part of the difference from this run is between runs, this one not pinned. Every shape here has a gap of 4 or 8 bytes, so the forms for a gap of 9 to 16 bytes, two 8-byte stores or a clear of 16, were not timed. In place of the 8-byte store, a 16-byte vector store written through an unchecked reference measured the same where it applied, and could not serve M1, whose row and gap make 15 bytes.

The strided forms pay their per-row copies here, which a strided placer does as part of its unpack. Measured apart in another run, one copy of the core took 2.7 to 4.9 ns on M1 to M4 and the margins alone 11.0 to 13.8 ns, so a strided placer would add 11 to 14 ns to the quiet-zone-free encode, against 28 to 41 ns in that run for the moves and the margins of the in-place form, its 8-byte stores less the copy. Micro QR therefore writes the window directly, as rMQR does. Its vector unpack runs past a row's end, but only with the row's packed bits past the core, which are zero, so the margins are cleared first and stay light.

Stores that zero exactly the gap and nothing in front of it, which would let the margins be cleared after the placer, were slower on the margins alone: 15.6 to 19.8 ns against 11.2 to 16.5 on M1 to M4 as 8-, 4- and 2-byte stores in turn, 14.3 to 20.7 against 11.4 to 16.1 as two overlapping stores of one width, and 187 and 193 ns against 140 and 146 at V40, in two runs.

On .NET 10 the library writes each gap as a `Span.Clear` of the constant 8 or 16 bytes, which that JIT writes as one store, and of the 2q bytes past 16. Builds before .NET 10 write 8-byte stores (from the review, below). On the margins alone, in one run, it took 0.87 to 0.99 of the time of the stores through `BinaryPrimitives` the table above used: 9.9 against 11.3 ns on M1, 13.9 against 14.4 on M4, 18.3 against 19.2 on V1 and 134 against 141 at V40. On WebAssembly AOT and interpreted, three alternating rounds of the small symbols, the two forms read within 0.03 of each other end to end.

Two more costs came from moving code, not from the kernels. The gap clear is a method of its own shared by the three symbologies, and the WebAssembly interpreter called it once a row: the quiet-zone encodes of Standard QR at versions 1 and 6, of Micro QR M2 to M4 and of three rMQR sizes there took 1.03 to 1.04, 1.13 to 1.15 and 1.05 to 1.11 of their time before the change, and marked `AggressiveInlining` they took 0.98 to 1.00, 0.96 to 0.99 and 1.00 to 1.03 there (three rounds, base, the call and the inlined clear in one run). The JIT inlined it without the attribute. And once the Micro QR placer's entry took the stride, the JIT stopped inlining it into the generator's core writers, which its disassembly showed for the contiguous entry before. Without AVX2 the quiet-zone-free encodes of M2 and M3 then took 1.08 and 1.01 of their time. Marked `AggressiveInlining`, they took 1.00 and 0.98 (seven rounds of the three builds).

On default NativeAOT the M4 placer stage took 1.11 to 1.16 of its time in two runs of seven rounds, its quiet-zone-free encode 1.03 and its class encode 1.02 to 1.03, while M2 and M3 gained (0.92 to 0.95 on the same rows in the second run). ILC's unpack loops are the base's instructions, the rest of the method differing in register names, a few moves and a frame 16 bytes smaller, and the inner loop, which runs twice a row only at M4, starts at another offset (0x35D against 0x374), so the loss is taken to be code placement and was left.

### End to end

The shipped code against base, alternating one process each, pinned to one CCD with the .NET build servers shut down, the median of the run medians, change over base: five rounds on the JIT and NativeAOT, three on WebAssembly. The JIT without AVX2 column's Micro QR rows are a run of seven rounds of the Micro QR shapes alone. In the five-round run of every shape they read 0.99 to 1.06, its quiet-zone-free and class rows at 1.01 to 1.06, which run the same code as in the two runs of seven rounds, where they read 0.97 to 1.00. The quiet zone over the quiet-zone-free row there read 1.21, 1.20 and 1.15 at M2 to M4, against base 1.22, 1.20 and 1.19. The review of the phase found this again in runs of every shape and not its cause (below).

| Shape | JIT, AVX2 | JIT, no AVX | Default NativeAOT | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|---|
| V1-L span, quiet zone 4 | 0.99 | 0.99 | 0.99 | 0.87 | 1.00 |
| V1-L span, quiet zone 0 | 1.00 | 0.99 | 0.98 | 0.97 | 1.00 |
| V1-L class | 0.98 | 0.99 | 0.97 | 0.99 | 0.99 |
| V6-M span, quiet zone 4 | 1.00 | 0.99 | 0.98 | 0.89 | 1.00 |
| V6-M span, quiet zone 0 | 1.01 | 0.99 | 1.00 | 0.99 | 1.02 |
| V6-M class | 0.94 | 0.98 | 0.96 | 0.97 | 1.01 |
| V10-M span, quiet zone 4 | 1.00 | 0.99 | 0.98 | 0.89 | 1.00 |
| V10-M span, quiet zone 0 | 1.01 | 0.98 | 0.99 | 1.00 | 1.01 |
| V10-M class | 0.95 | 0.98 | 0.96 | 0.99 | 1.00 |
| V19-M span, quiet zone 4 | 1.01 | 0.99 | 0.96 | 0.95 | 1.01 |
| V19-M span, quiet zone 0 | 1.00 | 0.98 | 0.96 | 1.00 | 0.99 |
| V19-M class | 0.96 | 0.96 | 0.95 | 0.98 | 1.00 |
| V40-L span, quiet zone 4 | 0.98 | 0.98 | 0.96 | 0.96 | 1.01 |
| V40-L span, quiet zone 0 | 1.00 | 1.00 | 0.98 | 0.99 | 1.01 |
| V40-L class | 0.90 | 0.97 | 0.97 | 0.98 | 1.00 |
| M2 span, quiet zone 2 | 0.84 | 0.90 | 0.84 | 0.88 | 0.98 |
| M2 span, quiet zone 0 | 1.00 | 1.00 | 0.93 | 1.01 | 1.06 |
| M2 class | 0.96 | 0.99 | 0.92 | 0.99 | 1.01 |
| M3 span, quiet zone 2 | 0.86 | 0.89 | 0.85 | 0.88 | 1.02 |
| M3 span, quiet zone 0 | 1.00 | 0.99 | 0.95 | 1.01 | 1.01 |
| M3 class | 0.95 | 1.00 | 0.94 | 0.94 | 1.03 |
| M4 span, quiet zone 2 | 0.86 | 0.88 | 0.94 | 0.87 | 0.98 |
| M4 span, quiet zone 0 | 0.99 | 0.97 | 1.04 | 0.99 | 1.02 |
| M4 class | 0.96 | 0.99 | 1.04 | 0.97 | 1.01 |
| R7x43 span, quiet zone 2 | 0.96 | 1.02 | 0.97 | 0.95 | 1.01 |
| R7x43 span, quiet zone 0 | 0.98 | 1.01 | 0.98 | 0.98 | 1.00 |
| R7x43 class | 0.98 | 0.98 | 0.99 | 1.02 | 1.01 |
| R11x59 span, quiet zone 2 | 0.98 | 0.97 | 0.96 | 0.95 | 1.09 |
| R11x59 span, quiet zone 0 | 1.00 | 0.99 | 0.99 | 0.97 | 1.06 |
| R11x59 class | 1.02 | 1.00 | 0.95 | 0.99 | 1.00 |
| R17x139 span, quiet zone 2 | 0.99 | 0.99 | 1.00 | 0.97 | 1.02 |
| R17x139 span, quiet zone 0 | 1.03 | 0.98 | 0.99 | 1.00 | 1.00 |
| R17x139 class | 0.99 | 0.99 | 0.98 | 0.99 | 0.99 |

The interpreted run's R11x59 rows read 1.06 to 1.09 with its quiet-zone-free row among them, which the change does not touch there, and 1.00 to 1.03 in the three-round run of the small symbols above.

Each span row with a quiet zone, and each class row, over its symbol's quiet-zone-free span row, base and change, from the same runs:

| Symbol | JIT, AVX2 | JIT, no AVX | Default NativeAOT | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|---|
| V1-L, quiet zone | 1.08, 1.07 | 1.06, 1.05 | 1.05, 1.05 | 1.20, 1.07 | 1.05, 1.05 |
| V1-L, class | 1.09, 1.07 | 1.05, 1.04 | 1.07, 1.05 | 1.06, 1.07 | 1.07, 1.07 |
| V6-M, quiet zone | 1.05, 1.03 | 1.05, 1.05 | 1.06, 1.04 | 1.21, 1.08 | 1.07, 1.05 |
| V6-M, class | 1.11, 1.03 | 1.06, 1.06 | 1.09, 1.05 | 1.07, 1.05 | 1.07, 1.06 |
| V10-M, quiet zone | 1.06, 1.05 | 1.03, 1.04 | 1.06, 1.04 | 1.20, 1.07 | 1.05, 1.03 |
| V10-M, class | 1.11, 1.05 | 1.06, 1.06 | 1.09, 1.06 | 1.07, 1.06 | 1.06, 1.04 |
| V19-M, quiet zone | 1.01, 1.01 | 1.01, 1.02 | 1.02, 1.02 | 1.08, 1.02 | 1.02, 1.03 |
| V19-M, class | 1.07, 1.02 | 1.04, 1.03 | 1.04, 1.03 | 1.04, 1.02 | 1.03, 1.03 |
| V40-L, quiet zone | 1.03, 1.01 | 1.02, 1.00 | 1.03, 1.02 | 1.06, 1.02 | 1.01, 1.01 |
| V40-L, class | 1.10, 0.99 | 1.06, 1.03 | 1.05, 1.04 | 1.04, 1.03 | 1.03, 1.01 |
| M2, quiet zone | 1.26, 1.06 | 1.23, 1.11 | 1.19, 1.08 | 1.24, 1.07 | 1.11, 1.03 |
| M2, class | 1.16, 1.11 | 1.13, 1.12 | 1.19, 1.18 | 1.13, 1.11 | 1.22, 1.15 |
| M3, quiet zone | 1.23, 1.06 | 1.19, 1.06 | 1.19, 1.07 | 1.24, 1.08 | 1.10, 1.11 |
| M3, class | 1.16, 1.10 | 1.10, 1.10 | 1.16, 1.16 | 1.18, 1.10 | 1.17, 1.19 |
| M4, quiet zone | 1.25, 1.09 | 1.18, 1.07 | 1.18, 1.06 | 1.23, 1.08 | 1.10, 1.05 |
| M4, class | 1.15, 1.12 | 1.09, 1.11 | 1.15, 1.14 | 1.14, 1.12 | 1.16, 1.15 |
| R7x43, quiet zone | 1.18, 1.16 | 1.19, 1.20 | 1.19, 1.17 | 1.19, 1.15 | 1.07, 1.09 |
| R11x59, quiet zone | 1.20, 1.18 | 1.24, 1.21 | 1.25, 1.21 | 1.20, 1.17 | 1.07, 1.10 |
| R17x139, quiet zone | 1.12, 1.07 | 1.07, 1.08 | 1.08, 1.09 | 1.15, 1.12 | 1.03, 1.06 |

rMQR's class rows did not change, its pack being the vector packer before and after. Over the quiet-zone-free row they read 1.07 to 1.31 on both sides.

### From the review (2026-10-06)

The reviewed code against base `main` (1df5b10) in the BenchmarkDotNet encode classes, .NET 10 x64, the process pinned to one CCD: ShortRun, two alternating rounds a side, the mean of the two runs' means, and for Micro QR two rounds of 5 warmups and 15 iterations, where ShortRun had put two of its class rows at 1.07 and 1.08. A process whose standard deviation passed 10 % of its mean was left out whole, which left base one run in two rows: V19-M's class row, whose first round read 10.8 to 15.1 µs against 10.7 to 10.8 in the second, and Kanji_Long_V15_L's "(Pinned)" row, whose second round read 9.2 to 11.4 µs against 9.4 to 9.5 in the first. Change over base, then each row over its shape's quiet-zone-free row, base and change:

| Row | Class | Span, quiet zone | Span, QZ0 | Class over QZ0 | Quiet zone over QZ0 |
|---|---|---|---|---|---|
| Numeric_V1_L | 0.96 | 1.02 | 1.02 | 1.10, 1.03 | 1.08, 1.07 |
| Numeric_V40_L | 0.88 | 0.95 | 1.01 | 1.14, 1.00 | 1.04, 0.99 |
| Alphanumeric_V1_M | 0.99 | 0.99 | 1.00 | 1.07, 1.06 | 1.07, 1.06 |
| Alphanumeric_V10_M | 0.91 | 0.96 | 1.01 | 1.16, 1.04 | 1.08, 1.03 |
| Alphanumeric_V40_L | 0.92 | 0.97 | 1.03 | 1.12, 1.00 | 1.04, 0.98 |
| Byte_Url_V6_M | 0.96 | 1.02 | 1.00 | 1.13, 1.09 | 1.08, 1.10 |
| Byte_V20_M (V19-M) | 0.94 | 1.00 | 1.00 | 1.09, 1.02 | 1.02, 1.02 |
| Byte_V40_L | 0.93 | 0.98 | 1.01 | 1.11, 1.03 | 1.05, 1.02 |
| Byte_V40_H (V39-H) | 0.93 | 0.98 | 1.01 | 1.12, 1.03 | 1.05, 1.02 |
| Kanji_V6_M | 0.96 | 0.95 | 1.02 | 1.12, 1.05 | 1.14, 1.06 |
| Kanji_Long_V15_L | 0.93 | 1.00 | 1.02 | 1.10, 1.00 | 1.03, 1.00 |
| Numeric_R11x27 | 0.99 | 0.96 | 1.00 | 1.18, 1.17 | 1.40, 1.33 |
| Alphanumeric_R15x43 | 1.00 | 0.94 | 0.98 | 1.13, 1.16 | 1.44, 1.39 |
| Byte_R17x139 | 1.02 | 0.96 | 1.01 | 1.11, 1.12 | 1.18, 1.12 |
| Latin1Eci_R15x139 | 1.02 | 1.00 | 1.07 | 1.11, 1.06 | 1.14, 1.06 |
| Utf8Eci_R13x139 | 0.99 | 0.97 | 1.02 | 1.14, 1.11 | 1.15, 1.09 |
| Kanji_R17x139 | 1.00 | 1.01 | 0.99 | 1.09, 1.10 | 1.11, 1.13 |
| Numeric_M2 | 0.98 | 0.87 | 0.98 | 1.12, 1.13 | 1.24, 1.11 |
| Alphanumeric_M3 | 1.00 | 0.91 | 1.01 | 1.10, 1.09 | 1.23, 1.12 |
| Byte_M4 | 1.02 | 0.88 | 0.99 | 1.11, 1.14 | 1.25, 1.11 |
| Kanji_M4 | 1.02 | 0.87 | 0.99 | 1.08, 1.10 | 1.22, 1.07 |

Standard QR's "(Boost)" and "(Pinned)" rows took 0.92 to 0.98 of their time, rMQR's "(Pinned)" rows 0.98 to 1.02, and Micro QR's 0.98. rMQR's quiet-zone-free Latin-1 row read 1.07 with its span and class rows at 1.00 and 1.02, which the change does not touch on that path.

On .NET 8 its profile decides which of the two constant gap clears is a store: the form it saw run is, and the other stays a call to `SpanHelpers.ClearWithoutReferences`, for as long as the process runs. A probe of its own on .NET 8.0.28 warmed a process for 1.5 s with Micro QR M2, Standard QR version 10 and rMQR R11x27 (12 digits) at the quiet zones given, then timed each at 6, 2 and 4 (Micro QR M2 below, the fastest iteration of each run, three runs a side, ns):

| Warm-up, timed at | Base | Commit | 8-byte stores |
|---|---|---|---|
| 6, at 6 | 164.7 to 166.1 | 138.6 to 141.7 | 146.5 to 149.5 |
| 6, at 2 and 4 | 164.1 to 172.1 | 171.0 to 177.2 | 145.4 to 150.2 |
| 2 and 4, at 2 and 4 | 165.5 to 169.7 | 140.0 to 149.7 | 141.0 to 147.0 |
| 2 and 4, at 6 | 164.1 to 170.6 | 166.1 to 166.4 | 149.2 to 167.1 |

rMQR R11x27 in the same processes lost the same way at the commit, 229.7 to 232.9 ns after a warm-up of the other form against base 214.9 to 224.0, and read 204.1 to 216.6 in every case with the stores. Standard QR at version 10 read 3,284 to 3,547 ns with the stores, against base's 3,363 to 3,537 and the commit's 3,327 to 3,687. .NET 10 wrote both forms as stores in every case.

On .NET Framework 4.8 (the netstandard2.0 build, three or four alternating runs, each the median of nine rounds of the fastest of fifteen batches, µs):

| Path | Base | Commit | Shipped |
|---|---|---|---|
| Class, version 1 | 4.63 to 4.66 | 4.65 to 4.70 | 4.62 to 4.65 |
| Class, version 10 | 20.4 to 20.6 | 20.6 to 20.9 | 19.7 |
| Class, version 23 | 382 to 383 | 383 to 386 | 384 to 391 |
| Class, version 39 | 671 to 673 | 672 to 677 | 663 to 668 |
| Span, quiet zone 4, version 1 | 4.78 to 4.82 | 4.90 to 4.93 | 4.78 to 4.86 |
| Span, quiet zone 4, version 10 | 19.8 to 20.4 | 21.3 | 20.4 to 20.6 |

The span rows' commit column is from an earlier alternation of three runs with base, where base read 4.78 to 4.80 and 19.9 to 20.0. Base's and the shipped build's span columns are from a later alternation of four. The class rows' shipped build packs with the multiply-gather the model had, base's loop, so its version 10 reading is not the pack's: at versions 1, 23 and 39 it reads 1.00, 1.01 and 0.99 of base. The span rows' shipped build takes the old path, a pooled core copied into the cleared destination. That path is base's copy loop moved into a helper, yet it read 1.03 of base at version 10, for no cause measured. The commit's excess there came with the move: in another alternation of three runs, a build of the commit with base's `QRCodeGenerator.cs` read 19.97 to 20.10 µs at version 10 and 4.84 to 4.85 at version 1, against base's 20.02 to 20.05 and 4.85 to 4.86 and the commit's 21.37 to 21.78 and 4.93 to 4.96. The kernel that follows puts the move's own work, a clear a gap, only 0.18 µs over the old path at version 10 and quiet zone 4, 825 against 642 ns. In that kernel, on .NET Framework 4.8, one process of a Stopwatch harness (each form the median of nine rounds of its fastest batch over 60 ms, at versions 1, 10, 20 and 40 and quiet zones 4 and 6), the move in place had taken 1.13 to 1.14 times that path's time at versions 1 to 20 with one store a gap (quiet zone 4), 1.15 to 1.26 with two (quiet zone 6) and 1.17 to 1.29 with a clear a gap, and 0.99 to 1.09 at version 40. A form writing the same two stores at quiet zone 6 behind one test fewer read 1.05 to 1.14 at versions 1 to 20 in that process, 0.10 to 0.14 below them. The pack was timed in a second Stopwatch harness of the same kind, one process with each pack the median of nine rounds of its fastest batch over 80 ms, on random 0 and 1 modules at versions 1, 10, 20 and 40. There the packer's scalar loop, as netstandard2.0 compiles it, took 1.43 to 1.60 times the gather's time, and 2.02 to 2.10 on .NET 8 with hardware intrinsics off.

Micro QR's and rMQR's quiet-zone span paths are the rewritten ones on every build. Two runs of the review timed them on .NET Framework 4.8 against base with the netstandard2.0 build, in alternating processes pinned to one CCD: three a build in the first run and two in the second, each process giving the median of nine rounds of the fastest of fifteen batches. A process a disturbance slowed was left out whole, one of each build in the first run and one of base's in the second, so the second run's base is one process. Current over base, the median of each build's processes:

| Shape | Current over base |
|---|---|
| Micro QR M2, quiet zone 2 | 0.74 |
| Micro QR M2, quiet zone 6 | 0.79 |
| Micro QR M4, quiet zone 2 | 0.80 to 0.81 |
| rMQR R11x27, quiet zone 2 | 0.97 to 0.99 |
| rMQR R11x27, quiet zone 6 | 1.00 to 1.01 |
| rMQR, 150 alphanumeric characters, quiet zone 2 | 1.00 to 1.01 |
| The same, quiet zone 0, code unchanged | 1.01 to 1.03 |

Without AVX2, the stage harness's shapes of every symbology in one process (three alternating rounds of base, the build at the commit and the reviewed build, the median of the run medians, with base's third round left out, which ran 1.7 to 2.1 times as long on every shape), the commit read Micro QR's quiet-zone-free and class rows at M3 and M4 1.05 to 1.07 of base, and the reviewed build those rows at M2 to M4 0.98 to 1.01, with its quiet-zone rows at 0.86 to 0.91. The harness runs on .NET 10, where both builds' Tier1 span `CreateCore` without AVX2 is 3,140 bytes with `ValidateArguments` a call, so the difference is not that inline, and its cause was not found.

The phase's kernel run timed its forms against an old form neither symbology had (Phase 8, the quiet zone, kernel). Two runs of the review timed them against each symbology's own path before the change, line for line from 1df5b10, on .NET 10 with AVX2 (BenchmarkDotNet, 3 warmups and 15 iterations, the process pinned to one CCD, 2026-10-06):

- the M rows against Micro QR's, which zeroed a 289-byte stack buffer and cleared the core's part of it again, a clear of a length known only at run time
- the V rows against Standard QR's, which rented its core and did not clear it
- the R rows against rMQR's, which cleared the margins a row at a time and wrote the window

The in-place form as first written moves every row and then clears the margins in a second pass. The other in-place forms zero each gap as the row after it moves. Each form over that path, the two runs:

| Shape | In place, as first written | In place, a clear as each row moves | In place, 8-byte stores | In place, a constant clear | Strided, a clear per gap | Strided, 8-byte stores | Strided, a constant clear | The phase's old form |
|---|---|---|---|---|---|---|---|---|
| M1, quiet zone 2 | 1.31, 1.30 | 1.21, 1.23 | 0.95, 0.97 | 0.94, 0.96 | 1.22, 1.29 | 1.04 | 1.01, 1.02 | 1.08, 1.12 |
| M2 | 1.37, 1.34 | 1.27, 1.26 | 1.04, 0.99 | 1.02, 0.98 | 1.30, 1.27 | 1.08 | 1.07, 1.05 | 1.12, 1.11 |
| M4 | 1.38 | 1.27, 1.29 | 0.99, 0.98 | 0.99 | 1.31, 1.32 | 1.08, 1.07 | 1.09, 1.07 | 1.10, 1.11 |
| V1, quiet zone 4 | 1.44 | 1.32 | 1.01, 1.02 | 1.01 | 1.33, 1.34 | 1.12, 1.13 | 1.12 | 1.11, 1.08 |
| V6 | 1.39 | 1.29, 1.30 | 1.01, 1.00 | 1.01 | 1.27, 1.31 | 1.08, 1.06 | 1.08, 1.04 | 1.43, 1.42 |
| V19 | 1.57, 1.21 | 1.12, 1.11 | 0.91 | 0.91 | 1.04, 1.03 | 0.86, 0.84 | 0.83, 0.84 | 1.64, 1.63 |
| V40 | 0.91, 0.90 | 0.83, 0.82 | 0.73 | 0.74, 0.73 | 0.67, 0.65 | 0.56 | 0.55, 0.56 | 1.42, 1.41 |
| R7x43, quiet zone 2 | 1.16, 1.18 | 1.01, 1.02 | 0.85, 0.86 | 0.83, 0.87 | 0.98, 0.99 | 0.86 | 0.83, 0.85 | 1.12, 1.14 |
| R17x139 | 1.24 | 1.14, 1.16 | 1.03, 0.99 | 1.17, 0.99 | 1.00, 1.01 | 0.85, 0.87 | 0.84, 0.86 | 1.92, 2.01 |

The runs differ by more than 0.07 only at V19 as first written, and at R17x139 with the constant clear and in the phase's old form. The R rows' strided form with a clear per gap is rMQR's own path, so its 0.98 to 1.01 is what the same code reads against itself within a run. The phase's old form allocated 1,712 to 31,360 bytes a call from V6 and 2,392 at R17x139. The strided forms pay a copy per row here, which a strided placer does as part of its unpack.

### netstandard2.1 on .NET 6 and 7 (2026-10-07)

The review left the netstandard2.1 build, which kept Standard QR's old quiet-zone path, untimed. .NET 6 and 7 load that build. .NET 6.0.36 and 7.0.20 x64 were installed for this run with `dotnet-install` into a folder of their own, and ran the Release netstandard2.1 builds of `main` (5b666aa) and of the change from a console harness built for net6.0 and net7.0, and for net8.0 to run the kernel on .NET 8.0.28. The harness referenced the build by path, under the timing mode's assembly name and key, so its kernel called the loaded build's own `QuietZoneWindow`. Each process was pinned to one CCD, with the .NET build servers shut down before each run. It warmed each row for 300 ms, then timed nine rounds, in each the rows in turn, a row's time the fastest of fifteen batches, and gave each row the median of its rounds.

End to end: the span `QRCodeGenerator.Create` into a destination of exactly the matrix, in batches of about 2 ms, on the stage harness's texts at quiet zones 0 and 4, and at 6 and 9 for two of them. Six alternating rounds of base and change a runtime, the build that went first alternating by round. Every process of both builds wrote the same 16 outputs, hashed after an encode into a destination filled with 0xA5. One .NET 7 process of the change is left out whole: its two version 40 rows, the quiet-zone-free one included, read 446 to 452 µs in all nine rounds, 2.7 times the other processes, while its other rows matched theirs. Change over base, the median of each build's process medians, and the quiet zone over its symbol's quiet-zone-free row in the same process, the median over the processes, base then change:

| Row | .NET 6, change over base | .NET 6, quiet zone over QZ0, base and change | .NET 7, change over base | .NET 7, quiet zone over QZ0, base and change |
|---|---|---|---|---|
| V1-L numeric, quiet zone 0 | 0.995 | | 0.988 | |
| V1-L numeric, quiet zone 4 | 0.989 | 2.53 %, 1.86 % | 0.984 | 2.64 %, 2.04 % |
| V1-M alphanumeric, quiet zone 0 | 0.995 | | 0.989 | |
| V1-M alphanumeric, quiet zone 4 | 0.989 | 2.10 %, 1.45 % | 0.977 | 2.57 %, 1.73 % |
| V1-M alphanumeric, quiet zone 6 | 0.993 | 2.14 %, 2.07 % | 0.976 | 2.65 %, 1.93 % |
| V1-M alphanumeric, quiet zone 9 | 1.002 | 2.20 %, 2.88 % | 0.983 | 2.94 %, 2.59 % |
| V6-M URL, quiet zone 0 | 0.997 | | 0.987 | |
| V6-M URL, quiet zone 4 | 0.996 | 1.65 %, 1.61 % | 0.981 | 1.78 %, 1.36 % |
| V10-M alphanumeric, quiet zone 0 | 0.998 | | 0.985 | |
| V10-M alphanumeric, quiet zone 4 | 0.995 | 1.54 %, 1.25 % | 0.983 | 1.53 %, 1.34 % |
| V10-M alphanumeric, quiet zone 6 | 0.996 | 1.60 %, 1.34 % | 0.984 | 1.57 %, 1.42 % |
| V10-M alphanumeric, quiet zone 9 | 1.003 | 1.62 %, 2.09 % | 0.991 | 1.60 %, 2.04 % |
| V19-M byte, quiet zone 0 | 1.005 | | 0.990 | |
| V19-M byte, quiet zone 4 | 1.002 | 0.51 %, 0.28 % | 0.991 | 0.51 %, 0.39 % |
| V40-L byte, quiet zone 0 | 1.004 | | 0.993 | |
| V40-L byte, quiet zone 4 | 1.000 | 0.81 %, 0.44 % | 0.989 | 0.76 %, 0.54 % |

The quiet-zone-free rows run code the change does not touch. On .NET 7 the change's build read them at 0.985 to 0.993, so its quiet-zone rows over base carry that offset, and the quiet zone over the same process's quiet-zone-free row is what the change moved.

The kernel: the old path line for line from 5b666aa (clear the destination, rent the core, copy its rows into the window, return the core) against the netstandard2.1 build's `QuietZoneWindow.CenterCore`, each form a call the JIT does not inline and neither writing the core, on a destination of the version's matrix and quiet zone, in batches of about 1 ms, the two forms in turn. Three processes a runtime, on base's build, whose `QuietZoneWindow` is the change's. The median of the three processes' medians, and the range of the processes' ratios:

| Shape | .NET 6, old and in place (ns) | .NET 6, in place over old | .NET 7, old and in place (ns) | .NET 7, in place over old | .NET 8, old and in place (ns) | .NET 8, in place over old |
|---|---|---|---|---|---|---|
| V1, quiet zone 4 | 74, 60 | 0.80 | 74, 58 | 0.74 to 0.79 | 74, 58 | 0.78 |
| V1, quiet zone 6 | 75, 82 | 1.07 to 1.09 | 75, 63 | 0.83 to 0.84 | 75, 67 | 0.88 to 0.89 |
| V1, quiet zone 9 | 76, 111 | 1.46 | 76, 87 | 1.14 to 1.15 | 77, 109 | 1.42 to 1.45 |
| V10, quiet zone 4 | 188, 185 | 0.97 to 0.99 | 178, 158 | 0.88 to 0.89 | 187, 168 | 0.90 |
| V10, quiet zone 6 | 193, 207 | 1.07 to 1.08 | 180, 171 | 0.94 to 0.95 | 190, 192 | 1.01 |
| V10, quiet zone 9 | 197, 292 | 1.46 to 1.48 | 187, 237 | 1.25 to 1.27 | 197, 307 | 1.55 to 1.58 |
| V20, quiet zone 4 | 370, 339 | 0.90 to 0.92 | 378, 316 | 0.83 to 0.84 | 353, 303 | 0.84 to 0.86 |
| V20, quiet zone 6 | 377, 380 | 0.99 to 1.01 | 388, 361 | 0.93 to 0.94 | 358, 351 | 0.98 |
| V20, quiet zone 9 | 385, 527 | 1.36 to 1.37 | 405, 483 | 1.18 to 1.23 | 368, 554 | 1.50 to 1.52 |
| V40, quiet zone 4 | 1196, 872 | 0.72 to 0.73 | 1104, 779 | 0.71 | 1060, 739 | 0.69 to 0.71 |
| V40, quiet zone 6 | 1223, 1002 | 0.82 to 0.84 | 1152, 954 | 0.78 to 0.84 | 1120, 912 | 0.80 to 0.82 |
| V40, quiet zone 9 | 1252, 1246 | 0.98 to 1.00 | 1189, 1122 | 0.91 to 1.01 | 1139, 1249 | 1.08 to 1.11 |

A gap is one 8-byte store at quiet zone 4, two at 6, and at 9 a `Span.Clear` of its 18 bytes, a call on every build. .NET 8 ran the same netstandard2.1 build, whose gap stores are the net8.0 build's source. The review's .NET Framework kernel copied a prepared core in both forms and ran its own copy of the move, the gap form given as an argument, so its ratios and these are not one series.

## Phase 9: leads (2026-10-07)

Base b47ee9b against the change, the stage harness on both sides (base's copy given the version 12 shape), .NET 10.0.9 on a Ryzen 9 7950X3D (Zen 4) unless a row says otherwise. Each shape runs alone in its own process pinned to one CCD, with the .NET build servers shut down. Rounds alternate base and change, and a ratio is the median of the run medians, change over base.

### Ceilings

Each stage alone in its own pinned process, one run, on the JIT with AVX2, as a share of the quiet-zone-free encode. Stages timed alone need not sum to the encode.

| Symbol | Encode | RS | Placement | Mask selection |
|---|---|---|---|---|
| V1-L, 10 digits | 0.743 µs | 3.2 % | 8.5 % | 74.7 % |
| V1-M, 16 alphanumeric | 0.758 µs | 2.8 % | 8.0 % | 82.5 % |
| V6-M URL | 1.813 µs | 6.2 % | 16.6 % | 61.8 % |
| V10-M, 300 alphanumeric | 2.904 µs | 7.4 % | 21.6 % | 53.4 % |
| V19-M bytes | 10.63 µs | 6.1 % | 17.4 % | 74.5 % |
| V26-M bytes | 14.49 µs | 8.0 % | 20.5 % | 57.7 % |
| V39-H bytes | 29.86 µs | 4.7 % | 19.9 % | 55.8 % |
| V40-L alphanumeric | 34.82 µs | 8.6 % | 18.6 % | 50.6 % |
| V40-L bytes | 33.97 µs | 8.4 % | 18.3 % | 52.1 % |
| V40-L numeric | 33.87 µs | 8.7 % | 18.4 % | 49.1 % |

RS is 2.8 to 3.2 % at version 1 and 4.7 to 8.7 % from version 6, placement 8.0 % or more everywhere, so both were built. The two leads for versions 1 to 11 sit inside mask selection and were measured as kernels.

### Versions 1 to 11: the popcount reduction and a 512-bit pass

BenchmarkDotNet (3 warmups, 15 iterations, pinned to one CCD), the AVX2 single-word scorer selecting among all eight candidates, two groups of four lanes, against the shipped selection at the sizes of versions 1, 2, 6, 10 and 11. The deferred form sums the popcounts as bytes and folds them every 31 rows. The ceiling drops the reduction (`vpsadbw`) outright, so its scores are wrong and it bounds what any deferral could save; it runs without the early abort and is set against the shipped scorer without it. The 512-bit pass holds the eight candidates in one `Vector512`, with the same nibble-table popcount and no early abort. `VPOPCNTQ` was not tried. Two runs of the deferred form, two of the ceiling, one of the 512-bit pass:

| Size | Shipped (run 3) | Deferred over shipped | Ceiling over shipped without abort | Shipped without abort over shipped | 512-bit over shipped |
|---|---|---|---|---|---|
| 21 | 478 ns | 1.12, 1.11 | 1.00, 0.98 | 1.00, 1.00 | 1.15 |
| 25 | 574 ns | 1.12, 1.11 | 0.99, 1.00 | 1.01, 0.98 | 1.11 |
| 41 | 923 ns | 1.10, 1.15 | 1.01, 0.98 | 1.01, 1.03 | 1.06 |
| 57 | 1,299 ns | 1.10, 1.13 | 0.97, 0.95 | 1.02, 1.05 | 1.05 |
| 61 | 1,524 ns | 1.26, 1.04 | 1.00, 1.03 | 1.02, 0.90 | 1.07 |

The most a deferred reduction could save is inside the runs' spread, and the 512-bit pass lost at every size, so both were dropped.

### Reed-Solomon across blocks

BenchmarkDotNet as above, each variant over a symbol's blocks against the shipped GFNI kernel one block at a time. The two- and four-chain kernels hold their blocks' remainders in registers of their own and run the chains in one loop, at 128 bits up to 16 ECC codewords and 256 bits above. The lane kernel puts one block in each byte lane of a 256-bit vector, 32 blocks a vector, from a lane-major copy of the data. Its second form, for 30 ECC codewords only (and the first form elsewhere), keeps the remainders in locals. Three runs of the chains, two of the lanes; the one-at-a-time column is run 2's time:

| Symbol (blocks, ECC codewords) | One at a time | Two chains | Four chains | Lanes | Lanes, second form | Lanes, second form, data copied into lanes |
|---|---|---|---|---|---|---|
| V6-M (4, 16) | 116 ns | 0.74 to 0.76 | 0.68 to 0.71 | 2.00 to 2.16 | 2.01 to 2.11 | 2.80 to 2.81 |
| V10-M (5, 26) | 216 ns | 0.80 to 0.81 | 0.79 to 0.80 | 2.52 to 2.57 | 2.53 to 2.57 | 3.29 to 3.33 |
| V19-M (14, 26) | 629 ns | 0.77 to 0.78 | 0.75 to 0.76 | 0.88 | 0.87 to 0.88 | 1.63 to 1.67 |
| V26-M (23, 28) | 1,066 ns | 0.76 to 0.78 | 0.73 to 0.75 | 0.60 to 0.62 | 0.60 to 0.61 | 1.28 to 1.32 |
| V39-H (77, 30) | 1,467 ns | 0.78 to 0.79 | 0.73 to 0.86 | 0.55 | 0.71 to 0.72 | 1.30 to 1.32 |
| V40-L (25, 30) | 2,873 ns | 0.72 to 0.75 | 0.72 to 0.73 | 0.58 to 0.59 | 0.73 to 0.74 | 1.43 to 1.44 |

The 0.86 at V39-H is run 3's; runs 1 and 2 read 0.73 there. The lanes win only with the data already lane-major: copied in a byte at a time, the second form lost everywhere, and their output was left in lanes. A vector transpose in and out was not tried. At V6-M and V10-M the blocks fill 4 and 5 of the 32 lanes. The chains shipped, four then two then one: from 17 ECC codewords only on 256-bit GFNI with AVX2, since there are no 128-bit chains for 32-byte remainders.

With GFNI off, an SSSE3 pair (one run) took 0.72 of one block at a time at V6-M and 0.90 to 1.06 at V10-M to V40-L. That pair read its sixteen generator vectors from the nibble table at every step (the JIT's fully optimized code on this machine: all 72 shuffles take a memory operand, with no vector spills), where the 16-codeword pair's 36 shuffles all take registers. A 32-byte pair holding its vectors in registers was not tried. SSSE3 groups therefore pair only up to 16 ECC codewords.

End to end, five rounds, change over base, the RS change alone on the JIT with AVX2, with GFNI and with GFNI off (`DOTNET_EnableGFNI=0`, the SSSE3 kernels). The multi-block rows are from the first form of the entry, whose block path is the shipped one. The one-block rows are from the shipped entry (below):

| Shape | RS, GFNI | Encode, GFNI | RS, SSSE3 | Encode, SSSE3 |
|---|---|---|---|---|
| V1-L, 10 digits (1 block) | 0.88 | 1.02 | 0.89 | 1.02 |
| V1-M, 16 alphanumeric (1 block) | 0.91 | 1.01 | 0.93 | 1.01 |
| V6-M URL | 0.70 | 1.00 | 0.72 | 0.97 |
| V10-M, 300 alphanumeric | 0.79 | 1.00 | 1.00 | 0.99 |
| V19-M bytes | 0.75 | 0.99 | 0.99 | 1.02 |
| V26-M bytes | 0.70 | 0.98 | 0.99 | 0.98 |
| V39-H bytes | 0.67 | 0.99 | 0.98 | 0.98 |
| V40-L alphanumeric | 0.70 | 0.95 | 1.00 | 0.99 |
| V40-L bytes | 0.70 | 0.97 | 1.00 | 0.99 |
| V40-L numeric | 0.69 | 0.97 | 1.00 | 0.99 |
| R7x43 numeric (1 block) | 1.03 | 1.01 | 1.03 | 1.02 |
| R11x59 alphanumeric (1 block) | 1.00 | 1.00 | 0.99 | 0.98 |
| R17x139 bytes | 0.85 | 0.92 | 0.98 | 0.99 |
| Micro QR M4, untouched (both runs) | | 1.01, 1.01 | | 0.98, 1.01 |

The rMQR RS row is the codeword assembly, RS and interleave. A second run of V6-M, V40-L bytes and R17x139 read the RS rows at 0.70, 0.71 and 0.87 with GFNI, the encodes at 0.97, 0.96 and 0.92, and the run of the shipped entry R17x139 at 0.82 and 0.93.

The entry as first written checked its arguments and dispatched both groups in calls of its own for every symbol. The one-block RS rows read 1.13 and 1.14 at version 1 and 1.21 at R7x43 with GFNI, and 1.03, 1.07 and 1.14 with SSSE3. A path for single blocks brought version 1 to 0.83 to 0.97, while R7x43 still read 1.11 with SSSE3 (0.038 to 0.044 µs a round against base's 0.035 to 0.036). With the single-block test in an inlined entry and the checks moved behind it, R7x43 reads 0.035 to 0.037 µs, 1.03.

Default NativeAOT (its 128-bit GFNI tier, no AVX2), the RS change alone: the RS rows read 0.73 to 0.76 at V6-M, 0.94 at V40-L and R17x139 and 0.97 to 1.00 elsewhere, in two runs of five rounds. Its encode rows took two or more levels on both builds, process by process (V10-M base 5.35 to 5.79 µs, change 5.34 to 5.82 and one process at 8.96), and read 0.95 to 1.06 across three runs, V10-M 0.96 in one and 1.05 in another. The fastest process of ten a build: V1-M 1.468 against 1.470 µs, V6-M 3.374 against 3.424, V10-M 5.338 against 5.345, V19-M 19.37 against 19.57, M4 0.289 against 0.293. V40-L and R17x139 have 30 and 20 ECC codewords a block, above the 16 this build's chains take, so there both builds run the same kernel a block at a time. Their 0.94 was not explained.

### Placement into the column words

The change build alone, the template copy, byte placement and selection (the placed route) against the stream placed into the column words and the same selection (the stream route), five rounds a run. The first run's versions 1 to 10 are a stream form for the single-word tier, since removed:

| Symbol | Stream over placed, run 1 | Run 2 |
|---|---|---|
| V1-L, 10 digits | 1.14 | |
| V1-M, 16 alphanumeric | 1.12 | |
| V6-M URL | 1.03 | |
| V10-M, 300 alphanumeric | 0.97 | |
| V12-M bytes | | 0.97 |
| V19-M bytes | 0.86 | 0.93 |
| V26-M bytes | 0.93 | 0.91 |
| V39-H bytes | 0.92 | 0.89 |
| V40-L bytes | 0.94 | 0.91 |
| V40-L alphanumeric | | 0.91 |
| V40-L numeric | | 0.89 |

The stream form's tables (the column template, the walk's runs and the scattered modules' places) take 3,944 bytes at version 12 to 13,352 at version 40, built on first use.

### End to end, the shipped code

Both changes against base, five rounds, the JIT with AVX2:

| Shape | Quiet-zone-free span | Class |
|---|---|---|
| V1-L, 10 digits | 0.99 | 0.98 |
| V1-M, 16 alphanumeric | 0.98 | 0.97 |
| V6-M URL | 0.99 | 0.94 |
| V10-M, 300 alphanumeric | 0.98 | 0.98 |
| V12-M bytes | 0.94 | 0.90 |
| V19-M bytes | 0.89 | 1.39 |
| V26-M bytes | 0.88 | 0.90 |
| V39-H bytes | 0.95 | 0.87 |
| V40-L alphanumeric | 0.84 | 0.87 |
| V40-L bytes | 0.84 | 0.88 |
| V40-L numeric | 0.85 | 0.89 |
| R7x43 numeric | 0.99 | |
| R11x59 alphanumeric | 1.00 | |
| R17x139 bytes | 0.92 | |
| Micro QR M2, untouched | 1.04 | |
| Micro QR M4, untouched | 0.99 | |

The V19-M class row read 1.38 again in ten rounds. Over both runs the change's processes took three levels, 10.5, 14.8 to 15.3 and 18.1 to 19.0 µs, against base's 12.8 to 13.8. A Stopwatch harness of the class API alone, eight alternating processes a build, each the median of nine rounds of the fastest batch, took V18-M, V19-M and V20-M to 0.91, 0.90 and 0.89 of base's time, every process of the change below every one of base, and V26-M and V40-L to 0.87 and 0.85. The level the stage harness reads was not explained.

BenchmarkDotNet's `QRCodeEncodeEndToEnd` rows (3 launches, 3 warmups, 15 iterations, pinned to one CCD, two alternating rounds a build), change over base:

| Row | Round 1 | Round 2 |
|---|---|---|
| V6-M URL, class | 0.97 | 0.97 |
| V6-M URL, span | 0.98 | 0.98 |
| V6-M URL, boost | 0.98 | 0.98 |
| V20-M bytes, class | 0.90 | 0.94 |
| V20-M bytes, span | 0.90 | 0.91 |
| V40-L bytes, class | 0.88 | 0.89 |
| V40-L bytes, span | 0.88 | 0.90 |
| V40-L bytes, pinned version | 0.90 | 0.90 |

The class rows allocate 280, 1,152 and 3,984 bytes on both builds, the span rows nothing.

The other builds, five rounds unless stated, change over base:

- The JIT without AVX (`DOTNET_EnableAVX=0`): encodes 0.97 to 1.02, R17x139 0.99 in a second run of ten rounds after 1.04 in the first, Micro QR M4 0.94 and 0.99. The RS rows read 0.71 at V6-M, 0.88 at V1-L and 0.97 to 1.03 elsewhere.
- .NET Framework 4.8, the netstandard2.0 build (no intrinsics; the block entry goes one block at a time), four alternating processes a build in a Stopwatch harness: 0.99 to 1.00 over V1-L to V40-L, R7x43, R17x139 and Micro QR M4.
- WebAssembly, where neither multi-block kernel nor the stream form runs. Interpreted, three rounds: 0.98 to 1.02. AOT-compiled, three rounds: 0.96 to 1.08, with V19-M at 1.07 and the untouched Micro QR M4 at 1.08. A second AOT run of five rounds: V6-M 1.00, V19-M 1.00, V40-L 1.04, R11x59 1.00, the untouched Micro QR M2 to M4 0.94 to 1.00. V40-L's processes there read 62.97 to 67.33 µs on base and 63.03 to 67.61 on the change.

Not measured: ARM64, where the block entry goes a block at a time through the NEON kernel and the stream form does not run.

## Phase 9 follow-up: scalar mask selection on .NET Framework 4.8 (2026-10-07)

Base dceb2aa against the change, .NET Framework 4.8 (4.8.9345, x64) with the netstandard2.0 build, on the Ryzen 9 7950X3D (Zen 4). A Stopwatch harness built for net48, named and signed so it reaches the internals, times the stage harness's Standard QR shapes as the timing mode does (300 ms of warm-up, batches sized to 20 ms, 11 rounds, the median). Each shape runs alone in its own process pinned to one CCD, with the .NET build servers shut down. A/B rounds alternate the builds and reverse their order every other round, and a ratio is the median of each build's process medians, change over base.

Not timed: .NET Framework on 32-bit x86, and the runtimes that load the netstandard2.1 build.

### Where the time goes

Each stage alone in its own pinned process, one run, base. Stages timed alone need not sum to the encode.

| Symbol | Encode | RS | Placement | Mask selection | One candidate's score |
|---|---|---|---|---|---|
| V10-M, 300 alphanumeric | 19.4 µs | 3.95 µs (20 %) | 1.08 µs (5.6 %) | 12.7 µs (65 %) | 1.50 µs |
| V12-M bytes | 217 µs | 5.68 µs (2.6 %) | 1.47 µs (0.7 %) | 218 µs (100 %) | 25.6 µs |
| V19-M bytes | 329 µs | 14.3 µs (4.4 %) | 2.80 µs (0.9 %) | 309 µs (94 %) | 36.9 µs |
| V40-L bytes | 676 µs | 76.9 µs (11 %) | 10.4 µs (1.5 %) | 602 µs (89 %) | 70.0 µs |

The score column is the scorer alone over one candidate's packed rows, eight a selection: 26 ns a row at version 10 (one word a row) and 394 to 397 ns at versions 12, 19 and 40. On .NET Framework 4.8 the base scorer compiles to 13,083 bytes, 840 of its instructions with a stack operand, and calls the span's indexer at 10 sites and `Row192`'s AND, OR and popcount at 7, 1 and 1, all in the last loops of the column finder windows. The popcount is the SWAR count, since netstandard has no `BitOperations`. On .NET 10 with hardware intrinsics off the same method compiles to 4,919 bytes with 308 instructions with a stack operand, every operator and indexer inlined, and 30 calls to the popcount's software fallback.

### The variant ladder

Mask selection with its copy of the unmasked matrix, each variant over its parent, two runs, every variant of a run in one pinned process. Before any timing, each variant's eight scores, chosen pattern and masked matrix were checked against V1 at versions 12 to 40, all four levels and seven streams (random, all 0, all 1, 0xAA, 0xCC, 0xF0, sparse), 812 symbols a variant. A dropped carry planted in V5's run starts failed 652 of them.

| Variant | Over | V12-M | V19-M | V26-M | V40-L |
|---|---|---|---|---|---|
| V1, a copy of base's selection | base | 1.00, 1.00 | 0.99, 0.99 | 0.98, 0.99 | 1.00, 0.99 |
| V2, the same rules over words, three a row in one rented `ulong[]` | V1 | 0.23, 0.22 | 0.23, 0.23 | 0.23, 0.23 | 0.24, 0.24 |
| V3, V2 without the column rules of a word that holds no module | V2 | 0.91, 0.91 | 0.91, 0.91 | 0.91, 0.91 | 0.99, 1.00 |
| V4, rule 1 along a row from the equalities | V2 | 0.73, 0.72 | 0.73, 0.73 | 0.73, 0.73 | 0.74, 0.74 |
| V5, two words a row up to version 27 (shipped) | V4 | 0.75, 0.75 | 0.76, 0.76 | 0.76, 0.77 | 0.99, 0.99 |
| V6a, in the row loop, each term's popcounts of a row's words summed before one reduction | V5 | 1.00, 1.00 | 1.00, 1.00 | 1.00, 1.00 | 0.98, 0.98 |
| V6b, one loop for every word's column rules, each term's popcounts summed there too | V5 | 1.07, 1.09 | 1.09, 1.07 | 1.07, 1.06 | 1.08, 1.06 |
| V6, both | V5 | 1.09, 1.07 | 1.07, 1.09 | 1.06, 1.06 | 1.05, 1.04 |
| V7, an early abort before the column rules | V5 | 1.00, 1.00 | 1.01, 1.00 | 1.00, 1.01 | 1.00, 1.01 |
| V8, no AND with the low words' row and equality masks | V5 | 1.02, 1.02 | 1.02, 1.02 | 1.01, 1.02 | 1.03, 1.02 |

V5 took 26.1 to 26.2 µs at version 12, 38.8 to 39.3 at 19, 52.1 to 52.8 at 26 and 104 to 105 at 40, 0.12 to 0.13 of V1 at versions 12 to 26 and 0.18 at 40. V3 is part of V5, whose two-word path has no third word. V6 as first written, with its merged column loops inline in its two scorers, read 1.12 to 1.14 over V5 in two runs. The table's V6 does the same arithmetic with those loops in a method of their own, which let V6a and V6b run alone. V8 drops masks that are all ones from size 65, and as run it also left the two-word rows' second light word without its row mask, which no count reads past the symbol. Two earlier pairs of runs set a variant's choice in a static field as each shape was built, before any was timed, so the variants sharing it ran the last one set: V2 and V3 in the first pair, whose library and V1 rows are the V1 row above, and V2, V4 and V5 in the second, which was discarded.

### End to end

Run 1 is the change against base, five rounds. Run 2 adds a build of the change with one unused method in `ModulePlacer` and a build of base with the same method (the canary), five rounds. Times are run 2's medians.

| Shape | Base | Change | Change, run 1 | Change, run 2 | Change with the unused method | Canary |
|---|---|---|---|---|---|---|
| V1-L encode | 4.68 µs | 4.62 µs | 1.01 | 0.99 | 1.00 | 0.98 |
| V6-M encode | | | 1.00 | | | |
| V10-M encode | 19.0 µs | 19.4 µs | 1.02 | 1.02 | 1.06 | 1.00 |
| V10-M RS | 4.04 µs | 5.01 µs | | 1.24 | 0.98 | 0.99 |
| V12-M encode | 221 µs | 34.9 µs | 0.16 | 0.16 | 0.15 | 1.00 |
| V12-M mask selection | | | 0.12 | | | |
| V19-M encode | 327 µs | 58.6 µs | 0.18 | 0.18 | 0.17 | 1.01 |
| V19-M class API | | | 0.17 | | | |
| V19-M mask selection | 309 µs | 39.2 µs | 0.13 | 0.13 | 0.12 | 1.00 |
| V19-M RS | 14.1 µs | 11.3 µs | | 0.80 | 0.78 | 1.00 |
| V26-M encode | 434 µs | 85.1 µs | 0.20 | 0.20 | 0.19 | 1.01 |
| V26-M mask selection | | | 0.13 | | | |
| V40-L encode | 680 µs | 199 µs | 0.29 | 0.29 | 0.27 | 1.00 |
| V40-L class API | | | 0.27 | | | |
| V40-L mask selection | 597 µs | 110 µs | 0.19 | 0.18 | 0.18 | 1.00 |
| V40-L RS | 74.1 µs | 57.5 µs | | 0.78 | 0.76 | 1.00 |

The RS kernel compiles to the same 808 bytes on base, the change and the change with the unused method, addresses and a per-process constant aside, and the change does not touch it. Two more runs checked the rows it does not reach. Seven rounds on versions 1, 6 and 10 put V10's RS stage at 1.26, its placement at 1.04, its mask selection at 0.98 and the three encodes at 0.99 to 1.02. A three-way run with the canary put V10's RS stage at 1.28 on the change and 1.01 on the canary, and V19's and V40's at 0.80 and 0.77 on the change and 1.02 and 0.98 on the canary. With the RS stage's difference timed alone added to the change's encode (2.8 and 3.1 µs at version 19, 16.6 and 17.5 at version 40), run 2's encodes read 0.19 and 0.18 at version 19 and 0.32 and 0.30 at version 40.

### .NET 8 and 10

.NET 8 and later reach this selection only without accelerated vectors, so these run with hardware intrinsics off, five rounds. .NET 10 runs the stage harness's own timing mode and .NET 8 the Stopwatch harness built for net8.0, since the stage harness compiles for .NET 10 only.

| Shape | .NET 10.0.9 | .NET 8.0.28 |
|---|---|---|
| V10-M encode, untouched | 1.00 | 1.00 |
| V12-M encode | 0.58 | 0.62 |
| V19-M encode | 0.61 | 0.65 |
| V26-M encode | 0.63 | 0.66 |
| V40-L encode | 0.85 | 0.88 |
| V12-M mask selection | 0.54 | 0.57 |
| V19-M mask selection | 0.54 | 0.57 |
| V26-M mask selection | 0.54 | 0.57 |
| V40-L mask selection | 0.79 | 0.83 |

Base's version 19 mask selection took 67.4 µs on .NET 10 and 58.2 on .NET 8 there, against 309 on .NET Framework 4.8.

On the JIT with AVX2, which does not run the changed code, five rounds of the stage harness read the quiet-zone-free encodes at V1-L, V10-M, V19-M and V40-L at 0.98 to 1.01 and V40-L's framed encode at 1.00. The V19-M class row read 1.29, its processes at levels on both builds, about 11.1, 15.3 and 19.8 µs on base and 15.3 and 19.7 to 19.8 on the change. Phase 9 recorded such levels on that row for the change alone and did not explain them. In the three rounds where both builds landed on one level it read 1.00, 1.00 and 0.99.

### Output

The corpus hashes are the same on base and the change: on .NET Framework 4.8, and on .NET 10 with AVX2, with AVX off and with hardware intrinsics off, the 7,647 symbols hash to 28E8DED2 and the 6,504 pinned-mask symbols to 795A3C5D (the first eight hex digits of each SHA-256).

### From the review (2026-10-08)

`ScoreColumnWords` with `AggressiveOptimization`, as the two scorers that call it have, over the change without it, with hardware intrinsics off, five rounds, the harnesses above. A first attempt ran beside another build on the machine and is left out. In its .NET 8 run three of the five base processes of the untouched V10-M ran slow, and that row read 0.67 of base. In its .NET 10 run mask selection over base came within 0.012 of the run kept. In the runs kept, processes more than 1.15 times the fastest of their build and shape fell in three rounds on .NET 10 and one on .NET 8, never more than one of a build and shape, which a median of five leaves out.

| Shape | .NET 10.0.9 | .NET 8.0.28 |
|---|---|---|
| V10-M encode, untouched | 1.00 | 1.00 |
| V12-M encode | 1.08 | 1.07 |
| V19-M encode | 1.09 | 1.04 |
| V26-M encode | 1.07 | 1.03 |
| V40-L encode | 1.08 | 1.04 |
| V12-M mask selection | 1.10 | 1.05 |
| V19-M mask selection | 1.10 | 1.06 |
| V26-M mask selection | 1.10 | 1.06 |
| V40-L mask selection | 1.10 | 1.06 |

In the same runs the change took 0.54 to 0.55 of base's mask selection at versions 12 to 26 and 0.79 at 40 on .NET 10, and 0.56 to 0.57 and 0.84 on .NET 8. At version 17 on .NET 10, the fully optimized first compile of `ScoreColumnWords` (1,203 bytes) calls the software popcount three times and `Row192.MaskLow` once, and its Tier1 code after 20,000 encodes (1,465 bytes) calls neither. With hardware intrinsics off the fully optimized `ScoreTwoWords` and `ScoreThreeWords`, which keep the attribute, call the software popcount at 10 and 15 sites in their row loops on .NET 10 and .NET 8 on x64, where base's `CalculateScorePacked` called it at 30 sites on both. On 32-bit x86 with hardware intrinsics off, .NET 8.0.28 and 10.0.9 compiled `ScoreColumnWords` fully optimized at its first call ("Tier-0 switched to FullOpts", 2,030 and 2,885 bytes), and none of the three called the software popcount. On .NET Framework 4.8 on 32-bit x86, `ModulePlacer.PopCount` stays a method of its own that calls a helper for its 64-bit multiply, and `ScoreTwoWords` and `ScoreColumnWords` call it at 10 and 3 sites.

The vector tiers against the new scalar path through the stage harness's kernel shapes, the AVX2 tier through the dispatch and the 128-bit tier and the scalar path entered directly, each shape alone in its own pinned process, five rounds, .NET 10.0.9. The scalar path's time steps 1.49 to 1.52 times from version 27 to 28 in these runs, where it reads a third word a row:

| Version | AVX2 tier over scalar, default JIT | 128-bit transposed tier over scalar, AVX off |
|---|---|---|
| 12 | 0.44 | 0.73 |
| 20 | 0.40 | 0.72 |
| 27 | 0.43 | 0.73 |
| 28 | 0.36 | 0.69 |
| 40 | 0.35 | 0.67 |

On 2026-10-05 the 128-bit tier had taken 0.39 to 0.71 of the old scalar paths on the JIT without AVX2, default NativeAOT and WebAssembly. Default NativeAOT and WebAssembly were not measured against the new one.
