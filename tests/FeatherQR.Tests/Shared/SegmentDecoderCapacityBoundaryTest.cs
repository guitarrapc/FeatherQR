using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// The bit-sufficiency boundary of every segment payload decoder, from both sides:
/// a payload that ends exactly on the last available bit must decode, and one that
/// is a single bit short must be rejected.
/// </summary>
/// <remarks>
/// <para>
/// The exact-fill side is real content — ISO/IEC 18004 7.4.9 lets the terminator be
/// shortened away when the data fills the capacity, so a symbol whose last character
/// ends on the final bit is legal and common at capacity boundaries.
/// </para>
/// <para>
/// The one-bit-short side is what keeps <see cref="BitReader"/> inside the buffer.
/// For Standard QR and for Micro QR M2/M4 the capacity and the buffer end together,
/// so a decoder that let the payload run one bit over would not merely misread: it
/// would throw <see cref="InvalidOperationException"/> out of a bool-returning
/// <c>TryDecode</c>. Both sides are needed — a guard pinned only from above can be
/// weakened by one bit and no test notices.
/// </para>
/// </remarks>
public class SegmentDecoderCapacityBoundaryTest
{
    /// <summary>Packs a bit string MSB-first, rounding the buffer up to whole bytes.</summary>
    private static byte[] Bits(string bits)
    {
        var clean = bits.Replace(" ", "");
        var result = new byte[(clean.Length + 7) / 8];
        for (var i = 0; i < clean.Length; i++)
            if (clean[i] == '1')
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    private static string Binary(int value, int width) => Convert.ToString(value, 2).PadLeft(width, '0');

    /// <summary>ISO/IEC 18004 8.4.5 compaction, rendered as a 13-bit string.</summary>
    private static string Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return Binary(((shifted >> 8) * 0xC0) + (shifted & 0xFF), 13);
    }

    /// <summary>Each case: the packed payload, its character count, its exact bit length, and the expected text.</summary>
    public static IEnumerable<(string Mode, string Stream, int Count, int RequiredBits, string Text)> Cases()
    {
        // Numeric: two groups of three digits, 10 bits each.
        yield return ("Numeric", Binary(123, 10) + Binary(456, 10), 6, 20, "123456");
        // Numeric with a two-digit remainder group (7 bits).
        yield return ("Numeric-remainder2", Binary(123, 10) + Binary(45, 7), 5, 17, "12345");
        // Numeric with a one-digit remainder group (4 bits).
        yield return ("Numeric-remainder1", Binary(123, 10) + Binary(4, 4), 4, 14, "1234");
        // Alphanumeric: two pairs, 11 bits each. A=10 B=11 -> 10*45+11; '1'=1 '2'=2 -> 1*45+2.
        yield return ("Alphanumeric", Binary(10 * 45 + 11, 11) + Binary(1 * 45 + 2, 11), 4, 22, "AB12");
        // Alphanumeric with an odd trailing character (6 bits). Z = 35.
        yield return ("Alphanumeric-odd", Binary(10 * 45 + 11, 11) + Binary(35, 6), 3, 17, "ABZ");
        // Byte: 5 ASCII bytes, 8 bits each.
        yield return ("Byte", string.Concat("Hello".Select(c => Binary(c, 8))), 5, 40, "Hello");
        // Kanji: 4 characters, 13 bits each.
        yield return ("Kanji", Kanji(0x93FA) + Kanji(0x967B) + Kanji(0x8CEA) + Kanji(0x889F), 4, 52, "日本語亜");
    }

    private static DecodeStatus Decode(string mode, byte[] data, int totalBits, int count, Span<char> destination, out int charsWritten)
    {
        var reader = new BitReader(data);
        charsWritten = 0;
        return mode switch
        {
            "Byte" => SegmentDecoders.DecodeBytePayload(ref reader, totalBits, count, ByteSegmentCharset.Iso8859_1, new byte[64], destination, ref charsWritten),
            "Kanji" => SegmentDecoders.DecodeKanjiPayload(ref reader, totalBits, count, destination, ref charsWritten),
            _ when mode.StartsWith("Alphanumeric", StringComparison.Ordinal)
                => SegmentDecoders.DecodeAlphanumericPayload(ref reader, totalBits, count, destination, ref charsWritten),
            _ => SegmentDecoders.DecodeNumericPayload(ref reader, totalBits, count, destination, ref charsWritten),
        };
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PayloadEndingExactlyAtTheLastBit_Succeeds(string mode, string stream, int count, int requiredBits, string text)
    {
        await Assert.That(stream.Length).IsEqualTo(requiredBits).Because($"{mode}: the case's own bit count must be right");

        var destination = new char[64];
        var status = Decode(mode, Bits(stream), requiredBits, count, destination, out var charsWritten);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success).Because(mode);
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(text);
    }

