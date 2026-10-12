using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// Writing FNC1 into a Standard QR symbol (fnc1-support-plan.md, Phase 3): the indicator in the standard's order
/// (ECI, FNC1, the application indicator, data), the <c>%</c> rule applied in reverse in Alphanumeric segments (a
/// separator written <c>%</c>, a literal <c>%</c> written <c>%%</c>), the separator pairs no segment can hold kept
/// out of Alphanumeric mode, the two examples of ISO/IEC 18004 6.4.8 reproduced bit for bit under
/// <see cref="QRSegmentation.Optimal"/>, every class read back by this library's decoder with the mode, and
/// sizing that agrees with <c>Create</c>.
/// </summary>
public class Fnc1EncodeTest
{
    private const string Gs = "\u001D";
    private const string Gtin = "0109524000059109";
    private static readonly QRCodeGeneratorOptions First = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0 };
    private static readonly QRCodeGeneratorOptions FirstOptimal = new() { Fnc1 = Fnc1Mode.FirstPosition, QuietZoneSize = 0, Segmentation = QRSegmentation.Optimal };

    // The two examples of the standard, bit for bit

    [Test]
    public async Task IsoExample_FirstPosition_Optimal_WritesTheStandardsStream()
    {
        // Level M: the split's 182 bits fit version 2-M (224) where the single Alphanumeric stream (226) does not, so Optimal
        // splits. At level L both fit version 2 and Optimal emits the single-mode stream, as its contract says.
        var text = "01049123451234591597033130128" + Gs + "10ABC123";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, FirstOptimal);
        var (decoded, info, codewords) = Read(data);

        await Assert.That(info.Version).IsEqualTo(2).Because("182 data bits, over version 1-M's 128 and within 2-M's 224");
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
        var data = QRCodeGenerator.Create(text, QREccLevel.M, First);
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
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);
        await Assert.That(shape.Headers).IsEmpty();
        await Assert.That(shape.DataModes).IsEquivalentTo([mode], CollectionOrdering.Matching);
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

    /// <summary>The standard's header order (decision 5): ECI, then FNC1, then the data. Non-ASCII text takes UTF-8 behind the ECI header, as it does without FNC1.</summary>
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
    [Arguments(0, "00")]
    [Arguments(99, "99")]
    [Arguments(165, "A")]
    [Arguments(222, "z")]
    public async Task SecondPosition_EachIndicatorForm_ReadsBackAsItsCodeword(int codeword, string written)
    {
        var mode = codeword < 100 ? Fnc1Mode.SecondPosition(codeword) : Fnc1Mode.SecondPosition((char)(codeword - 100));
        var text = "AA1234BBB112";
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = mode, QuietZoneSize = 0 });
        var (decoded, info, _) = Read(data);
        var shape = Shape(data, info);

        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Fnc1.ApplicationIndicator).IsEqualTo(codeword);
        await Assert.That(shape.Headers).IsEquivalentTo([$"FNC1Second:{codeword}"], CollectionOrdering.Matching);
        await Assert.That(written.Length).IsBetween(1, 2);
    }

    // Optimal: the separator pairs, and the mix of modes

    /// <summary>A separator pair (decision 4) is resolved by a segment break where the runs are long and by a Byte segment where they are short, whichever costs less.</summary>
    [Test]
    public async Task Optimal_SeparatorPair_IsResolvedByABreakOrByByte()
    {
        var longText = "ABCDEFGHIJKLMNOP" + Gs + Gs + "QRSTUVWXYZ0123";
        var longData = QRCodeGenerator.Create(longText, QREccLevel.M, FirstOptimal);
        var (longDecoded, longInfo, _) = Read(longData);
        await Assert.That(longDecoded).IsEqualTo(longText);
        await Assert.That(Shape(longData, longInfo).DataModes).IsEquivalentTo(["Alphanumeric", "Alphanumeric"], CollectionOrdering.Matching).Because("two Alphanumeric runs cost 13 bits more, a Byte run 100 more");

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

    /// <summary>Under FNC1 a Kanji-eligible text with ASCII is planned over UTF-8 only; the symbol still reads back.</summary>
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
        var digits = new string('7', 41); // 4 + 10 + 136 = 150 of version 1-L's 152 bits
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
        var text = new string('7', 7090);
        await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.L, First)).Throws<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(new string('7', 41), QREccLevel.L, First with { Version = QRVersionRange.Exactly(1) })).Throws<ArgumentException>();
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
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 7090), QREccLevel.L, out _, First)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('7', 41), QREccLevel.L, out _, First with { Version = QRVersionRange.Exactly(1) })).IsFalse();
    }

    // The Phase 0 fixtures: every symbol another writer made reads back from this writer's symbol of the same data

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
    /// <summary>The span form allocates nothing under FNC1, Single and Optimal alike (Release only, as the other allocation tests are).</summary>
    [Test]
    [NotInParallel]
    public async Task SpanForm_AllocatesNothing()
    {
        var text = Gtin + "10ABC123" + Gs + "17231120" + Gs + "21abc%def";
        var buffer = new byte[40_000];
        var single = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        var optimal = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = QRSegmentation.Optimal };
        for (var i = 0; i < 3; i++)
        {
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, single);
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), QREccLevel.M, out _, optimal);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, single);
            QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, buffer, optimal);
            QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), QREccLevel.M, out _, optimal);
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
