namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="QRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration, so set only what you need: <c>new QRCodeGeneratorOptions { EciMode = EciMode.Utf8, QuietZoneSize = 0 }</c>.
/// </summary>
/// <remarks>
/// <para>
/// Standard QR specific rather than shared: the three generators agree on almost nothing.
/// <see cref="Version"/> is a different type in each, <see cref="QuietZoneSize"/> defaults to 4 here and to 2 in the other two, Micro QR has no ECI, and rMQR carries fit options that mean nothing here.
/// </para>
/// <para>
/// A plain value: write it with an object initializer, assign a member afterwards (<c>options.QuietZoneSize = 0;</c>), or copy it with <c>with</c>, and the three give equal values.
/// Each variable holds its own value: assigning it to another variable, or passing it to a method by value, copies it, so an assignment changes one variable and no copy of it.
/// A method or a callback that takes the options by value and assigns to them therefore changes only its own copy, and has to return them or take them by <c>ref</c>.
/// The generators take the options by <c>in</c>, a reference: they read it during the call and do not keep a reference to it afterwards, so do not assign to that variable from another thread while a call is running.
/// </para>
/// <para>
/// The members have <c>set</c> accessors, not <c>init</c>: an object initializer over them compiles at C# 7.3, where <c>init</c> needs C# 9, so no constructor has to list the options and an option added later is one more property.
/// </para>
/// </remarks>
public record struct QRCodeGeneratorOptions
{
    /// <summary>ISO/IEC 18004 quiet zone for Standard QR, and the default the 1.1.1 parameter lists applied.</summary>
    internal const int DefaultQuietZone = 4;

    // Offset from the specified default, so default(T) carries 4 rather than 0 (a legitimate
    // caller choice that cannot double as "unset"). An offset rather than a value+1 sentinel
    // keeps the canonical form unique, so the generated equality does not report two
    // identical option sets as different.
    private int _quietZoneSizeOffset;

    private int? _maskPattern;

    /// <summary>The default configuration, identical to <c>default</c>.</summary>
    public static QRCodeGeneratorOptions Default => default;

    /// <summary>
    /// Character encoding declaration.
    /// The default auto-detects ASCII (no ECI), ISO-8859-1 (assignment 3) or UTF-8 (assignment 26) from the content, and with <see cref="AllowKanji"/> Kanji mode (no ECI) for text JIS X 0208 holds.
    /// </summary>
    public EciMode EciMode { get; set; }

    /// <summary>
    /// Include a UTF-8 byte order mark.
    /// Ignored unless the content is written as UTF-8 in Byte mode; asking for one keeps text that <see cref="AllowKanji"/> would write in Kanji mode in UTF-8.
    /// When a BOM would be written, <see cref="QRSegmentation.Optimal"/> emits the single-mode stream instead of a split (the BOM is a stream-level prefix, and a split would relocate it into the middle of the decoded text).
    /// </summary>
    public bool Utf8Bom { get; set; }

    /// <summary>
    /// The versions the generator may choose from.
    /// Defaults to <see cref="QRVersionRange.Any"/>; an <c>int</c> or <c>int?</c> converts implicitly, so <c>Version = 15</c> pins one and a <c>null</c> means automatic.
    /// </summary>
    public QRVersionRange Version { get; set; }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 4, the ISO/IEC 18004 value; 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        readonly get => DefaultQuietZone + _quietZoneSizeOffset;
        set => _quietZoneSizeOffset = value - DefaultQuietZone;
    }

    /// <summary>
    /// Pin one of the eight ISO/IEC 18004 data mask patterns (0-7) instead of the automatic penalty-scored selection.
    /// <c>null</c> (the default) selects the lowest-penalty pattern.
    /// Any pattern yields a valid, decodable symbol; the automatic choice merely optimizes scan reliability.
    /// </summary>
    /// <remarks>
    /// For reproducing a symbol produced elsewhere byte-for-byte (the pattern another encoder chose is reported by <see cref="QRCodeDecodeInfo.MaskPattern"/>), and for exercising a decoder against all eight patterns.
    /// Like <see cref="Version"/>, an invalid value is an argument error and is rejected here rather than when a generator reads it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not 0-7 or <c>null</c>.</exception>
    public int? MaskPattern
    {
        readonly get => _maskPattern;
        set
        {
            if (value is < 0 or > 7)
                throw new ArgumentOutOfRangeException(nameof(MaskPattern), $"Mask pattern must be 0-7, or null for automatic selection, but was {value}");
            _maskPattern = value;
        }
    }

    /// <summary>
    /// Raise the error correction level above the requested one when the chosen version's capacity allows it, without changing the version.
    /// The requested level becomes the minimum; the version is still chosen for it, so boosting never produces a larger symbol, only spends padding that would otherwise be wasted.
    /// Recommended when an icon or custom module shape overlays the symbol.
    /// </summary>
    /// <remarks>
    /// Off by default: a raised level rewrites the format information and can change the chosen mask, so a default of on would silently change every existing symbol.
    /// Sizing is unaffected either way, the buffer size depends only on the version.
    /// </remarks>
    public bool BoostEccLevel { get; set; }

    /// <summary>
    /// How the content is split into encoding-mode segments (see <see cref="QRSegmentation"/>).
    /// Defaults to <see cref="QRSegmentation.Single"/>.
    /// <see cref="QRSegmentation.Optimal"/> never selects a larger version, emits the identical bit stream when a split would not shrink the symbol, and defers to the single-mode stream when <see cref="Utf8Bom"/> would actually write a byte order mark.
    /// Size a destination buffer with the same value you encode with.
    /// </summary>
    public QRSegmentation Segmentation { get; set; }

    /// <summary>
    /// Write text in Kanji mode where it can be: 13 bits a character and no ECI header, where UTF-8 takes 24 bits a kana or kanji behind a 12-bit ECI header.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies only where the charset is the library's choice (<see cref="EciMode.Default"/>) and no byte order mark is asked for (<see cref="Utf8Bom"/>); a charset the caller names is written as named.
    /// Then text whose every character is in JIS X 0208 (Japanese, and the Greek, Cyrillic and symbols that table holds) is one Kanji segment, and under <see cref="QRSegmentation.Optimal"/> text that is so apart from its ASCII can be Kanji runs beside runs of that ASCII, where that is the smaller symbol.
    /// Seven JIS X 0208 characters that Windows code page 932 reads differently, among them the wave dash 〜, keep a text in UTF-8, as does any character outside the table.
    /// A Structured Append set made with this option can be a Kanji set: no symbol carries an ECI header, and the parity is the XOR of the whole text's Shift_JIS bytes.
    /// </para>
    /// <para>
    /// Off by default because not every reader reads Kanji mode. An Android 17 phone's own QR scanner and Google Lens on it show nothing readable for it, where they read UTF-8 behind ECI 26.
    /// The iPhone camera, Google Lens on iPhone, ZXing and its ports, zxing-cpp and Denso Wave's reader read it.
    /// Turn it on where the readers are known to read it, an industrial scanner for example, or where the symbol has to be smaller.
    /// </para>
    /// </remarks>
    public bool AllowKanji { get; set; }
}
