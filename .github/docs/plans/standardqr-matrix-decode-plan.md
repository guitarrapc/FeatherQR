# Reading a Structured Append set faster: the Standard QR matrix decode path

## Purpose

Reading a Structured Append set is `QRCodeDecoder.TryDecode` once per symbol, and a set is large symbols by construction: 14 to 16 symbols at version 39 or 40 in the `QRCodeStructuredAppendDecode` shapes, 150 to 178 us a symbol. The stage profile says where that goes, and it is not the header:

| Stage (us per symbol, version 39 and 40, level L) | byte-45k | numeric-100k | mixed-40k-opt | utf8-15k |
|---|---:|---:|---:|---:|
| End to end | 171.7 | 153.5 | 177.5 | 152.0 |
| `QRCodeData.GetCoreData` (bits to one byte a module) | 3.8 | 3.6 | 3.7 | 3.8 |
| **`QRMatrixDecoder.ExtractCodewords`** | **139.9** | **116.5** | **140.2** | **117.0** |
| `DeinterleaveCodewords` | 1.8 | 1.8 | 1.7 | 1.8 |
| `EccBinaryDecoder.TryCorrect`, every block, clean | 1.7 | 1.4 | 1.4 | 1.5 |
| **`QRBinaryDecoder.DecodeBitStream`** | **27.8** | **31.3** | **36.2** | **28.4** |
| Result string | 0.4 | 0.4 | 0.4 | 0.2 |

`ExtractCodewords` is about 78 % of a decode and `DecodeBitStream` about 19 %. Everything else together is under 5 %. The label-sized shape (version 10) has the same proportions at a tenth of the size: 11.4 of 14.8 us.

Two findings that change how the benchmark reads:

- The 1.26x to 1.29x "Structured Append set" over "Plain symbols" ratio is not the cost of the header. It sits entirely inside `ExtractCodewords` and comes from which mask pattern the encoder picked for each arm. With the mask forced on one version 40 symbol the walk costs 114 us at mask 0, 125 at mask 1, 138 to 140 at masks 2 to 4 and 147 to 154 at masks 5 to 7, because `GetMaskBit` runs per module and the costly predicates divide. The byte and mixed sets land mostly on masks 4 to 7; the numeric and UTF-8 shapes pick the same masks in both arms and read 1.00.
- Mask 0 still costs 3.8 ns a module. The walk itself is the cost (a blocked-bit test, a stream-end test, a data-dependent branch and a memory OR per module); the mask predicate is a 20 to 35 % adder on top.

None of this is Structured Append specific. The same two stages run for every Standard QR matrix decode and for the image decoder once it has sampled a grid. The plan carries the name of the benchmark that surfaced it; the change belongs to the Standard QR decoder and lands in `specs/standardqr-decoder.md`.

## Scope

| In | Out |
|---|---|
| `QRMatrixDecoder.ExtractCodewords`: a table-driven fast path, the current walk kept as the reference | `EccBinaryDecoder`'s correction path, its x64 and scalar tiers, and everything else the ECC decoder does. Phase 5 takes its ARM64 syndrome pass alone |
| `QRCodeData.GetCoreData`, routed through the encoder's vector bit expansion | A second, packed-bit extract kernel, unless phase 4 measures a reason for one. It measured none |
| The unmask step, moved out of the per-module loop | The image decoder's detection and sampling stages. They gain from the faster matrix decode without being touched |
| `BitReader.Reads` and the Byte payload path of `SegmentDecoders` (phase 3, after the extract change makes it the largest stage) | Micro QR and rMQR extraction. rMQR has its own bit-plane kernel; Micro QR symbols are at most 17 modules a side |
| `EccBinaryDecoder`'s ARM64 syndrome kernel, and `DeinterleaveCodewords` behind it (phase 5). The 2 % that put them out of this plan was an x64 figure; on ARM64 they are 40 % of a version 40 decode | The image decoder's own stages, and Micro QR and rMQR, which share the ECC decoder but not this plan's shapes |
| The doc comment of `QRCodeStructuredAppendDecode`, which states the Ratio column is the header's cost | Public API. Nothing is added or changed |
| `specs/standardqr-decoder.md`: Decisions, Lessons, and the zero-allocation paragraph | A bit-plane PEXT kernel for Standard QR unless phase 2 leaves a measured reason for one (see Approach) |

## What has to stay true

- The extracted codeword stream is byte-identical to the reference walk's for every version, every mask pattern and every module grid, including grids whose dark modules are any non-zero byte (the public span overloads promise "0 = light, non-zero = dark").
- Steady-state decode allocates nothing, and the fast path adds no memory that grows with the versions or masks seen. It reads the placement tables `ModulePlacer.GetLayout` already builds for the version; what it adds is one fixed table of about a kilobyte. The Release-only allocation test keeps passing unchanged.
- The reference walk stays in the assembly and stays independent of the placement tables. Today the encoder places through `PlacementLayout.Ops` and `Index` and the decoder walks the grid on its own, so every round trip cross-checks the tables against an independent walk for free. Once the decoder reads through the same tables that cross-check is gone, and a wrong table would round-trip cleanly while breaking interop. The parity test against the reference walk, and the third-party fixtures, are what replace it.
- The caller's module buffer is never written. The span overloads take `ReadOnlySpan<byte>`.
- netstandard2.0 runs the fast path. It is table loads and shifts, no intrinsics.

