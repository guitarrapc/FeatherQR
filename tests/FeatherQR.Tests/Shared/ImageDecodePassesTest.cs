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

        public int Count(string pass) => Calls.Count(call => call.Pass == pass);
    }

    /// <summary>What the stand-in writes on every call; the passes zero it unless the result is a read.</summary>
    private const int Written = 7;

    /// <summary>
    /// The stand-in: its info names the attempt the result came from, 1 and 2 for the global positive and negative, 3 and 4 for the regional, 5 and 6 for the midpoint; -1 is not detected.
    /// Its global pass takes the threshold and levels from the histogram, as the decoders do.
    /// </summary>
    private readonly struct RecordingPass(Script script, bool midpointPass) : ISymbolPass<int>
    {
        public bool HasMidpointPass => midpointPass;

        public int NotDetected => -1;

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out int info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out grey);
            var index = script.Count("global");
            script.Lengths.Add(luminance.Length);
            script.Calls.Add(("global", luminance.Slice(0, width * height).ToArray(), threshold));
            noFinder = index < script.NoFinder.Length && script.NoFinder[index];
            charsWritten = Written;
            info = 1 + index;
            return index < script.Global.Length ? script.Global[index] : DecodeStatus.NotDetected;
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out int info)
        {
            var index = script.Count("regional");
            script.Calls.Add(("regional", luminance.Slice(0, width * height).ToArray(), -1));
            charsWritten = Written;
            info = 3 + index;
            return index < script.Regional.Length ? script.Regional[index] : DecodeStatus.NotDetected;
        }

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out int info)
        {
            var index = script.Count("midpoint");
            script.Calls.Add(("midpoint", luminance.Slice(0, width * height).ToArray(), threshold));
            charsWritten = Written;
            info = 5 + index;
            return index < script.Midpoint.Length ? script.Midpoint[index] : DecodeStatus.NotDetected;
        }
    }

    private static DecodeStatus Run(byte[] luminance, int width, int height, Script script, bool midpointPass, out int charsWritten, out int info)
    {
        var pass = new RecordingPass(script, midpointPass);
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
}
