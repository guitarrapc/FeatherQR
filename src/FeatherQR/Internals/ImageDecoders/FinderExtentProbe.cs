namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Whether the symbol a finder belongs to extends past a length along one of the finder's axes.
/// </summary>
/// <remarks>
/// <para>
/// Every symbology puts its finder at a corner of the symbol with a light separator a module wide beside it, so from the finder's outer edge the symbol runs along two of the four axis directions and nothing along the other two.
/// How far it runs tells the symbologies apart: a Micro QR symbol ends within 13 modules of the separator (17 modules a side at most), a Standard QR code runs 14 or more along both axes (21 a side at least), and an rMQR symbol 20 or more along its width (27 at least).
/// So a finder whose symbol extends 15 modules or more along some axis is not a Micro QR symbol's, and the read of every symbology spares Micro QR's search of that finder's orientations ([qrcode-symbologies.md](../../../../.github/docs/specs/qrcode-symbologies.md), every symbol of any symbology).
/// </para>
/// <para>
/// The walk along a direction starts 4.5 modules from the finder's centre, past the separator, in half-module steps, each step a band 7 modules across, the finder's width: the whole height of the lowest rMQR symbol, so a timing module alone or an alignment ring keeps a step dark where a quiet zone of 2 modules does not.
/// A step is dark when a quarter or more of its pixels are, and three light steps in a row are the gap the symbol ends at. Each step is sampled at least twice along and fourteen times across, since below 2 pixels a module a half-module step has no pixel of its own.
/// The walk gives no answer where it cannot tell: when it leaves the image inside the modules, when the axes are shorter than half a pixel a module, and when two steps in a row are nine tenths dark or more within the first 15 modules, which a symbol's modules never are and a shadowed background under the global threshold is, where the walk would otherwise run on past the quiet zone.
/// </para>
/// </remarks>
internal static class FinderExtentProbe
{
    /// <summary>Half the band's width across the walk, in modules: the finder's own half-width.</summary>
    private const float HalfBand = 3.5f;

    /// <summary>Where the walk starts, in modules from the finder's centre: its outer edge and the separator.</summary>
    private const float Start = 4.5f;

    /// <summary>Dark pixels a step needs, as a share, to count as dark.</summary>
    private const float DarkShare = 0.25f;

    /// <summary>Light steps in a row that end the symbol: a module and a half, less than the quiet zone and more than a module of light inside a symbol.</summary>
    private const int GapSteps = 3;

    /// <summary>The share two steps in a row reach only on a dark background, never on a symbol's modules.</summary>
    private const float ShadeShare = 0.9f;

    /// <summary>
    /// Whether the symbol at the finder centred on (<paramref name="centerX"/>, <paramref name="centerY"/>) extends <paramref name="modules"/> modules or more past the separator along one of the four directions of <paramref name="axes"/>.
    /// <see langword="false"/> where no direction does, and where the walk cannot tell.
    /// </summary>
    public static bool ExtendsBeyond(in ImageView image, float centerX, float centerY, in OrientationCandidate axes, int modules)
    {
        return Extends(image, centerX, centerY, axes.UX, axes.UY, axes.VX, axes.VY, modules)
            || Extends(image, centerX, centerY, -axes.UX, -axes.UY, axes.VX, axes.VY, modules)
            || Extends(image, centerX, centerY, axes.VX, axes.VY, axes.UX, axes.UY, modules)
            || Extends(image, centerX, centerY, -axes.VX, -axes.VY, axes.UX, axes.UY, modules);
    }

    /// <summary>
    /// One direction: (<paramref name="ux"/>, <paramref name="uy"/>) is a module along the walk in pixels, (<paramref name="vx"/>, <paramref name="vy"/>) a module across it.
    /// </summary>
    private static bool Extends(in ImageView image, float centerX, float centerY, float ux, float uy, float vx, float vy, int modules)
    {
        var luminance = image.Luminance;
        int width = image.Width, height = image.Height;
        var threshold = image.Threshold;
        var alongLength = (float)Math.Sqrt(ux * ux + uy * uy);
        var acrossLength = (float)Math.Sqrt(vx * vx + vy * vy);
        if (alongLength < 0.5f || acrossLength < 0.5f)
            return false;

        // A pixel apart along and across, two and fourteen samples a step at least
        var alongStep = Math.Min(1f / alongLength, 0.25f);
        var acrossStep = Math.Min(1f / acrossLength, 0.5f);
        // The symbol extends past the length when no gap has ended it by the step the length's last module ends on
        var lastStep = 2 * modules + 1;
        var shadeSteps = 2 * modules;
        var lightRun = 0;
        var previousShare = 0f;
        for (var step = 0; step <= lastStep; step++)
        {
            var from = Start + step * 0.5f;
            var to = from + 0.5f;
            int dark = 0, total = 0;
            for (var along = from + alongStep * 0.5f; along < to; along += alongStep)
            {
                for (var across = -HalfBand + acrossStep * 0.5f; across < HalfBand; across += acrossStep)
                {
                    var x = (int)Math.Floor(centerX + along * ux + across * vx);
                    var y = (int)Math.Floor(centerY + along * uy + across * vy);
                    // Off the image inside the modules the walk cannot tell, inside a light run the symbol has ended
                    if (x < 0 || y < 0 || x >= width || y >= height)
                        return false;
                    total++;
                    if (luminance[y * width + x] < threshold)
                        dark++;
                }
            }

            var share = dark / (float)total;
            if (share < DarkShare)
            {
                if (++lightRun == GapSteps)
                    return false;
            }
            else
            {
                lightRun = 0;
                if (step > 0 && step < shadeSteps && share >= ShadeShare && previousShare >= ShadeShare)
                    return false;
            }
            previousShare = share;
        }
        return true;
    }
}
