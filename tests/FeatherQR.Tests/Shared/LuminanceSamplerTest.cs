using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

public class LuminanceSamplerTest
{
    // 3 × 2: row 0 = 0, 100, 200; row 1 = 50, 150, 250
    private static readonly byte[] Image = [0, 100, 200, 50, 150, 250];

    [Test]
    [Arguments(0.5f, 0.5f, 0f)]
    [Arguments(1.5f, 0.5f, 100f)]
    [Arguments(2.5f, 1.5f, 250f)]
    public async Task Bilinear_AtAPixelCentre_IsThatPixel(float x, float y, float expected)
        => await Assert.That(LuminanceSampler.Bilinear(Image, 3, 2, x, y)).IsEqualTo(expected);

    [Test]
    [Arguments(1f, 0.5f, 50f)]
    [Arguments(0.5f, 1f, 25f)]
    [Arguments(1f, 1f, 75f)]
    [Arguments(1.25f, 0.5f, 75f)]
    public async Task Bilinear_BetweenCentres_IsTheWeightedMean(float x, float y, float expected)
        => await Assert.That(LuminanceSampler.Bilinear(Image, 3, 2, x, y)).IsEqualTo(expected).Within(1e-4f);

    /// <summary>Past the outer centres, and past the image, the edge pixels stand in: no read outside the buffer.</summary>
    [Test]
    [Arguments(0.2f, 0.5f, 0f)]
    [Arguments(-3f, 0.5f, 0f)]
    [Arguments(2.9f, 1.9f, 250f)]
    [Arguments(40f, 40f, 250f)]
    [Arguments(1.5f, -7f, 100f)]
    public async Task Bilinear_PastTheOuterCentres_TakesTheEdge(float x, float y, float expected)
        => await Assert.That(LuminanceSampler.Bilinear(Image, 3, 2, x, y)).IsEqualTo(expected).Within(1e-4f);

    /// <summary>The midpoint is where a pixel reads half dark, whatever the two levels are.</summary>
    [Test]
    [Arguments(20, 230)]
    [Arguments(0, 255)]
    [Arguments(90, 200)]
    public async Task GreyLevels_Midpoint_IsHalfDark(int dark, int light)
    {
        var histogram = new int[256];
        histogram[dark] = 500;
        histogram[light] = 500;
        histogram[(dark + light) / 2] = 10;
        var levels = GreyLevels.FromHistogram(histogram, (dark + light) / 2 + 1);

        await Assert.That(levels.IsEnabled).IsTrue();
        await Assert.That(levels.Midpoint).IsEqualTo((dark + light) / 2f).Within(1e-3f);
        await Assert.That(levels.Darkness((byte)Math.Floor(levels.Midpoint))).IsGreaterThanOrEqualTo(0.5f - 1e-4f);
        await Assert.That(levels.Darkness((byte)Math.Ceiling(levels.Midpoint))).IsLessThanOrEqualTo(0.5f + 1e-4f);
    }
}
