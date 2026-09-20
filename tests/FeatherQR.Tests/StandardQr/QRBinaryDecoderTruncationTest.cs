using System.Text;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A one-segment stream cut at every byte, for every mode, at payload lengths that put the end of
/// the payload at every alignment inside a byte.
/// The outcome is known from the layout alone: nothing but an implicit terminator when fewer than
/// four bits are left, <c>InvalidBitstream</c> while the cut falls inside the count indicator or
/// the payload, the whole text once the payload is complete.
/// </summary>
/// <remarks>
/// The reader takes fields from a 64-bit window, and the last seven bytes of a stream go through a
/// different window than the rest. A cut stream is where a field's bits run out inside that
/// window, and the failure to avoid is not a wrong status but an exception out of a decoder that
/// reports by status, or bits read from beyond the cut.
/// </remarks>
public class QRBinaryDecoderTruncationTest
{
    private const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static string Binary(int value, int bits) => Convert.ToString(value, 2).PadLeft(bits, '0');

    private static byte[] Pack(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        return bytes;
    }

    /// <summary>Mode indicator, count indicator width per version band, payload bits, text.</summary>
    private static (int Mode, int[] CountBits, string Payload, string Text) Segment(string mode, int length)
    {
        var payload = new StringBuilder();
        var text = new StringBuilder();
        switch (mode)
        {
            case "Numeric":
                for (var i = 0; i < length; i++)
                    text.Append((char)('0' + (i * 7 + 3) % 10));
                for (var i = 0; i < length; i += 3)
                {
                    var group = text.ToString(i, Math.Min(3, length - i));
                    payload.Append(Binary(int.Parse(group), group.Length * 3 + 1));
                }
                return (0b0001, [10, 12, 14], payload.ToString(), text.ToString());
            case "Alphanumeric":
                for (var i = 0; i < length; i++)
                    text.Append(AlphanumericChars[(i * 11 + 5) % 45]);
                for (var i = 0; i < length; i += 2)
                {
                    var first = AlphanumericChars.IndexOf(text[i]);
                    payload.Append(i + 1 < length
                        ? Binary(first * 45 + AlphanumericChars.IndexOf(text[i + 1]), 11)
                        : Binary(first, 6));
                }
                return (0b0010, [9, 11, 13], payload.ToString(), text.ToString());
            case "Byte":
                for (var i = 0; i < length; i++)
                {
                    text.Append((char)('a' + i % 26));
                    payload.Append(Binary('a' + i % 26, 8));
                }
                return (0b0100, [8, 16, 16], payload.ToString(), text.ToString());
            default:
                // 0x0D9F and 0x1AAA are the compacted forms of the two characters ISO/IEC 18004 works through
                for (var i = 0; i < length; i++)
                {
                    text.Append(i % 2 == 0 ? '点' : '茗');
                    payload.Append(Binary(i % 2 == 0 ? 0x0D9F : 0x1AAA, 13));
                }
                return (0b1000, [8, 10, 12], payload.ToString(), text.ToString());
        }
    }

    /// <summary>
    /// A group out of range fails the stream, and the count handed back is what was written before
    /// it. The span overloads return that count to the caller on failure too, so a loop that keeps
    /// its index in a local has to put it back on the way out.
    /// </summary>
    [Test]
    [Arguments(0b0001, 10, 9, "0001111011" + "1001000111" + "1111101000", 6)]   // 123, 583, then 1000
    [Arguments(0b0010, 9, 6, "00111001101" + "01000101001" + "11111101001", 4)] // AB, CD, then 2025
    public async Task GroupOutOfRange_ReportsTheCharactersBeforeIt(int mode, int countBits, int count, string payload, int expectedWritten)
    {
        var stream = Pack(Binary(mode, 4) + Binary(count, countBits) + payload);
        var destination = new char[count];

        var status = QRBinaryDecoder.DecodeBitStream(stream, 1, destination, out var written, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.InvalidBitstream);
        await Assert.That(written).IsEqualTo(expectedWritten);
    }

    [Test]
    [Arguments("Numeric")]
    [Arguments("Alphanumeric")]
    [Arguments("Byte")]
    [Arguments("Kanji")]
    public async Task StreamCutAtEveryByte_ReportsByStatus_NeverByException(string mode)
    {
        var mismatches = new List<string>();
        int[] versions = [1, 10, 27];
        for (var band = 0; band < versions.Length; band++)
        {
            for (var length = 1; length <= 40; length++)
            {
                var (indicator, countBits, payload, text) = Segment(mode, length);
                var header = 4 + countBits[band];
                var stream = Pack(Binary(indicator, 4) + Binary(length, countBits[band]) + payload);
                var destination = new char[length];

                for (var cut = 0; cut <= stream.Length; cut++)
                {
                    DecodeStatus status;
                    int written;
                    try
                    {
                        status = QRBinaryDecoder.DecodeBitStream(stream.AsSpan(0, cut), versions[band], destination, out written, out _);
                    }
                    catch (Exception ex)
                    {
                        mismatches.Add($"v{versions[band]} length {length} cut {cut}: threw {ex.GetType().Name}");
                        continue;
                    }

                    var bits = cut * 8;
                    var complete = bits >= header + payload.Length;
                    var expected = bits < 4 || complete ? DecodeStatus.Success : DecodeStatus.InvalidBitstream;
                    var expectedText = complete ? text : "";
                    var actualText = status == DecodeStatus.Success ? new string(destination, 0, written) : "";
                    if (status != expected || actualText != expectedText)
                        mismatches.Add($"v{versions[band]} length {length} cut {cut}: {status} '{actualText}'");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }
}
