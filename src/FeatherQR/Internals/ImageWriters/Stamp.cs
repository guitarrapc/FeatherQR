using System.Buffers;
using System.Diagnostics;

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
        ModuleRegion(width, height, in options, out var outer);
        FillMirrored(levels, width, height, levelCount, in outer, default, default);
    }

    /// <summary>One pixel row, <paramref name="y"/>, of the module shape's coverage of a cell.</summary>
    public static void ModuleRow(Span<byte> levels, int width, int height, int y, in SymbolImageOptions options, int levelCount)
    {
        ModuleRegion(width, height, in options, out var outer);
        Fill(levels, width, y, y + 1, levelCount, in outer, default, default);
    }

    /// <summary>A finder pattern's coverage of its box of <paramref name="width"/> by <paramref name="height"/> pixels, row after row.</summary>
    public static void Finder(Span<byte> levels, int width, int height, in SymbolImageOptions options, int levelCount)
    {
        FinderRegions(width, height, in options, out var outer, out var hole, out var centre);
        FillMirrored(levels, width, height, levelCount, in outer, in hole, in centre);
    }

    /// <summary>One pixel row, <paramref name="y"/>, of a finder pattern's coverage of its box.</summary>
    public static void FinderRow(Span<byte> levels, int width, int height, int y, in SymbolImageOptions options, int levelCount)
    {
        FinderRegions(width, height, in options, out var outer, out var hole, out var centre);
        Fill(levels, width, y, y + 1, levelCount, in outer, in hole, in centre);
    }

    // The module's shape inset by the scale: a square, a rounded square or the inscribed ellipse.
    internal static void ModuleRegion(int width, int height, in SymbolImageOptions options, out Region outer)
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
    }

    // The three regions of a finder pattern in its box: nested ellipses, nested rounded squares, or rounded squares around an ellipse.
    internal static void FinderRegions(int width, int height, in SymbolImageOptions options, out Region outer, out Region hole, out Region centre)
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
    }

    // Every region is centred on the box's middle row and reads the same at a row and its mirror, so the bottom half copies the top
    // rather than being sampled. The box is open at its bottom edge and closed at its top, so a row with a sample on a region's bottom edge
    // is sampled again.
    private static void FillMirrored(Span<byte> levels, int width, int height, int levelCount, in Region outer, in Region hole, in Region centre)
    {
        var top = (height + 1) / 2;
        Fill(levels, width, 0, top, levelCount, in outer, in hole, in centre);
        for (var py = top; py < height; py++)
            levels.Slice((height - 1 - py) * width, width).CopyTo(levels.Slice(py * width, width));
        Refill(levels, width, top, height, outer.BottomEdgeRow, levelCount, in outer, in hole, in centre);
        Refill(levels, width, top, height, hole.BottomEdgeRow, levelCount, in outer, in hole, in centre);
        Refill(levels, width, top, height, centre.BottomEdgeRow, levelCount, in outer, in hole, in centre);
    }

    private static void Refill(Span<byte> levels, int width, int top, int height, int row, int levelCount, in Region outer, in Region hole, in Region centre)
    {
        if (row >= top && row < height)
            Fill(levels.Slice(row * width, width), width, row, row + 1, levelCount, in outer, in hole, in centre);
    }

    // Each region is convex, so on a sample row it holds one run of sample columns, and a pixel's count is the length of the runs over
    // its columns. Testing every sample against the regions instead is many times slower on a large shape.
    private static void Fill(Span<byte> levels, int width, int fromRow, int toRow, int levelCount, in Region outer, in Region hole, in Region centre)
    {
        const int s = SamplesPerSide, samples = s * s, StackPixels = 256;
        var columns = width * s;
        int[]? rented = null;
        var counts = width < StackPixels ? stackalloc int[StackPixels + 1] : (rented = ArrayPool<int>.Shared.Rent(width + 1));
        counts = counts.Slice(0, width + 1);
        try
        {
            var at = 0;
            int outerFrom = 0, outerTo = 0, holeFrom = 0, holeTo = 0, centreFrom = 0, centreTo = 0;
            for (var py = fromRow; py < toRow; py++)
            {
                counts.Clear();
                for (var j = 0; j < s; j++)
                {
                    long y = 4L * (py * s + j) + 2;
                    outer.Next(y, columns, ref outerFrom, ref outerTo);
                    hole.Next(y, columns, ref holeFrom, ref holeTo);
                    centre.Next(y, columns, ref centreFrom, ref centreTo);
                    // The ring is the outer run less the hole's, two runs at most. The centre lies inside the hole, a module in from it in every
                    // style, so its run never meets the ring's and adds on its own.
                    int leftFrom = outerFrom, leftTo = outerTo, rightFrom = 0, rightTo = 0;
                    if (holeFrom < holeTo)
                    {
                        leftTo = Math.Min(outerTo, holeFrom);
                        rightFrom = Math.Max(outerFrom, holeTo);
                        rightTo = outerTo;
                    }
                    Add(counts, leftFrom, leftTo);
                    Add(counts, rightFrom, rightTo);
                    Add(counts, centreFrom, centreTo);
                }
                var count = 0;
                for (var px = 0; px < width; px++)
                {
                    count += counts[px];
                    levels[at++] = (byte)((count * (levelCount - 1) + samples / 2) / samples);
                }
            }
        }
        finally
        {
            if (rented != null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    // Adds a run of sample columns to the pixels' counts, kept as differences so a run costs four writes whatever its length: the
    // part in its first pixel, s in each pixel between, and the part in its last.
    private static void Add(Span<int> counts, int from, int to)
    {
        const int s = SamplesPerSide;
        if (from >= to)
            return;
        int first = from / s, last = (to - 1) / s;
        if (first == last)
        {
            counts[first] += to - from;
            counts[first + 1] -= to - from;
            return;
        }
        int head = (first + 1) * s - from, tail = to - last * s;
        counts[first] += head;
        counts[first + 1] += s - head;
        counts[last] += tail - s;
        counts[last + 1] -= tail;
    }

    /// <summary>A rounded rectangle, which with no radius is a rectangle, or the ellipse inscribed in one. The default contains nothing.</summary>
    internal readonly struct Region(int kind, long x0, long y0, long x1, long y1, long radius)
    {
        // Past this product of the axes, the sum of the two squares in the ellipse test passes a long, so the test is made at a coarser unit.
        private const long LargestExactAxes = 2_000_000_000;

        private readonly int _kind = kind;
        private readonly long _x0 = x0, _y0 = y0, _x1 = x1, _y1 = y1, _radius = radius;

        // A radius past half the shorter side would put the corners' centres across each other, so it stops where the corners meet, as a renderer draws it.
        public static Region RoundedRect(long x0, long y0, long x1, long y1, long radius) => new(1, x0, y0, x1, y1, Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2));

        public static Region Ellipse(long x0, long y0, long x1, long y1) => new(2, x0, y0, x1, y1, 0);

        // The pixel row that holds a sample on the region's bottom edge, or -1 where no sample lands on it.
        public int BottomEdgeRow => _kind != 0 && (_y1 - 2) % 4 == 0 ? (int)((_y1 - 2) / (4 * SamplesPerSide)) : -1;

        /// <summary>
        /// The run of sample columns q below <paramref name="columns"/> whose x = 4q + 2 the region contains on row <paramref name="y"/>,
        /// from <paramref name="from"/> up to <paramref name="to"/>, empty when the two are equal.
        /// </summary>
        public void Run(long y, int columns, out int from, out int to)
        {
            from = to = 0;
            if (_kind == 0 || y < _y0 || y >= _y1)
                return;
            // The columns whose samples fall inside the box.
            int lo = (int)Math.Max(0, (_x0 + 1) / 4), hi = (int)Math.Min(columns, (_x1 + 1) / 4);
            if (lo >= hi)
                return;
            // The shape is convex and symmetric about its centre, so a run that is not empty holds a column next to the centre.
            var mid = (int)Math.Min(hi - 1, Math.Max(lo, ((_x0 + _x1) / 2 - 2) / 4));
            var seed = -1;
            for (var q = Math.Max(lo, mid - 1); q <= Math.Min(hi - 1, mid + 1) && seed < 0; q++)
            {
                if (Contains(4L * q + 2, y))
                    seed = q;
            }
            if (seed < 0)
                return;
            int low = lo, high = seed;
            while (low < high)
            {
                var m = (low + high) / 2;
                if (Contains(4L * m + 2, y))
                    high = m;
                else
                    low = m + 1;
            }
            from = low;
            to = MirrorEnd(from, hi);
        }

        // Every region is centred on a column boundary, so the run's right end mirrors its left. The box's right edge is open where its
        // left is closed, and hi keeps the column on it out.
        private int MirrorEnd(int from, int hi)
        {
            Debug.Assert((_x0 + _x1) % 4 == 0, "the region is centred on a column boundary");
            return Math.Min(hi, (int)((_x0 + _x1) / 4) - from);
        }

        /// <summary>
        /// The run on row <paramref name="y"/> from the run on the sample row before it. Two runs that are not empty both hold a column
        /// next to the centre, so the left end walks from where it was by the few columns the edge moves, rather than being searched for
        /// again, and the right end mirrors it.
        /// </summary>
        public void Next(long y, int columns, ref int from, ref int to)
        {
            if (from >= to || y < _y0 || y >= _y1)
            {
                Run(y, columns, out from, out to);
                return;
            }
            int lo = (int)Math.Max(0, (_x0 + 1) / 4), hi = (int)Math.Min(columns, (_x1 + 1) / 4);
            if (Contains(4L * from + 2, y))
            {
                while (from > lo && Contains(4L * (from - 1) + 2, y))
                    from--;
            }
            else
            {
                // Walking in past the column the two runs share means this row's run is empty.
                do
                    from++;
                while (from < to && !Contains(4L * from + 2, y));
                if (from >= to)
                {
                    from = to = 0;
                    return;
                }
            }
            to = MirrorEnd(from, hi);
        }

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
