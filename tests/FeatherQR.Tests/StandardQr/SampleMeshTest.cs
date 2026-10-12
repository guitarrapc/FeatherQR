using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The sampling mesh over the alignment lattice: built from version 7, with each node where the render drew its lattice position, the patterns on the timing lines and the corner nodes beside the finders among them.
/// </summary>
/// <remarks>
/// A decode cannot pin the mesh: when the mesh read fails the decoder resamples through the global homography, which is exact on a render in perspective, so misplaced nodes still decode. The nodes are held to the render's own map instead.
/// </remarks>
public class SampleMeshTest
{
    // A node taken from a neighbouring mark, or left where a plane puts it on a bent render, is a module or more off
    private const float Tolerance = 0.5f;

    /// <summary>Every node of lattices of 3 to 7 coordinates: the interior ones and the ones on the timing lines found, the three corner nodes placed from them.</summary>
    [Test]
    [Arguments(7, 0.15f, 20f, 5f)]
    [Arguments(10, 0.1f, 213f, 4f)]
    [Arguments(13, 0.15f, 110f, 3.5f)]
    [Arguments(14, 0.1f, 20f, 5f)]
    [Arguments(24, 0.1f, 213f, 4f)]
    [Arguments(30, 0.15f, 20f, 4f)]
    [Arguments(40, 0.15f, 20f, 3.7f)]
    public async Task Mesh_OnAPlaneInPerspective_PutsEveryNodeWhereTheRenderDrewIt(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var (luminance, width, height, dimension, truth) = Render(version, keystone, degrees, pixelsPerModule);
        var nodeXs = new float[49];
        var nodeYs = new float[49];
        var built = BuildMesh(luminance, width, height, dimension, truth, nodeXs, nodeYs, out var meshSize, out var searchedNodes, out var foundNodes);

        var lattice = LatticeCoordinates(version);
        await Assert.That(built).IsTrue();
        await Assert.That(meshSize).IsEqualTo(lattice.Length);
        await Assert.That(searchedNodes).IsEqualTo((meshSize - 1) * (meshSize - 1));
        await Assert.That(foundNodes).IsEqualTo(searchedNodes);

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

    /// <summary>Versions 2 to 6 have a single node with a pattern, the bottom-right one the four-point transform already uses; no alignment pattern is searched.</summary>
    [Test]
    [Arguments(2)]
    [Arguments(6)]
    public async Task Mesh_BelowVersion7_IsNotBuilt(int version)
    {
        var (luminance, width, height, dimension, truth) = Render(version, 0.1f, 20f, 5f);
        var built = BuildMesh(luminance, width, height, dimension, truth, new float[49], new float[49], out var meshSize, out var searchedNodes, out _);

        await Assert.That(built).IsFalse();
        await Assert.That(meshSize).IsEqualTo(0);
        await Assert.That(searchedNodes).IsEqualTo(0);
    }

    /// <summary>The mesh is kept when half the interior nodes are found: two of a version 10 symbol's four interior alignment patterns painted over leave it built, three do not.</summary>
    [Test]
    [Arguments(2, true)]
    [Arguments(3, false)]
    public async Task Mesh_WithInteriorPatternsPaintedOver_IsKeptWhenHalfAreFound(int paintedOver, bool expected)
    {
        const int Version = 10;
        var qr = QRCodeGenerator.Create("FQR MESH 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(Version), QuietZoneSize = 0 });
        var lattice = LatticeCoordinates(Version);
        // Interior nodes in search order: (1, 1), (2, 1), (1, 2), (2, 2); the first ones painted light
        var centres = new[] { (lattice[1], lattice[1]), (lattice[2], lattice[1]), (lattice[1], lattice[2]), (lattice[2], lattice[2]) };
        bool IsDark(int row, int column)
        {
            for (var k = 0; k < paintedOver; k++)
            {
                var (u, v) = centres[k];
                if (Math.Abs(column + 0.5f - u) <= 2.5f && Math.Abs(row + 0.5f - v) <= 2.5f)
                    return false;
            }
            return qr[row, column];
        }
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, dimension, dimension, 5f, 20f, 0.1f);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, 5f, 20f, 0.1f);

        var built = BuildMesh(luminance, width, height, dimension, truth, new float[49], new float[49], out _, out var searchedNodes, out var foundNodes);

        await Assert.That(searchedNodes).IsEqualTo(4);
        await Assert.That(foundNodes).IsEqualTo(4 - paintedOver);
        await Assert.That(built).IsEqualTo(expected);
    }

