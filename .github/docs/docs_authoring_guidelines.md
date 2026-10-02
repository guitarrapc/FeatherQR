# Documentation Authoring Guidelines

These rules keep the documents under `.github/docs/` from drifting apart in structure as they split across files.

## Directory layout

| Location | Purpose |
|---|---|
| `README.md` | The documentation index and the only list of all documents |
| `DESIGN.md` | Design principles (English + Japanese) |
| `specs/` | Design records and spec-to-code maps for shipped behavior |
| `images/` | Figures the specs show, one folder per symbology, generated rather than drawn by hand (the decode figures by `tools/decode_figures.cs`, which also decodes each input it draws). Regenerate them with the tool instead of editing the SVG |
| `plans/` | Forward-looking strategy and implementation plans. After implementation, durable decisions move into `specs/` and the plan file is deleted, so no completed plan survives as a second, drifting copy of the record |
| `plans/references/` | Research for an open plan (registry checks, measurements, inventories) that lets later sessions continue without re-surveying. It is linked from the plan and deleted with it |

## Linking policy

These rules keep links maintainable when documents split:

- `README.md` in this directory is the only document that lists every document. GitHub renders it when the `.github/docs` folder is opened, so one link to the folder shows the index.
- The repository root `README.md` links freely to user documentation (`docs/`) but into `.github/docs` only via the index, plus at most one design-record deep link per feature section where that section discusses scope or limitations the design record explains.
- Documents inside `.github/docs` cross-link with relative links as needed. Shared content lives only in `specs/qrcode-symbologies.md`, except the SIMD tier tables rendered into `specs/qrcode-simd-tiers.md`.
- When you rename or add a document, update the index, then grep the repository for the old file name and fix every inbound link (README, skills and memory files too) in the same change.

## What belongs in a document

- WHAT: what the feature or behavior is
- WHY: the reasoning and motivation behind decisions
- Lessons learned: what was found only by trying

Detailed HOW (step-by-step implementation, algorithm internals, bit layouts, formulas) goes in code comments next to the implementation, not here, so it stays in sync with the code.

## Writing

Write straight, concise English: conclusion first, full sentences, no decoration, no emphasis markup, no em-dashes and no semicolons. Split joined clauses into sentences, and turn a run of items that contain commas into a list. Put each paragraph on one line. DESIGN.md is the exception and puts one sentence per line.

## Spec organization and naming

Specs are organized symbology-first, mirroring `src/` and `tests/`:

- `qrcode-symbologies.md`: the cross-cutting architecture record and document index
- `{symbology}-{doctype}.md`: per-symbology documents, where `{symbology}` is `standardqr`, `microqr` or `rmqr`

## Document types and required structure

Every per-symbology spec follows one of the templates below. For a new symbology, copy the section skeleton of the Standard QR document of the same type. Do not invent a new outline.

### Architecture record (`qrcode-symbologies.md`)

It is the only home for anything shared across symbologies: shared component inventory, dependency rules, API and data-model direction, scope decisions and the document index. Per-symbology documents link here instead of restating shared content, because duplication makes split files drift.

### Generated tables (`qrcode-simd-tiers.md`)

Each table is rendered from the code, sits between `<!-- BEGIN GENERATED {name}: … -->` and `<!-- END GENERATED {name} -->`, and is never edited by hand: `SimdTiersDocTest` fails when one differs from the code and, run outside CI, rewrites it. The text around the tables only says how to read them (the builds in each class, the tier key). A cell's reason stays beside its row in the code, and the reason for the table's shape stays in `qrcode-symbologies.md`.

### Spec-to-code map (`{symbology}-spec-map.md`)

It must have these sections, in order:

1. Title and intro: which standard, which symbology, and the "map, not a spec copy" statement
2. Pipeline overview (diagram)
3. One section per pipeline stage, each with a `| Spec reference | Topic | Implementation |` table followed by a `Reference tests:` line
4. Maintenance Notes

Implementation links outside the symbology's own namespace are marked "shared across symbologies".

