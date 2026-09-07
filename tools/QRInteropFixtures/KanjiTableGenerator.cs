using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// Generates <c>src/FeatherQR/Internals/ShiftJisKanjiTable.cs</c> from the
/// Kanji-mode sweep (<c>probe-kanji-sweep</c>).
///
/// Provenance matters more than convenience here: hand-transcribing ~6,900
/// mappings is not reviewable, and a table copied from CP932 would silently
/// mis-map the seven cells where JIS X 0208 disagrees plus the 83 NEC row 13
/// characters CP932 adds inside the Kanji-mode range. So the values come from the sweep (an external reader applying JIS X
/// 0208), and this generator refuses to emit unless the swept data satisfies the
/// standard's own published invariants and its delta against .NET CP932 is
/// exactly the seven documented cells.
/// </summary>
public static class KanjiTableGenerator
{
    private const int IndexCount = 8192;

    /// <summary>The only cells where JIS X 0208 and CP932 may legitimately differ.</summary>
    private static readonly (int Sjis, char JisX0208, char Cp932)[] KnownDivergences =
    [
        (0x815F, '\\', '＼'),
        (0x8160, '〜', '～'),
        (0x8161, '‖', '∥'),
        (0x817C, '−', '－'),
        (0x8191, '¢', '￠'),
        (0x8192, '£', '￡'),
        (0x81CA, '¬', '￢'),
    ];

    public static int Run(string repoRoot)
    {
        var sweepPath = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "kanji-sweep.tsv");
        if (!File.Exists(sweepPath))
        {
            Console.Error.WriteLine($"sweep data not found at {sweepPath}; run 'probe-kanji-sweep' first.");
            return 1;
        }

        var table = new char[IndexCount];
        var cp932 = new char[IndexCount];
        var sjisOf = new int[IndexCount];
        var swept = 0;

        foreach (var line in File.ReadLines(sweepPath).Skip(1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            var sjis = Convert.ToInt32(parts[0], 16);
            var index = int.Parse(parts[1]);
            sjisOf[index] = sjis;
            table[index] = ParseCodePoint(parts[2]);
            cp932[index] = ParseCodePoint(parts[3]);
            swept++;
        }

        if (!Validate(table, cp932, sjisOf, swept))
            return 1;

        var outputPath = Path.Combine(repoRoot, "src", "FeatherQR", "Internals", "ShiftJisKanjiTable.cs");
        File.WriteAllText(outputPath, Emit(table), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath} ({new FileInfo(outputPath).Length:N0} bytes source, {IndexCount * 2:N0} bytes of table data)");

        // ShiftJisKanjiTableUnitTest.Table_MatchesItsGoldenDigest pins every entry
        // against this value; a regeneration that legitimately changes the data has to
        // update it, and a bare hex mismatch in CI is not a useful instruction.
        Console.WriteLine($"golden digest (GoldenDigest in ShiftJisKanjiTableUnitTest): {Digest(table)}");
        return 0;
    }

