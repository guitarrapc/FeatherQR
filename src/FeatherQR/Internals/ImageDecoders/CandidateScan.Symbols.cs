using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A decoder of <see cref="CandidateScan"/> that also returns every symbol of an image (<see cref="CandidateScan.DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/>): the result it returns a symbol as, and the text buffer every symbol fits.
/// </summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
/// <typeparam name="TResult">The result a symbol is returned as.</typeparam>
internal interface ISymbolsDecoder<TInfo, TResult> : ICandidateDecoder<TInfo>
{
    /// <summary>The longest text any symbol of the symbology decodes to.</summary>
    int MaxChars { get; }

    /// <summary>A read symbol as a result.</summary>
    TResult Result(string text, in TInfo info);

    /// <summary>Where a result's symbol lies in the image.</summary>
    SymbolCorners ResultCorners(in TResult result);

    /// <summary>The histogram of <paramref name="luminance"/>, the image the scan reads, which its global thresholds come from.</summary>
    void FillHistogram(ReadOnlySpan<byte> luminance, Span<int> histogram);
}

internal static partial class CandidateScan
{
    /// <summary>
    /// The candidates a finder scan's list has room for when every symbol is read. The one-symbol scan's 32 would leave finders of a large sheet unread.
    /// A finder past this many is dropped, and nothing reports it.
    /// </summary>
    internal const int MaxSymbolCandidates = 4096;

    /// <summary>The room the array form starts with, the smallest array the pool rents, doubled each time it fills.</summary>
    private const int GrowingRoom = 16;

    /// <summary>What one call of <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/> carries through its passes.</summary>
    private ref struct SymbolSearch<TResult>
    {
        /// <summary>The room, the first <see cref="Count"/> of it read.</summary>
        public Span<TResult> Symbols;

        public int Count;

        /// <summary>The array form's room, rented from the pool, which grows where the caller's room fills: <c>null</c> for the caller's room.</summary>
        public TResult[]? Growing;

        /// <summary>Checked as each reading starts and before each candidate is decoded, never inside a candidate's decode.</summary>
        public CancellationToken Token;

        /// <summary>The text buffer every decode writes into: the longest text of the symbology.</summary>
        public Span<char> Chars;

        /// <summary>The module buffer every decode samples into: the largest symbol's.</summary>
        public Span<byte> Modules;

        /// <summary>The strided rows' hits a reading keeps for its sweep, and the end of each row's hits (<see cref="FinderPatternFinder.FindCandidatesKeepingRowHits(ReadOnlySpan{byte}, int, int, byte, in GreyLevels, Span{FinderPattern}, out int, Span{FinderPattern}, Span{int}, int, FinderPatternFinder.KeptRows)"/>). Empty where the room grows.</summary>
        public Span<FinderPattern> Kept;

        public Span<int> RowEnds;

        /// <summary>The strided rows' hits a read of the same readings kept for this one (<see cref="SharedRowHits"/>). Empty when there are none.</summary>
        public SharedRowHits Shared;

        public readonly bool IsFull => Growing is null && Count == Symbols.Length;

        public void Add(TResult symbol)
        {
            if (Count == Symbols.Length)
            {
                // Only the array form gets here: the passes stop before a symbol the caller's room has no space for
                var grown = ArrayPool<TResult>.Shared.Rent(Symbols.Length * 2);
                Symbols.CopyTo(grown);
                // The room holds the texts, which the pool would otherwise keep alive
                ArrayPool<TResult>.Shared.Return(Growing!, clearArray: true);
                Growing = grown;
                Symbols = grown;
            }
            Symbols[Count++] = symbol;
        }
    }

    /// <summary>
    /// Decodes every symbol of a grayscale image into <paramref name="symbols"/>, as many as it has room for, each once and in the order found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The image is read in the passes of <see cref="ImageDecodePasses"/>, in their order, as a decoder with the midpoint pass and no reduced-scale search reads it. A read does not end them: each finder scan decodes its candidates in rank order, the candidates inside a symbol read so far left out, and the passes go on until the room is filled.
    /// The full sweep runs after the strided scan in every pass, since a symbol beside a read one can be found by the sweep alone. Its candidates the strided scan already tried are not tried again.
    /// </para>
    /// <para>
    /// A symbol whose content gives a verdict is left out.
    /// Nothing but the texts is allocated.
    /// </para>
    /// </remarks>
    /// <returns>How many symbols were written.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check, the symbols read so far left in <paramref name="symbols"/>.</exception>
    internal static int DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, Span<TResult> symbols, CancellationToken cancellationToken = default)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        if (symbols.IsEmpty)
            return 0;
        TResult[]? none = null;
        return DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, symbols, ref none, default, cancellationToken);
    }

    /// <summary>
    /// <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/> given the strided rows' hits a read of the same readings kept (<see cref="SharedRowHits"/>): a reading whose rows are there scans them no more.
    /// </summary>
    internal static int DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, Span<TResult> symbols, in SharedRowHits shared, CancellationToken cancellationToken = default)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        if (symbols.IsEmpty)
            return 0;
        TResult[]? none = null;
        return DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, symbols, ref none, shared, cancellationToken);
    }

    /// <summary>The array form given the strided rows' hits a read of the same readings kept (<see cref="SharedRowHits"/>).</summary>
    internal static TResult[] DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, in SharedRowHits shared, CancellationToken cancellationToken)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        var room = ArrayPool<TResult>.Shared.Rent(GrowingRoom);
        try
        {
            var count = DecodeSymbols<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, ref room, shared, cancellationToken);
            return count == 0 ? [] : room.AsSpan(0, count).ToArray();
        }
        finally
        {
            ArrayPool<TResult>.Shared.Return(room, clearArray: true);
        }
    }

    /// <summary>
    /// Decodes every symbol of a grayscale image, as <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/> does with room for all of them.
    /// </summary>
    /// <remarks>
    /// The room grows as it fills instead of ending the passes, so the image is read once however many symbols it holds.
    /// The array returned and the texts are allocated, and nothing else: the room is rented.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check.</exception>
    internal static TResult[] DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, CancellationToken cancellationToken = default)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        var room = ArrayPool<TResult>.Shared.Rent(GrowingRoom);
        try
        {
            var count = DecodeSymbols<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, ref room, cancellationToken);
            return count == 0 ? [] : room.AsSpan(0, count).ToArray();
        }
        finally
        {
            // The room holds the texts, which the pool would otherwise keep alive
            ArrayPool<TResult>.Shared.Return(room, clearArray: true);
        }
    }

    /// <summary>
    /// Decodes every symbol of a grayscale image into <paramref name="room"/>, an array rented from <see cref="ArrayPool{T}.Shared"/>, as <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, CancellationToken)"/> does.
    /// </summary>
    /// <remarks>
    /// Each time the room fills, a rental of twice its length takes its place and it goes back to the pool cleared. The caller returns the last, with its texts cleared, after a cancel too.
    /// </remarks>
    /// <returns>How many symbols the room holds.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check, the symbols read so far in <paramref name="room"/>.</exception>
    internal static int DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, ref TResult[] room, CancellationToken cancellationToken)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
        => DecodeSymbols<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, ref room, default, cancellationToken);

    /// <summary>The rented-room form given the strided rows' hits a read of the same readings kept (<see cref="SharedRowHits"/>).</summary>
    internal static int DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, ref TResult[] room, in SharedRowHits shared, CancellationToken cancellationToken)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        TResult[]? growing = room;
        try
        {
            return DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, room, ref growing, shared, cancellationToken);
        }
        finally
        {
            room = growing!;
        }
    }

    /// <summary>
    /// The passes into <paramref name="symbols"/>. <paramref name="growing"/> is the array form's room, replaced by the one the passes ended with, after a cancel too, so that the caller returns that one, and <c>null</c> for the caller's room.
    /// </summary>
    private static int DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, Span<TResult> symbols, ref TResult[]? growing, in SharedRowHits shared, CancellationToken cancellationToken)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return 0;
        luminance = luminance.Slice(0, pixelCount);

        var maxChars = decoder.MaxChars;
        var moduleLength = decoder.ModuleBufferLength;
        var rentedChars = ArrayPool<char>.Shared.Rent(maxChars);
        var rentedModules = ArrayPool<byte>.Shared.Rent(moduleLength);
        // Two lists: the strided scan's, whose candidates the sweep does not try again, and the sweep's. A caller's room also keeps the strided rows' hits for the sweep.
        // They are rented apart because one rental of them would be over the large object heap's 85,000-byte threshold, and each alone is under it.
        var rentedStrided = ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>());
        var rentedSwept = ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>());
        var rentedKept = growing is null ? ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>()) : null;
        var rowCount = FinderPatternFinder.StridedRowCount(height);
        var rentedRowEnds = growing is null ? ArrayPool<int>.Shared.Rent(rowCount) : null;
        var rentedImage = ArrayPool<byte>.Shared.Rent(pixelCount);
        var search = new SymbolSearch<TResult>
        {
            Symbols = symbols,
            Growing = growing,
            Token = cancellationToken,
            Chars = rentedChars.AsSpan(0, maxChars),
            Modules = rentedModules.AsSpan(0, moduleLength),
            Kept = rentedKept is null ? default : MemoryMarshal.Cast<byte, FinderPattern>(rentedKept.AsSpan()).Slice(0, MaxSymbolCandidates),
            RowEnds = rentedRowEnds is null ? default : rentedRowEnds.AsSpan(0, rowCount),
            Shared = shared,
        };
        try
        {
            var strided = MemoryMarshal.Cast<byte, FinderPattern>(rentedStrided.AsSpan()).Slice(0, MaxSymbolCandidates);
            var swept = MemoryMarshal.Cast<byte, FinderPattern>(rentedSwept.AsSpan()).Slice(0, MaxSymbolCandidates);
            DecodeSymbolsInPasses<TDecoder, TInfo, TResult>(ref decoder, ref search, luminance, width, height, rentedImage.AsSpan(0, pixelCount), strided, swept);
            return search.Count;
        }
        finally
        {
            growing = search.Growing;
            ArrayPool<byte>.Shared.Return(rentedImage, clearArray: false);
            if (rentedRowEnds is not null)
                ArrayPool<int>.Shared.Return(rentedRowEnds, clearArray: false);
            if (rentedKept is not null)
                ArrayPool<byte>.Shared.Return(rentedKept, clearArray: false);
            ArrayPool<byte>.Shared.Return(rentedSwept, clearArray: false);
            ArrayPool<byte>.Shared.Return(rentedStrided, clearArray: false);
            ArrayPool<byte>.Shared.Return(rentedModules, clearArray: false);
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// The passes of <see cref="ImageDecodePasses"/> in their order, none ended by a read: the global threshold of each polarity, the regional binarization of each, and the midpoint sweep of each polarity whose global pass found no finder.
    /// </summary>
    /// <remarks><paramref name="buffer"/>, one byte a pixel, takes the inverted image, then each binarization.</remarks>
    private static void DecodeSymbolsInPasses<TDecoder, TInfo, TResult>(ref TDecoder decoder, ref SymbolSearch<TResult> search, scoped ReadOnlySpan<byte> luminance, int width, int height, scoped Span<byte> buffer, scoped Span<FinderPattern> strided, scoped Span<FinderPattern> swept)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        decoder.FillHistogram(luminance, histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var positiveGrey);
        var noFinder = DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(luminance, width, height, positiveThreshold, positiveGrey), strided, swept, sweepOnly: false, SharedRowHits.Positive);
        if (search.IsFull)
            return;

        var inverted = buffer;
        LuminanceInverter.Invert(luminance, inverted);
        Binarizer.InvertHistogram(histogram);
        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var negativeGrey);
        var invertedNoFinder = DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, negativeThreshold, negativeGrey), strided, swept, sweepOnly: false, SharedRowHits.Negative);
        if (search.IsFull)
            return;

        // Only 0 and 255: no regional threshold would move a pixel to the other class
        var scratchLength = LocalBinarizer.ScratchLength(width, height);
        if (scratchLength != 0 && !RegionalRetry.HoldsOnlyExtremes(histogram))
        {
            // The negative is complemented as it loads, so the inverted image's buffer takes each binarization
            var rentedScratch = ArrayPool<int>.Shared.Rent(scratchLength);
            try
            {
                // The negative is binarized from the block statistics the positive left in the scratch
                var measured = false;
                for (var polarity = 0; polarity < 2 && !search.IsFull; polarity++)
                {
                    var negative = polarity == 1;
                    if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, negative ? negativeThreshold : positiveThreshold, inverted, rentedScratch, ref measured, out var darkCount))
                        continue;
                    histogram.Clear();
                    histogram[LocalBinarizer.Dark] = darkCount;
                    histogram[LocalBinarizer.Light] = inverted.Length - darkCount;
                    var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
                    DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, threshold, grey), strided, swept, sweepOnly: false, SharedRowHits.RegionalPositive + polarity);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rentedScratch);
            }
        }

        if (noFinder && !search.IsFull && ImageDecodePasses.TryMidpoint(positiveThreshold, positiveGrey, out var positiveMidpoint))
            DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(luminance, width, height, positiveMidpoint, positiveGrey), strided, swept, sweepOnly: true, reading: -1);
        if (invertedNoFinder && !search.IsFull && ImageDecodePasses.TryMidpoint(negativeThreshold, negativeGrey, out var negativeMidpoint))
        {
            // The regional pass wrote its binarization into this buffer
            LuminanceInverter.Invert(luminance, inverted);
            DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, negativeMidpoint, negativeGrey), strided, swept, sweepOnly: true, reading: -1);
        }
    }

    /// <summary>
    /// One reading of the image: the strided scan's candidates, then the full sweep's less those the strided scan found, each list decoded in turn (<see cref="DecodeSymbolsOfScan"/>). With <paramref name="sweepOnly"/>, the sweep alone, as the midpoint pass scans.
    /// </summary>
    /// <remarks>
    /// A room that grows never fills, so the sweep runs whatever the strided candidates read, and both lists come from one pass over the rows (<see cref="FinderPatternFinder.FindCandidatesStridedAndFullSweep(ReadOnlySpan{byte}, int, int, byte, in GreyLevels, Span{FinderPattern}, out int, Span{FinderPattern}, out int, FinderPatternFinder.KeptRows)"/>).
    /// A caller's room can fill on the strided candidates, so the sweep is scanned only after them, and it merges the hits the strided scan kept in those rows' places (<see cref="FinderPatternFinder.FindCandidatesKeepingRowHits(ReadOnlySpan{byte}, int, int, byte, in GreyLevels, Span{FinderPattern}, out int, Span{FinderPattern}, Span{int}, int, FinderPatternFinder.KeptRows)"/>).
    /// </remarks>
    /// <returns>Whether neither scan found a finder candidate.</returns>
    private static bool DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref TDecoder decoder, ref SymbolSearch<TResult> search, scoped ImageView image, scoped Span<FinderPattern> strided, scoped Span<FinderPattern> swept, bool sweepOnly, int reading)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        // Every reading checks once at least, so an image where nothing is decoded still sees the token
        search.Token.ThrowIfCancellationRequested();
        var stridedCount = 0;
        var sweptCount = 0;
        if (!sweepOnly)
        {
            var onePass = search.Growing is not null;
            // The rows a read before this one kept of this reading, merged by both scans where they come to one, so neither scans it
            search.Shared.TryTake(reading, image.Height, out var given);
            if (onePass)
                FinderPatternFinder.FindCandidatesStridedAndFullSweep(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, strided, out stridedCount, swept, out sweptCount, given);
            else
                FinderPatternFinder.FindCandidatesKeepingRowHits(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, strided, out stridedCount, search.Kept, search.RowEnds, FinderPatternFinder.CandidateRowStride, given);
            FinderPatternFinder.RankByConfirmation(strided.Slice(0, stridedCount));
            DecodeSymbolsOfScan<TDecoder, TInfo, TResult>(ref decoder, ref search, image, strided.Slice(0, stridedCount), skip: default);
            if (search.IsFull)
                return false;
            if (!onePass)
                sweptCount = FinderPatternFinder.FindCandidatesFullSweepReplaying(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, swept, new FinderPatternFinder.KeptRows(search.Kept, search.RowEnds, FinderPatternFinder.CandidateRowStride), given);
        }
        else
        {
            sweptCount = FinderPatternFinder.FindCandidatesFullSweep(image.Luminance, image.Width, image.Height, image.Threshold, swept, image.Grey);
        }

        FinderPatternFinder.RankByConfirmation(swept.Slice(0, sweptCount));
        // A candidate equal to one the strided scan found decodes the same way: read, it lies inside its symbol, and otherwise it fails again
        DecodeSymbolsOfScan<TDecoder, TInfo, TResult>(ref decoder, ref search, image, swept.Slice(0, sweptCount), strided.Slice(0, stridedCount));
        return stridedCount == 0 && sweptCount == 0;
    }

    /// <summary>
    /// Decodes the <paramref name="ranked"/> candidates in their order, less those equal to one in <paramref name="skip"/> and those inside a symbol read so far, and adds each symbol read.
    /// </summary>
    /// <remarks>
    /// A symbol's own finder lies inside its outline, so the skip is also what keeps a symbol from being read again by a later scan or pass.
    /// </remarks>
    private static void DecodeSymbolsOfScan<TDecoder, TInfo, TResult>(ref TDecoder decoder, ref SymbolSearch<TResult> search, scoped ImageView image, scoped ReadOnlySpan<FinderPattern> ranked, scoped ReadOnlySpan<FinderPattern> skip)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        foreach (var candidate in ranked)
        {
            if (search.IsFull)
                return;
            if (FinderPatternFinder.ContainsCandidate(skip, candidate) || IsInsideRead<TDecoder, TInfo, TResult>(ref decoder, search.Symbols.Slice(0, search.Count), candidate))
                continue;
            search.Token.ThrowIfCancellationRequested();

            // The candidate's attempts see only its own results
            var result = new SearchResult<TInfo>(ReportRule.Furthest, decoder.NotDetected);
            var status = decoder.DecodeCandidate(image, candidate, search.Modules, search.Chars, out var charsWritten, out var info, ref result);
            if (status != DecodeStatus.Success)
                continue;
            search.Add(decoder.Result(search.Chars.Slice(0, charsWritten).ToString(), info));
        }
    }

    /// <summary>Whether a candidate lies inside a symbol read so far: a finder-like pattern in that symbol's own data, or its own finder found again.</summary>
    private static bool IsInsideRead<TDecoder, TInfo, TResult>(ref TDecoder decoder, scoped ReadOnlySpan<TResult> read, in FinderPattern candidate)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        foreach (var symbol in read)
        {
            if (SymbolGeometry.Contains(decoder.ResultCorners(symbol), candidate.X, candidate.Y))
                return true;
        }
        return false;
    }
}
