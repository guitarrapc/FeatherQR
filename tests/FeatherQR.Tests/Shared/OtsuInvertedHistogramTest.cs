using FeatherQR.Internals.ImageDecoders;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The inverted retry takes its threshold and grey levels from the first polarity's histogram, mirrored, instead of counting the negative's pixels.
/// The negative's histogram is the first one with bin i at 255 − i, so the result must be what counting the negative gives, on every input.
/// </summary>
/// <remarks>
/// <para>
/// The expected values come from a per-pixel count of the negative written here, not from the library's fill.
/// </para>
/// <para>
/// The search runs again on the mirrored bins rather than mirroring the threshold. Two values with nothing between them tie at every split, the search keeps the first, and the first from the other end is a different split: <c>256 − threshold</c> is wrong there.
/// </para>
/// <para>
/// The decoder tests use a light-on-dark symbol whose two levels are not mirror images of each other. On pure black and white the first polarity's bins, unmirrored, give a threshold that also splits the negative, so a retry that forgot to mirror would still read it.
/// </para>
/// </remarks>
public class OtsuInvertedHistogramTest
{
    [Test]
    public async Task HistogramOverload_MatchesPerPixelReference()
    {
        var mismatches = new List<string>();
        var histogram = new int[256];
        foreach (var (name, data) in Inputs())
        {
            var expected = NaiveOtsu(data, histogram, out var expectedGrey);
            var actual = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var actualGrey);
            if (actual != expected || !Same(actualGrey, expectedGrey))
                mismatches.Add($"{name}: threshold {actual}, expected {expected}");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task MirroredHistogram_MatchesCountingTheNegative()
    {
        var mismatches = new List<string>();
        var histogram = new int[256];
        var scratch = new int[256];
        foreach (var (name, data) in Inputs())
        {
            var negative = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
                negative[i] = (byte)(255 - data[i]);
            var expected = NaiveOtsu(negative, scratch, out var expectedGrey);

            Binarizer.FillHistogram(data, histogram);
            Binarizer.InvertHistogram(histogram);
            var actual = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var actualGrey);

            if (actual != expected || !Same(actualGrey, expectedGrey))
                mismatches.Add($"{name}: threshold {actual}, expected {expected}");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task ShortHistogram_IsRefused()
    {
        await Assert.That(() => Binarizer.ComputeOtsuThresholdFromHistogram(new int[255], out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Binarizer.InvertHistogram(new int[255])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardQR_LightOnDark_UnevenLevels()
    {
        const string content = "light on dark, uneven levels";
        var qr = QRCodeGenerator.Create(content, QREccLevel.M);
        var side = qr.Size * 6;
        using var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, side, side), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }
        var luminance = LightOnDark(bitmap);

        var chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        var decoded = QRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, chars, out var written, out var info);

        await Assert.That(decoded).IsTrue().Because($"status={info.Status}");
        await Assert.That(new string(chars, 0, written)).IsEqualTo(content);
    }

    [Test]
    public async Task MicroQR_LightOnDark_UnevenLevels()
    {
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = new MicroQRCodeImageBuilder(data).WithModulePixelSize(8).ToBitmap();
        var luminance = LightOnDark(bitmap);

        var chars = new char[64];
        var decoded = MicroQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, chars, out var written, out var info);

        await Assert.That(decoded).IsTrue().Because($"status={info.Status}");
        await Assert.That(new string(chars, 0, written)).IsEqualTo(content);
    }

    [Test]
    public async Task RmQR_LightOnDark_UnevenLevels()
    {
        const string content = "RMQR IMAGE 123";
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59 });
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(6).ToBitmap();
        var luminance = LightOnDark(bitmap);

