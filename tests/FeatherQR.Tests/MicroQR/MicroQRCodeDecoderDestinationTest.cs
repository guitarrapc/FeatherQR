using SkiaSharp;
using FeatherQR.Internals.MicroQR;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A destination too short for a symbol's text, on the image path: the read that does not fit is terminal for the finder
/// candidate that made it, as in rMQR (microqr-decoder.md, Decisions). The candidate's other grids and searches are not tried,
/// so the call costs about what a sized one does and reports the read the sized call returns, not another grid's; the other
/// candidates are still tried, so another symbol that fits is read, but not one inside the symbol that read, which is a
/// finder-like pattern in its own data.
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
    /// A grid whose text does not fit the destination is not read again by coverage. On a real image that re-read reads the same
    /// text; on this crafted one (<see cref="TwoTextRenderer"/>) it reads another, and a call too short for the text a sized call
    /// returns reported the other as a success. Upright, the symbol reads in the axis-aligned path, and turned, in the
    /// arbitrary-orientation one, each of which stops at the read that does not fit.
    /// </summary>
    [Test]
    [Arguments(0.0)]
    [Arguments(20.0)]
    public async Task DecodeImage_DestinationTooSmall_IsNotReadAgainByCoverage(double degrees)
    {
        const string text = "1234567890";
        const string coverageText = "12";
        const int quietZone = 2;
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4, QuietZoneSize = quietZone, MaskPattern = 0 };
        var first = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, options);
        var second = MicroQRCodeGenerator.Create(coverageText, MicroQREccLevel.L, options);
        var renderer = new TwoTextRenderer(first.Size, first.Size, pixelsPerModule: 6, offset: 0.25f, degrees);
        var luminance = renderer.Render((row, column) => first[row, column], (row, column) => second[row, column]);
        var (width, height) = (renderer.Width, renderer.Height);
        var (thresholded, readByCoverage) = renderer.SampleGrids(luminance, quietZone);
        var size = first.Size - 2 * quietZone;
        await Assert.That((DecodeGrid(thresholded, size), DecodeGrid(readByCoverage, size))).IsEqualTo((text, coverageText))
            .Because("premise: the thresholded grid reads one text and the coverage grid the other");

        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out _)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(text)
            .Because("premise: the sized call reads the thresholded grid's text");

        var tiny = new char[coverageText.Length];
        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out var written, out var info);

        await Assert.That((ok, written, info.Status, info.Version)).IsEqualTo((false, 0, DecodeStatus.DestinationTooSmall, MicroQRVersion.M4))
            .Because($"the call too short for the sized call's text reports that read, not {new string(tiny, 0, written)}");
    }

    private static string DecodeGrid(byte[] grid, int size)
    {
        var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var status = MicroQRMatrixDecoder.DecodeMatrix(grid, size, destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : status.ToString();
    }

    /// <summary>
    /// The cost: while the read went on through the candidate's other grids, the arbitrary-orientation frames and their scale
    /// and perspective searches, a destination of 2 characters cost about 200 to 270 times a sized call here on a process's first
    /// calls and about 1,100 times once warm (2026-10-01), and a grid that reads its transpose after a read that does not fit lets
    /// one scale search run, about 40 times on the first calls and 70 warm. Stopped at the read, the call costs what a sized one
    /// does. The test runs alone; its bound is 5 times a sized call and 30 ms for a scheduling stall, which the 2,000 runs keep
    /// near 10 times a sized call once warm, below either regression.
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

        const int iterations = 2_000;
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
    /// Renders whose scan ranks a finder-like pattern in the symbol's own data after the real finder: it lay inside the symbol that
    /// read, at (61,110) and (49,56), and was searched in full after the read that did not fit, about 40 to 47 times a sized call
    /// once the code was warm and 12 to 38 on a process's first calls (2026-10-01). A candidate inside a symbol that read is skipped, so the
    /// call costs what a sized one does. Transposed, the same renders are a mirrored capture, read by the transposed grid, whose
    /// read that does not fit has to keep its corners as the straight one's does. The 100 runs keep the regression several times
    /// the slack of 30 ms, which is for a scheduling stall.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments("63028458518747710056101171", MicroQREccLevel.M, 5.1, 93.0, false)]
    [Arguments("98411748017212729364", MicroQREccLevel.Q, 3.6, 133.0, false)]
    [Arguments("63028458518747710056101171", MicroQREccLevel.M, 5.1, 93.0, true)]
    [Arguments("98411748017212729364", MicroQREccLevel.Q, 3.6, 133.0, true)]
    public async Task DecodeImage_DestinationTooSmall_SkipsACandidateInsideTheSymbolThatRead(string content, MicroQREccLevel eccLevel, double pixelsPerModule, double degrees, bool mirrored)
    {
        var (luminance, side) = RenderTurnedSupersampled(content, eccLevel, pixelsPerModule, degrees);
        if (mirrored)
            luminance = Transpose(luminance, side);
        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var tiny = new char[2];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, side, side, sized, out var sizedWritten, out var sizedInfo)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);
        await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, side, side, sizedInfo.Corners)).IsTrue()
            .Because("premise: the scan ranks a finder-like pattern inside the symbol after its finder");
        MicroQRCodeDecoder.TryDecodeImage(luminance, side, side, tiny, out _, out _);

        const int iterations = 100;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            MicroQRCodeDecoder.TryDecodeImage(luminance, side, side, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var info = default(MicroQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            MicroQRCodeDecoder.TryDecodeImage(luminance, side, side, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((info.Status, info.Version, info.Corners.IsEmpty)).IsEqualTo((DecodeStatus.DestinationTooSmall, MicroQRVersion.M4, true));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * 5) + TimeSpan.FromMilliseconds(30))
            .Because($"a candidate inside the symbol that read is skipped: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
    }

    /// <summary>
    /// The first render above a copy of itself: neither symbol fits the destination, and each has a finder-like pattern ranked
    /// after its own finder. Every read that does not fit keeps its corners, not only the first, so the pattern inside the second
    /// symbol is skipped as the first one's is; searched in full, it cost about 10 to 25 times a sized call on a process's first
    /// calls and about 40 once warm (2026-10-01).
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DecodeImage_DestinationTooSmallForTwoSymbols_SkipsACandidateInsideEach()
    {
        const string content = "63028458518747710056101171";
        var (single, side) = RenderTurnedSupersampled(content, MicroQREccLevel.M, 5.1, 93.0);
        var (luminance, width, height) = SkipPremise.StackTwice(single, side, side);
        var sized = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var tiny = new char[2];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out var sizedInfo)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);
        foreach (var corners in SkipPremise.BothCopies(sizedInfo.Corners, side))
        {
            await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, width, height, corners)).IsTrue()
                .Because($"premise: the scan ranks a finder-like pattern inside the symbol at {corners.TopLeft} after its finder");
        }
        MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out _);

        const int iterations = 100;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var info = default(MicroQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((info.Status, info.Version, info.Corners.IsEmpty)).IsEqualTo((DecodeStatus.DestinationTooSmall, MicroQRVersion.M4, true));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * 5) + TimeSpan.FromMilliseconds(30))
            .Because($"a candidate inside either symbol that read is skipped: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
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

    /// <summary>
    /// An M4 symbol turned about the image centre, each pixel the mean of 2 × 2 point samples of the modules (dark 20, light 235),
    /// in a square image half as wide again as the symbol and its quiet zone.
    /// </summary>
    private static (byte[] Luminance, int Side) RenderTurnedSupersampled(string content, MicroQREccLevel eccLevel, double pixelsPerModule, double degrees)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 });
        var side = (int)(data.Size * pixelsPerModule * 1.5) + 16;
        var luminance = new byte[side * side];
        var cos = Math.Cos(-degrees * Math.PI / 180);
        var sin = Math.Sin(-degrees * Math.PI / 180);
        var centre = side / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var px = x + 0.25 + 0.5 * sx - centre;
                        var py = y + 0.25 + 0.5 * sy - centre;
                        var column = (int)Math.Floor((px * cos - py * sin) / pixelsPerModule + data.Size / 2.0);
                        var row = (int)Math.Floor((px * sin + py * cos) / pixelsPerModule + data.Size / 2.0);
                        sum += column >= 0 && row >= 0 && column < data.Size && row < data.Size && data[row, column] ? 20 : 235;
                    }
                }
                luminance[y * side + x] = (byte)Math.Round(sum / 4);
            }
        }
        return (luminance, side);
    }

    /// <summary>A square image with its rows and columns swapped: a mirrored capture, as a front camera takes one.</summary>
    private static byte[] Transpose(byte[] luminance, int side)
    {
        var transposed = new byte[luminance.Length];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                transposed[x * side + y] = luminance[y * side + x];
        }
        return transposed;
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
