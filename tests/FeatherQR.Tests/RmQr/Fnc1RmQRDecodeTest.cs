using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The FNC1 rules of the rMQR bit stream decoder (ISO/IEC 23941 7.4.8: indicator <c>101</c> for first
/// position, <c>110</c> for second), pinned on hand-assembled streams at R7x43 (count widths 4 / 3 / 3 / 2)
/// with the exact bit count the matrix decoder would pass. The rules are the Standard QR ones: once,
/// before the first data segment, with ECI headers on either side, the application indicator held to its
/// ranges, and the <c>%</c> rule in Alphanumeric segments. The two indicators used to be refused as
/// reserved, which the record of this symbology stated without a source: zxing-cpp's and qrcode2's mode
/// tables, libzint's and CodeGlyphX's writers, and zxing-cpp reading their symbols as GS1 say otherwise.
/// </summary>
public class Fnc1RmQRDecodeTest
{
    private const string Gs = "\u001D";

    private static (DecodeStatus Status, string Text, Fnc1Mode Fnc1) Decode(string bits, int destinationLength = 64)
    {
        var clean = bits.Replace(" ", "");
        var data = new byte[(clean.Length + 7) / 8];
        for (var i = 0; i < clean.Length; i++)
        {
            if (clean[i] == '1')
                data[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        var destination = new char[destinationLength];
        var status = RmQRBinaryDecoder.DecodeBitStream(data, clean.Length, RmQRVersion.R7x43, destination, out var written, out var fnc1);
        return (status, new string(destination, 0, written), fnc1);
    }

    // Alphanumeric values at R7x43: 3-bit count, 11 bits a pair, 6 bits a single.
    private const string A = "001010";          // 10
    private const string Percent = "100110";    // 38
    private const string PairAPercent = "00111101000"; // 10 * 45 + 38 = 488
    private const string PairPercentA = "11010111000"; // 38 * 45 + 10 = 1720
    private const string PairAB = "00111001101";       // 10 * 45 + 11 = 461
    private const string PairPercentPercent = "11011010100"; // 38 * 45 + 38 = 1748
    private const string PairPercentB = "11010111001"; // 38 * 45 + 11 = 1721
    private const string B = "001011";          // 11
    private const string Kanji93FA = "0111000111010"; // 日: 0x93FA - 0x8140 = 0x12BA, 0x12 * 0xC0 + 0xBA = 3642

    [Test]
    public async Task FirstPosition_IsReadAndReported()
    {
        // 101 FNC1 first, 001 Numeric, count 1 (4 bits), the digit 7, 000 terminator.
        var (status, text, fnc1) = Decode("101 001 0001 0111 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
        await Assert.That(fnc1.IsSecondPosition).IsFalse();
        await Assert.That(fnc1.ApplicationIndicator).IsNull();
    }

    [Test]
    public async Task SecondPosition_ReportsTheIndicatorApartFromTheText()
    {
        // 110 FNC1 second, 37 in 8 bits, 010 Alphanumeric, count 1, B, terminator.
        var (status, text, fnc1) = Decode("110 00100101 010 001 " + B + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("B");
        await Assert.That(fnc1.IsSecondPosition).IsTrue();
        await Assert.That(fnc1.IsFirstPosition).IsFalse();
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    [Test]
    [Arguments("00000000", 0)]
    [Arguments("01100011", 99)]
    [Arguments("10100101", 165)]
    [Arguments("11011110", 222)]
    public async Task SecondPosition_EveryIndicatorForm_IsReportedAsItsCodeword(string indicatorBits, int codeword)
    {
        var (status, _, fnc1) = Decode("110 " + indicatorBits + " 001 0001 0111 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(codeword);
    }

    [Test]
    [Arguments("01100100")] // 100
    [Arguments("10100100")] // 164
    [Arguments("10111111")] // 191
    [Arguments("11000100")] // 196
    [Arguments("11011111")] // 223
    [Arguments("11111111")] // 255
    public async Task SecondPosition_IndicatorOutsideTheRanges_IsInvalidBitstream(string indicatorBits)
    {
        await Assert.That(Decode("110 " + indicatorBits + " 001 0001 0111 000").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>The indicator's 8 bits have to fit in the data bits: none left, and one short, are both truncated.</summary>
    [Test]
    public async Task SecondPosition_IndicatorCutOffByTheCapacity_IsInvalidBitstream()
    {
        await Assert.That(Decode("110").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
        await Assert.That(Decode("110 0010010").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>Fewer than three bits after the indicator is the shortened terminator, so the mode alone is a valid, empty stream.</summary>
    [Test]
    public async Task IndicatorAloneAtCapacity_IsReportedWithAnEmptyText()
    {
        var (status, text, fnc1) = Decode("101");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task FirstPosition_OnEitherSideOfEci_IsReported()
    {
        // 111 ECI, 26 (UTF-8), 011 Byte, count 2, C3 A9 (é), terminator. FNC1 before or after the ECI header.
        var afterEci = Decode("111 00011010 101 011 010 11000011 10101001 000");
        var beforeEci = Decode("101 111 00011010 011 010 11000011 10101001 000");

        await Assert.That(afterEci.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(afterEci.Text).IsEqualTo("é");
        await Assert.That(afterEci.Fnc1.IsFirstPosition).IsTrue();
        await Assert.That(beforeEci.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(beforeEci.Text).IsEqualTo("é");
        await Assert.That(beforeEci.Fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task SecondPosition_OnEitherSideOfEci_IsReported()
    {
        var afterEci = Decode("111 00011010 110 00100101 011 010 11000011 10101001 000");
        var beforeEci = Decode("110 00100101 111 00011010 011 010 11000011 10101001 000");

        await Assert.That(afterEci.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(afterEci.Text).IsEqualTo("é");
        await Assert.That(afterEci.Fnc1.ApplicationIndicator).IsEqualTo(37);
        await Assert.That(beforeEci.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(beforeEci.Text).IsEqualTo("é");
        await Assert.That(beforeEci.Fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    [Test]
    public async Task FirstPosition_AfterADataSegment_IsInvalidBitstream()
    {
        await Assert.That(Decode("001 0001 0111 101 001 0001 0111 000").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>rMQR skips an empty segment, which is still a data segment indicator, so an FNC1 behind it is late, in either position.</summary>
    [Test]
    [Arguments("101")]
    [Arguments("110 00100101")]
    public async Task Indicator_AfterAnEmptyDataSegment_IsInvalidBitstream(string indicator)
    {
        await Assert.That(Decode("001 0000 " + indicator + " 001 0001 0111 000").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    [Arguments("101", "101")]
    [Arguments("101", "110 00100101")]
    [Arguments("110 00100101", "101")]
    [Arguments("110 00100101", "110 00100101")]
    public async Task TwoIndicators_IsInvalidBitstream(string first, string second)
    {
        await Assert.That(Decode(first + " " + second + " 001 0001 0111 000").Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task FirstPosition_PercentInAlphanumeric_IsTheSeparator()
    {
        // 010 Alphanumeric, count 3: the pair A% then B.
        var (status, text, _) = Decode("101 010 011 " + PairAPercent + " " + B + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A" + Gs + "B");
    }

    [Test]
    public async Task FirstPosition_DoubledPercent_IsOneLiteral()
    {
        // count 4: A% then %B, which is A, %%, B.
        var (status, text, _) = Decode("101 010 100 " + PairAPercent + " " + PairPercentB + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A%B");
    }

    [Test]
    public async Task FirstPosition_TriplePercent_IsALiteralThenTheSeparator()
    {
        // count 5: A% %% B.
        var (status, text, _) = Decode("101 010 101 " + PairAPercent + " " + PairPercentPercent + " " + B + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A%" + Gs + "B");
    }

    [Test]
    public async Task FirstPosition_PercentAtASegmentStartOrEnd_IsTheSeparator()
    {
        // Count 2: the pair %A. Count 3: the pair AB then %.
        var atStart = Decode("101 010 010 " + PairPercentA + " 000");
        var atEnd = Decode("101 010 011 " + PairAB + " " + Percent + " 000");

        await Assert.That(atStart.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(atStart.Text).IsEqualTo(Gs + "A");
        await Assert.That(atEnd.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(atEnd.Text).IsEqualTo("AB" + Gs);
    }

    [Test]
    public async Task FirstPosition_QuadruplePercent_IsTwoLiterals()
    {
        var (status, text, _) = Decode("101 010 100 " + PairPercentPercent + " " + PairPercentPercent + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("%%");
    }

    /// <summary>The rule is applied within one segment: a % ending one segment and a % starting the next are two separators.</summary>
    [Test]
    public async Task FirstPosition_PercentAtASegmentEndAndTheNextStart_AreTwoSeparators()
    {
        var (status, text, _) = Decode("101 010 010 " + PairAPercent + " 010 010 " + PairPercentB + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A" + Gs + Gs + "B");
    }

    [Test]
    public async Task FirstPosition_KanjiSegment_IsUnchanged()
    {
        // 100 Kanji, 2-bit count 1, one 13-bit character.
        var (status, text, fnc1) = Decode("101 100 01 " + Kanji93FA + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("日");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task SecondPosition_PercentInAlphanumeric_FollowsTheSameRule()
    {
        var (status, text, fnc1) = Decode("110 00100101 010 011 " + PairAPercent + " " + B + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A" + Gs + "B");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    [Test]
    [Arguments("010 011 " + PairAPercent + " " + B, "A%B")]
    [Arguments("010 100 " + PairAPercent + " " + PairPercentB, "A%%B")]
    [Arguments("010 101 " + PairAPercent + " " + PairPercentPercent + " " + B, "A%%%B")]
    [Arguments("010 010 " + PairPercentA, "%A")]
    [Arguments("010 011 " + PairAB + " " + Percent, "AB%")]
    [Arguments("010 100 " + PairPercentPercent + " " + PairPercentPercent, "%%%%")]
    public async Task WithoutFnc1_PercentStaysLiteral(string segment, string expected)
    {
        var (status, text, fnc1) = Decode(segment + " 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
        await Assert.That(fnc1).IsEqualTo(default(Fnc1Mode));
    }

    /// <summary>A Byte segment carries the separator as 0x1D and a literal % as 0x25, under no ECI and under ECI 3 and 26.</summary>
    [Test]
    [Arguments("")]
    [Arguments("111 00000011 ")]
    [Arguments("111 00011010 ")]
    public async Task FirstPosition_ByteSegment_CarriesSeparatorAndPercentAsBytes(string eci)
    {
        // 011 Byte, count 2 (3 bits): 0x25 then 0x1D.
        var (status, text, fnc1) = Decode(eci + "101 011 010 00100101 00011101 000");

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("%" + Gs);
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    /// <summary>The destination is held to the text as it reads, which the rule only shrinks.</summary>
    [Test]
    public async Task DestinationSizedForTheCollapsedText_Succeeds()
    {
        var stream = "101 010 100 " + PairAPercent + " " + PairPercentB + " 000"; // A%%B reads A%B

        await Assert.That(Decode(stream, destinationLength: 3).Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(Decode(stream, destinationLength: 2).Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }
}
