using System.Text;

/// <summary>
/// Measures Structured Append encoding against encoding its planned chunks individually.
/// </summary>
/// <remarks>
/// The baseline uses the balanced split's chunks and shared version, so Ratio normally measures planning, charset selection and parity overhead.
/// Allocations differ by the result array. Boost baselines use the level selected by the set.
/// For Kanji-eligible mixed text, Optimal may choose fewer Kanji symbols than the Single UTF-8 baseline, so its ratio also includes that reduction.
/// Cases cover version limits, uneven density, UTF-8, Kanji, frequent mode changes and U+FEFF boundaries.
/// Fixture definitions are the authority for content sizes. Selected versions and symbol counts are results, not fixed benchmark contracts.
/// </remarks>
public class QRCodeStructuredAppendEncode
{
    private static readonly string[] shapeKeys =
    [
        "byte-45k-any", "byte-45k-v40", "byte-4k-max10",
        "numeric-100k-any", "alnum-65k-any", "mixed-40k-any", "uneven-40k-any", "utf8-15k-any", "utf8-15k-mixed", "marked-40k-any",
        "kanji-15k-any", "kanji-15k-mixed", "cells-15k-any", "cells-15k-utf8",
        "kanji-1k-max10", "cells-1k-max10", "interleaved-1k-max10", "interleaved-1k-utf8",
        "byte-45k-boost", "numeric-100k-boost",
    ];

    private string _content = default!;
    private QRCodeGeneratorOptions _single;
    private QRCodeGeneratorOptions _optimal;
    private QRCodeGeneratorOptions _chunkOptions;
    private QREccLevel _chunkLevel;
    private string[] _chunks = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_content, var range, var boost) = Shape switch
        {
            "byte-45k-any" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any, false),
            "byte-45k-v40" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Exactly(40), false),
            "byte-4k-max10" => (Repeat("The quick brown fox jumps over the lazy dog. ", 4_000), QRVersionRange.AtMost(10), false),
            "numeric-100k-any" => (Repeat("0123456789", 100_000), QRVersionRange.Any, false),
            "alnum-65k-any" => (Repeat("HELLO WORLD 12345 $%*+-./: ", 65_000), QRVersionRange.Any, false),
            "mixed-40k-any" => (Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRVersionRange.Any, false),
            "uneven-40k-any" => (Repeat("0123456789", 20_000) + Repeat("order 20260915 item 0000123456 qty 42 ", 20_000), QRVersionRange.Any, false),
            "utf8-15k-any" => (Repeat("こんにちは世界、QRコードの分割テストです。", 15_000), QRVersionRange.Any, false),
            "utf8-15k-mixed" => (Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000), QRVersionRange.Any, false),
            "marked-40k-any" => (Repeat("order 20260915 item 0000123456 qty 42" + (char)0x0A + (char)0xFEFF, 40_000), QRVersionRange.Any, false),
            "byte-45k-boost" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any, true),
            "kanji-15k-any" => (Repeat("こんにちは世界、QRコードの分割テストです。", 15_000), QRVersionRange.Any, false),
            "kanji-15k-mixed" => (Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000), QRVersionRange.Any, false),
            "cells-15k-any" or "cells-15k-utf8" => (Repeat("こんにちは世界、日本語の分割テストです。", 15_000), QRVersionRange.Any, false),
            "kanji-1k-max10" => (Repeat("こんにちは世界、QRコードの分割テストです。", 1_000), QRVersionRange.AtMost(10), false),
            "cells-1k-max10" => (Repeat("こんにちは世界、日本語の分割テストです。", 1_000), QRVersionRange.AtMost(10), false),
            "interleaved-1k-max10" or "interleaved-1k-utf8" => (Repeat("a日b本c", 1_000), QRVersionRange.AtMost(10), false),
            "numeric-100k-boost" => (Repeat("0123456789", 100_000), QRVersionRange.Any, true),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };
        var charset = Shape is "utf8-15k-any" or "utf8-15k-mixed" or "cells-15k-utf8" or "interleaved-1k-utf8" ? EciMode.Utf8 : EciMode.Default;
        var allowKanji = Shape is "kanji-15k-any" or "kanji-15k-mixed" or "cells-15k-any" or "kanji-1k-max10" or "cells-1k-max10" or "interleaved-1k-max10";

        _single = new QRCodeGeneratorOptions { Version = range, BoostEccLevel = boost, EciMode = charset, Segmentation = QRSegmentation.Single, AllowKanji = allowKanji };
        _optimal = new QRCodeGeneratorOptions { Version = range, BoostEccLevel = boost, EciMode = charset, Segmentation = QRSegmentation.Optimal, AllowKanji = allowKanji };

        // The chunks of the Single set, read back through the decoder, with the version and
        // level every symbol of the set shares. The baseline encodes exactly those symbols,
        // so a boosted set is compared against symbols at the level the boost reached.
        var set = QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _single);
        _chunks = new string[set.Length];
        for (var i = 0; i < set.Length; i++)
        {
            if (!QRCodeDecoder.TryDecode(set[i], out _chunks[i], out var info))
                throw new InvalidOperationException($"symbol {i} of shape {Shape} did not decode: {info.Status}");
            _chunkLevel = info.EccLevel;
        }
        _chunkOptions = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(set[0].Version), EciMode = charset, Segmentation = QRSegmentation.Single, AllowKanji = allowKanji };
    }

    [Benchmark(Baseline = true, Description = "Symbols")]
    public int SymbolsOnly()
    {
        var modules = 0;
        foreach (var chunk in _chunks)
            modules += QRCodeGenerator.Create(chunk.AsSpan(), _chunkLevel, _chunkOptions).Size;
        return modules;
    }

    [Benchmark(Description = "Single")]
    public QRCodeData[] SingleSet()
    {
        return QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _single);
    }

    [Benchmark(Description = "Optimal")]
    public QRCodeData[] OptimalSet()
    {
        return QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _optimal);
    }

    private static string Repeat(string text, int length)
    {
        var sb = new StringBuilder(length + text.Length);
        while (sb.Length < length)
            sb.Append(text);
        return sb.ToString(0, length);
    }
}
