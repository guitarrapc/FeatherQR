/// <summary>
/// End-to-end rMQR matrix encoding through the public API (RmQRCodeGenerator).
/// Baseline for the future placement / bit-stream fast paths, and the guard that
/// the reference-shaped pipeline stays allocation-free on the span path.
///
/// Scenarios (requested versions pin the size so the fit search is not what is measured):
///   Numeric_R7x43_M      : smallest symbol, 12 digits (capacity boundary)
///   Alphanumeric_R11x59_M: mid symbol, 43 chars (capacity boundary)
///   Byte_R17x139_M       : largest symbol, 150 bytes (capacity boundary, 4 RS blocks)
///   Latin1_Eci_R17x139_M : explicit ECI 3 Byte segment
///   Utf8_Eci_R17x139_M   : explicit ECI 26 Byte segment
///   Numeric_AutoFit_M    : automatic version selection cost on top of the smallest symbol
///
/// Mixed-mode segmentation has its own class (<see cref="RmQRSegmentationEncode"/>):
/// it varies content shape rather than version, and every row needs a same-run Single
/// pair, which does not belong in this table.
/// </summary>
public class RmQREncodeEndToEnd
{
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private string _latin1 = default!;
    private string _utf8 = default!;
    private byte[] _spanDestination = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "012345678901";                                     // R7x43-M numeric boundary
        _alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.";   // 43 chars: R11x59-M alphanumeric boundary
        _byte = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150); // R17x139-M byte boundary
        _latin1 = string.Concat(Enumerable.Repeat("Café déjà vu. ", 8));
        _utf8 = string.Concat(Enumerable.Repeat("日本語QRコード", 5));
        _spanDestination = new byte[Math.Max(
            Sizing.Required(_byte.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }).BufferSize,
            Sizing.Required(_numeric.AsSpan(), QREccLevel.L).BufferSize)];
    }

    // Class API (allocates the result object only)

    [Benchmark(Baseline = true)]
    public RmQRCodeData RmQR_Numeric_R7x43_Encode()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Alphanumeric_R11x59_Encode()
    {
        return RmQRCodeGenerator.Create(_alphanumeric.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Byte_R17x139_Encode()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Latin1Eci_R17x139_Encode()
    {
        return RmQRCodeGenerator.Create(_latin1.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Version = RmQRVersion.R17x139 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Utf8Eci_R17x139_Encode()
    {
        return RmQRCodeGenerator.Create(_utf8.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Version = RmQRVersion.R17x139 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Numeric_AutoFit_Encode()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M);
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Description = "RmQR_Numeric_R7x43_Encode (Span)")]
    public int RmQR_Numeric_R7x43_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });
    }

    [Benchmark(Description = "RmQR_Alphanumeric_R11x59_Encode (Span)")]
    public int RmQR_Alphanumeric_R11x59_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_alphanumeric.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59 });
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Encode (Span)")]
    public int RmQR_Byte_R17x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
    }

    [Benchmark(Description = "RmQR_Latin1_ECI_R17x139_Encode (Span)")]
    public int RmQR_Latin1Eci_R17x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_latin1.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Version = RmQRVersion.R17x139 });
    }

    [Benchmark(Description = "RmQR_UTF8_ECI_R17x139_Encode (Span)")]
    public int RmQR_Utf8Eci_R17x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_utf8.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Version = RmQRVersion.R17x139 });
    }

    [Benchmark(Description = "RmQR_Numeric_AutoFit_Encode (Span)")]
    public int RmQR_Numeric_AutoFit_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, _spanDestination);
    }

    // Standard QR version 1 with the same numeric payload, for scale reference. Also
    // the control row when comparing two runs: it is untouched by any rMQR change, so
    // how far it moves between runs is the machine drift to divide out.

    [Benchmark(Description = "StandardQr_Numeric_V1_Encode (Span)")]
    public int StandardQr_Numeric_V1_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L, _spanDestination);
    }
}
