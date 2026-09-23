# Image decode accuracy, measured against other readers

## Purpose

The clean-image work of the 2.0.0 plan (F13 to F18) was driven by this library's own renders of this library's own symbols: a sweep found a failing class, the class got a letter, the letter got a fix. That found real defects and it has no end, because the next sweep always finds another class, and it leaves one question open: whether the decoder got better or the tests got easier to pass.

This plan replaces that loop with an outside yardstick. The target is the set of images **another reader decodes and this library does not**, counted render for render on the same image, over symbols from every encoder the repository can run. The count is called the gap. It cannot be moved by changing a test, it says where the decoder is behind rather than where it is merely imperfect, and it has an end: zero, or a residual that is explained.

A sweep on `main` (2026-09-21) showed the yardstick is usable and where the gap is.

- **The encoder does not matter.** Seven Standard QR encoders (this library, ZXing.Net, QRCoder, QrCodeGenerator, CodeGlyphX, libzint, qrtool), three for Micro QR and rMQR. Same payload and same render parameters per case. Per render kind the read rate differs between encoders by at most 2.5 points (10 of 400), which is mask choice: 40 to 50 % of the QrCodeGenerator, libzint and qrtool matrices differ from this library's, 6 % of ZXing.Net's, 1.5 % of QRCoder's and CodeGlyphX's. Every library's own image writer reads 100 %. 135,000 decodes, no misread.
- **The gap is Standard QR.** zxing-cpp reads 2,002 Standard QR renders this library does not, 1,907 of them in five kinds. For Micro QR and rMQR it is the other way round by a wide margin (rMQR at any rotation: 5 to 16 % against 100 %), so those two have no outside yardstick and are tracked by their absolute rate.
- **ZXing.Net's reader is behind this library in every kind** and is not a target. It stays in the table as a second reference.

## Baseline

The library as of `main` at 416a3b1, which reads as a874f5e did (the first baseline, taken there, reproduced image for image). 400 Standard QR cases (ten per version, random level, numeric / alphanumeric / URL-like byte payload, longer than the version below holds so that the version is the one asked for), 400 Micro QR (M1-M4, the version pinned), 640 rMQR (twenty per version, pinned); every case through every encoder and every kind. Density in px/module. "Gap" is renders zxing-cpp reads and this library does not; "reverse" is the opposite. qrtool's R17x43 symbols are excluded (see Verification notes). The Standard QR table was taken again after the PR review of phase 1 found the first payload rule putting 230 of 400 cases under their version (Progress log); Micro QR and rMQR did not move by a row.

### Standard QR (2,800 renders per kind, 2,000 for the last)

| Kind | FeatherQR | zxing-cpp | ZXing.Net | Gap | Reverse |
|---|---|---|---|---|---|
| Crisp nearest-neighbour, random offset, 1.0-1.25 | 2,800 | 654 | 130 | 0 | 2,146 |
| Crisp, 1.25-1.5 | 2,800 | 2,424 | 886 | 0 | 376 |
| Crisp, 1.5-2 | 2,800 | 2,765 | 2,213 | 0 | 35 |
| Crisp, 2-3 | 2,800 | 2,784 | 2,549 | 0 | 16 |
| Crisp, 3-6 | 2,800 | 2,800 | 2,737 | 0 | 0 |
| Anti-aliased path, 1.0-1.25 | 41 | 49 | 7 | 39 | 31 |
| **Anti-aliased path, 1.25-1.5** | 1,487 | 1,926 | 516 | **688** | 249 |
| **Anti-aliased path, 1.5-2** | 2,637 | 2,718 | 1,149 | **149** | 68 |
| Anti-aliased path, 2-3 | 2,800 | 2,799 | 1,949 | 0 | 1 |
| Anti-aliased path, 3-6 | 2,800 | 2,800 | 2,598 | 0 | 0 |
| **Bilinear upscale of a 1 px/module image, 1.5-2** | 2,022 | 2,583 | 641 | **644** | 83 |
| Bilinear upscale, 2-2.5 | 2,775 | 2,791 | 1,536 | 25 | 9 |
| Bilinear upscale, 2.5-3.5 | 2,797 | 2,783 | 1,922 | 3 | 17 |
| Downscale of an 8 px/module image, 1.5-2.5 | 2,789 | 2,793 | 1,678 | 11 | 7 |
| Downscale, 2.5-4 | 2,800 | 2,800 | 2,333 | 0 | 0 |
| Right-angle turn, crisp, 1.5-3 | 2,800 | 2,800 | 2,395 | 0 | 0 |
| Mirrored, crisp, 1.5-3 | 2,800 | 2,793 | 2,401 | 0 | 7 |
| **Supersampled, any rotation, 2-3** | 2,735 | 2,746 | 1,336 | **65** | 54 |
| Supersampled, any rotation, 3-6 | 2,794 | 2,800 | 2,243 | 6 | 0 |
| Supersampled, any rotation, keystone up to 6 %, 2-6 | 2,789 | 2,800 | 1,712 | 11 | 0 |
| **Supersampled, any rotation, keystone 6-20 %, 3-6** | 2,439 | 2,800 | 1,009 | **361** | 0 |
| JPEG quality 50-90, crisp, 2-4 | 2,800 | 2,793 | 2,520 | 0 | 7 |
| The encoder library's own image writer | 2,000 | 1,995 | 1,631 | 0 | 5 |

