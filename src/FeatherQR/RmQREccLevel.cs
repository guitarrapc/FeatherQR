namespace FeatherQR;

/// <summary>
/// rMQR Code error correction level (ISO/IEC 23941). rMQR defines only two levels; the numeric value is the ECC bit carried in the format information.
/// </summary>
public enum RmQREccLevel
{
    /// <summary>Medium: about 15% of codewords recoverable.</summary>
    M = 0,

    /// <summary>High: about 30% of codewords recoverable.</summary>
    H = 1,
}
