using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FeatherQR;
using FeatherQR.Tests;

namespace QRImageDecodeSweep;

/// <summary>
/// Sheets of several Micro QR or rMQR symbols, composed from the sweep's cases and drawn as one image, and what this library reads of them.
/// The one-symbol call is read again until it reads nothing, each read symbol painted over (<see cref="ReadAgain"/>), and the call that reads every symbol (<see cref="Every"/>) is scored beside it.
/// </summary>
/// <remarks>
/// A sheet holds one symbology, each symbol in its own cell with the two-module quiet zone both specifications ask for, so neighbours are four modules apart.
/// The texts of a sheet differ, so a text read twice, or one not drawn, is a false read.
/// </remarks>
internal static class Sheets
{
    /// <summary>A symbol a reader returned: its text, where it lies, and its size in modules.</summary>
    public readonly record struct Found(string Text, SymbolCorners Corners, int Columns, int Rows);

    /// <summary>A reader of every symbol of an image in one call.</summary>
    public delegate Found[] EveryReader(string symbology, byte[] luminance, int width, int height);

    /// <summary>One sheet: its image and the texts drawn on it.</summary>
    public sealed record Sheet(string Symbology, string Kind, string Layout, int Index, byte[] Luminance, int Width, int Height, string[] Texts);

    /// <summary>Calls <see cref="ReadAgain"/> makes of one image at most: the fullest sheet holds 100 symbols.</summary>
    private const int ReadAgainLimit = 128;

    private static (int Across, int Down)[] Layouts(string symbology) => symbology == Symbologies.MicroQr
        ? [(2, 1), (3, 1), (2, 2), (3, 3), (5, 5), (6, 10), (10, 10)]
        // rMQR is up to 139 modules wide, so its sheets are five across at most
        : [(2, 1), (3, 1), (2, 2), (3, 3), (5, 5), (5, 12), (5, 20)];

    private static float Range(Random random, float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);

    /// <summary>How a sheet is drawn: the module grid of the whole sheet, quiet zones included, into an image.</summary>
    private sealed record SheetKind(string Name, Func<Func<int, int, bool>, int, int, Random, (byte[] Luminance, int Width, int Height)> Render);

    private static readonly SheetKind[] Kinds =
    [
        new("01 crisp nearest-neighbour 2.00-4.00", static (isDark, columns, rows, random) =>
            NearestNeighbourRenderer.Render(isDark, columns, rows, Range(random, 2f, 4f), (float)random.NextDouble(), (float)random.NextDouble())),
        new("02 supersampled, any rotation 3.00-5.00", static (isDark, columns, rows, random) =>
            SupersampledRenderer.Render(isDark, columns, rows, Range(random, 3f, 5f), Range(random, 0f, 360f))),
        new("03 supersampled, any rotation, keystone <= 6 % 3.00-6.00", static (isDark, columns, rows, random) =>
            SupersampledRenderer.Render(isDark, columns, rows, Range(random, 3f, 6f), Range(random, 0f, 360f), Range(random, 0f, 0.06f))),
        // The card is the whole sheet. Its own quiet zone is the outer cells', so the card adds none
        new("04 camera, phone mix 3.00-6.00", static (isDark, columns, rows, random) =>
        {
            var ppm = Range(random, 3f, 6f);
            var shot = new CameraRenderer.Shot(ppm, Range(random, 0f, 35f), Range(random, 0f, 360f), Range(random, 0f, 360f), BowModules: (random.Next(2) * 2 - 1) * Range(random, 0f, 1.5f), Distortion: -Range(random, 0f, 0.1f), BlurSigma: ppm * Range(random, 0f, 0.3f), NoiseSigma: Range(random, 0f, 8f), Seed: random.Next());
            var photo = CameraRenderer.Render(isDark, columns, rows, quietZone: 0, shot);
            return (photo.Luminance, photo.Width, photo.Height);
        }),
    ];