        var chars = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var decoded = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, chars, out var written, out var info);

        await Assert.That(decoded).IsTrue().Because($"status={info.Status}");
        await Assert.That(new string(chars, 0, written)).IsEqualTo(content);
    }

    /// <summary>Modules at 150 on a background of 25: the negative is 105 on 230, and only a threshold between those reads it.</summary>
    private static byte[] LightOnDark(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red < 128 ? (byte)150 : (byte)25;
        }
        return luminance;
    }

    private static bool Same(GreyLevels a, GreyLevels b)
    {
        if (a.IsEnabled != b.IsEnabled)
            return false;
        for (var value = 0; value < 256; value++)
        {
            if (a.Darkness((byte)value) != b.Darkness((byte)value))
                return false;
        }
        return true;
    }

    /// <summary>Per-pixel count and the search as it was before the histogram could come from elsewhere: the total is the pixel count, not a sum of bins.</summary>
    private static byte NaiveOtsu(ReadOnlySpan<byte> luminance, int[] histogram, out GreyLevels grey)
    {
        Array.Clear(histogram);
        foreach (var value in luminance)
            histogram[value]++;

        var total = luminance.Length;
        long sumAll = 0;
        for (var i = 0; i < 256; i++)
            sumAll += (long)i * histogram[i];

        long sumBackground = 0;
        long weightBackground = 0;
        var bestVariance = -1.0;
        var bestThreshold = 128;
        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
                continue;
            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
                break;

            sumBackground += (long)t * histogram[t];
            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            var diff = meanBackground - meanForeground;
            var variance = weightBackground * (double)weightForeground * diff * diff;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t + 1;
            }
        }

        var threshold = Math.Min(bestThreshold, 255);
        grey = GreyLevels.FromHistogram(histogram, threshold);
        return (byte)threshold;
    }

    private static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        // two values, nothing between: every split between them ties
        yield return ("black and white", TwoValued(4000, 0, 255, 45));
        yield return ("black and white, even", TwoValued(4000, 0, 255, 50));
        yield return ("pair 25/150", TwoValued(4000, 25, 150, 40));
        yield return ("pair 22/EE", TwoValued(4000, 0x22, 0xEE, 55));
        yield return ("pair 01/FE", TwoValued(4000, 1, 254, 30));
        // the only split is at the top bin, and at the bottom one in the negative
        yield return ("pair FE/FF", TwoValued(4000, 254, 255, 50));

        // edge pixels between the two levels: the grey levels are enabled
        for (var seed = 0; seed < 8; seed++)
            yield return ($"soft edges {seed}", SoftEdges(6000, seed));

        var rng = new Random(23);
        for (var round = 0; round < 8; round++)
        {
            // a skewed spread, so the negative is not the same image
            var skewed = new byte[3000 + round];
            for (var i = 0; i < skewed.Length; i++)
                skewed[i] = (byte)(rng.Next(256) * rng.Next(256) / 255);
            yield return ($"skewed {round}", skewed);
        }

        var gradient = new byte[200 * 200];
        for (var i = 0; i < gradient.Length; i++)
            gradient[i] = (byte)(255 * (i % 200 + i / 200) / 400);
        yield return ("gradient", gradient);

        var noise = new byte[128 * 128];
        rng.NextBytes(noise);
        yield return ("noise", noise);

        foreach (var value in new byte[] { 0, 1, 127, 128, 254, 255 })
        {
            var constant = new byte[500];
            constant.AsSpan().Fill(value);
            yield return ($"constant {value}", constant);
        }

        yield return ("empty", []);
        for (var length = 1; length <= 40; length++)
        {
            var ragged = new byte[length];
            rng.NextBytes(ragged);
            yield return ($"ragged {length}", ragged);
        }
    }

    private static byte[] TwoValued(int length, byte dark, byte light, int darkPercent)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = i * 100L / length < darkPercent ? dark : light;
        return data;
    }

    /// <summary>Runs of two levels with a ramp of a few pixels at every change, the levels themselves off centre.</summary>
    private static byte[] SoftEdges(int length, int seed)
    {
        var rng = new Random(seed);
        var dark = (byte)(10 + seed * 9);
        var light = (byte)(250 - seed * 13);
        var data = new byte[length];
        var current = light;
        var i = 0;
        while (i < length)
        {
            var run = rng.Next(3, 20);
            var next = current == light ? dark : light;
            for (var k = 0; k < run && i < length; k++)
                data[i++] = current;
            for (var k = 1; k <= 2 && i < length; k++)
                data[i++] = (byte)(current + (next - current) * k / 3);
            current = next;
        }
        return data;
    }
}
