# Image decode follow-ups: WebAssembly timing, several symbols in one image

## Purpose

The photograph plan of 2026-10 is complete and folded into the specs ([README](../README.md)). This plan holds the two pieces of work its measurements still argue for. The other leads left open then are not here. Each stays in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions) (Image decode leads left open) with what would reopen it, and none of those conditions has been met.

## Where it stands (2026-10-13)

- BoofCV's 536 photographs: this library reads 456, zxing-cpp 392 and go-qr 412. Seven are read by one of the others and not here, and 23 here alone.
- The photograph stages were timed on x64 (.NET 10 JIT) and on an Apple M2. On WebAssembly only the halving kernel was. That one timing found a fault no other build has: AOT-compiled, the portable 16-bit shift ran 16 times slower than `PackedSimd`'s, which left the kernel 4.2 times slower than the scalar loop it replaced.
- Each decoder returns one symbol a call. 168 of the 536 photographs hold more than one code: 42 hold two, 116 three, 3 four and 7 sixty, which is 864 of the set's 1,232 codes. zxing-cpp reads 720 of the 864. This library reads one code on 150 of the 168 photographs. On the 368 photographs with one code it reads 306 and zxing-cpp 257.

## Order

Work 1 first. It is about a day, changes no behaviour unless it finds a fault, and closes a hole in what was measured of code that already ships. Work 2 starts with a measurement that needs no change to `src`, and stops at a decision that is the maintainer's.

## Work 1: the photograph stages on WebAssembly

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
- No public API before the maintainer rules on it. Until then the entry is internal.
- No switch in `src` that only a test or a measurement uses.

### Phases

| Phase | Work | Output |
|---|---|---|
| 0 | The ceiling, with no change to `src`. A probe in the test project reads a photograph, paints the read symbol's quadrilateral with the image's light level, and reads again until nothing is found. The `boofcv` command gains a column for this library's codes, scored as zxing-cpp's are: located inside a labelled outline, text agreed. go-qr's lines are scored the same way if they carry more than one text a photograph | Codes read of the 1,232 by category, texts no one confirms, the time a photograph, and for each code zxing-cpp reads and the loop does not, where it stopped (no triple, a triple across two symbols, the grid) |
| 1 | An internal entry for Standard QR that returns every symbol in one call, designed from where Phase 0's loop stopped. The expected shape: inside a pass, after a read, the candidates inside the read symbol's outline are dropped and the selection runs again, so no row is scanned twice, and the results of different passes are merged by outline. Tests first: renders of two, three and four symbols and a grid, every symbol's corners asserted. The negatives are a symbol returned twice, a symbol built from two neighbours' finders, and a second result on a one-symbol image | Codes against Phase 0's loop and zxing-cpp's 977, the time of the new call on the 368 one-code photographs against the one-symbol call, and the one-symbol call's instructions on the report shapes |
| 2 | The decisions below. Nothing public is written before them | The maintainer's ruling |
| 3 | Only on a yes: the public call, the SkiaSharp overloads, the Playground, the API page and `docs/` | The feature |

### Open decisions (the maintainer's)

| | Decision | Recommendation |
|---|---|---|
| M1 | Whether reading several symbols ships | Decide on Phase 0's number. Under half of zxing-cpp's 720 on the multi-code photographs, the causes come before any API |
| M2 | The call's shape | A span the caller lends for the results and a count handed back, so that nothing but the texts is allocated |
| M3 | Micro QR and rMQR | Out at first. The photographs counted above are all Standard QR |
| M4 | The release | After 2.0.0. The call is an addition, so it does not hold the release |

## Documents to change

- Work 1: [standardqr-decoder.md](../specs/standardqr-decoder.md) (Performance), the scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions), and [qrcode-simd-tiers.md](../specs/qrcode-simd-tiers.md) if a tier is added.
- Work 2: [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md) (the codes column and its counts), [standardqr-decoder.md](../specs/standardqr-decoder.md) (a Decisions row, Photographs), the scope row "Several symbols in one image", and the spec map. `docs/` and the API page only at Phase 3.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.

Nothing yet.
