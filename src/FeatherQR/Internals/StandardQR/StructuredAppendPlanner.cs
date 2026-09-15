namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Splits text across Structured Append symbols: the fewest symbols the version cap allows, all at one version, balanced so the fullest symbol is as empty as it can be.
/// </summary>
/// <remarks>
/// Every symbol pays the 20-bit header and, when the set carries a charset, its own ECI header, so a symbol's payload budget is the version's data capacity less those; a chunk's cost is what the single-mode stream needs, or under <see cref="QRSegmentation.Optimal"/> the cheaper of that and the minimal mixed plan.
/// Cost is monotone in the chunk's length (a longer prefix never plans cheaper), so the longest chunk that fits a budget is found by binary search, and a greedy walk at a budget gives the symbol count that budget needs. The three steps of the split are three searches over that count.
/// Splits fall on <c>char</c> boundaries and never inside a surrogate pair. Design and the rules behind it: specs/standardqr-encoder.md.
/// </remarks>
internal static class StructuredAppendPlanner
{
    /// <summary>Mode indicator, position, count and parity (ISO/IEC 18004 Structured Append).</summary>
    public const int HeaderBits = 20;

    /// <summary>The 4-bit count field holds at most sixteen symbols.</summary>
    public const int MaxSymbols = 16;

    private const int ModeIndicatorBits = 4;
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

        // Fewest symbols, reached at the largest version.
        var count = CountChunks(text, eccLevel, charset, utf8Bom, segmentation, maxVersion, Capacity(maxVersion, eccLevel), chunkEnds);
        if (count == Impossible || count > MaxSymbols)
            return false;
        if (count == 1)
        {
            chunkCount = 1;
            version = maxVersion;
            return true;
        }

        // Smallest version that still holds that many.
        version = maxVersion;
        for (var candidate = minVersion; candidate < maxVersion; candidate++)
        {
            if (CountChunks(text, eccLevel, charset, utf8Bom, segmentation, candidate, Capacity(candidate, eccLevel), chunkEnds) <= count)
            {
                version = candidate;
                break;
            }
        }

        // Smallest per-symbol budget at that version that still holds that many: the balanced split.
        var low = 1;
        var high = Capacity(version, eccLevel);
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (CountChunks(text, eccLevel, charset, utf8Bom, segmentation, version, middle, chunkEnds) <= count)
                high = middle;
            else
                low = middle + 1;
        }

        budgetBits = low;
        chunkCount = CountChunks(text, eccLevel, charset, utf8Bom, segmentation, version, low, chunkEnds);
        return true;
    }

    /// <summary>Data capacity of a symbol at this version and level, in bits.</summary>
    public static int Capacity(int version, QREccLevel eccLevel) => QRCodeConstants.GetEccInfo(version, eccLevel).TotalDataCodewords * 8;

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
    /// <see cref="Impossible"/> when some single character does not fit the budget.
    /// </summary>
    private static int CountChunks(ReadOnlySpan<char> text, QREccLevel eccLevel, EciMode charset, bool utf8Bom, QRSegmentation segmentation, int version, int budgetBits, Span<int> chunkEnds)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
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
    /// The largest end offset such that the chunk from <paramref name="start"/> fits the budget, snapped past a low surrogate so a pair is never split; -1 when not even the first character fits.
    /// </summary>
    private static int LongestChunkEnd(ReadOnlySpan<char> text, int start, EciMode charset, int version, QRSegmentation segmentation, bool utf8Bom, int budgetBits)
    {
        var low = start + 1;
        var high = text.Length;
        if (ChunkBits(text.Slice(start, Snap(text, low) - start), charset, version, segmentation, utf8Bom) > budgetBits)
            return -1;

        // Fits(Snap(e)) is monotone in e because Snap is monotone and the cost is monotone in the length.
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (ChunkBits(text.Slice(start, Snap(text, middle) - start), charset, version, segmentation, utf8Bom) <= budgetBits)
                low = middle;
            else
                high = middle - 1;
        }

        return Snap(text, low);
    }

    /// <summary>An end offset that would split a surrogate pair moves past the pair.</summary>
    private static int Snap(ReadOnlySpan<char> text, int end)
        => end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]) ? end + 1 : end;
}
