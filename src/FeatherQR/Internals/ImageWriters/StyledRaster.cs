using System.Buffers;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The pixel rows of a styled image as levels, 0 for the light colour up to the last level for the dark one: each dark module's shape
/// stamped into its cell, and each finder pattern drawn as its own shape over its box, never as styled modules.
/// </summary>
/// <remarks>
/// One instance a thread is kept and its stamps live in a pooled buffer, so a call allocates nothing once warm. A module's stamp is made once for each
/// cell size the layout gives, two widths and two heights at most at a set size, and copied into every dark module. A finder pattern's
/// stamp is made once for each box size, which the three boxes share unless a set size makes one a pixel larger. A stamp past
/// <see cref="MaxKeptStampPixels"/> is not kept, and its rows are sampled as they are asked for.
/// </remarks>
internal sealed class StyledRaster
{
    [ThreadStatic]
    private static StyledRaster? cached;

    private const int MaxModuleStamps = 4;
    private const int MaxFinders = 3;
    // Seven stamps at this size are a few of the image's own megabytes, and past it a row is sampled instead.
    internal const int MaxKeptStampPixels = 1 << 22;

    private ImageLayout _layout;
    private SymbolImageOptions _options;
    private IconArea _icon;
    private int _levels;
    private int _quietZone;
    private int _coreWidth;
    private int _coreHeight;
    private byte[] _stamps = [];
    private readonly int[] _stampWidths = new int[MaxModuleStamps];
    private readonly int[] _stampHeights = new int[MaxModuleStamps];
    private readonly int[] _stampOffsets = new int[MaxModuleStamps];
    private int _stampCount;
    private int _finders;
    private readonly int[] _finderRows = new int[MaxFinders];
    private readonly int[] _finderColumns = new int[MaxFinders];
    // A stamp's start in the rented buffer, or -1 where none is kept: a square finder pattern, drawn as whole cells, or a box too large to keep.
    private readonly int[] _finderOffsets = new int[MaxFinders];

    /// <summary>
    /// The colour of a level: the dark colour at that share of its alpha painted over the light one, so the first level is the light colour
    /// and the last the dark one as the one-bit output paints it.
    /// </summary>
    public static SymbolColor LevelColor(SymbolColor dark, SymbolColor light, int level, int levels)
    {
        if (level == 0)
            return light;
        var alpha = (byte)((dark.A * level + (levels - 1) / 2) / (levels - 1));
        return new SymbolColor(dark.R, dark.G, dark.B, alpha).Over(light);
    }

    public static StyledRaster Take()
    {
        var raster = cached ?? new StyledRaster();
        cached = null;
        return raster;
    }

    public static void Return(StyledRaster raster)
    {
        if (raster._stamps.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(raster._stamps);
            raster._stamps = [];
        }
        // The options hold the caller's icon image, which the kept instance must not keep alive.
        raster._options = default;
        cached = raster;
    }

    public void Prepare<TView>(in TView view, in ImageLayout layout, in SymbolImageOptions options, in IconArea icon, int levels)
        where TView : struct, IModuleMatrixView
    {
        _layout = layout;
        _options = options;
        _icon = icon;
        _levels = levels;
        _quietZone = (view.Width - view.CoreWidth) / 2;
        _coreWidth = view.CoreWidth;
        _coreHeight = view.CoreHeight;
        _finders = view.FinderPatternCount;
        for (var i = 0; i < _finders; i++)
            view.GetFinderPatternOrigin(i, out _finderRows[i], out _finderColumns[i]);

        // The cell sizes the symbol's columns and rows take: one each at whole pixels, up to two each otherwise.
        _stampCount = 0;
        long total = 0;
        Span<int> widths = stackalloc int[2];
        Span<int> heights = stackalloc int[2];
        var widthCount = Sizes(widths, layout.Columns, isRow: false);
        var heightCount = Sizes(heights, layout.Rows, isRow: true);
        for (var i = 0; i < widthCount; i++)
        {
            for (var j = 0; j < heightCount; j++)
            {
                var pixels = (long)widths[i] * heights[j];
                _stampWidths[_stampCount] = widths[i];
                _stampHeights[_stampCount] = heights[j];
                _stampOffsets[_stampCount] = pixels > MaxKeptStampPixels ? -1 : (int)total;
                if (pixels <= MaxKeptStampPixels)
                    total += pixels;
                _stampCount++;
            }
        }
        for (var i = 0; i < _finders; i++)
        {
            _finderOffsets[i] = -1;
            if (options.FinderPatternStyle == FinderPatternStyle.Square)
                continue;
            FinderBox(i, out _, out var width, out _, out var height);
            var pixels = (long)width * height;
            if (pixels > MaxKeptStampPixels)
                continue;
            // The boxes are the same size at a uniform module size, and then one stamp serves them all.
            var shared = -1;
            for (var j = 0; j < i; j++)
            {
                FinderBox(j, out _, out var earlierWidth, out _, out var earlierHeight);
                if (_finderOffsets[j] >= 0 && earlierWidth == width && earlierHeight == height)
                    shared = _finderOffsets[j];
            }
            if (shared >= 0)
            {
                _finderOffsets[i] = shared;
                continue;
            }
            _finderOffsets[i] = (int)total;
            total += pixels;
        }

        if (_stamps.Length < total)
        {
            if (_stamps.Length != 0)
                ArrayPool<byte>.Shared.Return(_stamps);
            _stamps = ArrayPool<byte>.Shared.Rent((int)total);
        }
        for (var k = 0; k < _stampCount; k++)
        {
            if (_stampOffsets[k] >= 0)
                Stamp.Module(_stamps.AsSpan(_stampOffsets[k], _stampWidths[k] * _stampHeights[k]), _stampWidths[k], _stampHeights[k], in options, levels);
        }
        for (var i = 0; i < _finders; i++)
        {
            if (_finderOffsets[i] < 0 || IsSharedFromAnEarlier(i))
                continue;
            FinderBox(i, out _, out var width, out _, out var height);
            Stamp.Finder(_stamps.AsSpan(_finderOffsets[i], width * height), width, height, in options, levels);
        }
    }

