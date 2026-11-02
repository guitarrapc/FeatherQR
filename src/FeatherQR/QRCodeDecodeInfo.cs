namespace FeatherQR;

/// <summary>
/// Diagnostic information produced by a QR code decode attempt.
/// </summary>
public readonly record struct QRCodeDecodeInfo
{
    internal QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected)
        : this(status, version, eccLevel, maskPattern, errorsCorrected, default, default, default, null)
    {
    }

    internal QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected, QRStructuredAppend structuredAppend, Fnc1Mode fnc1, int? eciAssignment)
        : this(status, version, eccLevel, maskPattern, errorsCorrected, default, structuredAppend, fnc1, eciAssignment)
    {
    }

    private QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected, SymbolCorners corners, QRStructuredAppend structuredAppend, Fnc1Mode fnc1, int? eciAssignment)
    {
        Status = status;
        Version = version;
        EccLevel = eccLevel;
        MaskPattern = maskPattern;
        ErrorsCorrected = errorsCorrected;
        Corners = corners;
        StructuredAppend = structuredAppend;
        Fnc1 = fnc1;
        _eciAssignmentPlusOne = eciAssignment is { } number ? number + 1 : 0;
    }

    // Not an int?: its 8 bytes would grow the result arrays the text calls allocate. Zero is none, so a default info reports none.
    private readonly int _eciAssignmentPlusOne;

    /// <summary>The same result with the symbol's image position attached. The image decoders call this on success.</summary>
    internal QRCodeDecodeInfo WithCorners(SymbolCorners corners) => new(Status, Version, EccLevel, MaskPattern, ErrorsCorrected, corners, StructuredAppend, Fnc1, EciAssignment);

    /// <summary>Decode result status. <see cref="DecodeStatus.Success"/> when decoding succeeded.</summary>
    public DecodeStatus Status { get; }

    /// <summary>QR code version (1-40), or 0 when the matrix was invalid.</summary>
    public int Version { get; }

    /// <summary>Error correction level read from the format information.</summary>
    public QREccLevel EccLevel { get; }

    /// <summary>Mask pattern (0-7) read from the format information, or -1 when unknown.</summary>
    public int MaskPattern { get; }

    /// <summary>Total number of codeword errors corrected by Reed-Solomon decoding.</summary>
    public int ErrorsCorrected { get; }

    /// <summary>Where the symbol sits in the image, when decoded from one. See <see cref="SymbolCorners"/>. Empty for a matrix-level decode or a failed one.</summary>
    public SymbolCorners Corners { get; }

    /// <summary>The Structured Append header, when the symbol is one of a set. See <see cref="QRStructuredAppend"/>. Empty for a symbol that carries none, or a failed decode.</summary>
    public QRStructuredAppend StructuredAppend { get; }

    /// <summary>The FNC1 mode, when the symbol carries one (GS1 data, or an industry format with its application indicator). See <see cref="Fnc1Mode"/>. <c>default</c>, neither position, for a symbol that carries none or a failed decode.</summary>
    public Fnc1Mode Fnc1 { get; }

    /// <summary>
    /// The ECI assignment the content is declared under, or <c>null</c> for none. A conforming symbol's assignments run from 0 to 999999.
    /// </summary>
    /// <remarks>
    /// The text calls report the first assignment the symbol declares.
    /// A bytes call that reads the symbol (<see cref="QRCodeDecoder.TryDecodeBytes(QRCodeData, out byte[], out QRCodeDecodeInfo)"/>) reports the one the bytes of its Byte and Kanji segments are under, so the bytes can be converted with the charset it names, and the first when no such segment holds a byte.
    /// Reported on a read and on a read whose content cannot be given as text (<see cref="DecodeStatus.UnsupportedContent"/>, <see cref="DecodeStatus.UnmappedCharacter"/>), which reports the first.
    /// An assignment the text calls have no charset for fails them with <see cref="DecodeStatus.UnsupportedContent"/>, and the bytes calls read it.
    /// <c>null</c> after any other failure.
    /// </remarks>
    public int? EciAssignment => _eciAssignmentPlusOne == 0 ? null : _eciAssignmentPlusOne - 1;
}
