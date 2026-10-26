using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QRImageDecodeSweep;

/// <summary>
/// Every case through every encoder and every kind, read by the three readers.
/// A case fixes the payload, the level and every render parameter, so the encoders are compared on the same image but for their own matrix, and two runs, or two trees, are compared render for render.
/// </summary>
internal static class Sweep
{
    public static readonly string[] KeyColumns = ["symbology", "case", "encoder", "kind", "version", "ecc", "mode", "ppm", "width", "height"];

    /// <summary>Seeds are arithmetic on the indices: <c>GetHashCode</c> is randomized per process and would draw a different sample each run.</summary>
    public static Random RenderRandom(int caseId, int kindIndex) => new(caseId * 1009 + kindIndex * 31 + 7);

    /// <param name="dumpDir">Where to write every Standard QR render for a reader run in its own harness, or null.</param>
    public static List<ResultRow> Run(string symbology, int caseCount, string? dumpDir = null)
    {
        // The other readers in their own harness read Standard QR alone
        dumpDir = symbology == Symbologies.StandardQr ? dumpDir : null;
        var encoders = Encoders.For(symbology);
        var kinds = Kinds.All(symbology);
        var rows = new ConcurrentBag<ResultRow>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var caseId in Libzint.Fill(symbology, caseCount, Array.FindIndex(kinds, static k => k.Name == Kinds.OwnWriter)))
        {
            var definition = Cases.Create(symbology, caseId);
            rows.Add(Failure(definition, encoders.First(static e => e.Name == Libzint.Name), "(encode)", definition.VersionName, $"EncodeFailed: {Libzint.Name}{Libzint.DiedStatus}"));
        }

