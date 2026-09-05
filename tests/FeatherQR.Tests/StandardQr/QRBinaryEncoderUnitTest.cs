using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals;
using System.Text;

namespace FeatherQR.Tests;

public class QRBinaryEncoderUnitTest
{
    // Basic test - WriteMode
    [Test]
    [Arguments(EncodingMode.Numeric, EciMode.Default, 0b_00010000)]
    [Arguments(EncodingMode.Alphanumeric, EciMode.Default, 0b_00100000)]
    [Arguments(EncodingMode.Byte, EciMode.Default, 0b_01000000)]
    internal async Task WriteMode_Numeric_Produces4Bits(EncodingMode encoding, EciMode eci, int expected)
    {
        byte[] buffer = new byte[1];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(encoding, eci);
        var data = encoder.GetEncodedData().ToArray();

        await Assert.That(data[0]).IsEqualTo((byte)expected);
    }

    [Test]
    [Arguments(EciMode.Iso8859_1, 0b_0111_0000, 0b_0011_0100)]  // ECI(0111) + Value(00000011) + Byte(0100)
    [Arguments(EciMode.Utf8, 0b_0111_0001, 0b_1010_0100)]      // ECI(0111) + Value(00011010) + Byte(0100)
    internal async Task WriteMode_WithECI_Produces16Bits(EciMode eci, int expected0, int expected1)
    {
        byte[] buffer = new byte[2];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Byte, eci);
        var data = encoder.GetEncodedData().ToArray();

