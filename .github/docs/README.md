# Documentation Index

This is the only index of the design documents under `.github/docs/`, which are written for contributors. User documentation (usage, migration, capacity tables) is in [docs/](../../docs/).

[docs_authoring_guidelines.md](docs_authoring_guidelines.md) gives the authoring rules, document types and naming conventions.

## Design principles

| Document | Covers |
|---|---|
| [DESIGN.md](DESIGN.md) | Library design principles (English + Japanese) |

## Specs (`specs/`)

This folder holds design records and spec-to-code maps for shipped behavior, organized symbology-first.

| Document | Type | Covers |
|---|---|---|
| [qrcode-symbologies.md](specs/qrcode-symbologies.md) | Architecture record | Symbology model, shared vs per-symbology components, API and data-model direction, scope decisions |
| [qrcode-simd-tiers.md](specs/qrcode-simd-tiers.md) | Generated tables | The SIMD tier each kernel runs on each build: the builds in each build class, what each class leaves to the CPU, the tier key, every kernel's tiers and cells. A test renders the tables from `SimdTiers.cs` |
| [standardqr-spec-map.md](specs/standardqr-spec-map.md) | Spec-to-code map | Standard QR pipeline vs ISO/IEC 18004 |
| [standardqr-encoder.md](specs/standardqr-encoder.md) | Design record | Standard QR encoder scope and decisions (single segment per input, Kanji on request for text that JIS X 0208 holds, ECI policy) |
| [standardqr-decoder.md](specs/standardqr-decoder.md) | Design record | Standard QR decoder scope, input tiers, lessons learned |
| [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) | Design record | Committed fixture corpus, manifest schema, external-oracle capability matrix, image decode sweep and real-image corpus measured against other readers |
| [microqr-spec-map.md](specs/microqr-spec-map.md) | Spec-to-code map | Micro QR encoding and decoding pipelines vs ISO/IEC 18004 |
| [microqr-decoder.md](specs/microqr-decoder.md) | Design record | Micro QR decoder scope (matrix and image level), single-finder image path, decode figures, decisions, lessons learned |
| [rmqr-spec-map.md](specs/rmqr-spec-map.md) | Spec-to-code map | rMQR pipeline vs ISO/IEC 23941 (encoder, rendering, matrix decoder and image detection implemented) |
| [rmqr-encoder.md](specs/rmqr-encoder.md) | Design record | rMQR encoder API, oracle-verified symbol parameter tables, decisions, verification record (spec-first) |
| [rmqr-decoder.md](specs/rmqr-decoder.md) | Design record | rMQR decoder scope (matrix and image level), image detection design (format-first, sub-finder anchored, gated perspective search), decisions including the open Table 8 misdecode-protection reading, lessons |

## Plans (`plans/`)

Plans hold forward-looking strategy. After implementation, durable decisions move into `specs/` and the plan is deleted, not kept as a parallel history.

| Document | Covers |
|---|---|
| [cross-language-benchmark-plan.md](plans/cross-language-benchmark-plan.md) | Speed against readers and writers outside .NET (zxing-cpp, libzint, the Rust crates, the JVM readers): one protocol and collector for every language's CLI, whole-process timing as an outside check, one Docker image for linux-x64 and linux-arm64, NativeAOT's default build against `x86-64-v3` |
| [featherqr-2.0.0-plan.md](plans/featherqr-2.0.0-plan.md) | Remaining 2.0.0 work: naming rule and renames, announced removals, value-kind and immutability unification, symbol geometry, Structured Append, Kanji encoding, 128-bit tiers for builds without AVX2, release checklist |
| [standardqr-binary-encoder-plan.md](plans/standardqr-binary-encoder-plan.md) | A fast path for the Standard QR Alphanumeric writer, and the Numeric one if it registers: the writer's share of an encode measured first as the ceiling, then a variant ladder up to the rMQR writer's vector form, keeping streams byte-identical at every bit alignment |

