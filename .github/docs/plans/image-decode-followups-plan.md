# Image decode follow-ups: WebAssembly timing, several symbols in one image

## Purpose

The photograph plan of 2026-10 is complete and folded into the specs ([README](../README.md)). This plan holds the two pieces of work its measurements still argue for. The other leads left open then are not here. Each stays in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions) (Image decode leads left open) with what would reopen it, and none of those conditions has been met.

## Where it stands (2026-10-15)

- BoofCV's 536 photographs: this library reads 456, zxing-cpp 392 and go-qr 412. Seven are read by one of the others and not here, and 23 here alone.
- The photograph stages were timed on x64 (.NET 10 JIT) and on an Apple M2. On WebAssembly only the halving kernel was, until Work 1 timed the whole chain there on 2026-10-13 and found no fault. The halving's timing had found one no other build has: AOT-compiled, the portable 16-bit shift ran 16 times slower than `PackedSimd`'s, which left the kernel 4.2 times slower than the scalar loop it replaced.
- Each decoder returns one symbol a call. 168 of the 536 photographs hold more than one code: 42 hold two, 116 three, 3 four and 7 sixty, which is 864 of the set's 1,232 codes. zxing-cpp reads 720 of the 864. This library reads one code on 150 of the 168 photographs. On the 368 photographs with one code it reads 306 and zxing-cpp 257.
- Phase 0 of Work 2 (2026-10-14): called again with each read symbol painted over, the one-symbol call reads 765 of the 864 and 1,071 of the 1,232, against zxing-cpp's 720 and 977. No text is unconfirmed and no symbol is read twice.
- Phase 1 of Work 2 (2026-10-14): an internal entry, `QRImageDecoder.DecodeSymbols`, returns the same 1,071 in one call at 2.5 times the one-symbol call's time over the set. The one-symbol call compiles to the code it had. What is left is Phase 2, the maintainer's decisions.
- The entry's time (2026-10-14): measured by pass and by stage, then the two kernels that took most of it were made faster. The entry takes 0.60 of its time over the set and the one-symbol call 0.67, with every read as it was. The entry is 2.2 times the one-symbol call, where it was 2.5.
- Phase 1b continued (2026-10-15): the row scan and the sheets of 60 codes were measured, and nothing was shipped. The entry is 67 ms behind zxing-cpp over those seven, 59 of them on one photograph whose time goes to stages that run after a failure.
- Phase 2 (2026-10-15): the maintainer ruled. The call ships for Standard QR, in an array form and a span form, in 2.0.0, and the Phase 7 freeze waits for it. Phase 3 is next.
- ARM64 (2026-10-15): Phases 1 and 1b were measured on an Apple M2. No read moves, and the one-symbol call compiles to the code it had before the entry, on NativeAOT and on the JIT. Phase 1b takes the entry to 0.83 of its time over the set and the one-symbol call to 0.89, where x64 took them to 0.60 and 0.67, and the entry is 2.3 times the one-symbol call.
- The row scan (2026-10-15): a word's edges are taken out without a branch. On x64 the rows between strided rows take 0.84 to 0.91 of their time, the entry 0.95 of its time over the set and the one-symbol call 0.97, every read as it was. On an Apple M2 (2026-10-15) the same: the rows between take 0.80 to 0.88 of their cycles, the entry 0.94 of its time and the one-symbol call 0.97, so the form stands there.

## Order

