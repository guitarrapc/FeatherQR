namespace FeatherQR;

/// <summary>
/// How <c>MicroQRCodeGenerator</c> splits the content into encoding-mode segments.
/// Micro QR capacities are tiny (5 digits at M1, 15 Byte-mode characters at M4-L), so mixing modes (for example a short prefix followed by a numeric tail) can drop the symbol a version, or encode content no single mode fits at all.
/// </summary>
public enum MicroQRSegmentation
{
    /// <summary>
    /// Selects the mixed-mode segmentation with the fewest total bits. This is the default.
    /// </summary>
    /// <remarks>
    /// Never selects a larger version than <see cref="Single"/>, and preserves the Single bit stream when splitting would not shrink the symbol.
    /// With <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/>, eligible text can mix Kanji and ASCII runs when that produces a smaller symbol.
    /// Can encode content that exceeds single-mode capacity. U+FEFF inside UTF-8 content is preserved.
    /// Reports "does not fit" if the minimal plan would be decoded with a different charset, rather than emitting text that cannot round-trip.
    /// <para/>
    /// The search prices candidate versions and allocates nothing (Micro QR content never exceeds 35 characters). <see cref="Single"/> skips it.
    /// The plan respects each version's mode set (M1 is Numeric-only and M2 has no Byte mode), so a version is never selected for a plan whose runs it cannot carry.
    /// Size buffers with the same segmentation you encode with, the two can select different versions.
    /// </remarks>
    Optimal = 0,

    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte, or Kanji for text JIS X 0208 holds entirely when <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/> is set).
    /// Does not plan mixed-mode segments.
    /// </summary>
    Single = 1,
}
