namespace QRInteropFixtures;

/// <summary>
/// Input definition of one rMQR corpus case. Version (height × width) and ECC
/// are pinned per case. The mode is forced on the generator so the corpus can
/// pin per-mode facts (count-indicator widths) from the bit stream, and it is
/// recorded in the manifest.
/// </summary>
/// <param name="ErrorCorrectionLevel">"M" or "H", the RmQREccLevel name.</param>
/// <param name="Height">Symbol height in modules (7, 9, 11, 13, 15, 17).</param>
/// <param name="Width">Symbol width in modules (27, 43, 59, 77, 99, 139).</param>
/// <param name="Mode">"Numeric", "Alphanumeric", "Byte" or "Kanji". Only qrtool can produce
/// the Kanji cases (libzint emits Byte mode with ECI 20 for the same input).</param>
/// <param name="Utf8">Whether the payload needs a UTF-8-capable generator. Cases for libzint must leave this false.</param>
/// <param name="ShiftJisHex">
/// Kanji cases only: the exact Shift_JIS bytes to hand the generator, hex-encoded.
/// Normally left null, and the bytes come from CP932-encoding <paramref name="PayloadText"/>.
/// It exists for the cells where JIS X 0208 and CP932 disagree, which .NET cannot encode
/// at all (U+301C is not in CP932), so the case has to state the bytes itself.
/// </param>
public sealed record RmQRFixtureCaseDefinition(string Id, string PayloadText, string ErrorCorrectionLevel, int Height, int Width, string Mode, bool Utf8 = false, string? ShiftJisHex = null)
{
    public string VersionName => $"R{Height}x{Width}";
}

/// <summary>An rMQR fixture generator backed by one external encoder implementation.</summary>
public interface IRmQRFixtureGenerator
{
    /// <summary>Directory name under Fixtures/RmQr/ (e.g. "zint-libzint").</summary>
    string Name { get; }

    /// <summary>False when the backing toolchain is not present on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>False when this generator cannot produce the case (e.g. libzint rejects UTF-8 payloads).</summary>
    bool SupportsCase(RmQRFixtureCaseDefinition caseDefinition);

    GeneratedFixture Generate(RmQRFixtureCaseDefinition caseDefinition);
}

/// <summary>
/// The 32 rMQR versions in ISO/IEC 23941 index order (height-major), with the
/// data capacities used to build capacity-boundary corpus payloads. Values are the
/// oracle-verified tables recorded in .github/docs/specs/rmqr-encoder.md. The
/// tool keeps its own copy on purpose (it must not depend on the library it
/// produces oracles for).
/// </summary>
public static class RmQRVersionTable
{
    public sealed record Entry(int Index, int Height, int Width, int NumericM, int AlphanumericM, int ByteM, int NumericH, int AlphanumericH, int ByteH)
    {
        public string Name => $"R{Height}x{Width}";

        /// <summary>libzint / RmQRVersion numbering: index + 1.</summary>
        public int Number => Index + 1;

        public int Capacity(string ecc, string mode) => (ecc, mode) switch
        {
            ("M", "Numeric") => NumericM,
            ("M", "Alphanumeric") => AlphanumericM,
            ("M", "Byte") => ByteM,
            ("H", "Numeric") => NumericH,
            ("H", "Alphanumeric") => AlphanumericH,
            ("H", "Byte") => ByteH,
            _ => throw new ArgumentException($"Unknown ECC/mode {ecc}/{mode}"),
        };
    }

    public static readonly Entry[] Entries =
    [
        new(0, 7, 43, 12, 7, 5, 5, 3, 2),
        new(1, 7, 59, 26, 16, 11, 14, 8, 6),
        new(2, 7, 77, 45, 27, 19, 21, 13, 9),
        new(3, 7, 99, 64, 39, 27, 30, 18, 13),
        new(4, 7, 139, 102, 62, 42, 54, 33, 22),
        new(5, 9, 43, 26, 16, 11, 14, 8, 6),
        new(6, 9, 59, 47, 29, 20, 23, 14, 10),
        new(7, 9, 77, 71, 43, 30, 37, 23, 16),
        new(8, 9, 99, 97, 59, 40, 49, 30, 20),
        new(9, 9, 139, 147, 89, 61, 75, 46, 31),
        new(10, 11, 27, 14, 8, 6, 9, 6, 4),
        new(11, 11, 43, 42, 26, 18, 23, 14, 10),
        new(12, 11, 59, 71, 43, 30, 33, 20, 14),
        new(13, 11, 77, 100, 60, 41, 52, 31, 21),
        new(14, 11, 99, 133, 81, 55, 66, 40, 27),
        new(15, 11, 139, 198, 120, 82, 97, 59, 40),
        new(16, 13, 27, 26, 16, 11, 14, 8, 6),
        new(17, 13, 43, 62, 37, 26, 28, 17, 12),
        new(18, 13, 59, 88, 53, 36, 45, 27, 18),
        new(19, 13, 77, 124, 75, 51, 66, 40, 27),
        new(20, 13, 99, 171, 104, 71, 80, 49, 33),
        new(21, 13, 139, 251, 152, 104, 126, 76, 52),
        new(22, 15, 43, 76, 46, 31, 33, 20, 13),
        new(23, 15, 59, 112, 68, 46, 59, 36, 24),
        new(24, 15, 77, 157, 95, 65, 71, 43, 29),
        new(25, 15, 99, 207, 126, 86, 111, 68, 46),
        new(26, 15, 139, 301, 182, 125, 162, 98, 67),
        new(27, 17, 43, 90, 55, 37, 47, 28, 19),
        new(28, 17, 59, 131, 79, 54, 63, 38, 26),
        new(29, 17, 77, 183, 111, 76, 87, 53, 36),
        new(30, 17, 99, 236, 143, 98, 131, 79, 54),
        new(31, 17, 139, 361, 219, 150, 178, 108, 74),
    ];

    public static Entry Find(int height, int width) =>
        Entries.FirstOrDefault(e => e.Height == height && e.Width == width)
        ?? throw new ArgumentException($"R{height}x{width} is not an rMQR version.");
}
