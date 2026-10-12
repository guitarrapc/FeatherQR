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

    /// <summary>Whether the decoder reads the image again at reduced scale when nothing settles at full size: a constant of the implementing struct, so the passes drop the search where it is false.</summary>
    bool HasReducedScaleSearch { get; }

    /// <summary>The diagnostics of an image not decoded at all.</summary>
    TInfo NotDetected { get; }

    /// <summary>
    /// The pass at the global threshold of <paramref name="histogram"/>, as <see cref="ILuminanceAttempt{TInfo}.Decode"/>; also the threshold and grey levels it used, and whether it found no finder candidate at all, which only a decoder with the midpoint pass reports.
    /// </summary>
    DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info, out bool noFinder, out byte threshold, out GreyLevels grey);

    /// <summary>The full finder sweep at <paramref name="threshold"/>, the midpoint of <paramref name="grey"/>.</summary>
    DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out TInfo info);

    /// <summary>
    /// <paramref name="info"/>, found on the image reduced to one pixel for every <paramref name="scale"/> by <paramref name="scale"/>, with whatever it holds in pixels put in the pixels of the full image.
    /// </summary>
    TInfo AtFullScale(in TInfo info, int scale);
}

/// <summary>
/// The passes every image decoder reads an image in: the global threshold, the inverted image, the regional binarization of each polarity, and, for a decoder that has it, a sweep at the midpoint of the grey levels for each polarity whose global pass found no finder.
/// A decoder with the reduced-scale search then reads the image halved through the same passes, and halved again, when nothing settled and the image is not black and white alone.
/// </summary>
/// <remarks>
/// A pass runs only when the passes before it read nothing; a read ends the sequence, and so does a verdict on the content, except that the negative pass still runs after one from the positive.
/// When nothing reads or gives a verdict, the global positive pass at full size is reported.
/// The midpoint pass is for a finder the global threshold cannot see: edge greys pull that threshold toward light, and a blurred finder's light ring can keep too few pixels above it; halfway between the two levels the ring reads its width.
/// The reduced-scale search is for a symbol under noise or a texture finer than its modules, which breaks a finder's runs at full size and averages out of the image halved.
/// The rules and why they are so are in the architecture record (qrcode-symbologies.md, image decode passes).
/// </remarks>
internal static class ImageDecodePasses
{
    /// <summary>The shorter side, in pixels, the reduced-scale search stops under: no level is read whose image would be smaller.</summary>
    internal const int MinimumReducedSide = 64;

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
        var status = pass.DecodeGlobal(luminance, histogram, width, height, destination, out charsWritten, out info, out var noFinder, out var threshold, out var grey);
        if (AttemptStatus.IsTerminal(status))
            return status;

        // Read before the later passes overwrite the histogram. Black and white alone is a rendered image, left out for what its failures would pay
        var searchReduced = pass.HasReducedScaleSearch && !RegionalRetry.HoldsOnlyExtremes(histogram);

