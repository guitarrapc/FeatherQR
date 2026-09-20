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
| `QRMatrixDecoder.ExtractCodewords`: a table-driven fast path, the current walk kept as the reference | Reed-Solomon, deinterleave, `GetCoreData`: 5 % together, already vectorized or trivially cheap |
| The unmask step, moved out of the per-module loop | The image decoder's detection and sampling stages. They gain from the faster matrix decode without being touched |
| `BitReader.Reads` and the Byte payload path of `SegmentDecoders` (phase 3, after the extract change makes it the largest stage) | Micro QR and rMQR extraction. rMQR has its own bit-plane kernel; Micro QR symbols are at most 17 modules a side |
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

All three remove the mask dependence. Projected end to end at version 40: about 172 us to about 43 us.

### 1. Run walk over the encoder's ops (chosen)

`ModulePlacer.GetLayout(version)` already holds, beside the index table, the same walk segmented into runs: stretches of rows where both modules of a column pair are free (`PlacementLayout.Ops`). Version 40 is 326 ops, and only 206 of its 29,648 free modules fall outside a run (the column pairs that straddle an alignment pattern or the version information). The decoder already calls `GetLayout` for the blocked mask, so the ops are resident before the first extract and nothing new is built per version.

Four rows of a run are one output byte: four 16-bit loads a row apart, a SWAR step that turns non-zero bytes into bits, one multiply that packs them. No index table is streamed, no blocked-bit or stream-end test runs per module. The modules outside a run go one at a time through their slice of the index table.

The mask never reaches the per-module level either. Every mask predicate repeats every 12 rows and every 6 columns, so the eight mask bits under one output byte of a run depend only on the pattern, the walk direction, the column phase and the row phase: 8 x 2 x 6 x 12 = 1,152 bytes cover every version. That table is the only memory this adds, and it can be a static data blob rather than a heap array.

It measured 10 to 15 % behind the index gather on a desktop part with a 1 MB L2, where the index gather's 94 KB per decode is free. It is chosen anyway: the gap is about 1.5 us of a 43 us decode, and it buys zero per-version memory and a table footprint a ninth the size, which is where a browser or a small core is expected to reverse the order. The per-op setup (one division, two loop exits per op) is the known slack and is phase 2 tuning, not a reason to change form.

### 2. Index gather (measured, not chosen)

Reading `PlacementLayout.Index` in order, eight modules a byte, with a cached mask stream per (version, mask). Fastest of the three here by a small margin and the simplest loop. Not chosen because the mask streams are new resident memory that grows with every version and mask seen, and because each decode streams 59 KB of index beside the 31 KB grid. Unmasking the grid with periodic tiles instead would remove the mask streams but needs a writable copy of the grid on the span path, and leaves the index footprint as it is.

### 3. Run walk straight off the packed bits (deferred)

`QRCodeData` stores the core matrix bit-packed, 3.9 KB at version 40, and `TryDecode(QRCodeData)` unpacks it into a rented 31 KB grid only so that the walk can read bytes. The run walk can read the packed bits directly: slower per byte, but the unpack and the rental disappear, the total is a tie (14.9 us against 10.9 + 3.8), and the whole working set of the extract fits in L1. It is deferred, not dropped: it only pays if the matrix decoder's input becomes packed bits for every entry point (the span overloads and the image decoder would pack first, one vector pass that also normalizes non-zero to 1), and that is a change to the decoder's internal seam rather than to one stage. Reopened after phase 3 with the image path measured.

### 4. The bit stream reader

`BitReader.Reads` calls a bounds-checked `Read` once per bit, about 1.2 ns a bit, and after phase 2 it is two thirds of what is left. Candidates, in the order they are expected to pay:

