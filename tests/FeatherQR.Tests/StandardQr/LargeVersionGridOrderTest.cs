using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Which grid reads a version 14+ symbol: the four-point transform when the bottom-right alignment pattern anchors it, since a plane in perspective is one projective map that four correct points fix; the mesh over the alignment lattice when nothing anchors that corner, and after an anchored grid that does not read.
/// </summary>
public class LargeVersionGridOrderTest
{
    private static QRCodeData Symbol(int version)
        => QRCodeGenerator.Create($"FQR MESH ORDER v{version} 0123456789", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });

    /// <summary>
    /// An intact symbol in perspective reads through the anchored transform with no corrections.
    /// The mesh reads the same render with corrections, from interpolating between measured nodes, so a read without any is the transform's.
    /// </summary>
    [Test]
    [Arguments(14, 4f, 20f, 0.16f)]
    [Arguments(17, 5f, 20f, 0.16f)]
    [Arguments(20, 3.5f, 20f, 0.12f)]
    [Arguments(25, 4f, 200f, 0.16f)]
    public async Task AnchoredIntactSymbol_ReadsThroughTheFourPointTransform(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var qr = Symbol(version);
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, degrees, keystone);

        // Premise: the grid choice shows in the corrections
        var truth = SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, degrees, keystone);
        await Assert.That(MeshCorrections(luminance, width, height, qr.Size, truth)).IsGreaterThan(0);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo($"FQR MESH ORDER v{version} 0123456789");
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    /// <summary>
    /// A nearly flat symbol whose bottom-right alignment pattern is lost reads through the mesh, anchored on the rest of the lattice, and reports its corners from it.
    /// The finders' parallelogram, the grid that would otherwise come first, reads such a symbol too but misplaces its far corner by more than half a module.
    /// </summary>
    [Test]
    [Arguments(35, 5f, 200f)]
    [Arguments(40, 4f, 20f)]
    public async Task LostAlignmentPattern_ReadsThroughTheMeshFirst(int version, float pixelsPerModule, float degrees)
    {
        const float Keystone = 0.004f;
        var qr = Symbol(version);
        var (luminance, width, height) = SupersampledRenderer.Render(WithoutBottomRightAlignment(qr), qr.Size, qr.Size, pixelsPerModule, degrees, Keystone);
        var truth = SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, degrees, Keystone);

        // Premise: the parallelogram through the true finder centres puts a corner past the bound
        var moduleSize = SizeAlong(truth, 3.5f, 3.5f, 1f, 0f);
        var parallelogram = QRImageDecoder.BuildParallelogramTransform(At(truth, 3.5f, 3.5f, moduleSize), At(truth, qr.Size - 3.5f, 3.5f, moduleSize), At(truth, 3.5f, qr.Size - 3.5f, moduleSize), qr.Size);
        await Assert.That(WorstCornerModules(truth, SymbolGeometry.FromTransform(parallelogram, qr.Size, qr.Size, transposed: false), qr.Size)).IsGreaterThan(0.25f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo($"FQR MESH ORDER v{version} 0123456789");
        await Assert.That(WorstCornerModules(truth, info.Corners, qr.Size)).IsLessThan(0.25f);
    }

    /// <summary>
    /// A symbol bowed off the plane is anchored like a flat one, but no single projective map reads it; the mesh, tried after the anchored transform fails, follows the bow through the lattice.
    /// </summary>
    [Test]
    [Arguments(14, 5f, 0f, 1f)]
    [Arguments(25, 4f, 20f, 2f)]
    [Arguments(40, 3.5f, 200f, 1.5f)]
    public async Task BowedSymbol_ReadsThroughTheMeshAfterTheAnchoredTransform(int version, float pixelsPerModule, float degrees, float bowModules)
    {
        var qr = Symbol(version);
        var (luminance, side) = RenderBowed(qr, pixelsPerModule, degrees, bowModules);

        // Premise: the transform through the true finder centres and the true bottom-right alignment centre does not read it
        var threshold = Binarizer.ComputeOtsuThreshold(luminance);
        var d = qr.Size;
        var (x0, y0) = BowedToPixel(d, pixelsPerModule, degrees, bowModules, side, 3.5f, 3.5f);
        var (x1, y1) = BowedToPixel(d, pixelsPerModule, degrees, bowModules, side, d - 3.5f, 3.5f);
        var (x2, y2) = BowedToPixel(d, pixelsPerModule, degrees, bowModules, side, d - 6.5f, d - 6.5f);
        var (x3, y3) = BowedToPixel(d, pixelsPerModule, degrees, bowModules, side, 3.5f, d - 3.5f);
        var fourPoint = PerspectiveTransform.QuadrilateralToQuadrilateral(3.5f, 3.5f, d - 3.5f, 3.5f, d - 6.5f, d - 6.5f, 3.5f, d - 3.5f, x0, y0, x1, y1, x2, y2, x3, y3);
        var modules = new byte[d * d];
        QRImageDecoder.SampleGridScalar(luminance, side, side, threshold, fourPoint, d, modules);
        await Assert.That(QRMatrixDecoder.DecodeMatrix(modules, d, new char[QRCodeDecoder.GetMaxDecodedLength(40)], out _, out _)).IsNotEqualTo(DecodeStatus.Success);

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo($"FQR MESH ORDER v{version} 0123456789");
    }

    /// <summary>The mesh after an anchored transform is one more grid, not a looser read: an anchored symbol in perspective whose data is destroyed, alignment lattice kept, does not decode.</summary>
    [Test]
    public async Task AnchoredSymbolWithItsDataDestroyed_DoesNotDecode()
    {
        var qr = Symbol(25);
        var lattice = new List<int>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice((25 - 1) * 7, 7))
        {
            if (value != 0)
                lattice.Add(value);
        }
        bool NearLattice(int row, int column) => lattice.Exists(r => Math.Abs(row - r) <= 2) && lattice.Exists(c => Math.Abs(column - c) <= 2);
        // Every module between the finders' rows and columns but the alignment patterns, clear of both format and both version copies
        bool IsDark(int row, int column) => row >= 9 && column >= 9 && row < qr.Size - 11 && column < qr.Size - 11 && !NearLattice(row, column) ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, qr.Size, qr.Size, 4f, 20f, 0.12f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    /// <summary>The symbol with its bottom-right alignment pattern painted over light.</summary>
    private static Func<int, int, bool> WithoutBottomRightAlignment(QRCodeData qr)
    {
        var centre = qr.Size - 7;
        return (row, column) => (Math.Abs(row - centre) > 2 || Math.Abs(column - centre) > 2) && qr[row, column];
    }

    /// <summary>How far the worst of the four reported corners is from the drawn one, in modules there.</summary>
    private static float WorstCornerModules(in PerspectiveTransform truth, SymbolCorners corners, int size)
    {
        ImagePoint[] points = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
        (float U, float V)[] grid = [(0f, 0f), (size, 0f), (size, size), (0f, size)];
        var worst = 0f;
        for (var i = 0; i < 4; i++)
        {
            truth.Transform(grid[i].U, grid[i].V, out var x, out var y);
            var off = MathF.Sqrt((points[i].X - x) * (points[i].X - x) + (points[i].Y - y) * (points[i].Y - y)) / SizeAlong(truth, grid[i].U, grid[i].V, 1f, 0f);
            worst = MathF.Max(worst, off);
        }
        return worst;
    }

    /// <summary>
    /// The symbol, with a 4-module quiet zone, with its rows bent into arcs that sag <paramref name="bowModules"/> at the middle column, then turned about the centre of a square canvas; 2×2 supersampled.
    /// </summary>
    private static (byte[] Luminance, int Side) RenderBowed(QRCodeData qr, float pixelsPerModule, float degrees, float bowModules)
    {
        var span = qr.Size + 8;
        var side = (int)((span + 2 * bowModules) * pixelsPerModule * 1.45f) + 8;
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);
        var radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        var centre = side / 2.0;
        var half = span * pixelsPerModule / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        double px = x + 0.25 + sx * 0.5 - centre, py = y + 0.25 + sy * 0.5 - centre;
                        // Undo the turn, then the bow
                        var s = (px * cos + py * sin + half) / pixelsPerModule;
                        var t = (-px * sin + py * cos + half) / pixelsPerModule - bowModules * Math.Sin(Math.PI * s / span);
                        var column = (int)Math.Floor(s) - 4;
                        var row = (int)Math.Floor(t) - 4;
                        if (row >= 0 && column >= 0 && row < qr.Size && column < qr.Size && qr[row, column])
                            dark++;
                    }
                }
                luminance[y * side + x] = (byte)((4 - dark) * 255 / 4);
            }
        }
        return (luminance, side);
    }

    /// <summary>Where <see cref="RenderBowed"/> draws grid point (<paramref name="u"/>, <paramref name="v"/>) of the symbol, quiet zone excluded.</summary>
    private static (float X, float Y) BowedToPixel(int size, float pixelsPerModule, float degrees, float bowModules, int side, float u, float v)
    {
        var span = size + 8;
        var s = u + 4.0;
        var fx = s * pixelsPerModule - span * pixelsPerModule / 2.0;
        var fy = (v + 4.0 + bowModules * Math.Sin(Math.PI * s / span)) * pixelsPerModule - span * pixelsPerModule / 2.0;
        var radians = degrees * Math.PI / 180.0;
        return ((float)(side / 2.0 + fx * Math.Cos(radians) - fy * Math.Sin(radians)), (float)(side / 2.0 + fx * Math.Sin(radians) + fy * Math.Cos(radians)));
    }

    /// <summary>The corrections a mesh built from the true finder centres reads the render with, or -1 when it does not read.</summary>
    private static int MeshCorrections(byte[] luminance, int width, int height, int dimension, in PerspectiveTransform truth)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var moduleSize = (SizeAlong(truth, 3.5f, 3.5f, 1f, 0f) + SizeAlong(truth, 3.5f, 3.5f, 0f, 1f) + SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f) + SizeAlong(truth, 3.5f, dimension - 3.5f, 0f, 1f)) / 4f;
        var gridCoords = new float[7];
        var nodeXs = new float[49];
        var nodeYs = new float[49];
        if (!QRImageDecoder.TryBuildSampleMesh(luminance, width, height, threshold, grey, At(truth, 3.5f, 3.5f, moduleSize), At(truth, dimension - 3.5f, 3.5f, moduleSize), At(truth, 3.5f, dimension - 3.5f, moduleSize), dimension, moduleSize, gridCoords, nodeXs, nodeYs, out var meshSize, out _, out _))
            return -1;

        var modules = new byte[dimension * dimension];
        QRImageDecoder.SampleGridPiecewiseScalar(luminance, width, height, threshold, gridCoords.AsSpan(0, meshSize), nodeXs, nodeYs, meshSize, dimension, modules);
        var status = QRMatrixDecoder.DecodeMatrix(modules, dimension, new char[QRCodeDecoder.GetMaxDecodedLength(40)], out _, out var info);
        return status == DecodeStatus.Success ? info.ErrorsCorrected : -1;
    }

    private static FinderPattern At(in PerspectiveTransform truth, float u, float v, float moduleSize)
    {
        truth.Transform(u, v, out var x, out var y);
        return new FinderPattern { X = x, Y = y, ModuleSize = moduleSize, Count = 2 };
    }

    private static float SizeAlong(in PerspectiveTransform truth, float u, float v, float du, float dv)
    {
        truth.Transform(u - 0.5f * du, v - 0.5f * dv, out var x0, out var y0);
        truth.Transform(u + 0.5f * du, v + 0.5f * dv, out var x1, out var y1);
        return MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
    }
}
