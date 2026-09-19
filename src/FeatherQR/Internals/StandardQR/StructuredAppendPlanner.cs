using System.Diagnostics;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Splits text across Structured Append symbols: the fewest symbols the version cap allows, all at one version, balanced so the fullest symbol is as empty as it can be.
/// </summary>
/// <remarks>
/// Every symbol pays the 20-bit header and, when the set carries a charset, its own ECI header, so a symbol's payload budget is the version's data capacity less those; a chunk's cost is what the single-mode stream needs, or under <see cref="QRSegmentation.Optimal"/> the cheaper of that and the minimal mixed plan.
/// Cost is monotone in the chunk's length (a longer prefix never plans cheaper), so the longest chunk that fits a budget is well defined and found in one forward step (<see cref="LongestChunkEnd"/>), and a greedy walk at a budget gives the symbol count that budget needs. The three steps of the split are three searches over that count. Under Optimal a walk is a pass of the segmentation program, so one walk near the plan's floor settles the count, the versions of its count indicator band and the ceiling of the budget search at once.
/// A walk stops once it passes the count it is asked about, and a lower bound on what any split costs (<see cref="CanHold"/>) keeps the searches off versions and budgets that cannot hold the count.
/// Splits fall on <c>char</c> boundaries and never inside a surrogate pair. Design and the rules behind it: specs/standardqr-encoder.md.
/// </remarks>
internal static partial class StructuredAppendPlanner
{
    /// <summary>Mode indicator, position, count and parity (ISO/IEC 18004 Structured Append).</summary>
    public const int HeaderBits = 20;

    /// <summary>The 4-bit count field holds at most sixteen symbols.</summary>
    public const int MaxSymbols = 16;

    private const int ModeIndicatorBits = 4;

    /// <summary>Narrowest count indicator of any mode at any version (Byte at versions 1-9); pinned by <c>QRSegmentPlannerUnitTest</c>.</summary>
    private const int MinCountIndicatorBits = 8;

    private const int Impossible = int.MaxValue;

    /// <summary>
    /// How far above the plan's floor (the headers plus the count's share of the whole text's minimal plan) the walk that settles the count is placed.
    /// </summary>
    /// <remarks>
    /// The balanced budget sits 3 to 22 bits above that floor on every content measured, periodic or not: what separates them is one chunk's rounding and a run header or two at the cuts, not the content. 31 keeps the bracket under it at five probes.
    /// Not a correctness parameter: a walk that fails at it falls back to the walk at the capacity, and the search runs from there.
    /// </remarks>
    private const int FloorMarginBits = 31;

    private const char ByteOrderMark = ModeSegmenter.ByteOrderMark;

    /// <summary>
    /// The walks taken on this thread, scalar and eight budgets at once: what the search spends, which no plan shows, for tests that pin where it spends it. One increment a walk of thousands of steps.
    /// </summary>
    [ThreadStatic]
    internal static int ScalarWalks, LaneBatches;

    /// <summary>
    /// Plans the split. <paramref name="chunkEnds"/> receives the end offset of each chunk (at least <see cref="MaxSymbols"/> entries); <paramref name="chunkCount"/> is 1 when the text fits one symbol within the range even with the set header every chunk is priced with, in which case nothing else is planned. Whether the text fits one symbol without that header, which is what makes it not a set, is the caller's to ask, the way <c>Create</c> asks it, of a text this splits or refuses.
    /// Returns <c>false</c> when the text needs more than sixteen symbols at <paramref name="maxVersion"/>, or some single character cannot fit a symbol there.
    /// </summary>
    public static bool TryPlan(ReadOnlySpan<char> text, QREccLevel eccLevel, EciMode charset, EncodingMode singleMode, bool utf8Bom, QRSegmentation segmentation, int minVersion, int maxVersion, Span<int> chunkEnds, out int chunkCount, out int version, out int budgetBits)
        => TryPlan(text, eccLevel, charset, singleMode, utf8Bom, segmentation, minVersion, maxVersion, chunkEnds, out chunkCount, out version, out budgetBits, allowLanes: true);

    /// <summary>
    /// <see cref="TryPlan(ReadOnlySpan{char}, QREccLevel, EciMode, EncodingMode, bool, QRSegmentation, int, int, Span{int}, out int, out int, out int)"/> with the choice of walking several budgets at once left to the caller; the plan is the same either way, which is what <c>StructuredAppendLaneWalkTest</c> holds it to.
    /// </summary>
    internal static bool TryPlan(ReadOnlySpan<char> text, QREccLevel eccLevel, EciMode charset, EncodingMode singleMode, bool utf8Bom, QRSegmentation segmentation, int minVersion, int maxVersion, Span<int> chunkEnds, out int chunkCount, out int version, out int budgetBits, bool allowLanes)
        => TryPlan(text, eccLevel, charset, singleMode, utf8Bom, segmentation, minVersion, maxVersion, chunkEnds, out chunkCount, out version, out budgetBits, out _, out _, allowLanes);

