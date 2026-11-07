namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The styled document: one shape in <c>defs</c>, used at each dark module that is not part of a finder pattern, the finder patterns as
/// circles or even-odd paths, and the stroked runs of the plain document for squares at full size and for the standard finder patterns.
/// </summary>
/// <remarks>
/// Everything dark sits in one group that carries the colour, so a use element and a finder's elements name no colour of their own.
/// The shape's numbers are in modules to three decimals, which a percent of a module is.
/// </remarks>
internal static partial class SvgWriter
{
    // The group, the shape, and three finder patterns of an even-odd path and a circle each.
    private const int StyledMarkupBound = 2048;
    // "<use href="#m" x="" y=""/>" around the two numbers.
    private const int StyledUseBound = 26;

    /// <summary>
    /// Opens the group that carries the colour. Squares at full size are the plain document's stroked runs, which the caller writes next.
    /// Shaped modules are the shape defined once and placed at each dark module outside the finder patterns and the icon area, after the
    /// finder patterns as stroked runs when they are square.
    /// </summary>
    private static void OpenStyled<TView>(ref AsciiWriter w, in TView view, in SymbolImageOptions options, in IconArea icon, int quietZone, bool squareModules)
        where TView : struct, IModuleMatrixView
    {
        var dark = options.DarkColor;
        w.Raw("<g fill=\""u8);
        w.Color(dark);
        w.Byte((byte)'"');
        if (dark.A != 255)
        {
            w.Raw(" fill-opacity=\""u8);
            w.Opacity(dark.A);
            w.Byte((byte)'"');
        }
        w.Byte((byte)'>');
        if (squareModules)
            return;

        if (options.FinderPatternStyle == FinderPatternStyle.Square)
            WriteFinderRuns(ref w, in view, quietZone, dark);
        WriteModuleShape(ref w, in options);
        var bits = view.CoreBits;
        int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
        for (var row = 0; row < coreHeight; row++)
        {
            var start = (long)row * coreWidth;
            var column = 0;
            var isDark = false;
            while (column < coreWidth)
            {
                var length = PackedRuns.Length(bits, start + column, coreWidth - column, isDark);
                if (isDark)
                {
                    for (var c = column; c < column + length; c++)
                    {
                        if (view.IsFinderPattern(row, c) || icon.Covers(row, c))
                            continue;
                        w.Raw("<use href=\"#m\" x=\""u8);
                        w.Int(quietZone + c);
                        w.Raw("\" y=\""u8);
                        w.Int(quietZone + row);
                        w.Raw("\"/>"u8);
                    }
                }
                column += length;
                isDark = !isDark;
            }
        }
    }

    /// <summary>The finder patterns as shapes of their own when they have one, and the group's end.</summary>
    private static void CloseStyled<TView>(ref AsciiWriter w, in TView view, in SymbolImageOptions options, int quietZone, SymbolColor dark)
        where TView : struct, IModuleMatrixView
    {
        if (options.FinderPatternStyle != FinderPatternStyle.Square)
        {
            for (var i = 0; i < view.FinderPatternCount; i++)
            {
                view.GetFinderPatternOrigin(i, out var row, out var column);
                WriteFinder(ref w, quietZone + column, quietZone + row, in options, dark);
            }
        }
        w.Raw("</g>"u8);
    }

    /// <summary>The module's shape, defined once at the origin of a cell: a rectangle inset by the scale, rounded when asked, or a circle.</summary>
    private static void WriteModuleShape(ref AsciiWriter w, in SymbolImageOptions options)
    {
        // In thousandths of a module: the inset on each side and the side left.
        long inset = (100 - options.ModuleScalePercent) * 5, side = options.ModuleScalePercent * 10;
        w.Raw("<defs>"u8);
        if (options.ModuleStyle == ModuleStyle.Circle)
        {
            w.Raw("<circle id=\"m\" cx=\".5\" cy=\".5\" r=\""u8);
            w.Thousandths(side / 2);
            w.Raw("\"/>"u8);
        }
        else
        {
            w.Raw("<rect id=\"m\" x=\""u8);
            w.Thousandths(inset);
            w.Raw("\" y=\""u8);
            w.Thousandths(inset);
            w.Raw("\" width=\""u8);
            w.Thousandths(side);
            w.Raw("\" height=\""u8);
            w.Thousandths(side);
            if (options.ModuleStyle == ModuleStyle.RoundedSquare && options.CornerRadiusPercent != 0)
            {
                // Past half the side the corners meet, so the radius stops there as the raster's does.
                w.Raw("\" rx=\""u8);
                w.Thousandths(Math.Min(side * options.CornerRadiusPercent / 100, side / 2));
            }
            w.Raw("\"/>"u8);
        }
        w.Raw("</defs>"u8);
    }

