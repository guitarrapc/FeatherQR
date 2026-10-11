using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A version 25 or 30 symbol on a bent card, photographed at a tilt, through blur and sensor noise, with the small modules of a distant shot: the alignment lattice is found, but its coordinate-6 row and column, extrapolated along the lattice lines from nodes that carry the photograph's noise, land far enough out that the mesh through them does not read.
/// The mesh built places those nodes by measurement, the alignment patterns on the timing lines searched around the extrapolation and the finder corners interpolated between the finder centres and the lattice, and reads.
/// The shots were chosen from a sweep of tilts, bends, blurs, noise and distances at levels L and M where the extrapolated mesh fails and the built one reads: the first four read only with the edge nodes searched, the last two with the corners interpolated alone.
/// </summary>
public class MeshEdgeNodeDecodeTest
{
    [Test]
    [Arguments(25, QREccLevel.L, 4f, 15f, 2f, 0f, 0f, 1.5f)]
    [Arguments(25, QREccLevel.L, 3f, 15f, 4f, 0f, 0f, 1.5f)]
    [Arguments(25, QREccLevel.M, 3f, 0f, 4f, 1f, 10f, 2.5f)]
    [Arguments(30, QREccLevel.L, 3f, 15f, 4f, 1f, 10f, 1.5f)]
    [Arguments(25, QREccLevel.L, 3f, 25f, 2f, 0f, 0f, 2.5f)]
    [Arguments(30, QREccLevel.M, 3f, 15f, 2f, 1f, 10f, 2.5f)]
    public async Task BentHighVersionPhotograph_ReadsThroughTheMeasuredEdgeNodes(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance)
    {
        var (photograph, qr, text) = Render(version, level, pixelsPerModule, tiltDegrees, bowModules, blurSigma, noiseSigma, distance, destroyData: false);
        await AssertPremises(photograph, qr);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>The negative the measured nodes must not admit: the same photograph with its data destroyed reads nothing.</summary>
    [Test]
    [Arguments(25, QREccLevel.L, 4f, 15f, 2f, 0f, 0f, 1.5f)]
    [Arguments(30, QREccLevel.L, 3f, 15f, 4f, 1f, 10f, 1.5f)]
    public async Task BentHighVersionPhotograph_DestroyedData_ReadsNothing(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance)
    {
        var (photograph, _, _) = Render(version, level, pixelsPerModule, tiltDegrees, bowModules, blurSigma, noiseSigma, distance, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>The premises: the three drawn finders are the selected triple, the mesh builds from them, and the same mesh with its coordinate-6 nodes extrapolated along the lattice lines does not read.</summary>
    private static async Task AssertPremises(CameraRenderer.Photograph photograph, QRCodeData qr)
    {
        var d = qr.Size;
        var luminance = photograph.Luminance;
        int width = photograph.Width, height = photograph.Height;
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var patterns = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey)).IsTrue();
        QRImageDecoder.OrderFinderPatterns(patterns, out var topLeft, out var topRight, out var bottomLeft);
        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, default, topLeft, topRight, bottomLeft);
        QRImageDecoder.RefineFinderCentres(luminance, width, height, grey, sizes, ref topLeft, ref topRight, ref bottomLeft);
        sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);
        foreach (var (finder, u, v) in new[] { (topLeft, 3.5f, 3.5f), (topRight, d - 3.5f, 3.5f), (bottomLeft, 3.5f, d - 3.5f) })
        {
            var (x, y) = photograph.ToPixel(u, v);
            var off = MathF.Sqrt((finder.X - x) * (finder.X - x) + (finder.Y - y) * (finder.Y - y)) / sizes.Mean;
            await Assert.That(off).IsLessThan(0.5f).Because($"the drawn finder at ({u}, {v}) is selected");
        }

