using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes the QR data bitstream (mode segments) back into text.
/// </summary>
/// <remarks>
/// Inverse of <see cref="QRBinaryEncoder"/>.
/// Supports the segments the encoder can produce, Numeric, Alphanumeric, Byte (ISO-8859-1 / UTF-8) and ECI headers, plus multi-segment streams from other encoders.
/// Kanji mode is decoded as JIS X 0208 (decode only: this library never emits it).
/// FNC1 and Structured Append are recognized but reported as <see cref="DecodeStatus.UnsupportedContent"/>.
/// <para>
/// Byte segments without an ECI header have no declared charset (ISO/IEC 18004 defaults to ISO-8859-1, but UTF-8 payloads are common in the wild).
/// The decoder uses UTF-8 when the payload validates as UTF-8 (or carries a BOM) and falls back to ISO-8859-1 otherwise, ASCII decodes identically either way.
/// </para>
/// <para>
/// Segment payload decoding (digit/character groups, byte charset resolution) is shared with the other symbology decoders via <see cref="SegmentDecoders"/>; this class owns the Standard QR framing: 4-bit mode indicators, version-dependent count indicator widths, and ECI headers.
/// </para>
/// </remarks>
internal static class QRBinaryDecoder
{
    // Mode indicators (ISO/IEC 18004 Table 2). EncodingMode covers the modes the
    // encoder writes; the remaining indicators are listed here for classification.
    private const int ModeTerminator = 0b0000;
    private const int ModeNumeric = 0b0001;
    private const int ModeAlphanumeric = 0b0010;
    private const int ModeStructuredAppend = 0b0011;
    private const int ModeByte = 0b0100;
    private const int ModeFnc1First = 0b0101;
    private const int ModeEci = 0b0111;
    private const int ModeKanji = 0b1000;
    private const int ModeFnc1Second = 0b1001;

    // ECI assignment numbers this decoder can map to a charset.
    private const int EciIso8859_1a = 1;  // ISO-8859-1 (historical assignment)
    private const int EciIso8859_1b = 3;  // ISO-8859-1
    private const int EciUtf8 = 26;       // UTF-8
    private const int EciAscii = 27;      // US-ASCII (subset of ISO-8859-1)

    /// <summary>
    /// Decodes the corrected data codewords into characters.
    /// </summary>
    /// <param name="data">Corrected data codewords (without ECC).</param>
    /// <param name="version">QR code version (1-40), determines character count indicator widths.</param>
    /// <param name="destination">Destination for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    public static DecodeStatus DecodeBitStream(ReadOnlySpan<byte> data, int version, Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        var reader = new BitReader(data);
        var totalBits = data.Length * 8;
        var charset = ByteSegmentCharset.Unspecified;

        byte[]? rentedBytes = null;
        try
        {
            while (true)
            {
                // Fewer than 4 bits left acts as an implicit terminator (ISO/IEC 18004 7.4.9)
                if (totalBits - reader.BitPosition < 4)
                    break;

                var mode = reader.Reads(4);
                if (mode == ModeTerminator)
                    break;

                switch (mode)
                {
                    case ModeNumeric:
                        {
                            var countBits = EncodingMode.Numeric.GetCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            var status = SegmentDecoders.DecodeNumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeAlphanumeric:
                        {
                            var countBits = EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            var status = SegmentDecoders.DecodeAlphanumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeByte:
                        {
                            var countBits = EncodingMode.Byte.GetCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);

                            // Segment payload bytes are staged in a rented buffer (reused
                            // across segments) so the charset conversion sees them whole.
                            rentedBytes ??= ArrayPool<byte>.Shared.Rent(data.Length);
                            var status = SegmentDecoders.DecodeBytePayload(ref reader, totalBits, count, charset, rentedBytes, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeEci:
                        {
                            var status = SegmentDecoders.ReadEciDesignator(ref reader, totalBits, out var eciValue);
                            if (status != DecodeStatus.Success)
                                return status;
                            switch (eciValue)
                            {
                                case EciIso8859_1a:
                                case EciIso8859_1b:
                                case EciAscii:
                                    charset = ByteSegmentCharset.Iso8859_1;
                                    break;
                                case EciUtf8:
                                    charset = ByteSegmentCharset.Utf8;
                                    break;
                                default:
                                    return DecodeStatus.UnsupportedContent;
                            }
                            break;
                        }
                    case ModeKanji:
                        {
                            var countBits = EncodingModeExtensions.GetKanjiCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            var status = SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeStructuredAppend:
                    case ModeFnc1First:
                    case ModeFnc1Second:
                        return DecodeStatus.UnsupportedContent;
                    default:
                        return DecodeStatus.InvalidBitstream;
                }
            }

            return DecodeStatus.Success;
        }
        finally
        {
            if (rentedBytes is not null)
                ArrayPool<byte>.Shared.Return(rentedBytes, clearArray: false);
        }
    }
}
