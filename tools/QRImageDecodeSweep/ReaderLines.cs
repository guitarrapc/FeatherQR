using System.Text.Json;

namespace QRImageDecodeSweep;

/// <summary>
/// Readers run elsewhere, in their own harness, and read here from a file of results: one JSON object a line, <c>{"dec": reader, "cat": directory, "img": file, "texts": [...], "ms": milliseconds}</c>, one line a reader and image.
/// An image is matched by its directory and its file name without the extension, so the same lines serve a set whose images were written out again in another format.
/// </summary>
internal sealed class ReaderLines
{
    /// <summary>What one reader made of one image: every text it returned, and its own clock's time for the call.</summary>
    public sealed record Line(string[] Texts, double Milliseconds);

    public static readonly ReaderLines None = new([], []);

    private readonly Dictionary<(string Reader, string Directory, string Stem), Line> lines;

    private ReaderLines(string[] readers, Dictionary<(string Reader, string Directory, string Stem), Line> lines)
    {
        Readers = readers;
        this.lines = lines;
    }

    /// <summary>The readers the file holds, in the order they first appear.</summary>
    public string[] Readers { get; }

    public int Count => lines.Count;

    public Line? Find(string reader, string directory, string file)
        => lines.GetValueOrDefault((reader, directory, Path.GetFileNameWithoutExtension(file)));

    /// <summary>The images of <paramref name="reader"/> the file holds, as directory and stem.</summary>
    public IEnumerable<(string Directory, string Stem)> Images(string reader)
        => lines.Keys.Where(k => k.Reader == reader).Select(static k => (k.Directory, k.Stem));

    public static ReaderLines Load(string path)
    {
        var readers = new List<string>();
        var lines = new Dictionary<(string Reader, string Directory, string Stem), Line>();
        foreach (var text in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var reader = root.GetProperty("dec").GetString()!;
            if (!readers.Contains(reader))
                readers.Add(reader);
            string[] texts = [.. root.GetProperty("texts").EnumerateArray().Select(static t => t.GetString() ?? "")];
            var key = (reader, root.GetProperty("cat").GetString()!, Path.GetFileNameWithoutExtension(root.GetProperty("img").GetString()!));
            if (!lines.TryAdd(key, new Line(texts, root.GetProperty("ms").GetDouble())))
                throw new InvalidDataException($"{path}: two lines for {key}");
        }
        return new ReaderLines([.. readers], lines);
    }

    /// <summary>Takes <c>--lines &lt;file&gt;</c> out of <paramref name="arguments"/>.</summary>
    public static ReaderLines FromArguments(List<string> arguments)
    {
        var at = arguments.IndexOf("--lines");
        if (at < 0)
            return None;
        if (at + 1 >= arguments.Count)
            throw new ArgumentException("--lines takes a file of result lines");
        var lines = Load(arguments[at + 1]);
        arguments.RemoveRange(at, 2);
        return lines;
    }
}
