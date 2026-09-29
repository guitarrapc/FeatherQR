using System.Runtime.Intrinsics;
using FeatherQR;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using FeatherQR.Tests;

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
        failures += Report("FinderPatternFinder edge-list kernel", FinderEdgeListMismatches);
        failures += Report("RmQRModulePlacer", RmQRPlacerMismatches);
        failures += Report("ModuleBitPacker", BitPackerMismatches);
        failures += Report("TextAnalyzer", TextAnalyzerMismatches);
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

    /// <summary>
    /// The finder search's candidates with the edge-list row kernel against the scalar walk's, bit for bit and in order: symbols from
    /// under two pixels a module up, crisp and blurred, every row and strided; noise at widths around the 16-pixel compares, the
    /// 64-pixel words and the eight-window steps, thresholds at the extremes, the grey second look on and off; and a finder flush
    /// against the right edge behind bars that move a row's last window across every lane of a step.
    /// </summary>
    private static List<string> FinderEdgeListMismatches()
    {
        var mismatches = new List<string>();
        var reference = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var actual = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var compared = 0;
        void Compare(string name, byte[] luminance, int width, int height, byte threshold, GreyLevels grey, int stride)
        {
            Array.Clear(reference);
            Array.Clear(actual);
            var expected = FinderPatternFinder.FindCandidatesWith(luminance, width, height, threshold, reference, grey, stride, FinderRowKernel.Scalar);
            var count = FinderPatternFinder.FindCandidatesWith(luminance, width, height, threshold, actual, grey, stride, FinderRowKernel.EdgeList);
            compared += expected;
            var same = count == expected;
            for (var i = 0; same && i < expected; i++)
            {
                same = BitConverter.SingleToInt32Bits(actual[i].X) == BitConverter.SingleToInt32Bits(reference[i].X)
                    && BitConverter.SingleToInt32Bits(actual[i].Y) == BitConverter.SingleToInt32Bits(reference[i].Y)
                    && BitConverter.SingleToInt32Bits(actual[i].ModuleSize) == BitConverter.SingleToInt32Bits(reference[i].ModuleSize)
                    && actual[i].Count == reference[i].Count;
            }
            if (!same)
                mismatches.Add($"{name}: {count} candidates, reference {expected}");
        }

        foreach (var version in new[] { 1, 7, 20 })
        {
            var data = QRCodeGenerator.Create($"row kernel {version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
            foreach (var pitch in new[] { 1.3f, 2f, 2.2f, 3f, 3.4f, 7f })
            {
                var (crisp, width, height) = NearestNeighbourRenderer.Render((row, column) => data[row, column], data.Size, data.Size, pitch, 18.5f, 2.5f);
                foreach (var blurred in new[] { false, true })
                {
                    var scene = blurred ? BoxBlur(crisp, width, height) : crisp;
                    var threshold = Binarizer.ComputeOtsuThreshold(scene, out var grey);
                    foreach (var stride in new[] { 1, 3 })
                        Compare($"version {version}, pitch {pitch}, blurred {blurred}, stride {stride}", scene, width, height, threshold, grey, stride);
                }
            }
        }

        foreach (var width in new[] { 16, 17, 31, 32, 33, 47, 63, 64, 65, 96, 127, 128, 129, 191, 192, 193, 255, 256, 257 })
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                const int Height = 24;
                var noise = new byte[width * Height];
                var random = new Random(width * 31 + threshold);
                random.NextBytes(noise);
                // All dark, all light, starting dark, ending dark: the rows a kernel gets wrong at its ends
                noise.AsSpan(0, width).Fill(0);
                noise.AsSpan(width, width).Fill(255);
                noise[2 * width] = 0;
                noise[3 * width - 1] = 0;
                // Runs of two to five pixels in the lower rows, so ratios hold more often than in pixel noise
                for (var y = 12; y < Height; y++)
                {
                    for (var x = 0; x < width;)
                    {
                        var run = random.Next(2, 6);
                        var level = random.Next(2) == 0 ? (byte)0 : (byte)255;
                        for (var k = 0; k < run && x < width; k++, x++)
                            noise[y * width + x] = level;
                    }
                }
                var histogram = new int[256];
                foreach (var value in noise)
                    histogram[value]++;
                Compare($"noise {width} px, threshold {threshold}, grey on", noise, width, Height, threshold, GreyLevels.FromHistogram(histogram, threshold), 1);
                Compare($"noise {width} px, threshold {threshold}, grey off", noise, width, Height, threshold, default, 1);
            }
        }

        // Fields of finders, every one accepted, the phase moving them across the 64-pixel words and the steps of windows
        foreach (var module in new[] { 2, 3, 4, 6 })
        {
            for (var phase = 0; phase < 8; phase++)
            {
                var cell = 9 * module + 3;
                var width = 6 * cell + phase * 9 + 40;
                var height = 4 * cell + 8;
                var field = new byte[width * height];
                field.AsSpan().Fill(255);
                for (var gy = 0; gy < 4; gy++)
                {
                    for (var gx = 0; gx < 6; gx++)
                        WriteFinder(field, width, phase * 9 + 5 + gx * cell + (gy & 1) * module, 4 + gy * cell, module);
                }
                foreach (var stride in new[] { 1, 3 })
                    Compare($"finder field, module {module}, phase {phase}, stride {stride}", field, width, height, 128, default, stride);
            }
        }

        foreach (var module in new[] { 2, 3, 4 })
        {
            for (var bars = 0; bars < 16; bars++)
            {
                var x0 = 30 + bars * 2 * module;
                var width = x0 + 7 * module;
                var height = 9 * module + 2;
                var scene = new byte[width * height];
                scene.AsSpan().Fill(255);
                for (var y = 0; y < height; y++)
                {
                    for (var bar = 0; bar < bars; bar++)
                        scene.AsSpan(y * width + 10 + bar * 2 * module, module).Fill(0);
                }
                WriteFinder(scene, width, x0, module, module);
                // One row's right ring light: its last window stops a dark run short of the row above's
                scene.AsSpan((module + 3 * module) * width + x0 + 6 * module, module).Fill(255);
                Compare($"notched finder at the right edge, module {module}, {bars} bars", scene, width, height, 128, default, 1);
            }
        }
        // A comparison over next to no candidates would pass whatever the kernel did
        if (compared < 1_000)
            mismatches.Add($"{compared} candidates compared, fewer than expected");
        return mismatches;
    }

    /// <summary>A finder of <paramref name="module"/>-pixel modules at (<paramref name="x0"/>, <paramref name="y0"/>) with a light module of quiet zone around it.</summary>
    private static void WriteFinder(byte[] image, int width, int x0, int y0, int module)
    {
        for (var my = -1; my <= 7; my++)
        {
            for (var mx = -1; mx <= 7; mx++)
            {
                var ring = mx is >= 0 and <= 6 && my is >= 0 and <= 6 ? Math.Max(Math.Abs(mx - 3), Math.Abs(my - 3)) : 2;
                var level = ring == 2 ? (byte)255 : (byte)0;
                for (var y = y0 + my * module; y < y0 + (my + 1) * module; y++)
                {
                    for (var x = x0 + mx * module; x < x0 + (mx + 1) * module; x++)
                    {
                        if (x >= 0 && x < width && y >= 0)
                            image[y * width + x] = level;
                    }
                }
            }
        }
    }

    /// <summary>rMQR placement through the dispatch against the reference placer: every version and level, messages all 0, all 1 and random.</summary>
    private static List<string> RmQRPlacerMismatches()
    {
        var mismatches = new List<string>();
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var size = RmQRConstants.GetWidth(version) * RmQRConstants.GetHeight(version);
                var message = new byte[RmQRConstants.GetTotalCodewordCount(version)];
                for (var kind = 0; kind < 3; kind++)
                {
                    if (kind == 1)
                        Array.Fill(message, (byte)0xFF);
                    else if (kind == 2)
                        new Random((int)version * 7 + (int)ecc).NextBytes(message);
                    var expected = new byte[size];
                    var actual = new byte[size];
                    RmQRModulePlacer.PlaceSymbolReference(expected, version, ecc, message);
                    RmQRModulePlacer.PlaceSymbol(actual, version, ecc, message);
                    if (!expected.AsSpan().SequenceEqual(actual))
                        mismatches.Add($"{version} {ecc}, message {(kind == 0 ? "all 0" : kind == 1 ? "all 1" : "random")}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// Pack and Unpack against a loop of single bits, at every length to 300 modules and an R17x139 core, dark modules any
    /// non-zero byte.
    /// </summary>
    private static List<string> BitPackerMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(17);
        foreach (var count in Enumerable.Range(0, 301).Append(17 * 139))
        {
            var modules = new byte[count];
            for (var i = 0; i < count; i++)
                modules[i] = random.Next(3) == 0 ? (byte)0 : (byte)random.Next(1, 256);
            var expected = new byte[(count + 7) / 8];
            for (var i = 0; i < count; i++)
            {
                if (modules[i] != 0)
                    expected[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            var packed = new byte[expected.Length];
            ModuleBitPacker.Pack(modules, packed);
            if (!packed.AsSpan().SequenceEqual(expected))
                mismatches.Add($"pack {count}");

            var unpacked = new byte[count];
            ModuleBitPacker.Unpack(expected, unpacked);
            for (var i = 0; i < count; i++)
            {
                if (unpacked[i] != (modules[i] != 0 ? 1 : 0))
                {
                    mismatches.Add($"unpack {count}, module {i}");
                    break;
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The text analysis through the dispatch against the scalar pass: each boundary char, U+0130 among them (a narrowing that
    /// dropped the high byte would read it as '0'), at every position of every length from 1 to 40, among digits and alphanumerics.
    /// </summary>
    private static List<string> TextAnalyzerMismatches()
    {
        var mismatches = new List<string>();
        const string Fillers = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        char[] boundaries = ['/', '0', '9', ':', ';', '@', 'A', 'Z', '[', ' ', '!', '$', '%', '&', '*', '+', ',', '-', '.', 'a',
            '\u007F', '\u0080', '\u00FF', '\u0100', '\u0120', '\u012D', '\u0130', '\u013A', '\u0141', '\u01FF', '\u8000', '\uFFFD'];
        foreach (var c in boundaries)
        {
            for (var length = 1; length <= 40; length++)
            {
                for (var position = 0; position < length; position++)
                {
                    var chars = new char[length];
                    for (var k = 0; k < length; k++)
                        chars[k] = Fillers[(k * 7 + length) % Fillers.Length];
                    chars[position] = c;
                    var text = new string(chars);
                    foreach (var eci in new[] { EciMode.Default, EciMode.Utf8 })
                    {
                        if (TextAnalyzer.Analyze(text, eci) != TextAnalyzer.AnalyzeScalar(text, eci))
                            mismatches.Add($"U+{(int)c:X4} at {position} of {length}, {eci}");
                    }
                }
            }
        }
        return mismatches;
    }

    /// <summary>A 3 x 3 box blur, edges clamped: grey pixels for the second look.</summary>
    private static byte[] BoxBlur(byte[] luminance, int width, int height)
    {
        var soft = new byte[luminance.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                        sum += luminance[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
                }
                soft[y * width + x] = (byte)(sum / 9);
            }
        }
        return soft;
    }
}
