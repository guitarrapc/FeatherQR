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
/// A photograph counts for a reader when one of its reads is agreed. The set is never committed (its licence is not stated); the path is given on the command line.
/// </summary>
internal static class BoofCv
{
    private static readonly string[] imageExtensions = [".jpg", ".jpeg", ".png"];

    private static readonly string[] readers = ["FeatherQR", "zxing-cpp", "ZXing.Net"];

    /// <summary>A decoded symbol: its text and the centre of the outline the reader reported.</summary>
    private sealed record Read(string Text, double X, double Y);

    private sealed record Photo(string Category, string File, int Width, int Height, List<(double X, double Y)[]> Labels, string FeatherQrStatus, Read[][] Reads)
    {
        public double FeatherQrMs { get; set; } = double.NaN;

        public double ZXingCppMs { get; set; } = double.NaN;
    }

    public static int Run(string root, string outDir, bool timed, Func<string, string, int> finish)
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
        // A photograph is up to 12 MP, decoded to 4 bytes a pixel before it is grey; fewer threads keep the memory bounded
        Parallel.For(0, images.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 16) }, i =>
        {
            photos[i] = Measure(images[i].Category, images[i].Path);
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == images.Count)
                Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {n}/{images.Count} photographs");
        });
        if (timed)
        {
            for (var i = 0; i < images.Count; i++)
                Time(photos[i], images[i].Path);
        }

        var observations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            foreach (var reads in photo.Reads)
            {
                foreach (var text in reads.Select(static r => r.Text).Distinct(StringComparer.Ordinal))
                    observations[text] = observations.GetValueOrDefault(text) + 1;
            }
        }
        bool Agreed(Read read) => observations[read.Text] >= 2;

        Directory.CreateDirectory(outDir);
        WriteCsv(Path.Combine(outDir, "boofcv.csv"), photos, Agreed);
        return finish(Path.Combine(outDir, "boofcv.md"), Markdown(photos, Agreed, timed));
    }

    private static Photo Measure(string category, string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
        var (luminance, width, height) = Pixels.ToLuminance(bitmap);

        Read[] own = [];
        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        if (success)
        {
            var c = info.Corners;
            own = [new Read(text, (c.TopLeft.X + c.TopRight.X + c.BottomRight.X + c.BottomLeft.X) / 4.0, (c.TopLeft.Y + c.TopRight.Y + c.BottomRight.Y + c.BottomLeft.Y) / 4.0)];
        }

        return new Photo(category, Path.GetFileName(path), width, height, Labels(Path.ChangeExtension(path, ".txt")), success ? "Success" : info.Status.ToString(), [own, ZXingCpp(luminance, width, height), ZXingNet(luminance, width, height)]);
    }

    private static void Time(Photo photo, string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
        var (luminance, width, height) = Pixels.ToLuminance(bitmap);
        photo.FeatherQrMs = Fastest(() => QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _));
        photo.ZXingCppMs = Fastest(() => ZXingCpp(luminance, width, height));
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
        try
        {
            var view = new global::ZXingCpp.ImageView(luminance, width, height, global::ZXingCpp.ImageFormat.Lum);
            var results = new global::ZXingCpp.BarcodeReader { Formats = global::ZXingCpp.BarcodeFormat.QRCode, TryHarder = true }.From(view);
            return [.. results.Select(static r =>
            {
                var p = r.Position;
                return new Read(r.Text, (p.TopLeft.X + p.TopRight.X + p.BottomRight.X + p.BottomLeft.X) / 4.0, (p.TopLeft.Y + p.TopRight.Y + p.BottomRight.Y + p.BottomLeft.Y) / 4.0);
            })];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>ZXing.Net with <c>TryHarder</c>, every symbol it finds; its result points are the finder centres, whose mean lies inside the symbol.</summary>
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"{photos.Length:N0} photographs, {photos.Sum(static p => p.Labels.Count):N0} labelled codes. A photograph counts for a reader when one of its reads is agreed; a code counts when an agreed read lies inside its outline. FeatherQR reads one symbol a call, the other two every symbol they find.");
        sb.AppendLine();
        sb.Append("| Category | Photographs | Codes | ").AppendJoin(" | ", readers).Append(" | Gap | Reverse | ").AppendJoin(" | ", readers.Select(static r => r + " codes")).AppendLine(" |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
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
            sb.AppendLine("| Category | FeatherQR median | zxing-cpp median | FeatherQR total | zxing-cpp total |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var group in photos.GroupBy(static p => p.Category).OrderBy(static g => g.Key, StringComparer.Ordinal).Append(photos.GroupBy(static _ => "**All**").Single()))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {Median(group.Select(static p => p.FeatherQrMs)):F2} | {Median(group.Select(static p => p.ZXingCppMs)):F2} | {group.Sum(static p => p.FeatherQrMs):F0} | {group.Sum(static p => p.ZXingCppMs):F0} |");
            sb.AppendLine();
        }

        List(sb, "Gap: zxing-cpp has an agreed read, FeatherQR none", photos.Where(p => p.Reads[1].Any(agreed) && !p.Reads[0].Any(agreed)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes): {p.FeatherQrStatus}");
        List(sb, "Reverse: FeatherQR has an agreed read, zxing-cpp none", photos.Where(p => p.Reads[0].Any(agreed) && !p.Reads[1].Any(agreed)), static p => $"{p.Category}/{p.File} ({p.Width}x{p.Height}, {p.Labels.Count} codes)");

        // A FeatherQR read inside a code zxing-cpp read otherwise, or a text no other read confirms, is either a code read once or a misread; each is looked at by hand
        List(sb, "FeatherQR read a code zxing-cpp read with another text", photos.Where(p => p.Reads[0].Any(own => LabelOf(p, own) is var label && label >= 0 && p.Reads[1].Any(other => LabelOf(p, other) == label && other.Text != own.Text))), static p => $"{p.Category}/{p.File}");
        List(sb, "FeatherQR texts no other read confirms", photos.Where(p => p.Reads[0].Any(own => !agreed(own))), p => $"{p.Category}/{p.File}: {Show(p.Reads[0][0].Text)}");
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

    private static void WriteCsv(string path, Photo[] photos, Func<Read, bool> agreed)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine("category,file,width,height,codes,featherqr_status,featherqr,featherqr_located,zxingcpp,zxingcpp_codes,zxingnet,featherqr_ms,zxingcpp_ms");
        foreach (var p in photos)
        {
            var own = p.Reads[0].Any(agreed);
            var located = p.Reads[0].Any(r => LabelOf(p, r) >= 0);
            var cpp = p.Reads[1].Any(agreed);
            var cppCodes = p.Reads[1].Where(agreed).Select(r => LabelOf(p, r)).Where(static l => l >= 0).Distinct().Count();
            var net = p.Reads[2].Any(agreed);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.Category},{p.File},{p.Width},{p.Height},{p.Labels.Count},{p.FeatherQrStatus},{(own ? 1 : 0)},{(located ? 1 : 0)},{(cpp ? 1 : 0)},{cppCodes},{(net ? 1 : 0)},{p.FeatherQrMs:F3},{p.ZXingCppMs:F3}"));
        }
    }
}
