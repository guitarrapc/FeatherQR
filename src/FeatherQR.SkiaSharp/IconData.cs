using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// The logo or image drawn at the center of a QR code, and its size.
/// </summary>
/// <remarks>
/// The icon covers modules, so the symbol relies on error correction to stay readable.
/// Use the highest error correction level (<see cref="QREccLevel.H"/>) and keep the icon
/// small. When the size is given in modules, rendering throws if the icon and its border
/// span more than <see cref="MaxCoreOccupancyPercent"/> percent of the core width; sizes
/// given as a percentage of the image are not checked against the symbol at all.
/// </remarks>
public class IconData
{
    /// <summary>
    /// The icon shape to overlay on the QR code.
    /// </summary>
    public required IconShape Icon { get; set; }

    /// <summary>
    /// The size of the icon as a percentage of the QR code size (1-100).
    /// Ignored when <see cref="IconSizeModules"/> is set.
    /// </summary>
    public int IconSizePercent { get; set; } = 10;

    /// <summary>
    /// The border width around the icon in pixels. Creates a background-colored padding around the icon.
    /// Ignored when <see cref="IconSizeModules"/> is set.
    /// </summary>
    public int IconBorderWidth { get; set; } = 2;

    /// <summary>
    /// The size of the icon body in QR modules.
    /// When set, module-based sizing is used and <see cref="IconSizePercent"/> / <see cref="IconBorderWidth"/> are ignored.
    /// </summary>
    public int? IconSizeModules { get; set; }

    /// <summary>
    /// The border width around the icon in QR modules.
    /// When <see cref="IconSizeModules"/> is set and this is null, defaults to 1.
    /// </summary>
    public int? IconBorderModules { get; set; }

    /// <summary>
    /// Maximum allowed icon occupancy of the QR core area, as a percentage (1-100).
    /// Used only for module-based sizing. Default is 30.
    /// </summary>
    public int MaxCoreOccupancyPercent { get; set; } = 30;

    /// <summary>
    /// Create IconData from a bitmap image using percent/pixel sizing.
    /// </summary>
    /// <param name="image">The bitmap image to display as the icon.</param>
    /// <param name="iconSizePercent">The size of the icon as a percentage of the QR code size (1-100). Default is 10.</param>
    /// <param name="iconBorderWidth">The border width around the icon in pixels. Default is 2.</param>
    /// <returns>A new <see cref="IconData"/> instance configured with the specified image, size, and border width.</returns>
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
    /// Create IconData from a bitmap image using module-based sizing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer combining this with <c>WithModulePixelSize</c> so each module maps to an integer pixel size.
    /// Optional <c>WithSize</c> can then set a larger canvas; content is centered and padded.
    /// </para>
    /// <para>
    /// Validation against QR size/core occupancy happens at render time.
    /// </para>
    /// </remarks>
    /// <param name="image">The bitmap image to display as the icon.</param>
    /// <param name="iconSizeModules">Icon body size in modules (must be &gt;= 1).</param>
    /// <param name="iconBorderModules">Border size in modules (must be &gt;= 0). Default is 1.</param>
    /// <param name="maxCoreOccupancyPercent">Maximum core occupancy percent (1-100). Default is 30.</param>
    /// <returns>A new <see cref="IconData"/> instance configured with module-based sizing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
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
