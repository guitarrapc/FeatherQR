# PNG start marks: a vector tier for the match-start masks

## Purpose

The PNG writer's counting pass marks, before it searches a scanline, every position where a run or a copy of the scanline above may start, so the search visits those positions alone and the bytes between them go into a plain literal loop ([Image output](../specs/qrcode-symbologies.md#image-output), PNG). The marks are built eight positions a step from 64-bit words. This plan gives that step a portable 128-bit tier, sixteen positions a step, under the rules of [the 128-bit round](../specs/qrcode-symbologies.md#the-128-bit-round), and a 256-bit tier where the share left on x64 with AVX2 warrants one. The output stays byte for byte what it is.

The marks were left as the lead when the PNG writing loop closed on 2026-11-06. Whether they are worth a tier is not known: the counting pass went from 202 to 244 µs down to 158 µs when the marks replaced a compare at every position, which is the net of what the marks cost and what they saved, not the marks' own cost. The first phase measures that cost alone and can end the plan.

When the plan completes, its durable content moves into [qrcode-symbologies.md](../specs/qrcode-symbologies.md) (the PNG bullet of Image output, the SIMD tier inventory's counts, lessons) and the reasons beside the kernel's row in `SimdTiers.cs`, and this file is deleted.

## Scope

| In | Out |
|---|---|
| The equal-byte masks of `PngEncoder.MarkStarts` on portable 128-bit vectors, with a platform step behind `IsSupported` where the portable form lowers badly, kept only where it wins | The deflate matcher's search itself, which visits the marked positions and is not a loop over every byte |
| A 256-bit tier for x64 with AVX2, built only if the kernel's share there is still over the bar after the 128-bit tier | The CRC (slicing-by-8, a separate lead on carry-less multiplication) and the Adler-32, each its own kernel |
| The timing shapes for the PNG and SVG writers in the report projects, so the measurement on each build is repeatable | The PNG's scanline packing, which writes from a table |
| The kernel's row in `SimdTiers.cs`, its parity test, its `--parity` entry, and the rendered table | Any change to the bytes a PNG holds |

## What exists

`PngEncoder.MarkStarts` runs once for each module row inside the core, in the counting pass only, and only while the scanline is within a copy's reach (32,768 bytes). Quiet-zone rows skip it, and the writing pass replays the record and never marks. For each 64-bit word of positions it reads eight bytes at a time, compares them with the eight bytes one position back and with the scanline above through zero-byte detection in a word, and gathers each byte's result into a bit. The shift-and-AND passes that turn "equal here" into "equal here and for the next three" (a run) or "the next seven" (a copy), the forced positions (the first, and the last seven), and the mask of the last word follow, on whole words, and the search reads the words through `NextStart`, a trailing-zero count a jump.

A scanline sits in a slot eight bytes longer than itself, so the packing's eight-byte stores and the matcher's eight-byte compares may run past its end. Bytes read past the scanline are noise that reaches only positions the marks force or clear.

The netstandard builds have no vector instructions and keep the word form, which is the kernel's scalar tier. The net10.0 build takes the vector tier through `Vector128.IsHardwareAccelerated`, which x64, ARM64 and WebAssembly with SIMD all report.

## Design

The tier is `MarkStarts` whole, as a method of its own in `PngEncoder.Vector128.cs`, and the word form stays in the stem file as the scalar tier. Both take the slots and the three word spans as arguments, so a parity test and the report projects enter either directly, and the entry on the encoder takes the scanlines the search already holds, reads each slot back from its scanline, and dispatches. The shift-and-AND passes, the forced positions and the last word's mask are static helpers both tiers call. The entry's cost per row is part of the measurement: a first form that computed each slot from its row number and sliced it with a bounds check, and called the tier, cost interpreted WebAssembly two calls a row more than before and the x64 JIT a Micro QR M4 PNG 4 to 8 % (Phase 2 in the Progress log), where the tier itself saved less than that on a 22-byte scanline.

The vector step loads sixteen bytes at a position, sixteen at the position before, and sixteen of the scanline above, compares lane by lane and takes each compare's sign bits as sixteen mask bits, four steps a word. The load one position before the scanline's first byte reads the byte before the slot, so each slot gets one byte of lead, and the loads of the last step read up to fifteen bytes past the scanline, so the slot's slack grows from eight to sixteen bytes. A lane shift of the vector itself would avoid the lead byte, but a byte shuffle is not SSE2 and a default NativeAOT publish would run it as a slow sequence ([the 128-bit round](../specs/qrcode-symbologies.md#the-128-bit-round)). The lead byte's value reaches position 0 alone, which is forced, and the slack's values reach the last seven and the positions past the scanline, which are forced or cleared, as the word form's over-reads already are. The parity test compares the start words, not the intermediate masks, since the two forms may differ in those noise positions and agree in every start.

On ARM64 the sign-bit gather is a short instruction sequence, where x64 has one instruction and WebAssembly its own. Whether the tier wins there is measured, not assumed, and a cell that loses keeps the word form with its reason beside the row.

Nothing allocates. The lead byte and the wider slack cost 9 bytes a slot, from the array pool, for the three slots of a call, or for every slot in a copy's reach at the module sizes where the search also looks positions up by their bytes.

## Open decisions

| # | Question | Recommendation |
|---|---|---|
| D1 | The bar to ship a cell | The round's own rule, unchanged: the tier beats what the build runs, measured on that build, loses nowhere beyond noise, and holds on both interpreted and AOT-compiled WebAssembly. A kernel under about 3 % of every PNG shape on a build stays as it is there, with its reason beside the row |
| D2 | ARM64, which this machine cannot run | The branch is measured on the maintainer's Apple M2 before it merges, as the earlier rounds were. Until then the ARM64 cell is declared as the tier the dispatch takes, and a loss there is fixed by excluding ARM64 in the dispatch, not by a tier of its own, which is a follow-up |
| D3 | A 256-bit tier | In scope, gated: built only if, after the 128-bit tier, the kernel's share on x64 with AVX2 is still over the bar on a PNG shape. The AVX2 JIT is where the benchmark's numbers come from, so a tier there is the one most readers would see. The gate was met and the tier built (Phase 3) |
| D4 | Where the PNG and SVG timing shapes live | In `TierTiming.ImageWriters.cs` of the report projects, committed. The SVG and PNG loops of 2026-11-06 each measured WebAssembly through a scratch copy of such a file, and a third measurement should not need a fourth copy. Done as recommended (Phase 0) |

## Phases

Each phase follows the test-first workflow, and a phase that changes `src` appends a Progress log entry with Done, Lessons and the benchmark delta.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 0 | The marks' share | Done 2026-11-07 (Progress log). `TierTiming.ImageWriters.cs`: the benchmark's PNG and SVG shapes end to end (`png/*`, `svg/*`), the marks alone through their scalar entry (`kernel/PngStartMarks-*`) on the scanlines of those shapes, and a digest of a fixed set of files for byte identity across builds. Run `--time` on the JIT, a default NativeAOT publish, and WebAssembly interpreted and AOT-compiled. No change to `src` beyond making the scalar entry reachable | The marks' share of each PNG shape on each build. Under the bar everywhere, the plan ends here: the figures and the reason go into the PNG bullet and the scope table, and this file is deleted. Measured at 4 to 16 % on the x64 JIT, so the plan went on |
| 1 | The 128-bit tier | Done 2026-11-07. A parity test that enters both tiers directly, red before the tier exists, over random scanlines, the packed scanlines of real symbols at 1 to 33 pixels a module, quiet-zone rows, strides from 2 to a copy's reach, with and without a scanline above. Then the tier, the slot's lead byte and slack, the dispatch, the kernel's row in `SimdTiers.cs`, its files in `SimdTiersTest`, the rendered table, the `--parity` entry and the `-v128` timing shapes | The parity test and the full suite pass, with `NetStandardBuildParityTest.ImageOutput` and the pinned PNG tests holding the bytes. A planted fault in the tier (a cleared mark, a mask placed one step off) fails the parity test |
| 2 | Measure | Done 2026-11-07. End to end and the kernel alone, on the x64 JIT (the benchmark in process, two alternating runs a side, with and without dynamic PGO), a default NativeAOT publish, and WebAssembly interpreted under `--no-liftoff` and AOT-compiled, each shape in its own process on WebAssembly. Where the portable form loses on WebAssembly, try its own instruction behind `PackedSimd.IsSupported` in a `.Wasm.cs` file, kept only where it wins (not needed: the portable form won). The machine code of `Tokenize` and the word form is read on the JIT with and without AVX and on ILC default x64 and `x86-64-v3`, and must not change beyond the dispatch | Each cell decided under D1, its reason beside the row where it keeps the word form, and the bytes identical on every build (the digest). Every cell took the tier, and the entry was reshaped twice on the way (Progress log) |
| 3 | The 256-bit tier | Done 2026-11-07, the gate met (7.2 of about 130 µs at version 40). Thirty-two positions a step in `PngEncoder.Vector256.cs`, the slack to 32, and the x64 with AVX2 cell | The same exits as phases 1 and 2 on the x64 JIT and an `x86-64-v3` publish |
| 4 | Fold | The PNG bullet gains the tier and its figures, the SIMD tier inventory's counts are checked, lessons go to the specs, the maintainer measures ARM64 (D2), and this file and its index entry are deleted | No figure in the specs without its run, and no copy of the plan left |

Phase 0 can end the plan. Phases 1 and 2 are one change, since a tier is not committed before it is measured on the builds it will run on. Phase 3 depends on phase 2's x64 figures.

## What has to stay true

- The bytes do not change. A missed mark still decodes, since the position becomes a literal, so decoding proves nothing here: the pins, the netstandard parity and the digest across builds are the checks.
- The scalar tier keeps its instructions. Moving the word form into a method that takes spans is a refactor, and its loop is compared in the JIT's machine code before and after.
- A tier is portable `Vector128`, with a platform instruction only behind its `IsSupported` check and only where measured to win. A step only WebAssembly needs is gated on `PackedSimd.IsSupported`.
- Both report projects run the tier through its own entry (`--parity`, `--time`), and the table, the source test and the layout test agree on its files.
- No public API, no allocation, and no name of another library or of the private benchmark repository in the code or the specs.

## Progress log

### Phase 0, the marks' share (2026-11-07)

Done. `TierTiming.ImageWriters.cs` in the report projects: the benchmark's PNG and SVG symbols end to end (`png/*`, `svg/*`, the writer form into a buffer that is kept), the marks alone through `kernel/PngStartMarks-*` on the packed scanlines of the same symbols, every core row against the row above, and `--writer-digest`, one FNV-1a hash over 126 PNG and SVG files of seven symbols at nine option sets. The word form of `MarkStarts` became `MarkStartsScalar`, a static method that takes the slots and the three word spans, so the shapes and the parity test enter it directly, and the entry on the encoder slices its fields.

The marks' share on the x64 JIT, the marks' time over the PNG's at the minimums of 11 rounds (another session's builds were running, so the medians spread up to 2x): version 1 at 8 pixels 0.69 of 4.37 µs (16 %), version 40 at 8 pixels 16.3 of 134.6 (12 %), at 3 pixels 8.5 of 130.3 (7 %), at 10 pixels 22.5 of 512.5 (4 %), Micro QR M4 0.48 of 3.71 (13 %), rMQR R17x139 1.32 of 10.9 (12 %). Over the bar on every shape, so the plan went on.

