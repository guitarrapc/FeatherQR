namespace FeatherQR;

/// <summary>
/// The error correction level used to recover damaged data.
/// Higher levels leave less room for content, so the same text may need a larger symbol.
/// </summary>
public enum QREccLevel
{
    /// <summary>
    /// Low error correction.
    /// </summary>
    L,
    /// <summary>
    /// Medium error correction.
    /// </summary>
    M,
    /// <summary>
    /// Quartile error correction.
    /// </summary>
    Q,
    /// <summary>
    /// High error correction.
    /// </summary>
    H
}
