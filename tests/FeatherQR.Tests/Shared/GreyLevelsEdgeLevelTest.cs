using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The level an edge is located at is halfway between the two classes whether or not the image has grey pixels, and the threshold only for an image of one class.
/// </summary>
public class GreyLevelsEdgeLevelTest
{
    /// <summary>A two-valued image has no grey to read, but its edges still lie halfway: the threshold sits a level above the dark class.</summary>
    [Test]
    [Arguments((byte)0, (byte)255, 127.5f)]
    [Arguments((byte)40, (byte)215, 127.5f)]
    [Arguments((byte)0x22, (byte)0xEE, 136f)]
    public async Task TwoValuedImage_HalfwayNotTheThreshold(byte dark, byte light, float halfway)
    {
        var histogram = new int[256];
        histogram[dark] = 5_000;
        histogram[light] = 7_000;
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.IsEnabled).IsFalse().Because("no pixel lies between the levels");
        await Assert.That(grey.EdgeLevel(threshold)).IsEqualTo(halfway);
        await Assert.That(grey.EdgeLevel(threshold)).IsNotEqualTo((float)threshold);
    }

    [Test]
    public async Task GreyImage_TheMidpoint()
    {
        var histogram = new int[256];
        histogram[10] = 5_000;
        histogram[200] = 5_000;
        histogram[105] = 3;
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.IsEnabled).IsTrue();
        await Assert.That(grey.EdgeLevel(threshold)).IsEqualTo(grey.Midpoint);
    }

    /// <summary>Classes too close for a grey pixel to mean coverage still have an edge between them.</summary>
    [Test]
    public async Task ClassesTooClose_Halfway()
    {
        var histogram = new int[256];
        histogram[100] = 5_000;
        histogram[130] = 5_000;
        histogram[115] = 5;
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.IsEnabled).IsFalse().Because("the classes are closer than the range grey needs");
        await Assert.That(grey.EdgeLevel(threshold)).IsEqualTo(115f);
    }

    [Test]
    public async Task OneClass_TheThreshold()
    {
        var histogram = new int[256];
        histogram[128] = 5_000;
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        await Assert.That(grey.EdgeLevel(threshold)).IsEqualTo((float)threshold);
    }
}
