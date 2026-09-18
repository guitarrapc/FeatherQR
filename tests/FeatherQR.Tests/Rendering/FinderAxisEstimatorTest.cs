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
        var threshold = Binarizer.ComputeOtsuThreshold(luminance);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates);
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
}
