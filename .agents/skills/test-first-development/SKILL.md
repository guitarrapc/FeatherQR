---
name: test-first-development
description: Mandatory test-first workflow for all implementation, modification, and bug fix tasks in this project. Covers red-green test cycle, regression tests, benchmark verification, spec updates, equivalence-class coverage, and Playground (WASM) manual verification. Applies whenever code under src/ is added or changed.
---

# Test-First Development

**This skill is mandatory for every task that adds or modifies code under `src/`.**

Skip this skill only when the change is limited to documentation, configuration, samples, or CI workflow metadata with no production-code behavior change.

## Workflow

### 1. Write Failing Tests First (Red)

Before writing any production code, create tests that demonstrate the current behavior is wrong or missing.

- **New feature**: Write a test that exercises the new behavior and verify it fails (compile error or assertion failure).
- **Bug fix**: Write a test that reproduces the bug and verify it fails.
- **Modification**: Write a test that asserts the new expected behavior and verify it fails against the current code.

Run the failing test to confirm:

```shell
dotnet test --project tests/FeatherQR.Tests --treenode-filter /*/*/YourTestClass/YourTestMethod*
```

### 2. Implement (Green)

Write the minimum production code to make the failing test pass. Then run the test again to confirm it passes.

### 3. Run Full Test Suite

After the implementation passes targeted tests, run all tests to catch regressions:

```shell
dotnet test
```

All tests must pass on both target frameworks (`net8.0` and `net10.0`) before proceeding.

### 4. Add Regression Tests

For bug fixes, the test written in Step 1 often doubles as the regression test. If Step 1 already covers the fix scenario, you do not need a separate test, but verify it matches the pattern below. For new features, add edge-case tests beyond the initial happy-path test from Step 1.

**When fixing classification or heuristic logic bugs**: Step 1 writes the single failing test that reproduces the bug. After the fix passes (Step 2), add the remaining equivalence-class tests HERE in Step 4, not in Step 1. This keeps the red-green cycle tight while still achieving full class coverage.

Regression test patterns by change type:

| Change type | Test pattern | Assertion |
|---|---|---|
| Encode/decode round-trip broken | `QRCodeDecoderRoundTripTest` method | `TryDecode` succeeds; decoded text matches |
| Decoder missed valid input (false negative) | `QRCodeDecoderZXingCrossTest` or `QRCodeDecoderImageTest` | `TryDecode` succeeds on known-good matrix/image |
| Decoder accepted invalid input (false positive) | Dedicated invalid-input test | `TryDecode` returns false or non-success status |
| SIMD/kernel optimization regression | `*ParityTest` | Optimized path matches naive/scalar reference byte-for-bit |
| Visual rendering regression | `QRCodeVisualCompatibilityTest` | Pixel hash matches golden in `testdata/pixels/` |
| Encoder capacity or version boundary | `QRCodeGeneratorVersionBoundaryTest` or `QRCodeGeneratorUnitTest` | Correct version selected or expected exception |
| Image builder / SVG output | `QRCodeImageBuilder*Test` | Expected colors, dimensions, or SVG structure |

### 5. Benchmark Verification

When changing hot-path encode, decode, image-render, or SIMD code, run benchmarks:

```shell
cd src/FeatherQR.Benchmark
dotnet run -c Release
```

Filter to the relevant benchmark class when bisecting:

```shell
dotnet run -c Release -- --filter "*SimpleEncode*"
dotnet run -c Release -- --filter "*QrCodeDecodeEndToEnd*"
dotnet run -c Release -- --filter "*QrCodeImageEndToEnd*"
```

Compare results against a baseline from the `main` branch (or the previous commit on your branch). If no prior baseline exists, run the benchmark on `main` first to establish one.

- **Mean**: must not increase by more than +10%
- **Allocated**: must not increase by more than +10%

Relevant benchmarks by change area:

| Changed area | Benchmark to check |
|---|---|
| `QRCodeGenerator`, binary encoders | `SimpleEncode`, `QrCodeEndToEnd` |
| `QRCodeDecoder`, image decoders | `QrCodeDecodeEndToEnd` |
| `QRCodeImageBuilder`, rendering | `QrCodeImageEndToEnd` |
| `QRCodeData` serialization | `SimpleSerialize` |

### 6. Update Specs

If the implementation changes observable behavior or adds new functionality, update the relevant specification:

