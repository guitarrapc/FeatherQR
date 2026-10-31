using FeatherQR.Internals;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// Writing FNC1 into a Standard QR symbol (specs/standardqr-encoder.md, FNC1). In Alphanumeric segments a separator is
/// written <c>%</c> and a literal <c>%</c> is written <c>%%</c>, so the separator pairs no segment can hold stay out of
/// Alphanumeric mode.
/// </summary>
public class Fnc1EncodeTest
{
    private const string Gs = "\u001D";
    private const string Gtin = "0109524000059109";
    private static readonly QRCodeGeneratorOptions First = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0, Segmentation = QRSegmentation.Single };
    private static readonly QRCodeGeneratorOptions FirstOptimal = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0, Segmentation = QRSegmentation.Optimal };

    // The two examples of the standard, bit for bit

    [Test]
    public async Task IsoExample_FirstPosition_Optimal_WritesTheStandardsStream()
    {
        // Level M: the split's 178 bits fit version 2-M (224) where the single Alphanumeric stream's 226 do not, so Optimal
        // splits. At level L both fit version 2 and Optimal emits the single-mode stream, as its contract says.
        var text = "01049123451234591597033130128" + Gs + "10ABC123";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal);
        var (decoded, info, codewords) = Read(data);

        await Assert.That(info.Version).IsEqualTo(2).Because("178 data bits and the terminator, over version 1-M's 128 and within 2-M's 224");
        await Assert.That(Convert.ToHexString(codewords, 0, 23)).IsEqualTo("510740A7ACEA80159E4FCA52D2D38409D5E028FD82F0C0")
            .Because("0101 FNC1, 0001 Numeric, the count 29, nine 10-bit and one 7-bit group, 0010 Alphanumeric, the count 9, four 11-bit pairs and one 6-bit character, 0000");
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
    }

    [Test]
    public async Task IsoExample_SecondPosition_Optimal_WritesTheStandardsStream()
    {
        var text = "AA1234BBB112text text text text\r";
        var data = QRCodeGenerator.Create(text, QREccLevel.L, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = QRSegmentation.Optimal, QuietZoneSize = 0 });
        var (decoded, info, codewords) = Read(data);

        await Assert.That(info.Version).IsEqualTo(2);
        await Assert.That(Convert.ToHexString(codewords)).IsEqualTo("9252061CC05E22CFD1F005E828E8CAF0E840E8CAF0E840E8CAF0E840E8CAF0E81A00")
            .Because("1001 FNC1 second, 00100101 the indicator 37, 0010 Alphanumeric, the count 12, six pairs, 0100 Byte, the count 20, twenty bytes, 0000, and no padding at 2-L's exact capacity");
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition(37));
    }

    // Single: the mode the text takes under FNC1, and the stream it makes

    [Test]
    [Arguments(Gtin + "17231120", "Numeric", false)]
    [Arguments(Gtin + "10ABC123" + Gs + "17231120", "Alphanumeric", true)]
    [Arguments(Gtin + "10AB%CD" + Gs + "17231120", "Alphanumeric", true)]
    [Arguments("AB%%CD", "Alphanumeric", true)]
    [Arguments("10AB%" + Gs + "CD", "Alphanumeric", true)]
    [Arguments("087408145020" + Gs + "38" + Gs + "055%802919575", "Alphanumeric", true)] // two separators apart, then a literal %: neither pair
    [Arguments("%", "Alphanumeric", true)]
    [Arguments(Gs, "Alphanumeric", true)]
    [Arguments(Gtin + "101234567p" + Gs + "17231120", "Byte", false)]
    [Arguments("10AB" + Gs + Gs + "CD", "Byte", false)]
    [Arguments("10AB" + Gs + "%CD", "Byte", false)]
    [Arguments(Gs + Gs, "Byte", false)]
    [Arguments(Gs + "%", "Byte", false)]
    [Arguments("", "Byte", false)]
    public async Task Single_PicksTheModeTheRuleAllows_AndReadsBack(string text, string mode, bool percentInAlphanumeric)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M, First);
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(shape.Headers).IsEquivalentTo(["FNC1First"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo([mode], CollectionOrdering.Matching);
        await Assert.That(shape.AlphanumericHasPercent).IsEqualTo(percentInAlphanumeric);
    }

    /// <summary>The same texts without FNC1 keep their modes and read back literally, so the option changes nothing it is not set on.</summary>
    [Test]
    [Arguments("10AB%CD", "Alphanumeric")]
    [Arguments("10AB" + Gs + "CD", "Byte")]
    [Arguments(Gtin + "17231120", "Numeric")]
    public async Task WithoutFnc1_TheSameTextsAreUnchanged(string text, string mode)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);
        await Assert.That(shape.Headers).IsEmpty();
        await Assert.That(shape.DataModes).IsEquivalentTo([mode], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Without FNC1, Optimal writes Single's stream wherever no split reaches a smaller version. A literal <c>%</c> is one
    /// Alphanumeric character there, and writing it twice as the FNC1 rule does would overrun the count the stream declares.
    /// </summary>
    [Test]
    [Arguments("AB%CD")]
    [Arguments("100%")]
    [Arguments("%")]
    [Arguments("%%")]
    [Arguments("A%B%C%D%")]
    [Arguments("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:")]
    public async Task WithoutFnc1_Optimal_AlphanumericWithPercent_IsSinglesSymbol(string text)
    {
        var singleOptions = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        var optimalOptions = singleOptions with { Segmentation = QRSegmentation.Optimal };
        var single = QRCodeGenerator.Create(text, QREccLevel.M, singleOptions);
        var optimal = QRCodeGenerator.Create(text, QREccLevel.M, optimalOptions);

        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);
        // The premise: no split of these texts reaches a smaller version, so the stream is one Alphanumeric segment.
        await Assert.That(Shape(optimal, info).DataModes).IsEquivalentTo(["Alphanumeric"], CollectionOrdering.Matching);
        await Assert.That(Modules(optimal)).IsEquivalentTo(Modules(single), CollectionOrdering.Matching).Because("Optimal emits Single's stream when a split does not shrink the symbol");

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, optimalOptions)).IsTrue();
        var spanForm = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, QREccLevel.M, spanForm, optimalOptions);
        await Assert.That(spanForm).IsEquivalentTo(Modules(single), CollectionOrdering.Matching).Because("the span form writes the same symbol");
    }

    [Test]
    public async Task WithoutFnc1_Optimal_APercentAtEachPositionOfAlphanumericText_ReadsBack()
    {
        var failures = new List<string>();
        var options = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        for (var length = 1; length <= 40; length++)
        {
            for (var at = 0; at < length; at++)
            {
                var chars = new string('A', length).ToCharArray();
                chars[at] = '%';
                var text = new string(chars);
                var data = QRCodeGenerator.Create(text, QREccLevel.L, options);
                if (!QRCodeDecoder.TryDecode(data, out var decoded, out var info) || decoded != text)
                    failures.Add($"{text}: {info.Status} \"{decoded}\"");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Single_KanjiWithAllowKanji_WritesKanjiMode()
    {
        var text = "こんにちは世界";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition('A'), AllowKanji = true, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition('A'));
        await Assert.That(shape.Headers).IsEquivalentTo(["FNC1Second:165"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(["Kanji"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// A charset the caller names is written as named, under FNC1 as without. Japanese text that Kanji mode could carry stays UTF-8
    /// behind its ECI header when UTF-8 is named, and when a byte order mark is asked for, which only a Byte segment has a place for.
    /// </summary>
    [Test]
    [Arguments(EciMode.Utf8, false, QRSegmentation.Single)]
    [Arguments(EciMode.Utf8, false, QRSegmentation.Optimal)]
    [Arguments(EciMode.Default, true, QRSegmentation.Single)]
    public async Task AllowKanji_WithANamedCharsetOrAByteOrderMark_WritesUtf8(EciMode eci, bool bom, QRSegmentation segmentation)
    {
        var text = "日本語のテキスト";
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, AllowKanji = true, EciMode = eci, Utf8Bom = bom, Segmentation = segmentation, QuietZoneSize = 0 };
        var data = QRCodeGenerator.Create(text, QREccLevel.M, options);
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(shape.Headers).IsEquivalentTo(["ECI:26", "FNC1First"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(["Byte"], CollectionOrdering.Matching);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(data.Version);
    }

    /// <summary>
    /// The charset under FNC1 follows the content as it does without: U+007F is ASCII and takes no ECI header, U+00FF is the last
    /// character ISO-8859-1 holds and takes ECI 3, and U+0100 is past it and takes UTF-8.
    /// </summary>
    [Test]
    [Arguments("AB\u007F12", "")]
    [Arguments("ABÿ12", "ECI:3")]
    [Arguments("ABĀ12", "ECI:26")]
    public async Task Charset_AtTheEdgesOfAsciiAndIso8859_1_FollowsTheContent(string text, string eciHeader)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M, First);
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        string[] expected = eciHeader.Length == 0 ? ["FNC1First"] : [eciHeader, "FNC1First"];
        await Assert.That(Shape(data, info).Headers).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>The standard's header order: ECI, then FNC1, then the data.</summary>
    [Test]
    [Arguments(Gtin + "10é" + Gs + "17231120", EciMode.Default, "ECI:3")]
    [Arguments(Gtin + "10€" + Gs + "17231120", EciMode.Default, "ECI:26")]
    [Arguments(Gtin + "10ABC", EciMode.Utf8, "ECI:26")]
    [Arguments(Gtin + "10ABC", EciMode.Iso8859_1, "ECI:3")]
    public async Task Eci_IsWrittenBeforeFnc1(string text, EciMode eci, string eciHeader)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, EciMode = eci, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(shape.Headers).IsEquivalentTo([eciHeader, "FNC1First"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Utf8Bom_FollowsTheFnc1HeaderAsData()
    {
        var text = "10€";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, Utf8Bom = true, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(shape.Headers).IsEquivalentTo(["ECI:26", "FNC1First"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(["Byte"], CollectionOrdering.Matching);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
    }

    // Second position: every indicator form

    [Test]
    [Arguments(0)]
    [Arguments(99)]
    [Arguments(165)]
    [Arguments(222)]
    public async Task SecondPosition_EachIndicatorForm_ReadsBackAsItsCodeword(int codeword)
    {
        var mode = codeword < 100 ? Fnc1Mode.SecondPosition(codeword) : Fnc1Mode.SecondPosition((char)(codeword - 100));
        var text = "AA1234BBB112";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = mode, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1.ApplicationIndicator).IsEqualTo(codeword);
        await Assert.That(shape.Headers).IsEquivalentTo([$"FNC1Second:{codeword}"], CollectionOrdering.Matching);
    }

    // Optimal: the separator pairs, and the mix of modes

    /// <summary>A separator pair is resolved by a segment break where the runs are long and by a Byte segment where they are short, whichever costs less.</summary>
    [Test]
    public async Task Optimal_SeparatorPair_IsResolvedByABreakOrByByte()
    {
        var longText = "ABCDEFGHIJKLMNOP" + Gs + Gs + "QRSTUVWXYZ0123";
        var longData = QRCodeGenerator.Create(longText, QREccLevel.M, FirstOptimal);
        var (longDecoded, longInfo, _) = Read(longData);
        await Assert.That(longDecoded).IsEqualTo(longText);
        await Assert.That(Shape(longData, longInfo).DataModes).IsEquivalentTo(["Alphanumeric", "Alphanumeric"], CollectionOrdering.Matching).Because("two Alphanumeric runs cost 203 bits, a Byte run 268");

        var shortText = "A" + Gs + "%B";
        var shortData = QRCodeGenerator.Create(shortText, QREccLevel.M, FirstOptimal);
        var (shortDecoded, shortInfo, _) = Read(shortData);
        await Assert.That(shortDecoded).IsEqualTo(shortText);
        await Assert.That(Shape(shortData, shortInfo).DataModes).IsEquivalentTo(["Byte"], CollectionOrdering.Matching).Because("a Byte run of four is cheaper than two Alphanumeric runs");
    }

    [Test]
    [Arguments(Gtin + "10ABC123" + Gs + "17231120")]
    [Arguments(Gtin + "21abc" + Gs + "10AB%CD" + Gs + "17231120")]
    [Arguments("ABC" + Gs + Gs + "%%" + Gs + "%" + Gs + "123456789")]
    [Arguments("%" + Gs + "%" + Gs + "%" + Gs)]
    [Arguments("12345678901234567890" + Gs + "A")]
    public async Task Optimal_ReadsBack_AndIsNeverLargerThanSingle(string text)
    {
        var single = QRCodeGenerator.Create(text, QREccLevel.M, First);
        var optimal = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal);
        var (decoded, info, _) = Read(optimal);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(optimal.Version).IsLessThanOrEqualTo(single.Version);
    }

    [Test]
    public async Task Optimal_MixedDigitsAndText_SelectsASmallerVersionThanSingle()
    {
        var text = "01049123451234591597033130128" + Gs + "10ABC123" + Gs + "21" + new string('9', 20);
        var single = QRCodeGenerator.Create(text, QREccLevel.M, First);
        var optimal = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal);
        var (decoded, info, _) = Read(optimal);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(optimal.Version).IsLessThan(single.Version);
        await Assert.That(Shape(optimal, info).DataModes).IsEquivalentTo(["Numeric", "Alphanumeric", "Numeric"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// The Alphanumeric run opens at a literal <c>%</c> and holds a second one, the two places a literal is priced. A plan
    /// priced wrong is dropped for the single-mode stream, which reads back too, so the modes show the plan was kept.
    /// </summary>
    [Test]
    public async Task Optimal_AlphanumericRunWithLiteralPercents_KeepsThePlan()
    {
        var digits = string.Concat(Enumerable.Repeat("0123456789", 5));
        var text = digits + "%AB%CD" + digits;
        var single = QRCodeGenerator.Create(text, QREccLevel.M, First);
        var optimal = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal);
        var (decoded, info, _) = Read(optimal);
        var shape = Shape(optimal, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(optimal.Version).IsLessThan(single.Version);
        await Assert.That(shape.DataModes).IsEquivalentTo(["Numeric", "Alphanumeric", "Numeric"], CollectionOrdering.Matching);
        await Assert.That(shape.AlphanumericHasPercent).IsTrue();
    }

    /// <summary>
    /// Optimal picks the smallest version a plan fits, held against each smaller version in the range by a fixed-version sizing. Each text
    /// leads the planner where a cost could go wrong: the indicator's bits in the version scan, separators beside Byte text in the scan's
    /// lower bound, separators and literal percents priced as Alphanumeric units, and a literal percent among separators in a run's cost.
    /// </summary>
    [Test]
    [Arguments("Gc38061687817646030977FERLGYPAILQZJCLERCRKSFCLQVVLFICMQUEc7ZZDf", 90, QREccLevel.Q, 3, 7, EciMode.Default, 5)]
    [Arguments("A\u001DA\u001DA\u001DA\u001DA\u001DA\u001DA\u001DA\u001DA\u001Dabc", -1, QREccLevel.L, 1, 40, EciMode.Default, 1)]
    [Arguments("\u001D\u001D\u001D\u001DAA\u001DA\u001DAAA\u001DAAAA\u001DA\u001DAAAAAAAA\u001D", -1, QREccLevel.H, 1, 8, EciMode.Iso8859_1, 3)]
    [Arguments("%%00%%", 'c' + 100, QREccLevel.H, 1, 40, EciMode.Default, 1)]
    [Arguments("1 Z\u001D\u001DA1Z\u001DZ1%Z\u001D 10", 'g' + 100, QREccLevel.L, 1, 40, EciMode.Default, 1)]
    public async Task Optimal_PicksTheSmallestVersionAPlanFits(string text, int indicator, QREccLevel level, int min, int max, EciMode eci, int expected)
    {
        var mode = indicator < 0 ? Fnc1Mode.FirstPosition : indicator < 100 ? Fnc1Mode.SecondPosition(indicator) : Fnc1Mode.SecondPosition((char)(indicator - 100));
        var options = new QRCodeGeneratorOptions { Fnc1 = mode, EciMode = eci, Version = QRVersionRange.Between(min, max), Segmentation = QRSegmentation.Optimal, QuietZoneSize = 0 };
        var data = QRCodeGenerator.Create(text, level, options);
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(mode);
        await Assert.That(data.Version).IsEqualTo(expected);
        for (var version = min; version < expected; version++)
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, level, out _, options with { Version = QRVersionRange.Exactly(version) })).IsFalse().Because($"version {version} would hold a plan");
    }

    /// <summary>
    /// A U+FEFF inside the content stays in its Byte run under Optimal, as it does without FNC1, so the digits before it keep their
    /// Numeric run and the symbol is the plan's version, smaller than the one Byte run Single writes.
    /// </summary>
    [Test]
    public async Task Optimal_AByteOrderMarkInsideTheContent_StaysInItsByteRun()
    {
        var text = new string('1', 40) + "\uFEFF" + new string('a', 22);
        var data = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal with { EciMode = EciMode.Utf8 });
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(Shape(data, info).DataModes).IsEquivalentTo(["Numeric", "Byte"], CollectionOrdering.Matching);
        await Assert.That(data.Version).IsLessThan(QRCodeGenerator.Create(text, QREccLevel.M, First with { EciMode = EciMode.Utf8 }).Version);
    }

    /// <summary>The planned stream keeps the standard's header order too: ECI, then FNC1. Digits then other text make the plan two runs, so it is the planned route that writes the headers.</summary>
    [Test]
    [Arguments("0123456789012345éabc", -1, "ECI:3", "FNC1First")]
    [Arguments("01234567890123456789012345678901€abc", 37, "ECI:26", "FNC1Second:37")]
    public async Task Optimal_Eci_IsWrittenBeforeFnc1(string text, int indicator, string eciHeader, string fnc1Header)
    {
        var mode = indicator < 0 ? Fnc1Mode.FirstPosition : Fnc1Mode.SecondPosition(indicator);
        var data = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal with { Fnc1 = mode });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(shape.DataModes.Count).IsGreaterThan(1);
        await Assert.That(shape.Headers).IsEquivalentTo([eciHeader, fnc1Header], CollectionOrdering.Matching);
    }

    /// <summary>Under FNC1 a Kanji-eligible text with ASCII is planned over UTF-8 only. The symbol still reads back.</summary>
    [Test]
    public async Task Optimal_KanjiAndAscii_ReadsBack()
    {
        var text = "注文" + Gs + "1234567890";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), AllowKanji = true, Segmentation = QRSegmentation.Optimal, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition(37));
    }

    // Capacity: the header's bits move a text across a version edge

    [Test]
    public async Task Capacity_TheIndicatorBitsMoveATextAtTheEdge()
    {
        var digits = new string('7', 41); // 4 + 10 + 137 = 151 of version 1-L's 152 bits
        await Assert.That(QRCodeGenerator.Create(digits, QREccLevel.L, new QRCodeGeneratorOptions { QuietZoneSize = 0 }).Version).IsEqualTo(1);
        await Assert.That(QRCodeGenerator.Create(digits, QREccLevel.L, First).Version).IsEqualTo(2).Because("4 more bits");
        await Assert.That(QRCodeGenerator.Create(digits, QREccLevel.L, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(0), QuietZoneSize = 0 }).Version).IsEqualTo(2).Because("12 more bits");

        var alnum = new string('A', 24); // 4 + 4 + 9 + 132 = 149 bits under first position
        await Assert.That(QRCodeGenerator.Create(alnum, QREccLevel.L, First).Version).IsEqualTo(1);
        var withPercent = alnum.Substring(1) + "%"; // the same 24 characters, 25 units
        await Assert.That(QRCodeGenerator.Create(withPercent, QREccLevel.L, First).Version).IsEqualTo(2).Because("a literal % costs two characters");
        await Assert.That(QRCodeGenerator.Create(withPercent, QREccLevel.L, new QRCodeGeneratorOptions { QuietZoneSize = 0 }).Version).IsEqualTo(1).Because("without FNC1 it costs one");
        var (decoded, _, _) = Read(QRCodeGenerator.Create(withPercent, QREccLevel.L, First));
        await Assert.That(decoded).IsEqualTo(withPercent);
    }

    // Options under FNC1

    [Test]
    public async Task Options_VersionRangeBoostAndMask_Apply()
    {
        var text = Gtin + "10ABC123" + Gs + "17231120";
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0, Version = QRVersionRange.Exactly(6), BoostEccLevel = true, MaskPattern = 3 };
        var data = QRCodeGenerator.Create(text, QREccLevel.L, options);
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Version).IsEqualTo(6);
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.H).Because("the stream is far below version 6's capacity at every level");
        await Assert.That(info.MaskPattern).IsEqualTo(3);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);

        var optimal = QRCodeGenerator.Create(text, QREccLevel.L, options with { Segmentation = QRSegmentation.Optimal });
        var (optimalDecoded, optimalInfo, _) = Read(optimal);
        await Assert.That(optimalDecoded).IsEqualTo(text);
        await Assert.That((optimalInfo.Version, optimalInfo.EccLevel, optimalInfo.MaskPattern)).IsEqualTo((6, QREccLevel.H, 3));
    }

    /// <summary>
    /// The boost raises the level only while the stream with the indicator's bits fits. 26 digits take 4 + 10 + 87 bits, which version
    /// 1-Q's 104 hold, and the 4 bits of first position make 105, so the level stays at M. A boost that left those bits out would go to Q
    /// and overrun the stream.
    /// </summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task Boost_StopsWhereTheIndicatorBitsOverflowTheNextLevel(QRSegmentation segmentation)
    {
        var digits = new string('7', 26);
        var options = new QRCodeGeneratorOptions { BoostEccLevel = true, Segmentation = segmentation, QuietZoneSize = 0 };
        var withoutFnc1 = Read(QRCodeGenerator.Create(digits, QREccLevel.M, options)).Info;
        await Assert.That((withoutFnc1.Version, withoutFnc1.EccLevel)).IsEqualTo((1, QREccLevel.Q));

        var (decoded, info, _) = Read(QRCodeGenerator.Create(digits, QREccLevel.M, options with { Fnc1 = Fnc1Mode.FirstPosition }));
        await Assert.That(decoded).IsEqualTo(digits);
        await Assert.That((info.Version, info.EccLevel)).IsEqualTo((1, QREccLevel.M));
    }

    /// <summary>The boost of a written plan counts the indicator's bits too. This mixed text's plan fits version 1-L with second position's 12 bits, and not 1-M.</summary>
    [Test]
    public async Task Boost_OfAPlan_StopsWhereTheIndicatorBitsOverflowTheNextLevel()
    {
        var text = "1 Z\u001D\u001DA1Z\u001DZ1%Z\u001D 10";
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition('g'), BoostEccLevel = true, Segmentation = QRSegmentation.Optimal, QuietZoneSize = 0 };
        var data = QRCodeGenerator.Create(text, QREccLevel.L, options);
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That((info.Version, info.EccLevel)).IsEqualTo((1, QREccLevel.L));
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out _, options with { BoostEccLevel = false, Version = QRVersionRange.Exactly(1) })).IsFalse()
            .Because("the premise: no plan of the text fits version 1-M under second position");
    }

    [Test]
    public async Task StructuredAppend_RefusesFnc1()
    {
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(Gtin + "10ABC", QREccLevel.M, options)).Throws<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(Gtin + "10ABC", QREccLevel.M, options with { Fnc1 = Fnc1Mode.SecondPosition(1) })).Throws<ArgumentException>();
    }

    [Test]
    public async Task TooLong_ThrowsAsWithoutFnc1()
    {
        // 7089 digits fill version 40-L exactly (23,648 bits), so only the indicator's 4 bits overflow it.
        var digits = new string('7', 7089);
        await Assert.That(QRCodeGenerator.Create(digits, QREccLevel.L, new QRCodeGeneratorOptions { QuietZoneSize = 0 }).Version).IsEqualTo(40);
        await Assert.That(() => QRCodeGenerator.Create(digits, QREccLevel.L, First)).Throws<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(digits, QREccLevel.L, FirstOptimal)).Throws<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(new string('7', 41), QREccLevel.L, First with { Version = QRVersionRange.Exactly(1) })).Throws<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.Create(new string('7', 41), QREccLevel.L, FirstOptimal with { Version = QRVersionRange.Exactly(1) })).Throws<ArgumentException>();

        // 4296 letters are 23,645 bits in one Alphanumeric run, 23,649 with the indicator, and no split is smaller than the one run.
        var letters = new string('A', 4296);
        await Assert.That(QRCodeGenerator.Create(letters, QREccLevel.L, new QRCodeGeneratorOptions { QuietZoneSize = 0 }).Version).IsEqualTo(40);
        await Assert.That(() => QRCodeGenerator.Create(letters, QREccLevel.L, FirstOptimal)).Throws<InvalidOperationException>()
            .Because("an unconstrained overflow is the same exception under Optimal as under Single");
    }

    /// <summary>
    /// Twenty letters take 4 + 9 + 110 bits, which version 1-M's 128 hold, and second position's 12 more overflow it. The message
    /// names those bits, so the caller can tell the mode's cost from the content's. Without FNC1 the message keeps its words.
    /// </summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task TooLong_ANarrowedRangeTheIndicatorOverflows_NamesTheIndicatorBits(QRSegmentation segmentation)
    {
        var text = "ABCDEFGHIJKLMNOPQRST";
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), Segmentation = segmentation };
        await Assert.That(QRCodeGenerator.Create(text, QREccLevel.M, options).Version).IsEqualTo(1);

        var error = await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.M, options with { Fnc1 = Fnc1Mode.SecondPosition(37) })).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("FNC1: 12 bits");
        var withoutFnc1 = await Assert.That(() => QRCodeGenerator.Create(text + "UVWXYZ", QREccLevel.M, options)).Throws<ArgumentException>();
        await Assert.That(withoutFnc1!.Message).DoesNotContain("FNC1");
    }

    /// <summary>The span form writes every byte of its symbol, the quiet zone included, so a buffer that held other data ends as a fresh one does.</summary>
    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task SpanForm_IntoABufferHoldingOtherData_WritesWhatAFreshOneHolds(QRSegmentation segmentation)
    {
        var text = Gtin + "10ABC123";
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, Segmentation = segmentation, QuietZoneSize = 2 };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, options)).IsTrue();
        var fresh = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, QREccLevel.M, fresh, options);
        var used = Enumerable.Repeat((byte)0xAB, size.BufferSize).ToArray();
        QRCodeGenerator.Create(text, QREccLevel.M, used, options);

        await Assert.That(used).IsEquivalentTo(fresh, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1 << 22)]
    [Arguments((1 << 22) - 1)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task Options_UndefinedSegmentation_IsRefused(int value)
    {
        // The field that holds the segmentation beside the FNC1 mode is narrower than an int.
        var options = new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)value };
        var error = await Assert.That(() => QRCodeGenerator.Create("123", QREccLevel.M, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.Create("123", QREccLevel.M, options with { Fnc1 = Fnc1Mode.FirstPosition })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize("123", QREccLevel.M, out _, options)).Throws<ArgumentOutOfRangeException>();

        // The field keeps no value of this range apart, so the message names the range and no number the caller did not give.
        await Assert.That(error!.Message).Contains("Invalid segmentation: a value below 0 or above 4194302");
        await Assert.That(error.Message).DoesNotContain("4194303");
    }

    [Test]
    [Arguments(7)]
    [Arguments((1 << 22) - 2)]
    public async Task Options_UndefinedSegmentationTheFieldHolds_IsReportedAsGiven(int value)
    {
        var options = new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)value };
        var error = await Assert.That(() => QRCodeGenerator.Create("123", QREccLevel.M, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(error!.Message).Contains($"Invalid segmentation: {value}");
    }

    // Sizing and the span form

    public static IEnumerable<Func<(string Text, QRCodeGeneratorOptions Options)>> SizingCases()
    {
        var texts = new[] { Gtin + "17231120", Gtin + "10ABC123" + Gs + "17231120", Gtin + "101234567p" + Gs + "17231120", "10AB" + Gs + Gs + "CD", Gtin + "10é", "AA1234BBB112text text text text\r", "" };
        var modes = new[] { Fnc1Mode.FirstPosition, Fnc1Mode.SecondPosition(37), Fnc1Mode.SecondPosition('z') };
        foreach (var text in texts)
        {
            foreach (var mode in modes)
            {
                foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
                {
                    foreach (var boost in new[] { false, true })
                    {
                        var captured = (text, new QRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, BoostEccLevel = boost });
                        yield return () => captured;
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(SizingCases))]
    public async Task Sizing_AgreesWithCreate_AndTheSpanFormWritesTheSameSymbol((string Text, QRCodeGeneratorOptions Options) c)
    {
        var data = QRCodeGenerator.Create(c.Text, QREccLevel.Q, c.Options);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(c.Text, QREccLevel.Q, out var size, c.Options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(data.Version);
        await Assert.That(size.Size).IsEqualTo(data.Size);

        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.Create(c.Text, QREccLevel.Q, buffer, c.Options);
        await Assert.That(written).IsEqualTo(size.BufferSize);
        var fromData = new byte[size.BufferSize];
        for (var row = 0; row < size.Size; row++)
            for (var col = 0; col < size.Size; col++)
                fromData[row * size.Size + col] = data[row, col] ? (byte)1 : (byte)0;
        await Assert.That(buffer).IsEquivalentTo(fromData, CollectionOrdering.Matching).Because("the span form writes the modules the data form holds");

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(c.Text);
        await Assert.That(info.Fnc1).IsEqualTo(c.Options.Fnc1);
    }

    [Test]
    public async Task Sizing_TooLong_ReturnsFalse()
    {
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 7089), QREccLevel.L, out _, First)).IsFalse().Because("7089 digits fill version 40-L exactly without the indicator");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 7089), QREccLevel.L, out _, FirstOptimal)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 7089), QREccLevel.L, out _, new QRCodeGeneratorOptions())).IsTrue();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 41), QREccLevel.L, out _, First with { Version = QRVersionRange.Exactly(1) })).IsFalse();
    }

    // The committed FNC1 fixtures: the data each holds encodes here and reads back with its mode

    public static IEnumerable<Func<string>> StandardQrFixtureIds() => FixtureLoader.EnumerateFixtureIds("StandardQrFnc1").Select(id => (Func<string>)(() => id));

    [Test]
    [MethodDataSource(nameof(StandardQrFixtureIds))]
    public async Task Fixtures_TheSameDataEncodesHere_AndReadsBackWithTheMode(string fixtureId)
    {
        var manifest = FixtureLoader.Load("StandardQrFnc1", fixtureId).Manifest;
        var fnc1 = manifest.Fnc1!;
        var mode = fnc1.Position == "First"
            ? Fnc1Mode.FirstPosition
            : fnc1.ApplicationIndicator < 100 ? Fnc1Mode.SecondPosition(fnc1.ApplicationIndicator!.Value) : Fnc1Mode.SecondPosition((char)(fnc1.ApplicationIndicator!.Value - 100));
        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            var options = new QRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, EciMode = manifest.EciCharset is null ? EciMode.Default : EciMode.Utf8 };
            var data = QRCodeGenerator.Create(manifest.PayloadText, Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel), options);
            await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because($"{fixtureId} ({segmentation}): {info.Status}");
            await Assert.That(decoded).IsEqualTo(manifest.PayloadText).Because($"{fixtureId} ({segmentation})");
            await Assert.That(info.Fnc1).IsEqualTo(mode).Because(fixtureId);
        }
    }