        // One rented buffer for everything past the first pass, taken only on the failure path, so a read and a genuinely
        // short destination stay allocation-free: the inverted image, then each reduced level beside its own inverted image
        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var buffer = rented.AsSpan(0, pixelCount);
            status = DecodeAfterPositive<TPass, TInfo>(ref pass, luminance, histogram, width, height, buffer, destination, status, noFinder, threshold, grey, ref charsWritten, ref info);
            if (AttemptStatus.IsSettled(status) || !searchReduced)
                return status;
            return DecodeReduced<TPass, TInfo>(ref pass, luminance, histogram, width, height, buffer, destination, status, ref charsWritten, ref info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// The passes after the global positive one, which gave <paramref name="status"/> short of a read: the negative, the regional of each polarity, and the midpoint sweeps.
    /// </summary>
    /// <remarks>
    /// <paramref name="buffer"/>, one byte a pixel, takes the inverted image and then each binarization, and <paramref name="histogram"/>, the positive's, is overwritten.
    /// <paramref name="info"/> comes in as the global positive pass's diagnostics and goes out as those of the pass that settles, with <paramref name="charsWritten"/> as that pass set it.
    /// </remarks>
    /// <returns>The status of the pass that settled, or <paramref name="status"/> when none did.</returns>
    private static DecodeStatus DecodeAfterPositive<TPass, TInfo>(ref TPass pass, ReadOnlySpan<byte> luminance, Span<int> histogram, int width, int height, Span<byte> buffer, Span<char> destination, DecodeStatus status, bool noFinder, byte positiveThreshold, in GreyLevels positiveGrey, ref int charsWritten, ref TInfo info)
        where TPass : struct, ISymbolPass<TInfo>
    {
        // Reflectance reversal
        var inverted = buffer.Slice(0, luminance.Length);
        LuminanceInverter.Invert(luminance, inverted);
        Binarizer.InvertHistogram(histogram);

        var invertedStatus = pass.DecodeGlobal(inverted, histogram, width, height, destination, out charsWritten, out var invertedInfo, out var invertedNoFinder, out var negativeThreshold, out var negativeGrey);
        if (AttemptStatus.IsTerminal(invertedStatus))
        {
            info = invertedInfo;
            return invertedStatus;
        }

        // A verdict skips the regional pass, which looks for a symbol the global threshold did not see
        if (AttemptStatus.IsContentVerdict(status))
            return status;
        if (AttemptStatus.IsContentVerdict(invertedStatus))
        {
            info = invertedInfo;
            return invertedStatus;
        }

        // Uneven lighting: each polarity binarized again against each region's own level
        var regionalStatus = RegionalRetry.Decode<TPass, TInfo>(ref pass, luminance, inverted, histogram, width, height, destination, out charsWritten, out var regionalInfo);
        if (AttemptStatus.IsSettled(regionalStatus))
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
                if (AttemptStatus.IsSettled(midpointStatus))
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
                if (AttemptStatus.IsSettled(midpointStatus))
                {
                    info = midpointInfo;
                    return midpointStatus;
                }
            }
        }

        // Every attempt failed short of the content: the first one's diagnostics stand
        return status;
    }

    /// <summary>
    /// The image halved, and halved again while its shorter side stays at least <see cref="MinimumReducedSide"/>, each level read through every pass until one settles.
    /// </summary>
    /// <remarks>
    /// <paramref name="buffer"/> is one byte a pixel of the full image. A level is at most a quarter of it, so the level and its own pass buffer share it, and the next level is halved in place.
    /// <paramref name="status"/> is what full size gave, and is returned, with <paramref name="info"/> untouched, when no level settles.
    /// </remarks>
    private static DecodeStatus DecodeReduced<TPass, TInfo>(ref TPass pass, ReadOnlySpan<byte> luminance, Span<int> histogram, int width, int height, Span<byte> buffer, Span<char> destination, DecodeStatus status, ref int charsWritten, ref TInfo info)
        where TPass : struct, ISymbolPass<TInfo>
    {
        var above = luminance;
        for (var scale = 2; Math.Min(width, height) / 2 >= MinimumReducedSide; scale *= 2)
        {
            int levelWidth = width / 2, levelHeight = height / 2;
            var level = buffer.Slice(0, levelWidth * levelHeight);
            LuminanceHalver.Halve(above, width, height, level);

            Binarizer.FillHistogram(level, histogram);
            var levelStatus = pass.DecodeGlobal(level, histogram, levelWidth, levelHeight, destination, out var levelWritten, out var levelInfo, out var noFinder, out var threshold, out var grey);
            if (!AttemptStatus.IsTerminal(levelStatus))
                levelStatus = DecodeAfterPositive<TPass, TInfo>(ref pass, level, histogram, levelWidth, levelHeight, buffer.Slice(level.Length, level.Length), destination, levelStatus, noFinder, threshold, grey, ref levelWritten, ref levelInfo);
            if (AttemptStatus.IsSettled(levelStatus))
            {
                charsWritten = levelWritten;
                info = pass.AtFullScale(levelInfo, scale);
                return levelStatus;
            }

            above = level;
            width = levelWidth;
            height = levelHeight;
        }
        return status;
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
}
