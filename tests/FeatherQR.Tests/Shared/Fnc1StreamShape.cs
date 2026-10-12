using FeatherQR.Internals;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// What a committed FNC1 fixture's bit stream holds, read from its module matrix without the production
/// bit stream decoders: the codewords come from the reference extraction (<see cref="QRMatrixDecoder.ExtractCodewordsReference"/>
/// for Standard QR, <see cref="RmQRNaiveReference"/> for rMQR), and a walk of the segment framing records the headers
/// in order, with an FNC1 indicator required before the first data segment, and the data modes with the two characters
/// the FNC1 rules turn on.
/// Neither the writers nor the zxing-cpp gate report these, so the corpus-shape tests read them here.
/// </summary>
/// <param name="Headers">The headers in stream order: <c>FNC1First</c>, <c>FNC1Second:n</c> (the codeword), <c>ECI:n</c> (the assignment) or <c>SA</c>. An ECI or Structured Append header may follow a data segment (a charset switch, or a late header the decoder reads), an FNC1 indicator may not.</param>
/// <param name="DataModes">The data segments' modes in stream order: <c>Numeric</c>, <c>Alphanumeric</c>, <c>Byte</c> or <c>Kanji</c>.</param>
/// <param name="AlphanumericHasPercent">An Alphanumeric segment holds a <c>%</c> (value 38), the separator or half of a literal under FNC1.</param>
/// <param name="ByteHasGs">A Byte segment holds the byte 0x1D, the separator under FNC1.</param>
internal sealed record Fnc1StreamShape(IReadOnlyList<string> Headers, IReadOnlyList<string> DataModes, bool AlphanumericHasPercent, bool ByteHasGs)
{
    public static Fnc1StreamShape OfStandardQr(byte[] modules, int size, int version, QREccLevel eccLevel, int maskPattern)
    {
        var eccInfo = QRCodeConstants.GetEccInfo(version, eccLevel);
        var totalCodewords = eccInfo.TotalDataCodewords + (eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock;
        var layout = ModulePlacer.GetLayout(version);
        var interleaved = new byte[totalCodewords];
        QRMatrixDecoder.ExtractCodewordsReference(modules, size, layout.BlockedMask, maskPattern, interleaved);
        var blocks = new byte[totalCodewords];
        if (!EccBlockDecoder.TryCorrect(interleaved, blocks, eccInfo, eccInfo.ECCPerBlock / 2, out _))
            throw new InvalidOperationException("The fixture's codewords are not correctable, so its stream cannot be read.");
        return Walk(interleaved.AsSpan(0, eccInfo.TotalDataCodewords), eccInfo.TotalDataCodewords * 8, modeBits: 4, StandardQrToken,
            mode => mode == EncodingMode.Kanji ? EncodingModeExtensions.GetKanjiCountIndicatorLength(version) : mode.GetCountIndicatorLength(version));
    }

    public static Fnc1StreamShape OfRmQr(byte[] modules, int width, int height, RmQRVersion version, RmQREccLevel eccLevel)
    {
        var eccInfo = RmQRConstants.GetEccInfo(version, eccLevel);
        var stream = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out _);
        var data = RmQRNaiveReference.DeinterleaveData(stream, eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2, eccInfo.BlocksInGroup1, eccInfo.CodewordsInGroup1);
        return Walk(data.AsSpan(0, eccInfo.TotalDataCodewords), eccInfo.TotalDataCodewords * 8, modeBits: 3, RmQrToken,
            mode => mode == EncodingMode.Kanji ? RmQRConstants.GetKanjiCountIndicatorLength(version) : RmQRConstants.GetCountIndicatorLength(version, mode));
    }

    // ISO/IEC 18004 Table 2 and ISO/IEC 23941 Table 2, as tokens.
    private static string StandardQrToken(int indicator) => indicator switch
    {
        0b0001 => "Numeric",
        0b0010 => "Alphanumeric",
        0b0011 => "SA",
        0b0100 => "Byte",
        0b0101 => "FNC1First",
        0b0111 => "ECI",
        0b1000 => "Kanji",
        0b1001 => "FNC1Second",
        _ => throw new InvalidOperationException($"Unassigned Standard QR mode indicator {indicator}."),
    };

    private static string RmQrToken(int indicator) => indicator switch
    {
        0b001 => "Numeric",
        0b010 => "Alphanumeric",
        0b011 => "Byte",
        0b100 => "Kanji",
        0b101 => "FNC1First",
        0b110 => "FNC1Second",
        0b111 => "ECI",
        _ => throw new InvalidOperationException($"Unassigned rMQR mode indicator {indicator}."),
    };

    private static Fnc1StreamShape Walk(ReadOnlySpan<byte> data, int totalBits, int modeBits, Func<int, string> token, Func<EncodingMode, int> countBits)
    {
        var reader = new BitReader(data);
        var headers = new List<string>();
        var modes = new List<string>();
        var percent = false;
        var gs = false;

        while (totalBits - reader.BitPosition >= modeBits)
        {
            var indicator = reader.Reads(modeBits);
            if (indicator == 0)
                break; // terminator

            var name = token(indicator);
            switch (name)
            {
                case "FNC1First":
                    AddHeader(headers, modes, name);
                    break;
                case "FNC1Second":
                    AddHeader(headers, modes, $"{name}:{reader.Reads(8)}");
                    break;
                case "SA":
                    reader.Reads(16);
                    AddHeader(headers, modes, name);
                    break;
                case "ECI":
                    {
                        if (SegmentDecoders.ReadEciDesignator(ref reader, totalBits, out var assignment) != DecodeStatus.Success)
                            throw new InvalidOperationException("Malformed ECI designator in the fixture stream.");
                        AddHeader(headers, modes, $"{name}:{assignment}");
                        break;
                    }
                default:
                    {
                        var mode = name switch
                        {
                            "Numeric" => EncodingMode.Numeric,
                            "Alphanumeric" => EncodingMode.Alphanumeric,
                            "Byte" => EncodingMode.Byte,
                            _ => EncodingMode.Kanji,
                        };
                        var count = reader.Reads(countBits(mode));
                        modes.Add(name);
                        switch (mode)
                        {
                            case EncodingMode.Numeric:
                                for (; count >= 3; count -= 3)
                                    reader.Reads(10);
                                if (count == 2)
                                    reader.Reads(7);
                                else if (count == 1)
                                    reader.Reads(4);
                                break;
                            case EncodingMode.Alphanumeric:
                                for (; count >= 2; count -= 2)
                                {
                                    var pair = reader.Reads(11);
                                    percent |= pair / 45 == 38 || pair % 45 == 38;
                                }
                                if (count == 1)
                                    percent |= reader.Reads(6) == 38;
                                break;
                            case EncodingMode.Byte:
                                for (var i = 0; i < count; i++)
                                    gs |= reader.Reads(8) == 0x1D;
                                break;
                            default:
                                for (var i = 0; i < count; i++)
                                    reader.Reads(13);
                                break;
                        }
                        break;
                    }
            }
        }

        return new Fnc1StreamShape(headers, modes, percent, gs);
    }

    private static void AddHeader(List<string> headers, List<string> modes, string header)
    {
        // FNC1 belongs before the first data segment, since the decoder refuses it later. The other two headers may sit anywhere.
        if (modes.Count > 0 && header.StartsWith("FNC1", StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixture stream has the indicator {header} after a data segment.");
        headers.Add(header);
    }
}