## Approach

The constraint that shaped this section: the fast path may not add resident memory per version, and should touch less memory per decode than it has to. Three forms were prototyped and parity-checked byte for byte against the reference walk on all five benchmark shapes, both arms, including grids whose dark bytes are non-zero values other than 1.

| Extract, us per symbol | Reference walk | Index gather | Run walk | Run walk, packed bits |
|---|---:|---:|---:|---:|
| Version 39 and 40 | 104 to 150 | 9.0 to 9.5 | 10.2 to 11.0 | 14.4 to 15.1, and no 3.6 to 3.8 us unpack before it |
| Version 10 | 12.4 to 13.9 | 0.9 | 1.1 | 1.6, and no 0.4 us unpack |
| New resident memory | | 29.6 KB per version 40 mask stream set, 441 KB worst case | 1,152 B, fixed, all versions | 1,152 B, fixed, all versions |
| Tables read per version 40 decode | 3.9 KB blocked mask | 59 KB index + 3.7 KB mask stream | 6.5 KB of ops + 206 index entries + 144 B | same as the run walk |
| Module data read per version 40 decode | 31 KB grid | 31 KB grid | 31 KB grid | 3.9 KB packed bits |

All three remove the mask dependence. Projected end to end at version 40 on x64: about 172 us to about 43 us, about 40 with the vector unpack of section 3.

### 1. Run walk over the encoder's ops (chosen)

`ModulePlacer.GetLayout(version)` already holds, beside the index table, the same walk segmented into runs: stretches of rows where both modules of a column pair are free (`PlacementLayout.Ops`). Version 40 is 326 ops, and only 206 of its 29,648 free modules fall outside a run (the column pairs that straddle an alignment pattern or the version information). The decoder already calls `GetLayout` for the blocked mask, so the ops are resident before the first extract and nothing new is built per version.

Four rows of a run are one output byte: four 16-bit loads a row apart, a SWAR step that turns non-zero bytes into bits, one multiply that packs them. No index table is streamed, no blocked-bit or stream-end test runs per module. The modules outside a run go one at a time through their slice of the index table.

The mask never reaches the per-module level either. Every mask predicate repeats every 12 rows and every 6 columns, so the eight mask bits under one output byte of a run depend only on the pattern, the walk direction, the column phase and the row phase: 8 x 2 x 6 x 12 = 1,152 bytes cover every version. That table is the only memory this adds, and it can be a static data blob rather than a heap array.

It measured 10 to 15 % behind the index gather on the x64 box. It is chosen anyway, and for one reason that does not need another machine to hold: it adds no memory that grows with the versions and masks seen, and the index gather does. The gap is about 1.5 us of a 43 us decode. The smaller table footprint is a fact (6.5 KB against 63 KB per decode); what it is worth in time is not known anywhere, and no ordering on ARM64 or in the browser is claimed from it. Apple silicon's L1 is larger than the measuring box's, so the footprint argument may well be worth nothing there. The per-op setup (one division, two loop exits per op) is the known slack and is phase 2 tuning, not a reason to change form.

Nothing in the kernel is x64 specific. It is 16-bit loads, shifts and one 64-bit multiply, so ARM64 runs the same code; the rows of a run are a row apart in memory, so a NEON form would have to gather first and is not planned.

### 2. Index gather (measured, not chosen)

Reading `PlacementLayout.Index` in order, eight modules a byte, with a cached mask stream per (version, mask). Fastest of the three here by a small margin and the simplest loop. Not chosen because the mask streams are new resident memory that grows with every version and mask seen, and because each decode streams 59 KB of index beside the 31 KB grid. Unmasking the grid with periodic tiles instead would remove the mask streams but needs a writable copy of the grid on the span path, and leaves the index footprint as it is.

### 3. The unpack in front of the walk

`TryDecode(QRCodeData)` unpacks the 3.9 KB of packed bits into a rented 31 KB grid through `QRCodeData.GetCoreData`, a SWAR loop at 3.7 to 3.9 us for version 40. Once the extract is 11 us that is a quarter of the two together. The encoder's `ModulePlacer.ExpandBits` writes the same layout (one 0/1 byte per bit, MSB first) with AVX2, SSSE3 and AdvSimd tiers, and measured 0.37 to 0.40 us for the same grid, output equal to `GetCoreData`'s. Routing `GetCoreData` through it (whole bytes through the kernel, the last few modules scalar) is part of phase 2.

### 4. Run walk straight off the packed bits (measured, not chosen)

The run walk can also read `QRCodeData`'s packed bits directly and skip the unpack and the 31 KB rental. It is slower per byte (bit position arithmetic and a shift per row), and what it is compared against is the byte walk plus the unpack in front of it:

