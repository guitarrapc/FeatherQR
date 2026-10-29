# Reading the three symbologies in one call

## Purpose

One call that returns every symbol of an image whatever its symbology: Standard QR, Micro QR and rMQR together. Today a caller picks a decoder (the scope row "Image detection default" in [qrcode-symbologies.md](../specs/qrcode-symbologies.md#scope-decisions): Standard QR by default, typed entry points for the other two), and the Playground tries the three in that order, reading a Micro QR or rMQR symbol only when no Standard QR symbol read. The maintainer asked for a mixed read on 2026-10-27. This plan builds on the Micro QR and rMQR reads for every symbol, shipped on 2026-10-27 ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#every-symbol-of-an-image)): without them a mixed sheet with two Micro QR symbols would return one of them.

## Where it stands (2026-10-29)

- Phase 0 is done (Progress log): the shape that skips the candidates inside what the decoders before it read reads every symbol the three calls read, and no false one, at 3.4 times the Standard QR entry on BoofCV's photographs as separate calls. No shape measured comes under X4's 1.5.
- X3 and X4 are ruled (2026-10-29).
- Phase 1 is done (Progress log): the internal entry reads every code of Phase 0's mixed sheets that the three calls read and 16 Standard QR codes more, nothing false on the photographs or the renders, at 2.9 times the Standard QR entry on BoofCV's photographs. Nothing public is built.
- X1 and X2 are ruled (2026-10-29): `SymbolDecodeResult` and the enum `Symbology`, from `SymbolDecoder` in the core and `SymbolImageDecoder` in the SkiaSharp package. A public enum named `Symbology` binds ahead of the `using Symbology =` aliases of two test files, which Phase 3 renames.
- X5 is ruled (2026-10-29): the Playground gains a checkbox that switches its read of every code from one symbology after another to this read.
- The price is ruled (2026-10-29): 2.9 times the Standard QR entry is cut before Phase 3, with no read moved (Phase 2b).
- Phase 2b (Progress log) measured where the time goes, refuted one lever, and shares the image's histogram, at the maintainer's choice of that part of the preprocessing alone: 2.7 times the Standard QR entry on BoofCV's photographs, where Phase 1 took 2.9, with no read moved. Whether that price stands, or Phase 2b goes on, is the maintainer's to rule.

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
| X1 | The result type | One record struct for a symbol of any symbology, with no prefix under the naming rule ([featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md), The naming rule), carrying the text, the corners and the symbology as an enum, and the three info types behind it, two of them default. Three arrays, one a symbology, would make a caller merge by position again, and a common interface over the three results would change public types to serve this call | Ruled 2026-10-29: `SymbolDecodeResult`, a record struct with `Symbology`, `Text`, `Corners` and `Success`, and the symbology's own result in `StandardQR`, `MicroQR` or `RmQR`, the other two default. The enum is `Symbology`, with `StandardQR`, `MicroQR` and `RmQR`. The three result types stay. One result type for every call was weighed and not taken: the info types differ in the types of the version and the level, and in the mask, Structured Append and FNC1, so a call of one symbology would return its info nullable inside it |
| X2 | The entry's home | A static class with no prefix, in the core on luminance and in the SkiaSharp package on an `SKBitmap` and a stream, since `QRCodeDecoder` means Standard QR under the rule. The names are drafts until Phase 3 | Ruled 2026-10-29: `SymbolDecoder` in the core, with `DecodeImageSymbols` on luminance as an array and into a caller's span, and `SymbolImageDecoder` in the SkiaSharp package, extending it with `DecodeSymbols` on an `SKBitmap`, on a stream and on a stream with limits, and `DecodeSymbolsAsync` on a stream with and without limits. The names and overloads are those of each symbology's classes. A call for one symbol of any symbology, a matrix decode and a choice of symbologies are not added until asked |
| X3 | The order | Standard QR first, since a triple that reads is the strongest claim on three finders, then Micro QR, then rMQR, each taking what it read out of the list. Phase 0 measures the other orders on the mixed sheets and keeps this one unless another reads more | Ruled 2026-10-29: Standard QR, then Micro QR, then rMQR, then Standard QR again when Micro QR or rMQR read anything, Phase 0's Q |
| X4 | Where the single-finder decoders run | Once after the Standard QR passes, on the full-size leftover, unless Phase 0 shows the per-pass form reads more on the mixed sheets at a price under 1.5 times the Standard QR entry on BoofCV's photographs | Ruled 2026-10-29: in every pass of the single-finder scan, skipping the candidates inside what was read before, Phase 0's P, not once on Standard QR's leftover. No shape came under 1.5, and trying a finder once lost reads |
| X5 | The Playground | Its option becomes this read, and the fallback goes | Ruled 2026-10-29: the decode card keeps its option to read every code and gains a checkbox beside it, usable while that option is on, that reads every symbol with this read. Off, every symbol is read as the page does now: Standard QR, and with none Micro QR, and with none of those rMQR. One symbol is read as now, Standard QR, then Micro QR, then rMQR. A choice of one symbology on the card was weighed and dropped for this checkbox |
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
| 2b | The price, cut before Phase 3 at the maintainer's ruling: where the mixed read's time goes on BoofCV's photographs, then the changes that take time off with no read moved on Phase 0's sheets, renders and photographs, nor on each symbology's own calls | The time over the Standard QR entry, and what each change saved |
| 3 | The public call in the core and the SkiaSharp package, a stream read with limits and its async twin, the approved API listings, `docs/`, the README, the specs and spec maps, and the Playground's option | The feature |

## Documents to change

- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the public API direction, the scope rows "Image detection default" and "Several symbols in one image", the image decode passes, the internal organization, where the mixed read composes the three symbologies outside the internals, and a record of the mixed read's rules and their measured costs.
- [standardqr-decoder.md](../specs/standardqr-decoder.md), [microqr-decoder.md](../specs/microqr-decoder.md) and [rmqr-decoder.md](../specs/rmqr-decoder.md): what of it reaches each, and a Decisions row where the mixed read changes a rule.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the mixed sheets, the `boofcv` command's columns for the mixed read, and the false-read count.
- The three spec maps, `docs/`, the README, the approved API listings and the Playground, at Phase 3 only.

## Progress log

Each finished phase is appended here: what was done, what it taught, and the numbers it moved.

### Phase 0, 2026-10-29

Done: a probe ran the three every-symbol entries on a scratch copy of the tree at e42919cc, with two switches that the copy alone has. One makes a decoder skip the candidates inside what the decoders before it read, Standard QR's list included. The other makes a single-finder decoder try a finder once, in the first reading that found it, and skip one within half a finder of it in a later reading. The shapes: T, the three calls in turn with nothing skipped and their reads merged. P, each decoder skipping inside the reads before it, which is the per-pass form with each decoder running its own passes. O, P with each finder tried once, the nearest form to running once on the leftover. Q, P in the order Standard QR, Micro QR, rMQR, then Standard QR again when the single-finder decoders read anything.

BoofCV's 536 photographs, each shape once, one thread, a warm-up first. The Standard QR entry took 4.0 s and read 1,073 codes. Micro QR and rMQR read nothing in any shape, so Q's second Standard QR read never ran. Over the Standard QR entry: T 14.7 times, P 3.37, O 3.09. Micro QR and rMQR scan the same passes, and that scan with no candidate decoded took 2.3 s, so with it paid once P comes to 2.78 and O to 2.50. On the seven photographs of 60 codes T was 53 times and P 1.92. P decoded 1,143 Micro QR candidates where T decoded 11,638, each failed one 3.3 to 4.0 ms against rMQR's 0.5 to 0.6. The 79 photographs where Standard QR reads nothing carried 2.5 s of Micro QR's 6.5 s in P.

The sweep's one-symbol renders, 48,240: every kind at 400 Standard QR, 400 Micro QR and 640 rMQR cases, and 100 cases of each lit unevenly at six depths, each with its negative. P and Q returned each symbol the symbology's own call reads, alone: no read lost and no extra read. O lost 210 Micro QR and 420 rMQR reads.

Mixed sheets, 432: Standard QR with Micro QR, Standard QR with rMQR, Micro QR with rMQR, and all three, in three layouts of 2 to 18 symbols, through the sweep's four sheet kinds, three of each, each also lit unevenly at two depths. 3,024 codes, 1,008 a symbology, Standard QR at versions 1 to 10, each symbol in its own quiet zone. T read 2,868. P read 2,868 with Standard QR first, up to 2,879 with it last, and Q 2,884: Standard QR 963, 974 and 979, Micro QR 944 and rMQR 961 in every order. O read 2,790 to 2,805. No shape or order read a false code. The evenly lit sheets read 994 to 996 in every order: the differences are on the unevenly lit ones, and 9 of the 16 more Standard QR codes Q reads are on the sheets of Standard QR beside rMQR.

Lessons:
- Trying a finder once is not the cheap bound X4 assumed. It saves 8 % of P's time, and it loses the reads of the regional pass: a finder the global pass found but could not read is not tried where the regional binarization reads it.
- Standard QR reads more once the single-finder symbols are out of its list. An rMQR finder is a Standard QR finder, and the triple selection takes it in a real one's place. Running Standard QR first keeps the photographs' price, and running it again only after a single-finder read keeps that price too.
- What is left of the price is Micro QR's failed candidates outside every read symbol, in a Standard QR symbol that did not read or in texture, each spending its whole budget.

### Phase 1, 2026-10-29

Done: `MixedImageDecoder`, internal, in a span form and an array form, and `QRImageDecoder.DecodeSymbolsBeside`, Standard QR's read for every symbol with symbols already read seeded into its room. Standard QR's read comes first, as it is. Micro QR and rMQR then share one run of the single-finder scan through a decoder that tries each candidate as a Micro QR symbol, then as an rMQR one, and skips a candidate inside a Standard QR read as the scan skips one inside its own reads. When either reads anything, Standard QR reads again with every symbol read so far seeded, so that their candidates leave its lists. The span form rents its Standard QR room and allocates only the texts. The array form also allocates the rooms its three reads start with, as each symbology's array form does.

The entry, its result `MixedSymbol` and the enum `SymbologyKind` are in the `FeatherQR` namespace, beside the decoders they compose, and the names are drafts until X1 and X2. The internals' dependency rule ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#internal-organization)) keeps shared code and each symbology from referencing another symbology, and `SymbologyDependencyTest` holds `FeatherQR.Internals` to it, so a composition of the three cannot sit there. The enum is not named `Symbology`, which inside `FeatherQR.Tests` would bind ahead of two test files' `using Symbology =` aliases.

Against the Phase 1 row, Standard QR's histogram, binarizations and finder scans are not shared. Its readings keep a list of their own, scan the rows between into it and run at reduced scales, so a shared scan would change what it reads. The scan shared is the single-finder one, as Phase 0's estimate with that scan paid once assumed.

Tests: `MixedSymbolsDecodeTest`, 91 cases a target framework, and two in `DecodeAllocationTest`, six cases. 17 faults are planted in [`tools/mutants/mixed-symbology-read.tsv`](../../../tools/mutants/mixed-symbology-read.tsv), and all are caught.

Phase 0's 432 mixed sheets: 2,884 of 3,024 codes, Standard QR 979, Micro QR 944 and rMQR 961, what Phase 0's Q read, and no false code. The three calls read 2,868. Standard QR's second read added codes on 12 sheets.

Phase 0's 48,240 one-symbol renders: on each, the mixed read returns what the symbology's own call reads, at the same corners, and nothing else. That is 13,020 of 13,600 Standard QR renders, 12,229 of 13,600 Micro QR and 19,839 of 21,040 rMQR.

BoofCV's 536 photographs: the Standard QR entry's 1,073 codes and nothing else. Each entry ran twice a photograph in turn and the faster was kept, in two runs on one machine: Standard QR 3.58 s both times, the mixed read 10.31 and 10.32 s, 2.88 and 2.89 times. Phase 0 estimated 2.78 from single runs with the scan subtracted. The 450 photographs of 1 to 4 codes took 2.46 and 2.48 times, the seven of 60 codes 1.57 and 1.65, and the 79 where Standard QR reads nothing 5.8, 3.1 s of the 10.3.

Listings: a probe ran the six calls on a symbol of each symbology and on noise, each method compiled fully optimized. On .NET 10 x64 the 408 FeatherQR methods compiled had byte-identical listings, addresses masked. On .NET 8 x64 the 420 had the same instructions and the same 343,413 bytes of code. 36 of them differ in immediates only, the class IDs their static bases are looked up by, which moved with the types added.

Lessons:
- Standard QR's rounds loop, shared by its two cores through a method the JIT inlines, kept the core's 312 instructions but moved its stack frame (424 to 408 bytes), and the listing changed. The loop is written out in each. An instruction count does not show such a change, and a listing does.
- What the second read is for, on five of the six sheets probed where it added a code: the Micro QR or rMQR symbols keep Standard QR from reading a QR code it reads with them blanked, or with their reads seeded, and with Standard QR's own reads alone seeded it still misses the code. On the four crisp and supersampled sheets three of them are enough to hide it, some on the far side of the sheet. On the camera sheet of Standard QR and Micro QR, taking away any one of the other three symbols, a QR code among them, lets it read. A column of three rMQR symbols above a QR code in a shadow shows it at 1.75 to 2.25 px a module and is the test.
- On the sixth, a camera sheet of Standard QR and rMQR, the second read finds its two codes with Standard QR's own reads alone seeded: running Standard QR's rounds again would read them too, a lead for its own read of every symbol.

### Phase 2b, 2026-10-29

Where the time goes: a copy of the tree with counters ran the mixed read once a photograph over BoofCV's 536, after a warm-up, while other sessions ran on the machine, so the shares are the reading and the seconds are slower than Phase 1's. Of 13.0 s, Standard QR's read took 4.5 s and the single-finder scan 8.5 s: Micro QR's 1,143 failed candidates 5.0 s, the histogram, the inversions and the regional binarizations 1.5 s (the regional ones 0.6 s), the finder scans 1.1 s, rMQR's failed candidates 0.8 s. Nothing read as Micro QR or rMQR. A failed Micro QR candidate took 3.8 ms at the median. 629 of the 1,143 lie inside a code the set annotates, a Standard QR code that did not read, and 2.4 s of those are on the 79 photographs where Standard QR reads nothing. 253 are a finder tried again in a later pass, which Phase 0's try-once shape showed cannot be skipped.

Refuted: leaving out the candidates at the finders of a triple the Standard QR stages decoded past its format information. On the photographs 528 failed candidates lie there, 3.3 s of the failed candidates' 6.5 s in that run. But on Phase 0's 432 mixed sheets 479 Micro QR reads and 421 rMQR reads are at such a finder: Standard QR's format information reads within its bound on triples of Micro QR and rMQR finders too, so that evidence would lose about 900 reads.

The preprocessing split, in a run of 12.4 s: the histogram 0.82 s, the regional binarizations 0.55 s, the inversions 0.19 s. Standard QR's first round computes each of them at full size from the same pixels and thresholds. Sharing the histogram costs a kilobyte. Sharing an image costs that image's memory, a binarization of each polarity two images' worth, and the maintainer chose the histogram alone.

Done: the histogram shared. The mixed read fills the image's histogram once and gives it to both Standard QR reads, through `DecodeSymbolsBeside`, which takes it in place of filling one each round, and to the single-finder scan, whose decoder fills the scan's histogram (`ISymbolsDecoder.FillHistogram`): Micro QR's and rMQR's from the pixels as before, the mixed read's by copying the one given. Two cases hold the seeded read to the histogram it is given: the image's own reads as the entry does, and a black and white image's on a grey sheet reads nothing. 19 of the 20 faults are caught. The one left, the scan filling its histogram again from the pixels, changes the time only.

Against Phase 1, the same codes at the same corners on Phase 0's sheets (2,884 of 3,024, the second read adding on the same 12 sheets) and on its 48,240 renders, and the Standard QR entry's 1,073 codes on the photographs. Time over the Standard QR entry, each entry twice a photograph in turn with the faster kept, in two runs: 2.67 and 2.70, where Phase 1 took 2.88 and 2.89. The photographs of 1 to 4 codes took 2.21 and 2.23, the seven of 60 codes 1.47 and 1.48, and the 79 where Standard QR reads nothing 5.61 and 5.72.

Listings, as in Phase 1: on .NET 10 x64 the 408 methods compiled have the same code, line for line, and the single-finder scan's passes differ in the JIT's count of the calls it inlined, the decoder's fill being one more. On .NET 8 x64 the 420 have the same instructions and the same 343,413 bytes.

Lessons:
- The histogram is the larger part of the preprocessing, not the regional binarizations.
- Standard QR's format information is no evidence of a Standard QR symbol for this purpose: it reads within its bound on triples of single-finder symbols' finders.
- Left to try: evidence of a Standard QR symbol stronger than its format information, for the 629 failed candidates inside Standard QR codes that do not read.
