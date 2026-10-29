using System.Buffers;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR;

/// <summary>
/// Reads every symbol of an image whatever its symbology: Standard QR's read for every symbol, then Micro QR and rMQR on one scan of their passes, then Standard QR again when either read anything.
/// </summary>
/// <remarks>
/// <para>
/// Standard QR comes first, since a triple that reads is the strongest claim on three finders, and its passes are its own: its candidate list and the rows it scans between differ from those of the single-finder scan.
/// Micro QR and rMQR scan the same passes, so they share one scan of each (<see cref="CandidateScan"/>), and each candidate is decoded as a Micro QR symbol, then as an rMQR one if that does not read.
/// A candidate inside a symbol read so far, by either scan, is decoded by neither: it is that symbol's finder or a finder-like pattern in its data.
/// </para>
/// <para>
/// The finders of Micro QR and rMQR symbols can keep the Standard QR stages from reading a QR code that they read once those finders are gone. So when either reads anything, Standard QR reads again with every symbol read so far taken as read (<see cref="QRImageDecoder.DecodeSymbolsBeside(ReadOnlySpan{byte}, int, int, ReadOnlySpan{int}, Span{QRCodeDecodeResult}, int)"/>).
/// An image where neither reads, a photograph of Standard QR codes among them, pays nothing for it.
/// </para>
/// <para>
/// The results are in that order: Standard QR's first reads, then Micro QR's and rMQR's as found, then the codes Standard QR's second read adds.
/// </para>
/// <para>
/// The image's histogram is filled once and given to every read, since each would fill it from the same pixels at full size.
/// </para>
/// </remarks>
internal static class MixedImageDecoder
{
    /// <summary>The room past the symbols read so far that Standard QR's second read starts with, doubled each time it fills.</summary>
    private const int GrowingRoom = 8;

    /// <summary>
    /// Decodes every symbol of a grayscale image, of any of the three symbologies, into <paramref name="symbols"/>, as many as it has room for, each once.
    /// </summary>
    /// <remarks>Nothing but the texts is allocated.</remarks>
    /// <returns>How many symbols were written.</returns>
    internal static int DecodeSymbols(ReadOnlySpan<byte> luminance, int width, int height, Span<MixedSymbol> symbols)
    {
        if (symbols.IsEmpty || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return 0;
        luminance = luminance.Slice(0, pixelCount);

        // Standard QR reads into a room of its own result type. Its second read holds every symbol read before it, so the room is the caller's size
        var rented = ArrayPool<QRCodeDecodeResult>.Shared.Rent(symbols.Length);
        // Rented, not on the stack, since the single-finder decoder holds it
        var rentedHistogram = ArrayPool<int>.Shared.Rent(Binarizer.HistogramBins);
        try
        {
            var histogram = rentedHistogram.AsSpan(0, Binarizer.HistogramBins);
            Binarizer.FillHistogram(luminance, histogram);
            var room = rented.AsSpan(0, symbols.Length);
            var count = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, histogram, room, seeded: 0);
            for (var i = 0; i < count; i++)
                symbols[i] = new MixedSymbol(room[i]);
            if (count == symbols.Length)
                return count;

            var decoder = new SingleFinderDecoder(rented, count, rentedHistogram);
            var single = CandidateScan.DecodeSymbols<SingleFinderDecoder, SingleFinderInfo, MixedSymbol>(ref decoder, luminance, width, height, symbols.Slice(count));
            var read = count + single;
            if (single == 0 || read == symbols.Length)
                return read;

            for (var i = count; i < read; i++)
                room[i] = TakenAsRead(symbols[i]);
            var total = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, histogram, room, read);
            for (var i = read; i < total; i++)
                symbols[i] = new MixedSymbol(room[i]);
            return total;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedHistogram);
            // The room holds the texts, which the pool would otherwise keep alive
            ArrayPool<QRCodeDecodeResult>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Decodes every symbol of a grayscale image, of any of the three symbologies, as <see cref="DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{MixedSymbol})"/> does with room for all of them.
    /// </summary>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled, at the next check.</exception>
    internal static MixedSymbol[] DecodeSymbols(ReadOnlySpan<byte> luminance, int width, int height, CancellationToken cancellationToken = default)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            return [];
        luminance = luminance.Slice(0, pixelCount);

        var rentedHistogram = ArrayPool<int>.Shared.Rent(Binarizer.HistogramBins);
        try
        {
            var histogram = rentedHistogram.AsSpan(0, Binarizer.HistogramBins);
            Binarizer.FillHistogram(luminance, histogram);
            var standard = new QRCodeDecodeResult[GrowingRoom];
            var standardCount = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, histogram, ref standard, seeded: 0, cancellationToken);
            var decoder = new SingleFinderDecoder(standard, standardCount, rentedHistogram);
            var single = CandidateScan.DecodeSymbols<SingleFinderDecoder, SingleFinderInfo, MixedSymbol>(ref decoder, luminance, width, height, cancellationToken);
            var read = standardCount + single.Length;
            if (read == 0)
                return [];

