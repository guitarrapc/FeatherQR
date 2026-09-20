# Documentation Index

The single index of design documentation. Documents under `.github/docs/` are for contributors and design; user-facing documentation (usage, migration, capacity tables) lives in [docs/](../../docs/).

Authoring rules, document types, and naming conventions: [docs_authoring_guidelines.md](docs_authoring_guidelines.md).

## Design principles

| Document | Covers |
|---|---|
| [DESIGN.md](DESIGN.md) | Library design principles (English + Japanese) |

## Specs (`specs/`)

Design records and spec-to-code maps for shipped behavior, organized symbology-first.

| Document | Type | Covers |
|---|---|---|
| [qrcode-symbologies.md](specs/qrcode-symbologies.md) | Architecture record | Symbology model, shared vs per-symbology components, API and data-model direction, scope decisions |
| [standardqr-spec-map.md](specs/standardqr-spec-map.md) | Spec-to-code map | Standard QR pipeline vs ISO/IEC 18004 |
| [standardqr-encoder.md](specs/standardqr-encoder.md) | Design record | Standard QR encoder scope and decisions (single segment per input, no Kanji encoding, ECI policy) |
| [standardqr-decoder.md](specs/standardqr-decoder.md) | Design record | Standard QR decoder scope, input tiers, lessons learned |
| [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md) | Design record | Committed fixture corpus, manifest schema, external-oracle capability matrix |
| [microqr-spec-map.md](specs/microqr-spec-map.md) | Spec-to-code map | Micro QR encoding pipeline vs ISO/IEC 18004 |
| [rmqr-spec-map.md](specs/rmqr-spec-map.md) | Spec-to-code map | rMQR pipeline vs ISO/IEC 23941 (encoder, rendering, matrix decoder and image detection implemented) |
| [rmqr-encoder.md](specs/rmqr-encoder.md) | Design record | rMQR encoder API, oracle-verified symbol parameter tables, decisions, verification record (spec-first) |
| [rmqr-decoder.md](specs/rmqr-decoder.md) | Design record | rMQR decoder scope (matrix and image level), image detection design (format-first, sub-finder anchored, gated perspective search), decisions incl. the still-open Table 8 misdecode-protection reading, lessons |

## Plans (`plans/`)

Forward-looking strategy; durable decisions graduate into `specs/` after implementation, and the plan is then deleted rather than kept as a parallel history.

| Document | Covers |
|---|---|
| [featherqr-2.0.0-plan.md](plans/featherqr-2.0.0-plan.md) | The rest of 2.0.0: naming rule and renames, the announced removals, value-kind and immutability unification, symbol geometry, Structured Append, Kanji encoding, release checklist |

The 2.0.0 core split plan (`FeatherQR` core, `FeatherQR.SkiaSharp` renderer, `SkiaSharp.QrCode` metapackage, repository rename) completed on 2026-09-06 and was folded into [qrcode-symbologies.md](specs/qrcode-symbologies.md) (package architecture, seam, graph, the "why three packages" record, scope decisions and lessons) and [DESIGN.md](DESIGN.md). The Micro QR / rMQR implementation and test-strategy plans, the Kanji mode decode plan and the generator API options plan all completed and were folded into the specs above. What survived them lives in: the API and options rules, the Kanji mapping decision and the scope table in [qrcode-symbologies.md](specs/qrcode-symbologies.md); the oracle landscape, test-layer reasoning and fixture lessons in [qrcode-test-fixtures.md](specs/qrcode-test-fixtures.md); and the per-symbology encoder and decoder records. The Standard QR matrix decode plan (codeword extraction through the encoder's placement runs, the 64-bit window bit stream reader, the ARM64 measurement and the re-associated AdvSimd syndrome kernel) completed on 2026-09-21 and was folded into [standardqr-decoder.md](specs/standardqr-decoder.md) (the decisions, the measurements, why the round stopped where it did, and the lessons on measuring and verifying a hot-path change) and [qrcode-symbologies.md](specs/qrcode-symbologies.md) (the reopened and re-closed ARM64 queue).
