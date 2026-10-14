using System.Buffers;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>One decode attempt of an image decoder on a luminance buffer and its histogram.</summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface ILuminanceAttempt<TInfo>
{
    /// <summary>
    /// Reads <paramref name="luminance"/>, a binarization of two levels. <paramref name="source"/> is the grey image it was binarized from, for a stage that reads grey levels where the binarization placed the symbol,
    /// and <paramref name="negative"/> says <paramref name="luminance"/> is the binarization of that image's negative, so that its dark pixels are the light ones there.
    /// </summary>
    DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> source, bool negative, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info);
}

/// <summary>
/// The retry after both global polarities fail: the <see cref="LocalBinarizer"/> output of the positive, then of the negative.
/// </summary>
/// <remarks>
/// Each polarity is binarized from its own pixels, since a flat block reads as paper. A polarity whose output keeps every pixel's global class is not decoded.
/// </remarks>
internal static class RegionalRetry
{
    /// <summary>Decodes the regional binarization of the positive, then of the negative unless the positive's read the symbol or gave a verdict on its content.</summary>
    /// <typeparam name="TAttempt">The decoder's attempt type.</typeparam>
    /// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
    /// <param name="attempt">The decoder's attempt. It is a struct, so the call is direct.</param>
    /// <param name="luminance">The image, positive.</param>
    /// <param name="buffer">The inverted retry's buffer, one byte a pixel. It is overwritten with each binarization.</param>
    /// <param name="histogram">The negative's histogram, as the inverted retry left it. It is overwritten.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where the attempt writes the decoded characters.</param>
    /// <param name="charsWritten">What the last polarity's decode wrote, or 0 when that polarity was not decoded.</param>
    /// <param name="info">The last polarity's decode diagnostics, or default when that polarity was not decoded.</param>
    /// <returns>The last polarity's status: the positive's when it read the symbol or gave a verdict on its content, else the negative's. It is <see cref="DecodeStatus.NotDetected"/> for a polarity not decoded.</returns>
    public static DecodeStatus Decode<TAttempt, TInfo>(ref TAttempt attempt, ReadOnlySpan<byte> luminance, Span<byte> buffer, Span<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TAttempt : struct, ILuminanceAttempt<TInfo>
    {
        charsWritten = 0;
        info = default!;

        // Only 0 and 255: every regional threshold is in [0, 251], so no pixel would change class
        var scratchLength = LocalBinarizer.ScratchLength(width, height);
        if (scratchLength == 0 || HoldsOnlyExtremes(histogram))
            return DecodeStatus.NotDetected;

        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        Binarizer.InvertHistogram(histogram);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);

        // The negative is complemented as it loads, so the inverted retry's buffer can take the output
        var binarized = buffer.Slice(0, width * height);
        var rentedScratch = ArrayPool<int>.Shared.Rent(scratchLength);
        try
        {
            // The negative is binarized from the block statistics the positive left in the scratch
            var measured = false;
            var status = DecodePolarity(ref attempt, luminance, negative: false, positiveThreshold, binarized, rentedScratch, ref measured, histogram, width, height, destination, out charsWritten, out info);
            // A verdict is final like a read: the negative of one symbol can only read another
            if (AttemptStatus.IsSettled(status))
                return status;
            return DecodePolarity(ref attempt, luminance, negative: true, negativeThreshold, binarized, rentedScratch, ref measured, histogram, width, height, destination, out charsWritten, out info);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedScratch);
        }
    }

    private static DecodeStatus DecodePolarity<TAttempt, TInfo>(ref TAttempt attempt, ReadOnlySpan<byte> luminance, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, ref bool measured, Span<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info)
        where TAttempt : struct, ILuminanceAttempt<TInfo>
    {
        if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, globalThreshold, binarized, scratch, ref measured, out var darkCount))
        {
            charsWritten = 0;
            info = default!;
            return DecodeStatus.NotDetected;
        }

        histogram.Clear();
        histogram[LocalBinarizer.Dark] = darkCount;
        histogram[LocalBinarizer.Light] = binarized.Length - darkCount;
        return attempt.Decode(binarized, luminance, negative, histogram, width, height, destination, out charsWritten, out info);
    }

    /// <summary>Whether every pixel is 0 or 255. This is symmetric, so it holds for a histogram in either polarity.</summary>
    internal static bool HoldsOnlyExtremes(ReadOnlySpan<int> histogram)
    {
        foreach (var count in histogram.Slice(1, Binarizer.HistogramBins - 2))
        {
            if (count != 0)
                return false;
        }
        return true;
    }
}
