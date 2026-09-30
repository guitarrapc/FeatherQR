using System.Buffers;
using System.Diagnostics;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// Mixed-mode segmentation for <see cref="RmQRSegmentation.Optimal"/>: the split of the content into Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a given version, and the version fit that follows from it.
/// </summary>
/// <remarks>
/// The cost model and reconstruction are <see cref="ModeSegmenter"/>, shared with the Standard QR and Micro QR planners; what lives here is rMQR's version scan.
/// Unlike Standard QR's banded widths, the 32 rMQR versions carry 13 distinct count indicator width triples across a strategy-ordered ranking, so the scan bounds each candidate before pricing it.
/// When the content fits in a single mode that fit caps the search, so a plan is produced only when it lowers the core module count; otherwise the single-mode stream is emitted unchanged.
/// Design rationale, bounds and measurements: specs/rmqr-encoder.md, "Mixed-mode segmentation".
/// </remarks>
internal static class RmQRSegmentPlanner
{
    /// <summary>
    /// Upper bound on the runs a plan can contain; the largest capacity cannot hold more than 76, and <c>RmQRSegmentPlannerUnitTest</c> pins that maximum below this.
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
    /// The 32 versions share 13 distinct triples, but the bounds narrow the band so far that measured sweeps never stored more than 3. Sized for that plus headroom: undersizing is safe, not wrong, because the store is guarded and a miss simply recomputes.
    /// </summary>
    private const int MemoCapacity = 8;

    // Narrowest count indicator any version uses, per mode (ISO/IEC 23941 Table 3; pinned by RmQRSegmentPlannerUnitTest).
    // A cost run at these widths is a lower bound for every version, because widening a count indicator can only raise the price of the run that carries it, and the minimum over plans of a pointwise larger cost is itself larger.
    private const int MinCountBitsNumeric = 4;
    private const int MinCountBitsAlnum = 3;
    private const int MinCountBitsByte = 3;

    /// <summary>Narrowest count indicator of any mode at any version; the minimum of the three widths pinned by RmQRSegmentPlannerUnitTest.</summary>
    private const int MinCountBitsAny = 3;

    // Kanji's narrowest count indicator (R7x43 and R11x27), for the Kanji plan's floor and screen; pinned by RmQRSegmentPlannerUnitTest.
    private const int MinCountBitsKanji = 2;

    /// <summary>rMQR ECI prefix: 3-bit mode indicator 111 plus a one-byte assignment designator.</summary>
    private const int EciHeaderBits = RmQRConstants.ModeIndicatorLength + 8;

