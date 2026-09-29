#if NET8_0_OR_GREATER
using System.Buffers;
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The edge-list row kernel of the finder search (net8.0+, 128-bit vectors or wider).
/// </summary>
/// <remarks>
/// The mask walk goes through a row run by run and judges a window at the end of every dark run with up to fifteen compares that may each leave early. On a large symbol that is ten thousand windows a search and on an image without a symbol forty-five thousand, nearly all of them refused, and the branches are what it costs.
/// Here the row's dark bitmask is never stored: 64 pixels become one word, and the word's rising and falling edges (the starts and the ends of dark runs) go straight into two arrays. A window is then three consecutive starts and ends, and sixteen windows are classified a step without a branch: the strict ratio, the near miss and the small crisp runs, the same three integer checks the other kernels make. Only the windows a check flags run scalar code, in row order, through the same follow-ups and the same cross-checks.
/// The strict ratio is checked in integers, which gives the verdict of the float form on every input: the two can only differ where total / 7f is inexact, and equality in the check needs a total divisible by 14, where it is exact. <c>FinderRowKernelParityTest</c> holds the candidate list to the scalar kernel's, bit for bit.
/// Positions are kept in sixteen bits, which is what bounds the row width: below 4,096 pixels every quantity of the checks fits a signed lane once 2·|d| &lt; t is written |d| &lt; (t + 1) / 2.
/// On 128-bit vectors the same kernel runs eight windows a step, and a step's verdicts become bits only once the OR of the three says a lane is flagged: nearly every step has nothing flagged, and on ARM64 turning eight lanes into bits is a sequence. ARM64, which has no movemask, makes the row's word with the NEON fold the mask walk already used; other 128-bit targets take a movemask each 16 pixels.
/// </remarks>
internal static partial class FinderPatternFinder
{
    /// <summary>Rows at least this wide go to the mask walk: positions are kept in sixteen bits, and 7 · run and 3 · total have to fit a signed lane.</summary>
    internal const int EdgeListWidthLimit = 4096;

    /// <summary>Rows narrower than one vector of pixels go to the mask walk.</summary>
    private const int EdgeListMinWidth = 32;

    /// <summary>Whether this runtime and machine have the edge-list kernel at all.</summary>
    internal static bool IsEdgeListKernelSupported
#if NET8_0_OR_GREATER
        => Vector128.IsHardwareAccelerated;
#else
        => false;
#endif

    /// <summary>Windows <c>ClassifyWindows</c> judges in one call: sixteen with 256-bit vectors, eight with 128-bit ones.</summary>
    internal static int ClassifyWindowLanes
#if NET8_0_OR_GREATER
        => Vector256.IsHardwareAccelerated ? 16 : 8;
#else
        => 0;
#endif

    /// <summary>The edge buffer for a search over rows of this width, or null when the search runs another kernel.</summary>
    private static short[]? RentEdgeBuffer(FinderRowKernel kernel, int width)
    {
#if NET8_0_OR_GREATER
        if ((kernel == FinderRowKernel.Auto || kernel == FinderRowKernel.EdgeList) && IsEdgeListKernelSupported && width >= EdgeListMinWidth && width < EdgeListWidthLimit)
            return ArrayPool<short>.Shared.Rent(2 * EdgeHalfLength(width));
#endif
        return null;
    }

    private static void ReturnEdgeBuffer(short[]? rented)
    {
#if NET8_0_OR_GREATER
        if (rented is not null)
            ArrayPool<short>.Shared.Return(rented);
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>Slots past the last edge that a sixteen-lane load starting at any window may read.</summary>
    private const int EdgeLoadSlack = 32;

    /// <summary>Half of the edge buffer, one half for the starts and one for the ends: a row of w pixels has at most (w + 1) / 2 dark runs.</summary>
    private static int EdgeHalfLength(int width) => (width + 1) / 2 + 1 + EdgeLoadSlack;
#endif
}
