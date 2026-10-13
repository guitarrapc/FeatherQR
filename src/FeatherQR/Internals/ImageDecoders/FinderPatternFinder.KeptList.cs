namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>
    /// The strided scan <see cref="TryFind(ReadOnlySpan{byte}, int, int, byte, Span{FinderPattern}, in GreyLevels, Span{FinderPattern}, out int, out bool)"/> starts with, into a list as long as the caller gives: the same rows in the same order, so the list's first <see cref="MaxFinderCandidates"/> are that search's candidates.
    /// </summary>
    /// <returns>The number of candidates written. A list that comes back full may have left candidates out.</returns>
    internal static int FindCandidatesStrided(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, in GreyLevels grey)
        => FindCandidatesCore(luminance, width, height, threshold, grey, candidates, RowStride(height), FinderRowKernel.Auto);

    /// <summary>The rows <see cref="FindCandidatesStrided"/> skipped, merged into its list: with them the list is a full scan's.</summary>
    internal static void FindCandidatesInSkippedRows(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, ref int candidateCount, in GreyLevels grey)
    {
        var rentedEdges = RentEdgeBuffer(FinderRowKernel.Auto, width);
        try
        {
            ScanSkippedRows(luminance, width, height, threshold, grey, FinderRowKernel.Auto, rentedEdges, candidates, ref candidateCount);
        }
        finally
        {
            ReturnEdgeBuffer(rentedEdges);
        }
    }

    /// <summary>
    /// The triple <see cref="TryFind(ReadOnlySpan{byte}, int, int, byte, Span{FinderPattern}, in GreyLevels, Span{FinderPattern}, out int, out bool)"/> selects, from a list the caller keeps between selections (<see cref="FindCandidatesStrided"/>).
    /// </summary>
    /// <remarks>
    /// It reads the first <see cref="MaxFinderCandidates"/> of the list, the candidates that search would hold, and leaves them in place.
    /// While <paramref name="everyRowScanned"/> is false the list is the strided scan's, and a selection that is none or doubtful scans the skipped rows into the whole list first, as that search does, and sets it.
    /// </remarks>
    internal static bool TrySelectFromList(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<FinderPattern> candidates, ref int candidateCount, ref bool everyRowScanned, Span<FinderPattern> patterns)
    {
        Span<FinderPattern> scratch = stackalloc FinderPattern[MaxCandidates];
        var held = Math.Min(candidateCount, MaxCandidates);
        if (!everyRowScanned)
        {
            candidates.Slice(0, held).CopyTo(scratch);
            var strideSelected = TrySelectBestThree(scratch.Slice(0, held), patterns, out var unconfirmedLeftOut);
            if (strideSelected && !unconfirmedLeftOut)
                return true;

            FindCandidatesInSkippedRows(luminance, width, height, threshold, candidates, ref candidateCount, grey);
            everyRowScanned = true;
            held = Math.Min(candidateCount, MaxCandidates);

            if (strideSelected)
            {
                // The sweep's triple has to be confirmed over as much as the strided one, as in TryFind
                var strideHeight = ConfirmedHeight(candidates.Slice(0, held), patterns);
                Span<FinderPattern> swept = stackalloc FinderPattern[3];
                candidates.Slice(0, held).CopyTo(scratch);
                if (TrySelectBestThree(scratch.Slice(0, held), swept)
                    && ConfirmedHeight(swept, swept) >= strideHeight)
                {
                    swept.CopyTo(patterns);
                }
                return true;
            }
        }

        return TrySelectKeepingList(candidates.Slice(0, held), patterns);
    }

    /// <summary><see cref="TrySelectBestThree(Span{FinderPattern}, Span{FinderPattern})"/> on a copy, which it compacts and sorts, so the list stays as it is.</summary>
    internal static bool TrySelectKeepingList(ReadOnlySpan<FinderPattern> candidates, Span<FinderPattern> patterns)
    {
        Span<FinderPattern> scratch = stackalloc FinderPattern[MaxCandidates];
        candidates.CopyTo(scratch);
        return TrySelectBestThree(scratch.Slice(0, candidates.Length), patterns);
    }
}
