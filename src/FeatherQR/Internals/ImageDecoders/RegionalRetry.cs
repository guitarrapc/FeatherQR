using System.Buffers;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>One decode attempt of an image decoder on a luminance buffer and its histogram.</summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface ILuminanceAttempt<TInfo>
{
    DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info);
}

/// <summary>
/// The retry every image decoder makes when both global polarities have failed: the <see cref="LocalBinarizer"/> output of the positive, then of the negative, each decoded when it moves a pixel out of the class its polarity's global threshold gave it.
/// </summary>
/// <remarks>
/// Each polarity is binarized from its own pixels: a flat block reads as paper, and the paper of a negative is dark, so its regional output is not the positive's inverted.
/// The negative is read from the positive, complemented as it loads, which leaves the inverted retry's buffer free to take the output.
/// A polarity is decoded only when its regional binarization moves a pixel out of the class its global threshold put it in; otherwise the decoder has read that image already.
/// </remarks>
internal static class RegionalRetry
{
    /// <summary>Decodes the regional binarization of the positive, then of the negative unless the positive's was terminal.</summary>
    /// <typeparam name="TAttempt">The decoder's attempt type.</typeparam>
    /// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
    /// <param name="attempt">The decoder's attempt; a struct, so the call is direct.</param>
    /// <param name="luminance">The image, positive.</param>
    /// <param name="buffer">The inverted retry's buffer, one byte a pixel; overwritten with each binarization.</param>
    /// <param name="histogram">The negative's histogram, as the inverted retry left it; overwritten.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where the attempt writes the decoded characters.</param>
    /// <param name="charsWritten">What the last polarity's decode wrote; 0 when that polarity was not decoded.</param>
    /// <param name="info">The last polarity's decode diagnostics; default when that polarity was not decoded.</param>
    /// <returns>
    /// The status of the last polarity reached: its decode's, or <see cref="DecodeStatus.NotDetected"/> when its binarization agreed with its global threshold and it was not decoded.
    /// <see cref="DecodeStatus.NotDetected"/> with no polarity reached when the image holds only 0 and 255 or is under five blocks a side.
    /// Callers act only on a terminal status; any other is not a diagnosis of the image.
    /// </returns>
    public static DecodeStatus Decode<TAttempt, TInfo>(ref TAttempt attempt, ReadOnlySpan<byte> luminance, Span<byte> buffer, Span<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TAttempt : struct, ILuminanceAttempt<TInfo>
    {
        charsWritten = 0;
        info = default!;

        // Only 0 and 255: every regional threshold lies in [0, 251], so both polarities
        // read exactly as their global thresholds did, and binarizing would prove nothing
        var scratchLength = LocalBinarizer.ScratchLength(width, height);
        if (scratchLength == 0 || HoldsOnlyExtremes(histogram))
            return DecodeStatus.NotDetected;

        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        Binarizer.InvertHistogram(histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);

        // Both polarities read the positive, the negative complemented as it loads, so the
        // buffer the negative was inverted into can take the output
        var binarized = buffer.Slice(0, width * height);
        var rentedScratch = ArrayPool<int>.Shared.Rent(scratchLength);
        try
        {
            var status = DecodePolarity(ref attempt, luminance, negative: false, positiveThreshold, binarized, rentedScratch, histogram, width, height, destination, out charsWritten, out info);
            if (status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall)
                return status;
            return DecodePolarity(ref attempt, luminance, negative: true, negativeThreshold, binarized, rentedScratch, histogram, width, height, destination, out charsWritten, out info);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedScratch);
        }
    }

    private static DecodeStatus DecodePolarity<TAttempt, TInfo>(ref TAttempt attempt, ReadOnlySpan<byte> luminance, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, Span<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TAttempt : struct, ILuminanceAttempt<TInfo>
    {
        if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, globalThreshold, binarized, scratch, out var darkCount))
        {
            charsWritten = 0;
            info = default!;
            return DecodeStatus.NotDetected;
        }

        histogram.Clear();
        histogram[LocalBinarizer.Dark] = darkCount;
        histogram[LocalBinarizer.Light] = binarized.Length - darkCount;
        return attempt.Decode(binarized, histogram, width, height, destination, out charsWritten, out info);
    }

    /// <summary>Whether every pixel is 0 or 255; symmetric, so it holds for a histogram in either polarity.</summary>
    private static bool HoldsOnlyExtremes(ReadOnlySpan<int> histogram)
    {
        foreach (var count in histogram.Slice(1, Binarizer.HistogramBins - 2))
        {
            if (count != 0)
                return false;
        }
        return true;
    }
}
