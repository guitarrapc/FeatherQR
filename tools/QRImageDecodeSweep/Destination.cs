using System.Diagnostics;
using System.Globalization;
using System.Text;
using FeatherQR;
using FeatherQR.Tests;

namespace QRImageDecodeSweep;

/// <summary>
/// What a destination too short for a symbol's text costs the decoders that read around a single finder, Micro QR and rMQR:
/// each render decoded into a sized destination, one a character short and one of 2 characters, with the status, version and
/// time of each (the fastest of a few calls).
/// </summary>
/// <remarks>
/// Two sets a symbology. The random renders are the ones the decoder records' figures were measured on: a random version, level
/// and text, turned at random, each pixel the mean of 2 × 2 point samples, with uniform noise, from a fixed seed, so two trees
/// draw the same images. The known renders are the ones whose scan ranks a finder-like pattern inside the symbol after its
/// finder, which the skip exists for (the destination contract test draws the same ones).
/// </remarks>
internal static class Destination
{
    public const int DefaultCount = 600;

    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static readonly string[] columns = ["render", "kind", "ppm", "angle", "noise", "sized", "sizedVersion", "text", "short", "shortVersion", "tiny", "tinyVersion", "sizedUs", "shortUs", "tinyUs"];

    /// <summary>One render's three decodes: the sized one's status, version and text, the others' status and version, and each one's time in microseconds.</summary>
    public sealed record Row(string Render, string Kind, string Ppm, string Angle, string Noise, string Sized, string SizedVersion, string Text, string Short, string ShortVersion, string Tiny, string TinyVersion, double SizedUs, double ShortUs, double TinyUs)
    {
        public bool Read => Sized == nameof(DecodeStatus.Success);

        public double ShortRatio => ShortUs / SizedUs;

        public double TinyRatio => TinyUs / SizedUs;

        /// <summary>Whether 2 characters are too few for the text; a text of 2 fits them, and that call is a sized one.</summary>
        public bool TinyTooShort => Text.Length > 2;
    }

    public static List<Row> Run(string symbology, int count)
    {
        var rows = new List<Row>();
        foreach (var (name, luminance, width, height, ppm, angle, noise, kind) in symbology == Symbologies.MicroQr ? MicroRenders(count) : RmqrRenders(count))
            rows.Add(Measure(symbology, name, kind, luminance, width, height, ppm, angle, noise));
        return rows;
    }

    private static Row Measure(string symbology, string name, string kind, byte[] luminance, int width, int height, double ppm, double angle, double noise)
    {
        var sized = new char[symbology == Symbologies.MicroQr ? MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4) : RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var (sizedStatus, sizedVersion, sizedWritten) = Decode(symbology, luminance, width, height, sized);
        var text = sizedStatus == DecodeStatus.Success ? new string(sized, 0, sizedWritten) : "";
        var sizedUs = Time(() => Decode(symbology, luminance, width, height, sized), 5);
        if (text.Length < 2)
            return new Row(name, kind, F(ppm), F(angle), F(noise), sizedStatus.ToString(), sizedVersion, text, "-", "", "-", "", sizedUs, 0, 0);

        var shortDestination = new char[text.Length - 1];
        var (shortStatus, shortVersion, _) = Decode(symbology, luminance, width, height, shortDestination);
        var shortUs = Time(() => Decode(symbology, luminance, width, height, shortDestination), 3);
        var tiny = new char[2];
        var (tinyStatus, tinyVersion, _) = Decode(symbology, luminance, width, height, tiny);
        var tinyUs = Time(() => Decode(symbology, luminance, width, height, tiny), 3);
        return new Row(name, kind, F(ppm), F(angle), F(noise), sizedStatus.ToString(), sizedVersion, text, shortStatus.ToString(), shortVersion, tinyStatus.ToString(), tinyVersion, sizedUs, shortUs, tinyUs);
    }

