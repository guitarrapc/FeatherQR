using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The three bitstream decoders writing a symbol's bytes instead of its text: every mode as the bytes the standard defines, no charset applied,
/// the first ECI assignment reported in both outputs, and a stream whose Byte and Kanji segments fall under two assignments refused.
/// In the bytes output each char holds one byte.
/// </summary>
public class BitStreamBytesTest
{
    private const int QrVersion = 1;

    // Standard QR mode indicators (ISO/IEC 18004 Table 2), version 1 count widths: Numeric 10, Alphanumeric 9, Byte 8, Kanji 8
    private const int Numeric = 0b0001;
    private const int Alphanumeric = 0b0010;
    private const int Byte = 0b0100;
    private const int Fnc1First = 0b0101;
    private const int Eci = 0b0111;
    private const int Kanji = 0b1000;
    private const int Terminator = 0b0000;

    // Kanji-mode values: (lead - 0x81 or - 0xC1) * 0xC0 + (trail - 0x40)
    private const int KanjiA = 288;           // 0x82A0, あ
    private const int KanjiCircledOne = 1152; // 0x8740, a CP932 cell outside JIS X 0208
    private const int KanjiBadTrail = 189;    // low byte 0xBD after the subtraction, which no trail byte gives

    // Standard QR

    [Test]
    public async Task Qr_EveryMode_GivesTheBytesTheStandardDefines()
    {
        var data = Bits(
            (Numeric, 4), (2, 10), (12, 7),
            (Alphanumeric, 4), (1, 9), (10, 6),
            (Byte, 4), (2, 8), (0xFF, 8), (0x00, 8),
            (Kanji, 4), (1, 8), (KanjiA, 13),
            (Terminator, 4));

        var (status, bytes, eci) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { (byte)'1', (byte)'2', (byte)'A', 0xFF, 0x00, 0x82, 0xA0 }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(-1);
    }

    [Test]
    public async Task Qr_ByteOrderMark_IsKeptInTheBytes()
    {
        var data = Bits((Eci, 4), (26, 8), (Byte, 4), (4, 8), (0xEF, 8), (0xBB, 8), (0xBF, 8), ('A', 8), (Terminator, 4));

        var (textStatus, text, _) = QrText(data);
        await Assert.That(textStatus).IsEqualTo(DecodeStatus.Success).Because("the premise: the text drops the mark");
        await Assert.That(text).IsEqualTo("A");

        var (status, bytes, eci) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A' }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(26);
    }

