using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="RmQRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration and is what an omitted argument sends, so the shortest correct call is <c>Create(text, eccLevel)</c>.
/// </summary>
/// <remarks>
/// rMQR specific rather than shared: <see cref="Version"/> is a different type in each symbology, <see cref="QuietZoneSize"/> has a different specified default, and <see cref="FitStrategy"/>, <see cref="Height"/> and <see cref="Segmentation"/> have no meaning outside rMQR. <see cref="Fnc1"/> is the same type as Standard QR's, with rMQR's costs.
/// There is no version range here because rMQR's 32 versions are not totally ordered; fit is constrained by strategy and height instead.
/// A plain value a caller may assign to and copy with <c>with</c>, for the reasons on <see cref="QRCodeGeneratorOptions"/>.
/// </remarks>
public record struct RmQRCodeGeneratorOptions
{
    // Offset from the specified default, so default(T) carries the ISO/IEC 23941 value of 2
    // rather than 0 (a legitimate caller choice). An offset rather than a value+1 sentinel
    // keeps the canonical form unique, so equality does not report two identical option sets
    // as different.
    private int _quietZoneSizeOffset;

    /// <summary>The default configuration, identical to <c>default</c>.</summary>
    public static RmQRCodeGeneratorOptions Default => default;

    /// <summary>
    /// Character encoding declaration.
    /// The default auto-detects ASCII (no ECI), ISO-8859-1 (assignment 3) or UTF-8 (assignment 26) from the content, and with <see cref="AllowKanji"/> Kanji mode (no ECI) for text JIS X 0208 holds.
    /// </summary>
    /// <remarks>
    /// Only <see cref="EciMode.Default"/>, <see cref="EciMode.Iso8859_1"/> and <see cref="EciMode.Utf8"/> are accepted.
    /// Declaring Latin-1 over content it cannot represent throws rather than silently re-encoding.
    /// </remarks>
    public EciMode EciMode { get; set; }

    /// <summary>
    /// A specific version, or <c>null</c> (the default) to fit one automatically by <see cref="FitStrategy"/> and <see cref="Height"/>.
    /// </summary>
    public RmQRVersion? Version { get; set; }

    /// <summary>
    /// How to choose among the versions that hold the content.
    /// Defaults to <see cref="RmQRFitStrategy.MinimizeArea"/>, the choice both reference encoders make.
    /// </summary>
    public RmQRFitStrategy FitStrategy { get; set; }

    /// <summary>
    /// Restrict automatic fitting to one symbol height, or <c>null</c> (the default) to consider every height.
    /// Must agree with <see cref="Version"/> when both are set.
    /// </summary>
    public RmQRHeight? Height { get; set; }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 2, the ISO/IEC 23941 value; 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        readonly get => RmQRConstants.QuietZoneModules + _quietZoneSizeOffset;
        set => _quietZoneSizeOffset = value - RmQRConstants.QuietZoneModules;
    }

    /// <summary>
    /// Whether to split the content into mixed-mode segments.
    /// Defaults to <see cref="RmQRSegmentation.Single"/>.
    /// Size a destination buffer with the same value you encode with: the two modes can select different versions.
    /// </summary>
    public RmQRSegmentation Segmentation
    {
        readonly get => (RmQRSegmentation)(_route & SegmentationMask);
        set => _route = (_route & ~SegmentationMask) | ((int)value & SegmentationMask);
    }

    /// <summary>
    /// The FNC1 mode to write (ISO/IEC 23941 7.4.8, as ISO/IEC 18004 6.4.8): <see cref="Fnc1Mode.FirstPosition"/> for GS1 data, or second position with an application indicator for an industry format registered with AIM.
    /// Defaults to <see cref="Fnc1Mode.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The content is the data as a reader returns it: a GS1 element string with U+001D as the separator after each variable-length value another follows, without a symbology identifier or the application indicator.
    /// The indicator is written after any ECI header and before the data, and in an Alphanumeric segment the separator is written as <c>%</c> and a literal <c>%</c> as <c>%%</c>, so the content reads back as given.
    /// A separator followed by a separator or a literal <c>%</c> cannot share an Alphanumeric segment, since a reader takes the <c>%%</c> first: such content takes Byte mode under <see cref="RmQRSegmentation.Single"/>, and <see cref="RmQRSegmentation.Optimal"/> breaks the run or uses Byte mode, whichever is smaller.
    /// </para>
    /// <para>
    /// The mode costs 3 bits in first position and 11 in second (the 3-bit indicator and the 8-bit application indicator), which can move content at a version's edge to another version under the fit strategy, or out of a requested version. Size a destination buffer with the same value you encode with.
    /// Whether the data is valid GS1 data is not checked here; the <c>FeatherQR.Gs1</c> package checks element strings and builds them from <c>(AI)</c> text.
    /// Few readers read rMQR at all, and of those zxing-cpp reads both modes; no other writer is known to write second position in rMQR.
    /// </para>
    /// </remarks>
    public Fnc1Mode Fnc1
    {
        readonly get => Fnc1Mode.FromRouteBits((int)((uint)_route >> Fnc1Shift));
        set => _route = (_route & SegmentationMask) | (value.RouteBits << Fnc1Shift);
    }

    // One field holds the segmentation (the low 22 bits, which keeps an undefined enum value as it was given so
    // the generator can refuse it) and the FNC1 mode (the top 10 bits), so the generator's default route tells
    // "nothing but Single and no FNC1" from everything else with the one compare it already made on the
    // segmentation alone: a non-zero field sends the call to the cold route, which reads both out. Declared
    // where the segmentation's own field was, so the struct keeps its layout.
    private int _route;
    private const int SegmentationMask = (1 << Fnc1Shift) - 1;
    private const int Fnc1Shift = 22;

    /// <summary>Whether a call takes the cold route: a segmentation other than <see cref="RmQRSegmentation.Single"/>, an FNC1 mode, or both.</summary>
    internal readonly bool TakesColdRoute => _route != 0;

    /// <summary>
    /// Write text in Kanji mode where it can be: 13 bits a character and no ECI header, where UTF-8 takes 24 bits a kana or kanji behind an 11-bit ECI header.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies only where the charset is the library's choice (<see cref="EciMode.Default"/>); a charset the caller names is written as named.
    /// Then text whose every character is in JIS X 0208 (Japanese, and the Greek, Cyrillic and symbols that table holds) is one Kanji segment, and under <see cref="RmQRSegmentation.Optimal"/> text that is so apart from its ASCII can be Kanji runs beside runs of that ASCII, where that is the smaller symbol.
    /// Seven JIS X 0208 characters that Windows code page 932 reads differently, among them the wave dash 〜, keep a text in UTF-8, as does any character outside the table.
    /// The capacity can matter more than in Standard QR: R7x43-M holds 3 characters in Kanji mode and 1 in UTF-8.
    /// </para>
    /// <para>
    /// Off by default, as on the other two symbologies, because not every reader reads Kanji mode: in Standard QR an Android 17 phone's own QR scanner and Google Lens on it show nothing readable for it.
    /// Neither phone's own scanner reads rMQR at all; the dedicated readers tried read it in Kanji mode and in UTF-8 alike.
    /// </para>
    /// </remarks>
    public bool AllowKanji { get; set; }
}
