# Mixed read: the cost of a candidate decoded as the other symbology

## Purpose

The read of every symbology ([qrcode-symbologies.md](../specs/qrcode-symbologies.md#every-symbol-of-any-symbology)) decodes each finder candidate Standard QR left over as a Micro QR symbol, then as an rMQR one. A finder of one symbology is a failed candidate of the other, and a failed candidate costs far more than a read. On crisp pages at 4 px a module, Micro QR's failure on an rMQR finder takes about 2.9 ms, its budget of 10,000 arbitrary-orientation attempts, and rMQR's failure on a Micro QR finder 0.4 ms alone and about 3.1 ms with an rMQR symbol in the next cell 320 px away, whose structure its frames reach, where a read of either takes 0.1 to 0.5 ms (probe, 2026-10-30). On the page of twelve symbols, four of each symbology, the single-finder scan is 11.5 of the call's 19.2 ms, almost all of it those failures. On BoofCV's photographs of Standard QR codes, Micro QR's failed candidates were 38 % of the time, 629 of 1,143 of them inside codes that do not read.

The order of the two decoders is not the lever. Tried per candidate the other way round, rMQR first, the page of four rMQR symbols went from 16.5 to 5.6 ms and one rMQR symbol from 5.3 to 2.4, but four Micro QR symbols from 5.6 to 7.8, eight of both from 16.6 to 20.6 and the twelve from 19.2 to 23.5, the photographs unchanged (A/B on a worktree, 2026-10-30). Each order pays the other symbology's failure on the finders it does not own.

This plan finds a test of the symbol's shape at a candidate, cheap against either failure, that says which decoder need not run, with no read lost. Reads are the measure: a rule that loses a read of any set is tightened or dropped ([qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md#image-decode-sweep)).

## What a finder tells about its symbol

| Symbology | From the finder's outer edge along the symbol's two axes | The other two directions | Quiet zone |
|---|---|---|---|
| Micro QR | 4 to 10 modules on both (a square of 11 to 17), timing patterns along row 0 and column 0 | nothing | 2 modules |
| rMQR | 0 to 10 on one (a height of 7 to 17), 20 to 132 on the other (a width of 27 to 139), timing patterns on every edge | nothing | 2 |
| Standard QR | 14 to 170 on both (a side of 21 to 177) | nothing | 4 |

The symbol lies in the quadrant two of the four directions span, and the quiet zone begins at the finder's edge in the other two. The lengths do not overlap: Micro QR ends by 10, Standard QR starts at 14, rMQR's long side at 20.

## Candidate A: the extent probe

For each of the four directions along the symbol's axes, from the finder's outer edge (3.5 modules from its centre), Q is how many modules the band 7 modules across stays dark before a light gap of 1.5 modules: half-module steps, a step light when at most 10 % of its pixels are dark, the walk ended by the gap, by the image's edge (Q unknown) or by a bound. The band spans the whole height of an R7 symbol and the finder's width in the short directions, so single timing modules and alignment rings keep it dark and a 2-module quiet zone ends it.

The rules, each alone enough to skip a decoder, none firing on an unknown Q:
- Not Micro QR when some Q is 13 or more: Micro QR's 10 and a quarter for the module size overestimated. This skips Micro QR at rMQR finders and, on photographs, at the finders of Standard QR codes that did not read, the share of the 38 %.
- Not rMQR when every Q is 12 or less and every gap was found: rMQR's long side of 20 shrunk by 40 % under perspective, which changes the pitch by a quarter across 139 modules ([rmqr-decoder.md](../specs/rmqr-decoder.md)). This skips rMQR at Micro QR finders.
- A dead band: a step is dark when 25 % or more of its pixels are, light at 10 % or less, and a walk that meets a step between gives up, since the regional binarizer's speckle sits there.

Where it runs: in `MixedImageDecoder`'s composite candidate decoder, before either symbology's decoder, so the one-symbology calls keep their behaviour and no switch enters the symbology code. Cost: at most four walks of about 30 half-steps over 7 modules of pixels, some 10 µs at 4 px a module against failures of 0.4 to 3 ms.

The walk must follow the finder's own axes, not the image's. Measured on the pages of the benchmark `SymbolDecodeImageSymbols` with a scratch probe, the walk on the image axes separated the three exactly while the symbols were upright, and the texture page, where 70 to 100 % of the pixels past the 2-module quiet zone are dark, moved nothing:

| Page | Standard QR finders (12 or 36) | Micro QR (4) | rMQR (4) |
|---|---|---|---|
| mixed-12, single-8, qr-page-12, mixed-12-tex, crisp 4 px | 18 / 18 / 0 / 0 | 8 / 8 / 0 / 0 | 20 / 6 / 0 / 0 |

Turned, with the walk still on the image axes (the page scaled by 0.7): Micro QR's longest Q stayed 8 to 9 and Standard QR's 17 to 22 from 0° to 45°, but rMQR's fell from 20 to 12 or 13 at 20° and 7 to 9 at 30° and 45°, where the band walks out through the symbol's slanted edge, and a Micro QR M4 at 45° would reach about 15 along its diagonal. On the image axes the second rule would skip rMQR at its own turned finders. The axes come from the finder's outline (`FinderOutline.TryMeasure`, which rMQR already measures at every candidate, 2.5 % of a turned read) or from the orientation candidates both decoders compute; the image axes serve only a finder the outline measures square.

## Candidate B: stage the two decoders and share the orientation sweep

Each decoder tries the axis-aligned frames first, then runs the angular sweep (`FinderAxisEstimator.FindOrientationCandidates`) for its arbitrary-orientation search, each for itself at the same candidate. Staged per candidate, the cheap frames of both would run first, one sweep would serve both, and the probe along the sweep's axes would gate the two expensive searches. It is the larger change: the composition stays in `MixedImageDecoder` (the symbology seam forbids cross references), so each decoder gains internal entry points for its stages. It is measured after A, which may leave it nothing on sheets.

## Candidate C: the Standard QR triple

The lead recorded with the mixed read: a leftover candidate that forms a triple with two finders of its module size, timing modules between, is a Standard QR finder, whatever its format information reads. A's first rule covers the same candidates without the other two finders, so it is tried first; this stays the alternative for photographs where A's band meets the dead band.

## Measurement plan

1. Characterize, in a scratch worktree with trace lines and nothing changed in the repository: for every candidate the composite decoder sees on the 432 mixed sheets, the one-symbol renders of the three symbologies with their uneven-light and noise kinds, and BoofCV's 536 photographs, record Q in the four directions along the outline's axes and along the image axes, the dark shares met, the finder's turn and keystone, the truth (read as which symbology, inside which read, or none) and what each decoder's failure cost there. The output is the distribution of Q by truth, the thresholds that lose no read, and the saving as the sum of the failures skipped.
2. Implement A with its tests first, as step 1 shaped it: the not-Micro rule alone, judged on the first orientation candidate's axes as a gate on Micro QR's arbitrary-orientation path, handed in by the composite decoder and a no-op in the one-symbology calls. Tests: renders of Micro QR M1 to M4, rMQR R7x27 to R17x139 and Standard QR versions 1, 2 and 10, upright, at 15°, 30°, 45° and 90°, under keystone to 8 %, each asserting the class the probe gives through an internal entry; negatives that give unknown: noise, a speckled quiet zone, a shadowed sheet at the global threshold, a symbol at the image's edge; `MixedSymbolsDecodeTest` reading as before; planted faults for the threshold, the separator skip, the sampling floor and the darkest pair's bound and window.
3. Exactness: `sheets mixed` (2,884 of 3,024, no false read), the one-symbol sweep against the commit before with `--lines` (no diff), `again`, and `boofcv` (1,073 and the time). A read lost anywhere tightens or drops the rule that lost it.
4. Time: the probe pages (mixed-12 19.2, single-8 16.6, rmqr-4 16.5, micro-4 5.6 ms), `sheets mixed --time`, and the BoofCV set, where the first rule's share of Micro QR's 38 % is the number.
5. Decide B from what A leaves on turned sheets.

## Open questions

- Perspective: the thresholds hold for a quarter of stretch at the far end; step 1 says how far the outline's axes drift across a long rMQR symbol under the sweep's keystones.
- The passes: the probe reads the pass's binarized view (global, inverted, regional). The regional pass's speckle sets the dead band's width.
- A symbol whose quiet zone another dark object enters may be skipped where it would not decode anyway. Step 3 counts such reads, if any.

## Progress log

### Candidates and the first probe (2026-10-30)

Done: the three candidates above, the extent probe measured on the benchmark pages upright and turned (scratch, not committed), the A/B of the decoder order. Lesson: on the image axes the walk reads the symbol's extent only while the symbol is upright, and a rule on it would skip rMQR at its own turned finders; the finder's axes are needed before any rule is tried.

### Step 1, the distributions (2026-10-30)

Done, in a scratch worktree whose composite decoder traced every candidate it saw (the probe's Q along the image axes, along each orientation candidate's axes and along the outline's, the darkest pair of steps, each decoder's status and time) and whose sweep tool ran the every-symbology call on the 432 mixed sheets, BoofCV's 536 photographs and the one-symbol renders of the three symbologies (400 cases each, every kind and encoder, 100,800 images), one image at a time. 65,451 candidates, every one later labelled by what it became: a Micro QR read, an rMQR read, or a failure inside a read, inside a labelled code, or in nothing.

Where the single-finder time goes, by candidate: Micro QR's attempt is 86 to 95 % of it on every set, and rMQR's the rest. On the sheets 51 % of Micro QR's time is spent failing at the finders rMQR then reads, 28 % at candidates in nothing (speckle of the regional binarizer, mostly) and 6 % at Standard QR finders; on the photographs 59 % at the finders of labelled codes that did not read and 41 % in nothing; on the rMQR renders 82 % at rMQR's finders; on the Micro QR renders 82 % at candidates in nothing, which no extent rule can touch, and a Micro QR read itself takes 81 µs. rMQR's own failures are 0.4 to 0.9 s a set against Micro QR's 4 to 49 s, and where Micro QR reads rMQR is not tried, so a not-rMQR rule saves 40 ms a set and is dropped.

The probe as measured, after three corrections the first run forced: the walk starts 4.5 modules from the centre, past the 1-module separator every symbology draws, since at 2 to 3 px a module the separator read as the gap; a half-module step is sampled at least twice along and fourteen times across, since below 2 px a module it had no pixel of its own and read light; the stretch's darkest pair of consecutive steps is kept over its first 15 modules, since under the global threshold a shadowed sheet is dark past the quiet zone and the walk ran on through it, where a symbol's modules never make two steps of 90 % dark, and an rMQR sub-finder further out does. The rule judged: not Micro QR when, on the first orientation candidate's axes, some direction's light-biased Q is 15 or more and that direction's darkest pair is under 0.9.

| Set | Candidates | Micro QR reads | Reads the rule would lose | Micro QR time the rule skips |
|---|---|---|---|---|
| Mixed sheets (432) | 2,806 | 944 | 0 | 5,336 of 7,431 ms (72 %) |
| BoofCV photographs (536) | 1,143 | 0 | 0 | 2,601 of 4,332 ms (60 %) |
| Standard QR renders | 14,209 | 0 | 0 | 35,419 of 39,372 ms (90 %) |
| rMQR renders | 24,071 | 0 | 0 | 39,054 of 48,753 ms (80 %) |
| Micro QR renders | 23,222 | 20,449 | 0 | 71 of 10,184 ms |

The variants measured and set aside: the image axes lose 3 sheet reads and 6 Micro QR renders (turned symbols in a textured scene, the band walking out through a slanted edge); the outline's axes lose none but skip less on the photographs (1,193 ms); requiring every orientation candidate to agree loses none and skips a fifth; the corner condition (the opposite direction empty) adds no safety once the darkest pair is checked and costs a seventh of the sheets' saving and half of the photographs'; without the darkest pair the first orientation's axes lose 14 sheet reads, every one under a shadow or a ramp of light at the global threshold; a threshold of 13 loses the same, 20 skips 8 % less. Without the darkest pair's 15-module window, rMQR's sub-finder was read as a shadow and a third of the rMQR finders' saving went.

What the probe costs at a candidate on a crisp 4 px page: the module-size refinement 0.5 µs, the four walks 7 µs, the orientation sweep 43 µs, against a Micro QR read of 80 µs and a failure of 1,900 to 4,000. The sweep is the price, and both decoders already run it for their arbitrary-orientation search, so the rule goes where the sweep is: a gate on Micro QR's arbitrary-orientation path, handed in by the composite decoder and a no-op in the one-symbology calls, which keeps the symbology seam and leaves the axis-aligned frames, which cost little, to run first. Candidate B is then only the sharing of the sweep between the two decoders, which the gate does not need.

Not touched by any extent rule: the candidates in nothing on the Micro QR renders and the sheets, speckle the regional binarizer leaves, where Micro QR spends its whole budget. That is Micro QR's own failure cost on noise, a lever of its own.

The probe sources live in the session's scratch and are not kept; the trace format and the analysis are described above well enough to rebuild them.

### Steps 2 to 4, the gate built and measured (2026-10-30)

Done. `FinderExtentProbe` (shared image-decoder code) walks the four directions of a finder's axes as step 1 shaped it and says whether the symbol extends past a length; `IOrientationSearchGate` is the question Micro QR's candidate decode asks once its angular sweep has the axes, before the arbitrary-orientation search, `SearchAlways` the answer of the one-symbology calls; the composite decoder of the read of every symbology hands in a gate that says no where the probe finds 15 modules or more. Written test-first in `FinderExtentProbeTest`: Micro QR M1 to M4, rMQR R13x27, R9x59 and R17x139, Standard QR versions 5 and 10, each through eight renders (2.5 to 6 px a module, 0° to 90°, keystone to 6 %), a Micro QR with a Standard QR code two modules past its quiet zone and the other way round, a shadow over the quiet zone, a symbol cut by the image's edge, axes below half a pixel. Small Standard QR versions and the 7-module rMQR heights are not asserted: the other finders' separators lie within the length for versions 1 and 2, and a light column of data in the band ends the walk early for both, which keeps their search and loses a saving, not a read.

Reads unchanged, image for image with corners, against the reads logged in step 1: the 432 mixed sheets (2,884 of 3,024 codes, no false read, 979 Standard QR, 944 Micro QR, 961 rMQR, 16 beyond Standard QR's call), BoofCV's 536 photographs, and the 100,800 one-symbol renders the two runs share (the second run also drew 27,163 renders of an encoder the first run's worktree had not located). Full test suite green on net8.0 and net10.0. Faults planted: 8 in `tools/mutants/mixed-symbology-read.tsv`, 7 caught by `FinderExtentProbeTest`, `MixedSymbolsDecodeTest` and `SingleFinderSymbolsDecodeTest`, the gate that always searches a change of time only.

Time, the probe pages and photographs of step 1 (fastest of 25, two pinned cores, two runs each, ms): mixed-12 19.4 to 19.5 -> 9.2 to 9.3, its single-finder scan 11.6 -> 1.4; single-8 16.9 to 17.0 -> 6.4; mixed-12-tex 31.5 to 31.8 -> 15.4 to 15.6; qr-one, no-symbol and qr-page-12 unchanged at 4.1, 4.0 and 2.9. Photographs: nominal/image001 12.5 -> 5.4, its scan 8.0 -> 0.7; lots/image001 (60 codes) 58.1 -> 52.8; shadows 9.5 -> 9.4; nominal/image002 and blurred unchanged at 0.7 and 0.4. Against the public Standard QR call, the twelve mixed symbols went from 2.7 to 1.3 times and the 60-code photograph from 1.47 to 1.34.

What is left of the scan's time on the pages is the reads themselves and the pass overhead the scan shares with nothing; on the 60-code photograph, 17 ms of 53, the passes over 12 megapixels (candidate B of the plan, the pass sharing the maintainer set aside) and Micro QR's failures at finders the probe cannot tell (speckle, small versions). Candidate B's sweep sharing is moot: the gate reuses Micro QR's sweep.

### Step 5, what the scan's passes cost beside Standard QR's (2026-10-30)

Measured with the gate in place, each stage of both reads timed inside the mixed call in a scratch worktree (two pinned cores, the code warm, 10 calls), ms a call:

| Image | Mixed call | Scan: strided scans | Scan: full sweeps (midpoint ones included) | Scan: regional binarizations | Scan: inversions | Scan: candidates | Standard QR: everything else |
|---|---|---|---|---|---|---|---|
| no-symbol texture, 1.2 MP | 4.0 | 0.30 | 1.05 | 0.23 | 0.04 | 0.00 | 2.4 |
| one Standard QR symbol, 1.2 MP | 4.0 | 0.30 | 1.05 | 0.23 | 0.04 | 0.00 | 2.4 |
| twelve Standard QR symbols | 3.0 | 0.13 | 0.42 | 0.20 | 0.05 | 0.01 | 2.2 |
| twelve of three symbologies | 12.1 | 0.14 | 0.44 | 0.25 | 0.05 | 0.37 | 10.9 |
| BoofCV lots/image001, 12 MP, 60 codes | 63 | 2.5 | 8.1 | 2.4 | 0.5 | 1.7 | 48 |
| BoofCV shadows/image001, 3 MP | 10.1 | 0.35 | 0.90 | 0.59 | 0.06 | 3.4 | 4.8 |
| BoofCV nominal/image001 | 3.9 | 0.06 | 0.22 | 0.04 | 0.01 | 1.55 | 2.0 |

The scan's passes are 40 % of the call on an image of Standard QR codes or of nothing at 1.2 MP and 24 % on the 60-code photograph, and 70 % of them are the finder scans, the full sweep of every row above all (5 sweeps a call: the four readings and a midpoint pass), not the binarizations: the two regional binarizations are 16 %, the inversions 3 %. The candidates left are 10 % on the pages and the nominal photograph and a third on the shadowed one, where the probe gives no answer in the shadow and Micro QR spends its budget, the guard's price.

What can be shared with Standard QR's read, which runs the same four readings at full size before its reduced scales:
- The strided lists, bit for bit: the scan's strided list is what `FinderPatternFinder.FindCandidatesStrided` leaves on the same binarization at the same threshold, which Standard QR's reading computes and then consumes (candidates inside reads leave, heads given up on leave, hits of the rows between are averaged in). A copy of each list before the stages, 16 bytes a candidate, hands the scan its strided lists for 2.5 ms of 63 on the photograph and 0.3 of 4.0 on a page, exact. (Wrong, found in step 6 by the test that compared the two lists: Standard QR strides by 3·height/708 rows, 3 at least, and the scan by 4, so the lists are the same only on images 944 to 1,179 rows tall. What the two share exactly is each scanned row's hits.)
- The full sweeps, not exactly: Standard QR sweeps the rows between only where its stages give up, and averages those hits into the candidates it holds, so its list is neither the sweep's nor always there. Sharing them means Standard QR scanning both lists as the scan does and keeping its own candidates apart from the sweep's, a change to Standard QR's own candidate handling and so to its reads, outside this plan.
- The regional binarizations and the inversion, only by keeping two more images alive (refused before, 0.74 s of 12.4 over the set) or by interleaving the readings, each binarization read by Standard QR and then by the scan before the next is made, which puts Micro QR and rMQR reads of the global pass before Standard QR's regional reading, against the ruling that a triple that reads claims its finders first, and saves 16 %.
- The midpoint sweeps, 1.5 of 63 ms on the photograph, 0.24 of 4.0 on a page, run only when a global pass found no finder; what they read in the mixed sets is unmeasured.

Left as the decision: the exact share (the strided lists) is worth 4 to 7 % of the call; the rest trades reads or memory for at most 20 %. With the gate the read of every symbology stands at 1.3 times Standard QR's call on the twelve symbols and 1.34 on the 60-code photograph, where the floor without pass sharing is Standard QR's call plus the sweeps, about 1.2.

### Step 6, the strided rows shared (2026-10-30)

Done, not as step 5 described it. The test written first for the list copy compared Standard QR's strided list with the scan's on a sheet and found them different: Standard QR strides by 3·height/708 rows, 3 at least, the scan by a constant 4, so the two lists are the same only on images 944 to 1,179 rows tall, and the first plumbing had changed Standard QR's own stride. What the two reads share exactly is each scanned row's hits, which depend on neither the list they go to nor the stride that reached the row. So Standard QR's first round keeps the hits of each full-size reading's strided rows (`SharedRowHits`, rented by the read of every symbology: a quarter of a candidate list of hits a reading, 65,536 bytes in all, and the row ends of Standard QR's stride), and the scan's strided scan and sweep take them through `FinderPatternFinder.KeptRows`, merging a kept row's hits where they would scan it and scanning only the rows between. Both of the scan's lists stay bit for bit its own scan's, held by `FinderCandidatesOneRowPassParityTest` at given strides 3, 4, 5, 7 and 12 with full and small rooms, and `SharedRowHitsTest` on the mixed sheets: Standard QR keeps every reading at its own stride, a sweep from the kept rows is the full sweep bit for bit, no room or too little room keeps nothing, and the scan given the rows reads what it reads alone, with one reading left out too.

Rows spared, counted with the probe: on the 1,280 × 960 pages both stride by 4, so every strided row of the scan is spared and a quarter of its sweep's, in three of the four readings on a crisp page (its regional positive binarization moves no pixel, in either read) and all four on a textured one; BoofCV's 60-code photograph, 3,024 rows at Standard QR's stride of 12, spares a third of the scan's strided rows and 8 % of its sweep's; the 308-row nominal photograph at stride 3, a third and a third; the 2,016-row shadows photograph at stride 8, a half and 12 %.

Reads unchanged, image for image with corners, against the logs of steps 1 and 2: the 432 mixed sheets, BoofCV's 536 photographs, and all 127,963 one-symbol renders of step 2's run (the 100,800 of step 1's among them). Full test suite green on net8.0 and net10.0. Faults planted: 9 more in `tools/mutants/mixed-symbology-read.tsv`, 7 caught; the sweep that replays only its own rows and the scan that takes no rows change the time only, as a spared scan cannot be seen in a read.

Time, the probe pages and photographs (fastest of 25 on two pinned cores, the fastest of five runs of each build in turn on the pages and four on the photographs, ms): no-symbol 3.96 -> 3.76, qr-one 4.08 -> 3.85, qr-page-12 2.93 -> 2.88, mixed-12 9.24 -> 9.45, single-8 6.50 -> 6.53, mixed-12-tex 15.97 -> 15.96; lots/image001 53.3 -> 52.4, shadows 9.65 -> 9.31, nominal/image001, nominal/image002 and blurred within their noise at 5.4, 0.66 and 0.45. So 5 to 6 % off the pages whose time is the passes alone, within noise on the pages of twelve symbols, where the reads are the time, and 1 to 2 % off the photographs: less than step 5's 0.3 and 2.5 ms, since the strided scan it counted is spared whole only where the strides agree, and the sweeps, which it did not count, give up a quarter of their rows on the pages and 8 % on the photograph. The machine carried eight orphaned `cat /dev/zero` processes from 05:37 through every measurement of steps 5 and 6, so the builds compare under the same load, and the pinned cores held the pages' fastest runs within 3 % of each other.
