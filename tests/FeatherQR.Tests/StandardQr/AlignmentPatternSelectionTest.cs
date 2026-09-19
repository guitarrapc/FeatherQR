using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Which alignment pattern the search returns when more than one candidate passes the run
/// checks inside the window. Both kernels are asserted, since the SIMD row walk and the
/// scalar one feed the same selection.
/// </summary>
public class AlignmentPatternSelectionTest
{
    private const int PixelsPerModule = 6;
    private const int Modules = 30;

    /// <summary>
    /// Two real patterns in the window, the one on the prediction to the right: the row scan
    /// meets the left one first, and the first hit used to win.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Find_TwoPatternsInTheWindow_ReturnsTheOneOnThePrediction(bool scalar)
    {
        var image = Blank();
        DrawAlignmentPattern(image, 9, 15);
        DrawAlignmentPattern(image, 15, 15);

        var (found, x, y) = Find(image, 15, 15, scalar);

        await Assert.That(found).IsTrue();
        await Assert.That(x).IsEqualTo(Center(15)).Within(1f);
        await Assert.That(y).IsEqualTo(Center(15)).Within(1f);
    }

    /// <summary>
    /// A pattern on the prediction with one dark module in its light ring passes the
    /// light-dark-light runs through its center and the dark ring, like data can; only the
    /// light ring tells it apart, and the real pattern five modules away must win.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Find_BrokenLightRingOnThePrediction_ReturnsTheRealPattern(bool scalar)
    {
        var image = Blank();
        DrawAlignmentPattern(image, 12, 15);
        SetModule(image, 13, 16, dark: true);
        DrawAlignmentPattern(image, 18, 15);

        var (found, x, y) = Find(image, 12, 15, scalar);

        await Assert.That(found).IsTrue();
        await Assert.That(x).IsEqualTo(Center(18)).Within(1f);
        await Assert.That(y).IsEqualTo(Center(15)).Within(1f);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Find_OnlyABrokenLightRing_FindsNothing(bool scalar)
    {
        var image = Blank();
        DrawAlignmentPattern(image, 15, 15);
        SetModule(image, 14, 14, dark: true);

        var (found, _, _) = Find(image, 15, 15, scalar);

        await Assert.That(found).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Find_SinglePatternOffThePrediction_ReturnsIt(bool scalar)
    {
        var image = Blank();
        DrawAlignmentPattern(image, 17, 13);

        var (found, x, y) = Find(image, 15, 15, scalar);

        await Assert.That(found).IsTrue();
        await Assert.That(x).IsEqualTo(Center(17)).Within(1f);
        await Assert.That(y).IsEqualTo(Center(13)).Within(1f);
    }

    /// <summary>
    /// The centre is in the same coordinates as the finder centres, where integers are pixel
    /// edges. The vertical refinement used to land half a pixel above the pattern's centre.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Find_AxisAlignedPattern_CentreIsExact(bool scalar)
    {
        var image = Blank();
        DrawAlignmentPattern(image, 15, 15);

        var (found, x, y) = Find(image, 15, 15, scalar);

        await Assert.That(found).IsTrue();
        await Assert.That(x).IsEqualTo(Center(15)).Within(0.01f);
        await Assert.That(y).IsEqualTo(Center(15)).Within(0.01f);
    }

    private static (bool Found, float X, float Y) Find(byte[] image, int expectedColumn, int expectedRow, bool scalar)
    {
        const int side = Modules * PixelsPerModule;
        (float, float) axisX = (PixelsPerModule, 0f);
        (float, float) axisY = (0f, PixelsPerModule);
        float x, y;
        var found = scalar
            ? AlignmentPatternFinder.TryFindScalar(image, side, side, 128, Center(expectedColumn), Center(expectedRow), PixelsPerModule, axisX, axisY, 8f, out x, out y)
            : AlignmentPatternFinder.TryFind(image, side, side, 128, Center(expectedColumn), Center(expectedRow), PixelsPerModule, axisX, axisY, 8f, out x, out y);
        return (found, x, y);
    }

    private static float Center(int module) => (module + 0.5f) * PixelsPerModule;

    private static byte[] Blank()
    {
        var image = new byte[Modules * PixelsPerModule * Modules * PixelsPerModule];
        Array.Fill(image, (byte)255);
        return image;
    }

    private static void DrawAlignmentPattern(byte[] image, int centerColumn, int centerRow)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetModule(image, centerColumn + dx, centerRow + dy, dark: ring != 1);
            }
        }
    }

    private static void SetModule(byte[] image, int column, int row, bool dark)
    {
        const int side = Modules * PixelsPerModule;
        for (var y = row * PixelsPerModule; y < (row + 1) * PixelsPerModule; y++)
        {
            for (var x = column * PixelsPerModule; x < (column + 1) * PixelsPerModule; x++)
                image[y * side + x] = dark ? (byte)0 : (byte)255;
        }
    }
}
