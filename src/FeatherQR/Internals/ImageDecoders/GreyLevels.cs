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

        long count = 0, sum = 0;
        for (var i = 0; i < threshold && count * 2 < darkWeight; i++)
        {
            count += histogram[i];
            sum += (long)i * histogram[i];
        }
        var dark = (float)sum / count;

        count = 0;
        sum = 0;
        for (var i = 255; i >= threshold && count * 2 < lightWeight; i--)
        {
            count += histogram[i];
            sum += (long)i * histogram[i];
        }
        var light = (float)sum / count;

        if (light - dark < MinimumRange)
            return default;

        // No pixel strictly between the levels: every share is 0 or 1
        var any = false;
        for (var i = (int)dark + 1; i < light; i++)
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
