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
}

internal static partial class CandidateScan
{
    /// <summary>
    /// The candidates a finder scan's list has room for when every symbol is read. The one-symbol scan's 32 would leave finders of a large sheet unread.
    /// A finder past this many is dropped, and nothing reports it.
    /// </summary>
    private const int MaxSymbolCandidates = 4096;

    /// <summary>The room the array form starts with, doubled each time it fills.</summary>
    private const int GrowingRoom = 8;

    /// <summary>What one call of <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/> carries through its passes.</summary>
    private ref struct SymbolSearch<TResult>
    {
        /// <summary>The room, the first <see cref="Count"/> of it read.</summary>
        public Span<TResult> Symbols;

        public int Count;

        /// <summary>The array form's room, which grows where the caller's room fills: <c>null</c> for the caller's room.</summary>
        public TResult[]? Growing;

        /// <summary>Checked as each reading starts and before each candidate is decoded, never inside a candidate's decode.</summary>
        public CancellationToken Token;

        /// <summary>The text buffer every decode writes into: the longest text of the symbology.</summary>
        public Span<char> Chars;

        /// <summary>The module buffer every decode samples into: the largest symbol's.</summary>
        public Span<byte> Modules;

        public readonly bool IsFull => Growing is null && Count == Symbols.Length;

        public void Add(TResult symbol)
        {
            if (Count == Symbols.Length)
            {
                // Only the array form gets here: the passes stop before a symbol the caller's room has no space for
                var grown = new TResult[Symbols.Length * 2];
                Symbols.CopyTo(grown);
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
        return DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, symbols, growing: null, cancellationToken, out _);
    }

    /// <summary>
    /// Decodes every symbol of a grayscale image, as <see cref="DecodeSymbols{TDecoder, TInfo, TResult}(ref TDecoder, ReadOnlySpan{byte}, int, int, Span{TResult}, CancellationToken)"/> does with room for all of them.
    /// </summary>
    /// <remarks>The room grows as it fills instead of ending the passes, so the image is read once however many symbols it holds.</remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check.</exception>
    internal static TResult[] DecodeSymbols<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, CancellationToken cancellationToken = default)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        var room = new TResult[GrowingRoom];
        var count = DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, room, room, cancellationToken, out var grown);
        return count == grown.Length ? grown : grown.AsSpan(0, count).ToArray();
    }

    private static int DecodeSymbolsCore<TDecoder, TInfo, TResult>(ref TDecoder decoder, ReadOnlySpan<byte> luminance, int width, int height, Span<TResult> symbols, TResult[]? growing, CancellationToken cancellationToken, out TResult[] grown)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        grown = growing ?? [];
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return 0;
        luminance = luminance.Slice(0, pixelCount);

        var maxChars = decoder.MaxChars;
        var moduleLength = decoder.ModuleBufferLength;
        var rentedChars = ArrayPool<char>.Shared.Rent(maxChars);
        var rentedModules = ArrayPool<byte>.Shared.Rent(moduleLength);
        // Two lists: the strided scan's, whose candidates the sweep does not try again, and the sweep's.
        // They are rented apart because one rental of both would be over the large object heap's 85,000-byte threshold, and each alone is under it.
        var rentedStrided = ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>());
        var rentedSwept = ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>());
        var rentedImage = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var search = new SymbolSearch<TResult> { Symbols = symbols, Growing = growing, Token = cancellationToken, Chars = rentedChars.AsSpan(0, maxChars), Modules = rentedModules.AsSpan(0, moduleLength) };
            var strided = MemoryMarshal.Cast<byte, FinderPattern>(rentedStrided.AsSpan()).Slice(0, MaxSymbolCandidates);
            var swept = MemoryMarshal.Cast<byte, FinderPattern>(rentedSwept.AsSpan()).Slice(0, MaxSymbolCandidates);
            DecodeSymbolsInPasses<TDecoder, TInfo, TResult>(ref decoder, ref search, luminance, width, height, rentedImage.AsSpan(0, pixelCount), strided, swept);
            grown = search.Growing ?? [];
            return search.Count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedImage, clearArray: false);
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
        Binarizer.FillHistogram(luminance, histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var positiveGrey);
        var noFinder = DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(luminance, width, height, positiveThreshold, positiveGrey), strided, swept, sweepOnly: false);
        if (search.IsFull)
            return;

        var inverted = buffer;
        LuminanceInverter.Invert(luminance, inverted);
        Binarizer.InvertHistogram(histogram);
        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var negativeGrey);
        var invertedNoFinder = DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, negativeThreshold, negativeGrey), strided, swept, sweepOnly: false);
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
                    DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, threshold, grey), strided, swept, sweepOnly: false);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rentedScratch);
            }
        }

        if (noFinder && !search.IsFull && ImageDecodePasses.TryMidpoint(positiveThreshold, positiveGrey, out var positiveMidpoint))
            DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(luminance, width, height, positiveMidpoint, positiveGrey), strided, swept, sweepOnly: true);
        if (invertedNoFinder && !search.IsFull && ImageDecodePasses.TryMidpoint(negativeThreshold, negativeGrey, out var negativeMidpoint))
        {
            // The regional pass wrote its binarization into this buffer
            LuminanceInverter.Invert(luminance, inverted);
            DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref decoder, ref search, new ImageView(inverted, width, height, negativeMidpoint, negativeGrey), strided, swept, sweepOnly: true);
        }
    }

    /// <summary>
    /// One reading of the image: the strided scan, then the full sweep less the candidates the strided scan found, each decoding its candidates (<see cref="DecodeSymbolsOfScan"/>). With <paramref name="sweepOnly"/>, the sweep alone, as the midpoint pass scans.
    /// </summary>
    /// <returns>Whether neither scan found a finder candidate.</returns>
    private static bool DecodeSymbolsOfReading<TDecoder, TInfo, TResult>(ref TDecoder decoder, ref SymbolSearch<TResult> search, scoped ImageView image, scoped Span<FinderPattern> strided, scoped Span<FinderPattern> swept, bool sweepOnly)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        // Every reading checks once at least, so an image where nothing is decoded still sees the token
        search.Token.ThrowIfCancellationRequested();
        var stridedCount = 0;
        if (!sweepOnly)
        {
            stridedCount = FinderPatternFinder.FindCandidates(image.Luminance, image.Width, image.Height, image.Threshold, strided, image.Grey);
            FinderPatternFinder.RankByConfirmation(strided.Slice(0, stridedCount));
            DecodeSymbolsOfScan<TDecoder, TInfo, TResult>(ref decoder, ref search, image, strided.Slice(0, stridedCount), skip: default);
            if (search.IsFull)
                return false;
        }

        var sweptCount = FinderPatternFinder.FindCandidatesFullSweep(image.Luminance, image.Width, image.Height, image.Threshold, swept, image.Grey);
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
