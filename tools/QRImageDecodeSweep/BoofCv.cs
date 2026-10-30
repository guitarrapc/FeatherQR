using System.Diagnostics;
using System.Globalization;
using System.Text;
using FeatherQR;
using SkiaSharp;

namespace QRImageDecodeSweep;

/// <summary>
/// BoofCV's QR photographs (<c>qrcodes_v3.zip</c>, its <c>detection</c> directory): one directory per category, each photograph beside a <c>.txt</c> of hand-picked corners, four a code.
/// The set records where each code is, not what it says, so a read is scored without a known text, two ways:
/// located, when the read symbol's centre lies inside a labelled code's outline, and agreed, when another reader, or the same reader in another photograph, read the same text.
/// A photograph counts for a reader when one of its reads is agreed. The set is never committed (its licence is not stated), so the path is given on the command line.
/// Readers run in their own harness join through a file of result lines (<see cref="ReaderLines"/>): their texts count toward agreement like any other read, and each gets a table and a gap of its own. Their reads count photographs, and codes too where the lines say where each read is.
/// This library's one-symbol call returns one symbol. What it reads of a photograph's other codes is measured by painting each read symbol over and reading again until a call reads nothing (<see cref="ReadAgain"/>), with the public call alone.
/// Beside it are the call for every Standard QR symbol and the call for every symbol of any symbology, which on this set of Standard QR alone should return what the first returns (<see cref="EveryMarkdown"/>).
/// </summary>
internal static class BoofCv
{
    private static readonly string[] imageExtensions = [".jpg", ".jpeg", ".png"];

    private static readonly string[] readers = ["FeatherQR", "zxing-cpp", "ZXing.Net"];

    /// <summary>Calls <see cref="ReadAgain"/> makes of one photograph at most. The set's fullest photograph holds 60 codes.</summary>
    private const int ReadAgainLimit = 128;

    /// <summary>A decoded symbol: its text, the centre of the outline the reader reported, and its symbology where this library's call for every symbology read it.</summary>
    private sealed record Read(string Text, double X, double Y, Symbology Symbology = Symbology.StandardQR);

    /// <summary>What <see cref="Alone"/> made of one code: the call's status, and its text when it read.</summary>
    private sealed record Cut(string Status, string? Text);

    /// <summary>Index in <see cref="Photo.Reads"/> of the first reader that comes from result lines.</summary>
    private static int FirstLineReader => readers.Length;

    /// <summary>Whether a reader of the lines is this library, run in a harness of its own: one whose name starts with this library's. Its texts are this library's observation, so they agree with nothing this library read here.</summary>
    private static bool IsThisLibrary(string reader) => reader.StartsWith(readers[0], StringComparison.Ordinal);

    /// <param name="Again">Every read of <see cref="ReadAgain"/>, the one-call read first.</param>
    /// <param name="AgainStatus">The status of the call that ended <see cref="ReadAgain"/>.</param>
    /// <param name="Alone">For each label no read of <paramref name="Again"/> lies in, what the code reads cut out on its own. Null for the others.</param>
    /// <param name="Entry">The reads of the call for every Standard QR symbol.</param>
    /// <param name="Every">The reads of the call for every symbol of any symbology.</param>
    private sealed record Photo(string Category, string File, int Width, int Height, List<(double X, double Y)[]> Labels, string FeatherQrStatus, Read[][] Reads, Read[] Again, string AgainStatus, Cut?[] Alone, Read[] Entry, Read[] Every)
    {
        public double EntryMs { get; set; } = double.NaN;

        public double EveryMs { get; set; } = double.NaN;

        public double FeatherQrMs { get; set; } = double.NaN;

        public double FeatherQrAgainMs { get; set; } = double.NaN;

        public double ZXingCppMs { get; set; } = double.NaN;
    }

