namespace FeatherQR.Tests;

/// <summary>
/// The rules the decoders that read around a single finder, Micro QR and rMQR, follow for a destination too short for a
/// symbol's text, each held for both by one test (shared-candidate-scan-plan.md).
/// </summary>
/// <remarks>
/// <para>
/// A read that does not fit ends the finder candidate that made it: its grids are tried in the same order whatever the
/// destination, so it is the read a sized call returns, and the candidate's other grids and searches could only read something
/// else at many times the cost. It carries its corners until the scan has used them: a later candidate inside that symbol is a
/// finder-like pattern in its own data, and is skipped. The other candidates are still tried, so another symbol that fits is
/// read. The corners are reported with a read only.
/// </para>
/// <para>
/// The timing tests run alone; each one's bound is a multiple of a sized call and 30 ms for a scheduling stall, sized against the
/// regression it guards, which its summary gives as measured.
/// </para>
/// </remarks>
public class DestinationContractTest
{
    /// <summary>An image of one symbol, the text a sized call reads from it, and the symbol's version.</summary>
    public sealed record Image(SingleFinderDecoder Decoder, string Name, Func<(byte[] Luminance, int Width, int Height)> Render, string Text, string Version)
    {
        public override string ToString() => $"{Decoder}, {Name}";
    }

    /// <summary>An image of one symbol, the text a sized call reads from it, the symbol's version, and how its timing is held: the runs and the bound's multiple of a sized call.</summary>
    public sealed record TimedImage(SingleFinderDecoder Decoder, string Name, Func<(byte[] Luminance, int Width, int Height)> Render, string Text, string Version, int Iterations, int BoundFactor)
    {
        public override string ToString() => $"{Decoder}, {Name}";
    }

    /// <summary>
    /// An image of two symbols: the big one, tried first, and the other, with a destination of <see cref="DestinationLength"/>
    /// characters, too short for the big one's text.
    /// </summary>
    public sealed record TwoSymbols(SingleFinderDecoder Decoder, string Name, Func<(byte[] Luminance, int Width, int Height)> Render, string BigText, string BigVersion, string OtherText, string OtherVersion, int DestinationLength)
    {
        public override string ToString() => $"{Decoder}, {Name}";
    }

    /// <summary>An image whose thresholded grid reads <c>1234567890</c> and whose grid read by coverage reads <c>12</c> (<see cref="TwoTextRenderer"/>).</summary>
    public sealed record TwoTextImage(SingleFinderDecoder Decoder, string Name, Func<(byte[] Luminance, int Width, int Height, string Thresholded, string ReadByCoverage)> Render, string Version)
    {
        public override string ToString() => $"{Decoder}, {Name}";
    }

    private const string Text = "1234567890";
    private const string CoverageText = "12";

