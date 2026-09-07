namespace FeatherQR;

/// <summary>
/// How <c>RmQRCodeGenerator</c> splits the content into encoding-mode segments. rMQR capacities are small, so mixing modes (for example a Byte prefix followed by a Numeric tail) can drop the symbol by one or more versions.
/// </summary>
public enum RmQRSegmentation
{
    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte).
    /// The default, and the cheapest to encode.
    /// </summary>
    Single = 0,

    /// <summary>
    /// The mixed-mode split with the fewest total bits.
    /// Never selects a symbol with more core modules than <see cref="Single"/>, emits the <see cref="Single"/> bit stream verbatim when a split would not shrink it, and additionally encodes content that overflows every version in a single mode — unless the minimal-bit plan would be misread on decode (a relocated byte order mark), in which case it reports "does not fit" rather than emitting a stream that decodes differently.
    /// </summary>
    /// <remarks>
    /// Opt-in because it searches candidate versions; the search itself allocates nothing, and content no split can help is ruled out before it starts.
    /// Fewer core modules is not the same as a smaller image: <see cref="RmQRFitStrategy"/> ranks by core modules while the quiet zone adds to each dimension, so a flatter symbol can render onto a larger grid.
    /// Size buffers with the same segmentation you encode with.
    /// </remarks>
    Optimal = 1,
}
