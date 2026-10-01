namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A sampled grid as a decoder that reads around a single finder reads it: its matrix decode, whether that got past the format
/// information, the decoder's own condition for reading it again by coverage, and where each module centre lies in the image.
/// A decoder implements this on a struct, so <see cref="GridRead"/> calls it directly.
/// </summary>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal interface IGridRead<TInfo>
{
    /// <summary>Modules across the grid.</summary>
    int Columns { get; }

    /// <summary>Modules down the grid.</summary>
    int Rows { get; }

    /// <summary>The image position of grid point (<paramref name="u"/>, <paramref name="v"/>), in modules from the grid's corner.</summary>
    void Map(float u, float v, out float x, out float y);

    /// <summary>
    /// The matrix decode of <paramref name="modules"/>, in every orientation the decoder reads a grid in: a terminal result carries
    /// the corners of the read, and every result but a read is kept in <paramref name="result"/>.
    /// </summary>
    DecodeStatus Decode(ReadOnlySpan<byte> modules, in ImageView image, Span<char> destination, out int charsWritten, out TInfo info, ref SearchResult<TInfo> result);

    /// <summary>Whether the last decode got past the format information, in an orientation the decoder reads.</summary>
    bool PastFormat { get; }

    /// <summary>The decoder's own condition for reading the last decoded grid again by coverage, asked only once the shared one holds.</summary>
    bool MayReadByCoverage(ReadOnlySpan<byte> modules);
}

/// <summary>
/// A sampled grid's decode, and on an image with grey levels its re-read by coverage: each module's luminance interpolated at its
/// centre and split halfway between the two levels, decoded only when that changes a module.
/// </summary>
/// <remarks>
/// A grid is read again only when it got past its format information and neither read nor read too long for the destination, and
/// the decoder's own condition holds. A read, or a read too long, is the symbol's: read again, the same grid reads the same text on
/// a real image and, on an image crafted to read another, that other, which the call would return though a sized call never does.
/// The grid's result is returned unless the re-read went further.
/// </remarks>
internal static class GridRead
{
    /// <summary>
    /// Decodes the grid sampled into <paramref name="modules"/> and, when the gate holds, reads it again by coverage into the same
    /// modules.
    /// </summary>
    /// <typeparam name="TGrid">The decoder's grid; a struct, so the calls are direct.</typeparam>
    /// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
    /// <param name="grid">The grid sampled, which the decode records its orientations' results in.</param>
    /// <param name="image">The image the grid was sampled from, its grey levels the re-read's split.</param>
    /// <param name="modules">The grid as sampled, <see cref="IGridRead{TInfo}.Columns"/> a row; the re-read writes over it.</param>
    /// <param name="destination">Where a read writes its characters.</param>
    /// <param name="charsWritten">The characters the returned decode wrote.</param>
    /// <param name="info">The returned decode's diagnostics.</param>
    /// <param name="result">The candidate's result, which keeps every decode's failure.</param>
    /// <param name="pastFormat">Whether the grid as sampled got past the format information.</param>
    /// <param name="readByCoverage">Whether the grid was read again by coverage, decoded or not: a decoder that counts re-reads charges one.</param>
    public static DecodeStatus Decode<TGrid, TInfo>(ref TGrid grid, in ImageView image, Span<byte> modules, Span<char> destination, out int charsWritten, out TInfo info, ref SearchResult<TInfo> result, out bool pastFormat, out bool readByCoverage)
        where TGrid : struct, IGridRead<TInfo>
    {
        var status = grid.Decode(modules, image, destination, out charsWritten, out info, ref result);
        pastFormat = grid.PastFormat;
        readByCoverage = false;
        if (AttemptStatus.IsTerminal(status) || !image.Grey.IsEnabled || !pastFormat || !grid.MayReadByCoverage(modules))
            return status;

        readByCoverage = true;
        var midpoint = image.Grey.Midpoint;
        var columns = grid.Columns;
        var changed = false;
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                grid.Map(column + 0.5f, row + 0.5f, out var x, out var y);
                var dark = LuminanceSampler.Bilinear(image.Luminance, image.Width, image.Height, x, y) < midpoint ? (byte)1 : (byte)0;
                changed |= modules[row * columns + column] != dark;
                modules[row * columns + column] = dark;
            }
        }

        // The grid that already failed decodes the same way again
        if (!changed)
            return status;

        var coverageStatus = grid.Decode(modules, image, destination, out var coverageChars, out var coverageInfo, ref result);
        if (AttemptStatus.Progress(coverageStatus) <= AttemptStatus.Progress(status))
            return status;
        charsWritten = coverageChars;
        info = coverageInfo;
        return coverageStatus;
    }
}
