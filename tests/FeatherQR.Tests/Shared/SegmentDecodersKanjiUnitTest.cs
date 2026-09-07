using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// The shared Kanji segment payload decoder. Negative cases outnumber positive
/// ones on purpose: a Kanji segment is 13 bits per character with no internal
/// redundancy, so a decoder that guesses at malformed input turns a corrupt
/// symbol into a plausible wrong answer.
/// </summary>
public class SegmentDecodersKanjiUnitTest
{
    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helper.</summary>
    private static int Index13(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>Packs 13-bit values MSB-first, the way a Kanji segment sits on the wire.</summary>
    private static byte[] Pack(params int[] indices)
    {
        var bits = indices.Length * 13;
        var bytes = new byte[(bits + 7) / 8];
        var position = 0;
        foreach (var index in indices)
        {
            for (var bit = 12; bit >= 0; bit--)
            {
                if ((index >> bit & 1) != 0)
                    bytes[position / 8] |= (byte)(1 << (7 - position % 8));
                position++;
            }
        }
        return bytes;
    }

    private static (DecodeStatus Status, string Text) Decode(byte[] data, int count, int destinationLength, int? totalBits = null)
    {
        var reader = new BitReader(data);
        var destination = new char[destinationLength];
        var charsWritten = 0;
        var status = SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits ?? data.Length * 8, count, destination, ref charsWritten);
        return (status, new string(destination, 0, charsWritten));
    }

