using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// A destination too short for an rMQR symbol's text, on the image path: the read that does not fit ends its finder candidate
/// (rmqr-decoder.md, Decisions), and a later candidate inside the symbol that read is skipped, since it is a finder-like pattern
/// in that symbol's own data, not another symbol. <see cref="RmQRCodeDecoderImageTest"/> holds the other symbols that are still read.
/// </summary>
public class RmQRCodeDecoderDestinationTest
{
    /// <summary>
    /// Renders whose scan ranks a finder-like pattern in the symbol's data after the real finder, searched in full after the read
    /// that did not fit until it was skipped. Each reads through a different path, and each path's read that does not fit has to
    /// keep its corners for the skip: a frame's grid (R13x27 at 4 px/module, the pattern at (76,34), about 55 to 105 times a sized
    /// call), the module boundaries (R13x77 at 1.38 px/module, about 38 times) and the coverage re-read (R15x139 upscaled
    /// bilinearly to 1.31 px/module and mirrored, about 9 times; 2026-10-01). Skipped, the call costs what a sized one does. The
    /// bound is 3 times a sized call and 30 ms for a scheduling stall; with the calls warm, as in a run of the whole suite, the 30 ms
    /// is most of it for the two quick cases, and the bound comes to about 4, 6 to 13 and 23 times a sized call for the coverage,
    /// boundary and grid cases, each below the regression it guards there. The premise that the scan ranks such a pattern after
    /// the finder is asserted (<see cref="SkipPremise"/>), since a render a little larger or smaller may have none (R13x27 at 4.25,
    /// R13x77 at 1.40 and R15x139 at 1.32 px/module).
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments(RmQRVersion.R13x27, RmQREccLevel.M, "HELLO12345ABCDE", "nearest", 4f)]
    [Arguments(RmQRVersion.R13x77, RmQREccLevel.M, "VZA$R9CMKV0T7W.C", "nearest", 1.38f)]
    [Arguments(RmQRVersion.R15x139, RmQREccLevel.M, "41516104048408666070497882893323", "bilinear, mirrored", 1.3069739f)]
    public async Task DecodeImage_DestinationTooSmall_SkipsACandidateInsideTheSymbolThatRead(RmQRVersion version, RmQREccLevel eccLevel, string content, string renderer, float pixelsPerModule)
    {
        var data = RmQRCodeGenerator.Create(content, eccLevel, new RmQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = renderer switch
        {
            "bilinear, mirrored" => FlipHorizontally(BilinearUpscaleRenderer.Render((row, column) => data[row, column], data.Width, data.Height, pixelsPerModule)),
            _ => RenderNearest(data, pixelsPerModule),
        };

        var sized = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var tiny = new char[2];
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out var sizedInfo)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);
        await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, width, height, sizedInfo.Corners)).IsTrue()
            .Because("premise: the scan ranks a finder-like pattern inside the symbol after its finder");
        RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out _);

        const int iterations = 500;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var info = default(RmQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((info.Status, info.Version, info.Corners.IsEmpty)).IsEqualTo((DecodeStatus.DestinationTooSmall, version, true));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * 3) + TimeSpan.FromMilliseconds(30))
            .Because($"a candidate inside the symbol that read is skipped: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
    }

    /// <summary>
    /// The frame's-grid render above a copy of itself: neither symbol fits the destination, and each has a finder-like pattern
    /// ranked after its own finder. Every read that does not fit keeps its corners, not only the first, so the pattern inside the
    /// second symbol is skipped as the first one's is; searched in full, it cost about 30 to 80 times a sized call on a process's
    /// first calls and 60 to 70 once warm (2026-10-01).
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DecodeImage_DestinationTooSmallForTwoSymbols_SkipsACandidateInsideEach()
    {
        const string content = "HELLO12345ABCDE";
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x27 });
        var (single, singleWidth, singleHeight) = RenderNearest(data, 4f);
        var (luminance, width, height) = SkipPremise.StackTwice(single, singleWidth, singleHeight);

        var sized = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var tiny = new char[2];
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out var sizedInfo)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);
        foreach (var corners in SkipPremise.BothCopies(sizedInfo.Corners, singleHeight))
        {
            await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, width, height, corners)).IsTrue()
                .Because($"premise: the scan ranks a finder-like pattern inside the symbol at {corners.TopLeft} after its finder");
        }
        RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out _);

        const int iterations = 500;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var info = default(RmQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((info.Status, info.Version, info.Corners.IsEmpty)).IsEqualTo((DecodeStatus.DestinationTooSmall, RmQRVersion.R13x27, true));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * 3) + TimeSpan.FromMilliseconds(30))
            .Because($"a candidate inside either symbol that read is skipped: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
    }

    /// <summary>
    /// A grid whose text does not fit the destination is not read again by coverage. On a real image that re-read reads the same
    /// text; on this crafted one (<see cref="TwoTextRenderer"/>) it reads another, and a call too short for the text a sized call
    /// returns reported the other as a success.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R13x43, RmQREccLevel.M, 6, 0.25f)]
    [Arguments(RmQRVersion.R17x99, RmQREccLevel.H, 4, 0.3f)]
    public async Task DecodeImage_DestinationTooSmall_IsNotReadAgainByCoverage(RmQRVersion version, RmQREccLevel eccLevel, int pixelsPerModule, float offset)
    {
        const string text = "1234567890";
        const string coverageText = "12";
        const int quietZone = 2;
        var first = RmQRCodeGenerator.Create(text, eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        var second = RmQRCodeGenerator.Create(coverageText, eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        var renderer = new TwoTextRenderer(first.Width, first.Height, pixelsPerModule, offset, degrees: 0);
        var luminance = renderer.Render((row, column) => first[row, column], (row, column) => second[row, column]);
        var (width, height) = (renderer.Width, renderer.Height);
        var (thresholded, readByCoverage) = renderer.SampleGrids(luminance, quietZone);
        await Assert.That((DecodeGrid(thresholded, first.Width - 2 * quietZone, first.Height - 2 * quietZone), DecodeGrid(readByCoverage, first.Width - 2 * quietZone, first.Height - 2 * quietZone)))
            .IsEqualTo((text, coverageText))
            .Because("premise: the thresholded grid reads one text and the coverage grid the other");

        var sized = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out _)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(text);

        var tiny = new char[coverageText.Length];
        var ok = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out var written, out var info);

        await Assert.That((ok, written, info.Status, info.Version)).IsEqualTo((false, 0, DecodeStatus.DestinationTooSmall, version))
            .Because($"the call too short for the sized call's text reports that read, not {new string(tiny, 0, written)}");
    }

    private static string DecodeGrid(byte[] grid, int columns, int rows)
    {
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, columns, rows, destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : status.ToString();
    }

    /// <summary>The image flipped left to right: a mirrored capture.</summary>
    private static (byte[] Luminance, int Width, int Height) FlipHorizontally((byte[] Luminance, int Width, int Height) image)
    {
        var (luminance, width, height) = image;
        var flipped = new byte[luminance.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                flipped[y * width + width - 1 - x] = luminance[y * width + x];
        }
        return (flipped, width, height);
    }

    /// <summary>Each pixel the module under its centre, quiet zone included.</summary>
    private static (byte[] Luminance, int Width, int Height) RenderNearest(RmQRCodeData data, float pixelsPerModule)
    {
        var width = (int)Math.Ceiling(data.Width * pixelsPerModule);
        var height = (int)Math.Ceiling(data.Height * pixelsPerModule);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = Math.Min(data.Width - 1, (int)((x + 0.5) / pixelsPerModule));
                var row = Math.Min(data.Height - 1, (int)((y + 0.5) / pixelsPerModule));
                luminance[y * width + x] = data[row, column] ? (byte)0 : (byte)255;
            }
        }
        return (luminance, width, height);
    }
}
