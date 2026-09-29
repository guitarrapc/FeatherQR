using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using FeatherQR;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using FeatherQR.Tests;

/// <summary>
/// The opt-in timing mode (<c>--time</c>): the benchmark shapes timed end to end on the build itself, for the builds
/// BenchmarkDotNet does not run (a default NativeAOT publish, WebAssembly interpreted and AOT-compiled). Shared by the
/// NativeAOT gate and the WebAssembly report (tests/FeatherQR.WasmReport links this file), so every build times the same inputs.
/// </summary>
/// <remarks>
/// Images are drawn by the test suite's renderers, since the WebAssembly report has no SkiaSharp: the image shapes follow the
/// benchmark's in kind, not in pixels. Rounds run the shapes interleaved, so drift during a run spreads over all of them.
/// <c>--loop</c> runs one shape for a fixed time instead, for a sampling profiler.
/// </remarks>
internal static class TierTiming
{
    private sealed record Shape(string Name, Func<Func<int>> Build, bool Available = true);

    private static int s_sink;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (Array.IndexOf(args, "--time") < 0)
            return false;

        var filters = Option(args, "--shape")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var rounds = int.Parse(Option(args, "--rounds") ?? "11");
        var batchMs = double.Parse(Option(args, "--batch-ms") ?? "20", System.Globalization.CultureInfo.InvariantCulture);
        var warmupMs = double.Parse(Option(args, "--warmup-ms") ?? "300", System.Globalization.CultureInfo.InvariantCulture);
        var loop = Option(args, "--loop");
        var shapes = Shapes();

        if (Array.IndexOf(args, "--list") >= 0)
        {
            foreach (var shape in shapes)
                Console.WriteLine(shape.Name);
            return true;
        }

        PrintBuild(Option(args, "--label"));
        if (loop is not null)
        {
            var seconds = double.Parse(Option(args, "--seconds") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
            var shape = Array.Find(shapes, s => s.Name == loop);
            if (shape is null)
            {
                Console.Error.WriteLine($"No shape named {loop}.");
                exitCode = 2;
                return true;
            }
            var run = shape.Build();
            var calls = 0L;
            var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end)
            {
                s_sink += run();
                calls++;
            }
            Console.WriteLine($"loop\t{loop}\t{calls}\t{seconds * 1e6 / calls:F2}us\tsink {s_sink}");
            return true;
        }

        var selected = shapes.Where(s => s.Available && (filters.Length == 0 || filters.Any(f => s.Name.Contains(f, StringComparison.Ordinal)))).ToArray();
        var runs = new Func<int>[selected.Length];
        for (var i = 0; i < selected.Length; i++)
            runs[i] = selected[i].Build();

        var batch = new int[selected.Length];
        for (var i = 0; i < selected.Length; i++)
        {
            var calls = 0;
            var start = Stopwatch.GetTimestamp();
            while (calls < 3 || Elapsed(start) < warmupMs * 1e6)
            {
                s_sink += runs[i]();
                calls++;
            }
            batch[i] = Math.Max(1, (int)(batchMs * 1e6 / (Elapsed(start) / calls)));
        }

        var samples = new double[selected.Length][];
        for (var i = 0; i < selected.Length; i++)
            samples[i] = new double[rounds];
        for (var round = 0; round < rounds; round++)
        {
            for (var i = 0; i < selected.Length; i++)
            {
                var run = runs[i];
                var n = batch[i];
                var start = Stopwatch.GetTimestamp();
                for (var k = 0; k < n; k++)
                    s_sink += run();
                samples[i][round] = Elapsed(start) / n;
            }
        }