#if !DEBUG
    /// <summary>
    /// The span form allocates nothing under FNC1, Single and Optimal alike (Release only, as the other allocation tests are). The long text
    /// is past the planner's stack buffers, so its plan and its parent table come from the pool.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task SpanForm_AllocatesNothing()
    {
        var text = Gtin + "10ABC123" + Gs + "17231120" + Gs + "21abc%def";
        var longText = string.Concat(Enumerable.Repeat(Gtin + "10ab%c" + Gs + "17231120" + Gs, 9));
        var buffer = new byte[40_000];
        var single = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        var optimal = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = QRSegmentation.Optimal };
        // Here a plan is built only below the single-mode fit, and the FNC1 table leaves the stack only for a text longer than its stack buffer holds.
        var longSingle = QRCodeGenerator.Create(longText, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = optimal.Fnc1, Segmentation = QRSegmentation.Single });
        await Assert.That(QRCodeGenerator.Create(longText, QREccLevel.M, optimal).Version).IsLessThan(longSingle.Version);
        await Assert.That(longText.Length * ModeSegmenter.Fnc1ParentBytesPerChar).IsGreaterThan(ModeSegmenter.Fnc1MaxStackParents);
        for (var i = 0; i < 3; i++)
        {
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, single);
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), QREccLevel.M, out _, optimal);
            QRCodeGenerator.Create(longText.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(longText.AsSpan(), QREccLevel.M, out _, optimal);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, single);
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), QREccLevel.M, out _, optimal);
            QRCodeGenerator.Create(longText.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(longText.AsSpan(), QREccLevel.M, out _, optimal);
        }
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsEqualTo(0);
    }
#endif

    // Helpers

    private static (string Text, QRCodeDecodeInfo Info, byte[] Codewords) Read(QRCodeData data)
    {
        if (!QRCodeDecoder.TryDecode(data, out var text, out var info))
            throw new InvalidOperationException($"The symbol does not decode: {info.Status}");
        return (text, info, Fnc1StreamShape.DataCodewordsOfStandardQr(Modules(data), data.Size, info.Version, info.EccLevel, info.MaskPattern));
    }

    private static Fnc1StreamShape Shape(QRCodeData data, QRCodeDecodeInfo info)
        => Fnc1StreamShape.OfStandardQr(Modules(data), data.Size, info.Version, info.EccLevel, info.MaskPattern);

    /// <summary>The modules of a symbol built with no quiet zone, 1 for dark, as the fixture matrices are read.</summary>
    private static byte[] Modules(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }
}
