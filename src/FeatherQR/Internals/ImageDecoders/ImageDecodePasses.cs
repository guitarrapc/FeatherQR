using System.Buffers;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A decoder's symbol pass: its finder search and pipeline on one reading of the image, which <see cref="ImageDecodePasses"/> runs once per pass.
/// </summary>
/// <remarks>
/// <see cref="ILuminanceAttempt{TInfo}.Decode"/> is the pass on a regional binarization. A decoder implements this on a struct, so the passes call it directly.
/// </remarks>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface ISymbolPass<TInfo> : ILuminanceAttempt<TInfo>
{
    /// <summary>Whether the decoder has the midpoint pass: a constant of the implementing struct, so the passes drop it where it is false.</summary>
    bool HasMidpointPass { get; }

    /// <summary>The diagnostics of an image not decoded at all.</summary>
    TInfo NotDetected { get; }

    /// <summary>
    /// The pass at the global threshold of <paramref name="histogram"/>, as <see cref="ILuminanceAttempt{TInfo}.Decode"/>; also the threshold and grey levels it used, and whether it found no finder candidate at all, which only a decoder with the midpoint pass reports.
    /// </summary>
    DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info, out bool noFinder, out byte threshold, out GreyLevels grey);

    /// <summary>The full finder sweep at <paramref name="threshold"/>, the midpoint of <paramref name="grey"/>.</summary>
    DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out TInfo info);
}

/// <summary>
/// The passes every image decoder reads an image in: the global threshold, the inverted image, the regional binarization of each polarity, and, for a decoder that has it, a sweep at the midpoint of the grey levels for each polarity whose global pass found no finder.
/// </summary>
/// <remarks>
/// A pass runs only when the passes before it read nothing; a read ends the sequence, and so does a verdict on the content, except that the negative pass still runs after one from the positive.
/// When nothing reads or gives a verdict, the global positive pass is reported.
/// The midpoint pass is for a finder the global threshold cannot see: edge greys pull that threshold toward light, and a blurred finder's light ring can keep too few pixels above it; halfway between the two levels the ring reads its width.
/// The rules and why they are so are in the architecture record (qrcode-symbologies.md, image decode passes).
/// </remarks>
internal static class ImageDecodePasses
{
    /// <summary>
    /// Reads the image in the passes, with the decoder's symbol pass.
    /// </summary>
    /// <typeparam name="TPass">The decoder's symbol pass; a struct, so the calls are direct.</typeparam>
    /// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
    /// <param name="pass">The decoder's symbol pass.</param>
    /// <param name="luminance">Grayscale pixels, row-major, <paramref name="width"/> × <paramref name="height"/> bytes read.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where the pass writes the decoded characters.</param>
    /// <param name="charsWritten">Characters written; 0 unless the image decoded, since a failing pass can stop after a segment was written.</param>
    /// <param name="info">The reported pass's diagnostics.</param>
    public static DecodeStatus Decode<TPass, TInfo>(ref TPass pass, ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TPass : struct, ISymbolPass<TInfo>
    {
        var status = DecodeInPasses<TPass, TInfo>(ref pass, luminance, width, height, destination, out charsWritten, out info);
        if (status != DecodeStatus.Success)
            charsWritten = 0;
        return status;
    }

    private static DecodeStatus DecodeInPasses<TPass, TInfo>(ref TPass pass, ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TPass : struct, ISymbolPass<TInfo>
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
        {
            charsWritten = 0;
            info = pass.NotDetected;
            return DecodeStatus.NotDetected;
        }

        luminance = luminance.Slice(0, pixelCount);
        // One count serves both polarities: the negative's histogram is this one mirrored
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var status = pass.DecodeGlobal(luminance, histogram, width, height, destination, out charsWritten, out info, out var noFinder, out var positiveThreshold, out var positiveGrey);
        if (IsTerminal(status))
            return status;

        // Reflectance reversal: the image inverted into a rented buffer, taken only on the failure path, so a read and a
        // genuinely short destination stay allocation-free
        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var inverted = rented.AsSpan(0, pixelCount);
            LuminanceInverter.Invert(luminance, inverted);
            Binarizer.InvertHistogram(histogram);

            var invertedStatus = pass.DecodeGlobal(inverted, histogram, width, height, destination, out charsWritten, out var invertedInfo, out var invertedNoFinder, out var negativeThreshold, out var negativeGrey);
            if (IsTerminal(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // A verdict skips the regional pass, which looks for a symbol the global threshold did not see
            if (RegionalRetry.IsContentVerdict(status))
                return status;
            if (RegionalRetry.IsContentVerdict(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // Uneven lighting: each polarity binarized again against each region's own level
            var regionalStatus = RegionalRetry.Decode<TPass, TInfo>(ref pass, luminance, inverted, histogram, width, height, destination, out charsWritten, out var regionalInfo);
            if (IsTerminal(regionalStatus) || RegionalRetry.IsContentVerdict(regionalStatus))
            {
                info = regionalInfo;
                return regionalStatus;
            }

            // Last, and only for a polarity whose global threshold found no finder at all: a symbol the regional pass reads never pays for it
            if (pass.HasMidpointPass)
            {
                if (noFinder && TryMidpoint(positiveThreshold, positiveGrey, out var positiveMidpoint))
                {
                    var midpointStatus = pass.DecodeAtMidpoint(luminance, width, height, positiveMidpoint, positiveGrey, destination, out charsWritten, out var midpointInfo);
                    if (IsTerminal(midpointStatus) || RegionalRetry.IsContentVerdict(midpointStatus))
                    {
                        info = midpointInfo;
                        return midpointStatus;
                    }
                }
                if (invertedNoFinder && TryMidpoint(negativeThreshold, negativeGrey, out var negativeMidpoint))
                {
                    // The regional pass wrote its binarization into this buffer
                    LuminanceInverter.Invert(luminance, inverted);
                    var midpointStatus = pass.DecodeAtMidpoint(inverted, width, height, negativeMidpoint, negativeGrey, destination, out charsWritten, out var midpointInfo);
                    if (IsTerminal(midpointStatus) || RegionalRetry.IsContentVerdict(midpointStatus))
                    {
                        info = midpointInfo;
                        return midpointStatus;
                    }
                }
            }

            // Every attempt failed short of the content: report the first one's diagnostics
            return status;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// The midpoint of <paramref name="grey"/> to the nearest level, unless the image has no grey levels or it is <paramref name="threshold"/>, where the sweep would read what the global pass read.
    /// </summary>
    private static bool TryMidpoint(byte threshold, in GreyLevels grey, out byte midpoint)
    {
        midpoint = 0;
        if (!grey.IsEnabled)
            return false;
        midpoint = (byte)Math.Round(grey.Midpoint);
        return midpoint != threshold;
    }

    /// <summary>A read, or a read too long for the destination, which only a larger destination changes.</summary>
    private static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;
}