    /// <summary>
    /// The same, handing the writer what the pass over the text's dense runs also settles: <paramref name="oneRunPlans"/> says the minimal plan of every chunk that has a character outside the alphanumeric alphabet is one Byte run, which is its single-mode stream, so such a chunk needs no plan of its own. <paramref name="mayBeOneSymbol"/> says whether a symbol of the largest version could hold the text without the set header, by the measure the search ran on (the whole text's plan, or its single-mode stream): false rules the one symbol out, true leaves it to the caller to ask as <c>Create</c> does.
    /// </summary>
    internal static bool TryPlan(ReadOnlySpan<char> text, QREccLevel eccLevel, EciMode charset, EncodingMode singleMode, bool utf8Bom, QRSegmentation segmentation, int minVersion, int maxVersion, Span<int> chunkEnds, out int chunkCount, out int version, out int budgetBits, out bool oneRunPlans, out bool mayBeOneSymbol, bool allowLanes)
    {
        chunkCount = 0;
        version = 0;
        budgetBits = 0;
        oneRunPlans = false;
        mayBeOneSymbol = false;

        // Nothing to split: the single-symbol path encodes an empty Byte segment.
        if (text.IsEmpty)
        {
            chunkCount = 1;
            version = maxVersion;
            return true;
        }

        // What no split can cost less than, priced once; every search below is gated on it.
        var cheapest = CheapestPayloadBits(text, charset);

        // A chunk's cost is the cheaper of its single-mode stream and its minimal plan, and the
        // two are the same number for content holding no run dense enough to repay a mode
        // header. Deciding that once, in one pass, is what keeps the searches below off the
        // segmentation program, whose every probe is a pass of its own: the split it would find
        // is the split the closed-form cost finds, so the searches run as Single and the symbols
        // are still written exactly as the caller asked.
        var searched = segmentation == QRSegmentation.Optimal && CanPlanHelp(text, singleMode, out oneRunPlans)
            ? QRSegmentation.Optimal
            : QRSegmentation.Single;

        var largest = Capacity(maxVersion, eccLevel);
        if (!CanHold(largest, MaxSymbols, cheapest, charset))
            return false;

        // The minimal plan for the whole text, per count indicator band, taken on first use and
        // shared by the count, the version scan and the budget search.
        Span<int> wholeTextPlans = stackalloc int[3];
        wholeTextPlans.Fill(-1);
        // The walk that last held the count: its split is the answer's when the search ends on its
        // budget; a failing probe overwrites the caller's buffer, so it is kept aside.
        Span<int> settledEnds = stackalloc int[MaxSymbols];
        var settledBudget = -1;
        var settledCount = 0;
        var settledBand = -1;
        // The walk that last failed to hold failedCount chunks at failedBand's widths, and its first
        // failedCount ends: a floor for the budget, and with the settled walk what a probe between
        // the two can skip.
        Span<int> failedEnds = stackalloc int[MaxSymbols];
        var failedBudget = -1;
        var failedBand = -1;
        var failedCount = 0;
        var setHeaders = HeaderBits + charset.GetStandardQrHeaderBits();
        // Several budgets walked at once, where that pays: not under a byte order mark, whose
        // first chunk is priced by its own rule.
        var lanes = allowLanes && searched == QRSegmentation.Optimal && !(utf8Bom && charset == EciMode.Utf8);

        // Fewest symbols, reached at the largest version. Under Optimal a walk is a pass of the
        // segmentation program, so the count is settled by one walk placed where it also serves
        // the two searches after it, instead of a walk at the capacity.
        var count = -1;
        if (searched == QRSegmentation.Optimal)
        {
            // One single-mode stream that fits is the walk's own closed form; the program is never asked.
            var bom = utf8Bom && charset == EciMode.Utf8;
            if (text.Length <= QRSegmentPlanner.MaxPlannableChars
                && SingleModeLength(text, charset, maxVersion, bom, largest - setHeaders - ModeIndicatorBits, out _, out _) == text.Length)
            {
                chunkEnds[0] = text.Length;
                chunkCount = 1;
                version = maxVersion;
                return true;
            }

            // The chunks' plans cost at least the whole text's plan between them, so the count is
            // at least fewest; a walk near the plan's floor that holds fewest chunks therefore
            // settles it, and measured balanced budgets sit within FloorMarginBits of that floor.
            var whole = WholeTextPlanBits(text, charset, maxVersion, wholeTextPlans);
            // No stream of the text costs less than its plan, so a plan the largest symbol does not hold
            // without the set header rules the one symbol out.
            mayBeOneSymbol = whole + charset.GetStandardQrHeaderBits() <= largest;
            var perSymbol = largest - setHeaders;
            if (whole < ModeSegmenter.Unreachable && perSymbol > 0)
            {
                var fewest = (int)(((long)whole + perSymbol - 1) / perSymbol);
                if (fewest > MaxSymbols)
                    return false;
                if (fewest <= 1)
                {
                    // One chunk's plan is the whole text's, unless a byte order mark forces its
                    // single-mode stream or it is past what any plan covers.
                    if (!bom && text.Length <= QRSegmentPlanner.MaxPlannableChars && whole + setHeaders <= largest)
                    {
                        chunkEnds[0] = text.Length;
                        count = 1;
                    }
                }
                else
                {
                    // Attempted only with headroom: when the bound's count leaves each symbol less than
                    // two margins of slack, packing losses usually put it out of reach and the walk
                    // would be wasted, so the walk at the capacity counts as it always did.
                    var floor = setHeaders + (whole + fewest - 1) / fewest;
                    var target = floor + FloorMarginBits;
                    if (target + FloorMarginBits < largest)
                    {
                        int unusedLow = floor, unusedHigh = largest;
                        if (lanes && TryNarrowWithLanes(text, charset, maxVersion, floor, largest, fewest, ref unusedLow, ref unusedHigh, settledEnds, ref settledBudget, ref settledCount, failedEnds, ref failedBudget, fromFloor: true))
                        {
                            // Eight walks from the floor at once: the cheapest that holds the bound's
                            // count settles it, and the one below it is the failed neighbour.
                            if (settledBudget >= 0)
                            {
                                count = settledCount;
                                settledBand = Band(maxVersion);
                            }
                        }
                        else
                        {
                            var probed = CountChunks(text, charset, utf8Bom, searched, maxVersion, target, fewest, chunkEnds);
                            if (probed <= fewest)
                            {
                                count = probed;
                                chunkEnds.Slice(0, probed).CopyTo(settledEnds);
                                settledBudget = target;
                                settledCount = probed;
                                settledBand = Band(maxVersion);
                            }
                            else if (probed == fewest + 1)
                            {
                                chunkEnds.Slice(0, fewest).CopyTo(failedEnds);
                                failedBudget = target;
                            }
                        }

                        if (failedBudget >= 0)
                        {
                            failedBand = Band(maxVersion);
                            failedCount = fewest;
                        }
                    }
                }
            }
        }

        // Searched as single-mode streams, the one symbol is its stream's closed form, which reads the text: not one too long
        // for a symbol at the best a mode does, ten bits on three characters.
        if (searched == QRSegmentation.Single)
            mayBeOneSymbol = text.Length * 10L <= largest * 3L
                && SingleModeLength(text, charset, maxVersion, utf8Bom && charset == EciMode.Utf8, largest - charset.GetStandardQrHeaderBits() - ModeIndicatorBits, out _, out _) == text.Length;

        if (count < 0)
            count = CountChunks(text, charset, utf8Bom, searched, maxVersion, largest, MaxSymbols, chunkEnds);
        if (count > MaxSymbols)
            return false;
        if (count == 1)
        {
            chunkCount = 1;
            version = maxVersion;
            return true;
        }

        // Smallest version that still holds that many; a version the bounds rule out is not walked.
        // Under Optimal a walk is a pass over the text, so a version the rate bound admits is also
        // checked against the whole text's minimal plan, which on mixed content is thousands of
        // bits nearer the truth and turns away the versions just below the answer; and a version
        // whose capacity holds the settled walk's budget at the same count indicator widths holds
        // that very split, so it is not walked either.
        version = maxVersion;
        for (var candidate = minVersion; candidate < maxVersion; candidate++)
        {
            var capacity = Capacity(candidate, eccLevel);
            if (!CanHold(capacity, count, cheapest, charset))
                continue;
            if (searched == QRSegmentation.Optimal)
            {
                if (!CanHoldPlanned(capacity, count, WholeTextPlanBits(text, charset, candidate, wholeTextPlans), charset))
                    continue;
                if (settledBudget >= 0 && settledBand == Band(candidate) && settledBudget <= capacity)
                {
                    version = candidate;
                    break;
                }
            }
            if (CountChunks(text, charset, utf8Bom, searched, candidate, capacity, count, chunkEnds) <= count)
            {
                version = candidate;
                break;
            }
        }

        // Smallest per-symbol budget at that version that still holds that many: the balanced split.
        // The floor is the bound's: the count fits at this version, so its average share is at most the capacity.
        var low = MinimumBudget(count, cheapest, charset);
        var high = Capacity(version, eccLevel);
        if (searched == QRSegmentation.Optimal)
        {
            // Under Optimal a probe is a pass, so the bracket is the exact cost model's: below, a
            // split costs at least the whole text's plan, so its fullest chunk is at least the
            // average of that plus the headers every symbol pays; above, the settled walk's budget.
            var answerBand = Band(version);
            var wholeText = WholeTextPlanBits(text, charset, version, wholeTextPlans);
            if (wholeText < ModeSegmenter.Unreachable)
                low = Math.Max(low, setHeaders + (wholeText + count - 1) / count);

            // A failed walk is a floor for its own count and band only.
            if (failedBand != answerBand || failedCount != count)
                failedBudget = -1;
            else if (failedBudget >= low)
                low = failedBudget + 1;

            if (settledBudget >= 0 && settledBand == answerBand && settledBudget <= high)
            {
                high = settledBudget;
            }
            else
            {
                settledBudget = -1;

                // No walk has bracketed this band yet: the bracket opens from the floor, where the
                // answer is, not from the middle of one that ends at the capacity; in one batch of
                // lanes where they pay, else by probes in widening steps. Content whose characters
                // are wide (a surrogate pair is 32 bits that cannot be cut) sits 32 to 47 bits above
                // its floor, so the second step keeps the first margin; each step that fails raises
                // the floor.
                if (lanes)
                    TryNarrowWithLanes(text, charset, version, low, high, count, ref low, ref high, settledEnds, ref settledBudget, ref settledCount, failedEnds, ref failedBudget, fromFloor: true, ceilingHolds: true);

                var margin = FloorMarginBits;
                var widened = false;
                while (settledBudget < 0 && low + margin < high)
                {
                    var target = low + margin;
                    var probed = CountChunks(text, charset, utf8Bom, searched, version, target, count, chunkEnds);
                    if (probed <= count)
                    {
                        high = target;
                        chunkEnds.Slice(0, probed).CopyTo(settledEnds);
                        settledBudget = target;
                        settledCount = probed;
                        break;
                    }

                    if (probed == count + 1)
                    {
                        chunkEnds.Slice(0, count).CopyTo(failedEnds);
                        failedBudget = target;
                        failedBand = answerBand;
                        failedCount = count;
                    }
                    low = target + 1;
                    if (widened)
                        margin = margin * 3 + 2;
                    widened = true;
                }
            }
        }

        // Under Optimal the rest of the bracket is walked eight budgets at a time, each batch leaving
        // at most the gap between two of its budgets; what is left of it is bisected.
        if (lanes)
        {
            while (high - low >= 2
                && TryNarrowWithLanes(text, charset, version, low, high, count, ref low, ref high, settledEnds, ref settledBudget, ref settledCount, failedEnds, ref failedBudget))
            {
            }
        }

        while (low < high)
        {
            var middle = low + (high - low) / 2;

            // A chunk's end is monotone in the budget, so from one start a walk between a failed
            // budget and a held one ends its chunk between theirs: the leading chunks those two
            // walks share are this probe's too, and it resumes after them. No chunk's cost is needed.
            var shared = searched == QRSegmentation.Optimal
                ? SharedChunks(settledEnds, settledBudget, settledCount, failedEnds, failedBudget, count, middle)
                : 0;
            var start = 0;
            if (shared > 0)
            {
                settledEnds.Slice(0, shared).CopyTo(chunkEnds);
                start = settledEnds[shared - 1];
            }

            var probed = CountChunks(text, charset, utf8Bom, searched, version, middle, count, chunkEnds, shared, start);
            if (probed <= count)
            {
                high = middle;
                chunkEnds.Slice(0, probed).CopyTo(settledEnds);
                settledBudget = middle;
                settledCount = probed;
            }
            else
            {
                low = middle + 1;
                if (searched == QRSegmentation.Optimal && probed == count + 1)
                {
                    chunkEnds.Slice(0, count).CopyTo(failedEnds);
                    failedBudget = middle;
                }
            }
        }

        budgetBits = low;
        if (settledBudget == low)
        {
            settledEnds.Slice(0, settledCount).CopyTo(chunkEnds);
            chunkCount = settledCount;
            return true;
        }

        // No probe settled it: the answer is the ceiling the bracket started from.
        chunkCount = CountChunks(text, charset, utf8Bom, searched, version, low, count, chunkEnds);
        return true;
    }