    [Test]
    [Arguments(0x82B1, "こ")]
    [Arguments(0x889F, "亜")]
    [Arguments(0x8140, "　")]
    [Arguments(0x9FFC, "滌")]
    [Arguments(0xE040, "漾")]
    [Arguments(0xEAA4, "熙")]
    public async Task DecodeKanjiPayload_SingleCell_AcrossBothRanges(int sjis, string expected)
    {
        var (status, text) = Decode(Pack(Index13(sjis)), count: 1, destinationLength: 1);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task DecodeKanjiPayload_MultipleCells_SpanningBothRanges()
    {
        var indices = new[] { 0x82B1, 0x82F1, 0x82C9, 0x82BF, 0x82CD, 0x90A2, 0x8A45 }.Select(Index13).ToArray();
        var (status, text) = Decode(Pack(indices), count: indices.Length, destinationLength: indices.Length);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("こんにちは世界");
    }

    /// <summary>The divergent cells decode to their JIS X 0208 readings through the payload path too.</summary>
    [Test]
    public async Task DecodeKanjiPayload_DivergentCells_UseJisX0208()
    {
        var indices = new[] { 0x815F, 0x8160, 0x8161, 0x817C, 0x8191, 0x8192, 0x81CA }.Select(Index13).ToArray();
        var (status, text) = Decode(Pack(indices), count: indices.Length, destinationLength: indices.Length);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("\\〜‖−¢£¬");
    }

    /// <summary>
    /// A segment whose payload ends exactly on the last available bit is legal: the
    /// terminator is shortened away at capacity (ISO/IEC 18004 7.4.9). The sufficiency
    /// guard must therefore reject only <c>&lt;</c>, never <c>&lt;=</c>.
    /// </summary>
    /// <remarks>
    /// Eight characters pack into 104 bits = 13 bytes with nothing left over, so
    /// <c>totalBits</c> equals <c>count * 13</c> exactly. The numeric, alphanumeric and
    /// byte decoders each had an exact-fill case already; Kanji did not, and an
    /// off-by-one there rejects a valid symbol rather than accepting an invalid one.
    /// </remarks>
    [Test]
    public async Task DecodeKanjiPayload_PayloadEndsExactlyAtCapacity_Succeeds()
    {
        var indices = new[] { 0x93FA, 0x967B, 0x8CEA, 0x889F, 0x82B1, 0x82F1, 0x82C9, 0x8A45 }.Select(Index13).ToArray();
        var packed = Pack(indices);

        await Assert.That(packed.Length * 8).IsEqualTo(indices.Length * 13).Because("the stream must fill its last byte exactly");

        var (status, text) = Decode(packed, count: indices.Length, destinationLength: indices.Length);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日本語亜こんに界");
    }

    [Test]
    public async Task DecodeKanjiPayload_ZeroCount_WritesNothing()
    {
        var (status, text) = Decode(Pack(Index13(0x82B1)), count: 0, destinationLength: 0);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("");
    }

    /// <summary>
    /// A count that names more characters than the remaining bits could hold is a
    /// malformed stream, not a short buffer; reporting DestinationTooSmall here would
    /// stop the image decoder from looking for the real symbol. Same ordering rule the
    /// numeric and alphanumeric payload decoders follow.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(100)]
    public async Task DecodeKanjiPayload_CountExceedsAvailableBits_ReportsInvalidBitstream(int count)
    {
        var (status, _) = Decode(Pack(Index13(0x82B1)), count, destinationLength: 200, totalBits: 13);
        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task DecodeKanjiPayload_ShortDestinationOnAValidStream_ReportsDestinationTooSmall()
    {
        var indices = new[] { 0x82B1, 0x82F1, 0x82C9 }.Select(Index13).ToArray();
        var (status, _) = Decode(Pack(indices), count: indices.Length, destinationLength: 2);
        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>When both are wrong, the bitstream verdict wins.</summary>
    [Test]
    public async Task DecodeKanjiPayload_ShortDestinationAndShortBitstream_ReportsInvalidBitstream()
    {
        var (status, _) = Decode(Pack(Index13(0x82B1)), count: 5, destinationLength: 1, totalBits: 13);
        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// The three outcomes a Kanji cell can produce must be distinguishable by status
    /// alone, because they call for different things from the caller: decode the text,
    /// hand the symbol to a CP932-capable reader, or treat the symbol as corrupt.
    /// </summary>
    [Test]
    public async Task DecodeKanjiPayload_TheThreeOutcomesHaveDistinctStatuses()
    {
        var mapped = Decode(Pack(Index13(0x82B1)), count: 1, destinationLength: 1).Status;
        var unmapped = Decode(Pack(Index13(0x8740)), count: 1, destinationLength: 1).Status;   // NEC row 13, CP932-only
        var impossible = Decode(Pack(0x3F), count: 1, destinationLength: 1).Status;            // no such Shift_JIS pair

        await Assert.That(mapped).IsEqualTo(DecodeStatus.Success);
        await Assert.That(unmapped).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(impossible).IsEqualTo(DecodeStatus.InvalidBitstream);

        // The point of the new status: "this symbol is readable by a CP932 reader" must
        // not be confused with "this symbol uses a feature we do not implement".
        await Assert.That(unmapped).IsNotEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(unmapped).IsNotEqualTo(impossible);
    }

    /// <summary>
    /// Well-formed cells outside JIS X 0208 (NEC row 13, the tail past 0xEAA4) are an
    /// unmapped character, not a corrupt bitstream and not an unsupported feature: the
    /// symbol was read correctly, the chosen mapping simply has no character for it.
    /// </summary>
    [Test]
    [Arguments(0x8740)] // NEC row 13, CP932-only
    [Arguments(0x879C)] // NEC row 13, last cell
    [Arguments(0x8540)] // row 9, unassigned in both
    [Arguments(0xEAA5)] // first cell past the repertoire
    [Arguments(0xEBBF)] // last cell of the Kanji-mode range
    public async Task DecodeKanjiPayload_UnassignedCell_ReportsUnmappedCharacter(int sjis)
    {
        var (status, _) = Decode(Pack(Index13(sjis)), count: 1, destinationLength: 1);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>
    /// A 13-bit value that no Shift_JIS pair can produce (low byte 0x3F would need
    /// trail byte 0x7F, or above 0xBC would need a trail byte past 0xFC) is corruption.
    /// </summary>
    [Test]
    [Arguments(0x3F)]        // low byte 0x3F, lead 0x81
    [Arguments(0xC0 + 0x3F)] // low byte 0x3F, lead 0x82
    [Arguments(0xBD)]        // low byte past the last expressible trail
    [Arguments(0xBF)]
    public async Task DecodeKanjiPayload_StructurallyImpossibleValue_ReportsInvalidBitstream(int index13)
    {
        var (status, _) = Decode(Pack(index13), count: 1, destinationLength: 1);
        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>A bad cell in the middle stops the segment; earlier characters are not trusted.</summary>
    [Test]
    public async Task DecodeKanjiPayload_UnassignedCellMidSegment_FailsTheWholeSegment()
    {
        var indices = new[] { Index13(0x82B1), Index13(0x8740), Index13(0x82F1) };
        var (status, _) = Decode(Pack(indices), count: 3, destinationLength: 3);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }
}
