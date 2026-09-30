using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A destination too short for a symbol's text, on the image path: the read that does not fit is terminal for the finder
/// candidate that made it, as in rMQR (microqr-decoder.md, Decisions). The candidate's other grids and searches are not tried,
/// so the call costs about what a sized one does and reports the read the sized call returns, not another grid's; the other
/// candidates are still tried, so another symbol that fits is read.
/// </summary>
public class MicroQRCodeDecoderDestinationTest
{
    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel EccLevel, string Content, float Degrees)> Symbols()
    {
        foreach (var (version, eccLevel, content) in MicroQRCodeDecoderImageTest.AllVersionEccCombinations())
        {
            foreach (var degrees in new[] { 0f, 30f, 90f })
                yield return (version, eccLevel, content, degrees);
        }
    }

    /// <summary>
    /// The grids are sampled in the same order whatever the destination, so the first one that reads is the sized call's answer;
    /// one character short, that read does not fit and is the answer too.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Symbols))]
    public async Task DecodeImage_DestinationOneShort_ReportsTheReadTheSizedCallReturns(MicroQRVersion version, MicroQREccLevel eccLevel, string content, float degrees)
    {
        var (luminance, width, height) = Render(content, version, eccLevel, pixelsPerModule: 8, degrees);
        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out var sizedInfo)).IsTrue()
            .Because($"{version}-{eccLevel} at {degrees}°: {sizedInfo.Status}");
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);

        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[content.Length - 1], out var written, out var info);

        await Assert.That((ok, written, info.Status, info.Version)).IsEqualTo((false, 0, DecodeStatus.DestinationTooSmall, sizedInfo.Version));
    }

    /// <summary>
    /// The cost: while the read went on through the candidate's other grids, the arbitrary-orientation frames and their scale
    /// and perspective searches, a destination of 2 characters cost about 270 times a sized call here (2026-09-30), and a grid
    /// that reads its transpose after a read that does not fit lets one scale search run, about 40 times. Stopped at the read,
    /// the call costs what a sized one does. The test runs alone, and its slack of 30 ms is for a scheduling stall.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DecodeImage_DestinationTooSmall_CostsAboutASizedCall()
    {
        var (luminance, width, height) = Render("MICRO QR M4 TEST", MicroQRVersion.M4, MicroQREccLevel.M, pixelsPerModule: 4, degrees: 0f);
        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var tiny = new char[2];
        MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out _);

        const int iterations = 200;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var ok = false;
        var info = default(MicroQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((ok, info.Status, info.Version)).IsEqualTo((false, DecodeStatus.DestinationTooSmall, MicroQRVersion.M4));
        var bound = TimeSpan.FromTicks(sizedElapsed.Ticks * 5) + TimeSpan.FromMilliseconds(30);
        await Assert.That(tinyElapsed).IsLessThan(bound)
            .Because($"a read that does not fit ends its candidate: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");

        // The inverted pass stops at the same read
        var inverted = new byte[luminance.Length];
        for (var i = 0; i < inverted.Length; i++)
            inverted[i] = (byte)(255 - luminance[i]);
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(inverted, width, height, tiny, out _, out var invertedInfo)).IsFalse();
        await Assert.That((invertedInfo.Status, invertedInfo.Version)).IsEqualTo((DecodeStatus.DestinationTooSmall, MicroQRVersion.M4));
    }

    /// <summary>
    /// Two symbols, the one tried first (its finder confirmed on more rows) too long for the destination: the read that does not
    /// fit ends that candidate only, and the other symbol's short text is returned, whichever of them is drawn above.
    /// </summary>
    [Test]
    public async Task DecodeImage_DestinationTooSmallForOneSymbol_StillFindsAnotherThatFits()
    {
        const string bigContent = "MICRO QR M4 TEST";
        var destination = new char[8];
        foreach (var bigAbove in new[] { true, false })
        {
            var (luminance, width, height) = RenderTwo(bigContent, MicroQRVersion.M4, MicroQREccLevel.M, "12345", MicroQRVersion.M2, MicroQREccLevel.L, bigAbove);
            await AssertTheBigSymbolIsTriedFirst(luminance, width, height, bigContent);

            var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);

            await Assert.That(ok).IsTrue().Because($"bigAbove={bigAbove}, status={info.Status}, version={info.Version}");
            await Assert.That(new string(destination, 0, written)).IsEqualTo("12345");
            await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M2);
        }
    }

    /// <summary>
    /// The symbol that fits is turned, so it reads only in the arbitrary-orientation path, after grids whose format information
    /// reads have opened the scale and perspective searches: a read that did not fit on the first candidate must end none of them.
    /// </summary>
    [Test]
    [Arguments(30f)]
    [Arguments(45f)]
    [Arguments(60f)]
    public async Task DecodeImage_DestinationTooSmallForOneSymbol_StillReadsATurnedSymbolThatFits(float degrees)
    {
        const string bigContent = "MICRO QR M4 TEST";
        var destination = new char[8];
        var (luminance, width, height) = RenderTwo(bigContent, MicroQRVersion.M4, MicroQREccLevel.M, "12345", MicroQRVersion.M2, MicroQREccLevel.L, bigAbove: true, smallDegrees: degrees);
        await AssertTheBigSymbolIsTriedFirst(luminance, width, height, bigContent);

        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);

        await Assert.That(ok).IsTrue().Because($"degrees={degrees}, status={info.Status}, version={info.Version}");
        await Assert.That(new string(destination, 0, written)).IsEqualTo("12345");
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M2);
    }

    /// <summary>Neither symbol fits: the read that did not fit is reported, the first candidate's on the tie, and no text.</summary>
    [Test]
    public async Task DecodeImage_DestinationTooSmallForBothSymbols_ReportsTheFirstCandidates()
    {
        const string bigContent = "MICRO QR M4 TEST";
        var destination = new char[8];
        var (luminance, width, height) = RenderTwo(bigContent, MicroQRVersion.M4, MicroQREccLevel.M, "HELLO WORLD", MicroQRVersion.M3, MicroQREccLevel.L, bigAbove: false);
        await AssertTheBigSymbolIsTriedFirst(luminance, width, height, bigContent);

        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);

        await Assert.That((ok, written, info.Status, info.Version)).IsEqualTo((false, 0, DecodeStatus.DestinationTooSmall, MicroQRVersion.M4));
    }

    /// <summary>Premise: a sized call returns the big symbol, so its candidate is the first one tried.</summary>
    private static async Task AssertTheBigSymbolIsTriedFirst(byte[] luminance, int width, int height, string bigContent)
    {
        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var written, out _)).IsTrue();
        await Assert.That(new string(sized, 0, written)).IsEqualTo(bigContent);
    }

    private static (byte[] Luminance, int Width, int Height) Render(string content, MicroQRVersion version, MicroQREccLevel eccLevel, int pixelsPerModule, float degrees)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        var symbolPx = data.Size * pixelsPerModule;
        var canvasPx = (int)(symbolPx * 1.5f) + 16;
        using var bitmap = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(canvasPx / 2f, canvasPx / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-symbolPx / 2f, -symbolPx / 2f);
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, symbolPx, symbolPx), data, SKColors.Black, SKColors.White);
        }
        return (Luminance(bitmap), canvasPx, canvasPx);
    }

    /// <summary>
    /// The big symbol at 8 px/module, whose finder is confirmed on more rows, and the small one at 5, one above the other; the
    /// small one turned about its centre by <paramref name="smallDegrees"/>, in a cell wide enough for any turn.
    /// </summary>
    private static (byte[] Luminance, int Width, int Height) RenderTwo(string bigContent, MicroQRVersion bigVersion, MicroQREccLevel bigLevel, string smallContent, MicroQRVersion smallVersion, MicroQREccLevel smallLevel, bool bigAbove, float smallDegrees = 0f)
    {
        var big = MicroQRCodeGenerator.Create(bigContent, bigLevel, new MicroQRCodeGeneratorOptions { Version = bigVersion });
        var small = MicroQRCodeGenerator.Create(smallContent, smallLevel, new MicroQRCodeGeneratorOptions { Version = smallVersion });
        var bigPx = big.Size * 8;
        var smallPx = small.Size * 5;
        var smallCell = (int)Math.Ceiling(smallPx * 1.5f);
        const int margin = 16;
        var width = Math.Max(bigPx, smallCell) + 2 * margin;
        var height = bigPx + smallCell + 3 * margin;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            var (bigTop, smallTop) = bigAbove ? (margin, 2 * margin + bigPx) : (2 * margin + smallCell, margin);
            SymbolRenderer.Render(canvas, SKRect.Create(margin, bigTop, bigPx, bigPx), big, SKColors.Black, SKColors.White);
            canvas.Save();
            canvas.Translate(margin + smallCell / 2f, smallTop + smallCell / 2f);
            canvas.RotateDegrees(smallDegrees);
            SymbolRenderer.Render(canvas, SKRect.Create(-smallPx / 2f, -smallPx / 2f, smallPx, smallPx), small, SKColors.Black, SKColors.White);
            canvas.Restore();
        }
        return (Luminance(bitmap), width, height);
    }

    private static byte[] Luminance(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
        }
        return luminance;
    }
}
