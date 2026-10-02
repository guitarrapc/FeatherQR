using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR's budget for a grid read again by coverage (<see cref="RmQRImageDecoder.Attempt"/>): the re-read is charged a decode of
/// the candidate's budget whether or not it changes a module, and is not made with no budget left.
/// </summary>
public class RmQRCoverageBudgetTest
{
    private const int QuietZone = 2;
    private const float PixelsPerModule = 2.5f;

    /// <summary>
    /// An R13x99 upscaled bilinearly, so the image has grey levels, with its data band inverted past what Reed-Solomon corrects,
    /// sampled through its own geometry: the grid gets past its format information and fails, so the gate asks only the budget.
    /// One decode leaves none for the re-read; two pay for both; five leave three.
    /// </summary>
    [Test]
    [Arguments(1, 0)]
    [Arguments(2, 0)]
    [Arguments(5, 3)]
    public async Task Attempt_ChargesTheReRead_AndMakesNoneWithNoBudgetLeft(int budget, int remaining)
    {
        var data = RmQRCodeDecoderImageTest.Create("RMQR IMAGE 123", RmQREccLevel.M, RmQRVersion.R13x99);
        bool IsDark(int row, int column)
        {
            var (r, c) = (row - QuietZone, column - QuietZone);
            return data[row, column] ^ (r >= 1 && r <= 11 && c >= 20 && c <= 80);
        }
        var (luminance, width, height) = BilinearUpscaleRenderer.Render(IsDark, data.Width, data.Height, PixelsPerModule);
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue().Because("premise: the image has grey levels");

        var (columns, rows) = (data.Width - 2 * QuietZone, data.Height - 2 * QuietZone);
        var (scaleX, scaleY) = ((float)width / data.Width, (float)height / data.Height);
        var transform = PerspectiveTransform.FromCoefficients(scaleX, 0, QuietZone * scaleX, 0, scaleY, QuietZone * scaleY, 0, 0, 1);
        var image = new ImageView(luminance, width, height, threshold, grey);
        var best = new SearchResult<RmQRCodeDecodeInfo>(ReportRule.Furthest, default);
        var left = budget;

        var status = RmQRImageDecoder.Attempt(image, transform, columns, rows, PixelsPerModule, new byte[columns * rows], new char[64], out _, out _, ref best, ref left);

        await Assert.That(status).IsEqualTo(DecodeStatus.DataUncorrectable).Because("premise: the grid got past its format information and failed at its data");
        await Assert.That(left).IsEqualTo(remaining);
    }
}
