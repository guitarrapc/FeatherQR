using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol lit unevenly has no single threshold between its modules; the decoders binarize again against each region's level when the global threshold reads nothing.
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

    public enum Symbology { StandardQR, MicroQR, RmQR }

    /// <summary>The lighting of <see cref="Lighting"/> a global threshold still reads, per symbology; every other case needs the regional pass.</summary>
    private static readonly Dictionary<Symbology, (UnevenLight, float)[]> ReadByGlobalThreshold = new()
    {
        [Symbology.StandardQR] = [(UnevenLight.Ramp, 45f)],
        [Symbology.MicroQR] = [(UnevenLight.Ramp, 45f), (UnevenLight.Ramp, 135f), (UnevenLight.Ramp, 315f)],
        [Symbology.RmQR] = [(UnevenLight.Ramp, 0f), (UnevenLight.Ramp, 45f), (UnevenLight.Ramp, 135f), (UnevenLight.Ramp, 315f)],
    };

    /// <summary>
    /// Asserts which pass reads the case: a decode by the global threshold proves nothing about the regional pass.
    /// </summary>
    private static async Task AssertWhichPassReads(Symbology symbology, UnevenLight light, float degrees, byte[] luminance, int width, int height)
    {
        var global = ReadByGlobalThreshold[symbology].Contains((light, degrees));
        await Assert.That(GlobalAttemptsRead(symbology, luminance, width, height)).IsEqualTo(global).Because(global ? "listed as read by the global threshold" : "needs the regional pass");
    }

    /// <summary>Whether either global polarity reads the image, sweep included: the attempts made before the regional pass.</summary>
    private static bool GlobalAttemptsRead(Symbology symbology, byte[] luminance, int width, int height)
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
            var status = symbology switch
            {
                Symbology.StandardQR => QRImageDecoder.DecodeLuminanceCore(image, bins, width, height, destination, out _, out _),
                Symbology.MicroQR => MicroQRImageDecoder.DecodeLuminanceCore(image, bins, width, height, destination, out _, out _),
                _ => RmQRImageDecoder.DecodeLuminanceCore(image, bins, width, height, destination, out _, out _),
            };
            if (status == DecodeStatus.Success)
                return true;
        }
        return false;
    }

    [Test]
    [MethodDataSource(nameof(Lighting))]
    public async Task StandardQR_UnevenLight_Decodes(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        Func<int, int, bool> isDark = (row, column) => qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, light, degrees, depth);

        await AssertWhichPassReads(Symbology.StandardQR, light, degrees, luminance, width, height);

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

        await AssertWhichPassReads(Symbology.MicroQR, light, degrees, luminance, width, height);

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

        await AssertWhichPassReads(Symbology.RmQR, light, degrees, luminance, width, height);

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
        Negate(luminance);
        await AssertWhichPassReads(Symbology.StandardQR, light, degrees, luminance, width, height);

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
        Negate(luminance);
        await AssertWhichPassReads(Symbology.MicroQR, light, degrees, luminance, width, height);

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
        Negate(luminance);
        await AssertWhichPassReads(Symbology.RmQR, light, degrees, luminance, width, height);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{light} towards {degrees} at depth {depth}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    /// <summary>
    /// Renders whose finder falls between the rows a strided scan visits: the regional attempt reads them only through its full sweep.
    /// </summary>
    public static IEnumerable<(bool, float, float, float, bool, UnevenLight, float, float)> BetweenScannedRows()
    {
        // (Micro QR, turn, offset x, offset y, anti-aliased, light, light direction, depth)
        // Chosen to hold under small nudges of turn, offset and depth, so the case keeps needing the sweep
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
        => await Assert.That(GlobalAttemptsRead(microQr ? Symbology.MicroQR : Symbology.RmQR, luminance, width, height)).IsFalse();

    /// <summary>
    /// The group's premise: on the regional binarization the strided scan finds no finder and the sweep does.
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

    /// <summary>What the Micro QR decoder reads with the evidence rule off: a test's premise that the rule is what refuses the read.</summary>
    private static (bool Success, string Text, MicroQRCodeDecodeInfo Info) ReadWithoutTheRule(byte[] luminance, int width, int height)
    {
        MicroQRGridEvidence.SuspendedOnThisThread = true;
        try
        {
            var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            return (success, text, info);
        }
        finally
        {
            MicroQRGridEvidence.SuspendedOnThisThread = false;
        }
    }

    /// <summary>
    /// An evenly lit Standard QR image read as M1 at its own finder: the separator and finder match, and only the timing patterns and the quiet zone tell the corner of another symbol from an M1.
    /// </summary>
    [Test]
    [Arguments("PQJB0blseF3NxjuVLwKq7dFLjJAsOujjtqo9w9YI3j", QREccLevel.Q, 3.9258766f, 270f, 2.6744332f, 2.0729795f, 270f)]
    [Arguments("WwsLYCfK87uLJMiurOaPnLPcyoMFCF4cH2", QREccLevel.M, 3.342065f, 0f, 2.522543f, 1.4961945f, 225f)]
    public async Task MicroQR_EvenlyLitStandardQRImage_NotReadAsM1(string payload, QREccLevel level, float pixelsPerModule, float turn, float offsetX, float offsetY, float lightDegrees)
    {
        var symbol = QRCodeGenerator.Create(payload, level);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => symbol[row, column], symbol.Size, symbol.Size, pixelsPerModule, turn, offsetX, offsetY, false, UnevenLight.Ramp, lightDegrees, 0f);
        var (premiseRead, _, premiseInfo) = ReadWithoutTheRule(luminance, width, height);
        await Assert.That(premiseRead).IsTrue();
        await Assert.That(premiseInfo.Version).IsEqualTo(MicroQRVersion.M1);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because($"read \"{text}\" as {info.Version}");
    }

    /// <summary>
    /// A real M1 symbol under a ramp: a grid near the real one passes M1's check with other text, while its structure does not earn the read, and the search goes on to the real grid.
    /// </summary>
    [Test]
    [Arguments(1.8233829f, 94.35809f, 0.18023811f, 0.41934952f, 0.46335387f, false)]
    [Arguments(1.7421498f, 250.21765f, 0.034180205f, 0.023200577f, 0.52600694f, true)]
    public async Task MicroQR_M1UnderRamp_NotMisread(float pixelsPerModule, float turn, float offsetX, float offsetY, float depth, bool reflectanceReversed)
    {
        var qr = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, turn, offsetX, offsetY, true, UnevenLight.Ramp, 225f, depth);
        if (reflectanceReversed)
            Negate(luminance);
        var (premiseRead, premiseText, _) = ReadWithoutTheRule(luminance, width, height);
        await Assert.That(premiseRead).IsTrue();
        await Assert.That(premiseText).IsNotEqualTo("1");

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("1");
    }

    /// <summary>
    /// Another symbology's image binarized regionally passes an M1 check on some grids; that is not a read.
    /// </summary>
    [Test]
    [Arguments("2553", RmQREccLevel.M, 270f)]
    [Arguments("NZN0", RmQREccLevel.H, 180f)]
    [Arguments("1W0H", RmQREccLevel.M, 270f)]
    [Arguments("sknh", RmQREccLevel.H, 90f)]
    public async Task MicroQR_UnevenLightRmQRImage_NoFalseRead(string payload, RmQREccLevel level, float lightDegrees)
    {
        var rmqr = RmQRCodeGenerator.Create(payload, level);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => rmqr[row, column], rmqr.Width, rmqr.Height, 2.2f, 13f, 0f, 0f, true, UnevenLight.Shadow, lightDegrees, 0.35f);
        Negate(luminance);
        await Assert.That(ReadWithoutTheRule(luminance, width, height).Success).IsTrue();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because($"read \"{text}\" as {info.Version}");
    }

    /// <summary>
    /// Another symbology's texture can correct to a read at M3-M's limit; the regional pass refuses M3-M and M4-M reads at their limit.
    /// </summary>
    public static IEnumerable<(bool RmQR, string Payload, QREccLevel Level, float PixelsPerModule, float Turn, float OffsetX, float OffsetY, UnevenLight Light, float LightDegrees, float Depth)> ForeignImagesAtTheCorrectionLimit()
    {
        yield return (false, "tddutrZIg", QREccLevel.L, 3f, 171.35826f, 0.69572777f, 0.75405896f, UnevenLight.Ramp, 270f, 0.42188275f);
        yield return (false, "aLQ0lryjgEo1p7rRwXssrRGpx8d", QREccLevel.M, 2.5f, 54.391262f, 0.27924117f, 0.36606738f, UnevenLight.Ramp, 90f, 0.4495769f);
        yield return (false, "hD567gtHuB8XxZakOc4mHwAo", QREccLevel.L, 3f, 282.45126f, 0.56217444f, 0.03247166f, UnevenLight.Shadow, 270f, 0.406949f);
        yield return (true, "8Q3eYUVAd4", default, 2.5f, 90f, 0.10880279f, 0.78370607f, UnevenLight.Shadow, 0f, 0.43216985f);
    }

    [Test]
    [MethodDataSource(nameof(ForeignImagesAtTheCorrectionLimit))]
    public async Task MicroQR_UnevenLightForeignImage_NoReadAtTheCorrectionLimit(bool rmqr, string payload, QREccLevel level, float pixelsPerModule, float turn, float offsetX, float offsetY, UnevenLight light, float lightDegrees, float depth)
    {
        (byte[] Luminance, int Width, int Height) image;
        if (rmqr)
        {
            var symbol = RmQRCodeGenerator.Create(payload, RmQREccLevel.H);
            image = UnevenLightingRenderer.Render((row, column) => symbol[row, column], symbol.Width, symbol.Height, pixelsPerModule, turn, offsetX, offsetY, true, light, lightDegrees, depth);
        }
        else
        {
            var symbol = QRCodeGenerator.Create(payload, level);
            image = UnevenLightingRenderer.Render((row, column) => symbol[row, column], symbol.Size, symbol.Size, pixelsPerModule, turn, offsetX, offsetY, true, light, lightDegrees, depth);
        }

        await Assert.That(RegionalReadsWithoutRefusal(image.Luminance, image.Width, image.Height).Any(read => read.Status == DecodeStatus.Success && read.ErrorsCorrected == MicroQRConstants.GetErrorCorrectionCapacity(read.Version, read.EccLevel))).IsTrue();

        var success = MicroQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, out var text, out var info);

        await Assert.That(success).IsFalse().Because($"read \"{text}\" as {info.Version}-{info.EccLevel} with {info.ErrorsCorrected} corrected");
    }

    /// <summary>
    /// A verdict from another symbology's texture at M3-M's or M4-M's correction limit is refused like a read.
    /// </summary>
    public static IEnumerable<(bool RmQR, string Payload, float PixelsPerModule, float Turn, float OffsetX, float OffsetY, UnevenLight Light, float LightDegrees, float Depth)> ForeignVerdictsAtTheCorrectionLimit()
    {
        yield return (false, "qrf2BRFy", 2.2f, 180f, 0.87237096f, 0.5018127f, UnevenLight.Ramp, 0f, 0.30661732f);
        yield return (true, "DC3XnKYuLsSY", 2.4056265f, 3.60112f, -0.45129496f, 0.29853797f, UnevenLight.Shadow, 315f, 0.37471867f);
    }

    [Test]
    [MethodDataSource(nameof(ForeignVerdictsAtTheCorrectionLimit))]
    public async Task MicroQR_UnevenLightForeignImage_NoVerdictAtTheCorrectionLimit(bool rmqr, string payload, float pixelsPerModule, float turn, float offsetX, float offsetY, UnevenLight light, float lightDegrees, float depth)
    {
        (byte[] Luminance, int Width, int Height) image;
        if (rmqr)
        {
            var symbol = RmQRCodeGenerator.Create(payload, RmQREccLevel.H);
            image = UnevenLightingRenderer.Render((row, column) => symbol[row, column], symbol.Width, symbol.Height, pixelsPerModule, turn, offsetX, offsetY, true, light, lightDegrees, depth);
        }
        else
        {
            var symbol = QRCodeGenerator.Create(payload, QREccLevel.H);
            image = UnevenLightingRenderer.Render((row, column) => symbol[row, column], symbol.Size, symbol.Size, pixelsPerModule, turn, offsetX, offsetY, true, light, lightDegrees, depth);
        }
        Negate(image.Luminance);
        await Assert.That(RegionalReadsWithoutRefusal(image.Luminance, image.Width, image.Height).Any(read => read.Status == DecodeStatus.UnmappedCharacter && read.ErrorsCorrected == MicroQRConstants.GetErrorCorrectionCapacity(read.Version, read.EccLevel))).IsTrue();

        MicroQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, out _, out var info);

        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.UnmappedCharacter).Because($"{info.Version}-{info.EccLevel} with {info.ErrorsCorrected} corrected");
    }

    /// <summary>What the Micro QR regional pass reads with its refusals off, one decode a polarity: a refusal test's premise.</summary>
    private static List<MicroQRCodeDecodeInfo> RegionalReadsWithoutRefusal(byte[] luminance, int width, int height)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        Binarizer.InvertHistogram(histogram);
        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        var reads = new List<MicroQRCodeDecodeInfo>();
        foreach (var (negative, threshold) in new[] { (false, positiveThreshold), (true, negativeThreshold) })
        {
            var binarized = new byte[luminance.Length];
            if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, threshold, binarized, new int[LocalBinarizer.ScratchLength(width, height)], out var darkCount))
                continue;
            var bins = new int[Binarizer.HistogramBins];
            bins[LocalBinarizer.Dark] = darkCount;
            bins[LocalBinarizer.Light] = binarized.Length - darkCount;
            MicroQRGridEvidence.SuspendedOnThisThread = true;
            try
            {
                MicroQRImageDecoder.DecodeLuminanceCore(binarized, bins, width, height, new char[64], out _, out var info);
                reads.Add(info);
            }
            finally
            {
                MicroQRGridEvidence.SuspendedOnThisThread = false;
            }
        }
        return reads;
    }

    /// <summary>
    /// A real symbol shows its structure, so it may use every correction its level allows: reads at every level's limit, one short of it, and a one-character read that needed correction are kept; a short destination reports a short destination.
    /// </summary>
    [Test]
    [Arguments("12345", MicroQREccLevel.M, MicroQRVersion.M3, new[] { 14, 14, 14, 12, 14, 10, 14, 8 })]
    [Arguments("12345", MicroQREccLevel.M, MicroQRVersion.M4, new[] { 16, 16, 16, 14, 16, 12, 16, 10, 16, 8 })]
    [Arguments("12345678", MicroQREccLevel.L, MicroQRVersion.M2, new[] { 12, 12 })]
    [Arguments(Content, MicroQREccLevel.L, MicroQRVersion.M3, new[] { 14, 14 })]
    [Arguments(Content, MicroQREccLevel.L, MicroQRVersion.M4, new[] { 16, 16, 16, 14 })]
    [Arguments(Content, MicroQREccLevel.L, MicroQRVersion.M4, new[] { 16, 16, 16, 14, 16, 12 })]
    [Arguments("3", MicroQREccLevel.L, MicroQRVersion.M3, new[] { 14, 14 })]
    [Arguments("12345", MicroQREccLevel.L, MicroQRVersion.M3, new[] { 14, 14, 14, 12 })]
    public async Task MicroQR_DamagedUnevenLight_ReadByTheRegionalPass(string payload, MicroQREccLevel level, MicroQRVersion version, int[] flippedModules)
    {
        var qr = MicroQRCodeGenerator.Create(payload, level, new MicroQRCodeGeneratorOptions { Version = version });
        // Flipped data modules, (row, column) pairs in symbol coordinates: each one error to correct
        var flipped = new HashSet<(int, int)>();
        for (var i = 0; i < flippedModules.Length; i += 2)
            flipped.Add((flippedModules[i], flippedModules[i + 1]));
        Func<int, int, bool> isDark = (row, column) => flipped.Contains((row - 2, column - 2)) ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);
        var limit = MicroQRConstants.GetErrorCorrectionCapacity(version, level);
        await Assert.That(flipped.Count).IsLessThanOrEqualTo(limit);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[payload.Length - 1], out _, out var shortInfo);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(payload);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(flipped.Count);
        await Assert.That(shortInfo.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>A turned empty symbol under a shadow is read by the regional pass.</summary>
    [Test]
    public async Task MicroQR_EmptySymbolTurnedUnevenLight_ReadByALaterGrid()
    {
        var qr = MicroQRCodeGenerator.Create("", MicroQREccLevel.M);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 2.5f, 220.17834f, -0.16711113f, 2.105269f, false, UnevenLight.Shadow, 90f, 0.39125693f);
        await Assert.That(qr.Version).IsEqualTo(MicroQRVersion.M3);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("");
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M3);
    }

    /// <summary>Another symbology's texture correcting to an empty read is not a read.</summary>
    [Test]
    [Arguments(0.375f)]
    [Arguments(0.385f)]
    public async Task MicroQR_UnevenLightRmQRImage_NoEmptyRead(float depth)
    {
        var rmqr = RmQRCodeGenerator.Create("GJFLIBPZDCB0B", RmQREccLevel.M);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => rmqr[row, column], rmqr.Width, rmqr.Height, 2.5f, 338f, 0.5041975f, 0.74804866f, true, UnevenLight.Shadow, 270f, depth);
        Negate(luminance);
        await Assert.That(ReadWithoutTheRule(luminance, width, height).Success).IsTrue();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because($"read \"{text}\" as {info.Version}");
    }

    /// <summary>A real symbol holding no text is read by the regional pass.</summary>
    [Test]
    [Arguments(0f)]
    [Arguments(90f)]
    public async Task MicroQR_EmptySymbolUnevenLight_ReadByTheRegionalPass(float degrees)
    {
        var qr = MicroQRCodeGenerator.Create("", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3 });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, degrees, 0.55f);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("");
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M3);
    }

    /// <summary>The regional pass reads an M2 symbol the global threshold cannot.</summary>
    [Test]
    public async Task MicroQR_M2UnevenLight_ReadByTheRegionalPass()
    {
        var qr = MicroQRCodeGenerator.Create("12345678", MicroQREccLevel.L);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);
        await Assert.That(qr.Version).IsEqualTo(MicroQRVersion.M2);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo("12345678");
    }

    /// <summary>
    /// An M1 symbol only the regional pass reads is read in either polarity, and into a short destination reports a short destination.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MicroQR_M1UnevenLight_ReadByTheRegionalPass(bool reflectanceReversed)
    {
        var qr = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, 0.55f);
        if (reflectanceReversed)
            Negate(luminance);
        await Assert.That(qr.Version).IsEqualTo(MicroQRVersion.M1);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[1], out var shortCharsWritten, out var shortInfo);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("12345");
        await Assert.That(shortInfo.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(shortCharsWritten).IsEqualTo(0);
    }

    /// <summary>The same through the grids read from module boundaries, which is how a crisp symbol under 1.5 px/module is read: their quiet zone is counted too.</summary>
    [Test]
    [Arguments(1f, 180f, 0.3f)]
    [Arguments(1.25f, 270f, 0.4f)]
    public async Task MicroQR_M1UnevenLightLowDensity_ReadByTheRegionalPass(float pixelsPerModule, float degrees, float depth)
    {
        var qr = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly);
        // In the corner of a wider light grid, so the image is at least 33 px a side
        const int GridSize = 28;
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => row < qr.Size && column < qr.Size && qr[row, column], GridSize, GridSize, pixelsPerModule, 0f, 0f, 0f, false, UnevenLight.Shadow, degrees, depth);
        await Assert.That(qr.Version).IsEqualTo(MicroQRVersion.M1);
        await Assert.That(GlobalAttemptsRead(Symbology.MicroQR, luminance, width, height)).IsFalse();

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("12345");
    }

    /// <summary>
    /// A grid the global threshold samples off the real one reaches an unmapped Kanji cell at M3-M's or M4-M's limit without the structure to earn it, so no verdict is reported and the symbol is read.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M4, 4.363f, 245.54f, 2.320f, 3.068f)]
    [Arguments(MicroQRVersion.M4, 4.242f, 243.36f, 2.739f, 1.752f)]
    public async Task MicroQR_VerdictAtTheCorrectionLimitWithoutStructure_SymbolRead(MicroQRVersion version, float pixelsPerModule, float turn, float offsetX, float offsetY)
    {
        var qr = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row + 2, column + 2], qr.Size - 4, qr.Size - 4, pixelsPerModule, turn, offsetX, offsetY, true, UnevenLight.Shadow, 270f, 0.4f);
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        DecodeStatus suspendedStatus;
        MicroQRCodeDecodeInfo suspendedInfo;
        MicroQRGridEvidence.SuspendedOnThisThread = true;
        try
        {
            suspendedStatus = MicroQRImageDecoder.DecodeLuminanceCore(luminance, histogram, width, height, new char[64], out _, out suspendedInfo);
        }
        finally
        {
            MicroQRGridEvidence.SuspendedOnThisThread = false;
        }
        await Assert.That(suspendedStatus).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(suspendedInfo.ErrorsCorrected).IsEqualTo(MicroQRConstants.GetErrorCorrectionCapacity(suspendedInfo.Version, suspendedInfo.EccLevel));

        var globalStatus = MicroQRImageDecoder.DecodeLuminanceCore(luminance, histogram, width, height, new char[64], out _, out _);
        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(globalStatus).IsNotEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("12345");
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

    /// <summary>An image with no symbol under a lighting ramp reads as nothing.</summary>
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

    /// <summary>A symbol read by the regional pass into a short destination reports a short destination.</summary>
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
    /// On images of only 0 and 255 the regional binarization changes no pixel's class.
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

    /// <summary>
    /// A 40 % shadow on a crisp render of the grid and its quiet zone, 2 to 16 px/module, whichever pass reads it.
    /// Standard QR is version 1 at level H, among the first to fail.
    /// </summary>
    public static IEnumerable<(Symbology, int, float)> ShadowUpToFortyPercent()
    {
        foreach (var symbology in new[] { Symbology.StandardQR, Symbology.MicroQR, Symbology.RmQR })
        {
            foreach (var pixelsPerModule in new[] { 2, 3, 4, 6, 8, 12, 16 })
            {
                foreach (var degrees in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
                    yield return (symbology, pixelsPerModule, degrees);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(ShadowUpToFortyPercent))]
    public async Task UnevenLight_ShadowUpToFortyPercent_Decodes(Symbology symbology, int pixelsPerModule, float degrees)
    {
        var (luminance, width, height) = RenderShadow(symbology, pixelsPerModule, degrees, 0.4f, standardQRVersion: 1, standardQREccLevel: QREccLevel.H);

        var (status, text) = Decode(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>
    /// The same shadow on anti-aliased edges at 3.5 px/module, turned or not.
    /// </summary>
    public static IEnumerable<(Symbology, float, float)> AntiAliasedShadowUpToFortyPercent()
    {
        foreach (var symbology in new[] { Symbology.StandardQR, Symbology.MicroQR, Symbology.RmQR })
        {
            foreach (var turn in new[] { 0f, 13f, 45f })
            {
                foreach (var degrees in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
                    yield return (symbology, turn, degrees);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(AntiAliasedShadowUpToFortyPercent))]
    public async Task UnevenLight_AntiAliasedShadowUpToFortyPercent_Decodes(Symbology symbology, float turn, float degrees)
    {
        const float Scale = 3.5f;
        const float OffsetX = 0.3f;
        const float OffsetY = 0.7f;
        // The renderer adds a two-module margin, so each symbol is drawn without its own quiet zone
        string expected;
        (byte[] Luminance, int Width, int Height) image;
        switch (symbology)
        {
            case Symbology.StandardQR:
            {
                var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 0 });
                expected = Content;
                image = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, Scale, turn, OffsetX, OffsetY, true, UnevenLight.Shadow, degrees, 0.4f);
                break;
            }
            case Symbology.MicroQR:
            {
                var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
                expected = Content;
                image = UnevenLightingRenderer.Render((row, column) => qr[row + 2, column + 2], qr.Size - 4, qr.Size - 4, Scale, turn, OffsetX, OffsetY, true, UnevenLight.Shadow, degrees, 0.4f);
                break;
            }
            default:
            {
                var qr = RmQRCodeGenerator.Create("12345", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 });
                expected = "12345";
                image = UnevenLightingRenderer.Render((row, column) => qr[row + 2, column + 2], qr.Width - 4, qr.Height - 4, Scale, turn, OffsetX, OffsetY, true, UnevenLight.Shadow, degrees, 0.4f);
                break;
            }
        }

        var (status, text) = Decode(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>
    /// Past half the light on modules aligned with the 8 × 8 blocks, the regional pass reads where the shadow falls away from the lit blocks above and to the left.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR, 8, 180f)]
    [Arguments(Symbology.StandardQR, 8, 270f)]
    [Arguments(Symbology.MicroQR, 8, 180f)]
    [Arguments(Symbology.RmQR, 8, 180f)]
    public async Task UnevenLight_ShadowPastHalfOnBlockAlignedModules_RegionalPassDecodes(Symbology symbology, int pixelsPerModule, float degrees)
    {
        var (luminance, width, height) = RenderShadow(symbology, pixelsPerModule, degrees, 0.55f, standardQRVersion: 3, standardQREccLevel: QREccLevel.M);
        await Assert.That(GlobalAttemptsRead(symbology, luminance, width, height)).IsFalse();

        var (status, text) = Decode(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(Content);
    }

    private static (byte[] Luminance, int Width, int Height) RenderShadow(Symbology symbology, int pixelsPerModule, float degrees, float depth, int standardQRVersion, QREccLevel standardQREccLevel)
    {
        switch (symbology)
        {
            case Symbology.StandardQR:
            {
                var qr = QRCodeGenerator.Create(Content, standardQREccLevel, new QRCodeGeneratorOptions { Version = standardQRVersion });
                return UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, UnevenLight.Shadow, degrees, depth);
            }
            case Symbology.MicroQR:
            {
                var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
                return UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, UnevenLight.Shadow, degrees, depth);
            }
            default:
            {
                var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
                return UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, UnevenLight.Shadow, degrees, depth);
            }
        }
    }

    private static (DecodeStatus Status, string Text) Decode(Symbology symbology, byte[] luminance, int width, int height)
    {
        switch (symbology)
        {
            case Symbology.StandardQR:
            {
                QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                return (info.Status, text);
            }
            case Symbology.MicroQR:
            {
                MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                return (info.Status, text);
            }
            default:
            {
                RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                return (info.Status, text);
            }
        }
    }

    /// <summary>
    /// A failed decode writes no characters, even when the last attempt fails part-way through the bitstream.
    /// </summary>
    [Test]
    [Arguments(UnevenLight.Shadow, 0f, 0.55f)]
    [Arguments(UnevenLight.Ramp, 90f, 0.8f)]
    public async Task StandardQR_UnevenLightBitstreamFailsPartWay_WritesNoCharacters(UnevenLight light, float degrees, float depth)
    {
        var modules = BuildBitstreamFailingAfterText();
        const int quietZone = 4;
        const int columns = 21 + 2 * quietZone;
        // Mirrored: rows and columns swapped
        Func<int, int, bool> isDark = (row, column) =>
        {
            var r = column - quietZone;
            var c = row - quietZone;
            return r >= 0 && c >= 0 && r < 21 && c < 21 && modules[r * 21 + c] != 0;
        };
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, columns, columns, PixelsPerModule, light, degrees, depth);
        Negate(luminance);
        var destination = new char[64];

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>
    /// A destination too short for the second segment still reports no characters: the first segment was written before the second found no room.
    /// </summary>
    [Test]
    [Arguments(0f)]
    [Arguments(0.55f)]
    public async Task StandardQR_DestinationShortOfTheSecondSegment_WritesNoCharacters(float depth)
    {
        var qr = QRCodeGenerator.Create(new string('1', 40) + "abc", QREccLevel.L, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, PixelsPerModule, UnevenLight.Shadow, 0f, depth);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[40], out var written, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A real version 1-L symbol carrying Byte "HELLO" and then an undefined mode indicator: its decode writes five characters before it fails.</summary>
    private static byte[] BuildBitstreamFailingAfterText()
    {
        const int version = 1;
        const int size = 21;
        var eccInfo = QRCodeConstants.GetEccInfo(version, QREccLevel.L);
        var data = new byte[eccInfo.TotalDataCodewords];
        var writer = new BitWriter(data);
        writer.Write(0b0100, 4);
        writer.Write(5, 8);
        foreach (var ch in "HELLO")
            writer.Write(ch, 8);
        writer.Write(0b0110, 4);
        writer.Write(0, 4);
        writer.Flush();
        for (var i = writer.GetData().Length; i < data.Length; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11;
        var ecc = new byte[eccInfo.ECCPerBlock];
        EccBinaryEncoder.CalculateECC(data, ecc, eccInfo.ECCPerBlock);
        var codewords = new byte[data.Length + ecc.Length];
        data.CopyTo(codewords, 0);
        ecc.CopyTo(codewords, data.Length);
        var layout = ModulePlacer.GetLayout(version);
        var modules = new byte[size * size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, codewords);
        var mask = ModulePlacer.MaskCode(modules, size, version, layout.BlockedMask, QREccLevel.L);
        ModulePlacer.PlaceFormat(modules, size, QRCodeConstants.GetFormatBits(QREccLevel.L, mask));
        return modules;
    }
}