| `QRCodeData` entry, version 40, us | Unpack | Extract | Together |
|---|---:|---:|---:|
| Today | 3.8 | 115 to 145 | 119 to 149 |
| Byte run walk | 3.8 | 10.9 | 14.7 |
| Byte run walk behind the vector unpack | 0.4 | 10.9 | 11.3 |
| Packed run walk | none | 14.9 to 15.1 | 14.9 to 15.1 |

A tie against today's unpack, 3.6 us behind once the unpack is the vector one. What is left in its favour is the rental, which is pooled and not an allocation. Not chosen on x64. It would apply to the `QRCodeData` entry alone (the span overloads and the image decoder keep the byte walk, everything after the extract is shared), so it does not need the decoder's input format to change; an earlier draft of this plan said it did, and that was wrong. The price is a second extract kernel to keep in parity. It stays in the ARM64 comparison of phase 4 and is dropped there unless it wins by more than that price.

### 5. The bit stream reader

`BitReader.Reads` calls a bounds-checked `Read` once per bit, about 1.2 ns a bit, and after phase 2 it is two thirds of what is left. Candidates, in the order they are expected to pay:

1. A 64-bit window: `Reads(n)` is a shift and a mask, refilled by whole bytes.
2. Byte payload in one pass: a copy when the payload is byte aligned, `(d[i] << s) | (d[i + 1] >> (8 - s))` otherwise. With a Structured Append header at version 10 and above the payload starts at bit 40, aligned.
3. UTF-8 in one pass on net8.0 and later (`Utf8.ToUtf16` validates and transcodes), in place of `IsValidUtf8`, `GetCharCount` and `GetChars`.

### 6. Reed-Solomon on ARM64 (phase 5, nothing prototyped yet)

Phase 4 left one stage bigger than this plan assumed. `EccBinaryDecoder.TryCorrect` over clean version 40 blocks costs 5.4 us on the M2 against 1.7 on the x64 box, 28 % of what a decode now is, and `DeinterleaveCodewords` 2.5 against 1.8; together 40 % of an ARM64 version 40 decode where the x64 profile that wrote them out of Scope read 2 %.

The syndrome pass is the only cost a clean block pays and the likely cause. It has three tiers (`EccBinaryDecoder.cs`): GFNI on net10.0+ x64, where one instruction is the GF(2^8) multiply; AdvSimd on ARM64, which builds the same product from table lookups and a reduction; and a scalar log-domain path elsewhere. ARM64 has no GFNI, but it has PMULL and PMULL2, which is the usual way to do GF(2^8) without it, and the current kernel does not use them. That is the first hypothesis and it is a hypothesis, not a finding: no variant has been written or measured.

Two things constrain the work before it starts. The stage figure above came from a harness that timed `TryCorrect` inside a loop with a restore copy subtracted out, so phase 5 re-measures it on its own before treating it as a baseline. And a destination span shorter than the kernel's store width throws on the GFNI tier but silently corrupts the caller's stack on ARM64, which is why `EccBinaryDecoderKernelParityTest` pins the store width from the kernel side; a new kernel is held to the same pin.

### Considered and deferred: a bit-plane PEXT kernel

rMQR extracts through column bit planes with one PEXT and one PDEP per column. Its columns are at most 15 data rows and fit a `ushort`; a Standard QR column is up to 177 rows, so the planes need 8-row bands and three 64-bit words a column. The paper estimate is 6 to 8 us against the 10 us the run walk already measures, for a kernel that needs a fast-PEXT gate, an ARM64 tier and a portable tier. It is reopened only if extraction is still a stage worth naming after phase 3.

## Risks

| Risk | Why it matters | Answer |
|---|---|---|
| Encoder and decoder share one table | Wrong `Ops` would round-trip cleanly and fail against other readers | Reference walk kept independent; parity over all 40 versions x 8 masks; third-party fixtures |
| Remainder bits and the stream end | Free modules exceed 8 x codewords by 0, 3, 4 or 7 bits depending on version, and the stream can end inside a run or between the two modules of a row. The error correction level does not move the end: data plus ECC codewords are one total per version | All 40 versions cover every remainder class; the cut itself is driven by handing the kernel a shorter output than the version's total, which the reference walk also honours. The prototypes passed 37,440 such cases (40 versions x 8 masks x 3 random grids x 39 output lengths) |
| The periodic mask table | One wrong entry corrupts one byte in 144 positions, easy to miss with a few fixtures | The table is checked against the predicate for every entry, and the parity grids are random so every phase is hit |
| Byte order | The pair load reads the right module from the high byte | Little-endian read helper, as `GetCoreData` already does for its big-endian branch |
| Unchecked reads | The walk drops bounds checks | Safe only behind the existing `modules.Length >= size * size` check and tables built by this assembly; stated at the kernel, and the safe form ships if it is within noise |
| ARM64 and the browser unmeasured | The x64 ratios say nothing about another JIT back end or another cache. The choice of form does not rest on them, but the quoted gains do | Phase 4 measures four arms in one run on ARM64; the Playground is the browser check. No ratio is quoted for a target before it has one |

## Phases