    /// <summary>Every sheet of <paramref name="symbology"/>: <paramref name="perCell"/> of each layout and kind, each drawn from cases no other sheet uses.</summary>
    public static IEnumerable<Sheet> Compose(string symbology, int perCell)
    {
        var layouts = Layouts(symbology);
        var nextCase = 0;
        for (var l = 0; l < layouts.Length; l++)
        {
            for (var k = 0; k < Kinds.Length; k++)
            {
                for (var n = 0; n < perCell; n++)
                {
                    var (across, down) = layouts[l];
                    var symbols = new List<(string Text, Symbol Symbol)>();
                    var texts = new HashSet<string>(StringComparer.Ordinal);
                    while (symbols.Count < across * down)
                    {
                        var definition = Cases.Create(symbology, nextCase++);
                        if (texts.Add(definition.Text))
                            symbols.Add((definition.Text, Encode(definition)));
                    }
                    var (isDark, columns, rows) = Layout(symbols.Select(static s => s.Symbol).ToArray(), across, down);
                    var random = new Random(((l * 31) + k) * 1009 + n * 7 + symbology.Length);
                    var (luminance, width, height) = Kinds[k].Render(isDark, columns, rows, random);
                    yield return new Sheet(symbology, Kinds[k].Name, $"{across} x {down}", n, luminance, width, height, [.. symbols.Select(static s => s.Text)]);
                }
            }
        }
    }