1. A 64-bit window: `Reads(n)` is a shift and a mask, refilled by whole bytes.
2. Byte payload in one pass: a copy when the payload is byte aligned, `(d[i] << s) | (d[i + 1] >> (8 - s))` otherwise. With a Structured Append header at version 10 and above the payload starts at bit 40, aligned.
3. UTF-8 in one pass on net8.0 and later (`Utf8.ToUtf16` validates and transcodes), in place of `IsValidUtf8`, `GetCharCount` and `GetChars`.

### Considered and deferred: a bit-plane PEXT kernel

rMQR extracts through column bit planes with one PEXT and one PDEP per column. Its columns are at most 15 data rows and fit a `ushort`; a Standard QR column is up to 177 rows, so the planes need 8-row bands and three 64-bit words a column. The paper estimate is 6 to 8 us against the 10 us the run walk already measures, for a kernel that needs a fast-PEXT gate, an ARM64 tier and a portable tier. It is reopened only if extraction is still a stage worth naming after phase 3.

## Risks

| Risk | Why it matters | Answer |
|---|---|---|
| Encoder and decoder share one table | Wrong `Ops` would round-trip cleanly and fail against other readers | Reference walk kept independent; parity over all 40 versions x 8 masks; third-party fixtures |
| Remainder bits and the stream end | Free modules exceed 8 x codewords by 0, 3, 4 or 7 bits depending on version, and the stream can end inside a run or between the two modules of a row. The prototype only ran versions 10, 39 and 40, all with 0 | The all-version parity test covers every remainder class; a level H symbol per version moves the stream end |
| The periodic mask table | One wrong entry corrupts one byte in 144 positions, easy to miss with a few fixtures | The table is checked against the predicate for every entry, and the parity grids are random so every phase is hit |
| Byte order | The pair load reads the right module from the high byte | Little-endian read helper, as `GetCoreData` already does for its big-endian branch |
| Unchecked reads | The walk drops bounds checks | Safe only behind the existing `modules.Length >= size * size` check and tables built by this assembly; stated at the kernel, and the safe form ships if it is within noise |
| Small-cache targets unmeasured | The choice of the run walk over the index gather rests on an expectation about ARM64 and the browser, not a number | Both prototypes are kept until one of those targets has been measured; the Playground is the browser check |

## Phases

Each phase follows the test-first workflow, updates the decoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. Each reports the kernel ratio and the end-to-end delta from `QRCodeStructuredAppendDecode` and `QRCodeDecodeEndToEnd`, and reads the arms it does not touch as the noise of the day.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Measure | Stage profile, mask dependence, the three extract prototypes | Done, see Progress log |
| 2 | **P0** | Extract fast path | Parity tests first (red), then the run walk with the periodic mask table; per-op setup tuned; `ExtractCodewords` renamed to the reference and kept; benchmark doc comment corrected | Byte-identical streams for all 40 versions x 8 masks on random grids, on grids with non-zero dark bytes other than 1, on the all-light and all-dark grids, and at level L and H of each version so the stream ends in different places; every mask table entry equal to the predicate; planted faults (a run one row short, a wrong row phase step, the pair order swapped, a dropped last byte) each fail a test; allocation test unchanged and no per-version allocation added; kernel ratio and end-to-end delta reported |
| 3 | P1 | Bit stream reader | The three reader candidates above, one hypothesis a variant | Decoded text and status identical over the existing decoder tests and fixtures, including every malformed-stream status; truncated streams at every bit offset still return `InvalidBitstream` rather than reading past the end; ratio and delta reported |
| 4 | P2 | Packed input, decide | The image path's stage profile; the packed run walk with a pack pass in front of the span and image entry points | A number for each entry point, and a go or a recorded no |
| 5 | P2 | Fold | Decisions and measurements into `specs/standardqr-decoder.md`; this plan deleted | The spec carries what was decided and why |

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
- Fastest on the measuring box is not the choice when the box is the friendliest target there is. The index gather won by 1.5 us on a part with a 1 MB L2, and a 1 MB L2 is what the smaller targets do not have.
