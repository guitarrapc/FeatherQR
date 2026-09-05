using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The multi-segment writer against the naive bit-string reference
/// (<see cref="RmQRNaiveReference.NaiveSegmentedDataCodewords"/>), plus the two
/// properties the rest of the pipeline depends on: a one-run plan is byte-identical
/// to the single-segment writer, and every stream the writer emits decodes back.
/// </summary>
public class RmQRBinaryEncoderSegmentedTest
{
    private static string ModeName(EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => "Numeric",
        EncodingMode.Alphanumeric => "Alphanumeric",
        _ => "Byte",
    };

    private static byte[] EncodePlan(string text, RmQRVersion version, RmQREccLevel ecc, EciMode charset, RmQRSegmentPlannerUnitTest.PlannedSegment[] plan)
    {
        Span<ModeSegment> segments = stackalloc ModeSegment[RmQRSegmentPlanner.MaxSegments];
        for (var i = 0; i < plan.Length; i++)
        {
            var modeIndex = RmQRConstants.GetModeIndex(plan[i].Mode);
            segments[i] = new ModeSegment(modeIndex, plan[i].Start, plan[i].Length, plan[i].UnitCount);
        }

        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewordsSegmented(text.AsSpan(), version, ecc, charset, segments.Slice(0, plan.Length), destination);
        return destination;
    }

