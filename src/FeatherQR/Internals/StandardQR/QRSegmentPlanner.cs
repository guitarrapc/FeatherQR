using System.Buffers;
using System.Diagnostics;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Mixed-mode segmentation for <see cref="QRSegmentation.Optimal"/>: the split of the content into Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a given version, and the version fit that follows from it.
/// </summary>
/// <remarks>
/// The cost model and reconstruction are <see cref="ModeSegmenter"/>, shared with the Micro QR and rMQR planners; what lives here is Standard QR's version scan.
/// That scan is cheap by construction: character count indicator widths are constant within the three ISO/IEC 18004 version bands (1-9 / 10-26 / 27-40), so the optimal cost is computed at most once per band, and the single-mode fit caps the scan so a plan is produced only when it lowers the version.
/// Design rationale, bounds and measurements: specs/standardqr-encoder.md, "Mixed-mode segmentation".
/// </remarks>
internal static class QRSegmentPlanner
{
    /// <summary>
    /// Content length up to which a plan buffer fits the stack (<see cref="MaxStackSegments"/> runs, 512 bytes); longer content rents a text-length buffer, which no plan can outgrow because every run holds at least one character.
    /// </summary>
    public const int MaxStackSegments = 64;

    /// <summary>
    /// Longest content any Standard QR symbol can hold, in characters (7089 digits at version 40-L, an exact fit: 2363 groups × 10 bits + 4 + 14 = 23,648 bits).
    /// No mixed plan can beat it: mixing only adds headers to a denser-per-character mode that does not exist.
    /// </summary>
    /// <remarks>
    /// A rejection rule, not only a work cap: a mixed plan can encode content no single mode holds, so this is what declares longer content impossible.
    /// The margin is 4 bits (7090 digits cost 23,652 against 23,648), so re-derive it rather than nudge it if the capacity tables change.
    /// Pinned by <c>QRSegmentPlannerUnitTest</c>.
    /// </remarks>
    public const int MaxPlannableChars = 7089;

    /// <summary>Standard QR mode indicator width (ISO/IEC 18004 7.4.1).</summary>
    private const int ModeIndicatorBits = 4;

    /// <summary>Standard QR ECI prefix: 4-bit mode indicator 0111 plus a one-byte assignment designator.</summary>
    private const int EciHeaderBits = ModeIndicatorBits + 8;

    /// <summary>Narrowest count indicator of any mode at any version (Byte at versions 1-9); pinned by QRSegmentPlannerUnitTest.</summary>
    private const int MinCountBitsAny = 8;

    /// <summary>
    /// Whether a mixed plan could ever cost less than the single-mode stream for content the analyser put in <paramref name="singleMode"/>.
    /// </summary>
    /// <remarks>
    /// All-Numeric content is already at the optimum: no mode prices a digit below Numeric, and every extra run adds a header, so one run is it.
    /// Numeric only, since an Alphanumeric or Byte payload can still hide a digit run worth splitting off.
    /// Every caller skips the whole cost run on a <c>false</c>, which changes the emitted stream if the rule is ever wrong, so <c>QRSegmentPlannerUnitTest</c> pins it against the program itself.
    /// </remarks>
    public static bool CanPlanBeatSingleMode(EncodingMode singleMode) => singleMode != EncodingMode.Numeric;

    /// <summary>Shortest run of digits that could repay the one mode header splitting it out adds, at the version band whose headers are narrowest.</summary>
    /// <remarks>
    /// Splitting a run out of a surrounding Byte run is cheapest when the run sits at one end of the content, where it adds its own mode and count indicator and nothing else; versions 1 to 9 make that 4 + 10 bits.
    /// Three digits save 24 - 10 = 14 against Byte, which only matches it; four save 32 - 14 = 18 and clear it.
    /// Splitting digits out of an Alphanumeric run instead needs seven, so four is the binding number. Re-derive rather than nudge if the count indicator widths change.
    /// </remarks>
    private const int ShortestPayingNumericRun = 4;

    /// <summary>Shortest run in the alphanumeric alphabet that could repay the one mode header splitting it out of a Byte run adds, at versions 1 to 9 (4 + 9 bits).</summary>
    /// <remarks>Five characters save 40 - 28 = 12 and fall short; six save 48 - 33 = 15 and clear it.</remarks>
    private const int ShortestPayingAlnumRun = 6;

