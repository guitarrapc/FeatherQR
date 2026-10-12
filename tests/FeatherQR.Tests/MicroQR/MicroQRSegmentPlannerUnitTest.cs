using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Unit tests for <see cref="MicroQRSegmentPlanner"/>: the dynamic program is held to
/// an independent brute-force optimum that respects each version's mode set (M2 has
/// no Byte mode), the plan it reconstructs is held to its own cost model, and the
/// <see cref="MicroQRSegmentPlanner.MaxPlannableChars"/> bound is pinned with its
/// margin so a capacity-table change cannot silently invalidate it.
/// </summary>
public class MicroQRSegmentPlannerUnitTest
{
    [Test]
    public async Task MaxPlannableChars_IsTheM4Optimum_WithItsMargin()
    {
        // 35 digits: 11 groups x 10 bits + a 7-bit pair tail + the 9-bit M4 header
        // = 126 of M4-L's 128 bits. One more digit completes a group: 129 > 128.
        var capacityBits = MicroQRConstants.GetDataBitCapacity(MicroQRVersion.M4, MicroQREccLevel.L);
        var atLimit = new string('1', MicroQRSegmentPlanner.MaxPlannableChars);
        await Assert.That(MicroQRSegmentPlanner.MinimumPayloadBits(atLimit, EciMode.Default, MicroQRVersion.M4)).IsEqualTo(capacityBits - 2);

        var overLimit = new string('1', MicroQRSegmentPlanner.MaxPlannableChars + 1);
        await Assert.That(MicroQRSegmentPlanner.MinimumPayloadBits(overLimit, EciMode.Default, MicroQRVersion.M4)).IsEqualTo(capacityBits + 1);
    }