    public static IEnumerable<Func<Image>> SingleSymbols()
    {
        foreach (var (version, eccLevel, content) in MicroQRCodeDecoderImageTest.AllVersionEccCombinations())
        {
            foreach (var degrees in new[] { 0f, 30f, 90f })
                yield return () => new Image(SingleFinderDecoder.MicroQR, $"{version}-{eccLevel} at {degrees}°", () => MicroQRDestinationRenders.Render(content, version, eccLevel, pixelsPerModule: 8, degrees), content, version.ToString());
        }
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            // R11x27-M holds 8 alphanumerics
            var content = version == RmQRVersion.R11x27 ? "RMQR 27" : RmQRCodeDecoderImageTest.ContentFor(version);
            foreach (var degrees in new[] { 0f, 30f, 90f })
                yield return () => new Image(SingleFinderDecoder.RmQR, $"{version}-M at {degrees}°", () => RmQRDestinationRenders.RenderRotated(content, version, degrees), content, version.ToString());
        }
    }

    /// <summary>
    /// The grids are sampled in the same order whatever the destination, so the first one that reads is the sized call's answer;
    /// one character short, that read does not fit and is the answer too. Its corners are reported with the sized call's read only.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SingleSymbols))]
    public async Task DestinationOneShort_ReportsTheReadTheSizedCallReturns(Image image)
    {
        var (luminance, width, height) = image.Render();
        var sized = new char[image.Decoder.MaxDecodedLength];
        var sizedRead = image.Decoder.Decode(luminance, width, height, sized);
        await Assert.That((sizedRead.Ok, new string(sized, 0, sizedRead.Written), sizedRead.Version, sizedRead.Corners.IsEmpty)).IsEqualTo((true, image.Text, image.Version, false))
            .Because($"premise: a sized call reads the symbol ({sizedRead.Status})");

        var read = image.Decoder.Decode(luminance, width, height, new char[image.Text.Length - 1]);

        await Assert.That(read).IsEqualTo(new SingleFinderRead(false, 0, DecodeStatus.DestinationTooSmall, image.Version, default));
    }

    public static IEnumerable<Func<TimedImage>> SymbolsToTime()
    {
        yield return () => new TimedImage(SingleFinderDecoder.MicroQR, "M4-M at 4 px/module", () => MicroQRDestinationRenders.Render("MICRO QR M4 TEST", MicroQRVersion.M4, MicroQREccLevel.M, pixelsPerModule: 4, degrees: 0f), "MICRO QR M4 TEST", "M4", 2_000, 5);
        // Micro QR renders that read through one grid site each, found by planting a read that does not fit ending nothing there
        yield return () => new TimedImage(SingleFinderDecoder.MicroQR, "M4-L at 2.65 px/module, 90.5°, noisy: the timing frame", () => MicroQRDestinationRenders.RenderTurnedSupersampled(MicroQRVersion.M4, MicroQRText, MicroQREccLevel.L, 2.65, 90.5, noise: 28, seed: 1355), MicroQRText, "M4", 200, 5);
        yield return () => new TimedImage(SingleFinderDecoder.MicroQR, "M3-L nearest at 1.35 px/module: the module boundaries", () => MicroQRDestinationRenders.RenderNearest(MicroQRVersion.M3, "HELLO WORLD", MicroQREccLevel.L, 1.35), "HELLO WORLD", "M3", 500, 5);
        yield return () => new TimedImage(SingleFinderDecoder.MicroQR, "M4-L at 2.5 px/module, 2°: the coverage re-read", () => MicroQRDestinationRenders.RenderTurnedSupersampled(MicroQRVersion.M4, MicroQRText, MicroQREccLevel.L, 2.5, 2.0), MicroQRText, "M4", 200, 5);
        yield return () => new TimedImage(SingleFinderDecoder.RmQR, "R13x99-M at 6 px/module", () => RmQRDestinationRenders.Render("RMQR IMAGE 123", RmQRVersion.R13x99, pixelsPerModule: 6), "RMQR IMAGE 123", "R13x99", 2_000, 5);
    }

    private const string MicroQRText = "MICRO QR M4L TEST 01";

    /// <summary>
    /// The cost. While the read went on through the candidate's other grids and searches, a destination of 2 characters cost
    /// Micro QR about 200 to 270 times a sized call on a process's first calls and about 1,100 times once warm (2026-10-01), and
    /// rMQR 250 to 500 times (its perspective search, then the inverted pass). A Micro QR grid that reads its transpose after a
    /// read that does not fit lets one scale search run, about 40 times on the first calls and 70 warm. A read that does not fit
    /// ends the candidate wherever it is made: on the renders read through the timing frame, the module boundaries and the
    /// coverage re-read, a grid site that let it go on cost about 27, 58 and 56 times. Stopped at the read, the call costs what a
    /// sized one does. The bound is 5 times a sized call and 30 ms; the runs keep it near 10 times once warm, below every one of
    /// those. The inverted image stops at the same read.
    /// </summary>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(SymbolsToTime))]
    public async Task DestinationTooSmall_CostsAboutASizedCall(TimedImage image)
    {
        var (luminance, width, height) = image.Render();
        var decoder = image.Decoder;
        var sized = new char[decoder.MaxDecodedLength];
        var tiny = new char[2];
        var sizedRead = decoder.Decode(luminance, width, height, sized);
        await Assert.That(new string(sized, 0, sizedRead.Written)).IsEqualTo(image.Text);

        await AssertCostsAboutASizedCall(decoder, luminance, width, height, sized, tiny, image.Iterations, image.BoundFactor, image.Version, "a read that does not fit ends its candidate");

        var inverted = new byte[luminance.Length];
        for (var i = 0; i < inverted.Length; i++)
            inverted[i] = (byte)(255 - luminance[i]);
        await Assert.That(decoder.Decode(inverted, width, height, tiny)).IsEqualTo(new SingleFinderRead(false, 0, DecodeStatus.DestinationTooSmall, image.Version, default));
    }

    public static IEnumerable<Func<TwoTextImage>> TwoTextImages()
    {
        // Upright, Micro QR reads in its axis-aligned path; turned, in its arbitrary-orientation one
        yield return () => new TwoTextImage(SingleFinderDecoder.MicroQR, "M4-L upright", () => MicroQRDestinationRenders.RenderTwoTexts(Text, CoverageText, degrees: 0), "M4");
        yield return () => new TwoTextImage(SingleFinderDecoder.MicroQR, "M4-L at 20°", () => MicroQRDestinationRenders.RenderTwoTexts(Text, CoverageText, degrees: 20), "M4");
        yield return () => new TwoTextImage(SingleFinderDecoder.RmQR, "R13x43-M at 6 px/module", () => RmQRDestinationRenders.RenderTwoTexts(RmQRVersion.R13x43, RmQREccLevel.M, Text, CoverageText, 6, 0.25f), "R13x43");
        yield return () => new TwoTextImage(SingleFinderDecoder.RmQR, "R17x99-H at 4 px/module", () => RmQRDestinationRenders.RenderTwoTexts(RmQRVersion.R17x99, RmQREccLevel.H, Text, CoverageText, 4, 0.3f), "R17x99");
    }

    /// <summary>
    /// A grid whose text does not fit the destination is not read again by coverage. On a real image that re-read reads the same
    /// text; on this crafted one it reads another, and a call too short for the text a sized call returns reported the other as a
    /// success.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(TwoTextImages))]
    public async Task DestinationTooSmall_IsNotReadAgainByCoverage(TwoTextImage image)
    {
        var (luminance, width, height, thresholded, readByCoverage) = image.Render();
        await Assert.That((thresholded, readByCoverage)).IsEqualTo((Text, CoverageText))
            .Because("premise: the thresholded grid reads one text and the coverage grid the other");
        var sized = new char[image.Decoder.MaxDecodedLength];
        var sizedRead = image.Decoder.Decode(luminance, width, height, sized);
        await Assert.That(new string(sized, 0, sizedRead.Written)).IsEqualTo(Text)
            .Because("premise: the sized call reads the thresholded grid's text");

        var tiny = new char[CoverageText.Length];
        var read = image.Decoder.Decode(luminance, width, height, tiny);

        await Assert.That(read).IsEqualTo(new SingleFinderRead(false, 0, DecodeStatus.DestinationTooSmall, image.Version, default))
            .Because($"the call too short for the sized call's text reports that read, not {new string(tiny, 0, read.Written)}");
    }

    public static IEnumerable<Func<TimedImage>> SkipImages()
    {
        // Micro QR: the pattern at (61,110) and (49,56); transposed, a mirrored capture read by the transposed grid
        foreach (var mirrored in new[] { false, true })
        {
            yield return () => new TimedImage(SingleFinderDecoder.MicroQR, $"M4-M at 5.1 px/module, 93°{(mirrored ? ", mirrored" : "")}", () => MicroQRDestinationRenders.RenderTurnedSupersampled(MicroQRVersion.M4, "63028458518747710056101171", MicroQREccLevel.M, 5.1, 93.0, mirrored), "63028458518747710056101171", "M4", 100, 5);
            yield return () => new TimedImage(SingleFinderDecoder.MicroQR, $"M4-Q at 3.6 px/module, 133°{(mirrored ? ", mirrored" : "")}", () => MicroQRDestinationRenders.RenderTurnedSupersampled(MicroQRVersion.M4, "98411748017212729364", MicroQREccLevel.Q, 3.6, 133.0, mirrored), "98411748017212729364", "M4", 100, 5);
        }

        // rMQR: one render for each path whose read that does not fit has to keep its corners: a frame's grid (the pattern at
        // (76,34)), the module boundaries, and the coverage re-read
        yield return () => new TimedImage(SingleFinderDecoder.RmQR, "R13x27-M nearest at 4 px/module", () => RmQRDestinationRenders.RenderNearest(RmQRCodeDecoderImageTest.Create("HELLO12345ABCDE", RmQREccLevel.M, RmQRVersion.R13x27), 4f), "HELLO12345ABCDE", "R13x27", 500, 3);
        yield return () => new TimedImage(SingleFinderDecoder.RmQR, "R13x77-M nearest at 1.38 px/module", () => RmQRDestinationRenders.RenderNearest(RmQRCodeDecoderImageTest.Create("VZA$R9CMKV0T7W.C", RmQREccLevel.M, RmQRVersion.R13x77), 1.38f), "VZA$R9CMKV0T7W.C", "R13x77", 500, 3);
        yield return () => new TimedImage(SingleFinderDecoder.RmQR, "R15x139-M bilinear at 1.31 px/module, mirrored", () =>
        {
            var data = RmQRCodeDecoderImageTest.Create("41516104048408666070497882893323", RmQREccLevel.M, RmQRVersion.R15x139);
            return RmQRDestinationRenders.FlipHorizontally(BilinearUpscaleRenderer.Render((row, column) => data[row, column], data.Width, data.Height, 1.3069739f));
        }, "41516104048408666070497882893323", "R15x139", 500, 3);
    }

    /// <summary>
    /// A read that does not fit keeps its corners until the scan has used them, so a later candidate inside the symbol is skipped:
    /// searched in full after the read, it cost Micro QR about 40 to 47 times a sized call once warm and 12 to 38 on a process's
    /// first calls, and rMQR about 55 to 105 times through a frame's grid, about 38 through the module boundaries and about 9
    /// through the coverage re-read (2026-10-01). Skipped, the call costs what a sized one does. Each image's bound (3 or 5 times a
    /// sized call and 30 ms) and runs are sized against its own regression. The premise is asserted (<see cref="SkipPremise"/>),
    /// since a render a little larger or smaller may have none.
    /// </summary>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(SkipImages))]
    public async Task DestinationTooSmall_SkipsACandidateInsideTheSymbolThatRead(TimedImage image)
    {
        var (luminance, width, height) = image.Render();
        var decoder = image.Decoder;
        var sized = new char[decoder.MaxDecodedLength];
        var tiny = new char[2];
        var sizedRead = decoder.Decode(luminance, width, height, sized);
        await Assert.That(new string(sized, 0, sizedRead.Written)).IsEqualTo(image.Text);
        await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, width, height, sizedRead.Corners)).IsTrue()
            .Because("premise: the scan ranks a finder-like pattern inside the symbol after its finder");

        await AssertCostsAboutASizedCall(decoder, luminance, width, height, sized, tiny, image.Iterations, image.BoundFactor, image.Version, "a candidate inside the symbol that read is skipped");
    }

    public static IEnumerable<Func<TimedImage>> TwoSkipImages()
    {
        yield return () => new TimedImage(SingleFinderDecoder.MicroQR, "M4-M at 5.1 px/module, 93°, twice", () =>
        {
            var (single, side, _) = MicroQRDestinationRenders.RenderTurnedSupersampled(MicroQRVersion.M4, "63028458518747710056101171", MicroQREccLevel.M, 5.1, 93.0, mirrored: false);
            return SkipPremise.StackTwice(single, side, side);
        }, "63028458518747710056101171", "M4", 100, 5);
        yield return () => new TimedImage(SingleFinderDecoder.RmQR, "R13x27-M nearest at 4 px/module, twice", () =>
        {
            var (single, width, height) = RmQRDestinationRenders.RenderNearest(RmQRCodeDecoderImageTest.Create("HELLO12345ABCDE", RmQREccLevel.M, RmQRVersion.R13x27), 4f);
            return SkipPremise.StackTwice(single, width, height);
        }, "HELLO12345ABCDE", "R13x27", 500, 3);
    }

    /// <summary>
    /// An image above a copy of itself: neither symbol fits the destination, and each has a finder-like pattern ranked after its
    /// own finder. Every read that does not fit keeps its corners, not only the first, so the pattern inside the second symbol is
    /// skipped as the first one's is. Searched in full, it cost Micro QR about 10 to 25 times a sized call on a process's first
    /// calls and about 40 once warm, and rMQR about 30 to 80 on the first calls and 60 to 70 once warm (2026-10-01).
    /// </summary>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(TwoSkipImages))]
    public async Task DestinationTooSmallForTwoSymbols_SkipsACandidateInsideEach(TimedImage image)
    {
        var (luminance, width, height) = image.Render();
        var decoder = image.Decoder;
        var sized = new char[decoder.MaxDecodedLength];
        var tiny = new char[2];
        var sizedRead = decoder.Decode(luminance, width, height, sized);
        await Assert.That(new string(sized, 0, sizedRead.Written)).IsEqualTo(image.Text);
        foreach (var corners in SkipPremise.BothCopies(sizedRead.Corners, height / 2))
        {
            await Assert.That(SkipPremise.RanksAnotherCandidateInsideAfterTheFinder(luminance, width, height, corners)).IsTrue()
                .Because($"premise: the scan ranks a finder-like pattern inside the symbol at {corners.TopLeft} after its finder");
        }

        await AssertCostsAboutASizedCall(decoder, luminance, width, height, sized, tiny, image.Iterations, image.BoundFactor, image.Version, "a candidate inside either symbol that read is skipped");
    }

    public static IEnumerable<Func<TwoSymbols>> AnotherSymbolFits()
    {
        const string microBig = "MICRO QR M4 TEST";
        foreach (var bigAbove in new[] { true, false })
            yield return () => new TwoSymbols(SingleFinderDecoder.MicroQR, $"M4 {(bigAbove ? "above" : "below")} M2", () => MicroQRDestinationRenders.RenderTwo(microBig, MicroQRVersion.M4, MicroQREccLevel.M, "12345", MicroQRVersion.M2, MicroQREccLevel.L, bigAbove), microBig, "M4", "12345", "M2", 8);
        // Turned, the symbol that fits reads only in the arbitrary-orientation path, after grids whose format information reads have opened the scale and perspective searches
        foreach (var degrees in new[] { 30f, 45f, 60f })
            yield return () => new TwoSymbols(SingleFinderDecoder.MicroQR, $"M4 above M2 at {degrees}°", () => MicroQRDestinationRenders.RenderTwo(microBig, MicroQRVersion.M4, MicroQREccLevel.M, "12345", MicroQRVersion.M2, MicroQREccLevel.L, bigAbove: true, smallDegrees: degrees), microBig, "M4", "12345", "M2", 8);

        const string rmqrBig = "RMQR IMAGE 123 LONGER PAYLOAD";
        foreach (var bigAbove in new[] { true, false })
        {
            yield return () => new TwoSymbols(SingleFinderDecoder.RmQR, $"R13x99 {(bigAbove ? "above" : "below")} R7x43", () => RmQRDestinationRenders.RenderStacked(
                RmQRCodeDecoderImageTest.Create(rmqrBig, RmQREccLevel.M, RmQRVersion.R13x99), RmQRCodeDecoderImageTest.Create("AB1", RmQREccLevel.M, RmQRVersion.R7x43), bigAbove), rmqrBig, "R13x99", "AB1", "R7x43", 8);
        }
        // Turned, the symbol that fits reads only in a frame after the first one whose format copy reads, or only in the angular sweep's frames
        foreach (var degrees in new[] { 90f, 180f, 270f, 30f })
        {
            yield return () => new TwoSymbols(SingleFinderDecoder.RmQR, $"R13x99 above R7x43 at {degrees}°", () => RmQRDestinationRenders.RenderUprightAndTurned(
                RmQRCodeDecoderImageTest.Create(rmqrBig, RmQREccLevel.M, RmQRVersion.R13x99), RmQRCodeDecoderImageTest.Create("AB1", RmQREccLevel.M, RmQRVersion.R7x43), degrees), rmqrBig, "R13x99", "AB1", "R7x43", 8);
        }
    }

    /// <summary>
    /// Two symbols, the one tried first too long for the destination: the read that does not fit ends that candidate only, and the
    /// other symbol's text is returned.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AnotherSymbolFits))]
    public async Task DestinationTooSmallForOneSymbol_StillReadsAnotherThatFits(TwoSymbols image)
    {
        var (luminance, width, height) = image.Render();
        await AssertTheBigSymbolIsTriedFirst(image, luminance, width, height);
        var destination = new char[image.DestinationLength];

        var read = image.Decoder.Decode(luminance, width, height, destination);

        await Assert.That((read.Ok, new string(destination, 0, read.Written), read.Version, read.Corners.IsEmpty)).IsEqualTo((true, image.OtherText, image.OtherVersion, false))
            .Because($"status={read.Status}");
    }

    public static IEnumerable<Func<TwoSymbols>> NeitherSymbolFits()
    {
        const string microBig = "MICRO QR M4 TEST";
        yield return () => new TwoSymbols(SingleFinderDecoder.MicroQR, "M4 below M3", () => MicroQRDestinationRenders.RenderTwo(microBig, MicroQRVersion.M4, MicroQREccLevel.M, "HELLO WORLD", MicroQRVersion.M3, MicroQREccLevel.L, bigAbove: false), microBig, "M4", "HELLO WORLD", "M3", 8);

        const string rmqrBig = "RMQR IMAGE 123 LONGER PAYLOAD";
        foreach (var degrees in new[] { 90f, 30f })
        {
            yield return () => new TwoSymbols(SingleFinderDecoder.RmQR, $"R13x99 above R11x59 at {degrees}°", () => RmQRDestinationRenders.RenderUprightAndTurned(
                RmQRCodeDecoderImageTest.Create(rmqrBig, RmQREccLevel.M, RmQRVersion.R13x99), RmQRCodeDecoderImageTest.Create("RMQR IMAGE 123", RmQREccLevel.M, RmQRVersion.R11x59), degrees), rmqrBig, "R13x99", "RMQR IMAGE 123", "R11x59", 8);
        }
    }

    /// <summary>Neither symbol fits: the read that did not fit is reported, the first candidate's on the tie, and no text.</summary>
    [Test]
    [MethodDataSource(nameof(NeitherSymbolFits))]
    public async Task DestinationTooSmallForBothSymbols_ReportsTheFirstCandidates(TwoSymbols image)
    {
        var (luminance, width, height) = image.Render();
        await AssertTheBigSymbolIsTriedFirst(image, luminance, width, height);

        var read = image.Decoder.Decode(luminance, width, height, new char[image.DestinationLength]);

        await Assert.That(read).IsEqualTo(new SingleFinderRead(false, 0, DecodeStatus.DestinationTooSmall, image.BigVersion, default));
    }

    /// <summary>Premise: a sized call returns the big symbol, so its candidate is the first one tried.</summary>
    private static async Task AssertTheBigSymbolIsTriedFirst(TwoSymbols image, byte[] luminance, int width, int height)
    {
        var sized = new char[image.Decoder.MaxDecodedLength];
        var read = image.Decoder.Decode(luminance, width, height, sized);
        await Assert.That((new string(sized, 0, read.Written), read.Version)).IsEqualTo((image.BigText, image.BigVersion))
            .Because("premise: the big symbol's candidate is tried first");
    }

    /// <summary>A destination of 2 characters, timed over <paramref name="iterations"/> runs against a sized one, and what it reports.</summary>
    private static async Task AssertCostsAboutASizedCall(SingleFinderDecoder decoder, byte[] luminance, int width, int height, char[] sized, char[] tiny, int iterations, int boundFactor, string version, string because)
    {
        decoder.Decode(luminance, width, height, tiny);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            decoder.Decode(luminance, width, height, sized);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var read = default(SingleFinderRead);
        for (var i = 0; i < iterations; i++)
            read = decoder.Decode(luminance, width, height, tiny);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That(read).IsEqualTo(new SingleFinderRead(false, 0, DecodeStatus.DestinationTooSmall, version, default));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * boundFactor) + TimeSpan.FromMilliseconds(30))
            .Because($"{because}: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
    }
}
