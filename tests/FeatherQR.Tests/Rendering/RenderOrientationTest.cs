using SkiaSharp;
using System.Text;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Pins the XY orientation of every raster render path against the matrix
/// indexer: the module at <c>data[row, col]</c> must land at the pixel block
/// whose top-left corner is <c>(x = col * ppm, y = row * ppm)</c>. A transposed
/// or flipped renderer can slip through symmetric-looking content and decode
/// round-trips (the Standard QR decoder tolerates mirroring), so every module
/// center is sampled against the matrix instead. Both raster code paths are
/// covered per symbology: the merged-run fast path (rectangle modules at 100%)
/// and the per-module shape path (circle modules). rMQR's rectangular matrix
/// makes a row/column swap change the output dimensions, the strongest canary.
/// </summary>
public class RenderOrientationTest
{
    private const int PixelsPerModule = 5;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StandardQr_RenderedPixels_MatchMatrixOrientation(bool circleModules)
    {
        var data = QRCodeGenerator.Create("ORIENTATION 2026", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var builder = new QRCodeImageBuilder(data);
        using var bitmap = Render(builder, circleModules);

        await AssertOrientation(bitmap, data.Size, data.Size, (row, col) => data[row, col]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MicroQr_RenderedPixels_MatchMatrixOrientation(bool circleModules)
    {
        var data = MicroQRCodeGenerator.Create("MICRO 26", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var builder = new MicroQRCodeImageBuilder(data);
        using var bitmap = Render(builder, circleModules);

        await AssertOrientation(bitmap, data.Size, data.Size, (row, col) => data[row, col]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RmQr_RenderedPixels_MatchMatrixOrientation(bool circleModules)
    {
        var data = RmQRCodeGenerator.Create("RMQR ORIENTATION", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var builder = new RmQRCodeImageBuilder(data);
        using var bitmap = Render(builder, circleModules);

        await AssertOrientation(bitmap, data.Width, data.Height, (row, col) => data[row, col]);
    }

    private static SKBitmap Render<TSelf>(SymbolImageBuilderBase<TSelf> builder, bool circleModules)
        where TSelf : SymbolImageBuilderBase<TSelf>
    {
        builder
            .WithModulePixelSize(PixelsPerModule)
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.White);
        if (circleModules)
        {
            builder.WithModuleShape(CircleModuleShape.Default);
        }
        return builder.ToBitmap();
    }

    /// <summary>
    /// Samples the center pixel of every module: a full-size rectangle covers its
    /// cell entirely and an inscribed circle of diameter 5 fully covers the center
    /// pixel, so both paths must produce the exact solid color there, antialiasing
    /// cannot bleed into a covered center.
    /// </summary>
    private static async Task AssertOrientation(SKBitmap bitmap, int matrixWidth, int matrixHeight, Func<int, int, bool> module)
    {
        await Assert.That(bitmap.Width).IsEqualTo(matrixWidth * PixelsPerModule);
        await Assert.That(bitmap.Height).IsEqualTo(matrixHeight * PixelsPerModule);

        var mismatches = new StringBuilder();
        var mismatchCount = 0;
        for (var row = 0; row < matrixHeight; row++)
        {
            for (var col = 0; col < matrixWidth; col++)
            {
                var expected = module(row, col) ? SKColors.Black : SKColors.White;
                var actual = bitmap.GetPixel(col * PixelsPerModule + PixelsPerModule / 2, row * PixelsPerModule + PixelsPerModule / 2);
                if (actual != expected)
                {
                    mismatchCount++;
                    if (mismatchCount <= 10)
                    {
                        mismatches.AppendLine($"[{row},{col}] expected {expected}, got {actual}");
                    }
                }
            }
        }

        await Assert.That(mismatchCount).IsEqualTo(0)
            .Because($"{mismatchCount} module centers disagree with the matrix (first 10):\n{mismatches}");
    }
}
