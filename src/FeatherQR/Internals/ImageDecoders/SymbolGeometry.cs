namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Turns the grid-to-image mapping a decoder sampled with into the <see cref="SymbolCorners"/> it reports: the four corners of module space through the mapping.
/// </summary>
/// <remarks>
/// <para>
/// Runs once, on success, and always through the mapping that produced the decoded modules — a projective transform, Micro QR's affine frame, or the finder-plus-mesh fit Standard QR builds for a mesh-sampled decode — never a sibling estimate the decode did not validate. The square decoders retry a mirrored capture by transposing the sampled matrix rather than the mapping, so when that retry is the one that decoded, the grid's column axis is the symbol's row axis and the top-right and bottom-left corners swap; <c>transposed</c> carries that fact. rMQR never transposes: its frames carry the swap in the axes themselves, so its mapping is always in symbol order.
/// </para>
/// <para>
/// No half-pixel shift is applied. The finder centres that anchor every mapping come from run boundaries whose end index is exclusive, so they are already continuous coordinates in which integer values are pixel edges; a symbol drawn from pixel 48 maps grid (0, 0) to exactly 48.0. (Measured: adding 0.5 here put every finder-anchored corner half a pixel outside the symbol.) The samplers' own <c>(int)(x + 0.5f)</c> is a round-to-nearest on those coordinates, not evidence of a pixel-centre convention.
/// </para>
/// </remarks>
internal static class SymbolGeometry
{
    /// <summary>Corners of a <paramref name="columns"/> × <paramref name="rows"/> grid through a projective grid-to-image transform.</summary>
    public static SymbolCorners FromTransform(in PerspectiveTransform transform, int columns, int rows, bool transposed)
    {
        transform.Transform(0f, 0f, out var tlX, out var tlY);
        transform.Transform(columns, 0f, out var trX, out var trY);
        transform.Transform(columns, rows, out var brX, out var brY);
        transform.Transform(0f, rows, out var blX, out var blY);
        return FromMapped(tlX, tlY, trX, trY, brX, brY, blX, blY, transposed);
    }

    /// <summary>Corners of a <paramref name="size"/> × <paramref name="size"/> grid through an affine frame: <c>origin + u · column + v · row</c>, in pixels per module.</summary>
    public static SymbolCorners FromAffine(float originX, float originY, float uX, float uY, float vX, float vY, int size, bool transposed)
        => FromMapped(
            originX, originY,
            originX + size * uX, originY + size * uY,
            originX + size * (uX + vX), originY + size * (uY + vY),
            originX + size * vX, originY + size * vY,
            transposed);

    /// <summary>
    /// Corners already mapped to image coordinates, given in grid order — grid (0, 0), (columns, 0), (columns, rows), (0, rows) — reordered into the symbol's frame.
    /// </summary>
    public static SymbolCorners FromMapped(float tlX, float tlY, float gridTrX, float gridTrY, float brX, float brY, float gridBlX, float gridBlY, bool transposed)
    {
        var topLeft = new ImagePoint(tlX, tlY);
        var bottomRight = new ImagePoint(brX, brY);
        var gridTopRight = new ImagePoint(gridTrX, gridTrY);
        var gridBottomLeft = new ImagePoint(gridBlX, gridBlY);

        return transposed
            ? new SymbolCorners(topLeft, gridBottomLeft, bottomRight, gridTopRight)
            : new SymbolCorners(topLeft, gridTopRight, bottomRight, gridBottomLeft);
    }
}
