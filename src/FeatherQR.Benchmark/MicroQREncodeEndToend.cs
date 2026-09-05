/// <summary>
/// End-to-end Micro QR matrix encoding through the public API (MicroQRCodeGenerator).
/// Used to measure the user-visible impact of internal kernel changes such as the
/// Reed-Solomon ECC encoder optimization.
///
/// Scenarios:
///   Numeric_M2_L : M2-L (numeric capacity boundary)
///   Alphanumeric_M3_L : M3-L (alphanumeric capacity boundary)
///   Byte_M4_M : M4-M (byte capacity boundary)
/// </summary>
public class MicroQREncodeEndToend
{
    // Representative payloads: numeric M2-L, alphanumeric M3-L, byte M4-M.
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private byte[] _spanDestination = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "0123456789";        // M2-L (numeric capacity boundary)
        _alphanumeric = "HELLO WORLD 14"; // M3-L (alphanumeric capacity boundary)
        _byte = "bytes m4 mode";        // M4-M (byte capacity boundary)
        // Sized for the largest consumer: the Standard QR v1 reference benchmark
        // (29x29 with quiet zone) exceeds every Micro QR buffer size.
        _spanDestination = new byte[Math.Max(
            Sizing.Required(_byte.AsSpan(), MicroQREccLevel.M).BufferSize,
            Sizing.Required(_numeric.AsSpan(), QREccLevel.L).BufferSize)];
    }

    // Class API (allocates the result object only)

    [Benchmark(Baseline = true)]
    public MicroQRCodeData MicroQR_Numeric_M2_Encode()
    {
        return MicroQRCodeGenerator.Create(_numeric.AsSpan(), MicroQREccLevel.L);
    }

    [Benchmark]
    public MicroQRCodeData MicroQR_Alphanumeric_M3_Encode()
    {
        return MicroQRCodeGenerator.Create(_alphanumeric.AsSpan(), MicroQREccLevel.L);
    }

    [Benchmark]
    public MicroQRCodeData MicroQR_Byte_M4_Encode()
    {
        return MicroQRCodeGenerator.Create(_byte.AsSpan(), MicroQREccLevel.M);
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Description = "MicroQR_Numeric_M2_Encode (Span)")]
    public int MicroQR_Numeric_M2_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_numeric.AsSpan(), MicroQREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "MicroQR_Alphanumeric_M3_Encode (Span)")]
    public int MicroQR_Alphanumeric_M3_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_alphanumeric.AsSpan(), MicroQREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "MicroQR_Byte_M4_Encode (Span)")]
    public int MicroQR_Byte_M4_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_byte.AsSpan(), MicroQREccLevel.M, _spanDestination);
    }

    // Standard QR version 1 with the same numeric payload, for scale reference.

    [Benchmark(Description = "StandardQr_Numeric_V1_Encode (Span)")]
    public int StandardQr_Numeric_V1_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_numeric.AsSpan(), QREccLevel.L, _spanDestination);
    }
}
