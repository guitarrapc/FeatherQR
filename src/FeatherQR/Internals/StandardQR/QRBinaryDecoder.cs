using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes the QR data bitstream (mode segments) back into text.
/// </summary>
/// <remarks>
/// Inverse of <see cref="QRBinaryEncoder"/>.
/// Supports the segments the encoder can produce, Numeric, Alphanumeric, Byte (ISO-8859-1 / UTF-8), Kanji and ECI headers, plus multi-segment streams and Shift_JIS Byte segments from other encoders.
/// Kanji mode is decoded as JIS X 0208, the mapping the encoder writes it with (with <c>AllowKanji</c>).
/// A Structured Append header and either FNC1 indicator (ISO/IEC 18004 6.4.8) are read and returned to the caller.
/// FNC1 is accepted once and before the first data segment, with the ECI and Structured Append headers on either side of it. An indicator after a data segment, a second one, or an application indicator outside 00-99 and the letters is <see cref="DecodeStatus.InvalidBitstream"/>.
/// While FNC1 is active a lone <c>%</c> in an Alphanumeric segment is the separator (U+001D) and <c>%%</c> is one <c>%</c>, read left to right within the segment, as every reader probed reads them. Byte, Numeric and Kanji segments are unchanged.
/// <para>
/// Byte segments without an ECI header have no declared charset (ISO/IEC 18004 defaults to ISO-8859-1, but UTF-8 payloads are common in the wild).
/// The decoder uses UTF-8 when the payload validates as UTF-8 (or carries a BOM), Shift_JIS when the bytes give it away, and falls back to ISO-8859-1 otherwise, ASCII decodes identically either way.
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
    private const int EciShiftJis = 20;   // Shift_JIS
    private const int EciUtf8 = 26;       // UTF-8
    private const int EciAscii = 27;      // US-ASCII (subset of ISO-8859-1)

    /// <summary>
    /// Decodes the corrected data codewords into characters.
    /// </summary>
    /// <param name="data">Corrected data codewords (without ECC).</param>
    /// <param name="version">QR code version (1-40), determines character count indicator widths.</param>
    /// <param name="destination">Destination for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="structuredAppend">The Structured Append header when the stream carries one, accepted at any position; default otherwise.</param>
    /// <param name="fnc1">The FNC1 mode when the stream carries one, with its application indicator in second position; default otherwise.</param>
    public static DecodeStatus DecodeBitStream(ReadOnlySpan<byte> data, int version, Span<char> destination, out int charsWritten, out QRStructuredAppend structuredAppend, out Fnc1Mode fnc1)
    {
        charsWritten = 0;
        structuredAppend = default;
        fnc1 = default;
        var reader = new BitReader(data);
        var totalBits = data.Length * 8;
        var charset = ByteSegmentCharset.Unspecified;
        // FNC1 precedes the first data segment (6.4.8.1, 6.4.8.2): the mode changes how every
        // Alphanumeric segment reads, so one that arrives later would re-read what came before.
        var dataSegmentSeen = false;

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
                            dataSegmentSeen = true;
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
                            dataSegmentSeen = true;
                            var status = fnc1.IsActive
                                ? SegmentDecoders.DecodeAlphanumericPayloadFnc1(ref reader, totalBits, count, destination, ref charsWritten)
                                : SegmentDecoders.DecodeAlphanumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
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
                            dataSegmentSeen = true;

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
                                case EciShiftJis:
                                    charset = ByteSegmentCharset.ShiftJis;
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
                            dataSegmentSeen = true;
                            var status = SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeStructuredAppend:
                        {
                            // 4-bit position, 4-bit count-1, 8-bit parity (ISO/IEC 18004 Structured Append).
                            // One header per stream, and a position past the count is not encodable by
                            // a conforming encoder, so both are malformed rather than unsupported.
                            if (totalBits - reader.BitPosition < 16 || !structuredAppend.IsEmpty)
                                return DecodeStatus.InvalidBitstream;
                            var index = reader.Reads(4);
                            var count = reader.Reads(4) + 1;
                            var parity = (byte)reader.Reads(8);
                            if (index >= count)
                                return DecodeStatus.InvalidBitstream;
                            structuredAppend = new QRStructuredAppend(index, count, parity);
                            break;
                        }
                    case ModeFnc1First:
                        // 6.4.8.1: once, before the first data segment. No conforming encoder writes it
                        // later, and a late one would change how the segments before it read.
                        if (dataSegmentSeen || fnc1.IsActive)
                            return DecodeStatus.InvalidBitstream;
                        fnc1 = Fnc1Mode.FirstPosition;
                        break;
                    case ModeFnc1Second:
                        {
                            // 6.4.8.2: the same placement, then one 8-bit application indicator: 00-99
                            // as its value, or a letter as its ASCII value plus 100. Anything else is
                            // malformed, as a count that overruns the stream is.
                            if (dataSegmentSeen || fnc1.IsActive || totalBits - reader.BitPosition < 8)
                                return DecodeStatus.InvalidBitstream;
                            var indicator = reader.Reads(8);
                            if (!Fnc1Mode.IsValidApplicationIndicator(indicator))
                                return DecodeStatus.InvalidBitstream;
                            fnc1 = Fnc1Mode.FromCodeword(indicator);
                            break;
                        }
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
