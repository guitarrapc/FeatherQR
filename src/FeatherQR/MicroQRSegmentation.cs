namespace FeatherQR;

/// <summary>
/// How <c>MicroQRCodeGenerator</c> splits the content into encoding-mode segments.
/// Micro QR capacities are tiny (5 digits at M1, 15 Byte-mode characters at M4-L), so mixing modes (for example a short prefix followed by a numeric tail) can drop the symbol a version, or encode content no single mode fits at all.
/// </summary>
public enum MicroQRSegmentation
{
    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte).
    /// The default, and the cheapest to encode.
    /// </summary>
    Single = 0,

    /// <summary>
    /// The mixed-mode split with the fewest total bits.
    /// Never selects a larger version than <see cref="Single"/>, emits the <see cref="Single"/> bit stream verbatim when a split would not shrink the symbol, and additionally encodes content that overflows every version in a single mode — unless the minimal-bit plan would be misread on decode (a relocated byte order mark, or a Latin-1 run the charset heuristic would read as UTF-8), in which case it reports "does not fit" rather than emitting a stream that decodes differently.
    /// </summary>
    /// <remarks>
    /// Opt-in because it prices candidate versions; the search allocates nothing (Micro QR content never exceeds 35 characters).
    /// The plan respects each version's mode set — M1 is Numeric-only and M2 has no Byte mode — so a version is never selected for a plan whose runs it cannot carry.
    /// Size buffers with the same segmentation you encode with, the two can select different versions.
    /// </remarks>
    Optimal = 1,
}
