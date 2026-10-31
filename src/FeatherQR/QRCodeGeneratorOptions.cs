namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="QRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration, so set only what you need: <c>new QRCodeGeneratorOptions { EciMode = EciMode.Utf8, QuietZoneSize = 0 }</c>.
/// </summary>
/// <remarks>
/// Mutable value type with C# 7.3-compatible setters. Changes to a copy do not affect the original.
/// Generators read it by <c>in</c> during the call, so do not mutate the same variable concurrently.
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
    /// Allows Kanji mode for eligible text to reduce symbol size. Off by default.
    /// </summary>
    /// <remarks>
    /// Applies only with <see cref="EciMode.Default"/> and without <see cref="Utf8Bom"/>.
    /// Uses supported JIS X 0208 characters, excluding those that CP932 interprets differently.
    /// Mixing Kanji with ASCII requires <see cref="QRSegmentation.Optimal"/>. Text with characters outside the supported set keeps its normal encoding.
    /// Off by default for reader compatibility. Enable it when the intended readers support Kanji mode.
    /// </remarks>
    public bool AllowKanji { get; set; }
}
