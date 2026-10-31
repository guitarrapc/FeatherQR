namespace FeatherQR;

/// <summary>
/// How <c>QRCodeGenerator</c> splits the content into encoding-mode segments.
/// Mixed content (for example a URL prefix followed by a long numeric identifier) packs into fewer bits when each run is encoded in its densest mode, which can drop the symbol by one or more versions.
/// </summary>
public enum QRSegmentation
{
    /// <summary>
    /// Selects the mixed-mode segmentation with the fewest total bits. This is the default.
    /// </summary>
    /// <remarks>
    /// Never selects a larger version than <see cref="Single"/>, and preserves the Single bit stream when splitting would not shrink the symbol.
    /// With <see cref="QRCodeGeneratorOptions.AllowKanji"/>, eligible text can mix Kanji and ASCII runs when that produces a smaller symbol.
    /// Kanji plans require the charset to be left to the library and carry no ECI header.
    /// Can encode content that exceeds single-mode capacity. U+FEFF inside UTF-8 content is preserved.
    /// <para/>
    /// The search prices candidate versions. It allocates nothing for typical content and rents pooled buffers for long content. <see cref="Single"/> skips it.
    /// When <see cref="QRCodeGeneratorOptions.Utf8Bom"/> would actually write a byte order mark (a UTF-8 Byte-mode stream), the split is disabled and the <see cref="Single"/> stream is emitted: the BOM is a stream-level prefix, and a split would relocate it into the middle of the decoded text.
    /// Content whose single mode is Numeric or Alphanumeric never carries a BOM and still splits.
    /// Size buffers with the same segmentation you encode with, the two can select different versions.
    /// </remarks>
    Optimal = 0,

    /// <summary>
    /// One segment in the single mode that can represent the whole content (Numeric, else Alphanumeric, else Byte, or Kanji for text JIS X 0208 holds entirely when <see cref="QRCodeGeneratorOptions.AllowKanji"/> is set and the charset is left to the library).
    /// Does not plan mixed-mode segments.
    /// </summary>
    Single = 1,
}
