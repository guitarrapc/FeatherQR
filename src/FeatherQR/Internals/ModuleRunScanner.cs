using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// Scans a symbol matrix into merged horizontal runs of dark modules.
/// This is the single implementation behind both the public <c>GetModuleRectangles</c> family (finder skipping off) and the renderer's merged-run drawing path (finder skipping on when finder patterns are styled separately), so the geometry the public API reports is by construction the geometry the renderer draws.
/// </summary>
internal static class ModuleRunScanner
{
    /// <summary>
    /// Upper bound on the number of runs a core matrix can produce: dark runs in a row are separated by at least one light module, so a row of width w holds at most ceil(w / 2) runs.
    /// O(1), quiet zone contributes nothing (always light).
    /// </summary>
    public static int GetMaxRunCount(int coreWidth, int coreHeight) => (coreWidth + 1) / 2 * coreHeight;

    /// <summary>Counts the runs <see cref="TryScan{TView}"/> would emit.</summary>
    public static int Count<TView>(in TView view)
        where TView : struct, IModuleMatrixView
    {
        var count = 0;
        var enumerator = new ModuleRunEnumerator<TView>(view, skipFinderPatterns: false);
        while (enumerator.MoveNext())
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Allocates an exact-size array of the merged runs, the shared implementation of the <c>GetModuleRectangles()</c> overloads.
    /// Counting and scanning use the same enumerator over the same matrix, so the guard is unreachable by construction; it exists to turn any future divergence between the two passes into a loud failure instead of a silently truncated or zero-padded result.
    /// </summary>
    public static ModuleRect[] ScanToArray<TView>(in TView view)
        where TView : struct, IModuleMatrixView
    {
        var result = new ModuleRect[Count(in view)];
        if (!TryScan(in view, result, out var written) || written != result.Length)
            throw new InvalidOperationException("Module run scan diverged from its counting pass; this is a bug in ModuleRunScanner.");
        return result;
    }

    /// <summary>
    /// Writes the merged runs as quiet-zone-inclusive <see cref="ModuleRect"/> values.
    /// Returns false (with <paramref name="written"/> reset to 0) only when the destination cannot hold every run.
    /// </summary>
    public static bool TryScan<TView>(in TView view, Span<ModuleRect> destination, out int written)
        where TView : struct, IModuleMatrixView
    {
        // Both axes share one quiet zone size (symbols pad symmetrically), matching
        // the renderer's single-offset math.
        var quietZone = (view.Width - view.CoreWidth) / 2;

        written = 0;
        var enumerator = new ModuleRunEnumerator<TView>(view, skipFinderPatterns: false);
        while (enumerator.MoveNext())
        {
            if (written == destination.Length)
            {
                written = 0;
                return false;
            }
            destination[written++] = new ModuleRect(enumerator.RunStart + quietZone, enumerator.RunRow + quietZone, enumerator.RunLength, 1);
        }
        return true;
    }
}

/// <summary>
/// Enumerates maximal horizontal runs of dark core modules in row-major order.
/// A run never crosses a light module and, when <c>skipFinderPatterns</c> is set, never crosses into a finder pattern module (those are drawn separately by the styled finder path).
/// Coordinates are core-relative; callers add the quiet zone.
/// </summary>
internal ref struct ModuleRunEnumerator<TView>(TView view, bool skipFinderPatterns)
    where TView : struct, IModuleMatrixView
{
    private readonly TView _view = view;
    private readonly bool _skipFinderPatterns = skipFinderPatterns;
    private readonly int _coreWidth = view.CoreWidth;
    private readonly int _coreHeight = view.CoreHeight;
    private int _row = 0;
    private int _col = 0;

    /// <summary>Core row of the current run.</summary>
    public int RunRow { get; private set; }

    /// <summary>Core column where the current run starts.</summary>
    public int RunStart { get; private set; }

    /// <summary>Length of the current run in modules (always positive).</summary>
    public int RunLength { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool IsRunModule(int row, int col)
        => _view.GetCoreModule(row, col) && !(_skipFinderPatterns && _view.IsFinderPattern(row, col));

    public bool MoveNext()
    {
        for (; _row < _coreHeight; _row++, _col = 0)
        {
            while (_col < _coreWidth)
            {
                if (!IsRunModule(_row, _col))
                {
                    _col++;
                    continue;
                }

                var runStart = _col;
                do
                {
                    _col++;
                } while (_col < _coreWidth && IsRunModule(_row, _col));

                RunRow = _row;
                RunStart = runStart;
                RunLength = _col - runStart;
                return true;
            }
        }
        return false;
    }
}
