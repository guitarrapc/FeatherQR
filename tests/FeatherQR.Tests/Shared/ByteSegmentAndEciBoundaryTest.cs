using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The byte-segment charset resolution and ECI designator parsing in
/// <see cref="SegmentDecoders"/>: the destination guard on the Latin-1 path, the
/// value masks of all three ECI designator forms, and every rule in the strict
/// UTF-8 validator that decides whether a payload is read as UTF-8 or widened as
/// ISO-8859-1.
/// </summary>
/// <remarks>
/// <para>
/// A mutation sweep found all of these unpinned: the production logic is right,
/// but weakening any one of them passed the whole suite. Two are behaviour-visible
/// rather than merely uncovered — the Latin-1 destination guard turns into an
/// <see cref="IndexOutOfRangeException"/> escaping a bool-returning <c>TryDecode</c>,
/// and a wrong ECI mask turns a symbol that should be rejected into a `Success` with
/// text decoded under the wrong charset.
/// </para>
/// <para>
/// The UTF-8 cases all pivot on the same observable: an invalid sequence makes
/// <c>IsValidUtf8</c> return false, so the payload falls through to the ISO-8859-1
/// widening and each byte becomes the same-numbered code point. A validator that
/// wrongly accepts the sequence produces UTF-8 output instead, which is a different
/// string — that difference is what these tests assert.
/// </para>
/// </remarks>
public class ByteSegmentAndEciBoundaryTest
{
    private static (DecodeStatus Status, string Text) DecodeBytes(byte[] payload, ByteSegmentCharset charset, int destinationLength = 64)
    {
        var reader = new BitReader(payload);
        var destination = new char[destinationLength];
        var charsWritten = 0;
        var status = SegmentDecoders.DecodeBytePayload(ref reader, payload.Length * 8, payload.Length, charset, new byte[64], destination, ref charsWritten);
        return (status, new string(destination, 0, charsWritten));
    }

    // 1. Latin-1 destination guard.

