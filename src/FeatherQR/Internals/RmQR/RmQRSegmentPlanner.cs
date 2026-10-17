using System.Buffers;
using System.Diagnostics;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// Mixed-mode segmentation for <see cref="RmQRSegmentation.Optimal"/>: the split of the content into Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a given version, and the version fit that follows from it.
/// </summary>
/// <remarks>
/// The cost model and reconstruction are <see cref="ModeSegmenter"/>, shared with the Standard QR and Micro QR planners. What lives here is rMQR's version scan.
/// Unlike Standard QR's banded widths, the 32 rMQR versions carry 13 distinct count indicator width triples across a strategy-ordered ranking, so the scan bounds each candidate before pricing it.
/// When the content fits in a single mode that fit caps the search, so a plan is produced only when it lowers the core module count. Otherwise the single-mode stream is emitted unchanged.
/// Design rationale, bounds and measurements: specs/rmqr-encoder.md, "Mixed-mode segmentation".
/// </remarks>
internal static partial class RmQRSegmentPlanner
{
    /// <summary>
    /// Upper bound on the runs a plan can contain. The largest capacity cannot hold more than 76, and <c>RmQRSegmentPlannerUnitTest</c> pins that maximum below this.
    /// </summary>
    public const int MaxSegments = 96;

    /// <summary>
    /// Longest content any rMQR symbol can hold, in characters (361 digits at R17x139-M).
    /// No mixed plan can beat it: mixing only adds headers to a denser-per-character mode that does not exist.
    /// </summary>
    /// <remarks>
    /// A rejection rule, not only a work cap: a mixed plan can encode content no single mode holds, so this is what declares longer content impossible.
    /// The margin is 3 bits (362 digits cost 1219 against 1216), so re-derive it rather than nudge it if the capacity tables change.
    /// Pinned by <c>RmQRSegmentPlannerUnitTest</c>.
    /// </remarks>
    private const int MaxPlannableChars = 361;

    /// <summary>
    /// Count indicator triples memoised during a version scan.
    /// The 32 versions share 13 distinct triples, but the trivial bound and the skip on a priced triple leave few to price (at most 3 a text over 3,913 texts, specs/rmqr-encoder.md). Undersizing is safe, not wrong, because the store is guarded and a miss simply recomputes.
    /// </summary>
    private const int MemoCapacity = 8;

    // Narrowest count indicator any version uses, per mode (ISO/IEC 23941 Table 3, pinned by RmQRSegmentPlannerUnitTest).
    // A cost run at these widths is a lower bound for every version, because widening a count indicator can only raise the price of the run that carries it, and the minimum over plans of a pointwise larger cost is itself larger.
    private const int MinCountBitsNumeric = 4;
    private const int MinCountBitsAlnum = 3;
    private const int MinCountBitsByte = 3;

    /// <summary>Narrowest count indicator of any mode at any version, the minimum of the three widths pinned by RmQRSegmentPlannerUnitTest.</summary>
    private const int MinCountBitsAny = 3;

    // Kanji's narrowest count indicator (R7x43 and R11x27), for the Kanji plan's floor and screen. Pinned by RmQRSegmentPlannerUnitTest.
    private const int MinCountBitsKanji = 2;

    /// <summary>rMQR ECI prefix: 3-bit mode indicator 111 plus a one-byte assignment designator.</summary>
    private const int EciHeaderBits = RmQRConstants.ModeIndicatorLength + 8;

