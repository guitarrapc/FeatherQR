using System.Text;

/// <summary>
/// End-to-end QR matrix encoding through the public API (QRCodeGenerator).
/// Used to measure the user-visible impact of internal kernel changes such as the Reed-Solomon ECC encoder optimization.
///
/// Scenarios:
///   Numeric_V1_L : version 1, numeric mode (digits only)
///   Numeric_V40_L : version 40-L, numeric mode (7,089 digits, capacity boundary)
///   Alphanumeric_V1_M : version 1, alphanumeric mode (uppercase / punctuation subset)
///   Alphanumeric_V10_M : version 10-M, alphanumeric mode (300 characters)
///   Alphanumeric_V40_L : version 40-L, alphanumeric mode (4,296 characters, capacity boundary)
///   Byte_Url_V6_M : version 6, byte mode (typical URL with lowercase)
///   Byte_V20_M : version 19-M, byte mode (mid-size, exercises the transposed mask tier on two-word rows). Named V20 before 620 bytes was found to fit version 19
///   Byte_V40_L : version 40-L, byte mode (largest data blocks)
///   Byte_V40_H : version 39-H, byte mode (77 blocks x 30 ecc). Named V40 before 1,200 bytes was found to fit version 39
///   Kanji_V6_M : version 6-M, Kanji mode (capacity boundary, 65 characters)
///   Kanji_Long_V15_L : version 15-L, Kanji mode (capacity boundary, 320 characters)
///
/// Byte_Url_V6_M and Kanji_V6_M share version and level, so they differ in mode, not in symbol size.
/// The "(Span)" rows keep the default quiet zone of 4, the matrix a caller gets with default options. Their quiet-zone-free twins are in <see cref="QRCodeQuietZone0Encode"/>.
/// The three long alphanumeric and numeric shapes are long enough to time the Alphanumeric and Numeric writers.
/// The "(Pinned)" and "(Boost)" rows resolve the version or level before the pipeline, the path tools/CrossLanguageBenchmark takes on every encode.
/// </summary>
public class QRCodeEncodeEndToEnd
{
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byteUrl = default!;
    private string _byteMidM = default!;
    private string _byteLongL = default!;
    private string _byteLongH = default!;
    private string _kanji = default!;
    private string _kanjiLong = default!;
    private string _alphanumericMidM = default!;
    private string _alphanumericLongL = default!;
    private string _numericLongL = default!;
    private byte[] _spanDestination = default!;

