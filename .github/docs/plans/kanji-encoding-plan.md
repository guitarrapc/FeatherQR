# Kanji mode encoding for Standard QR, Micro QR and rMQR

## Purpose

Phase 6 of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md). All three decoders read Kanji mode and no generator writes it ([qrcode-symbologies.md](../specs/qrcode-symbologies.md), "Why Kanji mode is read but never written"), so Japanese text goes out as UTF-8 in Byte mode: 24 bits for a kana or a kanji where Kanji mode spends 13, plus a 12-bit ECI header. A Standard QR 40-L symbol holds 1,817 characters in Kanji mode and 984 as UTF-8; Micro QR M4-L holds 9 against 5, and M3-L 6 against 3. Micro QR has a second reason: it has no ECI, so its UTF-8 Byte output reads as Japanese only in a reader that guesses UTF-8, while Kanji mode is the standard's own way to carry the text.

This plan fixes WHAT ships, in WHICH order, and WHY each rule is what it is. HOW each piece is verified follows the mandatory test-first workflow. The code-level inventory it was written from (insertion points, the premises that stop holding, the tests and statements that move) is in [references/kanji-encoding-inventory.md](references/kanji-encoding-inventory.md). When the plan completes, its durable content graduates into [qrcode-symbologies.md](../specs/qrcode-symbologies.md), the three encoder records and spec maps and [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md); the 2.0.0 plan's Phase 6 row gets its Progress log entry, and this file and its reference are deleted.

## Scope

| In | Out |
|---|---|
| Kanji segments written by all three generators, in single mode and under `Optimal` segmentation | Kanji beside an ECI header (D6). A text with a character that is neither ASCII nor encodable in Kanji mode stays entirely UTF-8 in the first cut; phase 6.7 extends this only if the oracle sweep allows it |
| Structured Append sets (Standard QR) | Shift_JIS written as Byte mode or under ECI 20. The encoder still writes only Latin-1 and UTF-8 bytes, so the reasoning of D7 in the parent plan stands; the ECI 20 decode scope row is untouched |
| A reverse table generated from the sweep that produced `ShiftJisKanjiTable` | CP932's characters: NEC row 13, the IBM extensions, halfwidth katakana, and the CP932 readings of the seven divergent cells. They have no cell to write, and reading them is already out of scope (`UnmappedCharacter`) |
| Sizing (`TryGetRequiredBufferSize`), refusal messages, the capacity documentation | A public option to turn Kanji off. `EciMode.Utf8` already does it on Standard QR and rMQR. Micro QR has no charset option, and a switch for it waits for a concrete request (the public API growth rule) |
| Oracle cross-checks and committed fixtures covering every Kanji count-indicator width of every symbology | Changing the `Segmentation` default. Japanese text with ASCII in it (URLs, order numbers) reaches Kanji only under `Optimal`; making `Optimal` the default is its own decision |
| The segmenter's lane kernels, if measurement says they are needed (6.8) | The 128-bit tiers for the kernels this plan changes (parent plan, Phase 6b). They wait for this plan; see "Relation to other work" |

## When a text is written in Kanji mode

A text is **eligible** when both of these hold:

1. The library chose the charset itself, and it chose UTF-8. That means `EciMode.Default` (always the case on Micro QR), a character above U+00FF in the text, and on Standard QR `Utf8Bom` off.
2. Every character is either ASCII or has an **encoder cell**: a JIS X 0208 cell whose reading is not one of the seven that CP932 reads differently (K3).

Every text that is not eligible is written exactly as today, bit for bit.

| Text | `Single` | `Optimal` |
|---|---|---|
| ASCII or Latin-1 | As today | As today |
| `EciMode.Iso8859_1` or `EciMode.Utf8` set, or `Utf8Bom` on | As today: a charset the caller chose is honoured | As today |
| Eligible, and every character has a cell (「日本語」, 「脂至肢」) | One Kanji segment, no ECI | The same stream: one Kanji run is the program's optimum |
| Eligible, with ASCII in it (「QRコード」, 「日本7777」) | Today's UTF-8 Byte stream, because one segment cannot hold both | Kanji runs beside Numeric, Alphanumeric and Byte runs of ASCII, with no ECI. Taken when it lowers the version, as any plan is today |
| Has a non-ASCII character with no cell (é, emoji, ～ U+FF5E, ① U+2460, ｶ U+FF76, 〜 U+301C) | As today | As today, until 6.7 |
| Micro QR with the version range limited to M1-M2 | As today (Kanji exists from M3) | As today |

