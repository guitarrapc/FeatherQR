# A fast path for the Standard QR Alphanumeric writer

## Purpose

The Standard QR data stream writer has a bulk path for Byte mode but none for the two denser modes. `QRBinaryEncoder.WriteAlphanumericData` takes two characters a step, maps each through `CharacterSets.GetAlphanumericValue` with its two throwing checks, and appends one 11-bit field through `BitWriter.Write`. It runs at 2.1 ns a character, while the Byte writer moves eight characters a store. Every Alphanumeric symbol runs it: a single symbol, a Structured Append symbol, or a mixed-mode run, under `Single` and `Optimal` alike.

It surfaced in the Structured Append performance work (the writer's plans, taken together in [standardqr-encoder.md](../specs/standardqr-encoder.md#structured-append)). Once the writer's plan was cut, the Alphanumeric stream was the largest stage left in an alphanumeric set's writer: 63 us of 114 on 30,000 characters in seven symbols. The single-mode set pays the same 63. It is not Structured Append work, so it gets its own plan, branch off `main` and PR. The change is then measured against a baseline with nothing else in it, and can be reverted without touching a feature.

The rMQR writer already has a vector form (`RmQRBinaryEncoder.WriteAlphanumeric`, and its Numeric writer takes twelve digits an iteration the same way), and the Micro QR writer has a register-held form. Standard QR has the longest Alphanumeric payloads (4,296 characters at version 40-L) and is the only symbology still on the per-pair loop.

## Scope

| In | Out |
|---|---|
| `QRBinaryEncoder.WriteAlphanumericData`: the value lookup, the pairing, the appends | The Byte writer (`WriteLatin1Data`, `WriteUtf8Segment`), already bulk. Narrowing a plan's short Byte runs straight into the writer measured 3 to 9 % of the planned writer in the Structured Append rounds, below the end-to-end noise, and is not reopened here |
| `QRBinaryEncoder.WriteNumericData` (one 10-bit append per three digits), with the same shape and rMQR precedent. Measured in the same pass. It gets its own phase only if it registers | `BitWriter` as a type: its accumulator and 32-bit word stores stay. A kernel may add a wider append beside `Write` and `Write64`, but not rewrite the type |
| Runtime tiers: a vector kernel where the hardware has one, the best portable loop everywhere else, netstandard2.0 included | Sharing one kernel across the three symbologies. The field widths, tails and writers differ (rMQR writes through a `ref byte` with its own accumulator). Only the value mapping can be shared, and the winning variant decides whether that is worth a shared helper |
| An alphanumeric shape in `QRCodeStructuredAppendEncode`, and an alphanumeric payload long enough to measure in `QRCodeEncodeEndToEnd` (the current one is version 1) | Public API. Nothing is added or changed |
| The encoder spec's "Build the data codewords" and "Performance" sections | ARM64 measurement. A NEON tier is written only if the portable vector API gives one for free. Otherwise it is a follow-up on a machine that can measure it |

## What has to stay true

- The stream is byte-identical to the current writer's. A run inside a mixed-mode plan starts where the previous run ended, so the kernel starts at any bit alignment of the writer, not only at a byte boundary.
- A character outside the alphabet still throws `ArgumentException`. The analysis or the plan has already found every character in the alphabet, so the fast path may skip the per-character check. It still may not write a wrong field when a caller reaches the writer with a bad run: a vector step that sees such a character hands the rest of the run to the scalar writer, which throws as it does now.
- The code neither allocates nor uses `unsafe`. `Unsafe.Add` and `MemoryMarshal.GetReference` may be used where a measured win needs them. If the safe form is within noise, it ships.
- The scalar fallback is the best portable variant, not the old loop.

## Approach

Measure first. The writer's share of `Create` and `CreateStructuredAppend` on alphanumeric content is the ceiling, stated before any variant: a kernel several times faster gains no more than that share end to end.

Then each variant tests one hypothesis against a verbatim copy of the current writer, passes a correctness gate before any measurement, and runs beside a byte-identical canary that shows each scenario's noise. The disassembly is read for every verdict. The candidates follow in the order they are expected to pay off:

1. A value lookup with no throw on the path: one table load a character, validity folded into the table (a sentinel the pair arithmetic cannot produce) and checked once per step or per run.
2. Several 11-bit fields per append. Two characters make 11 bits, and an append costs a shift, a branch and sometimes a store. Packing five pairs into one 55-bit append pays that once for ten characters instead of five times.
3. Pairs packed several at a time: `first * 45 + second` over eight or sixteen characters in one multiply-add.
4. The rMQR vector form ported: `pshufb` offset classes for the value (the alphabet sits in four ASCII rows: the specials, the digits and colon, and two of letters), `pmaddwd` for the pairs, then the fields packed into the stream. rMQR's code does not carry over as is for packing 11-bit fields, and that step is measured on its own.
5. The same ladder for `WriteNumericData`: digits are `c - '0'`, groups of three are `pmaddubsw` then `pmaddwd`, 10-bit fields.

Scenarios use the sizes callers produce, not powers of two: a full version 40-L symbol (4,296 characters), a mid version, a label-sized symbol, a plan's short runs (8 to 40 characters, where per-call cost decides), odd lengths, and runs starting at every bit alignment. A winner must win per scenario. A kernel that loses on short runs gets a length cut-over.

## Phases

Each phase follows the test-first workflow, updates the encoder spec in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. No public API changes, so `PublicAPI.approved.txt` and the migration guide stay the same. The phases do change a hot path, so each reports the kernel ratio and the end-to-end delta from a worktree at the previous commit against the change, with the same benchmark file and three alternating rounds, and reads the arms it does not touch as the day's noise.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | P0 | Measure | The benchmark shapes (an alphanumeric Structured Append set, a long alphanumeric single symbol, a long numeric one). The writer's share of each, by stage timing inside the encode. The same for `WriteNumericData` | Each writer's ceiling is stated as a number. A writer under about 3 % of its encode is dropped from the plan, with that number recorded |
| 2 | P1 | Alphanumeric writer | The variant ladder above, the winner per tier ported with runtime dispatch, and parity tests | Streams byte-identical to the current writer over every length from 0 to a few hundred, both tail parities, all 45 characters and every starting bit alignment. A character outside the alphabet at every position of a vector step still throws. One parity test per tier, called directly and capability-guarded, against a scalar reference independent of the library's loop. Planted faults caught. Kernel ratio and end-to-end delta reported. Refuted variants recorded with the reason |
| 3 | P2 | Numeric writer | Only if phase 1 keeps it. Same ladder, same exit, 10-bit fields and the two tail forms (7 bits for two digits, 4 for one) | As phase 2 |
| 4 | P2 | Fold | Decisions and measurements folded into `specs/standardqr-encoder.md`. This plan deleted | The spec records what was decided and why. Nothing is only here |

Phase 1 is first because it can end the plan: if the writer is a few percent of an encode, a faster writer is not worth maintaining a kernel, and a number that shows this is the deliverable.

## Verification notes

- The test writes the parity reference from the definition (value table, `first * 45 + second`, 11 bits a pair, 6 for a last odd character, and every field MSB first), not by calling the scalar tier of the code under test.
- Tests set the alignment by writing 0 to 31 bits of a known pattern before the run, and compare the whole buffer, because the writer keeps up to 31 bits pending between appends.
- Mutating the multiplier (45), the field width, the odd tail, the validity sentinel or the cut-over length must each fail a test.
- End to end, the modules of every benchmark shape and of a corpus of random alphanumeric and mixed-mode encodes are compared between the previous commit and the change through the public API, and the comparison prints which tree each side loaded.

## Progress log

(empty)
