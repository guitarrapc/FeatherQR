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
    /// Ignored unless the content is written as UTF-8 in Byte mode. Asking for one keeps text that <see cref="AllowKanji"/> would write in Kanji mode in UTF-8.
    /// When a BOM would be written, <see cref="QRSegmentation.Optimal"/> emits the single-mode stream instead of a split (the BOM is a stream-level prefix, and a split would relocate it into the middle of the decoded text).
    /// </summary>
    public bool Utf8Bom { get; set; }

    /// <summary>
    /// The versions the generator may choose from.
    /// Defaults to <see cref="QRVersionRange.Any"/>. An <c>int</c> or <c>int?</c> converts implicitly, so <c>Version = 15</c> pins one and a <c>null</c> means automatic.
    /// </summary>
    public QRVersionRange Version { get; set; }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 4, the ISO/IEC 18004 value, and 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        readonly get => DefaultQuietZone + _quietZoneSizeOffset;
        set => _quietZoneSizeOffset = value - DefaultQuietZone;
    }

    /// <summary>
    /// Pin one of the eight ISO/IEC 18004 data mask patterns (0-7) instead of the automatic penalty-scored selection.
    /// <c>null</c> (the default) selects the lowest-penalty pattern.
    /// Any pattern yields a valid, decodable symbol. The automatic choice merely optimizes scan reliability.
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
    /// The requested level becomes the minimum. The version is still chosen for it, so boosting never produces a larger symbol, only spends padding that would otherwise be wasted.
    /// Recommended when an icon or custom module shape overlays the symbol.
    /// </summary>
    /// <remarks>
    /// Off by default: a raised level rewrites the format information and can change the chosen mask, so a default of on would silently change every existing symbol.
    /// Sizing is unaffected either way, the buffer size depends only on the version.
    /// </remarks>
    public bool BoostEccLevel { get; set; }

    /// <summary>
    /// How the content is split into encoding-mode segments (see <see cref="QRSegmentation"/>).
    /// Defaults to <see cref="QRSegmentation.Optimal"/>.
    /// <see cref="QRSegmentation.Optimal"/> never selects a larger version, emits the identical bit stream when a split would not shrink the symbol, and defers to the single-mode stream when <see cref="Utf8Bom"/> would actually write a byte order mark.
    /// Size a destination buffer with the same value you encode with.
    /// </summary>
    public QRSegmentation Segmentation
    {
        readonly get => (QRSegmentation)(_route & SegmentationMask);
        set => _route = (_route & ~SegmentationMask) | ((uint)value <= SegmentationMask ? (int)value : SegmentationMask);
    }

    /// <summary>
    /// The FNC1 mode to write (ISO/IEC 18004:2006 6.4.8): <see cref="Fnc1Mode.FirstPosition"/> for GS1 data, or second position with an application indicator for an industry format registered with AIM.
    /// Defaults to <see cref="Fnc1Mode.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The content is the data as a reader returns it, with U+001D as the separator and without a symbology identifier or the application indicator. In first position it is a GS1 element string, with the separator after each variable-length value another follows.
    /// The indicator is written after any ECI header and before the data, and in an Alphanumeric segment the separator is written as <c>%</c> and a literal <c>%</c> as <c>%%</c>, so the content reads back as given.
    /// A separator followed by a separator or a literal <c>%</c> cannot share an Alphanumeric segment, since a reader takes the <c>%%</c> first: such content takes Byte mode under <see cref="QRSegmentation.Single"/>, and <see cref="QRSegmentation.Optimal"/> breaks the run or uses Byte mode, whichever is smaller.
    /// </para>
    /// <para>
    /// The mode costs 4 bits in first position and 12 in second, which can move content at a version's edge to the next version. Size a destination buffer with the same value you encode with.
    /// A Structured Append set refuses FNC1 (<see cref="QRCodeGenerator.CreateStructuredAppend(ReadOnlySpan{char}, QREccLevel, in QRCodeGeneratorOptions)"/> throws <see cref="ArgumentException"/>), as no reader is known to read FNC1 across a set.
    /// Whether the data is valid GS1 data is not checked here. The <c>FeatherQR.Gs1</c> package checks element strings and builds them from <c>(AI)</c> text.
    /// </para>
    /// </remarks>
    public Fnc1Mode Fnc1
    {
        readonly get => Fnc1Mode.FromRouteBits((int)((uint)_route >> Fnc1Shift));
        set => _route = (_route & SegmentationMask) | (value.RouteBits << Fnc1Shift);
    }

    // One field holds the segmentation (the low 22 bits) and the FNC1 mode (the top 10 bits), so the generator
    // tells "Single and no FNC1" from everything else with the one compare it made on the segmentation alone.
    // A segmentation value the 22 bits cannot hold saturates to the mask rather than being masked, because a
    // masked value could read back as a defined one and pass validation.
    private int _route;
    private const int SegmentationMask = (1 << Fnc1Shift) - 1;
    private const int Fnc1Shift = 22;

    /// <summary>The largest segmentation value the field keeps as given. A value below 0 or above it reads back as the next one up, so an error cannot name the value the caller gave.</summary>
    internal const int LargestKeptSegmentation = SegmentationMask - 1;

    /// <summary>Whether a call takes the single-mode route: <see cref="QRSegmentation.Single"/> and no FNC1 mode, the one route that reads no other option on its way in.</summary>
    internal readonly bool TakesSingleRoute => _route == (int)QRSegmentation.Single;

    /// <summary>
    /// Write text in Kanji mode where it can be: 13 bits a character and no ECI header, where UTF-8 takes 24 bits a kana or kanji behind a 12-bit ECI header.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies only where the charset is the library's choice (<see cref="EciMode.Default"/>) and no byte order mark is asked for (<see cref="Utf8Bom"/>). A charset the caller names is written as named.
    /// Then text whose every character is in JIS X 0208 (Japanese, and the Greek, Cyrillic and symbols that table holds) is one Kanji segment, and under <see cref="QRSegmentation.Optimal"/> text that is so apart from its ASCII can be Kanji runs beside runs of that ASCII, where that is the smaller symbol.
    /// Seven JIS X 0208 characters that Windows code page 932 reads differently, among them the wave dash 〜, keep a text in UTF-8, as does any character outside the table.
    /// A Structured Append set made with this option can be a Kanji set: no symbol carries an ECI header, and the parity is the XOR of the whole text's Shift_JIS bytes.
    /// </para>
    /// <para>
    /// Off by default because not every reader reads Kanji mode. An Android 17 phone's own QR scanner and Google Lens on it show nothing readable for it, where they read UTF-8 behind ECI 26.
    /// The iPhone camera, Google Lens on iPhone and the dedicated readers tried read it.
    /// Turn it on where the readers are known to read it, an industrial scanner for example, or where the symbol has to be smaller.
    /// </para>
    /// </remarks>
    public bool AllowKanji { get; set; }
}
