# Several symbols in one image: Micro QR and rMQR

## Purpose

Standard QR reads every symbol of an image in one call since 2.0.0 (`DecodeImageSymbols`, `DecodeSymbols`: the Decisions row "Several symbols of one image" in [standardqr-decoder.md](../specs/standardqr-decoder.md#decisions)). Micro QR and rMQR read one symbol a call. The ruling of 2026-10-15 left them out until a request or a set of photographs that holds several of them, and the maintainer asked for them on 2026-10-27. This plan brings the two to the same call, measured before any API as the Standard QR work was. Reading the three symbologies together in one call is [mixed-symbology-read-plan.md](mixed-symbology-read-plan.md), which builds on this one.

## Where it stands (2026-10-27)

- Phases 0, 1 and 1b are done (Progress log): the internal entries read the composed sheets with no false read, and return the one-symbol read on every one-symbol image. N1 to N3 were ruled on 2026-10-27 as recommended, and Phase 3 shipped the public calls the same day. What is left is folding this plan, whose numbers and rules are in the specs (Documents to change).

## Why

- A sheet of rMQR labels, or a card of Micro QR codes, returns one code a call. A caller who wants them all paints each read symbol over and calls again, which the Standard QR measurements put at about twice the cost of one call that goes on.
- The Playground's "Read every QR Code in the image" option reads every Standard QR symbol, and one Micro QR or rMQR symbol only when there is none. A visitor with an image of several Micro QR or rMQR symbols sees one.
- Most of what the call needs exists. The shared candidate scan already skips a later candidate inside a symbol that read, every read carries its corners, and the result types the stream reads return (`MicroQRCodeDecodeResult`, `RmQRCodeDecodeResult`) are the types an array of symbols holds.

## What there is to build on

The two decoders read around a single finder through the shared scan ([single-finder candidate scan](../specs/qrcode-symbologies.md#single-finder-candidate-scan)) inside the shared passes ([image decode passes](../specs/qrcode-symbologies.md#image-decode-passes)). The rules that a call for every symbol changes, and the ones it keeps:

- A read ends the scan, and a terminal result ends the passes. For every symbol, neither may: the scan goes on past a read, and the later passes run, as the Standard QR entry learned (30 of its 202 later reads came from a pass the first read had ended).
- A scan decodes its first eight candidates, most confirmed first, and a false hit ranks behind. A sheet of 60 holds more than eight finders, so the cap has to lift, and every candidate past the eighth that is not a symbol is paid for in full. That price is what Phase 0 measures first.
- A later candidate inside a symbol that read but did not fit is skipped, since symbols do not overlap. The same test (`SymbolGeometry.AnyContains`) skips the candidates inside any symbol read so far, in this pass and the passes before it, which is how a symbol is returned once.
- The full sweep runs only when the strided scan read nothing. For every symbol, the sweep has to run after reads too, since a small symbol beside a read one is found only by it (the residual the scan records: a symbol that fits beside one too long, read only with the sweep). What that costs on an image of one symbol is Phase 0's second number.
- Each candidate keeps its own result, within its own budget: Micro QR 10,000 decodes on its arbitrary-orientation path, rMQR 256 a candidate. The budgets stay, and bound what a false candidate can cost.
- Micro QR keeps a read only when the grid's evidence justifies its corrections, and rMQR gates a grid on its two format copies. These are what stand between a Standard QR finder, or texture, and a false Micro QR or rMQR read once the scan no longer stops at the first read. The sweep's single-symbol renders and the corpus, where every second result is a false read, measure them.
- One candidate is one symbol. There is no triple across two neighbours to pass over and no selection to make again, which were most of the Standard QR entry's rules. The Standard QR entry's second round is not needed either: a single-finder scan gives nothing up that a later read could free.
- The Standard QR entry's contract transfers as it is: each symbol once with its text and corners, only the texts allocated, the caller's room ends the search and the array form grows its room, a symbol whose content gives a verdict is left out, and the token is checked between symbols.

## What has to stay true

- The one-symbol calls compile to the code they have, checked on their listings as the Standard QR entry was ([Measuring and verifying a hot-path change](../specs/standardqr-decoder.md#measuring-and-verifying-a-hot-path-change)). The scan is shared, so the every-symbol rules enter it as a struct parameter the one-symbol instantiation does not pay for, or as a scan written beside it. Either way a rule added to one has to be added to the other, and a test holds the entry's first symbol to the one-symbol call's read where the two agree.
- The one-symbol calls read what they read: the sweep's result files, the corpus and the `destination` sets byte for byte.
- On an image of one symbol the entry returns that symbol and nothing else, over every Micro QR and rMQR render of the sweep and the corpus. No symbol is returned twice, and no text is returned that the render did not carry.
- Only the texts are allocated, and nothing on an image with no symbol.
- No public API before Phase 2 rules on it, and no switch in `src` that only a test or a measurement uses.

## Phases

| Phase | Work | Output |
|---|---|---|
| 0 | The ceiling and the price, with no change to `src`. Sheets are composed from the sweep's renders: two, three and four symbols, grids of 9 to 100, one symbology a sheet, flat and through the camera kinds where the renderer takes a card of several symbols, with the texts known. A probe reads each sheet with the one-symbol call, paints the read symbol over and reads again until nothing is found, as the `boofcv` command does. The last call, which reads nothing, is what the entry pays after its last read, and the second call on a one-symbol render is the false-read measurement | Codes read of the sheets by kind and symbology, false reads, the time of the loop against one call, and the cost of the sweep and of the candidates past the eighth on the one-symbol renders |
| 1 | Internal entries, `MicroQRImageDecoder.DecodeSymbols` and `RmQRImageDecoder.DecodeSymbols`, through the shared scan: it goes on past a read, skips the candidates inside every symbol read so far, takes every ranked candidate or the cap Phase 0 justifies, runs the sweep after a read, and the passes go on. Tests first, as the Standard QR entry's: two, three and four symbols, grids, both polarities, a symbol read by the regional pass, room for fewer than the image holds, and the negatives, a symbol twice, a second result on a one-symbol image, a symbol with a content verdict and a read too long for the text buffer. Faults planted in the new rules | Codes against Phase 0's loop, false reads over the sweep's renders and the corpus, the entry's time on the one-symbol renders against the one-symbol call, and that call's listings |
| 1b | Only if Phase 1's time on one-symbol images is over 1.5 times the one-symbol call, or the sheets cost more than their symbols' one-symbol calls together: time by pass and by stage, then whatever the measurement names, with no read moving | Where the time goes, and both calls before and after |
| 2 | The decisions below | The maintainer's ruling |
| 3 | The public calls under the naming rule, by analogy with Standard QR's: `MicroQRCodeDecoder.DecodeImageSymbols` on luminance in an array form and a span form, `MicroQRCodeImageDecoder.DecodeSymbols` on an `SKBitmap` and a stream, with limits, and `DecodeSymbolsAsync`, and the same for `RmQR`. The approved API listings, `docs/`, the README, the specs and spec maps, and the Playground's option | The feature |

Phase 0 needs no code in `src` and settles whether Phase 1 is worth building. Phase 1 is internal. Phase 3 adds public API, and the 2.0.0 Phase 7 freeze waits for it (N4): this plan is Phase 6f of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md).

## Decisions (the maintainer's)

| | Decision | Recommendation | Ruling |
|---|---|---|---|
| N1 | Whether the calls ship | Decided on Phase 0 and Phase 1: ship if the entry reads the sheets' codes with no false read over the sweep and the corpus, and a one-symbol image costs under 1.5 times the one-symbol call. Measured: no false read, and 1.27 times for Micro QR, 3.80 for rMQR. The rMQR bound is below what any call that goes on after a read pays (3.56, Phase 0), and the entry is within 7 % of that. Ship, with the bound read as "within 10 % of Phase 0's floor". The rule Phase 1b measured, sweep candidates one row found left out, would bring rMQR to about 0.83 of that at the cost of 2 to 3 of 11,772 reads, and is not recommended | Ruled 2026-10-27: ship, the bound read as within 10 % of Phase 0's floor. The single-row rule is not taken |
| N2 | The calls' shape | Standard QR's, as above: array and span forms on luminance, the array form on an `SKBitmap` and a stream, an async stream read, and the result types that exist. A different shape would make three APIs where a caller expects one | Ruled 2026-10-27: Standard QR's shape |
| N3 | The Playground | The option reads every Standard QR symbol, and when there is none, every Micro QR symbol, and when there is none, every rMQR symbol. The three together in one read is the mixed plan's | Ruled 2026-10-27: as recommended |
| N4 | The release | After 2.0.0, in 2.1.0. The calls are additive. Hold the freeze only if Phase 3 is ready when the freeze is otherwise ready | Ruled 2026-10-27: in 2.0.0, as Phase 6f of the 2.0.0 plan. The Phase 7 freeze waits for Phase 3 |

## Documents to change

- [microqr-decoder.md](../specs/microqr-decoder.md) and [rmqr-decoder.md](../specs/rmqr-decoder.md): the Image level paragraph, a Decisions row for every symbol with the counts and the costs, and their lessons.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the single-finder candidate scan's rules for every symbol, the scope rows "Several symbols in one image" and "Image detection default", and the public API direction.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the composed sheets and the command that reads them.
- The two spec maps, `docs/`, the README, the approved API listings and the Playground, at Phase 3 only.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.

### Phase 0, the ceiling and the price (2026-10-27)

Done, with no change to `src`. `tools/QRImageDecodeSweep` gained two commands ([qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#image-decode-sweep)). `sheets` composes sheets from the sweep's cases: 2, 3 and 4 symbols and grids of 9 to 100, one symbology a sheet, each symbol in a cell of its own with a two-module quiet zone, drawn crisp, supersampled at any rotation, the same with keystone to 6 %, and through the camera's phone mix with the whole sheet as the card. Three sheets of each layout and kind make 84 sheets and 2,436 codes a symbology. It reads each sheet with the one-symbol call, paints the read symbol over and reads again until a call reads nothing. `again` does the same to the sweep's one-symbol renders and the corpus, where any second read is false.

- Read again reads 2,340 of the 2,436 Micro QR codes and 2,435 of the rMQR codes, with no false read. All 96 Micro QR codes it misses are on the camera sheets, 59 of them on the three 6 × 10 sheets.
- Over 400 cases of every kind and the corpus at four turns, 12,464 Micro QR images and 12,412 rMQR images, no second call reads anything. The one-symbol call reads 11,571 and 11,772 of them.
- Timed over 40 cases of every kind, the second call costs 0.25 times the read for Micro QR and 2.56 for rMQR, summed over the images read. rMQR's camera kind with noise of 12 to 30 grey levels is 19.7: in the noise, false finder candidates are searched in full.
- On one sheet of each layout and kind, the loop costs 205 times one call for Micro QR (2,955 ms against 14.4) and 54 times for rMQR (944 ms against 17.5), and its last call, which reads nothing, 24 and 6.1 times.

Lessons:

- The second call is the floor of any call that goes on after a read through the same passes: it is the search of the image with the read symbol gone. On an image of one symbol that floor is 1.25 times the read for Micro QR and 3.56 for rMQR, so N1's bound of 1.5 cannot be met for rMQR by an entry that searches the rest of the image as the one-symbol call does.

### Phase 1, the internal entries (2026-10-27)

Done. `MicroQRImageDecoder.DecodeSymbols` and `RmQRImageDecoder.DecodeSymbols`, each in an array form whose room grows and a span form that stops when the caller's room is full, both taking a token. They share one generic scan written beside the one-symbol scan (`CandidateScan.DecodeSymbols`, in `CandidateScan.Symbols.cs`), which each decoder's candidate struct joins through `ISymbolsDecoder`: the longest text, and the result a read symbol is returned as. The one-symbol scan is untouched.

The rules:

- The passes all run, in the one-symbol order: the global threshold of each polarity, the regional binarization of each, and the midpoint sweep of a polarity whose global pass found no finder. No read ends them.
- Each reading runs the strided scan and then the full sweep, after a read too. The sweep does not try again a candidate equal to one the strided scan found.
- Every ranked candidate is decoded, not the first eight. A candidate inside a symbol read so far, in this pass or an earlier one, is skipped. A symbol's own finder lies inside its outline, so the skip is also what returns each symbol once: a second check, that a read's corners match a held symbol's, caught nothing the skip did not and was taken out.
- A symbol whose content gives a verdict is left out. The text buffer is the symbology's longest text, so no read is too long for it.
- The token is checked as each reading starts and before each candidate, so an image with no candidate sees it too.
- The buffers (text, modules, two candidate lists of 4,096, one image) are rented once a call. Only the texts are allocated.

Numbers:

- Sheets (84 a symbology): 2,347 of the 2,436 Micro QR codes against read again's 2,340, and 2,435 of the rMQR codes as read again, with no false read. The Micro QR difference is all camera sheets: 8 more on the 6 × 10 sheets and 2 more on the 5 × 5, 3 fewer on the 10 × 10.
- The one-symbol images of Phase 0, all 24,876: the entry returns exactly the one-symbol call's read on each of the 23,343 that call reads, and nothing on the others. No second result anywhere.
- Time on one-symbol images (40 cases of every kind, summed): 1.27 times the one-symbol call for Micro QR and 3.80 for rMQR, which is 1.02 and 1.07 times Phase 0's floor (the read plus the second call). The clean renders are 1.6 to 6.5 times, a few microseconds an image: at 3 to 6 px/module a Micro QR read costs about 5 µs, and the entry about 17.
- Time on sheets (one a layout and kind): 0.28 times read again's loop for Micro QR (828 ms against 2,955) and 0.46 for rMQR (430 against 944). One sheet costs more than its loop: an rMQR 5 × 5 camera sheet, 229 ms against 99.
- The one-symbol calls compile to the code they had. On a probe that reads Micro QR and rMQR renders and noise, every library method the JIT compiled has the same IL size and code size as at the commit before (255 methods), and the listings of the scan, the passes and both decoders' candidate decodes are byte-identical (21 methods, diffable listings, tiered compilation off). The only other difference is the generated name of a local function.
- Tests: `SingleFinderSymbolsDecodeTest` (grids of 2 to 60 turned and under keystone, both polarities, a symbol only the regional pass reads, one only the sweep finds beside a read, one only the midpoint pass reads, a verdict, room for fewer, the longest texts, no symbol, one-symbol images and the corpus, a pre-canceled token and a cancel between symbols) and three allocation tests in `DecodeAllocationTest`. 11 faults planted (`tools/mutants/single-finder-several-symbols.tsv`), 9 caught. The other two change only the time: the sweep trying the strided scan's candidates again, and a midpoint sweep where the global pass found a finder.

Lessons:

- A test of the room has to look at the count returned. The first room test compared the results the caller's span holds, and a fault that let the span form grow past the room passed it.
- A rule a test cannot see is held by another. The corner match was the only thing a fault removing the skip inside a read met, so the skip went unpinned until the corner match went.

### Phase 1b, where the time goes (2026-10-27)

Run, since the one-symbol images cost over 1.5 times the one-symbol call and one sheet costs more than its loop. A copy of the scan timed each reading and counted its candidates.

- After the global pass has read every symbol, the regional binarization makes finder-like texture of flat paper and noise, and every such candidate is searched in full. On the rMQR 5 × 5 camera sheet, 158 failed searches in the regional readings were 199 of the 233 ms, and on the 40 rMQR renders with noise 81 were 82 of 112 ms.
- Candidates the full sweep found on one row were most of it: on the rMQR sheets 136 tried, none read, 39 % of the time. Leaving them out cut rMQR's time to 0.60 on the sheets and 0.83 on the one-symbol images, and Micro QR's to 0.94 and 0.99, with no read moved over the sheets and 40 cases of every kind. Over 400 cases it moved reads: 3 rMQR one-symbol images got no result (the camera kinds with blur, barrel distortion and noise) and the Micro QR sheets lost a code. Leaving them out on the regional readings only still lost 2 (`14 camera, blur` case 329 and `15 camera, barrel` case 139). So it was not kept, and it is a trade-off for N1.
- Nothing else named a cost the entry adds. Its time over Phase 0's floor is 2 to 7 %.

Lessons:

- A rule that moves no read on a part of a set is checked on the whole of it before it is kept: 40 cases a kind showed none of the reads 400 cases lost.

### Phase 2, the rulings (2026-10-27)

N1: ship, with the bound read as within 10 % of Phase 0's floor, and without the single-row rule. N2: Standard QR's shape. N3: the Playground reads every Standard QR symbol, else every Micro QR symbol, else every rMQR symbol.

### Phase 3, the public calls (2026-10-27)

Done. `MicroQRCodeDecoder.DecodeImageSymbols` and `RmQRCodeDecoder.DecodeImageSymbols` on luminance, as an array or into a span, and `DecodeSymbols` on an `SKBitmap` and a stream, with limits, and `DecodeSymbolsAsync`, on `MicroQRCodeImageDecoder` and `RmQRCodeImageDecoder`, each the Standard QR call's twin. The approved API listings, the Playground's API page, the README, `docs/migration.md`, the specs and both spec maps carry them. The sweep tool's `sheets` and `again` score the public call, and give the internal entry's numbers (2,347 and 2,435 codes).

- Tests: `SingleFinderDecodeImageSymbolsTest` holds each public form to the internal entry and to the others (array against span, room for one and none, a bitmap against its luminance, a stream read both ways against the bitmap, a bitmap too small, the token reaching the search), and `ImageStreamDecodeTest` takes the stream reads into its limits and argument checks.
- The Playground, published and read in the browser pane: a sheet of 9 Micro QR codes gave 9 results and one of 4 rMQR codes 4, each with its text.
