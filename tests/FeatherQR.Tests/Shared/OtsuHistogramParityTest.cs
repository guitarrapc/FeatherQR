using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Every tier of <see cref="Binarizer.FillHistogram"/> against a per-pixel count written here, bin for bin.
/// The threshold and the grey levels are functions of the histogram alone, so the 256 bins are the whole contract; <c>OtsuThresholdParityTest</c> compares thresholds, which a wrong count in a bin the search never weighs can leave unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The vector tier counts the pixels equal to 0 and to 255 in registers and sends only the others to the bins, a block at a time, and hands a block that is mostly other values to the scalar groups.
/// So the inputs are chosen by what a block holds, not by what the image shows: pure blocks, blocks with a few intruders, blocks on both sides of the dense cut-over, blocks of neither value, and module boundaries at every phase of a 32- and 8-pixel block.
/// </para>
/// <para>
/// Each tier is entered directly. The dispatcher reaches one tier per machine, so a test through it alone would never run the scalar tier on a machine with vectors.
/// </para>
/// </remarks>
public class OtsuHistogramParityTest
{
    private delegate void Fill(ReadOnlySpan<byte> luminance, Span<int> histogram);

    [Test]
    public async Task Dispatcher_MatchesPerPixelCount()
        => await AssertMatchesPerPixelCount(Binarizer.FillHistogram);

    [Test]
    public async Task ScalarTier_MatchesPerPixelCount()
        => await AssertMatchesPerPixelCount(Binarizer.FillHistogramScalar);

    [Test]
    public async Task Vector256Tier_MatchesPerPixelCount()
    {
        if (!Vector256.IsHardwareAccelerated)
            Skip.Test("Vector256 not accelerated on this machine");
        await AssertMatchesPerPixelCount(Binarizer.FillHistogramVector256);
    }

    /// <summary>The tiers store through unchecked references, so a histogram that cannot hold 256 bins is refused rather than overrun.</summary>
    [Test]
    public async Task ShortHistogram_IsRefused()
    {
        await Assert.That(() => Binarizer.FillHistogram(new byte[64], new int[255])).Throws<ArgumentOutOfRangeException>();
    }

    private static async Task AssertMatchesPerPixelCount(Fill fill)
    {
        var mismatches = new List<string>();
        var expected = new int[256];
        var actual = new int[256];
        foreach (var (name, data) in Inputs())
        {
            Array.Clear(expected);
            foreach (var value in data)
                expected[value]++;

            // dirty on purpose: a fill overwrites the bins, it does not add to them
            actual.AsSpan().Fill(-12345);
            fill(data, actual);

            for (var bin = 0; bin < 256; bin++)
            {
                if (actual[bin] != expected[bin])
                {
                    mismatches.Add($"{name} (length {data.Length}): bin {bin} = {actual[bin]}, expected {expected[bin]}");
                    break;
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    private static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        // two-valued symbols at whole and fractional module sizes: boundaries at every block phase
        for (var tenths = 10; tenths <= 160; tenths += 7)
            yield return ($"pitch {tenths / 10.0}", Symbol(24, tenths / 10f, tenths));
        yield return ("version 40 at 3.4 px", Symbol(185, 3.4f, 42));

        // two values that are not the counted ones
        var pair = Symbol(30, 3.4f, 5);
        for (var i = 0; i < pair.Length; i++)
            pair[i] = pair[i] == 0 ? (byte)0x22 : (byte)0xEE;
        yield return ("pair 22/EE", pair);

        // the neighbours of the counted values, which a compare off by one would count
        var neighbours = Symbol(30, 4f, 8);
        for (var i = 0; i < neighbours.Length; i++)
            neighbours[i] = neighbours[i] == 0 ? (byte)1 : (byte)254;
        yield return ("pair 01/FE", neighbours);

        var rng = new Random(11);
        var sparse = Symbol(30, 4f, 6);
        for (var i = 0; i < sparse.Length; i += rng.Next(1, 40))
            sparse[i] = (byte)rng.Next(1, 255);
        yield return ("sparse intruders", sparse);

        // exactly k other pixels in every block, k across the dense cut-over
        for (var k = 0; k <= 32; k++)
        {
            var block = new byte[32 * 9 + 5];
            for (var i = 0; i < block.Length; i++)
                block[i] = i % 32 < k ? (byte)(1 + i * 37 % 254) : (i % 3 == 0 ? (byte)0 : (byte)255);
            yield return ($"{k} others a block", block);
        }

        // uniform groups inside dense blocks: the scalar group's fold
        var plateaus = new byte[4096];
        for (var i = 0; i < plateaus.Length; i++)
            plateaus[i] = (byte)(64 + i / 24 % 100);
        yield return ("plateaus", plateaus);

        var gradient = new byte[200 * 200];
        for (var i = 0; i < gradient.Length; i++)
            gradient[i] = (byte)(255 * (i % 200 + i / 200) / 400);
        yield return ("gradient", gradient);

        var noise = new byte[256 * 256];
        rng.NextBytes(noise);
        yield return ("noise", noise);

        foreach (var value in new byte[] { 0, 1, 127, 128, 254, 255 })
        {
            var constant = new byte[1000 + value % 7];
            constant.AsSpan().Fill(value);
            yield return ($"constant {value}", constant);
        }

        // A run of blocks holding neither counted value is taken untested for a stretch: ending at every
        // distance from the end of the buffer, and running on into a two-valued region it must not swallow
        for (var length = 200; length <= 2300; length += 7)
        {
            var stretch = new byte[length];
            for (var i = 0; i < length; i++)
                stretch[i] = (byte)(1 + rng.Next(254));
            yield return ($"photo-like to the end {length}", stretch);
        }
        for (var lead = 0; lead <= 2200; lead += 61)
        {
            var mixed = new byte[lead + 1500];
            for (var i = 0; i < mixed.Length; i++)
                mixed[i] = i < lead || i >= mixed.Length - 90 ? (byte)(1 + rng.Next(254)) : (i / 5 % 2 == 0 ? (byte)0 : (byte)255);
            yield return ($"photo-like {lead} then two-valued", mixed);
        }

        // every length around every block width, random and two-valued
        for (var length = 0; length <= 100; length++)
        {
            var ragged = new byte[length];
            rng.NextBytes(ragged);
            yield return ($"ragged {length}", ragged);

            var binary = new byte[length];
            for (var i = 0; i < length; i++)
                binary[i] = (i * 7 + length) % 5 < 2 ? (byte)0 : (byte)255;
            yield return ($"ragged two-valued {length}", binary);
        }
    }

    /// <summary>A two-valued square, module k spanning pixels round(k · pitch) to round((k + 1) · pitch), as a crisp renderer lays out a fractional module size.</summary>
    private static byte[] Symbol(int modules, float pitch, int seed)
    {
        var side = (int)MathF.Round(modules * pitch);
        var scene = new byte[side * side];
        scene.AsSpan().Fill(255);
        var random = new Random(seed);
        for (var my = 0; my < modules; my++)
        {
            var y0 = (int)MathF.Round(my * pitch);
            var y1 = (int)MathF.Round((my + 1) * pitch);
            for (var mx = 0; mx < modules; mx++)
            {
                if (random.Next(100) >= 48)
                    continue;
                var x0 = (int)MathF.Round(mx * pitch);
                var x1 = (int)MathF.Round((mx + 1) * pitch);
                for (var y = y0; y < y1; y++)
                    scene.AsSpan(y * side + x0, x1 - x0).Clear();
            }
        }
        return scene;
    }
}
