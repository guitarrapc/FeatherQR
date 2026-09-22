namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The luminance of a wholly dark and a wholly light pixel, for reading a pixel between them as the share of it a dark module covers.
/// </summary>
/// <remarks>
/// An anti-aliased or resampled edge leaves a grey pixel whose level is that share, which a threshold rounds to a whole pixel: at 2 px/module that is half a module.
/// <c>default</c> is disabled, and so is an image with no pixel between the two levels, where the share is 0 or 1 everywhere and says nothing the threshold did not.
/// </remarks>
internal readonly struct GreyLevels
{
    /// <summary>A pixel at or above this is wholly light.</summary>
    private readonly float _light;

    /// <summary>1 / (light − dark), 0 when disabled.</summary>
    private readonly float _scale;

    /// <summary>The two classes have to be apart for a level between them to mean coverage.</summary>
    private const int MinimumRange = 32;

    private GreyLevels(float light, float scale)
    {
        _light = light;
        _scale = scale;
    }

    /// <summary>False for <c>default</c> and for an image the levels say nothing about, where every caller must read whole pixels.</summary>
    public bool IsEnabled => _scale > 0f;

    /// <summary>The share of the pixel that is dark, 0 to 1.</summary>
    public float Darkness(byte luminance)
    {
        var darkness = (_light - luminance) * _scale;
        return darkness < 0f ? 0f : darkness > 1f ? 1f : darkness;
    }

    /// <summary>
    /// Levels from a luminance histogram split at <paramref name="threshold"/>: the mean of each class's outer half.
    /// The inner halves hold the edge pixels being measured, and would pull the levels toward each other.
    /// </summary>
    /// <param name="histogram">Counts per luminance, 256 bins.</param>
    /// <param name="threshold">The split the caller binarizes on, dark below it, as <see cref="Binarizer.ComputeOtsuThreshold(ReadOnlySpan{byte}, out GreyLevels)"/> returns it.</param>
    public static GreyLevels FromHistogram(ReadOnlySpan<int> histogram, int threshold)
    {
        long darkWeight = 0;
        for (var i = 0; i < threshold; i++)
            darkWeight += histogram[i];
        long lightWeight = 0;
        for (var i = threshold; i < 256; i++)
            lightWeight += histogram[i];
        if (darkWeight == 0 || lightWeight == 0)
            return default;

        long darkCount = 0, darkSum = 0;
        for (var i = 0; i < threshold && darkCount * 2 < darkWeight; i++)
        {
            darkCount += histogram[i];
            darkSum += (long)i * histogram[i];
        }
        var dark = (float)darkSum / darkCount;

        long lightCount = 0, lightSum = 0;
        for (var i = 255; i >= threshold && lightCount * 2 < lightWeight; i--)
        {
            lightCount += histogram[i];
            lightSum += (long)i * histogram[i];
        }
        var light = (float)lightSum / lightCount;

        if (light - dark < MinimumRange)
            return default;

        // No pixel strictly between the levels: every share is 0 or 1. The bounds come from the integer sums, not the
        // float means: past 2^24 in a weighted sum the float mean of a class at 255 rounds above 255, and a scan bounded
        // by it would count bin 255 itself. The smallest bin above the dark mean is floor(mean) + 1, the largest below
        // the light mean is ceil(mean) - 1, both exact in integers
        var firstAbove = (int)(darkSum / darkCount) + 1;
        var lastBelow = (int)((lightSum - 1) / lightCount);
        var any = false;
        for (var i = firstAbove; i <= lastBelow; i++)
        {
            if (histogram[i] != 0)
            {
                any = true;
                break;
            }
        }
        return any ? new GreyLevels(light, 1f / (light - dark)) : default;
    }
}
