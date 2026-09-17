using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
namespace FeatherQR.Tests;

/// <summary>
/// The three properties of the split, checked against an independent greedy walk that
/// shares only the per-chunk cost with the planner: the count is the fewest the largest
/// version allows, the version is the smallest that holds that count, and the budget is
/// the smallest that holds it, so the fullest symbol is as empty as it can be.
/// </summary>
public class StructuredAppendPlannerTest
{
    [Test]
    [Arguments("ascii", 600, QREccLevel.M, 1, 5, QRSegmentation.Single)]
    [Arguments("ascii", 470, QREccLevel.L, 1, 2, QRSegmentation.Single)]
    [Arguments("digits", 1201, QREccLevel.M, 1, 6, QRSegmentation.Single)]
    [Arguments("latin1", 329, QREccLevel.M, 1, 3, QRSegmentation.Single)]
    [Arguments("japanese", 67, QREccLevel.M, 1, 5, QRSegmentation.Single)]
    [Arguments("mixed", 600, QREccLevel.M, 1, 6, QRSegmentation.Optimal)]
    [Arguments("ascii", 600, QREccLevel.Q, 3, 9, QRSegmentation.Single)]
    // Under Optimal the count, the version and the bracket come from one walk near the plan's
    // floor. Content whose density varies along the text, ranges that cross a count indicator
    // band (9/10, 26/27), and content whose characters are too wide for the walk's first margin
    // (a pair is 32 bits that cannot be cut, so the balanced budget sits 32 to 47 bits above the
    // floor and the widening steps run) are where a shortcut that was only right on periodic
    // content would show.
    [Arguments("mixed", 20000, QREccLevel.L, 1, 40, QRSegmentation.Optimal)]
    [Arguments("digitsThenMixed", 20000, QREccLevel.L, 1, 40, QRSegmentation.Optimal)]
    [Arguments("digitsThenMixed", 3000, QREccLevel.L, 1, 10, QRSegmentation.Optimal)]
    [Arguments("proseThenDigits", 2500, QREccLevel.L, 5, 9, QRSegmentation.Optimal)]
    [Arguments("randomRuns", 4000, QREccLevel.L, 9, 12, QRSegmentation.Optimal)]
    [Arguments("randomRuns", 4000, QREccLevel.H, 10, 26, QRSegmentation.Optimal)]
    [Arguments("randomRuns", 9000, QREccLevel.L, 26, 28, QRSegmentation.Optimal)]
    [Arguments("emojiDigits", 393, QREccLevel.H, 1, 10, QRSegmentation.Optimal)]
    [Arguments("emojiDigits", 578, QREccLevel.H, 9, 12, QRSegmentation.Optimal)]
    [Arguments("emojiDigits", 1022, QREccLevel.H, 1, 10, QRSegmentation.Optimal)]
    [Arguments("emojiDigits", 1244, QREccLevel.H, 26, 28, QRSegmentation.Optimal)]
    public async Task Plan_IsFewestSymbolsAtTheSmallestVersionWithTheSmallestBudget(string kind, int length, QREccLevel ecc, int minVersion, int maxVersion, QRSegmentation segmentation)
    {
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan(text, ecc, charset, TextAnalyzer.Analyze(text, charset).EncodingMode, false, segmentation, minVersion, maxVersion, ends, out var count, out var version, out var budget);

        await Assert.That(planned).IsTrue();
        await Assert.That(count).IsBetween(2, 16);
        await Assert.That(version).IsBetween(minVersion, maxVersion);

        // Fewest: the reference walk at the largest version's full capacity needs exactly this many.
        await Assert.That(GreedyCount(text, charset, segmentation, maxVersion, StructuredAppendPlanner.Capacity(maxVersion, ecc))).IsEqualTo(count);

        // Smallest version: one version down (when allowed) needs more.
        if (version > minVersion)
        {
            await Assert.That(GreedyCount(text, charset, segmentation, version - 1, StructuredAppendPlanner.Capacity(version - 1, ecc))).IsGreaterThan(count);
        }

        // Smallest budget: every chunk fits it, and one bit less needs more chunks.
        await Assert.That(budget).IsLessThanOrEqualTo(StructuredAppendPlanner.Capacity(version, ecc));
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            await Assert.That(StructuredAppendPlanner.ChunkBits(text.AsSpan(start, ends[i] - start), charset, version, segmentation, false)).IsLessThanOrEqualTo(budget);
            start = ends[i];
        }
        await Assert.That(start).IsEqualTo(text.Length);
        await Assert.That(GreedyCount(text, charset, segmentation, version, budget - 1)).IsGreaterThan(count);
    }

    [Test]
    [Arguments("ascii", 600, QRSegmentation.Single)]
    [Arguments("digits", 1201, QRSegmentation.Single)]
    [Arguments("latin1", 329, QRSegmentation.Single)]
    [Arguments("japanese", 67, QRSegmentation.Single)]
    [Arguments("emoji", 120, QRSegmentation.Single)]
    [Arguments("mixed", 600, QRSegmentation.Optimal)]
    [Arguments("mixed", 600, QRSegmentation.Single)]
    public async Task Bound_NeverRejectsACountTheWalkReaches(string kind, int length, QRSegmentation segmentation)
    {
        // The bound gates the walk, so wherever the walk reaches a count at a version, the
        // bound must admit that count there; a bound that rejects one turns a fit into a
        // "does not fit".
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var cheapest = StructuredAppendPlanner.CheapestPayloadBits(text, charset);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            for (var version = 1; version <= 40; version++)
            {
                var capacity = StructuredAppendPlanner.Capacity(version, ecc);
                var count = StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, capacity, int.MaxValue, ends);
                if (count == int.MaxValue)
                    continue; // a character that fits no symbol at this version; no count exists
                await Assert.That(StructuredAppendPlanner.CanHold(capacity, count, cheapest, charset)).IsTrue()
                    .Because($"the walk splits {kind} into {count} chunks at version {version}-{ecc}");
            }
        }
    }

    [Test]
    [Arguments("mixed", 900, QRSegmentation.Optimal)]
    [Arguments("mixed", 900, QRSegmentation.Single)]
    [Arguments("transitions", 900, QRSegmentation.Optimal)]
    [Arguments("ascii", 600, QRSegmentation.Optimal)]
    [Arguments("digits", 1201, QRSegmentation.Optimal)]
    [Arguments("latin1", 329, QRSegmentation.Optimal)]
    [Arguments("japanese", 67, QRSegmentation.Optimal)]
    [Arguments("emoji", 120, QRSegmentation.Optimal)]
    [Arguments("alnumDigitsThenJapanese", 400, QRSegmentation.Optimal)]
    public async Task PlannedBound_NeverRejectsACountTheWalkReaches(string kind, int length, QRSegmentation segmentation)
    {
        // The planned bound gates the version scan, so wherever the walk reaches a count at a
        // version, the bound built from the whole text's minimal plan at that version's widths
        // must admit that count there; rejecting one would move the set to a larger version.
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            for (var version = 1; version <= 40; version++)
            {
                var capacity = StructuredAppendPlanner.Capacity(version, ecc);
                var count = StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, capacity, int.MaxValue, ends);
                if (count == int.MaxValue)
                    continue;
                var wholeText = ModeSegmenter.ComputeCosts(text, charset, 4,
                    EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version),
                    default, out _);

                await Assert.That(StructuredAppendPlanner.CanHoldPlanned(capacity, count, wholeText, charset)).IsTrue()
                    .Because($"the walk splits {kind} into {count} chunks at version {version}-{ecc} under {segmentation}");
            }
        }
    }

    [Test]
    public async Task PlannedBound_RejectsVersionsTheRateBoundAdmits()
    {
        // On digit-and-word content the rate bound prices digits as if every one sat in a full
        // Numeric group, so it admits versions no split can hold; the planned bound is the one
        // that turns those away without walking them.
        var text = Text("mixed", 3000);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var cheapest = StructuredAppendPlanner.CheapestPayloadBits(text, charset);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        const int count = 4;

        var turnedAway = 0;
        for (var version = 1; version <= 40; version++)
        {
            var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
            var wholeText = ModeSegmenter.ComputeCosts(text, charset, 4,
                EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version),
                default, out _);
            if (!StructuredAppendPlanner.CanHold(capacity, count, cheapest, charset) || StructuredAppendPlanner.CanHoldPlanned(capacity, count, wholeText, charset))
                continue;

            turnedAway++;
            await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, capacity, count, ends)).IsGreaterThan(count)
                .Because($"version {version} was turned away, so the walk must agree it cannot hold {count}");
        }

        await Assert.That(turnedAway).IsGreaterThan(0);
    }

    [Test]
    [Arguments("mixed", 600)]
    [Arguments("mixed", 3000)]
    [Arguments("transitions", 900)]
    [Arguments("ascii", 2000)]
    [Arguments("digits", 1201)]
    [Arguments("japanese", 67)]
    [Arguments("alnumDigitsThenJapanese", 400)]
    public async Task Version_IsTheSmallestThatHoldsTheCount(string kind, int length)
    {
        // Every version below the one chosen must fail to hold the count, checked by the walk
        // itself rather than by any bound, so a gate that turns away a version able to hold it
        // shows up as a smaller version that fits.
        var text = Text(kind, length);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                if (!StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, false, segmentation, 1, 40, ends, out var count, out var version, out _) || count == 1)
                    continue;

                for (var lower = 1; lower < version; lower++)
                {
                    await Assert.That(StructuredAppendPlanner.CountChunks(text, analysis.EciMode, false, segmentation, lower, StructuredAppendPlanner.Capacity(lower, ecc), count, ends)).IsGreaterThan(count)
                        .Because($"{kind} {length} in {segmentation} at {ecc}: version {lower} holds {count} but {version} was chosen");
                }
            }
        }
    }

    [Test]
    [Arguments("mixed", 600)]
    [Arguments("mixed", 3000)]
    [Arguments("transitions", 900)]
    [Arguments("ascii", 600)]
    [Arguments("ascii", 2000)]
    [Arguments("digits", 1201)]
    [Arguments("latin1", 329)]
    [Arguments("japanese", 67)]
    [Arguments("emoji", 120)]
    [Arguments("alnumDigitsThenJapanese", 400)]
    public async Task Plan_ChunkEnds_AreTheWalkAtTheReturnedBudget(string kind, int length)
    {
        // The split handed back has to be the one the walk produces at the budget handed back,
        // however the planner came by it: re-walked at the end, or kept from the search probe
        // that settled the answer. Pinned versions and narrow ranges put the answer at the top
        // of the bracket too, where no probe settles it.
        var text = Text(kind, length);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        var reference = new int[StructuredAppendPlanner.MaxSymbols];

        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                foreach (var (minVersion, maxVersion) in new[] { (1, 40), (1, 10), (10, 10), (27, 27), (40, 40) })
                {
                    Array.Fill(ends, -1);
                    if (!StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, false, segmentation, minVersion, maxVersion, ends, out var count, out var version, out var budget) || count == 1)
                        continue;

                    Array.Fill(reference, -1);
                    var expected = StructuredAppendPlanner.CountChunks(text, analysis.EciMode, false, segmentation, version, budget, count, reference);

                    var because = $"{kind} {length} in {segmentation} at {ecc}, versions {minVersion}-{maxVersion}";
                    await Assert.That(count).IsEqualTo(expected).Because(because);
                    await Assert.That(ends.Take(count)).IsEquivalentTo(reference.Take(expected)).Because(because);
                }
            }
        }
    }

    [Test]
    public async Task Bound_RejectsACountNoSplitCanReach()
    {
        // 600 ASCII characters cost at least 600 x 8 bits in any mode; five version 1-M
        // symbols offer 5 x (128 - 32) bits of payload. The walk needs far more than five.
        var text = Text("ascii", 600);
        var cheapest = StructuredAppendPlanner.CheapestPayloadBits(text, EciMode.Default);

        await Assert.That(StructuredAppendPlanner.CanHold(StructuredAppendPlanner.Capacity(1, QREccLevel.M), 5, cheapest, EciMode.Default)).IsFalse();
        await Assert.That(StructuredAppendPlanner.CanHold(StructuredAppendPlanner.Capacity(1, QREccLevel.M), 60, cheapest, EciMode.Default)).IsTrue();
    }

    [Test]
    [Arguments("ascii", 600, QREccLevel.M, 5, QRSegmentation.Single)]
    [Arguments("digits", 1201, QREccLevel.M, 6, QRSegmentation.Single)]
    [Arguments("japanese", 67, QREccLevel.M, 5, QRSegmentation.Single)]
    [Arguments("emoji", 120, QREccLevel.M, 3, QRSegmentation.Single)]
    [Arguments("mixed", 600, QREccLevel.M, 6, QRSegmentation.Optimal)]
    public async Task CountChunks_StopsPastTheLimit_AndMatchesTheWalkWithinIt(string kind, int length, QREccLevel ecc, int version, QRSegmentation segmentation)
    {
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var budget = StructuredAppendPlanner.Capacity(version, ecc);
        var referenceEnds = new List<int>();
        var expected = GreedyCount(text, charset, segmentation, version, budget, referenceEnds);
        await Assert.That(expected).IsBetween(2, StructuredAppendPlanner.MaxSymbols);

        // At the limit: the count, and the same chunk ends the reference walk found.
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget, expected, ends)).IsEqualTo(expected);
        await Assert.That(ends.Take(expected)).IsEquivalentTo(referenceEnds);

        // Above it: unchanged.
        await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget, StructuredAppendPlanner.MaxSymbols, ends)).IsEqualTo(expected);

        // Below it: more than the limit, and nothing more is promised.
        await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget, expected - 1, ends)).IsGreaterThan(expected - 1);
        await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget, 1, ends)).IsGreaterThan(1);
    }

    [Test]
    [Arguments("ascii", QRSegmentation.Single, false)]
    [Arguments("ascii", QRSegmentation.Optimal, false)]
    [Arguments("digits", QRSegmentation.Single, false)]
    [Arguments("digits", QRSegmentation.Optimal, false)]
    [Arguments("transitions", QRSegmentation.Single, false)]
    [Arguments("transitions", QRSegmentation.Optimal, false)]
    [Arguments("latin1", QRSegmentation.Single, false)]
    [Arguments("latin1", QRSegmentation.Optimal, false)]
    [Arguments("mixed", QRSegmentation.Single, false)]
    [Arguments("mixed", QRSegmentation.Optimal, false)]
    [Arguments("japanese", QRSegmentation.Single, false)]
    [Arguments("japanese", QRSegmentation.Optimal, false)]
    [Arguments("japanese", QRSegmentation.Single, true)]
    [Arguments("japanese", QRSegmentation.Optimal, true)]
    [Arguments("emoji", QRSegmentation.Single, false)]
    [Arguments("emoji", QRSegmentation.Optimal, false)]
    [Arguments("emoji", QRSegmentation.Single, true)]
    [Arguments("emoji", QRSegmentation.Optimal, true)]
    [Arguments("digitsThenJapanese", QRSegmentation.Single, false)]
    [Arguments("digitsThenJapanese", QRSegmentation.Optimal, false)]
    [Arguments("digitsThenJapanese", QRSegmentation.Single, true)]
    [Arguments("digitsThenJapanese", QRSegmentation.Optimal, true)]
    // A leading alphanumeric run a split makes cheaper, in a UTF-8 set: the chunk carries no
    // byte order mark while it stays in that run, so the mark must not suppress the plan there.
    [Arguments("alnumDigitsThenJapanese", QRSegmentation.Optimal, true)]
    [Arguments("alnumDigitsThenJapanese", QRSegmentation.Optimal, false)]
    [Arguments("alnumDigitsThenJapanese", QRSegmentation.Single, true)]
    public async Task LongestChunkEnd_MatchesTheReferenceWalk(string kind, QRSegmentation segmentation, bool utf8Bom)
    {
        // The chunk end is found without pricing whole prefixes; the reference extends one
        // character (or pair) at a time and prices each prefix with ChunkBits, the cost the
        // split is defined by. Every budget the answer can turn on is tried: each candidate
        // prefix's cost, and one bit below it.
        var text = Text(kind, 120);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;

        foreach (var version in new[] { 1, 10, 27 })
        {
            foreach (var start in new[] { 0, 3, text.Length / 2 })
            {
                var budgets = new HashSet<int> { 0, StructuredAppendPlanner.HeaderBits };
                var end = start;
                while (end < text.Length)
                {
                    end += char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                    var cost = StructuredAppendPlanner.ChunkBits(text.AsSpan(start, end - start), charset, version, segmentation, utf8Bom);
                    budgets.Add(cost);
                    budgets.Add(cost - 1);
                }

                foreach (var budget in budgets)
                {
                    var expected = ReferenceChunkEnd(text, start, charset, version, segmentation, utf8Bom, budget);

                    await Assert.That(StructuredAppendPlanner.LongestChunkEnd(text, start, charset, version, segmentation, utf8Bom, budget)).IsEqualTo(expected)
                        .Because($"{kind} from {start} at version {version} with budget {budget}");
                }
            }
        }
    }

    [Test]
    [Arguments("ascii", 600)]
    [Arguments("ascii", 2000)]
    [Arguments("digits", 1201)]
    [Arguments("latin1", 329)]
    [Arguments("japanese", 67)]
    [Arguments("emoji", 120)]
    [Arguments("mixed", 600)]
    [Arguments("mixed", 3000)]
    [Arguments("transitions", 900)]
    [Arguments("alnumDigitsThenJapanese", 400)]
    public async Task Budget_IsTheSmallestThatHoldsTheCount(string kind, int length)
    {
        // The balanced split is defined by the budget being minimal, so whatever the search
        // brackets it with has to leave that exact. A floor above the answer would raise the
        // budget and leave a symbol fuller than it has to be, which one bit below catches.
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                if (!StructuredAppendPlanner.TryPlan(text, ecc, charset, TextAnalyzer.Analyze(text, charset).EncodingMode, false, segmentation, 1, 40, ends, out var count, out var version, out var budget))
                    continue;
                if (count == 1)
                    continue;

                var because = $"{kind} {length} in {segmentation} at {ecc}";
                await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget, count, ends)).IsLessThanOrEqualTo(count).Because(because);
                await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, segmentation, version, budget - 1, count, ends)).IsGreaterThan(count).Because($"{because}: one bit below must not hold the count");
            }
        }
    }

    [Test]
    public async Task Plan_EmptyText_IsOneSymbol()
    {
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan("", QREccLevel.M, EciMode.Default, EncodingMode.Byte, false, QRSegmentation.Single, 1, 40, ends, out var count, out _, out _);

        await Assert.That(planned).IsTrue();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Plan_CharacterThatFitsNoSymbol_IsRefused()
    {
        // One emoji under a UTF-8 ECI at version 1-H: 20 + 12 + 4 + 8 + 32 = 76 bits against 72.
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan("🎉", QREccLevel.H, EciMode.Utf8, EncodingMode.Byte, false, QRSegmentation.Single, 1, 1, ends, out _, out _, out _);

        await Assert.That(planned).IsFalse();
    }

    [Test]
    public async Task Plan_MoreThanSixteen_IsRefused()
    {
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan(Text("ascii", 600), QREccLevel.H, EciMode.Default, EncodingMode.Byte, false, QRSegmentation.Single, 1, 1, ends, out _, out _, out _);

        await Assert.That(planned).IsFalse();
    }

    [Test]
    [Arguments("ascii", 301)]
    [Arguments("latin1", 329)]
    [Arguments("japanese", 67)]
    [Arguments("emoji", 120)]
    public async Task Parity_MatchesTheEncodersBytes(string kind, int length)
    {
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var bytes = charset == EciMode.Utf8 ? Encoding.UTF8.GetBytes(text) : Encoding.Latin1.GetBytes(text);
        var expected = (byte)bytes.Aggregate(0, (p, b) => p ^ b);

        await Assert.That(StructuredAppendPlanner.Parity(text, charset, false)).IsEqualTo(expected);
        await Assert.That(StructuredAppendPlanner.Parity(text, charset, true)).IsEqualTo((byte)(expected ^ 0xEF ^ 0xBB ^ 0xBF));
    }

    [Test]
    public async Task Parity_LoneSurrogate_IsTheReplacementCharacter()
    {
        var text = "a\uD83Cb";

        await Assert.That(StructuredAppendPlanner.Parity(text, EciMode.Utf8, false)).IsEqualTo((byte)Encoding.UTF8.GetBytes(text).Aggregate(0, (p, b) => p ^ b));
    }

    /// <summary>Reference walk: extend each chunk one character at a time while it fits; surrogate pairs move together.</summary>
    private static int GreedyCount(string text, EciMode charset, QRSegmentation segmentation, int version, int budget, List<int>? ends = null)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
            var end = ReferenceChunkEnd(text, start, charset, segmentation: segmentation, version: version, utf8Bom: false, budget: budget);
            if (end < 0)
                return int.MaxValue;
            count++;
            ends?.Add(end);
            start = end;
        }
        return count;
    }

    /// <summary>Reference chunk end: extend one character (or pair) at a time while <see cref="StructuredAppendPlanner.ChunkBits"/> fits; -1 when the first does not.</summary>
    private static int ReferenceChunkEnd(string text, int start, EciMode charset, int version, QRSegmentation segmentation, bool utf8Bom, int budget)
    {
        var end = start;
        while (end < text.Length)
        {
            var next = end + (char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1);
            if (StructuredAppendPlanner.ChunkBits(text.AsSpan(start, next - start), charset, version, segmentation, utf8Bom) > budget)
                break;
            end = next;
        }
        return end == start ? -1 : end;
    }

    private static string Text(string kind, int length)
    {
        // Content whose density changes along the text: an even cut by characters is far from
        // balanced on it, so nothing that assumes periodic content survives these.
        if (kind == "digitsThenMixed")
            return Text("digits", length / 2) + Text("mixed", length - length / 2);
        if (kind == "proseThenDigits")
            return Text("ascii", length / 2) + Text("digits", length - length / 2);
        if (kind == "randomRuns")
        {
            // Runs of one class each (digits, upper-case alphanumerics, lower-case and punctuation), 1 to 39 long.
            const string alphabet = "0123456789012345678901234567ABCDEF abcdefghijklmnop.,-:";
            var random = new Random(20260917);
            var runs = new StringBuilder(length + 40);
            while (runs.Length < length)
            {
                var run = random.Next(1, 40);
                var from = random.Next(3) switch { 0 => 0, 1 => 28, _ => 35 };
                var span = from == 0 ? 28 : from == 28 ? 7 : alphabet.Length - 35;
                for (var i = 0; i < run; i++)
                    runs.Append(alphabet[from + random.Next(span)]);
            }
            return runs.ToString(0, length);
        }

        var unit = kind switch
        {
            "emojiDigits" => "🎉1234",
            "digits" => "0123456789",
            "latin1" => "Crème brûlée à la carte, jalapeño, naïve café. ",
            "japanese" => "こんにちは世界、QRコードの分割テストです。",
            "emoji" => "🎉🎊🎈",
            "mixed" => "order 20260915 item 0000123456 qty 42 ",
            "transitions" => "12345ABCDE abcde 67890 FGHIJ fghij ",
            "digitsThenJapanese" => "1234567こんにちは ",
            "alnumDigitsThenJapanese" => "AB01234567890123456789012345678901234567890123456789こんにちは ",
            _ => "The quick brown fox jumps over the lazy dog. ",
        };
        var sb = new StringBuilder(length);
        while (sb.Length < length)
            sb.Append(unit);
        var text = sb.ToString(0, length);
        // Never end on a high surrogate: the encoder would write U+FFFD for it.
        return char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }
}
