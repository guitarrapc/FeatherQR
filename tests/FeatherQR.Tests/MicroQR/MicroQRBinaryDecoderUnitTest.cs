using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Bitstream decoding: corrected Micro QR data codewords back to text. Golden
/// vectors are independent of the encoder (hand-derived per ISO/IEC 18004 rules);
/// encoder round-trips cover every version/ECC/mode/length combination; negative
/// cases cover malformed streams (invalid modes, out-of-range group values,
/// truncated segments) that must not decode.
/// </summary>
public class MicroQRBinaryDecoderUnitTest
{
    private static (DecodeStatus status, string text) Decode(byte[] data, int dataBitCount, MicroQRVersion version)
    {
        var destination = new char[64];
        var status = MicroQRBinaryDecoder.DecodeBitStream(data, dataBitCount, version, destination, out var charsWritten);
        return (status, new string(destination, 0, charsWritten));
    }

    [Test]
    [Arguments("99999", new byte[] { 0xBF, 0x3E, 0x30 })] // exactly fills 20 bits, no terminator
    [Arguments("9999", new byte[] { 0x9F, 0x3C, 0x80 })]  // terminator exactly fills capacity
    [Arguments("123", new byte[] { 0x63, 0xD8, 0x00 })]   // 011 + 0001111011 + 3-bit terminator + zeros (final 4-bit pad 0000)
    public async Task DecodeBitStream_M1GoldenVectors(string expected, byte[] codewords)
    {
        var (status, text) = Decode(codewords, dataBitCount: 20, MicroQRVersion.M1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task DecodeBitStream_M2L_IsoExample01234567()
    {
        // ISO/IEC 18004 Micro QR encoding example: "01234567" in M2-L.
        var (status, text) = Decode([0x40, 0x18, 0xAC, 0xC3, 0x00], dataBitCount: 40, MicroQRVersion.M2);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("01234567");
    }

    public static IEnumerable<(string text, MicroQRVersion version, MicroQREccLevel ecc)> RoundTripCases()
    {
        // Every version/ECC combination, every supported mode, boundary and
        // mid-fill lengths (padding paths: full capacity, terminator + pads).
        yield return ("1", MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return ("12345", MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return ("0123456789", MicroQRVersion.M2, MicroQREccLevel.L);
        yield return ("12345678", MicroQRVersion.M2, MicroQREccLevel.M);
        yield return ("AC-42", MicroQRVersion.M2, MicroQREccLevel.L);
        yield return ("HELLO", MicroQRVersion.M2, MicroQREccLevel.M);
        yield return ("12345678901234567890123", MicroQRVersion.M3, MicroQREccLevel.L);
        yield return ("HELLO WORLD 14", MicroQRVersion.M3, MicroQREccLevel.L);
        yield return ("byte hi", MicroQRVersion.M3, MicroQREccLevel.M);
        yield return ("x", MicroQRVersion.M3, MicroQREccLevel.L);
        yield return ("HELLO WORLD PLUS 21ST", MicroQRVersion.M4, MicroQREccLevel.L);
        yield return ("bytes m4 mode", MicroQRVersion.M4, MicroQREccLevel.M);
        yield return ("bytes!!!!", MicroQRVersion.M4, MicroQREccLevel.Q);
        yield return ("99999999999999999999999999999999999", MicroQRVersion.M4, MicroQREccLevel.L); // 35 digits = M4-L numeric capacity
        yield return ("fifteen bytes!!", MicroQRVersion.M4, MicroQREccLevel.L); // 15 bytes = M4-L byte capacity
        yield return ("Café au lait", MicroQRVersion.M4, MicroQREccLevel.L); // Latin-1 high byte
        yield return ("こんにちは", MicroQRVersion.M4, MicroQREccLevel.L); // UTF-8, 15 bytes
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task DecodeBitStream_EncoderRoundTrip(string text, MicroQRVersion version, MicroQREccLevel ecc)
    {
        var analysis = TextAnalyzer.Analyze(text.AsSpan(), EciMode.Default);
        var codewords = new byte[16];
        MicroQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), version, ecc, analysis.EncodingMode, codewords);

        var (status, decoded) = Decode(codewords, MicroQRConstants.GetDataBitCapacity(version, ecc), version);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(text);
    }

    // Kanji mode (decode only; M3 and M4 are the only versions that define it)

    /// <summary>Bit-string builder for hand-made streams (MSB first, zero-padded).</summary>
    private static byte[] Bits(string bits)
    {
        var clean = bits.Replace(" ", "");
        var result = new byte[(clean.Length + 7) / 8];
        for (var i = 0; i < clean.Length; i++)
            if (clean[i] == '1')
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    /// <summary>ISO/IEC 18004 8.4.5 compaction, rendered as a 13-bit string.</summary>
    private static string Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return Convert.ToString(((shifted >> 8) * 0xC0) + (shifted & 0xFF), 2).PadLeft(13, '0');
    }

    /// <summary>
    /// Decodes a hand-made stream with a capacity-SHAPED bound: the buffer is rounded up
    /// to whole codewords and <c>dataBitCount</c> is that length, minus 4 for M1 and M3,
    /// which end on a half codeword. It is not necessarily one of the version's real
    /// capacities -- these streams are shorter than any real symbol -- but it reproduces
    /// the relationship production has, which is what the guards depend on.
    /// </summary>
    /// <remarks>
    /// Passing the raw bit length as <c>dataBitCount</c> instead leaves the buffer
    /// LONGER than the capacity, which is the inverse of production and makes every
    /// truncation guard untestable: <see cref="BitReader"/> bounds itself on the buffer,
    /// so a decoder that skipped the capacity check would read the pad bits quietly
    /// rather than throwing. Two of these tests were written that way and passed
    /// against a decoder with the guard removed.
    /// </remarks>
    private static (DecodeStatus status, string text) DecodeAtCapacity(string stream, MicroQRVersion version)
    {
        var clean = stream.Replace(" ", "");
        var data = Bits(clean);
        var dataBitCount = data.Length * 8 - (version is MicroQRVersion.M1 or MicroQRVersion.M3 ? 4 : 0);
        if (clean.Length > dataBitCount)
            throw new ArgumentException($"{clean.Length}-bit stream does not fit the {dataBitCount}-bit bound this helper derives for {version}", nameof(stream));

        return Decode(data, dataBitCount, version);
    }

    /// <summary>M4 Kanji: 3-bit mode indicator 011, 4-bit count, 13 bits per character.</summary>
    [Test]
    public async Task DecodeBitStream_M4Kanji_DecodesToJisX0208()
    {
        var (status, text) = DecodeAtCapacity("011" + "0010" + Kanji(0x93FA) + Kanji(0x967B), MicroQRVersion.M4);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日本");
    }

    /// <summary>M3 Kanji: 2-bit mode indicator 11, 3-bit count.</summary>
    [Test]
    public async Task DecodeBitStream_M3Kanji_DecodesToJisX0208()
    {
        var (status, text) = DecodeAtCapacity("11" + "001" + Kanji(0x889F), MicroQRVersion.M3);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("亜");
    }

    [Test]
    public async Task DecodeBitStream_M4Kanji_CellOutsideJisX0208_ReportsUnmappedCharacter()
    {
        var (status, _) = DecodeAtCapacity("011" + "0001" + Kanji(0x8740), MicroQRVersion.M4); // NEC row 13, CP932-only

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    [Test]
    [Arguments("011", 4, MicroQRVersion.M4)]
    [Arguments("11", 3, MicroQRVersion.M3)]
    public async Task DecodeBitStream_KanjiStructurallyImpossibleCell_ReportsInvalidBitstream(string modeBits, int countBits, MicroQRVersion version)
    {
        // Low byte 0x3F would require Shift_JIS trail byte 0x7F, which does not exist.
        var stream = modeBits + Convert.ToString(1, 2).PadLeft(countBits, '0') + Convert.ToString(0x3F, 2).PadLeft(13, '0');
        var (status, _) = DecodeAtCapacity(stream, version);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A count naming more characters than the remaining bits can hold is a malformed
    /// stream, not a short buffer; the destination here is ample.
    /// </summary>
    [Test]
    public async Task DecodeBitStream_M4KanjiCountExceedsAvailableBits_ReportsInvalidBitstream()
    {
        var (status, _) = DecodeAtCapacity("011" + "1111" + Kanji(0x93FA), MicroQRVersion.M4); // count 15, one character present

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A Kanji mode indicator whose count field is cut off by the CAPACITY is a
    /// truncated segment, not a shortened terminator (only Numeric gets that reading).
    /// </summary>
    /// <remarks>
    /// The stream fills an M4 symbol exactly, so the 4-bit count field has only 2 bits
    /// of capacity left. A decoder that skipped the capacity check would read past
    /// <c>dataBitCount</c>, and because the buffer ends there too, <see cref="BitReader"/>
    /// throws <see cref="InvalidOperationException"/> straight out of a bool-returning
    /// <c>TryDecode</c>. That is the failure this test exists to prevent.
    /// </remarks>
    [Test]
    public async Task DecodeBitStream_M4KanjiTruncatedCountIndicator_ReportsInvalidBitstream()
    {
        // Numeric "0123456789" (3 + 6 + 34 bits), then a Kanji indicator with 2 bits left of 48.
        const string Digits = "0000001100" + "0101011001" + "1010100110" + "1001";
        var (status, _) = DecodeAtCapacity("000" + "001010" + Digits + "011" + "01", MicroQRVersion.M4);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A non-empty Kanji segment does not end the stream: the segment after it decodes
    /// too, and its characters land after the Kanji ones.
    /// </summary>
    /// <remarks>
    /// Micro QR is the only decoder where Kanji is handled by a bespoke branch outside
    /// the payload switch, so continuing correctly is the one thing that branch has to
    /// get right and nothing else covers. Turning its <c>continue</c> into a
    /// <c>break</c> — a decoder that silently drops everything after the first Kanji
    /// segment — passed the whole suite before this test existed.
    /// </remarks>
    [Test]
    public async Task DecodeBitStream_M4KanjiFollowedByAnotherSegment_ConcatenatesBoth()
    {
        var (status, text) = DecodeAtCapacity("011" + "0001" + Kanji(0x93FA) + "000" + "000011" + "0001111011", MicroQRVersion.M4);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日123");
    }

    /// <summary>
    /// A zero-count Kanji segment is empty, not a terminator: only Numeric ends the
    /// stream that way, so decoding continues into the following segment.
    /// </summary>
    [Test]
    public async Task DecodeBitStream_M4KanjiZeroCount_IsAnEmptySegmentAndDecodingContinues()
    {
        var (status, text) = DecodeAtCapacity("011" + "0000" + "000" + "000011" + "0001111011", MicroQRVersion.M4);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("123");
    }

    /// <summary>
    /// The decoder parses untrusted input, so no stream may make it throw. This is what
    /// protects every bounds guard in the loop, including ones no targeted test names;
    /// Standard QR has had the equivalent since its decoder shipped.
    /// </summary>
    [Test]
    public async Task DecodeBitStream_RandomGarbage_NeverThrows()
    {
        var random = new Random(20260823);
        var destination = new char[512];
        foreach (var version in Enum.GetValues<MicroQRVersion>())
        {
            for (var round = 0; round < 500; round++)
            {
                var data = new byte[random.Next(1, 20)];
                random.NextBytes(data);
                // Never above the buffer: a caller-supplied bit count past the buffer is
                // outside the contract, and the matrix decoder never produces one.
                var dataBitCount = random.Next(0, data.Length * 8 + 1);

                MicroQRBinaryDecoder.DecodeBitStream(data, dataBitCount, version, destination, out _);
            }
        }
    }

    /// <summary>
    /// M1 and M2 do not define Kanji mode, and their mode indicators (0 and 1 bit) can
    /// never express its value, so the accessor's guard is unreachable from the
    /// decoder. Pinning it here records that fact, so widening the mode handling
    /// without revisiting the guard shows up as a change in intent rather than silently.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M1)]
    [Arguments(MicroQRVersion.M2)]
    public async Task GetKanjiCountIndicatorLength_BelowM3_Throws(MicroQRVersion version)
    {
        await Assert.That(() => MicroQRConstants.GetKanjiCountIndicatorLength(version))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(MicroQRVersion.M3, 3)]
    [Arguments(MicroQRVersion.M4, 4)]
    public async Task GetKanjiCountIndicatorLength_MatchesTable3(MicroQRVersion version, int expected)
    {
        await Assert.That(MicroQRConstants.GetKanjiCountIndicatorLength(version)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(new byte[] { 0b100_00000, 0x00 })] // mode 100 (undefined)
    [Arguments(new byte[] { 0b111_00000, 0x00 })] // mode 111 (undefined)
    public async Task DecodeBitStream_UndefinedM4ModeIndicator_ReportsInvalidBitstream(byte[] codewords)
    {
        var (status, _) = Decode(codewords, dataBitCount: 80, MicroQRVersion.M4);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task DecodeBitStream_NumericGroupAboveRange_ReportsInvalidBitstream()
    {
        // M1: count = 3 (bits 011), then a 10-bit group of all ones (1023 > 999).
        // Stream: 011 1111111111 -> 0111 1111 1111 1000 -> 0x7F 0xF8
        var (status, _) = Decode([0x7F, 0xF8, 0x00], dataBitCount: 20, MicroQRVersion.M1);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task DecodeBitStream_AlphanumericPairAboveRange_ReportsInvalidBitstream()
    {
        // M2: mode 1 (alnum), count 2 (bits 010), 11-bit pair of all ones (2047 >= 45*45=2025).
        // Stream: 1 010 11111111111 -> 1010 1111 1111 1110 -> 0xAF 0xFE
        var (status, _) = Decode([0xAF, 0xFE, 0x00, 0x00, 0x00], dataBitCount: 40, MicroQRVersion.M2);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task DecodeBitStream_ByteSegmentOverrunsCapacity_ReportsInvalidBitstream()
    {
        // M3: mode 10 (byte), count 9 = 72 data bits needed, but M3-M capacity
        // after the 6 header bits leaves only 62.
        // Stream: 10 1001 ... -> 0b10_1001_00
        var (status, _) = Decode([0b10_1001_00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], dataBitCount: 68, MicroQRVersion.M3);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task DecodeBitStream_ZeroCountNonNumericSegment_DecodesAsEmpty()
    {
        // M2: mode 1 (alnum), count 0, an empty segment is not a terminator
        // (the terminator is numeric-mode-with-zero-count); the stream then ends.
        var (status, text) = Decode([0b1_000_0000, 0x00, 0x00, 0x00, 0x00], dataBitCount: 40, MicroQRVersion.M2);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("");
    }

    [Test]
    public async Task DecodeBitStream_DestinationTooSmall_ReportsStatus()
    {
        var codewords = new byte[16];
        MicroQRBinaryEncoder.EncodeDataCodewords("0123456789".AsSpan(), MicroQRVersion.M2, MicroQREccLevel.L, EncodingMode.Numeric, codewords);

        var destination = new char[4];
        var status = MicroQRBinaryDecoder.DecodeBitStream(codewords.AsSpan(0, 5), 40, MicroQRVersion.M2, destination, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task DecodeBitStream_HalfCodewordBitsBeyondCapacity_AreNeverRead()
    {
        // M3-L: 84 data bits = 10.5 codewords; the low nibble of the final
        // codeword is outside the data capacity. Fill it with garbage, the
        // decoder must ignore it.
        var codewords = new byte[16];
        var analysis = TextAnalyzer.Analyze("ABC".AsSpan(), EciMode.Default);
        MicroQRBinaryEncoder.EncodeDataCodewords("ABC".AsSpan(), MicroQRVersion.M3, MicroQREccLevel.L, analysis.EncodingMode, codewords);
        codewords[10] |= 0x0F; // garbage in the forced-zero low nibble

        var (status, text) = Decode(codewords, dataBitCount: 84, MicroQRVersion.M3);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("ABC");
    }
}
