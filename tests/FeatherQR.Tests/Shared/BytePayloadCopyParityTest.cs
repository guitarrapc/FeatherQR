using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes decode's copy of a Byte segment, and its narrowing of chars back to bytes, against a reference that reads and writes one byte at a time.
/// The copy reads whole words, so the segment starts at every bit offset within a byte, and the payload runs past a word.
/// </summary>
public class BytePayloadCopyParityTest
{
    public static IEnumerable<(int Offset, int Count)> Shapes()
    {
        for (var offset = 0; offset < 8; offset++)
        {
            foreach (var count in new[] { 0, 1, 7, 8, 9, 256, 300 })
                yield return (offset, count);
        }
    }

    [Test]
    [MethodDataSource(nameof(Shapes))]
    public async Task CopyBytePayload_MatchesByteByByteReads(int offset, int count)
    {
        var payload = new byte[count];
        for (var i = 0; i < count; i++)
            payload[i] = (byte)(i * 151 + 7);
        var data = Stream(offset, payload);

        var reference = ReadByteByByte(data, offset, count);
        var (status, destination, written, position) = Copy(data, offset, count);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(written).IsEqualTo(1 + count);
        await Assert.That(destination.AsSpan(1, count).ToArray()).IsEquivalentTo(reference, CollectionOrdering.Matching);
        await Assert.That(position).IsEqualTo(offset + count * 8);
        await Assert.That(destination[0]).IsEqualTo('x').Because("the copy writes after what is already there");
    }

    [Test]
    public async Task Narrow_GivesEveryByteValueBack()
    {
        var chars = new char[256];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)i;
        var bytes = new byte[256 + 1];
        bytes[256] = 0xA5;

        PayloadOutput.Narrow(chars, bytes);

        for (var i = 0; i < 256; i++)
            await Assert.That(bytes[i]).IsEqualTo((byte)i);
        await Assert.That(bytes[256]).IsEqualTo((byte)0xA5).Because("only as many bytes as chars are written");
    }

    private static char[] ReadByteByByte(byte[] data, int offset, int count)
    {
        var reader = new BitReader(data);
        if (offset > 0)
            reader.Reads(offset);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = (char)reader.Reads(8);
        return chars;
    }

    private static (DecodeStatus Status, char[] Destination, int Written, int Position) Copy(byte[] data, int offset, int count)
    {
        var reader = new BitReader(data);
        if (offset > 0)
            reader.Reads(offset);
        var destination = new char[count + 2];
        destination[0] = 'x';
        var written = 1;
        var status = SegmentDecoders.CopyBytePayload(ref reader, data.Length * 8, count, new byte[data.Length], destination, ref written);
        return (status, destination, written, reader.BitPosition);
    }

    /// <summary>The payload behind <paramref name="offset"/> zero bits, with a byte to spare.</summary>
    private static byte[] Stream(int offset, byte[] payload)
    {
        var buffer = new byte[(offset + payload.Length * 8 + 7) / 8 + 1];
        var writer = new BitWriter(buffer);
        if (offset > 0)
            writer.Write(0, offset);
        foreach (var b in payload)
            writer.Write(b, 8);
        writer.Flush();
        return buffer;
    }
}
