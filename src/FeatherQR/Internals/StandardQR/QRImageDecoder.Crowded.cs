using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>Confirmed candidates from which a list is crowded: two symbols' worth of finders, of which a triple can span symbols whole or cut.</summary>
    private const int CrowdedConfirmed = 6;

    /// <summary>The most compact well-shaped triples kept of a crowded list.</summary>
    private const int MaxCrowdedTriples = 256;

    /// <summary>The selection score past which a triple is not kept: a symbol's own triple under 20 % keystone scores up to 0.88, one spanning symbols as little as 0.</summary>
    private const float MaxCrowdedScore = 0.5f;

    /// <summary>How many of the kept triples have their timing patterns read, and how many of those that read are decoded.</summary>
    private const int MaxCrowdedGates = 64;

    private const int MaxCrowdedDecodes = 4;

    /// <summary>Whether a list holds two symbols' worth of confirmed candidates, the strided scan's counts included: there the confirmation only filters, and the stage's order and gate do the rest.</summary>
    private static bool IsCrowded(ReadOnlySpan<FinderPattern> candidates)
    {
        var confirmed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Count >= 2)
                confirmed++;
        }
        return confirmed >= CrowdedConfirmed;
    }

    private struct CompactTriple
    {
        public float Longest;
        public byte I;
        public byte J;
        public byte K;
    }

    /// <summary>
    /// Decodes a symbol among many, after the selected triple and the alternatives failed on a list of at least <see cref="CrowdedConfirmed"/> confirmed candidates.
    /// </summary>
    /// <remarks>
    /// Where symbols of one size stand side by side, the three finders meeting at a corner of three symbols form a right isosceles triangle as well shaped as a symbol's own, as confirmed, and smaller: no shape or count tells them apart, and the selection and the alternatives, ordered by shape and confirmation, pick such triples among hundreds.
    /// What a symbol's own triple has is the two timing patterns between its finders, so the well-shaped triples are walked most compact first and each is asked whether its timing patterns read, the gate an alternative already passes before it costs a decode; the first two that do are decoded.
    /// Most compact first because the gate is paid per triple and a symbol's own triple is among the smallest of the well shaped: the crossroads triples that are smaller still fail the gate at once.
    /// </remarks>
    private static DecodeStatus DecodeCrowded<TAttempt>(ref TAttempt attempt, in ImageView image, ReadOnlySpan<FinderPattern> candidates, ReadOnlySpan<FinderPattern> selected, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        where TAttempt : struct, ICornerAttempt
    {
        charsWritten = 0;
        info = NotDetectedInfo;

        Span<FinderPattern> confirmed = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var confirmedCount = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Count >= 2)
                confirmed[confirmedCount++] = candidate;
        }
        if (confirmedCount < CrowdedConfirmed)
            return DecodeStatus.NotDetected;

        // The well-shaped triples, the most compact kept, in order of their longest side
        Span<CompactTriple> triples = stackalloc CompactTriple[MaxCrowdedTriples];
        var count = 0;
        for (var i = 0; i < confirmedCount; i++)
        {
            for (var j = i + 1; j < confirmedCount; j++)
            {
                var ab = DistanceSquared(confirmed[i], confirmed[j]);
                for (var k = j + 1; k < confirmedCount; k++)
                {
                    if (FinderPatternFinder.TripleScore(confirmed[i], confirmed[j], confirmed[k]) > MaxCrowdedScore)
                        continue;
                    var longest = Math.Max(ab, Math.Max(DistanceSquared(confirmed[i], confirmed[k]), DistanceSquared(confirmed[j], confirmed[k])));
                    if (count == triples.Length && longest >= triples[count - 1].Longest)
                        continue;
                    var at = count < triples.Length ? count : count - 1;
                    while (at > 0 && triples[at - 1].Longest > longest)
                    {
                        triples[at] = triples[at - 1];
                        at--;
                    }
                    triples[at] = new CompactTriple { Longest = longest, I = (byte)i, J = (byte)j, K = (byte)k };
                    if (count < triples.Length)
                        count++;
                }
            }
        }

        var result = NewResult();
        var decoded = 0;
        Span<FinderPattern> triple = stackalloc FinderPattern[3];
        for (var t = 0; t < count && t < MaxCrowdedGates && decoded < MaxCrowdedDecodes; t++)
        {
            triple[0] = confirmed[triples[t].I];
            triple[1] = confirmed[triples[t].J];
            triple[2] = confirmed[triples[t].K];
            if (SameTriple(triple, selected) || !TryCornerByTimingPatterns(ref attempt, image, triple, out var topLeft, out var topRight, out var bottomLeft))
                continue;
            decoded++;
            var status = attempt.Decode(image, topLeft, topRight, bottomLeft, ref modules, destination, out var attemptCharsWritten, out var attemptInfo);
            if (decoded == 1 ? result.Main(status, attemptCharsWritten, attemptInfo) : result.Other(status, attemptCharsWritten, attemptInfo))
                break;
        }
        return result.Report(out charsWritten, out info);
    }
}