    /// <summary>
    /// Version fit for mixed-mode segmentation.
    /// Returns the version to encode at and whether a mixed-mode plan is what makes it fit. When <paramref name="useSegments"/> is false the caller emits the ordinary single-mode stream, bit-identical to <see cref="RmQRSegmentation.Single"/>.
    /// When <paramref name="kanjiPlan"/> is true the plan is the Kanji plan of a Kanji-eligible text, built by <see cref="TryBuildKanjiPlan"/> and written with no ECI header.
    /// Throws exactly what <see cref="RmQRVersionSelector"/> throws.
    /// </summary>
    public static RmQRVersion SelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, out bool useSegments, out bool kanjiPlan)
        => SelectVersion(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, default, out useSegments, out kanjiPlan, out _, out _, out _);

    /// <summary>The same, keeping the last cost run's predecessors in <paramref name="table"/>, which holds the plan where <paramref name="tableHoldsPlan"/> says so.</summary>
    public static RmQRVersion SelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Span<byte> table, out bool useSegments, out bool kanjiPlan, out bool tableHoldsPlan, out int tableFinalState, out int tablePlannedBits)
    {
        if (TrySelectVersion(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, table, out var version, out useSegments, out kanjiPlan, out tableHoldsPlan, out tableFinalState, out tablePlannedBits))
            return version;

        // Neither one mode nor a mixed plan fits: the single-mode selector owns the message.
        // Only the Numeric shortcut can reach here with a requested version, and it wants that wording, so forwarding it unchanged is correct.
        return Select(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height);
    }

    /// <summary>
    /// <see cref="SelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, out bool, out bool)"/> without the capacity throw. Argument errors still throw, in the same order and with the same messages.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, out RmQRVersion selected, out bool useSegments, out bool kanjiPlan)
        => TrySelectVersion(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, default, out selected, out useSegments, out kanjiPlan, out _, out _, out _);

    /// <summary>
    /// The same, keeping the last cost run's predecessors in <paramref name="table"/> (<see cref="ModeSegmenter.ParentBytesPerChar"/> bytes a character, or empty to keep nothing).
    /// <paramref name="tableHoldsPlan"/> says whether that run priced the candidate accepted. Then the table ended in <paramref name="tableFinalState"/> at <paramref name="tablePlannedBits"/>, and
    /// <see cref="TryBuildPlan(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, ReadOnlySpan{byte}, int, int, Span{ModeSegment}, out int)"/> builds the plan from it without running the program again.
    /// A Kanji-eligible text's scan keeps no table.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Span<byte> table, out RmQRVersion selected, out bool useSegments, out bool kanjiPlan, out bool tableHoldsPlan, out int tableFinalState, out int tablePlannedBits)
    {
        useSegments = false;
        kanjiPlan = false;
        tableHoldsPlan = false;
        tableFinalState = 0;
        tablePlannedBits = 0;
        var charset = analysis.EciMode;
        var mode = analysis.EncodingMode;
        var dataLength = analysis.DataLength;

        // Validate before any planning so argument errors keep the selector's type, message and precedence. The selector re-validates on the paths reaching it.
        RmQRVersionSelector.ValidateFitArguments(eccLevel, fitStrategy, height, requestedVersion, charset);

        // All-Numeric content is already at the optimum: splitting a Numeric run never lowers its payload and every extra run adds a header.
        // So is Kanji content: the analyser chooses it only when every character has a Kanji cell, so none of them fits another mode more cheaply, and Byte would need an ECI header.
        // Not Alphanumeric or Byte, since those payloads can still hide a digit run worth splitting off.
        if (mode is EncodingMode.Numeric or EncodingMode.Kanji)
            return RmQRVersionSelector.TrySelect(mode, dataLength, charset, eccLevel, requestedVersion, fitStrategy, height, out selected);

        var eciBits = charset == EciMode.Default ? 0 : EciHeaderBits;

        if (requestedVersion is { } requested)
        {
            // Single mode already fits: mixing cannot shrink a fixed version, so the stream stays the single-mode one.
            selected = requested;
            if (Fits(requested, eccLevel, mode, dataLength, charset))
                return true;

            // One candidate, so pricing it here would decide what building the plan decides anyway. TryBuildPlan rejects a plan the version cannot hold and the caller falls back to the single-mode selector, which owns the error.
            // A Kanji-eligible text has two plans, and which one to build is this call's to say, so its Kanji plan is priced here. Where it does not fit, the UTF-8 plan is left to TryBuildPlan like any other.
            useSegments = true;
            if (analysis.KanjiPlannable && text.Length is > 0 and <= MaxPlannableChars)
                kanjiPlan = KanjiPlanCost(text, requested, default, out _) <= 8 * RmQRConstants.GetDataCodewordCount(requested, eccLevel);
            return true;
        }

        // Ceiling: the version single-mode encoding lands on, when there is one.
        // When there is none the scan runs to the end instead of stopping early, because content that overflows every version in one mode can still fit once the modes are mixed (100 letters followed by 100 digits is 200 Byte-mode characters, 50 over the largest capacity, but 1157 bits when split).
        var hasSingle = RmQRVersionSelector.TrySelectAutoFit(mode, dataLength, charset, eccLevel, fitStrategy, height, out var single);

        // Unplannable content skips the scan before any cost run, which is what keeps a pathological length from paying for one. The runs below are not guarded themselves.
        if (text.Length is 0 or > MaxPlannableChars)
        {
            selected = single;
            return hasSingle;
        }

        if (analysis.KanjiPlannable)
            return TrySelectVersionKanji(text, eccLevel, fitStrategy, height, single, hasSingle, out selected, out useSegments, out kanjiPlan);

        var order = RmQRVersionSelector.GetFitOrder(fitStrategy);
        var heightMask = RmQRVersionSelector.GetFitHeightMask(fitStrategy, height);

        // Two filters ahead of a cost run. The trivial bound is a lower bound for every version. A priced width triple is a lower bound for every
        // version whose three widths are at least its, since widening a count indicator only raises the price of the run that carries it.
        // A candidate neither filter rejects is priced at its own widths, with its predecessors kept, so the accepting run's table is the plan's.
        // A floor run at the narrowest widths, with the floor plan re-priced as an upper bound, decided most candidates without a run of their own
        // but left the build to run the program again. Rationale and measurements: specs/rmqr-encoder.md, "Mixed-mode segmentation".
        var trivialBits = TrivialLowerBoundBits(text, charset) + eciBits;

        Span<int> memoKeys = stackalloc int[MemoCapacity];
        Span<int> memoCosts = stackalloc int[MemoCapacity];
        var memoCount = 0;

        Debug.Assert(table.IsEmpty || table.Length >= text.Length * ModeSegmenter.ParentBytesPerChar);
        var window = table.IsEmpty ? default : table.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar);
        var tableKey = -1;
        var tableState = 0;

        for (var rank = 0; rank < order.Length; rank++)
        {
            var candidate = (RmQRVersion)order[rank];
            if (hasSingle && candidate == single)
                break; // every later rank is no better than the single-mode fit
            if ((heightMask & (1u << rank)) == 0)
                continue;

            var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(candidate, eccLevel);
            if (capacityBits < trivialBits)
                continue; // no split of this content could fit, whatever the plan

            if (PlanFits(text, charset, candidate, eccLevel, eciBits, memoKeys, memoCosts, ref memoCount, window, ref tableKey, ref tableState, out var cost, out var key))
            {
                useSegments = true;
                selected = candidate;
                tableHoldsPlan = !window.IsEmpty && tableKey == key;
                tableFinalState = tableState;
                tablePlannedBits = cost;
                return true;
            }
        }

        selected = single;
        return hasSingle;
    }

    /// <summary>
    /// The version scan for a Kanji-eligible text with ASCII in it (<see cref="TextAnalysisResult.KanjiPlannable"/>), where two plans compete ahead of the single-mode (UTF-8) fit.
    /// At each candidate, in the strategy's order, the Kanji plan (Kanji runs beside runs of the ASCII, no ECI header) is taken where it fits, otherwise the UTF-8 plan the seven-state program gives the same text.
    /// </summary>
    /// <remarks>
    /// The UTF-8 plan is weighed so that a Kanji-eligible text never needs a larger symbol than its UTF-8 plan gives: finely interleaved kanji and ASCII pay a header per run as Kanji, and there one UTF-8 Byte run can be the smaller plan.
    /// Each program has its own three filters: the trivial bound <see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, out RmQRVersion, out bool, out bool)"/> keeps, and a floor and the floor plan's upper bound, which that scan dropped on 2026-10-17 for a kept table. The Kanji plan's are re-derived for its eighth state: the screen prices a character with a cell at 13 bits, and the floor runs at Kanji's narrowest count indicator, 2 bits, so that a plan filling R7x43 exactly is not skipped. The upper bound re-prices the floor plan's Kanji runs too. The memo key carries Kanji's width, which tells apart versions whose other three widths agree (R13x77 and R15x59, R13x139 and R17x99).
    /// </remarks>
    private static bool TrySelectVersionKanji(ReadOnlySpan<char> text, RmQREccLevel eccLevel, RmQRFitStrategy fitStrategy, RmQRHeight? height, RmQRVersion single, bool hasSingle, out RmQRVersion selected, out bool useSegments, out bool kanjiPlan)
    {
        useSegments = false;
        kanjiPlan = false;
        var order = RmQRVersionSelector.GetFitOrder(fitStrategy);
        var heightMask = RmQRVersionSelector.GetFitHeightMask(fitStrategy, height);

        // One pass for both screens: the UTF-8 one is TrivialLowerBoundBits under UTF-8 plus the ECI header.
        var kanjiSixths = ModeSegmenter.CheapestSixthsKanji(text, out var utf8Sixths, out var cellRuns, out var asciiRuns);
        var kanjiTrivialBits = KanjiScreenBits(kanjiSixths, cellRuns, asciiRuns);
        var utf8TrivialBits = (utf8Sixths + 5) / 6 + RmQRConstants.ModeIndicatorLength + MinCountBitsAny + EciHeaderBits;

        var kanjiFloor = -1;
        int kanjiRunsNumeric = 0, kanjiRunsAlnum = 0, kanjiRunsByte = 0, kanjiRunsKanji = 0;
        var utf8Floor = -1;
        int utf8RunsNumeric = 0, utf8RunsAlnum = 0, utf8RunsByte = 0;
        int utf8TableKey = -1, utf8TableState = 0;

        // One store for both programs: a Kanji key carries Kanji's width (at least 2) in its top byte, a UTF-8 key none.
        Span<int> memoKeys = stackalloc int[MemoCapacity];
        Span<int> memoCosts = stackalloc int[MemoCapacity];
        var memoCount = 0;

        for (var rank = 0; rank < order.Length; rank++)
        {
            var candidate = (RmQRVersion)order[rank];
            if (hasSingle && candidate == single)
                break; // every later rank is no better than the single-mode fit
            if ((heightMask & (1u << rank)) == 0)
                continue;

            var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(candidate, eccLevel);

            if (capacityBits >= kanjiTrivialBits)
            {
                if (kanjiFloor < 0)
                    kanjiFloor = ComputeFloorKanji(text, out kanjiRunsNumeric, out kanjiRunsAlnum, out kanjiRunsByte, out kanjiRunsKanji);

                if (capacityBits >= kanjiFloor
                    && (capacityBits >= UpperBoundKanji(kanjiFloor, kanjiRunsNumeric, kanjiRunsAlnum, kanjiRunsByte, kanjiRunsKanji, candidate)
                        || KanjiPlanFits(text, candidate, capacityBits, memoKeys, memoCosts, ref memoCount)))
                {
                    useSegments = true;
                    kanjiPlan = true;
                    selected = candidate;
                    return true;
                }
            }

            if (capacityBits >= utf8TrivialBits)
            {
                if (utf8Floor < 0)
                    utf8Floor = ComputeFloor(text, EciMode.Utf8, out utf8RunsNumeric, out utf8RunsAlnum, out utf8RunsByte);

                if (capacityBits >= utf8Floor + EciHeaderBits
                    && (capacityBits >= UpperBound(utf8Floor, utf8RunsNumeric, utf8RunsAlnum, utf8RunsByte, candidate) + EciHeaderBits
                        || PlanFits(text, EciMode.Utf8, candidate, eccLevel, EciHeaderBits, memoKeys, memoCosts, ref memoCount, default, ref utf8TableKey, ref utf8TableState, out _, out _)))
                {
                    useSegments = true;
                    selected = candidate;
                    return true;
                }
            }
        }

        selected = single;
        return hasSingle;
    }

    /// <summary>
    /// <see cref="TryBuildPlan(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, Span{ModeSegment}, out int)"/> for the Kanji plan <see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, out RmQRVersion, out bool, out bool)"/> chose: Kanji runs beside runs of the ASCII, which the caller writes with no ECI header.
    /// </summary>
    public static bool TryBuildKanjiPlan(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        var parentLength = text.Length * ModeSegmenter.ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.MaxStackParents
            ? stackalloc byte[ModeSegmenter.MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        int plannedBits;
        try
        {
            var window = parents.Slice(0, parentLength);
            plannedBits = KanjiPlanCost(text, version, window, out var finalState);
            if (!ModeSegmenter.Reconstruct(text, window, finalState, segments, out segmentCount))
            {
                segmentCount = 0;
                return false;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }

        var measuredBits = PriceKanjiPlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        if (measuredBits != plannedBits || measuredBits > 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel))
        {
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fills each run of a Kanji plan with the value its count indicator carries and returns what the plan measures, in one pass over the runs: <see cref="ModeSegmenter.FillUnitCounts"/> and <see cref="MeasurePlan"/> together.
    /// Every run's value is its length, since a Kanji plan's Byte runs hold only ASCII, one byte a character.
    /// </summary>
    private static int PriceKanjiPlan(RmQRVersion version, Span<ModeSegment> segments)
    {
        var numericHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var alnumHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var byteHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var kanjiHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetKanjiCountIndicatorLength(version);
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
    /// Returns false when the content is unplannable, the plan needs more runs than the caller lent room for, the plan would be misread on decode (a Byte run opened at a mid-content U+FEFF, which the program does not build), or the exact re-costed stream would not fit. The caller answers all four by falling back to the single-mode stream.
    /// </summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        var cciNumeric = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var cciAlnum = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var cciByte = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);

        var parentLength = text.Length * ModeSegmenter.ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.MaxStackParents
            ? stackalloc byte[ModeSegmenter.MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        int plannedBits;
        try
        {
            var window = parents.Slice(0, parentLength);
            plannedBits = ModeSegmenter.ComputeCosts(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, window, out var finalState);
            if (!ModeSegmenter.Reconstruct(text, window, finalState, segments, out segmentCount))
            {
                segmentCount = 0;
                return false;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }

        return AcceptPlan(text, charset, version, eccLevel, plannedBits, segments, ref segmentCount);
    }

    /// <summary>
    /// <see cref="TryBuildPlan(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, Span{ModeSegment}, out int)"/> from the table the version scan kept for the plan it chose
    /// (<see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, Span{byte}, out RmQRVersion, out bool, out bool, out bool, out int, out int)"/>), without running the program again.
    /// </summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> table, int finalState, int plannedBits, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        if (!ModeSegmenter.Reconstruct(text, table.Slice(0, text.Length * ModeSegmenter.ParentBytesPerChar), finalState, segments, out segmentCount))
        {
            segmentCount = 0;
            return false;
        }

        return AcceptPlan(text, charset, version, eccLevel, plannedBits, segments, ref segmentCount);
    }

    /// <summary>What follows the walk back of a plan: the refusal of a plan the decoder would misread, the unit counts, and the re-measurement against the program and the capacity.</summary>
    private static bool AcceptPlan(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, int plannedBits, Span<ModeSegment> segments, ref int segmentCount)
    {
        // A plan that opens a Byte run at a mid-content U+FEFF would lose it to the decoder's BOM consumption (which fires even behind an explicit UTF-8 ECI).
        // Under UTF-8 the program builds none, so this is the refusal of a model that disagreed. The single-mode fallback keeps the character.
        if (ModeSegmenter.HasBomRelocatedToARunStart(text, segments.Slice(0, segmentCount)))
        {
            segmentCount = 0;
            return false;
        }

        ModeSegmenter.FillUnitCounts(text, charset, segments.Slice(0, segmentCount));

        // Re-cost the reconstructed plan from the byte counts the encoder will actually emit.
        // Disagreeing with the dynamic programming cost model is a bug in the model (the version scan would have compared the wrong number against a capacity), so it fails loudly in Debug and rejects the plan in Release rather than becoming a stream that overruns the data codewords.
        var measuredBits = MeasurePlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);
        if (measuredBits != plannedBits || measuredBits + (charset == EciMode.Default ? 0 : EciHeaderBits) > capacityBits)
        {
            // Either the model disagreed, or this version simply cannot hold the plan (a legitimate answer for a caller that asked about a specific version).
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The single-mode fit for the analyzed content, i.e. what <see cref="RmQRSegmentation.Single"/> would select.
    /// Throws the ordinary "content is too long" error when nothing fits.
    /// </summary>
    public static RmQRVersion SelectSingle(in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height)
        => Select(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height);

    /// <summary>Exact bit cost of a plan: per run, mode indicator + count indicator + payload.</summary>
    /// <remarks>
    /// The four headers are looked up once for the version rather than per run: a Kanji plan of interleaved text has a run every few characters, and the plan is measured twice (when built and before it is written).
    /// </remarks>
    public static int MeasurePlan(RmQRVersion version, ReadOnlySpan<ModeSegment> segments)
    {
        var numericHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var alnumHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var byteHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var kanjiHeader = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetKanjiCountIndicatorLength(version);
        var total = 0;
        foreach (var segment in segments)
        {
            var header = segment.ModeIndex switch { 0 => numericHeader, 1 => alnumHeader, 2 => byteHeader, _ => kanjiHeader };
            total += header + ModeSegmenter.PayloadBitsOfIndex(segment.ModeIndex, segment.UnitCount);
        }
        return total;
    }

    /// <summary>Payload bits of <paramref name="unitCount"/> units in <paramref name="mode"/> (ISO/IEC 23941 7.4).</summary>
    public static int PayloadBits(EncodingMode mode, int unitCount)
        => ModeSegmenter.PayloadBits(mode, unitCount);

    /// <summary>Encoded byte count of a Byte-mode run, i.e. the value its count indicator carries.</summary>
    public static int ByteUnitCount(ReadOnlySpan<char> text, EciMode charset)
        => ModeSegmenter.ByteUnitCount(text, charset);

    /// <summary>
    /// Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the minimal payload bits (no ECI prefix) at explicit count indicator widths, which is the value the version scan compares against a data capacity.
    /// </summary>
    public static int MinimumPayloadBits(ReadOnlySpan<char> text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte)
        => ModeSegmenter.ComputeCosts(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, default, out _);

    /// <summary>
    /// Bytes of the table the version scan keeps for the plan's build: the program's predecessors, <see cref="ModeSegmenter.ParentBytesPerChar"/> a character, for a text of plannable length whose single mode a plan could beat and whose scan is the seven-state one, and none for any other.
    /// </summary>
    public static int TableLength(ReadOnlySpan<char> text, in TextAnalysisResult analysis)
        => analysis.EncodingMode is EncodingMode.Numeric or EncodingMode.Kanji || analysis.KanjiPlannable || text.Length is 0 or > MaxPlannableChars ? 0 : text.Length * ModeSegmenter.ParentBytesPerChar;

    /// <summary>Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the Kanji plan's minimal payload bits at explicit count indicator widths.</summary>
    public static int MinimumPayloadBitsKanji(ReadOnlySpan<char> text, int cciNumeric, int cciAlnum, int cciByte, int cciKanji)
        => ModeSegmenter.ComputeCostsKanji(text, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, cciKanji, default, out _);

    /// <summary>Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the Kanji plan's floor, the minimal payload bits at the narrowest widths of all four modes.</summary>
    public static int FloorKanji(ReadOnlySpan<char> text)
        => ComputeFloorKanji(text, out _, out _, out _, out _);

    /// <summary>Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the Kanji plan's floor plan re-priced at <paramref name="version"/>.</summary>
    public static int FloorPlanUpperBoundKanji(ReadOnlySpan<char> text, RmQRVersion version)
    {
        var floor = ComputeFloorKanji(text, out var runsNumeric, out var runsAlnum, out var runsByte, out var runsKanji);
        return UpperBoundKanji(floor, runsNumeric, runsAlnum, runsByte, runsKanji, version);
    }

    // ---------------------------------------------------------------
    // Version scan
    // ---------------------------------------------------------------

    /// <summary>
    /// Whether the plan at <paramref name="version"/>'s widths fits its capacity, from the memo, from a priced triple no wider in any mode whose cost does not fit, or from a cost run.
    /// A run made with a <paramref name="table"/> leaves its predecessors there and names its widths in <paramref name="tableKey"/>, so the caller can tell whose plan the table holds.
    /// </summary>
    private static bool PlanFits(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, int eciBits, Span<int> memoKeys, Span<int> memoCosts, ref int memoCount, Span<byte> table, ref int tableKey, ref int tableState, out int cost, out int key)
    {
        var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var cciNumeric = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var cciAlnum = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var cciByte = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        key = cciNumeric | (cciAlnum << 8) | (cciByte << 16);

        cost = -1;
        for (var i = 0; i < memoCount; i++)
        {
            if (memoKeys[i] == key)
            {
                cost = memoCosts[i];
                break;
            }
        }

        if (cost < 0)
        {
            // A triple priced already whose every width is at most this one's prices every plan at most what this one would, so its cost not fitting here is this one's not fitting.
            for (var i = 0; i < memoCount; i++)
            {
                var priced = memoKeys[i];
                if ((priced & 0xFF) <= cciNumeric && ((priced >> 8) & 0xFF) <= cciAlnum && (priced >> 16) <= cciByte && memoCosts[i] + eciBits > capacityBits)
                    return false;
            }

            cost = ModeSegmenter.ComputeCosts(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, table, out var finalState);
            if (!table.IsEmpty)
            {
                tableKey = key;
                tableState = finalState;
            }
            if (memoCount < memoKeys.Length)
            {
                memoKeys[memoCount] = key;
                memoCosts[memoCount] = cost;
                memoCount++;
            }
        }

        return cost + eciBits <= capacityBits;
    }

    /// <summary><see cref="PlanFits"/> for the Kanji plan, memoised under a key that carries Kanji's width as well.</summary>
    private static bool KanjiPlanFits(ReadOnlySpan<char> text, RmQRVersion version, int capacityBits, Span<int> memoKeys, Span<int> memoCosts, ref int memoCount)
    {
        var key = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)
            | (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric) << 8)
            | (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte) << 16)
            | (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji) << 24);

        var cost = -1;
        for (var i = 0; i < memoCount; i++)
        {
            if (memoKeys[i] == key)
            {
                cost = memoCosts[i];
                break;
            }
        }

        if (cost < 0)
        {
            cost = KanjiPlanCost(text, version, default, out _);
            if (memoCount < memoKeys.Length)
            {
                memoKeys[memoCount] = key;
                memoCosts[memoCount] = cost;
                memoCount++;
            }
        }

        return cost <= capacityBits;
    }

    /// <summary>The Kanji plan's program at <paramref name="version"/>'s widths.</summary>
    private static int KanjiPlanCost(ReadOnlySpan<char> text, RmQRVersion version, Span<byte> parents, out int finalState)
        => ModeSegmenter.ComputeCostsKanji(
            text, RmQRConstants.ModeIndicatorLength,
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji),
            parents, out finalState);

    // ---------------------------------------------------------------
    // Scan bounds
    // ---------------------------------------------------------------

    /// <summary>
    /// A lower bound on any plan at any version, in payload bits, computed in one O(n) pass with no dynamic programming table: each character priced at the cheapest rate any mode could give it, plus the cheapest possible single segment header.
    /// </summary>
    /// <remarks>
    /// Deliberately cruder than <see cref="ComputeFloor"/> and far cheaper: it answers "could a split reach a better version at all" before any cost run.
    /// Loose where a split is worth searching, tight where it is not.
    /// </remarks>
    public static int TrivialLowerBoundBits(ReadOnlySpan<char> text, EciMode charset)
        => (ModeSegmenter.CheapestSixths(text, charset) + 5) / 6 + RmQRConstants.ModeIndicatorLength + MinCountBitsAny;

    /// <summary>
    /// The floor cost run of the Kanji scan's UTF-8 plan: minimal payload bits at the narrowest count indicator widths, plus how many runs of each mode the plan achieving it contains.
    /// The run counts are what let <see cref="UpperBound"/> re-price that same plan at any version without a second cost run.
    /// </summary>
    private static int ComputeFloor(ReadOnlySpan<char> text, EciMode charset, out int runsNumeric, out int runsAlnum, out int runsByte)
    {
        var parentLength = text.Length * ModeSegmenter.ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.MaxStackParents
            ? stackalloc byte[ModeSegmenter.MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        try
        {
            var window = parents.Slice(0, parentLength);
            var cost = ModeSegmenter.ComputeCosts(text, charset, RmQRConstants.ModeIndicatorLength, MinCountBitsNumeric, MinCountBitsAlnum, MinCountBitsByte, window, out var finalState);
            ModeSegmenter.CountRuns(window, finalState, text.Length, out runsNumeric, out runsAlnum, out runsByte);
            return cost;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// The floor plan re-priced at <paramref name="version"/>: its payload and run structure are unchanged, so only each run's count indicator grows.
    /// Being the price of a real plan, this is an upper bound on the version's optimum.
    /// </summary>
    private static int UpperBound(int floorPayload, int runsNumeric, int runsAlnum, int runsByte, RmQRVersion version)
        => floorPayload
            + runsNumeric * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric) - MinCountBitsNumeric)
            + runsAlnum * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric) - MinCountBitsAlnum)
            + runsByte * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte) - MinCountBitsByte);

    /// <summary>
    /// Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the upper bound the Kanji scan uses to accept a candidate for the UTF-8 plan without pricing it. It is the floor plan re-priced at <paramref name="version"/>, in payload bits (no ECI prefix).
    /// </summary>
    public static int FloorPlanUpperBound(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version)
    {
        var floor = ComputeFloor(text, charset, out var runsNumeric, out var runsAlnum, out var runsByte);
        return UpperBound(floor, runsNumeric, runsAlnum, runsByte, version);
    }

    /// <summary>
    /// <see cref="TrivialLowerBoundBits"/> for the Kanji plan: a character with a cell at 13 bits, a header for every stretch of either kind (<see cref="KanjiScreenBits"/>), and no ECI header.
    /// </summary>
    public static int TrivialLowerBoundBitsKanji(ReadOnlySpan<char> text)
        => KanjiScreenBits(ModeSegmenter.CheapestSixthsKanji(text, out _, out var cellRuns, out var asciiRuns), cellRuns, asciiRuns);

    /// <summary>Each stretch of characters with a cell opens a Kanji run (3 + 2 bits at the narrowest), and each stretch of ASCII a run of another mode (3 + 3), since no run crosses between them.</summary>
    private static int KanjiScreenBits(int sixths, int cellRuns, int asciiRuns)
        => (sixths + 5) / 6
            + cellRuns * (RmQRConstants.ModeIndicatorLength + MinCountBitsKanji)
            + asciiRuns * (RmQRConstants.ModeIndicatorLength + Math.Min(MinCountBitsNumeric, Math.Min(MinCountBitsAlnum, MinCountBitsByte)));

    /// <summary>
    /// <see cref="ComputeFloor"/> for the Kanji plan: its program at the narrowest widths of all four modes, Kanji's 2 bits included, and the runs of each mode on the plan achieving it.
    /// </summary>
    private static int ComputeFloorKanji(ReadOnlySpan<char> text, out int runsNumeric, out int runsAlnum, out int runsByte, out int runsKanji)
    {
        var parentLength = text.Length * ModeSegmenter.ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.MaxStackParents
            ? stackalloc byte[ModeSegmenter.MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        try
        {
            var window = parents.Slice(0, parentLength);
            var cost = ModeSegmenter.ComputeCostsKanji(text, RmQRConstants.ModeIndicatorLength, MinCountBitsNumeric, MinCountBitsAlnum, MinCountBitsByte, MinCountBitsKanji, window, out var finalState);
            ModeSegmenter.CountRuns(window, finalState, text.Length, out runsNumeric, out runsAlnum, out runsByte, out runsKanji);
            return cost;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary><see cref="UpperBound"/> for the Kanji plan's floor plan: its Kanji runs' count indicators grow too.</summary>
    private static int UpperBoundKanji(int floorPayload, int runsNumeric, int runsAlnum, int runsByte, int runsKanji, RmQRVersion version)
        => UpperBound(floorPayload, runsNumeric, runsAlnum, runsByte, version)
            + runsKanji * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji) - MinCountBitsKanji);

    // ---------------------------------------------------------------
    // Selector adapters (the two Select overloads stay apart, as the selector keeps them)
    // ---------------------------------------------------------------

    private static bool Fits(RmQRVersion version, RmQREccLevel eccLevel, EncodingMode mode, int dataLength, EciMode charset)
        => charset == EciMode.Default
            ? RmQRVersionSelector.Fits(version, eccLevel, mode, dataLength)
            : RmQRVersionSelector.Fits(version, eccLevel, mode, dataLength, charset);

    private static RmQRVersion Select(EncodingMode mode, int dataLength, EciMode charset, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height)
        => charset == EciMode.Default
            ? RmQRVersionSelector.Select(mode, dataLength, eccLevel, requestedVersion, fitStrategy, height)
            : RmQRVersionSelector.Select(mode, dataLength, charset, eccLevel, requestedVersion, fitStrategy, height);
}