Lessons. The scalar form had to take slots rather than scanlines once the design settled: the 128-bit form reads one byte before the scanline, so a span of the scanline alone cannot state what it reads.

### Phase 1, the 128-bit tier (2026-11-07)

Done. `PngEncoder.Vector128.cs` holds `MarkStartsVector128`: sixteen bytes a load at a position, at the position before and in the scanline above, lane compares, each compare's sign bits into the word. Each slot has one byte of lead (`SlotLead`) and 16 bytes of slack where it had 8, and the lookup's row addressing follows. `FinishStarts` is the shared tail of both tiers (the shift-and-AND passes, the forced positions, the mask of the last word). The dispatch in `MarkStarts` reads `Vector128.IsHardwareAccelerated`. `PngStartMarks` has its row in `SimdTiers.cs` (`Vector128` in every class until the builds are measured), its files in `SimdTiersTest`, its line in the rendered table, and its `--parity` entry.

`PngStartMarksParityTest` compares the start words of both forms over random scanlines, runs of one byte, copies of the scanline above with bytes changed, and the packed rows of five symbols at 14 module sizes from 1 to 33, with and without a scanline above, the lead and slack of each slot filled with different noise for each form. It failed to compile before the tier existed. A planted fault that loads the "byte before" one position off fails it with 4,368 mismatches, the first at stride 9, since every position of a shorter scanline is forced. `NetStandardBuildParityTest.ImageOutput` (170 pairs), the pinned PNG tests and the writer digest (`00e80f39f0fc5196` before and after) hold the bytes.

