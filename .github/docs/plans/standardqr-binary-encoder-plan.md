# A fast path for the Standard QR Alphanumeric writer

## Purpose

The Standard QR data stream writer has a bulk path for Byte mode and none for the two denser modes. `QRBinaryEncoder.WriteAlphanumericData` takes two characters a step, each through `CharacterSets.GetAlphanumericValue` with its two throwing checks, and appends one 11-bit field through `BitWriter.Write`; it runs at 2.1 ns a character, where the Byte writer moves eight characters a store. Every Alphanumeric symbol runs it: a single symbol, a symbol of a Structured Append set, a run of a mixed-mode plan, `Single` and `Optimal` alike.

It surfaced in the Structured Append performance rounds ([structured-append-plan.md](structured-append-plan.md), round 14): once the writer's plan was cut, the Alphanumeric stream was the largest stage left of an alphanumeric set's writer, 63 us of 114 on 30,000 characters in seven symbols, and the single-mode set pays the same 63. It is not Structured Append work, so it has its own plan, its own branch off `main`, and its own PR: the change is measured against a baseline that holds nothing else, and can be reverted without touching a feature.

The rMQR writer already has the vector form (`RmQRBinaryEncoder.WriteAlphanumeric`: `pshufb` offset classes for the value, `pmaddwd` for the pairs; its Numeric writer takes twelve digits an iteration the same way) and the Micro QR writer a register-held one. Standard QR is the symbology with the longest Alphanumeric payloads (4,296 characters at version 40-L) and the only one still on the per-pair loop.

## Scope

| In | Out |
|---|---|
| `QRBinaryEncoder.WriteAlphanumericData`: the value lookup, the pairing, the appends | The Byte writer (`WriteLatin1Data`, `WriteUtf8Segment`): already bulk. Narrowing the short Byte runs of a plan straight into the writer was measured in the Structured Append rounds at 3 to 9 % of the planned writer and below the end-to-end noise; not reopened here |
| `QRBinaryEncoder.WriteNumericData` (one 10-bit append per three digits): same shape, same rMQR precedent. Measured in the same pass; it gets a phase of its own only if it registers | `BitWriter` as a type: its accumulator and its 32-bit word stores stay. A kernel may need a wider append beside `Write` and `Write64`; that is an addition to it, not a rewrite |
| Runtime tiers: a vector kernel where the hardware has one, the best portable loop everywhere else, netstandard2.0 included | Sharing one kernel across the three symbologies. The field widths, the tails and the writers differ (rMQR writes through a `ref byte` with its own accumulator); what can be shared is the value mapping, and whether that is worth a shared helper is decided from the winning variant, not before it |
| An alphanumeric shape in `QRCodeStructuredAppendEncode` and an alphanumeric payload long enough to measure in `QRCodeEncodeEndToEnd` (the one there is version 1) | Public API. Nothing is added or changed |
| The encoder spec's "Build the data codewords" and "Performance" sections | ARM64 measurement. A NEON tier is written only if the portable vector API gives one for free; otherwise it is a follow-up for the machine that can measure it |

## What has to stay true

- The stream is byte-identical to the current writer's. A run inside a mixed-mode plan starts wherever the run before it ended, so the kernel starts at any of the writer's bit alignments, not only at a byte boundary.
- A character outside the alphabet still throws `ArgumentException`. The analysis or the plan has already said every character is in the alphabet, so the fast path may skip the check per character, but it may not write a wrong field for a caller that reaches the writer with a bad run: a vector step that sees such a character hands the rest of the run to the scalar writer, which throws as it does now.
- No allocation, no `unsafe`. `Unsafe.Add` and `MemoryMarshal.GetReference` are available where a measured win needs them; if the safe form is within noise, the safe form ships.
- The scalar fallback is the best portable variant, not the old loop.

## Approach

Measure first. The writer's share of `Create` and of `CreateStructuredAppend` on alphanumeric content is the ceiling, stated before any variant: a kernel several times faster buys no more than that share end to end.

