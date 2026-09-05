namespace FeatherQR;

/// <summary>
/// How <c>QRCodeGenerator</c> splits the content into encoding-mode segments.
/// Mixed content (for example a URL prefix followed by a long numeric identifier)
/// packs into fewer bits when each run is encoded in its densest mode, which can
/// drop the symbol by one or more versions.
/// </summary>
public enum QRCodeSegmentation
{
    /// <summary>
    /// One segment in the single mode that can represent the whole content
    /// (Numeric, else Alphanumeric, else Byte). The default, and the cheapest to
    /// encode.
    /// </summary>
    Single = 0,

    /// <summary>
    /// The mixed-mode split with the fewest total bits. Never selects a larger
    /// version than <see cref="Single"/>, emits the <see cref="Single"/> bit stream
    /// verbatim when a split would not shrink the symbol, and additionally encodes
    /// content that overflows every version in a single mode — unless the minimal-bit
    /// plan would be misread on decode (a relocated byte order mark), in which case it
    /// reports "does not fit" rather than emitting a stream that decodes differently.
    /// </summary>
    /// <remarks>
    /// Opt-in because it searches candidate versions; the search itself allocates
    /// nothing for typical content and rents pooled buffers for long content.
    /// When <see cref="QRCodeGeneratorOptions.Utf8BOM"/> would actually write a byte
    /// order mark (a UTF-8 Byte-mode stream), the split is disabled and the
    /// <see cref="Single"/> stream is emitted: the BOM is a stream-level prefix, and
    /// a split would relocate it into the middle of the decoded text. Content whose
    /// single mode is Numeric or Alphanumeric never carries a BOM and still splits.
    /// Size buffers with the same segmentation you encode with, the two can select
    /// different versions.
    /// </remarks>
    Optimal = 1,
}