Benchmark delta, the marks alone on the x64 JIT (fastest of 11 rounds, `-scalar` over `-v128`): version 1 at 8 pixels 0.578 to 0.420 µs, at 10 pixels 0.634 to 0.442, version 40 at 8 pixels 15.38 to 7.53, at 3 pixels 8.49 to 5.28, at 10 pixels 19.15 to 8.34, Micro QR M4 0.476 to 0.339, rMQR R17x139 1.30 to 0.703. End to end and the other builds are Phase 2's.

### Phase 2, measured on each build (2026-11-07)

Done. Two worktrees under a short path, the tree before (HEAD 3d0071a4 with the timing shapes alone) and the tree after, each published as a default NativeAOT build, as WebAssembly interpreted and AOT-compiled, and built for the JIT, one process per shape and tree, trees alternated, on two pinned cores. The digest was `00e80f39f0fc5196` on all eight builds. The bytes being equal, the cells were decided on time alone, and every cell keeps the 128-bit tier: `PngStartMarks` is `Vector128` in all four classes of `SimdTiers.cs`.

The marks alone, scalar to the tier, the fastest of 3 to 5 processes of 7 rounds: on the x64 JIT version 40 at 8 pixels 14.8 to 7.2 µs, at 3 pixels 8.2 to 5.1, version 1 0.58 to 0.41, Micro QR M4 0.45 to 0.33, rMQR R17x139 1.26 to 0.64. On a default NativeAOT publish 15.8 to 7.6, 8.8 to 6.0, 0.72 to 0.59, 0.54 to 0.48 and 1.28 to 0.69. On WebAssembly AOT-compiled 19.1 to 10.0, 12.1 to 8.6, 1.14 to 0.95, 0.87 to 0.78 and 1.60 to 0.93, and interpreted 98 to 72, 64 to 55, 5.8 to 5.4, 4.6 to 4.3 and 8.6 to 6.7.

