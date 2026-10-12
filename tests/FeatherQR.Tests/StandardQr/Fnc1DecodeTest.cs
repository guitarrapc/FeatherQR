using System.Text;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The FNC1 rules of the Standard QR bit stream decoder (ISO/IEC 18004 6.4.8), pinned on hand-assembled
/// streams at version 1 so no image, matrix or encoder is in the loop. Both indicators are read and
/// reported on <see cref="Fnc1Mode"/>, the application indicator is read and held to the standard's
/// ranges, an indicator after a data segment or a second indicator is a malformed stream, and while a
/// mode is active a lone <c>%</c> in an Alphanumeric segment is the separator (U+001D) and <c>%%</c>
/// is one <c>%</c>, read left to right within the segment. The same texts without FNC1 stay literal.
/// Eight streams are zxing-cpp's own unit test vectors (six it reads, one it rejects, one it disabled), of which this
/// decoder reads five and refuses three (the late first-position one by decision 6, the rejected one and the disabled
/// one), so the bit layout is checked against an independent reader, and the two examples of the standard are
/// assembled from its description and pinned as bytes.
/// </summary>
public class Fnc1DecodeTest
{
    private const int Version = 1;
    private const int ModeTerminator = 0b0000;
    private const int ModeNumeric = 0b0001;
    private const int ModeAlphanumeric = 0b0010;
    private const int ModeStructuredAppend = 0b0011;
    private const int ModeByte = 0b0100;
    private const int ModeFnc1First = 0b0101;
    private const int ModeEci = 0b0111;
    private const int ModeKanji = 0b1000;
    private const int ModeFnc1Second = 0b1001;
    private const string Gs = "\u001D";
    private const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // The mode and how it is reported

