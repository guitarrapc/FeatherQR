# Encode performance measurements (2026-10-02)

For [encode-performance-plan.md](../encode-performance-plan.md), these measurements locate where the three encoders spend their time and test the candidate changes before any of them is written into the library. Nothing in the repository was changed to take them.

All numbers are from one Windows box (Ryzen 9 7950X3D, Zen 4) under .NET 10.0.9 JIT (x86-64-v4, so the AVX2 tiers and GFNI run), BenchmarkDotNet 0.15.8. ARM64, WebAssembly and NativeAOT were not measured.

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

The tier took 0.39 to 0.59 of the scalar kernel's time at versions 12 to 27 and 0.56 to 0.71 at 28 to 40, on every build at every version. The step at version 28 is the tier's: the scalar kernel works on three words at every version from 12, so its time grows evenly, while the transposed tier pays for the words a row has, two up to version 27 and three from 28. The SoA scorers ported to the same vectors in the 128-bit tiers round (2026-09-30), two rows per vector, took 0.94 to 1.68 of the scalar kernel's time on default NativeAOT and 1.40 to 5.80 on WebAssembly.

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

The rows the change does not touch (V19-M Byte, Micro QR M4, rMQR R17x139) read 0.98 to 1.01. At version 1 the payload is under 2 % of the encode, so those rows' 0.94 to 1.01 is the spread, not the writers. ARM64 still runs the SoA scorers for versions 12 to 40 (phase 6), and the stage rows of the change put mask selection at 78 to 80 % of a version 40 encode there (72.6 of 92.3 µs on the JIT, 63.9 of 82.5 µs on NativeAOT) and 68 to 71 % of the version 10 one, so the writers' share is smaller than it will be.

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

With the shares above, the best steps would save 1.2 to 1.8 % of an alphanumeric encode and 0.7 to 0.9 % of a numeric one, and a step taking no time at all would save the share itself, at most 3.2 %. No NEON step ships: ARM64 keeps the portable writers. The step is worth measuring again once ARM64 has a faster scorer for versions 12 to 40. With mask selection at the 0.4 of its time the transposed tier reached on AVX2, a version 40 alphanumeric encode would take about 49 µs, and the step would save about 2.8 % of it (an estimate from the stage rows, not measured).