    /// <summary>
    /// The standard finder patterns' dark modules as stroked runs in the plain document's form, walked box by box, which is the few
    /// modules they are. Beside shaped modules they are the only runs.
    /// </summary>
    private static void WriteFinderRuns<TView>(ref AsciiWriter w, in TView view, int quietZone, SymbolColor dark)
        where TView : struct, IModuleMatrixView
    {
        var first = true;
        int x = 0, y = 0;
        for (var i = 0; i < view.FinderPatternCount; i++)
        {
            view.GetFinderPatternOrigin(i, out var finderRow, out var finderColumn);
            for (var row = finderRow; row < finderRow + 7; row++)
            {
                var column = finderColumn;
                while (column < finderColumn + 7)
                {
                    if (!view.GetCoreModule(row, column))
                    {
                        column++;
                        continue;
                    }
                    var runStart = column;
                    while (column < finderColumn + 7 && view.GetCoreModule(row, column))
                        column++;
                    var start = runStart + quietZone;
                    var rowAt = row + quietZone;
                    if (first)
                    {
                        w.Raw("<path stroke=\""u8);
                        w.Color(dark);
                        w.Byte((byte)'"');
                        if (dark.A != 255)
                        {
                            w.Raw(" stroke-opacity=\""u8);
                            w.Opacity(dark.A);
                            w.Byte((byte)'"');
                        }
                        w.Raw(" d=\"M"u8);
                        w.Int(start);
                        w.Byte((byte)' ');
                        w.Int(rowAt);
                        w.Raw(".5h"u8);
                        first = false;
                    }
                    else
                    {
                        w.Byte((byte)'m');
                        w.Int(start - x);
                        w.Byte((byte)' ');
                        w.Int(rowAt - y);
                        w.Byte((byte)'h');
                    }
                    w.Int(column - runStart);
                    x = start + column - runStart;
                    y = rowAt;
                }
            }
        }
        if (!first)
            w.Raw("\"/>"u8);
    }

    /// <summary>
    /// A finder pattern at <paramref name="left"/>, <paramref name="top"/> in modules: nested circles as a stroked ring and a disc, or nested
    /// rounded squares as one even-odd path, each square rounded by the corner radius of its own side, around a square or a disc.
    /// </summary>
    private static void WriteFinder(ref AsciiWriter w, int left, int top, in SymbolImageOptions options, SymbolColor dark)
    {
        long centreX = left * 1000L + 3500, centreY = top * 1000L + 3500;
        var radius = options.CornerRadiusPercent;
        switch (options.FinderPatternStyle)
        {
            case FinderPatternStyle.Circle:
                // The ring between the 5 and 7 module circles is a stroke one module wide along the 6 module one.
                w.Raw("<circle cx=\""u8);
                w.Thousandths(centreX);
                w.Raw("\" cy=\""u8);
                w.Thousandths(centreY);
                w.Raw("\" r=\"3\" fill=\"none\" stroke=\""u8);
                w.Color(dark);
                w.Byte((byte)'"');
                if (dark.A != 255)
                {
                    w.Raw(" stroke-opacity=\""u8);
                    w.Opacity(dark.A);
                    w.Byte((byte)'"');
                }
                w.Raw("/>"u8);
                WriteCentreCircle(ref w, centreX, centreY);
                break;
            case FinderPatternStyle.RoundedSquare:
                w.Raw("<path fill-rule=\"evenodd\" d=\""u8);
                WriteRoundedSquare(ref w, left * 1000L, top * 1000L, 7000, 70 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 1000, top * 1000L + 1000, 5000, 50 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 2000, top * 1000L + 2000, 3000, 30 * radius);
                w.Raw("\"/>"u8);
                break;
            default:
                w.Raw("<path fill-rule=\"evenodd\" d=\""u8);
                WriteRoundedSquare(ref w, left * 1000L, top * 1000L, 7000, 70 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 1000, top * 1000L + 1000, 5000, 50 * radius);
                w.Raw("\"/>"u8);
                WriteCentreCircle(ref w, centreX, centreY);
                break;
        }
    }

    private static void WriteCentreCircle(ref AsciiWriter w, long centreX, long centreY)
    {
        w.Raw("<circle cx=\""u8);
        w.Thousandths(centreX);
        w.Raw("\" cy=\""u8);
        w.Thousandths(centreY);
        w.Raw("\" r=\"1.5\"/>"u8);
    }

    /// <summary>One closed contour of a square with rounded corners, in thousandths of a module, as an absolute move and relative lines and arcs.</summary>
    private static void WriteRoundedSquare(ref AsciiWriter w, long x, long y, long side, long radius)
    {
        radius = Math.Min(radius, side / 2);
        var straight = side - 2 * radius;
        w.Byte((byte)'M');
        w.Thousandths(x + radius);
        w.Byte((byte)' ');
        w.Thousandths(y);
        if (straight > 0)
        {
            w.Byte((byte)'h');
            w.Thousandths(straight);
        }
        Arc(ref w, radius, radius, radius);
        if (straight > 0)
        {
            w.Byte((byte)'v');
            w.Thousandths(straight);
        }
        Arc(ref w, radius, -radius, radius);
        if (straight > 0)
        {
            w.Byte((byte)'h');
            w.Thousandths(-straight);
        }
        Arc(ref w, radius, -radius, -radius);
        if (straight > 0)
        {
            w.Byte((byte)'v');
            w.Thousandths(-straight);
        }
        Arc(ref w, radius, radius, -radius);
        w.Byte((byte)'z');
    }

    // A quarter circle of the radius to the relative end point, clockwise, written only when there is a corner to round.
    private static void Arc(ref AsciiWriter w, long radius, long dx, long dy)
    {
        if (radius == 0)
            return;
        w.Byte((byte)'a');
        w.Thousandths(radius);
        w.Byte((byte)' ');
        w.Thousandths(radius);
        w.Raw(" 0 0 1 "u8);
        w.Thousandths(dx);
        w.Byte((byte)' ');
        w.Thousandths(dy);
    }
}
