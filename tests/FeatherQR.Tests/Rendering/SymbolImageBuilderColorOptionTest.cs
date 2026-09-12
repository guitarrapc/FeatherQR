using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The option setters shared by every builder set the option they name and nothing else.
/// A colour is a value, so it is passed as one; only an option that can be absent takes <see langword="null"/>.
/// </summary>
public class SymbolImageBuilderColorOptionTest
{
    private const string TestContent = "color-option-test";
    private const int ModulePixelSize = 4;

    [Test]
    public async Task WithClearColor_KeepsTheBackgroundColorSetEarlier()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var (canvasSide, origin) = Layout(qr.Size);

        using var bitmap = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(ModulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithBackgroundColor(SKColors.Yellow)
            .WithClearColor(SKColors.Transparent)
            .ToBitmap();

        // The padding is transparent and the quiet zone inside the content keeps the background.
        // Alpha only: a fully transparent pixel carries no color channels to compare.
        await Assert.That(bitmap.GetPixel(0, 0).Alpha).IsEqualTo((byte)0);
        await Assert.That(bitmap.GetPixel(origin + 1, origin + 1)).IsEquivalentTo(SKColors.Yellow);
    }

    [Test]
    public async Task WithCodeColor_KeepsTheBackgroundColorSetEarlier()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var (canvasSide, origin) = Layout(qr.Size);

        using var bitmap = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(ModulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithBackgroundColor(SKColors.Yellow)
            .WithCodeColor(SKColors.Blue)
            .ToBitmap();

        await Assert.That(bitmap.GetPixel(origin + 1, origin + 1)).IsEquivalentTo(SKColors.Yellow);
        // Top-left finder pattern: four quiet-zone modules in, one module in from its edge.
        var finderCenter = origin + (4 * ModulePixelSize) + (ModulePixelSize / 2);
        await Assert.That(bitmap.GetPixel(finderCenter, finderCenter)).IsEquivalentTo(SKColors.Blue);
    }

    [Test]
    public async Task WithColors_MatchesTheTwoSingleColorSetters()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        using var pair = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithColors(SKColors.Blue, SKColors.Yellow)
            .ToBitmap();
        using var singles = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithCodeColor(SKColors.Blue)
            .WithBackgroundColor(SKColors.Yellow)
            .ToBitmap();

        await Assert.That(pair.Bytes).IsEquivalentTo(singles.Bytes);
    }

    [Test]
    public async Task WithColors_AfterWithClearColor_LeavesTheClearColorAlone()
    {
        // The reverse order of the same rule: WithColors sets the two colours it names and no third one.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var (canvasSide, origin) = Layout(qr.Size);

        using var bitmap = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(ModulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithClearColor(SKColors.Red)
            .WithColors(SKColors.Blue, SKColors.Yellow)
            .ToBitmap();

        await Assert.That(bitmap.GetPixel(0, 0)).IsEquivalentTo(SKColors.Red);
        await Assert.That(bitmap.GetPixel(origin + 1, origin + 1)).IsEquivalentTo(SKColors.Yellow);
    }

    [Test]
    public async Task WithClearColor_Null_FollowsTheBackgroundColor()
    {
        // The absent clear colour is the one null that stays: it is a state, not a default value.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var (canvasSide, _) = Layout(qr.Size);

        using var explicitlyAbsent = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(ModulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithBackgroundColor(SKColors.Yellow)
            .WithClearColor(null)
            .ToBitmap();
        using var neverSet = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(ModulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithBackgroundColor(SKColors.Yellow)
            .ToBitmap();

        await Assert.That(explicitlyAbsent.Bytes).IsEquivalentTo(neverSet.Bytes);
        await Assert.That(explicitlyAbsent.GetPixel(0, 0)).IsEquivalentTo(SKColors.Yellow);
    }

    [Test]
    public async Task WithModuleShape_RectangleDefault_MatchesTheOmittedShape()
    {
        // The null that used to mean "plain squares" had this value all along.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        using var named = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithModuleShape(RectangleModuleShape.Default)
            .ToBitmap();
        using var omitted = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .ToBitmap();

        await Assert.That(named.Bytes).IsEquivalentTo(omitted.Bytes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WithFinderPatternShape_RectangleDefault_MatchesTheOmittedShape(bool styledModules)
    {
        // Why the finder shape could stop taking null: omitting it and naming the square render the same,
        // whether or not the modules are styled. Only the draw path differs.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        using var named = Build(qr, styledModules).WithFinderPatternShape(RectangleFinderPatternShape.Default).ToBitmap();
        using var omitted = Build(qr, styledModules).ToBitmap();

        await Assert.That(named.Bytes).IsEquivalentTo(omitted.Bytes);

        static QRCodeImageBuilder Build(QRCodeData qr, bool styledModules)
        {
            var builder = new QRCodeImageBuilder(qr).WithSize(300, 300);
            return styledModules ? builder.WithModuleShape(CircleModuleShape.Default, 0.85f) : builder;
        }
    }

    [Test]
    public async Task WithFinderPatternShape_Null_Throws()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var builder = new QRCodeImageBuilder(qr);

        await Assert.That(() => builder.WithFinderPatternShape(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithModuleShape_Null_Throws()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var builder = new QRCodeImageBuilder(qr);

        await Assert.That(() => builder.WithModuleShape(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ColorSetters_ChainOnEverySymbology()
    {
        var micro = MicroQRCodeGenerator.Create("1234", MicroQREccLevel.L);
        var rmqr = RmQRCodeGenerator.Create(TestContent, RmQREccLevel.M);

        using var microBitmap = new MicroQRCodeImageBuilder(micro)
            .WithSize(200, 200)
            .WithCodeColor(SKColors.Blue)
            .WithBackgroundColor(SKColors.Yellow)
            .ToBitmap();
        using var rmqrBitmap = new RmQRCodeImageBuilder(rmqr)
            .WithSize(300, 100)
            .WithCodeColor(SKColors.Blue)
            .WithBackgroundColor(SKColors.Yellow)
            .WithClearColor(SKColors.Red)
            .ToBitmap();

        await Assert.That(microBitmap.GetPixel(0, 0)).IsEquivalentTo(SKColors.Yellow);
        await Assert.That(rmqrBitmap.GetPixel(0, 0)).IsEquivalentTo(SKColors.Red);
    }

    private static (int canvasSide, int origin) Layout(int symbolSize)
    {
        var contentSide = symbolSize * ModulePixelSize;
        var canvasSide = contentSide + 40;
        return (canvasSide, (canvasSide - contentSide) / 2);
    }
}
