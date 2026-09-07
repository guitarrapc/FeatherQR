using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// Branch-level tests for the bitstream decoder using handcrafted segment streams
/// (version 1 count-indicator widths). Covers the decision branches that
/// encoder-round-trip tests cannot reach: multi-segment streams, unsupported modes,
/// invalid segment values, ECI designator forms, and truncation. The decoder parses
/// untrusted input, so negative cases outnumber positive ones.
/// </summary>
public class QRBinaryDecoderUnitTest
{
    private const int Version = 1;

    // Mode indicator constants (ISO/IEC 18004 Table 2)
    private const int ModeNumeric = 0b0001;
    private const int ModeAlphanumeric = 0b0010;
    private const int ModeStructuredAppend = 0b0011;
    private const int ModeByte = 0b0100;
    private const int ModeFnc1First = 0b0101;
    private const int ModeEci = 0b0111;
    private const int ModeKanji = 0b1000;
    private const int ModeFnc1Second = 0b1001;
    private const int ModeTerminator = 0b0000;

    // Positive cases

    [Test]
    public async Task MultiSegment_NumericAlphanumericByte_ConcatenatesInOrder()
    {
        // "12" (numeric) + "A" (alphanumeric) + "!" (byte) + terminator
        var data = Build(
            (ModeNumeric, 4), (2, 10), (12, 7),
            (ModeAlphanumeric, 4), (1, 9), (10, 6),
            (ModeByte, 4), (1, 8), ('!', 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("12A!");
    }

    [Test]
    public async Task EciUtf8_ByteSegment_DecodesUtf8()
    {
        // ECI 26 (UTF-8) + byte segment with a 3-byte UTF-8 char (U+3042 あ)
        var data = Build(
            (ModeEci, 4), (26, 8),
            (ModeByte, 4), (3, 8), (0xE3, 8), (0x81, 8), (0x82, 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("あ");
    }

    [Test]
    public async Task EciUtf8_WithBom_BomIsStripped()
    {
        var data = Build(
            (ModeEci, 4), (26, 8),
            (ModeByte, 4), (4, 8), (0xEF, 8), (0xBB, 8), (0xBF, 8), ('A', 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("A");
    }

    [Test]
    public async Task EciIso8859_1_BomLikeBytes_AreLatin1Text_NotBom()
    {
        // ECI 3 explicitly declares ISO-8859-1: EF BB BF is the legitimate Latin-1
        // text "ï»¿", not a BOM, an explicit charset declaration must win over the
        // BOM heuristic.
        var data = Build(
            (ModeEci, 4), (3, 8),
            (ModeByte, 4), (4, 8), (0xEF, 8), (0xBB, 8), (0xBF, 8), ('A', 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("ï»¿A");
    }

    [Test]
    public async Task NoEci_BomBytes_TreatedAsUtf8AndStripped()
    {
        var data = Build(
            (ModeByte, 4), (4, 8), (0xEF, 8), (0xBB, 8), (0xBF, 8), ('A', 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("A");
    }

    [Test]
    public async Task EciTwoByteDesignator_Parses()
    {
        // 2-byte designator form: 10xxxxxx xxxxxxxx; value 26 = UTF-8
        var data = Build(
            (ModeEci, 4), (0x80, 8), (26, 8),
            (ModeByte, 4), (1, 8), ('X', 8),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("X");
    }

    [Test]
    public async Task EmptyData_DecodesToEmpty()
    {
        var status = Decode([], out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo(string.Empty);
    }

    [Test]
    public async Task ImplicitTerminator_FewerThanFourBitsRemaining_Succeeds()
    {
        // Single numeric digit, stream ends without an explicit terminator
        var data = Build((ModeNumeric, 4), (1, 10), (7, 4), (0, 6));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("7");
    }

    // Unsupported content (recognized but rejected, never misdecoded)

    [Test]
    [Arguments(ModeStructuredAppend)]
    [Arguments(ModeFnc1First)]
    [Arguments(ModeFnc1Second)]
    public async Task UnsupportedModes_ReturnUnsupportedContent(int mode)
    {
        var data = Build((mode, 4), (0, 12));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.UnsupportedContent);
    }

    /// <summary>
    /// Every mode's count indicator has to be bounded from the NEAR side, one bit
    /// short, not only by leaving no room at all. A guard weakened by one bit lets
    /// <see cref="BitReader"/> read past the buffer — <c>totalBits</c> is
    /// <c>data.Length * 8</c> here, so capacity and buffer end together — and it throws
    /// <see cref="InvalidOperationException"/> out of a bool-returning <c>TryDecode</c>.
    /// </summary>
    /// <remarks>
    /// Each stream is 32 bits: a complete leading segment, the mode indicator under
    /// test, and filler that is one bit short of that mode's count width. The filler is
    /// never read — the guard fires first — so its content does not matter.
    /// </remarks>
    [Test]
    [Arguments("0010" + "000000001" + "001010", ModeNumeric, 9)]        // 10-bit numeric count, 9 bits left
    [Arguments("0100" + "00000001" + "01000001", ModeAlphanumeric, 8)]  // 9-bit alphanumeric count, 8 bits left
    [Arguments("0001" + "0000000010" + "0001100", ModeByte, 7)]         // 8-bit byte count, 7 bits left
    public async Task CountIndicatorOneBitShort_ReturnsInvalidBitstream(string prefix, int mode, int bitsLeft)
    {
        var data = Build((Convert.ToInt32(prefix, 2), prefix.Length), (mode, 4), (0, bitsLeft));

        await Assert.That(data.Length * 8).IsEqualTo(prefix.Length + 4 + bitsLeft)
            .Because("the stream must end exactly on the byte boundary the filler was sized for");

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    // Kanji mode (decode only: no generator in this library emits it)

    /// <summary>ISO/IEC 18004 8.4.5 compaction, so the streams below read as hand-made.</summary>
    private static int Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    [Test]
    public async Task KanjiSegment_DecodesToJisX0208()
    {
        // Version 1 Kanji count indicator is 8 bits; 7 characters at 13 bits each.
        var data = Build(
            (ModeKanji, 4), (7, 8),
            (Kanji(0x82B1), 13), (Kanji(0x82F1), 13), (Kanji(0x82C9), 13),
            (Kanji(0x82BF), 13), (Kanji(0x82CD), 13), (Kanji(0x90A2), 13), (Kanji(0x8A45), 13),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("こんにちは世界");
    }

    /// <summary>A Kanji segment can sit beside the other modes in one stream.</summary>
    [Test]
    public async Task KanjiSegment_MixedWithOtherModes_ConcatenatesInOrder()
    {
        var data = Build(
            (ModeAlphanumeric, 4), (1, 9), (10, 6),
            (ModeKanji, 4), (1, 8), (Kanji(0x889F), 13),
            (ModeNumeric, 4), (1, 10), (7, 4),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("A亜7");
    }

    /// <summary>The wave dash is the cell a CP932-derived table would silently get wrong.</summary>
    [Test]
    public async Task KanjiSegment_DivergentCell_UsesJisX0208Reading()
    {
        var data = Build((ModeKanji, 4), (1, 8), (Kanji(0x8160), 13), (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("〜");
    }

    /// <summary>
    /// A Kanji segment that fills the last data codeword exactly, with the terminator
    /// shortened away (ISO/IEC 18004 7.4.9), is legal and must decode.
    /// </summary>
    [Test]
    public async Task KanjiSegment_FillsCapacityExactly_Succeeds()
    {
        // 4 + 8 + 4 × 13 = 64 bits = 8 data codewords, no terminator.
        var data = Build(
            (ModeKanji, 4), (4, 8),
            (Kanji(0x93FA), 13), (Kanji(0x967B), 13), (Kanji(0x8CEA), 13), (Kanji(0x889F), 13));

        await Assert.That(data.Length).IsEqualTo(8).Because("the stream must fill its last byte exactly");

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("日本語亜");
    }

    /// <summary>
    /// A zero-count Kanji segment is empty, not a terminator: decoding continues into
    /// the segment after it. Only the Standard QR terminator (mode 0000) ends a stream.
    /// </summary>
    [Test]
    public async Task KanjiSegment_ZeroCount_IsAnEmptySegmentAndDecodingContinues()
    {
        var data = Build(
            (ModeKanji, 4), (0, 8),
            (ModeNumeric, 4), (1, 10), (7, 4),
            (ModeTerminator, 4));

        var status = Decode(data, out var text);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.Success);
        await Assert.That(text).IsEquivalentTo("7");
    }

    /// <summary>NEC row 13 is CP932-only: well-formed, but outside the chosen repertoire.</summary>
    [Test]
    public async Task KanjiSegment_CellOutsideJisX0208_ReturnsUnmappedCharacter()
    {
        var data = Build((ModeKanji, 4), (1, 8), (Kanji(0x8740), 13), (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.UnmappedCharacter);
    }

    [Test]
    public async Task KanjiSegment_StructurallyImpossibleCell_ReturnsInvalidBitstream()
    {
        // Low byte 0x3F would require Shift_JIS trail byte 0x7F, which does not exist.
        var data = Build((ModeKanji, 4), (1, 8), (0x3F, 13), (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A Kanji count indicator one bit short of its width is a truncated segment. The
    /// guard has to be pinned from the near side too: leaving zero bits proves only
    /// that a far-away shortfall is caught, and a guard weakened by one bit then lets
    /// <see cref="BitReader"/> read past the buffer and throw out of a bool-returning
    /// <c>TryDecode</c>.
    /// </summary>
    [Test]
    public async Task KanjiSegment_CountIndicatorOneBitShort_ReturnsInvalidBitstream()
    {
        // Numeric "12" then a Kanji indicator at bit 21, leaving 7 of the 8 count bits.
        var data = Build((ModeNumeric, 4), (2, 10), (12, 7), (ModeKanji, 4));

        await Assert.That(data.Length * 8 - 25).IsEqualTo(7).Because("exactly one bit short of the 8-bit count field");

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task KanjiSegment_CountExceedsRemainingBits_ReturnsInvalidBitstream()
    {
        var data = Build((ModeKanji, 4), (40, 8), (Kanji(0x889F), 13), (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task UnknownEciCharset_ReturnsUnsupportedContent()
    {
        // ECI 20 = Shift-JIS: recognized designator, unsupported charset
        var data = Build(
            (ModeEci, 4), (20, 8),
            (ModeByte, 4), (1, 8), ('A', 8),
            (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.UnsupportedContent);
    }

    // Invalid bitstreams (malformed input must fail cleanly, never throw)

    [Test]
    [Arguments(0b0110)]
    [Arguments(0b1010)]
    [Arguments(0b1111)]
    public async Task UnassignedModeIndicators_ReturnInvalidBitstream(int mode)
    {
        var data = Build((mode, 4), (0, 12));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Numeric_GroupValueAboveRange_ReturnsInvalidBitstream()
    {
        // 10-bit group encodes 3 digits, so values 1000-1023 are invalid
        var data = Build((ModeNumeric, 4), (3, 10), (1000, 10), (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Alphanumeric_PairValueAboveRange_ReturnsInvalidBitstream()
    {
        // 11-bit pair encodes values 0..2024
        var data = Build((ModeAlphanumeric, 4), (2, 9), (2025, 11), (ModeTerminator, 4));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task ByteSegment_CountBeyondStream_ReturnsInvalidBitstream()
    {
        // Declares 200 bytes but the stream ends immediately
        var data = Build((ModeByte, 4), (200, 8), (0, 8));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Numeric_CountBeyondStream_ReturnsInvalidBitstream()
    {
        var data = Build((ModeNumeric, 4), (100, 10), (0, 2));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Numeric_CountBeyondStream_TakesPrecedenceOverDestinationTooSmall()
    {
        // Four digits require 14 payload bits, but only two remain after the header.
        var data = Build((ModeNumeric, 4), (4, 10), (0, 2));

        var status = QRBinaryDecoder.DecodeBitStream(data, Version, Span<char>.Empty, out _);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task EciDesignator_TruncatedMultiByte_ReturnsInvalidBitstream()
    {
        // 2-byte designator prefix (10xxxxxx) with no second byte
        var data = Build((ModeEci, 4), (0x80, 8));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task EciDesignator_InvalidPrefix_ReturnsInvalidBitstream()
    {
        // 111xxxxx is not a valid designator length prefix
        var data = Build((ModeEci, 4), (0xE0, 8), (0, 16));

        await Assert.That(Decode(data, out _)).IsEquivalentTo(DecodeStatus.InvalidBitstream);
    }

    // Destination sizing

    [Test]
    public async Task DestinationTooSmall_ReportsStatus_NotException()
    {
        var data = Build((ModeNumeric, 4), (3, 10), (123, 10), (ModeTerminator, 4));

        Span<char> tiny = stackalloc char[2];
        var status = QRBinaryDecoder.DecodeBitStream(data, Version, tiny, out _);

        await Assert.That(status).IsEquivalentTo(DecodeStatus.DestinationTooSmall);
    }

    // Robustness: untrusted input must never throw

    [Test]
    public void RandomGarbage_NeverThrows()
    {
        var random = new Random(20260712);
        Span<char> destination = stackalloc char[256];
        for (var round = 0; round < 2000; round++)
        {
            var data = new byte[random.Next(0, 80)];
            random.NextBytes(data);

            // Any status is fine; throwing is not.
            QRBinaryDecoder.DecodeBitStream(data, random.Next(1, 41), destination, out _);
        }
    }

    private static DecodeStatus Decode(byte[] data, out string text)
    {
        Span<char> destination = stackalloc char[256];
        var status = QRBinaryDecoder.DecodeBitStream(data, Version, destination, out var charsWritten);
        text = destination.Slice(0, charsWritten).ToString();
        return status;
    }

    private static byte[] Build(params (int value, int bits)[] fields)
    {
        var buffer = new byte[64];
        var writer = new BitWriter(buffer);
        foreach (var (value, bits) in fields)
        {
            writer.Write(value, bits);
        }
        writer.Flush();
        return writer.GetData().ToArray();
    }
}
