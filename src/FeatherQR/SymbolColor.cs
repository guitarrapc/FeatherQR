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
/// An object initializer starts from that too, so <c>new SymbolColor { R = 255 }</c> is transparent red. Use the constructor, which is opaque unless given an alpha.
/// Opaque black is <c>new SymbolColor(0, 0, 0)</c>.
/// </remarks>
public readonly record struct SymbolColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>The four channels in one word, red in the top byte and alpha in the bottom one.</summary>
    internal uint Rgba => (uint)R << 24 | (uint)G << 16 | (uint)B << 8 | A;

    internal static SymbolColor FromRgba(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    /// <summary>This colour painted over <paramref name="below"/> with straight alpha (source over), each channel rounded to the nearest byte.</summary>
    internal SymbolColor Over(SymbolColor below)
    {
        // Weights in 1/255² of a pixel: this colour's alpha, and the share of the colour below that shows through it.
        int top = A * 255, under = below.A * (255 - A), sum = top + under;
        if (sum == 0)
            return this;
        return new SymbolColor(Blend(R, below.R, top, under), Blend(G, below.G, top, under), Blend(B, below.B, top, under), (byte)((sum + 127) / 255));
    }

    private static byte Blend(byte upper, byte lower, int top, int under) => (byte)((upper * top + lower * under + (top + under) / 2) / (top + under));
}
