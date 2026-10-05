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
        failures += Report("PerspectiveGridSampler.SampleVector128", QRSamplerMismatches);
        failures += Report("MicroQRImageDecoder.SampleGridVector128", MicroSamplerMismatches);
        failures += Report("RmQRImageDecoder.SampleGridSimd128", RmQRSamplerMismatches);
        failures += Report("RmQRImageDecoder.ClassifySubFinderLatticeVector128", LatticeMismatches);
        failures += Report("FinderPatternFinder edge-list kernel", FinderEdgeListMismatches);
        failures += Report("RmQRModulePlacer", RmQRPlacerMismatches);
        failures += Report("ModuleBitPacker", BitPackerMismatches);
        failures += Report("TextAnalyzer", TextAnalyzerMismatches);
        failures += Report("Binarizer histogram", HistogramMismatches);
        failures += Report("LuminanceConverter", LuminanceMismatches);
        failures += Report("QRImageDecoder.SampleGridPiecewise", PiecewiseMismatches);
        failures += Report("ModulePlacer.MaskCode", MaskCodeMismatches);
        failures += Report("ModulePlacer.ApplyMaskPattern", MaskApplyMismatches);
        failures += Report("QRBinaryEncoder payload writers", PayloadWriterMismatches);
        failures += Report("ModeSegmenter.ComputeCostsLanes", SegmenterLaneMismatches);
        failures += Report("StructuredAppendPlanner.WalkLanes", WalkLaneMismatches);
        failures += Report("EccBinaryDecoder.ComputeSyndromesVector128", SyndromeMismatches);
        failures += Report("EccBinaryEncoder.PackedSimdKernel", EccEncoderMismatches);
        failures += Report("RmQRMatrixDecoder pair planes, 128-bit", ExtractMismatches);
        failures += Report("RmQRBinaryEncoder value writers", ValueWriterMismatches);
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
                PerspectiveGridSampler.SampleScalar(luminance, width, height, threshold, transform, dimension, scalar);
                PerspectiveGridSampler.SampleVector128(luminance, width, height, threshold, transform, dimension, vector);
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
    /// The text analysis through the dispatch and through the 128-bit tier's own entry against the scalar pass: each boundary char,
    /// U+0130 among them (a narrowing that dropped the high byte would read it as '0'), at every position of every length from 1 to 40,
    /// among digits and alphanumerics. The dispatch takes the tier on WebAssembly from 32 chars only, and the tier from 8.
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
                        var expected = TextAnalyzer.AnalyzeScalar(text, eci);
                        if (TextAnalyzer.Analyze(text, eci) != expected)
                            mismatches.Add($"U+{(int)c:X4} at {position} of {length}, {eci}");
                        if (length >= 8 && TextAnalyzer.AnalyzeVector128(text, eci) != expected)
                            mismatches.Add($"128-bit tier, U+{(int)c:X4} at {position} of {length}, {eci}");
                    }
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The histogram through the dispatch against a per-pixel count: noise, two-valued runs of every length the blocks meet (a rendered
    /// symbol), runs with a grey pixel in them, a gradient, and lengths around the 32-pixel block and the untested stretch.
    /// </summary>
    private static List<string> HistogramMismatches()
    {
        var mismatches = new List<string>();
        var random = new Random(23);
        var histogram = new int[Binarizer.HistogramBins];
        foreach (var length in new[] { 0, 1, 31, 32, 33, 63, 64, 65, 1023, 1024, 1056, 4097, 70_000 })
        {
            for (var kind = 0; kind < 4; kind++)
            {
                var pixels = new byte[length];
                for (var i = 0; i < length;)
                {
                    var run = kind == 0 ? 1 : random.Next(1, 40);
                    var value = kind switch
                    {
                        0 => (byte)random.Next(256),
                        1 => random.Next(2) == 0 ? (byte)0 : (byte)255,
                        2 => random.Next(9) == 0 ? (byte)random.Next(256) : random.Next(2) == 0 ? (byte)0 : (byte)255,
                        _ => (byte)(i * 256 / Math.Max(length, 1)),
                    };
                    for (var k = 0; k < run && i < length; k++, i++)
                        pixels[i] = value;
                }
                var expected = new int[Binarizer.HistogramBins];
                foreach (var value in pixels)
                    expected[value]++;
                Binarizer.FillHistogram(pixels, histogram);
                if (!histogram.AsSpan().SequenceEqual(expected))
                    mismatches.Add($"length {length}, kind {kind}");
            }
        }
        return mismatches;
    }

    /// <summary>
    /// Luminance through the dispatch and through the 128-bit tier's own entry against the scalar tier, each layout straight and premultiplied:
    /// every channel value against every alpha (a premultiplied channel above its alpha wraps in the scalar byte cast), then random scenes whose
    /// rows take the optimistic, classified and composite-only modes, across the 16-pixel block and its overlapping tail, packed and padded.
    /// The dispatch hides the 128-bit tier behind the AVX2 and dot-product tiers, and every ARM64 runner has the dot product.
    /// </summary>
    private static List<string> LuminanceMismatches()
    {
        var mismatches = new List<string>();
        (int R, int G, int B, int A, PixelLayout Layout)[] layouts = [(2, 1, 0, 3, PixelLayout.Bgra8888), (0, 1, 2, 3, PixelLayout.Rgba8888), (0, 1, 2, -1, PixelLayout.Rgb888x)];
        void Compare(byte[] pixels, int width, int height, int rowBytes, (int R, int G, int B, int A, PixelLayout Layout) layout, bool premultiplied, string label)
        {
            var expected = new byte[width * height];
            var actual = new byte[width * height];
            LuminanceConverter.ConvertRgbaForTest(pixels, expected, width, height, rowBytes, layout.R, layout.G, layout.B, layout.A, premultiplied, LuminanceConverter.ConvertTier.Scalar);
            LuminanceConverter.Convert(pixels, width, height, rowBytes, layout.Layout, premultiplied, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
                mismatches.Add($"{label}, {layout.Layout}, premultiplied {premultiplied}");
            // The tier takes rows of one block or more; its dispatch keeps narrower ones scalar
            if (width < 16)
                return;
            Array.Clear(actual);
            LuminanceConverter.ConvertRgbaVector128(pixels, actual, width, height, rowBytes, bgra: layout.R == 2, hasAlpha: layout.A >= 0, premultiplied);
            if (!expected.AsSpan().SequenceEqual(actual))
                mismatches.Add($"128-bit tier, {label}, {layout.Layout}, premultiplied {premultiplied}");
        }

        foreach (var layout in layouts)
        {
            var sweep = new byte[256 * 256 * 4];
            for (var a = 0; a < 256; a++)
            {
                for (var c = 0; c < 256; c++)
                {
                    var p = (a * 256 + c) * 4;
                    sweep[p + layout.R] = (byte)c;
                    sweep[p + layout.G] = (byte)(255 - c);
                    sweep[p + layout.B] = (byte)(c ^ 0x55);
                    sweep[p + (layout.A < 0 ? 3 : layout.A)] = (byte)a;
                }
            }
            foreach (var premultiplied in layout.A < 0 ? new[] { false } : [false, true])
                Compare(sweep, 256, 256, 1024, layout, premultiplied, "sweep");
        }

        var random = new Random(29);
        foreach (var layout in layouts)
        {
            foreach (var width in new[] { 15, 16, 17, 31, 32, 33, 48, 100, 139 })
            {
                foreach (var pad in new[] { 0, 12 })
                {
                    const int Height = 9;
                    var rowBytes = width * 4 + pad;
                    var pixels = new byte[rowBytes * Height];
                    random.NextBytes(pixels);
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var alpha = y switch
                            {
                                < 2 or 6 => 255,
                                2 or 3 or 7 => random.Next(4) == 0 ? 0 : 255,
                                4 or 8 => random.Next(4) == 0 ? random.Next(256) : 255,
                                _ => random.Next(2) * 255,
                            };
                            pixels[y * rowBytes + x * 4 + (layout.A < 0 ? 3 : layout.A)] = (byte)alpha;
                        }
                    }
                    foreach (var premultiplied in layout.A < 0 ? new[] { false } : [false, true])
                        Compare(pixels, width, Height, rowBytes, layout, premultiplied, $"width {width}, pad {pad}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The mesh sampler through the dispatch against the per-module reference: Annex E lattices upright and bent by rotation and keystone,
    /// symbols pushed past an image edge, and nodes no detector would produce (NaN, infinities, magnitudes past the int range), where each
    /// runtime's cast differs and the reference's clamp does not.
    /// </summary>
    private static List<string> PiecewiseMismatches()
    {
        var mismatches = new List<string>();
        float[] poisons = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e12f, -1e12f, 2147483648f, -2147483904f];
        for (var version = 7; version <= 40; version += 3)
        {
            foreach (var (bent, shift) in new[] { (false, 0f), (true, 0f), (true, 6f), (true, -6f) })
            {
                for (var poison = -1; poison < poisons.Length; poison++)
                {
                    var (luminance, width, height, gridCoords, nodeXs, nodeYs) = MeshScene(version, bent, shift, version * 13 + poison);
                    var meshSize = gridCoords.Length;
                    if (poison >= 0)
                        (poison % 2 == 0 ? nodeXs : nodeYs)[(version + poison) % (meshSize * meshSize)] = poisons[poison];
                    var dimension = 17 + 4 * version;
                    var expected = new byte[dimension * dimension];
                    var actual = new byte[dimension * dimension];
                    QRImageDecoder.SampleGridPiecewiseScalar(luminance, width, height, 128, gridCoords, nodeXs, nodeYs, meshSize, dimension, expected);
                    QRImageDecoder.SampleGridPiecewise(luminance, width, height, 128, gridCoords, nodeXs, nodeYs, meshSize, dimension, actual);
                    if (!expected.AsSpan().SequenceEqual(actual))
                        mismatches.Add($"version {version}, bent {bent}, shift {shift}, poison {(poison < 0 ? "none" : poisons[poison].ToString())}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>A version's alignment lattice as mesh nodes at 3 px a module, rotated 17 degrees with a keystone when bent, over random blocks.</summary>
    private static (byte[] Luminance, int Width, int Height, float[] GridCoords, float[] NodeXs, float[] NodeYs) MeshScene(int version, bool bent, float shiftModules, int seed)
    {
        const float PixelsPerModule = 3f;
        var dimension = 17 + 4 * version;
        var gridCoords = new List<float>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
        {
            if (value != 0)
                gridCoords.Add(value + 0.5f);
        }
        var meshSize = gridCoords.Count;
        var random = new Random(seed);
        var angle = bent ? 17.0 * Math.PI / 180.0 : 0.0;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var keystone = bent ? 0.00035 : 0.0;
        var side = (int)Math.Ceiling((dimension + 8) * PixelsPerModule * (Math.Abs(cos) + Math.Abs(sin)));
        var centre = side / 2.0 - shiftModules * PixelsPerModule;
        var nodeXs = new float[meshSize * meshSize];
        var nodeYs = new float[meshSize * meshSize];
        for (var j = 0; j < meshSize; j++)
        {
            for (var i = 0; i < meshSize; i++)
            {
                var gx = (gridCoords[i] - dimension / 2.0) * PixelsPerModule;
                var gy = (gridCoords[j] - dimension / 2.0) * PixelsPerModule;
                var w = 1.0 + keystone * gx + keystone * 0.5 * gy;
                nodeXs[j * meshSize + i] = (float)(centre + (gx * cos - gy * sin) / w + (random.NextDouble() - 0.5) * 0.6);
                nodeYs[j * meshSize + i] = (float)(centre + (gx * sin + gy * cos) / w + (random.NextDouble() - 0.5) * 0.6);
            }
        }
        // Not square, so a clamp against the wrong side's limit shows
        var height = side + 37;
        var luminance = new byte[side * height];
        random.NextBytes(luminance);
        return (luminance, side, height, gridCoords.ToArray(), nodeXs, nodeYs);
    }

    /// <summary>
    /// Mask selection through the dispatch, and the 128-bit transposed tier entered directly, against the scalar bit-packed kernels: every
    /// version, each ECC level, random data and all-dark and all-light data (long runs, uniform blocks, an extreme balance). Same pattern,
    /// same matrix.
    /// </summary>
    private static List<string> MaskCodeMismatches()
    {
        var mismatches = new List<string>();
        for (var version = 1; version <= 40; version++)
        {
            var layout = ModulePlacer.GetLayout(version);
            var size = layout.Size;
            for (var fill = -1; fill < 6; fill++)
            {
                var buffer = new byte[size * size];
                layout.Template.AsSpan().CopyTo(buffer);
                var codewords = new byte[layout.FreeModules / 8];
                if (fill >= 4)
                    codewords.AsSpan().Fill(fill == 4 ? (byte)0 : (byte)0xFF);
                else
                    new Random(version * 7 + fill).NextBytes(codewords);
                ModulePlacer.PlaceDataWords(buffer, layout, codewords);
                foreach (var eccLevel in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                {
                    var expected = (byte[])buffer.Clone();
                    var expectedBest = size <= 64
                        ? ModulePlacer.MaskCode64(expected, size, version, layout.BlockedMask, eccLevel)
                        : ModulePlacer.MaskCode192(expected, size, version, layout.BlockedMask, eccLevel);
                    var actual = (byte[])buffer.Clone();
                    var actualBest = ModulePlacer.MaskCode(actual, size, version, layout.BlockedMask, eccLevel);
                    if (actualBest != expectedBest || !expected.AsSpan().SequenceEqual(actual))
                        mismatches.Add($"version {version}, data {(fill < 4 ? "random" : fill == 4 ? "light" : "dark")}, {eccLevel}: pattern {actualBest}, expected {expectedBest}");
                    if (size <= 64)
                        continue;
                    var transposed = (byte[])buffer.Clone();
                    var transposedBest = ModulePlacer.MaskCodeTransposedVector128(transposed, size, version, layout.BlockedMask, eccLevel);
                    if (transposedBest != expectedBest || !expected.AsSpan().SequenceEqual(transposed))
                        mismatches.Add($"version {version}, data {(fill < 4 ? "random" : fill == 4 ? "light" : "dark")}, {eccLevel}, 128-bit transposed: pattern {transposedBest}, expected {expectedBest}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The Alphanumeric and Numeric payload writers, every tier this build runs entered directly, against the writers they replaced
    /// (<see cref="TierTiming.OldAlphanumeric"/>, <see cref="TierTiming.OldNumeric"/>): lengths 0 to 300 and long runs, after 0 to 30 bits
    /// of a pattern (every third, every seventh past 300 characters), the whole buffer and the bit position, each run alone and as a slice of a longer text whose next characters are in
    /// the alphabet (as a plan passes a segment, so a step that reads past the run writes it); for Alphanumeric also every character
    /// outside the alphabet up to 0xFF, and some past it, in the lanes of the vector steps, which must throw after the same bits. The old
    /// writers read CharacterSets' value table too, so the table is held to the alphabet itself. The one run of the WebAssembly tier.
    /// </summary>
    private static List<string> PayloadWriterMismatches()
    {
        var mismatches = new List<string>();
        var alphanumeric = new List<(string Name, TierTiming.PayloadWriter Write)> { ("scalar", QRBinaryEncoder.WriteAlphanumericScalar) };
        var numeric = new List<(string Name, TierTiming.PayloadWriter Write)> { ("scalar", QRBinaryEncoder.WriteNumericScalar) };
        // each as its dispatch asks: the Alphanumeric step blends with SSE4.1, the Numeric step needs SSSE3 alone
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported && System.Runtime.Intrinsics.X86.Sse41.IsSupported)
            alphanumeric.Add(("ssse3", QRBinaryEncoder.WriteAlphanumericSsse3));
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
            numeric.Add(("ssse3", QRBinaryEncoder.WriteNumericSsse3));
        if (System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported)
        {
            alphanumeric.Add(("wasm", QRBinaryEncoder.WriteAlphanumericPackedSimd));
        }

        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        var values = CharacterSets.AlphanumericValues;
        for (var c = 0; c < values.Length; c++)
        {
            if (values[c] != alphabet.IndexOf((char)c))
                mismatches.Add($"alphanumeric value table: U+{c:X4} holds {values[c]}, the alphabet gives {alphabet.IndexOf((char)c)}");
        }

        var lengths = Enumerable.Range(0, 301).Concat([511, 512, 513, 4296, 7089]);
        foreach (var length in lengths)
        {
            var random = new Random(length);
            var text = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var digits = new string(Enumerable.Range(0, length).Select(_ => (char)('0' + random.Next(10))).ToArray());
            var textInLonger = text + "ABCDEFGHIJKLMNOP";
            var digitsInLonger = digits + "0123456789012345";
            for (var align = 0; align < 32; align += length > 300 ? 7 : 3)
            {
                foreach (var (name, write) in alphanumeric)
                {
                    if (!SameRun(text, align, TierTiming.OldAlphanumeric, write))
                        mismatches.Add($"alphanumeric {name}: length {length}, align {align}");
                    if (!SameRun(textInLonger.AsSpan(0, length), align, TierTiming.OldAlphanumeric, write))
                        mismatches.Add($"alphanumeric {name}: length {length} as a slice, align {align}");
                }
                foreach (var (name, write) in numeric)
                {
                    if (!SameRun(digits, align, TierTiming.OldNumeric, write))
                        mismatches.Add($"numeric {name}: length {length}, align {align}");
                    if (!SameRun(digitsInLonger.AsSpan(0, length), align, TierTiming.OldNumeric, write))
                        mismatches.Add($"numeric {name}: length {length} as a slice, align {align}");
                }
            }
        }

        // every character outside the alphabet up to 0xFF, and some past it whose low byte is in it, in the lanes of the vector steps
        var outside = Enumerable.Range(0, 256).Select(c => (char)c).Where(c => alphabet.IndexOf(c) < 0)
            .Concat(['Ā', 'Ł', 'İ', '翿', '聁', 'Ａ', '￿']);
        foreach (var bad in outside)
        {
            foreach (var (length, position) in new[] { (1, 0), (9, 0), (9, 7), (9, 8), (17, 0), (17, 9), (17, 15), (17, 16), (33, 31) })
            {
                var random = new Random(length * 41 + position + bad);
                var chars = Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
                chars[position] = bad;
                foreach (var (name, write) in alphanumeric)
                {
                    if (!SameRun(new string(chars), 5, TierTiming.OldAlphanumeric, write))
                        mismatches.Add($"alphanumeric {name}: U+{(int)bad:X4} at {position} of {length}");
                }
            }
        }
        return mismatches;

        static bool SameRun(ReadOnlySpan<char> text, int align, TierTiming.PayloadWriter expected, TierTiming.PayloadWriter actual)
        {
            var a = Run(text, align, expected);
            var b = Run(text, align, actual);
            return a.Bits == b.Bits && a.Error == b.Error && a.Buffer.AsSpan().SequenceEqual(b.Buffer);
        }

        static (byte[] Buffer, int Bits, string? Error) Run(ReadOnlySpan<char> text, int align, TierTiming.PayloadWriter write)
        {
            var buffer = new byte[(text.Length * 6 + 64) / 8 + 16];
            var writer = new FeatherQR.Internals.BinaryEncoders.BitWriter(buffer);
            if (align > 0)
                writer.Write(unchecked((int)0xA5C3_9E71) >> (32 - align), align);
            string? error = null;
            try
            {
                write(ref writer, text);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
            }
            var bits = writer.BitPosition;
            writer.Flush();
            return (buffer, bits, error);
        }
    }

    /// <summary>
    /// A pinned mask through the dispatch, the route this build takes, against the decoder's mask predicate applied module by module
    /// to the unblocked modules: every version, every pattern, on placed random data.
    /// </summary>
    private static List<string> MaskApplyMismatches()
    {
        var mismatches = new List<string>();
        for (var version = 1; version <= 40; version++)
        {
            var layout = ModulePlacer.GetLayout(version);
            var size = layout.Size;
            var buffer = layout.Template.ToArray();
            var codewords = new byte[layout.FreeModules / 8];
            new Random(version * 11).NextBytes(codewords);
            ModulePlacer.PlaceDataWords(buffer, layout, codewords);
            for (var pattern = 0; pattern < 8; pattern++)
            {
                var expected = (byte[])buffer.Clone();
                for (var index = 0; index < expected.Length; index++)
                {
                    if ((layout.BlockedMask[index >> 3] & (1 << (index & 7))) == 0 && QRMatrixDecoder.GetMaskBit(pattern, index / size, index % size))
                        expected[index] ^= 1;
                }
                var actual = (byte[])buffer.Clone();
                ModulePlacer.ApplyMaskPattern(actual, size, layout.BlockedMask, pattern);
                if (!expected.AsSpan().SequenceEqual(actual))
                    mismatches.Add($"version {version}, pattern {pattern}");
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The mixed-mode program on several pieces at once through the dispatch against the per-piece program: the cost, the final state and
    /// the walk back of every lane, on digits, alphanumerics, Latin-1, wider characters, byte order marks and lone surrogates, in groups the
    /// four-lane form runs and in groups uneven enough to go to the scalar loops.
    /// </summary>
    private static List<string> SegmenterLaneMismatches()
    {
        var mismatches = new List<string>();
        if (!ModeSegmenter.LanesAccelerated)
            return mismatches;
        var random = new Random(20260930);
        const string alphabet = "0123456789AZ $xéλあ﻿𐀀";
        int[][] shapes = [[40, 41, 39, 44], [120, 5, 7, 9], [64, 64, 64, 64, 64, 64, 64, 64], [300, 250, 200, 150, 100, 50, 25], [1, 1, 1], [900, 880, 870, 860, 3]];
        foreach (var lengths in shapes)
        {
            foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
            {
                var chunks = lengths.Select(length => new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray())).ToArray();
                var text = string.Concat(chunks);
                var starts = new int[chunks.Length];
                for (var lane = 1; lane < chunks.Length; lane++)
                    starts[lane] = starts[lane - 1] + lengths[lane - 1];
                var table = new byte[lengths.Max() * ModeSegmenter.LaneTableBytesPerChar];
                var costs = new int[chunks.Length];
                var states = new int[chunks.Length];
                ModeSegmenter.ComputeCostsLanes(text, starts, lengths, charset, 4, 14, 13, 16, table, costs, states);
                for (var lane = 0; lane < chunks.Length; lane++)
                {
                    var parents = new byte[lengths[lane] * ModeSegmenter.ParentBytesPerChar];
                    var cost = ModeSegmenter.ComputeCosts(chunks[lane], charset, 4, 14, 13, 16, parents, out var state);
                    var expected = new ModeSegment[lengths[lane]];
                    var actual = new ModeSegment[lengths[lane]];
                    var built = ModeSegmenter.Reconstruct(chunks[lane], parents, state, expected, out var expectedCount);
                    var laneBuilt = ModeSegmenter.ReconstructLane(lengths[lane], table, lane, states[lane], actual, out var actualCount);
                    var same = cost == costs[lane] && state == states[lane] && built == laneBuilt && expectedCount == actualCount;
                    for (var k = 0; same && k < expectedCount; k++)
                        same = expected[k].Start == actual[k].Start && expected[k].Length == actual[k].Length && expected[k].ModeIndex == actual[k].ModeIndex;
                    if (!same)
                        mismatches.Add($"lengths {string.Join(',', lengths)}, {charset}, lane {lane}: cost {costs[lane]}, expected {cost}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The planner's walk at up to eight budgets through the dispatch against the scalar walk at each: chunk counts and ends, on runs of
    /// digits, alphanumerics, words, wider characters, pairs, lone surrogates and byte order marks, one-byte and UTF-8.
    /// </summary>
    private static List<string> WalkLaneMismatches()
    {
        var mismatches = new List<string>();
        var runs = new[] { "0123456789", "ABC $%*+-./:", "order", "é", "🎉", "�", "�", "x﻿﻿" };
        var expected = new int[StructuredAppendPlanner.MaxSymbols];
        var actual = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        foreach (var seed in new[] { 7, 20260919 })
        {
            var random = new Random(seed);
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 700; i++)
                builder.Append(runs[random.Next(runs.Length)]);
            var text = builder.ToString();
            foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
            {
                var analysis = TextAnalyzer.Analyze(text, charset);
                foreach (var version in new[] { 1, 10, 27, 40 })
                {
                    foreach (var used in new[] { 1, 3, 8 })
                    {
                        var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
                        var budgets = Enumerable.Range(0, used).Select(i => capacity - 3 * i).ToArray();
                        var walked = StructuredAppendPlanner.WalkLanes(text, analysis.EciMode, version, budgets, StructuredAppendPlanner.MaxSymbols, 0, 0, counts, actual, out _);
                        for (var lane = 0; lane < used; lane++)
                        {
                            var count = StructuredAppendPlanner.CountChunks(text, analysis.EciMode, false, QRSegmentation.Optimal, version, budgets[lane], StructuredAppendPlanner.MaxSymbols, expected);
                            var same = walked ? counts[lane] == count : count == int.MaxValue;
                            for (var k = 0; same && walked && k < Math.Min(count, StructuredAppendPlanner.MaxSymbols); k++)
                                same = actual[lane * StructuredAppendPlanner.MaxSymbols + k] == expected[k];
                            if (!same)
                                mismatches.Add($"seed {seed}, {analysis.EciMode}, v{version}, {used} budgets, lane {lane}: {(walked ? counts[lane] : -1)} chunks, expected {count}");
                        }
                    }
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The 128-bit syndrome pass against the scalar one: random blocks, then every ECC count over lengths across the eight-byte step, its
    /// four-byte remainder and its tail, with degenerate fills. The has-error flag too: a lane past eccCount is non-zero on a clean block.
    /// </summary>
    private static List<string> SyndromeMismatches()
    {
        var mismatches = new List<string>();
        if (!Vector128.IsHardwareAccelerated)
            return mismatches;
        var random = new Random(20261001);
        var cases = new List<byte[]>();
        for (var round = 0; round < 200; round++)
        {
            var codeword = new byte[random.Next(8, 256)];
            if (round % 10 != 0)
                random.NextBytes(codeword);
            cases.Add(codeword);
        }
        for (var length = 2; length <= 40; length++)
        {
            foreach (var fill in new byte[] { 0x00, 0xFF, 0x80 })
                cases.Add(Enumerable.Repeat(fill, length).ToArray());
        }
        var expected = new byte[FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.SyndromeLanes];
        var actual = new byte[FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.SyndromeLanes];
        foreach (var codeword in cases)
        {
            for (var eccCount = 1; eccCount <= Math.Min(30, codeword.Length - 1); eccCount++)
            {
                var expectedError = FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, expected);
                var actualError = FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.ComputeSyndromesVector128(codeword, eccCount, actual);
                if (expectedError != actualError || !expected.AsSpan(0, eccCount).SequenceEqual(actual.AsSpan(0, eccCount)))
                    mismatches.Add($"length {codeword.Length}, eccCount {eccCount}");
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The WebAssembly Reed-Solomon kernel, and its entry with the size gate, against the scalar one: every ECC count to 32 (one
    /// register and two) over lengths across the four-byte step and its tail, random data and degenerate fills.
    /// </summary>
    private static List<string> EccEncoderMismatches()
    {
        var mismatches = new List<string>();
        if (!System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported)
            return mismatches;
        var random = new Random(20261001);
        var expected = new byte[32];
        var actual = new byte[32];
        for (var length = 0; length <= 160; length += length < 24 ? 1 : 17)
        {
            for (var kind = 0; kind < 4; kind++)
            {
                var data = new byte[length];
                if (kind == 0)
                    random.NextBytes(data);
                else
                    Array.Fill(data, kind == 1 ? (byte)0xFF : kind == 2 ? (byte)0x80 : (byte)0x00);
                for (var eccCount = 1; eccCount <= 32; eccCount++)
                {
                    FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.CalculateEccScalar(data, expected, eccCount);
                    FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.PackedSimdKernel(data, actual, eccCount);
                    if (!expected.AsSpan(0, eccCount).SequenceEqual(actual.AsSpan(0, eccCount)))
                        mismatches.Add($"length {length}, eccCount {eccCount}, {(kind == 0 ? "random" : "fill " + data.FirstOrDefault())}");
                    FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.CalculateEccPackedSimd(data, actual, eccCount);
                    if (!expected.AsSpan(0, eccCount).SequenceEqual(actual.AsSpan(0, eccCount)))
                        mismatches.Add($"entry, length {length}, eccCount {eccCount}, {(kind == 0 ? "random" : "fill " + data.FirstOrDefault())}");
                }
            }
        }
        return mismatches;
    }

    /// <summary>
    /// The portable pair planes (pinned, so every version runs them) and the dispatch against the scalar walk, every version, over
    /// random grids with dark modules 1 or 2, all light and all dark.
    /// </summary>
    private static List<string> ExtractMismatches()
    {
        var mismatches = new List<string>();
        if (!RmQRMatrixDecoder.IsPairPlaneVector128TierSupported)
            return mismatches;
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var width = RmQRConstants.GetWidth(version);
            var height = RmQRConstants.GetHeight(version);
            var total = RmQRConstants.GetTotalCodewordCount(version);
            for (var kind = 0; kind < 4; kind++)
            {
                var modules = new byte[width * height];
                if (kind < 2)
                {
                    var random = new Random((int)version * 13 + kind);
                    for (var i = 0; i < modules.Length; i++)
                        modules[i] = (byte)(random.Next(2) == 0 ? 0 : kind + 1);
                }
                else if (kind == 3)
                {
                    Array.Fill(modules, (byte)0xFF);
                }
                var expected = new byte[total];
                RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, expected, RmQRMatrixDecoder.ExtractKernel.Scalar);
                foreach (var kernel in new[] { RmQRMatrixDecoder.ExtractKernel.PairPlanesVector128, RmQRMatrixDecoder.ExtractKernel.Auto })
                {
                    var actual = new byte[total];
                    Array.Fill(actual, (byte)0xA5);
                    RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, actual, kernel);
                    if (!expected.AsSpan().SequenceEqual(actual))
                        mismatches.Add($"{version}, {kernel}, grid {kind}");
                }
            }
        }
        return mismatches;
    }

    private delegate void ValueWriter(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> text, bool vectorized);

    /// <summary>
    /// The rMQR Numeric and Alphanumeric writers, the tier this build takes against the SWAR and table loops, from every pending-bit
    /// count a header leaves: every length to the R17x139 capacities (zeros, nines or colons, the alphabet in turn, random), and each
    /// alphanumeric symbol in each of 16 positions. The streams compared are the stored bytes followed by the pending bits.
    /// </summary>
    private static List<string> ValueWriterMismatches()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        var mismatches = new List<string>();
        var random = new Random(20261001);
        string RandomText(string set, int length) => string.Create(length, set, (span, s) => { for (var i = 0; i < span.Length; i++) span[i] = s[random.Next(s.Length)]; });
        string CyclicText(string set, int length) => string.Create(length, set, (span, s) => { for (var i = 0; i < span.Length; i++) span[i] = s[(i + length) % s.Length]; });
        void Check(string name, ValueWriter writer, string text)
        {
            for (var header = 0; header <= 12; header++)
            {
                if (!Stream(writer, text, header, vectorized: true).AsSpan().SequenceEqual(Stream(writer, text, header, vectorized: false)))
                    mismatches.Add($"{name}, length {text.Length}, header {header}");
            }
        }
        for (var length = 0; length <= 361; length++)
        {
            Check("numeric zeros", RmQRBinaryEncoder.WriteNumeric, new string('0', length));
            Check("numeric nines", RmQRBinaryEncoder.WriteNumeric, new string('9', length));
            Check("numeric random", RmQRBinaryEncoder.WriteNumeric, RandomText("0123456789", length));
        }
        for (var length = 0; length <= 219; length++)
        {
            Check("alphanumeric colons", RmQRBinaryEncoder.WriteAlphanumeric, new string(':', length));
            Check("alphanumeric cyclic", RmQRBinaryEncoder.WriteAlphanumeric, CyclicText(alphabet, length));
            Check("alphanumeric random", RmQRBinaryEncoder.WriteAlphanumeric, RandomText(alphabet, length));
        }
        foreach (var symbol in alphabet)
        {
            for (var position = 0; position < 16; position++)
            {
                var chars = new string('A', 16).ToCharArray();
                chars[position] = symbol;
                Check($"alphanumeric '{symbol}' at {position}", RmQRBinaryEncoder.WriteAlphanumeric, new string(chars));
            }
        }
        return mismatches;
    }

    /// <summary>The writer's stream from <paramref name="header"/> set bits: stored bytes, then the pending bits, as whole bytes and the bit count.</summary>
    private static byte[] Stream(ValueWriter writer, string text, int header, bool vectorized)
    {
        var stored = new byte[512];
        var acc = header == 0 ? 0UL : ulong.MaxValue << (64 - header);
        var accBits = header;
        var bytePos = 0;
        writer(ref stored[0], ref acc, ref accBits, ref bytePos, text, vectorized);
        var stream = new byte[bytePos + 8 + 4];
        stored.AsSpan(0, bytePos).CopyTo(stream);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(stream.AsSpan(bytePos), acc);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(stream.AsSpan(bytePos + 8), bytePos * 8 + accBits);
        return stream.AsSpan(0, bytePos + (accBits + 7) / 8).ToArray().Concat(stream.AsSpan(bytePos + 8).ToArray()).ToArray();
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
