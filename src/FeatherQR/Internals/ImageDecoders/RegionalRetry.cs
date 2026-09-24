using System.Buffers;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>One decode attempt of an image decoder on a luminance buffer and its histogram.</summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface ILuminanceAttempt<TInfo>
{
    DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out TInfo info);
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
    /// <param name="attempt">The decoder's attempt; a struct, so the call is direct.</param>
    /// <param name="luminance">The image, positive.</param>
    /// <param name="buffer">The inverted retry's buffer, one byte a pixel; overwritten with each binarization.</param>
    /// <param name="histogram">The negative's histogram, as the inverted retry left it; overwritten.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where the attempt writes the decoded characters.</param>
    /// <param name="charsWritten">What the last polarity's decode wrote; 0 when that polarity was not decoded.</param>
    /// <param name="info">The last polarity's decode diagnostics; default when that polarity was not decoded.</param>
    /// <returns>The last polarity's status: the positive's when it read the symbol or gave a verdict on its content, else the negative's; <see cref="DecodeStatus.NotDetected"/> for a polarity not decoded.</returns>
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
            var status = DecodePolarity(ref attempt, luminance, negative: false, positiveThreshold, binarized, rentedScratch, histogram, width, height, destination, out charsWritten, out info);
            // A verdict is final like a read: the negative of one symbol can only read another
            if (status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall || IsContentVerdict(status))
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

    /// <summary>
    /// Whether a failed decode read the symbol and failed on its content: a verdict the caller can act on. <see cref="DecodeStatus.DataUncorrectable"/> and <see cref="DecodeStatus.InvalidBitstream"/> are not, since noise reaches them.
    /// </summary>
    internal static bool IsContentVerdict(DecodeStatus status)
        => status is DecodeStatus.UnmappedCharacter or DecodeStatus.UnsupportedContent;

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
