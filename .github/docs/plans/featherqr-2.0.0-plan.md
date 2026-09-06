# 2.0.0: API cleanup, the announced removals, and three feature gaps

## Purpose

The core split shipped as `2.0.0-preview.2`: three packages, new namespaces, new repository name. Nothing about the *shape* of the API changed in it, and that was deliberate — the split plan called the renames "a separate workstream that lands in the same major after this plan". This document is that workstream, plus the three feature gaps that the 2026-09-04 reference-library evaluation identified as worth closing before the major closes.

It fixes WHAT changes, in WHICH order, and WHY. HOW each piece is verified follows the mandatory test-first workflow. When the plan completes, its durable content graduates into [specs/qrcode-symbologies.md](../specs/qrcode-symbologies.md) and the per-symbology records, and this file is deleted.

**The window argument.** A public API is added in a minor and removed only in a major. 2.0.0 is the only release in which the accumulated naming drift, the value-type inconsistencies and the deprecated overloads can all be corrected at once, and consumers are already editing every `using` line for the namespace change. Two edits in one line of caller code (`ECCLevel` → `QREccLevel` next to `CreateQrCode` → `Create`) cost the same as one. Everything in Phases 1-3 exists because of that arithmetic; after 2.0.0 the next chance is 3.0.0.

## Scope

| In | Out |
|---|---|
| The announced removals (`Compression`, `GetRequiredBufferSize` ×2, parameter-list generator overloads) | `CancellationToken` / time-budget decode overloads (additive, 2.1.0) |
| Type and method renames under one naming rule | Payload helpers (deferred by the maintainer; unchanged) |
| Value-kind, immutability and sealing unification | GS1 / FNC1 (unchanged) |
| Symbol geometry in the decode result | A pure-BCL SVG / 1-bit PNG writer (waits for a concrete request) |
| Structured Append, encode and decode (Standard QR only) | `EciMode` as a value type — decided against, see D7 |
| Kanji encoding for all three symbologies | Additional renderer packages |
| Migration table and a mechanical replacement script | Readability heuristics for styled output |

Features are additive and could ship in 2.1.0 without breaking anyone. They are in 2.0.0 because the maintainer chose to make the major a feature-complete line rather than a rename-only one. Kanji encoding is the largest and last of them precisely so it can be demoted to 2.1.0 without disturbing anything else if it slips.

## The naming rule

One rule decides every name in both packages:

> **A prefix says which symbology. No prefix means all three.** The prefixes are `QR` (Standard QR), `MicroQR` and `RmQR`. A noun that literally denotes *a code* keeps the `{Sym}Code` form (`QRCodeData`, `MicroQRCodeGenerator`, `RmQRCodeDecodeInfo`); everything else — enums, ranges, strategies — takes the short prefix. Three-letter acronyms are Pascal-cased (`Ecc`, `Eci`, `Bom`), which the existing `EccLevel` *properties* already are.

Micro QR and rMQR were built after the rule was implicit and already follow it. Every violation is in Standard QR, which was written first, or in a shared type that took the `QRCode` prefix while meaning "the family". That double meaning of `QRCode` — Standard QR in `QRCodeData`, the family in `QRCodeDecodeStatus` — is the actual defect the rule removes.

### Core (`FeatherQR`)

| Now | 2.0.0 | Why |
|---|---|---|
| `ECCLevel` | `QREccLevel` | The only unprefixed enum; siblings are `MicroQREccLevel` and `RmQREccLevel` |
| `QRCodeSegmentation` | `QRSegmentation` | Siblings are `MicroQRSegmentation`, `RmQRSegmentation` |
| `QRCodeVersionRange` | `QRVersionRange` | Sibling is `MicroQRVersionRange` |
| `QRCodeDecodeStatus` | `DecodeStatus` | Shared by all three symbologies; moves out of `QRCodeDecodeInfo.cs` into its own file |
| `QRCodeGenerator.CreateQrCode` | `QRCodeGenerator.Create` | The class already names the symbology; deletes the `Qr` / `QR` casing question rather than answering it |
| `MicroQRCodeGenerator.CreateMicroQRCode` | `MicroQRCodeGenerator.Create` | Same |
| `RmQRCodeGenerator.CreateRmQRCode` | `RmQRCodeGenerator.Create` | Same |
| `QRCodeCalculatedSize.QrSize`, `MicroQRCodeCalculatedSize.QrSize` | `Size` | Matches `QRCodeData.Size`; rMQR's `Width`/`Height` stay |
| `QRCodeGeneratorOptions.Utf8BOM` | `Utf8Bom` | Acronym rule |
| `Internals.StandardQr`, `Internals.RmQr` (namespaces) | `Internals.StandardQR`, `Internals.RmQR` | Internal, but the same rule. The `Internals/RmQr` folder is renamed with the namespace, so folder and namespace agree the way `MicroQR` and `StandardQR` already did |

