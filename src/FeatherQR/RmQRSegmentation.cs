namespace FeatherQR;

/// <summary>
/// How <c>RmQRCodeGenerator</c> splits the content into encoding-mode segments. rMQR capacities are small, so mixing modes (for example a Byte prefix followed by a Numeric tail) can drop the symbol by one or more versions.
/// </summary>
public enum RmQRSegmentation
{
    /// <summary>
    /// Selects the mixed-mode segmentation with the fewest total bits. This is the default.
    /// </summary>
    /// <remarks>
    /// Never selects a symbol with more core modules than <see cref="Single"/>, and preserves the Single bit stream when splitting would not shrink the symbol.
    /// With <see cref="RmQRCodeGeneratorOptions.AllowKanji"/>, eligible text can mix Kanji and ASCII runs when that produces a smaller symbol.
    /// Kanji plans require the charset to be left to the library and carry no ECI header.
    /// Can encode content that exceeds single-mode capacity. U+FEFF inside UTF-8 content is preserved.
    /// <para/>
    /// The search prices candidate versions. It allocates nothing, and content no split can help is ruled out before it starts. <see cref="Single"/> skips it.
    /// Fewer core modules is not the same as a smaller image: <see cref="RmQRFitStrategy"/> ranks by core modules while the quiet zone adds to each dimension, so a flatter symbol can render onto a larger grid.
    /// Size buffers with the same segmentation you encode with.
    /// </remarks>
    Optimal = 0,

    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte, or Kanji for text JIS X 0208 holds entirely when <see cref="RmQRCodeGeneratorOptions.AllowKanji"/> is set and the charset is left to the library).
    /// Does not plan mixed-mode segments.
    /// </summary>
    Single = 1,
}