    /// <summary>Data capacity of a symbol at this version and level, in bits.</summary>
    public static int Capacity(int version, QREccLevel eccLevel) => QRCodeConstants.GetEccInfo(version, eccLevel).TotalDataCodewords * 8;

    /// <summary>
    /// Payload bits no split of the text can go below, at any version and under either segmentation: every character priced at the cheapest rate any mode gives it.
    /// Additive over chunks, since a split never cuts a surrogate pair, so it bounds a whole set as it bounds one symbol.
    /// </summary>
    public static int CheapestPayloadBits(ReadOnlySpan<char> text, EciMode charset) => (ModeSegmenter.CheapestSixths(text, charset) + 5) / 6;

    /// <summary>
    /// Whether <paramref name="count"/> symbols of <paramref name="capacityBits"/> could hold the text at all: each pays the Structured Append header, the set's ECI header, a mode indicator and the narrowest count indicator, and the payloads sum to at least <paramref name="cheapestPayloadBits"/>.
    /// A lower bound, so it only rejects; the walk decides the rest.
    /// </summary>
    public static bool CanHold(int capacityBits, int count, int cheapestPayloadBits, EciMode charset)
        => (long)count * (capacityBits - ChunkFloorBits(charset)) >= cheapestPayloadBits;

    /// <summary>The smallest per-symbol budget <see cref="CanHold"/> admits for the count: the floor plus the average share of the cheapest payload.</summary>
    private static int MinimumBudget(int count, int cheapestPayloadBits, EciMode charset)
        => ChunkFloorBits(charset) + (cheapestPayloadBits + count - 1) / count;

