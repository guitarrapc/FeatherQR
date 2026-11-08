namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// Where a symbol's pixels lie in its image: the canvas, the first pixel of each module column and row, and the light rows and
/// columns around the symbol when the canvas is larger than it.
/// </summary>
/// <remarks>
/// <para>
/// A module is <see cref="Numerator"/> / <see cref="Denominator"/> pixels. Without a set size that is <see cref="SymbolImageOptions.ModulePixelSize"/>
/// whole pixels, and module k starts at k times it. With a set size the symbol fills the canvas at one scale, the smaller the two sides allow,
/// centred on whole pixels, and pixel x belongs to module floor((x + 1/2) × Denominator / Numerator), the module its centre falls in, which is
/// where a renderer that snaps a module drawn at a fractional position puts it. So module k starts at the first pixel whose centre is at or past
/// k × Numerator / Denominator, and adjacent modules differ by at most one pixel.
/// </para>
/// <para>
/// The symbol's columns and rows, quiet zone included, are numbered from 0. The light rows above and below the symbol are a row group each,
/// so a writer walks <see cref="Groups"/> groups and asks each for its symbol row, which is -1 above the symbol and <see cref="Rows"/> below it.
/// </para>
/// </remarks>
internal readonly struct ImageLayout
{
    public readonly int Width;
    public readonly int Height;
    /// <summary>The first pixel of module column 0 and of module row 0.</summary>
    public readonly int Left;
    public readonly int Top;
    /// <summary>The symbol's modules across and down, quiet zone included.</summary>
    public readonly int Columns;
    public readonly int Rows;
    /// <summary>The pixels a module is, as a fraction. The denominator is 1 where a module is a whole number of pixels.</summary>
    public readonly long Numerator;
    public readonly long Denominator;

    private ImageLayout(int width, int height, int left, int top, int columns, int rows, long numerator, long denominator)
    {
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Columns = columns;
        Rows = rows;
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>Every module the same whole number of pixels and nothing around the symbol, which is the image without a set size.</summary>
    public bool Uniform => Denominator == 1 && Left == 0 && Top == 0 && Width == Columns * Numerator && Height == Rows * Numerator;

    public static ImageLayout Create<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        int columns = view.Width, rows = view.Height;
        if (!options.HasSize)
        {
            long pixels = options.ModulePixelSize;
            if (columns * pixels > int.MaxValue || rows * pixels > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options), $"A module of {pixels} pixels makes the image {columns * pixels} × {rows * pixels} pixels, and a side can be at most {int.MaxValue}.");
            return new ImageLayout((int)(columns * pixels), (int)(rows * pixels), 0, 0, columns, rows, pixels, 1);
        }

        long width = options.Width, height = options.Height;
        long numerator, denominator;
        int left = 0, top = 0;
        if (height == 0 || (width != 0 && width * rows <= height * columns))
        {
            // The width binds, or is the one side given, and the height follows or pads.
            numerator = width;
            denominator = columns;
            if (height == 0)
                height = Fitted(numerator, denominator, rows);
            else
                top = (int)((height * columns - rows * width) / (2 * columns));
        }
        else
        {
            numerator = height;
            denominator = rows;
            if (width == 0)
                width = Fitted(numerator, denominator, columns);
            else
                left = (int)((width * rows - columns * height) / (2 * rows));
        }
        if (numerator < denominator)
            throw new ArgumentOutOfRangeException(nameof(options), $"A canvas of {options.Width} × {options.Height} pixels gives the symbol's {columns} × {rows} modules less than a pixel each.");
        if (width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), $"The side that follows from {options.Width} × {options.Height} pixels is {Math.Max(width, height)} pixels, and a side can be at most {int.MaxValue}.");
        if (numerator % denominator == 0)
        {
            numerator /= denominator;
            denominator = 1;
        }
        return new ImageLayout((int)width, (int)height, left, top, columns, rows, numerator, denominator);
    }

    /// <summary>The first pixel column of module column <paramref name="column"/>, or with <see cref="Columns"/> the pixel after the last.</summary>
    public int ColumnStart(int column) => Left + Start(column);

    /// <summary>The first pixel row of module row <paramref name="row"/>, or with <see cref="Rows"/> the pixel row after the last.</summary>
    public int RowStart(int row) => Top + Start(row);

    /// <summary>The row groups a writer walks: the symbol's rows, and a group for the light rows above and below it when there are any.</summary>
    public int Groups => Rows + (Top > 0 ? 1 : 0) + (RowStart(Rows) < Height ? 1 : 0);

    /// <summary>The symbol row of a group: -1 for the light rows above the symbol, <see cref="Rows"/> for those below it.</summary>
    public int GroupRow(int group) => Top > 0 ? group - 1 : group;

    /// <summary>The first pixel row of a group, or with <see cref="Groups"/> the canvas's height.</summary>
    public int GroupStart(int group)
    {
        var row = GroupRow(group);
        return row < 0 ? 0 : row <= Rows ? RowStart(row) : Height;
    }

    public int GroupHeight(int group) => GroupStart(group + 1) - GroupStart(group);

    /// <summary>The module row a pixel row of the symbol belongs to, for a pixel row from <see cref="Top"/> up to the symbol's last. -1 above and <see cref="Rows"/> below.</summary>
    public int RowOf(int y)
    {
        if (y < Top)
            return -1;
        var row = Denominator == 1 ? (y - Top) / (int)Numerator : (int)(((2L * (y - Top) + 1) * Denominator) / (2 * Numerator));
        return Math.Min(row, Rows);
    }

    // Module k starts at the first pixel x whose centre, x + 1/2 along, is at or past k × Numerator / Denominator.
    private int Start(int k) => Denominator == 1 ? k * (int)Numerator : (int)CeilDiv(2 * Numerator * k - Denominator, 2 * Denominator);

    // The side that follows the other at the scale it gives, rounded up so the symbol fits inside and the same two sides bind on the given one.
    private static long Fitted(long numerator, long denominator, int modules) => CeilDiv(numerator * modules, denominator);

    private static long CeilDiv(long n, long d) => n >= 0 ? (n + d - 1) / d : -(-n / d);
}
