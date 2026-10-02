using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// Structured Append sets of Kanji-eligible text (standardqr-encoder.md, "A Kanji-eligible text can be a Kanji set").
/// </summary>
/// <remarks>
/// <para>
/// Every option here sets <c>AllowKanji</c> unless a test says otherwise; without it every set is the UTF-8 set it was before Kanji mode existed.
/// The rules the expectations are written from: a set is eligible when the whole text is, and an eligible set carries no ECI header and the XOR of the whole text's Shift_JIS bytes as its parity.
/// Text whose every character has a cell is a Kanji set under both segmentations, every chunk under <c>Single</c> one Kanji segment; it is never larger than its UTF-8 set, character for character 13 bits against 16 or 24 and no ECI header.
/// Text with ASCII in it is a Kanji set only under <c>Optimal</c>, every chunk its Kanji plan, and only when that set needs fewer symbols than today's UTF-8 set, or as many at a lower version; otherwise it is today's set.
/// A text that fits one symbol is <c>Create</c>'s symbol.
/// </para>
/// <para>
/// The planner is held to a reference walk that prices each chunk from <see cref="KanjiPlanReference"/> (the plan) or from the standard (one Kanji segment), and each symbol is written out from <see cref="KanjiStreamReference"/> with the mask pinned.
/// </para>
/// </remarks>
public class StructuredAppendKanjiTest
{
    /// <summary>The CodeGlyphX fixture set's text (4 symbols of version 3-M, Kanji mode, parity 176).</summary>
    private const string CodeGlyphXText = "こんにちは世界、QRコードの分割テストです。こんにちは世界、QRコードの分割テストです。こんにちは世界、QRコードの分割テストです。";

    private static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    // ---- Parity ---------------------------------------------------------------------

    [Test]
    public async Task Parity_OfTheCodeGlyphXText_Is176()
    {
        await Assert.That(StructuredAppendPlanner.ParityKanji(CodeGlyphXText)).IsEqualTo((byte)176);
        await Assert.That((byte)ShiftJisBytes(CodeGlyphXText).Aggregate(0, (p, b) => p ^ b)).IsEqualTo((byte)176);
    }

    [Test]
    [MethodDataSource(typeof(ModeSegmenterKanjiParityTest), nameof(ModeSegmenterKanjiParityTest.Corpus))]
    public async Task Parity_IsTheXorOfTheShiftJisBytes(string name, string text)
    {
        var expected = (byte)ShiftJisBytes(text).Aggregate(0, (p, b) => p ^ b);
        await Assert.That(StructuredAppendPlanner.ParityKanji(text)).IsEqualTo(expected).Because(name);
    }

    /// <summary>Cell by cell, so that no two wrong bytes cancel: both Shift_JIS ranges, 0x8140 to 0x9FFC and 0xE040 to 0xEBBF.</summary>
    [Test]
    public async Task Parity_OfEveryEncoderCell_IsTheXorOfItsShiftJisBytes()
    {
        var wrong = new List<string>();
        var upperRange = 0;
        foreach (var c in EncoderCells)
        {
            var bytes = ShiftJisBytes(c.ToString());
            if (bytes[0] >= 0xE0)
                upperRange++;
            var expected = (byte)(bytes[0] ^ bytes[1]);
            var actual = StructuredAppendPlanner.ParityKanji(c.ToString());
            if (actual != expected)
                wrong.Add($"U+{(int)c:X4}: {actual} != {expected}");
        }
        await Assert.That(upperRange).IsGreaterThan(0);
        await Assert.That(wrong).IsEmpty();
    }

    // ---- The cost model --------------------------------------------------------------

    /// <summary>What a chunk of a Kanji set costs as a symbol: the 20-bit header, no ECI header, and one Kanji segment (every character with a cell) or the Kanji plan.</summary>
    private static int ReferenceChunkBits(string chunk, int version, QRSegmentation segmentation)
        => StructuredAppendPlanner.HeaderBits + (segmentation == QRSegmentation.Optimal
            ? KanjiPlanReference.Cost(chunk, KanjiPlanReference.StandardQr(version), null, out _)
            : 4 + StandardQrCountBits('K', version) + 13 * chunk.Length);

    private static int ReferenceChunkEnd(string text, int start, int version, QRSegmentation segmentation, int budget)
    {
        var end = start;
        while (end < text.Length && ReferenceChunkBits(text.Substring(start, end + 1 - start), version, segmentation) <= budget)
            end++;
        return end == start ? -1 : end;
    }

    private static int ReferenceCount(string text, int version, QRSegmentation segmentation, int budget)
    {
        var count = 0;
        for (var start = 0; start < text.Length; count++)
        {
            var end = ReferenceChunkEnd(text, start, version, segmentation, budget);
            if (end < 0)
                return int.MaxValue;
            start = end;
        }
        return count;
    }

