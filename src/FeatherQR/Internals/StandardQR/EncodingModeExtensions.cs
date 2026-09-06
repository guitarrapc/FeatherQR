using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Standard QR helpers for <see cref="EncodingMode"/>.
/// The indicator widths encode ISO/IEC 18004 version thresholds (1-9 / 10-26 / 27-40); Micro QR and rMQR define their own width tables in their respective symbology namespaces.
/// </summary>
internal static class EncodingModeExtensions
{
    /// <summary>
    /// Character count indicator width for Kanji mode (ISO/IEC 18004 Table 3): 8 bits for versions 1-9, 10 for 10-26, 12 for 27-40.
    /// </summary>
    /// <remarks>
    /// Kanji sits outside <see cref="EncodingMode"/> because that enum names the modes the encoder writes, and this library reads Kanji without emitting it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetKanjiCountIndicatorLength(int version)
        => version < 10 ? 8 : version < 27 ? 10 : 12;

    /// <summary>
    /// Gets the bit length of character count indicator based on version and mode
    /// </summary>
    /// <param name="version">QR code version (1-40).</param>
    /// <param name="mode">Encoding mode.</param>
    /// <returns>Bit length (8-16 bits):
    /// - Version 1-9: Numeric=10, Alphanumeric=9, Byte=8
    /// - Version 10-26: Numeric=12, Alphanumeric=11, Byte=16
    /// - Version 27-40: Numeric=14, Alphanumeric=13, Byte=16</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCountIndicatorLength(this EncodingMode mode, int version)
    {
        if (version < 10)
        {
            return mode switch
            {
                EncodingMode.Numeric => 10,
                EncodingMode.Alphanumeric => 9,
                EncodingMode.Byte => 8,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), "Invalid encoding mode"),
            };
        }
        else if (version < 27)
        {
            return mode switch
            {
                EncodingMode.Numeric => 12,
                EncodingMode.Alphanumeric => 11,
                EncodingMode.Byte => 16,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), "Invalid encoding mode"),
            };
        }
        else
        {
            return mode switch
            {
                EncodingMode.Numeric => 14,
                EncodingMode.Alphanumeric => 13,
                EncodingMode.Byte => 16,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), "Invalid encoding mode"),
            };
        }
    }
}
