/// <summary>
/// Quiet-zone-free span encodes of the <see cref="QRCodeEncodeEndToEnd"/> shapes, for performance work rather than the default path a caller takes.
/// The matrix is the one <see cref="QRCodeDecodeEndToEnd"/> decodes, and the quiet zone's cost is the gap to the "(Span)" row of the same name there.
/// </summary>
public class QRCodeQuietZone0Encode
{
    private string _numeric = default!;
    private string _numericLongL = default!;
    private string _alphanumeric = default!;
    private string _alphanumericMidM = default!;
    private string _alphanumericLongL = default!;
    private string _byteUrl = default!;
    private string _byteMidM = default!;
    private string _byteLongL = default!;
    private string _byteLongH = default!;
    private string _kanji = default!;
    private string _kanjiLong = default!;
    private byte[] _spanDestination = default!;

    private static readonly QRCodeGeneratorOptions NoQuietZone = new() { QuietZoneSize = 0 };
    private static readonly QRCodeGeneratorOptions KanjiNoQuietZone = new() { AllowKanji = true, QuietZoneSize = 0 };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "0123456789"; // version 1-L
        _numericLongL = QRCodeEncodeEndToEnd.BuildText(7089, "0123456789"); // version 40-L
        _alphanumeric = "HELLO WORLD 2026"; // version 1-M
        _alphanumericMidM = QRCodeEncodeEndToEnd.BuildText(300, QRCodeEncodeEndToEnd.AlphanumericAlphabet); // version 10-M
        _alphanumericLongL = QRCodeEncodeEndToEnd.BuildText(4296, QRCodeEncodeEndToEnd.AlphanumericAlphabet); // version 40-L
        _byteUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42"; // version 6-M
        _byteMidM = QRCodeEncodeEndToEnd.BuildDeterministicText(620); // version 19-M (the row name says V20)
        _byteLongL = QRCodeEncodeEndToEnd.BuildDeterministicText(2900); // version 40-L
        _byteLongH = QRCodeEncodeEndToEnd.BuildDeterministicText(1200); // version 39-H (the row name says V40)
        _kanjiLong = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20)); // version 15-L
        _kanji = _kanjiLong.Substring(0, 65); // version 6-M
        _spanDestination = new byte[Sizing.Required(_byteLongL.AsSpan(), QREccLevel.L, NoQuietZone).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "QR_Numeric_V1_L_Encode (Span, QZ0)")]
    public int QR_Numeric_V1_L_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Numeric_V40_L_Encode (Span, QZ0)")]
    public int QR_Numeric_V40_L_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_numericLongL.AsSpan(), QREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Alphanumeric_V1_M_Encode (Span, QZ0)")]
    public int QR_Alphanumeric_V1_M_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_alphanumeric.AsSpan(), QREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Alphanumeric_V10_M_Encode (Span, QZ0)")]
    public int QR_Alphanumeric_V10_M_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_alphanumericMidM.AsSpan(), QREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Alphanumeric_V40_L_Encode (Span, QZ0)")]
    public int QR_Alphanumeric_V40_L_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_alphanumericLongL.AsSpan(), QREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Byte_Url_V6_M_Encode (Span, QZ0)")]
    public int QR_Byte_Url_V6_M_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_byteUrl.AsSpan(), QREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Byte_V20_M_Encode (Span, QZ0)")]
    public int QR_Byte_V20_M_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_byteMidM.AsSpan(), QREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Byte_V40_L_Encode (Span, QZ0)")]
    public int QR_Byte_V40_L_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_byteLongL.AsSpan(), QREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Byte_V40_H_Encode (Span, QZ0)")]
    public int QR_Byte_V40_H_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_byteLongH.AsSpan(), QREccLevel.H, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "QR_Kanji_V6_M_Encode (Span, QZ0)")]
    public int QR_Kanji_V6_M_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_kanji.AsSpan(), QREccLevel.M, _spanDestination, KanjiNoQuietZone);
    }

    [Benchmark(Description = "QR_Kanji_Long_V15_L_Encode (Span, QZ0)")]
    public int QR_Kanji_Long_V15_L_EncodeSpanNoQuietZone()
    {
        return QRCodeGenerator.Create(_kanjiLong.AsSpan(), QREccLevel.L, _spanDestination, KanjiNoQuietZone);
    }
}