            var total = read;
            var room = standard;
            if (single.Length != 0)
            {
                room = new QRCodeDecodeResult[read + GrowingRoom];
                standard.AsSpan(0, standardCount).CopyTo(room);
                for (var i = 0; i < single.Length; i++)
                    room[standardCount + i] = TakenAsRead(single[i]);
                total = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, histogram, ref room, read, cancellationToken);
            }

            var symbols = new MixedSymbol[total];
            for (var i = 0; i < standardCount; i++)
                symbols[i] = new MixedSymbol(standard[i]);
            single.CopyTo(symbols, standardCount);
            for (var i = read; i < total; i++)
                symbols[i] = new MixedSymbol(room[i]);
            return symbols;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedHistogram);
        }
    }

    /// <summary>
    /// A symbol of another symbology as Standard QR's second read takes it, for its corners alone. It never leaves this class.
    /// </summary>
    private static QRCodeDecodeResult TakenAsRead(in MixedSymbol symbol)
        => new(symbol.Text, new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, 0, 0).WithCorners(symbol.Corners));

    /// <summary>A single-finder decode's diagnostics: Micro QR's, or rMQR's.</summary>
    private readonly struct SingleFinderInfo
    {
        public SingleFinderInfo(in MicroQRCodeDecodeInfo info) => MicroQR = info;

        public SingleFinderInfo(in RmQRCodeDecodeInfo info)
        {
            IsRmQR = true;
            RmQR = info;
        }

        public bool IsRmQR { get; }

        public MicroQRCodeDecodeInfo MicroQR { get; }

        public RmQRCodeDecodeInfo RmQR { get; }

        public SymbolCorners Corners => IsRmQR ? RmQR.Corners : MicroQR.Corners;

        public SingleFinderInfo WithoutCorners() => IsRmQR ? new SingleFinderInfo(RmQR.WithCorners(default)) : new SingleFinderInfo(MicroQR.WithCorners(default));
    }

    /// <summary>
    /// Micro QR's decode of a finder candidate, then rMQR's when that does not end the candidate, for the scan that returns every symbol: one scan of the passes serves both.
    /// A candidate inside a Standard QR symbol read before the scan, one of the first <paramref name="standardCount"/> of <paramref name="standard"/>, is decoded by neither.
    /// The scan's histogram is <paramref name="histogram"/>, the image's, which the Standard QR read was given.
    /// </summary>
    private readonly struct SingleFinderDecoder(QRCodeDecodeResult[] standard, int standardCount, int[] histogram) : ISymbolsDecoder<SingleFinderInfo, MixedSymbol>
    {
        private static MicroQRImageDecoder.CandidateDecoder Micro => default;

        private static RmQRImageDecoder.CandidateDecoder Rmqr => default;

        private readonly QRCodeDecodeResult[] _standard = standard;
        private readonly int _standardCount = standardCount;
        private readonly int[] _histogram = histogram;

        public int MaxChars => Math.Max(Micro.MaxChars, Rmqr.MaxChars);

        public MixedSymbol Result(string text, in SingleFinderInfo info)
            => info.IsRmQR ? new MixedSymbol(new RmQRCodeDecodeResult(text, info.RmQR)) : new MixedSymbol(new MicroQRCodeDecodeResult(text, info.MicroQR));

        public SymbolCorners ResultCorners(in MixedSymbol result) => result.Corners;

        public void FillHistogram(ReadOnlySpan<byte> luminance, Span<int> histogram) => _histogram.AsSpan(0, Binarizer.HistogramBins).CopyTo(histogram);

        public SingleFinderInfo NotDetected => new(Micro.NotDetected);

        public int ModuleBufferLength => Math.Max(Micro.ModuleBufferLength, Rmqr.ModuleBufferLength);

        public SymbolCorners Corners(in SingleFinderInfo info) => info.Corners;

        public SingleFinderInfo WithoutCorners(in SingleFinderInfo info) => info.WithoutCorners();

        public DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out SingleFinderInfo info, ref SearchResult<SingleFinderInfo> result)
        {
            if (IsInsideStandardQR(candidate))
                return result.Report(out charsWritten, out info);

            // Each decoder sees the buffers its own scan gives it
            var micro = new SearchResult<MicroQRCodeDecodeInfo>(ReportRule.Furthest, Micro.NotDetected);
            var status = Micro.DecodeCandidate(image, candidate, modules.Slice(0, Micro.ModuleBufferLength), destination.Slice(0, Micro.MaxChars), out charsWritten, out var microInfo, ref micro);
            if (AttemptStatus.IsTerminal(status))
            {
                info = new SingleFinderInfo(microInfo);
                return status;
            }
            result.Other(status, charsWritten, new SingleFinderInfo(microInfo));

            var rmqr = new SearchResult<RmQRCodeDecodeInfo>(ReportRule.Furthest, Rmqr.NotDetected);
            status = Rmqr.DecodeCandidate(image, candidate, modules.Slice(0, Rmqr.ModuleBufferLength), destination.Slice(0, Rmqr.MaxChars), out charsWritten, out var rmqrInfo, ref rmqr);
            if (AttemptStatus.IsTerminal(status))
            {
                info = new SingleFinderInfo(rmqrInfo);
                return status;
            }
            result.Other(status, charsWritten, new SingleFinderInfo(rmqrInfo));
            return result.Report(out charsWritten, out info);
        }

        private bool IsInsideStandardQR(in FinderPattern candidate)
        {
            for (var i = 0; i < _standardCount; i++)
            {
                if (SymbolGeometry.Contains(_standard[i].Info.Corners, candidate.X, candidate.Y))
                    return true;
            }
            return false;
        }
    }
}
