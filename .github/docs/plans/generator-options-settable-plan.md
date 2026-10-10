# Generator options: settable properties, no constructor

## Purpose

This plan changes the three generator option structs (`QRCodeGeneratorOptions`, `MicroQRCodeGeneratorOptions`, `RmQRCodeGeneratorOptions`) from `init` accessors to `set` accessors and removes the constructor each has for compilers before C# 9. It is Phase 3c of the [2.0.0 plan](featherqr-2.0.0-plan.md) (D13, decided 2026-10-10) and ships as its own pull request before the FNC1 work. It has to land before the 2.0.0 freeze, because `init` and `set` cannot be exchanged afterwards without breaking every compiled caller.

The reason is what happens when an option is added after 2.0.0. With `init`, a caller at C# 7.3 or 8.0 cannot write an object initializer, so each struct carries a constructor that lists every option, and a shape test requires it. A new option then changes that constructor, which breaks compiled callers, or adds a second one, which makes existing calls ambiguous. With `set`, the object initializer compiles at every language version, no constructor is needed, and a new option is one new property. [fnc1-support-plan.md](fnc1-support-plan.md) plans the first such option, and its statement that the option is added without a break holds only with this change.

The measurements and the code inventory behind the plan are in [references/generator-options-settable-research.md](references/generator-options-settable-research.md).

## Where it stands (2026-10-10, `main` at f4b74de)

- The three option structs are `readonly record struct`s with `init` accessors. They hold 8, 5 and 7 options.
- Five getters are hand-written: `QuietZoneSize` on all three, which stores an offset so that `default` carries the specified quiet zone, and `MaskPattern` on Standard QR and Micro QR, whose accessor validates.
- Each struct has one public constructor whose parameters are all optional, added in #396 for callers below C# 9. It is in `2.0.0-preview.3` to `2.0.0-preview.5` and in no stable release.
- `TypeShapeTest` requires that constructor. Every settable property of every exported type must be a parameter of one constructor, and each of the three structs and `IconData` must have exactly one constructor with parameters, all optional unless the member is `required`.
- The generators take the options by `in`. 24 of their methods call a hand-written getter through that reference.
- `IconData`, a `sealed record class`, has `init` accessors and a constructor for the same callers.
- 1.2.0 is the only stable release with the option structs. It had them with `init` and without the constructor.
- No project enforces code style when it builds. `.editorconfig` sets IDE0005, IDE0051 and IDE0052 to warnings, which only an editor reports today.

## What the investigation found

### The option structs never had `set`

They were introduced with `init` in #375 (2026-08-29, released in 1.2.0). The type that went from `set` to `init` between 1.2.0 and the 2.0.0 previews is `IconData`, in #396. Its reason is recorded and is about a class: a settable `IconData` let a caller reach a state the factory methods validate against, and a builder holds the same instance the caller does.

### No record weighs `init` against `set` for the structs

The generator API options plan (deleted in f15bbabe, read from history) argues three things. A struct instead of a class keeps an allocation out of the span overloads. A record gives `with`, value equality and `ToString`. `default` has to be the complete default configuration. `init` came with `readonly record struct` and is not argued. The 2.0.0 plan's "the generator options structs keep `init` because a caller builds those" contrasts them with the get-only result values. When the C# 9 gap was found, the fix was the constructor, and `set` was not considered.

### On a record struct, `init` guards almost nothing

`with` is public on these types, so `o.P = v` does nothing that `o = o with { P = v }` cannot already do. The reachable states are the same. So is who can change a variable, because a struct is copied when it is passed or stored and no other code holds a reference to the caller's value. What `init` adds is the type-level `readonly`, the compiler's guarantee that reading the struct through an `in` parameter or a `readonly` field never copies it first.

### A setter does not write to a copy unnoticed

The usual objection to a settable struct is a write that lands on a copy. For a property setter the compiler refuses each such write. Eleven spellings were compiled in a C# 7.3 consumer project and all eleven are errors:

- a property's value, a method result, an unboxed value
- a `List<T>` element, a `Dictionary` value, a `Nullable<T>` value
- an `in` parameter, a `ref readonly` local, a `foreach` variable, a `readonly` field
- a nested object initializer

Assigning through a local, an array element or a writable field compiles and changes that variable.

### The `readonly` guarantee can be held by a rule

The compiler copies a struct only when a member that is not `readonly` is called on a read-only location. So the guarantee is kept when every instance member except a property setter is `readonly`. Auto-property getters and the members a record struct generates are `readonly` without anyone writing it, which leaves the five hand-written getters.