Work 1 came first and is done (2026-10-13, [Progress log](#progress-log)). It changed no behaviour, and closed a hole in what was measured of code that already ships. Work 2 started with a measurement that needs no change to `src`, Phase 0, and went on to the internal entry, Phase 1. Both are done (2026-10-14, [Progress log](#progress-log)), and so is Phase 1b, which made the entry and the one-symbol call faster. The maintainer ruled on Phase 2 on 2026-10-15, so Phase 3 is next, and it lands before the 2.0.0 freeze.

## Work 1: the photograph stages on WebAssembly

Done on 2026-10-13 with no finding. The ratios are in the Performance lessons of [standardqr-decoder.md](../specs/standardqr-decoder.md), and what follows is the plan as it was run.

### Why

- The Playground decodes in the browser as WebAssembly, and its Release publish is AOT-compiled.
- Mono's AOT compiler and its interpreter each lower vector code their own way, and neither as RyuJIT or ILC does. The one stage timed there needed a `PackedSimd` method of its own.
- The mesh sampler reaches WebAssembly through the portable 128-bit tier, which the mesh stage rewrote to interpolate through three nodes a side. That rewrite was timed on x64 and ARM64 only.
- The reduced-scale passes, the module-space re-read and the mesh builder run where a read fails or bends, so a slow kernel there shows on the images that take longest already.

### Steps

1. Publish `tests/FeatherQR.WasmReport` interpreted and AOT-compiled, at `57bef5c5^` (the commit before the stages) and at the head. Time the 14 `image/` shapes with `--time` under Node.js, the two commits in turn, the fastest of several processes each.
2. Time x64 the same way at the same two commits with `tests/FeatherQR.AotAnalysis`, so that every shape has a ratio to hold WebAssembly's against.
3. A shape whose WebAssembly ratio is more than 10 % over its x64 ratio, interpreted or AOT-compiled, is a finding. The two shapes with no symbol count as much as the reading ones.
4. A finding is bisected by stage. The stage commits were squashed into #466 and survive only as unreachable objects in the local repository (`30c319a`, `b1e88584`, `9cd29519`, `d2c1972e` and `0fb54421` among them). Bisect before a `git gc` prunes them, or switch one stage's call site off at a time in a scratch tree.
5. A kernel at fault is fixed as the halving was: a method in a `.Wasm.cs` file on `PackedSimd`, a tier in `SimdTiers.cs`, a parity test, and both the interpreter and AOT timed, since one can win where the other loses.
6. With no finding, the ratios go into the record and the work is closed.

### What has to stay true

- Reads do not change. A WebAssembly tier picks the same pixels and the same candidates as its reference (the parity tests, and `--parity` on the report).
- x64 and ARM64 run the instructions they ran.

### Done when

The scope row "Image decode changes of 2026-10 on builds not timed" no longer lists WebAssembly, and the decoder's Performance section carries the WebAssembly ratios beside x64's.

## Work 2: several symbols in one image

### Why

It is the largest difference left against the readers this library was measured with, and the one a caller would notice without a benchmark: a photograph of a shelf or a sheet of labels returns one code. Where a photograph holds one code this library is ahead of zxing-cpp, 306 against 257.

### What has to stay true

- The one-symbol calls run the instructions they run now on the report shapes, and allocate nothing.
- No text is returned that neither a labelled outline nor another reader confirms. The failure to watch is a symbol assembled from the finders of two neighbours, which the crowded-list stage already meets.
- No symbol is returned twice.
- No public API beyond what Phase 2 ruled.
- No switch in `src` that only a test or a measurement uses.

### Phases

| Phase | Work | Output |
|---|---|---|
| 0 | The ceiling, with no change to `src`. A probe in the test project reads a photograph, paints the read symbol's quadrilateral with the image's light level, and reads again until nothing is found. The `boofcv` command gains a column for this library's codes, scored as zxing-cpp's are: located inside a labelled outline, text agreed. go-qr's lines are scored the same way if they carry more than one text a photograph | Codes read of the 1,232 by category, texts no one confirms, the time a photograph, and for each code zxing-cpp reads and the loop does not, where it stopped (no triple, a triple across two symbols, the grid) |
| 1 | An internal entry for Standard QR that returns every symbol in one call, designed from where Phase 0's loop stopped. The expected shape: inside a pass, after a read, the candidates inside the read symbol's outline are dropped and the selection runs again, so no row is scanned twice, and the results of different passes are merged by outline. Tests first: renders of two, three and four symbols and a grid, every symbol's corners asserted. The negatives are a symbol returned twice, a symbol built from two neighbours' finders, and a second result on a one-symbol image | Codes against Phase 0's loop and zxing-cpp's 977, the time of the new call on the 368 one-code photographs against the one-symbol call, and the one-symbol call's instructions on the report shapes |
| 1b | Added on 2026-10-14, when the maintainer asked for the entry's time to be measured and brought down before any decision: the entry's time by pass and by stage on the photographs, then every kernel the measurement names made faster, vector tiers among them. No read may move, and the one-symbol call may not slow down on a report shape | Where the time goes, the time of both calls before and after on each build class, and what was measured and left |
| 2 | The decisions below. Done 2026-10-15 | The maintainer's ruling |
| 3 | The public call for Standard QR in M2's shape, an array form and a span form, the SkiaSharp overload, the Playground, the API page and `docs/`. Before the 2.0.0 freeze (M4) | The feature |

### Decisions (the maintainer's, ruled 2026-10-15)

| | Decision | Recommendation | Ruling |
|---|---|---|---|
| M1 | Whether reading several symbols ships | Decide on Phase 0's number. Under half of zxing-cpp's 720 on the multi-code photographs, the causes come before any API. The number is 765, and Phase 1's entry returns the same 765 in one call | It ships |
| M2 | The call's shape | A span the caller lends for the results and a count handed back, so that nothing but the texts is allocated. Phase 1's entry has this shape and allocates the texts alone | An array form beside the span form, as the one-symbol call has a `string` form beside its `Span<char>` form. The SkiaSharp package has the array form only. The names are settled in Phase 3 |
| M3 | Micro QR and rMQR | Out at first. The photographs counted above are all Standard QR | Out at first |
| M4 | The release | After 2.0.0. The call is an addition, so it does not hold the release | In 2.0.0. The Phase 7 freeze waits for Phase 3 |

## Documents to change

- Work 1: [standardqr-decoder.md](../specs/standardqr-decoder.md) (Performance), the scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions), and [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md) if a tier is added.
- Work 2: [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) (the codes column and its counts), [standardqr-decoder.md](../specs/standardqr-decoder.md) (a Decisions row, Photographs), the scope row "Several symbols in one image", and the spec map. `docs/` and the API page only at Phase 3.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.

### Work 1: the photograph stages on WebAssembly (2026-10-13)

Done, with no finding. Nothing in `src` changed, so no benchmark moved.

What was done:

- `tests/FeatherQR.WasmReport` was published interpreted and AOT-compiled, and `tests/FeatherQR.AotAnalysis` built for the JIT and published as default NativeAOT, at c73392ad (`57bef5c5^`) and at 18747ec9, in two worktrees outside the repository.
- The 14 `image/` shapes were timed with `--time`, each shape in its own process and the two commits in turn, with the processes pinned to two cores and the builds' servers stopped. Each commit had 11 processes on WebAssembly AOT and on NativeAOT, 15 interpreted, and 7 on the JIT with a 3 s warm-up.
- The mesh sampler was timed alone the same way, through its dispatch and through its column table. The perspective sampler, whose code no stage changed, was timed beside it and read 0.99 to 1.01 on the four builds.
- Both WebAssembly builds pass `--simd-class Wasm --parity` at both commits.
- The record is the table and the sampler's figures in [standardqr-decoder.md](../specs/standardqr-decoder.md) (Performance), the scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions), which no longer lists WebAssembly, and the interpreter's two speeds in that document's lessons. No tier was added, so [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md) is as it was.

The numbers, the later commit's time over the earlier one's, each commit's fastest process:

| Build | Ten reading shapes that are not bowed | Bowed, version 25 and 40 | Noise and ramp |
|---|---|---|---|
| WebAssembly AOT | 1.00 to 1.02 | 1.10 and 1.08 | 1.80 and 1.42 |
| WebAssembly interpreted | 0.98 to 1.04 | 1.13 and 1.10 | 1.87 and 1.44 |
| x64, default NativeAOT | 1.00 to 1.04 | 1.10 and 1.10 | 1.83 and 1.40 |
| x64, .NET 10 JIT | 0.98 to 1.03 | 1.10 and 1.20 | 1.83 and 1.40 |

- Against default NativeAOT's ratio, shape by shape, WebAssembly AOT is at 0.98 to 1.01 and the interpreter at 0.97 to 1.04, highest on the bowed version 25 symbol. Against the JIT's they are at most 1.02 and 1.04. The bar was 1.10.
- The mesh sampler through three nodes takes 1.46 times its two-node time AOT-compiled, 1.59 interpreted, 1.34 on NativeAOT and 1.42 on the JIT's AVX2 tier. It still takes 0.34 and 0.45 of the column table's time on the two WebAssembly builds.

What it taught:

- Under Node.js the interpreter runs a process at one of two speeds, 1.3 times apart. The first pass, five processes a commit, read two shapes over the bar (1.14 and 1.16 against NativeAOT's 1.03 and 1.01) and three under 0.92, all of it the mix. `node --no-liftoff` holds the processes to the faster speed. It does not remove the AOT-compiled build's two levels, which are 5 % apart.
- Default NativeAOT is the x64 build to hold WebAssembly against, since both run 128-bit tiers. The JIT's 1.20 on the bowed version 40 symbol is 99 µs added to 507, where NativeAOT adds 79 to 814 and WebAssembly AOT 57 to 705.
- The JIT needs the fastest of several processes too. On one commit the turned version 40 shape read 187 to 192 µs in two processes and 220 to 235 in five, after a 3 s warm-up.
- Step 4's stage commits were not needed. They are still unreachable objects, so a `git gc` can prune them.

### Work 2, Phase 0: the ceiling (2026-10-14)

Done. Nothing in `src` changed, so no benchmark moved. The one change that stays is to `tools/QRImageDecodeSweep`.

What was done:

- `boofcv` now repeats the one-symbol call on each photograph: a read symbol's outline, a module wider on every side, is filled with the symbol's own light level, and the photograph is read again until a call reads nothing. It uses the public call alone. The reads are scored as zxing-cpp's are, and `boofcv-codes.csv` has a row a labelled code with the call that read it and, for a code left unread, what it reads cut out alone.
- go-qr's lines of 2026-10-11 carry one text a photograph (412 lines of one text, 124 of none), so there was nothing of its to score as codes. Its `DecodeAll` was not run.
- Where each unread code stopped, and which pass read each later code, came from a trace of the decoder's searches and decodes in a scratch worktree. That build was never part of the repository and is deleted.
- The record is in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) (the `boofcv` rules and the read-again table), the scope row "Several symbols in one image" and the Photographs section of [standardqr-decoder.md](../specs/standardqr-decoder.md).

The numbers:

| | Codes | One call | Read again | zxing-cpp |
|---|---|---|---|---|
| The 368 photographs of one code | 368 | 306 | 306 | 257 |
| The 168 of more than one | 864 | 150 | 765 | 720 |
| All | 1,232 | 456 | 1,071 | 977 |

- Reading again is level with zxing-cpp or ahead in every category, reads 104 codes zxing-cpp does not and misses 10 it reads.
- All 1,071 reads are agreed and inside a labelled outline. None is a second read of a code, and none differs from zxing-cpp's text at the same code. The photographs counted stay 456, 392 and 210.
- Time on one thread: 11.9 s for the set against 2.6 s for one call and zxing-cpp's 6.9 s. The one-code photographs take 2.8 times the one call. A 60-code photograph takes 400 to 820 ms against zxing-cpp's 40 to 43.
- Five codes are labelled twice, so the set's countable codes are 1,227.
- Of the 156 codes left, 15 read cut out alone. In the last call's trace 79 never had three finders in one pass's list, 68 had their own triple decoded and fail, 7 were tried only with a finder elsewhere and 2 were in a list and never tried.
- Of the 10 codes zxing-cpp reads and reading again does not: 7 no triple, 2 a triple across two symbols (`bright_spots/image022`, `high_version/image030`), 1 the grid (`glare/image037`).

What it taught, for Phase 1:

- The number M1 asks for is 765 on the multi-code photographs, over zxing-cpp's 720 and not under half of it, so no cause has to come before an API. The decoder already reaches the codes. What is missing is a call that returns them without reading the image again for each. The decision stays the maintainer's.
- The expected shape, dropping a read symbol's candidates and selecting again in the same scan, reaches most later reads but not on its own. Of the 202 reads after a photograph's first outside the 60-code photographs, the first call's list already held all three finders of 153 in the pass that later read them. It held one or two of 19, 15 of those from a strided list, and 30 were read by a pass the first call ended before. So the entry has to go on to the later passes after a pass reads, and to scan the rows between when a strided list runs out.
- The candidate list's 32 entries are what a 60-code photograph runs into: ten symbols at most a scan. The first list held all three finders of 59 of the 413 later reads there, and of 367 with the cap at 256 in the scratch build, where the list holds 151 to 182 candidates. The selection over a list that long was not timed.
- The cost to beat is the failing call. A photograph of one code pays 2.8 times for the call that finds nothing more, so the entry's time on the 368 one-code photographs is the number to watch.
- Seven of the 15 codes that read alone had their own triple decoded in the whole image and fail. Cut out, the threshold comes from the symbol's own pixels and the scan's row stride from its height. Which of the two reads them was not measured.
- A label is not a code. The first count of the codes left was 161, and five were second labels of codes already read.

### Work 2, Phase 1: the internal entry (2026-10-14)

Done. `src` gained two files and no existing method changed. Nothing public was written.

What was done:

- `QRImageDecoder.DecodeSymbols` (`QRImageDecoder.Symbols.cs`) writes every symbol of an image into a span the caller lends and hands the count back. It runs the one-symbol call's passes without a read ending them. Each pass scans once into one list, a read symbol's candidates leave the list by its outline, and the stages select again from the rest.
- `FinderPatternFinder.KeptList.cs` holds the search's selection for a list the caller keeps: the strided scan into a list of any length, the skipped rows merged into it, and the triple the search would select from its first 32.
- Tests first: `SeveralSymbolsDecodeTest` (two, three and four symbols, grids of 9 to 100, four versions in one image, symbols read by the negative, the regional pass and at reduced scale, room for fewer than the image holds, the three negatives of this plan and a symbol with a content verdict), `FinderKeptListParityTest` (23 scenes against the search's own selection) and three cases in `DecodeAllocationTest`. The stub that returned the one-symbol call's read left 15 of the first 28 red.
- `tools/QRImageDecodeSweep`: result lines may carry each read's centre (`"at"`), and a reader that does is counted by codes. A reader named `FeatherQR ...` is this library and confirms nothing this library read. The entry was run over BoofCV's set by a probe in the test project, deleted after.
- The record: the Decisions row "Several symbols of one image", two lessons on detection and one on measuring in [standardqr-decoder.md](../specs/standardqr-decoder.md), the scope row, the spec map's "Image level, every symbol", and the entry's table in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md).

The numbers this phase was to give:

| | Codes of 1,232 | Of the 864 on photographs of more than one | Photographs |
|---|---|---|---|
| The entry | 1,071 | 765 | 456 |
| Phase 0's loop | 1,071 | 765 | 456 |
| zxing-cpp | 977 | 720 | 392 |

- Code for code the entry and the loop differ by two: the entry reads `bright_spots/image021`'s first code and not `brightness/image026`'s third. Every read is agreed and inside a labelled outline, and none is a second read of a code.
- Time, in one process, the fastest of three calls: 2.46 times the one-symbol call on the 368 one-code photographs, 2.50 over the set, 59 to 150 ms on a 60-code photograph. The loop takes 2.8 times on the one-code photographs, 11.9 s against the one-symbol call's 2.6 over the set, and 400 to 820 ms on those seven, where zxing-cpp takes 40 to 43 ms. With room for one symbol the entry takes 1.00 of the one-symbol call.
- The one-symbol call's instructions on the report shapes: on the .NET 10 JIT for x64 the 446 library methods compiled while the image and matrix shapes decode are the same instruction for instruction, and on a default NativeAOT publish for x64 all 9,171 methods of the build before are byte for byte the same. The publish is 21 KB larger. ARM64 and WebAssembly were not compared, and the WebAssembly report and the NativeAOT gate pass. ARM64 was compared on 2026-10-15 ([below](#work-2-phases-1-and-1b-on-arm64-2026-10-15)).
- Allocation: the texts and nothing else, on a grid of nine and on one symbol, and nothing on an image with no symbol.
- The suite: 58,068 tests, none failing.

What it taught:

- Phase 0's shape was not enough by itself. With the passes going on after a read and nothing else the entry read 1,038. The stages read 32 candidates and then give a pass up, so what a pass cannot read hid what was scanned after it. A head the stages give up on now leaves the list (1,061), and the passes are run a second time when a later read took candidates out of a list an earlier pass gave up on and a triple's worth is left (1,071). Two rounds read all that four do, and a second round without that rule costs 4.4 times the one-symbol call where the rule costs 2.5.
- Keeping the one-symbol call's code as it was meant writing the entry's pass beside `DecodeLuminanceCore` and not sharing it. The two hold the same stages in the same order, and the tests hold the entry's first symbol to the one-symbol call's read. A stage added to one has to be added to the other.
- A pool of a new element type is not free for the code beside it. The first build rented the candidate list from `ArrayPool<FinderPattern>`, and on NativeAOT `Rent`, `Return` and the bucket setup of every other pool came out the same length with other bytes. The list is rented as bytes.
- 33 faults planted in the two files: 25 caught. The eight left: the two things that keep a symbol from being returned twice, each of which covers for the other and which fail together. The same pair after the rows between are scanned. The order of the two regional polarities, which changes no result. The second round and the inferred stage after a failed triple, which only the photographs show (10 and 6 codes). And the swept triple's decode, which no test render and no photograph of the set needs.

For the maintainer, beside M1 to M4:

- The room the caller lends ends the search, so a caller who knows how many codes to expect pays for no more. A caller who wants all of them pays the failing passes once the last is read: 2.5 times the one-symbol call on a photograph of one code.
- A symbol drawn inside another is not returned once the outer one is read. `qrcode-2/16` of the committed real images gives one symbol where zxing-cpp gives both. It is the only committed image of more than one symbol.
- A symbol whose content gives a verdict is left out of the results. The one-symbol call reports the verdict.

### Work 2, Phase 1b: the entry's time (2026-10-14)

Done. No read moved: the entry's 536 result lines are the same, text for text and centre for centre, on the build with AVX2 and on the one without. The one-symbol call's code changed where it shares the two kernels, and it is faster too.

What was done:

- The entry was timed by pass and by stage over BoofCV's 536 photographs, with counters in a scratch worktree, and profiled by sampling on the unchanged build. Each kernel was then timed alone on the same photographs.
- `LocalBinarizer`, the regional binarization, was rewritten. It read the image three times a polarity. Now a block's sum, minimum and maximum are read once and serve both polarities, a block row's thresholds come from five column sums where they came from 25 black points, and each row is written once against one threshold a pixel and counted as it is written. It has a 256-bit tier, and the 128-bit tier took the same shape. `RegionalRetry` and the entry keep the statistics between the two polarities.
- `Binarizer.FillHistogramVector256` counts its dense blocks into four sub-histograms, as the ARM64 tier does, from the first stretch of an image of 65,536 pixels or more. A rendered symbol has no stretch and rents nothing.
- Tests: `LocalBinarizerParityTest` holds every form the machine runs to the reference written there (4,673 cases a target framework), with the statistics kept between polarities, a proof that kept statistics are not read again, and every form at a page end. `OtsuHistogramParityTest` has inputs long enough for the sub-histograms. The build-class check (`--parity`) has a `LocalBinarizer` entry. 34 faults were planted in the two kernels. Three survived and one was caught only by a decode test, each for an input the tests did not have: a counter running over needs one column light in every step of a row, a minimum passed over needs it in one pixel of a flat block, and the extremes counted before a stretch need a two-valued start. With those inputs all 34 are caught.
- The record: the Performance lessons and the lighting lesson of [standardqr-decoder.md](../specs/standardqr-decoder.md), the Decisions row "Several symbols of one image", the `Binarizer` and `LocalBinarizer` entries and the scope row on builds not timed in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the kernel table of [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md), and the entry's times in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md).

Where the time went before, on x64 with AVX2:

| | Share of the entry's time |
|---|---|
| The two regional binarizations, every scale | 45 % |
| The histograms | 17 % |
| The rows a strided scan skipped | 20 % |
| The strided scans | 4 % |
| Every decode stage | 7 % |
| The inverted images and the halvings | 3 % |

- Three methods held 80 % of the samples: `LocalBinarizer.TryBinarizeCore` 46 %, `Binarizer.FillHistogramVector256` 17 % and `FinderPatternFinder.ScanRowEdges` 17 %.
- The negative passes took 35 % and read none of the 1,071 codes. The set holds no light-on-dark symbol, so they stay.
- A photograph of one code costs the entry what its failing passes cost: each reading scans every row once it has no triple left.

The time of both calls over the 536 photographs, the fastest of three calls a photograph, the two builds in turn:

| Build | One-symbol call, ms | The entry, ms | The entry over the one-symbol call |
|---|---|---|---|
| x64, .NET 10 JIT with AVX2, before | 2,463 to 2,507 | 5,988 to 6,256 | 2.43 to 2.50 |
| The same, after | 1,640 to 1,659 | 3,641 to 3,697 | 2.22 to 2.25 |
| x64, the JIT without AVX, before | 2,612 to 2,745 | 6,551 to 6,788 | 2.47 to 2.51 |
| The same, after | 2,100 to 2,114 | 4,789 to 4,809 | 2.28 |

- With AVX2 the one-symbol call takes 0.67 of its time and the entry 0.60. Without AVX they take 0.79 and 0.72.
- The 368 photographs of one code: the entry 3,353 to 3,600 ms before and 2,054 to 2,082 after, which is 2.10 to 2.13 times the one-symbol call where it was 2.38 to 2.45.
- A photograph of 60 codes: 44 to 123 ms before and 33 to 100 after, in this harness. zxing-cpp took 40 to 43 ms on those seven in Phase 0.
- The regional binarization alone, a pixel: 0.40 to 0.46 ns before and 0.09 to 0.13 after with AVX2, 0.40 and 0.17 without. The second polarity from kept statistics saves the 0.04 the statistics take.
- The histogram alone with AVX2: 0.36 to 0.37 ns a pixel against 0.41 to 0.44, which is 0.81 to 0.91 over three runs.
- The report shapes, after over before. On the JIT with AVX2 the reading shapes are at 0.97 to 1.02, the noise image 0.93 and the gradient 0.50. On default NativeAOT the reading shapes are at 0.99 to 1.01, noise 0.96 and the gradient 0.68. Three processes a side read two of the reading shapes at 1.02 and 1.03, and nine read them at 1.00 and 0.99. On WebAssembly AOT-compiled they are at 0.98 to 1.02, 0.97 and 0.76, and interpreted at 0.96 to 1.04, 0.96 and 0.66. The regional binarization alone takes 0.23 on the JIT, 0.40 on NativeAOT, 0.79 to 0.83 on WebAssembly AOT and 0.53 interpreted.
- The suite: 65,682 tests, none failing. The NativeAOT gate and both WebAssembly reports pass `--parity`.

Where the time goes now, by the same profile: the row scan 38 % with its candidate checks, the histograms 27 %, the regional binarizations about 15 % and the decode stages about 13 %.

What it taught:

- The time was not in the entry's own logic. Two thirds of it was three kernels the one-symbol call runs too, on its failing passes, so making them faster moved both calls. The entry's ratio to the one-symbol call fell less than its time did.
- A kernel that already has a vector tier can still be four times its floor. The regional binarization's phases were timed one by one before any line was changed: the block statistics 0.087 ns a pixel, the flat rule 0.036, the thresholds 0.160, the block writes 0.163 and the count 0.049. The thresholds, a scalar sum of 25 black points a block, took as much as writing every pixel. After: the statistics 0.040, the black points 0.031, the thresholds 0.013 and the rows 0.037.
- The sub-histograms the 256-bit tier was refused four times pay on photographs. The refusals were made on rendered noise, a gradient and photo-like renders. In the ARM64 tier's form, a reference a lane, a photograph counts in 0.81 to 0.91 of the time, and the report's shapes read 0.94 soft, 0.98 to 1.00 on noise and 0.64 on the gradient, so the earlier loss does not show in this form.
- The same lanes do not transfer to the 128-bit tier. On default NativeAOT they took a soft render to 0.90 and a gradient to 0.63 but a two-valued render to 1.07, with the walk shared between the two forms. On WebAssembly AOT-compiled they took noise to 1.09. The 128-bit tier is as it was.
- A scalar loop that reads worse can be the faster one. The flat rule written without branches took 0.041 ns a pixel against 0.031: its chain through the block before is longer than the mispredictions it removes. Eight blocks a step with the flat blocks visited by mask took 0.034 to 0.035.

Measured and left:

- The comparison against the global threshold dropped from the row loop once one pixel has differed: the rows take 0.032 ns a pixel against 0.037. It needs each loop twice.
- The row scan is 38 % of the entry now. The rows a strided scan skipped take 0.08 ns a pixel on a photograph and 0.10 on its regional image, four to five times what inverting the image takes. An extraction that writes eight positions at a time was refused when the kernel was written, and was not tried again.
- The histogram's scalar increment is five instructions a pixel on x64 whatever the lanes. Counting in registers needs the values of a block to stay in a narrow window: 37 % of 32-pixel blocks span 8 values or fewer and 66 % span 16 or fewer, and a window of 16 kept across blocks holds for 3.6 blocks before it moves. No vector count was built.
- The row kernel does not take rows of 4,096 pixels or more. No photograph of the set is that wide.

For the maintainer:

- ARM64 was not timed. Its regional binarization is the 128-bit tier, which changed shape, so the scope row on builds not timed names it. Its histogram tier is untouched. It was timed on 2026-10-15 ([below](#work-2-phases-1-and-1b-on-arm64-2026-10-15)).
- The 128-bit histogram tier could take the sub-histograms on x64 alone, as a tier of its own, if a default NativeAOT publish reading photographs is worth one: on the JIT without AVX the photographs' histogram took 0.82 of its time with them.

### Work 2, Phase 1b, continued: the row scan and the sheets of 60 codes (2026-10-15)

Measured, and nothing shipped. Every change tried either read the same and took the same time, or took less time and read fewer codes. The source is as Phase 1b left it.

Where the 60-code photographs stand, the entry against zxing-cpp's time in Phase 0, the same machine on one thread:

| Photograph | The entry, ms | zxing-cpp, ms |
|---|---|---|
| `lots/image001` | 42.3 | 39.9 |
| `lots/image002` | 33.8 | 39.6 |
| `lots/image003` | 51.8 | 40.7 |
| `lots/image004` | 35.2 | 40.1 |
| `lots/image005` | 43.4 | 43.1 |
| `lots/image006` | 46.9 | 42.2 |
| `lots/image007` | 101.0 | 41.6 |
| All seven | 354 | 287 |

- `lots/image007` is 59 ms of the 67 behind. By stage, timed with counters: the second round 27.8 ms, the inferred stage 26.1, the rows between strided rows 23.9 over 46 scans, the triples' decodes 15.7, the crowded stage 10.1, and the pixel passes 16.6.
- The other six spend their time on the rows between (13 to 14 ms each), the triples' decodes (6 to 20 ms) and the pixel passes (9 ms).

The row scan. On a photograph 4,032 pixels tall the strided scan takes one row in 17, so the scan of the rows between reads the other sixteen, in each of the four readings at every scale.

- Its time on the photographs, split by stopping the kernel early: the dark masks about 35 %, the edges taken out of them about 30 %, the windows classified about 10 % and the cross-checks of the windows flagged about 25 %. No stage is most of it.
- 70 % of the 64-pixel words hold no edge, and a row holds 21.6 dark runs on average.
- Writing the first four edges of each kind a word without a branch took the rows between from 0.081 to 0.105 ns a pixel: it pays four writes on the words that have none.
- Leaving a word with no edge at one test measured inside the noise.
- Scanning every third row between, not every row, read 1,048 codes of 1,071, and the one-symbol call 454 photographs of 456.

Four rules for the entry alone were measured. Once a symbol is read, a reading left with few candidates outside the read symbols scans fewer rows between:

| Rule | The entry's time | The seven sheets | Codes |
|---|---|---|---|
| None, as shipped | 1.00 | 354 ms | 1,071 |
| Under two left: no rows between | 0.75 | 276 ms | 1,069 |
| None left: no rows between | 0.79 | 287 ms | 1,070 |
| None left: every third row between | about 0.86 | 304 ms | 1,070 |
| None left: every second row between | about 0.92 | 370 ms | 1,071 |

- The build before took 354 to 384 ms on the sheets in these runs, and the last two rows are timed against the run before them, so their times are rough.
- The code the third rule loses is `bright_spots/image017`'s second, a version 1 symbol at 9 pixels a module that glare keeps out of the strided rows. So the rows between find hard symbols beside a read one, not only small ones.
- None of the four was taken: each trades codes the set has, or keeps them on this set alone.

`lots/image007`:

- A second round that takes again only the readings a read took given-up candidates from read the same 1,071 codes and took `image007` from 102 to 97 to 99 ms: most of its readings had such candidates. Holding each reading to a triple's worth left, as the round rule does for all of them, lost `noncompliant/image001`'s second code, which a reading reads from two finders. Not taken: it is not the same rule as the round's, and it saves little.
- The inferred stage's centre search spends 90 % of its time sampling the timing patterns, and the frame and its transform about 6 %. Stopping a position's count once its share passed the best so far read the same codes in the same time, since a hypothesis's positions all read about half their modules wrong.
- Stopping the count at the decode bar, a share of an eighth, on both searches changed four photographs' reads and lost `nominal/image001`'s, because the wide search's best seeds the fine one whatever its share. On the fine search alone it read the same and took `image007` from 102.6 to 93.3 ms, with the set level. Not taken: two faults planted in it, the bar on the wide search too and the fine search held to the bar alone, pass every test that reads an inferred finder, so nothing would hold it.

What is left:

- The time on a dense sheet is spread over stages that run because something failed: the second round, the inferred and crowded stages, the triples' second grids. Closing the gap there means reading more symbols in the first round, which changes what is read.
- No test pins the inferred centre search's result: a fault that changed four photographs' reads passed them all.

### Work 2, Phase 2: the decisions (2026-10-15)

Done. Nothing in `src` changed.

The rulings:

- M1: the call ships. It was decided on these numbers: 1,071 of BoofCV's 1,232 codes against zxing-cpp's 977, and 765 of the 864 on photographs of more than one code against 720. No text is unconfirmed and no symbol is returned twice. The entry takes 2.2 times the one-symbol call over the set, and 354 ms on the seven sheets of 60 codes against zxing-cpp's 287.
- M2: an array form and a span form. The recommendation was the span form alone, and writing the shape out changed it. A result holds its text, so a caller cannot `stackalloc` the room and keeps an array anyway. A caller who wants every symbol cannot size the room, since a full room does not say whether more were left. The array form returns every symbol. The span form keeps what the room gives: the search ends when the room is full, so a caller who knows how many symbols to expect pays for no more, and with room for one pays the one-symbol call's time.
- M2's contract stays the entry's: a symbol whose content gives a verdict is left out, and a symbol drawn inside another is not returned once the outer one is read.
- M3: Standard QR only. Micro QR and rMQR wait for a request or a set of photographs that holds several of them.
- M4: in 2.0.0. The call is additive and could ship in 2.1.0, and the maintainer chose 2.0.0 as for the other features. The Phase 7 freeze waits for Phase 3, which [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) lists as Phase 6d.

For Phase 3:

- The entry ends its search when the room is full, so the array form needs a room that grows. Calling the entry again with a larger room would read the image twice.
- The draft names, `QRCodeDecodeResult` for the result and `DecodeImageSymbols` and `DecodeSymbols` for the calls, follow the naming rule and are not settled.
- The SkiaSharp package gets the array form only, as its one-symbol decoder has the `string` form only.

### Work 2, Phases 1 and 1b on ARM64 (2026-10-15)

Measured, and nothing changed. Phase 1 compared the one-symbol call's code on x64 alone, and Phase 1b timed nothing on ARM64, whose regional binarization is the 128-bit tier that changed shape. On an Apple M2 no read moves, the one-symbol call compiles to the code it had before the entry, and the regional binarization runs in 0.47 of its cycles. The calls gain less than on x64: over the set the entry takes 0.83 of its time and the one-symbol call 0.89.

What was done:

- Three builds, each a `git archive` in a scratch tree: 928f40e2 (before the entry), 0dc123f1 (the entry) and 381db167 (Phase 1b, whose `src` is the head's). Apple M2, osx-arm64, SDK 10.0.401 with .NET 10.0.12 and ILCompiler 10.0.12, and .NET 8.0.31 for the suite's other target.
- The suite at the head, in Release and in Debug. `tests/FeatherQR.AotAnalysis` published as default NativeAOT at the three commits, and the head's run with `--simd-class Arm64 --parity`.
- The code: each publish's object file disassembled and compared method by method, bytes and relocations, with the padding between methods left out. On the .NET 10 JIT, with tiering off, every method compiled while the image and matrix shapes decode, at the first two commits.
- The report shapes: instructions and cycles a call from the process's counters, a short loop taken from a long one (1 s and 4 s on NativeAOT, 4 s and 10 s on the JIT), and time on NativeAOT, seven processes a build in turn.
- The photographs: a probe in a scratch project, deleted after, read BoofCV's 536 photographs as grey images written once the way the sweep tool hands them to the readers. It wrote every result of both calls, which `boofcv --lines` scored, and timed both calls on one thread, the fastest of three calls a photograph, four processes a build in turn. Published as NativeAOT, it looped the regional binarization alone and the entry for the process's counters and for a sampling profile (`sample`).

The reads and the suite:

- Before and after Phase 1b both calls return the same results on all 536 photographs, text, version and corners. The one-symbol call reads 456 photographs and the entry returns 1,071 symbols.
- Scored, every count is x64's. The entry reads 1,071 of the 1,232 codes and 765 of the 864 on photographs of more than one, all agreed and inside a labelled outline, none twice. Phase 0's loop reads 1,071 and zxing-cpp 977, and the entry and the loop differ by the same two codes as on x64.
- The suite: 65,682 tests in Release (32,888 on net10.0, 32,794 on net8.0) and 65,446 in Debug, none failing. The head's publish passes `--simd-class Arm64 --parity`: the regional binarization runs its 128-bit tier there, and its `LocalBinarizer` entry matches.

The code, on default NativeAOT for osx-arm64:

- Phase 1: all 12,036 code symbols of 928f40e2's publish, 3,280 of them the library's, are byte for byte the same at 0dc123f1, which adds 64 (40 of them the library's) and is 33 KB larger. On the JIT the 443 library methods compiled while the image and matrix shapes decode are the same instruction for instruction.
- Phase 1b: 3,297 of 3,320 library symbols are the same. What changed is the regional binarization, `RegionalRetry`'s three instantiations, which keep the statistics between polarities, the entry's pass after the positive reading, the tier table, and the 256-bit histogram tier, which ARM64 does not run. The ARM64 histogram tier and `CountGroupLaned`, which moved to `Binarizer.cs`, compile to the bytes they had.

The report shapes, after over before:

| Measure | The 12 reading shapes | Noise | Gradient | Regional binarization alone | Histogram alone |
|---|---|---|---|---|---|
| NativeAOT, instructions | 0.999 to 1.001 | 0.946 | 0.713 | 0.517 | 1.000 |
| NativeAOT, cycles | 0.955 to 1.037 | 0.951 | 0.663 | 0.426 | 1.004 |
| NativeAOT, time | 0.94 to 1.07 | 0.95 | 0.67 | 0.41 | 1.00 |
| .NET 10 JIT, instructions | 0.993 to 1.005 | 0.958 | 0.736 | 0.546 | 1.000 |
| .NET 10 JIT, cycles | 0.925 to 1.072 | 0.963 | 0.725 | 0.466 | 1.003 |

- The reading shapes do not reach the regional binarization, and on NativeAOT every method they run is the same bytes. Their cycles and times spread as this machine does: seven processes of one build read the version 40 shape at 136.7 to 160.0 µs. The time row is the fastest of seven processes.
- x64 had the noise image at 0.93 and 0.96 and the gradient at 0.50 and 0.68, on the JIT with AVX2 and on default NativeAOT. ARM64's 0.95 and 0.67 to 0.74 sit with default NativeAOT's, which runs the same 128-bit tier.
- The scalar form, which ARM64 does not run, takes 0.67 of its instructions on NativeAOT and 0.72 on the JIT.

The photographs, on the .NET 10 JIT, the fastest of three calls a photograph, four processes a build:

| Build | One-symbol call, ms | The entry, ms | The entry over the one-symbol call |
|---|---|---|---|
| ARM64, .NET 10 JIT, before | 2,725 to 3,103 | 6,823 to 7,821 | 2.44 to 2.52 |
| The same, after | 2,366 to 2,441 | 5,513 to 5,741 | 2.33 to 2.35 |

- Each photograph's fastest over the four processes: the one-symbol call 2,569 ms before and 2,276 after (0.89), the entry 6,439 and 5,323 (0.83), and the entry 2.51 and 2.34 times the one-symbol call. On x64 with AVX2 the same calls went to 0.67 and 0.60, and the entry to 2.22 to 2.25 times.
- The fourth process before is the slowest on both calls, 3,103 and 7,821 ms, and ran the cycles the other three did. A whole process, whose loading and warm-up are the same for both builds, ran 452 to 454 billion instructions before and 361 to 365 after (0.80), and 100.4 to 101.2 billion cycles before and 81.9 to 83.8 after (0.82).
- The 368 photographs of one code: the entry 3,677 ms before and 3,054 after, 2.48 and 2.28 times the one-symbol call.
- The seven sheets of 60 codes: 486 ms before and 466 after, of which `lots/image007` 137 and 127.
- Photograph by photograph the entry takes 0.49 to 1.05 of its time. The one-symbol call reaches 1.16 on three photographs of `close`, reads of 5 to 7 ms whose four processes overlap between the builds.

The regional binarization alone on default NativeAOT, one polarity with its statistics read, every twelfth photograph (45) in a loop:

| | Instructions a pixel | Cycles a pixel | ns a pixel |
|---|---|---|---|
| Before | 7.03 | 1.40 to 1.44 | 0.43 to 0.49 |
| After | 3.73 | 0.67 to 0.68 | 0.23 to 0.24 |

- 0.53 of the instructions and 0.47 of the cycles. The report's 742-pixel render takes 7.02 and 3.63 instructions a pixel, and 0.52 cycles after, so a photograph costs a pixel what a render does. x64 runs the photographs at 0.09 to 0.13 ns a pixel with AVX2 and 0.17 without AVX.
- Where it goes after, by samples. On the photographs: the block statistics 35 %, the rows 28 %, the thresholds 17 %, the black points 16 % and the spreading of the thresholds 4 %. On the render: 30, 33, 21, 11 and 5 %.

Where the entry's time goes, by samples on default NativeAOT over the 536 photographs:

| | Before | After |
|---|---|---|
| The regional binarizations, every scale | 37 % | 20 % |
| The row scans, strided and between | 24 % | 31 % |
| The histograms | 20 % | 25 % |
| The candidate checks | 9 % | 12 % |
| Every decode stage and the rest | 7 % | 9 % |
| The inversions and the halvings | 3 % | 3 % |

- Everything but the regional binarizations runs the bytes it ran. Held at the time it took, the shares put the regional binarizations at 0.43 of their time and the entry at 0.79 of its, against the 0.83 the entry measured on the JIT.

What it taught:

- ARM64 gains less because it had less to gain. On x64 the regional binarization was 45 % of the entry and fell to about a quarter of its time with AVX2, and the 256-bit histogram took the sub-histograms the ARM64 tier already had. On ARM64 the regional binarization was 37 % and fell to 0.43 to 0.47, as the 128-bit tier did on x64 without AVX (0.40 to 0.17 ns a pixel), and the histogram did not change.
- Time on this machine moves tens of percent between processes. A first timing of the regional binarization photograph by photograph, the fastest of three calls beside the histogram, put it at 0.58 to 0.69 of its time before, while the untouched histogram moved 0.96 to 1.14 between processes. The loop's ns a pixel moved 0.43 to 0.61 between processes of one build. The process's counters over a short and a long loop gave 0.53 of the instructions, repeating to four digits, and 0.47 of the cycles, repeating within 3 %. A ratio of times here needs a control beside it, and the verdict comes from the counters.

Measured and left:

- The thresholds run scalar on ARM64: only the 256-bit tier has a vector form of them. They take 17 % of the regional binarization on the photographs, where the 256-bit form takes 11 % on x64. A 128-bit form that halved them would save about 8 % of the kernel and under 2 % of the entry, under the 3 % bar.

The record: the scope row "Image decode changes of 2026-10 on builds not timed" in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions), which no longer leaves the regional binarization untimed on ARM64. In [standardqr-decoder.md](../specs/standardqr-decoder.md), the Decisions row "Several symbols of one image" and the Performance lessons: the photographs' time, the regional binarization, the comparison of listings and the lesson on timing. The entry's paragraph in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md).

### Work 2, Phase 1b, continued: the row scan's edges (2026-10-15)

Done. The row scan takes edges out of a word without a branch, and every read is as it was. It gains on x64 and loses a little on WebAssembly. The maintainer asked again for the row scan, after the round above shipped nothing.

What was done:

- The rows between strided rows were timed reading by reading on BoofCV's 536 photographs: global positive and negative, regional positive and negative, at full size. Their time was split by ending the kernel early in a scratch tree.
- The edges came out largest, so `ExtractRowEdges` was rewritten. A word with no edge leaves at one test. Any other has its first four rising and four falling edges written without a branch, and a fifth of either kind loops. The method is not inlined.
- Tests: `FinderRowEdgesTest` checks slots after both arrays, which the extraction writes past its count, and has two rows more, one that alternates every pixel and one whose last end is alone in a word. Ten faults were planted in the extraction and all ten are caught. The first capacity held one position more than any row needs, which a planted fault showed by surviving. It was made exact, and the second row is the one that catches a position too few.
- The suite: 65,682 tests, none failing. `--parity` passes on the JIT with AVX2, with AVX off, on a default NativeAOT publish and on WebAssembly, interpreted and AOT-compiled.

Where the row scan's time went, the rows between at full size, each stage cut off in turn:

| Reading | Dark words | Edges | Classification | Follow-ups |
|---|---|---|---|---|
| Global positive | 21 % | 38 % | 5 % | 36 % |
| Regional positive | 17 % | 48 % | 8 % | 26 % |

- 70 % of a photograph's words hold no edge, and a row holds 21.6 dark runs, so the loop of the old extraction mispredicted its exit on nearly every word that had an edge. The round above refused four writes of each kind without a branch, which paid them on every word: with the test that passes an empty word they pay.
- The follow-ups are the cross-checks. `TryAddCandidate` was called 2.49 million times over the entry's run of the set, and 89 % failed. The merge into the list took 7.0 million turns over all of them, 26 for each that passed, so there is nothing to take there.
- The global negative is the positive's complement on all 536 photographs, so its row scan could take the positive's edges. The regional negative is not. Not built: it saves the dark words and the edges of one reading in four, for edges kept between two readings.

The forms measured, the rows between of the four readings, each kernel a copy of its own in one process against a copy of the old kernel:

| Form | Rows between, over the old kernel |
|---|---|
| A second copy of the old kernel | 0.99 to 1.01 |
| Four of each kind without a branch, out of line (shipped) | 0.84 to 0.91 |
| The same, inlined into the row kernel | 0.93 to 1.03 |
| Three of each kind, out of line | 0.88 to 0.93 |
| Edges dealt to the two arrays by the parity of the edges before them, six or eight a word, out of line | 0.85 to 0.92 |
| The same, inlined | 0.92 to 0.96 |

- A list of edges in row order, dealt out to the two arrays by a vector step at the end of the row, measured no better on the photographs and cost the small renders that step on every row.
- Timed in separate processes first, the same build read 192 to 246 ms on one reading's rows between, more than any form moved. Every figure above comes from one process, the forms in turn on each photograph.

Both calls over the set, each photograph's fastest of four processes a build in turn:

| | Before, ms | After, ms | After over before |
|---|---|---|---|
| The one-symbol call | 1,613.2 | 1,559.6 | 0.967 |
| The entry | 3,659.8 | 3,468.4 | 0.948 |
| The seven sheets of 60 codes, the entry | 353.9 | 345.3 | 0.976 |

- Every result line of both calls is the same, text and centre.
- The report's shapes, the fastest of six processes a build pinned to two cores, on the JIT with AVX2: the Standard QR images 0.85 to 1.01, noise and the gradient 1.00 and 1.01, the Micro QR M4 1.06, the rMQR R7x43 1.01 and R17x139 at 8 px 1.04. On default NativeAOT every shape read 0.97 to 1.01. The row kernel's own shapes read 0.93 and 0.99 on the JIT, 0.96 and 0.99 on NativeAOT.
- Two copies of one kernel in one process read up to 5 % apart on the small renders, and 64 different Micro QR M4 symbols read in turn took 1.00 of their time under the new kernel. So the small renders' 1.04 to 1.06 sit inside what placement moves.
- WebAssembly, the report's shapes, five processes a build in turn. AOT-compiled: the row kernel's shapes 1.01, the images 0.94 to 1.03, the Micro QR M4 1.03 and the rMQR R17x139 at 8 px 1.03. Interpreted, under `node --no-liftoff`: the row kernel's shapes 1.02 and 1.04, the Standard QR images 1.00 to 1.03, noise 1.01, the gradient 1.05, the Micro QR M4 1.08 and the two rMQR at 8 px 1.06 and 1.04. Both pass `--parity`.

What it taught:

- A kernel timed alone in a process beside its own copy is what tells a gain from placement on this machine. Separate processes moved more than the change. A switch between forms inside one method made that method large, and the forms then measured against each other the wrong way round.
- The same extraction gained 0.84 to 0.91 out of line and nearly nothing inlined.

For the maintainer:

- ARM64 runs the same extraction and was not timed. Its population count and trailing-zero count are sequences there. It was timed on 2026-10-15 ([below](#work-2-phase-1b-continued-the-row-scans-edges-on-arm64-2026-10-15)).
- WebAssembly loses where x64 gains: up to 8 % on the small renders interpreted, and up to 3 % AOT-compiled. The Playground's Release publish is AOT-compiled. Photographs were not read on WebAssembly.
- Ruled on 2026-10-15: one form on every build. ARM64 is timed on the M2 next, and the form is looked at again if it loses there.

The record: the Performance lesson on the row scan's edges and the ARM64 kernel's note in [standardqr-decoder.md](../specs/standardqr-decoder.md), and the scope row on builds not timed in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions).

### Work 2, Phase 1b, continued: the row scan's edges on ARM64 (2026-10-15)

Measured, and nothing changed. The ruling asked for ARM64 before the form was taken as final. On an Apple M2 it does not lose: the extraction takes 0.71 to 0.83 of its cycles, the rows between strided rows 0.80 to 0.88, and over the photographs the entry 0.94 of its time and the one-symbol call 0.97, as on x64. No read moves.

What was done:

- Two builds, each a `git archive` in a scratch tree: a2377261, whose `src` is 381db167's, and 8d8526d7. Apple M2, osx-arm64, SDK 10.0.401 with .NET 10.0.12 and ILCompiler 10.0.12.
- The suite at 8d8526d7, in Release and in Debug. `tests/FeatherQR.AotAnalysis` published as default NativeAOT at both commits, its object files compared method by method, and the head's run with `--simd-class Arm64 --parity`.
- The extraction alone: the old form, a second copy of it and the new form, each with its own copy of `DarkWord`, in one process, over every row of every twelfth photograph (45) at the global positive reading and at the regional positive reading. Each form's passes are a method of their own, the forms take turns a photograph, and each turn reads the process's instruction and cycle counters before and after.
- The rows between strided rows: the library's own scan (`FindCandidatesInSkippedRows`) on the same 45 photographs at the same two readings, instructions and cycles a pixel from the process's counters, a 3 s loop taken from a 12 s one, the builds in turn.
- The photographs: both calls on the .NET 10 JIT, the fastest of three calls a photograph, four processes a build in turn, by the probe of the ARM64 round above. The report's shapes: instructions and cycles a call, as in that round.

The reads, the suite and the code:

- Both calls return the same results on all 536 photographs at both commits, text, version and corners: 456 photographs read by the one-symbol call and 1,071 symbols by the entry.
- The suite: 65,682 tests in Release and 65,446 in Debug, none failing. The publish passes `--simd-class Arm64 --parity`. The row kernel runs its ARM64 tier, and the edge-list kernel's entry matches.
- On default NativeAOT 12,142 of the build's 12,145 code symbols are byte for byte the same. The extraction is 377 instructions where it was 321, the row kernel, which inlined it, 554 where it was 846, and `EdgeRunCapacity` is new.

The extraction alone, over the old form in the same process:

| Build | The new form, cycles | Instructions | A second copy of the old form, cycles |
|---|---|---|---|
| Default NativeAOT | 0.71 to 0.73 | 1.03 | 1.00 to 1.02 |
| .NET 10 JIT | 0.82 to 0.83 | 1.22 to 1.24 | 0.98 to 1.01 |

- The two forms agree on every row.
- The JIT runs 1.22 times the instructions and still gains. The two population counts and the four writes of each kind cost instructions on every word with an edge, and the exits the loop mispredicted cost more than they do. ILC's code runs the old form at 1.12 to 1.13 instructions a pixel, where the JIT's runs 0.86 to 0.88, and the new form at 1.16 where the JIT's runs 1.07, so its gain is larger.

The rows between strided rows, the library's scan on default NativeAOT:

| Reading | Cycles a pixel, before | After | After over before | Instructions |
|---|---|---|---|---|
| Global positive | 0.458 to 0.471 | 0.402 to 0.404 | 0.86 to 0.88 | 1.00 |
| Regional positive | 0.683 to 0.684 | 0.543 to 0.548 | 0.80 | 1.02 |

- Every pass finds the candidates it found: 3.3 a call at the global reading and 4.0 at the regional one. x64 measured 0.84 to 0.91 for the rows between of the four readings, by time.

The photographs, on the .NET 10 JIT:

| Build | One-symbol call, ms | The entry, ms | The entry over the one-symbol call |
|---|---|---|---|
| ARM64, .NET 10 JIT, before | 2,341 to 2,390 | 5,499 to 5,592 | 2.34 to 2.37 |
| The same, after | 2,224 to 2,240 | 5,138 to 5,159 | 2.30 to 2.31 |

- Each photograph's fastest over the four processes: the one-symbol call 2,188 ms before and 2,114 after (0.967), the entry 5,179 and 4,885 (0.943). The 368 photographs of one code: the entry 0.948. The seven sheets of 60 codes: 455 ms and 414 (0.910), `lots/image007` 132 and 124. On x64 the same figures were 0.967, 0.948 and 0.976.
- A whole process ran 359 to 360 billion instructions after against 362 to 367 before (0.99), and 79.4 to 80.2 billion cycles against 84.5 to 86.2 (0.94).
- Photograph by photograph the entry takes 0.75 to 1.12 of its time. The three above 1.08, `blurred/image016` and `monitor/image006` and `image007` at 25 to 33 ms, were counted on default NativeAOT: the entry runs 0.94 to 0.96 of its cycles on each, the instructions within 1 %. `lots/image007` runs 0.96.

The report's shapes, after over before, a call:

| Measure | The 8 Standard QR images | Noise | Gradient | Micro QR M4 | rMQR R7x43, R17x139 at 8 px | The row kernel's shapes | Two kernels the change does not reach |
|---|---|---|---|---|---|---|---|
| NativeAOT, instructions | 0.977 to 1.001 | 0.990 | 0.996 | 1.043 | 1.013, 1.021 | 0.982, 0.975 | 1.002, 1.000 |
| NativeAOT, cycles | 0.90 to 1.02 | 1.02 | 1.00 | 1.02 | 0.96, 0.99 | 0.85, 0.98 | 1.00, 1.00 |
| .NET 10 JIT, instructions | 0.946 to 0.997 | 0.974 | 0.976 | 1.033 | 1.006, 1.020 | 0.970, 0.965 | 0.995, 1.004 |
| .NET 10 JIT, cycles | 0.89 to 0.99 | 0.99 | 0.98 | 0.97 to 0.98 | 0.97, 0.98 | 0.93, 0.98 | 0.98, 1.00 |

- The row kernel's shapes are the version 40 render at 3 px and the noise image. The two the change does not reach are the mask walk on that render and the regional binarization.
- The Micro QR and rMQR renders at 8 px run up to 4 % more instructions: a module 8 px wide puts several edges in every word, and each word with one pays the population counts. Their cycles read 0.96 to 1.02.
- The Micro QR M4's JIT cycles read 0.73 in the first pass. Two more rounds of that shape read 0.97 and 0.98: one process before was slow.
- The bowed version 40 render's 1.02 and the noise image's 1.02 on NativeAOT are cycles alone. Their instructions are 0.998 and 0.990.

What it taught:

- A kernel that is called and a kernel that is inlined reach their last tier at different times on the JIT. The first in-process timing ran each form's loop once, in one large method: a copy of the old form read 0.84 of the old form, and the new form 1.43 of its instructions. With each form's passes in a method of its own, the called new form was still at its instrumented first tier through the first photographs, since its Tier1 compile came after them. A whole round over the set before the timed one put both at Tier1, and the copy at 0.98 to 1.01.
- ILC and the JIT disagree by 0.1 on the extraction alone, and agree on what the scan around it gains. The figure a build class gets has to come from that build class.

For the maintainer:

- The form is not looked at again: ARM64 gains in the extraction, the rows between, the entry and the one-symbol call, on both of its build classes. What it costs is 1 to 4 % more instructions on the small renders, which the cycles do not show.
- Only WebAssembly loses, by up to 8 % on the small renders interpreted and 3 % AOT-compiled, as above.

The record: the ARM64 bullet of the Performance lesson on the row scan's edges and the lesson on a called kernel's tier in [standardqr-decoder.md](../specs/standardqr-decoder.md), and the scope row on builds not timed in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions).

### Work 2, Phase 1: a verdict reached by a later stage (2026-10-15)

Fixed after a review of the branch. When the stages after the selected triple reached a verdict on a symbol's content, the entry gave up the head of its list instead of taking that symbol's finders out, and a symbol that reads could be lost with it.

What was wrong:

- `DecodeNextSymbol` handed back the triple a verdict came from only for the selected triple and the triple selected again after the rows between. A verdict from another triple, the crowded list or an inferred finder left it empty, and the caller then gave up the first 32 candidates of the list, the finders of symbols not read yet among them.
- A grid of version 1 symbols with one of them a Kanji cell outside the table showed it. Under keystone, with that symbol top-left in a 2 by 2 grid or in the centre of a 3 by 3, a symbol that reads was not returned. With it at any of the eight other places tried, the triple the verdict came from was handed back and every symbol was read.

What was done:

- The entry's stages take their corner steps through a struct of their own, which keeps the triple a decode gave a verdict for. The stages stop at a verdict, so the triple kept is the one reported. The selected triple, another triple and the crowded list all decode through it.
- The inferred stage hands back the hypothesis its reported result came from. Its two finders are copies of list entries and leave the list, and the inferred one is not a list entry.
- The one-symbol call keeps its corner steps. It calls the inferred stage through the form it had, which discards the hypothesis.
- Tests: `ContentVerdictInAGrid_LeavesEverySymbolThatReads`, the verdict symbol at each place of 2 by 2 and 3 by 1 grids and in the centre of a 3 by 3, flat and under keystone, and `ContentVerdictOfAnInferredFinder_LeavesTheSymbolBesideIt`, two symbols each with a finder blanked. Before the fix three of the eleven cases failed. Without the struct grid cases fail, and so does the earlier verdict test, and without the hypothesis handed back the inferred case fails.
- BoofCV's 536 photographs: both calls return what they returned, the entry's 1,071 symbols. The one-symbol call reports a verdict on none of them.
- The suite: 65,704 tests, none failing.

What it taught:

- A verdict through the inferred stage needed a scene where every pass gives up before it but one. Two symbols that each lack a finder leave four candidates, and the selection takes a triple across the two. When that triple does not read the stages stop there, unless the strided scan undersampled the finders or the list is crowded. In the case kept, that happens at a quarter of the image's size. That stop is how the one-symbol call works too, and was left as it is.
