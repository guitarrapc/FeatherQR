using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FeatherQR.Tests;
using SkiaSharp;

namespace QRImageDecodeSweep;

/// <summary>
/// Real images with known text: third-party sample sets committed under the test fixtures, each image beside a <c>.txt</c> holding what it encodes.
/// Every image is read upright and at the three other right angles, as the sets' own test runner reads them.
/// </summary>
internal static class Corpus
{
    public const string RelativeRoot = "tests/FeatherQR.Tests/Fixtures/RealImages";

    public static readonly string[] KeyColumns = ["symbology", "lineage", "set", "file", "rotation", "width", "height"];

    private static readonly string[] imageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>The symbology a sample set holds, from its directory name.</summary>
    public static string? SymbologyOf(string setName) => setName switch
    {
        _ when setName.StartsWith("qrcode-", StringComparison.Ordinal) => Symbologies.StandardQr,
        _ when setName.StartsWith("microqrcode-", StringComparison.Ordinal) => Symbologies.MicroQr,
        _ when setName.StartsWith("rmqrcode-", StringComparison.Ordinal) => Symbologies.RmQr,
        _ => null,
    };

    public static bool IsImage(string path) => Array.IndexOf(imageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>The committed sets, every lineage under <see cref="RelativeRoot"/>.</summary>
    public static List<ResultRow> Run(string repoRoot)
    {
        var root = Path.Combine(repoRoot, RelativeRoot);
        return Run(Directory.EnumerateDirectories(root).OrderBy(static x => x, StringComparer.Ordinal).Select(static lineage => (Path.GetFileName(lineage), lineage)));
    }

    /// <summary>The images of every sample set (a directory named for its symbology) under each root, each image beside the <c>.txt</c> it encodes. An image with no <c>.txt</c> has nothing to compare and is left out.</summary>
    public static List<ResultRow> Run(IEnumerable<(string Lineage, string Root)> lineages)
    {
        var images = Images(lineages);
        var rows = new ConcurrentBag<(int Order, ResultRow Row)>();
        Parallel.For(0, images.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, i =>
        {
            var (lineage, set, path) = images[i];
            var symbology = SymbologyOf(set)!;
            var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"), Encoding.UTF8);
            using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
            var (luminance, width, height) = Pixels.ToLuminance(bitmap);

            for (var turns = 0; turns < 4; turns++)
            {
                var (Luminance, Width, Height) = NearestNeighbourRenderer.Turn(luminance, width, height, turns, mirror: false);
                var image = new Rendered(Luminance, Width, Height, 0);
                string[] key =
                [
                    symbology,
                    lineage,
                    set,
                    Path.GetFileName(path),
                    (turns * 90).ToString(CultureInfo.InvariantCulture),
                    Width.ToString(CultureInfo.InvariantCulture),
                    Height.ToString(CultureInfo.InvariantCulture),
                ];
                rows.Add((i * 4 + turns, Readers.Read(key, symbology, image, expected)));
            }
        });
        return [.. rows.OrderBy(static r => r.Order).Select(static r => r.Row)];
    }

    public static List<(string Lineage, string Set, string Path)> Images(IEnumerable<(string Lineage, string Root)> lineages)
    {
        var images = new List<(string Lineage, string Set, string Path)>();
        foreach (var (lineage, root) in lineages)
        {
            foreach (var set in Directory.EnumerateDirectories(root).OrderBy(static x => x, StringComparer.Ordinal))
            {
                if (SymbologyOf(Path.GetFileName(set)) is null)
                    continue;
                images.AddRange(Directory.EnumerateFiles(set).Where(IsImage).Where(static p => File.Exists(Path.ChangeExtension(p, ".txt"))).OrderBy(static x => x, StringComparer.Ordinal).Select(p => (lineage, Path.GetFileName(set), p)));
            }
        }
        return images;
    }

    /// <summary>
    /// Each upright image read by this library and by zxing-cpp, one image at a time on one thread, each call timed as the fastest of <paramref name="rounds"/> after one untimed call.
    /// The accuracy pass runs in parallel and its times are not comparable, so this is a pass of its own.
    /// </summary>
    public static string Timing(IReadOnlyList<(string Lineage, string Set, string Path)> images, int rounds)
    {
        var decoded = new List<double>();
        var failed = new List<double>();
        var zxing = new List<double>();
        double total = 0, zxingTotal = 0;
        foreach (var (_, set, path) in images)
        {
            var symbology = SymbologyOf(set)!;
            using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
            var (luminance, width, height) = Pixels.ToLuminance(bitmap);
            var image = new Rendered(luminance, width, height, 0);
            var success = Readers.FeatherQr(symbology, image).Text is not null;
            Readers.ZXingCpp(symbology, image);
            var own = Fastest(rounds, () => Readers.FeatherQr(symbology, image));
            var other = Fastest(rounds, () => Readers.ZXingCpp(symbology, image));
            (success ? decoded : failed).Add(own);
            zxing.Add(other);
            total += own;
            zxingTotal += other;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Timing, upright, one thread");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Fastest of {rounds} calls a reader, after one untimed call, in milliseconds. A decode returned text, whether or not it was the expected one.");
        sb.AppendLine();
        sb.AppendLine("| Reader | Images | Median | 90th percentile | Total |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| FeatherQR, decoded | {decoded.Count:N0} | {Percentile(decoded, 0.5):F3} | {Percentile(decoded, 0.9):F3} | |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| FeatherQR, failed | {failed.Count:N0} | {Percentile(failed, 0.5):F3} | {Percentile(failed, 0.9):F3} | |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| FeatherQR, all | {images.Count:N0} | {Percentile([.. decoded, .. failed], 0.5):F3} | {Percentile([.. decoded, .. failed], 0.9):F3} | {total:F1} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| zxing-cpp (`TryHarder`), all | {zxing.Count:N0} | {Percentile(zxing, 0.5):F3} | {Percentile(zxing, 0.9):F3} | {zxingTotal:F1} |");
        sb.AppendLine();
        return sb.ToString();
    }

    private static double Fastest(int rounds, Action call)
    {
        var best = double.MaxValue;
        for (var i = 0; i < rounds; i++)
        {
            var start = Stopwatch.GetTimestamp();
            call();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return best;
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(fraction * sorted.Count))];
    }
}