Each phase follows the test-first workflow, updates the decoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. Each reports the kernel ratio and the end-to-end delta from `QRCodeStructuredAppendDecode` and `QRCodeDecodeEndToEnd`, and reads the arms it does not touch as the noise of the day.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Measure | Stage profile, mask dependence, the three extract prototypes | Done, see Progress log |
| 2 | **P0** | Extract fast path | Parity tests first (red), then the run walk with the periodic mask table; per-op setup tuned; `GetCoreData` through `ExpandBits`; `ExtractCodewords` renamed to the reference and kept; benchmark doc comment corrected | Done, see Progress log. Byte-identical streams for all 40 versions x 8 masks on random grids, on grids with non-zero dark bytes other than 1, on the all-light and all-dark grids, and with the output shortened by 0 to 20 bytes and to arbitrary lengths so the stream ends inside a run, between the two modules of a row and inside a scatter range; `GetCoreData` byte-identical to its current output for every version; every mask table entry equal to the predicate; planted faults (a run one row short, a wrong row phase step, the pair order swapped, a dropped last byte) each fail a test; allocation test unchanged and no per-version allocation added; kernel ratio and end-to-end delta reported |
| 3 | P1 | Bit stream reader | The three reader candidates above, one hypothesis a variant | Done, see Progress log. Decoded text and status identical over the existing decoder tests and fixtures, including every malformed-stream status; truncated streams at every bit offset still return `InvalidBitstream` rather than reading past the end; ratio and delta reported |
| 4 | P1 | ARM64 measurement | Four arms in one run on the ARM64 machine: today, byte run walk, byte run walk behind the vector unpack, packed run walk; versions 1, 10 and 39 or 40; the `QRCodeData` entry and the span entry separately | Done, see Progress log. A number per arm, entry and size on an Apple M2, five processes of 15 rounds, with two arms of identical code carried as the run's noise canary; the packed run walk loses at every size and on both entries and is recorded as refuted |
| 5 | P1 | Reed-Solomon on ARM64 | Re-measure `EccBinaryDecoder.TryCorrect` on its own over clean blocks; read the AdvSimd syndrome kernel's disassembly and say where the time goes; then one hypothesis a variant, PMULL first. `DeinterleaveCodewords` only after the syndrome question is settled either way | A same-run number per variant against the current AdvSimd kernel, with the x64 tiers read on a machine that has them and no ARM64 ratio quoted for x64 or the reverse; every variant held to the scalar log-domain path over every ECC count Standard QR, Micro QR and rMQR use, and to the store-width pin, before it is timed; a winner ships behind the existing tier gate with `EccBinaryDecoderKernelParityTest` extended, a loser is recorded as refuted with its numbers. Either way the end-to-end delta comes from `QRCodeDecodeEndToEnd` and `QRCodeStructuredAppendDecode` on ARM64 |
| 6 | P2 | Fold | Decisions and measurements into `specs/standardqr-decoder.md`, and the ECC ones into `specs/qrcode-symbologies.md`, where the shared Reed-Solomon components are recorded; this plan deleted | The spec carries what was decided and why |

## Verification notes

- The parity reference is the current walk, untouched, called directly. It must not read `PlacementLayout.Ops` or `PlacementLayout.Index`.
- Grids for parity are random bytes, not encoder output: the extract stage does not care whether the grid is a valid symbol, and random grids exercise every free module with both values.
- The box these numbers came from was loaded: a BenchmarkDotNet ShortRun the same day reported errors larger than the means. The stage figures are the minimum of 15 rounds after warm-up. Phase deltas use the same method or a quiet machine.

## Progress log

### Phase 1, Measure (2026-09-20)

Done: stage timing of `TryDecode(QRCodeData)` over the five `QRCodeStructuredAppendDecode` shapes, both arms, through a harness that compiles the library sources and reaches the private stages by delegate; `ExtractCodewords` timed with each mask pattern forced; index-gather prototype with a cached mask stream, parity-checked against the reference walk on every shape.

Lessons:
- The benchmark's Ratio column measured mask-pattern luck, not the header. A baseline arm that re-encodes the content is free to pick a different mask, and the stage under it was mask sensitive. Two arms are comparable only when the stage costs under them do not depend on something the arms choose differently.
- The table that makes the decoder fast was already built, by the encoder, for the opposite direction. Look for the inverse of an existing table before designing a kernel.
- The per-module predicate looked like the cost and was a third of it. Forcing mask 0 separated the walk from the predicate in one run.

Benchmark delta: none shipped. Prototype extract 114 to 145 us to 9.1 to 9.7 us at version 39 and 40 (12x to 16x), 13 us to 0.9 us at version 10.

Addendum, same day: the index gather was set aside for its memory (a mask stream set per version seen, 59 KB of index streamed per decode). Two more prototypes, both parity-checked including non-zero dark bytes other than 1: the run walk over the encoder's `Ops` with a 1,152-byte periodic mask table (10.2 to 11.0 us at version 39 and 40, 1.1 us at version 10), and the same walk straight off `QRCodeData`'s packed bits (14.4 to 15.1 us with no unpack before it, a tie in total).

