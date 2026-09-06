namespace FeatherQR.Internals;

/// <summary>
/// Encoding mode enumeration (ISO/IEC 18004 Section 7.4.1).
/// Determines how data is encoded in the QR code, affecting capacity and efficiency.
///
/// Mode priority in automatic detection:
/// 1. Numeric (most efficient)
/// 2. Alphanumeric
/// 3. Byte (least efficient)
///
/// Note: ECI is not a data encoding mode, but a character set declaration.
/// </summary>
internal enum EncodingMode
{
    /// <summary>
    /// 0-9 only (10 bits per 3 digits)
    /// </summary>
    Numeric = 1,
    /// <summary>
    /// 0-9, A-Z, space, $%*+-./:  (11 bits per 2 chars).
    /// </summary>
    Alphanumeric = 2,
    /// <summary>
    /// Any 8-bit data (8 bits per byte) Default: ISO-8859-1, can be UTF-8 with ECI.
    /// </summary>
    Byte = 4,
    /// <summary>
    /// Extended Channel Interpretation (metadata only).
    /// Mode indicator: 0111 + 8-bit assignment number Specifies character encoding for Byte mode:
    ///   - ECI 3: ISO-8859-1
    ///   - ECI 4: ISO-8859-2
    ///   - ECI 26: UTF-8 Always followed by another mode (typically Byte).
    /// </summary>
    ECI = 7,
}
