using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FeatherQR.Internals.ImageDecoders;
using static FeatherQR.Internals.ImageDecoders.AttemptStatus;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>The candidates a reading's list has room for, every row scanned: 1,365 symbols' finders.</summary>
    private const int MaxSymbolCandidates = 4096;

    /// <summary>The room the array form starts with, the smallest array the pool rents, doubled each time it fills.</summary>
    private const int GrowingRoom = 16;

    /// <summary>How many times the passes are run over one image at most.</summary>
    private const int MaxSymbolRounds = 2;

    /// <summary>What one call of <see cref="DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{QRCodeDecodeResult}, CancellationToken)"/> carries through its passes.</summary>
    private ref struct SymbolSearch
    {
        /// <summary>The room, the first <see cref="Count"/> of it read.</summary>
        public Span<QRCodeDecodeResult> Symbols;

        public int Count;

        /// <summary>The array form's room, rented from the pool, which grows where the caller's room fills: <c>null</c> for the caller's room.</summary>
        public QRCodeDecodeResult[]? Growing;

        /// <summary>Checked before each symbol is tried, never inside one symbol's read.</summary>
        public CancellationToken Token;

        /// <summary>The text buffer every decode writes into.</summary>
        public Span<char> Chars;

        /// <summary>The finders the passes of this round gave up on, in the pixels of the image, each once: the first that fit, and <see cref="LeftOut"/> when there were more.</summary>
        public Span<FinderPattern> Left;

        public int LeftCount;

        public bool LeftOut;

        public readonly bool IsFull => Growing is null && Count == Symbols.Length;

        public readonly ReadOnlySpan<QRCodeDecodeResult> Read => Symbols.Slice(0, Count);

        public void Add(QRCodeDecodeResult symbol)
        {
            if (Count == Symbols.Length)
            {
                // Only the array form gets here: the passes stop before a symbol the caller's room has no space for
                var grown = ArrayPool<QRCodeDecodeResult>.Shared.Rent(Symbols.Length * 2);
                Symbols.CopyTo(grown);
                // The room holds the texts, which the pool would otherwise keep alive
                ArrayPool<QRCodeDecodeResult>.Shared.Return(Growing!, clearArray: true);
                Growing = grown;
                Symbols = grown;
            }
            Symbols[Count++] = symbol;
        }

    }

    /// <summary>
    /// Whether the passes could read more if they were run again: a pass gave up on candidates, a read after it took some of them, and at least a triple's worth is left. Or the passes gave up on more finders than were kept.
    /// </summary>
    /// <remarks>
    /// A pass gives up where its best triples do not decode, and they can be the finders of a symbol only a later pass reads. With that symbol read its finders are gone from the list, and the pass selects from the others.
    /// A pass whose list no later read touched would give up the same way again.
    /// </remarks>
    /// <param name="left">The finders the round's passes gave up on, each once, in the pixels of the image. Those inside a symbol of <paramref name="read"/> are taken out.</param>
    /// <param name="leftOut">Whether the passes gave up on more than <paramref name="left"/> holds.</param>
    /// <param name="read">The symbols read so far.</param>
    internal static bool IsWorthAnotherRound(Span<FinderPattern> left, bool leftOut, ReadOnlySpan<QRCodeDecodeResult> read)
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
    /// So an image of one symbol costs what that call's failing passes cost on top of the read, and a second round when one runs, unless there is room for one only.
    /// </para>
    /// <para>
    /// The first symbol is the one <see cref="DecodeLuminance"/> reads, unless a pass of that call ends on a symbol whose content gives a verdict, or on candidates its stages give up on with more of the list behind them.
    /// This entry goes on in that pass, so its first symbol can be one that call reads in a later pass or not at all.
    /// </para>
    /// <para>
    /// The passes are run a second time when the first round read a symbol whose finders an earlier pass of it had given up on, or gave up on more finders than it keeps (<see cref="IsWorthAnotherRound"/>).
    /// </para>
    /// <para>
    /// Nothing but the texts is allocated.
    /// </para>
    /// </remarks>
    /// <returns>How many symbols were written.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check, the symbols read so far left in <paramref name="symbols"/>.</exception>
    internal static int DecodeSymbols(ReadOnlySpan<byte> luminance, int width, int height, Span<QRCodeDecodeResult> symbols, CancellationToken cancellationToken = default)
    {
        if (symbols.IsEmpty)
            return 0;
        QRCodeDecodeResult[]? none = null;
        return DecodeSymbolsCore(luminance, width, height, symbols, ref none, cancellationToken);
    }

    /// <summary>
    /// Decodes every QR code of a grayscale image, as <see cref="DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{QRCodeDecodeResult}, CancellationToken)"/> does with room for all of them.
    /// </summary>
    /// <remarks>
    /// The room grows as it fills instead of ending the passes, so the image is read once however many symbols it holds.
    /// The array returned and the texts are allocated, and nothing else: the room is rented.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check.</exception>
    internal static QRCodeDecodeResult[] DecodeSymbols(ReadOnlySpan<byte> luminance, int width, int height, CancellationToken cancellationToken = default)
    {
        var room = ArrayPool<QRCodeDecodeResult>.Shared.Rent(GrowingRoom);
        try
        {
            var count = DecodeSymbolsGrowing(luminance, width, height, ref room, cancellationToken);
            return count == 0 ? [] : room.AsSpan(0, count).ToArray();
        }
        finally
        {
            // The room holds the texts, which the pool would otherwise keep alive
            ArrayPool<QRCodeDecodeResult>.Shared.Return(room, clearArray: true);
        }
    }

    /// <summary>
    /// The array form's read into <paramref name="room"/>, an array rented from <see cref="ArrayPool{T}.Shared"/>: each time it fills, a rental of twice its length takes its place and it goes back to the pool cleared. The caller returns the last, after a cancel too.
    /// </summary>
    private static int DecodeSymbolsGrowing(ReadOnlySpan<byte> luminance, int width, int height, ref QRCodeDecodeResult[] room, CancellationToken cancellationToken)
    {
        QRCodeDecodeResult[]? growing = room;
        try
        {
            return DecodeSymbolsCore(luminance, width, height, room, ref growing, cancellationToken);
        }
        finally
        {
            room = growing!;
        }
    }

    /// <summary>
    /// The rounds into <paramref name="symbols"/>. <paramref name="growing"/> is the array form's room, replaced by the one the rounds ended with, after a cancel too, so that the caller returns that one, and <c>null</c> for the caller's room.
    /// </summary>
    private static int DecodeSymbolsCore(ReadOnlySpan<byte> luminance, int width, int height, Span<QRCodeDecodeResult> symbols, ref QRCodeDecodeResult[]? growing, CancellationToken cancellationToken)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return 0;
        luminance = luminance.Slice(0, pixelCount);

        // The version is not known before a symbol reads, so the text buffer is the largest symbol's
        var maxChars = QRMatrixDecoder.GetMaxCharCount(40);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxChars);
        Span<FinderPattern> left = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var search = new SymbolSearch { Symbols = symbols, Growing = growing, Token = cancellationToken, Chars = rentedChars.AsSpan(0, maxChars), Left = left };
        try
        {
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
            growing = search.Growing;
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes every QR code of a grayscale image that is not one of the first <paramref name="seeded"/> symbols of <paramref name="symbols"/>, after them, as many as there is room for.
    /// </summary>
    /// <remarks>
    /// The seeded symbols are taken as read, whatever their symbology, since only their corners are looked at: the candidates inside them leave every list before anything is selected, and a read at their corners is not added.
    /// The finders of other symbologies' symbols can keep the stages from reading a QR code, which they read with those finders out of the lists.
    /// </remarks>
    /// <param name="luminance">Grayscale pixels, row-major, <paramref name="width"/> × <paramref name="height"/> bytes read.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="histogram">The image's histogram, as <see cref="Binarizer.FillHistogram"/> fills it from those pixels. Each round's passes at full size take their thresholds from it.</param>
    /// <param name="symbols">The seeded symbols, then the room the codes read are written to.</param>
    /// <param name="seeded">How many symbols at the start of <paramref name="symbols"/> are taken as read.</param>
    /// <returns>How many symbols the room holds, the seeded ones included.</returns>
    internal static int DecodeSymbolsBeside(ReadOnlySpan<byte> luminance, int width, int height, ReadOnlySpan<int> histogram, Span<QRCodeDecodeResult> symbols, int seeded)
    {
        if (seeded >= symbols.Length)
            return seeded;
        QRCodeDecodeResult[]? none = null;
        return DecodeSymbolsBesideCore(luminance, width, height, histogram, symbols, seeded, ref none, CancellationToken.None);
    }

    /// <summary>
    /// Decodes every QR code of a grayscale image that is not one of the first <paramref name="seeded"/> symbols of <paramref name="room"/>, as <see cref="DecodeSymbolsBeside(ReadOnlySpan{byte}, int, int, ReadOnlySpan{int}, Span{QRCodeDecodeResult}, int)"/> does in a room that grows as it fills.
    /// </summary>
    /// <remarks>
    /// <paramref name="room"/> is an array rented from <see cref="ArrayPool{T}.Shared"/> holding the seeded symbols: each time it fills, a rental of twice its length takes its place and it goes back to the pool cleared. The caller returns the last, after a cancel too.
    /// </remarks>
    /// <returns>How many symbols the room holds, the seeded ones included.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check.</exception>
    internal static int DecodeSymbolsBeside(ReadOnlySpan<byte> luminance, int width, int height, ReadOnlySpan<int> histogram, ref QRCodeDecodeResult[] room, int seeded, CancellationToken cancellationToken)
    {
        // The room doubles as it fills, so an empty one could not grow
        System.Diagnostics.Debug.Assert(room.Length > 0);
        QRCodeDecodeResult[]? growing = room;
        try
        {
            return DecodeSymbolsBesideCore(luminance, width, height, histogram, room, seeded, ref growing, cancellationToken);
        }
        finally
        {
            room = growing!;
        }
    }

    /// <summary><see cref="DecodeSymbolsCore"/> with the first <paramref name="seeded"/> symbols of the room taken as read, and the image's histogram given.</summary>
    /// <remarks>The rounds are that method's, written out again: shared through a method the JIT inlines, they moved that method's stack frame.</remarks>
    private static int DecodeSymbolsBesideCore(ReadOnlySpan<byte> luminance, int width, int height, ReadOnlySpan<int> histogram, Span<QRCodeDecodeResult> symbols, int seeded, ref QRCodeDecodeResult[]? growing, CancellationToken cancellationToken)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return seeded;
        luminance = luminance.Slice(0, pixelCount);

        var maxChars = QRMatrixDecoder.GetMaxCharCount(40);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxChars);
        Span<FinderPattern> left = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var search = new SymbolSearch { Symbols = symbols, Count = seeded, Growing = growing, Token = cancellationToken, Chars = rentedChars.AsSpan(0, maxChars), Left = left };
        try
        {
            for (var round = 0; round < MaxSymbolRounds; round++)
            {
                var before = search.Count;
                search.LeftCount = 0;
                search.LeftOut = false;
                DecodeSymbolsInPasses(ref search, luminance, histogram, width, height);
                if (search.Count == before || search.IsFull || !IsWorthAnotherRound(search.Left.Slice(0, search.LeftCount), search.LeftOut, search.Read))
                    break;
            }
            return search.Count;
        }
        finally
        {
            growing = search.Growing;
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>One round of <see cref="DecodeSymbolsInPasses(ref SymbolSearch, ReadOnlySpan{byte}, int, int)"/>, with the image's histogram given in place of one filled from the pixels.</summary>
    /// <remarks>Written out again with that line changed, so that the method that fills the histogram keeps its code.</remarks>
    private static void DecodeSymbolsInPasses(ref SymbolSearch search, scoped ReadOnlySpan<byte> luminance, scoped ReadOnlySpan<int> imageHistogram, int width, int height)
    {
        // A copy: the passes overwrite it, and each round starts from the image's
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        imageHistogram.CopyTo(histogram);
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
    /// <paramref name="scale"/> is how many pixels of the image given to <see cref="DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{QRCodeDecodeResult}, CancellationToken)"/> one pixel of this reading is, along each axis. The symbols are kept in that image's pixels.
    /// </para>
    /// </remarks>
    private static void DecodeSymbolsOfReading(ref SymbolSearch search, scoped ReadOnlySpan<byte> luminance, scoped ReadOnlySpan<byte> source, bool negative, scoped ReadOnlySpan<int> histogram, int width, int height, int scale)
    {
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        // The list is rented as bytes: a pool of candidates would be one more pool in every build, and the pools the one-symbol call rents from would be laid out around it.
        // It has its whole room from the start, since the rows between can bring more finders than the strided scan found, and a full list drops them
        var rented = ArrayPool<byte>.Shared.Rent(MaxSymbolCandidates * Unsafe.SizeOf<FinderPattern>());
        try
        {
            var candidates = MemoryMarshal.Cast<byte, FinderPattern>(rented.AsSpan()).Slice(0, MaxSymbolCandidates);
            var candidateCount = FinderPatternFinder.FindCandidatesStrided(luminance, width, height, threshold, candidates, grey);
            RemoveCandidatesOfSymbols(candidates, ref candidateCount, search.Read, scale);

            var image = new ImageView(luminance, source, negative, width, height, threshold, grey);
            var everyRowScanned = false;
            var modules = new ModuleWorkspace();
            try
            {
                while (!search.IsFull)
                {
                    // Every reading enters this loop once at least, so an image where nothing reads still sees the token
                    search.Token.ThrowIfCancellationRequested();
                    var status = DecodeNextSymbol(image, candidates, ref candidateCount, ref everyRowScanned, search.Read, scale, ref modules, search.Chars, out var charsWritten, out var info, out var tried);
                    if (status == DecodeStatus.Success)
                    {
                        var read = scale == 1 ? info : info.WithCorners(SymbolGeometry.Scaled(info.Corners, scale));
                        // The read's finders are inside its outline. Were none removed, the next selection would be this one again
                        var before = candidateCount;
                        RemoveCandidatesInside(candidates, ref candidateCount, read.Corners, scale);
                        if (!IsReadAlready(search.Read, read.Corners))
                            search.Add(new QRCodeDecodeResult(search.Chars.Slice(0, charsWritten).ToString(), read));
                        if (candidateCount == before)
                            break;
                        continue;
                    }

                    // A symbol whose content does not decode stays unread, and its finders leave the list so that the others can be reached
                    if (IsContentVerdict(status) && RemoveCandidates(candidates, ref candidateCount, tried))
                        continue;

                    // The stages gave up on the head of the list
                    var head = Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates);
                    if (head >= 3 && !KeepAsLeft(search.Left, ref search.LeftCount, candidates.Slice(0, head), scale))
                        search.LeftOut = true;
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

    /// <summary>
    /// Keeps the candidates a pass gave up on for the round's end, in the pixels of the image, as many as there is room for.
    /// The readings of a round scan the same finders, so a finder another reading gave up on is kept once.
    /// </summary>
    /// <returns>False when a finder found no room.</returns>
    internal static bool KeepAsLeft(Span<FinderPattern> left, ref int leftCount, scoped ReadOnlySpan<FinderPattern> candidates, int scale)
    {
        foreach (var candidate in candidates)
        {
            var kept = candidate;
            kept.X *= scale;
            kept.Y *= scale;
            kept.ModuleSize *= scale;
            if (ContainsFinder(left.Slice(0, leftCount), kept))
                continue;
            if (leftCount == left.Length)
                return false;
            left[leftCount++] = kept;
        }
        return true;
    }

    /// <summary>
    /// Whether one of <paramref name="finders"/> is the finder <paramref name="candidate"/> is: its centre within half a finder's width of the candidate's.
    /// Two finders are a finder's width apart at least, and the same finder moves by less between readings, between scales and when the rows between are averaged in.
    /// </summary>
    private static bool ContainsFinder(scoped ReadOnlySpan<FinderPattern> finders, in FinderPattern candidate)
    {
        var near = 3.5f * candidate.ModuleSize;
        near *= near;
        foreach (var finder in finders)
        {
            float dx = finder.X - candidate.X, dy = finder.Y - candidate.Y;
            if (dx * dx + dy * dy <= near)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The next symbol of a candidate list: the stages of <see cref="DecodeLuminanceCore(ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, ReadOnlySpan{int}, int, int, Span{char}, out int, out QRCodeDecodeInfo)"/> in their order, on the first <see cref="FinderPatternFinder.MaxFinderCandidates"/> of a list kept between symbols, where that method scans one for itself.
    /// </summary>
    /// <remarks>
    /// The two are kept apart so that the one-symbol call carries nothing this entry keeps between symbols. A stage added to one is added to the other, and <c>SeveralSymbolsDecodeTest</c> holds the first symbol of this one to that call's read.
    /// Rows the strided scan skipped are scanned once a reading, into the whole list, and the candidates they add inside a symbol already read leave it at once.
    /// <paramref name="tried"/> is the triple whose decode gave a verdict on its content, at whichever stage, for a caller that takes its finders out of the list, and empty otherwise.
    /// </remarks>
    private static DecodeStatus DecodeNextSymbol(in ImageView image, Span<FinderPattern> candidates, ref int candidateCount, ref bool everyRowScanned, ReadOnlySpan<QRCodeDecodeResult> read, int scale, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out SelectedTriple tried)
    {
        var corners = new VerdictCorners();
        var status = DecodeNextSymbolStages(ref corners, image, candidates, ref candidateCount, ref everyRowScanned, read, scale, ref modules, destination, out charsWritten, out info, out var inferred);
        tried = !IsContentVerdict(status) ? default : corners.Verdict.HasValue ? corners.Verdict : inferred;
        return status;
    }

    /// <summary>
    /// The corner steps on the image, as <see cref="ImageCorners"/> takes them, keeping the triple a decode gave a verdict on its content for.
    /// The stages stop at a verdict, so the one kept is the one reported.
    /// </summary>
    private struct VerdictCorners : ICornerAttempt
    {
        public SelectedTriple Verdict;

        public readonly bool ReadsTimingPatterns(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
            => TimingPatternsRead(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight, bottomLeft);

        public DecodeStatus Decode(in ImageView image, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        {
            var status = DecodeCorners(image, topLeft, topRight, bottomLeft, ref modules, destination, out charsWritten, out info);
            if (IsContentVerdict(status))
                Verdict = new SelectedTriple(topLeft, topRight, bottomLeft);
            return status;
        }
    }

    /// <summary>The stages of <see cref="DecodeNextSymbol"/>. <paramref name="inferred"/> is the triple the inferred stage settled on, when that stage reported.</summary>
    private static DecodeStatus DecodeNextSymbolStages(ref VerdictCorners corners, in ImageView image, Span<FinderPattern> candidates, ref int candidateCount, ref bool everyRowScanned, ReadOnlySpan<QRCodeDecodeResult> read, int scale, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out SelectedTriple inferred)
    {
        charsWritten = 0;
        inferred = default;

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
            return DecodeInferred(image, held, ref modules, destination, out charsWritten, out info, out inferred);

        var status = DecodeTriple(ref corners, image, patterns, ref modules, destination, out charsWritten, out info);
        if (IsSettled(status))
            return status;
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
            {
                if (TryCornerByTimingPatterns(ref corners, image, patterns, out _, out _, out _))
                    return status;
                var strideInferredStatus = DecodeInferred(image, held, ref modules, destination, out var strideInferredCharsWritten, out var strideInferredInfo, out var strideSettled);
                if (result.Other(strideInferredStatus, strideInferredCharsWritten, strideInferredInfo))
                    inferred = strideSettled;
                return result.Report(out charsWritten, out info);
            }
            FinderPatternFinder.FindCandidatesInSkippedRows(image.Luminance, image.Width, image.Height, image.Threshold, candidates, ref candidateCount, image.Grey);
            everyRowScanned = true;
            RemoveCandidatesOfSymbols(candidates, ref candidateCount, read, scale);
            held = candidates.Slice(0, Math.Min(candidateCount, FinderPatternFinder.MaxFinderCandidates));

            Span<FinderPattern> sweptPatterns = stackalloc FinderPattern[3];
            if (FinderPatternFinder.TrySelectKeepingList(held, sweptPatterns) && !SameTriple(sweptPatterns, patterns))
            {
                var sweptStatus = DecodeTriple(ref corners, image, sweptPatterns, ref modules, destination, out var sweptCharsWritten, out var sweptInfo);
                if (result.Other(sweptStatus, sweptCharsWritten, sweptInfo))
                    return result.Report(out charsWritten, out info);
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

        var inferredStatus = DecodeInferred(image, held, ref modules, destination, out var inferredCharsWritten, out var inferredInfo, out var settled);
        if (result.Other(inferredStatus, inferredCharsWritten, inferredInfo))
            inferred = settled;
        return result.Report(out charsWritten, out info);
    }

    /// <summary>The three finders of a triple, by value.</summary>
    internal readonly struct SelectedTriple(FinderPattern first, FinderPattern second, FinderPattern third)
    {
        public readonly FinderPattern First = first;
        public readonly FinderPattern Second = second;
        public readonly FinderPattern Third = third;
        public readonly bool HasValue = true;
    }

    /// <summary>
    /// Whether a symbol read is one of <paramref name="symbols"/>: each of its corners within a quarter of its shorter side of that one's.
    /// </summary>
    /// <remarks>
    /// A second reading of a symbol, by another pass, is that symbol again. Symbols beside each other, and one drawn inside another, differ by more at some corner, so neither is dropped for the other.
    /// </remarks>
    internal static bool IsReadAlready(ReadOnlySpan<QRCodeDecodeResult> symbols, in SymbolCorners corners)
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
    private static bool RemoveCandidatesOfSymbols(Span<FinderPattern> candidates, ref int candidateCount, ReadOnlySpan<QRCodeDecodeResult> symbols, int scale)
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

    /// <summary>Takes a tried triple's finders out of the list. False when there is no triple or none of its finders is in the list.</summary>
    /// <remarks>A triple is matched by <see cref="ContainsFinder"/>, not by value: the stages can decode a triple selected before the rows between were averaged into its candidates.</remarks>
    internal static bool RemoveCandidates(Span<FinderPattern> candidates, ref int candidateCount, in SelectedTriple triple)
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
            if (!ContainsFinder(patterns, candidates[i]))
                candidates[kept++] = candidates[i];
        }
        var removed = kept != candidateCount;
        candidateCount = kept;
        return removed;
    }
}