### Rendering (`FeatherQR.SkiaSharp`)

| Now | 2.0.0 | Why |
|---|---|---|
| `QRCodeExtensions` | `SKCanvasExtensions` | It holds `SKCanvas` extension methods and nothing else; named after what it extends, as the BCL does |
| `QRCodeRenderer` | `SymbolRenderer` | Renders all three symbologies, so it takes no prefix; "symbol" is the word both standards use |
| `QRCodeImageBuilderBase<TSelf>` | `SymbolImageBuilderBase<TSelf>` | Same reasoning, same word |
| `Vector2Slim` (public) | internal | Appears in no public signature; it is an implementation detail that leaked into the surface |

### Names that deliberately do not change

`QRCodeData`, `QRCodeGenerator`, `QRCodeDecoder`, `QRCodeGeneratorOptions`, `QRCodeCalculatedSize`, `QRCodeDecodeInfo`, `QRCodeImageBuilder`, `QRCodeImageDecoder` and their Micro/rMQR siblings are all "a code" nouns and keep their form. `EciMode`, `ModuleRect` and the shape classes are already correct.

## Removals

| Removed | Announced in | Note |
|---|---|---|
| `Compression` | 1.2.0 `[Obsolete]` | No API accepts or returns it |
| `QRCodeGenerator.GetRequiredBufferSize`, `MicroQRCodeGenerator.GetRequiredBufferSize` | 1.2.0 `[Obsolete]` | `TryGetRequiredBufferSize` is the whole sizing surface afterwards |
| Parameter-list `CreateQrCode` / `CreateMicroQRCode` overloads | Spec, "frozen, removed at 2.0.0" | rMQR never had them and is the shape the other two converge on |
| `QRCodeCalculatedSize` public constructor and `IsValid` | Not previously announced | Part of the value-kind unification below, not of the removals phase; `IsValid` duplicates the `bool` that `TryGetRequiredBufferSize` already returns |

`IconData()` is **not** on this list, though the approved API listing shows it as `[Obsolete]`. The type has a `required` member, and that marker is what the compiler emits on the implicit constructor to stop a pre-C#-11 consumer from bypassing the required-member check.

Two consequences to handle inside the phase rather than discover later:

- **`SizingExceptionContractTest` disappears with its subject.** It pins the frozen disagreement between the two deprecated sizing methods (`InvalidOperationException` vs `ArgumentException` for overflow, `ArgumentException` vs `ArgumentOutOfRangeException` for an undefined ECC level). Once both throwing methods are gone the disagreement is gone; the spec paragraph describing it becomes a "lessons learned" entry rather than a live contract.
- **The `options` parameter can finally be defaulted everywhere.** The asymmetry the spec records ("rMQR defaults it, the other two cannot, because their parameter-list overloads would make the call ambiguous") is caused entirely by the overloads being removed here. After the removal all three generators read `Create(text, ecc, in {Sym}CodeGeneratorOptions options = default)`, which is the shape the spec wanted and could not have.

## Shape unification

