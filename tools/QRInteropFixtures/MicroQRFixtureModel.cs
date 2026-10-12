namespace QRInteropFixtures;

/// <summary>
/// Input definition of one Micro QR corpus case. Version and ECC are pinned per
/// case (the corpus enumerates every version × legal ECC combination), and the
/// expected mode is declared explicitly because external encoders choose their
/// own segmentation, the manifest mode is informational, not asserted.
/// </summary>
/// <param name="ErrorCorrectionLevel">"ErrorDetectionOnly" (M1), "L", "M" or "Q", the MicroQREccLevel name.</param>
/// <param name="Version">Micro QR version 1-4 (M1-M4), requested from the generator.</param>
/// <param name="Mode">"Numeric", "Alphanumeric", "Byte" or "Kanji". Kanji is decode-only
/// for this library and only M3 and M4 define it (narrower mode indicators cannot express
/// its value). Only qrtool can produce those cases.</param>
/// <param name="Utf8">Whether the payload needs a UTF-8-capable generator. libzint cases must leave this false.</param>
/// <param name="ShiftJisHex">
/// Kanji cases only: the exact Shift_JIS bytes to hand the generator, hex-encoded.
/// Left null by every case here, so the bytes come from CP932-encoding
/// <paramref name="PayloadText"/>. Honoured all the same, for the cells where JIS X 0208
/// and CP932 disagree and .NET therefore cannot encode the text. The rMQR corpus has
/// such a case, the Micro QR one does not yet.
/// </param>
public sealed record MicroQRFixtureCaseDefinition(string Id, string PayloadText, string ErrorCorrectionLevel, int Version, string Mode, bool Utf8 = false, string? ShiftJisHex = null);

/// <summary>A Micro QR fixture generator backed by one external encoder implementation.</summary>
public interface IMicroQRFixtureGenerator
{
    /// <summary>Directory name under Fixtures/MicroQR/ (e.g. "zint-libzint").</summary>
    string Name { get; }

    /// <summary>False when the backing toolchain is not present on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>False when this generator cannot produce the case (e.g. libzint rejects UTF-8 payloads).</summary>
    bool SupportsCase(MicroQRFixtureCaseDefinition caseDefinition);

    GeneratedFixture Generate(MicroQRFixtureCaseDefinition caseDefinition);
}
