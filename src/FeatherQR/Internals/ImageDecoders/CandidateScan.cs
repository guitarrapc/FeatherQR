using System.Buffers;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A decoder that reads a symbol around a single finder: its decode of one finder candidate, and what <see cref="CandidateScan"/>
/// reads of its results. A decoder implements this on a struct, so the scan calls it directly.
/// </summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface ICandidateDecoder<TInfo>
{
    /// <summary>The diagnostics of an image with nothing decoded.</summary>
    TInfo NotDetected { get; }

    /// <summary>The bytes of module buffer a candidate's decode samples its grids into: the largest symbol's modules.</summary>
    int ModuleBufferLength { get; }

    /// <summary>Where the symbol a result read lies in the image; empty when the result carries no corners.</summary>
    SymbolCorners Corners(in TInfo info);

    /// <summary>The result with its corners stripped, as the scan reports every result but a read.</summary>
    TInfo WithoutCorners(in TInfo info);

    /// <summary>
    /// One finder candidate's grids and searches, each failure kept in <paramref name="result"/>, which is this candidate's own.
    /// Ends at a terminal result; a read that does not fit the destination carries its corners, for the scan to skip the
    /// candidates inside it.
    /// </summary>
    DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out TInfo info, ref SearchResult<TInfo> result);
}

/// <summary>
/// The scan of the decoders that read around a single finder: a strided finder scan, then a full sweep unless it read a symbol
/// or read one too long for the destination; in each, the first eight candidates, most confirmed first, less any inside a symbol
/// that read but did not fit the destination. A successful decode ends the scan, a read that did not fit ends its candidate,
/// and each candidate's grids see only its own results; otherwise each finder scan reports the result that went furthest, and
/// the scan the sweep's if it settled, the strided scan's if not.
/// </summary>
/// <remarks>
/// The rules and why they are so are in the architecture record (qrcode-symbologies.md, single-finder candidate scan); what each decoder does with one
/// candidate is its own.
/// </remarks>
internal static class CandidateScan
{
    /// <summary>Candidates actually tried, most confirmed first (false hits rank behind).</summary>
    public const int MaxCandidatesToTry = 8;

    /// <summary>The largest module buffer taken from the stack; a larger one is rented, since the scan sits under the public image entry point.</summary>
    private const int MaxStackModuleBuffer = 512;

    /// <summary>
    /// The strided scan, then the sweep unless it read a symbol or read one too long for the destination, at the global threshold of <paramref name="histogram"/>;
    /// <paramref name="noFinder"/> when neither found a finder candidate, with the threshold and grey levels they used.
    /// </summary>
    /// <remarks>
    /// The widening trigger has to be a question about the symbol, and "did anything decode" is the only one available.
    /// The scan itself cannot ask it: every signal inside a flat candidate list is a statement about the image, so a second symbol or a noise artefact would answer it in the real symbol's place and suppress the sweep the symbol needed.
    /// Paid only on images the strided scan does not read, and on an image of one symbol it makes the detection envelope a superset of a full sweep's: the symbol is read if either scan reads it.
    /// After a read too long for the destination no sweep runs, so a second symbol that fits and that only the sweep finds is not read (a residual: qrcode-symbologies.md, single-finder candidate scan).
    /// </remarks>
    public static DecodeStatus Decode<TDecoder, TInfo>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
        where TDecoder : struct, ICandidateDecoder<TInfo>
    {
        // Hoisted: the two scans binarize the same buffer
        threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out grey);
        var image = new ImageView(luminance, width, height, threshold, grey);

        Span<FinderPattern> tried = stackalloc FinderPattern[MaxCandidatesToTry];
        var status = Scan<TDecoder, TInfo>(ref decoder, image, destination, out charsWritten, out info, fullSweep: false, skip: default, tried, out var triedCount, out var stridedFound);
        noFinder = false;
        // Terminal, not just successful: DestinationTooSmall is only reached after the symbol has been located, sampled,
        // RS-corrected and its segment found to fit the bitstream. (That ordering is a precondition, not a given: the segment
        // decoders check bitstream sufficiency before destination sufficiency precisely so a malformed count cannot masquerade as
        // a short buffer here; they check the content after it.) A sized call stops at that symbol as a read, so no sweep runs
        // here either, which keeps a probe for the buffer's size as cheap as a sized call; the price is a second symbol that
        // fits and that only the sweep finds. A verdict on the content does not end it: the sweep can find another symbol that
        // reads.
        if (AttemptStatus.IsTerminal(status))
            return status;

