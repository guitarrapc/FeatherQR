using System.Text;

namespace QRImageDecodeSweep;

/// <summary>The per-image result file: the key columns, a digest of the image's pixels, then status and one 0/1 column per reader.</summary>
internal static class Csv
{
    private static readonly string[] resultColumns = ["image", "status", "featherqr", "misread", "zxingcpp", "zxingnet"];

    public static void Write(string path, string[] keyColumns, IEnumerable<ResultRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(string.Join(',', keyColumns.Concat(resultColumns)));
        foreach (var row in rows)
        {
            var fields = row.Key.Select(Quote).Append(row.Image).Append(Quote(row.Status)).Append(Bit(row.FeatherQr)).Append(Bit(row.Misread)).Append(Bit(row.ZXingCpp)).Append(Bit(row.ZXingNet));
            writer.WriteLine(string.Join(',', fields));
        }
    }

    public static (string[] KeyColumns, List<ResultRow> Rows) Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = Split(lines[0]);
        var keyCount = header.Length - resultColumns.Length;
        if (keyCount < 1 || !header.AsSpan(keyCount).SequenceEqual(resultColumns))
            throw new InvalidDataException($"{path} is not a result file of this tool.");

        var rows = new List<ResultRow>(lines.Length - 1);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                continue;
            var f = Split(line);
            rows.Add(new ResultRow(f[..keyCount], f[keyCount], f[keyCount + 1], f[keyCount + 2] == "1", f[keyCount + 3] == "1", f[keyCount + 4] == "1", f[keyCount + 5] == "1"));
        }
        return (header[..keyCount], rows);
    }

    private static string Bit(bool value) => value ? "1" : "0";

    private static string Quote(string field) => field.AsSpan().IndexOfAny(',', '"') < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";

    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return [.. fields];
    }
}
