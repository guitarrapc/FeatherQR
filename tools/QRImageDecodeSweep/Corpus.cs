using System.Collections.Concurrent;
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

    public static List<ResultRow> Run(string repoRoot)
    {
        var root = Path.Combine(repoRoot, RelativeRoot);
        var images = new List<(string Lineage, string Set, string Path)>();
        foreach (var lineage in Directory.EnumerateDirectories(root).OrderBy(static x => x, StringComparer.Ordinal))
        {
            foreach (var set in Directory.EnumerateDirectories(lineage).OrderBy(static x => x, StringComparer.Ordinal))
            {
                if (SymbologyOf(Path.GetFileName(set)) is null)
                    continue;
                images.AddRange(Directory.EnumerateFiles(set).Where(IsImage).OrderBy(static x => x, StringComparer.Ordinal).Select(p => (Path.GetFileName(lineage), Path.GetFileName(set), p)));
            }
        }

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
}
