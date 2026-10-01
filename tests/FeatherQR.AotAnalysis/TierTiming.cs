using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// <c>--loop</c> runs one shape for a fixed time instead, for a sampling profiler. A <c>--shape</c> filter matches names containing it, or
/// with a trailing <c>$</c> the one name: interpreted WebAssembly times a shape by what ran before it in the process, so a comparison
/// there runs each shape alone.
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

        var selected = shapes.Where(s => s.Available && (filters.Length == 0 || filters.Any(f => f.EndsWith('$') ? s.Name == f[..^1] : s.Name.Contains(f, StringComparison.Ordinal)))).ToArray();
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
        new("encode/rmqr-numeric-361", () => RmQREncodeFit(new string('7', 361))),
        new("encode/rmqr-alnum-120", () => RmQREncodeFit(new string('A', 120))),
        // The data-object API: the symbol packed to bits by the generator, unpacked again by the decoder
        new("data/micro-m4-byte-encode", () => () => MicroQRCodeGenerator.Create("bytes m4 mode", MicroQREccLevel.M).Size),
        new("data/micro-m4-byte-decode", () => MicroDataDecode("bytes m4 mode", MicroQREccLevel.M)),
        new("data/rmqr-r17x139-byte-encode", () => RmQRDataEncode(RmQRByte, RmQRVersion.R17x139)),
        new("data/rmqr-r17x139-byte-decode", () => RmQRDataDecode(RmQRByte, RmQRVersion.R17x139)),
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
        // Bowed off the plane: read only through the piecewise mesh, after the anchored transform fails
        new("image/qr-v25-4px-bowed", () => QRImage(MeshSymbol(25), image => Bowed(image, 4f, 20f, 2f))),
        new("image/qr-v40-3.5px-bowed", () => QRImage(MeshSymbol(40), image => Bowed(image, 3.5f, 200f, 1.5f))),
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
        new("kernel/Binarizer-gradient", () => Histogram(Gradient(740))),
        // The 128-bit tier and the scalar one entered directly, beside the dispatch above
        new("kernel/Binarizer-v40-3px-v128", () => Histogram(Crisp(Large(), 3f), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-v40-3px-scalar", () => Histogram(Crisp(Large(), 3f), Binarizer.FillHistogramScalar)),
        new("kernel/Binarizer-v40-3.4px-v128", () => Histogram(Crisp(Large(), 3.4f), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-v40-3.4px-scalar", () => Histogram(Crisp(Large(), 3.4f), Binarizer.FillHistogramScalar)),
        new("kernel/Binarizer-v40-4px-rot17-v128", () => Histogram(Supersampled(Large(), 4f, 17f, 0f), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-v40-4px-rot17-scalar", () => Histogram(Supersampled(Large(), 4f, 17f, 0f), Binarizer.FillHistogramScalar)),
        new("kernel/Binarizer-v40-4px-soft-v128", () => Histogram(Soften(Crisp(Large(), 4f)), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-v40-4px-soft-scalar", () => Histogram(Soften(Crisp(Large(), 4f)), Binarizer.FillHistogramScalar)),
        new("kernel/Binarizer-noise-v128", () => Histogram(Noise(740), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-noise-scalar", () => Histogram(Noise(740), Binarizer.FillHistogramScalar)),
        new("kernel/Binarizer-gradient-v128", () => Histogram(Gradient(740), Binarizer.FillHistogramVector128)),
        new("kernel/Binarizer-gradient-scalar", () => Histogram(Gradient(740), Binarizer.FillHistogramScalar)),
        new("kernel/LuminanceConverter-v40-3px", () => Converter(Crisp(Large(), 3f))),
        new("kernel/LuminanceConverter-rmqr-r17x139-8px", () => Converter(RmQRCrisp(RmQRLarge()))),
        new("kernel/LuminanceConverter-v40-3px-scalar", () => Converter(Crisp(Large(), 3f), scalar: true)),
        new("kernel/LuminanceConverter-rmqr-r17x139-8px-scalar", () => Converter(RmQRCrisp(RmQRLarge()), scalar: true)),
        new("kernel/LuminanceConverter-v40-3px-alpha", () => Converter(Crisp(Large(), 3f), straightAlpha: true)),
        new("kernel/LuminanceConverter-v40-3px-alpha-scalar", () => Converter(Crisp(Large(), 3f), scalar: true, straightAlpha: true)),
        new("kernel/MaskCode-v1", () => MaskScoring(1, score: true)),
        new("kernel/MaskCode-v1-copy", () => MaskScoring(1, score: false)),
        new("kernel/MaskCode-v6", () => MaskScoring(6, score: true)),
        new("kernel/MaskCode-v20", () => MaskScoring(20, score: true)),
        new("kernel/MaskCode-v40", () => MaskScoring(40, score: true)),
        new("kernel/MaskCode-v40-copy", () => MaskScoring(40, score: false)),
        new("kernel/MaskCode-v10", () => MaskScoring(10, score: true)),
        new("kernel/MaskCode-v1-scalar", () => MaskScoring(1, score: true, scalar: true)),
        new("kernel/MaskCode-v6-scalar", () => MaskScoring(6, score: true, scalar: true)),
        new("kernel/MaskCode-v10-scalar", () => MaskScoring(10, score: true, scalar: true)),
        new("kernel/MaskCode-v20-scalar", () => MaskScoring(20, score: true, scalar: true)),
        new("kernel/MaskCode-v40-scalar", () => MaskScoring(40, score: true, scalar: true)),
        new("kernel/MaskCode-v12", () => MaskScoring(12, score: true)),
        new("kernel/MaskCode-v12-scalar", () => MaskScoring(12, score: true, scalar: true)),
        new("kernel/MaskCode-v27", () => MaskScoring(27, score: true)),
        new("kernel/MaskCode-v27-scalar", () => MaskScoring(27, score: true, scalar: true)),
        new("kernel/MaskCode-v28", () => MaskScoring(28, score: true)),
        new("kernel/MaskCode-v28-scalar", () => MaskScoring(28, score: true, scalar: true)),
        new("kernel/StructuredAppendParity-byte-45k", () => Parity(Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), EciMode.Iso8859_1)),
        new("kernel/StructuredAppendPlan-mixed-40k-optimal", () => SetPlan(Repeat("order 20260915 item 0000123456 qty 42 ", 40_000))),
        new("kernel/StructuredAppendPlan-utf8-15k-optimal", () => SetPlan(Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000))),
        new("probe/set-plan-v3M-lanes", () => SetPlanAt(3, lanes: true, QREccLevel.M)),
        new("probe/set-plan-v3M-scalar", () => SetPlanAt(3, lanes: false, QREccLevel.M)),
        new("probe/set-plan-v3-lanes", () => SetPlanAt(3, lanes: true)),
        new("probe/set-plan-v3-scalar", () => SetPlanAt(3, lanes: false)),
        new("probe/set-plan-v4M-lanes", () => SetPlanAt(4, lanes: true, QREccLevel.M)),
        new("probe/set-plan-v4M-scalar", () => SetPlanAt(4, lanes: false, QREccLevel.M)),
        new("probe/set-plan-v2-lanes", () => SetPlanAt(2, lanes: true)),
        new("probe/set-plan-v2-scalar", () => SetPlanAt(2, lanes: false)),
        new("probe/set-plan-v4-lanes", () => SetPlanAt(4, lanes: true)),
        new("probe/set-plan-v4-scalar", () => SetPlanAt(4, lanes: false)),
        new("probe/set-plan-v6-lanes", () => SetPlanAt(6, lanes: true)),
        new("probe/set-plan-v6-scalar", () => SetPlanAt(6, lanes: false)),
        new("kernel/EccSyndromes-148-30", () => Syndromes(148, 30, scalar: false)),
        new("kernel/EccSyndromes-148-30-scalar", () => Syndromes(148, 30, scalar: true)),
        new("kernel/EccSyndromes-45-30", () => Syndromes(45, 30, scalar: false)),
        new("kernel/EccSyndromes-45-30-scalar", () => Syndromes(45, 30, scalar: true)),
        new("kernel/EccSyndromes-24-8", () => Syndromes(24, 8, scalar: false)),
        new("kernel/EccSyndromes-24-8-scalar", () => Syndromes(24, 8, scalar: true)),
        new("kernel/EccEncode-118-30", () => EccEncode(118, 30, scalar: false)),
        new("kernel/EccEncode-118-30-scalar", () => EccEncode(118, 30, scalar: true)),
        new("kernel/EccEncode-15-30", () => EccEncode(15, 30, scalar: false)),
        new("kernel/EccEncode-15-30-scalar", () => EccEncode(15, 30, scalar: true)),
        new("kernel/EccEncode-16-8", () => EccEncode(16, 8, scalar: false)),
        new("kernel/EccEncode-16-8-scalar", () => EccEncode(16, 8, scalar: true)),
        new("kernel/RmQRNumeric-12", () => RmQRValueWriter("012345678901", alphanumeric: false)),
        new("kernel/RmQRNumeric-12-scalar", () => RmQRValueWriter("012345678901", alphanumeric: false, vectorized: false)),
        new("kernel/RmQRNumeric-361", () => RmQRValueWriter(new string('7', 361), alphanumeric: false)),
        new("kernel/RmQRNumeric-361-scalar", () => RmQRValueWriter(new string('7', 361), alphanumeric: false, vectorized: false)),
        new("kernel/RmQRAlphanumeric-43", () => RmQRValueWriter("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", alphanumeric: true)),
        new("kernel/RmQRAlphanumeric-43-scalar", () => RmQRValueWriter("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", alphanumeric: true, vectorized: false)),
        new("kernel/RmQRAlphanumeric-120", () => RmQRValueWriter(new string('A', 120), alphanumeric: true)),
        new("kernel/RmQRAlphanumeric-120-scalar", () => RmQRValueWriter(new string('A', 120), alphanumeric: true, vectorized: false)),
        new("kernel/RmQRExtract-R17x139", () => RmQRExtract(RmQRVersion.R17x139, scalar: false)),
        new("kernel/RmQRExtract-R17x139-scalar", () => RmQRExtract(RmQRVersion.R17x139, scalar: true)),
        new("kernel/RmQRExtract-R13x77", () => RmQRExtract(RmQRVersion.R13x77, scalar: false)),
        new("kernel/RmQRExtract-R13x77-scalar", () => RmQRExtract(RmQRVersion.R13x77, scalar: true)),
        new("kernel/RmQRExtract-R7x43", () => RmQRExtract(RmQRVersion.R7x43, scalar: false)),
        new("kernel/RmQRExtract-R7x43-scalar", () => RmQRExtract(RmQRVersion.R7x43, scalar: true)),
        new("kernel/RmQRExtract-R11x27", () => RmQRExtract(RmQRVersion.R11x27, scalar: false)),
        new("kernel/RmQRExtract-R11x27-scalar", () => RmQRExtract(RmQRVersion.R11x27, scalar: true)),
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
        new("kernel/FinderRowEdges-v40", () => FinderRows(Crisp(Large(), 3f), FinderRowKernel.EdgeList)),
        new("kernel/FinderRowEdges-noise", () => FinderRows(Noise(740), FinderRowKernel.EdgeList)),
        new("kernel/AlignmentRowMask", () => Alignment(scalar: false)),
        new("kernel/AlignmentRowMask-scalar", () => Alignment(scalar: true)),
        new("kernel/PerspectiveGridSampler", () => QRSample(scalar: false)),
        new("kernel/PerspectiveGridSampler-scalar", () => QRSample(scalar: true)),
        new("kernel/QRSampleGridPiecewise", () => QRSamplePiecewise(scalar: false)),
        new("kernel/QRSampleGridPiecewise-scalar", () => QRSamplePiecewise(scalar: true)),
        new("kernel/MicroQRSampleGrid", () => MicroSample(scalar: false)),
        new("kernel/MicroQRSampleGrid-scalar", () => MicroSample(scalar: true)),
        new("kernel/RmQRSampleGrid", () => RmQRSample(scalar: false)),
        new("kernel/RmQRSampleGrid-scalar", () => RmQRSample(scalar: true)),
        new("kernel/RmQRSubFinderLattice", () => RmQRLattice(scalar: false)),
        new("kernel/RmQRSubFinderLattice-scalar", () => RmQRLattice(scalar: true)),
        new("kernel/TextAnalyzer-byte-2900", () => AnalyzeText(DeterministicText(2900), scalar: false)),
        new("kernel/TextAnalyzer-byte-2900-scalar", () => AnalyzeText(DeterministicText(2900), scalar: true)),
        new("kernel/TextAnalyzer-digits-2900", () => AnalyzeText(Repeat("0123456789", 2900), scalar: false)),
        new("kernel/TextAnalyzer-digits-2900-scalar", () => AnalyzeText(Repeat("0123456789", 2900), scalar: true)),
        new("kernel/TextAnalyzer-url", () => AnalyzeText(ShortUrl, scalar: false)),
        new("kernel/TextAnalyzer-url-scalar", () => AnalyzeText(ShortUrl, scalar: true)),
        new("kernel/ModuleBitPacker-pack-r17x139", () => BitPack(unpack: false)),
        new("kernel/ModuleBitPacker-unpack-r17x139", () => BitPack(unpack: true)),
        new("kernel/ModulePlacerExpandBits-v40", () => ExpandV40),
        new("kernel/MicroQRByteSegment-m4", () => MicroByteCodewords),
        new("kernel/MicroQRModulePlacer", () => MicroPlace(scalar: false)),
        new("kernel/MicroQRModulePlacer-scalar", () => MicroPlace(scalar: true)),
        new("kernel/RmQRModulePlacer", () => RmQRPlace(RmQRModulePlacer.PlaceKernel.Auto)),
        new("kernel/RmQRModulePlacer-portable", () => RmQRPlace(RmQRModulePlacer.PlaceKernel.Portable)),
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
        new("probe/pixel-vectorcast", () => Probe(PixelVectorCast)),
        new("probe/pixel-inline", () => Probe(PixelInline), PackedSimd.IsSupported),
        new("probe/pixel-signed", () => Probe(PixelSigned), PackedSimd.IsSupported),
        new("probe/pixel-intclamp", () => Probe(PixelIntClamp)),
        new("probe/clamp-base", () => Probe(ClampBase)),
        new("probe/clamp-inline", () => Probe(ClampInline)),
        new("probe/clamp-unsigned", () => Probe(ClampUnsigned)),
        new("probe/clamp-signfix", () => Probe(ClampSignFix)),
        new("probe/clamp-pixelindex", () => Probe(ClampPixelIndex)),
        new("probe/expand-swar", () => Probe(ExpandSwar)),
        new("probe/expand-shuffle", () => Probe(ExpandShuffle)),
        new("probe/expand-swizzle", () => Probe(ExpandSwizzle), PackedSimd.IsSupported),
        new("probe/expand-multiply", () => Probe(ExpandMultiply)),
        new("probe/movemask-portable", () => Probe(MovemaskPortable)),
        new("probe/movemask-packedsimd", () => Probe(MovemaskPackedSimd), PackedSimd.IsSupported),
        .. new[] { 3, 5, 6, 9, 13, 16 }.SelectMany(d => new[] { 2, 5, 7, 10, 17 }.SelectMany(e => new Shape[] { new($"probe/ecc-encode-{d}-{e}", () => EccEncodePackedSimd(d, e), PackedSimd.IsSupported), new($"probe/ecc-encode-{d}-{e}-scalar", () => EccEncode(d, e, scalar: true)) })),
        .. Enum.GetValues<RmQRVersion>().SelectMany(v => new Shape[] { new($"probe/rmqr-extract-{v}", () => RmQRExtract(v, scalar: false)), new($"probe/rmqr-extract-{v}-scalar", () => RmQRExtract(v, scalar: true)) }),
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

    /// <summary>The version chosen to fit, as the segmentation benchmark's Single arm encodes.</summary>
    private static Func<int> RmQREncodeFit(string text)
    {
        var destination = new byte[1 << 13];
        return () => RmQRCodeGenerator.Create(text, RmQREccLevel.M, destination);
    }

    /// <summary>The rMQR Numeric or Alphanumeric writer alone over a payload: the tier this build takes, or the SWAR and table loops.</summary>
    private static Func<int> RmQRValueWriter(string text, bool alphanumeric, bool vectorized = true)
    {
        var destination = new byte[256];
        return () =>
        {
            ref var dest = ref destination[0];
            ulong acc = 0;
            var accBits = 0;
            var bytePos = 0;
            if (alphanumeric)
                RmQRBinaryEncoder.WriteAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized);
            else
                RmQRBinaryEncoder.WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized);
            return bytePos + accBits;
        };
    }

    private static Func<int> RmQRDataEncode(string text, RmQRVersion version)
    {
        var options = new RmQRCodeGeneratorOptions { Version = version };
        return () => RmQRCodeGenerator.Create(text, RmQREccLevel.M, options).Width;
    }

    private static Func<int> RmQRDataDecode(string text, RmQRVersion version)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        return Checked(() => RmQRCodeDecoder.TryDecode(data, out var decoded) ? decoded.Length : 0, text.Length);
    }

    private static Func<int> MicroDataDecode(string text, MicroQREccLevel ecc)
    {
        var data = MicroQRCodeGenerator.Create(text, ecc);
        return Checked(() => MicroQRCodeDecoder.TryDecode(data, out var decoded) ? decoded.Length : 0, text.Length);
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

    private static QRCodeData MeshSymbol(int version)
        => QRCodeGenerator.Create($"FQR MESH ORDER v{version} 0123456789", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });

    private static Image Bowed(QRCodeData data, float pixelsPerModule, float degrees, float bowModules)
    {
        var (luminance, side) = BowedRenderer.Render(data, pixelsPerModule, degrees, bowModules);
        return new(luminance, side, side);
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

    private static Func<int> Histogram(Image image) => Histogram(image, Binarizer.FillHistogram);

    private static Func<int> Histogram(Image image, HistogramFill fill)
    {
        var histogram = new int[256];
        return () =>
        {
            fill(image.Luminance, histogram);
            return histogram[0];
        };
    }

    private delegate void HistogramFill(ReadOnlySpan<byte> luminance, Span<int> histogram);

    /// <summary>The no-symbol gradient: neighbours share a bin, so a per-pixel count waits on the one before.</summary>
    private static Image Gradient(int side)
    {
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                luminance[y * side + x] = (byte)((x + y) * 255 / (2 * side - 2));
        }
        return new(luminance, side, side);
    }

    /// <summary>Mask scoring and selection of one symbol, on random codewords placed as the encoder places them; the copy restores the unmasked matrix each call (<paramref name="score"/> false times the copy alone).</summary>
    private static Func<int> MaskScoring(int version, bool score, bool scalar = false)
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
        if (scalar)
        {
            return () =>
            {
                pristine.AsSpan().CopyTo(work);
                return size <= 64
                    ? ModulePlacer.MaskCode64(work, size, version, layout.BlockedMask, QREccLevel.L)
                    : ModulePlacer.MaskCode192(work, size, version, layout.BlockedMask, QREccLevel.L);
            };
        }
        return () =>
        {
            pristine.AsSpan().CopyTo(work);
            return ModulePlacer.MaskCode(work, size, version, layout.BlockedMask, QREccLevel.L);
        };
    }

    /// <summary>The set's split alone, as the generator asks for it: the budget search, its walks, the version.</summary>
    private static Func<int> SetPlan(string content)
    {
        var analysis = TextAnalyzer.Analyze(content, EciMode.Default);
        var ends = new int[16];
        return () => StructuredAppendPlanner.TryPlan(content, QREccLevel.L, analysis.EciMode, analysis.EncodingMode, utf8Bom: false, QRSegmentation.Optimal, 1, 40, ends, out var count, out _, out _, allowLanes: true) ? count : -1;
    }

    /// <summary>
    /// A set held at one version, sixteen symbols of mixed content, so its chunks average what one symbol of that version holds: the planner
    /// with its lane walks, or with its scalar probes only.
    /// </summary>
    private static Func<int> SetPlanAt(int version, bool lanes, QREccLevel eccLevel = QREccLevel.L)
    {
        var perSymbol = StructuredAppendPlanner.Capacity(version, eccLevel) / 8;
        var content = Repeat("order 20260915 item 0000123456 qty 42 ", 15 * perSymbol);
        var analysis = TextAnalyzer.Analyze(content, EciMode.Default);
        var ends = new int[16];
        StructuredAppendPlanner.TryPlan(content, eccLevel, analysis.EciMode, analysis.EncodingMode, utf8Bom: false, QRSegmentation.Optimal, version, version, ends, out var chunks, out _, out _, allowLanes: false);
        Console.Error.WriteLine($"# version {version}-{eccLevel}: {content.Length} chars in {chunks} chunks");
        return () => StructuredAppendPlanner.TryPlan(content, eccLevel, analysis.EciMode, analysis.EncodingMode, utf8Bom: false, QRSegmentation.Optimal, version, version, ends, out var count, out _, out _, allowLanes: lanes) ? count : -1;
    }

    /// <summary>
    /// One block's syndrome pass, shaped as a version 40-L block (148 bytes, 30 ECC), a 40-H block (45, 30) or Micro QR M4-L (24, 8):
    /// the 128-bit tier through its entry, which the dispatch takes on x64 without AVX2 GFNI and on WebAssembly, or the scalar one.
    /// </summary>
    private static Func<int> Syndromes(int length, int eccCount, bool scalar)
    {
        var codeword = new byte[length];
        new Random(length * 31 + eccCount).NextBytes(codeword);
        var syndromes = new byte[FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.SyndromeLanes];
        return scalar
            ? () => FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, syndromes) ? syndromes[0] : 0
            : () => FeatherQR.Internals.BinaryDecoders.EccBinaryDecoder.ComputeSyndromesVector128(codeword, eccCount, syndromes) ? syndromes[0] : 0;
    }

    /// <summary>
    /// One block's Reed-Solomon remainder, shaped as a version 40-L block (118 data bytes, 30 ECC), a 40-H block (15, 30) or Micro QR
    /// M4-L (16, 8): the dispatch, or the scalar kernel.
    /// </summary>
    private static Func<int> EccEncode(int length, int eccCount, bool scalar)
    {
        var data = new byte[length];
        new Random(length * 31 + eccCount).NextBytes(data);
        var ecc = new byte[eccCount];
        if (scalar)
            return () => { FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.CalculateEccScalar(data, ecc, eccCount); return ecc[0]; };
        return () => { FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.CalculateECC(data, ecc, eccCount); return ecc[0]; };
    }

    /// <summary>The WebAssembly Reed-Solomon kernel past its entry's size gate: where that gate sits.</summary>
    private static Func<int> EccEncodePackedSimd(int length, int eccCount)
    {
        var data = new byte[length];
        new Random(length * 31 + eccCount).NextBytes(data);
        var ecc = new byte[eccCount];
        return () => { FeatherQR.Internals.BinaryEncoders.EccBinaryEncoder.PackedSimdKernel(data, ecc, eccCount); return ecc[0]; };
    }

    /// <summary>The codeword extraction of one rMQR symbol over a random grid: the portable pair planes through their pinned entry, or the scalar walk.</summary>
    private static Func<int> RmQRExtract(RmQRVersion version, bool scalar)
    {
        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var modules = new byte[width * height];
        new Random((int)version * 17).NextBytes(modules);
        for (var i = 0; i < modules.Length; i++)
            modules[i] &= 1;
        var stream = new byte[RmQRConstants.GetTotalCodewordCount(version)];
        var kernel = scalar ? RmQRMatrixDecoder.ExtractKernel.Scalar : RmQRMatrixDecoder.ExtractKernel.PairPlanesVector128;
        return () => { RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, stream, kernel); return stream[0]; };
    }

    private static Func<int> Parity(string text, EciMode charset) => () => StructuredAppendPlanner.Parity(text, charset, utf8Bom: false);

    /// <summary>
    /// The image as opaque premultiplied RGBA, as a rendered bitmap arrives; with <paramref name="straightAlpha"/>, straight alpha with the light
    /// pixels of every eighth row half transparent, so the composite runs.
    /// </summary>
    private static Func<int> Converter(Image image, bool scalar = false, bool straightAlpha = false)
    {
        var rgba = new byte[image.Luminance.Length * 4];
        for (var i = 0; i < image.Luminance.Length; i++)
        {
            rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = image.Luminance[i];
            rgba[4 * i + 3] = straightAlpha && i / image.Width % 8 == 0 && image.Luminance[i] > 128 ? (byte)128 : (byte)255;
        }
        var luminance = new byte[image.Luminance.Length];
        var (width, height) = (image.Width, image.Height);
        if (scalar)
        {
            return () =>
            {
                LuminanceConverter.ConvertRgbaForTest(rgba, luminance, width, height, width * 4, 0, 1, 2, 3, !straightAlpha, LuminanceConverter.ConvertTier.Scalar);
                return luminance[1];
            };
        }
        return () =>
        {
            LuminanceConverter.Convert(rgba, width, height, width * 4, PixelLayout.Rgba8888, premultipliedAlpha: !straightAlpha, luminance);
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
                PerspectiveGridSampler.SampleScalar(image.Luminance, image.Width, image.Height, 128, transform, 177, modules);
                return modules[200];
            }
        : () =>
        {
            PerspectiveGridSampler.Sample(image.Luminance, image.Width, image.Height, 128, transform, 177, modules);
            return modules[200];
        };
    }

    /// <summary>The version 40 mesh sampler at 3 px a module: the dispatch, or the column-table path a build without a vector tier takes.</summary>
    private static Func<int> QRSamplePiecewise(bool scalar)
    {
        var image = Crisp(Large(), 3f);
        var gridCoords = new List<float>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice(39 * 7, 7))
        {
            if (value != 0)
                gridCoords.Add(value + 0.5f);
        }
        var meshSize = gridCoords.Count;
        var nodeXs = new float[meshSize * meshSize];
        var nodeYs = new float[meshSize * meshSize];
        for (var j = 0; j < meshSize; j++)
        {
            for (var i = 0; i < meshSize; i++)
            {
                nodeXs[j * meshSize + i] = 3f * gridCoords[i] + 12f;
                nodeYs[j * meshSize + i] = 3f * gridCoords[j] + 12f;
            }
        }
        var grid = gridCoords.ToArray();
        var modules = new byte[177 * 177];
        return scalar
            ? () =>
            {
                QRImageDecoder.SampleGridPiecewiseColumnTable(image.Luminance, image.Width, image.Height, 128, grid, nodeXs, nodeYs, meshSize, 177, modules);
                return modules[200];
            }
        : () =>
        {
            QRImageDecoder.SampleGridPiecewise(image.Luminance, image.Width, image.Height, 128, grid, nodeXs, nodeYs, meshSize, 177, modules);
            return modules[200];
        };
    }

    /// <summary>An R17x139 core, 2,363 modules, packed to bits or unpacked from them, as the data model stores it.</summary>
    private static Func<int> BitPack(bool unpack)
    {
        var modules = new byte[17 * 139];
        var bits = new byte[(modules.Length + 7) / 8];
        var random = new Random(3);
        for (var i = 0; i < modules.Length; i++)
            modules[i] = (byte)random.Next(2);
        ModuleBitPacker.Pack(modules, bits);
        if (unpack)
            return () => { ModuleBitPacker.Unpack(bits, modules); return modules[7]; };
        return () => { ModuleBitPacker.Pack(modules, bits); return bits[3]; };
    }

    /// <summary>A version 40 message, 3,706 codewords, expanded to module bytes as the Standard QR placer does.</summary>
    private static readonly byte[] ExpandMessage = CreateExpandMessage();
    private static readonly byte[] ExpandBits = new byte[3706 * 8];

    private static byte[] CreateExpandMessage()
    {
        var message = new byte[3706];
        new Random(5).NextBytes(message);
        return message;
    }

    private static int ExpandV40()
    {
        ModulePlacer.ExpandBits(ExpandMessage, ExpandMessage.Length, ExpandBits);
        return ExpandBits[9];
    }

    private static readonly byte[] MicroCodewords = new byte[64];

    /// <summary>Micro QR's data codewords for a 14-character Byte payload at M4-L, the encoder the Byte segment kernel sits in.</summary>
    private static int MicroByteCodewords()
        => MicroQRBinaryEncoder.EncodeDataCodewords("MICRO QR BYTE!", MicroQRVersion.M4, MicroQREccLevel.L, EncodingMode.Byte, MicroCodewords);

    private static Func<int> AnalyzeText(string text, bool scalar)
        => scalar
            ? () => TextAnalyzer.AnalyzeScalar(text, EciMode.Default).DataLength
            : () => TextAnalyzer.Analyze(text, EciMode.Default).DataLength;

    /// <summary>An M4-L symbol placed, masked and its mask chosen, from random codewords.</summary>
    private static Func<int> MicroPlace(bool scalar)
    {
        const MicroQRVersion Version = MicroQRVersion.M4;
        const MicroQREccLevel Ecc = MicroQREccLevel.L;
        var size = MicroQRConstants.SizeFromVersion(Version);
        var data = new byte[MicroQRConstants.GetDataCodewordCount(Version, Ecc)];
        var ecc = new byte[MicroQRConstants.GetEccCodewordCount(Version, Ecc)];
        var random = new Random(7);
        random.NextBytes(data);
        random.NextBytes(ecc);
        var bits = MicroQRConstants.GetDataBitCapacity(Version, Ecc);
        var matrix = new byte[size * size];
        return scalar
            ? () => MicroQRModulePlacer.PlaceSymbolScalar(matrix, size, data, ecc, bits, Version, Ecc)
            : () => MicroQRModulePlacer.PlaceSymbol(matrix, size, data, ecc, bits, Version, Ecc);
    }

    /// <summary>An R17x139-M symbol placed from random codewords, through the dispatch or pinned to the portable path.</summary>
    private static Func<int> RmQRPlace(RmQRModulePlacer.PlaceKernel kernel)
    {
        const RmQRVersion Version = RmQRVersion.R17x139;
        var width = RmQRConstants.GetWidth(Version);
        var core = new byte[width * RmQRConstants.GetHeight(Version)];
        var message = new byte[RmQRConstants.GetTotalCodewordCount(Version)];
        new Random(11).NextBytes(message);
        return () =>
        {
            RmQRModulePlacer.PlaceSymbol(core, width, Version, RmQREccLevel.M, message, kernel);
            return core[core.Length / 2];
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

    /// <summary>VectorCast.ToPixel's WebAssembly operations written in the loop: against pixel-vectorcast, the cost of the call where it is not inlined.</summary>
    private static int PixelInline(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var last = Vector128.Create(4095f);
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.09375f - Vector128.Create(1024f);
            acc += PackedSimd.ConvertToUInt32Saturate(PackedSimd.PseudoMin(v, last)).AsInt32();
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>Both edges clamped and the signed conversion, against pixel-inline's far edge and the unsigned one.</summary>
    private static int PixelSigned(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var zero = Vector128<float>.Zero;
        var last = Vector128.Create(4095f);
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.09375f - Vector128.Create(1024f);
            acc += PackedSimd.ConvertToInt32Saturate(PackedSimd.PseudoMin(PackedSimd.PseudoMax(zero, v), last));
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The samplers before VectorCast: the saturating conversion, then the clamp on the integers.</summary>
    private static int PixelIntClamp(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var last = Vector128.Create(4095);
        for (nuint i = 0; i < ProbeVectors; i++)
        {
            var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.09375f - Vector128.Create(1024f);
            acc += Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(v), last), Vector128<int>.Zero);
        }
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>Coordinates from -1,024 to 5,120 into pixels of a 4,096-pixel line, a third of them clamped.</summary>
    private static int PixelVectorCast(byte[] data)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(data);
        var acc = Vector128<int>.Zero;
        var last = Vector128.Create(4095f);
        for (nuint i = 0; i < ProbeVectors; i++)
            acc += VectorCast.ToPixel(Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref start, i * 16).AsInt32() & Vector128.Create(0xFFFF)) * 0.09375f - Vector128.Create(1024f), last);
        return acc.ToScalar() + acc.GetElement(3);
    }

    /// <summary>The scalar samplers' clamp before PixelIndex: the cast, then both edges on the integer. 16,384 coordinates a call, a
    /// sixth of them past an edge, into a limit the runtime cannot fold.</summary>
    private static int ClampBase(byte[] data)
    {
        var sum = 0;
        var limit = 160 + (data.Length & 1);
        for (var i = 0; i < data.Length; i += 4)
        {
            var p = (int)(data[i] * 0.75f - 20f);
            if (p < 0)
                p = 0;
            else if (p >= limit)
                p = limit - 1;
            sum += p;
        }
        return sum;
    }

    /// <summary>The far edge on the float, then the cast and the near edge, as PixelIndex.Clamp takes them on WebAssembly.</summary>
    private static int ClampInline(byte[] data)
    {
        var sum = 0;
        var limit = 160 + (data.Length & 1);
        for (var i = 0; i < data.Length; i += 4)
        {
            var x = data[i] * 0.75f - 20f;
            var p = x >= limit ? limit - 1 : (int)x;
            if (p < 0)
                p = 0;
            sum += p;
        }
        return sum;
    }

    /// <summary>The cast, then one unsigned test for both edges; the coordinate's sign picks the edge.</summary>
    private static int ClampUnsigned(byte[] data)
    {
        var sum = 0;
        var limit = 160 + (data.Length & 1);
        for (var i = 0; i < data.Length; i += 4)
        {
            var x = data[i] * 0.75f - 20f;
            var p = (int)x;
            if ((uint)p >= (uint)limit)
                p = x > 0 ? limit - 1 : 0;
            sum += p;
        }
        return sum;
    }

    /// <summary>The integer edges as before PixelIndex; a negative result from a positive coordinate is an overflow the cast did not saturate.</summary>
    private static int ClampSignFix(byte[] data)
    {
        var sum = 0;
        var limit = 160 + (data.Length & 1);
        for (var i = 0; i < data.Length; i += 4)
        {
            var x = data[i] * 0.75f - 20f;
            var p = (int)x;
            if (p < 0)
                p = x > 0 ? limit - 1 : 0;
            else if (p >= limit)
                p = limit - 1;
            sum += p;
        }
        return sum;
    }

    private static int ClampPixelIndex(byte[] data)
    {
        var sum = 0;
        var limit = 160 + (data.Length & 1);
        for (var i = 0; i < data.Length; i += 4)
            sum += PixelIndex.Clamp(data[i] * 0.75f - 20f, limit);
        return sum;
    }

    /// <summary>16 bits to 16 module bytes (0 or 1), bit k to byte k, 4,096 times: two SWAR spreads of 8, as the scalar Micro QR placer writes.</summary>
    private static int ExpandSwar(byte[] data)
    {
        var output = new byte[16];
        ref var o = ref MemoryMarshal.GetArrayDataReference(output);
        var sum = 0;
        for (var i = 0; i < ProbeVectors; i++)
        {
            ulong bits = Unsafe.ReadUnaligned<ushort>(ref data[i * 2 & (data.Length - 2)]);
            for (var half = 0; half < 2; half++)
            {
                var spread = (((bits >> (8 * half)) & 0xFF) * 0x0101010101010101UL) & 0x8040201008040201UL;
                spread |= spread >> 4;
                spread |= spread >> 2;
                spread |= spread >> 1;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 8 * half), spread & 0x0101010101010101UL);
            }
            sum += output[i & 15];
        }
        return sum;
    }

    /// <summary>The same with the placers' WebAssembly step: broadcast, a constant byte shuffle, the bit kept, a min with 1.</summary>
    private static int ExpandShuffle(byte[] data)
    {
        var output = new byte[16];
        ref var o = ref MemoryMarshal.GetArrayDataReference(output);
        var sum = 0;
        for (var i = 0; i < ProbeVectors; i++)
        {
            var src = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref data[i * 2 & (data.Length - 2)])).AsByte();
            var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bitm = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            Vector128.Min(Vector128.Shuffle(src, sel) & bitm, Vector128<byte>.One).StoreUnsafe(ref o);
            sum += output[i & 15];
        }
        return sum;
    }

    /// <summary>The same with WebAssembly's swizzle for the shuffle.</summary>
    private static int ExpandSwizzle(byte[] data)
    {
        var output = new byte[16];
        ref var o = ref MemoryMarshal.GetArrayDataReference(output);
        var sum = 0;
        for (var i = 0; i < ProbeVectors; i++)
        {
            var src = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref data[i * 2 & (data.Length - 2)])).AsByte();
            var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bitm = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            Vector128.Min(PackedSimd.Swizzle(src, sel) & bitm, Vector128<byte>.One).StoreUnsafe(ref o);
            sum += output[i & 15];
        }
        return sum;
    }

    /// <summary>The same with each byte broadcast over a 64-bit lane by a multiply, no shuffle.</summary>
    private static int ExpandMultiply(byte[] data)
    {
        var output = new byte[16];
        ref var o = ref MemoryMarshal.GetArrayDataReference(output);
        var sum = 0;
        for (var i = 0; i < ProbeVectors; i++)
        {
            ulong bits = Unsafe.ReadUnaligned<ushort>(ref data[i * 2 & (data.Length - 2)]);
            var src = Vector128.Create((bits & 0xFF) * 0x0101010101010101UL, (bits >> 8) * 0x0101010101010101UL).AsByte();
            var bitm = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            Vector128.Min(src & bitm, Vector128<byte>.One).StoreUnsafe(ref o);
            sum += output[i & 15];
        }
        return sum;
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
