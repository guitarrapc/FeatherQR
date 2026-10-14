using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FeatherQR.Internals.ImageDecoders;
using static FeatherQR.Internals.ImageDecoders.AttemptStatus;

namespace FeatherQR.Internals.StandardQR;

/// <summary>One symbol read from an image: its text, and what the decode found, its corners in the image among it.</summary>
internal readonly struct DecodedSymbol(string text, QRCodeDecodeInfo info)
{
    public string Text { get; } = text;

    public QRCodeDecodeInfo Info { get; } = info;
}

internal static partial class QRImageDecoder
{
    /// <summary>The candidates a pass's list starts with room for: 85 symbols' finders.</summary>
    private const int SymbolCandidates = 256;

    /// <summary>The longest list a pass grows to when its scan fills one.</summary>
    private const int MaxSymbolCandidates = 4096;

    /// <summary>How many times the passes are run over one image at most.</summary>
    private const int MaxSymbolRounds = 2;

    /// <summary>What one call of <see cref="DecodeSymbols"/> carries through its passes.</summary>
    private ref struct SymbolSearch
    {
        /// <summary>The caller's room, the first <see cref="Count"/> of it read.</summary>
        public Span<DecodedSymbol> Symbols;

        public int Count;

        /// <summary>The text buffer every decode writes into.</summary>
        public Span<char> Chars;

        /// <summary>The candidates the passes of this round gave up on, in the pixels of the image: the first that fit, and <see cref="LeftOut"/> when there were more.</summary>
        public Span<FinderPattern> Left;

        public int LeftCount;

        public bool LeftOut;

        public readonly bool IsFull => Count == Symbols.Length;

        public readonly ReadOnlySpan<DecodedSymbol> Read => Symbols.Slice(0, Count);

    }

    /// <summary>
    /// Whether the passes could read more if they were run again: a pass gave up on candidates, a read after it took some of them, and at least a triple's worth is left. Or the passes gave up on more candidates than were kept.
    /// </summary>
    /// <remarks>
    /// A pass gives up where its best triples do not decode, and they can be the finders of a symbol only a later pass reads. With that symbol read its finders are gone from the list, and the pass selects from the others.
    /// A pass whose list no later read touched would give up the same way again.
    /// </remarks>
    /// <param name="left">The candidates the round's passes gave up on, in the pixels of the image. Those inside a symbol of <paramref name="read"/> are taken out.</param>
    /// <param name="leftOut">Whether the passes gave up on more than <paramref name="left"/> holds.</param>
    /// <param name="read">The symbols read so far.</param>
    internal static bool IsWorthAnotherRound(Span<FinderPattern> left, bool leftOut, ReadOnlySpan<DecodedSymbol> read)
    {
        if (leftOut)
            return true;
        var count = left.Length;
        RemoveCandidatesOfSymbols(left, ref count, read, scale: 1);
        return count != left.Length && count >= 3;
    }

