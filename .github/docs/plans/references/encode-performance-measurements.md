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

These answer phase 1 of [standardqr-binary-encoder-plan.md](../standardqr-binary-encoder-plan.md). The writer row is the whole data codeword stage (mode, count, payload, padding) for single symbols, and the payload writer alone for Structured Append sets.

| Shape | Writer | Encode | Share |
|---|---|---|---|
| V1-M, 16 alphanumeric | 26.6 ns | 840 ns | 3.2 % |
| V10-M, 300 alphanumeric | 298 ns | 3.22 µs | 9.3 % |
| V40-L, 4,296 alphanumeric | 4.28 µs | 77.7 µs | 5.5 % |
| V40-L, 7,089 digits | 3.66 µs | 70.3 µs | 5.2 % |
| Structured Append, 30,000 alphanumeric (7 symbols at L) | 29.7 µs | 733 µs | 4.1 % |
| Structured Append, 50,000 digits (L) | 25.7 µs | 709 µs | 3.6 % |

The Alphanumeric writer ran at about 1.0 ns a character and the Numeric writer at about 0.52 ns a digit. Every share is above that plan's 3 % bar. Each is a ceiling that grows as mask selection shrinks. With the shared windows alone the V40-L alphanumeric share is about 6 %, and with a scorer at the prototype's speed it would be about 10 % (an estimate from the stage rows, not measured).