Where the five gap kinds fail. Below 2 px/module the status is `DataUncorrectable` almost throughout (bilinear 1.5-2: 771 of 778; anti-aliased 1.25-1.5: 1,308 of 1,313): the finders are found and the sampling is lost, which is what F18(a) left. Keystone 6-20 % splits by version: v10-20 reads 764 of 771, v1-9 574 of 629, v21-30 582 of 699, v31-40 519 of 701, and density barely matters (3-4: 811 of 924, 5-6: 812 of 945).

### Micro QR (1,200 per kind) and rMQR (1,899 per kind)

| Kind | Micro QR | zxing-cpp | Gap | rMQR | zxing-cpp | Gap |
|---|---|---|---|---|---|---|
| Crisp, 1.0-1.25 | 1,197 | 201 | 0 | 1,899 | 88 | 0 |
| Crisp, 1.25-6 (four bands) | 1,200 each | 573-1,197 | 0 | 1,893-1,899 | 309-1,674 | 0 |
| **Anti-aliased path, 1.0-1.25** | **249** | 45 | 3 | **334** | 15 | 0 |
| **Anti-aliased path, 1.25-1.5** | **1,048** | 200 | 3 | **1,489** | 51 | 3 |
| Anti-aliased path, 1.5-2 | 1,185 | 491 | 3 | 1,866 | 128 | 0 |
| Anti-aliased path, 2-6 (two bands) | 1,200 each | 1,133-1,169 | 0 | 1,899 each | 626-969 | 0 |
| **Bilinear upscale, 1.5-2** | **1,050** | 162 | 3 | **1,643** | 9 | 3 |
| **Bilinear upscale, 2-2.5** | **1,056** | 532 | **54** | 1,854 | 94 | 6 |
| Bilinear upscale, 2.5-3.5 | 1,191 | 718 | 3 | 1,899 | 35 | 0 |
| Downscale, 1.5-2.5 | 1,191 | 814 | 0 | 1,896 | 384 | 0 |
| Downscale, 2.5-4 | 1,200 | 1,170 | 0 | 1,899 | 774 | 0 |
| Right-angle turn / mirrored | 1,200 each | 1,012 / 983 | 0 | 1,899 each | 959 / 0 | 0 |
| Supersampled, any rotation, 2-6, keystone up to 6 % (three kinds) | 1,200 each | 789-1,164 | 0 | 1,899 each | 86-306 | 0 |
| **Supersampled, any rotation, keystone 6-20 %, 3-6** | 1,200 | 1,143 | 0 | **1,562** | 187 | 15 |
| JPEG / own image writer | 1,200 each | 1,164 / 1,179 | 0 | 1,899 each | 1,402 / 1,677 | 0 |

