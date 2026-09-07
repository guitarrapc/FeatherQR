using System.Diagnostics;
using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Mixed-mode segmentation for <see cref="MicroQRSegmentation.Optimal"/>: the split of the content into Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a given version, and the version fit that follows from it.
/// </summary>
/// <remarks>
/// The cost model and reconstruction are <see cref="ModeSegmenter"/>, shared with the Standard QR and rMQR planners; what lives here is Micro QR's version scan: at most three candidates below the single-mode fit, each screened by the trivial per-character lower bound before a cost run (a Micro QR encode is so cheap that even one wasted dynamic-program pass doubles it).
/// What Micro QR adds is per-version mode availability: M1 is Numeric-only and M2 has no Byte mode, so candidates whose mode set cannot carry the content are skipped, and the missing transitions are disabled in the dynamic program.
/// </remarks>
internal static class MicroQRSegmentPlanner
{
    /// <summary>
    /// Longest content any Micro QR symbol can hold, in characters (35 digits at M4-L: 11 groups × 10 bits + 7 + the 9-bit header = 126 of 128 bits).
    /// No mixed plan can beat it, and every plan buffer is sized by it (35 runs of one character each is the theoretical worst case, 280 stack bytes).
    /// </summary>
    /// <remarks>
    /// A rejection rule, not only a work cap: a mixed plan can encode content no single mode holds, so this is what declares longer content impossible.
    /// The margin is one whole group (36 digits cost 129 against 128), so re-derive it rather than nudge it if the capacity tables change.
    /// Pinned by <c>MicroQRSegmentPlannerUnitTest</c>.
    /// </remarks>
    public const int MaxPlannableChars = 35;

    /// <summary>
    /// Version fit for mixed-mode segmentation, restricted to <paramref name="range"/>.
    /// Returns the version to encode at and whether a mixed-mode plan is what makes it fit; when <paramref name="useSegments"/> is false the caller emits the ordinary single-mode stream, bit-identical to <see cref="MicroQRSegmentation.Single"/>.
    /// <c>false</c> means the content fits neither one mode nor a mixed plan in the range; the caller owns the error.
    /// Throws exactly what the single-mode selector throws for argument errors.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, MicroQREccLevel eccLevel, MicroQRVersionRange range, out MicroQRVersion selected, out bool useSegments)
    {
        useSegments = false;

        // Ceiling, and the argument validation: the single-mode selector throws the
        // same ECC / range-contradiction errors Single throws, before any planning.
        var hasSingle = MicroQRCodeGenerator.TrySelectVersionInRange(in analysis, eccLevel, range, out var single);

        // All-Numeric content is already at the optimum: splitting a Numeric run
        // never lowers its payload and every extra run adds a header.
        if (analysis.EncodingMode == EncodingMode.Numeric)
        {
            selected = single;
            return hasSingle;
        }

        if (text.Length is 0 or > MaxPlannableChars)
        {
            selected = single;
            return hasSingle;
        }

        // Strictly below the single-mode fit: at that version the single-mode stream
        // already fits, and emitting it unchanged is the blast-radius bound the
        // feature is designed around. When no single mode fits, scan the whole range.
        var top = hasSingle ? (MicroQRVersion)Math.Min((int)single - 1, (int)range.Max) : range.Max;
        if (top < range.Min)
        {
            selected = single;
            return hasSingle;
        }

        // One O(n) pass pricing each character at the cheapest rate any mode could
        // give it: a lower bound on any plan at any version, so it may only reject.
        // It is what keeps Optimal roughly free on content no split can shrink.
        var cheapestSixths = ModeSegmenter.CheapestSixths(text, analysis.EciMode);

        for (var candidate = range.Min; candidate <= top; candidate++)
        {
            // The content's single mode is the widest mode any of its characters
            // needs, so a candidate without it cannot carry any plan of this content.
            if (!MicroQRConstants.IsValidCombination(candidate, eccLevel) || !MicroQRConstants.IsModeSupported(candidate, analysis.EncodingMode))
                continue;

            // Cheapest header at this version: the (version - 1)-bit mode indicator
            // plus the narrowest count indicator, version + 1 bits (Alphanumeric/Byte).
            var trivialBits = (cheapestSixths + 5) / 6 + 2 * (int)candidate;
            if (MicroQRConstants.GetDataBitCapacity(candidate, eccLevel) < trivialBits)
                continue; // no split of this content could fit, whatever the plan

            var cost = PlanCost(text, analysis.EciMode, candidate, default, out _);
            Debug.Assert(cost < ModeSegmenter.Unreachable, "the mode pre-filter admits only candidates whose mode set covers the content");
            if (cost < ModeSegmenter.Unreachable && cost <= MicroQRConstants.GetDataBitCapacity(candidate, eccLevel))
            {
                useSegments = true;
                selected = candidate;
                return true;
            }
        }

        selected = single;
        return hasSingle;
    }

