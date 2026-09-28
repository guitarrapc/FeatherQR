using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// After a grid anchored on an alignment pattern fails, the finders' parallelogram is decoded and, where the image has grey levels, read again by coverage.
/// </summary>
public class ParallelogramCoverageDecodeTest
{
    /// <summary>
    /// Round finders and small round modules, from the real-image fixtures (Apache-2.0), at every right angle: the alignment pattern drawn as dots anchors the grid a fifth of a module off, and the parallelogram reads only by coverage.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealImageOfRoundModules_Decodes(int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-2", "qr-circles-1.png");
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>The coverage re-read is one more reading of the same grid, not a looser read: an anchored grey symbol whose data is destroyed does not decode.</summary>
    [Test]
    [Arguments(5, 4f, 20f)]
    [Arguments(10, 3f, 200f)]
    public async Task AnchoredSymbolWithItsDataDestroyed_DoesNotDecode(int version, float pixelsPerModule, float degrees)
    {
        const float Keystone = 0.05f;
        var qr = QRCodeGenerator.Create("FQR COVERAGE 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var alignment = qr.Size - 7;
        // Every module between the finders' rows and columns but the bottom-right alignment pattern, clear of both format copies
        bool IsDark(int row, int column) => row >= 9 && column >= 9 && row < qr.Size - 8 && column < qr.Size - 8 && (Math.Abs(row - alignment) > 2 || Math.Abs(column - alignment) > 2) ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, qr.Size, qr.Size, pixelsPerModule, degrees, Keystone);

        // Premise: the alignment search anchors the grid through the true finder centres, and the image has grey levels, so the parallelogram is read by coverage after it
        var truth = SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, degrees, Keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var topLeft = At(truth, 3.5f, 3.5f, pixelsPerModule);
        var topRight = At(truth, qr.Size - 3.5f, 3.5f, pixelsPerModule);
        var bottomLeft = At(truth, 3.5f, qr.Size - 3.5f, pixelsPerModule);
        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);
        QRImageDecoder.BuildGridTransform(luminance, width, height, threshold, grey, QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes), qr.Size, sizes.Mean, out var anchored);
        await Assert.That(anchored).IsTrue();
        await Assert.That(grey.IsEnabled).IsTrue();

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    private static FinderPattern At(in PerspectiveTransform truth, float u, float v, float moduleSize)
    {
        truth.Transform(u, v, out var x, out var y);
        return new FinderPattern { X = x, Y = y, ModuleSize = moduleSize, Count = 2 };
    }
}