Micro QR's bilinear 2-2.5 cell is the only one where the finder is the loss (`NotDetected` 111 of 144). rMQR's keystone failures grow with width: 139 modules reads 95 of 240, 99 reads 182 of 240, 27 and 43 read everything, which matches the F18(e) measurement (the perspective search's resolution over a long symbol, not the row pitch).

### Real images (phase 2, `main` at 02168b0)

zxing-cpp's black-box sample sets at commit 7e51e7b, 156 images, each read upright and at the three other right angles (624 reads). "Content" is images this library got through and then decoded into another text or refused in the bit stream; it is not gap.

| Set | Images | FeatherQR | zxing-cpp | ZXing.Net | Gap | Reverse | Content |
|---|---|---|---|---|---|---|---|
| `qrcode-1` | 8 | 24/32 | 32 | 24 | 8 | 0 | 0 |
| `qrcode-2` | 56 | 104/224 | 201 | 100 | 56 | 3 | 52 |
| `qrcode-3` | 18 | 44/72 | 72 | 56 | 28 | 0 | 0 |
| `qrcode-4` | 24 | 36/96 | 60 | 64 | 24 | 0 | 0 |
| `qrcode-5` | 16 | 29/64 | 64 | 64 | 35 | 0 | 0 |
| `qrcode-6` | 15 | 58/60 | 60 | 60 | 2 | 0 | 0 |
| **Standard QR** | 137 | **295/548** | 489 | 368 | **153** | 3 | 52 |
| `microqrcode-1` | 16 | 60/64 | 59 | | 3 | 4 | 0 |
| `rmqrcode-1` | 3 | 12/12 | 12 | | 0 | 0 | 0 |

On photographs this library reads 54 % of what is there and zxing-cpp 89 %; ZXing.Net, behind in every synthetic kind, is ahead here (67 %). The gap is 28 % of the Standard QR reads, against 3.1 % in the synthetic sweep, and it is of another kind:

- **`qrcode-5` and `qrcode-1` are the threshold.** Every failure in `qrcode-5` is `NotDetected` (35 of 35) on photographs with a shading gradient across the symbol, where one global threshold puts a whole side of the symbol in one class. A diagnostic run that binarized each image against its local mean before the decoder saw it (not a candidate, a probe: window a tenth of the image, 8 % under the mean) read 63 of 64 there and 32 of 32 in `qrcode-1`, 42 of the 43 gap reads of the two sets. Over the whole corpus it moved Standard QR 295 → 335 (+54, −14) and Micro QR 60 → 63; the 14 it lost say it cannot simply replace the global threshold.
- **`qrcode-3` and `qrcode-4` are not the threshold** (the probe moved them 44 → 44 and 36 → 37). They are larger versions at 2.5 to 3 px/module, turned and keystoned, some already binarized by the camera, and blurred photographs: the same classes as the synthetic rotation, keystone and low-density kinds, failing as `DataUncorrectable` with the finders found.
- **`qrcode-2` is a mixed bag** and is triaged by image: an inverted symbol, a Model 1 symbol, round modules, tilted low-resolution captures, and the eleven Shift_JIS images counted as content.

Content, 52 reads of 13 images, all in `qrcode-2`: eleven Byte-mode symbols that carry Shift_JIS with no ECI header, which zxing-cpp reads by guessing the character set and this library decodes as ISO-8859-1, the specified default, once the bytes fail as UTF-8 (another text, by design); one GS1 symbol (`UnsupportedContent`); one symbol whose bit stream is refused and that no reader reads. None of it is image decoding, and it stays out of this plan.

## Scope

| In | Out |
|---|---|
| The gap to zxing-cpp on Standard QR, kind by kind, largest first | ZXing.Net's reader as a target: it is behind in every kind |
| Absolute read rates for Micro QR and rMQR where they are under 99 %: anti-aliased input under 1.5 px/module, bilinear upscales up to 2.5, rMQR keystone past 6 % on wide symbols | Beating zxing-cpp where neither reads (Standard QR anti-aliased at 1.0-1.25: 52 and 58 of 2,800). A module centre covers under half of any pixel there; that needs its own decision, as F18(c) did |
| The sweep as a repository tool, deterministic, so a gap number can be reproduced by anyone | Encoding conventions between libraries (ECI, UTF-8 defaults, Kanji). The payloads are ASCII so that a failure is an image failure |
| A corpus of real images with known text, read by all three readers | The speed of the image path. It has its own plan, and its rule that results stay bit-identical does not bind this one: a phase here that moves the threshold or the sampled grid says so and re-baselines |
| The decoder specs' envelope tables and the fixtures spec's oracle notes | F19 (the cross that becomes a finder candidate below 2.25 px/module). It stays in the 2.0.0 plan; no phase here may make it worse |
| | Public API. Nothing is added or changed |

## What has to stay true

- **No misread.** A decode that returns text returns the encoded text, over every sweep and the corpus. The baseline is zero.
- **Nothing is lost silently.** Every phase compares render for render against the commit before it and lists what stopped reading, per kind. "Reverse" does not shrink without a line saying why.
- **Failure paths are measured on failures.** Each phase reports two-level noise, blurred noise, a damaged symbol of each symbology, and another symbology's image, fastest of several interleaved runs, beside the success-path benchmarks. A read that costs every unreadable image a multiple is a trade to be stated, not a side effect.
- **The finder's candidate list on noise is pinned.** Candidate counts on the two-level noise set stay identical unless the phase is about the finder, and then the change is the phase's headline number.
- No allocation on the decode path, no `unsafe`, no public surface.

## Approach

Diagnose before repairing, with a column the earlier rounds lacked until F18(e): sample each failing render through the **true** transform, then matrix-decode. A render that reads that way failed in the frame (finder centres, dimension, perspective); one that does not failed in the pixels (threshold, where a module is sampled). The split decides which candidates are worth building, and it is cheap because the sweep knows every render's geometry.

Candidates, by the kind they are expected to move. None is chosen yet; each gets one hypothesis, one measurement, and a recorded verdict.

0. **The threshold under uneven lighting** (real images: `qrcode-5`, `qrcode-1`, part of `qrcode-2`). A second binarization tried when the global one finds no symbol, so that an evenly lit image pays nothing: a threshold per block from the block's own range with flat blocks taking their neighbours' (what ZXing's hybrid binarizer and zxing-cpp's local-average one do), or the global method run per tile and interpolated. What it has to be measured against is this plan's own history: every loosening of what reaches the finder has paid in candidates on noise, the finder's grey-level second look needs the dark and light levels the global histogram gives, and the image-path speed plan holds the binarizer bit-identical, so a local pass is an added path and not a change to the first one.
1. **Pixels below 2 px/module** (anti-aliased 1.25-2, bilinear 1.5-2.5: a gap of about 1,500, and the Micro QR / rMQR cells beside them). The threshold sits near 144 because edge greys fall in the dark class; the midpoint of the two class levels gave bilinear upscales +21 to +54 of 300 in the F18(a) prototype and nothing on anti-aliased edges. Reading a module as the mean over its footprint instead of one pixel. A threshold local to the symbol once the finders are known. A 2x resample of the symbol's own area, which the F18(a) round measured as the best reader and rejected only because it ran on the whole image of every failure.
2. **Keystone 6-20 %** (gap 361, zxing-cpp 100 %). The version split says the frame: v1 has no alignment pattern and v2-6 one, and from v21 up there are many and the decoder anchors on one. A mesh over several alignment patterns; corners from the finders' outer edges and the timing lines where there is no pattern to anchor on.
3. **Rotation at 2-3 px/module** (gap 65, reverse 54) and the small cells (bilinear 2-2.5, downscale 1.5-2.5). Diagnosed after 1, which probably moves them.
4. **Micro QR bilinear 2-2.5** (`NotDetected`): the single finder under blur. **rMQR keystone on wide symbols**: the perspective search's step over 99 and 139 modules.

## Phases

Each phase follows the test-first workflow, updates the decoder specs in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. Every phase reports the full baseline table again, not only the kind it aimed at.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | ~~**P0**~~ | ~~The sweep as a tool~~ **Done 2026-09-21** | The measurement moves from a throwaway probe into `tools/`, beside the interop fixtures: the encoders above, the test renderers, the three readers, fixed seeds, a gap table and a per-render CSV as output, and a mode that compares two trees render for render. QRCoder gets a pin with the other tool packages | The baseline above reproduces exactly from a clean checkout; two runs are identical |
| 2 | ~~**P0**~~ | ~~Real images~~ **Done 2026-09-21** | zxing-cpp's black-box sets for QR, Micro QR and rMQR (Apache-2.0), with their expected texts, read by all three readers; acquisition follows the fixtures spec's toolchain policy (committed if small, otherwise fetched by pinned commit and checksum). The gap table gets a corpus section | The corpus gap is a stated number per set. If it is larger than or different in kind from the synthetic gap, the phases below are reordered and the reason recorded |
| 3 | ~~**P0**~~ | ~~The threshold under uneven lighting~~ **Done 2026-09-23** | Added by phase 2. Approach 0: where in the decode a second binarization runs, which one, and what it costs the images that hold no symbol | `qrcode-5` and `qrcode-1` at gap 0 or each residual explained; no read lost in the corpus or in any synthetic kind, render for render; candidate counts on the two-level noise set unchanged on the first pass; the no-symbol benchmarks and the failure paths stated before and after |
| 4 | **P1** | Pixels below 2 px/module | Diagnosis column first, then the candidates of Approach 1, Standard QR first and the shared pieces for all three | Standard QR gap under 1 % of renders in every kind from 1.5 px/module up, and no larger than the reverse at 1.25-1.5; Micro QR and rMQR at or above 99 % from 1.5 px/module up; losses listed; failure-path cost stated |
| 5 | **P1** | Standard QR under keystone | Diagnosis by version band, then Approach 2; `qrcode-3` and `qrcode-4` are this phase's and phase 4's real images, and each reports them | Gap under 1 % at keystone 6-20 %, the flat and up-to-6 % kinds identical or better render for render |
| 6 | P2 | The small Standard QR cells, and `qrcode-2` by image | Approach 3, only what phases 3 to 5 left; the `qrcode-2` gap triaged image by image into a class above, a new class, or out of the envelope (inverted, Model 1) with the reason | Each residual cell is under 0.5 % or carries a recorded cause |
| 7 | P2 | Micro QR and rMQR absolutes | Approach 4 | Micro QR bilinear 2-2.5 at or above 99 %; rMQR keystone 6-20 % at widths 99 and 139 measured against the true-transform column, and either repaired or recorded as the envelope with its number |
| 8 | P2 | Fold | Envelope tables, decisions, refuted candidates and lessons into the three decoder specs; the sweep and the corpus into the fixtures spec; this plan deleted | Nothing here is only here |

Phases 1 and 2 were first because they could redirect everything after them, and phase 2 did: the real-image gap is nine times the synthetic one in proportion, and the largest single cause in it, the global threshold under a lighting gradient, is a class no synthetic kind draws. It goes first as phase 3. The synthetic phases keep their order behind it, because the rest of the real-image gap (`qrcode-3`, `qrcode-4`) is the classes they already target, and from here on every phase reports the corpus beside the sweep.

## Verification notes

- A row's key is how its image was made, and beside it the row carries a digest of the pixels. `compare` keeps a pair whose digests differ out of gained and lost: if an encoder's mask choice or a renderer changes between two trees, the pair is two images, and the read that moved is not the decoder's.
- The sweep pairs by construction: a case fixes the payload, the level and every render parameter, and each encoder's symbol goes through the same parameters. Seeds are arithmetic on the case and kind indices, never `GetHashCode`, which is randomized per process.
- libzint's creator (the pinned ZXingCpp package) dies with an access violation on some payloads, some of the time (Standard QR case 301, about one run in three, alone in its process). The first reading, that readers on other threads caused it, was wrong. The sweep runs the creator in a worker process, retries the case it died on, and gives a case up only after eight deaths in a row. A drop is rare, not impossible, so it is never silent: the case gets a row saying so and the run exits with a failure, because its images are no longer the set other runs have. A process killed that way can linger and hold its files: build to another output directory rather than wait for it.
- qrtool 0.13.2 emits a wrong R17x43 symbol at level M: about 88 modules differ from this library's and libzint's matrices, which agree byte for byte, and zxing-cpp does not read it either; level H is only the documented tail defect. R17x43 from qrtool is excluded from every table; the fixtures spec carries the note.
- A synthetic kind cannot stand in for a photograph's lighting, so the threshold phase's regression renders need a generator of their own: a symbol under a luminance ramp and under a soft shadow edge, with the ramp's depth as the parameter, checked against the corpus sets before it is trusted as their stand-in.
- A regression test added by a phase is a render of the failing class with its geometry asserted (drawn corners), plus the negative that the repair must not admit, each failing with the repair removed. The sweep is the measurement, not the test.
- Mutation checks per phase: every new bound and gate must fail a test when moved; a guard no sweep and no test can tell from its absence is deleted.

## Progress log

### Phase 1: the sweep is a tool, and the baseline reproduces (2026-09-21)

**Done.** `tools/QRImageDecodeSweep`, in the solution beside the interop fixtures: `sweep` (seven encoders for Standard QR, three for Micro QR and rMQR, 23 kinds drawn by the test suite's own renderers linked from `tests/FeatherQR.Tests/Shared`, three readers), `corpus`, `compare` (two result files image for image: gained, lost, every lost image by name) and `import-corpus`. Output is a per-image CSV and the gap table as Markdown. Every package it needs was already pinned centrally. The fixtures spec has the section ("Image decode sweep") and the two oracle defects as lessons.

**Exit met.** Against the throwaway probe's result files, which were this plan's first baseline: 134,877 of 134,877 images identical in status, in all three readers' verdicts, and in density and size (63,600 Standard QR, 27,600 Micro QR, 43,677 rMQR). Three full runs wrote byte-identical files. The baseline was taken at a874f5e and the tool ran at 02168b0, so the decode-speed change between them (#415) moved no read.

**Lessons.** The libzint crash was misdiagnosed at first. It first appeared with readers running on other threads, a serial scan of the same cases passed, and "not thread-safe" fitted; a lock did not help, which should have ended that reading and instead got explained away as the readers' threads. Moved into a serial pass of its own, it crashed there, on the same case, one run in three. A fault that is probabilistic per input looks like a race under every test that varies the threading and nothing else; the test that separates them holds the threading still and repeats the input. And escapes do not survive this machine's shell tooling (a `\u0001` separator arrived in the source as a raw control character, and a quoted apostrophe ended a script): generated edits go through files, and a sort key is built from `ThenBy`, not from a joined string.

**Benchmark delta.** None; nothing under `src/` changed. The full sweep takes about two minutes on this machine and the corpus a few seconds.

### Phase 1, review round: the version each case asks for is the version it gets (2026-09-21)

**Done.** Four review comments on the tool's PR, three taken and one answered in the docs.

- **Taken: Standard QR cases did not land on their version.** The payload was 75-100 % of the asked version's capacity, and from version 6 up the version below holds more than 75 % of it, so 230 of 400 cases encoded smaller: version 40 had two cases and 39 three, where the plan said ten each. The per-band figures were never wrong (they read each symbol's own version), but the coverage was thin exactly where F13-F18 had found a class. The payload is now longer than the version below holds and no longer than the asked one does; every version has its ten, and three cases see an encoder choose a neighbouring version through its own segmentation. The Standard QR baseline was taken again: gap 1,870 → 2,002, the same five kinds, the encoder spread at most 10 of 400. Micro QR and rMQR pin the version in every encoder and did not move by a row.
- **Taken: a comparison paired by render parameters alone.** If an encoder's mask choice or a renderer changed between two trees, `compare` would have paired two different images and charged the difference to the decoder. Each row now carries a digest of its pixels, and a pair whose digests differ is counted in a column of its own and kept out of gained and lost.
- **Taken in part: a case dropped after eight creator deaths changes the row set.** Eight attempts make that rare and cannot make it impossible. The retries stay, because an unknown payload may need them, but a drop is no longer silent: the case gets a row saying so, the table lists it, and the run exits with a failure.
- **Answered: the supersampled renderer's four-module quiet zone round Micro QR and rMQR.** True, and left: those kinds measure what the rotation tests draw, the renderer is the tests', and a wider quiet zone is within both specifications. The spec and the kind's comment now say so.

**Lessons.** "Ten per version" was a claim about the generator that nothing checked, and a claim about a sample is cheap to check: count it. The sweep now counts the cases whose symbol is not the version asked for and prints the count, and it prints zero. A key that names how an image was made is not the image; the two agree until something upstream changes, which is the moment a comparison is needed most.

**Benchmark delta.** None; nothing under `src/` changed. Two full runs after the change are byte-identical.

### Phase 2: real images, and they reorder the plan (2026-09-21)

**Done.** zxing-cpp's black-box sets for the three symbologies (`qrcode-1` to `-6`, `microqrcode-1`, `rmqrcode-1`; 156 images, 1.1 MB, Apache-2.0) are committed under `tests/FeatherQR.Tests/Fixtures/RealImages/zxing-cpp-samples` with the LICENSE and a PROVENANCE.md naming commit 7e51e7b, the commit the Structured Append captures already came from. They are small enough to commit, so nothing is fetched at run time. No test reads them yet; the tool does, at the four right angles, as that repository's own runner does. The numbers are the "Real images" table under Baseline.

**Exit met, and its condition fired.** The corpus gap is stated per set: 153 of 548 Standard QR reads, 3 of 64 Micro QR, 0 of 12 rMQR. It is nine times the synthetic gap in proportion and its largest cause is of another kind, so the phases are reordered: the threshold under uneven lighting is the new phase 3, ahead of every synthetic cell. The evidence is a diagnostic, not a candidate: a local-mean binarization in front of the decoder reads 42 of the 43 gap reads of the two sets that fail as `NotDetected` under a lighting gradient, moves the two blurred, keystoned sets by a net of one read, and loses 14 reads elsewhere.

**Lessons.** "Misread" needed a second look before it meant anything: all 44 were one class, Byte-mode Shift_JIS with no ECI header, which this library decodes as the specified default on purpose and zxing-cpp reads by guessing. Counting them as gap would have put a character-set policy at the top of an image-accuracy plan, and counting them as reads would have hidden them, so they have a column of their own, together with the bit streams this library refuses. The synthetic sweep was built from the failure classes this project already knew, which is why it had no kind for the one that matters most on photographs; a corpus someone else collected is what finds the class nobody here thought to draw. And ZXing.Net, last in every synthetic kind, is ahead of this library on photographs: its hybrid binarizer is the difference, and that is one more pointer at phase 3.

**Benchmark delta.** None; nothing under `src/` changed.


### Phase 3: a regional binarization after both polarities, and what it costs (2026-09-23)

**Done.** `LocalBinarizer` in `Internals/ImageDecoders`, shared by the three image decoders: each 8 × 8 block gets a black point from its own range (half its minimum when flat, or its top and left neighbours' when that is above it), and each pixel is read against the mean black point of the 5 × 5 blocks around its block. That is ZXing's hybrid scheme, which zxing-cpp also uses. Each decoder tries it once, after both global polarities have failed, into the buffer the inverted retry already rented, and reports the first attempt's status when it fails too. It is skipped when it puts every pixel in the class the global threshold did, which a two-level image always does, so two-level noise and a crisp symbol that failed on its data pay for the binarization and not for a third decode. The three passes (block statistics, the thresholded write, the count and the comparison with the global classes) have a 128-bit tier on net8.0 and later, held to the scalar form and to a reference written in the test. The decoder specs, the three spec maps, the shared-component list, the README and the public decoders' XML docs no longer call uneven lighting out of scope; hard-edged shadows, blur and strong perspective still are.

Tests: `UnevenLightingDecodeTest`, 71 per framework: a crisp symbol of each symbology under a ramp that takes 80 % of the light and under a shadow that takes 55 % across a three-module edge, eight directions each (40 of the 48 red before; the other 8 are ramps the global threshold already read), each case first asserting that the global threshold reads at least a tenth of the paper as ink; a symbol past correction under both lights does not decode; an unevenly lit image with no symbol is `NotDetected` in all three decoders; a short destination reports `DestinationTooSmall` through the regional pass; and both sides of the skip, two-level noise and a crisp damaged symbol agreeing with the global threshold, every lit case disagreeing. `LocalBinarizerParityTest`, 327: nine sizes (off a multiple of 8 on either axis, where the last block overlaps the one before), nine contents, four global thresholds, vector and scalar against the reference, and images under five blocks a side refused without a write. Mutations: a skip that always runs fails three tests, and the flat rule's comparison reversed fails 32. Full suite green on both frameworks.

**The renderer was checked against the corpus and against the other readers before it was trusted.** `UnevenLightingRenderer` (reflectance times light, crisp at 4 px/module) fails on `main` the way `qrcode-5` does, `NotDetected`. The first depths chosen were past what any reader reads: at a shadow depth of 0.75 FeatherQR, zxing-cpp and ZXing.Net each read 2 of 8 Standard QR turns, and at 0.85 none do, because across a three-module edge the modules on both sides share one neighbourhood. At the tested depths every reader reads every turn, and FeatherQR reads at or above zxing-cpp at every depth and symbology probed (Micro QR shadow 0.65: 5 of 8 against 1; rMQR 0.8: 5 of 8 against 0). `main` reads no shadow at any depth.

**Three binarizations were probed as a fallback before one was built** (real images, after the decoder had failed): a local mean over a tenth of the image, 8 % under it; the same over an eighth, 5 % under; and the hybrid scheme. All three read `qrcode-5` and `qrcode-1` almost completely; the hybrid scheme read the most over the corpus (Standard QR 359 of 548 against 353 and 356) at about half the cost, needs no integral image, and is what the two readers ahead on photographs use.

**Exit met.** Real images, render for render against `main`: Standard QR 295 → 357 of 548 (+62, none lost; `qrcode-1` 24 → 31 of 32, `qrcode-5` 29 → 63 of 64, `qrcode-4` 36 → 49, `qrcode-2` 104 → 111, `qrcode-3` 44 → 45), Micro QR 60 → 64 of 64, rMQR 12 of 12; gap 153 → 92. The two residuals in the target sets are one turn each (`qrcode-1/17.webp` at 180°, `NotDetected`, a blurred dense symbol; `qrcode-5/01.webp` at 180°, `DataUncorrectable`, keystoned with large modules): the binarization of both is clean by eye, both read at the other three turns and under the two mean-window binarizations probed, so they sit on a margin of the finder and the frame, not of the threshold, and belong to phases 5 and 6. Synthetic sweep: +400 Standard QR reads and none lost, the gap 2,002 → 1,691 (bilinear upscales at 1.5-2 px/module 2,022 → 2,176, anti-aliased edges at 1.25-1.5 1,487 → 1,572 and at 1.5-2 2,637 → 2,655, keystone 6-20 % 2,439 → 2,513), Micro QR +45 (bilinear at 2-2.5 1,056 → 1,098), rMQR +11 at 640 cases; no misread anywhere. The finder is untouched, and the regional pass does not run on a two-level image, so the two-level noise set's candidates are what they were.

**Lessons.** A second binarization is an added attempt, not a better first one: as a replacement the probe lost 14 corpus reads, because it has no grey levels for the finder's second look and moves edges the global threshold had right, and as an attempt after both polarities it lost none. The gains it made where nothing is unevenly lit (bilinear and anti-aliased renders below 2 px/module, keystone) say the same thing from the other side: a block-local threshold puts grey edge pixels differently, and a symbol one threshold misses by a few edges can read at the other. A skip that is exact beats a skip that is tuned: "every pixel where the global threshold put it" costs nothing to decide beyond the pass itself and cannot lose a read, where a fraction of changed pixels would have needed a bound and a sweep to defend it. And a stand-in renderer is a claim about the world that has to be checked against the world: the first shadow depths were chosen to make `main` fail and turned out to be past every reader, which the test would have pinned as a regression target nothing can meet.

**Benchmark delta.** Success paths unchanged (short job, in-process, `main` → branch): `v40-3px` 94.8 → 94.4 µs, `v40-3.4px` 111.8 → 105.5, `v40-4px-rot17` 305.8 → 288.7, `v40-4px-soft` 265.8 → 260.6, `v6-4px` 9.9 → 10.2, `M4_ImageDecode_Span` 5.1 → 4.9, `R7x43_ImageDecode_Span` 5.2 → 5.3, `R17x139_ImageDecode_Span` 19.3 → 20.7. No-symbol paths, where the image has grey in it and the pass runs: Standard QR `none-noise` (740 × 740 8-bit noise) 1.44 → 1.92 ms, `none-gradient` 324 → 540 µs, rMQR `NoSymbol_Gradient_1144x168` 105 → 197-204 µs; two-level noise, where it is skipped, `NoSymbol_Noise_1144x168` 6.87 → 6.73-7.06 ms. Failure paths, fastest of two interleaved rounds of five, at 512 × 512 unless named: skipped (two-level) Standard QR 1 px noise 0.90 → 0.99 ms, 2 px noise 0.21 → 0.32 ms, a crisp damaged v10 60 → 87 µs, Micro QR and rMQR noise within 5 % (their failure paths are 5-120 ms); run (grey) blurred noise Standard QR 0.35 → 0.61 ms and Micro QR and rMQR +6 to +7 %, a gradient 0.12 → 0.24 ms in all three, an anti-aliased damaged v10 63 → 131 µs and v25 215 → 440 µs, an anti-aliased damaged R13x99 20 → 48 ms, a Standard QR image read as Micro QR 40 → 72 ms and as rMQR 12 → 22 ms, and a textured ramp with no symbol 0.32 → 1.09 ms for Standard QR and 21 → 51 ms / 25 → 57 ms for Micro QR and rMQR, whose regional pass turns the texture into two-level specks and pays their finder's noise cost. The binarization alone is about 0.1 ms at 512 × 512 (it was 0.6 ms before its vector tier). A symbol that failed in the first two attempts now costs up to one attempt more, which is the trade this phase takes for the reads above; it was not narrowed further because every narrowing measured would have had to name a bound the corpus does not supply.
