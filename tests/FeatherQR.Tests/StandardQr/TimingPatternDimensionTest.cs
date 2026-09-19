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

    /// <summary>
    /// At versions 39 and 40 the snapped finders measure a few percent small, which puts a
    /// 173- or 177-module symbol more than four modules past the largest version. The estimate is
    /// refused as wild, so the count has to run without it.
    /// </summary>
    [Test]
    [Arguments(39, 350)]
    [Arguments(39, 385)]
    [Arguments(40, 285)]
    [Arguments(40, 320)]
    [Arguments(40, 358)]
    [Arguments(40, 443)]
    [Arguments(40, 540)]
    public async Task Decode_EstimatePastVersion40_ReadsByTheCount(int version, int sizePx)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version });
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"v{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    /// <summary>
    /// The same render with a timing module painted over: nothing counts, and an estimate that
    /// was refused stays refused.
    /// </summary>
    [Test]
    public async Task Decode_EstimatePastVersion40_NoCount_IsNotDetected()
    {
        const int sizePx = 540;
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();
        var luminance = new byte[sizePx * sizePx];
        for (var y = 0; y < sizePx; y++)
        {
            for (var x = 0; x < sizePx; x++)
                luminance[y * sizePx + x] = bitmap.GetPixel(x, y).Red;
        }
        // Light timing modules 9 and 11 on both lines, painted dark at the average pitch
        // (qr.Size includes the quiet zone)
        var pitch = sizePx / (float)qr.Size;
        foreach (var (row, column) in new[] { (6, 9), (6, 11), (9, 6), (11, 6) })
        {
            for (var y = (int)((row + QuietZone) * pitch); y < (int)((row + QuietZone + 1) * pitch); y++)
            {
                for (var x = (int)((column + QuietZone) * pitch); x < (int)((column + QuietZone + 1) * pitch); x++)
                    luminance[y * sizePx + x] = 0;
            }
        }

        var success = QRCodeDecoder.TryDecodeImage(luminance, sizePx, sizePx, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    /// <summary>
    /// Three finders and two timing lines as a version 45 symbol would draw them: the count is
    /// exact and names no version, so the out-of-range estimate is not rescued.
    /// </summary>
    [Test]
    public async Task Decode_TimingCountPastVersion40_IsNotDetected()
    {
        const int dimension = 17 + 4 * 45;
        const int pixelsPerModule = 2;
        var dark = new bool[dimension, dimension];
        foreach (var (top, left) in new[] { (0, 0), (0, dimension - 7), (dimension - 7, 0) })
        {
            for (var r = 0; r < 7; r++)
            {
                for (var c = 0; c < 7; c++)
                    dark[top + r, left + c] = r is 0 or 6 || c is 0 or 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4);
            }
        }
        for (var i = 8; i < dimension - 8; i++)
        {
            dark[6, i] = i % 2 == 0;
            dark[i, 6] = i % 2 == 0;
        }
        var side = (dimension + 2 * QuietZone) * pixelsPerModule;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var row = y / pixelsPerModule - QuietZone;
                var column = x / pixelsPerModule - QuietZone;
                luminance[y * side + x] = row >= 0 && column >= 0 && row < dimension && column < dimension && dark[row, column] ? (byte)0 : (byte)255;
            }
        }

        await Assert.That(Count(luminance, side, side, pixelsPerModule)).IsEqualTo(0);
        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out _, out var info);
        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
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