        Console.WriteLine("shape\tmedian_us\tmin_us\tmax_us\tcalls_per_batch");
        for (var i = 0; i < selected.Length; i++)
        {
            var sorted = samples[i].Order().ToArray();
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{selected[i].Name}\t{sorted[sorted.Length / 2] / 1e3:F3}\t{sorted[0] / 1e3:F3}\t{sorted[^1] / 1e3:F3}\t{batch[i]}"));
        }
        Console.WriteLine($"# sink {s_sink}");
        return true;
    }

    private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * (1e9 / Stopwatch.Frequency);

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintBuild(string? label)
    {
        Console.WriteLine($"# label {label ?? "-"}");
        Console.WriteLine($"# runtime {RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier} {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"# vectors 128 {Vector128.IsHardwareAccelerated}, 256 {Vector256.IsHardwareAccelerated}");
        Console.WriteLine($"# tiers {string.Join(' ', SimdTiers.Report().Select(k => $"{k.Name}={k.Active}"))}");
    }

    // ---- Shapes: the benchmark project's payloads and symbol choices ----

    private const string Url = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy";
    private const string ShortUrl = "https://github.com/guitarrapc/FeatherQR";
    private static readonly string RmQRByte = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4))[..150];

    private static Shape[] Shapes() =>
    [
        new("encode/qr-v1-num-L", () => QREncode("0123456789", QREccLevel.L)),
        new("encode/qr-v1-alnum-M", () => QREncode("HELLO WORLD 2026", QREccLevel.M)),
        new("encode/qr-v6-url-M", () => QREncode(Url, QREccLevel.M)),
        new("encode/qr-v20-byte-M", () => QREncode(DeterministicText(620), QREccLevel.M)),
        new("encode/qr-v40-byte-L", () => QREncode(DeterministicText(2900), QREccLevel.L)),
        new("encode/qr-v40-byte-H", () => QREncode(DeterministicText(1200), QREccLevel.H)),
        new("encode/micro-m2-num", () => MicroEncode("0123456789", MicroQREccLevel.L)),
        new("encode/micro-m3-alnum", () => MicroEncode("HELLO WORLD 14", MicroQREccLevel.L)),
        new("encode/micro-m4-byte", () => MicroEncode("bytes m4 mode", MicroQREccLevel.M)),
        new("encode/rmqr-r7x43-num", () => RmQREncode("012345678901", RmQRVersion.R7x43)),
        new("encode/rmqr-r11x59-alnum", () => RmQREncode("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", RmQRVersion.R11x59)),
        new("encode/rmqr-r17x139-byte", () => RmQREncode(RmQRByte, RmQRVersion.R17x139)),
        new("encode/sa-byte-45k-single", () => StructuredAppend(Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRSegmentation.Single)),
        new("encode/sa-mixed-40k-single", () => StructuredAppend(Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRSegmentation.Single)),
        new("encode/sa-mixed-40k-optimal", () => StructuredAppend(Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRSegmentation.Optimal)),
        new("encode/sa-utf8-15k-mixed-optimal", () => StructuredAppend(Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000), QRSegmentation.Optimal)),

        new("matrix/qr-v1-num-L", () => QRMatrix("0123456789", QREccLevel.L)),
        new("matrix/qr-v6-url-M", () => QRMatrix(Url, QREccLevel.M)),
        new("matrix/qr-v40-byte-L", () => QRMatrix(DeterministicText(2900), QREccLevel.L)),
        new("matrix/qr-v40-byte-H", () => QRMatrix(DeterministicText(1200), QREccLevel.H)),
        new("matrix/micro-m2-num", () => MicroMatrix("0123456789", MicroQREccLevel.L)),
        new("matrix/micro-m4-byte", () => MicroMatrix("bytes m4 mode", MicroQREccLevel.M)),
        new("matrix/rmqr-r7x43-num", () => RmQRMatrix("012345678901", RmQRVersion.R7x43, flips: 0, seed: 0)),
        new("matrix/rmqr-r17x139-byte", () => RmQRMatrix(RmQRByte, RmQRVersion.R17x139, flips: 0, seed: 0)),
        new("matrix/rmqr-r17x139-byte-corrected", () => RmQRMatrix(RmQRByte, RmQRVersion.R17x139, flips: 6, seed: 23)),
        new("matrix/sa-byte-45k-set", () => StructuredAppendDecode(Repeat("The quick brown fox jumps over the lazy dog. ", 45_000))),

        new("image/qr-v40-3px", () => QRImage(Large(), image => Crisp(image, 3f))),
        new("image/qr-v40-3.4px", () => QRImage(Large(), image => Crisp(image, 3.4f))),
        new("image/qr-v40-4px-rot17", () => QRImage(Large(), image => Supersampled(image, 4f, 17f, 0f))),
        new("image/qr-v40-4px-soft", () => QRImage(Large(), image => Soften(Crisp(image, 4f)))),
        new("image/qr-v25-4px-keystone15", () => QRImage(Medium(), image => Supersampled(image, 4f, 23f, 0.15f))),
        new("image/qr-v6-4px", () => QRImage(Small(), image => Crisp(image, 4f))),
        new("image/none-noise", () => NoSymbol(740, noise: true)),
        new("image/none-gradient", () => NoSymbol(740, noise: false)),
        new("image/micro-m4-8px", () => MicroImage()),
        new("image/rmqr-r7x43-8px", () => RmQRImage(RmQRCodeGenerator.Create("RMQR 43", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }), keystone: false)),
        new("image/rmqr-r17x139-8px", () => RmQRImage(RmQRLarge(), keystone: false)),
        new("image/rmqr-r17x139-4px-keystone15", () => RmQRImage(RmQRLarge(), keystone: true)),

        new("bitmap/qr-v40-3px", () => Bitmap(Crisp(Large(), 3f))),
        new("bitmap/rmqr-r17x139-8px", () => Bitmap(RmQRCrisp(RmQRLarge()))),

        // Kernels called once per decode, through their dispatch: their share of a decode on a build is their time over the decode's
        new("kernel/Binarizer-v40-3px", () => Histogram(Crisp(Large(), 3f))),
        new("kernel/Binarizer-v40-3.4px", () => Histogram(Crisp(Large(), 3.4f))),
        new("kernel/Binarizer-v40-4px-rot17", () => Histogram(Supersampled(Large(), 4f, 17f, 0f))),
        new("kernel/Binarizer-v40-4px-soft", () => Histogram(Soften(Crisp(Large(), 4f)))),
        new("kernel/Binarizer-noise", () => Histogram(Noise(740))),
        new("kernel/LuminanceConverter-v40-3px", () => Converter(Crisp(Large(), 3f))),
        new("kernel/LuminanceConverter-rmqr-r17x139-8px", () => Converter(RmQRCrisp(RmQRLarge()))),
        new("kernel/MaskCode-v1", () => MaskScoring(1, score: true)),
        new("kernel/MaskCode-v1-copy", () => MaskScoring(1, score: false)),
        new("kernel/MaskCode-v6", () => MaskScoring(6, score: true)),
        new("kernel/MaskCode-v20", () => MaskScoring(20, score: true)),
        new("kernel/MaskCode-v40", () => MaskScoring(40, score: true)),
        new("kernel/MaskCode-v40-copy", () => MaskScoring(40, score: false)),
        new("kernel/StructuredAppendParity-byte-45k", () => Parity(Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), EciMode.Iso8859_1)),
        new("kernel/StructuredAppendParity-utf8-15k", () => Parity(Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000), EciMode.Utf8)),

        // The kernels that already take a 128-bit tier on WebAssembly: each through its dispatch, and its scalar form
        new("kernel/LuminanceInverter", () => Inverter(scalar: false)),
        new("kernel/LuminanceInverter-scalar", () => Inverter(scalar: true)),
        new("kernel/LocalBinarizer", () => LocalBinarize(scalar: false)),
        new("kernel/LocalBinarizer-scalar", () => LocalBinarize(scalar: true)),
        new("kernel/FinderRowMask-v40", () => FinderRows(Crisp(Large(), 3f), FinderRowKernel.MaskWalk)),
        new("kernel/FinderRowMask-v40-scalar", () => FinderRows(Crisp(Large(), 3f), FinderRowKernel.Scalar)),
        new("kernel/FinderRowMask-noise", () => FinderRows(Noise(740), FinderRowKernel.MaskWalk)),
        new("kernel/FinderRowMask-noise-scalar", () => FinderRows(Noise(740), FinderRowKernel.Scalar)),
        new("kernel/AlignmentRowMask", () => Alignment(scalar: false)),
        new("kernel/AlignmentRowMask-scalar", () => Alignment(scalar: true)),
        new("kernel/QRSampleGrid", () => QRSample(scalar: false)),
        new("kernel/QRSampleGrid-scalar", () => QRSample(scalar: true)),
        new("kernel/MicroQRSampleGrid", () => MicroSample(scalar: false)),
        new("kernel/MicroQRSampleGrid-scalar", () => MicroSample(scalar: true)),
        new("kernel/RmQRSampleGrid", () => RmQRSample(scalar: false)),
        new("kernel/RmQRSampleGrid-scalar", () => RmQRSample(scalar: true)),
        new("kernel/RmQRSubFinderLattice", () => RmQRLattice(scalar: false)),
        new("kernel/RmQRSubFinderLattice-scalar", () => RmQRLattice(scalar: true)),
        new("kernel/RmQRLatin1Segment", () => Latin1(scalar: false)),
        new("kernel/RmQRLatin1Segment-scalar", () => Latin1(scalar: true)),

        // Portable 128-bit operations beside the WebAssembly instruction each stands in for, over 4,096 vectors a call
        new("probe/popcount-scalar", () => Probe(PopCountScalar)),
        new("probe/popcount-swar", () => Probe(PopCountSwar)),
        new("probe/popcount-shuffle", () => Probe(PopCountShuffle)),
        new("probe/popcount-shufflenative", () => Probe(PopCountShuffleNative)),
        new("probe/popcount-packedsimd", () => Probe(PopCountPackedSimd), PackedSimd.IsSupported),
        new("probe/shuffle-variable", () => Probe(ShuffleVariable)),
        new("probe/shuffle-native", () => Probe(ShuffleNativeVariable)),
        new("probe/shuffle-swizzle", () => Probe(ShuffleSwizzle), PackedSimd.IsSupported),
        new("probe/dot16-portable", () => Probe(Dot16Portable)),
        new("probe/dot16-packedsimd", () => Probe(Dot16PackedSimd), PackedSimd.IsSupported),
        new("probe/pairwise-portable", () => Probe(PairwisePortable)),
        new("probe/pairwise-packedsimd", () => Probe(PairwisePackedSimd), PackedSimd.IsSupported),
        new("probe/convert-scalar", () => Probe(ConvertScalar)),
        new("probe/convert-saturating", () => Probe(ConvertSaturating)),
        new("probe/convert-native", () => Probe(ConvertNative)),
        new("probe/convert-native-fixed", () => Probe(ConvertNativeFixed)),
        new("probe/convert-packedsimd", () => Probe(ConvertPackedSimd), PackedSimd.IsSupported),
        new("probe/convert-packedsimd-min", () => Probe(ConvertPackedSimdMin), PackedSimd.IsSupported),
        new("probe/convert-vectorcast", () => Probe(ConvertVectorCast)),
        new("probe/convert-pseudo-inline", () => Probe(ConvertPseudoInline), PackedSimd.IsSupported),
        new("probe/clamp-inline", () => Probe(ClampInline)),
        new("probe/clamp-pixelindex", () => Probe(ClampPixelIndex)),
        new("probe/movemask-portable", () => Probe(MovemaskPortable)),
        new("probe/movemask-packedsimd", () => Probe(MovemaskPackedSimd), PackedSimd.IsSupported),
    ];

    private static QRCodeData Large() => QRCodeGenerator.Create(DeterministicText(2900), QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(40) });
    private static QRCodeData Medium() => QRCodeGenerator.Create(DeterministicText(400), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(25) });
    private static QRCodeData Small() => QRCodeGenerator.Create(ShortUrl, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(6) });
    private static RmQRCodeData RmQRLarge() => RmQRCodeGenerator.Create(RmQRByte, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });

    private static Func<int> QREncode(string text, QREccLevel ecc)
    {
        var destination = new byte[1 << 16];
        return () => QRCodeGenerator.Create(text, ecc, destination);
    }

    private static Func<int> MicroEncode(string text, MicroQREccLevel ecc)
    {
        var destination = new byte[1 << 10];
        return () => MicroQRCodeGenerator.Create(text, ecc, destination);
    }

    private static Func<int> RmQREncode(string text, RmQRVersion version)
    {
        var destination = new byte[1 << 13];
        var options = new RmQRCodeGeneratorOptions { Version = version };
        return () => RmQRCodeGenerator.Create(text, RmQREccLevel.M, destination, options);
    }

    private static Func<int> StructuredAppend(string content, QRSegmentation segmentation)
    {
        var options = new QRCodeGeneratorOptions { Segmentation = segmentation };
        return () => QRCodeGenerator.CreateStructuredAppend(content, QREccLevel.L, options).Length;
    }

    private static Func<int> QRMatrix(string text, QREccLevel ecc)
    {
        var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        if (!QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options))
            throw new InvalidOperationException("does not fit");
        var modules = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, ecc, modules, options);
        var chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        return Checked(() => QRCodeDecoder.TryDecode(modules, size.Size, chars, out var written, out _) ? written : 0, text.Length);
    }

    private static Func<int> MicroMatrix(string text, MicroQREccLevel ecc)
    {
        var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 };
        if (!MicroQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options))
            throw new InvalidOperationException("does not fit");
        var modules = new byte[size.BufferSize];
        MicroQRCodeGenerator.Create(text, ecc, modules, options);
        var chars = new char[64];
        return Checked(() => MicroQRCodeDecoder.TryDecode(modules, size.Size, chars, out var written, out _) ? written : 0, text.Length);
    }

    private static Func<int> RmQRMatrix(string text, RmQRVersion version, int flips, int seed)
    {
        var options = new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 };
        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, options))
            throw new InvalidOperationException("does not fit");
        var modules = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(text, RmQREccLevel.M, modules, options);
        if (flips > 0)
            modules = Damage(modules, size.Width, size.Height, text, flips, seed);
        var chars = new char[512];
        return Checked(() => RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, chars, out var written, out _) ? written : 0, text.Length);
    }

    /// <summary>The benchmark's correctable damage: exactly <paramref name="flips"/> corrected codeword errors.</summary>
    private static byte[] Damage(byte[] modules, int width, int height, string text, int flips, int seed)
    {
        for (var attempt = 0; attempt < 4096; attempt++)
        {
            var random = new Random(seed + attempt);
            var damaged = (byte[])modules.Clone();
            var picked = new HashSet<int>();
            while (picked.Count < flips)
                picked.Add(random.Next(damaged.Length));
            foreach (var index in picked)
                damaged[index] ^= 1;
            if (RmQRCodeDecoder.TryDecode(damaged, width, height, out var decoded, out var info) && decoded == text && info.ErrorsCorrected == flips)
                return damaged;
        }
        throw new InvalidOperationException("no correctable damage found");
    }

    private static Func<int> StructuredAppendDecode(string content)
    {
        var set = QRCodeGenerator.CreateStructuredAppend(content, QREccLevel.L);
        return Checked(() =>
        {
            var length = 0;
            foreach (var symbol in set)
            {
                QRCodeDecoder.TryDecode(symbol, out var text, out _);
                length += text.Length;
            }
            return length;
        }, content.Length);
    }

    private sealed record Image(byte[] Luminance, int Width, int Height);

    private static Image Crisp(QRCodeData data, float pixelsPerModule)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => data[row, column], data.Size, data.Size, pixelsPerModule, 0f, 0f);
        return new(luminance, width, height);
    }

    private static Image Supersampled(QRCodeData data, float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, side) = SupersampledRenderer.Render(data, pixelsPerModule, degrees, keystone);
        return new(luminance, side, side);
    }

    /// <summary>Blurred, contrast reduced, a brightness ramp and noise: no pure black or white left.</summary>
    private static Image Soften(Image image)
    {
        var (source, width, height) = (image.Luminance, image.Width, image.Height);
        var soft = new byte[source.Length];
        var state = 0x9E3779B9u;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int sum = 0, count = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var sx = x + dx;
                        var sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                            continue;
                        sum += source[sy * width + sx];
                        count++;
                    }
                }
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                var value = 48 + sum / count * 150 / 255 + (x + y) * 40 / (width + height) + (int)(state % 21) - 10;
                soft[y * width + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }
        return new(soft, width, height);
    }

    private static Func<int> QRImage(QRCodeData data, Func<QRCodeData, Image> render)
    {
        var image = render(data);
        var chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        return Checked(() => QRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, chars, out var written, out _) ? written : 0, expectDecode: true);
    }

    private static Func<int> NoSymbol(int side, bool noise)
    {
        var luminance = new byte[side * side];
        var state = 0x2545F491u;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                luminance[y * side + x] = noise ? (byte)state : (byte)((x + y) * 255 / (2 * side - 2));
            }
        }
        var chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        return Checked(() => QRCodeDecoder.TryDecodeImage(luminance, side, side, chars, out var written, out _) ? written : 0, expectDecode: false);
    }

    private static Func<int> MicroImage()
    {
        var data = MicroQRCodeGenerator.Create("MICRO QR M4 BENCH", MicroQREccLevel.M);
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => data[row, column], data.Size, data.Size, 8f, 0f, 0f);
        var chars = new char[64];
        return Checked(() => MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, chars, out var written, out _) ? written : 0, expectDecode: true);
    }

    private static Image RmQRCrisp(RmQRCodeData data)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => data[row, column], data.Width, data.Height, 8f, 0f, 0f);
        return new(luminance, width, height);
    }

    private static Func<int> RmQRImage(RmQRCodeData data, bool keystone)
    {
        var image = keystone
            ? SupersampledImage(SupersampledRenderer.Render((row, column) => data[row, column], data.Width, data.Height, 4f, 23f, 0.15f))
            : RmQRCrisp(data);
        var chars = new char[512];
        return Checked(() => RmQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, chars, out var written, out _) ? written : 0, expectDecode: true);
    }

    private static Image SupersampledImage((byte[] Luminance, int Width, int Height) rendered) => new(rendered.Luminance, rendered.Width, rendered.Height);

    /// <summary>The image as opaque RGBA, converted to luminance inside the measurement as the SKBitmap entry does, then decoded by the symbology the image holds.</summary>
    private static Func<int> Bitmap(Image image)
    {
        var rgba = new byte[image.Luminance.Length * 4];
        for (var i = 0; i < image.Luminance.Length; i++)
        {
            var v = image.Luminance[i];
            rgba[4 * i] = v;
            rgba[4 * i + 1] = v;
            rgba[4 * i + 2] = v;
            rgba[4 * i + 3] = 255;
        }
        var luminance = new byte[image.Luminance.Length];
        var chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        var (width, height) = (image.Width, image.Height);
        var qr = QRCodeDecoder.TryDecodeImage(image.Luminance, width, height, chars, out _, out _);
        return Checked(() =>
        {
            LuminanceConverter.Convert(rgba, width, height, width * 4, PixelLayout.Rgba8888, premultipliedAlpha: true, luminance);
            return qr
                ? QRCodeDecoder.TryDecodeImage(luminance, width, height, chars, out var written, out _) ? written : 0
                : RmQRCodeDecoder.TryDecodeImage(luminance, width, height, chars, out written, out _) ? written : 0;
        }, expectDecode: true);
    }

    // ---- Kernels alone ----

    private static Func<int> Histogram(Image image)
    {
        var histogram = new int[256];
        return () =>
        {
            Binarizer.FillHistogram(image.Luminance, histogram);
            return histogram[0];
        };
    }

    /// <summary>Mask scoring and selection of one symbol, on random codewords placed as the encoder places them; the copy restores the unmasked matrix each call (<paramref name="score"/> false times the copy alone).</summary>
    private static Func<int> MaskScoring(int version, bool score)
    {
        var layout = ModulePlacer.GetLayout(version);
        var size = layout.Size;
        var codewords = new byte[layout.FreeModules / 8];
        new Random(version).NextBytes(codewords);
        var pristine = new byte[size * size];
        layout.Template.AsSpan().CopyTo(pristine);
        ModulePlacer.PlaceDataWords(pristine, layout, codewords);
        var work = new byte[pristine.Length];
        if (!score)
        {
            return () =>
            {
                pristine.AsSpan().CopyTo(work);
                return work[size + 1];
            };
        }
        return () =>
        {
            pristine.AsSpan().CopyTo(work);
            return ModulePlacer.MaskCode(work, size, version, layout.BlockedMask, QREccLevel.L);
        };
    }

    private static Func<int> Parity(string text, EciMode charset) => () => StructuredAppendPlanner.Parity(text, charset, utf8Bom: false);

    private static Func<int> Converter(Image image)
    {
        var rgba = new byte[image.Luminance.Length * 4];
        for (var i = 0; i < image.Luminance.Length; i++)
        {
            rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = image.Luminance[i];
            rgba[4 * i + 3] = 255;
        }
        var luminance = new byte[image.Luminance.Length];
        return () =>
        {
            LuminanceConverter.Convert(rgba, image.Width, image.Height, image.Width * 4, PixelLayout.Rgba8888, premultipliedAlpha: true, luminance);
            return luminance[1];
        };
    }

    private static Func<int> Inverter(bool scalar)
    {
        var source = Crisp(Large(), 3f).Luminance;
        var destination = new byte[source.Length];
        if (scalar)
        {
            return () =>
            {
                for (var i = 0; i < source.Length; i++)
                    destination[i] = (byte)~source[i];
                return destination[1];
            };
        }
        return () =>
        {
            LuminanceInverter.Invert(source, destination);
            return destination[1];
        };
    }

    private static Func<int> LocalBinarize(bool scalar)
    {
        var image = Soften(Crisp(Large(), 4f));
        var threshold = Binarizer.ComputeOtsuThreshold(image.Luminance);
        var binarized = new byte[image.Luminance.Length];
        var scratch = new int[LocalBinarizer.ScratchLength(image.Width, image.Height)];
        return scalar
            ? () => LocalBinarizer.TryBinarizeScalar(image.Luminance, image.Width, image.Height, false, threshold, binarized, scratch, out var dark) ? dark : -1
            : () => LocalBinarizer.TryBinarize(image.Luminance, image.Width, image.Height, false, threshold, binarized, scratch, out var dark) ? dark : -1;
    }

    private static Image Noise(int side)
    {
        var luminance = new byte[side * side];
        var state = 0x2545F491u;
        for (var i = 0; i < luminance.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            luminance[i] = (byte)state;
        }
        return new(luminance, side, side);
    }

    /// <summary>The finder search's row pass over every row (stride 1), the full sweep a failed stride-4 pass falls back to.</summary>
    private static Func<int> FinderRows(Image image, FinderRowKernel kernel)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(image.Luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        return () => FinderPatternFinder.FindCandidatesWith(image.Luminance, image.Width, image.Height, threshold, candidates, grey, 1, kernel);
    }

    /// <summary>The version 40 bottom-right alignment pattern searched where it is drawn, 8 modules either way.</summary>
    private static Func<int> Alignment(bool scalar)
    {
        var image = Crisp(Large(), 3f);
        var threshold = Binarizer.ComputeOtsuThreshold(image.Luminance);
        const float Centre = (170 + 4 + 0.5f) * 3f;
        return scalar
            ? () => AlignmentPatternFinder.TryFindScalar(image.Luminance, image.Width, image.Height, threshold, Centre, Centre, 3f, (3f, 0f), (0f, 3f), 8f, out _, out _) ? 1 : 0
            : () => AlignmentPatternFinder.TryFind(image.Luminance, image.Width, image.Height, threshold, Centre, Centre, 3f, (3f, 0f), (0f, 3f), 8f, out _, out _) ? 1 : 0;
    }

    private static Func<int> QRSample(bool scalar)
    {
        var image = Crisp(Large(), 3f);
        var transform = PerspectiveTransform.FromCoefficients(3f, 0f, 12f, 0f, 3f, 12f, 0f, 0f, 1f);
        var modules = new byte[177 * 177];
        return scalar
            ? () =>
            {
                QRImageDecoder.SampleGridScalar(image.Luminance, image.Width, image.Height, 128, transform, 177, modules);
                return modules[200];
            }
        : () =>
        {
            QRImageDecoder.SampleGrid(image.Luminance, image.Width, image.Height, 128, transform, 177, modules);
            return modules[200];
        };
    }

    private static Func<int> MicroSample(bool scalar)
    {
        var data = MicroQRCodeGenerator.Create("MICRO QR M4 BENCH", MicroQREccLevel.M);
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => data[row, column], data.Size, data.Size, 8f, 0f, 0f);
        var modules = new byte[17 * 17];
        return scalar || !Vector128.IsHardwareAccelerated
            ? () =>
            {
                MicroQRImageDecoder.SampleGridScalar(luminance, width, height, 128, 20f, 20f, 8f, 0f, 0f, 8f, 17, modules);
                return modules[20];
            }
        : () =>
        {
            MicroQRImageDecoder.SampleGridVector128(luminance, width, height, 128, 20f, 20f, 8f, 0f, 0f, 8f, 17, modules);
            return modules[20];
        };
    }

    private static Func<int> RmQRSample(bool scalar)
    {
        var image = RmQRCrisp(RmQRLarge());
        var transform = PerspectiveTransform.FromCoefficients(8f, 0f, 16f, 0f, 8f, 16f, 0f, 0f, 1f);
        var modules = new byte[139 * 17];
        return scalar
            ? () =>
            {
                RmQRImageDecoder.SampleGridScalar(image.Luminance, image.Width, image.Height, 128, transform, 139, 17, modules);
                return modules[200];
            }
        : () =>
        {
            RmQRImageDecoder.SampleGrid(image.Luminance, image.Width, image.Height, 128, transform, 139, 17, modules);
            return modules[200];
        };
    }

    private static Func<int> RmQRLattice(bool scalar)
    {
        var image = RmQRCrisp(RmQRLarge());
        var dark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var light = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var (x, y) = (image.Width / 2f, image.Height / 2f);
        return scalar || !Vector128.IsHardwareAccelerated
            ? () =>
            {
                RmQRImageDecoder.ClassifySubFinderLatticeScalar(image.Luminance, image.Width, image.Height, 128, x, y, 8f, 0f, 0f, 8f, 15, 0.1f, 0.1f, dark, light);
                return (int)dark[3];
            }
        : () =>
        {
            RmQRImageDecoder.ClassifySubFinderLatticeVector128(image.Luminance, image.Width, image.Height, 128, x, y, 8f, 0f, 0f, 8f, 15, 0.1f, 0.1f, dark, light);
            return (int)dark[3];
        };
    }

    private static Func<int> Latin1(bool scalar)
    {
        var text = Repeat("Crème brûlée à la carte, déjà vu ", 150);
        var destination = new byte[512];
        return () =>
        {
            ulong acc = 0;
            int accBits = 0, bytePos = 0;
            RmQRBinaryEncoder.WriteLatin1(ref destination[0], ref acc, ref accBits, ref bytePos, text, vectorized: !scalar);
            return bytePos;
        };
    }

    // ---- Probes: one operation over 4,096 vectors ----

    private const int ProbeVectors = 4096;
    private static readonly Vector128<byte> NibbleCounts = Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);

    private static Func<int> Probe(Func<byte[], int> body)
    {
        var data = Noise(256).Luminance; // 65,536 bytes: 4,096 vectors
        return () => body(data);
    }

    private static int PopCountScalar(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var sum = 0;
        for (nuint i = 0; i < ProbeVectors * 16; i += 8)
            sum += System.Numerics.BitOperations.PopCount(System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref System.Runtime.CompilerServices.Unsafe.Add(ref start, i)));
        return sum;
    }

    private static int PopCountSwar(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<byte>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.LoadUnsafe(ref start, i * 16);
            v -= (v >> 1) & Vector128.Create((byte)0x55);
            v = (v & Vector128.Create((byte)0x33)) + ((v >> 2) & Vector128.Create((byte)0x33));
            acc += (v + (v >> 4)) & Vector128.Create((byte)0x0F);
        }
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int PopCountShuffle(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<byte>.Zero;
        var table = NibbleCounts;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.LoadUnsafe(ref start, i * 16);
            acc += Vector128.Shuffle(table, v & Vector128.Create((byte)0x0F)) + Vector128.Shuffle(table, v >> 4);
        }
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int PopCountShuffleNative(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<byte>.Zero;
        var table = NibbleCounts;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.LoadUnsafe(ref start, i * 16);
            acc += Vector128.ShuffleNative(table, v & Vector128.Create((byte)0x0F)) + Vector128.ShuffleNative(table, v >> 4);
        }
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int PopCountPackedSimd(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<byte>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += PackedSimd.PopCount(Vector128.LoadUnsafe(ref start, i * 16));
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int ShuffleVariable(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = NibbleCounts;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc ^= Vector128.Shuffle(acc, Vector128.LoadUnsafe(ref start, i * 16) & Vector128.Create((byte)0x0F));
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int ShuffleNativeVariable(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = NibbleCounts;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc ^= Vector128.ShuffleNative(acc, Vector128.LoadUnsafe(ref start, i * 16) & Vector128.Create((byte)0x0F));
        return acc.ToScalar() + acc.GetElement(15);
    }

    private static int ShuffleSwizzle(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = NibbleCounts;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc ^= PackedSimd.Swizzle(acc, Vector128.LoadUnsafe(ref start, i * 16) & Vector128.Create((byte)0x0F));
        return acc.ToScalar() + acc.GetElement(15);
    }

    /// <summary>Signed 16-bit products summed in pairs into 32-bit lanes, with portable operations.</summary>
    private static int Dot16Portable(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var weights = Vector128.Create((short)77, 150, 29, 0, 77, 150, 29, 0);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.LoadUnsafe(ref start, i * 16).AsInt16() & Vector128.Create((short)0xFF);
            var lo = Vector128.WidenLower(v) * Vector128.WidenLower(weights);
            var hi = Vector128.WidenUpper(v) * Vector128.WidenUpper(weights);
            acc += Vector128.Shuffle(lo, Vector128.Create(0, 2, 0, 2)) + Vector128.Shuffle(lo, Vector128.Create(1, 3, 1, 3)) + hi;
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    private static int Dot16PackedSimd(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var weights = Vector128.Create((short)77, 150, 29, 0, 77, 150, 29, 0);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += PackedSimd.Dot(Vector128.LoadUnsafe(ref start, i * 16).AsInt16() & Vector128.Create((short)0xFF), weights);
        return acc.ToScalar() + acc.GetElement(3);
    }

    private static int PairwisePortable(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<ushort>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.LoadUnsafe(ref start, i * 16).AsUInt16();
            acc += (v & Vector128.Create((ushort)0xFF)) + (v >> 8);
        }
        return acc.ToScalar() + acc.GetElement(7);
    }

    private static int PairwisePackedSimd(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<ushort>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += PackedSimd.AddPairwiseWidening(Vector128.LoadUnsafe(ref start, i * 16));
        return acc.ToScalar() + acc.GetElement(7);
    }

    /// <summary>Four floats to 32-bit integers a step, per lane with a cast, as the scalar samplers do.</summary>
    private static int ConvertScalar(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var sum = 0;
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f;
            sum += (int)v.GetElement(0) + (int)v.GetElement(1) + (int)v.GetElement(2) + (int)v.GetElement(3);
        }
        return sum;
    }

    /// <summary>Four floats to 32-bit integers a step with the saturating conversion the 128-bit samplers use.</summary>
    private static int ConvertSaturating(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += Vector128.ConvertToInt32(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f);
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The same with the platform's own conversion, which differs from the cast only out of range.</summary>
    private static int ConvertNative(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += Vector128.ConvertToInt32Native(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f);
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The platform's conversion made saturating in portable operations: positive overflow to the maximum, NaN to zero.</summary>
    private static int ConvertNativeFixed(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var overflow = Vector128.Create(2147483648f);
        var max = Vector128.Create(int.MaxValue);
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f;
            var r = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(v, overflow).AsInt32(), max, Vector128.ConvertToInt32Native(v));
            acc += r & Vector128.Equals(v, v).AsInt32();
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    private static int ConvertPackedSimd(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += PackedSimd.ConvertToInt32Saturate(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f);
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The operations VectorCast.ToInt32 takes on WebAssembly, written in the loop, so a call the interpreter does not inline shows against it.</summary>
    private static int ConvertPackedSimdMin(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var cap = Vector128.Create(2147483520f);
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += PackedSimd.ConvertToInt32Saturate(Vector128.Min(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f, cap));
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>VectorCast.ToInt32's WebAssembly operations written in the loop: against convert-vectorcast, the cost of the call where it is not inlined.</summary>
    private static int ConvertPseudoInline(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var cap = Vector128.Create(2147483520f);
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f;
            acc += PackedSimd.ConvertToInt32Saturate(PackedSimd.PseudoMin(PackedSimd.PseudoMax(Vector128<float>.Zero, v), cap));
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The scalar samplers' clamp written in the loop, against clamp-pixelindex, which calls PixelIndex.Clamp: 16,384 coordinates a call.</summary>
    private static int ClampInline(byte[] data)
    {
        var sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            var x = data[i] * 0.75f - 20f;
            var p = x >= 160 ? 159 : (int)x;
            if (p < 0)
                p = 0;
            sum += p;
        }
        return sum;
    }

    private static int ClampPixelIndex(byte[] data)
    {
        var sum = 0;
        for (var i = 0; i < data.Length; i += 4)
            sum += PixelIndex.Clamp(data[i] * 0.75f - 20f, 160);
        return sum;
    }

    private static int ConvertVectorCast(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += VectorCast.ToInt32(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.75f);
        return acc.ToScalar() + acc.GetElement(3);
    }

    private static int MovemaskPortable(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var sum = 0u;
        for (nuint i = 0; i < ProbeVectors; i++)
            sum += Vector128.LoadUnsafe(ref start, i * 16).ExtractMostSignificantBits();
        return (int)sum;
    }

    private static int MovemaskPackedSimd(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var sum = 0;
        for (nuint i = 0; i < ProbeVectors; i++)
            sum += PackedSimd.Bitmask(Vector128.LoadUnsafe(ref start, i * 16));
        return sum;
    }

    /// <summary>Runs the shape once and refuses one that stopped doing its work, which would go on being timed as a fast failure.</summary>
    private static Func<int> Checked(Func<int> run, int expectedLength)
    {
        var result = run();
        if (result != expectedLength)
            throw new InvalidOperationException($"shape returned {result}, expected {expectedLength}");
        return run;
    }

    private static Func<int> Checked(Func<int> run, bool expectDecode)
    {
        var result = run();
        if (expectDecode != result > 0)
            throw new InvalidOperationException(expectDecode ? "shape did not decode" : "shape decoded where it should not");
        return run;
    }

    private static string Repeat(string pattern, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = pattern[i % pattern.Length];
        return new string(chars);
    }

    private static string DeterministicText(int length)
    {
        var random = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }
}