Then one hypothesis a variant against a verbatim copy of the current writer, a correctness gate before any measurement, a byte-identical canary for the noise of each scenario, and the disassembly read for every verdict. The candidates, in the order they are expected to pay:

1. The value lookup without a throw on the path: one table load a character, validity folded into the table (a sentinel the pair arithmetic cannot produce) and checked once per step or per run.
2. Several 11-bit fields per append. Two characters make 11 bits, and an append is a shift, a branch and sometimes a store; five pairs packed into one wider append (55 bits) pay that once for ten characters instead of five times.
3. Pairs packed several at a time: `first * 45 + second` over eight or sixteen characters in one multiply-add.
4. The rMQR vector form ported: `pshufb` offset classes for the value (the alphabet sits in four ASCII rows: the specials, the digits and colon, and two of letters), `pmaddwd` for the pairs, then the fields packed into the stream. The packing of 11-bit fields is where rMQR's code does not carry over as is, and it is the step to measure on its own.
5. The same ladder for `WriteNumericData`: digits are `c - '0'`, groups of three are `pmaddubsw` then `pmaddwd`, 10-bit fields.

Scenarios are the sizes the callers produce, not powers of two: a full version 40-L symbol (4,296 characters), a mid version, a label-sized symbol, the short runs of a plan (8 to 40 characters, where per-call cost decides), odd lengths, and runs that begin at every bit alignment. Winners have to win per scenario; a kernel that loses on short runs gets a length cut-over.

## Phases

Each phase follows the test-first workflow, updates the encoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. No public API moves, so `PublicAPI.approved.txt` and the migration guide do not change. This plan's phases do move a hot path; each reports the kernel ratio and the end-to-end delta, a worktree at the previous commit against the change with the same benchmark file, three rounds alternating, and reads the arms it does not touch as the noise of the day.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | **P0** | Measure | The benchmark shapes (an alphanumeric Structured Append set, a long alphanumeric single symbol, a long numeric one); the writer's share of each, by stage timing inside the encode; the same for `WriteNumericData` | The ceiling for each writer is a stated number. A writer under about 3 % of its encode is dropped from the plan with that number recorded |
| 2 | **P1** | Alphanumeric writer | The variant ladder above; the winner per tier ported with runtime dispatch; parity tests | Streams byte-identical to the current writer over every length from 0 to a few hundred, both parities of the tail, all 45 characters, every starting bit alignment, and a character outside the alphabet at every position of a vector step, which still throws; one parity test per tier, called directly and capability-guarded, against a scalar reference that is independent of the library's loop; planted faults caught; kernel ratio and end-to-end delta both reported; refuted variants recorded with the reason |
| 3 | P2 | Numeric writer | Only if phase 1 keeps it. Same ladder, same exit, 10-bit fields and the two tail forms (7 bits for two digits, 4 for one) | As phase 2 |
| 4 | P2 | Fold | The decisions and the measurements folded into `specs/standardqr-encoder.md`; this plan deleted | The spec carries what was decided and why; nothing here is only here |

Phase 1 is first because it can end the plan: if the writer is a few percent of an encode, a faster writer is not worth a kernel to maintain, and saying so with a number is the deliverable.

## Verification notes

- The parity reference is written in the test, from the definition (value table, `first * 45 + second`, 11 bits a pair, 6 for a last odd character; MSB first), not by calling the scalar tier of the code under test.
- Alignment is driven by writing 0 to 31 bits of a known pattern before the run and comparing the whole buffer, since the writer keeps up to 31 bits pending between appends.
- Mutation checks: the multiplier (45), the field width, the odd tail, the validity sentinel, the cut-over length. Each must fail a test.
- End to end, the modules of every benchmark shape and of a corpus of random alphanumeric and mixed-mode encodes are compared between the previous commit and the change through the public API; the comparison prints which tree each side loaded.

## Progress log

(empty)