| Item | Now | 2.0.0 |
|---|---|---|
| `*CalculatedSize` | QR is a `record struct` with a public constructor, `init` setters and `IsValid`; Micro/rMQR are plain `readonly struct`s with internal constructors | One kind: `readonly struct`, internal constructor, get-only properties, no `IsValid`. It is an `out` parameter of a `Try` method, never something a caller builds |
| `*DecodeInfo` | Three `readonly struct`s with different member order; rMQR has no `MaskPattern` (correct — rMQR has one fixed mask) | Same member order everywhere: `Status`, `Version`, `EccLevel`, `MaskPattern` where the symbology defines one, `ErrorsCorrected`, then the new geometry and Structured Append members |
| `GradientOptions` | `record class` holding `SKColor[]` and `float[]` through `init` properties, with a mutable `public static readonly Default` | `sealed class`, constructor copies both arrays, exposes them as read-only, structural equality over elements |
| `IconData` | `class` with public get/set properties and an `[Obsolete]` parameterless constructor | `sealed`, init-only properties, factory methods only |
| `QRCodeData`, `MicroQRCodeData`, `RmQRCodeData`, the three image builders | Non-sealed with no extension point | `sealed`. `SymbolImageBuilderBase<TSelf>` stays public and abstract with a `private protected` constructor; the shape classes stay open on purpose |

**`GradientOptions` carries a live defect, not just a shape problem.** A `record class` generates equality that compares `SKColor[]` by reference, so two gradients with identical colours already compare unequal, and `GradientOptions.Default.Colors[0] = …` mutates the shared instance every caller uses. Both are fixed by the same change, which is why it is in this phase and not filed as a bug.

## Feature work

### Symbol geometry in the decode result

No reference library returns the symbol's position for Standard QR (CodeGlyphX does it for Micro QR only), so this is a differentiator, and it is cheap: the image decoders already build a `PerspectiveTransform`, and the corners are that transform applied to the four corners of module space.

The contract to settle before writing it (D3): which four points, in which space, in which order, and what a matrix-level decode reports. The intended answer is the outer corners of the module area excluding the quiet zone, in source-image pixel coordinates, ordered from the symbol's own top-left — its own, so the quad also conveys rotation and mirroring — with a matrix-level decode leaving the member at its default and a documented way to tell that apart. The type is a small `readonly struct` pair in the core; `System.Numerics.Vector2` is not used, because on netstandard2.0 it would add a package dependency to a package that advertises none.

### Structured Append (Standard QR only)

rMQR does not define it (recorded in [rmqr-encoder.md](../specs/rmqr-encoder.md)) and neither does Micro QR; both keep reporting `UnsupportedContent`. For Standard QR the work is two-sided:

- **Decode** currently rejects the mode as `UnsupportedContent`. After this phase it reads the header and reports symbol index, total count and parity on `QRCodeDecodeInfo`, returning that symbol's own text. A caller concatenates in index order after checking that the parity bytes agree.
- **Encode** splits content across symbols. The parity byte is the XOR of *the original input bytes*, taken before splitting — not derived from the segment plan, which is where CodeGlyphX diverges from every other encoder. QrCodeGenerator is the correct parity reference since its own bug was fixed in `5c6fdfd`.

Open (D4): whether the encode entry point balances the split automatically across the fewest symbols, or takes an explicit count, and whether a "combine these results" helper ships with it. The minimal surface is one method that returns the symbols plus documented rules for recombination; a helper is easy to add in a minor and impossible to remove.

### Kanji encoding

Today all three symbologies decode Kanji mode and none of them writes it; the scope row in [qrcode-symbologies.md](../specs/qrcode-symbologies.md) says "decode only, revisit on concrete demand". This phase flips that row. Four pieces:

1. **A reverse table**, Unicode → the 13-bit compacted value, generated by `tools/QRInteropFixtures` from the same sweep data that produced `ShiftJisKanjiTable`, so the two cannot disagree. Characters outside the JIS X 0208 repertoire simply have no reverse entry and fall through to Byte mode, which is also what makes the seven-cell CP932 divergence a non-issue for encoding.
2. **A Kanji state in `ModeSegmenter`** (7 states → 8), at a flat 13 bits per character, with per-symbology and per-version availability the way `allowAlnum` / `allowByte` already work: Micro QR M3 and M4 only, all rMQR versions, all Standard QR versions. The per-symbology Kanji count-indicator tables already exist on the decode side.
3. **`EncodingMode` gains Kanji.** The enum deliberately excludes it today ("that enum names the modes the encoder can produce"); this phase is what makes that comment false.
4. **Cross-verification against the external oracles** (qrtool, ZXingCpp, ZXing.Net, libzint) plus committed fixtures, since a Kanji encoder that no third-party reader agrees with is worse than no Kanji encoder.