    public static IEnumerable<(string Name, string Text, QRSegmentation Segmentation)> PlannerCorpus() =>
    [
        ("cells-40", Cells(0, 40), QRSegmentation.Single),
        ("cells-40", Cells(0, 40), QRSegmentation.Optimal),
        ("cells-150", Cells(11, 150), QRSegmentation.Single),
        ("codeglyphx", CodeGlyphXText, QRSegmentation.Optimal),
        ("kanji-digits", Repeat("日本7777", 20), QRSegmentation.Optimal),
        ("order-lines", Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 3), QRSegmentation.Optimal),
        ("interleaved", Repeat("a日b本c", 12), QRSegmentation.Optimal),
    ];

    [Test]
    [MethodDataSource(nameof(PlannerCorpus))]
    public async Task ChunkBitsKanji_IsTheReferenceCost(string name, string text, QRSegmentation segmentation)
    {
        foreach (var version in new[] { 1, 10, 27 })
        {
            foreach (var length in new[] { 1, 2, 7, 23 })
            {
                if (length > text.Length)
                    continue;
                var chunk = text.Substring(text.Length - length);
                await Assert.That(StructuredAppendPlanner.ChunkBitsKanji(chunk, version, segmentation)).IsEqualTo(ReferenceChunkBits(chunk, version, segmentation)).Because($"{name} {length} at {version}");
            }
        }
    }

    /// <summary>The chunk end is the longest chunk whose cost fits, at every budget the answer can turn on.</summary>
    [Test]
    [MethodDataSource(nameof(PlannerCorpus))]
    public async Task LongestChunkEndKanji_MatchesTheReferenceWalk(string name, string text, QRSegmentation segmentation)
    {
        foreach (var version in new[] { 1, 10, 27 })
        {
            foreach (var start in new[] { 0, 5, text.Length / 2 })
            {
                var budgets = new SortedSet<int>();
                for (var end = start + 1; end <= Math.Min(text.Length, start + 40); end++)
                {
                    var cost = ReferenceChunkBits(text.Substring(start, end - start), version, segmentation);
                    budgets.Add(cost - 1);
                    budgets.Add(cost);
                }
                foreach (var budget in budgets)
                {
                    var expected = ReferenceChunkEnd(text, start, version, segmentation, budget);
                    await Assert.That(StructuredAppendPlanner.LongestChunkEndKanji(text, start, version, segmentation, budget)).IsEqualTo(expected).Because($"{name} from {start} at {version}, budget {budget}");
                }
            }
        }
    }

    public static IEnumerable<(string Name, string Text, QRSegmentation Segmentation, QREccLevel Ecc, int MaxVersion)> PlanCases() =>
    [
        ("cells-40", Cells(0, 40), QRSegmentation.Single, QREccLevel.M, 2),
        ("cells-40", Cells(0, 40), QRSegmentation.Optimal, QREccLevel.M, 2),
        ("cells-150", Cells(11, 150), QRSegmentation.Single, QREccLevel.H, 5),
        ("codeglyphx", CodeGlyphXText, QRSegmentation.Optimal, QREccLevel.M, 3),
        ("kanji-digits", Repeat("日本7777", 20), QRSegmentation.Optimal, QREccLevel.L, 3),
        ("order-lines", Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 3), QRSegmentation.Optimal, QREccLevel.Q, 4),
        ("interleaved", Repeat("a日b本c", 12), QRSegmentation.Optimal, QREccLevel.M, 2),
    ];

    /// <summary>The three properties of the balanced split, for a Kanji set: fewest symbols at the largest version, the smallest version that holds that many, and the smallest budget that does.</summary>
    [Test]
    [MethodDataSource(nameof(PlanCases))]
    public async Task PlanKanji_IsFewestSymbolsAtTheSmallestVersionWithTheSmallestBudget(string name, string text, QRSegmentation segmentation, QREccLevel ecc, int maxVersion)
    {
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        var planned = StructuredAppendPlanner.TryPlanKanji(text, ecc, segmentation, 1, maxVersion, ends, out var count, out var version, out var budget);

        await Assert.That(planned).IsTrue().Because(name);
        await Assert.That(count).IsBetween(2, 16).Because(name);
        await Assert.That(ReferenceCount(text, maxVersion, segmentation, StructuredAppendPlanner.Capacity(maxVersion, ecc))).IsEqualTo(count).Because($"{name}: fewest");
        if (version > 1)
            await Assert.That(ReferenceCount(text, version - 1, segmentation, StructuredAppendPlanner.Capacity(version - 1, ecc))).IsGreaterThan(count).Because($"{name}: smallest version");

        await Assert.That(budget).IsLessThanOrEqualTo(StructuredAppendPlanner.Capacity(version, ecc));
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            await Assert.That(ReferenceChunkBits(text.Substring(start, ends[i] - start), version, segmentation)).IsLessThanOrEqualTo(budget).Because($"{name}: chunk {i}");
            start = ends[i];
        }
        await Assert.That(start).IsEqualTo(text.Length);
        await Assert.That(ReferenceCount(text, version, segmentation, budget - 1)).IsGreaterThan(count).Because($"{name}: smallest budget");
    }

    /// <summary>The lower bound prices a character with a cell at 13 bits: at the UTF-8 rate a kana is 24, and the bound would refuse counts the walk reaches.</summary>
    [Test]
    public async Task CheapestPayloadBitsKanji_PricesACellAt13Bits()
    {
        var text = Repeat("日本7777", 20);
        await Assert.That(StructuredAppendPlanner.CheapestPayloadBitsKanji(text)).IsEqualTo((40 * 78 + 80 * 20 + 5) / 6);
        foreach (var (name, planText, segmentation) in PlannerCorpus())
        {
            foreach (var version in new[] { 1, 10, 27 })
            {
                var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
                var count = ReferenceCount(planText, version, segmentation, capacity);
                if (count > 16)
                    continue;
                await Assert.That(StructuredAppendPlanner.CanHold(capacity, count, StructuredAppendPlanner.CheapestPayloadBitsKanji(planText), EciMode.Default)).IsTrue().Because($"{name} at {version}");
            }
        }
    }

    /// <summary>Maximal stretches of ASCII and of characters with a cell, counted from the text: no Kanji plan has a run that crosses between them.</summary>
    private static int Stretches(string text)
    {
        var stretches = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i == 0 || (text[i] < 0x80) != (text[i - 1] < 0x80))
                stretches++;
        }
        return stretches;
    }

    /// <summary>
    /// The Kanji set's bound with the runs it cannot do without, one for every stretch: it never refuses a count the reference walk reaches at the version's capacity.
    /// </summary>
    [Test]
    public async Task CanHoldKanji_NeverRefusesACountTheWalkReaches()
    {
        foreach (var (name, planText, segmentation) in PlannerCorpus())
        {
            foreach (var version in new[] { 1, 2, 5, 10, 27 })
            {
                var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
                var count = ReferenceCount(planText, version, segmentation, capacity);
                if (count > 16)
                    continue;
                await Assert.That(StructuredAppendPlanner.CanHoldKanji(capacity, count, StructuredAppendPlanner.CheapestPayloadBitsKanji(planText), Stretches(planText))).IsTrue().Because($"{name} {segmentation} at {version}");
            }
        }
    }

    /// <summary>
    /// Where ASCII and cells take turns the runs decide: 「a日b本c」 × 12 is 50 bits of characters in every 5 and 49 stretches.
    /// At version 1-L (152 bits a symbol, 32 of them each symbol's header and first run) the characters alone fit 5 symbols; with a run for every stretch 8 do not, 9 do to the bit, and the walk needs at least that.
    /// </summary>
    [Test]
    public async Task CanHoldKanji_CountsARunForEveryStretch()
    {
        var text = Repeat("a日b本c", 12);
        var cheapest = StructuredAppendPlanner.CheapestPayloadBitsKanji(text);
        await Assert.That(cheapest).IsEqualTo(600);
        await Assert.That(Stretches(text)).IsEqualTo(49);

        var capacity = StructuredAppendPlanner.Capacity(1, QREccLevel.L);
        await Assert.That(StructuredAppendPlanner.CanHold(capacity, 5, cheapest, EciMode.Default)).IsTrue();
        await Assert.That(StructuredAppendPlanner.CanHoldKanji(capacity, 8, cheapest, 49)).IsFalse();
        await Assert.That(StructuredAppendPlanner.CanHoldKanji(capacity, 9, cheapest, 49)).IsTrue();
        await Assert.That(ReferenceCount(text, 1, QRSegmentation.Optimal, capacity)).IsGreaterThanOrEqualTo(9);
    }

    // ---- Through the public API -----------------------------------------------------

    private static readonly QRCodeGeneratorOptions Pinned = new() { MaskPattern = 0, QuietZoneSize = 0, AllowKanji = true };

    private static byte[] Modules(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static (string Text, QRStructuredAppend Header) Decode(QRCodeData symbol)
    {
        var modules = Modules(symbol);
        if (!QRCodeDecoder.TryDecode(modules, symbol.Size, out var text, out var info))
            throw new InvalidOperationException("a symbol of the set did not decode");
        return (text, info.StructuredAppend);
    }

    /// <summary>Every symbol decodes, shares the set's version and parity, and the set reassembles to the text.</summary>
    private static async Task<(string[] Chunks, int Version, byte Parity)> AssertRoundTrip(QRCodeData[] set, string text, string because)
    {
        var chunks = new string[set.Length];
        var parity = -1;
        for (var i = 0; i < set.Length; i++)
        {
            var (chunk, header) = Decode(set[i]);
            await Assert.That(header.Index).IsEqualTo(i).Because(because);
            await Assert.That(header.Count).IsEqualTo(set.Length).Because(because);
            await Assert.That(set[i].Version).IsEqualTo(set[0].Version).Because($"{because}: one version");
            if (parity < 0)
                parity = header.Parity;
            await Assert.That((int)header.Parity).IsEqualTo(parity).Because($"{because}: one parity");
            chunks[i] = chunk;
        }
        await Assert.That(string.Concat(chunks)).IsEqualTo(text).Because(because);
        return (chunks, set[0].Version, (byte)parity);
    }

    /// <summary>The set a Kanji set is written as, symbol by symbol: the header, then one Kanji segment or the chunk's Kanji plan, and no ECI header.</summary>
    private static async Task AssertIsTheKanjiSet(QRCodeData[] set, string text, QREccLevel ecc, QRSegmentation segmentation, string because)
    {
        var (chunks, version, parity) = await AssertRoundTrip(set, text, because);
        await Assert.That(parity).IsEqualTo(StructuredAppendPlanner.ParityKanji(text)).Because($"{because}: the Shift_JIS parity");
        for (var i = 0; i < set.Length; i++)
        {
            Run[] body = segmentation == QRSegmentation.Optimal
                ? KanjiPlanReference.Runs(chunks[i], KanjiPlanReference.StandardQr(version))
                : [new Run('K', chunks[i])];
            Run[] runs = [new Run('S', $"{i},{set.Length},{parity}"), .. body];
            var expected = KanjiSymbolBuilder.StandardQr(StandardQrStream(version, QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords, runs), version, ecc, mask: 0);
            await Assert.That(Modules(set[i])).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{because}: symbol {i}");
        }
    }

    private static async Task AssertSameSet(QRCodeData[] actual, QRCodeData[] expected, string because)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length).Because(because);
        for (var i = 0; i < actual.Length; i++)
        {
            await Assert.That(actual[i].Version).IsEqualTo(expected[i].Version).Because(because);
            await Assert.That(Modules(actual[i])).IsEquivalentTo(Modules(expected[i]), CollectionOrdering.Matching).Because($"{because}: symbol {i}");
        }
    }

    [Test]
    public async Task CodeGlyphXText_OptimalWritesParity176_SingleStillWrites6()
    {
        var range = QRVersionRange.AtMost(3);
        var optimal = QRCodeGenerator.CreateStructuredAppend(CodeGlyphXText, QREccLevel.M, Pinned with { Version = range, Segmentation = QRSegmentation.Optimal });
        await AssertIsTheKanjiSet(optimal, CodeGlyphXText, QREccLevel.M, QRSegmentation.Optimal, "Optimal");
        await Assert.That(Decode(optimal[0]).Header.Parity).IsEqualTo((byte)176);

        var single = QRCodeGenerator.CreateStructuredAppend(CodeGlyphXText, QREccLevel.M, Pinned with { Version = range });
        var (_, _, parity) = await AssertRoundTrip(single, CodeGlyphXText, "Single");
        await Assert.That(parity).IsEqualTo((byte)6);
        await AssertSameSet(single, QRCodeGenerator.CreateStructuredAppend(CodeGlyphXText, QREccLevel.M, Pinned with { Version = range, EciMode = EciMode.Utf8 }), "Single is today's UTF-8 set");
    }

    public static IEnumerable<(string Name, string Text, QREccLevel Ecc, int MaxVersion)> AllCellsSets() =>
    [
        ("cells-40-M-2", Cells(0, 40), QREccLevel.M, 2),
        ("cells-150-H-5", Cells(11, 150), QREccLevel.H, 5),
        ("cells-600-L-10", Cells(23, 600), QREccLevel.L, 10),
        ("sentence-cells", Repeat("こんにちは世界、日本語の分割テストです。", 8), QREccLevel.Q, 4),
    ];

    /// <summary>Every character has a cell: a Kanji set under both segmentations, never larger than the UTF-8 set it replaces.</summary>
    [Test]
    [MethodDataSource(nameof(AllCellsSets))]
    public async Task AllCells_IsAKanjiSet_UnderBothSegmentations(string name, string text, QREccLevel ecc, int maxVersion)
    {
        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            var options = Pinned with { Version = QRVersionRange.AtMost(maxVersion), Segmentation = segmentation };
            var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
            await Assert.That(set.Length).IsGreaterThan(1).Because(name);
            await AssertIsTheKanjiSet(set, text, ecc, segmentation, $"{name} {segmentation}");

            var ends = new int[StructuredAppendPlanner.MaxSymbols];
            StructuredAppendPlanner.TryPlanKanji(text, ecc, segmentation, 1, maxVersion, ends, out var count, out var version, out _);
            await Assert.That(set.Length).IsEqualTo(count).Because($"{name} {segmentation}: the planner's split");
            await Assert.That(set[0].Version).IsEqualTo(version).Because($"{name} {segmentation}: the planner's version");

            var utf8 = QRCodeGenerator.CreateStructuredAppend(text, ecc, options with { EciMode = EciMode.Utf8 });
            await Assert.That(set.Length < utf8.Length || (set.Length == utf8.Length && set[0].Version <= utf8[0].Version)).IsTrue().Because($"{name} {segmentation}: never larger than the UTF-8 set");
        }
    }

    public static IEnumerable<(string Name, string Text, QREccLevel Ecc, int MaxVersion)> WithAsciiSets() =>
    [
        ("codeglyphx-M-3", CodeGlyphXText, QREccLevel.M, 3),
        ("codeglyphx-L-2", CodeGlyphXText, QREccLevel.L, 2),
        ("kanji-digits-L-3", Repeat("日本7777", 21), QREccLevel.L, 3),
        ("order-lines-Q-4", Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 3), QREccLevel.Q, 4),
        ("interleaved-M-2", Repeat("a日b本c", 13), QREccLevel.M, 2),
        ("dosage-M-2", Repeat("1日", 31) + Repeat("1234567890", 8) + "7", QREccLevel.M, 2),
        ("url-M-3", Repeat("https://例え.jp/パス?q=123 ", 7), QREccLevel.M, 3),
    ];

    /// <summary>
    /// Text with ASCII in it: under Optimal the Kanji set where it needs fewer symbols than today's UTF-8 set, or as many at a lower version, and today's set otherwise; under Single today's set.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(WithAsciiSets))]
    public async Task WithAscii_OptimalTakesTheKanjiSetOnlyWhereItIsSmaller(string name, string text, QREccLevel ecc, int maxVersion)
    {
        var options = Pinned with { Version = QRVersionRange.AtMost(maxVersion) };
        var utf8Single = QRCodeGenerator.CreateStructuredAppend(text, ecc, options with { EciMode = EciMode.Utf8 });
        await AssertSameSet(QRCodeGenerator.CreateStructuredAppend(text, ecc, options), utf8Single, $"{name}: Single is today's set");

        var optimalOptions = options with { Segmentation = QRSegmentation.Optimal };
        var utf8 = QRCodeGenerator.CreateStructuredAppend(text, ecc, optimalOptions with { EciMode = EciMode.Utf8 });
        var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, optimalOptions);

        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        var kanjiPlanned = StructuredAppendPlanner.TryPlanKanji(text, ecc, QRSegmentation.Optimal, 1, maxVersion, ends, out var count, out var version, out _);
        var kanjiIsSmaller = kanjiPlanned && (count < utf8.Length || (count == utf8.Length && version < utf8[0].Version));
        if (kanjiIsSmaller)
        {
            await Assert.That(set.Length).IsEqualTo(count).Because(name);
            await AssertIsTheKanjiSet(set, text, ecc, QRSegmentation.Optimal, name);
        }
        else
        {
            await AssertSameSet(set, utf8, $"{name}: today's set");
        }
    }

    /// <summary>Whether the set is today's UTF-8 set, symbol for symbol.</summary>
    private static bool IsUtf8Set(QRCodeData[] set, string text, QREccLevel ecc, QRCodeGeneratorOptions options)
    {
        var utf8 = QRCodeGenerator.CreateStructuredAppend(text, ecc, options with { EciMode = EciMode.Utf8 });
        return utf8.Length == set.Length && utf8.Zip(set).All(p => p.First.Version == p.Second.Version && Modules(p.First).SequenceEqual(Modules(p.Second)));
    }

    /// <summary>Of the texts above, some become Kanji sets and some stay today's sets (the interleaved ones, whose Kanji runs pay a header every few characters), so the comparison has both outcomes to judge.</summary>
    [Test]
    public async Task WithAscii_BothOutcomesOccur()
    {
        var kanji = new List<string>();
        var utf8 = new List<string>();
        foreach (var (name, text, ecc, maxVersion) in WithAsciiSets())
        {
            var options = Pinned with { Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal };
            (IsUtf8Set(QRCodeGenerator.CreateStructuredAppend(text, ecc, options), text, ecc, options) ? utf8 : kanji).Add(name);
        }
        await Assert.That(kanji).IsNotEmpty();
        await Assert.That(utf8).Contains("interleaved-M-2");
    }

    /// <summary>
    /// A text one symbol holds in Kanji mode, or as a Kanji plan, but not as UTF-8: the set is Create's one symbol.
    /// Before sets learned Kanji it came out as a UTF-8 set of two or more.
    /// </summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task OneSymbolAsKanji_IsCreatesSymbol(QRSegmentation segmentation)
    {
        var text = segmentation == QRSegmentation.Single ? Cells(0, 10) : Repeat("日本7777", 10);
        var version = segmentation == QRSegmentation.Single ? 1 : 5;
        var ecc = segmentation == QRSegmentation.Single ? QREccLevel.L : QREccLevel.M;
        var options = Pinned with { Version = QRVersionRange.Exactly(version), Segmentation = segmentation };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out _, options with { EciMode = EciMode.Utf8 })).IsFalse();

        var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
        await Assert.That(set.Length).IsEqualTo(1);
        await Assert.That(Modules(set[0])).IsEquivalentTo(Modules(QRCodeGenerator.Create(text, ecc, options)), CollectionOrdering.Matching);
    }

    /// <summary>
    /// At the last bit: seven cells in one Kanji segment are 4 + 8 + 13 × 7 = 103 of version 1-Q's 104 bits, so the set is Create's one symbol; an eighth cell makes it a set of two.
    /// </summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task OneSymbolAsKanji_AtTheLastBit_IsCreatesSymbol(QRSegmentation segmentation)
    {
        var options = Pinned with { Version = QRVersionRange.Exactly(1), Segmentation = segmentation };
        var text = Cells(5, 7);
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.Q, options);
        await Assert.That(set.Length).IsEqualTo(1);
        await Assert.That(Modules(set[0])).IsEquivalentTo(Modules(QRCodeGenerator.Create(text, QREccLevel.Q, options)), CollectionOrdering.Matching);

        await Assert.That(QRCodeGenerator.CreateStructuredAppend(Cells(5, 8), QREccLevel.Q, options).Length).IsEqualTo(2);
    }

    /// <summary>The boost prices the set's Kanji streams: the level rises while the fullest chunk still fits.</summary>
    [Test]
    public async Task Boost_PricesTheKanjiStreams()
    {
        foreach (var (name, text, ecc, maxVersion) in AllCellsSets().Concat(WithAsciiSets()))
        {
            if (ecc == QREccLevel.H)
                continue;
            var options = Pinned with { Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal };
            var plain = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
            if (IsUtf8Set(plain, text, ecc, options))
                continue; // a UTF-8 set; its boost is the existing one's

            var chunks = plain.Select(s => Decode(s).Text).ToArray();
            var version = plain[0].Version;
            var fullest = chunks.Max(c => ReferenceChunkBits(c, version, QRSegmentation.Optimal));
            var level = ecc;
            while (level < QREccLevel.H && fullest <= StructuredAppendPlanner.Capacity(version, level + 1))
                level++;

            var boosted = QRCodeGenerator.CreateStructuredAppend(text, ecc, options with { BoostEccLevel = true });
            await Assert.That(boosted.Length).IsEqualTo(plain.Length).Because(name);
            for (var i = 0; i < boosted.Length; i++)
            {
                Run[] runs = [new Run('S', $"{i},{boosted.Length},{StructuredAppendPlanner.ParityKanji(text)}"), .. KanjiPlanReference.Runs(chunks[i], KanjiPlanReference.StandardQr(version))];
                var expected = KanjiSymbolBuilder.StandardQr(StandardQrStream(version, QRCodeConstants.GetEccInfo(version, level).TotalDataCodewords, runs), version, level, mask: 0);
                await Assert.That(Modules(boosted[i])).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{name}: symbol {i} at {level}");
            }
        }
    }

    /// <summary>
    /// The boost at a level the fullest chunk nearly fills: fourteen cells at version 1-L are two chunks of seven, each 20 + 4 + 8 + 13 × 7 = 123 bits, which M's 128 holds and Q's 104 does not.
    /// </summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task Boost_RisesToALevelTheFullestChunkNearlyFills(QRSegmentation segmentation)
    {
        var text = Cells(3, 14);
        var options = Pinned with { Version = QRVersionRange.Exactly(1), Segmentation = segmentation, BoostEccLevel = true };
        var boosted = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        await Assert.That(boosted.Length).IsEqualTo(2);
        await Assert.That(StructuredAppendPlanner.ChunkBitsKanji(text.AsSpan(0, 7), 1, segmentation)).IsEqualTo(123);
        await AssertIsTheKanjiSet(boosted, text, QREccLevel.M, segmentation, $"{segmentation}: boosted to M");
    }

    /// <summary>
    /// A text with ASCII in it plans first the set whose floor is lower, and the other only where that one's floor says it could still win.
    /// 「a日b本c」 × 200 at version 10-L at most: 10,000 bits of characters in 801 stretches put the Kanji floor at 10 symbols, and the UTF-8 set is 7, so no Kanji set is planned.
    /// The CodeGlyphX sentence × 45: the Kanji set is 7 symbols and the UTF-8 floor 11 (22,095 bits), so no UTF-8 set is planned.
    /// </summary>
    [Test]
    [Arguments("interleaved", "a日b本c", 200, 0, 1, false)]
    [Arguments("sentence", "こんにちは世界、QRコードの分割テストです。", 45, 1, 0, true)]
    public async Task WithAscii_PlansASecondSetOnlyWhereItCouldWin(string name, string unit, int repeat, int kanjiPlans, int utf8Plans, bool kanjiSet)
    {
        var text = Repeat(unit, repeat);
        var options = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.AtMost(10), Segmentation = QRSegmentation.Optimal };
        int kanji = QRCodeGenerator.KanjiSetPlans, utf8 = QRCodeGenerator.Utf8SetPlans;
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        kanji = QRCodeGenerator.KanjiSetPlans - kanji;
        utf8 = QRCodeGenerator.Utf8SetPlans - utf8;

        await Assert.That(kanji).IsEqualTo(kanjiPlans).Because(name);
        await Assert.That(utf8).IsEqualTo(utf8Plans).Because(name);
        await Assert.That(set.Length).IsEqualTo(7).Because(name);
        await Assert.That(IsUtf8Set(set, text, QREccLevel.L, options)).IsEqualTo(!kanjiSet).Because(name);
    }

    /// <summary>
    /// Texts of short units where kanji, digits and letters take turns, so that the two sets are close and their floors decide least.
    /// Whichever set is planned first and whichever is skipped, the set is the one planning both would choose, each set is planned at most once, and the corpus reaches every way the planning can go.
    /// </summary>
    [Test]
    public async Task WithAscii_SkippingASetNeverChangesTheSet()
    {
        string[] units = ["日", "1", "12", "a", "日本", "3錠", "x", "b", "回", "0"];
        var random = new Random(20260930);
        var corpus = new List<(string Text, QREccLevel Ecc, int MaxVersion)>
        {
            // The UTF-8 set is 6 symbols and the Kanji floor 7.
            (Repeat("a日b本c", 13), QREccLevel.M, 2),
            // The Kanji set is 2 symbols of version 4-M. The UTF-8 floor at 6-M is 2 as well, but 2 symbols of 4-M cannot hold its 1,473 bits of characters.
            (CodeGlyphXText, QREccLevel.M, 6),
            // The UTF-8 set is 2 symbols of version 1-Q, the lowest the range allows, and the Kanji floor is 2: the Kanji set could only tie, at a version no lower.
            ("123a本a123", QREccLevel.Q, 1),
            // The UTF-8 set is 2 symbols of 2-H and the Kanji set is not planned, but the Kanji plan (25 + 24 + 25 + 28 + 24 = 126 bits) is one symbol of 2-H, where UTF-8 (136) is not.
            ("日123本aa999", QREccLevel.H, 2),
        };
        for (var n = 0; n < 800; n++)
        {
            var builder = new System.Text.StringBuilder();
            var length = random.Next(10, 200);
            while (builder.Length < length)
                builder.Append(units[random.Next(units.Length)]);
            corpus.Add((builder.ToString(), (QREccLevel)random.Next(4), random.Next(1, 11)));
        }

        var paths = new HashSet<string>();
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        foreach (var (text, ecc, maxVersion) in corpus)
        {
            if (!text.Any(c => c >= 0x80) || !text.Any(c => c < 0x80))
                continue;

            var kanjiPlanned = StructuredAppendPlanner.TryPlanKanji(text, ecc, QRSegmentation.Optimal, 1, maxVersion, ends, out var kanjiCount, out var kanjiVersion, out _);
            var utf8Planned = StructuredAppendPlanner.TryPlan(text, ecc, EciMode.Utf8, EncodingMode.Byte, false, QRSegmentation.Optimal, 1, maxVersion, ends, out var utf8Count, out var utf8Version, out _, allowLanes: false);
            if (!kanjiPlanned && !utf8Planned)
                continue;
            var kanjiWins = kanjiPlanned && (!utf8Planned || kanjiCount < utf8Count || (kanjiCount == utf8Count && kanjiVersion < utf8Version));
            var count = kanjiWins ? kanjiCount : utf8Count;
            var version = kanjiWins ? kanjiVersion : utf8Version;

            var options = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal };
            int kanjiPlans = QRCodeGenerator.KanjiSetPlans, utf8Plans = QRCodeGenerator.Utf8SetPlans;
            var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
            kanjiPlans = QRCodeGenerator.KanjiSetPlans - kanjiPlans;
            utf8Plans = QRCodeGenerator.Utf8SetPlans - utf8Plans;

            var because = $"{text} at {ecc}, versions up to {maxVersion}";
            await Assert.That(kanjiPlans).IsBetween(0, 1).Because(because);
            await Assert.That(utf8Plans).IsBetween(0, 1).Because(because);
            var fitsOneSymbol = QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out _, options);
            await Assert.That(set.Length == 1).IsEqualTo(fitsOneSymbol).Because(because);
            if (fitsOneSymbol)
            {
                await Assert.That(Modules(set[0])).IsEquivalentTo(Modules(QRCodeGenerator.Create(text, ecc, options)), CollectionOrdering.Matching).Because(because);
                continue;
            }
            await Assert.That(set.Length).IsEqualTo(count).Because(because);
            await Assert.That(set[0].Version).IsEqualTo(version).Because(because);
            if (utf8Planned)
                await Assert.That(IsUtf8Set(set, text, ecc, options)).IsEqualTo(!kanjiWins).Because(because);

            // Which way the planning went, from the floors it starts from.
            var sixths = ModeSegmenter.CheapestSixthsKanji(text, out var utf8Sixths, out _, out _);
            var largest = StructuredAppendPlanner.Capacity(maxVersion, ecc);
            var kanjiFloor = StructuredAppendPlanner.FewestSymbolsKanji(largest, (sixths + 5) / 6, Stretches(text));
            var utf8Floor = StructuredAppendPlanner.FewestSymbols(largest, (utf8Sixths + 5) / 6, EciMode.Utf8);
            var first = kanjiFloor <= utf8Floor ? "Kanji first" : "UTF-8 first";
            paths.Add((kanjiPlans, utf8Plans) switch
            {
                (1, 1) => $"{first}, both planned, {(kanjiWins ? "Kanji" : "UTF-8")} taken",
                (1, 0) => $"{first}, UTF-8 skipped {(utf8Floor == kanjiCount ? "by its version" : "by its count")}",
                _ => $"{first}, Kanji skipped {(kanjiFloor == utf8Count ? "by its version" : "by its count")}",
            });
        }

        await Assert.That(paths).IsEquivalentTo(new[]
        {
            "Kanji first, both planned, Kanji taken", "Kanji first, both planned, UTF-8 taken",
            "Kanji first, UTF-8 skipped by its count", "Kanji first, UTF-8 skipped by its version",
            "UTF-8 first, both planned, Kanji taken", "UTF-8 first, both planned, UTF-8 taken",
            "UTF-8 first, Kanji skipped by its count", "UTF-8 first, Kanji skipped by its version",
        }, CollectionOrdering.Any).Because(string.Join(" | ", paths));
    }

    /// <summary>
    /// A Kanji set takes the scalar program (standardqr-encoder.md, "The lanes"), whose eighth state the lanes do not have: neither the Kanji set's budget search nor its writer runs lanes.
    /// The UTF-8 set it is compared with is the seven-state program's and may; so may the same text asked for as UTF-8, which it does where the machine accelerates lanes, and that is what makes the zeros mean something.
    /// </summary>
    [Test]
    public async Task KanjiSet_TakesTheScalarProgram()
    {
        var text = Repeat("日本語のテキスト、", 60) + Repeat("order 20260915 item 0000123456 qty 42 ", 150);
        var options = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.AtMost(20), Segmentation = QRSegmentation.Optimal };

        StructuredAppendPlanner.LaneBatches = 0;
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        StructuredAppendPlanner.TryPlanKanji(text, QREccLevel.L, QRSegmentation.Optimal, 1, 20, ends, out _, out _, out _);
        var laneBatches = StructuredAppendPlanner.LaneBatches;
        await Assert.That(laneBatches).IsEqualTo(0);

        QRCodeGenerator.LanePlanPasses = 0;
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        var lanePlanPasses = QRCodeGenerator.LanePlanPasses;
        await Assert.That(IsUtf8Set(set, text, QREccLevel.L, options)).IsFalse().Because("the text is a Kanji set");
        await Assert.That(lanePlanPasses).IsEqualTo(0);

        if (!ModeSegmenter.LanesAccelerated)
            return;
        StructuredAppendPlanner.LaneBatches = 0;
        QRCodeGenerator.LanePlanPasses = 0;
        QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options with { EciMode = EciMode.Utf8 });
        var utf8Lanes = StructuredAppendPlanner.LaneBatches + QRCodeGenerator.LanePlanPasses;
        await Assert.That(utf8Lanes).IsGreaterThan(0);
    }

    /// <summary>A Kanji set's refusal counts characters in Kanji mode.</summary>
    [Test]
    public async Task Refusal_OfAnAllCellsText_CountsKanjiCharacters()
    {
        var text = Cells(0, 200);
        var ex = await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.H, new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.AtMost(1) })).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("mode: Kanji");
        await Assert.That(ex.Message).Contains("200 data units");
    }

    /// <summary>The advice plans the text the way it advises: a text with ASCII in it that only a Kanji set holds is told to use Optimal.</summary>
    [Test]
    public async Task Refusal_UnderSingle_AdvisesOptimalWhereOnlyAKanjiSetHolds()
    {
        // A version 2-L symbol holds about 19 characters of this text as a Kanji plan and 9 as UTF-8: 162 characters are about ten Kanji symbols and eighteen UTF-8 ones.
        var text = Repeat("日本語のテキスト7", 18);
        var single = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.AtMost(2) };
        var ex = await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, single)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("use QRSegmentation.Optimal");

        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, single with { Segmentation = QRSegmentation.Optimal, MaskPattern = 0, QuietZoneSize = 0 });
        await AssertIsTheKanjiSet(set, text, QREccLevel.L, QRSegmentation.Optimal, "the advised set");
    }

    /// <summary>Without <c>AllowKanji</c> the advice plans the other segmentation without Kanji too: the same text, which only a Kanji set holds, is not told to use Optimal, where its UTF-8 set does not fit either.</summary>
    [Test]
    public async Task Refusal_WithoutAllowKanji_DoesNotAdviseAKanjiSet()
    {
        var text = Repeat("日本語のテキスト7", 18);
        var single = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2) };
        var ex = await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, single)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).DoesNotContain("use QRSegmentation.Optimal");
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, single with { Segmentation = QRSegmentation.Optimal })).Throws<ArgumentException>();
    }
}
