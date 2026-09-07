using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRSegmentPlanner"/> against an independent optimum. The reference is
/// a segment-level dynamic program (minimise over every cut point and mode
/// assignment, costing each run from scratch), which is a different formulation from
/// the production character-level state machine, so the two only agree when both are
/// right. Also pins the plan shape invariants the encoder relies on and the UTF-8
/// per-character cost model against <see cref="Encoding.UTF8"/>.
/// </summary>
public class RmQRSegmentPlannerUnitTest
{
    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>Contents chosen so every mode transition and both surrogate shapes occur.</summary>
    public static IEnumerable<string> Corpus() =>
    [
        "",
        "0",
        "A",
        "a",
        "1234567890",
        "ABCDEFGHIJ",
        "abcdefghij",
        "123Abc",
        "https://example.com/p/1234567890123456",
        "https://example.com/",
        "ABC-1234567890123456",
        "ORDER 12345 ITEM 6789",
        "a1b2c3d4e5f6g7h8i9",
        "1234567890abcdefghij1234567890",
        "12A34B56C78D90E",
        "$%*+-./: 0123456789",
        "SN/2024-000123456789",
        "x1234567890123456789012345678901234567890",
        "1234567890123456789012345678901234567890x",
        "AAAAAAAAAA1111111111AAAAAAAAAA",
        "éèê1234567890",
        "日本語1234567890",
        "こんにちは",
        "😀😁1234567890",
        "\ud800",
        "\udc00",
        "𐀀\udc00",
        "\ud800𐀀",
        "é😀A1",
    ];

    public static IEnumerable<(RmQRVersion Version, RmQREccLevel Ecc)> SampleVersions() =>
    [
        (RmQRVersion.R7x43, RmQREccLevel.M),
        (RmQRVersion.R11x27, RmQREccLevel.H),
        (RmQRVersion.R13x59, RmQREccLevel.M),
        (RmQRVersion.R15x43, RmQREccLevel.M),
        (RmQRVersion.R17x139, RmQREccLevel.M),
        (RmQRVersion.R17x139, RmQREccLevel.H),
    ];

    // ---------------------------------------------------------------
    // Independent reference
    // ---------------------------------------------------------------

    private static bool IsNumericRun(string run)
    {
        foreach (var c in run)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return run.Length > 0;
    }

    private static bool IsAlnumRun(string run)
    {
        foreach (var c in run)
        {
            if (!AlphanumericAlphabet.Contains(c))
                return false;
        }
        return run.Length > 0;
    }

    private static int ByteCount(string run, EciMode charset)
        => charset == EciMode.Utf8 ? Encoding.UTF8.GetByteCount(run) : run.Length;

    /// <summary>
    /// Minimal payload bits over every partition of the content into runs and every
    /// mode assignment. Deliberately quadratic and string-based; nothing here is
    /// shared with the production planner.
    /// </summary>
    private static int ReferenceMinimumBits(string text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte)
    {
        const int infinity = int.MaxValue / 4;
        var n = text.Length;
        var best = new int[n + 1];
        for (var i = 1; i <= n; i++)
            best[i] = infinity;

        for (var end = 1; end <= n; end++)
        {
            for (var start = 0; start < end; start++)
            {
                if (best[start] >= infinity)
                    continue;
                var run = text.Substring(start, end - start);

                if (IsNumericRun(run))
                {
                    var bits = run.Length / 3 * 10 + (run.Length % 3) switch { 2 => 7, 1 => 4, _ => 0 };
                    best[end] = Math.Min(best[end], best[start] + 3 + cciNumeric + bits);
                }
                if (IsAlnumRun(run))
                {
                    var bits = run.Length / 2 * 11 + run.Length % 2 * 6;
                    best[end] = Math.Min(best[end], best[start] + 3 + cciAlnum + bits);
                }
                best[end] = Math.Min(best[end], best[start] + 3 + cciByte + 8 * ByteCount(run, charset));
            }
        }

        return best[n];
    }

    // ---------------------------------------------------------------
    // Production access
    // ---------------------------------------------------------------

    internal readonly record struct PlannedSegment(EncodingMode Mode, int Start, int Length, int UnitCount);

    internal static PlannedSegment[] BuildPlan(string text, EciMode charset, RmQRVersion version, RmQREccLevel ecc)
    {
        Span<ModeSegment> buffer = stackalloc ModeSegment[RmQRSegmentPlanner.MaxSegments];
        if (!RmQRSegmentPlanner.TryBuildPlan(text.AsSpan(), charset, version, ecc, buffer, out var count))
            return [];

        var plan = new PlannedSegment[count];
        for (var i = 0; i < count; i++)
            plan[i] = new PlannedSegment(buffer[i].Mode, buffer[i].Start, buffer[i].Length, buffer[i].UnitCount);
        return plan;
    }