    /// <summary>
    /// Decodes every QR code of a grayscale image into <paramref name="symbols"/>, as many as it has room for, each once and in the order found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The image is read in the passes of <see cref="DecodeLuminance"/>, in their order. A read does not end them: each pass gives every symbol its candidate list holds, and the passes go on until the room is filled.
    /// So the first symbol is the one <see cref="DecodeLuminance"/> reads, and an image of one symbol costs what that call's failing passes cost on top of the read, unless there is room for one only.
    /// </para>
    /// <para>
    /// The passes are run a second time when the first round read a symbol whose finders an earlier pass of it had given up on (<see cref="IsWorthAnotherRound"/>).
    /// </para>
    /// <para>
    /// Nothing but the texts is allocated.
    /// </para>
    /// </remarks>
    /// <returns>How many symbols were written.</returns>
    internal static int DecodeSymbols(ReadOnlySpan<byte> luminance, int width, int height, Span<DecodedSymbol> symbols)
    {
        if (symbols.IsEmpty || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return 0;
        luminance = luminance.Slice(0, pixelCount);

        // The version is not known before a symbol reads, so the text buffer is the largest symbol's
        var maxChars = QRMatrixDecoder.GetMaxCharCount(40);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxChars);
        try
        {
            Span<FinderPattern> left = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var search = new SymbolSearch { Symbols = symbols, Chars = rentedChars.AsSpan(0, maxChars), Left = left };
            for (var round = 0; round < MaxSymbolRounds; round++)
            {
                var before = search.Count;
                search.LeftCount = 0;
                search.LeftOut = false;
                DecodeSymbolsInPasses(ref search, luminance, width, height);
                if (search.Count == before || search.IsFull || !IsWorthAnotherRound(search.Left.Slice(0, search.LeftCount), search.LeftOut, search.Read))
                    break;
            }
            return search.Count;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>One round: the passes of <see cref="ImageDecodePasses"/> in their order, at full size and then at each reduced scale, none ended by a read.</summary>
    private static void DecodeSymbolsInPasses(ref SymbolSearch search, scoped ReadOnlySpan<byte> luminance, int width, int height)
    {
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        DecodeSymbolsOfReading(ref search, luminance, default, negative: false, histogram, width, height, scale: 1);
        if (search.IsFull)
            return;

        // Read before the later passes overwrite the histogram, as the one-symbol passes do
        var searchReduced = !RegionalRetry.HoldsOnlyExtremes(histogram);
        var rented = ArrayPool<byte>.Shared.Rent(luminance.Length);
        try
        {
            var buffer = rented.AsSpan(0, luminance.Length);
            DecodeSymbolsAfterPositive(ref search, luminance, histogram, width, height, scale: 1, buffer);

            var above = luminance;
            for (var scale = 2; searchReduced && !search.IsFull && Math.Min(width, height) / 2 >= ImageDecodePasses.MinimumReducedSide; scale *= 2)
            {
                int levelWidth = width / 2, levelHeight = height / 2;
                var level = buffer.Slice(0, levelWidth * levelHeight);
                LuminanceHalver.Halve(above, width, height, level);

                Binarizer.FillHistogram(level, histogram);
                DecodeSymbolsOfReading(ref search, level, default, negative: false, histogram, levelWidth, levelHeight, scale);
                if (!search.IsFull)
                    DecodeSymbolsAfterPositive(ref search, level, histogram, levelWidth, levelHeight, scale, buffer.Slice(level.Length, level.Length));

                above = level;
                width = levelWidth;
                height = levelHeight;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// The passes after the global positive one, on one size of the image: the negative, then the regional binarization of each polarity, each giving what symbols it holds that are not read yet.
    /// </summary>
    /// <remarks>
    /// <paramref name="buffer"/>, one byte a pixel, takes the inverted image and then each binarization, and <paramref name="histogram"/>, the positive's, is overwritten.
    /// </remarks>
    private static void DecodeSymbolsAfterPositive(ref SymbolSearch search, scoped ReadOnlySpan<byte> luminance, scoped Span<int> histogram, int width, int height, int scale, scoped Span<byte> buffer)
    {
        var inverted = buffer.Slice(0, luminance.Length);
        LuminanceInverter.Invert(luminance, inverted);
        Binarizer.InvertHistogram(histogram);
        DecodeSymbolsOfReading(ref search, inverted, default, negative: false, histogram, width, height, scale);
        if (search.IsFull)
            return;

        // Only 0 and 255: no regional threshold would move a pixel to the other class
        var scratchLength = LocalBinarizer.ScratchLength(width, height);
        if (scratchLength == 0 || RegionalRetry.HoldsOnlyExtremes(histogram))
            return;

        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        Binarizer.InvertHistogram(histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);

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
                DecodeSymbolsOfReading(ref search, inverted, luminance, negative, histogram, width, height, scale);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedScratch);
        }
    }

    /// <summary>
    /// One reading of the image, as <see cref="DecodeLuminanceCore(ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, ReadOnlySpan{int}, int, int, Span{char}, out int, out QRCodeDecodeInfo)"/> takes it: every symbol its finder candidates give that is not read yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows are scanned once, into one list. After a read the candidates inside the symbol's outline leave the list, and the stages run again on what is left, so each symbol is found as the one-symbol search finds it in an image where the symbols before it are gone.
    /// The candidates inside a symbol an earlier pass read leave the list before anything is selected, so no pass reads a symbol again.
    /// </para>
    /// <para>
    /// The stages read the head of the list, as many candidates as the one-symbol search holds. When they give up on a head with more of the list behind it, that head leaves the list and the stages go on with the next, so symbols that do not read cannot hide the ones scanned after them.
    /// </para>
    /// <para>
    /// <paramref name="scale"/> is how many pixels of the image given to <see cref="DecodeSymbols"/> one pixel of this reading is, along each axis. The symbols are kept in that image's pixels.
    /// </para>
    /// </remarks>
    private static void DecodeSymbolsOfReading(ref SymbolSearch search, scoped ReadOnlySpan<byte> luminance, scoped ReadOnlySpan<byte> source, bool negative, scoped ReadOnlySpan<int> histogram, int width, int height, int scale)
    {
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        // The list is rented as bytes: a pool of candidates would be one more pool in every build, and the pools the one-symbol call rents from would be laid out around it
        var capacity = SymbolCandidates;
        var rented = ArrayPool<byte>.Shared.Rent(capacity * Unsafe.SizeOf<FinderPattern>());
        try
        {
            // A scan that fills its list may have left finders out, so it is run again with twice the room
            var candidates = MemoryMarshal.Cast<byte, FinderPattern>(rented.AsSpan()).Slice(0, capacity);
            var candidateCount = FinderPatternFinder.FindCandidatesStrided(luminance, width, height, threshold, candidates, grey);
            while (candidateCount == capacity && capacity < MaxSymbolCandidates)
            {
                capacity *= 2;
                var larger = ArrayPool<byte>.Shared.Rent(capacity * Unsafe.SizeOf<FinderPattern>());
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                rented = larger;
                candidates = MemoryMarshal.Cast<byte, FinderPattern>(rented.AsSpan()).Slice(0, capacity);
                candidateCount = FinderPatternFinder.FindCandidatesStrided(luminance, width, height, threshold, candidates, grey);
            }
            RemoveCandidatesOfSymbols(candidates, ref candidateCount, search.Read, scale);

            var image = new ImageView(luminance, source, negative, width, height, threshold, grey);
            var everyRowScanned = false;
            var modules = new ModuleWorkspace();
            try
            {
                while (!search.IsFull)
                {
                    var status = DecodeNextSymbol(image, candidates, ref candidateCount, ref everyRowScanned, search.Read, scale, ref modules, search.Chars, out var charsWritten, out var info, out var tried);
                    if (status == DecodeStatus.Success)
                    {
                        var read = scale == 1 ? info : info.WithCorners(SymbolGeometry.Scaled(info.Corners, scale));
                        // The read's finders are inside its outline. Were none removed, the next selection would be this one again
                        var before = candidateCount;
                        RemoveCandidatesInside(candidates, ref candidateCount, read.Corners, scale);
                        if (!IsReadAlready(search.Read, read.Corners))
                            search.Symbols[search.Count++] = new DecodedSymbol(search.Chars.Slice(0, charsWritten).ToString(), read);
                        if (candidateCount == before)
                            break;
                        continue;
                    }

                    // A symbol whose content does not decode stays unread, and its finders leave the list so that the others can be reached
                    if (IsContentVerdict(status) && RemoveCandidates(candidates, ref candidateCount, tried))
                        continue;

                    // The stages gave up on the head of the list
                    var head = Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates);
                    if (head >= 3)
                        KeepAsLeft(ref search, candidates.Slice(0, head), scale);
                    if (candidateCount == head)
                        break;
                    candidates.Slice(head, candidateCount - head).CopyTo(candidates);
                    candidateCount -= head;
                }
            }
            finally
            {
                modules.Dispose();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>Keeps the candidates a pass gave up on for the round's end, in the pixels of the image, as many as there is room for.</summary>
    private static void KeepAsLeft(ref SymbolSearch search, scoped ReadOnlySpan<FinderPattern> candidates, int scale)
    {
        foreach (var candidate in candidates)
        {
            if (search.LeftCount == search.Left.Length)
            {
                search.LeftOut = true;
                return;
            }
            var kept = candidate;
            kept.X *= scale;
            kept.Y *= scale;
            search.Left[search.LeftCount++] = kept;
        }
    }

    /// <summary>
    /// The next symbol of a candidate list: the stages of <see cref="DecodeLuminanceCore(ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, ReadOnlySpan{int}, int, int, Span{char}, out int, out QRCodeDecodeInfo)"/> in their order, on the first <see cref="FinderPatternFinder.MaxFinderCandidates"/> of a list kept between symbols, where that method scans one for itself.
    /// </summary>
    /// <remarks>
    /// The two are kept apart so that the one-symbol call runs the instructions it ran before this entry was written. A stage added to one is added to the other, and <c>SeveralSymbolsDecodeTest</c> holds the first symbol of this one to that call's read.
    /// Rows the strided scan skipped are scanned once a reading, into the whole list, and the candidates they add inside a symbol already read leave it at once.
    /// <paramref name="tried"/> is the selected triple when its own decode gave the result, for a caller that takes its finders out of the list, and empty otherwise.
    /// </remarks>
    private static DecodeStatus DecodeNextSymbol(in ImageView image, Span<FinderPattern> candidates, ref int candidateCount, ref bool everyRowScanned, ReadOnlySpan<DecodedSymbol> read, int scale, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out SelectedTriple tried)
    {
        charsWritten = 0;
        tried = default;

        Span<FinderPattern> patterns = stackalloc FinderPattern[3];
        var swept = everyRowScanned;
        var found = FinderPatternFinder.TrySelectFromList(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, candidates, ref candidateCount, ref everyRowScanned, patterns);
        if (everyRowScanned != swept && RemoveCandidatesOfSymbols(candidates, ref candidateCount, read, scale))
            found = FinderPatternFinder.TrySelectKeepingList(candidates.Slice(0, Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates)), patterns);

        var held = candidates.Slice(0, Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates));
        if (!found && (!everyRowScanned || held.Length < 2))
        {
            info = NotDetectedInfo;
            return DecodeStatus.NotDetected;
        }
        if (!found)
            return DecodeInferred(image, held, ref modules, destination, out charsWritten, out info);

        var corners = new ImageCorners();
        var status = DecodeTriple(ref corners, image, patterns, ref modules, destination, out charsWritten, out info);
        if (IsSettled(status))
        {
            tried = new SelectedTriple(patterns);
            return status;
        }
        if (held.Length > 3)
        {
            status = DecodeAlternativeTriples(ref corners, image, held, patterns, ref modules, destination, status, ref charsWritten, ref info);
            if (IsSettled(status))
                return status;
        }

        var result = NewResult();
        result.Main(status, charsWritten, info);

        if (!everyRowScanned)
        {
            if (!FinderPatternFinder.IsUndersampled(patterns, image.Height) && !IsCrowded(held))
                return status;
            FinderPatternFinder.FindCandidatesInSkippedRows(image.Luminance, image.Width, image.Height, image.Threshold, candidates, ref candidateCount, image.Grey);
            everyRowScanned = true;
            RemoveCandidatesOfSymbols(candidates, ref candidateCount, read, scale);
            held = candidates.Slice(0, Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates));

            Span<FinderPattern> sweptPatterns = stackalloc FinderPattern[3];
            if (FinderPatternFinder.TrySelectKeepingList(held, sweptPatterns) && !SameTriple(sweptPatterns, patterns))
            {
                var sweptStatus = DecodeTriple(ref corners, image, sweptPatterns, ref modules, destination, out var sweptCharsWritten, out var sweptInfo);
                if (result.Other(sweptStatus, sweptCharsWritten, sweptInfo))
                {
                    tried = new SelectedTriple(sweptPatterns);
                    return result.Report(out charsWritten, out info);
                }
                if (held.Length > 3)
                {
                    sweptStatus = DecodeAlternativeTriples(ref corners, image, held, sweptPatterns, ref modules, destination, sweptStatus, ref sweptCharsWritten, ref sweptInfo);
                    if (result.Other(sweptStatus, sweptCharsWritten, sweptInfo))
                        return result.Report(out charsWritten, out info);
                }
                sweptPatterns.CopyTo(patterns);
            }
        }

        var crowdedStatus = DecodeCrowded(ref corners, image, held, patterns, ref modules, destination, out var crowdedCharsWritten, out var crowdedInfo);
        if (result.Other(crowdedStatus, crowdedCharsWritten, crowdedInfo))
            return result.Report(out charsWritten, out info);

        var inferredStatus = DecodeInferred(image, held, ref modules, destination, out var inferredCharsWritten, out var inferredInfo);
        result.Other(inferredStatus, inferredCharsWritten, inferredInfo);
        return result.Report(out charsWritten, out info);
    }

    /// <summary>The three finders of a selected triple, by value.</summary>
    private readonly struct SelectedTriple(ReadOnlySpan<FinderPattern> patterns)
    {
        public readonly FinderPattern First = patterns[0];
        public readonly FinderPattern Second = patterns[1];
        public readonly FinderPattern Third = patterns[2];
        public readonly bool HasValue = true;
    }

    /// <summary>
    /// Whether a symbol read is one of <paramref name="symbols"/>: each of its corners within a quarter of its shorter side of that one's.
    /// </summary>
    /// <remarks>
    /// A second reading of a symbol, by another pass, is that symbol again. Symbols beside each other, and one drawn inside another, differ by more at some corner, so neither is dropped for the other.
    /// </remarks>
    internal static bool IsReadAlready(ReadOnlySpan<DecodedSymbol> symbols, in SymbolCorners corners)
    {
        var near = Math.Min(DistanceSquared(corners.TopLeft, corners.TopRight), DistanceSquared(corners.TopLeft, corners.BottomLeft)) / 16f;
        foreach (var symbol in symbols)
        {
            var other = symbol.Info.Corners;
            if (DistanceSquared(corners.TopLeft, other.TopLeft) <= near && DistanceSquared(corners.TopRight, other.TopRight) <= near
                && DistanceSquared(corners.BottomRight, other.BottomRight) <= near && DistanceSquared(corners.BottomLeft, other.BottomLeft) <= near)
            {
                return true;
            }
        }
        return false;

        static float DistanceSquared(ImagePoint a, ImagePoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    }

    /// <summary>Takes the candidates inside the outline of any of <paramref name="symbols"/> out of the list, the others keeping their order. True when any left.</summary>
    private static bool RemoveCandidatesOfSymbols(Span<FinderPattern> candidates, ref int candidateCount, ReadOnlySpan<DecodedSymbol> symbols, int scale)
    {
        var before = candidateCount;
        foreach (var symbol in symbols)
            RemoveCandidatesInside(candidates, ref candidateCount, symbol.Info.Corners, scale);
        return candidateCount != before;
    }

    /// <summary>Takes the candidates inside <paramref name="corners"/> out of the list. The corners are in the pixels of the image, and a candidate's centre is <paramref name="scale"/> times as far in them.</summary>
    private static void RemoveCandidatesInside(Span<FinderPattern> candidates, ref int candidateCount, in SymbolCorners corners, int scale)
    {
        var kept = 0;
        for (var i = 0; i < candidateCount; i++)
        {
            if (!SymbolGeometry.Contains(corners, candidates[i].X * scale, candidates[i].Y * scale))
                candidates[kept++] = candidates[i];
        }
        candidateCount = kept;
    }

    /// <summary>Takes a tried triple's finders out of the list. False when there is no triple or none of its finders is in the list as it was selected.</summary>
    private static bool RemoveCandidates(Span<FinderPattern> candidates, ref int candidateCount, in SelectedTriple triple)
    {
        if (!triple.HasValue)
            return false;
        Span<FinderPattern> patterns = stackalloc FinderPattern[3];
        patterns[0] = triple.First;
        patterns[1] = triple.Second;
        patterns[2] = triple.Third;
        var kept = 0;
        for (var i = 0; i < candidateCount; i++)
        {
            if (!FinderPatternFinder.ContainsCandidate(patterns, candidates[i]))
                candidates[kept++] = candidates[i];
        }
        var removed = kept != candidateCount;
        candidateCount = kept;
        return removed;
    }
}
