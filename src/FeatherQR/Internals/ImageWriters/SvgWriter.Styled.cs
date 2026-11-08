namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The styled document: one stroked path with a subpath at each dark module that is not part of a finder pattern, the finder patterns as
/// circles or even-odd paths, and the stroked runs of the plain document for squares at full size and for the standard finder patterns.
/// </summary>
/// <remarks>
/// <para>
/// Everything dark sits in one group that carries the fill colour, so a finder's filled elements name no colour of their own.
/// The shape's numbers are in modules to three decimals, which a percent of a module is.
/// </para>
/// <para>
/// A circle or a square below full size is a segment of no length at the module's centre, which a round or a square cap draws the
/// stroke's width across. A rounded square is a smaller square, filled and stroked with round joins as wide as its corners' diameter.
/// Every subpath starts at the same place in its cell and ends where it started, so the next one is a move by whole modules.
/// A subpath costs a few bytes a module, where an element at each module costs several times that.
/// </para>
/// </remarks>
internal static partial class SvgWriter
{
    // The group, the module path's attributes and first move, and three finder patterns of an even-odd path and a circle each.
    private const int StyledMarkupBound = 2048;
    // The longest shape after a subpath's move: a rounded square's "h.999v.999h-.999z".
    private const int ModuleTailBound = 17;

    /// <summary>
    /// Opens the group that carries the colour. Squares at full size are the plain document's stroked runs, which the caller writes next.
    /// Shaped modules are one path with a subpath at each dark module outside the finder patterns and the icon area, after the finder
    /// patterns as stroked runs when they are square.
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

        // In thousandths of a module: the shape's side, its inset from the cell's edge, and its corner radius, which stops where the corners meet.
        long side = options.ModuleScalePercent * 10, inset = (100 - options.ModuleScalePercent) * 5;
        var radius = options.ModuleStyle switch
        {
            ModuleStyle.Circle => side / 2,
            ModuleStyle.RoundedSquare => Math.Min(side * options.CornerRadiusPercent / 100, side / 2),
            _ => 0,
        };
        var joined = radius != 0 && radius != side / 2;
        Span<byte> tail = stackalloc byte[ModuleTailBound];
        var t = new AsciiWriter(tail);
        if (joined)
        {
            var innerSide = (int)(side - 2 * radius);
            t.Byte((byte)'h');
            t.Fraction(innerSide);
            t.Byte((byte)'v');
            t.Fraction(innerSide);
            t.Raw("h-"u8);
            t.Fraction(innerSide);
            t.Byte((byte)'z');
        }
        else
        {
            t.Raw("h0"u8);
        }
        tail = tail.Slice(0, t.Position);
        var offset = joined ? inset + radius : 500;

        var bits = view.CoreBits;
        int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
        int lastRow = -1, lastColumn = 0;
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
                        if (lastRow < 0)
                        {
                            OpenModulePath(ref w, side, radius, joined, dark);
                            w.Thousandths((quietZone + c) * 1000L + offset);
                            w.Byte((byte)' ');
                            w.Thousandths((quietZone + row) * 1000L + offset);
                        }
                        else
                        {
                            w.Byte((byte)'m');
                            w.Int(c - lastColumn);
                            w.Byte((byte)' ');
                            w.Int(row - lastRow);
                        }
                        w.Raw(tail);
                        lastRow = row;
                        lastColumn = c;
                    }
                }
                column += length;
                isDark = !isDark;
            }
        }
        if (lastRow >= 0)
            w.Raw("\"/>"u8);
    }

    /// <summary>The module path's stroke up to its first move: a cap that draws a segment of no length, or round joins around a smaller square.</summary>
    private static void OpenModulePath(ref AsciiWriter w, long side, long radius, bool joined, SymbolColor dark)
    {
        w.Raw("<path stroke=\""u8);
        w.Color(dark);
        w.Byte((byte)'"');
        if (dark.A != 255)
        {
            // A rounded square's fill and stroke overlap inside it, so its alpha is the path's opacity, which composites the two once.
            w.Raw(joined ? " fill-opacity=\"1\" opacity=\""u8 : " stroke-opacity=\""u8);
            w.Opacity(dark.A);
            w.Byte((byte)'"');
        }
        w.Raw(" stroke-width=\""u8);
        w.Thousandths(joined ? 2 * radius : side);
        w.Raw(joined ? "\" stroke-linejoin=\"round\" d=\"M"u8 : radius == 0 ? "\" stroke-linecap=\"square\" d=\"M"u8 : "\" stroke-linecap=\"round\" d=\"M"u8);
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
    /// rounded squares as one even-odd path around a square or a disc, the inner squares rounded by shares of the outer radius as the image builders draw them.
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
                // The outer radius is the given share of 7 modules, 70 thousandths a percent, and the inner squares take 0.8 and 0.6 of it.
                w.Raw("<path fill-rule=\"evenodd\" d=\""u8);
                WriteRoundedSquare(ref w, left * 1000L, top * 1000L, 7000, 70 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 1000, top * 1000L + 1000, 5000, 56 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 2000, top * 1000L + 2000, 3000, 42 * radius);
                w.Raw("\"/>"u8);
                break;
            default:
                // Around a circle the ring's inner square takes 0.7 of the outer radius.
                w.Raw("<path fill-rule=\"evenodd\" d=\""u8);
                WriteRoundedSquare(ref w, left * 1000L, top * 1000L, 7000, 70 * radius);
                WriteRoundedSquare(ref w, left * 1000L + 1000, top * 1000L + 1000, 5000, 49 * radius);
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
