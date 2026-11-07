using System.Buffers.Binary;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>CRC-32 as PNG uses it (ISO 3309), eight bytes a step by slicing.</summary>
/// <remarks>
/// Table k (k from 0 to 7) gives the CRC of a byte followed by k zero bytes, so eight bytes fold in with eight independent lookups,
/// where a byte at a time chains every lookup on the one before.
/// </remarks>
internal static class PngCrc
{
    private static readonly uint[] Tables = BuildTables();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        var t = Tables;
        var i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
            crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + (one >> 8 & 0xFF)] ^ t[5 * 256 + (one >> 16 & 0xFF)] ^ t[4 * 256 + (one >> 24)]
                ^ t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + (two >> 8 & 0xFF)] ^ t[256 + (two >> 16 & 0xFF)] ^ t[two >> 24];
        }
        for (; i < data.Length; i++)
            crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTables()
    {
        var tables = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            tables[n] = c;
        }
        for (var k = 1; k < 8; k++)
        {
            for (var n = 0; n < 256; n++)
            {
                var previous = tables[(k - 1) * 256 + n];
                tables[k * 256 + n] = (previous >> 8) ^ tables[previous & 0xFF];
            }
        }
        return tables;
    }
}