- With `set` accessors and `readonly` on those five getters, every method body of `FeatherQR` and `FeatherQR.SkiaSharp` is unchanged on all four target frameworks. The one difference is a type reference number in one method of the two netstandard builds of `FeatherQR.SkiaSharp`.
- Without `readonly` on the five getters, 24 generator methods gain a copy on every target framework. The generated `ToString` and `PrintMembers` of the three structs stop being `readonly` too.
- A reflection rule (on every exported struct that is not `readonly`, every instance member except a setter is `readonly`) passed on the correct build. It reported each of the five getters when their `readonly` was removed, and a planted method, computed property and get/set pair. It passed a planted auto-property.
- IDE0251 set to an error rejects the same faults at build time and reports nothing on the correct build. It needs `EnforceCodeStyleInBuild`, which also brings the existing IDE0005 warnings into the build: 27 unused `using` lines in 13 files of `FeatherQR`. Removing them changes no method body.
- Set to an error for every source file of the two shipping projects, IDE0251 asks for `readonly` on six more members, all on the internal `BitReader`, `BitWriter` and `QRBinaryEncoder`. Adding it changes no method body either. Across the whole solution it names one more, a method of a private struct in the tests.
- Code style enforced in the build of the whole solution is a different matter from this rule. IDE0005 cannot run in the 17 projects that generate no documentation file. Each of them warns about that, and `FeatherQR.AotAnalysis` treats the warning as an error. IDE0052 reports four members of a benchmark class.
- A miss costs time and not output. The copy is of one option struct, and the getter returns the same value from it.

### `init` and `set` cannot be exchanged within a major version

An application compiled against one and run against the other fails with `MissingMethodException`, in both directions, because the accessor's signature differs. At the next major version, `init` to `set` leaves every source file compiling, and `set` to `init` breaks each assignment after construction. So `set` is the harder choice to take back, and 2.0.0 is the last point before 3.0.0 at which either can be chosen.

### An option added to the constructor breaks callers

- A parameter added to the existing constructor changes its signature, so an assembly compiled against the old one fails at run time.
- A second constructor whose parameters are all optional makes an existing `new QRCodeGeneratorOptions(eciMode: …)` ambiguous (CS0121).
- A second constructor that takes the new option as a mandatory first parameter binds, and breaks the parameter order the three structs share.
- The old signature kept without its default values, beside a new all-optional one, binds and keeps old binaries working. It leaves one constructor behind for every release that adds an option. Roslyn's `CSharpCompilationOptions` has this shape and carries four of them.

### Precedent

`JsonWriterOptions` and `JsonNodeOptions` in System.Text.Json are `public struct`s with `set` accessors, and `JsonWriterOptions` validates in its setters. `JsonNodeOptions` was added in .NET 6, after `init` existed.

## Scope

| In | Out |
|---|---|
| The three option structs become `record struct`s with `set` accessors, and their five hand-written getters are `readonly` | `IconData`, which keeps `init` and its constructor. It is a class a builder holds by reference, and its `init` has a recorded reason |
| Their three constructors are removed | `GradientOptions`, `ModuleRect` and the result values, which keep their shapes |
| `TypeShapeTest`: the constructor rules narrowed to `init`-only members, and two new rules for structs that are not `readonly` | The FNC1 option, which [fnc1-support-plan.md](fnc1-support-plan.md) adds on top of this |
| IDE0251 an error for every C# file, enforced in the build of the two shipping projects: `EnforceCodeStyleInBuild` there, the 27 unused `using` lines it brings up removed, `readonly` on the seven other members the rule names | A `With…` method for each option. It was considered and is not needed once `set` compiles everywhere |
| The approved API listing, the Playground API page, `tools/decode_figures.cs`, the XML docs, the migration guide, the README and the spec | Code style enforced in the build of the test, tool, benchmark and sample projects |

## What has to stay true

- Every symbol is bit-identical. The golden and fixture tests pass unchanged.
- No method body changes while the accessors change. The comparison of the investigation is repeated on the real change, on both assemblies and four target frameworks.
- The encode benchmarks stay within noise and the zero-allocation tests pass.
- `default` is still the complete default configuration. An option set written with its defaults named still equals `default`.
- `MaskPattern` is validated on assignment by every route: initializer, later assignment and `with`.
- Every object initializer and `with` expression that compiles today compiles unchanged.
- The three option structs are the only exported structs that are not `readonly`.
- The two shipping projects build without a warning on every target framework, with code style enforced.

## Decisions

