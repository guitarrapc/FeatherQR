using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The regional retry every image decoder runs after both global polarities fail: which polarities it decodes, from which pixels, and when it decodes nothing.
/// The decode itself is a recording stand-in, so what is asserted is the retry's own decisions.
/// </summary>
public class RegionalRetryTest
{
    private sealed class Calls
    {
        public List<byte[]> Inputs { get; } = [];
        public List<int[]> Histograms { get; } = [];
    }

    private struct RecordingAttempt : ILuminanceAttempt<int>
    {
        public Calls Calls;
        public DecodeStatus[] Results;

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out int info)
        {
            var index = Calls.Inputs.Count;
            Calls.Inputs.Add(luminance.Slice(0, width * height).ToArray());
            Calls.Histograms.Add(histogram.Slice(0, Binarizer.HistogramBins).ToArray());
            charsWritten = 0;
            info = index + 1;
            return index < Results.Length ? Results[index] : DecodeStatus.NotDetected;
        }
    }

    /// <summary>The state the decoders call it in: the negative made, and the histogram mirrored for it.</summary>
    private static (byte[] Negative, int[] Histogram) AfterInvertedRetry(byte[] luminance)
    {
        var negative = new byte[luminance.Length];
        for (var i = 0; i < luminance.Length; i++)
            negative[i] = (byte)(255 - luminance[i]);
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        Binarizer.InvertHistogram(histogram);
        return (negative, histogram);
    }

    private static DecodeStatus Run(byte[] luminance, int width, int height, Calls calls, params DecodeStatus[] results)
        => Run(luminance, width, height, calls, out _, results);

    /// <param name="info">The decode the result came from: 1 for the positive, 2 for the negative.</param>
    private static DecodeStatus Run(byte[] luminance, int width, int height, Calls calls, out int info, params DecodeStatus[] results)
    {
        var (negative, histogram) = AfterInvertedRetry(luminance);
        var attempt = new RecordingAttempt { Calls = calls, Results = results };
        return RegionalRetry.Decode<RecordingAttempt, int>(ref attempt, luminance, negative, histogram, width, height, new char[16], out _, out info);
    }

    private static byte[] Lit(bool negate)
    {
        var qr = QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, _, _) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, UnevenLight.Shadow, 0f, 0.55f);
        if (negate)
        {
            for (var i = 0; i < luminance.Length; i++)
                luminance[i] = (byte)(255 - luminance[i]);
        }
        return luminance;
    }

    private static int LitSide => QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 }).Size * 4;

    private static byte[] Expected(byte[] source, int width, int height)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(source, out _);
        var binarized = new byte[source.Length];
        LocalBinarizer.TryBinarize(source, width, height, negative: false, threshold, binarized, new int[LocalBinarizer.ScratchLength(width, height)], out _);
        return binarized;
    }

    [Test]
    public async Task Decode_UnevenLight_DecodesThePositiveRegionalBinarizationFirst()
    {
        var side = LitSide;
        var luminance = Lit(negate: false);
        var calls = new Calls();

        var status = Run(luminance, side, side, calls, DecodeStatus.Success);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(calls.Inputs.Count).IsEqualTo(1);
        await Assert.That(calls.Inputs[0]).IsEquivalentTo(Expected(luminance, side, side));
        await AssertHistogramIsTheInputs(calls, 0);
    }

    private static async Task AssertHistogramIsTheInputs(Calls calls, int index)
    {
        var expected = new int[Binarizer.HistogramBins];
        foreach (var value in calls.Inputs[index])
            expected[value]++;
        await Assert.That(calls.Histograms[index]).IsEquivalentTo(expected);
    }

    /// <summary>The negative is binarized from its own pixels, not the positive's output inverted.</summary>
    [Test]
    public async Task Decode_PositiveFails_DecodesTheNegativesOwnRegionalBinarization()
    {
        var side = LitSide;
        var luminance = Lit(negate: true);
        var (negative, _) = AfterInvertedRetry(luminance);
        var calls = new Calls();

        var status = Run(luminance, side, side, calls, DecodeStatus.DataUncorrectable, DecodeStatus.Success);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(calls.Inputs.Count).IsEqualTo(2);
        await Assert.That(calls.Inputs[1]).IsEquivalentTo(Expected(negative, side, side));
        await AssertHistogramIsTheInputs(calls, 1);
    }

    [Test]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    [Arguments(DecodeStatus.DataUncorrectable)]
    [Arguments(DecodeStatus.NotDetected)]
    public async Task Decode_NegativeResult_IsReturned(DecodeStatus negativeResult)
    {
        var side = LitSide;
        var calls = new Calls();

        var status = Run(Lit(negate: true), side, side, calls, DecodeStatus.NotDetected, negativeResult);

        await Assert.That(calls.Inputs.Count).IsEqualTo(2);
        await Assert.That(status).IsEqualTo(negativeResult);
    }

    /// <summary>A verdict on the symbol's content ends the retry like a read does: the negative of one symbol could only read another.</summary>
    [Test]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    public async Task Decode_PositiveContentVerdict_StopsThere(DecodeStatus positiveResult)
    {
        var side = LitSide;
        var calls = new Calls();

        var status = Run(Lit(negate: true), side, side, calls, out var info, positiveResult, DecodeStatus.Success);

        await Assert.That(calls.Inputs.Count).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(positiveResult);
        await Assert.That(info).IsEqualTo(1);
    }

    [Test]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    public async Task Decode_NegativeContentVerdict_IsReturned(DecodeStatus negativeResult)
    {
        var side = LitSide;
        var calls = new Calls();

        var status = Run(Lit(negate: true), side, side, calls, out var info, DecodeStatus.DataUncorrectable, negativeResult);

        await Assert.That(calls.Inputs.Count).IsEqualTo(2);
        await Assert.That(status).IsEqualTo(negativeResult);
        await Assert.That(info).IsEqualTo(2);
    }

    [Test]
    public async Task Decode_PositiveTooShortForItsDestination_StopsThere()
    {
        var side = LitSide;
        var calls = new Calls();

        var status = Run(Lit(negate: false), side, side, calls, DecodeStatus.DestinationTooSmall);

        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(calls.Inputs.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Noise of two levels more than 24 apart binarizes to itself in either polarity, so neither polarity is decoded again.
    /// Uneven levels are what tell each polarity's threshold apart: given the other polarity's threshold the negative of 20/100 noise would all read light and differ.
    /// </summary>
    [Test]
    [Arguments((byte)0, (byte)255)]
    [Arguments((byte)20, (byte)100)]
    [Arguments((byte)150, (byte)240)]
    public async Task Decode_TwoLevelNoise_DecodesNeitherPolarity(byte dark, byte light)
    {
        const int side = 96;
        var random = new Random(dark + light);
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = random.Next(2) == 0 ? dark : light;
        var calls = new Calls();

        var status = Run(luminance, side, side, calls);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(calls.Inputs.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Each polarity has its own gate: flat cells of 0 and 100 agree with the positive's global threshold and not with the negative's.
    /// </summary>
    [Test]
    public async Task Decode_OnlyTheNegativeDiffers_DecodesOnlyTheNegative()
    {
        const int side = 96;
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = ((i % side / 16) + (i / side / 16)) % 2 == 0 ? (byte)0 : (byte)100;
        var (negative, _) = AfterInvertedRetry(luminance);
        var calls = new Calls();

        var status = Run(luminance, side, side, calls);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(calls.Inputs.Count).IsEqualTo(1);
        await Assert.That(calls.Inputs[0]).IsEquivalentTo(Expected(negative, side, side));
    }

    /// <summary>
    /// A level one away from an extreme is not an extreme: a flat cell of 1 changes class, so the image is binarized and decoded.
    /// The same from the other end: a corner of 254 among 0 is that cell of 1 in the negative.
    /// </summary>
    [Test]
    [Arguments((byte)1, (byte)255)]
    [Arguments((byte)254, (byte)0)]
    public async Task Decode_LevelBesideAnExtreme_IsBinarized(byte corner, byte other)
    {
        const int side = 192;
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = ((i % side / 64) + (i / side / 64)) % 2 == 0 ? corner : other;
        var calls = new Calls();

        Run(luminance, side, side, calls);

        await Assert.That(calls.Inputs.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_UnderFiveBlocksASide_DecodesNothing()
    {
        var luminance = new byte[32 * 200];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(i * 7);
        var calls = new Calls();

        var status = Run(luminance, 32, 200, calls, DecodeStatus.Success);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(calls.Inputs.Count).IsEqualTo(0);
    }

    /// <summary>
    /// An image of only 0 and 255 reads the same regionally as globally in both polarities, so it skips the binarization.
    /// Held here over sizes whose last block overlaps the one before and over flat and isolated patterns, both polarities, vector and scalar.
    /// </summary>
    [Test]
    public async Task TryBinarize_OnlyExtremes_AgreesInBothPolarities()
    {
        (int, int)[] sizes = [(40, 40), (41, 43), (47, 64), (64, 41), (100, 37), (129, 131)];
        var disagreements = 0;
        var cases = 0;
        foreach (var (width, height) in sizes)
        {
            for (var pattern = 0; pattern < 6; pattern++)
            {
                var random = new Random(width * 31 + height + pattern);
                var luminance = new byte[width * height];
                for (var i = 0; i < luminance.Length; i++)
                {
                    var x = i % width;
                    var y = i / width;
                    var dark = pattern switch
                    {
                        0 => random.Next(2) == 0,
                        1 => false,
                        2 => true,
                        3 => x == width / 2 && y == height / 2,
                        4 => ((x / 8) + (y / 8)) % 2 == 0,
                        _ => random.Next(16) != 0,
                    };
                    luminance[i] = dark ? (byte)0 : (byte)255;
                }
                var (negative, _) = AfterInvertedRetry(luminance);
                foreach (var source in new[] { luminance, negative })
                {
                    var threshold = Binarizer.ComputeOtsuThreshold(source, out _);
                    var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
                    cases += 2;
                    disagreements += LocalBinarizer.TryBinarize(source, width, height, negative: false, threshold, new byte[source.Length], scratch, out _) ? 1 : 0;
                    disagreements += LocalBinarizer.TryBinarizeScalar(source, width, height, negative: false, threshold, new byte[source.Length], scratch, out _) ? 1 : 0;
                }
            }
        }

        await Assert.That(cases).IsEqualTo(144);
        await Assert.That(disagreements).IsEqualTo(0);
    }
}
