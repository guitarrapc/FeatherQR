using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A small symbol photographed at a tilt from close by, blurred, at 3 to 4 px/module: its three finder centres measure well, and every grid through them and one more point, the alignment anchor or the parallelogram's corner, the finders' frame, the fourth point from the finders' edges, leaves the far corner where no read survives.
/// The plane fitted to the corners of the three finders' inner outlines, with the anchor where there is one, takes each finder as a shape that carries the perspective where it stands, and reads.
/// The shots vary version, level, tilt, axis and distance, with the alignment pattern kept and painted over, and each one's read needs the fitted plane, which its premise check holds.
/// </summary>
public class FinderOutlineFitDecodeTest
{
    [Test]
    [Arguments(1, QREccLevel.L, 3f, 40f, 45f, 2.5f, 0f, false)]
    [Arguments(2, QREccLevel.L, 4f, 40f, 0f, 1.2f, 200f, true)]
    [Arguments(5, QREccLevel.L, 3f, 30f, 0f, 1.5f, 0f, true)]
    [Arguments(5, QREccLevel.L, 4f, 45f, 0f, 1.5f, 0f, true)]
    [Arguments(5, QREccLevel.M, 3f, 30f, 0f, 1.5f, 0f, true)]
    public async Task TiltedSmallSymbol_ReadsThroughThePlaneFittedToTheFinderOutlines(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float distance, float rollDegrees, bool alignmentPaintedOver)
    {
        var (photograph, qr, text) = Render(version, level, pixelsPerModule, tiltDegrees, tiltAxisDegrees, distance, rollDegrees, alignmentPaintedOver, destroyData: false);
        await Assert.That(FailedPremise(photograph, qr, anchored: false)).IsNull();

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>
    /// A small symbol with its alignment pattern, on a card bent three modules round a cylinder and tilted: the alignment search anchors, and neither the grid through the three centres and the anchor nor the parallelogram of the centres reads a surface no four points fix.
    /// The plane fitted to the outlines and the anchor, the anchor counted three times against the twelve corners, splits the bend between them and reads.
    /// Each shot's read needs the fitted plane after an anchored grid, which its premise check holds.
    /// </summary>
    [Test]
    [Arguments(2, QREccLevel.L, 6f, 35f, 90f, 2.5f, 3f, 1f)]
    [Arguments(3, QREccLevel.M, 4f, 35f, 45f, 2.5f, 3f, 1f)]
    [Arguments(5, QREccLevel.M, 3f, 20f, 45f, 2.5f, 3f, 1f)]
    [Arguments(6, QREccLevel.L, 4f, 20f, 45f, 1.5f, 3f, 0.5f)]
    public async Task BentSmallSymbol_ReadsThroughThePlaneFittedWithTheAnchor(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float distance, float bowModules, float blurSigma)
    {
        var (photograph, qr, text) = Render(version, level, pixelsPerModule, tiltDegrees, tiltAxisDegrees, distance, 0f, alignmentPaintedOver: false, destroyData: false, bowModules, blurSigma);
        await Assert.That(FailedPremise(photograph, qr, anchored: true)).IsNull();

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>The same negative for the anchored fit: a bent shot with its data destroyed reads nothing.</summary>
    [Test]
    [Arguments(3, QREccLevel.M, 4f, 35f, 45f, 2.5f, 3f, 1f)]
    [Arguments(6, QREccLevel.L, 4f, 20f, 45f, 1.5f, 3f, 0.5f)]
    public async Task BentSmallSymbol_DestroyedData_ReadsNothing(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float distance, float bowModules, float blurSigma)
    {
        var (photograph, _, _) = Render(version, level, pixelsPerModule, tiltDegrees, tiltAxisDegrees, distance, 0f, alignmentPaintedOver: false, destroyData: true, bowModules, blurSigma);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>
    /// Two photographs of the real-image fixtures (Apache-2.0), read at the right angles they read at: the alignment search anchors, and where the plane fitted with the anchor does not read, the plane of the twelve outline corners alone does.
    /// </summary>
    [Test]
    [Arguments("05", 0)]
    [Arguments("05", 1)]
    [Arguments("05", 2)]
    [Arguments("05", 3)]
    [Arguments("06", 0)]
    [Arguments("06", 1)]
    [Arguments("06", 3)]
    public async Task RealImage_ReadsThroughThePlaneFittedWithoutTheAnchor(string name, int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-4", name + ".webp");
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>The negative the fitted plane must not admit: the same shots with their data destroyed read nothing.</summary>
    [Test]
    [Arguments(2, QREccLevel.L, 4f, 40f, 0f, 1.2f, 200f, true)]
    [Arguments(5, QREccLevel.M, 3f, 30f, 0f, 1.5f, 0f, true)]
    public async Task TiltedSmallSymbol_DestroyedData_ReadsNothing(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float distance, float rollDegrees, bool alignmentPaintedOver)
    {
        var (photograph, _, _) = Render(version, level, pixelsPerModule, tiltDegrees, tiltAxisDegrees, distance, rollDegrees, alignmentPaintedOver, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>
    /// On a clean render in perspective the fitted plane is the plane the symbol was drawn on: its four outer corners within a quarter of a module of the drawn ones, from the finders alone.
    /// </summary>
    [Test]
    [Arguments(1, 6f, 0f, 0f)]
    [Arguments(2, 5f, 20f, 0.15f)]
    [Arguments(4, 4f, 200f, 0.2f)]
    public async Task FittedPlane_OnACleanRender_IsThePlaneDrawn(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var qr = QRCodeGenerator.Create($"FQR FIT v{version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var d = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], d, d, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(d, d, pixelsPerModule, degrees, keystone);
        var (frame, sizes) = Measure(luminance, width, height, out var threshold, out var grey);

        var fits = TryFit(luminance, width, height, threshold, grey, frame, d, anchored: false, default, out var fitted);

        await Assert.That(fits).IsTrue();
        foreach (var (u, v) in new[] { (0f, 0f), ((float)d, 0f), ((float)d, (float)d), (0f, (float)d) })
        {
            truth.Transform(u, v, out var x, out var y);
            fitted.Transform(u, v, out var fx, out var fy);
            await Assert.That(MathF.Sqrt((x - fx) * (x - fx) + (y - fy) * (y - fy)) / sizes.Mean).IsLessThan(0.25f).Because($"corner ({u}, {v})");
        }
    }

    /// <summary>The plane is for symbols with no alignment lattice: the same clean render is fitted at version 6 and not at version 7, where the mesh follows the lattice.</summary>
    [Test]
    [Arguments(6, true)]
    [Arguments(7, false)]
    public async Task FittedPlane_FromVersion7_IsNotFitted(int version, bool expected)
    {
        var qr = QRCodeGenerator.Create($"FQR FIT v{version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var d = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], d, d, 4f, 20f, 0.1f);
        var (frame, _) = Measure(luminance, width, height, out var threshold, out var grey);

        var fits = TryFit(luminance, width, height, threshold, grey, frame, d, anchored: false, default, out _);

        await Assert.That(fits).IsEqualTo(expected);
    }

    /// <summary>A finder with no outline to measure, its light ring filled in, leaves no fit: the plane is fitted to three shapes or not at all.</summary>
    [Test]
    public async Task FittedPlane_FinderWithoutALightRing_IsNotFitted()
    {
        var qr = QRCodeGenerator.Create("FQR FIT v2", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(2), QuietZoneSize = 0 });
        var d = qr.Size;
        var (clean, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], d, d, 5f, 20f, 0.1f);
        // The frame from the clean render, so that only the outline differs
        var (frame, _) = Measure(clean, width, height, out _, out _);
        var (luminance, _, _) = SupersampledRenderer.Render((row, column) => (row >= d - 7 && column < 7) || qr[row, column], d, d, 5f, 20f, 0.1f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var cleanFits = TryFit(clean, width, height, threshold, grey, frame, d, anchored: false, default, out _);
        var fits = TryFit(luminance, width, height, threshold, grey, frame, d, anchored: false, default, out _);

        await Assert.That(cleanFits).IsTrue();
        await Assert.That(fits).IsFalse();
    }

    /// <summary>
    /// The first of the premises that does not hold, or null when all do: at the global threshold the drawn finders are the selected triple, the alignment search anchors or not as stated, no grid through the three centres and one more point decodes by the threshold, by coverage or in module space, and the fitted plane does.
    /// </summary>
    internal static string? FailedPremise(CameraRenderer.Photograph photograph, QRCodeData qr, bool anchored)
    {
        var d = qr.Size;
        var luminance = photograph.Luminance;
        int width = photograph.Width, height = photograph.Height;
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey))
            return "the global threshold finds no triple";
        var (frame, sizes) = Measure(luminance, width, height, out _, out _);
        foreach (var (finder, u, v) in new[] { (frame.TopLeft, 3.5f, 3.5f), (frame.TopRight, d - 3.5f, 3.5f), (frame.BottomLeft, 3.5f, d - 3.5f) })
        {
            var (x, y) = photograph.ToPixel(u, v);
            if (!(Modules(finder.X, finder.Y, x, y, sizes.Mean) < 0.5f))
                return $"the drawn finder at ({u}, {v}) is not selected";
        }

        var transform = QRImageDecoder.BuildGridTransform(luminance, width, height, threshold, grey, frame, d, sizes.Mean, out var alignmentAnchored);
        if (alignmentAnchored != anchored)
            return anchored ? "the alignment search does not anchor" : "the alignment search anchors";

        var grids = new List<(string Name, PerspectiveTransform Transform)> { (anchored ? "anchored" : "parallelogram", transform) };
        if (anchored)
        {
            grids.Add(("parallelogram", QRImageDecoder.BuildParallelogramTransform(frame.TopLeft, frame.TopRight, frame.BottomLeft, d)));
        }
        else
        {
            grids.Add(("frame", frame.Transform(d)));
            if (TryEdgePointGrid(luminance, width, height, threshold, grey, frame, sizes.Mean, d, out var edgeGrid))
                grids.Add(("fourth point from the edges", edgeGrid));
        }
        foreach (var (name, grid) in grids)
        {
            if (Decodes(luminance, width, height, threshold, grey, grid, d))
                return $"the {name} grid reads without the fitted plane";
        }

        if (!TryFit(luminance, width, height, threshold, grey, frame, d, anchored, transform, out var fitted))
            return "the finders' outlines are not measured";
        return Decodes(luminance, width, height, threshold, grey, fitted, d) ? null : "the fitted plane does not read";
    }

    private static bool TryFit(byte[] luminance, int width, int height, byte threshold, GreyLevels grey, in QRImageDecoder.FinderFrame frame, int d, bool anchored, in PerspectiveTransform anchoredTransform, out PerspectiveTransform fitted)
    {
        if (!QRImageDecoder.TryFitToFinderOutlines(new ImageView(luminance, width, height, threshold, grey), frame, d, anchored, anchoredTransform, out fitted, out var anchoredFit))
            return false;
        if (anchored)
            fitted = anchoredFit;
        return true;
    }

    /// <summary>The grid through the three centres and the fourth point measured from the finders' edges, at module (d − 7, d − 7).</summary>
    private static bool TryEdgePointGrid(byte[] luminance, int width, int height, byte threshold, GreyLevels grey, in QRImageDecoder.FinderFrame frame, float moduleSize, int d, out PerspectiveTransform grid)
    {
        grid = default;
        if (!QRImageDecoder.TryFourthPointFromEdges(new ImageView(luminance, width, height, threshold, grey), frame.TopLeft, frame.TopRight, frame.BottomLeft, moduleSize, out var x, out var y))
            return false;
        grid = PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f, d - 3.5f, 3.5f, d - 7f, d - 7f, 3.5f, d - 3.5f,
            frame.TopLeft.X, frame.TopLeft.Y, frame.TopRight.X, frame.TopRight.Y, x, y, frame.BottomLeft.X, frame.BottomLeft.Y);
        return true;
    }

    /// <summary>The selected triple of the global threshold, its centres refined and its frame, as the decoder measures them.</summary>
    internal static (QRImageDecoder.FinderFrame Frame, QRImageDecoder.FinderModuleSizes Sizes) Measure(byte[] luminance, int width, int height, out byte threshold, out GreyLevels grey)
    {
        threshold = Binarizer.ComputeOtsuThreshold(luminance, out grey);
        var patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey))
            throw new InvalidOperationException("no finder triple at the global threshold");
        QRImageDecoder.OrderFinderPatterns(patterns, out var topLeft, out var topRight, out var bottomLeft);
        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, default, topLeft, topRight, bottomLeft);
        QRImageDecoder.RefineFinderCentres(luminance, width, height, grey, sizes, ref topLeft, ref topRight, ref bottomLeft);
        sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);
        return (QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes), sizes);
    }

    /// <summary>Whether the grid decodes any of the three ways a grid is read: by the threshold, by coverage, in module space.</summary>
    internal static bool Decodes(byte[] luminance, int width, int height, byte threshold, GreyLevels grey, in PerspectiveTransform grid, int d)
    {
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(6)];
        var modules = new byte[d * d];
        PerspectiveGridSampler.Sample(luminance, width, height, threshold, grid, d, modules);
        if (QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _) == DecodeStatus.Success)
            return true;
        QRImageDecoder.SampleGridByCoverage(luminance, width, height, grey.Midpoint, grid, d, modules);
        if (QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _) == DecodeStatus.Success)
            return true;
        QRImageDecoder.SampleGridInModuleSpace(luminance, width, height, grid, d, QRImageDecoder.IsBelowInterpolation(grid, d), negative: false, new int[QRImageDecoder.ModuleSpaceScratchLength(d)], modules);
        return QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _) == DecodeStatus.Success;
    }

    private static float Modules(float x0, float y0, float x1, float y1, float module) => MathF.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1)) / module;

    /// <summary>The symbol on a card tilted about an axis in its plane and bent round a cylinder where asked, photographed from the distance given through a blur, its bottom-right alignment pattern painted over light where asked.</summary>
    internal static (CameraRenderer.Photograph Photograph, QRCodeData Symbol, string Text) Render(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float distance, float rollDegrees, bool alignmentPaintedOver, bool destroyData, float bowModules = 0f, float blurSigma = 1f)
    {
        var text = $"FQR FIT v{version}";
        var qr = QRCodeGenerator.Create(text, level, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var centre = qr.Size - 7;
        var random = new Random(4321);
        var destroyed = new bool[qr.Size * qr.Size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        bool IsAlignment(int row, int column) => version >= 2 && Math.Abs(row - centre) <= 2 && Math.Abs(column - centre) <= 2;
        // The data past the finders, their separators, the timing patterns and the format information, clear of the alignment pattern
        bool IsDark(int row, int column)
        {
            if (IsAlignment(row, column))
                return !alignmentPaintedOver && qr[row, column];
            return destroyData && row >= 9 && column >= 9 ? destroyed[row * qr.Size + column] : qr[row, column];
        }
        var photograph = CameraRenderer.Render(IsDark, qr.Size, qr.Size, 4, new CameraRenderer.Shot(pixelsPerModule, TiltDegrees: tiltDegrees, TiltAxisDegrees: tiltAxisDegrees, RollDegrees: rollDegrees, BowModules: bowModules, BlurSigma: blurSigma, Distance: distance));
        return (photograph, qr, text);
    }
}
