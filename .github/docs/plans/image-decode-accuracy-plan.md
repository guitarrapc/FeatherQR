# Image decode accuracy, measured against other readers

## Purpose

The clean-image work of the 2.0.0 plan (F13 to F18) was driven by this library's own renders of this library's own symbols: a sweep found a failing class, the class got a letter, the letter got a fix. That found real defects and it has no end, because the next sweep always finds another class, and it leaves one question open: whether the decoder got better or the tests got easier to pass.

This plan replaces that loop with an outside yardstick. The target is the set of images **another reader decodes and this library does not**, counted render for render on the same image, over symbols from every encoder the repository can run. The count is called the gap. It cannot be moved by changing a test, it says where the decoder is behind rather than where it is merely imperfect, and it has an end: zero, or a residual that is explained.

A sweep on `main` at a874f5e (2026-09-21) showed the yardstick is usable and where the gap is.

- **The encoder does not matter.** Seven Standard QR encoders (this library, ZXing.Net, QRCoder, QrCodeGenerator, CodeGlyphX, libzint, qrtool), three for Micro QR and rMQR. Same payload and same render parameters per case. Per render kind the read rate differs between encoders by at most 1.5 points (6 of 400), which is mask choice: 40 to 50 % of the QrCodeGenerator, libzint and qrtool matrices differ from this library's, 6 % of ZXing.Net's, 1.5 % of QRCoder's and CodeGlyphX's. Every library's own image writer reads 100 %. 135,000 decodes, no misread.
- **The gap is Standard QR.** zxing-cpp reads 1,870 Standard QR renders this library does not, 1,755 of them in five kinds. For Micro QR and rMQR it is the other way round by a wide margin (rMQR at any rotation: 5 to 16 % against 100 %), so those two have no outside yardstick and are tracked by their absolute rate.
- **ZXing.Net's reader is behind this library in every kind** and is not a target. It stays in the table as a second reference.

## Baseline

`main` at a874f5e. 400 Standard QR cases (ten per version, random level, numeric / alphanumeric / URL-like byte payload filled to 75-100 % of the version), 400 Micro QR (M1-M4), 640 rMQR (twenty per version); every case through every encoder and every kind. Density in px/module. "Gap" is renders zxing-cpp reads and this library does not; "reverse" is the opposite. qrtool's R17x43 symbols are excluded (see Verification notes).

### Standard QR (2,800 renders per kind, 2,000 for the last)

| Kind | FeatherQR | zxing-cpp | ZXing.Net | Gap | Reverse |
|---|---|---|---|---|---|
| Crisp nearest-neighbour, random offset, 1.0-1.25 | 2,800 | 625 | 137 | 0 | 2,175 |
| Crisp, 1.25-1.5 | 2,800 | 2,486 | 914 | 0 | 314 |
| Crisp, 1.5-2 | 2,800 | 2,757 | 2,248 | 0 | 43 |
| Crisp, 2-3 | 2,800 | 2,786 | 2,597 | 0 | 14 |
| Crisp, 3-6 | 2,800 | 2,800 | 2,758 | 0 | 0 |
| Anti-aliased path, 1.0-1.25 | 52 | 58 | 1 | 37 | 31 |
| **Anti-aliased path, 1.25-1.5** | 1,506 | 1,933 | 584 | **627** | 200 |
| **Anti-aliased path, 1.5-2** | 2,691 | 2,718 | 1,142 | **95** | 68 |
| Anti-aliased path, 2-3 | 2,800 | 2,800 | 2,044 | 0 | 0 |
| Anti-aliased path, 3-6 | 2,800 | 2,800 | 2,619 | 0 | 0 |
| **Bilinear upscale of a 1 px/module image, 1.5-2** | 2,013 | 2,581 | 625 | **668** | 100 |
| Bilinear upscale, 2-2.5 | 2,772 | 2,793 | 1,552 | 28 | 7 |
| Bilinear upscale, 2.5-3.5 | 2,791 | 2,786 | 1,879 | 9 | 14 |
| Downscale of an 8 px/module image, 1.5-2.5 | 2,779 | 2,793 | 1,724 | 21 | 7 |
| Downscale, 2.5-4 | 2,800 | 2,792 | 2,366 | 0 | 8 |
| Right-angle turn, crisp, 1.5-3 | 2,800 | 2,800 | 2,431 | 0 | 0 |
| Mirrored, crisp, 1.5-3 | 2,800 | 2,793 | 2,373 | 0 | 7 |
| **Supersampled, any rotation, 2-3** | 2,749 | 2,748 | 1,326 | **50** | 51 |
| Supersampled, any rotation, 3-6 | 2,794 | 2,800 | 2,347 | 6 | 0 |
| Supersampled, any rotation, keystone up to 6 %, 2-6 | 2,786 | 2,793 | 1,822 | 14 | 7 |
| **Supersampled, any rotation, keystone 6-20 %, 3-6** | 2,485 | 2,800 | 1,018 | **315** | 0 |
| JPEG quality 50-90, crisp, 2-4 | 2,800 | 2,800 | 2,570 | 0 | 0 |
| The encoder library's own image writer | 2,000 | 1,992 | 1,638 | 0 | 8 |

