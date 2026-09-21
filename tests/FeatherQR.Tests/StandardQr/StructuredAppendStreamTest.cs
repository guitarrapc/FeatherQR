using System.Text;
using FeatherQR.Internals.StandardQR;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// What the symbols of a set carry on the wire, read out of the data codewords an independent
/// decoder recovers, since this library's decoder hides exactly the things pinned here: it consumes
/// a byte order mark, and it reports the same text whether or not a symbol declares its charset.
/// Also the edges of the entry point's contract: the last bits of a single symbol, a byte order mark
/// inside the text, a forced charset that does not hold the text, and what the refusal says.
/// </summary>
public class StructuredAppendStreamTest
{
    private const char Mark = (char)0xFEFF;
    private const string OrderLine = "order 20260915 item 0000123456 qty 42 ";

    [Test]
    [Arguments(QRSegmentation.Single, "こんにちは世界、QRコードの分割テストです。", 5, 200)]
    [Arguments(QRSegmentation.Optimal, OrderLine, 6, 600)]
    public async Task Utf8Bom_IsTheFirstThreeBytesOfTheFirstSymbolAndOfNoOther(QRSegmentation segmentation, string unit, int maxVersion, int length)
    {
        var text = Repeat(unit, length);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion), Segmentation = segmentation, EciMode = EciMode.Utf8, Utf8Bom = true };
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, options);
        await Assert.That(symbols.Length).IsGreaterThan(1);

        for (var i = 0; i < symbols.Length; i++)
        {
            var stream = new Stream(DataCodewords(symbols[i]));
            var header = stream.ReadSetHeader();
            await Assert.That(stream.ReadEci()).IsEqualTo(26).Because($"symbol {i}");
            // The first symbol opens with a Byte run whose first three bytes are the mark; no later Byte run opens with one.
            var opensInByteMode = stream.Read(4) == 0b0100;
            if (i == 0)
                await Assert.That(opensInByteMode).IsTrue();
            var opensWithMark = false;
            if (opensInByteMode)
            {
                stream.Read(symbols[i].Version < 10 ? 8 : 16);
                opensWithMark = stream.Read(8) == 0xEF && stream.Read(8) == 0xBB && stream.Read(8) == 0xBF;
            }
            await Assert.That(opensWithMark).IsEqualTo(i == 0).Because($"symbol {i} of {symbols.Length}");
            await Assert.That(header.Index).IsEqualTo(i);
        }

        // And the parity counts it once.
        var expected = 0xEF ^ 0xBB ^ 0xBF;
        foreach (var b in Encoding.UTF8.GetBytes(text))
            expected ^= b;
        await Assert.That(QRCodeDecoder.TryDecode(symbols[0], out _, out var info)).IsTrue();
        await Assert.That((int)info.StructuredAppend.Parity).IsEqualTo(expected);
    }

    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task Utf8Bom_IsNotWrittenNorCountedWhenTheFirstSymbolIsNotByteMode(QRSegmentation segmentation)
    {
        // The mark belongs at the head of the byte stream, and a first symbol of digits has no byte stream to head.
        var text = new string('7', 400) + new string('x', 400);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(5), Segmentation = segmentation, EciMode = EciMode.Utf8, Utf8Bom = true };
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);

        var expected = 0;
        foreach (var b in Encoding.UTF8.GetBytes(text))
            expected ^= b;
        for (var i = 0; i < symbols.Length; i++)
        {
            var stream = new Stream(DataCodewords(symbols[i]));
            var header = stream.ReadSetHeader();
            await Assert.That(header.Parity).IsEqualTo(expected).Because($"symbol {i}");
            await Assert.That(stream.ReadEci()).IsEqualTo(26).Because($"symbol {i}");
            if (stream.Read(4) == 0b0100)
            {
                stream.Read(symbols[i].Version < 10 ? 8 : 16);
                var opensWithMark = stream.Read(8) == 0xEF && stream.Read(8) == 0xBB && stream.Read(8) == 0xBF;
                await Assert.That(opensWithMark).IsFalse().Because($"symbol {i}");
            }
        }
    }

    [Test]
    public async Task ForcedCharset_IsDeclaredInEverySymbol()
    {
        // ASCII reads the same with or without the declaration, so it is looked for on the wire.
        var text = Repeat("The quick brown fox jumps over the lazy dog. ", 300);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), EciMode = EciMode.Utf8 };
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, options);
        await Assert.That(symbols.Length).IsGreaterThan(2);

        for (var i = 0; i < symbols.Length; i++)
        {
            var stream = new Stream(DataCodewords(symbols[i]));
            stream.ReadSetHeader();
            await Assert.That(stream.ReadEci()).IsEqualTo(26).Because($"symbol {i} of {symbols.Length}");
        }

        var plain = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3) });
        var undeclared = new Stream(DataCodewords(plain[1]));
        undeclared.ReadSetHeader();
        await Assert.That(undeclared.ReadEci()).IsEqualTo(-1);
    }

    [Test]
    public async Task Boost_UnderOptimal_IsDecidedByWhatThePlansCost()
    {
        // Two symbols of version 10 whose plans leave room for level M; as single-mode streams they do not.
        var text = Repeat("id" + new string('7', 40), 600);
        var optimal = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), Segmentation = QRSegmentation.Optimal, BoostEccLevel = true });
        var plain = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), Segmentation = QRSegmentation.Optimal });

        await Assert.That(optimal.Length).IsEqualTo(plain.Length);
        var parts = new StringBuilder();
        foreach (var symbol in optimal)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out var info)).IsTrue();
            await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.M);
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(text);
    }

    [Test]
    // Fourteen bytes at version 1 are two chunks, and only the first carries the byte order mark: its 116 bits fit level M's
    // 128, which the second chunk's would not were it charged the mark too.
    [Arguments("a", 14, 1, true, QRSegmentation.Single, QREccLevel.M)]
    // Thirty at version 2 put the mark's chunk first and fullest (180 bits): level Q's 176 would hold it only were it not charged.
    [Arguments("a", 30, 2, true, QRSegmentation.Single, QREccLevel.M)]
    // Seventy-seven digits at version 2 fill level Q to the bit (20 + 12 + 4 + 10 + 130 = 176), which is a fit.
    [Arguments("7", 77, 2, false, QRSegmentation.Single, QREccLevel.Q)]
    [Arguments("7", 77, 2, false, QRSegmentation.Optimal, QREccLevel.Q)]
    public async Task Boost_ReachesTheLevelTheFullestChunkFits(string unit, int length, int version, bool utf8Bom, QRSegmentation segmentation, QREccLevel expected)
    {
        var text = Repeat(unit, length);
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), EciMode = EciMode.Utf8, Utf8Bom = utf8Bom, Segmentation = segmentation, BoostEccLevel = true });

        await Assert.That(symbols.Length).IsEqualTo(2);
        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            await Assert.That(info.EccLevel).IsEqualTo(expected);
        }
    }

    [Test]
    // Prose: every plan is its single Byte run, so the writer plans nothing.
    [Arguments("prose", "The quick brown fox jumps over the lazy dog. ", 3_000, 10, QREccLevel.M, EciMode.Default, false, 15, 0, 0, 0)]
    // Order lines of twelve symbols: two passes plan them, eight and four together, and none alone.
    [Arguments("order-lines", OrderLine, 2_600, 10, QREccLevel.M, EciMode.Default, false, 12, 0, 2, 0)]
    // Three are the fewest a pass takes.
    [Arguments("three", OrderLine, 650, 10, QREccLevel.M, EciMode.Default, false, 3, 0, 1, 0)]
    // A label-sized set of two, below what a pass takes, so planned alone; its plan rules out one symbol, so the question is not asked.
    [Arguments("label", OrderLine, 400, 10, QREccLevel.M, EciMode.Default, false, 2, 2, 0, 0)]
    // Seventeen bytes at version 1-L are one symbol only without the set header, which the question finds.
    [Arguments("one-symbol", "a", 17, 1, QREccLevel.L, EciMode.Default, false, 1, 0, 0, 1)]
    // A plan of 150 bits (three bytes, then thirty digits) and the 12 of a declared charset are past 152 at 1-L, which rules the question out.
    [Arguments("declared", "xyz012345678901234567890123456789", 33, 1, QREccLevel.L, EciMode.Utf8, false, 2, 1, 0, 0)]
    // Under a byte order mark only the first symbol writes its single-mode stream; the other eleven still go through two passes.
    [Arguments("order-lines-marked", OrderLine, 2_600, 10, QREccLevel.M, EciMode.Utf8, true, 12, 0, 2, 0)]
    // With ten, the other nine are one pass of eight and one alone; were the first given a lane too, two would be alone.
    [Arguments("order-lines-marked-ten", OrderLine, 2_000, 10, QREccLevel.M, EciMode.Utf8, true, 10, 1, 1, 0)]
    // Where no plan helps the bound is the stream's: seventeen bytes (148 bits) and the charset's 12 are past 152,
    [Arguments("stream-declared", "a", 17, 1, QREccLevel.L, EciMode.Utf8, false, 2, 0, 0, 0)]
    // and so are fifteen with the byte order mark's 24 (4 + 8 + 120 + 24 + 12 = 168).
    [Arguments("stream-marked", "a", 15, 1, QREccLevel.L, EciMode.Utf8, true, 2, 0, 0, 0)]
    public async Task Writer_SpendsWhatTheSetNeeds(string name, string unit, int length, int maxVersion, QREccLevel ecc, EciMode charset, bool utf8Bom, int symbols, int chunkPlans, int lanePlanPasses, int oneSymbolQuestions)
    {
        // What the writer skips is invisible in the symbols, so it is counted. A change that moves these must say why, with a measurement.
        var text = Repeat(unit, length);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal, EciMode = charset, Utf8Bom = utf8Bom };
        int plans = QRCodeGenerator.ChunkPlans, passes = QRCodeGenerator.LanePlanPasses, questions = QRCodeGenerator.OneSymbolQuestions;
        var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
        plans = QRCodeGenerator.ChunkPlans - plans;
        passes = QRCodeGenerator.LanePlanPasses - passes;
        questions = QRCodeGenerator.OneSymbolQuestions - questions;

        await Assert.That(set.Length).IsEqualTo(symbols).Because(name);
        await Assert.That(questions).IsEqualTo(oneSymbolQuestions).Because(name);
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            await Assert.That(plans).IsEqualTo(chunkPlans).Because(name);
            await Assert.That(passes).IsEqualTo(lanePlanPasses).Because(name);
        }