Lessons:
- The second table was also already there. `Ops` was built to make the encoder's stores cheap; read backwards it makes the decoder's loads cheap, and it is a ninth the size of the index.
- A mask stream per version is a cache of something periodic. Asking what the mask bits under one output byte depend on (pattern, direction, column phase, row phase) turned 441 KB into 1,152 bytes.
- The index gather won by 1.5 us and lost on memory. That is a sufficient reason by itself, and it is the only one that was measured.

Addendum 2, after a review from the ARM64 side (2026-09-20). Three corrections, each checked on x64 before it went in:
- "A smaller footprint will reverse the order on ARM64" was a prediction written as a reason. Apple silicon has more L1 than the box that produced the numbers. The choice now rests on memory alone, and phase 4 measures the rest.
- The packed walk was compared against the wrong arm. Its rival is the byte walk plus the unpack, and the unpack was the slow SWAR one: `ModulePlacer.ExpandBits` does the same job in 0.37 to 0.40 us against 3.7 to 3.9. Behind that unpack the byte walk totals 11.3 us and the packed walk 14.9 to 15.1. The claim that a packed walk needs every entry point packed was also wrong; it can serve the `QRCodeData` entry alone.
- "Level L and H move the stream end" was false: data plus ECC is one total per version. The cut is driven by a shortened output instead, and the prototypes were run that way over all 40 versions x 8 masks x 3 random grids x 39 lengths, 37,440 cases, both walks equal to the reference. That also closes the remainder-bit gap the first round left open (versions 10, 39 and 40 only).

Lessons:
- Before removing a stage, check whether it is slow or merely unoptimized. The unpack looked like a cost worth designing around and was one call away from a tenth of its time.
- An exit criterion is a claim about the format and gets checked like one. "Level H moves the end" read plausibly and tested nothing.
- In this harness a fast kernel's first shape read 2x to 8x slow, run after run (index gather 24 us, then 9.4 on every later shape). The cause was not pinned down; a kernel this short can finish its rounds before the JIT has promoted it. Every figure quoted here is from a later shape, and a harness for phase 2 warms by time, not by count.

### Phase 2, Extract fast path (2026-09-20)

Done: `QRMatrixDecoder.ExtractCodewords` is the run walk over `PlacementLayout.Ops` with the 1,152-byte periodic mask table (`QRMatrixDecoder.Extract.cs`); the per-module walk is `ExtractCodewordsReference`, off the decode path and independent of `Ops` and `Index`; `DecodeMatrix` no longer clears the codeword buffer, since the walk stores every byte; `QRCodeData.GetCoreData` goes through `ModulePlacer.ExpandBits`; the benchmark's doc comment says what its Ratio column can and cannot mean. Tests first: `QRMatrixDecoderExtractParityTest` failed to compile against the old code, then passed (all 40 versions x 8 masks x 7 grid shapes onto a dirty buffer, 27 shortened outputs a version with a sentinel behind them, outputs long enough to take the remainder bits, the mask table against the predicate at real coordinates over a whole version 40 grid in both directions, the two entry checks, and no allocation for a mask a warm version has not seen). `QRCodeDataCoreDataParityTest` passed before and after the unpack change. Full suite green on net8.0 and net10.0, Release and Debug. Spec and spec map updated.

Planted faults, each run against the parity class and each red: a run one row short, the row phase step 4 -> 3, the pair order swapped, the stream-end cut dropping its odd bit (38 of 129 tests, the shortened outputs), the tail row phase step, the up and down halves of the table swapped, the remainder flush removed (26 of 129, the long outputs). An eighth, the reciprocal without its `+ 1`, was red too; the reciprocal itself did not ship.

Lessons:
- "Per-op setup tuned" found nothing to tune. A reciprocal multiply in place of the one division per run was inside the noise against the prototype that divides (10.4 to 11.3 us against 10.2 to 10.9), so the division stayed. 326 divisions a symbol were never the slack; what is left per op is two loop exits, which a run table cannot remove.
- The safe form was worth measuring fairly. The first safe arm recomputed the mask phase with a modulo per byte and read 45 % slower; with the same phase stepping and only the accesses checked it was 28 % slower. Still a loss, so the unchecked walk shipped behind two entry checks, but the first number blamed bounds checks for a cost that was mostly mine.
- A dirty output buffer in the parity test is what let the `Clear` in `DecodeMatrix` go. The reference walk ORs into a cleared buffer; the run walk stores. Testing onto zeros would have passed either way and said nothing about whether the clear was still needed.
- Warming by time removed the slow first shape of the earlier harness (index gather 9.5 us on the first shape, where it had read 24). The promotion explanation held.

Benchmark delta (x64, the same public-API harness built against the committed tree and the changed one, three alternating rounds, minimum of 25 rounds each after 1.5 s of warm-up; the binaries were checked to hold different trees):

