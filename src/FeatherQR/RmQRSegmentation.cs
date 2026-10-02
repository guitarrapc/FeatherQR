namespace FeatherQR;

/// <summary>
/// How <c>RmQRCodeGenerator</c> splits the content into encoding-mode segments. rMQR capacities are small, so mixing modes (for example a Byte prefix followed by a Numeric tail) can drop the symbol by one or more versions.
/// </summary>
public enum RmQRSegmentation
{
    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte, or Kanji for text JIS X 0208 holds entirely when <see cref="RmQRCodeGeneratorOptions.AllowKanji"/> is set and the charset is left to the library).
    /// The default, and the cheapest to encode.
    /// </summary>
    Single = 0,

    /// <summary>
    /// The mixed-mode split with the fewest total bits. Text JIS X 0208 holds apart from its ASCII characters can be written as Kanji runs beside runs of that ASCII, with no ECI header, when <see cref="RmQRCodeGeneratorOptions.AllowKanji"/> is set, the charset is left to the library and that is the smaller symbol.
    /// Never selects a symbol with more core modules than <see cref="Single"/>, emits the <see cref="Single"/> bit stream verbatim when a split would not shrink it, and additionally encodes content that overflows every version in a single mode. A U+FEFF inside UTF-8 content never opens a Byte run, where a reader would drop it as a byte order mark: the run opens a character early and keeps it interior.
    /// </summary>
    /// <remarks>
    /// Opt-in because it searches candidate versions; the search itself allocates nothing, and content no split can help is ruled out before it starts.
    /// Fewer core modules is not the same as a smaller image: <see cref="RmQRFitStrategy"/> ranks by core modules while the quiet zone adds to each dimension, so a flatter symbol can render onto a larger grid.
    /// Size buffers with the same segmentation you encode with.
    /// </remarks>
    Optimal = 1,
}
