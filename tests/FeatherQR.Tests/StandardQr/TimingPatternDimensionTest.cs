using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The builder draws every module a whole number of pixels wide, so at a fractional scale the
/// modules are 2 or 3 px and a finder can land on 2 px modules alone: it measures 2.0 against a
/// symbol averaging 2.13, and over the span between the finders that is two versions. The
/// timing pattern between the finders counts the modules instead of measuring them.
/// </summary>
public class TimingPatternDimensionTest
{
    private const string Content = "https://github.com/guitarrapc/FeatherQR";

    /// <summary>Fixed-size builder renders that read two versions high before the count.</summary>
    [Test]
    [Arguments(21, 208)]
    [Arguments(22, 211)]
    [Arguments(22, 241)]
    [Arguments(23, 244)]
    [Arguments(24, 226)]
    public async Task Decode_SnappedModuleWidths_ReadsTheTrueVersion(int version, int sizePx)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version });
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"v{version} at {sizePx} px: {info.Status}, read as v{info.Version}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    public static IEnumerable<(int, int)> VersionsAndDensities()
    {
        foreach (var pixelsPerModule in new[] { 1, 2, 3 })
        {
            for (var version = 1; version <= 40; version++)
                yield return (version, pixelsPerModule);
        }
    }

    [Test]
    [MethodDataSource(nameof(VersionsAndDensities))]
    public async Task Count_PixelAlignedRender_IsTheDimension(int version, int pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("T", QREccLevel.L, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var (luminance, width, height) = PixelAligned(qr, pixelsPerModule);

        await Assert.That(Count(luminance, width, height, pixelsPerModule)).IsEqualTo(qr.Size);
    }

    /// <summary>
    /// Off-axis and degraded renders may not count, but a count that is given is never wrong:
    /// the count goes first, and a wrong one would cost a decode attempt on every image.
    /// </summary>
    [Test]
    [Arguments(5, 3, 0)]
    [Arguments(5, 3, 30)]
    [Arguments(5, 3, 45)]
    [Arguments(20, 3, 12)]
    [Arguments(20, 4, 60)]
    [Arguments(40, 3, 7)]
    [Arguments(40, 5, 45)]
    public async Task Count_RotatedRender_IsTheDimensionOrNothing(int version, int pixelsPerModule, int degrees)
    {
        var qr = QRCodeGenerator.Create("T", QREccLevel.L, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var (luminance, side) = SupersampledRenderer.Render(qr, pixelsPerModule, degrees);

        var counted = Count(luminance, side, side, pixelsPerModule);

        await Assert.That(counted == 0 || counted == qr.Size).IsTrue().Because($"counted {counted}, true {qr.Size}");
    }

    /// <summary>
    /// One timing module painted over merges three runs into one three modules long, which no
    /// timing pattern has: the line refuses to count rather than count two fewer, and one line
    /// refusing leaves the dimension to the estimate.
    /// </summary>
    [Test]
    [Arguments(5, true)]
    [Arguments(5, false)]
    [Arguments(20, true)]
    [Arguments(20, false)]
    public async Task Count_DamagedTimingModule_CountsNothing(int version, bool inRow)
    {
        const int pixelsPerModule = 3;
        var qr = QRCodeGenerator.Create("T", QREccLevel.L, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var (luminance, width, height) = PixelAligned(qr, pixelsPerModule);
        // Timing module 9 is light; paint it dark
        var (row, column) = inRow ? (6, 9) : (9, 6);
        for (var y = 0; y < pixelsPerModule; y++)
        {
            for (var x = 0; x < pixelsPerModule; x++)
                luminance[((row + QuietZone) * pixelsPerModule + y) * width + (column + QuietZone) * pixelsPerModule + x] = 0;
        }

        await Assert.That(Count(luminance, width, height, pixelsPerModule)).IsEqualTo(0);
    }

    private const int QuietZone = 4;

    private static int Count(byte[] luminance, int width, int height, float moduleSize)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance);
        Span<FinderPattern> patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns))
            throw new InvalidOperationException("finders not found");
        QRImageDecoder.OrderFinderPatterns(patterns, out var topLeft, out var topRight, out var bottomLeft);
        return QRImageDecoder.CountTimingDimension(luminance, width, height, threshold, topLeft, topRight, bottomLeft, moduleSize);
    }

    private static (byte[] Luminance, int Width, int Height) PixelAligned(QRCodeData qr, int pixelsPerModule)
    {
        var side = (qr.Size + 2 * QuietZone) * pixelsPerModule;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var row = y / pixelsPerModule - QuietZone;
                var column = x / pixelsPerModule - QuietZone;
                var dark = row >= 0 && column >= 0 && row < qr.Size && column < qr.Size && qr[row, column];
                luminance[y * side + x] = dark ? (byte)0 : (byte)255;
            }
        }
        return (luminance, side, side);
    }
}
