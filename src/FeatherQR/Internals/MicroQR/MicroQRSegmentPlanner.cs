using System.Diagnostics;
using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Mixed-mode segmentation for <see cref="MicroQRSegmentation.Optimal"/>: the split of the content into Numeric / Alphanumeric / Byte runs (and Kanji runs, for a Kanji-eligible text) whose total bit cost is minimal for a given version, and the version fit that follows from it.
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
    /// When <paramref name="kanjiPlan"/> is true the plan is the Kanji plan of a Kanji-eligible text, built by <see cref="TryBuildKanjiPlan(ReadOnlySpan{char}, MicroQRVersion, MicroQREccLevel, Span{ModeSegment}, out int)"/> and written under <see cref="EciMode.Default"/>.
    /// <c>false</c> means the content fits neither one mode nor a mixed plan in the range; the caller owns the error.
    /// Throws exactly what the single-mode selector throws for argument errors.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, MicroQREccLevel eccLevel, MicroQRVersionRange range, out MicroQRVersion selected, out bool useSegments, out bool kanjiPlan)
        => TrySelectVersion(text, in analysis, eccLevel, range, default, out selected, out useSegments, out kanjiPlan, out _, out _);

    /// <summary>
    /// <see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, MicroQREccLevel, MicroQRVersionRange, out MicroQRVersion, out bool, out bool)"/>, keeping the run that priced the Kanji plan it chose.
    /// When <paramref name="kanjiPlan"/> is true, <paramref name="kanjiTable"/> holds that program's predecessors at the selected version's widths, which ended in <paramref name="kanjiFinalState"/> at <paramref name="kanjiPlannedBits"/>, and <see cref="TryBuildKanjiPlan(ReadOnlySpan{char}, MicroQRVersion, MicroQREccLevel, ReadOnlySpan{byte}, int, int, Span{ModeSegment}, out int)"/> builds the plan from it without running the program again.
    /// An empty <paramref name="kanjiTable"/> keeps nothing; otherwise it holds at least <see cref="ModeSegmenter.ParentBytesPerChar"/> bytes a character.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, MicroQREccLevel eccLevel, MicroQRVersionRange range, Span<byte> kanjiTable, out MicroQRVersion selected, out bool useSegments, out bool kanjiPlan, out int kanjiFinalState, out int kanjiPlannedBits)
    {
        useSegments = false;
        kanjiPlan = false;
        kanjiFinalState = 0;
        kanjiPlannedBits = 0;

        // Ceiling, and the argument validation: the single-mode selector throws the
        // same ECC / range-contradiction errors Single throws, before any planning.
        var hasSingle = MicroQRCodeGenerator.TrySelectVersionInRange(in analysis, eccLevel, range, out var single);

        // All-Numeric content is already at the optimum: splitting a Numeric run
        // never lowers its payload and every extra run adds a header. So is Kanji
        // content: the analyser chooses it only when every character has a Kanji cell,
        // so none of them fits another mode more cheaply.
        if (analysis.EncodingMode is EncodingMode.Numeric or EncodingMode.Kanji)
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

        if (analysis.KanjiPlannable)
            return TrySelectVersionKanji(text, in analysis, eccLevel, range.Min, top, single, hasSingle, kanjiTable, out selected, out useSegments, out kanjiPlan, out kanjiFinalState, out kanjiPlannedBits);

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
    /// The version scan for a Kanji-eligible text with ASCII in it (<see cref="TextAnalysisResult.KanjiPlannable"/>), from <paramref name="min"/> to <paramref name="top"/>, below the single-mode (UTF-8) fit.
    /// At each candidate the Kanji plan (Kanji runs beside runs of the ASCII) is taken where it fits, otherwise the UTF-8 plan the seven-state program gives the same text; neither carries an ECI header, since Micro QR has none.
    /// </summary>
    /// <remarks>
    /// The UTF-8 plan is weighed so that Optimal never needs a larger version than it did before Kanji plans. Both need Byte or Kanji mode, so M1 and M2 are skipped as the mode pre-filter skips them for UTF-8 text.
    /// The Kanji screen prices a character with a cell at 13 bits, and a header for every stretch of characters with a cell and every stretch of ASCII (<see cref="KanjiScreenBits(int, int, int, MicroQRVersion)"/>).
    /// At the UTF-8 rate of 24 bits a kana, the screen would reject M3 for 「日本語12345」, whose Kanji plan fits M3-L.
    /// With a <paramref name="kanjiTable"/>, each Kanji cost run keeps its predecessors there, so the one that accepts is the plan's table: the build would run the same program at the same widths.
    /// </remarks>
    private static bool TrySelectVersionKanji(ReadOnlySpan<char> text, in TextAnalysisResult analysis, MicroQREccLevel eccLevel, MicroQRVersion min, MicroQRVersion top, MicroQRVersion single, bool hasSingle, Span<byte> kanjiTable, out MicroQRVersion selected, out bool useSegments, out bool kanjiPlan, out int kanjiFinalState, out int kanjiPlannedBits)
    {
        useSegments = false;
        kanjiPlan = false;
        kanjiFinalState = 0;
        kanjiPlannedBits = 0;
        Debug.Assert(analysis.EncodingMode == EncodingMode.Byte && analysis.EciMode == EciMode.Utf8, "a plannable text's analysis is the UTF-8 one");

        // One pass for both screens.
        var kanjiSixths = ModeSegmenter.CheapestSixthsKanji(text, out var utf8Sixths, out var cellRuns, out var asciiRuns);
        var window = kanjiTable.IsEmpty ? default : kanjiTable.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar);

        for (var candidate = min; candidate <= top; candidate++)
        {
            if (!MicroQRConstants.IsValidCombination(candidate, eccLevel) || !MicroQRConstants.IsModeSupported(candidate, EncodingMode.Kanji))
                continue;

            var capacityBits = MicroQRConstants.GetDataBitCapacity(candidate, eccLevel);
            if (KanjiScreenBits(kanjiSixths, cellRuns, asciiRuns, candidate) <= capacityBits)
            {
                var cost = KanjiPlanCost(text, candidate, window, out var finalState);
                if (cost <= capacityBits)
                {
                    useSegments = true;
                    kanjiPlan = true;
                    kanjiFinalState = finalState;
                    kanjiPlannedBits = cost;
                    selected = candidate;
                    return true;
                }
            }

            if ((utf8Sixths + 5) / 6 + 2 * (int)candidate <= capacityBits && PlanCost(text, EciMode.Utf8, candidate, default, out _) <= capacityBits)
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
    /// <see cref="TryBuildPlan"/> for the Kanji plan <see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, MicroQREccLevel, MicroQRVersionRange, out MicroQRVersion, out bool, out bool)"/> chose: Kanji runs beside runs of the ASCII, which the caller writes under <see cref="EciMode.Default"/>.
    /// </summary>
    public static bool TryBuildKanjiPlan(ReadOnlySpan<char> text, MicroQRVersion version, MicroQREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars || !MicroQRConstants.IsModeSupported(version, EncodingMode.Kanji))
            return false;

        Span<byte> parents = stackalloc byte[MaxPlannableChars * ModeSegmenter.ParentBytesPerChar];
        var window = parents.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar);
        var plannedBits = KanjiPlanCost(text, version, window, out var finalState);
        return TryAcceptKanjiPlan(text, version, eccLevel, window, finalState, plannedBits, segments, out segmentCount);
    }

    /// <summary>
    /// <see cref="TryBuildKanjiPlan(ReadOnlySpan{char}, MicroQRVersion, MicroQREccLevel, Span{ModeSegment}, out int)"/> from the table the version scan kept for the plan it chose (<see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, MicroQREccLevel, MicroQRVersionRange, Span{byte}, out MicroQRVersion, out bool, out bool, out int, out int)"/>), without running the program again.
    /// </summary>
    public static bool TryBuildKanjiPlan(ReadOnlySpan<char> text, MicroQRVersion version, MicroQREccLevel eccLevel, ReadOnlySpan<byte> table, int finalState, int plannedBits, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars || !MicroQRConstants.IsModeSupported(version, EncodingMode.Kanji))
            return false;

        return TryAcceptKanjiPlan(text, version, eccLevel, table.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar), finalState, plannedBits, segments, out segmentCount);
    }

    /// <summary>The Kanji plan the program's table holds, walked back, priced, and held to what the program computed and to the version's capacity.</summary>
    private static bool TryAcceptKanjiPlan(ReadOnlySpan<char> text, MicroQRVersion version, MicroQREccLevel eccLevel, ReadOnlySpan<byte> table, int finalState, int plannedBits, Span<ModeSegment> segments, out int segmentCount)
    {
        if (!ModeSegmenter.Reconstruct(text, table, finalState, segments, out segmentCount))
        {
            segmentCount = 0;
            return false;
        }

        var measuredBits = PriceKanjiPlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        if (measuredBits != plannedBits || measuredBits > MicroQRConstants.GetDataBitCapacity(version, eccLevel))
        {
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fills each run of a Kanji plan with the value its count indicator carries and returns what the plan measures, in one pass over the runs: <see cref="ModeSegmenter.FillUnitCounts"/> and <see cref="MeasurePlan"/> together.
    /// Every run's value is its length, since a Kanji plan's Byte runs hold only ASCII, one byte a character. M3 and M4 only, where Kanji mode exists.
    /// </summary>
    private static int PriceKanjiPlan(MicroQRVersion version, Span<ModeSegment> segments)
    {
        var modeIndicator = MicroQRConstants.GetModeIndicatorLength(version);
        var numericHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var alnumHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var byteHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var kanjiHeader = modeIndicator + MicroQRConstants.GetKanjiCountIndicatorLength(version);
        var total = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var header = segment.ModeIndex switch { 0 => numericHeader, 1 => alnumHeader, 2 => byteHeader, _ => kanjiHeader };
            total += header + ModeSegmenter.PayloadBitsOfIndex(segment.ModeIndex, segment.Length);
            segments[i] = new ModeSegment(segment.ModeIndex, segment.Start, segment.Length, segment.Length);
        }
        return total;
    }

    /// <summary>
    /// Builds the minimal-cost plan for <paramref name="version"/> into <paramref name="segments"/>.
    /// Returns false when the content is unplannable at this version, the plan needs more runs than the caller lent room for, the plan would be misread on decode (a Latin-1 run the charset heuristic reads as UTF-8 or Shift_JIS; a Byte run opened at a mid-content U+FEFF, which the program does not build), or the exact re-costed stream would not fit; the caller answers all four by falling back to the single-mode stream.
    /// </summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, MicroQRVersion version, MicroQREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        Span<byte> parents = stackalloc byte[MaxPlannableChars * ModeSegmenter.ParentBytesPerChar];
        var window = parents.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar);
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
        //  - a mid-content U+FEFF at a run start is consumed as a BOM (the program
        //    builds no such plan under UTF-8; this refuses a model that disagreed);
        //  - a Latin-1 run whose narrowed bytes read as UTF-8 or Shift_JIS (the
        //    disambiguating bytes now live in another run) decodes as different text.
        if (ModeSegmenter.HasBomRelocatedToARunStart(text, segments.Slice(0, segmentCount))
            || (charset == EciMode.Iso8859_1 && HasLatin1RunTheHeuristicReadsOtherwise(text, segments.Slice(0, segmentCount))))
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
    /// <remarks>
    /// The headers are looked up once for the version rather than per run, as rMQR's are: the plan is measured twice (when built and before it is written).
    /// Kanji's is taken only from M3, the first version that has the mode; below it a plan holds no Kanji run.
    /// </remarks>
    public static int MeasurePlan(MicroQRVersion version, ReadOnlySpan<ModeSegment> segments)
    {
        var modeIndicator = MicroQRConstants.GetModeIndicatorLength(version);
        var numericHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var alnumHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var byteHeader = modeIndicator + MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var kanjiHeader = version >= MicroQRVersion.M3 ? modeIndicator + MicroQRConstants.GetKanjiCountIndicatorLength(version) : 0;
        var total = 0;
        foreach (var segment in segments)
        {
            var header = segment.ModeIndex switch { 0 => numericHeader, 1 => alnumHeader, 2 => byteHeader, _ => kanjiHeader };
            total += header + ModeSegmenter.PayloadBitsOfIndex(segment.ModeIndex, segment.UnitCount);
        }
        return total;
    }

    /// <summary>
    /// Named entry point for <c>MicroQRSegmentPlannerUnitTest</c>: the minimal payload bits at <paramref name="version"/>'s widths and mode set, or a value at or above <see cref="ModeSegmenter.Unreachable"/> when the content needs a mode the version lacks.
    /// </summary>
    public static int MinimumPayloadBits(ReadOnlySpan<char> text, EciMode charset, MicroQRVersion version)
        => PlanCost(text, charset, version, default, out _);

    /// <summary>
    /// Whether any Byte run's narrowed Latin-1 bytes would be read as UTF-8 or Shift_JIS by the decoder's unspecified-charset resolution (Micro QR has no ECI to pin it).
    /// Pure-ASCII runs are exempt: they decode identically either way.
    /// </summary>
    private static bool HasLatin1RunTheHeuristicReadsOtherwise(ReadOnlySpan<char> text, ReadOnlySpan<ModeSegment> segments)
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

            if (nonAscii && !SegmentDecoders.ResolvesToIso8859_1WhenUnspecified(bytes.Slice(0, chars.Length)))
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

    /// <summary>
    /// Named entry point for <c>KanjiPlanBoundsTest</c>: the Kanji plan's screen at <paramref name="version"/> (M3 or M4), in bits.
    /// </summary>
    public static int KanjiScreenBits(ReadOnlySpan<char> text, MicroQRVersion version)
        => KanjiScreenBits(ModeSegmenter.CheapestSixthsKanji(text, out _, out var cellRuns, out var asciiRuns), cellRuns, asciiRuns, version);

    /// <summary>
    /// Each character at its cheapest rate, a character with a cell at 13 bits, and a header for every stretch of either kind, since no run crosses between them: (version - 1) + version bits for a stretch of characters with a cell, (version - 1) + (version + 1) for a stretch of ASCII.
    /// </summary>
    private static int KanjiScreenBits(int sixths, int cellRuns, int asciiRuns, MicroQRVersion version)
        => (sixths + 5) / 6 + cellRuns * (2 * (int)version - 1) + asciiRuns * (2 * (int)version);

    /// <summary>The Kanji plan's program at this version's widths; M3 and M4 only, where every mode exists.</summary>
    private static int KanjiPlanCost(ReadOnlySpan<char> text, MicroQRVersion version, Span<byte> parents, out int finalState)
        => ModeSegmenter.ComputeCostsKanji(
            text,
            MicroQRConstants.GetModeIndicatorLength(version),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
            MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte),
            MicroQRConstants.GetKanjiCountIndicatorLength(version),
            parents, out finalState);
}
