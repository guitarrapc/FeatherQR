# Reading the three symbologies in one call

## Purpose

One call that returns every symbol of an image whatever its symbology: Standard QR, Micro QR and rMQR together. Today a caller picks a decoder (the scope row "Image detection default" in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions): Standard QR by default, typed entry points for the other two), and the Playground tries the three in that order, reading a Micro QR or rMQR symbol only when no Standard QR symbol read. The maintainer asked for a mixed read on 2026-10-27. This plan builds on the Micro QR and rMQR reads for every symbol, shipped on 2026-10-27 ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#every-symbol-of-an-image)): without them a mixed sheet with two Micro QR symbols would return one of them.

## Where it stands (2026-10-27)

- Nothing of this plan is built. The Micro QR and rMQR reads for every symbol it builds on shipped on 2026-10-27. Each searches every finder in full, and on three of BoofCV's photographs of 60 Standard QR codes the Micro QR read took 14 to 20 times its one-symbol call (the every-symbol section of [qrcode-symbologies.md](../specs/qrcode-symbologies.md#every-symbol-of-an-image)), which is part of what Phase 0 prices.

## Why

- A sheet that mixes symbologies, rMQR beside a Standard QR code on one label, costs three calls, and each pays its own histogram, binarizations and finder scans over the same pixels. Those passes are two thirds of a failing call's time on a photograph.
- A caller who does not know which symbology an image holds has to call three decoders and merge what they return. The Playground does this today, and its fallback shows the shape: it reads Standard QR, and only an image with none goes on.

## What there is to build on

- The three symbologies share the finder pattern, and so the finder scan (`FinderPatternFinder`), the passes (`ImageDecodePasses`), the global and regional binarizations and the histogram. One scan of a pass can serve all three.
- The Standard QR entry scans each pass into one list of 4,096 candidates, hands its stages the first 32, takes a read symbol's candidates out by outline and gives up the head of the list when the stages cannot read it. What its stages leave is the natural input of the single-finder decoders: a candidate not inside any read symbol, and not part of a triple that read.
- Every read carries its corners (`SymbolCorners`), and `SymbolGeometry.AnyContains` tests a point against them whatever the symbology that read.
- The three result types have the same members, `Text`, `Info` and `Success`, and the info types differ. No public type names the three symbologies together, and no enum does.

## The price, and where it is measured

The single-finder decoders search a candidate in full, within their budgets: a false finder candidate outside a read symbol costs Micro QR 32 to 39 times a sized one-symbol call and rMQR about 86 ([single-finder candidate scan](../specs/qrcode-symbologies.md#single-finder-candidate-scan)). On a photograph of Standard QR alone every candidate the Standard QR stages leave is such a candidate, so a mixed read pays those searches for nothing. BoofCV's 536 photographs hold no Micro QR or rMQR symbol and the sweep's Standard QR renders none, so on them a mixed read's time over the Standard QR entry is the price, and every Micro QR or rMQR symbol it returns is a false read. The Micro QR grid evidence and rMQR's format copies are what refuse them, and the photographs are their largest test.

Two shapes bound the price, and Phase 0 measures both:

- The single-finder decoders run on each pass's leftover, inside the pass. Costly on photographs, since a pass of a photograph leaves many candidates, and finds every symbol a pass can show.
- They run once, after every Standard QR pass, on the candidates left outside the read symbols at full size. Cheaper, and a Micro QR symbol only the regional pass can show is read only if the leftover carries its finder from that pass.

## Decisions (the maintainer's)

| | Decision | Recommendation | Ruling |
|---|---|---|---|
| X1 | The result type | One record struct for a symbol of any symbology, with no prefix under the naming rule ([featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md), The naming rule), carrying the text, the corners and the symbology as an enum, and the three info types behind it, two of them default. Three arrays, one a symbology, would make a caller merge by position again, and a common interface over the three results would change public types to serve this call | |
| X2 | The entry's home | A static class with no prefix, in the core on luminance and in the SkiaSharp package on an `SKBitmap` and a stream, since `QRCodeDecoder` means Standard QR under the rule. The names are drafts until Phase 3 | |
| X3 | The order | Standard QR first, since a triple that reads is the strongest claim on three finders, then Micro QR, then rMQR, each taking what it read out of the list. Phase 0 measures the other orders on the mixed sheets and keeps this one unless another reads more | |
| X4 | Where the single-finder decoders run | Once after the Standard QR passes, on the full-size leftover, unless Phase 0 shows the per-pass form reads more on the mixed sheets at a price under 1.5 times the Standard QR entry on BoofCV's photographs | |
| X5 | The Playground | Its option becomes this read, and the fallback goes | |
| X6 | The release | After 2.0.0. The call is additive, and it waits for the Micro QR and rMQR entries | Ruled 2026-10-27: in 2.0.0, as Phase 6g of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md). The Phase 7 freeze waits for Phase 3 |

## What has to stay true

- The three one-symbol calls and the three every-symbol calls compile to the code they have, checked on their listings.
- On BoofCV's photographs the mixed read returns the Standard QR entry's codes, 1,073, and nothing else. On every single-symbol render of the sweep and the corpus, of any symbology, it returns that symbol alone.
- Each symbol is returned once, across symbologies: a candidate inside a read symbol is skipped whichever decoder read the symbol.
- Only the texts are allocated, and nothing on an image with no symbol.
- No public API before Phase 2 rules on it, and no switch in `src` that only a test or a measurement uses.

## Phases

| Phase | Work | Output |
|---|---|---|
| 0 | The price and the ceiling, with no change to `src`. A probe runs the three every-symbol entries in turn over BoofCV's photographs and the sweep's single-symbol renders and merges their results by outline, in both shapes above, and reads mixed sheets composed from the sweep's renders, two symbologies and three on one sheet, flat and through the camera kinds, with the texts known | Time over the Standard QR entry on the photographs, false reads on them and on the renders, and the codes read of the mixed sheets by order and shape |
| 1 | An internal entry over the shared passes: one histogram and one binarization a polarity, one finder scan a pass into one list, the Standard QR stages, then the single-finder decoders on the leftover in the shape X4 rules, every read taking its candidates out of the list. Tests first: a sheet of each pair of symbologies and of all three, each symbol's corners asserted, and the negatives, a Standard QR finder read as a Micro QR symbol, a symbol twice, a second result on a one-symbol image | The codes of Phase 0's sheets, the time and false reads on the photographs, and the six calls' listings |
| 2 | The decisions above, with Phase 0's and Phase 1's numbers | The maintainer's ruling |
| 3 | The public call in the core and the SkiaSharp package, a stream read with limits and its async twin, the approved API listings, `docs/`, the README, the specs and spec maps, and the Playground's option | The feature |

## Documents to change

- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the public API direction, the scope rows "Image detection default" and "Several symbols in one image", the image decode passes, and a record of the mixed read's rules and their measured costs.
- [standardqr-decoder.md](../specs/standardqr-decoder.md), [microqr-decoder.md](../specs/microqr-decoder.md) and [rmqr-decoder.md](../specs/rmqr-decoder.md): what of it reaches each, and a Decisions row where the mixed read changes a rule.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the mixed sheets, the `boofcv` command's columns for the mixed read, and the false-read count.
- The three spec maps, `docs/`, the README, the approved API listings and the Playground, at Phase 3 only.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.
