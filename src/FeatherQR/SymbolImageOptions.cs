namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="SymbolImageWriter"/>.
/// <c>default</c> is 8 pixels a module, opaque black on opaque white, so set only what you need: <c>new SymbolImageOptions { ModulePixelSize = 4 }</c>,
/// or <c>new SymbolImageOptions { Width = 512, Height = 512 }</c> for an image of a set size.
/// </summary>
/// <remarks>
/// Mutable value type with C# 7.3-compatible setters. Changes to a copy do not affect the original.
/// The writer reads it by <c>in</c> during the call, so do not mutate the same variable concurrently.
/// </remarks>
public record struct SymbolImageOptions
{
    internal const int DefaultModulePixelSize = 8;
    private const uint OpaqueBlack = 0x000000FF;
    private const uint OpaqueWhite = 0xFFFFFFFF;

    // Each field holds its difference from the default, so default(T) reads 8 pixels, opaque black
    // and opaque white. A difference has one form per value, so the generated equality agrees with
    // the values a caller reads.
    internal const int DefaultModuleScalePercent = 100;
    internal const int DefaultCornerRadiusPercent = 30;
    internal const int DefaultIconBorderModules = 1;
    internal const int DefaultMaxCoreOccupancyPercent = 30;

    private int _modulePixelSizeOffset;
    private uint _darkColorDifference;
    private uint _lightColorDifference;
    private int _width;
    private int _height;
    private ModuleStyle _moduleStyle;
    private int _moduleScaleOffset;
    private FinderPatternStyle _finderPatternStyle;
    private int _cornerRadiusOffset;
    private int _iconSizeModules;
    private int _iconBorderOffset;
    private int _maxCoreOccupancyOffset;
    private ReadOnlyMemory<byte> _iconImage;
    private string? _iconMediaType;

    /// <summary>The default settings, identical to <c>default</c>.</summary>
    public static SymbolImageOptions Default => default;

