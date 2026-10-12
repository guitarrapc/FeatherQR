namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// One reading of an image, as a pass reads it: the pixels and their size, the threshold that splits them and the grey levels either side of it, carried through the stages of a decoder's search as one argument.
/// </summary>
/// <remarks>
/// The stages that measure (finder sizes, grids, samplers) take the pieces they read, so each says what it reads and a test can call it on its own. This is what the stages that only pass the image on hand down.
/// </remarks>
internal readonly ref struct ImageView
{
    /// <summary>Grayscale pixels, row-major, <see cref="Width"/> × <see cref="Height"/> bytes.</summary>
    public readonly ReadOnlySpan<byte> Luminance;

    public readonly int Width;

    public readonly int Height;

    /// <summary>A pixel below it is dark.</summary>
    public readonly byte Threshold;

    public readonly GreyLevels Grey;

    /// <summary>The luminance an edge is located at (<see cref="GreyLevels.EdgeLevel"/>): halfway between the image's two levels, on an image with no grey pixel too. It is the threshold only on an image of one level, or with no levels measured.</summary>
    public readonly float EdgeLevel;

    /// <summary>
    /// The grey image of this reading, the same size: <see cref="Luminance"/> itself when it holds grey levels, the image it was binarized from when it is a binarization, and empty when there is none.
    /// A stage that reads grey levels where this reading placed the symbol reads them here.
    /// </summary>
    public readonly ReadOnlySpan<byte> GreySource;

    /// <summary>Whether <see cref="Luminance"/> was binarized from the negative of <see cref="GreySource"/>: a dark pixel of this reading is a light one there.</summary>
    public readonly bool GreySourceNegative;

    public ImageView(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, GreyLevels grey)
        : this(luminance, default, false, width, height, threshold, grey)
    {
    }

    /// <summary>A reading with the grey image it was binarized from: <paramref name="source"/>, empty when <paramref name="luminance"/> is no binarization, and <paramref name="negative"/> when it was binarized from that image's negative.</summary>
    public ImageView(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> source, bool negative, int width, int height, byte threshold, GreyLevels grey)
    {
        Luminance = luminance;
        Width = width;
        Height = height;
        Threshold = threshold;
        Grey = grey;
        EdgeLevel = grey.EdgeLevel(threshold);
        if (!source.IsEmpty)
        {
            GreySource = source;
            GreySourceNegative = negative;
        }
        else if (grey.IsEnabled)
        {
            GreySource = luminance;
        }
    }
}
