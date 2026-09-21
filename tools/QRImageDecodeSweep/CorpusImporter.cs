using System.Globalization;
using System.Text;

namespace QRImageDecodeSweep;

/// <summary>
/// Copies zxing-cpp's black-box sample sets for the three symbologies in as they are: each image, the <c>.txt</c> with the text it encodes, the source LICENSE so the files can be redistributed, and a PROVENANCE.md naming the commit.
/// The Structured Append set (<c>qrcode-7</c>) is already a fixture lineage of its own and is left out.
/// </summary>
internal static class CorpusImporter
{
    public const string LineageName = "zxing-cpp-samples";

    private static readonly string[] sets = ["qrcode-1", "qrcode-2", "qrcode-3", "qrcode-4", "qrcode-5", "qrcode-6", "microqrcode-1", "rmqrcode-1"];

    public static int Run(string repoRoot, string sourceRoot, string sourceCommit)
    {
        var samples = Path.Combine(sourceRoot, "test", "samples");
        var license = Path.Combine(sourceRoot, "LICENSE");
        if (!Directory.Exists(samples) || !File.Exists(license))
        {
            Console.Error.WriteLine($"expected {samples} and {license}");
            return 1;
        }

        var missing = sets.Where(set => !Directory.Exists(Path.Combine(samples, set))).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"missing sample sets under {samples}: {string.Join(", ", missing)}");
            return 1;
        }

        // Built beside the committed corpus and swapped in whole, so an import that fails leaves the last good one as it was
        var target = Path.Combine(repoRoot, Corpus.RelativeRoot, LineageName);
        var staging = target + ".importing";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        try
        {
            Stage(samples, license, sourceCommit, staging);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return 0;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void Stage(string samples, string license, string sourceCommit, string target)
    {
        Directory.CreateDirectory(target);
        File.Copy(license, Path.Combine(target, "LICENSE"));

        var provenance = new StringBuilder();
        provenance.AppendLine("# Provenance");
        provenance.AppendLine();
        provenance.AppendLine(CultureInfo.InvariantCulture, $"Images copied unmodified from the zxing-cpp repository, `test/samples/`, commit `{sourceCommit}`, Apache License 2.0 (see LICENSE beside this file). They are photographs, scans and renders of third-party symbols; the `.txt` beside each image is the text it encodes, as that repository's own black-box tests expect it. Nothing here was produced or checked by this library.");
        provenance.AppendLine();
        provenance.AppendLine("Imported with `dotnet run --project tools/QRImageDecodeSweep -- import-corpus <zxing-cpp-root> <commit>`.");
        provenance.AppendLine();
        provenance.AppendLine("| Set | Symbology | Images |");
        provenance.AppendLine("|---|---|---|");

        foreach (var set in sets)
        {
            var source = Path.Combine(samples, set);
            var destination = Path.Combine(target, set);
            Directory.CreateDirectory(destination);
            var count = 0;
            foreach (var image in Directory.EnumerateFiles(source).Where(Corpus.IsImage).OrderBy(static x => x, StringComparer.Ordinal))
            {
                var text = Path.ChangeExtension(image, ".txt");
                if (!File.Exists(text))
                {
                    // A few samples carry their payload as raw bytes only; they have no text to compare
                    Console.WriteLine($"skipped {set}/{Path.GetFileName(image)}: no .txt beside it");
                    continue;
                }
                File.Copy(image, Path.Combine(destination, Path.GetFileName(image)));
                File.Copy(text, Path.Combine(destination, Path.GetFileName(text)));
                count++;
            }
            provenance.AppendLine(CultureInfo.InvariantCulture, $"| `{set}` | {Corpus.SymbologyOf(set)} | {count} |");
            Console.WriteLine($"{set}: {count} images");
        }

        File.WriteAllText(Path.Combine(target, "PROVENANCE.md"), provenance.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }
}
