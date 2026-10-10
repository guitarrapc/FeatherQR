using TUnit.Assertions.Enums;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The image decode passes all three image decoders run (<see cref="ImageDecodePasses"/>): which pass runs after which result, which result is reported, and what each pass is given to read.
/// The symbol pass is a recording stand-in, so what is asserted is the passes' own decisions, rule by rule as the architecture record states them (qrcode-symbologies.md, image decode passes).
/// </summary>
public class ImageDecodePassesTest
{
    /// <summary>What the stand-in returns, per pass in call order, and what it was called with.</summary>
    private sealed class Script
    {
        public DecodeStatus[] Global = [];
        public bool[] NoFinder = [];
        public DecodeStatus[] Regional = [];
        public DecodeStatus[] Midpoint = [];
        public List<(string Pass, byte[] Image, int Threshold)> Calls { get; } = [];

        /// <summary>The length of the buffer each global pass was given.</summary>
        public List<int> Lengths { get; } = [];

        /// <summary>The dimensions each pass was given, in call order.</summary>
        public List<(string Pass, int Width, int Height)> Shapes { get; } = [];

        public int Count(string pass) => Calls.Count(call => call.Pass == pass);
    }

    /// <summary>What the stand-in writes on every call; the passes zero it unless the result is a read.</summary>
    private const int Written = 7;