    public static int Run(string root, string outDir, bool timed, ReaderLines lines, Func<string, string, int> finish)
    {
        var images = new List<(string Category, string Path)>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(static x => x, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(directory).Where(static p => imageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(static x => x, StringComparer.Ordinal))
                images.Add((Path.GetFileName(directory), file));
        }
        if (images.Count == 0)
        {
            Console.Error.WriteLine($"no category directories with .jpg or .png photographs under {root}; pass the dataset's detection directory");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var photos = new Photo[images.Count];
        var done = 0;
        // A photograph is up to 12 MP, decoded to 4 bytes a pixel before it is grey, so fewer threads keep the memory bounded
        Parallel.For(0, images.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 16) }, i =>
        {
            photos[i] = Measure(images[i].Category, images[i].Path, lines);
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == images.Count)
                Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {n}/{images.Count} photographs");
        });
        if (timed)
        {
            for (var i = 0; i < images.Count; i++)
                Time(photos[i], images[i].Path);
        }

        // This library's reads of a photograph are one observation of each text, however many calls made them: the one-call read is the first of the reads again,
        // and a reader of the lines that is this library run elsewhere confirms nothing this library read here
        var observations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            IEnumerable<Read> own = photo.Again;
            var others = new List<Read[]> { photo.Reads[1], photo.Reads[2] };
            for (var r = 0; r < lines.Readers.Length; r++)
            {
                if (IsThisLibrary(lines.Readers[r]))
                    own = own.Concat(photo.Reads[FirstLineReader + r]);
                else
                    others.Add(photo.Reads[FirstLineReader + r]);
            }
            foreach (var reads in others.Prepend([.. own]))
            {
                foreach (var text in reads.Select(static r => r.Text).Distinct(StringComparer.Ordinal))
                    observations[text] = observations.GetValueOrDefault(text) + 1;
            }
        }
        bool Agreed(Read read) => observations.GetValueOrDefault(read.Text) >= 2;
        bool Seen(string text) => observations.ContainsKey(text);

        Directory.CreateDirectory(outDir);
        WriteCsv(Path.Combine(outDir, "boofcv.csv"), photos, Agreed, lines);
        WriteCodesCsv(Path.Combine(outDir, "boofcv-codes.csv"), photos, Agreed, Seen, lines);
        return finish(Path.Combine(outDir, "boofcv.md"), Markdown(photos, Agreed, timed) + AgainMarkdown(photos, Agreed, Seen) + EveryMarkdown(photos, Agreed, timed) + LinesMarkdown(photos, Agreed, lines));
    }

    private static Photo Measure(string category, string path, ReaderLines lines)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
        var (luminance, width, height) = Pixels.ToLuminance(bitmap);

        var labels = Labels(Path.ChangeExtension(path, ".txt"));

        // The other readers are handed the photograph, so the painting is done on a copy
        var painted = (byte[])luminance.Clone();
        var again = ReadAgain(painted, width, height, out var firstStatus, out var lastStatus);
        Read[] own = again.Length > 0 ? [again[0]] : [];
        var alone = labels.Select(label => again.Any(read => Inside(label, read.X, read.Y)) ? null : Alone(painted, width, height, label)).ToArray();

        // A read from result lines lies inside no label unless its line says where it is
        var fromLines = lines.Readers.Select(reader => lines.Find(reader, category, Path.GetFileName(path)) is { } line ? line.Texts.Select((t, i) => new Read(t, line.Centres?[i].X ?? double.NaN, line.Centres?[i].Y ?? double.NaN)).ToArray() : []);
        Read[] entry = [.. QRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => Centre(r.Text, r.Info.Corners, Symbology.StandardQR))];
        Read[] every = [.. SymbolDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => Centre(r.Text, r.Corners, r.Symbology))];
        return new Photo(category, Path.GetFileName(path), width, height, labels, firstStatus, [own, ZXingCpp(luminance, width, height), ZXingNet(luminance, width, height), .. fromLines], again, lastStatus, alone, entry, every);
    }

    private static Read Centre(string text, SymbolCorners c, Symbology symbology) =>
        new(text, (c.TopLeft.X + c.TopRight.X + c.BottomRight.X + c.BottomLeft.X) / 4.0, (c.TopLeft.Y + c.TopRight.Y + c.BottomRight.Y + c.BottomLeft.Y) / 4.0, symbology);

    /// <summary>
    /// Every symbol this library reads of an image through its one-symbol call: each read symbol is painted over and the image read again, until a call reads nothing.
    /// Each read after the first pays for the whole image again.
    /// </summary>
    /// <param name="luminance">The image, painted in place.</param>
    /// <param name="firstStatus">The first call's status, which is the one-symbol call's.</param>
    /// <param name="lastStatus">The status of the call that read nothing.</param>
    private static Read[] ReadAgain(byte[] luminance, int width, int height, out string firstStatus, out string lastStatus)
    {
        var reads = new List<Read>();
        firstStatus = lastStatus = "";
        while (reads.Count < ReadAgainLimit)
        {
            var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            lastStatus = success ? "Success" : info.Status.ToString();
            if (reads.Count == 0)
                firstStatus = lastStatus;
            if (!success)
                break;
            reads.Add(Centre(text, info.Corners, Symbology.StandardQR));
            PaintOver(luminance, width, height, info.Corners, info.Version);
        }
        return [.. reads];
    }

    /// <summary>
    /// Fills a read symbol's outline, a module wider on every side, with the light level of the pixels it holds.
    /// The level is the symbol's own and not the image's, so a symbol in a shadow does not become a bright patch.
    /// </summary>
    private static void PaintOver(byte[] luminance, int width, int height, SymbolCorners corners, int version)
    {
        var dimension = 17 + 4 * version;
        var grow = (dimension + 2.0) / dimension;
        ImagePoint[] points = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
        var cx = points.Average(static p => (double)p.X);
        var cy = points.Average(static p => (double)p.Y);
        (double X, double Y)[] outline = [.. points.Select(p => (cx + (p.X - cx) * grow, cy + (p.Y - cy) * grow))];

        var top = Math.Clamp((int)Math.Floor(outline.Min(static p => p.Y)), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(outline.Max(static p => p.Y)), 0, height - 1);

        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var y = top; y <= bottom; y++)
        {
            foreach (var pixel in RowInside(luminance, width, outline, y))
                histogram[pixel]++;
            count += RowInside(luminance, width, outline, y).Length;
        }

        // About half a symbol's pixels are light, so the upper quartile is the middle of the light ones
        var level = 255;
        for (var above = 0; level > 0 && above + histogram[level] <= count / 4; level--)
            above += histogram[level];

        for (var y = top; y <= bottom; y++)
            RowInside(luminance, width, outline, y).Fill((byte)level);
    }

    /// <summary>The pixels of row <paramref name="y"/> whose centres lie inside a convex <paramref name="outline"/>: one run, between the two edges the row's centre line crosses.</summary>
    internal static Span<byte> RowInside(byte[] luminance, int width, (double X, double Y)[] outline, int y)
    {
        var centre = y + 0.5;
        double from = double.MaxValue, to = double.MinValue;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var (xi, yi) = outline[i];
            var (xj, yj) = outline[j];
            if ((yi > centre) == (yj > centre))
                continue;
            var x = (xj - xi) * (centre - yi) / (yj - yi) + xi;
            from = Math.Min(from, x);
            to = Math.Max(to, x);
        }
        if (from > to)
            return [];
        // A pixel's centre is at x + 0.5
        var first = (int)Math.Ceiling(Math.Max(0, from - 0.5));
        var last = (int)Math.Ceiling(Math.Min(width, to - 0.5)) - 1;
        return first > last ? [] : luminance.AsSpan(y * width + first, last - first + 1);
    }

    /// <summary>
    /// A labelled code cut out of the image with a quarter of its size around it, read on its own.
    /// A code that reads this way and not in the whole image is within the decoder's reach once nothing else is in the image.
    /// </summary>
    private static Cut Alone(byte[] luminance, int width, int height, (double X, double Y)[] label)
    {
        var margin = 0.25 * Math.Max(label.Max(static p => p.X) - label.Min(static p => p.X), label.Max(static p => p.Y) - label.Min(static p => p.Y));
        var left = Math.Clamp((int)Math.Floor(label.Min(static p => p.X) - margin), 0, width - 1);
        var right = Math.Clamp((int)Math.Ceiling(label.Max(static p => p.X) + margin), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(label.Min(static p => p.Y) - margin), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(label.Max(static p => p.Y) + margin), 0, height - 1);
        int cutWidth = right - left + 1, cutHeight = bottom - top + 1;
        var cut = new byte[cutWidth * cutHeight];
        for (var y = 0; y < cutHeight; y++)
            luminance.AsSpan((top + y) * width + left, cutWidth).CopyTo(cut.AsSpan(y * cutWidth, cutWidth));
        return QRCodeDecoder.TryDecodeImage(cut, cutWidth, cutHeight, out var text, out var info) ? new Cut("Success", text) : new Cut(info.Status.ToString(), null);
    }

    private static void Time(Photo photo, string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
        var (luminance, width, height) = Pixels.ToLuminance(bitmap);
        photo.FeatherQrMs = Fastest(() => QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _));
        photo.ZXingCppMs = Fastest(() => ZXingCpp(luminance, width, height));

        // Each run paints, so each starts from a copy made outside its time
        var best = double.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var painted = (byte[])luminance.Clone();
            var start = Stopwatch.GetTimestamp();
            ReadAgain(painted, width, height, out _, out _);
            if (i > 0)
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        photo.FeatherQrAgainMs = best;
        photo.EntryMs = Fastest(() => QRCodeDecoder.DecodeImageSymbols(luminance, width, height));
        photo.EveryMs = Fastest(() => SymbolDecoder.DecodeImageSymbols(luminance, width, height));
    }

    private static double Fastest(Action call)
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

    /// <summary>zxing-cpp with <c>TryHarder</c>, every symbol it finds.</summary>
    private static Read[] ZXingCpp(byte[] luminance, int width, int height)
    {
        return Readers.ZXingCpp(global::ZXingCpp.BarcodeFormat.QRCode, luminance, width, height, static r =>
        {
            var p = r.Position;
            return new Read(r.Text, (p.TopLeft.X + p.TopRight.X + p.BottomRight.X + p.BottomLeft.X) / 4.0, (p.TopLeft.Y + p.TopRight.Y + p.BottomRight.Y + p.BottomLeft.Y) / 4.0);
        });
    }

    /// <summary>ZXing.Net with <c>TryHarder</c>, every symbol it finds. Its result points are the finder centres, whose mean lies inside the symbol.</summary>
    private static Read[] ZXingNet(byte[] luminance, int width, int height)
    {
        try
        {
            var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true };
            reader.Options.TryHarder = true;
            reader.Options.PossibleFormats = [ZXing.BarcodeFormat.QR_CODE];
            var source = new ZXing.RGBLuminanceSource(luminance, width, height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
            var results = reader.DecodeMultiple(source) ?? [];
            return [.. results.Where(static r => r.Text is not null).Select(static r => new Read(r.Text, r.ResultPoints.Average(static p => p.X), r.ResultPoints.Average(static p => p.Y)))];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The label file as BoofCV's own evaluation reads it: <c>#</c> starts a comment, a line <c>SETS</c> means one code a line, and otherwise the file's numbers are one code. Eight numbers a code, its four corners in no promised order.
    /// </summary>
    private static List<(double X, double Y)[]> Labels(string path)
    {
        var codes = new List<(double X, double Y)[]>();
        if (!File.Exists(path))
            return codes;
        var sets = false;
        var single = new List<double>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            if (trimmed.StartsWith("SETS", StringComparison.Ordinal))
            {
                sets = true;
                continue;
            }
            var values = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(static s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
            if (sets)
            {
                if (values.Count == 8)
                    codes.Add(Quad(values));
            }
            else
            {
                single.AddRange(values);
            }
        }
        if (!sets && single.Count == 8)
            codes.Add(Quad(single));
        return codes;

        static (double X, double Y)[] Quad(List<double> v)
        {
            (double X, double Y)[] points = [(v[0], v[1]), (v[2], v[3]), (v[4], v[5]), (v[6], v[7])];
            var cx = points.Average(static p => p.X);
            var cy = points.Average(static p => p.Y);
            return [.. points.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))];
        }
    }

    private static int LabelOf(Photo photo, Read read)
    {
        for (var i = 0; i < photo.Labels.Count; i++)
        {
            if (Inside(photo.Labels[i], read.X, read.Y))
                return i;
        }
        return -1;
    }

    private static bool Inside((double X, double Y)[] polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static string Markdown(Photo[] photos, Func<Read, bool> agreed, bool timed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## BoofCV QR photographs, by category");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{photos.Length:N0} photographs, {photos.Sum(static p => p.Labels.Count):N0} labelled codes. A photograph counts for a reader when one of its reads is agreed; a code counts when an agreed read lies inside its outline. FeatherQR reads one symbol a call, the other two every symbol they find. Read again is FeatherQR's one-symbol call repeated, each read symbol painted over, until a call reads nothing.");
        sb.AppendLine();
        sb.Append("| Category | Photographs | Codes | ").AppendJoin(" | ", readers).Append(" | Gap | Reverse | ").AppendJoin(" | ", readers.Select(static r => r + " codes")).AppendLine(" | FeatherQR codes, read again |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in photos.GroupBy(static p => p.Category).OrderBy(static g => g.Key, StringComparer.Ordinal))
            Line(sb, group.Key, [.. group], agreed);
        Line(sb, "**All**", photos, agreed);
        sb.AppendLine();

        var stopped = photos.Where(p => !p.Reads[0].Any(agreed)).GroupBy(static p => p.FeatherQrStatus).OrderByDescending(static g => g.Count()).Select(static g => $"{g.Key} {g.Count():N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Where FeatherQR read no agreed text: {string.Join(", ", stopped)}.");
        sb.AppendLine();

        if (timed)
        {
            sb.AppendLine("## Timing, one thread, fastest of three after one untimed call (ms)");
            sb.AppendLine();
            sb.AppendLine("| Category | FeatherQR median | Read again median | zxing-cpp median | FeatherQR total | Read again total | zxing-cpp total |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var group in photos.GroupBy(static p => p.Category).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(photos.GroupBy(static _ => "**All**").Single()))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {Median(group.Select(static p => p.FeatherQrMs)):F2} | {Median(group.Select(static p => p.FeatherQrAgainMs)):F2} | {Median(group.Select(static p => p.ZXingCppMs)):F2} | {group.Sum(static p => p.FeatherQrMs):F0} | {group.Sum(static p => p.FeatherQrAgainMs):F0} | {group.Sum(static p => p.ZXingCppMs):F0} |");
            sb.AppendLine();
        }

        List(sb, "Gap: zxing-cpp has an agreed read, FeatherQR none", photos.Where(p => p.Reads[1].Any(agreed) && !p.Reads[0].Any(agreed)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes): {p.FeatherQrStatus}");
        List(sb, "Reverse: FeatherQR has an agreed read, zxing-cpp none", photos.Where(p => p.Reads[0].Any(agreed) && !p.Reads[1].Any(agreed)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes)");

        // A FeatherQR read inside a code zxing-cpp read otherwise, or a text no other read confirms, is either a code read once or a misread, so each is looked at by hand
        List(sb, "FeatherQR read a code zxing-cpp read with another text", photos.Where(p => p.Again.Any(own => LabelOf(p, own) is var label && label >= 0 && p.Reads[1].Any(other => LabelOf(p, other) == label && other.Text != own.Text))), static p => $"{p.Category}/{p.File}");
        List(sb, "FeatherQR texts no other read confirms", photos.Where(p => p.Again.Any(own => !agreed(own))), p => $"{p.Category}/{p.File}: {string.Join(", ", p.Again.Select((read, call) => (read, call)).Where(x => !agreed(x.read)).Select(static x => $"call {x.call + 1} {Show(x.read.Text)}"))}");
        return sb.ToString();
    }

    /// <summary>
    /// What reading again adds to the one-symbol call: codes by how many a photograph holds, the reads no outline or an outline twice holds, and, for each code zxing-cpp reads and reading again does not, what the code reads cut out on its own.
    /// </summary>
    private static string AgainMarkdown(Photo[] photos, Func<Read, bool> agreed, Func<string, bool> seen)
    {
        int Codes(Photo p, IEnumerable<Read> reads) => reads.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();

        var sb = new StringBuilder();
        sb.AppendLine("## FeatherQR read again, by codes a photograph");
        sb.AppendLine();
        sb.AppendLine("| Codes a photograph | Photographs | Codes | FeatherQR codes | FeatherQR codes, read again | zxing-cpp codes | ZXing.Net codes | Calls |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        List<(string Name, Photo[] Photos)> byCount = [.. photos.GroupBy(static p => p.Labels.Count).OrderBy(static g => g.Key).Select(static g => (g.Key.ToString(CultureInfo.InvariantCulture), g.ToArray())), ("**More than one**", [.. photos.Where(static p => p.Labels.Count > 1)]), ("**All**", photos)];
        foreach (var group in byCount)
        {
            // A photograph's calls are its reads and the one that read nothing
            var calls = group.Photos.Sum(static p => p.Again.Length + (p.AgainStatus == "Success" ? 0 : 1));
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Name} | {group.Photos.Length:N0} | {group.Photos.Sum(static p => p.Labels.Count):N0} | {group.Photos.Sum(p => Codes(p, p.Reads[0])):N0} | {group.Photos.Sum(p => Codes(p, p.Again)):N0} | {group.Photos.Sum(p => Codes(p, p.Reads[1])):N0} | {group.Photos.Sum(p => Codes(p, p.Reads[2])):N0} | {calls:N0} |");
        }
        sb.AppendLine();

        var all = photos.SelectMany(static p => p.Again.Select(read => (Photo: p, Read: read))).ToList();
        var twice = photos.Sum(p => p.Again.Select(r => LabelOf(p, r)).Where(static l => l >= 0).GroupBy(static l => l).Sum(static g => g.Count() - 1));
        var ended = photos.GroupBy(static p => p.AgainStatus).OrderByDescending(static g => g.Count()).Select(static g => $"{g.Key} {g.Count():N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{all.Count:N0} reads in {photos.Sum(static p => p.Again.Length + (p.AgainStatus == "Success" ? 0 : 1)):N0} calls: {all.Count(x => agreed(x.Read) && LabelOf(x.Photo, x.Read) >= 0):N0} agreed and inside an outline, {all.Count(x => !agreed(x.Read)):N0} with a text no other read confirms, {all.Count(x => LabelOf(x.Photo, x.Read) < 0):N0} inside no outline, {twice:N0} inside an outline another read of the photograph is in. The call that ended a photograph: {string.Join(", ", ended)}.");
        sb.AppendLine();

        // A read counts for the first outline it lies in, so the second label of one code is counted for no reader
        var labelledTwice = photos.Sum(static p => p.Labels.Where((label, i) => p.Labels.Take(i).Any(earlier => Inside(earlier, label.Average(static q => q.X), label.Average(static q => q.Y)))).Count());
        sb.AppendLine(CultureInfo.InvariantCulture, $"{labelledTwice:N0} labelled codes have their centre inside an earlier outline of the same photograph, so no reader can count them.");
        sb.AppendLine();
        List(sb, "FeatherQR reads inside no labelled outline", photos.Where(p => p.Again.Any(r => LabelOf(p, r) < 0)), p => $"{p.Category}/{p.File} ({p.Labels.Count} codes): {string.Join(", ", p.Again.Select((read, call) => (read, call)).Where(x => LabelOf(p, x.read) < 0).Select(x => $"call {x.call + 1} at ({x.read.X:F0}, {x.read.Y:F0}) {(agreed(x.read) ? "agreed" : "not agreed")} {Show(x.read.Text)}"))}");

        // A code zxing-cpp reads and reading again does not: what the decoder makes of it with nothing else in the image
        var missed = photos.SelectMany(p => Enumerable.Range(0, p.Labels.Count).Where(label => ReadsCode(p, p.Reads[1], label, agreed) && !ReadsCode(p, p.Again, label, agreed)).Select(label => (Photo: p, Label: label))).ToList();
        string Class((Photo Photo, int Label) code)
        {
            // Null where a read lies in the outline. A missed code counts no agreed read, so an agreed one there is counted for an earlier outline
            var cut = code.Photo.Alone[code.Label];
            if (cut is null)
                return code.Photo.Again.Any(r => agreed(r) && Inside(code.Photo.Labels[code.Label], r.X, r.Y)) ? "Read for an earlier outline" : "Read, not agreed";
            if (cut.Text is null)
                return cut.Status;
            return CutStatus(code.Photo, code.Label, cut.Text, seen) == "Success" ? "Reads alone" : "Reads alone, another text";
        }
        var classes = missed.Select(Class).GroupBy(static c => c).OrderByDescending(static g => g.Count()).Select(static g => g.Key).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Codes zxing-cpp reads and reading again does not, by what the code reads cut out on its own ({missed.Count:N0})");
        sb.AppendLine();
        sb.Append("| Category | Codes |").AppendJoin("", classes.Select(static c => $" {c} |")).AppendLine();
        sb.Append("|---|---|").AppendJoin("", classes.Select(static _ => "---|")).AppendLine();
        List<(string Name, List<(Photo Photo, int Label)> Codes)> byCategory = [.. missed.GroupBy(static c => c.Photo.Category).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => (g.Key, g.ToList())), ("**All**", missed)];
        foreach (var group in byCategory)
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Name} | {group.Codes.Count:N0} |").AppendJoin("", classes.Select(c => string.Create(CultureInfo.InvariantCulture, $" {group.Codes.Count(code => Class(code) == c):N0} |"))).AppendLine();
        sb.AppendLine();
        var reverse = photos.Sum(p => Enumerable.Range(0, p.Labels.Count).Count(label => ReadsCode(p, p.Again, label, agreed) && !ReadsCode(p, p.Reads[1], label, agreed)));
        sb.AppendLine(CultureInfo.InvariantCulture, $"Codes reading again reads and zxing-cpp does not: {reverse:N0}.");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// The call for every Standard QR symbol and the call for every symbology, by codes a photograph, beside reading again.
    /// The set holds Standard QR alone, so a Micro QR or rMQR read is false, and the call for every symbology should return the other call's reads and nothing else.
    /// </summary>
    private static string EveryMarkdown(Photo[] photos, Func<Read, bool> agreed, bool timed)
    {
        int Codes(Photo p, IEnumerable<Read> reads) => reads.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
        static bool Same(Photo p) => p.Every.Length == p.Entry.Length && p.Every.Zip(p.Entry).All(static x => x.First == x.Second);

        var sb = new StringBuilder();
        sb.AppendLine("## Every symbol in one call");
        sb.AppendLine();
        sb.AppendLine("Standard QR's call is the call for every Standard QR symbol, and every symbology the call for every symbol of any symbology. The set holds Standard QR alone, so every symbology should return Standard QR's call's reads and nothing else, and a Micro QR or rMQR read is false.");
        sb.AppendLine();
        sb.AppendLine("| Codes a photograph | Photographs | Codes | Standard QR's call | Every symbology | Read again |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in ByCodes(photos))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Name} | {group.Photos.Length:N0} | {group.Photos.Sum(static p => p.Labels.Count):N0} | {group.Photos.Sum(p => Codes(p, p.Entry)):N0} | {group.Photos.Sum(p => Codes(p, p.Every)):N0} | {group.Photos.Sum(p => Codes(p, p.Again)):N0} |");
        sb.AppendLine();

        var others = photos.SelectMany(static p => p.Every).Where(static r => r.Symbology != Symbology.StandardQR).GroupBy(static r => r.Symbology).Select(static g => $"{g.Key} {g.Count():N0}").ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Standard QR's call makes {photos.Sum(static p => p.Entry.Length):N0} reads, {photos.Sum(p => p.Entry.Count(r => agreed(r) && LabelOf(p, r) >= 0)):N0} of them agreed and inside an outline. Every symbology returns the same reads in the same order at the same centres on {photos.Count(Same):N0} of the {photos.Length:N0} photographs. Its Micro QR and rMQR reads: {(others.Count == 0 ? "none" : string.Join(", ", others))}.");
        sb.AppendLine();
        List(sb, "Every symbology returns other reads than Standard QR's call", photos.Where(static p => !Same(p)), static p => $"{p.Category}/{p.File}: Standard QR's call {string.Join(", ", p.Entry.Select(static r => Show(r.Text)))}; every symbology {string.Join(", ", p.Every.Select(static r => $"{r.Symbology} {Show(r.Text)}"))}");

        if (timed)
        {
            sb.AppendLine("### Every symbol in one call, timed: one thread, fastest of three after one untimed call (ms)");
            sb.AppendLine();
            sb.AppendLine("By the codes Standard QR's call reads, since the call for every symbology decodes no Micro QR or rMQR candidate inside a symbol it read.");
            sb.AppendLine();
            sb.AppendLine("| Codes Standard QR's call reads | Photographs | Standard QR's call | Every symbology | Every / Standard QR's call |");
            sb.AppendLine("|---|---|---|---|---|");
            List<(string Name, Photo[] Photos)> groups =
            [
                .. photos.GroupBy(static p => p.Entry.Length).OrderBy(static g => g.Key).Select(static g => (g.Key.ToString(CultureInfo.InvariantCulture), g.ToArray())),
                ("**One to four**", [.. photos.Where(static p => p.Entry.Length is >= 1 and <= 4)]),
                ("**All**", photos),
            ];
            foreach (var group in groups)
            {
                double entry = group.Photos.Sum(static p => p.EntryMs), every = group.Photos.Sum(static p => p.EveryMs);
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Name} | {group.Photos.Length:N0} | {entry:F0} | {every:F0} | {every / entry:F2} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The photographs by how many codes each holds, then those of more than one and all of them.</summary>
    private static List<(string Name, Photo[] Photos)> ByCodes(Photo[] photos) =>
        [.. photos.GroupBy(static p => p.Labels.Count).OrderBy(static g => g.Key).Select(static g => (g.Key.ToString(CultureInfo.InvariantCulture), g.ToArray())), ("**More than one**", [.. photos.Where(static p => p.Labels.Count > 1)]), ("**All**", photos)];

    /// <summary>Whether an agreed read of <paramref name="reads"/> lies inside the photograph's <paramref name="label"/>.</summary>
    private static bool ReadsCode(Photo photo, IEnumerable<Read> reads, int label, Func<Read, bool> agreed) => reads.Any(r => agreed(r) && LabelOf(photo, r) == label);

    /// <summary>
    /// What a code cut out and read alone read, against zxing-cpp's reads inside the same outline: its text, or another text. Where zxing-cpp has none there, a text some reader read, or one no reader read.
    /// </summary>
    private static string CutStatus(Photo photo, int label, string text, Func<string, bool> seen)
    {
        var there = photo.Reads[1].Where(r => LabelOf(photo, r) == label).ToList();
        if (there.Count > 0)
            return there.Any(r => r.Text == text) ? "Success" : "SuccessAnotherText";
        return seen(text) ? "Success" : "SuccessNotSeen";
    }

    /// <summary>A table and the photographs by name for each reader that came from result lines: what it reads, the gap (it has an agreed read, FeatherQR none) and the reverse.</summary>
    private static string LinesMarkdown(Photo[] photos, Func<Read, bool> agreed, ReaderLines lines)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < lines.Readers.Length; r++)
        {
            var name = lines.Readers[r];
            var index = FirstLineReader + r;
            bool Own(Photo p) => p.Reads[0].Any(agreed);
            bool Other(Photo p) => p.Reads[index].Any(agreed);
            var held = photos.Count(p => lines.Find(name, p.Category, p.File) is not null);
            var known = photos.Select(static p => (p.Category, Path.GetFileNameWithoutExtension(p.File))).ToHashSet();
            var strangers = lines.Images(name).Count(i => !known.Contains(i));
            var times = photos.Select(p => lines.Find(name, p.Category, p.File)?.Milliseconds ?? double.NaN).ToList();

            sb.AppendLine(CultureInfo.InvariantCulture, $"## {name}, from result lines");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Run in its own harness: {held:N0} of the {photos.Length:N0} photographs have a line, {strangers:N0} lines name no photograph of the set. Its texts count toward agreement, so a text only it and one other reader read is agreed for both. Its own clock: {times.Where(static t => !double.IsNaN(t)).Sum():F0} ms over the set, median {Median(times):F2}.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Category | Photographs | FeatherQR | {name} | Gap | Reverse |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var group in photos.GroupBy(static p => p.Category).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(photos.GroupBy(static _ => "**All**").Single()))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count():N0} | {group.Count(Own):N0} | {group.Count(Other):N0} | {group.Count(p => Other(p) && !Own(p)):N0} | {group.Count(p => Own(p) && !Other(p)):N0} |");
            sb.AppendLine();

            List(sb, $"Gap: {name} has an agreed read, FeatherQR none", photos.Where(p => Other(p) && !Own(p)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes): {p.FeatherQrStatus}");
            List(sb, $"Reverse: FeatherQR has an agreed read, {name} none", photos.Where(p => Own(p) && !Other(p)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes)");
            List(sb, $"{name} texts no other read confirms", photos.Where(p => p.Reads[index].Any(read => !agreed(read))), p => $"{p.Category}/{p.File}: {Show(p.Reads[index].First(read => !agreed(read)).Text)}");

            // A reader whose lines say where its reads are is counted by codes too, beside reading again and zxing-cpp
            if (!photos.Any(p => p.Reads[index].Any(static read => !double.IsNaN(read.X))))
                continue;
            int Codes(Photo p, IEnumerable<Read> reads) => reads.Where(agreed).Select(read => LabelOf(p, read)).Where(static l => l >= 0).Distinct().Count();
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {name}, by codes a photograph");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Codes a photograph | Photographs | Codes | {name} codes | FeatherQR codes, read again | zxing-cpp codes |");
            sb.AppendLine("|---|---|---|---|---|---|");
            List<(string Name, Photo[] Photos)> byCount = [.. photos.GroupBy(static p => p.Labels.Count).OrderBy(static g => g.Key).Select(static g => (g.Key.ToString(CultureInfo.InvariantCulture), g.ToArray())), ("**More than one**", [.. photos.Where(static p => p.Labels.Count > 1)]), ("**All**", photos)];
            foreach (var group in byCount)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Name} | {group.Photos.Length:N0} | {group.Photos.Sum(static p => p.Labels.Count):N0} | {group.Photos.Sum(p => Codes(p, p.Reads[index])):N0} | {group.Photos.Sum(p => Codes(p, p.Again)):N0} | {group.Photos.Sum(p => Codes(p, p.Reads[1])):N0} |");
            sb.AppendLine();

            var all = photos.SelectMany(p => p.Reads[index].Select(read => (Photo: p, Read: read))).ToList();
            var twice = photos.Sum(p => p.Reads[index].Select(read => LabelOf(p, read)).Where(static l => l >= 0).GroupBy(static l => l).Sum(static g => g.Count() - 1));
            sb.AppendLine(CultureInfo.InvariantCulture, $"{all.Count:N0} reads: {all.Count(x => agreed(x.Read) && LabelOf(x.Photo, x.Read) >= 0):N0} agreed and inside an outline, {all.Count(x => !agreed(x.Read)):N0} with a text no other read confirms, {all.Count(x => LabelOf(x.Photo, x.Read) < 0):N0} inside no outline, {twice:N0} inside an outline another of its reads of the photograph is in.");
            sb.AppendLine();

            static string Named(Photo p, int label) => $"{p.Category}/{p.File} code {label}";
            var gained = photos.SelectMany(p => Enumerable.Range(0, p.Labels.Count).Where(label => ReadsCode(p, p.Reads[index], label, agreed) && !ReadsCode(p, p.Again, label, agreed)).Select(label => Named(p, label))).ToList();
            var lost = photos.SelectMany(p => Enumerable.Range(0, p.Labels.Count).Where(label => ReadsCode(p, p.Again, label, agreed) && !ReadsCode(p, p.Reads[index], label, agreed)).Select(label => Named(p, label))).ToList();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Codes {name} reads and reading again does not ({gained.Count:N0}): {string.Join(", ", gained)}.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Codes reading again reads and {name} does not ({lost.Count:N0}): {string.Join(", ", lost)}.");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string name, Photo[] photos, Func<Read, bool> agreed)
    {
        bool Reads(Photo p, int reader) => p.Reads[reader].Any(agreed);
        int Codes(Photo p, int reader) => p.Reads[reader].Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
        sb.Append(CultureInfo.InvariantCulture, $"| {name} | {photos.Length:N0} | {photos.Sum(static p => p.Labels.Count):N0} |");
        for (var r = 0; r < readers.Length; r++)
            sb.Append(CultureInfo.InvariantCulture, $" {photos.Count(p => Reads(p, r)):N0} |");
        sb.Append(CultureInfo.InvariantCulture, $" {photos.Count(p => Reads(p, 1) && !Reads(p, 0)):N0} | {photos.Count(p => Reads(p, 0) && !Reads(p, 1)):N0} |");
        for (var r = 0; r < readers.Length; r++)
            sb.Append(CultureInfo.InvariantCulture, $" {photos.Sum(p => Codes(p, r)):N0} |");
        sb.Append(CultureInfo.InvariantCulture, $" {photos.Sum(p => p.Again.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count()):N0} |");
        sb.AppendLine();
    }

    private static void List(StringBuilder sb, string title, IEnumerable<Photo> photos, Func<Photo, string> describe)
    {
        var list = photos.ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({list.Count:N0})");
        sb.AppendLine();
        foreach (var photo in list)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {describe(photo)}");
        sb.AppendLine();
    }

    private static string Show(string text) => string.Concat(text.Take(40).Select(static c => c < 0x20 || c == 0x7F ? $"<{(int)c:X2}>" : c.ToString()));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(static v => !double.IsNaN(v)).Order().ToList();
        return sorted.Count == 0 ? double.NaN : sorted[sorted.Count / 2];
    }

    private static void WriteCsv(string path, Photo[] photos, Func<Read, bool> agreed, ReaderLines lines)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        // The readers from result lines come last, so the columns before them keep their places
        writer.WriteLine("category,file,width,height,codes,featherqr_status,featherqr,featherqr_located,zxingcpp,zxingcpp_codes,zxingnet,featherqr_ms,zxingcpp_ms,featherqr_again_reads,featherqr_again_codes,featherqr_again_status,featherqr_again_ms,featherqr_entry_codes,featherqr_every_codes,featherqr_every_other,featherqr_entry_ms,featherqr_every_ms" + string.Concat(lines.Readers.Select(static r => $",{r},{r}_ms")));
        foreach (var p in photos)
        {
            var own = p.Reads[0].Any(agreed);
            var located = p.Reads[0].Any(r => LabelOf(p, r) >= 0);
            var cpp = p.Reads[1].Any(agreed);
            var cppCodes = p.Reads[1].Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
            var net = p.Reads[2].Any(agreed);
            var againCodes = p.Again.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
            var entryCodes = p.Entry.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
            var everyCodes = p.Every.Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
            var everyOther = p.Every.Count(static r => r.Symbology != Symbology.StandardQR);
            var fromLines = string.Concat(lines.Readers.Select((reader, r) => string.Create(CultureInfo.InvariantCulture, $",{(p.Reads[FirstLineReader + r].Any(agreed) ? 1 : 0)},{lines.Find(reader, p.Category, p.File)?.Milliseconds ?? double.NaN:F3}")));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.Category},{p.File},{p.Width},{p.Height},{p.Labels.Count},{p.FeatherQrStatus},{(own ? 1 : 0)},{(located ? 1 : 0)},{(cpp ? 1 : 0)},{cppCodes},{(net ? 1 : 0)},{p.FeatherQrMs:F3},{p.ZXingCppMs:F3},{p.Again.Length},{againCodes},{p.AgainStatus},{p.FeatherQrAgainMs:F3},{entryCodes},{everyCodes},{everyOther},{p.EntryMs:F3},{p.EveryMs:F3}") + fromLines);
        }
    }

    /// <summary>
    /// One row a labelled code: its size, which reader has an agreed read inside it, and, where reading again has none, what the code reads cut out on its own.
    /// A change to what one call returns is compared against these rows code for code.
    /// </summary>
    private static void WriteCodesCsv(string path, Photo[] photos, Func<Read, bool> agreed, Func<string, bool> seen, ReaderLines lines)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        // A reader of the lines that says where its reads are has a column of its own, after the others
        int[] located = [.. Enumerable.Range(0, lines.Readers.Length).Where(r => photos.Any(p => p.Reads[FirstLineReader + r].Any(static read => !double.IsNaN(read.X))))];
        writer.WriteLine("category,file,codes,code,centre_x,centre_y,side_px,featherqr,featherqr_again,featherqr_again_call,zxingcpp,zxingnet,alone,featherqr_entry,featherqr_every" + string.Concat(located.Select(r => $",{lines.Readers[r]}")));
        foreach (var p in photos)
        {
            for (var label = 0; label < p.Labels.Count; label++)
            {
                var outline = p.Labels[label];
                // The outline's area as a square's side: the labels are in no promised order, and Labels sorts each round its centre
                var area = 0.0;
                for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
                    area += outline[j].X * outline[i].Y - outline[i].X * outline[j].Y;
                var call = Array.FindIndex(p.Again, r => agreed(r) && LabelOf(p, r) == label);
                var cut = p.Alone[label];
                var alone = cut is null ? "" : cut.Text is null ? cut.Status : CutStatus(p, label, cut.Text, seen);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.Category},{p.File},{p.Labels.Count},{label},{outline.Average(static q => q.X):F1},{outline.Average(static q => q.Y):F1},{Math.Sqrt(Math.Abs(area) / 2):F1},{(ReadsCode(p, p.Reads[0], label, agreed) ? 1 : 0)},{(call >= 0 ? 1 : 0)},{(call >= 0 ? call + 1 : 0)},{(ReadsCode(p, p.Reads[1], label, agreed) ? 1 : 0)},{(ReadsCode(p, p.Reads[2], label, agreed) ? 1 : 0)},{alone},{(ReadsCode(p, p.Entry, label, agreed) ? 1 : 0)},{(ReadsCode(p, p.Every, label, agreed) ? 1 : 0)}") + string.Concat(located.Select(r => ReadsCode(p, p.Reads[FirstLineReader + r], label, agreed) ? ",1" : ",0")));
            }
        }
    }
}
