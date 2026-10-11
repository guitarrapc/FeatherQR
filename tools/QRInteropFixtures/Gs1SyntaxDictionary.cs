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
/// A reader of the GS1 Barcode Syntax Dictionary file, enough to drive the corpus: entries, their
/// components and linters, their pairing attributes, and the linters the release names. It is a
/// tool-side parser; the library's own table generator comes in a later phase of the GS1 plan.
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

/// <summary>
/// Reads the unit-test vectors out of the Syntax Dictionary's reference linter sources
/// (<c>src/lint_*.c</c>, <c>UNIT_TEST_PASS</c> / <c>UNIT_TEST_FAIL</c> with string literals). A
/// handful of tests build their value in a variable rather than a literal and are not imported.
/// </summary>
public static class Gs1LinterVectors
{
    private static readonly Regex vectorPattern = new(
        @"UNIT_TEST_(PASS|FAIL)\(\s*gs1_lint_\w+\s*,\s*((?:""(?:[^""\\]|\\.)*""\s*)+)(?:,\s*(GS1_LINTER_\w+)\s*,\s*(""(?:[^""\\]|\\.)*""))?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex anyVectorPattern = new(@"UNIT_TEST_(PASS|FAIL)\(", RegexOptions.Compiled);

    /// <summary>Every linter source in the directory, as (name, vectors, how many tests the file holds that are not literal).</summary>
    public static IEnumerable<(string Linter, Gs1LinterVector[] Vectors, int NotImported)> Read(string sourceDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "lint_*.c").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file).Substring("lint_".Length);
            if (name.StartsWith("_", StringComparison.Ordinal))
                continue; // lint__stubs.c: deprecated names with no tests
            var source = File.ReadAllText(file).Replace("\r\n", "\n");
            var vectors = vectorPattern.Matches(source).Select(m => new Gs1LinterVector
            {
                Value = CSource.Literal(m.Groups[2].Value),
                Pass = m.Groups[1].Value == "PASS",
                Error = m.Groups[3].Success ? m.Groups[3].Value : null,
                Markup = m.Groups[4].Success ? CSource.Literal(m.Groups[4].Value) : null,
            }).ToArray();
            var total = anyVectorPattern.Matches(source).Count;
            yield return (name, vectors, total - vectors.Length);
        }
    }
}

/// <summary>C string literals, as the test sources write them: adjacent literals concatenate, escapes are the usual ones.</summary>
public static class CSource
{
    private static readonly Regex literalPattern = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex escapePattern = new(@"\\(x[0-9a-fA-F]{1,2}|[0-7]{1,3}|.)", RegexOptions.Compiled);

    /// <summary>The value of one or more adjacent C string literals.</summary>
    public static string Literal(string text)
    {
        var parts = literalPattern.Matches(text);
        if (parts.Count == 0)
            throw new InvalidDataException($"Not a C string literal: {text}");
        return string.Concat(parts.Select(m => Unescape(m.Groups[1].Value)));
    }

    public static bool IsLiteral(string text) => text.Length > 0 && text[0] == '"';

    private static string Unescape(string body) => escapePattern.Replace(body, m =>
    {
        var escape = m.Groups[1].Value;
        if (escape[0] == 'x')
            return ((char)Convert.ToInt32(escape.Substring(1), 16)).ToString();
        if (escape.All(c => c >= '0' && c <= '7'))
            return ((char)Convert.ToInt32(escape, 8)).ToString();
        return escape switch { "n" => "\n", "t" => "\t", "r" => "\r", _ => escape };
    });

    /// <summary>Strips block comments and full-line comments, so a commented-out test is not read.</summary>
    public static string StripComments(string source)
    {
        source = Regex.Replace(source.Replace("\r\n", "\n"), @"/\*[\s\S]*?\*/", "");
        return string.Join("\n", source.Split('\n').Where(l => !Regex.IsMatch(l, @"^\s*//")));
    }

    /// <summary>
    /// Every statement <c>name(args…);</c> at the start of a line, its arguments split at top-level commas
    /// (string literals and nested parentheses respected), with the trailing line comment.
    /// </summary>
    public static IEnumerable<(string[] Args, string Comment)> Calls(string source, string name)
    {
        var pattern = new Regex(@"^\s*" + Regex.Escape(name) + @"\(([\s\S]*?)\);[ \t]*(//.*)?$", RegexOptions.Multiline);
        foreach (Match match in pattern.Matches(source))
        {
            var raw = match.Groups[1].Value;
            var args = new List<string>();
            var comments = new List<string>();
            var current = new System.Text.StringBuilder();
            var depth = 0;
            var inString = false;
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (inString)
                {
                    current.Append(c);
                    if (c == '\\' && i + 1 < raw.Length)
                        current.Append(raw[++i]);
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    current.Append(c);
                    continue;
                }
                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
                {
                    // A line comment between the arguments: kept as a note, never as an argument.
                    var end = raw.IndexOf('\n', i);
                    if (end < 0)
                        end = raw.Length;
                    comments.Add(raw.Substring(i + 2, end - i - 2).Trim());
                    i = end;
                    continue;
                }
                if (c == '(' || c == '[')
                    depth++;
                else if (c == ')' || c == ']')
                    depth--;
                if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }
            args.Add(current.ToString().Trim());
            if (match.Groups[2].Success)
                comments.Add(match.Groups[2].Value.Substring(2).Trim());
            yield return (args.ToArray(), string.Join("; ", comments.Where(c => c.Length > 0)));
        }
    }
}
