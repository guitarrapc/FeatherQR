using FeatherQR.Internals.ImageDecoders;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A finder's centre is the same share of every line through it, so a falling diagonal whose centre is a much smaller share of its line than the row's and column's are of theirs has to be confirmed by the rising diagonal.
/// </summary>
public class FinderDiagonalCentreTest
{
    /// <summary>
    /// A cross whose row and column carry the finder's runs and whose falling diagonal is 1:1:1:1:1. Its 45° walk takes the corner pixels beside the one-module centre into that run and passes the ratio, with or without the grey levels, at these scales and offsets; the rising diagonal crosses no ring.
    /// </summary>
    [Test]
    [Arguments(2.00f, 0.25f, 0.125f)]
    [Arguments(2.05f, 0f, 0f)]
    [Arguments(2.15f, 0.125f, 0f)]
    [Arguments(2.25f, 0f, 0f)]
    [Arguments(2.50f, 0.5f, 0f)]
    [Arguments(3.00f, 0.5f, 0f)]
    [Arguments(3.25f, 0.25f, 0f)]
    [Arguments(4.00f, 0.5f, 0f)]
    public async Task FindCandidates_OneModuleDiagonalCentre_IsNotACandidate(float pixelsPerModule, float offsetX, float offsetY)
    {
        var (luminance, width, height) = AntiAliasedRenderer.Render(DiagonalDecoyPattern, 15, 15, pixelsPerModule, offsetX, offsetY);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];

        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, default)).IsEqualTo(0);
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey)).IsEqualTo(0);
    }

    /// <summary>
    /// Real finders whose falling diagonal reads a centre short enough to be sent to the rising diagonal, which reads them: a plain render, prints a fifth and a tenth of a module thin, a tenth thick, and turned ones. Each is found once, at its centre.
    /// </summary>
    [Test]
    [Arguments(0f, 0f, 2.75f, 0.5f, 0.5f)]
    [Arguments(-0.2f, 0f, 2.25f, 0.5f, 0.25f)]
    [Arguments(-0.1f, 0f, 2.25f, 0.5f, 0.5f)]
    [Arguments(0.1f, 0f, 2.25f, 0f, 0f)]
    [Arguments(0f, 30f, 2.25f, 0f, 0f)]
    [Arguments(0.1f, 20f, 2.75f, 0f, 0f)]
    public async Task FindCandidates_ShortDiagonalCentre_RisingDiagonalReads_IsFound(float spread, float degrees, float pixelsPerModule, float offsetX, float offsetY)
    {
        var (luminance, side) = RenderFinder(pixelsPerModule, offsetX, offsetY, spread, degrees);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];

        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, candidates, grey);

        await Assert.That(count).IsEqualTo(1);
        var centre = side / 2f;
        await Assert.That(Math.Abs(candidates[0].X - (centre + offsetX))).IsLessThan(pixelsPerModule);
        await Assert.That(Math.Abs(candidates[0].Y - (centre + offsetY))).IsLessThan(pixelsPerModule);
    }

    [Test]
    [Arguments(3, 11, 7, 16, 7, 16, true)]   // a one-module centre with its corner pixels, against a finder's row and column
    [Arguments(7, 16, 7, 16, 7, 16, false)]  // a finder's diagonal
    [Arguments(7, 20, 7, 16, 7, 16, false)]  // exactly four fifths of the axes' share
    [Arguments(7, 21, 7, 16, 7, 16, true)]   // just under
    [Arguments(7, 21, 5, 14, 9, 18, true)]   // the mean of a row and a column that differ
    [Arguments(3, 11, 6, 13, 6, 13, false)]  // a row under 2 px a module is not compared
    [Arguments(3, 11, 6, 14, 6, 14, true)]   // a row of 2 px modules is
    public async Task IsDiagonalCentreShort_ComparesTheShares(int diagonalCentre, int diagonalTotal, int rowCentre, int rowTotal, int columnCentre, int columnTotal, bool expected)
    {
        await Assert.That(FinderPatternFinder.IsDiagonalCentreShort(diagonalCentre, diagonalTotal, rowCentre, rowTotal, columnCentre, columnTotal)).IsEqualTo(expected);
    }

    /// <summary>The falling diagonal's ring cells (1, 1) and (5, 5) dark and its centre's neighbours light; the rising diagonal holds only the centre.</summary>
    private static bool DiagonalDecoyPattern(int row, int column)
    {
        var r = row - 4;
        var c = column - 4;
        if (r < 0 || c < 0 || r >= 7 || c >= 7)
            return false;
        if (r == 3)
            return c is 0 or 2 or 3 or 4 or 6;
        if (c == 3)
            return r is 0 or 2 or 3 or 4 or 6;
        return r == c && (r == 1 || r == 5);
    }

    /// <summary>A lone anti-aliased finder centred in a 15-module square, every dark edge moved outward by <paramref name="spread"/> modules (inward when negative), turned by <paramref name="degrees"/>.</summary>
    private static (byte[] Luminance, int Side) RenderFinder(float pixelsPerModule, float offsetX, float offsetY, float spread, float degrees)
    {
        var side = (int)MathF.Ceiling(15 * pixelsPerModule) + 2;
        using var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(side / 2f + offsetX, side / 2f + offsetY);
            canvas.RotateDegrees(degrees);
            canvas.Scale(pixelsPerModule);
            canvas.Translate(-3.5f, -3.5f);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
            ring.AddRect(new SKRect(-spread, -spread, 7 + spread, 7 + spread));
            ring.AddRect(new SKRect(1 + spread, 1 + spread, 6 - spread, 6 - spread));
            canvas.DrawPath(ring, paint);
            canvas.DrawRect(new SKRect(2 - spread, 2 - spread, 5 + spread, 5 + spread), paint);
        }

        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, side).CopyTo(luminance.AsSpan(y * side, side));
        return (luminance, side);
    }
}
