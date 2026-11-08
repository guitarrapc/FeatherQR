namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The icon area of a Standard QR code: a square of modules at the centre, snapped to the grid, with a border of modules around it,
/// all left in the light colour so the caller can draw an icon there.
/// </summary>
/// <remarks>
/// The symbol relies on its error correction to stay readable, as it does under the rendering package's icons, and the same limit holds:
/// the icon with its border takes at most <see cref="SymbolImageOptions.MaxCoreOccupancyPercent"/> of the core's side. An area that reaches
/// a finder pattern is refused, since nothing could read the result.
/// </remarks>
internal readonly struct IconArea
{
    public readonly bool Present;
    /// <summary>The cleared square in core modules: its top-left corner and its side, the icon with its border.</summary>
    public readonly int Row;
    public readonly int Column;
    public readonly int Size;
    /// <summary>The icon inside it.</summary>
    public readonly int IconRow;
    public readonly int IconColumn;
    public readonly int IconSize;

    private IconArea(int start, int size, int iconStart, int iconSize)
    {
        Present = true;
        Row = start;
        Column = start;
        Size = size;
        IconRow = iconStart;
        IconColumn = iconStart;
        IconSize = iconSize;
    }

    public static IconArea Get<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var icon = options.IconSizeModules;
        if (icon == 0)
        {
            if (!options.IconImage.IsEmpty)
                throw new ArgumentOutOfRangeException(nameof(options), "An icon image needs an icon area: set IconSizeModules.");
            return default;
        }
        if (!view.SupportsIcon)
            throw new ArgumentOutOfRangeException(nameof(options), "An icon area is drawn on Standard QR codes only.");

        var core = view.CoreWidth;
        var border = options.IconBorderModules;
        var total = icon + 2L * border;
        if (total > core)
            throw new ArgumentOutOfRangeException(nameof(options), $"An icon of {icon} modules with a border of {border} takes {total} modules, more than the symbol's {core} modules without its quiet zone.");
        var allowed = core * options.MaxCoreOccupancyPercent / 100;
        if (total > allowed)
            throw new ArgumentOutOfRangeException(nameof(options), $"An icon of {icon} modules with a border of {border} takes {total} modules, but {options.MaxCoreOccupancyPercent} percent of the symbol's {core} modules without its quiet zone allows {allowed}. Raise MaxCoreOccupancyPercent or make the icon smaller.");

        // Snapped to the grid: a core's side is odd, so an even icon cannot sit on the exact centre.
        var origin = (core - icon) / 2;
        var start = origin - border;
        if (start < 7 || start + total > core - 7)
            throw new ArgumentOutOfRangeException(nameof(options), "An icon area that reaches a finder pattern leaves nothing to read.");
        return new IconArea(start, (int)total, origin, icon);
    }

    public bool CoversRow(int coreRow) => Present && (uint)(coreRow - Row) < (uint)Size;

    public bool Covers(int coreRow, int coreColumn) => Present && (uint)(coreRow - Row) < (uint)Size && (uint)(coreColumn - Column) < (uint)Size;

    /// <summary>Sets the pixels from <paramref name="from"/> up to <paramref name="to"/> of a one-bit scanline to light, which is 1.</summary>
    public static void SetLightBits(Span<byte> line, int from, int to)
    {
        while (from < to && (from & 7) != 0)
        {
            line[from >> 3] |= (byte)(0x80 >> (from & 7));
            from++;
        }
        var whole = (to - from) >> 3;
        line.Slice(from >> 3, whole).Fill(0xFF);
        from += whole << 3;
        while (from < to)
        {
            line[from >> 3] |= (byte)(0x80 >> (from & 7));
            from++;
        }
    }
}
