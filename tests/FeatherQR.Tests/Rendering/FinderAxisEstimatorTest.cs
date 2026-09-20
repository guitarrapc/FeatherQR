using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The single-finder axis recovery Micro QR and rMQR rely on under rotation. The first
/// orientation candidate is the axis fitted to the whole angular sweep: the shortest span
/// alone is a flat minimum that pixel noise moved 5-11 degrees at 3 px/module. Its sizes are
/// measured on rays that took no part in choosing it, so they are not the low outlier that
/// won the choice.
/// </summary>
public class FinderAxisEstimatorTest
{
    public static IEnumerable<(int, int)> Rotations()
    {
        foreach (var pixelsPerModule in new[] { 3, 5 })
        {
            for (var degrees = 0; degrees < 90; degrees += 3)
                yield return (pixelsPerModule, degrees);
        }
    }

    [Test]
    [MethodDataSource(nameof(Rotations))]
    public async Task FirstOrientation_IsTheFinderAxis(int pixelsPerModule, int degrees)
    {
        var data = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4, QuietZoneSize = 0 });
        var (luminance, width, height) = SupersampledRenderer.Render((row, col) => data[row, col], data.Size, data.Size, pixelsPerModule, degrees);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        var finder = candidates.Take(count).OrderByDescending(c => c.Count).First();

        var orientations = new OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var found = FinderAxisEstimator.FindOrientationCandidates(luminance, width, height, threshold, finder, orientations);

        await Assert.That(found).IsGreaterThan(0);
        var first = orientations[0];
        var angle = (Math.Atan2(first.UY, first.UX) * 180 / Math.PI % 90 + 90) % 90;
        var error = Math.Abs(angle - degrees % 90);
        await Assert.That(Math.Min(error, 90 - error)).IsLessThanOrEqualTo(1.5).Because($"fitted {angle:F2} deg");
        await Assert.That(first.USize).IsEqualTo(pixelsPerModule).Within(0.05f * pixelsPerModule);
        await Assert.That(first.VSize).IsEqualTo(pixelsPerModule).Within(0.05f * pixelsPerModule);
    }

    /// <summary>
    /// One direction of the walk from the centre of a pixel-aligned finder: the dark ring's inner
    /// edge 2.5 modules out and its outer edge 3.5. The module size pairs a forward walk with a
    /// backward one, so a probe half a pixel off shifts the two the opposite way and cancels there;
    /// each walk on its own does not.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DarkLightDarkRun_PixelAlignedFinder_FindsTheRingEdges(int pixelsPerModule)
    {
        const int quietZone = 2;
        var side = (7 + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var row = y / pixelsPerModule - quietZone;
                var column = x / pixelsPerModule - quietZone;
                var ring = Math.Max(Math.Abs(row - 3), Math.Abs(column - 3));
                luminance[y * side + x] = row is >= 0 and < 7 && column is >= 0 and < 7 && ring != 2 ? (byte)0 : (byte)255;
            }
        }
        var center = (quietZone + 3.5f) * pixelsPerModule;

        foreach (var (dirX, dirY) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
        {
            var found = FinderAxisEstimator.TryDarkLightDarkRun(luminance, side, side, 128, center, center, dirX, dirY, float.PositiveInfinity, out var inner, out var outer);

            await Assert.That(found).IsTrue().Because($"direction ({dirX}, {dirY})");
            await Assert.That(inner).IsEqualTo(2.5f * pixelsPerModule).Within(0.5f).Because($"inner, direction ({dirX}, {dirY})");
            await Assert.That(outer).IsEqualTo(3.5f * pixelsPerModule).Within(0.5f).Because($"outer, direction ({dirX}, {dirY})");
        }
    }
}
