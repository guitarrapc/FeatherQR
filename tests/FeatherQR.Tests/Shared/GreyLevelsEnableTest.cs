using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Whether <see cref="GreyLevels.FromHistogram"/> enables the levels is a statement about the bins: some pixel lies strictly between the two class levels, or none does.
/// It must not depend on how many pixels the image has. The class mean is a weighted sum over a count, and a float loses that sum's low bits past 2²⁴, which for a light class at 255 is 65,794 pixels: past that the mean can round above 255 and the "strictly between" scan reaches the level's own bin.
/// </summary>
public class GreyLevelsEnableTest
{
    /// <summary>Two-valued images at every size a decoder sees, the two counts that showed the rounding included (a 555 × 555 render: 126,063 dark and 181,962 light).</summary>
    [Test]
    public async Task TwoValuedImage_NeverEnablesTheLevels_WhateverItsPixelCount()
    {
        var enabled = new List<string>();
        var pairs = new (byte Dark, byte Light)[] { (0, 255), (1, 254), (0, 254), (1, 255), (3, 253), (0x22, 0xEE), (40, 215) };
        foreach (var (dark, light) in pairs)
        {
            foreach (var (darkCount, lightCount) in Counts())
            {
                var histogram = new int[256];
                histogram[dark] = darkCount;
                histogram[light] = lightCount;
                var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
                if (grey.IsEnabled)
                    enabled.Add($"{dark}/{light} x {darkCount}/{lightCount} at threshold {threshold}");
            }
        }

        await Assert.That(enabled).IsEmpty();
    }

    /// <summary>One pixel strictly between the levels is what enables them, at the same counts.</summary>
    [Test]
    public async Task OnePixelBetween_EnablesTheLevels_AtEveryPixelCount()
    {
        var disabled = new List<string>();
        foreach (var (darkCount, lightCount) in Counts())
        {
            var histogram = new int[256];
            histogram[0] = darkCount;
            histogram[255] = lightCount;
            histogram[128] = 1;
            Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
            if (!grey.IsEnabled)
                disabled.Add($"{darkCount}/{lightCount}");
        }

        await Assert.That(disabled).IsEmpty();
    }

    /// <summary>
    /// A pixel one bin inside either level enables them, and so does one in the bin next to a class mean that is not a whole number.
    /// These are the two bounds of the "strictly between" scan, the first bin above the dark mean and the last below the light one.
    /// </summary>
    [Test]
    public async Task PixelNextToALevel_EnablesTheLevels()
    {
        var disabled = new List<string>();
        foreach (var between in new byte[] { 1, 2, 253, 254 })
        {
            var histogram = new int[256];
            histogram[0] = 5_000;
            histogram[255] = 5_000;
            histogram[between] = 1;
            Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
            if (!grey.IsEnabled)
                disabled.Add($"0/255 and one pixel at {between}");
        }

        // A light mean of 250.43 (400 at 250, 300 at 251): bin 250 lies below it
        var light = new int[256];
        light[0] = 1_000;
        light[250] = 400;
        light[251] = 300;
        Binarizer.ComputeOtsuThresholdFromHistogram(light, out var lightGrey);
        if (!lightGrey.IsEnabled)
            disabled.Add("light mean 250.43");

        // A dark mean of 4.57 (300 at 4, 400 at 5): bin 5 lies above it
        var dark = new int[256];
        dark[4] = 300;
        dark[5] = 400;
        dark[255] = 1_000;
        Binarizer.ComputeOtsuThresholdFromHistogram(dark, out var darkGrey);
        if (!darkGrey.IsEnabled)
            disabled.Add("dark mean 4.57");

        await Assert.That(disabled).IsEmpty();
    }

    /// <summary>
    /// Each class mean is taken over the outer half of its class, and a class whose count reaches exactly half its weight ends there:
    /// the bin that would tie is part of the inner half, where the edge pixels being measured sit, and pulls the level toward the other class.
    /// A range of exactly <c>MinimumRange</c> is wide enough for a level between the two to mean coverage.
    /// </summary>
    [Test]
    public async Task ClassMeansStopAtTheHalfWeight_AndTheRangeBoundIsInclusive()
    {
        // 100 at bin 0 is exactly half the dark class, so the dark level is 0 and not the mean with bin 10
        var darkTie = new int[256];
        darkTie[0] = 100;
        darkTie[10] = 100;
        darkTie[255] = 100;
        Binarizer.ComputeOtsuThresholdFromHistogram(darkTie, out var darkGrey);
        await Assert.That(darkGrey.Darkness(128)).IsEqualTo(127f / 255f).Within(1e-5f);

        // and 100 at bin 255 is exactly half the light class
        var lightTie = new int[256];
        lightTie[0] = 100;
        lightTie[245] = 100;
        lightTie[255] = 100;
        Binarizer.ComputeOtsuThresholdFromHistogram(lightTie, out var lightGrey);
        await Assert.That(lightGrey.Darkness(250)).IsEqualTo(5f / 255f).Within(1e-5f);

        // Levels 32 apart are far enough apart; one closer is not
        var atTheBound = new int[256];
        atTheBound[0] = 100;
        atTheBound[10] = 1;
        atTheBound[32] = 100;
        await Assert.That(GreyLevels.FromHistogram(atTheBound, 20).IsEnabled).IsTrue();

        var insideTheBound = new int[256];
        insideTheBound[0] = 100;
        insideTheBound[10] = 1;
        insideTheBound[31] = 100;
        await Assert.That(GreyLevels.FromHistogram(insideTheBound, 20).IsEnabled).IsFalse();
    }

    /// <summary>The levels and the scale are the class means as before: a pixel at the light level reads 0, at the dark level 1, halfway between 0.5.</summary>
    [Test]
    public async Task Darkness_ReadsTheShareBetweenTheClassMeans()
    {
        var histogram = new int[256];
        histogram[10] = 181_962;
        histogram[200] = 181_962;
        histogram[105] = 1;
        Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.IsEnabled).IsTrue();
        await Assert.That(grey.Darkness(200)).IsEqualTo(0f);
        await Assert.That(grey.Darkness(10)).IsEqualTo(1f);
        await Assert.That(grey.Darkness(105)).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(grey.Darkness(255)).IsEqualTo(0f);
        await Assert.That(grey.Darkness(0)).IsEqualTo(1f);
    }

    /// <summary>Two classes closer than the minimum range stay disabled, at large counts too.</summary>
    [Test]
    public async Task ClassesTooClose_StayDisabled()
    {
        var histogram = new int[256];
        histogram[100] = 300_000;
        histogram[130] = 300_000;
        histogram[115] = 5;
        Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.IsEnabled).IsFalse();
    }

    private static IEnumerable<(int Dark, int Light)> Counts()
    {
        yield return (126_063, 181_962);
        yield return (181_962, 126_063);
        yield return (1, 1);
        yield return (65_793, 65_794);
        yield return (65_794, 65_795);
        for (var count = 1_000; count <= 4_000_000; count = count * 3 / 2)
        {
            yield return (count, count);
            yield return (count / 3 + 1, count);
            yield return (count, count / 3 + 1);
        }
    }
}
