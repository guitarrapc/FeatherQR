namespace FeatherQR;

/// <summary>
/// Diagnostic information from an rMQR decode attempt (<see cref="RmQRCodeDecoder"/>): status, and when the format information could be read, the version and ECC level plus the number of Reed-Solomon codeword corrections applied. rMQR has a single data mask, so there is no mask pattern to report.
/// </summary>
public readonly record struct RmQRCodeDecodeInfo
{
    internal RmQRCodeDecodeInfo(DecodeStatus status, RmQRVersion version, RmQREccLevel eccLevel, int errorsCorrected)
        : this(status, version, eccLevel, errorsCorrected, default)
    {
    }

    private RmQRCodeDecodeInfo(DecodeStatus status, RmQRVersion version, RmQREccLevel eccLevel, int errorsCorrected, SymbolCorners corners)
    {
        Status = status;
        Version = version;
        EccLevel = eccLevel;
        ErrorsCorrected = errorsCorrected;
        Corners = corners;
    }

    /// <summary>The same result with the symbol's image position attached; the image decoder calls this on success.</summary>
    internal RmQRCodeDecodeInfo WithCorners(SymbolCorners corners) => new(Status, Version, EccLevel, ErrorsCorrected, corners);

    /// <summary>Decode outcome; <see cref="DecodeStatus.Success"/> when text was produced.</summary>
    public DecodeStatus Status { get; }

    /// <summary>The symbol version (from the physical dimensions), or 0 when the input is not an rMQR matrix.</summary>
    public RmQRVersion Version { get; }

    /// <summary>The ECC level read from the format information (valid once the format decoded).</summary>
    public RmQREccLevel EccLevel { get; }

    /// <summary>Total Reed-Solomon codeword corrections across all blocks (0 for a clean symbol).</summary>
    public int ErrorsCorrected { get; }

    /// <summary>Where the symbol sits in the image, when decoded from one; see <see cref="SymbolCorners"/>. Empty for a matrix-level decode or a failed one.</summary>
    public SymbolCorners Corners { get; }
}
