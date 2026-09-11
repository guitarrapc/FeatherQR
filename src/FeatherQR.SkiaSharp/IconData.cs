using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// The logo or image drawn at the center of a QR code, and its size.
/// </summary>
/// <remarks>
/// The icon covers modules, so the QR code relies on error correction to stay readable.
/// Use the highest error correction level (<see cref="QREccLevel.H"/>) and keep the icon small.
/// When the size is given in modules, rendering throws if the icon and its border span more than <see cref="MaxCoreOccupancyPercent"/> percent of the core width; sizes given as a percentage of the symbol are not checked against the QR code at all.
/// <para>
/// Equality compares the sizing members by value and <see cref="Icon"/> by reference, so two instances built from the same <see cref="SKBitmap"/> through separate <see cref="FromImage(SKBitmap, int, int)"/> calls are not equal.
/// <see cref="IconShape"/> is an open extension point wrapping an <see cref="SKBitmap"/>, neither of which defines value equality, so there is nothing to compare element-wise the way <see cref="GradientOptions"/> compares its colours.
/// </para>
/// </remarks>
public sealed record class IconData
{
    /// <summary>
    /// Creates an icon whose members are set through an object initializer.
    /// </summary>
    /// <remarks>
    /// Declared rather than left implicit, because declaring the constructor below would otherwise remove it and break every <c>new IconData { … }</c> call site.
    /// </remarks>
    public IconData()
    {
    }

    /// <summary>
    /// Creates an icon without an object initializer, for consumers whose language version predates C# 9.
    /// </summary>
    /// <remarks>
    /// Prefer the object initializer (<c>new IconData { Icon = shape, IconSizePercent = 15 }</c>) or one of the <c>FromImage</c> factories: they name only what they set and do not depend on this parameter order.
    /// This exists because <c>init</c> accessors are unassignable before C# 9, and .NET Framework and netstandard2.0 projects default to C# 7.3 while netstandard2.1 defaults to C# 8.0. Without it the factories are the only route, and both hardcode <see cref="ImageIconShape"/>, so <see cref="ImageTextIconShape"/> and any caller-written <see cref="IconShape"/> would be unreachable there.
    /// Pass arguments by name: <paramref name="iconSizePercent"/>, <paramref name="iconBorderWidth"/> and <paramref name="maxCoreOccupancyPercent"/> are all integers, so a positional call can transpose them and still compile.
    /// Each default is the value the object initializer leaves the property at, so omitting one changes nothing.
    /// </remarks>
    /// <param name="icon">See <see cref="Icon"/>. Required, so it has no default.</param>
    /// <param name="iconSizePercent">See <see cref="IconSizePercent"/>.</param>
    /// <param name="iconBorderWidth">See <see cref="IconBorderWidth"/>.</param>
    /// <param name="iconSizeModules">See <see cref="IconSizeModules"/>.</param>
    /// <param name="iconBorderModules">See <see cref="IconBorderModules"/>.</param>
    /// <param name="maxCoreOccupancyPercent">See <see cref="MaxCoreOccupancyPercent"/>.</param>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public IconData(
        IconShape icon,
        int iconSizePercent = 10,
        int iconBorderWidth = 2,
        int? iconSizeModules = null,
        int? iconBorderModules = null,
        int maxCoreOccupancyPercent = 30)
    {
        // Guarded here and not on the property, because this constructor is reached from
        // language versions with no nullable analysis, where passing null compiles silently
        // and then renders a symbol with no icon at all.
        Icon = icon ?? throw new ArgumentNullException(nameof(icon));
        IconSizePercent = iconSizePercent;
        IconBorderWidth = iconBorderWidth;
        IconSizeModules = iconSizeModules;
        IconBorderModules = iconBorderModules;
        MaxCoreOccupancyPercent = maxCoreOccupancyPercent;
    }

    /// <summary>
    /// What to draw at the center.
    /// </summary>
    public required IconShape Icon { get; init; }

    /// <summary>
    /// Icon size as a percentage of the symbol's side, quiet zone included, 1 to 100. Ignored once <see cref="IconSizeModules"/> is set.
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
    /// Creates an icon sized as a percentage of the symbol's side.
    /// </summary>
    /// <param name="image">The image to draw. The caller keeps ownership of it.</param>
    /// <param name="iconSizePercent">Icon size as a percentage of the symbol's side, quiet zone included, 1 to 100.</param>
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