    /// <summary>
    /// The Latin-1 path widens one byte to one char, so it needs exactly as many
    /// destination slots as bytes. One short is <c>DestinationTooSmall</c>; a guard
    /// weakened by one writes past the caller's span instead.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4)] // one short of the five bytes
    public async Task Latin1Payload_DestinationOneShort_ReportsDestinationTooSmall(int destinationLength)
    {
        var (status, _) = DecodeBytes([0xE9, 0xE8, 0xE7, 0xE6, 0xE5], ByteSegmentCharset.Iso8859_1, destinationLength);

        await Assert.That(status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>The matching positive: exactly enough room succeeds.</summary>
    [Test]
    public async Task Latin1Payload_DestinationExactlyBigEnough_Succeeds()
    {
        var (status, text) = DecodeBytes([0xE9, 0xE8, 0xE7, 0xE6, 0xE5], ByteSegmentCharset.Iso8859_1, destinationLength: 5);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("éèçæå");
    }

    // 2-4. ECI designator value masks.

    private static (DecodeStatus Status, int Eci) ReadEci(params byte[] designator)
    {
        var reader = new BitReader(designator);
        var status = SegmentDecoders.ReadEciDesignator(ref reader, designator.Length * 8, out var eci);
        return (status, eci);
    }

    /// <summary>
    /// Each designator form carries its value in a different number of payload bits
    /// (7, 14 and 21), and the mask that strips the form prefix has to match. A mask
    /// that takes too few bits silently yields a DIFFERENT, valid-looking assignment
    /// number — 8218 read as 26 means a symbol that should be rejected is decoded as
    /// UTF-8 instead.
    /// </summary>
    [Test]
    [Arguments(new byte[] { 0x40 }, 64)]                   // one byte: 0xxxxxxx
    [Arguments(new byte[] { 0x00 }, 0)]                    // one byte, lowest
    [Arguments(new byte[] { 0x7F }, 127)]                  // one byte, highest
    [Arguments(new byte[] { 0xA0, 0x1A }, 8218)]           // two bytes: 10xxxxxx, mask 0x3F
    [Arguments(new byte[] { 0x80, 0x00 }, 0)]              // two bytes, lowest
    [Arguments(new byte[] { 0xBF, 0xFF }, 16383)]          // two bytes, highest
    [Arguments(new byte[] { 0xD0, 0x00, 0x1A }, 1048602)]  // three bytes: 110xxxxx, mask 0x1F
    [Arguments(new byte[] { 0xC0, 0x00, 0x00 }, 0)]        // three bytes, lowest
    [Arguments(new byte[] { 0xDF, 0xFF, 0xFF }, 2097151)]  // three bytes, highest
    public async Task EciDesignator_CarriesItsFullValue(byte[] designator, int expected)
    {
        var (status, eci) = ReadEci(designator);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(eci).IsEqualTo(expected);
    }

    /// <summary>A leading <c>111</c> is not a defined designator form.</summary>
    [Test]
    [Arguments(new byte[] { 0xE0, 0x00, 0x00, 0x00 })]
    [Arguments(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    public async Task EciDesignator_UndefinedForm_ReportsInvalidBitstream(byte[] designator)
    {
        await Assert.That(ReadEci(designator).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    /// <summary>
    /// The behaviour-visible half of the mask rule, through the Standard QR decoder:
    /// ECI 8218 is a charset this library does not map, so the symbol is rejected. A
    /// mask that dropped the high bits would read it as ECI 26 and hand back text.
    /// </summary>
    [Test]
    public async Task EciWithAHighAssignmentNumber_IsRejectedRatherThanReadAsUtf8()
    {
        // ECI designator A0 1A = 8218, then a byte segment holding UTF-8 "あ".
        var data = Build(
            (0b0111, 4), (0xA0, 8), (0x1A, 8),
            (0b0100, 4), (3, 8), (0xE3, 8), (0x81, 8), (0x82, 8),
            (0b0000, 4));

        Span<char> destination = stackalloc char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, 1, destination, out var charsWritten);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(charsWritten).IsEqualTo(0);
    }

    // 5. Strict UTF-8 validation.

    /// <summary>
    /// An explicit UTF-8 ECI declaration is obeyed even when the bytes are not valid
    /// UTF-8: the declaration is the symbol's own statement about its charset, and the
    /// decoder substitutes replacement characters rather than second-guessing it into
    /// Latin-1.
    /// </summary>
    [Test]
    public async Task DeclaredUtf8_WithInvalidBytes_SubstitutesRatherThanFallingBackToLatin1()
    {
        var (status, text) = DecodeBytes([0xFF], ByteSegmentCharset.Utf8);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("�");
        await Assert.That(text).IsNotEqualTo("ÿ").Because("that would be the Latin-1 reading, which an explicit ECI 26 forbids");
    }

    /// <summary>
    /// Every rule in the strict validator (RFC 3629), each with a payload that only
    /// that rule rejects. Undeclared charset, so the validator alone decides: invalid
    /// means the bytes are widened as ISO-8859-1, one char per byte.
    /// </summary>
    [Test]
    [Arguments(new byte[] { 0xC1, 0x81 }, "Á")]                          // overlong 2-byte (codepoint < 0x80)
    [Arguments(new byte[] { 0xE0, 0x90, 0x80 }, "à")]              // overlong 3-byte (codepoint < 0x800)
    [Arguments(new byte[] { 0xED, 0xA0, 0x80 }, "í ")]              // UTF-16 surrogate D800
    [Arguments(new byte[] { 0xF0, 0x88, 0x80, 0x80 }, "ð")]  // overlong 4-byte (codepoint < 0x10000)
    [Arguments(new byte[] { 0xF7, 0xBF, 0xBF, 0xBF }, "÷¿¿¿")]  // above U+10FFFF
    [Arguments(new byte[] { 0xF8, 0x90, 0x80, 0x80 }, "ø")]  // 0xF8 is not a lead byte
    [Arguments(new byte[] { 0xC3, 0xC0 }, "ÃÀ")]                          // 0xC0 is not a continuation byte
    [Arguments(new byte[] { 0xC3 }, "Ã")]                                      // truncated: continuation missing
    public async Task InvalidUtf8_FallsBackToLatin1(byte[] payload, string expected)
    {
        var (status, text) = DecodeBytes(payload, ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>
    /// The positive side of the same rules: sequences the validator must ACCEPT, so a
    /// validator tightened by mistake would show up here rather than passing quietly.
    /// U+007F matters specifically — it is the last byte the ASCII branch owns.
    /// </summary>
    [Test]
    [Arguments(new byte[] { 0x7F, 0xC3, 0xA9 }, "é")]         // DEL then 2-byte é
    [Arguments(new byte[] { 0xC2, 0x80 }, "")]                     // smallest legal 2-byte
    [Arguments(new byte[] { 0xE0, 0xA0, 0x80 }, "ࠀ")]               // smallest legal 3-byte
    [Arguments(new byte[] { 0xEF, 0xBF, 0xBD }, "�")]               // largest legal 3-byte below surrogates' successors
    [Arguments(new byte[] { 0xF0, 0x90, 0x80, 0x80 }, "𐀀")]   // smallest legal 4-byte, U+10000
    [Arguments(new byte[] { 0xF4, 0x8F, 0xBF, 0xBF }, "􏿿")]   // largest legal 4-byte, U+10FFFF
    public async Task ValidUtf8_IsDecodedAsUtf8(byte[] payload, string expected)
    {
        var (status, text) = DecodeBytes(payload, ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo(expected);
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
