namespace FeatherQR;

/// <summary>
/// Diagnostic information from an rMQR decode attempt (<see cref="RmQRCodeDecoder"/>): status, and when the format information could be read, the version and ECC level plus the number of Reed-Solomon codeword corrections applied, and on success the FNC1 mode when the symbol carries one. rMQR has a single data mask, so there is no mask pattern to report, and it does not define Structured Append.
/// </summary>
public readonly record struct RmQRCodeDecodeInfo
{
    internal RmQRCodeDecodeInfo(DecodeStatus status, RmQRVersion version, RmQREccLevel eccLevel, int errorsCorrected)
        : this(status, version, eccLevel, errorsCorrected, default, default, null)
    {
    }

    internal RmQRCodeDecodeInfo(DecodeStatus status, RmQRVersion version, RmQREccLevel eccLevel, int errorsCorrected, Fnc1Mode fnc1, int? eciAssignment)
        : this(status, version, eccLevel, errorsCorrected, default, fnc1, eciAssignment)
    {
    }

    private RmQRCodeDecodeInfo(DecodeStatus status, RmQRVersion version, RmQREccLevel eccLevel, int errorsCorrected, SymbolCorners corners, Fnc1Mode fnc1, int? eciAssignment)
    {
        Status = status;
        Version = version;
        EccLevel = eccLevel;
        ErrorsCorrected = errorsCorrected;
        Corners = corners;
        Fnc1 = fnc1;
        _eciAssignmentPlusOne = eciAssignment is { } number ? number + 1 : 0;
    }

    // Not an int?: its 8 bytes would grow the result arrays the text calls allocate. Zero is none, so a default info reports none.
    private readonly int _eciAssignmentPlusOne;

    /// <summary>
    /// The same result with the symbol's image position attached. The image decoder attaches it to a read, one that did not fit the
    /// destination too, whose corners its scan keeps to skip the candidates inside, and strips it (<c>default</c>) from every
    /// result but a successful one before reporting.
    /// </summary>
    internal RmQRCodeDecodeInfo WithCorners(SymbolCorners corners) => new(Status, Version, EccLevel, ErrorsCorrected, corners, Fnc1, EciAssignment);

    /// <summary>Decode outcome. <see cref="DecodeStatus.Success"/> when text was produced.</summary>
    public DecodeStatus Status { get; }

    /// <summary>The symbol version (from the physical dimensions), or 0 when the input is not an rMQR matrix.</summary>
    public RmQRVersion Version { get; }

    /// <summary>The ECC level read from the format information (valid once the format decoded).</summary>
    public RmQREccLevel EccLevel { get; }

    /// <summary>Total Reed-Solomon codeword corrections across all blocks (0 for a clean symbol).</summary>
    public int ErrorsCorrected { get; }

    /// <summary>Where the symbol sits in the image, when decoded from one. See <see cref="SymbolCorners"/>. Empty for a matrix-level decode or a failed one.</summary>
    public SymbolCorners Corners { get; }

    /// <summary>The FNC1 mode, when the symbol carries one (GS1 data, or an industry format with its application indicator). See <see cref="Fnc1Mode"/>. <c>default</c>, neither position, for a symbol that carries none or a failed decode.</summary>
    public Fnc1Mode Fnc1 { get; }

    /// <summary>
    /// The ECI assignment the content is declared under, or <c>null</c> for none. A conforming symbol's assignments run from 0 to 999999.
    /// </summary>
    /// <remarks>
    /// The text calls report the first assignment the symbol declares.
    /// A bytes call that reads the symbol (<see cref="RmQRCodeDecoder.TryDecodeBytes(RmQRCodeData, out byte[], out RmQRCodeDecodeInfo)"/>) reports the one its Byte and Kanji segments are under, so the bytes can be converted with the charset it names, and the first when the symbol has no such segment.
    /// Reported on a read and on a read whose content cannot be given as text (<see cref="DecodeStatus.UnsupportedContent"/>, <see cref="DecodeStatus.UnmappedCharacter"/>), which reports the first.
    /// An assignment the text calls have no charset for fails them with <see cref="DecodeStatus.UnsupportedContent"/>, and the bytes calls read it.
    /// <c>null</c> after any other failure.
    /// </remarks>
    public int? EciAssignment => _eciAssignmentPlusOne == 0 ? null : _eciAssignmentPlusOne - 1;
}
