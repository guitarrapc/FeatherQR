using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A finder's module size along a line from sub-pixel edges: where the luminance interpolated along the line crosses halfway between the dark and light levels.
/// </summary>
public class FinderAxisSubPixelTest
{
    public static IEnumerable<(float, float, float)> Renders()
    {
        yield return (2.3f, 17f, 0.15f);
        yield return (2.6f, 45f, 0f);
        yield return (3.1f, 200f, 0.1f);
        yield return (4.6f, 290f, 0.2f);
    }

    /// <summary>Within 2 % of the size the render drew along each of the four finder-to-finder measurements.</summary>
    [Test]
    [MethodDataSource(nameof(Renders))]
    public async Task SubPixelEdges_MeasureTheDrawnSizeAlongTheFinderLines(float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, width, height, lines) = Render(pixelsPerModule, degrees, keystone);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        foreach (var (x, y, directionX, directionY, drawn) in lines)
        {
            var measured = FinderAxisEstimator.MeasureAxisAtLevel(luminance, width, height, grey.Midpoint, x, y, directionX, directionY, 6f * pixelsPerModule);
            await Assert.That(Math.Abs(measured / drawn - 1f)).IsLessThan(0.02f).Because($"measured {measured} against {drawn} drawn");
        }
    }

    /// <summary>Whole-pixel runs miss that bound on these renders, so the bound is one only the sub-pixel edges meet.</summary>
    [Test]
    public async Task WholePixelRuns_MissTheBoundTheSubPixelEdgesMeet()
    {
        var worst = 0f;
        foreach (var (pixelsPerModule, degrees, keystone) in Renders())
        {
            var (luminance, width, height, lines) = Render(pixelsPerModule, degrees, keystone);
            var threshold = Binarizer.ComputeOtsuThreshold(luminance, out _);
            foreach (var (x, y, directionX, directionY, drawn) in lines)
                worst = Math.Max(worst, Math.Abs(FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, x, y, directionX, directionY) / drawn - 1f));
        }

        await Assert.That(worst).IsGreaterThan(0.02f);
    }

    [Test]
    public async Task Walk_LeavingTheImageOrStartingOnLight_IsNaN()
    {
        var (luminance, width, height, lines) = Render(3.1f, 200f, 0.1f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var (x, y, directionX, directionY, _) = lines[0];

        var shortWalk = FinderAxisEstimator.MeasureAxisAtLevel(luminance, width, height, grey.Midpoint, x, y, directionX, directionY, 2f * 3.1f);
        var onLight = FinderAxisEstimator.MeasureAxisAtLevel(luminance, width, height, grey.Midpoint, 1f, 1f, 1f, 0f, float.PositiveInfinity);

        await Assert.That(float.IsNaN(shortWalk)).IsTrue();
        await Assert.That(float.IsNaN(onLight)).IsTrue();
    }

    /// <summary>A version 7 symbol and, for each measurement, the finder centre, the unit direction toward the other finder, and the size drawn along it there.</summary>
    private static (byte[] Luminance, int Width, int Height, (float X, float Y, float DirectionX, float DirectionY, float Drawn)[] Lines) Render(float pixelsPerModule, float degrees, float keystone)
    {
        var qr = QRCodeGenerator.Create("FQR SUBPIXEL 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(7), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, keystone);
        var far = dimension - 3.5f;
        return (luminance, width, height,
        [
            Line(truth, 3.5f, 3.5f, far, 3.5f),
            Line(truth, far, 3.5f, 3.5f, 3.5f),
            Line(truth, 3.5f, 3.5f, 3.5f, far),
            Line(truth, 3.5f, far, 3.5f, 3.5f),
        ]);
    }

    private static (float, float, float, float, float) Line(in PerspectiveTransform truth, float u, float v, float towardU, float towardV)
    {
        truth.Transform(u, v, out var x, out var y);
        truth.Transform(towardU, towardV, out var otherX, out var otherY);
        var length = MathF.Sqrt((otherX - x) * (otherX - x) + (otherY - y) * (otherY - y));
        var du = Math.Sign(towardU - u) * 0.5f;
        var dv = Math.Sign(towardV - v) * 0.5f;
        truth.Transform(u - du, v - dv, out var x0, out var y0);
        truth.Transform(u + du, v + dv, out var x1, out var y1);
        var drawn = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        return (x, y, (otherX - x) / length, (otherY - y) / length, drawn);
    }
}
