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

    public static IEnumerable<Type> ResultValues()
    {
        yield return typeof(QRCodeCalculatedSize);
        yield return typeof(MicroQRCodeCalculatedSize);
        yield return typeof(RmQRCodeCalculatedSize);
        yield return typeof(QRCodeDecodeInfo);
        yield return typeof(MicroQRCodeDecodeInfo);
        yield return typeof(RmQRCodeDecodeInfo);
        yield return typeof(SymbolCorners);
        yield return typeof(ImagePoint);
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
        await Assert.That(names).Contains("Corners");
        await Assert.That(names.Contains("MaskPattern")).IsEqualTo(hasMaskPattern);
        await Assert.That(names.Length).IsEqualTo(hasMaskPattern ? 6 : 5);

        // Status and Corners are the shared types on all three; version and ECC level are per-symbology.
        await Assert.That(type.GetProperty("Status")!.PropertyType).IsEqualTo(typeof(DecodeStatus));
        await Assert.That(type.GetProperty("Corners")!.PropertyType).IsEqualTo(typeof(SymbolCorners));
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

    public static IEnumerable<Type> SealedTypes()
    {
        yield return typeof(QRCodeData);
        yield return typeof(MicroQRCodeData);
        yield return typeof(RmQRCodeData);
        yield return typeof(QRCodeImageBuilder);
        yield return typeof(MicroQRCodeImageBuilder);
        yield return typeof(RmQRCodeImageBuilder);
        yield return typeof(IconData);
        yield return typeof(GradientOptions);
    }

    /// <summary>
    /// A public class with no designed extension point is sealed. The three shape
    /// hierarchies (<see cref="ModuleShape"/>, <see cref="FinderPatternShape"/>,
    /// <see cref="IconShape"/>) are the extension points, and
    /// <see cref="SymbolImageBuilderBase{TSelf}"/> is open only to this assembly through
    /// its <c>private protected</c> constructor.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SealedTypes))]
    public async Task TypeWithNoExtensionPoint_IsSealed(Type type)
    {
        await Assert.That(type.IsSealed).IsTrue().Because($"{type.Name} has no designed extension point");
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

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a).IsNotEqualTo(differentColor);
        await Assert.That(a).IsNotEqualTo(differentDirection);
        await Assert.That(a).IsNotEqualTo(withPositions);
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
    /// An option object a caller holds can be varied with <c>with</c>, on every option
    /// type: the generator options, <see cref="GradientOptions"/> and
    /// <see cref="IconData"/>. Without it, changing one field of an instance you did not
    /// construct means retyping every other field and silently taking the defaults for
    /// any you forget.
    /// </summary>
    [Test]
    public async Task IconData_CanBeVariedWithWith()
    {
        using var logo = new SKBitmap(8, 8);
        var icon = IconData.FromImage(logo, iconSizePercent: 10, iconBorderWidth: 2);

        var bigger = icon with { IconSizePercent = 30 };

        await Assert.That(bigger.IconSizePercent).IsEqualTo(30);
        await Assert.That(icon.IconSizePercent).IsEqualTo(10);
        await Assert.That(bigger.IconBorderWidth).IsEqualTo(icon.IconBorderWidth);
        await Assert.That(bigger.Icon).IsSameReferenceAs(icon.Icon);
        await Assert.That(bigger).IsNotEqualTo(icon);
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