**Why "only where UTF-8 would have been written".** ASCII and Latin-1 output never moves, so the change reaches exactly the texts that cost the most today. Every non-ASCII character with a cell costs 16 or 24 bits in UTF-8 against 13 in Kanji mode, so wherever the rule applies, Kanji is the cheaper encoding character for character. The one cell cheaper in Byte mode, U+005C, is ASCII and one of the seven, so it is never written as Kanji. A single-mode Kanji stream also drops the ECI header.

**Why no ECI.** A text whose Byte runs carry only ASCII needs no ECI header at all. That avoids D6's open question (does every reader apply JIS X 0208 to a Kanji segment that follows ECI 26?). This library's decoder does, but no test covers it and no other reader's behaviour has been recorded. It also keeps Structured Append parity well defined: the bytes an eligible text carries are its Shift_JIS encoding whatever the plan, so the parity does not depend on how the text is segmented (K6).

**The cost this rule accepts.** Japanese text typed on Windows often carries CP932 readings (～ U+FF5E, － U+FF0D, ￠ ￡ ￢, ∥) or NEC characters (①, ㈱), and Japanese text from other sources often carries halfwidth katakana. Any one of them keeps the whole text in UTF-8. Phase 6.7 is where that limit is lifted, if it can be.

## Structured Append

| Rule | Reason |
|---|---|
| A set is eligible when the whole text is. An eligible set carries no ECI header in any symbol | One charset per set, as today ([structured-append-plan.md](structured-append-plan.md)) |
| The parity of an eligible set is the XOR of the whole text's Shift_JIS bytes, ASCII characters taken as their single byte. It is computed once, before splitting | Those bytes do not depend on the plan, so the "computed once" rule survives. For the corpus sentence 「こんにちは世界、QRコードの分割テストです。」×3 this gives 176, the value CodeGlyphX writes in its Kanji-mode set; QrCodeGenerator's UTF-8 set of the same text carries 6 |
| Under `Single` segmentation, an eligible set is written in Kanji mode only when every character has a cell. Otherwise the set is today's UTF-8 set | Every chunk must be one segment, and a chunk holding both kana and ASCII cannot be one Kanji segment |
| Under `Optimal`, the Kanji set is compared to today's UTF-8 set the way `Create` compares a plan with the single stream. It is taken only when it needs fewer symbols, or the same number at a lower version | `Optimal` must never give a larger set than today's. The balanced-split rules apply unchanged inside either set |
| Inside a Kanji set, a chunk has no UTF-8 fallback; its stream is its Kanji plan | The set carries one charset |
| A text that fits in one symbol still returns `Create`'s symbol, byte for byte | As today |
| The cost model learns Kanji. A text's single mode can now be Kanji; the closed-form chunk end gains a Kanji prefix; the lower bound prices a character with a cell at 13 bits | Two premises stop holding in the eligible path: that a text's single mode changes at most twice along it (Numeric, Alphanumeric, Byte), and that a character outside the alphanumeric alphabet extends only a Byte run |

This closes S8 of the Structured Append plan: the encoder-side Japanese parity cases are added in 6.5.

## Decisions

