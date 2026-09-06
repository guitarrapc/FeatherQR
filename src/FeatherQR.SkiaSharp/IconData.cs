using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// The logo or image drawn at the center of a QR code, and its size.
/// </summary>
/// <remarks>
/// The icon covers modules, so the QR code relies on error correction to stay readable.
/// Use the highest error correction level (<see cref="QREccLevel.H"/>) and keep the icon small.
/// When the size is given in modules, rendering throws if the icon and its border span more than <see cref="MaxCoreOccupancyPercent"/> percent of the core width; sizes given as a percentage of the image are not checked against the QR code at all.
/// </remarks>
public sealed record class IconData
{
    /// <summary>
    /// What to draw at the center.
    /// </summary>
    public required IconShape Icon { get; init; }

    /// <summary>
    /// Icon size as a percentage of the image, 1 to 100. Ignored once <see cref="IconSizeModules"/> is set.
    /// </summary>
    public int IconSizePercent { get; init; } = 10;

    /// <summary>
    /// Width of the background-colored padding around the icon, in pixels.
    /// Ignored once <see cref="IconSizeModules"/> is set.
    /// </summary>
    public int IconBorderWidth { get; init; } = 2;

    /// <summary>
    /// Icon size in modules, which keeps the icon aligned to the grid.
    /// Setting it switches sizing to modules, and <see cref="IconSizePercent"/> and <see cref="IconBorderWidth"/> stop applying.
    /// </summary>
    public int? IconSizeModules { get; init; }

    /// <summary>
    /// Width of the padding around the icon, in modules.
    /// 1 when omitted.
    /// </summary>
    public int? IconBorderModules { get; init; }

    /// <summary>
    /// How much of the QR code the icon and its border may cover, as a percentage.
    /// 30 by default.
    /// Only checked when the size is given in modules; rendering throws when the icon exceeds it.
    /// </summary>
    public int MaxCoreOccupancyPercent { get; init; } = 30;

    /// <summary>
    /// Creates an icon sized as a percentage of the image.
    /// </summary>
    /// <param name="image">The image to draw. The caller keeps ownership of it.</param>
    /// <param name="iconSizePercent">Icon size as a percentage of the image, 1 to 100.</param>
    /// <param name="iconBorderWidth">Width of the padding around the icon, in pixels.</param>
    public static IconData FromImage(SKBitmap image, int iconSizePercent = 10, int iconBorderWidth = 2)
    {
        return new IconData
        {
            Icon = new ImageIconShape(image),
            IconSizePercent = iconSizePercent,
            IconBorderWidth = iconBorderWidth
        };
    }

    /// <summary>
    /// Creates an icon sized in modules, so it lines up with the QR code grid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pair it with <c>WithModulePixelSize</c> so every module lands on a whole number of pixels; <c>WithSize</c> can then give a larger canvas, with the QR code centered and padded.
    /// </para>
    /// <para>
    /// The size is checked against the QR code when it is drawn, not here.
    /// </para>
    /// </remarks>
    /// <param name="image">The image to draw. The caller keeps ownership of it.</param>
    /// <param name="iconSizeModules">Icon size in modules, at least 1.</param>
    /// <param name="iconBorderModules">Width of the padding around the icon, in modules.</param>
    /// <param name="maxCoreOccupancyPercent">How much of the QR code the icon may cover, as a percentage.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is out of range.</exception>
    public static IconData FromImageByModules(
        SKBitmap image,
        int iconSizeModules,
        int iconBorderModules = 1,
        int maxCoreOccupancyPercent = 30)
    {
        if (iconSizeModules < 1)
            throw new ArgumentOutOfRangeException(nameof(iconSizeModules), "Icon size modules must be at least 1.");
        if (iconBorderModules < 0)
            throw new ArgumentOutOfRangeException(nameof(iconBorderModules), "Icon border modules must be 0 or greater.");
        if (maxCoreOccupancyPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCoreOccupancyPercent), "Max core occupancy percent must be between 1 and 100.");

        return new IconData
        {
            Icon = new ImageIconShape(image),
            IconSizeModules = iconSizeModules,
            IconBorderModules = iconBorderModules,
            MaxCoreOccupancyPercent = maxCoreOccupancyPercent,
        };
    }
}