    /// <summary>
    /// Short contents covering the mode-availability classes: alnum-capable content
    /// plannable at M2, byte-needing content plannable only at M3/M4, every charset,
    /// and shapes where switching wins and where it must not.
    /// </summary>
    public static IEnumerable<(string Content, EciMode Charset)> BruteForceCorpus() =>
    [
        ("1", EciMode.Default),
        ("A", EciMode.Default),
        ("a", EciMode.Default),
        ("A1234567", EciMode.Default),
        ("AB123456", EciMode.Default),
        ("ab123456", EciMode.Default),
        ("a1B2c3", EciMode.Default),
        ("A 12:34/", EciMode.Default),
        ("é123456", EciMode.Iso8859_1),
        ("あ12345", EciMode.Utf8),
        ("😀1234", EciMode.Utf8),
        ("\uD83D123", EciMode.Utf8),
        ("12\uDE00A", EciMode.Utf8),
    ];

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task MinimumPayloadBits_MatchesAnExhaustiveModeAssignment(string content, EciMode charset)
    {
        foreach (var version in new[] { MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var allowByte = MicroQRConstants.IsModeSupported(version, EncodingMode.Byte);
            var expected = BruteForceMinimumBits(content, charset, version, allowByte);
            var actual = MicroQRSegmentPlanner.MinimumPayloadBits(content, charset, version);

            if (expected < 0)
            {
                // No assignment covers the content at this version's mode set.
                await Assert.That(actual >= ModeSegmenter.Unreachable).IsTrue().Because($"content=\"{content}\", version={version} should be unreachable");
                continue;
            }

            await Assert.That(actual).IsEqualTo(expected).Because($"content=\"{content}\", version={version}");
        }
    }

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task TrivialLowerBound_NeverExceedsTheOptimum(string content, EciMode charset)
    {
        // The scan screens each candidate with cheapest-rate sixths plus the
        // version's cheapest header (mode indicator + narrowest count indicator =
        // 2 x version bits). A sound lower bound must sit at or below the optimum.
        foreach (var version in new[] { MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var optimum = MicroQRSegmentPlanner.MinimumPayloadBits(content, charset, version);
            if (optimum >= 1 << 28)
                continue; // no plan exists at this version's mode set

            var bound = (ModeSegmenter.CheapestSixths(content, charset) + 5) / 6 + 2 * (int)version;
            await Assert.That(bound).IsLessThanOrEqualTo(optimum).Because($"content=\"{content}\", version={version}");
        }
    }

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task TryBuildPlan_CostsExactlyWhatTheDynamicProgramComputed(string content, EciMode charset)
    {
        foreach (var version in new[] { MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var segments = new ModeSegment[content.Length];
            if (!MicroQRSegmentPlanner.TryBuildPlan(content, charset, version, MicroQREccLevel.L, segments, out var count))
                continue; // the plan legitimately does not fit this version (or its mode set)

            var planned = MicroQRSegmentPlanner.MeasurePlan(version, segments.AsSpan(0, count));
            var optimum = MicroQRSegmentPlanner.MinimumPayloadBits(content, charset, version);
            await Assert.That(planned).IsEqualTo(optimum).Because($"content=\"{content}\", version={version}");

            var expectedStart = 0;
            for (var i = 0; i < count; i++)
            {
                await Assert.That((int)segments[i].Start).IsEqualTo(expectedStart);
                await Assert.That((int)segments[i].Length).IsGreaterThan(0);
                expectedStart += segments[i].Length;
            }
            await Assert.That(expectedStart).IsEqualTo(content.Length);
        }
    }

    [Test]
    public async Task TryBuildPlan_DigitIslandBetweenByteRuns_ProducesThreeRuns()
    {
        // Byte(2) + Numeric(5) + Byte(2) at M4: (8+16) + (9+17) + (8+16) = 74 bits
        // against 80 all-Byte. The island's 23-bit saving beats the 17 extra header
        // bits, so the optimal plan must switch modes twice.
        var content = "ab12345cd";
        var segments = new ModeSegment[content.Length];
        await Assert.That(MicroQRSegmentPlanner.TryBuildPlan(content, EciMode.Default, MicroQRVersion.M4, MicroQREccLevel.L, segments, out var count)).IsTrue();

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(segments[0].Mode).IsEqualTo(EncodingMode.Byte);
        await Assert.That(segments[1].Mode).IsEqualTo(EncodingMode.Numeric);
        await Assert.That((int)segments[1].Length).IsEqualTo(5);
        await Assert.That(segments[2].Mode).IsEqualTo(EncodingMode.Byte);
    }

    /// <summary>
    /// Independent optimum: every assignment of a mode to every character (3^n),
    /// assignments using Byte discarded when the version lacks it, consecutive
    /// same-mode characters merged into runs, each run priced from the ISO/IEC 18004
    /// Micro QR widths written out here rather than taken from the planner. Returns
    /// -1 when no valid assignment exists.
    /// </summary>
    private static int BruteForceMinimumBits(string content, EciMode charset, MicroQRVersion version, bool allowByte)
    {
        var n = content.Length;
        var best = int.MaxValue;
        var modeBits = (int)version - 1;
        var cciNumeric = (int)version + 2;
        var cciOther = (int)version + 1;

        var assignment = new int[n];
        var total = (int)Math.Pow(3, n);
        for (var mask = 0; mask < total; mask++)
        {
            var m = mask;
            var valid = true;
            for (var i = 0; i < n; i++)
            {
                assignment[i] = m % 3;
                m /= 3;
                var c = content[i];
                if (assignment[i] == 0 && !(c >= '0' && c <= '9'))
                    valid = false;
                if (assignment[i] == 1 && !IsAlphanumeric(c))
                    valid = false;
                if (assignment[i] == 2 && !allowByte)
                    valid = false;
            }
            if (!valid)
                continue;

            var bits = 0;
            var start = 0;
            for (var i = 1; i <= n; i++)
            {
                if (i == n || assignment[i] != assignment[start])
                {
                    var run = content.Substring(start, i - start);
                    bits += assignment[start] switch
                    {
                        0 => modeBits + cciNumeric + run.Length / 3 * 10 + (run.Length % 3) switch { 2 => 7, 1 => 4, _ => 0 },
                        1 => modeBits + cciOther + run.Length / 2 * 11 + run.Length % 2 * 6,
                        _ => modeBits + cciOther + 8 * (charset == EciMode.Utf8 ? System.Text.Encoding.UTF8.GetByteCount(run) : run.Length),
                    };
                    start = i;
                }
            }
            best = Math.Min(best, bits);
        }
        return best == int.MaxValue ? -1 : best;
    }

    private static bool IsAlphanumeric(char c)
        => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || " $%*+-./:".IndexOf(c) >= 0;
}