| us per symbol | Before, set / plain | After, set / plain |
|---|---:|---:|
| byte-45k-any, version 40 | 169.5 to 174.9 / 146.1 to 147.7 | 42.3 to 43.2 / 42.2 to 44.6 |
| numeric-100k-any, version 39 | 147.6 to 151.5 / 145.8 to 152.4 | 45.4 to 47.0 / 45.4 to 47.0 |
| mixed-40k-opt, version 39 | 173.5 to 191.5 / 142.9 to 152.7 | 43.3 to 45.3 / 43.6 to 44.9 |
| utf8-15k-any, version 40 | 145.1 to 146.9 / 142.7 to 153.2 | 42.6 to 44.2 / 42.9 to 44.2 |
| byte-4k-max10, version 10 | 14.4 to 15.7 / 14.7 to 15.2 | 4.3 to 4.5 / 4.3 to 4.5 |

Set over plain was 1.14 to 1.25 on the byte and mixed shapes and is 0.97 to 1.02 on every shape. Kernel: extract 104 to 150 us to 10.2 to 11.1 (10x to 14x), 12.4 to 13.9 to 1.1 at version 10; unpack 3.8 us to 0.4. What is left of a version 40 decode is about two thirds bit stream reader, which is phase 3.

BenchmarkDotNet, same day, against its own 2026-09-18 report: `QRCodeStructuredAppendDecode` byte-45k-any 2,981 us to 696 us a set (plain 2,377 to 689, ratio 1.26 to 1.01), mixed-40k-opt 2,757 to 614, numeric-100k-any 2,314 to 718, byte-4k-max10 258 to 66; Allocated unchanged on every row (88.23 KB, 78.46 KB, 195.7 KB, 29.63 KB, 8.2 KB). `QRCodeDecodeEndToEnd` version 40-L 43.2 us, the `Span` overloads still at zero bytes. The utf8-15k-any set row came back with an error three times its mean and is not quoted.

### Phase 3, Bit stream reader (2026-09-20)