    /// <summary>
    /// Version fit for mixed-mode segmentation.
    /// Returns the version to encode at and whether a mixed-mode plan is what makes it fit; when <paramref name="useSegments"/> is false the caller emits the ordinary single-mode stream, bit-identical to <see cref="RmQRSegmentation.Single"/>.
    /// When <paramref name="kanjiPlan"/> is true the plan is the Kanji plan of a Kanji-eligible text, built by <see cref="TryBuildKanjiPlan"/> and written with no ECI header.
    /// Throws exactly what <see cref="RmQRVersionSelector"/> throws.
    /// </summary>
    public static RmQRVersion SelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, out bool useSegments, out bool kanjiPlan)
    {
        if (TrySelectVersion(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, out var version, out useSegments, out kanjiPlan))
            return version;

        // Neither one mode nor a mixed plan fits: the single-mode selector owns the message.
        // Only the Numeric shortcut can reach here with a requested version, and it wants that wording, so forwarding it unchanged is correct.
        return Select(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height);
    }

    /// <summary>
    /// <see cref="SelectVersion"/> without the capacity throw; argument errors still throw, in the same order and with the same messages.
    /// </summary>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, out RmQRVersion selected, out bool useSegments, out bool kanjiPlan)
    {
        useSegments = false;
        kanjiPlan = false;
        var charset = analysis.EciMode;
        var mode = analysis.EncodingMode;
        var dataLength = analysis.DataLength;

        // Validate before any planning so argument errors keep their current type, message and precedence; the selector re-validates on the paths reaching it.
        RmQRVersionSelector.ValidateFitArguments(eccLevel, fitStrategy, height, requestedVersion, charset);

        // All-Numeric content is already at the optimum: splitting a Numeric run never lowers its payload and every extra run adds a header.
        // So is Kanji content: the analyser chooses it only when every character has a Kanji cell, so none of them fits another mode more cheaply, and Byte would need an ECI header.
        // Not Alphanumeric or Byte — those payloads can still hide a digit run worth splitting off.
        if (mode is EncodingMode.Numeric or EncodingMode.Kanji)
            return RmQRVersionSelector.TrySelect(mode, dataLength, charset, eccLevel, requestedVersion, fitStrategy, height, out selected);

        var eciBits = charset == EciMode.Default ? 0 : EciHeaderBits;

        if (requestedVersion is { } requested)
        {
            // Single mode already fits: mixing cannot shrink a fixed version, so the stream stays exactly as it is today.
            selected = requested;
            if (Fits(requested, eccLevel, mode, dataLength, charset))
                return true;

            // One candidate, so pricing it here would decide what building the plan decides anyway; TryBuildPlan rejects a plan the version cannot hold and the caller falls back to the single-mode selector, which owns the error.
            // A Kanji-eligible text has two plans, and which one to build is this call's to say, so its Kanji plan is priced here; where it does not fit, the UTF-8 plan is left to TryBuildPlan like any other.
            useSegments = true;
            if (analysis.KanjiPlannable && text.Length is > 0 and <= MaxPlannableChars)
                kanjiPlan = KanjiPlanCost(text, requested, default, out _) <= 8 * RmQRConstants.GetDataCodewordCount(requested, eccLevel);
            return true;
        }

        // Ceiling: the version single-mode encoding lands on, when there is one.
        // When there is none the scan runs to the end instead of stopping early, because content that overflows every version in one mode can still fit once the modes are mixed (100 letters followed by 100 digits is 200 Byte-mode characters, 50 over the largest capacity, but 1157 bits when split).
        var hasSingle = RmQRVersionSelector.TrySelectAutoFit(mode, dataLength, charset, eccLevel, fitStrategy, height, out var single);

        // Unplannable content skips the scan before any cost run, which is what keeps a pathological length from paying for one (the floor run below is not itself guarded, unlike the per-version runs in PlanFits).
        if (text.Length is 0 or > MaxPlannableChars)
        {
            selected = single;
            return hasSingle;
        }

        if (analysis.KanjiPlannable)
            return TrySelectVersionKanji(text, eccLevel, fitStrategy, height, single, hasSingle, out selected, out useSegments, out kanjiPlan);

        var order = RmQRVersionSelector.GetFitOrder(fitStrategy);
        var heightMask = RmQRVersionSelector.GetFitHeightMask(fitStrategy, height);

        // Three filters, cheapest first, so a candidate only reaches an expensive one when the cheap ones could not answer.
        // Soundness in one line each: the trivial bound and the floor are lower bounds, so they may only reject; the ceiling is the price of a real plan, so it may only accept.
        // Rationale and measurements: specs/rmqr-encoder.md, "Bounding the scan".
        var trivialBits = TrivialLowerBoundBits(text, charset) + eciBits;

        var floorPayload = -1;
        var runsNumeric = 0;
        var runsAlnum = 0;
        var runsByte = 0;

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
            if (capacityBits < trivialBits)
                continue; // no split of this content could fit, whatever the plan

            if (floorPayload < 0)
                floorPayload = ComputeFloor(text, charset, out runsNumeric, out runsAlnum, out runsByte);

            if (capacityBits < floorPayload + eciBits)
                continue; // cannot hold even the cheapest count indicators
            if (capacityBits >= UpperBound(floorPayload, runsNumeric, runsAlnum, runsByte, candidate) + eciBits)
            {
                // Holds the floor plan re-priced at this version, so it holds the optimum too; no cost run needed.
                useSegments = true;
                selected = candidate;
                return true;
            }

            if (PlanFits(text, charset, candidate, eccLevel, eciBits, memoKeys, memoCosts, ref memoCount))
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
    /// The version scan for a Kanji-eligible text with ASCII in it (<see cref="TextAnalysisResult.KanjiPlannable"/>), where two plans compete ahead of the single-mode (UTF-8) fit.
    /// At each candidate, in the strategy's order, the Kanji plan (Kanji runs beside runs of the ASCII, no ECI header) is taken where it fits, otherwise the UTF-8 plan the seven-state program gives the same text.
    /// </summary>
    /// <remarks>
    /// The UTF-8 plan is weighed so that Optimal never needs a larger symbol than it did before Kanji plans: finely interleaved kanji and ASCII pay a header per run as Kanji, and there one UTF-8 Byte run can be the smaller plan.
    /// Each program has its own three filters, the ones <see cref="TrySelectVersion"/> describes. The Kanji plan's are re-derived for its eighth state: the screen prices a character with a cell at 13 bits, and the floor runs at Kanji's narrowest count indicator, 2 bits, so that a plan filling R7x43 exactly is not skipped; the upper bound re-prices the floor plan's Kanji runs too; and the memo key carries Kanji's width, which tells apart versions whose other three widths agree (R13x77 and R15x59, R13x139 and R17x99).
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
                        || PlanFits(text, EciMode.Utf8, candidate, eccLevel, EciHeaderBits, memoKeys, memoCosts, ref memoCount)))
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
    /// <see cref="TryBuildPlan"/> for the Kanji plan <see cref="TrySelectVersion"/> chose: Kanji runs beside runs of the ASCII, which the caller writes with no ECI header.
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

        ModeSegmenter.FillUnitCounts(text, EciMode.Default, segments.Slice(0, segmentCount));

        var measuredBits = MeasurePlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        if (measuredBits != plannedBits || measuredBits > 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel))
        {
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the minimal-cost plan for <paramref name="version"/> into <paramref name="segments"/>.
    /// Returns false when the content is unplannable, the plan needs more runs than the caller lent room for, the plan would be misread on decode (a Byte run opened at a mid-content U+FEFF, which the program does not build), or the exact re-costed stream would not fit; the caller answers all four by falling back to the single-mode stream.
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

        // A plan that opens a Byte run at a mid-content U+FEFF would lose it to the decoder's BOM consumption (which fires even behind an explicit UTF-8 ECI).
        // Under UTF-8 the program builds none, so this is the refusal of a model that disagreed; the single-mode fallback keeps the character.
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
    /// Named entry point for <c>RmQRSegmentPlannerUnitTest</c>: the upper bound the version scan uses to accept a candidate without pricing it — the floor plan re-priced at <paramref name="version"/>, in payload bits (no ECI prefix).
    /// </summary>
    public static int FloorPlanUpperBound(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version)
    {
        var floor = ComputeFloor(text, charset, out var runsNumeric, out var runsAlnum, out var runsByte);
        return UpperBound(floor, runsNumeric, runsAlnum, runsByte, version);
    }

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

    private static bool PlanFits(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, int eciBits, Span<int> memoKeys, Span<int> memoCosts, ref int memoCount)
    {
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        var cciNumeric = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var cciAlnum = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var cciByte = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var key = cciNumeric | (cciAlnum << 8) | (cciByte << 16);

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
            cost = ModeSegmenter.ComputeCosts(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, default, out _);
            if (memoCount < memoKeys.Length)
            {
                memoKeys[memoCount] = key;
                memoCosts[memoCount] = cost;
                memoCount++;
            }
        }

        return cost + eciBits <= 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);
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
    /// The floor cost run: minimal payload bits at the narrowest count indicator widths, plus how many runs of each mode the plan achieving it contains.
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