    [Test]
    public async Task FirstPosition_ZxingCppVector_ReportsTheModeAndTheText()
    {
        // zxing-cpp QRDecodedBitStreamParserTest.SymbologyIdentifier: FNC1(1st) NUM(4) 2001 reads "]Q3" and "2001".
        var status = Decode([0x51, 0x01, 0x0C, 0x81, 0x00], out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("2001");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
        await Assert.That(fnc1.IsSecondPosition).IsFalse();
        await Assert.That(fnc1.ApplicationIndicator).IsNull();
    }

    [Test]
    public async Task NoFnc1_ReportsTheDefault()
    {
        var status = Decode(Build(Numeric("2001"), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("2001");
        await Assert.That(fnc1).IsEqualTo(default(Fnc1Mode));
        await Assert.That(fnc1.IsFirstPosition).IsFalse();
        await Assert.That(fnc1.IsSecondPosition).IsFalse();
        await Assert.That(fnc1.ApplicationIndicator).IsNull();
    }

    [Test]
    public async Task SecondPosition_ZxingCppVector_ReportsTheIndicatorApartFromTheText()
    {
        // FNC1(2nd) 99 (0x63) ANUM(1) A reads "]Q5" and "99A" in zxing-cpp, which puts the indicator in the text.
        var status = Decode([0x96, 0x32, 0x00, 0x94, 0x00], out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A");
        await Assert.That(fnc1.IsSecondPosition).IsTrue();
        await Assert.That(fnc1.IsFirstPosition).IsFalse();
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(99);
    }

    [Test]
    [Arguments(new byte[] { 0x9A, 0x52, 0x00, 0x96, 0x00 }, 165)] // A: 65 + 100
    [Arguments(new byte[] { 0x9C, 0x52, 0x00, 0x96, 0x00 }, 197)] // a: 97 + 100
    public async Task SecondPosition_LetterIndicator_IsReportedAsItsCodeword(byte[] data, int codeword)
    {
        var status = Decode(data, out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("B");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(codeword);
    }

    /// <summary>The indicator 00 is the value 0, not an absent value: <c>null</c> is for first position and no FNC1.</summary>
    [Test]
    public async Task SecondPosition_Indicator00_IsZeroNotAbsent()
    {
        var status = Decode(Build(Fnc1Second(0), Alphanumeric("AB"), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("AB");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(0);
    }

    [Test]
    [Arguments(99)]
    [Arguments(165)]
    [Arguments(190)]
    [Arguments(197)]
    [Arguments(222)]
    public async Task SecondPosition_EveryEdgeOfTheValidRanges_IsAccepted(int codeword)
    {
        var status = Decode(Build(Fnc1Second(codeword), Numeric("7"), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(codeword);
    }

    /// <summary>00-99 as a number, 165-190 for A-Z and 197-222 for a-z (ISO/IEC 18004 6.4.8.2). Everything between and above is malformed.</summary>
    [Test]
    [Arguments(100)]
    [Arguments(164)]
    [Arguments(191)]
    [Arguments(196)]
    [Arguments(223)]
    [Arguments(255)]
    public async Task SecondPosition_IndicatorOutsideTheRanges_IsInvalidBitstream(int codeword)
    {
        var status = Decode(Build(Fnc1Second(codeword), Numeric("7"), Terminator()), out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task SecondPosition_ZxingCppInvalidIndicatorVector_IsInvalidBitstream()
    {
        // FNC1(2nd) @ (0xA4 = 164) ANUM(1) B, which zxing-cpp rejects too.
        var status = Decode([0x9A, 0x42, 0x00, 0x96, 0x00], out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// The 8-bit indicator has to fit before the stream ends. Both shortfalls a byte-aligned stream
    /// can hold are pinned: no bits left after the mode indicator, and four.
    /// </summary>
    [Test]
    public async Task SecondPosition_IndicatorCutOff_IsInvalidBitstream()
    {
        // ECI (12 bits) and the indicator (4) fill two bytes: 0 bits for the indicator's value.
        var noBits = Build(Eci(26), [(ModeFnc1Second, 4)]);
        await Assert.That(noBits.Length * 8 - 16).IsEqualTo(0).Because("the stream must end on the mode indicator");
        await Assert.That(Decode(noBits, out _, out _)).IsEqualTo(DecodeStatus.InvalidBitstream);

        // Two ECI headers (24 bits) and the indicator (4) leave 4 bits of byte 4.
        var fourBits = Build(Eci(26), Eci(3), [(ModeFnc1Second, 4), (0, 4)]);
        await Assert.That(fourBits.Length * 8 - 28).IsEqualTo(4).Because("exactly four bits short of the 8-bit indicator");
        await Assert.That(Decode(fourBits, out _, out _)).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task SecondPosition_IndicatorThatEndsTheStream_IsReported()
    {
        // 12 bits in two bytes: the four zero bits of padding are the terminator.
        var status = Decode(Build(Fnc1Second(37)), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    // Placement: before the first data segment, on either side of the ECI and Structured Append headers

    [Test]
    public async Task FirstPosition_AfterEci_IsReported()
    {
        var status = Decode(Build(Eci(26), Fnc1First(), Bytes(0xC3, 0xA9), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("é");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task FirstPosition_BeforeEci_IsReported()
    {
        var status = Decode(Build(Fnc1First(), Eci(26), Bytes(0xC3, 0xA9), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("é");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task SecondPosition_OnEitherSideOfEci_IsReported()
    {
        var afterEci = Decode(Build(Eci(26), Fnc1Second(37), Bytes(0xC3, 0xA9), Terminator()), out var textAfter, out var fnc1After);
        var beforeEci = Decode(Build(Fnc1Second(37), Eci(26), Bytes(0xC3, 0xA9), Terminator()), out var textBefore, out var fnc1Before);

        await Assert.That(afterEci).IsEqualTo(DecodeStatus.Success);
        await Assert.That(textAfter).IsEqualTo("é");
        await Assert.That(fnc1After.ApplicationIndicator).IsEqualTo(37);
        await Assert.That(beforeEci).IsEqualTo(DecodeStatus.Success);
        await Assert.That(textBefore).IsEqualTo("é");
        await Assert.That(fnc1Before.ApplicationIndicator).IsEqualTo(37);
    }

    [Test]
    public async Task FirstPosition_AfterAStructuredAppendHeader_IsReportedWithTheHeader()
    {
        var status = Decode(Build(StructuredAppend(1, 2, 0x5A), Fnc1First(), Numeric("7"), Terminator()), out var text, out var header, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
        await Assert.That(header.Index).IsEqualTo(1);
        await Assert.That(header.Count).IsEqualTo(2);
    }

    [Test]
    public async Task StructuredAppendHeader_AfterFnc1_IsReportedWithTheMode()
    {
        var status = Decode(Build(Fnc1Second(37), StructuredAppend(0, 2, 0x5A), Numeric("7"), Terminator()), out var text, out var header, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("7");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
        await Assert.That(header.Index).IsEqualTo(0);
        await Assert.That(header.Count).IsEqualTo(2);
    }

    /// <summary>
    /// zxing-cpp reads this stream (NUM(4) 2001 FNC1(1st) NUM(3) 301) as "2001301" under "]Q3", against the
    /// standard. The mode changes how every Alphanumeric segment reads, so one that arrives after data is
    /// refused here, as a count that overruns the stream is.
    /// </summary>
    [Test]
    public async Task FirstPosition_AfterADataSegment_IsInvalidBitstream()
    {
        var status = Decode([0x10, 0x10, 0xC8, 0x15, 0x10, 0x0D, 0x2D, 0x00], out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>An empty segment is still a data segment indicator, so an FNC1 behind it is late too, in either position.</summary>
    [Test]
    [Arguments(ModeFnc1First)]
    [Arguments(ModeFnc1Second)]
    public async Task Indicator_AfterAnEmptyDataSegment_IsInvalidBitstream(int mode)
    {
        var status = Decode(Build([(ModeNumeric, 4), (0, 10)], Indicator(mode), Numeric("7"), Terminator()), out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task SecondPosition_AfterADataSegment_IsInvalidBitstream()
    {
        // BYTE(1) A FNC1(2nd) 99 BYTE(1) B, a zxing-cpp vector that zxing-cpp itself disabled as a violation.
        var status = Decode([0x40, 0x14, 0x19, 0x63, 0x40, 0x14, 0x20, 0x00], out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    [Arguments(ModeFnc1First, ModeFnc1First)]
    [Arguments(ModeFnc1First, ModeFnc1Second)]
    [Arguments(ModeFnc1Second, ModeFnc1First)]
    [Arguments(ModeFnc1Second, ModeFnc1Second)]
    public async Task TwoIndicators_IsInvalidBitstream(int first, int second)
    {
        var status = Decode(Build(Indicator(first), Indicator(second), Numeric("7"), Terminator()), out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    // The % rule in Alphanumeric segments (6.4.8.1): read left to right, %% first

    [Test]
    [Arguments("A%B", "A" + Gs + "B")]
    [Arguments("A%%B", "A%B")]
    [Arguments("A%%%B", "A%" + Gs + "B")]
    [Arguments("A%%%%B", "A%%B")]
    [Arguments("%AB", Gs + "AB")]
    [Arguments("AB%", "AB" + Gs)]
    [Arguments("%", Gs)]
    [Arguments("%%", "%")]
    [Arguments("%%%", "%" + Gs)]
    [Arguments("%%%%", "%%")]
    [Arguments("12%34", "12" + Gs + "34")]
    public async Task FirstPosition_PercentInAlphanumeric_IsTheSeparatorAndDoubledIsLiteral(string written, string expected)
    {
        var status = Decode(Build(Fnc1First(), Alphanumeric(written), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    [Arguments("A%B")]
    [Arguments("A%%B")]
    [Arguments("A%%%B")]
    [Arguments("A%%%%B")]
    [Arguments("%AB")]
    [Arguments("AB%")]
    [Arguments("%")]
    [Arguments("%%")]
    [Arguments("%%%")]
    [Arguments("%%%%")]
    [Arguments("12%34")]
    public async Task WithoutFnc1_PercentStaysLiteral(string written)
    {
        var status = Decode(Build(Alphanumeric(written), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(written);
        await Assert.That(fnc1).IsEqualTo(default(Fnc1Mode));
    }

    [Test]
    public async Task SecondPosition_PercentInAlphanumeric_FollowsTheSameRule()
    {
        var status = Decode(Build(Fnc1Second(37), Alphanumeric("A%%%B"), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A%" + Gs + "B");
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    [Test]
    public async Task PercentRule_ZxingCppVector_TriplePercentReadsPercentThenSeparator()
    {
        // zxing-cpp QRDecodedBitStreamParserTest.GS1PercentGS: FNC1(1st) A(11) 9112%%%2012.
        var status = Decode([0x52, 0x05, 0x99, 0x60, 0x5F, 0xB5, 0x35, 0x80, 0x01, 0x08, 0x00, 0xEC, 0x11], out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("9112%" + Gs + "2012");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    /// <summary>The rule is applied within one segment: a % that ends one segment and a % that starts the next are two separators, not one literal.</summary>
    [Test]
    public async Task PercentAtASegmentEndAndTheNextStart_AreTwoSeparators()
    {
        var status = Decode(Build(Fnc1First(), Alphanumeric("A%"), Alphanumeric("%B"), Terminator()), out var text, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A" + Gs + Gs + "B");
    }

    /// <summary>A Byte segment carries the separator as 0x1D and a literal % as 0x25, whatever the charset (6.4.8.1).</summary>
    [Test]
    [Arguments(null)]
    [Arguments(3)]
    [Arguments(26)]
    public async Task FirstPosition_ByteSegment_CarriesSeparatorAndPercentAsBytes(int? eci)
    {
        var header = eci is int assignment ? Eci(assignment) : [];
        var status = Decode(Build(header, Fnc1First(), Bytes(0x41, 0x25, 0x1D, 0x42), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A%" + Gs + "B");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    [Test]
    public async Task FirstPosition_NumericAndKanjiSegments_AreUnchanged()
    {
        var status = Decode(Build(Fnc1First(), Numeric("0123"), Kanji(0x93FA), Terminator()), out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("0123日");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
    }

    // The two examples of the standard, assembled from its description

    /// <summary>
    /// ISO/IEC 18004:2006 6.4.8.1 example 1: FNC1 in first position, the 29 digits 01049123451234591597033130128 in
    /// Numeric mode, then %10ABC123 in Alphanumeric mode, transmitted as ]Q3 01049123451234591597033130128 GS 10ABC123.
    /// The stream is assembled from that description with this file's helpers and pinned as bytes, so a helper change
    /// shows here rather than moving the decoder's expectation with it.
    /// </summary>
    [Test]
    public async Task IsoExample_FirstPosition_ReadsTheTransmittedElementString()
    {
        var data = Build(Fnc1First(), Numeric("01049123451234591597033130128"), Alphanumeric("%10ABC123"), Terminator());
        await Assert.That(Convert.ToHexString(data)).IsEqualTo("5107 40A7 ACEA 8015 9E4F CA52 D2D3 8409 D5E0 28FD 82F0 C0".Replace(" ", ""))
            .Because("0101 FNC1, 0001 Numeric, the 10-bit count 29, nine 10-bit and one 7-bit digit group, 0010 Alphanumeric, the 9-bit count 9, four 11-bit pairs and one 6-bit character, 0000");

        var status = Decode(data, out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("01049123451234591597033130128" + Gs + "10ABC123");
        await Assert.That(fnc1.IsFirstPosition).IsTrue();
        await Assert.That(fnc1.ApplicationIndicator).IsNull();
    }

    /// <summary>
    /// ISO/IEC 18004:2006 6.4.8.2 example: FNC1 in second position with the application indicator 37, AA1234BBB112 in
    /// Alphanumeric mode and "text text text text" with a carriage return (the standard prints it as <CR>) in Byte mode, transmitted as ]Q5 37 and the data.
    /// </summary>
    [Test]
    public async Task IsoExample_SecondPosition_ReadsTheIndicatorAndTheData()
    {
        var data = Build(Fnc1Second(37), Alphanumeric("AA1234BBB112"), Ascii("text text text text\r"), Terminator());
        await Assert.That(Convert.ToHexString(data)).IsEqualTo("9252 061C C05E 22CF D1F0 05E8 28E8 CAF0 E840 E8CA F0E8 40E8 CAF0 E840 E8CA F0E8 1A00".Replace(" ", ""))
            .Because("1001 FNC1 second, 00100101 the indicator 37, 0010 Alphanumeric, the 9-bit count 12, six 11-bit pairs, 0100 Byte, the 8-bit count 20, twenty bytes, 0000");

        var status = Decode(data, out var text, out var fnc1);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("AA1234BBB112text text text text\r");
        await Assert.That(fnc1.IsSecondPosition).IsTrue();
        await Assert.That(fnc1.ApplicationIndicator).IsEqualTo(37);
    }

    // Destination sizing: the text shrinks under the rule, and the destination is held to what is written

    [Test]
    public async Task DestinationSizedForTheCollapsedText_Succeeds()
    {
        var status = Decode(Build(Fnc1First(), Alphanumeric("A%%B"), Terminator()), out var text, out _, destinationLength: 3);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("A%B");
    }

    [Test]
    public async Task DestinationOneShortOfTheCollapsedText_IsDestinationTooSmall()
    {
        var status = Decode(Build(Fnc1First(), Alphanumeric("A%%B"), Terminator()), out _, out _, destinationLength: 2);

        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task TrailingSeparator_NeedsItsOwnCharacter()
    {
        var data = Build(Fnc1First(), Alphanumeric("AB%"), Terminator());

        await Assert.That(Decode(data, out _, out _, destinationLength: 2)).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(Decode(data, out var text, out _, destinationLength: 3)).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("AB" + Gs);
    }

    // The value type

    [Test]
    public async Task Fnc1Mode_EqualityFollowsTheMode()
    {
        await Assert.That(Fnc1Mode.FirstPosition).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Fnc1Mode.SecondPosition(37)).IsEqualTo(Fnc1Mode.SecondPosition(37));
        await Assert.That(Fnc1Mode.SecondPosition(37)).IsNotEqualTo(Fnc1Mode.SecondPosition(38));
        await Assert.That(Fnc1Mode.SecondPosition(37)).IsNotEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Fnc1Mode.FirstPosition).IsNotEqualTo(default(Fnc1Mode));
        await Assert.That(Fnc1Mode.SecondPosition(0).ApplicationIndicator).IsEqualTo(0);
    }

    // Helpers

    private static DecodeStatus Decode(byte[] data, out string text, out Fnc1Mode fnc1, int destinationLength = 128)
        => Decode(data, out text, out _, out fnc1, destinationLength);

    private static DecodeStatus Decode(byte[] data, out string text, out QRStructuredAppend header, out Fnc1Mode fnc1, int destinationLength = 128)
    {
        var destination = new char[destinationLength];
        var status = QRBinaryDecoder.DecodeBitStream(data, Version, destination, out var charsWritten, out header, out fnc1);
        text = new string(destination, 0, charsWritten);
        return status;
    }

    private static IEnumerable<(int Value, int Bits)> Indicator(int mode) => mode == ModeFnc1First ? Fnc1First() : Fnc1Second(37);

    private static IEnumerable<(int Value, int Bits)> Fnc1First() => [(ModeFnc1First, 4)];

    private static IEnumerable<(int Value, int Bits)> Fnc1Second(int indicator) => [(ModeFnc1Second, 4), (indicator, 8)];

    private static IEnumerable<(int Value, int Bits)> Eci(int assignment) => [(ModeEci, 4), (assignment, 8)];

    private static IEnumerable<(int Value, int Bits)> StructuredAppend(int index, int count, int parity) => [(ModeStructuredAppend, 4), (index, 4), (count - 1, 4), (parity, 8)];

    private static IEnumerable<(int Value, int Bits)> Terminator() => [(ModeTerminator, 4)];

    /// <summary>A Numeric segment at the version 1 count width (10 bits): 10 bits per three digits, 7 per two, 4 for one.</summary>
    private static IEnumerable<(int Value, int Bits)> Numeric(string digits)
    {
        yield return (ModeNumeric, 4);
        yield return (digits.Length, 10);
        var i = 0;
        for (; i + 3 <= digits.Length; i += 3)
            yield return (int.Parse(digits.Substring(i, 3)), 10);
        if (digits.Length - i == 2)
            yield return (int.Parse(digits.Substring(i, 2)), 7);
        else if (digits.Length - i == 1)
            yield return (digits[i] - '0', 4);
    }

    /// <summary>An Alphanumeric segment at the version 1 count width (9 bits): 11 bits per pair, 6 for an odd last character. Written as given: a % is a %.</summary>
    private static IEnumerable<(int Value, int Bits)> Alphanumeric(string text)
    {
        yield return (ModeAlphanumeric, 4);
        yield return (text.Length, 9);
        var i = 0;
        for (; i + 2 <= text.Length; i += 2)
            yield return (AlphanumericChars.IndexOf(text[i]) * 45 + AlphanumericChars.IndexOf(text[i + 1]), 11);
        if (i < text.Length)
            yield return (AlphanumericChars.IndexOf(text[i]), 6);
    }

    /// <summary>A Byte segment at the version 1 count width (8 bits).</summary>
    private static IEnumerable<(int Value, int Bits)> Bytes(params byte[] bytes)
    {
        yield return (ModeByte, 4);
        yield return (bytes.Length, 8);
        foreach (var b in bytes)
            yield return (b, 8);
    }

    private static IEnumerable<(int Value, int Bits)> Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    /// <summary>A one-character Kanji segment at the version 1 count width (8 bits), compacted as ISO/IEC 18004 8.4.5 does.</summary>
    private static IEnumerable<(int Value, int Bits)> Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return [(ModeKanji, 4), (1, 8), (((shifted >> 8) * 0xC0) + (shifted & 0xFF), 13)];
    }

    /// <summary>Packs the fields MSB-first; the last byte is zero-padded, which reads as the terminator.</summary>
    private static byte[] Build(params IEnumerable<(int Value, int Bits)>[] parts)
    {
        var fields = parts.SelectMany(p => p).ToList();
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