Done: `BitReader.Reads` takes a field out of a 64-bit window (one big-endian load and two shifts; the last seven bytes of a stream are assembled bytewise) in place of a loop over single-bit reads; `BitReader.ReadBytes` reads a Byte segment in one call, a copy when the payload is byte aligned and an eight-bytes-a-step shift when it is not; on net8.0 and later `SegmentDecoders.DecodeBytePayload` transcodes once with `Utf8.ToUtf16`, which decides validity as it writes, and the validator plus count plus transcode path stays for the older targets. A read that does not fit now throws before it consumes anything. Tests first: `BitReaderParityTest` was red twice, at compile time for `ReadBytes`, then, with `ReadBytes` added as a bit loop so the rest could run against the old reader, on the seven cases that pin "throws and does not advance". `ByteSegmentCharsetParityTest` (every one- and two-byte string, the boundary bytes of every three- and four-byte form, each also behind a BOM and inside ASCII, long text with one bad byte, every start alignment, the destination one short, and the planners' mirror of the rule, against an oracle built on `Rune.DecodeFromUtf8`) and `QRBinaryDecoderTruncationTest` (every mode x three count-indicator bands x 40 lengths cut at every byte, and the count reported when a later group is out of range) passed on the old code before the change and after it. Full suite green on net8.0 and net10.0, Release and Debug.

Planted faults, one test class an invocation: the window's start shift masked with 3, the field shift 64 -> 63, the tail window's byte offset, the whole-load boundary 8 -> 7 (also red in the truncation class, which is where the last seven bytes are met from the decoder), the `Reads` underflow check removed, the `ReadBytes` carry shift, its word step's carry byte, its underflow check removed, always substitute, never substitute, the BOM not consumed: all red. One survived, removing the explicit `DestinationTooSmall` return after the transcode. It is an equivalent mutant, not a gap: the fall-through reaches the ISO-8859-1 size check, which fails for the same input because UTF-16 units never outnumber UTF-8 bytes. The return stays, so that reading the code does not take knowing that.

Lessons:
- The first run of the planted faults proved nothing and looked like a clean sweep of survivors: an OR filter over four classes matched no test, and "failed: 0" was printed twelve times. The baseline line printed "succeeded: 0" as well, which is what gave it away. A mutation run starts with a baseline that has to show the expected count.
- Splitting red into two steps was worth the five minutes. A compile error for a missing method says nothing about the tests around it; adding the method in the old style first showed exactly which assertions were new contract (seven) and which were characterization (the rest), against the old reader rather than by argument.
- Keeping the output index in a local, the textbook fix for a `ref int` that may alias the destination, measured nothing on a 6,900-digit payload and cost a mixed-mode plan 1.7 us, because the rewrite sliced the destination once per segment and a plan has many short ones. Both loops went back to what they were. Five groups per window load estimated 7 -> 4.8 us on Numeric, about 10 % of one pure-numeric version 40 decode, and was left alone: it is a second decode path and a peek API in a decoder three symbologies share.
- The isolated stage timing of the bit stream moved from 7 to 13 us between two builds of the harness while the end-to-end figure stayed at 21.6 to 22.2. An isolated stage is a hint; the end-to-end A/B is the number.
- With the mask dependence and the reader gone, the set-over-plain ratio finally measures something about the header, and it is alignment: a version 10+ set symbol's 40 header bits leave a Byte payload byte aligned (the copy), a plain symbol's 20 do not (the shift), so the set reads 0.94; behind a 12-bit ECI header it is the other way round, 1.05.

Benchmark delta (x64, same A/B method as phase 2, baseline the committed tree that already holds phase 2; the two binaries checked to differ in `ReadBytes` and to share `ExtractCodewordsReference`):

| us per symbol, set / plain | Before | After |
|---|---:|---:|
| byte-45k-any, version 40 | 42.3 to 42.7 / 43.2 to 44.5 | 15.1 to 15.3 / 16.1 to 16.2 |
| numeric-100k-any, version 39 | 46.4 to 47.4 / 46.5 to 46.6 | 21.8 to 22.2 / 21.8 to 22.2 |
| mixed-40k-opt, version 39 | 44.3 to 45.2 / 43.4 to 45.1 | 16.9 to 17.0 / 17.3 to 17.4 |
| utf8-15k-any, version 40 | 43.8 to 44.5 / 43.9 to 44.2 | 16.7 to 17.5 / 15.9 to 16.5 |
| byte-4k-max10, version 10 | 4.4 to 4.6 / 4.4 to 4.6 | 1.7 to 1.8 / 1.8 |

Bit stream stage at version 39 and 40: Byte 27.8 -> 0.9 with the window and the one-call read, -> 0.1 with the one-pass transcode; UTF-8 behind an ECI 28.4 -> 2.6 -> 1.8; mixed 36.2 -> 3.0 -> 2.0; Numeric 31.3 -> 7.0. From the start of this plan a version 40 Byte symbol has gone 170 -> 43 -> 15 us. Extraction is the largest stage again, about 11 of 15.

One round of the three had an outlier on the after side (mixed-40k-opt 24.5 / 23.4, byte-4k-max10 plain 2.3); the ranges above are the other two rounds, and the outliers were slower, not faster.

Small symbols, since Micro QR and rMQR read through the same reader (same A/B, string overloads, minimum of three rounds, ns): Standard QR version 1 Numeric 309 -> 248, version 1 Alphanumeric 406 -> 280, a version 6 URL 948 -> 546; Micro QR Numeric 329 -> 303, Alphanumeric 501 -> 423, Byte 672 -> 508; rMQR Numeric 241 -> 202, Alphanumeric 368 -> 235, Byte 920 -> 377. Nothing slower. The Micro QR rows were bimodal between rounds on both sides (Alphanumeric 500 or 950), so only their minimum means anything.

BenchmarkDotNet against its own phase 2 report: `QRCodeStructuredAppendDecode` byte-45k-any 696 -> 267 us a set, mixed-40k-opt 614 -> 248, numeric-100k-any 718 -> 343, utf8-15k-any 258 after (its phase 2 row was not usable), byte-4k-max10 66 -> 27; `QRCodeDecodeEndToEnd` version 40-L 43.2 -> 16.6 us, version 6 URL 1,628 -> 916 ns, version 1 Numeric 359 -> 292 ns. Allocated unchanged on every row of every class, the `Span` overloads at zero bytes in all three symbologies. Errors on this box were again a third of the mean or more, so these are for the allocation column and the order of magnitude; the A/B above is the measurement.

### Phase 4, ARM64 measurement (2026-09-21)

Done: the four arms in one process on an Apple M2 (macOS 26.6.2, .NET 10.0.9, arm64, 8 cores, workstation GC), through a harness that compiles the library sources and swaps nothing but the extract kernel and the unpack; versions 1, 10 and 40; the `QRCodeData` entry and the span entry separately; five processes of 15 rounds each, minimum per round, the arm order rotated every round and every arm warmed by time. The packed run walk does not exist in the tree, so it was written for this run against the same `Ops` and the same periodic mask table as the byte walk, and gated before anything was timed: its stream equals the reference walk's over 40 versions x 8 masks x 3 random grids x 5 output lengths (4,800 cases, the byte walk checked alongside it), its copy of the mask table equals the library's entry for entry, and every arm decodes every fixture to the same text through both entries. Every arm allocates zero bytes a decode on both entries. No source file changed.

End to end, us per decode, minimum of 15 rounds, range over the five processes:

| `QRCodeData` entry | today | byte run walk + SWAR unpack | byte run walk + vector unpack | packed run walk |
|---|---:|---:|---:|---:|
| version 1, Byte | 1.17 to 1.24 | 0.31 to 0.32 | 0.28 | 0.30 |
| version 10, Byte | 9.75 to 12.72 | 2.37 to 2.39 | 2.06 to 2.08 | 2.49 to 2.54 |
| version 40, Byte | 166.7 to 186.3 | 22.60 to 22.67 | 19.14 to 19.59 | 24.15 to 24.61 |
| version 40, Numeric | 122.3 to 155.5 | 28.40 to 28.62 | 25.33 to 25.55 | 29.90 to 30.51 |

| Span entry (no unpack) | today | run walk | run walk, second arm of the same code |
|---|---:|---:|---:|
| version 1, Byte | 1.12 to 1.18 | 0.26 | 0.26 to 0.27 |
| version 10, Byte | 9.27 to 12.42 | 2.00 to 2.02 | 1.96 to 2.02 |
| version 40, Byte | 160.0 to 181.0 | 19.03 to 19.12 | 18.84 to 19.07 |
| version 40, Numeric | 115.8 to 152.1 | 24.67 to 24.97 | 24.61 to 24.97 |

The last two columns are one arm entered twice, as the run's noise canary: their spread is 0.0 to 1.6 %, and 0.7 % or less on 18 of the 20 rows.

Stages on their own, us, same runs:

| | unpack SWAR | unpack vector | extract reference | extract byte run walk | extract packed run walk |
|---|---:|---:|---:|---:|---:|
| version 1 | 0.050 to 0.051 | 0.010 to 0.011 | 0.84 to 0.90 | 0.097 | 0.134 to 0.137 |
| version 10 | 0.368 to 0.373 | 0.055 to 0.056 | 7.34 to 9.19 | 1.03 to 1.06 | 1.55 to 1.59 |
| version 40 | 3.47 to 3.53 | 0.501 to 0.502 | 93.0 to 147.5 | 9.94 to 10.20 | 15.28 to 15.74 |

Verdict: **the packed run walk is refuted**. It loses on every size and on the only entry it could serve, by 1.07x to 1.10x at version 1, 1.21x to 1.23x at version 10 and 1.18x to 1.28x at version 40, which is 5.0 us of a 19.6 us decode. To break even its kernel would have to fall from 15.7 to 10.7 us, a 32 % cut, and there is no slack of that size in it: it pays a variable-shift field extract per row where the byte walk pays one 16-bit load per row and shares one SWAR step and one multiply across four. Nothing gates a second kernel, so the `QRCodeData` entry keeps the byte walk behind the vector unpack, and section 4 of Approach is settled.

ARM64 did not reorder the forms. Every kernel figure lands on the x64 prototype's: byte run walk 10.2 against 10.2 to 11.0, packed walk 15.7 against 14.4 to 15.1, SWAR unpack 3.5 against 3.7 to 3.9, vector unpack 0.50 against 0.37 to 0.40. The packed walk's penalty is if anything larger here, 1.47x at the kernel against x64's 1.32x.

Stage profile of the shipped path on this machine, us, version 40 level L (the columns sum to within 2 % of the end-to-end figure beside them):

| | vector unpack | extract | deinterleave | ECC, every block, clean | bit stream | end to end |
|---|---:|---:|---:|---:|---:|---:|
| version 40, Byte | 0.50 | 10.2 | 2.5 | 5.4 | 0.66 | 19.6 |
| version 40, Numeric | 0.50 | 10.1 | 2.5 | 5.4 | 6.7 | 25.4 |
| version 10, Byte | 0.06 | 1.05 | 0.25 | 0.49 | 0.08 | 2.07 |
| version 1, Byte | 0.01 | 0.10 | 0.03 | 0.03 | 0.03 | 0.28 |

Lessons:
- Two arms running identical code were the most useful column in the table. Their spread is what makes a 24 % gap a verdict instead of an opinion, and it cost one enum value. Without it the only defence of a 5 us difference would have been the number of rounds behind it.
- ARM64 changed nothing about the ordering and everything about what is left. Deinterleave and Reed-Solomon were 2 % of a decode when this plan started (1.8 and 1.7 us of 172) and were written out of Scope on that basis; on this machine they are 2.5 and 5.4 us of 19.6, 40 % together, with the ECC stage about 3x its x64 figure for the same version and level. The stage phase 1 dismissed as already vectorized is now the second largest, and it took a different back end to say so. It is phase 5.
- A capability-gated test is evidence only on a machine with the capability. Phase 2 routed `GetCoreData` through `ModulePlacer.ExpandBits`, whose `ExpandBitsAdvSimd` parity test is gated on `AdvSimd.Arm64.IsSupported` and had therefore been skipped on every x64 run of phases 2 and 3. It executed for the first time here, and passed, as did the rest of the suite (12,013 tests Release and 12,003 Debug, net10.0; net8.0 could not run, that runtime is not installed on this machine).
- The slowest arm was the least reproducible one. The reference walk's version 40 Numeric figure ranged 93 to 118 us across the five processes while its version 40 Byte figure held at 140 to 147, and no cause was pinned down. Nothing rests on it: every verdict here is between arms that reproduce inside 2 %.
- The deferred PEXT kernel's reopening condition has fired and its estimate has not survived the move. Extraction is 10.2 of 19.6 us, 52 % of a version 40 decode and the largest stage, which is the "still a stage worth naming after phase 3" test. But the 6 to 8 us behind it was a PEXT estimate, and ARM64 has no PEXT; on this machine the question is what a NEON form would cost, and the rows of a run are a row apart in memory, so it would have to gather. Phase 6 decides whether to reopen it or close it.

Not measured, and left that way on purpose: the index gather, which phase 1 set aside on memory rather than speed and which is not one of the four arms; and the browser check named in the Risks table, which needs the `wasm-tools` workload this machine does not have.