Where the five gap kinds fail. Below 2 px/module the status is `DataUncorrectable` almost throughout (bilinear 1.5-2: 772 of 787; anti-aliased 1.25-1.5: 1,283 of 1,294): the finders are found and the sampling is lost, which is what F18(a) left. Keystone 6-20 % splits by version: v10-20 reads 826 of 826, v1-9 595 of 651, v21-30 592 of 727, v31-40 472 of 596, and density barely matters (3-4: 802 of 924, 5-6: 829 of 945).

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

On photographs this library reads 54 % of what is there and zxing-cpp 89 %; ZXing.Net, behind in every synthetic kind, is ahead here (67 %). The gap is 28 % of the Standard QR reads, against 2.9 % in the synthetic sweep, and it is of another kind:

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
1. **Pixels below 2 px/module** (anti-aliased 1.25-2, bilinear 1.5-2.5: a gap of about 1,400, and the Micro QR / rMQR cells beside them). The threshold sits near 144 because edge greys fall in the dark class; the midpoint of the two class levels gave bilinear upscales +21 to +54 of 300 in the F18(a) prototype and nothing on anti-aliased edges. Reading a module as the mean over its footprint instead of one pixel. A threshold local to the symbol once the finders are known. A 2x resample of the symbol's own area, which the F18(a) round measured as the best reader and rejected only because it ran on the whole image of every failure.
2. **Keystone 6-20 %** (gap 315, zxing-cpp 100 %). The version split says the frame: v1 has no alignment pattern and v2-6 one, and from v21 up there are many and the decoder anchors on one. A mesh over several alignment patterns; corners from the finders' outer edges and the timing lines where there is no pattern to anchor on.
3. **Rotation at 2-3 px/module** (gap 50, reverse 51) and the small cells (bilinear 2-2.5, downscale 1.5-2.5). Diagnosed after 1, which probably moves them.
4. **Micro QR bilinear 2-2.5** (`NotDetected`): the single finder under blur. **rMQR keystone on wide symbols**: the perspective search's step over 99 and 139 modules.

## Phases

