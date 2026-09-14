using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The three rounded shapes refuse a corner radius they cannot draw.
/// A non-finite radius reaches Skia, which degrades the round rect to a plain rectangle, so the
/// caller asks for rounded corners and silently gets square ones.
/// </summary>
public class RoundedShapeCornerRadiusGuardTest
{
    [Test]
    [Arguments(float.NaN)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(float.NegativeInfinity)]
    [Arguments(-0.01f)]
    [Arguments(1.01f)]
    public async Task RoundedRectangleModuleShape_RadiusOutsideItsRange_Throws(float cornerRadiusPercent)
    {
        var thrown = await Assert.That(() => new RoundedRectangleModuleShape(cornerRadiusPercent))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(thrown!.ParamName).IsEqualTo("cornerRadiusPercent");
    }

    [Test]
    [Arguments(float.NaN)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(float.NegativeInfinity)]
    [Arguments(-0.01f)]
    [Arguments(1.01f)]
    public async Task RoundedRectangleFinderPatternShape_RadiusOutsideItsRange_Throws(float cornerRadiusPercent)
    {
        var thrown = await Assert.That(() => new RoundedRectangleFinderPatternShape(cornerRadiusPercent))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(thrown!.ParamName).IsEqualTo("cornerRadiusPercent");
    }

    [Test]
    [Arguments(float.NaN)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(float.NegativeInfinity)]
    [Arguments(-0.01f)]
    [Arguments(1.01f)]
    public async Task RoundedRectangleCircleFinderPatternShape_RadiusOutsideItsRange_Throws(float cornerRadiusPercent)
    {
        var thrown = await Assert.That(() => new RoundedRectangleCircleFinderPatternShape(cornerRadiusPercent))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(thrown!.ParamName).IsEqualTo("cornerRadiusPercent");
    }

    [Test]
    [Arguments(0f)]
    [Arguments(0.3f)]
    [Arguments(1f)]
    public async Task RoundedShapes_RadiusAtOrInsideItsRange_Constructs(float cornerRadiusPercent)
    {
        await Assert.That(() => new RoundedRectangleModuleShape(cornerRadiusPercent)).ThrowsNothing();
        await Assert.That(() => new RoundedRectangleFinderPatternShape(cornerRadiusPercent)).ThrowsNothing();
        await Assert.That(() => new RoundedRectangleCircleFinderPatternShape(cornerRadiusPercent)).ThrowsNothing();
    }

    /// <summary>
    /// The premise the guard rests on: a rounded module is visibly not a square one, so the
    /// rectangle a non-finite radius used to produce was a silent downgrade rather than a no-op.
    /// </summary>
    [Test]
    public async Task ARealRadius_DrawsSomethingOtherThanASquare()
    {
        var square = DarkSamples(RectangleModuleShape.Default);
        var rounded = DarkSamples(new RoundedRectangleModuleShape(0.3f));

        await Assert.That(rounded).IsNotEqualTo(square);
    }

    private const int Side = 56;

    private static int DarkSamples(ModuleShape shape)
    {
        using var bitmap = new SKBitmap(Side, Side);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = shape.RequiresAntialiasing };
        shape.Draw(canvas, SKRect.Create(0, 0, Side, Side), paint);

        var dark = 0;
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                if (bitmap.GetPixel(x, y).Red < 128) dark++;
            }
        }
        return dark;
    }
}
