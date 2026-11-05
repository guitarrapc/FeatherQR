namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="SymbolImageWriter"/>.
/// <c>default</c> is 8 pixels a module, opaque black on opaque white, so set only what you need: <c>new SymbolImageOptions { ModulePixelSize = 4 }</c>.
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
    private int _modulePixelSizeOffset;
    private uint _darkColorDifference;
    private uint _lightColorDifference;

    /// <summary>The default settings, identical to <c>default</c>.</summary>
    public static SymbolImageOptions Default => default;

    /// <summary>
    /// The side of one module in pixels. Defaults to 8.
    /// </summary>
    /// <remarks>
    /// Every module is a sharp square of this many pixels, so the image is the symbol's size in modules, quiet zone included, times this.
    /// The writer refuses a size that makes the image wider or taller than <see cref="int.MaxValue"/> pixels.
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
    /// The colour of the dark modules. Defaults to opaque black.
    /// </summary>
    /// <remarks>
    /// A fully transparent colour draws no dark modules.
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
    /// A fully transparent colour leaves the background out, so whatever is behind the image shows through.
    /// </remarks>
    public SymbolColor LightColor
    {
        readonly get => SymbolColor.FromRgba(_lightColorDifference ^ OpaqueWhite);
        set => _lightColorDifference = value.Rgba ^ OpaqueWhite;
    }
}