End to end, the tree after over the tree before by the fastest of five processes a side, for version 1, version 40 at 8, 3 and 10 pixels, Micro QR M4 and rMQR R17x139: a default NativeAOT publish 0.986, 0.968, 1.009, 0.971, 0.991, 0.992. WebAssembly AOT-compiled 0.977, 0.961, 0.973, 0.979, 0.981, 0.964. Interpreted under `--no-liftoff` 0.982, 0.976, 0.992, 0.984, 1.010, 0.996. The two cells past 1.00 are inside their runs' spread (the NativeAOT 3-pixel row read 0.988 and 0.994 in the two earlier passes, and the interpreted Micro QR M4 row 1.008 and 1.044 before the entry was fixed, below). On the x64 JIT the benchmark in process (ShortRun, two alternating launches a side, then three a side with PGO on, PGO off and without AVX for the three smallest rows) put version 40 at 0.94 to 0.98 in its four rows, rMQR at 0.94 to 0.99, and version 1 and Micro QR M4 inside their launches' spread of about 4 %.

The entry's cost was found by this measurement and fixed twice. The first entry computed each slot from its row number, sliced it with a bounds check, and called the tier: interpreted WebAssembly read version 1 at 1.029 and Micro QR M4 at 1.044 while the tier alone won there, since the interpreter paid three calls a row where it had paid one, and `AggressiveInlining` on the slot helpers and the tier took both to 1.001 and 1.008. The x64 JIT then read Micro QR M4 at 1.045 and 1.079 in the benchmark, more than the tier saves on a 22-byte scanline, and the entry now takes the scanlines the search holds and reads each slot back from its scanline with no slot number and no check (`SlotOf`), which put M4 inside its spread. netstandard2.0 has no `MemoryMarshal.CreateReadOnlySpan`, so that target finds the slot by the scanline's offset in the rented rows.