    /// <summary>
    /// Whether a mixed plan could cost less than one run, from the longest run of each denser mode the content holds.
    /// </summary>
    /// <remarks>
    /// A plan is only cheaper than one run when some run of it is in a mode denser than the whole content's, and such a run has to repay at least the one mode header it adds, so content whose densest stretches are shorter than that cannot be improved by any plan at any version.
    /// Generous on purpose: saying yes costs a cost run that finds nothing, saying no when a plan would have won would change what the library emits, so <c>PlanCouldWinTest</c> holds this direction against the program itself.
    /// </remarks>
    public static bool PlanCouldBeatSingleMode(int longestNumericRun, int longestAlnumRun)
        => longestNumericRun >= ShortestPayingNumericRun || longestAlnumRun >= ShortestPayingAlnumRun;

    /// <summary>Shortest run of digits whose split out of a Byte run can tie with leaving it in: three digits save 24 - 10 = 14 bits, the mode and count indicator of versions 1 to 9.</summary>
    private const int ShortestTyingNumericRun = 3;

    /// <summary>
    /// Whether the minimal plan of Byte content is one Byte run whatever the tie-breaks, from the longest run of each denser mode it holds.
    /// </summary>
    /// <remarks>
    /// One below <see cref="PlanCouldBeatSingleMode"/> for digits, since the program gives a tie to the split. Two digits save 9 bits and five alphanumerics 12, against a header of at least 13, so every run outside Byte costs strictly more than the bytes it replaces, at every version.
    /// Holds for any piece of the content that has a character outside the alphanumeric alphabet, which is every piece longer than five characters.
    /// </remarks>
    public static bool PlanIsOneByteRun(int longestNumericRun, int longestAlnumRun)
        => longestNumericRun < ShortestTyingNumericRun && longestAlnumRun < ShortestPayingAlnumRun;

    /// <summary>
    /// Version fit for mixed-mode segmentation, restricted to <paramref name="minVersion"/> through <paramref name="maxVersion"/>.
    /// Returns the version to encode at and whether a mixed-mode plan is what makes it fit; when <paramref name="useSegments"/> is false the caller emits the ordinary single-mode stream, bit-identical to <see cref="QRSegmentation.Single"/>.
    /// <c>false</c> means the content fits neither one mode nor a mixed plan in the window; the caller owns the error.
    /// </summary>
    /// <remarks>
    /// The scan needs no floor/ceiling machinery: count indicator widths are constant within the three version bands, so the optimal cost is computed at most once per band (three O(n) runs in the worst case, no reconstruction table), and capacity grows monotonically inside a band, so the first version that holds the band cost is the smallest.
    /// </remarks>
    public static bool TrySelectVersion(ReadOnlySpan<char> text, in TextAnalysisResult analysis, QREccLevel eccLevel, int minVersion, int maxVersion, out int selected, out bool useSegments)
    {
        useSegments = false;
        var charset = analysis.EciMode;

        // Ceiling: the version single-mode encoding lands on, when there is one. When
        // there is none the scan runs to the window's end instead of stopping early,
        // because content that overflows every version in one mode can still fit once
        // the modes are mixed (3000 letters followed by 4000 digits is 7000 Byte-mode
        // characters, far over the largest Byte capacity, but fits as two runs).
        var hasSingle = QRCodeGenerator.TryGetVersionInRange(analysis.DataLength, analysis.EncodingMode, eccLevel, charset, utf8BOM: false, minVersion, maxVersion, out var single);

        if (!CanPlanBeatSingleMode(analysis.EncodingMode))
        {
            selected = single;
            return hasSingle;
        }

        // Unplannable content skips the scan before any cost run, which is what keeps
        // a pathological length from paying for one.
        if (text.Length is 0 or > MaxPlannableChars)
        {
            selected = single;
            return hasSingle;
        }

        var eciBits = charset == EciMode.Default ? 0 : EciHeaderBits;

        // Strictly below the single-mode fit: at that version the single-mode stream
        // already fits, and emitting it unchanged is the blast-radius bound the
        // feature is designed around.
        var top = hasSingle ? Math.Min(single - 1, maxVersion) : maxVersion;
        if (top < minVersion)
        {
            selected = single;
            return hasSingle;
        }

        // One O(n) pass pricing each character at the cheapest rate any mode could
        // give it: a lower bound on any plan at any version, so it may only reject.
        // It is what keeps Optimal roughly free on content no split can shrink — a
        // candidate only pays for a cost run when it could hold at least this much.
        var trivialBits = TrivialLowerBoundBits(text, charset) + eciBits;

        var band = -1;
        var bandCost = 0;
        for (var version = minVersion; version <= top; version++)
        {
            var capacityBits = QRCodeConstants.GetEccInfo(version, eccLevel).TotalDataCodewords * 8;
            if (capacityBits < trivialBits)
                continue; // no split of this content could fit, whatever the plan

            var candidateBand = version < 10 ? 0 : version < 27 ? 1 : 2;
            if (candidateBand != band)
            {
                band = candidateBand;
                bandCost = ModeSegmenter.ComputeCosts(
                    text, charset, ModeIndicatorBits,
                    EncodingMode.Numeric.GetCountIndicatorLength(version),
                    EncodingMode.Alphanumeric.GetCountIndicatorLength(version),
                    EncodingMode.Byte.GetCountIndicatorLength(version),
                    default, out _);
            }

            if (bandCost + eciBits <= capacityBits)
            {
                useSegments = true;
                selected = version;
                return true;
            }
        }

        selected = single;
        return hasSingle;
    }