    /// <summary>SHA-256 of the emitted table, little-endian UTF-16 code units.</summary>
    private static string Digest(char[] table)
    {
        var bytes = new byte[IndexCount * 2];
        for (var index = 0; index < IndexCount; index++)
        {
            bytes[index * 2] = (byte)table[index];
            bytes[index * 2 + 1] = (byte)(table[index] >> 8);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static char ParseCodePoint(string field)
        => field.Length == 0 ? '\0' : (char)Convert.ToInt32(field[2..], 16);

    /// <summary>
    /// The generator's own gate. Every check here is against JIS X 0208 as published,
    /// not against whatever produced the sweep, so a sweep taken from a reader that
    /// applied a different mapping cannot pass.
    /// </summary>
    private static bool Validate(char[] table, char[] cp932, int[] sjisOf, int swept)
    {
        var ok = true;

        void Check(bool condition, string message)
        {
            if (condition) return;
            Console.Error.WriteLine($"  FAIL: {message}");
            ok = false;
        }

        Check(swept == 8023, $"sweep covers 8023 structurally valid cells (got {swept})");

        var assigned = table.Count(static c => c != '\0');
        Check(assigned == 6879, $"JIS X 0208 repertoire is 6,879 cells (got {assigned})");

        var nonKanji = CountRows(table, sjisOf, 1, 8);
        var kanji = CountRows(table, sjisOf, 16, 84);
        Check(nonKanji == 524, $"JIS X 0208 non-kanji rows 1-8 hold 524 cells (got {nonKanji})");
        Check(kanji == 6355, $"JIS X 0208 kanji rows 16-84 hold 6,355 cells (got {kanji})");
        Check(CountRows(table, sjisOf, 9, 15) == 0, "rows 9-15 are unassigned in JIS X 0208");
        Check(CountRows(table, sjisOf, 85, 94) == 0, "rows 85-94 are unassigned in JIS X 0208");

        // The delta against CP932 must be exactly the documented cells: any other
        // difference means the sweep captured a mapping we have not reasoned about.
        var divergences = new List<int>();
        for (var index = 0; index < IndexCount; index++)
        {
            if (table[index] == '\0' || cp932[index] == '\0') continue;
            if (table[index] != cp932[index]) divergences.Add(index);
        }

        Check(divergences.Count == KnownDivergences.Length,
            $"{KnownDivergences.Length} documented JIS X 0208 / CP932 divergences (got {divergences.Count}: {string.Join(", ", divergences.Select(i => $"0x{sjisOf[i]:X4}"))})");

        foreach (var (sjis, jis, cp) in KnownDivergences)
        {
            var index = ToIndex13(sjis);
            Check(table[index] == jis, $"0x{sjis:X4} maps to U+{(int)jis:X4} (got U+{(int)table[index]:X4})");
            Check(cp932[index] == cp, $"0x{sjis:X4} is U+{(int)cp:X4} in CP932 (got U+{(int)cp932[index]:X4})");
        }

        Console.WriteLine(ok
            ? $"validated: {assigned} assigned cells ({nonKanji} non-kanji + {kanji} kanji), {divergences.Count} documented CP932 divergences"
            : "validation failed; table not written");
        return ok;
    }

    private static int CountRows(char[] table, int[] sjisOf, int firstRow, int lastRow)
    {
        var total = 0;
        for (var index = 0; index < IndexCount; index++)
        {
            if (table[index] == '\0') continue;
            var row = ToRow(sjisOf[index]);
            if (row >= firstRow && row <= lastRow) total++;
        }
        return total;
    }

    /// <summary>Shift_JIS to JIS X 0208 row (ku), the inverse of the standard's conversion.</summary>
    private static int ToRow(int sjis)
    {
        var lead = sjis >> 8;
        var trail = sjis & 0xFF;
        var rowPair = lead >= 0xE0 ? lead - 0xC1 : lead - 0x81;
        return rowPair * 2 + (trail >= 0x9F ? 2 : 1);
    }

    private static int ToIndex13(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    private static string Emit(char[] table)
    {
        var sb = new StringBuilder(IndexCount * 12);
        sb.Append("""
            // <auto-generated>
            //   Generated by tools/QRInteropFixtures: dotnet run --project tools/QRInteropFixtures -- generate-kanji-table
            //   Source data: tools/QRInteropFixtures/kanji-sweep.tsv (probe-kanji-sweep).
            //   Do not edit by hand; regenerate instead. See .github/docs/specs/qrcode-symbologies.md.
            // </auto-generated>

            using System.Buffers.Binary;
            using System.Runtime.CompilerServices;

            namespace FeatherQR.Internals;

            /// <summary>
            /// JIS X 0208 to Unicode for ISO/IEC 18004 Kanji mode, shared by all three
            /// symbologies. Indexed by the 13-bit compacted value (8.4.5), so a lookup is a
            /// single load with no arithmetic.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The mapping is JIS X 0208, not Microsoft CP932; the two disagree, and the
            /// cells CP932 adds stay unmapped here, so a symbol carrying them is reported
            /// as DecodeStatus.UnmappedCharacter rather than silently rewritten. The
            /// canonical statement of
            /// the divergence set and the reasoning is the scope decision in
            /// .github/docs/specs/qrcode-symbologies.md; keep the counts out of this file
            /// so a regeneration cannot reintroduce a stale copy (they were wrong in four
            /// places once already).
            /// </para>
            /// <para>
            /// Unmapped cells hold 0, which is never a legitimate JIS X 0208 mapping. The
            /// caller separates the two reasons a cell can be unmapped with
            /// <see cref="IsStructurallyValid"/>: a value no Shift_JIS pair can express is a
            /// corrupt bitstream (DecodeStatus.InvalidBitstream), a well-formed value
            /// outside the repertoire is a character this mapping has no reading for
            /// (DecodeStatus.UnmappedCharacter).
            /// </para>
            /// </remarks>
            internal static class ShiftJisKanjiTable
            {
                /// <summary>Size of the index space: the compacted value is exactly 13 bits.</summary>
                public const int IndexCount = 8192;

                /// <summary>Cells the JIS X 0208 repertoire assigns (6,355 kanji + 524 non-kanji).</summary>
                public const int AssignedCellCount = 6879;

                /// <summary>Highest low byte a Shift_JIS trail byte can produce after the range subtraction.</summary>
                private const int MaxLowByte = 0xBC;

                /// <summary>Low byte that would require trail byte 0x7F, which Shift_JIS never uses.</summary>
                private const int ReservedLowByte = 0x3F;

                /// <summary>
                /// Maps a 13-bit Kanji-mode value to its JIS X 0208 character, or
                /// <c>'\0'</c> when the cell is not in the repertoire.
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static char Lookup(int index13)
                    => (char)BinaryPrimitives.ReadUInt16LittleEndian(Table.Slice(index13 * 2, 2));

                /// <summary>
                /// True when some Shift_JIS pair in the Kanji-mode ranges can produce this
                /// value. False means the bitstream is corrupt, not merely unmapped: the two
                /// get different statuses, InvalidBitstream and UnmappedCharacter.
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static bool IsStructurallyValid(int index13)
                {
                    if ((uint)index13 >= IndexCount) return false;
                    var low = index13 % 0xC0;
                    return low <= MaxLowByte && low != ReservedLowByte;
                }

                // Little-endian UTF-16 code units, one per index. Emitted as bytes because
                // only byte-typed span literals become RVA data (no allocation, no static
                // constructor); 8192 entries x 2 bytes = 16,384 bytes.
                private static ReadOnlySpan<byte> Table =>
                [

            """);

        for (var index = 0; index < IndexCount; index += 8)
        {
            sb.Append("        ");
            for (var i = 0; i < 8; i++)
            {
                var value = table[index + i];
                sb.Append($"0x{value & 0xFF:X2}, 0x{value >> 8:X2},");
                if (i < 7) sb.Append(' ');
            }
            sb.Append('\n');
        }

        sb.Append("""
                ];
            }

            """);

        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
