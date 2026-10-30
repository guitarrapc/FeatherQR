using System.Buffers;
using System.Diagnostics;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// The version scan and the plan under FNC1 (specs/rmqr-encoder.md, FNC1). The indicator's bits, 3 in first
/// position and 11 in second, join the ECI prefix in every capacity check, and the single-mode ceiling is the fit with
/// those bits. The costs come from the FNC1 program (<see cref="ModeSegmenter.ComputeCostsFnc1"/>), whose Alphanumeric runs count a literal
/// <c>%</c> twice and never hold a separator pair. No Kanji plan is priced. The scan has the shape of the scan without
/// FNC1 and without Kanji: the trivial bound, a memoised cost run per width triple and the kept table. It has no floor run,
/// since one counting runs would read the other program's packed table, and the route is cold enough for the exact cost run to decide alone.
/// With a table, the scan leaves the predecessors of its last run there and says whether that run priced the
/// candidate it accepted. A remembered cost accepts a candidate priced by an earlier run, whose table is gone,
/// and a requested version is accepted unpriced. Both build their plan with a run of their own.
/// </summary>
internal static partial class RmQRSegmentPlanner
{
    /// <summary><see cref="SelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, out bool, out bool)"/> under FNC1, throwing what <see cref="RmQRVersionSelector.SelectFnc1"/> throws when nothing fits.</summary>
    public static RmQRVersion SelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, out bool useSegments)
        => SelectVersionFnc1(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, fnc1, default, out useSegments, out _, out _, out _);

    /// <summary>The same, keeping the last cost run's predecessors in <paramref name="table"/>, which holds the plan where <paramref name="tableHoldsPlan"/> says so.</summary>
    public static RmQRVersion SelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, Span<byte> table, out bool useSegments, out bool tableHoldsPlan, out int tableFinalState, out int tablePlannedBits)
    {
        if (TrySelectVersionFnc1(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, fnc1, table, out var version, out useSegments, out tableHoldsPlan, out tableFinalState, out tablePlannedBits))
            return version;

        return RmQRVersionSelector.SelectFnc1(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height, fnc1.RmQrHeaderBits);
    }

    /// <summary><see cref="TrySelectVersion(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, out RmQRVersion, out bool, out bool)"/> under FNC1. Argument errors still throw, in the same order and with the same messages.</summary>
    public static bool TrySelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, out RmQRVersion selected, out bool useSegments)
        => TrySelectVersionFnc1(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, fnc1, default, out selected, out useSegments, out _, out _, out _);

    /// <summary>
    /// The same, keeping the last cost run's predecessors in <paramref name="table"/> (<see cref="ModeSegmenter.Fnc1ParentBytesPerChar"/> bytes a character, or empty to keep nothing).
    /// <paramref name="tableHoldsPlan"/> says whether that run priced the candidate accepted. Then the table ended in <paramref name="tableFinalState"/> at <paramref name="tablePlannedBits"/>, and
    /// <see cref="TryBuildPlanFnc1(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, Fnc1Mode, ReadOnlySpan{byte}, int, int, Span{ModeSegment}, out int)"/> builds the plan from it without running the program again.
    /// </summary>
    public static bool TrySelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, Span<byte> table, out RmQRVersion selected, out bool useSegments, out bool tableHoldsPlan, out int tableFinalState, out int tablePlannedBits)
    {
        useSegments = false;
        tableHoldsPlan = false;
        tableFinalState = 0;
        tablePlannedBits = 0;
        var charset = analysis.EciMode;
        var mode = analysis.EncodingMode;
        var dataLength = analysis.DataLength;
        var fnc1Bits = fnc1.RmQrHeaderBits;

        RmQRVersionSelector.ValidateFitArguments(eccLevel, fitStrategy, height, requestedVersion, charset);

        // All-Numeric content is at the optimum, and so is a Kanji text (every character has a cell), as without FNC1.
        if (mode is EncodingMode.Numeric or EncodingMode.Kanji)
            return RmQRVersionSelector.TrySelectFnc1(mode, dataLength, charset, eccLevel, requestedVersion, fitStrategy, height, fnc1Bits, out selected);

        var headerBits = (charset == EciMode.Default ? 0 : EciHeaderBits) + fnc1Bits;

        if (requestedVersion is { } requested)
        {
            selected = requested;
            if (RmQRVersionSelector.FitsFnc1(requested, eccLevel, mode, dataLength, charset, fnc1Bits))
                return true;

            // One candidate: TryBuildPlanFnc1 decides, and the caller falls back to the single-mode selector, which owns the error.
            useSegments = true;
            return true;
        }

        var hasSingle = RmQRVersionSelector.TrySelectFnc1(mode, dataLength, charset, eccLevel, requestedVersion: null, fitStrategy, height, fnc1Bits, out var single);

        if (text.Length is 0 or > MaxPlannableChars)
        {
            selected = single;
            return hasSingle;
        }

        var order = RmQRVersionSelector.GetFitOrder(fitStrategy);
        var heightMask = RmQRVersionSelector.GetFitHeightMask(fitStrategy, height);
        // The bound carries one header only. Under FNC1 a literal percent costs two Alphanumeric units and one byte, so one Byte run can undercut the Alphanumeric stream the fit priced and fit a candidate ahead of it.
        var trivialBits = TrivialLowerBoundBitsFnc1(text, charset) + headerBits;

        Span<int> memoKeys = stackalloc int[MemoCapacity];
        Span<int> memoCosts = stackalloc int[MemoCapacity];
        var memoCount = 0;

        Debug.Assert(table.IsEmpty || table.Length >= text.Length * ModeSegmenter.Fnc1ParentBytesPerChar);
        var window = table.IsEmpty ? default : table.Slice(0, text.Length * ModeSegmenter.Fnc1ParentBytesPerChar);
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

            if (PlanFitsFnc1(text, charset, candidate, eccLevel, headerBits, memoKeys, memoCosts, ref memoCount, window, ref tableKey, ref tableState, out var cost, out var key))
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

    /// <summary><see cref="TryBuildPlan(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, Span{ModeSegment}, out int)"/> under FNC1: the FNC1 program's plan, its Alphanumeric runs counting their literal percents twice, and the indicator's bits in the capacity check.</summary>
    public static bool TryBuildPlanFnc1(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, Fnc1Mode fnc1, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        var cciNumeric = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var cciAlnum = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var cciByte = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);

        var parentLength = text.Length * ModeSegmenter.Fnc1ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.Fnc1MaxStackParents
            ? stackalloc byte[ModeSegmenter.Fnc1MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        int plannedBits;
        try
        {
            var window = parents.Slice(0, parentLength);
            plannedBits = ModeSegmenter.ComputeCostsFnc1(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, window, out var finalState);
            if (!ModeSegmenter.ReconstructFnc1(text, window, finalState, segments, out segmentCount))
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

        return AcceptPlanFnc1(text, charset, version, eccLevel, fnc1, plannedBits, segments, ref segmentCount);
    }

    /// <summary>
    /// <see cref="TryBuildPlanFnc1(ReadOnlySpan{char}, EciMode, RmQRVersion, RmQREccLevel, Fnc1Mode, Span{ModeSegment}, out int)"/> from the table the version scan kept for the plan it chose
    /// (<see cref="TrySelectVersionFnc1(ReadOnlySpan{char}, in TextAnalysisResult, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?, Fnc1Mode, Span{byte}, out RmQRVersion, out bool, out bool, out int, out int)"/>), without running the program again.
    /// </summary>
    public static bool TryBuildPlanFnc1(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, Fnc1Mode fnc1, ReadOnlySpan<byte> table, int finalState, int plannedBits, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        if (!ModeSegmenter.ReconstructFnc1(text, table.Slice(0, text.Length * ModeSegmenter.Fnc1ParentBytesPerChar), finalState, segments, out segmentCount))
        {
            segmentCount = 0;
            return false;
        }

        return AcceptPlanFnc1(text, charset, version, eccLevel, fnc1, plannedBits, segments, ref segmentCount);
    }

    /// <summary>What follows the walk back of a plan under FNC1: the refusal of a plan the decoder would misread, the unit counts, and the re-measurement against the program and the capacity.</summary>
    private static bool AcceptPlanFnc1(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, Fnc1Mode fnc1, int plannedBits, Span<ModeSegment> segments, ref int segmentCount)
    {
        // The same refusal as without FNC1: a Byte run opened at a mid-content U+FEFF would lose it to the decoder.
        if (ModeSegmenter.HasBomRelocatedToARunStart(text, segments.Slice(0, segmentCount)))
        {
            segmentCount = 0;
            return false;
        }

        FillUnitCountsFnc1(text, charset, segments.Slice(0, segmentCount));

        var measuredBits = MeasurePlan(version, segments.Slice(0, segmentCount));
        Debug.Assert(measuredBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);
        if (measuredBits != plannedBits || measuredBits + (charset == EciMode.Default ? 0 : EciHeaderBits) + fnc1.RmQrHeaderBits > capacityBits)
        {
            segmentCount = 0;
            return false;
        }

        return true;
    }

    /// <summary><see cref="SelectSingle"/> under FNC1: the single-mode fit with the indicator's bits, throwing when nothing fits.</summary>
    public static RmQRVersion SelectSingleFnc1(in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1)
        => RmQRVersionSelector.SelectFnc1(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height, fnc1.RmQrHeaderBits);

    /// <summary><see cref="TrivialLowerBoundBits"/> under FNC1's character classes: a separator costs an Alphanumeric character, a literal percent the lesser of two of them and one byte.</summary>
    public static int TrivialLowerBoundBitsFnc1(ReadOnlySpan<char> text, EciMode charset)
        => (ModeSegmenter.CheapestSixthsFnc1(text, charset) + 5) / 6 + RmQRConstants.ModeIndicatorLength + MinCountBitsAny;

    /// <summary>
    /// Bytes of the table the scan under FNC1 keeps for the plan's build: the program's predecessors, <see cref="ModeSegmenter.Fnc1ParentBytesPerChar"/> a character, for a text the scan prices, and none for one it does not (all-Numeric, Kanji, empty or too long).
    /// </summary>
    public static int Fnc1TableLength(ReadOnlySpan<char> text, in TextAnalysisResult analysis)
        => analysis.EncodingMode is EncodingMode.Numeric or EncodingMode.Kanji || text.Length is 0 or > MaxPlannableChars ? 0 : text.Length * ModeSegmenter.Fnc1ParentBytesPerChar;

    /// <summary>
    /// <see cref="ModeSegmenter.FillUnitCounts"/> under FNC1: a Byte run's encoded byte count, an Alphanumeric run's characters plus its literal percents, a Numeric run's digits.
    /// </summary>
    private static void FillUnitCountsFnc1(ReadOnlySpan<char> text, EciMode charset, Span<ModeSegment> segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var chars = text.Slice(segment.Start, segment.Length);
            var units = segment.ModeIndex switch
            {
                1 => ModeSegmenter.AlphanumericUnitsFnc1(chars),
                2 => ModeSegmenter.ByteUnitCount(chars, charset),
                _ => segment.Length,
            };
            segments[i] = new ModeSegment(segment.ModeIndex, segment.Start, segment.Length, units);
        }
    }

    /// <summary>
    /// Whether the plan at <paramref name="version"/>'s widths fits its capacity, from the memo or a cost run. A run made with a <paramref name="table"/> leaves its predecessors there and names its widths in <paramref name="tableKey"/>, so the caller can tell whose plan the table holds.
    /// </summary>
    private static bool PlanFitsFnc1(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, int headerBits, Span<int> memoKeys, Span<int> memoCosts, ref int memoCount, Span<byte> table, ref int tableKey, ref int tableState, out int cost, out int key)
    {
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
            cost = ModeSegmenter.ComputeCostsFnc1(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, table, out var finalState);
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

        return cost + headerBits <= 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);
    }
}
