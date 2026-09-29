#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="VectorCast.ToInt32"/> truncates toward zero from -1 (exclusive) up to 2^31 on every runtime, and outside that range lands
/// on the side it left: 0 or less below it or at NaN, at least 2,147,483,520 above it. The scalar cast is not so defined (it saturates
/// only from .NET 9 on and only on CoreCLR; the WebAssembly interpreter writes <see cref="int.MinValue"/>), and the samplers clamp such
/// lanes into the image and the sub-finder lattice skips them, so the tiers agree.
/// </summary>
public class VectorCastParityTest
{
    /// <summary>Where a conversion can go wrong: NaN, infinities, the int range's edges from both sides, and the fractions around zero.</summary>
    private static readonly float[] Edges =
    [
        float.NaN, -float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        0f, -0f, 0.49999997f, 0.5f, 0.99999994f, 1f, -0.5f, -0.99999994f, -1f, -1.5f,
        2147483520f, 2147483648f, 2147483904f, 4294967296f, 1e20f, float.MaxValue,
        -2147483520f, -2147483648f, -2147483904f, -4294967296f, -1e20f, float.MinValue,
        float.Epsilon, -float.Epsilon, 16777216f, 16777217f, 8388607.5f, -8388607.5f,
    ];

    [Test]
    public async Task ToInt32_TruncatesAndSaturates_OnTheEdges()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var mismatches = new List<string>();
        foreach (var value in Edges)
        {
            // Every lane position, the other lanes holding values of other kinds, so a fix-up that leaks across lanes shows
            for (var lane = 0; lane < 4; lane++)
            {
                var lanes = new[] { 3.7f, float.NaN, 2147483648f, -2.5f };
                lanes[lane] = value;
                Compare(lanes, mismatches);
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    [Test]
    public async Task ToInt32_TruncatesAndSaturates_OnRandomBitPatterns()
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
            for (var lane = 0; lane < 4; lane++)
            {
                // Half raw bit patterns (every exponent, NaN payloads, denormals), half coordinates around an image
                lanes[lane] = (trial & 1) == 0
                    ? BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue))
                    : (float)(random.NextDouble() * 8192 - 4096);
            }
            Compare(lanes, mismatches);
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    /// <summary>What the contract allows: the truncation from -1 up to 2^31 (the cast is exact there on every runtime), outside it the side it left.</summary>
    internal static bool Allowed(float value, int got)
        => float.IsNaN(value) || value <= -1f ? got <= 0
            : value >= 2147483648f ? got >= 2147483520
            : got == (int)value;

    private static void Compare(float[] lanes, List<string> mismatches)
    {
        var actual = VectorCast.ToInt32(Vector128.Create(lanes));
        for (var lane = 0; lane < 4; lane++)
        {
            var got = actual.GetElement(lane);
            var ok = Allowed(lanes[lane], got);
            if (!ok)
                mismatches.Add($"{lanes[lane]:R} (0x{BitConverter.SingleToInt32Bits(lanes[lane]):X8}) in lane {lane}: {got}");
        }
    }
}
#endif
