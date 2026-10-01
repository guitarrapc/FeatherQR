using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The premise of a test of a candidate skipped inside a symbol that read but did not fit the destination: the global pass's
/// strided scan, in the order the Micro QR and rMQR scans decode (<see cref="FinderPatternFinder.RankByConfirmation"/>), ranks
/// the symbol's own finder first among the candidates inside its corners and another one inside after it. Without it the skip
/// has nothing to skip, and the test passes whatever the decoder does: a render a module size away, or a change to how the
/// finder confirms or ranks candidates, can move the pattern out of the scan or ahead of the finder.
/// </summary>
internal static class SkipPremise
{
    /// <summary>How many candidates a scan decodes, the first eight.</summary>
    private const int CandidatesDecoded = 8;

    public static bool RanksAnotherCandidateInsideAfterTheFinder(byte[] luminance, int width, int height, SymbolCorners corners)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey);
        FinderPatternFinder.RankByConfirmation(candidates.AsSpan(0, count));

        var inside = candidates.Take(Math.Min(count, CandidatesDecoded)).Where(c => SymbolGeometry.Contains(corners, c.X, c.Y)).ToArray();
        if (inside.Length < 2)
            return false;

        // The finder sits beside the top-left corner, nearer it than anything else inside the symbol
        var finder = inside.MinBy(c => Distance(c, corners.TopLeft));
        return inside[0].X == finder.X && inside[0].Y == finder.Y;
    }

    /// <summary>The image with a copy of itself below it: two symbols, neither of which the scan can tell from the other.</summary>
    public static (byte[] Luminance, int Width, int Height) StackTwice(byte[] luminance, int width, int height)
    {
        var stacked = new byte[luminance.Length * 2];
        luminance.CopyTo(stacked, 0);
        luminance.CopyTo(stacked, luminance.Length);
        return (stacked, width, height * 2);
    }

    /// <summary>The corners of both symbols of <see cref="StackTwice"/>, given those of either and the height of one copy.</summary>
    public static SymbolCorners[] BothCopies(SymbolCorners corners, int copyHeight)
    {
        var offset = corners.TopLeft.Y < copyHeight ? copyHeight : -copyHeight;
        var other = new SymbolCorners(Shift(corners.TopLeft, offset), Shift(corners.TopRight, offset), Shift(corners.BottomRight, offset), Shift(corners.BottomLeft, offset));
        return [corners, other];
    }

    private static ImagePoint Shift(ImagePoint point, float dy) => new(point.X, point.Y + dy);

    private static float Distance(FinderPattern candidate, ImagePoint point)
        => MathF.Sqrt((candidate.X - point.X) * (candidate.X - point.X) + (candidate.Y - point.Y) * (candidate.Y - point.Y));
}