| # | Decision | Status |
|---|---|---|
| 1 | `set` accessors on the three option structs, as its own pull request before FNC1 | Decided 2026-10-10 by the maintainer. What `init` guaranteed is kept by decision 3, and what it cost is the constructor and a break with every later option |
| 2 | The constructors are removed, not kept beside the setters | Decided 2026-10-10. Kept, each would still have to grow with every new option, which is the break this plan removes. They shipped only in previews |
| 3 | Two `TypeShapeTest` rules hold the `readonly` guarantee. On an exported struct that is not `readonly`, no instance member but a setter lacks `readonly`. The three option structs are the only such structs | Decided 2026-10-10. The first rule is a sweep, so a later type is covered. The second makes a new settable struct a decision instead of an accident |
| 4 | IDE0251 as a build error | Decided 2026-10-10: it is part of this plan and comes before the accessors, so that a getter without `readonly` fails the build before any test runs. The rule is an error for every C# file of the repository, so an editor shows it everywhere and no list of files has to be kept up. The build enforces it on the two shipping projects. That costs `readonly` on six internal members, with no method body changed, and on one member in the tests. Code style enforced in the build of the whole solution is left out, because what it adds is IDE0005 and IDE0052 in the other projects and not this rule |
| 5 | `IconData` keeps `init` and its constructor | Decided 2026-10-10. An `IconData` option added after 2.0.0 meets the constructor problem above, and then takes the form that keeps the old signature without its default values |
| 6 | The spec's rule for option objects splits by sharing. A reference type the library holds is immutable once built. An option struct is a plain value a caller may assign to | Decided 2026-10-10. It replaces "an option object a caller builds is immutable once built" in [qrcode-symbologies.md](../specs/qrcode-symbologies.md) |

## Equivalence classes

The shape rule:

- Struct kind: `readonly`, where the rule does not apply, and not `readonly`.
- Member kind on a struct that is not `readonly`: auto-property getter, hand-written getter, computed property, method, setter, generated record member. Each of the first four with and without `readonly` where the language allows both.
- The negative cases are planted faults: `readonly` removed from each of the five getters in turn, and a method, a computed property and a get/set pair added without it. Each must fail the rule. A new auto-property must pass.

Assignment:

- Route: object initializer, assignment after construction, `with`.
- Value: the default left unnamed, the default named, a value that is not the default. For `MaskPattern`, both sides of each bound and `null`.
- Every option of every struct.
- A `QuietZoneSize` assigned its specified default equals `default`.

Consumer compilation, measured from a consumer project against the netstandard2.0 asset:

- Language version 7.3 and the SDK default.
- An initializer and a later assignment compile at both.

## Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | This plan, its research file, D13 and the Phase 3c row of the 2.0.0 plan, the index | Done 2026-10-10 |
| 1 | Build enforcement: IDE0251 an error for every C# file, `EnforceCodeStyleInBuild` on the two shipping projects, the 27 unused `using` lines removed, `readonly` on the seven members the rule names | Done 2026-10-10. The two projects build without a warning on four target frameworks, and the solution builds as before. Every method body of both assemblies equals the commit before. It changes no API, so it can merge on its own |
| 2 | Tests first: the assignment classes, the rule that names the three structs, and the sweep | Done 2026-10-10. The first two fail against today's structs for the stated reason (no setter, no struct that is not `readonly`). The sweep has nothing to check yet |
| 3 | The accessors: `record struct`, `set`, `readonly` on the five getters. The constructors stay for this step, so that method bodies can be compared byte for byte | Phase 2's tests pass. Every method body of both assemblies equals the commit before, on four target frameworks. Each planted fault is refused twice: the build fails with IDE0251, and with code-style enforcement off for the run a shape rule fails, through `tools/mutation_check.cs`. Encode benchmarks within noise |
| 4 | The constructors removed. `TypeShapeTest`'s constructor rules narrowed to `init`-only members, which leaves `IconData`. `tools/decode_figures.cs` moved to initializers. The approved API listing and the Playground API page | The full suite passes on both target frameworks. A consumer project at C# 7.3 sets every option. The listing diff is the accessors, the struct kind and the three constructors |
| 5 | The documents below, then this plan folded into the specs and deleted with its research file | D13 and Phase 3c of the 2.0.0 plan marked done |

## Documents to change

