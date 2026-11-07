namespace FeatherQR;

/// <summary>
/// The channel order and depth of a raw pixel buffer: one byte a pixel for <see cref="Gray8"/>, four for the others.
/// </summary>
/// <remarks>
/// These are the layouts phone cameras, graphics libraries and GPU textures commonly hand over or take.
/// A buffer <see cref="SymbolImageWriter"/> writes has its rows one after another with no padding. A buffer <see cref="LuminanceConverter"/> reads may have a stride wider than its rows.
/// </remarks>
public enum PixelLayout
{
    /// <summary>One byte a pixel: luminance, 0 black to 255 white.</summary>
    Gray8 = 0,

    /// <summary>Four bytes a pixel: red, green, blue, alpha, alpha not premultiplied.</summary>
    Rgba8888 = 1,

    /// <summary>Four bytes a pixel: blue, green, red, alpha, alpha not premultiplied.</summary>
    Bgra8888 = 2,

    /// <summary>Four bytes a pixel: red, green, blue, and a fourth byte that carries no colour.</summary>
    Rgb888x = 3,
}