        var done = 0;
        var offVersion = 0;
        Parallel.For(0, caseCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, caseId =>
        {
            var definition = Cases.Create(symbology, caseId);
            foreach (var encoder in encoders)
            {
                Symbol? symbol;
                try
                {
                    symbol = encoder.Encode(definition);
                }
                catch (Exception ex)
                {
                    rows.Add(Failure(definition, encoder, "(encode)", definition.VersionName, "EncodeFailed: " + ex.Message));
                    continue;
                }
                if (symbol is null)
                    continue;
                if (ReferenceEquals(encoder, encoders[0]) && symbol.VersionName != definition.VersionName)
                    Interlocked.Increment(ref offVersion);

                for (var k = 0; k < kinds.Length; k++)
                {
                    Rendered? rendered;
                    try
                    {
                        rendered = kinds[k].Render(symbol, definition, encoder, RenderRandom(caseId, k));
                    }
                    catch (Exception ex)
                    {
                        rows.Add(Failure(definition, encoder, kinds[k].Name, symbol.VersionName, "RenderFailed: " + ex.Message));
                        continue;
                    }
                    if (rendered is null)
                        continue;

                    var key = Key(definition, encoder, kinds[k].Name, symbol.VersionName, rendered.PixelsPerModule, rendered.Width, rendered.Height);
                    rows.Add(Readers.Read(key, symbology, rendered, definition.Text));
                    if (dumpDir is not null)
                        Pixels.WritePng(rendered.Luminance, rendered.Width, rendered.Height, Path.Combine(dumpDir, DumpDirectory(kinds[k].Name), DumpFile(caseId, encoder.Name)));
                }
            }
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == caseCount)
                Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {symbology}: {n}/{caseCount} cases");
        });

        // A case names its version. This library's encoder is first in the list and has to land on it, or the sample is not the one the table claims.
        Console.WriteLine($"{symbology}: {offVersion} of {caseCount} cases are not the version they ask for");

        return [.. rows.OrderBy(static r => r.Key[3], StringComparer.Ordinal).ThenBy(static r => r.Key[2], StringComparer.Ordinal).ThenBy(static r => int.Parse(r.Key[1], CultureInfo.InvariantCulture))];
    }

    private static string[] Key(CaseDefinition d, Encoder encoder, string kind, string version, float ppm, int width, int height) =>
    [
        d.Symbology,
        d.CaseId.ToString(CultureInfo.InvariantCulture),
        encoder.Name,
        kind,
        version,
        d.Ecc,
        d.Mode,
        ppm.ToString("0.###", CultureInfo.InvariantCulture),
        width.ToString(CultureInfo.InvariantCulture),
        height.ToString(CultureInfo.InvariantCulture),
    ];

    private static ResultRow Failure(CaseDefinition d, Encoder encoder, string kind, string version, string status) =>
        new(Key(d, encoder, kind, version, 0, 0, 0), "", status.ReplaceLineEndings(" "), false, false, false, false);

    /// <summary>A kind's directory among the dumped renders: its name with the characters a file name cannot hold as <c>_</c>. A reader's lines name it as their category.</summary>
    public static string DumpDirectory(string kind)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(kind.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    public static string DumpFile(int caseId, string encoder) => string.Create(CultureInfo.InvariantCulture, $"{caseId:0000}-{encoder}.png");

    /// <summary>Takes <c>--dump &lt;dir&gt;</c> out of <paramref name="arguments"/>.</summary>
    public static string? DumpFromArguments(List<string> arguments)
    {
        var at = arguments.IndexOf("--dump");
        if (at < 0)
            return null;
        if (at + 1 >= arguments.Count)
            throw new ArgumentException("--dump takes a directory");
        var dir = Path.GetFullPath(arguments[at + 1]);
        arguments.RemoveRange(at, 2);
        return dir;
    }

    /// <summary>
    /// Each reader of <paramref name="lines"/> on the dumped renders, by kind, beside the three readers here: a render reads when one of its texts is the case's text.
    /// The lines name a render by its kind's directory and its file, so they pair with any run of the same cases.
    /// </summary>
    public static string LinesMarkdown(string symbology, IReadOnlyList<ResultRow> rows, ReaderLines lines)
    {
        var sb = new StringBuilder();
        var read = rows.Where(static r => !Report.IsFailure(r.Status)).ToList();
        var texts = new Dictionary<int, string>();
        foreach (var name in lines.Readers)
        {
            var scored = new List<(ResultRow Row, bool Held, bool Read, string? AnotherText)>(read.Count);
            foreach (var row in read)
            {
                var caseId = int.Parse(row.Key[1], CultureInfo.InvariantCulture);
                if (!texts.TryGetValue(caseId, out var expected))
                    texts[caseId] = expected = Cases.Create(symbology, caseId).Text;
                var line = lines.Find(name, DumpDirectory(row.Key[3]), DumpFile(caseId, row.Key[2]));
                var ok = line is not null && Array.IndexOf(line.Texts, expected) >= 0;
                scored.Add((row, line is not null, ok, line is { Texts.Length: > 0 } && !ok ? Readers.Difference(expected, line.Texts[0]) : null));
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"## {name}, from result lines");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Run in its own harness on the dumped renders: {scored.Count(static s => s.Held):N0} of the {scored.Count:N0} renders have a line. Gap and reverse are against FeatherQR.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Kind | Renders | FeatherQR | zxing-cpp | ZXing.Net | {name} | Gap | Reverse | Another text |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var group in scored.GroupBy(static s => s.Row.Key[3]).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(scored.GroupBy(static _ => "**All**").Single()))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count():N0} | {group.Count(static s => s.Row.FeatherQr):N0} | {group.Count(static s => s.Row.ZXingCpp):N0} | {group.Count(static s => s.Row.ZXingNet):N0} | {group.Count(static s => s.Read):N0} | {group.Count(static s => s.Read && !s.Row.FeatherQr):N0} | {group.Count(static s => s.Row.FeatherQr && !s.Read):N0} | {group.Count(static s => s.AnotherText is not null):N0} |");
            sb.AppendLine();

            AppendList(sb, $"Gap: {name} reads, FeatherQR does not", scored.Where(static s => s.Read && !s.Row.FeatherQr).Select(static s => $"{s.Row.Key[3]}, {string.Join(", ", s.Row.Key[1..3])}, {s.Row.Key[4]}: {s.Row.Status}"));
            AppendList(sb, $"{name} decoded, with another text", scored.Where(static s => s.AnotherText is not null).Select(static s => $"{s.Row.Key[3]}, {string.Join(", ", s.Row.Key[1..3])}: {s.AnotherText}"));
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
}
