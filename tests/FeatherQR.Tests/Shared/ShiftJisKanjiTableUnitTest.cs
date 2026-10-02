using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The Kanji-mode Shift_JIS table, checked against JIS X 0208 itself rather than
/// against whatever produced it: the standard's published totals (6,879 cells =
/// 6,355 kanji + 524 non-kanji) and its row/cell layout are external invariants,
/// so a table generated from the wrong source cannot satisfy them by accident.
/// </summary>
public class ShiftJisKanjiTableUnitTest
{
    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helper.</summary>
    private static int Index13(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>JIS X 0208 row/cell (ku-ten) to Shift_JIS, per the standard's conversion.</summary>
    private static int FromRowCell(int row, int cell)
    {
        var lead = row <= 62 ? 0x81 + (row - 1) / 2 : 0xC1 + (row - 1) / 2;
        int trail;
        if (row % 2 == 1)
        {
            trail = 0x3F + cell;
            if (trail >= 0x7F) trail++;
        }
        else
        {
            trail = 0x9E + cell;
        }
        return (lead << 8) | trail;
    }

    private static int CountAssignedInRows(int firstRow, int lastRow)
    {
        var total = 0;
        for (var row = firstRow; row <= lastRow; row++)
            for (var cell = 1; cell <= 94; cell++)
                if (ShiftJisKanjiTable.Lookup(Index13(FromRowCell(row, cell))) != '\0')
                    total++;
        return total;
    }

    /// <summary>The whole table holds exactly the JIS X 0208 repertoire.</summary>
    [Test]
    public async Task AssignedCells_MatchTheJisX0208Total()
    {
        var assigned = 0;
        for (var index = 0; index < ShiftJisKanjiTable.IndexCount; index++)
            if (ShiftJisKanjiTable.Lookup(index) != '\0')
                assigned++;

        await Assert.That(assigned).IsEqualTo(6879);
        await Assert.That(assigned).IsEqualTo(ShiftJisKanjiTable.AssignedCellCount)
            .Because("the declared count must track the data");
    }

    /// <summary>Non-kanji rows 1-8 hold 524 cells; kanji rows 16-84 hold 6,355.</summary>
    [Test]
    public async Task AssignedCells_SplitIntoTheStandardsNonKanjiAndKanjiTotals()
    {
        await Assert.That(CountAssignedInRows(1, 8)).IsEqualTo(524).Because("JIS X 0208 non-kanji repertoire");
        await Assert.That(CountAssignedInRows(16, 84)).IsEqualTo(6355).Because("JIS X 0208 level 1 + level 2 kanji");
    }

    /// <summary>Kanji rows are dense: 16-46 and 48-83 are full, 47 stops at cell 51, 84 at cell 6.</summary>
    [Test]
    [Arguments(16, 94)]
    [Arguments(46, 94)]
    [Arguments(47, 51)]
    [Arguments(48, 94)]
    [Arguments(83, 94)]
    [Arguments(84, 6)]
    public async Task KanjiRows_AreAssignedUpToTheirLastCell(int row, int lastAssignedCell)
    {
        for (var cell = 1; cell <= lastAssignedCell; cell++)
        {
            var mapped = ShiftJisKanjiTable.Lookup(Index13(FromRowCell(row, cell)));
            await Assert.That(mapped).IsNotEqualTo('\0').Because($"row {row} cell {cell} is assigned");
        }

        for (var cell = lastAssignedCell + 1; cell <= 94; cell++)
        {
            var mapped = ShiftJisKanjiTable.Lookup(Index13(FromRowCell(row, cell)));
            await Assert.That(mapped).IsEqualTo('\0').Because($"row {row} cell {cell} is past the end of the row");
        }
    }

    /// <summary>Rows 9-15 and 85-94 are unassigned in JIS X 0208, whatever CP932 puts there.</summary>
    [Test]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)]
    [Arguments(15)]
    [Arguments(85)]
    public async Task ReservedRows_AreUnassigned(int row)
    {
        for (var cell = 1; cell <= 94; cell++)
        {
            var sjis = FromRowCell(row, cell);
            if (sjis > 0xEBBF) continue; // past the Kanji-mode range entirely

            await Assert.That(ShiftJisKanjiTable.Lookup(Index13(sjis))).IsEqualTo('\0')
                .Because($"row {row} cell {cell} (0x{sjis:X4}) is not in JIS X 0208");
        }
    }

    /// <summary>
    /// Row 13 is the NEC extension block (circled digits, unit ligatures) that CP932
    /// assigns and JIS X 0208 does not. Choosing JIS X 0208 means these stay unmapped;
    /// this is the false-positive case a CP932-derived table would silently get wrong.
    /// </summary>
    [Test]
    [Arguments(0x8740)] // CP932 U+2460 circled digit one
    [Arguments(0x8741)] // CP932 U+2461
    [Arguments(0x875F)] // CP932 roman numeral
    [Arguments(0x879C)] // CP932 last NEC row 13 cell
    public async Task NecRow13_IsUnassigned(int sjis)
    {
        await Assert.That(ShiftJisKanjiTable.Lookup(Index13(sjis))).IsEqualTo('\0');
    }

    /// <summary>
    /// The seven cells where JIS X 0208 and CP932 disagree. These are the whole reason
    /// the mapping choice is a decision and not a detail: a CP932 table returns the
    /// right-hand column and mangles wave dashes, minus signs and currency symbols.
    /// </summary>
    [Test]
    [Arguments(0x815F, '\\', '＼')] // reverse solidus vs fullwidth reverse solidus
    [Arguments(0x8160, '〜', '～')] // wave dash vs fullwidth tilde
    [Arguments(0x8161, '‖', '∥')] // double vertical line vs parallel to
    [Arguments(0x817C, '−', '－')] // minus sign vs fullwidth hyphen-minus
    [Arguments(0x8191, '¢', '￠')] // cent sign vs fullwidth cent sign
    [Arguments(0x8192, '£', '￡')] // pound sign vs fullwidth pound sign
    [Arguments(0x81CA, '¬', '￢')] // not sign vs fullwidth not sign
    public async Task DivergentCells_UseTheJisX0208Reading(int sjis, char jisX0208, char cp932)
    {
        var mapped = ShiftJisKanjiTable.Lookup(Index13(sjis));
        await Assert.That(mapped).IsEqualTo(jisX0208);
        await Assert.That(mapped).IsNotEqualTo(cp932);
    }

    /// <summary>Spot checks across both Kanji-mode ranges and their boundaries.</summary>
    [Test]
    [Arguments(0x8140, '　')] // ideographic space, first cell of the range
    [Arguments(0x82B1, 'こ')] // hiragana ko
    [Arguments(0x889F, '亜')] // first level 1 kanji
    [Arguments(0x9FFC, '滌')] // last cell of the lower range
    [Arguments(0xE040, '漾')] // first cell of the upper range
    [Arguments(0xEAA4, '熙')] // last assigned JIS X 0208 cell
    public async Task KnownCells_MapToTheirJisX0208Character(int sjis, char expected)
    {
        await Assert.That(ShiftJisKanjiTable.Lookup(Index13(sjis))).IsEqualTo(expected);
    }

    /// <summary>0xEAA5 through 0xEBBF are inside the Kanji-mode range but past JIS X 0208.</summary>
    [Test]
    [Arguments(0xEAA5)]
    [Arguments(0xEB40)]
    [Arguments(0xEBBF)]
    public async Task CellsPastTheRepertoire_AreUnassigned(int sjis)
    {
        await Assert.That(ShiftJisKanjiTable.IsStructurallyValid(Index13(sjis))).IsTrue();
        await Assert.That(ShiftJisKanjiTable.Lookup(Index13(sjis))).IsEqualTo('\0');
    }

    /// <summary>
    /// Structural validity separates "corrupt bitstream" from "well-formed but not in
    /// the repertoire", which is what lets the decoder report InvalidBitstream and
    /// UnmappedCharacter for the two different causes of a zero lookup.
    /// </summary>
    /// <remarks>
    /// The expected set is built by enumerating real Shift_JIS lead/trail pairs and
    /// compacting them, not by restating the production predicate. Mirroring the
    /// implementation would pin its constants while proving nothing about the rule.
    /// </remarks>
    [Test]
    public async Task StructuralValidity_AcceptsExactlyTheExpressibleShiftJisPairs()
    {
        var reachable = new bool[ShiftJisKanjiTable.IndexCount];
        var pairs = 0;
        foreach (var (low, high) in new[] { (0x8140, 0x9FFC), (0xE040, 0xEBBF) })
        {
            for (var lead = low >> 8; lead <= high >> 8; lead++)
            {
                for (var trail = 0x40; trail <= 0xFC; trail++)
                {
                    if (trail == 0x7F) continue; // never a Shift_JIS trail byte
                    var sjis = (lead << 8) | trail;
                    if (sjis < low || sjis > high) continue;
                    reachable[Index13(sjis)] = true;
                    pairs++;
                }
            }
        }

        for (var index = 0; index < ShiftJisKanjiTable.IndexCount; index++)
        {
            await Assert.That(ShiftJisKanjiTable.IsStructurallyValid(index)).IsEqualTo(reachable[index])
                .Because($"index {index} is {(reachable[index] ? "reachable" : "unreachable")} from a Shift_JIS pair");
        }

        // No two pairs collapse onto one index, so the count is also the pair count.
        await Assert.That(reachable.Count(static r => r)).IsEqualTo(pairs);
        await Assert.That(pairs).IsEqualTo(8023);
    }

    /// <summary>Values outside the 13-bit index space are rejected rather than read.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(8192)]
    [Arguments(int.MaxValue)]
    [Arguments(int.MinValue)]
    public async Task StructuralValidity_RejectsIndicesOutsideTheThirteenBitSpace(int index13)
    {
        await Assert.That(ShiftJisKanjiTable.IsStructurallyValid(index13)).IsFalse();
    }

    /// <summary>
    /// Pins all 8,192 entries at once. The other tests here constrain only WHICH cells
    /// are assigned; a table whose readings were permuted within a row satisfies every
    /// one of them, and that is exactly how a regenerated table can go wrong. Update
    /// this hash only together with a deliberate, reviewed regeneration.
    /// </summary>
    [Test]
    public async Task Table_MatchesItsGoldenDigest()
    {
        var bytes = new byte[ShiftJisKanjiTable.IndexCount * 2];
        for (var index = 0; index < ShiftJisKanjiTable.IndexCount; index++)
        {
            var value = ShiftJisKanjiTable.Lookup(index);
            bytes[index * 2] = (byte)value;
            bytes[index * 2 + 1] = (byte)(value >> 8);
        }

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        await Assert.That(digest).IsEqualTo(GoldenDigest);
    }

    /// <summary>
    /// SHA-256 of the 16,384-byte table, little-endian UTF-16 code units. On a
    /// deliberate regeneration, `generate-kanji-table` prints the new value; paste it
    /// here in the same reviewed change.
    /// </summary>
    private const string GoldenDigest = "7C0016107C60919AF564AA12482CB915BBA964D74E2A107D172C7C1C9490D2E2";

    /// <summary>Every assigned cell is structurally expressible; the reverse does not hold.</summary>
    [Test]
    public async Task EveryAssignedCell_IsStructurallyValid()
    {
        for (var index = 0; index < ShiftJisKanjiTable.IndexCount; index++)
        {
            if (ShiftJisKanjiTable.Lookup(index) == '\0') continue;
            await Assert.That(ShiftJisKanjiTable.IsStructurallyValid(index)).IsTrue().Because($"index {index}");
        }
    }
}
