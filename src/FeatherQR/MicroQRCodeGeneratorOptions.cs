namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="MicroQRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration (automatic version, 2-module quiet zone), so set only what you need: <c>new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, QuietZoneSize = 0 }</c>.
/// </summary>
/// <remarks>
/// The smallest option set of the three symbologies: Micro QR has no ECI, and no fit strategy because M1-M4 are totally ordered by capacity.
/// A plain value a caller may assign to and copy with <c>with</c>, for the reasons on <see cref="QRCodeGeneratorOptions"/>.
/// </remarks>
public record struct MicroQRCodeGeneratorOptions
{
    // Offset from the specified default, for the reasons on QRCodeGeneratorOptions.QuietZoneSize.
    private int _quietZoneSizeOffset;

    private int? _maskPattern;

    /// <summary>The default configuration, identical to <c>default</c>.</summary>
    public static MicroQRCodeGeneratorOptions Default => default;

    /// <summary>
    /// The versions the generator may choose from.
    /// Defaults to <see cref="MicroQRVersionRange.Any"/>; a <see cref="MicroQRVersion"/> or its nullable converts implicitly, so a <c>null</c> means automatic.
    /// </summary>
    public MicroQRVersionRange Version { get; set; }

    /// <summary>
    /// Pin one of the four Micro QR data mask patterns (0-3, ISO/IEC 18004 Table 10) instead of the automatic edge-score selection.
    /// <c>null</c> (the default) selects the highest-scoring pattern.
    /// Any pattern yields a valid, decodable symbol; the automatic choice merely optimizes scan reliability.
    /// </summary>
    /// <remarks>
    /// For reproducing a symbol produced elsewhere byte-for-byte (the pattern another encoder chose is reported by <see cref="MicroQRCodeDecodeInfo.MaskPattern"/>), and for exercising a decoder against all four patterns.
    /// Micro QR numbers its patterns 0-3; they are not the Standard QR patterns of the same index.
    /// Like <see cref="Version"/>, an invalid value is an argument error and is rejected here rather than when a generator reads it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not 0-3 or <c>null</c>.</exception>
    public int? MaskPattern
    {
        readonly get => _maskPattern;
        set
        {
            if (value is < 0 or > 3)
                throw new ArgumentOutOfRangeException(nameof(MaskPattern), $"Mask pattern must be 0-3, or null for automatic selection, but was {value}");
            _maskPattern = value;
        }
    }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 2, the ISO/IEC 18004 value for Micro QR; 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        readonly get => MicroQRCodeGenerator.DefaultQuietZone + _quietZoneSizeOffset;
        set => _quietZoneSizeOffset = value - MicroQRCodeGenerator.DefaultQuietZone;
    }

    /// <summary>
    /// How the content is split into encoding-mode segments (see <see cref="MicroQRSegmentation"/>).
    /// Defaults to <see cref="MicroQRSegmentation.Single"/>.
    /// <see cref="MicroQRSegmentation.Optimal"/> never selects a larger version, and emits the identical bit stream when a split would not shrink the symbol.
    /// Size a destination buffer with the same value you encode with.
    /// </summary>
    public MicroQRSegmentation Segmentation { get; set; }

    /// <summary>
    /// Write text in Kanji mode where it can be: 13 bits a character, where UTF-8 takes 24 bits a kana or kanji.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text whose every character is in JIS X 0208 (Japanese, and the Greek, Cyrillic and symbols that table holds) is one Kanji segment, and under <see cref="MicroQRSegmentation.Optimal"/> text that is so apart from its ASCII can be Kanji runs beside runs of that ASCII, where that is the smaller symbol.
    /// Seven JIS X 0208 characters that Windows code page 932 reads differently, among them the wave dash 〜, keep a text in UTF-8, as does any character outside the table.
    /// Kanji mode exists from M3, and nearly doubles what Japanese text fits: M4-L holds 9 characters in Kanji mode and 5 in UTF-8.
    /// Micro QR has no ECI, so its UTF-8 bytes read as UTF-8 only in a reader that recognises them, where Kanji mode declares itself.
    /// </para>
    /// <para>
    /// Off by default, as on the other two symbologies, because not every reader reads Kanji mode: in Standard QR an Android 17 phone's own QR scanner and Google Lens on it show nothing readable for it.
    /// Neither phone's own scanner reads Micro QR at all; Denso Wave's reader and zxing-cpp read it in Kanji mode and in UTF-8 alike.
    /// </para>
    /// </remarks>
    public bool AllowKanji { get; set; }
}
