using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FeatherQR;
using FeatherQR.Tests;

namespace QRImageDecodeSweep;

/// <summary>
/// Sheets of two and three symbologies, composed from the sweep's cases, read by the call for every symbology (<c>SymbolDecoder.DecodeImageSymbols</c>) and by the three calls for every symbol in turn.
/// </summary>
/// <remarks>
/// Each sheet is drawn the four ways <see cref="Sheets"/> draws its sheets, and once more under each of two uneven lights.
/// Each symbol is in a cell of its own with its symbology's quiet zone, four modules for Standard QR and two for the others.
/// The texts of a sheet differ, so a text read twice, one not drawn, or one read as another symbology is a false read.
/// </remarks>
internal static class MixedSheets
{
    private static readonly string[][] Mixes =
    [
        [Symbologies.StandardQr, Symbologies.MicroQr],
        [Symbologies.StandardQr, Symbologies.RmQr],
        [Symbologies.MicroQr, Symbologies.RmQr],
        [Symbologies.StandardQr, Symbologies.MicroQr, Symbologies.RmQr],
    ];

    /// <summary>One sheet: its image, and the text and symbology of each symbol drawn on it.</summary>
    public sealed record Sheet(string Mix, string Kind, string Layout, string Light, int Index, byte[] Luminance, int Width, int Height, string[] Texts, string[] Symbologies);

    private readonly record struct Read(string Symbology, string Text);

    private sealed record Result(Sheet Sheet, int[] CallsCodes, int CallsFalse, int[] EveryCodes, int EveryFalse, string[] Beyond, string[] FalseTexts)
    {
        public double CallsMs { get; set; }
        public double EveryMs { get; set; }
    }

    private static string Show(string text) => text.Length > 24 ? text[..24] + "..." : text;

    private static string Display(string symbology) => symbology switch
    {
        Symbologies.StandardQr => "Standard QR",
        Symbologies.MicroQr => "Micro QR",
        _ => "rMQR",
    };

    /// <summary>
    /// Every sheet: for each mix, three layouts, each drawn the four ways, <paramref name="perCell"/> sheets each, every sheet also lit unevenly at two depths.
    /// The symbologies take turns along a sheet, each from cases no other sheet uses.
    /// </summary>
    public static List<Sheet> Compose(int perCell)
    {
        var sheets = new List<Sheet>();
        var nextCase = Symbologies.All.ToDictionary(static s => s, static _ => 0);
        for (var m = 0; m < Mixes.Length; m++)
        {
            var mix = Mixes[m];
            var name = string.Join(" + ", mix.Select(Display));
            (int Across, int Down)[] layouts = mix.Length == 2 ? [(2, 1), (2, 2), (4, 3)] : [(3, 1), (3, 3), (6, 3)];
            for (var l = 0; l < layouts.Length; l++)
            {
                for (var k = 0; k < Sheets.Kinds.Length; k++)
                {
                    for (var n = 0; n < perCell; n++)
                    {
                        var (across, down) = layouts[l];
                        var symbols = new List<(string Symbology, string Text, Symbol Symbol)>();
                        var texts = new HashSet<string>(StringComparer.Ordinal);
                        while (symbols.Count < across * down)
                        {
                            var symbology = mix[symbols.Count % mix.Length];
                            var definition = Cases.Create(symbology, nextCase[symbology]++);
                            // Versions 1 to 10 keep a Standard QR symbol within 57 modules a side, so a sheet of 18 symbols stays small
                            if (symbology == Symbologies.StandardQr && definition.Version > 10)
                                continue;
                            if (texts.Add(definition.Text))
                                symbols.Add((symbology, definition.Text, Encoders.For(symbology)[0].Encode(definition)!));
                        }
                        var (isDark, columns, rows) = Sheets.Layout([.. symbols.Select(static s => s.Symbol)], i => Symbologies.QuietZone(symbols[i].Symbology), across, down);
                        var (luminance, width, height) = Sheets.Kinds[k].Render(isDark, columns, rows, new Random(((m * 7 + l) * 31 + k) * 1009 + n * 7));
                        string[] drawn = [.. symbols.Select(static s => s.Text)];
                        string[] drawnSymbologies = [.. symbols.Select(static s => s.Symbology)];
                        var layout = $"{across} x {down}";
                        sheets.Add(new Sheet(name, Sheets.Kinds[k].Name, layout, "even", n, luminance, width, height, drawn, drawnSymbologies));

                        var lit = new Random(n * 7919 + width);
                        foreach (var depth in new[] { 0.6f, 0.9f })
                        {
                            var copy = (byte[])luminance.Clone();
                            var light = lit.Next(2) == 0 ? UnevenLight.Ramp : UnevenLight.Shadow;
                            var degrees = lit.Next(8) * 45f;
                            // The shadow's soft edge is measured at 4 px a module, so it is 12 px wide on every sheet
                            UnevenLightingRenderer.Light(copy, width, height, 4f, light, degrees, depth);
                            sheets.Add(new Sheet(name, Sheets.Kinds[k].Name, layout, string.Create(CultureInfo.InvariantCulture, $"{light} {degrees} {depth}"), n, copy, width, height, drawn, drawnSymbologies));
                        }
                    }
                }
            }
        }
        return sheets;
    }

