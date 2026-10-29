namespace FeatherQR;

/// <summary>
/// The symbology of a symbol read from an image: Standard QR, Micro QR or rMQR.
/// </summary>
public enum Symbology
{
    /// <summary>Standard QR (ISO/IEC 18004), versions 1 to 40.</summary>
    StandardQR = 0,

    /// <summary>Micro QR (ISO/IEC 18004), versions M1 to M4.</summary>
    MicroQR = 1,

    /// <summary>rMQR, the rectangular Micro QR code (ISO/IEC 23941), versions R7x43 to R17x139.</summary>
    RmQR = 2,
}
