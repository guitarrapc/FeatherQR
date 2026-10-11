using TUnit.Assertions.Enums;
using System.Reflection;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The type shapes 2.0.0 unified: one value kind for every sizing and decode result,
/// option objects that are immutable where a builder keeps the caller's instance and
/// plain settable values where the library keeps nothing after a call, and no extension
/// point that was not designed as one.
/// </summary>
/// <remarks>
/// Shape tests, not behaviour tests. Each of these was inconsistent across the three
/// symbologies before 2.0.0 — Standard QR's sizing result was a <c>record struct</c> with
/// a public constructor while the other two were plain structs, and the drift was
/// invisible until someone read all three files side by side. A rule a test states is a
/// rule the next type has to follow.
/// </remarks>
public class TypeShapeTest
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

    // Func<Type> rather than Type: TUnit asks for a factory when a data source yields a
    // reference type, so each case builds its own value and cannot share state with another.
    //
    // The settings objects whose members are init-only, which is IconData alone. The three
    // generator options have set accessors and no constructor (GeneratorOptionStructs below).
    public static IEnumerable<Func<Type>> InitOnlySettingsObjects()
    {
        yield return () => typeof(IconData);
    }

    private static bool IsInitOnly(MethodInfo setter)
        => setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");

    // A property with a public init accessor, indexers aside. A plain set accessor is left
    // out on purpose, because an object initializer assigns it at C# 7.3 as well.
    private static PropertyInfo[] InitOnlyProperties(Type type) => type.GetProperties(Instance)
        .Where(p => p.SetMethod is { IsPublic: true } setter && IsInitOnly(setter) && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>
    /// Every property with a public <c>init</c> accessor, on every exported type but the
    /// three generator options, is also a parameter of one public constructor, and no
    /// exported type has a protected <c>init</c> accessor. A consumer whose compiler predates
    /// C# 9 cannot assign an <c>init</c> accessor, so a constructor parameter is the only
    /// route to it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sweep, not a list. The rule was first written over a hand-listed set of option
    /// structs and <see cref="IconData"/> was found missing from it a review round later,
    /// with its two factories both hardcoding <see cref="ImageIconShape"/> — so every other
    /// shape was unreachable below C# 9. A list only states the rule for the types someone
    /// remembered; the sweep states it for the next type too.
    /// </para>
    /// <para>
    /// The generator options were swept here until their constructors went. The
    /// constructor this rule made them carry had to list every option, so an option added
    /// in a later release would have changed its signature under every compiled caller.
    /// They are left out by name now: <see cref="GeneratorOptions_EveryOptionHasAPlainSetter"/>
    /// refuses an <c>init</c> accessor on one of them, and
    /// <see cref="GeneratorOptions_HaveNoConstructorOrFactoryThatListsTheOptions"/> the
    /// constructor this rule would ask for.
    /// </para>
    /// <para>
    /// A protected <c>init</c> accessor is refused outright and not matched to a constructor:
    /// a class deriving below C# 9 cannot assign one either.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryInitOnlyProperty_IsAlsoAConstructorParameter()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        var swept = new List<Type>();

        var protectedInit = ExportedTypes()
            .SelectMany(t => t.GetProperties(declared)
                .Where(p => p.SetMethod is { } setter && (setter.IsFamily || setter.IsFamilyOrAssembly) && IsInitOnly(setter))
                .Select(p => $"{t.FullName}.{p.Name}"));
        await Assert.That(protectedInit).IsEmpty()
            .Because("a class deriving below C# 9 cannot assign a protected init accessor");

        foreach (var type in ExportedTypes().Where(t => !GeneratorOptionStructTypes.Contains(t) && InitOnlyProperties(t).Length > 0))
        {
            swept.Add(type);

            // One constructor has to cover everything. Pooling parameters across all of
            // them would pass a type whose settings are split between two constructors,
            // where a caller picking either one still cannot set the rest.
            //
            // Match on name AND type: a parameter named `iconSizeModules` typed `int` rather
            // than `int?` would satisfy a name-only rule while making `null`, the real
            // default, unreachable, and would quietly change the default configuration.
            var initOnly = InitOnlyProperties(type);
            var covered = type.GetConstructors().Any(c =>
                initOnly.All(p => c.GetParameters().Any(a =>
                    string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase) && a.ParameterType == p.PropertyType)));

            if (!covered)
            {
                offenders.Add($"{type.FullName}: no single constructor sets [{string.Join(", ", initOnly.Select(p => p.Name))}]");
            }
        }

        await Assert.That(swept).Contains(typeof(IconData))
            .Because("the sweep has to find the type the rule was written for, or the rule passes without it");
        await Assert.That(offenders).IsEmpty()
            .Because("an init-only property no constructor can set is unreachable below C# 9");
    }

    /// <summary>
    /// A settings object whose members are <c>init</c>-only takes one constructor whose
    /// parameters are all optional, so setting one option never means restating the rest.
    /// Only a <c>required</c> member may be mandatory, because it alone has no default to
    /// fall back on.
    /// </summary>
    /// <remarks>
    /// Listed rather than swept, because it is an ergonomic rule about settings objects and
    /// not every constructible type is one: <see cref="ModuleRect"/> is a positional value
    /// whose four components are all meaningful, and <see cref="GradientOptions"/> cannot
    /// default its colours or express them as a property at all (they are read back as a
    /// <see cref="ReadOnlySpan{T}"/>). Reachability, the rule that actually protects the
    /// pre-C#-9 audience, is swept over both assemblies above and covers those two. The
    /// generator options are not listed: they have <c>set</c> accessors and, by
    /// <see cref="GeneratorOptions_HaveNoConstructorOrFactoryThatListsTheOptions"/>, no constructor.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(InitOnlySettingsObjects))]
    public async Task SettingsObject_TakesOneConstructorOfOptionalParameters(Type type)
    {
        var constructors = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .ToArray();
        await Assert.That(constructors.Length).IsEqualTo(1)
            .Because($"{type.Name} needs exactly one init-free way to set every option");

        var requiredNames = InitOnlyProperties(type)
            .Where(p => p.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute"))
            .Select(p => p.Name)
            .ToArray();
        var wronglyMandatory = constructors[0].GetParameters()
            .Where(p => !p.IsOptional && !requiredNames.Contains(p.Name!, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Name!)
            .ToArray();
        await Assert.That(wronglyMandatory).IsEmpty()
            .Because("only a required member may be a mandatory constructor parameter");
    }

    /// <summary>
    /// <see cref="IconData"/>'s constructor is a second spelling of its object initializer:
    /// for the same settings it produces an equal value, because it assigns through the same
    /// <c>init</c> accessors. The two differ on a null icon, which the constructor refuses
    /// and the initializer keeps.
    /// </summary>
    /// <remarks>
    /// The three generator options had the same check for their constructors, which went
    /// with those constructors. <see cref="GeneratorOptionsAssignmentTest"/> compares the
    /// routes that remain for them.
    /// </remarks>
    [Test]
    public async Task IconData_ConstructorAgreesWithTheObjectInitializer()
    {
        // IconData carries three `int` and two `int?` parameters, so every value here is
        // distinct and a crossing shows up as a wrong property rather than a wrong count.
        var shape = new ImageIconShape(new SKBitmap(8, 8));
        await Assert.That(new IconData(
                icon: shape,
                iconSizePercent: 15,
                iconBorderWidth: 3,
                iconSizeModules: 5,
                iconBorderModules: 1,
                maxCoreOccupancyPercent: 40))
            .IsEqualTo(new IconData
            {
                Icon = shape,
                IconSizePercent = 15,
                IconBorderWidth = 3,
                IconSizeModules = 5,
                IconBorderModules = 1,
                MaxCoreOccupancyPercent = 40,
            });
        // Omitted parameters have to match what the object initializer leaves behind.
        await Assert.That(new IconData(icon: shape)).IsEqualTo(new IconData { Icon = shape });

        // IconData's guard is the one place the two routes deliberately differ. The
        // constructor is reached from language versions with no nullable analysis, where
        // a null compiles silently, so it throws; the initializer needs `null!` to get
        // there at all and keeps its existing behaviour of drawing no icon.
        await Assert.That(() => new IconData(null!)).Throws<ArgumentNullException>();
        await Assert.That(new IconData { Icon = null! }.Icon).IsNull();
    }

    // Func<Type>, for the reason on InitOnlySettingsObjects.
    public static IEnumerable<Func<Type>> ResultValues()
    {
        yield return () => typeof(QRCodeCalculatedSize);
        yield return () => typeof(MicroQRCodeCalculatedSize);
        yield return () => typeof(RmQRCodeCalculatedSize);
        yield return () => typeof(QRCodeDecodeInfo);
        yield return () => typeof(MicroQRCodeDecodeInfo);
        yield return () => typeof(RmQRCodeDecodeInfo);
        yield return () => typeof(SymbolCorners);
        yield return () => typeof(ImagePoint);
        yield return () => typeof(QRStructuredAppend);
    }

    /// <summary>
    /// Every sizing and decode result is a <c>readonly struct</c> the library alone
    /// constructs: a caller receives one from a <c>Try</c> method and reads it, and there
    /// is no half-built instance to hand back in.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ResultValues))]
    public async Task ResultValue_IsAReadOnlyStructTheLibraryAloneBuilds(Type type)
    {
        await Assert.That(type.IsValueType).IsTrue().Because($"{type.Name} must be a struct");
        await Assert.That(type.GetCustomAttributes().Any(a => a.GetType().Name == "IsReadOnlyAttribute")).IsTrue()
            .Because($"{type.Name} must be readonly");
        await Assert.That(type.GetConstructors(Instance)).IsEmpty()
            .Because($"{type.Name} is produced by the library, so its constructor stays internal");

        foreach (var property in type.GetProperties(Instance))
        {
            await Assert.That(property.SetMethod).IsNull()
                .Because($"{type.Name}.{property.Name} must be get-only, which excludes init");
        }
    }

    /// <summary>
    /// Being library-built does not mean being opaque: every result value is a
    /// <c>record struct</c>, so it prints its members in a log line and compares without
    /// boxing. Getting there does not need a public constructor or <c>init</c> setters —
    /// the record only has to be declared without positional parameters.
    /// </summary>
    /// <remarks>
    /// The first cut of this rule demoted them to plain structs, which threw away
    /// <c>ToString</c>, <c>==</c> and <see cref="IEquatable{T}"/> for nothing: what had to
    /// go was the public constructor and the <c>init</c> setters, not the record.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(ResultValues))]
    public async Task ResultValue_ComparesAndPrintsByValue(Type type)
    {
        await Assert.That(typeof(IEquatable<>).MakeGenericType(type).IsAssignableFrom(type)).IsTrue()
            .Because($"{type.Name} must compare without boxing");
        await Assert.That(type.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType).IsEqualTo(type)
            .Because($"{type.Name} must print its own members, not the type name");
        await Assert.That(type.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static)).IsNotNull();
    }

    /// <summary>
    /// The same rule at the call site rather than through reflection: a sizing result and
    /// a decode result both name their members when logged.
    /// </summary>
    [Test]
    public async Task ResultValue_ToString_NamesItsMembers()
    {
        QRCodeGenerator.TryGetRequiredBufferSize("hello", QREccLevel.M, out var size);
        var sizeText = size.ToString();

        await Assert.That(sizeText).Contains("BufferSize");
        await Assert.That(sizeText).Contains("Version");

        QRCodeDecoder.TryDecode(QRCodeGenerator.Create("hello", QREccLevel.M), out _, out var info);
        var infoText = info.ToString();

        await Assert.That(infoText).Contains("Status");
        await Assert.That(infoText).Contains("EccLevel");
    }

    /// <summary>
    /// The three decode results report the same things under the same names, so code that
    /// reads one reads all three. rMQR is the one documented difference: ISO/IEC 23941
    /// defines a single data mask, so there is no mask pattern to report. Structured Append
    /// is the other: only Standard QR defines it, so only its result carries the header.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeDecodeInfo), true, true)]
    [Arguments(typeof(MicroQRCodeDecodeInfo), true, false)]
    [Arguments(typeof(RmQRCodeDecodeInfo), false, false)]
    public async Task DecodeInfo_ReportsTheSameMembers(Type type, bool hasMaskPattern, bool hasStructuredAppend)
    {
        var names = type.GetProperties(Instance).Select(p => p.Name).ToArray();

        await Assert.That(names).Contains("Status");
        await Assert.That(names).Contains("Version");
        await Assert.That(names).Contains("EccLevel");
        await Assert.That(names).Contains("ErrorsCorrected");
        await Assert.That(names).Contains("Corners");
        await Assert.That(names.Contains("MaskPattern")).IsEqualTo(hasMaskPattern);
        await Assert.That(names.Contains("StructuredAppend")).IsEqualTo(hasStructuredAppend);
        await Assert.That(names.Length).IsEqualTo(5 + (hasMaskPattern ? 1 : 0) + (hasStructuredAppend ? 1 : 0));

        // Status and Corners are the shared types on all three; version and ECC level are per-symbology.
        await Assert.That(type.GetProperty("Status")!.PropertyType).IsEqualTo(typeof(DecodeStatus));
        await Assert.That(type.GetProperty("Corners")!.PropertyType).IsEqualTo(typeof(SymbolCorners));
        if (hasStructuredAppend)
        {
            await Assert.That(type.GetProperty("StructuredAppend")!.PropertyType).IsEqualTo(typeof(QRStructuredAppend));
        }
    }

    /// <summary>
    /// Sizing results agree the same way, with rMQR rectangular where the square
    /// symbologies report one side length.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeCalculatedSize), "Size")]
    [Arguments(typeof(MicroQRCodeCalculatedSize), "Size")]
    [Arguments(typeof(RmQRCodeCalculatedSize), "Width")]
    public async Task CalculatedSize_ReportsBufferSizeVersionAndDimensions(Type type, string dimension)
    {
        var names = type.GetProperties(Instance).Select(p => p.Name).ToArray();

        await Assert.That(names).Contains("BufferSize");
        await Assert.That(names).Contains("Version");
        await Assert.That(names).Contains(dimension);
        await Assert.That(names).DoesNotContain("IsValid")
            .Because("whether the content fits is the bool TryGetRequiredBufferSize already returned");
    }

    /// <summary>
    /// A public class with no designed extension point is sealed. This sweeps both shipped
    /// assemblies rather than listing the types it knows about, so a new unsealed class is
    /// caught by the rule instead of slipping past a list nobody remembered to extend.
    /// The extension points are the three shape hierarchies (<see cref="ModuleShape"/>,
    /// <see cref="FinderPatternShape"/>, <see cref="IconShape"/>) and
    /// <see cref="SymbolImageBuilderBase{TSelf}"/>, which is open only to this package
    /// through its <c>private protected</c> constructor; the set is pinned here too, so
    /// adding a fifth is also a decision rather than an accident. Static classes read as
    /// sealed in metadata, so they need no carve-out.
    /// </summary>
    [Test]
    public async Task EveryExportedClass_IsSealedOrADeclaredExtensionPoint()
    {
        string[] extensionPoints =
        [
            "FeatherQR.SkiaSharp.FinderPatternShape",
            "FeatherQR.SkiaSharp.IconShape",
            "FeatherQR.SkiaSharp.ModuleShape",
            "FeatherQR.SkiaSharp.SymbolImageBuilderBase`1",
        ];

        var open = new[] { typeof(QRCodeData).Assembly, typeof(QRCodeImageBuilder).Assembly }
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsClass && !t.IsSealed)
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(open.Where(n => !extensionPoints.Contains(n))).IsEmpty()
            .Because("a public class with no designed extension point is sealed");
        await Assert.That(open).IsEquivalentTo(extensionPoints, CollectionOrdering.Matching)
            .Because("a new extension point is a design decision, so it is declared here");
    }

    /// <summary>
    /// The shape base classes stay open, and the image builder base stays closed to
    /// anyone outside this assembly.
    /// </summary>
    [Test]
    public async Task ShapeHierarchies_StayOpen_AndTheBuilderBaseDoesNot()
    {
        foreach (var open in new[] { typeof(ModuleShape), typeof(FinderPatternShape), typeof(IconShape) })
        {
            await Assert.That(open.IsAbstract).IsTrue();
            await Assert.That(open.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.IsFamily || c.IsFamilyOrAssembly)).IsTrue()
                .Because($"{open.Name} is an extension point, so it needs a protected constructor");
        }

        // Ask what the type HAS, not what a filtered query happens to contain: an earlier
        // version of this assertion ran All() over the non-public constructors, which a
        // public constructor empties, so the violation it names passed vacuously.
        var builderBase = typeof(SymbolImageBuilderBase<>);
        await Assert.That(builderBase.GetConstructors(BindingFlags.Public | BindingFlags.Instance)).IsEmpty()
            .Because("a public constructor on the builder base would open it to outside derivation");

        var hidden = builderBase.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(hidden).IsNotEmpty()
            .Because("the builder base still has to be constructible by the builders in this package");
        await Assert.That(hidden.All(c => c.IsFamilyAndAssembly)).IsTrue()
            .Because("the builder base is private protected: derivable here, not from outside");
    }

    /// <summary>
    /// <see cref="GradientOptions"/> copies the arrays it is given. Before 2.0.0 it stored
    /// the caller's array, so mutating that array afterwards silently changed the options,
    /// and <see cref="GradientOptions.Default"/> could be repainted for the whole process.
    /// </summary>
    [Test]
    public async Task GradientOptions_CopiesTheArraysItIsGiven()
    {
        var colors = new[] { SKColors.Red, SKColors.Blue };
        var positions = new[] { 0f, 1f };
        var options = new GradientOptions(colors, GradientDirection.TopToBottom, positions);

        colors[0] = SKColors.Lime;
        positions[0] = 0.5f;

        await Assert.That(options.Colors[0]).IsEqualTo(SKColors.Red);
        await Assert.That(options.ColorPositions[0]).IsEqualTo(0f);

        // No positions is an empty span rather than null: "evenly distributed".
        await Assert.That(new GradientOptions([SKColors.Red, SKColors.Blue]).ColorPositions.IsEmpty).IsTrue();
    }

    /// <summary>
    /// The constructor takes the shapes the properties hand back, so an existing gradient
    /// rebuilds with one member changed and nothing translated.
    /// </summary>
    /// <remarks>
    /// It used to be asymmetric — colours came out as a span and went in as an array, and
    /// "no stops" came out empty but went in as <c>null</c> — so recolouring cost a
    /// <c>ToArray()</c> and a ternary just to carry the stops across, and a caller who
    /// omitted the ternary silently lost them.
    /// </remarks>
    [Test]
    public async Task GradientOptions_RebuildsFromItsOwnProperties()
    {
        var original = new GradientOptions([SKColors.Red, SKColors.Lime, SKColors.Blue], GradientDirection.TopToBottom, [0f, 0.2f, 1f]);

        var recolored = new GradientOptions([SKColors.Black, SKColors.White, SKColors.Gray], original.Direction, original.ColorPositions);

        await Assert.That(recolored.Direction).IsEqualTo(original.Direction);
        await Assert.That(recolored.ColorPositions.SequenceEqual(original.ColorPositions)).IsTrue();
        await Assert.That(recolored.Colors[0]).IsEqualTo(SKColors.Black);

        // The evenly-distributed case needs no translation either: empty in, empty out.
        var evenly = new GradientOptions([SKColors.Red, SKColors.Blue]);
        var rebuilt = new GradientOptions([SKColors.Black, SKColors.White], evenly.Direction, evenly.ColorPositions);

        await Assert.That(rebuilt.ColorPositions.IsEmpty).IsTrue();
        await Assert.That(new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.LeftToRight, []).ColorPositions.IsEmpty).IsTrue();
    }

    /// <summary>
    /// A colour count the stops cannot match is refused where the pair is set: at
    /// construction, not at draw time. This is the invariant that keeps
    /// <see cref="GradientOptions.Colors"/> out of a <c>with</c> expression.
    /// </summary>
    [Test]
    public async Task GradientOptions_RefusesAColorCountItsStopsCannotMatch()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GradientOptions([SKColors.Black, SKColors.White], GradientDirection.TopToBottom, [0f, 0.2f, 1f]));
        await Assert.That(error.Message).Contains("Color positions length must match colors length");

        // No stops attached: any colour count is fine.
        var evenly = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.LeftToRight);
        var wider = new GradientOptions([SKColors.Black, SKColors.White, SKColors.Gray], evenly.Direction, evenly.ColorPositions);
        await Assert.That(wider.Colors.Length).IsEqualTo(3);
    }

    /// <summary>
    /// Equality compares the colours, not the array references. The generated
    /// <c>record</c> equality it replaces reported two identical gradients as different,
    /// because arrays compare by reference.
    /// </summary>
    [Test]
    public async Task GradientOptions_EqualityIsStructural()
    {
        var a = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom);
        var b = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom);
        var differentColor = new GradientOptions([SKColors.Red, SKColors.Lime], GradientDirection.TopToBottom);
        var differentDirection = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.LeftToRight);
        var withPositions = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom, [0f, 0.25f]);
        var samePositions = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom, [0f, 0.25f]);
        // Same stop count, different stop values: the pair that a length-only comparison
        // would call equal. It renders visibly differently, so it must not be.
        var movedStop = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom, [0f, 0.75f]);
        var thirdColor = new GradientOptions([SKColors.Red, SKColors.Blue, SKColors.Lime], GradientDirection.TopToBottom);

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a).IsNotEqualTo(differentColor);
        await Assert.That(a).IsNotEqualTo(differentDirection);
        await Assert.That(a).IsNotEqualTo(withPositions);
        await Assert.That(a).IsNotEqualTo(thirdColor);
        await Assert.That(withPositions).IsNotEqualTo(movedStop);

        // Equal-with-stops is the only pair that exercises the stop fold in GetHashCode,
        // and equal values have to hash equal or a Dictionary lookup misses its own key.
        await Assert.That(withPositions).IsEqualTo(samePositions);
        await Assert.That(withPositions.GetHashCode()).IsEqualTo(samePositions.GetHashCode());
        // Not a contract — unequal values may legally collide — but this hash is a
        // hand-written FNV-1a with no randomized seed, so it is deterministic, and the
        // assertion is what catches a GetHashCode that stops folding the stops in at all.
        await Assert.That(withPositions.GetHashCode()).IsNotEqualTo(movedStop.GetHashCode());
    }

    /// <summary>
    /// A caller varies the direction of a ready-made gradient with <c>with</c>, the same
    /// way the generator options structs are varied. The copy shares the colour array,
    /// which is safe precisely because the array is private and never handed out — that
    /// sharing was the bug when the array was a public <c>init</c> property.
    /// </summary>
    [Test]
    public async Task GradientOptions_WithExpression_ChangesTheDirectionAndKeepsTheColors()
    {
        var rotated = GradientOptions.Default with { Direction = GradientDirection.BottomToTop };

        await Assert.That(rotated.Direction).IsEqualTo(GradientDirection.BottomToTop);
        await Assert.That(GradientOptions.Default.Direction).IsEqualTo(GradientDirection.TopLeftToBottomRight);
        await Assert.That(rotated.Colors.SequenceEqual(GradientOptions.Default.Colors)).IsTrue();
        await Assert.That(rotated).IsNotEqualTo(GradientOptions.Default);
    }

    /// <summary>
    /// The generated <c>ToString</c> cannot print a <c>ReadOnlySpan</c> member usefully,
    /// so the record prints its own; this also keeps the generated printer from having to
    /// compile against a ref struct on four target frameworks.
    /// </summary>
    [Test]
    public async Task GradientOptions_ToString_NamesTheColorsAndDirection()
    {
        var text = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom).ToString();

        await Assert.That(text).Contains("2 colors");
        await Assert.That(text).Contains("TopToBottom");
        // Stops are printed only when there are some, so evenly distributed gradients do
        // not carry a misleading "0 stops"; both halves of that branch are printed here.
        await Assert.That(text).DoesNotContain("stops");

        var withStops = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom, [0f, 0.25f]).ToString();

        await Assert.That(withStops).Contains("2 stops");
    }

    /// <summary>
    /// <see cref="GradientOptions.Default"/> is shared by every caller that does not
    /// choose colours, so nothing a caller does may change what the next one gets.
    /// </summary>
    [Test]
    public async Task GradientOptionsDefault_IsTheDocumentedGradient()
    {
        await Assert.That(GradientOptions.Default.Colors.Length).IsEqualTo(2);
        await Assert.That(GradientOptions.Default.Colors[0]).IsEqualTo(SKColors.DarkOrange);
        await Assert.That(GradientOptions.Default.Colors[1]).IsEqualTo(SKColors.Firebrick);
        await Assert.That(GradientOptions.Default.Direction).IsEqualTo(GradientDirection.TopLeftToBottomRight);
    }

    /// <summary>
    /// An option object a caller holds can be varied with <c>with</c>: the three generator
    /// options and <see cref="IconData"/> here, <see cref="GradientOptions"/> in its own
    /// case above. Without it, changing one field of an immutable instance you did not
    /// construct means retyping every other field and silently taking the defaults for any
    /// you forget. On the generator options a copy and an assignment do the same.
    /// </summary>
    /// <remarks>
    /// <see cref="IconData"/> is a record <em>class</em>, so <c>with</c> runs a real copy
    /// constructor that a future edit could get wrong. The three option types are record
    /// <em>structs</em>, where the copy is memberwise and not user-overridable. What their
    /// cases can fail on is the accessor <c>with</c> calls: a setter after which the quiet
    /// zone does not read the 3 it was given, or one that also writes the other member the
    /// case reads.
    /// </remarks>
    [Test]
    public async Task OptionTypes_CanBeVariedWithWith()
    {
        using var logo = new SKBitmap(8, 8);
        var icon = IconData.FromImage(logo, iconSizePercent: 10, iconBorderWidth: 2);

        var bigger = icon with { IconSizePercent = 30 };

        await Assert.That(bigger.IconSizePercent).IsEqualTo(30);
        await Assert.That(icon.IconSizePercent).IsEqualTo(10);
        await Assert.That(bigger.IconBorderWidth).IsEqualTo(icon.IconBorderWidth);
        await Assert.That(bigger.Icon).IsSameReferenceAs(icon.Icon);
        await Assert.That(bigger).IsNotEqualTo(icon);

        // The summary names every option type, so every option type is exercised here.
        // GradientOptions has its own case above; these three had none. The quiet zone goes
        // to 3, which is the default of none of the three (4, 2 and 2), so that a setter
        // that left the default does not pass.
        var qr = new QRCodeGeneratorOptions { QuietZoneSize = 0, Version = 5 } with { QuietZoneSize = 3 };
        await Assert.That(qr.QuietZoneSize).IsEqualTo(3);
        await Assert.That(qr.Version).IsEqualTo(QRVersionRange.Exactly(5));

        var micro = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, MaskPattern = 1 } with { QuietZoneSize = 3 };
        await Assert.That(micro.QuietZoneSize).IsEqualTo(3);
        await Assert.That(micro.MaskPattern).IsEqualTo(1);

        var rm = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, FitStrategy = RmQRFitStrategy.MinimizeWidth } with { QuietZoneSize = 3 };
        await Assert.That(rm.QuietZoneSize).IsEqualTo(3);
        await Assert.That(rm.FitStrategy).IsEqualTo(RmQRFitStrategy.MinimizeWidth);
    }

    /// <summary>
    /// <see cref="IconData"/> is configured at construction and never after. The
    /// properties were settable, which let a caller reach a state the factory methods
    /// validate against and the renderer then rejects at draw time.
    /// </summary>
    [Test]
    public async Task IconData_PropertiesAreInitOnly()
    {
        foreach (var property in typeof(IconData).GetProperties(Instance))
        {
            var setter = property.SetMethod;
            await Assert.That(setter).IsNotNull().Because($"IconData.{property.Name} must be settable at construction");
            await Assert.That(setter!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit")).IsTrue()
                .Because($"IconData.{property.Name} must be init-only");
        }
    }

    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // The three generator options. Every rule about them below reads this list, and
    // GeneratorOptions_AreTheOnlyStructsACallerAssignsTo holds it against what the two
    // assemblies export, so a struct added to it on purpose meets every one of those rules.
    private static readonly Type[] GeneratorOptionStructTypes =
    [
        typeof(MicroQRCodeGeneratorOptions),
        typeof(QRCodeGeneratorOptions),
        typeof(RmQRCodeGeneratorOptions),
    ];

    // Func<Type>, for the reason on InitOnlySettingsObjects.
    public static IEnumerable<Func<Type>> GeneratorOptionStructs()
        => GeneratorOptionStructTypes.Select(type => (Func<Type>)(() => type));

    private static bool IsReadOnly(IEnumerable<CustomAttributeData> attributes)
        => attributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute");

    // Every type a caller can name: the public ones, and those nested as public or protected
    // in one of them. GetExportedTypes leaves out a type nested as protected, which a class
    // deriving from one of the extension points still sees.
    private static IEnumerable<Type> ExportedTypes() => new[] { typeof(QRCodeData).Assembly, typeof(IconData).Assembly }
        .SelectMany(a => a.GetTypes())
        .Where(IsVisibleOutsideItsAssembly)
        .OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static bool IsVisibleOutsideItsAssembly(Type type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) && IsVisibleOutsideItsAssembly(type.DeclaringType!)
        : type.IsPublic;

    // The full names in ordinal order, which is what a failure of the two rules below prints.
    private static string[] FullNames(IEnumerable<Type> types) => types.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // Every exported struct that is not declared `readonly`, enums aside.
    private static Type[] SettableStructs() => ExportedTypes()
        .Where(t => t.IsValueType && !t.IsEnum && !IsReadOnly(t.CustomAttributes))
        .ToArray();

    /// <summary>
    /// The three generator options are the only exported structs that are not
    /// <c>readonly</c>, so they are the only structs a caller may assign to member by member.
    /// </summary>
    /// <remarks>
    /// Each variable of a struct type holds its own value, and the library keeps no options
    /// after a call, so a setter changes the caller's own variable and nothing else, and an
    /// object initializer over <c>set</c> accessors compiles at C# 7.3, where <c>init</c>
    /// needs C# 9. That is why these three are settable. It is a decision made for them and
    /// not a default: a new struct that is not <c>readonly</c> fails here. A fourth set of
    /// generator options is added to <c>GeneratorOptionStructTypes</c> on purpose, and the
    /// rules of this class that read that list then apply to it.
    /// <see cref="GeneratorOptionsAssignmentTest"/> does not read the list, so it needs a
    /// table for the new struct. A struct that has to change for another reason, an
    /// enumerator for example, does not fit the list, and this rule and
    /// <see cref="SettableStruct_EveryMemberButASetterIsReadOnly"/> have no other exception
    /// today.
    /// </remarks>
    [Test]
    public async Task GeneratorOptions_AreTheOnlyStructsACallerAssignsTo()
    {
        var notReadOnly = FullNames(SettableStructs());
        var listed = FullNames(GeneratorOptionStructTypes);

        // Two differences and not one comparison, so that a failure names the type.
        await Assert.That(notReadOnly.Except(listed)).IsEmpty()
            .Because("the exported structs that are not readonly are the ones listed in GeneratorOptionStructTypes");
        await Assert.That(listed.Except(notReadOnly)).IsEmpty()
            .Because("GeneratorOptionStructTypes lists exported structs that are not readonly, and nothing else");
    }

    /// <summary>
    /// No exported type but the three generator options has a property whose <c>set</c>
    /// accessor is public or protected.
    /// </summary>
    /// <remarks>
    /// A settable class is the shape <see cref="IconData"/> had before 2.0.0, which let a
    /// caller reach a state its factory methods validate against. A <c>readonly</c> struct
    /// with a setter that writes into something its copies share would be assignable too.
    /// Neither is a struct that is not <c>readonly</c>, so
    /// <see cref="GeneratorOptions_AreTheOnlyStructsACallerAssignsTo"/> does not see them,
    /// and this sweep does. A protected accessor counts, because a class deriving from one
    /// of the extension points can call it. An <c>init</c> accessor does not:
    /// <see cref="EveryInitOnlyProperty_IsAlsoAConstructorParameter"/> reads the public and
    /// the protected ones.
    /// </remarks>
    [Test]
    public async Task GeneratorOptions_AreTheOnlyTypesWithAPlainSetter()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var withAPlainSetter = FullNames(ExportedTypes()
            .Where(t => t.GetProperties(declared).Any(p => p.SetMethod is { } setter
                && (setter.IsPublic || setter.IsFamily || setter.IsFamilyOrAssembly)
                && !IsInitOnly(setter))));
        var listed = FullNames(GeneratorOptionStructTypes);

        // Two differences and not one comparison, so that a failure names the type.
        await Assert.That(withAPlainSetter.Except(listed)).IsEmpty()
            .Because("a public or protected set accessor is a decision made for the generator options, and for no other type");
        await Assert.That(listed.Except(withAPlainSetter)).IsEmpty()
            .Because("each of the generator options has a set accessor a caller can use");
    }

    /// <summary>
    /// No exported type has a field a caller can assign.
    /// </summary>
    /// <remarks>
    /// A public or protected field that is neither a constant nor <c>readonly</c> is assigned
    /// like a plain setter, and <see cref="GeneratorOptions_AreTheOnlyTypesWithAPlainSetter"/>
    /// reads properties only. No type is excepted, the generator options included: a field
    /// cannot become a property later without breaking compiled callers.
    /// </remarks>
    [Test]
    public async Task NoExportedType_HasAFieldACallerCanAssign()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Enums aside: the runtime keeps an enum's value in a public field, value__, that C# cannot name.
        var assignable = ExportedTypes()
            .Where(t => !t.IsEnum)
            .SelectMany(t => t.GetFields(declared)
                .Where(f => (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) && !f.IsLiteral && !f.IsInitOnly)
                .Select(f => $"{t.FullName}.{f.Name}"));

        await Assert.That(assignable).IsEmpty()
            .Because("a field a caller can assign is a setter that no accessor can replace later");
    }

    /// <summary>
    /// Every option of the three generator options has a plain setter, not an <c>init</c>
    /// accessor.
    /// </summary>
    /// <remarks>
    /// An <c>init</c> accessor cannot be assigned below C# 9, so an <c>init</c>-only option
    /// is reachable there only through a constructor parameter, and a constructor that has
    /// to list every option changes its signature under every compiled caller whenever one
    /// is added. A sweep, so the next option is covered without a case of its own. An
    /// option is a property with a setter: one that only computes a value has none to check.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionStructs))]
    public async Task GeneratorOptions_EveryOptionHasAPlainSetter(Type type)
    {
        var options = type.GetProperties(Declared).Where(p => p.SetMethod is not null).ToArray();
        await Assert.That(options).IsNotEmpty().Because($"{type.Name} has options, so a sweep that finds none is broken");

        foreach (var property in options)
        {
            var setter = property.SetMethod!;
            await Assert.That(setter.IsPublic).IsTrue()
                .Because($"{type.Name}.{property.Name} must be settable by a caller");
            await Assert.That(IsInitOnly(setter)).IsFalse()
                .Because($"{type.Name}.{property.Name} must be a set accessor, which every language version can assign");
        }
    }

    /// <summary>
    /// Every option of the three generator options has a public getter.
    /// </summary>
    /// <remarks>
    /// The tests see the library's internals, so a getter made <c>internal</c> would still
    /// compile in every test that reads it. An option is a property with a setter, as in
    /// <see cref="GeneratorOptions_EveryOptionHasAPlainSetter"/>.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionStructs))]
    public async Task GeneratorOptions_EveryOptionHasAPublicGetter(Type type)
    {
        var options = type.GetProperties(Declared).Where(p => p.SetMethod is not null).ToArray();
        await Assert.That(options).IsNotEmpty().Because($"{type.Name} has options, so a sweep that finds none is broken");

        foreach (var property in options)
        {
            await Assert.That(property.GetMethod is { IsPublic: true }).IsTrue()
                .Because($"{type.Name}.{property.Name} must be readable by a caller");
        }
    }

    /// <summary>
    /// Each of the three generator options implements <see cref="IEquatable{T}"/> of itself,
    /// declares a <c>ToString</c> that names every option, and has <c>==</c>, which
    /// <c>record</c> gives a struct.
    /// </summary>
    /// <remarks>
    /// <c>with</c> and the equality assertions work on a plain struct too. When this rule
    /// was written, the rest of the suite passed with <c>record</c> taken off the three
    /// declarations.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionStructs))]
    public async Task GeneratorOptions_CompareAndPrintByValue(Type type)
    {
        await Assert.That(typeof(IEquatable<>).MakeGenericType(type).IsAssignableFrom(type)).IsTrue()
            .Because($"{type.Name} must implement IEquatable<{type.Name}>");
        await Assert.That(type.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType).IsEqualTo(type)
            .Because($"{type.Name} must declare ToString");
        await Assert.That(type.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static)).IsNotNull()
            .Because($"{type.Name} must have ==");

        // The text of the default value. An option is a property with a setter, as in the rules above.
        var printed = Activator.CreateInstance(type)!.ToString()!;
        foreach (var option in type.GetProperties(Declared).Where(p => p.SetMethod is not null))
        {
            await Assert.That(printed).Contains($"{option.Name} = ")
                .Because($"{type.Name}.ToString must name {option.Name}");
        }
    }

    /// <summary>
    /// The three generator options have no public constructor with parameters, and no public
    /// static method with parameters that returns the struct.
    /// </summary>
    /// <remarks>
    /// A constructor existed while the options were <c>init</c>-only, for compilers before
    /// C# 9. It had to list every option, so an option added in a later release would have
    /// changed its signature under every compiled caller, or stood beside it as a second
    /// constructor and made <c>new QRCodeGeneratorOptions(eciMode: …)</c> ambiguous. With
    /// <c>set</c> accessors the object initializer serves C# 7.3 too, and a constructor would
    /// only bring that problem back. So would a static method on the struct that returns
    /// one from the options, which is the same constructor under another name. The rule
    /// reads those two shapes: it does not see a method that hands the struct back through
    /// an <c>out</c> parameter, or one declared on another type.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionStructs))]
    public async Task GeneratorOptions_HaveNoConstructorOrFactoryThatListsTheOptions(Type type)
    {
        await Assert.That(type.GetConstructors().Where(c => c.GetParameters().Length > 0)).IsEmpty()
            .Because($"{type.Name} is configured through its setters, and a constructor would have to change with every new option");

        var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == type && m.GetParameters().Length > 0)
            .Select(m => m.ToString());
        await Assert.That(factories).IsEmpty()
            .Because($"a public static method on {type.Name} that returns it from parameters is a constructor under another name");
    }

    /// <summary>
    /// On each of the three generator options, <c>Default</c> is a public static property
    /// with no setter that returns the struct, no field but a constant is public or static,
    /// and no static property has a setter.
    /// </summary>
    /// <remarks>
    /// A public field could not become a property later without breaking compiled callers.
    /// <see cref="NoExportedType_HasAFieldACallerCanAssign"/> refuses one a caller can assign,
    /// and this rule also refuses a <c>readonly</c> one. A <c>Default</c> that is a field or a
    /// <c>ref</c> return would be one value for the whole process, which
    /// <c>QRCodeGeneratorOptions.Default.QuietZoneSize = 0;</c> would change. As a property
    /// that returns by value, that statement does not compile. A <c>Default</c> that returned
    /// a static field of the struct would have that shape and still be one value for the
    /// whole process, which is why the struct declares no static field but a constant. The
    /// rule reads the struct's own members: it does not see a value kept in another type.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionStructs))]
    public async Task GeneratorOptions_ExposeNoFieldAndDefaultIsAValue(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Default first, so that a Default of another shape is reported as that and not as a field or a setter.
        var defaultValue = type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        await Assert.That(defaultValue).IsNotNull().Because($"{type.Name}.Default must be a public static property");
        await Assert.That(defaultValue!.SetMethod).IsNull().Because($"{type.Name}.Default must have no setter");
        await Assert.That(defaultValue.PropertyType).IsEqualTo(type).Because($"{type.Name}.Default must return the struct by value");

        var fields = type.GetFields(declared).Where(f => !f.IsLiteral).ToArray();
        await Assert.That(fields.Where(f => f.IsPublic).Select(f => f.Name)).IsEmpty()
            .Because($"{type.Name} has no public field but a constant: one could not become a property later without breaking compiled callers");
        await Assert.That(fields.Where(f => f.IsStatic).Select(f => f.Name)).IsEmpty()
            .Because($"{type.Name} declares no static field but a constant: one would hold a value for the whole process");
        await Assert.That(type.GetProperties(declared).Where(p => p.SetMethod is { IsStatic: true }).Select(p => p.Name)).IsEmpty()
            .Because($"{type.Name} declares no static property with a setter");
    }

    /// <summary>
    /// On an exported struct that is not <c>readonly</c>, every instance member except a
    /// property's setter is <c>readonly</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generators take their options by <c>in</c>. The compiler copies a struct before
    /// it calls a member that is not <c>readonly</c> through such a reference, so one getter
    /// without the modifier costs a copy in every method that reads it, and takes
    /// <c>readonly</c> off the generated <c>ToString</c> and <c>PrintMembers</c> with it.
    /// A <c>readonly struct</c> cannot make that mistake, which is what these three gave up
    /// to be settable.
    /// </para>
    /// <para>
    /// IDE0251 refuses, at build time in the two shipping projects, a member that could be
    /// <c>readonly</c> and is not. This rule also refuses a member that writes state, such
    /// as a private helper a setter calls, which the analyzer accepts: code inside the
    /// struct can call such a member through <c>in</c>, and the write then lands on a copy.
    /// The analyzer in turn reports a setter that could be <c>readonly</c>, which this rule
    /// leaves alone. This rule also holds for a build that did not run the analyzer, and for
    /// both assemblies: <see cref="GeneratorOptions_AreTheOnlyStructsACallerAssignsTo"/>
    /// says which structs the sweep finds.
    /// </para>
    /// </remarks>
    [Test]
    public async Task SettableStruct_EveryMemberButASetterIsReadOnly()
    {
        var offenders = new List<string>();
        foreach (var type in SettableStructs())
        {
            var setters = type.GetProperties(Declared).Select(p => p.SetMethod).Where(m => m is not null).ToHashSet();
            offenders.AddRange(type.GetMethods(Declared)
                .Where(m => !setters.Contains(m) && !IsReadOnly(m.CustomAttributes))
                .Select(m => $"{type.Name}.{m.Name}"));
        }

        await Assert.That(offenders).IsEmpty()
            .Because("the compiler copies the struct before it calls a member that is not readonly through `in`");
    }
}
