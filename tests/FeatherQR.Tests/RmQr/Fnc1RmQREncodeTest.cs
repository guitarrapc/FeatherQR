using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// Writing FNC1 into an rMQR symbol (specs/rmqr-encoder.md, FNC1): the indicator in the standard's order (ECI, FNC1
/// <c>101</c> or <c>110</c> with the application indicator, data), the <c>%</c> rule applied in reverse in Alphanumeric
/// segments, the separator pairs no segment can hold kept out of Alphanumeric mode, the version fit with the
/// indicator's 3 or 11 bits beside the 11-bit ECI header under every strategy and height, every class read back by
/// this library's decoder with the mode, and sizing that agrees with <c>Create</c>.
/// </summary>
public class Fnc1RmQREncodeTest
{
    private const string Gs = "\u001D";
    private const string Gtin = "0109524000059109";
    private static readonly RmQRCodeGeneratorOptions First = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0 };
    private static readonly RmQRCodeGeneratorOptions FirstOptimal = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0, Segmentation = RmQRSegmentation.Optimal };

    // The two smallest streams, bit for bit: the decode tests read these same bits

    [Test]
    public async Task FirstPosition_OneDigit_WritesTheStreamTheDecoderReads()
    {
        var data = RmQRCodeGenerator.Create("7", RmQREccLevel.M, First with { Version = RmQRVersion.R7x43 });
        var (decoded, info, codewords) = Read(data);

        await Assert.That(Convert.ToHexString(codewords)).IsEqualTo("A45C00EC11EC")
            .Because("101 FNC1 first, 001 Numeric, the count 1 in 4 bits, 0111 the digit, 000 terminator, the pad bits, then 0xEC 0x11 0xEC");
        await Assert.That(decoded).IsEqualTo("7");
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
    }

    [Test]
    public async Task SecondPosition_OneLetter_WritesTheStreamTheDecoderReads()
    {
        var data = RmQRCodeGenerator.Create("B", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        var (decoded, info, codewords) = Read(data);

        await Assert.That(Convert.ToHexString(codewords)).IsEqualTo("C4A89600EC11")
            .Because("110 FNC1 second, 00100101 the indicator 37, 010 Alphanumeric, the count 1 in 3 bits, 001011 B, 000 terminator, the pad bits, then 0xEC 0x11");
        await Assert.That(decoded).IsEqualTo("B");
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition(37));
    }

    [Test]
    public async Task FirstPosition_TwoLiteralPercents_WritesEachTwice()
    {
        var data = RmQRCodeGenerator.Create("A%%B", RmQREccLevel.M, First with { Version = RmQRVersion.R7x43 });
        var (decoded, info, codewords) = Read(data);

        await Assert.That(Convert.ToHexString(codewords)).IsEqualTo("AB1E8DA9AE40")
            .Because("101 FNC1 first, 010 Alphanumeric, the count 6 in 3 bits, the pairs A% (488), %% (1748) and %B (1721) in 11 bits each, 000 terminator, the pad bits");
        await Assert.That(decoded).IsEqualTo("A%%B");
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
    }

    // Single: the mode the text takes under FNC1, and the stream it makes

    [Test]
    [Arguments(Gtin + "17231120", "Numeric", false)]
    [Arguments(Gtin + "10ABC123" + Gs + "17231120", "Alphanumeric", true)]
    [Arguments(Gtin + "10AB%CD" + Gs + "17231120", "Alphanumeric", true)]
    [Arguments("AB%%CD", "Alphanumeric", true)]
    [Arguments("10AB%" + Gs + "CD", "Alphanumeric", true)]
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
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, First);
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(shape.Headers).IsEquivalentTo(["FNC1First"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo([mode], CollectionOrdering.Matching);
        await Assert.That(shape.AlphanumericHasPercent).IsEqualTo(percentInAlphanumeric);
    }

    /// <summary>The same texts without FNC1 keep today's modes and read back literally, so the option changes nothing it is not set on.</summary>
    [Test]
    [Arguments("10AB%CD", "Alphanumeric")]
    [Arguments("10AB" + Gs + "CD", "Byte")]
    [Arguments(Gtin + "17231120", "Numeric")]
    public async Task WithoutFnc1_TheSameTextsAreUnchanged(string text, string mode)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);
        await Assert.That(shape.Headers).IsEmpty();
        await Assert.That(shape.DataModes).IsEquivalentTo([mode], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Without FNC1, a literal <c>%</c> in Alphanumeric content is one character under Optimal as under Single. Writing it
    /// twice as the FNC1 rule does would overrun the count the stream declares.
    /// </summary>
    [Test]
    [Arguments("AB%CD")]
    [Arguments("100%")]
    [Arguments("%")]
    [Arguments("%%")]
    [Arguments("A%B%C%D%")]
    [Arguments("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:")]
    public async Task WithoutFnc1_Optimal_AlphanumericWithPercent_ReadsBack(string text)
    {
        var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = RmQRSegmentation.Optimal };
        var optimal = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);

        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);

        // Where Optimal keeps Single's version it writes Single's stream, so the two symbols are the same modules.
        var single = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options with { Segmentation = RmQRSegmentation.Single });
        if (optimal.Version == single.Version)
            await Assert.That(Modules(optimal)).IsEquivalentTo(Modules(single), CollectionOrdering.Matching);

        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, options)).IsTrue();
        var spanForm = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(text, RmQREccLevel.M, spanForm, options);
        await Assert.That(spanForm).IsEquivalentTo(Modules(optimal), CollectionOrdering.Matching).Because("the span form writes the same symbol");
    }

    [Test]
    public async Task WithoutFnc1_Optimal_APercentAtEachPositionOfAlphanumericText_ReadsBack()
    {
        var failures = new List<string>();
        var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal };
        for (var length = 1; length <= 40; length++)
        {
            for (var at = 0; at < length; at++)
            {
                var chars = new string('A', length).ToCharArray();
                chars[at] = '%';
                var text = new string(chars);
                var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
                if (!RmQRCodeDecoder.TryDecode(data, out var decoded, out var info) || decoded != text)
                    failures.Add($"{text}: {info.Status} \"{decoded}\"");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Single_KanjiWithAllowKanji_WritesKanjiMode()
    {
        var text = "こんにちは世界";
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition('A'), AllowKanji = true, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition('A'));
        await Assert.That(shape.Headers).IsEquivalentTo(["FNC1Second:165"], CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(["Kanji"], CollectionOrdering.Matching);
    }

    /// <summary>The standard's header order: the 11-bit ECI header, then FNC1, then the data. Non-ASCII text takes UTF-8 behind the ECI header, as it does without FNC1.</summary>
    [Test]
    [Arguments(Gtin + "10é" + Gs + "17231120", EciMode.Default, "ECI:3")]
    [Arguments(Gtin + "10€" + Gs + "17231120", EciMode.Default, "ECI:26")]
    [Arguments(Gtin + "10ABC", EciMode.Utf8, "ECI:26")]
    [Arguments(Gtin + "10ABC", EciMode.Iso8859_1, "ECI:3")]
    [Arguments("10é", EciMode.Default, "ECI:3")]
    public async Task Eci_IsWrittenBeforeFnc1(string text, EciMode eci, string eciHeader)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, EciMode = eci, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(shape.Headers).IsEquivalentTo([eciHeader, "FNC1First"], CollectionOrdering.Matching);
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
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = mode, QuietZoneSize = 0 });
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
        var longData = RmQRCodeGenerator.Create(longText, RmQREccLevel.M, FirstOptimal);
        var (longDecoded, longInfo, _) = Read(longData);
        await Assert.That(longDecoded).IsEqualTo(longText);
        await Assert.That(Shape(longData, longInfo).DataModes).IsEquivalentTo(["Alphanumeric", "Alphanumeric"], CollectionOrdering.Matching).Because("a second Alphanumeric header costs a few bits, a Byte run of 32 costs 80 more");
        await Assert.That(Area(longData)).IsLessThan(Area(RmQRCodeGenerator.Create(longText, RmQREccLevel.M, First))).Because("Single writes the pair in Byte mode");

        var shortText = "A" + Gs + "%B";
        var shortData = RmQRCodeGenerator.Create(shortText, RmQREccLevel.M, FirstOptimal);
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
    [Arguments("01049123451234591597033130128" + Gs + "10ABC123")]
    public async Task Optimal_ReadsBack_AndIsNeverLargerThanSingle(string text)
    {
        var single = RmQRCodeGenerator.Create(text, RmQREccLevel.M, First);
        var optimal = RmQRCodeGenerator.Create(text, RmQREccLevel.M, FirstOptimal);
        var (decoded, info, _) = Read(optimal);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Area(optimal)).IsLessThanOrEqualTo(Area(single));
    }

    [Test]
    public async Task Optimal_MixedDigitsAndText_SelectsASmallerSymbolThanSingle()
    {
        var text = "01049123451234591597033130128" + Gs + "10ABC123" + Gs + "21" + new string('9', 20);
        var single = RmQRCodeGenerator.Create(text, RmQREccLevel.M, First);
        var optimal = RmQRCodeGenerator.Create(text, RmQREccLevel.M, FirstOptimal);
        var (decoded, info, _) = Read(optimal);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(Area(optimal)).IsLessThan(Area(single));
        await Assert.That(Shape(optimal, info).Headers).IsEquivalentTo(["FNC1First"], CollectionOrdering.Matching);
        await Assert.That(Shape(optimal, info).DataModes).IsEquivalentTo(["Numeric", "Alphanumeric", "Numeric"], CollectionOrdering.Matching);
    }

    /// <summary>Under FNC1 a Kanji-eligible text with ASCII is planned over UTF-8 only; the symbol still reads back.</summary>
    [Test]
    public async Task Optimal_KanjiAndAscii_ReadsBack()
    {
        var text = "注文" + Gs + "1234567890";
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), AllowKanji = true, Segmentation = RmQRSegmentation.Optimal, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition(37));
        await Assert.That(Shape(data, info).Headers).IsEquivalentTo(["ECI:26", "FNC1Second:37"], CollectionOrdering.Matching);
    }

    /// <summary>A requested version the single-mode stream overflows is held by a plan, as without FNC1.</summary>
    [Test]
    public async Task Optimal_RequestedVersion_IsHeldByAPlanWhereSingleOverflows()
    {
        var text = "01049123451234591597033130128" + Gs + "10ABC1234" + Gs + "2112345678";
        var options = FirstOptimal with { Version = RmQRVersion.R7x99 };
        await Assert.That(() => RmQRCodeGenerator.Create(text, RmQREccLevel.M, First with { Version = RmQRVersion.R7x99 })).Throws<ArgumentException>()
            .Because("50 Alphanumeric units cost 3 + 3 + 6 + 275 bits, over R7x99-M's 224");
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
        var (decoded, info, _) = Read(data);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(data.Version).IsEqualTo(RmQRVersion.R7x99).Because("the cheapest plan's 97 + 61 + 34 payload bits, headers of 10, 9 and 10, and the indicator's 3 fill the 224 exactly, and the splits one and two characters later cost 225 and 228");
        await Assert.That(Shape(data, info).DataModes).IsEquivalentTo(["Numeric", "Alphanumeric", "Numeric"], CollectionOrdering.Matching);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(RmQRVersion.R7x99);
    }

    // Capacity: the header's bits move a text across a version edge

    [Test]
    public async Task Capacity_TheIndicatorBitsMoveATextAtTheEdge()
    {
        // R7x43-M holds 6 data codewords, 48 bits. Numeric: 3 mode + 4 count bits.
        var fixedVersion = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 };
        await Assert.That(RmQRCodeGenerator.Create(new string('7', 12), RmQREccLevel.M, fixedVersion).Version).IsEqualTo(RmQRVersion.R7x43).Because("7 + 40 = 47 bits");
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 12), RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.FirstPosition })).Throws<ArgumentException>().Because("3 more bits, 50");
        await Assert.That(RmQRCodeGenerator.Create(new string('7', 11), RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.FirstPosition }).Version).IsEqualTo(RmQRVersion.R7x43).Because("3 + 7 + 37 = 47 bits");
        await Assert.That(RmQRCodeGenerator.Create(new string('7', 9), RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.SecondPosition(0) }).Version).IsEqualTo(RmQRVersion.R7x43).Because("11 + 7 + 30 = 48 bits, the capacity exactly");
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 10), RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.SecondPosition(0) })).Throws<ArgumentException>().Because("11 + 7 + 34 = 52 bits");

        // Alphanumeric: 3 mode + 3 count bits, 39 left under first position: seven characters exactly, and a literal % costs two.
        await Assert.That(RmQRCodeGenerator.Create("ABCDEFG", RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.FirstPosition }).Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(() => RmQRCodeGenerator.Create("ABCDEF%", RmQREccLevel.M, fixedVersion with { Fnc1 = Fnc1Mode.FirstPosition })).Throws<ArgumentException>().Because("8 units, 44 bits");
        await Assert.That(RmQRCodeGenerator.Create("ABCDEF%", RmQREccLevel.M, fixedVersion).Version).IsEqualTo(RmQRVersion.R7x43).Because("without FNC1 the % costs one, 6 + 39 = 45 bits");
        var (decoded, _, _) = Read(RmQRCodeGenerator.Create("ABCDEF%", RmQREccLevel.M, First));
        await Assert.That(decoded).IsEqualTo("ABCDEF%");

        // The automatic fit moves with the same bits: 14 digits fill R11x27-M (56 bits) without FNC1 and overflow it with.
        var digits = new string('7', 14);
        await Assert.That(RmQRCodeGenerator.Create(digits, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 }).Version).IsEqualTo(RmQRVersion.R11x27).Because("7 + 47 = 54 bits");
        var moved = RmQRCodeGenerator.Create(digits, RmQREccLevel.M, First);
        await Assert.That(moved.Version).IsNotEqualTo(RmQRVersion.R11x27).Because("57 bits");
        await Assert.That(Area(moved)).IsGreaterThan(11 * 27);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(digits, RmQREccLevel.M, out var size, First)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(moved.Version);
    }

    // Options under FNC1

    [Test]
    [Arguments(RmQRFitStrategy.MinimizeArea)]
    [Arguments(RmQRFitStrategy.MinimizeWidth)]
    [Arguments(RmQRFitStrategy.MinimizeHeight)]
    public async Task Options_FitStrategy_ChoosesAsWithoutFnc1(RmQRFitStrategy strategy)
    {
        var text = Gtin + "10ABC123" + Gs + "17231120";
        foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
        {
            var options = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, FitStrategy = strategy, Segmentation = segmentation, QuietZoneSize = 0 };
            var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
            var (decoded, info, _) = Read(data);
            await Assert.That(decoded).IsEqualTo(text).Because(segmentation.ToString());
            await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);

            // No other version that holds the stream ranks better under the strategy: every version the stream fits is checked by a fixed-version encode.
            foreach (var candidate in Enum.GetValues<RmQRVersion>())
            {
                if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out _, options with { Version = candidate }))
                    continue;
                await Assert.That(IsBetter(candidate, data.Version, strategy)).IsFalse().Because($"{candidate} holds the stream and ranks better than {data.Version} under {strategy} ({segmentation})");
            }
        }
    }

    [Test]
    public async Task Options_HeightAndVersion_Apply()
    {
        var text = Gtin + "10ABC123" + Gs + "17231120";
        var byHeight = RmQRCodeGenerator.Create(text, RmQREccLevel.M, First with { Height = RmQRHeight.H7 });
        await Assert.That(byHeight.Height).IsEqualTo(7);
        var (decoded, info, _) = Read(byHeight);
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);

        var byVersion = RmQRCodeGenerator.Create(text, RmQREccLevel.H, First with { Version = RmQRVersion.R13x99, Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(byVersion.Version).IsEqualTo(RmQRVersion.R13x99);
        var (decodedByVersion, infoByVersion, _) = Read(byVersion);
        await Assert.That(decodedByVersion).IsEqualTo(text);
        await Assert.That(infoByVersion.EccLevel).IsEqualTo(RmQREccLevel.H);

        await Assert.That(() => RmQRCodeGenerator.Create(text, RmQREccLevel.M, First with { Version = RmQRVersion.R13x99, Height = RmQRHeight.H7 })).Throws<ArgumentException>()
            .Because("a version and a height that disagree are refused under FNC1 as without");
        await Assert.That(() => RmQRCodeGenerator.Create(text, RmQREccLevel.M, First with { Version = (RmQRVersion)99 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create(text, RmQREccLevel.M, First with { Segmentation = (RmQRSegmentation)7 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("€", RmQREccLevel.M, First with { EciMode = EciMode.Iso8859_1 })).Throws<ArgumentException>()
            .Because("Latin-1 declared over content it cannot represent is refused under FNC1 as without");
    }

    [Test]
    public async Task TooLong_HeadersAlone_SaysSoRatherThanAZeroMaximum()
    {
        // R7x43-H holds 24 bits; the ECI prefix, the second-position indicator, the mode and the count are 28.
        var options = new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Fnc1 = Fnc1Mode.SecondPosition(0), Version = RmQRVersion.R7x43 };
        var error = await Assert.That(() => RmQRCodeGenerator.Create("", RmQREccLevel.H, options)).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("headers");
        await Assert.That(error.Message).DoesNotContain("maximum is 0 bytes");
    }

    [Test]
    public async Task TooLong_Alphanumeric_CountsUnitsNotCharacters()
    {
        var error = await Assert.That(() => RmQRCodeGenerator.Create("%%%%", RmQREccLevel.M, First with { Version = RmQRVersion.R7x43 })).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("8 units in Alphanumeric mode").Because("a literal % is two units, and the four characters are not eight");
    }

    [Test]
    [Arguments(1 << 22)]
    [Arguments((1 << 22) - 1)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task Options_UndefinedSegmentation_IsRefused(int value)
    {
        var options = new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)value };
        var error = await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, options with { Fnc1 = Fnc1Mode.FirstPosition })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("123", RmQREccLevel.M, out _, options)).Throws<ArgumentOutOfRangeException>();

        // The field keeps no value of this range apart, so the message names the range and no number the caller did not give.
        await Assert.That(error!.Message).Contains("Invalid rMQR segmentation: a value below 0 or above 4194302");
        await Assert.That(error.Message).DoesNotContain("4194303");
    }

    [Test]
    [Arguments(7)]
    [Arguments((1 << 22) - 2)]
    public async Task Options_UndefinedSegmentationTheFieldHolds_IsReportedAsGiven(int value)
    {
        var options = new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)value };
        var error = await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(error!.Message).Contains($"Invalid rMQR segmentation: {value}");
    }

    [Test]
    public async Task TooLong_ThrowsAsWithoutFnc1()
    {
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 362), RmQREccLevel.M, First)).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 361), RmQREccLevel.M, First)).Throws<ArgumentException>().Because("361 digits fill R17x139-M exactly, and the indicator needs 3 more");
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 362), RmQREccLevel.M, FirstOptimal)).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 12), RmQREccLevel.M, First with { Version = RmQRVersion.R7x43 })).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 12), RmQREccLevel.M, First with { Height = RmQRHeight.H7 })).ThrowsNothing().Because("R7x59 holds it");
    }

    // Sizing and the span form

    public static IEnumerable<Func<(string Text, RmQRCodeGeneratorOptions Options)>> SizingCases()
    {
        var texts = new[] { Gtin + "17231120", Gtin + "10ABC123" + Gs + "17231120", Gtin + "101234567p" + Gs + "17231120", "10AB" + Gs + Gs + "CD", Gtin + "10é", "AA1234BBB112text text text text\r", "" };
        var modes = new[] { Fnc1Mode.FirstPosition, Fnc1Mode.SecondPosition(37), Fnc1Mode.SecondPosition('z') };
        foreach (var text in texts)
        {
            foreach (var mode in modes)
            {
                foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
                {
                    foreach (var strategy in new[] { RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeHeight })
                    {
                        var captured = (text, new RmQRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, FitStrategy = strategy });
                        yield return () => captured;
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(SizingCases))]
    public async Task Sizing_AgreesWithCreate_AndTheSpanFormWritesTheSameSymbol((string Text, RmQRCodeGeneratorOptions Options) c)
    {
        var data = RmQRCodeGenerator.Create(c.Text, RmQREccLevel.M, c.Options);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(c.Text, RmQREccLevel.M, out var size, c.Options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(data.Version);
        await Assert.That((size.Width, size.Height)).IsEqualTo((data.Width, data.Height));

        var buffer = new byte[size.BufferSize];
        var written = RmQRCodeGenerator.Create(c.Text, RmQREccLevel.M, buffer, c.Options);
        await Assert.That(written).IsEqualTo(size.BufferSize);
        var fromData = new byte[size.BufferSize];
        for (var row = 0; row < size.Height; row++)
            for (var col = 0; col < size.Width; col++)
                fromData[row * size.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        await Assert.That(buffer).IsEquivalentTo(fromData, CollectionOrdering.Matching).Because("the span form writes the modules the data form holds");

        await Assert.That(RmQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(c.Text);
        await Assert.That(info.Fnc1).IsEqualTo(c.Options.Fnc1);
    }

    [Test]
    public async Task Sizing_TooLong_ReturnsFalse()
    {
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('7', 361), RmQREccLevel.M, out _, First)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('7', 361), RmQREccLevel.M, out _, FirstOptimal)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('7', 12), RmQREccLevel.M, out _, First with { Version = RmQRVersion.R7x43 })).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('7', 361), RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions())).IsTrue().Because("without FNC1 the digits fit");
    }

    // The committed FNC1 fixtures: every rMQR symbol another writer made reads back from this writer's symbol of the same data, under Optimal at the same version (the writers split
    // the modes, so a mixed-case serial fits a version its single-mode stream does not)

    public static IEnumerable<Func<string>> RmQrFixtureIds() => FixtureLoader.EnumerateFixtureIds("RmQrFnc1").Select(id => (Func<string>)(() => id));

    [Test]
    [MethodDataSource(nameof(RmQrFixtureIds))]
    public async Task Fixtures_TheSameDataEncodesHere_AndReadsBackWithTheMode(string fixtureId)
    {
        var manifest = FixtureLoader.Load("RmQrFnc1", fixtureId).Manifest;
        var fnc1 = manifest.Fnc1!;
        var mode = fnc1.Position == "First"
            ? Fnc1Mode.FirstPosition
            : fnc1.ApplicationIndicator < 100 ? Fnc1Mode.SecondPosition(fnc1.ApplicationIndicator!.Value) : Fnc1Mode.SecondPosition((char)(fnc1.ApplicationIndicator!.Value - 100));
        foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
        {
            var options = new RmQRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, EciMode = manifest.EciCharset is null ? EciMode.Default : EciMode.Utf8 };
            if (segmentation == RmQRSegmentation.Optimal)
                options.Version = (RmQRVersion)manifest.Version;
            var data = RmQRCodeGenerator.Create(manifest.PayloadText, Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel), options);
            await Assert.That(RmQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because($"{fixtureId} ({segmentation}): {info.Status}");
            await Assert.That(decoded).IsEqualTo(manifest.PayloadText).Because($"{fixtureId} ({segmentation})");
            await Assert.That(info.Fnc1).IsEqualTo(mode).Because(fixtureId);
            if (segmentation == RmQRSegmentation.Optimal)
                await Assert.That((int)info.Version).IsEqualTo(manifest.Version).Because(fixtureId);
        }
    }

