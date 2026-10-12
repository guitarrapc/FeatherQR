using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol photographed at a tilt near 70°: the card's far axis is drawn at a third of its near one, so a finder's column of runs is about three times its row's, or a third of it.
/// Every line through such a finder's centre still crosses its rings 1:1:3:1:1. Only how far the two axes may differ decides whether it is a candidate.
/// </summary>
public class SteepTiltDecodeTest
{
    [Test]
    [Arguments(2, 8f, 68f, 0f, 0f, 2.4f)]
    [Arguments(2, 8f, 70f, 90f, 0f, 2.4f)]
    [Arguments(3, 9f, 69f, 0f, 180f, 2.4f)]
    [Arguments(5, 9f, 68f, 90f, 0f, 2.4f)]
    [Arguments(2, 8f, 71f, 0f, 0f, 3.2f)]
    [Arguments(3, 9f, 71f, 90f, 0f, 3.2f)]
    [Arguments(5, 10f, 71f, 0f, 180f, 3.2f)]
    public async Task SymbolAtASteepTilt_Reads(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees, float past)
    {
        var (photograph, qr, text) = Render(version, pixelsPerModule, tiltDegrees, tiltAxisDegrees, rollDegrees);

        // The premise: at a drawn finder's centre the row and the column both read, and one is more than the given factor times the other. A keystone of a half draws a finder to 2.33.
        var threshold = Binarizer.ComputeOtsuThreshold(photograph.Luminance, out _);
        var furthest = 0f;
        var d = qr.Size;
        foreach (var (u, v) in new[] { (3.5f, 3.5f), (d - 3.5f, 3.5f), (3.5f, d - 3.5f) })
        {
            var (x, y) = photograph.ToPixel(u, v);
            var row = Total(photograph, threshold, (int)x, (int)y, 1, 0);
            var column = Total(photograph, threshold, (int)x, (int)y, 0, 1);
            await Assert.That(row).IsGreaterThan(0);
            await Assert.That(column).IsGreaterThan(0);
            furthest = MathF.Max(furthest, MathF.Max((float)row / column, (float)column / row));
        }
        await Assert.That(furthest).IsGreaterThan(past);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>The five runs' total along a line through a finder's centre, or 0 when they are not a finder's.</summary>
    private static int Total(CameraRenderer.Photograph photograph, byte threshold, int x, int y, int stepX, int stepY)
    {
        var runs = new int[5];
        return FinderPatternFinder.MeasureRuns(photograph.Luminance, photograph.Width, photograph.Height, threshold, x, y, stepX, stepY, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)
            ? runs[0] + runs[1] + runs[2] + runs[3] + runs[4]
            : 0;
    }

    private static (CameraRenderer.Photograph Photograph, QRCodeData Symbol, string Text) Render(int version, float pixelsPerModule, float tiltDegrees, float tiltAxisDegrees, float rollDegrees)
    {
        var text = $"FQR STEEP v{version}";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var photograph = CameraRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, new CameraRenderer.Shot(pixelsPerModule, TiltDegrees: tiltDegrees, TiltAxisDegrees: tiltAxisDegrees, RollDegrees: rollDegrees, BlurSigma: 0.5f, Distance: 4f));
        return (photograph, qr, text);
    }
}
