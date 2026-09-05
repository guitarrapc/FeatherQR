namespace FeatherQR;

/// <summary>
/// Diagnostic information produced by a Micro QR code decode attempt.
/// </summary>
/// <remarks>
/// Statuses are shared with the Standard QR decoder (<see cref="DecodeStatus"/>);
/// version, ECC level and mask pattern use the Micro QR domains.
/// </remarks>
public readonly struct MicroQRCodeDecodeInfo
{
    internal MicroQRCodeDecodeInfo(DecodeStatus status, MicroQRVersion version, MicroQREccLevel eccLevel, int maskPattern, int errorsCorrected)
    {
        Status = status;
        Version = version;
        EccLevel = eccLevel;
        MaskPattern = maskPattern;
        ErrorsCorrected = errorsCorrected;
    }

    /// <summary>Decode result status. <see cref="DecodeStatus.Success"/> when decoding succeeded.</summary>
    public DecodeStatus Status { get; }

    /// <summary>Micro QR version (M1-M4), or <c>default</c> (0) when the matrix was invalid.</summary>
    public MicroQRVersion Version { get; }

    /// <summary>Error correction level read from the format information.</summary>
    public MicroQREccLevel EccLevel { get; }

    /// <summary>Mask pattern (0-3) read from the format information, or -1 when unknown.</summary>
    public int MaskPattern { get; }

    /// <summary>Number of codeword errors corrected by Reed-Solomon decoding.</summary>
    public int ErrorsCorrected { get; }
}
