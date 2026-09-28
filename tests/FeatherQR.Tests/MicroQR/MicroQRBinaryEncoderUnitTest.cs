using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

public class MicroQRBinaryEncoderUnitTest
{
    // M1 golden vectors, hand-derivable from ISO/IEC 18004 rules:
    // no mode indicator, 3-bit count, 10/7/4 bits per 3/2/1 digits, 3-bit
    // terminator (shortened at capacity), zero bit padding to byte boundary,
    // 0xEC/0x11 pad codewords, final 4-bit pad codeword = 0000 (high nibble).
    [Test]
    [Arguments("99999", new byte[] { 0xBF, 0x3E, 0x30 })] // exactly fills 20 bits, no terminator
    [Arguments("9999", new byte[] { 0x9F, 0x3C, 0x80 })]  // terminator exactly fills capacity
    [Arguments("999", new byte[] { 0x7F, 0x38, 0x00 })]   // final 4-bit pad codeword is 0000
    [Arguments("", new byte[] { 0x00, 0xEC, 0x00 })]      // full pad codeword then 4-bit 0000
    public async Task EncodeDataCodewords_M1_Numeric_GoldenVectors(string text, byte[] expected)
    {
        var buffer = new byte[16];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, EncodingMode.Numeric, buffer);

        await Assert.That(written).IsEqualTo(3);
        await Assert.That(buffer[..3]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EncodeDataCodewords_M2L_IsoExample01234567()
    {
        // ISO/IEC 18004 Micro QR encoding example: "01234567" in M2-L.
        // 1-bit numeric mode indicator (0), 4-bit count (1000), numeric groups
        // 012/345/67, 5-bit terminator, zero padding to 40 bits.
        var buffer = new byte[16];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords("01234567".AsSpan(), MicroQRVersion.M2, MicroQREccLevel.L, EncodingMode.Numeric, buffer);

        await Assert.That(written).IsEqualTo(5);
        await Assert.That(buffer[..5]).IsEquivalentTo(new byte[] { 0x40, 0x18, 0xAC, 0xC3, 0x00 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EncodeDataCodewords_M2L_Alphanumeric_MatchesNaiveReference()
    {
        // Independent naive reference: build the exact bit string per ISO rules
        // (1-bit mode indicator = 1, 3-bit count, 11 bits per pair + 6 bits odd).
        var expected = BitsToBytes(
            "1" + "101" +
            ToBits(10 * 45 + 12, 11) + // "AC"
            ToBits(41 * 45 + 4, 11) +  // "-4"
            ToBits(2, 6) +             // "2"
            "00000",                   // terminator
            totalBits: 40);

        var buffer = new byte[16];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords("AC-42".AsSpan(), MicroQRVersion.M2, MicroQREccLevel.L, EncodingMode.Alphanumeric, buffer);

        await Assert.That(written).IsEqualTo(5);
        await Assert.That(buffer[..5]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EncodeDataCodewords_M4M_Byte_MatchesNaiveReference()
    {
        // M4 byte mode: 3-bit mode indicator (010), 5-bit count, 8 bits per byte,
        // 9-bit terminator, then pad codewords to 112 bits (14 codewords).
        var payload = "hello"u8.ToArray();
        var bits = "010" + ToBits(payload.Length, 5);
        foreach (var b in payload)
        {
            bits += ToBits(b, 8);
        }
        bits += "000000000"; // terminator
        var expected = BitsToBytes(bits, totalBits: 112);

        var buffer = new byte[24];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords("hello".AsSpan(), MicroQRVersion.M4, MicroQREccLevel.M, EncodingMode.Byte, buffer);

        await Assert.That(written).IsEqualTo(14);
        await Assert.That(buffer[..14]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EncodeDataCodewords_M3M_HalfCodewordPadding()
    {
        // M3-M capacity is 68 bits = 9 data codewords with a 4-bit final codeword.
        // Short numeric payload: 2-bit mode indicator (00), 5-bit count, digits,
        // 7-bit terminator, zero-fill, pad codewords, final half codeword = 0000.
        var bits = "00" + ToBits(3, 5) + ToBits(123, 10) + "0000000";
        var expected = BitsToBytes(bits, totalBits: 68);

        var buffer = new byte[16];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords("123".AsSpan(), MicroQRVersion.M3, MicroQREccLevel.M, EncodingMode.Numeric, buffer);

        await Assert.That(written).IsEqualTo(9);
        await Assert.That(buffer[..9]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static string ToBits(int value, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (value & (1 << (count - 1 - i))) != 0 ? '1' : '0';
        }
        return new string(chars);
    }

    /// <summary>
    /// Independent reference for terminator/padding: zero-fill the current byte,
    /// then alternate 0xEC/0x11 full pad codewords over whole bytes, and leave the
    /// final half byte (when totalBits % 8 == 4) as 0000 in the high nibble.
    /// </summary>
    private static byte[] BitsToBytes(string bits, int totalBits)
    {
        var fullBytes = totalBits / 8;
        var hasHalf = totalBits % 8 != 0;
        var result = new byte[fullBytes + (hasHalf ? 1 : 0)];

        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                result[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        // pad codewords start at the next byte boundary after the data bits
        var nextPadByte = (bits.Length + 7) / 8;
        var padIndex = 0;
        for (var i = nextPadByte; i < fullBytes; i++)
        {
            result[i] = (padIndex++ % 2 == 0) ? (byte)0xEC : (byte)0x11;
        }
        // final half codeword (if any) is already 0000 in the high nibble
        return result;
    }
}
