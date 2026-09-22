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