The 2.0.0 core split plan (`FeatherQR` core, `FeatherQR.SkiaSharp` renderer, `SkiaSharp.QrCode` metapackage, repository rename) completed on 2026-09-06 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (package architecture, seam, graph, the "why three packages" record, scope decisions, lessons) and [DESIGN.md](DESIGN.md).

The Micro QR / rMQR implementation and test-strategy plans, the Kanji mode decode plan and the generator API options plan completed and were folded into the specs above. [qrcode-symbologies.md](specs/qrcode-symbologies.md) has their API and options rules, Kanji mapping decision and scope table. [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) has the oracle landscape, test-layer reasoning and fixture lessons. The per-symbology encoder and decoder records have the rest.

The Standard QR matrix decode plan (codeword extraction through the encoder's placement runs, the 64-bit window bit stream reader, ARM64 measurement, the re-associated AdvSimd syndrome kernel) completed on 2026-09-21 and was folded into [standardqr-decoder.md](specs/standardqr-decoder.md) (decisions, measurements, why the round stopped where it did, lessons on measuring and verifying a hot-path change) and [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the reopened and re-closed ARM64 queue).

The Standard QR image decode plan (the threshold fill's vector tiers and mirrored second polarity, the vectorized piecewise mesh sampler, the finder search's cross-check walks, run bounds and edge-list row kernel, ARM64 measurement and tiers) completed on 2026-09-23 and was folded into [standardqr-decoder.md](specs/standardqr-decoder.md) (the decision on what stayed bit-identical and where the round stopped, per-stage measurements on both machines, lessons on measuring) and [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the shared binarizer, the ARM64 queue reopened for three stages).

The decode pipeline diagrams plan completed on 2026-09-27 and was folded into the three spec-to-code maps (matrix and image diagrams matching the decoders, a maintenance rule), the three decoder records (decode figures, with [microqr-decoder.md](specs/microqr-decoder.md) created for them, and three stages found almost never to decide inside the measured envelope), [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the shared image decode passes), the [authoring guidelines](docs_authoring_guidelines.md) (diagram and figure rules), the test-first skill and `tools/decode_figures.cs`. It left out encoder diagrams and figures (not audited), plan identifiers and other-library names already in the specs (separate cleanups), rewrites of correct but long design-record prose, and figures captured from a decoder run (a trace needs hooks into internals, and the figures explain the same path without them).

The SIMD tier coverage plan (which tier each kernel runs on which build, stated in one table and checked against the builds, plus the tier file layout) completed on 2026-09-28 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the SIMD tier inventory with the four build classes and what keeps the table true, two scope decisions, lessons on dispatch codegen, NativeAOT, ARM64 knobs, moving code between files and WebAssembly). The table's only copy is `src/FeatherQR/Internals/SimdTiers.cs`. Since 2026-10-02 [qrcode-simd-tiers.md](specs/qrcode-simd-tiers.md) shows it rendered. It left out the decisions the table is for: which instruction set a NativeAOT publish should target, and 128-bit tiers where x64 without AVX runs scalar.