    /// <summary>A symbol bowed three and a half modules at its middle puts the middle nodes further from where the frame and their neighbours put them than the tight window reaches; the wider one finds them, each where the render drew it.</summary>
    [Test]
    [Arguments(11, 3.739f, 324.42f, 3.480f)]
    [Arguments(13, 5.757f, 253.34f, 3.389f)]
    public async Task Mesh_OnADeeplyBowedSymbol_FindsTheNodesPastTheTightWindow(int version, float pixelsPerModule, float degrees, float bowModules)
    {
        var qr = QRCodeGenerator.Create($"FQR LATTICE v{version} 0123456789", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, side) = BowedRenderer.Render(qr, pixelsPerModule, degrees, bowModules);
        FinderPattern AtBowed(float u, float v)
        {
            var (x, y) = BowedRenderer.ToPixel(dimension, pixelsPerModule, degrees, bowModules, side, u, v);
            return new FinderPattern { X = x, Y = y, ModuleSize = pixelsPerModule, Count = 2 };
        }
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var topLeft = AtBowed(3.5f, 3.5f);
        var topRight = AtBowed(dimension - 3.5f, 3.5f);
        var bottomLeft = AtBowed(3.5f, dimension - 3.5f);
        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, side, side, threshold, grey, topLeft, topRight, bottomLeft);
        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);
        var gridCoords = new float[7];
        var nodeXs = new float[49];
        var nodeYs = new float[49];

        var built = QRImageDecoder.TryBuildLatticeMesh(luminance, side, side, threshold, grey, frame, dimension, gridCoords, nodeXs, nodeYs, out _, out _, out var foundNodes);

        await Assert.That(built).IsTrue();
        await Assert.That(foundNodes).IsEqualTo(4);
        var worst = 0f;
        for (var node = 0; node < 9; node++)
        {
            if (node % 3 == 0 || node / 3 == 0)
                continue;
            var (x, y) = BowedRenderer.ToPixel(dimension, pixelsPerModule, degrees, bowModules, side, gridCoords[node % 3], gridCoords[node / 3]);
            worst = MathF.Max(worst, MathF.Sqrt((nodeXs[node] - x) * (nodeXs[node] - x) + (nodeYs[node] - y) * (nodeYs[node] - y)) / pixelsPerModule);
        }
        await Assert.That(worst).IsLessThan(Tolerance);
    }

    private static (byte[] Luminance, int Width, int Height, int Dimension, PerspectiveTransform Truth) Render(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR MESH 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, keystone);
        return (luminance, width, height, dimension, SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, keystone));
    }

    /// <summary>The mesh from the true finder centres and the frame the decoder builds on them: its module sizes measured along the finder lines.</summary>
    private static bool BuildMesh(byte[] luminance, int width, int height, int dimension, in PerspectiveTransform truth, float[] nodeXs, float[] nodeYs, out int meshSize, out int searchedNodes, out int foundNodes)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var moduleSize = SizeAlong(truth, 3.5f, 3.5f, 1f, 0f);
        var topLeft = At(truth, 3.5f, 3.5f, moduleSize);
        var topRight = At(truth, dimension - 3.5f, 3.5f, moduleSize);
        var bottomLeft = At(truth, 3.5f, dimension - 3.5f, moduleSize);
        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);
        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);
        return QRImageDecoder.TryBuildLatticeMesh(luminance, width, height, threshold, grey, frame, dimension, new float[7], nodeXs, nodeYs, out meshSize, out searchedNodes, out foundNodes);
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