Two decisions inside the phase. **D5:** whether single-mode selection picks Kanji automatically. It is the cheapest single mode for Japanese text, so saying yes changes the bytes the library emits by default for existing callers — acceptable in a major, and it is what other libraries do, but it must be a decision and a migration note rather than a side effect. **D6:** whether a plan may mix Kanji segments with ECI-tagged Byte segments in one symbol. Legal per the standard, historically uneven across readers; the oracle sweep from piece 4 answers it, and the conservative fallback is to suppress Kanji whenever an ECI header is emitted.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Drop the `string` convenience overloads once the parameter-list overloads are gone? | **Decided 2026-09-06: drop.** All four (`QRCodeGenerator` ×2, `MicroQRCodeGenerator` ×1, `RmQRCodeGenerator` ×1). `string` converts implicitly to `ReadOnlySpan<char>` on all four target frameworks, so `Create("text", ecc)` keeps compiling and the overloads contribute a parameter name and nothing else. `Create` becomes two methods per symbology, span and span-plus-destination, which also removes the asymmetry where only Standard QR had `(string, ecc, destination)` |
| D2 | `SymbolRenderer` / `SymbolImageBuilderBase<TSelf>` as the shared-renderer names | **Decided 2026-09-06: adopt**, together with `QRCodeExtensions` → `SKCanvasExtensions`. "Symbol" is the word both standards and these specs already use for the thing all three symbologies produce; the per-symbology `*ImageDecoder` / `*ImageBuilder` types keep their prefixes |
| D3 | Geometry contract: which corners, which space, which order, matrix-level behaviour | As described above; write it into the XML docs and a test before the implementation |
| D4 | Structured Append encode API shape and whether a combine helper ships | Auto-balanced split, no combine helper in 2.0.0 |
| D5 | Does `Segmentation.Single` choose Kanji mode automatically? | Yes, with a migration note; explicit `Utf8Bom` / `EciMode` settings keep Byte mode |
| D6 | Kanji segments mixed with ECI-tagged Byte segments | Let the oracle sweep decide; suppress Kanji under ECI if any oracle disagrees |
| D7 | `EciMode` enum → a value type with `FromValue` / `Value` | **No.** An encoder can only emit a charset it can convert text into (Latin-1 and UTF-8), and the decoder reports every other ECI as `UnsupportedContent` without needing to carry the number. A value type would also need a sentinel for "no ECI", which collides with the real ECI 0, in exchange for expressing values nothing can produce or consume. Recorded as a scope decision with this reason rather than left open |

## Phases

Each phase follows the test-first workflow, regenerates both `PublicAPI.approved.txt` files, updates `docs/migration.md` and the affected specs in the same change, and appends a Progress log entry with Done / Lessons / benchmark delta. A phase that moves a hot path measures it; a phase that only renames states that it did not.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Announced removals | The removals table; retire `SizingExceptionContractTest`; default the `options` parameter on all three generators | Surface shrinks, no rename yet |
| 2 | Renames | Both rename tables; the `string` overload removal from D1; internal namespaces and test namespaces follow | One reviewable `PublicAPI.approved.txt` diff |
| 3 | Shape unification | `*CalculatedSize`, `*DecodeInfo` member set, `GradientOptions`, `IconData`, sealing (`Vector2Slim` went with the Phase 2 rename table) | **API-final for the cleanup.** Tag `2.0.0-preview.3` |
| 4 | Symbol geometry | D3, the geometry members on all three `*DecodeInfo`, all three image decode paths | Matrix-level decode behaviour documented and tested |
| 5 | Structured Append | D4, decode-side header reporting, encode-side split, parity over original input bytes | Round-trip plus oracle cross-check |
| 6 | Kanji encoding | D5, D6, reverse table, segmenter state, `EncodingMode`, capacity docs | Oracle sweep green. Tag `2.0.0-preview.4` |
| 7 | Docs and API freeze | `docs/migration.md` 2.0.0 section rewritten with the full rename table and a mechanical replacement script; README, DESIGN.md, spec scope rows (Kanji, Structured Append, geometry); fold this plan into the specs and delete it | Approved API listing frozen |
| 8 | Release | Below | `2.0.0` on nuget.org |

