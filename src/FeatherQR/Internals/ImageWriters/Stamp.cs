namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The share of each pixel that a shape covers, as a level from 0, none of it, to the last level, all of it: a module's shape in its cell,
/// or a finder pattern's in its seven-by-seven box.
/// </summary>
/// <remarks>
/// <para>
/// Each pixel is sampled on a grid of points and the points inside the shape are counted. The arithmetic is in whole numbers of a quarter
/// of a sample, where a sample's centre, a pixel's edge and a shape's inset, side and radius all land, so every target writes the same
/// levels. Four points a side give seventeen counts, enough for the sixteen levels.
/// </para>
/// <para>
/// A shape is an outer region, a hole in it and a centre inside the hole, each a rounded rectangle or an ellipse. A module is the outer
/// region alone. A finder pattern's ring is the outer region less the hole, and its centre is drawn again inside. A stamp is the whole
/// shape kept, and a row is sampled on its own where a shape is too large to keep.
/// </para>
/// </remarks>
internal static class Stamp
{
    public const int SamplesPerSide = 4;

    /// <summary>The module shape's coverage of a cell of <paramref name="width"/> by <paramref name="height"/> pixels, row after row.</summary>
    public static void Module(Span<byte> levels, int width, int height, in SymbolImageOptions options, int levelCount)
    {
        var s = ModuleRegion(width, height, in options, out var outer);
        Fill(levels, width, 0, height, s, levelCount, in outer, default, default);
    }

    /// <summary>One pixel row, <paramref name="y"/>, of the module shape's coverage of a cell.</summary>
    public static void ModuleRow(Span<byte> levels, int width, int height, int y, in SymbolImageOptions options, int levelCount)
    {
        var s = ModuleRegion(width, height, in options, out var outer);
        Fill(levels, width, y, y + 1, s, levelCount, in outer, default, default);
    }

    /// <summary>A finder pattern's coverage of its box of <paramref name="width"/> by <paramref name="height"/> pixels, row after row.</summary>
    public static void Finder(Span<byte> levels, int width, int height, in SymbolImageOptions options, int levelCount)
    {
        var s = FinderRegions(width, height, in options, out var outer, out var hole, out var centre);
        Fill(levels, width, 0, height, s, levelCount, in outer, in hole, in centre);
    }

    /// <summary>One pixel row, <paramref name="y"/>, of a finder pattern's coverage of its box.</summary>
    public static void FinderRow(Span<byte> levels, int width, int height, int y, in SymbolImageOptions options, int levelCount)
    {
        var s = FinderRegions(width, height, in options, out var outer, out var hole, out var centre);
        Fill(levels, width, y, y + 1, s, levelCount, in outer, in hole, in centre);
    }

    // The module's shape inset by the scale: a square, a rounded square or the inscribed ellipse.
    private static int ModuleRegion(int width, int height, in SymbolImageOptions options, out Region outer)
    {
        const int s = SamplesPerSide;
        long w = 4L * s * width, h = 4L * s * height;
        var scale = options.ModuleScalePercent;
        var insetX = w * (100 - scale) / 200;
        var insetY = h * (100 - scale) / 200;
        var side = Math.Min(w - 2 * insetX, h - 2 * insetY);
        outer = options.ModuleStyle switch
        {
            ModuleStyle.Circle => Region.Ellipse(insetX, insetY, w - insetX, h - insetY),
            ModuleStyle.RoundedSquare => Region.RoundedRect(insetX, insetY, w - insetX, h - insetY, side * options.CornerRadiusPercent / 100),
            _ => Region.RoundedRect(insetX, insetY, w - insetX, h - insetY, 0),
        };
        return s;
    }

