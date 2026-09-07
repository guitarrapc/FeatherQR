namespace FeatherQR;

/// <summary>
/// The character encoding declared in the symbol, as an ECI assignment.
/// </summary>
public enum EciMode
{
    // ECI header makes QR mask pattern difference, due to bit length difference.
    //
    // # Data structure:
    // When ASCII text "AB" was passed... 12 bits difference for ECI header.
    // Byte mode at version 1-9, where the count indicator is 8 bits (it widens to 16 at version 10).
    //
    // EciMode.Default
    // ┌──────────┬────────────────┬──────────┐
    // │ Mode(4b) │ Count(8b)      │ Data     │
    // │ 0100     │ 00000010       │ ...      │
    // └──────────┴────────────────┴──────────┘
    // 4 + 8 + (2 * 8) = 28 bits
    //
    // EciMode.Iso8859_1
    // ┌──────────┬────────────┬──────────┬────────────────┬──────────┐
    // │ ECI(4b)  │ Value(8b)  │ Mode(4b) │ Count(8b)      │ Data     │
    // │ 0111     │ 00000100   │ 0100     │ 00000010       │ ...      │
    // └──────────┴────────────┴──────────┴────────────────┴──────────┘
    // 4 + 8 + 4 + 8 + (2 * 8) = 40 bits
    //
    // # Effects:
    // 1. Padding
    // public void WritePadding(int targetBitCount)
    // {
    //     var remaining = targetBitCount - _builder.Length; // <- - different due to ECI header
    //     ....
    // }
    //
    // 2. ECC
    // Data difference -> Reed Solomon ECC calculation difference -> Data after interleaving is different.
    //
    // 3. Mask pattern
    // Data difference -> Optimal mask pattern may be different -> Final QR code pattern is different.

    /// <summary>
    /// Picks the encoding from the content, which is what you want unless a reader demands otherwise.
    /// </summary>
    /// <remarks>
    /// Pure ASCII is sent with no ECI header at all, which is both smallest and most widely readable.
    /// Anything ISO-8859-1 can represent becomes <see cref="Iso8859_1"/>, and the rest becomes <see cref="Utf8"/>.
    /// The header is the whole cost, 12 bits in Standard QR and 11 in rMQR, and it is what can push the content into a larger version.
    /// Two characters in Byte mode at version 1-9, where the count indicator is 8 bits wide:
    /// <code>
    /// "HE"    no header       4 + 8 + 16       = 28 bits
    /// "Ca"    ECI 3           12 + 4 + 8 + 16  = 40 bits
    /// "🎉"    ECI 26          12 + 4 + 8 + 32  = 56 bits
    /// </code>
    /// </remarks>
    Default = 0,
    /// <summary>
    /// ISO-8859-1, covering ASCII plus Western European letters such as À, Ç, Ñ, é and ü.
    /// </summary>
    /// <remarks>
    /// The ECI header costs 12 bits in Standard QR and 11 in rMQR.
    /// Emoji, CJK and Cyrillic cannot be encoded; use <see cref="Utf8"/> for those.
    /// </remarks>
    Iso8859_1 = 3,
    /// <summary>
    /// UTF-8, which covers every Unicode character: emoji, CJK, Cyrillic, Arabic, Hebrew and the rest.
    /// </summary>
    /// <remarks>
    /// The ECI header costs 12 bits in Standard QR and 11 in rMQR, and each character takes 1 to 4 bytes.
    /// For Western European text <see cref="Iso8859_1"/> is denser.
    /// </remarks>
    Utf8 = 26
}

/// <summary>
/// Helpers for <see cref="EciMode"/>.
/// </summary>
internal static class EciModeExtensions
{
    // ECI Header Structure (ISO/IEC 18004):
    // ┌─────────────────┬────────────────────────┐
    // │ ECI Indicator   │ ECI Assignment Number  │
    // │ 0111 (4 bits)   │ Variable (8-24 bits)   │
    // └─────────────────┴────────────────────────┘

    /// <summary>
    /// Gets the Standard QR ECI header size in bits.
    /// </summary>
    /// <param name="eciMode">ECI mode.</param>
    /// <returns>Header size in bits:
    /// - Default (no ECI): 0 bits
    /// - With ECI: 4 bits (ECI indicator) + 8 bits (assignment number) = 12 bits</returns>
    /// <remarks>
    /// Current implementation supports 0-127 range (8 bits).
    /// </remarks>
    internal static int GetStandardQrHeaderBits(this EciMode eciMode)
    {
        if (eciMode == EciMode.Default)
        {
            return 0; // No ECI header
        }

        // ECI mode indicator (4 bits) + ECI assignment number (8 bits for 0-127)
        return 4 + 8;
    }
}
