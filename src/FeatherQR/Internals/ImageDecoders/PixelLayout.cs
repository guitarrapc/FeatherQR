namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Channel order and depth of a pixel buffer handed to <see cref="LuminanceConverter"/>.
/// </summary>
/// <remarks>
/// These are the layouts QR sources actually use: 8-bit gray, the two 32-bit orders that SkiaSharp, ImageSharp, WPF and GDI+ produce, and 32-bit RGB with a padding byte.
/// Three-byte RGB and 16-bit formats are converted by the adapter first.
/// </remarks>
internal enum PixelLayout
{
    /// <summary>One byte per pixel, luminance as-is.</summary>
    Gray8,

    /// <summary>Four bytes per pixel: red, green, blue, alpha.</summary>
    Rgba8888,

    /// <summary>Four bytes per pixel: blue, green, red, alpha.</summary>
    Bgra8888,

    /// <summary>Four bytes per pixel: red, green, blue, and a padding byte that is ignored.</summary>
    Rgb888x,
}
