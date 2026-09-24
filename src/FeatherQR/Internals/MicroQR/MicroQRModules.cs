namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// How the matrix decoder reads a module of a sampled grid: straight, or through a view that mirrors the grid, so a mirrored capture is read without copying the grid transposed.
/// </summary>
/// <remarks>
/// The grid is passed on each call rather than held, since a span cannot be a field of a struct a generic method takes.
/// </remarks>
internal interface IMicroQRModules
{
    bool IsDark(ReadOnlySpan<byte> grid, int row, int col);
}

/// <summary>A sampled matrix, one byte per module (non-zero dark), row-major.</summary>
internal readonly struct MatrixModules(int size) : IMicroQRModules
{
    public bool IsDark(ReadOnlySpan<byte> grid, int row, int col) => grid[row * size + col] != 0;
}

/// <summary>The grid mirrored across its diagonal: a mirrored capture keeps the finder and transposes the data.</summary>
internal readonly struct TransposedModules<TModules>(TModules modules) : IMicroQRModules
    where TModules : struct, IMicroQRModules
{
    private readonly TModules _modules = modules;

    public bool IsDark(ReadOnlySpan<byte> grid, int row, int col) => _modules.IsDark(grid, col, row);
}
