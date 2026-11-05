namespace FeatherQR;

/// <summary>
/// A colour for <see cref="SymbolImageWriter"/>: red, green, blue and alpha, each 0 to 255.
/// </summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
/// <param name="A">Alpha, from 0 (transparent) to 255 (opaque). Opaque unless given.</param>
/// <remarks>
/// <c>default</c> and <c>new SymbolColor()</c> are transparent black, since every channel is 0.
/// Opaque black is <c>new SymbolColor(0, 0, 0)</c>.
/// </remarks>
public readonly record struct SymbolColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>The four channels in one word, red in the top byte and alpha in the bottom one.</summary>
    internal uint Rgba => (uint)R << 24 | (uint)G << 16 | (uint)B << 8 | A;

    internal static SymbolColor FromRgba(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
}
