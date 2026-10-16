using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// rMQR bit-stream decoder (ISO/IEC 23941 7.4): 3-bit mode indicators, per-version character count indicator widths, terminator <c>000</c> (possibly shortened at capacity), ECI segments (parsed, mapped to the charsets the shared byte decoder knows), Kanji decoded as JIS X 0208 via the shared <see cref="ShiftJisKanjiTable"/>, and both FNC1 indicators (7.4.8) under the Standard QR rules: once, before the first data segment, with ECI headers on either side, and a lone <c>%</c> in an Alphanumeric segment read as the separator.
/// Segment payloads decode through the shared <see cref="SegmentDecoders"/>.
/// </summary>
internal static class RmQRBinaryDecoder
{
    private const int ModeTerminator = 0b000;
    private const int ModeNumeric = 0b001;
    private const int ModeAlphanumeric = 0b010;
    private const int ModeByte = 0b011;
    private const int ModeKanji = 0b100;
    // FNC1 in first and second position, once taken for reserved here: the evidence is in rmqr-decoder.md
    private const int ModeFnc1First = 0b101;
    private const int ModeFnc1Second = 0b110;
    private const int ModeEci = 0b111;

    // ECI assignment numbers the shared byte decoder can map to a charset.
    private const int EciIso8859_1a = 1;
    private const int EciIso8859_1b = 3;
    private const int EciShiftJis = 20;
    private const int EciUtf8 = 26;
    private const int EciAscii = 27;

    /// <summary>
    /// Decodes the corrected data codewords into characters.
    /// </summary>
    /// <param name="data">Corrected data codewords (without ECC).</param>
    /// <param name="dataBitCount">The number of data bits to read: 8 times the data codewords on the matrix path, since rMQR places its remainder bits after whole codewords. A smaller count ends the stream early, which the tests use.</param>
    /// <param name="version">The rMQR version, which sets the character count indicator widths.</param>
    /// <param name="destination">Destination for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="fnc1">The FNC1 mode when the stream carries one, with its application indicator in second position, and default otherwise.</param>
    public static DecodeStatus DecodeBitStream(ReadOnlySpan<byte> data, int dataBitCount, RmQRVersion version, Span<char> destination, out int charsWritten, out Fnc1Mode fnc1)
    {
        charsWritten = 0;
        fnc1 = default;
        var reader = new BitReader(data);
        var totalBits = dataBitCount;
        var charset = ByteSegmentCharset.Unspecified;
        // FNC1 precedes the first data segment, as in Standard QR (see QRBinaryDecoder).
        var dataSegmentSeen = false;

        byte[]? rentedBytes = null;
        try
        {
            while (true)
            {
                // Fewer than 3 bits left: the terminator was shortened away at capacity.
                if (totalBits - reader.BitPosition < RmQRConstants.ModeIndicatorLength)
                    break;

                var modeValue = reader.Reads(RmQRConstants.ModeIndicatorLength);
                if (modeValue == ModeTerminator)
                    break;

                switch (modeValue)
                {
                    case ModeNumeric:
                    case ModeAlphanumeric:
                    case ModeByte:
                        {
                            var mode = modeValue switch
                            {
                                ModeNumeric => EncodingMode.Numeric,
                                ModeAlphanumeric => EncodingMode.Alphanumeric,
                                _ => EncodingMode.Byte,
                            };
                            var countBits = RmQRConstants.GetCountIndicatorLength(version, mode);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            dataSegmentSeen = true;
                            if (count == 0)
                                continue; // empty segment (the encoder emits one only for empty text)

                            DecodeStatus status;
                            switch (mode)
                            {
                                case EncodingMode.Numeric:
                                    status = SegmentDecoders.DecodeNumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                                    break;
                                case EncodingMode.Alphanumeric:
                                    status = fnc1.IsActive
                                        ? SegmentDecoders.DecodeAlphanumericPayloadFnc1(ref reader, totalBits, count, destination, ref charsWritten)
                                        : SegmentDecoders.DecodeAlphanumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                                    break;
                                default:
                                    // Data codewords top out at 152 bytes (R17x139-M).
                                    rentedBytes ??= ArrayPool<byte>.Shared.Rent(data.Length);
                                    status = SegmentDecoders.DecodeBytePayload(ref reader, totalBits, count, charset, fnc1.IsActive, rentedBytes, destination, ref charsWritten);
                                    break;
                            }
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
                            var countBits = RmQRConstants.GetKanjiCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return DecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            dataSegmentSeen = true;
                            var status = SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != DecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeFnc1First:
                        // Once, before the first data segment: a late indicator would change how the
                        // segments before it read, and no conforming encoder writes one.
                        if (dataSegmentSeen || fnc1.IsActive)
                            return DecodeStatus.InvalidBitstream;
                        fnc1 = Fnc1Mode.FirstPosition;
                        break;
                    case ModeFnc1Second:
                        {
                            // The same placement, then one 8-bit application indicator in the ranges
                            // of ISO/IEC 18004 6.4.8.2. Anything else is malformed.
                            if (dataSegmentSeen || fnc1.IsActive || totalBits - reader.BitPosition < 8)
                                return DecodeStatus.InvalidBitstream;
                            var indicator = reader.Reads(8);
                            if (!Fnc1Mode.IsValidApplicationIndicator(indicator))
                                return DecodeStatus.InvalidBitstream;
                            fnc1 = Fnc1Mode.FromCodeword(indicator);
                            break;
                        }
                    default:
                        return DecodeStatus.InvalidBitstream; // no other 3-bit value is assigned
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
