using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// The deterministic Standard QR fixture corpus. Cases are chosen to cover every mode
/// (Numeric / Alphanumeric / Byte) and ECC level, plus capacity boundaries, ECI (UTF-8)
/// payloads, and the smallest/largest versions. Everything is a fixed literal or a
/// fixed repetition, no randomness, no timestamps, so regeneration is reproducible.
/// </summary>
public static class StandardQrCorpus
{
    public static IReadOnlyList<FixtureCaseDefinition> Cases { get; } = Build();

    private static List<FixtureCaseDefinition> Build()
    {
        var cases = new List<FixtureCaseDefinition>();

        // Every mode × every ECC level at small versions.
        foreach (var ecc in new[] { "L", "M", "Q", "H" })
        {
            var suffix = ecc.ToLowerInvariant();
            cases.Add(new($"numeric-10digits-{suffix}", "1234567890", ecc));
            cases.Add(new($"alphanumeric-helloworld-{suffix}", "HELLO WORLD", ecc));
            cases.Add(new($"byte-url-{suffix}", "https://example.com/path?query=value&lang=en", ecc));
        }

        // Mid-size and maximum-capacity versions.
        cases.Add(new("numeric-500digits-m", Digits(500), "M"));
        cases.Add(new("numeric-500digits-h", Digits(500), "H"));
        cases.Add(new("numeric-7089digits-l", Digits(7089), "L")); // exactly version 40-L numeric capacity
        cases.Add(new("byte-long-sentence-m", RepeatSentence(900), "M"));

        // Alphanumeric charset coverage and version 1-L capacity boundary (25 chars).
        cases.Add(new("alphanumeric-fullset-m", "ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:0123456789", "M"));
        cases.Add(new("alphanumeric-25chars-v1-boundary-l", "HELLO WORLD 0123456789 :A", "L"));

        // UTF-8 (ECI) payloads.
        cases.Add(new("byte-utf8-japanese-m", "こんにちは世界、QRコードのテストです。", "M", Utf8: true));
        cases.Add(new("byte-utf8-japanese-h", "こんにちは世界、QRコードのテストです。", "H", Utf8: true));
        cases.Add(new("byte-utf8-emoji-m", "Emoji 🎌 test ✅ done", "M", Utf8: true));

        // Kanji mode (ISO/IEC 18004 8.4.5): the other encoder's symbols this library's decoder reads
        // and its encoder is compared with, module for module (KanjiEncoderOracleTest).
        // ZXing.Net encodes through .NET CP932, so payloads stay clear of the seven
        // cells where CP932 and JIS X 0208 disagree; those live in the qrtool-generated
        // rMQR case, where the corpus supplies the Shift_JIS bytes directly.
        cases.Add(new("kanji-hello-m", "こんにちは世界", "M", Mode: "Kanji"));
        cases.Add(new("kanji-hello-h", "こんにちは世界", "H", Mode: "Kanji"));
        cases.Add(new("kanji-mixed-scripts-m", "日本語漢字符号化試験用文字列", "M", Mode: "Kanji"));
        cases.Add(new("kanji-hiragana-katakana-l", "ひらがなカタカナ漢字混在", "L", Mode: "Kanji"));
        cases.Add(new("kanji-long-q", RepeatKanji(120), "Q", Mode: "Kanji"));

        // The 12-bit Kanji count band (versions 27-40), which the cases above do not reach: one
        // character past version 26-M's 652, and version 40-L filled (1,817).
        cases.Add(new("kanji-v27-edge-m", CycleCells(653), "M", Mode: "Kanji"));
        cases.Add(new("kanji-v40-max-l", CycleCells(1817), "L", Mode: "Kanji"));

        return cases;
    }

    /// <summary>
    /// Cells from across the table, the edges of both Shift_JIS ranges among them (0x8140, 0x9FFC,
    /// 0xE040, 0xEAA4), so that a symbol exercises both offsets of the Kanji value.
    /// </summary>
    public const string KanjiCellCycle = "　滌漾熙亜弌腕ΩЖ─┼０Ａぁヶ日本語漢字符号化試験用文字列";

    private static string CycleCells(int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
            sb.Append(KanjiCellCycle[i % KanjiCellCycle.Length]);
        return sb.ToString();
    }

    /// <summary>A fixed cyclic run of JIS X 0208 characters, for the larger Kanji versions.</summary>
    private static string RepeatKanji(int count)
    {
        const string Cycle = "日本語漢字符号化試験用文字列";
        var sb = new StringBuilder(count);
        while (sb.Length < count)
        {
            sb.Append(Cycle, 0, Math.Min(Cycle.Length, count - sb.Length));
        }
        return sb.ToString();
    }

    private static string Digits(int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            sb.Append((char)('0' + (i % 10)));
        }
        return sb.ToString();
    }

    private static string RepeatSentence(int minLength)
    {
        const string Sentence = "The quick brown fox jumps over the lazy dog 0123456789. ";
        var sb = new StringBuilder(minLength + Sentence.Length);
        while (sb.Length < minLength)
        {
            sb.Append(Sentence);
        }
        return sb.ToString();
    }
}