| # | Decision | Outcome |
|---|---|---|
| K1 | When is Kanji considered? | **Recommended:** the eligibility rule above. It refines the parent plan's D5 escape hatch: an explicit `EciMode`, or `Utf8Bom`, keeps today's stream |
| K2 | Does single mode pick Kanji automatically? (D5) | **Yes**, when every character has a cell, with a migration note. This is the parent plan's recommendation, and 6.3 confirms it |
| K3 | The seven cells CP932 reads differently (0x815F, 0x8160, 0x8161, 0x817C, 0x8191, 0x8192, 0x81CA) | **Never written**; neither of their readings has an encoder cell. This library reads 0x8160 as U+301C, and a CP932 reader such as ZXing.Net reads it as U+FF5E. Writing either reading at that cell would make a symbol that two readers decode differently, and writing U+FF5E would also break this library's own round trip. The parent plan's claim that "falls through to Byte" makes the divergence a non-issue for encoding held only for the CP932 readings, which have no reverse entry; the JIS X 0208 readings would have had one |
| K4 | Kanji segments beside ECI-tagged Byte segments (D6) | **Not in the first cut.** 6.6 measures the readers, and 6.7 decides from what they show |
| K5 | Micro QR | **The same rule, with no option.** Kanji is the more interoperable form there, because Micro QR has no ECI. It is available from M3 |
| K6 | Structured Append parity with Kanji | **The XOR of the whole text's Shift_JIS bytes**, per the table above |
| K7 | Non-Japanese scripts in JIS X 0208 (Greek, Cyrillic, box drawing, Latin-1 symbols) | **Recommended: eligible.** The rule is about what can be represented, not about script, so the single word 「Привет」 becomes one Kanji segment (13 bits a letter against 16). The 6.6 sweep over every encoder cell is the check. A row that any pinned reader misreads loses its cells |
| K8 | Reverse table | **Generated** from `kanji-sweep.tsv` by the generator that writes `ShiftJisKanjiTable`. The generator refuses unless the reverse table is the exact inverse of the forward one over the 6,872 encoder cells. It is RVA data: no allocation, no static constructor, no `System.Text.Encoding.CodePages`. The target size is no larger than the forward table's 16 KB. The representation is chosen in 6.1 by measured lookup time and size, and the trimmed-size delta is recorded beside the sizes in [qrcode-symbologies.md](../specs/qrcode-symbologies.md) |
| K9 | The segmenter's lane kernels (Vector256 and AdvSimd, and the Structured Append lane walks) | **Eligible texts take the scalar program** until 6.8 measures the lanes. Other texts keep the lanes unchanged |

## What has to stay true