    private static (DecodeStatus Status, string Version, int Written) Decode(string symbology, byte[] luminance, int width, int height, char[] destination)
    {
        if (symbology == Symbologies.MicroQr)
        {
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);
            return (info.Status, info.Version.ToString(), written);
        }
        RmQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var rmqrWritten, out var rmqrInfo);
        return (rmqrInfo.Status, rmqrInfo.Version.ToString(), rmqrWritten);
    }

    private static double Time(Action action, int runs)
    {
        var best = double.MaxValue;
        for (var r = 0; r < runs; r++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            best = Math.Min(best, stopwatch.Elapsed.TotalMicroseconds);
        }
        return best;
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static IEnumerable<(string Name, byte[] Luminance, int Width, int Height, double Ppm, double Angle, double Noise, string Kind)> MicroRenders(int count)
    {
        (MicroQRVersion Version, MicroQREccLevel Level)[] kinds =
        [
            (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly), (MicroQRVersion.M2, MicroQREccLevel.L), (MicroQRVersion.M2, MicroQREccLevel.M),
            (MicroQRVersion.M3, MicroQREccLevel.L), (MicroQRVersion.M3, MicroQREccLevel.M), (MicroQRVersion.M4, MicroQREccLevel.L),
            (MicroQRVersion.M4, MicroQREccLevel.M), (MicroQRVersion.M4, MicroQREccLevel.Q),
        ];
        var random = new Random(20260930);
        for (var i = 0; i < count; i++)
        {
            var (version, level) = kinds[random.Next(kinds.Length)];
            var data = Generate(random, 34, content => MicroQRCodeGenerator.Create(content, level, new MicroQRCodeGeneratorOptions { Version = version }));
            if (data is null)
                continue;
            var ppm = 2.5 + random.NextDouble() * 5.5;
            var angle = random.NextDouble() * 360;
            var noise = random.NextDouble() * 30;
            var (luminance, side) = Turned((row, column) => data[row, column], data.Size, data.Size, ppm, angle, noise, random, (int)(data.Size * ppm * 1.5) + 16, mirrored: false);
            yield return ($"random {i}", luminance, side, side, ppm, angle, noise, $"{version}-{level}");
        }

        // The destination contract test's renders, mirrored too
        foreach (var (content, level, ppm, angle) in new[] { ("63028458518747710056101171", MicroQREccLevel.M, 5.1, 93.0), ("98411748017212729364", MicroQREccLevel.Q, 3.6, 133.0) })
        {
            var data = MicroQRCodeGenerator.Create(content, level, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 });
            foreach (var mirrored in new[] { false, true })
            {
                var (luminance, side) = Turned((row, column) => data[row, column], data.Size, data.Size, ppm, angle, 0, random, (int)(data.Size * ppm * 1.5) + 16, mirrored);
                yield return ($"known {content[..4]}{(mirrored ? " mirrored" : "")}", luminance, side, side, ppm, angle, 0, $"M4-{level}");
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Luminance, int Width, int Height, double Ppm, double Angle, double Noise, string Kind)> RmqrRenders(int count)
    {
        var random = new Random(20261001);
        for (var i = 0; i < count; i++)
        {
            var version = (RmQRVersion)random.Next(32);
            var level = random.Next(2) == 0 ? RmQREccLevel.M : RmQREccLevel.H;
            var data = Generate(random, 60, content => RmQRCodeGenerator.Create(content, level, new RmQRCodeGeneratorOptions { Version = version }));
            if (data is null)
                continue;
            var ppm = 2.5 + random.NextDouble() * 5.5;
            var angle = random.NextDouble() * 360;
            var noise = random.NextDouble() * 30;
            var (luminance, side) = Turned((row, column) => data[row, column], data.Width, data.Height, ppm, angle, noise, random, (int)(data.Width * ppm * 1.2) + 16, mirrored: false);
            yield return ($"random {i}", luminance, side, side, ppm, angle, noise, $"{version}-{level}");
        }

        // The destination contract test's renders: through a frame's grid, the module boundaries and the coverage re-read
        var grid = RmQRCodeGenerator.Create("HELLO12345ABCDE", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x27 });
        var (gridImage, gridWidth, gridHeight) = Nearest(grid, 4f);
        yield return ("known R13x27 grid", gridImage, gridWidth, gridHeight, 4, 0, 0, "R13x27-M");
        var boundaries = RmQRCodeGenerator.Create("VZA$R9CMKV0T7W.C", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x77 });
        var (boundaryImage, boundaryWidth, boundaryHeight) = Nearest(boundaries, 1.38f);
        yield return ("known R13x77 boundaries", boundaryImage, boundaryWidth, boundaryHeight, 1.38, 0, 0, "R13x77-M");
        var coverage = RmQRCodeGenerator.Create("41516104048408666070497882893323", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R15x139 });
        var (coverageImage, coverageWidth, coverageHeight) = BilinearUpscaleRenderer.Render((row, column) => coverage[row, column], coverage.Width, coverage.Height, 1.3069739f);
        yield return ("known R15x139 coverage", FlipHorizontally(coverageImage, coverageWidth, coverageHeight), coverageWidth, coverageHeight, 1.31, 0, 0, "R15x139-M");
    }

    /// <summary>A random text of up to <paramref name="maxLength"/> characters in one mode that the symbol holds, shortened until it does.</summary>
    private static T? Generate<T>(Random random, int maxLength, Func<string, T> create)
        where T : class
    {
        for (var length = 2 + random.Next(maxLength); length >= 2; length--)
        {
            var mode = random.Next(3);
            var content = new string(Enumerable.Range(0, length).Select(_ => mode switch
            {
                0 => (char)('0' + random.Next(10)),
                1 => Alphanumeric[random.Next(Alphanumeric.Length)],
                _ => (char)('a' + random.Next(26)),
            }).ToArray());
            try
            {
                return create(content);
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// The symbol turned about the centre of a square image, each pixel the mean of 2 × 2 point samples (dark 20, light 235), then
    /// uniform noise; mirrored, transposed.
    /// </summary>
    private static (byte[] Luminance, int Side) Turned(Func<int, int, bool> isDark, int columns, int rows, double ppm, double angle, double noise, Random random, int side, bool mirrored)
    {
        var luminance = new byte[side * side];
        var cos = Math.Cos(-angle * Math.PI / 180);
        var sin = Math.Sin(-angle * Math.PI / 180);
        var centre = side / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var px = x + 0.25 + 0.5 * sx - centre;
                        var py = y + 0.25 + 0.5 * sy - centre;
                        var column = (int)Math.Floor((px * cos - py * sin) / ppm + columns / 2.0);
                        var row = (int)Math.Floor((px * sin + py * cos) / ppm + rows / 2.0);
                        sum += column >= 0 && row >= 0 && column < columns && row < rows && isDark(row, column) ? 20 : 235;
                    }
                }
                var value = noise == 0 ? sum / 4 : sum / 4 + (random.NextDouble() * 2 - 1) * noise;
                luminance[mirrored ? x * side + y : y * side + x] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        }
        return (luminance, side);
    }

    /// <summary>Each pixel the module under its centre, quiet zone included.</summary>
    private static (byte[] Luminance, int Width, int Height) Nearest(RmQRCodeData data, float ppm)
    {
        var width = (int)Math.Ceiling(data.Width * ppm);
        var height = (int)Math.Ceiling(data.Height * ppm);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = Math.Min(data.Width - 1, (int)((x + 0.5) / ppm));
                var row = Math.Min(data.Height - 1, (int)((y + 0.5) / ppm));
                luminance[y * width + x] = data[row, column] ? (byte)0 : (byte)255;
            }
        }
        return (luminance, width, height);
    }

    private static byte[] FlipHorizontally(byte[] luminance, int width, int height)
    {
        var flipped = new byte[luminance.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                flipped[y * width + width - 1 - x] = luminance[y * width + x];
        }
        return flipped;
    }

    public static void Write(string path, IEnumerable<Row> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(string.Join(',', columns));
        foreach (var r in rows)
            writer.WriteLine(string.Join(',', r.Render, r.Kind, r.Ppm, r.Angle, r.Noise, r.Sized, r.SizedVersion, Quote(r.Text), r.Short, r.ShortVersion, r.Tiny, r.TinyVersion, F(r.SizedUs), F(r.ShortUs), F(r.TinyUs)));
    }

    public static List<Row> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0] != string.Join(',', columns))
            throw new InvalidDataException($"{path} is not a destination result file of this tool.");
        return lines.Skip(1).Where(static l => l.Length > 0).Select(static line =>
        {
            var f = Split(line);
            return new Row(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], double.Parse(f[12], CultureInfo.InvariantCulture), double.Parse(f[13], CultureInfo.InvariantCulture), double.Parse(f[14], CultureInfo.InvariantCulture));
        }).ToList();
    }

    /// <summary>
    /// Per set: the renders, those a sized destination reads, whether a short destination reports what the rules say (one
    /// character short and 2 characters, <see cref="DecodeStatus.DestinationTooSmall"/> with the sized call's version), and the
    /// cost of each against the sized call; then each known render.
    /// </summary>
    public static string Summary(string symbology, IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {symbology}, a destination too short");
        sb.AppendLine();
        sb.AppendLine("| Set | Renders | Read sized | Short as the rules say | Short / sized: median, 95 %, worst | 2 characters / sized: median, 95 %, worst |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in rows.GroupBy(static r => r.Render.StartsWith("known", StringComparison.Ordinal) ? "known" : "random"))
        {
            var measured = group.Where(static r => r.Read && r.Text.Length >= 2).ToList();
            var asRules = measured.Count(static r => r.Short == nameof(DecodeStatus.DestinationTooSmall) && r.ShortVersion == r.SizedVersion && (!r.TinyTooShort || (r.Tiny == nameof(DecodeStatus.DestinationTooSmall) && r.TinyVersion == r.SizedVersion)));
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {group.Count(static r => r.Read)} | {asRules} of {measured.Count} | {Spread(measured.Select(static r => r.ShortRatio))} | {Spread(measured.Where(static r => r.TinyTooShort).Select(static r => r.TinyRatio))} |");
        }
        sb.AppendLine();
        foreach (var r in rows.Where(static r => r.Render.StartsWith("known", StringComparison.Ordinal)))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Render} ({r.Kind}): sized {r.Sized}, short {r.Short} {r.ShortRatio:F2} times, 2 characters {r.Tiny} {r.TinyRatio:F2} times");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Two result files of the same set from two trees, render for render: every status, version or text that moved, and the cost before and after.</summary>
    public static int Compare(string beforePath, string afterPath)
    {
        var before = Read(beforePath);
        var afterByRender = Read(afterPath).ToDictionary(static r => r.Render);
        var moved = new List<string>();
        foreach (var b in before)
        {
            if (!afterByRender.TryGetValue(b.Render, out var a))
            {
                moved.Add($"{b.Render}: not in {afterPath}");
                continue;
            }
            if ((b.Sized, b.SizedVersion, b.Text, b.Short, b.ShortVersion, b.Tiny, b.TinyVersion) != (a.Sized, a.SizedVersion, a.Text, a.Short, a.ShortVersion, a.Tiny, a.TinyVersion))
                moved.Add($"{b.Render} ({b.Kind}): {b.Sized} {b.SizedVersion} '{b.Text}', short {b.Short} {b.ShortVersion}, 2 characters {b.Tiny} {b.TinyVersion} -> {a.Sized} {a.SizedVersion} '{a.Text}', short {a.Short} {a.ShortVersion}, 2 characters {a.Tiny} {a.TinyVersion}");
        }

        Console.WriteLine("| | Short / sized: median, 95 %, worst | 2 characters / sized: median, 95 %, worst |");
        Console.WriteLine("|---|---|---|");
        foreach (var (name, rows) in new[] { ("before", before), ("after", afterByRender.Values.ToList()) })
        {
            var measured = rows.Where(static r => r.Read && r.Text.Length >= 2).ToList();
            Console.WriteLine($"| {name} | {Spread(measured.Select(static r => r.ShortRatio))} | {Spread(measured.Where(static r => r.TinyTooShort).Select(static r => r.TinyRatio))} |");
        }
        Console.WriteLine();
        Console.WriteLine(moved.Count == 0 ? $"{before.Count:N0} renders, no status, version or text moved." : $"Moved ({moved.Count:N0}):");
        foreach (var line in moved)
            Console.WriteLine($"  {line}");
        return 0;
    }

    private static string Spread(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(static v => v).ToList();
        if (sorted.Count == 0)
            return "-";
        return string.Create(CultureInfo.InvariantCulture, $"{sorted[sorted.Count / 2]:F2}, {sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))]:F2}, {sorted[^1]:F2}");
    }

    private static string Quote(string value) => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
