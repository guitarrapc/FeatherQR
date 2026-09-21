using System.Text;
using QRImageDecodeSweep;

// Measures this library's image decoders against other readers, image for image.
// See .github/docs/specs/qrcode-test-fixtures.md ("Image decode sweep") for what it measures and why.
//
// Usage: dotnet run -c Release --project tools/QRImageDecodeSweep -- <command>
//   sweep [qr|micro|rmqr|all] [cases] [outDir]   synthetic renders of every encoder's symbols
//   corpus [outDir]                              the committed real-image sets
//   compare <before.csv> <after.csv>             two result files of the same run, image for image
//   import-corpus <zxing-cpp-root> <commit>      copies the sample sets in, with provenance

if (Libzint.IsWorker(args))
    return Libzint.RunAsWorker(args);

var command = args.Length == 0 ? "sweep" : args[0];
var repoRoot = FindRepoRoot();
var defaultOut = Path.Combine(repoRoot, "tools", "QRImageDecodeSweep", "bin", "results");

switch (command)
{
    case "sweep":
        {
            var which = args.Length > 1 ? args[1] : "all";
            var outDir = args.Length > 3 ? args[3] : defaultOut;

            // A typo must not pass for a run that measured nothing
            var caseCount = 0;
            if ((which != "all" && !Symbologies.All.Contains(which)) || (args.Length > 2 && (!int.TryParse(args[2], out caseCount) || caseCount < 1)))
            {
                Console.Error.WriteLine($"sweep takes one of all, {string.Join(", ", Symbologies.All)}, then a positive case count; got '{string.Join(' ', args.Skip(1).Take(2))}'");
                return 1;
            }
            Qrtool.Locate(repoRoot);

            var markdown = new StringBuilder();
            var incomplete = false;
            foreach (var symbology in Symbologies.All)
            {
                if (which != "all" && which != symbology)
                    continue;
                var cases = args.Length > 2 ? caseCount : Symbologies.DefaultCases(symbology);
                var rows = Sweep.Run(symbology, cases);
                incomplete |= rows.Any(static r => r.Status.EndsWith(Libzint.DiedStatus, StringComparison.Ordinal));
                var csv = Path.Combine(outDir, $"sweep-{symbology}.csv");
                Csv.Write(csv, Sweep.KeyColumns, rows);
                Console.WriteLine($"wrote {csv}");
                markdown.Append(Report.Table($"{symbology}, {cases} cases", rows, rowColumn: 3, splitColumn: 2, withZXingNet: symbology == Symbologies.StandardQr));
            }
            Finish(Path.Combine(outDir, $"sweep-{which}.md"), markdown.ToString());
            if (!incomplete)
                return 0;
            Console.Error.WriteLine("A case was given up, so this run's images are not the set other runs have. Run it again before comparing.");
            return 2;
        }
    case "corpus":
        {
            var outDir = args.Length > 1 ? args[1] : defaultOut;
            var rows = Corpus.Run(repoRoot);
            var csv = Path.Combine(outDir, "corpus.csv");
            Csv.Write(csv, Corpus.KeyColumns, rows);
            Console.WriteLine($"wrote {csv}");

            var markdown = new StringBuilder();
            foreach (var symbology in Symbologies.All)
            {
                var part = rows.Where(r => r.Key[0] == symbology).ToList();
                if (part.Count > 0)
                    markdown.Append(Report.Table($"{symbology}, real images by rotation", part, rowColumn: 2, splitColumn: 4, withZXingNet: symbology == Symbologies.StandardQr));
            }
            var notes = rows.Where(static r => r.Note is not null).ToList();
            if (notes.Count > 0)
            {
                markdown.AppendLine("## Decoded, with another text");
                markdown.AppendLine();
                foreach (var row in notes)
                    markdown.AppendLine($"- {row.Key[2]}/{row.Key[3]} at {row.Key[4]}: {row.Note}");
                markdown.AppendLine();
            }
            return Finish(Path.Combine(outDir, "corpus.md"), markdown.ToString());
        }
    case "compare" when args.Length == 3:
        return Compare.Run(args[1], args[2]);
    case "import-corpus" when args.Length == 3:
        return CorpusImporter.Run(repoRoot, args[1], args[2]);
    default:
        Console.Error.WriteLine("Usage: sweep [qr|micro|rmqr|all] [cases] [outDir] | corpus [outDir] | compare <before.csv> <after.csv> | import-corpus <zxing-cpp-root> <commit>");
        return 1;
}

static int Finish(string path, string markdown)
{
    File.WriteAllText(path, markdown.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    Console.WriteLine();
    Console.WriteLine(markdown);
    Console.WriteLine($"wrote {path}");
    return 0;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "FeatherQR.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Repository root (FeatherQR.slnx) not found above the tool output directory.");
}