A decode diagram follows these rules:

- It is plain text in a fenced block, not Mermaid, so it reads raw in editors, terminals and search results.
- The matrix level and the image level are separate diagrams: an arrow chain for the matrix level and an indented outline for the image level, where indentation shows what runs inside a loop or only after the step above it. The outline ends with a line saying what decides or ends the scan.
- It shows the stages, their order, the loop each stage sits in and the branches that skip stages, but no tuned numbers (thresholds, budgets, tolerances, measured bounds). Structural facts from the standard, such as version ranges, are allowed.
- Each box names a stage that has a table row using the same words (two small stages may share one), and the rows follow the diagram's order.
- The shared image decode passes are drawn once, in `qrcode-symbologies.md`. Each map's image diagram starts where a pass hands over, after a line linking there.
- The image decoder class's pipeline remarks are a short numbered list of the same stages in the same order, under the diagram's names or short forms of them, with their main conditions. They say each method states its own conditions in full. They nest one level only where a flat list would read in the wrong order.
- Adding, removing or reordering a decode stage or retry updates the diagrams, the remarks and the decode figures in the same change. There is no automated check: a check on names would catch a rename but not a new order or an added retry, and those drifted before.
- Before it ships, a reviewer who did not draw the diagram traces it against the code.

### Design record (`{symbology}-{feature}.md`, e.g. `standardqr-decoder.md`)

It must have these sections, in order:

1. Title and intro: scope, links to the symbology's spec map
2. What: behavior, supported/unsupported tables, measured envelope
3. Why: scope reasoning
4. Decisions: choices made, with rationale
5. Lessons Learned: grouped by area

A decoder's design record shows its decode figures under What, following these rules:

- There are two kinds: a stage strip with one panel per stage of the main path on a clean symbol, and one figure per input class showing the input, with what the decoder finds drawn over it, next to the path through the stages (above it for a wide symbol). Every input figure of a symbology has the same boxes, so only their states change.
- They live in `images/{symbology}/` and are drawn by `tools/decode_figures.cs`, which also decodes every input it draws. Rerun it with `dotnet run tools/decode_figures.cs -- .github/docs/images`. Never edit an SVG by hand.
- They show the path an input takes as the code defines it, not a trace captured from a run.
- The map's decode diagram is the authority. A figure's boxes use its stage names or short forms of them. Each box is one stage or a group of stages, and the first box stands for the shared passes.
- A figure has only pictures and labels. Its one-line description goes above it and its notes below it, in Markdown, and neither has tuned numbers. Notes about a key or dashed box are numbered to match the box, in the boxes' order, on the box whose stage does the work. What runs as usual is plain prose. The clean figure has no key box, so it shows the plain path.
- Where a figure claims a mechanism the decode alone does not show, the tool asserts that too, as it does for the lighting figure's threshold.
- A box is key (green) when, with its stage switched off for that input, the input would not read, or would read only after more grids fail than with the stage on. A grid is one sampling, however often it is read. A switch-off probe decides this, not which grid happened to decode.
- An input class gets a figure only where the decoder has a measured envelope for it. Each input is tuned until the stage its figure is about is the one that decides it, inside that envelope and not at a setting where this holds only by chance.
- Figures follow Standard QR's order. A symbology's own class sits next to the nearest Standard QR class. Classes the shared passes decide (uneven lighting, light on dark) are drawn once, for Standard QR.
- Each figure is an opaque white card, so it reads on light and dark pages. `--preview` writes a page of every figure on both, to check.
- Before it ships, an independent reviewer renders each input as the tool does, switches stages off, and checks every note and box state against the result and the code.

## Cross-document consistency rules

- Shared knowledge appears once, in `qrcode-symbologies.md` (the SIMD tier tables in `qrcode-simd-tiers.md`). Per-symbology documents link to it.
- When code moves or is added, update the affected spec-map links in the same change.
- After implementing, add the decisions and lessons learned that were not captured upfront to the design record.
- Keep the [documentation index](README.md) in sync when adding or renaming documents.