The image decode accuracy plan (renders and photographs another reader decodes and this library does not, counted on the same images: 2,002 synthetic Standard QR renders and 153 real-image reads on 2026-09-21, 18 and 28 on 2026-09-28) completed on 2026-09-28 and was folded into [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) (the sweep tool, its measuring rules, where the gap stands, the cause of each remaining real image, oracle lessons), the three decoder records (envelopes against the other readers, plus decisions, refuted candidates, leads and lessons of regional binarization, the Micro QR and rMQR failure paths, Micro QR grid evidence, the grey-level frame, the finders' frame and the stages before it, the like-edge finder check, the small-lattice mesh and rMQR's perimeter trace, with [standardqr-decoder.md](specs/standardqr-decoder.md) also holding lessons on measuring an accuracy change), [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the passes' skip and verdict rules) and the Micro QR and rMQR spec maps. It left out per-round narration and test counts, benchmark tables that decided nothing, and one machine's shell-tool traps.

The Structured Append plan (decode, the balanced encode split, interop, fourteen performance rounds, the adversarial review of the branch) completed on 2026-09-29 and was folded into [standardqr-encoder.md](specs/standardqr-encoder.md) (design, refusal rule, decisions, lessons), [standardqr-decoder.md](specs/standardqr-decoder.md) (decode decisions, reassembly rules, a lesson on one-conditional rules) and [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the decode result member only Standard QR has). The corpus and interop checks were already in [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md), and the plan's open items became follow-ups F20 to F23 of the 2.0.0 plan. It left out per-round narration, test counts and benchmark tables.

The decode pipeline structure plan (the decoders held against the generators' pipeline shape: allocation guards over every decode path, shared image decode passes, result rule and image view, one module buffer per scan, a shared Reed-Solomon block stage, rMQR's format positions stated once, the four-point sampler moved to the shared namespace, a failed matrix decode reporting no characters written) completed on 2026-09-30 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (shared decode primitives, the result rules and why Standard QR keeps its own, the allocation contract with its guard and measuring lessons, lessons on timing a refactor), [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) (a baseline built apart, the corpus compared on this library's columns), [standardqr-decoder.md](specs/standardqr-decoder.md) (the failure diagnostics decision, the module buffer lesson and its cost), [rmqr-decoder.md](specs/rmqr-decoder.md) (two lessons on the format readers) and [migration.md](../../docs/migration.md) (characters written on a failed matrix decode). Its open decision on decoder options became D8 of the 2.0.0 plan, decided against on 2026-10-02. It left out the findings table of the review it started from, per-phase narration and test counts, and each phase's timing tables.

The 128-bit tiers plan (a portable 128-bit tier for each kernel that ran scalar on x64 without AVX or on WebAssembly, where one beat that build's code when measured on that build, plus the timing mode of the report projects, the merge of main's shared image decode and its follow-ups, and the ARM64 measurement) completed on 2026-10-01 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the round's rules, measuring method, end-to-end result and lessons under the SIMD tier inventory, plus the scope row and shared kernels with no record of their own), the four encoder and decoder records of Standard QR and rMQR (each kernel's tier, numbers and lessons) and the reasons beside the rows of `SimdTiers.cs`. It left out per-phase narration, the variant and kernel tables, and each change's instruction counts.

The shared candidate scan plan (Micro QR's and rMQR's per-candidate rules checked by one contract test both decoders run, their candidate scan and grid read moved into shared code, a short-destination measurement in the sweep tool) completed on 2026-10-01 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the single-finder candidate scan: its rules and their reasons, what they cost before, how they are checked and their residuals, plus the lesson on the order of an A/B run), [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) (the `destination` measurement, plus lessons on premises, masked faults, worst cases and the mutation tool) and the Micro QR and rMQR decoder records and spec maps, which keep only their own parts and link the shared code. It left out per-phase narration and benchmark tables. The planted faults, including those a review added, are in [tools/mutants/single-finder-candidate-scan.tsv](../../tools/mutants/single-finder-candidate-scan.tsv).

The Kanji encoding plan (Kanji mode written by all three generators on request through `AllowKanji`, the reverse table, an eighth segmentation state, Kanji Structured Append sets, the interop sweep and the phone scans that turned the default back to UTF-8, and the speed work) completed on 2026-10-02 and was folded into:
- [qrcode-symbologies.md](specs/qrcode-symbologies.md): which texts are written in Kanji mode and why the rule is not wider (on request only, no Kanji beside an ECI header, the seven CP932-divergent cells), the reverse table, trimmed sizes, the scope row and lessons.
- [standardqr-encoder.md](specs/standardqr-encoder.md): the Kanji plan under `Optimal`, Kanji Structured Append sets and their parity, the scalar lanes, the speed record and lessons.
- [rmqr-encoder.md](specs/rmqr-encoder.md): the Kanji scan's filters, and why its build runs the program itself.
- [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md): the Kanji fixtures, the oracle test, `spot-check-kanji` and oracle lessons.
- `docs/migration.md`, the README, and the Kanji columns of `docs/data-capacity.md`.

It left out per-phase narration, step timings and benchmark tables, and the code inventory the plan was written from.
