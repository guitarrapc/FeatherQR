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

1. **Pixels below 2 px/module** (anti-aliased 1.25-2, bilinear 1.5-2.5: a gap of about 1,400, and the Micro QR / rMQR cells beside them). The threshold sits near 144 because edge greys fall in the dark class; the midpoint of the two class levels gave bilinear upscales +21 to +54 of 300 in the F18(a) prototype and nothing on anti-aliased edges. Reading a module as the mean over its footprint instead of one pixel. A threshold local to the symbol once the finders are known. A 2x resample of the symbol's own area, which the F18(a) round measured as the best reader and rejected only because it ran on the whole image of every failure.
2. **Keystone 6-20 %** (gap 315, zxing-cpp 100 %). The version split says the frame: v1 has no alignment pattern and v2-6 one, and from v21 up there are many and the decoder anchors on one. A mesh over several alignment patterns; corners from the finders' outer edges and the timing lines where there is no pattern to anchor on.
3. **Rotation at 2-3 px/module** (gap 50, reverse 51) and the small cells (bilinear 2-2.5, downscale 1.5-2.5). Diagnosed after 1, which probably moves them.
4. **Micro QR bilinear 2-2.5** (`NotDetected`): the single finder under blur. **rMQR keystone on wide symbols**: the perspective search's step over 99 and 139 modules.

## Phases

Each phase follows the test-first workflow, updates the decoder specs in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. Every phase reports the full baseline table again, not only the kind it aimed at.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | The sweep as a tool | The measurement moves from a throwaway probe into `tools/`, beside the interop fixtures: the encoders above, the test renderers, the three readers, fixed seeds, a gap table and a per-render CSV as output, and a mode that compares two trees render for render. QRCoder gets a pin with the other tool packages | The baseline above reproduces exactly from a clean checkout; two runs are identical |
| 2 | **P0** | Real images | zxing-cpp's black-box sets for QR, Micro QR and rMQR (Apache-2.0), with their expected texts, read by all three readers; acquisition follows the fixtures spec's toolchain policy (committed if small, otherwise fetched by pinned commit and checksum). The gap table gets a corpus section | The corpus gap is a stated number per set. If it is larger than or different in kind from the synthetic gap, the phases below are reordered and the reason recorded |
| 3 | **P1** | Pixels below 2 px/module | Diagnosis column first, then the candidates of Approach 1, Standard QR first and the shared pieces for all three | Standard QR gap under 1 % of renders in every kind from 1.5 px/module up, and no larger than the reverse at 1.25-1.5; Micro QR and rMQR at or above 99 % from 1.5 px/module up; losses listed; failure-path cost stated |
| 4 | **P1** | Standard QR under keystone | Diagnosis by version band, then Approach 2 | Gap under 1 % at keystone 6-20 %, the flat and up-to-6 % kinds identical or better render for render |
| 5 | P2 | The small Standard QR cells | Approach 3, only what phases 3 and 4 left | Each residual cell is under 0.5 % or carries a recorded cause |
| 6 | P2 | Micro QR and rMQR absolutes | Approach 4 | Micro QR bilinear 2-2.5 at or above 99 %; rMQR keystone 6-20 % at widths 99 and 139 measured against the true-transform column, and either repaired or recorded as the envelope with its number |
| 7 | P2 | Fold | Envelope tables, decisions, refuted candidates and lessons into the three decoder specs; the sweep and the corpus into the fixtures spec; this plan deleted | Nothing here is only here |

Phases 1 and 2 are first because they can redirect everything after them: a gap that cannot be reproduced is not a target, and a real-image gap of another kind (uneven lighting, blur, curvature) would outrank every synthetic cell above.

## Verification notes

- The sweep pairs by construction: a case fixes the payload, the level and every render parameter, and each encoder's symbol goes through the same parameters. Seeds are arithmetic on the case and kind indices, never `GetHashCode`, which is randomized per process.
- libzint's creator (the pinned ZXingCpp package) crashes with an access violation when zxing-cpp readers run on other threads at the same time, with or without a lock around the creator. Everything it creates is created in a serial pass before the parallel one. A process killed that way can linger and hold its output file; the tool must not depend on overwriting its own binaries between runs.
- qrtool 0.13.2 emits a wrong R17x43 symbol at level M: about 88 modules differ from this library's and libzint's matrices, which agree byte for byte, and zxing-cpp does not read it either; level H is only the documented tail defect. R17x43 from qrtool is excluded from every table, and the fixtures spec gets the note in phase 1.
- A regression test added by a phase is a render of the failing class with its geometry asserted (drawn corners), plus the negative that the repair must not admit, each failing with the repair removed. The sweep is the measurement, not the test.
- Mutation checks per phase: every new bound and gate must fail a test when moved; a guard no sweep and no test can tell from its absence is deleted.

## Progress log

Nothing yet.
