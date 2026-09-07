using System.Reflection;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The type shapes 2.0.0 unified: one value kind for every sizing and decode result,
/// one immutability story for the option objects, and no extension point that was not
/// designed as one.
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
    public static IEnumerable<Func<Type>> GeneratorOptionTypes()
    {
        yield return () => typeof(QRCodeGeneratorOptions);
        yield return () => typeof(MicroQRCodeGeneratorOptions);
        yield return () => typeof(RmQRCodeGeneratorOptions);
        // Not a generator option, but the same rule: it is a settings object a caller builds,
        // and its members are init-only for the same reason.
        yield return () => typeof(IconData);
    }

    // CanWrite is true for a private setter too, and an indexer is not a setting, so
    // neither belongs in a rule about what a caller can configure: QRCodeData.Version is
    // `{ get; private set; }` and would otherwise demand a constructor parameter it has
    // no business exposing.
    private static PropertyInfo[] SettableProperties(Type type) => type.GetProperties(Instance)
        .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>
    /// Every settable property on every exported type is reachable without an <c>init</c>
    /// setter. A consumer whose compiler predates C# 9 cannot assign one, and the parameter
    /// list generator overloads that used to serve those consumers were removed in 2.0.0,
    /// so a constructor parameter is the only remaining route.
    /// </summary>
    /// <remarks>
    /// A sweep, not a list. The rule was first written over a hand-listed set of option
    /// structs and <see cref="IconData"/> was found missing from it a review round later,
    /// with its two factories both hardcoding <see cref="ImageIconShape"/> — so every other
    /// shape was unreachable below C# 9. A list only states the rule for the types someone
    /// remembered; the sweep states it for the next type too.
    /// </remarks>
    [Test]
    public async Task EverySettableProperty_IsReachableWithoutAnInitSetter()
    {
        var offenders = new List<string>();

        foreach (var type in new[] { typeof(QRCodeData).Assembly, typeof(IconData).Assembly }
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => SettableProperties(t).Length > 0)
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            // One constructor has to cover everything. Pooling parameters across all of
            // them would pass a type whose settings are split between two constructors,
            // where a caller picking either one still cannot set the rest.
            //
            // Match on name AND type: a parameter named `maskPattern` typed `int` rather
            // than `int?` would satisfy a name-only rule while making `null`, the real
            // default, unreachable, and would quietly change the default configuration.
            var settable = SettableProperties(type);
            var covered = type.GetConstructors().Any(c =>
                settable.All(p => c.GetParameters().Any(a =>
                    string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase) && a.ParameterType == p.PropertyType)));

            if (!covered)
            {
                offenders.Add($"{type.FullName}: no single constructor sets [{string.Join(", ", settable.Select(p => p.Name))}]");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("a property no constructor can set is unreachable below C# 9");
    }

    /// <summary>
    /// A settings object a caller configures takes one constructor whose parameters are all
    /// optional, so setting one option never means restating the rest. Only a
    /// <c>required</c> member may be mandatory, because it alone has no default to fall
    /// back on.
    /// </summary>
    /// <remarks>
    /// Listed rather than swept, because it is an ergonomic rule about settings objects and
    /// not every constructible type is one: <see cref="ModuleRect"/> is a positional value
    /// whose four components are all meaningful, and <see cref="GradientOptions"/> cannot
    /// default its colours or express them as a property at all (they are read back as a
    /// <see cref="ReadOnlySpan{T}"/>). Reachability, the rule that actually protects the
    /// pre-C#-9 audience, is swept over both assemblies above and covers those two.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(GeneratorOptionTypes))]
    public async Task SettingsObject_TakesOneConstructorOfOptionalParameters(Type type)
    {
        var constructors = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .ToArray();
        await Assert.That(constructors.Length).IsEqualTo(1)
            .Because($"{type.Name} needs exactly one init-free way to set every option");

        var requiredNames = SettableProperties(type)
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
    /// The constructor is a second spelling of the object initializer, not a second
    /// behaviour: it produces equal values, and where an accessor validates (only
    /// <c>MaskPattern</c>, on two of these types) it validates identically, because it
    /// assigns through the same <c>init</c> accessors.
    /// </summary>
    [Test]
    public async Task GeneratorOptions_ConstructorAgreesWithTheObjectInitializer()
    {
        // Every parameter is given a value that differs from its default, so a dropped or
        // crossed assignment in the constructor body shows up. Setting only a few would
        // leave the rest verified by name in the test above and by nothing at all here.
        await Assert.That(new QRCodeGeneratorOptions(
                eciMode: EciMode.Utf8,
                utf8Bom: true,
                version: 5,
                quietZoneSize: 0,
                maskPattern: 3,
                boostEccLevel: true,
                segmentation: QRSegmentation.Optimal))
            .IsEqualTo(new QRCodeGeneratorOptions
            {
                EciMode = EciMode.Utf8,
                Utf8Bom = true,
                Version = 5,
                QuietZoneSize = 0,
                MaskPattern = 3,
                BoostEccLevel = true,
                Segmentation = QRSegmentation.Optimal,
            });
        await Assert.That(new MicroQRCodeGeneratorOptions(
                version: MicroQRVersion.M3,
                quietZoneSize: 0,
                maskPattern: 2,
                segmentation: MicroQRSegmentation.Optimal))
            .IsEqualTo(new MicroQRCodeGeneratorOptions
            {
                Version = MicroQRVersion.M3,
                QuietZoneSize = 0,
                MaskPattern = 2,
                Segmentation = MicroQRSegmentation.Optimal,
            });
        await Assert.That(new RmQRCodeGeneratorOptions(
                eciMode: EciMode.Utf8,
                version: RmQRVersion.R7x43,
                fitStrategy: RmQRFitStrategy.MinimizeWidth,
                height: RmQRHeight.H7,
                quietZoneSize: 0,
                segmentation: RmQRSegmentation.Optimal))
            .IsEqualTo(new RmQRCodeGeneratorOptions
            {
                EciMode = EciMode.Utf8,
                Version = RmQRVersion.R7x43,
                FitStrategy = RmQRFitStrategy.MinimizeWidth,
                Height = RmQRHeight.H7,
                QuietZoneSize = 0,
                Segmentation = RmQRSegmentation.Optimal,
            });

        // An omitted parameter has to reproduce what `default` carries for that property.
        // The quiet zone is the one that can silently disagree: each struct stores it as an
        // offset from its specified default so that `default` means 4 / 2 / 2 rather than 0,
        // and a constructor default of 0 would look right and encode a different symbol.
        // `new T()` cannot show this — on a struct it binds to the synthesized parameterless
        // constructor and emits initobj, never the all-optional one — so each call below
        // sets some other option and leaves the quiet zone to the parameter default.
        await Assert.That(new QRCodeGeneratorOptions(maskPattern: 3))
            .IsEqualTo(new QRCodeGeneratorOptions { MaskPattern = 3 });
        await Assert.That(new QRCodeGeneratorOptions(maskPattern: 3).QuietZoneSize).IsEqualTo(4);
        await Assert.That(new MicroQRCodeGeneratorOptions(maskPattern: 3))
            .IsEqualTo(new MicroQRCodeGeneratorOptions { MaskPattern = 3 });
        await Assert.That(new MicroQRCodeGeneratorOptions(maskPattern: 3).QuietZoneSize).IsEqualTo(2);
        await Assert.That(new RmQRCodeGeneratorOptions(eciMode: EciMode.Utf8))
            .IsEqualTo(new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        await Assert.That(new RmQRCodeGeneratorOptions(eciMode: EciMode.Utf8).QuietZoneSize).IsEqualTo(2);

        // Same-typed parameters given the same value hide a crossed assignment, and the
        // call above gives both bools `true`. One call per bool separates them; the other
        // parameter pairs are all distinctly typed, so a crossing there cannot compile.
        await Assert.That(new QRCodeGeneratorOptions(utf8Bom: true).BoostEccLevel).IsFalse();
        await Assert.That(new QRCodeGeneratorOptions(boostEccLevel: true).Utf8Bom).IsFalse();

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

        // The init accessors validate; routing through them means the constructor does too.
        await Assert.That(() => new QRCodeGeneratorOptions(maskPattern: 8)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MicroQRCodeGeneratorOptions(maskPattern: 4)).Throws<ArgumentOutOfRangeException>();

        // IconData's guard is the one place the two routes deliberately differ. The
        // constructor is reached from language versions with no nullable analysis, where
        // a null compiles silently, so it throws; the initializer needs `null!` to get
        // there at all and keeps its existing behaviour of drawing no icon.
        await Assert.That(() => new IconData(null!)).Throws<ArgumentNullException>();
        await Assert.That(new IconData { Icon = null! }.Icon).IsNull();
    }

    // Func<Type>, for the reason on GeneratorOptionTypes.
    public static IEnumerable<Func<Type>> ResultValues()
    {
        yield return () => typeof(QRCodeCalculatedSize);
        yield return () => typeof(MicroQRCodeCalculatedSize);
        yield return () => typeof(RmQRCodeCalculatedSize);
        yield return () => typeof(QRCodeDecodeInfo);
        yield return () => typeof(MicroQRCodeDecodeInfo);
        yield return () => typeof(RmQRCodeDecodeInfo);
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
    /// defines a single data mask, so there is no mask pattern to report.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeDecodeInfo), true)]
    [Arguments(typeof(MicroQRCodeDecodeInfo), true)]
    [Arguments(typeof(RmQRCodeDecodeInfo), false)]
    public async Task DecodeInfo_ReportsTheSameMembers(Type type, bool hasMaskPattern)
    {
        var names = type.GetProperties(Instance).Select(p => p.Name).ToArray();

        await Assert.That(names).Contains("Status");
        await Assert.That(names).Contains("Version");
        await Assert.That(names).Contains("EccLevel");
        await Assert.That(names).Contains("ErrorsCorrected");
        await Assert.That(names.Contains("MaskPattern")).IsEqualTo(hasMaskPattern);
        await Assert.That(names.Length).IsEqualTo(hasMaskPattern ? 5 : 4);

        // Status is the shared enum on all three; version and ECC level are per-symbology.
        await Assert.That(type.GetProperty("Status")!.PropertyType).IsEqualTo(typeof(DecodeStatus));
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
        await Assert.That(open).IsEquivalentTo(extensionPoints)
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
    /// case above. Without it, changing one field of an instance you did not construct
    /// means retyping every other field and silently taking the defaults for any you forget.
    /// </summary>
    /// <remarks>
    /// Only <see cref="IconData"/> can fail the "keeps the other members" half: it is a
    /// record <em>class</em>, so <c>with</c> runs a real copy constructor that a future
    /// edit could get wrong. The three option types are record <em>structs</em>, where the
    /// copy is memberwise and not user-overridable, so their cases pin the API shape —
    /// that <c>with</c> compiles and reaches an <c>init</c> accessor — rather than guarding
    /// a copy step that cannot break.
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
        // GradientOptions has its own case above; these three had none.
        var qr = new QRCodeGeneratorOptions { QuietZoneSize = 0, Version = 5 } with { QuietZoneSize = 2 };
        await Assert.That(qr.QuietZoneSize).IsEqualTo(2);
        await Assert.That(qr.Version).IsEqualTo(QRVersionRange.Exactly(5));

        var micro = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, MaskPattern = 1 } with { QuietZoneSize = 2 };
        await Assert.That(micro.QuietZoneSize).IsEqualTo(2);
        await Assert.That(micro.MaskPattern).IsEqualTo(1);

        var rm = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, FitStrategy = RmQRFitStrategy.MinimizeWidth } with { QuietZoneSize = 2 };
        await Assert.That(rm.QuietZoneSize).IsEqualTo(2);
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
}
