namespace FeatherQR;

/// <summary>
/// How <c>MicroQRCodeGenerator</c> splits the content into encoding-mode segments.
/// Micro QR capacities are tiny (5 digits at M1, 15 Byte-mode characters at M4-L), so mixing modes (for example a short prefix followed by a numeric tail) can drop the symbol a version, or encode content no single mode fits at all.
/// </summary>
public enum MicroQRSegmentation
{
    /// <summary>
    /// The default: the mixed-mode split with the fewest total bits. Text JIS X 0208 holds apart from its ASCII characters can be written as Kanji runs beside runs of that ASCII, when <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/> is set and that is the smaller symbol.
    /// Never selects a larger version than <see cref="Single"/>, emits the <see cref="Single"/> bit stream verbatim when a split would not shrink the symbol, and additionally encodes content that overflows every version in a single mode, unless the minimal-bit plan would be misread on decode (a Latin-1 run the charset heuristic would read as UTF-8 or Shift_JIS), in which case it reports "does not fit" rather than emitting a stream that decodes differently. A U+FEFF inside UTF-8 content never opens a Byte run, where a reader would drop it as a byte order mark: the run opens a character early and keeps it interior.
    /// </summary>
    /// <remarks>
    /// The search prices candidate versions and allocates nothing (Micro QR content never exceeds 35 characters). <see cref="Single"/> skips it.
    /// The plan respects each version's mode set (M1 is Numeric-only and M2 has no Byte mode), so a version is never selected for a plan whose runs it cannot carry.
    /// Size buffers with the same segmentation you encode with, the two can select different versions.
    /// </remarks>
    Optimal = 0,

    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte, or Kanji for text JIS X 0208 holds entirely when <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/> is set).
    /// The cheapest to encode: one pass over the content and no version search, and the bit stream every release before 2.0.0 emitted.
    /// </summary>
    Single = 1,
}
