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

    /// <summary>
    /// At one and two pixels per module a probe half a pixel off the convention lands in the
    /// neighbouring module: the ring checks read the wrong ring and the pattern is missed.
    /// </summary>
    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task Find_LowDensity_CentreIsExact(bool scalar, int pixelsPerModule)
    {
        var image = Blank(pixelsPerModule);
        DrawAlignmentPattern(image, 15, 15, pixelsPerModule);

        var (found, x, y) = Find(image, 15, 15, scalar, pixelsPerModule);

        await Assert.That(found).IsTrue();
        await Assert.That(x).IsEqualTo(Center(15, pixelsPerModule)).Within(0.01f);
        await Assert.That(y).IsEqualTo(Center(15, pixelsPerModule)).Within(0.01f);
    }

    private static (bool Found, float X, float Y) Find(byte[] image, int expectedColumn, int expectedRow, bool scalar, int pixelsPerModule = PixelsPerModule)
    {
        var side = Modules * pixelsPerModule;
        (float, float) axisX = (pixelsPerModule, 0f);
        (float, float) axisY = (0f, pixelsPerModule);
        float x, y;
        var found = scalar
            ? AlignmentPatternFinder.TryFindScalar(image, side, side, 128, Center(expectedColumn, pixelsPerModule), Center(expectedRow, pixelsPerModule), pixelsPerModule, axisX, axisY, 8f, out x, out y)
            : AlignmentPatternFinder.TryFind(image, side, side, 128, Center(expectedColumn, pixelsPerModule), Center(expectedRow, pixelsPerModule), pixelsPerModule, axisX, axisY, 8f, out x, out y);
        return (found, x, y);
    }

    private static float Center(int module, int pixelsPerModule = PixelsPerModule) => (module + 0.5f) * pixelsPerModule;

    private static byte[] Blank(int pixelsPerModule = PixelsPerModule)
    {
        var image = new byte[Modules * pixelsPerModule * Modules * pixelsPerModule];
        Array.Fill(image, (byte)255);
        return image;
    }

    private static void DrawAlignmentPattern(byte[] image, int centerColumn, int centerRow, int pixelsPerModule = PixelsPerModule)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetModule(image, centerColumn + dx, centerRow + dy, dark: ring != 1, pixelsPerModule);
            }
        }
    }

    private static void SetModule(byte[] image, int column, int row, bool dark, int pixelsPerModule = PixelsPerModule)
    {
        var side = Modules * pixelsPerModule;
        for (var y = row * pixelsPerModule; y < (row + 1) * pixelsPerModule; y++)
        {
            for (var x = column * pixelsPerModule; x < (column + 1) * pixelsPerModule; x++)
                image[y * side + x] = dark ? (byte)0 : (byte)255;
        }
    }
}