    private static Symbol Encode(CaseDefinition definition)
    {
        if (definition.Symbology == Symbologies.MicroQr)
        {
            var micro = MicroQRCodeGenerator.Create(definition.Text, Enum.Parse<MicroQREccLevel>(definition.Ecc), new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, Version = (MicroQRVersion)definition.Version });
            return Modules(micro.Size, micro.Size, (r, c) => micro[r, c]);
        }
        var rmqr = RmQRCodeGenerator.Create(definition.Text, Enum.Parse<RmQREccLevel>(definition.Ecc), new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = (RmQRVersion)definition.Version });
        return Modules(rmqr.Width, rmqr.Height, (r, c) => rmqr[r, c]);

        static Symbol Modules(int width, int height, Func<int, int, bool> isDark)
        {
            var modules = new bool[width * height];
            for (var r = 0; r < height; r++)
                for (var c = 0; c < width; c++)
                    modules[r * width + c] = isDark(r, c);
            return new Symbol(modules, width, height, "");
        }
    }

    /// <summary>
    /// The symbols in rows of <paramref name="across"/>, each in a cell of its own size plus a two-module quiet zone all round, each row as tall as its tallest cell.
    /// </summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows) Layout(Symbol[] symbols, int across, int down)
    {
        const int QuietZone = 2;
        var placed = new (int Left, int Top, Symbol Symbol)[symbols.Length];
        int top = 0, columns = 0;
        for (var row = 0; row < down; row++)
        {
            int left = 0, tallest = 0;
            for (var i = row * across; i < (row + 1) * across; i++)
            {
                placed[i] = (left + QuietZone, top + QuietZone, symbols[i]);
                left += symbols[i].Width + 2 * QuietZone;
                tallest = Math.Max(tallest, symbols[i].Height + 2 * QuietZone);
            }
            columns = Math.Max(columns, left);
            top += tallest;
        }

        // Each module looks up the symbol that covers it, by cell, so a sheet of a hundred costs a lookup a module
        var owner = new int[top * columns];
        Array.Fill(owner, -1);
        for (var i = 0; i < placed.Length; i++)
        {
            var (l, t, s) = placed[i];
            for (var r = 0; r < s.Height; r++)
                owner.AsSpan((t + r) * columns + l, s.Width).Fill(i);
        }
        return ((row, column) =>
        {
            var i = owner[row * columns + column];
            return i >= 0 && placed[i].Symbol[row - placed[i].Top, column - placed[i].Left];
        }, columns, top);
    }

    /// <summary>The call that reads every symbol of an image.</summary>
    public static Found[] Every(string symbology, byte[] luminance, int width, int height) => symbology == Symbologies.MicroQr
        ? [.. MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Found(r.Text, r.Info.Corners, 2 * (int)r.Info.Version + 9, 2 * (int)r.Info.Version + 9))]
        : [.. RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Found(r.Text, r.Info.Corners, 0, 0))];

    /// <summary>The one-symbol call's read of an image, or null.</summary>
    public static Found? ReadOne(string symbology, byte[] luminance, int width, int height, out string status)
    {
        if (symbology == Symbologies.MicroQr)
        {
            var read = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            status = read ? "Success" : info.Status.ToString();
            var size = 2 * (int)info.Version + 9;
            return read ? new Found(text, info.Corners, size, size) : null;
        }
        else
        {
            var read = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            status = read ? "Success" : info.Status.ToString();
            if (!read)
                return null;
            var parts = info.Version.ToString()[1..].Split('x');
            return new Found(text, info.Corners, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[0], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Every symbol the one-symbol call reads of an image: each read symbol painted over and the image read again, until a call reads nothing.
    /// </summary>
    /// <param name="luminance">The image, painted in place.</param>
    public static List<Found> ReadAgain(string symbology, byte[] luminance, int width, int height)
    {
        var reads = new List<Found>();
        while (reads.Count < ReadAgainLimit && ReadOne(symbology, luminance, width, height, out _) is { } found)
        {
            reads.Add(found);
            PaintOver(luminance, width, height, found);
        }
        return reads;
    }

    /// <summary>
    /// Fills a read symbol's outline, a module wider on every side, with the light level of the pixels it holds (<see cref="BoofCv"/> paints the same way).
    /// The outline is the corners extended by one module along each of the symbol's own axes, so a rectangle grows by a module on its short sides too.
    /// </summary>
    public static void PaintOver(byte[] luminance, int width, int height, Found found)
    {
        var c = found.Corners;
        (double X, double Y) At(double a, double b) =>
            ((1 - a) * (1 - b) * c.TopLeft.X + a * (1 - b) * c.TopRight.X + a * b * c.BottomRight.X + (1 - a) * b * c.BottomLeft.X,
             (1 - a) * (1 - b) * c.TopLeft.Y + a * (1 - b) * c.TopRight.Y + a * b * c.BottomRight.Y + (1 - a) * b * c.BottomLeft.Y);
        double du = 1.0 / found.Columns, dv = 1.0 / found.Rows;
        (double X, double Y)[] outline = [At(-du, -dv), At(1 + du, -dv), At(1 + du, 1 + dv), At(-du, 1 + dv)];

        var top = Math.Clamp((int)Math.Floor(outline.Min(static p => p.Y)), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(outline.Max(static p => p.Y)), 0, height - 1);
        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var y = top; y <= bottom; y++)
        {
            var row = BoofCv.RowInside(luminance, width, outline, y);
            foreach (var pixel in row)
                histogram[pixel]++;
            count += row.Length;
        }
        // About half a symbol's pixels are light, so the upper quartile is the middle of the light ones
        var level = 255;
        for (var above = 0; level > 0 && above + histogram[level] <= count / 4; level--)
            above += histogram[level];
        for (var y = top; y <= bottom; y++)
            BoofCv.RowInside(luminance, width, outline, y).Fill((byte)level);
    }

    /// <summary>What one sheet gave: codes and false reads of the read-again loop and of the every-symbol reader, and their times.</summary>
    private sealed record SheetResult(Sheet Sheet, int AgainCodes, int AgainFalse, bool FirstRead, int EveryCodes, int EveryFalse, string[] FalseTexts)
    {
        public double OneMs { get; set; }
        public double AgainMs { get; set; }
        public double LastMs { get; set; }
        public double EveryMs { get; set; }
    }

    private static (int Codes, int False, List<string> FalseTexts) Score(string[] drawn, IEnumerable<string> read)
    {
        var expected = new HashSet<string>(drawn, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var falseTexts = new List<string>();
        foreach (var text in read)
        {
            if (expected.Contains(text) && seen.Add(text))
                continue;
            falseTexts.Add(text);
        }
        return (seen.Count, falseTexts.Count, falseTexts);
    }

    /// <summary>The sheets of each symbology, read again and by <paramref name="every"/> when given, as Markdown.</summary>
    public static string Run(string which, int perCell, bool timed, EveryReader? every)
    {
        var markdown = new StringBuilder();
        foreach (var symbology in new[] { Symbologies.MicroQr, Symbologies.RmQr })
        {
            if (which != "all" && which != symbology)
                continue;
            var stopwatch = Stopwatch.StartNew();
            var sheets = Compose(symbology, perCell).ToList();
            Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {symbology}: {sheets.Count} sheets composed");
            var results = new ConcurrentBag<SheetResult>();
            Parallel.ForEach(sheets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, sheet =>
            {
                var again = ReadAgain(symbology, (byte[])sheet.Luminance.Clone(), sheet.Width, sheet.Height);
                var (againCodes, againFalse, againFalseTexts) = Score(sheet.Texts, again.Select(static f => f.Text));
                var (everyCodes, everyFalse, everyFalseTexts) = every is null ? (0, 0, []) : Score(sheet.Texts, every(symbology, sheet.Luminance, sheet.Width, sheet.Height).Select(static f => f.Text));
                results.Add(new SheetResult(sheet, againCodes, againFalse, again.Count > 0, everyCodes, everyFalse, [.. againFalseTexts, .. everyFalseTexts]));
            });
            Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {symbology}: read");

            var ordered = results.OrderBy(static r => r.Sheet.Layout.Length).ThenBy(static r => r.Sheet.Layout, StringComparer.Ordinal).ThenBy(static r => r.Sheet.Kind, StringComparer.Ordinal).ThenBy(static r => r.Sheet.Index).ToList();
            if (timed)
            {
                // One at a time, so the times are not shared with other sheets' reads
                foreach (var result in ordered)
                    Time(result, every);
                Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {symbology}: timed");
            }
            markdown.Append(Table(symbology, ordered, timed, every is not null));
        }
        return markdown.ToString();
    }

    private static void Time(SheetResult result, EveryReader? every)
    {
        var sheet = result.Sheet;
        result.OneMs = Fastest(() => ReadOne(sheet.Symbology, sheet.Luminance, sheet.Width, sheet.Height, out _));
        var painted = (byte[])sheet.Luminance.Clone();
        ReadAgain(sheet.Symbology, painted, sheet.Width, sheet.Height);
        result.LastMs = Fastest(() => ReadOne(sheet.Symbology, painted, sheet.Width, sheet.Height, out _));

        // Each run paints, so each starts from a copy made outside its time
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var copy = (byte[])sheet.Luminance.Clone();
            var start = Stopwatch.GetTimestamp();
            ReadAgain(sheet.Symbology, copy, sheet.Width, sheet.Height);
            if (i > 0)
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        result.AgainMs = best;
        if (every is not null)
            result.EveryMs = Fastest(() => every(sheet.Symbology, sheet.Luminance, sheet.Width, sheet.Height));
    }

    public static double Fastest(Action call)
    {
        call();
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var start = Stopwatch.GetTimestamp();
            call();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return best;
    }

    private static string Table(string symbology, List<SheetResult> results, bool timed, bool withEvery)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {symbology}, sheets of several symbols");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{results.Count:N0} sheets, {results.Sum(static r => r.Sheet.Texts.Length):N0} codes. Read again is the one-symbol call repeated, each read symbol painted over, until a call reads nothing. A false read is a text not drawn on the sheet, or one read twice.");
        sb.AppendLine();

        sb.Append("| Layout | Kind | Sheets | Codes | Read again | False |");
        if (withEvery)
            sb.Append(" Every | False |");
        if (timed)
            sb.Append(" One call ms | Again ms | Last call ms |").Append(withEvery ? " Every ms | Every / one |" : "");
        sb.AppendLine();
        sb.Append("|---|---|---|---|---|---|").Append(withEvery ? "---|---|" : "").AppendLine(timed ? (withEvery ? "---|---|---|---|---|" : "---|---|---|") : "");

        foreach (var group in results.GroupBy(static r => (r.Sheet.Layout, r.Sheet.Kind)).Append(results.GroupBy(static _ => ("**All**", "")).Single()))
        {
            var g = group.ToList();
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key.Item1} | {group.Key.Item2} | {g.Count:N0} | {g.Sum(static r => r.Sheet.Texts.Length):N0} | {g.Sum(static r => r.AgainCodes):N0} | {g.Sum(static r => r.AgainFalse):N0} |");
            if (withEvery)
                sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.EveryCodes):N0} | {g.Sum(static r => r.EveryFalse):N0} |");
            if (timed)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.OneMs):F1} | {g.Sum(static r => r.AgainMs):F1} | {g.Sum(static r => r.LastMs):F1} |");
                if (withEvery)
                    sb.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.EveryMs):F1} | {g.Sum(static r => r.EveryMs) / g.Sum(static r => r.OneMs):F2} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        var falseReads = results.Where(static r => r.FalseTexts.Length > 0).ToList();
        if (falseReads.Count > 0)
        {
            sb.AppendLine("False reads:");
            sb.AppendLine();
            foreach (var r in falseReads)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Sheet.Layout}, {r.Sheet.Kind}, sheet {r.Sheet.Index}: {string.Join(", ", r.FalseTexts.Select(static t => $"\"{t}\""))}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The sweep's one-symbol renders of this library's own symbols and the committed real images: each that reads is painted over and read again, where any read is a false one.
    /// With <paramref name="every"/>, a second result of it is a false read too.
    /// </summary>
    public static string Again(string repoRoot, string which, int cases, bool timed, EveryReader? every)
    {
        var markdown = new StringBuilder();
        foreach (var symbology in new[] { Symbologies.MicroQr, Symbologies.RmQr })
        {
            if (which != "all" && which != symbology)
                continue;
            var images = new List<(string Kind, string Name, Rendered Image, string Text)>();
            var kinds = QRImageDecodeSweep.Kinds.All(symbology);
            var encoder = Encoders.For(symbology)[0];
            for (var caseId = 0; caseId < cases; caseId++)
            {
                var definition = Cases.Create(symbology, caseId);
                var symbol = encoder.Encode(definition)!;
                for (var k = 0; k < kinds.Length; k++)
                {
                    if (kinds[k].Render(symbol, definition, encoder, Sweep.RenderRandom(caseId, k)) is { } rendered)
                        images.Add((kinds[k].Name, caseId.ToString(CultureInfo.InvariantCulture), rendered, definition.Text));
                }
            }
            foreach (var (_, set, path) in Corpus.Images([("corpus", Path.Combine(repoRoot, Corpus.RelativeRoot, "zxing-cpp-samples"))]).Where(i => Corpus.SymbologyOf(i.Set) == symbology))
            {
                using var bitmap = SkiaSharp.SKBitmap.Decode(path);
                var (luminance, width, height) = Pixels.ToLuminance(bitmap);
                var text = File.ReadAllText(Path.ChangeExtension(path, ".txt"), Encoding.UTF8);
                for (var turns = 0; turns < 4; turns++)
                {
                    var (l, w, h) = NearestNeighbourRenderer.Turn(luminance, width, height, turns, mirror: false);
                    images.Add(("corpus " + set, $"{Path.GetFileName(path)} at {turns * 90}", new Rendered(l, w, h, 0), text));
                }
            }
            Console.WriteLine($"{symbology}: {images.Count} images");

            var rows = new ConcurrentBag<(string Kind, string Name, bool Read, string? Second, int EveryCount, string[] EveryOthers, double FirstMs, double SecondMs, double EveryMs)>();
            Parallel.ForEach(images, new ParallelOptions { MaxDegreeOfParallelism = timed ? 1 : Math.Max(1, Environment.ProcessorCount - 2) }, image =>
            {
                var luminance = image.Image.Luminance;
                var first = ReadOne(symbology, luminance, image.Image.Width, image.Image.Height, out _);
                var read = first?.Text == image.Text;
                string? second = null;
                double firstMs = 0, secondMs = 0, everyMs = 0;
                if (first is { } found)
                {
                    var painted = (byte[])luminance.Clone();
                    PaintOver(painted, image.Image.Width, image.Image.Height, found);
                    second = ReadOne(symbology, painted, image.Image.Width, image.Image.Height, out _)?.Text;
                    if (timed)
                    {
                        firstMs = Fastest(() => ReadOne(symbology, luminance, image.Image.Width, image.Image.Height, out _));
                        secondMs = Fastest(() => ReadOne(symbology, painted, image.Image.Width, image.Image.Height, out _));
                    }
                }
                var everyCount = 0;
                string[] everyOthers = [];
                if (every is not null)
                {
                    var all = every(symbology, luminance, image.Image.Width, image.Image.Height);
                    everyCount = all.Length;
                    everyOthers = [.. all.Select(static f => f.Text).Where(t => t != image.Text)];
                    if (timed && first is not null)
                        everyMs = Fastest(() => every(symbology, luminance, image.Image.Width, image.Image.Height));
                }
                rows.Add((image.Kind, image.Name, read, second, everyCount, everyOthers, firstMs, secondMs, everyMs));
            });

            var list = rows.ToList();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"## {symbology}, one-symbol images read again");
            markdown.AppendLine();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"{list.Count:N0} images: the sweep's renders of this library's symbols, {cases} cases, and the committed real images at four turns. Each the one-symbol call reads is painted over and read again: a second read is false. Times are the fastest of three calls, summed over the images the first call reads.");
            markdown.AppendLine();
            markdown.Append("| Kind | Images | Read | Second read |");
            if (every is not null)
                markdown.Append(" Every: none | Every: more than one |");
            if (timed)
                markdown.Append(" First ms | Second ms | Second / first |").Append(every is not null ? " Every ms | Every / first |" : "");
            markdown.AppendLine();
            markdown.Append("|---|---|---|---|").Append(every is not null ? "---|---|" : "").AppendLine(timed ? (every is not null ? "---|---|---|---|---|" : "---|---|---|") : "");
            foreach (var group in list.GroupBy(static r => r.Kind).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(list.GroupBy(static _ => "**All**").Single()))
            {
                var g = group.ToList();
                markdown.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {g.Count:N0} | {g.Count(static r => r.Read):N0} | {g.Count(static r => r.Second is not null):N0} |");
                if (every is not null)
                    markdown.Append(CultureInfo.InvariantCulture, $" {g.Count(static r => r.Read && r.EveryCount == 0):N0} | {g.Count(static r => r.EveryOthers.Length > 0):N0} |");
                if (timed)
                {
                    double first = g.Sum(static r => r.FirstMs), second = g.Sum(static r => r.SecondMs);
                    markdown.Append(CultureInfo.InvariantCulture, $" {first:F1} | {second:F1} | {second / first:F2} |");
                    if (every is not null)
                        markdown.Append(CultureInfo.InvariantCulture, $" {g.Sum(static r => r.EveryMs):F1} | {g.Sum(static r => r.EveryMs) / first:F2} |");
                }
                markdown.AppendLine();
            }
            markdown.AppendLine();
            var missed = list.Where(r => every is not null && r.Read && r.EveryCount == 0).ToList();
            if (missed.Count > 0)
            {
                markdown.AppendLine("Read by the one-symbol call and not by the call for every symbol:");
                markdown.AppendLine();
                foreach (var r in missed.OrderBy(static r => r.Kind, StringComparer.Ordinal))
                    markdown.AppendLine(CultureInfo.InvariantCulture, $"- {r.Kind}, {r.Name}");
                markdown.AppendLine();
            }
            var falseReads = list.Where(static r => r.Second is not null || r.EveryOthers.Length > 0).ToList();
            if (falseReads.Count > 0)
            {
                markdown.AppendLine("False reads:");
                markdown.AppendLine();
                foreach (var r in falseReads.OrderBy(static r => r.Kind, StringComparer.Ordinal))
                    markdown.AppendLine(CultureInfo.InvariantCulture, $"- {r.Kind}, {r.Name}: {(r.Second is null ? "" : $"read again \"{r.Second}\" ")}{(r.EveryOthers.Length == 0 ? "" : $"every \"{string.Join("\", \"", r.EveryOthers)}\"")}");
                markdown.AppendLine();
            }
        }
        return markdown.ToString();
    }
}
