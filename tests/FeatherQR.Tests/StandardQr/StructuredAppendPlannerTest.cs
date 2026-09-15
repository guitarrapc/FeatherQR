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
    public async Task Plan_IsFewestSymbolsAtTheSmallestVersionWithTheSmallestBudget(string kind, int length, QREccLevel ecc, int minVersion, int maxVersion, QRSegmentation segmentation)
    {
        var text = Text(kind, length);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan(text, ecc, charset, false, segmentation, minVersion, maxVersion, ends, out var count, out var version, out var budget);

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
    public async Task Plan_EmptyText_IsOneSymbol()
    {
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan("", QREccLevel.M, EciMode.Default, false, QRSegmentation.Single, 1, 40, ends, out var count, out _, out _);

        await Assert.That(planned).IsTrue();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Plan_CharacterThatFitsNoSymbol_IsRefused()
    {
        // One emoji under a UTF-8 ECI at version 1-H: 20 + 12 + 4 + 8 + 32 = 76 bits against 72.
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan("🎉", QREccLevel.H, EciMode.Utf8, false, QRSegmentation.Single, 1, 1, ends, out _, out _, out _);

        await Assert.That(planned).IsFalse();
    }

    [Test]
    public async Task Plan_MoreThanSixteen_IsRefused()
    {
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        var planned = StructuredAppendPlanner.TryPlan(Text("ascii", 600), QREccLevel.H, EciMode.Default, false, QRSegmentation.Single, 1, 1, ends, out _, out _, out _);

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
    private static int GreedyCount(string text, EciMode charset, QRSegmentation segmentation, int version, int budget)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length)
            {
                var next = end + (char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1);
                if (StructuredAppendPlanner.ChunkBits(text.AsSpan(start, next - start), charset, version, segmentation, false) > budget)
                    break;
                end = next;
            }
            if (end == start)
                return int.MaxValue;
            count++;
            start = end;
        }
        return count;
    }

    private static string Text(string kind, int length)
    {
        var unit = kind switch
        {
            "digits" => "0123456789",
            "latin1" => "Crème brûlée à la carte, jalapeño, naïve café. ",
            "japanese" => "こんにちは世界、QRコードの分割テストです。",
            "emoji" => "🎉🎊🎈",
            "mixed" => "order 20260915 item 0000123456 qty 42 ",
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
