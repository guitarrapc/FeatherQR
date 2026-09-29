using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

/// <summary>
/// With <c>--parity</c>: vector tiers held to their scalar forms on the build itself, for the code a test run never executes:
/// a platform instruction inside a portable tier (<c>PackedSimd</c> on WebAssembly) and the code ILC emits for a native build.
/// Shared by the NativeAOT gate and the WebAssembly report (tests/FeatherQR.WasmReport links this file); the unit tests hold
/// the same tiers to the same forms under the JIT.
/// </summary>
internal static class SimdParity
{
    public static bool Requested(string[] args) => Array.IndexOf(args, "--parity") >= 0;

    /// <summary>Prints each check and returns 1 when any output differs from the scalar form's, else 0.</summary>
    public static int Run()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Console.WriteLine("Parity: no 128-bit vectors on this build, nothing to compare.");
            return 0;
        }

        var failures = 0;
        failures += Report("VectorCast", CastMismatches);
        failures += Report("QRImageDecoder.SampleGridSimd128", QRSamplerMismatches);
        failures += Report("MicroQRImageDecoder.SampleGridVector128", MicroSamplerMismatches);
        failures += Report("RmQRImageDecoder.SampleGridSimd128", RmQRSamplerMismatches);
        failures += Report("RmQRImageDecoder.ClassifySubFinderLatticeVector128", LatticeMismatches);
        return failures == 0 ? 0 : 1;
    }

    private static int Report(string name, Func<List<string>> check)
    {
        List<string> mismatches;
        try
        {
            mismatches = check();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Parity {name}: threw");
            Console.Error.WriteLine(e);
            return 1;
        }
        Console.WriteLine($"Parity {name}: {(mismatches.Count == 0 ? "matches" : $"{mismatches.Count} mismatches")}");
        foreach (var mismatch in mismatches.Distinct().Where((_, i) => i % Math.Max(1, mismatches.Count / 12) == 0).Take(12))
            Console.Error.WriteLine($"  {mismatch}");
        return mismatches.Count == 0 ? 0 : 1;
    }

    private static readonly float[] Edges =
    [
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -0f, 0.99999994f, -0.99999994f, -1.5f, 4094.9998f, 4095f, 4095.9998f, 4096f,
        2147483520f, 2147483648f, 1e20f, float.MaxValue, -2147483520f, -2147483648f, -2147483904f, -1e20f, float.MinValue, float.Epsilon,
    ];

    /// <summary>The image <see cref="CastMismatches"/> clamps into, 4,096 pixels across.</summary>
    private const int CastLimit = 4096;

    private static List<string> CastMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(20260929);
        var lanes = new float[4];
        for (var trial = 0; trial < Edges.Length * 4 + 20_000; trial++)
        {
            if (trial < Edges.Length * 4)
            {
                lanes[0] = 3.7f;
                lanes[1] = float.NaN;
                lanes[2] = 2147483648f;
                lanes[3] = -2.5f;
                lanes[trial & 3] = Edges[trial >> 2];
            }
            else
            {
                for (var lane = 0; lane < 4; lane++)
                    lanes[lane] = (trial & 1) == 0 ? BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue)) : (float)(random.NextDouble() * 8192 - 4096);
            }
            var pixel = VectorCast.ToPixel(Vector128.Create(lanes), Vector128.Create((float)(CastLimit - 1)));
            var native = VectorCast.ToInt32Native(Vector128.Create(lanes));
            for (var lane = 0; lane < 4; lane++)
            {
                // The pixel the scalar samplers take; the unclamped conversion only where the lattice reads it
                var value = lanes[lane];
                if (pixel.GetElement(lane) != PixelIndex.Clamp(value, CastLimit))
                    mismatches.Add($"{value:R}: pixel {pixel.GetElement(lane)}, scalar {PixelIndex.Clamp(value, CastLimit)}");
                if (value > -1f && value < 2147483648f && native.GetElement(lane) != (int)value)
                    mismatches.Add($"{value:R}: native {native.GetElement(lane)}");
            }
        }
        return mismatches;
    }

    /// <summary>A random dark and light image, and coefficients that put points inside it, past its edges, and at infinity or NaN.</summary>
    private static (byte[] Luminance, int Width, int Height) Scene(Random random, int width, int height)
    {
        var luminance = new byte[width * height];
        random.NextBytes(luminance);
        return (luminance, width, height);
    }

    private static float Coefficient(Random random, float scale) => random.Next(10) switch
    {
        0 => float.NaN,
        1 => random.Next(2) == 0 ? float.PositiveInfinity : float.NegativeInfinity,
        2 => (float)(random.NextDouble() * 2 - 1) * 1e12f,
        _ => (float)(random.NextDouble() * 2 - 1) * scale,
    };

    private static PerspectiveTransform Transform(Random random, bool degenerate)
    {
        if (!degenerate)
        {
            return PerspectiveTransform.FromCoefficients(
                (float)(random.NextDouble() * 12 - 2), (float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 400 - 100),
                (float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 12 - 2), (float)(random.NextDouble() * 400 - 100),
                (float)(random.NextDouble() * 0.004 - 0.002), (float)(random.NextDouble() * 0.004 - 0.002), 1f);
        }
        return PerspectiveTransform.FromCoefficients(
            Coefficient(random, 12), Coefficient(random, 4), Coefficient(random, 400),
            Coefficient(random, 4), Coefficient(random, 12), Coefficient(random, 400),
            Coefficient(random, 0.01f), Coefficient(random, 0.01f), random.Next(4) == 0 ? 0f : 1f);
    }

    private static List<string> QRSamplerMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(1);
        foreach (var dimension in new[] { 21, 25, 33, 77, 177 })
        {
            for (var trial = 0; trial < 40; trial++)
            {
                var (luminance, width, height) = Scene(random, 300 + random.Next(400), 300 + random.Next(400));
                var transform = Transform(random, degenerate: trial % 4 == 3);
                var threshold = (byte)random.Next(256);
                var scalar = new byte[dimension * dimension];
                var vector = new byte[dimension * dimension];
                QRImageDecoder.SampleGridScalar(luminance, width, height, threshold, transform, dimension, scalar);
                QRImageDecoder.SampleGridSimd128(luminance, width, height, threshold, transform, dimension, vector);
                if (!scalar.AsSpan().SequenceEqual(vector))
                    mismatches.Add($"dimension {dimension}, trial {trial}");
            }
        }
        return mismatches;
    }

    private static List<string> MicroSamplerMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(2);
        foreach (var size in new[] { 11, 13, 15, 17 })
        {
            for (var trial = 0; trial < 200; trial++)
            {
                var (luminance, width, height) = Scene(random, 60 + random.Next(300), 60 + random.Next(300));
                var degenerate = trial % 4 == 3;
                var (originX, originY) = degenerate ? (Coefficient(random, 400), Coefficient(random, 400)) : ((float)(random.NextDouble() * 300 - 50), (float)(random.NextDouble() * 300 - 50));
                var (uX, uY, vX, vY) = degenerate
                    ? (Coefficient(random, 20), Coefficient(random, 20), Coefficient(random, 20), Coefficient(random, 20))
                    : ((float)(random.NextDouble() * 24 - 4), (float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 24 - 4));
                var threshold = (byte)random.Next(256);
                var scalar = new byte[size * size];
                var vector = new byte[size * size];
                MicroQRImageDecoder.SampleGridScalar(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, scalar);
                MicroQRImageDecoder.SampleGridVector128(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, vector);
                if (!scalar.AsSpan().SequenceEqual(vector))
                {
                    var k = scalar.AsSpan().CommonPrefixLength(vector);
                    var (v, u) = (k / size, k % size);
                    var x = originX + (v + 0.5f) * vX + (u + 0.5f) * uX;
                    var y = originY + (v + 0.5f) * vY + (u + 0.5f) * uY;
                    mismatches.Add($"size {size}, trial {trial}, module ({u}, {v}): x {x:R} y {y:R}, cast ({(int)x}, {(int)y}), pixel ({PixelIndex.Clamp(x, width)}, {PixelIndex.Clamp(y, height)}), vector pixel ({VectorCast.ToPixel(Vector128.Create(x), Vector128.Create((float)(width - 1))).ToScalar()}, {VectorCast.ToPixel(Vector128.Create(y), Vector128.Create((float)(height - 1))).ToScalar()})");
                }
            }
        }
        return mismatches;
    }

    private static List<string> RmQRSamplerMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(3);
        foreach (var (columns, rows) in new[] { (27, 7), (43, 7), (59, 11), (77, 13), (99, 15), (139, 17) })
        {
            for (var trial = 0; trial < 40; trial++)
            {
                var (luminance, width, height) = Scene(random, 300 + random.Next(900), 80 + random.Next(300));
                var transform = Transform(random, degenerate: trial % 4 == 3);
                var threshold = (byte)random.Next(256);
                var scalar = new byte[columns * rows];
                var vector = new byte[columns * rows];
                RmQRImageDecoder.SampleGridScalar(luminance, width, height, threshold, transform, columns, rows, scalar);
                RmQRImageDecoder.SampleGridSimd128(luminance, width, height, threshold, transform, columns, rows, vector);
                if (!scalar.AsSpan().SequenceEqual(vector))
                    mismatches.Add($"{columns}x{rows}, trial {trial}");
            }
        }
        return mismatches;
    }

    private static List<string> LatticeMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(4);
        var scalarDark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var scalarLight = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var vectorDark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var vectorLight = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        for (var trial = 0; trial < 2000; trial++)
        {
            var (luminance, width, height) = Scene(random, 40 + random.Next(400), 40 + random.Next(300));
            var degenerate = trial % 8 == 7;
            var module = (float)(random.NextDouble() * 10 + 0.5);
            var angle = random.NextDouble() * 2 * Math.PI;
            var uX = degenerate ? Coefficient(random, 10) : module * (float)Math.Cos(angle);
            var uY = degenerate ? Coefficient(random, 10) : module * (float)Math.Sin(angle);
            var svX = module * 1.1f * (float)Math.Sin(angle + 0.2);
            var svY = degenerate ? Coefficient(random, 10) : module * 1.1f * (float)Math.Cos(angle - 0.2);
            var predictedX = (float)(random.NextDouble() * 2 - 0.5) * width;
            var predictedY = (float)(random.NextDouble() * 2 - 0.5) * height;
            var side = 2 * random.Next(0, 28) + 9;
            var marginX = (float)random.NextDouble() * 0.3f;
            var marginY = (float)random.NextDouble() * 0.3f;
            var threshold = (byte)random.Next(1, 256);
            Array.Clear(scalarDark);
            Array.Clear(scalarLight);
            Array.Clear(vectorDark);
            Array.Clear(vectorLight);
            RmQRImageDecoder.ClassifySubFinderLatticeScalar(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, scalarDark, scalarLight);
            RmQRImageDecoder.ClassifySubFinderLatticeVector128(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, vectorDark, vectorLight);
            if (!scalarDark.AsSpan().SequenceEqual(vectorDark) || !scalarLight.AsSpan().SequenceEqual(vectorLight))
                mismatches.Add($"trial {trial}");
        }
        return mismatches;
    }
}