Each phase follows the test-first workflow, updates the decoder specs in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. Every phase reports the full baseline table again, not only the kind it aimed at.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | ~~**P0**~~ | ~~The sweep as a tool~~ **Done 2026-09-21** | The measurement moves from a throwaway probe into `tools/`, beside the interop fixtures: the encoders above, the test renderers, the three readers, fixed seeds, a gap table and a per-render CSV as output, and a mode that compares two trees render for render. QRCoder gets a pin with the other tool packages | The baseline above reproduces exactly from a clean checkout; two runs are identical |
| 2 | ~~**P0**~~ | ~~Real images~~ **Done 2026-09-21** | zxing-cpp's black-box sets for QR, Micro QR and rMQR (Apache-2.0), with their expected texts, read by all three readers; acquisition follows the fixtures spec's toolchain policy (committed if small, otherwise fetched by pinned commit and checksum). The gap table gets a corpus section | The corpus gap is a stated number per set. If it is larger than or different in kind from the synthetic gap, the phases below are reordered and the reason recorded |
| 3 | **P0** | The threshold under uneven lighting | Added by phase 2. Approach 0: where in the decode a second binarization runs, which one, and what it costs the images that hold no symbol | `qrcode-5` and `qrcode-1` at gap 0 or each residual explained; no read lost in the corpus or in any synthetic kind, render for render; candidate counts on the two-level noise set unchanged on the first pass; the no-symbol benchmarks and the failure paths stated before and after |
| 4 | **P1** | Pixels below 2 px/module | Diagnosis column first, then the candidates of Approach 1, Standard QR first and the shared pieces for all three | Standard QR gap under 1 % of renders in every kind from 1.5 px/module up, and no larger than the reverse at 1.25-1.5; Micro QR and rMQR at or above 99 % from 1.5 px/module up; losses listed; failure-path cost stated |
| 5 | **P1** | Standard QR under keystone | Diagnosis by version band, then Approach 2; `qrcode-3` and `qrcode-4` are this phase's and phase 4's real images, and each reports them | Gap under 1 % at keystone 6-20 %, the flat and up-to-6 % kinds identical or better render for render |
| 6 | P2 | The small Standard QR cells, and `qrcode-2` by image | Approach 3, only what phases 3 to 5 left; the `qrcode-2` gap triaged image by image into a class above, a new class, or out of the envelope (inverted, Model 1) with the reason | Each residual cell is under 0.5 % or carries a recorded cause |
| 7 | P2 | Micro QR and rMQR absolutes | Approach 4 | Micro QR bilinear 2-2.5 at or above 99 %; rMQR keystone 6-20 % at widths 99 and 139 measured against the true-transform column, and either repaired or recorded as the envelope with its number |
| 8 | P2 | Fold | Envelope tables, decisions, refuted candidates and lessons into the three decoder specs; the sweep and the corpus into the fixtures spec; this plan deleted | Nothing here is only here |

Phases 1 and 2 were first because they could redirect everything after them, and phase 2 did: the real-image gap is ten times the synthetic one in proportion, and the largest single cause in it, the global threshold under a lighting gradient, is a class no synthetic kind draws. It goes first as phase 3. The synthetic phases keep their order behind it, because the rest of the real-image gap (`qrcode-3`, `qrcode-4`) is the classes they already target, and from here on every phase reports the corpus beside the sweep.

## Verification notes

- The sweep pairs by construction: a case fixes the payload, the level and every render parameter, and each encoder's symbol goes through the same parameters. Seeds are arithmetic on the case and kind indices, never `GetHashCode`, which is randomized per process.
- libzint's creator (the pinned ZXingCpp package) dies with an access violation on some payloads, some of the time (Standard QR case 301, about one run in three, alone in its process). The first reading, that readers on other threads caused it, was wrong. The sweep runs the creator in a worker process, retries the case it died on, and drops a case only after eight deaths in a row, so two runs stay identical. A process killed that way can linger and hold its files: build to another output directory rather than wait for it.
- qrtool 0.13.2 emits a wrong R17x43 symbol at level M: about 88 modules differ from this library's and libzint's matrices, which agree byte for byte, and zxing-cpp does not read it either; level H is only the documented tail defect. R17x43 from qrtool is excluded from every table; the fixtures spec carries the note.
- A synthetic kind cannot stand in for a photograph's lighting, so the threshold phase's regression renders need a generator of their own: a symbol under a luminance ramp and under a soft shadow edge, with the ramp's depth as the parameter, checked against the corpus sets before it is trusted as their stand-in.
- A regression test added by a phase is a render of the failing class with its geometry asserted (drawn corners), plus the negative that the repair must not admit, each failing with the repair removed. The sweep is the measurement, not the test.
- Mutation checks per phase: every new bound and gate must fail a test when moved; a guard no sweep and no test can tell from its absence is deleted.