Machine code. The word loop of the scalar tier is instruction for instruction what `MarkStarts` compiled to before, and the method is 238 instructions where it was 272, the fields it read now being arguments. `Tokenize` is 5,402 bytes with AVX and 5,384 without, exactly its sizes before, since the entry kept its signature. The tier compiles to two `pcmpeqb` and two `pmovmskb` a step (`vpcmpeqb` and `vpmovmskb` with AVX), and the entry folds it in, 1,065 bytes where the old `MarkStarts` was 1,011. ILC compiles `Tokenize` to 4,970 bytes for the default x64 target and 4,983 for `x86-64-v3` in both trees, and the entry to 1,122 and 1,059 bytes where the old `MarkStarts` was 1,052 and 1,010.

Lessons. A vector tier's entry is measured with it: the per-row cost of reaching the tier can exceed what the tier saves on a small symbol, and shows only end to end. `--time` on the x64 JIT swings 2 to 3 % between processes on microsecond shapes, so a JIT cell is decided by the benchmark in process over three launches with PGO on and off. A worktree of the commit before lacks a new timing file and needs its own copy with the end-to-end shapes alone. Republishing WebAssembly into the same output folder leaves the old fingerprinted library beside the new one, which `dotnet.js` no longer references, so the folder is cleared first.

Not done. ARM64 (D2) is unmeasured on this machine and the cell is declared as the dispatch takes it. The 256-bit tier (D3) is gated on the share left on x64 with AVX2, which at version 40 and 8 pixels is 7.2 of about 130 µs.

### Phase 3, the 256-bit tier (2026-11-07)

Done, the gate being met. `PngEncoder.Vector256.cs` holds `MarkStartsVector256`, the 128-bit step over thirty-two positions, two steps a word, and the slack grew to 32 bytes. The dispatch takes it first, the x64 with AVX2 cell is `Vector256`, and the parity test, the `--parity` entry and the `-v256` timing shape cover it as the 128-bit tier's do. The parity test was red before the tier existed. The digest is unchanged on the JIT and on an `x86-64-v3` publish, whose `--simd-class` report shows the tier running. The JIT folds it into the entry, 1,070 bytes, with `Tokenize` still 5,402.

The marks alone, 128-bit to 256-bit, the fastest of 3 processes of 7 rounds: on the x64 JIT version 40 at 8 pixels 7.20 to 6.12 µs, at 3 pixels 5.07 to 4.69, at 10 pixels 7.85 to 6.66, version 1 0.40 to 0.38, Micro QR M4 0.34 to 0.31, rMQR R17x139 0.63 to 0.60. On an `x86-64-v3` NativeAOT publish 7.32 to 6.58, 5.67 to 5.48, 8.44 to 7.36, 0.57 to 0.56, 0.46 to 0.45 and 0.68 to 0.63. End to end on that publish, the 256-bit tree over the 128-bit one by the fastest of five processes a side: version 1 0.997, version 40 at 8 pixels 0.967, at 3 pixels 1.004, at 10 pixels 0.995, Micro QR M4 1.000, rMQR 0.971. The benchmark in process on the JIT, two launches a side over the tree before, the 128-bit tree and the 256-bit tree, reads the 256-bit tree at or under the tree before on every row (version 40 at 8 pixels 130 µs against 134 and 133, rMQR 10.4 against 10.7 and 11.2) and mixed against the 128-bit tree, whose two launches of version 40 (121.5 and 126.1 µs) fell below its own four earlier launches (128 to 131): dynamic PGO's inlining of the counting loop differs by process, as the PNG lessons record, so the kernel rows and the `x86-64-v3` publish carry the verdict.

Lessons. On a shape whose scanlines are three words long, a wider step changes little: the tail (the shift-and-AND passes, the forced positions) is the same work, and version 40 at 3 pixels read level on the publish. The gain is in the long scanlines, where the compare loop is the marks.