    private static int MeasurePlan(PlannedSegment[] plan, RmQRVersion version)
    {
        var total = 0;
        foreach (var segment in plan)
        {
            total += 3 + RmQRConstants.GetCountIndicatorLength(version, segment.Mode);
            total += segment.Mode switch
            {
                EncodingMode.Numeric => segment.UnitCount / 3 * 10 + (segment.UnitCount % 3) switch { 2 => 7, 1 => 4, _ => 0 },
                EncodingMode.Alphanumeric => segment.UnitCount / 2 * 11 + segment.UnitCount % 2 * 6,
                _ => segment.UnitCount * 8,
            };
        }
        return total;
    }

    // ---------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task MinimumPayloadBits_MatchesIndependentOptimum_AllVersionsAndCharsets(string text)
    {
        if (text.Length == 0)
            return;

        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var cciNumeric = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
            var cciAlnum = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
            var cciByte = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);

            foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
            {
                // ISO-8859-1 and Default cost the same one byte per character; the
                // Utf8 pass is what exercises the multi-byte and surrogate model.
                if (charset == EciMode.Default && text.Any(c => c > 255))
                    continue;

                var actual = RmQRSegmentPlanner.MinimumPayloadBits(text.AsSpan(), charset, cciNumeric, cciAlnum, cciByte);
                var expected = ReferenceMinimumBits(text, charset, cciNumeric, cciAlnum, cciByte);
                await Assert.That(actual).IsEqualTo(expected);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task MinimumPayloadBits_NeverExceedsSingleMode(string text)
    {
        if (text.Length == 0)
            return;

        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var charset = text.Any(c => c > 255) ? EciMode.Utf8 : EciMode.Default;
            var analysis = TextAnalyzer.Analyze(text.AsSpan(), charset == EciMode.Utf8 ? EciMode.Utf8 : EciMode.Default);
            var singleBits = 3 + RmQRConstants.GetCountIndicatorLength(version, analysis.EncodingMode)
                + RmQRSegmentPlanner.PayloadBits(analysis.EncodingMode, analysis.DataLength);

            var optimal = RmQRSegmentPlanner.MinimumPayloadBits(
                text.AsSpan(),
                analysis.EciMode,
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));

            await Assert.That(optimal).IsLessThanOrEqualTo(singleBits);
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task BuildPlan_CoversContentWithValidRuns(string text)
    {
        if (text.Length == 0)
            return;

        var charset = text.Any(c => c > 255) ? EciMode.Utf8 : EciMode.Default;
        var version = RmQRVersion.R17x139;
        var plan = BuildPlan(text, charset, version, RmQREccLevel.M);

        await Assert.That(plan.Length).IsGreaterThan(0);

        var offset = 0;
        for (var i = 0; i < plan.Length; i++)
        {
            var segment = plan[i];
            await Assert.That(segment.Start).IsEqualTo(offset);
            await Assert.That(segment.Length).IsGreaterThan(0);
            if (i > 0)
                await Assert.That(segment.Mode).IsNotEqualTo(plan[i - 1].Mode);

            var run = text.Substring(segment.Start, segment.Length);
            switch (segment.Mode)
            {
                case EncodingMode.Numeric:
                    await Assert.That(IsNumericRun(run)).IsTrue();
                    await Assert.That(segment.UnitCount).IsEqualTo(segment.Length);
                    break;
                case EncodingMode.Alphanumeric:
                    await Assert.That(IsAlnumRun(run)).IsTrue();
                    await Assert.That(segment.UnitCount).IsEqualTo(segment.Length);
                    break;
                default:
                    await Assert.That(segment.UnitCount).IsEqualTo(ByteCount(run, charset));
                    break;
            }
            offset += segment.Length;
        }

        await Assert.That(offset).IsEqualTo(text.Length);
    }

    [Test]
    public async Task BuildPlan_CostMatchesTheScanCost()
    {
        foreach (var target in SampleVersions())
            foreach (var text in Corpus())
            {
                if (text.Length == 0)
                    continue;
                var charset = text.Any(c => c > 255) ? EciMode.Utf8 : EciMode.Default;
                var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(target.Version, target.Ecc);
                var scan = RmQRSegmentPlanner.MinimumPayloadBits(
                    text.AsSpan(),
                    charset,
                    RmQRConstants.GetCountIndicatorLength(target.Version, EncodingMode.Numeric),
                    RmQRConstants.GetCountIndicatorLength(target.Version, EncodingMode.Alphanumeric),
                    RmQRConstants.GetCountIndicatorLength(target.Version, EncodingMode.Byte));
                var eciBits = charset == EciMode.Default ? 0 : 11;
                if (scan + eciBits > capacityBits)
                    continue; // TryBuildPlan legitimately rejects a plan that does not fit

                var plan = BuildPlan(text, charset, target.Version, target.Ecc);
                await Assert.That(plan.Length).IsGreaterThan(0);
                await Assert.That(MeasurePlan(plan, target.Version)).IsEqualTo(scan);
            }
    }

    /// <summary>
    /// The per-character UTF-8 cost the dynamic program uses has to sum to what the
    /// encoder actually emits, or a plan could be one byte over the capacity the
    /// version was chosen for.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Utf8CostModel_SumsToEncodedByteCount(string text)
    {
        if (text.Length == 0)
            return;

        // A single Byte run over the whole content: its cost is exactly the sum of
        // the per-character model, so a wide count indicator isolates it.
        const int cci = 8;
        var bits = RmQRSegmentPlanner.MinimumPayloadBits(text.AsSpan(), EciMode.Utf8, 15, 15, cci);
        var byteOnly = 3 + cci + 8 * Encoding.UTF8.GetByteCount(text);

        // 15-bit numeric / alphanumeric indicators make any split more expensive than
        // the single Byte run for these contents, so the optimum is the Byte run.
        await Assert.That(bits).IsLessThanOrEqualTo(byteOnly);
        if (bits == byteOnly)
            return;

        // Otherwise a split still won: verify the model additively instead.
        var sum = 0;
        var plan = BuildPlan(text, EciMode.Utf8, RmQRVersion.R17x139, RmQREccLevel.M);
        foreach (var segment in plan)
        {
            if (segment.Mode == EncodingMode.Byte)
                sum += segment.UnitCount;
        }
        var expected = 0;
        foreach (var segment in plan)
        {
            if (segment.Mode == EncodingMode.Byte)
                expected += Encoding.UTF8.GetByteCount(text.Substring(segment.Start, segment.Length));
        }
        await Assert.That(sum).IsEqualTo(expected);
    }

    /// <summary>
    /// The version scan prunes candidates with a cost run at the narrowest count
    /// indicators any version uses. If those constants were not the true minima the
    /// floor would not be a floor, and a version that actually fits could be skipped.
    /// </summary>
    [Test]
    public async Task MinimumCountIndicatorWidths_AreTheNarrowestAnyVersionUses()
    {
        var minNumeric = int.MaxValue;
        var minAlnum = int.MaxValue;
        var minByte = int.MaxValue;
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            minNumeric = Math.Min(minNumeric, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric));
            minAlnum = Math.Min(minAlnum, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric));
            minByte = Math.Min(minByte, RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));
        }

        await Assert.That(minNumeric).IsEqualTo(4);
        await Assert.That(minAlnum).IsEqualTo(3);
        await Assert.That(minByte).IsEqualTo(3);
    }


    /// <summary>
    /// The pruning floor must never exceed a version's real cost, or the scan would
    /// skip a version the plan fits.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task FloorCost_NeverExceedsAnyVersionCost(string text)
    {
        if (text.Length == 0)
            return;

        var charset = text.Any(c => c > 255) ? EciMode.Utf8 : EciMode.Default;
        var floor = RmQRSegmentPlanner.MinimumPayloadBits(text.AsSpan(), charset, 4, 3, 3);

        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var actual = RmQRSegmentPlanner.MinimumPayloadBits(
                text.AsSpan(),
                charset,
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));
            await Assert.That(floor).IsLessThanOrEqualTo(actual);
        }
    }

    /// <summary>
    /// Before any dynamic programming table is touched, the scan filters candidates
    /// with an O(n) bound built from per-character minimum rates. It must never exceed
    /// the true optimum at any version, or a version the split could actually reach
    /// would be skipped and the symbol would come out larger than it should.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task TrivialLowerBound_NeverExceedsAnyVersionCost(string text)
    {
        if (text.Length == 0)
            return;

        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            if (charset == EciMode.Default && text.Any(c => c > 255))
                continue;

            var trivial = RmQRSegmentPlanner.TrivialLowerBoundBits(text.AsSpan(), charset);
            foreach (var version in Enum.GetValues<RmQRVersion>())
            {
                var actual = RmQRSegmentPlanner.MinimumPayloadBits(
                    text.AsSpan(),
                    charset,
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));

                await Assert.That(trivial).IsLessThanOrEqualTo(actual);
            }
        }
    }

    /// <summary>
    /// It also has to be tight enough to be worth having. For content a single mode
    /// already encodes optimally the bound is within one header of the truth, which is
    /// what lets the scan skip planning for uniform content entirely.
    /// </summary>
    [Test]
    [Arguments("AAAAAAAAAAAAAAAAAAAA")]
    [Arguments("aaaaaaaaaaaaaaaaaaaa")]
    [Arguments("$%*+-./: $%*+-./: ")]
    public async Task TrivialLowerBound_IsTightForUniformContent(string text)
    {
        var trivial = RmQRSegmentPlanner.TrivialLowerBoundBits(text.AsSpan(), EciMode.Default);
        var actual = RmQRSegmentPlanner.MinimumPayloadBits(
            text.AsSpan(),
            EciMode.Default,
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Numeric),
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Alphanumeric),
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Byte));

        // Only the count indicator width and the rounding of a partial group separate them.
        await Assert.That(actual - trivial).IsLessThanOrEqualTo(12);
    }

    /// <summary>
    /// The version scan accepts a candidate without pricing it when the candidate
    /// holds an arithmetic upper bound derived from the floor plan. The bound must
    /// never fall below the version's true cost, or a version the plan does not fit
    /// would be accepted.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task UpperBound_NeverFallsBelowTheTrueCost(string text)
    {
        if (text.Length == 0)
            return;

        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            if (charset == EciMode.Default && text.Any(c => c > 255))
                continue;

            foreach (var version in Enum.GetValues<RmQRVersion>())
            {
                var bound = RmQRSegmentPlanner.FloorPlanUpperBound(text.AsSpan(), charset, version);
                var actual = RmQRSegmentPlanner.MinimumPayloadBits(
                    text.AsSpan(),
                    charset,
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));

                await Assert.That(bound).IsGreaterThanOrEqualTo(actual);
            }
        }
    }

    /// <summary>
    /// And it must stay tight enough to be worth having: the floor plan is a real
    /// plan, so at the narrowest widths the bound is exactly the true cost.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task UpperBound_IsExactAtTheNarrowestWidths(string text)
    {
        if (text.Length == 0)
            return;

        var charset = text.Any(c => c > 255) ? EciMode.Utf8 : EciMode.Default;
        var floor = RmQRSegmentPlanner.MinimumPayloadBits(text.AsSpan(), charset, 4, 3, 3);

        // R7x43 is the only version whose widths are exactly the minima (4 / 3 / 3), so
        // it is the one version where the bound must equal the floor exactly.
        var bound = RmQRSegmentPlanner.FloorPlanUpperBound(text.AsSpan(), charset, RmQRVersion.R7x43);
        await Assert.That(bound).IsEqualTo(floor);
        await Assert.That(bound).IsGreaterThanOrEqualTo(floor);
        await Assert.That(bound - floor).IsLessThanOrEqualTo(RmQRSegmentPlanner.MaxSegments);
    }

    /// <summary>
    /// The 361-character planning limit is a rejection rule, not just a work cap:
    /// content above it is declared impossible rather than merely unplanned. The
    /// narrowest count indicator widths are pinned above; this pins the other side,
    /// that 362 characters really do fit nowhere. The margin is three bits, so a capacity
    /// table change could silently make the constant unsound or over-strict.
    /// </summary>
    [Test]
    public async Task PlanningLimit_IsTheLongestContentAnyVersionCanHold()
    {
        // Densest shapes available: all digits, and digits with one non-numeric
        // character at either end (which forces a second run without adding bulk).
        string[] shapes =
        [
            new string('7', 362),
            "A" + new string('7', 361),
            new string('7', 361) + "A",
            new string('A', 362),
        ];

        foreach (var text in shapes)
        {
            foreach (var version in Enum.GetValues<RmQRVersion>())
            {
                var cost = RmQRSegmentPlanner.MinimumPayloadBits(
                    text.AsSpan(),
                    EciMode.Default,
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));

                foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
                    await Assert.That(cost).IsGreaterThan(8 * RmQRConstants.GetDataCodewordCount(version, ecc));
            }
        }

        // And 361 digits must still fit, or the limit would be over-strict.
        var atLimit = new string('7', 361);
        var atLimitCost = RmQRSegmentPlanner.MinimumPayloadBits(
            atLimit.AsSpan(),
            EciMode.Default,
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Numeric),
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Alphanumeric),
            RmQRConstants.GetCountIndicatorLength(RmQRVersion.R17x139, EncodingMode.Byte));
        await Assert.That(atLimitCost).IsLessThanOrEqualTo(8 * RmQRConstants.GetDataCodewordCount(RmQRVersion.R17x139, RmQREccLevel.M));
    }

    /// <summary>
    /// <see cref="RmQRSegmentPlanner.MaxSegments"/> has to exceed the number of runs
    /// any version could hold, or a legitimate plan would silently fall back.
    /// </summary>
    [Test]
    public async Task MaxSegments_ExceedsTheRunsAnyVersionCanHold()
    {
        var worst = 0;
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(version, ecc);
                var cheapestRun = Math.Min(
                    Math.Min(
                        3 + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric) + 4,
                        3 + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric) + 6),
                    3 + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte) + 8);
                worst = Math.Max(worst, capacityBits / cheapestRun);
            }
        }

        await Assert.That(worst).IsLessThan(RmQRSegmentPlanner.MaxSegments);
    }
}