    internal const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private static readonly QRCodeGeneratorOptions Boost = new() { BoostEccLevel = true };
    private static readonly QRCodeGeneratorOptions KanjiVersion6 = new() { AllowKanji = true, Version = QRVersionRange.Exactly(6) };
    private static readonly QRCodeGeneratorOptions KanjiVersion15 = new() { AllowKanji = true, Version = QRVersionRange.Exactly(15) };
    private static readonly QRCodeGeneratorOptions Version40 = new() { Version = QRVersionRange.Exactly(40) };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "0123456789"; // version 1-L, numeric mode
        _alphanumeric = "HELLO WORLD 2026"; // version 1-M, alphanumeric mode
        _byteUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42"; // version 6-M, byte mode
        _byteMidM = BuildDeterministicText(620); // version 19-M byte mode (the row name says V20)
        _byteLongL = BuildDeterministicText(2900); // version 40-L byte mode (max 2953)
        _byteLongH = BuildDeterministicText(1200); // version 39-H byte mode (the row name says V40)
        _alphanumericMidM = BuildText(300, AlphanumericAlphabet); // version 10-M alphanumeric mode
        _alphanumericLongL = BuildText(4296, AlphanumericAlphabet); // version 40-L alphanumeric mode (max 4296)
        _numericLongL = BuildText(7089, "0123456789"); // version 40-L numeric mode (max 7089)
        _kanjiLong = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20)); // version 15-L Kanji mode (max 320)
        _kanji = _kanjiLong.Substring(0, 65); // version 6-M Kanji mode (max 65)
        _spanDestination = new byte[Sizing.Required(_byteLongL.AsSpan(), QREccLevel.L).BufferSize];
    }

    // Class API (allocates the result object only)

    [Benchmark(Baseline = true)]
    public QRCodeData QR_Numeric_V1_L_Encode()
    {
        return QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    public QRCodeData QR_Numeric_V40_L_Encode()
    {
        return QRCodeGenerator.Create(_numericLongL.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    public QRCodeData QR_Alphanumeric_V1_M_Encode()
    {
        return QRCodeGenerator.Create(_alphanumeric.AsSpan(), QREccLevel.M);
    }

    [Benchmark]
    public QRCodeData QR_Alphanumeric_V10_M_Encode()
    {
        return QRCodeGenerator.Create(_alphanumericMidM.AsSpan(), QREccLevel.M);
    }

    [Benchmark]
    public QRCodeData QR_Alphanumeric_V40_L_Encode()
    {
        return QRCodeGenerator.Create(_alphanumericLongL.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    public QRCodeData QR_Byte_Url_V6_M_Encode()
    {
        return QRCodeGenerator.Create(_byteUrl.AsSpan(), QREccLevel.M);
    }

    [Benchmark]
    public QRCodeData QR_Byte_V20_M_Encode()
    {
        return QRCodeGenerator.Create(_byteMidM.AsSpan(), QREccLevel.M);
    }

    [Benchmark]
    public QRCodeData QR_Byte_V40_L_Encode()
    {
        return QRCodeGenerator.Create(_byteLongL.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    public QRCodeData QR_Byte_V40_H_Encode()
    {
        return QRCodeGenerator.Create(_byteLongH.AsSpan(), QREccLevel.H);
    }

    [Benchmark]
    public QRCodeData QR_Kanji_V6_M_Encode()
    {
        return QRCodeGenerator.Create(_kanji.AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { AllowKanji = true });
    }

    [Benchmark]
    public QRCodeData QR_Kanji_Long_V15_L_Encode()
    {
        return QRCodeGenerator.Create(_kanjiLong.AsSpan(), QREccLevel.L, new QRCodeGeneratorOptions { AllowKanji = true });
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Description = "QR_Numeric_V1_L_Encode (Span)")]
    public int QR_Numeric_V1_L_EncodeSpan()
    {
        return QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "QR_Numeric_V40_L_Encode (Span)")]
    public int QR_Numeric_V40_L_EncodeSpan()
    {
        return QRCodeGenerator.Create(_numericLongL.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "QR_Alphanumeric_V1_M_Encode (Span)")]
    public int QR_Alphanumeric_V1_M_EncodeSpan()
    {
        return QRCodeGenerator.Create(_alphanumeric.AsSpan(), QREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "QR_Alphanumeric_V10_M_Encode (Span)")]
    public int QR_Alphanumeric_V10_M_EncodeSpan()
    {
        return QRCodeGenerator.Create(_alphanumericMidM.AsSpan(), QREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "QR_Alphanumeric_V40_L_Encode (Span)")]
    public int QR_Alphanumeric_V40_L_EncodeSpan()
    {
        return QRCodeGenerator.Create(_alphanumericLongL.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "QR_Byte_Url_V6_M_Encode (Span)")]
    public int QR_Byte_Url_V6_M_EncodeSpan()
    {
        return QRCodeGenerator.Create(_byteUrl.AsSpan(), QREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "QR_Byte_V20_M_Encode (Span)")]
    public int QR_Byte_V20_M_EncodeSpan()
    {
        return QRCodeGenerator.Create(_byteMidM.AsSpan(), QREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "QR_Byte_V40_L_Encode (Span)")]
    public int QR_Byte_V40_L_EncodeSpan()
    {
        return QRCodeGenerator.Create(_byteLongL.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "QR_Byte_V40_H_Encode (Span)")]
    public int QR_Byte_V40_H_EncodeSpan()
    {
        return QRCodeGenerator.Create(_byteLongH.AsSpan(), QREccLevel.H, _spanDestination);
    }

    [Benchmark(Description = "QR_Kanji_V6_M_Encode (Span)")]
    public int QR_Kanji_V6_M_EncodeSpan()
    {
        return QRCodeGenerator.Create(_kanji.AsSpan(), QREccLevel.M, _spanDestination, new QRCodeGeneratorOptions { AllowKanji = true });
    }

    [Benchmark(Description = "QR_Kanji_Long_V15_L_Encode (Span)")]
    public int QR_Kanji_Long_V15_L_EncodeSpan()
    {
        return QRCodeGenerator.Create(_kanjiLong.AsSpan(), QREccLevel.L, _spanDestination, new QRCodeGeneratorOptions { AllowKanji = true });
    }

    // Version or level resolved before the pipeline: a pinned version, or the ECC boost. Each
    // writes the symbol of the row above it with the same name (the boost raises the numeric
    // shape to H, which the URL shape has no room for), from the analysis that resolved it.

    [Benchmark(Description = "QR_Numeric_V1_L_Encode (Boost)")]
    public QRCodeData QR_Numeric_V1_L_EncodeBoost()
    {
        return QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L, Boost);
    }

    [Benchmark(Description = "QR_Byte_Url_V6_M_Encode (Boost)")]
    public QRCodeData QR_Byte_Url_V6_M_EncodeBoost()
    {
        return QRCodeGenerator.Create(_byteUrl.AsSpan(), QREccLevel.M, Boost);
    }

    [Benchmark(Description = "QR_Byte_V40_L_Encode (Pinned)")]
    public QRCodeData QR_Byte_V40_L_EncodePinned()
    {
        return QRCodeGenerator.Create(_byteLongL.AsSpan(), QREccLevel.L, Version40);
    }

    [Benchmark(Description = "QR_Kanji_V6_M_Encode (Pinned)")]
    public QRCodeData QR_Kanji_V6_M_EncodePinned()
    {
        return QRCodeGenerator.Create(_kanji.AsSpan(), QREccLevel.M, KanjiVersion6);
    }

    [Benchmark(Description = "QR_Kanji_Long_V15_L_Encode (Pinned)")]
    public QRCodeData QR_Kanji_Long_V15_L_EncodePinned()
    {
        return QRCodeGenerator.Create(_kanjiLong.AsSpan(), QREccLevel.L, KanjiVersion15);
    }

    // Three "(options)" benchmarks stood here until 2.0.0, pairing each options overload
    // against the parameter list overload it forwarded to so that "the forwarder costs
    // nothing" stayed a measured fact. The parameter list overloads were removed, and
    // `QRCodeGeneratorOptions.Default` is `default`, so each pair collapsed into two
    // spellings of one call and the pairs were dropped rather than left measuring
    // themselves. The options path is now the only path, and the benchmarks above are on it.

    internal static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Characters drawn from <paramref name="alphabet"/> with a fixed seed: one encoding mode for the whole text.</summary>
    internal static string BuildText(int length, string alphabet)
    {
        var rng = new Random(7);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        }
        return new string(chars);
    }
}