- **Non-eligible text is byte-identical** in all three symbologies under every option. This covers `Create` (both overloads), `CreateStructuredAppend`, `TryGetRequiredBufferSize` and the refusal messages. It is checked through the public API against the previous commit, on the existing corpora plus a random one.
- **`Decode(Encode(x)) == x` for every eligible text**, in this library and in every pinned reader of the symbology. The asymmetry recorded in the spec ("`Encode(Decode(y))` does not reproduce a Kanji symbol") then holds only for symbols this plan does not write.
- **`PublicAPI.approved.txt` does not change.** `EncodingMode` is internal and gains `Kanji`; only XML docs change.
- **The existing parity suites pass unchanged** (`ModeSegmenter*ParityTest` and the `StructuredAppend*` walks and scans), because the programs they pin are the ones non-eligible text still runs.
- **Speed.** The benchmark arms of non-eligible text stay within noise. An arm whose output moves does different work now, so it is not held to the +10 % rule. It is reported next to the same text with `EciMode.Utf8` forced (today's path), and it must be no slower than that.

## Design

What each piece is. Where it goes in the code is in the inventory.

- **Mode.** `EncodingMode.Kanji` uses Standard QR's indicator (`1000`), as the other members do. Micro QR writes `11` on M3 and `011` on M4, and rMQR writes `100`. The count-indicator widths already exist for decoding: Standard QR 8/10/12, Micro QR 3/4, rMQR 2 to 7. Micro QR's mode availability gains Kanji from M3.
- **Writers.** Each symbology's single-segment and segmented writers gain a 13-bit writer. Adding an arm to a hot `switch` has moved the other arms before (a fourth arm in the Micro QR decoder cost the M2 numeric and M4 byte benchmarks 12-15 %, and rMQR keeps its writers in one `switch` for that reason), so each writer is measured for what it does to the arms it does not touch.
- **Version selection and capacity.** Standard QR's capacity table already carries a Kanji column that nothing reads. rMQR's precomputed fit table grows by one mode. Micro QR's required-bits arithmetic learns the 13-bit rate.
- **Segmenter.** An eighth state, enabled only for eligible text, so every other program is today's. In the eligible path, ASCII characters behave as they do today; a character with a cell reaches only the Kanji state, because Byte would need an ECI. The lower bounds and shortcuts that assume a character outside the alphanumeric alphabet extends only a Byte run are re-derived for the eligible path. There are about a dozen of them across the three planners and the Structured Append walks (listed in the inventory), and each is a place where a wrong bound would silently skip a winning plan.
- **Analysis.** The eligibility pass runs only after the analysis has resolved UTF-8, and stops at the first character without a cell. ASCII and Latin-1 input never pays for it.

## Phases

Each phase follows the test-first workflow, updates the affected specs and `docs/migration.md` in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. `PublicAPI.approved.txt` stays unchanged, and a diff there is a defect. Phases that move a hot path measure it the way the Standard QR writer plan does: a worktree at the previous commit against the change, the same benchmark file, three rounds alternating, and the untouched arms read as the noise of the day. The sub-phases are numbered, not lettered, because the parent plan's Phase 6b is the 128-bit tiers.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 6.1 | **P0** | Reverse table | Generator extension (K8), K3's exclusions, lookup variants measured | The reverse table is the exact inverse of the forward one over 6,872 cells; the seven cells, rows 9-15 and rows 85-94 have no entry; the golden digest is pinned; size and lookup time are recorded. Internal only, and no generator output changes |
| 6.2 | **P0** | Writers and version selection, dark | `EncodingMode.Kanji`; the writers in all three encoders, single and segmented; count widths; capacity and fit tables. Reachable from tests only | Streams equal an independent reference written from the standard's definition, at every count-indicator width of every symbology and at every starting bit alignment of a segmented run. Capacity at every version and level equals the standard's Kanji column: Standard QR from the table in `QRCodeConstants`, Micro QR from ISO/IEC 18004, rMQR from ISO/IEC 23941. Round trip through the three matrix decoders. The non-Kanji arms of `SimpleEncode` and `*EncodeEndToEnd` stay within noise |
| 6.3 | **P0** | Single mode flips (K1, K2, K5) | Eligibility in the analysis; the three generators, sizing and refusal messages; migration note; golden images that move (e.g. `Create_Default_PixelsMatchSample("脂至肢")`, while its `Utf8` twin does not) are regenerated deliberately | One test per class of the eligibility truth table (`EciMode` × `Utf8Bom` × text class × symbology × version range), negatives included. Non-eligible corpus byte-identical. ZXing.Net decodes, in CI, Standard QR output covering every encoder cell. zxing-cpp reads the Micro QR and rMQR output through the tool's spot checks |
| 6.4 | P1 | `Optimal` (K9) | The eighth state; the three planners; the re-derived bounds; eligible texts kept off the lanes | Parity against an independent eight-state reference. The seven-state suites unchanged. For every re-derived bound, a test where the old bound would have skipped the winning plan. `Optimal` never larger than `Single`. Round trip |
| 6.5 | P1 | Structured Append (K6) | The rules above; the chunk cost model; the parity | For the CodeGlyphX set's text, `Optimal` writes parity 176 and `Single` still writes 6. Round trip. Every symbol shares one version and one parity. The balanced bound holds. A one-symbol result equals `Create`. The Structured Append arms are reported against forced UTF-8 |
| 6.6 | P1 | Interop and fixtures | A ZXing.Net Standard QR Kanji fixture in the 12-bit band (versions 27-40). qrtool rMQR Kanji fixtures at count widths 2, 3 and 6, compared module for module (rMQR has one mask, so matrix equality is a valid encoder oracle there); this closes the widths [rmqr-encoder.md](../specs/rmqr-encoder.md) says rest on derivation alone. Every encoder cell through zxing-cpp (all three symbologies) and ZXing.Net (Standard QR). ASCII `\` and `~` in Byte runs beside Kanji runs (a JIS X 0201 reader shows ¥ and ‾). A Kanji segment after ECI 26, read by each reader, as evidence for 6.7. A spot-check list for the Phase 8 checklist | Every reader agrees with the input on every cell it is asked about, or the row goes (K7). Fixtures committed with provenance. Tag `2.0.0-preview.4` (the parent plan's Phase 6 exit) |
| 6.7 | P2 | Kanji beside UTF-8 ECI (D6) | Only if 6.6 shows every pinned reader applies JIS X 0208 to a Kanji segment after ECI 26. It needs its own parity rule, since the carried bytes then depend on the plan | Its own truth table and sweep. It moves output that 6.3-6.5 left alone, so it ships in 2.0.0 or waits for the next major, not a minor |
| 6.8 | P2 | Lanes (K9) | The segmenter lanes and the Structured Append lane walks gain the Kanji state, but only if the scalar path misses the "no slower than forced UTF-8" bar | The measurement recorded either way. It lands before the release if the bar is missed |
| 6.9 | P1 | Fold | Specs; `README.md` (the mode table, the encoding FAQ, the decode-only notes); `docs/data-capacity.md` (Kanji columns; the UTF-8 columns lose 'あ' as their test character, since it is now written as Kanji); `docs/migration.md`; XML docs; the Playground API page regenerated; the index | The specs carry the rules and decisions, and nothing is left only here. The plan and its reference are deleted |

6.1 and 6.2 land dark: no generator writes Kanji until 6.3. The flips are 6.3 to 6.5, and **no tag is cut between the first flip and the last**. So no release has `Create` writing Kanji while `CreateStructuredAppend` writes UTF-8 for the same text, or has `Single` and `Optimal` disagreeing about which texts can be Kanji. 6.6 closes the P0/P1 work with the preview tag. 6.7 and 6.8 can each be dropped without reopening anything else.

## Relation to other work

- **Parent decisions.** K2 answers D5 and K4 answers the first half of D6. D7 stands, because Kanji is a mode, not a charset, and the encoder still writes no ECI 20. **Decided 2026-09-29: this plan ships in 2.0.0, and 2.1.0 is not an option.** It changes default output, which D5 accepts in a major only, so a slip delays Phase 8 rather than moving the plan. That makes the P0 and P1 phases (6.1-6.6 and 6.9) release blockers. Only 6.7 and 6.8 can be dropped: 6.7 then waits for the next major, since it moves output too, and 6.8 is needed only if its bar is missed.
- **128-bit tiers (parent Phase 6b).** `ModeSegmenterLanes`, `TextAnalyzer`, `StructuredAppendScanner`, `StructuredAppendParity` and `StructuredAppendLanes` wait for 6.4 and 6.5: this plan changes their state set, their analysis and their parity bytes. The other kernels of 6b are unaffected and can proceed in parallel. 6b's encode-side ranking is taken after 6.5 lands, since the share of each kernel moves.
- **Benchmarks whose output moves.** The `Optimal` arms of `QRCodeSegmentationEncode` `utf8-60`, `RmQRSegmentationEncode` `utf8-60` and `QRCodeStructuredAppendEncode` `utf8-15k-*`. Their `Single` arms carry ASCII, so they do not move and serve as controls. New shapes: a text in which every character has a cell, per symbology, under `Single`.

## Verification notes

- The reference writer is built in the test from the standard's definition, not from the library's table: the Shift_JIS pair taken from `kanji-sweep.tsv`, minus 0x8140 or 0xC140, high byte × 0xC0 plus low byte, 13 bits MSB first.
- Mutation checks with `tools/mutation_check.cs`: each clause of the eligibility rule, each of the seven exclusions, the 13-bit width, the two offsets and the multiplier, every symbology's mode indicator and count width, Micro QR's M3 floor, the source of the parity bytes, and every re-derived bound. Each must fail a test.
- Matrix equality is a valid encoder oracle on rMQR only. On Standard QR and Micro QR the mask choice differs between encoders, so they are checked through readers, and through data codewords where the stream has only one valid form (a single Kanji segment at a fixed version and level).
- The byte-identical comparison prints which tree each side loaded.
- By hand in the Playground: encode Japanese in each symbology, check the decode preview and the share-link round trip. The Phase 8 physical-scanner pass is already weighted toward Kanji.

## Progress log

Entries are appended per phase: what was done, what was learned, and the benchmark delta or an explicit statement that no hot path moved.

(empty)
