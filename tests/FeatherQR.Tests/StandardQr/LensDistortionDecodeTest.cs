using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol photographed through a lens that draws its corners nearer the centre than a pinhole would: its rows and columns bow, the three finder centres still measure well, and no plane through them and an alignment pattern follows the bow across a symbol of version 7 or more.
/// The mesh over the alignment lattice does, with every pattern of the lattice measured, the ones on the timing lines among them, and each cell curved through three nodes a side.
/// The shots are versions 7 to 40 through a lens term of 6 to 10 %, each asserted to read by no sampling of the grid through the finders and the fourth point. Four of them, versions 28 to 36, read only with the offset carried along the timing lines, two of those only with the dimension read at the finders.
/// </summary>
public class LensDistortionDecodeTest
{
    [Test]
    [Arguments(7, 4.5f, 0f, 0f, 20f, -0.08f)]
    [Arguments(8, 4f, 12f, 30f, 200f, -0.07f)]
    [Arguments(10, 5f, 5f, 120f, 75f, -0.08f)]
    [Arguments(11, 5f, 10f, 20f, 200f, -0.09f)]
    [Arguments(13, 4f, 10f, 45f, 300f, -0.07f)]
    [Arguments(13, 6f, 20f, 200f, 110f, -0.07f)]
    [Arguments(17, 4.5f, 5f, 0f, 40f, -0.06f)]
    [Arguments(20, 6f, 25f, 90f, 250f, -0.06f)]
    [Arguments(25, 5f, 15f, 300f, 15f, -0.08f)]
    [Arguments(28, 3.5f, 8f, 9f, 59f, -0.1f)]
    [Arguments(32, 4.2f, 3f, 249f, 133f, -0.093f)]
    [Arguments(32, 5f, 5f, 60f, 160f, -0.08f)]
    [Arguments(34, 4f, 3f, 115f, 206f, -0.08f)]
    [Arguments(36, 4.5f, 16f, 204f, 68f, -0.085f)]
    [Arguments(40, 4.5f, 20f, 135f, 330f, -0.06f)]
    public async Task SymbolThroughALens_ReadsThroughTheMesh(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion)
    {
        var (photograph, qr, text) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees, distortion, destroyData: false);
        await Assert.That(FailedPremise(photograph, qr)).IsNull();

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>The negative the mesh must not admit: the same shots with their data destroyed read nothing.</summary>
    [Test]
    [Arguments(10, 5f, 5f, 120f, 75f, -0.08f)]
    [Arguments(25, 5f, 15f, 300f, 15f, -0.08f)]
    public async Task SymbolThroughALens_DestroyedData_ReadsNothing(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion)
    {
        var (photograph, _, _) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees, distortion, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>What a shot must show for its read to need the mesh, or null: the drawn finders selected at the global threshold, and the grid through them and the alignment anchor, or their parallelogram, reading by no sampling.</summary>
    internal static string? FailedPremise(CameraRenderer.Photograph photograph, QRCodeData qr)
    {
        var d = qr.Size;
        var luminance = photograph.Luminance;
        int width = photograph.Width, height = photograph.Height;
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey))
            return "the global threshold finds no triple";
        var (frame, sizes) = FinderOutlineFitDecodeTest.Measure(luminance, width, height, out _, out _);
        foreach (var (finder, u, v) in new[] { (frame.TopLeft, 3.5f, 3.5f), (frame.TopRight, d - 3.5f, 3.5f), (frame.BottomLeft, 3.5f, d - 3.5f) })
        {
            var (x, y) = photograph.ToPixel(u, v);
            if (!(MathF.Sqrt((finder.X - x) * (finder.X - x) + (finder.Y - y) * (finder.Y - y)) / sizes.Mean < 0.5f))
                return $"the drawn finder at ({u}, {v}) is not selected";
        }

