using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Test-side text helpers for the GS1 fixtures: the (AI) text of a fixture or of the Syntax Engine's output
/// as (AI, value) pairs, and an element string built from such pairs with the separators the General
/// Specifications require. They stand in for the library's own conversions until those exist (Phase 4), and
/// they skip rather than guess: text with an AI the table does not know or that is not <c>(digits)value</c>
/// pairs returns <c>null</c>.
/// </summary>
public static class Gs1TestText
{
    public const char Gs = '\u001D';

    /// <summary>Parses <c>(01)…(10)…</c> with <c>\(</c> as a literal parenthesis, or <c>null</c> when the text is not such pairs.</summary>
    public static List<(string Ai, string Value)>? ParseBracketed(string text)
    {
        var pairs = new List<(string, string)>();
        var position = 0;
        while (position < text.Length)
        {
            if (text[position] != '(')
                return null;
            var close = text.IndexOf(')', position);
            if (close < 0)
                return null;
            var ai = text.Substring(position + 1, close - position - 1);
            if (ai.Length < 2 || ai.Length > 4 || !ai.All(char.IsAsciiDigit))
                return null;
            position = close + 1;
            var value = new System.Text.StringBuilder();
            while (position < text.Length)
            {
                if (text[position] == '\\' && position + 1 < text.Length && text[position + 1] == '(')
                {
                    value.Append('(');
                    position += 2;
                    continue;
                }
                if (text[position] == '(')
                    break;
                value.Append(text[position++]);
            }
            pairs.Add((ai, value.ToString()));
        }
        return pairs;
    }

    /// <summary>The element string of the pairs: a separator after every element but the last whose AI is not of predefined length. <c>null</c> when an AI is not in the table.</summary>
    public static string? ToElementString(List<(string Ai, string Value)> pairs)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (ai, value) = pairs[i];
            if (!Gs1AiTable.TryFind(ai, out var entry))
                return null;
            sb.Append(ai).Append(value);
            if (i < pairs.Count - 1 && !entry.IsPredefinedLength)
                sb.Append(Gs);
        }
        return sb.ToString();
    }
}
