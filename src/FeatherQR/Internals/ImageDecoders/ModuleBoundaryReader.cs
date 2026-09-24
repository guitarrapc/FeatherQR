namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// An axis-aligned frame: a finder's centre pixel and the unit image axes the symbol's columns (u) and rows (v) run along.
/// Boundary positions count pixels from that centre along an axis, pixel k covering [k, k + 1).
/// </summary>
internal readonly struct AxisAlignedFrame
{
    public readonly int CenterX, CenterY, UX, UY, VX, VY;

    public AxisAlignedFrame(int centerX, int centerY, int uX, int uY, int vX, int vY)
    {
        CenterX = centerX;
        CenterY = centerY;
        UX = uX;
        UY = uY;
        VX = vX;
        VY = vY;
    }

    /// <summary>The image point of boundary position (s, t), in continuous image coordinates.</summary>
    public void ToImage(float s, float t, out float x, out float y)
    {
        // Against the image axis pixel k covers [−k, −k + 1), hence the one-pixel base
        x = CenterX + (UX + VX < 0 ? 1f : 0f) + s * UX + t * VX;
        y = CenterY + (UY + VY < 0 ? 1f : 0f) + s * UY + t * VY;
    }
}

/// <summary>
/// Reads a crisp, axis-aligned symbol at 1 to 2 px/module through its module boundaries instead of through a fitted grid.
/// </summary>
/// <remarks>
/// At that density a crisp render draws each module 1 or 2 px wide. A finder centre is good to half a pixel, a sample has (pitch − 1) / 2 to spare, and no grid extrapolated from a centre keeps that across a symbol.
/// The boundaries themselves are exact. A timing pattern crosses every one of them past the finder, the finder's centre line crosses four of its own, and the two inside its centre square show in the data modules in line with it. Each module is then read at a pixel known to lie inside it, however wide it came out.
/// Shared by the symbologies; each knows where its timing patterns run and where they end.
/// </remarks>
internal static class ModuleBoundaryReader
{
    /// <summary>The marker for a boundary no line crossed.</summary>
    public const int Unknown = int.MinValue;

    /// <summary>A finder this many pixels per module or larger is left to the fitted grids, which hold from there.</summary>
    public const float MaxModuleSize = 1.75f;

    /// <summary>The unit image axis a vector lies along, when it is within about 3° of one.</summary>
    public static bool TryAxisDirection(float dx, float dy, out int unitX, out int unitY)
    {
        unitX = unitY = 0;
        var length = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (length <= 0f || Math.Min(Math.Abs(dx), Math.Abs(dy)) > 0.05f * length)
            return false;
        if (Math.Abs(dx) > Math.Abs(dy))
            unitX = dx > 0f ? 1 : -1;
        else
            unitY = dy > 0f ? 1 : -1;
        return true;
    }