    private static byte[] EncodeSingle(string text, RmQRVersion version, RmQREccLevel ecc, EciMode requestedEci)
    {
        var analysis = TextAnalyzer.Analyze(text.AsSpan(), requestedEci);
        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), version, ecc, in analysis, destination);
        return destination;
    }

    private static string DecodeStream(byte[] dataCodewords, RmQRVersion version)
    {
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(version)];
        var status = RmQRBinaryDecoder.DecodeBitStream(dataCodewords, dataCodewords.Length * 8, version, destination, out var written);
        if (status != DecodeStatus.Success)
            throw new InvalidOperationException($"decode failed: {status}");
        return new string(destination, 0, written);
    }

    public static IEnumerable<string> MixedContents() =>
    [
        "https://example.com/p/1234567890123456",
        "ABC-1234567890123456",
        "ORDER 12345 ITEM 6789",
        "123Abc",
        "a1b2c3d4e5f6",
        "SN/2024-000123456789",
        "AAAAAAAAAA1111111111",
        "x123456789012345678901234567890",
    ];

    public static IEnumerable<string> Utf8Contents() =>
    [
        "日本語1234567890",
        "éèê1234567890",
        "😀1234567890",
        "商品12345です",
    ];

    [Test]
    [MethodDataSource(nameof(MixedContents))]
    public async Task EncodeSegmented_MatchesNaiveReference_Ascii(string content)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        const RmQREccLevel ecc = RmQREccLevel.M;
        var plan = RmQRSegmentPlannerUnitTest.BuildPlan(content, EciMode.Default, version, ecc);
        await Assert.That(plan.Length).IsGreaterThan(0);

        var actual = EncodePlan(content, version, ecc, EciMode.Default, plan);
        var expected = RmQRNaiveReference.NaiveSegmentedDataCodewords(
            RmQRConstants.GetDataCodewordCount(version, ecc),
            plan.Select(s => (ModeName(s.Mode), content.Substring(s.Start, s.Length), RmQRConstants.GetCountIndicatorLength(version, s.Mode))).ToArray(),
            utf8: false);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Utf8Contents))]
    public async Task EncodeSegmented_MatchesNaiveReference_Utf8(string content)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        const RmQREccLevel ecc = RmQREccLevel.M;
        var plan = RmQRSegmentPlannerUnitTest.BuildPlan(content, EciMode.Utf8, version, ecc);
        await Assert.That(plan.Length).IsGreaterThan(0);

        var actual = EncodePlan(content, version, ecc, EciMode.Utf8, plan);
        var expected = RmQRNaiveReference.NaiveSegmentedDataCodewords(
            RmQRConstants.GetDataCodewordCount(version, ecc),
            plan.Select(s => (ModeName(s.Mode), content.Substring(s.Start, s.Length), RmQRConstants.GetCountIndicatorLength(version, s.Mode))).ToArray(),
            utf8: true,
            eciMode: EciMode.Utf8);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    public static IEnumerable<string> Latin1Contents() =>
    [
        "éèê1234567890",
        "Café 12345678901234",
        "Ærøskøbing 987654321",
    ];

    /// <summary>
    /// Latin-1 runs behind an ECI prefix: the only charset combination the writer
    /// handles that the Default and UTF-8 cases do not reach.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Latin1Contents))]
    public async Task EncodeSegmented_MatchesNaiveReference_Iso88591(string content)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        const RmQREccLevel ecc = RmQREccLevel.M;
        var plan = RmQRSegmentPlannerUnitTest.BuildPlan(content, EciMode.Iso8859_1, version, ecc);
        await Assert.That(plan.Length).IsGreaterThan(0);

        var actual = EncodePlan(content, version, ecc, EciMode.Iso8859_1, plan);
        var expected = RmQRNaiveReference.NaiveSegmentedDataCodewords(
            RmQRConstants.GetDataCodewordCount(version, ecc),
            plan.Select(s => (ModeName(s.Mode), content.Substring(s.Start, s.Length), RmQRConstants.GetCountIndicatorLength(version, s.Mode))).ToArray(),
            utf8: false,
            eciMode: EciMode.Iso8859_1);

        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That(DecodeStream(actual, version)).IsEqualTo(content);
    }

    [Test]
    [Arguments("1234567890", 0)]
    [Arguments("ABCDEF12345", 1)]
    [Arguments("hello world", 2)]
    public async Task EncodeSegmented_SingleRunPlan_MatchesTheSingleSegmentWriter(string content, int modeIndex)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        const RmQREccLevel ecc = RmQREccLevel.M;
        var mode = modeIndex switch { 0 => EncodingMode.Numeric, 1 => EncodingMode.Alphanumeric, _ => EncodingMode.Byte };
        var plan = new[] { new RmQRSegmentPlannerUnitTest.PlannedSegment(mode, 0, content.Length, content.Length) };

        var segmented = EncodePlan(content, version, ecc, EciMode.Default, plan);
        var single = EncodeSingle(content, version, ecc, EciMode.Default);

        await Assert.That(segmented).IsEquivalentTo(single);
    }

    [Test]
    [MethodDataSource(nameof(MixedContents))]
    public async Task EncodeSegmented_RoundTripsThroughTheBitStreamDecoder(string content)
    {
        foreach (var version in new[] { RmQRVersion.R15x43, RmQRVersion.R13x139, RmQRVersion.R17x139 })
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var plan = RmQRSegmentPlannerUnitTest.BuildPlan(content, EciMode.Default, version, ecc);
                if (plan.Length == 0)
                    continue;
                var stream = EncodePlan(content, version, ecc, EciMode.Default, plan);
                await Assert.That(DecodeStream(stream, version)).IsEqualTo(content);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Utf8Contents))]
    public async Task EncodeSegmented_Utf8_RoundTripsThroughTheBitStreamDecoder(string content)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            var plan = RmQRSegmentPlannerUnitTest.BuildPlan(content, EciMode.Utf8, version, ecc);
            if (plan.Length == 0)
                continue;
            var stream = EncodePlan(content, version, ecc, EciMode.Utf8, plan);
            await Assert.That(DecodeStream(stream, version)).IsEqualTo(content);
        }
    }

    // -----------------------------------------------------------------
    // Negative cases: a hand-built plan that does not describe the content must be
    // rejected before the writer stores anything, because the fast writers do not
    // bounds-check per flush.
    // -----------------------------------------------------------------

    private static void EncodeRaw(string text, RmQRVersion version, RmQREccLevel ecc, EciMode charset, ModeSegment[] segments)
    {
        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewordsSegmented(text.AsSpan(), version, ecc, charset, segments, destination);
    }

    [Test]
    public async Task EncodeSegmented_EmptyPlan_Throws()
        => await Assert.That(() => EncodeRaw("123", RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, [])).Throws<ArgumentException>();

    [Test]
    public async Task EncodeSegmented_PlanThatDoesNotFit_Throws()
    {
        var content = new string('a', 40);
        // R7x43-M holds 5 bytes; a Byte run over 40 characters cannot be written.
        ModeSegment[] plan = [new ModeSegment(2, 0, content.Length, content.Length)];
        await Assert.That(() => EncodeRaw(content, RmQRVersion.R7x43, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EncodeSegmented_PlanWithAGap_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 2, 2), new ModeSegment(2, 3, 3, 3)];
        await Assert.That(() => EncodeRaw("12abcd", RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EncodeSegmented_PlanThatStopsShort_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 2, 2)];
        await Assert.That(() => EncodeRaw("12abcd", RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EncodeSegmented_PlanWithAnEmptyRun_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 0, 0), new ModeSegment(2, 0, 6, 6)];
        await Assert.That(() => EncodeRaw("12abcd", RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    /// <summary>
    /// The bit budget is computed from <c>UnitCount</c> while the payload is written
    /// from the run's characters, so a plan whose two disagree would pass the budget
    /// check and then write past the data codewords. The writers store without
    /// per-flush bounds checks, so this has to be rejected, not merely asserted in
    /// Debug: R7x43-M is 48 bits, and this plan budgets 11 while writing 74.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task EncodeSegmented_UnitCountBelowRunLength_Throws(int modeIndex)
    {
        var content = modeIndex switch { 0 => new string('7', 20), 1 => new string('A', 20), _ => new string('a', 20) };
        ModeSegment[] plan = [new ModeSegment(modeIndex, 0, content.Length, 1)];

        await Assert.That(() => EncodeRaw(content, RmQRVersion.R7x43, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    /// <summary>A unit count above the run length is equally inconsistent and equally rejected.</summary>
    [Test]
    public async Task EncodeSegmented_UnitCountAboveRunLength_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 3, 9)];
        await Assert.That(() => EncodeRaw("123", RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, plan)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EncodeSegmented_Utf8RunWithAWrongByteBudget_Throws()
    {
        const string content = "日本語";
        // Three characters, nine UTF-8 bytes; a plan claiming three bytes must not write.
        ModeSegment[] plan = [new ModeSegment(2, 0, 3, 3)];
        await Assert.That(() => EncodeRaw(content, RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Utf8, plan)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EncodeSegmented_UnsupportedCharset_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 3, 3)];
        await Assert.That(() => EncodeRaw("123", RmQRVersion.R17x139, RmQREccLevel.M, (EciMode)4, plan)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncodeSegmented_DestinationTooSmall_Throws()
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 3, 3)];
        await Assert.That(() =>
        {
            var destination = new byte[1];
            RmQRBinaryEncoder.EncodeDataCodewordsSegmented("123".AsSpan(), RmQRVersion.R17x139, RmQREccLevel.M, EciMode.Default, plan, destination);
        }).Throws<ArgumentException>();
    }
}