    [Test]
    public async Task Qr_IllFormedUtf8UnderEci26_IsKeptAsWritten()
    {
        var data = Bits((Eci, 4), (26, 8), (Byte, 4), (2, 8), (0xC3, 8), (0x28, 8), (Terminator, 4));

        var (_, text, _) = QrText(data);
        await Assert.That(text).IsEqualTo("�(").Because("the premise: the text substitutes the ill-formed byte");

        var (status, bytes, _) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xC3, 0x28 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(899)]
    [Arguments(16383)]
    [Arguments(16384)]
    [Arguments(999999)]
    public async Task Qr_AssignmentWithoutACharset_IsReadAsBytes_AndReportedByBoth(int assignment)
    {
        var data = Bits([(Eci, 4), Designator(assignment), (Byte, 4), (2, 8), (0x80, 8), (0x41, 8), (Terminator, 4)]);

        var (textStatus, _, textEci) = QrText(data);
        await Assert.That(textStatus).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(textEci).IsEqualTo(assignment);

        var (status, bytes, eci) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x80, 0x41 }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(assignment);
    }

    [Test]
    public async Task Qr_KanjiCellOutsideJisX0208_GivesItsPair()
    {
        var data = Bits((Kanji, 4), (1, 8), (KanjiCircledOne, 13), (Terminator, 4));

        var (textStatus, _, _) = QrText(data);
        await Assert.That(textStatus).IsEqualTo(DecodeStatus.UnmappedCharacter).Because("the premise: the text has no reading for the cell");

        var (status, bytes, _) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x87, 0x40 }, CollectionOrdering.Matching);
    }

    /// <summary>The two lead-byte ranges meet between 0x9FFC and 0xE040, where the value's high part passes 0x1E.</summary>
    [Test]
    [Arguments(0, 0x81, 0x40)]
    [Arguments(30 * 0xC0 + 0xBC, 0x9F, 0xFC)]
    [Arguments(31 * 0xC0, 0xE0, 0x40)]
    [Arguments(42 * 0xC0 + 0x7F, 0xEB, 0xBF)]
    public async Task Qr_KanjiValue_GivesItsPair_InBothLeadByteRanges(int value, int lead, int trail)
    {
        var data = Bits((Kanji, 4), (1, 8), (value, 13), (Terminator, 4));

        var (status, bytes, _) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new[] { (byte)lead, (byte)trail }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Qr_KanjiValueNoPairExpresses_IsInvalidBitstream()
    {
        var data = Bits((Kanji, 4), (1, 8), (KanjiBadTrail, 13), (Terminator, 4));

        var (status, _, _) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Qr_KanjiUnderEci26_GivesThePairs_WhereTheTextReadsJisX0208()
    {
        var data = Bits((Eci, 4), (26, 8), (Kanji, 4), (1, 8), (KanjiA, 13), (Terminator, 4));

        var (_, text, _) = QrText(data);
        await Assert.That(text).IsEqualTo("あ");

        var (status, bytes, eci) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x82, 0xA0 }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(26);
    }

    [Test]
    public async Task Qr_ByteSegmentsUnderTwoAssignments_AreRefused_WhereTheTextReadsThem()
    {
        var data = Bits(
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('A', 8),
            (Eci, 4), (3, 8), (Byte, 4), (1, 8), ('B', 8),
            (Terminator, 4));

        var (textStatus, text, textEci) = QrText(data);
        await Assert.That(textStatus).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("AB");
        await Assert.That(textEci).IsEqualTo(26);

        var (status, _, eci) = QrBytes(data);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(eci).IsEqualTo(26);
    }

    [Test]
    public async Task Qr_UndeclaredByteSegmentThenAnAssignment_IsRefused()
    {
        var data = Bits(
            (Byte, 4), (1, 8), ('A', 8),
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('B', 8),
            (Terminator, 4));

        var (status, _, _) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnsupportedContent);
    }

    [Test]
    public async Task Qr_KanjiAndByteUnderTwoAssignments_AreRefused()
    {
        var data = Bits(
            (Kanji, 4), (1, 8), (KanjiA, 13),
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('B', 8),
            (Terminator, 4));

        var (status, _, _) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnsupportedContent);
    }

    [Test]
    public async Task Qr_SameAssignmentDeclaredTwice_IsRead()
    {
        var data = Bits(
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('A', 8),
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('B', 8),
            (Terminator, 4));

        var (status, bytes, eci) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo("AB"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(26);
    }

    [Test]
    public async Task Qr_NumericAndAlphanumericBeforeTheAssignment_DoNotCount()
    {
        var data = Bits(
            (Numeric, 4), (2, 10), (12, 7),
            (Alphanumeric, 4), (1, 9), (10, 6),
            (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('B', 8),
            (Terminator, 4));

        var (status, bytes, eci) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo("12AB"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(26);
    }

    [Test]
    public async Task Qr_Fnc1Alphanumeric_GivesTheSeparatorAsGs()
    {
        // "A%B%%C" under FNC1 is A, GS, B, %, C
        var data = Bits(
            (Fnc1First, 4),
            (Alphanumeric, 4), (6, 9), (10 * 45 + 38, 11), (11 * 45 + 38, 11), (38 * 45 + 12, 11),
            (Terminator, 4));

        var (status, bytes, _) = QrBytes(data);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { (byte)'A', 0x1D, (byte)'B', (byte)'%', (byte)'C' }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Qr_TextReportsTheFirstAssignment_OrNoneWithoutOne()
    {
        var none = QrText(Bits((Byte, 4), (1, 8), ('A', 8), (Terminator, 4)));
        var two = QrText(Bits((Eci, 4), (3, 8), (Eci, 4), (26, 8), (Byte, 4), (1, 8), ('A', 8), (Terminator, 4)));

        await Assert.That(none.Eci).IsEqualTo(-1);
        await Assert.That(two.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(two.Eci).IsEqualTo(3);
    }

    [Test]
    [Arguments(Byte, 1)]
    [Arguments(Kanji, 1)]
    public async Task Qr_DestinationOneShort_IsDestinationTooSmall(int mode, int destinationLength)
    {
        var data = mode == Byte
            ? Bits((Byte, 4), (2, 8), (0x80, 8), (0x81, 8), (Terminator, 4))
            : Bits((Kanji, 4), (1, 8), (KanjiA, 13), (Terminator, 4));

        var (status, _, _) = QrBytes(data, destinationLength);

        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    [Arguments(Byte)]
    [Arguments(Kanji)]
    public async Task Qr_SegmentPastTheStream_IsInvalidBitstream_EvenIntoNoRoom(int mode)
    {
        // The count says two, the stream ends after one: malformed, which outranks a destination with no room
        var data = mode == Byte
            ? Bits((Byte, 4), (2, 8), (0x80, 8))
            : Bits((Kanji, 4), (2, 8), (KanjiA, 13));

        var (status, _, _) = QrBytes(data, destinationLength: 0);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    // rMQR

    [Test]
    public async Task RmQr_EveryMode_GivesTheBytesTheStandardDefines()
    {
        var version = RmQRVersion.R13x99;
        var data = Bits(
            (0b001, 3), (2, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)), (12, 7),
            (0b010, 3), (1, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric)), (10, 6),
            (0b011, 3), (2, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)), (0xFF, 8), (0x00, 8),
            (0b100, 3), (1, RmQRConstants.GetKanjiCountIndicatorLength(version)), (KanjiA, 13),
            (0b000, 3));

        var (status, bytes, eci) = RmQrBytes(data, version);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { (byte)'1', (byte)'2', (byte)'A', 0xFF, 0x00, 0x82, 0xA0 }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(-1);
    }

    [Test]
    [Arguments(4)]
    [Arguments(899)]
    public async Task RmQr_AssignmentWithoutACharset_IsReadAsBytes_AndReportedByBoth(int assignment)
    {
        var version = RmQRVersion.R13x99;
        var data = Bits([(0b111, 3), Designator(assignment), (0b011, 3), (1, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)), (0x80, 8), (0b000, 3)]);

        var (textStatus, textEci) = RmQrText(data, version);
        await Assert.That(textStatus).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(textEci).IsEqualTo(assignment);

        var (status, bytes, eci) = RmQrBytes(data, version);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x80 }, CollectionOrdering.Matching);
        await Assert.That(eci).IsEqualTo(assignment);
    }

    [Test]
    public async Task RmQr_ByteSegmentsUnderTwoAssignments_AreRefused()
    {
        var version = RmQRVersion.R13x99;
        var byteCount = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var data = Bits(
            (0b111, 3), (26, 8), (0b011, 3), (1, byteCount), ('A', 8),
            (0b111, 3), (3, 8), (0b011, 3), (1, byteCount), ('B', 8),
            (0b000, 3));

        var (status, _, eci) = RmQrBytes(data, version);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(eci).IsEqualTo(26);
    }

    // Micro QR

    [Test]
    public async Task MicroQr_EveryMode_GivesTheBytesTheStandardDefines()
    {
        var version = MicroQRVersion.M4;
        var data = Bits(
            (0, 3), (2, MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)), (12, 7),
            (1, 3), (1, MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric)), (10, 6),
            (2, 3), (2, MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)), (0xC3, 8), (0x28, 8),
            (3, 3), (1, MicroQRConstants.GetKanjiCountIndicatorLength(version)), (KanjiCircledOne, 13),
            (0, 3), (0, MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)));

        var destination = new char[64];
        var status = MicroQRBinaryDecoder.DecodeBitStream(data, data.Length * 8, version, destination, out var written, bytes: true);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(Narrow(destination, written)).IsEquivalentTo(new byte[] { (byte)'1', (byte)'2', (byte)'A', 0xC3, 0x28, 0x87, 0x40 }, CollectionOrdering.Matching);
    }

    // Helpers

    private static (int Value, int Bits) Designator(int assignment) => assignment switch
    {
        < 128 => (assignment, 8),
        < 16384 => ((0b10 << 14) | assignment, 16),
        _ => ((0b110 << 21) | assignment, 24),
    };

    private static (DecodeStatus Status, byte[] Bytes, int Eci) QrBytes(byte[] data, int destinationLength = 64)
    {
        var destination = new char[destinationLength];
        var status = QRBinaryDecoder.DecodeBitStream(data, QrVersion, destination, out var written, out _, out _, out var eci, bytes: true);
        return (status, Narrow(destination, written), eci);
    }

    private static (DecodeStatus Status, string Text, int Eci) QrText(byte[] data)
    {
        var destination = new char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, QrVersion, destination, out var written, out _, out _, out var eci);
        return (status, new string(destination, 0, written), eci);
    }

    private static (DecodeStatus Status, byte[] Bytes, int Eci) RmQrBytes(byte[] data, RmQRVersion version)
    {
        var destination = new char[64];
        var status = RmQRBinaryDecoder.DecodeBitStream(data, data.Length * 8, version, destination, out var written, out _, out var eci, bytes: true);
        return (status, Narrow(destination, written), eci);
    }

    private static (DecodeStatus Status, int Eci) RmQrText(byte[] data, RmQRVersion version)
    {
        var status = RmQRBinaryDecoder.DecodeBitStream(data, data.Length * 8, version, new char[64], out _, out _, out var eci);
        return (status, eci);
    }

    /// <summary>Each char of a bytes decode holds one byte. The checked cast fails a test that finds one above 0xFF.</summary>
    private static byte[] Narrow(char[] chars, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
            bytes[i] = checked((byte)chars[i]);
        return bytes;
    }

    private static byte[] Bits(params (int Value, int Bits)[] fields)
    {
        var buffer = new byte[64];
        var writer = new BitWriter(buffer);
        foreach (var (value, bits) in fields)
            writer.Write(value, bits);
        writer.Flush();
        return writer.GetData().ToArray();
    }
}
