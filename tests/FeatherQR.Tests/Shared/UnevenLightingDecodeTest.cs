using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol lit unevenly has no single threshold between its modules: at the dim side a light module is darker than the global threshold, so the finders there read as solid and nothing is detected.
/// The decoders binarize again against each region's own level when the global threshold reads nothing.
/// </summary>
public class UnevenLightingDecodeTest
{
    private const string Content = "FQR 2.0";
    private const int PixelsPerModule = 4;

    public static IEnumerable<(UnevenLight, float, float)> Lighting()
    {
        foreach (var degrees in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
        {
            yield return (UnevenLight.Ramp, degrees, 0.8f);
            yield return (UnevenLight.Shadow, degrees, 0.55f);
        }
    }

    /// <summary>
    /// The case is only one of this class if the global threshold reads part of the paper as ink; otherwise a decode proves nothing about the regional pass.
    /// </summary>
    private static async Task AssertGlobalThresholdSplitsThePaper(Func<int, int, bool> isDark, int columns, byte[] luminance, int width)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out _);
        var paperAsInk = 0;
        var paper = 0;
        for (var i = 0; i < luminance.Length; i++)
        {
            var x = i % width;
            var y = i / width;
            if (isDark(y / PixelsPerModule, x / PixelsPerModule))
                continue;
            paper++;
            if (luminance[i] < threshold)
                paperAsInk++;
        }
        await Assert.That(paperAsInk * 10).IsGreaterThan(paper).Because($"threshold {threshold} reads {paperAsInk} of {paper} paper pixels as ink; the case needs a tenth");
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task StandardQR_UnevenLight_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);

        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Size, luminance, width);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task MicroQR_UnevenLight_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);

        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Size, luminance, width);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task RmQR_UnevenLight_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Width, qr.Height, PixelsPerModule, light, degrees, depth);

        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Width, luminance, width);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    /// <summary>
    /// The negative of each lit render decodes too: light modules on dark paper, with the shading running the other way, since negating the image negates its light.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task StandardQR_UnevenLightReflectanceReversed_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);
        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Size, luminance, width);
        Negate(luminance);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task MicroQR_UnevenLightReflectanceReversed_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);
        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Size, luminance, width);
        Negate(luminance);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task RmQR_UnevenLightReflectanceReversed_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Width, qr.Height, PixelsPerModule, light, degrees, depth);
        await AssertGlobalThresholdSplitsThePaper(isDark, qr.Width, luminance, width);
        Negate(luminance);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    /// <summary>
    /// Renders whose finder band falls between the rows a strided scan visits, at 2 px/module: the regional attempt reads them only through the strideless sweep, which it has like the global attempts.
    /// </summary>
    public static IEnumerable<(bool, float, float, float, bool, UnevenLight, float, float)> BetweenScannedRows()
    {
        // (Micro QR, turn, offset x, offset y, anti-aliased, light, light direction, depth)
        // Chosen to keep needing the sweep, and reading with it, under a nudge of up to 0.5° of turn, 0.1 px of either offset or 0.03 of depth: a case at a knife edge stops guarding the sweep on the next unrelated change
        yield return (true, 31f, 0.25f, 2.5f, false, UnevenLight.Ramp, 0f, 0.8f);
        yield return (true, 61f, 0.5f, 3f, false, UnevenLight.Ramp, 90f, 0.8f);
        yield return (true, 57f, 0.25f, 2.5f, true, UnevenLight.Ramp, 90f, 0.8f);
        yield return (false, 33f, 0f, 2.5f, false, UnevenLight.Ramp, 0f, 0.8f);
        yield return (false, 59f, 0.25f, 2.5f, false, UnevenLight.Ramp, 90f, 0.8f);
        yield return (false, 33f, 0f, 2.5f, true, UnevenLight.Ramp, 90f, 0.8f);
    }

    [Test]
    [MethodDataSource(nameof(BetweenScannedRows))]
    public async Task UnevenLight_FinderBetweenScannedRows_Decodes(bool microQr, float turn, float offsetX, float offsetY, bool antiAliased, UnevenLight light, float lightDegrees, float depth)
    {
        var micro = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        var rmqr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
        var (luminance, width, height) = microQr
            ? UnevenLightingRenderer.Render((row, column) => micro[row, column], micro.Size, micro.Size, 2f, turn, offsetX, offsetY, antiAliased, light, lightDegrees, depth)
            : UnevenLightingRenderer.Render((row, column) => rmqr[row, column], rmqr.Width, rmqr.Height, 2f, turn, offsetX, offsetY, antiAliased, light, lightDegrees, depth);
        await AssertGlobalAttemptsFail(microQr, luminance, width, height);
        await AssertOnlyTheSweepFindsTheFinder(luminance, width, height);

        string text;
        DecodeStatus status;
        if (microQr)
        {
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out var info);
            status = info.Status;
        }
        else
        {
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out var info);
            status = info.Status;
        }

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>
    /// The case is only one of this group if neither global polarity reads it, sweep included; otherwise the regional attempt is never reached.
    /// </summary>
    private static async Task AssertGlobalAttemptsFail(bool microQr, byte[] luminance, int width, int height)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var negative = new byte[luminance.Length];
        LuminanceInverter.Invert(luminance, negative);
        var negativeHistogram = histogram.ToArray();
        Binarizer.InvertHistogram(negativeHistogram);
        var destination = new char[64];

        foreach (var (image, bins) in new[] { (luminance, histogram), (negative, negativeHistogram) })
        {
            var status = microQr
                ? MicroQRImageDecoder.DecodeLuminanceCore(image, bins, width, height, destination, out _, out _)
                : RmQRImageDecoder.DecodeLuminanceCore(image, bins, width, height, destination, out _, out _);
            await Assert.That(status).IsNotEqualTo(DecodeStatus.Success);
        }
    }

    /// <summary>
    /// The case is only one of this group if, on the regional binarization, the strided scan finds no finder and the sweep does; otherwise a decode proves nothing about the sweep.
    /// </summary>
    private static async Task AssertOnlyTheSweepFindsTheFinder(byte[] luminance, int width, int height)
    {
        var binarized = new byte[luminance.Length];
        var moved = LocalBinarizer.TryBinarize(luminance, width, height, negative: false, Binarizer.ComputeOtsuThreshold(luminance, out _), binarized, new int[LocalBinarizer.ScratchLength(width, height)], out _);
        var threshold = Binarizer.ComputeOtsuThreshold(binarized, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];

        await Assert.That(moved).IsTrue();
        await Assert.That(FinderPatternFinder.FindCandidates(binarized, width, height, threshold, candidates, grey)).IsEqualTo(0);
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(binarized, width, height, threshold, candidates, grey)).IsGreaterThan(0);
    }

    private static void Negate(byte[] luminance)
    {
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(255 - luminance[i]);
    }

    /// <summary>A symbol under the same light whose data is past correction does not decode, and gives no text.</summary>
    [Test]
    [Arguments(UnevenLight.Ramp, 0f, 0.8f)]
    [Arguments(UnevenLight.Shadow, 90f, 0.55f)]
    public async Task StandardQR_UnevenLight_DataDestroyed_DoesNotDecode(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.L, new QRCodeGeneratorOptions { Version = 3 });
        // Every other module of the lower half flipped, outside the finders: far past level L
        Func<int, int, bool> isDark = (row, column) => row >= 4 + 15 && row < qr.Size - 4 && column >= 4 + 9 && ((row + column) & 1) == 0 ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because($"decoded \"{text}\"");
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEmpty();
    }

    /// <summary>An image with no symbol and a lighting ramp in it reads as nothing: the regional pass turns the ramp into paper and the noise into specks, neither a finder.</summary>
    [Test]
    public async Task AllSymbologies_UnevenLightNoSymbol_NotDetected()
    {
        const int size = 256;
        var random = new Random(7);
        var luminance = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                luminance[y * size + x] = (byte)Math.Clamp(40 + 180 * x / size + random.Next(-20, 21), 0, 255);
        }

        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, size, size, out _, out var qr)).IsFalse();
        await Assert.That(qr.Status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, size, size, out _, out var micro)).IsFalse();
        await Assert.That(micro.Status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, size, size, out _, out var rmqr)).IsFalse();
        await Assert.That(rmqr.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    /// <summary>A symbol read by the regional pass into a destination too short reports that, as the first two attempts do, rather than falling through to their failure.</summary>
    [Test]
    public async Task StandardQR_UnevenLight_ShortDestination_ReportsDestinationTooSmall()
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[2], out var written, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task MicroQR_UnevenLight_ShortDestination_ReportsDestinationTooSmall()
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[2], out var written, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task RmQR_UnevenLight_ShortDestination_ReportsDestinationTooSmall()
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[2], out var written, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>
    /// On images of only 0 and 255 the regional binarization puts every pixel where the global threshold did (every regional threshold lies in [0, 251]), which is why the decoders skip such images without binarizing them.
    /// </summary>
    [Test]
    [Arguments("noise 1 px", 1)]
    [Arguments("noise 3 px", 3)]
    [Arguments("crisp damaged symbol", 0)]
    public async Task TryBinarize_TwoLevelImage_AgreesWithGlobalThreshold(string name, int cell)
    {
        byte[] luminance;
        int width, height;
        if (cell > 0)
        {
            width = height = 240;
            luminance = new byte[width * height];
            var random = new Random(cell);
            for (var cy = 0; cy < height; cy += cell)
            {
                for (var cx = 0; cx < width; cx += cell)
                {
                    var value = random.Next(2) == 0 ? (byte)0 : (byte)255;
                    for (var y = cy; y < Math.Min(height, cy + cell); y++)
                    {
                        for (var x = cx; x < Math.Min(width, cx + cell); x++)
                            luminance[y * width + x] = value;
                    }
                }
            }
        }
        else
        {
            var qr = QRCodeGenerator.Create(Content, QREccLevel.L, new QRCodeGeneratorOptions { Version = 3 });
            (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => row > 20 && ((row + column) & 1) == 0 ? !qr[row, column] : qr[row, column], qr.Size, qr.Size, 4f, 0f, 0f);
        }
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out _);
        var binarized = new byte[luminance.Length];

        var differs = LocalBinarizer.TryBinarize(luminance, width, height, negative: false, threshold, binarized, new int[LocalBinarizer.ScratchLength(width, height)], out _);

        await Assert.That(differs).IsFalse().Because(name);
    }

    /// <summary>The gate's other side: the lit symbols of this class do move pixels between classes, or the pass would never run for them.</summary>
    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task TryBinarize_UnevenLight_DisagreesWithGlobalThreshold(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out _);

        var differs = LocalBinarizer.TryBinarize(luminance, width, height, negative: false, threshold, new byte[luminance.Length], new int[LocalBinarizer.ScratchLength(width, height)], out _);

        await Assert.That(differs).IsTrue();
    }
}