#if !DEBUG
    /// <summary>The span form allocates nothing under FNC1, Single and Optimal alike (Release only, as the other allocation tests are).</summary>
    [Test]
    [NotInParallel]
    public async Task SpanForm_AllocatesNothing()
    {
        var text = Gtin + "10ABC123" + Gs + "17231120" + Gs + "21abc%def";
        var buffer = new byte[4_000];
        var single = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        var optimal = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = RmQRSegmentation.Optimal };
        for (var i = 0; i < 3; i++)
        {
            RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, buffer, single);
            RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, buffer, optimal);
            RmQRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), RmQREccLevel.M, out _, optimal);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, buffer, single);
            RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, buffer, optimal);
            RmQRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), RmQREccLevel.M, out _, optimal);
        }
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsEqualTo(0);
    }
#endif

    // Helpers

    private static (string Text, RmQRCodeDecodeInfo Info, byte[] Codewords) Read(RmQRCodeData data)
    {
        if (!RmQRCodeDecoder.TryDecode(data, out var text, out var info))
            throw new InvalidOperationException($"The symbol does not decode: {info.Status}");
        return (text, info, Fnc1StreamShape.DataCodewordsOfRmQr(Modules(data), data.Width, data.Height, info.Version, info.EccLevel));
    }

    private static Fnc1StreamShape Shape(RmQRCodeData data, RmQRCodeDecodeInfo info)
        => Fnc1StreamShape.OfRmQr(Modules(data), data.Width, data.Height, info.Version, info.EccLevel);

    /// <summary>The core modules of a symbol built with no quiet zone.</summary>
    private static int Area(RmQRCodeData data) => data.Width * data.Height;

    /// <summary>The fit-strategy order, written out apart from the selector's comparator: fewer modules, a smaller width or a smaller height first, the other dimension breaking a tie.</summary>
    private static bool IsBetter(RmQRVersion candidate, RmQRVersion incumbent, RmQRFitStrategy strategy)
    {
        var (ch, cw) = Dimensions(candidate);
        var (ih, iw) = Dimensions(incumbent);
        return strategy switch
        {
            RmQRFitStrategy.MinimizeArea => ch * cw != ih * iw ? ch * cw < ih * iw : ch < ih,
            RmQRFitStrategy.MinimizeWidth => cw != iw ? cw < iw : ch < ih,
            _ => ch != ih ? ch < ih : cw < iw,
        };
    }

    private static (int Height, int Width) Dimensions(RmQRVersion version)
    {
        var name = version.ToString(); // R{height}x{width}
        var x = name.IndexOf('x');
        return (int.Parse(name.AsSpan(1, x - 1)), int.Parse(name.AsSpan(x + 1)));
    }

    /// <summary>The modules of a symbol built with no quiet zone, 1 for dark, as the fixture matrices are read.</summary>
    private static byte[] Modules(RmQRCodeData data)
    {
        var width = data.Width;
        var height = data.Height;
        var modules = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                modules[row * width + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }
}
