namespace FeatherQR;

/// <summary>
/// A position in an image, in continuous pixel coordinates: (0, 0) is the top-left corner of the top-left pixel and (width, height) the bottom-right corner of the image, so the value plots directly in SkiaSharp, a canvas or System.Drawing.
/// </summary>
/// <remarks>
/// Produced by the image decoders as the corners of a located symbol (<see cref="SymbolCorners"/>). Module-space geometry, such as <see cref="ModuleRect"/>, is a different coordinate system: this one is pixels of the input image.
/// </remarks>
public readonly record struct ImagePoint
{
    internal ImagePoint(float x, float y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Horizontal position in pixels, increasing to the right.</summary>
    public float X { get; }

    /// <summary>Vertical position in pixels, increasing downward.</summary>
    public float Y { get; }
}
