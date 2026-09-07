/// <summary>
/// Payloads shared by the Micro QR encode and decode comparisons, so both directions measure the same three symbols.
/// </summary>
internal static class MicroQRPayloads
{
    public const string Numeric = "0123456789";          // M2-L, numeric capacity boundary
    public const string Alphanumeric = "HELLO WORLD 14"; // M3-L, alphanumeric capacity boundary
    public const string Byte = "bytes m4 mode";          // M4-M, byte capacity boundary
}

/// <summary>
/// Payloads shared by the rMQR encode and decode comparisons, so both directions measure the same three symbols.
/// </summary>
internal static class RmQRPayloads
{
    public const string Numeric = "012345678901";                                     // R7x43-M numeric boundary
    public const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-."; // 43 chars, R11x59-M alphanumeric boundary

    /// <summary>150 bytes, the R17x139-M byte boundary.</summary>
    public static readonly string Byte =
        string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150);
}

/// <summary>
/// Converts a module matrix produced by this library into the form CodeGlyphX decodes, so the comparison rows start from identical modules.
/// </summary>
internal static class GlyphBitMatrix
{
    public static CodeGlyphX.BitMatrix From(byte[] modules, int width, int height)
    {
        var matrix = new CodeGlyphX.BitMatrix(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                matrix.Set(x, y, modules[y * width + x] != 0);
            }
        }
        return matrix;
    }
}