    /// <summary>
    /// A lower bound on any plan at any version, in payload bits, computed in one O(n) pass with no dynamic programming table: each character priced at the cheapest rate any mode could give it, plus the cheapest possible single segment header.
    /// </summary>
    /// <remarks>
    /// Deliberately crude: it answers "could a split reach a better version at all" before any cost run.
    /// Loose where a split is worth searching, tight where it is not.
    /// Its blind spot is finely alternating content, which looks far cheaper than it is because seeing that switching modes every character never pays requires modelling the switch cost — that is the dynamic program itself.
    /// </remarks>
    public static int TrivialLowerBoundBits(ReadOnlySpan<char> text, EciMode charset)
        => (ModeSegmenter.CheapestSixths(text, charset) + 5) / 6 + ModeIndicatorBits + MinCountBitsAny;

    /// <summary>
    /// Builds the minimal-cost plan for <paramref name="version"/> into <paramref name="segments"/>.
    /// Returns false when the content is unplannable, the plan needs more runs than the caller lent room for, the plan would be misread on decode (a relocated byte order mark), or the exact re-costed stream would not fit; the caller answers all four by falling back to the single-mode stream.
    /// </summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, int version, QREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount)
        => TryBuildPlan(text, charset, version, eccLevel, segments, out segmentCount, out _);

    /// <summary>The same, handing over what the plan measures (no ECI prefix), for a caller with a header of its own to add to it.</summary>
    public static bool TryBuildPlan(ReadOnlySpan<char> text, EciMode charset, int version, QREccLevel eccLevel, Span<ModeSegment> segments, out int segmentCount, out int planBits)
    {
        segmentCount = 0;
        planBits = 0;
        if (text.Length is 0 or > MaxPlannableChars)
            return false;

        var cciNumeric = EncodingMode.Numeric.GetCountIndicatorLength(version);
        var cciAlnum = EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
        var cciByte = EncodingMode.Byte.GetCountIndicatorLength(version);

        var parentLength = text.Length * ModeSegmenter.ParentBytesPerChar;
        byte[]? rented = null;
        Span<byte> parents = parentLength <= ModeSegmenter.MaxStackParents
            ? stackalloc byte[ModeSegmenter.MaxStackParents]
            : (rented = ArrayPool<byte>.Shared.Rent(parentLength));
        int plannedBits;
        try
        {
            var window = parents.Slice(0, parentLength);
            plannedBits = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, window, out var finalState);
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

        return TryAcceptPlan(text, charset, version, eccLevel, plannedBits, segments, ref segmentCount, out planBits);
    }

#if NET8_0_OR_GREATER
    /// <summary>Fewest symbols of a set worth planning together; below it each plans alone.</summary>
    public const int MinLaneChunks = 3;

    /// <summary>The program of <see cref="TryBuildPlan(ReadOnlySpan{char}, EciMode, int, QREccLevel, Span{ModeSegment}, out int, out int)"/> for up to <see cref="ModeSegmenter.Lanes"/> chunks of one text at once; see <see cref="ModeSegmenter.ComputeCostsLanes"/>.</summary>
    public static void PlanChunks(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int version, Span<byte> table, Span<int> costs, Span<int> finalStates)
        => ModeSegmenter.ComputeCostsLanes(text, starts, lengths, charset, ModeIndicatorBits,
            EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version),
            table, costs, finalStates);

    /// <summary>The rest of the plan builder for one chunk <see cref="PlanChunks"/> planned: the walk back over its lane, then what every plan goes through.</summary>
    public static bool TryBuildPlanFromLane(ReadOnlySpan<char> chunk, EciMode charset, int version, QREccLevel eccLevel, ReadOnlySpan<byte> table, int lane, int finalState, int plannedBits, Span<ModeSegment> segments, out int segmentCount, out int planBits)
    {
        planBits = 0;
        if (!ModeSegmenter.ReconstructLane(chunk.Length, table, lane, finalState, segments, out segmentCount))
        {
            segmentCount = 0;
            return false;
        }

        return TryAcceptPlan(chunk, charset, version, eccLevel, plannedBits, segments, ref segmentCount, out planBits);
    }
