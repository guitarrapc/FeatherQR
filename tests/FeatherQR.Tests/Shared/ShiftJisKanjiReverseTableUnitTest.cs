using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The encoder's reverse table, checked against the decoder's forward table and against JIS X 0208 as published.
/// The reverse table is generated separately, so a stale or hand-edited copy cannot agree with the forward table by construction; every test here would fail on it.
/// </summary>
public class ShiftJisKanjiReverseTableUnitTest
{
    /// <summary>The seven cells CP932 reads differently. Neither reading has an encoder cell.</summary>
    private static readonly int[] DivergentCells = [0x815F, 0x8160, 0x8161, 0x817C, 0x8191, 0x8192, 0x81CA];

    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helpers.</summary>
    private static int Index13(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>13-bit value back to its Shift_JIS pair, then to its JIS X 0208 row (ku).</summary>
    private static int RowOf(int index13)
    {
        var sjis = ((index13 / 0xC0) << 8 | index13 % 0xC0) + (index13 >= Index13(0xE040) ? 0xC140 : 0x8140);
        var lead = sjis >> 8;
        var rowPair = lead >= 0xE0 ? lead - 0xC1 : lead - 0x81;
        return rowPair * 2 + ((sjis & 0xFF) >= 0x9F ? 2 : 1);
    }

    /// <summary>
    /// The exact inverse of the forward table over its assigned cells minus the seven, and nothing else, for every UTF-16 code unit.
    /// This is the property the encoder relies on: a character is written at a cell only if the decoder reads that cell back as the same character.
    /// </summary>
    [Test]
    public async Task EveryCodeUnit_InvertsTheForwardTableOverTheEncoderCells()
    {
        var excluded = DivergentCells.Select(Index13).ToHashSet();
        var expected = new int[65536];
        Array.Fill(expected, -1);
        for (var index = 0; index < ShiftJisKanjiTable.IndexCount; index++)
        {
            var c = ShiftJisKanjiTable.Lookup(index);
            if (c == '\0' || excluded.Contains(index)) continue;
            await Assert.That(expected[c]).IsEqualTo(-1).Because($"U+{(int)c:X4} is read from one cell only");
            expected[c] = index;
        }

        var cells = 0;
        for (var c = 0; c < 65536; c++)
        {
            var actual = ShiftJisKanjiReverseTable.Lookup((char)c);
            if (actual != expected[c])
                await Assert.That(actual).IsEqualTo(expected[c]).Because($"U+{c:X4}");
            if (actual >= 0) cells++;
        }

        await Assert.That(cells).IsEqualTo(6872).Because("6,879 JIS X 0208 cells minus the seven CP932 reads differently");
        await Assert.That(cells).IsEqualTo(ShiftJisKanjiReverseTable.EncoderCellCount).Because("the declared count must track the data");
    }

    /// <summary>
    /// The membership test the analysis runs answers yes exactly where a lookup finds a cell, for every UTF-16 code unit: it is the lookup without the rank and the value, so the two must never disagree about which characters a Kanji plan can hold.
    /// </summary>
    [Test]
    public async Task HasCell_AgreesWithLookup_EveryCodeUnit()
    {
        var cells = 0;
        for (var c = 0; c < 65536; c++)
        {
            var expected = ShiftJisKanjiReverseTable.Lookup((char)c) >= 0;
            var actual = ShiftJisKanjiReverseTable.HasCell((char)c);
            if (actual != expected)
                await Assert.That(actual).IsEqualTo(expected).Because($"U+{c:X4}");
            if (actual) cells++;
        }

        await Assert.That(cells).IsEqualTo(ShiftJisKanjiReverseTable.EncoderCellCount);
    }

    /// <summary>
    /// Both readings of each divergent cell miss: the JIS X 0208 reading, which the forward table returns, and the CP932 reading, which a CP932 reader returns.
    /// Writing either would make a symbol two readers decode differently.
    /// </summary>
    [Test]
    [Arguments('\\')] // 0x815F, JIS X 0208; also ASCII, cheaper in Byte mode
    [Arguments('＼')] // 0x815F, CP932
    [Arguments('〜')] // 0x8160, JIS X 0208 wave dash
    [Arguments('～')] // 0x8160, CP932 fullwidth tilde
    [Arguments('‖')] // 0x8161, JIS X 0208
    [Arguments('∥')] // 0x8161, CP932
    [Arguments('−')] // 0x817C, JIS X 0208 minus sign
    [Arguments('－')] // 0x817C, CP932 fullwidth hyphen-minus
    [Arguments('¢')] // 0x8191, JIS X 0208
    [Arguments('￠')] // 0x8191, CP932
    [Arguments('£')] // 0x8192, JIS X 0208
    [Arguments('￡')] // 0x8192, CP932
    [Arguments('¬')] // 0x81CA, JIS X 0208
    [Arguments('￢')] // 0x81CA, CP932
    public async Task DivergentCellReadings_HaveNoEncoderCell(char c)
    {
        await Assert.That(ShiftJisKanjiReverseTable.Lookup(c)).IsEqualTo(-1);
    }

    /// <summary>The forward table reads each divergent cell, so the exclusion is the reverse table's own, not an inherited gap.</summary>
    [Test]
    public async Task DivergentCells_AreReadByTheForwardTable()
    {
        foreach (var sjis in DivergentCells)
            await Assert.That(ShiftJisKanjiTable.Lookup(Index13(sjis))).IsNotEqualTo('\0').Because($"0x{sjis:X4}");
    }

    /// <summary>Characters CP932 or other sources carry that JIS X 0208 has no cell for.</summary>
    [Test]
    [Arguments('①')] // NEC row 13
    [Arguments('㈱')] // NEC row 13
    [Arguments('Ⅰ')] // NEC row 13 roman numeral
    [Arguments('纊')] // NEC-selected IBM extension, CP932 0xED40 / 0xFA5C
    [Arguments('ｶ')] // halfwidth katakana, JIS X 0201
    [Arguments('｡')] // halfwidth ideographic full stop
    [Arguments('é')] // Latin-1 outside JIS X 0208
    [Arguments('¥')] // JIS X 0201 yen, not in JIS X 0208
    [Arguments('‾')] // JIS X 0201 overline
    [Arguments('\uD842')] // high surrogate of 𠮷, a supplementary kanji
    [Arguments('\uDFB7')] // low surrogate
    [Arguments('￿')]
    [Arguments('\0')]
    public async Task CharactersOutsideJisX0208_HaveNoEncoderCell(char c)
    {
        await Assert.That(ShiftJisKanjiReverseTable.Lookup(c)).IsEqualTo(-1);
    }

    /// <summary>ASCII is written in the ASCII modes, never as Kanji, whatever JIS X 0208 holds.</summary>
    [Test]
    public async Task Ascii_HasNoEncoderCell()
    {
        for (var c = 0; c < 0x80; c++)
            await Assert.That(ShiftJisKanjiReverseTable.Lookup((char)c)).IsEqualTo(-1).Because($"U+{c:X4}");
    }

    /// <summary>Spot checks across the repertoire, its scripts and both Kanji-mode ranges.</summary>
    [Test]
    [Arguments('　', 0x8140)] // ideographic space, value 0
    [Arguments('×', 0x817E)] // Latin-1 symbol in row 1
    [Arguments('§', 0x8198)]
    [Arguments('―', 0x815C)] // agrees between JIS X 0208 and CP932, though often listed as divergent
    [Arguments('あ', 0x82A0)]
    [Arguments('ン', 0x8393)]
    [Arguments('Ω', 0x83B6)] // Greek
    [Arguments('П', 0x8450)] // Cyrillic
    [Arguments('─', 0x849F)] // box drawing, row 8
    [Arguments('亜', 0x889F)] // first level 1 kanji
    [Arguments('腕', 0x9872)] // last level 1 kanji
    [Arguments('弌', 0x989F)] // first level 2 kanji
    [Arguments('滌', 0x9FFC)] // last cell of the lower range
    [Arguments('漾', 0xE040)] // first cell of the upper range
    [Arguments('熙', 0xEAA4)] // last assigned cell
    public async Task KnownCharacters_MapToTheirCell(char c, int sjis)
    {
        await Assert.That(ShiftJisKanjiReverseTable.Lookup(c)).IsEqualTo(Index13(sjis));
    }

    /// <summary>No character reaches a row JIS X 0208 leaves unassigned: rows 9-15 (NEC row 13 among them) and 85-94.</summary>
    [Test]
    public async Task NoCharacter_ReachesAnUnassignedRow()
    {
        for (var c = 0; c < 65536; c++)
        {
            var index = ShiftJisKanjiReverseTable.Lookup((char)c);
            if (index < 0) continue;
            var row = RowOf(index);
            if (row is (>= 9 and <= 15) or >= 85)
                await Assert.That(row).IsBetween(1, 8).Because($"U+{c:X4} maps to row {row}");
        }
    }

    /// <summary>
    /// Pins every lookup at once, including the misses. The inverse test above already ties the table to the forward one; this digest makes a regeneration that changes it visible in review on its own.
    /// Update it only together with a deliberate, reviewed regeneration; `generate-kanji-table` prints the new value.
    /// </summary>
    [Test]
    public async Task Lookups_MatchTheirGoldenDigest()
    {
        var bytes = new byte[65536 * 2];
        for (var c = 0; c < 65536; c++)
        {
            var value = (ushort)ShiftJisKanjiReverseTable.Lookup((char)c);
            bytes[c * 2] = (byte)value;
            bytes[c * 2 + 1] = (byte)(value >> 8);
        }

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        await Assert.That(digest).IsEqualTo(GoldenDigest);
    }

    /// <summary>SHA-256 of the lookup of every UTF-16 code unit, as little-endian 16-bit values with a miss as 0xFFFF.</summary>
    private const string GoldenDigest = "520C5767B4361E2040A7A633F1F84A9267B97CA1983D8D6A63DBA6698990ACC4";
}