## Progress log

### Phase 1: the sweep is a tool, and the baseline reproduces (2026-09-21)

**Done.** `tools/QRImageDecodeSweep`, in the solution beside the interop fixtures: `sweep` (seven encoders for Standard QR, three for Micro QR and rMQR, 23 kinds drawn by the test suite's own renderers linked from `tests/FeatherQR.Tests/Shared`, three readers), `corpus`, `compare` (two result files image for image: gained, lost, every lost image by name) and `import-corpus`. Output is a per-image CSV and the gap table as Markdown. Every package it needs was already pinned centrally. The fixtures spec has the section ("Image decode sweep") and the two oracle defects as lessons.

**Exit met.** Against the throwaway probe's result files, which are this plan's baseline: 134,877 of 134,877 images identical in status, in all three readers' verdicts, and in density and size (63,600 Standard QR, 27,600 Micro QR, 43,677 rMQR). Three full runs wrote byte-identical files. The baseline was taken at a874f5e and the tool ran at 02168b0, so the decode-speed change between them (#415) moved no read.

**Lessons.** The libzint crash was misdiagnosed at first. It first appeared with readers running on other threads, a serial scan of the same cases passed, and "not thread-safe" fitted; a lock did not help, which should have ended that reading and instead got explained away as the readers' threads. Moved into a serial pass of its own, it crashed there, on the same case, one run in three. A fault that is probabilistic per input looks like a race under every test that varies the threading and nothing else; the test that separates them holds the threading still and repeats the input. And escapes do not survive this machine's shell tooling (a `\u0001` separator arrived in the source as a raw control character, and a quoted apostrophe ended a script): generated edits go through files, and a sort key is built from `ThenBy`, not from a joined string.

**Benchmark delta.** None; nothing under `src/` changed. The full sweep takes about two minutes on this machine and the corpus a few seconds.

### Phase 2: real images, and they reorder the plan (2026-09-21)

**Done.** zxing-cpp's black-box sets for the three symbologies (`qrcode-1` to `-6`, `microqrcode-1`, `rmqrcode-1`; 156 images, 1.1 MB, Apache-2.0) are committed under `tests/FeatherQR.Tests/Fixtures/RealImages/zxing-cpp-samples` with the LICENSE and a PROVENANCE.md naming commit 7e51e7b, the commit the Structured Append captures already came from. They are small enough to commit, so nothing is fetched at run time. No test reads them yet; the tool does, at the four right angles, as that repository's own runner does. The numbers are the "Real images" table under Baseline.

**Exit met, and its condition fired.** The corpus gap is stated per set: 153 of 548 Standard QR reads, 3 of 64 Micro QR, 0 of 12 rMQR. It is ten times the synthetic gap in proportion and its largest cause is of another kind, so the phases are reordered: the threshold under uneven lighting is the new phase 3, ahead of every synthetic cell. The evidence is a diagnostic, not a candidate: a local-mean binarization in front of the decoder reads 42 of the 43 gap reads of the two sets that fail as `NotDetected` under a lighting gradient, moves the two blurred, keystoned sets by a net of one read, and loses 14 reads elsewhere.

**Lessons.** "Misread" needed a second look before it meant anything: all 44 were one class, Byte-mode Shift_JIS with no ECI header, which this library decodes as the specified default on purpose and zxing-cpp reads by guessing. Counting them as gap would have put a character-set policy at the top of an image-accuracy plan, and counting them as reads would have hidden them, so they have a column of their own, together with the bit streams this library refuses. The synthetic sweep was built from the failure classes this project already knew, which is why it had no kind for the one that matters most on photographs; a corpus someone else collected is what finds the class nobody here thought to draw. And ZXing.Net, last in every synthetic kind, is ahead of this library on photographs: its hybrid binarizer is the difference, and that is one more pointer at phase 3.

**Benchmark delta.** None; nothing under `src/` changed.
