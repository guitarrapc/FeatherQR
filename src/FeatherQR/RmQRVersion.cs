namespace FeatherQR;

/// <summary>
/// rMQR Code symbol version (ISO/IEC 23941): 32 rectangular sizes named R{height}x{width}.
/// Values follow the ISO version index (height-major order) plus one, so <c>(int)version - 1</c> is the 5-bit version index carried in the format information and <c>(int)version</c> is libzint's rMQR version number.
/// </summary>
public enum RmQRVersion
{
    /// <summary>7 × 43 modules.</summary>
    R7x43 = 1,
    /// <summary>7 × 59 modules.</summary>
    R7x59 = 2,
    /// <summary>7 × 77 modules.</summary>
    R7x77 = 3,
    /// <summary>7 × 99 modules.</summary>
    R7x99 = 4,
    /// <summary>7 × 139 modules.</summary>
    R7x139 = 5,
    /// <summary>9 × 43 modules.</summary>
    R9x43 = 6,
    /// <summary>9 × 59 modules.</summary>
    R9x59 = 7,
    /// <summary>9 × 77 modules.</summary>
    R9x77 = 8,
    /// <summary>9 × 99 modules.</summary>
    R9x99 = 9,
    /// <summary>9 × 139 modules.</summary>
    R9x139 = 10,
    /// <summary>11 × 27 modules.</summary>
    R11x27 = 11,
    /// <summary>11 × 43 modules.</summary>
    R11x43 = 12,
    /// <summary>11 × 59 modules.</summary>
    R11x59 = 13,
    /// <summary>11 × 77 modules.</summary>
    R11x77 = 14,
    /// <summary>11 × 99 modules.</summary>
    R11x99 = 15,
    /// <summary>11 × 139 modules.</summary>
    R11x139 = 16,
    /// <summary>13 × 27 modules.</summary>
    R13x27 = 17,
    /// <summary>13 × 43 modules.</summary>
    R13x43 = 18,
    /// <summary>13 × 59 modules.</summary>
    R13x59 = 19,
    /// <summary>13 × 77 modules.</summary>
    R13x77 = 20,
    /// <summary>13 × 99 modules.</summary>
    R13x99 = 21,
    /// <summary>13 × 139 modules.</summary>
    R13x139 = 22,
    /// <summary>15 × 43 modules.</summary>
    R15x43 = 23,
    /// <summary>15 × 59 modules.</summary>
    R15x59 = 24,
    /// <summary>15 × 77 modules.</summary>
    R15x77 = 25,
    /// <summary>15 × 99 modules.</summary>
    R15x99 = 26,
    /// <summary>15 × 139 modules.</summary>
    R15x139 = 27,
    /// <summary>17 × 43 modules.</summary>
    R17x43 = 28,
    /// <summary>17 × 59 modules.</summary>
    R17x59 = 29,
    /// <summary>17 × 77 modules.</summary>
    R17x77 = 30,
    /// <summary>17 × 99 modules.</summary>
    R17x99 = 31,
    /// <summary>17 × 139 modules.</summary>
    R17x139 = 32,
}
