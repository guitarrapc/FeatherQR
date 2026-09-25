using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A concentric pattern's centre from the darkness of its centre square, against the centre it was drawn at, and the cases it must refuse.
/// </summary>
public class ConcentricCentroidTest
{
    /// <summary>A finder pattern alone on paper: 7 × 7 modules, the centre at (7.5, 7.5) of a 15-module grid.</summary>
    private static bool IsFinderDark(int row, int column)
    {
        var r = row - 4;
        var c = column - 4;
        if (r < 0 || c < 0 || r > 6 || c > 6)
            return false;
        var ring = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
        return ring != 2;
    }

    public static IEnumerable<(float, float, float)> ScalesAndOffsets()
    {
        foreach (var pixelsPerModule in new[] { 1.25f, 1.4f, 1.6f, 1.85f, 2.3f, 3.7f })
        {
            foreach (var offsetX in new[] { 0.05f, 0.4f, 0.75f })
            {
                foreach (var offsetY in new[] { 0.2f, 0.65f })
                    yield return (pixelsPerModule, offsetX, offsetY);
            }
        }
    }

    /// <summary>A start half a pixel off in both axes, the error a thresholded run leaves, comes back to within a tenth of a module.</summary>
    [Test]
    [MethodDataSource(nameof(ScalesAndOffsets))]
    public async Task TryRefine_AntiAliasedFinder_WithinATenthOfAModule(float pixelsPerModule, float offsetX, float offsetY)
    {
        var (luminance, width, height) = AntiAliasedRenderer.Render(IsFinderDark, 15, 15, pixelsPerModule, offsetX, offsetY);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var trueX = offsetX + 7.5f * pixelsPerModule;
        var trueY = offsetY + 7.5f * pixelsPerModule;
        var x = trueX + 0.5f;
        var y = trueY - 0.5f;

        var refined = ConcentricCentroid.TryRefine(luminance, width, height, grey, pixelsPerModule, 0f, 0f, pixelsPerModule, 2f, 9f, 0.75f, ref x, ref y, out var darkArea);

        await Assert.That(refined).IsTrue();
        await Assert.That(Math.Abs(x - trueX) / pixelsPerModule).IsLessThan(0.1f);
        await Assert.That(Math.Abs(y - trueY) / pixelsPerModule).IsLessThan(0.1f);
        await Assert.That(darkArea / (pixelsPerModule * pixelsPerModule)).IsBetween(8f, 10f);
    }

    /// <summary>The window follows the symbol's axes, so a turned pattern refines as well as an upright one.</summary>
    [Test]
    [Arguments(2.2f, 17f)]
    [Arguments(2.6f, 45f)]
    [Arguments(3.1f, 71f)]
    public async Task TryRefine_TurnedFinder_WithinATenthOfAModule(float pixelsPerModule, float degrees)
    {
        var (luminance, width, height) = SupersampledRenderer.Render(IsFinderDark, 15, 15, pixelsPerModule, degrees);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var radians = degrees * MathF.PI / 180f;
        var uX = MathF.Cos(radians) * pixelsPerModule;
        var uY = MathF.Sin(radians) * pixelsPerModule;
        // The renderer turns the grid about the image centre, where the pattern's centre is
        var trueX = width / 2f;
        var trueY = height / 2f;
        var x = trueX - 0.5f;
        var y = trueY + 0.5f;

        var refined = ConcentricCentroid.TryRefine(luminance, width, height, grey, uX, uY, -uY, uX, 2f, 9f, 0.75f, ref x, ref y, out _);

        await Assert.That(refined).IsTrue();
        await Assert.That(MathF.Sqrt((x - trueX) * (x - trueX) + (y - trueY) * (y - trueY)) / pixelsPerModule).IsLessThan(0.1f);
    }

    /// <summary>Without grey levels a pixel is wholly dark or light, and the centroid of whole pixels is no finer than the runs.</summary>
    [Test]
    public async Task TryRefine_TwoLevelImage_LeavesThePointAlone()
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render(IsFinderDark, 15, 15, 1.4f, 0.3f, 0.3f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsFalse();
        var x = 11f;
        var y = 11f;

        var refined = ConcentricCentroid.TryRefine(luminance, width, height, grey, 1.4f, 0f, 0f, 1.4f, 2f, 9f, 0.75f, ref x, ref y, out _);

        await Assert.That(refined).IsFalse();
        await Assert.That(x).IsEqualTo(11f);
        await Assert.That(y).IsEqualTo(11f);
    }

    /// <summary>
    /// The dark area the window holds has to be about the pattern's: a solid dark area fills the window (16 modules against 9), a lone module leaves it nearly empty (1 against 9).
    /// Either way the point stays where it was.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TryRefine_DarkAreaNotThePatterns_Refused(bool solid)
    {
        const float pixelsPerModule = 1.6f;
        var (luminance, width, height) = AntiAliasedRenderer.Render(solid ? (row, column) => row is >= 3 and < 12 && column is >= 3 and < 12 : (row, column) => row == 7 && column == 7, 15, 15, pixelsPerModule, 0.3f, 0.3f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var startX = 0.3f + 7.5f * pixelsPerModule;
        var startY = startX;
        var x = startX;
        var y = startY;

        var refined = ConcentricCentroid.TryRefine(luminance, width, height, grey, pixelsPerModule, 0f, 0f, pixelsPerModule, 2f, 9f, 0.75f, ref x, ref y, out var darkArea);

        await Assert.That(refined).IsFalse();
        await Assert.That(darkArea).IsEqualTo(0f);
        await Assert.That(x).IsEqualTo(startX);
        await Assert.That(y).IsEqualTo(startY);
    }

    /// <summary>A centroid further from the start than the bound allows is refused: a start that far off was not on this pattern.</summary>
    [Test]
    public async Task TryRefine_CentreFurtherThanTheBound_Refused()
    {
        const float pixelsPerModule = 1.6f;
        var (luminance, width, height) = AntiAliasedRenderer.Render(IsFinderDark, 15, 15, pixelsPerModule, 0.3f, 0.3f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var startX = 0.3f + 8.3f * pixelsPerModule;
        var startY = 0.3f + 7.5f * pixelsPerModule;

        var x = startX;
        var y = startY;
        var refused = ConcentricCentroid.TryRefine(luminance, width, height, grey, pixelsPerModule, 0f, 0f, pixelsPerModule, 2f, 9f, 0.5f, ref x, ref y, out _);
        await Assert.That(refused).IsFalse();
        await Assert.That(x).IsEqualTo(startX);

        // The same start inside the bound moves to the centre
        var allowed = ConcentricCentroid.TryRefine(luminance, width, height, grey, pixelsPerModule, 0f, 0f, pixelsPerModule, 2f, 9f, 1f, ref x, ref y, out _);
        await Assert.That(allowed).IsTrue();
        await Assert.That(Math.Abs(x - (0.3f + 7.5f * pixelsPerModule)) / pixelsPerModule).IsLessThan(0.1f);
    }

    /// <summary>Paper alone has no dark area to take a centroid of.</summary>
    [Test]
    public async Task TryRefine_NoDarkPixels_Refused()
    {
        var (luminance, width, height) = AntiAliasedRenderer.Render(IsFinderDark, 15, 15, 1.6f, 0.3f, 0.3f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var x = 2f;
        var y = 2f;

        await Assert.That(ConcentricCentroid.TryRefine(luminance, width, height, grey, 1.6f, 0f, 0f, 1.6f, 1f, 9f, 0.75f, ref x, ref y, out _)).IsFalse();
    }
}