        var gridCoords = new float[7];
        var nodeXs = new float[49];
        var nodeYs = new float[49];
        await Assert.That(QRImageDecoder.TryBuildSampleMesh(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft, d, sizes.Mean, gridCoords, nodeXs, nodeYs, out var meshSize, out _, out _)).IsTrue();

        // The coordinate-6 row and column re-derived by quadratic extrapolation through the three nearest interior nodes of each lattice line, the top-left corner the average of its row's and its column's
        var count = meshSize;
        for (var i = 1; i < count; i++)
            Extrapolate(nodeXs, nodeYs, gridCoords, target: i, stride: count, first: count + i);
        for (var j = 1; j < count; j++)
            Extrapolate(nodeXs, nodeYs, gridCoords, target: j * count, stride: 1, first: j * count + 1);
        Extrapolate(nodeXs, nodeYs, gridCoords, target: 0, stride: 1, first: 1);
        float rowX = nodeXs[0], rowY = nodeYs[0];
        Extrapolate(nodeXs, nodeYs, gridCoords, target: 0, stride: count, first: count);
        nodeXs[0] = (nodeXs[0] + rowX) / 2f;
        nodeYs[0] = (nodeYs[0] + rowY) / 2f;
        var modules = new byte[d * d];
        QRImageDecoder.SampleGridPiecewise(luminance, width, height, threshold, gridCoords.AsSpan(0, meshSize), nodeXs, nodeYs, meshSize, d, modules);
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        await Assert.That(QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _)).IsNotEqualTo(DecodeStatus.Success).Because("the mesh with its coordinate-6 nodes extrapolated reads");
    }

    private static void Extrapolate(Span<float> nodeXs, Span<float> nodeYs, ReadOnlySpan<float> gridCoords, int target, int stride, int first)
    {
        float t0 = gridCoords[0], t1 = gridCoords[1], t2 = gridCoords[2], t3 = gridCoords[3];
        var l1 = (t0 - t2) * (t0 - t3) / ((t1 - t2) * (t1 - t3));
        var l2 = (t0 - t1) * (t0 - t3) / ((t2 - t1) * (t2 - t3));
        var l3 = (t0 - t1) * (t0 - t2) / ((t3 - t1) * (t3 - t2));
        nodeXs[target] = l1 * nodeXs[first] + l2 * nodeXs[first + stride] + l3 * nodeXs[first + 2 * stride];
        nodeYs[target] = l1 * nodeYs[first] + l2 * nodeYs[first + stride] + l3 * nodeYs[first + 2 * stride];
    }

    /// <summary>The symbol on a card tilted about its columns' axis and bent round a cylinder, photographed from the distance given.</summary>
    internal static (CameraRenderer.Photograph Photograph, QRCodeData Symbol, string Text) Render(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance, bool destroyData)
    {
        var text = $"FQR MESH EDGE v{version} 0123456789";
        var qr = QRCodeGenerator.Create(text, level, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var random = new Random(4321);
        var destroyed = new bool[qr.Size * qr.Size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        // Every module between the finders' rows and columns but the alignment patterns, clear of both format and both version copies
        bool IsDark(int row, int column) => destroyData && row >= 9 && column >= 9 && row < qr.Size - 11 && column < qr.Size - 11 && !IsAlignment(row, column) ? destroyed[row * qr.Size + column] : qr[row, column];
        bool IsAlignment(int row, int column)
        {
            foreach (var r in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
            {
                if (r == 0 || Math.Abs(row - r) > 2)
                    continue;
                foreach (var c in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
                {
                    if (c != 0 && Math.Abs(column - c) <= 2)
                        return true;
                }
            }
            return false;
        }
        var photograph = CameraRenderer.Render(IsDark, qr.Size, qr.Size, 4, new CameraRenderer.Shot(pixelsPerModule, TiltDegrees: tiltDegrees, TiltAxisDegrees: 90f, BowModules: bowModules, BlurSigma: blurSigma, NoiseSigma: noiseSigma, Distance: distance));
        return (photograph, qr, text);
    }
}
