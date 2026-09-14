using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// One Structured Append set. <c>MinVersion</c>/<c>MaxVersion</c> bound the version a
/// balancing encoder may choose; <c>Parts</c> is the split an explicit-parts encoder is
/// handed (equal character counts), chosen so every part fits within <c>MaxVersion</c>.
/// </summary>
public sealed record StructuredAppendCaseDefinition(string Id, string PayloadText, string ErrorCorrectionLevel, int MinVersion, int MaxVersion, int Parts);

/// <summary>A fixture generator that produces every symbol of one Structured Append set.</summary>
public interface IStructuredAppendFixtureGenerator
{
    /// <summary>Directory name under Fixtures/StandardQrStructuredAppend/.</summary>
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Symbols in wire order. Manifests carry what the encoder itself reports; the sanity
    /// gate fills whatever the encoder does not expose (per-symbol text, parity) from the
    /// reader and cross-checks the rest.
    /// </summary>
    GeneratedFixture[] Generate(StructuredAppendCaseDefinition caseDefinition);
}

/// <summary>
/// The deterministic Structured Append corpus: every mode, both charsets, the smallest
/// (2) and largest (16) set, and set sizes in between. Fixed literals and fixed
/// repetitions only, so regeneration is byte-reproducible for a given generator version.
/// </summary>
public static class StructuredAppendCorpus
{
    public static IReadOnlyList<StructuredAppendCaseDefinition> Cases { get; } =
    [
        // Odd repetition counts throughout: an even repetition XORs to 0 in every charset
        // and would let a parity computed over the wrong bytes pass the gate.
        new("numeric-1201digits-v6-m", Digits(1201), "M", 1, 6, Parts: 6),
        new("alphanumeric-500chars-v4-l", Alphanumeric(500), "L", 1, 4, Parts: 5),
        new("byte-ascii-sentence-v10-q", RepeatSentence(600), "Q", 1, 10, Parts: 5),
        // Seven accented characters per repetition: with an even count the ISO-8859-1 and
        // UTF-8 parities coincide and the set could not tell the two charsets apart.
        new("byte-latin1-diacritics-v3-m", Repeat("Crème brûlée à la carte, jalapeño, naïve café. ", 7), "M", 1, 3, Parts: 12),
        new("byte-utf8-japanese-v5-m", Repeat("こんにちは世界、QRコードの分割テストです。", 3), "M", 1, 5, Parts: 4),
        new("two-symbols-min-v1-l", "STRUCTURED APPEND 2 OF 2", "L", 1, 1, Parts: 2),
        new("sixteen-symbols-max-v2-l", RepeatSentence(470), "L", 1, 2, Parts: 16),
    ];

    private static string Digits(int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
            sb.Append((char)('0' + (i * 7 + 3) % 10));
        return sb.ToString();
    }

    private static string Alphanumeric(int count)
    {
        const string set = "ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:0123456789";
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
            sb.Append(set[(i * 11 + 5) % set.Length]);
        return sb.ToString();
    }

    private static string RepeatSentence(int length)
    {
        const string sentence = "The quick brown fox jumps over the lazy dog. ";
        var sb = new StringBuilder(length);
        while (sb.Length < length)
            sb.Append(sentence);
        return sb.ToString(0, length);
    }

    private static string Repeat(string text, int times)
    {
        var sb = new StringBuilder(text.Length * times);
        for (var i = 0; i < times; i++)
            sb.Append(text);
        return sb.ToString();
    }
}