    /// <summary>The three calls for every symbol in turn, their reads together.</summary>
    private static Read[] Calls(byte[] luminance, int width, int height) =>
    [
        .. QRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(Symbologies.StandardQr, r.Text)),
        .. MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(Symbologies.MicroQr, r.Text)),
        .. RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(Symbologies.RmQr, r.Text)),
    ];

    /// <summary>The call for every symbology.</summary>
    private static Read[] Every(byte[] luminance, int width, int height) =>
        [.. SymbolDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(r.Symbology switch
        {
            Symbology.StandardQR => Symbologies.StandardQr,
            Symbology.MicroQR => Symbologies.MicroQr,
            _ => Symbologies.RmQr,
        }, r.Text))];

    /// <summary>Codes read by symbology, in the order of <see cref="Symbologies.All"/>, and the false reads.</summary>
    private static (int[] Codes, List<string> FalseTexts) Score(Sheet sheet, Read[] reads)
    {
        var codes = new int[Symbologies.All.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var falseTexts = new List<string>();
        foreach (var read in reads)
        {
            var drawn = Array.IndexOf(sheet.Texts, read.Text);
            if (drawn >= 0 && sheet.Symbologies[drawn] == read.Symbology && seen.Add(read.Text))
            {
                codes[Array.IndexOf(Symbologies.All, read.Symbology)]++;
                continue;
            }
            falseTexts.Add($"{Display(read.Symbology)} \"{read.Text}\"{(drawn >= 0 ? " (twice or as another symbology)" : "")}");
        }
        return (codes, falseTexts);
    }

    public static string Run(int perCell, bool timed)
    {
        var stopwatch = Stopwatch.StartNew();
        var sheets = Compose(perCell);
        Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] mixed: {sheets.Count} sheets composed");
        var results = new ConcurrentBag<Result>();
        Parallel.ForEach(sheets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, sheet =>
        {
            var calls = Calls(sheet.Luminance, sheet.Width, sheet.Height);
            var every = Every(sheet.Luminance, sheet.Width, sheet.Height);
            var (callsCodes, callsFalse) = Score(sheet, calls);
            var (everyCodes, everyFalse) = Score(sheet, every);
            var standard = calls.Where(static r => r.Symbology == Symbologies.StandardQr).Select(static r => r.Text).ToHashSet(StringComparer.Ordinal);
            string[] beyond = [.. every.Where(r => r.Symbology == Symbologies.StandardQr && !standard.Contains(r.Text)).Select(static r => r.Text)];
            results.Add(new Result(sheet, callsCodes, callsFalse.Count, everyCodes, everyFalse.Count, beyond, [.. callsFalse.Select(static t => "three calls " + t), .. everyFalse.Select(static t => "every symbology " + t)]));
        });
        Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] mixed: read");

        var ordered = results.OrderBy(static r => Array.FindIndex(Mixes, m => string.Join(" + ", m.Select(Display)) == r.Sheet.Mix))
            .ThenBy(static r => r.Sheet.Kind, StringComparer.Ordinal).ThenBy(static r => r.Sheet.Layout.Length).ThenBy(static r => r.Sheet.Layout, StringComparer.Ordinal)
            .ThenBy(static r => r.Sheet.Index).ThenBy(static r => r.Sheet.Light, StringComparer.Ordinal).ToList();
        if (timed)
        {
            // One at a time, so the times are not shared with other sheets' reads
            foreach (var result in ordered)
            {
                var sheet = result.Sheet;
                result.CallsMs = Sheets.Fastest(() => Calls(sheet.Luminance, sheet.Width, sheet.Height));
                result.EveryMs = Sheets.Fastest(() => Every(sheet.Luminance, sheet.Width, sheet.Height));
            }
            Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] mixed: timed");
        }
        return Table(ordered, timed);
    }

    private static string Table(List<Result> results, bool timed)
    {
        var sb = new StringBuilder();
        var drawnBy = string.Join(", ", Symbologies.All.Select(s => string.Create(CultureInfo.InvariantCulture, $"{Display(s)} {results.Sum(r => r.Sheet.Symbologies.Count(x => x == s)):N0}")));
        sb.AppendLine("## Every symbology, sheets of two and three symbologies");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{results.Count:N0} sheets, {results.Sum(static r => r.Sheet.Texts.Length):N0} codes ({drawnBy}). Every symbology is the one call for every symbol of any symbology, and three calls each symbology's call for every symbol in turn, their reads together. A false read is a text not drawn on the sheet, one read twice, or one read as another symbology. Beyond Standard QR's call is a Standard QR code the one call reads and Standard QR's own call does not.");
        sb.AppendLine();
        sb.Append("| Symbologies | Kind | Sheets | Codes | Three calls | False | Every symbology | False | Standard QR | Micro QR | rMQR | Beyond Standard QR's call |");
        if (timed)
            sb.Append(" Three calls ms | Every symbology ms | Every / three |");
        sb.AppendLine();
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|" + (timed ? "---|---|---|" : ""));

        IEnumerable<IGrouping<(string, string), Result>> groups = results.GroupBy(static r => (r.Sheet.Mix, r.Sheet.Kind))
            .Concat(results.GroupBy(static r => (r.Sheet.Light == "even" ? "**Lit evenly**" : "**Lit unevenly**", "")).OrderBy(static g => g.Key.Item1, StringComparer.Ordinal))
            .Append(results.GroupBy(static _ => ("**All**", "")).Single());
        foreach (var group in groups)
        {
            var g = group.ToList();
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key.Item1} | {group.Key.Item2} | {g.Count:N0} | {g.Sum(static r => r.Sheet.Texts.Length):N0} | {g.Sum(static r => r.CallsCodes.Sum()):N0} | {g.Sum(static r => r.CallsFalse):N0} | {g.Sum(static r => r.EveryCodes.Sum()):N0} | {g.Sum(static r => r.EveryFalse):N0} |");
            for (var s = 0; s < Symbologies.All.Length; s++)
                sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(r => r.EveryCodes[s]):N0} |");
            sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.Beyond.Length):N0} |");
            if (timed)
                sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.CallsMs):F1} | {g.Sum(static r => r.EveryMs):F1} | {g.Sum(static r => r.EveryMs) / g.Sum(static r => r.CallsMs):F2} |");
            sb.AppendLine();
        }
        sb.AppendLine();
        var byCalls = string.Join(", ", Symbologies.All.Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"{Display(s)} {results.Sum(r => r.CallsCodes[i]):N0}")));
        sb.AppendLine(CultureInfo.InvariantCulture, $"Three calls by symbology: {byCalls}.");
        sb.AppendLine();

        static string Name(Sheet s) => $"{s.Mix}, {s.Kind}, {s.Layout}, sheet {s.Index}, {s.Light}";
        var beyond = results.Where(static r => r.Beyond.Length > 0).ToList();
        if (beyond.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Standard QR codes beyond Standard QR's call, on {beyond.Count:N0} sheets:");
            sb.AppendLine();
            foreach (var r in beyond)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {Name(r.Sheet)}: {string.Join(", ", r.Beyond.Select(static t => $"\"{Show(t)}\""))}");
            sb.AppendLine();
        }
        var falseReads = results.Where(static r => r.FalseTexts.Length > 0).ToList();
        if (falseReads.Count > 0)
        {
            sb.AppendLine("False reads:");
            sb.AppendLine();
            foreach (var r in falseReads)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {Name(r.Sheet)}: {string.Join(", ", r.FalseTexts)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