#endif

    /// <summary>
    /// What follows the walk back, for a plan of <paramref name="plannedBits"/>: the unit counts, the refusal of a plan the decoder would misread, and the re-measurement against the program and the capacity.
    /// </summary>
    private static bool TryAcceptPlan(ReadOnlySpan<char> text, EciMode charset, int version, QREccLevel eccLevel, int plannedBits, Span<ModeSegment> segments, ref int segmentCount, out int planBits)
    {
        // Re-cost the reconstructed plan from the byte counts the encoder will
        // actually emit. Disagreeing with the dynamic programming cost model is a bug
        // in the model (the version scan would have compared the wrong number against
        // a capacity), so it fails loudly in Debug and rejects the plan in Release
        // rather than becoming a stream that overruns the data codewords.
        planBits = PricePlan(text, charset, version, segments.Slice(0, segmentCount));
        Debug.Assert(planBits < 0 || planBits == plannedBits, "the reconstructed plan must cost exactly what the dynamic program computed");

        var capacityBits = QRCodeConstants.GetEccInfo(version, eccLevel).TotalDataCodewords * 8;
        if (planBits != plannedBits || planBits + (charset == EciMode.Default ? 0 : EciHeaderBits) > capacityBits)
        {
            // A plan the decoder would misread, a model that disagreed, or a version that
            // simply cannot hold the plan (a legitimate answer for a caller that asked
            // about a specific version).
            segmentCount = 0;
            planBits = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fills each run with the value its count indicator carries and returns what the plan measures, in one pass over the runs; -1 for a plan that relocates a mid-content U+FEFF to the start of a Byte run (see <see cref="ModeSegmenter.HasBomRelocatedToARunStart"/>), which the caller answers with the single-mode stream that keeps it interior.
    /// </summary>
    private static int PricePlan(ReadOnlySpan<char> text, EciMode charset, int version, Span<ModeSegment> segments)
    {
        var numericHeader = ModeIndicatorBits + EncodingMode.Numeric.GetCountIndicatorLength(version);
        var alnumHeader = ModeIndicatorBits + EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
        var byteHeader = ModeIndicatorBits + EncodingMode.Byte.GetCountIndicatorLength(version);
        var total = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            int units = segment.Length;
            switch (segment.ModeIndex)
            {
                case 0:
                    total += numericHeader + ModeSegmenter.PayloadBits(EncodingMode.Numeric, units);
                    break;
                case 1:
                    total += alnumHeader + ModeSegmenter.PayloadBits(EncodingMode.Alphanumeric, units);
                    break;
                default:
                    if (segment.Start > 0 && text[segment.Start] == (char)0xFEFF)
                        return -1;
                    units = ModeSegmenter.ByteUnitCount(text.Slice(segment.Start, segment.Length), charset);
                    total += byteHeader + units * 8;
                    break;
            }
            segments[i] = new ModeSegment(segment.ModeIndex, segment.Start, segment.Length, units);
        }
        return total;
    }

    /// <summary>Exact bit cost of a plan (excluding any ECI prefix): per run, mode indicator + count indicator + payload.</summary>
    public static int MeasurePlan(int version, ReadOnlySpan<ModeSegment> segments)
    {
        var total = 0;
        foreach (var segment in segments)
        {
            var mode = segment.Mode;
            total += ModeIndicatorBits + mode.GetCountIndicatorLength(version) + ModeSegmenter.PayloadBits(mode, segment.UnitCount);
        }
        return total;
    }

    /// <summary>
    /// Named entry point for <c>QRSegmentPlannerUnitTest</c>: the minimal payload bits (no ECI prefix) at explicit count indicator widths, which is the value the version scan compares against a data capacity.
    /// </summary>
    public static int MinimumPayloadBits(ReadOnlySpan<char> text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte)
        => ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, default, out _);
}
