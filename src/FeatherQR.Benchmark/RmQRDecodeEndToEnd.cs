/// <summary>
/// End-to-end rMQR matrix decoding through the public API (RmQRCodeDecoder): module matrix (no quiet zone) → text.
/// Baseline for the reference-shaped decoder; span-destination variants must stay allocation-free.
///
/// Scenarios (same payloads as RmQREncodeEndToEnd):
///   Numeric_R7x43_M      : smallest symbol, single RS block
///   Alphanumeric_R11x59_M: mid symbol, single block
///   Byte_R17x139_M       : largest symbol, 4 RS blocks
///   *_Corrected          : Numeric_R7x43 and Byte_R17x139 with damage the decoder
///                          confirms as exactly N corrected errors, so the
///                          Berlekamp-Massey/Chien/Forney correction path runs rather
///                          than syndrome generation alone (the clean cases exit early)
/// </summary>
public class RmQRDecodeEndToEnd
{
    private byte[] _numericModules = default!;
    private byte[] _alphanumericModules = default!;
    private byte[] _byteModules = default!;
    private (int Width, int Height) _numericSize;
    private (int Width, int Height) _alphanumericSize;
    private (int Width, int Height) _byteSize;
    private byte[] _numericDamagedModules = default!;
    private byte[] _byteDamagedModules = default!;
    private byte[] _standardModules = default!;
    private int _standardSize;
    private char[] _chars = default!;
    private char[] _standardChars = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = Build("012345678901", RmQREccLevel.M, RmQRVersion.R7x43);
        (_alphanumericModules, _alphanumericSize) = Build("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", RmQREccLevel.M, RmQRVersion.R11x59);
        (_byteModules, _byteSize) = Build(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150), RmQREccLevel.M, RmQRVersion.R17x139);
        _chars = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];

        // Correctable damage: flip a few modules and keep only a corruption the decoder
        // still recovers, so the measurement covers correction rather than failure.
        _numericDamagedModules = Damage(_numericModules, _numericSize, RmQRVersion.R7x43, flips: 2, seed: 17);
        _byteDamagedModules = Damage(_byteModules, _byteSize, RmQRVersion.R17x139, flips: 6, seed: 23);

        var calculated = Sizing.Required("012345678901", QREccLevel.L, 0);
        _standardModules = new byte[calculated.BufferSize];
        FeatherQR.QRCodeGenerator.Create("012345678901", QREccLevel.L, _standardModules, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        _standardSize = calculated.Size;
        _standardChars = new char[QRCodeDecoder.GetMaxDecodedLength(1)];
    }

    // String-returning variants (allocate the result string only)

    [Benchmark]
    public string RmQR_Numeric_R7x43_Decode()
    {
        RmQRCodeDecoder.TryDecode(_numericModules, _numericSize.Width, _numericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Alphanumeric_R11x59_Decode()
    {
        RmQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize.Width, _alphanumericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Byte_R17x139_Decode()
    {
        RmQRCodeDecoder.TryDecode(_byteModules, _byteSize.Width, _byteSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Numeric_R7x43_CorrectedDecode()
    {
        RmQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize.Width, _numericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Byte_R17x139_CorrectedDecode()
    {
        RmQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize.Width, _byteSize.Height, out var text, out _);
        return text;
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Baseline = true, Description = "RmQR_Numeric_R7x43_Decode (Span)")]
    public int RmQR_Numeric_R7x43_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_numericModules, _numericSize.Width, _numericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Alphanumeric_R11x59_Decode (Span)")]
    public int RmQR_Alphanumeric_R11x59_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize.Width, _alphanumericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Decode (Span)")]
    public int RmQR_Byte_R17x139_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_byteModules, _byteSize.Width, _byteSize.Height, _chars, out var written, out _);
        return written;
    }

    // Correctable damage: same symbols, modules flipped within RS capacity.

    [Benchmark(Description = "RmQR_Numeric_R7x43_Corrected_Decode (Span)")]
    public int RmQR_Numeric_R7x43_CorrectedDecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize.Width, _numericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Corrected_Decode (Span)")]
    public int RmQR_Byte_R17x139_CorrectedDecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize.Width, _byteSize.Height, _chars, out var written, out _);
        return written;
    }

    // Standard QR version 1 with the same numeric payload, for scale reference.

    [Benchmark(Description = "StandardQr_Numeric_V1_Decode (Span)")]
    public int StandardQr_Numeric_V1_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_standardModules, _standardSize, _standardChars, out var written, out _);
        return written;
    }

    /// <summary>
    /// Flips <paramref name="flips"/> distinct modules and keeps the draw only when the decoder reports exactly that many corrected errors, so the scenario measures the correction path at its stated strength rather than the failure path.
    /// </summary>
    /// <remarks>
    /// The ErrorsCorrected check is what makes the count honest.
    /// Drawing from the whole matrix spends flips on function patterns, which carry no codeword, and two flips can land in one codeword byte: either way a nominal 2-flip case injects one actual error and measures a shorter correction than its name promises.
    /// Rejecting those draws is conservative — every excluded sample is easier than the one kept.
    /// </remarks>
    private static byte[] Damage(byte[] modules, (int Width, int Height) size, RmQRVersion version, int flips, int seed)
    {
        RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, out var expected, out _);

        for (var attempt = 0; attempt < 4096; attempt++)
        {
            var random = new Random(seed + attempt);
            var damaged = (byte[])modules.Clone();
            var picked = new HashSet<int>();
            while (picked.Count < flips)
                picked.Add(random.Next(damaged.Length));
            foreach (var index in picked)
                damaged[index] ^= 1;

            // ErrorsCorrected, not just "it decoded": a flip that lands on a function
            // pattern carries no codeword, so the symbol still decodes but the scenario
            // measures a shorter correction than its name promises.
            if (RmQRCodeDecoder.TryDecode(damaged, size.Width, size.Height, out var text, out var info)
                && text == expected
                && info.ErrorsCorrected == flips)
            {
                return damaged;
            }
        }

        throw new InvalidOperationException($"No {flips}-error correctable damage found for {version} ({size.Width}x{size.Height}).");
    }

    private static (byte[] modules, (int Width, int Height) size) Build(string content, RmQREccLevel eccLevel, RmQRVersion version)
    {
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return (buffer, (calculated.Width, calculated.Height));
    }
}