Phases 4-6 are independent of each other and depend only on 1-3. Phase 6 is last so that it can move to 2.1.0 without reopening anything.

## Release checklist (Phase 8)

- `tools/QRInteropFixtures` manual spot-checks, including new ones for Kanji-mode and Structured Append symbols
- Physical scanner acceptance pass, weighted toward Kanji-mode and Structured Append symbols, which are the two outputs no committed fixture can prove a phone accepts
- `PackageValidationBaselineVersion` set to `2.0.0` in both packed projects — the csproj comments already say "no baseline until 2.0.0 ships; set it then"
- `SkiaSharp.QrCode` 2.0.0 stays an empty metapackage; `tools/check_package_deps.cs` asserts the graph after pack, as it does today
- Version bump: `tools/bump_version.cs` parses only `X.Y.Z`, so going from `2.0.0-preview.4` to `2.0.0` is a hand edit of `Directory.Build.props` and the README install lines, or the tool learns prerelease first. Decide which inside the phase; the tool already prints the manual instruction
- GitHub release notes lead with the migration table link; the Playground redeploys from the same tag

## Progress log

Entries are appended per phase: what was done, what was learned, and the benchmark delta or an explicit statement that no hot path moved.

### Phase 1, announced removals (2026-09-06)

**Done.** `Compression`, both throwing `GetRequiredBufferSize` methods and all seven parameter list `Create*` overloads are gone; the six surviving `Create*` entry points and `TryGetRequiredBufferSize` all take `in {Sym}CodeGeneratorOptions options = default`, so `CreateQrCode(text, ecc)` still compiles and now resolves to the options path. Core drops from 34 exported types to 33, `QRCodeGenerator` from 11 public methods to 6, `MicroQRCodeGenerator` from 8 to 4; both `PublicAPI.approved.txt` files regenerated, and the renderer listing is unchanged. 119 call sites migrated across tests, benchmarks, Playground, samples and `tools/QRInteropFixtures`. `docs/migration.md` gains an "announced removals" section with the argument-to-property table and the `-1` trap; the spec's API-direction paragraphs move to past tense and its exception-disagreement record becomes a lesson. Full suite green: 17,609 tests, 0 failures, both target frameworks.