    /// <summary>
    /// Builds the minimal-cost plan for <paramref name="version"/> into <paramref name="segments"/>.
    /// Returns false when the content is unplannable at this version, the plan needs more runs than the caller lent room for, the plan would be misread on decode (a relocated byte order mark, or a Latin-1 run the charset heuristic reads as UTF-8), or the exact re-costed stream would not fit; the caller answers all four by falling back to the single-mode stream.
    /// </summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, MicroQRVersion version, MicroQREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        Span<byte> parents = stackalloc byte[MaxPlannableChars * ModeSegmenter.StateCount];
        var window = parents.Slice(0, text.Length * ModeSegmenter.StateCount);
        var plannedBits = PlanCost(text, charset, version, window, out var finalState);
        if (plannedBits >= ModeSegmenter.Unreachable)
            return false; // a character no mode of this version encodes

        if (!ModeSegmenter.Reconstruct(text, window, finalState, segments, out segmentCount))
        {
            segmentCount = 0;
            return false;
        }

        // A plan the byte-segment decoder would misread is worse than no plan.
        // Micro QR has no ECI, so the decoder resolves each Byte run's charset
        // heuristically; two shapes lose to that once a split isolates them, and
        // both fall back to the single-mode stream:
        //  - a relocated mid-content U+FEFF at a run start is consumed as a BOM;
        //  - a Latin-1 run whose narrowed bytes read as UTF-8 (the disambiguating
        //    invalid bytes now live in another run) decodes as different text.
        if (ModeSegmenter.HasBomRelocatedToARunStart(text, segments.Slice(0, segmentCount))
            || (charset == EciMode.Iso8859_1 && HasLatin1RunTheHeuristicReadsAsUtf8(text, segments.Slice(0, segmentCount))))
        {
            segmentCount = 0;
            return false;
        }

        ModeSegmenter.FillUnitCounts(text, charset, segments.Slice(0, segmentCount));

        // Re-cost the reconstructed plan from the byte counts the encoder will
        // actually emit; disagreement is a cost-model bug and rejects the plan
        // rather than becoming a stream that overruns the data codewords.
        var measuredBits = MeasurePlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        if (measuredBits != plannedBits || measuredBits > MicroQRConstants.GetDataBitCapacity(version, eccLevel))
        {
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>Exact bit cost of a plan: per run, mode indicator + count indicator + payload.</summary>
    public static int MeasurePlan(MicroQRVersion version, ReadOnlySpan<ModeSegment> segments)
    {
        var total = 0;
        foreach (var segment in segments)
        {
            var mode = segment.Mode;
            total += MicroQRConstants.GetModeIndicatorLength(version) + MicroQRConstants.GetCountIndicatorLength(version, mode) + ModeSegmenter.PayloadBits(mode, segment.UnitCount);
        }
        return total;
    }

    /// <summary>
    /// Named entry point for <c>MicroQRSegmentPlannerUnitTest</c>: the minimal payload bits at <paramref name="version"/>'s widths and mode set, or a value at or above <see cref="ModeSegmenter.Unreachable"/> when the content needs a mode the version lacks.
    /// </summary>
    public static int MinimumPayloadBits(ReadOnlySpan<char> text, EciMode charset, MicroQRVersion version)
        => PlanCost(text, charset, version, default, out _);

    /// <summary>
    /// Whether any Byte run's narrowed Latin-1 bytes would be read as UTF-8 by the decoder's unspecified-charset resolution (Micro QR has no ECI to pin it).
    /// Pure-ASCII runs are exempt: they decode identically either way.
    /// </summary>
    private static bool HasLatin1RunTheHeuristicReadsAsUtf8(ReadOnlySpan<char> text, ReadOnlySpan<ModeSegment> segments)
    {
        Span<byte> bytes = stackalloc byte[MaxPlannableChars];
        foreach (var segment in segments)
        {
            if (segment.ModeIndex != 2)
                continue;

            var chars = text.Slice(segment.Start, segment.Length);
            var nonAscii = false;
            for (var i = 0; i < chars.Length; i++)
            {
                bytes[i] = (byte)chars[i]; // Latin-1 narrows one byte per char (validated upstream)
                nonAscii |= chars[i] > 0x7F;
            }

            if (nonAscii && SegmentDecoders.ResolvesToUtf8WhenUnspecified(bytes.Slice(0, chars.Length)))
                return true;
        }
        return false;
    }

    /// <summary>The shared dynamic program at this version's widths and mode set.</summary>
    private static int PlanCost(ReadOnlySpan<char> text, EciMode charset, MicroQRVersion version, Span<byte> parents, out int finalState)
        => ModeSegmenter.ComputeCosts(
            text, charset,
            MicroQRConstants.GetModeIndicatorLength(version),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte),
            parents, out finalState,
            allowAlnum: MicroQRConstants.IsModeSupported(version, EncodingMode.Alphanumeric),
            allowByte: MicroQRConstants.IsModeSupported(version, EncodingMode.Byte));
}
