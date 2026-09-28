using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The least-squares homography from point correspondences: exact points give back the map that made them, wherever the symbol lies in the image, and points that do not determine a plane are refused.
/// </summary>
public class ProjectiveFitTest
{
    /// <summary>Image quadrilaterals a 139 × 17 grid is mapped onto: square on, narrowed along either axis, toward a corner, and 40,000 px from the origin.</summary>
    public static IEnumerable<float[]> Quadrilaterals()
    {
        yield return [100f, 100f, 800f, 100f, 800f, 186f, 100f, 186f];
        yield return [180f, 120f, 720f, 120f, 900f, 260f, 20f, 260f];
        yield return [40f, 300f, 900f, 40f, 900f, 520f, 40f, 380f];
        yield return [310f, 80f, 1010f, 250f, 960f, 420f, 250f, 190f];
        yield return [40100f, 20050f, 40900f, 20100f, 40880f, 20230f, 40090f, 20190f];
    }

    [Test]
    [MethodDataSource(nameof(Quadrilaterals))]
    public async Task TrySolve_ExactEdgePoints_ReturnsTheMap(float[] quad)
    {
        var map = GridMap(quad);
        var fit = new ProjectiveFit(quad[0], quad[1], 400d);
        AddEdges(ref fit, map);

        await Assert.That(fit.TrySolve(out var solved)).IsTrue();
        await Assert.That(LargestDistance(map, solved)).IsLessThan(0.01f);
    }

    /// <summary>
    /// 400,000 px from the origin, as on a very wide image: the image side moved and scaled to unit size keeps the equations solvable, and the map comes back to the float precision of the coordinates.
    /// Raw pixel coordinates leave them singular from 4,000 px out.
    /// </summary>
    [Test]
    public async Task TrySolve_FarFromTheOrigin_ReturnsTheMap()
    {
        var quad = new[] { 400180f, 400120f, 400720f, 400120f, 400900f, 400260f, 400020f, 400260f };
        var map = GridMap(quad);
        var fit = new ProjectiveFit(quad[0], quad[1], 400d);
        AddEdges(ref fit, map);

        await Assert.That(fit.TrySolve(out var solved)).IsTrue();
        await Assert.That(LargestDistance(map, solved)).IsLessThan(0.25f);
    }

    /// <summary>The normalization only conditions the solve: any centre and span near the symbol give the same map.</summary>
    [Test]
    [Arguments(500d, 150d, 400d)]
    [Arguments(900d, 20d, 50d)]
    [Arguments(0d, 0d, 1000d)]
    public async Task TrySolve_AnyNormalization_SameMap(double imageX, double imageY, double imageSpan)
    {
        var map = GridMap([180f, 120f, 720f, 120f, 900f, 260f, 20f, 260f]);
        var fit = new ProjectiveFit(imageX, imageY, imageSpan);
        AddEdges(ref fit, map);

        await Assert.That(fit.TrySolve(out var solved)).IsTrue();
        await Assert.That(LargestDistance(map, solved)).IsLessThan(0.01f);
    }

    /// <summary>Points off by up to half a pixel either way average out: the fit lands closer to the map than the worst point.</summary>
    [Test]
    public async Task TrySolve_NoisyPoints_CloserThanTheNoise()
    {
        var map = GridMap([180f, 120f, 720f, 120f, 900f, 260f, 20f, 260f]);
        var fit = new ProjectiveFit(460d, 190d, 400d);
        var random = new Random(3);
        foreach (var (u, v) in EdgeGrid())
        {
            map.Transform(u, v, out var x, out var y);
            fit.Add(u, v, x + (float)(random.NextDouble() - 0.5), y + (float)(random.NextDouble() - 0.5));
        }

        await Assert.That(fit.TrySolve(out var solved)).IsTrue();
        await Assert.That(LargestDistance(map, solved)).IsLessThan(0.25f);
    }

    [Test]
    public async Task TrySolve_ThreePoints_Refused()
    {
        var map = GridMap([100f, 100f, 800f, 100f, 800f, 186f, 100f, 186f]);
        var fit = new ProjectiveFit(450d, 143d, 400d);
        foreach (var (u, v) in new[] { (0f, 0f), (139f, 0f), (139f, 17f) })
        {
            map.Transform(u, v, out var x, out var y);
            fit.Add(u, v, x, y);
        }

        await Assert.That(fit.TrySolve(out _)).IsFalse();
    }

    /// <summary>Every point on one line of the grid: a line does not determine a plane.</summary>
    [Test]
    public async Task TrySolve_OneLine_Refused()
    {
        var map = GridMap([180f, 120f, 720f, 120f, 900f, 260f, 20f, 260f]);
        var fit = new ProjectiveFit(460d, 190d, 400d);
        for (var u = 0; u <= 139; u++)
        {
            map.Transform(u, 0.5f, out var x, out var y);
            fit.Add(u, 0.5f, x, y);
        }

        await Assert.That(fit.TrySolve(out _)).IsFalse();
    }

    private static PerspectiveTransform GridMap(float[] quad)
        => PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, 139f, 0f, 139f, 17f, 0f, 17f, quad[0], quad[1], quad[2], quad[3], quad[4], quad[5], quad[6], quad[7]);

    /// <summary>The points a perimeter trace meets: both long edges and the short one at the finder.</summary>
    private static IEnumerable<(float U, float V)> EdgeGrid()
    {
        for (var u = 8; u <= 139; u++)
        {
            yield return (u, 0.5f);
            yield return (u, 16.5f);
        }
        for (var v = 8; v <= 17; v++)
            yield return (0.5f, v);
    }

    private static void AddEdges(ref ProjectiveFit fit, in PerspectiveTransform map)
    {
        foreach (var (u, v) in EdgeGrid())
        {
            map.Transform(u, v, out var x, out var y);
            fit.Add(u, v, x, y);
        }
    }

    private static float LargestDistance(in PerspectiveTransform expected, in PerspectiveTransform actual)
    {
        var worst = 0f;
        for (var v = 0; v <= 17; v++)
        {
            for (var u = 0; u <= 139; u++)
            {
                expected.Transform(u, v, out var x0, out var y0);
                actual.Transform(u, v, out var x1, out var y1);
                worst = Math.Max(worst, MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
            }
        }
        return worst;
    }
}
