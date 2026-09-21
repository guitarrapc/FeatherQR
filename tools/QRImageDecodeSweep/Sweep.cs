using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

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

    public static List<ResultRow> Run(string symbology, int caseCount)
    {
        var encoders = Encoders.For(symbology);
        var kinds = Kinds.All(symbology);
        var rows = new ConcurrentBag<ResultRow>();
        var stopwatch = Stopwatch.StartNew();

        // The own-writer kind is the last one
        foreach (var caseId in Libzint.Fill(symbology, caseCount, kinds.Length - 1))
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
                }
            }
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == caseCount)
                Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {symbology}: {n}/{caseCount} cases");
        });

        // A case names its version; this library's encoder is first in the list and has to land on it, or the sample is not the one the table claims
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
}
