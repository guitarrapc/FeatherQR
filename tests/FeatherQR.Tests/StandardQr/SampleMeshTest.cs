using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The piecewise sampling mesh over the alignment lattice: built from version 14, whose 4×4 lattice gives every edge line three nodes to extrapolate through, with each node where the render drew its lattice position.
/// </summary>
/// <remarks>
/// A decode cannot pin the mesh: when the mesh read fails the decoder resamples through the global homography, which is exact on a render in perspective, so misplaced nodes still decode. The nodes are held to the render's own map instead.
/// </remarks>
public class SampleMeshTest
{
    // A misplaced edge line puts its nodes a module or more off on these renders
    private const float Tolerance = 0.5f;

    /// <summary>Detected nodes, and the coordinate-6 row and column extrapolated from them, finder corners included, for lattices of 4 to 7 coordinates.</summary>
    [Test]
    [Arguments(14, 0.1f, 20f, 5f)]
    [Arguments(24, 0.1f, 213f, 4f)]
    [Arguments(30, 0.15f, 20f, 4f)]
    [Arguments(40, 0.15f, 20f, 3.7f)]
    public async Task Mesh_OnAPlaneInPerspective_PutsEveryNodeWhereTheRenderDrewIt(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var (luminance, width, height, dimension, truth) = Render(version, keystone, degrees, pixelsPerModule);
        var nodeXs = new float[49];
        var nodeYs = new float[49];
        var built = BuildMesh(luminance, width, height, dimension, truth, nodeXs, nodeYs, out var meshSize, out _);

        var lattice = LatticeCoordinates(version);
        await Assert.That(built).IsTrue();
        await Assert.That(meshSize).IsEqualTo(lattice.Length);

        var worst = 0f;
        var worstNode = 0;
        for (var j = 0; j < meshSize; j++)
        {
            for (var i = 0; i < meshSize; i++)
            {
                var node = j * meshSize + i;
                var off = ModulesOff(truth, lattice[i], lattice[j], nodeXs[node], nodeYs[node]);
                if (!(off <= worst))
                {
                    worst = off;
                    worstNode = node;
                }
            }
        }
        await Assert.That(worst).IsLessThan(Tolerance).Because($"node ({worstNode % meshSize}, {worstNode / meshSize}) is {worst:F3} modules off");
    }

    /// <summary>Below the 4×4 lattice there are not three nodes per edge line to extrapolate through, and the global homography samples instead; no alignment pattern is searched.</summary>
    [Test]
    [Arguments(2)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(13)]
    public async Task Mesh_BelowVersion14_IsNotBuilt(int version)
    {
        var (luminance, width, height, dimension, truth) = Render(version, 0.1f, 20f, 5f);
        var built = BuildMesh(luminance, width, height, dimension, truth, new float[49], new float[49], out var meshSize, out var searchedNodes);

        await Assert.That(built).IsFalse();
        await Assert.That(meshSize).IsEqualTo(0);
        await Assert.That(searchedNodes).IsEqualTo(0);
    }

    private static (byte[] Luminance, int Width, int Height, int Dimension, PerspectiveTransform Truth) Render(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR MESH 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, keystone);
        return (luminance, width, height, dimension, SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, keystone));
    }

    /// <summary>The mesh from the true finder centres and the mean of the true module sizes along the two finder lines, the decoder's own inputs on a clean read.</summary>
    private static bool BuildMesh(byte[] luminance, int width, int height, int dimension, in PerspectiveTransform truth, float[] nodeXs, float[] nodeYs, out int meshSize, out int searchedNodes)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var moduleSize = (SizeAlong(truth, 3.5f, 3.5f, 1f, 0f) + SizeAlong(truth, 3.5f, 3.5f, 0f, 1f) + SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f) + SizeAlong(truth, 3.5f, dimension - 3.5f, 0f, 1f)) / 4f;
        return QRImageDecoder.TryBuildSampleMesh(luminance, width, height, threshold, grey, At(truth, 3.5f, 3.5f, moduleSize), At(truth, dimension - 3.5f, 3.5f, moduleSize), At(truth, 3.5f, dimension - 3.5f, moduleSize), dimension, moduleSize, new float[7], nodeXs, nodeYs, out meshSize, out searchedNodes, out _);
    }

    /// <summary>Module centres of the alignment lattice rows and columns (ISO/IEC 18004 Annex E).</summary>
    private static float[] LatticeCoordinates(int version)
    {
        var coordinates = new List<float>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
        {
            if (value != 0)
                coordinates.Add(value + 0.5f);
        }
        return [.. coordinates];
    }

    private static FinderPattern At(in PerspectiveTransform truth, float u, float v, float moduleSize)
    {
        truth.Transform(u, v, out var x, out var y);
        return new FinderPattern { X = x, Y = y, ModuleSize = moduleSize, Count = 2 };
    }

    /// <summary>How far (<paramref name="x"/>, <paramref name="y"/>) is from where the render drew grid point (<paramref name="u"/>, <paramref name="v"/>), in modules there.</summary>
    private static float ModulesOff(in PerspectiveTransform truth, float u, float v, float x, float y)
    {
        truth.Transform(u, v, out var expectedX, out var expectedY);
        return MathF.Sqrt((x - expectedX) * (x - expectedX) + (y - expectedY) * (y - expectedY)) / SizeAlong(truth, u, v, 1f, 0f);
    }

    /// <summary>The render's pixels per module at (<paramref name="u"/>, <paramref name="v"/>) along the grid direction given.</summary>
    private static float SizeAlong(in PerspectiveTransform truth, float u, float v, float du, float dv)
    {
        truth.Transform(u - 0.5f * du, v - 0.5f * dv, out var x0, out var y0);
        truth.Transform(u + 0.5f * du, v + 0.5f * dv, out var x1, out var y1);
        return MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
    }
}
