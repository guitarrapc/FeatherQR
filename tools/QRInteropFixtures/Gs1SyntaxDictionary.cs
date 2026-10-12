using System.Text.RegularExpressions;

namespace QRInteropFixtures;

/// <summary>One component of an AI's data field: type <c>N</c>, <c>X</c>, <c>Y</c> or <c>Z</c>, a fixed or variable length, optional or not, and its linters.</summary>
public sealed record Gs1Component(char Type, int Min, int Max, bool Optional, string[] Linters)
{
    public bool IsVariable => Min != Max;
}

/// <summary>One entry line of the Syntax Dictionary, covering one AI or a range.</summary>
public sealed record Gs1AiEntry(string FirstAi, string LastAi, bool PredefinedLength, bool DlAttribute, Gs1Component[] Components, string[] Req, string[] Ex, string? DlPKey, string Title, string Line)
{
    public IEnumerable<string> Ais
    {
        get
        {
            var first = int.Parse(FirstAi);
            var last = int.Parse(LastAi);
            for (var ai = first; ai <= last; ai++)
                yield return ai.ToString().PadLeft(FirstAi.Length, '0');
        }
    }
}

/// <summary>
/// A reader of the GS1 Barcode Syntax Dictionary file: entries, their components and linters, their pairing
/// attributes, and the linters the release names. The fixture tool uses it to build the GS1 corpus, and the test
/// project links this file to generate the library's AI table from the same reading.
/// </summary>
public sealed class Gs1SyntaxDictionary
{
    private static readonly Regex componentPattern = new(@"^(\[)?([NXYZ])(\.\.)?(\d+)\]?((?:,[a-z0-9]+)*)$", RegexOptions.Compiled);

    public string Release { get; }
    public IReadOnlyList<Gs1AiEntry> Entries { get; }
    private readonly Dictionary<string, Gs1AiEntry> byAi = new(StringComparer.Ordinal);

    public Gs1SyntaxDictionary(string path)
    {
        var release = "";
        var entries = new List<Gs1AiEntry>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# Release:", StringComparison.Ordinal))
                release = line.Substring("# Release:".Length).Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var entry = ParseEntry(line);
            entries.Add(entry);
            foreach (var ai in entry.Ais)
                byAi.Add(ai, entry);
        }
        if (release.Length == 0)
            throw new InvalidDataException($"No '# Release:' header in {path}.");
        Release = release;
        Entries = entries;
    }

    public bool TryGet(string ai, out Gs1AiEntry entry) => byAi.TryGetValue(ai, out entry!);

    public IEnumerable<string> Ais => byAi.Keys.OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>The linter names the entries use, in first-use order.</summary>
    public IReadOnlyList<string> Linters => Entries.SelectMany(e => e.Components).SelectMany(c => c.Linters).Distinct(StringComparer.Ordinal).ToList();

    private static Gs1AiEntry ParseEntry(string line)
    {
        var title = "";
        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            title = line.Substring(hash + 1).Trim();
            line = line.Substring(0, hash);
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var aiToken = tokens[0];
        var dash = aiToken.IndexOf('-');
        var firstAi = dash < 0 ? aiToken : aiToken.Substring(0, dash);
        var lastAi = dash < 0 ? aiToken : aiToken.Substring(dash + 1);

        var index = 1;
        var flags = "";
        if (index < tokens.Length && tokens[index].All(c => c == '*' || c == '?'))
            flags = tokens[index++];

        var components = new List<Gs1Component>();
        var req = new List<string>();
        var ex = new List<string>();
        string? dlpkey = null;
        for (; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.StartsWith("req=", StringComparison.Ordinal))
                req.Add(token.Substring(4));
            else if (token.StartsWith("ex=", StringComparison.Ordinal))
                ex.Add(token.Substring(3));
            else if (token == "dlpkey")
                dlpkey = "";
            else if (token.StartsWith("dlpkey=", StringComparison.Ordinal))
                dlpkey = token.Substring(7);
            else
            {
                var match = componentPattern.Match(token);
                if (!match.Success)
                    throw new InvalidDataException($"Unrecognised component '{token}' in dictionary line: {line}");
                var optional = match.Groups[1].Success;
                var type = match.Groups[2].Value[0];
                var variable = match.Groups[3].Success;
                var length = int.Parse(match.Groups[4].Value);
                var linters = match.Groups[5].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                components.Add(new Gs1Component(type, variable ? 1 : length, length, optional, linters));
            }
        }

        return new Gs1AiEntry(firstAi, lastAi, flags.Contains('*'), flags.Contains('?'), components.ToArray(), req.ToArray(), ex.ToArray(), dlpkey, title, line);
    }
}
