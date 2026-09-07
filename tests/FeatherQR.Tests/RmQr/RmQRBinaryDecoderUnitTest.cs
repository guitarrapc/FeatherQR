using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR bit-stream decoding (ISO/IEC 23941 7.4): 3-bit mode indicators, per-version
/// count widths, terminator, ECI segments (parsed even though the encoder never
/// emits them), Kanji decoded as JIS X 0208, malformed streams rejected.
/// </summary>
public class RmQRBinaryDecoderUnitTest
{
    private static (DecodeStatus Status, string Text) Decode(byte[] data, RmQRVersion version)
    {
        var destination = new char[RmQRMatrixDecoder.GetMaxCharCount(version)];
        var status = RmQRBinaryDecoder.DecodeBitStream(data, data.Length * 8, version, destination, out var written);
        return (status, new string(destination, 0, written));
    }

    private static byte[] Encode(string text, RmQRVersion version, RmQREccLevel ecc)
    {
        var analysis = TextAnalyzer.Analyze(text.AsSpan(), EciMode.Default);
        var data = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), version, ecc, in analysis, data);
        return data;
    }

    /// <summary>Bit-string builder for hand-made streams (MSB first, zero-padded to the codeword count).</summary>
    private static byte[] Bits(string bits, int codewords)
    {
        var clean = bits.Replace(" ", "");
        var result = new byte[codewords];
        for (var i = 0; i < clean.Length; i++)
            if (clean[i] == '1')
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    [Test]
    public async Task Decode_OracleGolden_R7x43M_Numeric1()
    {
        var (status, text) = Decode([0x22, 0x20, 0xEC, 0x11, 0xEC, 0x11], RmQRVersion.R7x43);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("1");
    }

    [Test]
    [Arguments("012345678901", RmQRVersion.R7x43, RmQREccLevel.M)]
    [Arguments("ABCDEF $%*+-./:", RmQRVersion.R7x59, RmQREccLevel.M)]
    [Arguments("hello, rMQR!", RmQRVersion.R11x59, RmQREccLevel.H)]
    [Arguments("naïve café", RmQRVersion.R11x59, RmQREccLevel.M)]
    [Arguments("こんにちは世界", RmQRVersion.R13x59, RmQREccLevel.M)]
    [Arguments("", RmQRVersion.R7x43, RmQREccLevel.H)]
    [Arguments("😀🎉", RmQRVersion.R9x77, RmQREccLevel.M)]
    public async Task Decode_EncoderRoundTrip(string text, RmQRVersion version, RmQREccLevel ecc)
    {
        var (status, decoded) = Decode(Encode(text, version, ecc), version);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(text);
    }

    [Test]
    public async Task Decode_ExactCapacity_NoTerminator_Succeeds()
    {
        // R7x43-H byte "ab": 3 + 3 + 16 = 22 bits + 2-bit terminator, no padding.
        var (status, decoded) = Decode(Encode("ab", RmQRVersion.R7x43, RmQREccLevel.H), RmQRVersion.R7x43);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo("ab");
        // 12 digits at R7x43-M: 47 of 48 bits used, 1-bit terminator.
        var (status2, decoded2) = Decode(Encode("999999999999", RmQRVersion.R7x43, RmQREccLevel.M), RmQRVersion.R7x43);
        await Assert.That(status2).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded2).IsEqualTo("999999999999");
    }

    [Test]
    public async Task Decode_EciUtf8Segment_IsParsed()
    {
        // 111 (ECI) + 00011010 (26 = UTF-8) + 011 (byte) + count 3 bits = 2 + "é" as UTF-8 C3 A9 + terminator, R7x43-M (6 codewords).
        var data = Bits("111 00011010 011 010 11000011 10101001 000", 6);
        var (status, text) = Decode(data, RmQRVersion.R7x43);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("é");
    }

    [Test]
    public async Task Decode_EciIso88591Segment_IsParsed()
    {
        // ECI 3 = ISO-8859-1, then byte "é" = E9.
        var data = Bits("111 00000011 011 001 11101001 000", 6);
        var (status, text) = Decode(data, RmQRVersion.R7x43);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("é");
    }

    [Test]
    public async Task Decode_UnsupportedEci_ReportsUnsupportedContent()
    {
        var data = Bits("111 00010100 011 001 01000001 000", 6); // ECI 20 (Shift JIS): not mapped
        await Assert.That(Decode(data, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.UnsupportedContent);
    }

    // Kanji mode (decode only; the rMQR encoder never emits it)

    /// <summary>ISO/IEC 18004 8.4.5 compaction, rendered as a 13-bit string.</summary>
    private static string Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return Convert.ToString(((shifted >> 8) * 0xC0) + (shifted & 0xFF), 2).PadLeft(13, '0');
    }

    /// <summary>R7x43 Kanji: 3-bit mode indicator 100, 2-bit count, 13 bits per character.</summary>
    [Test]
    public async Task Decode_Kanji_DecodesToJisX0208()
    {
        var data = Bits("100 10 " + Kanji(0x93FA) + Kanji(0x967B) + " 000", 6);
        var (status, text) = Decode(data, RmQRVersion.R7x43);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日本");
    }

    /// <summary>A wider version uses a wider count indicator; R13x139 Kanji is 7 bits.</summary>
    [Test]
    public async Task Decode_Kanji_UsesThePerVersionCountWidth()
    {
        var data = Bits("100 0000001 " + Kanji(0x8A45) + " 000", 20);
        var (status, text) = Decode(data, RmQRVersion.R13x139);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("界");
    }

    /// <summary>
    /// A non-empty Kanji segment does not end the stream: the segment after it decodes
    /// too, and its characters land after the Kanji ones.
    /// </summary>
    [Test]
    public async Task Decode_KanjiFollowedByAnotherSegment_ConcatenatesBoth()
    {
        // R7x43: Kanji count 2 bits, Numeric count 4 bits.
        var data = Bits("100 01 " + Kanji(0x93FA) + " 001 0011 0001111011 000", 6);
        var (status, text) = Decode(data, RmQRVersion.R7x43);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日123");
    }

    [Test]
    public async Task Decode_KanjiCellOutsideJisX0208_ReportsUnmappedCharacter()
    {
        var data = Bits("100 01 " + Kanji(0x8740) + " 000", 6); // NEC row 13, CP932-only
        await Assert.That(Decode(data, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    [Test]
    public async Task Decode_KanjiStructurallyImpossibleCell_ReportsInvalidBitstream()
    {
        var data = Bits("100 01 0000000111111 000", 6); // low byte 0x3F: no such Shift_JIS trail byte
        await Assert.That(Decode(data, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A zero-count Kanji segment is empty, not a terminator: decoding continues into
    /// the segment after it. Only mode <c>000</c> ends an rMQR stream.
    /// </summary>
    [Test]
    public async Task Decode_KanjiZeroCount_IsAnEmptySegmentAndDecodingContinues()
    {
        var data = Bits("100 00 001 0001 0111 000", 6); // empty Kanji, then Numeric "7"
        var (status, text) = Decode(data, RmQRVersion.R7x43);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
    }

    /// <summary>
    /// A Kanji mode indicator whose count field is cut off by the capacity is a
    /// truncated segment, and must be reported as such.
    /// </summary>
    /// <remarks>
    /// The matrix decoder passes <c>TotalDataCodewords * 8</c> as the bit count over a
    /// buffer of exactly that many codewords, so the capacity and the buffer end
    /// together. Drop the guard and <see cref="BitReader"/> reads past both, throwing
    /// <see cref="InvalidOperationException"/> out of a bool-returning <c>TryDecode</c>.
    /// </remarks>
    [Test]
    public async Task Decode_KanjiTruncatedCountIndicator_ReportsInvalidBitstream()
    {
        // R7x43-M holds 48 bits. Nine empty Kanji segments (3-bit mode + 2-bit count)
        // consume 45, leaving a Kanji mode indicator with 0 bits for its count.
        var data = Bits(string.Concat(Enumerable.Repeat("100 00 ", 9)) + "100", 6);

        await Assert.That(Decode(data, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// The same guard from the near side: one bit short, not all of them. Pinning only
    /// the zero-bits case lets the guard be weakened by one bit unnoticed, and a
    /// one-bit overrun is exactly what makes <see cref="BitReader"/> throw.
    /// </summary>
    [Test]
    public async Task Decode_KanjiCountIndicatorOneBitShort_ReportsInvalidBitstream()
    {
        // Three Kanji characters (3 + 2 + 39 = 44 bits of 48), then a Kanji mode
        // indicator at bit 44, leaving 1 of the 2 count bits.
        var data = Bits("100 11 " + Kanji(0x93FA) + Kanji(0x967B) + Kanji(0x8CEA) + " 100", 6);

        await Assert.That(Decode(data, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// The decoder parses untrusted input, so no stream may make it throw. This is what
    /// protects every bounds guard in the loop, including ones no targeted test names;
    /// Standard QR has had the equivalent since its decoder shipped.
    /// </summary>
    [Test]
    public async Task Decode_RandomGarbage_NeverThrows()
    {
        var random = new Random(20260823);
        var destination = new char[512];
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            for (var round = 0; round < 200; round++)
            {
                var data = new byte[random.Next(1, 40)];
                random.NextBytes(data);
                // Never above the buffer: a caller-supplied bit count past the buffer is
                // outside the contract, and the matrix decoder never produces one.
                var dataBitCount = random.Next(0, data.Length * 8 + 1);

                RmQRBinaryDecoder.DecodeBitStream(data, dataBitCount, version, destination, out _);
            }
        }
    }

    [Test]
    public async Task Decode_ReservedModes_AreInvalidBitstream()
    {
        await Assert.That(Decode(Bits("101 0000", 6), RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
        await Assert.That(Decode(Bits("110 0000", 6), RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Decode_TruncatedCountOrPayload_IsInvalidBitstream()
    {
        // Numeric mode indicator at bit 45 of 48 (after a byte segment and four empty numeric
        // segments that consume bits without terminating): its 4-bit count does not fit.
        var truncatedCount = Bits("011 001 01000001 0010000 0010000 0010000 0010000 001", 6);
        await Assert.That(Decode(truncatedCount, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
        // Byte segment claiming more bytes than the stream holds.
        var truncatedPayload = Bits("011 111 01000001 01000010", 6); // claims 7 bytes (56 bits) with 42 bits left
        await Assert.That(Decode(truncatedPayload, RmQRVersion.R7x43).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Decode_TerminatorAndPadding_EndTheStream()
    {
        // 011 (byte) count 1 'A' then 000 terminator, then arbitrary pad garbage must be ignored.
        var data = Bits("011 001 01000001 000 11111111 10101010 11001100", 6);
        var (status, text) = Decode(data, RmQRVersion.R7x43);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A");
        // Empty-count segments are skipped, not terminators (only 000 ends the stream).
        var empty = Bits("001 0000 011 001 01000010 000", 6);
        await Assert.That(Decode(empty, RmQRVersion.R7x43)).IsEqualTo((DecodeStatus.Success, "B"));
    }
}
