# Generator options, settable properties: research

Research behind [generator-options-settable-plan.md](../generator-options-settable-plan.md), so a later session can continue without measuring again. It is deleted with the plan. Code references are at `main` f4b74de (2026-10-10), .NET SDK 10.0.301.

The probes were scratch projects and are not committed. Each section says what was built, so a probe can be rebuilt when the real change is checked.

## Sources

| Source | What was read |
|---|---|
| Git history of this repository | 14817a58 (#375, 2026-08-29), which introduced the option structs. Tag 1.2.0. 2eb9d6a7 (#396, 2026-09-08), which added the option constructors and made `IconData` `init`-only. f15bbabe, which deleted the generator API options plan |
| `.github/docs/plans/generator-api-options-plan.md` at the parent of f15bbabe | The section "`readonly record struct`, passed by `in`" and every mention of `readonly`, `in`, `with` and equality. It has no mention of `init` or `set` accessors |
| [featherqr-2.0.0-plan.md](../featherqr-2.0.0-plan.md) | The Shape unification table, the Phase 3 entry ("the generator options structs keep `init` because a caller builds those") and the entry that added the constructors |
| [qrcode-symbologies.md](../../specs/qrcode-symbologies.md) | The paragraphs on option objects and on consumers below C# 9 |
| `tests/FeatherQR.Tests/Shared/TypeShapeTest.cs` | Every rule about option types |
| dotnet/runtime, `main` | `JsonWriterOptions.cs` and `JsonNodeOptions.cs`: both `public struct`, every property with `set`. `JsonWriterOptions` validates in its `MaxDepth`, `IndentCharacter`, `IndentSize` and `NewLine` setters. The API reference lists `JsonNodeOptions` from .NET 6 |
| dotnet/roslyn, `main` | `CSharpCompilationOptions.cs`: four constructors marked "BACKCOMPAT OVERLOAD", three of them hidden with `EditorBrowsable(Never)`, beside `With…` methods |

## History

| Type | 14817a58 (#375) | 1.2.0 | 2.0.0-preview.5 |
|---|---|---|---|
| `QRCodeGeneratorOptions` | `readonly record struct`, `init`, no `set` | the same | the same, with a constructor since #396 |
| `MicroQRCodeGeneratorOptions` | `readonly record struct`, `init`, no `set` | the same | the same, with a constructor since #396 |
| `RmQRCodeGeneratorOptions` | `readonly record struct`, `init`, no `set` | the same | the same, with a constructor since #396 |
| `IconData` | not checked | six properties, each `{ get; set; }` | `sealed record class`, `init`, a constructor (#396) |

The constructors are in `2.0.0-preview.3`, `2.0.0-preview.4` and `2.0.0-preview.5`.

## Measurements

### A second constructor

Three single-file programs, each a struct with two constructors and a few calls.

| Shape | Result |
|---|---|
| Two constructors, all parameters optional, the second with one more | `new Opts(eci: 26)` is CS0121, ambiguous |
| The second takes the new option as a mandatory first parameter | Every call binds. A call without the new option reaches the old constructor |
| The old signature without default values, the new one all optional | Every call binds. A call that passes every old parameter reaches the old constructor, and every other call the new one |

### A consumer at C# 7.3

A library (netstandard2.0, C# 14) with one `readonly record struct` with `init` and one `record struct` with `set`, and a consumer (netstandard2.0, `LangVersion` 7.3).

- `new SetOpts { A = 1, Q = 0 }` and a later `s.A = 2` compile.
- `new InitOpts { A = 1 }` is CS8370.

### Writes to a copy

Compiled in the same consumer. Every line is an error.

| Write through | Error |
|---|---|
| A property's value | CS1612 |
| A `List<T>` element | CS1612 |
| A `Dictionary` value | CS1612 |
| A `Nullable<T>` value | CS1612 |
| A method result | CS1612 |
| An unboxed value | CS0445 |
| An `in` parameter | CS8332 |
| A `ref readonly` local | CS0131 |
| A `foreach` variable | CS1654 |
| A `readonly` instance field | CS1648 |
| A `static readonly` field | CS1650 |
| A nested object initializer | CS1918 |

A local, an array element and a writable field are assigned in place.

### Binary compatibility

One library built twice, with `init` and with `set`, and an application compiled against each build and run against the other without recompiling. Both crossings fail with `MissingMethodException: Method not found: 'Void Lib.Opts.set_A(Int32)'`.

### Method bodies

Three copies of `src/FeatherQR` and `src/FeatherQR.SkiaSharp`, built in Release for every target framework:

- base: unchanged.
- set: the three structs as `record struct`, `set` accessors, backing fields not `readonly`, `readonly` on the five hand-written getters.
- plain: as set, without `readonly` on those getters.

The constructors were kept in all three, so that method numbers stay equal and raw bytes can be compared. The comparer reads each assembly with System.Reflection.Metadata, keys a method by type, name and position among its overloads, and compares the IL bytes and the number of locals.

| Assembly | Target framework | Bodies | set differs from base | plain differs from base |
|---|---|---|---|---|
| `FeatherQR` | netstandard2.0 | 1,808 | 0 | 24 |
| `FeatherQR` | netstandard2.1 | 1,761 | 0 | 24 |
| `FeatherQR` | net8.0 | 2,033 | 0 | 24 |
| `FeatherQR` | net10.0 | 2,048 | 0 | 24 |
| `FeatherQR.SkiaSharp` | netstandard2.0 | 384 | 1 | 1 |
| `FeatherQR.SkiaSharp` | netstandard2.1 | 339 | 1 | 1 |
| `FeatherQR.SkiaSharp` | net8.0 | 293 | 0 | 0 |
| `FeatherQR.SkiaSharp` | net10.0 | 287 | 0 | 0 |

The one method in `FeatherQR.SkiaSharp` is `SymbolRenderer.ValidateIcon`, 363 bytes and 7 locals on both sides. Two `box System.Int64` instructions name type reference 0x0100007D in base and 0x0100007C in the others. The base build has 130 type references, one of them `IsExternalInit` from `FeatherQR` at row 0x71, which the `init` accessors' signatures need. The other builds have 129, so every later row moves up by one.

The 24 methods of the plain variant, each with one more local:

- `QRCodeGenerator`: both `Create` overloads, `TryGetRequiredBufferSize`, `CreateStructuredAppend`, `CreateKanjiSet`, `CreateResolved`, `CreateResolvedTo`, `ResolveConfiguration`, `CreateOptimal`, `CreateOptimalPlanned`, `CreateOptimalTo`, `CreateOptimalToPlanned`.
- `MicroQRCodeGenerator`: both `Create` overloads, `TryGetRequiredBufferSizeRanged`, `CreateResolved`, `CreateResolvedTo`, `ResolveConfiguration`, `CreateOptimal`, `CreateOptimalTo`, `TryGetRequiredBufferSizeOptimal`.
- `RmQRCodeGenerator`: both `Create` overloads and `TryGetRequiredBufferSize`.

The real change removes the constructors, which renumbers the methods after them. Its comparison is made on the commit before they go, or with tokens resolved to names.

### Which members are `readonly`

Read from the set build of `QRCodeGeneratorOptions` (net10.0). The struct is not `readonly`. The eight getters, `ToString`, `PrintMembers`, `GetHashCode` and both `Equals` are. The eight setters are not.

### The shape rule, prototyped

A file-based program with the reflection calls a `TypeShapeTest` case would use. It lists the exported structs that are not `readonly` and, on each, the instance methods that are neither a property's setter nor `readonly`. A fourth copy, fault, is set with four members added to `QRCodeGeneratorOptions`: a method, a computed property and a get/set pair without `readonly`, and one auto-property.

| Variant | Structs that are not `readonly` | Members reported |
|---|---|---|
| base | none | none |
| set | the three option structs | none |
| plain | the three | 11: the five getters, and `ToString` and `PrintMembers` of each struct |
| fault | the three | 5: the planted method, computed property and pair getter, and `ToString` and `PrintMembers` of that struct. The planted auto-property is not reported |

### IDE0251 as a build error

`dotnet_diagnostic.IDE0251.severity = error` for `*GeneratorOptions.cs`, built with `EnforceCodeStyleInBuild=true` (`FeatherQR`, net10.0).

| Variant | IDE0251 errors |
|---|---|
| set | 0 |
| plain | 5, one on each hand-written getter |
| fault | 3, on the planted method, computed property and pair |

### IDE0005 under build enforcement

With `EnforceCodeStyleInBuild=true` and no other change, the set variant builds both projects for every target framework with 36 warnings, all IDE0005 and all in `FeatherQR`. Without it the build has none. The 36 are 17 `using` lines, each reported by net8.0 and net10.0, and one of them by the two netstandard builds as well.

IDE0005 reports one diagnostic for a run of unused `using` lines, so a fifth copy, clean, had the reported lines removed and was built again until none was left: 17 lines, then 8, then 2. That is 27 lines in 13 files, all under `Internals`.

| File | Lines |
|---|---|
| `ImageDecoders/FinderPatternFinder.cs` | 4 |
| `RmQR/RmQRImageDecoder.cs` | 4 |
| `StandardQR/AlignmentPatternFinder.cs` | 3 |
| `ImageDecoders/Binarizer.cs` | 2 |
| `ImageDecoders/LocalBinarizer.cs` | 2 |
| `ImageDecoders/PerspectiveGridSampler.Vector256.cs` | 2 |
| `StandardQR/StructuredAppendPlanner.Parity.cs` | 2 |
| `StandardQR/StructuredAppendScanner.cs` | 2 |
| `TextAnalyzer.cs` | 2 |
| `MicroQR/MicroQRModulePlacer.PlaceSymbol.cs` | 1 |
| `ModeSegmenter.Lanes.cs` | 1 |
| `ModeSegmenter.Lanes.Vector256.cs` | 1 |
| `TextAnalyzer.X86.cs` | 1 |

After the removal both projects build on every target framework with no error and no warning, so none of the 27 was needed on a netstandard build either. `Binarizer.cs` and `LocalBinarizer.cs` are each left with an empty `#if NET8_0_OR_GREATER` block at the top, to delete by hand.

### IDE0251 over both projects

In the clean copy, IDE0251 was set to an error for `[*.cs]`, which there is every source file of the two projects. Beyond the option structs it reported four members, then two more once those were `readonly`, and then none.

| Type | Member |
|---|---|
| `BitReader` (`internal ref struct`) | `BitPosition`, `HasBits` |
| `BitWriter` (`internal ref struct`) | `BitPosition`, `ByteCount` |
| `QRBinaryEncoder` (`internal ref partial struct`) | `BitPosition`, `ByteCount`, which forward to `BitWriter`'s |

With the 27 lines removed and these six members `readonly`, every method body of both assemblies on all four target frameworks equals the set variant.

### The whole solution

A copy of the tracked tree, 20 projects, built with `EnforceCodeStyleInBuild=true` on every project.

- IDE0251, set to a warning for `[*.cs]`, reports five members: the four of `BitReader` and `BitWriter` above, and `RecordingAttempt.Decode`, a method of a private struct in `RegionalRetryTest`. The cascade was not followed in the tests.
- The file-based tools under `tools/` are outside the solution. They declare three structs, all `readonly`, so the rule has nothing to report there.
- IDE0005 needs `GenerateDocumentationFile`. Each of the 17 projects without it warns `EnableGenerateDocumentationFile`, and `FeatherQR.AotAnalysis`, which treats warnings as errors, then fails to build.
- IDE0052 reports `Version1` to `Version4` of `SimpleEncode` in the benchmark project.

### A consumer reading through read-only locations

The C# 7.3 consumer and the same source compiled at the SDK default read two getters through a `static readonly` field and through an `in` parameter, for both struct kinds. Each method has no local and the same length for both kinds at both language versions (22 bytes through the field, 14 through the parameter). So the current compiler does not copy a struct with `readonly` getters, whatever `LangVersion` says.

## Code inventory

- [QRCodeGeneratorOptions](../../../../src/FeatherQR/QRCodeGeneratorOptions.cs), [MicroQRCodeGeneratorOptions](../../../../src/FeatherQR/MicroQRCodeGeneratorOptions.cs), [RmQRCodeGeneratorOptions](../../../../src/FeatherQR/RmQRCodeGeneratorOptions.cs): 8, 5 and 7 options. Hand-written getters are `QuietZoneSize` on all three and `MaskPattern` on the first two. Backing fields are `_quietZoneSizeOffset` on all three and `_maskPattern` on the first two. Each has one constructor, whose XML docs give the C# 9 reason and tell callers to pass arguments by name.
- The generators read the options through `in` and need no change. `FeatherQR.SkiaSharp` builds option values with object initializers in the three image builders and reads `default(RmQRCodeGeneratorOptions).QuietZoneSize`.
- Calls to an option constructor: 16 in [TypeShapeTest](../../../../tests/FeatherQR.Tests/Shared/TypeShapeTest.cs), 6 in `tools/decode_figures.cs`, 1 in `docs/migration.md`.
- `TypeShapeTest` rules that involve the option structs:
  - `EverySettableProperty_IsReachableWithoutAnInitSetter` sweeps every property with a public setter, `init` or not. It has to sweep `init`-only properties alone.
  - `SettingsObject_TakesOneConstructorOfOptionalParameters` runs on the three structs and `IconData`. Only `IconData` keeps a constructor.
  - `GeneratorOptions_ConstructorAgreesWithTheObjectInitializer` compares constructor and initializer for all four types. The `IconData` half stays.
  - `OptionTypes_CanBeVariedWithWith` stays as it is.
  - `IconData_PropertiesAreInitOnly` stays as it is.
- The approved listing is checked by `tools/check_public_api.cs` in the build workflow. The Playground API page is written by `tools/public_api.cs`, and only the release job passes `--source-links`.
- The encode benchmark classes are `SimpleEncode`, `QRCodeEncodeEndToEnd`, `QRCodeSegmentationEncode`, `QRCodeQuietZone0Encode` and `QRCodeStructuredAppendEncode`, with Micro QR and rMQR classes for the first four kinds.

## Not measured

- Benchmarks. The method bodies are equal, so no difference is expected, and the real change measures it.
- A compiler from before C# 8, which does not know `readonly` members and would copy the struct in the consumer's own code.
- IDE0251 in an editor without `EnforceCodeStyleInBuild`.
- The build workflow and the Playground publish with code style enforced on the two shipping projects.
- The `field` keyword for `MaskPattern`, which would leave `QuietZoneSize` as the only hand-written getter.
