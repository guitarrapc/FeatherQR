using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// The encoder's inverse of the Kanji table, built and emitted by <c>generate-kanji-table</c> into
/// <c>src/FeatherQR/Internals/ShiftJisKanjiReverseTable.cs</c>.
///
/// It maps a UTF-16 code unit to the 13-bit value of its JIS X 0208 cell, over the forward table's
/// assigned cells minus the seven that CP932 reads differently: a symbol written at one of those
/// cells would decode to different text in a CP932 reader and in this library, whichever reading
/// was written. The layout was chosen by measurement (qrcode-symbologies.md, the reverse table's layout): a page
/// directory over the high byte, a record per 64 code units holding a membership word and the
/// count of members before it, and the values in code-unit order packed at 13 bits.
/// </summary>
internal sealed class ReverseTable
{
    /// <summary>6,879 JIS X 0208 cells minus the seven divergent ones.</summary>
    public const int EncoderCellCount = 6872;

    private const int CodeUnitCount = 65536;
    private const byte NoPage = 0xFF;
    private const int BlocksPerPage = 4;
    private const int RecordSize = 10;

    private readonly byte[] _pages;
    private readonly byte[] _blocks;
    private readonly byte[] _values;
    private readonly HashSet<int> _divergentIndices;

    private ReverseTable(byte[] pages, byte[] blocks, byte[] values, HashSet<int> divergentIndices)
    {
        _pages = pages;
        _blocks = blocks;
        _values = values;
        _divergentIndices = divergentIndices;
    }

    public int DataSize => _pages.Length + _blocks.Length + _values.Length;

    public static ReverseTable Build(char[] forward, HashSet<int> divergentIndices)
    {
        // Code unit -> 13-bit value, in code-unit order; the rank of a member is its position here.
        var cells = new SortedDictionary<char, int>();
        for (var index = 0; index < forward.Length; index++)
        {
            if (forward[index] == '\0' || divergentIndices.Contains(index)) continue;
            if (!cells.TryAdd(forward[index], index))
                throw new InvalidOperationException($"U+{(int)forward[index]:X4} is read from two cells; the forward table has no inverse");
        }

        var pageOf = cells.Keys.Select(static c => c >> 8).Distinct().Order().ToArray();
        if (pageOf.Length >= NoPage)
            throw new InvalidOperationException($"{pageOf.Length} pages do not fit a byte directory");

        var pages = Enumerable.Repeat(NoPage, 256).ToArray();
        for (var i = 0; i < pageOf.Length; i++)
            pages[pageOf[i]] = (byte)i;

        var words = new ulong[pageOf.Length * BlocksPerPage];
        foreach (var c in cells.Keys)
            words[pages[c >> 8] * BlocksPerPage + ((c >> 6) & 3)] |= 1UL << (c & 63);

        var blocks = new byte[words.Length * RecordSize];
        var before = 0;
        for (var block = 0; block < words.Length; block++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(blocks.AsSpan(block * RecordSize), words[block]);
            BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(block * RecordSize + 8), checked((ushort)before));
            before += BitOperations.PopCount(words[block]);
        }

        // 13 bits per value, least significant bit first; three trailing bytes let the last
        // value be read as a whole uint.
        var values = new byte[(cells.Count * 13 + 7) / 8 + 3];
        var rank = 0;
        foreach (var value in cells.Values)
        {
            for (var bit = 0; bit < 13; bit++)
            {
                if (((value >> bit) & 1) == 0) continue;
                var at = rank * 13 + bit;
                values[at >> 3] |= (byte)(1 << (at & 7));
            }
            rank++;
        }

