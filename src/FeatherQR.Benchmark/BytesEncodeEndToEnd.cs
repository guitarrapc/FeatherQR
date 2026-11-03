using System.Text;

/// <summary>
/// Measures the bytes calls of the three generators beside the text calls on the same content, so each pair reads as the cost of giving bytes instead of text.
/// </summary>
/// <remarks>
/// The content is ASCII, so both calls write the same symbol, which setup checks. The symbols are those of <see cref="BytesDecodeEndToEnd"/>, with digits at M2-L added for Micro QR,
/// plus 2,900 bytes of every value under ECI 899, which only the bytes calls write.
/// Span destinations must stay allocation-free.
/// </remarks>
public class BytesEncodeEndToEnd
{
    private string _qrUrl = default!;
    private byte[] _qrUrlBytes = default!;
    private string _qrLong = default!;
    private byte[] _qrLongBytes = default!;
    private byte[] _qrBinary = default!;
    private string _microNumeric = default!;
    private byte[] _microNumericBytes = default!;
    private string _microByte = default!;
    private byte[] _microByteBytes = default!;
    private string _rmqr = default!;
    private byte[] _rmqrBytes = default!;

    private byte[] _qrDestination = default!;
    private byte[] _microDestination = default!;
    private byte[] _rmqrDestination = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _qrUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42"; // V6-M
        _qrLong = BuildDeterministicText(2900);                                                              // V40-L
        _qrBinary = new byte[2900];                                                                          // V40-L under ECI 899
        for (var i = 0; i < _qrBinary.Length; i++)
            _qrBinary[i] = (byte)(i * 151 + 7);
        _microNumeric = "0123456789";                                                                        // M2-L
        _microByte = "bytes m4 mode";                                                                        // M4-M
        _rmqr = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150); // R17x139-M

        _qrUrlBytes = Encoding.ASCII.GetBytes(_qrUrl);
        _qrLongBytes = Encoding.ASCII.GetBytes(_qrLong);
        _microNumericBytes = Encoding.ASCII.GetBytes(_microNumeric);
        _microByteBytes = Encoding.ASCII.GetBytes(_microByte);
        _rmqrBytes = Encoding.ASCII.GetBytes(_rmqr);

        _qrDestination = new byte[Sizing.Required(_qrLong.AsSpan(), QREccLevel.L).BufferSize];
        _microDestination = new byte[Sizing.Required(_microByte.AsSpan(), MicroQREccLevel.M).BufferSize];
        _rmqrDestination = new byte[Sizing.Required(_rmqr.AsSpan(), RmQREccLevel.M).BufferSize];

        var text = new byte[_qrDestination.Length];
        var bytes = new byte[_qrDestination.Length];
        RequireSame(text, QRCodeGenerator.Create(_qrUrl.AsSpan(), QREccLevel.M, text), bytes, QRCodeGenerator.Create(_qrUrlBytes, QREccLevel.M, bytes), "Standard QR URL");
        RequireSame(text, QRCodeGenerator.Create(_qrLong.AsSpan(), QREccLevel.L, text), bytes, QRCodeGenerator.Create(_qrLongBytes, QREccLevel.L, bytes), "Standard QR V40-L");
        QRCodeGenerator.Create(_qrBinary, QREccLevel.L, bytes, eciAssignment: 899);
        RequireSame(text, MicroQRCodeGenerator.Create(_microNumeric.AsSpan(), MicroQREccLevel.L, text), bytes, MicroQRCodeGenerator.Create(_microNumericBytes, MicroQREccLevel.L, bytes), "Micro QR M2-L");
        RequireSame(text, MicroQRCodeGenerator.Create(_microByte.AsSpan(), MicroQREccLevel.M, text), bytes, MicroQRCodeGenerator.Create(_microByteBytes, MicroQREccLevel.M, bytes), "Micro QR M4-M");
        RequireSame(text, RmQRCodeGenerator.Create(_rmqr.AsSpan(), RmQREccLevel.M, text), bytes, RmQRCodeGenerator.Create(_rmqrBytes, RmQREccLevel.M, bytes), "rMQR");
    }

    // Standard QR, the URL at V6-M

    [Benchmark(Description = "QR_Byte_Url_V6_M_Encode (Span)")]
    public int QR_Byte_Url_V6_M_EncodeSpan()
        => QRCodeGenerator.Create(_qrUrl.AsSpan(), QREccLevel.M, _qrDestination);

    [Benchmark(Description = "QR_Byte_Url_V6_M_EncodeBytes (Span)")]
    public int QR_Byte_Url_V6_M_EncodeBytesSpan()
        => QRCodeGenerator.Create(_qrUrlBytes, QREccLevel.M, _qrDestination);

    // Standard QR, 2,900 characters at V40-L

    [Benchmark]
    public QRCodeData QR_Byte_V40_L_Encode()
        => QRCodeGenerator.Create(_qrLong.AsSpan(), QREccLevel.L);

    [Benchmark]
    public QRCodeData QR_Byte_V40_L_EncodeBytes()
        => QRCodeGenerator.Create(_qrLongBytes, QREccLevel.L);

    [Benchmark(Description = "QR_Byte_V40_L_Encode (Span)")]
    public int QR_Byte_V40_L_EncodeSpan()
        => QRCodeGenerator.Create(_qrLong.AsSpan(), QREccLevel.L, _qrDestination);

    [Benchmark(Description = "QR_Byte_V40_L_EncodeBytes (Span)")]
    public int QR_Byte_V40_L_EncodeBytesSpan()
        => QRCodeGenerator.Create(_qrLongBytes, QREccLevel.L, _qrDestination);

    // Standard QR, 2,900 bytes of every value under ECI 899 at V40-L, which no text writes

    [Benchmark(Description = "QR_Binary_Eci899_V40_L_EncodeBytes (Span)")]
    public int QR_Binary_Eci899_V40_L_EncodeBytesSpan()
        => QRCodeGenerator.Create(_qrBinary, QREccLevel.L, _qrDestination, eciAssignment: 899);

    // Micro QR, digits at M2-L: the bytes take Numeric mode as the text does

    [Benchmark(Description = "MicroQR_Numeric_M2_Encode (Span)")]
    public int MicroQR_Numeric_M2_EncodeSpan()
        => MicroQRCodeGenerator.Create(_microNumeric.AsSpan(), MicroQREccLevel.L, _microDestination);

    [Benchmark(Description = "MicroQR_Numeric_M2_EncodeBytes (Span)")]
    public int MicroQR_Numeric_M2_EncodeBytesSpan()
        => MicroQRCodeGenerator.Create(_microNumericBytes, MicroQREccLevel.L, _microDestination);

    // Micro QR, M4-M

    [Benchmark]
    public MicroQRCodeData MicroQR_Byte_M4_Encode()
        => MicroQRCodeGenerator.Create(_microByte.AsSpan(), MicroQREccLevel.M);

    [Benchmark]
    public MicroQRCodeData MicroQR_Byte_M4_EncodeBytes()
        => MicroQRCodeGenerator.Create(_microByteBytes, MicroQREccLevel.M);

    [Benchmark(Description = "MicroQR_Byte_M4_Encode (Span)")]
    public int MicroQR_Byte_M4_EncodeSpan()
        => MicroQRCodeGenerator.Create(_microByte.AsSpan(), MicroQREccLevel.M, _microDestination);

    [Benchmark(Description = "MicroQR_Byte_M4_EncodeBytes (Span)")]
    public int MicroQR_Byte_M4_EncodeBytesSpan()
        => MicroQRCodeGenerator.Create(_microByteBytes, MicroQREccLevel.M, _microDestination);

    [Benchmark(Description = "MicroQR_Byte_M4_RequiredSize")]
    public int MicroQR_Byte_M4_RequiredSize()
    {
        MicroQRCodeGenerator.TryGetRequiredBufferSize(_microByte.AsSpan(), MicroQREccLevel.M, out var size);
        return size.BufferSize;
    }

    [Benchmark(Description = "MicroQR_Byte_M4_RequiredSizeBytes")]
    public int MicroQR_Byte_M4_RequiredSizeBytes()
    {
        MicroQRCodeGenerator.TryGetRequiredBufferSize(_microByteBytes, MicroQREccLevel.M, out var size);
        return size.BufferSize;
    }

    // rMQR, R17x139-M

    [Benchmark(Description = "RmQR_Byte_R17x139_Encode (Span)")]
    public int RmQR_Byte_R17x139_EncodeSpan()
        => RmQRCodeGenerator.Create(_rmqr.AsSpan(), RmQREccLevel.M, _rmqrDestination);

    [Benchmark(Description = "RmQR_Byte_R17x139_EncodeBytes (Span)")]
    public int RmQR_Byte_R17x139_EncodeBytesSpan()
        => RmQRCodeGenerator.Create(_rmqrBytes, RmQREccLevel.M, _rmqrDestination);

    private static void RequireSame(byte[] text, int textLength, byte[] bytes, int bytesLength, string symbol)
    {
        if (!text.AsSpan(0, textLength).SequenceEqual(bytes.AsSpan(0, bytesLength)))
            throw new InvalidOperationException($"The text and bytes calls wrote different {symbol} symbols, so the pair would not compare one symbol.");
    }

    private static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }
}
