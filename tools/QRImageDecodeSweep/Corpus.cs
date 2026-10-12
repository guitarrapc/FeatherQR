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

    /// <summary>
    /// For each reader that came from result lines (<see cref="ReaderLines"/>), the upright images it reads, set by set, beside this library's and zxing-cpp's, with the gap and the reverse against this library and each image by name.
    /// It reads an image when one of its texts is the expected one, the rule every reader here is scored by.
    /// </summary>
    public static string LinesMarkdown(IReadOnlyList<ResultRow> rows, IReadOnlyList<(string Lineage, string Set, string Path)> images, ReaderLines lines)
    {
        var upright = rows.Where(static r => r.Key[4] == "0").ToDictionary(static r => (r.Key[2], r.Key[3]));
        var sb = new StringBuilder();
        foreach (var name in lines.Readers)
        {
            var scored = new List<(string Set, string File, ResultRow Row, bool Held, bool Read, string? AnotherText)>();
            foreach (var (_, set, path) in images)
            {
                var file = Path.GetFileName(path);
                var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"), Encoding.UTF8);
                var line = lines.Find(name, set, file);
                var read = line is not null && Array.IndexOf(line.Texts, expected) >= 0;
                scored.Add((set, file, upright[(set, file)], line is not null, read, line is { Texts.Length: > 0 } && !read ? Readers.Difference(expected, line.Texts[0]) : null));
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"## {name}, from result lines, upright");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Run in its own harness: {scored.Count(static s => s.Held):N0} of the {scored.Count:N0} images have a line.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Set | Images | FeatherQR | zxing-cpp | {name} | Gap | Reverse | Another text |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var group in scored.GroupBy(static s => s.Set).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(scored.GroupBy(static _ => "**All**").Single()))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count():N0} | {group.Count(static s => s.Row.FeatherQr):N0} | {group.Count(static s => s.Row.ZXingCpp):N0} | {group.Count(static s => s.Read):N0} | {group.Count(static s => s.Read && !s.Row.FeatherQr):N0} | {group.Count(static s => s.Row.FeatherQr && !s.Read):N0} | {group.Count(static s => s.AnotherText is not null):N0} |");
            sb.AppendLine();

            AppendList(sb, $"Gap: {name} reads, FeatherQR does not", scored.Where(static s => s.Read && !s.Row.FeatherQr).Select(static s => $"{s.Set}/{s.File}: {s.Row.Status}"));
            AppendList(sb, $"Reverse: FeatherQR reads, {name} does not", scored.Where(static s => s.Row.FeatherQr && !s.Read).Select(static s => $"{s.Set}/{s.File}"));
            AppendList(sb, $"{name} decoded, with another text", scored.Where(static s => s.AnotherText is not null).Select(static s => $"{s.Set}/{s.File}: {s.AnotherText}"));
        }
        return sb.ToString();

        static void AppendList(StringBuilder sb, string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
                return;
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({list.Count:N0})");
            sb.AppendLine();
            foreach (var item in list)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
            sb.AppendLine();
        }
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