#endif
    }

    [Test]
    public async Task PlanThatFillsASymbolToTheBit_IsWrittenAsPlanned()
    {
        // The first chunk's plan is the symbol's capacity exactly (152 bits); its single-mode stream is not (176), so it has to be the plan that is written.
        var text = "ab1234567ab1234567ab1234567ab12345";
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), Segmentation = QRSegmentation.Optimal });

        await Assert.That(symbols.Length).IsEqualTo(2);
        var parts = new StringBuilder();
        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(text);
    }

    [Test]
    public async Task Refusal_NamesTheLargestVersionAndWhatTheTextIs()
    {
        // The version the set was refused at is the range's largest, and the text's mode, charset and length say what was asked of it,
        // as Create's refusal does: a declared charset that doubles every byte is visible there.
        var byVersion = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(new string('a', 5_000), QREccLevel.H, new QRCodeGeneratorOptions { Version = new QRVersionRange(5, 10) }));
        await Assert.That(byVersion.Message).StartsWith("Content does not fit 16 Structured Append symbols of version 10 at ECC level H (mode: Byte, ECI: Default, 5000 data units).");
        var byCharset = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(new string((char)0xE9, 110), QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), EciMode = EciMode.Utf8 }));
        await Assert.That(byCharset.Message).Contains("(mode: Byte, ECI: Utf8, 220 data units).");
        // What the text resolved to, not what the options asked: Latin-1 letters resolve to ISO-8859-1, digits to Numeric.
        var one = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        var latin1 = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(new string((char)0xE9, 3_000), QREccLevel.L, one));
        await Assert.That(latin1.Message).Contains("(mode: Byte, ECI: Iso8859_1, 3000 data units).");
        var digits = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(new string('7', 20_000), QREccLevel.L, one));
        await Assert.That(digits.Message).Contains("(mode: Numeric, ECI: Default, 20000 data units).");
    }

    [Test]
    public async Task Set_WhoseFirstChunkIsItsShortest_IsWritten()
    {
        // Japanese ahead of order lines: the first chunks are a third the length of the later ones, and the buffers the writer
        // sizes by the longest chunk are sized by the longest, not the first.
        var text = Repeat("日本語のテキスト、", 200) + Repeat(OrderLine, 2_000);
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(10), Segmentation = QRSegmentation.Optimal });

        await Assert.That(symbols.Length).IsEqualTo(13);
        var parts = new StringBuilder();
        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(text);
    }

    [Test]
    // A text with a mark a line past sixteen symbols by the cheapest bound, marks or not: of any version, and of the
    // range and level asked for, which is the bound that holds.
    [Arguments(1_000_000, 40, QREccLevel.L)]
    [Arguments(20_000, 10, QREccLevel.L)]
    [Arguments(60_000, 40, QREccLevel.H)]
    public async Task Refusal_OfATextNoSplitHolds_DoesNotCopyItToReplaceItsMarks(int length, int maxVersion, QREccLevel ecc)
    {
        var text = Repeat(OrderLine + "\n" + Mark, length);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal };
        Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, ecc, options));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, ecc, options));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A copy is two bytes a character; the message and the exception are a few kilobytes.
        await Assert.That(allocated).IsLessThan(text.Length);
    }

    public static IEnumerable<(string Name, string Text, QREccLevel Ecc, QRVersionRange Range)> LastBitsOfOneSymbol()
    {
        // The most Create holds at the range's largest version: the set's 20 header bits are not owed by a symbol that is not part of a set.
        yield return ("bytes-v1", new string('a', 17), QREccLevel.L, QRVersionRange.AtMost(1));
        yield return ("digits-v1", new string('7', 41), QREccLevel.L, QRVersionRange.AtMost(1));
        yield return ("alnum-v1", new string('A', 25), QREccLevel.L, QRVersionRange.AtMost(1));
        yield return ("bytes-v40", new string('a', 2953), QREccLevel.L, QRVersionRange.Any);
        // The densest a symbol gets, ten bits to three characters: the longest text that can be one symbol at all.
        yield return ("digits-v40", new string('7', 7089), QREccLevel.L, QRVersionRange.Any);
        yield return ("bytes-v40-q", new string('a', 1663), QREccLevel.Q, QRVersionRange.Any);
        yield return ("bytes-v9", new string('a', 230), QREccLevel.L, QRVersionRange.AtMost(9));
        yield return ("bytes-v10", new string('a', 271), QREccLevel.L, QRVersionRange.AtMost(10));
        yield return ("one-emoji-v1-h", "\U0001F600", QREccLevel.H, QRVersionRange.Exactly(1));
        // Six bytes that one symbol holds to the bit and that a set cuts into three: "a", the kana, "aa".
        yield return ("three-chunks-as-a-set-v1-h", "aあaa", QREccLevel.H, QRVersionRange.Exactly(1));
    }

    [Test]
    [MethodDataSource(nameof(LastBitsOfOneSymbol))]
    public async Task TextThatCreateHoldsInOneSymbol_IsThatSymbol(string name, string text, QREccLevel ecc, QRVersionRange range)
    {
        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            var options = new QRCodeGeneratorOptions { Version = range, Segmentation = segmentation };
            var one = QRCodeGenerator.Create(text, ecc, options);
            var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);

            await Assert.That(set.Length).IsEqualTo(1).Because($"{name} under {segmentation}");
            await Assert.That(set[0].GetRawData().AsSpan().SequenceEqual(one.GetRawData())).IsTrue().Because($"{name} under {segmentation}");
        }
    }

    [Test]
    public async Task TextWhosePlanFillsASymbolToTheBit_IsThatSymbol()
    {
        // The planner says which texts are worth asking the one-symbol question of, by the whole text's plan
        // against the symbol without the set header; a plan that fills a symbol exactly is the edge of that.
        // Every tail of up to four characters over a digit, an alphanumeric and a byte: some fill a symbol to the bit.
        var tails = new List<string> { "" };
        for (var from = 0; tails[from].Length < 4; from++)
        {
            foreach (var c in "7Ax")
                tails.Add(tails[from] + c);
        }

        var found = 0;
        for (var version = 2; version <= 9; version++)
        {
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(version), Segmentation = QRSegmentation.Optimal };
                var capacity = StructuredAppendPlanner.Capacity(version, ecc);
                for (var length = capacity / 8; length <= capacity / 4; length++)
                {
                    var lead = Repeat(OrderLine, length);
                    if (Math.Abs(QRSegmentPlanner.MinimumPayloadBits(lead, EciMode.Default, 10, 9, 8) - capacity) > 48)
                        continue;

                    foreach (var tail in tails)
                    {
                        var text = lead + tail;
                        if (QRSegmentPlanner.MinimumPayloadBits(text, EciMode.Default, 10, 9, 8) != capacity)
                            continue;

                        found++;
                        var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
                        await Assert.That(set.Length).IsEqualTo(1).Because($"version {version}-{ecc}: {length} characters and {tail}");
                    }
                }
            }
        }

        await Assert.That(found).IsGreaterThan(0);
    }

    [Test]
    public async Task LongestTextAPlanHoldsInOneSymbol_IsThatSymbol()
    {
        // Under Optimal the one symbol may be held by its plan alone; the longest such text is found, not guessed.
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(5), Segmentation = QRSegmentation.Optimal };
        var longest = 0;
        for (var length = 100; length <= 400; length++)
        {
            try
            {
                QRCodeGenerator.Create(Repeat(OrderLine, length), QREccLevel.L, options);
                longest = length;
            }
            catch (ArgumentException)
            {
                break;
            }
        }

        // Past what one Byte run holds at version 5-L (106 bytes), so it is the plan that holds it.
        await Assert.That(longest).IsGreaterThan(106);
        var text = Repeat(OrderLine, longest);
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        await Assert.That(set.Length).IsEqualTo(1);
        await Assert.That(set[0].GetRawData().AsSpan().SequenceEqual(QRCodeGenerator.Create(text, QREccLevel.L, options).GetRawData())).IsTrue();
    }

    [Test]
    public async Task OneCharacterPastWhatCreateHolds_IsTwoSymbolsWithHeaders()
    {
        var text = new string('a', 18);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(1) };
        Assert.Throws<ArgumentException>(() => QRCodeGenerator.Create(text, QREccLevel.L, options));

        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        await Assert.That(set.Length).IsEqualTo(2);
        foreach (var symbol in set)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            await Assert.That(info.StructuredAppend.Count).IsEqualTo(2);
        }
    }

    public static IEnumerable<(string Name, string Text, QREccLevel Ecc, QRVersionRange Range, bool Boost)> MarksInsideTheText()
    {
        var unit = new string('7', 20) + Mark + "abc";
        yield return ("plan-opens-a-byte-run-at-the-mark", unit + unit, QREccLevel.L, QRVersionRange.AtMost(1), false);
        // A mark every unit, at lengths no single-mode stream holds: the plan the search priced the text by is the one the writer has to accept, or nothing fits.
        yield return ("mark-a-unit-one-symbol", Repeat(unit, 3_000), QREccLevel.L, QRVersionRange.Any, false);
        yield return ("mark-a-unit-a-set", Repeat(unit, 9_000), QREccLevel.L, QRVersionRange.Any, false);
        yield return ("mark-a-unit-below-the-band", Repeat(unit, 3_000), QREccLevel.L, QRVersionRange.AtMost(12), false);
        yield return ("one-symbol-by-its-plan-only", new string('7', 3000) + Mark + "abcdefghij", QREccLevel.L, QRVersionRange.Any, false);
        yield return ("boost-priced-by-a-plan-around-the-mark", Repeat("12345678901234567890" + Mark + "abcdefghijkl", 400), QREccLevel.L, QRVersionRange.Exactly(10), true);
        yield return ("long", Repeat(new string('7', 28) + Mark, 9000), QREccLevel.M, QRVersionRange.Any, false);
        yield return ("split-lands-before-the-mark", new string('a', 6) + Mark + new string('b', 5), QREccLevel.L, QRVersionRange.Exactly(1), false);
        yield return ("marks-in-a-row", "ab" + new string(Mark, 4) + "cdefghijklmnopqrstuvwxyz", QREccLevel.L, QRVersionRange.Exactly(1), false);
        // A mark a line: some cut lands just before one and is moved off it, in the walk at eight budgets as in the scalar one.
        yield return ("marks-after-line-feeds", Repeat("order 20260915 item 0000123456 qty 42" + (char)0x0A + Mark, 9_000), QREccLevel.Q, QRVersionRange.Between(10, 26), false);
        // The planner holds this in two symbols of version 9 by a plan that keeps the mark off the head of its Byte run.
        yield return ("two-symbols-by-a-plan-around-the-mark", "x" + new string('7', 300) + "abc123456" + Mark + "xyz", QREccLevel.L, QRVersionRange.Between(9, 10), false);
    }

    [Test]
    [MethodDataSource(nameof(MarksInsideTheText))]
    public async Task MarkInsideTheText_IsEncodedAndComesBack(string name, string text, QREccLevel ecc, QRVersionRange range, bool boost)
    {
        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            var options = new QRCodeGeneratorOptions { Version = range, Segmentation = segmentation, BoostEccLevel = boost };
            var set = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);

            var parts = new StringBuilder();
            foreach (var symbol in set)
            {
                await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue().Because($"{name} under {segmentation}");
                parts.Append(part);
            }
            await Assert.That(parts.ToString()).IsEqualTo(text).Because($"{name} under {segmentation}: {set.Length} symbols");
        }
    }

    [Test]
    public async Task MarkInsideTheText_ComesBackWhereverTheSplitFalls()
    {
        // The split walks across the mark as the text before it grows a character at a time; the
        // character ahead of the mark is one UTF-16 unit, or a pair that the cut has to clear whole.
        foreach (var ahead in new[] { "", "\U0001F600" })
        {
            foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
            {
                for (var version = 1; version <= 3; version++)
                {
                    for (var lead = 1; lead <= 60; lead++)
                    {
                        var text = new string('a', lead) + ahead + Mark + new string('b', 40);
                        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), Segmentation = segmentation });
                        var parts = new StringBuilder();
                        foreach (var symbol in set)
                        {
                            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
                            parts.Append(part);
                        }
                        await Assert.That(parts.ToString()).IsEqualTo(text).Because($"{lead} characters and a pair ({ahead.Length > 0}) before the mark at version {version} under {segmentation}: {set.Length} symbols");
                    }
                }
            }
        }
    }

    [Test]
    public async Task MarkAfterACharacterOnlyByteEncodes_CostsNoSymbols()
    {
        // A mark changes a plan only where it would open a Byte run, which needs the character
        // before it to sit in a Numeric or Alphanumeric run. After a line feed, where concatenated files put
        // one, the set is the unmarked text's set in count, and fewer symbols than the single-mode set.
        var random = new Random(7);
        var sb = new StringBuilder();
        for (var n = 0; n < 2000; n++)
        {
            sb.Append("id:");
            for (var d = 0; d < 16; d++)
                sb.Append((char)('0' + random.Next(10)));
            sb.Append('\n');
        }
        var unmarked = sb.ToString();
        var marked = unmarked.Insert(20, Mark.ToString());
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };

        var markedSet = QRCodeGenerator.CreateStructuredAppend(marked, QREccLevel.L, optimal);
        var unmarkedSet = QRCodeGenerator.CreateStructuredAppend(unmarked, QREccLevel.L, optimal);
        var singleSet = QRCodeGenerator.CreateStructuredAppend(marked, QREccLevel.L);

        await Assert.That(markedSet.Length).IsEqualTo(unmarkedSet.Length);
        await Assert.That(markedSet.Length).IsLessThan(singleSet.Length);
        var parts = new StringBuilder();
        foreach (var symbol in markedSet)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(marked);
    }

    [Test]
    public async Task MarkAtTheHeadOfTheText_DoesNotChangeHowTheSetIsPriced()
    {
        // No symbol but the first can begin with it, and the first is a single symbol's case.
        var body = Repeat("id" + new string('7', 40), 600);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), Segmentation = QRSegmentation.Optimal };

        var withMark = QRCodeGenerator.CreateStructuredAppend(Mark + body, QREccLevel.L, options);
        var without = QRCodeGenerator.CreateStructuredAppend(body, QREccLevel.L, options);
        var single = QRCodeGenerator.CreateStructuredAppend(Mark + body, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10) });

        await Assert.That(withMark.Length).IsEqualTo(without.Length);
        await Assert.That(withMark.Length).IsLessThan(single.Length);
    }

    [Test]
    public async Task MarkAfterADigitRun_CostsTheSetNoSymbolAndNoVersion()
    {
        // The unconstrained plan opens a Byte run at such a mark; the plan written takes the digit before it
        // into the run instead, eight bits dearer, and the set is the unmarked text's in count and version.
        // Forty-eight thousand characters are sixteen symbols of version 40, with nothing to spare for a set priced any dearer.
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        foreach (var length in new[] { 40_000, 48_000 })
        {
            var unmarked = Repeat("order 20260915 item 0000123456 qty 42\n", length);
            var at = unmarked.IndexOf("0000123456", length / 2, StringComparison.Ordinal) + 10;
            var marked = unmarked.Insert(at, Mark.ToString());

            var markedSet = QRCodeGenerator.CreateStructuredAppend(marked, QREccLevel.L, optimal);
            var unmarkedSet = QRCodeGenerator.CreateStructuredAppend(unmarked, QREccLevel.L, optimal);

            await Assert.That(markedSet.Length).IsEqualTo(unmarkedSet.Length).Because($"{length} characters");
            await Assert.That(markedSet[0].Version).IsEqualTo(unmarkedSet[0].Version).Because($"{length} characters");
            var parts = new StringBuilder();
            foreach (var symbol in markedSet)
            {
                await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
                parts.Append(part);
            }
            await Assert.That(parts.ToString()).IsEqualTo(marked).Because($"{length} characters");
        }
    }

    [Test]
    public async Task MarkUnderAForcedLatin1_TakesThePlanThatOpensAByteRunAtIt()
    {
        // A declared ISO-8859-1 stops the decoder dropping anything and the mark is a replacement byte, so the
        // plan that opens a Byte run at it is the plan written, and the chunks sized by it fit. The mark is then
        // one Byte-only byte like '?', so the set is the set of the text with '?' in its place, to the module:
        // a plan that kept the Byte run from opening at it would pull a digit into the run and differ.
        var text = Repeat(new string('7', 40) + Mark + "ab", 2_500);
        var latin1 = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), Segmentation = QRSegmentation.Optimal, EciMode = EciMode.Iso8859_1 };
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, latin1);
        var twin = QRCodeGenerator.CreateStructuredAppend(text.Replace(Mark, '?'), QREccLevel.L, latin1);
        var single = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), EciMode = EciMode.Iso8859_1 });

        await Assert.That(set.Length).IsLessThan(single.Length);
        await Assert.That(set.Length).IsEqualTo(twin.Length);
        for (var i = 0; i < set.Length; i++)
            await Assert.That(set[i].GetRawData().AsSpan().SequenceEqual(twin[i].GetRawData())).IsTrue().Because($"symbol {i}");
        var parts = new StringBuilder();
        foreach (var symbol in set)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(text.Replace(Mark, '?'));
    }

    [Test]
    public async Task MarkUnderAForcedLatin1_IsAnOrdinaryCharacter()
    {
        // The charset is declared, so the decoder drops nothing, and the mark is written as the
        // transcoder's replacement anyway: no cut has to be kept off it.
        var text = "a" + new string(Mark, 30);
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(1), EciMode = EciMode.Iso8859_1 });

        await Assert.That(set.Length).IsEqualTo(3);
        var parts = new StringBuilder();
        foreach (var symbol in set)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.Length).IsEqualTo(text.Length);
        await Assert.That(parts[0]).IsEqualTo('a');
    }

    [Test]
    public async Task OneSymbolText_DoesNotAnswerForTheSizeOfItsQuietZone()
    {
        // Create holds it, so the set is that symbol; the quiet zone is not part of the question.
        // Seventeen bytes are in the last bits of version 1-L, where the planner splits and the question is asked.
        var text = new string('a', 17);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(1), QuietZoneSize = 30_000 };
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);

        await Assert.That(set.Length).IsEqualTo(1);
        await Assert.That(set[0].Size).IsEqualTo(QRCodeGenerator.Create(text, QREccLevel.L, options).Size);
    }

    [Test]
    public async Task Parity_IsOfTheBytesWritten_WhenAForcedCharsetDoesNotHoldTheText()
    {
        // A euro sign under a forced ISO-8859-1 is written as whatever the Byte writer makes of it; the parity is of that byte.
        var text = Repeat("price 12€ ", 310);
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(5), EciMode = EciMode.Iso8859_1 });

        var written = 0;
        var parity = -1;
        foreach (var symbol in set)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out var info)).IsTrue();
            parity = info.StructuredAppend.Parity;
            foreach (var c in part)
                written ^= (byte)c;
        }
        await Assert.That(parity).IsEqualTo(written);
    }

    public static IEnumerable<(string Name, string Text, QREccLevel Ecc, QRVersionRange Range, QRSegmentation Segmentation, string? Advice)> Refusals()
    {
        var letters = new string('a', 30_000);
        var records = Repeat("k=" + new string('7', 12) + ";", 48_000);
        // Nothing holds sixty thousand letters or a hundred thousand of prose, whatever is changed: the refusal says what is so and stops.
        // Thirty thousand letters fit level M and not Q, so the level named is not simply the next one down.
        yield return ("nothing-helps", new string('a', 60_000), QREccLevel.H, QRVersionRange.AtMost(10), QRSegmentation.Single, "");
        yield return ("nothing-helps-prose", Repeat("lorem ipsum dolor sit amet; ", 100_000), QREccLevel.M, QRVersionRange.Any, QRSegmentation.Optimal, "");
        yield return ("widen", letters, QREccLevel.L, QRVersionRange.AtMost(10), QRSegmentation.Single, "Widen the version range.");
        yield return ("lower", letters, QREccLevel.H, QRVersionRange.Any, QRSegmentation.Single, "Lower the ECC level to M.");
        yield return ("either", new string('a', 3_000), QREccLevel.H, QRVersionRange.AtMost(10), QRSegmentation.Single, "Widen the version range or lower the ECC level to M.");
        yield return ("both", letters, QREccLevel.H, QRVersionRange.AtMost(10), QRSegmentation.Single, "Widen the version range and lower the ECC level to M.");
        yield return ("plan", records, QREccLevel.L, QRVersionRange.Any, QRSegmentation.Single, "Use QRSegmentation.Optimal.");
        yield return ("plan-under-a-capped-range", records, QREccLevel.L, QRVersionRange.AtMost(39), QRSegmentation.Single, "Use QRSegmentation.Optimal.");
        yield return ("widen-and-plan", records, QREccLevel.L, QRVersionRange.AtMost(36), QRSegmentation.Single, "Widen the version range and use QRSegmentation.Optimal.");
        yield return ("lower-or-plan", records.Substring(0, 38_000), QREccLevel.M, QRVersionRange.Any, QRSegmentation.Single, "Lower the ECC level to L or use QRSegmentation.Optimal.");
        yield return ("lower-or-plan-under-a-capped-range", records.Substring(0, 38_000), QREccLevel.M, QRVersionRange.AtMost(39), QRSegmentation.Single, "Lower the ECC level to L or use QRSegmentation.Optimal.");
        yield return ("any-of-three", records.Substring(0, 24_000), QREccLevel.M, QRVersionRange.AtMost(30), QRSegmentation.Single, "Widen the version range, lower the ECC level to L or use QRSegmentation.Optimal.");
        yield return ("all-three", records, QREccLevel.M, QRVersionRange.AtMost(36), QRSegmentation.Single, "Widen the version range, lower the ECC level to L and use QRSegmentation.Optimal.");
        yield return ("first-pair-that-works", records.Substring(0, 38_000), QREccLevel.M, QRVersionRange.AtMost(10), QRSegmentation.Single, "Widen the version range and lower the ECC level to L.");
        // Both pairs with a plan in them work here, and the first in order is the one given.
        yield return ("first-of-two-pairs-that-work", Repeat("k" + new string('7', 40), 60_000), QREccLevel.M, QRVersionRange.AtMost(36), QRSegmentation.Single, "Widen the version range and use QRSegmentation.Optimal.");
        // What a lower level holds may be the one symbol, which carries no set header: the caller gets that symbol, so it counts.
        yield return ("one-symbol-at-the-next-level-down", "a" + new string(Mark, 3), QREccLevel.H, QRVersionRange.Exactly(1), QRSegmentation.Single, "Widen the version range or lower the ECC level to Q.");
        yield return ("one-symbol-two-levels-down", new string(Mark, 5), QREccLevel.Q, QRVersionRange.Exactly(1), QRSegmentation.Optimal, "Widen the version range or lower the ECC level to L.");
    }

    [Test]
    [MethodDataSource(nameof(Refusals))]
    public async Task Refusal_AdvisesExactlyTheChangesThatWouldWork(string name, string text, QREccLevel ecc, QRVersionRange range, QRSegmentation segmentation, string? advice)
    {
        var refusal = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, ecc, new QRCodeGeneratorOptions { Version = range, Segmentation = segmentation }));
        var sentence = "data units).";
        var said = refusal.Message.Substring(refusal.Message.IndexOf(sentence, StringComparison.Ordinal) + sentence.Length);
        said = said.Substring(0, said.IndexOf(" (Parameter", StringComparison.Ordinal)).Trim();
        // The reason a run of marks gives is not advice.
        const string Reason = "The text holds a run of U+FEFF that no symbol holds together with the character ahead of it, and no symbol after the first may begin with that character.";
        const string Cuts = "The text holds U+FEFF, which no symbol after the first may begin with; with those characters replaced it would fit.";
        said = said.Replace(Reason, "").Replace(Cuts, "").Trim();
        if (advice is not null)
            await Assert.That(said).IsEqualTo(advice).Because(name);

        // Whatever the wording, the advice is checked against what it promises: each change alone, then what is said of it.
        bool Fits(bool widen, QREccLevel level, bool plan)
        {
            try
            {
                QRCodeGenerator.CreateStructuredAppend(text, level, new QRCodeGeneratorOptions
                {
                    Version = widen ? QRVersionRange.AtLeast(range.Min) : range,
                    Segmentation = plan ? QRSegmentation.Optimal : segmentation,
                });
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        const string Widen = "iden the version range", Lower = "ower the ECC level to ", Plan = "se QRSegmentation.Optimal";
        var saysWiden = said.Contains(Widen, StringComparison.Ordinal);
        var saysLower = said.Contains(Lower, StringComparison.Ordinal);
        var saysPlan = said.Contains(Plan, StringComparison.Ordinal);
        var alone = new[] { (saysWiden, Fits(true, ecc, false)), (saysLower, Fits(false, QREccLevel.L, false)), (saysPlan, Fits(false, ecc, true)) };
        if (alone.Any(change => change.Item2))
        {
            await Assert.That(said).DoesNotContain(" and ").Because(name);
            foreach (var (isSaid, works) in alone)
                await Assert.That(isSaid).IsEqualTo(works).Because(name);
        }
        else if (said.Length > 0)
        {
            await Assert.That(said).Contains(" and ").Because(name);
        }
        else
        {
            await Assert.That(Fits(true, QREccLevel.L, true)).IsFalse().Because(name);
        }

        // The level named is the highest that does it, with whatever else is said of the same set of changes.
        if (saysLower)
        {
            var named = Enum.Parse<QREccLevel>(said.Substring(said.IndexOf(Lower, StringComparison.Ordinal) + Lower.Length, 1));
            var together = said.Contains(" and ", StringComparison.Ordinal);
            await Assert.That((int)named).IsLessThan((int)ecc).Because(name);
            await Assert.That(Fits(together && saysWiden, named, together && saysPlan)).IsTrue().Because(name);
            if (named + 1 < ecc)
                await Assert.That(Fits(together && saysWiden, named + 1, together && saysPlan)).IsFalse().Because(name);
        }
        else if (said.Contains(" and ", StringComparison.Ordinal))
        {
            await Assert.That(Fits(saysWiden, ecc, saysPlan)).IsTrue().Because(name);
        }
    }

    [Test]
    public async Task Refusal_AdvisesNoPlanWhereThePlansDoNotFitEither()
    {
        // Prose with one year in it has a run dense enough for a plan to pay, and sixteen symbols do not hold its plans either.
        var prose = Repeat("the quick brown fox jumps over the lazy dog. ", 47_300).Insert(20_000, " in 2026 ");
        var noPlan = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(prose, QREccLevel.L));
        await Assert.That(noPlan.Message).DoesNotContain("QRSegmentation.Optimal");
        Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(prose, QREccLevel.L, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal }));
    }

    [Test]
    public async Task OneRiskyMark_DoesNotPriceTheWholeSetAsSingle()
    {
        // A mark right after an alphanumeric moves one run of one chunk by a character; the split is priced by plans
        // all the same, and the other forty thousand characters keep theirs.
        var body = Repeat("1234567890123456789012345678;", 40_000);
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };

        var marked = QRCodeGenerator.CreateStructuredAppend(body + "A" + Mark, QREccLevel.L, optimal);
        var unmarked = QRCodeGenerator.CreateStructuredAppend(body + "AB", QREccLevel.L, optimal);

        await Assert.That(marked.Length).IsEqualTo(unmarked.Length);
        var parts = new StringBuilder();
        foreach (var symbol in marked)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(body + "A" + Mark);
    }

    [Test]
    public async Task Refusal_NamesARunOfMarks_ExactlyWhenNoSymbolHoldsItWithTheCharacterAheadOfIt()
    {
        const string Reason = "a run of U+FEFF that no symbol holds together with the character ahead of it";
        const string Cuts = "with those characters replaced it would fit";
        var tail = new string('a', 300);
        string Refused(string text, QRCodeGeneratorOptions options, QREccLevel ecc = QREccLevel.L)
            => Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, ecc, options)).Message;

        // A symbol of a version 1-L set has thirteen and a half bytes past its headers: a character and four marks, not five.
        var one = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        await Assert.That(Refused("a" + new string(Mark, 4) + tail, one)).DoesNotContain(Reason);
        await Assert.That(Refused("a" + new string(Mark, 5) + tail, one)).Contains(Reason);
        // A two-byte character ahead is the byte the Byte mode header does not leave room for.
        await Assert.That(Refused("é" + new string(Mark, 4) + tail, one)).Contains(Reason);
        // Runs a symbol holds one at a time, and a cut that may fall between the mark and the character before the run.
        await Assert.That(Refused("a" + new string(Mark, 3) + "b" + new string(Mark, 3) + tail, one)).DoesNotContain(Reason);
        // The character ahead may be a pair, which the cut clears whole, or the mark at the head of the text.
        await Assert.That(Refused("\U0001F600" + new string(Mark, 3) + tail, one)).DoesNotContain(Reason);
        await Assert.That(Refused("\U0001F600" + new string(Mark, 4) + tail, one)).Contains(Reason);
        // A pair that opens its chunk is cleared whole too: at 4-Q the room is a byte short of the pair and thirteen marks but
        // not of its low half and them, so a cut stepping back one unit would split the pair and the set would read U+FFFD twice.
        var fourQ = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(4) };
        var pairOpens = char.ConvertFromUtf32(0x1F600) + new string(Mark, 13) + new string('a', 40);
        await Assert.That(Refused(pairOpens, fourQ, QREccLevel.Q)).Contains(Reason);
        await Assert.That(Refused(pairOpens, fourQ with { Segmentation = QRSegmentation.Optimal }, QREccLevel.Q)).Contains(Reason);
        await Assert.That(Refused(new string(Mark, 4) + tail, one)).DoesNotContain(Reason);
        await Assert.That(Refused(new string(Mark, 5) + tail, one)).Contains(Reason);
        // Behind the text, where nothing follows it, and with the byte order mark the first symbol also carries.
        await Assert.That(Refused(tail + new string(Mark, 5), one)).Contains(Reason);
        var withBom = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), EciMode = EciMode.Utf8, Utf8Bom = true };
        await Assert.That(Refused("a" + new string(Mark, 3) + tail, withBom)).DoesNotContain(Reason);
        await Assert.That(Refused("a" + new string(Mark, 4) + tail, withBom)).Contains(Reason);
        await Assert.That(Refused("xa" + new string(Mark, 4) + tail, withBom)).DoesNotContain(Reason);

        // Ten characters that sixteen symbols of version 2-M are refused: it is the run, by a byte, and seven marks are one symbol.
        var two = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(2), EciMode = EciMode.Utf8 };
        await Assert.That(Refused("ab" + new string(Mark, 8), two, QREccLevel.M)).Contains(Reason);
        await Assert.That(QRCodeGenerator.CreateStructuredAppend("ab" + new string(Mark, 7), QREccLevel.M, two).Length).IsEqualTo(1);

        // No run is too long for a symbol, but cuts kept off the marks leave every symbol a unit of ten bytes, and seventeen
        // units are one symbol too many; the same units with an ordinary character in the marks' place are fourteen symbols.
        var units = Repeat("a" + new string(Mark, 3), 17 * 4);
        await Assert.That(QRCodeGenerator.CreateStructuredAppend(units.Substring(0, 16 * 4), QREccLevel.L, one).Length).IsEqualTo(16);
        var byCuts = Refused(units, one);
        await Assert.That(byCuts).DoesNotContain(Reason);
        await Assert.That(byCuts).Contains(Cuts);
        await Assert.That(Refused("a" + new string(Mark, 5) + tail, one)).DoesNotContain(Cuts);
        await Assert.That(Refused(tail + tail + Mark + tail, one)).DoesNotContain(Cuts);
        // The stand-in is as wide as the mark: with a narrower one these units would fit and the size read as the marks' doing.
        // Fifty of them, few enough to pass the cheapest bound, so the stand-in is tried at all.
        var pairs = Repeat("a" + Mark, 100);
        Refused(pairs.Replace(Mark, (char)0x3042), one);
        await Assert.That(Refused(pairs, one)).DoesNotContain(Cuts);
        // One reason, the run, when both would be true: the same text with its marks replaced fits as well.
        var runAtH = Refused("a" + new string(Mark, 3), new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) }, QREccLevel.H);
        await Assert.That(runAtH).Contains(Reason);
        await Assert.That(runAtH).DoesNotContain(Cuts);

        // Under a declared ISO-8859-1 the mark is a byte like any other and no rule keeps a cut off it.
        var latin1 = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), EciMode = EciMode.Iso8859_1 };
        await Assert.That(Refused("a" + new string(Mark, 400), latin1)).DoesNotContain(Reason);
        await Assert.That(Refused("a" + new string(Mark, 400), latin1)).DoesNotContain(Cuts);
    }

    [Test]
    public async Task RunOfMarksLongerThanASymbol_IsRefusedInALongTextToo_AndTheRefusalSaysWhy()
    {
        // Long enough for the walk at eight budgets, which has to refuse it as the scalar walk does. At version 12 the
        // shorter texts are well within sixteen symbols by size, so the size of the set does not explain the refusal.
        foreach (var lead in new[] { 2_000, 3_000, 4_000, 6_000 })
            foreach (var maxVersion in new[] { 7, 9, 12 })
                foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
                {
                    var text = Repeat(OrderLine, lead) + new string(Mark, 600) + Repeat(OrderLine, 1_000);
                    var refusal = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion), Segmentation = segmentation }));
                    await Assert.That(refusal.Message).Contains("a run of U+FEFF that no symbol holds together with the character ahead of it").Because($"{segmentation} {lead} v{maxVersion}");
                    // A version 40 symbol holds the run, and the refusal says that too.
                    await Assert.That(refusal.Message).Contains("Widen the version range").Because($"{segmentation} {lead} v{maxVersion}");
                }

        // A run a symbol does hold is not the reason a text is refused, and is not given as one.
        var tooLong = Repeat(OrderLine, 30_000) + new string(Mark, 40) + Repeat(OrderLine, 30_000);
        var bySize = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(tooLong, QREccLevel.L, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal }));
        await Assert.That(bySize.Message).DoesNotContain("U+FEFF");
    }

    [Test]
    public async Task RunOfMarksNoSymbolCanKeepOffItsHead_IsRefused()
    {
        // Each symbol here holds thirteen bytes and a mark is three: whichever way the text is cut, some symbol would begin with one and lose it.
        foreach (var text in new[] { "a" + new string(Mark, 6), new string('a', 10) + new string(Mark, 6) + "bcd" })
        {
            foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
                Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), Segmentation = segmentation }));
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task OneSymbolQuestion_UnderAByteOrderMark_IsTheSingleModeQuestion()
    {
        // With the mark asked for, Create writes the single-mode stream, which no version here holds, so this is a set of two.
        var text = Repeat(OrderLine, 108);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(5), Segmentation = QRSegmentation.Optimal, EciMode = EciMode.Utf8, Utf8Bom = true };

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out _, options)).IsFalse();
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);
        await Assert.That(set.Length).IsEqualTo(2);
        var parts = new StringBuilder();
        foreach (var symbol in set)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var part, out _)).IsTrue();
            parts.Append(part);
        }
        await Assert.That(parts.ToString()).IsEqualTo(text);
    }

    private static string Repeat(string unit, int length)
    {
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length)
            sb.Append(unit);
        return sb.ToString(0, length);
    }

    /// <summary>The corrected data codewords of a symbol, as an independent decoder recovers them.</summary>
    private static byte[] DataCodewords(QRCodeData data)
    {
        const int scale = 4;
        var size = data.Size;
        using var bitmap = new SKBitmap(size * scale, size * scale);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var row = 0; row < size; row++)
                for (var col = 0; col < size; col++)
                    if (data[row, col])
                        canvas.DrawRect(col * scale, row * scale, scale, scale, paint);
        }

        var reader = new BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, PureBarcode = true },
        };
        var result = reader.Decode(bitmap) ?? throw new InvalidOperationException("the independent decoder could not read the symbol");
        return result.RawBytes;
    }

    /// <summary>The head of a symbol's bit stream, most significant bit first.</summary>
    private sealed class Stream(byte[] codewords)
    {
        private int _bit;

        public int Read(int bits)
        {
            var value = 0;
            for (var i = 0; i < bits; i++, _bit++)
                value = (value << 1) | ((codewords[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            return value;
        }

        public (int Index, int Count, int Parity) ReadSetHeader()
        {
            if (Read(4) != 0b0011)
                throw new InvalidOperationException("the stream does not open with a Structured Append header");
            return (Read(4), Read(4) + 1, Read(8));
        }

        /// <summary>The charset the stream declares next, or -1 (and nothing consumed) when it declares none.</summary>
        public int ReadEci()
        {
            var at = _bit;
            if (Read(4) == 0b0111)
                return Read(8);
            _bit = at;
            return -1;
        }
    }
}