    /// <summary>
    /// One bit short of the payload's own length. The destination is ample, so this can
    /// only be reported as a malformed stream.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PayloadOneBitShortOfItsLength_ReportsInvalidBitstream(string mode, string stream, int count, int requiredBits, string text)
    {
        _ = text;
        var destination = new char[64];
        var status = Decode(mode, Bits(stream), requiredBits - 1, count, destination, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream).Because(mode);
    }

    /// <summary>
    /// One bit short AND a destination too small: the bitstream verdict wins.
    /// </summary>
    /// <remarks>
    /// This is what the up-front sufficiency check is actually for. Numeric and
    /// alphanumeric also check per group inside their loops, so weakening the up-front
    /// check by a bit still yields <c>InvalidBitstream</c> when the destination is
    /// ample — the inner check catches it. It only becomes visible here: with a short
    /// destination, a weakened up-front check falls through to the destination test and
    /// reports <c>DestinationTooSmall</c>, which callers read as "the symbol was read,
    /// grow your buffer" and which stops the image decoder hunting for the real symbol.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PayloadOneBitShortAndDestinationTooSmall_StillReportsInvalidBitstream(string mode, string stream, int count, int requiredBits, string text)
    {
        _ = text;
        var status = Decode(mode, Bits(stream), requiredBits - 1, count, Span<char>.Empty, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream).Because(mode);
    }

    // The remaining guards in SegmentDecoders that bound a read or a value. They are
    // not Kanji work, but they are the same family as the four above and were equally
    // unpinned; the ECI pair fails in the way this class exists to prevent.

    /// <summary>
    /// A truncated ECI designator is a malformed stream, not an exception. Both
    /// multi-byte forms have to be bounded: the reader is bounded by the BUFFER, so a
    /// designator that runs past <c>totalBits</c> in a full buffer throws.
    /// </summary>
    [Test]
    [Arguments("10", 15)] // two-byte form, one bit short of its 16
    [Arguments("110", 23)] // three-byte form, one bit short of its 24
    public async Task TruncatedEciDesignator_ReportsInvalidBitstream(string prefix, int totalBits)
    {
        var stream = prefix.PadRight(totalBits, '1');
        var reader = new BitReader(Bits(stream));

        var status = SegmentDecoders.ReadEciDesignator(ref reader, totalBits, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>The one-byte ECI form still needs its 8 bits.</summary>
    [Test]
    public async Task EciDesignatorWithFewerThanEightBits_ReportsInvalidBitstream()
    {
        var reader = new BitReader(Bits("0000001"));

        var status = SegmentDecoders.ReadEciDesignator(ref reader, totalBits: 7, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// Numeric group values are bounded per group size, not only for the 10-bit group:
    /// a 7-bit group above 99 or a 4-bit group above 9 is not three, two or one digit,
    /// it is corruption. Without the bound the decoder emits characters past '9'.
    /// </summary>
    [Test]
    [Arguments(5, 7, 100)] // two-digit remainder group, one past its maximum
    [Arguments(5, 7, 127)] // two-digit remainder group, all ones
    [Arguments(4, 4, 10)]  // one-digit remainder group, one past its maximum
    [Arguments(4, 4, 15)]  // one-digit remainder group, all ones
    public async Task NumericRemainderGroupAboveItsMaximum_ReportsInvalidBitstream(int count, int groupBits, int groupValue)
    {
        var stream = Binary(123, 10) + Binary(groupValue, groupBits);
        var reader = new BitReader(Bits(stream));
        var destination = new char[64];
        var charsWritten = 0;

        var status = SegmentDecoders.DecodeNumericPayload(ref reader, stream.Length, count, destination, ref charsWritten);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// A byte segment that is nothing but a UTF-8 BOM decodes to the empty string: the
    /// BOM is a charset marker, not content. The length guard has to accept exactly 3
    /// bytes, not just more than 3.
    /// </summary>
    [Test]
    public async Task ByteSegmentThatIsOnlyAUtf8Bom_DecodesToEmptyText()
    {
        var reader = new BitReader([0xEF, 0xBB, 0xBF]);
        var destination = new char[64];
        var charsWritten = 0;

        var status = SegmentDecoders.DecodeBytePayload(ref reader, totalBits: 24, count: 3, ByteSegmentCharset.Unspecified, new byte[8], destination, ref charsWritten);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(charsWritten).IsEqualTo(0);
    }
}
