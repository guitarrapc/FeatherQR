# Several symbols in one image: Micro QR and rMQR

## Purpose

Standard QR reads every symbol of an image in one call since 2.0.0 (`DecodeImageSymbols`, `DecodeSymbols`: the Decisions row "Several symbols of one image" in [standardqr-decoder.md](../specs/standardqr-decoder.md#decisions)). Micro QR and rMQR read one symbol a call. The ruling of 2026-10-15 left them out until a request or a set of photographs that holds several of them, and the maintainer asked for them on 2026-10-27. This plan brings the two to the same call, measured before any API as the Standard QR work was. Reading the three symbologies together in one call is [mixed-symbology-read-plan.md](mixed-symbology-read-plan.md), which builds on this one.

## Where it stands (2026-10-27)

- Nothing is built. Phase 0 is next.

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
| N1 | Whether the calls ship | Decide on Phase 0 and Phase 1: ship if the entry reads the sheets' codes with no false read over the sweep and the corpus, and a one-symbol image costs under 1.5 times the one-symbol call | |
| N2 | The calls' shape | Standard QR's, as above: array and span forms on luminance, the array form on an `SKBitmap` and a stream, an async stream read, and the result types that exist. A different shape would make three APIs where a caller expects one | |
| N3 | The Playground | The option reads every Standard QR symbol, and when there is none, every Micro QR symbol, and when there is none, every rMQR symbol. The three together in one read is the mixed plan's | |
| N4 | The release | After 2.0.0, in 2.1.0. The calls are additive. Hold the freeze only if Phase 3 is ready when the freeze is otherwise ready | Ruled 2026-10-27: in 2.0.0, as Phase 6f of the 2.0.0 plan. The Phase 7 freeze waits for Phase 3 |

## Documents to change

- [microqr-decoder.md](../specs/microqr-decoder.md) and [rmqr-decoder.md](../specs/rmqr-decoder.md): the Image level paragraph, a Decisions row for every symbol with the counts and the costs, and their lessons.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the single-finder candidate scan's rules for every symbol, the scope rows "Several symbols in one image" and "Image detection default", and the public API direction.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the composed sheets and the command that reads them.
- The two spec maps, `docs/`, the README, the approved API listings and the Playground, at Phase 3 only.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.