        // A candidate equal to one the strided scan tried (same position and module size, to the bit) decodes the same way in the
        // sweep, so it is not tried again; unless that scan settled, when every candidate stays
        var skip = AttemptStatus.IsSettled(status) ? default : tried.Slice(0, triedCount);
        var sweptStatus = Scan<TDecoder, TInfo>(ref decoder, image, destination, out var sweptChars, out var sweptInfo, fullSweep: true, skip, tried: default, out _, out var sweptFound);
        noFinder = stridedFound == 0 && sweptFound == 0;
        // Settled, not just successful: when the sweep is the scan that reads the symbol, its DestinationTooSmall or its verdict
        // on the content is the answer
        if (AttemptStatus.IsSettled(sweptStatus))
        {
            charsWritten = sweptChars;
            info = sweptInfo;
            return sweptStatus;
        }

        // Both failed: the strided scan's diagnostic, the one whose candidates the caller would have seen before the sweep existed
        return status;
    }

    /// <summary>
    /// One finder scan, strided or a full sweep, and the candidates it ranks first (<see cref="DecodeRanked"/>);
    /// <paramref name="found"/> is how many candidates it found.
    /// </summary>
    public static DecodeStatus Scan<TDecoder, TInfo>(ref TDecoder decoder, in ImageView image, Span<char> destination, out int charsWritten, out TInfo info, bool fullSweep, ReadOnlySpan<FinderPattern> skip, Span<FinderPattern> tried, out int triedCount, out int found)
        where TDecoder : struct, ICandidateDecoder<TInfo>
    {
        Span<FinderPattern> candidates = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        found = fullSweep
            ? FinderPatternFinder.FindCandidatesFullSweep(image.Luminance, image.Width, image.Height, image.Threshold, candidates, image.Grey)
            : FinderPatternFinder.FindCandidates(image.Luminance, image.Width, image.Height, image.Threshold, candidates, image.Grey);
        FinderPatternFinder.RankByConfirmation(candidates.Slice(0, found));
        return DecodeRanked<TDecoder, TInfo>(ref decoder, image, candidates.Slice(0, found), destination, out charsWritten, out info, skip, tried, out triedCount);
    }

    /// <summary>
    /// The first eight of the <paramref name="ranked"/> candidates, less those equal to one in <paramref name="skip"/> and those
    /// inside a symbol that read but did not fit the destination; each one decoded is written to <paramref name="tried"/>.
    /// </summary>
    /// <remarks>
    /// A candidate's decode depends only on its position and module size, the image and the threshold, so a candidate tried by a
    /// scan that settled on nothing settles on nothing again, and skipping it changes only a failure the caller does not report.
    /// A candidate inside a symbol that read is a finder-like pattern in that symbol's own data: symbols do not overlap.
    /// </remarks>
    internal static DecodeStatus DecodeRanked<TDecoder, TInfo>(ref TDecoder decoder, in ImageView image, ReadOnlySpan<FinderPattern> ranked, Span<char> destination, out int charsWritten, out TInfo info, ReadOnlySpan<FinderPattern> skip, Span<FinderPattern> tried, out int triedCount)
        where TDecoder : struct, ICandidateDecoder<TInfo>
    {
        triedCount = 0;

        // The furthest-progressing failure is the most useful diagnostic: an attempt that passed format decoding but failed RS
        // says more than "not detected"
        var best = new SearchResult<TInfo>(ReportRule.Furthest, decoder.NotDetected);

        var moduleLength = decoder.ModuleBufferLength;
        byte[]? rented = null;
        Span<byte> modules = moduleLength <= MaxStackModuleBuffer
            ? stackalloc byte[moduleLength]
            : (rented = ArrayPool<byte>.Shared.Rent(moduleLength)).AsSpan(0, moduleLength);
        try
        {
            // Where each read that did not fit lies: a later candidate inside one is a finder-like pattern in that symbol's own data, not another symbol
            Span<SymbolCorners> readSymbols = stackalloc SymbolCorners[MaxCandidatesToTry];
            var readCount = 0;
            var count = Math.Min(ranked.Length, MaxCandidatesToTry);
            for (var c = 0; c < count; c++)
            {
                // Not replaced by the next in rank: the candidates tried stay the first eight
                if (FinderPatternFinder.ContainsCandidate(skip, ranked[c]))
                    continue;
                if (SymbolGeometry.AnyContains(readSymbols.Slice(0, readCount), ranked[c].X, ranked[c].Y))
                    continue;
                if (!tried.IsEmpty)
                    tried[triedCount++] = ranked[c];

                // The candidate's attempts see only its own results: a read that did not fit on another candidate ends none of them
                var result = new SearchResult<TInfo>(ReportRule.Furthest, decoder.NotDetected);
                var status = decoder.DecodeCandidate(image, ranked[c], modules, destination, out charsWritten, out info, ref result);
                if (status == DecodeStatus.Success)
                    return status;
                var corners = decoder.Corners(result.Info);
                if (result.Status == DecodeStatus.DestinationTooSmall && !corners.IsEmpty)
                    readSymbols[readCount++] = corners;
                // Corners are reported with a read only
                best.Other(result.Status, 0, decoder.WithoutCorners(result.Info));
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }

        return best.Report(out charsWritten, out info);
    }
}