    /// <summary>
    /// The side of one module in pixels. Defaults to 8.
    /// </summary>
    /// <remarks>
    /// Every module is a sharp square of this many pixels, so the image is the symbol's size in modules, quiet zone included, times this.
    /// The writer refuses a size that makes the image wider or taller than <see cref="int.MaxValue"/> pixels.
    /// It does not apply once <see cref="Width"/> or <see cref="Height"/> is set, where the module size follows from the canvas.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is below 1.</exception>
    public int ModulePixelSize
    {
        readonly get => DefaultModulePixelSize + _modulePixelSizeOffset;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(ModulePixelSize), $"Module pixel size must be at least 1, but was {value}");
            _modulePixelSizeOffset = value - DefaultModulePixelSize;
        }
    }

    /// <summary>
    /// The image's width in pixels. Defaults to 0, which is the symbol's own width at <see cref="ModulePixelSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With a width or a height set, the symbol fills the canvas: one scale, the smaller the two sides allow, the symbol with its quiet zone
    /// centred on whole pixels, and each pixel belonging to the module its centre falls in. Every module is still whole pixels, so the PNG stays
    /// one bit a pixel, and adjacent modules differ by at most one pixel. The rest of the canvas, where its aspect differs from the symbol's, is
    /// the light colour. This is the image the FeatherQR.SkiaSharp builders write for the same size with no module size pinned.
    /// </para>
    /// <para>
    /// A side left at 0 follows the symbol's aspect at the scale the other side gives, rounded up to whole pixels, so the image is the one
    /// both sides would give. <see cref="ModulePixelSize"/> does not apply once a size is set, where the builders pin it and pad the canvas instead.
    /// The writer refuses a canvas that gives a module under one pixel.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int Width
    {
        readonly get => _width;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(Width), $"Width must be 0 or more, but was {value}");
            _width = value;
        }
    }

    /// <summary>
    /// The image's height in pixels. Defaults to 0, which is the symbol's own height at <see cref="ModulePixelSize"/>.
    /// </summary>
    /// <remarks>
    /// Sized together with <see cref="Width"/>, which says how the symbol fills the canvas.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int Height
    {
        readonly get => _height;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(Height), $"Height must be 0 or more, but was {value}");
            _height = value;
        }
    }

    /// <summary>Whether a width or a height is set, so the symbol fills a canvas instead of taking <see cref="ModulePixelSize"/>.</summary>
    internal readonly bool HasSize => _width != 0 || _height != 0;

    /// <summary>
    /// The shape of each dark module. Defaults to <see cref="ModuleStyle.Square"/>.
    /// </summary>
    /// <remarks>
    /// A circle, a rounded square, a square below 100 percent or a <see cref="FinderPatternStyle"/> other than the square is drawn with soft edges,
    /// so the PNG and the pixel buffer carry levels between the two colours and the PNG leaves one bit a pixel.
    /// The finder patterns never take the module style, since a reader finds them by their shape.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not one of the styles.</exception>
    public ModuleStyle ModuleStyle
    {
        readonly get => _moduleStyle;
        set
        {
            if (value is < ModuleStyle.Square or > ModuleStyle.RoundedSquare)
                throw new ArgumentOutOfRangeException(nameof(ModuleStyle), $"Module style {(int)value} is not one of the three defined.");
            _moduleStyle = value;
        }
    }

    /// <summary>
    /// The side of each dark module's shape as a percentage of its cell, 50 to 100. Defaults to 100.
    /// </summary>
    /// <remarks>
    /// Below 100 the shape sits centred in its cell with the light colour around it. A module is read at its centre, so a gap costs
    /// nothing to a reader, but the finder patterns keep their full cells whatever this is. Half the cell is the floor, as the image
    /// builders' <c>WithModuleStyle</c> takes it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 50 to 100.</exception>
    public int ModuleScalePercent
    {
        readonly get => DefaultModuleScalePercent + _moduleScaleOffset;
        set
        {
            if (value is < 50 or > 100)
                throw new ArgumentOutOfRangeException(nameof(ModuleScalePercent), $"Module scale must be 50 to 100 percent, but was {value}");
            _moduleScaleOffset = value - DefaultModuleScalePercent;
        }
    }

    /// <summary>
    /// The shape of the finder patterns. Defaults to <see cref="FinderPatternStyle.Square"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not one of the styles.</exception>
    public FinderPatternStyle FinderPatternStyle
    {
        readonly get => _finderPatternStyle;
        set
        {
            if (value is < FinderPatternStyle.Square or > FinderPatternStyle.RoundedSquareCircle)
                throw new ArgumentOutOfRangeException(nameof(FinderPatternStyle), $"Finder pattern style {(int)value} is not one of the four defined.");
            _finderPatternStyle = value;
        }
    }

    /// <summary>
    /// The corner radius of a rounded square as a percentage of its side, 0 to 100. Defaults to 30.
    /// </summary>
    /// <remarks>
    /// It rounds <see cref="ModuleStyle.RoundedSquare"/> modules, and the outer square of the rounded finder pattern styles, by this share of their side.
    /// The inner squares of a finder pattern take fixed shares of the outer radius, as the image builders draw them. From 50 up a square is a circle, since its corners meet.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 0 to 100.</exception>
    public int CornerRadiusPercent
    {
        readonly get => DefaultCornerRadiusPercent + _cornerRadiusOffset;
        set
        {
            if (value is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(CornerRadiusPercent), $"Corner radius must be 0 to 100 percent, but was {value}");
            _cornerRadiusOffset = value - DefaultCornerRadiusPercent;
        }
    }

    /// <summary>Whether anything but squares at full size is asked for, which takes the output off its one-bit path.</summary>
    internal readonly bool IsStyled => _moduleStyle != ModuleStyle.Square || _moduleScaleOffset != 0 || _finderPatternStyle != FinderPatternStyle.Square;

    /// <summary>
    /// The side of a square of modules at the centre of a Standard QR code left in the light colour for an icon, with <see cref="IconBorderModules"/> around it.
    /// Defaults to 0, no icon area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The area is snapped to the module grid, as the FeatherQR.SkiaSharp builders snap an icon sized in modules, and <see cref="SymbolImageWriter.GetIconRectangle"/>
    /// says where the icon lands in pixels, for you to draw it there. The symbol relies on its error correction to stay readable, so use
    /// <see cref="QREccLevel.H"/> and keep the icon small: the writer refuses an icon that with its border takes more than
    /// <see cref="MaxCoreOccupancyPercent"/> of the core's side, the symbol without its quiet zone, or that reaches a finder pattern. Micro QR and rMQR take no icon.
    /// </para>
    /// <para>
    /// The SVG embeds <see cref="IconImage"/> over the area when one is given. The PNG and the pixel buffer leave the area to you.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int IconSizeModules
    {
        readonly get => _iconSizeModules;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(IconSizeModules), $"Icon size must be 0 or more modules, but was {value}");
            _iconSizeModules = value;
        }
    }

    /// <summary>The modules of light left around the icon. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int IconBorderModules
    {
        readonly get => DefaultIconBorderModules + _iconBorderOffset;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(IconBorderModules), $"Icon border must be 0 or more modules, but was {value}");
            _iconBorderOffset = value - DefaultIconBorderModules;
        }
    }

    /// <summary>The most of the core's side, the symbol without its quiet zone, that the icon and its border may take, 1 to 100 percent. Defaults to 30.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 1 to 100.</exception>
    public int MaxCoreOccupancyPercent
    {
        readonly get => DefaultMaxCoreOccupancyPercent + _maxCoreOccupancyOffset;
        set
        {
            if (value is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(MaxCoreOccupancyPercent), $"Max core occupancy must be 1 to 100 percent, but was {value}");
            _maxCoreOccupancyOffset = value - DefaultMaxCoreOccupancyPercent;
        }
    }

    /// <summary>
    /// An encoded image the SVG embeds over the icon area, stretched to fill it, as a <c>data:</c> URI. Empty by default.
    /// </summary>
    /// <remarks>
    /// The bytes go in as they are, the writer decodes nothing. <see cref="IconMediaType"/> names their type. It needs <see cref="IconSizeModules"/>, and the PNG and the pixel buffer leave it out.
    /// Two options are equal on this member when they hold the same memory, as a <see cref="ReadOnlyMemory{T}"/> compares, not when the bytes match.
    /// </remarks>
    public ReadOnlyMemory<byte> IconImage
    {
        readonly get => _iconImage;
        set => _iconImage = value;
    }

    /// <summary>The media type of <see cref="IconImage"/>, such as <c>image/svg+xml</c> or <c>image/jpeg</c>. <c>image/png</c> when left <c>null</c>.</summary>
    /// <remarks>It goes into the SVG's attribute as it is, so it is printable ASCII with no space, quote, angle bracket, ampersand or apostrophe.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value has a character the attribute cannot carry.</exception>
    public string? IconMediaType
    {
        readonly get => _iconMediaType;
        set
        {
            if (value is not null)
            {
                foreach (var c in value)
                {
                    if (c <= ' ' || c > '~' || c == '"' || c == '<' || c == '>' || c == '&' || c == '\'')
                        throw new ArgumentOutOfRangeException(nameof(IconMediaType), "An icon's media type is printable ASCII with no space, quote, angle bracket, ampersand or apostrophe.");
                }
            }
            _iconMediaType = value;
        }
    }

    /// <summary>
    /// The colour of the dark modules. Defaults to opaque black.
    /// </summary>
    /// <remarks>
    /// It is painted over the light colour in every format, so an alpha below 255 blends the two and a fully transparent colour draws no dark modules.
    /// </remarks>
    public SymbolColor DarkColor
    {
        readonly get => SymbolColor.FromRgba(_darkColorDifference ^ OpaqueBlack);
        set => _darkColorDifference = value.Rgba ^ OpaqueBlack;
    }

    /// <summary>
    /// The colour of the light modules and the quiet zone. Defaults to opaque white.
    /// </summary>
    /// <remarks>
    /// A fully transparent colour leaves the background out, so whatever is behind the image shows through, or white in a pixel layout with no alpha.
    /// </remarks>
    public SymbolColor LightColor
    {
        readonly get => SymbolColor.FromRgba(_lightColorDifference ^ OpaqueWhite);
        set => _lightColorDifference = value.Rgba ^ OpaqueWhite;
    }
}
