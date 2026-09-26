# Documentation Authoring Guidelines

Rules for documents under `.github/docs/`. These exist so that documents split across files keep one consistent structure instead of drifting apart.

## Directory layout

| Location | Purpose |
|---|---|
| `README.md` | The documentation index, the single enumeration of all documents |
| `DESIGN.md` | Design principles (English + Japanese) |
| `specs/` | Design records and spec-to-code maps for shipped behavior |
| `images/` | Figures the specs show, one folder per symbology. Generated, not drawn by hand: the decode figures by `tools/decode_figures.cs`, which also decodes each input it draws. Regenerate them with the tool rather than editing the SVG |
| `plans/` | Forward-looking strategy and implementation plans; durable decisions graduate into `specs/` after implementation, and the plan file is then **deleted**, so a completed plan never survives as a second, drifting copy of the record |
| `plans/references/` | Research gathered for an open plan (registry checks, measurements, inventories) so later sessions can continue without re-surveying; linked from the plan and deleted with it |

## Linking policy

Links must not turn file splits into maintenance chaos:

- `README.md` in this directory is the **only** document that enumerates the full document set. GitHub renders it automatically when the `.github/docs` folder is opened, so one link to the folder lands on the index.
- The repository root `README.md` links freely to user documentation (`docs/`), and into `.github/docs` only via the index, plus **at most one** design-record deep link per feature section, where the section discusses scope or limitations that the design record explains.
- Documents inside `.github/docs` cross-link each other with relative links as needed; shared content itself lives only in `specs/qrcode-symbologies.md`.
- When renaming or adding a document: update the index, then grep the repository for the old file name and fix every inbound link in the same change (README, skills, memory files included).

## What belongs in a document

- **WHAT**, what the feature or behavior is
- **WHY**, the reasoning and motivation behind decisions
- **Lessons learned**, things discovered only by actually trying

Detailed HOW (step-by-step implementation, algorithm internals, bit layouts, formulas) does not belong here, it lives in code comments next to the implementation, where it stays in sync with the code.

## Spec organization and naming

Specs are organized symbology-first, mirroring `src/` and `tests/`:

- `qrcode-symbologies.md`, the cross-cutting architecture record and document index
- `{symbology}-{doctype}.md`, per-symbology documents, where `{symbology}` is `standardqr`, `microqr`, or `rmqr`

## Document types and required structure

Every per-symbology spec follows one of the templates below. When adding a new symbology, copy the section skeleton from the existing Standard QR document of the same type, do not invent a new outline.

### Architecture record (`qrcode-symbologies.md`)

The single home for anything shared across symbologies: shared component inventory, dependency rules, API and data-model direction, scope decisions, and the document index. Per-symbology documents link here instead of restating shared content, duplication is how split files drift.

### Spec-to-code map (`{symbology}-spec-map.md`)

Required sections, in order:

1. Title and intro, which standard, which symbology, and the "map, not a spec copy" statement
2. Pipeline overview (diagram)
3. One section per pipeline stage, each with a `| Spec reference | Topic | Implementation |` table followed by a `Reference tests:` line
4. Maintenance Notes

Implementation links that point outside the symbology's own namespace are marked "shared across symbologies".

### Design record (`{symbology}-{feature}.md`, e.g. `standardqr-decoder.md`)

Required sections, in order:

1. Title and intro, scope, links to the symbology's spec map
2. What, behavior, supported/unsupported tables, measured envelope
3. Why, scope reasoning
4. Decisions, choices made, with rationale
5. Lessons Learned, grouped by area

## Cross-document consistency rules

- Shared knowledge appears exactly once, in `qrcode-symbologies.md`; per-symbology documents link to it.
- When code moves or is added, update the affected spec-map links in the same change.
- After implementing, update the relevant design record with decisions and lessons learned that were not captured upfront.
- Keep the [documentation index](README.md) in sync when adding or renaming documents.
