using System.Buffers;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// A module matrix as text for a terminal: each character shows two modules, one above the other, with the half blocks
/// U+2580 (upper), U+2584 (lower) and U+2588 (both), or a space for neither.
/// </summary>
/// <remarks>
/// A cell of a monospace font is about twice as tall as it is wide, so a character's two halves are close to square.
/// The quiet zone is the symbol's own. A symbol's row count is odd with any quiet zone, so the last line's lower half lies past
/// the matrix and is light. Every line ends with <c>\n</c>.
/// </remarks>
internal static class HalfBlockText
{
    private const char Upper = '▀';
    private const char Lower = '▄';
    private const char Full = '█';

    public static string Get<TView>(in TView view, bool invert)
        where TView : struct, IModuleMatrixView
    {
        int width = view.Width, height = view.Height;
        var lines = (height + 1) / 2;
        var length = lines * (width + 1);
        var rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            var text = rented.AsSpan(0, length);
            var quietZone = (width - view.CoreWidth) / 2;
            var at = 0;
            for (var row = 0; row < height; row += 2)
            {
                for (var col = 0; col < width; col++)
                {
                    // A filled half is a dark module, or a light one when inverted.
                    var top = Dark(in view, row, col, quietZone) != invert;
                    var bottom = Dark(in view, row + 1, col, quietZone) != invert;
                    text[at++] = top ? (bottom ? Full : Upper) : (bottom ? Lower : ' ');
                }
                text[at++] = '\n';
            }
            return new string(rented, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Whether a module, counted from the outer edge of the quiet zone, is dark. Outside the core, including past the last row, it is light.</summary>
    private static bool Dark<TView>(in TView view, int row, int col, int quietZone)
        where TView : struct, IModuleMatrixView
    {
        var coreRow = row - quietZone;
        var coreCol = col - quietZone;
        return (uint)coreRow < (uint)view.CoreHeight && (uint)coreCol < (uint)view.CoreWidth && view.GetCoreModule(coreRow, coreCol);
    }
}