    /// <summary>
    /// From a finder's centre pixel along a unit axis: the offset of the first pixel of the pattern's last dark run (its edge row), past the rest of the centre square and the light ring.
    /// </summary>
    public static bool TryFinderEdgeRow(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int dirX, int dirY, out int offset)
    {
        offset = 0;
        var inRing = false;
        for (var step = 0; step < 16; step++)
        {
            var dark = IsDarkAt(luminance, width, height, threshold, centerX + step * dirX, centerY + step * dirY);
            if (!inRing && !dark)
            {
                inRing = true;
            }
            else if (inRing && dark)
            {
                offset = step;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Walks a timing line from a pixel on the finder's edge row: back to the finder's outer edge, then forward over the separator and the timing modules, recording every boundary crossed at its module index.
    /// The line ends on a far finder's edge row, seven modules of dark, when <paramref name="endsOnFinder"/>; otherwise at the quiet zone after a dark module.
    /// With <paramref name="allowTriples"/> a dark run of three modules is part of the line (rMQR's alignment and corner patterns sit on its timing rows); its two inner boundaries stay unknown.
    /// A crisp module is 1 or 2 px below <see cref="MaxModuleSize"/>, three are 3 to 6 and seven are 7 or more, so a run's length says which it is.
    /// <paramref name="maxPosition"/> is how far past the starting pixel the line may run, and <paramref name="count"/> the number of modules it crossed: the last boundary's index.
    /// </summary>
    public static bool TryReadTimingLine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int startX, int startY, int dirX, int dirY, float moduleSize, bool endsOnFinder, bool allowTriples, int maxPosition, Span<int> boundaries, out int count)
    {
        count = 0;
        boundaries.Fill(Unknown);

        // Back to the outer edge: boundary 0
        var first = 0;
        while (IsDarkAt(luminance, width, height, threshold, startX + (first - 1) * dirX, startY + (first - 1) * dirY))
        {
            first--;
            if (first < -8f * moduleSize)
                return false;
        }
        boundaries[0] = first;

        // Forward. The edge row ends at boundary 7, then every run is one module
        // A crisp module is its pitch rounded down or up, and the finder's measure of the pitch is good to a fifth
        var maxModuleRun = (int)Math.Ceiling(1.2f * moduleSize);
        var maxFinderRun = (int)Math.Ceiling(7f * 1.6f * moduleSize);
        var index = 0;
        var runStart = first;
        var dark = true;
        for (var position = first + 1; ; position++)
        {
            if (position > maxPosition)
                return false;
            var run = position - runStart;
            if (IsDarkAt(luminance, width, height, threshold, startX + position * dirX, startY + position * dirY) == dark)
            {
                // A light run too long for a module is the quiet zone: the last boundary closed the symbol
                if (!endsOnFinder && !dark && index >= 7 && run >= maxModuleRun)
                    break;
                continue;
            }

            var ended = false;
            if (index == 0)
            {
                if (run < 5 || run > maxFinderRun)
                    return false;
                index = 7;
            }
            else if (run <= maxModuleRun)
            {
                index++;
            }
            else if (allowTriples && dark && run <= 3 * maxModuleRun)
            {
                index += 3;
            }
            else if (endsOnFinder && dark && run <= maxFinderRun)
            {
                // A dark run too long for a module is the far finder's edge row
                index += 7;
                ended = true;
            }
            else
            {
                return false;
            }

            if (index >= boundaries.Length)
                return false;
            boundaries[index] = position;
            if (ended)
                break;
            runStart = position;
            dark = !dark;
        }

        count = index;
        return true;
    }

    /// <summary>
    /// A finder's centre line crosses boundaries 1, 2, 5 and 6 of its seven modules; written at <paramref name="firstIndex"/> + those when the line reads dark-light-dark-light-dark.
    /// <paramref name="along"/> is the finder centre's position on the axis.
    /// </summary>
    public static void ReadFinderLine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int dirX, int dirY, int along, Span<int> boundaries, int firstIndex)
    {
        Span<int> edges = stackalloc int[4];
        var position = along;
        for (var edge = 1; edge >= 0; edge--)
        {
            var wantDark = edge == 1;
            var steps = 0;
            while (IsDarkAt(luminance, width, height, threshold, centerX + (position - 1) * dirX, centerY + (position - 1) * dirY) == wantDark)
            {
                position--;
                if (++steps > 8)
                    return;
            }
            edges[edge] = position;
        }
        position = along;
        for (var edge = 2; edge <= 3; edge++)
        {
            var wantDark = edge == 2;
            var steps = 0;
            while (IsDarkAt(luminance, width, height, threshold, centerX + position * dirX, centerY + position * dirY) == wantDark)
            {
                position++;
                if (++steps > 8)
                    return;
            }
            edges[edge] = position;
        }

        boundaries[firstIndex + 1] = edges[0];
        boundaries[firstIndex + 2] = edges[1];
        boundaries[firstIndex + 5] = edges[2];
        boundaries[firstIndex + 6] = edges[3];
    }

    /// <summary>
    /// Boundaries <paramref name="index"/> + 1 and + 2 lie inside a run of three modules, a finder's centre square or an alignment pattern, where the line that found the run does not cross them. The modules in line with it do: over cross lines <paramref name="fromLine"/> to <paramref name="toLine"/>, the positions where any of them changes colour are those two boundaries, since a crisp module has no edge inside it.
    /// Left unknown unless exactly two positions show an edge, or the square is three pixels and leaves no choice.
    /// </summary>
    public static void ReadCentreBoundaries(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int dirX, int dirY, int crossX, int crossY, Span<int> boundaries, int index, ReadOnlySpan<int> crossBoundaries, int fromLine, int toLine)
    {
        var from = boundaries[index];
        var to = boundaries[index + 3];
        if (from == Unknown || to == Unknown || to - from < 3 || to - from > 8)
            return;
        if (to - from == 3)
        {
            boundaries[index + 1] = from + 1;
            boundaries[index + 2] = from + 2;
            return;
        }

        Span<bool> edge = stackalloc bool[8];
        edge.Clear();
        for (var line = fromLine; line < toLine; line++)
        {
            if (crossBoundaries[line] == Unknown || crossBoundaries[line + 1] == Unknown)
                continue;
            var cross = (crossBoundaries[line] + crossBoundaries[line + 1] - 1) / 2;
            var previous = IsDarkAt(luminance, width, height, threshold, centerX + from * dirX + cross * crossX, centerY + from * dirY + cross * crossY);
            for (var position = from + 1; position < to; position++)
            {
                var dark = IsDarkAt(luminance, width, height, threshold, centerX + position * dirX + cross * crossX, centerY + position * dirY + cross * crossY);
                if (dark != previous)
                    edge[position - from] = true;
                previous = dark;
            }
        }

        int first = 0, second = 0, edges = 0;
        for (var offset = 1; offset < to - from; offset++)
        {
            if (!edge[offset])
                continue;
            edges++;
            if (first == 0)
                first = offset;
            else
                second = offset;
        }
        if (edges != 2)
            return;
        boundaries[index + 1] = from + first;
        boundaries[index + 2] = from + second;
    }

    /// <summary>
    /// <see cref="ReadCentreBoundaries"/> for every run of three modules in the table: a known boundary, two unknown ones, a known one.
    /// </summary>
    public static void ReadRunInteriors(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int dirX, int dirY, int crossX, int crossY, Span<int> boundaries, int count, ReadOnlySpan<int> crossBoundaries, int crossCount)
    {
        for (var index = 0; index + 3 <= count; index++)
        {
            if (boundaries[index] != Unknown && boundaries[index + 1] == Unknown && boundaries[index + 2] == Unknown && boundaries[index + 3] != Unknown)
                ReadCentreBoundaries(luminance, width, height, threshold, centerX, centerY, dirX, dirY, crossX, crossY, boundaries, index, crossBoundaries, 0, crossCount);
        }
    }

    /// <summary>
    /// Fills the boundaries no line crossed from position = start + pitch · index fitted to the ones that were, each kept between its neighbours, and requires the whole table to rise by at least a pixel per module.
    /// </summary>
    public static bool TryFillBoundaries(Span<int> boundaries, int count)
    {
        double sumI = 0, sumP = 0, sumII = 0, sumIP = 0;
        var known = 0;
        for (var i = 0; i <= count; i++)
        {
            if (boundaries[i] == Unknown)
                continue;
            sumI += i;
            sumP += boundaries[i];
            sumII += (double)i * i;
            sumIP += (double)i * boundaries[i];
            known++;
        }
        var denominator = known * sumII - sumI * sumI;
        if (denominator <= 0 || boundaries[0] == Unknown || boundaries[count] == Unknown)
            return false;
        var pitch = (known * sumIP - sumI * sumP) / denominator;
        var start = (sumP - pitch * sumI) / known;

        for (var i = 1; i <= count; i++)
        {
            if (boundaries[i] == Unknown)
            {
                var next = i + 1;
                while (boundaries[next] == Unknown)
                    next++;
                var fitted = (int)Math.Round(start + pitch * i);
                boundaries[i] = Math.Min(Math.Max(fitted, boundaries[i - 1] + 1), boundaries[next] - (next - i));
            }
            if (boundaries[i] <= boundaries[i - 1])
                return false;
        }
        return true;
    }

    /// <summary>Reads every module at the middle pixel of the cell its boundaries frame.</summary>
    public static void Sample(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in AxisAlignedFrame frame, ReadOnlySpan<int> columns, int columnCount, ReadOnlySpan<int> rows, int rowCount, Span<byte> modules)
    {
        for (var row = 0; row < rowCount; row++)
        {
            var t = (rows[row] + rows[row + 1] - 1) / 2;
            for (var column = 0; column < columnCount; column++)
            {
                var s = (columns[column] + columns[column + 1] - 1) / 2;
                modules[row * columnCount + column] = IsDarkAt(luminance, width, height, threshold, frame.CenterX + s * frame.UX + t * frame.VX, frame.CenterY + s * frame.UY + t * frame.VY) ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>
    /// Dark modules on the line past the last column and the last row (one module as wide as the last), read like <see cref="Sample"/>; outside the image counts as light.
    /// </summary>
    public static int CountQuietZoneDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in AxisAlignedFrame frame, ReadOnlySpan<int> columns, int columnCount, ReadOnlySpan<int> rows, int rowCount)
    {
        var s = columns[columnCount] + (columns[columnCount] - columns[columnCount - 1] - 1) / 2;
        var t = rows[rowCount] + (rows[rowCount] - rows[rowCount - 1] - 1) / 2;
        var dark = IsDarkAt(luminance, width, height, threshold, frame.CenterX + s * frame.UX + t * frame.VX, frame.CenterY + s * frame.UY + t * frame.VY) ? 1 : 0;
        for (var row = 0; row < rowCount; row++)
        {
            var rowT = (rows[row] + rows[row + 1] - 1) / 2;
            if (IsDarkAt(luminance, width, height, threshold, frame.CenterX + s * frame.UX + rowT * frame.VX, frame.CenterY + s * frame.UY + rowT * frame.VY))
                dark++;
        }
        for (var column = 0; column < columnCount; column++)
        {
            var columnS = (columns[column] + columns[column + 1] - 1) / 2;
            if (IsDarkAt(luminance, width, height, threshold, frame.CenterX + columnS * frame.UX + t * frame.VX, frame.CenterY + columnS * frame.UY + t * frame.VY))
                dark++;
        }
        return dark;
    }

    public static bool IsDarkAt(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int x, int y)
        => x >= 0 && y >= 0 && x < width && y < height && luminance[y * width + x] < threshold;
}