    // The three regions of a finder pattern in its box: nested ellipses, nested rounded squares, or rounded squares around an ellipse.
    private static int FinderRegions(int width, int height, in SymbolImageOptions options, out Region outer, out Region hole, out Region centre)
    {
        const int s = SamplesPerSide;
        long w = 4L * s * width, h = 4L * s * height;
        long moduleWidth = w / 7, moduleHeight = h / 7;
        // The inner squares take fixed shares of the outer radius rather than a share of their own sides, as the image builders draw
        // them, so one style looks the same from both packages.
        var outerRadius = Math.Min(w, h) * options.CornerRadiusPercent / 100;
        switch (options.FinderPatternStyle)
        {
            case FinderPatternStyle.Circle:
                outer = Region.Ellipse(0, 0, w, h);
                hole = Region.Ellipse(moduleWidth, moduleHeight, w - moduleWidth, h - moduleHeight);
                centre = Region.Ellipse(2 * moduleWidth, 2 * moduleHeight, w - 2 * moduleWidth, h - 2 * moduleHeight);
                break;
            case FinderPatternStyle.RoundedSquare:
                outer = Region.RoundedRect(0, 0, w, h, outerRadius);
                hole = Region.RoundedRect(moduleWidth, moduleHeight, w - moduleWidth, h - moduleHeight, outerRadius * 8 / 10);
                centre = Region.RoundedRect(2 * moduleWidth, 2 * moduleHeight, w - 2 * moduleWidth, h - 2 * moduleHeight, outerRadius * 6 / 10);
                break;
            default:
                outer = Region.RoundedRect(0, 0, w, h, outerRadius);
                hole = Region.RoundedRect(moduleWidth, moduleHeight, w - moduleWidth, h - moduleHeight, outerRadius * 7 / 10);
                centre = Region.Ellipse(2 * moduleWidth, 2 * moduleHeight, w - 2 * moduleWidth, h - 2 * moduleHeight);
                break;
        }
        return s;
    }

    private static void Fill(Span<byte> levels, int width, int fromRow, int toRow, int s, int levelCount, in Region outer, in Region hole, in Region centre)
    {
        var samples = s * s;
        var at = 0;
        for (var py = fromRow; py < toRow; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var count = 0;
                for (var j = 0; j < s; j++)
                {
                    long y = 4L * (py * s + j) + 2;
                    for (var i = 0; i < s; i++)
                    {
                        long x = 4L * (px * s + i) + 2;
                        if (centre.Contains(x, y) || (outer.Contains(x, y) && !hole.Contains(x, y)))
                            count++;
                    }
                }
                levels[at++] = (byte)((count * (levelCount - 1) + samples / 2) / samples);
            }
        }
    }

    /// <summary>A rounded rectangle, which with no radius is a rectangle, or the ellipse inscribed in one. The default contains nothing.</summary>
    private readonly struct Region(int kind, long x0, long y0, long x1, long y1, long radius)
    {
        // Past this product of the axes, the sum of the two squares in the ellipse test passes a long, so the test is made at a coarser unit.
        private const long LargestExactAxes = 2_000_000_000;

        private readonly int _kind = kind;
        private readonly long _x0 = x0, _y0 = y0, _x1 = x1, _y1 = y1, _radius = radius;

        // A radius past half the shorter side would put the corners' centres across each other, so it stops where the corners meet, as a renderer draws it.
        public static Region RoundedRect(long x0, long y0, long x1, long y1, long radius) => new(1, x0, y0, x1, y1, Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2));

        public static Region Ellipse(long x0, long y0, long x1, long y1) => new(2, x0, y0, x1, y1, 0);

        public bool Contains(long x, long y)
        {
            if (_kind == 0 || x < _x0 || x >= _x1 || y < _y0 || y >= _y1)
                return false;
            if (_kind == 2)
            {
                // Twice the offsets from the centre against the axes, so the halves stay whole.
                long dx = 2 * x - (_x0 + _x1), dy = 2 * y - (_y0 + _y1), rx = _x1 - _x0, ry = _y1 - _y0;
                // The offsets are halved toward zero, so a point and its mirror across the centre stay the same distance from it.
                while (rx * ry > LargestExactAxes)
                {
                    dx /= 2;
                    dy /= 2;
                    rx >>= 1;
                    ry >>= 1;
                }
                return dx * dx * ry * ry + dy * dy * rx * rx <= rx * rx * ry * ry;
            }
            var r = _radius;
            if (r == 0)
                return true;
            // Inside a corner's square, the point has to be within the radius of the corner's centre.
            var cx = x < _x0 + r ? _x0 + r : x > _x1 - r ? _x1 - r : x;
            var cy = y < _y0 + r ? _y0 + r : y > _y1 - r ? _y1 - r : y;
            long ox = x - cx, oy = y - cy;
            return ox * ox + oy * oy <= r * r;
        }
    }
}
