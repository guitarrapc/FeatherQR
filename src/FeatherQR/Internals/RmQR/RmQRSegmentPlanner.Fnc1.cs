using System.Buffers;
using System.Diagnostics;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// The version scan and the plan under FNC1 (specs/rmqr-encoder.md, FNC1): the indicator's bits (3 in first
/// position, 11 in second) join the ECI prefix in every capacity check, the single-mode ceiling is the fit with
/// those bits, the costs come from the FNC1 program (<see cref="ModeSegmenter.ComputeCostsFnc1"/>), whose Alphanumeric runs count a literal
/// <c>%</c> twice and never hold a separator pair, and no Kanji plan is priced. The scan keeps the trivial bound and
/// the memoised cost run of the scan without FNC1 and drops its floor filter, whose run counting reads the other
/// program's packed table. The route is cold, so the exact cost run can decide alone.
/// </summary>
internal static partial class RmQRSegmentPlanner
{
    /// <summary><see cref="SelectVersion"/> under FNC1, throwing what <see cref="RmQRVersionSelector.SelectFnc1"/> throws when nothing fits.</summary>
    public static RmQRVersion SelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, out bool useSegments)
    {
        if (TrySelectVersionFnc1(text, in analysis, eccLevel, requestedVersion, fitStrategy, height, fnc1, out var version, out useSegments))
            return version;

        return RmQRVersionSelector.SelectFnc1(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height, fnc1.RmQrHeaderBits);
    }

    /// <summary><see cref="TrySelectVersion"/> under FNC1. Argument errors still throw, in the same order and with the same messages.</summary>
    public static bool TrySelectVersionFnc1(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Fnc1Mode fnc1, out RmQRVersion selected, out bool useSegments)
    {
        useSegments = false;
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
        var trivialBits = TrivialLowerBoundBitsFnc1(text, charset) + headerBits;

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

            if (PlanFitsFnc1(text, charset, candidate, eccLevel, headerBits, memoKeys, memoCosts, ref memoCount))
            {
                useSegments = true;
                selected = candidate;
                return true;
            }
        }

        selected = single;
        return hasSingle;
    }

    /// <summary><see cref="TryBuildPlan"/> under FNC1: the FNC1 program's plan, its Alphanumeric runs counting their literal percents twice, and the indicator's bits in the capacity check.</summary>
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

    private static bool PlanFitsFnc1(ReadOnlySpan<char> text, EciMode charset, RmQRVersion version, RmQREccLevel eccLevel, int headerBits, Span<int> memoKeys, Span<int> memoCosts, ref int memoCount)
    {
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
            cost = ModeSegmenter.ComputeCostsFnc1(text, charset, RmQRConstants.ModeIndicatorLength, cciNumeric, cciAlnum, cciByte, default, out _);
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