        var transform = QRImageDecoder.BuildGridTransform(luminance, width, height, threshold, grey, frame, d, sizes.Mean, out var anchored);
        if (FinderOutlineFitDecodeTest.Decodes(luminance, width, height, threshold, grey, transform, d))
            return "the grid through the finders and the fourth point reads without the mesh";
        if (anchored && FinderOutlineFitDecodeTest.Decodes(luminance, width, height, threshold, grey, QRImageDecoder.BuildParallelogramTransform(frame.TopLeft, frame.TopRight, frame.BottomLeft, d), d))
            return "the parallelogram of the finders reads without the mesh";
        return null;
    }

    /// <summary>The symbol on a card tilted about an axis in its plane, photographed through a lens with a light blur and noise, with its data modules redrawn at random where asked.</summary>
    internal static (CameraRenderer.Photograph Photograph, QRCodeData Symbol, string Text) Render(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion, bool destroyData)
    {
        var text = $"FQR LENS v{version}";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var d = qr.Size;
        var random = new Random(8642);
        var destroyed = new bool[d * d];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        var lattice = new List<int>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
        {
            if (value != 0)
                lattice.Add(value);
        }
        // What a reader locates the grid by: the finders with their separators and the format information, the timing lines, the version information and the alignment patterns
        bool IsFunction(int row, int column)
        {
            if ((row < 9 && (column < 9 || column >= d - 8)) || (row >= d - 8 && column < 9) || row == 6 || column == 6)
                return true;
            if ((row < 6 && column >= d - 11) || (column < 6 && row >= d - 11))
                return true;
            foreach (var centreRow in lattice)
            {
                foreach (var centreColumn in lattice)
                {
                    if (Math.Abs(row - centreRow) <= 2 && Math.Abs(column - centreColumn) <= 2)
                        return true;
                }
            }
            return false;
        }
        bool IsDark(int row, int column) => destroyData && !IsFunction(row, column) ? destroyed[row * d + column] : qr[row, column];
        var photograph = CameraRenderer.Render(IsDark, d, d, 4, new CameraRenderer.Shot(pixelsPerModule, TiltDegrees: tiltDegrees, TiltAxisDegrees: tiltAxisDegrees, RollDegrees: rollDegrees, Distortion: distortion, BlurSigma: 0.5f, NoiseSigma: 2f, Seed: version));
        return (photograph, qr, text);
    }

    /// <summary>
    /// The mesh on such shots: every interior node found, and every node, the patterns on the timing lines and the three beside the finders among them, where the lens drew it, where the finders' frame, one plane, puts a node more than half a module out.
    /// </summary>
    [Test]
    [Arguments(7, 4.5f, 0f, 0f, 20f, -0.08f)]
    [Arguments(13, 4f, 10f, 45f, 300f, -0.07f)]
    [Arguments(20, 6f, 25f, 90f, 250f, -0.06f)]
    [Arguments(32, 5f, 5f, 60f, 160f, -0.08f)]
    [Arguments(40, 4.5f, 20f, 135f, 330f, -0.06f)]
    public async Task Mesh_ThroughALens_PutsEveryNodeWhereItWasDrawn(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion)
    {
        var (photograph, qr, _) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees, distortion, destroyData: false);
        var d = qr.Size;
        var (frame, _) = FinderOutlineFitDecodeTest.Measure(photograph.Luminance, photograph.Width, photograph.Height, out var threshold, out var grey);
        var gridCoords = new float[7];
        var nodeXs = new float[49];
        var nodeYs = new float[49];

        var built = QRImageDecoder.TryBuildLatticeMesh(photograph.Luminance, photograph.Width, photograph.Height, threshold, grey, frame, d, gridCoords, nodeXs, nodeYs, out var meshSize, out var searchedNodes, out var foundNodes);

        await Assert.That(built).IsTrue();
        await Assert.That(foundNodes).IsEqualTo(searchedNodes);
        var worstOnTheFrame = 0f;
        var worst = 0f;
        var worstNode = 0;
        for (var node = 0; node < meshSize * meshSize; node++)
        {
            float u = gridCoords[node % meshSize], v = gridCoords[node / meshSize];
            frame.Map(u, v, d, out var frameX, out var frameY);
            worstOnTheFrame = MathF.Max(worstOnTheFrame, ModulesOff(photograph, u, v, frameX, frameY));
            var off = ModulesOff(photograph, u, v, nodeXs[node], nodeYs[node]);
            if (!(off <= worst))
            {
                worst = off;
                worstNode = node;
            }
        }
        await Assert.That(worstOnTheFrame).IsGreaterThan(0.5f);
        await Assert.That(worst).IsLessThan(0.3f).Because($"node ({worstNode % meshSize}, {worstNode / meshSize}) is {worst:F3} modules off");
    }

    /// <summary>
    /// Through a lens the finders, at the corners, measure a module smaller than the symbol's mean, and the dimension estimated from them lands on a larger version. The version information read at the far finders, each copy through its own finder's frame, names the dimension drawn.
    /// </summary>
    [Test]
    [Arguments(7, 4.5f, 0f, 0f, 20f, -0.08f)]
    [Arguments(13, 4f, 10f, 45f, 300f, -0.07f)]
    [Arguments(20, 6f, 25f, 90f, 250f, -0.06f)]
    [Arguments(32, 5f, 5f, 60f, 160f, -0.08f)]
    [Arguments(40, 4.5f, 20f, 135f, 330f, -0.06f)]
    public async Task VersionAtFinders_ThroughALens_NamesTheDimensionTheEstimateMisses(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion)
    {
        var (photograph, qr, _) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees, distortion, destroyData: false);

        var (estimate, named) = Dimensions(photograph);

        await Assert.That(estimate).IsGreaterThan(qr.Size + 2f);
        await Assert.That(named).IsEqualTo(qr.Size);
    }

    /// <summary>Every version that carries version information, on a clean render turned off the axes: the dimension drawn.</summary>
    [Test]
    public async Task VersionAtFinders_OnACleanRender_NamesEveryVersion()
    {
        var wrong = new List<string>();
        for (var version = 7; version <= 40; version++)
        {
            var qr = QRCodeGenerator.Create("FQR", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
            var photograph = CameraRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, new CameraRenderer.Shot(4f, TiltDegrees: 15f, TiltAxisDegrees: 30f, RollDegrees: 17f * version, BlurSigma: 0.5f));
            var (_, named) = Dimensions(photograph);
            if (named != qr.Size)
                wrong.Add($"v{version}: {named}");
        }

        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>The dimension the finders estimate, unsnapped, and the one the version information read at them names. Both are from the triple of the global threshold, as the decoder measures it.</summary>
    private static (float Estimate, int Named) Dimensions(CameraRenderer.Photograph photograph)
    {
        var (frame, sizes) = FinderOutlineFitDecodeTest.Measure(photograph.Luminance, photograph.Width, photograph.Height, out var threshold, out var grey);
        var estimate = QRImageDecoder.EstimateModules(frame.TopLeft, frame.TopRight, frame.BottomLeft, sizes, frame);
        var named = QRImageDecoder.ReadVersionAtFinders(new ImageView(photograph.Luminance, photograph.Width, photograph.Height, threshold, grey), frame.TopLeft, frame.TopRight, frame.BottomLeft, sizes);
        return (estimate, named);
    }

    /// <summary>How far (<paramref name="x"/>, <paramref name="y"/>) is from where the photograph drew grid point (<paramref name="u"/>, <paramref name="v"/>), in modules there.</summary>
    private static float ModulesOff(CameraRenderer.Photograph photograph, float u, float v, float x, float y)
    {
        var (expectedX, expectedY) = photograph.ToPixel(u, v);
        var (leftX, leftY) = photograph.ToPixel(u - 0.5f, v);
        var (rightX, rightY) = photograph.ToPixel(u + 0.5f, v);
        var module = MathF.Sqrt((rightX - leftX) * (rightX - leftX) + (rightY - leftY) * (rightY - leftY));
        return MathF.Sqrt((x - expectedX) * (x - expectedX) + (y - expectedY) * (y - expectedY)) / module;
    }

    /// <summary>The two copies stand in for each other: with either painted over, the other names the dimension.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task VersionAtFinders_WithOneCopyPaintedOver_NamesTheDimension(bool besideTopRight)
    {
        var qr = QRCodeGenerator.Create("FQR", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(25), QuietZoneSize = 0 });
        var d = qr.Size;
        // The copy beside the top-right finder is six rows by three columns, the one below the bottom-left finder its transpose
        bool IsDark(int row, int column)
        {
            var (along, across) = besideTopRight ? (row, column) : (column, row);
            return (along < 6 && across >= d - 11 && across < d - 8) || qr[row, column];
        }
        var photograph = CameraRenderer.Render(IsDark, d, d, 4, new CameraRenderer.Shot(4f, TiltDegrees: 15f, TiltAxisDegrees: 30f, RollDegrees: 40f, BlurSigma: 0.5f));

        var (_, named) = Dimensions(photograph);

        await Assert.That(named).IsEqualTo(d);
    }
    /// <summary>
    /// On a render no lens bends, the finders' frame reads both copies as the drawn dimension's codeword and as no neighbouring one. That is where the outlines are not measured for a dimension the estimate already has.
    /// </summary>
    [Test]
    public async Task StatesDimension_OnACleanRender_IsTheDrawnDimensionAlone()
    {
        var wrong = new List<string>();
        for (var version = 7; version <= 40; version++)
        {
            var qr = QRCodeGenerator.Create("FQR", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
            var photograph = CameraRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, new CameraRenderer.Shot(4f, TiltDegrees: 15f, TiltAxisDegrees: 30f, RollDegrees: 17f * version, BlurSigma: 0.5f));
            foreach (var versionsOff in new[] { -1, 0, 1 })
            {
                if (version + versionsOff is < 7 or > 40)
                    continue;
                if (States(photograph, _ => qr.Size + 4 * versionsOff, out _) != (versionsOff == 0))
                    wrong.Add($"v{version} asked for v{version + versionsOff}");
            }
        }

        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>
    /// Through a lens the estimate lands versions high and the symbol does not state it, so the outlines are still read.
    /// </summary>
    [Test]
    [Arguments(7, 4.5f, 0f, 0f, 20f, -0.08f)]
    [Arguments(13, 4f, 10f, 45f, 300f, -0.07f)]
    [Arguments(20, 6f, 25f, 90f, 250f, -0.06f)]
    [Arguments(32, 5f, 5f, 60f, 160f, -0.08f)]
    public async Task StatesDimension_ThroughALens_IsNotTheEstimate(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float distortion)
    {
        var (photograph, qr, _) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees, distortion, destroyData: false);
        var states = States(photograph, static estimate => 17 + 4 * (int)MathF.Round((estimate - 17f) / 4f), out var snapped);

        // The premise: the estimate snaps to a dimension that is a version or more above the drawn one and still a version's
        await Assert.That(snapped).IsGreaterThan(qr.Size);
        await Assert.That(snapped).IsLessThanOrEqualTo(177);

        await Assert.That(states).IsFalse();
    }

    /// <summary>Both copies are asked. With either painted over the symbol no longer states its dimension, and the outlines are read as before.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StatesDimension_WithOneCopyPaintedOver_IsFalse(bool besideTopRight)
    {
        var qr = QRCodeGenerator.Create("FQR", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(25), QuietZoneSize = 0 });
        var d = qr.Size;
        bool IsPainted(int row, int column)
        {
            var (along, across) = besideTopRight ? (row, column) : (column, row);
            return along < 6 && across >= d - 11 && across < d - 8;
        }
        var shot = new CameraRenderer.Shot(4f, TiltDegrees: 15f, TiltAxisDegrees: 30f, RollDegrees: 40f, BlurSigma: 0.5f);

        // The premise: the same render with both copies as drawn states the dimension
        foreach (var painted in new[] { false, true })
        {
            var photograph = CameraRenderer.Render((row, column) => (painted && IsPainted(row, column)) || qr[row, column], d, d, 4, shot);

            await Assert.That(States(photograph, _ => d, out _)).IsEqualTo(!painted);
        }
    }

    /// <summary>Whether the photograph states a dimension through its finders' frame, as the decoder measures the frame at the global threshold. The dimension is chosen from the unsnapped estimate.</summary>
    private static bool States(CameraRenderer.Photograph photograph, Func<float, int> dimensionFromEstimate, out int dimension)
    {
        var (frame, sizes) = FinderOutlineFitDecodeTest.Measure(photograph.Luminance, photograph.Width, photograph.Height, out var threshold, out var grey);
        dimension = dimensionFromEstimate(QRImageDecoder.EstimateModules(frame.TopLeft, frame.TopRight, frame.BottomLeft, sizes, frame));
        return dimension is >= 45 and <= 177
            && QRImageDecoder.StatesDimension(new ImageView(photograph.Luminance, photograph.Width, photograph.Height, threshold, grey), frame, dimension);
    }
}
