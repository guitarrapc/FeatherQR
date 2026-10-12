using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>The smallest dimension that carries version information: version 7.</summary>
    private const int VersionInformationFromDimension = 17 + 4 * 7;

    /// <summary>
    /// The dimension the two copies of the version information name, each read through the frame of the finder it stands beside, the centre and the axes of that finder's inner outline, or 0 when neither finder's outline is measured or neither copy is within 3 bits of a codeword.
    /// </summary>
    /// <remarks>
    /// A copy lies within seven modules of its finder's centre, where that finder's own frame holds whatever the symbol's dimension is.
    /// The dimension estimated from the finders does not: a lens draws a symbol's corners smaller than its middle, and the module size measured at the finders puts the count versions high.
    /// </remarks>
    internal static int ReadVersionAtFinders(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes moduleSizes)
    {
        var uLength = Distance(topLeft, topRight);
        var vLength = Distance(topLeft, bottomLeft);
        if (uLength < 1f || vLength < 1f)
            return 0;
        var uX = (topRight.X - topLeft.X) / uLength;
        var uY = (topRight.Y - topLeft.Y) / uLength;
        var vX = (bottomLeft.X - topLeft.X) / vLength;
        var vY = (bottomLeft.Y - topLeft.Y) / vLength;

        // Bit x·3 + y of a copy: six modules along the finder's side, three across, seven to five modules before its centre
        uint beside = 0;
        uint below = 0;
        if (TryMeasureOutline(image, topRight, moduleSizes.TopRight, uX, uY, vX, vY, out var outline))
        {
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    if (IsDarkAt(image, outline, y - 7f, x - 3f))
                        beside |= 1u << (x * 3 + y);
                }
            }
        }
        if (TryMeasureOutline(image, bottomLeft, moduleSizes.BottomLeft, uX, uY, vX, vY, out outline))
        {
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    if (IsDarkAt(image, outline, x - 3f, y - 7f))
                        below |= 1u << (x * 3 + y);
                }
            }
        }
        return NamedDimension(beside, below);

        static bool IsDarkAt(in ImageView image, in FinderOutline outline, float u, float v)
        {
            var x = outline.CenterX + u * outline.UX + v * outline.VX;
            var y = outline.CenterY + u * outline.UY + v * outline.VY;
            return image.Luminance[PixelIndex.Clamp(y, image.Height) * image.Width + PixelIndex.Clamp(x, image.Width)] < image.Threshold;
        }
    }

    /// <summary>The inner outline of a finder, from the directions of the finder lines at the module size measured on the finder itself.</summary>
    private static bool TryMeasureOutline(in ImageView image, in FinderPattern finder, float moduleSize, float uX, float uY, float vX, float vY, out FinderOutline outline)
    {
        outline = default;
        // NaN where the measurement left the image
        return moduleSize >= 1f
            && FinderOutline.TryMeasure(image.Luminance, image.Width, image.Height, image.EdgeLevel, finder.X, finder.Y, uX * moduleSize, uY * moduleSize, vX * moduleSize, vY * moduleSize, out outline);
    }
}