        return new ReverseTable(pages, blocks, values, divergentIndices);
    }

    /// <summary>
    /// The emitted lookup, run on the emitted bytes. The generated C# is this method's text, so
    /// what is validated here is what ships.
    /// </summary>
    private int Lookup(char c)
    {
        int page = _pages[c >> 8];
        if (page == NoPage) return -1;
        var record = _blocks.AsSpan((page * BlocksPerPage + ((c >> 6) & 3)) * RecordSize, RecordSize);
        var word = BinaryPrimitives.ReadUInt64LittleEndian(record);
        var bit = 1UL << (c & 63);
        if ((word & bit) == 0) return -1;
        var rank = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) + BitOperations.PopCount(word & (bit - 1));
        var at = rank * 13;
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(_values.AsSpan(at >> 3)) >> (at & 7)) & 0x1FFF;
    }

    /// <summary>
    /// The generator's gate: over every UTF-16 code unit, the table is the exact inverse of the
    /// forward table's assigned cells minus the divergent ones, and misses everything else,
    /// including both readings (JIS X 0208 and CP932) of every divergent cell.
    /// </summary>
    public bool Validate(char[] forward, IEnumerable<char> divergentReadings)
    {
        var expected = new int[CodeUnitCount];
        Array.Fill(expected, -1);
        for (var index = 0; index < forward.Length; index++)
        {
            if (forward[index] != '\0' && !_divergentIndices.Contains(index))
                expected[forward[index]] = index;
        }

        var ok = true;
        var cells = 0;
        for (var c = 0; c < CodeUnitCount; c++)
        {
            var actual = Lookup((char)c);
            if (actual >= 0) cells++;
            if (actual == expected[c]) continue;
            Console.Error.WriteLine($"  FAIL: U+{c:X4} reverse-maps to {actual}, expected {expected[c]}");
            ok = false;
        }

        if (cells != EncoderCellCount)
        {
            Console.Error.WriteLine($"  FAIL: {EncoderCellCount} encoder cells (got {cells})");
            ok = false;
        }

        foreach (var reading in divergentReadings)
        {
            if (Lookup(reading) == -1) continue;
            Console.Error.WriteLine($"  FAIL: U+{(int)reading:X4}, a reading of a divergent cell, has an encoder cell");
            ok = false;
        }

        Console.WriteLine(ok
            ? $"validated reverse: {cells} encoder cells, exact inverse of the forward table without the {_divergentIndices.Count} divergent cells, {DataSize:N0} bytes"
            : "reverse validation failed; tables not written");
        return ok;
    }

    /// <summary>SHA-256 of the lookup of every UTF-16 code unit, little-endian 16-bit, a miss as 0xFFFF.</summary>
    public string Digest()
    {
        var bytes = new byte[CodeUnitCount * 2];
        for (var c = 0; c < CodeUnitCount; c++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(c * 2), (ushort)Lookup((char)c));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public string Emit()
    {
        var sb = new StringBuilder(DataSize * 7);
        sb.Append($$"""
            // <auto-generated>
            //   Generated by tools/QRInteropFixtures: dotnet run --project tools/QRInteropFixtures -- generate-kanji-table
            //   Source data: tools/QRInteropFixtures/kanji-sweep.tsv (probe-kanji-sweep), through ShiftJisKanjiTable.
            //   Do not edit by hand; regenerate instead. See .github/docs/specs/qrcode-symbologies.md.
            // </auto-generated>

            using System.Buffers.Binary;
            using System.Runtime.CompilerServices;

            namespace FeatherQR.Internals;

            /// <summary>
            /// Unicode to ISO/IEC 18004 Kanji mode, the encoder's inverse of <see cref="ShiftJisKanjiTable"/>.
            /// Maps a UTF-16 code unit to the 13-bit compacted value (8.4.5) of its JIS X 0208 cell.
            /// </summary>
            /// <remarks>
            /// <para>
            /// It holds the forward table's cells minus the ones CP932 reads differently: a symbol written at one of those cells decodes to different text in a CP932 reader and in this library, whichever reading was written.
            /// The generator refuses to emit unless this table is the exact inverse of the forward one over the remaining cells; the canonical statement of the rule is in .github/docs/specs/qrcode-symbologies.md.
            /// </para>
            /// <para>
            /// Layout: a directory over the code unit's high byte, then per 64 code units a record of a membership word and the count of members before it, then the values in code-unit order packed at 13 bits.
            /// A lookup is three dependent loads and a popcount, with no search, no allocation and no static constructor.
            /// </para>
            /// </remarks>
            internal static class ShiftJisKanjiReverseTable
            {
                /// <summary>Characters that have an encoder cell.</summary>
                public const int EncoderCellCount = {{EncoderCellCount}};

                /// <summary>Directory entry of a high byte that holds no encoder cell.</summary>
                private const byte NoPage = 0x{{NoPage:X2}};

                /// <summary>A membership word (8 bytes) and the count of members before it (2 bytes).</summary>
                private const int RecordSize = {{RecordSize}};

                /// <summary>
                /// Maps a UTF-16 code unit to the 13-bit Kanji-mode value of its cell, or -1 when it has no encoder cell.
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static int Lookup(char c)
                {
                    int page = Pages[c >> 8];
                    if (page == NoPage) return -1;
                    var record = Blocks.Slice((page * {{BlocksPerPage}} + ((c >> 6) & 3)) * RecordSize, RecordSize);
                    var word = BinaryPrimitives.ReadUInt64LittleEndian(record);
                    var bit = 1UL << (c & 63);
                    if ((word & bit) == 0) return -1;
                    var rank = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8)) + PopCount(word & (bit - 1));
                    var at = rank * 13;
                    return (int)(BinaryPrimitives.ReadUInt32LittleEndian(Values.Slice(at >> 3)) >> (at & 7)) & 0x1FFF;
                }

                /// <summary>
                /// Whether a UTF-16 code unit has an encoder cell: <see cref="Lookup"/> without the rank and the value, for a pass that asks only which characters Kanji mode can hold and leaves the values to the writer.
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static bool HasCell(char c)
                {
                    int page = Pages[c >> 8];
                    return page != NoPage
                        && (BinaryPrimitives.ReadUInt64LittleEndian(Blocks.Slice((page * {{BlocksPerPage}} + ((c >> 6) & 3)) * RecordSize)) & (1UL << (c & 63))) != 0;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private static int PopCount(ulong value)
                {
            #if NET8_0_OR_GREATER
                    return System.Numerics.BitOperations.PopCount(value);
            #else
                    // SWAR popcount for targets without System.Numerics.BitOperations.
                    value -= (value >> 1) & 0x5555555555555555UL;
                    value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
                    value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                    return (int)((value * 0x0101010101010101UL) >> 56);
            #endif
                }

                // Byte-typed span literals become RVA data (no allocation, no static constructor).
                // Multi-byte fields are little-endian.

            """);
        sb.Append('\n');

        EmitBytes(sb, "Pages", $"Directory: high byte to page number, 0x{NoPage:X2} for none. {_pages.Length:N0} bytes.", _pages);
        EmitBytes(sb, "Blocks", $"{BlocksPerPage} records per page, one per 64 code units. {_blocks.Length:N0} bytes.", _blocks);
        EmitBytes(sb, "Values", $"13-bit values in code-unit order, least significant bit first, 3 bytes of padding. {_values.Length:N0} bytes.", _values);

        sb.Append("}\n");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static void EmitBytes(StringBuilder sb, string name, string comment, byte[] bytes)
    {
        sb.Append($"    // {comment}\n");
        sb.Append($"    private static ReadOnlySpan<byte> {name} =>\n    [\n");
        for (var i = 0; i < bytes.Length; i += 16)
        {
            sb.Append("        ");
            var end = Math.Min(i + 16, bytes.Length);
            for (var j = i; j < end; j++)
            {
                sb.Append($"0x{bytes[j]:X2},");
                if (j < end - 1) sb.Append(' ');
            }
            sb.Append('\n');
        }
        sb.Append("    ];\n");
        if (name != "Values") sb.Append('\n');
    }
}
