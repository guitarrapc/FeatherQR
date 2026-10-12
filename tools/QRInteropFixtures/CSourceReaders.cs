using System.Text.RegularExpressions;

namespace QRInteropFixtures;

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
    /// (string literals and nested parentheses respected), with the line comments inside and after the call.
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
