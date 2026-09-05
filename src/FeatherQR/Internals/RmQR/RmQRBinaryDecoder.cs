using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR bit-stream decoder (ISO/IEC 23941 7.4): 3-bit mode indicators, per-version
/// character count indicator widths, terminator <c>000</c> (possibly shortened at
/// capacity), ECI segments (parsed, mapped to the charsets the shared byte decoder
/// knows), Kanji decoded as JIS X 0208 via the shared <see cref="ShiftJisKanjiTable"/>.
/// Segment payloads decode through the shared <see cref="SegmentDecoders"/>.
/// </summary>
internal static class RmQRBinaryDecoder
{
    private const int ModeTerminator = 0b000;
    private const int ModeNumeric = 0b001;
    private const int ModeAlphanumeric = 0b010;
    private const int ModeByte = 0b011;
    private const int ModeKanji = 0b100;
    private const int ModeEci = 0b111;

    // ECI assignment numbers the shared byte decoder can map to a charset.
    private const int EciIso8859_1a = 1;
    private const int EciIso8859_1b = 3;
    private const int EciUtf8 = 26;
    private const int EciAscii = 27;

    public static QRCodeDecodeStatus DecodeBitStream(ReadOnlySpan<byte> data, int dataBitCount, RmQRVersion version, Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        var reader = new BitReader(data);
        var totalBits = dataBitCount;
        var charset = ByteSegmentCharset.Unspecified;

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
                                return QRCodeDecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            if (count == 0)
                                continue; // empty segment (the encoder emits one only for empty text)

                            QRCodeDecodeStatus status;
                            switch (mode)
                            {
                                case EncodingMode.Numeric:
                                    status = SegmentDecoders.DecodeNumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                                    break;
                                case EncodingMode.Alphanumeric:
                                    status = SegmentDecoders.DecodeAlphanumericPayload(ref reader, totalBits, count, destination, ref charsWritten);
                                    break;
                                default:
                                    // Data codewords top out at 152 bytes (R17x139-M).
                                    rentedBytes ??= ArrayPool<byte>.Shared.Rent(data.Length);
                                    status = SegmentDecoders.DecodeBytePayload(ref reader, totalBits, count, charset, rentedBytes, destination, ref charsWritten);
                                    break;
                            }
                            if (status != QRCodeDecodeStatus.Success)
                                return status;
                            break;
                        }
                    case ModeEci:
                        {
                            var status = SegmentDecoders.ReadEciDesignator(ref reader, totalBits, out var eciValue);
                            if (status != QRCodeDecodeStatus.Success)
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
                                    return QRCodeDecodeStatus.UnsupportedContent;
                            }
                            break;
                        }
                    case ModeKanji:
                        {
                            var countBits = RmQRConstants.GetKanjiCountIndicatorLength(version);
                            if (totalBits - reader.BitPosition < countBits)
                                return QRCodeDecodeStatus.InvalidBitstream;
                            var count = reader.Reads(countBits);
                            var status = SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits, count, destination, ref charsWritten);
                            if (status != QRCodeDecodeStatus.Success)
                                return status;
                            break;
                        }
                    default:
                        return QRCodeDecodeStatus.InvalidBitstream; // 101, 110 are reserved
                }
            }

            return QRCodeDecodeStatus.Success;
        }
        finally
        {
            if (rentedBytes is not null)
                ArrayPool<byte>.Shared.Return(rentedBytes, clearArray: false);
        }
    }
}