- Symbology architecture, shared components, document index → `.github/docs/specs/qrcode-symbologies.md`
- Spec-to-code map (per symbology) → `.github/docs/specs/standardqr-spec-map.md` (later `microqr-*.md`, `rmqr-*.md`)
- Decoder design and scope → `.github/docs/specs/standardqr-decoder.md`
- Public API or migration notes → `docs/migration.md`
- Capacity tables → `docs/data-capacity.md`

Follow the [document-spec-policy](https://github.com/guitarrapc/FeatherQR/blob/main/.github/docs/docs_authoring_guidelines.md) rules: specs document **WHAT** and **WHY**, not step-by-step implementation HOW (that belongs in code comments).

## Test Project Layout

Single test assembly: `tests/FeatherQR.Tests`.

The library exposes `InternalsVisibleTo` for this assembly, so internal encode/decode components can be tested directly when the public API is not the right seam.

### Test class categories

| Suffix / pattern | Purpose | Examples |
|---|---|---|
| `*UnitTest` / `*UnitTests` | Single component, table-driven or property checks | `BitWriterUnitTests`, `GaloisFieldUnitTest`, `QRBinaryEncoderUnitTest` |
| `*ParityTest` | Optimized/SIMD path vs naive or scalar reference | `EccBinaryEncoderKernelParityTest`, `FinderPatternFinderParityTest`, `BinaryInterleaverParityTest` |
| `*RoundTripTest` | Encode → decode with library's own pipeline | `QRCodeDecoderRoundTripTest` |
| `*CrossTest` | Cross-validation against an independent library | `QRCodeDecoderZXingCrossTest` |
| `*CompatibilityTest` | Golden pixel or visual output stability | `QRCodeVisualCompatibilityTest` |
| `QRCodeDecoder*Test` | Decoder integration (image, perspective, robustness) | `QRCodeDecoderImageTest`, `QRCodeDecoderPerspectiveTest` |

**Parity tests are mandatory** when adding or changing SIMD kernels, pointer-arithmetic fast paths, or any optimization that has a simpler reference implementation. The reference must be independent of the production algorithm (see `BinaryInterleaverParityTest` for the pattern).

### Running tests (TUnit)

**This project uses TUnit. Always use `--treenode-filter`, do NOT use `dotnet test --filter` (that is xUnit/MSTest syntax and will not work).**

```shell
# Run all tests in a class
dotnet test --project tests/FeatherQR.Tests --treenode-filter /*/*/QRCodeDecoderRoundTripTest/*

# Run a single test method (prefix match)
dotnet test --project tests/FeatherQR.Tests --treenode-filter /*/*/QRCodeDecoderRoundTripTest/RoundTrip_Numeric*

# Run all parity tests in one class
dotnet test --project tests/FeatherQR.Tests --treenode-filter /*/*/EccBinaryEncoderKernelParityTest/*
```

Tests run on both `net8.0` and `net10.0`; a SIMD-only code path may pass on one TFM and exercise a different kernel on the other.

## Test Conventions

### Naming

- Class: `{Feature}UnitTest`, `{Feature}ParityTest`, or `{Feature}Test` (follow existing files in the same area)
- Method: `{Action}_{Context}_{ExpectedOutcome}` (e.g., `RoundTrip_Numeric`, `InterleaveCodewords_MatchesReference_AllVersionsAndLevels`, `Decode_ZXingEncoded_Utf8`)

### Data sources

- **`[Arguments(...)]`**: Inline parameterized cases for small inputs (encoding modes, ECC levels, short strings).
- **`[MethodDataSource(nameof(...))]`**: Enumerate version/level combinations or structural edge cases (see `BinaryInterleaverParityTest.AllVersionLevelCombinations`).
- **Golden pixels**: `testdata/pixels/`, SHA-256 hashes of rendered PNG output (`QRCodeVisualCompatibilityTest`). Regenerate only when the visual change is intentional; commit updated `.pixels` files with the PR.
- **Synthetic scenes**: Build in-test for decoder geometry (finder patterns, alignment grids) rather than checking in large binary fixtures.

### Assertions

Use TUnit async assertions in `async Task` test methods:

```csharp
await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue();
await Assert.That(decoded).IsEquivalentTo(content);
await Assert.That(actual).IsEquivalentTo(expected);
await Assert.That(info.Version).IsEqualTo(version);
await Assert.That(info.MaskPattern).IsBetween(0, 7);
```

Use `IsEqualTo` for scalars and strings; use `IsEquivalentTo` for collections and byte arrays.

### Line endings

Source and test files use CRLF on Windows checkout. Bulk `sed`/`perl` edits anchored on `\n` may silently fail against CRLF files, verify with `grep` after mechanical edits.

## Playground (`src/FeatherQR.Playground`)

There is **no automated Playground test project**. Playground changes touch the WASM host (`QrInterop`), `wwwroot/` UI, and SkiaSharp native relink on publish.

### Verification workflow

1. **Publish** (SkiaSharp native code links only on publish, not `dotnet build`):

```shell
dotnet workload install wasm-tools

# Fast inner loop (no AOT)
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Debug -p:PlaygroundSoftFingerprint=true -o publish/playground

# Production-like (AOT + trimming)
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Release -p:PlaygroundSoftFingerprint=true -o publish/playground
```

2. **Serve** `publish/playground/wwwroot` and manually verify encode, decode preview, benchmark panel, and share-link round-trip.
3. **CI**: The `build-playground` job in `.github/workflows/build.yaml` publishes Release without AOT on every PR, ensure it stays green.

Publish to a **clean output directory** each time; re-publishing into the same `-o` folder leaves stale fingerprinted assets and can break the build.

Do not pass `-r browser-wasm` on the CLI, it propagates to the multi-targeted library and requires the `wasm-tools-net8` workload.

## Test Design Guardrails

- Prefer black-box tests through the public API (`QRCodeGenerator`, `QRCodeDecoder`, `QRCodeImageBuilder`) for integration behavior.
- Use `InternalsVisibleTo` and internal types for unit/parity tests of encode/decode kernels where the public surface is too coarse, this is an established pattern in this repo.
- Do not use reflection to invoke private methods; test through named internal entry points (`TryFindScalar`, parity reference helpers) instead.
- Cross-validate decoder changes with ZXing.Net (`QRCodeDecoderZXingCrossTest`) when the bug is about accepting externally generated symbols.
- Round-trip tests (`QRCodeDecoderRoundTripTest`) are the primary guard for encoder+decoder consistency, extend them when adding encoding modes or ECI handling.
- Avoid tests whose only assertion is that a private helper returns a constant; test the observable matrix, decode result, or rendered output.

## Classification Logic: Equivalence Class Coverage

When implementing or modifying **classification/decision logic** (version selection, mask scoring, format-information decode, finder-pattern acceptance, decode-status routing, perspective tier gates), apply equivalence class partitioning to ensure both positive AND negative cases are covered.

### Mandatory Steps

1. **Enumerate input variables** that affect the decision (e.g., `moduleCount`, `eccLevel`, `damagedCodewords`, `finderCount`, `keystoneRatio`).
2. **Build a truth table** of variable combinations that make each branch true/false. Each combination is an equivalence class.
3. **Write at least one test per class**, with priority on:
   - Cases where the condition is true AND should be true (true positive)
   - Cases where the condition is true BUT should be false (false positive, **these are the most commonly missed**)
   - Cases where the condition is false AND should be false (true negative)
   - Cases where the condition is false BUT should be true (false negative)
4. **For decode acceptance rules**: include negative cases (inputs that must **not** decode successfully) equal to or greater in count than positive cases. False decodes erode trust more than false rejections.

### Example: Mask Selection Boundary

For mask evaluation that picks the lowest penalty among patterns 0–7:

| Condition | Expected | Test case |
|---|---|---|
| All masks valid, one clear winner | Selected mask = winner | Fixed matrix with known penalty spread |
| Two masks tie on penalty | Deterministic tie-break (lower index) | Constructed tie scenario |
| Single mask eligible (others invalid) | That mask selected | Edge matrix from `ModulePlacerMaskPackedParityTest` patterns |
| Invalid mask bits in format info | Decode fails or status ≠ Success | Corrupted format region |

The **false positive** rows, inputs where a naive heuristic would pick the wrong mask or accept corrupt format data, are the ones most likely to be missed.

### When to Apply

- Any `if`/`switch` with 3+ input variables affecting the decision
- Finder / alignment pattern heuristics and perspective mesh fallbacks
- ECC correction thresholds and "errors corrected" reporting
- Version auto-selection vs `requestedVersion` override
- **Bug fixes on classification logic**: even when fixing a single false positive/negative, build the full truth table first. This prevents the fix from introducing new false positives in adjacent equivalence classes.