    /// <summary>
    /// Whether <paramref name="count"/> symbols of <paramref name="capacityBits"/> could hold the text, judged by the minimal plan for the whole of it at that version's count indicator widths.
    /// </summary>
    /// <remarks>
    /// The segment plans of a split concatenate into one plan for the whole text at the same widths, so the chunks' plans cost at least <paramref name="wholeTextPlanBits"/> between them, and each chunk also pays the Structured Append and ECI headers; if even that total does not fit the count, no walk at this version can.
    /// A lower bound, so it only rejects. <c>StructuredAppendPlannerTest</c> holds it to never refusing a count the walk reaches.
    /// </remarks>
    public static bool CanHoldPlanned(int capacityBits, int count, int wholeTextPlanBits, EciMode charset)
        => wholeTextPlanBits >= ModeSegmenter.Unreachable
            || (long)count * (capacityBits - HeaderBits - charset.GetStandardQrHeaderBits()) >= wholeTextPlanBits;

    /// <summary>The minimal plan for the whole text at this version's count indicator widths, computed once per band into <paramref name="cache"/>.</summary>
    /// <remarks>The widths are constant within the three ISO/IEC 18004 bands (1-9, 10-26, 27-40), so a band's cost holds for every version in it.</remarks>
    private static int WholeTextPlanBits(ReadOnlySpan<char> text, EciMode charset, int version, Span<int> cache)
    {
        var band = Band(version);
        if (cache[band] < 0)
        {
            cache[band] = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits,
                EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version),
                default, out _);
        }
        return cache[band];
    }

    /// <summary>
    /// Whether any chunk of this text could be cheaper as a mixed plan than as one run, which is what decides whether the searches have to consult the segmentation program at all.
    /// </summary>
    /// <remarks>
    /// One pass over the text for the longest run of each denser mode, since a chunk's runs are never longer than the whole text's; if none of them could repay the mode header splitting it out adds, then no chunk's plan beats its single-mode stream, the two costs are the same number everywhere, and the split is the same split.
    /// </remarks>
    private static bool CanPlanHelp(ReadOnlySpan<char> text, EncodingMode singleMode, out bool oneRunPlans)
    {
        oneRunPlans = false;
        if (!QRSegmentPlanner.CanPlanBeatSingleMode(singleMode))
            return false;

        ModeSegmenter.LongestDenseRuns(text, out var numericRun, out var alnumRun);
        // The same two numbers say when the plan of every chunk is settled without the program.
        oneRunPlans = singleMode == EncodingMode.Byte && QRSegmentPlanner.PlanIsOneByteRun(numericRun, alnumRun);
        return QRSegmentPlanner.PlanCouldBeatSingleMode(numericRun, alnumRun);
    }

    /// <summary>
    /// How many leading chunks a walk at <paramref name="budget"/> shares with the held walk above it and the failed walk below it: those on which the two agree. 0 when either is missing.
    /// </summary>
    private static int SharedChunks(ReadOnlySpan<int> settledEnds, int settledBudget, int settledCount, ReadOnlySpan<int> failedEnds, int failedBudget, int count, int budget)
    {
        if (settledBudget < budget || failedBudget < 0 || failedBudget > budget)
            return 0;
        var shared = 0;
        var limit = Math.Min(settledCount, count);
        while (shared < limit && settledEnds[shared] == failedEnds[shared])
            shared++;
        return shared;
    }

    /// <summary>The count indicator band of a version (1-9, 10-26, 27-40): a chunk's cost depends on the version only through it, so a split priced in one band holds for every version of that band whose capacity holds its budget.</summary>
    private static int Band(int version) => version < 10 ? 0 : version < 27 ? 1 : 2;

    /// <summary>Bits every symbol of a set pays before its first payload bit, at the narrowest widths any version has.</summary>
    private static int ChunkFloorBits(EciMode charset) => HeaderBits + charset.GetStandardQrHeaderBits() + ModeIndicatorBits + MinCountIndicatorBits;

    /// <summary>
    /// Bits one chunk needs as a symbol of the set: the Structured Append header, the ECI header when the set carries a charset, and the cheapest stream the segmentation allows.
    /// The byte order mark, when it applies, is paid by the first chunk only and forces its single-mode stream, as it does for a single symbol.
    /// </summary>
    public static int ChunkBits(ReadOnlySpan<char> chunk, EciMode charset, int version, QRSegmentation segmentation, bool utf8Bom)
    {
        var analysis = TextAnalyzer.Analyze(chunk, charset);
        var bomApplies = utf8Bom && charset == EciMode.Utf8 && analysis.EncodingMode == EncodingMode.Byte;
        var bits = SingleModeChunkBits(in analysis, charset, version, bomApplies);

        if (segmentation == QRSegmentation.Optimal && !bomApplies && QRSegmentPlanner.CanPlanBeatSingleMode(analysis.EncodingMode) && chunk.Length <= QRSegmentPlanner.MaxPlannableChars)
        {
            var planned = HeaderBits + charset.GetStandardQrHeaderBits()
                + QRSegmentPlanner.MinimumPayloadBits(chunk, charset, EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version));
            if (planned < bits)
                bits = planned;
        }

        return bits;
    }

    /// <summary>
    /// What <see cref="ChunkBits"/> charges the chunk's single-mode stream, from an analysis the caller already has.
    /// </summary>
    public static int SingleModeChunkBits(in TextAnalysisResult analysis, EciMode charset, int version, bool bomApplies)
        => HeaderBits + charset.GetStandardQrHeaderBits()
            + ModeIndicatorBits + analysis.EncodingMode.GetCountIndicatorLength(version)
            + ModeSegmenter.PayloadBits(analysis.EncodingMode, analysis.DataLength)
            + (bomApplies ? 24 : 0);

    /// <summary>
    /// Greedy walk: how many chunks of at most <paramref name="budgetBits"/> each the text needs at this version, writing each chunk's end offset into <paramref name="chunkEnds"/> while it has room.
    /// Stops as soon as the text needs more than <paramref name="limit"/> chunks and returns a value greater than the limit; a walk that cannot answer "at most this many" with yes has nothing left to learn.
    /// <see cref="Impossible"/> when some single character does not fit the budget.
    /// </summary>
    internal static int CountChunks(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom, QRSegmentation segmentation, int version, int budgetBits, int limit, Span<int> chunkEnds)
        => CountChunks(text, charset, utf8Bom, segmentation, version, budgetBits, limit, chunkEnds, 0, 0);

    /// <summary>The walk resumed: <paramref name="count"/> chunks are already in <paramref name="chunkEnds"/> and the next starts at <paramref name="start"/>.</summary>
    private static int CountChunks(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom, QRSegmentation segmentation, int version, int budgetBits, int limit, Span<int> chunkEnds, int count, int start)
    {
        ScalarWalks++;
        while (start < text.Length)
        {
            if (count == limit)
                return count + 1;
            var end = LongestChunkEnd(text, start, charset, version, segmentation, utf8Bom && start == 0, budgetBits);
            if (end >= 0)
                end = BeforeMark(text, charset, start, end);
            if (end < 0)
                return Impossible;
            if (count < chunkEnds.Length)
                chunkEnds[count] = end;
            count++;
            start = end;
        }

        return count;
    }

    /// <summary>
    /// The cut moved back so that the next chunk does not begin with U+FEFF; -1 when that leaves this chunk nothing.
    /// </summary>
    /// <remarks>
    /// A chunk is a symbol of its own, and the byte-segment decoder takes a segment's leading U+FEFF for a byte order mark and drops it, so a chunk that began with one would lose it; inside a chunk its plan keeps it interior to a Byte run (<see cref="ModeSegmenter.ByteOrderMark"/>).
    /// The character ahead of the mark, a surrogate pair whole, goes to the next chunk with it, and so on back through a run of marks. A shorter chunk still fits its budget, and the end stays monotone in the budget, which the searches rely on.
    /// Only under UTF-8: a declared ISO-8859-1 stops the decoder dropping anything, and the mark is not in that charset to begin with.
    /// The lanes close their chunks by the same rule. A mark at the head of the text is the first symbol's, as it is a single symbol's, and is lost the same way.
    /// </remarks>
    private static int BeforeMark(ReadOnlySpan<char> text, EciMode charset, int start, int end)
    {
        if (charset != EciMode.Utf8)
            return end;

        while (end < text.Length && text[end] == ByteOrderMark)
        {
            end -= end - start >= 2 && char.IsLowSurrogate(text[end - 1]) && char.IsHighSurrogate(text[end - 2]) ? 2 : 1;
            if (end <= start)
                return -1;
        }
        return end;
    }

    /// <summary>
    /// The most a cut kept off a run of U+FEFF can leave of a chunk's budget unused: the run and the character ahead of it, which move to the next chunk together (<see cref="BeforeMark"/>). Zero outside UTF-8 and for text holding no mark past its first character.
    /// </summary>
    internal static int KeptOffBits(ReadOnlySpan<char> text, EciMode charset)
    {
        if (charset != EciMode.Utf8 || text.Length < 2 || text.Slice(1).IndexOf(ByteOrderMark) < 0)
            return 0;

        // From one run to the next by a search for the mark, so a text holding a few costs a few.
        var most = 0;
        var i = 1;
        while (i < text.Length)
        {
            var next = text.Slice(i).IndexOf(ByteOrderMark);
            if (next < 0)
                break;
            i += next;
            var ahead = i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]) ? i - 2 : i - 1;
            var bytes = ModeSegmenter.ByteCost(text, ahead, charset);
            for (; i < text.Length && text[i] == ByteOrderMark; i++)
                bytes += 3;
            most = Math.Max(most, bytes);
        }
        return most * 8;
    }

    /// <summary>
    /// Whether the text holds a run of U+FEFF that no symbol of this version holds together with the character ahead of it, which no cut can part it from (<see cref="BeforeMark"/>) and no Byte run can open after (<see cref="ModeSegmenter.ByteOrderMark"/>): the least such a chunk costs is one Byte run of the two. UTF-8 only, as those rules are.
    /// </summary>
    internal static bool HoldsMarkRunNoSymbolHolds(ReadOnlySpan<char> text, EciMode charset, int version, QREccLevel eccLevel, bool utf8Bom)
    {
        if (charset != EciMode.Utf8)
            return false;

        var room = Capacity(version, eccLevel) - HeaderBits - charset.GetStandardQrHeaderBits() - ModeIndicatorBits - EncodingMode.Byte.GetCountIndicatorLength(version);
        var bytes = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] != ByteOrderMark)
            {
                bytes = 0;
                continue;
            }

            if (bytes == 0)
            {
                // The character ahead, a pair whole, and the mark the first symbol begins with when it is asked for.
                var ahead = i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]) ? i - 2 : i - 1;
                bytes = ModeSegmenter.ByteCost(text, ahead, charset) + (utf8Bom && ahead == 0 ? 3 : 0);
            }

            bytes += 3;
            if (bytes * 8 > room)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The largest end offset such that the chunk from <paramref name="start"/> fits the budget, never inside a surrogate pair; -1 when not even the first character fits.
    /// </summary>
    /// <remarks>
    /// Found without pricing whole prefixes. The single-mode cost of a prefix is a closed form of its length once its mode is known, and the mode changes at most twice along the text (Numeric, then Alphanumeric, then Byte, each boundary the first character outside the narrower alphabet), so the end is a few arithmetic steps plus, for a UTF-8 Byte run, one pass over the run's own bytes.
    /// Under <see cref="QRSegmentation.Optimal"/> the mixed plan holds the single-mode plan among its candidates, so its cost is what decides, and one forward pass of the program finds the prefix; except inside an all-digit run, which no split improves, and behind a byte order mark, where only the leading alphanumeric run is planned, since a chunk past it is Byte mode and carries the mark.
    /// The definition this must agree with is <see cref="ChunkBits"/>; <c>StructuredAppendPlannerTest</c> holds the two together on every boundary budget.
    /// </remarks>
    internal static int LongestChunkEnd(ReadOnlySpan<char> text, int start, EciMode charset, int version, QRSegmentation segmentation, bool utf8Bom, int budgetBits)
    {
        // No chunk is longer than the most any symbol holds, so nothing past that is a
        // candidate; the window never ends inside a pair either.
        var length = Math.Min(text.Length - start, QRSegmentPlanner.MaxPlannableChars);
        if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length]))
            length--;
        var window = text.Slice(start, length);
        var payloadBudget = budgetBits - HeaderBits - charset.GetStandardQrHeaderBits() - ModeIndicatorBits;
        var bom = utf8Bom && charset == EciMode.Utf8;

        var single = SingleModeLength(window, charset, version, bom, payloadBudget, out var digitRun, out var alnumRun);
        if (segmentation != QRSegmentation.Optimal)
            return single == 0 ? -1 : start + single;

        // The single-mode end fell inside a digit run: one Numeric run is the optimum of
        // all-digit content, so the plan ends where the single mode does. When the run
        // ends exactly there, a plan may still open another run past it.
        if (single == length || single < digitRun)
            return single == 0 ? -1 : start + single;

        // A byte order mark is written only into a Byte-mode chunk, where it costs 24 bits
        // and forces the single-mode stream; a prefix inside the alphanumeric alphabet
        // carries none, so under a mark that prefix is all a plan may cover.
        var planWindow = window;
        if (bom)
        {
            if (single > alnumRun)
                return start + single;
            planWindow = window.Slice(0, alnumRun);
        }

        // The program prices each run's mode indicator itself, so its budget keeps those bits.
        var planned = ModeSegmenter.LongestPrefixWithinBudget(planWindow, charset, ModeIndicatorBits,
            EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version), payloadBudget + ModeIndicatorBits);
        Debug.Assert(planned >= single, "the plan holds the single-mode stream among its candidates, so it never fits less");
        return planned == 0 ? -1 : start + planned;
    }

    /// <summary>
    /// The longest prefix of <paramref name="window"/> whose single-mode stream (mode indicator excluded) fits <paramref name="payloadBudget"/>, in characters; 0 when the first character does not.
    /// <paramref name="digitRun"/> and <paramref name="alnumRun"/> receive the two mode boundaries the walk crosses: how many digits the window starts with, and how many characters of it stay inside the alphanumeric alphabet.
    /// </summary>
    private static int SingleModeLength(ReadOnlySpan<char> window, EciMode charset, int version, bool bom, int payloadBudget, out int digitRun, out int alnumRun)
    {
        var n = window.Length;

        // The two boundaries: the first character outside 0-9, then the first outside the
        // 45-character alphabet. A prefix is Numeric up to the one and Alphanumeric up to
        // the other, and Byte past it, which is how the analyser classifies it.
        StructuredAppendScanner.ModeBoundaries(window, out var d, out var a);
        digitRun = d;
        alnumRun = a;

        if (d > 0)
        {
            var most = MostNumeric(payloadBudget - EncodingMode.Numeric.GetCountIndicatorLength(version));
            if (most < d)
                return most;
            if (d == n)
                return n;
        }

        if (a > d)
        {
            var most = MostAlphanumeric(payloadBudget - EncodingMode.Alphanumeric.GetCountIndicatorLength(version));
            if (most < a)
                return Math.Max(d, most);
            if (a == n)
                return n;
        }

        // Byte for the rest: every character costs its bytes, the byte order mark three more.
        var bytes = (payloadBudget - EncodingMode.Byte.GetCountIndicatorLength(version) - (bom ? 24 : 0)) / 8;
        if (bytes <= a)
            return a; // the shortest Byte-mode prefix has a + 1 characters, so at least a + 1 bytes

        if (charset != EciMode.Utf8)
        {
            // One byte per character; a forced ISO-8859-1 charset can still meet a pair, which is not split.
            var end = Math.Min(n, bytes);
            if (end < n && char.IsLowSurrogate(window[end]) && char.IsHighSurrogate(window[end - 1]))
                end--;
            return end;
        }

        // The leading alphanumeric characters are ASCII; spend their bytes before
        // scanning the suffix, whose boundary keeps a surrogate pair together.
        return a + StructuredAppendScanner.Utf8PrefixLength(window.Slice(a), bytes - a);
    }

    /// <summary>The most digits whose Numeric payload (10 bits per 3, then 4 or 7) fits the bits; 0 when one does not.</summary>
    private static int MostNumeric(int bits)
    {
        if (bits < 4)
            return 0;
        var rest = bits % 10;
        return bits / 10 * 3 + (rest >= 7 ? 2 : rest >= 4 ? 1 : 0);
    }

    /// <summary>The most characters whose Alphanumeric payload (11 bits per 2, then 6) fits the bits; 0 when one does not.</summary>
    private static int MostAlphanumeric(int bits)
    {
        if (bits < 6)
            return 0;
        return bits / 11 * 2 + (bits % 11 >= 6 ? 1 : 0);
    }
}