    /// <summary>
    /// The stand-in: its info names the attempt the result came from, 1 and 2 for the global positive and negative, 3 and 4 for the regional, 5 and 6 for the midpoint; -1 is not detected.
    /// Its global pass takes the threshold and levels from the histogram, as the decoders do.
    /// </summary>
    private readonly struct RecordingPass(Script script, bool midpointPass, bool reducedScale) : ISymbolPass<int>
    {
        public bool HasMidpointPass => midpointPass;

        public bool HasReducedScaleSearch => reducedScale;

        /// <summary>A thousand times the scale on top of the attempt, so a test sees both.</summary>
        public int AtFullScale(in int info, int scale) => info + 1000 * scale;

        public int NotDetected => -1;

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out int info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out grey);
            var index = script.Count("global");
            script.Lengths.Add(luminance.Length);
            script.Shapes.Add(("global", width, height));
            script.Calls.Add(("global", luminance.Slice(0, width * height).ToArray(), threshold));
            noFinder = index < script.NoFinder.Length && script.NoFinder[index];
            charsWritten = Written;
            info = 1 + index;
            return index < script.Global.Length ? script.Global[index] : DecodeStatus.NotDetected;
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out int info)
        {
            var index = script.Count("regional");
            script.Shapes.Add(("regional", width, height));
            script.Calls.Add(("regional", luminance.Slice(0, width * height).ToArray(), -1));
            charsWritten = Written;
            info = 3 + index;
            return index < script.Regional.Length ? script.Regional[index] : DecodeStatus.NotDetected;
        }

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out int info)
        {
            var index = script.Count("midpoint");
            script.Shapes.Add(("midpoint", width, height));
            script.Calls.Add(("midpoint", luminance.Slice(0, width * height).ToArray(), threshold));
            charsWritten = Written;
            info = 5 + index;
            return index < script.Midpoint.Length ? script.Midpoint[index] : DecodeStatus.NotDetected;
        }
    }

    private static DecodeStatus Run(byte[] luminance, int width, int height, Script script, bool midpointPass, out int charsWritten, out int info, bool reducedScale = false)
    {
        var pass = new RecordingPass(script, midpointPass, reducedScale);
        return ImageDecodePasses.Decode<RecordingPass, int>(ref pass, luminance, width, height, new char[16], out charsWritten, out info);
    }

    /// <summary>
    /// A symbol under a shadow, light on dark: grey levels in both polarities, with each polarity's midpoint off its threshold, and a regional binarization of each that moves pixels, so every pass can run.
    /// </summary>
    private static (byte[] Luminance, int Side) Shadowed()
    {
        var qr = QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, _) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, UnevenLight.Shadow, 0f, 0.55f);
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(255 - luminance[i]);
        return (luminance, width);
    }

    private static byte[] Negative(byte[] luminance)
    {
        var negative = new byte[luminance.Length];
        for (var i = 0; i < luminance.Length; i++)
            negative[i] = (byte)(255 - luminance[i]);
        return negative;
    }

    /// <summary>The threshold and the rounded midpoint of each polarity, as the passes compute them.</summary>
    private static (byte Threshold, byte Midpoint, bool Enabled) Levels(byte[] image)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(image, histogram);
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
        return (threshold, grey.IsEnabled ? (byte)Math.Round(grey.Midpoint) : (byte)0, grey.IsEnabled);
    }

    private static string[] Passes(Script script) => script.Calls.Select(call => call.Pass).ToArray();

    [Test]
    public async Task Shadowed_ReachesEveryPass()
    {
        // Premise of the tests below: both polarities have grey levels with the midpoint off the threshold
        var (luminance, side) = Shadowed();
        foreach (var image in new[] { luminance, Negative(luminance) })
        {
            var (threshold, midpoint, enabled) = Levels(image);
            await Assert.That(enabled).IsTrue();
            await Assert.That(midpoint).IsNotEqualTo(threshold);
        }

        var script = new Script { NoFinder = [true, true] };
        Run(luminance, side, side, script, midpointPass: true, out _, out _);

        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global", "regional", "regional", "midpoint", "midpoint"], CollectionOrdering.Matching);
    }

    #region The global passes

    [Test]
    [Arguments(DecodeStatus.Success, Written)]
    [Arguments(DecodeStatus.DestinationTooSmall, 0)]
    public async Task PositiveTerminal_EndsTheSequence(DecodeStatus result, int expectedWritten)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [result] };

        var status = Run(luminance, side, side, script, midpointPass: true, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info).IsEqualTo(1);
        await Assert.That(charsWritten).IsEqualTo(expectedWritten);
        await Assert.That(Passes(script)).IsEquivalentTo(["global"], CollectionOrdering.Matching);
    }

    /// <summary>The negative pass reads the image inverted, with the threshold of the mirrored histogram.</summary>
    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    public async Task PositiveFails_NegativeTerminalIsReported(DecodeStatus result)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [DecodeStatus.DataUncorrectable, result] };

        var status = Run(luminance, side, side, script, midpointPass: true, out _, out var info);

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info).IsEqualTo(2);
        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global"], CollectionOrdering.Matching);
        await Assert.That(script.Calls[0].Image).IsEquivalentTo(luminance, CollectionOrdering.Matching);
        await Assert.That(script.Calls[1].Image).IsEquivalentTo(Negative(luminance), CollectionOrdering.Matching);
        await Assert.That(script.Calls[1].Threshold).IsEqualTo((int)Levels(Negative(luminance)).Threshold);
    }

    /// <summary>A verdict from the positive pass does not end the sequence before the negative pass, and a read there wins.</summary>
    [Test]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    public async Task PositiveVerdict_NegativeStillRuns_AndItsReadWins(DecodeStatus verdict)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [verdict, DecodeStatus.Success] };

        var status = Run(luminance, side, side, script, midpointPass: true, out _, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info).IsEqualTo(2);
    }

    /// <summary>A verdict from either global pass ends the sequence there: no regional or midpoint pass, the positive's verdict before the negative's.</summary>
    [Test]
    [Arguments(DecodeStatus.UnmappedCharacter, DecodeStatus.DataUncorrectable, 1)]
    [Arguments(DecodeStatus.UnsupportedContent, DecodeStatus.UnmappedCharacter, 1)]
    [Arguments(DecodeStatus.NotDetected, DecodeStatus.UnmappedCharacter, 2)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.UnsupportedContent, 2)]
    public async Task GlobalVerdict_EndsTheSequence(DecodeStatus positive, DecodeStatus negative, int expectedInfo)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [positive, negative], NoFinder = [true, true] };

        var status = Run(luminance, side, side, script, midpointPass: true, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(expectedInfo == 1 ? positive : negative);
        await Assert.That(info).IsEqualTo(expectedInfo);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global"], CollectionOrdering.Matching);
    }

    #endregion

    #region The regional pass

    /// <summary>A settled regional pass is reported, and the midpoint pass does not run although neither global pass found a finder.</summary>
    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    public async Task BothGlobalFail_RegionalSettledIsReported(DecodeStatus result)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [DecodeStatus.NotDetected, DecodeStatus.FormatInformationInvalid], NoFinder = [true, true], Regional = [result] };

        var status = Run(luminance, side, side, script, midpointPass: true, out _, out var info);

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info).IsEqualTo(3);
        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global", "regional"], CollectionOrdering.Matching);
    }

    /// <summary>The negative's regional read, after the positive's regional pass did not settle.</summary>
    [Test]
    public async Task RegionalNegativeRead_IsReported()
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Regional = [DecodeStatus.DataUncorrectable, DecodeStatus.Success] };

        var status = Run(luminance, side, side, script, midpointPass: true, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info).IsEqualTo(4);
        await Assert.That(charsWritten).IsEqualTo(Written);
    }

    #endregion

    #region The midpoint pass

    /// <summary>A decoder without the midpoint pass, Standard QR, reports its global positive pass when the regional pass fails, finder or not.</summary>
    [Test]
    public async Task EverythingFails_WithoutMidpointPass_ReportsTheGlobalPositive()
    {
        var (luminance, side) = Shadowed();
        var script = new Script { Global = [DecodeStatus.FormatInformationInvalid, DecodeStatus.NotDetected], NoFinder = [true, true] };

        var status = Run(luminance, side, side, script, midpointPass: false, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.FormatInformationInvalid);
        await Assert.That(info).IsEqualTo(1);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global", "regional", "regional"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Each polarity whose global pass found no finder is swept at its own midpoint, the positive first, the negative from its own pixels although the regional pass wrote into the buffer that held them.
    /// When nothing settles, the global positive pass is reported.
    /// </summary>
    [Test]
    public async Task NoFinder_SweepsEachPolarityAtItsMidpoint()
    {
        var (luminance, side) = Shadowed();
        var negative = Negative(luminance);
        var script = new Script { Global = [DecodeStatus.NotDetected, DecodeStatus.NotDetected], NoFinder = [true, true] };

        var status = Run(luminance, side, side, script, midpointPass: true, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(info).IsEqualTo(1);
        await Assert.That(charsWritten).IsEqualTo(0);
        var midpoints = script.Calls.Where(call => call.Pass == "midpoint").ToArray();
        await Assert.That(midpoints.Length).IsEqualTo(2);
        await Assert.That(midpoints[0].Image).IsEquivalentTo(luminance, CollectionOrdering.Matching);
        await Assert.That(midpoints[0].Threshold).IsEqualTo((int)Levels(luminance).Midpoint);
        await Assert.That(midpoints[1].Image).IsEquivalentTo(negative, CollectionOrdering.Matching);
        await Assert.That(midpoints[1].Threshold).IsEqualTo((int)Levels(negative).Midpoint);
    }

    /// <summary>A polarity whose global pass found a finder candidate is not swept again at its midpoint.</summary>
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(false, false)]
    public async Task FinderFound_ThatPolarityIsNotSweptAtItsMidpoint(bool positiveNoFinder, bool negativeNoFinder)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { NoFinder = [positiveNoFinder, negativeNoFinder] };
        var expected = new List<byte[]>();
        if (positiveNoFinder)
            expected.Add(luminance);
        if (negativeNoFinder)
            expected.Add(Negative(luminance));

        Run(luminance, side, side, script, midpointPass: true, out _, out _);

        var midpoints = script.Calls.Where(call => call.Pass == "midpoint").ToArray();
        await Assert.That(midpoints.Length).IsEqualTo(expected.Count);
        for (var i = 0; i < expected.Count; i++)
            await Assert.That(midpoints[i].Image).IsEquivalentTo(expected[i], CollectionOrdering.Matching);
    }

    /// <summary>A settled midpoint sweep is reported: the positive's ends the sequence before the negative's.</summary>
    [Test]
    [Arguments(DecodeStatus.Success, DecodeStatus.Success, DecodeStatus.Success, 5)]
    [Arguments(DecodeStatus.UnmappedCharacter, DecodeStatus.Success, DecodeStatus.UnmappedCharacter, 5)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.Success, DecodeStatus.Success, 6)]
    [Arguments(DecodeStatus.NotDetected, DecodeStatus.DestinationTooSmall, DecodeStatus.DestinationTooSmall, 6)]
    public async Task MidpointSettled_IsReported(DecodeStatus positive, DecodeStatus negative, DecodeStatus expected, int expectedInfo)
    {
        var (luminance, side) = Shadowed();
        var script = new Script { NoFinder = [true, true], Midpoint = [positive, negative] };

        var status = Run(luminance, side, side, script, midpointPass: true, out _, out var info);

        await Assert.That(status).IsEqualTo(expected);
        await Assert.That(info).IsEqualTo(expectedInfo);
        await Assert.That(script.Count("midpoint")).IsEqualTo(expectedInfo - 4);
    }

    /// <summary>An image of two levels has no grey levels for a midpoint: no midpoint pass, and no regional pass either.</summary>
    [Test]
    public async Task TwoLevels_NoMidpointPass()
    {
        const int side = 96;
        var random = new Random(96);
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = random.Next(2) == 0 ? (byte)0 : (byte)255;
        await Assert.That(Levels(luminance).Enabled).IsFalse();
        var script = new Script { NoFinder = [true, true] };

        Run(luminance, side, side, script, midpointPass: true, out _, out _);

        await Assert.That(Passes(script)).IsEquivalentTo(["global", "global"], CollectionOrdering.Matching);
    }

    /// <summary>A midpoint that rounds to the global threshold would sweep what the global pass swept: that polarity is not swept again.</summary>
    [Test]
    public async Task MidpointOnTheThreshold_NotSweptAgain()
    {
        // Levels 118 and 160 either side of a ramp 38 px wide: the positive's midpoint rounds to its threshold (139), the negative's does not (116 against 117)
        const int side = 64;
        const int dark = 118, light = 160, ramp = 38, start = (side - ramp) / 2;
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
        {
            var x = i % side;
            luminance[i] = x < start ? (byte)dark : x >= start + ramp ? (byte)light : (byte)(dark + (light - dark) * (x - start + 1) / (ramp + 1));
        }
        var positive = Levels(luminance);
        var negative = Levels(Negative(luminance));
        await Assert.That(positive.Enabled && positive.Midpoint == positive.Threshold).IsTrue().Because("a premise: the positive's midpoint is its threshold");
        await Assert.That(negative.Enabled && negative.Midpoint != negative.Threshold).IsTrue().Because("a premise: the negative's is not");
        var script = new Script { NoFinder = [true, true] };

        Run(luminance, side, side, script, midpointPass: true, out _, out _);

        var midpoints = script.Calls.Where(call => call.Pass == "midpoint").ToArray();
        await Assert.That(midpoints.Length).IsEqualTo(1);
        await Assert.That(midpoints[0].Image).IsEquivalentTo(Negative(luminance), CollectionOrdering.Matching);
    }

    #endregion

    #region The image

    /// <summary>A buffer smaller than its dimensions, or dimensions whose product overflows, is not detected, with the decoder's own diagnostics, and nothing runs.</summary>
    [Test]
    [Arguments(16, 16, 255)]
    [Arguments(0, 16, 0)]
    [Arguments(int.MaxValue, 2, 16)]
    public async Task ImageSmallerThanItsDimensions_IsNotDetected(int width, int height, int length)
    {
        var script = new Script { Global = [DecodeStatus.Success] };

        var status = Run(new byte[length], width, height, script, midpointPass: true, out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(info).IsEqualTo(-1);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(script.Calls.Count).IsEqualTo(0);
    }

    /// <summary>A buffer longer than its dimensions is read, and its histogram counted, to its dimensions only: as many dark pixels again past them would move both thresholds.</summary>
    [Test]
    public async Task ImageLongerThanItsDimensions_ReadsItsDimensionsOnly()
    {
        var (luminance, side) = Shadowed();
        var longer = luminance.Concat(new byte[luminance.Length]).ToArray();
        await Assert.That(Levels(longer).Threshold).IsNotEqualTo(Levels(luminance).Threshold).Because("a premise: the tail moves the threshold");
        var script = new Script { Global = [DecodeStatus.NotDetected, DecodeStatus.Success] };

        Run(longer, side, side, script, midpointPass: true, out _, out _);

        await Assert.That(script.Lengths).IsEquivalentTo([side * side, side * side], CollectionOrdering.Matching);
        await Assert.That(script.Calls[0].Threshold).IsEqualTo((int)Levels(luminance).Threshold);
        await Assert.That(script.Calls[1].Threshold).IsEqualTo((int)Levels(Negative(luminance)).Threshold);
    }

    #endregion

    #region The reduced-scale search

    /// <summary>Seeded grey levels and no symbol: an image every pass that can run is given.</summary>
    private static byte[] Texture(int width, int height)
    {
        var luminance = new byte[width * height];
        new Random(20261010).NextBytes(luminance);
        return luminance;
    }

    /// <summary>Each pixel the rounded mean of the two by two block above it, an odd last column or row dropped.</summary>
    private static byte[] Halved(byte[] luminance, int width, int height)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        var halved = new byte[halfWidth * halfHeight];
        for (var y = 0; y < halfHeight; y++)
        {
            for (var x = 0; x < halfWidth; x++)
            {
                var sum = luminance[2 * y * width + 2 * x] + luminance[2 * y * width + 2 * x + 1]
                    + luminance[(2 * y + 1) * width + 2 * x] + luminance[(2 * y + 1) * width + 2 * x + 1];
                halved[y * halfWidth + x] = (byte)((sum + 2) / 4);
            }
        }
        return halved;
    }

    private static (int Width, int Height)[] GlobalShapes(Script script)
        => [.. script.Shapes.Where(static shape => shape.Pass == "global").Select(static shape => (shape.Width, shape.Height))];

    /// <summary>
    /// When nothing settles at full size, the same passes read the image halved, and halved again while its shorter side stays at least 64 pixels.
    /// When nothing settles there either, the full-size positive pass is reported, as without the search.
    /// </summary>
    [Test]
    [Arguments(400, 300, new[] { 200, 150, 100, 75 })]
    [Arguments(401, 301, new[] { 200, 150, 100, 75 })]
    [Arguments(1024, 128, new[] { 512, 64 })]
    public async Task NothingReads_SearchesEachHalfDownToTheFloor(int width, int height, int[] levels)
    {
        var script = new Script { Global = [DecodeStatus.DataUncorrectable] };

        var status = Run(Texture(width, height), width, height, script, midpointPass: false, out var charsWritten, out var info, reducedScale: true);

        var expected = new List<(int, int)> { (width, height), (width, height) };
        for (var i = 0; i < levels.Length; i += 2)
        {
            expected.Add((levels[i], levels[i + 1]));
            expected.Add((levels[i], levels[i + 1]));
        }
        await Assert.That(GlobalShapes(script)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(info).IsEqualTo(1);
        await Assert.That(charsWritten).IsEqualTo(0);
    }

    /// <summary>A level is the level above it halved, the first from the image itself, which is not written.</summary>
    [Test]
    public async Task EachLevel_IsTheLevelAboveHalved()
    {
        const int Width = 301, Height = 260;
        var luminance = Texture(Width, Height);
        var script = new Script();

        Run(luminance, Width, Height, script, midpointPass: false, out _, out _, reducedScale: true);

        var globals = script.Calls.Where(static call => call.Pass == "global").ToArray();
        var half = Halved(luminance, Width, Height);
        var quarter = Halved(half, Width / 2, Height / 2);
        await Assert.That(GlobalShapes(script)).IsEquivalentTo([(301, 260), (301, 260), (150, 130), (150, 130), (75, 65), (75, 65)], CollectionOrdering.Matching);
        await Assert.That(globals[2].Image.AsSpan().SequenceEqual(half)).IsTrue();
        await Assert.That(globals[3].Image.AsSpan().SequenceEqual(Negative(half))).IsTrue();
        await Assert.That(globals[4].Image.AsSpan().SequenceEqual(quarter)).IsTrue();
        await Assert.That(globals[5].Image.AsSpan().SequenceEqual(Negative(quarter))).IsTrue();
        await Assert.That(luminance.AsSpan().SequenceEqual(Texture(Width, Height))).IsTrue();
    }

    /// <summary>
    /// A level goes through every pass a full-size image does: the calls on it are the calls its image gets on its own, pass for pass, image for image and threshold for threshold.
    /// </summary>
    [Test]
    public async Task EachLevel_RunsThePassesItsImageGetsOnItsOwn()
    {
        // The shadowed symbol at twice the scale, so its half is an image every pass runs on
        var qr = QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, side, _) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 8, UnevenLight.Shadow, 0f, 0.55f);
        luminance = Negative(luminance);
        var half = Halved(luminance, side, side);
        var alone = new Script { NoFinder = [true, true] };
        Run(half, side / 2, side / 2, alone, midpointPass: true, out _, out _);
        await Assert.That(Passes(alone)).IsEquivalentTo(["global", "global", "regional", "regional", "midpoint", "midpoint"], CollectionOrdering.Matching).Because("a premise: every pass runs on the half");

        var script = new Script { NoFinder = [true, true, true, true] };
        Run(luminance, side, side, script, midpointPass: true, out _, out _, reducedScale: true);

        var fullSize = script.Shapes.Count(shape => shape.Width == side);
        var level = script.Calls.Skip(fullSize).Take(alone.Calls.Count).ToArray();
        await Assert.That(level.Length).IsEqualTo(alone.Calls.Count);
        for (var i = 0; i < level.Length; i++)
        {
            await Assert.That(level[i].Pass).IsEqualTo(alone.Calls[i].Pass);
            await Assert.That(level[i].Threshold).IsEqualTo(alone.Calls[i].Threshold);
            await Assert.That(level[i].Image.AsSpan().SequenceEqual(alone.Calls[i].Image)).IsTrue().Because($"call {i}, {level[i].Pass}");
        }
    }

    /// <summary>A read at a reduced level ends the search, and its diagnostics go through the mapping to full scale with the scale of the level.</summary>
    [Test]
    [Arguments(2, 2)]
    [Arguments(3, 2)]
    [Arguments(4, 4)]
    [Arguments(6, 8)]
    public async Task ReducedRead_EndsTheSearch_AndIsReportedAtFullScale(int failedGlobalPasses, int scale)
    {
        DecodeStatus[] global = [.. Enumerable.Repeat(DecodeStatus.NotDetected, failedGlobalPasses), DecodeStatus.Success];
        var script = new Script { Global = global };

        var status = Run(Texture(800, 600), 800, 600, script, midpointPass: false, out var charsWritten, out var info, reducedScale: true);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info).IsEqualTo(failedGlobalPasses + 1 + 1000 * scale);
        await Assert.That(charsWritten).IsEqualTo(Written);
        await Assert.That(script.Count("global")).IsEqualTo(failedGlobalPasses + 1);
        await Assert.That(script.Calls[^1].Pass).IsEqualTo("global");
    }

    /// <summary>A reduced level that settles without a read, too long for the destination or a verdict on the content, is reported the same way and ends the search.</summary>
    [Test]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    public async Task ReducedSettledWithoutARead_IsReported_AndEndsTheSearch(DecodeStatus result)
    {
        var script = new Script { Global = [DecodeStatus.NotDetected, DecodeStatus.NotDetected, DecodeStatus.NotDetected, result] };

        var status = Run(Texture(400, 300), 400, 300, script, midpointPass: false, out var charsWritten, out var info, reducedScale: true);

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info).IsEqualTo(4 + 2000);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(GlobalShapes(script)).IsEquivalentTo([(400, 300), (400, 300), (200, 150), (200, 150)], CollectionOrdering.Matching);
    }

    /// <summary>Whatever settles at full size is final: nothing is read reduced after a read, a read too long for the destination, or a verdict, from a global pass or the regional one.</summary>
    [Test]
    [Arguments(DecodeStatus.Success, false)]
    [Arguments(DecodeStatus.DestinationTooSmall, false)]
    [Arguments(DecodeStatus.UnsupportedContent, false)]
    [Arguments(DecodeStatus.UnmappedCharacter, false)]
    [Arguments(DecodeStatus.Success, true)]
    [Arguments(DecodeStatus.UnsupportedContent, true)]
    public async Task FullSizeSettled_NothingIsReadReduced(DecodeStatus result, bool regional)
    {
        var (luminance, side) = Shadowed();
        await Assert.That(side / 2).IsGreaterThanOrEqualTo(64).Because("a premise: the image is large enough to be searched reduced");
        var script = regional
            ? new Script { Regional = [DecodeStatus.NotDetected, result] }
            : new Script { Global = [DecodeStatus.NotDetected, result] };

        var status = Run(luminance, side, side, script, midpointPass: false, out _, out var info, reducedScale: true);

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info).IsEqualTo(regional ? 4 : 2);
        await Assert.That(script.Shapes.All(shape => shape.Width == side && shape.Height == side)).IsTrue();
    }

    /// <summary>A decoder without the search reads at full size only.</summary>
    [Test]
    public async Task DecoderWithoutTheSearch_ReadsAtFullSizeOnly()
    {
        var script = new Script();

        Run(Texture(400, 300), 400, 300, script, midpointPass: false, out _, out var info);

        await Assert.That(GlobalShapes(script)).IsEquivalentTo([(400, 300), (400, 300)], CollectionOrdering.Matching);
        await Assert.That(info).IsEqualTo(1);
    }

    /// <summary>An image whose shorter side would be under 64 pixels halved is not searched reduced, and the levels stop where the next would be.</summary>
    [Test]
    [Arguments(127, 400, 0)]
    [Arguments(400, 127, 0)]
    [Arguments(128, 400, 1)]
    [Arguments(255, 255, 1)]
    [Arguments(256, 256, 2)]
    public async Task ShorterSideUnderTheFloorWhenHalved_IsNotSearched(int width, int height, int levels)
    {
        var script = new Script();

        Run(Texture(width, height), width, height, script, midpointPass: false, out _, out _, reducedScale: true);

        await Assert.That(script.Count("global")).IsEqualTo(2 * (1 + levels));
    }

    /// <summary>
    /// An image of only black and white is not searched reduced, as it is not binarized region by region.
    /// One grey pixel makes it an image like any other.
    /// </summary>
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 2)]
    public async Task OnlyBlackAndWhite_IsNotSearchedReduced(bool oneGreyPixel, int levels)
    {
        var luminance = Texture(400, 300);
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = luminance[i] < 128 ? (byte)0 : (byte)255;
        if (oneGreyPixel)
            luminance[^1] = 128;
        var script = new Script();

        Run(luminance, 400, 300, script, midpointPass: false, out _, out _, reducedScale: true);

        await Assert.That(script.Count("global")).IsEqualTo(2 * (1 + levels));
    }

    #endregion
}
