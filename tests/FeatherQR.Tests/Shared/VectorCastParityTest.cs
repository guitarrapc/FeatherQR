#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="VectorCast.ToPixel"/> lands every lane on the pixel <see cref="PixelIndex.Clamp(float, int)"/> takes, for any float,
/// which is what keeps the 128-bit samplers byte-identical to their scalar tiers; <see cref="VectorCast.ToInt32Native"/> truncates
/// exactly from -1 (exclusive) up to 2^31, the only lanes the sub-finder lattice reads.
/// </summary>
public class VectorCastParityTest
{
    /// <summary>Where a conversion can go wrong: NaN, infinities, the int range's edges from both sides, the fractions around zero and the image's far edge.</summary>
    private static readonly float[] Edges =
    [
        float.NaN, -float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        0f, -0f, 0.49999997f, 0.5f, 0.99999994f, 1f, -0.5f, -0.99999994f, -1f, -1.5f,
        16.999998f, 17f, 17.5f, 4094.9998f, 4095f, 4095.9998f, 4096f, 4096.5f,
        2147483520f, 2147483648f, 2147483904f, 4294967296f, 1e20f, float.MaxValue,
        -2147483520f, -2147483648f, -2147483904f, -4294967296f, -1e20f, float.MinValue,
        float.Epsilon, -float.Epsilon, 16777216f, 16777217f, 8388607.5f, -8388607.5f,
    ];

    /// <summary>Image widths: a single pixel, a small symbol's, a large image's.</summary>
    private static readonly int[] Limits = [1, 17, 4096];

    [Test]
    public async Task ToPixel_MatchesScalarClamp_OnTheEdges()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var mismatches = new List<string>();
        foreach (var limit in Limits)
        {
            foreach (var value in Edges)
            {
                // Every lane position, the other lanes holding values of other kinds, so a fix-up that leaks across lanes shows
                for (var lane = 0; lane < 4; lane++)
                {
                    var lanes = new[] { 3.7f, float.NaN, 2147483648f, -2.5f };
                    lanes[lane] = value;
                    ComparePixel(lanes, limit, mismatches);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    [Test]
    public async Task ToPixel_MatchesScalarClamp_OnRandomBitPatterns()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var random = new Random(20260929);
        var lanes = new float[4];
        var mismatches = new List<string>();
        for (var trial = 0; trial < 20_000; trial++)
        {
            RandomLanes(random, trial, lanes);
            ComparePixel(lanes, Limits[trial % Limits.Length], mismatches);
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    [Test]
    public async Task ToInt32Native_Truncates_InsideTheIntRange()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var random = new Random(20260929);
        var lanes = new float[4];
        var mismatches = new List<string>();
        foreach (var value in Edges)
        {
            lanes[0] = lanes[1] = lanes[2] = lanes[3] = value;
            CompareNative(lanes, mismatches);
        }
        for (var trial = 0; trial < 20_000; trial++)
        {
            RandomLanes(random, trial, lanes);
            CompareNative(lanes, mismatches);
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    /// <summary>Half raw bit patterns (every exponent, NaN payloads, denormals), half coordinates around and past a 4,096-pixel image.</summary>
    private static void RandomLanes(Random random, int trial, float[] lanes)
    {
        for (var lane = 0; lane < 4; lane++)
        {
            lanes[lane] = (trial & 1) == 0
                ? BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue))
                : (float)(random.NextDouble() * 8192 - 2048);
        }
    }

    private static void ComparePixel(float[] lanes, int limit, List<string> mismatches)
    {
        var actual = VectorCast.ToPixel(Vector128.Create(lanes), Vector128.Create((float)(limit - 1)));
        for (var lane = 0; lane < 4; lane++)
        {
            var expected = PixelIndex.Clamp(lanes[lane], limit);
            if (actual.GetElement(lane) != expected)
                mismatches.Add($"{lanes[lane]:R} (0x{BitConverter.SingleToInt32Bits(lanes[lane]):X8}) in lane {lane}, limit {limit}: {actual.GetElement(lane)}, scalar {expected}");
        }
    }

    private static void CompareNative(float[] lanes, List<string> mismatches)
    {
        var actual = VectorCast.ToInt32Native(Vector128.Create(lanes));
        for (var lane = 0; lane < 4; lane++)
        {
            var value = lanes[lane];
            if (value > -1f && value < 2147483648f && actual.GetElement(lane) != (int)value)
                mismatches.Add($"{value:R} (0x{BitConverter.SingleToInt32Bits(value):X8}) in lane {lane}: {actual.GetElement(lane)}");
        }
    }
}
#endif
