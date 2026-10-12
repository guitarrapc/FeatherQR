using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A version 1 symbol on a card bent round a cylinder and seen at a tilt, as a label on a bottle is photographed: no alignment pattern anchors its fourth corner, and the grids through the finder centres alone, the parallelogram and the finders' frame, place that corner up to a module and a half out.
/// The bottom-left finder's top edge and the top-right finder's left edge, each the ring's outer edge followed across the finder, meet within a fifth of a module of where the symbol's row and column 7 from the far corner do, and the grid through that fourth point reads.
/// The shots were chosen from a sweep of tilts, bends and distances at error correction level L, where the parallelogram and the frame fail and the edge point reads with a bend more or less.
/// </summary>
public class EdgeFourthPointDecodeTest
{
    internal const string Content = "FQR EDGE POINT 01";

    [Test]
    [Arguments(15f, 0f, 2.5f, 4f)]
    [Arguments(25f, 200f, 2.5f, 3f)]
    [Arguments(25f, 0f, 1.5f, 3f)]
    public async Task BentVersion1_ReadsThroughTheFourthPointFromTheEdges(float tiltDegrees, float rollDegrees, float distance, float bowModules)
    {
        var (photograph, qr) = Render(tiltDegrees, rollDegrees, distance, bowModules, destroyData: false);
        await AssertPremises(photograph, qr);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>The negative the fourth point must not admit: the same symbol with its data destroyed reads nothing.</summary>
    [Test]
    [Arguments(15f, 0f, 2.5f, 4f)]
    [Arguments(25f, 200f, 2.5f, 3f)]
    public async Task BentVersion1_DestroyedData_ReadsNothing(float tiltDegrees, float rollDegrees, float distance, float bowModules)
    {
        var (photograph, _) = Render(tiltDegrees, rollDegrees, distance, bowModules, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>The premises: the three drawn finders are the selected triple, neither the parallelogram of their centres nor the finders' frame samples a grid that decodes, and the fourth point from the edges lies within a fifth of a module of the drawn one.</summary>
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
            await Assert.That(Modules(finder.X, finder.Y, x, y, sizes.Mean)).IsLessThan(0.5f).Because($"the drawn finder at ({u}, {v}) is selected");
        }

        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(1)];
        foreach (var (name, transform) in new[] { ("parallelogram", QRImageDecoder.BuildParallelogramTransform(topLeft, topRight, bottomLeft, d)), ("frame", frame.Transform(d)) })
        {
            var modules = new byte[d * d];
            PerspectiveGridSampler.Sample(luminance, width, height, threshold, transform, d, modules);
            await Assert.That(QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _)).IsNotEqualTo(DecodeStatus.Success).Because($"the {name} grid reads without the fourth point");
        }

        var image = new ImageView(luminance, width, height, threshold, grey);
        await Assert.That(QRImageDecoder.TryFourthPointFromEdges(image, topLeft, topRight, bottomLeft, sizes.Mean, out var edgeX, out var edgeY)).IsTrue();
        var (drawnX, drawnY) = photograph.ToPixel(d - 7, d - 7);
        await Assert.That(Modules(edgeX, edgeY, drawnX, drawnY, sizes.Mean)).IsLessThan(0.2f).Because("the fourth point from the edges is where the symbol's row and column 7 from the far corner meet");
    }

    private static float Modules(float x0, float y0, float x1, float y1, float module) => MathF.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1)) / module;

    /// <summary>The version 1 symbol at level L, 8 px/module, on a card tilted about its columns' axis and bent round a cylinder, with no blur or noise.</summary>
    internal static (CameraRenderer.Photograph Photograph, QRCodeData Symbol) Render(float tiltDegrees, float rollDegrees, float distance, float bowModules, bool destroyData)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), QuietZoneSize = 0 });
        var random = new Random(4321);
        var destroyed = new bool[qr.Size * qr.Size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        // Version 1's data modules lie past the finders, separators, timing patterns and format information
        bool IsDark(int row, int column) => destroyData && row >= 9 && column >= 9 ? destroyed[row * qr.Size + column] : qr[row, column];
        var photograph = CameraRenderer.Render(IsDark, qr.Size, qr.Size, 4, new CameraRenderer.Shot(8f, TiltDegrees: tiltDegrees, TiltAxisDegrees: 90f, RollDegrees: rollDegrees, BowModules: bowModules, Distance: distance));
        return (photograph, qr);
    }
}
