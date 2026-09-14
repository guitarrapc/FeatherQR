using FeatherQR.Internals.StandardQR;
namespace FeatherQR.Tests;

/// <summary>
/// The Structured Append header rules, pinned on hand-assembled bit streams so no image,
/// matrix or encoder is in the loop: the header is reported wherever it sits, a set of
/// one is accepted, and a position past the count, a second header or a truncated one is
/// a malformed stream. Version 1 throughout (numeric count indicator 10 bits).
/// </summary>
public class StructuredAppendDecodeTest
{
    private const int Version = 1;
    private const int ModeTerminator = 0b0000;
    private const int ModeNumeric = 0b0001;
    private const int ModeAlphanumeric = 0b0010;
    private const int ModeStructuredAppend = 0b0011;
    private const int ModeByte = 0b0100;
    private const int ModeEci = 0b0111;

    [Test]
    public async Task HeaderFirst_IsReportedAndTheTextIsTheSymbolsOwn()
    {
        var data = Build((ModeStructuredAppend, 4), (2, 4), (3, 4), (0xA5, 8), (ModeNumeric, 4), (3, 10), (123, 10), (ModeTerminator, 4));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("123");
        await Assert.That(header.IsEmpty).IsFalse();
        await Assert.That(header.Index).IsEqualTo(2);
        await Assert.That(header.Count).IsEqualTo(4);
        await Assert.That(header.Parity).IsEqualTo((byte)0xA5);
    }

    [Test]
    public async Task HeaderAfterASegment_IsReported()
    {
        var data = Build((ModeNumeric, 4), (3, 10), (123, 10), (ModeStructuredAppend, 4), (0, 4), (1, 4), (0x5A, 8), (ModeTerminator, 4));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("123");
        await Assert.That(header.Index).IsEqualTo(0);
        await Assert.That(header.Count).IsEqualTo(2);
        await Assert.That(header.Parity).IsEqualTo((byte)0x5A);
    }

    [Test]
    public async Task HeaderAfterEci_IsReported()
    {
        var data = Build((ModeEci, 4), (26, 8), (ModeStructuredAppend, 4), (1, 4), (1, 4), (0x00, 8), (ModeNumeric, 4), (1, 10), (7, 4), (ModeTerminator, 4));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(header.Index).IsEqualTo(1);
        await Assert.That(header.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SetOfOne_IsAcceptedAndReported()
    {
        var data = Build((ModeStructuredAppend, 4), (0, 4), (0, 4), (0xFF, 8), (ModeNumeric, 4), (1, 10), (7, 4), (ModeTerminator, 4));

        var status = Decode(data, out _, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(header.IsEmpty).IsFalse();
        await Assert.That(header.Index).IsEqualTo(0);
        await Assert.That(header.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LastOfSixteen_IsReported()
    {
        var data = Build((ModeStructuredAppend, 4), (15, 4), (15, 4), (0x01, 8), (ModeNumeric, 4), (1, 10), (7, 4), (ModeTerminator, 4));

        var status = Decode(data, out _, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(header.Index).IsEqualTo(15);
        await Assert.That(header.Count).IsEqualTo(16);
    }

    [Test]
    public async Task NoHeader_LeavesTheValueEmpty()
    {
        var data = Build((ModeNumeric, 4), (3, 10), (123, 10), (ModeTerminator, 4));

        var status = Decode(data, out _, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(header.IsEmpty).IsTrue();
        await Assert.That(header).IsEqualTo(default(QRStructuredAppend));
    }

    [Test]
    public async Task IndexNotBelowCount_IsInvalidBitstream()
    {
        // Position 3 in a set of 3.
        var data = Build((ModeStructuredAppend, 4), (3, 4), (2, 4), (0xA5, 8), (ModeNumeric, 4), (1, 10), (7, 4), (ModeTerminator, 4));

        var status = Decode(data, out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task SecondHeader_IsInvalidBitstream()
    {
        var data = Build((ModeStructuredAppend, 4), (0, 4), (1, 4), (0xA5, 8), (ModeStructuredAppend, 4), (1, 4), (1, 4), (0xA5, 8), (ModeTerminator, 4));

        var status = Decode(data, out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task TruncatedHeader_IsInvalidBitstream()
    {
        // Mode indicator followed by 8 header bits; the byte padding makes it 12 of the 16.
        var data = Build((ModeStructuredAppend, 4), (0, 4), (1, 4));

        var status = Decode(data, out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    // The 16-bit guard at its boundary. Every stream is sized to leave exactly the stated
    // number of bits after the mode indicator, so the guard's threshold is what decides.

    [Test]
    public async Task FifteenBitsAfterTheIndicator_IsInvalidBitstreamNotAnException()
    {
        // Numeric "12" (4+10+7 = 21 bits), indicator at 21..24, 15 bits to the end of byte 5.
        var data = Build((ModeNumeric, 4), (2, 10), (12, 7), (ModeStructuredAppend, 4), (0, 15));

        var status = Decode(data, out _, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
        await Assert.That(header.IsEmpty).IsTrue();
    }

    [Test]
    public async Task HeaderThatEndsTheStream_IsReported()
    {
        // Byte "A" (4+8+8 = 20 bits), indicator at 20..24, exactly the 16 header bits remain.
        var data = Build((ModeByte, 4), (1, 8), ('A', 8), (ModeStructuredAppend, 4), (0, 4), (1, 4), (0xA5, 8));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A");
        await Assert.That(header.Index).IsEqualTo(0);
        await Assert.That(header.Count).IsEqualTo(2);
        await Assert.That(header.Parity).IsEqualTo((byte)0xA5);
    }

    [Test]
    public async Task HeaderFollowedByFewerThanFourBits_IsReportedAndTheStreamEnds()
    {
        // Alphanumeric "A" (4+9+6 = 19 bits), indicator at 19..23, header to bit 39, one bit left:
        // fewer than four is the implicit terminator, so the header is the last thing read.
        var data = Build((ModeAlphanumeric, 4), (1, 9), (10, 6), (ModeStructuredAppend, 4), (3, 4), (3, 4), (0x0F, 8), (0, 1));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A");
        await Assert.That(header.Index).IsEqualTo(3);
        await Assert.That(header.Count).IsEqualTo(4);
        await Assert.That(header.Parity).IsEqualTo((byte)0x0F);
    }

    [Test]
    public async Task HeaderAfterASegmentWithTwoBitsLeft_IsReported()
    {
        // Numeric "7" (18 bits) then the header (20 bits) = 38, padded to 40: two bits after it.
        var data = Build((ModeNumeric, 4), (1, 10), (7, 4), (ModeStructuredAppend, 4), (0, 4), (1, 4), (0x5A, 8));

        var status = Decode(data, out var text, out var header);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(header.Index).IsEqualTo(0);
        await Assert.That(header.Count).IsEqualTo(2);
    }

    private static DecodeStatus Decode(byte[] data, out string text, out QRStructuredAppend header)
    {
        Span<char> destination = stackalloc char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, Version, destination, out var charsWritten, out header);
        text = destination.Slice(0, charsWritten).ToString();
        return status;
    }

    /// <summary>Packs (value, bit count) fields MSB-first; the last byte is zero-padded.</summary>
    private static byte[] Build(params (int Value, int Bits)[] fields)
    {
        var totalBits = fields.Sum(f => f.Bits);
        var bytes = new byte[(totalBits + 7) / 8];
        var position = 0;
        foreach (var (value, bits) in fields)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    bytes[position / 8] |= (byte)(0x80 >> (position % 8));
                position++;
            }
        }
        return bytes;
    }
}
