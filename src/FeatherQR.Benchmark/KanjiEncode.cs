/// <summary>
/// Text whose every character has a Kanji cell, which the generators write in Kanji mode when the charset is left to them, next to the same text with UTF-8 asked for, which is the path such text took before Kanji mode was written.
/// Micro QR has no charset option, so its twin is the same arm run against a build that predates Kanji mode.
/// </summary>
[MemoryDiagnoser]
public class KanjiEncode
{
    private const string Short = "日本語のテキスト";                                // 8 characters: version 1-M in Kanji mode, 2-M in UTF-8
    private const string Micro = "こんにちは";                                     // M3-L in Kanji mode, M4-L's 15 bytes in UTF-8
    private const string RmQr = "日本語のテキストです、ようこそ";                  // 15 characters: R13x43-M in Kanji mode, R15x59-M in UTF-8

    /// <summary>320 characters: version 15-L in Kanji mode, 22-L in UTF-8.</summary>
    private static readonly string Long = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20));

    private static readonly QRCodeGeneratorOptions Utf8 = new() { EciMode = EciMode.Utf8 };
    private static readonly RmQRCodeGeneratorOptions RmQrUtf8 = new() { EciMode = EciMode.Utf8 };

    private byte[] _destination = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _destination = new byte[Math.Max(
            Sizing.Required(Long.AsSpan(), QREccLevel.L, Utf8).BufferSize,
            Sizing.Required(RmQr.AsSpan(), RmQREccLevel.M, RmQrUtf8).BufferSize)];
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
}