**Lessons.** Recorded in [qrcode-symbologies.md](../specs/qrcode-symbologies.md): differential tests die with their reference and had to be converted to frozen values *before* the deletion; `required` members make the implicit constructor `[Obsolete]`, which read as an announced removal in the approved listing and is not one (`IconData()` was on this plan's removal list in error); and the one break the compiler could not find, `requestedVersion: -1` against a `QRCodeVersionRange` that rejects `-1` on purpose.

**Benchmarks: not run, no hot path moved.** The removed overloads were forwarding wrappers; the surviving options path and the private `Create*Core` methods are byte-identical. The only benchmark files touched are decode-fixture builders (`BuildModules` and friends), outside any measured method.

**Playground verified by hand**, since `QrInterop` changed: published Debug WASM, served it, and encoded through all four paths the change touches — Standard QR automatic (v5), Standard QR pinned (v10, 65×65), Micro QR automatic (M2, 17×17) and Micro QR pinned (M4, 21×21). Decode was not re-checked; no decode call site changed.

**Not done here, by design.** `QRCodeCalculatedSize`'s public constructor and `IsValid` are listed under the removals table but belong to Phase 3's value-kind unification, and D1 (the `string` convenience overloads) stays with Phase 2 where the rest of the shape decisions are.

### Phase 2, renames (2026-09-06)

**Done.** Both rename tables applied, plus D1: the four `string` convenience overloads are gone, so every generator is two `Create` methods (span, span-plus-destination) and `TryGetRequiredBufferSize`. `Vector2Slim` is internal and moved under `FeatherQR.SkiaSharp.Internals`; the `Internals/RmQr` folder is `Internals/RmQR`, matching its namespace and its `MicroQR` / `StandardQR` siblings. Core stays at 33 exported types, the rendering package drops to 25 (`Vector2Slim`), 58 across both. 215 files touched; full suite green (17,609 tests, both target frameworks), and the golden-pixel tests passing is what proves the mechanical rewrite changed names and nothing else. Playground re-verified in WASM (QR v5 automatic, QR pinned v10, Micro QR M2, rMQR R9x139) and its committed API page regenerated. `docs/migration.md` gains the rename table and a runnable replacement script; the naming rule itself is now recorded in [qrcode-symbologies.md](../specs/qrcode-symbologies.md) under "Public API direction", since this plan is deleted at the end.

**Test names followed the API.** `CreateQrCode_*` / `CreateMicroQRCode_*` test methods became `Create_*`, `QRCodeRendererRunMergeParityTest` became `SymbolRendererRunMergeParityTest`, and `QRCodeSegmentationTest` became `QRSegmentationTest`. Benchmark class names were left alone on purpose: they are the identity of historical BenchmarkDotNet reports, and `QRCodeSegmentationEncode` reads as English rather than as a type reference.

**Lessons.** Two are worth carrying (both recorded in the spec): PowerShell's `-ne` is case-insensitive, so a case-only rename silently writes nothing; and a blanket identifier rewrite reaches third-party API with the same spelling — `QRCoder.QRCodeGenerator.ECCLevel` in the comparator benchmarks was renamed along with ours and only the compiler caught it. A third is process rather than code: `@(@('a','b'))` collapses to a two-element array in PowerShell, so `$pair[0]` became a *character* and `String.Replace(char, char)` corrupted three test files. They were restored from HEAD (Phase 1 was committed) and the rules re-applied; the lesson is to build replacement pairs as `[pscustomobject]` or ordered dictionaries, never as nested arrays.

**Benchmarks: not run, no hot path moved.** Renames only; the two call sites that changed shape (`string` overload callers) now bind to the span overload the string one forwarded to.

### Phase 3, shape unification (2026-09-06)

**Done.** `QRCodeCalculatedSize` is a plain `readonly struct` with an internal constructor and get-only properties, like its two siblings: the public constructor, `IsValid`, the `init` setters, deconstruction and `IEquatable<T>` are gone. `GradientOptions` is a `sealed record class` that copies the arrays it is given, reads them back as `ReadOnlySpan<T>` (`ColorPositions` empty rather than null when evenly distributed) and compares by element — the generated equality it replaces compared arrays by reference, so identical gradients were unequal and `Default` was publicly mutable. `IconData` is `sealed` with `init`-only properties. `QRCodeData`, `MicroQRCodeData`, `RmQRCodeData` and the three image builders are `sealed`. `TypeShapeTest` (25 cases) is the new guard for all of it. Full suite green: 17,659 tests, both target frameworks. **This is the API-final point for the cleanup; `2.0.0-preview.3` can be tagged from here.**

**One item of the plan turned out to be already true.** The `*DecodeInfo` member order was listed as inconsistent, and it is not: all three declare `Status, Version, EccLevel, [MaskPattern,] ErrorsCorrected` in that order, with matching constructor order. What differs is the *approved API listing*, which sorts members by type and name, and reading the listing rather than the source is what produced the claim. The test now pins the member set instead, which is the property that actually matters.

**Second round of rework, from an adversarial review (2026-09-06).** Three reviewers and three refutation passes found the same mistake still in place elsewhere: demoting the six result values to plain structs had also removed `ToString` (a logged size printed as the type name), `==` and `IEquatable<T>` — measured 64 bytes of boxing per `Equals` — while `ModuleRect` and the three generator options next door stayed `readonly record struct`. `IconData` had lost `with` the same way `GradientOptions` had. Both are now `record`s again with the members that were actually wrong (public constructor, `init` setters on results) still removed, so the public surface gains only `IEquatable<T>` on seven types and loses nothing. The rule that came out of it is in the spec: name the member that is wrong and remove *that*, rather than reaching for the enclosing kind. **`with` is offered for the members that carry no invariant, and that line is now written down.** `GradientOptions.Direction` is `init`; `Colors` and `ColorPositions` are not, because `with` assigns one member at a time and those two have to agree in length — an `init` colour list would let a caller build a gradient whose stops no longer match and find out only when it is drawn. Verified that a span property *can* take an `init` accessor, and that doing so lets exactly that mismatch through, which is what settled it. `IconData` needs no carve-out because none of its members validate against another at construction, which is why the two option types differ. A `WithColors` wither was written to carry the direction and stops for the caller, and then dropped: it saved two copied-over arguments and nothing else — a mismatched colour count throws either way — and nothing in the repository recolours an existing gradient, so it was API added on anticipation. The reason sits in a code comment where the methods were.

**The migration guide was the symptom, not the defect.** The recolour recipe had to read `options.ColorPositions.IsEmpty ? null : options.ColorPositions.ToArray()` because `GradientOptions` handed colours back as spans and took them as arrays, and spelled "no stops" as empty on the way out and `null` on the way in. The constructor takes spans now, empty on both sides, so the line is `new GradientOptions([…], options.Direction, options.ColorPositions)`. Arrays still convert implicitly, so no existing call site changed; what changed is that `null` is no longer passable for the stops and an empty array now means "evenly distributed" instead of throwing. Documenting an unreadable incantation would have shipped the asymmetry.

Four more findings from the same round were fixed with it: the migration guide's recolour recipe silently dropped `ColorPositions` (it now carries them, and says why the omission is invisible); `README.md` still described `GetRequiredBufferSize` as deprecated-but-present, a release out of date; `ShapeHierarchies_StayOpen_AndTheBuilderBaseDoesNot` ran `All()` over the *non-public* constructors, which a public constructor empties, so it passed vacuously for the violation it names — it now asserts the public constructor list is empty, demonstrated by making the constructor public and watching the test fail; and the immutability wording claimed the colour array was unreachable, which `MemoryMarshal.GetReference` disproves with no `unsafe` and no opt-in, so the documented guarantee is "immutable through the API". Naming the escape hatch in the XML remarks was the first attempt and was wrong for the same reason a library does not caveat reflection: it is noise in front of a caller who wants a gradient, and it is not a threat anyone can close. What the finding was really about is the *absolute* claim, which a later change could have leaned on to skip the defensive copy; the qualifier alone carries that, and the reasoning sits in a code comment beside the property. Left for the maintainer to weigh: `GradientOptions` is no longer System.Text.Json-serializable, because `ReadOnlySpan<T>` properties throw on both serialize and deserialize — it compiles clean and fails at run time, so it needs a decision (array-typed companion members, a `JsonConverter`, or documenting the type as not serializable).

**One round of rework, from a maintainer review.** The first cut made `GradientOptions` a plain `sealed class`, which silently removed `with` — an idiom the three generator options structs still support and the README teaches. Restoring the record while keeping the arrays private is what the defect actually called for: the bug was the *public array*, not the record, and a private array makes the record's shallow copy safe rather than dangerous. `Direction` is the only `init` member, so `with { Direction = … }` compiles and `with { Colors = … }` does not, which is the honest split — colours define the gradient, direction is the cheap variation. `PrintMembers` is hand-written because the generated one would print a `ReadOnlySpan<T>` member as a type name and has to compile on four target frameworks.

**Left alone deliberately.** `ModuleRect` keeps its public constructor and `init` members: it is a geometry value a caller may also build (for tests or their own layout), not an answer the library hands back, so the `Try`-result rule does not apply to it. The generator options structs keep `init` for the same reason in reverse — a caller builds those.

**Benchmarks: not run, no hot path moved.** The only behavioural change is `GradientOptions` copying two small arrays once at construction, outside any measured path; the renderer reaches the same arrays through an internal property. The Playground was not re-verified because `QrInterop` did not change in this phase, and the gradient rendering path is covered by the committed golden PNGs in `RenderingApprovalTest`.
