using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The builders' entry points that take the core's styles in the core's whole percents. Each draws what the matching shape class draws,
/// so a style chosen once reads the same through <see cref="SymbolImageWriter"/> and through a builder.
/// </summary>
public class SymbolImageBuilderStyleTest
{
    private const string Content = "https://github.com/guitarrapc/FeatherQR";

    public static IEnumerable<(ModuleStyle Style, int ScalePercent, int CornerRadiusPercent)> ModuleCases() =>
    [
        (ModuleStyle.Square, 75, 30),
        (ModuleStyle.Circle, 92, 30),
        (ModuleStyle.Circle, 50, 30),
        (ModuleStyle.RoundedSquare, 90, 30),
        (ModuleStyle.RoundedSquare, 90, 10),
        (ModuleStyle.RoundedSquare, 80, 50),
        (ModuleStyle.RoundedSquare, 90, 0),
        (ModuleStyle.RoundedSquare, 90, 100),
    ];

    /// <summary>Every case changes the image, so an entry point that ignored its arguments would fail the case.</summary>
    [Test]
    [MethodDataSource(nameof(ModuleCases))]
    public async Task WithModuleStyle_DrawsAsTheMatchingShape(ModuleStyle style, int scalePercent, int cornerRadiusPercent)
    {
        ModuleShape shape = style switch
        {
            ModuleStyle.Circle => CircleModuleShape.Default,
            ModuleStyle.RoundedSquare => new RoundedRectangleModuleShape(cornerRadiusPercent / 100f),
            _ => RectangleModuleShape.Default,
        };
        var plain = new QRCodeImageBuilder(Content).ToByteArray();
        var expected = new QRCodeImageBuilder(Content).WithModuleShape(shape, scalePercent / 100f).ToByteArray();
        var actual = new QRCodeImageBuilder(Content).WithModuleStyle(style, scalePercent, cornerRadiusPercent).ToByteArray();

        await Assert.That(expected).IsNotEquivalentTo(plain, CollectionOrdering.Matching).Because("the case draws something the plain image does not");
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    public static IEnumerable<(FinderPatternStyle Style, int CornerRadiusPercent)> FinderCases() =>
    [
        (FinderPatternStyle.Circle, 30),
        (FinderPatternStyle.RoundedSquare, 30),
        (FinderPatternStyle.RoundedSquare, 10),
        (FinderPatternStyle.RoundedSquareCircle, 30),
        (FinderPatternStyle.RoundedSquareCircle, 45),
        (FinderPatternStyle.RoundedSquare, 100),
        (FinderPatternStyle.RoundedSquareCircle, 100),
    ];

    /// <summary>A radius of 0 is in range, and draws the rounded finder pattern class at no radius.</summary>
    [Test]
    public async Task WithFinderPatternStyle_TakesARadiusOfZero()
    {
        var zero = new QRCodeImageBuilder(Content).WithFinderPatternStyle(FinderPatternStyle.RoundedSquare, 0).ToByteArray();
        var shape = new QRCodeImageBuilder(Content).WithFinderPatternShape(new RoundedRectangleFinderPatternShape(0f)).ToByteArray();

        await Assert.That(zero).IsEquivalentTo(shape, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(FinderCases))]
    public async Task WithFinderPatternStyle_DrawsAsTheMatchingShape(FinderPatternStyle style, int cornerRadiusPercent)
    {
        FinderPatternShape shape = style switch
        {
            FinderPatternStyle.Circle => CircleFinderPatternShape.Default,
            FinderPatternStyle.RoundedSquare => new RoundedRectangleFinderPatternShape(cornerRadiusPercent / 100f),
            FinderPatternStyle.RoundedSquareCircle => new RoundedRectangleCircleFinderPatternShape(cornerRadiusPercent / 100f),
            _ => RectangleFinderPatternShape.Default,
        };
        var plain = new QRCodeImageBuilder(Content).ToByteArray();
        var expected = new QRCodeImageBuilder(Content).WithFinderPatternShape(shape).ToByteArray();
        var actual = new QRCodeImageBuilder(Content).WithFinderPatternStyle(style, cornerRadiusPercent).ToByteArray();

        await Assert.That(expected).IsNotEquivalentTo(plain, CollectionOrdering.Matching).Because("the case draws something the plain image does not");
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>A square finder pattern is the standard one, the image a builder draws without the call.</summary>
    [Test]
    public async Task WithFinderPatternStyle_Square_IsThePlainImage()
    {
        var plain = new QRCodeImageBuilder(Content).ToByteArray();
        var square = new QRCodeImageBuilder(Content).WithFinderPatternStyle(FinderPatternStyle.Square).ToByteArray();

        await Assert.That(square).IsEquivalentTo(plain, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The defaults are the core's: a module at full size, and a corner radius of 30 percent for modules and finder patterns alike,
    /// where the rounded finder pattern class defaults to 20.
    /// </summary>
    [Test]
    public async Task Defaults_AreTheCores()
    {
        var circle = new QRCodeImageBuilder(Content).WithModuleStyle(ModuleStyle.Circle).ToByteArray();
        var circleFull = new QRCodeImageBuilder(Content).WithModuleShape(CircleModuleShape.Default, 1f).ToByteArray();
        await Assert.That(circle).IsEquivalentTo(circleFull, CollectionOrdering.Matching).Because("the scale defaults to 100 percent");

        var rounded = new QRCodeImageBuilder(Content).WithModuleStyle(ModuleStyle.RoundedSquare, 90).ToByteArray();
        var roundedThirty = new QRCodeImageBuilder(Content).WithModuleShape(new RoundedRectangleModuleShape(0.3f), 0.9f).ToByteArray();
        await Assert.That(rounded).IsEquivalentTo(roundedThirty, CollectionOrdering.Matching);

        var finder = new QRCodeImageBuilder(Content).WithFinderPatternStyle(FinderPatternStyle.RoundedSquare).ToByteArray();
        var finderThirty = new QRCodeImageBuilder(Content).WithFinderPatternShape(new RoundedRectangleFinderPatternShape(0.3f)).ToByteArray();
        var classDefault = new QRCodeImageBuilder(Content).WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default).ToByteArray();
        await Assert.That(finder).IsEquivalentTo(finderThirty, CollectionOrdering.Matching);
        await Assert.That(finder).IsNotEquivalentTo(classDefault, CollectionOrdering.Matching).Because("30 and 20 percent draw different corners");
    }

    /// <summary>The style and the shape calls set one choice, so whichever comes last is drawn.</summary>
    [Test]
    public async Task TheLaterCall_Wins()
    {
        var plain = new QRCodeImageBuilder(Content).ToByteArray();
        var circle = new QRCodeImageBuilder(Content).WithModuleStyle(ModuleStyle.Circle, 92).ToByteArray();

        var styleThenShape = new QRCodeImageBuilder(Content).WithModuleStyle(ModuleStyle.Circle, 92).WithModuleShape(RectangleModuleShape.Default).ToByteArray();
        var shapeThenStyle = new QRCodeImageBuilder(Content).WithModuleShape(RectangleModuleShape.Default).WithModuleStyle(ModuleStyle.Circle, 92).ToByteArray();

        await Assert.That(styleThenShape).IsEquivalentTo(plain, CollectionOrdering.Matching);
        await Assert.That(shapeThenStyle).IsEquivalentTo(circle, CollectionOrdering.Matching);
    }

    /// <summary>The entry points sit on the builders' shared base, so the Micro QR and rMQR builders take them too.</summary>
    [Test]
    public async Task MicroQrAndRmQrBuilders_TakeTheStyles()
    {
        var micro = new MicroQRCodeImageBuilder("12345").WithModuleStyle(ModuleStyle.Circle, 85).WithFinderPatternStyle(FinderPatternStyle.Circle).ToByteArray();
        var microShapes = new MicroQRCodeImageBuilder("12345").WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default).ToByteArray();
        await Assert.That(micro).IsEquivalentTo(microShapes, CollectionOrdering.Matching);

        var rm = new RmQRCodeImageBuilder("FeatherQR").WithModuleStyle(ModuleStyle.RoundedSquare, 90).WithFinderPatternStyle(FinderPatternStyle.RoundedSquare).ToByteArray();
        var rmShapes = new RmQRCodeImageBuilder("FeatherQR").WithModuleShape(new RoundedRectangleModuleShape(0.3f), 0.9f).WithFinderPatternShape(new RoundedRectangleFinderPatternShape(0.3f)).ToByteArray();
        await Assert.That(rm).IsEquivalentTo(rmShapes, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The core's rounded finder patterns are the builders' shape. At 40 pixels a module no pixel of the finder box is clearly dark in one
    /// package and clearly light in the other. The two anti-alias their edges apart, so only pixels past a quarter from either end count,
    /// and inner squares rounded by a share of their own sides instead give such pixels at the inner corners.
    /// </summary>
    [Test]
    [Arguments(FinderPatternStyle.RoundedSquare, 20)]
    [Arguments(FinderPatternStyle.RoundedSquare, 30)]
    [Arguments(FinderPatternStyle.RoundedSquareCircle, 30)]
    public async Task RoundedFinders_AreTheBuildersShape(FinderPatternStyle style, int cornerRadiusPercent)
    {
        const int pixels = 40;
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = pixels, FinderPatternStyle = style, CornerRadiusPercent = cornerRadiusPercent };
        var core = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, options)];
        SymbolImageWriter.TryWritePixels(qr, core, PixelLayout.Gray8, out var width, out var height, options);
        using var skia = new QRCodeImageBuilder(qr).WithModulePixelSize(pixels).WithFinderPatternStyle(style, cornerRadiusPercent).ToBitmap();
        await Assert.That((skia.Width, skia.Height)).IsEqualTo((width, height));

        var origin = (qr.Size - (17 + 4 * qr.Version)) / 2 * pixels;
        var differing = 0;
        for (var y = origin; y < origin + 7 * pixels; y++)
            for (var x = origin; x < origin + 7 * pixels; x++)
            {
                int a = core[y * width + x], b = skia.GetPixel(x, y).Red;
                if ((a < 64 && b > 192) || (a > 192 && b < 64))
                    differing++;
            }
        await Assert.That(differing).IsEqualTo(0);
    }

    [Test]
    public async Task OutOfRangeArguments_Throw_NamingTheArgument()
    {
        var builder = new QRCodeImageBuilder(Content);
        await ThrowsFor(() => builder.WithModuleStyle((ModuleStyle)3), "style");
        await ThrowsFor(() => builder.WithModuleStyle((ModuleStyle)(-1)), "style");
        await ThrowsFor(() => builder.WithModuleStyle(ModuleStyle.Circle, 49), "scalePercent");
        await ThrowsFor(() => builder.WithModuleStyle(ModuleStyle.Circle, 101), "scalePercent");
        await ThrowsFor(() => builder.WithModuleStyle(ModuleStyle.RoundedSquare, 90, -1), "cornerRadiusPercent");
        await ThrowsFor(() => builder.WithModuleStyle(ModuleStyle.RoundedSquare, 90, 101), "cornerRadiusPercent");
        await ThrowsFor(() => builder.WithFinderPatternStyle((FinderPatternStyle)4), "style");
        await ThrowsFor(() => builder.WithFinderPatternStyle(FinderPatternStyle.RoundedSquare, -1), "cornerRadiusPercent");
        await ThrowsFor(() => builder.WithFinderPatternStyle(FinderPatternStyle.RoundedSquare, 101), "cornerRadiusPercent");
    }

    private static async Task ThrowsFor(Action call, string parameter)
    {
        var thrown = await Assert.That(call).Throws<ArgumentOutOfRangeException>();
        await Assert.That(thrown!.ParamName).IsEqualTo(parameter);
    }
}