    private bool IsSharedFromAnEarlier(int index)
    {
        for (var j = 0; j < index; j++)
        {
            if (_finderOffsets[j] == _finderOffsets[index])
                return true;
        }
        return false;
    }

    // The distinct sizes of the symbol's cells along an axis, which differ by at most one pixel.
    private int Sizes(Span<int> sizes, int modules, bool isRow)
    {
        var count = 0;
        for (var k = 0; k < modules; k++)
        {
            var size = isRow ? _layout.RowStart(k + 1) - _layout.RowStart(k) : _layout.ColumnStart(k + 1) - _layout.ColumnStart(k);
            if (count > 0 && (sizes[0] == size || (count == 2 && sizes[1] == size)))
                continue;
            if (count == 2)
                break;
            sizes[count++] = size;
        }
        return count;
    }

    // The pixels of a finder pattern's seven-by-seven box.
    private void FinderBox(int index, out int left, out int width, out int top, out int height)
    {
        left = _layout.ColumnStart(_quietZone + _finderColumns[index]);
        width = _layout.ColumnStart(_quietZone + _finderColumns[index] + 7) - left;
        top = _layout.RowStart(_quietZone + _finderRows[index]);
        height = _layout.RowStart(_quietZone + _finderRows[index] + 7) - top;
    }

    /// <summary>Whether pixel row <paramref name="y"/> is light alone: above or below the symbol, or in its quiet zone.</summary>
    public bool IsLightRow(int y)
    {
        var row = _layout.RowOf(y);
        return (uint)(row - _quietZone) >= (uint)_coreHeight;
    }

    /// <summary>Writes pixel row <paramref name="y"/> of the image as levels, <see cref="ImageLayout.Width"/> of them.</summary>
    public void ComposeRow<TView>(in TView view, int y, Span<byte> levels)
        where TView : struct, IModuleMatrixView
    {
        levels.Clear();
        var layout = _layout;
        var row = layout.RowOf(y);
        var coreRow = row - _quietZone;
        if ((uint)coreRow >= (uint)_coreHeight)
            return;
        var subRow = y - layout.RowStart(row);
        var cellHeight = layout.RowStart(row + 1) - layout.RowStart(row);

        var core = view.CoreBits;
        var start = (long)coreRow * _coreWidth;
        var column = 0;
        var dark = false;
        while (column < _coreWidth)
        {
            var length = PackedRuns.Length(core, start + column, _coreWidth - column, dark);
            if (dark)
            {
                for (var c = column; c < column + length; c++)
                {
                    // A finder pattern's modules are drawn below as one shape, so a stamp here would only be painted over.
                    if (view.IsFinderPattern(coreRow, c))
                        continue;
                    var left = layout.ColumnStart(_quietZone + c);
                    var cellWidth = layout.ColumnStart(_quietZone + c + 1) - left;
                    var stamp = StampOf(cellWidth, cellHeight);
                    if (stamp >= 0)
                        _stamps.AsSpan(stamp + subRow * cellWidth, cellWidth).CopyTo(levels.Slice(left, cellWidth));
                    else
                        Stamp.ModuleRow(levels.Slice(left, cellWidth), cellWidth, cellHeight, subRow, in _options, _levels);
                }
            }
            column += length;
            dark = !dark;
        }

        for (var i = 0; i < _finders; i++)
        {
            int finderRow = _finderRows[i], finderColumn = _finderColumns[i];
            if ((uint)(coreRow - finderRow) >= 7)
                continue;
            if (_options.FinderPatternStyle == FinderPatternStyle.Square)
            {
                // The standard pattern, each of its dark modules a whole cell, whatever the module style.
                for (var c = finderColumn; c < finderColumn + 7; c++)
                {
                    if (view.GetCoreModule(coreRow, c))
                        levels.Slice(layout.ColumnStart(_quietZone + c), layout.ColumnStart(_quietZone + c + 1) - layout.ColumnStart(_quietZone + c)).Fill((byte)(_levels - 1));
                }
                continue;
            }
            FinderBox(i, out var left, out var width, out var top, out var height);
            var target = levels.Slice(left, width);
            if (_finderOffsets[i] >= 0)
                _stamps.AsSpan(_finderOffsets[i] + (y - top) * width, width).CopyTo(target);
            else
                Stamp.FinderRow(target, width, height, y - top, in _options, _levels);
        }

        if (_icon.CoversRow(coreRow))
        {
            var from = layout.ColumnStart(_quietZone + _icon.Column);
            levels.Slice(from, layout.ColumnStart(_quietZone + _icon.Column + _icon.Size) - from).Clear();
        }
    }

    // The stamp's start for a cell size, or -1 for a cell sampled row by row.
    private int StampOf(int width, int height)
    {
        for (var k = 0; k < _stampCount; k++)
        {
            if (_stampWidths[k] == width && _stampHeights[k] == height)
                return _stampOffsets[k];
        }
        throw new InvalidOperationException("A cell size the layout did not announce; this is a bug in StyledRaster.");
    }
}
