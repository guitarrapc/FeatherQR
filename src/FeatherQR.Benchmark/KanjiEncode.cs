/// <summary>
/// Text whose every character has a Kanji cell, which the generators write in Kanji mode when the charset is left to them, next to the same text with UTF-8 asked for, which is the path such text took before Kanji mode was written.
/// Then text with ASCII in it under <c>Optimal</c>, which takes a Kanji plan (Kanji runs beside runs of the ASCII) where that is smaller, next to the same text with UTF-8 asked for, which plans it as before.
/// Micro QR has no charset option, so its twins are the same arms run against a build that predates Kanji mode (or Kanji plans).
/// </summary>
[MemoryDiagnoser]
public class KanjiEncode
{
    private const string Short = "日本語のテキスト";                                // 8 characters: version 1-M in Kanji mode, 2-M in UTF-8
    private const string Micro = "こんにちは";                                     // M3-L in Kanji mode, M4-L's 15 bytes in UTF-8
    private const string RmQr = "日本語のテキストです、ようこそ";                  // 15 characters: R13x43-M in Kanji mode, R15x59-M in UTF-8

    /// <summary>320 characters: version 15-L in Kanji mode, 22-L in UTF-8.</summary>
    private static readonly string Long = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20));

    /// <summary>The segmentation classes' <c>utf8-60</c>: 5-M as a Kanji plan, 6-M as UTF-8 (no UTF-8 plan beats one Byte run); R17x77-M against R13x139-M.</summary>
    private static readonly string Mixed = string.Concat(Enumerable.Repeat("日本7777", 10));

    /// <summary>66 characters, two of them ASCII: 7-M as a Kanji plan, 10-M as UTF-8.</summary>
    private static readonly string Sentence = string.Concat(Enumerable.Repeat("こんにちは世界、QRコードの分割テストです。", 3));

    private const string MicroMixed = "日本語12345";                                 // M3-L as a Kanji plan; 14 UTF-8 bytes, M4-L

    private static readonly QRCodeGeneratorOptions Utf8 = new() { EciMode = EciMode.Utf8 };
    private static readonly RmQRCodeGeneratorOptions RmQrUtf8 = new() { EciMode = EciMode.Utf8 };
    private static readonly QRCodeGeneratorOptions Optimal = new() { Segmentation = QRSegmentation.Optimal };
    private static readonly QRCodeGeneratorOptions OptimalUtf8 = new() { Segmentation = QRSegmentation.Optimal, EciMode = EciMode.Utf8 };
    private static readonly MicroQRCodeGeneratorOptions MicroOptimal = new() { Segmentation = MicroQRSegmentation.Optimal };
    private static readonly RmQRCodeGeneratorOptions RmQrOptimal = new() { Segmentation = RmQRSegmentation.Optimal };
    private static readonly RmQRCodeGeneratorOptions RmQrOptimalUtf8 = new() { Segmentation = RmQRSegmentation.Optimal, EciMode = EciMode.Utf8 };

    private byte[] _destination = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _destination = new byte[Math.Max(
            Math.Max(Sizing.Required(Long.AsSpan(), QREccLevel.L, Utf8).BufferSize, Sizing.Required(Sentence.AsSpan(), QREccLevel.M, Utf8).BufferSize),
            Math.Max(Sizing.Required(RmQr.AsSpan(), RmQREccLevel.M, RmQrUtf8).BufferSize, Sizing.Required(Mixed.AsSpan(), RmQREccLevel.M, RmQrUtf8).BufferSize))];
    }

    [Benchmark]
    public int QR_Kanji_Short_Encode() => QRCodeGenerator.Create(Short.AsSpan(), QREccLevel.M, _destination);

    [Benchmark]
    public int QR_Utf8_Short_Encode() => QRCodeGenerator.Create(Short.AsSpan(), QREccLevel.M, _destination, Utf8);

    [Benchmark]
    public int QR_Kanji_Long_Encode() => QRCodeGenerator.Create(Long.AsSpan(), QREccLevel.L, _destination);

    [Benchmark]
    public int QR_Utf8_Long_Encode() => QRCodeGenerator.Create(Long.AsSpan(), QREccLevel.L, _destination, Utf8);

    [Benchmark]
    public int MicroQR_Kanji_Encode() => MicroQRCodeGenerator.Create(Micro.AsSpan(), MicroQREccLevel.L, _destination);

    [Benchmark]
    public int RmQR_Kanji_Encode() => RmQRCodeGenerator.Create(RmQr.AsSpan(), RmQREccLevel.M, _destination);

    [Benchmark]
    public int RmQR_Utf8_Encode() => RmQRCodeGenerator.Create(RmQr.AsSpan(), RmQREccLevel.M, _destination, RmQrUtf8);

    [Benchmark]
    public int QR_KanjiPlan_Mixed_Encode() => QRCodeGenerator.Create(Mixed.AsSpan(), QREccLevel.M, _destination, Optimal);

    [Benchmark]
    public int QR_Utf8Plan_Mixed_Encode() => QRCodeGenerator.Create(Mixed.AsSpan(), QREccLevel.M, _destination, OptimalUtf8);

    [Benchmark]
    public int QR_KanjiPlan_Sentence_Encode() => QRCodeGenerator.Create(Sentence.AsSpan(), QREccLevel.M, _destination, Optimal);

    [Benchmark]
    public int QR_Utf8Plan_Sentence_Encode() => QRCodeGenerator.Create(Sentence.AsSpan(), QREccLevel.M, _destination, OptimalUtf8);

    [Benchmark]
    public int MicroQR_KanjiPlan_Encode() => MicroQRCodeGenerator.Create(MicroMixed.AsSpan(), MicroQREccLevel.L, _destination, MicroOptimal);

    [Benchmark]
    public int RmQR_KanjiPlan_Mixed_Encode() => RmQRCodeGenerator.Create(Mixed.AsSpan(), RmQREccLevel.M, _destination, RmQrOptimal);

    [Benchmark]
    public int RmQR_Utf8Plan_Mixed_Encode() => RmQRCodeGenerator.Create(Mixed.AsSpan(), RmQREccLevel.M, _destination, RmQrOptimalUtf8);
}