        await Assert.That(data[0]).IsEqualTo((byte)expected0);
        await Assert.That(data[1]).IsEqualTo((byte)expected1);
    }

    [Test]
    [Arguments(EciMode.Iso8859_1, 3)]
    [Arguments(EciMode.Utf8, 26)]
    public async Task WriteMode_WithECI_HasCorrectStructure(EciMode eci, int eciValue)
    {
        byte[] buffer = new byte[2];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Byte, eci);
        var result = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);

        // Assert: Total bit length is 16 (4 + 8 + 4)
        await Assert.That(result.Length).IsEqualTo(16);

        // Assert: ECI mode indicator (0111)
        await Assert.That(result.Substring(0, 4)).IsEqualTo("0111");

        // Assert: ECI value (8 bits)
        var eciValueBits = result.Substring(4, 8);
        await Assert.That(eciValueBits).IsEqualTo(Convert.ToString(eciValue, 2).PadLeft(8, '0'));

        // Assert: Byte mode indicator (0100)
        await Assert.That(result.Substring(12, 4)).IsEqualTo("0100");
    }

    // WriteCharacterCount

    [Test]
    [Arguments(10, 10, 0b_0000_0010_1000_0000)] // 10 in 10 bits
    [Arguments(255, 16, 0b_0000_0000_1111_1111)] // 255 in 16 bits (first 2 bytes)
    public async Task WriteCharacterCount_ProducesCorrectBits(int count, int bitsLength, int expectedFirstByte)
    {
        byte[] buffer = new byte[3];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteCharacterCount(count, bitsLength);
        var data = encoder.GetEncodedData().ToArray();

        var result = data[0] << 8 | data[1];
        await Assert.That(result).IsEqualTo(expectedFirstByte);
    }

    [Test]
    [Arguments(10, 10, "0000001010")]        // Version 1-9 Numeric: 10 bits
    [Arguments(123, 12, "000001111011")]     // Version 10-26 Numeric: 12 bits
    [Arguments(1234, 14, "00010011010010")]  // Version 27-40 Numeric: 14 bits
    [Arguments(25, 9, "000011001")]          // Version 1-9 Alphanumeric: 9 bits
    [Arguments(100, 11, "00001100100")]      // Version 10-26 Alphanumeric: 11 bits
    public async Task WriteCharacterCount_VariousBitLengths_ProducesCorrectBits(int count, int bitsLength, string expectedBits)
    {
        byte[] buffer = new byte[3];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteCharacterCount(count, bitsLength);

        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        await Assert.That(actual).IsEquivalentTo(expectedBits);
    }

    // WriteData

    [Test]
    [Arguments("1", "0001")] // 1 = 0001 (4 bits)
    [Arguments("12", "0001100")] // 12 = 0001100 (7 bits)
    [Arguments("123", "0001111011")] // 123 = 0001111011 (10 bits)
    [Arguments("12345", "00011110110101101")] // 12345 = 00011110110101101
    [Arguments("8675309", "110110001110000100101001")]            // Mixed
    [Arguments("0123456789", "0000001100010101100110101001101001")] // Border
    public async Task WriteNumericData_ProducesCorrectBits(string input, string expectedBits)
    {
        byte[] buffer = new byte[10];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteData(input, EncodingMode.Numeric, EciMode.Default, false);

        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        var expected = expectedBits.Replace("_", "");
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("AC-42", EciMode.Default, "0011100111011100111001000010")]  // ISO/IEC 18004
    [Arguments("AC-42", EciMode.Iso8859_1, "0011100111011100111001000010")]  // ISO/IEC 18004
    [Arguments("HELLO WORLD", EciMode.Default, "0110000101101111000110100010111001011011100010011010100001101")] // typical alphanumeric
    [Arguments("HELLO WORLD", EciMode.Iso8859_1, "0110000101101111000110100010111001011011100010011010100001101")] // typical alphanumeric
    [Arguments("A", EciMode.Default, "001010")]                          // 1 letter: 6 bit
    [Arguments("A", EciMode.Iso8859_1, "001010")]                          // 1 letter: 6 bit
    [Arguments("AB", EciMode.Default, "00111001101")]                    // 2 letters: 11 bit
    [Arguments("AB", EciMode.Iso8859_1, "00111001101")]                    // 2 letters: 11 bit
    public async Task WriteData_Alphanumeric_ProducesCorrectBits(string input, EciMode eci, string expectedBits)
    {
        byte[] buffer = new byte[8];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteData(input, EncodingMode.Alphanumeric, eci, false);

        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        var expected = expectedBits.Replace("_", "");
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    // should be same result as text is EciMode.Iso8859_1
    [Test]
    [Arguments("AC-42", EciMode.Default, "0100000101000011001011010011010000110010")]
    [Arguments("AC-42", EciMode.Iso8859_1, "0100000101000011001011010011010000110010")]
    [Arguments("HELLO WORLD", EciMode.Default, "0100100001000101010011000100110001001111001000000101011101001111010100100100110001000100")]
    [Arguments("HELLO WORLD", EciMode.Iso8859_1, "0100100001000101010011000100110001001111001000000101011101001111010100100100110001000100")]
    [Arguments("Hello", EciMode.Default, "0100100001100101011011000110110001101111")]
    [Arguments("Hello", EciMode.Iso8859_1, "0100100001100101011011000110110001101111")]
    public async Task WriteData_Byte_ProducesCorrectBits(string input, EciMode eci, string expectedBits)
    {
        byte[] buffer = new byte[12];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteData(input, EncodingMode.Byte, eci, false);

        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        var expected = expectedBits.Replace("_", "");
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    // should be same result as text is EciMode.Utf8
    [Test]
    [Arguments("Hello", EciMode.Default)]
    [Arguments("Hello", EciMode.Utf8)]
    [Arguments("こんにちは", EciMode.Default)]
    [Arguments("こんにちは", EciMode.Utf8)]
    [Arguments("🐈🐕🔑💫🚪あ感요Ц", EciMode.Default)]
    [Arguments("🐈🐕🔑💫🚪あ感요Ц", EciMode.Utf8)]
    public async Task WriteData_Byte_UTF8WithoutBOM(string input, EciMode eci)
    {
        byte[] buffer = new byte[32];

        // Expected
        var utf8Bytes = Encoding.UTF8.GetBytes(input);
        var expected = string.Concat(utf8Bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

        // to simulate automatic encoding mode detection, which sets EciMode
        var analysisResult = TextAnalyzer.Analyze(input, eci);
        // need detected as EncodingMode.Byte + EciMode.Utf8 beforehand.
        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteData(input, analysisResult.EncodingMode, analysisResult.EciMode, false);

        // Assert
        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        await Assert.That(analysisResult.EncodingMode).IsEqualTo(EncodingMode.Byte);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("Hello")]
    [Arguments("こんにちは")]
    [Arguments("🐈🐕🔑💫🚪あ感요Ц")]
    public async Task WriteData_Byte_UTF8WithBOM(string input)
    {
        byte[] buffer = new byte[34];

        // Expected
        // BOM (EF BB BF) + Hello (48 65 6C 6C 6F)
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var utf8Bytes = Encoding.UTF8.GetBytes(input);
        var expected = string.Concat(bom.Concat(utf8Bytes).Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

        // to simulate automatic encoding mode detection, which sets EciMode
        var analysisResult = TextAnalyzer.Analyze(input, EciMode.Utf8);

        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteData(input, analysisResult.EncodingMode, analysisResult.EciMode, true);

        var actual = ToBinaryString(encoder.GetEncodedData(), encoder.BitPosition);
        await Assert.That(analysisResult.EncodingMode).IsEqualTo(EncodingMode.Byte);
        await Assert.That(analysisResult.EciMode).IsEqualTo(EciMode.Utf8);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    // WritePadding

    [Test]
    public async Task WritePadding_FillsToTargetWithAlternatingBytes()
    {
        byte[] buffer = new byte[4];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Numeric, EciMode.Default); // 4 bits
        encoder.WritePadding(32); // Target: 32 bits (4 bytes)

        // Expected: 0001_0000           | 1110_1100 | 0001_0001 | 1110_1100
        //           Mode(4) + Term(4)   | Pad1(8)   | Pad2(8)   | Pad3(8)
        await Assert.That(buffer[0]).IsEqualTo((byte)0b_0001_0000);
        await Assert.That(buffer[1]).IsEqualTo((byte)0b_1110_1100);
        await Assert.That(buffer[2]).IsEqualTo((byte)0b_0001_0001);
        await Assert.That(buffer[3]).IsEqualTo((byte)0b_1110_1100);
    }

    [Test]
    public async Task WritePadding_WithByteAlignment_AddsZeros()
    {
        byte[] buffer = new byte[2];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Numeric, EciMode.Default); // 4 bits
        encoder.WriteCharacterCount(1, 10); // 10 bits
        encoder.WritePadding(16); // target 2 bytes

        // Expected: 0001_0000_0000_01   | 00_1110_1100
        //           Mode(4) + Count(10) | Align(2) + Pad(8 bits but only 6 bits space)
        await Assert.That(buffer[0]).IsEqualTo((byte)0b_0001_0000);
        await Assert.That(buffer[1]).IsEqualTo((byte)0b_0000_0100);
    }

    // Edge cases

    [Test]
    public async Task WriteData_EmptyString_ProducesEmptyOutput()
    {
        byte[] buffer = new byte[10];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteData("", EncodingMode.Numeric, EciMode.Default, false);
        var result = encoder.GetEncodedData();

        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("000")] // Leading zeros
    [Arguments("999")] // Max 3 digits
    [Arguments("0")]   // Single zero
    public async Task WriteData_Numeric_EdgeCases(string input)
    {
        byte[] buffer = new byte[10];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteData(input, EncodingMode.Numeric, EciMode.Default, false);
        var result = encoder.GetEncodedData();

        await Assert.That(result.Length > 0).IsTrue();
        // Verify all bytes contain valid bit patterns
    }

    [Test]
    public async Task WritePadding_AlreadyAtTarget_DoesNotModify()
    {
        byte[] buffer = new byte[5];
        var encoder = new QRBinaryEncoder(buffer);

        // Fill exactly 32 bits
        encoder.WriteMode(EncodingMode.Numeric, EciMode.Default); // 4 bits
        encoder.WriteCharacterCount(1234, 14); // 14 bits
        encoder.WriteData("12345", EncodingMode.Numeric, EciMode.Default, false); // 14 bits (3+2 digits)
                                                                                  // Total: 32 bits

        var beforePadding = encoder.GetEncodedData().ToArray();
        encoder.WritePadding(32);
        var afterPadding = encoder.GetEncodedData().ToArray();

        await Assert.That(afterPadding).IsEquivalentTo(beforePadding);
    }

    // Full pipeline parity (mode + count + data + padding)

    // ISO/IEC 18004 canonical example: "HELLO WORLD", alphanumeric, version 1-M
    // (16 data codewords). Encoded stream is the well-known byte sequence.
    [Test]
    public async Task EncodePipeline_HelloWorld_V1M_ProducesCanonicalCodewords()
    {
        byte[] buffer = new byte[16];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Alphanumeric, EciMode.Default);
        encoder.WriteCharacterCount(11, 9); // version 1-9 alphanumeric: 9 bits
        encoder.WriteData("HELLO WORLD", EncodingMode.Alphanumeric, EciMode.Default, false);
        encoder.WritePadding(16 * 8);

        var expected = new byte[]
        {
            0x20, 0x5B, 0x0B, 0x78, 0xD1, 0x72, 0xDC, 0x4D,
            0x43, 0x40, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11,
        };
        var byteCount = encoder.ByteCount;
        var encoded = encoder.GetEncodedData().ToArray();
        await Assert.That(byteCount).IsEqualTo(16);
        await Assert.That(encoded).IsEquivalentTo(expected);
    }

    // Exact capacity fit: 14 bytes data in version 1-M (16 data codewords)
    // leaves room for the 4-bit terminator only - zero pad bytes.
    [Test]
    public async Task EncodePipeline_ExactCapacityFit_NoPadBytes()
    {
        byte[] buffer = new byte[16];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Byte, EciMode.Default);
        encoder.WriteCharacterCount(14, 8); // version 1-9 byte: 8 bits
        encoder.WriteData("ABCDEFGHIJKLMN", EncodingMode.Byte, EciMode.Default, false);
        var bitPositionBeforePad = encoder.BitPosition;
        encoder.WritePadding(16 * 8);
        var bitPositionAfterPad = encoder.BitPosition;
        var byteCount = encoder.ByteCount;
        var data = encoder.GetEncodedData().ToArray();

        await Assert.That(bitPositionBeforePad).IsEqualTo(124);
        await Assert.That(bitPositionAfterPad).IsEqualTo(128);
        await Assert.That(byteCount).IsEqualTo(16);
        // last byte = low nibble of 'N' (0x4E -> 1110) + 4-bit terminator (0000)
        await Assert.That(data[15]).IsEqualTo((byte)0xE0);
    }

    // Regression: WritePadding with a target at or below the current position is a
    // no-op aside from flushing - no throw, no position advance, data intact.
    [Test]
    public async Task WritePadding_TargetBelowPosition_KeepsDataAndPosition()
    {
        byte[] buffer = new byte[8];
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(EncodingMode.Numeric, EciMode.Default); // 4 bits
        encoder.WriteCharacterCount(1234, 14); // 14 bits
        encoder.WriteData("12345", EncodingMode.Numeric, EciMode.Default, false); // 17 bits -> total 35

        var before = encoder.GetEncodedData().ToArray();
        encoder.WritePadding(32); // below current position (35 bits)
        var after = encoder.GetEncodedData().ToArray();

        var bitPosition = encoder.BitPosition;

        await Assert.That(after).IsEquivalentTo(before);
        await Assert.That(bitPosition).IsEqualTo(35);
    }

    // helpers

    private static string ToBinaryString(ReadOnlySpan<byte> data, int bitCount)
    {
        var binaryString = string.Concat(data.ToArray().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

        // Trim to actual bit count
        return binaryString.Substring(0, bitCount);
    }
}
