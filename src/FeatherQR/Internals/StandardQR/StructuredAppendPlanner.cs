using System.Diagnostics;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Splits text across Structured Append symbols: the fewest symbols the version cap allows, all at one version, balanced so the fullest symbol is as empty as it can be.
/// </summary>
/// <remarks>
/// Every symbol pays the 20-bit header and, when the set carries a charset, its own ECI header, so a symbol's payload budget is the version's data capacity less those; a chunk's cost is what the single-mode stream needs, or under <see cref="QRSegmentation.Optimal"/> the cheaper of that and the minimal mixed plan.
/// Cost is monotone in the chunk's length (a longer prefix never plans cheaper), so the longest chunk that fits a budget is well defined and found in one forward step (<see cref="LongestChunkEnd"/>), and a greedy walk at a budget gives the symbol count that budget needs. The three steps of the split are three searches over that count.
/// A walk stops once it passes the count it is asked about, and a lower bound on what any split costs (<see cref="CanHold"/>) keeps the searches off versions and budgets that cannot hold the count.
/// Splits fall on <c>char</c> boundaries and never inside a surrogate pair. Design and the rules behind it: specs/standardqr-encoder.md.
/// </remarks>
internal static class StructuredAppendPlanner
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
    /// Plans the split. <paramref name="chunkEnds"/> receives the end offset of each chunk (at least <see cref="MaxSymbols"/> entries); <paramref name="chunkCount"/> is 1 when the text fits one symbol within the range, in which case nothing else is planned.
    /// Returns <c>false</c> when the text needs more than sixteen symbols at <paramref name="maxVersion"/>, or some single character cannot fit a symbol there.
    /// </summary>
    public static bool TryPlan(ReadOnlySpan<char> text, QREccLevel eccLevel, EciMode charset, bool utf8Bom, QRSegmentation segmentation, int minVersion, int maxVersion, Span<int> chunkEnds, out int chunkCount, out int version, out int budgetBits)
    {
        chunkCount = 0;
        version = 0;
        budgetBits = 0;

        // Nothing to split: the single-symbol path encodes an empty Byte segment.
        if (text.IsEmpty)
        {
            chunkCount = 1;
            version = maxVersion;
            return true;
        }

        // What no split can cost less than, priced once; every search below is gated on it.
        var cheapest = CheapestPayloadBits(text, charset);

        // Fewest symbols, reached at the largest version.
        var largest = Capacity(maxVersion, eccLevel);
        if (!CanHold(largest, MaxSymbols, cheapest, charset))
            return false;
        var count = CountChunks(text, charset, utf8Bom, segmentation, maxVersion, largest, MaxSymbols, chunkEnds);
        if (count > MaxSymbols)
            return false;
        if (count == 1)
        {
            chunkCount = 1;
            version = maxVersion;
            return true;
        }

        // Smallest version that still holds that many; a version the bound rules out is not walked.
        version = maxVersion;
        for (var candidate = minVersion; candidate < maxVersion; candidate++)
        {
            var capacity = Capacity(candidate, eccLevel);
            if (CanHold(capacity, count, cheapest, charset) && CountChunks(text, charset, utf8Bom, segmentation, candidate, capacity, count, chunkEnds) <= count)
            {
                version = candidate;
                break;
            }
        }

        // Smallest per-symbol budget at that version that still holds that many: the balanced split.
        // The floor is the bound's: the count fits at this version, so its average share is at most the capacity.
        var low = MinimumBudget(count, cheapest, charset);
        var high = Capacity(version, eccLevel);
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (CountChunks(text, charset, utf8Bom, segmentation, version, middle, count, chunkEnds) <= count)
                high = middle;
            else
                low = middle + 1;
        }

        budgetBits = low;
        chunkCount = CountChunks(text, charset, utf8Bom, segmentation, version, low, count, chunkEnds);
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
        var bits = ModeIndicatorBits + analysis.EncodingMode.GetCountIndicatorLength(version) + ModeSegmenter.PayloadBits(analysis.EncodingMode, analysis.DataLength) + (bomApplies ? 24 : 0);

        if (segmentation == QRSegmentation.Optimal && !bomApplies && chunk.Length <= QRSegmentPlanner.MaxPlannableChars)
        {
            var planned = QRSegmentPlanner.MinimumPayloadBits(chunk, charset, EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version), EncodingMode.Byte.GetCountIndicatorLength(version));
            if (planned < bits)
                bits = planned;
        }

        return HeaderBits + charset.GetStandardQrHeaderBits() + bits;
    }

    /// <summary>
    /// The parity byte of a set: the XOR of the whole text's bytes in the charset the set is written in, with the byte order mark first when one is written.
    /// </summary>
    public static byte Parity(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom)
    {
        var parity = utf8Bom ? 0xEF ^ 0xBB ^ 0xBF : 0;
        if (charset != EciMode.Utf8)
        {
            // ISO-8859-1 and the default charset are the low byte of each char; the analysis
            // that chose the charset already established every char fits it.
            foreach (var c in text)
                parity ^= (byte)c;
            return (byte)parity;
        }

        // UTF-8 per code point, without an encoder: the same bytes the Byte-mode writer
        // produces, a lone surrogate included (it becomes U+FFFD there too).
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                codePoint = 0xFFFD;
            }

            if (codePoint < 0x80)
            {
                parity ^= codePoint;
            }
            else if (codePoint < 0x800)
            {
                parity ^= 0xC0 | (codePoint >> 6);
                parity ^= 0x80 | (codePoint & 0x3F);
            }
            else if (codePoint < 0x10000)
            {
                parity ^= 0xE0 | (codePoint >> 12);
                parity ^= 0x80 | ((codePoint >> 6) & 0x3F);
                parity ^= 0x80 | (codePoint & 0x3F);
            }
            else
            {
                parity ^= 0xF0 | (codePoint >> 18);
                parity ^= 0x80 | ((codePoint >> 12) & 0x3F);
                parity ^= 0x80 | ((codePoint >> 6) & 0x3F);
                parity ^= 0x80 | (codePoint & 0x3F);
            }
        }
        return (byte)parity;
    }

    /// <summary>
    /// Greedy walk: how many chunks of at most <paramref name="budgetBits"/> each the text needs at this version, writing each chunk's end offset into <paramref name="chunkEnds"/> while it has room.
    /// Stops as soon as the text needs more than <paramref name="limit"/> chunks and returns a value greater than the limit; a walk that cannot answer "at most this many" with yes has nothing left to learn.
    /// <see cref="Impossible"/> when some single character does not fit the budget.
    /// </summary>
    internal static int CountChunks(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom, QRSegmentation segmentation, int version, int budgetBits, int limit, Span<int> chunkEnds)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
            if (count == limit)
                return count + 1;
            var end = LongestChunkEnd(text, start, charset, version, segmentation, utf8Bom && start == 0, budgetBits);
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
        var d = 0;
        while (d < n && CharacterSets.IsNumeric(window[d]))
            d++;
        var a = d;
        while (a < n && CharacterSets.IsAlphanumeric(window[a]))
            a++;
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

        // UTF-8 by code point, the same bytes the writer emits: the a leading characters
        // are ASCII, then one pass over the run until the next character would overflow.
        var used = a;
        var i = a;
        while (i < n)
        {
            var c = window[i];
            int cost, step = 1;
            if (c < 0x80)
                cost = 1;
            else if (c < 0x800)
                cost = 2;
            else if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(window[i + 1]))
                (cost, step) = (4, 2);
            else
                cost = 3; // a BMP character, or a lone surrogate written as U+FFFD
            if (used + cost > bytes)
                break;
            used += cost;
            i += step;
        }
        return i;
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