- [migration.md](../../../docs/migration.md): in "Older language versions", the initializer column (the option structs work at every language version, `IconData` still needs C# 9 or its constructor) and the sample, which uses an option constructor. In "Results, options and sealing", the sentence that calls the option objects immutable.
- The root README: the paragraph that explains `with` by calling the options `readonly record struct`.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the paragraph on the constructor for consumers below C# 9, and the rule for option objects (decision 6), with the history and the measurements of this plan. It also gains the build rule and its reason.
- `.editorconfig` and the two shipping project files, for the build rule.
- The XML docs of the three option types, which describe the constructor and name `init`.
- `PublicAPI.approved.txt` of `FeatherQR`, and the committed Playground API page, regenerated without source links.
- [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md): D13 and the Phase 3c row marked done, with a Progress log entry.
- [fnc1-support-plan.md](fnc1-support-plan.md): decision 1 points here for its "without a break".
- The [documentation index](../README.md).

## Progress log

### Investigation and decision (2026-10-10)

Done: the history of the option structs and of `IconData` was read from git, with the deleted generator API options plan. Measured in scratch projects:

- how a second constructor binds
- what a C# 7.3 consumer can write, and whether it copies a struct it reads through a read-only location
- which writes to a copy compile
- whether `init` and `set` are binary compatible
- every method body of both assemblies on four target frameworks, under three variants
- which members of a settable record struct are `readonly`
- a prototype of the shape rule against planted faults
- IDE0251 as a build error

The maintainer decided decisions 1, 2, 3 and 5. The 2.0.0 plan (D13, Phase 3c), the FNC1 plan (decision 1) and the index were updated in the same change. No code changed.

Lessons:

- A remembered decision is a claim to check. The change remembered as the option structs going from `set` to `init` was `IconData`'s, and the structs' `init` had no recorded reason to weigh.
- "Added without a break" has to be checked on every route to a member. The property was additive, and the constructor a shape test demands was not.
- On a record struct, `with` already does what a setter does, so `init` protected less than it cost. The cost was visible only from a consumer's compiler.
- One getter without `readonly` also takes `readonly` off the generated `ToString` and `PrintMembers`, so the rule reports more members than there are mistakes.
- Raw method bytes compare only while the metadata tables keep their rows. The measured variants kept the constructors for that reason, and one type reference row still moved in the other assembly.

### Decisions 2 to 6 (2026-10-10)

Done: the maintainer decided the remaining decisions. The constructors go. Two shape rules hold the guarantee. `IconData` stays as it is. The spec's rule splits by sharing. IDE0251 becomes a build error inside this plan, which added Phase 1. Two more measurements followed, both in the research file:

- The IDE0005 warnings that build enforcement brings up are 27 `using` lines in 13 files, and removing them changes no method body.
- IDE0251 over every source file of the two shipping projects names six more members, and `readonly` on them changes no method body.
- Across the whole solution the rule names one more member, in the tests. The maintainer accepted either scope. The rule is set for every C# file, and the build enforces it on the two shipping projects, because code style in every project's build brings IDE0005 and IDE0052 work that has nothing to do with the option structs.

No code changed.

Lessons:

- IDE0005 reports one diagnostic for a run of unused `using` lines, so 17 reported lines were 27. The count came from removing the reported lines and building again until none was left.
- IDE0251 cascades. A member that only reads a property becomes eligible once that property is `readonly`, so four reported members were six.

### Phase 1, build enforcement (2026-10-10)

Done. IDE0251 is an error in `.editorconfig`, and `FeatherQR` and `FeatherQR.SkiaSharp` enforce code style when they build. The 27 unused `using` lines are gone from 13 files, with the two conditional blocks that had nothing left in them. Seven members are `readonly`: `BitPosition` and `HasBits` of `BitReader`, `BitPosition` and `ByteCount` of `BitWriter` and of `QRBinaryEncoder`, and `Decode` of a private struct in `RegionalRetryTest`.

- Red first. With the rule on and nothing else changed, the build failed with IDE0251 on the four `BitReader` and `BitWriter` members and gave 36 IDE0005 warnings.
- The two projects build without a warning on four target frameworks. The solution builds with the 15 warnings it had before, none of them from a code-style rule.
- Every method body of both assemblies equals the commit before, on four target frameworks.
- The approved API listings are unchanged.
- In a one-off build with code style forced on for every project, IDE0251 reports nothing in the solution.
- The full suite passes: 38,080 tests, both target frameworks.

Benchmarks: not run, no method body changed.

### Phase 2, tests first (2026-10-10)

Done. `TypeShapeTest` has three new rules and `GeneratorOptionsAssignmentTest` is new. All of it is red or idle against today's structs, for the reasons the plan gives.

- `GeneratorOptions_AreTheOnlyStructsACallerAssignsTo` fails. The sweep finds no struct that is not `readonly` and expects the three.
- `GeneratorOptions_EveryOptionHasAPlainSetter` fails on each of the three structs, because every option is `init`-only. The plan did not list this rule. It states decision 1 for every option, so that one added later with `init` fails without a case of its own.
- `SettableStruct_EveryMemberButASetterIsReadOnly` passes with nothing to check.
- `GeneratorOptionsAssignmentTest` does not compile: CS8852 at each of its 49 assignments, and no other error. It writes each of the 20 options by assignment, by initializer and by `with`, once with a value and once with the default named. It assigns every option in turn in both orders, puts `MaskPattern` on both sides of each bound by every route, and checks that an assignment reaches one variable and no copy of it.

The test project does not build until Phase 3.
