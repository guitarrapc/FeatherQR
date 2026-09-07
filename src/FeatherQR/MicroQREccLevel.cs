namespace FeatherQR;

/// <summary>
/// Micro QR Code error correction level (ISO/IEC 18004).
/// Micro QR levels differ from Standard QR <see cref="QREccLevel"/>: version M1 supports error detection only, and level H does not exist.
/// </summary>
public enum MicroQREccLevel
{
    /// <summary>
    /// Error detection only, no correction capacity.
    /// Valid only for version M1.
    /// </summary>
    ErrorDetectionOnly = 0,

    /// <summary>~7% recovery capacity. Valid for versions M2-M4.</summary>
    L = 1,

    /// <summary>~15% recovery capacity. Valid for versions M2-M4.</summary>
    M = 2,

    /// <summary>~25% recovery capacity. Valid for version M4 only.</summary>
    Q = 3,
}
