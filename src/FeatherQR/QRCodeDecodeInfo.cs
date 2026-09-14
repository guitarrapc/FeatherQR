namespace FeatherQR;

/// <summary>
/// Diagnostic information produced by a QR code decode attempt.
/// </summary>
public readonly record struct QRCodeDecodeInfo
{
    internal QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected)
        : this(status, version, eccLevel, maskPattern, errorsCorrected, default, default)
    {
    }

    internal QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected, QRStructuredAppend structuredAppend)
        : this(status, version, eccLevel, maskPattern, errorsCorrected, default, structuredAppend)
    {
    }

    private QRCodeDecodeInfo(DecodeStatus status, int version, QREccLevel eccLevel, int maskPattern, int errorsCorrected, SymbolCorners corners, QRStructuredAppend structuredAppend)
    {
        Status = status;
        Version = version;
        EccLevel = eccLevel;
        MaskPattern = maskPattern;
        ErrorsCorrected = errorsCorrected;
        Corners = corners;
        StructuredAppend = structuredAppend;
    }

    /// <summary>The same result with the symbol's image position attached; the image decoders call this on success.</summary>
    internal QRCodeDecodeInfo WithCorners(SymbolCorners corners) => new(Status, Version, EccLevel, MaskPattern, ErrorsCorrected, corners, StructuredAppend);

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

    /// <summary>Where the symbol sits in the image, when decoded from one; see <see cref="SymbolCorners"/>. Empty for a matrix-level decode or a failed one.</summary>
    public SymbolCorners Corners { get; }

    /// <summary>The Structured Append header, when the symbol is one of a set; see <see cref="QRStructuredAppend"/>. Empty for a symbol that carries none, or a failed decode.</summary>
    public QRStructuredAppend StructuredAppend { get; }
}
